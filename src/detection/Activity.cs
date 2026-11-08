using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Keeper;

/// <summary>「你一直在用这台机器」这件事的实测结果。三个数就够：距离上一次输入多久、
/// 连续用了多久、以及从某个时刻起连续输入了多久。
///
/// 两个常量的语义写死在这里：
/// 离开超过 <see cref="StreakGapMs"/>，连续使用归零（所以休息回来是干净的一段）；
/// 两次输入之间隔了一秒以上，这一段就不算「回来工作」了 —— 这两条隔开，
/// 才让「只来一下输入」永远凑不成挥散。</summary>
public sealed class Activity : Input
{
    /// <summary>一个输入间隔长到这个数，当前这段连续使用就结束了。
    ///
    /// <b>它现在只答一件事：「人是不是离开了」</b>（2026-10-21 所有者定夺）——
    /// 而且只在**挥散过之后**才问（<see cref="Scheduler.Step"/> 里那一行）。
    /// 倒计时和触发都改按现实时间走了，跟这个数再没有任何关联。
    ///
    /// <b>为什么还留着它</b>：契约那句「休息过、或者离开过键盘，这个计数复位」里的
    /// 「离开过键盘」就是它 —— 欠账（`Scheduler` 内部那一格取消计数）在离开够久的时候清零，
    /// 下一次回到休息延迟序列的第一格。</summary>
    public const long StreakGapMs = 60_000;

    /// 两次输入隔长到这个数，它们就不属于同一段了。留得短，是为了让只来一下输入
    /// 永远攒不成一次挥散。
    public const long BurstGapMs = 1_000;

    /// 鼠标事件是一串一串来的，攒到这个间隔再记一次，免得每次移动都写三个数。
    /// 和上面那两个常量一样是 public 的：自检里那一组的绿话要把它印出来，
    /// 而不是在字符串里再抄一个 16（那三个数手抄过期过两次）。
    public const long CoalesceMs = 16;

    private long _startedAtMs;
    private long _lastEventMs;
    private long _streakMs;
    private long _burstStartedMs;
    private long _lastFlushMs;
    private nint _keyboardHook;
    private nint _mouseHook;
    private Win32.HookProc? _keyboardProc;
    private Win32.HookProc? _mouseProc;

    /// 程序自己的一条休息流程，从装上钩子开始数。用的是单调 tick 的差值，
    /// 不是现实时间 —— 把系统的钟拨了，不该把休息节奏一起改了。
    public long NowMs => Math.Max(0, (long)Time.GetTicksMsec() - _startedAtMs);

    /// <summary>装钩子、泵消息、摘钩子 —— 全在<b>一条专用线程</b>上。
    ///
    /// <b>为什么不是主线程（2026-10-12 改，这一段的注释是反着写的，工单
    /// <c>desktop-comfort/02</c>）</b>：原来写着「低级钩子在安装它的那个线程上回调，
    /// 而引擎自己的循环就在泵 Windows 消息，所以两个钩子都挂在主线程上，不必再开一个线程去
    /// <c>GetMessage</c>」。那句话在 <b>2026-09-25</b> 是对的：那时候量的是「引擎的主循会派发
    /// 自己窗的 <c>WndProc</c>」，省一条线程换的就是那份方便。
    ///
    /// 代价是 <b>2026-10-12 才量到的</b>：<c>WH_MOUSE_LL</c> 在**全机输入的同步路径**上 ——
    /// 全机每一个鼠标事件都要过它，而它在主线程回调。主线程一忙（画画、读文件、
    /// <c>TrackPopupMenu</c>），**全机鼠标一起等**。所有者报的就是这个：「启动后鼠标好像被抢控制」。
    ///
    /// <b>所以多一条线程</b>：主线程和全机输入解耦。原先那版省下这条线程，
    /// 换来的是全机鼠标跟着主线程排队 —— <c>WH_MOUSE_LL</c> 本来就不该挂在主线程上。</summary>
private void HookThread()
    {
        // 「程序从这一刻起算」在 Start 那一侧（主线程）就定好了 —— 休息节奏按真实流逝的时间算，
        // 不该从这条线程起来的早晚开始。
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        _hookThreadId = Win32.GetCurrentThreadId();
        // 这条线程的「我起来了」：Stop 要拿它等线程真退（不是「递了 WM_QUIT」就算退了）。
        if (_hookReady != IntPtr.Zero) Win32.SetEvent(_hookReady);
        // GetMessage 循环：**它本身就是钩子能活下来的原因** —— `LowLevelHooksTimeout`
        // 认的是「装钩子的线程多久没泵消息」。这一发递回来 WM_QUIT 就走。
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
        // 「我真退了」：Stop 等的是这一发，不是「WM_QUIT 递出去了」。
        // 不置的话 Stop 每次白等两秒，而那两秒是主线程。
        if (_hookThreadExited != IntPtr.Zero) Win32.SetEvent(_hookThreadExited);
    }

    public void Start()
    {
        if (_hookReady == IntPtr.Zero)
            _hookReady = Win32.CreateEvent(IntPtr.Zero, true, false, null);
        if (_hookThreadExited == IntPtr.Zero)
            _hookThreadExited = Win32.CreateEvent(IntPtr.Zero, true, false, null);
        // 「程序从这一刻起算」在**主线程**上开始数：休息节奏按真实流逝的时间算，
        // 不该从那条钩子线程起来的早晚开始（那一发和 NowMs 的单调时钟差一点点）。
        _startedAtMs = (long)Time.GetTicksMsec();
        _hookThread = new Thread(HookThread) { IsBackground = true, Name = "Keeper hooks" };
        _hookThread.Start();
        // 等它真把两个钩子装上再返回：装钩子要在装它的那条线程上，
        // 而 App._Ready 后面几发已经开始问状态了。
        Win32.WaitForSingleObject(_hookReady, 5000);
    }

    public void Stop()
    {
        if (_keyboardHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
        if (_hookThread is { IsAlive: true })
            Win32.PostThreadMessage(_hookThreadId, Win32.WmQuit, IntPtr.Zero, IntPtr.Zero);
        _hookThread = null;
        // 等线程真退，不等就等于没摘。
        if (_hookThreadExited != IntPtr.Zero)
        {
            Win32.WaitForSingleObject(_hookThreadExited, 2000);
            Win32.CloseHandle(_hookThreadExited);
            _hookThreadExited = IntPtr.Zero;
        }
        if (_hookReady != IntPtr.Zero)
        {
            Win32.CloseHandle(_hookReady);
            _hookReady = IntPtr.Zero;
        }
    }

    /// 装钩子的那条线程自己的 id，和它的「起来了 / 退了」两个事件。
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookReady;
    private IntPtr _hookThreadExited;

    /// <summary><b>钩子装在哪条线程上</b>（日志读的那一发，工单 <c>desktop-comfort/02</c>）。
    ///
    /// 它和主线程的号**必须不一样** —— 一样就说明退回 2026-10-12 之前那版了，
    /// 而那一版的代价就是全机鼠标跟着主线程排队。</summary>
    public uint HookThreadId => _hookThreadId;

    // ---- 判定 -------------------------------------------------------------------
    //
    // 2026-09-29 之前这几条判定住在下面两个私有回调的体内：哪几类算输入、16 毫秒合流、
    // Esc 只认按下、非左键不进桥。于是「接口就是测试面」在这一处是破的 —— 唯一可测的形状
    // 是 Feed(nowMs)，而 Feed 只 Record，判定根本过不去接口，那条判据是空的（而且 Feed 零调用方）。
    //
    // 现在回调只做两件事：把 wParam/lParam **拆**成一个已解析事件（adapter 1），然后照判定办事。
    // 判定本身是 <see cref="Judge"/>：纯的，不碰 Win32、不碰时钟（nowMs 由调用方递进来），
    // 所以自检里造得出事件来问它（adapter 2）。两个 adapter 都在，这个 seam 不是假设的。

    /// <summary>一发已经解析好的输入事件属于哪一类。<see cref="OtherKey"/> 和
    /// <see cref="OtherMouse"/> 是「认不出但确实来了」那一档 —— 它们<b>照样算输入</b>
    /// （滚轮、中键都算），只是不进桥、不置旗。这一条是原来实施里的行为，不是新规矩：
    /// <c>OnMouse</c> 里 <c>CountMouse</c> 早退了，<c>Record</c> 照样跑。</summary>
    internal enum InputKind { KeyDown, OtherKey, MouseMove, LeftDown, LeftUp, RightDown, RightUp, OtherMouse }

    /// <summary><paramref name="Msg"/> 留着原始的 wParam，因为 <c>LastButtonEvent</c> 那行实测结果
    /// 印的是 <see cref="Win32.MouseMessageName"/> 给出的名字 —— 换成按 Kind 拼一个名字，
    /// 那条实测结果就跟着变了，而搬家不该动实测结果。</summary>
    internal readonly record struct InputEvent(InputKind Kind, int Msg, uint Vk, Win32.Point At, bool Injected);

    /// <summary>判定的三个答案：这一发算不算输入、要不要置 Esc 那颗旗、要不要出给桥。</summary>
    internal readonly record struct Judgement(bool Counts, bool Escape, bool ToBridge);

    /// <summary>判定本身。<paramref name="lastFlushMs"/> 是上一次真记进休息流程的时刻，
    /// 由调用方递进来 —— 它不读实例状态，所以自检里造得出事件、也造得出时序来问它。</summary>
    internal static Judgement Judge(long nowMs, InputEvent e, long lastFlushMs)
    {
        var keyboard = e.Kind is InputKind.KeyDown or InputKind.OtherKey;
        return new Judgement(
            // 键盘不合流（一次按键就是一发），鼠标合流到 CoalesceMs —— 移动一秒几百发，
            // 每发都写三个数没有意义。
            Counts: keyboard || nowMs - lastFlushMs >= CoalesceMs,
            // 只认 Esc 那一下，而且只在按下那一下。逃生口有两只手：那颗钮，还有键盘上的
            // Esc。键盘那一只手不去动窗的样式 —— 契约里「换步不碰窗：不闪、不重举」那条（行为不变量那一节）
            // 和 `## Solution` 那句「它拿不到焦点，所以不抢你的光标和按键」都留在原地，改走我们已经
            // 装着的这一把全局钩子：Esc 本来就路过这个进程，谁的焦点也没被拿走。
            // 要不要收尾是 App 那侧的闸门（那一步收不收鼠标）。
            Escape: e.Kind == InputKind.KeyDown && e.Vk == Win32.VkEscape,
            // 只报左键按下和松开 —— 日历上没有悬停这回事（触屏那一型本来也不产生 mouse_entered），
            // 而右键跟桥无关。
            ToBridge: e.Kind is InputKind.LeftDown or InputKind.LeftUp);
    }

    /// <summary>钩子那一侧的拆包（adapter 1）。<c>VkCode</c> 只在按下那一下才去读 ——
    /// 原来那一份就是这么短路的，而这一把钩子挂在全机输入的同步路径上，少一次 marshal 是一次。</summary>
    internal static InputEvent ParseKey(int wParam, nint lParam)
    {
        var down = wParam is Win32.WmKeydown or Win32.WmSysKeydown;
        return new InputEvent(down ? InputKind.KeyDown : InputKind.OtherKey, wParam,
            down ? Win32.ReadKeyboardHook(lParam).VkCode : 0, default, false);
    }

    /// <summary>同上。移动和「认不出的那一档」不 marshal 结构 —— 原来那一份也不读，
    /// 而移动一秒几百发。</summary>
    internal static InputEvent ParseMouse(int wParam, nint lParam)
    {
        var kind = wParam switch
        {
            Win32.WmMouseMove => InputKind.MouseMove,
            Win32.WmLButtonDown => InputKind.LeftDown,
            Win32.WmLButtonUp => InputKind.LeftUp,
            Win32.WmRButtonDown => InputKind.RightDown,
            Win32.WmRButtonUp => InputKind.RightUp,
            _ => InputKind.OtherMouse,
        };
        if (kind is InputKind.MouseMove or InputKind.OtherMouse)
            return new InputEvent(kind, wParam, 0, default, false);
        var raw = Win32.ReadMouseHook(lParam);
        return new InputEvent(kind, wParam, 0, raw.Pt,
            (raw.Flags & (Win32.LlmhfInjected | Win32.LlmhfLowerIlInjected)) != 0);
    }

    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var e = ParseKey(wParam.ToInt32(), lParam);
            var now = NowMs;
            var verdict = Judge(now, e, _lastFlushMs);
            if (verdict.Counts) Record(now);
            // 回调里还是什么都不写日志（这一条钩子挂在全机输入的同步路径上），只置一个旗。
            if (verdict.Escape)
            {
                _escapeSeen = true;
                EscapeEvents++;
            }
        }
        return Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private volatile bool _escapeSeen;

    /// 钩子看见过几次 Esc 按下（测量程序实测结果，不是判据本身）。
    public long EscapeEvents { get; private set; }

    /// 取走「有人按了 Esc」那面旗：读一次清一次，所以一按只收尾一次，不会每帧都收尾。
    public bool TakeEscape()
    {
        if (!_escapeSeen) return false;
        _escapeSeen = false;
        return true;
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var now = NowMs;
            var e = ParseMouse(wParam.ToInt32(), lParam);
            var verdict = Judge(now, e, _lastFlushMs);
            CountMouse(e, now);
            if (verdict.Counts) Record(now);
            // 出不出给桥由判定说，不在这里再筛一遍 —— 原来 CountMouse 里还有一道
            // `wParam is WmLButtonDown or WmLButtonUp`，同一条规矩写在两处。
            if (verdict.ToBridge) MouseObserver?.Invoke(e, now);
            // **吃掉那一刀**（2026-10-10）：只有委托给了、而它说「吃」才丢这一发。
            // 返回非零 = 系统不把这一下递给任何窗（实测结果在
            // `docs/research/desktop-layer-and-window-measurements.md`「钩子能不能吃掉那一档」那一节）。
            // **别的一律 `CallNextHookEx`** —— 那一族量输入要看得见全部，吞了它就瞎了。
            if (e.Kind == InputKind.LeftDown && MouseSwallower?.Invoke(e.At) == true)
            {
                _swallowed = e.At;
                _lastSwallowAt = e.At;
                _swallowCount++;
                return 1;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    // 钩子那侧收到了哪几类事件。「点不动」那两个候选（事件没进系统 / 进了系统但窗口收不到）
    // 只有这一侧的实测结果能分开，所以这里记的是事件身份，不只是时序。
    //
    // 回调里一行日志都不写：低级钩子挂在全机输入的同步路径上，往里塞一次磁盘写就把
    // 所有人的输入拖慢一次。这里只攒几个数，读的人自己挑时候打印。
    public long MoveEvents { get; private set; }
    public long ButtonDownEvents { get; private set; }
    public long ButtonUpEvents { get; private set; }
    public string LastButtonEvent { get; private set; } = "还没有一个按键事件";

    /// <summary>左键按下 / 松开的旁路出口，给日历那条桥用（<see cref="MouseBridge"/>）。
    ///
    /// 桥不自己装钩子：一条 <c>WH_MOUSE_LL</c> 已经挂在全机输入的同步路径上了，再装一条是
    /// 让所有人的输入多过一遍回调。这里加一个出口就够。
    ///
    /// 只报左键按下和松开 —— 移动一秒几百发，从钩子里往外递一个委托就是几百次；
    /// 而日历上没有悬停这回事（触屏那一型本来也不产生 <c>mouse_entered</c>）。
    /// 「只报左键」这一条现在由 <see cref="Judge"/> 的 <c>ToBridge</c> 说，不在订阅方再筛一遍。
    /// 订阅方要自己做「当场什么都不做，只排队」：这一发是在钩子里跑的。</summary>
    internal Action<InputEvent, long>? MouseObserver;

    /// <summary><b>这一发要不要吃掉。</b><c>null</c>（默认）= 全部放行；给了一个委托 =
    /// 问它这一次左键按下要不要丢。
    ///
    /// <para><b>为什么加在这里</b>（2026-10-10 所有者定夺「永远都不能抢鼠标」）：
    /// 拆掉那一扇收鼠标的窗之后，「清单和那颗钮点得动」这件事要有个新出处，而
    /// <b>这条钩子本来就是全机输入的同步路径上那一个</b> —— 它看得见每一次左键按下
    /// 连同坐标。设计文档 `docs/design/hit-testing.md`「判定住在全局钩子里」那一节说这一条，
    /// 走向和代价在 <c>docs/adr/0003</c>，「返回非零 = 吃掉」那一发的实测结果在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c>「钩子能不能吃掉那一档」那一节。</para>
    ///
    /// <para><b>委托只许答「吃 / 不吃」，不许在回调里干活</b>：它跑在全机输入的同步路径上，
    /// 里面做一次控件树遍历就是把所有人的输入都拖慢一次。所以它只判坐标，
    /// 勾清单那一下留给下一帧（<see cref="TakeSwallowed"/>）。</para>
    ///
    /// <para><b>它认的只有左键按下那一下</b>：拖动、滚轮、双击的第二下各走各的老规矩。</para>
    internal Func<Win32.Point, bool>? MouseSwallower;

    /// <summary>刚才那一发被吃掉的左键按下（屏幕坐标）。<b>读一次清一次</b> —— 一按只勾一次，
    /// 和 <see cref="TakeEscape"/> 那一族同款。</summary>
    private Win32.Point? _swallowed;

    /// 被吃掉过几发，和最后一发在哪儿。见 <see cref="SwallowCount"/> 和 <see cref="LastSwallow"/>。
    private int _swallowCount;
    private Win32.Point _lastSwallowAt;

    /// <summary>取走刚才被吃掉的那一下；没有就是 <c>null</c>。
    ///
    /// <b>internal</b>：坐标那个类型（<c>Win32.Point</c>）本身是 internal，
    /// 而一个 public 方法不许拿 internal 类型当参数或返回值（编译器会报）。
    /// 这一发只有 <c>App._Process</c> 一个读者，正是同一份程序集里的那一个。</summary>
    internal Win32.Point? TakeSwallowed()
    {
        if (_swallowed is not { } at) return null;
        _swallowed = null;
        return at;
    }

    /// <summary>被吃掉过几发。有窗那一版的对数是「点击窗收到几个鼠标事件」——
    /// 窗拆掉之后那个数没地方记了，量「勾上没勾上」要问的就是这一发。</summary>
    public int SwallowCount => _swallowCount;

    /// <summary>上一发被吃掉的那一下落在哪儿（给日志读）。
    ///
    /// <b>它跟 <see cref="_swallowed"/> 是两件事</b>：那一个读一次就清（留给下一帧去勾），
    /// 这一条不清 —— 它报的是「一共吃掉过几发、最后一发在哪儿」，量具读它是为了对账，
    /// 而对账那一发发生在勾完之后。</summary>
    public string LastSwallow =>
        _swallowCount == 0 ? "还没有一发给吃掉过" : $"({_lastSwallowAt.X},{_lastSwallowAt.Y})";

    private void CountMouse(InputEvent e, long nowMs)
    {
        switch (e.Kind)
        {
            case InputKind.MouseMove:
                MoveEvents++;
                return;
            case InputKind.LeftDown or InputKind.RightDown:
                ButtonDownEvents++;
                break;
            case InputKind.LeftUp or InputKind.RightUp:
                ButtonUpEvents++;
                break;
            // 滚轮、中键那一档：算输入（那是 Judge 的 Counts 说的），但不是一次「按键事件」，
            // 所以不进这两个计数、也不写 LastButtonEvent —— 跟原来那一份的早退是同一件事。
            default:
                return;
        }
        // 时刻用的是 OnMouse 顶上抓的那一个 now，不是这里再问一次 NowMs：一发事件一个时刻，
        // 原来那一份在同一个回调里问了两次，印出来的数可能差一毫秒。
        LastButtonEvent = $"{Win32.MouseMessageName(e.Msg)} @({e.At.X},{e.At.Y}) " +
                          $"注入={e.Injected} 钩子时刻={nowMs}ms";
    }

    private void Record(long now)
    {
        var since = now - _lastEventMs;
        _streakMs = since <= StreakGapMs ? _streakMs + since : 0;
        if (since > BurstGapMs) _burstStartedMs = now;
        _streakMs = Math.Max(0, _streakMs);
        _lastEventMs = now;
        _lastFlushMs = now;
    }

    public long IdleMs => NowMs - _lastEventMs;

    /// 连续用了多久。离开超过 StreakGapMs 就归零，所以休息回来是一段干净的。
    public long ContinuousMs => IdleMs > StreakGapMs ? 0 : _streakMs + IdleMs;

    /// <summary>这一段连续使用从<em>此刻</em>起算。
    ///
    /// <b>谁在什么时候调它</b>（2026-10-13 所有者定的那一版语义）：只有<b>真歇过</b>（走完、
    /// 离开够久）和<b>休息延迟那一串取完、这一轮作罢</b>这两种。
    /// <b>取消不清</b> —— 挥散和「提前结束休息」都还要往后推一格，而清了就把那一格推空。
    /// 那一段症状（点一次、一分钟后再点一次，看着就是无限）就是取消也清了攒的那一大段：
    /// 等过那一格之后「到线」早就成立，停手一秒就又抬一次。
    ///
    /// <b>只清累加量，那两个「上一次输入」的记号不动</b>：<see cref="_lastEventMs"/> 和
    /// <see cref="_burstStartedMs"/> 说的是「人还在不在打字」，而按完那颗钮之后通常真的马上
    /// 接着干活 —— 那是新的事实，不该被一次休息抹掉。要清掉的只有「歇之前攒了多久」这一个数。
    /// </summary>
    public void ResetStreak() => _streakMs = 0;

    /// 从 from 那一刻起，输入有没有一直在来。断了就是 0 —— 这就是「一下都不算回来」
    /// 那句规矩的落点：一次轻碰在 1 秒静默后就作废，永远加不到「你回来了」那一条线上。
    public long BusyMs(long from)
    {
        if (IdleMs > BurstGapMs) return 0;
        return NowMs - Math.Max(_burstStartedMs, from);
    }

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf ActivityJudge = new(
        () => $"输入判定通过：Esc 只认按下那一下、键盘不合流、鼠标合到 {CoalesceMs} 毫秒、" +
              "滚轮那一档照样算输入、桥上只有左键按下和松开",
        "输入判定", SelfTest);

    /// <summary>判定那一层的自检：全是「造一个事件、问 <see cref="Judge"/>」。
    ///
    /// 这一组存在的理由是接口而不是覆盖率。2026-09-29 之前这些判定住在两个私有回调体内，
    /// 唯一可测的形状是 <c>Feed(nowMs)</c>，而 <c>Feed</c> 只 <c>Record</c>、判定拿不到 ——
    /// 于是那条判据是<b>空的</b>，而且 <c>Feed</c> 全仓库零调用方（已删）。现在判定是一个纯函数，
    /// 自检里造得出事件、也造得出时序。
    ///
    /// <b>范围</b>：真键盘上切中文打一发、真鼠标那一发，仍然归「这一侧测不了」（判据一），
    /// 这一组<b>不</b>因此往欠账工单（<c>.scratch/keeper-debt/issues/</c>）加行。它要的是把<b>已经写下的</b>
    /// 判据搬到一个自检问得到的接口上，不是新增行为。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        var at = new Win32.Point { X = 10, Y = 20 };
        InputEvent Key(InputKind kind, uint vk) => new(kind, Win32.WmKeydown, vk, default, false);
        InputEvent Mouse(InputKind kind) => new(kind, Win32.WmMouseMove, 0, at, false);

        // 1. Esc 只认按下那一下 —— 认抬起的话一按会收尾两次。
        if (!Judge(0, Key(InputKind.KeyDown, Win32.VkEscape), -1_000).Escape)
            problems.Add("Esc 按下那一下没置旗");
        if (Judge(0, Key(InputKind.OtherKey, Win32.VkEscape), -1_000).Escape)
            problems.Add("Esc 抬起那一下也置了旗 —— 一按会收尾两次");
        if (Judge(0, Key(InputKind.KeyDown, Win32.VkEscape + 1), -1_000).Escape)
            problems.Add("不是 Esc 的那个键也置了旗");

        // 2. 键盘不合流：一次按键就是一发，隔着 1 毫秒也记。
        if (!Judge(1_000, Key(InputKind.KeyDown, 0x41), 999).Counts)
            problems.Add($"键盘那一发在合流窗里被丢掉了（窗是 {CoalesceMs} 毫秒，键盘不该受它管）");

        // 3. 鼠标合流到 CoalesceMs：差 1 毫秒不记，正好到就记。
        if (Judge(1_000 + CoalesceMs - 1, Mouse(InputKind.MouseMove), 1_000).Counts)
            problems.Add($"鼠标在 {CoalesceMs - 1} 毫秒上就记了，合流没生效");
        if (!Judge(1_000 + CoalesceMs, Mouse(InputKind.MouseMove), 1_000).Counts)
            problems.Add($"鼠标到了 {CoalesceMs} 毫秒还不记，合流把输入吃掉了");

        // 4. 哪几类算输入：滚轮和中键那一档「认不出」的照样算 —— 这是原来实施里的行为
        //    （CountMouse 早退了，Record 照样跑），不是新规矩。
        if (!Judge(1_000 + CoalesceMs, Mouse(InputKind.OtherMouse), 1_000).Counts)
            problems.Add("滚轮那一档不算输入了 —— 原来算");

        // 5. 非左键不进桥。右键进桥等于往别人指的地方点一下，移动进桥是假装有悬停。
        foreach (var kind in new[] { InputKind.LeftDown, InputKind.LeftUp })
            if (!Judge(0, Mouse(kind), -1_000).ToBridge)
                problems.Add($"左键 {kind} 没出给桥");
        foreach (var kind in new[]
                 {
                     InputKind.RightDown, InputKind.RightUp, InputKind.MouseMove, InputKind.OtherMouse,
                 })
            if (Judge(0, Mouse(kind), -1_000).ToBridge)
                problems.Add($"{kind} 出给了桥 —— 桥上只有左键按下和松开");

        return problems;
    }
}

/// <summary>调度要知道的那三件事，外加一个清零的口子。抽出来是因为状态机要能被假时钟推一遍：
/// 「只来一下输入不该挥散遮罩」这条只能靠模拟证，靠手点证不了。
///
/// <b>清零那一个为什么在接口上</b>：它改的是「这一段从哪一刻起算」这个状态，
/// 而那个状态只住在这一族里（<see cref="Activity"/> 和 <see cref="FakeInput"/> 各一份）。
/// 调度看得见 <see cref="ContinuousMs"/> 这个数，动不了它 —— 所以它问这一族要。</summary>
public interface Input
{
    long IdleMs { get; }
    long ContinuousMs { get; }
    long BusyMs(long from);

    /// <summary>这一段连续使用从<em>此刻</em>起算（见 <see cref="Activity.ResetStreak"/>）。</summary>
    void ResetStreak();
}

/// 一个假时钟加一段假输入，给自检用。
public sealed class FakeInput(long start) : Input
{
    public long Now = start;
    public long LastEvent = start;
    private long _streak;
    private long _burstStarted = start;

    public void Type(long untilMs)
    {
        // 每 200 毫秒一个事件，所以是一段真的连续输入，不是一下子跳过去。
        while (LastEvent + 200 <= untilMs)
        {
            LastEvent += 200;
            _streak += 200;
            Now = LastEvent;
        }
    }

    public void Advance(long ms) => Now += ms;

    /// 一次孤立的输入：给一下然后就静下来。
    public void Nudge(long atMs)
    {
        Now = atMs;
        LastEvent = atMs;
        _burstStarted = atMs;
    }

    public long IdleMs => Now - LastEvent;
    public long ContinuousMs => IdleMs > Activity.StreakGapMs ? 0 : _streak + IdleMs;
    public long BusyMs(long from) =>
        IdleMs > Activity.BurstGapMs ? 0 : Now - Math.Max(_burstStarted, from);

    /// 与 <see cref="Activity.ResetStreak"/> 同形：假时钟这一族也归零同一个数，
    /// 好让状态机那一组量的是产品那条路，不是「归零被绕过去了」那一条。
    public void ResetStreak() => _streak = 0;
}
