using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>鼠标桥：把系统已经收到的那一发按下、松开，喂给日历那一扇窗。<b>默认喂</b>。
///
/// <b>2026-10-12 翻了默认</b>，理由是日历那一扇换了性质：它现在带 <c>WS_EX_TRANSPARENT</c>
/// （<see cref="DesktopLayer.Park"/>），鼠标<b>穿过去</b>落到图标层，于是 OS 再也不会把鼠标消息
/// 投进这一扇。桥成了格子唯一的入口 —— 桥关着，这一扇就一个字也点不动。
///
/// <para>翻过来的原因不是当年那条错了，是那条量的是<b>另一扇窗</b>。2026-09-26 量到的是
/// 「挂进 <c>SHELLDLL_DefView</c> 那一版不带 <c>WS_EX_TRANSPARENT</c>，OS 自己会投」，
/// 两路一起喂的后果是「一次点击算两次」。今天这一版 OS 不投了，只剩桥这一路。
/// 实测结果在 <c>docs/research/desktop-layer-and-window-measurements.md</c>，
/// 「鼠标桥」这个词在 <c>GLOSSARY.md</c>。桥不新建钩子，是在 <see cref="Activity"/> 上那条
/// 已有的全局回调里加一个出口。</para>
///
/// <para>喂进去的那一型是 <b>触屏</b>：<c>InputEventScreenTouch</c>，不是 <c>InputEventMouseButton</c>。
/// 这不是审美，是 2026-09-25 那一发 <c>--push</c> 读出来的 —— 鼠标那一型喂进去，控件的
/// <c>gui_input</c> 收到了、自己的反应没跑（<c>pressed=0</c>）；触屏那一型 <c>pressed=1</c>。
/// 引擎的 <c>push_input</c> 不做 <c>emulate_touch_from_mouse</c> 那道重映射，所以那一型得自己造。</para>
///
/// <para><b>闸门是三问</b>（<see cref="ShouldFeed"/>）。原来那一问
/// 「<c>WindowFromPoint</c> 答的是不是我们」在 <c>WS_EX_TRANSPARENT</c> 下<b>恒为假</b> ——
/// 鼠标穿过去了，那一点命中的是图标层。于是换成：摆着吗、那一点在我们这块矩形里吗、
/// 那一点上有没有图标。第三问是「图标能随意拖放」那条要求的关键：
/// 鼠标穿透之后图标层本来就收得到点击，桥再喂一发就是同一次点击两处响应。</para>
///
/// <para><b>第三问在钩子那一刻答，不在抽干那一刻答</b>（工单 27 第二轮拆点 6，
/// <see cref="OnMouseHook"/>）。一格点击是「按下」「松开」两发，中间隔着几十毫秒、七八帧，
/// 而这一份状态在那一段时间里正相反着变：按下被抽干时左键还按着，松开被抽干时左键已经松开。
/// 于是「按住那一段闸门闭着」把<b>按下丢掉、把松开喂进去</b>，控件收到一发没有配对的松开
/// （<c>Button</c> 什么都不做），格子一个也点不动 —— 2026-10-14 真机读数：
/// 点 22 那一格前后日历那块一个像素都没变。问一次和答一次必须是同一时刻的状态。</para>
///
/// <para><b>按下和松开是一对</b>（<see cref="Plan"/>）：没喂出去的按下，它后面那一发松开也不喂；
/// 喂出去的松开喂在<b>按下的那一格</b>，不是松手那一格。</para>
///
/// <para>钩子回调里一行日志都不写、一次 <c>PushInput</c> 都不做：低级钩子挂在全机输入的同步路径上，
/// 而 <c>PushInput</c> 会当场走一遍控件树。所以回调只排队，喂的动作在 <see cref="Plan"/> 里做，
/// 由日历自己的 <c>_Process</c> 调。排队那一发要答的那一问是<b>纯矩形比较</b>、几微秒，
/// 不碰 COM、不发消息，所以钩子那条路径上放得下。</para></summary>
internal sealed class MouseBridge
{
    private readonly Window _target;
    private readonly Func<bool> _parked;
    private readonly Action<string> _log;
    private readonly Queue<Queued> _queued = new();

    /// 有一发按下已经喂出去了，正等它那一发松开。
    ///
    /// <para><b>它是字段不是局部变量</b>：按下和松开之间隔着几十毫秒、七八帧，而
    /// <see cref="Drain"/> 每帧调一次 —— 放局部的话按下那一帧一结束就忘了，
    /// 下一帧的松开会答「前面没有配对的按下」。</para>
    private bool _pairOpen;
    private Win32.Point _pairDownAt;

    /// 喂进去几发、因为不归我们而丢掉几发。这两个数是这一轮真正要的第一手实测结果：
    /// 钩子侧「系统收到了」和窗侧「控件收到了」中间就差这一发。
    public int Fed { get; private set; }
    public int Dropped { get; private set; }

    /// <summary>桥喂进去过几次<b>物理</b>点击（一次按下加一次松开算一次）。
    ///
    /// 日历判双击要拿这个数，不能拿 <c>ButtonUp</c> 的次数：2026-09-26 那一跑读出来，
    /// 喂进去一发（按下+松开两件事），格子收到 3 个事件、<c>ButtonUp</c> 发了两次 ——
    /// 引擎把触屏事件又重映射出一份鼠标事件（<c>emulate_mouse_from_touch</c> 默认开着）。
    /// 一次物理点击两次松开，按松开次数判双击就是「一点就抬」。</summary>
    public long FedClicks { get; private set; }

    /// 最近一次判据那行实测结果，日志里对着控件计数看。
    public string LastDecision { get; private set; } = "还没喂过一发";

    public MouseBridge(Window target, Func<bool> parked, Action<string>? log = null)
    {
        _target = target;
        _parked = parked;
        _log = log ?? (_ => { });
    }

    /// <summary>钩子那侧收下的一发。<b>闸门第三问（那一点上有没有图标）在钩子那一刻就答好了</b>，
    /// 答案跟着这一发排队，抽干那一刻不再问第二遍（工单 27 第二轮拆点 6）。</summary>
    internal readonly record struct Queued(bool Down, Win32.Point At, long AtMs, bool OnIcon);

    /// <summary>闸门本身：三问全过才喂。
    ///
    /// <list type="number">
    /// <item><paramref name="parked"/> —— 摆着才喂。抬起打字那一段桥整条闭嘴：
    /// 那时这一扇摘掉了 <c>WS_EX_TRANSPARENT</c>，OS 自己会投，桥再喂一发就是一次点击两下。</item>
    /// <item><paramref name="overCells"/> —— 那一点在我们这块矩形里。</item>
    /// <item><paramref name="onIcon"/> —— 那一点上<b>没有</b>桌面图标。有就归 shell：
    /// 选中、拖放、双击打开都是它的，我们让开。<b>「读不可信」也走这一档</b>
    /// （<b>从没有读到过好的一份</b>，见 <see cref="DesktopIcons.OnIcon"/>），
    /// 也就是「宁可不吃这一次点击，也不抢双击图标去打开它」。
    /// 这一问答的是<b>上一次读到的那一份快照</b>，它跟左键按没按着无关。</item>
    /// </list>
    ///
    /// <b>三个入参都是纯算术或一次消息</b>，所以自检能拿假的数把几档全拧一遍，不碰任何一扇窗。
    public static bool ShouldFeed(bool parked, bool overCells, bool onIcon) =>
        parked && overCells && !onIcon;

    /// 钩子那侧调进来。**这里不再筛「是不是左键」** —— 那是 <see cref="Activity.Judge"/>
    /// 的 <c>ToBridge</c> 说的，能递到这儿来的就已经只有左键按下和松开了；原来这里还有一道同样的
    /// 判断，同一条规矩写在两处。日历上只有「点一下选一天、点两下改备注」，移动不喂
    /// （触屏那一型本来也不产生 <c>mouse_entered</c>，喂了是假装有悬停）。
    ///
    /// <para><b>第三问在这一刻答</b>（工单 27 第二轮拆点 6）：它是纯矩形比较、几微秒，
    /// 不碰 COM 也不发消息，钩子那条同步路径上放得下。答完跟着这一发排队。</para>
    public void OnMouseHook(Activity.InputEvent e, long atMs)
    {
        var onIcon = DesktopLayer.OnIcon(e.At);
        if (_queued.Count >= 64) _queued.Dequeue();   // 一帧都抽不出去的话，别把队列撑爆
        _queued.Enqueue(new Queued(e.Kind == Activity.InputKind.LeftDown, e.At, atMs, onIcon));
    }

    /// <summary>判据那一族：造几发排进来，不接钩子。<b>只有 <c>--check</c> 的判据在用</b>
    /// —— 产品那一路只有 <see cref="OnMouseHook"/> 一个出处，它自己答第三问。</summary>
    internal void EnqueueForTest(bool down, Win32.Point at, bool onIcon) =>
        _queued.Enqueue(new Queued(down, at, 0, onIcon));

    /// <summary>屏幕物理点 → 窗内坐标。<c>PushInput</c> 要的是<b>窗内</b>坐标
    /// （<c>localCoords: true</c>，和 <c>--push</c> 里量成的那一发同一口径）。
    ///
    /// 原点取 Win32 那块矩形的左上角。两版都实测过：挂进壁纸那版位置是
    /// <c>MoveWindow</c> 按爹的坐标挪的，引擎那侧不知道；今天这一版引擎和 Win32 说的是同一个
    /// 屏幕坐标，但仍然用 Win32 这一份 —— 判据不该有两把尺子。</summary>
    public static Vector2I ToLocal(Win32.Point at, Win32.Rect window) =>
        new(at.X - window.Left, at.Y - window.Top);

    /// <summary>把排着的每一发判一遍、该喂的喂进去。返回这一抽喂了几发。
    ///
    /// 决策本体在 <see cref="Plan"/>：这一发只管拿矩形、把这个月的几样读数递进去
    /// （真窗、引擎位置、真「那一点归谁」），不写判据。</summary>
    public int Drain(nint hwnd, Action<bool, Win32.Point, WindowTier.Owner>? every = null)
    {
        if (_queued.Count == 0) return 0;
        if (hwnd == IntPtr.Zero) { _log("桥：没有 HWND，排着的 " + _queued.Count + " 发一发都不喂"); return 0; }
        Win32.GetWindowRect(hwnd, out var rect);
        return Plan(
            rect,
            _parked,
            at => DesktopLayer.OwnerOf(hwnd, at),
            () => $"引擎窗位=({_target.Position.X},{_target.Position.Y})",
            every,
            (pressed, at) => _target.PushInput(
                new InputEventScreenTouch { Position = at, Pressed = pressed }, true));
    }

    /// <summary>抽干那一段的决策本体。<b>纯算术</b>（不碰窗、不发消息、不碰 COM），
    /// 真窗那一侧的出口全从参数递进来，所以判据能造几份假队列、假窗、假出口喂进去
    /// （工单 27 第二轮拆点 8）。
    ///
    /// <list type="number">
    /// <item>闸门第三问是<b>排队那一发带来的</b>（<see cref="OnMouseHook"/> 在钩子那一刻答的），
    /// 这里不重问第二遍 —— 问一次和答一次必须是同一时刻的状态。</item>
    /// <item><b>按下和松开配成对</b>：没喂出去的按下，它后面那一发松开也不喂；
    /// 喂出去的松开喂在<b>按下的那一格</b>，不是松手那一格。</item>
    /// </list>
    ///
    /// <paramref name="every"/> 每一发都会被告知，**包括没喂出去的那些** —— 抬起打字那一段
    /// 桥整条闭着，日历要靠这一发判断「人点到别处去了，该把窗放回去了」。
    ///
    /// 两个原点差多少是一行实测结果，所以照旧印出来。</summary>
    internal int Plan(
        Win32.Rect rect,
        Func<bool> parked,
        Func<Win32.Point, (WindowTier.Owner Owner, string Measurement)> ownerOf,
        Func<string> engineAt,
        Action<bool, Win32.Point, WindowTier.Owner>? every,
        Action<bool, Vector2I> feed)
    {
        if (_queued.Count == 0) return 0;
        var fed = 0;
        var parkedNow = parked();
        while (_queued.Count > 0)
        {
            var q = _queued.Dequeue();
            var overCells = DesktopLayer.OverCells(rect, q.At);
            var (owner, measurement) = ownerOf(q.At);
            every?.Invoke(q.Down, q.At, owner);

            if (q.Down)
            {
                // 第三问是**排队这一发带来的**（钩子那一刻答的），这里不重问第二遍。
                if (!ShouldFeed(parkedNow, overCells, q.OnIcon))
                {
                    Dropped++;
                    _pairOpen = false;      // 没喂出去的按下不算一对：它后面那一发松开也不喂
                    LastDecision = "丢掉 按下：" + Why(parkedNow, overCells, q.OnIcon) +
                                   $"：命中={DesktopLayer.Name(owner)}（{measurement}）";
                    continue;
                }

                _pairOpen = true;
                _pairDownAt = q.At;
                var downAt = ToLocal(q.At, rect);
                feed(true, downAt);
                Fed++;
                fed++;
                LastDecision = $"喂 按下 {downAt} ← ({q.At.X},{q.At.Y})，{engineAt()} " +
                               $"Win32 原点=({rect.Left},{rect.Top})";
                continue;
            }

            // 松开**必须配它前面那一发按下**。没有配对的按下就不喂：喂一发光脚事件，
            // 控件收到的是「松开而没有按下」，`Button` 什么都不做。
            if (!_pairOpen)
            {
                Dropped++;
                LastDecision = $"丢掉 松开：前面那一发按下没有喂出去，配不上对 —— " +
                               $"({q.At.X},{q.At.Y})，命中={DesktopLayer.Name(owner)}（{measurement}）";
                continue;
            }

            // 反向验 2 恢复：喂在**按下的那一格**，不是松手那一格：横着拖过日历时，
            // 控件收到的是「在按下的那一格上按下、在同一格上松开」，按钮不会卡在按下状态。
            var upAt = ToLocal(_pairDownAt, rect);
            _pairOpen = false;
            feed(false, upAt);
            Fed++;
            FedClicks++;   // 一次物理点击数以松开那一下为准
            fed++;
            LastDecision = $"喂 松开 {upAt} ← 按下的那一点 ({_pairDownAt.X},{_pairDownAt.Y})，" +
                           $"松手在 ({q.At.X},{q.At.Y})，{engineAt()}";
        }

        return fed;
    }

    /// 闸门三问里前两问答假的时，第三问那一档读给人听的话。
    private static string Why(bool parked, bool overCells, bool onIcon) =>
        !parked ? "这一扇不在摆着（抬起打字那一段，归 OS 投）"
        : !overCells ? "那一点不在日历这块矩形里"
        : "那一点上有一个桌面图标（归 shell：选中、拖放、双击打开）";

    /// 换状态（摆下 / 抬起）的时候清一次队列：跨界那一刻排着的旧事件，
    /// 拿新状态的判据去喂会喂错地方。<b>那一发没配上对的松开也一并作废</b>
    /// —— 新状态下的第一发松并没有对应的按下。
    public void Clear()
    {
        _queued.Clear();
        _pairOpen = false;
    }

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>鼠标桥闸门那一族的判据（工单 27 第二轮拆点 8）。<b>纯算术</b>：
    /// 造假队列、假窗、假出口喂进去，问的是「按下落掉了松开还喂不喂」
    /// 「松开喂在哪一点」「闸门答的是不是钩子那一刻那一问」。不碰桌面、不发消息、不碰 COM。
    public static List<string> BridgeSelfTest()
    {
        var checks = new Checks();
        var rect = new Win32.Rect { Left = 1000, Top = 0, Right = 2000, Bottom = 1000 };
        var a = new Win32.Point { X = 1200, Y = 400 };   // 日历里的一格
        var b = new Win32.Point { X = 1600, Y = 400 };   // 同一块矩形里的另一格

        // 跑一遍：造一个空的桥（不接真窗），排几发，抽干一次，返回喂到哪儿去了。
        // 「引擎窗位」那一栏判据这一发没有窗，给一句占位的。
        (List<(bool Pressed, Vector2I At)> fed, MouseBridge bridge) Run(
            params (bool Down, Win32.Point At, bool OnIcon)[] events)
        {
            var bridge = new MouseBridge(null!, () => true);
            foreach (var (down, at, onIcon) in events) bridge.EnqueueForTest(down, at, onIcon);
            var fed = new List<(bool, Vector2I)>();
            bridge.Plan(rect, () => true, _ => (WindowTier.Owner.Ours, "判据那一发"), () => "没有窗", null,
                (pressed, at) => fed.Add((pressed, at)));
            return (fed, bridge);
        }

        // 对照（一定该通）：点在一格里、闸门三问全过 —— 两发都喂、都在那一格。
        // 没有这一行的话，下面三条一个「闸门永远闭着」的测量程序也能过。
        var ok = Run((true, a, false), (false, a, false));
        if (ok.fed.Count != 2 || ok.fed[0] != (true, MouseBridge.ToLocal(a, rect)) ||
            ok.fed[1] != (false, MouseBridge.ToLocal(a, rect)))
            checks.Fail($"点在一格里、那一点上没图标，两发却没原样喂出去：" +
                        $"{Describe(ok.fed, rect)}（闸门关着的话日历格子一个也点不动）");

        // ---- 八之一：按下落掉时，紧跟着的松开也不许喂 ------------------------------
        checks.Check("配对", () =>
        {
            var lost = Run((true, a, true), (false, a, false));
            if (lost.fed.Count != 0)
                checks.Fail($"按下落掉了（那一点上有图标），紧跟着的松开还喂了 {lost.fed.Count} 发：" +
                            $"{Describe(lost.fed, rect)} —— 控件收到一发没有配对的松开，Button 什么都不做");
            if (lost.bridge.Dropped != 2)
                checks.Fail($"按下落掉、松开也丢掉，这两发该记成丢掉 2，实录 {lost.bridge.Dropped}");
            if (!lost.bridge.LastDecision.Contains("配不上对"))
                checks.Fail("丢掉的松开那一行没写清「前面那一发按下没有喂出去」，读出来是" +
                            $"「{lost.bridge.LastDecision}」");
        });

        // ---- 八之二：按下喂了、松开在别处时，松开喂在按下的那一格 --------------------
        checks.Check("松开那一格", () =>
        {
            var across = Run((true, a, false), (false, b, false));
            var want = MouseBridge.ToLocal(a, rect);
            if (across.fed.Count != 2)
                checks.Fail($"横着拖过日历时应该喂两发，实喂 {across.fed.Count} 发：" +
                            $"{Describe(across.fed, rect)}");
            else if (across.fed[1] != (false, want))
                checks.Fail($"松开喂在了 {MouseBridge.ToLocal(b, rect)}（松手那一格），" +
                            $"该喂在按下的那一格 {want} —— 控件收到的是「在甲格按下、在乙格松开」，" +
                            "按钮会卡在按下状态");
            if (across.bridge.FedClicks != 1)
                checks.Fail($"一次物理点击该记成 1 次，实录 {across.bridge.FedClicks}");

            // 反向那一半（必须当次就红）：喂在松手那一格 —— 就是这一版的错法。
            var naive = MouseBridge.ToLocal(b, rect);
            if (naive == want)
                checks.Fail("这条判据量错了东西：松手那一格和按下的那一格落在同一个窗内坐标上");
        });

        // ---- 八之三：闸门答的是钩子那一刻那一问，不是抽干那一刻 ----------------------
        checks.Check("闸门那一刻", () =>
        {
            // 判法：**同一个点、同样的两发，只有排队那一发的第三问答案不同**，
            // 两次跑出来的发数必须不一样。
            //
            // 为什么不拿「静态那一份快照此刻是什么状态」当前置条件 —— 无头自检里
            // **它确实会被读到**（2026-10-14 实测：判据跑之前 `DesktopIcons.Current`
            // 已经答出图标了），而读到没读到决定重问那一版答什么，两种情形一好一坏。
            // 这一条不问静态状态，所以两种情形下都判得死。
            var onIconSays = Run((true, a, true), (false, a, false));      // 排队答「那一点上有图标」
            var offIconSays = Run((true, a, false), (false, a, false));    // 排队答「那一点上没有图标」
            if (onIconSays.fed.Count == offIconSays.fed.Count)
                checks.Fail($"同一个点上、排队那一发答「有图标」和答「没有图标」，两次跑出来喂的" +
                            $"发数却一样（各 {onIconSays.fed.Count} 发）—— 闸门答的不是钩子那一刻的答案，" +
                            "而是在抽干那一刻重新问了一遍（按下被丢掉、松开被喂进去，格子一个也点不动）");
            // 上面那一条量的真是这一问的两档：答「有图标」的一发都不喂、答「没有图标」的两发都喂。
            if (onIconSays.fed.Count != 0 || offIconSays.fed.Count != 2)
                checks.Fail($"闸门那两档答错了：排队答「有图标」喂了 {onIconSays.fed.Count} 发" +
                            $"（该 0 发），排队答「没有图标」喂了 {offIconSays.fed.Count} 发（该 2 发）" +
                            $"：{Describe(offIconSays.fed, rect)}");
        });

        return checks.Report();
    }

    /// 判据报错那句话：喂出去几发、每一发喂在哪一点、按没按下。
    private static string Describe(List<(bool Pressed, Vector2I At)> fed, Win32.Rect rect) =>
        fed.Count == 0 ? "一发都没喂"
        : string.Join("、", fed.Select(e => $"{(e.Pressed ? "按下" : "松开")} {e.At}"));

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 27 第二轮拆点 8）。</summary>
    internal static readonly CriterionOf Bridge = new(
        () => "鼠标桥配对通过：点在一格里两发原样喂出去；按下落掉时紧跟着的松开也不喂；" +
              "横着拖过时松开喂在按下的那一格、一次物理点击记成 1 次；" +
              "闸门答的是钩子那一刻那一问（按下答有图标、同一格里松开答没有，两发都丢掉）",
        "鼠标桥配对", BridgeSelfTest);
}
