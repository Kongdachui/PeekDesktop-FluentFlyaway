using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeekDesktop;

/// <summary>
/// Captures the state of all visible top-level windows, minimizes them,
/// and restores them to their exact previous positions (including maximized state).
/// </summary>
public sealed class WindowTracker
{
    private const int AnimationSteps = 30;
    private const int DefaultAnimationDurationMs = 200;
    private const int MinAnimationDurationMs = 40;
    private const int MaxAnimationDurationMs = 1000;
    private const int OffscreenMargin = 64;

    private readonly List<WindowInfo> _savedWindows = new();
    private readonly HashSet<IntPtr> _maximizedWindows = new();

    public bool HasWindows => _savedWindows.Count > 0;
    public int SavedWindowCount => _savedWindows.Count;

    /// <summary>
    /// When set to true, the current animation loop exits early.
    /// Reset to false by the caller after checking.
    /// </summary>
    public bool CancelAnimation { get; set; }

    /// <summary>
    /// Speed multiplier for the FlyAway peek/restore animation. 1.0 keeps the
    /// historical 200 ms; 2.0 is twice as fast, 0.5 is twice as slow.
    /// </summary>
    public double AnimationSpeed { get; set; } = 1.0;

    private int AnimationDurationMs
    {
        get
        {
            double speed = double.IsFinite(AnimationSpeed) && AnimationSpeed > 0 ? AnimationSpeed : 1.0;
            return (int)Math.Clamp(DefaultAnimationDurationMs / speed, MinAnimationDurationMs, MaxAnimationDurationMs);
        }
    }

    /// <summary>Resolved animation duration for the current speed (exposed for the interop harness).</summary>
    internal int CurrentAnimationDurationMs => AnimationDurationMs;

    /// <summary>
    /// Snapshot all visible, non-system top-level windows and their placements.
    /// </summary>
    public void CaptureWindows()
    {
        var stopwatch = Stopwatch.StartNew();
        _savedWindows.Clear();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldTrackWindow(hwnd))
            {
                var placement = new NativeMethods.WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
                {
                    if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT bounds))
                        bounds = placement.rcNormalPosition;

                    _savedWindows.Add(new WindowInfo(hwnd, placement, bounds));
                    AppDiagnostics.LogWindow("Captured window", hwnd);
                }
            }
            return true;
        }, IntPtr.Zero);

        AppDiagnostics.Log($"Capture complete: {_savedWindows.Count} window(s) saved");
        AppDiagnostics.Metric($"CaptureWindows: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Minimize every captured window.
    /// </summary>
    public void MinimizeAll()
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var window in _savedWindows)
        {
            AppDiagnostics.LogWindow("Minimizing window", window.Handle);
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
        }

        AppDiagnostics.Metric($"MinimizeAll: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    public void ClearSavedWindows()
    {
        _savedWindows.Clear();
        _maximizedWindows.Clear();
    }

    public IntPtr[] GetSavedWindowHandlesSnapshot()
    {
        var handles = new IntPtr[_savedWindows.Count];
        for (int i = 0; i < _savedWindows.Count; i++)
            handles[i] = _savedWindows[i].Handle;

        return handles;
    }

    public IntPtr[] RemoveSavedWindows(Predicate<IntPtr> shouldRemove)
    {
        var removedHandles = new List<IntPtr>();
        _savedWindows.RemoveAll(window =>
        {
            if (!shouldRemove(window.Handle))
                return false;

            removedHandles.Add(window.Handle);
            return true;
        });

        return removedHandles.ToArray();
    }

    /// <summary>
    /// Move captured windows toward the corner of the screen they are already closest to.
    /// This is an experiment to mimic macOS-style "show desktop" animation.
    /// </summary>
    public void FlyAwayAll()
    {
        var stopwatch = Stopwatch.StartNew();
        var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);
        _maximizedWindows.Clear();

        foreach (var window in _savedWindows)
        {
            if (!NativeMethods.IsWindow(window.Handle))
                continue;

            if (window.Placement.showCmd == NativeMethods.SW_MAXIMIZE)
            {
                // Un-maximize to normal size, then animate offscreen from there.
                // The size change happens in one frame (~6ms) — invisible during
                // the 200ms animation. RestoreAll will re-maximize via SetWindowPlacement.
                _maximizedWindows.Add(window.Handle);
                var normalPlacement = window.Placement;
                normalPlacement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                normalPlacement.showCmd = NativeMethods.SW_SHOWNORMAL;
                NativeMethods.SetWindowPlacement(window.Handle, ref normalPlacement);
            }

            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOWNOACTIVATE);

            NativeMethods.RECT startBounds = GetCurrentBounds(window);
            NativeMethods.RECT targetBounds = ComputeFlyAwayTarget(startBounds);
            animationWindows.Add(new AnimatedWindow(window.Handle, startBounds, targetBounds));
        }

        AnimateWindows(animationWindows);
        AppDiagnostics.Metric($"FlyAwayAll: {animationWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Restore every captured window to its saved placement.
    /// Restores bottom-to-top to preserve Z-order. The common path un-minimizes windows
    /// without activating them, so focus stays with whatever the shell restored.
    ///
    /// Windows 11 snap groups ("Snap layouts" / side-by-side split) are restored by
    /// un-minimizing the window and letting Windows put it back into its snap slot;
    /// forcing WINDOWPLACEMENT would move it to the pre-snap rect and dissolve the split.
    /// </summary>
    public void RestoreAll(PeekMode peekMode = PeekMode.Minimize)
    {
        var stopwatch = Stopwatch.StartNew();
        int restoredCount = 0;

        if (peekMode == PeekMode.FlyAway)
        {
            var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);

            foreach (var info in _savedWindows)
            {
                if (!NativeMethods.IsWindow(info.Handle))
                    continue;

                NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_SHOWNOACTIVATE);

                NativeMethods.RECT startBounds = GetCurrentBounds(info);
                NativeMethods.RECT endBounds = GetRestoreBounds(info);
                animationWindows.Add(new AnimatedWindow(info.Handle, startBounds, endBounds));
            }

            AnimateWindows(animationWindows);
        }

        // Restore in reverse order (bottom windows first) to preserve Z-order
        for (int i = _savedWindows.Count - 1; i >= 0; i--)
        {
            var info = _savedWindows[i];

            // Skip windows that were destroyed while we were peeking
            if (!NativeMethods.IsWindow(info.Handle))
            {
                AppDiagnostics.LogWindow("Skipping destroyed window", info.Handle);
                continue;
            }

            AppDiagnostics.LogWindow("Restoring window", info.Handle);
            RestoreWindow(info);
            restoredCount++;
        }

        _savedWindows.Clear();
        _maximizedWindows.Clear();
        AppDiagnostics.Log("Restore list cleared");
        AppDiagnostics.Metric($"RestoreAll: {restoredCount} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Restores a single captured window without trampling state that the shell owns.
    ///
    /// For a window arranged in a Windows 11 snap group, <c>GetWindowRect</c> returns the
    /// snapped (split) rect while <c>WINDOWPLACEMENT.rcNormalPosition</c> still holds the
    /// pre-snap rect. Calling <c>SetWindowPlacement</c> therefore teleports the window back
    /// to its pre-snap position and dissolves the split. Un-minimizing with
    /// <c>SW_SHOWNOACTIVATE</c> instead lets Windows restore the window's pre-minimize state
    /// (including its snap slot) without activating it. Only when that natural restore
    /// does not land on the captured geometry do we force the rect with <c>SetWindowPos</c>.
    /// </summary>
    private static void RestoreWindow(WindowInfo info)
    {
        bool wasMaximized = info.Placement.showCmd == NativeMethods.SW_MAXIMIZE;

        if (wasMaximized)
        {
            // Maximized windows are never part of a snap group; restore the maximized
            // state exactly (un-minimizing with SW_SHOWNOACTIVATE re-maximizes too,
            // so SetWindowPlacement is only a fallback).
            if (NativeMethods.IsIconic(info.Handle) || !NativeMethods.IsWindowVisible(info.Handle))
            {
                NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_SHOWNOACTIVATE);
                AppDiagnostics.LogWindow("Restore: re-showed captured-maximized window", info.Handle);
            }

            if (!NativeMethods.IsZoomed(info.Handle) || !RectsMatch(GetCurrentBounds(info), info.Bounds))
            {
                AppDiagnostics.LogWindow("Restore: re-applying maximized placement", info.Handle);
                var placement = info.Placement;
                placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                NativeMethods.SetWindowPlacement(info.Handle, ref placement);
            }

            return;
        }

        if (NativeMethods.IsIconic(info.Handle) || !NativeMethods.IsWindowVisible(info.Handle))
        {
            // Restores the pre-minimize state (snapped windows return to their snap slot)
            // without activating the window. The visibility check covers apps that hide
            // themselves while being minimized ("minimize to tray"); SetWindowPlacement
            // used to bring those back, so keep that contract.
            NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_SHOWNOACTIVATE);
        }
        else if (NativeMethods.IsZoomed(info.Handle))
        {
            // The window was maximized behind our back while peeking; return it to normal.
            NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_RESTORE);
        }

        // Last-resort geometry fix-up. Skipped when the window is already on the captured
        // rect (the normal case) — and skipping it is what keeps a snap group intact.
        if (RectsMatch(GetCurrentBounds(info), info.Bounds))
        {
            AppDiagnostics.LogWindow("Restore: geometry already correct, snap state left intact", info.Handle);
            return;
        }

        AppDiagnostics.LogWindow("Restore: rect mismatch, forcing captured bounds", info.Handle);
        int width = Math.Max(1, info.Bounds.Right - info.Bounds.Left);
        int height = Math.Max(1, info.Bounds.Bottom - info.Bounds.Top);
        NativeMethods.SetWindowPos(
            info.Handle,
            IntPtr.Zero,
            info.Bounds.Left,
            info.Bounds.Top,
            width,
            height,
            NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE
            | NativeMethods.SWP_NOOWNERZORDER
            | NativeMethods.SWP_NOSENDCHANGING
            | NativeMethods.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Compares two rects with a small tolerance for DPI / non-client rounding.</summary>
    private static bool RectsMatch(NativeMethods.RECT a, NativeMethods.RECT b)
    {
        const int tolerance = 2;
        return Math.Abs(a.Left - b.Left) <= tolerance
            && Math.Abs(a.Top - b.Top) <= tolerance
            && Math.Abs(a.Right - b.Right) <= tolerance
            && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
    }

    /// <summary>
    /// Determines whether a window should be captured for peek/restore.
    /// Filters out system chrome, invisible windows, tool windows, etc.
    /// </summary>
    private static bool ShouldTrackWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (NativeMethods.IsIconic(hwnd))
            return false;

        // Skip owned windows — they follow their owner
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
            return false;

        // Skip cloaked windows (other virtual desktops, hidden UWP apps)
        if (NativeMethods.IsWindowCloaked(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(className))
            return false;

        // Skip shell and system windows
        if (IsExcludedClass(className))
            return false;

        // Skip tool windows (floating palettes, etc.)
        long exStyle = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            return false;

        return true;
    }

    private static bool IsExcludedClass(string className)
    {
        return className switch
        {
            "Progman" => true,
            "WorkerW" => true,
            "Shell_TrayWnd" => true,
            "Shell_SecondaryTrayWnd" => true,
            "NotifyIconOverflowWindow" => true,
            "DV2ControlHost" => true,            // Start menu (Win10)
            "Windows.UI.Core.CoreWindow" => true, // Start, Action Center
            _ => false
        };
    }

    private static NativeMethods.RECT GetCurrentBounds(WindowInfo window)
    {
        if (NativeMethods.GetWindowRect(window.Handle, out NativeMethods.RECT bounds))
            return bounds;

        return window.Bounds;
    }

    private static NativeMethods.RECT GetRestoreBounds(WindowInfo window)
    {
        if (window.Placement.showCmd == NativeMethods.SW_MAXIMIZE)
            return window.Placement.rcNormalPosition;

        return window.Bounds;
    }

    private static NativeMethods.RECT ComputeFlyAwayTarget(NativeMethods.RECT startBounds)
    {
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref startBounds, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo);
        NativeMethods.RECT screenBounds = monitorInfo.rcWork;

        int width = Math.Max(1, startBounds.Right - startBounds.Left);
        int height = Math.Max(1, startBounds.Bottom - startBounds.Top);
        int centerX = startBounds.Left + (width / 2);
        int centerY = startBounds.Top + (height / 2);

        int screenWidth = screenBounds.Right - screenBounds.Left;
        int screenHeight = screenBounds.Bottom - screenBounds.Top;

        bool moveLeft = centerX < screenBounds.Left + (screenWidth / 2);
        bool moveUp = centerY < screenBounds.Top + (screenHeight / 2);

        int targetLeft = moveLeft
            ? screenBounds.Left - width - OffscreenMargin
            : screenBounds.Right + OffscreenMargin;

        int targetTop = moveUp
            ? screenBounds.Top - height - OffscreenMargin
            : screenBounds.Bottom + OffscreenMargin;

        return new NativeMethods.RECT
        {
            Left = targetLeft,
            Top = targetTop,
            Right = targetLeft + width,
            Bottom = targetTop + height
        };
    }

    private void AnimateWindows(IReadOnlyList<AnimatedWindow> windows)
    {
        if (windows.Count == 0)
            return;

        int durationMs = AnimationDurationMs;
        int sleepMs = Math.Max(1, durationMs / AnimationSteps);
        AppDiagnostics.Log($"Animation: {durationMs}ms over {AnimationSteps} steps (speed={AnimationSpeed:0.##}x)");
        const uint flags = NativeMethods.SWP_NOACTIVATE
                         | NativeMethods.SWP_NOZORDER
                         | NativeMethods.SWP_NOOWNERZORDER
                         | NativeMethods.SWP_NOSENDCHANGING
                         | NativeMethods.SWP_ASYNCWINDOWPOS;

        for (int step = 1; step <= AnimationSteps; step++)
        {
            // Allow caller to cancel animation mid-flight (e.g. user clicked again)
            if (CancelAnimation)
            {
                AppDiagnostics.Log($"Animation cancelled at step {step}/{AnimationSteps}");
                break;
            }

            double progress = EaseOutCubic(step / (double)AnimationSteps);

            foreach (var window in windows)
            {
                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                NativeMethods.RECT frame = LerpRect(window.StartBounds, window.EndBounds, progress);
                int width = Math.Max(1, frame.Right - frame.Left);
                int height = Math.Max(1, frame.Bottom - frame.Top);

                NativeMethods.SetWindowPos(
                    window.Handle,
                    IntPtr.Zero,
                    frame.Left,
                    frame.Top,
                    width,
                    height,
                    flags);
            }

            PumpingSleep(sleepMs);
        }
    }

    /// <summary>
    /// Sleep for the specified duration while pumping Windows messages,
    /// so the UI thread stays responsive during animation.
    /// </summary>
    private static void PumpingSleep(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        var msg = new NativeMethods.MSG();
        while (Environment.TickCount64 < deadline)
        {
            // Process all pending messages (mouse, keyboard, paint, etc.)
            while (NativeMethods.PeekMessage(ref msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            // Sleep 1ms to avoid busy-spin, then let the next iteration
            // check for new messages or exit if time is up.
            Thread.Sleep(1);
        }
    }

    private static double EaseOutCubic(double t)
    {
        double inverse = 1d - t;
        return 1d - (inverse * inverse * inverse);
    }

    private static NativeMethods.RECT LerpRect(NativeMethods.RECT from, NativeMethods.RECT to, double t)
    {
        return new NativeMethods.RECT
        {
            Left = Lerp(from.Left, to.Left, t),
            Top = Lerp(from.Top, to.Top, t),
            Right = Lerp(from.Right, to.Right, t),
            Bottom = Lerp(from.Bottom, to.Bottom, t)
        };
    }

    private static int Lerp(int from, int to, double t)
    {
        return (int)Math.Round(from + ((to - from) * t));
    }

    private record WindowInfo(IntPtr Handle, NativeMethods.WINDOWPLACEMENT Placement, NativeMethods.RECT Bounds);
    private record AnimatedWindow(IntPtr Handle, NativeMethods.RECT StartBounds, NativeMethods.RECT EndBounds);
}
