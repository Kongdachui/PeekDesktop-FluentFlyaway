using System;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// User-facing strings for the tray menu and the dialogs it opens.
///
/// The menu follows the Windows display language: Chinese when the user's Windows
/// UI language is Chinese, otherwise the original English text. Native AOT builds
/// run with invariant globalization, so this asks the OS directly instead of
/// relying on CultureInfo.
/// </summary>
internal static class Strings
{
    private const int LanguageIdPrimaryMask = 0x3FF;
    private const int LanguageIdChinese = 0x04;

    public static readonly bool UseChinese = DetectChineseUi();

    // --- Tray menu ---
    public static string Enabled => T("启用 PeekDesktop", "Enabled");
    public static string StartWithWindows => T("开机启动", "Start with Windows");
    public static string RequireDoubleClick => T("需要双击触发", "Require Double-Click");
    public static string PeekOnDesktopClick => T("点击桌面空白处查看桌面", "Peek on Desktop Click");
    public static string PeekOnTaskbarClick => T("点击任务栏空白处查看桌面", "Peek on Taskbar Click");
    public static string RestoreOnAppSwitch => T("切换应用时恢复所有窗口", "Restore All Windows on App Switch");
    public static string PauseWhileGaming => T("游戏/全屏应用时暂停", "Pause While Gaming / Full-Screen");
    public static string ShowDesktopMode => T("显示桌面（系统原生）", "Show Desktop (Explorer)");
    public static string FlyAwayMode => T("飞走动画（实验性）", "Fly Away (Experimental)");
    public static string AboutMenu => T("关于 PeekDesktop", "About PeekDesktop");
    public static string CheckForUpdates => T("检查更新", "Check for Updates");
    public static string AutoCheckForUpdates => T("自动检查更新", "Auto-Check for Updates");
    public static string Exit => T("退出", "Exit");

    // --- Version labels ---
    public static string UnknownVersion => T("未知版本", "unknown version");
    public static string DevBuild => T("开发版", "dev build");

    // --- Peek mode names (tray tooltip) ---
    public static string PeekModeName(PeekMode mode) => mode switch
    {
        PeekMode.Minimize => T("经典最小化", "Classic Minimize"),
        PeekMode.FlyAway => T("飞走动画", "Fly Away"),
        PeekMode.NativeShowDesktop => T("系统原生显示桌面", "Native Show Desktop"),
        _ => T("Peek", "Peek")
    };

    public static string TooltipForMode(PeekMode mode) => $"PeekDesktop - {PeekModeName(mode)}";

    // --- About dialog ---
    public static string AboutTitle => T("关于 PeekDesktop", "About PeekDesktop");

    public static string AboutText(string version)
    {
        // "未知版本"/"dev build" style labels should not get a bogus "v" prefix.
        string versionLabel = version.Length > 0 && char.IsDigit(version[0]) ? $"v{version}" : version;

        return UseChinese
            ? $"PeekDesktop {versionLabel}\n\n" +
              "点击桌面壁纸即可查看桌面，\n" +
              "就像 macOS Sonoma 一样。\n\n" +
              "点击任意窗口或任务栏即可恢复。\n" +
              "“显示方式”可在系统原生显示桌面\n" +
              "和飞走动画之间切换。\n\n" +
              "更新来自 GitHub Releases。\n\n" +
              "github.com/shanselman/PeekDesktop"
            : $"PeekDesktop {versionLabel}\n\n" +
              "Click your desktop wallpaper to peek at your desktop,\n" +
              "just like macOS Sonoma.\n\n" +
              "Click any window or the taskbar to restore.\n" +
              "Peek Style lets you switch between Explorer show desktop\n" +
              "and fly-away mode.\n\n" +
              "Updates come from GitHub Releases.\n\n" +
              "github.com/shanselman/PeekDesktop";
    }

    // --- Update flow ---
    public static string UpdateDialogTitle => T("PeekDesktop 更新", "PeekDesktop Update");
    public static string AlreadyChecking => T("PeekDesktop 正在检查更新，请稍候。", "PeekDesktop is already checking for updates.");
    public static string UpdateErrorTitle => T("更新出错", "Update Error");
    public static string UpdateAvailableTitle => T("有可用更新", "Update Available");
    public static string UpdateInstalledTitle => T("更新已安装", "Update Installed");
    public static string UpdateBalloonTitle => T("PeekDesktop 有可用更新", "PeekDesktop Update Available");
    public static string StartupFailedTitle => T("PeekDesktop 启动失败", "PeekDesktop failed to start");
    public static string UpdateFolderNotWritable => T(
        "PeekDesktop 无法在当前文件夹自动更新（权限不足）。\n\n请把 PeekDesktop 移到当前用户可写的文件夹，或手动下载更新。",
        "PeekDesktop cannot update itself in this folder (permission denied).\n\nMove PeekDesktop to a user-writable folder, or download the update manually.");

    public static string UpToDate(string version) => UseChinese
        ? $"当前已是最新版本 PeekDesktop（{version}）。"
        : $"You're already on the latest version of PeekDesktop ({version}).";

    public static string CheckFailed(string detail) => UseChinese
        ? $"PeekDesktop 检查更新失败。\n\n{detail}"
        : $"PeekDesktop couldn't check for updates.\n\n{detail}";

    public static string UpdateAvailablePrompt(string? version) => UseChinese
        ? (version is null
            ? "PeekDesktop 有新版本可用。\n\n现在下载并安装吗？安装完成后 PeekDesktop 会自动重启。"
            : $"PeekDesktop {version} 已发布。\n\n现在下载并安装吗？安装完成后 PeekDesktop 会自动重启。")
        : (version is null
            ? "A new version of PeekDesktop is available.\n\nDownload and install it now? PeekDesktop will restart automatically."
            : $"PeekDesktop {version} is available.\n\nDownload and install it now? PeekDesktop will restart automatically.");

    public static string UpdateInstalledManually => T(
        "更新已安装，但 PeekDesktop 无法自动重启。\n\n请手动启动 PeekDesktop。",
        "The update was installed but PeekDesktop could not restart automatically.\n\nPlease start PeekDesktop manually.");

    public static string InstallFailed(string detail) => UseChinese
        ? $"PeekDesktop 安装更新失败。\n\n{detail}"
        : $"PeekDesktop couldn't install the update.\n\n{detail}";

    public static string UpdateBalloonText(string version) => UseChinese
        ? $"版本 {version} 已发布，点击此处下载并安装。"
        : $"Version {version} is available. Click here to download and install.";

    private static string T(string chinese, string english) => UseChinese ? chinese : english;

    /// <summary>Raw Windows UI LANGID (exposed so the interop harness can verify detection independently).</summary>
    internal static ushort GetUiLanguageId() => GetUserDefaultUILanguage();

    private static bool DetectChineseUi()
    {
        try
        {
            int languageId = GetUiLanguageId();
            return (languageId & LanguageIdPrimaryMask) == LanguageIdChinese;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"UI language detection failed, defaulting to English: {ex.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
