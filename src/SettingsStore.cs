using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>设置那份文档在磁盘上的样子。读是宽容的，写是对原文的编辑，所以这里没有
/// 「重写整个文件」这条路：每一条写出去的路径都是 <see cref="JsoncDoc.Patch"/>。
///
/// 目录由调用方给，不在这里写死 %APPDATA% —— 自检要在临时目录里跑，不该碰用户那份
/// 真文档。设计文档 `docs/design/config.md`「导出与导入」那条讲的就是这几个方法。</summary>
public static class SettingsStore
{
    /// 数据目录就叫项目名，和 `project.godot` 里 `config/name="Keeper"` 同一份来源。
    public const string AppFolder = "Keeper";

    /// 不带 BOM：首启文档和导出两条路都要对得上字节，BOM 是三个看不见的字节。
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string LiveDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);

    public static string FilePath(string dir) => Path.Combine(dir, Settings.FileName);

    /// 老版把这份文档叫 <c>settings.json</c>；新版第一次启动逐字节改名成 <see cref="Settings.FileName"/>。
    /// 执行者是 <see cref="Records.RenameLegacy"/>，契约里那一条在它上面。
    public const string LegacyFileName = "settings.json";

    public static string BackupPath(string dir) => FilePath(dir) + ".bak";

    /// <summary>一次读取的全部结果。读不通不是异常，是一个状态：程序带着默认值跑，
    /// 窗口说清哪里读不了，没读通之前 <see cref="Save"/> 拒绝落笔。
    ///
    /// <b>不可变</b>（2026-09-29 起）。原来 <see cref="Current"/>、<see cref="Error"/> 和
    /// <see cref="Text"/> 是可变字段，<see cref="Import"/> 就地把 <c>Current</c> 换成另一个对象，
    /// 于是「手上这个引用会不会过期」要靠三个模块各自<b>记得</b>去重拿 —— App 的
    /// <c>SettingsApplied</c> 重拿一次、Options 的 <c>Live</c> 每次现取、Calendar 那个字段特意
    /// 不写 <c>readonly</c>，四处注释讲同一件事。现在写出去的路径各还一份新结果，
    /// 没有可换的字段，就没有需要重拿的把手。
    ///
    /// 分寸：<see cref="Settings"/> 本体仍然是可变对象（保存走 <see cref="Settings.AdoptValues"/>
    /// 就地搬值）。这一发收的是「哪一份设置是活的」这个<b>身份</b>，不是设置里的值 ——
    /// 把值也收成不可变是另一张大得多的手术，不在这一张卡里。</summary>
    public sealed record Read
    {
        public Settings Current { get; init; } = new();

        /// <summary>加载那一刻磁盘上那份文档的原文。<b>不是</b>写出去那几条路的底稿 ——
        /// <see cref="Save"/> 和 <see cref="Export"/> 各自重读一遍磁盘，那是有意的：底稿要是内存里
        /// 这一份，别处改过文件就会被悄悄盖掉。这一条以前写成「原位编辑的底稿」，
        /// 是接口对调用方谎报实施；它唯一的外部读者是 <c>Probe.ReadLiveDocument</c>，
        /// 拿它数文档里有几个键。</summary>
        public string Text { get; init; } = "";

        /// 读不通的原因；null 表示一切都读到了。
        public string? Error { get; init; }

        /// 文档上那个格式版本。它不是设置，它描述这份文档由哪个格式写出。0 表示没写。
        public int DocumentVersion { get; init; }

        /// 文档里出现过的「改了名字的老键」（<see cref="SettingMap.RetiredKeys"/>）。
        /// 它们原样留在文件里、不生效，但要说出来，见那一条的注释。
        /// 说的是<b>加载那一刻</b>那份文档：导入不重算它（原来也不算），而它的读者只在启动那一趟。
        public IReadOnlyList<string> Retired { get; init; } = [];

        /// 磁盘上还没有这份文档，或者文件是空的。
        public bool IsNew => Text.Length == 0;

        /// 格式版本比这个构建还高：里面可能带着本程序没听过的设置，导入的时候要提醒一句。
        public bool NewerThanThisBuild => DocumentVersion > Settings.SchemaVersion;
    }

    /// <summary>一次落笔的结果：失败的原因（null 即成了），和落笔之后<b>新</b>的那一份状态。
    /// 失败时还回来的是递进去那一份原样 —— 磁盘和内存都没动，所以状态也不该动。</summary>
    public sealed record Written(string? Error, Read State);

    public static Read Load(string dir)
    {
        var path = FilePath(dir);
        if (!File.Exists(path)) return new Read();

        var text = File.ReadAllText(path, Utf8);
        if (text.Trim().Length == 0) return new Read();
        if (!JsoncDoc.TryParse(text, out var doc, out var error))
            // 原文留着，因为读不通的那份文档必须还在磁盘上，而且保存要拒绝动它。
            return new Read { Text = text, Error = $"{path}: {error}" };

        var current = new Settings();
        // 老名字先记下来再读值：读不通的那一批不影响这一批（它们是「Keeper 没听过的键」）。
        var retired = SettingMap.RetiredIn(doc!.Values);
        var problems = current.Apply(doc.Values);
        current.ClampToDeclarations();
        return new Read
        {
            Current = current,
            Text = text,
            Retired = retired,
            DocumentVersion = doc.DocumentVersion,
            Error = problems.Count > 0 ? $"{path}: {string.Join("；", problems)}" : null,
        };
    }

    /// 从设置窗口保存：把当前的值编辑进磁盘上那份文档，所以注释、空行、顺序和用户自己的
    /// 键都留在原地。读不通的时候还回失败的原因和<b>原样那一份状态</b>，并且一个字都不动。
    public static Written Save(string dir, Read state)
    {
        var path = FilePath(dir);
        var onDisk = File.Exists(path) ? File.ReadAllText(path, Utf8) : "";
        var (error, text) = Write(path, state.Current, onDisk);
        return error != null
            ? new Written(error, state)
            : new Written(null, state with { Text = text, Error = null });
    }

    /// 导出：把当前的值写到活文档上再取走，所以拿到手的还是一份设置文件 —— 注释和用户
    /// 自己的键都在里面。不存在第二种「导出格式」。
    ///
    /// 它写的是<b>别的</b>文件，所以不动活文档那一份状态，也就没有 <see cref="Written"/> 要还。
    /// 原来那一份会顺手把 <c>state.Text</c> 换成导出去那份的文本、把 <c>Error</c> 清掉 ——
    /// 那不是一次导出该做的事，只是它跟保存共用了同一条落笔路径的副作用。
    public static string? Export(string dir, Read state, string target)
    {
        var live = File.Exists(FilePath(dir)) ? File.ReadAllText(FilePath(dir), Utf8) : "";
        return Write(target, state.Current, live).Error;
    }

    /// 导入的共用机制只有这一条：读源 → parse → apply → clamp → 定底稿 → 留备份 → 落笔。
    ///
    /// <paramref name="inScope"/> 是<b>唯一</b>的模式参数。<c>null</c> 是整份导入：底稿是
    /// 源文本，不回填，被取代的那份留在 settings.jsonc.bak。非 null 是按域导入：底稿是
    /// 活文档全文（活文档不在时是空串），<paramref name="inScope"/> 不收的键从活的那一份
    /// 原样回填进 incoming —— 于是原位编辑那一步连它们那一行都碰不到
    /// （<see cref="JsoncDoc.Patch"/> 只写不一样的值，而它们写进去和原来一样）。
    /// <c>_ =&gt; true</c> <b>不是</b>整份导入：底稿和范围外键的回填都不同。
    ///
    /// 失败两档：<b>返回值</b>收成「源文档讲不通、apply/clamp 有 problems、底稿讲不通」；
    /// <b>异常</b>留给「读源文件、读活文档、复制 .bak、写目标文件」。拒收的导入一个字都不
    /// 落盘 —— 它止步于备份那一步之前，jsonc 组钉着这一条。
    ///
    /// 成了的时候还回来的那一份新状态里，<see cref="Read.Current"/> 指向的是<b>另一个</b>
    /// <see cref="Settings"/> 对象 —— 这一条以前是就地换字段，靠调用方记得重拿；
    /// 现在它由 <see cref="Written"/> 显式交回去。
    private static Written ImportCore(
        string dir, Read state, string source, Func<SettingDecl, bool>? inScope)
    {
        var text = File.ReadAllText(source, Utf8);
        if (!JsoncDoc.TryParse(text, out var doc, out var error))
            return new Written($"{source}: {error}", state);

        var incoming = new Settings();
        var problems = incoming.Apply(doc!.Values);
        incoming.ClampToDeclarations();
        if (problems.Count > 0) return new Written($"{source}: {string.Join("；", problems)}", state);

        var path = FilePath(dir);
        string baseText;
        if (inScope == null)
        {
            baseText = text;
        }
        else
        {
            // 生活模式外的那一半从活的那一份搬过来。走绑定表（不是声明表）：一个键要么绑在属性上
            // 才有值，要么它根本不持有值（组表和 flows 那一张表），所以这一份遍历量到的
            // 正好是「有值的那些键」。
            foreach (var entry in state.Current.Map.Entries)
                if (!inScope(entry.Decl)) incoming.Map.Set(entry.Decl.Key, entry.Get());
            baseText = File.Exists(path) ? File.ReadAllText(path, Utf8) : "";
        }

        if (File.Exists(path)) File.Copy(path, BackupPath(dir), overwrite: true);
        var (failure, written) = Write(path, incoming, baseText);
        return failure != null
            ? new Written(failure, state)
            : new Written(null, state with
            {
                Current = incoming,
                DocumentVersion = doc.DocumentVersion,
                Text = written,
                Error = null,
            });
    }

    /// 导入（整份）：选中的那份文档取代活的，连它的注释和它自己那些键一起；文件里
    /// 没提到的设置回到默认值。读不通的文档不改任何东西。机制在
    /// <see cref="ImportCore"/>，这一条只是 <c>inScope</c> 给 <c>null</c> 的那一档。
    public static Written Import(string dir, Read state, string source) =>
        ImportCore(dir, state, source, inScope: null);

    /// 切生活模式走的那一条：和 <see cref="Import"/> 同一条落笔路径（<see cref="ImportCore"/>），
    /// 换的只是「只写 <paramref name="inScope"/> 收着的那一半」。
    ///
    /// 设计文档 `docs/design/life-mode.md`「切一个生活模式 = 整份换掉」那条
    /// 里的「整份」说的是<b>生活模式范围内的那一整份</b>：
    /// 域在生活模式外面的键是「这台机器的属性」，不装在生活模式里，判据是域本身
    /// （<see cref="LifeModeStore.InLifeMode"/>）。进不来的键一律回到默认值，所以「半个生活模式」不存在。
    public static Written ImportScope(string dir, Read state, string source, Func<SettingDecl, bool> inScope) =>
        ImportCore(dir, state, source, inScope);

    /// 唯一的一条落笔路径：底稿读不通就说读不通，否则把值编辑进去写出去。底稿是空的
    /// 时候先渲染首启那一份 —— 所以第一次保存写出的那份文档，每个键上面都有两行注释。
    /// 还回写出去那个字节串，<b>由调用方决定要不要把它放进新的那一份状态里</b>：
    /// 导出写到别的文件去，它没有资格改活文档的状态。
    private static (string? Error, string Text) Write(string path, Settings settings, string baseText)
    {
        JsoncDoc? doc = null;
        if (baseText.Trim().Length > 0 && !JsoncDoc.TryParse(baseText, out doc, out var error))
            return (error, "");

        var text = doc == null ? SettingsDoc.Render(settings) : Edit(doc, settings);
        var parent = Path.GetDirectoryName(path);
        if (parent != null) Directory.CreateDirectory(parent);
        File.WriteAllText(path, text, Utf8);
        return (null, text);
    }

    private static string Edit(JsoncDoc doc, Settings settings)
    {
        doc.Patch(settings.ToDocument());
        return doc.ToText();
    }

    // ---- 自检 ---------------------------------------------------------------

    /// 手写的文档：注释、块注释、空行、顺序、还有一个本程序从没听过的键，末尾还多一个逗号。
    private const string HandWritten = """
        {
          // 我加的注释
          "activityTimeoutMin": 5, /* and a block one */
          "myOwnKey": { "keep": true },
          "language": "en",
        }
        """;

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Jsonc = new(
        () => "JSONC 组件通过：原位编辑、字节相同、导出导入一个来回",
        "JSONC 组件", SelfTest);

    /// 每个用例一个子目录，所以谁都不依赖谁跑在前面。整份自检跑在 .tmp 里，跑完删掉，
    /// 一条都不碰 %APPDATA% 那份真文档。
    public static List<string> SelfTest()
    {
        var checks = new Checks("jsonc");
        var root = checks.Root!;
        string Case(string name)
        {
            var one = Path.Combine(root, name);
            Directory.CreateDirectory(one);
            return one;
        }

        void Run(string what, Action test) => checks.Check(what, test);

        Run("样本 parse→写出 字节相同", ByteIdentity);
        Run("首启那份文档自己讲得通", FirstRunDocumentIsReadable);
        Run("JSONC 本身：注释、尾逗号、报错带位置", JsoncReadsWhatItShould);
        Run("保存保住注释、空行、顺序和不认识的键", SaveKeepsWhatTheUserOwns);
        Run("flows 那张表：改一步不碰别步的说明，删一条休息流程连它自己的说明一起走",
            FlowsTableIsEditedNotRewritten);
        Run("没人动过的文档保存一次不改一个字节", UntouchedSaveIsANoOp);
        Run("每个键都写得出去也读得回来", NonDefaultsRoundTrip);
        Run("导出→导入→导出 字节相同", ExportImportExportRoundTrips);
        Run("读不通的文档报出来并且原样留着", BrokenDocumentIsReportedAndLeftAlone);
        Run("更高版本的文档认得出来", NewerStampIsIdentifiable);
        Run("改了名字的老键：留着、不生效、要说出来", RetiredKeysAreSeenNotApplied);

        return checks.Report();

        void ByteIdentity()
        {
            foreach (var (name, text) in GoodSamples())
            {
                if (!JsoncDoc.TryParse(text, out var doc, out var error))
                    throw new Exception($"{name}：读不通 {error}");
                if (doc!.ToText() != text)
                    throw new Exception($"{name}：写回来的和读进去的不是同一份文本");
            }

            foreach (var (name, text) in BadSamples())
            {
                if (JsoncDoc.TryParse(text, out _, out var error))
                    throw new Exception($"{name}：这份该读不通，却读通了");
                // 报错要能指到地方去 —— 设置窗口要说清哪里读不了，而不是只说「读不了」。
                if (error == null || !error.Contains("行", StringComparison.Ordinal))
                    throw new Exception($"{name}：报错没带位置：{error}");
            }
        }

        void FirstRunDocumentIsReadable()
        {
            var text = SettingsDoc.Render(new Settings());
            if (!JsoncDoc.TryParse(text, out var doc, out var error))
                throw new Exception($"读不通：{error}");
            if (doc!.ToText() != text) throw new Exception("首启文档不是自己写出来的那个形状");
            if (!text.StartsWith("// Keeper", StringComparison.Ordinal)) throw new Exception("文件头没了");
            // 每个声明的键都得在文档里出现。这要对值视图数，不能对文本数 —— 文本里
            // image.enabled 是 image 里面的 enabled，数文本会数出一个假的不存在。
            var paths = DocumentPaths(doc.Values);
            foreach (var decl in Settings.Declarations)
                if (!paths.Contains(decl.Key))
                    throw new Exception($"文档里没有 {decl.Key}");
            if (!text.Contains("// 0–100", StringComparison.Ordinal))
                throw new Exception("范围没写在文件里，夹取就成了暗规则");
            // activityTimeoutMin 那一档 2026-09-27 拆了上限，写成「≥ 1」而不是「1–2147483647」：
            // 文档说的必须就是夹取做的那件事，两头都得看得见。
            var timeoutLine = text.Split('\n')
                .FirstOrDefault(l => l.Contains("\"activityTimeoutMin\"", StringComparison.Ordinal));
            if (timeoutLine == null || !timeoutLine.Contains("≥ 1", StringComparison.Ordinal))
                throw new Exception("拆了上限那一档在文档里没写成「≥ 1」，读的人不知道它没有顶");
            if (!text.Contains("连续用键盘鼠标多久之后开始淡入。", StringComparison.Ordinal))
                throw new Exception("中文注释没写出去");
            if (!text.Contains("Fading starts after", StringComparison.Ordinal))
                throw new Exception("英文注释没写出去");
            foreach (var flow in new[] { "\"fadetop\"", "\"todoList\"" })
                if (!text.Contains(flow, StringComparison.Ordinal))
                    throw new Exception($"自带的 {flow} 没写出来，用户没法改它");
            if (!text.Contains("淡入到位之后这个 step 停多久。", StringComparison.Ordinal))
                throw new Exception("step 的字段没有说明，那张表就成了猜谜");

            var back = new Settings();
            var unread = back.Apply(doc.Values);
            if (unread.Count > 0) throw new Exception("读不回来：" + string.Join("；", unread));
            if (!JsonNode.DeepEquals(back.ToDocument(), new Settings().ToDocument()))
                throw new Exception("读回来的不是默认值");
        }

        void JsoncReadsWhatItShould()
        {
            if (!JsoncDoc.TryParse(HandWritten, out var doc, out var error))
                throw new Exception($"手写的这份该读得通：{error}");
            var values = doc!.Values;
            if (values["activityTimeoutMin"]!.AsValue().GetValue<int>() != 5)
                throw new Exception("注释后面的值没读到");
            if (!values.ContainsKey("myOwnKey"))
                throw new Exception("不认识的键在值视图里就该看得见");
            if (doc.DocumentVersion != 0) throw new Exception("没写格式版本却读出了一个版本号");

            // 值视图必须跟着编辑走，不然下一次比较会拿旧值判断「没变」。
            doc.Patch(new JsonObject { ["activityTimeoutMin"] = 40 });
            if (doc.Values["activityTimeoutMin"]!.AsValue().GetValue<int>() != 40)
                throw new Exception("改过的值在值视图里还是旧的");
            if (!doc.ToText().Contains("40", StringComparison.Ordinal)) throw new Exception("没写进文本");
            if (!doc.ToText().Contains("// 我加的注释", StringComparison.Ordinal))
                throw new Exception("改值把上面的注释弄丢了");
        }

        void SaveKeepsWhatTheUserOwns()
        {
            var dir = Case("user-owned");
            var path = FilePath(dir);
            File.WriteAllText(path, HandWritten, Utf8);
            var read = Load(dir);
            if (read.Current.ActivityTimeoutMin != 5)
                throw new Exception($"手写的 5 没读到，读回来 {read.Current.ActivityTimeoutMin}");
            read.Current.ActivityTimeoutMin = 40;
            read.Current.Language = "en";
            Fail(read.Error);
            var failure = Save(dir, read);
            Fail(failure.Error);

            var saved = File.ReadAllText(path, Utf8);
            foreach (var kept in new[] { "// 我加的注释", "/* and a block one */",
                                         "\"myOwnKey\"", "\"keep\": true" })
                if (!saved.Contains(kept, StringComparison.Ordinal))
                    throw new Exception($"保存把这一行弄丢了：{kept}");
            // 这个构建新增的键追加在它所在对象的末尾，所以用户写的那三个键的相对顺序没动。
            if (!saved.Contains("\"language\": \"en\"", StringComparison.Ordinal))
                throw new Exception("没改过的值被重排了");
            if (!saved.Contains("\"activityTimeoutMin\": 40", StringComparison.Ordinal))
                throw new Exception("改了的值没写进去");
            foreach (var added in new[] { "\"fadeInMs\"", "\"dismissHoldMs\"", "\"calendar\"", "\"flows\"" })
                if (!saved.Contains(added, StringComparison.Ordinal))
                    throw new Exception($"缺的键没补上：{added}");
            int At(string needle) => saved.IndexOf(needle, StringComparison.Ordinal);
            if (!(At("activityTimeoutMin") < At("myOwnKey") && At("myOwnKey") < At("language")))
                throw new Exception("用户自己排的键顺序被改了");
            if (At("language") > At("fadeInMs"))
                throw new Exception("补上的键插到了用户的键中间");
            // 那一组的默认值从 `new Settings()` 问，不写死数字（`calendar.widthPercent` 的默认
            // 从 70 改成 100 之后，写死 70 的那一份就成了一句会过期的话）。
            if (Load(dir).Current.Calendar.WidthPercent != new Settings().Calendar.WidthPercent)
                throw new Exception("补上的组里没有值");
            if (Load(dir).Current.DismissHoldMs != 2_000) throw new Exception("补上的标量没值");
            if (Load(dir).Current.Language != "en") throw new Exception("改过的语言没写进去");
        }

        /// <summary>flows 那张表在界面上能改之后，保存多了三种以前没有的动作：改一步的一个字段、
        /// 加一步、删掉一条休息流程。三种都得在「只动那一处」的前提下成立，而这一组原来管不到它们：
        /// 首启文档把 step 的说明写在<b>每条休息流程的第一步上面</b>（那一篇讲的是所有 step），
        /// 所以记录列表整个替掉的话，改第二步的时长会把第一步的说明一起换掉。
        ///
        /// 对照组是一位用户自己写的行：它上面有自己的注释，而这一发删的是<b>别</b>的行。
        /// 删自己那一行是另一条规矩 —— flows 的行是设置本身，不是「Keeper 不认识的键」，
        /// 界面上删了，磁盘上就该少一行，那两行注释也跟着走（它说的就是这一行）。</summary>
        void FlowsTableIsEditedNotRewritten()
        {
            var dir = Case("flows-edit");
            var path = FilePath(dir);
            // 那一行手工插在自带那几行里、todoList 之前 —— 缩进照文档里那几行的（四个空格），
            // 不然是往一份对齐的文档里塞一块歪的，读的人先看见歪。
            var baseText = SettingsDoc.Render(new Settings()).Replace(
                "\n    \"todoList\": {",
                "\n    // 我自己起的那条\n" +
                "    \"mine\": { \"steps\": [ { \"ms\": 4000 } ] },\n" +
                "    \"todoList\": {");
            File.WriteAllText(path, baseText, Utf8);
            var read = Load(dir);
            Fail(read.Error);
            if (!read.Current.Flows.ContainsKey("mine"))
                throw new Exception("用户自己加的那条休息流程没读进来，下面全是空的");

            const string stageProse = "淡入到位之后这个 step 停多久。";
            var proseBefore = Count(baseText, stageProse);
            if (proseBefore < 2)
                throw new Exception($"底稿里 step 的说明只有 {proseBefore} 处，这一发没有东西可保");

            // 一、改 todoList 那唯一步的时长和 content；二、给它追加第二步。
            //
            // **步数从表上数，不写下标**（口径见 CODING_STANDARDS.md「跑一次自检」那节）：
            // 2026-10-06 todoList 从两步合成一步，这里原来写死的 `Steps[1]` 当场越界 ——
            // 判据量的是「保存是编辑不是重写」，它该跟出厂那条有几步无关。
            var list = read.Current.Flows["todoList"];
            var before = list.Steps.Count;
            if (before < 1) throw new Exception("todoList 一步都没有，下面全是空的");
            list.Steps[0].Ms = 45_000;
            list.Steps[0].Content = ["clock"];
            list.Steps.Add(new Step { Ms = 12_000, Content = ["health"] });
            Fail(Save(dir, read).Error);
            var saved = File.ReadAllText(path, Utf8);
            if (!saved.Contains("\"ms\": 45000", StringComparison.Ordinal))
                throw new Exception("改过的时长没写出去");
            if (Count(saved, stageProse) != proseBefore)
                throw new Exception($"改一步把 step 的说明弄掉了：{proseBefore} 处 → {Count(saved, stageProse)} 处");
            if (!saved.Contains("// 我自己起的那条\n    \"mine\"", StringComparison.Ordinal))
                throw new Exception("改别人的休息流程时，用户自己那一行连它的注释被挪走了：" +
                    Snippet(saved, "我自己起的那条"));
            if (!saved.Contains("\"ms\": 4000", StringComparison.Ordinal))
                throw new Exception("用户自己那条的时长被改了");
            var back = Load(dir);
            Fail(back.Error);
            var again = back.Current.Flows["todoList"].Steps;
            if (again.Count != before + 1) throw new Exception($"加的那一步没回来：{again.Count} 步（改之前 {before} 步）");
            if (again[0].Ms != 45_000 || string.Join('+', again[0].Content) != "clock" || again[^1].Ms != 12_000)
                throw new Exception($"读回来不是改后的样子：{string.Join('+', again[0].Content)}/{again[0].Ms}/{again[^1].Ms}");
            // 每一步都有自己的一个 content 字段，所以「content 那一行」的个数就是步数。
            //
            // **这个数从表上数，不手抄。** 原来这里写死的是 5，注释还把加法列了出来
            // （fadetop 1 + mine 1 + todoList 3）—— 2026-09-30 出厂多了一行 pomodoro（2 步），
            // 那个数当场过期成 7，而自检红的原因是「有人记得回来改这个字面量」，
            // 量不到「写出去的东西对不对」。口径写在 `CODING_STANDARDS.md`「跑一次自检」那节：
            // 数出来的东西不抄成常量。
            var wantSteps = back.Current.Flows.Values.Sum(flow => flow.Steps.Count);
            if (Count(saved, "\"content\"") != wantSteps)
                throw new Exception($"字段行数和步数对不上：{Count(saved, "\"content\"")} 处，" +
                                    $"该是 {wantSteps} 处（各行加起来：" +
                                    $"{string.Join("、", back.Current.Flows.Select(m => $"{m.Key} {m.Value.Steps.Count} 步"))}）");

            // 删掉用户自己那一行：行没了，它上面的注释也该没了，而别人的一个字不动。
            back.Current.Flows.Remove("mine");
            Fail(Save(dir, back).Error);
            var afterDelete = File.ReadAllText(path, Utf8);
            if (afterDelete.Contains("mine", StringComparison.Ordinal))
                throw new Exception("删掉的休息流程还留在文档里，下一次启动它又回来了");
            if (afterDelete.Contains("我自己起的那条", StringComparison.Ordinal))
                throw new Exception("行删了，写给那一行的注释留在原地，成了没人说的两行");
            if (Count(afterDelete, stageProse) != proseBefore)
                throw new Exception("删一行把别的休息流程的 step 说明带走了");
            if (!afterDelete.Contains("\"ms\": 45000", StringComparison.Ordinal))
                throw new Exception("删一行把刚改过的那个值弄丢了");
            var two = Load(dir);
            Fail(two.Error);
            // 同样是数出来的：底稿里那几条减去刚删掉的那一条。写死 2 的话，出厂多一行就过期
            // （2026-09-30 加了 pomodoro，当场从 2 变 3）。
            var wantRows = read.Current.Flows.Count - 1;
            if (two.Current.Flows.Count != wantRows)
                throw new Exception($"删完读回来 {two.Current.Flows.Count} 条，该是 {wantRows} 条");
            // 删完之后那份文档还得是份能读的文档：最后一行的逗号没留下（能读通就是这一条的实测结果）。
            if (!JsoncDoc.TryParse(afterDelete, out _, out var commaError))
                throw new Exception($"删一行之后读不通了：{commaError}");
        }

        /// 一段文本里某个片段出现几次。这一组问的是「说明还在不在原位」，数次数比数存在更严。
        static int Count(string haystack, string needle)
        {
            var at = 0;
            var seen = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { seen++; at += needle.Length; }
            return seen;
        }

        /// 报错的时候把周围那一段带出来：这一组比的是字节，光说「不一样」看不出哪里不一样。
        static string Snippet(string haystack, string needle)
        {
            var at = haystack.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return $"没有「{needle}」";
            var from = Math.Max(0, at - 40);
            return "…" + haystack[from..Math.Min(haystack.Length, at + needle.Length + 160)] + "…";
        }

        void UntouchedSaveIsANoOp()
        {
            var dir = Case("no-op");
            var path = FilePath(dir);
            File.WriteAllText(path, SettingsDoc.Render(new Settings()), Utf8);
            var before = File.ReadAllText(path, Utf8);
            var read = Load(dir);
            Fail(read.Error);
            Fail(Save(dir, read).Error);
            if (File.ReadAllText(path, Utf8) != before) throw new Exception("一次没人动过的保存改写了文档");

            // 空文件和没有文件一样：都写首启那一份。
            var fresh = Case("fresh");
            var state = Load(fresh);
            if (!state.IsNew) throw new Exception("磁盘上没有文档却没说自己是新的");
            Fail(Save(fresh, state).Error);
            if (File.ReadAllText(FilePath(fresh), Utf8) != before)
                throw new Exception("第一次保存写出的不是那份带注释的文档");
        }

        void NonDefaultsRoundTrip()
        {
            var settings = Filled();
            var dir = Case("non-defaults");
            var state = new Read { Current = settings };
            Fail(Save(dir, state).Error);
            var back = Load(dir);
            Fail(back.Error);
            if (!JsonNode.DeepEquals(back.Current.ToDocument(), settings.ToDocument()))
                throw new Exception("走了一趟磁盘不是同一份值：" + Settings.Diff(settings, back.Current));
            if (back.Current.Flows["quiet"].Steps[1].Ms != 90_000)
                throw new Exception("用户自己起的 flow 没活着回来");
            if (back.Current.Image.File != @"C:\图片\x.png")
                throw new Exception($"路径里的反斜杠或中文丢了：{back.Current.Image.File}");
            // 写出去的那一份里中文要还是中文：默认的转义器会把它写成 \u56fe\u7247，
            // 而这份文档是给人手工编辑的（同一条规矩 Todos 早就守着）。
            {
                var written = File.ReadAllText(FilePath(dir), Utf8);
                if (!written.Contains("图片", StringComparison.Ordinal))
                    throw new Exception("路径里的中文被转义成了 \\uXXXX，手工编辑的人读不出来");
            }
            if (back.Current.Language != "en") throw new Exception("界面语言没活着回来");
            if (!DelaysLive(back.Current)) throw new Exception("那一串分钟数没活着回来");

            // 一份缺了大半键的文档读回来是默认值加它写的那几个。
            var partial = Case("partial");
            File.WriteAllText(FilePath(partial), "{\n  \"blockWhenFullscreen\": false\n}\n", Utf8);
            var old = Load(partial);
            Fail(old.Error);
            if (old.Current.BlockWhenFullscreen) throw new Exception("写了 false 却读成 true");
            if (!DelaysAreDefault(old.Current)) throw new Exception("没写的键没回到默认值");
            // 保存要把它缺的那些补上，而且不动用户写了那一行的位置。
            Fail(Save(partial, old).Error);
            var filled = File.ReadAllText(FilePath(partial), Utf8);
            if (!filled.Contains("\"blockWhenFullscreen\": false", StringComparison.Ordinal))
                throw new Exception("补键的时候把用户写的那一行改了");
            if (!filled.Contains($"\"{Sample().Key}\"", StringComparison.Ordinal))
                throw new Exception("没补上这个构建才有的键");
        }

        /// 那一串分钟数走了一趟磁盘是不是同一份（**和出厂那一份比**，不写死数字 ——
        /// 出厂的几格改一次，这里就会红，而那份字面量过期得没有声音）。
        bool DelaysLive(Settings settings) =>
            Filled().RestDelays.SequenceEqual(settings.RestDelays);

        void ExportImportExportRoundTrips()
        {
            var dir = Case("round-trip");
            var live = Filled();
            var state = new Read { Current = live };
            Fail(Save(dir, state).Error);

            // 用户自己往文件里加了一行备注，导出要把它带上。
            var path = FilePath(dir);
            var onDisk = File.ReadAllText(path, Utf8);
            var where = onDisk.IndexOf("\"activityTimeoutMin\"", StringComparison.Ordinal);
            var lineStart = onDisk.LastIndexOf('\n', where) + 1;
            var annotated = onDisk.Insert(lineStart, "  // 我的备注\n");
            File.WriteAllText(path, annotated, Utf8);

            var exported = Path.Combine(dir, "exported.jsonc");
            Fail(Export(dir, Load(dir), exported));
            var copy = File.ReadAllText(exported, Utf8);
            if (!copy.Contains("// 我的备注", StringComparison.Ordinal))
                throw new Exception("导出没带上文件里那条备注");

            var imported = new Settings();
            if (!JsoncDoc.TryParse(copy, out var exportedDoc, out var copyError))
                throw new Exception($"导出的那份读不通：{copyError}");
            var unread = imported.Apply(exportedDoc!.Values);
            imported.ClampToDeclarations();
            if (unread.Count > 0) throw new Exception("导出的一份读不回来：" + string.Join("；", unread));
            if (!JsonNode.DeepEquals(imported.ToDocument(), live.ToDocument()))
                throw new Exception("导入回来的设置和导出前不一样：" + Settings.Diff(live, imported));

            // 再走一遍：一份已经是当前的文档，不该有任何字节变化。
            var again = Path.Combine(dir, "again.jsonc");
            Fail(Export(dir, Load(dir), again));
            if (File.ReadAllText(again, Utf8) != copy) throw new Exception("导出→导入→导出 不是同一份字节");

            // 导入一份已经是当前的文档，活文档不该被搅动，被取代的那份要留下来。
            var before = File.ReadAllText(path, Utf8);
            Fail(Import(dir, Load(dir), exported).Error);
            if (!File.Exists(BackupPath(dir))) throw new Exception("被取代的那份没留下来");
            if (File.ReadAllText(path, Utf8) != before)
                throw new Exception("导入一份已经是当前的文档搅动了活文档");
        }

        void BrokenDocumentIsReportedAndLeftAlone()
        {
            var dir = Case("broken");
            var path = FilePath(dir);
            var broken = "{ \"autoFade\": true, ";
            File.WriteAllText(path, broken, Utf8);
            var read = Load(dir);
            if (read.Error == null) throw new Exception("读不通却没报告");
            if (read.Error != null && !read.Error.Contains(Settings.FileName, StringComparison.Ordinal))
                throw new Exception($"报告里没说清是哪份文件：{read.Error}");
            if (Save(dir, new Read()).Error == null) throw new Exception("读不通的文档被保存覆盖了");
            if (File.ReadAllText(path, Utf8) != broken) throw new Exception("读不通的文档被改动了");
            if (Export(dir, new Read(), Path.Combine(dir, "out.jsonc")) == null)
                throw new Exception("读不通的文档被导出了");
            if (Import(dir, new Read(), path).Error == null)
                throw new Exception("读不通的一份被当成了新配置导入");
            // 拒收的导入一个字都不许落：备份是落笔前那一步才做的事，拒收发生在它之前。
            // 这一条挡住后面把备份步骤挪进公共管线的重排（卡片 05 · S12）。
            if (File.Exists(BackupPath(dir)))
                throw new Exception($"拒收的导入留下了 {BackupPath(dir)} —— 拒收不该动磁盘上任何东西");

            // 类型不对也是读不通：程序照默认值跑，但要报告。键名从 <see cref="Sample"/> 问，
            // 不手抄 —— 那个键退休了，判据里的字面量会跟着过期。
            File.WriteAllText(path, $"{{ \"{Sample().Key}\": \"很多\" }}", Utf8);
            var mistyped = Load(dir);
            if (mistyped.Error == null) throw new Exception("字符串写在一个整数键上却没报告");
            if ((int)mistyped.Current.Map.Get(Sample().Key) != (int)Sample().Default)
                throw new Exception("读不通的时候没照默认值跑");
            // 但这份文档语法是好的，所以保存照常编辑它，而不是拒绝。
            Fail(Save(dir, mistyped).Error);
            if (File.ReadAllBytes(path).Length == 0) throw new Exception("保存写空了");

            // 手工修好之后，程序该能照原位把它读回来。
            File.WriteAllText(path, "{ \"autoFade\": false }", Utf8);
            if (Load(dir).Current.AutoFade) throw new Exception("修好的文档没读回来");
        }

        void NewerStampIsIdentifiable()
        {
            var dir = Case("newer");
            var text = SettingsDoc.Render(new Settings()).Replace(
                $"\"{Settings.VersionKey}\": {Settings.SchemaVersion}",
                $"\"{Settings.VersionKey}\": {Settings.SchemaVersion + 2}");
            File.WriteAllText(FilePath(dir), text, Utf8);
            var read = Load(dir);
            if (!read.NewerThanThisBuild)
                throw new Exception($"格式版本是 {read.DocumentVersion}，导入的时候要能警告");
            if (read.Error != null) throw new Exception($"更高的版本该读得更宽，不是读不通：{read.Error}");

            // 一个比声明里还高的数要被夹住 —— 新文档不能把这个程序带进它处理不了的状态。
            File.WriteAllText(FilePath(dir), text.Replace(
                $"\"{Sample().Key}\": {(int)Sample().Default}",
                $"\"{Sample().Key}\": {Sample().Max + 1}"));
            var clamped = Load(dir);
            Fail(clamped.Error);
            if ((int)clamped.Current.Map.Get(Sample().Key) != Sample().Max) throw new Exception("超范围的数没被夹住");
        }

        /// <summary>改了名字的老键按「Keeper 没听过的键」处理：不许有代价、不许悄悄生效，但<b>要说出来</b> ——
        /// 不说，人只会以为日历没读进设置。见 SettingMap.RetiredKeys 上面那段注释。
        ///
        /// <b>份数从 <see cref="SettingMap.RetiredKeys"/> 问，不写死</b>：2026-10-13 又添了两条
        /// （那两个翻倍/封顶的键），而这一组当年把「四条」写进了字符串和断言里 ——
        /// 那一份会过期，而且过期得没有声音（`CODING_STANDARDS.md`「判据里不许存一份会腐烂的副本」）。
        /// 底稿里<b>每一对老键都摆上</b>（从那份表里按位置取），所以再加一条退休键，
        /// 这一组自动跟着量到 —— 而这一组量的正是「退休键这条路还通不通」。</summary>
        void RetiredKeysAreSeenNotApplied()
        {
            var dir = Case("retired");
            // 底稿：日历那四条照原样写死（它们嵌套，要摆出那个形状），两个顶层的按名字从
            // `RetiredKeys` 里按位置取 —— 那样「加一条退休键」不会从这一组头上溜过去。
            var flat = SettingMap.RetiredKeys.Where(pair => !pair.Old.Contains('.')).ToList();
            File.WriteAllText(FilePath(dir), """
                {
                  // 一份还留着老日历键的用户文档，那一段长这样
                  "calendar": {
                    "enabled": true,
                    "style": "wallpaper",
                    "offsetX": 24,
                    "offsetY": 30,
                    "coveragePercent": 55,
                    "scalePercent": 100
                  },
                """ + string.Join(",\n", flat.Select(pair => $"  \"{pair.Old}\": 1")) + "\n}\n", Utf8);
            var read = Load(dir);
            Fail(read.Error);
            var want = SettingMap.RetiredKeys.Length;
            if (read.Retired.Count != want)
                throw new Exception($"改了名字的老键该认出 {want} 条，认出 {read.Retired.Count} 条：" +
                                    $"{string.Join("；", read.Retired)}");
            var cal = read.Current.Calendar;
            // 老键退休之后，读回来的该是**声明表里的默认值** —— 从 `Decl` 问，不写死数字
            // （`calendar.widthPercent` 的默认从 70 改成 100 之后，写死 70 的那一份就成了
            // 一句会过期的话，而它过期得没有声音）。
            var fresh = new Settings();
            var wantW = fresh.Calendar.WidthPercent;
            if (cal.MarginXPercent != fresh.Calendar.MarginXPercent ||
                cal.MarginYPercent != fresh.Calendar.MarginYPercent ||
                cal.WidthPercent != wantW)
                throw new Exception($"老键悄悄生效了：marginX={cal.MarginXPercent} marginY={cal.MarginYPercent} " +
                                    $"width={cal.WidthPercent} —— 该是各自的默认值 " +
                                    $"{fresh.Calendar.MarginXPercent} / {fresh.Calendar.MarginYPercent} / {wantW}");
            // 2026-10-13 那一对也一样：它们各自指向同一个新键，而那一个新键的值原封不动。
            if (!DelaysAreDefault(read.Current))
                throw new Exception($"那两个老键悄悄生效了：休息延迟那一串是 " +
                                    $"{string.Join(", ", read.Current.RestDelays)}，该是出厂那一串 " +
                                    $"{string.Join(", ", fresh.RestDelays)}");
            if (!read.Retired[0].Contains("→")) throw new Exception("那一句该写成「老名字 → 新名字」");

            // 保存一次：它们还得在原处 —— 陌生的键不许有代价，也不许被顺手抹掉。
            Fail(Save(dir, read).Error);
            var text = File.ReadAllText(FilePath(dir), Utf8);
            foreach (var key in new[] { "offsetX", "offsetY", "coveragePercent", "scalePercent" }
                         .Concat(flat.Select(pair => pair.Old)))
                if (!text.Contains(key)) throw new Exception($"保存把那一条老键「{key}」抹掉了");
        }

        /// 那一串分钟数是不是出厂那一串（**不写死格数和每一格** —— 出厂改了这里就该红）。
        static bool DelaysAreDefault(Settings settings) =>
            settings.RestDelays.SequenceEqual(new Settings().RestDelays);

        void Fail(string? problem)
        {
            if (problem != null) throw new Exception(problem);
        }

        /// 这一组拿哪个整数键当「普通整数键」的例子。
        /// 2026-10-13 起是 <c>textScalePercent</c> —— 原来那个 <c>maxRetryDelayMs</c> 跟着
        /// 「每取消一次翻倍」那一套一起退休了（现在叫休息延迟）。默认值和上下限都**从声明表现问**：
        /// 抄一份数字进判据，那份会和声明表分家，而这里量的是「照声明表跑」。
        static SettingDecl Sample() =>
            Settings.Declarations.First(d => d.Key == "textScalePercent");
    }

    /// 每个种类都拿一个非默认值填一遍，包括嵌套组和用户自己加的那条休息流程。
    private static Settings Filled()
    {
        var settings = new Settings
        {
            ActivityTimeoutMin = 60,
            Flow = "quiet",
            MaxOpacityPercent = 5,
            FadeInMs = 300,
            FadeOutMs = 10_000,
            // 落定后置顶的默认值 2026-10-29 翻成 true，所以这一格的「非默认值」取 false。
            StayOnTopAfterLanding = false,
            MinBreakMs = 3_000,
            // 休息延迟那一串：非默认值（出厂两格，这里三格）—— 这一族是**每一种都拿一个
            // 非默认值填一遍**，而这一种的值是一串，所以这里给的是一串。
            RestDelays = [7, 11, 13],
            TriggerPauseMs = 0,
            DismissHoldMs = 300,
            TextScalePercent = 300,
            ForegroundColor = "#000000",
            BackgroundColor = "#ffffff",
            Clock24Hour = false,
            BlockWhenFullscreen = false,
            AutoFade = false,
            AllMonitors = false,
            Autostart = true,
            Language = "en",
        };
        settings.Image.Enabled = true;
        settings.Image.File = @"C:\图片\x.png";
        settings.Image.Position = "fit";
        settings.Sound.Enabled = true;
        settings.Sound.File = @"C:\声音\a.flac";
        settings.Sound.FadeIn = true;
        settings.Sound.FadeOut = false;
        settings.Sound.Volume = 11;
        settings.Sound.Repeat = true;
        settings.Calendar.Enabled = false;
        settings.Calendar.Style = "outline";
        settings.Calendar.Corner = "bottomLeft";
        settings.Calendar.MarginXPercent = 25;
        settings.Calendar.MarginYPercent = 0;
        settings.Calendar.WidthPercent = 20;
        settings.Calendar.HeightPercent = 100;
        settings.Calendar.WidgetWidthPercent = 6;
        settings.Calendar.HeaderPercent = 3;
        settings.Calendar.TextPercent = 50;
        settings.Calendar.OpacityPercent = 20;
        settings.Calendar.WeekStartsMonday = false;
        settings.Calendar.ShowLunar = false;
        // 作息这一族：总开关拧成 false，好让「哪一条是非默认值」有得问；时间块给一块
        // **跨午夜**的（起止、名字、种类、到点走哪一行、让位、抬不抬全都拧成非默认值）。
        // 有了它，下面那几条（走一趟磁盘是同一份值、导出→导入→导出逐字节相同）顺手就把
        // 这张表也量了 —— 那是现成那把尺子，不另开一把。
        settings.RoutineEnabled = false;
        settings.Blocks.Add(new TimeBlock
        {
            StartMinute = 22 * 60 + 30, EndMinute = 6 * 60 + 30,
            Name = "夜", Kind = Settings.BlockKindSleep, Fade = false,
            YieldsToInput = false, Flow = "quiet",
        });
        settings.Flows["quiet"] = new Flow
        {
            Steps =
            [
                new Step
                {
                    Ms = 0, Dismissible = true, Interactive = false,
                    // 这一步特意留着 `trail`：遮罩上那排方块 2026-10-06 撤了，
                    // 而别人的休息流程里还写着这个成员名。走一趟磁盘必须是逐字节相同 ——
                    // 这一组量的是「读得进来、写得回去」，不是「界面上画不画」。
                    Content = ["clock", "trail"], Sound = false,
                },
                new Step
                {
                    Ms = 90_000, Dismissible = false, Interactive = true,
                    Content = ["checklist"], Sound = true,
                },
            ],
        };
        return settings;
    }

    /// 一份文档里所有键的路径，点分。flows 那张表不往下走 —— 它的行名是用户起的，
    /// 不该拿声明表去要求它们存在。
    public static HashSet<string> DocumentPaths(JsonObject root)
    {
        var found = new HashSet<string>();
        Collect(root, "");
        return found;

        void Collect(JsonObject node, string prefix)
        {
            foreach (var (key, value) in node)
            {
                found.Add(prefix + key);
                if (value is JsonObject nested && prefix + key != Settings.ModesKey)
                    Collect(nested, prefix + key + ".");
            }
        }
    }

    /// 两份设置差在哪，读作「from 的值 → to 的值」。给报告用：设置窗口和自检都要说清
    /// 哪里不一样，而不是只说「不一样」。
    public static string DiffOf(Settings from, Settings to)
    {
        var a = from.ToDocument();
        var found = new List<string>();
        foreach (var (key, value) in to.ToDocument())
        {
            if (!a.TryGetPropertyValue(key, out var other)) { found.Add($"{key} 多了"); continue; }
            if (!JsonNode.DeepEquals(other, value))
                found.Add($"{key}: {other?.ToJsonString()} → {value?.ToJsonString()}");
        }
        return string.Join("；", found);
    }

    private readonly record struct Sample(string Name, string Text);

    /// 字节相同这件事唯一的证法：读进去、再写出来，和原文逐字对。这些样本是照着「用户会
    /// 怎么写这份文件」挑的，不是照着解析器的分支挑的。
    private static IEnumerable<Sample> GoodSamples() =>
    [
        new("空对象", "{}"),
        new("一行到底", "{\"a\":1,\"b\":[],\"c\":{}}"),
        new("CRLF", "{\r\n  \"a\": 1,\r\n  \"b\": \"x\"\r\n}\r\n"),
        new("行尾和块注释", "{\n  // 上面\n  \"a\": 1, /* 旁边 */ \"b\": 2\n}\n"),
        new("闭合括号前的注释", "{\n  \"a\": 1\n  // 这一行是我的\n}\n"),
        new("嵌套和尾逗号", "{\"a\":{\"b\":[1,2,3,],},}\n"),
        new("转义", "{\"path\":\"C:\\\\x\\\\y.png\",\"quote\":\"a\\\"b\",\"unicode\":\"\\u00e9\"}"),
        new("空数组里带空白", "{\"a\":[   ],\"b\":[ ]}\n"),
        new("数字的各种写法", "{\"a\":-0.5,\"b\":1e3,\"c\":-1,\"d\":2E-2,\"e\":15.0}\n"),
        new("注释在冒号两边", "{\n  \"a\" /* 左边 */ : 1,\n  \"b\":\n  2\n}\n"),
        new("空数组和空对象混在中间", "{\"a\":[],\"b\":{},\"c\":[{}]}\n"),
        new("手写的设置文档", HandWritten),
    ];

    private static IEnumerable<Sample> BadSamples() =>
    [
        new("半个对象", "{ \"autoFade\": true, "),
        new("没闭合的注释", "{ \"a\": 1 /* 没写完\n"),
        new("没闭合的字符串", "{ \"a\": \"没完\n"),
        new("裸键名", "{\n  \"a\": 1,\n  oops: 2\n}\n"),
        new("根不是对象", "[1, 2]\n"),
        new("值是缺的", "{ \"a\": , \"b\": 1 }\n"),
    ];
}
