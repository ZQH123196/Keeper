using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace Keeper;

/// <summary>产品主动开的那一道口：桌面量测工具（独立进程）从外面问那几句只有产品答得出的问。
///
/// <para><b>取向在 <c>docs/adr/0004</c>「桌面量测工具开在产品进程里的那一道口」那一节</b>：
/// 产品主动开，不是独立进程每次去问。产品每帧本来就在跑，<b>接一道口不需要新线程</b> ——
/// 所以这里没有第二根线程，接话是 <see cref="Pump"/>，每帧由 <see cref="App._Process"/> 调一次。
/// 要是另起一个线程去接话，那才是新增线程：主线程之外接活，等于多一条要照料的线程。</para>
///
/// <para><b>不许递活对象出去</b>（工单 05 那一条比「答什么」更要紧）：出去的是句柄、矩形、
/// 数、字符串。这五个动词里没有一个递得出 <see cref="Calendar"/> / <see cref="Fader"/> /
/// <see cref="Settings"/> 本身。<see cref="Hooks"/> 是<b>进来</b>的那一头，读的是产品此刻的实况，
/// 和 <see cref="Shots.Hooks"/> 同一形状。</para>
///
/// <para><b>不许让产品做测量程序的活</b>：屏、z 序、Win+D、真鼠标那一族全在独立进程那边，
/// 这一侧一句都不碰。这里答的每一句都是「拿着格子表、拿着窗句柄、拿着控件排完版的结果才答得出」
/// 的那几句 —— 独立进程手上只有一扇窗的句柄，答不出。</para>
///
/// <para><b>不许阻塞</b>：<see cref="Pump"/> 挡住主循环就是产品出故障。所以「有没有连上」
/// 问的是 <see cref="WaitForConnectionAsync"/> 那个任务完没完，不等它；读只读
/// <see cref="PipeStream.Available"/> 报出来的那几个字节，凑不齐一整行就原样留着。</para>
///
/// <para><b>旗子不在就不开</b>（<c>--probe-port</c>，认旗在 <see cref="App"/> 那一侧）：
/// 不带旗子的时候这里连构造都不走，一个句柄都不占。</para>
///
/// <para><b>协议</b>：UTF-8 文本行，一行请求、一行回答，行尾 <c>\n</c>。回答要么
/// <c>ok &lt;载荷&gt;</c>，要么 <c>err &lt;原因&gt;</c>。动词有五个：
/// <c>cell</c>、<c>checklist</c>、<c>editmode</c>、<c>swallow</c>、<c>mouse</c>。</para></summary>
internal sealed class ProbePort : IDisposable
{
    /// <summary>旗子。没带它 <see cref="App"/> 就不构造这一份，口一个句柄都不占。</summary>
    public const string Flag = "--probe-port";

    /// <summary>管道名。<b>名字带用户名和 pid。</b>两个都带旗子的实例各供各的一根，
    /// 不会抢同一个名字 —— 抢了的话量具可能问到另一个实例，而那个实例的读数
    /// 会被当成这一发的结论用。
    ///
    /// <para>pid 进了名字，量具就没法只靠名字认出「这是我起的那一个」，
    /// 得先有一份在跑的产品进程名单再挨个试。**那份名单量具本来就有** ——
    /// 找日历那一扇靠的就是它（<c>ProductProcess.RunningProductIds</c>），
    /// 所以这不是额外要照料的机制，只是把已经有的那份名单多拿来用一次。</para>
    ///
    /// <para>2026-10-12 所有者定：原来不带 pid（取舍记在工单 05 的 Comments 里），
    /// 那天改成带 pid。</para></summary>
    public string Name { get; }

    /// <summary>这个用户名的字符里，管道路径不收的那些换成短横 —— 域账号那种带反斜杠的
    /// 名字会让构造直接抛，抛在 <c>_Ready</c> 里就是产品起不来。名字是静态算一次的那一份，
    /// 测量程序那边照同一个式子算出来就是同一个。</summary>
    private static string MakeName()
    {
        // 写成 System.Environment：这一份也 `using Godot`，而 Godot 也有一个 Environment。
        var clean = new StringBuilder(System.Environment.UserName.Length);
        foreach (var c in System.Environment.UserName)
            clean.Append(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
        return $"keeper-probe-{clean}-{System.Environment.ProcessId}";
    }

    /// <summary>进来那一头：两个都现问，不缓存。理由和 <see cref="Shots.Hooks"/> 一样 ——
    /// 日历窗会被重建、遮罩按屏起落，缓存一份就等于把「这一发问的是哪一扇」定死在组装那一刻。
    /// 走的是两个 getter，不是把窗递进来 —— 递进来的活对象在这一族里一律不出门。</summary>
    internal sealed record Hooks(
        Func<Calendar?> Calendar,
        Func<IReadOnlyList<Fader>> Faders);

    private readonly Hooks _hooks;
    private NamedPipeServerStream? _server;

    /// <summary>「还在等连上」那个任务。没连上的时候存在，连上了就置 null。
    /// **每帧不再新建一个** —— 一根 60 帧的新任务就是产品那一帧多做的一件事。</summary>
    private Task? _waiting;

    /// <summary>这一刻是连着的。<c>true</c> 而下一帧读到「不连着」，就是那一头走了，
    /// 那时要先把口关掉再重新开一个 —— 这是工单 05「测量程序退出之后产品要立刻把口关掉」那一条。</summary>
    private bool _connected;

    /// <summary>那一次一直挂着的读。<b>管道没有 <c>Available</c> 那一问</b>
    /// （<c>PipeStream</c> 上没有这个成员，试过：编译报「未包含 Available 的定义」），
    /// 而带超时的 <c>Read</c> 就是把主循环按住 —— 按住的每一毫秒都是产品出故障。
    /// 所以改成挂一个异步读，每帧只问它完没完：完了就取走那几个字节，没完就直接回。
    /// 这就是工单 05 那句「没数据就返回」在管道上的做法。</summary>
    private Task<int>? _reading;

    /// <summary>凑行的那几字节。一次 <see cref="Pump"/> 最多读这么多个（见 <see cref="_chunk"/>）。</summary>
    private readonly List<byte> _pending = [];

    /// <summary>一帧里最多读这么多字节。管道一次能递几百 KB，一次读干净就是产品那一帧被
    /// 一条问句占住 —— 所以宁可分几帧读完。</summary>
    private readonly byte[] _chunk = new byte[512];

    /// <summary>凑不齐一整行的那些字节留到什么时候。测量程序死了又不发换行的话，无限长，
    /// 那一个句柄就慢慢吃内存 —— 所以超过这一份就把这一次的会话丢掉（丢掉的是那一头，
    /// 不是产品）。</summary>
    private const int PendingCap = 4096;

    /// <summary>一帧里最多回几句。同样是为了「口开着的时候产品那一帧不多做别的事」。</summary>
    private const int AnswersPerFrame = 32;

    /// <summary>回一句话最多等多少毫秒。到点就当这一头断了，产品照跑。</summary>
    private const int WriteTimeoutMilliseconds = 2000;

    // ---- 那一帧的耗时（工单 05 判据：口开着和关着，产品那一帧的耗时差要读出来）----

    /// <summary>报一次帧耗时的间隔。太密日志自己就成了负担。</summary>
    private static readonly TimeSpan CostReportInterval = TimeSpan.FromSeconds(10);

    private long _pumpTicks;
    private int _pumpSamples;
    private long _frameTicks;
    private int _frameSamples;
    private DateTime _costReportedAt = DateTime.UtcNow;

    /// <summary>产品每一帧把自己的耗时交过来。<b>只在口开着的时候才有人调这一句。</b>
    /// 交的是 <paramref name="ticks"/>（<c>Stopwatch</c> 的刻度），换算在这一处做。
    ///
    /// <para><b>交上来的是「那一帧从进来到出去」的整段时间</b>，口自己花的那一段含在里面。
    /// 要「口的开销」就把两个数相减 —— 别拿其中一个当另一个。</para></summary>
    internal void SampleFrame(long ticks)
    {
        _frameTicks += ticks;
        _frameSamples++;
    }

    /// <summary>口开着的时候，这一段时间里口花了多少。
    /// <para>交的是这一段的总刻度和帧数，差值<b>不是</b>「口那一帧的耗时」——
    /// 它含着这一帧里别的活。所以报出去的时候说清是哪一件。</para></summary>
    private void ReportCost()
    {
        var now = DateTime.UtcNow;
        if (now - _costReportedAt < CostReportInterval) return;
        _costReportedAt = now;
        if (_pumpSamples == 0) return;

        double pumpUs = _pumpTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / _pumpSamples;
        string frame = _frameSamples == 0
            ? "那一帧的耗时这一段没有样本（口开着但产品没交帧耗时）"
            : $"那一帧（口自己花的那段含在里面）平均 {_frameTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _frameSamples:0.000} 毫秒（{_frameSamples} 帧）";
        Log("port", $"口开着：Pump 平均 {pumpUs:0.0} 微秒（{_pumpSamples} 帧）；{frame}");
        _pumpTicks = 0;
        _pumpSamples = 0;
        _frameTicks = 0;
        _frameSamples = 0;
    }

    private ProbePort(Hooks hooks)
    {
        _hooks = hooks;
        Name = MakeName();
    }

    /// <summary>开这一道口。带旗子才走到这里 —— 开不出来（管道名被占）就答「没开」，
    /// 返回 null，产品照跑：口崩了不许拖垮产品。</summary>
    public static ProbePort? Open(Hooks hooks)
    {
        var port = new ProbePort(hooks);
        if (!port.OpenPipe())
        {
            port.Dispose();
            return null;
        }
        Log("port", $"口开上了：{port.Name}（旗子 {Flag}，进程 {System.Environment.ProcessId}）");
        return port;
    }

    /// <summary>建一个管道实例并开始等。**每帧只问一次「开出来了没有」**，
    /// 开着的时候它立刻就回，不会把产品卡住。</summary>
    /// <summary>开不出来的时候隔多久再试一次。<b>不设上限的话，产品那一帧每帧多一次
    /// 构造加一行日志</b> —— 一分钟六十行，日志自己就成了负担，而口还是没开。
    /// 报一次就够：开不成的理由不会自己变。</summary>
    private static readonly TimeSpan OpenRetryInterval = TimeSpan.FromSeconds(5);

    private DateTime _openTriedAt = DateTime.MinValue;

    /// <summary>建一个管道实例并开始等。<b>没开出来不立刻再来</b>：
    /// 报一次、等 <see cref="OpenRetryInterval"/> 再报一次。</summary>
    private bool OpenPipe(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _openTriedAt < OpenRetryInterval) return false;
        _openTriedAt = now;
        try
        {
            // MaxAllowedServerInstances：宁可多留几个名额，也不要构造时因为名额被占而等。
            // 等就是阻塞，阻塞就是产品出故障。
            _server = new NamedPipeServerStream(
                Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            return true;
        }
        catch (IOException)
        {
            Log("port", $"口没开成：{Name} 被占着（这一跑不带口，照跑；每 5 秒再试一次）");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Log("port", $"口没开成：{Name} 这一份没权限建（这一跑不带口，照跑；每 5 秒再试一次）");
            return false;
        }
    }

    /// <summary>每帧调一次。<b>三档，一档都不等</b>：没连上就问一次连上没有，
    /// 连上了但手上没字节就直接回，有字节才凑行、才回答。</summary>
    public void Pump()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // **这一道口不许把异常漏到产品的帧里。** 漏出去一次，Godot 就把它当未处理异常
        // 打进 stderr，而它每帧漏一次就是每秒几百行 —— 日志自己成了负担，
        // 产品还在跑，可那道口从此半死不活（量于 10-12：`Pipe is broken` 每帧一次）。
        // 口崩了不许拖垮产品，所以在这里兜住，并且**只报一次**：
        // 同一件事报几百遍，读数里就没有信息了。
        try
        {
            PumpOnce();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_reportedPumpFault)
            {
                _reportedPumpFault = true;
                Log("port", $"口这一帧出了 {ex.GetType().Name}（{ex.Message}），这一段照跑；" +
                             "同一件事只报这一次");
            }

            _connected = false;
            _pending.Clear();
            _waiting = null;
        }

        _pumpTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _pumpSamples++;
        ReportCost();
    }

    /// <summary>同一件事报过一次就别再报。</summary>
    private bool _reportedPumpFault;

    private void PumpOnce()
    {
        if (_server == null && !OpenPipe()) return;

        // **一根管子上不能并发起两个操作。**「等连上」那个 overlapped 操作还挂着的时候，
        // 对同一根管子发起读，两个操作会互相踩。踩出来的样子是
        // 「客户端连上了、写进去了、就是没人答」（量于 10-12，第一发端到端）。
        // 所以每帧第一件事是把上一帧挂着的那个任务收掉：完了就完了，坏了也收掉。
        Settle();

        if (!_server!.IsConnected)
        {
            if (_connected)
            {
                // 上一帧还连着、这一帧不连着了：那一头走了。
                // **这一根实例用完了**（实测：一根只服务一头，那一头走之后在同一根上再挂
                // 「等连上」，任务会立刻回来而 `IsConnected` 是 false）。所以直接换一根，
                // 不在用完的实例上再等 —— 顺带那一句「这个实例接不了下一头」的日志
                // 也就不必再留着冒充连接失败。
                _connected = false;
                _pending.Clear();
                Log("port", "那一头走了，换一根管子继续等");
                ReplacePipe();
                if (_server == null) return;
            }

            StartWaiting();   // 挂上等连上，**不等它连上**
            return;            // 没连上就这一帧到此为止
        }

        if (!_connected)
        {
            _connected = true;
            Log("port", "测量程序接上了");
        }

        // 「有没有字节到手」问的是那一次一直挂着的异步读：完了才取走，没完这一帧就到此为止。
        // 取走之后再挂下一次，所以一帧最多读一段 —— 管道一次能递几百 KB，
        // 一次读完就是产品那一帧被一条问句占住。
        var server = _server;
        _reading ??= server.ReadAsync(_chunk, 0, _chunk.Length);
        if (!_reading.IsCompleted) return;
        var finished = _reading;
        _reading = null;

        int got;
        try
        {
            // 这一句取的是已经完好的那个任务，不等它 —— 不阻塞就是这里唯一的判据。
            got = finished.GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            _connected = false;   // 那一头断了：下一帧回到等连上，产品照跑
            _pending.Clear();
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (got <= 0)
        {
            _connected = false;   // 读到 0 字节 = 那一头把管子关了；下一帧走「回到等连上」那一档
            _pending.Clear();
            return;
        }
        _pending.AddRange(_chunk[..got]);

        // 一帧里最多回几句，剩下那一堆下一帧再回。
        for (var answered = 0; answered < AnswersPerFrame; answered++)
        {
            var at = _pending.IndexOf((byte)'\n');
            if (at < 0) break;
            // 按 0x0A 切是安全的：它不是任何多字节序列里会出现的那几个字节之一，
            // 所以切出来的每一段都是完整的一行，不会切在半个汉字上。
            var line = Encoding.UTF8.GetString(_pending.GetRange(0, at).ToArray());
            _pending.RemoveRange(0, at + 1);
            if (!Write(Answer(line.TrimEnd('\r')))) break;   // 回不动了就到此为止，不接着回第二句
        }

        if (_pending.Count > PendingCap)
        {
            Write("err 这一行太长，这一回不算");
            _pending.Clear();
        }
    }

    /// <summary>关掉这一道口。<b>退出时由 <see cref="App"/> 调一次</b> —— 三条退出路
    /// （托盘的「退出」、<c>--seconds</c>、Esc）都走同一个 <c>Quit</c>，所以一句就够。
    ///
    /// <para>工单 05 有一行判据是「测量程序退出之后管道实例数回零」——
    /// <b>那一行回不去</b>：客户端断开不等于管道该关，而真正回零的是退出这一下。
    /// 记在工单 05 的 Comments 里。</para></summary>
    public void Dispose()
    {
        _waiting = null;
        _connected = false;
        _pending.Clear();
        ClosePipe();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    /// 把上一帧挂着的「等连上」那个任务收掉。完了、坏了，都收掉 ——
    /// <b>留着不管就是让它和这一帧的读并发起在同一根管子上</b>。
    ///
    /// <para>只看 <c>IsCompleted</c> 会把 faulted 也当成「连上了」，
    /// 所以这里把状态分开记。</para></summary>
    private void Settle()
    {
        var waiting = _waiting;
        if (waiting == null || !waiting.IsCompleted) return;
        _waiting = null;
        if (waiting.IsFaulted)
        {
            var why = waiting.Exception?.InnerException ?? waiting.Exception;
            Log("port", $"等连上那个任务带异常回来了（不算「接上了」）：{why?.GetType().Name}：{why?.Message}");
        }
    }

    /// <summary>挂上「等连上」。<b>挂上就回，不等它连上</b> ——
    /// 等就是阻塞，阻塞就是产品出故障。挂的时候抛的那一族就是这根实例不接这一档，
    /// 换一根再挂。
    ///
    /// <para><b>要接住的是两族，不是一族</b>（量于 10-12）：
    /// 实例刚被断开的时候抛的是 <c>IOException: Pipe is broken</c>，
    /// 挂不上的时候抛的是 <c>InvalidOperationException</c>。
    /// 漏掉前者的话，它每帧逃到 <c>_Process</c> 一次，
    /// 产品的帧被打穿、日志被刷爆，而那道口从此再也接不上 ——
    /// 症状是「口明明开着，量具答接不上」。</para></summary>
    private void StartWaiting()
    {
        if (_waiting != null || _server == null) return;
        try
        {
            _waiting = _server.WaitForConnectionAsync();
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ReportOnce(ex);
        }

        // 换一根再挂。**换的时候要强试**：平时那 5 秒的重试间隔是为了不让
        // 产品每帧多做一次构造，而这里是「上一根已经坏了」，等 5 秒就是让口白死 5 秒。
        _waiting = null;
        ReplacePipe();
        if (_server == null) return;
        try
        {
            _waiting = _server.WaitForConnectionAsync();
        }
        catch (Exception retry) when (retry is InvalidOperationException or IOException)
        {
            ReportOnce(retry);
        }
    }

    /// <summary>报一次「这一根管子不接了」，5 秒之内不重复报 ——
    /// 这个状态是每帧都在的，一次报一遍等于一秒一行，日志自己就成了负担。</summary>
    private void ReportOnce(Exception why)
    {
        var now = System.DateTime.UtcNow;
        if (now - _lastPipeFaultAt < System.TimeSpan.FromSeconds(5)) return;
        _lastPipeFaultAt = now;
        Log("port", $"这一根管子挂不上等连上了（{why.GetType().Name}：{why.Message}），换一根再说");
    }

    private System.DateTime _lastPipeFaultAt = System.DateTime.MinValue;

    /// <summary>换一个管道实例。<b>先把新的建起来，再把旧的交回去。</b>
    ///
    /// <para><b>实测出来的那一档</b>：一个实例只服务一头。那一头断开之后，
    /// <c>WaitForConnectionAsync</c> 会<b>立刻</b>回来（底下那次 <c>ConnectNamedPipe</c>
    /// 撞上「已经连过」那一档），而同一时刻 <c>IsConnected</c> 读出来是 false ——
    /// 不认这一档的话，口接完第一头就再也不接第二头（每一帧问一次、每一帧答「没有」，
    /// 量具那边只当产品挂了）。</para>
    ///
    /// <para><b>先建后关，不反过来</b>：先关后建会空出小半帧，正好够下一个客户端接上一个
    /// 正在被关掉的实例 —— 实测过那么写，它拿到的是一根断管子（<c>Pipe is broken</c>）。
    /// 新的没建出来就把旧的放回去（它虽然断了，但名字还在那儿），下一帧再试；
    /// 这一档不许把口整个关掉：名字没了，量具那边只会看到「找不到管道」。</para></summary>
    private void ReplacePipe()
    {
        var old = _server;
        // 挂在旧管子上的那个读也要带走：留着它，下一帧就会去问一个已经交回去的管子读到了什么。
        if (_reading is { IsCompleted: false } reading)
        {
            _ = reading.ContinueWith(static t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        _reading = null;

        if (OpenPipe(force: true))
        {
            Log("port", "换一根管子继续等");
            Drop(old);
            return;
        }
        _server = old;
        _waiting = null;
    }

    /// <summary>把一个实例的句柄交回去。断了的那一头照样交 —— 交不回去才是漏。</summary>
    private static void Drop(NamedPipeServerStream? server)
    {
        if (server == null) return;
        try { server.Dispose(); }
        catch (IOException) { /* 那一头已经没了，句柄照样交回去 */ }
    }

    private void ClosePipe()
    {
        // 挂着的那个读在管子被关掉的时候会带一个异常回来。没人去取它就是一次没被观察的
        // 异常 —— 在这里挂一个只收尾的延续，让它有地方落。
        if (_reading is { IsCompleted: false } pending)
            _ = pending.ContinueWith(static t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        _reading = null;

        // 等连上那个任务同样会带异常回来。`WaitForConnectionAsync` 在管子被换掉时
        // 抛的是 `InvalidOperationException`，不是 IO 异常 —— 不收尾就是一次没被观察的异常。
        if (_waiting is { IsCompleted: false } connect)
            _ = connect.ContinueWith(static t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        _waiting = null;

        var server = _server;
        _server = null;
        if (server == null) return;
        try { server.Dispose(); }
        catch (IOException) { /* 那一头已经没了，句柄照样交回去 */ }
    }

    /// <summary>回一句。答不出、管道断了、回不了 —— 三件事都只答一句就完，**不回第二句**：
    /// 断了以后还接着往一个已经不存在的管子上写，那是把「测量程序那边断了」变成「产品这边崩了」。
    /// 答出去的东西是句柄、矩形、数、字符串，一个活对象都不带。</summary>
    private bool Write(string reply)
    {
        var server = _server;
        if (server == null) return false;
        var bytes = Encoding.UTF8.GetBytes(reply + "\n");
        try
        {
            // **写也要上限**：管道缓冲是有限的那一块，一个只发不收的测量程序迟早把它填满，
            // 而填满之后 `Write` 就在等对端读 —— 那一下按住的是产品的主循环。
            //
            // <para><b>但这一句要先问 <c>CanTimeout</c></b>（量于 10-12）：
            // 这根管子是带 <c>PipeOptions.Asynchronous</c> 建的，不支持超时，
            // 设 <c>WriteTimeout</c> 会抛 <c>InvalidOperationException</c> ——
            // 而下面那个 catch 正好把它当成「管子已经没连上」吞掉，
            /// 于是<b>一个字都回不去</b>，量具那边只看到「写进去了没人答」。
            // 设不上就不设，但不能因为设不上就不回话。</para>
            if (server.CanTimeout) server.WriteTimeout = WriteTimeoutMilliseconds;
            server.Write(bytes, 0, bytes.Length);
            server.Flush();
            return true;
        }
        catch (IOException)
        {
            _connected = false;   // 回话那一头断了：下一帧回到等连上，产品照跑
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;   // 管子已经没连上（刚被 Reopen 换掉了）：同样别再往它上面写
        }
    }

    /// <summary>一行请求换一行回答。<b>认不出的动词一律答 <c>err</c>，不静默不回</b> ——
    /// 测量程序连不上口时会自己报断，这一头不回它就只当产品挂了。</summary>
    private string Answer(string request)
    {
        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "err 这一行是空的";
        return parts[0] switch
        {
            "cell" => OnCell(parts),
            "checklist" => OnChecklist(),
            "editmode" => OnEditMode(),
            "swallow" => OnSwallow(),
            "mouse" => OnMouse(),
            _ => $"err 认不出这个动词：{parts[0]}"
        };
    }

    /// <summary><c>cell &lt;x&gt; &lt;y&gt;</c>：屏幕上那一点落在哪一格、那一格是几号、
    /// 那一格在屏上占哪一块。
    ///
    /// 「一点 → 哪一格」是 <see cref="Calendar"/> 那一族的知识，独立进程手上只有一扇窗的
    /// 句柄、从外面反推出来的是猜的（ADR-0004 那一节否掉的正是反推），所以只能问
    /// <see cref="Calendar.CellAt"/>。答不出（日历没开着、或者那一点不落在任何一格里）
    /// 一律走 <c>err</c> —— 猜一个日号填上去，测量程序那几发就全成了脏的。</summary>
    private string OnCell(string[] parts)
    {
        if (parts.Length != 3 || !int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
            return "err cell 要两个整数：屏幕坐标";
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return "err 日历没开着";
        var hit = calendar.CellAt(new Win32.Point { X = x, Y = y });
        if (hit == null) return "err 这一点不落在任何一格里";
        var (day, rect) = hit.Value;
        return $"ok day={day} rect={Rect4(rect)}";
    }

    /// <summary><c>checklist</c>：遮罩的休息清单摆完之后有几行、每一条占哪一块。
    ///
    /// 矩形是 <see cref="Fader.RowRects"/> 量出来的：控件摆完版之后它自己的那个矩形，
    /// 不是手算的布局 —— 新加一行会把那一竖排撑高、居中的容器要再居中一次。
    /// 遮罩不在的时候答 <c>n=0</c>：屏上根本没有清单，报一份清单的条数是把没摆出来
    /// 说成了摆出来了。矩形那一栏没量到时是 <c>0,0,0,0</c>，不省掉 ——
    /// 省掉就分不出「这一行是空的」和「这一行量到了」。</summary>
    private string OnChecklist()
    {
        foreach (var fader in _hooks.Faders())
        {
            if (!GodotObject.IsInstanceValid(fader)) continue;
            var rows = fader.RowRects();
            return $"ok n={rows.Count} rows={string.Join("|", rows.Select(Rect4))}";
        }
        return "ok n=0 rows=";
    }

    /// <summary><c>editmode</c>：进出编辑态没有，正在编辑的是几号。
    ///
    /// <b><c>cell</c> 那一格答的是「正在编辑的那一天」而不是「选中的那一天」</b> ——
    /// 这两个在日历里是分着记的。可是在编辑态里它们<b>恒等</b>：只有
    /// <c>BeginEdit</c> 一处把窗摆进 <see cref="CalendarHost.State.Editing"/>（走
    /// <see cref="CalendarHost.Lift"/>），它紧跟着就把
    /// 正在编辑的那一天设成选中的那一天；编辑态里点另一格走的是 <c>MoveEdit</c>，
    /// 那一段先把正在编辑的那一天追成选中的那一天。所以读得到的那一个就是答案，
    /// 不是近似。（自检那两处把窗摆进编辑态走 <see cref="CalendarHost.ForceStateForTest"/>，
    /// 是另一回事：它们自己紧跟着把两个值都设了。）
    ///
    /// 日历没开着是另一个状态，答 0 就成了「摆着没在打字」，那是把一件没发生的事说成了另一件
    /// —— 所以那时候答 <c>err</c>。</summary>
    private string OnEditMode()
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return "err 日历没开着";
        if (calendar.Host.StateNow != CalendarHost.State.Editing) return "ok editing=0 cell=0";
        return DateOnly.TryParseExact(calendar.SelectedDay, "yyyy-MM-dd", out var day)
            ? $"ok editing=1 cell={day.Day}"
            : "err 读不出正在编辑的是哪一天";
    }

    /// <summary><c>swallow</c>：遮罩收没收鼠标。
    ///
    /// 答的是 <see cref="Fader.Interactive"/> —— <b>这一步是不是那个收鼠标的一步</b>，
    /// 不是把「吞掉鼠标的那一块」那个矩形当答案递出去（那是量到哪儿，不是收没收）。
    /// 这一族还分两档：声明要鼠标的那一下在换步时，收没收是淡入走完才落的，
    /// <see cref="Fader.Interactive"/> 答的已经是后一档。多块屏就一屏一扇，
    /// 答「有没有哪一扇收着」。</summary>
    private string OnSwallow()
    {
        foreach (var fader in _hooks.Faders())
            if (GodotObject.IsInstanceValid(fader) && fader.Interactive) return "ok swallow=1";
        return "ok swallow=0";
    }

    /// <summary><c>mouse</c>：鼠标桥那三个数，加上最近一发的判据原文。
    ///
    /// <para><b>它为什么是第五个（2026-10-13 所有者定）</b>：前四个答的都是产品<b>状态</b>
    /// （哪一格、清单、编辑态、收没收鼠标），而<b>闸门开不开</b>这件事在产品内部。
    /// 工单 09 第三发量到「两发点击全被丢掉」时，唯一的出处是产品日志里
    /// <c>最近一发[…]</c> 那一行 —— 测量程序读不到它，只能去翻日志，
    /// 而翻日志拿到的那一行不带时间戳对齐，很容易和屏上那一下对不上。
    /// 这一句答出来的仍然是数、矩形和字符串，没有活对象。</para>
    ///
    /// <para><b>日历没开着是另一个状态</b>，答 <c>err</c>：桥是日历那一族的，
    /// 日历不在的时候报一串零会让「没人点」和「没有那一扇」读起来一样。</para>
    private string OnMouse()
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return "err 日历没开着";
        return "ok " + calendar.BridgeReport();
    }

    /// <summary>一块矩形写成协议里那一句 <c>&lt;l,t,r,b&gt;</c>：左、上、右、下。
    /// 右下是算出来的（位置 + 尺寸），不是再问一遍引擎 —— 同一块矩形有两个数法的时候，
    /// 抄出来的那一份和算出来的那一份早晚会差一个像素。</summary>
    private static string Rect4(Rect2I r) =>
        $"{r.Position.X},{r.Position.Y},{r.Position.X + r.Size.X},{r.Position.Y + r.Size.Y}";

    /// <summary>写一行日志到 App 那一份（<see cref="LogWriter.ToApp"/>）。</summary>
    private static void Log(string step, string message) => LogWriter.ToApp(step, message);
}
