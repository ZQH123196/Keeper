using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>一组判据自己那一圈机器：那个用完就删的临时目录，和把异常收成一条问题的
/// <see cref="Check"/>。
///
/// <b>为什么要有这个类</b> —— 原来这两样每一组各抄一遍：<c>Check</c> 那四行抄了 27 处，
/// 「临时目录三行」抄了 8 处（2026-10-07 搬之前数出来的；搬完是 43 处调用点在用这一份）。
/// 抄的那一行 <c>catch (Exception e) { problems.Add($"{what}：{e.Message}"); }</c>
/// 逐字相同，所以<b>有一组没抄上就等于那一组的异常冲出去</b>：
/// <c>CalendarNotes</c> 那一组就没包 <c>Check</c>，一个 <c>FileNotFoundException</c>
/// 冲出整组、冲出整个 <c>--check</c>，后头三十几组一条都没报，而它自己那半句注释
/// 记着的正是这件事。<b>「有没有包着」当时靠记性，现在靠类型</b> —— 要写判据就得先有一个
/// <see cref="Checks"/>，没包 <c>Check</c> 的写法在这份文件里不存在。
///
/// <b>接口是这几个成员</b>：造（带临时目录 / 纯算术 / 沿用一份已有的）、<see cref="Check"/>、
/// <see cref="Fail"/>、<see cref="Problems"/>、<see cref="Report"/>，加上一个
/// <see cref="CheckTyped"/>（<b>只有一处用</b>，那一处量的是「哪一步炸了」，见它自己那段注释）。
/// 那一圈里剩下的都是各组自己的判据，不该搬走。
///
/// <b>抄走的两样都带各自的判断</b>：临时目录名带进程号（多发自检并行时不打架），
/// 删之前先删一次（上一发崩了留了半个目录时照样起得来）。
/// <see cref="Report"/> 把「删不掉」收成一条问题而不是咽下去 ——
/// 咽下去的话磁盘上会留一串 <c>keeper-*-selfcheck-*</c> 而没人知道。
///
/// 它和 <see cref="SelfCheck"/> 不是一层：<see cref="SelfCheck"/> 是<b>外面</b>那一层
/// （有哪几组、每组跑一次、退出码），这一份是<b>一组里面</b>的机器。</summary>
internal sealed class Checks
{
    private readonly List<string> _problems = [];
    private readonly string? _sandbox;

    /// <summary>那一组用的临时目录。<b>问它要，不要自己拼</b> —— 拼出来的那一份没有那个
    /// 进程号，两次跑就会共用一个目录（那不是省事，是两组的文件互相看见）。</summary>
    public string? Root { get; }

    /// <param name="sandbox">这一组叫什么（不带前缀不带后缀）。给 null 就是纯算术那一组，
    /// 它不碰磁盘、没有目录要删。<b>不要传两个字面相同的</b> —— 同一进程里两份同名会共用
    /// 一个目录，那不是省事，是两组的判据互相看见对方的文件。</param>
    public Checks(string? sandbox = null)
    {
        if (sandbox == null) return;
        _sandbox = Path.Combine(Tmp.Root, $"keeper-{sandbox}-selfcheck-{System.Environment.ProcessId}");
        // 上一发崩在中间时留的半个目录：先删一次，删不掉就当没有（下面 CreateDirectory
        // 撞上会说它自己那份话）。
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
        Directory.CreateDirectory(_sandbox);
        Root = _sandbox;
    }

    /// <param name="existing">**沿用**一份已经攒着的列表，不是把它抄一份下来。
    ///
    /// 什么时候用这一档 —— 那一组的前半截已经交出它自己的那份问题表（<c>Options.SelfTest</c>
    /// 的审计就是），而后半截的判据要往<b>同一个对象</b>里添。<b>抄一份的话收尾（<c>finally</c>）
    /// 那几行会漏掉</b> —— 而那几个 <c>return</c> 交出去的是「那个对象」，不是「它当时有几条」。
    /// 不要拿它当「导入外部列表」的通用口子：那是两份事实，那属于别处的决定。</param>
    public Checks(List<string> existing)
    {
        _problems = existing;
    }

    /// <summary>一条判据。<b>抛异常记成一条问题，不冲出去</b> —— 这一组后面还有判据，
    /// 冲出去等于后面那几十组一条都不报，而退出码仍是 0。</summary>
    public void Check(string what, Action body)
    {
        try { body(); }
        catch (Exception e) { _problems.Add($"{what}：{e.Message}"); }
    }

    /// <summary>同上，但问题那一句前面带上异常的<b>类型名</b>。
    ///
    /// <b>什么时候用这一档</b> —— 这一组要断的正是「哪一步炸了」：窗那一族的问题多半在
    /// 节点路径和类型上，只印 <c>e.Message</c> 的话两种原因给同一句话
    /// （<c>OptionsHost</c> 那一组是唯一一处，它量的就是「开窗那一段次序对不对」，
    /// 报出来要能一眼看出是 <c>NodeNotFound</c> 还是别的）。
    /// 别处用默认那一档就够。</summary>
    public void CheckTyped(string what, Action body)
    {
        try { body(); }
        catch (Exception e) { _problems.Add($"{what}：{e.GetType().Name} {e.Message}"); }
    }

    /// <summary>一条不是由抛异常来的问题（形状对但读回来的值不对那一类）。</summary>
    public void Fail(string problem) => _problems.Add(problem);

    /// <summary>那一份问题表本身，给「不成一条判据、只是一句判断」的那几行用
    /// （<c>Scheduler.SelfTest</c> 的出厂设置对账是那一类：它自己 <c>Add</c>，不抛异常）。
    /// <b>用这一份而不是另开一个 <c>List</c></b> —— 两个表的话 <see cref="Report"/>
    /// 只认得其中一个。</summary>
    public List<string> Problems => _problems;

    /// <summary>收尾：临时目录删掉，问题交出去。<b>这一组能跑几遍</b>。
    /// 删不掉收成一条问题，不咽下去。</summary>
    public List<string> Report()
    {
        if (_sandbox != null)
        {
            try { Directory.Delete(_sandbox, recursive: true); }
            catch (IOException e) { _problems.Add($"临时目录没删干净 {_sandbox}: {e.Message}"); }
        }
        return _problems;
    }
}

/// <summary>一组判据和<b>它自己那一句话</b>（工单 03）。
///
/// <b>为什么要有这个类型</b>：原来「这组量了什么」那句话是一个传进
/// <see cref="SelfCheck"/> 的字面量，写在 <c>Probe.cs</c> 里 —— 判据改了它不跟着改，
/// 而<b>编译器管不着</b>：判据删掉一条断言，那句话照样印「它量过了」。这正是
/// <c>CODING_STANDARDS.md</c> 取证方法论第 6 条那一条病（判据里不许存一份会腐烂的副本）
/// 在自检这一族上的样子。收进这个类型之后，<b>那句话和判据住在同一条声明里</b>，改一处就跟着改。
///
/// <b>为什么绿话是「读取」而不是字符串</b>：一句话里引的数（键的个数、那个毫秒）
/// 必须现问，不许抄一份。手抄的那个数就是同一种腐烂 —— 抄的时候它对，改了之后自检还绿。
/// <c>settings</c>、<c>activity</c> 那两组原本已经在现问，这个类型让那一档成为<b>唯一</b>的一档：
/// <b>抄一个数进来编译器不报，抄错了自检照样绿。</b>所以：<b>一句话里要引数，就写
/// <c>() =&gt; $"…{X.Count}…"</c></b>，不要写死一个数。
///
/// <b>红话为什么也在里面</b>：它是给失败的人看的半句主语，讲的是同一组。
/// 拆成两处就要在两处各写一遍那组叫什么，而那正是这一族要收的那条。
/// （主语和绿话的主语<b>不总是同一个词</b>：「调度模拟通过」对「调度状态机有 N 条问题」——
/// 合成一个会改实测结果，所以两个字段都留着。）
///
/// <b>它和 <see cref="SelfCheck"/> 不是一层</b>：<see cref="SelfCheck"/> 管的是
/// 有哪几组、跑几遍、退出码；这一份管的是<b>一组是什么</b>。组名不在这儿 ——
/// 它是登记次序和日志那一列的键，留在 <c>Probe.cs</c> 的登记链上。</summary>
internal abstract class Criterion
{
    protected Criterion(Func<string> green, string red, Func<string>? suffix)
    {
        Green = green ?? throw new ArgumentNullException(nameof(green));
        Red = red;
        Suffix = suffix;
    }

    /// <summary>这一组量到了什么。<b>要引数就现问</b>（见类型注释那一段）。</summary>
    public Func<string> Green { get; }

    /// <summary>没过时那半句的主语，跟 <see cref="Green"/> 的主语不总是同一个词。</summary>
    public string Red { get; }

    /// <summary>不管过没过都追在后面的现问实测结果（<c>wallpaper-source</c>、<c>presets-templates</c> 在用）。</summary>
    public Func<string>? Suffix { get; }
}

/// <summary>不需要一棵活节点树的那一组（纯算术，或只碰自己那个用完就删的临时目录）。
///
/// <b>为什么 <see cref="Run"/> 不收 host</b>：纯算术那一组拿到 host 却不读它，
/// 而「这一组需不需要一棵活树」是这一组自己的形状，不是一个可选项 ——
/// 给它们递一个 host 是把接口做宽。两种形状同样是有意的（<see cref="SelfCheck"/> 那边也是）。</summary>
internal sealed class CriterionOf : Criterion
{
    public CriterionOf(Func<string> green, string red, Func<List<string>> run, Func<string>? suffix = null)
        : base(green, red, suffix) => Run = run;

    public Func<List<string>> Run { get; }
}

/// <summary>要一棵活节点树的那一组（<c>options</c>、<c>calendar-ui</c>、<c>io</c> 那一族）。</summary>
internal sealed class CriterionOnHost : Criterion
{
    public CriterionOnHost(Func<string> green, string red, Func<Node, List<string>> run, Func<string>? suffix = null)
        : base(green, red, suffix) => Run = run;

    public Func<Node, List<string>> Run { get; }
}

/// <summary>无头自检自己：有哪几组、每组跑一次、唯一的出报告处、唯一的退出码。
///
/// <b>为什么要有这个类</b> —— 原来「一组」不是一个能登记的东西。加一组要在两个文件各写一段，
/// 而每一组都抄同一份四行：接住 <c>SelfTest()</c> 的返回、按 <c>Count == 0</c> 挑一句话印、
/// 把问题 <c>AddRange</c> 进累加器。登记进去的各组都抄它，只有排在第一的那组不抄 —— 它那份
/// list 就是累加器，所以「必须排第一」是一条<b>没写下来的接口约束</b>，靠记忆维持。
/// 接口比实施复杂，
/// 就是「浅」的定义。
///
/// 搬完之后一组是一行登记，顺序由登记顺序定（不再靠「谁恰好排在第一个」），组数由自检自己数出来印 ——
/// <c>CODING_STANDARDS.md</c> 那句「以 <c>--check</c> 那一跑印出来的为准」以前是悬空的，因为那一跑
/// 从来不印组数。
///
/// <b>两种签名</b>是有意的，不是没收干净：要一棵活节点树的那几组是 <c>options</c>、
/// <c>calendar-ui</c>、<c>io</c>、<c>flows</c>、<c>defaults</c>（<c>options</c>/<c>io</c>/
/// <c>flows</c>/<c>defaults</c> 各实例化一次 <c>Options.tscn</c>，<c>calendar-ui</c> 建一棵真的
/// <c>Calendar</c>），其余各组是纯算术或只碰自己那个用完就删的临时目录。<b>组数不抄在注释里</b> ——
/// 那一跑自己数出来印（<c>CODING_STANDARDS.md</c> 那条口径）。把这两类硬压成一种签名，就得给
/// 纯算术那一串递一个它们不读的 <c>host</c> —— 那是把接口做宽，不是做深。
///
/// <b>绿话和红话已经不在调用方手里了</b>（工单 03，2026-10-08）。原来它们是登记时传进来的
/// 两个字面量，写在 <c>Probe.cs</c> 里 —— 判据改了它们不跟着改，编译器管不着。
/// 现在它们跟着判据住在 <see cref="Criterion"/> 里，而登记那一行递的是那个值。
/// <b>自检吸收的是它们周围那圈机器</b>：计数、拼接、<c>AddRange</c>、顺序、退出码 ——
/// 外加 <see cref="AuditGreens"/> 那一圈收尾检查（不是一组，不改组数）。</summary>
internal sealed class SelfCheck
{
    /// <summary>一组。<paramref name="Red"/> 是没过时那半句的主语（印成「{Red}有 N 条问题：…」），
    /// 跟 <paramref name="Green"/> 的主语<b>不总是同一个词</b>（「调度模拟通过」对「调度状态机有 N 条问题」，
    /// 「日历算术通过」对「日历有 N 条问题」），所以两个都由 <see cref="Criterion"/> 带着 ——
    /// 合成一个会改实测结果。</summary>
    private sealed record Group(
        string Name, Criterion Criterion, Func<Node, List<string>> Run)
    {
        /// <summary>「不管过没过都追在后面的现问实测结果」。
        /// <b>不在这个 record 上另存一份</b> —— 它就在 <see cref="Criterion"/> 身上
        /// （<c>Criterion.Suffix</c>），另存一份就是同一份事实写在两个地方。
        /// 要读直接问 <see cref="Criterion"/>。</summary>
        public Func<string>? Suffix => Criterion.Suffix;
    }

    private readonly List<Group> _groups = [];
    private readonly Action<string, string> _log;

    /// <param name="log">印实测结果的笔。自检自己不知道实测结果是写进日志、写进 stdout 还是两者。</param>
    public SelfCheck(Action<string, string> log) => _log = log;

    /// <summary>登记一组不需要活节点树的。<paramref name="criterion"/> 自带它自己那两句话。</summary>
    public SelfCheck Check(string name, CriterionOf criterion)
        => Add(name, criterion, _ => criterion.Run());

    /// <summary>登记一组需要活节点树的：<see cref="CriterionOnHost.Run"/> 会拿到递进来的 host。</summary>
    public SelfCheck Check(string name, CriterionOnHost criterion)
        => Add(name, criterion, criterion.Run);

    // **没有「登记时传一句绿话」的那一档**（工单 03，2026-10-08）。搬的过程中先留着它，
    // 一个文件一组地搬、每搬一个跑一次 `--check`；搬完删掉 —— 留着就等于那一族还开着
    // 一个抄写的口子，而这一族要收的就是那个口子。
    //
    // **删掉之后漏一处当次就红**：第二参只有 `CriterionOf` / `CriterionOnHost` 两种形状，
    // 传字面量编译不过。这是工单第二条判据要的「抄漏了会响」的第一半。
    // 另一半（编译器挡不住的那两种：同一句话给了两组、绿话搬空了）在 `AuditGreens` 里。

    private SelfCheck Add(string name, Criterion criterion, Func<Node, List<string>> run)
    {
        _groups.Add(new Group(name, criterion, run));
        return this;
    }

    /// <summary>一跑的结果。<see cref="ExitCode"/> 是这个仓库里<b>唯一</b>决定退出码的地方。</summary>
    public sealed record Result(int Groups, List<string> Problems)
    {
        public int ExitCode => Problems.Count == 0 ? 0 : 1;
        public string Summary => $"--check 结束：{Groups} 组，{Problems.Count} 条问题";
    }

    /// <summary>按登记顺序跑完，每组印一行，还回所有问题。
    ///
    /// <paramref name="suffix"/> 那一档是「不管过没过都追在后面的现问实测结果」——
    /// <c>wallpaper-source</c> 用它把 <c>SPI_GETDESKWALLPAPER</c> 当时答的那一对钉在实测结果里。
    /// 它跟过没过无关，所以拼在三元之外。
    ///
    /// <b><see cref="AuditGreens"/> 那一发的问题也是印出来的</b>，不是只交出去让退出码变 1。
    /// 那一发不属于任何一组，所以没有那一组的行可挂；而
    /// <c>CODING_STANDARDS.md</c> 取证方法论第 5 条记的那一条病正是
    /// 「实测结果压根没出来，屏幕上只剩那句结论」—— 只交出一个条数就正好踩上去。
    /// 它挂的标签是 <c>greens</c>，不叫任何一组的组名，所以**不冒充某一组**。</summary>
    public Result Run(Node host)
    {
        var problems = new List<string>();
        foreach (var group in _groups)
        {
            var found = group.Run(host);
            var tail = group.Suffix == null ? "" : $"（现问：{group.Suffix()}）";
            _log(group.Name, (found.Count == 0
                ? group.Criterion.Green()
                : $"{group.Criterion.Red}有 {found.Count} 条问题：{string.Join("；", found)}") + tail);
            problems.AddRange(found);
        }

        var aboutGreens = AuditGreens();
        if (aboutGreens.Count > 0)
            _log("greens", $"绿话对账有 {aboutGreens.Count} 条问题：{string.Join("；", aboutGreens)}");
        problems.AddRange(aboutGreens);
        return new Result(_groups.Count, problems);
    }

    /// <summary>「每组报出来的绿话都是它自己出的」这一发（工单 03 第二条判据）。
    ///
    /// <b>这一发住在哪一层，为什么是这一层</b>：工单要问的是「自检自己有没有被塞过一句外来的话」。
    /// 抄写收进 <see cref="Criterion"/> 之后，<b>形状上已经塞不进去了</b> ——
    /// 第二个参数的类型是 <see cref="CriterionOf"/> / <see cref="CriterionOnHost"/>，
    /// 传一句字面量编译不过。所以这一发量的是形状<b>盖不住</b>的那两种漏：
    ///
    /// <list type="number">
    /// <item><b>同一句话给了两组</b>（从一处复制到另一处，编译得过、跑起来也过，
    /// 而其中一组量的是别的事 —— 复制那一刻抄错就是这一种）。</item>
    /// <item><b>绿话是一句空串或只剩半句</b>（搬的时候截断了）。</item>
    /// </list>
    ///
    /// <b>它为什么在 <c>Run</c> 的收尾而不是登记时</b>：登记时绿话还没现问，
    /// 而现问的那几组（<c>settings</c> 引键数、<c>activity</c> 引那个毫秒）
    /// 在登记那一刻读的是空档。<b>这一发问的是「跑完之后印出来的那句」</b>，所以在收尾问。
    ///
    /// <b>组数不因它变</b>：它不是一组，是一个问题收集器的一圈检查 ——
    /// 工单第四条判据「组数与改之前那一跑一样」靠的就是这里。</summary>
    private List<string> AuditGreens()
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, string>();
        foreach (var group in _groups)
        {
            var green = group.Criterion.Green();
            var what = $"{group.Name} 那一组的绿话";
            if (string.IsNullOrWhiteSpace(green))
            {
                problems.Add($"{what}是空的 —— 这一组量到了什么，得由它自己说");
                continue;
            }
            if (seen.TryGetValue(green, out var other))
            {
                problems.Add($"{what}和「{other}」那一组逐字相同 —— 抄漏一处，两组都在说自己量了同一件事");
                continue;
            }
            seen[green] = group.Name;
        }
        return problems;
    }
}

/// <summary>一个目录<b>逐字节</b>的尺子：文件名 + 长度 + 每一份的内容，按文件名序拼接，
/// 每一项后面带一个 <c>|</c>。目录本身读不出字节，所以比的是里面每一样东西
/// （和「首启改名」那一族量目录是同一种做法）。
///
/// <b>列的是目录下每一个文件，不过滤后缀</b>：这把尺子要回答的是「那一个目录一个字节没动」，
/// 而只数预设那一种后缀就等于给「顺手写一个缓存文件」留了个口子 —— 2026-10-02 反向验证
/// 头一次就是那么写的，往目录里写了一个 `cache`，尺子当没看见（自检全绿），当场改成列全部。
///
/// <b>非递归</b>：改成递归是另一件事，会改变尺子的答案。
///
/// 实施逐字照搬原来 <c>Presets.Shot</c> 那一份（卡片 05 · S14 收成一处）；
/// 尺子输出的字符串逐字不变是这一片的验收。<c>PresetTemplates</c> 那份同形状的尺子
/// 也改读这里（它原来就写着「与 Presets 那一处读同一个形状」，两份各写一遍必然过期）。
/// <c>Records.Manifest</c> 是另一族，不动。</summary>
internal static class DirBytes
{
    internal static string Shot(string dir) =>
        Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}:{Convert.ToHexString(File.ReadAllBytes(f))}")
            .Aggregate("", (all, one) => all + one + "|");
}
