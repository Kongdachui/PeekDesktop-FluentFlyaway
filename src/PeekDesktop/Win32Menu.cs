using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Win32 popup menu wrapper, replacing WinForms ContextMenuStrip.
/// </summary>
internal sealed class Win32Menu : IDisposable
{
    private IntPtr _hMenu;
    private readonly List<(uint id, Action action)> _items = new();
    private bool _disposed;

    public Win32Menu()
    {
        _hMenu = CreatePopupMenu();
    }

    public void AddItem(uint id, string text, Action onClick, bool isChecked = false)
    {
        uint flags = MF_STRING;
        if (isChecked)
            flags |= MF_CHECKED;
        AppendMenuW(_hMenu, flags, (nuint)id, text);
        _items.Add((id, onClick));
    }

    public void AddSeparator()
    {
        AppendMenuW(_hMenu, MF_SEPARATOR, 0, null);
    }

    public void SetChecked(uint id, bool isChecked)
    {
        CheckMenuItem(_hMenu, id, MF_BYCOMMAND | (isChecked ? MF_CHECKED : MF_UNCHECKED));
    }

    /// <summary>
    /// Shows the context menu at the current cursor position and executes
    /// the selected item's action.
    /// </summary>
    public void Show(IntPtr hwnd)
    {
        DarkModeMenuSupport.TryApply(hwnd);
        GetCursorPos(out NativeMethods.POINT pt);

        // Required for tray menus: the window must be foreground for the
        // menu to dismiss when the user clicks elsewhere.
        NativeMethods.SetForegroundWindow(hwnd);

        uint cmd = TrackPopupMenuEx(
            _hMenu, TPM_RETURNCMD | TPM_NONOTIFY,
            pt.x, pt.y, hwnd, IntPtr.Zero);

        // Required: send a benign message so the menu dismisses properly
        // when the user clicks outside it.
        PostMessageW(hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

        if (cmd != 0)
        {
            foreach (var (id, action) in _items)
            {
                if (id == cmd)
                {
                    action();
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hMenu != IntPtr.Zero)
        {
            DestroyMenu(_hMenu);
            _hMenu = IntPtr.Zero;
        }
    }

    // --- Constants ---
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_UNCHECKED = 0x0000;
    private const uint MF_BYCOMMAND = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    // --- P/Invoke ---
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeMethods.POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static class DarkModeMenuSupport
    {
        private const int MinimumSupportedBuild = 17763;
        private const int UXTHEME_ORDINAL_SHOULD_APPS_USE_DARK_MODE = 132;
        private const int UXTHEME_ORDINAL_ALLOW_DARK_MODE_FOR_WINDOW = 133;
        private const int UXTHEME_ORDINAL_SET_PREFERRED_APP_MODE = 135;
        private const int UXTHEME_ORDINAL_FLUSH_MENU_THEMES = 136;
        private const int PreferredAppModeAllowDark = 1;

        private static readonly object s_initLock = new();
        private static bool s_initialized;
        private static bool s_disabled;
        private static ShouldAppsUseDarkModeDelegate? s_shouldAppsUseDarkMode;
        private static AllowDarkModeForWindowDelegate? s_allowDarkModeForWindow;
        private static SetPreferredAppModeDelegate? s_setPreferredAppMode;
        private static FlushMenuThemesDelegate? s_flushMenuThemes;

        public static void TryApply(IntPtr hwnd)
        {
            if (s_disabled)
                return;

            try
            {
                EnsureInitialized();

                if (s_disabled || s_shouldAppsUseDarkMode is null || s_setPreferredAppMode is null || s_flushMenuThemes is null)
                    return;

                if (!s_shouldAppsUseDarkMode())
                    return;

                s_setPreferredAppMode(PreferredAppModeAllowDark);
                s_allowDarkModeForWindow?.Invoke(hwnd, true);
                s_flushMenuThemes();
            }
            catch (Exception ex)
            {
                s_disabled = true;
                AppDiagnostics.Log($"Dark tray menu theming disabled: {ex.Message}");
            }
        }

        private static void EnsureInitialized()
        {
            if (s_initialized || s_disabled)
                return;

            lock (s_initLock)
            {
                if (s_initialized || s_disabled)
                    return;

                if (Environment.OSVersion.Version.Build < MinimumSupportedBuild)
                {
                    s_disabled = true;
                    return;
                }

                IntPtr hUxTheme = GetModuleHandleW("uxtheme.dll");
                if (hUxTheme == IntPtr.Zero)
                {
                    s_disabled = true;
                    return;
                }

                s_shouldAppsUseDarkMode = GetDelegate<ShouldAppsUseDarkModeDelegate>(hUxTheme, UXTHEME_ORDINAL_SHOULD_APPS_USE_DARK_MODE);
                s_allowDarkModeForWindow = GetDelegate<AllowDarkModeForWindowDelegate>(hUxTheme, UXTHEME_ORDINAL_ALLOW_DARK_MODE_FOR_WINDOW);
                s_setPreferredAppMode = GetDelegate<SetPreferredAppModeDelegate>(hUxTheme, UXTHEME_ORDINAL_SET_PREFERRED_APP_MODE);
                s_flushMenuThemes = GetDelegate<FlushMenuThemesDelegate>(hUxTheme, UXTHEME_ORDINAL_FLUSH_MENU_THEMES);

                if (s_shouldAppsUseDarkMode is null || s_setPreferredAppMode is null || s_flushMenuThemes is null)
                    s_disabled = true;

                s_initialized = true;
            }
        }

        private static T? GetDelegate<T>(IntPtr module, int ordinal) where T : class
        {
            IntPtr proc = GetProcAddress(module, (IntPtr)ordinal);
            return proc == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(proc);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool ShouldAppsUseDarkModeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool AllowDarkModeForWindowDelegate(IntPtr hWnd, bool allow);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPreferredAppModeDelegate(int appMode);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FlushMenuThemesDelegate();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr lpProcName);
    }
}
