using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Keeper;

/// 每个设置在这里声明一次：键名、类型、默认值、范围、可选值，以及首启文档里写在它上面
/// 的那两行注释。夹取、设置窗里每个控件的范围和「恢复默认」、写出去的那份
/// `settings.jsonc` —— 三个读者都读下面那张表。设计文档 `docs/design/config.md`「每个设置只声明一次」那条
/// 讲的「一个限制只写一次」就是靠这一份成立的：谁都不许再抄第二个数字。
///
/// 这里不放解析和写文件的代码：读是宽容的、写是对原文的编辑，那是 Jsonc 那一份组件的活。
public enum SettingKind
{
    Int,
    Bool,
    Text,
    HexColor,
    Choice,
    ChoiceList,

    /// 一个嵌套组。它自己不持有值，值在它名下那些点分键上；声明它是为了让首启文档里
    /// 「image」这一行上面也有两行注释。
    Group,

    /// flows 那张表。行是用户写的，所以它的取值不受本程序的可选值约束。
    ModeTable,

    /// 作息表那张时间块表。和 <see cref="ModeTable"/> 同一种形状的另一个家族：行是用户写的
    /// （一行一个时间块），行里的字段是 <see cref="Settings.BlockDeclarations"/> 那一份。
    /// 单独一个成员而不是复用 ModeTable：两张表的行身份不一样（休息流程那张靠行名认，
    /// 时间块这张按位置认），而那个差异住在原位编辑那一步（<c>JsoncDoc</c>）和渲染那一步。
    BlockTable,

    /// 勿扰那张时段表。第三张这种表：行是用户写的（一条时段一行），行里的字段是
    /// <see cref="Settings.WindowDeclarations"/> 那一份。和前两张一样按位置认行
    /// （<c>JsoncDoc</c> 那一步逐元素比），所以原位编辑、渲染、生活模式那整套都照同一套路走。
    WindowTable,

    /// 一列用户写的分钟数 —— 取消一次休息之后那一串「休息延迟」。**和
    /// <see cref="ChoiceList"/> 的分别**：那一列的可选值是本程序给的（摆什么就只能是那几样），
    /// 这一列的每一项都是用户自己写的一个分钟数，所以它同样不挂可选值；
    /// 而它们还有一处分别 —— 每一项的<b>下限</b>来自声明（<c>Min</c>，1 分钟起），
    /// <see cref="ChoiceList"/> 压根没有「一项有多大」这回事。
    ///
    /// 界面上是一行字（逗号分隔），不是一颗数字格 —— 值是<b>一串</b>而不是一个数，
    /// 那一格没有值域那回事，所以 <see cref="SettingDecl.HasRange"/> 认的还是「界面上是不是
    /// 一颗数字格」，夹取走 <see cref="SettingDecl.ClampDelay"/> 那一条。
    DelayList,

    /// 一列用户写的词 —— 勿扰那张 exe 名单。**和 <see cref="ChoiceList"/> 的分别**：
    /// 那一列的可选值是本程序给的（摆什么就只能是那几样），这一列的每一项都是用户自己敲的
    /// （一个 exe 名就是他自己写的一个词），所以它不挂可选值、夹取也不按可选值筛。
    ///
    /// 它是**容器**（<see cref="IsContainer"/>）：值在自己的属性上，界面上由名单编辑器改，
    /// 而不是一个「一行设置」——和 <see cref="ModeTable"/> / <see cref="BlockTable"/> 同一处境。
    NameList,

    /// 布局里那一份控件的表。行是用户摆的（一行一个控件），行里的字段是
    /// <see cref="Settings.StepLayoutDeclarations"/> 那一份。
    ///
    /// <b>它不是容器</b>，和前两张表不同：<c>steps[].layout</c> 本身挂着那份值
    /// （一份控件的列表），所以它走绑定表、走夹取 —— 只是它的类型是
    /// <c>List&lt;StepLayout&gt;</c>，读写那两下由 <see cref="StepLayout"/> 自己干。
    /// 行那一层在它里面（<c>StepLayout</c> 自己有一张绑定表）。
    LayoutList,
}

/// <summary>一条声明。范围只对 <see cref="SettingKind.Int"/> 有意义；可选值只对
/// <see cref="SettingKind.Choice"/> 和 <see cref="SettingKind.ChoiceList"/> 有意义。</summary>
public sealed class SettingDecl
{
    /// 文档里的键名。嵌套组用点分：<c>calendar.marginXPercent</c>。
    public required string Key { get; init; }
    public required SettingKind Kind { get; init; }
    public required object Default { get; init; }
    public int Min { get; init; }
    public int Max { get; init; }
    public string[] Choices { get; init; } = [];

    /// 首启文档里写在键上面的两行：中文一行、英文一行，和文件头的双语写法一致。
    public required string CommentZh { get; init; }
    public required string CommentEn { get; init; }

    /// <summary>设置窗顶部那几个域里的哪一格 —— <c>break</c> / <c>calendar</c> /
    /// <c>routine</c> / <c>system</c>。
    /// 默认 <c>break</c>：休息那一侧是大多数键的家，不必每条都写出来。
    ///
    /// <b>为什么这条住在声明表里</b>：它和键名、范围、默认值是同一类东西 ——
    /// 「这一条是什么、它归哪儿、它能取哪些值」，一处说完。散到界面上就是第二份，
    /// 而 UI-only 那一版（上一版）已经被自检抓到过两次：分区名打错字、给
    /// <c>calendar.*</c> 漏标域。自检里 <c>Options.Audit</c> 第 13 条对着它核。</summary>
    public string Domain { get; init; } = "break";

    /// <summary>这一条是<b>常规</b>还是<b>高级</b> —— <c>common</c> / <c>advanced</c>，默认
    /// <c>common</c>。设置窗每个分区只把常规那几条摆出来，高级那几条收在「高级设置」
    /// 后面（默认收起，2026-09-29 量出来的实测结果在
    /// <c>docs/research/options-window-domains-and-search.md</c>）。
    ///
    /// <b>默认 common 是有意的</b>：新加一个键忘了标，它就<b>看得见</b>。反过来（默认高级）
    /// 会让每个新键静默藏起来 —— 那是一个没人报的问题，要等人来问「这个怎么调」。
    ///
    /// <b>为什么住在声明表</b>：和 <see cref="Domain"/> 同一个理由，它是「这一条是什么」的一部分。
    /// 散在界面上就成第二份，而「哪条算高级」是会过期的判断 —— 自检里 <c>Options.NavSelfTest</c>
    /// 断言档位取值合法、且默认视图只露常规那几条，让它过期时报出来而不是静默失准。</summary>
    public string Tier { get; init; } = "common";

    public bool HasRange => Kind == SettingKind.Int;

    /// <summary>这个键在界面上是不是一个<b>时间</b>（存的是毫秒，显示成人话）。
    ///
    /// <b>为什么按后缀判</b>：键名已经写着这一条是什么了（<c>fadeInMs</c>），再给声明表
    /// 加一个「显示单位」字段就是第二份真相 —— 改一个键名的后缀、忘了改那一处，
    /// 于是同一个键一半按毫秒一半按分钟。设计文档 `docs/design/config.md`「界面上不出现毫秒」那条要的就是
    /// 「由键名派生，不另设一份声明」。
    ///
    /// <b>两种大小写都认</b>：休息流程里那一步的字段就叫 <c>ms</c>（全小写），而设置那边
    /// 是 <c>fadeInMs</c> 驼峰。两者都是毫秒，都得走同一条显示口径 —— 契约写的是
    /// 「休息流程编辑器每一步那个 <c>ms</c> 格走同一条路」。</summary>
    public bool IsTime => Key.EndsWith("Ms", StringComparison.Ordinal)
                       || Key.EndsWith("ms", StringComparison.Ordinal);

    /// <summary>控件上那个数字格的地板：声明给的下限<b>换算到此刻这一格用的那种单位</b>，
    /// 往大了取整（<c>ceil</c>），所以「屏幕上能打出来的最小那个数」换回毫秒仍然 ≥
    /// 声明的下限。下限还是声明里那一个 —— 这里只是把它说成这一格用的单位。
    ///
    /// <paramref name="scale"/> 是 <see cref="Spell"/> 给的那个除数：数字格在「秒」和
    /// 「分钟」之间换档时地板跟着换，所以它不是一个常数（同一个键的两档地板不一样）。</summary>
    public double ControlMin(int scale) => scale > 1
        ? Math.Ceiling(Min / (double)scale)
        : Min;

    /// <summary>「没有上限」写成 <c>Max = int.MaxValue</c>，而不是把范围整个关掉：夹取那一步
    /// 照旧走 <see cref="Clamp"/>，只有文档里那一行改成只说下限。
    /// 2026-09-27 他把 <c>activityTimeoutMin</c> 写回那一步的 1–60 拆了 —— 屏幕上不许冒出一个
    /// 用户没写过的数，那根 60 的天花板整个不要了。
    /// 下限留着：0 和负数会把调度算坏（那是他这一条规则里没说要拆的）。</summary>
    public bool NoCeiling => Max == int.MaxValue;

    /// 首启文档行尾那一句，和夹取读的是同一条声明。
    public string RangeText => NoCeiling ? $"≥ {Min}" : $"{Min}–{Max}";

    public int Clamp(int value) => HasRange ? Math.Clamp(value, Min, Max) : value;

    /// <summary><see cref="SettingKind.DelayList"/> 里<b>一项</b>的范围。
    ///
    /// <b>为什么不走 <see cref="Clamp"/></b>：那一处问的是「这一条在界面上是不是一颗数字格」
    /// （<see cref="HasRange"/>），而这一族的控件是一行字、值是<b>一串</b>——「有没有值域」
    /// 问的是另一件事，混在一处就有一处会答错（把一串分钟数当成一颗数字格去夹）。
    /// 地板仍然是声明给的那一个：0 分钟和负数会把调度算坏。</summary>
    public int ClampDelay(int minutes) => Math.Clamp(minutes, Min, Max);

    /// 认不出的取值回到默认，而不是让程序带着一个谁都不认识的状态跑下去。没有可选值的键
    /// （自由文本、以及 flow 那种指向 flows 里某个名字的键）原样通过——它们没有「不认识」
    /// 这回事，硬过一遍会把用户自己起的 flow 名抹掉。
    public string OrKnown(string value) =>
        Choices.Length == 0 || Array.IndexOf(Choices, value) >= 0 ? value : AsString;

    public bool IsColor(string value) =>
        value.Length == 7 && value[0] == '#'
        && int.TryParse(value.AsSpan(1),
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture,
            out _);

    /// 组表、flows 那张表、作息表那张时间块表、勿扰那两张表自己不带值，值在别处；
    /// 夹取、读写都要跳过它们自己的那一行。
    public bool IsContainer =>
        Kind is SettingKind.Group or SettingKind.ModeTable or SettingKind.BlockTable
            or SettingKind.WindowTable or SettingKind.NameList;

    /// 文档里的一个值变成这个键该有的类型。类型不对算「这一份读不通」，不算「忽略这一行」——
    /// 设计文档 `docs/design/config.md`「解析失败是一个状态，不是崩溃」那条要的是程序用默认值跑起来，并且说清哪里读不了。
    public object Coerce(JsonNode node) => Kind switch
    {
        SettingKind.Int when AsInteger(node, out var n) => n,
        SettingKind.Bool when node is JsonValue b && b.TryGetValue(out bool flag) => flag,
        SettingKind.Text when node is JsonValue s && s.TryGetValue(out string? text) => text,
        SettingKind.Choice when node is JsonValue c && c.TryGetValue(out string? text) => text,
        SettingKind.HexColor when node is JsonValue h && h.TryGetValue(out string? text) => text,
        SettingKind.ChoiceList when node is JsonArray list && list.All(IsString) =>
            list.Select(item => item!.GetValue<string>()).ToList(),
        // 一串分钟数：数组里每一项都得是整数（15.5 不算，和 <see cref="AsInteger"/> 那一处同一个读法）。
        SettingKind.DelayList when node is JsonArray rows && rows.All(row => row != null && AsInteger(row, out _)) =>
            rows.Select(row => Minutes(row!)).ToList(),
        // 布局那一族带回自己那份报告（哪一个控件读不通），所以交出去的不是裸列表 ——
        // `SettingMap.ReadFrom` 在那一步把它拆开，别的键不碰这一种形状。
        SettingKind.LayoutList when node is JsonArray rows => StepLayout.Read(rows),
        _ => throw new ReadProblem($"{Key}: 期望 {Kind}，文档里写的是 {node.ToJsonString()}"),
    };

    private static bool IsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? _);

    /// <see cref="AsInteger"/> 已经问过「这一项认不认得出」之后，取它的值。
    private static int Minutes(JsonNode node)
    {
        AsInteger(node, out var minutes);
        return minutes;
    }

    /// 15 和 15.0 都算整数，15.5 不算 —— 悄悄抹掉小数等于替用户改一个他看不见的值。
    private static bool AsInteger(JsonNode node, out int value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out int i))
        {
            value = i;
            return true;
        }
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)l;
            return true;
        }
        if (v.TryGetValue(out double d) && d is >= int.MinValue and <= int.MaxValue && d == Math.Truncate(d))
        {
            value = (int)d;
            return true;
        }
        return false;
    }

    /// 整数键的写法，省掉每个键重复五行的对象初始化。
    /// <paramref name="domain"/> 默认 <c>break</c>（休息那一侧）；日历那 8 条百分比
    /// 显式写 <c>"calendar"</c>，其余不用写。
    public static SettingDecl Int(string key, int def, int min, int max, string zh, string en,
        string domain = "break", string tier = "common") => new()
    {
        Key = key, Kind = SettingKind.Int, Default = def, Min = min, Max = max,
        Domain = domain, Tier = tier, CommentZh = zh, CommentEn = en,
    };

    /// 嵌套组：只有名字和两行注释，值在它名下那些点分键里。
    public static SettingDecl Group(string key, string zh, string en, string domain = "break") => new()
    {
        Key = key, Kind = SettingKind.Group, Default = "", Domain = domain,
        CommentZh = zh, CommentEn = en,
    };

    /// 一串用户自己写的分钟数。形状照 <see cref="Int"/>，只是类型换成了那一族。
    /// <paramref name="def"/> 是逗号分隔的分钟数（默认值在声明表里也只有这一份），
    /// <paramref name="min"/> 是<b>每一项</b>的地板，不是这一串的。
    public static SettingDecl DelayList(string key, string def, int min, int max, string zh, string en,
        string domain = "break", string tier = "common") => new()
    {
        Key = key, Kind = SettingKind.DelayList, Default = def, Min = min, Max = max,
        Domain = domain, Tier = tier, CommentZh = zh, CommentEn = en,
    };

    public int AsInt => (int)Default;
    public bool AsBool => (bool)Default;
    public string AsString => (string)Default;

    /// <see cref="SettingKind.ChoiceList"/> 的默认值写成逗号分隔，读出来才和文件里的
    /// 一个形状。
    public string[] AsStringList => AsString.Split(',');

    /// <see cref="SettingKind.DelayList"/> 的默认值也是逗号分隔，读出来是<b>分钟数</b>。
    /// 这里<b>不用</b>「认不出就跳过」那种宽容写法：默认值是本程序自己写的那一份，
    /// 它写错一道就该当场炸在声明表上，而不是让每个用户读到一个悄悄少了一格的默认。
    public int[] AsIntList =>
        AsString.Split(',').Select(part => int.Parse(part.Trim(),
            System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    /// 界面上那一格：<b>值 → 一行字</b>（逗号分隔的分钟数）。
    /// 它是界面上那一格的**唯一**出处 —— 灌值走它、读回走 <see cref="ReadDelays"/>，
    /// 所以那一格写出来的样子和它读得懂的样子不会分家。
    public string SpellDelays(List<int> minutes) =>
        string.Join(", ", minutes.Select(m => m.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// 界面上那一格：<b>一行字 → 值</b>。逗号分隔，每一项是分钟数。
    /// 认不出的那一项丢掉（写着字母、或者只写了一个逗号）；<b>一项都认不出就整串回默认</b> ——
    /// 和 <see cref="KeepKnown"/> 对 <see cref="SettingKind.ChoiceList"/> 干的那件事一个路子：
    /// 一个什么都读不出来的格子上，屏幕上是出厂那一串，而不是一格空的。
    public List<int> ReadDelays(string text)
    {
        var read = text.Split(',')
            .Select(part => int.TryParse(part.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .Where(n => n != null)
            .Select(n => ClampDelay(n!.Value))
            .ToList();
        return read.Count > 0 ? read : [.. AsIntList];
    }

    /// 绑定时要往属性上放的那个默认值，类型和属性一致。ChoiceList 在声明表里是一串逗号
    /// 分隔的文本，因为它要和别的声明共用一个字段；到手上是一个列表。
    public object DefaultAsValue =>
        Kind switch
        {
            SettingKind.ChoiceList => AsStringList.ToList(),
            // 每一份都是新的一份列表：绑在属性上之后改一处会改另一处，
            // 那一条是 `StepLayout.Default()` 上面写着的那一条（同一个坑，不该踩两次）。
            SettingKind.DelayList => AsIntList.ToList(),
            // 布局的默认值是**每次一份新的**：绑在属性上的时候
            // 「同一个引用的话改一处会改另一处」那一条就废了（`Step.Clone` 上面写着为什么）。
            SettingKind.LayoutList => StepLayout.Default(),
            _ => Default,
        };

    /// 一个已经拿到手上的值，回到这条声明允许的范围里。认不出的取值回到默认；
    /// 但 flow 那种自由文本不动 —— 它指向的是 flows 里的一个名字，抹掉它才是事故。
    public object Sanitize(object value) => Kind switch
    {
        SettingKind.Int => Clamp((int)value),
        SettingKind.HexColor => IsColor((string)value) ? (string)value : AsString,
        SettingKind.Choice or SettingKind.Text => OrKnown((string)value),
        SettingKind.ChoiceList => KeepKnown((List<string>)value),
        SettingKind.DelayList => KeepDelays((List<int>)value),
        // 布局夹的是三件事：认不出的种类丢掉、每种至多一个、份额回到声明的范围。
        // **哪几条算「不认不出」不由这里判** —— 那是 `StepLayout.Sanitize` 读同一份名单。
        SettingKind.LayoutList => StepLayout.Sanitize((List<StepLayout>)value),
        _ => value,
    };

    /// 认不出的 content 丢掉；全丢光了回到默认，而不是留一个什么都不画的 step。
    private List<string> KeepKnown(List<string> value)
    {
        var kept = value.Where(Choices.Contains).ToList();
        return kept.Count > 0 ? kept : [.. AsStringList];
    }

    /// 每一项回到声明给的范围（地板 1 分钟 —— 0 和负数会把调度算坏）。
    /// **一项都剩不下就回默认那一串**：序列是空的等于「取一格都取不出来」，
    /// 而那一条的下场由调度去管（见 `Scheduler.NextWait`），不在夹取这一步发明。
    private List<int> KeepDelays(List<int> value)
    {
        var kept = value.Select(ClampDelay).ToList();
        return kept.Count > 0 ? kept : [.. AsIntList];
    }
}

/// <summary>一张声明表到一组属性之间的绑定。默认值、读文档、写文档、夹取四件事都走这一份，
/// 所以一个键名在这份代码里只出现一次，而且能读回来的必定写得出去、写了出去的必定有夹取。
/// 设计文档 `docs/design/config.md`「每个设置只声明一次」那条靠的是它。</summary>
public sealed class SettingMap(string keyPrefix = "")
{
    /// 组表里的行共用同一批字段名，所以 step 的声明带着 "steps[]." 前缀。走到文档里
    /// 找键的时候要把前缀去掉。
    private readonly string _prefix = keyPrefix;

    private string Path(string key) =>
        key.StartsWith(_prefix, StringComparison.Ordinal) ? key[_prefix.Length..] : key;

    public sealed class Entry(SettingDecl decl, Func<object> get, Action<object> set)
    {
        public SettingDecl Decl { get; } = decl;
        public Func<object> Get { get; } = get;
        public Action<object> Set { get; } = set;
    }

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;
    public IEnumerable<string> Keys => _entries.Select(e => e.Decl.Key);

    /// 绑定的时候就把默认值取过去：属性上没有第二个默认值可写。
    public void Bind<T>(string key, Func<T> get, Action<T> set) where T : notnull
    {
        var decl = Settings.Decl(key);
        if (decl.IsContainer)
            throw new ReadProblem($"{key}: 组表要按它名下的叶子绑定，不能绑组本身");
        set((T)decl.DefaultAsValue);
        _entries.Add(new Entry(decl, () => get()!, v => set((T)v)));
    }

    public object Get(string key) => _entries.First(e => e.Decl.Key == key).Get();

    /// 设置窗里的控件往回写走这一条：过一遍声明的 Sanitize（夹取 + 认不出的取值回默认），
    /// 所以界面这边不可能写进一个文档里读不出来的值。
    public void Set(string key, object value)
    {
        var entry = _entries.First(e => e.Decl.Key == key);
        entry.Set(entry.Decl.Sanitize(value));
    }


    /// 一个键当前值在文档里的样子。设置窗里的控件和「恢复默认」也走这里，不再抄一份。
    public JsonNode ValueJson(string key) => JsonOf(Get(key));

    /// 文档里该有的那份值。点分键还原成嵌套对象，键的顺序就是绑定的顺序。
    public JsonObject ToJson()
    {
        var root = new JsonObject();
        foreach (var entry in _entries) Put(root, Path(entry.Decl.Key), JsonOf(entry.Get()));
        return root;
    }

    /// 从文档里读。没写的键保持默认值（设计文档 `docs/design/config.md`：「文件没提到的设置回到默认值」），写了但类型不对的键是一条
    /// 读不通 —— 交给调用去说，磁盘上的文件一个字都不动。
    public List<string> ReadFrom(JsonObject document)
    {
        var problems = new List<string>();
        foreach (var entry in _entries)
        {
            var node = Find(document, Path(entry.Decl.Key));
            if (node == null) continue;
            try
            {
                var value = entry.Decl.Coerce(node);
                // 布局那一族读出来是「值 + 它自己的报告」：元素级的问题（哪一个控件读不通）
                // 不该把整份布局扔掉 —— 那是勿扰那张 exe 名单的同一个做法。
                if (value is StepLayout.LayoutValue layout)
                {
                    problems.AddRange(layout.Problems);
                    entry.Set(layout.Controls);
                }
                else entry.Set(value);
            }
            catch (ReadProblem e) { problems.Add(e.Message); }
        }
        return problems;
    }

    public void SanitizeAll()
    {
        foreach (var entry in _entries) entry.Set(entry.Decl.Sanitize(entry.Get()));
    }

    /// <summary>2026-09-26 那次「日历里不许有像素」的重写给这一组键换了名字
    /// （取向记在 <c>docs/adr/0002-settings-and-records.md</c>「陌生的键不许有代价」那一节：像素的
    /// <c>offsetX/offsetY</c> 换成
    /// 工作区份额的 <c>marginXPercent/marginYPercent</c>，<c>coveragePercent</c> 拆成两条边，
    /// <c>scalePercent</c> 拆成三条）。<b>不写近似映射</b>：老键是像素、新键是份额，
    /// 一条 <c>scalePercent</c> 对不上三条新键，硬映射等于替用户编一个他没写过的数。
    /// 老键按设计文档 `docs/design/config.md` 那条「陌生的键不许有代价」原样留在文件里、不生效 ——
    /// 但别让人把「这一组回默认」当成 bug，所以读到它们时印一行说清现在生效的是哪几个。
    ///
    /// <b>2026-10-13 所有者定：这一族又添了两条</b> —— 「每取消一次翻倍、到封顶为止、一直重试」
    /// 那两根退休，换成「取消之后走一串固定格子的休息延迟、取完那一轮作罢」。它们同样原样
    /// 留在文件里、不生效，同样印一行说清现在生效的是哪一个键。
    ///
    /// <b>为什么两条老键都指着同一个新键</b>：一份用户文件里可能同时留着两根，而它们现在
    /// 归的是**同一个**键 —— 说清「现在生效的是哪个」比说「这一条没了」有用。</summary>
    public static readonly (string Old, string Now)[] RetiredKeys =
    [
        ("calendar.coveragePercent", "calendar.widthPercent + calendar.heightPercent"),
        ("calendar.offsetX", "calendar.marginXPercent"),
        ("calendar.offsetY", "calendar.marginYPercent"),
        ("calendar.scalePercent", "calendar.widgetWidthPercent + calendar.textPercent"),
        ("retryDelayMs", "restDelay"),
        ("maxRetryDelayMs", "restDelay"),
    ];

    /// 文档里出现过哪些改了名字的老键。走的是 <see cref="Find"/> 那一条点分路径，
    /// 和读声明用的是同一件事，所以嵌套的写法（<c>"calendar": { "offsetX": 24 }</c>）也认得。
    public static List<string> RetiredIn(JsonObject document)
    {
        var seen = new List<string>();
        foreach (var (old, now) in RetiredKeys)
            if (Find(document, old) != null) seen.Add($"{old} → {now}");
        return seen;
    }

    private static JsonNode JsonOf(object value) => value switch
    {
        int n => JsonValue.Create(n)!,
        bool flag => JsonValue.Create(flag)!,
        string text => JsonValue.Create(text)!,
        List<string> list => ListOf(list),
        List<int> numbers => NumbersOf(numbers),
        // 布局：一行一个控件，一个元素一个对象（次序就是叠的次序，见 `StepLayout` 的类型注释）。
        List<StepLayout> layout => LayoutOf(layout),
        _ => throw new ReadProblem($"{value.GetType().Name} 没有写进文档的办法"),
    };

    private static JsonArray LayoutOf(List<StepLayout> controls)
    {
        var array = new JsonArray();
        foreach (var control in controls) array.Add(control.ToJson());
        return array;
    }

    private static JsonArray ListOf(List<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(JsonValue.Create(item));
        return array;
    }

    private static JsonArray NumbersOf(List<int> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(JsonValue.Create(item));
        return array;
    }

    internal static void Put(JsonObject root, string dottedKey, JsonNode value)
    {
        var parts = dottedKey.Split('.');
        var node = root;
        for (int i = 0; i < parts.Length - 1; i++)
            node = node[parts[i]] as JsonObject ?? SetNewObject(node, parts[i]);
        node[parts[^1]] = value;
    }

    private static JsonObject SetNewObject(JsonObject parent, string key)
    {
        var nested = new JsonObject();
        parent[key] = nested;
        return nested;
    }

    /// 文档里没有这个键。和「有一个值是 null」区分开，所以走 TryGet 而不是直接取。
    internal static JsonNode? Find(JsonObject root, string dottedKey)
    {
        JsonNode? node = root;
        foreach (var part in dottedKey.Split('.'))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node)) return null;
        }
        return node;
    }
}

/// <summary>设置本身。属性是大写的 C# 名字，文档里的键名和它们的范围都在
/// <see cref="Declarations"/> 里。</summary>
public sealed class Settings
{
    /// 1 是 <c>content</c> / <c>text</c> 那份写法；**2 是布局那一份**（2026-10-06，契约
    /// 「遮罩的布局是每一步自己那一份」那一节）：一步的屏上有什么改成 <c>steps[].layout</c>，
    /// <c>content</c> 和 <c>text</c> 从此不再是权威，只是被读一次就翻成布局的三个成员名。
    ///
    /// **为什么这一格要动**：一份 1 的文件读进来会翻出一份布局（老配置照今天的排法
    /// 自动生成），而写回去的时候带出来的是 2 —— 文件自己说了它被翻过，
    /// 而「读得通但悄悄换过一份东西」那种状态是最难发现的一种。
    public const int SchemaVersion = 2;

    /// 那个格式版本不是设置，它描述的是这份文档由哪个格式写出。
    public const string VersionKey = "schemaVersion";

    public const string FileName = "settings.jsonc";

    /// <summary>「系统」那一域的 id —— <see cref="SettingDecl.Domain"/> 里的那一个字。
    /// 它是<b>这台机器的属性</b>那一族（界面语言、设置窗自己的配色、开机启动）的家。
    ///
    /// <b>为什么值得一个常量</b>：生活模式那一族按域划界线（设计文档 `docs/design/life-mode.md`「生活模式是一整套配置的快照」
    /// 那一节「生活模式装的是业务功能，不是这台机器的属性」那条），
    /// 「留在生活模式外面」这个判据直接读这一个值，而不是抄一份键名清单 ——
    /// 抄清单的那一份在加第 4 个系统级设置时就过期了，而过期得没有声音。
    /// 界面上那三格标签里的第三格（<c>Options.Domains</c>）用的是同一个字，两边对不对得上
    /// 由 <c>Options.Audit</c> 第 14 条对着每一行核。</summary>
    public const string SystemDomain = "system";

    /// <summary>「作息」那一域的 id —— <see cref="SettingDecl.Domain"/> 里的那一个字。
    /// 作息表这一族（总开关、时间块那张表、时间块行里的每一个字段）的家。
    ///
    /// <b>它不是「系统」那一域</b>，所以它<b>整族进生活模式</b>（设计文档 `docs/design/life-mode.md`「装的是业务功能，不是这台机器的属性」那一节，判据是域本身）：
    /// 「嘉靖 / 村上」那类预设生活模式带的就是这一族。
    /// 界面上那一格标签由设置窗摆（工单 02），那边读的是同一个字。</summary>
    public const string RoutineDomain = "routine";

    /// 作息表那张时间块表的键名（点分，组 + 键）。它是容器，所以没有属性、也不在绑定表里 ——
    /// 值在 <see cref="Blocks"/> 上，走 <see cref="ToDocument"/> / <see cref="ApplyBlocks"/> 两条。
    public const string BlocksKey = "routine.blocks";

    /// 作息表那张表的<b>总开关</b>那一条的键名。它是这一族里**唯一一条挂在绑定表上**的
    /// （块行里那些字段两层深，走的是 <see cref="BlockDeclarations"/>），而界面上那一行、
    /// 搜索按它认、自检里对着它核 —— 有了这个常量，这几处就不必各抄一遍键名。
    public const string RoutineEnabledKey = "routine.enabled";

    /// <summary>勿扰那一族三个键的键名（<c>dnd.enabled</c> / <c>dnd.windows</c> / <c>dnd.apps</c>）。
    /// 键形和分域定在设计文档 `docs/design/dnd.md`「勿扰是用户定义的静默规则」那一节：
    /// 一个总开关、一张时段表、一张 exe 名单，归「休息提醒」那一域、跟着生活模式整套走。
    ///
    /// **为什么是三个常量而不是三处字面量**：界面上那两族各有自带的编辑器、自检里对着它们核，
    /// 而「这一族是哪几个键」在那些地方要各问一次 —— 字面量抄三遍就是三份会过期的东西。</summary>
    public const string DndEnabledKey = "dnd.enabled";

    /// 那张时段表。它是容器（<see cref="SettingKind.WindowTable"/>），所以没有属性、也不在绑定表里 ——
    /// 值在 <see cref="DndWindows"/> 上，走 <see cref="ToDocument"/> / 读文档那两条，和
    /// <see cref="BlocksKey"/> 同一处境。
    public const string DndWindowsKey = "dnd.windows";

    /// 那张 exe 名单。容器（<see cref="SettingKind.NameList"/>），值在 <see cref="DndApps"/> 上 ——
    /// 名单的每一项是用户自己写的一个词，所以它不挂可选值（那一族没有本程序给的取值）。
    public const string DndAppsKey = "dnd.apps";

    /// <summary>种类里<b>唯一带行为语义</b>的那一个取值：标着它的时间块里一切触发都不抬遮罩
    /// （设计文档 `docs/design/daily-routine.md`「作息表是一张按现实时间走的表，时间块是表里的行」那一节）。
    ///
    /// <b>为什么其余种类不带任何默认政策</b>：所有者的定夺是「默认值跟种类有什么关系？作息模式
    /// 有需要自己会去覆盖默认值的」—— 所以 <see cref="TimeBlock.Fade"/> 和
    /// <see cref="TimeBlock.YieldsToInput"/> 逐块显式写，从不从种类推（取向和代价：
    /// <c>docs/adr/0005-routine-text-and-preset-life-flows.md</c>「「睡眠」这个种类的静默是种类
    /// 语义，不是默认政策」那一节）。</summary>
    public const string BlockKindSleep = "sleep";

    public const string FlowFadeTop = "fadetop";
    public const string FlowTodoList = "todoList";

    /// 番茄钟那一行的名字。它和另外两行一样是 flows 里的一行 —— 番茄钟的<b>休息</b>段
    /// 走的就是它，可调的地方和别的休息流程完全同权（休息流程编辑器）。
    /// 专注段多长不住在这张表里，它是 <c>pomodoro.workMin</c> 那个键的事。
    public const string FlowPomodoro = "pomodoro";

    /// flows 那张表的名字。它和其他键一样在声明表里有一行（为了让首启文档里它上面也有
    /// 两行注释），但它装的是用户写的行，不是本程序能夹取的取值。
    public const string ModesKey = "flows";

    /// 属性不写默认值。默认值只在下面的声明表里出现一次，构造时从那里取——不然「一个
    /// 设置只声明一次」这句话在这一个文件里就不成立了。文本属性上的 = null! 不是默认值，
    /// 是告诉编译器「构造函数会通过一个闭包把这里填上」；真漏了一个键，
    /// SelfCheck 的「声明了但没有绑定」那条会抓到。
    public int ActivityTimeoutMin { get; set; }
    public string Flow { get; set; } = null!;
    public Dictionary<string, Flow> Flows { get; set; }
    public PomodoroOptions Pomodoro { get; set; }
    public int MaxOpacityPercent { get; set; }
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    public bool StayOnTopAfterLanding { get; set; }
    public int MinBreakMs { get; set; }
    /// <summary>取消一次休息之后那一串「休息延迟」，<b>单位是分钟</b>，一格一格往后取
    /// （见 <see cref="Scheduler.NextWait"/>）。界面上那一格是一行逗号分隔的字，
    /// 文件里是一个数组 —— 两个形状各走各的那条路（<see cref="SettingDecl.SpellDelays"/> /
    /// <see cref="SettingDecl.ReadDelays"/> / <see cref="SettingDecl.AsIntList"/>）。</summary>
    public List<int> RestDelays { get; set; } = null!;
    public int TriggerPauseMs { get; set; }
    public int DismissHoldMs { get; set; }
    public int TextScalePercent { get; set; }
    public string ForegroundColor { get; set; } = null!;
    public string BackgroundColor { get; set; } = null!;
    public ImageOptions Image { get; set; }
    public SoundOptions Sound { get; set; }
    public CalendarOptions Calendar { get; set; }
    public bool Clock24Hour { get; set; }
    public bool BlockWhenFullscreen { get; set; }
    public bool AutoFade { get; set; }
    public bool AllMonitors { get; set; }
    public bool Autostart { get; set; }
    public string Language { get; set; } = null!;

    /// 作息表整张表要不要用（那一族的总开关）。关掉它，块的行为全部回到现状。
    /// 属性上不写默认值：默认值只在声明表里出现一次（构造函数从那里取）。
    public bool RoutineEnabled { get; set; }

    /// 那张时间块表：<b>一行一个时间块</b>。和 <see cref="Flows"/> 一样藏着两层深，
    /// 没有一个键能直接指到它，所以读、写、夹取三处都单独走一遍。
    /// 出厂一个时间块都没有 —— 那是内容，预设生活模式那一张工单写；这一族只管形状。
    public List<TimeBlock> Blocks { get; set; }

    /// <summary>勿扰要不要用（那一族的总开关）。属性上不写默认值：默认值只在声明表里出现一次。
    ///
    /// 关掉它，<see cref="Dnd"/> 求出来永远是「不成立」，而时段表和名单都还在表里 ——
    /// 这一点和 <see cref="RoutineEnabled"/> 同一处置（算术不读开关，开关住在设置上）。</summary>
    public bool DndEnabled { get; set; }

    /// 那张时段表：<b>一行一条时段</b>。和 <see cref="Blocks"/> 一样藏着两层深、没有一个键能直接
    /// 指到它，所以读、写、夹取三处都单独走一遍（同一个道理，不同的行族）。
    /// 出厂一条都没有 —— 那是内容；这一族只管形状。</summary>
    public List<DndWindow> DndWindows { get; set; }

    /// 那张 exe 名单：一列词。行是用户自己敲的，所以它不挂可选值；出厂是空的。</summary>
    public List<string> DndApps { get; set; }

    /// 设置窗自己的配色（<c>light</c> / <c>dark</c>，默认 <c>light</c>）。
    /// 声明 2026-09-30 加的。<b>绑定是漏不得的</b>：声明表里有一条而这里没绑，
    /// <c>ToDocument()</c> 那一路 <c>Map.Get()</c> 就会抛「Sequence contains no matching
    /// element」—— 表现是「自检一跑就炸」，而不是「这一条没生效」，所以看不出是哪一条少了。
    public string UiTheme { get; set; } = null!;

    /// 上面每个属性到它那个键的绑定。构造一遍就把三件事定下来了：默认值从声明取、
    /// 读文档知道往哪个属性写、写文档知道从哪个属性取。
    private readonly SettingMap _map = new();

    public SettingMap Map => _map;

    public Settings()
    {
        _map.Bind("activityTimeoutMin", () => ActivityTimeoutMin, v => ActivityTimeoutMin = v);
        _map.Bind("flow", () => Flow, v => Flow = v);
        Flows = BuiltInFlows();

        Pomodoro = new PomodoroOptions();
        _map.Bind("pomodoro.workMin", () => Pomodoro.WorkMin, v => Pomodoro.WorkMin = v);

        _map.Bind("maxOpacityPercent", () => MaxOpacityPercent, v => MaxOpacityPercent = v);
        _map.Bind("fadeInMs", () => FadeInMs, v => FadeInMs = v);
        _map.Bind("fadeOutMs", () => FadeOutMs, v => FadeOutMs = v);
        _map.Bind("stayOnTopAfterLanding", () => StayOnTopAfterLanding, v => StayOnTopAfterLanding = v);
        _map.Bind("minBreakMs", () => MinBreakMs, v => MinBreakMs = v);
        _map.Bind("restDelay", () => RestDelays, v => RestDelays = v);
        _map.Bind("triggerPauseMs", () => TriggerPauseMs, v => TriggerPauseMs = v);
        _map.Bind("dismissHoldMs", () => DismissHoldMs, v => DismissHoldMs = v);
        _map.Bind("textScalePercent", () => TextScalePercent, v => TextScalePercent = v);
        _map.Bind("foregroundColor", () => ForegroundColor, v => ForegroundColor = v);
        _map.Bind("backgroundColor", () => BackgroundColor, v => BackgroundColor = v);

        Image = new ImageOptions();
        _map.Bind("image.enabled", () => Image.Enabled, v => Image.Enabled = v);
        _map.Bind("image.file", () => Image.File, v => Image.File = v);
        _map.Bind("image.position", () => Image.Position, v => Image.Position = v);

        Sound = new SoundOptions();
        _map.Bind("sound.enabled", () => Sound.Enabled, v => Sound.Enabled = v);
        _map.Bind("sound.file", () => Sound.File, v => Sound.File = v);
        _map.Bind("sound.fadeIn", () => Sound.FadeIn, v => Sound.FadeIn = v);
        _map.Bind("sound.fadeOut", () => Sound.FadeOut, v => Sound.FadeOut = v);
        _map.Bind("sound.volume", () => Sound.Volume, v => Sound.Volume = v);
        _map.Bind("sound.repeat", () => Sound.Repeat, v => Sound.Repeat = v);
        _map.Bind("sound.afterLanding", () => Sound.AfterLanding, v => Sound.AfterLanding = v);

        Calendar = new CalendarOptions();
        _map.Bind("calendar.enabled", () => Calendar.Enabled, v => Calendar.Enabled = v);
        _map.Bind("calendar.style", () => Calendar.Style, v => Calendar.Style = v);
        _map.Bind("calendar.corner", () => Calendar.Corner, v => Calendar.Corner = v);
        _map.Bind("calendar.widthPercent", () => Calendar.WidthPercent, v => Calendar.WidthPercent = v);
        _map.Bind("calendar.heightPercent", () => Calendar.HeightPercent, v => Calendar.HeightPercent = v);
        _map.Bind("calendar.widgetWidthPercent", () => Calendar.WidgetWidthPercent, v => Calendar.WidgetWidthPercent = v);
        _map.Bind("calendar.headerPercent", () => Calendar.HeaderPercent, v => Calendar.HeaderPercent = v);
        _map.Bind("calendar.marginXPercent", () => Calendar.MarginXPercent, v => Calendar.MarginXPercent = v);
        _map.Bind("calendar.marginYPercent", () => Calendar.MarginYPercent, v => Calendar.MarginYPercent = v);
        _map.Bind("calendar.textPercent", () => Calendar.TextPercent, v => Calendar.TextPercent = v);
        _map.Bind("calendar.opacityPercent", () => Calendar.OpacityPercent, v => Calendar.OpacityPercent = v);
        _map.Bind("calendar.weekStartsMonday", () => Calendar.WeekStartsMonday, v => Calendar.WeekStartsMonday = v);
        _map.Bind("calendar.showLunar", () => Calendar.ShowLunar, v => Calendar.ShowLunar = v);

        // 作息表那一族：只有总开关是绑在属性上的（那张表是容器，和 flows 一样单独走）。
        // 绑定的位置和声明表里那两行的位置一样 —— 顺序对不上 SelfCheck 会报。
        Blocks = [];
        _map.Bind(RoutineEnabledKey, () => RoutineEnabled, v => RoutineEnabled = v);

        _map.Bind("clock24Hour", () => Clock24Hour, v => Clock24Hour = v);
        _map.Bind("blockWhenFullscreen", () => BlockWhenFullscreen, v => BlockWhenFullscreen = v);
        _map.Bind("autoFade", () => AutoFade, v => AutoFade = v);
        // 勿扰那一族：总开关是这一族里**唯一一条挂在绑定表上**的（时段表和名单都是容器，
        // 和上面那两张表一样单独走）。绑定的位置和声明表里那几行的位置一样（它们就排在
        // autoFade 后面）—— 顺序对不上 SelfCheck 会报。
        DndWindows = [];
        DndApps = [];
        _map.Bind(DndEnabledKey, () => DndEnabled, v => DndEnabled = v);
        _map.Bind("allMonitors", () => AllMonitors, v => AllMonitors = v);
        _map.Bind("autostart", () => Autostart, v => Autostart = v);
        // 2026-10-06：`trailLength`（休息轨迹画多少格）跟着遮罩上那排方块一起撤了，
        // 所以这里不再 bind 它。别人配置里还写着的那一行按「陌生键」读进来
        // （ADR-0002「改过名字的日历老键原样留着」）：读得进来、不生效、不报错、导出原样留着。
        _map.Bind("language", () => Language, v => Language = v);
        _map.Bind("uiTheme", () => UiTheme, v => UiTheme = v);
    }

    /// <summary>设置窗的一份工作副本：值从当前这份抄过去，包括 flows 那张表。
    /// 副本用来编辑，按「保存」才 <see cref="AdoptValues"/> 搬回来。</summary>
    public Settings Clone()
    {
        var copy = new Settings();
        copy.AdoptValues(this);
        return copy;
    }

    /// <summary>把另一份的值搬到这一份上来，<b>对象不换</b>。App、Scheduler 和每块屏的 Fader
    /// 手里拿的都是同一个实例，换对象会让它们继续读旧的那一份 —— 保存这一步就白做了。
    /// 每个值都过一遍声明的 Sanitize，所以搬进来的不会是界面写歪的东西。</summary>
    public void AdoptValues(Settings from)
    {
        foreach (var entry in _map.Entries)
            entry.Set(entry.Decl.Sanitize(from.Map.Get(entry.Decl.Key)));

        // flows 藏在两层深，没有一个键能指到它，所以按文档形状走一遍现成的读法。
        var flows = new Dictionary<string, Flow>();
        foreach (var (name, flow) in from.Flows)
        {
            var row = new Flow();
            row.ReadFrom(name, flow.ToJson());
            flows[name] = row;
        }
        Flows = flows;

        // 时间块那张表同样藏在两层深。它照上面 flows 那一路走：一份 <see cref="ToJson"/>
        // 读回来就是一份**新的行** —— 共用同一个引用的话，改副本里那一块会把活的那一份
        // 也改了（设置窗里「按保存才搬」那条就废了）。
        var blocks = new List<TimeBlock>();
        foreach (var block in from.Blocks)
        {
            var row = new TimeBlock();
            row.ReadFrom(block.ToJson());
            blocks.Add(row);
        }
        Blocks = blocks;

        // 勿扰那两张表走上面那两段一模一样的路：一份 <see cref="DndWindow.ToJson"/> 读回来
        // 就是一份**新的行**，名单是**新的列表** —— 共用同一个引用的话，副本里改一行会把活的
        // 那一份也改了（设置窗里「按保存才搬」那条就废了）。
        var windows = new List<DndWindow>();
        foreach (var window in from.DndWindows)
        {
            var row = new DndWindow();
            row.ReadFrom(window.ToJson());
            windows.Add(row);
        }
        DndWindows = windows;
        DndApps = [.. from.DndApps];
    }

    /// <summary>一份文档该有的值：格式版本在最前，其余按 <see cref="Declarations"/> 的顺序，

    /// 点分键还原成嵌套对象。写出去的是它，原位编辑时用来比「这个键变没变」的也是它，
    /// 所以一个键在这里只出现一次。</summary>
    public JsonObject ToDocument()
    {
        var root = new JsonObject { [VersionKey] = SchemaVersion };
        foreach (var decl in Declarations)
        {
            switch (decl.Kind)
            {
                // 组自己不写值，值在它名下那些叶子上；叶子写的时候会把组带出来。
                case SettingKind.Group: break;
                case SettingKind.ModeTable: root[decl.Key] = ModesToJson(); break;
                // 时间块那张表和 flows 那张表同一种处境（值在表里，键名指不到），
                // 但它的键是点分的，所以走 Put 那一条 —— 组那一层是它带出来的。
                // 勿扰那两张表同这一条：时段表也是一层一行的记录表，名单是一列词。
                case SettingKind.BlockTable: SettingMap.Put(root, decl.Key, BlocksToJson()); break;
                case SettingKind.WindowTable: SettingMap.Put(root, decl.Key, WindowsToJson()); break;
                case SettingKind.NameList: SettingMap.Put(root, decl.Key, AppsToJson()); break;
                default: SettingMap.Put(root, decl.Key, _map.ValueJson(decl.Key)); break;
            }
        }
        return root;
    }

    /// <summary>从一份文档读进来。没写的键保持默认值，写了但类型不对的键是一条读不通；
    /// 两种情况都不碰磁盘。读到的值还要过一遍 <see cref="ClampToDeclarations"/>。</summary>
    public List<string> Apply(JsonObject document)
    {
        var problems = _map.ReadFrom(document);
        problems.AddRange(ApplyFlows(document));
        problems.AddRange(ApplyBlocks(document));
        problems.AddRange(ApplyWindows(document));
        problems.AddRange(ApplyApps(document));
        return problems;
    }

    /// <summary>按文档里的顺序排。首启那份文档就是这个顺序，控件名也从这里取。</summary>
    public static readonly SettingDecl[] Declarations =
    [
        // 上限拆了（2026-09-27 他点头那一档）：那根 60 分钟的天花板现在不在了，界面上和文件里都不会再冒出一个用户没写过的数。下限 1 留着。
        SettingDecl.Int("activityTimeoutMin", 25, 1, int.MaxValue,
            "连续用键盘鼠标多久之后开始淡入。写多少是多少，没有上限。",
            "Fading starts after this much uninterrupted keyboard/mouse activity. "
            + "Minutes, with no ceiling."),
        new()
        {
            Key = "flow", Kind = SettingKind.Text, Default = FlowTodoList,
            CommentZh = "一次休息要走的那条休息流程。",
            CommentEn = "Which flow a break walks through.",
        },
        new()
        {
            Key = ModesKey, Kind = SettingKind.ModeTable, Default = "",
            // 这一条的注释是**界面上原样显示的那一行**（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节），所以它写给用这个东西的人看：说「一步」不说 step，说「时钟/清单/
            // 文字」不说 content。自检里那一组判据对着界面上每一个字扫这几个词。
            // （休息轨迹和健康度 2026-10-06 从遮罩上撤了，所以这一句里那两个说法也去掉了 ——
            //  界面上已经不摆它们，这句话还留着就成了在介绍一样看不见的东西。）
            CommentZh = "Keeper 认识的每条休息流程。淡入到达第一步之后按顺序走；"
                        + "每一步摆的东西从时钟、休息清单、这一步自带的文字里挑。"
                        + "自带的三行写在这里，是为了能被改，而不只是被选。",
            CommentEn = "Every flow Keeper knows. After the fade lands it walks them in order; "
                        + "each stretch shows any of the clock, the checklist and its own words. "
                        + "The three shipped flows are written out so they "
                        + "can be edited rather than merely chosen.",
        },
        SettingDecl.Group("pomodoro",
            "番茄钟：托盘里开始之后，按现实时间一轮一轮走；专注段里屏幕照常，到点抬一次休息。",
            "The Pomodoro: a wall-clock loop started from the tray — the screen stays as it is during a "
            + "focus stretch, and a break goes up when the clock runs out."),
        // 番茄钟在设置窗里是**自己一区**（`Options` 的 SectionTitles 里那一条）。原来它是
        // 高级、开窗收着，于是那一区默认视图下只剩一个光标题 —— 自检里 Audit 第 17 条
        // （一区至少要有一条常规行）量的就是那件事。默认 25 分钟是这一区里唯一一个可调
        // 的数，也是「我这一轮想按多久工作」最直接的答案，所以留在常规里。
        SettingDecl.Int("pomodoro.workMin", 25, 1, int.MaxValue,
            "番茄钟的专注段多长。写多少是多少。休息多长不在这里 —— 休息那一段走的是「番茄钟」那条休息流程。",
            "How long one focus stretch lasts. Minutes, with no ceiling. The break is not set "
            + "here: it follows the Pomodoro flow.",
            domain: "break"),
        // 2026-10-06 所有者定夺：「遮罩这应该属于高级设置，没有人天天改遮罩颜色的」。
        // 这一族（最暗不透明度、淡入淡出时长、前景色底色、字号、足迹格数、时钟制式）
        // 全部是高级 —— 遮罩那张卡在默认视图下一条都不露，靠窗头那颗开关收放。
        SettingDecl.Int("maxOpacityPercent", 75, 5, 100,
            "遮罩最暗的时候有多不透明。",
            "How opaque the fader gets at its darkest.", tier: "advanced"),
        SettingDecl.Int("fadeInMs", 5_000, 300, 15_000,
            "屏幕缓缓压进那种颜色，要压多久。一条休息流程的第一步从它压完的地方开始。",
            "How long the screen takes to sink into that colour. A flow's first stretch "
            + "begins where this ends.", tier: "advanced"),
        SettingDecl.Int("fadeOutMs", 1_000, 200, 10_000,
            "休息结束之后，屏幕退回去要退多久。",
            "How long the screen takes to come back at the end of a break.", tier: "advanced"),
        new()
        {
            // 默认 true（2026-10-29 所有者定夺）：休息时段始终盖上。
            // 切到别的程序也盖着；关掉才让它留在原处、让别的窗口盖得住。
            Key = "stayOnTopAfterLanding", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "淡入到位之后仍压在别的窗口上面。打开时你切过去的程序也盖不住它——"
                        + "休息时段始终盖着；关掉就让那个程序盖得住它。",
            CommentEn = "Whether the fade stays above every other window once it has landed. "
                        + "On keeps the break covering everything, even a window you switch to; "
                        + "off lets that window cover it.",
        },
        SettingDecl.Int("minBreakMs", 20_000, 3_000, 120_000,
            "绿幕至少要站这么久，这一次才算真的休息了。站得比这短记成跳过。",
            "A fade held at least this long counts as a real break. Shorter than this, it is "
            + "skipped.", tier: "advanced"),
        SettingDecl.DelayList("restDelay", "1, 3", 1, int.MaxValue,
            "你取消掉一次休息之后，隔多久再提醒你。取消就是这一次不去 —— 把它挥下去，"
            + "或者按遮罩上那颗「提前结束休息」，两者一样。取消一次往后推一格，一格一格往后取；"
            + "取完就不再提醒你，直到你重新攒满一整段。这一格写几分钟一格，中间用逗号分开 —— "
            + "写了几格就有几格。歇过、或者离开过键盘，这个计数复位。",
            "How long Keeper waits after you cancel a break — waving the fade away or pressing "
            + "End the break, both the same. Each cancellation pushes the next reminder back one "
            + "step; once the steps run out, Keeper stops reminding you until you have worked a "
            + "full stretch again. Minutes per step, separated by commas: as many steps as you "
            + "write. Taking a break, or leaving the keyboard, resets the count.",
            tier: "advanced"),
        SettingDecl.Int("triggerPauseMs", 1_000, 0, 5_000,
            "键盘鼠标停下来这么久，它才开始淡入 —— 挑你停手的空隙落下来，不打断你写到一半的词。",
            "A short lull in input is what FadeTop calls fading in tentatively.", tier: "advanced"),
        SettingDecl.Int("dismissHoldMs", 2_000, 300, 15_000,
            // 键盘和鼠标一起算「输入」，不分哪一双手在动 —— 判定那一层没有设备的筛子
            // （`Activity.Judge` 的 Counts 是「键盘 || 过了合流窗」）。所以这一条要说
            // 「输入」，说「鼠标」就把键盘那一半漏在句子外边了。
            "输入要连着来这么久，才算你真的回来了。键盘和鼠标都算数。只来一下不算数。",
            "How long keyboard and mouse input has to keep coming before it counts as being back. "
            + "One touch does not.", tier: "advanced"),
        SettingDecl.Int("textScalePercent", 100, 50, 300,
            "遮罩上那些字的大小：时钟、倒计时、休息清单和时间块名。",
            "Size of the text on the fader: the clock, the countdown, the checklist "
            + "and the block name.", tier: "advanced"),
        new()
        {
            Key = "foregroundColor", Kind = SettingKind.HexColor, Default = "#FFFFFF",
            Tier = "advanced",
            CommentZh = "遮罩上文字的颜色。",
            CommentEn = "Overlay text colour.",
        },
        new()
        {
            Key = "backgroundColor", Kind = SettingKind.HexColor, Default = "#4CAF7A",
            Tier = "advanced",
            CommentZh = "遮罩底色。",
            CommentEn = "Overlay background colour.",
        },
        SettingDecl.Group("image",
            "遮罩背后那张图，替掉纯色底。",
            "The picture behind the fade, instead of a flat colour."),
        new()
        {
            Key = "image.enabled", Kind = SettingKind.Bool, Default = false,
            CommentZh = "用一张图替掉纯色底。",
            CommentEn = "Show a picture behind the fade instead of a flat colour.",
        },
        new()
        {
            Key = "image.file", Kind = SettingKind.Text, Default = "",
            Tier = "advanced",
            CommentZh = "图片的绝对路径。",
            CommentEn = "Absolute path to the picture.",
        },
        new()
        {
            Key = "image.position", Kind = SettingKind.Choice, Default = StepLayout.DefaultFitMode,
            Tier = "advanced",
            // 五个摆法只有一份名单（`StepLayout.FitModes`），布局里那个图控件走同一份 ——
            // 写两处就有一处会过期，而过期的那个是没人看得见的。
            Choices = StepLayout.FitModes,
            CommentZh = "图怎么铺满屏幕。",
            CommentEn = "How the picture covers the screen.",
        },
        SettingDecl.Group("sound",
            "休息期间放的那个文件，以及怎么放。",
            "The file that plays through a break, and how it plays."),
        new()
        {
            Key = "sound.enabled", Kind = SettingKind.Bool, Default = false,
            CommentZh = "遮罩在屏幕上的时候放声音。",
            CommentEn = "Play a sound while the fade is on screen.",
        },
        new()
        {
            Key = "sound.file", Kind = SettingKind.Text, Default = "",
            Tier = "advanced",
            CommentZh = "声音文件的绝对路径。",
            CommentEn = "Absolute path to the sound file.",
        },
        new()
        {
            Key = "sound.fadeIn", Kind = SettingKind.Bool, Default = false,
            Tier = "advanced",
            CommentZh = "跟着淡入把音量推上来。",
            CommentEn = "Ramp the sound up over the fade-in.",
        },
        new()
        {
            // 默认 true 是写出来的，不是推出来的：闹钟跟遮罩一起淡出才是它该有的样子。
            Key = "sound.fadeOut", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "跟着淡出把音量收掉。",
            CommentEn = "Ramp the sound down over the fade-out.",
        },
        SettingDecl.Int("sound.volume", 50, 0, 100,
            "播放音量，0 到 100。",
            "Playback volume, 0 to 100."),
        new()
        {
            Key = "sound.repeat", Kind = SettingKind.Bool, Default = false,
            Tier = "advanced",
            CommentZh = "文件放完了再放一遍——曲子比休息短的时候就靠它了。",
            CommentEn = "Start the file again once it has run out.",
        },
        new()
        {
            // 默认 true 是设计文档 `docs/design/fader-window.md`「「落定」是哪一个时刻」那一节那一句：
            // 落定之后才开始放声音。拧成 false 是「哪一步 step 说要声，那一步就响，淡入途中也响」。
            Key = "sound.afterLanding", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "等遮罩落定了才开始放；关掉它就跟着那一步一起响，淡入途中也算。",
            CommentEn = "Wait until the fade has landed. Off: a stretch that wants sound plays it "
                        + "including during the fade-in.",
        },
        SettingDecl.Group("calendar",
            "留在桌面上的那个月历。这一组里没有一个像素：每条尺寸都是工作区的一条百分比，"
            + "格子和字号再从那些百分比往下推，所以换一块屏换的是画面，不是那份配置。",
            "The calendar that lives on the desktop. Nothing here is a pixel: every size is a "
            + "share of the work area, and cells and text follow from those shares.",
            "calendar"),
        new()
        {
            Key = "calendar.enabled", Kind = SettingKind.Bool, Default = true,
            Domain = "calendar",
            CommentZh = "把这一月的格子留在桌面上。",
            CommentEn = "Keep the month grid on the desktop.",
        },
        new()
        {
            Key = "calendar.style", Kind = SettingKind.Choice, Default = "wallpaper",
            Domain = "calendar",
            Choices = ["wallpaper", "outline", "widget"],
            // 「铺满桌面的月历 / 只有格线的月历 / 角落里的小面板」这三个名字是**界面上那一栏
            // 里的字**（语言表里那三条），不是配置里那三个取值。原来这句直接写着
            // wallpaper / outline / widget —— 那是写给改配置的人看的（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节），自检里「设置窗文案」那一组当场报。
            CommentZh = "三种画法：铺满桌面的月历和只有格线的月历是同一张月历铺在桌面上，"
                        + "只是画不画面板不一样；角落里的小面板是另一块，格子下面还放一天的备注。",
            CommentEn = "Which drawing to use.",
        },
        new()
        {
            Key = "calendar.corner", Kind = SettingKind.Choice, Default = "topRight",
            Domain = "calendar",
            Choices = ["topLeft", "topRight", "bottomLeft", "bottomRight"],
            CommentZh = "贴着主屏的哪个角。离那个角多远，横竖各是一条，都是工作区的百分比 —— "
                        + "所以换一块分辨率的屏不用回头重挑这两个数。",
            CommentEn = "Which corner it sits in. How far in from that corner, "
                        + "sideways and down, each a share of the work area — "
                        + "so a screen of another size needs neither of them touched.",
        },
        // 默认 **40**（2026-10-12 所有者定夺「桌面日历的宽度改为屏幕的 2/5」）。
        // 五分之二正好是 40%，这根键收的就是整数百分比，一个百分点都不用让。
        //
        // **范围下限 20 是声明表里的**，`40` 在里面，滑块不动。
        //
        // **只改默认值，不改算术** —— 这个滑块仍然能拧大拧小，拧小是有意的。
        SettingDecl.Int("calendar.widthPercent", 40, 20, 100,
            "铺在桌面上的那两档：月历横向占工作区多少。角落里的小面板不看这一条，它有自己的一行。",
            "The grid styles: the month's width as a share of the work area.", "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.heightPercent", 100, 20, 100,
            "格子那两档：月历纵向占工作区多少。两条边各取一份，所以换一块形状的屏不必重挑。",
            "The grid styles: the month's height as a share of the work area.", "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.widgetWidthPercent", 18, 6, 60,
            "角落里的小面板：那块面板横向占工作区多少，格子和字号再从它推。铺在桌面上的那两档不看这个。",
            "The compact corner panel: how much of the work area's width it takes. "
            + "Cells and text follow from it. The two desktop-wide drawings ignore this.", "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.headerPercent", 7, 3, 25,
            "抬头那一整带（月份和星期那两行）占面板高度多少。这一条说的是铺在桌面上的那两档 —— "
            + "角落里的小面板抬头本来就是一格高。",
            "The header band — month and weekday lines — as a share of the panel height. The grid styles only.",
            "calendar", tier: "advanced"),
        // 两条边距默认 **0**，也就是**贴着那个角**（2026-10-12 所有者定夺
        // 「桌面日历右边没有贴边，这是 bug」）。竖着那一向原先看着是贴着的 —— 纵向占满工作区
        // 高度时 `Corner` 会把它夹回上沿 —— 横向那一向没有这道夹取，`1%` 就是屏边到日历
        // 之间一条看得见的缝（2560 宽的屏上是 24 像素）。今天改成同一档：横竖都是 0。
        //
        // **滑块仍然能拧开**：拧开就是「离那个角留一条边」。
        SettingDecl.Int("calendar.marginXPercent", 0, 0, 25,
            "离那个角横向多远，工作区宽度的百分比。0 是贴着那一侧的屏边。",
            "Distance from that corner, sideways, as a share of the work area's width. "
            + "Zero puts it flush against the screen edge on that side.", "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.marginYPercent", 0, 0, 25,
            "离那个角纵向多远，工作区高度的百分比。0 是贴着那一侧的屏边。",
            "Distance from that corner, up or down, as a share of the work area's height. "
            + "Zero puts it flush against the screen edge on that side.", "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.textPercent", 100, 50, 250,
            "字在推导出来的那个大小上再乘多少。它有一根按系统缩放放大的下限，"
            + "所以拧到最小也不会有字小到读不出来。",
            "Text size as a multiplier on what the layout derives, floored by a DPI-aware minimum.",
            "calendar", tier: "advanced"),
        SettingDecl.Int("calendar.opacityPercent", 88, 20, 100,
            "面板对着壁纸有多实。",
            "How solid the panel looks against the wallpaper.", "calendar", tier: "advanced"),
        new()
        {
            Key = "calendar.weekStartsMonday", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            Domain = "calendar",
            CommentZh = "一周从周一开始，而不是周日。",
            CommentEn = "Start the week on Monday rather than Sunday.",
        },
        new()
        {
            Key = "calendar.showLunar", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            Domain = "calendar",
            CommentZh = "每个日号下面写农历日期，或者那天过的节日。",
            CommentEn = "Write the lunar date, or the festival the day falls on, under each number.",
        },
        // ── 作息这一族 ──────────────────────────────────────────────────────
        // 三条声明：组、总开关、时间块那张表。**块行里那些字段不在这里** —— 它们和
        // step 那几个字段是同一种处境（表里的行，键名是裸的），所以住在
        // BlockDeclarations，两份表各自只有一份那个字段的声明。
        SettingDecl.Group("routine",
            "作息表：按现实时间排的一整天安排，一行一个时间块。",
            "The daily routine: a whole day arranged by the wall clock, one time block per row.",
            RoutineDomain),
        new()
        {
            // 默认 true 是**写出来的判断**：表里一个时间块都没有时，开着和关着没有区别，
            // 而「默认关着」会让这一族看起来像是坏了、还要人去找那颗开关。真要退回现状
            // 的人把它拧成 false，那之后块的行为全部回到现状（接线是调度那一侧的事）。
            Key = RoutineEnabledKey, Kind = SettingKind.Bool, Default = true,
            Domain = RoutineDomain,
            CommentZh = "整张作息表要不要用。关掉它之后块的行为全部回到现状 —— 连续用键盘鼠标太久"
                        + "的那次提醒和番茄钟各按各的老规矩。表里一个时间块都没有时，"
                        + "开着和关着没有区别。",
            CommentEn = "Whether the daily routine is in use at all. Off, every block goes back to "
                        + "normal and the other two triggers carry on as they always have. With no "
                        + "time block in the table, on and off are the same thing.",
        },
        new()
        {
            Key = BlocksKey, Kind = SettingKind.BlockTable, Default = "",
            Domain = RoutineDomain,
            // 同样一句提醒：这一条的注释是**界面上原样显示的那一行**（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节），所以它说「时间块」「一天里的第几分钟」，不说 blocks、
            // 键名，也不说毫秒 —— 这一族一个时长单位都没有。
            CommentZh = "时间块，一行一个。一行写清这一块从哪一分钟到哪一分钟、叫什么、是哪种、"
                        + "到点那一次抬不抬遮罩、走哪一行休息流程、连续输入的提醒在这一块里让不让位。"
                        + "不晚于开始的时刻读作跨到次日：22:30 到 06:30 是一夜的一块，不两块。"
                        + "整张表出厂是空的 —— 加一行才开始有哪一段时间属于哪一块。",
            CommentEn = "Time blocks, one per row. A row says which minute of the day the block starts "
                        + "and ends at, what it is called, which sort it is, whether the fade goes up "
                        + "when it begins, which flow that fade walks through, and whether the "
                        + "input reminder stands down inside the block. An end not later than the "
                        + "start means it runs into the next day: 22:30 to 06:30 is one block, a "
                        + "night. The table ships empty — a row is what turns a stretch of the day "
                        + "into a block.",
        },
        new()
        {
            Key = "clock24Hour", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "时钟走 24 小时制。",
            CommentEn = "Show the clock on a 24 hour face.",
        },
        new()
        {
            Key = "blockWhenFullscreen", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "别的程序占着独占全屏（游戏、演示、通话）的时候不出现。",
            CommentEn = "Stay out of the way while another application owns a full screen.",
        },
        new()
        {
            Key = "autoFade", Kind = SettingKind.Bool, Default = true,
            CommentZh = "排布淡入的总开关。",
            CommentEn = "Master switch for the scheduling of fades.",
        },
        // ── 勿扰这一族 ────────────────────────────────────────────────────────
        // 三条声明：组、总开关、时段表和名单。**时段表行里那几个字段不在这里** —— 它们和
        // 时间块的字段、step 的字段是同一种处境（表里的行，键名是裸的），所以住在
        // WindowDeclarations。三份表各自只有一份那个字段的声明。
        SettingDecl.Group("dnd",
            "勿扰：你自己的静默规则。成立的时候，什么提醒都不抬遮罩 —— 到点上来的、连续用"
            + "键盘鼠标太久的那一次、番茄钟那一轮，全都不抬。",
            "Do not disturb: your own quiet rules. While one of them holds, nothing goes up — "
            + "not the block that is beginning, not the reminder for too much input, not a "
            + "finished focus stretch."),
        new()
        {
            // 默认 true 是**写出来的判断**，和作息表那个总开关同一个理由：表里一条时段都没有、
            // 名单一个名字都没有时，开着和关着没有区别，而「默认关着」会让这一族看起来像是坏了、
            // 还要人去找那颗开关。真要退回现状的人把它拧成 false —— 拧成 false 之后时段和名单
            // 都还在表里（接线是调度那一侧的事）。
            Key = DndEnabledKey, Kind = SettingKind.Bool, Default = true,
            CommentZh = "勿扰要不要用。关掉它之后时段和名单都还在，只是什么都不再静默 —— "
                        + "连续用键盘鼠标太久的那次提醒和番茄钟照常抬。一条时段和一个名字都没有时，"
                        + "开着和关着没有区别。",
            CommentEn = "Whether Do not disturb is in use. Off, the times and the list of names are "
                        + "still there and nothing is quiet any more — the reminder for too much "
                        + "uninterrupted input and the Pomodoro carry on as they always have. With "
                        + "no time and no name in either, on and off are the same thing.",
        },
        new()
        {
            Key = DndWindowsKey, Kind = SettingKind.WindowTable, Default = "",
            // 同样一句提醒：这一条的注释是**界面上原样显示的那一行**（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节），所以它说「时段」「星期几」，不说 dnd.windows、键名，
            // 也不说毫秒 —— 这一族一个时长单位都没有。
            CommentZh = "时段，一行一个。一行写清这一段从哪一分钟到哪一分钟、星期几。不晚于开始"
                        + "的时刻读作跨到次日，而且整段挂在开始的那一天：周一 22:30 到 06:30 盖住的是"
                        + "周一夜到周二晨。整张表出厂是空的。",
            CommentEn = "Times, one per row. A row says which minute of the day the stretch starts "
                        + "and ends at, and which weekdays it is on. An end not later than the start "
                        + "means it runs into the next day, and the whole stretch hangs on the day it "
                        + "starts: Monday 22:30 to 06:30 covers Monday night through Tuesday "
                        + "morning. The table ships empty.",
        },
        new()
        {
            Key = DndAppsKey, Kind = SettingKind.NameList, Default = "",
            // 名单里的每一项是**用户自己写的一个词**（一个 exe 名），不是我们的措辞，
            // 所以它不进语言表、不带可选值、界面上原样显示（同设计文档 `docs/design/ui-text.md` 那条界线）。
            CommentZh = "名单，一行一个可执行文件名。整名对上才算，大小写不算 —— 写 chrome.exe "
                        + "认得出 chrome.exe，不认 chrome，也不认完整的路径。名单里没有的名字一律不认。",
            CommentEn = "The list, one executable file name per row. The whole name has to match "
                        + "and case does not count — chrome.exe is recognised as chrome.exe, "
                        + "and not as chrome, and not as a full path. A name that is not on the list "
                        + "is never recognised.",
        },
        new()
        {
            Key = "allMonitors", Kind = SettingKind.Bool, Default = true,
            Tier = "advanced",
            CommentZh = "每块屏都盖，还是只盖主屏。",
            CommentEn = "Fade every monitor, or the primary one only.",
        },
        new()
        {
            // 登录项本身在注册表里；这个键记的是意图，启动时和它对账。
            Key = "autostart", Kind = SettingKind.Bool, Default = false,
            Domain = "system",
            CommentZh = "登录时启动 Keeper。",
            CommentEn = "Start Keeper at sign-in.",
        },
        new()
        {
            Key = "language", Kind = SettingKind.Choice, Default = "zh", Choices = ["zh", "en"],
            Domain = "system",
            CommentZh = "遮罩、托盘菜单和窗口的语言。",
            CommentEn = "Language of the overlay, the tray menu and the windows.",
        },
        new()
        {
            // 2026-09-30 加。设置窗原先只有 Theme_light 一套，写死在场景里 ——
            // 深色桌面上它是一块白板，而这个程序的主题本来就是光和暗。
            // 默认亮色：他明确说过「当然默认是亮色」，所以 `auto` 不在可选值里
            // （跟着系统走是另一条取向，要做的话再加一个可选值，不改默认）。
            Key = "uiTheme", Kind = SettingKind.Choice, Default = "light", Choices = ["light", "dark"],
            Domain = "system",
            CommentZh = "设置窗自己的配色。默认亮色。",
            CommentEn = "The settings window's own colours. Light by default.",
        },
    ];

    /// step 的字段也在这张表里，这样休息清单编辑器里的滑块和这里读的是同一个范围。
    public static readonly SettingDecl[] StepDeclarations =
    [
        SettingDecl.Int("ms", 0, 0, 3_600_000,
            "淡入到位之后这个 step 停多久。最后一个 step 写 0 是「一直到有别的东西结束它」；"
            + "写在中间某个 step 上是「淡入一落定就进下一个」。",
            "How long this step holds once the fade has finished."),
        new()
        {
            Key = "dismissible", Kind = SettingKind.Bool, Default = true,
            CommentZh = "这一步里挥挥手还能把遮罩赶走。",
            CommentEn = "Waving the mouse about can still drive the fade away during this step.",
        },
        new()
        {
            Key = "interactive", Kind = SettingKind.Bool, Default = false,
            CommentZh = "这一步接鼠标，所以清单一列和那颗钮能点；它下面的屏幕照常用。",
            CommentEn = "This step takes the mouse for the checklist and that button; the screen below stays usable.",
        },
        new()
        {
            // `text` 和 `block` 是 2026-10-02 加的两个成员（取向和那条界线的来历：
            // docs/adr/0005-routine-text-and-preset-life-flows.md「遮罩文字正本清源」那一节）。
            // **`block` 是 content 里唯一动态的一个** —— 它不存任何字，值由作息表替它算
            // （Routine.At + TimeBlock.Name），所以它没有对应的段字段。
            //
            // **2026-10-06：`trail` 和 `health` 从这一份 Choices 里拿掉了**（所有者定夺
            // 「把健康度跟进度条移除」）。这一份是**编辑器里那排复选框的唯一来源** ——
            // 留着它们，「勾了它会出来」就是一句假话：勾完之后屏幕上什么也不多。
            //
            // 别人已经写好的休息流程里还有这两个成员名，它们照旧读得进来、不报错：
            // `Fader.Enter` 的 switch 里认得它们，只是不画（见 Fader.cs 那一处）。
            Key = "content", Kind = SettingKind.ChoiceList, Default = "clock",
            Choices = ["clock", "checklist", "text", "block"],
            CommentZh = "这一步在遮罩上摆什么。",
            CommentEn = "What the fade shows during this step.",
        },
        new()
        {
            // `Max` 在这一条上**不是数值范围，是字数上限** —— 界面上那颗输入格的 MaxLength
            // 和遮罩那一侧截断都读它，一个限制只写一次（Kind 是 Text 时 Min/Max 本来没有
            // 别的读者，见 <see cref="SettingDecl.HasRange"/>）。取 120：按清单那一行的字号
            // 在一块 1440 高的屏上，遮罩可用宽度的八成约放得下三十几个汉字一行，
            // 120 字是三四行 —— 够写一段话，又不至于把整块屏糊满。实测结果在工单 04 的 Comments 里。
            Key = "text", Kind = SettingKind.Text, Default = "", Max = StepLayout.TextCap,
            CommentZh = "这一步自带的文字。摆什么里有「文字」时原样画在遮罩上；自由编辑。",
            CommentEn = "This stretch's own words. Drawn on the fade as they are whenever "
                        + "\"What it shows\" has Words ticked; free text.",
        },
        new()
        {
            // **2026-10-06：`content` 和 `text` 两个成员退休**，它们现在只是被读一次就翻成
            // 布局的三个成员名（设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节）。上面那两条声明
            // 留着不撤：老配置里写着它们，要读得进来、还要翻得出布局 —— 撤掉声明就是一份老文件
            // 突然报错，那不是退休一件东西，是砸掉别人的文件。
            Key = "layout", Kind = SettingKind.LayoutList, Default = null!,
            CommentZh = "这一步屏上摆的那一份：哪些控件、各在哪、各占多大、谁盖住谁。"
                        + "存的是屏宽屏高的份额，所以换一块分辨率的屏不用重摆。",
            CommentEn = "What this step puts on the screen: which controls, where, how big, "
                        + "and which is on top of which. Sizes are shares of the screen, so a "
                        + "screen of another size needs no rearranging.",
        },
        new()
        {
            Key = "sound", Kind = SettingKind.Bool, Default = true,
            CommentZh = "这一步放不放 sound 那个文件。",
            CommentEn = "Whether `sound` plays through this step.",
        },
    ];

    /// <summary>布局里一个控件的字段的声明。形状和 <see cref="StepDeclarations"/>、
    /// <see cref="BlockDeclarations"/> 同一种处境：它住在表里三层深
    /// （<c>flows → steps[] → layout[]</c>），键名在文档里是裸的，所以那张索引按
    /// <see cref="StepLayout.KeyPrefix"/> 前缀收它们，而 <see cref="StepLayout"/> 走绑定表时
    /// 把这个前缀去掉。
    ///
    /// <b>六种（加文字和图两类）从这张表现读</b>，是契约写死的一条：加一种要在声明表加一行、
    /// 布局加一个分支、编辑器加一颗钮、遮罩加一段绘制。所以可选值只有一份，在
    /// <see cref="StepLayout.Kinds"/>。
    ///
    /// <b>这一族一个 <c>*Ms</c> 键都没有，也没有一个像素</b>：全是百分数 —— 契约
    /// 「界面上不出现毫秒」在这里靠<b>没有那种后缀</b>兑现，和时间块那一族同一招。
    ///
    /// <b>下面那些默认值是「刚摆上去的那个控件」</b>（摆在正中、占一半、字占框的四分之三），
    /// 管的是「少写一个键时读成什么」；<b>默认布局那一份不在这里</b>，它在
    /// <see cref="StepLayout.Default"/> —— 那是「今天那一屏」，不是「一个空控件」。
    /// 两件事，两处数。</summary>
    public static readonly SettingDecl[] StepLayoutDeclarations =
    [
        new()
        {
            // `kind` 先声明：它是这一行的身份，而「行里哪些字段算数」取决于它。
            Key = "kind", Kind = SettingKind.Choice, Default = StepLayout.KindClock,
            Choices = StepLayout.Kinds,
            CommentZh = "这一步摆的是哪一个。文字和图想摆几张摆几张，其余每一种至多一个。",
            CommentEn = "Which control this is. Words and a picture can be placed as many times "
                        + "as you like; every other one may be placed at most once.",
        },
        SettingDecl.Int("xPercent", 25, (int)StepLayout.MinShare, (int)StepLayout.MaxShare,
            "左缘离屏幕左边多远，占屏宽的几成。",
            "How far in from the left the control sits, as a share of the screen's width.",
            "break"),
        SettingDecl.Int("yPercent", 40, (int)StepLayout.MinShare, (int)StepLayout.MaxShare,
            "上缘离屏幕上边多远，占屏高的几成。",
            "How far in from the top the control sits, as a share of the screen's height.",
            "break"),
        // **下限是 1，不许是 0**：零大的框什么都装不下，而「控件不许是零」是契约写死的。
        SettingDecl.Int("widthPercent", 50, (int)StepLayout.MinSizeShare, (int)StepLayout.MaxShare,
            "这个框有多宽，占屏宽的几成。",
            "How wide this control is, as a share of the screen's width.",
            "break"),
        SettingDecl.Int("heightPercent", 20, (int)StepLayout.MinSizeShare, (int)StepLayout.MaxShare,
            "这个框有多高，占屏高的几成。",
            "How tall this control is, as a share of the screen's height.",
            "break"),
        SettingDecl.Int("fontPercent", 75, 1, (int)StepLayout.MaxShare,
            "字多大，占**这个框**的几成 —— 不是屏高的几成。框太小字会小到读不出来，"
            + "那时候有一根跟着系统缩放走的下限托着它。",
            "How big the text is, as a share of **this control's own box** — not of the screen. "
            + "A box too small for it has a floor under it that follows the system's scaling.",
            "break"),
        new()
        {
            // 上限是 <see cref="StepLayout.TextCap"/> —— 和 `steps[].text` 那一条声明的是同一个数。
            // **为什么那个数住在模型上而不是某一张声明表里**：两张表都要用它，而静态字段按
            // 声明的次序初始化，一张表在初始化时问另一张表读到的是一个还没初始化的字典
            // （症状是一处不相干的空引用，见 `BuildIndex` 上面那段注释）。
            Key = "text", Kind = SettingKind.Text, Default = "", Max = StepLayout.TextCap,
            CommentZh = "这个文字控件写的那一段话。自己写，随便写。",
            CommentEn = "What this text control says. Yours to write.",
        },
        new()
        {
            Key = "file", Kind = SettingKind.Text, Default = "",
            CommentZh = "这个图控件画哪张图。留空就是没有图。",
            CommentEn = "Which picture this control draws. Empty means none.",
        },
        new()
        {
            // 五个摆法只有一份名单（`StepLayout.FitModes`），顶层 `image.position` 走同一份。
            Key = "position", Kind = SettingKind.Choice, Default = StepLayout.DefaultFitMode,
            Choices = StepLayout.FitModes,
            CommentZh = "这张图在这个框里怎么摆。「平铺」是在这个框里平铺。",
            CommentEn = "How this picture sits in this box. Tile repeats it inside the box.",
        },
    ];

    /// <summary>时间块行里那些字段的声明，形状和 <see cref="StepDeclarations"/> 同一种处境：
    /// 它们住在表里两层深，键名在文档里是裸的，所以那张索引按 <see cref="TimeBlock.KeyPrefix"/>
    /// 前缀收它们，而 <see cref="TimeBlock"/> 走绑定表时把这个前缀去掉。
    ///
    /// <b>每一个字段都在这儿声明一次</b>（设计文档 `docs/design/config.md`「每个设置只声明一次」那条）：夹取、界面上的
    /// 范围和「恢复默认」、首启文档里写在它上面的那两行注释和行尾那个范围，四个读者读这一份。
    ///
    /// <b>单位只有两种：一天里的第几分钟，和一个开关。</b> 这一族一个 <c>*Ms</c> 键都没有 ——
    /// 设计文档 `docs/design/config.md`「界面上不出现毫秒」那条在休息流程上靠 <c>*Ms</c> 后缀推单位，在这里靠<b>没有那种后缀</b>：
    /// 起止是现实时间上的两个时刻（「哪一分钟」），行为是开关，没有一处是时长。
    ///
    /// <b>块内行为逐块写，不从种类推</b>：<see cref="TimeBlock.Fade"/> 和
    /// <see cref="TimeBlock.YieldsToInput"/> 的默认值是**声明表给的**（不是「睡眠给什么、
    /// 专注给什么」那种政策），要哪种过法就在某一行里写哪种值 —— 预设生活模式那张工单就是这么写的
    /// （<c>docs/adr/0005-routine-text-and-preset-life-flows.md</c>）。</summary>
    public static readonly SettingDecl[] BlockDeclarations =
    [
        SettingDecl.Int("startMinute", 0, 0, Routine.MinutesPerDay - 1,
            "这一块从一天的哪一分钟开始。0 是零点整，1439 是夜里 23:59。",
            "Which minute of the day this block starts at. 0 is midnight, 1439 is 23:59.",
            RoutineDomain),
        SettingDecl.Int("endMinute", 0, 0, Routine.MinutesPerDay - 1,
            "到一天的哪一分钟为止 —— 那一刻本身归下一块，不算在这一块里。"
            + "不晚于开始的时刻读作跨到次日：22:30 到 06:30 是一夜的一块，不两块；"
            + "写成和开始同一个数就是整整一天。",
            "Which minute it runs to — that minute belongs to whatever comes next. An end not later "
            + "than the start means it runs into the next day: 22:30 to 06:30 is one block, a night. "
            + "The same minute as the start makes it the whole day.",
            RoutineDomain),
        new()
        {
            Key = "name", Kind = SettingKind.Text, Default = "",
            Domain = RoutineDomain,
            CommentZh = "这一块叫什么。托盘那一行和遮罩上原样显示的就是这几个字。",
            CommentEn = "What this block is called. Those few characters are shown as they are, in the "
                        + "tray and on the fade.",
        },
        new()
        {
            // 自由文本而不是 Choice：种类只是标签，专注 / 休息 / 食事 / 运动…… 都是写给人看的
            // （设计文档 `docs/design/daily-routine.md`同一节：「其余种类只是标签，给块名显示和将来的主题化用，不带任何默认行为」）。
            // 挂了可选值就是给夹取一份清单，用户写第四个词时它会被抹成默认 —— 所以不挂。
            Key = "kind", Kind = SettingKind.Text, Default = "",
            Domain = RoutineDomain,
            CommentZh = "这一块是哪一种。写什么都不改行为，它只是给块名显示和将来的主题化用的标签；"
                        + "唯一带意思的是「睡眠」—— 标着它的那一块里，什么提醒都不抬遮罩。",
            CommentEn = "Which sort of block this is. Whatever you write changes nothing: it is a label "
                        + "for the name and for theming later. One value carries meaning — sleep, "
                        + "and nothing at all goes up during such a block.",
        },
        new()
        {
            // 默认 true 是**声明表给的**，不是从种类推的：标着「睡眠」的那一块靠种类语义本来就
            // 静默，而其余任何一种都要自己写清这一块到点抬不抬。
            Key = "fade", Kind = SettingKind.Bool, Default = true,
            Domain = RoutineDomain,
            CommentZh = "到点那一次抬不抬遮罩。抬起来的那一次是光的提示，不记成休息过。",
            CommentEn = "Whether the fade goes up when this block begins. That one is a hint in the "
                        + "light, not a break.",
        },
        new()
        {
            // 留空 = 走「这次休息走哪一条」指着的那一行（设计文档 `docs/design/daily-routine.md`同一节：「没写就走…」）。
            Key = "flow", Kind = SettingKind.Text, Default = "",
            Domain = RoutineDomain,
            CommentZh = "到点那一次走哪一行休息流程。留空就走「这次休息走哪一条」指着的那一行。",
            CommentEn = "Which flow the fade at the start of this block walks through. Leave it "
                        + "empty to follow the flow a break normally follows.",
        },
        new()
        {
            // 默认 true：这一块是有人特意排进去的，连续输入太久的那次提醒在这一块里让位。
            // 要「专注块里不被输入提醒打碎」以外的那种过法，就在某一行里写 false（预设生活模式
            // 那张工单写的就是它）—— **不从种类推**。
            Key = "yieldsToInput", Kind = SettingKind.Bool, Default = true,
            Domain = RoutineDomain,
            CommentZh = "这一块里，连续用键盘鼠标太久的那次提醒让不让位 —— 让位就是它在这一块里"
                        + "不插进来。",
            CommentEn = "Whether the reminder for too much uninterrupted input stands down during this "
                        + "block — standing down means it does not come in here.",
        },
    ];

    /// <summary>勿扰那张时段表里每一行的字段的声明，形状和 <see cref="BlockDeclarations"/>
    /// 同一处境：它们住在表里两层深，键名在文档里是裸的，所以那张索引按
    /// <see cref="DndWindow.KeyPrefix"/> 前缀收它们，而 <see cref="DndWindow"/> 走绑定表时
    /// 把这个前缀去掉。
    ///
    /// <b>单位只有两种：一天里的第几分钟，和一个星期几的名单。</b> 这一族一个 <c>*Ms</c> 键都没有
    /// —— 设计文档 `docs/design/config.md`「界面上不出现毫秒」那条在这一族里靠<b>没有那种后缀</b>兑现（同
    /// <see cref="BlockDeclarations"/> 那段注释里的同一个道理）。
    ///
    /// <b>星期几是一列而不是一个数</b>：22:30 → 07:00 那种「每天夜里」是契约里那个例子的
    /// 本体，要一行写全七天得抄七遍 —— 七行里改一行就漏了。所以它是可选值表里那七个词里
    /// 挑出来的几个（<see cref="Dnd.WeekdayKeys"/>），而<b>求值按开始那一天算</b>
    /// （<see cref="Dnd.WindowCovers"/>）。</summary>
    public static readonly SettingDecl[] WindowDeclarations =
    [
        SettingDecl.Int("startMinute", 0, 0, Routine.MinutesPerDay - 1,
            "这一段从一天的哪一分钟开始。0 是零点整，1439 是夜里 23:59。",
            "Which minute of the day this stretch starts at. 0 is midnight, 1439 is 23:59."),
        SettingDecl.Int("endMinute", 0, 0, Routine.MinutesPerDay - 1,
            "到一天的哪一分钟为止 —— 那一刻本身已经出去了。不晚于开始的时刻读作跨到次日："
            + "22:30 到 06:30 是一段，不两段，而且整段挂在开始的那一天上。",
            "Which minute it runs to — that minute is already outside. An end not later than the "
            + "start means it runs into the next day: 22:30 to 06:30 is one stretch, not two, and "
            + "the whole of it hangs on the day it starts."),
        new()
        {
            // 勾选框那一族：界面上七个勾选框是按这份可选值现造的，声明里加一天，这里多一格
            // （和「摆什么」那一排同一做法，`Options.Days`）。默认只勾周一：一条新加的时段
            // 立刻就在某个确定的晚上生效，而「哪一天」是这个产品里最容易忘的一格。
            Key = "weekdays", Kind = SettingKind.ChoiceList, Default = "mon",
            Choices = Dnd.WeekdayKeys,
            CommentZh = "这一段在星期几。整段挂在**开始的那一天**上 —— 勾了周一、22:30 到 06:30，"
                        + "盖住的是周一夜到周二晨。",
            CommentEn = "Which weekdays this stretch is on. The whole stretch hangs on the day it "
                        + "starts — on Mondays, 22:30 to 06:30 covers Monday night through Tuesday "
                        + "morning.",
        },
    ];

    /// <summary>那张索引。<b>它必须排在几份字段声明之后</b>：<c>static</c> 字段按声明的次序
    /// 初始化，而 <see cref="BuildIndex"/> 要遍历那几份 —— 排在前面的话它遍历的是一个
    /// <b>还没有初始化</b>的字段（空引用，而症状出现在别的类型初始化器里，像一条不相干的崩溃）。</summary>
    private static readonly Dictionary<string, SettingDecl> ByKey = BuildIndex();

    private static Dictionary<string, SettingDecl> BuildIndex()
    {
        var index = new Dictionary<string, SettingDecl>();
        foreach (var decl in Declarations) index[decl.Key] = decl;
        foreach (var decl in StepDeclarations) index[Step.KeyPrefix + decl.Key] = decl;
        foreach (var decl in StepLayoutDeclarations) index[StepLayout.KeyPrefix + decl.Key] = decl;
        foreach (var decl in BlockDeclarations) index[TimeBlock.KeyPrefix + decl.Key] = decl;
        foreach (var decl in WindowDeclarations) index[DndWindow.KeyPrefix + decl.Key] = decl;
        return index;
    }

    /// <summary>声明表里同一个键名被写了两遍的地方。<b>不靠
    /// <see cref="BuildIndex"/> 的字典报</b>：那里是「后写的盖掉先写的」，于是重名的那一条
    /// 安静地换成另一份默认值和另一个范围，而自检还是绿的 —— 「每个设置只声明一次」那条
    /// （设计文档 `docs/design/config.md`）就没有执行者。
    /// 所以 <see cref="SelfCheck"/> 拿它对账，自检里那一组是
    /// <c>settings</c>（键名从声明表问出来，报错时指着键名）。</summary>
    public static List<string> DuplicatedKeys()
    {
        var seen = new List<string>();
        var all = Declarations
            .Select(d => d.Key)
            .Concat(StepDeclarations.Select(d => Step.KeyPrefix + d.Key))
            .Concat(StepLayoutDeclarations.Select(d => StepLayout.KeyPrefix + d.Key))
            .Concat(BlockDeclarations.Select(d => TimeBlock.KeyPrefix + d.Key))
            .Concat(WindowDeclarations.Select(d => DndWindow.KeyPrefix + d.Key))
            .ToList();
        foreach (var key in all.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            seen.Add($"{key.Key}: 声明了 {key.Count()} 次");
        return seen;
    }

    /// 设置窗里的控件按这个取自己的范围和默认值——只认键名，不在场景里再写一个数字。
    /// <summary>界面上一个时长怎么写 —— <b>界面上不出现毫秒</b>那条（设计文档
    /// `docs/design/config.md`「配置这份文档」）
    /// 的<b>唯一一处口径</b>。返回（数字、语言表里那条单位词、除数）。
    ///
    /// 规则定死在设计文档 `docs/design/config.md`「界面上不出现毫秒」那一节里：**≥1 分钟写「X 分钟」，不到 1 分钟写「X 秒」**。所以这个
    /// 除数跟着<b>当前值</b>走而不是跟着默认值走 —— 59 秒那一格右边写「秒」，打到 60
    /// 就换成「1 分钟」。存的值仍然是毫秒，<b>一个字节都没改</b>，改的只是这一格上
    /// 摆出来的那个数和它右边那个词。
    ///
    /// <b>为什么是整数</b>：控件是 <c>SpinBox</c>，它按 <c>Step</c> 决定自己把那个数
    /// 写成什么样（实测 <c>Step = 0.01</c> 时屏幕上出现「3.0」—— 一个「三秒」写成
    /// 「3.0 秒」，正是这个界面被嫌弃的那一种）。步长定 1，屏幕上就只有「3 秒」。
    ///
    /// <b>取整的代价</b>：一个不是整秒/整分的毫秒值会被取整（90000 写 2 分钟）。
    /// 出厂七条时间键和三条出厂休息流程的每一步<b>全是整秒</b>，一条都不走这一支；自检里有
    /// 一条判据专门量这件事（<c>Options.ScreenSelfTest</c> 里「出厂那几处往返无损」）。
    ///
    /// <b>0 是 0</b>：休息流程里「最后一步写 0」读作「一直站到别的事结束它」（契约
    /// 「休息流程是一张表」那张表），它不是「不到一秒」，所以那个「取整到 0 就写 1」的兜底
    /// 把它排除在外。写成 1 秒的话，人只是看了一眼这一格就把「一直站着」改成了「一秒」。</summary>
    public static (long Number, string UnitKey, int Scale) Spell(long ms)
    {
        var scale = ms >= 60_000 ? 60_000 : 1_000;   // 契约那一条：跨过一分钟就换档
        var number = (long)Math.Round(ms / (double)scale, MidpointRounding.AwayFromZero);
        if (scale == 1_000 && number < 1 && ms > 0) number = 1;   // 不足一秒仍写秒，且不写 0
        return (number, scale == 60_000 ? "unit.minute" : "unit.second", scale);
    }

    /// <summary><see cref="Spell"/> 那个数字在控件里写成什么样：整数、用点号
    /// （小数点不跟语言走 —— 自检里 <c>StaleCells</c> 拿来对账的也是它）。</summary>
    public static string SpellText(long ms)
    {
        var (number, _, _) = Spell(ms);
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>界面上打进去的「人话单位的那一种」换算回毫秒 —— <see cref="Spell"/> 的逆。
    /// 换档是它自己说了算：打进去 60 就落回 60000（<paramref name="scale"/> 那一步决定
    /// 60 是 60 秒还是 1 分钟），所以调用方要先按<b>此刻那一档</b>把除数传对。</summary>
    public static int Unspelled(long number, int scale) => (int)(number * scale);

    /// <summary><see cref="Spell"/> 走一个来回之后是不是原来那个毫秒值 —— 自检里量
    /// 「出厂那几处不能被取整吃掉」用的就是它（<c>Options.ScreenSelfTest</c>）。</summary>
    public static bool RoundTrips(long ms) => Unspelled(Spell(ms).Number, Spell(ms).Scale) == ms;

    public static SettingDecl Decl(string key) =>
        ByKey.TryGetValue(key, out var found)
            ? found
            : throw new KeyError(key);

    /// 场景里那一行写的键名认不认得。绑定和 `--check` 那次自检都问这一句，不靠 catch 异常。
    public static bool Declared(string key) => ByKey.ContainsKey(key);

    /// 每个值都回到声明允许的范围里。读文件之后调用，所以一份改坏了的文档不会把程序
    /// 带进它自己处理不了的状态；设计文档 `docs/design/config.md` 的「夹取」一条就是这一步。
    /// 走的是绑定表，所以「哪些键有夹取」这件事不会和「哪些键存在」各说一套。
    public void ClampToDeclarations()
    {
        _map.SanitizeAll();

        // step 藏在表里两层深，没有一个键名能直接指到它，所以逐个走。
        foreach (var step in Flows.Values.SelectMany(flow => flow.Steps))
        {
            step.Ms = Decl("steps[].ms").Clamp(step.Ms);
            // 布局夹的是份额和「每种至多一个」，范围只有 <see cref="StepLayoutDeclarations"/>
            // 那一份里有。同理：控件的行藏在第三层，没有一个键名能直接指到它。
            step.Layout = StepLayout.Sanitize(step.Layout);
        }

        // 时间块行里的每一个字段同样没有键名能直接指到它，走的是**它自己那一份声明**的
        // 夹取（<see cref="BlockDeclarations"/>）。范围只写在声明表里一份：这一族没有
        // 第二个 1439。
        foreach (var block in Blocks)
        {
            block.StartMinute = Decl(TimeBlock.KeyPrefix + "startMinute").Clamp(block.StartMinute);
            block.EndMinute = Decl(TimeBlock.KeyPrefix + "endMinute").Clamp(block.EndMinute);
        }

        // 勿扰那一张时段表逐行走同一段道理（<see cref="WindowDeclarations"/>）—— 这一族同样没有
        // 第二个 1439。名单那一列的规矩不是夹取而是**去掉空的那几行**：一列名字里混一条空的，
        // 界面上就是一行看不见又删不掉的东西，而它不匹配任何东西。
        foreach (var window in DndWindows)
        {
            window.StartMinute = Decl(DndWindow.KeyPrefix + "startMinute").Clamp(window.StartMinute);
            window.EndMinute = Decl(DndWindow.KeyPrefix + "endMinute").Clamp(window.EndMinute);
        }
        DndApps = DndApps.Where(name => name.Trim().Length > 0)
            .Select(name => name.Trim()).ToList();
    }

    /// flows 那张表在文档里的样子。行的顺序就是字典插进来的顺序——用户自己加的休息流程
    /// 因此排在自带的两条后面。
    private JsonObject ModesToJson()
    {
        var table = new JsonObject();
        foreach (var (name, flow) in Flows) table[name] = flow.ToJson();
        return table;
    }

    /// 文档里有 flows 就整张换上：没写的 step 字段回到 StepDeclarations 的默认值，
    /// 文档里一个 step 都没有的那些 flow，由 <see cref="Steps"/> 读作 fadetop。
    private List<string> ApplyFlows(JsonObject document)
    {
        var problems = new List<string>();
        if (!document.TryGetPropertyValue(ModesKey, out var node) || node == null) return problems;
        if (node is not JsonObject table)
        {
            problems.Add($"{ModesKey}: 期望一张表，文档里写的是 {node.ToJsonString()}");
            return problems;
        }

        var flows = new Dictionary<string, Flow>();
        foreach (var (name, row) in table)
        {
            if (row is not JsonObject fields)
            {
                problems.Add($"{ModesKey}.{name}: 期望一个对象");
                continue;
            }
            var flow = new Flow();
            problems.AddRange(flow.ReadFrom(name, fields,
                // 顶层那三格图先读完了（`Apply` 里 `_map.ReadFrom` 在前），所以这里问得到。
                // **没开「使用图片」就是没有图** —— 和今天那扇窗一样，图只跟着总开关亮。
                Image.Enabled ? Image.File : "", Image.Position));
            flows[name] = flow;
        }
        Flows = flows;
        return problems;
    }

    /// 时间块那张表在文档里的样子：一个列表，一行一个时间块，行的顺序就是
    /// <see cref="Blocks"/> 的顺序。**出厂是空的**，所以这张表和 flows 那张表不一样：
    /// 出厂的那三条休息流程写出来是为了能被改，而时间块的内容是预设生活模式那一张工单的活。
    private JsonArray BlocksToJson()
    {
        var list = new JsonArray();
        foreach (var block in Blocks) list.Add(block.ToJson());
        return list;
    }

    /// <summary>文档里有那张表就整张换上。没写的字段回到
    /// <see cref="BlockDeclarations"/> 的默认值，所以一行可以只写起止时刻那几个 ——
    /// 写全是推荐的样子（首启文档那一行就是全的），少写的不是错。
    ///
    /// <b>宽容读不在这里，在 <c>JsoncDoc</c></b>：注释、块注释和尾逗号是那份组件读掉的
    /// （设计文档 `docs/design/config.md`「读的时候宽容」那条），所以「用户在时间块上面加一行注释」不花钱。
    ///
    /// <b>值超范围要夹**不在这里**：夹取走 <see cref="ClampToDeclarations"/>，和别的键同一条路
    /// —— 类型不对（<c>"22:30"</c> 这种写成了时刻的）才是「这一份读不通」，报的是<b>键名</b>。</summary>
    private List<string> ApplyBlocks(JsonObject document)
    {
        var problems = new List<string>();
        var node = SettingMap.Find(document, BlocksKey);
        if (node == null) return problems;
        if (node is not JsonArray list)
        {
            problems.Add($"{BlocksKey}: 期望一个列表（一个元素是一个时间块），文档里写的是 {node.ToJsonString()}");
            return problems;
        }

        var blocks = new List<TimeBlock>();
        int at = 0;
        foreach (var item in list)
        {
            at++;
            if (item is not JsonObject row)
            {
                problems.Add($"{BlocksKey}: 第 {at} 个元素要是一个时间块（一个对象）");
                continue;
            }
            var block = new TimeBlock();
            // 读不通的字段名要指着**那一行**：同一段里两行都叫 startMinute，只说键名
            // 分不出是哪一行（CODING_STANDARDS.md 第 6 条：报错时指着键名，行号是它的一部分）。
            problems.AddRange(block.ReadFrom(row).Select(p => $"{BlocksKey}[{at}]: {p}"));
            blocks.Add(block);
        }
        Blocks = blocks;
        return problems;
    }

    /// 勿扰那张时段表在文档里的样子：<b>和上面那张时间块表一模一样的形状</b>，只是行是
    /// <see cref="DndWindow"/> 而不是 <see cref="TimeBlock"/>。出厂是空的。
    private JsonArray WindowsToJson()
    {
        var list = new JsonArray();
        foreach (var window in DndWindows) list.Add(window.ToJson());
        return list;
    }

    /// 读那张时段表。三件事和 <see cref="ApplyBlocks"/> 同一处置：没写的字段回到
    /// <see cref="WindowDeclarations"/> 的默认值、宽容读在 <c>JsoncDoc</c> 那一侧、值超范围走
    /// <see cref="ClampToDeclarations"/>。读不通的报**键名 + 那是第几行**。
    private List<string> ApplyWindows(JsonObject document)
    {
        var problems = new List<string>();
        var node = SettingMap.Find(document, DndWindowsKey);
        if (node == null) return problems;
        if (node is not JsonArray list)
        {
            problems.Add($"{DndWindowsKey}: 期望一个列表（一个元素是一条时段），文档里写的是 {node.ToJsonString()}");
            return problems;
        }

        var windows = new List<DndWindow>();
        int at = 0;
        foreach (var item in list)
        {
            at++;
            if (item is not JsonObject row)
            {
                problems.Add($"{DndWindowsKey}: 第 {at} 个元素要是一条时段（一个对象）");
                continue;
            }
            var window = new DndWindow();
            problems.AddRange(window.ReadFrom(row).Select(p => $"{DndWindowsKey}[{at}]: {p}"));
            windows.Add(window);
        }
        DndWindows = windows;
        return problems;
    }

    /// 那张 exe 名单在文档里的样子：一列词，行的顺序就是 <see cref="DndApps"/> 的顺序。
    /// 空表写出来是 <c>[]</c> —— 界面上「还没有名字」那一档和这一份文档是同一件事的两面。
    private JsonArray AppsToJson()
    {
        var list = new JsonArray();
        foreach (var name in DndApps) list.Add(name);
        return list;
    }

    /// 读那张名单。<b>一个不是字符串的项就是这一份读不通</b>（报的是键名 + 第几项）——
    /// 名单是 exe 名，用户自己敲错一个字是「认不出」，而写成数字是「这一份读不通」，两件事。
    private List<string> ApplyApps(JsonObject document)
    {
        var problems = new List<string>();
        var node = SettingMap.Find(document, DndAppsKey);
        if (node == null) return problems;
        if (node is not JsonArray list)
        {
            problems.Add($"{DndAppsKey}: 期望一个列表（一个元素是一个可执行文件名），文档里写的是 {node.ToJsonString()}");
            return problems;
        }

        var names = new List<string>();
        int at = 0;
        foreach (var item in list)
        {
            at++;
            if (item is not JsonValue value || !value.TryGetValue(out string? name))
            {
                problems.Add($"{DndAppsKey}: 第 {at} 个元素要是一个可执行文件名（一个字符串）");
                continue;
            }
            names.Add(name);
        }
        DndApps = names;
        return problems;
    }

    // 工单 03：这一组自己的绿话，跟着判据住，不再由 Probe.cs 抄一份。
    //
    // **那三个数必须现问**（`Declarations.Length`、绑在属性上的键数、step 字段数）。
    // 手抄进来编译器不报，而抄的时候它对、改了之后它还绿 —— 那是同一个病的第二种形状。
    // 这一组原本已经在现问，这一轮让它成为唯一的一档。

    internal static readonly CriterionOf Table = new(
        () => $"声明表自洽：{Declarations.Length} 个键，" +
              $"{new Settings().Map.Keys.Count()} 个绑在属性上，" +
              $"{StepDeclarations.Length} 个 step 字段，" +
              $"休息延迟出厂 {Decl("restDelay").AsIntList.Length} 格、每项不短于 {Decl("restDelay").Min} 分钟",
        "声明表", SelfCheck);

    /// 一张表能不能自己讲得通，全在这里查。这条不靠运行到某个界面才发现，所以它排在
    /// 构建命令里：<c>--headless --quit-after N</c> 跑一遍，任何一条不合格就非零退出。
    /// 设计文档 `docs/design/ui-text.md` 给语言表定的就是这一手：「编译期那道硬保证换成构建命令里跑的一条
    /// 无头自检」—— 这一发对声明表走的是同一条路。
    public static List<string> SelfCheck()
    {
        var problems = new List<string>();

        foreach (var decl in Declarations)
        {
            if (decl.IsContainer) continue;
            var value = decl.Default;
            if (decl.Kind == SettingKind.Int && value is not int)
                problems.Add($"{decl.Key}: 默认值不是整数");
            if (decl.Kind == SettingKind.Int && decl.Min > decl.Max)
                problems.Add($"{decl.Key}: 范围反了 {decl.Min}–{decl.Max}");
            if (decl.Kind == SettingKind.Int && decl.Default is int n && (n < decl.Min || n > decl.Max))
                problems.Add($"{decl.Key}: 默认值 {n} 不在 {decl.Min}–{decl.Max} 里");
            if (decl.Kind == SettingKind.HexColor && !decl.IsColor(decl.AsString))
                problems.Add($"{decl.Key}: 默认值 {decl.AsString} 不是 #RRGGBB");
            if (decl.Kind is SettingKind.Choice && Array.IndexOf(decl.Choices, decl.AsString) < 0)
                problems.Add($"{decl.Key}: 默认值 {decl.AsString} 不在可选值里");
            if (decl.Kind is SettingKind.ChoiceList)
                foreach (var item in decl.AsStringList)
                    if (Array.IndexOf(decl.Choices, item) < 0)
                        problems.Add($"{decl.Key}: 默认值里的 {item} 不是可选值");
            if (decl.Kind == SettingKind.Text && decl.Choices.Length > 0)
                problems.Add($"{decl.Key}: 自由文本键挂了可选值，夹取会把认不出的值抹成默认");
            // 一串用户自己写的分钟数同样不该挂可选值：挂上等于「用户写一个不在名单上的数
            // 就被抹掉」，而这一列压根没有名单（和 `dnd.apps` 那一条同一个道理）。
            if (decl.Kind == SettingKind.DelayList && decl.Choices.Length > 0)
                problems.Add($"{decl.Key}: 一串用户自己写的分钟数不该挂可选值，夹取会把认不出的值抹掉");
            if (decl.Kind == SettingKind.Group
                && !Declarations.Any(other => other.Key.StartsWith(decl.Key + ".", StringComparison.Ordinal)))
                problems.Add($"{decl.Key}: 声明了组，名下却没有叶子");
        }

        foreach (var decl in StepDeclarations)
        {
            if (decl.Kind == SettingKind.Int && decl.Min > decl.Max)
                problems.Add($"steps[].{decl.Key}: 范围反了");
            if (decl.Key.Contains('.'))
                problems.Add($"steps[].{decl.Key}: step 的字段不能再嵌套");
        }

        // 布局里那些字段走同一套规矩，外加三条这一族特有的：
        // **一个毫秒键都不许有**（这一族的单位是百分数，界面上不许出现毫秒）、
        // **控件的字段不许再嵌一层**（再嵌就是第四层，而这张表已经在第三层了），
        // 以及**声明了却没有一行接着**（那一条的读法照时间块那一族，见下面那段）。
        var boundLayoutFields = new StepLayout().Map.Keys.ToHashSet();
        foreach (var decl in StepLayoutDeclarations)
        {
            if (decl.Key.Contains('.'))
                problems.Add($"{StepLayout.KeyPrefix}{decl.Key}: 控件的字段不能再嵌套");
            if (decl.Kind == SettingKind.Int)
            {
                if (decl.Min > decl.Max)
                    problems.Add($"{StepLayout.KeyPrefix}{decl.Key}: 范围反了");
                if (decl.IsTime)
                    problems.Add($"{StepLayout.KeyPrefix}{decl.Key}: 这一族存的是份额，不是时长");
            }
            if (!boundLayoutFields.Contains(decl.Key))
                problems.Add($"{StepLayout.KeyPrefix}{decl.Key}: 声明了但没有一个控件接着它");
        }
        // 那七种控件只有一份名单，声明表、夹取和「独一份那五种」读的都是它 ——
        // 名单和声明表对不上，就是「勾了它会出来」那句变成假话。
        if (Decl(StepLayout.KeyPrefix + "kind").Choices.Length != StepLayout.Kinds.Length
            || StepLayout.Kinds.Any(kind => Array.IndexOf(Decl(StepLayout.KeyPrefix + "kind").Choices, kind) < 0))
            problems.Add($"{StepLayout.KeyPrefix}kind: 可选值和 StepLayout.Kinds 对不上，"
                         + "六种控件那份名单改了、声明表没跟着");
        // 默认布局里那五种独一份的控件都只摆了一次 —— 摆两次的话第一次运行屏幕上就多一样东西。
        foreach (var kind in StepLayout.SingletonKinds)
            if (StepLayout.Default().Count(control => control.Kind == kind) > 1)
                problems.Add($"默认布局里 {kind} 摆了一个以上 —— 这一种至多一个");

        // 时间块那些字段走同一套规矩，加上两条这一族特有的：
        // 起止那两个键的范围必须和 <see cref="Routine.MinutesPerDay"/> 对得上（一天就是那么长，
        // 而这个数只在纯算术那一处写一次），以及**一个毫秒键都不许有** —— 这一族的单位是
        // 时刻和分钟，界面上不许出现毫秒，而「按 <c>*Ms</c> 后缀推显示单位」那条通则在
        // 这一族里靠「没有那种后缀」兑现。
        foreach (var decl in BlockDeclarations)
        {
            if (decl.Key.Contains('.'))
                problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 时间块的字段不能再嵌套");
            if (decl.Kind == SettingKind.Int)
            {
                if (decl.Min > decl.Max) problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 范围反了");
                if (decl is { IsTime: true })
                    problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 时间块里这一格是现实时间上的时刻，不是时长");
                if (decl.Key is "startMinute" or "endMinute"
                    && (decl.Min != 0 || decl.Max != Routine.MinutesPerDay - 1))
                    problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 范围是 0–{Routine.MinutesPerDay - 1}"
                                 + $"（一天里的第几分钟），声明写的是 {decl.RangeText}");
            }
            if (decl.Domain != RoutineDomain)
                problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 归的是「{decl.Domain}」这一域，"
                             + $"时间块那些字段归「{RoutineDomain}」");
        }
        // 声明了却没有一行接着的字段 = 写进文档却没人读的那一种设置。
        var boundFields = new TimeBlock().Map.Keys.ToHashSet();
        foreach (var decl in BlockDeclarations)
            if (!boundFields.Contains(decl.Key))
                problems.Add($"{TimeBlock.KeyPrefix}{decl.Key}: 声明了但没有一行接着它");

        // 勿扰那张时段表：同一套规矩，外加两条这一族特有的。
        // 判据里的域**从它自己那张表的声明问出来**（「时段表行里那些字段归哪一格」跟着表走），
        // 不写死「休息提醒」那一个字 —— 写死就是第二份真相，换了域它会安静地放过。
        var windowTable = Decl(DndWindowsKey);
        var boundWindowFields = new DndWindow().Map.Keys.ToHashSet();
        foreach (var decl in WindowDeclarations)
        {
            if (decl.Key.Contains('.'))
                problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 时段的字段不能再嵌套");
            if (decl.Kind == SettingKind.Int)
            {
                if (decl.Min > decl.Max) problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 范围反了");
                if (decl.IsTime)
                    problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 时段里这一格是现实时间上的时刻，不是时长");
                if (decl.Key is "startMinute" or "endMinute"
                    && (decl.Min != 0 || decl.Max != Routine.MinutesPerDay - 1))
                    problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 范围是 0–{Routine.MinutesPerDay - 1}" +
                                 $"（一天里的第几分钟），声明写的是 {decl.RangeText}");
            }
            if (decl.Domain != windowTable.Domain)
                problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 归的是「{decl.Domain}」这一域，" +
                             $"而 {DndWindowsKey} 那张表归「{windowTable.Domain}」—— 一张表的行和它自己那张表要在一格");
            if (!boundWindowFields.Contains(decl.Key))
                problems.Add($"{DndWindow.KeyPrefix}{decl.Key}: 声明了但没有一行接着它");
        }
        // 星期几那一列的可选值就是求值那一份问的七个词：一个都不认，那七格勾选框摆的是什么就
        // 只有界面上看得见，而求值一个都不认（「勾了没用」没有一句话会说）。
        if (Decl(DndWindow.KeyPrefix + "weekdays").Choices.Length != Dnd.WeekdayKeys.Length ||
            Decl(DndWindow.KeyPrefix + "weekdays").Choices.Any(key => Array.IndexOf(Dnd.WeekdayKeys, key) < 0))
            problems.Add($"{DndWindow.KeyPrefix}weekdays: 那七个可选值和求值那一份（Dnd.WeekdayKeys）对不上");
        // 名单那一列**不许挂可选值**：它的每一项是用户自己写的一个词，挂了可选值就是夹取
        // 会把认不出的名字抹成默认（那是 <see cref="SettingKind.Text"/> 那条判据的同一个道理）。
        if (Decl(DndAppsKey).Choices.Length > 0)
            problems.Add($"{DndAppsKey}: 一列用户自己写的词不该挂可选值，夹取会把认不出的名字抹掉");
        // 总开关是这一族里唯一一条挂在属性上的键：它漏绑了，写出去的文档没人读回来。
        if (!new Settings().Map.Keys.Contains(DndEnabledKey))
            problems.Add($"{DndEnabledKey}: 声明了但构造函数没有绑定它");

        problems.AddRange(DuplicatedKeys());

        // 每个能持有值的键都该有人接着；漏了的键就是写进文档却没人读的设置。绑了一个没声明
        // 的键在 Bind 里就已经抛 KeyError 了，用不着在这里再查。
        var fresh = new Settings();
        var boundOrder = fresh.Map.Keys.ToList();
        var wired = boundOrder.ToHashSet();
        foreach (var decl in Declarations)
            if (!decl.IsContainer && !wired.Contains(decl.Key))
                problems.Add($"{decl.Key}: 声明了但构造函数没有绑定它");

        // 文档里键的顺序就是声明的顺序。顺序错了不会报错，只会每次保存都把用户的文件重排
        // 一遍——那条规则写在设计文档 `docs/design/config.md`「读的时候宽容，写的时候是编辑」那段里。
        var declared = Declarations.Where(d => !d.IsContainer).Select(d => d.Key).ToList();
        if (!declared.SequenceEqual(boundOrder))
            problems.Add("绑定顺序和声明顺序不一致：" +
                         string.Join(" ", boundOrder.Except(declared)) + " vs " +
                         string.Join(" ", declared.Except(boundOrder)));

        if (fresh.Steps().Count == 0) problems.Add("内置 flow 的 step 列表是空的");
        fresh.Flow = "没有这条";
        if (fresh.Steps().Count != 1) problems.Add("认不出的 flow 没有回落到 fadetop");

        // 默认值自己得先过自己那一条规则，不然一份没人改过的文档存一次就被程序改坏了。
        foreach (var decl in Declarations)
        {
            if (decl.IsContainer) continue;
            if (decl.Kind == SettingKind.Int && decl.Clamp(decl.AsInt) != decl.AsInt)
                problems.Add($"{decl.Key}: 默认值过不了自己的夹取");
            if (decl.Kind is SettingKind.Choice or SettingKind.Text
                && decl.OrKnown(decl.AsString) != decl.AsString)
                problems.Add($"{decl.Key}: 默认值过不了自己的取值校验");
            // 一串分钟数过自己的夹取：**每一项**都得在声明给的范围内（地板 1 分钟），
            // 而且一项都剩不下时要回得出默认那一串 —— 否则出厂那一份自己就存不回去。
            if (decl.Kind == SettingKind.DelayList)
            {
                if (decl.Min > decl.Max)
                    problems.Add($"{decl.Key}: 范围反了 {decl.Min}–{decl.Max}");
                var clamped = (List<int>)decl.Sanitize(decl.AsIntList.ToList());
                if (!clamped.SequenceEqual(decl.AsIntList))
                    problems.Add($"{decl.Key}: 默认值过不了自己的夹取（{string.Join(", ", decl.AsIntList)}"
                                 + $" → {string.Join(", ", clamped)}）");
            }
        }

        // 属性和声明的种类对不对得上，靠这一条：一份文档写出去再读回来，必须还是那份文档。
        // 类型配错了在这里就露头，而不是等某次保存把一个键悄悄抹成默认值。
        var original = new Settings();
        var back = new Settings();
        var unread = back.Apply(original.ToDocument());
        if (unread.Count > 0)
            problems.Add("默认值写出去的文档读不回来：" + string.Join("；", unread));
        else if (Diff(original, back).Length > 0)
            problems.Add("文档走一个来回不是同一份值：" + Diff(original, back));

        // 休息延迟那一串：**每一项**的地板来自声明表（0 分钟和负数会把调度算坏），而文档里
        // 写错形状（不是整数）是一条读不通 —— 程序照默认值跑，但说出来。
        // **地板和出厂那几格都从声明问**，不写死 1 也不写死 1,3（那一份会过期，而它过期得没有声音）。
        var delays = Decl("restDelay");
        var hand = new JsonObject();
        SettingMap.Put(hand, delays.Key,
            new JsonArray(JsonValue.Create(0), JsonValue.Create(-5), JsonValue.Create(delays.AsIntList[^1])));
        var clampedDown = new Settings();
        var handProblems = clampedDown.Apply(hand);
        clampedDown.ClampToDeclarations();
        if (handProblems.Count > 0)
            problems.Add($"一串整数读不回来：{string.Join("；", handProblems)}");
        var wantDown = new List<int> { delays.Min, delays.Min, delays.AsIntList[^1] };
        if (!clampedDown.RestDelays.SequenceEqual(wantDown))
            problems.Add($"{delays.Key}: 文档里写的那两项没夹回声明给的 {delays.Min} —— " +
                         $"读到 {string.Join(", ", clampedDown.RestDelays)}，该是 {string.Join(", ", wantDown)}");

        var notInts = new JsonObject();
        SettingMap.Put(notInts, delays.Key, new JsonArray(JsonValue.Create("很多")));
        var mistyped = new Settings();
        if (mistyped.Apply(notInts).Count == 0)
            problems.Add($"{delays.Key}: 字符串写在一个整数列表上却没报读不通");
        // 读不通时程序照出厂那一串跑（磁盘上的文件一个字都不动）——「解析失败是一个状态，不是崩溃」。
        if (!mistyped.RestDelays.SequenceEqual(new Settings().RestDelays))
            problems.Add($"{delays.Key}: 读不通的时候没照出厂那一串跑");

        // **界面上那一格**：一格逗号分隔的字，认不出的那一项丢掉、一项都认不出就回出厂那一串。
        // 这两条正是「读的时候宽容」在那一格上的落点（`ReadDelays` 是它唯一的出处）。
        var read = delays.ReadDelays(" 0, 3 , abc ,");
        var wantRead = new List<int> { delays.Min, 3 };
        if (!read.SequenceEqual(wantRead))
            problems.Add($"{delays.Key}: 那一格里认不出的项没被丢掉/夹住 —— " +
                         $"读到 {string.Join(", ", read)}，该是 {string.Join(", ", wantRead)}");
        if (!delays.ReadDelays("abc").SequenceEqual(new Settings().RestDelays))
            problems.Add($"{delays.Key}: 那一格一项都认不出时没回出厂那一串");
        // 灌值那一头和读回那一头是**互逆**的：界面上那格显示的字，读回来还是同一串。
        // 这才是「那一格只有一个出处」量得到的东西（抄一份写法就是两份会分家的东西）。
        var shown = delays.SpellDelays(delays.AsIntList.ToList());
        if (!delays.ReadDelays(shown).SequenceEqual(delays.AsIntList))
            problems.Add($"{delays.Key}: 界面上那一格写着 '{shown}'，读回来却是 " +
                         $"{string.Join(", ", delays.ReadDelays(shown))} —— 灌值和读回不是互逆的");

        return problems;
    }

    /// <summary>两份设置的值视图差在哪，读作「from 的值 → to 的值」。给报告用：设置窗口
    /// 和自检都要说清哪里不一样，而不是只说「不一样」。</summary>
    public static string Diff(Settings from, Settings to)
    {
        var a = from.ToDocument();
        var found = new List<string>();
        foreach (var (key, value) in to.ToDocument())
        {
            if (!a.TryGetPropertyValue(key, out var other))
            {
                found.Add($"{key} 多了");
                continue;
            }
            if (!JsonNode.DeepEquals(other, value))
                found.Add($"{key}: {other?.ToJsonString()} → {value?.ToJsonString()}");
        }
        return string.Join("；", found);
    }

    /// 一条 flow 认不出是 Keeper 认识的休息流程，或者一条 step 都没有，读作 fadetop
    /// 那条，而不是「没有淡入」——见设计文档 `docs/design/routine.md`。
    ///
    /// <b>名字到休息流程这条路只有这一条</b>：<see cref="Steps"/> 把「这次休息走哪一条」指着的那
    /// 个名字递进来，作息表到点那一次把块里写的那个名字递进来（<see cref="Routine"/> 那一族），
    /// 两边走的是同一个方法、同一个回落。<b>不因为多了一个调用方就多一条读法</b>。
    public List<Step> Steps() => StepsOf(Flow);

    /// <summary>按<b>名字</b>取那一行休息流程。认不出的名字、空掉的那一行，读作
    /// <see cref="FlowFadeTop"/>（同上那条规矩）—— 这一族是唯一一处做「名字 → 休息流程」这件事的地方。
    /// </summary>
    public List<Step> StepsOf(string flow)
    {
        if (Flows.TryGetValue(flow, out var row) && row.Steps.Count > 0) return row.Steps;
        return BuiltInFlows()[FlowFadeTop].Steps;
    }

    /// <summary>番茄钟那一轮休息走的休息流程 —— <b>固定是 <c>pomodoro</c> 那一行</b>，
    /// 不跟着 <see cref="Flow"/> 走。
    ///
    /// 这么定是因为番茄钟有自己的休息形状：它和「连续输入满多久」那一条触发是两条路，
    /// 用户给后者配了什么休息，跟前者无关（设计文档 `docs/design/pomodoro.md`
    /// 那一族）。反过来它仍然是一行
    /// 普通的 flows 行 —— 步多长、显示什么、擦不擦得散，都在那里改，和别的休息流程同权。
    ///
    /// 用户把这一行删了或者清空了，就退回 <see cref="Steps"/>：那是「没写就回落到
    /// fadetop」那一条规矩的同一个落点，而不是「番茄钟不休息了」。</summary>
    public List<Step> PomodoroSteps()
    {
        if (Flows.TryGetValue(FlowPomodoro, out var flow) && flow.Steps.Count > 0) return flow.Steps;
        return Steps();
    }

    /// 专注段多长，毫秒。番茄钟唯一的时长旋钮。
    public long PomodoroWorkMs() => (long)Pomodoro.WorkMin * 60_000;

    /// 自带的那三条休息流程。第一条就是淡入本身，第二条是一步走完的休息，第三条是番茄钟的短休。
    ///
    /// <b>这几步身上只有老写法（<c>content</c> + <c>text</c>），所以它们的布局是翻出来的</b>
    /// （<see cref="Step.AdoptLegacyLayout"/>）：出厂那一屏于是和今天一模一样，而
    /// 「老配置照今天的排法自动生成一份布局」那条在出厂这几步身上走的是同一条路。
    /// 写成手摆的一份也行，只是那样就有两份地方写着「今天那一屏长什么样」，
    /// 而默认布局那一份已经管着它了（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）。
    public static Dictionary<string, Flow> BuiltInFlows()
    {
        var fadeOnly = new Step();
        fadeOnly.AdoptLegacyLayout();   // ms 是 0 —— 没有倒计时；内容只有时钟

        var todo = new Step
        {
            // 一分钟。
            Ms = 60_000,
            // 休息不是忍一块颜色：只来一下输入赶不走它（键盘和鼠标一样）。
            Dismissible = false,
            // 收鼠标 —— 但收的时刻由 <c>Fader</c> 那边定：遮罩完全浮现之后才收
            // （见 Fader.Enter 那段注释），淡入途中不抢鼠标。
            Interactive = true,
            // 中间：时钟、倒计时、这一步自带的文字。侧面：清单和那颗钮。
            // 休息足迹和健康度 2026-10-06 从遮罩上撤了，出厂这一步也就不写它们 ——
            // 那两个名字照旧认得，翻出来时**不变成控件**（StepLayout.Translate 那一段）。
            Content = ["clock", "text", "checklist"],
            // 出厂给一句语录，格式是中文常写的那种：一句正文 + 出处。
            // 它同时也是「文字」这一格长什么样的样本 —— 那一格默认是空的，
            // 空着时装上就看不见这个位置摆过东西。翻出来的那个文字控件装的就是这几句。
            Text = "诗云：'行百里者半于九十。'此言末路之难也。"
                   + "\n——《战国策·秦策五》《谓秦王》",
            // 设计文档 `docs/design/fader-window.md`「「落定」是哪一个时刻」那一节那一句
            // （落定之后才开始放声音），这一步本来就明着要声。
            // <b>它和默认值同向</b>
            // （steps[].sound 的默认就是 true，淡入那一步因此也放），写在这里是把
            // 「这一步要声」说成话，不是补一个本来会漏的开关。
            Sound = true,
        };
        // 有 ms 所以有倒计时、interactive 所以有那颗钮：这两样今天也照样出现，
        // 它们只是不在 content 那个名单里（倒计时跟 ms 走、那颗钮跟 interactive 走）。
        todo.AdoptLegacyLayout();

        var shortBreak = new Step
        {
            // 五分钟。**不是 25** —— 25 分钟是专注段，住在 pomodoro.workMin 那个键上；
            // 这里这一行是休息，遮罩底下那五分钟。
            Ms = 5 * 60_000,
            // 短休也是休息：只来一下输入赶不走它，理由和 todoList 那步一样 ——
            // 番茄钟的意义就是这一步真的发生了。
            Dismissible = false,
            // 短休里没有什么要点：不收鼠标，底下那块屏幕照常用。所以那颗钮不摆。
            Interactive = false,
            Content = ["clock", "trail", "health"],
            Sound = true,
        };
        shortBreak.AdoptLegacyLayout();

        return new Dictionary<string, Flow>
        {
            [FlowFadeTop] = new Flow { Steps = [fadeOnly] },
            [FlowTodoList] = new Flow { Steps = [todo] },
            [FlowPomodoro] = new Flow
            {
                Steps =
                [
                    // 淡入这一步，和另外两条一样：ms 是 0，意思是「淡一落定就进下一步」。
                    fadeOnly.Clone(),
                    shortBreak,
                ],
            },
        };
    }
}

/// <summary>某个设置声明了，代码里却找不到接它的属性。</summary>
public sealed class KeyError(string key) : Exception($"没有名为 {key} 的设置声明");

/// <summary>文档里的一个值不是这个键该有的样子。读的时候每一处都把它变成一条报告，
/// 而不是让程序带着半个值跑下去。</summary>
public sealed class ReadProblem(string message) : Exception(message);

/// 下面三个组类里没有默认值，一个字都没有：默认值由 Settings 构造函数里的绑定从声明表
/// 取走，写在这里就是第二份，而且是不会有人发现的那一份。
public sealed class ImageOptions
{
    public bool Enabled { get; set; }
    public string File { get; set; } = null!;
    public string Position { get; set; } = null!;
}

public sealed class SoundOptions
{
    public bool Enabled { get; set; }
    public string File { get; set; } = null!;
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public int Volume { get; set; }
    public bool Repeat { get; set; }
    /// <summary>响不响除了每一步自己那一条 <c>sound</c>，还看这一次淡入走完没有。
    /// 默认 true 就是「落定之后才开始放声音」；拧成 false 是「哪一步说要声，哪一步就响」（哪一步都要声就一路都响）。</summary>
    public bool AfterLanding { get; set; }
}

/// <summary>日历的那些值一律是**比例**，不是像素：一块 1280 的屏和一块 3840 的屏上，
/// 同一份配置该长出同一张月历 —— 所以这里没有一个键的单位是 px。
///
/// <c>WidthPercent</c>/<c>HeightPercent</c> 是铺桌面那两档沿工作区两条边各取的一份，
/// <c>WidgetWidthPercent</c> 是小面板那条边，<c>HeaderPercent</c> 是抬头一带占面板高度的多少，
/// <c>MarginX/Percent</c> 是离那个角多远（也是百分比，所以换分辨率不用重看这两个数），
/// <c>TextPercent</c> 是从格子高度推出来的那个字号再乘多少。
/// 每一档都有一条声明的范围兜着，布局那头发不再自己夹第二遍。</summary>
/// 番茄钟自己那一个键。和下面三个组类一样，这里没有默认值 —— 它由构造函数里的绑定
/// 从声明表取走。
///
/// **只有专注段在这儿**：休息段是 <c>flows</c> 里 <c>pomodoro</c> 那一行，跟别的休息流程
/// 同权，用休息流程编辑器改。在这一组里另设「休息几分钟」就是第二份真相，而那个数会过期。
public sealed class PomodoroOptions
{
    public int WorkMin { get; set; }
}

public sealed class CalendarOptions
{
    public bool Enabled { get; set; }
    public string Style { get; set; } = null!;
    public string Corner { get; set; } = null!;
    public int WidthPercent { get; set; }
    public int HeightPercent { get; set; }
    public int WidgetWidthPercent { get; set; }
    public int HeaderPercent { get; set; }
    public int MarginXPercent { get; set; }
    public int MarginYPercent { get; set; }
    public int TextPercent { get; set; }
    public int OpacityPercent { get; set; }
    public bool WeekStartsMonday { get; set; }
    public bool ShowLunar { get; set; }
}

/// 休息流程里的一步。默认值同样只在 Settings.StepDeclarations 里写一次。
public sealed class Step
{
    public int Ms { get; set; }
    public bool Dismissible { get; set; }
    public bool Interactive { get; set; }
    public List<string> Content { get; set; } = null!;
    public bool Sound { get; set; }

    /// 这一步自带的文字（<c>steps[].text</c>，工单 04）。空 = 什么都不画 ——
    /// 那是「这一步没带字」和「勾了文字但没写」共用的那一档。
    ///
    /// <b>它不在 <see cref="Content"/> 之外另算一份</b>：<c>content</c> 里有 <c>text</c>
    /// 才画，值在这一行上，两个字都不在别处存第二遍。
    ///
    /// <b>2026-10-06：它退休了，但没撤声明</b>（设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节）。
    /// 现在屏上的字是布局里那一个<b>文字控件</b>，这一行只在<b>老配置</b>里被读一次，
    /// 读完就翻成那个控件（见 <see cref="ReadFrom"/>）。所以：一份带 <c>layout</c> 的文件
    /// 读进来之后，<c>text</c> 说什么不算数 —— 它是「陌生的键」，零代价。
    ///
    /// <c>= null!</c> 不是默认值（同上面那几行）：构造函数里那个闭包会填上它，
    /// 真漏了一个键，SelfCheck 的「声明了但没有绑定」那条会抓到。
    public string Text { get; set; } = null!;

    /// 这一步的那一份<b>布局</b>（<c>steps[].layout</c>，设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）：
    /// 屏上摆哪几个控件、各在哪、各占多大、谁盖住谁。**挂在这一步上**，不挂在休息流程上、
    /// 不全局共用。
    ///
    /// <b>数组的次序就是叠的次序</b>：后来摆的在上。
    ///
    /// <c>= null!</c> 不是默认值（同上面那几行）：构造函数里那个闭包会填上**一份新的**
    /// 默认布局（`StepLayout.Default()`，那是今天那一屏）。
    public List<StepLayout> Layout { get; set; } = null!;

    /// 布局那个键在文档里的名字。读文档时靠它答「这一行有没有布局」——
    /// <b>有</b>，<c>content</c> / <c>text</c> 就只是陌生的键；<b>没有</b>，那两个成员
    /// 被读这一次、翻成一份布局。老配置能不能自己长出布局，全在这一个问句上。
    public const string LayoutKey = "layout";

    /// step 的声明带着 "steps[]." 前缀，因为「sound」这个名字在顶层已经被那个声音组
    /// 占了；走到文档里找键的时候前缀要去掉。
    private readonly SettingMap _map = new(Step.KeyPrefix);

    public const string KeyPrefix = "steps[].";

    public Step()
    {
        _map.Bind(KeyPrefix + "ms", () => Ms, v => Ms = v);
        _map.Bind(KeyPrefix + "dismissible", () => Dismissible, v => Dismissible = v);
        _map.Bind(KeyPrefix + "interactive", () => Interactive, v => Interactive = v);
        _map.Bind(KeyPrefix + "content", () => Content, v => Content = v);
        // 绑的次序和 StepDeclarations 里那几行一样 —— SelfCheck 会对着核。
        _map.Bind(KeyPrefix + "text", () => Text, v => Text = v);
        // `layout` 的默认值是一份**新的**默认布局（`DefaultAsValue` 每次现造）——
        // 共用同一个列表的话，改一步的布局会把每一步的布局一起改了。
        _map.Bind(KeyPrefix + LayoutKey, () => Layout, v => Layout = v);
        _map.Bind(KeyPrefix + "sound", () => Sound, v => Sound = v);
    }

    public JsonObject ToJson() => _map.ToJson();

    /// <summary>读文档里的一步。<b>有没有 <c>layout</c> 决定谁说了算</b>：
    /// 有，它就是权威，<c>content</c> / <c>text</c> 照旧读进来（老成员还得读得进来）但不作数；
    /// 没有，那两个成员被读这一次、翻成一份<b>照今天排法</b>的布局
    /// （<see cref="StepLayout.Translate"/>）。
    ///
    /// <paramref name="imageFile"/> / <paramref name="imageFit"/> 是那张全局的图 ——
    /// 老配置里图不在一步身上（在顶层 <c>image.*</c> 那三格），而翻出来的那份布局里
    /// 要有一个铺满整屏的图控件，所以翻译得知道那一张是谁。传空串就是「没有图」。
    /// </summary>
    public List<string> ReadFrom(JsonObject row, string imageFile = "", string imageFit = "")
    {
        var problems = _map.ReadFrom(row);
        if (!row.ContainsKey(LayoutKey))
            Layout = StepLayout.Translate(Content, Text, Ms > 0, Interactive, imageFile, imageFit);
        return problems;
    }

    /// <summary>按老写法（<c>content</c> + <c>text</c>）给自己翻一份布局。
    ///
    /// <b>出厂那几步走这一条</b>：它们身上本来就只有老写法（<c>content</c> 列表和
    /// <c>text</c>），所以它们的布局是<b>翻</b>出来的，不是手摆的 —— 于是出厂那一屏
    /// 和今天一模一样，而「老配置照今天的排法自动生成一份布局」那条在出厂这几步身上
    /// 也走的是同一条路（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）。翻出来的次序和位置都取
    /// <see cref="StepLayout.Default"/> 那一份。
    /// </summary>
    public void AdoptLegacyLayout() =>
        Layout = StepLayout.Translate(Content, Text, Ms > 0, Interactive);

    /// <summary>界面上改这一步的一个字段，走的是和读文档同一条路：夹取、
    /// 「认不出的 content 丢掉」、以及默认值都还是声明表那一份说了算 ——
    /// 编辑器里不写第二个规则。
    ///
    /// 字段名在这里是<b>裸的</b>（"ms"、"sound"）：那张索引按 "steps[]." 前缀收声明，
    /// 而一张 step 表内部的键就是那几个字段名 —— 传前缀进来会在 <see cref="SettingMap"/>
    /// 里查无此键。<see cref="Step.Set"/> 和 <see cref="Step.Get"/> 因此不对称于
    /// <see cref="Settings.Decl"/>，那是 step 的键会和顶层的 "sound" 撞名才有的两难。</summary>
    public void Set(string field, object value) => _map.Set(field, value);

    /// 这一步当前值，形状和声明表一致（整数、bool、字符串列表）。
    public object Get(string field) => _map.Get(field);

    /// 抄一步。「新增一条休息流程」抄的是正在编辑的那一条，所以这里要的是一份能改的副本，
    /// 不是同一个引用 —— 同一个引用的话，改新那条会把原来那条一起改了。
    /// <b>布局是深抄的</b>：那些控件对象照样是同一个引用的话，改新那一步挪一个控件，
    /// 原来那一步也跟着挪了 —— 那正是上面这句话要防的事，而布局比那几格更容易踩到。
    public Step Clone() => new()
    {
        Ms = Ms,
        Dismissible = Dismissible,
        Interactive = Interactive,
        Content = [.. Content],
        Text = Text,
        Layout = [.. Layout.Select(control => control.Clone())],
        Sound = Sound,
    };
}

/// <summary>作息表里的一行 —— 一个<b>时间块</b>。默认值同样只在
/// <see cref="Settings.BlockDeclarations"/> 里写一次。
///
/// <b>块的字段是「这一块怎么过」，不是「它属于哪一类」</b>：<see cref="Fade"/> 和
/// <see cref="YieldsToInput"/> 逐块写（默认值由声明表给），唯一从种类推出来的是
/// <see cref="IsSleep"/> —— 种类里只有「睡眠」带行为语义（取向和代价：
/// <c>docs/adr/0005-routine-text-and-preset-life-flows.md</c>）。
///
/// 起止是**一天里的第几分钟**（0–1439），不是一个时长：这一族一个毫秒都没有，
/// 界面上的单位是「时刻」和「分钟」两种。
///
/// 字段名在这张表的索引里带着 <see cref="KeyPrefix"/> 前缀（「sound」这类裸名会和顶层撞），
/// 走到文档里找键的时候前缀要去掉 —— 和 <see cref="Step"/> 同一处境。</summary>
public sealed class TimeBlock
{
    public const string KeyPrefix = "blocks[].";

    /// 从一天的哪一分钟开始，0 是零点整。
    public int StartMinute { get; set; }

    /// 到哪一分钟为止。**不晚于 <see cref="StartMinute"/> 就是跨到次日**（22:30 → 06:30
    /// 是一夜的一块），判据住在 <see cref="Routine.Covers"/>。
    public int EndMinute { get; set; }

    /// 这一块叫什么。托盘那一行和遮罩上原样显示的就是这几个字。
    public string Name { get; set; } = null!;

    /// 只是一个标签；带行为语义的只有 <see cref="Settings.BlockKindSleep"/>。
    public string Kind { get; set; } = null!;

    /// 到点那一次抬不抬遮罩。
    public bool Fade { get; set; }

    /// 到点那一次走哪一行休息流程；空 = 走「这次休息走哪一条」指着的那一行。
    public string Flow { get; set; } = null!;

    /// 这一块里，连续输入太久的那次提醒让不让位。
    public bool YieldsToInput { get; set; }

    private readonly SettingMap _map = new(KeyPrefix);

    public TimeBlock()
    {
        _map.Bind(KeyPrefix + "startMinute", () => StartMinute, v => StartMinute = v);
        _map.Bind(KeyPrefix + "endMinute", () => EndMinute, v => EndMinute = v);
        _map.Bind(KeyPrefix + "name", () => Name, v => Name = v);
        _map.Bind(KeyPrefix + "kind", () => Kind, v => Kind = v);
        _map.Bind(KeyPrefix + "fade", () => Fade, v => Fade = v);
        _map.Bind(KeyPrefix + "flow", () => Flow, v => Flow = v);
        _map.Bind(KeyPrefix + "yieldsToInput", () => YieldsToInput, v => YieldsToInput = v);
    }

    /// <summary>这一块是不是<b>睡眠</b>。整族唯一一条从「种类」推出来的行为语义，其余字段
    /// 逐块显式写（<see cref="Settings.BlockKindSleep"/> 那条声明的注释写着为什么）。</summary>
    public bool IsSleep => Kind == Settings.BlockKindSleep;

    public JsonObject ToJson() => _map.ToJson();

    public List<string> ReadFrom(JsonObject row) => _map.ReadFrom(row);

    /// 这一行接了哪几个字段。给 <see cref="Settings.SelfCheck"/> 问「声明了但没有接着」
    /// 那一问用 —— 那一问问的是**绑定表**，不是「字段名对不对」。
    public SettingMap Map => _map;

    /// 界面上改一个字段走这一条（工单 02 的编辑器），和读文档同一条路：夹取和默认值
    /// 仍然由声明表说了算。字段名是<b>裸的</b>，理由和 <see cref="Step.Set"/> 一样。
    public void Set(string field, object value) => _map.Set(field, value);

    public object Get(string field) => _map.Get(field);
}

/// <summary>勿扰那张时段表里的一行 —— 一<b>条时段</b>。默认值同样只在
/// <see cref="Settings.WindowDeclarations"/> 里写一次。
///
/// 形状和 <see cref="TimeBlock"/> 同一处境（表里的行，键名在文档里是裸的，索引按
/// <see cref="KeyPrefix"/> 前缀收声明），但**它自己不带任何行为**：一条时段只回答
/// 「此刻在不在这一段里」，静默的那些政策全在 <see cref="Scheduler"/> 那一侧（和
/// 「块内行为逐块写，不从种类推」那个取向同款 —— 这里连种类都没有）。
///
/// 起止是<b>一天里的第几分钟</b>（0–1439），不是一个时长：这一族一个毫秒都没有，
/// 界面上的单位是「时刻」和「分钟」两种。
///
/// <b>跨午夜整段挂在开始的那一天</b>（<see cref="Dnd.WindowCovers"/> 判它）—— 这一条不住在
/// 这一行上：它是那一族算术的一部分，判据和「止不晚于起 = 跨午夜」那一条是同一条。</summary>
public sealed class DndWindow
{
    public const string KeyPrefix = "windows[].";

    /// 从一天的哪一分钟开始，0 是零点整。
    public int StartMinute { get; set; }

    /// 到哪一分钟为止。<b>不晚于 <see cref="StartMinute"/> 就是跨到次日</b>（22:30 → 06:30）。
    public int EndMinute { get; set; }

    /// 星期几，一列词（<see cref="Dnd.WeekdayKeys"/> 里那七个）。空 = 一个都不在
    /// —— 那是一条**永远不成立**的时段，不是「每天」（每天要七个都勾上：声明表那一格
    /// 不给「每天」这个取值，因为它是七个词的一份缩写，而缩写会过期）。
    public List<string> Weekdays { get; set; } = null!;

    private readonly SettingMap _map = new(KeyPrefix);

    public DndWindow()
    {
        _map.Bind(KeyPrefix + "startMinute", () => StartMinute, v => StartMinute = v);
        _map.Bind(KeyPrefix + "endMinute", () => EndMinute, v => EndMinute = v);
        // 绑的次序和 WindowDeclarations 里那几行一样 —— SelfCheck 会对着核。
        _map.Bind(KeyPrefix + "weekdays", () => Weekdays, v => Weekdays = v);
    }

    public JsonObject ToJson() => _map.ToJson();

    public List<string> ReadFrom(JsonObject row) => _map.ReadFrom(row);

    /// 这一行接了哪几个字段。给 <see cref="Settings.SelfCheck"/> 问「声明了但没有接着」那一问用。
    public SettingMap Map => _map;

    /// 界面上改一个字段走这一条，和读文档同一条路：夹取和默认值仍然由声明表说了算。
    /// 字段名是<b>裸的</b>，理由和 <see cref="Step.Set"/> 一样。
    public void Set(string field, object value) => _map.Set(field, value);

    public object Get(string field) => _map.Get(field);
}

public sealed class Flow
{
    public const string StepsKey = "steps";

    public List<Step> Steps { get; set; } = [];

    public JsonObject ToJson()
    {
        var list = new JsonArray();
        foreach (var step in Steps) list.Add(step.ToJson());
        return new JsonObject { [StepsKey] = list };
    }

    /// 抄一条休息流程：每步都是抄的，不是同一个引用（见 <see cref="Step.Clone"/>）。
    public Flow Clone()
    {
        var copy = new Flow();
        foreach (var step in Steps) copy.Steps.Add(step.Clone());
        return copy;
    }

    /// 一张表里的一行。没写 steps 就读作「这条休息流程没有步」，由 Settings.Steps 回落
    /// 到 fadetop，而不是读作「不淡入」。
    ///
    /// <b>那两个图参数往下传给 <see cref="Step.ReadFrom"/></b>：老配置里的图不在一步身上，
    /// 翻出来的那份布局里却要有一个图控件（见 <see cref="StepLayout.Translate"/>）。
    /// 不传就是「没有图」——<see cref="Settings.AdoptValues"/> 抄一份 flows 的时候走的就是
    /// 那一路（那一份布局已经在 <c>ToJson</c> 里，不必再翻）。
    public List<string> ReadFrom(string name, JsonObject fields, string imageFile = "", string imageFit = "")
    {
        var problems = new List<string>();
        if (!fields.TryGetPropertyValue(StepsKey, out var node) || node == null) return problems;
        if (node is not JsonArray list)
        {
            problems.Add($"{Settings.ModesKey}.{name}.{StepsKey}: 期望一个列表");
            return problems;
        }
        var steps = new List<Step>();
        foreach (var item in list)
        {
            if (item is not JsonObject row)
            {
                problems.Add($"{Settings.ModesKey}.{name}.{StepsKey}: 每一项都要是一个对象");
                continue;
            }
            var step = new Step();
            problems.AddRange(step.ReadFrom(row, imageFile, imageFit));
            steps.Add(step);
        }
        Steps = steps;
        return problems;
    }
}
