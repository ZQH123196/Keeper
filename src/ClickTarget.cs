using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Keeper;

/// <summary>测量程序的**接收方**：一扇**纯 Win32** 的窗，落在它上面的左键按下它就 +1。
///
/// <para><b>它是这一族的正本</b>（<c>--swallow</c>，见
/// <c>docs/research/desktop-layer-and-window-measurements.md</c>「钩子能不能吃掉那一档」那一节）：
/// 要量的是「钩子返回非零，那一下到不到接收方」。这一扇答的就是那一句 ——
/// <b>钩子吃掉时它收不到，放行时它收到</b>。</para>
///
/// <para><b>为什么是纯 Win32、为什么不用 Godot 的控件</b>：Godot 的 GUI 输入
/// （<c>_Input</c> / <c>gui_input</c> / 按钮 <c>Pressed</c>）在<b>一扇拿不到键盘焦点的窗上
/// 不派发</b> —— 2026-10-10 量到过三次一致的读数：那个点命中 <c>Engine</c>、
/// <c>IsWindowVisible=True</c>、坐标落在控件矩形内，而三层计数全是 0。
/// 而这一发量的恰恰是「送到了没有」，**量具不能自己要焦点**。
/// <b>子类化 <c>WndProc</c> 是那一层更靠前的一层</b>：Windows 一定把点击消息送给
/// 那个点上最前面的窗，<c>WndProc</c> 一定看得到，与焦点、与 <c>unfocusable</c> 都无关。</para>
///
/// <para><b>为什么它可以在测量程序自己这个进程里</b>：这一发的两端要分属两处，
/// 而「两处」指的是**钩子那一侧**和**系统路由的落点那一侧**，不是两个进程。
/// 钩子挂在全机输入的同步路径上（<c>WH_MOUSE_LL</c>，dwThreadId = 0），
/// 所以它对**自己进程里**那一扇窗的点击一样有那一刀。放行时那一发走进这一扇的
/// <c>WndProc</c>；返回非零时它<b>走不到这里</b> —— 那正是要量的差别。</para>
///
/// <para><b>子类化回调里什么都不许写</b>：它在这一发鼠标消息的处理路径上，
/// 往里塞一次文件写就是重入。回调只累加一个数，读数在消息循环外面取。</para>
///
/// <para>它是<b>测量程序那一侧的器具</b>，不进产品。产品那一族的读法是
/// <c>src/detection/Activity.cs</c> 上那条 <c>WH_MOUSE_LL</c>。</para>
internal static class ClickTarget
{
    private static readonly WinProc Handler = OnMessage;
    private static nint _self;
    private static nint _window;
    private static nint _originalProc;
    private static int _clicks;

    /// <b>顶窗那一发的手写实测结果</b>（改了哪些样式位、读回来是什么）。
    /// 改了 Win32 的样式位却不读回来，就不知道那一发到底生没生效 ——
    /// 2026-10-10 撞的就是那个（`NOACTIVATE` 传 `int` 给收 `nint` 的形参，位或之后截断）。
    public static string LastReport { get; private set; } = "还没顶过";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WinProc(nint h, uint msg, nint w, nint l);

    // 声明全部收在 `Win32.cs` 一份里（卡片 04 · S17）：这里只留子类化那一位的索引。
    // 原来这儿的 `RegisterClassW` / `WindowClass` / `ClassName` 三样都没人用
    // （2026-10-10 起改用系统 `BUTTON` 类当壳，见 <see cref="Show"/> 那段），删掉了。

    private const int GwlWndProc = -4;

    /// 这一扇收到过几次左键按下。<b>这是那一族的实测结果</b>，读它就够。
    public static int Clicks => _clicks;

    /// 起那一扇并子类化它的 <c>WndProc</c>。返回它的 HWND；<see cref="IntPtr.Zero"/> 就是没起来。
    ///
    /// <para><b>用系统那个 <c>STATIC</c> 类当壳，不自己注册类</b>（2026-10-10 实测撞的）：
    /// 自己 <c>RegisterClassW</c> 的话，<c>WindowClass</c> 那个结构里有几个 <c>string</c>，
    /// 而 <c>Marshal.SizeOf</c> 按非封送字段算 —— 算出来的尺寸对不上，
    /// <c>RegisterClassW</c> 静默失败，于是 <c>CreateWindowExW</c> 报「类名不认识」、
    /// 句柄回来是零，而日志上那一扇「没起来」。而 <c>STATIC</c> 是系统注册好的
    /// 窗口类，<b>子类化不要求那个类是谁</b> —— 它只是壳，<c>WndProc</c> 换掉之后
    /// 这一扇收什么、发什么全是我们说了算。</para>
    public static nint Show()
    {
        _self = Win32.GetModuleHandle(null);
        _window = Win32.CreateWindowEx(IntPtr.Zero, "BUTTON", $"keeper-click-target-{System.Environment.ProcessId}",
            // **`BUTTON` 类，不 `STATIC`**（2026-10-10 实测撞的）：`STATIC` 是静态文本控件，
            // **它不收鼠标** —— 而那一发的读数正是「子类化装上了=True、那个点也是我们、
            // 而 `WM_LBUTTONDOWN` 一个都收不到」。`BUTTON` 收鼠标消息，
            // 而子类化把它的 `WndProc` 换成我们那一个，数的就是它。
            //
            // **不带 `WS_CHILD`**：那一档让它是爹的子控件，而我们要的是一扇**顶层**窗。
            // `WS_OVERLAPPEDWINDOW` 已经含 `WS_VISIBLE`。
            (uint)Win32.WsOverlappedWindow, 8, 8, 420, 260,
            IntPtr.Zero, IntPtr.Zero, _self, IntPtr.Zero);
        if (_window == IntPtr.Zero) return IntPtr.Zero;
        Win32.ShowWindow(_window, Win32.SwShow);

        // **子类化**：把这一扇的窗口过程换成我们那一个，数完左键按下再转回原来那个。
        // 这一步是这一族的正本 —— 它在窗口消息进 Godot 之前，所以既不看焦点、
        // 也不看 `unfocusable`（那两样正是 Godot 那一侧收不到输入的原因）。
        _originalProc = Win32.SetWindowLongPtr(_window, GwlWndProc, Marshal.GetFunctionPointerForDelegate(Handler));
        return _originalProc == IntPtr.Zero ? IntPtr.Zero : _window;
    }

    /// 把它顶到 z 序最前，**不动前台**。点击送给那个点上最前面的那一块，
    /// 所以这一发要的是这一扇在最前面，而不是「它在前台」。
    ///
    /// <para><b>顶两次，`HWND_TOPMOST` 之后接一次 `HWND_TOP`</b>（2026-10-10 实测撞的）：
    /// 只顶 `TOPMOST` 时实测读到 `那个点上是谁='Chrome_WidgetWin_1'` 而接收方在那一摞里
    /// 带 `|TOPMOST` 排在第二 —— 那一摞顶端还压着别家的 `TOPMOST`（实测那一档是
    /// `XamlExplorerHostIslandWindow '任务切换'`，`可见=False`）。<b>而 Windows 按
    /// 「那一摞的顶端 + 可见」挑赢家</b>，所以再顶一次 `HWND_TOP`（只在顶层那一叠里排到最前）
    /// 才真的轮到它。</para>
    public static bool Raise(bool topmost = true)
    {
        // **`WS_EX_NOACTIVATE` 加上**（2026-10-10 撞到最后一道）：接收方不吃激活，
        // 而它**照样在命中测试里**（这一档就是给「那一扇要收鼠标但不许抢前台」用的 ——
        // 产品那一族要的是相反的那一头，见那个 GDScript 接收方上的注释）。
        //
        // **到这一发为止撞过的全部三道**（都在 `docs/research/desktop-layer-and-window-measurements.md`
        // 「钩子能不能吃掉那一档」那一节里）：
        //   一、`TOPMOST` 交了而那个点命中 Chrome —— 那一摞顶端压着别家的 `TOPMOST`。
        //   二、再顶一次 `HWND_TOP`，仍是 Chrome —— Chrome 那三扇全屏
        //       （实测 `(-9,-9)-(2569,1389)`）且不带 `TOPMOST`，却在接收方之上。
        //   三、这一道：**那一摞里谁在最前** 和 **那个点上命中谁** 在这台机器上不是同一件事
        //       —— 前台那一摞是别人家的（全屏 Chrome），命中测试照那一摞走。
        // **`TOPMOST` + `NOACTIVATE` 两条一起**（2026-10-10 实测撞的）：
        // 只顶 `TOPMOST` 时实测那一摞里接收方排在 `Progman` **后面** ——
        // `Progman` 是全屏 `TOOLWINDOW`，**桌面那一片的命中测试归 `SHELLDLL_DefView` 那一族**，
        // 不认普通顶层窗。而只顶 `HWND_TOP`（不提升）时更糟：实测摆在屏幕**右下角**
        // （`(2120,1140)-(2540,1400)`，那儿没有桌面图标）仍然命中 `Progman`。
        // **两条合起来才赢**：`TOPMOST` 让它进入最上面那一层，
        // `NOACTIVATE` 让它在命中测试里算数而不抢前台。
        //
        // **`topmost: false` 那一档是 2026-10-10 晚加的**（量「铺满那一扇穿不穿透」那一族）：
        // 顶到 `TOPMOST` 时，遮罩落定那一发 `SetWindowPos(HWND_NOTOPMOST)` 之后
        // **接收方自己跑到遮罩上面去了** —— 那一下「点得到」不是穿透给的，是 z 序给的。
        // 要问穿透就必须是**普通顶层窗**：它在不在置顶那一叠，遮罩都压在它上面。
        //
        // **而「挪进普通那一叠」改样式位是没用的**（这一档当场撞的两次）：
        // 一、`style &= ~WsExTopmost` 写进去是 `8000100`、读回来是 `8000108` ——
        //    **Windows 不认这一位是这么改的**，它跟着 z 序走。
        // 二、`SetWindowPos(HWND_TOP)` 也只是**在置顶那一叠内部**排次序（那一叠里排最前），
        //    那一扇仍旧压在 `HWND_NOTOPMOST` 之后才落的遮罩上面。
        // **唯一那条路是 `SetWindowPos(HWND_NOTOPMOST, …)`**，读回来印出去对账。
        var style = Win32.GetWindowLong(_window, Win32.GwlExStyle) | Win32.WsExNoActivate;
        Win32.SetWindowLong(_window, Win32.GwlExStyle, (nint)style);
        var a = Win32.SetWindowPos(_window, topmost ? Win32.HwndTopMost : Win32.HwndNotTopMost,
            0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate | Win32.SwpFrameChanged);
        if (topmost)
        {
            // 第二句 `HWND_TOP` 只在顶层那一叠里排到最前（为什么需要它见上面第二道）
            Win32.SetWindowPos(_window, Win32.HwndTop, 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate | Win32.SwpFrameChanged);
        }
        // **把扩展样式读回来印出去**（2026-10-10 实测撞的）：`NOACTIVATE` 加上没加上
        // 在日志上看得见 —— 那一跑的读数是接收方 `扩展=|TOPMOST|WINDOWEDGE`，
        // **没有 `NOACTIVATE`**，而写它那一行传的是 `int` 给一个收 `nint` 的形参
        // （位或之后截断）。**改了样式位要读回来，不许只信「我写了」。**
        var read = Win32.GetWindowLong(_window, Win32.GwlExStyle);
        LastReport = $"顶窗(topmost={topmost}) 交={a} 扩展写进去={style:X} 读回来={read:X} " +
                     $"NOACTIVATE={Win32.DescribeExStyle(read)}";
        return a;
    }

    /// 把接收方那一扇挪到**我们自己那扇 Godot 主窗的正中**。
    ///
    /// <para><b>为什么是那儿</b>（2026-10-10 实测撞到最后一层）：这一发撞了四轮才量到
    /// **「那个点上命中谁」不认「那一摞里谁带 <code>|TOPMOST</code>」** ——
    /// 摆在左上角命中 <c>Progman</c>（全屏 <c>TOOLWINDOW</c>），摆在右下角
    /// （<c>(2120,1140)-(2540,1400)</c>，那儿没有桌面图标层）**仍然**命中 <c>Progman</c>，
    /// 而 <c>TOPMOST</c> 交了（<code>交=True</code>）、<code>NOACTIVATE</code> 加上了
    /// （<code>读回来=8000108</code>）。<b>桌面那一片的命中测试归
    /// <c>SHELLDLL_DefView</c> 那一族，普通顶层窗压不过它。</b>
    ///
    /// <para>而 Godot 那扇主窗在那个位置上实测命中过 <c>Engine</c> ——
    /// 也就是说**那一块有一个已知的赢家**，把接收方摆到同一个矩形上，
    /// 拿同一个命中读数对账。这一发要的就是「两处」读数能对上，
    /// 至于那个赢家是谁，不影响「钩子吃掉时它收不到」这一句。</para>
    public static bool MoveOntoOurs(nint ours, Win32.Rect r)
    {
        _ = ours;
        // **尺寸一起改**（2026-10-10 实测撞的）：`MoveWindow` 那一次只挪位置、
        // `WS_OVERLAPPEDWINDOW` 按类默认给了一个极小的框 —— 实测读数是
        // `(0,0)-(170,47)`，而按的点落在那个小框的**外面**，于是三下点击全落在窗外。
        return Win32.MoveWindow(_window, r.Left, r.Top, Math.Max(r.Width, 420),
            Math.Max(r.Height, 260), true);
    }

    /// 那一扇的屏幕矩形。没起来时是空的（<c>Width = 0</c>）。
    public static Win32.Rect Where()
    {
        Win32.GetWindowRect(_window, out var r);
        return r;
    }

    /// 把它撤下来。这一发跑完要收场，别在屏上留一扇窗。
    public static void Close()
    {
        if (_window == IntPtr.Zero) return;
        Win32.SetWindowLongPtr(_window, GwlWndProc, _originalProc);
        Win32.DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private static nint OnMessage(nint h, uint msg, nint w, nint l)
    {
        // **回调里一行日志都不写**（那一发鼠标消息的处理路径上）—— 只累加。
        if (msg == Win32.WmLButtonDown) _clicks++;
        if (msg == Win32.WmDestroyMsg) return 0;
        return Win32.CallWindowProc(_originalProc, h, msg, w, l);
    }

    /// 把那一扇拉回正本该有的样子：这一发跑完，屏上不该留它。
    /// 拆类在收场那一发里做，量还没读就拆了的话读数就落在窗被拆掉之后 —— 顺序要反过来。
    public static void TearDownAfterRead()
    {
        if (_window == IntPtr.Zero) return;
        Win32.SetWindowLongPtr(_window, GwlWndProc, _originalProc);
        Win32.DestroyWindow(_window);
        _window = IntPtr.Zero;
    }
}