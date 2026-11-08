using System;
using System.Collections.Generic;
using System.Linq;

namespace Keeper;

/// <summary>这一帧检测那一族的实测结果。<b>形状是消费方定的，装配是生产者做的</b> ——
/// 和 <see cref="Input"/> 同款：状态机要能被假时钟一帧一帧推，那条接缝的形状就得先定下来。
///
/// 装的是：输入三问（<see cref="IdleMs"/> / <see cref="ContinuousMs"/> / <see cref="BusyMs"/>）、
/// 独占全屏中（<see cref="OwnsScreen"/>）、前台应用（<see cref="ForegroundApp"/>）、
/// 勿扰中（<see cref="Dnd"/>，带原因）。
///
/// <b>政策不在这里</b>：休息延迟、让位、静默全在 <see cref="Scheduler"/> 那一侧 —— 它拿这个聚合和
/// settings 的钥匙合成行为，和今天「<c>blocked</c> + <c>blockWhenFullscreen</c>」同款
/// （设计文档 `docs/design/detection.md`「检测那一族」那一节；取向在 <c>docs/adr/0006-detection-module-boundary.md</c>）。
/// 「独占全屏中」以前是散参数上的一个 bool，现在只是聚合里的一格，<b>那三处判定的形状一个字没动</b>。
///
/// 两条被否掉的路（同一张卡里）：<b>散参数再加一个</b> —— 参数到六个，每加一种实测结果动一次全仓签名；
/// <b>模块推事件、Scheduler 订阅</b> —— 「假时钟一帧帧推」那套自检是契约级手感规矩唯一的证法，
/// 事件驱动会把它拆掉。
///
/// 第三问带着 <c>from</c>（<see cref="Scheduler.StageAtMs"/> 那一刻），所以它是<b>一个函数</b>
/// 而不是三个数：那一刻是状态机自己知道的时刻，装配那一侧不知道它。
/// 勿扰那一格是**业务求值出来的**（时段表 + 名单，<see cref="Dnd"/>），所以它由 <c>App</c>
/// 拿着设置去求，而不是检测那一族给的实测结果（取向在 ADR-0006「勿扰是业务功能」那一节）。</summary>
public readonly record struct Detected(
    long IdleMs,
    long ContinuousMs,
    Func<long, long> BusyMs,
    bool OwnsScreen,
    Foreground.Measurement ForegroundApp,
    Dnd.Measurement Dnd)
{
    /// <summary>自检和测量程序那一份：真那一侧是 App 每帧照着检测那一族拼的，这里照着假时钟拼。
    ///
    /// <b>假时钟仍然只出输入</b>（<see cref="FakeInput"/> 是那三问的入口，没有被拆掉）；
    /// 独占全屏、前台应用、勿扰这三格由调用方给 —— 各自的判据住在各自那一族的自检里。</summary>
    public static Detected Fake(Input input, bool ownsScreen = false, Foreground.Measurement? foreground = null,
        Dnd.Measurement dnd = default) =>
        new(input.IdleMs, input.ContinuousMs, input.BusyMs, ownsScreen, foreground ?? default, dnd);
}

/// <summary>淡入什么时候来、来了走哪条休息流程、什么时候走。规矩就是下面这四条，一条没新加：
///
/// - 闹钟不是固定间隔，量的是连续用键盘鼠标的时长；离开一段时间就归零，所以休息回来
///   是一段干净的。
/// - 到点之后还要等一个输入的空档才淡入（triggerPauseMs）。落在你正在打字的时候，
///   它几秒后就得再淡出去，那看着像闪，不像提醒。
/// - 只来一下输入不算「回来了」，要连着输入满 dismissHoldMs 才算。键盘和鼠标一样算数。
/// - 被取消而没真休息的，下一次往后推一格「休息延迟」，一格一格往后取；取完那一轮作罢
///   —— 欠账清掉、连续输入从零再算，重新攒满一整段才再提醒。真休息过一次，
///   或者人离开了一阵，这个计数复位。
///   （**取消 = 挥散，或者按遮罩上那颗「提前结束休息」** —— 两者同一条路。）
///
/// 上面那四条是连续输入那条触发（<see cref="Input"/>）的规矩。现实时间有<b>两条</b>路：
/// 番茄钟（<see cref="Pomodoro"/>，跑到停为止的循环）和作息表（<see cref="BlockRaise"/>，
/// 一天的框，见「作息表」那一节的注释）—— 两者形状不同、互不吞并，而到点那一次抬**不是休息**。
///
/// 这里不碰窗口也不碰磁盘：淡入淡出由 Fader 做，记录由 History 做，都挂在事件上。
/// 这样状态机可以被一个假时钟推完，而那几条关于手感的规矩只有在模拟里才证得了。</summary>
public sealed class Scheduler
{
    public enum Phase { Hidden, Shown }

    // 卡片 01（S03）：下面这一族运行期字段全部收为私有 —— 类外唯一的读法是 SnapshotOf
    // 与那五个查询，唯一的写法是状态转换入口（Raise / RaiseBlock / Step / TogglePomodoro /
    // RaisePreview / EndBreak）。内部白盒判据（事件载荷对 Plan 那几组）照旧直接读它们。
    private Phase State = Phase.Hidden;

    /// 这一次淡入要走的休息流程，在举起它的那一刻读。就是这一步让设置窗在休息中途保存
    /// 改的是下一次淡入，而不是把脚下这条抽掉。
    private List<Step> Plan = [];
    private int StageIndex;

    private long ShownAtMs;
    private long StageAtMs;
    private long NextAttemptMs;

    /// <summary><b>这一段的起点</b>（现实时间上的那一刻）。倒计时和触发都从它算。
    ///
    /// <b>2026-10-21 所有者定夺</b>：倒计时之前量的是「连续输入」，于是中间任何一次
    /// 停手超过 60 秒就要从头再攒，25 分钟那条线在正常使用下几乎永远攒不到 ——
    /// 本机实测：桌面那份导出版连跑 5 小时，遮罩零次抬起（实测读数在
    /// <c>docs/research/continuous-streak-gap.md</c>）。
    /// <b>现在这一段按现实时间走</b>：到点就提醒，跟「连续输入」再没有任何关联。
    ///
    /// <b>它什么时候动</b>：真歇过（走完或离开够久）和「序列取完、这一轮作罢」那两种
    /// 归零 —— 对应契约那句「重新攒满一整段才再提醒」。**取消不清**（2026-10-13 定的），
    /// 那一段继续跑，往后推的是 <see cref="NextAttemptMs"/>。
    /// 到点那一次抬起来的（<see cref="BlockRaise"/>）不是休息，一个字节都不动它。
    /// 对外的读法是 <see cref="ElapsedMs"/>（卡片 01）。</summary>
    private long SegmentStartMs;

    /// 这一轮里连续取消了几次 —— <b>「下一次往后推一格」的格数就是它</b>
    /// （<see cref="NextWait"/> 按它取序列里那一格）。
    /// 三种情况把它清零：真休息过一次、人离开了一阵、以及<b>序列取完、这一轮作罢</b>。
    /// 对外的读法是 <see cref="Retry"/>（卡片 01）。
    private long Skipped;

    /// 托盘点出来的预览：走同一套走法，但不记账、不占下一次的时间。
    /// 对外的读法在 <see cref="Snapshot"/> 里（卡片 01）。
    private bool Preview;

    // ---- 番茄钟 ---------------------------------------------------------------
    //
    // 番茄钟是**自己那一条**按现实时间走的触发（设计文档 `docs/design/pomodoro.md`）。
    // 它和活动那条是**两条路**：
    // 番茄钟在跑的时候活动那条整个让位（`Step` 里那两行），到点就抬。
    //
    // 活动那条现在**也是**按现实时间走的（2026-10-21 所有者定夺）—— 但那不把它们合成一条路：
    // 番茄钟的到期时刻一次也不顺延，活动那条的门槛走的是「这一段从哪一刻起算」。
    //
    // 「在跑」这件事是运行期状态，不是配置 —— 和托盘那一下预览一样，点一下开始、点一下停。
    // 落盘的是 `pomodoro.workMin` 那个键（专注段多长）和 flows 里 pomodoro 那一行（休息什么形状）。

    /// 番茄钟是不是在跑。对外的读法在 <see cref="Snapshot"/> 里（卡片 01）。
    private bool Pomodoro;

    /// 这一轮专注段的到期时刻。跑起来的时候定下，休息走完再定下一次。
    private long PomodoroDueMs;

    /// 走完几轮。测量程序实测结果 + 托盘那一行上的提示，进日志，不进休息记录。
    /// 对外的读法在 <see cref="Snapshot"/> 里（卡片 01）。
    private long PomodoroRounds;

    /// <b>眼下这一次淡入是不是番茄钟那一轮</b>。它选的是 <see cref="Settings.PomodoroSteps"/>
    /// 而不是 <see cref="Settings.Steps"/>，所以举起之后这个旗标就是「这一条休息流程是哪来的」
    /// 的答案 —— 托盘提示和日志都要用它把两者分开。类外的读法：抬起那一刻由
    /// <see cref="Raised"/> 的载荷认出走的是哪一行，落下之后这一旗标已在
    /// <see cref="CloseOut"/> 里复位，没有类外读者（卡片 01 收为私有）。
    private bool PomodoroRound;

    /// <b>眼下这一次淡入是不是块到点那一次</b>，以及是哪一块。收尾那一侧读它认出是哪一档
    /// （<see cref="CloseOut"/> 里 <c>scored</c> 认的就是它）—— 到点那一次抬**不是休息**：
    /// 不进休息记录，所以健康度那一读法不动（设计文档 `docs/design/daily-routine.md`「作息表是一张按现实时间走的表」那一节）。
    /// 它和 <see cref="PomodoroRound"/> 是同一族东西：一个回答「这一条休息流程是哪来的」。
    ///
    /// **只读**（2026-10-14）：它答的是「抬起那一刻的事实」，不是一份随便谁都能改写的东西 ——
    /// 改它的只有 <see cref="Lift"/> 那一行。原来是可写的公开字段，于是「这一发是哪来的」
    /// 变成了一个谁都能顺手写掉的公开变量，而 <c>App</c> 那一侧正在拿它拼判定。
    public TimeBlock? BlockRaise { get; private set; }

    // ---- 作息表 ---------------------------------------------------------------
    //
    // **作息表是「一天的框」那一条路**（设计文档 `docs/design/daily-routine.md`「作息表是一张按现实时间走的表，时间块是表里的行」，
    // 取向和被否的路在 <c>docs/adr/0005-routine-text-and-preset-life-flows.md</c>）。它和
    // 番茄钟、活动那条是三条路：作息表是「一天的框」，番茄钟是「跑到停为止的循环」。
    //
    // 「现在在哪个块」「下一个到点还有多久」是 <see cref="Routine"/> 的<b>纯算术</b>，一个字段都不写。
    // 这一族留在状态机里的**只有一件运行期的事**：上一帧那一刻是几点。
    //
    // 「到点只在当下那一刻发、错过的不补」是**结构上**成立的，不是靠记得什么：跨过边界的判据是
    // 「正好往前一分钟」，于是程序刚起来那一次采样（哪怕正好撞在某一刻的起点上）不是到点，
    // 机器睡过的那一跳（几分钟、几小时）也不是到点，撞上全屏的那一次也不是。
    // 而番茄钟那一轮**相反** —— 它的到期时刻是现实时间定的，从不顺延，所以被静默掉的那一轮
    // 在块走完 / 全屏让开之后照抬（和「全屏一让就抬」同一条款）。两条规则各自一条，不互相顶替。

    /// 上一帧那一刻是几点（一天里的第几分钟）。<b>运行期的一件事，不落盘</b>：它就是「到点只在
    /// 当下那一刻」那一条的记号 —— 程序一起来它是空的，所以第一次采样不算到点（错过的到点不补）。
    private int? LastMinute;

    /// 测量程序用的一个口子：把「连续用多久该提醒」从分钟改成任意毫秒。设置里没有这个键，
    /// 也不该有 —— 它量的是触发，触发本身是契约
    /// （契约「行为不变量」那一条）定的，
    /// 这个口子改的只是阈值，好让验一次不必真等那么久。
    /// <b>唯一的写法是 <see cref="OverrideOwedMs"/>（卡片 01）</b>，类外读不到它本身。
    private long? OwedMsOverride;

    /// <summary>「这一段连续使用」那份实测结果。<b>空的就是不归零</b> —— 只在自检里
    /// 不接（那一族量的是别的），产品那一侧接的是 <see cref="global::Keeper.Activity"/>。
    ///
    /// <b>为什么状态机要持有它</b>：「休息过一次就把连续输入清零」这条判断住在
    /// <see cref="CloseOut"/>，而「清零」这个动作动的是 <see cref="Input"/> 里的状态 ——
    /// 状态机只看得见 <see cref="Input.ContinuousMs"/> 那一个数。给它一份，它自己就能清。
    /// （名字不叫 `Activity`：那个名字是那一族的类名，字段叫它会把类名遮住。）
    /// </summary>
    private readonly Input? Measured;
    public Scheduler(Input? measured = null) => Measured = measured;

    public event Action<StageNow>? Raised;
    public event Action<StageNow>? StageEntered;

    /// <summary>「现在在哪一步」的<b>全部</b>答案：这一步是什么、它让位要在多久之后、
    /// 第几步、共几步、是不是最后一步。
    ///
    /// 2026-09-29 之前这两个事件不带载荷，于是同一条休息流程的位置由<b>四个模块各存一份</b>：
    /// 这里存 <see cref="StageIndex"/> 和 <see cref="StageAtMs"/>，App 自己索引
    /// <c>_machine.Plan[_machine.StageIndex]</c>，Fader 存 <c>_step</c>/<c>_stepMs</c>，
    /// Sound 存 <c>_step</c>。中间靠调用方<b>记得</b>去索引。更要紧的是「第一步留多久」
    /// 因此有了两套公式：<see cref="StageMs"/> 对第一步取 <c>max(ms, fadeInMs)</c>，
    /// 而 Fader 屏幕上那行「还剩」按裸 <c>step.Ms</c> 数 —— 同一时刻两个数。
    /// 现在 <see cref="HoldsMs"/> 只有一个出处。
    ///
    /// <b>载荷里不放 trail 和 health</b>：那两个数来自 <see cref="History"/>，而这个类
    /// 「不碰窗口也不碰磁盘」（见类型注释）。把它们塞进事件等于让状态机去认识记录，
    /// 那是加一条依赖，不是收一处所有权。</summary>
    public readonly record struct StageNow(Step Step, long? HoldsMs, int Index, int Total, bool IsLast)
    {
        /// 实测结果里印的「进 step 2/3」是 1 起的。
        public int Number => Index + 1;
    }

    /// <summary>当下这一步。只有状态机自己知道答案，所以由它交出去 ——
    /// 指针落在休息流程外面时（预览可能在设置读进来之前就被举起来）交回 default，
    /// 认 <see cref="StageNow.Total"/> 是 0。</summary>
    public StageNow Current(Settings settings) => StageIndex >= Plan.Count
        ? default
        : new StageNow(Plan[StageIndex], StageMs(Plan, StageIndex, settings),
            StageIndex, Plan.Count, StageIndex + 1 >= Plan.Count);

    // ---- 读模型（卡片 01）----------------------------------------------------
    //
    // 类外只读这一族：一份 Snapshot 加几个查询，字段直读收在类内。
    // 快照现算，不保存副本 —— 休息中途保存设置改了 FadeInMs 时，重算的第一步 HoldsMs
    // 可能与当初事件载荷不同；产品没有这种读者，Shots 和自检都递同一份设置。

    /// <summary>类外能一次读到的全部运行期状态：哪一相、哪一步、这一发是哪来的、
    /// 上一次落下是哪一档，加上预览 / 番茄钟那几个运行期旗标。
    /// <see cref="Stage"/> 由 <see cref="SnapshotOf"/> 现算（走 <see cref="Current"/>），
    /// 不另存副本。</summary>
    public readonly record struct Snapshot(
        Phase Phase, StageNow Stage, TimeBlock? BlockRaise, Outcome LastOutcome,
        bool Preview, bool Pomodoro, long PomodoroRounds);

    /// <summary>当下那一刻的快照。<paramref name="settings"/> 只用来现算
    /// <see cref="StageNow"/>（和事件载荷同一条算式）。</summary>
    public Snapshot SnapshotOf(Settings settings) =>
        new(State, Current(settings), BlockRaise, LastOutcome, Preview, Pomodoro, PomodoroRounds);

    /// <summary>脚下这一步<b>还剩多久</b>；<c>null</c> = 「这一步没有到期时刻」
    /// （最后一个 ms:0 的 step，<see cref="StageMs"/> 答 null 的那一档）。
    /// 到点之后报 <c>0</c>。算式与 <see cref="StageMs"/> 同源：<c>HoldsMs - (now - StageAtMs)</c>。</summary>
    public long? StageLeftMs(Settings settings, long now)
    {
        var holds = Current(settings).HoldsMs;
        if (holds is null) return null;
        return Math.Max(0, holds.Value - (now - StageAtMs));
    }

    /// <summary>托盘「下次休息还有多久」那一行的读数 —— <b>这一段还差多久到线</b>。
    /// 包住静态纯函数那一份；<b>不认 OwedMsOverride</b> —— 托盘报的是契约里那把门槛，
    /// 和它点不点测量程序那把口子无关（保持托盘现在的读数）。
    /// 它和 <see cref="NextFadeLeftMs"/> 不是一回事，不合并：那一问答「距下次淡入」，
    /// 这一问答「还差多久到线」。</summary>
    public long? BreakLeftMs(Settings settings, long now) =>
        BreakLeftMs(settings, ElapsedMs(now), Pomodoro, State != Phase.Hidden);

    /// <summary>设置窗抬头「距下次淡入还有多久」：取「这一段还没走够」和
    /// 「被挥散之后的等待」里较短的那个；遮罩抬着时报 <c>0</c>。
    /// <b>认 OwedMsOverride</b>（测量程序 <c>--streak</c> 那把口子改的就是它）。
    /// 算式照搬 App.StatusLine 改前那半段，一个数都不换。</summary>
    public long NextFadeLeftMs(Settings settings, long now)
    {
        if (State == Phase.Shown) return 0;
        var owed = OwedMsOverride ?? (long)settings.ActivityTimeoutMin * 60_000;
        // **这一段按现实时间走**（2026-10-21 所有者定夺），不按连续输入。
        var walked = owed - ElapsedMs(now);
        // NextAttemptMs 是 0 的时候意思是「没有被挥散过，没什么要等的」，不是「早就该淡入了」。
        var retry = NextAttemptMs - now;
        var left = retry > 0 ? Math.Min(walked, retry) : walked;
        return Math.Max(0, left);
    }

    /// <summary>日志那一句「下一次最早多久、连着取消几次」的读数：欠账格数，
    /// 加距下一次淡入还有多少毫秒（<c>now</c> 之后为负 = 早已在等）。</summary>
    public (long Skipped, long NextAttemptInMs) Retry(long now) => (Skipped, NextAttemptMs - now);

    /// <summary>测量程序 <c>--streak</c> 唯一的入口：把「连续用多久该提醒」换成任意毫秒。
    /// 传 null 交回设置里那把门槛。原来那个可直接赋值的 public 字段收在这里，
    /// 只剩这一个方法（取向同 <see cref="BlockRaise"/> 那次「只读」收口）。</summary>
    internal void OverrideOwedMs(long? owedMs) => OwedMsOverride = owedMs;

    /// 参数是这一次算不算真休息了一次。
    public event Action<bool>? Lowered;

    /// <summary>这个 step 什么时候让位；null 是「没有到期时间」。
    ///
    /// 写在最后一个 step 上的 0 是「一直到有别的东西结束它」（设计文档 `docs/design/routine.md`）；
    /// 写在中间某个 step 上的 0 是「淡入一落定就进下一个」。第一个 step 把淡入本身算进
    /// 自己的长度，因为淡入没落定之前谈不上「这一站到了」—— 一个比淡入还短的 step 不能把
    /// 淡入切短，否则 flow 会在一层画了一半的绿色上面锁住屏幕。
    ///
    /// 有一处要单独点名：淡入的下限不算「到期」。fadetop 只有那一条 ms:0 的 step，把下限
    /// 也当到期的话，它会在 3 秒整自己淡出去、还按「休息过了」记账 —— 而设计文档 `docs/design/routine.md` 写的是
    /// 最后一步的 0「it stays until something else ends it」。文档那一句为准。</summary>
    public static long? StageMs(IReadOnlyList<Step> plan, int index, Settings settings)
    {
        var held = plan[index].Ms;
        if (held == 0 && index + 1 >= plan.Count) return null;
        return index == 0 ? Math.Max(held, settings.FadeInMs) : held;
    }

    /// <summary>连着取消之后的那一次等待。<b>一格一格往后取，取完就是 null</b> ——
    /// <paramref name="cancelled"/> 是「这是第几次取消」，1 起算；第 N 次取序列第 N 格，
    /// N 超过序列长度就<b>没有下一格</b>，交回 null。
    ///
    /// <b>null 是「这一轮到此作罢」，不是一个数</b>（2026-10-13 所有者定）：欠账清掉、连续输入
    /// 从零再算，重新攒满一整段才再提醒。所以连着取消几轮之后，它就不再提醒了 —— 而不是
    /// 「每取消一次翻倍、到封顶为止、一直重试」那种，一个时间表排满的人会被一直催。
    /// 往下走的那一步在 <see cref="CloseOut"/>，那一格取空时它做三件事：清计数、这一段从零再算、
    /// 不再推下一次淡入。
    ///
    /// <b>为什么单位在读的地方换算</b>：序列存的是<b>分钟</b>（界面上写的就是分钟），
    /// 而 <see cref="NextAttemptMs"/> 认毫秒。换算只在这一处发生，所以「界面上不出现毫秒」
    /// 那条和调度那头的算术不会分家。</summary>
    public static long? NextWait(Settings settings, long cancelled)
    {
        var delays = settings.RestDelays;
        if (cancelled < 1 || cancelled > delays.Count) return null;
        return (long)delays[(int)cancelled - 1] * 60_000;
    }

    /// <summary>这一段<b>已经走了多久</b>（现实时间，毫秒）。倒计时和触发都从它算。</summary>
    public long ElapsedMs(long now) => now - SegmentStartMs;

    /// <summary>这一条触发<b>还差多久到线</b>；<c>null</c> = 「这一行不出现」。
    ///
    /// <b>量的是什么</b>：<see cref="Step"/> 里那条自动触发读的是<b>这一段走了多久</b>
    /// （<c>elapsedMs</c> 对 <c>activityTimeoutMin</c>），<b>是现实时间</b> ——
    /// 2026-10-21 所有者定夺：倒计时跟「连续输入」再没有任何关联。
    /// 之前它量的是连续输入，于是中间停手超过 60 秒就要从头再算，
    /// 那一行会突然跳回 25:00（实测读数在 docs/research/continuous-streak-gap.md）。
    ///
    /// <b>为什么是纯函数</b>：它只做减法，政策还在 <see cref="Step"/> 里 ——
    /// 「到了线还要等一个输入的空档才淡入」那一条（<c>TriggerPauseMs</c>）不在这行里，
    /// 因为那不是「还差多久到线」，那是「到了线之后它还在等什么」，两件事一句话说不完。
    /// <b>报 0 而不是报「到了」</b>：到线之后还要等一个空档，那一段里这一行报 0，
    /// 而屏幕上此刻还什么都没有 —— 报一个正数会让人以为「还有这么久才来」，
    /// 而实际是「已经在等一个空档了」。
    ///
    /// <b>哪几档不出现</b>（和托盘上「时间块」「勿扰中」那两行同一个形状：
    /// <c>null</c> = 那一行整个不在，而不是摆一个空着的那一行）：
    /// 自动淡入关着（它不会自己来）、番茄钟在跑（那时活动那条整个让位，
    /// 节奏由现实时间定 —— 见 <see cref="Step"/> 那一段）、遮罩已经抬着
    /// （屏幕上就有钟了，托盘再报一个数是两条打架的话，和
    /// <see cref="PomodoroFocusLeftMs"/> 同一处置）。</summary>
    public static long? BreakLeftMs(Settings settings, long elapsedMs, bool pomodoro, bool shown)
    {
        if (!settings.AutoFade || pomodoro || shown) return null;
        var owed = (long)settings.ActivityTimeoutMin * 60_000;
        return Math.Max(0, owed - elapsedMs);
    }

    /// 一帧一帧推。now 由调用给（引擎的单调时钟），<paramref name="detected"/> 是这一帧检测那一族
    /// 的实测结果（<see cref="Detected"/>：三问 + 独占全屏 + 前台应用），由 App 每帧装配。
    ///
    /// 「别的程序正占着独占全屏」不再是单独一个参数 —— 它和另外几格一起进了聚合：<b>实测结果来源换了一处，
    /// 政策一行没动</b>，下面那三处 <c>BlockWhenFullscreen</c> 照旧是那三处。
    ///
    /// <paramref name="wallMinute"/> 是现实时间落在一天里的第几分钟 ——「按现实时间走」
    /// 那一族唯一的接缝：<b>调用方给时刻</b>，这一族只做算术和判定（<see cref="Routine.MinuteOfDay"/>）。
    /// 必填：现实时间只在一处进来（卡片 02）—— 产品那一侧是 <c>App._wallNow</c> 每帧缓存的那一份，
    /// 自检和测量程序递自己的假分钟。这一族自己不再问 <c>DateTime.Now</c>。
    public void Step(long now, Detected detected, Settings settings, int wallMinute)
    {
        // 这一刻先算出来、跨过边界的判据也先跑 —— **哪怕这一刻什么都不抬**：上一分钟必须记上，
        // 而它只能在「问过一次」之后才有下一次。「错过的到点不补」靠的就是这一步无条件地走。
        var minute = wallMinute;
        var block = At(minute, settings);
        var arrived = Fire(minute, settings, detected);

        if (State == Phase.Hidden)
        {
            // **静默有两族，各有各的判据**（设计文档 `docs/design/daily-routine.md`「作息表是一张按现实时间走的表，时间块是表里的行」和
            // 「勿扰是用户定义的静默规则」那两节）：
            // **睡眠那一块**读的是此刻生效的那一块 —— 种类是逐块显式写的键，这里只认「睡眠」
            // 这一个取值（<see cref="Settings.BlockKindSleep"/>），其余种类在这一行上什么都不是
            //（<see cref="Silent"/>；「静默是种类语义，不是默认政策」那条取向在 ADR-0005）。
            // **勿扰**读的是聚合里那一格（<see cref="Detected.Dnd"/>）—— 它是用户自己配的规则，
            // 求值在 <see cref="Dnd"/> 那一侧，判据和睡眠**没有一处相同**。
            //
            // **三个静默都在这一行里**（自动淡入、番茄钟那一轮、到点那一次）：这一行在
            // <see cref="Phase.Hidden"/> 分支的最前面，所以后面那两条触发一条都走不到 ——
            // 「同款」是这个意思，不是三处各写一遍。
            if (Silent(block, detected)) return;

            // 到点那一次排在番茄钟前面：它是**只发一次、错过不补**的那一种，而番茄钟那一轮的到期
            // 时刻从不被推后（<see cref="PomodoroDueMs"/>），所以遮罩占着的那一步里它照样在等 ——
            // 让开之后照抬，两件事一件没丢。倒过来的话，先到的那一个会被整个吞掉。
            if (arrived is not null)
            {
                RaiseBlock(now, settings, arrived);
                return;
            }

            // 番茄钟在跑的时候，活动那条触发整个让位：这一步的节奏由现实时间定。
            // 放在活动触发**前面**是因为它优先 —— 番茄钟在跑的时候到点就抬，不看这一段走了多久。
            if (Pomodoro && !Preview)
            {
                if (now < PomodoroDueMs) return;
                // 全屏避让对番茄钟一样管用：别的程序正占着一整块屏时这一轮不抬，全屏一让就抬
                // （到期时刻不动过 —— 它是现实时间定的，不该被「当时不方便」顺延）。
                // **勿扰是同一条款**：上面那一行 <see cref="Silent"/> 挡掉的那一轮，
                // <see cref="PomodoroDueMs"/> 同样一个字节都没动，勿扰一结束就照抬。
                if (settings.BlockWhenFullscreen && detected.OwnsScreen) return;
                Raise(now, settings, pomodoro: true);
                return;
            }

            if (Preview || !settings.AutoFade || now < NextAttemptMs) return;

            // **「连续输入」只在这里、且只在挥散过之后才参与**（2026-10-21 所有者定夺）。
            // 它答的是「人是不是离开了」—— 离开够久就把欠账清掉，下一次回到序列的第一格。
            // 没挥散过的时候（<see cref="Skipped"/> 是 0）压根不看它，
            // 所以它跟倒计时、跟触发门槛再没有任何关联。
            if (Skipped > 0 && detected.IdleMs > Activity.StreakGapMs) Skipped = 0;

            var owedMs = OwedMsOverride ?? (long)settings.ActivityTimeoutMin * 60_000;
            // **这一段按现实时间走**：从 <see cref="SegmentStartMs"/> 到点就提醒。
            if (now - SegmentStartMs < owedMs) return;
            if (detected.IdleMs < settings.TriggerPauseMs) return;
            if (settings.BlockWhenFullscreen && detected.OwnsScreen) return;
            // **逐块写的那一条**：这一块里，那次提醒让不让位。
            // 它不从种类推（ADR-0005）：默认 true 就是「让位」，要别的过法在某一行里写 false。
            if (block is { YieldsToInput: false }) return;

            Raise(now, settings);
            return;
        }

        if (StageIndex >= Plan.Count)
        {
            // 休息流程不会为空，但预览可能在设置读进来之前就被举起来，指针会落在外面。
            CloseOut(now, settings, taken: true);
            return;
        }

        var step = Plan[StageIndex];
        var visibleFor = now - ShownAtMs;
        var runs = StageMs(Plan, StageIndex, settings);
        var last = StageIndex + 1 >= Plan.Count;

        if (runs is long due && now - StageAtMs >= due)
        {
            if (last)
            {
                // 这条休息流程自己的休息走完了 —— 不管多短，都算休息过了。
                CloseOut(now, settings, taken: true);
                return;
            }
            StageIndex++;
            StageAtMs = now;
            // 进一个 step 不碰窗口：不闪、不重排、不抬起来，只有画的东西和鼠标的用途变了。
            // 载荷带着「这一步留多久」，所以订阅方不必回头索引 Plan、也不必自己再算一遍。
            StageEntered?.Invoke(Current(settings));
            return;
        }

        // 一步不能被挥散的 step，正是有它的意义所在：休息是发生在遮罩上的事，不是让人
        // 忍一块颜色。
        var steppedAway = detected.IdleMs >= settings.MinBreakMs;
        var wavedAway = step.Dismissible && detected.BusyMs(StageAtMs) >= settings.DismissHoldMs;
        if (!steppedAway && !wavedAway) return;

        var taken = visibleFor >= settings.MinBreakMs || steppedAway;
        CloseOut(now, settings, taken);
    }

    public void Raise(long now, Settings settings, bool pomodoro = false) =>
        // 番茄钟那一轮走的是 pomodoro 那一行，跟 flow 指着的那一行无关 ——
        // 它和连续输入那条触发是两条路，用户给后者配什么休息不关它的事。
        Lift(now, settings, pomodoro ? settings.PomodoroSteps() : settings.Steps(), pomodoro, null);

    /// <summary>块到点那一次抬：走<b>块指的那一行</b>休息流程 —— 块里写了名字就走那一行，
    /// 没写就走「这次休息走哪一条」指着的那一行（两条读法同一个 <see cref="Settings.StepsOf"/>，
    /// 不因为多了一个调用方就多一条名字解析的路）。
    ///
    /// <b>它不是休息</b>：<see cref="BlockRaise"/> 认下这一发，收尾时 <see cref="CloseOut"/>
    /// 交出的那一档是「进入下个时间段」，所以不进休息记录，健康度那一份不动。挥散或走完都随意。
    ///
    /// 什么时候抬是 <see cref="Step"/> 的事（跨过边界的那一分钟），这里是那一发真抬起来时走的那一步。</summary>
    public void RaiseBlock(long now, Settings settings, TimeBlock block) =>
        Lift(now, settings, settings.StepsOf(block.Flow.Length == 0 ? settings.Flow : block.Flow),
             false, block);

    private void Lift(long now, Settings settings, List<Step> plan, bool pomodoro, TimeBlock? block)
    {
        Plan = plan;
        PomodoroRound = pomodoro;
        BlockRaise = block;
        State = Phase.Shown;
        ShownAtMs = now;
        StageIndex = 0;
        StageAtMs = now;
        Raised?.Invoke(Current(settings));
    }

    /// <summary>此刻生效的是哪一块。<b>总开关关着就是没有块</b> —— 这一族所有读法都走它，
    /// 好让「这张表算出来是什么」（<see cref="Routine"/>，无状态）和「整张表要不要用」
    /// （<see cref="Settings.RoutineEnabled"/>）这分成两半的事只在这里合一次。</summary>
    private static TimeBlock? At(int minute, Settings settings) =>
        settings.RoutineEnabled ? Routine.At(settings.Blocks, minute) : null;

    /// <summary>此刻是不是静默 —— <b>两族判据的同一个落点</b>。
    ///
    /// <b>睡眠那一块</b>：只有种类写着「睡眠」的那一种，其余种类在这一行上什么都不是
    ///（<see cref="Settings.BlockKindSleep"/>、<see cref="TimeBlock.IsSleep"/>）。
    ///
    /// <b>勿扰</b>：聚合里那一格，<b>带原因</b>（<see cref="Dnd.Measurement"/>）—— 原因不进这一行，
    /// 托盘那一侧自己问（<see cref="Tray.DndWhy"/>）。它和上面那一族的差别是**判据不同**：
    /// 睡眠是「块这一行的种类写着什么」，勿扰是「时段表或名单此刻成不成立」，
    /// 两边各问各的判据，<b>不是把一条判据套在另一族上</b>。
    ///
    /// 为什么合成一处：三个静默（自动淡入 / 番茄钟那一轮 / 到点那一次）都从这一行过，
    /// 而「同款」是同一个落点，不是三处各写一遍。番茄钟那一轮<b>不受影响地照抬</b>——
    /// 它自己的到期时刻一个字节都没动（见 <see cref="Step"/> 里那一段）。</summary>
    private static bool Silent(TimeBlock? block, Detected detected) =>
        block?.IsSleep == true || detected.Dnd.On;

    /// <summary>跨过边界了吗：这一帧该抬的那一块，<c>null</c> 是这一刻不该抬。
    ///
    /// **「只在当下那一刻」和「错过不补」是同一条判据的两面**：跨过 = 上一帧那一刻<b>正好往前
    /// 一分钟</b>（<see cref="Routine.Until"/> 走一圈的算法，跨午夜也认），而这一刻生效的块和
    /// 上一帧那一个不是同一行。所以程序刚起来那一次采样不是到点（<see cref="LastMinute"/> 是空的），
    /// 机器睡过的那一跳不是到点，<c>now</c> 一次跳两分钟也不是到点。
    ///
    /// 「到点抬不抬遮罩」这一发受两把钥匙管，和连续输入那条触发同款：<c>autoFade</c> 关着不抬，
    /// 撞上独占全屏不抬 —— 而两者都**只是不抬**，不留任何记号，所以让开之后也不补
    ///（「时刻过了就是过了」）。静默的那一块连候选都不产生。
    /// </summary>
    private TimeBlock? Fire(int minute, Settings settings, Detected detected)
    {
        var previous = LastMinute;
        LastMinute = minute;
        if (previous is not { } was || Routine.Until(minute, was) != 1) return null;
        var next = At(minute, settings);
        if (next is null || Silent(next, detected)) return null;
        if (!next.Fade) return null;
        if (!settings.AutoFade) return null;
        if (settings.BlockWhenFullscreen && detected.OwnsScreen) return null;
        // 换块才抬：还在同一行里（或者这一刻不在任何块里）就不是一次交接。
        return SameRow(At(was, settings), next) ? null : next;
    }

    /// 是不是作息表里的同一行。**比值不比引用** —— 设置窗存一次会换掉整张表、连着换掉每一行的对象，
    /// 那不是「换了一个块」，拿引用比会把它说成换了（症状是保存设置的那一分钟莫名抬一次遮罩）。
    private static bool SameRow(TimeBlock? a, TimeBlock b) =>
        a != null && a.StartMinute == b.StartMinute && a.EndMinute == b.EndMinute
                    && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>托盘点「番茄钟」的那一下。开：<paramref name="now"/> 这一刻起算一个专注段。
    /// 关：停下，但**不动脚下这轮休息** —— 正在抬着的那一轮照样走完、照样记账。</summary>
    public void TogglePomodoro(long now, Settings settings)
    {
        if (Pomodoro)
        {
            Pomodoro = false;
            // 交回给连续输入那条触发之前，先给它一个正常的等待 —— 不然一个刚停下的番茄钟
            // 可能让活动触发在下一帧就抬一次，撞在同一个托盘点上。正常 = 序列的第一格
            // （没取消过的时候下一次该等的就是它）。取不到就是「一格都写不出来」，
            // 那就不推 —— 那正是那一串为空该有的样子。
            NextAttemptMs = now + (NextWait(settings, 1) ?? 0);
            return;
        }
        Pomodoro = true;
        PomodoroRounds = 0;
        // 正在休息的话，从这一轮走完之后才开始数 —— 专注段不从遮罩底下起算。
        PomodoroDueMs = State == Phase.Shown ? long.MaxValue : now + settings.PomodoroWorkMs();
    }

    /// 专注段还剩多久。**休息已经抬起来的时候给 0** —— 那时候遮罩上就有钟了，
    /// 托盘再报一个「还剩 0」是两条互相打架的话。
    public long PomodoroFocusLeftMs(long now) =>
        Pomodoro && State == Phase.Hidden ? Math.Max(0, PomodoroDueMs - now) : 0;

    /// 手动举一次给托盘看。预览不是一段休息，所以落下那次不进记录（<see cref="CloseOut"/>
    /// 里 <c>scored</c> 认的就是这个旗标），也不占用「取消之后往后推一格」那一串休息延迟。
    public void RaisePreview(long now, Settings settings)
    {
        Preview = true;
        Raise(now, settings);
    }

    /// 遮罩上那颗「提前结束休息」（和 Esc）走的那条路。<b>它和挥散完全同一条</b>：
    /// 都是取消、都记跳过、都往后推一格休息延迟 —— 2026-10-13 所有者定夺
    /// （它原来算「休息过」，那一版已经作罢）。
    ///
    /// <b>为什么是这一件而不是另一件</b>：它是用那颗钮代替手去挥它，动作用的是同一个人、
    /// 后果是同一样（这一次不去），所以按后果归族；分出第三种记账只会让「我到底欠不欠」
    /// 在界面上没处看。</summary>
    public void EndBreak(long now, Settings settings)
    {
        if (State == Phase.Shown) CloseOut(now, settings, taken: false);
    }

    private void CloseOut(long now, Settings settings, bool taken)
    {
        // 到点那一次抬**不是休息**（<see cref="BlockRaise"/>）：它不进休息记录，所以
        // 「挥散或走完都随意」；它也不欠连续输入那条触发的账 —— 那边的「下一次等待往后推」
        // 和下一次淡入的时刻一个字都不动（和番茄钟那一轮同一个理由：它有自己的钟）。
        var hint = BlockRaise != null;
        var scored = !Preview && !hint;

        // **连续输入从零再算的是两种**：真歇过（走完、或者离开够久）和「序列取完、这一轮作罢」。
        // 两种之外的**取消不清** —— 清了就等于把欠账也一笔勾销，而欠账正是「下一次提醒往后推」
        // 那件事本身；一边往后推一边把攒的那段清掉，等于没推（这正是 2026-10-13 之前
        // 「点完提前结束就无限」那个症状：歇之前那一段原封不动留着，等过第一格就又抬一次）。
        // 预览压根不是休息、到点那一次也不是 —— 它们归在 `scored` 之外，
        // 和 <see cref="LastOutcome"/> 那一族是同一个意思（记的那一次给「休息过」或「跳过」，
        // 不记的那一次按「预览」或「进入下个时间段」分开给，不再压成一格）。
        if (scored && taken) Measured?.ResetStreak();

        if (!hint)
        {
            if (taken)
            {
                // 真歇过：欠账清掉，下一次回到**序列的第一格**（「复位」落到等待上就是这句话）。
                Skipped = 0;
                NextAttemptMs = now + (NextWait(settings, 1) ?? 0);
                // **这一段从零再算**（2026-10-21 所有者定夺）：倒计时按现实时间走，
                // 歇完之后从这一刻重新数那一整段。
                SegmentStartMs = now;
            }
            else
            {
                // 取消一次往后推一格。取不到下一格 = 这一轮作罢：三件事一起做 ——
                // 欠账清掉、这一段从零再算、不再推下一次淡入（重新攒满一整段才再提醒）。
                Skipped++;
                var wait = NextWait(settings, Skipped);
                if (wait is { } ms) NextAttemptMs = now + ms;
                else
                {
                    Skipped = 0;
                    Measured?.ResetStreak();
                    NextAttemptMs = now;
                    SegmentStartMs = now;
                }
            }
        }
        State = Phase.Hidden;
        Preview = false;
        Plan = [];
        StageIndex = 0;
        Lowered?.Invoke(taken);
        // 记账由调用方去做（它才知道 history.jsonc 在哪）。这一次交出去的是四档里的哪一档
        // —— 记的那两档进休息记录，不记的两档各自认下自己（原来两档都压成一个 null，
        // 调用方分不清是预览还是到点那一次，只有这里知道）。
        LastOutcome = !scored
            ? (hint ? Outcome.BlockHandover : Outcome.Preview)
            : (taken ? Outcome.Taken : Outcome.Skipped);

        // 番茄钟的那一轮走完了：接着是下一个专注段，一轮接一轮跑到有人停它为止。
        //
        // 三件事在这里是刻意的：挥散掉一轮照样接下一轮（番茄钟有自己的钟，不问「你要不要再来」）；
        // 休息延迟那个欠账跟番茄钟的钟无关（那一串是连续输入那条触发的账，而走完一轮
        // 会把欠账清零 —— 提前结束一轮则和挥散同路，记一次取消）；
        // 预览落下的那一次**不**在这里动到期时刻（预览是托盘左键那一下，跟番茄钟的钟没关系）。
        if (Pomodoro && PomodoroRound && !Preview)
        {
            PomodoroRounds++;
            PomodoroDueMs = now + settings.PomodoroWorkMs();
        }
        PomodoroRound = false;
    }

    /// 上一次落下那一次是什么。**四档，不是一根可空的 bool**（2026-10-14 所有者定）。
    ///
    /// 原来它是 <c>bool?</c>：<c>true</c> 记休息、<c>false</c> 记跳过、<c>null</c> 不记 ——
    /// 而「不记」那一格里住着<b>两种完全不同的东西</b>：手动预览，和从时间块换到下一个
    /// 时间块时抬起的那一次遮罩。于是 <c>App</c> 拿到的 <c>null</c> 分不出是哪一种，
    /// 只好绕过这一族去读原始旗标、自己拼半边判定（<c>App.OnLowered</c> 问旗标点名是哪一块，
    /// <c>App.CloseFaders</c> 问可空值决定记不记）。四档把那一族本来就有的知识交出来，
    /// 那一族以外就不用再自己猜。
    public enum Outcome
    {
        /// <b>休息过</b>：够长，或者离开了一阵。记休息。
        Taken,

        /// <b>跳过</b>：挥散，或者按了「提前结束休息」。记跳过。
        Skipped,

        /// <b>预览</b>：托盘点「预览」的那一下，走同一套走法但不是一次休息。
        /// 不进记录，也不占「取消之后往后推一格」那一串休息延迟。
        Preview,

        /// <b>进入下个时间段</b>：从时间块换到下一个时间块时抬起的那一次遮罩。
        /// <b>它不是休息</b>：不进记录，健康度不动，挥散或走完都随意，
        /// 下一次淡入的时刻一个字不动。
        BlockHandover,
    }

    /// <see cref="Outcome"/> 那一族。**只有「休息过」和「跳过」两档进休息记录**
    /// （<c>App.CloseFaders</c> 的闸门认的就是它们），另两档都不进 —— 而健康度是从
    /// 那一份记录推出来的（<c>History.HealthPercent</c> 只读 <c>Records</c>），所以
    /// 不进记录就不动健康度：这一族根本不接记录那一侧的对象。
    public Outcome LastOutcome { get; private set; }

    /// 假时钟那一组自己搭的调试输出（<c>记={...}</c>）要的是**记不记、记哪一头**那半边，
    /// 不是四档的全貌 —— 那两处印的字面是一段早于四档的输出，它照旧印 True / False / 不记，
    /// 下面那条判据就是照着「记=不记」这几个字在读它。所以折回那半边的是这里，
    /// 不是把那一族退回可空的 bool。
    private static string ScoredText(Outcome outcome) => outcome switch
    {
        Outcome.Taken => "True",
        Outcome.Skipped => "False",
        _ => "不记",
    };

    // ---- 工单 03：这一族的绿话跟着判据住，不再由 Probe.cs 抄一份 ----------------------
    //
    // 这一族各组（含卡片 01 的 scheduler-read）各自带一句「这一组量到了什么」。
    // **为什么在这里而不是在 Probe.cs**：那句话
    // 讲的是判据本身，判据改了它得跟着改 —— 而字面量写在登记那一行时编译器管不着
    // （判据删掉一条断言，那句话照样印「它量过了」）。见 Criterion 的类型注释。
    //
    // **绿话里不写死数**：这一族引的数（番茄钟到点那一刻等）一律现问。

    internal static readonly CriterionOf StateMachine = new(
        () => "调度模拟通过：擦一下挥不散、连续输入才算回来、" +
              "休息延迟一格一格取（写到几格就有几格、取完那一轮作罢，欠账和这一段都归零）、" +
              "「提前结束休息」和挥散同一条路、离开算休息、" +
              "倒计时和触发按现实时间走（中间停手超过一分钟也照样到线）、" +
              "挥散过之后离开够久欠账清零、托盘那半句和触发量的是同一个数、" +
              "番茄钟到点就抬、走 pomodoro 那一行、走完接下一轮、在跑的时候活动那条让位",
        "调度状态机", SelfTest);

    internal static readonly CriterionOf ReadModel = new(
        () => "读模型通过：Hidden 时快照的 Stage.Total 是 0、举起后 Number / Total 与 Plan 对上；" +
              "StageLeftMs 对没有到期时刻的 step 回 null、随时间递减、到点回 0；" +
              "BreakLeftMs 在关自动淡入 / 番茄钟在跑 / 遮罩抬着三档回 null、与静态纯函数同值；" +
              "NextFadeLeftMs 与抬头原算式逐档同值、遮罩抬着回 0、OverrideOwedMs 只改它不改 BreakLeftMs；" +
              "Retry 在第一次取消后报一格欠账和序列的第一格",
        "调度读模型", ReadSelfTest);

    internal static readonly CriterionOf RoutineFire = new(
        () => "块到点通过：只在跨过边界的那一分钟抬（前后一分钟都不抬、跨午夜那分钟也算）、" +
              "程序刚起来那一次采样不补（哪怕正好撞在起点上）、机器睡过的那一跳不补、" +
              "不是换块就不抬（还在同一行里 / 走到「不在任何块里」）；" +
              "每个到点都对过算法自己说的「下一个到点还有几分钟」",
        "块到点", RoutineFireSelfTest);

    internal static readonly CriterionOf RoutineNotABreak = new(
        () => "到点那一次不是休息通过：落下时交出「不记」而活动那一次交出「记」、" +
              "休息延迟那个欠账和下一次淡入的时刻一个字没动、" +
              "健康度和休息记录前后逐字节相同（真 History，推一条记录进去它们立刻就动）",
        "到点那一次不是休息", RoutineNotABreakSelfTest);

    internal static readonly CriterionOf RoutineGuard = new(
        () => "到点那两把钥匙通过：autoFade 关着不抬且拧回来也不补、撞上独占全屏不抬且让开之后也不补，" +
              "而番茄钟那一轮在同一把钥匙下让开就抬（两条规则确实不一样）、" +
              "这一块自己写「不抬」就不抬、这一块自己写「让位」就不插活动那条提醒（两条都逐块写，键与默认值问声明表）",
        "到点那两把钥匙", RoutineGuardSelfTest);

    internal static readonly CriterionOf SleepSilence = new(
        () => "睡眠那一块的静默通过：自动淡入不抬、番茄钟到点不抬（钟照跑、到期时刻不动）、" +
              "落在它里面的别的块到点不抬、进它自己的那一刻也不抬（摘掉种类就抬）、" +
              "块走完那一刻起两条触发都回来、到点没抬的那一轮块走完之后补抬且走 pomodoro 那一行",
        "睡眠那一块的静默", SleepSilenceSelfTest);

    internal static readonly CriterionOf RoutineSwitch = new(
        () => "作息表总开关通过：关掉之后活动触发和番茄钟那两段实测结果与「压根没有表」逐字节相同、" +
              "拧开之后必须变（表里盖住一整天的睡眠那一块把两条触发都静默掉）、" +
              "托盘那一行的名字也先认这个开关",
        "作息表总开关", RoutineSwitchSelfTest);

    internal static readonly CriterionOf DndSilence = new(
        () => "勿扰的静默通过：自动淡入不抬、番茄钟到点不抬（钟照跑、到期时刻连着几帧都不动）、" +
              "别的块到点不抬而错过的那一次不补、勿扰结束之后欠的那一轮照抬且走 pomodoro 那一行、" +
              "手动预览不受管、遮罩已经在台上时勿扰开始不清台照常走完；" +
              "托盘「勿扰中」那一行成立时带着那半句（名单里那个 exe 名在上面）、把总开关关掉之后项数差一；" +
              "睡眠和勿扰各自一套判据（只配一个睡眠块 / 只配一条勿扰时段，各自挡自己的那一种）",
        "勿扰的静默", DndSilenceSelfTest);

    /// 状态机的自检：一个假时钟把它推完，查的都是契约（<c>.scratch/keeper-contract/spec.md</c>）里写下的承诺。
    /// 这几条靠手点证不了 —— 手上一次只给得出一下输入。
    public static List<string> SelfTest()
    {
        var checks = new Checks();

        Settings Fast() => new()
        {
            ActivityTimeoutMin = 1,
            FadeInMs = 3_000,
            FadeOutMs = 1_000,
            MinBreakMs = 20_000,
            TriggerPauseMs = 1_000,
            DismissHoldMs = 2_000,
        };

        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        // 一个 List<bool> 用 == 比是比引用，永远不相等；写报告也要把内容印出来，
        // 印一个 List`1[System.Boolean] 等于什么都没说。
        void ExpectTaken(List<bool> lowered, bool expected)
        {
            var written = string.Join('+', lowered);
            Expect(lowered.Count == 1, $"该落下一次，落了 {lowered.Count} 次：{written}");
            Expect(lowered[0] == expected, $"记账记成 {written}，该是 {expected}");
        }

        /// 推到闹钟响：连续输入满一分钟，再等出一个空档。
        (Scheduler, FakeInput, Settings) Armed(string flow = Settings.FlowFadeTop)
        {
            var input = new FakeInput(0);
            var machine = new Scheduler();
            var settings = Fast();
            settings.Flow = flow;
            input.Type(70_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "闹钟到点又赶上空档，却没淡进来");
            return (machine, input, settings);
        }

        checks.Check("到点又赶上空档才淡入", () =>
        {
            var input = new FakeInput(0);
            var machine = new Scheduler();
            var settings = Fast();
            input.Type(70_000);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "手还在动就淡进来了");
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "等出空档就该淡进来");
        });

        checks.Check("只来一下输入挥不散", () =>
        {
            var (machine, input, settings) = Armed();
            Expect(machine.State == Phase.Shown, "没淡进来");
            // 一下轻碰，然后静下来：这一段输入在 1 秒静默后就作废，永远加不到「你回来了」那一条线上。
            input.Nudge(input.Now + 400);
            for (var i = 0; i < 12; i++)
            {
                input.Advance(400);
                machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            }
            Expect(machine.State == Phase.Shown, "只来一下就淡出去了");
        });

        checks.Check("连续输入才算回来了，而且没真休息就记一次跳过", () =>
        {
            var lowered = new List<bool>();
            var (machine, input, settings) = Armed();
            machine.Lowered += taken => lowered.Add(taken);
            input.Type(input.Now + 2_400);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "连续输入满 dismissHoldMs 还没淡出去");
            ExpectTaken(lowered, expected: false);
            Expect(machine.Skipped == 1, $"欠账计数是 {machine.Skipped}，该是 1");
            // 等的那一格**从声明表问**，不写死分钟数：默认序列的格数和那一格都是出厂声明里的话，
            // 抄一份进判据就是「改一个键、判据还绿着」（`calendar.widthPercent` 那次栽过）。
            Expect(machine.NextAttemptMs == input.Now + NextWait(settings, 1),
                $"第一次挥散该等序列的第一格，实际多等了 {machine.NextAttemptMs - input.Now} 毫秒");
        });

        checks.Check("休息延迟是一串固定格子，一格一格取，取完那一轮作罢", () =>
        {
            var settings = Fast();
            // 出厂序列：两格。格数和每一格的分钟数都从声明表问，不抄。
            var delays = Settings.Decl("restDelay").AsIntList;
            var by = (int minutes) => (long?)minutes * 60_000;
            Expect(delays.Length == 2, $"出厂那一串该是两格，读到 {delays.Length} 格");
            Expect(NextWait(settings, 0) == null, "没取消过不该取格（0 不是「取消了一次」）");
            Expect(NextWait(settings, 1) == by(delays[0]), "第一次取消没取到第一格");
            Expect(NextWait(settings, 2) == by(delays[1]), "第二次取消没取到第二格");
            // 取完了 = 这一轮作罢：不是「停在最后一格」，也不是「再往后取」。
            Expect(NextWait(settings, 3) == null, $"两格的序列取到第三次该作罢，实际 {NextWait(settings, 3)}");
            Expect(NextWait(settings, 40) == null, "取空了还在往后取");
            Expect(NextWait(settings, -1) == null, "负的取消次数也取出了格子");

            // **序列写到几格就有几格**（不是恒定两格、也不是恒定翻倍）。
            var three = Fast();
            three.RestDelays = [2, 4, 6];
            Expect(NextWait(three, 1) == by(2), "写了三格，第一格取错");
            Expect(NextWait(three, 2) == by(4), "写了三格，第二格取错");
            Expect(NextWait(three, 3) == by(6), "写了三格，第三格取错");
            Expect(NextWait(three, 4) == null, "写了三格却能取到第四格");
            var one = Fast();
            one.RestDelays = [5];
            Expect(NextWait(one, 1) == by(5), "只写一格时第一格取错");
            Expect(NextWait(one, 2) == null, "只写一格却能取到第二格");
        });

        checks.Check("序列取完就作罢：欠账清零、连续输入从零再算", () =>
        {
            var settings = Fast();
            // 阈值 5 分钟、序列**一格**（1 分钟）—— 于是「取完」就在第一次取消之后立刻发生，
            // 而「攒够一整段」和「等满一次休息延迟」两件事量得出差别（序列两格时它们会叠在一起）。
            settings.ActivityTimeoutMin = 5;
            settings.RestDelays = [1];
            // 走 fadetop 那一行：出厂默认那一条是 todoList，而它**不可挥散**（只来一下输入赶不走它），
            // 这一族量的是「取消」，所以要一条挥得散的。名字从 `Settings` 问，不手抄字面量。
            settings.Flow = Settings.FlowFadeTop;
            var input = new FakeInput(0);
            // 带上 input —— 作废那一轮时状态机要靠它把「这一段连续使用」清零，
            // 而产品那一侧正是这么接的（`App` 把 `Activity` 递给 `Scheduler`）。
            // 不接就量不到产品那条路：那样测的是「归零被绕过去了」。
            var machine = new Scheduler(input);

            input.Type(310_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "攒够五分钟又赶上空档，却没淡进来");

            // 挥散 → 取消第一次 → 取到唯一那一格。
            // 报错时把那两样**前置事实**带出来：这一行量的是「取消之后取格」，
            // 而「挥不挥得散」由 `Plan[0].Dismissible` 决定 —— 它一变，报错就该指着它，
            // 而不是让读报错的人去猜是不是取格那一段坏了。
            input.Type(input.Now + 2_400);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.Skipped == 1,
                $"一次挥散该记一格欠账，计数是 {machine.Skipped}" +
                $"（这一行该走的那一步可挥散={machine.Plan.Count > 0 && machine.Plan[0].Dismissible}，" +
                $"序列=[{string.Join(", ", settings.RestDelays)}]）");

            // 第二次取消 → 取不到下一格 → **这一轮作罢**。
            input.Type(input.Now + 65_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "第一次挥散之后没等满那一格就又抬了");
            input.Type(input.Now + 2_400);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.Skipped == 0,
                $"序列取完之后欠账该清零，计数是 {machine.Skipped} —— 那就是「一直重试」那条老路");
            Expect(machine.NextAttemptMs == input.Now,
                $"作罢之后不该再推下一次淡入，实际还推了 {machine.NextAttemptMs - input.Now} 毫秒");

            // **作罢之后连续输入从零再算**：歇（挥散）之前攒的那 5 分多钟一笔勾销，
            // 而欠账也清了 —— 于是「攒够一整段」是眼下唯一拦着它的那一件事。
            input.Type(input.Now + 65_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden,
                "作罢才一分多钟就又淡进来了 —— 取消之前那一段没清掉，这一轮等于没作罢");
            // 对照（一定该抬）：从零真的攒够一整段，就该抬。
            input.Type(input.Now + 4 * 60_000 + 10_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "从零再攒够五分钟就该淡进来");
        });

        checks.Check("人离开算真休息，不算跳过", () =>
        {
            var lowered = new List<bool>();
            var (machine, input, settings) = Armed();
            machine.Lowered += taken => lowered.Add(taken);
            // 走开：静默满 minBreakMs。
            for (var i = 0; i < 60; i++)
            {
                input.Advance(400);
                machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
                if (machine.State == Phase.Hidden) break;
            }
            Expect(machine.State == Phase.Hidden, "走开了遮罩还挂着");
            ExpectTaken(lowered, expected: true);
            Expect(machine.Skipped == 0, "真休息过还能欠着账？");
        });

        // 「提前结束休息」和挥散是**同一条路**（2026-10-13 所有者定夺）。
        //
        // 原先它是「休息过」—— 那一条已经作罢，所以下面这组量的是新的那一条：按下它记一次
        // 取消、`Skipped` 走到 1、等序列的第一格。**和「提前结束算休息过」正对着**，
        // 那一条判据绿着就说明这一个人为改动没生效。
        checks.Check("点「提前结束休息」和挥散同路：记跳过、欠一格、等第一格", () =>
        {
            var settings = Fast();
            settings.ActivityTimeoutMin = 5;
            settings.Flow = Settings.FlowFadeTop;
            var input = new FakeInput(0);
            var machine = new Scheduler(input);
            input.Type(310_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "攒够五分钟又赶上空档，却没淡进来");

            machine.EndBreak(input.Now, settings);
            Expect(machine.State == Phase.Hidden, "提前结束之后遮罩还挂着");
            // **记跳过**：它和挥散一样是「这一次不去」，所以 App 把这一条写进记录时写的是跳过。
            Expect(machine.LastOutcome == Outcome.Skipped,
                $"落下该认「跳过」那一档，拿到的是 {machine.LastOutcome}");
            Expect(machine.Skipped == 1, $"按它该记一次取消，欠账计数是 {machine.Skipped}");
            Expect(machine.NextAttemptMs == input.Now + NextWait(settings, 1),
                $"按它该等序列的第一格，实际多等了 {machine.NextAttemptMs - input.Now} 毫秒");

            // **取消不清连续输入** —— 清了就把那一格推空，「往后推一格」成了一句空话。
            // 于是等满那一格之后它**又**抬了（歇之前那一段一直在往上加）。
            // 「不再一直催」不是靠取消时清零来的，是靠**序列取完、那一轮作罢**来的
            // （上面那一组量的就是那件事）。
            input.Type(input.Now + 65_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown,
                "等满序列那一格之后它没有再提醒 —— 「往后推一格」落空了");
        });

        // 上面那条的另一半：**真歇过**（走开够久）要清连续输入，而取消不清 ——
        // 这两件事合起来才是「什么时候从零再算」的完整答案。
        checks.Check("真歇过清连续输入，取消不清", () =>
        {
            var settings = Fast();
            settings.ActivityTimeoutMin = 5;
            settings.Flow = Settings.FlowFadeTop;
            var input = new FakeInput(0);
            var machine = new Scheduler(input);
            input.Type(310_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "攒够五分钟又赶上空档，却没淡进来");

            // 走开够久 → 真歇过（`steppedAway` 那一条），不是取消。
            // **推的是假时钟本身**，不是另传一个时刻：判「走开」读的是 `IdleMs`，
            // 而 IdleMs 是假时钟自己算的（`Now - LastEvent`）。
            var start = input.Now;
            while (machine.State == Phase.Shown && input.Now < start + settings.MinBreakMs + 5_000)
            {
                input.Advance(500);
                machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            }
            Expect(machine.State == Phase.Hidden, "走开了遮罩还挂着");
            Expect(machine.Skipped == 0, $"真歇过该把欠账清零，计数是 {machine.Skipped}");

            // 回来接着干：等满序列第一格 + 一点余量，停手一秒就该能抬（欠账在，序列没走完）。
            // **而攒的那一大段被清掉了** —— 一分多钟远远不到 5 分钟，所以这一刻不该抬。
            // **继续用同一个假时钟**：另造一个就等于量「一个全新的假输入」，而它本来就没有
            // 攒过东西 —— 那样这条判据是空过的。清零这件事发生在 `Measured` 那一族身上。
            input.Type(input.Now + 65_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden,
                "歇完一分多钟就又淡进来了 —— 真歇过没有把连续输入清零");
            // 对照（一定该抬）：从零真的攒够一整段，就该抬。
            input.Type(input.Now + 4 * 60_000 + 10_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "从零再攒够五分钟就该淡进来");
        });

        // 「提前结束休息」之后连续输入该从零再算 —— **2026-10-13 这条判据整个作废**。
        //
        // 它建立在「点完它算休息过」那一版规定上，而所有者已经把那一版推翻了：现在它
        // 和挥散完全同一条路（记取消、欠一格、等序列那一格），而**取消不清连续输入** ——
        // 清了就把欠账一笔勾销，「往后推一格」落空。
        //
        // **症状就是 2026-10-13 之前那个「点一次、一分钟后再点一次，看着就是无限」**：
        // 歇之前那一段原封不动留着，等过第一格之后「到线」早就成立，停手一秒就又抬。
        // 它现在由上面「点「提前结束休息」和挥散同路」那一组顶着 —— 那一组正对着它量。
        checks.Check("挂一分半钟都不算回来，只要每一秒半只来一下", () =>
        {
            var (machine, input, settings) = Armed();
            Expect(machine.Plan.Count == 1, $"默认该走 fadetop 那一条，读到了 {machine.Plan.Count} 步");
            // fadetop 那一步是 ms:0，意思是「一直挂到别的什么结束它」。而「别的什么」
            // 不该是随手来一下输入：每次之后都静 1.5 秒，那一段就攒不起来。
            for (var i = 0; i < 70; i++)
            {
                input.Advance(1_500);
                input.Nudge(input.Now);
                machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            }
            Expect(machine.State == Phase.Shown, "挂到 105 秒时，每 1.5 秒来一下输入把它挥散了");
        });

        checks.Check("todoList 是一步，落定之后挥不散了", () =>
        {
            var (machine, input, settings) = Armed();
            settings.Flow = Settings.FlowTodoList;
            machine.Raise(input.Now, settings);
            // 2026-10-06 所有者定夺：这一行从两步合成一步（理由是他自己的话 ——
            // 「发现休息居然会发生切换到 todo 清单这回事」）。所以步数是 1，
            // 而「挥不散」这一档现在由那唯一一步自己声明，不再靠换步接力。
            Expect(machine.Plan.Count == 1, $"todoList 该是一步，读到了 {machine.Plan.Count} 步");
            Expect(!machine.Plan[0].Dismissible,
                "那唯一步该是不可挥散的 —— 不然休息就是忍一块颜色");
            // 中途换步这件事本身已经不在默认那条休息流程里了：这一步从举起到走完，
            // 屏幕上不换东西（中间那一族 + 侧面那一列同屏）。所以只问挥不散。
            input.Type(input.Now + 30_000);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "那唯一步被挥散了");

            // 走完这一步算休息过，不算跳过。
            input.Advance(settings.Steps()[0].Ms + 1_000);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "这一步走完了，窗还站着");
            Expect(machine.LastOutcome == Outcome.Taken,
                $"走完该认「休息过」那一档，记账拿到的是 {machine.LastOutcome}");
        });

        checks.Check("进下一个 step 不重排窗口，只报一次事件", () =>
        {
            var entered = 0;
            var enteredAt = -1L;
            var payload = default(StageNow);
            var (machine, input, settings) = Armed();
            // 这一条要测的是「换步不碰窗」，所以得挑一条**有两步**的休息流程：
            // 2026-10-06 之后 todoList 合成一步了（默认那条不再换步），带换步的是 pomodoro。
            settings.Flow = Settings.FlowPomodoro;
            machine.Raise(input.Now, settings);
            Expect(machine.Plan.Count == 2,
                $"pomodoro 该有两步（这一条判据量的是换步），读到了 {machine.Plan.Count} 步");
            machine.StageEntered += now => { entered++; enteredAt = input.Now; payload = now; };
            // 第一步是淡入本身（fadeInMs 那 3 秒），走完进第二步。
            while (entered == 0 && input.Now - machine.ShownAtMs < 10_000)
            {
                input.Advance(400);
                machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            }
            Expect(entered == 1, $"该进一次第二步，进了 {entered} 次");
            var arrivedAfter = enteredAt - machine.ShownAtMs;
            Expect(arrivedAfter >= settings.FadeInMs && arrivedAfter < settings.FadeInMs + 400,
                $"淡入那一步不是走完 fadeInMs 才让位：{arrivedAfter}");
            Expect(machine.StageAtMs == enteredAt, "进去的时刻没记下来");

            // 载荷本身。这几条钉的是「订阅方不必再回头索引 Plan、也不必自己算这一步留多久」——
            // 2026-09-29 之前事件不带东西，App 两件事都自己做，于是同一条休息流程的位置有四个持有者。
            Expect(payload.Index == 1 && payload.Number == 2,
                $"事件带的那一步该是第 2 步（下标 1），带的是第 {payload.Number} 步（下标 {payload.Index}）");
            Expect(payload.Total == machine.Plan.Count,
                $"事件带的总步数 {payload.Total} 跟 Plan 里那 {machine.Plan.Count} 步对不上");
            Expect(ReferenceEquals(payload.Step, machine.Plan[1]), "事件带的那一步不是 Plan 里那一步");
            Expect(payload.HoldsMs == StageMs(machine.Plan, 1, settings),
                $"「这一步留多久」该只有一个出处：事件带 {payload.HoldsMs}，" +
                $"StageMs 给 {StageMs(machine.Plan, 1, settings)}");
            Expect(payload.IsLast == (payload.Index + 1 >= payload.Total), "是不是最后一步跟步号对不上");

            machine.Step(input.Now + 1, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(entered == 1, "同一个 step 被重复进入");
        });

        checks.Check("第一步留多久只有一个出处", () =>
        {
            // 屏幕上那行「还剩」以前按裸 step.Ms 数，而状态机对第一步取 max(ms, fadeInMs)
            // （一个比淡入还短的 step 不能把淡入切短）。ms 写得比 fadeInMs 短的时候，
            // 同一时刻两个数。这一条钉住事件递出去的是状态机那一个，所以 Fader 读到的和判定同源。
            var (machine, input, settings) = Armed();
            // 这一条量的是「第一步把淡入算进自己的长度」，所以要挑一条第一步 ms 是 0 的 ——
            // todoList 2026-10-06 合成一步之后它的 ms 是 60000，用它量不到 max() 那一支。
            settings.Flow = Settings.FlowPomodoro;
            machine.Raise(input.Now, settings);
            Expect(machine.Plan.Count == 2 && machine.Plan[0].Ms == 0,
                $"这一条判据要的是「第一步 ms:0」，读到了 {machine.Plan.Count} 步 / 第一步 ms={machine.Plan[0].Ms}");
            var now = machine.Current(settings);
            Expect(now.Index == 0 && now.Number == 1, $"举起来那一刻该在第 1 步，读到第 {now.Number} 步");
            Expect(now.Total == machine.Plan.Count,
                $"总步数 {now.Total} 跟 Plan 里那 {machine.Plan.Count} 步对不上");
            Expect(now.HoldsMs == StageMs(machine.Plan, 0, settings),
                $"「这一步留多久」该只有一个出处：事件带 {now.HoldsMs}，" +
                $"StageMs 给 {StageMs(machine.Plan, 0, settings)}");
            Expect(now.HoldsMs >= settings.FadeInMs,
                $"第一步该把淡入算进自己的长度：事件带 {now.HoldsMs}，fadeInMs 是 {settings.FadeInMs}");

            // 另一档也要钉住，不然「同源」会被读成「都算出一个数来」：默认那条 fadetop
            // 只有一步 ms:0，意思是「一直挂到别的什么结束它」，所以它**没有到期时间**。
            var (solo, soloInput, soloSettings) = Armed();
            solo.Raise(soloInput.Now, soloSettings);
            Expect(solo.Plan.Count == 1, $"这一档要的是默认那条单步休息流程，读到了 {solo.Plan.Count} 步");
            Expect(solo.Current(soloSettings).HoldsMs == null,
                $"只有一步 ms:0 的那一条该没有到期时间，而不是 max(0, fadeInMs)=" +
                $"{Math.Max(solo.Plan[0].Ms, soloSettings.FadeInMs)}");
        });

        checks.Check("提前结束算取消，不算休息过", () =>
        {
            var lowered = new List<bool>();
            var (machine, input, settings) = Armed();
            machine.Lowered += taken => lowered.Add(taken);
            input.Advance(5_000);
            machine.EndBreak(input.Now, settings);
            // **反着来**：它原来交出的是 true（按休息过记账）。2026-10-13 所有者把它和挥散
            // 归成同一条路，所以这一次交出的是 false —— 记成跳过，App 才会把「跳过」写进记录。
            ExpectTaken(lowered, expected: false);
            Expect(machine.LastOutcome == Outcome.Skipped,
                $"落下该认「跳过」那一档，拿到的是 {machine.LastOutcome}");
        });

        checks.Check("挥散过之后，离开够久，欠账清零", () =>
        {
            // **欠账只有挥散过之后才有**（`Skipped > 0`），所以这一组先摆三格账进去。
            // 然后人走开一阵 —— 回来时该重新从序列的第一格开始挣，而不是接着往后取。
            //
            // **这一段不许走满**：触发现在按现实时间走，门槛 25 分钟（下面那行）。
            // 走满了它先抬起来，那这一组量到的就变成触发、不是欠账了。
            var input = new FakeInput(0);
            var machine = new Scheduler();
            var settings = Fast();
            settings.ActivityTimeoutMin = 25;
            machine.Skipped = 3;
            input.Type(70_000);
            input.Advance(Activity.StreakGapMs + 5_000);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "还没走满一整段就淡进来了");
            Expect(machine.Skipped == 0, $"离开一阵之后还留着 {machine.Skipped} 格账");
        });

        // **倒计时和触发按现实时间走**（2026-10-21 所有者定夺）。
        //
        // 这一组是那一条的**反证**：中间停手超过 60 秒（`Activity.StreakGapMs`），
        // 旧口径会把那一段清零，于是提醒永远攒不到线 —— 本机实测：导出版连跑 5 小时，
        // 遮罩零次抬起（实测读数在 `docs/research/continuous-streak-gap.md`）。
        // 现在这一段只认现实时间，停手多久都不清零。
        checks.Check("中间停手超过一分钟，这一段照样走到线", () =>
        {
            var input = new FakeInput(0);
            var machine = new Scheduler();
            var settings = Fast();          // 门槛 1 分钟
            input.Type(40_000);             // 先用 40 秒
            input.Advance(70_000);          // 然后停手 70 秒 —— 超过那一档
            input.Type(input.Now + 40_000); // 再用 40 秒
            input.Advance(1_500);           // 停一下，等出那个空档
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown,
                "中间停手超过一分钟就把这一段清零了 —— 触发还在按连续输入算");
        });

        // 托盘顶上那一行报的那个数：它必须和触发量的是**同一个数**。
        // 以前两边各量各的（一边连续输入、一边现实时间），于是托盘报的和实际触发的对不上。
        checks.Check("倒计时那一行报的是这一段还差多久", () =>
        {
            var settings = Fast();                       // 门槛 1 分钟 = 60_000 毫秒
            var machine = new Scheduler();
            machine.SegmentStartMs = 10_000;
            const long now = 25_000;
            Expect(machine.ElapsedMs(now) == 15_000,
                $"这一段该走了 15_000 毫秒，拿到的是 {machine.ElapsedMs(now)}");
            Expect(Scheduler.BreakLeftMs(settings, machine.ElapsedMs(now), false, false) == 45_000,
                $"还差多少该是 45_000 毫秒，拿到的是 {Scheduler.BreakLeftMs(settings, machine.ElapsedMs(now), false, false)}");
            // 到线之后报 0，不报负数。
            Expect(Scheduler.BreakLeftMs(settings, 60_000, false, false) == 0,
                "过线之后该报 0");
        });

        // ---- 番茄钟 ---------------------------------------------------------------
        //
        // 这一族量的全是「现实时间 vs 连续输入」那件事，两只手点不出来：
        // 「到点了你正好在打字，它还抬不抬」「一个不看你打字的东西凭什么能被这扇窗的
        // 假时钟推完」。所以它们住在自检里，和上面那些一起用同一个假时钟。
        //
        // 每一发都从 `Pomo()` 起：专注段 4 分钟，休息 30 秒 —— 专注段分钟数大是刻意的，
        // 走完一整轮要 4 分半，而自检里不能真等那 4 分半。

        /// 一台番茄钟：假输入 + 状态机 + 一份把专注段缩到 4 分钟、休息缩到 30 秒的设置。
        ///
        /// 休息那一步缩的不是 <c>pomodoro.workMin</c>，是 <b>flows 那张表里 pomodoro 那一行
        /// 的 <c>ms</c></b> —— 它本来就住在那儿（用户改的就是那一张），而出厂那一条是 5 分钟，
        /// 自检里不能真等 5 分钟走完一轮。改这一格而不是改键，正是为了钉住「休息多长住在那张表里」。
        (Scheduler, FakeInput, Settings) Pomo()
        {
            var settings = Fast();
            settings.Pomodoro.WorkMin = 4;
            settings.Flow = Settings.FlowTodoList;   // 让「走错那条休息流程」这件事量得到
            settings.Flows[Settings.FlowPomodoro].Steps[1].Ms = 30_000;
            var input = new FakeInput(0);
            var machine = new Scheduler();
            return (machine, input, settings);
        }

        checks.Check("番茄钟到点就抬，不问你打不打字", () =>
        {
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            Expect(machine.Pomodoro, "点了一下没开起来");
            Expect(machine.PomodoroDueMs == 4 * 60_000,
                $"专注段该是 pomodoro.workMin 分钟：到期在 {machine.PomodoroDueMs}");

            // 到点这一刻输入**一刻都没停** —— 连续触发那条路这时候早该抬了（70 秒就过线），
            // 番茄钟要等的是现实时间。
            var held = 5 * 60_000;
            input.Type(held);
            for (var t = 0L; t < 4 * 60_000; t += 500)
            {
                machine.Step(t, Detected.Fake(input), settings, Routine.At(12, 0));
                if (machine.State == Phase.Shown) break;
            }
            Expect(machine.State == Phase.Hidden, "手一直在动，番茄钟却提前抬了");
            machine.Step(held, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, $"到 {held} 毫秒还没抬，专注段该是 4 分钟");
        });

        checks.Check("番茄钟走的是 pomodoro 那一行，不是 flow 指着的那一行", () =>
        {
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.PomodoroRound, "这一发没被记成番茄钟那一轮");
            var wanted = settings.PomodoroSteps();
            Expect(machine.Plan.Count == wanted.Count,
                $"番茄钟该走 pomodoro 那一行的 {wanted.Count} 步，读到 {machine.Plan.Count} 步");
            Expect(ReferenceEquals(machine.Plan[0], wanted[0]),
                "番茄钟那一场走的不是 pomodoro 那一行的步");
            Expect(settings.Flow == Settings.FlowTodoList,
                "设置里的 flow 不该被番茄钟改掉 —— 那是另一条触发的东西");
        });

        checks.Check("休息走完接下一轮，一轮接一轮", () =>
        {
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            for (var round = 1; round <= 3; round++)
            {
                var due = machine.PomodoroDueMs;
                machine.Step(due, Detected.Fake(input), settings, Routine.At(12, 0));
                Expect(machine.State == Phase.Shown, $"第 {round} 轮该抬起来");
                // 把这一轮走完：淡入 + 休息那一步（30 秒）+ 一点余量。
                var at = due;
                while (machine.State == Phase.Shown && at < due + 120_000)
                {
                    at += 500;
                    machine.Step(at, Detected.Fake(input), settings, Routine.At(12, 0));
                }
                Expect(machine.State == Phase.Hidden, $"第 {round} 轮没走完");
                Expect(machine.PomodoroRounds == round, $"走完 {round} 轮，计数是 {machine.PomodoroRounds}");
                Expect(machine.Skipped == 0,
                    $"走完一轮该把欠账清零，计数是 {machine.Skipped} —— 番茄钟有自己的钟，不欠连续输入那条");
                Expect(machine.Pomodoro == true, "走完一轮自己停了");
                Expect(machine.PomodoroFocusLeftMs(at) == settings.PomodoroWorkMs(),
                    $"下一轮的专注段该重新从头数：剩 {machine.PomodoroFocusLeftMs(at)}，" +
                    $"而 workMin 是 {settings.PomodoroWorkMs()}");
            }
        });

        checks.Check("提前结束一轮照样接下一轮，而它记一次取消", () =>
        {
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            // 提前结束：淡入那一步是 ms:0，接着是休息那一步（dismissible=false），
            // 所以这里用「提前结束」那只手 —— 它和挥散同一条收尾，都记取消。
            machine.EndBreak(4 * 60_000 + 1_000, settings);
            Expect(machine.PomodoroRounds == 1, "提前结束一轮也该算走完一轮");
            // **这一条不再量「不欠翻倍那个账」** —— 那是旧语义（提前结束算休息过）。
            // 2026-10-13 起它就是一次取消，所以欠账该走到 1；番茄钟不欠账的那一句
            // 由上面「休息走完接下一轮，一轮接一轮」里 `Skipped == 0` 顶着。
            Expect(machine.Skipped == 1,
                $"提前结束一轮该记一次取消（和挥散同一条路），欠账计数是 {machine.Skipped}");
        });

        checks.Check("番茄钟在跑的时候，连续输入那条触发让位", () =>
        {
            var (machine, input, settings) = Pomo();
            // 先攒够连续输入：70 秒就过 activityTimeoutMin（1 分钟）。
            input.Type(70_000);
            input.Advance(1_500);
            machine.Step(input.Now, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "没开番茄钟时连续输入就该抬了 —— 前提这一条不成立");

            var (pomo, pomoInput, pomoSettings) = Pomo();
            pomoInput.Type(70_000);
            pomo.TogglePomodoro(pomoInput.Now, pomoSettings);
            pomoInput.Advance(1_500);
            pomo.Step(pomoInput.Now, Detected.Fake(pomoInput), pomoSettings, Routine.At(12, 0));
            Expect(pomo.State == Phase.Hidden,
                "番茄钟在跑，工作段中间被连续输入那条触发插了一次休息");
        });

        checks.Check("停番茄钟不掀脚下那一轮，也把时钟交还给连续输入", () =>
        {
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "该抬起来");
            machine.TogglePomodoro(4 * 60_000, settings);
            Expect(!machine.Pomodoro, "停不下来");
            Expect(machine.State == Phase.Shown, "停番茄钟把已经抬起来的那次休息也掀了");
            // 钟交回去了：连续输入那条触发下一次淡入被推到序列第一格之后，不许下一帧就抬。
            Expect(machine.NextAttemptMs == 4 * 60_000 + NextWait(settings, 1),
                $"停了之后下一次该等序列的第一格，实际多等了 {machine.NextAttemptMs - 4 * 60_000}");
            // 脚下这一轮照走。
            var at = 4 * 60_000;
            while (machine.State == Phase.Shown && at < 4 * 60_000 + 120_000)
            {
                at += 500;
                machine.Step(at, Detected.Fake(input), settings, Routine.At(12, 0));
            }
            Expect(machine.State == Phase.Hidden, "停了之后脚下那一轮也走不完");
            // 番茄钟那个钟已经交了：越过原来那个到期时刻，也不会再按它抬。
            var wasDue = machine.PomodoroDueMs;
            machine.Step(wasDue + 10_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "番茄钟停了还按自己的钟抬");
        });

        checks.Check("休息已经抬起来时，托盘那半句报 0", () =>
        {
            // 「还剩 m:ss」在专注段报剩余，在休息段必须闭嘴 —— 屏幕上已经有钟了，
            // 托盘再报一个数是两条打架的话。
            var (machine, input, settings) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            Expect(machine.PomodoroFocusLeftMs(0) == 4 * 60_000, "刚开就该报满");
            Expect(machine.PomodoroFocusLeftMs(0) != 0, "刚开就报 0");
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.PomodoroFocusLeftMs(4 * 60_000) == 0,
                "休息已经抬起来了，托盘还在报专注段剩余");
        });

        checks.Check("全屏避让对番茄钟一样管用，而且不推迟到期", () =>
        {
            var (machine, input, settings) = Pomo();
            settings.BlockWhenFullscreen = true;
            machine.TogglePomodoro(input.Now, settings);
            var due = machine.PomodoroDueMs;
            machine.Step(due, Detected.Fake(input, ownsScreen: true), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Hidden, "别的程序占着整块屏，番茄钟还是抬了");
            Expect(machine.PomodoroDueMs == due, $"避让把到期时刻推到了 {machine.PomodoroDueMs}（原 {due}）");
            machine.Step(due, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "全屏一让就该抬");
        });

        checks.Check("番茄钟盖住 autoFade，但盖不住全屏避让", () =>
        {
            // 开始番茄钟是一次明确的要求，autoFade 管的是「连续输入满多久」那条自动触发。
            var (machine, input, settings) = Pomo();
            settings.AutoFade = false;
            machine.TogglePomodoro(input.Now, settings);
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "autoFade 关着就把番茄钟一起关了 —— 它只管连续输入那条");
        });

        checks.Check("用户把 pomodoro 那一行删了，番茄钟回落到 flow，不是不休息", () =>
        {
            var settings = Fast();
            settings.Pomodoro.WorkMin = 4;
            settings.Flow = Settings.FlowTodoList;
            settings.Flows.Remove(Settings.FlowPomodoro);
            Expect(settings.PomodoroSteps().Count == settings.Steps().Count,
                "删掉 pomodoro 那一行之后，番茄钟该走 flow 指着的那一条");
            var (machine, input, _) = Pomo();
            machine.TogglePomodoro(input.Now, settings);
            machine.Step(4 * 60_000, Detected.Fake(input), settings, Routine.At(12, 0));
            Expect(machine.State == Phase.Shown, "删掉那一行就不休息了 —— 该回落而不是不抬");
        });

        checks.Check("出厂那三行都读得出自己那一步", () =>
        {
            // 「自带的三行写在这里是为了能被改」那条要的是三行都在，而且行数是从表上数出来的。
            var settings = Fast();
            var built = Settings.BuiltInFlows();
            foreach (var name in new[] { Settings.FlowFadeTop, Settings.FlowTodoList, Settings.FlowPomodoro })
                if (!built.ContainsKey(name))
                    checks.Fail($"出厂的休息流程里没有 {name}，表上是 {string.Join("、", built.Keys)}");
            if (settings.Steps().Count == 0) checks.Fail("默认那条休息流程读不出步");
            // todoList 现在的形状：2026-10-06 所有者定夺合成一步（原话：「发现休息居然会
            // 发生切换到 todo 清单这回事」）—— 中间那一族（时钟、倒计时、语录）
            // 和侧面那一条清单同屏，不靠换步接力。所以下面这几条钉的是那一次定夺
            //（足迹和健康度那天晚些时候从遮罩上撤了，所以这一步也不写它们）。
            var list = settings.StepsOf(Settings.FlowTodoList);
            if (list.Count != 1)
                checks.Fail($"todoList 该是一步（同屏，不换步），读到 {list.Count} 步");
            if (list.Count == 1)
            {
                var one = list[0];
                foreach (var want in new[] { "clock", "text", "checklist" })
                    if (!one.Content.Contains(want))
                        checks.Fail($"todoList 那唯一步的 content 里没有 {want}，读到 " +
                                     $"{string.Join('+', one.Content)}");
                if (string.IsNullOrEmpty(one.Text))
                    checks.Fail("todoList 那唯一步没带语录 —— 出厂就该有一句，装上才看得见那个位置");
                if (!one.Interactive)
                    checks.Fail("todoList 那唯一步不收鼠标 —— 侧面那列清单就点不动了");
                if (one.Dismissible)
                    checks.Fail("todoList 那唯一步只来一下输入就能赶走 —— 那不是休息");
            }
            var rest = settings.PomodoroSteps();
            if (rest.Count != 2)
                checks.Fail($"pomodoro 那一行该是「淡入 + 五分钟短休」两步，读到 {rest.Count} 步");
            if (rest.Count == 2)
            {
                if (rest[1].Ms != 5 * 60_000)
                    checks.Fail($"pomodoro 那一步的休息该是 5 分钟，读到 {rest[1].Ms} 毫秒");
                if (rest[1].Dismissible)
                    checks.Fail("pomodoro 的短休只来一下输入就能赶走 —— 那就不是休息了");
            }
        });

        checks.Check("时长的写法：不到一小时是 m:ss，过了那一头是 hh:mm:ss", () =>
        {
            // 托盘那一行、tooltip 和遮罩上那行「还剩」都读这一处。边界那几个数
            // （0、59、60、61、3600、3601）全得钉住。
            //
            // **2026-10-08 翻案**：这一条原来写死「不带小时」，理由是
            // 「没人把专注段调成两小时以上」—— 那是**番茄钟**的范围，
            // 而托盘顶上新加的那一行报的是连续输入还差多久到线，
            // 那个键（`activityTimeoutMin`）的上限是 **120 分钟**。
            // 前提没了，于是 90 分钟那一档会印成「90:00」—— 读不出那是九十分钟。
            // 所以一小时那一头写出来，而**番茄钟那一行一个字没变**（它到不了那一档，
            // 下面 25 分钟和 65 秒那两条就是它那一边的不变证明）。
            if (Tray.Clock(0) != "0:00") checks.Fail($"0 毫秒印成「{Tray.Clock(0)}」");
            if (Tray.Clock(59_000) != "0:59") checks.Fail($"59 秒印成「{Tray.Clock(59_000)}」");
            if (Tray.Clock(60_000) != "1:00") checks.Fail($"60 秒印成「{Tray.Clock(60_000)}」");
            if (Tray.Clock(61_000) != "1:01") checks.Fail($"61 秒印成「{Tray.Clock(61_000)}」");
            if (Tray.Clock(3_600_000) != "1:00:00") checks.Fail($"一小时印成「{Tray.Clock(3_600_000)}」");
            if (Tray.Clock(3_601_000) != "1:00:01") checks.Fail($"一小时零一秒印成「{Tray.Clock(3_601_000)}」");
            if (Tray.Clock(90 * 60_000) != "1:30:00") checks.Fail($"九十分钟印成「{Tray.Clock(90 * 60_000)}」");
            // **番茄钟那一侧不变**：它到不了两小时，所以这两条是「那一行没被顺带改掉」的证据。
            if (Tray.Clock(25 * 60_000) != "25:00") checks.Fail($"25 分钟印成「{Tray.Clock(25 * 60_000)}」");
            if (Tray.Clock(-5_000) != "0:00") checks.Fail($"负数印成「{Tray.Clock(-5_000)}」");
        });

        return checks.Report();
    }

    // ==================================================================
    // 作息表到点那一族的判据（工单 03）
    //
    // 五组，分的是**要什么**：到点那一刻（routine-fire）、那一次不是休息（routine-notbreak）、
    // 两把钥匙（routine-guard）、睡眠的静默（routine-sleep）、总开关（routine-switch）。
    // 托盘那一行是第六组，在 `Tray.BlockRowSelfTest`（它量的是菜单，不量状态机）。
    //
    // **全部纯算术**：假输入 + 状态机 + 在内存里造的表，不碰磁盘也不碰 %APPDATA%。
    // 时刻一律由 <see cref="HM"/> 算出来，块名取自这一组自己造的表 ——
    // 一个手抄的「09:00」或一句手抄的块名量的是当初那份副本（`CODING_STANDARDS.md` 第 6 条）。
    // ==================================================================

    /// 那一刻是「几点几分」换算成一天里的第几分钟。<b>不抄 1439、1500 那一类数</b>：
    /// 一天有多少分钟是 <see cref="Routine.MinutesPerDay"/> 推的（那一个 60 也在那儿）。
    private static int HM(int hour, int minute) => hour * Routine.MinutesPerHour + minute;

    /// 一份和 <see cref="SelfTest"/> 里那份同款的快设置（闹钟 1 分钟、淡入 3 秒），加上
    /// 番茄钟那一族的缩短 —— 这一族不缩它就得多等四分半。
    private static Settings RoutineFast(string flow = Settings.FlowFadeTop)
    {
        var s = new Settings
        {
            ActivityTimeoutMin = 1,
            FadeInMs = 3_000,
            FadeOutMs = 1_000,
            MinBreakMs = 20_000,
            TriggerPauseMs = 1_000,
            DismissHoldMs = 2_000,
        };
        s.Flow = flow;
        s.Pomodoro.WorkMin = 4;
        // 缩的是 flows 那张表里 pomodoro 那一行的步长（用户改的就是那一张），
        // 不是 `pomodoro.workMin` —— 理由和 Scheduler.SelfTest 里 `Pomo()` 那一处一样。
        s.Flows[Settings.FlowPomodoro].Steps[1].Ms = 30_000;
        return s;
    }

    /// 造一行时间块。<b>行为那两个键逐行显式写</b>（契约：不从种类推），
    /// 所以这里也照写 —— 自检要量的正是「写了的那个值说了算」。
    private static TimeBlock Row(int start, int end, string name, string kind = "",
        string flow = "", bool fade = true, bool yields = true) => new()
    {
        StartMinute = start, EndMinute = end, Name = name, Kind = kind,
        Flow = flow, Fade = fade, YieldsToInput = yields,
    };

    /// 两段实测结果不一样时把**差在第几行**带出来（这一族有一组要比逐字节）。
    private static void Fail(string what, string want, string got)
    {
        var a = want.Split('\n');
        var b = got.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : "（没有这一行）";
            var right = i < b.Length ? b[i] : "（没有这一行）";
            if (left == right) continue;
            throw new Exception($"{what}：第 {i + 1} 行 '{left}' → '{right}'（一段 {a.Length} 行，一段 {b.Length} 行）");
        }
        throw new Exception(what);
    }

    /// <summary>到点只在跨过边界的那一分钟抬；错过的（程序刚起来、机器睡过）不补。
    ///
    /// 承重处是<b>一条判据的两面</b>：跨过 = 上一帧那一刻正好往前一分钟（<see cref="Routine.Until"/>）。
    /// 「刚才那一帧是什么」由 <see cref="Routine.At"/> 答（纯算术、无状态），而「下一个到点还有几分钟」
    /// 由 <see cref="Routine.NextFire"/> 答 —— 触发用的时刻和那句话是<b>同一个出处</b>，
    /// 所以这一组不抄一份「几点该抬」的表（抄了就等于量那份副本）。</summary>
    public static List<string> RoutineFireSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        // 一张表：子夜（跨午夜的那条边界在 00:00）、凌晨、早晨、上午、跨夜。
        // 名字是这一组自己起的，判据里引的也是这几个字 —— 不引「用户会起的那种名字」。
        Settings Table()
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(0, 0), HM(0, 30), "子夜"));
            s.Blocks.Add(Row(HM(3, 0), HM(5, 0), "凌晨"));
            s.Blocks.Add(Row(HM(6, 0), HM(7, 0), "早晨"));
            s.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午"));
            s.Blocks.Add(Row(HM(23, 30), HM(0, 30), "跨夜"));
            return s;
        }

        // 抬起来的块叫什么 + 走的是哪一行休息流程。报的是这两个，不是「抬了」。
        string Shown(Scheduler m, Settings s) =>
            $"{(m.State == Phase.Shown ? "抬" : "没抬")}：" +
            $"{(m.BlockRaise == null ? "—" : m.BlockRaise.Name)}";

        checks.Check("跨过边界的那一分钟抬，前后两分钟都不抬", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            var start = HM(9, 0);
            // **对照（一定该通）**：那一刻之前的一个到点，算法自己说「还有 1 分钟」——
            // 触发和那句话同源；算法说别的而触发照抬，这一组量的就不是那件事了。
            var said = Routine.NextFire(s.Blocks, start - 1)
                       ?? throw new Exception($"算法在 {Routine.Clock(start - 1)} 算不出下一个到点");
            Expect(said.InMinutes == 1 && said.Block.Name == s.Blocks[3].Name,
                $"{Routine.Clock(start - 1)} 的下一个到点算成了「{said.Block.Name} 还有 {said.InMinutes} 分钟」");

            m.Step(0, Detected.Fake(input), s, start - 2);
            Expect(m.State == Phase.Hidden, $"{Routine.Clock(start - 2)} 那一块还没到就抬了");
            m.Step(500, Detected.Fake(input), s, start - 1);
            Expect(m.State == Phase.Hidden, $"到点前一分钟就抬了（{Shown(m, s)}）");
            m.Step(1_000, Detected.Fake(input), s, start);
            Expect(m.State == Phase.Shown, $"跨到 {Routine.Clock(start)} 没有抬（{Shown(m, s)}）");
            Expect(m.BlockRaise?.Name == s.Blocks[3].Name,
                $"抬起来的不是新进的那一块，是「{m.BlockRaise?.Name}」");
        });

        checks.Check("跨午夜的那一分钟也是到点（23:59 → 00:00）", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            // 「跨夜」盖住 23:59，「子夜」是 00:00 那一块 —— 到点正是午夜这一分钟。
            m.Step(0, Detected.Fake(input), s, HM(23, 59));
            Expect(m.State == Phase.Hidden, $"{Routine.Clock(HM(23, 59))} 就抬了");
            Expect(Routine.NextFire(s.Blocks, HM(23, 59)) is { InMinutes: 1 },
                $"算法说午夜不是下一个到点（{Routine.Clock(HM(23, 59))} 的下一个到点算成了别的）");
            m.Step(500, Detected.Fake(input), s, 0);
            Expect(m.State == Phase.Shown, $"跨过午夜没有抬（{Shown(m, s)}）");
            Expect(m.BlockRaise?.Name == s.Blocks[0].Name, $"午夜抬起来的不是「子夜」，是「{m.BlockRaise?.Name}」");
        });

        checks.Check("错过的到点不补：程序刚起来那一次采样不算到点（哪怕正好撞在起点上）", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            // 第一发：第一次采样就是 09:00 —— 那一块从 08:00 就开始了，
            // 对这一发进程来说时刻已经过去（用户还在用上一块），所以不补。
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(9, 0));
            Expect(m.State == Phase.Hidden, $"程序刚起来、正好撞在起点上就补抬了一次（{Shown(m, s)}）");

            // **对照（一定该通）**：同一张表，一分钟一分钟走过去，跨过去就抬 ——
            // 不然上面那条可能只是「这一组没抬起来」。
            var walked = new Scheduler();
            walked.Step(0, Detected.Fake(new FakeInput(0)), s, HM(8, 59));
            walked.Step(500, Detected.Fake(new FakeInput(0)), s, HM(9, 0));
            Expect(walked.State == Phase.Shown, "一分钟一分钟走过去，跨到 09:00 却没有抬 —— 对照不成立");
            Expect(walked.BlockRaise?.Name == s.Blocks[3].Name, "对照抬起来的不是新进的那一块");
        });

        checks.Check("机器睡过的那一跳不是到点（错过的几分钟、几小时都不补）", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(2, 59));
            Expect(m.State == Phase.Hidden, $"{Routine.Clock(HM(2, 59))} 那一块还没到就抬了");
            // 一觉睡到 06:00：醒来这一帧，**现实时间**跨了 181 分钟（中间三个到点全错过）。
            m.Step(181 * 60_000L, Detected.Fake(input), s, HM(6, 0));
            Expect(m.State == Phase.Hidden, $"机器睡了三个钟头，醒来把错过的到点补抬了（{Shown(m, s)}）");

            // **对照（一定该通）**：同一张表、同样从「到点前一分钟」出发，一分钟一分钟走过去 —— 抬。
            // 承重处是这一对实测结果只差一个「跳」：跳过去不补，一分钟一分钟走过去抬。
            var stepped = new Scheduler();
            var third = new FakeInput(0);
            stepped.Step(0, Detected.Fake(third), s, HM(5, 59));
            Expect(stepped.State == Phase.Hidden, $"{Routine.Clock(HM(5, 59))} 就抬了");
            stepped.Step(60_000, Detected.Fake(third), s, HM(6, 0));
            Expect(stepped.State == Phase.Shown, "一分钟一分钟走到 06:00 却没有抬 —— 对照不成立");
            Expect(stepped.BlockRaise?.Name == s.Blocks[2].Name, "对照抬起来的不是新进的那一块");
        });

        checks.Check("不是换块就不抬：还在同一行里、以及从一块走到「不在任何块里」，都不抬", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            // 「上午」是 09:00–17:00：16:59 → 17:00 那一刻**还在这一块里**（止那一刻归下一块，
            // 而下一块不在表里）—— 于是没有换块，也不该抬。
            m.Step(0, Detected.Fake(input), s, HM(16, 59));
            Expect(m.State == Phase.Hidden, $"{Routine.Clock(HM(16, 59))} 就抬了");
            m.Step(500, Detected.Fake(input), s, HM(17, 0));
            Expect(m.State == Phase.Hidden, $"走出「上午」就抬了（{Shown(m, s)}）");
            // **对照（一定该通）**：表上「上午」那一行的两端都归它 —— 这一发量的是
            // 「没有换块」，不是「这一刻不在任何块里」。
            if (Routine.At(s.Blocks, HM(16, 59))?.Name != s.Blocks[3].Name)
                throw new Exception($"{Routine.Clock(HM(16, 59))} 不在「上午」那一行里，对照不成立");
            if (Routine.At(s.Blocks, HM(17, 0)) != null)
                throw new Exception($"{Routine.Clock(HM(17, 0))} 还算在「上午」里，「止那一刻归下一块」没生效");
        });

        return checks.Report();
    }

    /// <summary>到点那一次抬<b>不是休息</b>：不进休息记录，健康度那一份不动。
    ///
    /// **「记录」那一侧量的是真家伙**（<see cref="History"/>，和 App 记账时用的是同一个类型）：
    /// 实测结果是休息记录和健康度，唯一的写入口是 <see cref="History.Push"/>。这一组量的是
    /// 「到点那一次前后，那两个实测结果一个字节都没动」，外加<b>敏感性对照</b>：推一条记录进去，
    /// 两个实测结果立刻就动 —— 不然「没动」可能只是「这两个数本来就不会动」。
    ///
    /// 而 App 那一侧写记录的唯一闸门是 <see cref="LastOutcome"/>（<c>App.CloseFaders</c> 里
    /// <c>if (_machine.LastOutcome is not (Outcome.Taken or Outcome.Skipped)) return;</c>
    /// 那一行）：到点那一次落下时它是「进入下个时间段」那一档，所以那一行就早退了。
    /// 判据把它钉住。</summary>
    public static List<string> RoutineNotABreakSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        Settings Table()
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午"));
            return s;
        }

        /// 走完一整轮「到点 → 遮罩走完 → 落」，把落那一刻的实测结果交出来。
        string Round(Settings s)
        {
            var m = new Scheduler();
            var input = new FakeInput(0);
            m.Step(0, Detected.Fake(input), s, HM(8, 59));
            m.Step(1_000, Detected.Fake(input), s, HM(9, 0));
            if (m.State != Phase.Shown) throw new Exception("到点那一发没抬起来，测的不是「那一次是不是休息」");
            // 走完：fadetop 那一行只有淡入本身（ms:0），所以由「提前结束休息」收尾 ——
            // 它和挥散同一条路（记一次取消），而落下去仍然不该记：`scored` 认的是
            // 「进入下个时间段」那一次，于是这一次交出的不是「休息过」也不是「跳过」。
            m.EndBreak(1_100, s);
            return $"记={ScoredText(m.LastOutcome)} 欠账={m.Skipped} " +
                   $"等={(m.NextAttemptMs - 1_100) / 1000} 秒 轮={m.PomodoroRounds}";
        }

        checks.Check("到点那一次落下时是「不记」，而连续输入那一次是「记」", () =>
        {
            var s = Table();
            var hint = Round(s);
            Expect(hint.StartsWith("记=不记", StringComparison.Ordinal),
                $"到点那一次落下时交出了 '{hint}' —— 该是「不记」，而它会走进 App 写记录的那一行");

            // **对照（一定该通）**：同一台状态机，连续输入那条触发举起来又落下的那一次交出
            // 「记=False」—— 不然上面那条可能只是「这个旗标压根没人写」。
            var input = new FakeInput(0);
            var m = new Scheduler();
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input), s, Routine.At(12, 0));
            Expect(m.State == Phase.Shown, "连续输入那条触发没抬起来 —— 对照不成立");
            input.Type(input.Now + 2_400);
            m.Step(input.Now, Detected.Fake(input), s, Routine.At(12, 0));
            Expect(m.LastOutcome == Outcome.Skipped, $"连续输入被挥散那一次交出了 {m.LastOutcome}，" +
                                            "该是「跳过」（被挥散要记成跳过，而那正是 App 会写一条记录的那种）");
            Expect(m.Skipped == 1, $"对照组那个欠账的计数是 {m.Skipped}");
        });

        checks.Check("到点那一次不动休息延迟那个欠账和下一次淡入的时刻", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            // 先攒一个已有的欠账：挥散过一次。
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input), s, Routine.At(12, 0));
            input.Type(input.Now + 2_400);
            m.Step(input.Now, Detected.Fake(input), s, Routine.At(12, 0));
            Expect(m.Skipped == 1, $"前置那一发没攒下欠账（欠账={m.Skipped}）");
            var wait = m.NextAttemptMs;
            // 到点 → 走完。
            m.EndBreak(input.Now, s);
            m.Step(input.Now + 1, Detected.Fake(input), s, HM(8, 59));
            m.Step(input.Now + 501, Detected.Fake(input), s, HM(9, 0));
            Expect(m.State == Phase.Shown, "到点那一发没抬起来");
            m.EndBreak(input.Now + 1_000, s);
            Expect(m.Skipped == 1, $"到点那一次把欠账动成了 {m.Skipped} —— 那是连续输入那条触发的账");
            Expect(m.NextAttemptMs == wait, $"到点那一次把下一次淡入推到了 {m.NextAttemptMs}（原 {wait}）");
        });

        checks.Check("记录和健康度：到点那一次前后逐字节相同，而推一条记录进去它们立刻就动", () =>
        {
            var s = Table();
            // **对照（一定该通）**：健康度是会动的 —— 推一条「跳过」，它立刻掉下来。
            var moved = new History();
            moved.Push(new History.Record { AtUnixMs = 1, Taken = true });
            var health = moved.HealthPercent();
            moved.Push(new History.Record { AtUnixMs = 2, Taken = false });
            if (moved.HealthPercent() >= health)
                throw new Exception($"推了一条「跳过」之后健康度还是 {moved.HealthPercent()}（原来 {health}）—— " +
                                    "这个实测结果不会动的话，下面那个「没动」是空的");
            // 记录本身照旧按先进先出摆着，最新的在末尾。
            if (moved.Records.Count != 2 || moved.Records[^1].AtUnixMs != 2)
                throw new Exception($"推了两条之后记录是 {string.Join(",", moved.Records.Select(r => r.AtUnixMs))}，" +
                                    "该是最新的排在末尾那条（第 2 毫秒那一条）");

            // 真跑一遍到点：这一次前后，那两样一个字节都不该动。
            // （休息足迹 2026-10-06 从界面上撤了，所以这一组不再量它 —— `History.Trail`
            //  连同那条「最新的排前面」的读法一起删掉了，界面上没人读它。
            //  健康度留着：记录照旧在写，它还是记录的一个读法，这一组继续用它。）
            var history = new History();
            history.Push(new History.Record { AtUnixMs = 1, Taken = true });
            var before = Measurement(history);
            Round(s);
            var after = Measurement(history);
            if (before != after) Fail("到点那一次动了健康度 / 记录", before, after);
        });

        return checks.Report();

        // 健康度 + 记录条数 + 最新那一条，一个字符串 —— 逐字节比的是这三样，不是一样。
        static string Measurement(History h) =>
            $"健康={h.HealthPercent()} 条数={h.Records.Count} 最新={(h.Records.Count > 0 ? h.Records[^1].AtUnixMs : 0)}";
    }

    /// <summary>到点那一次抬受两把钥匙管：<c>autoFade</c> 和 <c>blockWhenFullscreen</c>。
    /// 两把都是<b>只挡、不留记号</b>，所以让开之后也不补（「时刻过了就是过了」）。
    ///
    /// 承重的对照是<b>番茄钟那一轮在同一把钥匙下的反面</b>：它的到期时刻从不被推后，
    /// 所以全屏让开之后照抬 —— 两条规则各自一条，自检里对着核（判据说「不一样」，
    /// 只有两边都量了才不是空话）。</summary>
    public static List<string> RoutineGuardSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        Settings Table()
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午"));
            return s;
        }

        checks.Check("autoFade 关着：到点不抬，而过后把它拧回来也不补", () =>
        {
            var s = Table();
            s.AutoFade = false;
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(8, 59));
            m.Step(1_000, Detected.Fake(input), s, HM(9, 0));
            Expect(m.State == Phase.Hidden, $"autoFade 关着，到点还是抬了（{m.BlockRaise?.Name}）");
            // 拧回来，再过两分钟：时刻已经过去，不补。
            s.AutoFade = true;
            m.Step(121_000, Detected.Fake(input), s, HM(9, 2));
            Expect(m.State == Phase.Hidden, "autoFade 拧回来之后把错过的到点补抬了");
            // **对照（一定该通）**：同一张表、autoFade 开着，跨到 09:00 就抬 ——
            // 不然上面那些可能只是「这一组压根没抬起来」。
            var open = Table();
            var other = new FakeInput(0);
            var ok = new Scheduler();
            ok.Step(0, Detected.Fake(other), open, HM(8, 59));
            ok.Step(1_000, Detected.Fake(other), open, HM(9, 0));
            Expect(ok.State == Phase.Shown, "autoFade 开着，跨到 09:00 却没有抬 —— 对照不成立");
        });

        checks.Check("撞上独占全屏：到点不抬，让开之后也不补（而番茄钟那一轮让开就抬）", () =>
        {
            var s = Table();
            s.BlockWhenFullscreen = true;
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(8, 59));
            m.Step(1_000, Detected.Fake(input, ownsScreen: true), s, HM(9, 0));
            Expect(m.State == Phase.Hidden, $"别的程序占着整块屏，到点还是抬了（{m.BlockRaise?.Name}）");
            // 让开：时刻已经过去，不补。
            m.Step(1_500, Detected.Fake(input), s, HM(9, 0));
            Expect(m.State == Phase.Hidden, "全屏一让就把错过的到点补抬了");
            m.Step(121_000, Detected.Fake(input), s, HM(9, 2));
            Expect(m.State == Phase.Hidden, "过了两分钟还是补抬了");

            // **对照一（一定该通）**：同一张表、同一段分钟，只是不撞全屏 —— 抬。
            var clear = Table();
            clear.BlockWhenFullscreen = true;
            var ok = new Scheduler();
            var okInput = new FakeInput(0);
            ok.Step(0, Detected.Fake(okInput), clear, HM(8, 59));
            ok.Step(1_000, Detected.Fake(okInput), clear, HM(9, 0));
            Expect(ok.State == Phase.Shown, "不撞全屏时到点没有抬 —— 对照不成立");

            // **对照二（那两条规则确实不一样）**：番茄钟那一轮在同一把钥匙下让开就抬 ——
            // 它的到期时刻是现实时间定的，从不被推后。
            var pomo = Table();
            pomo.BlockWhenFullscreen = true;
            var pm = new Scheduler();
            var pmInput = new FakeInput(0);
            pm.TogglePomodoro(0, pomo);
            var due = pm.PomodoroDueMs;
            pm.Step(due, Detected.Fake(pmInput, ownsScreen: true), pomo, HM(9, 0));
            Expect(pm.State == Phase.Hidden, "独占全屏时番茄钟那一轮抬了 —— 这一发量的是那两条规则不一样");
            Expect(pm.PomodoroDueMs == due, $"避让把番茄钟的到期推到了 {pm.PomodoroDueMs}（原 {due}）");
            pm.Step(due + 1, Detected.Fake(pmInput), pomo, HM(9, 0));
            Expect(pm.State == Phase.Shown, "全屏一让，番茄钟那一轮没有抬 —— 「全屏一让就抬」这条不成立");
        });

        checks.Check("这一块自己写「不抬」：那一块到点就没有遮罩（逐块写，不从种类推）", () =>
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午", fade: false));
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(8, 59));
            m.Step(1_000, Detected.Fake(input), s, HM(9, 0));
            Expect(m.State == Phase.Hidden, "那一块写着「到点不抬遮罩」，遮罩还是起来了");
            // **对照（一定该通）**：键名和它的默认值问声明表 —— 判据里不另抄一份「默认抬」，
            // 也不手搜文档里的字：量的是「声明表默认 true，而这一行写的是 false」。
            var decl = Settings.Decl(TimeBlock.KeyPrefix + "fade");
            if (decl.Default is not true)
                throw new Exception($"声明表里 {TimeBlock.KeyPrefix}fade 的默认值是 {decl.Default}，该是 true");
            if (!new TimeBlock().Fade)
                throw new Exception("没写这一格的新块默认是「到点不抬」，而它的声明默认值是 true");
            if (Row(0, 1, "x", fade: true).Fade != true)
                throw new Exception("显式写 true 的那一行没有读成 true");
        });

        checks.Check("这一块自己写「让位」：连续输入那条提醒在这一块里不插进来", () =>
        {
            // 逐块写的那一条（设计文档 `docs/design/daily-routine.md`同一节：「连续输入提醒在这一块里让不让位」是块里的键）：
            // 默认 true 就是「让位」，要「专注那一块里不被打碎」就在某一行里写 false。
            // **不从种类推** —— 对照那一发就是同样写着「专注」而让位键没摘掉的那一行。
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午", "focus", yields: false));
            var input = new FakeInput(0);
            var m = new Scheduler();
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input), s, HM(10, 0));
            Expect(m.State == Phase.Hidden, "这一块写着「输入提醒让位」，连续输入那条触发还是插进来了");

            // **对照（一定该通）**：同一块、同一个种类，只把让位键摘掉（回到声明表的默认 true）—— 抬。
            var open = RoutineFast();
            open.Blocks.Add(Row(HM(9, 0), HM(17, 0), "上午", "focus"));
            var other = new FakeInput(0);
            var ok = new Scheduler();
            other.Type(70_000);
            other.Advance(1_500);
            ok.Step(other.Now, Detected.Fake(other), open, HM(10, 0));
            Expect(ok.State == Phase.Shown, "让位键摘掉之后同一发不抬 —— 对照不成立");
            // 键名和默认值问声明表。
            var decl = Settings.Decl(TimeBlock.KeyPrefix + "yieldsToInput");
            if (decl.Default is not true)
                throw new Exception($"声明表里 {TimeBlock.KeyPrefix}yieldsToInput 的默认值是 {decl.Default}，该是 true");
            // **总开关关着 → 让位键也不参与**（它是这一族的一个键，和到点那一次一样）。
            open.RoutineEnabled = false;
            var off = new FakeInput(0);
            var quiet = new Scheduler();
            off.Type(70_000);
            off.Advance(1_500);
            quiet.Step(off.Now, Detected.Fake(off), open, HM(10, 0));
            Expect(quiet.State == Phase.Shown, "总开关关着，表里的让位键还在拦着 —— 那一族该整个不参与");
        });

        return checks.Report();
    }

    /// <summary>「睡眠」那一块的五档静默，外加「块走完恢复」。
    ///
    /// **静默是种类语义**（<see cref="Settings.BlockKindSleep"/> 是唯一带行为语义的取值，
    /// 取向在 ADR-0005），而<b>别的行为逐块写</b>：这一族里凡「块内过法」的判据都指向
    /// 那一行的键，不指向种类。
    ///
    /// 承重处是<b>番茄钟的钟照跑</b>：静默的那一轮不抬，可它的到期时刻一个字不动
    /// （<see cref="PomodoroDueMs"/>），所以块走完之后那一轮照抬 —— 和「全屏一让就抬」同一条款，
    /// 自检里量的是同一个字段。</summary>
    public static List<string> SleepSilenceSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        const string Sleep = Settings.BlockKindSleep;

        /// 睡 22:00–06:00、上午 06:00–18:00：两段首尾相接，中间没有缝 ——
        /// 于是「块走完」和「进新块」落在同一分钟，那一分钟是静默结束的交接。
        Settings Table(string sleepKind = Sleep)
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(22, 0), HM(6, 0), "夜", sleepKind));
            s.Blocks.Add(Row(HM(6, 0), HM(18, 0), "上午"));
            return s;
        }

        checks.Check("静默之一：自动淡入在睡眠那一块里不抬", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, $"人在睡眠那一块里连续用了一分钟，遮罩还是起来了");

            // **对照（一定该通）**：同一张表、同一串输入，只把那一行的种类摘掉 —— 抬。
            var awake = Table("");
            var other = new FakeInput(0);
            var ok = new Scheduler();
            other.Type(70_000);
            other.Advance(1_500);
            ok.Step(other.Now, Detected.Fake(other), awake, HM(2, 0));
            Expect(ok.State == Phase.Shown, "种类摘掉之后同一发不抬 —— 对照不成立，说明量的是那一行");
        });

        checks.Check("静默之二：番茄钟那一轮在睡眠那一块里不抬，而它的钟照跑", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.TogglePomodoro(0, s);
            var due = m.PomodoroDueMs;
            Expect(due > 0, "番茄钟压根没开起来");
            m.Step(due, Detected.Fake(input), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, $"专注段到点时人在睡眠那一块里，遮罩还是起来了");
            Expect(m.PomodoroDueMs == due, $"静默把到期时刻推到了 {m.PomodoroDueMs}（原 {due}）—— " +
                                           "「钟照跑」就是这一个数不动");
            // **对照（一定该通）**：同一张表、同一段专注段，在白天那一块里到点 —— 抬。
            var day = Table();
            var dayInput = new FakeInput(0);
            var ok = new Scheduler();
            ok.TogglePomodoro(0, day);
            ok.Step(ok.PomodoroDueMs, Detected.Fake(dayInput), day, HM(9, 0));
            Expect(ok.State == Phase.Shown, "白天那一块里番茄钟到点不抬 —— 对照不成立");
        });

        checks.Check("静默之三：睡眠那一块里，别的块的到点也不抬", () =>
        {
            // 「里面」这一块 05:00 开始，而它整个落在睡眠那一块里（靠前的那一行赢）。
            var s = Table();
            s.Blocks.Add(Row(HM(5, 0), HM(5, 30), "里面"));
            var input = new FakeInput(0);
            var m = new Scheduler();
            Expect(Routine.At(s.Blocks, HM(5, 0))?.Name != s.Blocks[2].Name,
                "「里面」那一块在这一刻生效 —— 这一发量的就不是「落在睡眠那一块里的到点」");
            m.Step(0, Detected.Fake(input), s, HM(4, 59));
            m.Step(1_000, Detected.Fake(input), s, HM(5, 0));
            Expect(m.State == Phase.Hidden, "睡眠那一块里，别的块到点了还是抬起了遮罩");

            // **对照（一定该通）**：一张只差「睡眠那一块止在哪一刻」的表 —— 把夜缩到 04:00，
            // 05:00 就成了一次真的换块，遮罩抬起来。量的是「那一次到点抬不抬」。
            var open = Table();
            open.Blocks.Add(Row(HM(5, 0), HM(5, 30), "里面"));
            open.Blocks[0].EndMinute = HM(4, 0);
            var other = new FakeInput(0);
            var ok = new Scheduler();
            ok.Step(0, Detected.Fake(other), open, HM(4, 59));
            ok.Step(1_000, Detected.Fake(other), open, HM(5, 0));
            Expect(ok.State == Phase.Shown, "夜缩到 04:00 之后 05:00 那一发不抬 —— 对照不成立");

            // **另一半（进睡眠那一块自己的那一刻）**：那一块从 22:00 开始，而 22:00 已经在
            // 睡眠那一块里，所以不抬；把种类摘掉就抬。判据数的是「那一次抬不抬」。
            var into = Table();
            var intoInput = new FakeInput(0);
            var quiet = new Scheduler();
            quiet.Step(0, Detected.Fake(intoInput), into, HM(21, 59));
            quiet.Step(1_000, Detected.Fake(intoInput), into, HM(22, 0));
            Expect(quiet.State == Phase.Hidden, "进睡眠那一块的那一刻把遮罩抬起来了");
            var awake = Table("");
            var awakeInput = new FakeInput(0);
            var loud = new Scheduler();
            loud.Step(0, Detected.Fake(awakeInput), awake, HM(21, 59));
            loud.Step(1_000, Detected.Fake(awakeInput), awake, HM(22, 0));
            Expect(loud.State == Phase.Shown, "摘掉种类之后进「夜」那一块不抬 —— 对照不成立");
        });

        checks.Check("恢复：块走完那一刻起，两条触发都回来", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input), s, HM(5, 59));
            Expect(m.State == Phase.Hidden, $"静默那一块里 {Routine.Clock(HM(5, 59))} 就抬了");
            // 06:00：夜走完、上午开始 —— 那一刻已经在睡眠那一块外面了，于是抬起。
            m.Step(1_000, Detected.Fake(input), s, HM(6, 0));
            Expect(m.State == Phase.Shown, $"块走完了，到点却没有抬（{m.BlockRaise?.Name}）");
            Expect(m.BlockRaise?.Name == s.Blocks[1].Name,
                $"块走完抬起来的不是新进的那一块，是「{m.BlockRaise?.Name}」");

            // 连续输入那条触发也回来了：这一轮落掉之后重新攒够 + 一个空档。
            m.EndBreak(2_000, s);
            var later = new FakeInput(0);
            later.Type(72_000);
            later.Advance(1_500);
            m.Step(later.Now, Detected.Fake(later), s, HM(6, 1));
            Expect(m.State == Phase.Shown, "块走完之后连续输入那条触发还没回来");
            // **对照（一定该通）**：块里那一发同样的输入不抬（静默之一量过，这里是它的另一半）。
            var quiet = Table();
            var quietInput = new FakeInput(0);
            var silent = new Scheduler();
            quietInput.Type(72_000);
            quietInput.Advance(1_500);
            silent.Step(quietInput.Now, Detected.Fake(quietInput), quiet, HM(2, 0));
            Expect(silent.State == Phase.Hidden, "对照不成立：睡眠那一块里同一串输入抬了");
        });

        checks.Check("静默之四：到点没抬的那一轮，块走完之后补抬（和「全屏一让就抬」同一条款）", () =>
        {
            var s = Table();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.TogglePomodoro(0, s);
            var due = m.PomodoroDueMs;
            m.Step(due, Detected.Fake(input), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, "睡眠那一块里番茄钟那一轮抬了");
            // 块走完（06:00 那一帧）：钟早过了，于是照抬 —— 走的仍是 pomodoro 那一行。
            m.Step(due + 1, Detected.Fake(input), s, HM(6, 0));
            Expect(m.State == Phase.Shown, "块走完之后那一轮没有补抬");
            Expect(m.PomodoroRound, "补抬起来的不是番茄钟那一轮");
            Expect(m.Plan.Count == s.PomodoroSteps().Count,
                $"补抬走的是 {m.Plan.Count} 步，番茄钟那一行该是 {s.PomodoroSteps().Count} 步");
            Expect(m.BlockRaise == null, "补抬起来的这一次被记成了块到点");
            // **对照（一定该通）**：走的是 pomodoro 那一行，不是「这次休息走哪一条」指着的那一行。
            var raised = m.Plan[0];
            s.Flow = Settings.FlowTodoList;
            Expect(!ReferenceEquals(raised, s.Steps()[0]),
                "番茄钟那一轮和 flow 指着的那一行是同一条 —— 「同一条款」量的是它俩不是一条路");
        });

        return checks.Report();
    }

    /// <summary>整张作息表关掉：输入触发和番茄钟<b>逐字节</b>回到现状。
    ///
    /// 「回到现状」量的是<b>两段实测结果逐字节相同</b> —— 一段是「有一张表但总开关关着」，
    /// 一段是「压根没有表」。散着的几个断言会漏掉「某几个数对了、另一个数变了」这一种，
    /// 而那一種正是漏判之后最难查的。
    ///
    /// 对照：同一张表把总开关拧开 —— 那一段实测结果<b>必须不一样</b>（表里那一块盖住整天的睡眠），
    /// 不然上面那个「相同」可能只是「这一组压根什么都没量」。</summary>
    public static List<string> RoutineSwitchSelfTest()
    {
        var checks = new Checks();

        Settings Switched()
        {
            var s = RoutineFast();
            s.Blocks.Add(Row(HM(0, 0), HM(0, 0), "整天", Settings.BlockKindSleep));
            return s;
        }

        checks.Check("总开关关掉：连续输入和番茄钟那两段实测结果逐字节回到现状", () =>
        {
            var off = Switched();
            off.RoutineEnabled = false;
            var none = RoutineFast();          // 压根没有表
            var withTable = Switched();        // 同一张表，总开关拧开
            withTable.RoutineEnabled = true;

            var a = Transcript(off);
            var b = Transcript(none);
            if (a != b) Fail("总开关关掉之后实测结果没有逐字节回到现状（对着一个压根没有表的）", b, a);

            // **对照（一定该通）**：同一张表把开关拧开，那一段实测结果必须变 ——
            // 表里那一块盖住一整天且种类是睡眠，所以两条触发都被静默掉了。
            var c = Transcript(withTable);
            if (c == a) throw new Exception("总开关拧开之后实测结果和关着的时候一模一样 —— 对照不成立");
            if (c.Contains("抬 1/1", StringComparison.Ordinal))
                throw new Exception("总开关拧开、整张表都是睡眠那一块，那一段实测结果里却仍有一次淡入");
        });

        checks.Check("总开关是唯一一个「这张表要不要用」的地方：关掉它托盘那一行也不出现", () =>
        {
            // 判据量的是接线：托盘那一行的名字从 `Tray.BlockRowName` 问（App 走的是同一个方法），
            // 而它先认总开关。
            var s = Switched();
            var minute = HM(3, 0);
            if (Tray.BlockRowName(s, minute) != s.Blocks[0].Name)
                throw new Exception("拧开的时候托盘那一行没有块名");
            s.RoutineEnabled = false;
            if (Tray.BlockRowName(s, minute) != null)
                throw new Exception($"总开关关着，托盘那一行还显示「{Tray.BlockRowName(s, minute)}」");
            // **对照（一定该通）**：不在任何块里的那一刻，本来就没有名字 ——
            // 不然上面那条可能只是「这个问句永远答 null」。
            var open = Switched();
            if (Tray.BlockRowName(open, HM(3, 0)) == null) throw new Exception("对照不成立：拧开时也问不出块名");
        });

        return checks.Report();
    }

    /// <summary>勿扰那一族的静默：<b>三触发不抬、补抬、到点不补、预览照常、在台走完</b>，
    /// 外加托盘上那一行在不在。
    ///
    /// <b>判据是「镜像睡眠那一族」，不是「另一族新规矩」</b>（设计文档 `docs/design/dnd.md`「勿扰是用户定义的静默规则」
    /// 那一节：「静默语义镜像「睡眠」那一块」）：所以这里逐条对着
    /// <see cref="SleepSilenceSelfTest"/> 的那几条量，每一条都配一个<b>对照</b> ——
    /// 同一份表、同一串输入、同一段专注段，只把**勿扰那一格**换掉（关掉它 / 挪出那一段）。
    ///
    /// <b>睡眠和勿扰是两件不同的事，各自一套判据</b>：睡眠是「块这一行的种类写着什么」，
    /// 勿扰是「时段表或名单此刻成不成立」（<see cref="Dnd"/>）。这一组量的就是
    /// <b>后者不碰前者</b>：表里一个「睡眠」块都没有、只配一条勿扰时段，三条触发照样被挡；
    /// 反过来只摆一个「睡眠」块、勿扰表是空的，行为和今天一模一样。
    ///
    /// <b>纯算术</b>：假时钟 + 状态机 + 在内存里造的表，不碰磁盘也不碰 %APPDATA%。
    /// 时刻一律由 <see cref="HM"/> 算出来，星期几是 <see cref="DateTime"/> 自己的取值。</summary>
    public static List<string> DndSilenceSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        /// 一份快设置 + <b>一条覆盖全天的勿扰时段</b>（从 00:00 到 23:59，七天都勾）。
        /// 「全天」是为了让判据只量「勿扰挡不挡」，而星期几和跨午夜那一族在
        /// <see cref="Dnd.ArithmeticSelfTest"/> 里量。
        Settings Quiet()
        {
            var s = RoutineFast();
            s.DndWindows.Add(new DndWindow
            {
                StartMinute = HM(0, 0), EndMinute = HM(23, 59), Weekdays = [.. Dnd.WeekdayKeys],
            });
            return s;
        }

        /// 同一份表，**只把勿扰那一格关掉** —— 全部对照都从它来。
        Settings Loud()
        {
            var s = Quiet();
            s.DndEnabled = false;
            return s;
        }

        /// 「勿扰中」那一发实测结果：走的是求值那一族，聚合里的那一格和 App 装的是同一个。
        Dnd.Measurement On() => new(true, null, "chrome.exe");

        checks.Check("静默之一：自动淡入在勿扰里不抬", () =>
        {
            var s = Quiet();
            var input = new FakeInput(0);
            var m = new Scheduler();
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, "勿扰中连续用了一分钟，遮罩还是起来了");

            // **对照（一定该通）**：同一份表、同一串输入，只把勿扰关掉 —— 抬。
            // 对照要连表都不换，才量得出「是勿扰那一格在挡」。
            var open = Loud();
            var other = new FakeInput(0);
            var ok = new Scheduler();
            other.Type(70_000);
            other.Advance(1_500);
            ok.Step(other.Now, Detected.Fake(other), open, HM(2, 0));
            Expect(ok.State == Phase.Shown, "关掉勿扰之后同一发不抬 —— 对照不成立，说明量的是那一格");
        });

        checks.Check("静默之二：番茄钟那一轮在勿扰里不抬，而它的钟照跑", () =>
        {
            var s = Quiet();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.TogglePomodoro(0, s);
            var due = m.PomodoroDueMs;
            Expect(due > 0, "番茄钟压根没开起来");
            m.Step(due, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, "专注段到点时勿扰中，遮罩还是起来了");
            Expect(m.PomodoroDueMs == due, $"静默把到期时刻推到了 {m.PomodoroDueMs}（原 {due}）—— " +
                                           "「钟照跑」就是这一个数不动");
            // **对照（一定该通）**：同一份表、同一段专注段，只把勿扰关掉 —— 到点就抬。
            var open = Loud();
            var openInput = new FakeInput(0);
            var ok = new Scheduler();
            ok.TogglePomodoro(0, open);
            ok.Step(ok.PomodoroDueMs, Detected.Fake(openInput), open, HM(2, 0));
            Expect(ok.State == Phase.Shown, "关掉勿扰之后番茄钟到点不抬 —— 对照不成立");
        });

        checks.Check("静默之三：勿扰里，别的块到点也不抬，而错过的那一次不补", () =>
        {
            // 「里面」那一块 05:00 开始；勿扰时段盖住整天，所以那一刻它在勿扰里。
            var s = Quiet();
            s.Blocks.Add(Row(HM(5, 0), HM(5, 30), "里面"));
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.Step(0, Detected.Fake(input, dnd: On()), s, HM(4, 59));
            m.Step(1_000, Detected.Fake(input, dnd: On()), s, HM(5, 0));
            Expect(m.State == Phase.Hidden, "勿扰中，别的块到点了还是抬起了遮罩");

            // **「错过不补」那一半**：勿扰继续，下一帧越过了 05:00 —— 到点只在**当下那一刻**发，
            // 所以这一发仍然不抬，而且**后面再也不补**（不记、不排队、不顺延）。
            m.Step(2_000, Detected.Fake(input, dnd: On()), s, HM(5, 1));
            m.Step(3_000, Detected.Fake(input, dnd: On()), s, HM(5, 2));
            Expect(m.State == Phase.Hidden, "勿扰结束前，已经错过的那一次到点被补抬了");

            // **对照（一定该通）**：同一份表、同样两帧，只把勿扰关掉 —— 05:00 那一发抬。
            var open = Loud();
            open.Blocks.Add(Row(HM(5, 0), HM(5, 30), "里面"));
            var openInput = new FakeInput(0);
            var ok = new Scheduler();
            ok.Step(0, Detected.Fake(openInput), open, HM(4, 59));
            ok.Step(1_000, Detected.Fake(openInput), open, HM(5, 0));
            Expect(ok.State == Phase.Shown, "关掉勿扰之后 05:00 那一发不抬 —— 对照不成立");
        });

        checks.Check("补抬：勿扰结束的那一刻起，欠的那一轮照抬，走的仍是 pomodoro 那一行", () =>
        {
            var s = Quiet();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.TogglePomodoro(0, s);
            var due = m.PomodoroDueMs;
            m.Step(due, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            Expect(m.State == Phase.Hidden, "勿扰中番茄钟那一轮抬了");
            // 勿扰结束（下一帧那一格是 off）：钟早过了，于是照抬 —— 和「全屏一让就抬」同一条款。
            m.Step(due + 1, Detected.Fake(input), s, HM(2, 1));
            Expect(m.State == Phase.Shown, "勿扰结束之后那一轮没有补抬");
            Expect(m.PomodoroRound, "补抬起来的不是番茄钟那一轮");
            Expect(m.Plan.Count == s.PomodoroSteps().Count,
                $"补抬走的是 {m.Plan.Count} 步，番茄钟那一行该是 {s.PomodoroSteps().Count} 步");
            Expect(m.BlockRaise == null, "补抬起来的这一次被记成了块到点");
            // **「到期时刻不顺延」那一半**：欠的那一轮是原来那个到期时刻欠的，
            // 补抬之后下一轮的钟才重新起算 —— 量的是补抬之前那个数一个字节都没动（上面量过），
            // 这里量的是**连续两帧都还在勿扰里时它一直不动**。
            var still = new Scheduler();
            still.TogglePomodoro(0, s);
            var held = still.PomodoroDueMs;
            still.Step(held, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            still.Step(held + 5_000, Detected.Fake(input, dnd: On()), s, HM(2, 1));
            Expect(still.PomodoroDueMs == held,
                $"勿扰连着好几帧，到期时刻变成了 {still.PomodoroDueMs}（原 {held}）");
        });

        checks.Check("手动预览不受管：托盘那一下在勿扰里照样抬起来", () =>
        {
            var s = Quiet();
            var input = new FakeInput(0);
            var m = new Scheduler();
            m.RaisePreview(0, s);
            m.Step(0, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            Expect(m.State == Phase.Shown,
                "勿扰中手动的预览没有抬起来 —— 预览是明确的一次要求，勿扰管不到");
            // **对照（一定该通）**：不是「什么都抬」—— 预览抬着的时候它**不记账**
            //（`scored` 认的就是这个旗标），而这一族一个字都没改。
            m.EndBreak(1_000, s);
            Expect(m.LastOutcome == Outcome.Preview, "预览落下被记成一次真休息了 —— 预览那一条被碰坏了");
        });

        checks.Check("遮罩已经在台上时勿扰开始：不清台，照常走完", () =>
        {
            var s = Quiet();
            var input = new FakeInput(0);
            var m = new Scheduler();
            // 先在勿扰之外抬起来（预览是最短的那条路，不用真等一分钟）。
            m.RaisePreview(0, s);
            Expect(m.State == Phase.Shown, "前提不成立：遮罩压根没抬起来");
            // 勿扰开始：聚合里那一格翻成 on，而状态机在 Shown 分支上**一个字节都不动它**。
            m.Step(1_000, Detected.Fake(input, dnd: On()), s, HM(2, 0));
            Expect(m.State == Phase.Shown, "勿扰一开始就把已经在台上的遮罩收了 —— 在台不清台");
            Expect(m.Plan.Count > 0, "台上的那一轮被清空了（休息流程没了）");
            // **走完**：它照常按自己那一行走完，落下来时也不因为刚才在勿扰里而多记什么。
            m.EndBreak(2_000, s);
            Expect(m.State == Phase.Hidden, "走完之后状态还是 Shown");
        });

        checks.Check("托盘那一行：成立时带着那半句（因为哪一条），把总开关关掉之后项数差一", () =>
        {
            // 菜单是用 `Tray.Build` 造的**同一棵**树（`Tray.ReadRows` 就是那一族用的尺子），
            // 所以这里判过的就是产品跑的那一棵。语言表没装上，所以 `tr` 是恒等 —— 那一行上
            // 摆的就是键（中文原文）加原因，判据只对着**形状**和**原因里的数据**。
            var tr = new Func<string, string>(s => s);
            var fore = new Foreground.Measurement("chrome.exe", @"C:\x\chrome.exe", null);
            var when = new DateTime(2026, 10, 4, 22, 30, 0);   // 周日夜里
            var label = tr("勿扰中");

            // **因为时段那一档**：一条盖住整天、七天都勾的时段。
            var byTime = Quiet();
            var timeWhy = Tray.DndWhy(byTime, when, fore);
            var timeRow = RowOf(() => timeWhy, tr, label);
            // **因为名单那一档**：时段表是空的、名单上有那个 exe。
            var byApp = new Settings();
            byApp.DndApps.Add(fore.Exe!);
            var appWhy = Tray.DndWhy(byApp, when, fore);
            var appRow = RowOf(() => appWhy, tr, label);

            // 那一行的形状：**标签 + 两个空格 + 那半句**（和时间块、番茄钟那两行同一种写法），
            // 而「有半句」不是「标签后面空着」——空着的那一行和没有那一行是两件事。
            Expect(timeRow == $"{label}  {timeWhy}",
                $"菜单上那一行是 '{timeRow}'，该是「{label}  {timeWhy}」");
            Expect(timeRow.Length > label.Length + 2, $"那一行是 '{timeRow}' —— 标签后面没有那半句");
            // **两种「因为」说出来的话必须不一样**（表里没装语言表也判得了）：
            // 接反了或者「因为」那一半根本没接，两档会印出同一句话。
            Expect(appRow == $"{label}  {appWhy}" && appWhy != timeWhy,
                $"两档印出来的是同一句（'{timeRow}'）—— 「因为哪一条」没有接上");
            // **对照（一定该通）**：两档的「因为」在实测结果那一层是真的两件事（一条时段 / 名单里
            // 那个 exe 名）—— 不然上面那句话可能只是两句话碰巧不同。
            var timeHit = Dnd.Of(byTime, when, fore);
            var appHit = Dnd.Of(byApp, when, fore);
            Expect(timeHit is { On: true, Window: not null, Exe: null },
                $"时段那一档的实测结果是 {timeHit.Window?.StartMinute}/{timeHit.Exe} —— 「因为」不是那条时段");
            Expect(appHit is { On: true, Window: null, Exe: not null },
                $"名单那一档的实测结果不是名单里那个 exe（{appHit.Exe}）");

            // **项数**：把总开关关掉之后少一行（不是少半句）—— 两个「因为」各量一遍。
            // 两份设置**各造各的**（不是同一份拧一下）：同一个引用拧了两次，两次读到的
            // 就都是拧完的那一个，量出来「差的是零」。
            foreach (var (what, make) in new (string, Func<Settings>)[]
                     {
                         ("时段", () => Quiet()),
                         ("名单", () => { var s = new Settings(); s.DndApps.Add(fore.Exe!); return s; }),
                     })
            {
                var on = make();
                var off = make();
                off.DndEnabled = false;
                var lit = Tray.ReadRows(Hooks(() => Tray.DndWhy(on, when, fore)), tr);
                var quiet = Tray.ReadRows(Hooks(() => Tray.DndWhy(off, when, fore)), tr);
                Expect(quiet.Count == lit.Count - 1,
                    $"{what}那一档：关掉总开关时菜单上有 {quiet.Count} 项，成立时有 {lit.Count} 项 —— 差的不是一");
                Expect(!quiet.Contains(label, StringComparer.Ordinal),
                    $"{what}那一档：关掉总开关时还留着一个空的「{label}」");
            }
            // **对照（一定该通）**：总开关开着而此刻不在任何一段、也不在名单上 ——
            // 同样不出现（总开关是「这一族要不要用」，拧开它不能凭空多出一行）。
            var miss = Quiet();
            miss.DndWindows[0].Weekdays = ["mon"];
            var none = Tray.ReadRows(Hooks(() => Tray.DndWhy(miss, when, fore)), tr);
            var elsewhere = Tray.ReadRows(Hooks(() => Tray.DndWhy(miss, when,
                new Foreground.Measurement("notepad.exe", @"C:\x\notepad.exe", null))), tr);
            Expect(none.Count == elsewhere.Count,
                $"勾的是周一、而那一刻是周日，菜单上有 {none.Count} 项，该是 {elsewhere.Count} 项");

            string RowOf(Func<string?> why, Func<string, string> tr2, string at)
            {
                var texts = Tray.ReadRows(Hooks(why), tr2);
                var found = texts.FirstOrDefault(t => t.StartsWith(at, StringComparison.Ordinal));
                if (found == null)
                    throw new Exception($"菜单上没有那一行（读回来的是：{string.Join(" / ", texts)}）");
                return found;
            }

            Tray.TrayHooks Hooks(Func<string?> why) => new(
                Show: () => { }, Calendar: () => { }, Auto: () => { }, Options: () => { },
                About: () => { }, Exit: () => { }, SecondLaunch: () => { }, Pomodoro: () => { },
                Autostart: () => { },
History: () => { },
                SwitchLifeMode: _ => { }, ManageLifeModes: () => { },
                AutoFadeOn: () => false, PreviewOn: () => false, CalendarOn: () => false,
                PomodoroOn: () => false, AutostartOn: () => false, PomodoroLeftMs: () => 0,
                LifeModes: () => new Tray.LifeModeList([], null, false),
                // 「时间块」那一行不在（在不在是 routine-tray 那一组量的），
                // 而这里要量的就是「勿扰中」那一行。「下次休息还有多久」那一行同理
                // （在不在是 tray-break 量的）—— 上面那几发比的是项数差一，
                // 让它在就等于把分母改了。
                BlockName: () => null, DndWhy: why, BreakLeftMs: () => null, Today: () => new Tray.TodayList([], false));
        });

        checks.Check("睡眠和勿扰是两件不同的事，各自一套判据", () =>
        {
            // 只有睡眠那一块、勿扰表是空的：三条触发照旧被睡眠挡着（那是它自己的判据）。
            var sleep = RoutineFast();
            sleep.Blocks.Add(Row(HM(22, 0), HM(6, 0), "夜", Settings.BlockKindSleep));
            var input = new FakeInput(0);
            var m = new Scheduler();
            input.Type(70_000);
            input.Advance(1_500);
            m.Step(input.Now, Detected.Fake(input), sleep, HM(2, 0));
            Expect(m.State == Phase.Hidden, "一个「睡眠」块在凌晨没有静默 —— 睡眠那一族被碰坏了");

            // 反过来：只配一条勿扰时段、表里一个时间块都没有 —— 同样被挡，
            // 而这一条挡它的判据和上面那条**不是同一条**（一个是种类，一个是配置求值）。
            var dnd = Quiet();
            Expect(dnd.Blocks.Count == 0, "这一份里有一个时间块 —— 量的就不是「只靠勿扰挡着」");
            var other = new FakeInput(0);
            var ok = new Scheduler();
            other.Type(70_000);
            other.Advance(1_500);
            ok.Step(other.Now, Detected.Fake(other, dnd: On()), dnd, HM(2, 0));
            Expect(ok.State == Phase.Hidden, "一个时间块都没有，而勿扰没有挡住");

            // **对照（一定该通）**：同一条勿扰时段、同一串输入，聚合里那一格关掉就抬 ——
            // 所以刚才那两条挡住的确实是它，而不是「凌晨本来就不抬」。
            var open = Quiet();
            open.DndEnabled = false;
            var plain = new FakeInput(0);
            var live = new Scheduler();
            plain.Type(70_000);
            plain.Advance(1_500);
            live.Step(plain.Now, Detected.Fake(plain), open, HM(2, 0));
            Expect(live.State == Phase.Shown, "关掉勿扰之后凌晨也不抬 —— 对照不成立");
        });

        return checks.Report();
    }

    /// <summary>读模型那一族（卡片 01）的判据：假时钟下问 <see cref="SnapshotOf"/> 和五个查询。
    /// 每一条都拿「改前的读法」当对照 —— 这一族怕的就是读口和字段直读分家，
    /// 对照不在，判据量的就成了它自己。</summary>
    public static List<string> ReadSelfTest()
    {
        var checks = new Checks();
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        Settings Fast() => new()
        {
            ActivityTimeoutMin = 1,
            FadeInMs = 3_000,
            FadeOutMs = 1_000,
            MinBreakMs = 20_000,
            TriggerPauseMs = 1_000,
            DismissHoldMs = 2_000,
        };

        checks.Check("SnapshotOf：Hidden 时 Stage.Total 是 0；举起后 Phase、Number / Total 与 Plan 对上", () =>
        {
            var s = Fast();
            var m = new Scheduler();
            var snap = m.SnapshotOf(s);
            Expect(snap.Phase == Phase.Hidden && snap.Stage.Total == 0,
                $"没举之前快照指着 Phase={snap.Phase}、Stage.Total={snap.Stage.Total}");
            m.Raise(1_000, s);
            snap = m.SnapshotOf(s);
            Expect(snap.Phase == Phase.Shown, "Raise 之后快照还指着 Hidden");
            Expect(snap.Stage.Number == 1 && snap.Stage.Total == m.Plan.Count,
                $"快照指着 step {snap.Stage.Number}/{snap.Stage.Total}，Plan 里是 {m.Plan.Count} 步");
            Expect(ReferenceEquals(snap.Stage.Step, m.Plan[0]), "快照里那一步不是 Plan 里那一步");
            Expect(snap.BlockRaise == null && !snap.Preview && !snap.Pomodoro,
                "默认 Raise 起来的那一发不该带块到点 / 预览 / 番茄钟");

            // 多步的那一条也对一遍：番茄钟那一行有两步，Total 得跟着 Plan 而不是跟着单步的默认。
            var p = new Scheduler();
            p.Raise(0, s, pomodoro: true);
            var ps = p.SnapshotOf(s);
            Expect(ps.Stage.Total == s.PomodoroSteps().Count && ps.Stage.Number == 1,
                $"番茄钟那一轮快照指着 {ps.Stage.Number}/{ps.Stage.Total}，那一行有 {s.PomodoroSteps().Count} 步");
        });

        checks.Check("StageLeftMs：没有到期时刻回 null；随时间递减；到点回 0", () =>
        {
            var s = Fast();
            // fadetop 那一条只有一个 ms:0 的 step —— 写在最后一个上的 0 是「没有到期时刻」。
            s.Flow = Settings.FlowFadeTop;
            var m = new Scheduler();
            m.Raise(0, s);
            Expect(m.StageLeftMs(s, 1_000) is null, "没有到期时刻的 step 报出了数");
            Expect(new Scheduler().StageLeftMs(s, 0) is null, "没举的时候不该有这一步的余量");
            // pomodoro 那一行第一步 ms:0 不是最后一步：淡一落定（FadeInMs）就是它的到期时刻。
            var p = new Scheduler();
            p.Raise(0, s, pomodoro: true);
            Expect(p.StageLeftMs(s, 0) == s.FadeInMs,
                $"刚举起时报 {p.StageLeftMs(s, 0)}，该是淡入的 {s.FadeInMs}");
            Expect(p.StageLeftMs(s, 1_000) == s.FadeInMs - 1_000,
                $"过一秒该少一秒，报了 {p.StageLeftMs(s, 1_000)}");
            Expect(p.StageLeftMs(s, s.FadeInMs) == 0, "到点那一瞬该回 0");
            Expect(p.StageLeftMs(s, s.FadeInMs + 5_000) == 0, "过点之后该钉在 0");
        });

        checks.Check("BreakLeftMs：关自动淡入 / 番茄钟在跑 / 遮罩抬着三档回 null；与静态纯函数同值", () =>
        {
            var s = Fast();
            s.AutoFade = false;
            Expect(new Scheduler().BreakLeftMs(s, 0) is null, "自动淡入关着还报了余量");
            s.AutoFade = true;
            var running = new Scheduler();
            running.TogglePomodoro(0, s);
            Expect(running.BreakLeftMs(s, 1_000) is null, "番茄钟在跑还报了余量");
            var raised = new Scheduler();
            raised.Raise(0, s);
            Expect(raised.BreakLeftMs(s, 1_000) is null, "遮罩抬着还报了余量");

            // 同值：把静态那一份的三项输入交给当下状态机 —— 读口不许自己另算一遍。
            foreach (var m in new[] { new Scheduler(), running, raised })
            {
                var want = BreakLeftMs(s, m.ElapsedMs(10_000), m.Pomodoro, m.State != Phase.Hidden);
                Expect(m.BreakLeftMs(s, 10_000) == want,
                    $"实例读口报 {m.BreakLeftMs(s, 10_000)}，静态纯函数报 {want}");
            }
            // 数值档：这一段走了一半，剩下的那一半就是读数。
            var walked = new Scheduler { SegmentStartMs = 10_000 };
            var owed = (long)s.ActivityTimeoutMin * 60_000;
            Expect(walked.BreakLeftMs(s, 40_000) == owed - 30_000,
                $"走了 30 秒报 {walked.BreakLeftMs(s, 40_000)}，该是 {owed - 30_000}");
        });

        checks.Check("NextFadeLeftMs：与抬头原算式逐档同值；遮罩抬着回 0；OverrideOwedMs 只改它、不改 BreakLeftMs", () =>
        {
            var s = Fast();
            var now = 20_000L;
            // 对照 = App.StatusLine 改前那半段算式（字段直读），一处一档都过一遍。
            long Legacy(Scheduler m)
            {
                var owed = m.OwedMsOverride ?? (long)s.ActivityTimeoutMin * 60_000;
                var walked = owed - m.ElapsedMs(now);
                var retry = m.NextAttemptMs - now;
                var left = retry > 0 ? Math.Min(walked, retry) : walked;
                return m.State == Phase.Shown ? 0 : Math.Max(0, left);
            }

            var skipped = new Scheduler();
            skipped.Raise(0, s);
            skipped.EndBreak(5_000, s);   // 取消一次 → 推上「下一次淡入」的等待
            var pomodoro = new Scheduler();
            pomodoro.TogglePomodoro(0, s);
            var raised = new Scheduler();
            raised.Raise(0, s);
            var overridden = new Scheduler();
            overridden.OverrideOwedMs(45_000);
            foreach (var m in new[] { new Scheduler(), skipped, pomodoro, raised, overridden })
                Expect(m.NextFadeLeftMs(s, now) == Legacy(m),
                    $"读口报 {m.NextFadeLeftMs(s, now)}，原算式报 {Legacy(m)}");

            Expect(raised.NextFadeLeftMs(s, 10_000) == 0, "遮罩抬着没回 0");

            // OverrideOwedMs 改得了抬头那一问，改不动托盘那一行。
            var m2 = new Scheduler();
            var breakBefore = m2.BreakLeftMs(s, now);
            m2.OverrideOwedMs(5_000);
            Expect(m2.NextFadeLeftMs(s, now) == Math.Max(0, 5_000 - m2.ElapsedMs(now)),
                "OverrideOwedMs 没改成抬头那一问的读数");
            Expect(m2.BreakLeftMs(s, now) == breakBefore,
                $"OverrideOwedMs 动了 BreakLeftMs（{breakBefore} → {m2.BreakLeftMs(s, now)}）");
        });

        checks.Check("Retry：第一次取消后给出 Skipped == 1、NextAttemptInMs == NextWait(settings, 1)", () =>
        {
            var s = Fast();
            var m = new Scheduler();
            Expect(m.Retry(0).Skipped == 0, "没取消过不该有欠账");
            m.Raise(0, s);
            m.EndBreak(1_000, s);   // 提前结束 = 取消，和挥散同一条路
            var (skipped, nextIn) = m.Retry(1_000);
            Expect(skipped == 1, $"一次取消后欠账是 {skipped}，该是 1");
            var first = NextWait(s, 1)
                ?? throw new Exception("这一档的设置里休息延迟序列为空，这一发测不了下一次等待");
            Expect(nextIn == first,
                $"下一次最早在 {nextIn} 毫秒后，该是序列第一格的 {first} 毫秒");
        });

        return checks.Report();
    }

    /// <summary>一段固定脚本跑完留下的<b>逐行</b>实测结果：每一帧的状态、抬起来的那一条休息流程、
    /// 落下时算不算休息，外加收尾那一刻的等待 / 休息延迟的欠账 / 番茄钟那几个数。
    ///
    /// 走的是两条老路：连续输入攒够 → 淡入 → 被挥散 → 番茄钟跑到停。
    /// 现实时间上的那一刻整段不变（<see cref="HM"/> 算出来的，落在表外的一刻）——
    /// 这一段量的是那两条触发，而作息表那一族在总开关关着时压根不参与。</summary>
    private static string Transcript(Settings settings)
    {
        var m = new Scheduler();
        var input = new FakeInput(0);
        var lines = new List<string>();
        var minute = HM(4, 0);   // 表外的一刻

        m.Raised += n => lines.Add($"抬 {n.Number}/{n.Total} 步 {string.Join('/', m.Plan.Select(s => s.Ms))}");
        m.StageEntered += n => lines.Add($"进 {n.Number}/{n.Total}");
        m.Lowered += taken => lines.Add($"落 {taken}");

        void Say(string tag, long now) =>
            lines.Add($"{tag} 状态={m.State} 记={ScoredText(m.LastOutcome)} " +
                      $"等={(m.NextAttemptMs - now) / 1000} 秒 欠账={m.Skipped} " +
                      $"轮={m.PomodoroRounds} 专注剩={m.PomodoroFocusLeftMs(now) / 1000} 秒 " +
                      $"到点={m.PomodoroRound} 块={(m.BlockRaise?.Name ?? "—")}");

        input.Type(90_000);
        m.Step(91_500, Detected.Fake(input), settings, minute);
        Say("① 攒够 + 空档", 91_500);
        input.Type(94_000);
        m.Step(94_000, Detected.Fake(input), settings, minute);
        Say("② 挥散", 94_000);
        for (var t = 94_500L; t < 200_000; t += 500) m.Step(t, Detected.Fake(input), settings, minute);
        Say("③ 离开一阵", 200_000);
        m.TogglePomodoro(200_000, settings);
        Say("④ 番茄钟开", 200_000);
        var due = m.PomodoroDueMs;
        m.Step(due, Detected.Fake(input), settings, minute);
        Say("⑤ 到点", due);
        for (var t = due + 500L; t < due + 180_000 && m.State == Phase.Shown; t += 500)
            m.Step(t, Detected.Fake(input), settings, minute);
        Say("⑥ 一轮走完", due + 180_000);
        m.TogglePomodoro(due + 180_000, settings);
        Say("⑦ 停", due + 180_000);
        return string.Join('\n', lines);
    }
}
