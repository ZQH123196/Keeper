using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

// 卡片 03 · 日历宿主生命周期从绘制里抽出来。
// 这一份收三档状态（Hidden / Parked / Editing）加意图位 Wanted 的那一族：
// seam（IHostOps）、真 adapter（DesktopHostOps）和 module（CalendarHost）住在这里。
// 形状与机制的设计在 docs/design/calendar.md「宿主生命周期」那一节。

/// <summary>到点要推像素时 module 发给 <c>Calendar</c> 的那一问。
/// <see cref="Because"/> 是日志里那半句「为什么推」，<see cref="Force"/> 和
/// <see cref="Announce"/> 直接对上 <c>Calendar.PushPixels</c> 的两个形参 ——
/// 推像素要引擎的视口和桥，那些归 <c>Calendar</c>，module 只发这一问。</summary>
internal readonly record struct PushRequest(string Because, bool Force, bool Announce);

/// <summary>挂接那一发的结果。<see cref="Note"/> 是那一行实测结果（逐字等于
/// <c>NativeLayer.Attach</c> 返回的那一行）；<see cref="Proceeded"/> 答「挂上了没有」。
/// <b>挂失败不改状态机的档位</b> —— <c>Park</c> 的成不成由引擎 HWND 答，不由这一问答。</summary>
internal readonly record struct AttachResult(bool Proceeded, string Note);

/// <summary>宿主那一族的全部动作。<b>module 只经由这一问接口碰桌面，
/// 不直接碰 <c>DesktopLayer</c>、Win32、Godot</b>（卡片 03 的取向）。
///
/// <para><b>真实施 <see cref="DesktopHostOps"/></b>：包 <c>NativeLayer</c> 的挂接那一族、
/// <c>DesktopLayer</c> 的 <c>HandleOf</c> / <c>KeepEngineWindowHidden</c> / <c>GrabForTyping</c>、
/// 引擎窗的 <c>ShowWindow</c>。<b>假实施 <c>FakeHostOps</c></b>（<see cref="CalendarHost"/> 旁边，
/// 和 <c>Input</c> / <c>FakeInput</c> 同一个形状）只给自检：记调用序列、可编排各问的结果。</para></summary>
internal interface IHostOps
{
    /// <summary>引擎那一扇窗的 HWND（0 = 读不到）。<b>缓存在 adapter 里，
    /// <see cref="Attach"/> 时重新解析</b>。</summary>
    nint EngineHandle { get; }

    /// <summary>引擎那一扇窗还在不在。别的程序关掉它、Explorer 重启把它带走时答假。</summary>
    bool EngineAlive { get; }

    /// <summary>日历那一扇（借壳自己的原生子窗）挂好了没有。透传 <c>NativeLayer.Attached</c>。</summary>
    bool Attached { get; }

    /// <summary>最近一次挂接/重挂的那一行实测结果。透传 <c>NativeLayer.Measurement</c>。</summary>
    string Measurement { get; }

    AttachResult Attach(Win32.Rect where);
    string Reattach(Win32.Rect where);
    WatchResult Watch();
    string Lift(Win32.Rect where);
    PushOutcome Push(byte[] bgra, int width, int height, Win32.Rect where);
    string Detach();
    void HideEngineWindow();
    void ShowEngineWindow();
    string GrabForTyping();
    string? KeepEngineWindowHidden(bool parked);
}

/// <summary>宿主那一族的真 adapter（卡片 03 · S25）。
///
/// <para><b>它包的对象各有各的出处</b>：像素那一族住 <c>NativeLayer</c>（它自己消费
/// <c>DesktopLayer.CalendarHost()</c> 那份快照，卡片 04 · S19 定死）；引擎句柄走
/// <c>DesktopLayer.HandleOf</c>；打字那一路走 <c>DesktopLayer.GrabForTyping</c>，
/// <b>抬的是日历那一扇不是引擎那一扇</b>（判据在 <c>NativeLayer.EditingWindow</c>）。</para>
///
/// <para><b>引擎句柄缓存在这里</b>：以前那一份住在 <c>Calendar._hwnd</c>，宿主生命周期
/// 搬进 module 之后它是 adapter 的私事。读的时候没有才去解析一次，
/// <see cref="Attach"/> 时无条件重新解析 —— 和搬走之前 <c>Park</c> 每次先取
/// <c>HandleOf</c> 的那一步逐条对应。</para></summary>
internal sealed class DesktopHostOps(Window engineWindow) : IHostOps
{
    private readonly NativeLayer _native = new();
    private nint _engineHandle;

    /// <summary>缓存的那一份。<b>为 0 才去解析</b>（引擎窗还没进树时读不到，
    /// 下一发再读就是重来一次 —— 和搬走之前每次 <c>Park</c> 都重新解析不冲突，
    /// 因为解析到非 0 之后句柄不再变）。</summary>
    public nint EngineHandle
    {
        get
        {
            if (_engineHandle == 0) _engineHandle = DesktopLayer.HandleOf(engineWindow);
            return _engineHandle;
        }
    }

    public bool EngineAlive => Win32.IsWindow(EngineHandle);

    public bool Attached => _native.Attached;

    public string Measurement => _native.Measurement;

    /// <summary>挂接：<b>先重新解析引擎句柄</b>（<c>Park</c> 之后藏引擎窗、
    /// 编辑抬起算焦点都要用它），再走 <c>NativeLayer.Attach</c>。</summary>
    public AttachResult Attach(Win32.Rect where)
    {
        _engineHandle = DesktopLayer.HandleOf(engineWindow);
        // 先挂接、后读 Attached。实参从左到右求值，直接构造读到的是挂接前的状态。
        var note = _native.Attach(where);
        return new AttachResult(_native.Attached, note);
    }

    public string Reattach(Win32.Rect where) => _native.Reattach(where);

    public WatchResult Watch() => _native.Watch();

    public string Lift(Win32.Rect where) => _native.Lift(where);

    public PushOutcome Push(byte[] bgra, int width, int height, Win32.Rect where) =>
        _native.Push(bgra, width, height, where);

    public string Detach() => _native.Detach();

    /// <summary>藏引擎窗。<b>句柄读不到就一个字节都不动</b> —— 那一发以前由
    /// <c>Calendar</c> 的 <c>if (_hwnd != 0)</c> 守着，搬进 adapter 之后守在这里。</summary>
    public void HideEngineWindow()
    {
        if (EngineHandle != 0) Win32.ShowWindow(EngineHandle, Win32.SwHide);
    }

    public void ShowEngineWindow()
    {
        if (EngineHandle != 0) Win32.ShowWindow(EngineHandle, Win32.SwShowNoActivate);
    }

    /// <summary>抬出来打字那一路。<b>抬哪一扇的判据没搬</b> ——
    /// 还是 <c>NativeLayer.EditingWindow</c> 那一问：日历那一扇不在就一个都不抬。</summary>
    public string GrabForTyping() =>
        DesktopLayer.GrabForTyping(NativeLayer.EditingWindow(EngineHandle, _native.Handle));

    public string? KeepEngineWindowHidden(bool parked) =>
        DesktopLayer.KeepEngineWindowHidden(EngineHandle, parked);
}

/// <summary>宿主生命周期那一族（卡片 03）。<b>三档状态加一个意图位，转移只走
/// <c>Park</c> / <c>SetDown</c> / <c>Lift</c> / <c>Hide</c> / <c>Unhide</c> 这五发</b>
/// （<see cref="ForceStateForTest"/> 例外，只给白盒自检）。
///
/// <para><b>它收的是宿主那一半</b>：档位、意图位、看门狗与补推的计时和顺序、
/// 「窗没了要不要翻回 Hidden」的判据、到点该推像素时发出的 <see cref="PushRequest"/>。
/// <c>Calendar</c> 留布局、格子、备注、命中、绘制、过午夜、<c>PushPixels</c> 的读回与桥。
/// 分工与设计在 <c>docs/design/calendar.md</c>「宿主生命周期」那一节。</para>
///
/// <para><b>module 不直接碰 <c>DesktopLayer</c>、Win32、Godot</b>：那三样全在
/// <see cref="IHostOps"/> 后面，真的一侧住 <see cref="DesktopHostOps"/>，
/// 自检的一侧住 <see cref="FakeHostOps"/>。</para>
///
/// <para><b>两处照抄现状</b>：<see cref="Park"/> 在引擎 HWND 为 0 时不改档位；
/// <see cref="Lift"/> 先置 Editing，抬起失败也停在 Editing —— 那是搬走之前
/// <c>Calendar</c> 里逐条走着的次序，不是设计时重新挑的。</para></summary>
internal sealed class CalendarHost(IHostOps ops, Action<string> log)
{
    internal enum State { Hidden, Parked, Editing }

    private State _state = State.Hidden;

    /// <summary>意图位。<b>初始真</b>，只由 <see cref="Hide"/> / <see cref="Unhide"/> 改 ——
    /// 它和档位不是一回事：初始档位是 Hidden，意图却是真（还没摆、不是被藏）。</summary>
    private bool _wanted = true;

    /// <summary>看门狗那一发的计时（秒）。<b>从 <c>Calendar</c> 搬进来</b>（卡片 03）——
    /// 那一发是 <see cref="NativeLayer.WatchdogSeconds"/> 一次，不是 1 秒一次。</summary>
    private double _sinceWatch;

    /// <summary>补推那一发的计时（秒）。<b>同样从 <c>Calendar</c> 搬进来</b>。</summary>
    private double _sinceRepush;

    /// <summary>到点补推累计发数，<b>只用来按分钟记那一行</b>。同样搬自 <c>Calendar</c>。</summary>
    private int _repushes;

    internal State StateNow => _state;
    internal bool Wanted => _wanted;
    internal string Measurement => ops.Measurement;
    internal nint EngineHandle => ops.EngineHandle;

    /// <summary>推像素那一问透传给 adapter —— <c>Calendar.PushPixels</c> 拿它，
    /// 因为读回、BGRA、<c>_dirty</c> 那些还归 <c>Calendar</c>。</summary>
    internal PushOutcome Push(byte[] bgra, int width, int height, Win32.Rect where) =>
        ops.Push(bgra, width, height, where);

    /// <summary>摆上：读引擎 HWND → 置 Parked → 挂原生子窗 → 记那一行 → 藏引擎窗。
    ///
    /// <para><b>读不到 HWND 那一档照抄现状</b>：不改档位、答假，只记
    /// 「读不到 HWND」那一行，adapter 一个动作都不叫 —— 窗还开着，是普通顶层窗。</para>
    /// <para><b>挂接答假不改档位</b>：挂没挂上都到 Parked，那一行实测结果由日志说话。</para></summary>
    internal bool Park(string when, Win32.Rect where)
    {
        if (ops.EngineHandle == 0)
        {
            log($"日历：{when} 读不到 HWND，这一发没摆成（窗还开着，是普通顶层窗）");
            return false;
        }
        _state = State.Parked;
        var attach = ops.Attach(where);
        log($"日历：{when} {attach.Note}");
        ops.HideEngineWindow();
        return true;
    }

    /// <summary>落回摆着那一档：先置 Parked，再看引擎 HWND —— 和 <see cref="Park"/> 的
    /// 次序正好相反，那是 <c>CommitAndSetDown</c> 搬走之前就在的次序（窗没了也得先认下
    /// 「这一发是要落回摆着」）。答假的唯一一档是读不到 HWND。</summary>
    internal bool SetDown(string when, Win32.Rect where)
    {
        _state = State.Parked;
        if (ops.EngineHandle == 0)
        {
            log($"日历：{when} 读不到 HWND，这一发没摆成（窗还开着，是普通顶层窗）");
            return false;
        }
        var attach = ops.Attach(where);
        log($"日历：{when} {attach.Note}");
        ops.HideEngineWindow();
        return true;
    }

    /// <summary>抬出来打字：只有摆着那一档才抬。先置 Editing 再看抬没抬成 ——
    /// <b>抬起失败也停在 Editing</b>（照抄现状；那一扇不在时 <c>Lift</c> 返回的那一行
    /// 自己会说「抬起这一发没做」）。Hidden 档什么都不做。</summary>
    internal void Lift(Win32.Rect where)
    {
        if (_state != State.Parked) return;
        _state = State.Editing;
        var lifted = ops.Lift(where);
        var front = ops.GrabForTyping();
        log($"日历：抬出来打字 {lifted}；{front}");
    }

    /// <summary>托盘藏起来：档位翻 Hidden、意图位翻假、藏引擎窗。<b>不拆借来的那一扇</b> ——
    /// 还窗归 <see cref="Release"/>；已知缺口（原生子窗这一档还留在屏上）另开票，不在这里修。</summary>
    internal void Hide()
    {
        _state = State.Hidden;
        _wanted = false;
        ops.HideEngineWindow();
        log("日历：藏起来（托盘）");
    }

    /// <summary>托盘放回来：只把意图位翻真、放引擎窗。<b>档位不动</b> ——
    /// 真正重新摆上是调用方紧接着那发 <see cref="Park"/> 的事。</summary>
    internal void Unhide()
    {
        _wanted = true;
        ops.ShowEngineWindow();
    }

    /// <summary>退出还窗：只把借来的那一扇还回去，档位和意图位都不动。</summary>
    internal string Release() => ops.Detach();

    /// <summary>每帧递进来的秒数。<b>两个计时各归各的周期</b>，到点不发东西 ——
    /// 到点做什么由 <see cref="HostTick"/> 那一问每秒判一次。</summary>
    internal void Advance(double delta)
    {
        _sinceWatch += delta;
        _sinceRepush += delta;
    }

    /// <summary>每秒那一发，次序照搬 <c>Calendar.Tick</c> 里原来那三段：
    /// 看门狗 → 到点补推 → 引擎窗隐藏那一问（每一档都问）。
    ///
    /// <para><b>到点要推像素时只发一问 <see cref="PushRequest"/></b>：推像素要引擎的
    /// 视口和桥，那些在 <c>Calendar</c> 那边，module 不碰。</para></summary>
    internal void HostTick(Win32.Rect placed, Action<PushRequest> requestPush)
    {
        // 看门狗：不许省（工单 28 第 5 步）。宿主没变时那一发一个字节都不动。
        if (_state == State.Parked && _sinceWatch >= NativeLayer.WatchdogSeconds)
        {
            _sinceWatch = 0;
            var watch = ops.Watch();
            if (watch.Kind != WatchKind.NoMove)
            {
                log($"日历：看门狗 {watch.Note}");
                requestPush(new PushRequest("重挂", true, true));
            }
        }

        // 到点补推：判据本体在 NativeLayer.ShouldRepush / ShouldLogRepush，这里只递读数。
        if (_state == State.Parked &&
            NativeLayer.ShouldRepush(ops.Attached, _sinceRepush, NativeLayer.RepushSeconds))
        {
            _sinceRepush = 0;
            _repushes++;
            var back = ops.Reattach(placed);
            requestPush(new PushRequest("到点补推", true, false));
            if (NativeLayer.ShouldLogRepush(_repushes))
                log($"日历：到点补推 {back}；累计 {_repushes} 发，最近一次 {Measurement}");
        }

        // 引擎那一扇摆着的时候必须藏着 —— 每一档都问这一句（Hidden / Editing 档 parked 答假）。
        if (ops.KeepEngineWindowHidden(_state == State.Parked) is { } shown)
            log($"日历：{shown}");
    }

    /// <summary>「窗没了重摆」的判据：只有摆着那一档、而引擎那一扇已经不在了，
    /// 才翻回 Hidden 并答真。<b>真正的重摆仍由 <c>Calendar.Tick</c> 在原位置调
    /// <see cref="Park"/>("重摆")</b> —— 闸门日志和「摆上」那一推留在 <c>Calendar</c>。</summary>
    internal bool RecoverIfEngineGone()
    {
        if (_state != State.Parked || ops.EngineAlive) return false;
        log("日历：这一扇窗没了，重新摆一次");
        _state = State.Hidden;
        return true;
    }

    /// <summary>白盒自检那一发：直接把窗摆进某一档。<b>产品转移不走这里</b> ——
    /// 转移只走 Park / SetDown / Lift / Hide / Unhide。</summary>
    internal void ForceStateForTest(State state) => _state = state;

    /// <summary>这一组在自检里登记的那两句话（绿话/红话）。判据是 <see cref="SelfTest"/>。</summary>
    internal static readonly CriterionOf Criterion = new(
        () => "宿主生命周期那一族通过：初始 Hidden 加意图真、读不到 HWND 时 Park 不改档也不叫 adapter、" +
              "Park 的次序是挂接再藏引擎窗（挂接答假也到 Parked）、Lift 只在摆着那档抬且抬失败仍停在 Editing、" +
              "SetDown 先落 Parked 再看 HWND、Hide 只藏引擎窗不抬不拆、Unhide 只翻意图位不放档、" +
              "看门狗不到点不叫而到点清零只叫一次、NoMove 不发推送而重挂发一条「重挂」、" +
              "补推到点重新挂并发「到点补推」且累计那一行按 RepushLogEvery 发出现、" +
              "Editing 与 Hidden 两档看门狗和补推都不跑而引擎窗那一问照跑、" +
              "窗没了只有摆着那档翻回 Hidden、Release 还窗不改档、每一行日志拿完整字符串逐字比",
        "宿主生命周期", SelfTest);

    /// <summary>宿主那一族的判据（卡片 03 · S26 的十四条）。<b>纯算术</b>：全走
    /// <see cref="FakeHostOps"/>，不碰窗、不碰桌面、不碰 Godot。
    /// 计时边界拿 <c>NativeLayer.WatchdogSeconds</c> / <c>RepushSeconds</c> / <c>RepushLogEvery</c>
    /// 本尊来拧，不抄数字（第十四条）。</summary>
    public static List<string> SelfTest()
    {
        var bad = new List<string>();
        var at = new Win32.Rect { Left = 0, Top = 0, Right = 100, Bottom = 80 };
        const string NoHwndLine = "读不到 HWND，这一发没摆成（窗还开着，是普通顶层窗）";

        // ---- 一：初始档 Hidden、意图真、没日志、没叫 adapter --------------------------
        {
            var (host, ops, lines) = Armed();
            if (host.StateNow != State.Hidden) bad.Add("初始档该是 Hidden");
            if (!host.Wanted) bad.Add("初始意图位该是真 —— 还没摆和被藏是两回事");
            if (lines.Count != 0) bad.Add("刚建好不该有一行日志");
            if (ops.Calls.Count != 0) bad.Add("刚建好一个 adapter 动作都不该叫");
            if (host.Measurement != ops.Measurement) bad.Add("Measurement 没透传给 adapter 那一份");
            if (host.EngineHandle != ops.EngineHandle) bad.Add("EngineHandle 没透传给 adapter 那一份");
        }

        // ---- 二：Park 读不到 HWND：答假、不改档、记那一行、adapter 零动作 --------------
        {
            var (host, ops, lines) = Armed();
            ops.EngineHandle = 0;
            if (host.Park("摆上", at)) bad.Add("读不到 HWND 时 Park 答了真");
            if (host.StateNow != State.Hidden) bad.Add("读不到 HWND 时 Park 不该改档位 —— 窗还是普通顶层窗");
            if (!host.Wanted) bad.Add("读不到 HWND 时 Park 不许动意图位");
            if (!Lines(lines, $"日历：摆上 {NoHwndLine}"))
                bad.Add($"读不到 HWND 那一行逐字不对：{(lines.Count > 0 ? lines[0] : "一行都没有")}");
            if (ops.Calls.Count != 0)
                bad.Add($"读不到 HWND 时 adapter 一个动作都不该叫，读回 {string.Join("、", ops.Calls)}");
        }

        // ---- 三：Park 挂接答假：仍答真、仍到 Parked、次序是 Attach → HideEngineWindow ---
        {
            var (host, ops, lines) = Armed();
            ops.AttachAnswer = new AttachResult(false, "假挂接那一行");
            if (!host.Park("摆上", at)) bad.Add("引擎 HWND 读得到，挂接答假 Park 也该答真 —— 档位归档位、挂接归挂接");
            if (host.StateNow != State.Parked) bad.Add("挂接答假时 Park 没把档落到 Parked");
            if (!Calls(ops, "Attach", "HideEngineWindow"))
                bad.Add($"Park 的调用序该是 Attach → HideEngineWindow，读回 {string.Join("、", ops.Calls)}");
            if (!Lines(lines, "日历：摆上 假挂接那一行"))
                bad.Add($"挂接那一行逐字不对：{(lines.Count > 0 ? lines[0] : "一行都没有")}");
        }

        // ---- 四：Lift：Parked → Editing，次序 Lift → GrabForTyping；抬失败仍 Editing；Hidden 不抬 ----
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            ops.Calls.Clear();
            lines.Clear();
            host.Lift(at);
            if (host.StateNow != State.Editing) bad.Add("Lift 没把档抬到 Editing");
            if (!Calls(ops, "Lift", "GrabForTyping"))
                bad.Add($"Lift 的调用序该是 Lift → GrabForTyping，读回 {string.Join("、", ops.Calls)}");
            if (!Lines(lines, $"日历：抬出来打字 {ops.LiftAnswer}；{ops.GrabAnswer}"))
                bad.Add($"抬起那一行逐字不对：{(lines.Count > 0 ? lines[0] : "一行都没有")}");

            // 对照：那一扇不在（Attached=false）也停在 Editing —— 照抄现状，抬失败不翻回摆着。
            var (h2, o2, _) = Armed();
            h2.Park("摆上", at);
            o2.Attached = false;
            h2.Lift(at);
            if (h2.StateNow != State.Editing) bad.Add("抬起答失败时不该翻回 Parked —— 照抄现状：先置 Editing 再看成没成");

            // 对照：Hidden 档 Lift 一个动作都不叫、不改档。
            var (h3, o3, _) = Armed();
            h3.Lift(at);
            if (o3.Calls.Count != 0) bad.Add("Hidden 档 Lift 不该叫 adapter —— 窗没在屏上，抬什么抬");
            if (h3.StateNow != State.Hidden) bad.Add("Hidden 档 Lift 不该改档位");
        }

        // ---- 五：SetDown：先置 Parked 再看 HWND ------------------------------------------
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            host.Lift(at);
            ops.Calls.Clear();
            lines.Clear();
            ops.EngineHandle = 0;
            if (host.SetDown("放回最底下", at)) bad.Add("读不到 HWND 时 SetDown 该答假");
            if (host.StateNow != State.Parked) bad.Add("读不到 HWND 时 SetDown 也该已置 Parked —— 先落档再看窗");
            if (!Lines(lines, $"日历：放回最底下 {NoHwndLine}"))
                bad.Add($"SetDown 读不到 HWND 那一行逐字不对：{(lines.Count > 0 ? lines[0] : "一行都没有")}");
            if (ops.Calls.Count != 0) bad.Add("SetDown 读不到 HWND 时 adapter 零动作");

            var (h2, o2, l2) = Armed();
            h2.Park("摆上", at);
            h2.Lift(at);
            o2.Calls.Clear();
            l2.Clear();
            if (!h2.SetDown("放回最底下", at)) bad.Add("HWND 读得到时 SetDown 该答真");
            if (h2.StateNow != State.Parked) bad.Add("SetDown 没落到 Parked");
            if (!Calls(o2, "Attach", "HideEngineWindow"))
                bad.Add($"SetDown 的调用序该是 Attach → HideEngineWindow，读回 {string.Join("、", o2.Calls)}");
            if (!Lines(l2, "日历：放回最底下 假挂接那一行"))
                bad.Add($"SetDown 挂接那一行逐字不对：{(l2.Count > 0 ? l2[0] : "一行都没有")}");
        }

        // ---- 六：Hide：Hidden + 意图假，只叫 HideEngineWindow，不抬不拆 ------------------
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            ops.Calls.Clear();
            lines.Clear();
            host.Hide();
            if (host.StateNow != State.Hidden) bad.Add("Hide 没把档翻到 Hidden");
            if (host.Wanted) bad.Add("Hide 没把意图位翻假");
            if (!Calls(ops, "HideEngineWindow"))
                bad.Add($"Hide 只该叫 HideEngineWindow —— 不抬、不拆、也不藏原生窗，读回 {string.Join("、", ops.Calls)}");
            if (!Lines(lines, "日历：藏起来（托盘）")) bad.Add("藏起来那一行逐字不对");
        }

        // ---- 七：Unhide：意图翻真、档不动、叫 ShowEngineWindow --------------------------
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            host.Hide();
            ops.Calls.Clear();
            lines.Clear();
            host.Unhide();
            if (!host.Wanted) bad.Add("Unhide 没把意图位翻回真");
            if (host.StateNow != State.Hidden) bad.Add("Unhide 不该改档位 —— 重新摆上是调用方那发 Park 的事");
            if (!Calls(ops, "ShowEngineWindow"))
                bad.Add($"Unhide 只该叫 ShowEngineWindow，读回 {string.Join("、", ops.Calls)}");
            if (lines.Count != 0) bad.Add("Unhide 不该有日志 —— 放引擎窗那一发在搬走之前也没有行");
        }

        // ---- 八：看门狗：不到点不叫、到点清零只叫一次、NoMove 不发推送、重挂发「重挂」 ----
        {
            var (host, ops, lines) = Armed();
            ops.Attached = false;   // 补推隔离：没有接收方时补推那一族不跑
            host.Park("摆上", at);
            ops.Calls.Clear();
            lines.Clear();
            var reqs = new List<PushRequest>();

            host.Advance(NativeLayer.WatchdogSeconds - 0.001);
            host.HostTick(at, reqs.Add);
            if (ops.Calls.Contains("Watch")) bad.Add("看门狗还没到点就发了 Watch");

            host.Advance(0.001);
            host.HostTick(at, reqs.Add);
            if (Count(ops.Calls, "Watch") != 1) bad.Add($"到点该叫且只叫一次 Watch，读回 {Count(ops.Calls, "Watch")} 次");
            if (reqs.Count != 0) bad.Add("看门狗答 NoMove（宿主没变）不该发推送");
            if (lines.Count != 0) bad.Add("看门狗答 NoMove 不该记行");

            // 对照（反向「去掉计时清零」在这一格变红）：清零之后不许马上再叫。
            host.Advance(1.0);
            host.HostTick(at, reqs.Add);
            if (Count(ops.Calls, "Watch") != 1) bad.Add("到点那一发没把计时清零 —— 之后每一秒都白叫一次看门狗");

            ops.WatchAnswer = WatchKind.Reattached;
            host.Advance(NativeLayer.WatchdogSeconds);
            host.HostTick(at, reqs.Add);
            if (Count(ops.Calls, "Watch") != 2) bad.Add("补到整周期后看门狗没再叫");
            if (reqs.Count != 1 || reqs[0] != new PushRequest("重挂", true, true))
                bad.Add($"重挂该发一条 (\"重挂\", true, true)，读回 {reqs.Count} 条");
            if (!Lines(lines, $"日历：看门狗 {ops.WatchNote}")) bad.Add("看门狗那一行逐字不对");
        }

        // ---- 九：补推：到点 Reattach + 发「到点补推」；累计那一行的出现节奏 ---------------
        {
            var (host, ops, lines) = Armed();
            ops.WatchAnswer = WatchKind.NoMove;   // 看门狗隔离：NoMove 不发推送也不记行
            host.Park("摆上", at);
            ops.Calls.Clear();
            lines.Clear();
            var reqs = new List<PushRequest>();

            host.Advance(NativeLayer.RepushSeconds);
            host.HostTick(at, reqs.Add);
            if (!ops.Calls.Contains("Reattach")) bad.Add("补推到点没重新挂 —— 只推进去救不回被壳丢掉的那张面");
            if (reqs.Count != 1 || reqs[0] != new PushRequest("到点补推", true, false))
                bad.Add($"补推该发一条 (\"到点补推\", true, false)，读回 {reqs.Count} 条");
            if (!Lines(lines, $"日历：到点补推 {ops.ReattachAnswer}；累计 1 发，最近一次 {ops.Measurement}"))
                bad.Add($"补推累计那一行逐字不对：{(lines.Count > 0 ? lines[0] : "一行都没有")}");

            // 拧到 RepushLogEvery + 1 发：ShouldLogRepush 是「1 加每 RepushLogEvery 发」那一档。
            for (var i = 0; i < NativeLayer.RepushLogEvery; i++)
            {
                host.Advance(NativeLayer.RepushSeconds);
                host.HostTick(at, reqs.Add);
            }
            if (Count(ops.Calls, "Reattach") != NativeLayer.RepushLogEvery + 1)
                bad.Add($"到 {NativeLayer.RepushLogEvery + 1} 发该重新挂 {NativeLayer.RepushLogEvery + 1} 次");
            if (reqs.Count != NativeLayer.RepushLogEvery + 1)
                bad.Add($"每一发补推都该递一次推送请求，读回 {reqs.Count} 次");
            var cum = lines.Count(l => l.StartsWith("日历：到点补推", StringComparison.Ordinal) && l.Contains("累计", StringComparison.Ordinal));
            if (cum != 2) bad.Add($"累计那一行该在第 1 发和第 {NativeLayer.RepushLogEvery + 1} 发各出现一次，读回 {cum} 次");
            if (!lines.Contains($"日历：到点补推 {ops.ReattachAnswer}；累计 {NativeLayer.RepushLogEvery + 1} 发，最近一次 {ops.Measurement}"))
                bad.Add($"第 {NativeLayer.RepushLogEvery + 1} 发的累计那一行逐字不对");
        }

        // ---- 十：Editing / Hidden 两档：看门狗和补推都不跑；引擎窗那一问照跑、parked=假 ----
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            host.Lift(at);
            ops.Calls.Clear();
            lines.Clear();
            var reqs = new List<PushRequest>();
            host.Advance(NativeLayer.WatchdogSeconds + NativeLayer.RepushSeconds);
            ops.KeepHiddenAnswer = "假藏回去那一行";
            host.HostTick(at, reqs.Add);
            if (ops.Calls.Contains("Watch")) bad.Add("编辑档跑了看门狗 —— 抬着的窗不许被重按回壁纸底下");
            if (ops.Calls.Contains("Reattach")) bad.Add("编辑档跑了补推 —— 抬着的窗重新挂就是把编辑那一块按回去");
            if (reqs.Count != 0) bad.Add("编辑档不该递推送请求");
            if (!Calls(ops, "Keep:False")) bad.Add($"Editing 档引擎窗那一问照跑且 parked=假，读回 {string.Join("、", ops.Calls)}");
            if (!Lines(lines, "日历：假藏回去那一行")) bad.Add("引擎窗那一问答出的行逐字不对");

            var (h2, o2, _) = Armed();   // Hidden 档（刚建好）
            o2.KeepHiddenAnswer = null;
            h2.Advance(NativeLayer.WatchdogSeconds + NativeLayer.RepushSeconds);
            h2.HostTick(at, reqs.Add);
            if (o2.Calls.Contains("Watch") || o2.Calls.Contains("Reattach"))
                bad.Add("Hidden 档跑了看门狗或补推 —— 窗没在屏上，问那些做什么");
            if (!Calls(o2, "Keep:False")) bad.Add("Hidden 档引擎窗那一问也该照问");
        }

        // ---- 十一：RecoverIfEngineGone：只有 Parked 且窗没了才翻 Hidden ------------------
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            lines.Clear();
            ops.EngineAlive = false;
            if (!host.RecoverIfEngineGone()) bad.Add("窗没了那一发答了假 —— 句柄烂掉之后所有 Win32 调用都读 0，这一扇就再也回不来");
            if (host.StateNow != State.Hidden) bad.Add("窗没了翻回 Hidden 才对，重摆是调用方那发 Park");
            if (!Lines(lines, "日历：这一扇窗没了，重新摆一次")) bad.Add("窗没了那一行逐字不对");

            // 对照：原生窗掉了（Attached=假）而引擎窗还活着 —— 不许翻。
            var (h2, o2, l2) = Armed();
            h2.Park("摆上", at);
            l2.Clear();
            o2.Attached = false;
            o2.EngineAlive = true;
            if (h2.RecoverIfEngineGone()) bad.Add("引擎窗还活着、只原生窗掉了就翻 Hidden —— 那一档归看门狗和补推，不归这一问");
            if (h2.StateNow != State.Parked) bad.Add("答假的这一发不许动档位");
            if (l2.Count != 0) bad.Add("答假的这一发不许记行");

            // 对照：Hidden / Editing 档都不问。
            var (h3, o3, _) = Armed();
            o3.EngineAlive = false;
            if (h3.RecoverIfEngineGone()) bad.Add("Hidden 档窗本来就没摆，不该答「要重摆」");
            var (h4, o4, _) = Armed();
            h4.Park("摆上", at);
            h4.Lift(at);
            o4.EngineAlive = false;
            if (h4.RecoverIfEngineGone()) bad.Add("编辑档不许翻 Hidden —— 正在打字那一扇翻回 Hidden 就没人落它了");
        }

        // ---- 十二：Release：只还窗，档位与意图都不动 ------------------------------------
        {
            var (host, ops, lines) = Armed();
            host.Park("摆上", at);
            ops.Calls.Clear();
            lines.Clear();
            var note = host.Release();
            if (note != ops.DetachAnswer) bad.Add("Release 没把 adapter 那一行原样交回 —— 调用方要打进退出日志");
            if (host.StateNow != State.Parked) bad.Add("Release 不许改档位");
            if (!host.Wanted) bad.Add("Release 不许动意图位");
            if (!Calls(ops, "Detach")) bad.Add($"Release 只该叫 Detach，读回 {string.Join("、", ops.Calls)}");
            if (lines.Count != 0) bad.Add("Release 不该记行 —— 那一行由调用方带着「退出」前缀记");
        }

        // ---- 十三：ForceStateForTest 摆进档位后，下一发转移照状态表走 ----------------------
        {
            var (host, ops, _) = Armed();
            host.ForceStateForTest(State.Parked);
            if (host.StateNow != State.Parked) bad.Add("ForceStateForTest 没把窗摆进 Parked");
            ops.Calls.Clear();
            host.Lift(at);
            if (!Calls(ops, "Lift", "GrabForTyping")) bad.Add("摆进 Parked 后 Lift 该照状态表抬 —— 白盒那一发只改档，不改转移表");
        }

        return bad;

        // 假 adapter + 日志收口 + module 的一条线。每个判据自己拧自己那一份。
        static (CalendarHost, FakeHostOps, List<string>) Armed()
        {
            var ops = new FakeHostOps();
            var lines = new List<string>();
            return (new CalendarHost(ops, lines.Add), ops, lines);
        }

        static bool Lines(List<string> got, params string[] want) =>
            got.Count == want.Length && got.SequenceEqual(want);

        static bool Calls(FakeHostOps ops, params string[] want) =>
            ops.Calls.Count == want.Length && ops.Calls.SequenceEqual(want);

        static int Count(List<string> calls, string name) => calls.Count(c => c == name);
    }
}

/// <summary>宿主那一族的假 adapter，<b>只给自检</b>（和 <c>Input</c> / <c>FakeInput</c>
/// 同一个形状）：记调用序列，可编排 Attach / Watch / Push 的结果和引擎句柄。</summary>
internal sealed class FakeHostOps : IHostOps
{
    // ---- 可编排的读数（默认：引擎窗在且活着、原生窗挂着、看门狗答「宿主没变」）----
    public nint EngineHandle { get; set; } = (nint)0x1234;
    public bool EngineAlive { get; set; } = true;
    public bool Attached { get; set; } = true;
    public string Measurement { get; set; } = "假最近那一行";
    public AttachResult AttachAnswer { get; set; } = new(true, "假挂接那一行");
    public string ReattachAnswer { get; set; } = "假重新挂那一行";
    public WatchKind WatchAnswer { get; set; } = WatchKind.NoMove;
    public string WatchNote { get; set; } = "假看门狗那一行";
    public string LiftAnswer { get; set; } = "假抬起那一行";
    public PushOutcome PushAnswer { get; set; } = new(true, true, "假推进去那一行");
    public string DetachAnswer { get; set; } = "假还了那一行";
    public string GrabAnswer { get; set; } = "假前台那一行";
    public string? KeepHiddenAnswer { get; set; }

    /// <summary>调用序列。<b>属性读数不记账</b>（EngineHandle / Attached 那些是问，不是做）。</summary>
    public readonly List<string> Calls = [];

    public AttachResult Attach(Win32.Rect where) { Calls.Add("Attach"); return AttachAnswer; }
    public string Reattach(Win32.Rect where) { Calls.Add("Reattach"); return ReattachAnswer; }
    public WatchResult Watch() { Calls.Add("Watch"); return new WatchResult(WatchAnswer, WatchNote); }
    public string Lift(Win32.Rect where) { Calls.Add("Lift"); return LiftAnswer; }
    public PushOutcome Push(byte[] bgra, int width, int height, Win32.Rect where) { Calls.Add("Push"); return PushAnswer; }
    public string Detach() { Calls.Add("Detach"); return DetachAnswer; }
    public void HideEngineWindow() => Calls.Add("HideEngineWindow");
    public void ShowEngineWindow() => Calls.Add("ShowEngineWindow");
    public string GrabForTyping() { Calls.Add("GrabForTyping"); return GrabAnswer; }
    public string? KeepEngineWindowHidden(bool parked)
    {
        Calls.Add(parked ? "Keep:True" : "Keep:False");
        return KeepHiddenAnswer;
    }
}
