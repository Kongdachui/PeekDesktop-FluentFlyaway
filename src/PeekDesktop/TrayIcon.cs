using System;

namespace PeekDesktop;

/// <summary>
/// Manages the system tray (notification area) icon and its context menu
/// using raw Win32 APIs (no WinForms dependency).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint TrayRetryDelayMs = 1000;

    // Menu item IDs
    private const uint ID_ENABLED = 1;
    private const uint ID_STARTUP = 2;
    private const uint ID_DOUBLECLICK = 3;
    private const uint ID_GAME_GUARD = 4;
    private const uint ID_TASKBAR_CLICK = 5;
    private const uint ID_RESTORE_ON_APP_OPEN = 6;
    private const uint ID_DESKTOP_CLICK = 7;
    private const uint ID_MODE_MINIMIZE = 10;
    private const uint ID_MODE_FLYAWAY = 11;
    private const uint ID_MODE_NATIVE = 12;
    private const uint ID_ABOUT = 20;
    private const uint ID_UPDATES = 21;
    private const uint ID_AUTO_UPDATES = 22;
    private const uint ID_EXIT = 30;
    private const uint ID_ANIM_SPEED_BASE = 40;

    /// <summary>Selectable FlyAway animation speed multipliers (1.0 = 200 ms).</summary>
    private static readonly double[] AnimationSpeeds = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    private readonly Win32TrayIcon _trayIcon;
    private readonly Win32MessageLoop _messageLoop;
    private readonly DesktopPeek _desktopPeek;
    private readonly AppUpdater _appUpdater;
    private readonly Settings _settings;
    private readonly Action _exitAction;

    public TrayIcon(Win32MessageLoop messageLoop, DesktopPeek desktopPeek, AppUpdater appUpdater, Settings settings, Action exitAction)
    {
        _messageLoop = messageLoop;
        _desktopPeek = desktopPeek;
        _appUpdater = appUpdater;
        _settings = settings;
        _exitAction = exitAction;

        _trayIcon = new Win32TrayIcon(messageLoop.Handle);
        TryAddTrayIcon(scheduleRetryOnFailure: true);

        _messageLoop.MessageReceived += OnMessage;
        _messageLoop.TaskbarCreated += OnTaskbarCreated;

        _appUpdater.UpdateAvailable += (_, e) =>
        {
            _trayIcon.ShowBalloon(
                Strings.UpdateBalloonTitle,
                Strings.UpdateBalloonText(e.Version));
        };

        // Let the updater remove the tray icon before exiting during update
        AppUpdater.RemoveTrayIcon = () => _trayIcon.Remove();
    }

    private void OnTaskbarCreated()
    {
        AppDiagnostics.Log("Re-adding tray icon after Explorer restart");
        TryAddTrayIcon(scheduleRetryOnFailure: true);
    }

    private void TryAddTrayIcon(bool scheduleRetryOnFailure)
    {
        IntPtr hIcon = Win32Icon.CreateTrayIcon();
        if (_trayIcon.Add(hIcon, "PeekDesktop"))
            return;

        if (!scheduleRetryOnFailure)
            return;

        AppDiagnostics.Log("Tray icon add failed; scheduling one retry");
        _messageLoop.PostDeferredAction(TrayRetryDelayMs, () => TryAddTrayIcon(scheduleRetryOnFailure: false));
    }

    private (bool handled, IntPtr result) OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32TrayIcon.WM_TRAYICON)
        {
            if (Win32TrayIcon.IsRightClick(lParam))
            {
                ShowContextMenu();
                return (true, IntPtr.Zero);
            }

            if (Win32TrayIcon.IsBalloonClick(lParam))
            {
                _appUpdater.PromptAndInstall();
                return (true, IntPtr.Zero);
            }
        }

        return (false, IntPtr.Zero);
    }

    private void ShowContextMenu()
    {
        using var menu = new Win32Menu();

        menu.AddItem(ID_ENABLED, Strings.Enabled, ToggleEnabled, _settings.Enabled);
        menu.AddItem(ID_STARTUP, Strings.StartWithWindows, ToggleStartup, _settings.StartWithWindows);
        menu.AddItem(ID_DOUBLECLICK, Strings.RequireDoubleClick, ToggleDoubleClick, _settings.RequireDoubleClick);
        menu.AddItem(ID_DESKTOP_CLICK, Strings.PeekOnDesktopClick, ToggleDesktopClick, _settings.PeekOnDesktopClick);
        menu.AddItem(ID_TASKBAR_CLICK, Strings.PeekOnTaskbarClick, ToggleTaskbarClick, _settings.PeekOnTaskbarClick);
        menu.AddItem(ID_RESTORE_ON_APP_OPEN, Strings.RestoreOnAppSwitch, ToggleRestoreOnAppOpen, _settings.RestoreHiddenWindowsOnAppOpen);
        menu.AddItem(ID_GAME_GUARD, Strings.PauseWhileGaming, ToggleGameGuard, _settings.PauseWhileFullscreenAppActive);
        menu.AddSeparator();
        menu.AddItem(ID_MODE_NATIVE, Strings.ShowDesktopMode, () => SetPeekMode(PeekMode.NativeShowDesktop), _settings.PeekMode == PeekMode.NativeShowDesktop);
        menu.AddItem(ID_MODE_FLYAWAY, Strings.FlyAwayMode, () => SetPeekMode(PeekMode.FlyAway), _settings.PeekMode == PeekMode.FlyAway);

        Win32Menu speedMenu = menu.AddSubmenu(Strings.AnimationSpeedMenu, _settings.PeekMode == PeekMode.FlyAway);
        for (int i = 0; i < AnimationSpeeds.Length; i++)
        {
            double speed = AnimationSpeeds[i];
            speedMenu.AddItem(
                ID_ANIM_SPEED_BASE + (uint)i,
                Strings.AnimationSpeedItem(speed),
                () => SetAnimationSpeed(speed),
                Math.Abs(_settings.AnimationSpeed - speed) < 0.001);
        }

        menu.AddSeparator();
        menu.AddItem(ID_ABOUT, Strings.AboutMenu, ShowAbout);
        menu.AddItem(ID_UPDATES, Strings.CheckForUpdates, CheckForUpdates);
        menu.AddItem(ID_AUTO_UPDATES, Strings.AutoCheckForUpdates, ToggleAutoUpdates, _settings.AutoCheckForUpdates);
        menu.AddSeparator();
        menu.AddItem(ID_EXIT, Strings.Exit, DoExit);

        menu.Show(_messageLoop.Handle);
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _desktopPeek.IsEnabled = _settings.Enabled;

        if (_settings.Enabled)
            _desktopPeek.Start();
        else
            _desktopPeek.Stop();

        _settings.Save();
    }

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        _settings.Save();
        Settings.SetAutoStart(_settings.StartWithWindows);
    }

    private void ToggleDoubleClick()
    {
        _settings.RequireDoubleClick = !_settings.RequireDoubleClick;
        _desktopPeek.SetRequireDoubleClick(_settings.RequireDoubleClick);
        _settings.Save();
    }

    private void ToggleGameGuard()
    {
        _settings.PauseWhileFullscreenAppActive = !_settings.PauseWhileFullscreenAppActive;
        _desktopPeek.SetPauseWhileFullscreenAppActive(_settings.PauseWhileFullscreenAppActive);
        _settings.Save();
    }

    private void ToggleTaskbarClick()
    {
        _settings.PeekOnTaskbarClick = !_settings.PeekOnTaskbarClick;
        _desktopPeek.SetPeekOnTaskbarClick(_settings.PeekOnTaskbarClick);
        _settings.Save();
    }

    private void ToggleDesktopClick()
    {
        _settings.PeekOnDesktopClick = !_settings.PeekOnDesktopClick;
        _desktopPeek.SetPeekOnDesktopClick(_settings.PeekOnDesktopClick);
        _settings.Save();
    }

    private void ToggleRestoreOnAppOpen()
    {
        _settings.RestoreHiddenWindowsOnAppOpen = !_settings.RestoreHiddenWindowsOnAppOpen;
        _desktopPeek.SetRestoreHiddenWindowsOnAppOpen(_settings.RestoreHiddenWindowsOnAppOpen);
        _settings.Save();
    }

    private void SetPeekMode(PeekMode peekMode)
    {
        _settings.PeekMode = peekMode;
        _desktopPeek.SetPeekMode(peekMode);
        _trayIcon.UpdateTooltip(Strings.TooltipForMode(peekMode));
        _settings.Save();
    }

    private void SetAnimationSpeed(double speed)
    {
        _settings.AnimationSpeed = Settings.NormalizeAnimationSpeed(speed);
        _desktopPeek.SetAnimationSpeed(_settings.AnimationSpeed);
        _settings.Save();
    }

    private void ShowAbout()
    {
        string version = GetDisplayVersion();
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            Strings.AboutText(version),
            Strings.AboutTitle,
            NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
    }

    private async void CheckForUpdates()
    {
        await _appUpdater.CheckForUpdatesAsync(interactive: true);
    }

    private void ToggleAutoUpdates()
    {
        _settings.AutoCheckForUpdates = !_settings.AutoCheckForUpdates;
        _settings.Save();
    }

    private void DoExit()
    {
        _trayIcon.Remove();
        _exitAction();
    }

    internal static string GetDisplayVersion()
    {
        var (productVersion, fileVersion) = NativeMethods.GetExeVersionInfo();
        string? version = productVersion ?? fileVersion?.ToString();

        if (string.IsNullOrWhiteSpace(version))
            return Strings.UnknownVersion;

        int plusIndex = version.IndexOf('+');
        version = plusIndex >= 0 ? version[..plusIndex] : version;

        if (Version.TryParse(version, out var parsed) && parsed.Build >= 0 && parsed.Revision == 0)
            return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";

        return version switch
        {
            "1.0.0.0" => Strings.DevBuild,
            "1.0.0" => Strings.DevBuild,
            _ => version
        };
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
    }
}
