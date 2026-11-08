using System;
using System.Collections.Generic;

namespace Keeper;

/// <summary>别的程序正占着一块独占全屏吗 —— 占着的话这一发淡入就不来。
///
/// 契约那一句是 <c>.scratch/keeper-contract/spec.md</c>（<c>## Solution</c>）：「Nothing appears at all while another
/// application owns an exclusive full-screen surface, unless <c>blockWhenFullscreen</c> is off」，
/// 配置表里那句是「Stay out of games, presentations and calls」。状态机那一侧从 2026-09-25 就认
/// <c>blocked</c>（<see cref="Scheduler.Step"/>），缺的一直是这里 —— 之前 <c>App.cs</c> 永远传 false，
/// 所以那个键拧哪头都一样。
///
/// 判定的规矩就是下面这五条，一条没新加：
/// 前台窗 → 可见 → 不是我们自己那个进程 → 不是 shell 画桌面那几扇 → 它的矩形盖满<b>整块</b>
/// 监视器。最后那一条是分开「真全屏」和「最大化窗」的：最大化的窗让出任务栏那一条，
/// 所以盖不满，也就该被淡入打断。用整屏矩形（<c>rcMonitor</c>）而不是工作区，为的就是这个分别。
///
/// 判定本身是纯的（<see cref="Covers"/>、<see cref="IsShellForeground"/>），所以那五条规矩在
/// <c>--check</c> 里就能各判一次；<see cref="OwnsScreen"/> 只负责从 OS 取那四份数。
///
/// <c>internal</c> 和 <c>Win32</c> 同一档：签名里出现了 <c>Win32.Rect</c>，公开出去就是
/// 「可访问性不一致」那一条编译错误（<c>Wallpaper</c> 也是这么处理的）。</summary>
internal static class Fullscreen
{
    /// <summary>shell 自己画桌面 / 任务栏的那几扇：它们本来就铺满整块屏，把「有人在全屏」判在它们身上，
    /// 遮罩就永远不来了。
    ///
    /// <b>这份名单只回答「前台那一扇是不是 shell 自己的」</b>：它不含 <c>SHELLDLL_DefView</c>
    /// 和图标层（那两扇当不上前台），也不含壁纸层那一份管的东西。另两张名单在
    /// <see cref="DesktopLayer"/>，问的是「那一点露没露到壁纸」和「枚举桌面那一趟要不要跳过它」。
    /// 三问三单<b>故意不相等</b>，自检里 <c>fullscreen</c> 那一组钉着这个不相等，
    /// 谁把它们「统一」成一份就当场报。</summary>
    private static readonly string[] ShellClasses = [
        "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow",
    ];

    /// 这一次淡入该不该躲开。每 0.2 秒问一次（<c>App._Process</c> 那一发），四个 P/Invoke，
    /// 没有分配，所以不问第二次「多久问一次」。
    public static bool OwnsScreen()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd)) return false;

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;   // 遮罩自己、设置窗、日历

        var className = Win32.ClassName(hwnd);
        if (IsShellForeground(className)) return false;

        if (!Win32.GetWindowRect(hwnd, out var window)) return false;
        var info = new Win32.MonitorInfo
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MonitorInfo>(),
        };
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref info)) return false;

        return Covers(window, info.Monitor);
    }

    /// 屏幕上那一行实测结果：把「为什么这一发没来」说清楚，而不是只说「没来」。
    public static string Measurement()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "没有前台窗口";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        Win32.GetWindowRect(hwnd, out var window);
        var ours = pid == (uint)Environment.ProcessId;
        return $"前台=0x{hwnd.ToInt64():X} '{Win32.ClassName(hwnd)}' pid={pid}" +
               $"{(ours ? "（我们自己）" : "")} 可见={Win32.IsWindowVisible(hwnd)} 矩形={window}";
    }

    /// 纯的那一半：窗口矩形盖没盖满整块屏（含任务栏那一条）。
    public static bool Covers(Win32.Rect window, Win32.Rect desktop) =>
        window.Width > 1 && window.Height > 1 &&
        window.Left <= desktop.Left && window.Top <= desktop.Top &&
        window.Right >= desktop.Right && window.Bottom >= desktop.Bottom;

    /// <summary>前台那一扇是不是 shell 自己那几扇之一（见 <see cref="ShellClasses"/> 那三问三单）。</summary>
    public static bool IsShellForeground(string className) => Array.IndexOf(ShellClasses, className) >= 0;

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Detection = new(
        () => "全屏探测通过：盖满整块屏算、让出任务栏那一条不算、差一格不算、副屏上同一口径、" +
              "shell 那几扇认得出来、三问三单各自不相等（拿「统一名单」当好意改的，这一组当场报）",
        "全屏探测", SelfTest);

    /// 自检里那一发：五条规矩各判一次，加一条「最大化不算全屏」——那一条是这一整个类存在的理由，
    /// 也是最容易被「矩形差不多大」糊过去的一条。
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        void Expect(string what, bool got, bool want)
        {
            if (got != want) problems.Add($"全屏探测「{what}」读到了 {got}，该是 {want}");
        }

        var desk = new Win32.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
        Expect("盖满整块屏，含任务栏那一条", Covers(new Win32.Rect { Right = 2560, Bottom = 1440 }, desk), true);
        Expect("最大化：让出任务栏那一条", Covers(new Win32.Rect { Right = 2560, Bottom = 1400 }, desk), false);
        Expect("往屏幕外多伸出一格", Covers(new Win32.Rect { Left = -1, Top = -1, Right = 2561, Bottom = 1441 }, desk), true);
        Expect("右边差一个像素", Covers(new Win32.Rect { Right = 2559, Bottom = 1440 }, desk), false);
        Expect("1x1 的那种「盖满」", Covers(new Win32.Rect { Right = 1, Bottom = 1 }, desk), false);
        // 第二块屏：负坐标那一侧，同一个判据。差一格还是差一格。
        var second = new Win32.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        Expect("副屏上盖满", Covers(new Win32.Rect { Left = -1920, Right = 0, Bottom = 1080 }, second), true);
        Expect("副屏上差左边一格", Covers(new Win32.Rect { Left = -1919, Right = 0, Bottom = 1080 }, second), false);
        Expect("shell 那几扇认得出来", IsShellForeground("WorkerW"), true);
        Expect("游戏窗不是 shell", IsShellForeground("SDL_app"), false);

        // 三问三单故意不相等（工单 15）：这一问是「前台那一扇是不是 shell 铺满整块屏的窗」，
        // DesktopLayer 那两问问的是「那一点露没露到壁纸」「枚举桌面那一趟跳不跳过它」。
        // 拿「统一成一份名单」当好意来改，下面这几发当场报。
        Expect("DefView 当不上前台，但它是壁纸层（两问不该同答）",
            IsShellForeground("SHELLDLL_DefView") == DesktopLayer.IsWallpaperLayer("SHELLDLL_DefView"), false);
        Expect("任务栏算前台那一扇，但不算壁纸层（两问不该同答）",
            IsShellForeground("Shell_TrayWnd") == DesktopLayer.IsWallpaperLayer("Shell_TrayWnd"), false);
        Expect("图标层只在枚举那一趟被跳过（两问不该同答）",
            IsShellForeground(DesktopLayer.IconViewClass)
                == DesktopLayer.IsShellDesktopWindow(DesktopLayer.IconViewClass), false);
        Expect("Progman 两边都认（这一格是故意重合的）",
            IsShellForeground("Progman") && DesktopLayer.IsWallpaperLayer("Progman"), true);

        return problems;
    }
}
