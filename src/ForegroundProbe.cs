using System;
using System.Collections.Generic;
using System.Text;

namespace Keeper;

/// <summary>「前台是哪个应用」那一档测量程序（工单 01）。<b>产出发数，不产出产品代码。</b>
///
/// 要量的那条链是 <c>GetForegroundWindow</c> → <c>GetWindowThreadProcessId</c> →
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> → <c>QueryFullProcessImageNameW</c>。
/// 待测清单在 <c>docs/research/detection-module-api-survey.md</c> 第 6 节，实测结果补记到第 1、3 节。
///
/// <b>为什么这一档要自己一个旗（--fg），不挂在 --check 上</b>：--check 是无头自检，
/// 那里每一组判据都是纯的、自己能造出输入的；这一档的输入是<b>屏幕上此刻真的占着前台的那个程序</b>，
/// 无头造不出来，所以它走 <c>--push</c> / <c>--sweep</c> 那一档的形状。
/// 它<b>不带退出码</b>（<see cref="Probe"/> 里那条取向）：没量到不是失败，测量程序不替「量不到」编一个失败出来。
///
/// <b>本档跑在 headless 下</b>（<c>--headless</c>），这不是省事，是测量程序的形状要求：
/// 非 headless 时 Godot 会开一扇自己的主窗，它一起抢前台，于是实测结果量的是「测量程序和目标
/// 谁的窗在前台」而不是目标。headless 下本进程一扇窗都没有，读到的前台只能是别人起的那个。
/// 每一发实测结果都带 <c>headless=</c> 这一栏，所以「那一发是哪种形状」不用回头去猜。
///
/// <b>实测结果自己定纪律</b>，五条，都是从 CODING_STANDARDS.md「取证方法论」搬过来的：
/// <list type="number">
/// <item>一条实测结果只算它自己那一个属性。<c>QUNS_BUSY</c> 不等于独占全屏、矩形盖满不等于看见了、
/// <c>GetGUIThreadInfo</c> 在失活瞬间给无效句柄是文档自带的坑 —— 所以它们<b>并排</b>印，不合并成一个结论。</item>
/// <item>先放一发「一定该通」进去当对照。本档的对照是<b>自己这个 pid</b>：
/// <c>OpenProcess</c> 一定能开、路径一定能问出来。它不成立的话，下面每一发失败都可能是测量程序坏了。</item>
/// <item>取词取自那一行自己。<c>类名</c>、<c>包名</c>、<c>AUMID</c>、QUNS 的名字都是当场读出来当场印，
/// 判据里不存一份会腐烂的副本。</item>
/// <item>推的和量的分开写。哪些是量的、哪些是推的，印在实测结果里。</item>
/// <item>「不通」要说得清是哪一档不通。句柄空、HRESULT 非 0、错误码是几，分三样印。</item>
/// </list>
///
/// <b>为什么 UWP 那两条路要并排跑</b>：前台是商店应用时，<c>GetForegroundWindow</c> 给的那扇
/// 顶层窗属于 <c>ApplicationFrameHost.exe</c>，不是应用自己。两条正路（枚举子窗找 pid 不同的那个 /
/// 问前台线程的焦点窗）在实测之前无法说谁更稳，所以两条各跑一次、每发都印，**不替它们选一条**。
/// 选哪一条是工单 01 的 <c>## Comments</c> 里的定案，不是这一档的实测结果。
internal static class ForegroundProbe
{
    /// <summary>采样间隔，秒。0.2 是 <c>App._Process</c> 问全屏的那一个间隔，
    /// 所以本档的实测结果密到能看见 0.2 秒内的一次切换。</summary>
    public const double SampleSeconds = 0.2;

    /// <summary>发一发「一定该通」的对照：自己这个 pid。
    ///
    /// 它量的是<b>测量程序自己通不通</b>，不是「前台是谁」。所以它的实测结果里只印身份那一族，
    /// 不带 QUNS、不带几何 —— 那些和「自己开不开得了自己」是两件事，混进一行就成了两个结论。
    /// </summary>
    public static string ControlRow()
    {
        var pid = (uint)System.Environment.ProcessId;
        var (opened, code, path) = Win32.ExePathOf(pid);
        return $"对照(一定该通) pid={pid} 打开={opened} 错误码={code} 路径={(path == "" ? "<空>" : path)}";
    }

    /// <summary>一发采样。所有字段取自这一行自己，不带任何缓存或上一发的结论。</summary>
    public static string Sample()
    {
        var headless = DisplayServerName() == "headless";
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return $"headless={headless} 前台=0 没有前台窗口";

        var thread = Win32.GetWindowThreadProcessId(hwnd, out var framePid);
        var className = Win32.ClassName(hwnd);
        var title = Win32.WindowText(hwnd);
        var (opened, openCode, path) = Win32.ExePathOf(framePid);
        var (pkgCode, pkg) = Win32.PackageFullNameOf(framePid);

        // 前台那扇窗自己的 AUMID —— 这一格量的是「窗的身份」，和「进程的包名」分属两样，
        // 两者一致不一致本身就是实测结果（不强制它们一致，也不假设它们一致）。
        var aumid = Win32.WindowAumid(hwnd);
        var aumidText = aumid.Aumid == ""
            ? $"<没有>(店={aumid.StoreReplied} 店码={aumid.StoreCode} 值码={aumid.ValueCode})"
            : aumid.Aumid;

        var row = new StringBuilder();
        row.Append($"headless={headless} 前台=0x{hwnd.ToInt64():X} 类='{className}' " +
                   $"标题='{Trim(title)}' 线程={thread} 顶层pid={framePid} " +
                   $"打开={opened} 错误码={openCode} 路径={(path == "" ? "<问不出>" : path)}");
        row.Append($" 包名码={pkgCode} 包名={(pkg == "" ? "<空>" : pkg)}");
        row.Append($" 窗AUMID={aumidText}");

        // UWP 那两条路，**只在顶层窗真是 ApplicationFrameHost 时才问**：
        // 非 UWP 的前台去枚举子窗，量的是「随便哪个应用的子窗长什么样」，
        // 那一条会被读成「UWP 路线在这里不成立」，而它其实压根不是 UWP 场景。
        if (className == "ApplicationFrameWindow")
        {
            var child = UwpCoreWindowByEnum(hwnd);
            var focus = UwpCoreWindowByFocus();
            row.Append($" [UWP]枚举子窗={child}");
            row.Append($" 焦点窗={focus}");
        }

        // 勿扰状态与几何判定并排。两样**不合成一个结论**：QUNS_BUSY 不等于独占全屏、
        // 矩形盖满整块屏也不等于「看见了」，它们各自只算自己那一个属性。
        // （本函数一个参数都没有，见 Win32 那一段的正本实测结果，所以这里没有「传 0 还是传前台」那一发。）
        var q = Win32.QueryUserNotificationState();
        row.Append($" [勿扰]hr={q.HResult} {Win32.NotificationStateName(q.State)}");
        // 几何那一半用产品自己的判据（<see cref="Fullscreen.OwnsScreen"/>），只读不改：
        // 本档要的是「同一个瞬间、几何判据说的是什么」，所以问的必须是将来要走的那一条。
        row.Append($" [几何]盖满整块屏={Fullscreen.OwnsScreen()} {Fullscreen.Measurement()}");
        return row.ToString();
    }

    /// <summary>第一条路：枚举 <c>ApplicationFrameWindow</c> 的子窗，找 pid 不同的那个。
    /// 这一发的规矩是<b>只报观察到的事实</b>：pid 是不是真的不同、那个句柄是不是真是子窗，
    /// 都原样印出来；<c>GetGUIThreadInfo</c> 给的句柄不在这一发里混算。</summary>
    private static string UwpCoreWindowByEnum(IntPtr frame)
    {
        var found = IntPtr.Zero;
        var framePid = FramePid(frame);
        var seen = 0;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            seen++;
            Win32.GetWindowThreadProcessId(child, out var pid);
            if (found == IntPtr.Zero && pid != 0 && pid != framePid) found = child;
            return true;   // 枚举完整棵子树，不在第一个就停：那会把「第一个子窗」误当成规则
        }, IntPtr.Zero);
        return Format(found) + $" 子窗数={seen}";
    }

    /// <summary>第二条路：<c>GetGUIThreadInfo(0)</c> → <c>hwndFocus</c>。
    /// 文档自带一个坑：失活那一瞬间可能给无效句柄，所以<b>句柄是不是还指着窗</b>要一起印 ——
    /// 只印句柄会把「拿到一个死的」读成「拿到了」。</summary>
    private static string UwpCoreWindowByFocus()
    {
        var info = new Win32.GuiThreadInfo
        {
            CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.GuiThreadInfo>(),
        };
        var asked = Win32.GetGUIThreadInfo(0, ref info);
        // 失败时把错误码和 cbSize 一起印出来：**这一族曾经每一发都回 87**，
        // 而「87」和「这扇窗此刻没有焦点窗」在实测结果上长得一样。
        // 带上 cbSize 才分得开是「参数被拒」（测量程序坏了）还是「真的没焦点窗」。
        if (!asked)
            return $"<问不到> 错误码={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} " +
                   $"cbSize={info.CbSize} 有效句柄=false";
        if (info.HwndFocus == IntPtr.Zero) return "<没有焦点窗> 有效句柄=false";
        return Format(info.HwndFocus) + $" 有效句柄={Win32.IsWindow(info.HwndFocus)}";
    }

    private static uint FramePid(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>一扇窗的身份，四样并排：类名、pid、exe 路径、窗口 AUMID。
    /// <b>四样都不互相顶替</b> —— 「枚举路线和焦点路线答的不是同一个东西」正是要量的，
    /// 把其中一样省掉就成了替它们下了结论。</summary>
    private static string Format(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<没有>";
        var pid = FramePid(hwnd);
        var (opened, code, path) = Win32.ExePathOf(pid);
        var aumid = Win32.WindowAumid(hwnd);
        return $"0x{hwnd.ToInt64():X} '{Win32.ClassName(hwnd)}' pid={pid} " +
               $"路径={(opened && path != "" ? path : $"<问不出>(开={opened} 码={code})")} " +
               $"AUMID={(aumid.Aumid == "" ? "<没有>" : aumid.Aumid)}";
    }

    private static string Trim(string title) =>
        title.Length <= 40 ? title : title[..40] + "…";

    private static string DisplayServerName()
    {
        try { return Godot.DisplayServer.GetName(); }
        catch (Exception) { return "?"; }
    }

    /// <summary>要扫的那几档进程名。
    ///
    /// <b>这是量的选择，不是判据</b>，所以它是一份名字而不是一份 pid：pid 每次开机都不同，
    /// 抄 pid 进代码就是抄一份会腐烂的副本。名字在运行时从活着的进程表里现查
    /// （<see cref="PidOf"/>），查不到就印「没在跑」—— 那一档读不到要看得见，
    /// 悄悄跳过就成了「那一档没量到」和「那一档通了」分不开。
    ///
    /// 前两档是这一族里「一定该通」和「一定不通」的对照：自己这个 pid 一定开得了，
    /// 而以 SYSTEM 身份跑的那几个标准用户连查询权都拿不到（安全模型按设计如此）。
    /// 中间那一档是提权进程 —— 本轮量不到，理由见工单 01 的 <c>## Comments</c>。</summary>
    private static readonly (string Tier, string ProcessName)[] SweepTiers =
    [
        ("对照(一定该通)·自己", ""),
        ("普通进程", "explorer"),
        ("系统进程", "lsass"),
        ("系统进程", "winlogon"),
        ("系统进程", "wininit"),
        ("系统进程", "services"),
    ];

    /// <summary>按名字在活着的进程表里现查 pid。查不到回 0 —— 调用方要自己说「没在跑」。</summary>
    private static uint PidOf(string processName)
    {
        if (processName == "") return (uint)System.Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    return (uint)p.Id;
            }
            finally
            {
                p.Dispose();
            }
        }
        return 0;
    }

    /// <summary>三档对象各一发，量的<b>是 <c>OpenProcess</c> 那一发本身</b>。
    ///
    /// 为什么要单独扫一遍而不靠前台：<b>系统进程的窗当不了前台</b>，
    /// 所以「别的用户 / 系统进程那一档」在前台链路上永远量不到 —— 只能直接拿 pid 去问。
    /// 前台那一发量的是「此刻占着前台的是谁」，pid 扫描量的是「给一个 pid，问不问得出它的 exe 路径」。
    /// 两件事分开印，不合并。</summary>
    public static List<string> PidSweepRows()
    {
        var rows = new List<string>();
        foreach (var (tier, name) in SweepTiers)
        {
            var pid = PidOf(name);
            if (pid == 0)
            {
                rows.Add($"{tier} {name}：这一轮它没在跑，这一档没量到");
                continue;
            }
            var (opened, code, path) = Win32.ExePathOf(pid);
            var (pkgCode, pkg) = Win32.PackageFullNameOf(pid);
            rows.Add($"{tier} {name} pid={pid} 打开={opened} 错误码={code} " +
                     $"路径={(path == "" ? "<问不出>" : path)} 包名码={pkgCode} 包名={(pkg == "" ? "<空>" : pkg)}");
        }
        return rows;
    }

    /// <summary>旗后面跟的那个秒数。认不出来就用
    /// <see cref="FallbackSeconds"/>，不静默变成 0（0 那一发不发一实测结果，
    /// 而「一实测结果都没出来」和「量到了 0」在日志上是同一样东西）。</summary>
    public static double SecondsFrom(string[] args, string flag, double fallback)
    {
        var at = Array.IndexOf(args, flag);
        if (at < 0) return fallback;
        if (at + 1 < args.Length && double.TryParse(args[at + 1], out var seconds) && seconds > 0) return seconds;
        return fallback;
    }

    private const double FallbackSeconds = 30;
}
