using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DebugProbe;

/// <summary>
/// 产品主动开的那一道口。<b>测量程序从外面问它那几句只有产品答得出的问。</b>
///
/// <para>口是产品主动开的，测量程序不负责开。接不上就报断，**不许静默跳过那一发** ——
/// 静默跳过会让「没量到」和「量到了没问题」在读数上长得一模一样。</para>
///
/// <para>协议是行文本，五个动词（<c>cell</c> / <c>checklist</c> / <c>editmode</c> / <c>swallow</c> /
/// <c>mouse</c>，2026-10-13 所有者定加的第五个）。名字里带<b>用户名和产品的进程号</b> ——
/// 两个都带旗子的实例各供各的一根，量具按在跑的产品进程名单挨个试，
/// 认得出「这是我起的那一个」，不会问到另一个实例上去。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/05-product-opens-the-port.md</c>，
/// 取向在 <c>docs/adr/0004-where-the-engine-stops.md</c>
/// 「桌面量测工具开在产品进程里的那一道口」那一节。</para>
/// </summary>
internal sealed class Port : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private Port(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>产品的进程号。名字末尾那个数就是它 —— 这一个答得出「问的是哪一个实例」。</summary>
    internal static int ConnectedPid { get; private set; }

    /// <summary>接上的那根管路的名字。</summary>
    internal static string ConnectedName { get; private set; } = string.Empty;

    /// <summary>
    /// 挨个试过的每一根管路，一根一条。接不上的时候<b>这一句就是全部事实</b> ——
    /// 只报「接不上」会把「没带旗子」和「带了但进不去」抹成同一件。
    /// </summary>
    internal static string Attempts { get; private set; } = string.Empty;

    /// <summary>
    /// 那一根管路的名字。产品那一侧用同一个式子拼出来，末尾那个数是它自己的进程号。
    ///
    /// <para><b>用户名要洗一遍</b>：管道路径不收的那些字符换成短横，产品那一侧照同一个式子换。
    /// 两边换得不一样的话，域账号那种带反斜杠的名字会拼出两根不一样的管子，
    /// 症状是「口明明开着，量具说接不上」—— 而那一件最难查。</para>
    /// </summary>
    internal static string PipeNameFor(int productPid) => $"keeper-probe-{CleanUserName()}-{productPid}";

    private static string CleanUserName()
    {
        string user = Environment.UserName;
        var clean = new StringBuilder(user.Length);
        foreach (char c in user)
        {
            clean.Append(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
        }

        return clean.ToString();
    }

    /// <summary>一问等多久。到点没答上来就算断。</summary>
    private const int ReadTimeoutMilliseconds = 3000;

    /// <summary>
    /// <b>不能靠 <c>ReadTimeout</c> 给读设上限</b>（量于 2026-10-12）：
    /// 第一次接上这根管子时 <c>NamedPipeClientStream.ReadTimeout</c> 抛
    /// <c>InvalidOperationException: Timeouts are not supported on this stream</c>，
    /// 而 <c>CanTimeout</c> 读出来是 false —— 那一条走不通。
    ///
    /// <para>所以上限由<b>等</b>来给，不是由流来给：读挂到线程池上，这里到点不等了。
    /// 到点就当断、这一发不算完成 —— 而「测量程序卡住」和「产品答不出来」
    /// 在读数上必须分得开。</para>
    /// </summary>
    private const int ReplyWaitMilliseconds = 3000;

    /// <summary>一根管子等多久。<b>是每一根各自的时限</b>：名单上有几个产品进程就等几遍，
    /// 所以它不能按「总共等多久」给。名字不存在时 <c>Connect</c> 只会到点抛异常，
    /// 没有「一查就知道在不在」的那一问。</summary>
    private const int PerPipeTimeoutMilliseconds = 1000;

    /// <summary>接不上的时候是哪一件事实。<b>三件不一样的事，读数上不许长得一样</b>：
    /// 产品没开那道口 / 口开在那儿但连不上 / 连上了之后断了。</summary>
    internal static string LastFailure { get; private set; } = string.Empty;

    /// <summary>
    /// 挨个产品进程试，接上第一个答得出的就返回。接不上返回 null ——
    /// 那不是失败，是「没接上」这一件事实，理由全在 <see cref="Attempts"/> 里。
    /// </summary>
    internal static Port? Open()
    {
        List<int> pids = ProductProcess.RunningProductIds();
        if (pids.Count == 0)
        {
            Attempts = "一个产品进程也没有。" +
                       "「没有产品在跑」和「产品在跑但没带 --probe-port」是两句不同的话，" +
                       "这一发分不开，所以只答前者";
            LastFailure = Windows.IntegrityLevel();
            return null;
        }

        var log = new List<string>();
        foreach (int pid in pids)
        {
            string name = PipeNameFor(pid);
            NamedPipeClientStream? pipe = null;
            try
            {
                // PipeOptions 留空：读用同步那一套，开异步那一套会让同步的 ReadLine 卡住。
                // 那一问一问一句，不值得为它引一套异步泵。
                pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
                pipe.Connect(PerPipeTimeoutMilliseconds);
                log.Add($"{name}：接上了（CanTimeout={pipe.CanTimeout}）");
                Attempts = string.Join("｜", log);
                LastFailure = string.Empty;
                ConnectedPid = pid;
                ConnectedName = name;
                return new Port(pipe);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // 连不上也要把句柄交回去 —— 漏一个句柄，量具连发几发就攒出几个。
                pipe?.Dispose();
                log.Add($"{name}：{Describe(ex)}");
            }
        }

        Attempts = string.Join("｜", log);
        LastFailure = Windows.IntegrityLevel();
        return null;
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}（Win32 错误码 {Marshal.GetLastWin32Error()}）";

    /// <summary>问一句，答一句。断了答「断」，不抛出去。</summary>
    internal string Ask(string verb, params string[] args)
    {
        try
        {
            _writer.WriteLine(args.Length == 0 ? verb : verb + " " + string.Join(" ", args));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Disconnect = true;
            return "断（写不出去，口已经断了）";
        }

        string? line;
        try
        {
            // 读挂到线程池上，上限由这里等出来。见 <see cref="ReplyWaitMilliseconds"/> 那段。
            // 到点不等的那一次读还挂在管道上 —— 所以到点之后立刻当断，
            // 这一发不再问下一句：留着一条没读完的读去问下一句，读数会串。
            var reading = Task.Run(() => _reader.ReadLine());
            if (!reading.Wait(ReplyWaitMilliseconds))
            {
                Disconnect = true;
                return $"断（问「{verb}」之后 {ReplyWaitMilliseconds} 毫秒没答上来）";
            }

            line = reading.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is IOException or ObjectDisposedException)
        {
            Disconnect = true;
            return "断（管道读出错，或产品那边把口关了）";
        }

        if (line == null)
        {
            Disconnect = true;
            return "断（产品那一头把口关了）";
        }

        return line;
    }

    /// <summary>这一问之后口是不是断了。断了就不要再问下去。</summary>
    internal bool Disconnect { get; private set; }

    /// <summary>从 <c>ok 喂=12 点击=6 丢=34 …</c> 这一行里抠出一个数。
    /// 抠不出来答 -1，<b>不拿 0 顶替</b> —— 0 是一个合法的数，
    /// 而「没抠到」和「真的是 0」在读数上必须分得开。</summary>
    internal static int Counter(string line, string key)
    {
        int at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return -1;
        var digits = new System.Text.StringBuilder();
        for (int i = at + key.Length; i < line.Length && char.IsDigit(line[i]); i++) digits.Append(line[i]);
        return digits.Length == 0 ? -1 : int.Parse(digits.ToString());
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
    }
}

/// <summary>
/// 子命令 <c>ask</c>：把五个动词挨个问一遍。
///
/// <para>五个动词都在 <see cref="Port"/> 的注释里。答出来的是句柄、矩形、数、字符串
/// —— <b>没有活对象</b>：测量程序拿不到 <c>Calendar</c>，也拿不到任何控件本身。</para>
/// </summary>
internal static class Ask
{
    internal static int Run()
    {
        using Shot shot = Shot.Begin(
            "过那道口",
            "五个动词挨个问一遍 —— 日历那一格是哪一格、遮罩清单每一条的矩形、进没进编辑态、遮罩收没收鼠标、鼠标桥喂了几发；" +
            "接不上就报断，不许静默跳过这一发");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish("中止 —— 找不到日历那一扇，那一点从哪儿来就答不出来", "无");
            return Find.Refused;
        }

        // 问的那一点是日历矩形的中心 —— 从量到的那一扇推出来，不写死。
        // 「哪一格」那一问反推不出来（ADR-0004 那一节否掉的正是反推），所以它得是产品答。
        int cx = calendar.Rect.Left + calendar.Rect.Width / 2;
        int cy = calendar.Rect.Top + calendar.Rect.Height / 2;

        using Port? port = Port.Open();
        if (port == null)
        {
            shot.Finish(
                $"断 —— 挨个试过在跑的产品进程，一道都没接上。试过的管路：{Port.Attempts}。" +
                $"本进程完整性级别 {Port.LastFailure}。" +                "产品那一侧要么没起、要么没带 --probe-port，要么口开在那儿但这一头进不去；" +
                "产品那一侧的状态答不了，这一发不算完成",
                "无");
            return Find.Refused;
        }

        var answers = new List<string>();
        string cell = port.Ask("cell", cx.ToString(), cy.ToString());
        answers.Add($"日历中心 ({cx},{cy}) 那一格：{cell}");
        if (port.Disconnect) return Broken(shot, answers, cell);

        string checklist = port.Ask("checklist");
        answers.Add($"遮罩清单：{checklist}");
        if (port.Disconnect) return Broken(shot, answers, checklist);

        string edit = port.Ask("editmode");
        answers.Add($"编辑态：{edit}");
        if (port.Disconnect) return Broken(shot, answers, edit);

        string swallow = port.Ask("swallow");
        answers.Add($"遮罩收没收鼠标：{swallow}");
        if (port.Disconnect) return Broken(shot, answers, swallow);

        string mouse = port.Ask("mouse");
        answers.Add($"鼠标桥：{mouse}");
        if (port.Disconnect) return Broken(shot, answers, mouse);

        shot.Finish(
            $"管道 {Port.ConnectedName} 接上了（问的是产品进程 {Port.ConnectedPid}，" +
            $"试过的管路：{Port.Attempts}）；五个动词都答了 —— {string.Join("｜", answers)}。" +
            "答出来的全是数、矩形和字符串，没有活对象",
            "无");

        return 0;
    }

    private static int Broken(Shot shot, List<string> answers, string last)
    {
        shot.Finish(
            $"断 —— 问完「{last}」之后口断了。接的那一根：{Port.ConnectedName}" +
            $"（产品进程 {Port.ConnectedPid}），接的时候：{Port.Attempts}。" +
            $"已答的那几句：{string.Join("｜", answers)}。这一发不算完成",
            "无");
        return Find.Refused;
    }
}