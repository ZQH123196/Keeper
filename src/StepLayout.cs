using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>休息流程里<b>一步</b>的那一份<b>布局</b>：屏上摆着哪几个控件、各在哪、
/// 各占多大、谁盖住谁。挂在一<b>步</b>上，不挂在休息流程上、不全局共用 ——
/// 设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节的形状。
///
/// <b>位置和大小是屏宽屏高的份额，不是像素</b>：存像素的话换一块分辨率不同的屏，
/// 每一块屏上摆的位置都是错的（同一节）。<b>字号是控件那个框的一个份额</b>，加一根跟随
/// 系统 DPI 的地板 —— 那根地板是<b>日历那一根</b>（<see cref="Keeper.Calendar.MinFontAt96"/>），
/// 不是另起一个数。链是「屏的份额 → 框 → 字号 → 地板」，所以换一块屏换的是画面，
/// 不是那份配置。
///
/// <b>列表的次序就是叠的次序</b>：设计文档 `docs/design/fader-layout.md`「允许重叠，重叠时后来摆的在上」那一节这么说，
/// 所以一份布局的数组里越靠后越在上面 —— 默认布局第一项是铺在底层的那张图，
/// 就是这一条（<c>FaderContent.tscn</c> 里 <c>Fill</c> → <c>Picture</c> → <c>Middle</c>
/// → <c>Side</c> 那个兄弟序）。
///
/// <b>为什么控件的字段也走声明表</b>（<see cref="Settings.StepLayoutDeclarations"/>）：
/// ADR-0001「不建 schema 链，界面在编辑器里搭成场景」那一节 —— 一个限制只声明一次，
/// 界面、夹取、恢复默认读同一份。所以这里的字段名、范围、默认值、可选值和写在
/// <c>settings.jsonc</c> 里那两行说明全在那张表上，这个文件里不抄第二个数。
///
/// <b>这一族没有像素，也没有毫秒</b>：界面上不许出现毫秒那条（设计文档 `docs/design/config.md`「界面上不出现毫秒」）
/// 在这里靠<b>没有 <c>*Ms</c> 键</b>兑现 —— 全是百分数。</summary>
public sealed class StepLayout
{
    // 键名带着这一层的路径，因为这几个名字在别的层上也有（"text"、"kind"、"file"），
    // 而那张索引按前缀收声明，走到文档里找键的时候前缀要去掉 —— 和 Step / TimeBlock 同一处境。
    public const string KeyPrefix = "steps[].layout[].";

    /// 同一个地方在<b>报错时</b>的写法：带上下标、没有末尾那个点（<c>KeyPrefix</c> 那个
    /// 末尾的点是为了拼键名，两种用途所以两个字面量）。
    private const string RowPath = "steps[].layout";

    // ── 那七种控件 ─────────────────────────────────────────────────────────
    // 五种各至多一个（两个时钟没有意义，第二个永远显示同一个时刻），文字和图不限个数。
    // 「不加新的种类」是契约写死的：每加一种，声明表加一行、布局加一个分支、编辑器加一颗钮、
    // 遮罩加一段绘制。所以这里是七个名字，不是「一个字符串随便填」。
    public const string KindClock = "clock";
    public const string KindRemain = "remain";       // 界面上写「还剩」，代码这一族叫 Remain
    public const string KindChecklist = "checklist";
    public const string KindBlock = "block";
    public const string KindEnd = "end";
    public const string KindText = "text";
    public const string KindImage = "image";

    /// <b>可选值只有一份</b>：声明表从这儿取（<c>kind</c> 那一条），自检从这儿数。
    public static readonly string[] Kinds =
        [KindClock, KindRemain, KindChecklist, KindBlock, KindEnd, KindText, KindImage];

    /// 文字和图之外那五种：每种最多一个。<b>为什么是一份名单而不是从别处推</b> ——
    /// 「哪些控件是独一份」是会过期的判断，散到夹取那一处就没人跟着改了。
    public static readonly string[] SingletonKinds =
        [KindClock, KindRemain, KindChecklist, KindBlock, KindEnd];

    public static bool IsKind(string kind) => Array.IndexOf(Kinds, kind) >= 0;

    // ── 那五种摆法 ─────────────────────────────────────────────────────────
    // 图在<b>它那个框</b>里的摆法：2026-10-06 之前这五种是整屏背景的摆法，现在那个框就是
    // 它们的边界（「平铺」是在框里平铺）。五个名字和顶层 <c>image.position</c> 那一族完全一样，
    // 所以<b>顶层那一条的可选值也从这儿取</b> —— 五行名单只写一次，写两处就有一处会过期。
    public static readonly string[] FitModes = ["fill", "fit", "stretch", "tile", "center"];

    /// 图控件摆法的默认值，<b>和顶层 <c>image.position</c> 同源</b>：今天那一屏出厂摆的是
    /// 「居中」，所以默认布局里那个铺满整屏的图控件也是「居中」。
    public const string DefaultFitMode = "center";

    /// 认不出的摆法回到默认，而不是带着一个谁都不认识的值跑下去。
    public static string KnownFit(string fit) =>
        Array.IndexOf(FitModes, fit) >= 0 ? fit : DefaultFitMode;

    // ── 这一族的范围 ───────────────────────────────────────────────────────
    // 份额的单位是百分之一（和日历那一族同一套口径）。位置 0–100、大小 1–100：
    // **下限 1 是「不许是零」那条契约**（框太小字就读不出来，而零高的框什么都装不下）。
    public const double MinShare = 0;
    public const double MaxShare = 100;
    public const double MinSizeShare = 1;

    /// <summary>一个文字控件的字数上限。<b>它住在模型上而不是某一张声明表里</b>：
    /// 两个地方都要用它 —— <c>steps[].text</c>（老成员，今天的 <c>Fader.TextCap</c> 问的就是那一条）
    /// 和布局里那一个文字控件 —— 而静态字段按声明的次序初始化，一张表在初始化时问另一张表
    /// 读到的是一个还没初始化的字典。
    ///
    /// 取 120 的实测结果在 <c>docs/research/images/fader-mask-text-wrap.png</c> 和工单 04 的
    /// Comments 里：2560×1440 那块屏上八成列宽是一行 33 个汉字，120 字是四行上下。</summary>
    public const int TextCap = 120;

    // ── 一个控件 ───────────────────────────────────────────────────────────

    /// 哪一种（<c>kind</c>）。它先读，因为「那几行里哪些字段算数」取决于它。
    public string Kind { get; set; } = null!;

    /// 左缘是屏宽的几成。0 是屏幕左边，100 是右边。
    public int XPercent { get; set; }

    /// 上缘是屏高的几成。
    public int YPercent { get; set; }

    /// 宽是屏宽的几成。
    public int WidthPercent { get; set; }

    /// 高是屏高的几成。
    public int HeightPercent { get; set; }

    /// <b>字号是上面那个框的一个份额</b>（不是屏高的一份）。链是「屏的份额 → 框 → 字号 →
    /// 地板」，契约那一节写的就是这条链。
    public int FontPercent { get; set; }

    /// 文字控件装的那一段话。<b>人自己写，Keeper 一句劝说文案都不写</b>（契约
    /// 「遮罩上没有 Keeper 自己写的劝说文案」）。别的几种控件不读它。
    public string Text { get; set; } = null!;

    /// 图控件装的那个路径。别的几种控件不读它。
    public string File { get; set; } = null!;

    /// 图控件在<b>它那个框</b>里的摆法，五个取值见 <see cref="FitModes"/>。
    public string Position { get; set; } = null!;

    private readonly SettingMap _map = new(KeyPrefix);

    public StepLayout()
    {
        _map.Bind(KeyPrefix + "kind", () => Kind, v => Kind = v);
        _map.Bind(KeyPrefix + "xPercent", () => XPercent, v => XPercent = v);
        _map.Bind(KeyPrefix + "yPercent", () => YPercent, v => YPercent = v);
        _map.Bind(KeyPrefix + "widthPercent", () => WidthPercent, v => WidthPercent = v);
        _map.Bind(KeyPrefix + "heightPercent", () => HeightPercent, v => HeightPercent = v);
        _map.Bind(KeyPrefix + "fontPercent", () => FontPercent, v => FontPercent = v);
        // 绑的次序和 StepLayoutDeclarations 里那几行一样 —— SelfCheck 会对着核。
        _map.Bind(KeyPrefix + "text", () => Text, v => Text = v);
        _map.Bind(KeyPrefix + "file", () => File, v => File = v);
        _map.Bind(KeyPrefix + "position", () => Position, v => Position = v);
    }

    public JsonObject ToJson() => _map.ToJson();

    public List<string> ReadFrom(JsonObject row) => _map.ReadFrom(row);

    /// 这一行接了哪几个字段。给 <see cref="Settings.SelfCheck"/> 问「声明了但没有接着」
    /// 那一问用 —— 和 <see cref="TimeBlock.Map"/> 同一处境。
    public SettingMap Map => _map;

    /// 界面上改一个字段走这一条，和读文档同一条路：夹取仍然由声明表说了算。
    public void Set(string field, object value) => _map.Set(field, value);

    public object Get(string field) => _map.Get(field);

    /// 这个控件有没有字。图控件没有 —— 所以图那一行那个字号是占位，界面上摆图时不问它。
    public bool HasWords => Kind is KindClock or KindRemain or KindChecklist or KindBlock
        or KindEnd or KindText;

    /// 抄一个控件。设置窗的「按保存才搬」和「新增一步」都走它 ——
    /// 同一个引用的话，改副本里那一处会把活的那一处也改了。
    public StepLayout Clone() => new()
    {
        Kind = Kind,
        XPercent = XPercent,
        YPercent = YPercent,
        WidthPercent = WidthPercent,
        HeightPercent = HeightPercent,
        FontPercent = FontPercent,
        Text = Text,
        File = File,
        Position = Position,
    };

    // ── 默认布局：今天那一屏 ───────────────────────────────────────────────

    /// <summary>默认布局。<b>就是今天这一屏原样</b>（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）：
    /// 第一次运行和「回到默认布局」摆的都是这一份。
    ///
    /// <b>每一个数从哪来</b> —— 字号那一列对着 <c>src/Fader.cs</c> 的 <c>Measure</c>：
    /// 时钟 13.1%（<c>ClockRatio</c>）、倒计时 17%（<c>RemainRatio</c>）、
    /// 清单那一行和这一步自带的文字 4.3%（<c>RowRatio</c>）、块名 2.8%（<c>SmallRatio</c>）、
    /// 那颗钮跟块名同档。**框高和字号份额是照这一列倒推的** ——
    /// 今天那四个控件是贴字的 Label，它的框就是它的行盒，所以框比字号高约三成；
    /// 倒过来按「字号 = 框 × <c>fontPercent</c>」摆回去，今天那几个号原样回来（差不到 0.1 个百分点）。
    ///
    /// <b>横着那一列 10–90</b>：今天每一行都在整屏宽的居中容器里，而自由文字那一行的
    /// 最小宽度是屏宽的八成（<c>Fader.cs</c> 里 <c>CustomMinimumSize = Size.X * 0.8f</c>），
    /// 所以这一列占的就是那八成，四个控件居中在它里面 —— 和今天画出来的一模一样。
    ///
    /// <b>竖着那几段</b>：今天是一列竖排居中、行距 24 像素。24 是像素（换一块屏就不是那个数了），
    /// 而这一族没有「行距」这一格 —— 所以这里摆成 2 个百分点的缝（1440 高的屏上约合今天的
    /// 24 像素），整列上下各留 20。清单和那颗钮在侧面那一列（屏宽的 34%，左右缘 0.62 / 0.96），
    /// 它们的高度今天随条数长，这里给的是「几条放得下」的那个框。
    ///
    /// <b>图控件铺满整屏</b>，摆法跟 <see cref="DefaultFitMode"/>（今天的
    /// <c>image.position</c> 默认值）；路径是空的 —— 空路径就是没有图。</summary>
    public static List<StepLayout> Default() =>
    [
        new()
        {
            Kind = KindImage,
            XPercent = 0, YPercent = 0, WidthPercent = 100, HeightPercent = 100,
            FontPercent = 75, File = "", Position = DefaultFitMode,
        },
        new() { Kind = KindClock, XPercent = 10, YPercent = 20, WidthPercent = 80, HeightPercent = 17, FontPercent = 77 },
        new() { Kind = KindRemain, XPercent = 10, YPercent = 39, WidthPercent = 80, HeightPercent = 22, FontPercent = 77 },
        new() { Kind = KindBlock, XPercent = 10, YPercent = 63, WidthPercent = 80, HeightPercent = 4, FontPercent = 70 },
        new() { Kind = KindText, XPercent = 10, YPercent = 69, WidthPercent = 80, HeightPercent = 11, FontPercent = 40 },
        // **2026-10-12 所有者定夺重做了这一格和下面那颗钮**（原话：「休息阶段 todolist
        // **2026-10-12 所有者定夺重做了这一格和下面那颗钮**（原话：「休息阶段 todolist
        // 的位置和大小太难看」）。改三样，每一样有它自己的理由：
        //
        // <b>一、字号 4 → 3。</b>字号是<b>框高的一个份额</b>（<c>Place</c> 那一发），
        // 而这一格的框是满高，所以 <c>fontPercent</c> 写多少，字号就是屏高的百分之几。
        // 原来那个 4 在 1440 高的屏上是 57 像素 —— 和倒计时那两颗 189 像素的号只差一档，
        // 于是清单在休息这一屏上和时钟抢眼睛，而它是**辅助**信息：该被读到，不该被盯着看。
        // 3 在 1440 高的屏上是 43 像素，退回正文那一档。
        //
        // <b>为什么是 3 而不是 2.4</b>（那才是按「34 像素」倒推出来的数）：这一格
        // <c>fontPercent</c> 的类型是 <c>int</c>，配置里也是整数（声明表那一行），
        // 所以这一族的粒度是 1 个百分点 —— 1440 高的屏上就是 14 像素一跳。
        // 为了 9 个像素去改整个 schema 的类型，不值。
        //
        // <b>二、左右缘 0.62 / 0.96 → 0.64 / 0.94。</b>右边那一档原来离屏边只剩 4%（102 像素）。
        // 这一改不是为了让清单离屏边远一点 —— 清单的内容是<b>在列里居中</b>的，框贴不贴边
        // 和内容贴不贴边本来就是两件事。这一改是为了让<b>下面那颗钮</b>跟着同一列走：
        // 原来两栏重合，而钮比清单矮一档、又是按自己的宽度居中，于是它和上面几行对不齐。
        //
        // <b>三、框仍然满高 —— 这一条不是审美，是一条判据。</b>
        // 满高复现的是「内容在那一列里居中」这件事，这一列外面套着 <c>CenterContainer</c>。
        // 2026-10-06 那一发把按内容高度摆的版本拆穿过：采样点 (1920,720) 从两行之间的缝里
        // 挪到第三行上读到 (207,231,219)，「alpha 按 8 位混」那一问从 holds 翻成 DOES NOT HOLD
        // （逐通道差 143）。那一族在 <c>docs/research/probe-and-criteria-pitfalls.md</c>。
        // 位置和字号随便改，**满高这一条不许改**。
        new() { Kind = KindChecklist, XPercent = 70, YPercent = 0, WidthPercent = 26, HeightPercent = 100, FontPercent = 3 },
        // 那颗钮和清单同列（0.70 / 0.96）。上下不是紧挨着：清单那一列是满高、内容在里面
        // 居中，所以「清单有几条」决定它落到哪一档 —— 三条时最后一行落在屏高 56% 附近，
        // 钮原来摆 56% 就正好压在它下缘上（实测那一帧：清单到 y=810、钮从 y=806 开始）。
        // 摆 60% 就是 1440 高的屏上 864 起，三条时留 54 像素的空当。
        // 框 6% 高、字号占框的 40% —— 1440 上是 35 像素，和清单那一行同档，两样在视觉上是
        // 同一族（原来框 5% / 字 40% 是 29 像素，比清单小了一大截）。
        new() { Kind = KindEnd, XPercent = 70, YPercent = 60, WidthPercent = 26, HeightPercent = 6, FontPercent = 40 },
    ];

    // ── 老配置那一次翻译 ───────────────────────────────────────────────────

    /// <summary>老配置照<b>今天的排法</b>生成一份布局（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）。
    ///
    /// <b>选这条路而不是「老配置退回默认」</b>：那几个名字今天还认得，翻译是几行代码的事，
    /// 用户手改过的东西不该静悄悄消失。
    ///
    /// 摆哪些控件：<c>content</c> 里<b>认得</b>的成员照原样变成同名控件，那一步自带的
    /// <c>text</c> 变成一个文字控件，配置里那格图变成一个铺满整屏的图控件；
    /// 写着但不画的 <c>health</c> / <c>trail</c> <b>不变成控件</b>（2026-10-06 从遮罩上撤了，
    /// 但那两个名字在老配置里照旧认得 —— 按陌生成员处理会让一份老文件突然报错，
    /// 那不是删一样东西，是砸掉别人的文件）。
    ///
    /// <b>两样不在 <c>content</c> 里、今天也照样出现的</b>：<b>倒计时</b>跟着那一步的
    /// <c>ms</c> 走（没有到期时刻就没有倒计时，契约给用户的选择里没有这一条），
    /// <b>那颗钮</b>跟着 <c>interactive</c> 走（没收鼠标的那一步没有它就是卡住）。
    /// 「照今天的排法」说的就是这两样，所以它们也是被翻出来的，不是漏掉的。
    ///
    /// <b>位置从 <see cref="Default"/> 那一份取</b>：老配置里没有位置可翻，翻出来的
    /// 就是今天那一屏；叠的次序也照今天那份（先图、后中间那一族、再侧面两样）。
    /// 所以这里是「照 <see cref="Default"/> 走一遍，挑要的那几个」，不是「从零里造」。
    /// </summary>
    public static List<StepLayout> Translate(
        IReadOnlyList<string> content, string text, bool hasDeadline, bool interactive,
        string imageFile = "", string imageFit = "")
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in content)
            if (IsKind(member)) wanted.Add(member);
        if (hasDeadline) wanted.Add(KindRemain);
        if (interactive) wanted.Add(KindEnd);
        if (imageFile.Length > 0) wanted.Add(KindImage);

        var layout = new List<StepLayout>();
        foreach (var shape in Default())
        {
            if (!wanted.Contains(shape.Kind)) continue;
            var control = shape.Clone();
            if (control.Kind == KindText) control.Text = text;
            if (control.Kind == KindImage)
            {
                control.File = imageFile;
                control.Position = KnownFit(imageFit);
            }
            layout.Add(control);
        }
        return layout;
    }

    // ── 读文档 ─────────────────────────────────────────────────────────────

    /// <summary>读一份布局。<b>元素级的错不该把整份布局扔掉</b> —— 那一张名单
    /// （勿扰那张 exe 名单）就是那个做法：跳过那一个、把话说完，别的照旧读进来。
    ///
    /// <b>为什么 <c>kind</c> 缺了就是一条读不通</b>：它是这一行的身份，缺了它这一行
    /// 是什么控件就说不出来，而拿默认值补上等于替用户加一个他没摆的控件。</summary>
    public static LayoutValue Read(JsonArray rows)
    {
        var parsed = new List<(int At, StepLayout Control)>();
        var problems = new List<string>();
        int at = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject fields)
            {
                problems.Add($"{Where(at)}: 期望一个控件对象，文档里写的是 {row?.ToJsonString()}");
                at++;
                continue;
            }
            if (!fields.ContainsKey("kind"))
            {
                problems.Add($"{Where(at)}: 没有 kind —— 不知道这一行是什么控件");
                at++;
                continue;
            }
            var control = new StepLayout();
            foreach (var problem in control.ReadFrom(fields))
                problems.Add($"{Where(at)}: {problem}");
            if (!IsKind(control.Kind))
            {
                problems.Add($"{Where(at)}: 不认识的控件 '{control.Kind}'，这一行丢掉");
                at++;
                continue;
            }
            control.ClampShares();
            parsed.Add((at, control));
            at++;
        }

        // 独一份那五种：第二个丢掉。这不是「读坏了」，是那份文档摆了两个时钟 ——
        // 第二个永远显示同一个时刻（设计文档 `docs/design/fader-layout.md`「其余每种最多一个」）。说清楚丢的是哪一个。
        var kept = new List<StepLayout>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, control) in parsed)
        {
            if (Array.IndexOf(SingletonKinds, control.Kind) < 0 || seen.Add(control.Kind))
            {
                kept.Add(control);
                continue;
            }
            problems.Add($"{Where(index)}: {control.Kind} 又摆了一个 —— 这一种至多一个，这一个丢掉");
        }
        return new LayoutValue(kept, problems);
    }

    /// 报错时指着<b>第几个控件</b>（1 起）—— 文档里数组的位置和这一行对得上，
    /// 而「第几个存下来的」在丢过东西之后就对不上了。
    private static string Where(int at) => $"{RowPath}[{at + 1}]";

    // ── 夹取 ───────────────────────────────────────────────────────────────

    /// <summary>一份布局回到它自己那份声明允许的形状。<b>范围只写在声明表里一份</b>：
    /// 每一格的范围都从 <see cref="Settings.Decl"/> 问出来。</summary>
    public static List<StepLayout> Sanitize(List<StepLayout> value)
    {
        var kept = new List<StepLayout>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in value)
        {
            if (!IsKind(control.Kind)) continue;                        // 不认识的种类不当控件
            if (Array.IndexOf(SingletonKinds, control.Kind) >= 0 && !seen.Add(control.Kind)) continue;
            control.ClampShares();
            kept.Add(control);
        }
        // **全删光了就是空的一份**：用户按「全删了」是他的意思，不是读坏了。
        // `content` 那一条「全丢光了回到默认」是给读坏的文件兜底，布局这一族不留那个兜底 ——
        // 设计文档 `docs/design/fader-layout.md`「六种都能改大小，都能删」那一节明写这一条。
        return kept;
    }

    /// 份额回到声明的范围里。位置不许把框推到屏外、大小不许是零 —— 头两件事是
    /// <see cref="Place"/> 那条纯算术的活（它知道屏有多大），这里只管声明里那两格。
    private void ClampShares()
    {
        XPercent = Settings.Decl(KeyPrefix + "xPercent").Clamp(XPercent);
        YPercent = Settings.Decl(KeyPrefix + "yPercent").Clamp(YPercent);
        WidthPercent = Settings.Decl(KeyPrefix + "widthPercent").Clamp(WidthPercent);
        HeightPercent = Settings.Decl(KeyPrefix + "heightPercent").Clamp(HeightPercent);
        FontPercent = Settings.Decl(KeyPrefix + "fontPercent").Clamp(FontPercent);
        Position = KnownFit(Position);
    }

    // ── 摆到屏上：纯算术 ───────────────────────────────────────────────────

    /// 摆在屏上的那个结果。份额和像素各一份，因为两边都有读者：
    /// 编辑器拖的时候在像素那一头，存进文档的是份额那一头。
    public readonly record struct Placed(
        double XPercent, double YPercent, double WidthPercent, double HeightPercent,
        double LeftPx, double TopPx, double WidthPx, double HeightPx,
        int FontPx, int FontFloor);

    /// <summary>字那根「再小就读不出来」的下限。<b>它是日历那一根</b>
    /// （<see cref="Keeper.Calendar.MinFontAt96"/>），乘的是系统缩放而不是分辨率 ——
    /// 设计文档 `docs/design/fader-layout.md`「字号是控件那个框的一个份额」那一节说的就是同一根地板。
    /// 全仓只此一份，别处不许再写一个 10 或者 12。</summary>
    public static int FontFloor(int dpiPercent) =>
        Math.Max(1, (int)(Keeper.Calendar.MinFontAt96 * dpiPercent / 100));

    /// <summary>一份份额换成屏上的那一个框和那一个字号。<b>纯函数</b>：它不读窗、
    /// 不读设置、不动任何字段，所以自检能在无头里直接问它「这一发摆出来在不在屏上、
    /// 字号掉没掉到地板」—— 判据要问的是算术，不是屏幕上有没有画出来。
    ///
    /// <b>两条不许破</b>（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）：控件不许<b>出屏</b>、不许<b>是零</b>。
    /// 出屏那一条在这里就已经收掉 —— 左缘推得太右就把左缘拉回来，宁可框小也不伸出去，
    /// 因为「点得到的那几块」按这个矩形算，算出屏就是算到别的屏上了。
    ///
    /// <b>字号：框高 × 那一格，再夹到地板上</b>。地板赢 —— 那种情况是这块地方本来就摆不下
    /// 这么大的字，字挤出去比把字缩到读不出来好（和日历那一档「上限赢不过下限」同款）。
    /// <b>字号不封顶</b>：契约没有这条规则，用户把字放到框外面是他自己摆的。</summary>
    public static Placed Place(
        double xPercent, double yPercent, double widthPercent, double heightPercent, double fontPercent,
        int screenWidthPx, int screenHeightPx, int dpiPercent = 100)
    {
        var w = Math.Clamp(widthPercent, MinSizeShare, MaxShare);
        var h = Math.Clamp(heightPercent, MinSizeShare, MaxShare);
        var x = Math.Clamp(xPercent, MinShare, MaxShare - w);
        var y = Math.Clamp(yPercent, MinShare, MaxShare - h);
        var floor = FontFloor(dpiPercent);
        var fontPx = (int)(screenHeightPx * h / 100
                           * Math.Clamp(fontPercent, 1, MaxShare) / 100);
        return new Placed(x, y, w, h,
            screenWidthPx * x / 100, screenHeightPx * y / 100,
            screenWidthPx * w / 100, screenHeightPx * h / 100,
            Math.Max(floor, fontPx), floor);
    }

    /// 这个控件摆出来是什么样 —— <see cref="Place"/> 那一段的薄包装，
    /// 省得每个调用方把九个数按同一个次序传一遍（次序错了算出来的是别的东西，
    /// 而算术本身不会说话）。
    public Placed Place(int screenWidthPx, int screenHeightPx, int dpiPercent = 100) =>
        Place(XPercent, YPercent, WidthPercent, HeightPercent, FontPercent,
            screenWidthPx, screenHeightPx, dpiPercent);

    /// 一份布局读出来的样子和它自己那些报告。<b>为什么不是一个 <c>List&lt;StepLayout&gtgt;</b>
    /// 加上别的通道</b>：读一个元素出的问题说的是「第几个控件」，和键名是一层的；
    /// 让它从 <c>SettingMap.ReadFrom</c> 那一条路上冒出来，就还是那个读者在收。
    public sealed record LayoutValue(List<StepLayout> Controls, List<string> Problems)
    {
        public static implicit operator List<StepLayout>(LayoutValue value) => value.Controls;
    }
}
