using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DebugProbe;

/// <summary>
/// 工单 06 的第一发：一把尺子要先证明它量得对，再拿去量别人。
///
/// <para>这一发是<b>闸门</b>。做法是当场造一扇窗压在被测矩形上，然后问同一把尺子一句。
/// 答不出来的那一句和答对了的那一句，只差「尺子抓不抓得住」这一件。</para>
///
/// <para>为什么这一发非做不可：这一族栽过的几次，都是<b>尺子坏了、机制没坏</b>。
/// 一个不会拒收的尺子比没有尺子更坏 —— 它给出一个像样子的假答案，
/// 而那个假答案会被当成结论用好几轮。</para>
///
/// <para>跑两遍，中间只动一件事：把那扇窗挪开。<b>同一发里同一个尺子</b>，
/// 不换进程不换参数 —— 换了就分不清是尺子变了还是屏幕变了。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/06-prove-the-ruler-itself.md</c>。</para>
/// </summary>
internal static class Refuse
{
    internal static int Run(string[] args)
    {
        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "先验尺子自己（拒收那一发）",
            "当场造一扇窗压在被测矩形上跑一次 —— 必须报出那扇的类名并中止；" +
            "把它挪开之后同一条命令要正常答完。抓不住的话本尺子不合格，不许拿去量别的");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish(
                "答不出来 —— 找不到日历那一扇，没有被测对象就造不出「压在上面」这一场。" +
                Find.Measure().Text +
                "这一发不算通过，也不算不通过",
                "无");
            return Find.Refused;
        }

        RectI rect = calendar.Rect;

        // ---- 第一遍：压着的那一扇是当场造的 -------------------------------------
        using var cover = new CoverWindow(rect);
        if (!cover.Show())
        {
            shot.Finish(
                "答不出来 —— 当场那一扇没造起来（类名 " + CoverWindow.ClassName +
                "），压不上去就没有「尺子抓不抓得住」这一问",
                "无");
            return Find.Refused;
        }

        // 造的那一扇先自己认一遍：命中测试真的答它了，才轮到问尺子。
        // 不先认这一遍，「拒收」可能是因为别的什么窗压在上面，答错了对象。
        List<Find.PointOwner> underCover = Find.ProbeOwners(calendar);
        int ours = underCover.Count(o => o.Tier == WindowTier.Owner.AppWindow &&
                                         o.ClassName == CoverWindow.ClassName);

        List<Find.PointOwner> grabBefore = Find.ProbeOwners(calendar);
        Frame frame = Screen.Grab(rect);
        List<Find.PointOwner> grabAfter = Find.ProbeOwners(calendar);
        frame.OwnerText = FrameShot.OwnersText(grabBefore, grabAfter);
        string path = Path.Combine(outDir, "refused-frame.png");
        frame.SavePng(path);

        Find.MeasureResult refused = Find.Measure();

        if (ours == 0)
        {
            shot.Finish(
                $"答不出来 —— 压上去的那一扇（{CoverWindow.ClassName}）命中测试一格也没答到，" +
                $"所以尺子答「{Trim(refused.Text)}」不是被它逼出来的。本尺子不合格，不许拿去量别的",
                path);
            return Find.Refused;
        }

        if (refused.Ok || !refused.Text.Contains(CoverWindow.ClassName, StringComparison.Ordinal))
        {
            shot.Finish(
                $"**本尺子不合格，不许拿去量别的** —— 压着 {CoverWindow.ClassName} 的时候它答" +
                $"「{(refused.Ok ? "正常答完" : Trim(refused.Text))}」，没有把那一扇拒收掉。" +
                $"压着的窗是当场造的，类名 {CoverWindow.ClassName}",
                path);
            return Find.Refused;
        }

        // ---- 第二遍：把那扇窗挪开，同一把尺子 -------------------------------
        cover.Dispose();
        Find.MeasureResult clear = Find.Measure();

        if (!clear.Ok)
        {
            shot.Finish(
                $"不合格 —— 挪开之后同一把尺子还是答「{Trim(clear.Text)}」。" +
                "挪开那一步之后桌面本来应该是干净的",
                path);
            return Find.Refused;
        }

        shot.Finish(
            $"尺子合格 —— 压着的时候：命中测试 {underCover.Count} 格里有 {ours} 格归 " +
            $"{CoverWindow.ClassName}（pid {cover.ProcessId}），尺子拒收并报出那个类名，「{Trim(refused.Text)}」；" +
            $"挪开之后同一把尺子正常答完：「{Trim(clear.Text)}」。" +
            "压着的那一帧也留了档",
            path);

        return 0;
    }

    /// <summary>那一发拒绝的理由要短 —— 四行格式里读的是结论，不是整段叙述。
    /// 砍掉的是「矩形外面还有哪些窗」那一串，它和这一问的答案无关。</summary>
    private static string Trim(string text)
    {
        int cut = text.IndexOf("另有", StringComparison.Ordinal);
        return cut > 0 ? text[..cut].TrimEnd('；') + "…" : text;
    }
}

/// <summary>
/// 量具自己造的一扇窗。<b>它只活在这一发里</b>，跑完就交回去。
///
/// <para>类名用自己注册的一个，不借用系统那一族（<c>STATIC</c> 之类）——
/// 借用的话报出来的类名在屏幕上别处也可能出现，
/// 「尺子认出的是我造的那一扇」这句话就没有凭据了。</para>
///
/// <para>它是 <c>WS_POPUP</c> 加 <c>HWND_TOPMOST</c>：日历那一扇在图标层底下，
/// 不置顶的话压根压不住它。</para>
/// </summary>
internal sealed class CoverWindow : IDisposable
{
    internal const string ClassName = "DebugProbeCover";
    private const string WindowTitle = "量具自己造的遮挡窗";

    private const uint WsPopup = 0x8000_0000;
    private const uint WsVisible = 0x1000_0000;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShow = 5;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    private readonly RectI _rect;
    private readonly bool _topmost;
    private readonly WindowProc _proc;
    private IntPtr _hwnd;
    private ushort _atom;

    internal CoverWindow(RectI rect, bool topmost = true)
    {
        _rect = rect;
        _topmost = topmost;
        // 委托必须留着一个字段：被 GC 收走之后窗口过程就没人调了。
        _proc = OnWindowProc;
    }

    internal int ProcessId { get; private set; }

    /// <summary>这一扇的句柄。工单 08 要读它在 z 序里排第几。</summary>
    internal IntPtr Handle => _hwnd;

    /// <summary>造起来、摆到被测矩形上、把消息抽干。返回它到底有没有出现在屏幕上。</summary>
    internal bool Show()
    {
        var info = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = ClassName,
            // **要一个看得见的底色。** 背景刷给成空的话，这一扇画出来是透明的 ——
            // 留档那一帧就会和没压的时候一模一样，而那一帧正是拿来证明
            // 「尺子抓得住」的证据。证据自己看不见自己，等于没有证据。
            Background = GetStockObject(1 /* LTGRAY_BRUSH */),
        };

        _atom = RegisterClassEx(ref info);
        if (_atom == 0) return false;

        _hwnd = CreateWindowEx(
            _topmost ? 0x0008 /* WS_EX_TOPMOST */ : 0, ClassName, WindowTitle, WsPopup | WsVisible,
            _rect.Left, _rect.Top, _rect.Width, _rect.Height,
            IntPtr.Zero, IntPtr.Zero, info.Instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return false;

        ProcessId = Windows.ProcessIdOf(_hwnd);
        SetWindowPos(_hwnd, _topmost ? HwndTopMost : HwndNoTopMost,
            _rect.Left, _rect.Top, _rect.Width, _rect.Height, SwpShowWindow);
        RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, 0x0001 /* RDW_INVALIDATE */ | 0x0004 /* RDW_UPDATENOW */ | 0x0100 /* RDW_ALLCHILDREN */);
        Pump(400);
        return Windows.IsWindowVisible(_hwnd);
    }

    /// <summary>抽一会儿消息。<b>不抽，窗口不会真的出现</b> —— 造出来只是登记，
    /// 要消息循环走一遍才落到屏幕上。
    ///
    /// <para><b>必须用 <c>PeekMessage</c>，不能用 <c>GetMessage</c></b>：
    /// <c>GetMessage</c> 没有消息时是<b>阻塞</b>的，而这一发整段是靠外面那个时限兜着的 ——
    /// 阻塞的那一下让外层的时限永远没机会检查，整个进程就挂在那儿了（量于 10-12，第一发）。</para></summary>
    internal static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessage(out NativeMessage message, IntPtr.Zero, 0, 0, 0x0001 /* PM_REMOVE */))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            System.Threading.Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            Pump(200);
        }

        if (_atom != 0)
        {
            UnregisterClass(ClassName, GetModuleHandle(null));
            _atom = 0;
        }

        GC.KeepAlive(_proc);
    }

    private static IntPtr OnWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProc;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal string? MenuName;
        internal string ClassName;
        internal IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Window;
        internal uint Value;
        internal IntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal Point At;
    }

    /// <summary><c>POINT</c>。带在这里是因为 <c>MSG</c> 末尾那一个指针大小的那一对要占位，
    /// 少摆一个字段后面的东西全错位。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
