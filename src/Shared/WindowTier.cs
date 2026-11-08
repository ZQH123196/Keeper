using System;
using System.Linq;

/// <summary>「屏幕上那一点归谁」那一份判据。<b>两个工程共用这一份源码</b>：
/// 产品工程（<c>Keeper.csproj</c>）和测量程序（<c>src/tools/DebugProbe/DebugProbe.csproj</c>）
/// 互不引用，所以同一个文件被两边各编一次 —— 判据只有一份，两边的读数才对得上。
///
/// <b>这个文件不带 namespace，是这么定的</b>：产品那侧的代码在 <c>namespace Keeper</c> 里，
/// 测量程序在 <c>namespace DebugProbe</c> 里。带了 namespace，两边看到的就是两个同名不同形的
/// <c>Owner</c>；不带，两边看到的才是同一个 <c>WindowTier</c>。所以两边都写全名
/// <c>WindowTier.Owner</c>，谁都不用加 <c>using</c>。
///
/// <b>2026-10-14</b>：第五档（<see cref="Owner.SystemWindow"/>）和那份判据从测量程序搬到这里。
/// 搬之前测量程序那份的注释写着名单「照产品那一侧 <c>DesktopLayer.SystemWindows</c> 抄的」——
/// <b>全仓搜 <c>SystemWindows</c>，代码里零成员</b>，出处指空。产品那一侧原来只有四档，
/// 于是第五档只剩测量程序认得；现在正本在这一份，两边都调它。</summary>
public static class WindowTier
{
    /// <summary>那一点归谁。判据用枚举，日志用 <see cref="Name"/> 那一句，两边共用一个来源。</summary>
    public enum Owner
    {
        NoWindow,
        Ours,
        WallpaperLayer,

        /// <summary>任务栏那一族和输入法那几个辅助窗。它们本来就在桌面上下，
        /// 压不到内容，**不是**「露到壁纸」，也不是「被别家窗挡住」。</summary>
        SystemWindow,

        AppWindow,
    }

    /// <summary>第五档认哪几种类名。2026-10-14 从测量程序那份原样搬过来
    /// （<c>src/tools/DebugProbe/Find.cs</c> 在 2026-10-14 之前的那一份）。正本现在在这里。</summary>
    private static readonly string[] SystemShellClasses =
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "MSTaskListWClass",
        "NotifyIconOverflowWindow",
        "TrayNotifyWnd",
        "IME",
        "MSCTFIME UI",
        "WinUIDesktopWin32WindowClass",
    };

    /// <summary>那一种类名是不是第五档。匹配用序数比较：类名大小写不一样就是两扇窗。</summary>
    public static bool IsSystemShell(string className) =>
        SystemShellClasses.Contains(className, StringComparer.Ordinal);

    /// <summary>上面那一档判据的实现。先后顺序是 <c>Ours</c> → <c>NoWindow</c> →
    /// <c>WallpaperLayer</c> → <c>SystemWindow</c> → <c>AppWindow</c>。
    ///
    /// <b><paramref name="wallpaperLayer"/> 为什么从外头给进来</b>：壁纸层那三种类名的名单
    /// 两边各留一份，而且**故意不合成一份**（产品那一侧是 <c>DesktopLayer.IsWallpaperLayer</c>，
    /// 测量程序是 <c>Find.IsWallpaperLayer</c>）。这一份只管五档的先后顺序和第五档自己那份名单；
    /// 壁纸层那一问的答案，由调用方拿自己那份名单判完了再递进来。
    ///
    /// <paramref name="rootClass"/> 只给第五档用（壁纸层那一档已经由上面那个参数答完了）。</summary>
    public static Owner TierOf(nint hit, nint root, string rootClass, nint own, bool wallpaperLayer) =>
        own != IntPtr.Zero && (hit == own || root == own) ? Owner.Ours
        : hit == IntPtr.Zero ? Owner.NoWindow
        : wallpaperLayer ? Owner.WallpaperLayer
        : IsSystemShell(rootClass) ? Owner.SystemWindow
        : Owner.AppWindow;

    /// <summary>实测结果里那一档的中文说法。判据用枚举，日志用这一句，两边共用一个来源。</summary>
    public static string Name(Owner owner) => owner switch
    {
        Owner.Ours => "归我这扇窗",
        Owner.WallpaperLayer => "归壁纸层",
        Owner.SystemWindow => "归系统窗",
        Owner.AppWindow => "归 app 窗",
        _ => "什么都没有",
    };
}