using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace DebugProbe;

/// <summary>
/// 把日历那一扇真的挂进桌面层，量它挂进去之后是什么样。
///
/// <para><b>这一发为什么存在</b>：产品现在走的是「顶层窗 + owner + 每秒把自己插回桌面 z 序」
/// 那一族，而那一族在这台机器上收敛不了 —— <c>DesktopLayer.Where</c> 每一秒都答
/// 「壁纸层那一扇被翻到我们上面去了」，<c>KeepAtBottom</c> 就每秒重发一次 <c>SetWindowPos</c>，
/// 收之前和收之后一模一样（读数见 <c>docs/research/desktop-layer-and-window-measurements.md</c>）。
/// 社区做法（Lively / Wallpaper Engine / Rainmeter 那一族）是把自己的窗挂成桌面层的<b>子窗</b>：
/// 子窗不参与顶层 z 序，不需要每秒抢，Win+D 也不扫它。</para>
///
/// <para><b>这一发量什么</b>：挂进去之后连读若干秒，每秒报一遍爹、样式、z 序、命中测试。
/// <b>决定性的读数是产品那每秒一下会怎么动它</b> —— 顶层那一族和子窗那一族互斥，
/// 产品不停抢的话，这一发会直接把那个互斥摆出来。</para>
///
/// <para><b>它凭什么敢动别人的窗</b>：它改的是日历那一扇（产品自己的），产品随时能重摆回来；
/// 桌面层那几扇只读不写。真出了岔子，把产品重摆一次（托盘里的「桌面日历」）就回去了。</para>
///
/// <para>出处：<c>.scratch/debug-tool/SPEC.md</c>。取向在 <c>docs/adr/</c>。</para>
/// </summary>
internal static class Embed
{
    // ---- 这一族要摸的 Win32（量具自己声明，不和产品那份共用） --------------------

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000;
    private const long WsPopup = 0x80000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr h, ref System.Drawing.Point p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr h, int index);

    private static long StyleOf(IntPtr h) => (long)GetWindowLongPtr(h, GwlStyle).ToInt64();

    private static string DescribeStyle(long style) =>
        ((style & WsPopup) != 0 ? "|POPUP" : "") +
        ((style & WsChild) != 0 ? "|CHILD" : "") +
        (style == 0 ? "（0）" : "");

    private static string DescribeExStyle(long style)
    {
        var bits = new List<string>();
        if ((style & 0x00000020) != 0) bits.Add("LAYERED");
        if ((style & 0x00000080) != 0) bits.Add("TRANSPARENT");
        if ((style & 0x08000000) != 0) bits.Add("NOACTIVATE");
        if ((style & 0x00000080) != 0) bits.Add("TOOLWINDOW");
        return bits.Count == 0 ? "（无）" : "|" + string.Join("|", bits.Distinct());
    }

    // ---- 三层形状：Progman / DefView / 图标层 ----------------------------------

    private static IntPtr Progman() => FindWindow("Progman", null);

    private static IntPtr DefView(IntPtr progman)
    {
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        foreach (var w in Windows.ChildWindows(progman))
            if (w.ClassName == "SHELLDLL_DefView") return w.Handle;
        return IntPtr.Zero;
    }

    /// <summary>
    /// 图标层。<b>这一发读的是 z 序最顶上那扇</b>，不是按类名在 Progman / WorkerW 里二选一 ——
    /// 实测 Progman 底下挂着两个 <c>SysListView32</c>（一个在 <c>SHELLDLL_DefView</c> 里，
    /// 一个直连），二选一必然挑错，得问「哪一个真的画着图标」。
    /// </summary>
    private static IntPtr IconLayer()
    {
        var candidates = Windows.IconLayerCandidates();
        if (candidates.Count == 0) return IntPtr.Zero;
        foreach (var w in candidates)
            if (w.Handle != IntPtr.Zero && Windows.IconCount(w.Handle) > 0) return w.Handle;
        return candidates[0].Handle;
    }

    // ---- 读一遍结构 ----------------------------------------------------------

    private static string ReadOnce(IntPtr calendar, IntPtr iconLayer, string tag)
    {
        var parent = Windows.ParentOf(calendar);
        var root = Windows.RootOf(calendar);
        var rect = Windows.RectOf(calendar);
        var owner = GetWindowLongPtr(calendar, GwlExStyle);
        var hit = Windows.At(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

        // 日历那一扇相对图标层的先后：从日历往上数，数到几扇撞上图标层。
        var depth = RankAbove(calendar, iconLayer);
        var iconRank = RankAbove(iconLayer, calendar);

        var sb = new StringBuilder();
        sb.Append($"[{tag}] 爹=0x{parent.ToInt64():X}/{Windows.ClassOf(parent)} ");
        sb.Append($"根=0x{root.ToInt64():X}/{Windows.ClassOf(root)} ");
        sb.Append($"样式={DescribeStyle(StyleOf(calendar))} ");
        sb.Append($"矩形=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) ");
        sb.Append($"中心命中=0x{hit.ToInt64():X}/{Windows.ClassOf(hit)} ");
        sb.Append($"日历↑到图标层={depth} 图标层↑到日历={iconRank}");
        if (owner != IntPtr.Zero) sb.Append($" GWL_HwndParent=0x{owner.ToInt64():X}");
        return sb.ToString();
    }

    /// <summary>从 <paramref name="from"/> 往上走，数第几扇撞上 <paramref name="target"/>。
    /// 撞不上答 −1。</summary>
    private static int RankAbove(IntPtr from, IntPtr target)
    {
        if (target == IntPtr.Zero || from == IntPtr.Zero) return -1;
        var at = FirstAbove(from);
        for (var steps = 0; at != IntPtr.Zero && steps < 600; steps++)
        {
            if (at == target) return steps + 1;
            at = FirstAbove(at);
        }
        return -1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr h, uint cmd);

    /// <summary>往上一扇。<b>方向是量出来的</b>：<c>GW_HWNDPREV</c> 是「上一扇 = 上面」。
    /// 记错的常量在 <c>docs/research/win32-constants-and-structs.md</c>。</summary>
    private static IntPtr FirstAbove(IntPtr h) => GetWindow(h, 3 /* GW_HWNDPREV */);

    private static IntPtr TopMost(IntPtr h) => GetTopWindow(IntPtr.Zero);

    // ---- 那一发 -------------------------------------------------------------

    internal static int Run(string[] args)
    {
        var seconds = ReadSeconds(args);
        var calendarInfo = Find.CalendarWindow();
        if (calendarInfo is null)
        {
            Console.WriteLine("读不到日历那一扇（按「窗口类 SysHeader32 + 宿主 SHELLDLL_DefView " +
                              "+ 带 WS_EX_LAYERED + 矩形落在屏内」找；那一扇是壳自己的窗，属主不是产品进程）。产品开着吗？");
            return Find.Refused;
        }

        var calendar = calendarInfo.Handle;
        var progman = Progman();
        var defView = DefView(progman);
        var iconLayer = IconLayer();

        Console.WriteLine("=== 挂之前 ===");
        Console.WriteLine($"日历 0x{calendar.ToInt64():X}  Progman 0x{progman.ToInt64():X}  " +
                          $"DefView 0x{defView.ToInt64():X}  图标层 0x{iconLayer.ToInt64():X}/{Windows.ClassOf(iconLayer)}");
        Console.WriteLine($"图标数 = {Windows.IconCountPlain(iconLayer)}");
        Console.WriteLine(ReadOnce(calendar, iconLayer, "挂前"));

        // 挂上去：爹换成 Progman，样式补 WS_CHILD 摘 WS_POPUP，z 序排在 DefView 之下。
        // 排 DefView 之下 = 在图标层之下 —— DefView 是透明的容器，日历画在它底下、图标在上面。
        // 坐标要换一次：成了子窗之后 SetWindowPos 的 x/y 是父窗客户区坐标。
        var beforeStyle = StyleOf(calendar);
        var rect = Windows.RectOf(calendar);
        SetParent(calendar, progman);
        SetWindowLongPtr(calendar, GwlStyle,
            (IntPtr)((beforeStyle | WsChild) & ~WsPopup));
        var client = new System.Drawing.Point { X = rect.Left, Y = rect.Top };
        var ok = ScreenToClient(progman, ref client);
        SetWindowPos(calendar, defView, client.X, client.Y, rect.Width, rect.Height,
            SwpFrameChanged | SwpNoActivate);

        Console.WriteLine();
        Console.WriteLine($"=== 挂上去（屏幕坐标→客户区 ({rect.Left},{rect.Top})→({client.X},{client.Y}) 换成功={ok}） ===");
        Console.WriteLine(ReadOnce(calendar, iconLayer, "挂后"));

        Console.WriteLine();
        Console.WriteLine($"=== 连读 {seconds} 秒，看产品每秒那一下会怎么动它 ===");
        for (var i = 1; i <= seconds; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"  第 {i} 秒  " + ReadOnce(calendar, iconLayer, "t" + i));
        }

        Console.WriteLine();
        Console.WriteLine("=== 抬回顶层 ===");
        SetParent(calendar, IntPtr.Zero);
        SetWindowLongPtr(calendar, GwlStyle, (IntPtr)beforeStyle);
        SetWindowPos(calendar, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            SwpFrameChanged | SwpNoZorder | SwpNoActivate);
        Console.WriteLine(ReadOnce(calendar, iconLayer, "抬后"));
        Console.WriteLine();
        // 照现状写（2026-10-15 那一族退场之后）：产品不再挪这一扇。`KeepAtBottom`/`IsAtBottom`
        // 那一族连同「每秒把自己插回桌面 z 序」一起退场了，现在摆着的时候引擎那一扇是
        // 藏着的（`Calendar.Park` 那一发 `SW_HIDE`），它是离屏渲染的载体。
        Console.WriteLine(
            "产品现在不再挪这一扇：摆着的时候它是藏着的（Park 那一发 SW_HIDE），它是离屏渲染的载体。"
            + "人看见的是图标层底下借来的那一扇（壳自己的 SysHeader32），像素由 NativeLayer.Push 推进去 —— "
            + "那一族有两发：宿主变了重挂、到点补推。托盘里「桌面日历」能重摆，它重摆的是那扇原生子窗"
            + "（重新挂进图标层、排 SysListView32 之下），不是引擎这一扇。");
        return 0;
    }

    private static int ReadSeconds(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var n)) return Math.Clamp(n, 1, 30);
        return 6;
    }
}
