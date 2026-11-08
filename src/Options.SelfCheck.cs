using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>设置窗的自检那一半，和窗的实现住在同一个 type 上（<c>partial</c>）。
/// 跑在 <c>--check</c> 的 <c>options</c> 那一组里（登记在 <c>src/Probe.cs</c>）。
///
/// 为什么从 <c>Options.cs</c> 里分出来：2026-10-07 之前这一族和设置窗的实现挤在同一个文件里，
/// 那一族几乎占掉整份文件的一半，而它对那一扇窗没有任何用处。搬出来之后两件事各自成立 ——
/// <b>实现那一侧只有窗</b>，它的 public 面不再有只为自检而存在的成员；
/// <b>自检这一侧只有自检</b>，这一份里 0 个非 static 的 private 方法（全是 static）。
/// <b>行数不抄在这里</b> —— 以 <c>wc -l</c> 当场量的为准（工单 16 那一条：
/// 手抄的行数过期过，而注释里的数不参与任何一跑，没有判据会响）。
///
/// 分家不动实测结果：这一族里的每一行都是判据，搬一次要证明它们算出来的东西一模一样。
/// 判据本身没动过一个字，搬完各段落的缩进原样带过去（<c>partial</c> 不改缩进语义，
/// 所以那几个从 <c>SelfTest()</c> 里下来的 <c>static</c> 局部函数跟着走到这里）。
/// 这一族的外壳在 <c>src/SelfCheck.cs</c> —— 那是通用的那一圈（<c>Checks</c>）和外层登记；
/// 这一份是设置窗<b>自己那些判据</b>，两者不是一层。</summary>
public partial class Options : Window
{
    // ---- 自检 ----------------------------------------------------------------------

    // ---- 工单 03：设置窗这一族各组的绿话跟着判据住 --------------------------------
    //
    // 原来那些话写在 Probe.cs 的登记链上。判据改了（删掉一条断言、改掉一处形状），
    // 那句话不跟着改，而编译器管不着 —— 判据量的是当初那份名单。这里让它们跟着
    // 各自那一条判据住，见 Criterion 的类型注释。
    //
    // **组数不写在这里** —— 加一组 / 减一组都会让它过期，而它过期了没人看得见。
    // 数以 `--check` 那一跑印出来的为准（`CODING_STANDARDS.md`「写进文档的东西」那一节）。
    //
    // **`SelfTest` 那一组是纯算术**（自己实例化场景），**其余各组要一棵活节点树** ——
    // 那一层差别写在两个类型上，不是一个可选项。哪一组是哪一档，看下面那个字段的类型。

    internal static readonly CriterionOf Form = new(
        () => "设置窗通过：每一行对得上一条声明，每一条文字在语言表里；档位取值认得，" +
              "窗头域标签那一格在（不许被藏）、没有「显示高级设置」那颗开关（它一按就是在藏行）",
        "设置窗", SelfTest);

    internal static readonly CriterionOnHost Nav = new(
        () => "设置窗导航通过：窗头域标签在、搜索框排在最右一格（右上角）；" +
              "逐格点一遍，每一格名下每一行（含从声明表现取的一条高级键）都在屏幕上、该格的卡片都在；" +
              "搜中英文键名和标签都找得到（搜索压过域，别的格也搜得到）、搜空的分区连卡片一起收、" +
              "自带内容那几张留在场上、清空回到本格满窗",
        "设置窗导航", NavSelfTest);

    internal static readonly CriterionOnHost Screen = new(
        () => "设置窗文案通过：界面上没有配置文件内部的名字（键名、取值、文件名、那张表的键和字段名 —— " +
              "词表从声明表派生，不是手写的），也没有「毫秒」；时间格显示的是秒或分钟（换算回去正好是存着的毫秒）；" +
              "「生活模式」只出现在生活模式这一族自己的措辞里（名单从 LifeModeMenuLabels 派生），" +
              "休息流程那一族一处都不许带它",
        "设置窗文案", ScreenSelfTest);

    internal static readonly CriterionOnHost Io = new(
        () => "导出导入通过：文件框开在设置所在目录、注释和陌生键都跟着走、导入换掉了活设置并留下备份",
        "导出导入", IoSelfTest);

    internal static readonly CriterionOnHost Flows = new(
        () => "休息流程编辑器通过：界面上按出去的每一条都落了盘，" +
              "改一步不碰别步的说明，改名就地换标题（注释和位置都跟着那一行走），" +
              "删一条连它自己的注释一起走，读回来是同一份；" +
              "每步那个文字格：拧一个字文档里只有那一个字变（一个字都不改时一个字节都不动）",
        "休息流程编辑器", FlowsSelfTest);

    internal static readonly CriterionOnHost RoutineEditor = new(
        () => "作息表编辑器通过：界面上按出去的每一条都落了盘，" +
              "时刻是「几点 / 几分」两格合成的一个值、拧到最边写回去正好是声明里那个上限，" +
              "没碰的那一块一个字都不动，「到点走哪条休息流程」那个下拉摆的是每一个休息流程的名字，" +
              "上下挪只换位置不换值，空表那一句说得出来",
        "作息表编辑器", RoutineSelfTest);

    internal static readonly CriterionOnHost LifeModeTakeIn = new(
        () => "收生活模式通过：走菜单→文件框→问名字收进来，名单和磁盘那份对得上、" +
              "导出一个生活模式再收回来逐字节相同（注释、陌生键和那一张作息表都跟着走，来源那一份没被动）、" +
              "收进来的那一刻活的那一份一个字节没动且没多出备份而那颗钮还写着原来那个生活模式、" +
              "切过去之后域外那几条一个字节没动而那张作息表是收进来的那一张、" +
              "撞重名当场拦住并指向「盖回」（换个没撞的名字同一个调用就通）、" +
              "导出/导入的语义一个字没变",
        "收生活生活模式", LifeModeTakeInSelfTest);

    internal static readonly CriterionOnHost LifeModeUnreadable = new(
        () => "三档读不通通过：名单读不通时当成没有而说的是「名单」且磁盘那一份一个字节没动、" +
              "某一个生活模式那一份读不通时点名说是哪一个而别的照常能切、名字在而文件不在时照实说不补默认；" +
              "三档的话互不相同，每一档都配一条「一定该通」的对照",
        "三档读不通", LifeModeUnreadableSelfTest);

    // 名字用窗上那颗钮的字（`Options.cs` 里 `GetNode<Button>("Defaults")`），
    // 不叫 Defaults —— 那一族已经有一个同名的私有方法 `Defaults()` 了。
    internal static readonly CriterionOnHost RestoreDefaults = new(
        () => "恢复默认通过：按下去之后每一格显示的就是它自己的值，没有旧数一闪",
        "恢复默认", DefaultsSelfTest);

    internal static readonly CriterionOnHost LifeModeMenu = new(
        () => "生活模式菜单通过：起名那扇小窗开得起来、空名字当场不给而且说清为什么、" +
              "存完名单和磁盘都多一条、切过去之后窗上那几条和切之前一样、没保存的改动作废了而且说了一声",
        "生活生活模式菜单", LifeModeMenuSelfTest);

    /// <summary>场景和声明表对不对得上。跑在 <c>--check</c> 里。
    /// 少一行是一个设置改不到，多一行是一个没人读的控件，拼错是一个永远写不进文档的框。
    ///
    /// 场景形状那一批判据（1-10 和 12）在 <see cref="Audit"/> 里，运行期 <see cref="Bind"/>
    /// 收的是<b>同一份</b> —— 2026-09-29 之前这里和 Bind 各写一遍，两份各自漏，
    /// 于是「自检全绿」和「开窗不炸」是两件事。这里只剩下自检那一侧该管的：跟语言表对账（第 11 条）
    /// 和 flows 编辑器那一区的形状。</summary>
    public static List<string> SelfTest()
    {
        if (GD.Load("res://src/Options.tscn") is not PackedScene scene)
            return ["res://src/Options.tscn 加载不出来"];
        var root = scene.Instantiate();
        if (root == null) return ["Options.tscn 实例化失败"];

        var shape = Audit(root);
        // **审计自己那份问题表就是这一组的那一份** —— 所以这里的 `Checks` 用不着自己再攒一份：
        // 审计跑在前面，它交出来的那个列表对象直接当这一组的问题表（不是抄一份，是同一个对象）。
        var checks = new Checks(shape.Problems);
        var keys = shape.Keys;

        // 11. 每一条文字都得在语言表里 —— 这一条存在的原因是缺一条不报错，只是那一行露出键名。
        //     它不是场景形状，是场景和语言表两份的对账，所以只在自检那一侧，运行期不管。
        var known = Lang.Keys();
        foreach (var key in keys)
        {
            if (!known.Contains(key)) checks.Fail($"{key}：语言表里没有这一条标签");
            if (!Settings.Declared(key)) continue;
            var decl = Settings.Decl(key);
            if (decl.Kind == SettingKind.Int && !known.Contains(UnitKeyOf(decl)))
                checks.Fail($"{UnitKeyOf(decl)}：数字那一行要写单位，语言表里却没有");
            foreach (var choice in decl.Choices)
                if (!known.Contains(choice)) checks.Fail($"{choice}：{decl.Key} 的可选值，语言表里没有");
        }
        foreach (var section in SectionTitles)
            if (!known.Contains(section.Title)) checks.Fail($"{section.Title}：分区标题不在语言表里");
        foreach (var (_, title, _) in Domains)
            if (!known.Contains(title)) checks.Fail($"{title}：域标题不在语言表里");
        if (!known.Contains("搜索设置")) checks.Fail("搜索设置：搜索框的占位字不在语言表里");
        if (!known.Contains("显示高级设置")) checks.Fail("显示高级设置：窗头那颗开关的文案不在语言表里");
        checks.Problems.AddRange(ModeEditorShape(root, known));
        checks.Problems.AddRange(BlockEditorShape(root, known));
        checks.Problems.AddRange(DndEditorShape(root, known));
        checks.Problems.AddRange(ModeMenuShape(root, known));

        root.Free();
        return checks.Report();
    }

    /// <summary>一条整数键在语言表里要有的那条单位词。<b>时间键不给它自己的那一份</b> ——
    /// 2026-09-30 之前七条 <c>*Ms</c> 各有一条 <c>xxxMs.unit</c>，而显示单位是
    /// <see cref="Settings.Spell"/> 推出来的（≥1 分钟写分钟、不到写秒），那份逐键的词
    /// 既是会腐烂的第二份真相、也是「界面上出现毫秒」那条契约的漏口。统一成
    /// <c>unit.minute</c> / <c>unit.second</c> 两条。</summary>
    private static string UnitKeyOf(SettingDecl decl) =>
        decl.IsTime ? Settings.Spell(0).UnitKey : decl.Key + ".unit";

    /// <summary>界面上**每一个会显示的字**都不许带 <see cref="ForbiddenWords"/> 里那些词，
    /// 并且时间格显示的是人话单位（设计文档 `docs/design/config.md`「界面上不出现毫秒」/「界面上不许出现配置文件内部的
    /// 名字」那两条）。
    ///
    /// <b>必须在真窗上跑</b>：标签、提示、单位、分区标题、休息流程编辑器上的文字都是
    /// <see cref="Texts"/> 灌进去的，而它要一份活着的副本和一个装好的语言表 ——
    /// <see cref="SelfTest"/> 那一份（只 <c>Instantiate</c> 不 <c>AddChild</c>）里
    /// 每一颗标签都是空的，在那儿扫等于扫了一屏空气。所以这里自己开一扇窗。
    ///
    /// 窗和它那个临时目录归 <see cref="OptionsHost"/> 管：<b>「先删再建」和「跑完删掉」
    /// 两句都在那一类的注释里</b>（进程号会复用，所以上一发的残留必须先清掉，
    /// 否则「存一个生活模式」撞上残留名单里那一条，自检报的是残留而不是这一发量到的东西）。
    ///
    /// 扫的<b>范围</b>：Label / Button / CheckButton / CheckBox / LineEdit 的占位字 /
    /// OptionButton 和弹出菜单的每一项 / 窗头和窗底自己那几个字。扫的是**界面上有的字**，
    /// 不是键名（<c>fadeInMs</c> 那个键名当然含 <c>Ms</c>，它不进界面）。</summary>
    public static List<string> ScreenSelfTest(Node parent)
    {
        var checks = new Checks();
        // 窗和那个临时目录一起归 `OptionsHost`：先造目录（这一组要往里面写底稿），
        // 后开窗，收尾时两样一起没。
        var host = OptionsHost.InTempDir("screen");
        var dir = host.Dir;
        // **收尾只有一处**，在 `finally` 里。`OptionsHost.Dispose` 幂等，所以提前
        // return（那几处 `return Finish()` 走的就是同一条路）跑两遍也只放一次窗、删一次目录。
        // 提前 return 用 `return problems` 就行 —— 它交出去的就是**同一个对象**，
        // `finally` 里并进去的条目照样看得见。真正会漏的是 `Concat([...])` 那种造新列表的写法，
        // `OptionsHost.Dispose` 那段记着这条约束。
        void Finish() => checks.Problems.AddRange(host.Problems);
        try
        {
            // 底稿里**带一个时间块**（2026-10-02 复核补的）：块编辑器那些行是 `BlockRow`
            // 在 `_Ready` 里**按块现造**的，而这一份底稿原来一个块都没有 —— 于是那些行压根
            // 不存在，`Scan` 扫不到任何一行，「时间块那些字段没有内部名字」这句话就**空过**了。
            // 实测（改前红 / 改后绿，见工单 06 的 Comments）：把块字段名原样写进那一行的标签，
            // 自检**照样绿**；底稿里有了块之后，同一处当场红 7 条 × 两遍语言。
            // **和下面作息那一格、`Words` 那一行同一个道理**（`CODING_STANDARDS.md` 第 5 条）：
            // 量到的东西为零，那句话就什么都不是。字段名从声明表读，不手抄。
            var probe = Settings.BlockDeclarations
                .Select(d => $"\"{d.Key}\": {(d.Kind == SettingKind.Bool ? "true" : d.Kind == SettingKind.Int ? "60" : "\"x\"")}")
                .Aggregate("", (all, one) => all + (all.Length == 0 ? "" : ", ") + one);
            // 底稿里**也带一条勿扰时段和一个名单上的名字**（工单 03）：和上面那一个时间块
            // 同一个道理 —— 那两个编辑器是 `DndWindowRow` / `RefreshDndApps` 在 `_Ready` 里
            // **按行现造**的，底稿里没有行那些控件压根不存在，「勿扰那两个编辑器上没有内部名字」
            // 这句话就**空过**了。字段名和星期几都从声明表 / 那一族问，不手抄。
            var dndProbe = Settings.WindowDeclarations
                .Select(d => d.Kind == SettingKind.Int
                    ? $"\"{d.Key}\": {(d.Key == "startMinute" ? Routine.At(22, 30) : Routine.At(6, 30))}"
                    : $"\"{d.Key}\": [\"{Dnd.WeekdayKeys[2]}\"]")
                .Aggregate("", (all, one) => all + (all.Length == 0 ? "" : ", ") + one);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion},\n  \"flow\": \"fadetop\",\n" +
                $"  \"routine\": {{ \"enabled\": true, \"blocks\": [ {{ {probe} }} ] }},\n" +
                $"  \"dnd\": {{ \"enabled\": true, \"windows\": [ {{ {dndProbe} }} ], \"apps\": [\"chrome.exe\"] }}\n}}\n");
            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            // 先存一个生活模式，好让窗头那颗按钮展开的菜单里有东西可扫。**故意取一个撞上
            // 配置文件内部名字的名字**：用户自己起的词不是我们的措辞，那一档该放过 ——
            // 这一发不摆一条这样的生活模式，那一档就永远只是「代码里写了」，没人量过。
            var (seeded, _) = LifeModeStore.SaveAs(dir, "modes", state.Current);
            if (seeded != null) return [$"存一个生活模式没成：{seeded}"];
            if (Lang.Apply(state.Current.Language).Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];
            var win = host.Open(state, parent);

            var forbidden = ForbiddenWords();
            // 勿扰那两个编辑器在场景里的分区名：<b>从那一行反推</b>（行 → Body → 卡片 → Form），
            // 不手抄「Dnd」那两个字 —— 改名之后这里自动跟着走。
            var dndSection = "";
            foreach (var (key, _, node, _) in SectionedRows(win))
                if (key == Settings.DndEnabledKey) dndSection = node;
            // 「这一组量到了作息那一格」：那一区的文字得真的被扫到过。**对照是必需的** ——
            // Scan 走的是整棵树，场景里压根没有那一格时它一样是零条红，
            // 量到的东西为零，「新加的那一格没有内部名字」这句话就什么都不是。
            // 分区名从 Domains 那张表问出来（那一格归哪一格是表里的话）。
            var routineAt = DomainId(Settings.RoutineDomain);
            var routineSection = routineAt < 0
                ? "" : Domains[routineAt].Sections.FirstOrDefault() ?? "";
            var routineText = 0;
            // 「这一组量到了新加的那一行」：步编辑器里那颗布局钮得真的被扫到过。
            // 和上面作息那一格同一个道理 —— Scan 走的是整棵树，那一颗在场景里压根没有时
            // 它一样是零条红，而「那一行没有内部名字」就什么都不是。
            // **数那一行底下扫到的字**（不是数「扫描器跑过了」——那恒真）。
            // **2026-10-06 从 `Words` 那一行改成 `Show` 那一行**：`Words`（steps[].text）
            // 跟着 content 一起退休了，界面上改文字要去布局编辑器窗里拖文字控件。
            var wordsText = 0;
            // 时间块那些行底下扫到的字：同上，底稿里有了块之后它们才存在。少了这一条对照，
            // 「块字段没有内部名字」在块列表为空时是**空过**的（实测：改前照样绿）。
            var blockText = 0;
            // 勿扰那两个编辑器底下扫到的字：同上（底稿里有一条时段和一个名字）。
            var dndText = 0;
            // 用户自己的数据：休息流程的名字，和生活模式的名字。它们是**那一栏摆的东西本身**，
            // 不是我们的措辞 —— 一个用户管自己的生活模式叫「flows」是他的自由。
            var ownData = win._copy.Flows.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var entry in LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Entries) ownData.Add(entry.Name);
            // 工单 05 的画廊（<see cref="Presets"/>）：它挂在设置窗底下，所以 <c>Scan</c> 走整棵树
            // 就扫得到 —— **前提是它开着**。这一扇窗是**按那颗钮开出来的**（和真点它一下是同一条
            // 接线），所以这里也走那颗钮，不直接改字段。
            //
            // 它的列上写着**预设的名字和出处**，而那两者都是**数据**（名字是文件名去掉后缀，出处是
            // 文档顶上那段注释里的第一句 —— 见 <see cref="PresetLifeModes"/>）：既不是我们的措辞，
            // 也不是用户此刻敲的词。所以先摘成自己那份数据，剩下的半句才查内部名字
            // （同「休息流程的名字」那一档的处理）。
            var galleryText = 0;
            win.OpenPresetsGallery();
            if (win.Gallery == null)
                checks.Fail("按了管理器上那颗「预设生活模式…」，画廊没开起来 —— " +
                             "这一组量不到画廊上那些字（工单 05 那一扇）");
            else
                foreach (var preset in PresetLifeModes.Load().Presets)
                {
                    ownData.Add(preset.Name);
                    if (preset.Note.Length > 0) ownData.Add(preset.Note);
                }
            // 「生活模式」那一族的许用名单**在 <c>Scan</c> 里面取，不在外面取一次**：那一份要按
            // **当前语言**来（键是中文原文，en 那一栏从语言表取），而两遍之间 <c>Lang.Apply</c>
            // 把语言换过了 —— 2026-10-01 第一版在外面取了一次，英文那一遍拿中文那份名单去比，
            // 生活模式名那颗占位字当场被报成「把休息流程叫成 flow」。
            int Scan(string language, bool wholeList)
            {
                var allowed = LifeModeFamilyWording();   // 从 LifeModeMenuLabels 派生，不另抄一份
                var seen = 0;   // 这一门语言下量到的、认得出是生活模式这一族措辞的处数（对照要用）
                foreach (var node in Walk(win))
                {
                    foreach (var (what, text, chosenValue, isOwnData) in OnScreenText(node, ownData))
                    {
                        if (text.Length == 0) continue;
                        if (routineSection.Length > 0 &&
                            node.GetPath()?.ToString().Contains($"/{routineSection}/") == true)
                            routineText++;
                        if (node.GetPath()?.ToString().Contains("/Steps/") == true &&
                            node.GetPath()?.ToString().Contains("/Show/") == true) wordsText++;
                        // 时间块那些行：路径形如 `.../Form/Routine/Body/Blocks/.../Fields/<字段>/Tag`。
                        // **认 `Fields` 那一段而不是认某一个字段名** —— 认字段名就是又一份
                        // 手抄的清单，加一个字段名它就不认了（`CODING_STANDARDS.md` 第 6 条）。
                        if (node.GetPath()?.ToString().Contains("/Fields/") == true) blockText++;
                        // 勿扰那两个编辑器：认**它自己那个分区**（分区名从场景里那一行反推，
                        // 不手抄「Dnd」这两个字 —— 加一个分区名它就不认了）。
                        if (dndSection.Length > 0 &&
                            node.GetPath()?.ToString().Contains($"/{dndSection}/Body/") == true) dndText++;
                        // 画廊那一扇底下扫到的字：判据要量的是「画廊上那些字没有内部名字」，
                        // 而「扫过了零个字」和「那一扇窗上什么字都没有」是同一句话（同上）。
                        if (node.GetPath()?.ToString().Contains("/Presets/") == true) galleryText++;
                        foreach (var (word, cjk, value, distinctive) in forbidden)
                        {
                            // 英文只查有辨识度的那些名字（理由见 ForbiddenWords 的注释：
                            // 纯小写的那批在英文正文里就是普通词，整份查出来的红全是假的）。
                            if (!wholeList && !distinctive) continue;
                            // 下拉里那一项是**值该露面**的地方：枚举出来的取值放过，用户自己那份
                            // 数据（休息流程的名字）也放过 —— 它恰好和内置那三条的键同名，
                            // 但那是用户敲的词，不是我们的措辞。
                            if (chosenValue && (value || isOwnData)) continue;
                            if (!LeaksInternalName(text, word, cjk)) continue;
                            checks.Fail($"{language}：{Leaf(node)} 的{what}里有「{word}」：「{text}」" +
                                         (value
                                             ? "（这是配置文件里那个**取值**，下拉里摆着它是本分，"
                                             + "写在一句提示里就是漏 —— 那句话要用人话说）"
                                             : "（这是写给改配置的人看的词，不是给用这个东西的人看的；"
                                             + "词表是从声明表派生的 —— 要么把那句提示改成人话，"
                                             + "要么那确实是个该露的词）"));
                        }

                        // 界面上除了生活模式这一族，不许有第二处把休息流程叫「生活模式」。
                        foreach (var stolen in StolenForFlow(language).Where(w => StealsIt(text, w)))
                        {
                            // 用户自己起的名字是他的自由（同上面 ownData 那一档）：
                            // 一个用户管自己的生活模式或休息流程叫「生活模式大王」，那是他的词，不是我们的措辞。
                            if (isOwnData) continue;
                            if (allowed.Any(one => Wording(one, text))) { seen++; continue; }
                            checks.Fail($"{language}：{Leaf(node)} 的{what}里把休息流程叫成「{stolen}」：「{text}」" +
                                         "（「生活模式」从 2026-10-01 起专指配置快照，词汇表 GLOSSARY.md「休息流程」" +
                                         "那一条把它列为禁用词。生活模式这一族自己的措辞原样放过 —— " +
                                         "许用名单从 LifeModeMenuLabels 派生；这一族以外要改的是措辞本身，" +
                                         "不是把名单加长）");
                        }
                    }
                }

                // 对照，两头夹。这一条判据**不是**「界面上一个『生活模式』都不许有」——
                // 生活模式这一族合法地在用它自己那十来条，所以两门语言都得**量到**它们；
                // 一处都没量到，那这一组量的就不是屏幕上那些字（2026-09-29 那一族：
                // 实测结果压根没出来，而屏幕上只有那句结论）。
                // 反向对照（尺子自己能失败）：从名单里挑一句**不带占位符**的 —— 那种一句在屏幕上
                // 就是整句，它自己必须放过，而**后面再多缀一个那个词**必须报出来。
                // 用带占位符的那句试不出这个：{0} 吃几个字是定不下来的，那一句本来就该放过
                // 后面接字的（窗头那颗钮就是「生活模式：{0}」）。这一发不靠屏幕上有没有那一行：
                // 尺子坏了的话，上面那处计数会骗人。
                var probeWord = StolenForFlow(language)[0];
                var probe = allowed.FirstOrDefault(one =>
                    one.Length > 0 && !one.Contains('{') && StealsIt(one, probeWord));
                if (probe == null)
                    checks.Fail($"{language}：对照：生活模式这一族的措辞里找不到一句不带占位符的 —— " +
                                 "这一条判据量不到东西");
                else if (!Wording(probe, probe))
                    checks.Fail($"{language}：对照：名单里的话自己对不上自己，尺子坏了");
                else if (Wording(probe, probe + probeWord))
                    checks.Fail($"{language}：对照：名单里那句话后面再多带一个「{probeWord}」照样放过" +
                                 " —— 这一条判据不会失败");
                return seen;
            }

            // **两种语言各扫一遍**，而且**两门语言的名单不一样**：中文查全份，英文只查有
            // 辨识度的那些名字（理由和代价在 <see cref="ForbiddenWords"/>）。
            // 只扫中文的那半年里英文侧原样摆着 `marginXPercent` 和 `The widget`，自检全绿 ——
            // 中文正文里不夹英文词，那份名单在中文下只命中「真的漏出去的那几个」，
            // 而英文那一半**一次都没被看过**。判据要量的东西不因语言而不同，
            // 所以量法也不该只跑一种语言。
            var zhSeen = Scan("zh", wholeList: true);
            if (Lang.Apply("en").Count > 0) checks.Fail("语言表切到 en 失败，英文那一遍没量成");
            else
            {
                win.Texts();
                var enSeen = Scan("en", wholeList: false);
                if (zhSeen == 0) checks.Fail("对照：中文这一遍一个「生活模式」都没量到 —— 这一条判据量的不是屏幕上那些字");
                if (enSeen == 0) checks.Fail("对照：英文这一遍一个 flow 都没量到 —— 这一条判据量的不是屏幕上那些字");
                if (routineText == 0)
                    checks.Fail($"对照：作息那一区（{routineSection}）一处文字都没被量到 —— " +
                                 "这一组量不到新加的那一格，「它没有内部名字」什么都不是");
                if (wordsText == 0)
                    checks.Fail("对照：步编辑器里「这一步的屏」那一行一处字都没被量到 —— " +
                                 "这一组量不到布局那颗钮，「它没有内部名字」什么都不是");
                if (blockText == 0)
                    checks.Fail("对照：时间块那些行一处字都没被量到 —— 这一组量不到工单 01/02 " +
                                 "新加的那一族，「块字段没有内部名字」在块列表为空时是空过的");
                if (dndText == 0)
                    checks.Fail($"对照：勿扰那两个编辑器一处字都没被量到（分区 {dndSection}）—— " +
                                 "这一组量不到工单 03 新加的那一族，「它没有内部名字」什么都不是");
                if (galleryText == 0)
                    checks.Fail("对照：画廊那一扇一处字都没被量到 —— " +
                                 "这一组量不到工单 05 新加的那一扇，「它没有内部名字」什么都不是");

                // 数词和名词的搭配。**必须放在英文这一遍里**：中文的 `.one` 和本条是同一句话
                // （中文没有单复数），在中文下比「两条一样」会自己报自己。
                // 问的是**那条规则有没有被执行**，不是「这一格对不对」—— 后者换个值就绕过去了。
                // 抓帧量到的是英文那一面：「1 minutes」，以及丢了 {n} 的「Stretches」。
                //
                // 分成两族是因为**数住在哪儿不一样**，不是为了让判据好看：
                // 单位那两条，数是代码打在单位**前面**的（界面上是「3 seconds」），
                // 表里只有那个名词，所以它**不该**有 {n}；
                // 「共 N 步」那一条，数写在表**里面**（中文「共 {n} 步」、英文 "{n} steps"），
                // 所以它**必须**有 {n}，漏了屏幕上就是一个不带数目的名词。
                var known = Lang.Keys();
                void HasOne(string key, bool numberInside)
                {
                    var one = key + ".one";
                    if (!known.Contains(one))
                    {
                        checks.Fail($"en：{key}：语言表里没有 {one} —— 数是 1 的时候界面会印出复数那一条");
                        return;
                    }
                    if (TranslationServer.Translate(one) == TranslationServer.Translate(key))
                        checks.Fail($"en：{one}：和 {key} 翻成了同一句话 —— 数是 1 和数是 2 长得一样");
                    if (numberInside && !TranslationServer.Translate(key).ToString().Contains("{n}"))
                        checks.Fail($"en：{key}：漏了 {{n}} —— 界面上会印出一个不带数目的名词");
                    if (!numberInside && TranslationServer.Translate(key).ToString().Contains("{n}"))
                        checks.Fail($"en：{key}：带了 {{n}}，可数是代码打在单位前面的 —— 表里再放一个数会印出两个");
                }
                HasOne("unit.minute", numberInside: false);
                HasOne("unit.second", numberInside: false);
                HasOne("共 N 步", numberInside: true);
            }
            if (Lang.Apply(state.Current.Language).Count == 0) win.Texts();   // 摆回原样，别留脏状态
            else checks.Fail("语言表摆不回去，这一组跑完桌面上留着 en");

            // 时间格：界面上摆的数 × 那一档 == 存着的毫秒（Spell 说该摆几就摆几），
            // 而且那个单位词只有两种。判据问的是「存着的那个毫秒有没有原样出现在界面上」，
            // 换算那一步走 <see cref="Settings.Spell"/> —— 那是它唯一的一处口径。
            foreach (var row in win._rows.Where(r => r.Decl.IsTime && r.Value is SpinBox))
            {
                var box = (SpinBox)row.Value;
                var stored = (int)win._copy.Map.Get(row.Key);
                var spelled = Settings.Spell(stored);
                if (box.Value != spelled.Number)
                    checks.Fail($"{row.Key}：格子上是 {box.Value}、存的是 {stored} 毫秒，" +
                                 $"按 Spell 该摆 {spelled.Number}");
                if (row.Unit != null && row.Unit.Text != TrCounted(spelled.UnitKey, spelled.Number))
                    checks.Fail($"{row.Key}：单位词是「{row.Unit.Text}」，该是「{TrCounted(spelled.UnitKey, spelled.Number)}」");
            }

            // 勿扰那两个编辑器**灌对了没有**（工单 03）：界面上摆的是「几点 / 几分」和勾对了的
            // 星期几，而副本里存的是一天里的第几分钟和一列词 —— 两个是同一个值的两种样子。
            // 承重处是**同一份算术**：<see cref="Routine.HourOf"/> / <see cref="Routine.MinuteOf"/>
            // 走的是 <see cref="Routine"/> 那一个 60，勾选框走的是 <see cref="Dnd.WeekdayKeys"/> 那一份词。
            foreach (var (row, window, index) in DndWindowRows(win))
            {
                foreach (var decl in Settings.WindowDeclarations.Where(d => d.Kind == SettingKind.Int))
                {
                    var field = row.GetNode<HBoxContainer>(WindowField(decl));
                    var hour = field.GetNode<SpinBox>("Hour");
                    var minute = field.GetNode<SpinBox>("Minute");
                    var stored = (int)window.Get(decl.Key);
                    if (hour.Value != Routine.HourOf(stored) || minute.Value != Routine.MinuteOf(stored))
                        checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：界面上摆的是 " +
                                     $"{Routine.Clock(Routine.At((int)hour.Value, (int)minute.Value))}，" +
                                     $"存的是 {Routine.Clock(stored)} —— 那一格要摆时刻，不是一天里的第几分钟");
                }
                var days = row.GetNode<HBoxContainer>(WindowField(Settings.Decl(DndWindow.KeyPrefix + "weekdays"))
                        + "/Edit");
                var chosen = (List<string>)window.Get("weekdays");
                foreach (var at in Enumerable.Range(0, Dnd.WeekdayKeys.Length))
                    if (days.GetNode<CheckBox>(Dnd.WeekdayKeys[at]).ButtonPressed != chosen.Contains(Dnd.WeekdayKeys[at]))
                        checks.Fail($"第 {index + 1} 条时段：星期几那一格的「{Dnd.WeekdayText(at)}」" +
                                     $"勾错（存的是 [{string.Join(" / ", chosen)}]）");
            }
            // 名单那几行：一个 exe 名一行，原样显示（用户自己写的数据，不翻）。
            foreach (var (edit, name) in DndAppRows(win).Zip(win._copy.DndApps))
                if (edit.Text != name)
                    checks.Fail($"名单那一行显示的是「{edit.Text}」，副本里是「{name}」");
            // 「当前前台」那一条实测结果：问不到那一档照实说，不印一个空字符串（同一门语言里量一次）。
            if (win._dndForeground.Text.Trim().Length == 0)
                checks.Fail("「当前前台」那一条实测结果是空的 —— 问不到也要说出口");


            // 出厂那几处必须<b>往返无损</b>：Spell 是取整的（90000 毫秒写 2 分钟），
            // 所以要问一句「出厂值有没有被取整吃掉」。这一条量的是「出厂那几处时间键和出厂休息流程的每一步
            // 全是整秒」—— 它哪天不成立了，当场在这里红。
            foreach (var decl in Settings.Declarations.Where(d => d.IsTime && d.Kind == SettingKind.Int))
                if (decl.Default is int ms && !Settings.RoundTrips(ms))
                    checks.Fail($"{decl.Key}：出厂值 {ms} 毫秒经 Spell 取整成 {Settings.Spell(ms).Number}" +
                                 $"{TrCounted(Settings.Spell(ms).UnitKey, Settings.Spell(ms).Number)}，写回去就不是原来那个数了");
            foreach (var (name, flow) in Settings.BuiltInFlows())
                foreach (var step in flow.Steps)
                    if (!Settings.RoundTrips(step.Ms))
                        checks.Fail($"出厂休息流程「{name}」有一步 {step.Ms} 毫秒经 Spell 取整，" +
                                     "写回去就不是原来那个数了");
            return checks.Report();
        }
        catch (Exception e)
        {
            // 追加而不是 `problems.Concat([...])` —— 收尾（`finally`）要把
            // `OptionsHost.Problems` 并进**这一个对象**，而 Concat 造的是新列表，
            // 那一份就漏了。`OptionsHost.Dispose` 那段记着这条约束。
            checks.Fail($"屏幕上那些字这一组抛了：{e.GetType().Name} {e.Message}");
            return checks.Report();
        }
        finally
        {
            host.Dispose();
            Finish();
        }
    }

    /// <summary>一颗节点上<b>会显示给人看</b>的那些字。扫文案只认这一份清单 ——
    /// 「界面上不许出现内部名字」那条判据要是自己去找 Text 属性，就得逐个控件类型写一遍，
    /// 而漏掉的那一种恰好就是新加的那种控件。
    ///
    /// <b>次序要紧</b>：<c>CheckBox</c> / <c>CheckButton</c> / <c>OptionButton</c> 都是
    /// <c>Button</c> 的子类，所以它们必须排在 <c>Button</c> 前面 —— 排在后面的话编译器会把
    /// 那一支判成「不可达」（CS8510），而更要紧的是：<c>Button</c> 那一臂会把它们整个吃掉，
    /// 于是「挥得散」「接鼠标」那七颗复选框上的字、以及下拉里那几项，<b>一次都没被扫过</b>
    /// —— 判据绿着，而它量的是一屏空气。
    ///
    /// 第三个返回值 <c>ChosenValue</c> 说的是「这几个字是**某一个被选中的值**，不是话」：
    /// 只有下拉里那一项是。那是值该露面的地方（<c>flow</c> 那个下拉里摆的就是休息流程的名字，
    /// 那是数据），别的地方是句子，句子里出现配置里的词就是漏。
    /// 判据按这一位分两档查，见 <see cref="ForbiddenWords"/>。
    ///
    /// <paramref name="ownData"/> 是<b>用户自己那份数据</b>的名字集合（现在只有休息流程的名字）。
    /// 它跟「值」不是一回事：值是**声明表枚举出来的**几个（改一个键就多一个），
    /// 而休息流程的名字是用户敲的、能改能删能新建 —— 内置那三条（<c>fadetop</c> /
    /// <c>todoList</c> / <c>pomodoro</c>）恰好和它们在配置里的键同名，但那不把它变成一个
    /// 内部名字：用户新建一条叫「喝水」，它照样得摆在那儿。所以这一项两档都放过。</summary>
    private static IEnumerable<(string What, string Text, bool ChosenValue, bool OwnData)>
        OnScreenText(Node node, IReadOnlySet<string>? ownData = null) => node switch
    {
        CheckBox box => [("文字", box.Text, false, false)],
        CheckButton check => [("文字", check.Text, false, false)],
        OptionButton option => option.ItemCount <= 0
            ? []
            : Enumerable.Range(0, option.ItemCount).Select(i =>
            {
                var text = option.GetItemText(i);
                return ("下拉里那一项", text, true, ownData?.Contains(text) == true);
            }),
        // 弹出菜单的每一项。2026-10-01 加的：窗头那颗生活模式按钮展开的那一个菜单是界面上
        // **最长出来的一处文字**，而它原来不在扫的范围内 —— 菜单里原样写着一个配置文件内部的
        // 名字，自检会绿着。「我们自己的措辞」和「用户自己起的名字」分开判：后者是
        // ownData 那一档（同休息流程的名字）。
        PopupMenu menu => menu.ItemCount <= 0
            ? []
            : Enumerable.Range(0, menu.ItemCount)
                .Where(i => !menu.IsItemSeparator(i))
                .Select(i =>
                {
                    var text = menu.GetItemText(i);
                    return ("菜单里那一项", text, true, ownData?.Contains(text) == true);
                }),
        // 窗头那颗「生活模式：…」按钮。2026-10-01 补上：Godot 4 里 <c>MenuButton</c> 直接继承
        // <c>BaseButton</c>、<b>不是 <c>Button</c> 的子类</b>，所以下面那一支
        // <c>Button button</c> 压根接不到它 —— 而它上面是界面上最长的一处文字
        // （「生活模式：写作」／「还没有生活模式」／「不在任何生活模式里」）。比的是**我们自己的措辞**：
        // 用户自己起的生活模式名（ownData 那一档）先摘掉，剩下的才查内部名字。
        MenuButton mode => [("文字", WithoutOwnData(mode.Text, ownData), false, false)],
        Button button => [("文字", button.Text, false, false)],
        Label label => [("文字", label.Text, false, false)],
        LineEdit edit => [("占位字", edit.PlaceholderText, false, false)],
        RichTextLabel rich => [("文字", rich.Text, false, false)],
        _ => [],
    };

    /// 一句话里去掉**用户自己那份数据**的名字（休息流程的名字、生活模式的名字）。
    ///
    /// 窗头那颗钮写的是「生活模式：写作」：前半句是我们的措辞，后半句是用户敲的词。整句当成
    /// 措辞查，用户管自己的生活模式叫 <c>flows</c> 就会平白报一条红；当成数据放过，我们自己
    /// 写错的那半句就永远查不出来。所以摘掉名字再查剩下的那半句 ——
    /// 两档各判各的，和 <see cref="OnScreenText"/> 里 <c>ownData</c> 那一族是同一个道理。</summary>
    private static string WithoutOwnData(string text, IReadOnlySet<string>? ownData)
    {
        if (ownData == null || ownData.Count == 0) return text;
        var left = text;
        foreach (var name in ownData.Where(n => n.Length > 0))
            left = left.Replace(name, "", StringComparison.Ordinal);
        return left;
    }

    /// <summary>flows 那一区的形状：<b>step 的每条声明都要有它的控件</b>，摆什么的格子由
    /// 声明的可选值数出来。少一个控件就是那一个字段在界面上改不到 —— 而这张表的字段
    /// 不挂在 <c>metadata/setting</c> 上（一张表里的行不是「一条声明一个控件」那个形状），
    /// 所以上面那条按声明数行的判据管不到它。</summary>
    private static List<string> ModeEditorShape(Node root, HashSet<string> known)
    {
        var checks = new Checks();
        foreach (var path in new[]
        {
            "Pick/Which", "Pick/Name", "Pick/NewBtn", "Pick/DelBtn", "Steps/Template",
            "Steps/Template/Head/Which", "Steps/Template/Head/Ms", "Steps/Template/Head/Unit",
            "Steps/Template/Head/Up", "Steps/Template/Head/Down", "Steps/Template/Head/Del",
            // 2026-10-06：「摆什么」那排勾选框（content）和「文字」那一行输入格（text）
            // 退休了 —— 内容成了布局里的控件，要有它们得去布局编辑器窗里拖。这一排
            // 换成了那颗「摆这一屏…」的钮。原来的 `Show/Choices/Template` 和
            // `Words/Tag`、`Words/Edit` 三条从这一份里拿掉，不是漏了，是那一族没了。
            "Steps/Template/Show/Label", "Steps/Template/Show/Lay",
            "AddStep/Btn", "AddStep/Total",
        })
            if (root.GetNodeOrNull($"{FlowsPath}/{path}") == null)
                checks.Fail($"flows 编辑器：场景里少了 {FlowsPath}/{path}");

        // **退休的那两个成员不许再有控件** —— 这一条是反向的。
        // `content` 和 `text` 还在声明表里（老配置要读得进来、还要翻得出布局），
        // 但它们不再是权威，界面上留着改它们的控件就是「界面上能改、存下去不生效」。
        // 留着声明是给**读**的那一份用的，不是给**改**的那一份用的。
        foreach (var retired in new[] { "Show/Choices", "Words" })
            if (root.GetNodeOrNull($"{FlowsPath}/Steps/Template/{retired}") is CanvasItem { Visible: true })
                checks.Fail($"flows 编辑器：{retired} 那一片还露着 —— content 和 text 2026-10-06 退休，"
                             + "界面上不该再有改它们的控件");

        foreach (var decl in Settings.StepDeclarations)
        {
            // 声明表里 step 的键是裸的（"ms"、"layout"），带前缀的那一份在 ByKey 索引里。
            var field = decl.Key;
            var wants = decl.Kind switch
            {
                SettingKind.Int => "Head/Ms",
                SettingKind.Bool => $"Flags/{char.ToUpperInvariant(field[0]) + field[1..]}",
                // 布局不在这一排里改：它有自己的窗（程序第五扇窗），这一排只放
                // 一颗「摆这一屏…」的钮把它叫起来。
                SettingKind.LayoutList => "Show/Lay",
                // **`content` / `text` 走到这里 = 界面上还有改它们的控件**，判据在上面
                // 那两条已经查过了；这里只把它们的声明放过，不再要求控件。
                SettingKind.ChoiceList or SettingKind.Text when
                    field is "content" or "text" => null,
                _ => null,
            };
            if (wants == null)
            {
                // 退休的两个成员不报「没有这种控件」—— 它们**要**的就是没有控件。
                if (field is not ("content" or "text"))
                    checks.Fail($"steps[].{field}：种类是 {decl.Kind}，编辑器里没有这种控件");
                continue;
            }
            var node = root.GetNodeOrNull($"{FlowsPath}/Steps/Template/{wants}");
            if (node == null)
                checks.Fail($"steps[].{field}：声明里有这个字段，编辑器里没有它的控件（{wants}）");
            else if (decl.Kind == SettingKind.Bool && node is not CheckBox)
                checks.Fail($"steps[].{field}：该是 CheckBox，场景里是 {node.GetType().Name}");
            else if (decl.Kind == SettingKind.Int && node is not SpinBox)
                checks.Fail($"steps[].{field}：该是 SpinBox，场景里是 {node.GetType().Name}");
            else if (decl.Kind == SettingKind.LayoutList && node is not Button)
                checks.Fail($"steps[].{field}：该是一颗叫起编辑器窗的 Button，场景里是 {node.GetType().Name}");
            // 范围那一条不在这里了：它现在是 Audit 的第 12 条，走整棵场景树（21 颗数值控件），
            // 留在这里就是同一颗查两遍 —— 而且只有这一颗。
        }

        foreach (var choice in Settings.Decl(StepLayout.KeyPrefix + "kind").Choices)
            if (!known.Contains(choice))
                checks.Fail($"{choice}：布局 kind 的一个可选值，语言表里没有");
        foreach (var label in EditorLabels)
            if (!known.Contains(label))
                checks.Fail($"{label}：flows 编辑器上的文字，语言表里没有");
        return checks.Report();
    }

    /// <summary>作息表那一区的形状：<b>块行的每一条声明都要有它那一行控件</b>，而那一行里
    /// 放什么控件由<b>声明的种类</b>说。少一个控件就是那一个字段在界面上改不到 ——
    /// 而块行的字段不挂在 <c>metadata/setting</c> 上（表里的一行不是「一条声明一个控件」
    /// 那个形状），所以 <see cref="Audit"/> 那条按声明数行的判据管不到它。
    ///
    /// <b>行名从声明来</b>（<c>Fields/&lt;字段名&gt;</c>），标签那一栏在语言表里的键也是
    /// 同一个前缀（<see cref="TimeBlock.KeyPrefix"/>）—— 两边都从
    /// <see cref="Settings.BlockDeclarations"/> 推，所以加一个字段不会被这里漏掉，
    /// 也不用在这里抄一份字段清单。</summary>
    private static List<string> BlockEditorShape(Node root, HashSet<string> known)
    {
        var checks = new Checks();
        foreach (var path in new[]
        {
            "Title", "routine.enabled", "Hint", "Blocks/Template", "Blocks/Template/Fields",
            "Blocks/Template/Head/Which", "Blocks/Template/Head/Up",
            "Blocks/Template/Head/Down", "Blocks/Template/Head/Del",
            "AddBlock/Btn", "AddBlock/Total",
        })
            if (root.GetNodeOrNull($"{RoutinePath}/{path}") == null)
                checks.Fail($"作息表编辑器：场景里少了 {RoutinePath}/{path}");

        foreach (var decl in Settings.BlockDeclarations)
        {
            var where = $"{RoutinePath}/Blocks/Template/{BlockField(decl)}";
            if (root.GetNodeOrNull<HBoxContainer>(where) == null)
            {
                checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：声明里有这个字段，编辑器里没有它那一行（{where}）");
                continue;
            }
            if (root.GetNodeOrNull($"{where}/Tag") == null)
                checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：那一行没有 Tag 那一颗标签（字段叫什么）");
            switch (decl.Kind)
            {
                // 时刻是两个数格（几点 / 几分）而不是一个，理由见 BuildBlockEditor 上面
                // 那一段：一天里的第几分钟是内部表示，摆出来要人做除法。
                case SettingKind.Int:
                    if (root.GetNodeOrNull<SpinBox>($"{where}/Hour") == null)
                        checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：时刻要「几点」那一格，场景里没有");
                    if (root.GetNodeOrNull<SpinBox>($"{where}/Minute") == null)
                        checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：时刻要「几分」那一格，场景里没有");
                    if (root.GetNodeOrNull($"{where}/HourUnit") == null ||
                        root.GetNodeOrNull($"{where}/MinuteUnit") == null)
                        checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：那两个数格旁边各要一颗单位词（" +
                                     "界面上只有「时刻」和「分钟」两种单位）");
                    break;
                case SettingKind.Bool:
                    if (root.GetNodeOrNull($"{where}/Edit") is not CheckBox)
                        checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：该是 CheckBox，场景里不是");
                    break;
                default:
                    // 自由文本或者指向休息流程的名字，两种控件都合法：场景放什么就是什么，
                    // 代码按控件分两支（见 BlockText）。下拉那一支摆不摆得出休息流程的名字
                    // 是**真窗上的实测结果**（RoutineSelfTest），这一条只管形状。
                    if (root.GetNodeOrNull($"{where}/Edit") is not (LineEdit or OptionButton))
                        checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：该是 LineEdit 或 OptionButton，场景里不是");
                    break;
            }
            if (!known.Contains(TimeBlock.KeyPrefix + decl.Key))
                checks.Fail($"{TimeBlock.KeyPrefix}{decl.Key}：编辑器上那一行的标签，语言表里没有");
        }

        foreach (var label in BlockEditorLabels)
            if (!known.Contains(label))
                checks.Fail($"{label}：作息表编辑器上的一条文字，语言表里没有");
        return checks.Report();
    }

    /// <summary>勿扰那一区的形状：<b>时段行的每一条声明都要有它那一行控件</b>，名单那几行
    /// 的两个控件在，「当前前台」那一条实测结果在。
    ///
    /// 和 <see cref="BlockEditorShape"/> 同一处置、同一理由：表里的一行不是「一条声明一个控件」
    /// 那个形状（字段不挂在 <c>metadata/setting</c> 上），所以 <see cref="Audit"/> 按声明数行的
    /// 那条判据管不到它，得在这里对着声明核。行名从声明来（<c>Fields/&lt;字段名&gt;</c>），
    /// 标签在语言表里的键也是同一个前缀（<see cref="DndWindow.KeyPrefix"/>）。
    ///
    /// <b>星期几那七个勾选框是现造的</b>（<see cref="DndDays"/>），所以场景里只有一颗
    /// <c>Template</c> —— 这一条量的是「那颗模板在、七格会不会被造出来」（后者在真窗上量），
    /// 而<b>求值那一份和界面上那七个</b>得是同一份词：判据对着
    /// <see cref="Dnd.WeekdayKeys"/> 核，不抄一份七个词。
    private static List<string> DndEditorShape(Node root, HashSet<string> known)
    {
        var checks = new Checks();
        foreach (var path in new[]
        {
            "Title", "dnd.enabled", "WindowsHint", "Windows/Template", "Windows/Template/Fields",
            "Windows/Template/Head/Which", "Windows/Template/Head/Up",
            "Windows/Template/Head/Down", "Windows/Template/Head/Del",
            "AddWindow/Btn", "AddWindow/Total",
            "AppsHint", "Rows/Template", "Rows/Template/Edit", "Rows/Template/Del",
            "AddApp/Text", "AddApp/Btn", "AddApp/Total", "Foreground",
        })
            if (root.GetNodeOrNull($"{DndPath}/{path}") == null)
                checks.Fail($"勿扰编辑器：场景里少了 {DndPath}/{path}");

        foreach (var decl in Settings.WindowDeclarations)
        {
            var where = $"{DndPath}/Windows/Template/{WindowField(decl)}";
            if (root.GetNodeOrNull<HBoxContainer>(where) == null)
            {
                checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：声明里有这个字段，编辑器里没有它那一行（{where}）");
                continue;
            }
            if (root.GetNodeOrNull($"{where}/Tag") == null)
                checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：那一行没有 Tag 那一颗标签（字段叫什么）");
            switch (decl.Kind)
            {
                // 时刻是两个数格（几点 / 几分）而不是一个：一天里的第几分钟是内部表示，
                // 摆出来要人做除法（理由和 BlockClock 上面那一段同一个）。
                case SettingKind.Int:
                    if (root.GetNodeOrNull<SpinBox>($"{where}/Hour") == null)
                        checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：时刻要「几点」那一格，场景里没有");
                    if (root.GetNodeOrNull<SpinBox>($"{where}/Minute") == null)
                        checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：时刻要「几分」那一格，场景里没有");
                    if (root.GetNodeOrNull($"{where}/HourUnit") == null ||
                        root.GetNodeOrNull($"{where}/MinuteUnit") == null)
                        checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：那两个数格旁边各要一颗单位词（" +
                                     "这一族在屏幕上只有「时刻」和「分钟」两种单位）");
                    break;
                default:
                    // 星期几那一格 = 七颗勾选框 + 一颗模板。它们是**按声明的可选值现造**的，
                    // 所以这里只问「那颗模板在不在」；七格摆得对不对是**真窗上的实测结果**。
                    if (root.GetNodeOrNull($"{where}/Edit/Template") is not CheckBox)
                        checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：星期几要一颗 CheckBox 模板，场景里没有");
                    break;
            }
            if (!known.Contains(DndWindow.KeyPrefix + decl.Key))
                checks.Fail($"{DndWindow.KeyPrefix}{decl.Key}：编辑器上那一行的标签，语言表里没有");
        }

        // 那七个勾选框上的字走语言表里**已经有的**那七条星期（周一…周日）——
        // 「一个概念一个词」：这里不许另起一套 `dnd.weekday.mon` 那种键。键名问日历那一族
        // （<see cref="Calendar.WeekdayKey"/> 是那七条的唯一出处），不抄一份七个词。
        foreach (var at in Enumerable.Range(0, Dnd.WeekdayKeys.Length))
            if (!known.Contains(Calendar.WeekdayKey(at, mondayFirst: true)))
                checks.Fail($"星期几那一格上的「{Dnd.WeekdayKeys[at]}」在语言表里找不到" +
                             $"（那一格该走 {Calendar.WeekdayKey(at, mondayFirst: true)} —— 周一…周日那七条，不另起一套）");

        foreach (var label in DndEditorLabels)
            if (!known.Contains(label))
                checks.Fail($"{label}：勿扰编辑器上的一条文字，语言表里没有");
        foreach (var key in new[] { "dnd.why.window", "dnd.why.app", "勿扰中" })
            if (!known.Contains(key))
                checks.Fail($"{key}：托盘上「勿扰中」那一行（或者它的原因）语言表里没有");
        return checks.Report();
    }

    /// <summary>生活模式那一族在场景里的形状：窗头那颗按钮、它展开的菜单（引擎挂在
    /// <see cref="MenuButton"/> 自己身上，所以场景里没有它）、起名那扇小窗，和它上面那几个
    /// 控件。少一个的场合各不相同：少了按钮，那一族在界面上一点都够不着；少了起名那扇小窗，
    /// 「存为新生活模式」按下去没有任何反应。
    ///
    /// <b>「像不像一个控件」也在这儿判一条</b>：<c>flat = true</c> 的按钮没有边、没有底，
    /// 缩在窗头那一行里看上去就是一行漏排版的文字 —— 那是 <c>CODING_STANDARDS.md</c> 第 9 条
    /// 点名的那个坑（一版功能全对、自检全绿，而它是 <c>flat = true</c>），它不在任何别的判据里。</summary>
    private static List<string> ModeMenuShape(Node root, HashSet<string> known)
    {
        var checks = new Checks();
        if (root.GetNodeOrNull<MenuButton>(LifeModePath) is not { } button)
        {
            checks.Fail($"{LifeModePath}：窗头没有那颗「生活模式：…」按钮（它要和休息提醒 / 桌面日历 / " +
                         "系统那三格平级，因为生活模式管的是全部配置）");
        }
        else if (button.Flat)
            checks.Fail($"{LifeModePath}：flat = true 的按钮没有边也没有底，缩在窗头那一行里" +
                         "看上去是一行漏排版的文字，不是一个控件");

        // 画廊的入口（设计文档 `docs/design/preset-life-mode.md`「画廊的入口在生活模式管理器和设置窗」那条）。
        //
        // **2026-10-08 合并**：设置窗窗头原先有一颗只管开画廊的「预设生活模式…」常驻钮，
        // 而「预设生活模式…」**本来就已经在窗头那颗生活模式按钮的菜单下半**
        //（`RefreshLifeModeMenu` 的 `PresetsId`）—— 两颗并排摆着，同一个动作两个入口。
        // 所有者定合并成一颗：删掉窗头那颗，入口是菜单里那一条。
        //
        // **所以这一条判据反过来了**：原先问「那颗常驻钮在不在」（还问「像不像一个控件」，
        // `flat = true` 那个坑就是从这儿抓到的），现在问
        // 「那颗常驻钮**已经不在**了，而菜单里那一条还在」。
        // 反向验过：把 `Options.tscn` 里那个节点加回去，这一发当次就红。
        if (root.GetNodeOrNull<Button>(PresetsPath) != null)
            checks.Fail($"{PresetsPath}：窗头那颗常驻的「预设生活模式…」按钮还在 —— " +
                        "它和菜单里那一条是同一个动作的两处入口，2026-10-08 合并掉了它");

        var prompt = root.GetNodeOrNull<Window>(NamePath);
        if (prompt == null) checks.Fail($"{NamePath}：起名那扇小窗不在场景里，「存为新生活模式」按下去没有反应");
        else
            foreach (var node in new[] { "Margin/Stack/Err", "Margin/Stack/Text", "Margin/Stack/Buttons/Ok" })
                if (prompt.GetNodeOrNull(node) == null)
                    checks.Fail($"{NamePath}/{node}：起名那扇小窗上少了这个控件");

        // 画廊那一扇窗自己的形状：少一个控件就是那一列收不进来，而**那一列收不进来时屏幕上
        // 没有任何症状**（一颗钮不画 = 少一列，而「少一列」和「就这么多」长得一样）。
        if (GD.Load(Presets.ScenePath) is not PackedScene gallery) checks.Fail($"{Presets.ScenePath}：加载不出来");
        else if (gallery.Instantiate() is { } galleryRoot)
        {
            foreach (var path in new[]
                     {
                         "Page", "Margin/Stack/Title", "Margin/Stack/Status", "Margin/Stack/Err",
                         "Margin/Stack/List", "Margin/Stack/List/Template",
                         "Margin/Stack/List/Template/Head/Name", "Margin/Stack/List/Template/Head/Take",
                         "Margin/Stack/List/Template/Note", "Margin/Stack/Buttons/Close",
                     })
                if (galleryRoot.GetNodeOrNull(path) == null)
                    checks.Fail($"{Presets.ScenePath}：{path}：画廊的场景里少了这个节点");
            if (galleryRoot.GetNodeOrNull("Margin/Stack/List/Template") is CanvasItem { Visible: true })
                checks.Fail($"{Presets.ScenePath}：List/Template 露着 —— 那是模板，不是第一列");
            // 屏幕上那几句字得在语言表里（和下面 LifeModeMenuLabels 那一条是同一件事的另一半：
            // 名单是「这一族自己写的措辞」，这里管的是场景里摆着的每一句）。
            foreach (var (path, key) in new[]
                     {
                         ("Margin/Stack/Title", "预设生活模式"),
                         ("Margin/Stack/List/Template/Head/Take", "收进来"),
                         ("Margin/Stack/Buttons/Close", "关掉"),
                     })
                if (!known.Contains(key)) checks.Fail($"{key}：画廊上的那一处文字，语言表里没有");
            galleryRoot.Free();
        }

        foreach (var label in LifeModeMenuLabels)
            if (!known.Contains(label)) checks.Fail($"{label}：生活模式那一族的一条文字，语言表里没有");
        return checks.Report();
    }

    /// 生活模式这一族界面上那些<b>我们自己</b>的措辞。生活模式名和休息流程的名字不在这里 ——
    /// 那是用户的数据，不翻也不查（自检里那两条「不许出现」的判据把它们的用户数据放过，
    /// 同 <see cref="OnScreenText"/> 的 <c>ownData</c> 那一档）。
    ///
    /// 它同时是<b>界面上许用「生活模式」那一份名单的唯一起点</b>（<see cref="LifeModeFamilyWording"/>）：
    /// 「界面上除了生活模式这一族，不再有第二处把休息流程叫生活模式」那条判据从这儿派生，
    /// **不另抄一份**。这一族是 02/03/05 一点一点长起来的（那几张把它们收在这一处，
    /// 连 05 的「收生活模式」那几条也在里面），所以这里是唯一的落点。
    internal static readonly string[] LifeModeMenuLabels =
    [
        "生活模式：{0}", "还没有生活模式", "不在任何生活模式里", "切换", "保存并切换",
        "存为新生活模式", "盖回一个生活模式…", "删掉这一条",
        "给这个生活模式起个名", "盖回哪个生活模式", "删掉这个生活模式", "生活模式名",
        "确定", "盖回去", "删掉", "取消",
        "已存为新生活模式", "已盖回生活模式", "已切到生活模式", "已保存并切到生活模式",
        "切换没成", "删掉没成", "已删掉生活模式", "已从名单里删掉",
        "保存没成，所以没有切", "窗里没保存的改动没有算进去",
        "要盖回去就按「盖回一个生活模式…」",
        "删掉以后它就不在了，它的编号不会拿去给下一个新生活模式用", "你不在任何生活模式里，没有可删的",
        // 「读不通有三档」那三条 + 「压根没有那一个」那一条。**四句不许合并成一句**：
        // 名单坏掉、某一个生活模式坏掉、名单里有而文件不在，是三件事（LifeModeStore.Stop）。
        "生活模式名单读不通",
        "生活模式名单读不通，一个生活模式也没认出来；磁盘上那一份没有动过",
        "生活模式「{0}」读不通，所以没有切",
        "名单里有「{0}」，可它那一份已经不在了 —— 没有替它补上一个",
        "名单里没有「{0}」，所以没有切",
        // 「从一份文件收一个生活模式进来」那几条（工单 05）。收进来的那一档**没有预填**
        // （AskLifeModeName 那一支），所以这一族不共用「盖回哪个生活模式」那一句。
        "从一份文件收一个生活模式进来…", "收进来",
        "已收下生活模式", "你现在这份配置没动过", "还没有挑要收进来的那一份文件",
        // 「预设生活模式」那几条（工单 05 的画廊）。**「预设」两个字只活在这个组合词里**
        // （GLOSSARY.md「预设生活模式」那一条），而这一族在界面上的每一句话都得带全那五个字 ——
        // 单说「预设」不是生活模式的别名，而这一份名单同时是屏幕那一组判据的许用名单
        // （LifeModeFamilyWording），少一条这儿就红。
        "预设生活模式…", "预设生活模式", "关掉",
        "这些是随安装带来的只读模板，收一个进来才算自己的",
        "一个预设生活模式都没有",
        "预设生活模式那一整个目录不在了 —— 带在安装里的那几份一份也取不到",
        "有的预设生活模式读不通，那一份收不进来",
        "预设生活模式「{0}」读不通，所以没有收进来",
        "预设生活模式的画廊开不出来",
    ];

    /// 词汇表把「生活模式 / flow / 方案」列为<b>休息流程</b>的禁用词（<c>GLOSSARY.md</c>
    /// 「遮罩与休息」那一条），而 2026-10-01 起「生活模式」专指**配置快照**。所以界面上凡是
    /// 提到那一个词的地方，都得是生活模式这一族自己的措辞 —— 休息流程那一族（那张卡、选哪一条
    /// 那一行、它的步编辑器）一个字都不许带它。
    ///
    /// <b>为什么许用名单是派生的</b>：生活模式这一族在界面上的字有十来条，手抄一份就是
    /// CODING_STANDARDS.md 第 6 条说的那种会腐烂的副本 —— 加一条措辞忘了往名单里抄，
    /// 判据立刻开始误报（而没人知道该改哪儿）。所以从 <see cref="LifeModeMenuLabels"/> 派生，
    /// 键是中文原文、当前语言那一栏从语言表取（<c>Lang.Apply</c> 刚装过），
    /// **一份名单两门语言**，en 那一栏不另抄。
    private static string[] LifeModeFamilyWording() => [.. LifeModeMenuLabels.Select(key => Lang.T(key))];

    /// 那一个词在两门语言里的样子。**只查这几个词，不查「休息流程」这个词本身** ——
    /// 那是这一族该用的词。中文按子串比（中文没有词边界，同 <see cref="LeaksInternalName"/>），
    /// 英文按整词比（<c>flows</c>、<c>schemes</c> 那些复数形也是同一个词）。
    ///
    /// 中文那三个是<b>一个词的三种写法都要挡住</b>：现在叫「生活模式」，而「模式」是它的老名字
    /// ——界面上再写「模式」就是没改干净，那一条同样算把休息流程叫成这一族。
    private static string[] StolenForFlow(string language) =>
        language == "en" ? ["mode", "modes", "scheme", "schemes"] : ["生活模式", "模式", "方案"];

    private static bool StealsIt(string text, string word) =>
        word.Any(ch => ch > 127)
            ? text.Contains(word, StringComparison.Ordinal)
            : Tokens(text).Contains(word, StringComparer.OrdinalIgnoreCase);

    /// 屏幕上那句实际的话，是不是名单里某一条的<b>整句</b>。
    ///
    /// <b>占位符那一段当任意字</b>：名单里既有句尾的「生活模式：{0}」（生活模式名被摘掉之后屏幕上
    /// 就是「生活模式：」，占位符吃到零个字），也有句中的「生活模式「{0}」读不通」（{0} 装的是生活模式名），
    /// 所以那几段长度不定。<b>整句要对上，不是「句首像」</b> —— 后面再添一个字就是另一句话了，
    /// 而那种比法会把「休息生活模式」放到「生活模式：」跟前放过。
    private static bool Wording(string label, string text)
    {
        var pieces = Literals(label);
        // 整条名单就是一个占位符的话，它会放过屏幕上每一句话 —— 那种名单不许算数。
        if (pieces.Count == 0 || pieces.All(p => p.Length == 0)) return false;
        return Fits(pieces, 0, text, 0);
    }

    /// 一条名单拆成几段<b>字面量</b>，占位符切开的那些段不在里面。
    /// 半截的占位符（没有 <c>}</c>）交回空表：那一条名单本身坏了，不许它放过任何话。
    private static List<string> Literals(string label)
    {
        var pieces = new List<string>();
        var at = 0;
        for (var slot = label.IndexOf('{'); slot >= 0; slot = label.IndexOf('{', at))
        {
            var close = label.IndexOf('}', slot);
            if (close < 0) return [];
            pieces.Add(label[at..slot]);
            at = close + 1;
        }
        pieces.Add(label[at..]);
        return pieces;
    }

    /// 几段字面量按次序摆进那句话里，占位符吃掉中间那几个字。
    /// <b>头一段不许跳过</b>：名单开头那几个字就是那句话的开头，跳过它等于「句首像」。
    private static bool Fits(List<string> pieces, int i, string text, int at)
    {
        if (i == pieces.Count) return at == text.Length;
        var piece = pieces[i];
        if (At(text, at, piece) && Fits(pieces, i + 1, text, at + piece.Length)) return true;
        if (i == 0) return false;
        for (var k = at + 1; k + piece.Length <= text.Length; k++)
            if (At(text, k, piece) && Fits(pieces, i + 1, text, k + piece.Length)) return true;
        return false;
    }

    private static bool At(string text, int at, string piece) =>
        piece.Length == 0 ||
        (text.Length >= at + piece.Length && string.CompareOrdinal(text, at, piece, 0, piece.Length) == 0);

    /// 编辑器上那些不带声明的标签。「第 N 步」和「共 N 步」里那个 {n} 也要在：
    /// 缺一条不报错，屏幕上露出的是中文键名。
    private static readonly string[] EditorLabels =
    [
        "flows.hint", "这条叫什么", "第 N 步", "挥得散", "接鼠标", "放声音", "摆什么",
        "上移", "下移", "删掉这一步", "新增一条", "删掉这一条", "加一步", "新休息流程",
        "已经有这条休息流程", "共 N 步", "一步总得摆点东西，所以那三样又回来了",
        "这张表里还没有一条休息流程", "一步都没有，它就只淡进来", "遮罩预览",
        // 「文字」是契约给这一步自带的字写死的那三个字（steps[].text 那一行），
        // 标签和「摆什么」里 content 的 text 那一格共用它。
        "文字",
    ];

    /// <summary>界面上**不许出现**的那些词，<b>从声明表派生</b>（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节）。
    ///
    /// <b>为什么是派生的而不是一张手写的清单</b>：这张清单原来只有五个词
    /// （<c>flows</c> / <c>step</c> / <c>settings.jsonc</c> / <c>毫秒</c> / <c>ms</c>），
    /// 而规则说的是**一类**。它当场漏过：<c>calendar.style</c> 和 <c>calendar.corner</c>
    /// 那两条提示里原样写着 <c>wallpaper</c> <c>outline</c> <c>widget</c> 和
    /// <c>marginXPercent</c> —— 自检全绿，界面上摆着四个配置文件里的名字。
    /// 手写清单天生追不上声明表：加一个键就得记得往这里抄一个词，而忘了不报任何错。
    /// 声明表是**唯一**知道「配置里有哪些词」的地方，所以名单从那里来。
    ///
    /// <b>分两档</b>，因为「名字」和「值」在界面上该待的地方不一样：
    /// <c>Structure</c> 是**结构**的名字（每一条键的点分段、文件名、那张表的键、表里一行的
    /// 字段名），它在哪儿都不该出现 —— 连下拉里那一项也不该是它。
    /// <c>Value</c> 是**被选中的那个值**（每一条 <c>Choice</c> 的可选值）：下拉里摆着它是
    /// 那个控件的本分（<c>flow</c> 那个下拉里就是休息流程的名字，那是用户自己的数据），
    /// 但<b>提示句子里</b>出现它就是漏 —— 上一版自检全绿，而 <c>calendar.style</c> 那条提示
    /// 里原样写着 <c>wallpaper</c> <c>outline</c> <c>widget</c>。
    ///
    /// <b>两门语言查的名单不一样</b>，因为**证据不一样**：中文正文里不夹英文词，所以
    /// <c>sound</c> / <c>file</c> / <c>corner</c> / <c>stretch</c> 出现在那儿只可能是因为
    /// 「被引用了」—— 整份都查。英文正文里那些就是**普通词**（「Plays the sound」
    /// 「Picture file」「Which corner」「one focus stretch」），整份查出来的红**全是假的**：
    /// 2026-09-30 第一次开英文那一遍，一口气报 28 条，逐条看过没有一条是真的。
    /// 所以英文只查<b>有辨识度</b>的名字 —— 带大写字母 / 数字 / 非字母字符的（camelCase 的
    /// <c>marginXPercent</c>、<c>fadeInMs</c>、带点的 <c>settings.jsonc</c>）。
    /// **代价要说明白**：英文正文里引用一个**纯小写**的名字（原来那句 <c>The widget</c>）
    /// 这一遍查不出来 —— 那是判据覆盖不到的地方，不是它干净。已经改掉的那两条里
    /// <c>The widget</c> 属于这一类（靠人读出来的），<c>marginXPercent</c> 属于有辨识度那一类
    /// （靠判据抓到的）。要补这一档只有一条路：给纯小写的名字配一个「它在这门语言里
    /// 是普通词」的名单 —— 那又是一份要人维护的清单，所以没做。
    ///
    /// <b>比的是整词，不是子串</b>（<see cref="LeaksInternalName"/>）：子串匹配下 <c>ms</c>
    /// 会撞上 <c>forms</c>，而那种红是假的；整词匹配下 <c>flows.hint</c> 里那个
    /// <c>flows</c> 才真的算命中。</summary>
    private static (string Word, bool Cjk, bool Value, bool Distinctive)[] ForbiddenWords()
    {
        var structure = new SortedSet<string>(StringComparer.Ordinal);
        var value = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var decl in Settings.Declarations)
        {
            foreach (var part in decl.Key.Split('.')) structure.Add(part);
            foreach (var choice in decl.Choices) value.Add(choice);
        }
        // **step 那一张表和顶层一样**（工单 04）：`steps[].text` 是配置文件里的一个键名，
        // 而 `text` / `block` 是 `steps[].content` 的两个可选值 —— 四个都是内部名字，
        // 界面上一个都不该出现（「文字」那一行摆的是人话，见 locale/keeper.csv）。
        // 原来这一族整族不在名单里：那张表声明在 `Settings.StepDeclarations`，
        // 而这里原来只走 `Settings.Declarations`（顶层那一份）—— 加一个 step 字段
        // 或加一种内容，界面上摆出它而自检全绿，正是这份注释开头记的那一类漏。
        // **step 那一张表和顶层一样**（工单 04）：`steps[].text` 是配置文件里的一个键名，
        // 而 `text` / `block` 是 `steps[].content` 的两个可选值 —— 四个都是内部名字，
        // 界面上一个都不该出现（「文字」那一行摆的是人话，见 locale/keeper.csv）。
        // 原来这一族整族不在名单里：那张表声明在 `Settings.StepDeclarations`，
        // 而这里原来只走 `Settings.Declarations`（顶层那一份）—— 加一个 step 字段
        // 或加一种内容，界面上摆出它而自检全绿，正是这份注释开头记的那一类漏。
        //
        // **反向验（2026-10-02）**：把这一段拿掉、同时把步编辑器那颗标签改成原样摆
        // `steps[].text`，`screen` 那一组**当场还是绿的** —— 量到的实测结果是
        // 「扫到了 …/Words/Tag 文字='steps[].text'，而词表里没有 text」。
        // 也就是说这一族从前压根不在尺子上，补它不是为了让它红，是为了**下一次**
        // 有人把键名写进标签时它会红。改回那颗标签之后那一组是绿的。
        foreach (var decl in Settings.StepDeclarations)
        {
            structure.Add(decl.Key);
            foreach (var choice in decl.Choices) value.Add(choice);
        }
        // 时间块那七个字段是**第三张**声明表，和上面两张同一个处境：它们在
        // `Settings.BlockDeclarations` 里，键带 `blocks[].` 前缀，界面上不许出现。
        // 2026-10-02 复核补上：工单 01 先声明了这一族，工单 02 搭了那一格，两者都把「等那一格
        // 真搭起来再扩词表」记下来了，但**没有一张真去扩** —— 于是 `startMinute` /
        // `yieldsToInput` / `flow` / `kind` / `fade` 这一族不在尺子上，
        // 有人把它们写进标签时 `screen` 那一组不会红。上面那段 `StepDeclarations` 的注释
        // 记的正是同一个漏（那一族的反向验证读到的是「扫到了，而词表里没有」），
        // 这里是**同一族的第三个实例**：加一张声明表就漏一张。
        //
        // **前缀要摘掉**：词表收的是**界面上会写出来的那个键名**（`steps[].text` 那一条也是
        // 摘掉 `steps[].` 之后收 `text` 的），而界面上写的是「几点开始」「叫什么」，
        // 不是 `blocks[].startMinute`。`BlockDeclarations` 的键带着前缀，所以摘一次。
        foreach (var decl in Settings.BlockDeclarations)
        {
            var bare = decl.Key.StartsWith(TimeBlock.KeyPrefix, StringComparison.Ordinal)
                ? decl.Key[TimeBlock.KeyPrefix.Length..]
                : decl.Key;
            structure.Add(bare);
            foreach (var choice in decl.Choices) value.Add(choice);
        }
        foreach (var extra in new[] { Settings.FileName, Settings.ModesKey, LifeModeIndex.FolderName, "step", "ms", "毫秒" })
            structure.Add(extra);

        bool Dist(string w) =>
            w.Any(ch => ch > 127)                                   // 中文那一条
            || w == "ms"                                             // 存储单位那一族，英文正文里出现就是漏
            || !w.All(ch => ch is >= 'a' and <= 'z');                // 不是「纯小写英文词」就有辨识度
        return structure.Select(w => (Word: w, Cjk: w.Any(ch => ch > 127), Value: false, Distinct: Dist(w)))
            .Concat(value.Select(w => (Word: w, Cjk: w.Any(ch => ch > 127), Value: true, Distinct: Dist(w))))
            .ToArray();
    }

    /// <summary>这句话里有没有一个<b>整词</b>是 <paramref name="word"/>。
    /// ASCII 那个按整词比（中文正文里那些配置词就是原样夹进来的 ASCII），
    /// 含非 ASCII 的（<c>毫秒</c>）按子串比 —— 中文没有词边界，分不出整词。</summary>
    private static bool LeaksInternalName(string text, string word, bool cjk)
    {
        if (!cjk) return Tokens(text).Contains(word, StringComparer.Ordinal);
        return text.Contains(word, StringComparison.Ordinal);
    }

    /// <summary>把一句话切成「词」：连续的字母/数字/下划线算一个，其余都是分隔符。
    /// 所以 <c>marginXPercent</c>、<c>settings.jsonc</c>、<c>fadeInMs</c> 各自成一个词，
    /// 而中文夹着它也切得出来（「离 <c>marginXPercent</c> 和」里那一个）。</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var token = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') token.Append(ch);
            else if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
        }
        if (token.Length > 0) yield return token.ToString();
    }

    /// <summary>这一步最多能写多久，由声明说了算。场景上那颗数值控件自己不该带范围 ——
    /// 它一带，声明改了而场景没改，界面上就写不进文档深处那个值，或者反过来把能写的夹掉了。
    /// 引擎里 <c>MinValue</c>/<c>MaxValue</c> 没设过时是 0..100（<c>Range</c> 的默认），
    /// 所以「等于 0 和 100」读作「没写过」—— 上面那句注释以前写的是 -100..100，
    /// 跟下面这两条断言对不上，2026-09-29 按实测结果改准。
    ///
    /// 2026-09-29 之前这一条只接 <c>SpinBox</c>、只管 flows 编辑器 step 模板那一颗，
    /// 而场景里有 21 颗数值控件；根 <c>AGENTS.md</c> 那条「控件上只写 setting 的键名，
    /// min/max/default 从声明里取」是对全部数值控件说的。现在它是 <see cref="Audit"/> 的第 12 条，
    /// 走整棵场景树，<paramref name="who"/> 由调用方给（那里给的是场景路径）。</summary>
    private static List<string> RangeIsNotHardcoded(Control control, string who)
    {
        var (min, max) = control switch
        {
            SpinBox spin => (spin.MinValue, spin.MaxValue),
            _ => (0d, 100d),
        };
        var kind = control.GetType().Name;
        var checks = new Checks();
        if (Math.Abs(min) > 0.001)
            checks.Fail($"{who}：场景里那颗 {kind} 自己写了 MinValue={min}");
        if (Math.Abs(max - 100) > 0.001)
            checks.Fail($"{who}：场景里那颗 {kind} 自己写了 MaxValue={max}（100 是引擎没设过时的数）");
        return checks.Report();
    }

    /// <summary>导出 / 导入那一条路的常驻自检。临时目录里写一份带注释和一个陌生键的文档，
    /// 开一扇真的设置窗，按底排那两颗钮，各交一个路径给文件框，然后对字节。
    ///
    /// 全程不碰鼠标：按的是那颗钮自己的 <c>pressed</c>，交的是文件框选中文件时发的那一个信号 ——
    /// 从这两处往下的接线和真鼠标点出来的是同一条。引擎自己的文件列表怎么点，那是引擎的代码。</summary>
    public static List<string> IoSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("io");
        var dir = host.Dir;
        var exported = System.IO.Path.Combine(dir, "exported.jsonc");
        var incoming = System.IO.Path.Combine(dir, "incoming.jsonc");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName), $$"""
                {
                  // 这一行注释不许丢
                  "{{Settings.VersionKey}}": {{Settings.SchemaVersion}},
                  "activityTimeoutMin": 15,
                  "myOwnKey": "Keeper 不认识这个键"
                }
                """);
            System.IO.File.WriteAllText(incoming, $$"""
                {
                  // 这一份是要被导入进来的，它的值该在界面上看得见
                  "{{Settings.VersionKey}}": {{Settings.SchemaVersion}},
                  "activityTimeoutMin": 7,
                  "flow": "fadetop"
                }
                """);

            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            // **先装语言表**（`CODING_STANDARDS.md` 第 7 条）。装表的只有 App，自检那一侧
            // 不装 —— 于是下面那条判据要读的「屏幕上那句字」量到的是键名，
            // 而键名在导入那一发和导出那一发里都出现，判据会绿着而它量的不是那件事。
            if (Lang.Apply(state.Current.Language).Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];
            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死
            var pick = win.GetNode<FileDialog>("Pick");
            var before = win.Live;

            // 抬头那一行每秒一刷：这一组只证「那一行跟着 _Process 走、不到一秒不刷」这半条接线。
            // 「代码里现造的子窗，引擎真的每帧叫它的 _Process」是另外量的，在 Probe --push
            // 那一段（子窗 322 帧 / 5381 毫秒）。
            var head = win.GetNode<Label>("Margin/Stack/Header/Status");
            win.Measurement = () => (12.3, 7);
            win._Process(1.2);
            // 12.3 分钟那一格显示的是**人话单位**（设计文档 `docs/design/config.md`「界面上不出现毫秒」那一条的时间
            // 线版本）：走 <see cref="Settings.Spell"/>，够一分钟就写分钟、不带小数点。
            var want = Settings.Spell((long)Math.Round(12.3 * 60_000));
            if (!head.Text.Contains($"{want.Number} {TrCounted(want.UnitKey, want.Number)}"))
                checks.Fail($"抬头那一行没跟着 _Process 走：'{head.Text}'（该有 " +
                             $"{want.Number} {TrCounted(want.UnitKey, want.Number)}）");
            // 休息记录那一半还在（它量的是记录本身，而记录照旧在写）。
            if (!head.Text.Contains($"{Lang.T("休息记录")} 7"))
                checks.Fail($"抬头那一行没写休息记录条数：'{head.Text}'");
            // 健康度那一半 2026-10-06 撤了（所有者定夺「把健康度跟进度条移除」）。
            // 这一条查的是那一行**真的没有**它 —— 界面上不许出现的东西，
            // 留在计算里不算数，留在拼字里也算数。
            foreach (var gone in new[] { Lang.T("健康度"), Lang.T("健康指数"), "%" })
                if (head.Text.Contains(gone))
                    checks.Fail($"抬头那一行还有「{gone}」：'{head.Text}' —— 健康度 2026-10-06 从界面上撤了");
            win.Measurement = () => (99.9, 1);
            win._Process(0.05);
            var other = Settings.Spell((long)Math.Round(99.9 * 60_000));
            if (head.Text.Contains($"{other.Number} "))
                checks.Fail("抬头那一行每一帧都在刷，不是一秒一次");

            win.PressFooter("Export");
            if (!pick.Visible || pick.FileMode != FileDialog.FileModeEnum.SaveFile)
                checks.Fail($"按了导出，文件框没以「存」那一面开起来（visible={pick.Visible} flow={pick.FileMode}）");
            if (!pick.CurrentPath.Replace('\\', '/').StartsWith(dir.Replace('\\', '/')))
                checks.Fail($"文件框开到了设置所在目录之外：{pick.CurrentPath}（该在 {dir} 里）");
            win.Choose(exported);
            if (!System.IO.File.Exists(exported)) checks.Fail($"交了路径，{exported} 却没出现");
            else
            {
                var text = System.IO.File.ReadAllText(exported);
                if (!text.Contains("这一行注释不许丢")) checks.Fail("导出那份把注释弄丢了");
                if (!text.Contains("myOwnKey")) checks.Fail("导出那份把 Keeper 不认识的键弄丢了");
                if (!text.Contains("\"activityTimeoutMin\": 15")) checks.Fail("导出那份没带上当前的值");
            }

            win.PressFooter("Import");
            if (pick.FileMode != FileDialog.FileModeEnum.OpenFile)
                checks.Fail($"按了导入，文件框没换成「开」那一面（flow={pick.FileMode}）");
            win.Choose(incoming);
            if (ReferenceEquals(before, win.Live))
                checks.Fail("导入没有换掉活的设置那一个对象，窗口还在拿旧引用");
            if (win.Live.ActivityTimeoutMin != 7 || win.Live.Flow != "fadetop")
                checks.Fail($"导入之后窗口读的活设置是 {win.Live.ActivityTimeoutMin}/{win.Live.Flow}，该是 7/fadetop");
            var shown = (SpinBox)win._byKey["activityTimeoutMin"].Value;
            if ((int)shown.Value != 7) checks.Fail($"导入之后界面上那一行显示的是 {shown.Value}，没跟着重灌");
            var live = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, Settings.FileName));
            if (!live.Contains("要被导入进来的")) checks.Fail("选中的那份文档没成为活文档（它的注释不在）");
            if (!System.IO.File.Exists(SettingsStore.BackupPath(dir))) checks.Fail("导入没留下 .bak");
            else if (!System.IO.File.ReadAllText(SettingsStore.BackupPath(dir)).Contains("myOwnKey"))
                checks.Fail("被取代的那一份进备份时把用户自己的键弄丢了");

            // ---- 导入的提示句：格式版本比本程序新的时候**该说**，不新的时候**不该说** ------------
            // 这半句（`ImportFrom` 里那个 `State.NewerThanThisBuild` 的括号）在这一组
            // 上线之前自检里一条判据都没有 —— 设计文档 `docs/design/config.md`「格式」那一节要「导入一份 stamped 更新的文档要警告」，
            // 那个「要警告」今天是**无人验**的：把那句 `Tr()` 拿掉，这一组仍然全绿。
            //
            // **两头夹**，不是只量一头：只量「该说的时候说了」，那句判断写死成
            // 永远为真也照样绿 —— 契约要的恰恰是它**不该**在每份导入后面都出现。
            //
            // 取词**不手抄**：词从 `Tr` 那一栏现取（`LabelOf` 那一族的老办法，
            // `CODING_STANDARDS.md` 第 6 条），而语言表在上面已经装上了。
            //
            // 「表没装上」怎么判**不靠比中文原文**：zh 那一栏的译文就是中文原文本身，
            // 拿它当哨兵等于把「装得好好的」读成「没装」（这一版就栽在这里，当场就报了 1 条）。
            // 真正的信号是 `Tr` 返回空 —— 键在表里没这一栏。
            var stampWarn = win.Tr("这份文档的格式版本比本程序新");
            if (string.IsNullOrEmpty(stampWarn))
                checks.Fail("语言表里没有「这份文档的格式版本比本程序新」那一栏，这一组量的不是屏幕上那句字");
            var msgAfterSame = win.GetNode<RichTextLabel>("Margin/Stack/Msg").Text;
            if (!string.IsNullOrEmpty(stampWarn) && msgAfterSame.Contains(stampWarn, StringComparison.Ordinal))
                checks.Fail($"格式版本和本构建一样（{Settings.SchemaVersion}）却说了「比本程序新」：'{msgAfterSame}'");

            // 造一份真的更新的：格式版本从 `SchemaVersion` **推**（和 SettingsStore 那一组同一个办法），
            // 不写死一个数 —— 写死那就是下一份会腐烂的副本。
            var newer = System.IO.Path.Combine(dir, "newer.jsonc");
            System.IO.File.WriteAllText(newer, $$"""
                {
                  "{{Settings.VersionKey}}": {{Settings.SchemaVersion + 2}},
                  "activityTimeoutMin": 9,
                  "flow": "fadetop"
                }
                """, new System.Text.UTF8Encoding(false));
            win.PressFooter("Import");
            win.Choose(newer);
            var msgAfterNew = win.GetNode<RichTextLabel>("Margin/Stack/Msg").Text;
            if (!msgAfterNew.Contains(stampWarn, StringComparison.Ordinal))
                checks.Fail($"导入了一份格式版本是 {Settings.SchemaVersion + 2} 的文档，却没说「比本程序新」：'{msgAfterNew}'");
            if (win.Live.ActivityTimeoutMin != 9)
                checks.Fail($"导入那份更新的文档，值没换过来：{win.Live.ActivityTimeoutMin}，该是 9");

            win.PressFooter("Export");
            if (string.Join(";", pick.Filters) != "*.jsonc")
                checks.Fail($"导出那个框的过滤是「{string.Join(";", pick.Filters)}」，该是 *.jsonc");
            win.PressFooter("Import");
            if (string.Join(";", pick.Filters) != "*.jsonc;*.json")
                checks.Fail($"导入那个框的过滤是「{string.Join(";", pick.Filters)}」，该是 *.jsonc;*.json");
            win._byKey["image.file"].Browse!.EmitSignal(Button.SignalName.Pressed);
            var imageFilter = string.Join(";", pick.Filters);
            if (imageFilter.Contains("gif"))
                checks.Fail($"图那一档的过滤里有 gif：{imageFilter} —— 引擎放不了这一档，不该给人挑");
            if (!imageFilter.Contains("*.png") || !imageFilter.Contains("*.webp"))
                checks.Fail($"图那一档的过滤缺了 png/webp：{imageFilter}");
            win.Choose("C:/x/y.png");
            win._byKey["sound.file"].Browse!.EmitSignal(Button.SignalName.Pressed);
            var soundFilter = string.Join(";", pick.Filters);
            if (!soundFilter.Contains("*.mp3") || !soundFilter.Contains("*.ogg") || !soundFilter.Contains("*.wav"))
                checks.Fail($"声那一档的过滤不是 mp3/ogg/wav：{soundFilter}");
            if (soundFilter.Contains("m4a"))
                checks.Fail($"声那一档的过滤里有 m4a：{soundFilter} —— 引擎没有 AAC 解码器");
            win.Choose("C:/x/y.mp3");

            // ---- 抬头那一句「磁盘上那份读不通」是常驻的，不是等按下保存才说 ------------------
            var banner = win.GetNode<Label>("Margin/Stack/Header/Error");
            if (banner.Visible) checks.Fail("文档读得通的时候，抬头那一句不该露着");
            // 注一个「读不通」的状态进去。原来这里是直接改 win.State.Error 那个可变字段，
            // 现在 Read 不可变，所以换成整份替换 —— 顺带这一发也变成了「换一个状态」而不是
            // 「改一个字段」，跟产品那一条路（导入还回一份新的）是同一个形状。
            win.State = win.State with { Error = "这里需要一个 , 或者 }（行 239，列 3）" };
            win.ShowDocError();
            if (!banner.Visible || !banner.Text.Contains("行 239"))
                checks.Fail($"文档读不通时抬头没说清：可见={banner.Visible} 文字='{banner.Text}'");
            win.State = win.State with { Error = null };
            win.ShowDocError();
            if (banner.Visible) checks.Fail("文档读通了那一句还赖着");

            // ---- 清单：把已有的一行删空 = 那一条跟着没（这一条规矩滤两次：界面上、落盘前）----
            var todosPath = System.IO.Path.Combine(dir, Todos.FileName);
            var seeded = new Todos { Items = [new Todos.Todo { Text = "甲" }, new Todos.Todo { Text = "乙" }] };
            seeded.Save(todosPath);
            win.Checklist = Todos.Load(todosPath);
            win.BuildChecklist();
            var writes = 0;
            win.ChecklistChanged = () => { win.Checklist.Save(todosPath); writes++; };
            var first = ChecklistRow(win, 0).GetNode<LineEdit>("Text");
            first.EmitSignal(LineEdit.SignalName.TextChanged, "   ");
            if (win.Checklist.Items.Count != 1)
                checks.Fail($"把一行删空之后还剩 {win.Checklist.Items.Count} 条，该是 1（删空一条 = 删掉那一条）");
            if (writes != 1) checks.Fail($"删空那一行落盘了 {writes} 次，该是一次");
            if (Todos.Load(todosPath).Items.Count != 1)
                checks.Fail("磁盘上那份还带着空白的一条");
            if (ChecklistRowCount(win) != 1)
                checks.Fail($"界面上还摆着 {ChecklistRowCount(win)} 行，该跟着收掉那一行");
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();
    }

    /// <summary>生活模式那一族在<b>真窗</b>上的那一条链（组名 <c>flows-menu</c>）：窗头那颗钮
    /// 展开的菜单、它上面写的那一档，以及底下那几条。全程不发鼠标事件 —— 按的是菜单自己发的
    /// <c>id_pressed</c>（上半那几条按在<b>子菜单</b>自己身上）和那颗钮自己的 <c>pressed</c>，
    /// 从这些信号往下的接线和真鼠标点出来的是同一条（弹层那半步按 <c>CODING_STANDARDS.md</c>
    /// 那一段只能拿真鼠标量，而「菜单项被选中并递回 id」这一半是可以量的）。
    ///
    /// <b>次序是判据的一部分</b>（<c>CODING_STANDARDS.md</c> 第 8 条）：先把窗里拧一个数
    /// 制造出「窗里这份和磁盘上那个生活模式文件不一致」，才判「不在任何生活模式里」；反过来判，
    /// 量到的是「还没开窗时那一份」，那是另一件事。「切换两个动作」那两条是同一副牌：
    /// 先造出没保存的改动，再分头按那两项，而两者的差别量在<b>备份</b>上（切换那一份
    /// 备份里没有那个没保存的值，保存并切换那一份有）。
    ///
    /// 值那一半（系统那三条逐字节没动、其余整份换掉、备份在）在 <see cref="LifeModeStore.SwitchSelfTest"/>
    /// 那组里 —— 同一个族分两组是因为一个要活节点树、一个不要。位置那一档在纯算术那一侧
    /// （<see cref="LifeModeStore.ManageSelfTest"/>）判过一次，这里判的是<b>那颗钮上的字</b>。
    ///
    /// 跑在临时目录里，一条都不碰 <c>%APPDATA%</c>；<c>finally</c> 里窗和目录都放。</summary>
    public static List<string> LifeModeMenuSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("mode-menu");
        var dir = host.Dir;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            // 域外那三条给成非默认值：留在默认值上的时候，「切过去之后它没变」这条判据
            // 什么都量不出来（那正是它们被设成这样的原因）。
            var started = new Settings();
            started.ActivityTimeoutMin = 40;
            started.FadeInMs = 3_000;
            started.Language = "en";
            started.UiTheme = "dark";
            started.Autostart = true;
            SettingsStore.Save(dir, new SettingsStore.Read { Current = started });
            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            // 先装语言表（`CODING_STANDARDS.md` 第 7 条：判据要读「屏幕上那句字」，先装表），
            // 而且**钉住**语言 —— 不然这一组量的字取决于上一组把语言停在哪一门。
            if (Lang.Apply("zh").Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];

            // **窗里拧哪一格**：域内的一格（切过去会换成生活模式里那一份），而域外那几条切的时候
            // 一个字节都不动 —— 所以「切换」和「保存并切换」那两档量的正是它，一个备份里
            // 有、一个备份里没有。声明放在最前面：下面那扇窗（State 读不通的那一扇）也要用它。

            // ---- 「保存并切换」：保存不成时说清为什么，并且**不切** -------------------------
            // 这一条要一扇**带着读不通状态**的窗，所以它自己一个目录、一扇窗，跑完就放 ——
            // 和下面那扇不是同一扇（那扇的 State 是读得通的）。设计文档 `docs/design/life-mode.md`「切换有两个动作」那条
            // 的后半句：保存不成就说清为什么并且不切；而切过去是拿写好的文档换的，
            // 先切等于替用户把那份文档盖掉。
            var brokenDir = System.IO.Path.Combine(dir, "unreadable");
            System.IO.Directory.CreateDirectory(brokenDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(brokenDir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion}, \"flow\": \"fadetop\"\n");   // 少一个 }
            if (LifeModeStore.SaveAs(brokenDir, "写作", started).Error != null) checks.Fail("这一扇的底稿生活模式没存成");
            var brokenState = SettingsStore.Load(brokenDir);
            if (brokenState.Error == null) checks.Fail("那份故意写坏的文档居然读通了，这一发量的不是「保存不成」");
            // 这一扇在**自己的子目录**上（`dir` 底下那个 `unreadable`），所以它用
            // `ForDir` 那一支 —— 目录是现成的，`OptionsHost` 不删它（删的是外层那个
            // `host`，子目录跟着它一起没）。中途量完就放，放的是这一扇不是外层。
            var brokenHost = OptionsHost.ForDir(brokenDir);
            var broken = brokenHost.Open(brokenState, parent);
            var brokenBefore = System.IO.File.ReadAllText(SettingsStore.FilePath(brokenDir));
            var brokenMsg = broken.GetNode<RichTextLabel>("Margin/Stack/Msg");
            // 「不切」量在**那颗钮上**：切过去了它就会写「生活模式：写作」，而这一扇眼下读不通，
            // 活的那一份是带默认值的那份，和那个生活模式文件对不上。
            var brokenButton = broken.GetNode<MenuButton>(LifeModePath);
            var whereBefore = brokenButton.Text;
            PressSub(brokenButton.GetPopup(), 0, "保存并切换");
            if (brokenButton.Text != whereBefore)
                checks.Fail($"保存没成，却切过去了：那颗钮上从 '{whereBefore}' 变成了 '{brokenButton.Text}'");
            if (System.IO.File.ReadAllText(SettingsStore.FilePath(brokenDir)) != brokenBefore)
                checks.Fail("保存没成，活文档却动了一字");
            if (!brokenMsg.Visible || !brokenMsg.Text.Contains("没有切", StringComparison.Ordinal))
                checks.Fail($"保存没成却没说「没有切」：'{(brokenMsg.Visible ? brokenMsg.Text : "（没露出来）")}'");
            if (!brokenMsg.Text.Contains("读不通", StringComparison.Ordinal))
                checks.Fail($"没把「为什么」说清：'{brokenMsg.Text}'");
            brokenHost.Dispose();   // 量完就放这一扇（外层那个 host 不动）


            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死

            var button = win.GetNode<MenuButton>(LifeModePath);
            var popup = button.GetPopup();
            var prompt = win.GetNode<Window>(NamePath);
            var text = win.GetNode<LineEdit>(NameTextPath);
            var why = win.GetNode<Label>(NameErrPath);
            var ok = win.GetNode<Button>(NameOkPath);
            var msg = win.GetNode<RichTextLabel>("Margin/Stack/Msg");
            // **窗里拧哪一格**：域内的一格（切过去会换成生活模式里那一份），
            // 而域外那几条切的时候一个字节都不动 —— 所以「切换」和「保存并切换」那两档
            // 量的正是它，一个备份里有、一个备份里没有。
            const string turned = "activityTimeoutMin";

            // ---- 第一档：一个生活模式都还没有 → 那颗钮写「还没有生活模式」 ----------------------
            if (button.Text != TranslationServer.Translate("还没有生活模式"))
                checks.Fail($"一个生活模式都没有，那颗钮上写着 '{button.Text}'，该是语言表里「还没有生活模式」那一条");
            if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Entries.Count != 0) checks.Fail("还没存过，名单上却有条目");
            if (HasItem(popup, "存为新生活模式") == false) checks.Fail("菜单下半没有「存为新生活模式」那一项");

            // ---- 存：按菜单里「存为新生活模式」那一项 → 起名那扇小窗开起来 -----------------
            PressItem(popup, "存为新生活模式");
            if (!prompt.Visible) checks.Fail("按了「存为新生活模式」，起名那扇小窗没开起来");
            if (System.IO.Directory.Exists(LifeModeStore.DirOf(dir)))
                checks.Fail("还没存过，生活模式那个子目录就被造出来了");

            // 全是空格的名字：当场不给，名单和磁盘都不动，理由就摆在小窗里。
            text.Text = "   ";
            ok.EmitSignal(Button.SignalName.Pressed);
            if (!prompt.Visible) checks.Fail("名字没过，起名那扇小窗却关了");
            if (!why.Visible || why.Text.Trim().Length == 0)
                checks.Fail("名字没过，小窗里没说为什么");
            if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Entries.Count != 0)
                checks.Fail("名字没过，名单里却多了一条");

            // ---- 一个过得了的名字：存下、关窗、名单多一条 ------------------------------
            text.Text = "😀 画画 🎨";
            ok.EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("存成了，起名那扇小窗还开着");
            var index = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            if (index.Entries.Count != 1) checks.Fail($"名单里该有一条，实际 {index.Entries.Count} 条");
            if (index.Find("😀 画画 🎨") == null) checks.Fail("名单里没有刚存的那一条");
            var modeFile = index.Entries.Count == 1 ? LifeModeStore.PathOf(dir, index.Entries[0]) : "";
            if (modeFile.Length > 0 && !System.IO.File.Exists(modeFile))
                checks.Fail("名单里有那一条，磁盘上却没那份配置文档");
            // **菜单里不数项**：找那一条在不在、子菜单里那两项在不在，而不是「该有 N 项」。
            if (!HasItem(popup, "😀 画画 🎨")) checks.Fail("菜单上半没有刚存的那个名字");
            if (!HasItem(popup, "盖回一个生活模式…")) checks.Fail("菜单下半没有「盖回一个生活模式…」那一项");
            if (!HasItem(popup, "删掉这一条")) checks.Fail("菜单下半没有「删掉这一条」那一项");
            if (popup.GetItemSubmenuNode(0) == null) checks.Fail("上半那一条没有子菜单 —— 「两个动作共用一个目标」没接上");
            // 刚存下的那一份和刚写出去的那一个一致，所以那一档是「生活模式：那个名字」。
            if (button.Text != TranslationServer.Translate("生活模式：{0}").ToString().Replace("{0}", "😀 画画 🎨"))
                checks.Fail($"存完那颗钮上写着 '{button.Text}'，该是刚存的那一个");
            if (!msg.Visible || !msg.Text.Contains("已存为新生活模式", StringComparison.Ordinal))
                checks.Fail($"存完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");

            // ---- 重名当场拦住，并把「撞上的是哪一条」指向「盖回」 ----------------------
            var fileBefore = modeFile.Length > 0 ? System.IO.File.ReadAllText(modeFile) : "";
            PressItem(popup, "存为新生活模式");
            text.Text = "😀 画画 🎨";
            ok.EmitSignal(Button.SignalName.Pressed);
            if (!prompt.Visible) checks.Fail("撞了重名，起名那扇小窗却关了");
            if (!why.Visible || !why.Text.Contains("盖回", StringComparison.Ordinal))
                checks.Fail($"撞了重名，那句话没有指向「盖回」：'{(why.Visible ? why.Text : "（没露出来）")}'");
            if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Entries.Count != 1) checks.Fail("撞了重名，名单里却多了一条");
            if (modeFile.Length > 0 && System.IO.File.ReadAllText(modeFile) != fileBefore) checks.Fail("撞了重名，原来那份被改了");
            win.GetNode<Button>(NameCancelPath).EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("按了取消，那扇小窗还开着");

            // ---- 拧一格（不保存）：那一档该翻成「不在任何生活模式里」 ------------------------
            // **次序**：上面先判了「生活模式：那个名字」，这里才造出不一致再判下一档。
            ((SpinBox)win._byKey[turned].Value).Value = 7;
            if (button.Text != TranslationServer.Translate("不在任何生活模式里"))
                checks.Fail($"在窗里拧了一个数，那颗钮上写着 '{button.Text}'，该是「不在任何生活模式里」");
            // 「删掉这一条」按**它自己那一项**问，不按下标：05 在它下面加了一项之后
            // 「最后一项」就变成另一条了，而这一条判据量的正是它灰不灰（CODING_STANDARDS.md
            // 第 6 条：判据里不许存一份会腐烂的副本）。
            var deleteRow = Where(popup, "删掉这一条");
            if (deleteRow >= 0 && !popup.IsItemDisabled(deleteRow))
                checks.Fail("不在任何生活模式里，「删掉这一条」却不是灰的 —— 没有可删的那一个");

            // ---- 「切换」：作废没保存的改动、说一声；备份里**没有**那个没保存的值 ---------
            var untouched = SystemRows(win);       // 域外那几条此刻窗上摆着的（从声明表问出来是哪几条）
            var seven = (int)win._copy.Map.Get(turned);
            PressSub(popup, 0, "切换");
            if (win.Live.ActivityTimeoutMin != started.ActivityTimeoutMin)
                checks.Fail($"切过去之后活的那一份是 {win.Live.ActivityTimeoutMin}，该是生活模式里那一份 " +
                             $"{started.ActivityTimeoutMin}");
            // 域外那几条在窗上一个都没动 —— 而且是**对着这台机器自己的值**比的，
            // 不是「没变」：那几条被设成非默认值（见上面），留在默认值上时这条判据是空的。
            foreach (var (sysKey, was) in untouched)
            {
                var now = SystemRows(win)[sysKey];
                if (now != was) checks.Fail($"切过去之后窗上那一条 {sysKey} 变了：'{was}' → '{now}'");
                if (now != ShownFor(started, sysKey))
                    checks.Fail($"窗上那一条 {sysKey} 摆着 '{now}'，该是这台机器自己的 " +
                                 $"'{ShownFor(started, sysKey)}'");
            }
            // **对照**：域内那一格确实跟着换了（没有这一条，上面那几条「没变」是「什么都没动」的假绿）。
            if (((SpinBox)win._byKey[turned].Value).Value != started.ActivityTimeoutMin)
                checks.Fail("切过去之后，窗上还摆着那个没保存的值 —— 界面上没跟着换");
            if (!System.IO.File.Exists(SettingsStore.BackupPath(dir))) checks.Fail("切换没留下 .bak");
            // **拿备份里那个键去比窗里刚拧的那个数**，不是比窗里这一份：切过去以后窗里
            // 这一份已经是目标生活模式那一份了，两边当然一样，量不出先后。没保存的改动
            // 只在窗里，磁盘上从来没写过它 —— 备份里**有**那个数，就是先存过了。
            var backupAfterSwitch = At(SettingsStore.BackupPath(dir), turned)?.GetValue<int>();
            if (backupAfterSwitch == seven)
                checks.Fail($"「切换」的备份里有窗里那个没保存的值（{seven}）—— 那一档不许偷偷先存");
            if (!msg.Visible || !msg.Text.Contains("已切到生活模式", StringComparison.Ordinal))
                checks.Fail($"切完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
            if (!msg.Text.Contains("窗里没保存的改动", StringComparison.Ordinal))
                checks.Fail($"切掉了没保存的改动，却没说那半句：'{msg.Text}'");
            if (button.Text != TranslationServer.Translate("生活模式：{0}").ToString().Replace("{0}", "😀 画画 🎨"))
                checks.Fail($"切过去之后那颗钮上写着 '{button.Text}'");

            // ---- 「保存并切换」：先把窗里这一份按保存落到磁盘再切 -------------------------
            // **和上面那一条对着的实测结果**：备份里这一次**有**那个没保存的值。
            ((SpinBox)win._byKey[turned].Value).Value = seven;
            PressSub(popup, 0, "保存并切换");
            if (win.Live.ActivityTimeoutMin != started.ActivityTimeoutMin)
                checks.Fail("保存并切换之后活的那一份不是目标生活模式里那一份");
            // **和上面那一条对着的实测结果**：同一个键、同一把尺子，判据整个反过来。
            var backupAfterSaveSwitch = At(SettingsStore.BackupPath(dir), turned)?.GetValue<int>();
            if (backupAfterSaveSwitch != seven)
                checks.Fail($"「保存并切换」的备份里那个键是 {backupAfterSaveSwitch?.ToString() ?? "（没有）"}，" +
                             $"该是窗里那个没保存的值（{seven}）—— 它没有先按保存落盘");
            if (!msg.Visible || !msg.Text.Contains("已保存并切到生活模式", StringComparison.Ordinal))
                checks.Fail($"保存并切完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
            if (msg.Text.Contains("窗里没保存的改动", StringComparison.Ordinal))
                checks.Fail("窗里那些改动已经存下去了，状态栏却说它们没算进去");

            // ---- 盖回一个生活模式：同一个文件、名单一条不多、也不发新号 ----------------------
            // **预填先在「在生活模式里」那一份上量**：小窗里预填的该是当前那一个，
            // 预填错一个就盖错一个。量完按取消 —— 取消不许动磁盘。
            PressItem(popup, "盖回一个生活模式…");
            if (!prompt.Visible) checks.Fail("按了「盖回一个生活模式…」，那扇小窗没开起来");
            if (text.Text != "😀 画画 🎨")
                checks.Fail($"那扇小窗里预填的是 '{text.Text}'，该是当前那一个（预填错一个就盖错一个）");
            var listBefore = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            var textBefore = System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, listBefore.Entries[0]));
            win.GetNode<Button>(NameCancelPath).EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("按了取消，那扇小窗还开着");
            if (System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, listBefore.Entries[0])) != textBefore)
                checks.Fail("取消：还没点确定就把那个生活模式盖掉了");

            // 拧一格造出「不在任何生活模式里」：这时**没有**当前那一个可预填（小窗该是空的），
            // 而「盖回一个生活模式…」照样开着 —— 用户最想盖的时候够得着。
            ((SpinBox)win._byKey[turned].Value).Value = 9;
            if (button.Text != TranslationServer.Translate("不在任何生活模式里"))
                checks.Fail($"拧了一格之后那颗钮上写着 '{button.Text}'，该是「不在任何生活模式里」");
            PressItem(popup, "盖回一个生活模式…");
            if (!prompt.Visible) checks.Fail("不在任何生活模式里，按「盖回一个生活模式…」小窗却没开起来");
            if (text.Text.Length != 0)
                checks.Fail($"不在任何生活模式里却预填了 '{text.Text}' —— 没有当前那一个可预填");
            text.Text = "😀 画画 🎨";
            ok.EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("盖成了，那扇小窗还开着");
            var listAfter = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            if (listAfter.Entries.Count != listBefore.Entries.Count) checks.Fail("盖回去之后名单的条数变了");
            if (listAfter.NextNumber != listBefore.NextNumber) checks.Fail("盖回去却发了一个号");
            if (System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, listAfter.Entries[0])) == textBefore)
                checks.Fail("盖回去之后那个文件一个字节没变");
            if (button.Text != TranslationServer.Translate("生活模式：{0}").ToString().Replace("{0}", "😀 画画 🎨"))
                checks.Fail($"盖回去之后那颗钮上写着 '{button.Text}'");
            if (!msg.Visible || !msg.Text.Contains("已盖回生活模式", StringComparison.Ordinal))
                checks.Fail($"盖完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");

            // ---- 删掉这一条：名单和磁盘都少一条，编号不再发出去 ---------------------------
            var wasFile = listAfter.Entries[0].File;
            if (!LifeModeNumbers.TryNumberIn(wasFile, out var wasNumber)) checks.Fail($"{wasFile} 读不回成号");
            PressItem(popup, "删掉这一条");
            if (!prompt.Visible) checks.Fail("按了「删掉这一条」，确认那扇小窗没开起来");
            if (why.Visible && why.Text.Trim().Length == 0) checks.Fail("删掉之前没把「会发生什么」说清");
            ok.EmitSignal(Button.SignalName.Pressed);
            var left = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            if (left.Entries.Count != 0) checks.Fail($"删掉之后名单里该一条不剩，实际 {left.Entries.Count} 条");
            if (System.IO.File.Exists(LifeModeStore.PathOf(dir, listAfter.Entries[0])))
                checks.Fail("名单里少了一条，磁盘上那份还在");
            if (button.Text != TranslationServer.Translate("还没有生活模式"))
                checks.Fail($"删完最后一个之后那颗钮上写着 '{button.Text}'，该是「还没有生活模式」");
            if (!msg.Visible || !msg.Text.Contains("已删掉生活模式", StringComparison.Ordinal))
                checks.Fail($"删完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
            // **编号永不复用**（走的是真的删掉那一条路，不是只测名单那一个函数）：
            // 紧接着新存一个，它拿的号要比刚删掉的那个大。
            PressItem(popup, "存为新生活模式");
            text.Text = "接着来的";
            ok.EmitSignal(Button.SignalName.Pressed);
            var fresh = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            if (fresh.Entries.Count != 1) checks.Fail($"删完之后新存的该有一条，实际 {fresh.Entries.Count} 条");
            if (fresh.Entries.Count == 1 && LifeModeNumbers.TryNumberIn(fresh.Entries[0].File, out var nowNumber)
                && nowNumber <= wasNumber)
                checks.Fail($"删掉 {wasFile}（{wasNumber} 号）之后新发的号是 {fresh.Entries[0].File} —— 号被复用了一次");
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();

        /// 一份配置文档里那个键的值。<b>读的是文档，不是内存里那几个属性</b> ——
        /// 「切换」那一档量的正是「磁盘上那份里有没有那个没保存的值」。
        static System.Text.Json.Nodes.JsonNode? At(string path, string key)
        {
            if (!System.IO.File.Exists(path)) return null;
            if (!JsoncDoc.TryParse(System.IO.File.ReadAllText(path), out var doc, out _) || doc == null) return null;
            System.Text.Json.Nodes.JsonNode? at = doc.Values;
            foreach (var part in key.Split('.'))
            {
                if (at is not System.Text.Json.Nodes.JsonObject node || !node.TryGetPropertyValue(part, out at)) return null;
            }
            return at;
        }
    }

    // ---- 窗上那几格「摆着什么」的两把尺子（flows-menu / flows-takein 两组共用）--------
    //
    // **类上的方法而不是某一组里的局部函数**：两组都要问「域外那几条此刻在窗上摆着什么」
    // （第一组量切换，第二组量「收进来的那一份里带着系统那几条时切过去也不许动」），
    // 抄第二份就会有一份悄悄过期 —— 同 <see cref="PressItem"/> 那一族被抬到类上的理由。

    /// 界面上那一格现在摆的是什么 —— 问的是**屏幕上那一个**，不是副本里的值
    /// （下拉摆的是翻过的字，所以拿语言表里那一条来比；开关摆的是勾没勾上）。
    private static string Shown(Options win, string key) => win._byKey[key].Value switch
    {
        OptionButton option when option.Selected >= 0 => option.GetItemText(option.Selected),
        BaseButton toggle => toggle.ButtonPressed ? "按下" : "没按下",
        SpinBox box => box.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "",
    };

    /// 那一条键的<b>值</b>摆上窗该是什么样。选值那一档走语言表（表里那个键就是取值本身）。
    private static string ShownFor(Settings settings, string key) => settings.Map.Get(key) switch
    {
        string choice => TranslationServer.Translate(choice).ToString(),
        bool flag => flag ? "按下" : "没按下",
        _ => "",
    };

    /// 域外那几条（<b>从声明表的域问出来</b>，不是问 <see cref="LifeModeStore.InLifeMode"/>）
    /// 此刻在窗上摆着什么。写成 <c>!InLifeMode(d)</c> 的话，域界线一坏这一份就成了空的，
    /// 下面那几条判据一起空过 —— 被测的行为不能拿来当判据的事实那一半。
    private static Dictionary<string, string> SystemRows(Options win) =>
        Settings.Declarations
            .Where(d => !d.IsContainer && d.Domain == Settings.SystemDomain && win._byKey.ContainsKey(d.Key))
            .ToDictionary(d => d.Key, d => Shown(win, d.Key));

    /// <summary>「读不通有三档，各说各的」那一组（组名 <c>flows-unreadable</c>）。
    ///
    /// 三档是三件事，判据要能<b>把它们分别认出来</b>，所以每一档两头夹：
    /// <list type="bullet">
    /// <item><b>名单读不通</b>（<see cref="LifeModeStore.Stop.ListUnreadable"/>）：那一族当成没有，
    /// 窗头那颗钮写的是「名单读不通」而不是「还没有生活模式」，状态栏说的也是<b>名单</b>（不是
    /// 整个配置坏了 —— 活的那一份这时读得通），而<b>磁盘上那一份一个字节都没动</b>。</item>
    /// <item><b>某一个生活模式那一份读不通</b>（<see cref="LifeModeStore.Stop.LifeModeUnreadable"/>）：
    /// 不切它，状态栏<b>点名</b>是哪一个，<b>别的生活模式照常能切</b>。</item>
    /// <item><b>名字在名单里而文件不在</b>（<see cref="LifeModeStore.Stop.NoFile"/>）：照实说，
    /// <b>不静悄悄补一个默认</b>（那个文件事后仍然不在，名单也没有多一条）。</item>
    /// </list>
    ///
    /// <b>每一档都配一条「一定该通」的对照，而且对照排在判据前面</b>
    /// （<c>CODING_STANDARDS.md</c> 第 5 条）。那一半是这一组的关键：把一份<b>好的</b>放进去，
    /// 证明这一组量的真是「读不通那件事」，不是「生活模式族空不空」—— 一个只会说「没有生活模式」
    /// 的实现，01/02 那一版就是绿的（自检全绿，而界面上把一份坏名单说成了「还没有生活模式」）。
    ///
    /// <b>三句话不许塌成一</b>：三档那句话的<b>译文</b>互不相同、互不为子串，而每一档还断言
    /// 另外两档那句话<b>没有</b>出现在同一行里；再加上 <see cref="LifeModeStore.Stop"/> 是三个
    /// 分开的枚举值（事实那一半），于是「都变成同一句话」这一种改法当场就红。
    ///
    /// 这一组只跑 <c>.tmp</c> 里自己那个用完就删的子目录，一条都不碰 <c>%APPDATA%</c>
    /// （<c>CODING_STANDARDS.md</c>「写进文档的东西」那一节）；<c>finally</c> 里窗和目录都放。</summary>
    public static List<string> LifeModeUnreadableSelfTest(Node parent)
    {
        var checks = new Checks();
        // 外层那个 host 只管 `root` 这个目录；**每一档的窗走 `ForDir`**（那一档在
        // `root` 底下自己的子目录上），所以「放掉某一档」放的是那一档的 host。
        var host = OptionsHost.InTempDir("mode-unreadable");
        var root = host.Dir;
        // **每一档一颗自己的窗**：窗 `_Ready` 里读一次名单，而「把某一份弄坏」发生在开窗
        // **之前**（名单）或开窗**之后**（某一个生活模式那一份）—— 同一扇窗量不了两件事。
        // 所以这一组有**若干个** host 挂在手上，放掉一档就是 `Dispose` 那一个。
        var opened = new List<OptionsHost>();

        Options Open(string dir, SettingsStore.Read state)
        {
            var one = OptionsHost.ForDir(dir);
            opened.Add(one);
            return one.Open(state, parent);
        }

        void Drop()
        {
            foreach (var one in opened) { one.Dispose(); checks.Problems.AddRange(one.Problems); }
            opened.Clear();
        }

        try
        {
            // 词从语言表来（`CODING_STANDARDS.md` 第 6 条），语言也钉在中文 ——
            // 不然这一组量的字取决于上一组把语言停在哪一门。
            if (Lang.Apply("zh").Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];
            string Said(string key, string name = "") =>
                Fill(TranslationServer.Translate(key).ToString(), name);
            var saidList = Said("生活模式名单读不通，一个生活模式也没认出来；磁盘上那一份没有动过");
            var saidFile = Said("生活模式「{0}」读不通，所以没有切", "坏掉的那个");
            var saidGone = Said("名单里有「{0}」，可它那一份已经不在了 —— 没有替它补上一个", "没文件的那个");

            // 三档那句话：互不相同、互不为子串（**塌成一句就红**），也不许带配置文件内部的名字。
            var said = new[] { saidList, saidFile, saidGone };
            for (int i = 0; i < said.Length; i++)
                for (int j = 0; j < said.Length; j++)
                    if (i != j && said[i].Contains(said[j], StringComparison.Ordinal))
                        checks.Fail($"三档读不通那句话里有两句是同一句（第 {i + 1} 句包含第 {j + 1} 句）—— " +
                                     $"它们要各说各的：'{said[i]}'");
            foreach (var text in said.Append(TranslationServer.Translate("生活模式名单读不通").ToString()))
                foreach (var (word, cjk, _, _) in ForbiddenWords())
                    if (LeaksInternalName(text, word, cjk))
                        checks.Fail($"新加的那一句里有配置文件内部的名字「{word}」：'{text}'");
            // **「名单读不通」不等于「整个配置坏了」**：那两句话必须说得清是哪一样坏了。
            if (saidList == saidFile || saidList == saidGone || saidFile == saidGone)
                checks.Fail("三档读不通说的是同一句话");

            checks.Check("对照（好的一发放进去）：名单读得通、生活模式那一份读得通、名字在而文件在 —— 三档一句都不该说", () =>
            {
                var dir = System.IO.Path.Combine(root, "fine");
                System.IO.Directory.CreateDirectory(dir);
                var baseOne = Seed(dir);                     // 活的那一份 + 一个「好的」生活模式
                var state = SettingsStore.Load(dir);
                if (state.Error != null) throw new Exception($"底稿读不通：{state.Error}");

                // **对照要走的是判据量的那一条路**：同样的调用，同样的话，只是**没有弄坏**。
                if (LifeModeStore.StandingOf(dir, baseOne).At != LifeModeStore.Standing.In)
                    throw new Exception("刚存下的那一个没有被认出来，这一组量的不是读不通那件事");
                if (!LifeModeStore.SwitchTo(dir, state, "好的那个").Ok)
                    throw new Exception("读得通的那一个切不过去 —— 对照没立住，三档那几条可能只是在说「什么都没有」");
                var win = Open(dir, SettingsStore.Load(dir));
                if (Said("生活模式：{0}", "好的那个") != win.GetNode<MenuButton>(LifeModePath).Text)
                    throw new Exception($"对照：名单读得通时那颗钮上写着 '{win.GetNode<MenuButton>(LifeModePath).Text}'");
                Drop();
            });

            checks.Check("名单读不通：当成没有、说的是「名单」、磁盘上那一份一个字节没动", () =>
            {
                var dir = System.IO.Path.Combine(root, "list");
                System.IO.Directory.CreateDirectory(dir);
                Seed(dir);
                var listPath = LifeModeStore.ListPathOf(dir);
                var modesDir = System.IO.Path.GetDirectoryName(listPath)!;
                // 弄坏的是**名单**（截断），而生活模式那几份原封不动 —— 这一发量的正是「哪一份坏了」。
                System.IO.File.WriteAllText(listPath, "{ \"entries\": [ { \"name\": \"半截\", ");
                var before = System.IO.File.ReadAllBytes(listPath);
                var filesBefore = System.IO.Directory.GetFileSystemEntries(modesDir)
                    .Select(System.IO.Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();

                // 事实那一半：那一族当成没有，但**不落成「不在任何生活模式里」**（那是一档
                // 「你还站在别的什么地方」，不是「这一族没法用」）。
                if (!LifeModeIndex.Load(listPath).Unreadable) throw new Exception("截断的名单没有立旗");
                if (LifeModeStore.StandingOf(dir, new Settings()).At != LifeModeStore.Standing.ListUnreadable)
                    throw new Exception("名单读不通，却报成了「还没有生活模式」—— 那就是把一份坏文件说成一个生活模式都没有");
                var (error, index) = LifeModeStore.SaveAs(dir, "新的", new Settings());
                if (error == null) throw new Exception("名单读不通还是存进去了");
                if (index != null) throw new Exception("名单读不通，名单却还回来了");
                if (LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "好的那个").Stopped != LifeModeStore.Stop.ListUnreadable)
                    throw new Exception("名单读不通，切过去报的却不是「名单读不通」那一档");

                // **一个字节都没动**：对的是**字节**，不是「值没变」；而目录里也不许多出文件
                // （多一个 `.bak` 就是「开窗顺手把它盖了」，多一份 `0002.jsonc` 就是补了个默认）。
                if (!System.IO.File.ReadAllBytes(listPath).SequenceEqual(before))
                    throw new Exception("名单读不通那一份被动了 —— 契约要的是一个字节都不动");
                var filesAfter = System.IO.Directory.GetFileSystemEntries(modesDir)
                    .Select(System.IO.Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
                if (!filesAfter.SequenceEqual(filesBefore))
                    throw new Exception($"读不通那一次目录里多/少了东西：{string.Join("、", filesBefore)} → {string.Join("、", filesAfter)}");

                // 界面上那两句：钮上写「名单读不通」，状态栏说的是**名单**。
                var win = Open(dir, SettingsStore.Load(dir));
                var button = win.GetNode<MenuButton>(LifeModePath).Text;
                if (button != Said("生活模式名单读不通"))
                    throw new Exception($"名单读不通，那颗钮上写着 '{button}'，该是「生活模式名单读不通」");
                var msg = win.GetNode<RichTextLabel>("Margin/Stack/Msg");
                if (!msg.Visible || !msg.Text.Contains(saidList, StringComparison.Ordinal))
                    throw new Exception($"名单读不通，状态栏没把名单那句话说清：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
                foreach (var other in new[] { saidFile, saidGone })
                    if (msg.Text.Contains(other, StringComparison.Ordinal))
                        checks.Fail($"名单读不通那一档把别的档那句话也说了：'{msg.Text}'");
                // **说的是名单，不是整个配置坏了**：活的那一份这时读得通，那条常驻的横幅
                // （「磁盘上那份读不通」）不该露出来 —— 它说的是**配置**。
                if (win.State!.Error != null) throw new Exception("这一发量的是配置读不通那件事，不是名单那件事");
                if (win.GetNode<Label>("Margin/Stack/Header/Error").Visible)
                    throw new Exception("名单读不通，却举着「磁盘上那份读不通」那条横幅 —— 说错了是哪一份坏了");
                // 菜单上半**当成没有**（一条都没有），而下半那几条我们自己的措辞还在 ——
                // 那是「这一族现在用不了」，不是「你一个生活模式都没有，所以什么都干不了」。
                if (HasItem(win.GetNode<MenuButton>(LifeModePath).GetPopup(), "好的那个"))
                    throw new Exception("名单读不通，菜单上半却还摆着那个名字 —— 那一族该当成没有");
                if (!HasItem(win.GetNode<MenuButton>(LifeModePath).GetPopup(), "存为新生活模式"))
                    throw new Exception("名单读不通，菜单下半自己那几条不见了");
                Drop();
            });

            checks.Check("某一个生活模式那一份读不通：不切它、点名说是哪一个、别的照常能切", () =>
            {
                var dir = System.IO.Path.Combine(root, "one-bad");
                System.IO.Directory.CreateDirectory(dir);
                var good = Seed(dir);                          // 「好的那个」
                var broken = good.Clone();                    // 「坏掉的那个」：域内拧几个值
                broken.FadeInMs = 9_000;
                broken.ActivityTimeoutMin = 33;
                broken.Calendar.Corner = "bottomLeft";
                if (LifeModeStore.SaveAs(dir, "坏掉的那个", broken).Error != null) throw new Exception("存第二个没成");
                var entry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Find("坏掉的那个")
                            ?? throw new Exception("名单里没有刚存的那一条");
                var badPath = LifeModeStore.PathOf(dir, entry);
                var intact = System.IO.File.ReadAllText(badPath);   // 留着：对照那一下要把它放回去
                System.IO.File.WriteAllText(badPath, $"{{ \"{Settings.VersionKey}\": {Settings.SchemaVersion}, \"fadeInMs\": 9");   // 少一个 }
                var livePath = SettingsStore.FilePath(dir);
                var before = System.IO.File.ReadAllBytes(livePath);

                // 事实那一半：只有**那一个**是 LifeModeUnreadable，而**另一个**切得过去。
                var ontoBad = LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "坏掉的那个");
                if (ontoBad.Stopped != LifeModeStore.Stop.LifeModeUnreadable)
                    throw new Exception($"那一个读不通，报的却是 {ontoBad.Stopped?.ToString() ?? "成功"} —— " +
                                        $"三档没有分开（读不通 ≠ 文件不在 ≠ 名单读不通）");
                if (!System.IO.File.ReadAllBytes(livePath).SequenceEqual(before))
                    throw new Exception("没切成功，活的那一份却动了一字");
                var ontoGood = LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "好的那个");
                if (!ontoGood.Ok) throw new Exception($"**别的生活模式**也切不过去了：{ontoGood.Detail} —— 一个坏的不该拖累全部");
                if (SettingsStore.DiffOf(good, ontoGood.State!.Current) != "")
                    throw new Exception("切过去了，而窗里那一份不是那个生活模式里的值 —— 这一发量的不是「切得成」");
                // **对照**：那一个文件被修好之后，同一个调用就通了（实测结果说「不通」时先怀疑自己的测量程序）。
                System.IO.File.WriteAllText(badPath, intact);
                if (!LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "坏掉的那个").Ok)
                    throw new Exception("把那个文件修好之后同一个调用仍然不通 —— 这一发的测量程序有问题，不是被测的那件事");
                System.IO.File.WriteAllText(badPath, $"{{ \"{Settings.VersionKey}\": {Settings.SchemaVersion}, \"fadeInMs\": 9");

                // 界面上那一句：**点名**是哪一个。
                var win = Open(dir, SettingsStore.Load(dir));
                var popup = win.GetNode<MenuButton>(LifeModePath).GetPopup();
                var button = win.GetNode<MenuButton>(LifeModePath).Text;
                var msg = win.GetNode<RichTextLabel>("Margin/Stack/Msg");
                PressSwitchFor(popup, "坏掉的那个");
                if (!msg.Visible || !msg.Text.Contains(saidFile, StringComparison.Ordinal))
                    throw new Exception($"按了那一个读不通的，状态栏没点名说是哪一个：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
                foreach (var other in new[] { saidList, saidGone })
                    if (msg.Text.Contains(other, StringComparison.Ordinal))
                        checks.Fail($"「那一个读不通」那一档把别的档那句话也说了：'{msg.Text}'");
                if (win.GetNode<MenuButton>(LifeModePath).Text != button)
                    throw new Exception($"没切成功，那颗钮上的字却变了：'{button}' → '{win.GetNode<MenuButton>(LifeModePath).Text}'");
                // **别的生活模式照常能切**（界面上那一次，不只是 store 那一层）。
                PressSwitchFor(popup, "好的那个");
                if (win.GetNode<MenuButton>(LifeModePath).Text != Said("生活模式：{0}", "好的那个"))
                    throw new Exception($"坏掉一个之后，界面上切另一个切不过去：'{win.GetNode<MenuButton>(LifeModePath).Text}'");
                if (!msg.Text.Contains("已切到生活模式", StringComparison.Ordinal))
                    throw new Exception($"切成了却没说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
                Drop();
            });

            checks.Check("名字在名单里而文件不在：照实说，不静悄悄补一个默认", () =>
            {
                var dir = System.IO.Path.Combine(root, "no-file");
                System.IO.Directory.CreateDirectory(dir);
                var good = Seed(dir);
                var missing = good.Clone();
                missing.FadeInMs = 9_000;
                if (LifeModeStore.SaveAs(dir, "没文件的那个", missing).Error != null) throw new Exception("存第二个没成");
                var entry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Find("没文件的那个")
                            ?? throw new Exception("名单里没有刚存的那一条");
                var gone = LifeModeStore.PathOf(dir, entry);
                var kept = System.IO.File.ReadAllText(gone);   // 留着：对照那一下要把它放回去
                System.IO.File.Delete(gone);                  // 文件没了，名单上那一条还在
                var livePath = SettingsStore.FilePath(dir);
                var before = System.IO.File.ReadAllBytes(livePath);

                // 事实那一半：那一档**不是** LifeModeUnreadable（它不是「读不通」，是压根不在），
                // 而<b>不静悄悄补一个默认</b>：事后那个文件仍然不在，名单也没有多一条。
                var onto = LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "没文件的那个");
                if (onto.Stopped != LifeModeStore.Stop.NoFile)
                    throw new Exception($"名字在而文件不在，报的却是 {onto.Stopped?.ToString() ?? "成功"} —— " +
                                        $"它不是「读不通」那一档，说错了就等于把坏和丢混成一件");
                if (!System.IO.File.ReadAllBytes(livePath).SequenceEqual(before))
                    throw new Exception("没切成功，活的那一份却动了一字");
                if (System.IO.File.Exists(gone))
                    throw new Exception("那个文件不在，程序却补了一个出来 —— 契约要的是照实说，不补");
                if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Find("没文件的那个")?.File != entry.File)
                    throw new Exception("那一次动过名单上那一条");
                // **对照**：把那个文件放回去之后，同一个调用就通了（实测结果说「不通」时先怀疑
                // 自己的测量程序 —— 「文件不在」和「读不通」是两个不同的失败，这一发分的正是它）。
                System.IO.File.WriteAllText(gone, kept);
                if (!LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "没文件的那个").Ok)
                    throw new Exception("把那个文件放回去之后同一个调用仍然不通 —— 这一发的测量程序有问题");
                System.IO.File.Delete(gone);
                if (!LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "好的那个").Ok)
                    throw new Exception("**别的**生活模式也切不过去了 —— 一个丢的不该拖累全部");

                var win = Open(dir, SettingsStore.Load(dir));
                var popup = win.GetNode<MenuButton>(LifeModePath).GetPopup();
                var button = win.GetNode<MenuButton>(LifeModePath).Text;
                var msg = win.GetNode<RichTextLabel>("Margin/Stack/Msg");
                PressSwitchFor(popup, "没文件的那个");
                if (!msg.Visible || !msg.Text.Contains(saidGone, StringComparison.Ordinal))
                    throw new Exception($"按了那个文件不在的，状态栏没照实说：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
                foreach (var other in new[] { saidList, saidFile })
                    if (msg.Text.Contains(other, StringComparison.Ordinal))
                        checks.Fail($"「文件不在」那一档把别的档那句话也说了：'{msg.Text}'");
                if (win.GetNode<MenuButton>(LifeModePath).Text != button)
                    throw new Exception($"没切成功，那颗钮上的字却变了：'{button}' → '{win.GetNode<MenuButton>(LifeModePath).Text}'");
                if (System.IO.File.Exists(gone)) checks.Fail("界面上按了那么一下，那个文件被补出来了");
                PressSwitchFor(popup, "好的那个");
                if (win.GetNode<MenuButton>(LifeModePath).Text != Said("生活模式：{0}", "好的那个"))
                    throw new Exception("丢了一个之后，界面上切另一个切不过去");
                Drop();
            });
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            Drop();
            // 外层那个 host 放的是 `root` 整个目录，删不掉的话它自己说 ——
            // 原来这里是这一组手写的 `try { Delete } catch (IOException)`，九处收尾
            // 里只有这一处带实测结果（其余八处静悄悄吞掉，见 `OptionsHost.Dispose` 那段）。
            host.Dispose();
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();

        /// <summary>一份底稿 + 一个叫「好的那个」的生活模式，磁盘上留下三样东西（活文档、
        /// 名单、生活模式那一份）。<b>域外那三条给成非默认值</b>：留在默认值上时，任何一条
        /// 「切过去之后它没变」的判据都量不出东西来。</summary>
        Settings Seed(string dir)
        {
            var settings = new Settings();
            settings.ActivityTimeoutMin = 40;
            settings.FadeInMs = 3_000;
            settings.Calendar.Corner = "topRight";
            settings.Calendar.Enabled = false;
            settings.Language = "en";
            settings.UiTheme = "dark";
            settings.Autostart = true;
            SettingsStore.Save(dir, new SettingsStore.Read { Current = settings });
            if (LifeModeStore.SaveAs(dir, "好的那个", settings).Error != null)
                throw new Exception("存那个好的生活模式没成");
            return settings;
        }
    }

    /// <summary>「从一份文件收一个生活模式进来」那一组（组名 <c>flows-takein</c>，工单 05）。
    ///
    /// 这一族是<b>第三件事</b>，不是「换个皮儿的导入」：导出仍是那份文档本身、导入仍是整个
    /// 换成活的并留备份，两颗钮的语义一个字没动（最后一档就量这个）。收进来的那一档
    /// <b>只碰 <c>flows/</c> 底下那两样</b>。
    ///
    /// 判据分几段，各配对照（<c>CODING_STANDARDS.md</c> 第 5 条、第 8 条）：
    /// <list type="bullet">
    /// <item><b>收进来的那一刻不顶掉当前配置</b>（承重处）：活的那一份<b>逐字节</b>没动、
    /// <b>没有多出备份</b>（收进来<b>不是</b>导入，不走 <see cref="SettingsStore.Import"/>），
    /// 而窗头那颗钮<b>还写着原来那个生活模式</b>。<b>对照</b>：切过去之后那颗钮<b>才</b>换成
    /// 收进来的那一个，域内那一格也跟着换了 —— 没有这两条，「没变」是「什么都没动」的那种假绿。</item>
    /// <item><b>导出一个生活模式再收回来，逐字节相同</b>：搬进来那一份和给进来的那一份
    /// <c>ReadAllBytes</c> 一样长、一样一个字节，而且<b>来源那一份自己没被动</b>。
    /// 注释和陌生键另问一次「那一行还在不在」，因为长度一样也可能内容不一样。</item>
    /// <item><b>名单和磁盘上那份文件对得上</b>：名单上每一条指的那个文件都在、收进来的
    /// 那一条拿的号比已有的都大、原来那一条在名单上原样留着。</item>
    /// <item><b>重名走同一道判据</b>（和「存为新生活模式」撞名一样）：当场拦住、小窗不关、
    /// 那句话<b>指向「盖回」</b>、名单一条不多、撞上那一份一个字节没动、目录里没多出文件。
    /// <b>对照</b>：换个没撞的名字，同一个调用就通。</item>
    /// <item><b>导出那一族的语义没变</b>：在<b>自己那个子目录</b>里手写一份文档（注释 +
    /// 陌生键 + 尾逗号），导出仍是<b>那份文档本身</b>，导入换掉活设置并留备份、备份就是
    /// 导入之前那一份。收进来这一族加进来前后都是这样。</item>
    /// </list>
    ///
    /// 这一组只跑 <c>.tmp</c> 里自己那个用完就删的子目录，一条都不碰 <c>%APPDATA%</c>
    /// （<c>CODING_STANDARDS.md</c>「写进文档的东西」那一节）；<c>finally</c> 里窗和目录都放。</summary>
    public static List<string> LifeModeTakeInSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("mode-takein");
        var dir = host.Dir;
        var utf8 = new System.Text.UTF8Encoding(false);
        const string inName = "写作";       // 先在的那一个（收进来的那一个要拿它当对照）
        const string hadName = "长文";      // 名单上已经有的那一个
        const string tookName = "收进来的";
        // 域内那一格：切过去之后它该换成收进来的那一份的值。**对照的对照** ——
        // 没有它，上面那几条「域外那三条没变」是「整份都没换」的那种相等（同 flows-menu
        // 那一组里同一条理由）。键名是**这一组选的**那一格，不是「域内所有键」的一份清单。
        const string turned = "activityTimeoutMin";
        // 收进来的那一份：**另一套**配置，域内域外都给成和这台机器不一样的值。
        var other = new Settings();
        other.ActivityTimeoutMin = 12;
        other.FadeInMs = 9_000;
        other.Flow = "pomodoro";
        other.Language = "zh";
        other.UiTheme = "light";
        other.Autostart = false;
        // 名单上**已经有的**那一个：值和这台机器、和 <see cref="other"/> 都不同。
        // 域内域外都换过一遍，是为了让「切过去之后钮上换了一个名字」这一档有东西可比。
        var third = new Settings();
        third.ActivityTimeoutMin = 21;
        third.FadeInMs = 5_000;
        third.Flow = "todoList";
        third.Language = "zh";
        third.UiTheme = "light";
        third.Autostart = false;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            // 活的那一份：域内域外都给成**非默认值**。域外那三条留在默认值上时，
            // 「切过去之后它们没变」什么都量不出来（那是 flows-menu 已经踩过的坑）。
            var live = new Settings();
            live.ActivityTimeoutMin = 40;
            live.FadeInMs = 3_000;
            live.Language = "en";
            live.UiTheme = "dark";
            live.Autostart = true;
            if (SettingsStore.Save(dir, new SettingsStore.Read { Current = live }).Error != null)
                return ["活的那一份没写成，这一组自己的底稿就没了"];
            if (Lang.Apply("zh").Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];

            // 两个生活模式**在开窗之前**就存好：窗 `_Ready` 里读一次名单，晚存的话那颗钮上写的是
            // 「还没有生活模式」，而「收进来的那一刻钮上写的还是原来那个」这一档就量不到了。
            if (LifeModeStore.SaveAs(dir, inName, live).Error != null) return [$"存那个原来的生活模式「{inName}」没成"];
            if (LifeModeStore.SaveAs(dir, hadName, third).Error != null) return [$"存那个已有的生活模式「{hadName}」没成"];

            // ---- 「别人给的那一份」：一份**导出来的**完整配置文档 ------------------------
            // 走 <see cref="SettingsStore.Export"/>：生活模式那一份和导出那一份<b>是同一个格式、
            // 同一个渲染器</b>（设计文档 `docs/design/life-mode.md`「一个生活模式 = 一份完整的配置文档」），而「完整」是承重的
            // —— 手搓一份只带几个键的，切过去之后和别的生活模式逐键比不上，窗头那颗钮会写
            // 「不在任何生活模式里」，那一档就量不出「切到的是收进来的那一个」。
            //
            // 值给成 <see cref="other"/>：和这台机器不一样，也和名单上那个已有的不一样。
            // **两个一模一样的生活模式钮上分不出谁是谁**（StandingOf 返回名单里第一个对上的），
            // 而「从别人那儿拿到一份配置」的现实情形本来就是一份你还没有的配置。
            var foreign = System.IO.Path.Combine(dir, "from-somebody-else.jsonc");
            var state = SettingsStore.Load(dir);
            if (state.Error != null) return [$"活的那一份读不通：{state.Error}"];
            if (SettingsStore.Export(dir, state with { Current = other }, foreign) != null)
                return ["把「别人给的那一份」导出来没成"];
            // 在**根对象的左括号之后**加一行注释和一个 Keeper 没听过的键。渲染器写出来的
            // 那一份每个键上面本来就有两行注释，所以要量的是**用户自己加的这一行** ——
            // 它不在任何声明里，渲染器永远不会写它（不判这一条，「逐字节相同」和「原样搬」
            // 量的就是同一件事，而一个只搬键不搬注释的实现照样过）。
            // 插在开头而不是末尾：末尾那一条带行尾注释（`// 5–60`）和嵌套对象，
            // 在那儿补逗号要靠猜「最后一行是不是已经有逗号」。**第一个 `{` 就是根对象的
            // 那个**——渲染器写出的文件头只有 `//` 注释（`SettingsDoc.Render` 先 Header 后 `{`）。
            var carried = System.IO.File.ReadAllText(foreign);
            var root = carried.IndexOf('{');
            if (root < 0) return ["导出的那一份里没有根对象，量不了「原样搬」"];
            // 作息表**塞进渲染器已经写出来的那一张里**（不是另加一个 `routine` 键 —— 那会造出
            // 一个重名的键，而后一个把前一个盖掉，读出来是空表，于是这一条量的是「后写的赢」）。
            // 键名从声明表问（`Settings.BlocksKey` 是带前缀那一份，文档里是裸的叶子名）。
            var blocksLeaf = Settings.BlocksKey[(Settings.BlocksKey.LastIndexOf('.') + 1)..];
            var empty = $"\"{blocksLeaf}\": []";
            var at = carried.IndexOf(empty, StringComparison.Ordinal);
            if (at < 0) return [$"导出的那一份里没有 {empty} 那一行，量不了「原样搬作息表」"];
            // **再塞一张作息表进去**（工单 05 把这把尺子扩展到**预设模板**那一种形状）：
            // 预设生活模式就是「一份完整的配置文档 + 作息表 + 逐块出处」（设计文档 `docs/design/preset-life-mode.md`
            // 「预设生活模式是内置的只读模板，收进来才算自己的」那一节），而 01 给表定的键是 `routine.enabled` / `routine.blocks`
            // 加块行那七个字段。所以「原样搬」这把尺子必须量得到**那一张表**也一起走了 ——
            // 只搬标量键而丢了一张表的实现，逐字节相同那一半是照样绿的。
            // 时刻算出来（`Routine.MinuteOfDay` 那一族，**不抄 90 / 180**），块行那七个字段的
            // 键名从声明表问（`Settings.BlockDeclarations` 走 `TimeBlock.KeyPrefix` 那个前缀），
            // 而「睡眠」那一个种类值也问声明表 —— 种类是自由文本，可它是这一族唯一带行为语义
            // 的那一个，所以这里用的是表上那一份，而不是手抄的一个词。
            var blockName = "收进来的那一块";
            string BlockKey(string field) => Settings.Decl(TimeBlock.KeyPrefix + field).Key;
            // 两处编辑，各有各的理由：表**就地填**（渲染器写出来的那一行 `[]` 就是那张表的空值），
            // 注释和陌生键**加在根对象的左括号之后**。**不另加一个 `routine` 键** —— 那会造出
            // 一个重名的键，后一个把前一个盖掉，而读出来是空表（量到的是「后写的赢」）。
            var withRoutine = carried.Remove(at, empty.Length).Insert(at,
                $"\"{blocksLeaf}\": [\n      {{ " +
                $"\"{BlockKey("startMinute")}\": {Routine.MinuteOfDay(new DateTime(2026, 1, 1, 1, 30, 0))}, " +
                $"\"{BlockKey("endMinute")}\": {Routine.MinuteOfDay(new DateTime(2026, 1, 1, 3, 0, 0))}, " +
                $"\"{BlockKey("name")}\": \"{blockName}\", " +
                $"\"{BlockKey("kind")}\": \"{Settings.BlockKindSleep}\", " +
                $"\"{BlockKey("fade")}\": false, " +
                $"\"{BlockKey("flow")}\": \"\", " +
                $"\"{BlockKey("yieldsToInput")}\": true }}\n    ]");
            System.IO.File.WriteAllText(foreign, withRoutine.Insert(root + 1, "\n" +
                "  // 别人那份上的注释，收进来之后不许没\n" +
                "  \"aKeyIKnowNothingAbout\": { \"keep\": true },"), utf8);

            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死

            var button = win.GetNode<MenuButton>(LifeModePath);
            var popup = button.GetPopup();
            var pick = win.GetNode<FileDialog>("Pick");
            var prompt = win.GetNode<Window>(NamePath);
            var text = win.GetNode<LineEdit>(NameTextPath);
            var why = win.GetNode<Label>(NameErrPath);
            var ok = win.GetNode<Button>(NameOkPath);
            var msg = win.GetNode<RichTextLabel>("Margin/Stack/Msg");
            var livePath = SettingsStore.FilePath(dir);

            // 按**菜单上写着的那一句**走，词取自语言表（CODING_STANDARDS.md 第 6 条）。
            if (!HasItem(popup, "从一份文件收一个生活模式进来…"))
                return ["菜单下半没有「从一份文件收一个生活模式进来…」那一一项"];

            // ---- 走真的那一条：菜单 → 文件框 → 问名字 ---------------------------------
            PressItem(popup, "从一份文件收一个生活模式进来…");
            if (!pick.Visible) checks.Fail("按了「从一份文件收一个生活模式进来…」，文件框没开起来");
            if (prompt.Visible) checks.Fail("文件还没挑，起名那扇小窗就开起来了 —— 次序反了");
            // 文件框那半步交给它自己发的那一个信号（和真鼠标点出来的是同一条接线）。
            win.Choose(foreign);
            if (!prompt.Visible) checks.Fail("挑完了文件，起名那扇小窗没开起来");
            if (text.Text.Length != 0)
                checks.Fail($"收进来的那一档预填了 '{text.Text}' —— 这一趟要起的是一个新名字");
            if (ok.Text != TranslationServer.Translate("收进来").ToString())
                checks.Fail($"那一颗确定钮上写着 '{ok.Text}'，该是「收进来」");

            // ---- 收进来的那一刻：不顶掉当前配置（承重处） -----------------------------
            var liveBefore = System.IO.File.ReadAllBytes(livePath);
            var whereBefore = button.Text;
            var machine = SystemRows(win);   // 域外那几条此刻在窗上摆着的（收之前）
            if (whereBefore != TranslationServer.Translate("生活模式：{0}").ToString().Replace("{0}", inName))
                checks.Fail($"动手之前那颗钮上写着 '{whereBefore}'，该是「生活模式：{inName}」");
            text.Text = tookName;
            ok.EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("收成了，起名那扇小窗还开着");

            var index = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            var entry = index.Find(tookName);
            var tookText = "";
            if (entry == null) checks.Fail("收完了，名单里没有刚收进来的那一条");
            else
            {
                // 名单和磁盘上那份文件对得上
                foreach (var one in index.Entries)
                    if (!System.IO.File.Exists(LifeModeStore.PathOf(dir, one)))
                        checks.Fail($"名单上「{one.Name}」指的那个文件不在磁盘上");
                if (!LifeModeNumbers.TryNumberIn(entry.File, out var tookNumber))
                    checks.Fail($"{entry.File} 读不回成号");
                else
                    foreach (var sibling in index.Entries.Where(o => o.File != entry.File))
                        if (LifeModeNumbers.TryNumberIn(sibling.File, out var otherNumber) && otherNumber >= tookNumber)
                            checks.Fail($"收进来的那一份拿的是 {entry.File}，不大于 {sibling.File}");
                if (index.Find(inName)?.Name != inName) checks.Fail("收进来之后，原来那一条在名单上没原样留着");

                // 逐字节相同：搬进来的和给进来的那一份一个字节一样
                var tookBytes = System.IO.File.ReadAllBytes(LifeModeStore.PathOf(dir, entry));
                var foreignBytes = System.IO.File.ReadAllBytes(foreign);
                if (!tookBytes.SequenceEqual(foreignBytes))
                    checks.Fail($"收进来的那一份和给进来的那一份不是逐字节相同：" +
                                 $"{tookBytes.Length} 字节 vs {foreignBytes.Length} 字节");
                tookText = System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, entry));
                // 注释和陌生键跟着走 —— 「逐字节相同」之外再问一次「那一行还在不在」，
                // 因为长度一样也可能内容不一样（不判这一条，长度判据会掩护它）。
                // **第三样是那一张作息表**（工单 05 把这把尺子扩展到预设模板那一种形状）：
                // 预设就是「完整的配置文档 + 作息表 + 逐块出处」，所以「原样搬」必须量得到
                // 表里那个块名也一起走了 —— 块行那七个键是**嵌套**的，一个只搬标量键的实现
                // （逐键重建）照样能让上面那两条判据绿着。
                foreach (var kept in new[] { "// 别人那份上的注释，收进来之后不许没", "aKeyIKnowNothingAbout",
                                              blockName, BlockKey("startMinute") })
                    if (!tookText.Contains(kept, StringComparison.Ordinal))
                        checks.Fail($"收进来的那一份里没有 '{kept}' —— 原样搬不是搬了个空壳");
            }

            // 活的那一份一个字节没动，而且**没有多出备份**（收进来不是导入）
            if (!System.IO.File.ReadAllBytes(livePath).SequenceEqual(liveBefore))
                checks.Fail("收进来的那一刻活的那一份动了一字 —— 收进来不是导入");
            if (System.IO.File.Exists(SettingsStore.BackupPath(dir)))
                checks.Fail("收进来的那一趟留了一份备份 —— 收进来不是导入（导入才留备份）");
            // 窗头那颗钮**还写着原来那个生活模式**（收进来不切，切是另一个动作）
            if (button.Text != whereBefore)
                checks.Fail($"收进来之后那颗钮上从 '{whereBefore}' 变成了 '{button.Text}' —— 收进来的那一刻就顶掉了");
            if (win.Live.ActivityTimeoutMin != live.ActivityTimeoutMin)
                checks.Fail($"收进来之后活的那一份是 {win.Live.ActivityTimeoutMin}，该是收之前那一份");
            if (!msg.Visible || !msg.Text.Contains("已收下生活模式", StringComparison.Ordinal))
                checks.Fail($"收完没在状态栏说一声：'{(msg.Visible ? msg.Text : "（没露出来）")}'");
            if (!msg.Text.Contains("没动过", StringComparison.Ordinal))
                checks.Fail($"状态栏没说「你现在这份没动过」：'{msg.Text}'");
            if (!HasItem(popup, tookName)) checks.Fail("菜单上半没有刚收进来的那一个");

            // ---- 对照：切过去之后那颗钮**才**换（没有这一条，上面那几条是假绿） --------
            if (Where(popup, tookName) < 0) checks.Fail("菜单上半找不到刚收进来的那一个，切不过去");
            else
            {
                PressSub(popup, Where(popup, tookName), "切换");
                if (button.Text == whereBefore)
                    checks.Fail("切过去之后那颗钮上的字没变 —— 「没变」那一档量的不是「没动过」");
                if (button.Text != TranslationServer.Translate("生活模式：{0}").ToString().Replace("{0}", tookName))
                    checks.Fail($"切过去之后那颗钮上写着 '{button.Text}'，该是刚收进来的那一个");
                // 域内那一格确实跟着换了（对照的对照：整份换掉了，不是「什么都没动」）。
                if (((SpinBox)win._byKey[turned].Value).Value != 12)
                    checks.Fail("切过去之后窗上还摆着收之前那个数 —— 域内那一格没跟着换");
                // **那一张作息表也跟着换**（工单 05：预设模板的形状就是「完整的配置文档 + 作息表」，
                // 而标量那一格换对了不代表嵌套的那张表也换了 —— 这一条量的是「收进来的那张表」
                // 在活的那一份上真的读得出来，块名是它自己的那一个）。
                if (win.Live.Blocks.Count != 1 || win.Live.Blocks[0].Name != blockName)
                    checks.Fail($"切过去之后活的那一份里有 {win.Live.Blocks.Count} 个时间块" +
                                 $"{(win.Live.Blocks.Count > 0 ? $"，第一个叫「{win.Live.Blocks[0].Name}」" : "")}，" +
                                 $"该是收进来的那一份里那一个「{blockName}」");
                // **域外那三条一个字节都没动**：收进来的那一份里带着**不一样的**值，
                // 切过去它们照旧是这台机器自己的 —— 收进来那一侧不剪它们（剪了就毁掉
                // 逐字节相同），这一侧才是契约兑现的那一半。
                foreach (var (key, was) in machine)
                {
                    var now = SystemRows(win)[key];
                    if (now != was) checks.Fail($"切到收进来的那一个之后，窗上 {key} 从 '{was}' 变成了 '{now}'");
                    if (now != ShownFor(live, key))
                        checks.Fail($"切到收进来的那一个之后，窗上 {key} 是 '{now}'，该是这台机器自己的 '{ShownFor(live, key)}'");
                }
            }

            // ---- 重名走同一道判据（和「存为新生活模式」撞名一样） ---------------------------
            var second = System.IO.Path.Combine(dir, "another-one.jsonc");
            System.IO.File.WriteAllText(second, """
                {
                  "flow": "fadetop",
                  "activityTimeoutMin": 33,
                }
                """, utf8);
            var beforeCollision = NumberedFiles(dir);
            PressItem(popup, "从一份文件收一个生活模式进来…");
            win.Choose(second);
            text.Text = tookName;
            ok.EmitSignal(Button.SignalName.Pressed);
            if (!prompt.Visible) checks.Fail("撞了重名，起名那扇小窗却关了");
            if (!why.Visible || !why.Text.Contains("盖回", StringComparison.Ordinal))
                checks.Fail($"撞了重名，那句话没有指向「盖回」：'{(why.Visible ? why.Text : "（没露出来）")}'");
            if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Entries.Count != index.Entries.Count)
                checks.Fail("撞了重名，名单里却多了一条");
            if (entry != null && System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, entry)) != tookText)
                checks.Fail("撞了重名，被撞上的那一份被改了");
            if (NumberedFiles(dir) != beforeCollision)
                checks.Fail($"撞了重名，目录里却多出了一份（{beforeCollision} → {NumberedFiles(dir)} 份）");
            win.GetNode<Button>(NameCancelPath).EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("按了取消，那扇小窗还开着");

            // ---- 对照：换个没撞的名字，同一个调用就通（证明上面量的是「撞名」） -------
            PressItem(popup, "从一份文件收一个生活模式进来…");
            win.Choose(second);
            text.Text = "没撞名的那个";
            ok.EmitSignal(Button.SignalName.Pressed);
            if (prompt.Visible) checks.Fail("没撞名的名字也没收 —— 这一组量的不是「撞不撞名」，是「什么都收不进」");
            var last = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Find("没撞名的那个");
            if (last == null)
                checks.Fail("没撞名的那个没进名单（对照没通，上面那几条可能是「什么都收不进」的假绿）");
            else if (System.IO.File.ReadAllText(LifeModeStore.PathOf(dir, last)) != System.IO.File.ReadAllText(second))
                checks.Fail("收进来的那一份不是给进来的那一份（对照没通）");

            // ---- 导出那一族的语义没变（收进来这一族不许顺手改它） ---------------------
            // **自己那个子目录**：这一段要一份「带注释和陌生键的活文档」当底稿，而上面那
            // 几段的活文档是量「没动过」的那一把尺子，不能拿它来试导出。
            var ioDir = System.IO.Path.Combine(dir, "io");
            System.IO.Directory.CreateDirectory(ioDir);
            var handWritten = System.IO.Path.Combine(ioDir, Settings.FileName);
            System.IO.File.WriteAllText(handWritten, """
                {
                  // 我自己加的注释
                  "activityTimeoutMin": 5, /* and a block one */
                  "myOwnKey": { "keep": true },
                }
                """, utf8);
            var ioState = SettingsStore.Load(ioDir);
            if (ioState.Error != null) checks.Fail($"导出那一档的底稿读不通：{ioState.Error}");
            else
            {
                var outPath = System.IO.Path.Combine(ioDir, "out.jsonc");
                if (SettingsStore.Export(ioDir, ioState, outPath) != null) checks.Fail("导出没成");
                else
                    foreach (var kept in new[] { "// 我自己加的注释", "/* and a block one */", "myOwnKey" })
                        if (!System.IO.File.ReadAllText(outPath).Contains(kept, StringComparison.Ordinal))
                            checks.Fail($"导出的那一份里没有 '{kept}' —— 导出仍是那份文档本身");

                // 导入 = 整个换成活的，被换掉的留一份备份（**不是**收进来那一路）。
                var beforeImport = System.IO.File.ReadAllBytes(handWritten);
                var imported = SettingsStore.Import(ioDir, ioState, handWritten);
                if (imported.Error != null) checks.Fail("导入没成");
                else
                {
                    if (imported.State.Current.ActivityTimeoutMin != 5)
                        checks.Fail($"导入之后活的那一份是 {imported.State.Current.ActivityTimeoutMin}，该是导入进来的 5");
                    if (!System.IO.File.Exists(SettingsStore.BackupPath(ioDir)))
                        checks.Fail("导入没留下备份 —— 导入的语义被这一族改了");
                    else if (!System.IO.File.ReadAllBytes(SettingsStore.BackupPath(ioDir)).SequenceEqual(beforeImport))
                        checks.Fail("留下的那份备份不是导入之前那一份");
                }
            }
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();

        /// 目录里编号过的生活模式那一份有几份（<b>问的是「有没有编号过的」</b>，不是「有几个
        /// 文件」—— <c>list.jsonc</c> 自己、用户自己放进去的别的文件都不算）。
        static int NumberedFiles(string dataDir) =>
            System.IO.Directory.Exists(LifeModeStore.DirOf(dataDir))
                ? System.IO.Directory.EnumerateFiles(LifeModeStore.DirOf(dataDir))
                    .Count(f => LifeModeNumbers.TryNumberIn(System.IO.Path.GetFileName(f), out _))
                : 0;
    }

    /// <summary>「恢复默认」那一条的常驻自检（他真鼠标点出来的第二条 defect：按下去之后
    /// 数字框里先闪一个别的数，才回到默认值）。
    ///
    /// 临时目录里一份 <c>activityTimeoutMin: 40</c> 的文档 → 开一扇真的窗 → 把那一格拧到 40、
    /// 焦点放进它里面那一行字（人手打完字还没走开就是这个样子）→ 按「恢复默认」→
    /// <b>同一帧</b>问两头：控件里的值、和那一行字。值对、字还是旧的，屏幕上看到的就是旧数一闪。
    /// 最后整窗扫一遍：每一条 SpinBox 的字都得等于它自己的值。</summary>
    public static List<string> DefaultsSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("defaults");
        var dir = host.Dir;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion},\n  \"activityTimeoutMin\": 40,\n  \"flow\": \"fadetop\"\n}}\n");
            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死

            var row = win._byKey["activityTimeoutMin"];
            if (row.Value is not SpinBox box) return ["那一行的控件不是 SpinBox，这一组没法跑"];
            var line = box.GetLineEdit();
            box.Value = 40;
            line.GrabFocus();       // 「刚在那个格子里打过字」那一个状态

            // 输入框不带上限：越界的数就该留在格子里，屏幕上不许冒出一个你没写过的数。
            // （设计文档 `docs/design/config.md`「每个设置只声明一次」那条，；这一键声明的就是「no ceiling」。）
            box.Value = 151;
            if ((int)box.Value != 151)
                checks.Fail($"格子里打了 151，剩下的是 {(int)box.Value} —— 控件身上还带着上限");
            // 写回那一步现在也不夹了：2026-09-27 他把天花板那一档也拆了，
            // 所以格子写 151，文件里就是 151。
            // 下限还在（0 和负数会把调度算坏），所以那一头也留一条实测结果。
            if ((int)win._copy.Map.Get("activityTimeoutMin") != 151)
                checks.Fail($"格子写着 151，副本里却是 {win._copy.Map.Get("activityTimeoutMin")} —— " +
                             "这一档的上限已经拆了，谁都不该再把它夹回去");
            box.Value = 0;
            if ((int)win._copy.Map.Get("activityTimeoutMin") != 1)
                checks.Fail("打了 0，副本里没夹回 1：下限是留着的，那条规矩没拆");
            box.Value = 151;
            // 只数「恢复默认」这一趟：上面拧到 40 那一下是真的改动，算一次是应该的。
            win._writeBacks = 0;
            win.PressFooter("Defaults");
            if (win._writeBacks > 0)
                checks.Fail($"灌默认值这一趟往副本回写了 {win._writeBacks} 次，该是一次都不动 —— " +
                             "Load 自己的注释就写着「这一遍不许回写成一个假改动」");

            var wantTimeout = Settings.Decl("activityTimeoutMin").AsInt;
            if ((int)box.Value != wantTimeout)
                checks.Fail($"按了「恢复默认」，那一行的值读回来是 {box.Value}，该是声明里那个 {wantTimeout}");

            // 整窗扫一遍，别只盯着一行：每一行都对着一条声明，字追不上值的那些都会闪。
            // 这一发和界面上逐帧报的是同一个函数（StaleCells），不留第二份判据。
            foreach (var stale in win.StaleCells()) checks.Fail($"这一格的字追不上值 —— {stale}");

            // **一串分钟数那一行是另一种控件**（一行字，不是数字格），所以上面那一发量不到它 ——
            // 那一格灌的是文本，按错了类型会在夹取那一步炸或者悄悄回默认，而屏幕上只是「不对」。
            // 期望的字**从声明问**（出厂几格、每格几分钟都写在声明表里）。
            var delayDecl = Settings.Decl("restDelay");
            if (win._byKey["restDelay"].Value is not LineEdit delays)
                checks.Fail("休息延迟那一行的控件不是 LineEdit，这一组没法跑");
            else
            {
                var wantDelays = delayDecl.SpellDelays(new Settings().RestDelays);
                if (delays.Text != wantDelays)
                    checks.Fail($"按了「恢复默认」，休息延迟那一行是 '{delays.Text}'，该是 '{wantDelays}'");
                // 灌值**不许回写**成一次假改动 —— 上面清零 `_writeBacks` 之后按的那一下必须是 0。
                win._writeBacks = 0;
                win.PressFooter("Defaults");
                if (win._writeBacks > 0)
                    checks.Fail($"灌默认值又把休息延迟那一格回写了 {win._writeBacks} 次 —— " +
                                 "设 Text 不该发 text_changed");
                if (delays.Text != wantDelays)
                    checks.Fail($"再按一次「恢复默认」，休息延迟那一行变成 '{delays.Text}'，该还是 '{wantDelays}'");
            }
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();
    }

    /// <summary>导航那一组：顶部三个域 + 搜索框，跑在 <c>--check</c> 里。
    ///
    /// 临时目录里开一扇真的窗，然后问三件事，每一件都<b>两头夹</b>
    /// （CODING_STANDARDS 第 5 条：实测结果说「通」和说「不通」都要先怀疑测量程序）：
    /// <list type="bullet">
    /// <item><b>域切换</b>：切到桌面日历 → 日历那个分区该出现、休息那一侧的分区该消失；
    /// 切回休息 → 反过来。只验「该出现的出现」不够，一只从不切域的测量程序也能过那一条。</item>
    /// <item><b>搜索</b>：在休息域里搜 <c>opacity</c> → <c>maxOpacityPercent</c> 该在、
    /// <c>sound.volume</c> 不该在（对照：一定该搜不到的那一条，防的是「筛了个寂寞」）；
    /// 清空 → 44 行全回来。</item>
    /// <item><b>搜索跟着键名走</b>：搜英文键名 <c>volume</c> 找得到「音量」那一行 ——
    /// 中文标签和英文键名是两套词，只按一套筛会有一半的人搜不到。</item>
    /// </list>
    ///
    /// 不发鼠标事件，按的是 <c>tab_changed</c> / <c>text_changed</c> 两个信号 ——
    /// 「页签点得动」那一半是 Probe --push 量的，和 <see cref="FlowsSelfTest"/> 同一个分工。</summary>
    public static List<string> NavSelfTest(Node parent)
    {
        var checks = new Checks();
        var seen = new List<string>();
        var host = OptionsHost.InTempDir("nav");
        var dir = host.Dir;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion},\n  \"flow\": \"fadetop\"\n}}\n");
            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            // <b>先装语言表</b>。装表的只有 App，自检跑的 Probe 那一侧从来不装，于是这扇窗里
            // <c>Tr(键)</c> 原样吐回键名 —— 标签那一栏量到的是 "sound.volume" 而不是「音量」。
            // 后果不是哪条判据红了，是<b>有一条判据一直在量错的东西</b>：搜「音量」能中，
            // 中的是提示那一栏（提示直接灌 <c>CommentZh</c>，不过 Tr），标签栏压根没参与。
            // 装上之后下面那条前置判据守着这件事：表没装上会明说，不会再悄悄退回去。
            var lang = Lang.Apply(state.Current.Language);
            if (lang.Count > 0)
                return [$"语言表本身有 {lang.Count} 条问题，这一组量的就不是屏幕上那些字：{string.Join("；", lang)}"];
            // <b>接一个收集器</b>：Log 的默认值是空委托，自检里跑的时候窗自己的
            // 「可见性：…」「换到第 N 个域…」全被吞掉 —— 2026-09-29 那一发「搜 opacity，
            // 音量还在」查了半轮查不到，就是因为实测结果一条都没出来，只能靠推理猜。
            // 只收导航那几行：早上一版收全部、限 80 条，结果开窗那十几行就把额度用光，
            // 真正要看的搜索那几发反而一条没留下 —— 收集器自己成了那个假阴性。
            // 出问题时把最后几行附在问题后面：可见性这十几行是这一组唯一的现场，
            // 别的组（options/…）问不到它们。
            // **它在 `wire` 里接** —— 那一段跑在挂上去之前，所以开窗那十几行
            // 「可见性：…」真的进得来（挂上去之后 `_Ready` 已经刷完了）。
            var win = host.Open(state, parent, w => w.Log = (_, line) =>
            {
                if (line.Contains("可见性") && seen.Count < 40) seen.Add(line);
            });

            var search = win.GetNode<LineEdit>("Margin/Stack/Nav/Search");

            // ---- 窗头那一行的形状：域标签必须在、没有档位开关、搜索在右上角 ----------
            // 2026-10-07 所有者定夺：「域标签 不许被藏」。原来这一组问的是相反的一件事
            //（「切换那里不可出现切换」→ 域标签不许在），把定夺读反了，于是 2026-10-06
            // 那一发把 TabBar 整格删掉、自检还全绿。**判据从「不许在」翻成「必须在」**
            // 才是问得住「不许被藏」的那一条：藏在里面、藏成折叠、藏到要滚才看见，
            // 三样都在这一组下面。
            var bar = win.GetNodeOrNull<TabBar>("Margin/Stack/Nav/Domains");
            if (bar == null)
                checks.Fail("窗头没有域标签那一格 ——「域标签 不许被藏」，2026-10-06 那一发整格删掉就是它");
            else
            {
                if (bar.TabCount != Domains.Length)
                    checks.Fail($"域标签 {bar.TabCount} 格，Domains 表 {Domains.Length} 格 —— 标签数要等于域数");
                // **「不许被藏」的头一样：它自己得在屏幕上。**
                // 这一条是 2026-10-07 补的，补的缘由就是这一次事故本身：2026-10-06 那一发
                // 把 TabBar 整格删了，而当时那一组判据问的是「域标签**不许在**」——
                // 判据和定夺一起读反了，于是「删掉」被判成「符合定夺」。
                // 只问「在不在树上」不够：在树上但 `visible = false`，人一样看不见，
                // 而那种形状当时全绿。所以这里显式问 Visible。
                if (!bar.Visible)
                    checks.Fail("域标签那一格 visible = false —— 它在树上但不在屏幕上，"
                                 + "那就是「被藏」。「域标签 不许被藏」");
                // 「不许被藏」的三样，逐样问：它在不在（上面问过了）、一格一格摆着没有省略、
                // 没有折叠按钮。TabBar 在标签摆不下时会自己出滚动箭头 —— 那就是「要滚才看见」。
                var missingTabs = Domains.Where((_, i) =>
                    bar.GetTabTooltip(i) == "" && bar.GetTabTitle(i) == "").Select(d => d.Title).ToList();
                if (missingTabs.Count > 0)
                    checks.Fail($"域标签里有 {missingTabs.Count} 格既没有字也没有提示 —— "
                                 + $"{string.Join("、", missingTabs)}（标签不许是空壳）");
                if (!bar.SizeFlagsHorizontal.HasFlag(Control.SizeFlags.Expand))
                    checks.Fail("域标签那一格没有撑开（size_flags_horizontal 要带 EXPAND）");
            }
            if (win.GetNodeOrNull("Margin/Stack/Nav/Advanced") != null)
                checks.Fail("窗头还留着「显示高级设置」那颗开关 —— 它按下去就是在藏行，"
                             + "而所有者 2026-10-07 确认它不回来（域标签回来是另一件事）");
            var nav = win.GetNode<HBoxContainer>("Margin/Stack/Nav");
            var navOrder = nav.GetChildren().Cast<Node>().Select(n => n.Name.ToString()).ToList();
            if (navOrder.Count == 0 || navOrder[^1] != "Search")
                checks.Fail($"窗头那一行是 [{string.Join(" / ", navOrder)}]，搜索框该排最右一格"
                             + $"（窗头右上角），现在是第 {navOrder.IndexOf("Search") + 1} 格");
            // 撑开的是域标签那一格，不是曾经那颗空占位（Gap）—— Gap 是域标签被删那一次
            // 才插进去顶替它位置的，域标签回来以后 Gap 没有存在理由了。
            if (navOrder.Count > 0 && navOrder[0] != "Domains")
                checks.Fail($"窗头那一行第 1 格是 {navOrder[0]}，该是域标签 Domains —— "
                             + "域标签是这一行最左、常驻可见的那一格");

            // ---- 界面上有行的键，开窗时一条都不许少 ------------------------------------
            // 键名**从界面上现问**（<see cref="RowKeys"/>），不手抄一张清单出来：手抄的那一份
            // 就是会腐烂的副本（`CODING_STANDARDS.md` 取证方法论第 6 条）。加一条键、忘了把它
            // 算进来，这一组照样绿 —— 那样它量的是当初那份清单，不是「一切」。
            // ---- 逐个域验：每一格里的行一条都不许少 --------------------------------
            // **域接回来以后，「全在场」不再是开窗那一下的数，是逐格数的。**
            // 2026-10-06 那一发正是只量了开窗那一格就宣布全绿，而域标签被删了，
            // 11 张卡平铺着，于是「全在场」量到的和「全摆出来」是同一件事 —— 量不出缺失。
            // 现在每一格都要点一遍，点完再问：那一格名下每一行都在吗？
            // 键名**从界面上现问**（<see cref="RowKeys"/>），不手抄一张清单出来：
            // 手抄的那一份就是会腐烂的副本（`CODING_STANDARDS.md` 取证方法论第 6 条）。
            var every = win.RowKeys();
            var grand = 0;
            for (var d = 0; d < Domains.Length; d++)
            {
                win.GoDomain(d);
                var want = every.Where(k => DomainOf(win.SectionOfKey(k)) == d).ToList();
                var missing = want.Where(k => !win.RowShown(k)).ToList();
                if (missing.Count > 0)
                    checks.Fail($"第 {d + 1} 格「{Domains[d].Title}」里这 {missing.Count} 条不在屏幕上："
                                 + $"{string.Join("、", missing.Take(8))}" + (missing.Count > 8 ? " …" : "")
                                 + " —— 一格之内不许藏任何一行");
                // 那几格自己的卡片也都要在场上（KeepsOwnContent 的区自带内容，不靠这些行撑着）。
                foreach (var s in Domains[d].Sections)
                    if (!win.CardShown(s))
                        checks.Fail($"第 {d + 1} 格「{Domains[d].Title}」该摆 {s} 那张卡片，它不在屏幕上");
                grand += want.Count;
                if (win.VisibleRows() != want.Count)
                    checks.Fail($"第 {d + 1} 格可见 {win.VisibleRows()} 行，该格名下共 {want.Count} 行");
            }
            // 逐格加起来的总数，要等于界面上全部的行数 —— 少一格就等于有一格没人管。
            if (grand != every.Count)
                checks.Fail($"逐格加起来 {grand} 行，界面上共 {every.Count} 行 —— "
                             + "有一个分区不在任何一格里，或某一行被算进了两格");
            win.GoDomain(0);
            var total = win.VisibleRows();
            // 对照：高级那一档同样全在场。键名**从声明表现取**，不手抄 —— 声明表里一条高级键
            // 都没有的话，这一组量的就不是「全在场」了（它自己会说出来）。
            var advancedKey = every.FirstOrDefault(k => IsAdvanced(Settings.Decl(k)));
            if (advancedKey == null) checks.Fail("声明表里一条高级键都没有 —— 这一组量的不是「全在场」");
            else if (!win.RowShown(advancedKey))
                checks.Fail($"第 {win.DomainIndex() + 1} 格里高级键 {advancedKey} 不在屏幕上 —— "
                             + "档位不许再藏任何东西（「显示高级设置」那颗开关不回来，藏行没有别的入口）");

            // ---- 搜索：它是唯一还能把行收起来的控件，而那是用户自己敲了词才发生的 ----
            // 基线是第 1 格量过的那个数。搜索**压过域**：敲了词之后别的格也该搜得到，
            // 否则人在「休息提醒」那一格敲「日历」会一条都搜不到，又得出「选项不见了」那一个结论。
            // 这一条量的就是这个：停在第 1 格，敲一个只在别的格里的词，那一格照样要出来。
            search.Text = "日历";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "日历");
            if (!win.CardShown("Calendar"))
                checks.Fail($"停在第 1 格「{Domains[0].Title}」搜「日历」，日历那一张卡片不在屏幕上 —— "
                             + "搜索要压过域，否则敲了词只在自己的格子里找，等于又藏起来一批");
            // 对照：英文键名那一半。`opacity` 只在遮罩那张卡上，遮罩归第 1 格。
            search.Text = "opacity";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "opacity");
            if (!win.RowShown("maxOpacityPercent"))
                checks.Fail("搜 opacity，「最大不透明度」那一行不在（英文键名也该参与匹配）");
            if (win.RowShown("sound.volume"))
                checks.Fail("搜 opacity，「音量」那一行还在（对照：该搜不到的）" +
                             (win.MatchWhy("sound.volume", "opacity") == ""
                                 ? "：它四栏里没有这个词，可见性是别处留下的"
                                 : $"：命中在{win.MatchWhy("sound.volume", "opacity")}，那处该改"));
            if (win.VisibleRows() >= total)
                checks.Fail($"搜 opacity 还剩 {win.VisibleRows()} 行（第 1 格满窗 {total} 行）—— 搜索没在筛");
            // 被搜空的分区要连卡片一起收：留一张空卡片在屏幕上像出了什么事。
            if (win.CardShown("Sound") || win.CardShown("Image"))
                checks.Fail("搜 opacity，遮罩颜色和背景图那两张卡片还在屏幕上 —— " +
                             "它们一行都没剩下，该连卡片一起收（这一组量的是 drop 那一支）");

            // 中文标签那一半：敲标签上的字，键名认不出来也该中。
            search.Text = "音量";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "音量");
            if (!win.RowShown("sound.volume")) checks.Fail("搜「音量」，音量那一行不在（中文标签该参与匹配）");
            if (win.MatchWhy("sound.volume", "音量") != "标签")
                checks.Fail($"搜「音量」中的是{win.MatchWhy("sound.volume", "音量")}那一栏，不是标签栏 —— " +
                             "这一发量的是提示，不是标签");
            // 自带内容那几区：搜索筛不掉它们自己那一块，搜别的词时它们仍留在场上。
            if (!win.CardShown("Routine"))
                checks.Fail("搜「音量」之后作息那一张卡片不在屏幕上 —— "
                             + "那一区有搜索筛不掉的内容（时间块编辑器），该留在场上");
            if (!win.CardShown("List")) checks.Fail("搜「音量」之后休息清单那一张卡片不在屏幕上（它自带内容）");
            if (!win.CardShown("Dnd")) checks.Fail("搜「音量」之后勿扰那一张卡片不在屏幕上（它自带内容）");

            // 搜的词**从那一行自己的标签上现取**：手写的词哪天改掉字，这一组量的就是没人搜的
            // 那个词（上一版在 minBreakMs 那一发栽过同一个坑）。这一条同时量「高级键照样搜得到」
            // —— 档位已经不管事了，搜索是唯一那一支，而它不该被任何键的档位挡住。
            var probeKey = advancedKey!;
            var probe = win.LabelOf(probeKey);
            var probeWhy = win.MatchWhy(probeKey, probe);
            if (probeWhy != "标签")
                checks.Fail($"取高级键 {probeKey} 那行自己的标签当搜索词，它却没在标签栏命中自己"
                             + $"（命中在{probeWhy}）—— 那一发量的就不是搜索了，先怀疑测量程序");
            search.Text = probe;
            search.EmitSignal(LineEdit.SignalName.TextChanged, probe);
            if (!win.RowShown(probeKey))
                checks.Fail($"搜高级键 {probeKey} 那行自己的标签（「{probe}」），它不在屏幕上 —— "
                             + "搜索是唯一那一支，它不该被任何键挡住");

            // 清空 = 回到**这一格**满窗。域接回来以后不是「全部 11 张卡片都回来」，
            // 而是「第 1 格该有的那几张回来」—— 域切换是可见性的一支，不是搜完之后的残留。
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "");
            if (win.VisibleRows() != total)
                checks.Fail($"清空搜索后可见 {win.VisibleRows()} 行，该回到第 1 格满窗那 {total} 行");
            var mine = Domains[win.DomainIndex()].Sections;
            var afterCards = SectionTitles.Select(n => n.Node).Where(win.CardShown).ToList();
            var expected = SectionTitles.Select(n => n.Node).Where(mine.Contains).ToList();
            if (!afterCards.OrderBy(n => n).SequenceEqual(expected.OrderBy(n => n)))
                checks.Fail($"清空搜索后在场的卡片 [{string.Join("、", afterCards)}]，"
                             + $"该是第 {win.DomainIndex() + 1} 格那 [{string.Join("、", expected)}]");

            // ---- 分区之间看得见：每一张都是一张在屏幕上的卡片 ----------------------
            // 「区与区之间要看得见」由**卡片在不在树上**来说（分割线随卡片退休了，2026-09-30
            // 那一轮的结论）。两头还是夹着：不是「到处都不该是卡片」，也不是「到处都是就没事」——
            // 上面那一组已经数过「全部都在场」，这里问的是**形状**：在场的那些是不是真卡片。
            var inTree = SectionTitles.Select(n => n.Node).Where(win.SectionShown).ToList();
            if (inTree.Count == 0) checks.Fail("在场一个分区都没有 —— 这一组量的不是分区");
            foreach (var shown in inTree)
                if (!win.CardShown(shown))
                    checks.Fail($"「{shown}」在场，但它不是一张在屏幕上的卡片（分区又并回一坨了）");

            // 番茄钟自己一区：第 1 格就该看得见它那一个键。
            if (!win.RowShown("pomodoro.workMin"))
                checks.Fail("第 1 格该看得见番茄钟那一个键，它不在屏幕上 —— 那一区就成了一个光标题");

            // ---- 作息那一区：**它在第 3 格**，所以这一条先站到那一格再量 --------------------
            // 2026-10-06 那一发把域标签删掉之后，作息表和其余 10 张卡一起平铺在一条滚动里，
            // 「开窗该看得见 routine.enabled」成立。域接回来之后它在第 3 格，
            // 站在第 1 格当然看不见 —— 那不是缺陷，是域切换干的事。
            // 但「这一格那一行得全在场」仍然是硬要求，所以这里量的是**站到那一格之后**。
            win.GoDomain(DomainId("routine"));
            var routineKey = Settings.RoutineEnabledKey;
            if (!win.RowShown(routineKey))
                checks.Fail($"站到第 {win.DomainIndex() + 1} 格该看得见作息表的总开关 {routineKey}，它不在屏幕上");
            if (!win.CardShown("Routine"))
                checks.Fail($"站到第 {win.DomainIndex() + 1} 格，该看得见作息那一张卡片，它不在屏幕上");

            // 搜中文标签：键名认不出来也该中。标签现取，不手写。
            var routineLabel = win.LabelOf(routineKey);
            search.Text = routineLabel;
            search.EmitSignal(LineEdit.SignalName.TextChanged, routineLabel);
            if (!win.RowShown(routineKey))
                checks.Fail($"搜「{routineLabel}」，作息表那一行不在（中文标签该参与匹配）");
            if (win.MatchWhy(routineKey, routineLabel) != "标签")
                checks.Fail($"搜「{routineLabel}」中的是{win.MatchWhy(routineKey, routineLabel)}那一栏，"
                             + "不是标签栏 —— 这一发量的是键名，不是屏幕上的字");

            // 搜英文键名：两套词只按一套筛会有一半的人搜不到。
            search.Text = routineKey;
            search.EmitSignal(LineEdit.SignalName.TextChanged, routineKey);
            if (!win.RowShown(routineKey))
                checks.Fail($"搜英文键名 {routineKey}，作息表那一行不在");
            if (win.MatchWhy(routineKey, routineKey) != "键名")
                checks.Fail($"搜 {routineKey} 中的是{win.MatchWhy(routineKey, routineKey)}那一栏，不是键名栏");
            // 对照：一定该搜不到的那一条（防的是「搜索是个摆设」）。
            if (win.RowShown("sound.volume"))
                checks.Fail($"搜作息表那个键，遮罩那一侧的「音量」那一行也在屏幕上 —— "
                             + "它一个字都不沾，不该中");

            // 摆回默认视图：下面那一发（滚条）量的是这个状态。
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextChanged, "");

            // 窗底那根横条：`horizontal_scroll_mode` 曾经写成 2（SHOW_ALWAYS，不是「关掉」）。
            // 这里问的是屏幕上有没有那根条，不是配置里写的那个数。
            var hbar = win.GetNode<ScrollContainer>("Margin/Stack/Scroll").GetHScrollBar();
            if (hbar.IsVisibleInTree())
                checks.Fail("窗底挂着一根横向滚条 —— 内容宽度跟着窗宽走，横向没有可滚的余地");
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();
    }

    /// <summary>flows 编辑器那一条整链，跑在 <c>--check</c> 里：临时目录里一份带注释的文档 →
    /// 开一扇真的设置窗 → 换一条、拧时长、勾字段、勾内容、加一步、删一步、改名、新增、删掉 →
    /// 按保存 → 再从磁盘读回来对一遍。
    ///
    /// 全程不发鼠标事件，也不碰键盘：按的是控件自己的那几个信号（<c>pressed</c>、
    /// <c>toggled</c>、<c>value_changed</c>、<c>text_changed</c>、<c>item_selected</c>），
    /// 从那里往下的接线和真点出来的是同一条 —— 和 <see cref="IoSelfTest"/> 同一个理由。
    /// 「这些控件点得动」那一半不在这里，那是 <c>Probe --push</c> 量的。
    ///
    /// 磁盘那一段才是要害：界面上按下去的东西要真的落进 <c>settings.jsonc</c>，
    /// 而那份文档里没让它碰的注释和一个字都不许动（见 <see cref="JsoncDoc"/> 的 owned 那一步）。</summary>
    public static List<string> FlowsSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("flows");
        var dir = host.Dir;
        var path = System.IO.Path.Combine(dir, Settings.FileName);
        var utf8 = new System.Text.UTF8Encoding(false);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            // 用户自己加的一行，上面带着他自己写的注释 —— 它既是「这里改的是那张表」的样本，
            // 也是「改别的行不许碰这行」的对照。中性资产：没有截图，没有日历。
            var baseText = SettingsDoc.Render(new Settings()).Replace(
                "\n    \"todoList\": {",
                "\n    // 我自己起的那条\n" +
                "    \"mine\": { \"steps\": [ { \"ms\": 4000, \"dismissible\": true," +
                " \"interactive\": false, \"content\": [\"clock\"], \"sound\": false } ] },\n" +
                "    \"todoList\": {");
            System.IO.File.WriteAllText(path, baseText, utf8);

            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死

            if (win._editing != "todoList")
                checks.Fail($"一开窗该在编辑 flow 指着的那一条，实际在「{win._editing}」");

            // ---- 换一条：走下拉那一个信号，和鼠标点出来的是同一路 --------------------------
            win._picking.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            if (win._editing != "mine") checks.Fail($"点了 mine 那一格，编辑的还是「{win._editing}」");
            var rows = Rows(win);
            if (rows.Count != 1) checks.Fail($"mine 只有一步，界面上摆出 {rows.Count} 叠");
            var ms = rows[0].GetNode<SpinBox>("Head/Ms");
            // 这一格上摆的是**人话单位的那一种**（设计文档 `docs/design/config.md`「界面上不出现毫秒」），所以自检里
            // 也得按那一档去读去写：4 秒就是 4，不是 4000。判据问的仍然是「文档里那个值
            // 有没有原样出现在界面上」，只是换算那一步走 <see cref="Settings.Spell"/>。
            if (ms.Value != Settings.Spell(4000).Number)
                checks.Fail($"文档里 ms 是 4000，灌进控件成了 {ms.Value}" +
                             $"（那一档该摆 {Settings.Spell(4000).Number}）");
            if (rows[0].GetNode<Label>("Head/Unit").Text != TrCounted("unit.second", 4))
                checks.Fail($"那一步右边的单位词是「{rows[0].GetNode<Label>("Head/Unit").Text}」，" +
                             $"4000 毫秒该写「{TrCounted("unit.second", 4)}」");
            if (ms.MinValue != 0 || ms.MaxValue != int.MaxValue)
                checks.Fail($"那一步的输入框带了上限：{ms.MinValue}–{ms.MaxValue} —— " +
                             "数字格不带上限（下限照声明），上限只在写回那一步管");

            // ---- 改这一步的三个字段 --------------------------------------------------------
            ms.Value = Settings.Spell(9000).Number;   // 9000 毫秒 = 9 秒
            if ((int)win._copy.Flows["mine"].Steps[0].Get("ms") != 9000)
                checks.Fail("拧了时长，副本里没跟着变：控件到模型那一条线没接上");
            rows[0].GetNode<CheckBox>("Flags/Interactive")
                .EmitSignal(BaseButton.SignalName.Toggled, true);
            if (!win._copy.Flows["mine"].Steps[0].Interactive)
                checks.Fail("勾了「接鼠标」，副本里那一步还是不收鼠标");
            // ---- 这一步的布局：钮接上了、编辑器窗开得起来、内容摆进文档再读回来是同一份 ----
            //
            // **2026-10-06 重写这一段**：原来量的是「摆什么」那排勾选框（content）和
            // 「文字」那一行输入格（text）。那两样 2026-10-06 跟着布局一起退休了 ——
            // 内容成了布局里的控件，要有它们得去布局编辑器窗里拖。所以这里量的换成了
            // 「布局」那一族：步上有那颗钮、钮把编辑器窗叫起来、窗改的是**窗里这一份
            // 副本**、存下去再读回来是同一份。
            var lay = rows[0].GetNode<Button>("Show/Lay");
            if (lay.Text != win.Tr("摆这一屏…"))
                checks.Fail($"那颗钮上写的是「{lay.Text}」，该是「{win.Tr("摆这一屏…")}」");
            // 退休的那两片不许在树上露着 —— 露着就是「界面上能改、存下去不生效」。
            foreach (var gone in new[] { "Show/Choices", "Words" })
                if (rows[0].GetNodeOrNull(gone) is CanvasItem { Visible: true })
                    checks.Fail($"步编辑器里 {gone} 那一片还露着 —— content 和 text 2026-10-06 退休了");

            var step0 = win._copy.Flows["mine"].Steps[0];
            var layoutBefore = step0.Layout.Count;
            lay.EmitSignal(Button.SignalName.Pressed);
            if (win._layoutEditor == null)
            {
                checks.Fail("按了「摆这一屏…」，编辑器窗没开起来");
            }
            else
            {
                var editor = win._layoutEditor;
                if (!GodotObject.IsInstanceValid(editor) || !editor.IsInsideTree())
                    checks.Fail("编辑器窗那个对象还在，可它已经不在树上了 —— 开窗这一发没成");
                // 对照（一定该通）：窗对着的就是刚按下去的那一步，不是别的。
                if (!ReferenceEquals(editor.Editing, step0))
                    checks.Fail("编辑器窗对着的那一步不是刚按下去的那一步 —— 摆的东西会存到别处去");
                // **按第二次不叠第二扇**：用户连按几下只会以为程序卡了。
                var opens = 1;
                lay.EmitSignal(Button.SignalName.Pressed);
                if (!ReferenceEquals(win._layoutEditor, editor)) opens++;
                if (opens != 1)
                    checks.Fail($"按了两次那颗钮，编辑器窗开成了 {opens} 扇");
                // 编辑器改的是**窗里这一份副本** —— 和拧时长、勾「接鼠标」同一种身份。
                editor.Editing.Layout = StepLayout.Default();
                editor.Changed?.Invoke();
                if (step0.Layout.Count != StepLayout.Default().Count)
                    checks.Fail($"编辑器摆完一份默认布局，那一步上有 {step0.Layout.Count} 颗，" +
                                 $"该是 {StepLayout.Default().Count} 颗");
            }

            // 布局存下去再读回来是同一份（契约：「一份布局存下去再读回来是同一份」，纯算术）。
            win.GetNode<Button>("Margin/Stack/Footer/Save").EmitSignal(Button.SignalName.Pressed);
            var settled = System.IO.File.ReadAllText(path, utf8);
            if (!settled.Contains(Step.LayoutKey, StringComparison.Ordinal))
                checks.Fail($"存下去的那一份里没有 {Step.LayoutKey} —— 这一步屏上摆什么没落盘");
            // 对照（一定该通）：**一个字节都不改**再按一次保存，文档该一个字节都不动。
            win.GetNode<Button>("Margin/Stack/Footer/Save").EmitSignal(Button.SignalName.Pressed);
            if (System.IO.File.ReadAllText(path, utf8) != settled)
                checks.Fail("一个字都没改就按保存，文档却变了 —— 「原位编辑」那一条量不到东西");
            // 读回来：走真读文档那条路（<see cref="SettingsStore.Load"/> 读的就是这份文件），
            // 量的是**模型**那一族而不是界面。
            var reread = SettingsStore.Load(win.DataDir);
            var rereadStep = reread.Current.Flows.TryGetValue("mine", out var rf)
                ? rf.Steps.FirstOrDefault() : null;
            if (rereadStep == null)
                checks.Fail("存下去的那一份读不回来「mine」那一条");
            else if (rereadStep.Layout.Count != step0.Layout.Count ||
                     string.Join('|', rereadStep.Layout.Select(c => $"{c.Kind}:{c.XPercent}:{c.WidthPercent}"))
                     != string.Join('|', step0.Layout.Select(c => $"{c.Kind}:{c.XPercent}:{c.WidthPercent}")))
                checks.Fail($"布局存下去再读回来不是同一份：存的是 {step0.Layout.Count} 颗，" +
                             $"读回来 {rereadStep.Layout.Count} 颗");

            // ---- 加一步、删一步 -------------------------------------------------------------
            var before = win._copy.Flows["mine"].Steps.Count;
            win.GetNode<Button>(FlowsPath + "/AddStep/Btn").EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Flows["mine"].Steps.Count != before + 1)
                checks.Fail("按了「加一步」，那一条休息流程还是那么多步");
            if (Rows(win).Count != before + 1) checks.Fail("加了步，界面上没多出一叠");
            // 「加一步」加在末尾，所以删也删最后一行：删的若不是它，留下的就不是那 9000 毫秒那一步。
            Rows(win)[^1].GetNode<Button>("Head/Del").EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Flows["mine"].Steps.Count != before)
                checks.Fail($"删掉一步之后还剩 {win._copy.Flows["mine"].Steps.Count} 步，该是 {before}");
            if (win._copy.Flows["mine"].Steps.Count == 1 && win._copy.Flows["mine"].Steps[0].Ms != 9000)
                checks.Fail($"删掉最后一行之后留下的那一步是 {win._copy.Flows["mine"].Steps[0].Ms} 毫秒，" +
                             "该是刚才拧成 9000 的那一步");

            // ---- 改名：那一行在文档里换名字，旧名字不能留下 ---------------------------------
            win._naming.EmitSignal(LineEdit.SignalName.TextChanged, "我的");
            if (!win._copy.Flows.ContainsKey("我的") || win._copy.Flows.ContainsKey("mine"))
                checks.Fail("改名没换掉键，或旧名字还在表里");
            if (win._editing != "我的") checks.Fail($"改完名之后编辑的是「{win._editing}」，该跟着走");

            // 「走哪条休息流程」指着的那一条改名：那一个值要跟着走，不然它悄悄回落成 fadetop。
            var wasLifeMode = win._copy.Flow;
            win._picking.EmitSignal(OptionButton.SignalName.ItemSelected,
                win._copy.Flows.Keys.ToList().IndexOf(wasLifeMode));
            if (win._editing != wasLifeMode)
                checks.Fail($"下拉里点 {wasLifeMode} 没换成它在编辑（{win._editing}），下面那一发就不算数");
            win._naming.EmitSignal(LineEdit.SignalName.TextChanged, "走的");
            if (win._copy.Flow != "走的")
                checks.Fail($"把「走哪条」正指着的那一条改了名，那一行还留着旧名字 {win._copy.Flow}" +
                             "（下一次淡入会悄悄回落成 fadetop）");

            // ---- 新增一条、删掉一条 ----------------------------------------------------------
            var flows = win._copy.Flows.Count;
            var stagesOfMine = win._copy.Flows["走的"].Steps.Count;
            win.GetNode<Button>(FlowsPath + "/Pick/NewBtn").EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Flows.Count != flows + 1) checks.Fail("按了「新增一条」，表里没多出行");
            if (win._editing == "走的") checks.Fail("新增之后该在编辑新那一条，好让人马上改名字");
            if (!win._copy.Flows.TryGetValue(win._editing, out var fresh) ||
                fresh.Steps.Count != stagesOfMine)
                checks.Fail("新增的那条没抄正在编辑的那一条，那是从空里造，不是复制");
            var freshName = win._editing;
            win.GetNode<Button>(FlowsPath + "/Pick/DelBtn").EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Flows.Count != flows || win._copy.Flows.ContainsKey(freshName))
                checks.Fail("按了「删掉这一条」，那一行还在表里");

            // ---- 保存，然后对磁盘 ------------------------------------------------------------
            win.GetNode<Button>("Margin/Stack/Footer/Save").EmitSignal(Button.SignalName.Pressed);
            var saved = System.IO.File.ReadAllText(path, utf8);
            // 和 --check 那份首启样本同一个待遇：自检能说这个键在不在，说不出这份文档读起来
            // 像不像给人改的。界面上加出来的那一条长什么样，就看这一份。
            var sample = System.IO.Path.Combine(Tmp.Root, "keeper-lifemodes-sample.jsonc");
            System.IO.File.WriteAllText(sample, saved, utf8);
            GD.Print($"flows 样本：{sample}");
            if (!saved.Contains("\"我的\"", StringComparison.Ordinal))
                checks.Fail("界面上改完的「我的」没写进文档");
            if (saved.Contains("\"mine\"", StringComparison.Ordinal))
                checks.Fail("改名之后文档里还留着旧的那一行 —— 下一次启动它和新名字并列出两条");
            // 改名那一发欠的两条（规矩记在 CODING_STANDARDS.md「改名的匹配规则」）：那一行上面
            // 用户自己写的注释跟着旧行一起没了，而新行落到表的末尾。现在就地换标题，
            // 所以注释还贴着那一行、位置也没挪 —— 两条各一条实测结果。
            if (!saved.Contains("// 我自己起的那条", StringComparison.Ordinal))
                checks.Fail("改了个名字，那一行上面用户自己写的注释没了 —— 注释属于那一行，不属于那个名字");
            var rows2 = saved.Split('\n');
            var atMine = System.Array.FindIndex(rows2, l => l.Contains("\"我的\"", StringComparison.Ordinal));
            if (atMine < 1 || !rows2[atMine - 1].Contains("我自己起的那条", StringComparison.Ordinal))
                checks.Fail("那行注释没有贴在「我的」上面 —— 换名字之后它离了那一行，或者位置挪了");
            // 顺序：fadetop 在前，「我的」（原 mine）在中间，「走的」（原 todoList）在后。
            var atGone = saved.IndexOf("\"走的\"", StringComparison.Ordinal);
            if (atMine < 0 || atGone < 0 || atMine > atGone)
                checks.Fail($"改名之后两行的先后挪了位（我的={atMine} 走的={atGone}）—— 它们该留在原来的格子里");
            if (!saved.Contains("\"走的\"", StringComparison.Ordinal))
                checks.Fail("flow 那一行跟着改了名，文档里的休息流程却没跟着改");
            if (!saved.Contains("\"ms\": 9000", StringComparison.Ordinal))
                checks.Fail("拧过的时长没落盘");
            if (!saved.Contains("\"checklist\"", StringComparison.Ordinal))
                checks.Fail("勾上的那一项内容没落盘");
            var prose = Count(saved, Settings.StepDeclarations[0].CommentZh);
            // 字段说明跟着「这一行是新写出来的」走，不跟着「改名」走：mine 那一行是用户自己
            // 手写的，本来就没有那一步说明，改个名字不该凭空给他补一步模板 —— 那和他抱怨的
            // 「我写的注释没了」是同一件事的两头：那一行是他的，不是模板生成的。
            if (prose != Count(saved, "\"steps\"") - 1)
                checks.Fail($"字段说明该有 {Count(saved, "\"steps\"") - 1} 处（除用户手写那一行之外每条一步），" +
                             $"读回来 {prose} 处");
            // 那个数从声明表问，不写死：默认值一改，写死的那一份就成了一句过期的话，
            // 而它过期得没有声音（`calendar.widthPercent` 那次已经栽过一回，
            // `CODING_STANDARDS.md`「判据里不许存一份会腐烂的副本」那一条）。
            var wantTimeout = Settings.Decl("activityTimeoutMin").AsInt;
            if (!saved.Contains($"\"activityTimeoutMin\": {wantTimeout}", StringComparison.Ordinal))
                checks.Fail("改 flows 把别的键动了");
            if (!saved.Contains("// Keeper", StringComparison.Ordinal)) checks.Fail("文件头没了");

            // 磁盘上的那份再读回来：界面上按出来的东西，要能被下一次启动原样读懂。
            var back = SettingsStore.Load(dir);
            if (back.Error != null) checks.Fail($"保存之后读不回来：{back.Error}");
            else
            {
                if (!back.Current.Flows.ContainsKey("我的")) checks.Fail("改完名的那条读不回来");
                if (back.Current.Flow != "走的") checks.Fail($"flow 落盘成了 {back.Current.Flow}");
                var step = back.Current.Flows["我的"].Steps;
                // `Content` 不再问（2026-10-06 退休）：那一步「屏上摆什么」现在是**布局**，
                // 而布局在界面上是**另一个窗**里摆的 —— 那一步落盘时它跟着落
                // （上面 <c>mine</c> 那一段量过存读往返）。这一段量的是「界面上
                // 拧过的那几样还在不在」，布局不在这儿，它有自己那一段。
                if (step.Count != 1 || step[0].Ms != 9000 || !step[0].Interactive ||
                    step[0].Layout.Count == 0)
                    checks.Fail($"「我的」那一步读回来是 {step.Count} 步 / " +
                                 $"{(step.Count > 0 ? step[0].Ms : -1)} 毫秒 / " +
                                 $"接鼠标={step.Count > 0 && step[0].Interactive} / " +
                                 $"{(step.Count > 0 ? step[0].Layout.Count : -1)} 颗布局控件");
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(back.Current.ToDocument(), win.Live.ToDocument()))
                    checks.Fail("走了一趟磁盘，那张表不是同一份：" + Settings.Diff(back.Current, win.Live));
            }
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();

        static List<Control> Rows(Options win) => win._flowBox.GetChildren()
            .Where(c => c != win._stepTemplate).Cast<Control>().ToList();
    }

    /// <summary>作息表编辑器那一条整链，跑在 <c>--check</c> 里：临时目录里一份带注释、
    /// 带两行时间块的文档 → 开一扇真的设置窗 → 拧时刻、起名字、勾开关、挑一条休息流程、
    /// 上移、加一块、删一块 → 按保存 → 再从磁盘读回来对一遍。
    ///
    /// 和 <see cref="FlowsSelfTest"/> 同一处置：不发鼠标事件，按的是控件自己那几个信号
    /// （<c>pressed</c> / <c>toggled</c> / <c>value_changed</c> / <c>text_changed</c> /
    /// <c>item_selected</c>），从那里往下的接线和真点出来的是同一条。
    ///
    /// <b>找控件的方式也是从声明表来的</b>：一个时刻字段该是「几点 / 几分」两格、
    /// 一个开关字段该是 CheckBox、一个指向休息流程的文字字段该是下拉 —— 自检里按**这一族
    /// 声明的种类 + 场景里那个控件是什么**去找，不抄一份字段名清单（抄一份的话声明里
    /// 改了字段名，这一组量的就是一条已经不在的字段）。
    ///
    /// <b>反面对照也在这儿</b>：没碰的那一块一个字都不许跟着变（两组控件接的是同一个
    /// 副本而不是同一个对象），而两个数格是<b>一个</b>值的两半（拧一个数格之后那一个字段
    /// 是合成出来的数，不是拧过的那个数）。
    /// </summary>
    public static List<string> RoutineSelfTest(Node parent)
    {
        var checks = new Checks();
        var host = OptionsHost.InTempDir("routine");
        var dir = host.Dir;
        var path = System.IO.Path.Combine(dir, Settings.FileName);
        var utf8 = new System.Text.UTF8Encoding(false);
        try
        {
            // 两行出厂块：两个时刻都用 <see cref="Routine.At"/> 合出来（不是抄一遍 1350），
            // 而这一行 22:30 → 06:30 是**跨午夜**那一档 —— 读的时候那一格要能原样读回来。
            var doc = new Settings();
            doc.Blocks.Add(new TimeBlock
            {
                StartMinute = Routine.At(22, 30), EndMinute = Routine.At(6, 30), Name = "夜",
            });
            doc.Blocks.Add(new TimeBlock
            {
                StartMinute = Routine.At(9, 0), EndMinute = Routine.At(18, 0), Name = "白天",
            });
            System.IO.File.WriteAllText(path, SettingsDoc.Render(doc), utf8);

            var state = SettingsStore.Load(dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            var win = host.Open(state, parent);   // 次序（State/DataDir/Checklist 都在挂上去之前）由 OptionsHost 定死

            // 每一格按声明表的种类找，不按字段名找。
            var clock = Settings.BlockDeclarations.First(d => d.Kind == SettingKind.Int);
            var flag = Settings.BlockDeclarations.First(d => d.Kind == SettingKind.Bool);
            var text = Settings.BlockDeclarations.FirstOrDefault(d => d.Kind == SettingKind.Text);
            if (text == null) return ["声明表里一个文字字段都没有，这一组量的是别的字段"];

            // ---- 开窗那一刻：两行摆在界面上，时刻摆的是「几点 / 几分」 ----
            var rows = Rows(win);
            if (rows.Count != doc.Blocks.Count)
                checks.Fail($"文档里有 {doc.Blocks.Count} 块，界面上摆出 {rows.Count} 叠");
            if (rows.Count == 0) return checks.Report();
            var startHour = Field(rows[0], clock).GetNode<SpinBox>("Hour");
            var startMinute = Field(rows[0], clock).GetNode<SpinBox>("Minute");
            if (startHour.Value != Routine.HourOf(doc.Blocks[0].StartMinute) ||
                startMinute.Value != Routine.MinuteOf(doc.Blocks[0].StartMinute))
                checks.Fail($"第一块从 {Routine.Clock(doc.Blocks[0].StartMinute)} 开始，" +
                             $"界面上摆的是 {Routine.Clock(Routine.At((int)startHour.Value, (int)startMinute.Value))}" +
                             " —— 界面上要摆时刻，不是一天里的第几分钟");
            // 那两个数格各自的范围是从声明里那个「一天中的第几分钟」换算的，
            // 不是场景里写死的，也不是第二份抄来的 0–1439。
            if (startMinute.MaxValue != Routine.MinuteOf(Settings.Decl(TimeBlock.KeyPrefix + clock.Key).Max) ||
                startHour.MaxValue != Routine.HourOf(Settings.Decl(TimeBlock.KeyPrefix + clock.Key).Max))
                checks.Fail($"「{TimeBlock.KeyPrefix}{clock.Key}」那两格的上限是 " +
                             $"{Routine.Clock((int)startHour.MaxValue * Routine.MinutesPerHour + (int)startMinute.MaxValue)}，" +
                             "该是声明里那个范围换算出来的");

            // ---- 拧一个数格：两个数格是一个值的两半 ----
            startHour.Value = 7;
            if (startMinute.Value != Routine.MinuteOf(win._copy.Blocks[0].StartMinute))
                checks.Fail("拧了「几点」那一格，「几分」那一格跟着自己跳了 —— 两个数格不是同一个值");
            startMinute.Value = 15;
            if ((int)win._copy.Blocks[0].Get(clock.Key) != Routine.At(7, 15))
                checks.Fail($"拧了两个数格，副本里那一块是 {Routine.Clock((int)win._copy.Blocks[0].Get(clock.Key))}，" +
                             $"该是 {Routine.Clock(Routine.At(7, 15))}");
            // 反面对照：没碰的那一块一个字都不许跟着变。
            var other = (int)win._copy.Blocks[1].Get(clock.Key);
            if (other != doc.Blocks[1].StartMinute)
                checks.Fail($"改了第一块，第二块的起始变成了 {Routine.Clock(other)} —— 两叠接的是同一个块");
            // 拧到最边上：写回去正好是声明里那个上限 —— 那两格的范围就是从它换算的
            // （反过来说，拧不出越界的数：范围在控件上，夹取在声明表那一层）。
            var declClock = Settings.Decl(TimeBlock.KeyPrefix + clock.Key);
            startHour.Value = Routine.HourOf(declClock.Max);
            startMinute.Value = Routine.MinuteOf(declClock.Max);
            if ((int)win._copy.Blocks[0].Get(clock.Key) != declClock.Max)
                checks.Fail($"两个数格拧到最边，副本里是 {Routine.Clock((int)win._copy.Blocks[0].Get(clock.Key))}，" +
                             $"该是声明里那个上限 {declClock.Max}");
            startHour.Value = 7;
            startMinute.Value = 15;

            // ---- 名字：文本框按每一次击键走 ----
            var nameEdit = (LineEdit)Field(rows[0], text).GetNode<Control>("Edit");
            nameEdit.EmitSignal(LineEdit.SignalName.TextChanged, "夜里");
            if ((string)win._copy.Blocks[0].Get(text.Key) != "夜里")
                checks.Fail($"改了名字，副本里是「{win._copy.Blocks[0].Get(text.Key)}」");

            // ---- 开关 ----
            var box = (CheckBox)Field(rows[0], flag).GetNode<Control>("Edit");
            box.SetPressedNoSignal(!box.ButtonPressed);
            box.EmitSignal(BaseButton.SignalName.Toggled, !box.ButtonPressed);
            if ((bool)win._copy.Blocks[0].Get(flag.Key) == box.ButtonPressed)
                checks.Fail($"勾了开关，副本里没跟着变（{TimeBlock.KeyPrefix}{flag.Key}）");

            // ---- 指向休息流程的那一格：下拉里摆的必须是**每一个**休息流程的名字 ----
            var pickDecl = Settings.BlockDeclarations
                .FirstOrDefault(d => d.Kind == SettingKind.Text &&
                                     Field(rows[0], d).GetNode<Control>("Edit") is OptionButton);
            if (pickDecl == null)
                checks.Fail("编辑器里没有「到点走哪条休息流程」那一格的下拉 —— 那一个字段改不到");
            else
            {
                var option = (OptionButton)Field(rows[0], pickDecl).GetNode<Control>("Edit");
                // 第一格是「跟着设置里那一条」（空值），后面是休息流程的名字，一个不多一个不少。
                var want = new List<string> { TranslationServer.Translate(BlockFlowNone) };
                want.AddRange(win._copy.Flows.Keys);
                var got = Enumerable.Range(0, option.ItemCount).Select(i => option.GetItemText(i)).ToList();
                if (!got.SequenceEqual(want))
                    checks.Fail($"{TimeBlock.KeyPrefix}{pickDecl.Key} 那个下拉里是 [{string.Join(" / ", got)}]，" +
                                 $"该是 [{string.Join(" / ", want)}]");
                option.Select(1 + win._copy.Flows.Keys.ToList().IndexOf(win._copy.Flows.Keys.Last()));
                option.EmitSignal(OptionButton.SignalName.ItemSelected, option.Selected);
                if ((string)win._copy.Blocks[0].Get(pickDecl.Key) != win._copy.Flows.Keys.Last())
                    checks.Fail($"挑了一条休息流程，副本里是「{win._copy.Blocks[0].Get(pickDecl.Key)}」");
            }

            // ---- 上移、加一块、删一块 ----
            // 两头：最上面那一块的「上移」和最下面那一块的「下移」是**灰的**
            // （没有可挪的地方就不该摆一颗能按的钮），而中间那一块能挪。
            if (!rows[0].GetNode<HBoxContainer>("Head").GetNode<Button>("Up").Disabled)
                checks.Fail("第一块的「上移」不是灰的 —— 它上面没有可挪的地方");
            if (rows[0].GetNode<HBoxContainer>("Head").GetNode<Button>("Down").Disabled)
                checks.Fail("第一块的「下移」是灰的 —— 它下面还有一块");
            // 挪位置是**重灌**出来的，所以只能按内容比：拿旧节点比身份的话，
            // 重建之后没有两个节点是同一个，那条判据永远绿。
            rows[1].GetNode<HBoxContainer>("Head").GetNode<Button>("Up").EmitSignal(Button.SignalName.Pressed);
            if (Typed(Rows(win)[0], text) != doc.Blocks[1].Name)
                checks.Fail($"按了上移，第一叠显示的是「{Typed(Rows(win)[0], text)}」而不是刚挪上来的" +
                             $"「{doc.Blocks[1].Name}」—— 顺序没换");
            if (Typed(Rows(win)[1], text) != "夜里")
                checks.Fail($"上移之后第二叠显示的是「{Typed(Rows(win)[1], text)}」而不是刚才改的那一块");
            // 挪动不该改值：被挪上来的那一块带着它原来的时刻，挪下去的也还是改过的那个。
            if ((int)win._copy.Blocks[0].Get(clock.Key) != doc.Blocks[1].StartMinute ||
                (int)win._copy.Blocks[1].Get(clock.Key) != Routine.At(7, 15))
                checks.Fail("上移把两块的内容也换了 —— 挪位置不该动内容");
            if (!Rows(win)[^1].GetNode<HBoxContainer>("Head").GetNode<Button>("Down").Disabled)
                checks.Fail("最后一块的「下移」不是灰的 —— 它下面没有可挪的地方");

            var add = win.GetNode<Button>(RoutinePath + "/AddBlock/Btn");
            var before = win._copy.Blocks.Count;
            add.EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Blocks.Count != before + 1 || Rows(win).Count != before + 1)
                checks.Fail($"按了「加一个时间块」，表里 {win._copy.Blocks.Count} 块、界面上 {Rows(win).Count} 叠");
            // 新加的那一块：时刻那一格摆的是「几点 / 几分」，值是声明表的默认值。
            var added = Rows(win)[^1];
            if (added.GetNode<SpinBox>(BlockField(clock) + "/Hour").Value !=
                Routine.HourOf((int)win._copy.Blocks[^1].Get(clock.Key)))
                checks.Fail("新加的那一块时刻那一格是空的 —— 值没从声明表的默认值灌进去");

            Rows(win)[^1].GetNode<HBoxContainer>("Head").GetNode<Button>("Del")
                .EmitSignal(Button.SignalName.Pressed);
            if (win._copy.Blocks.Count != before)
                checks.Fail($"删掉一块之后还剩 {win._copy.Blocks.Count} 块，该是 {before}");

            // ---- 空表那一档要说出来，不是一个空列表 ----
            var empty = TranslationServer.Translate("还没有时间块");
            while (win._copy.Blocks.Count > 0)
                Rows(win)[0].GetNode<HBoxContainer>("Head").GetNode<Button>("Del")
                    .EmitSignal(Button.SignalName.Pressed);
            if (Rows(win).Count != 0) checks.Fail("表空了，界面上还摆着叠");
            if (win._blockTotal.Text != empty)
                checks.Fail($"空表那一句实测结果是「{win._blockTotal.Text}」，该是「{empty}」");
            add.EmitSignal(Button.SignalName.Pressed);
            if (win._blockTotal.Text == empty)
                checks.Fail("加了一块之后底下那一句还说「还没有时间块」");

            // ---- 拧成一份有内容的，再落盘 ----
            var written = new TimeBlock
            {
                StartMinute = Routine.At(7, 15), EndMinute = Routine.At(9, 0), Name = "夜里",
            };
            written.Set(flag.Key, !(bool)new TimeBlock().Get(flag.Key));
            win._copy.Blocks = [written];

            // ---- 保存，然后对磁盘 ----
            win.GetNode<Button>("Margin/Stack/Footer/Save").EmitSignal(Button.SignalName.Pressed);
            var saved = System.IO.File.ReadAllText(path, utf8);
            var sample = System.IO.Path.Combine(Tmp.Root, "keeper-routine-sample.jsonc");
            System.IO.File.WriteAllText(sample, saved, utf8);
            GD.Print($"作息表样本：{sample}");
            // 布尔的 JSON 写法是小写的 true/false，界面上读回来的值用 .NET 的写法，
            // 所以那一栏在这里翻一下 —— 不翻就是拿 False 去比一段 JSON，那条判据永远红。
            var flagOn = (bool)written.Get(flag.Key);
            foreach (var (label, want) in new[]
            {
                ($"\"{clock.Key}\": {written.StartMinute}", true),
                ($"\"{text.Key}\": \"夜里\"", true),
                ($"\"{flag.Key}\": {(flagOn ? "true" : "false")}", true),
            })
                if (saved.Contains(label, StringComparison.Ordinal) != want)
                    checks.Fail($"落盘的那一份里没有 {label} —— 界面上改的那一块没写进去");
            // 没有它自己的话、别的键也不该动。
            if (!saved.Contains("// Keeper", StringComparison.Ordinal)) checks.Fail("文件头没了");
            var wantTimeout = Settings.Decl("activityTimeoutMin").AsInt;
            if (!saved.Contains($"\"activityTimeoutMin\": {wantTimeout}", StringComparison.Ordinal))
                checks.Fail("改作息表把别的键动了");

            var back = SettingsStore.Load(dir);
            if (back.Error != null) checks.Fail($"保存之后读不回来：{back.Error}");
            else if (back.Current.Blocks.Count != 1)
                checks.Fail($"读回来 {back.Current.Blocks.Count} 块，该是 1");
            else
            {
                var one = back.Current.Blocks[0];
                if (one.StartMinute != written.StartMinute || one.EndMinute != written.EndMinute ||
                    one.Name != "夜里" || (bool)one.Get(flag.Key) != (bool)written.Get(flag.Key))
                    checks.Fail($"读回来的是 {Routine.Clock(one.StartMinute)}–{Routine.Clock(one.EndMinute)} / " +
                                 $"「{one.Name}」/ {TimeBlock.KeyPrefix}{flag.Key}={one.Get(flag.Key)}");
            }
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说（problems 里多一条）
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();

        static List<Control> Rows(Options win) => win._blockBox.GetChildren()
            .Where(c => c != win._blockTemplate).Cast<Control>().ToList();

        // 一块里某个字段的那一格。行是按声明表摆的，所以自检里也按声明表取，不抄字段名。
        static HBoxContainer Field(Control row, SettingDecl decl) => row.GetNode<HBoxContainer>(BlockField(decl));

        // 那一格文本框里此刻的字（挪位置是重灌出来的，只能按内容比）。
        static string Typed(Control row, SettingDecl decl) =>
            row.GetNode<HBoxContainer>(BlockField(decl)).GetNode<LineEdit>("Edit").Text;
    }

    /// 一段文本里某片段出现几次（FlowsSelfTest 拿它数「每条休息流程上面都有一步字段说明」）。
    private static int Count(string haystack, string needle)
    {
        var at = 0;
        var seen = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            seen++;
            at += needle.Length;
        }
        return seen;
    }
}
