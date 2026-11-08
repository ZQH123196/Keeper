using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>预设生活模式的画廊：<b>随安装带来的那几份只读模板</b>，一列在那儿，
/// 每一列一颗「收进来」（设计文档 `docs/design/preset-life-mode.md`「预设生活模式是内置的只读模板，收进来才算自己的」那一节，
/// ）。
///
/// <b>这一扇窗什么都不生成</b>：布局全在 <c>res://src/Presets.tscn</c> 里（一扇窗、抬头两行、
/// 一张列表、一颗关掉的钮），列是那条列表里 <c>Template</c> 那个节点
/// <c>Duplicate</c> 出来的 —— 和设置窗里时间块那一行、步那一行同一种做法
/// （<c>Options.BlockRow</c>）。<b>只有「有几列」是数据</b>，而那来自
/// <see cref="PresetLifeModes.Load"/>。
///
/// <b>「收一个」走的是现成的导入那条路</b>：<see cref="LifeModeStore.TakeIn(string,string,byte[])"/>
/// —— 和从一份文件收进来是<b>同一个方法</b>（发号、判重、搬字节、写名单全在那一处）。
/// 所以收进来的那一份和手存的生活模式完全同权：能切、能覆盖、能删、能导出，而递增号分配、
/// 撞名拦阻、备份与逐字节回环那一整套规矩都是白送的（ADR-0005「预设生活模式不是第二种实体」）。
///
/// <b>两处入口，都在设置窗那一族里</b>：生活模式管理器（窗头那颗「预设生活模式…」）和
/// 窗头那颗生活模式按钮展开的菜单。**托盘不加入口** —— 托盘在这一族照旧只做「切」
/// （设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那一条）；判据在 <see cref="EntriesSelfTest"/> 和
/// <see cref="Tray.LifeModeMenuSelfTest"/>。
///
/// <b>读不通的两档各说各的</b>：<b>一个目录整个不在</b>（<see cref="PresetLifeModes.Gallery.Missing"/>）
/// 和 <b>目录在、那一份读不通</b> 是两件事，各有自己的句子（见 <see cref="Refresh"/>）；
/// 读不通的那一份照样列出来，只是那颗钮点不动 —— 一份坏文件说成「一个都没有」和一份坏名单
/// 说成「你没存过」是同一对假象（<c>CODING_STANDARDS.md</c> 第 5 条那一族）。</summary>
public partial class Presets : Window
{
    public const string ScenePath = "res://src/Presets.tscn";

    private const string TitlePath = "Margin/Stack/Title";
    private const string StatusPath = "Margin/Stack/Status";
    private const string ErrPath = "Margin/Stack/Err";
    private const string ListPath = "Margin/Stack/List";
    private const string ClosePath = "Margin/Stack/Buttons/Close";

    /// <summary>收进来的那一份住哪儿（设置所在目录）。<b>由设置窗递进来</b>，不是它自己算的 ——
    /// 界面上一个窗不该知道配置住在哪。</summary>
    public string DataDir = "";

    /// <summary>预设那一个只读目录。默认装在程序里的那一个；<b>自检里递一个用完就删的临时目录</b>，
    /// 于是判据用的预设目录也是它自己那一份（<see cref="PresetLifeModes.Load"/> 那一段的说明）。</summary>
    public string PresetDir = PresetLifeModes.Dir;

    /// <summary>配色跟着设置窗那一个键（<see cref="UiTheme.Key"/>）走：暗色那一版是<b>同一份场景</b>、
    /// 同一份 Theme 条目换一套色值（设计文档 `docs/design/config.md`「配置这份文档」里 <c>uiTheme</c> 那条）。</summary>
    public string UiThemeValue = "light";

    public Action<string, string> Log = (_, _) => { };

    /// <summary>收进来一个就<b>叫一次</b>：设置窗那一侧的菜单要重建（名单上多了一条）。
    /// 不记状态，所以这一扇窗自己不需要知道谁在管菜单。</summary>
    public Action? Took;

    /// <summary>界面上摆出来的一列。<b>名字抄进闭包</b>：这一列和它那颗钮要说的是同一个名字
    /// （和托盘那一族同一条理由）。</summary>
    public sealed record Shown(PresetLifeModes.Preset Preset, Label Name, Label Note, Button Take);

    private readonly List<Shown> _shown = [];

    /// <summary>此刻界面上摆着的那几列。<b>自检里按它读</b>（按下标按名单的次序），
    /// 而不是自己在树上找 —— 引擎给 <c>Duplicate</c> 出来的名字是自取的
    /// （<c>Options.BuildChecklist</c> 那一段的注释：按父子找，不按名字）。</summary>
    public IReadOnlyList<Shown> Rows => _shown;

    public string StatusSaid => GetNode<Label>(StatusPath).Text;

    public string ErrorSaid => GetNode<Label>(ErrPath).Text;

    public bool ErrorShown => GetNode<Label>(ErrPath).Visible;

    public override void _Ready()
    {
        // 排在建行之前：第一帧排版就要按这一版配色来（和 Options.ApplyUiTheme 同一个理由）。
        UiTheme.Apply(this, UiThemeValue);
        GetNode<Label>(TitlePath).Text = Tr("预设生活模式");
        GetNode<Button>(ClosePath).Text = Tr("关掉");
        GetNode<Button>(ClosePath).Pressed += Close;
        Refresh();
        CloseRequested += Close;
        // 摆在主屏正当中：引擎给的位置是 (0,0)（同 Options._Ready 那一段的理由）。
        var work = DisplayServer.ScreenGetUsableRect(0);
        Position = new Vector2I(work.Position.X + (work.Size.X - Size.X) / 2,
            work.Position.Y + (work.Size.Y - Size.Y) / 2);
        Show();   // 场景里 visible=false：OS 窗到这一行才建
    }

    /// <summary>换语言之后把字重灌一遍（<see cref="UiTheme"/> 的角色是覆写，换语言也要重刷）。
    /// <b>列表按当前那一份重新列一次</b>：读得通那一档的「出处」是文档里的注释，
    /// 换语言不改它，而收不进来那一档的理由也不改 —— 重列是为了让「关掉」和「收进来」两颗钮
    /// 换过来。</summary>
    public void Texts()
    {
        GetNode<Label>(TitlePath).Text = Tr("预设生活模式");
        GetNode<Button>(ClosePath).Text = Tr("关掉");
        UiTheme.Paint(this);
        Refresh();
    }

    private void Close()
    {
        Log("presets", "画廊关了");
        QueueFree();
    }

    /// <summary>关掉这一扇窗。<b>给设置窗和自检用</b>：<see cref="Close"/> 是按那颗钮和 Esc 走的
    /// 接线，这里是「这一趟完了」。</summary>
    public void Dismiss() => QueueFree();

    /// <summary>列那一个目录，并把该说的那一档说出来。<b>三档各说各的</b>：
    /// <list type="bullet">
    /// <item><b>目录整个不在</b>：抬头下面挂一句说「带在安装里的那几份一份也取不到」，
    /// 列表空着。<b>不是</b>「一个都没有」—— 那会把一次不完整的安装说成产品没有这族东西。</item>
    /// <item><b>目录在、那几份读不通</b>：那一列照样列出来（名字在、那颗钮灰着、它自己说为什么），
    /// 抬头下面另有一句点名这一档。<b>读得通的那几列照常能收</b>。</item>
    /// <item><b>目录在、一个都没有</b>：抬头下面写「一个预设生活模式都没有」。</item>
    /// </list></summary>
    public void Refresh()
    {
        var gallery = PresetLifeModes.Load(PresetDir);
        var list = GetNode<VBoxContainer>(ListPath);
        var template = list.GetNode<Control>("Template");
        // 先摘下来再 QueueFree：排队等删的那一帧它**还在树上**，而「界面上摆着几列」
        // 这一句是判据量的一等公民（同 BuildChecklist 那一段的注释）。
        foreach (var child in list.GetChildren().Where(c => c != template).ToList())
        {
            list.RemoveChild(child);
            child.QueueFree();
        }
        _shown.Clear();

        var broken = 0;
        foreach (var preset in gallery.Presets)
        {
            var line = (VBoxContainer)template.Duplicate();
            line.Visible = true;
            var name = line.GetNode<Label>("Head/Name");
            var take = line.GetNode<Button>("Head/Take");
            var note = line.GetNode<Label>("Note");
            name.Text = preset.Name;                     // 名字是文件名去掉后缀（数据，不翻）
            take.Text = Tr("收进来");
            take.Disabled = !preset.Readable;            // 读不通的那一份摆出来但点不动
            take.Pressed += () => Take(preset);          // 名字抄进闭包
            note.Text = preset.Note;                     // 出处 / 读不通的理由
            note.Visible = preset.Note.Length > 0;
            if (!preset.Readable) broken++;
            list.AddChild(line);
            _shown.Add(new Shown(preset, name, note, take));
        }
        UiTheme.Paint(this);   // Duplicate 不带覆写，摆出来之后按角色再刷一遍

        var status = GetNode<Label>(StatusPath);
        var err = GetNode<Label>(ErrPath);
        err.Visible = false;
        if (gallery.Missing)
        {
            status.Text = Tr("这些是随安装带来的只读模板，收一个进来才算自己的");
            Say(Tr("预设生活模式那一整个目录不在了 —— 带在安装里的那几份一份也取不到"), true);
        }
        else if (gallery.None)
        {
            status.Text = Tr("一个预设生活模式都没有");
        }
        else if (broken > 0)
        {
            status.Text = Tr("这些是随安装带来的只读模板，收一个进来才算自己的");
            Say(Tr("有的预设生活模式读不通，那一份收不进来"), true);
        }
        else
        {
            status.Text = Tr("这些是随安装带来的只读模板，收一个进来才算自己的");
        }
        Log("presets", $"列了 {gallery.Presets.Count} 份（读不通 {broken} 份，目录不在={gallery.Missing}）：" +
                       string.Join("、", gallery.Presets.Select(p => p.Name)));
    }

    /// <summary>收一个。<b>只调 <see cref="LifeModeStore.TakeIn(string,string,byte[])"/> 那一处</b>：
    /// 递增号、判重、搬字节、写名单、留备份全在那一族里（设计文档 `docs/design/preset-life-mode.md`「收一个进来走现成的导入那条路」）。
    ///
    /// 三档没成的话由这一扇窗说：<b>重名那一档指向「盖回一个生活模式…」</b>（和从文件收进来
    /// 那一支是<b>同一个</b> <see cref="LifeModeStore.Verdict.Taken"/> 判出来的，同一句话，
    /// 不是两处各判一次）。
    ///
    /// <b>收进来的那一刻不切</b>：和从文件收进来一样，「什么时候切」是另一个动作。</summary>
    private void Take(PresetLifeModes.Preset preset)
    {
        if (DataDir.Length == 0)
        {
            // 到不了这一格（设置窗一定递目录进来），真到了就照实说，而不是默默收进别的地方。
            Log("presets", $"画廊没有拿到设置所在目录，预设「{preset.Name}」按下去什么也不会收进来");
            return;
        }
        var bytes = PresetLifeModes.BytesOf(preset);
        if (bytes == null)
        {
            Say(Tr("预设生活模式「{0}」读不通，所以没有收进来").Replace("{0}", preset.Name), true);
            Log("presets", $"预设「{preset.Name}」的字节没读出来（{preset.Path}）");
            return;
        }
        var took = LifeModeStore.TakeIn(DataDir, preset.Name, bytes);
        if (took.Error != null)
        {
            var text = took.Collided
                ? $"{took.Error} —— {Tr("要盖回去就按「盖回一个生活模式…」")}"
                : took.Error;
            Say(text, true);
            Log("presets", $"收预设生活模式「{preset.Name}」没成：{took.Error}；磁盘和界面都没动");
            return;
        }
        // **和从文件收进来是同一句**：这一扇窗没有自己的「收好了」措辞，所以「走的是同一条路」
        // 在屏幕上也看得出来（反过验时把这一句换掉，那一条判据就红 —— 见工单 05 的实测结果）。
        Say($"{Tr("已收下生活模式")}「{preset.Name}」，{Tr("你现在这份配置没动过")}", false);
        Log("presets", $"收预设生活模式「{preset.Name}」：{preset.Path} 逐字节搬进 " +
                       $"{LifeModeStore.PathOf(DataDir, took.Taken!)}，活的那一份没动，切不切是另一个动作");
        Took?.Invoke();
    }

    private void Say(string text, bool bad)
    {
        if (bad)
        {
            var err = GetNode<Label>(ErrPath);
            err.Text = text;
            err.Visible = true;
        }
        else
        {
            GetNode<Label>(ErrPath).Visible = false;
            GetNode<Label>(StatusPath).Text = text;
        }
    }

    // ---- 自检 ----------------------------------------------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOnHost Gallery = new(
        () => "预设生活模式画廊通过：装在程序里那一个目录是只读的（收一个、撞名、切换、" +
              "覆盖、删掉、导出这一整趟走完它一个字节没动，模拟一次升级之后用户收进来的那一份逐字节原样、" +
              "新加的那一份还是一份预设）；" +
              "「收一个」走的是导入那条路（号比已有的大、逐字节搬、活的那一份没动、没多出备份，" +
              "撞名当场拦住并指向「盖回」而换个没撞的名字同一个钮就通）；" +
              "切过去之后域内每一键都成了它那份值而域外那几条一个字节没动，导出的那一份再收回来逐字节相同；" +
              "收进来的那一份能覆盖（同一个文件同一个号）、能删、编号不回收；" +
              "读不通两档各说各的（目录整个不在 / 那一份读不通，两句互不为子串，而读得通的那几列照常收得进来）；" +
              "出处是从文档注释里读出来的，而名字就是文件名去掉后缀",
        "预设生活模式画廊", SelfTest);

    /// <summary>画廊那一组（组名 <c>presets-gallery</c>，工单 05）。
    ///
    /// 判据分五段，各配对照（<c>CODING_STANDARDS.md</c> 第 5 条、第 8 条）：
    /// <list type="bullet">
    /// <item><b>只读</b>：开窗、收一个、撞名、切换、覆盖、删掉、导出这一整趟走完，
    /// <b>装在程序里那一个目录（自检里是它自己那一个临时目录）一个字节没动</b>；
    /// 而<b>模拟一次「升级」</b>（改掉那一份、再加一份新的）之后，<b>用户收进来的副本逐字节原样</b>，
    /// 新加的那一份<b>还是一份预设</b>（读得通、是一份完整的配置文档），而画廊下一次列得出它。</item>
    /// <item><b>走的是导入那条路</b>：拿的号比已有的都大、撞名当场拦住<b>并指向「盖回」</b>、
    /// 撞名时名单一条不多、目录里不多出文件、活的那一份一个字节没动<b>而且没多出备份</b>
    /// （收进来不是导入）。<b>对照</b>：换一列没撞的按，同一颗钮就收得进来。</item>
    /// <item><b>逐字节相同</b>：搬进来的和预设那一份一个字节一样；切过去之后域内每一键都成了
    /// 它那份值（<b>域外那几条一个字节没动</b>）；导出的那一份再收回来逐字节相同。
    /// 键名和域都<b>从声明表问</b>，不抄一份清单。</item>
    /// <item><b>和手存的完全同权</b>：能切、能覆盖（同一个文件同一个号）、能删（编号不回收）、
    /// 能导出。</item>
    /// <item><b>读不通两档各说各的</b>：目录整个不在 / 那一份读不通 —— 各自的句子互不相同、
    /// 互不为子串，<b>而读得通的那几列照常收得进来</b>（两段的对照都是「同一扇窗指着同一个目录
    /// 时列得出、收得进」）。</item>
    /// </list>
    ///
    /// 这一组只跑 <c>.tmp</c> 里自己那两个用完就删的子目录（设置那一份 + <b>它自己那一个预设目录</b>），
    /// 一条都不碰 <c>%APPDATA%</c>，也<b>不碰装在程序里那一个目录</b>（只读它，见上面第一段末尾
    /// 那一发单独量它）。</summary>

    public static List<string> SelfTest(Node parent)
    {
        var checks = new Checks();
        var dataDir = OptionsHost.TempDir("presets-data");
        var presetDir = Path.Combine(dataDir, "installed");
        Presets? win = null;
        var utf8 = new System.Text.UTF8Encoding(false);
        var notes = new List<string>();

        // 预设的正文：**一份完整的配置文档**（和「导出」那一份同一种格式），值给成和默认**不一样**的
        // 那些，切过去之后「切没切成」才量得出来（整份一样的话那几处相等是「什么都没换」）。
        // 键名从声明表问，域也是 —— 下面 SystemOnlyKeys 那一段就是同一件事的另一半。
        string PresetDoc(string head, int timeoutMin, int fadeMs) => $$"""
            {{head}}
            {
              "{{Settings.VersionKey}}": {{Settings.SchemaVersion}},
              "activityTimeoutMin": {{timeoutMin}},
              "flow": "fadetop",
              "fadeInMs": {{fadeMs}},
              "maxOpacityPercent": 60,
              "routine": {
                "enabled": true,
                "blocks": [
                  { "startMinute": 1350, "endMinute": 390, "name": "夜里", "kind": "睡眠",
                    "fade": false, "flow": "", "yieldsToInput": true }
                ]
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(presetDir);
            // 顶上那一段 `//` 就是**出处**（注释即考证，ADR-0005）—— 画廊里那一行显示的就是它，
            // 所以这里给一段真的话，好让「出处从文档里读出来」这一条量得到东西。
            var one = Path.Combine(presetDir, "夜里的那一套.jsonc");
            var two = Path.Combine(presetDir, "白天的那一套.jsonc");
            var broken = Path.Combine(presetDir, "半截.jsonc");
            File.WriteAllText(one, PresetDoc(
                "// 出处：这一份的数是照着夜里那一段作息摆的，没有出处可考的地方都取了默认值。\n" +
                "// Provenance: a night-shaped routine; anything unsourced keeps the default.", 7, 4_000), utf8);
            File.WriteAllText(two, PresetDoc(
                "// 出处：同一族里的第二个样子，用来量「换一列没撞的按就收得进来」。\n" +
                "// Provenance: a second shape, so the non-colliding take-in can be measured.", 23, 6_000), utf8);
            // 读不通那一档：**截断的**一份（不是空的、不是缺后缀的）—— 目录在、别的读得通、
            // 只有它收不进来，而那一列照样列出来。
            File.WriteAllText(broken, "{ \"activityTimeoutMin\": 7, \"routine\": { \"blocks\": [ ", utf8);

            // 活的那一份：域内域外都给成**非默认值**，域外那三条留在默认值上时「切过去之后它们没变」
            // 什么都量不出来（flows-menu 那一组已经踩过这个坑）。
            var live = new Settings { ActivityTimeoutMin = 40, FadeInMs = 3_000, Language = "en", UiTheme = "dark", Autostart = true };
            if (SettingsStore.Save(dataDir, new SettingsStore.Read { Current = live }).Error != null)
                return ["活的那一份没写成，这一组自己的底稿就没了"];
            if (Lang.Apply("zh").Count > 0) return ["语言表没装上，这一组量的不是屏幕上那些字"];
            // 先存一个手存的生活模式：收进来的那一个要拿它当「号更大」和「活的那一份是哪一个」的对照。
            if (LifeModeStore.SaveAs(dataDir, "原来那一个", live).Error != null)
                return ["存那个手存的生活模式没成"];

            if (GD.Load<PackedScene>(ScenePath)?.Instantiate<Presets>() is not Presets made)
                return [$"{ScenePath} 实例化不出来，这一组没法跑"];
            win = made;
            win.DataDir = dataDir;
            win.PresetDir = presetDir;
            win.UiThemeValue = "light";
            win.Log = (_, message) => notes.Add(message);
            // **只读那一把尺子的基线在开窗之前取**：一开窗它就列一遍那个目录，而「列」这一步要是
            // 顺手写了什么（缓存、索引、大小写规范化），基线就已经脏了 —— 那样「走完这一趟没动」
            // 量的是「第二次之后没动」，第一次那一次写掉了。
            var installedBefore = DirBytes.Shot(presetDir);
            parent.AddChild(win);

            // ---- 段一：列出来的那几列，和「读不通的那一份照样列出来」 --------------------
            checks.Check("列出那一个目录", () =>
            {
                var shown = win!.Rows;
                var onDisk = Directory.GetFiles(presetDir, "*" + LifeModeNumbers.Suffix).Length;
                if (shown.Count != onDisk)
                    throw new Exception($"界面上列了 {shown.Count} 列，目录里 {onDisk} 份 —— " +
                                        $"这一组量的是「列出来的那几列」这一件事本身");
                var names = shown.Select(s => s.Preset.Name).ToList();
                foreach (var wanted in new[] { "夜里的那一套", "白天的那一套", "半截" })
                    if (!names.Contains(wanted))
                        throw new Exception($"界面上没有「{wanted}」那一列（列着的是 {string.Join("、", names)}）");
                // **两颗钮都是真控件**（ADR-0001「界面默认用真控件」）：一颗 Button，不是一个 Label。
                if (shown.Any(s => s.Take.GetType() != typeof(Button)))
                    throw new Exception("有一列那颗「收进来」不是 Button");
                if (shown.Any(s => !s.Name.Visible))
                    throw new Exception("有一列的名字没露出来");
            });

            checks.Check("读不通的那一份：列出来、点不动、它自己说为什么，而读得通的那两列照常能收", () =>
            {
                var shown = win!.Rows;
                var bad = shown.FirstOrDefault(s => s.Preset.Name == "半截")
                    ?? throw new Exception("「半截」那一列不在（这一发量的是读不通那一档，不是「没列出来」）");
                if (bad.Preset.Readable) throw new Exception("「半截」被说成读得通");
                if (!bad.Take.Disabled) throw new Exception("读不通的那一份那颗钮点得动");
                if (bad.Note.Text.Trim().Length == 0) throw new Exception("读不通的那一份没有说为什么");
                // 读不通的理由不许是「什么都没有」：它得是一句人话，且**不是**别那一档那句话
                // （两档互不为子串 —— 03 在生活模式那一族量过同一对假象）。
                if (bad.Note.Text.Contains(TranslationServer.Translate(
                        "预设生活模式那一整个目录不在了 —— 带在安装里的那几份一份也取不到").ToString(),
                        StringComparison.Ordinal))
                    throw new Exception("读不通那一档说的话和「目录不在」那一档是同一句");
                // **对照**：同一个目录里读得通的那两列是能按的 —— 证明上面量的是「读不通」，
                // 不是「什么按钮都点不动」。
                var good = shown.Where(s => s.Preset.Readable).ToList();
                if (good.Count == 0) throw new Exception("一列读得通的都没有，对照不成立");
                if (good.Any(s => s.Take.Disabled)) throw new Exception("读得通的那一列却是灰的");
                if (good.Any(s => s.Note.Text.Trim().Length == 0))
                    throw new Exception("读得通的那一列没有出处那一行 —— 出处是从文档注释里读出来的，" +
                                        "读不到就说明没读");
            });

            // ---- 段二：收一个走的是导入那条路 -------------------------------------------
            var livePath = SettingsStore.FilePath(dataDir);
            var liveBefore = File.ReadAllBytes(livePath);
            var hadEntry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find("夜里的那一套");
            if (hadEntry != null) checks.Fail("这一组自己的底稿就有「夜里的那一套」了，撞名那一发量不到");

            checks.Check("收一个：递增号分配、原样搬、活的那一份没动、没多出备份", () =>
            {
                Press(win!, "夜里的那一套");
                var index = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir));
                var entry = index.Find("夜里的那一套")
                    ?? throw new Exception("收完了，名单里没有刚收进来的那一条");
                if (!LifeModeNumbers.TryNumberIn(entry.File, out var number))
                    throw new Exception($"{entry.File} 读不回成号");
                foreach (var sibling in index.Entries.Where(o => o.File != entry.File))
                    if (LifeModeNumbers.TryNumberIn(sibling.File, out var other) && other >= number)
                        throw new Exception($"收进来的那一份拿的是 {entry.File}，不大于 {sibling.File} —— " +
                                           "递增号没有比已有的大");
                if (!File.ReadAllBytes(LifeModeStore.PathOf(dataDir, entry))
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(presetDir, "夜里的那一套.jsonc"))))
                    throw new Exception("收进来的那一份和预设那一份不是逐字节相同");
                if (!File.ReadAllBytes(livePath).SequenceEqual(liveBefore))
                    throw new Exception("收进来的那一刻活的那一份动了一字 —— 收进来不是导入");
                if (File.Exists(SettingsStore.BackupPath(dataDir)))
                    throw new Exception("收进来的那一趟留了一份备份 —— 收进来不是导入（导入才留备份）");
                // 收进来的那一份**在窗上说了话**，而且是「你现在这份没动过」那一句 ——
                // 和从文件收进来那一族共用同一个键，所以这一条量的就是「走的是同一条路」。
                if (!win!.StatusSaid.Contains(TranslationServer.Translate("你现在这份配置没动过").ToString(),
                        StringComparison.Ordinal))
                    throw new Exception($"收完没在抬头下面说「你现在这份配置没动过」：'{win.StatusSaid}'");
            });

            checks.Check("撞名：当场拦住并指向「盖回」，名单一条不多、目录不多一份、撞上那一份没被动", () =>
            {
                var before = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir));
                // **撞名那一档的前提是「那一条已经在名单上」**。少了它，后面读 `entry.File` 就是一次
                // 空引用，而红话是一句 `Object reference not set` —— 量不出「撞名」这件事本身
                // （2026-10-02 反向验证撞到过：把收进来的名字改掉之后，报出来的是空引用）。
                var entry = before.Find("夜里的那一套")
                    ?? throw new Exception("这一组自己的底稿里没有刚收进来的那一条，撞名那一档量不到");
                var bytes = File.ReadAllBytes(LifeModeStore.PathOf(dataDir, entry));
                Press(win!, "夜里的那一套");
                if (!win!.ErrorShown || !win.ErrorSaid.Contains("盖回", StringComparison.Ordinal))
                    throw new Exception($"撞了重名，那句话没有指向「盖回」：'{(win.ErrorShown ? win.ErrorSaid : "（没露出来）")}'");
                if (win.ErrorSaid.Contains(TranslationServer.Translate("你现在这份配置没动过").ToString(),
                        StringComparison.Ordinal))
                    throw new Exception("撞了重名，抬头下面却还说收成了");
                var after = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir));
                if (after.Entries.Count != before.Entries.Count) throw new Exception("撞了重名，名单里却多了一条");
                if (!File.ReadAllBytes(LifeModeStore.PathOf(dataDir, entry)).SequenceEqual(bytes))
                    throw new Exception("撞了重名，被撞上的那一份被改了");
            });

            checks.Check("对照：换一列没撞的按，同一颗钮就收得进来", () =>
            {
                Press(win!, "白天的那一套");
                if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find("白天的那一套") == null)
                    throw new Exception("没撞名的那一列也没收进来（对照不通，上面那几条可能是" +
                                        "「什么都收不进」的假绿）");
            });

            checks.Check("只读：这一整趟走完，预设那一个目录一个字节没动", () =>
            {
                if (DirBytes.Shot(presetDir) != installedBefore)
                    throw new Exception($"预设目录变了：装进去之前是 {installedBefore}，" +
                                       $"走完这一趟是 {DirBytes.Shot(presetDir)}");
            });

            // ---- 段三：切过去 → 导出 → 再收回来，逐字节相同 -----------------------------
            checks.Check("切过去：域内每一键都成了它那份值，域外那几条一个字节没动", () =>
            {
                var state = SettingsStore.Load(dataDir);
                if (state.Error != null) throw new Exception($"活的那一份读不通：{state.Error}");
                var (switchError, _) = LifeModeStore.SwitchTo(dataDir, state, "夜里的那一套");
                if (switchError != null) throw new Exception($"切不过去：{switchError}");
                var after = SettingsStore.Load(dataDir);
                if (after.Error != null) throw new Exception($"切过去之后活的那一份读不通：{after.Error}");
                // 预设那一份读成一份设置 —— 比的是**值**，不是字节（切是整份换掉，不是一次拷贝）。
                var preset = ReadAsSettings(Path.Combine(presetDir, "夜里的那一套.jsonc"));
                // **键名和域都从声明表问**：加一条作息表的键，这几处自动跟着它走。
                // **只比叶子键**（<see cref="SettingDecl.IsContainer"/> 那一族没有绑在属性上的值 ——
                // 那几张表另有各的读法：作息表和休息流程表由 routine-blocks / flows 那一族量）。
                var compared = 0;
                foreach (var decl in Settings.Declarations.Where(
                             d => LifeModeStore.InLifeMode(d) && !d.IsContainer))
                {
                    if (preset.Map.Get(decl.Key)?.ToString() != after.Current.Map.Get(decl.Key)?.ToString())
                        throw new Exception($"切过去之后 {decl.Key} 是 {after.Current.Map.Get(decl.Key)}，" +
                                           $"该是预设那一份的 {preset.Map.Get(decl.Key)}");
                    compared++;
                }
                if (compared == 0) throw new Exception("声明表里一条域内的叶子键都没有 —— 这一发什么也没比");
                foreach (var key in SystemOnlyKeys())
                    if (after.Current.Map.Get(key)?.ToString() != live.Map.Get(key)?.ToString())
                        throw new Exception($"切过去之后域外的 {key} 动了 —— 收进来的那一份带着不一样的值，" +
                                           "而系统那几条切的时候照旧一个字节都不动");
                // **对照的对照**：切过去确实换了整份（不是「什么都没换」那种相等）。
                if (after.Current.ActivityTimeoutMin == live.ActivityTimeoutMin)
                    throw new Exception("切过去之后活的那一份和切之前一样 —— 「域外没动」量的是「什么都没换」");
            });

            checks.Check("导出那一份再收回来，逐字节相同（收进来的那一份是预设形状的那一把尺子）", () =>
            {
                var state = SettingsStore.Load(dataDir);
                var outPath = Path.Combine(dataDir, "out-from-taken.jsonc");
                if (SettingsStore.Export(dataDir, state, outPath) != null) throw new Exception("导出没成");
                var exported = File.ReadAllBytes(outPath);
                var back = LifeModeStore.TakeIn(dataDir, "导出来的那一份", outPath);
                if (back.Error != null) throw new Exception($"把导出的那一份收回来没成：{back.Error}");
                var entry = back.Taken!;
                if (!File.ReadAllBytes(LifeModeStore.PathOf(dataDir, entry)).SequenceEqual(exported))
                    throw new Exception("导出的那一份再收回来不是逐字节相同");
                // **注释和陌生的键跟着走**：长度一样也可能内容不一样，所以另问一次。
                var text = File.ReadAllText(LifeModeStore.PathOf(dataDir, entry));
                if (!text.Contains(Settings.VersionKey, StringComparison.Ordinal))
                    throw new Exception("收回来的那一份里没有格式版本那一行 —— 逐字节相同量的可能是个空壳");
            });

            // ---- 段四：和手存的完全同权：能覆盖、能删、编号不回收 -------------------------
            // 先把那两个文件名**问出来**再动手：抄一份 "0002.jsonc" 进判据就是会腐烂的副本
            // （发号策略哪天变了判据就量着一份不存在的文件）。
            var coveredName = "白天的那一套";
            var coveredBefore = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find(coveredName)
                ?? throw new Exception("这一组自己的底稿里没有「白天的那一套」，覆盖那一发量不到");
            var covered = LifeModeStore.Overwrite(dataDir, coveredName, new Settings { ActivityTimeoutMin = 31 });
            checks.Check("能覆盖：同一个文件同一个号，而预设那一份没被动", () =>
            {
                if (covered.Error != null) throw new Exception($"盖不回去：{covered.Error}");
                var entry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find(coveredName)
                    ?? throw new Exception("盖回去之后名单上那一条没了");
                if (entry.File != coveredBefore.File)
                    throw new Exception($"盖回去写的是 {entry.File}，原来那一份是 {coveredBefore.File} —— " +
                                       "「盖回去」是同一个文件同一个号");
                // 读回来的是**那一份文件**（不是活的那一份 —— 活的那一份上一次切的是另一个）。
                if (ReadAsSettings(LifeModeStore.PathOf(dataDir, entry)).ActivityTimeoutMin != 31)
                    throw new Exception("盖回去之后读回来的不是盖进去那个值");
                if (DirBytes.Shot(presetDir) != installedBefore)
                    throw new Exception("盖回去之后预设那一个目录变了");
            });

            var deletedPath = LifeModeStore.PathOf(dataDir, coveredBefore);
            var deleted = LifeModeStore.Delete(dataDir, coveredName);
            checks.Check("能删：名单上没了、磁盘上那份也没了，编号不回收", () =>
            {
                if (deleted.Error != null) throw new Exception($"删不掉：{deleted.Error}");
                if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find(coveredName) != null)
                    throw new Exception("删掉了，名单上还有");
                if (File.Exists(deletedPath)) throw new Exception($"删掉了，{coveredBefore.File} 还在磁盘上");
            });

            checks.Check("删掉之后下一个收进来的号更大（编号不回收）", () =>
            {
                var before = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir));
                var highest = before.Entries
                    .Select(e => LifeModeNumbers.TryNumberIn(e.File, out var n) ? n : 0)
                    .DefaultIfEmpty(0).Max();
                Press(win!, "白天的那一套");
                var entry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find("白天的那一套")
                    ?? throw new Exception("删掉之后同一个名字收不进来了");
                if (!LifeModeNumbers.TryNumberIn(entry.File, out var number) || number <= highest)
                    throw new Exception($"删掉之后拿的是 {entry.File}，不大于删之前最大的 {highest}");
            });

            // ---- 段五：模拟一次「升级」 -------------------------------------------------
            checks.Check("模拟一次升级：用户收进来的那一份逐字节原样，而新加的那一份还是一份预设", () =>
            {
                var userCopy = LifeModeStore.PathOf(dataDir,
                    LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find("夜里的那一套")!);
                var userBytes = File.ReadAllBytes(userCopy);
                // 升级干两件事：改一份、加一份。**两件都只动那个只读目录**（自检里是它自己那一份
                // 临时目录；装在程序里那一个一个字节都不碰 —— 见段六）。
                File.WriteAllText(Path.Combine(presetDir, "夜里的那一套.jsonc"),
                    PresetDoc("// 出处：升级之后换的一份。\n// Provenance: replaced by the upgrade.", 9, 4_000), utf8);
                var added = Path.Combine(presetDir, "新加的那一套.jsonc");
                File.WriteAllText(added, PresetDoc(
                    "// 出处：升级新加的一份。\n// Provenance: added by the upgrade.", 31, 5_000), utf8);

                // **先让画廊重新列一遍，再问用户那一份**：升级之后用户会做的头一件事就是打开（或
                // 按一下那颗钮让）画廊重列一次，而「顺手把用户收进来的那一份也刷新成新装的那一份」
                // 正是 ADR-0005 说的那种打架（改过的被盖、没改的分不清谁是谁）。次序反过来就量不到
                // 它 —— 2026-10-02 第一版就是先比字节后重列，反向验证时这一条**没红**，当场改的。
                win!.Refresh();
                if (!File.ReadAllBytes(userCopy).SequenceEqual(userBytes))
                    throw new Exception("升级之后用户收进来的那一份动了 —— 「收进来才算自己的」是那一份，升级不许碰");
                if (LifeModeIndex.Load(LifeModeStore.ListPathOf(dataDir)).Find("夜里的那一套") == null)
                    throw new Exception("升级之后用户那一条从名单上没了");
                // 切得回去：它还是一个生活模式，不是一份被升级改坏的残留。
                var state = SettingsStore.Load(dataDir);
                var (backError, _) = LifeModeStore.SwitchTo(dataDir, state, "夜里的那一套");
                if (backError != null) throw new Exception($"升级之后用户那一条切不过去了：{backError}");
                if (SettingsStore.Load(dataDir).Current.ActivityTimeoutMin != 7)
                    throw new Exception("升级之后切过去，用户那一份里的值不是它自己的了");
                // **新加的那一份还是一份预设**：读得通、是一份完整的配置文档。
                if (PresetLifeModes.Why(File.ReadAllText(added)) is { } bad)
                    throw new Exception($"升级新加的那一份不像一份预设了：{bad}");
                if (!win.Rows.Any(s => s.Preset.Name == "新加的那一套"))
                    throw new Exception("升级新加的那一份没有出现在画廊里（对照：下面问的是「多了一列」）");
                if (win.Rows.Count != Directory.GetFiles(presetDir, "*" + LifeModeNumbers.Suffix).Length)
                    throw new Exception("升级之后画廊里列的列数和目录里的份数对不上");
            });

            // ---- 段六：两档读不通，各说各的；只读装在程序里那一个 ---------------------------
            checks.Check("目录整个不在：是一档说得清的话，列表空着，不崩溃也不静默", () =>
            {
                var missing = Path.Combine(dataDir, "no-such-folder");
                // **对照**：同一扇窗指着那个目录时列得出、收得进 —— 证明「空列表」量的是
                // 「目录不在」，不是「这个窗什么都列不出来」。
                var before = win!.Rows.Count;
                win.PresetDir = missing;
                win.Refresh();
                if (win.Rows.Count != 0) throw new Exception($"目录不在，界面上却还有 {win.Rows.Count} 列");
                if (!win.ErrorShown || win.ErrorSaid.Trim().Length == 0)
                    throw new Exception("目录不在，没有说一句话（那是静默，不是状态）");
                var missingSaid = win.ErrorSaid;
                if (missingSaid.Contains(Settings.VersionKey, StringComparison.Ordinal)
                    || missingSaid.Contains("presets", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"目录不在那句话里有配置文件内部的名字：'{missingSaid}'");
                // **两档互不为子串**：目录不在 ≠ 读不通（03 在生活模式那一族量过同一对假象）。
                if (missingSaid == ReadUnreadableSaid(win, "半截"))
                    throw new Exception("目录不在和读不通说的是同一句");
                win.PresetDir = presetDir;
                win.Refresh();
                if (win.Rows.Count != before) throw new Exception("指回那个目录就列不出来了（对照不成立）");
            });

            checks.Check("装在程序里那一个目录：列得出、读得通、而自检里那一组一个字节都没往里写", () =>
            {
                var shipped = PresetLifeModes.Load();
                if (shipped.Missing) throw new Exception($"{PresetLifeModes.Dir} 不在 —— 随安装没带那一份");
                if (shipped.None) throw new Exception($"{PresetLifeModes.Dir} 里一个都没有");
                if (shipped.Presets.Any(p => !p.Readable))
                    throw new Exception($"{PresetLifeModes.Dir} 里有读不通的：" +
                                        string.Join("、", shipped.Presets.Where(p => !p.Readable).Select(p => p.Name)));
                if (shipped.Presets.Any(p => p.Note.Length == 0))
                    throw new Exception($"{PresetLifeModes.Dir} 里有没写出处的：注释即考证（ADR-0005）");
                // 名字是**文件名去掉后缀**，不是另一份名单（预设不是第二种实体）。
                foreach (var preset in shipped.Presets)
                {
                    var stem = Path.GetFileNameWithoutExtension(preset.Path);
                    if (preset.Name != stem) throw new Exception($"「{preset.Name}」这一列的名字不是文件名");
                }
            });
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            if (win != null) { parent.RemoveChild(win); win.QueueFree(); }
            OptionsHost.DropDir(dataDir);
        }
        return checks.Report();

        // 界面上按那一列的「收进来」——按**名字**找，不按下标（名字取自
        // <see cref="PresetLifeModes.Preset.Name"/>，也就是它自己那一列写着的字）。
        void Press(Presets gallery, string presetName)
        {
            var row = gallery.Rows.FirstOrDefault(r => r.Preset.Name == presetName)
                ?? throw new Exception($"界面上没有「{presetName}」那一列（列着的是 " +
                                       $"{string.Join("、", gallery.Rows.Select(r => r.Preset.Name))}）");
            if (row.Take.Disabled) throw new Exception($"「{presetName}」那一列点不动");
            row.Take.EmitSignal(BaseButton.SignalName.Pressed);
        }

        // 「读不通那一档说的那一句」——用来判两档**不是同一句**。
        string ReadUnreadableSaid(Presets gallery, string presetName) =>
            gallery.Rows.FirstOrDefault(r => r.Preset.Name == presetName)?.Note.Text ?? "";

        // 预设那一份**读成一份设置**（和读活的那一份同一条读法：讲得通 → 夹取值 → 比）。
        // 键名从声明表问，所以下面那几处是「每一键」而不是一份手抄的清单。
        Settings ReadAsSettings(string path)
        {
            if (!JsoncDoc.TryParse(File.ReadAllText(path), out var doc, out var error))
                throw new Exception($"{Path.GetFileName(path)} 读不成一份配置文档：{error}");
            var settings = new Settings();
            var problems = settings.Apply(doc!.Values);
            if (problems.Count > 0)
                throw new Exception($"{Path.GetFileName(path)} 读出来有 {problems.Count} 条问题：{string.Join("；", problems)}");
            settings.ClampToDeclarations();
            return settings;
        }

        // 域外那几条（设计文档 `docs/design/life-mode.md`「凡是归系统那一域的键一律不进生活模式」）——**从声明表问**，
        // 不抄一份键名清单（加一条系统级设置，这里自动跟着它走）。同上一段：只列叶子键。
        IEnumerable<string> SystemOnlyKeys() =>
            Settings.Declarations.Where(d => !LifeModeStore.InLifeMode(d) && !d.IsContainer).Select(d => d.Key);
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOnHost Entries = new(
        () => "画廊在设置窗那一侧的入口通过：它就是窗头那颗生活模式按钮展开的菜单下半那一条" +
              "「预设生活模式…」，按下去真的开窗、列得出那几份；同一个入口点第二遍开的是同一扇窗" +
              "（窗头原先那颗只管开画廊的常驻钮 2026-10-08 合并掉了，设计文档 `docs/design/preset-life-mode.md`「入口」那一节要的入口由这一条顶着）；" +
              "对照：按「存为新生活模式」开的是另一扇窗、不会把画廊也带出来；" +
              "托盘那一侧一个字节都没动（量在 lifemodes-tray 那一组）",
        "画廊在设置窗那一侧的入口", EntriesSelfTest);

    /// <summary>画廊在设置窗那一侧的入口（组名 <c>presets-entries</c>，工单 05）。
    ///
    /// <b>2026-10-08 合并</b>：这一组原先量<b>两处</b> —— 窗头一颗常驻的「预设生活模式…」按钮，
    /// 和窗头那颗生活模式按钮展开的菜单下半那一条 —— 而它们开的是<b>同一扇</b>窗。
    /// 所有者定合并成一颗，删掉常驻那颗，于是这一组现在只量<b>菜单里那一条</b>。
    /// 设计文档 `docs/design/preset-life-mode.md`「入口」那一节那句「画廊的入口在生活模式管理器和设置窗」仍然成立：设置窗这一侧的入口就是这一条
    /// （那一份里的「管理器」指的是<b>那一族</b>的界面，不是「那颗常驻按钮」）。
    /// **托盘那一侧一个字节都没动** —— 那一半在
    /// <see cref="Tray.LifeModeMenuSelfTest"/>（它量的是托盘那一棵树），这里只管设置窗。
    ///
    /// 两条判据各配对照（<c>CODING_STANDARDS.md</c> 第 5 条）：
    /// <list type="bullet">
    /// <item><b>菜单里那一项在，而且按下去真的开窗、列得出那几份</b>。
    /// 对照：按<b>另一个</b>项（存为新生活模式）开的是另一扇窗 ——
    /// 证明「开窗」量的是那一条接线，不是「按什么都开」。</item>
    /// <item><b>同一个入口点第二遍开的是同一扇窗</b>（原先这一条量的是
    /// 「两处入口各开各的一扇」——那是同一个动作的两个入口；合并之后设置窗这一侧只剩一条，
    /// 于是改成量「同一个入口不许开出第二扇窗」）。</item>
    /// </list>
    ///
    /// <b>窗头那颗常驻钮不许回来</b> —— 由 <c>Options.SelfCheck</c> 的
    /// <c>ModeMenuShape</c> 那一条守着（反向验过：把它加回 <c>Options.tscn</c>，那一发当次就红）。
    ///
    /// 这一组只碰 <c>.tmp</c> 里自己那一个用完就删的目录；画廊只<b>列</b>装在程序里那一个
    /// 预设目录，一个字节都不写（<see cref="PresetLifeModes"/> 那一段的说明）。</summary>

    public static List<string> EntriesSelfTest(Node parent)
    {
        var checks = new Checks();
        // 窗和那个临时目录归 `OptionsHost`（和 `Options` 里那九组同一份实施）——
        // 这一族量的是「画廊那两处入口」，而它得先有一扇真的设置窗才量得着。
        var host = OptionsHost.InTempDir("presets-entries");
        var dir = host.Dir;
        void Finish() => checks.Problems.AddRange(host.Problems);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion},\n  \"flow\": \"fadetop\"\n}}\n");
            var state = SettingsStore.Load(dir);
            // 底稿读不通这一档**只交这一句**：原先就是 `return [ … ]`，不是「把到这儿攒的都交出去」。
            // 这一行是照抄搬之前那次搬家时的原样（2026-10-12 核过），所以不改成 Report()。
            if (state.Error != null) return [$"底稿读不通：{state.Error}"];
            if (Lang.Apply("zh").Count > 0) { checks.Fail("语言表没装上，这一组量的不是屏幕上那些字"); return checks.Report(); }
            var win = host.Open(state, parent);   // 次序由 OptionsHost 定死

            var label = TranslationServer.Translate("预设生活模式…").ToString();
            var popup = win.GetNode<MenuButton>(Options.LifeModePath).GetPopup();

            // **对照先行**：菜单里随便按一个别的项，量一下「按菜单项」这件事本身会开窗 ——
            // 免得后面那一条量的是一个恒为「没开窗」的东西。
            if (!Options.HasItem(popup, "存为新生活模式"))
                return ["菜单下半没有「存为新生活模式」那一项，这一组的对照不成立"];
            Options.PressItem(popup, "存为新生活模式");
            if (!win.GetNode<Window>(Options.NamePath).Visible)
                return ["按了「存为新生活模式」，起名那扇小窗没开起来 —— 这一组量的是「有没有接线」，不是「这一项坏了」"];
            win.GetNode<Button>(Options.NameCancelPath).EmitSignal(Button.SignalName.Pressed);
            if (win.Gallery != null) checks.Fail("按了「存为新生活模式」却把画廊开起来了 —— 那一项接错线了");

            // 设置窗这一侧的入口是**菜单里那一条**（2026-10-08 合并掉了窗头那颗常驻钮 ——
            // 它和这一条是同一个动作的两处入口，而设计文档 `docs/design/preset-life-mode.md`「入口」那一节只要设置窗这一处）。
            if (!Options.HasItem(popup, label))
                checks.Fail($"窗头那颗生活模式按钮展开的菜单下半没有「{label}」那一项 —— " +
                            "窗头那颗常驻钮 2026-10-08 合并掉了，入口就靠这一条");
            else
            {
                Options.PressItem(popup, label);
                if (win.Gallery == null)
                    checks.Fail($"按了「{label}」，画廊没开起来");
                else if (win.Gallery.Rows.Count == 0 && !PresetLifeModes.Load().Missing)
                    checks.Fail("画廊开起来了，可它一列都没列 —— 那一发量的不是「入口在不在」");
            }

            // **同一条动作只有一个窗对象**：点第二次不许再开一扇新的。
            // （原先这里量的是「管理器那颗和菜单那一条各开各的」，那是同一个动作的两个入口；
            // 合并之后设置窗这一侧只剩菜单里那一条，于是这一句改成量「同一项点两遍」。）
            if (win.Gallery != null)
            {
                var opened = win.Gallery;
                if (Options.HasItem(popup, label))
                {
                    Options.PressItem(popup, label);
                    if (!ReferenceEquals(opened, win.Gallery))
                        checks.Fail("同一个入口点第二遍开出了另一扇画廊 —— 那是两扇窗、两个状态");
                }
            }
        }
        catch (Exception e)
        {
            checks.Fail($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            host.Dispose();   // 放窗 + 删目录；删不掉它自己说
            Finish();
        }
        return checks.Report();
    }
}
