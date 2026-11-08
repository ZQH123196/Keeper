using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

namespace Keeper;

/// <summary>设置窗。位置在 <c>res://src/Options.tscn</c> 里摆；这里只做四件事：按每行带的键名
/// 把它接到声明表上、编辑一份<b>工作副本</b>、按保存把副本搬回活的那一份并让
/// <see cref="SettingsStore"/> 原位编辑磁盘上的文档、把清单那份记录落盘。
///
/// 为什么控件上一个设置相关的数字都没有：设计文档 `docs/design/config.md`「每个设置只声明一次」那条要的是「一个限制只写
/// 一次」。范围、默认值、可选值全在 <see cref="Settings.Declarations"/>，滑块和数字框在这里
/// 问它要 —— 场景里出现的只有宽度和间距。
///
/// 为什么不直接改活的设置：「恢复默认」和关掉窗口不保存这两条都要能回去，而磁盘只在按保存那
/// 一次才动。副本和活设置之间只有 <see cref="Settings.AdoptValues"/> 这一条路。
///
/// flows 那张表在这里能改（<see cref="BuildFlowEditor"/> 那一节；那之前它只在文档里编辑）。
/// 它和上面那些按 <c>metadata/setting</c> 接声明表的行不是一套接线：一张表里的行是用户起的名字，每行下面又是一串步，
/// 不是一条声明对一个控件 —— 所以它的范围、默认值、可选值问的是
/// <see cref="Settings.StepDeclarations">step 那一份声明表</see>，值写在
/// <see cref="Flow.Steps"/> 上，落盘仍然走同一条原位编辑（而且能把一行删掉，
/// 见 <see cref="JsoncDoc"/> 里 owned 那一步）。文档仍然是那份文档：这里改的就是
/// settings.jsonc 里那几行，契约的立场没动（设计文档 `docs/design/config.md`）。</summary>
public partial class Options : Window
{
    // ---- App 接进来的东西 -------------------------------------------------------

    /// 活的设置就是那次读取带着的那一份。保存按 <see cref="Settings.AdoptValues"/> 搬值、
    /// 不换对象；<b>导入</b>换对象，所以那一份新的由 <see cref="Applied"/> 显式交回给 App ——
    /// 2026-09-29 之前这里靠的是「<c>Read.Current</c> 是个可变字段，导入就地换掉它」，
    /// 于是每个持有者都得<b>记得</b>重新取一次（这里的 <c>Live</c> 每次现取就是这么来的）。
    /// <see cref="SettingsStore.Read"/> 现在不可变，「记得重拿」这一类 bug 没有落脚的地方了。
    public Settings Live => State.Current;

    /// 那次读取的结果：磁盘原文、读不通的原因、格式版本。保存和导入都要它。
    /// 可写是因为写出去的路径各还一份<b>新</b>结果（<see cref="SettingsStore.Written"/>），
    /// 谁落笔谁把新的那一份接过来，然后交给 <see cref="Applied"/> 往上递。
    public SettingsStore.Read State { get; set; } = null!;

    public string DataDir = "";
    public Todos Checklist = null!;
    public Action ChecklistChanged = () => { };
    public Action<string, string> Log = (_, _) => { };

    /// 「预览遮罩」那颗，和托盘左键同一条路。
    public Action PreviewRequested = () => { };

    /// 抬头那一行的两个数：离下一次淡入还有多少分钟、休息记录多少条。
    /// （2026-10-06 之前是三个 —— 中间那半「健康度」跟着界面上那一处一起撤了。）
    public Func<(double NextMin, int Records)> Measurement = () => (0, 0);

    /// 值搬到活设置之后叫一次，<b>带着当下那一份读取结果</b>：托盘的图标、tooltip、
    /// 以及导入换掉的那个对象都归 App 管。
    /// 载荷是 2026-09-29 加的 —— 原来 App 那一边是「记得去 <c>_read.Current</c> 重新拿一次」，
    /// 因为导入就地换了那个字段指向的对象；现在 <see cref="SettingsStore.Read"/> 不可变，
    /// 新的那一份<b>只能</b>由这里递上去，忘不掉。
    public Action<SettingsStore.Read>? Applied;

    // ---- 这份副本和它的控件 ------------------------------------------------------

    private Settings _copy = null!;
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _byKey = [];
    private Action<string>? _picked;

    // flows 那一区（见 BuildFlowEditor）：哪一条在编辑、它叫什么、以及那几步。
    private OptionButton _picking = null!;
    private LineEdit _naming = null!;
    private VBoxContainer _flowBox = null!;
    private Control _stepTemplate = null!;
    private Label _total = null!;
    private string _editing = "";

    // 作息表那一区（见 BuildBlockEditor）：那张时间块表的几叠、它们的模板、底下那一句实测结果。
    // **别和上面那几行混**：上面是配置里那张 flows 表（一条休息流程 = 一串步），
    // 这一张是按现实时间走的一整天安排（一行一个时间块），词汇表里是两组词
    // （GLOSSARY.md「作息表」/「时间块」）。
    private VBoxContainer _blockBox = null!;
    private Control _blockTemplate = null!;
    private Label _blockTotal = null!;

    // 勿扰那一区（见 BuildDndEditor）：那张时段表的几叠、那张名单的行、它们各自的模板，
    // 底下那两句实测结果（时段一共盖住多少、名单几个名字），以及「当前前台是哪个程序」那一条
    // （实测结果走 App 那一侧 0.2 秒一发的钩子，见 <see cref="ForegroundApp"/> —— 这一扇窗自己
    // 问系统问出来的永远是它自己那个 exe）。
    private VBoxContainer _dndWindowBox = null!;
    private Control _dndWindowTemplate = null!;
    private Label _dndWindowTotal = null!;
    private VBoxContainer _dndAppBox = null!;
    private Control _dndAppTemplate = null!;
    private Label _dndAppTotal = null!;
    private Label _dndForeground = null!;

    /// 当前前台应用那一发实测结果（<c>App</c> 那一侧问，钩子递进来）。**这一扇窗不自己问系统**：
    /// 它此刻就是前台，自己问出来的永远是 Keeper 自己 —— 那一条实测结果要的是「用户刚切过去的那个
    /// 程序是哪个、装在哪」，所以走 <c>Detection</c> 那一族 0.2 秒一发的同一个出处
    /// （ADR-0006「前台取数搭现有轮询底盘」那一节）。
    public Func<Foreground.Measurement> ForegroundApp = () => default;

    // 生活模式那一族（见 BuildLifeModeMenu）：窗头那颗按钮、它展开的菜单，以及当下磁盘上那份名单。
    // **别和上面那几行混**：上面是配置里那张 flows 表（一条休息流程 = 一串步），
    // 这一族是「一整套配置的命名快照」，两者在词汇表里是两组词
    // （GLOSSARY.md「休息流程」的禁用词就是「生活模式」）。
    private MenuButton _modeButton = null!;
    private LifeModeIndex _modeIndex = new();

    /// 程序往控件里灌值的那一小段。控件的 TextChanged / ItemSelected 不分「人打的」和
    /// 「我灌的」，不立这一面旗，把当前名字放回格子里就会被读成一次改名。
    private bool _filling;

    /// 一行：声明、控件、以及那两行文字。控件的类型由场景决定，由 SelfTest 对着声明核。
    private sealed class Row
    {
        public required SettingDecl Decl { get; init; }
        public required Control Value { get; init; }
        public required Label Name { get; init; }
        public Label? Hint { get; init; }
        public Label? Unit { get; init; }
        public ColorRect? Swatch { get; init; }
        public Button? Browse { get; init; }
        public string Key => Decl.Key;
    }

    public override void _Ready()
    {
        // 取证那一发（他真鼠标点出来的闪屏）：闪的前提是「算位置之前这扇窗就已经露面了」——
        // 那样它先按引擎给的位置（这台机器上是 (0,0)）露一脸，等 _Ready 把接声明表的那些行 + 清单 +
        // 休息流程编辑器全搭完才算得出正中间那一个点。所以问两头：算位置之前露没露面、
        // 这一趟走了多少毫秒（那一段毫秒就是那一闪有多长）。句柄印 -1 是 Godot 的
        // 「还没有 OS 窗」，不是 0 —— 第一版拿 0 当哨兵，把「没建」读成了「已建」。
        var t0 = Time.GetTicksMsec();
        var seenBefore = IsVisible();
        var bornId = GetWindowId();
        _copy = Live.Clone();
        ApplyUiTheme();   // 排在 Bind 之前：第一帧排版就要按这一版配色来
        Bind();
        Texts();
        BuildChecklist();
        BuildFlowEditor();
        BuildBlockEditor();
        BuildDndEditor();
        BuildNav();
        BuildLifeModeMenu();
        WireHistoryCard();
        WireFooter();
        RefreshPreview();
        ShowDocError();   // 读不通的那一句要一开窗就看得见，不是等按下保存那一刻
        CloseRequested += Close;
        // 摆在主屏正当中：引擎给的位置是 (0,0)，那在这台机器上是把标题栏推到屏幕外面去。
        var work = DisplayServer.ScreenGetUsableRect(0);
        Position = new Vector2I(work.Position.X + (work.Size.X - Size.X) / 2,
            work.Position.Y + (work.Size.Y - Size.Y) / 2);
        Show();   // 场景里 visible=false：OS 窗到这一行才建，建出来就在正中间那一个点上
        GrabFocus();
        var bornText = seenBefore ? $"露过面（句柄 {bornId}）← 左上角那一闪就是它"
                                  : $"没露面（句柄 {bornId}，-1 即「还没有 OS 窗」）← 那一闪没有落脚的地方";
        Log("options",
            $"开窗：可见={IsVisible()} 位置={Position} 大小={Size} 标题='{Title}' 焦点={HasFocus()}，" +
            $"算位置之前 {bornText}，露面时句柄={GetWindowId()}，" +
            $"这一趟 _Ready 走了 {Time.GetTicksMsec() - t0} 毫秒，" +
            $"接上 {_rows.Count} 行 + flows 那张表（{_copy.Flows.Count} 条）—— " +
            "范围/默认值/可选值都来自声明表，磁盘要等「保存」那一下");
    }

    private void Close()
    {
        Log("options", "窗口关了，没保存的改动跟着副本一起没了");
        QueueFree();
    }

    /// Esc 关这个窗口。App 那条 Esc 是退程序，焦点在设置窗里时不该顺手把程序带走。
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }

    // ---- 配色：uiTheme 那一个键 -------------------------------------------------

    /// <summary>把主题挂上、角色刷一遍。<b>挂在 <see cref="UiTheme"/> 那一族</b>：哪两张主题文件、
    /// 一个角色怎么变成节点上的样子，两扇窗（设置窗、画廊）读同一份（理由在那一类的类型注释）。
    ///
    /// 认不出的取值退回亮色、读不出来要说，两条都在 <see cref="UiTheme"/> 那一侧实现了，
    /// 这里只把「读不出来」那一档说给人和日志听。</summary>
    private void ApplyUiTheme()
    {
        var want = (string)_copy.Map.Get(UiTheme.Key);
        var path = UiTheme.Table.FirstOrDefault(t => t.Value == want).Path;
        if (path == null) path = UiTheme.Table[0].Path;
        if (!UiTheme.Apply(this, want))
        {
            Say($"{Tr("设置窗自己的配色读不出来")}：{path}", true);
            Log("options", $"配色：{path} 加载不出来，这一版退回引擎默认那套");
            return;
        }
        _previewBox = null;      // 预览那一圈是复制主题样式得来的，换主题就得重新复制一份
        PaintRoles();
        var page = GetNodeOrNull<Panel>("Page");
        var hint = GetNodeOrNull<Label>($"{FormPath}/Time/Body/autoFade/Text/Hint");
        var save = GetNodeOrNull<Button>("Margin/Stack/Footer/Save");
        var loaded = Theme!;
        Log("options", $"配色：uiTheme={want} → {path}（{loaded.ResourceName}，{UiTheme.Roles.Length} 个角色）");
        Log("options", $"配色落到节点上：页面底色={page?.GetThemeStylebox("panel")?.Get("bg_color")}" +
                       $"（该 {loaded.GetStylebox("panel", "Page")?.Get("bg_color")}） " +
                       $"提示字色={hint?.GetThemeColor("font_color")}" +
                       $"（该 {loaded.GetColor("font_color", "HintLabel")}） " +
                       $"保存钮底色={save?.GetThemeStylebox("normal")?.Get("bg_color")}" +
                       $"（该 {loaded.GetStylebox("normal", "PrimaryButton")?.Get("bg_color")}）");
    }

    /// <summary>这一扇窗那些<b>没挂 <c>metadata/role</c></b> 的节点：窗头三行、每行的三个格子、
    /// 窗底五颗。它们的角色是「结构」决定的，写在代码里比在场景里逐颗挂 metadata 更短也更好读。
    ///
    /// 场景里挂了的那一遍走 <see cref="UiTheme.Paint"/>（两扇窗共用），这里只补这一扇窗的
    /// 名字。<b>换配色和换语言之后都要再跑一遍</b>：刷的是覆写，不是主题的查找链。
    private void PaintRoles()
    {
        UiTheme.Paint(this);
        // 窗头那三行
        UiTheme.Text(Theme, GetNodeOrNull<Label>("Margin/Stack/Header/Title"), "WindowTitle");
        UiTheme.Text(Theme, GetNodeOrNull<Label>("Margin/Stack/Header/Status"), "StatusLabel");
        UiTheme.Text(Theme, GetNodeOrNull<Label>("Margin/Stack/Header/Error"), "ErrorLabel");
        // 每行的三个格子
        foreach (var row in _rows)
        {
            UiTheme.Text(Theme, row.Name, "RowName");
            UiTheme.Text(Theme, row.Hint, "HintLabel");
            UiTheme.Text(Theme, row.Unit, "UnitLabel");
        }
        // 窗底那五颗：主 / 次
        var foot = GetNodeOrNull<HBoxContainer>("Margin/Stack/Footer");
        if (foot != null)
        {
            UiTheme.Box(Theme, foot.GetNodeOrNull<Button>("Save"), "PrimaryButton");
            foreach (var ghost in new[] { "Preview", "Export", "Import", "Defaults" })
                UiTheme.Box(Theme, foot.GetNodeOrNull<Button>(ghost), "GhostButton");
        }
    }

    // ---- 遮罩实时预览 ----------------------------------------------------------

    /// 预览块那圈底色。**只刷颜色，形状归主题**：从主题那份 <c>Preview</c> 样式复制一份
    /// 下来改 <c>BgColor</c>，描边/圆角/内边距都还在主题里那一份上。
    private StyleBoxFlat? _previewBox;

    /// 预览块多高。<b>也是字号那一档的输入</b> —— 遮罩上的钟按屏高算，设置窗里这一小块
    /// 按它自己这一截高算（<see cref="Fader.Measure"/> 是同一个函数，比例那份不重抄）。
    private const int PreviewHeight = 132;

    /// <summary>把遮罩按当前设置画在 Fader 那张卡片顶上：底色、不透明度、字色、字号，
    /// 有背景图就连图一起摆。
    ///
    /// 为什么要有这一块：整个程序的存在理由是「一块屏幕压进一种颜色」，而调颜色那一格
    /// 原来只有一个写着 <c>#008040</c> 的文本框，按窗底那颗「预览遮罩」才看得见效果 ——
    /// 而那一按遮罩真盖满整块屏，窗就没了。这一块是<b>就地</b>的那一版：拖颜色、拖不透明
    /// 度、拖字号，它跟着变，不挡任何东西。
    ///
    /// 摆的是**副本**（<c>_copy</c>）不是磁盘上那份：和窗里所有行一样，改了不按保存就
    /// 不算数，所以预览也跟着「没保存」。</summary>
    private void RefreshPreview()
    {
        if (GetNodeOrNull<PanelContainer>(PreviewPath) is not { } frame) return;
        var alpha = (int)_copy.Map.Get("maxOpacityPercent") / 100f;
        var back = Fader.Rgb((string)_copy.Map.Get("backgroundColor"));
        var ink = Fader.Rgb((string)_copy.Map.Get("foregroundColor"));

        _previewBox ??= (frame.GetThemeStylebox("panel") as StyleBoxFlat ?? new StyleBoxFlat()).Duplicate() as StyleBoxFlat
                        ?? new StyleBoxFlat();
        frame.AddThemeStyleboxOverride("panel", _previewBox);
        _previewBox.BgColor = new Color(back.R, back.G, back.B, back.A * alpha);

        var sample = GetNodeOrNull<Label>(PreviewPath + "/Sample");
        if (sample != null)
        {
            sample.AddThemeColorOverride("font_color", new Color(ink.R, ink.G, ink.B, ink.A * alpha));
            sample.AddThemeFontSizeOverride("font_size",
                Fader.Measure(PreviewHeight, (int)_copy.Map.Get("textScalePercent"), 1f).ClockFont);
        }
        var paper = GetNodeOrNull<TextureRect>(PreviewPath + "/Backdrop");
        if (paper == null) return;
        // 键名是**声明表里那一份**，不是场景里那颗节点的名字：节点叫 image_file，
        // 键叫 image.file。抄了节点名就是「Map.Get 抛 Sequence contains no matching
        // element」，而那一抛发生在 _Ready 里 Show() 之前 —— 窗从头到尾没露过面，
        // 于是导航那一组 19 条判据跟着全红，一条真正的病因都看不出来。
        var on = (bool)_copy.Map.Get("image.enabled");
        var file = (string)_copy.Map.Get("image.file");
        // 图读不出来就退回纯色底 —— 和真遮罩同一处置（<see cref="FaderPicture"/>），
        // 不在这里编一句「预览失败」，那一格自己会显示读不进来的原因。
        var loaded = on && file.Length > 0 && Image.LoadFromFile(file) is { } img
                     && img.GetWidth() > 0 && img.GetHeight() > 0
                     ? ImageTexture.CreateFromImage(img) : null;
        paper.Texture = loaded;
        paper.Visible = loaded != null;
    }

    // ---- 绑定：每行按键名接到声明上 ---------------------------------------------

    /// <summary>审计读出来的两样东西：问题清单，和场景里那些 <c>metadata/setting</c> 的键。
    /// 键单还回来是因为自检那一侧还要拿它去跟语言表对账（第 11 条），而那份对账不是场景形状，
    /// 运行期不该管 —— 让审计把它也做了，就等于把「界面缺东西」和「翻译缺一条」混成一档。</summary>
    private sealed record SceneShape(List<string> Problems, List<string> Keys);

    /// <summary><b>一份场景审计，判据一处声明。</b>运行期 <see cref="Bind"/> 印它，自检里
    /// <see cref="SelfTest"/> 收它 —— 同一把尺子量两头。
    ///
    /// 2026-09-29 之前这两遍是各写各的，于是<b>两份各自漏，而且互相不包含</b>：自检查
    /// <c>Text</c> 那一竖排在不在，不查竖排里那颗 <c>Name</c>；而 Bind 那边是硬
    /// <c>GetNode&lt;Label&gt;("Name")</c>（<see cref="Row.Name"/> 是 <c>required</c>），
    /// 少了它自检全绿、开窗那一刻抛。反过来，Bind 读 <c>Hint</c>/<c>Unit</c>/<c>Swatch</c> 全走
    /// <c>GetNodeOrNull</c>，缺了一行实测结果都没有 —— 色块刷不上色那一条<b>永远</b>不会有实测结果。
    ///
    /// 判据不是这里发明的，每一条都指回一处已有的依赖：第 6 条指回 <c>Row.Name</c> 的
    /// <c>required</c>，第 8 条指回 <c>IoSelfTest</c> 里那两个 <c>Browse!</c>
    /// （<c>image.file</c> / <c>sound.file</c>），第 12 条指回根 <c>AGENTS.md</c>
    /// 「控件上只写 setting 的键名，min/max/default 从声明里取 —— 场景里不会出现第二个数字」。
    /// <c>Hint</c> 和 <c>Unit</c> 是<b>真的可选</b>（<see cref="Row.Hint"/> / <see cref="Row.Unit"/>
    /// 都可空，缺了就是不显示），所以这里不给它们立规矩 —— 给可选节点立必到的规矩是发明需求。
    /// 单位那条文字在不在语言表里是第 11 条的事，在自检那一侧。</summary>
    private static SceneShape Audit(Node root)
    {
        var problems = new List<string>();
        var keys = new List<string>();
        foreach (var node in Walk(root))
        {
            // 12. 场景里不许出现第二个数字。原来这一条只管 flows 编辑器 step 模板那一颗 SpinBox，
            //     而场景里有 21 颗数值控件 —— 那条原则是对全部数值控件说的，不是对一颗说的。
            //     **2026-10-06：那 11 颗滑块也换成 SpinBox 了**（所有者定夺「把进度条移除」），
            //     所以这里只剩一种数值控件；HSlider 那一支也一起删了 ——
            //     没有控件的那一支留着，就是一份没人走的分支。
            if (node is SpinBox)
                problems.AddRange(RangeIsNotHardcoded((Control)node, root.GetPathTo(node).ToString()));

            if (!node.HasMeta("setting")) continue;
            var key = node.GetMeta("setting").AsString();
            keys.Add(key);

            // 1. setting 得是一条声明里的键。
            if (!Settings.Declared(key))
            {
                problems.Add($"{node.Name}：setting=\"{key}\" 不是一条声明里的键");
                continue;
            }
            var decl = Settings.Decl(key);

            // 2. 带 setting 的那个节点得是容器（一行的形状是 容器 > Text + Value）。
            if (node is not Container row)
            {
                problems.Add($"{key}：带 setting 的节点要是一个容器");
                continue;
            }

            // 3. 得有一个叫 Value 的控件。
            var value = row.GetNodeOrNull<Control>("Value");
            if (value == null)
            {
                problems.Add($"{key}：这一行里没有叫 Value 的控件");
                continue;
            }

            // 7. 颜色那一格是一颗 ColorPickerButton。2026-09-30 之前它是「一个色块 + 一行
            //    #008040 的文本框」：最核心的那一格长得最像调试项，而色块是套两层的 Panel
            //    （白底上纯白的色块没边就看不见）。换成引擎自带的真控件之后这一层形状
            //    没有了，所以判据跟着换：声明是颜色，场景里就得是一颗取色器按钮。
            if (decl.Kind == SettingKind.HexColor && value is not ColorPickerButton)
                problems.Add($"{key}：声明是颜色，场景里那个控件却是 {value.GetType().Name}" +
                             "（2026-09-30 起颜色格用 ColorPickerButton，不是色块 + 文本框）");

            // 4. 控件种类得配得上声明那个种类。
            if (!KindFits(decl, value))
                problems.Add($"{key}：声明是 {decl.Kind}，场景里放的却是 {value.GetType().Name}");

            // 5. Text 那一竖排得在（标签和提示都在里面）。
            var text = row.GetNodeOrNull<VBoxContainer>("Text");
            if (text == null)
            {
                problems.Add($"{key}：这一行里没有 Text 那一竖排（标签和提示都在里面）");
            }
            // 6. 竖排里那颗 Name 也得在 —— 自检原来不查这一条，而 Bind 硬要它。
            else if (text.GetNodeOrNull<Label>("Name") == null)
                problems.Add($"{key}：Text 里没有 Name 那颗 Label（Row.Name 是 required，少了它开窗就抛）");

            // 8. Browse 只对那两颗是必到的：导出导入那一组按的是它自己（win._byKey["image.file"].Browse!）。
            if ((key == "image.file" || key == "sound.file") && row.GetNodeOrNull<Button>("Browse") == null)
                problems.Add($"{key}：这一行要有 Browse 那颗钮，导出导入那一组按的就是它");
        }

        // 9. 声明里有、场景里没这一行 —— 那是一个改不到的设置，比界面难看严重。
        //    这一族在工单 01 先声明进文档、工单 02 把「作息」那一格和时间块编辑器搭起来之前
        //    曾经开过一个例外（「那一格还没搭」），那是工单 01 留下的临时让步：工单 02 落地
        //    之后例外连同 <c>WaitingForTab()</c> 一起删掉，这一族重新回到「每一行都得在」
        //    这条底下。现在这一条里不该再有任何一个键名。
        foreach (var key in new Settings().Map.Keys.Where(key => !keys.Contains(key)))
            problems.Add($"{key}：声明里有这个键，设置窗里没有它的行");

        // 10. 一行不重复。原来 Bind 那一侧是后者盖前者（一行实测结果都没有），自检那一侧才查。
        foreach (var dup in keys.GroupBy(k => k).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key}：设置窗里有 {dup.Count()} 行，一个键只能有一行");

        // 13. 分区必须归某个域，两头都要真的存在。
        //     这一条的因：域是 Domains 那张表说的，分区是场景里那一张张卡片。表里写错一个节点名
        //     （打错字、或者新加的分区忘了归域），ApplyVisibility 就会把一个不存在的节点静默
        //     跳过（GetNodeOrNull）—— 分区从此在任何一格里都点不到，而自检还是绿的。
        var form = root.GetNodeOrNull<VBoxContainer>(FormPath);
        var declared = Domains.SelectMany(d => d.Sections).ToList();
        foreach (var (node, _) in SectionTitles)
        {
            if (Card(root, node) == null)
            {
                problems.Add($"{node}：SectionTitles 里有这个分区，场景里没有它");
                continue;
            }
            if (DomainOf(node) < 0)
                problems.Add($"{node}：这个分区不在 Domains 任何一格里，它在任何一格都点不到");
        }
        foreach (var section in declared.Distinct().Where(s => !SectionTitles.Any(t => t.Node == s)))
            problems.Add($"{section}：Domains 说它是一个分区，但它不在 SectionTitles 里");
        foreach (var d in declared.GroupBy(s => s).Where(g => g.Count() > 1))
            problems.Add($"{d.Key}：Domains 里它出现了 {d.Count()} 次");

        // 14. 每一条键的<b>声明域</b>（<c>SettingDecl.Domain</c>）要和它所在分区在
        //     <see cref="Domains"/> 里被归的那一格一致。两头各有一份，这一对不上就是
        //     「声明说这条在日历那格、界面却把它摆在休息那格」—— 人点得到，却不跟着声明走。
        //     这一条是分组从界面提到声明表之后才有的：上一版分区归域只存在 Domains 里，
        //     声明表压根不知道域这回事，于是「这条属于哪一格」有两份真相。
        foreach (var (key, decl, node, _) in SectionedRows(root))
        {
            var want = DomainId(decl.Domain);
            if (want < 0)
            {
                problems.Add($"{key}：声明里的域 '{decl.Domain}' 不认识（只有 " +
                             string.Join("、", Domains.Select(d => d.Id)) + "）");
                continue;
            }
            var got = DomainOf(node);
            if (got != want)
                problems.Add($"{key}：声明说它在「{decl.Domain}」那格，它却摆在 {node} 分区里" +
                             $"，而那个分区归在第 {got + 1} 格");
        }

        // 15. 档位得是认得的取值。档位只有 common/advanced 两种，拼错的（common、Common、
        //     advancde）在**文档里**就是归不到任何一档，而文档是要跟着走很久的。
        //
        //     **这一条 2026-10-06 换掉了另一半**：它原来还要求窗头有那颗「显示高级设置」开关，
        //     因为那时高级那 28 条默认收着，开关是它们唯一的入口。现在开关和域切换一起退休
        //     了（<see cref="BuildNav"/>），全部键默认在场 —— 所以开关**不许**在，
        //     它按下去就是在藏东西。
        var rowsBySection = SectionedRows(root).ToList();
        foreach (var (key, decl, _, _) in rowsBySection.Where(r => !TierKnown(r.Decl)))
            problems.Add($"{key}：声明里的档位 '{decl.Tier}' 不认识（只有 common / advanced）");
        if (root?.GetNodeOrNull("Margin/Stack/Nav/Advanced") != null)
            problems.Add("窗头还有一颗「显示高级设置」开关 —— 它按下去就是在藏行，"
                         + "而所有者 2026-10-07 确认它不回来（域标签回来是另一件事）");
        // 域标签那一格 2026-10-07 接回来了，**判据跟着从「不许在」翻成「必须在」**。
        // 原来的判据问的是「窗头还有域切换吗」，问的是「切换在不在」；所有者要的是
        // 「切换不许被藏」—— 两者不是同一个问题，翻转之后这一条才问得住真正的那件事。
        var bar = root?.GetNodeOrNull<TabBar>("Margin/Stack/Nav/Domains");
        if (bar == null) problems.Add("窗头没有域标签那一格 ——「域标签 不许被藏」，2026-10-06 那一发整格删掉就是它");
        else if (bar.Visible == false)
            problems.Add("域标签那一格 visible = false —— 它在树上但不在屏幕上，那就是「被藏」。"
                         + "「域标签 不许被藏」，2026-10-07");
        else if (!bar.SizeFlagsHorizontal.HasFlag(Control.SizeFlags.Expand))
            // 不撑开的话后面几颗离右缘一大段空白，搜索框就不在右上角。
            problems.Add("域标签那一格没有撑开（size_flags_horizontal 要带 EXPAND）—— "
                         + "不撑开的话后面几颗离右缘一大段空白，搜索框就不在右上角");
        // 标签**有几格**这里问不到：TabBar 里的 tab 是 <c>BuildNav</c> 运行时 AddTab 填的，
        // Audit 手里只有一棵没跑起来的场景树，数出来永远是 0 —— 那是一条会当次误报的判据。
        // 数格数的那一条在 <see cref="NavSelfTest"/>，它开的是真窗。

        // 12. 每一区都得是一张卡片，卡片里得有标题。原来这一区量的是「Rule → Title →
        //     各行」那个次序，判据是「少一个分割线就是分区又变回一个和设置行一样大的字」。
        //     2026-09-30 分区改成卡片之后那一条整个退休：分割线是卡片的第三种分法，
        //     而「这一片是另一个区」现在由卡片自己的边框和底色说。留下来的判据是形状：
        //     得是一张 PanelContainer + Card 变体，里面得有一个 Body 竖排和一颗标题。
        foreach (var (node, _) in SectionTitles)
        {
            var card = Card(root, node);          // 第 13 条问过它存不存在
            if (card == null) continue;
            if (card is not PanelContainer)
                problems.Add($"{node}：分区要是一张卡片（PanelContainer），现在是 {card.GetType().Name}");
            if ((card.GetMeta("role", "").AsString()) != "Card")
                problems.Add($"{node}：卡片没有 metadata/role = \"Card\" —— 它的底色和描边就没人刷了");
            var body = Body(root, node);
            if (body == null) problems.Add($"{node}：卡片里没有 Body 那一竖排（标题和那些行都在里面）");
            else if (body.GetNodeOrNull<Label>("Title") == null)
                problems.Add($"{node}：卡片里没有标题（Body/Title 那个 Label）");
        }

        // 19. 场景里挂的每一个角色名，两张主题都得真的有那个角色。
        //     角色名是「节点 → 主题里那一支」的桥（<see cref="PaintRoles"/> 按它刷覆写），
        //     而它拼错了一个字不会报任何错 —— 那一支刷不上，节点退回基础类型那一档，
        //     抓帧里看得出「不是我要的」看不出「名字对不上」。这一条就是那件事的判据。
        //     **两扇窗都问**：画廊（<see cref="Presets"/>）是同一族的一扇窗，角色是同一份表
        //     （<see cref="UiTheme.Paint"/>），所以一个角色名在那边拼错了也该在这里红 ——
        //     开着的是另一扇窗，而「那一扇的底色退回引擎默认」在设置窗的抓帧里根本看不见。
        var roleHolders = new List<Node> { root! };
        if (GD.Load<PackedScene>(Presets.ScenePath)?.Instantiate() is { } galleryRoot) roleHolders.Add(galleryRoot);
        foreach (var themePath in UiTheme.Table.Select(t => t.Path))
        {
            if (GD.Load<Theme>(themePath) is not { } theme)
            {
                problems.Add($"{themePath}：主题加载不出来");
                continue;
            }
            foreach (var role in roleHolders
                         .SelectMany(holder => Walk(holder))
                         .Where(n => n.HasMeta("role"))
                         .Select(n => n.GetMeta("role").AsString())
                         .Distinct())
            {
                if (!theme.HasColor("font_color", role) && !theme.HasStylebox("panel", role)
                    && !theme.HasStylebox("normal", role))
                    problems.Add($"{System.IO.Path.GetFileName(themePath)}：主题里没有角色「{role}」");
            }
        }
        (roleHolders.Count > 1 ? roleHolders[1] : null)?.Free();

        // 20. 每一行<b>前三格是 Text / Value / Unit，次序就是这个次序</b>。
        //     GridContainer 是**按子节点在场景里的次序**排的，不是按名字 —— 所以少一格
        //     或者次序错了不会报任何错，只会安静地歪：抓帧量到的是「带单位词的那几行
        //     标签从 34 起、没有单位的那几行从 84 起」，控件列的右缘一个 706 一个 756。
        //     两次都栽在同一个地方：2026-09-30 第一次是那些行压根没有 Unit（第三列宽 0），
        //     补空格时补在了行自己的节点头上，于是那颗空格子顶到了第 0 列。
        //     判据问的是「三格都在、次序对」，那两条都在的场景形状里量得到，不用开窗。
        //
        //     第四格往后不管：挑文件那两行（<c>image.file</c> / <c>sound.file</c>）本来就
        //     多一颗「浏览…」钮，而列宽是**每一行自己**取的（每行是一个独立的
        //     GridContainer），多一格不影响那三列。
        foreach (var row in Walk(root!).OfType<GridContainer>())
        {
            if (!row.HasMeta("setting")) continue;
            var cells = row.GetChildren().Select(c => c.Name.ToString()).ToList();
            if (cells.Count < 3 || !cells.Take(3).SequenceEqual(["Text", "Value", "Unit"]))
                problems.Add($"{row.GetMeta("setting").AsString()}：这一行的格子是 [{string.Join(" / ", cells)}]，" +
                             "头三格要的是 [Text / Value / Unit] —— 少一格，或者次序不是这个（GridContainer 按次序排，不按名字）");
        }

        // 17. 一个分区归了域，但它在界面上<b>一行都没有</b>，就成了一张空卡片。
        //     2026-10-07 换掉了判据要问的那件事：这一条原来问「整张全高级的区有没有被收掉」，
        //     前提是「显示高级设置」那颗开关还在、默认收着 28 条。那颗开关 2026-10-06 退休、
        //     2026-10-07 确认不回来，前提没了 —— 于是「整张全高级」不再是任何可见性的原因：
        //     遮罩那一卡全部声明行都是 <c>advanced</c>，现在照样整张摆着。
        //
        //     域切换接回来以后，这一格仍然是**可见性**的两支之一（搜索那一支除外）：
        //     它整张收的是「不在这一格里的分区」，收的是卡片不是行。所以真正的空卡片只剩一种
        //     形状 —— 分区归了域，场景里也有一行，可那一行是别的分区搬走后剩的空壳。
        //     静态这一层量得出来：分区名下<b>一条带 <c>setting</c> 的行都没有</b>、又不在
        //     KeepsOwnContent 那几区里，就是一张点不动的空壳。运行期那一半（搜空之后
        //     连卡片一起收）交给 <see cref="NavSelfTest"/>，它量的是真窗。
        var bySection = rowsBySection.GroupBy(r => r.Section).ToList();
        foreach (var (node, _) in SectionTitles)
        {
            if (KeepsOwnContent.Contains(node)) continue;
            var rows = bySection.FirstOrDefault(g => g.Key == node);
            if (rows != null && rows.Any()) continue;
            var home = Math.Max(DomainOf(node), 0);
            problems.Add($"{node}：它归在「{Domains[home].Title}」那一格，"
                         + "但一棵设置行都没有 —— 摆出来是一张点不动的空卡片");
        }

        // 18. 横向滚轮要的是关掉，不是「一直显示」。<c>horizontal_scroll_mode</c> 里
        //     **0 是 DISABLED、2 是 SHOW_ALWAYS**（引擎文档 ScrollContainer.ScrollMode）——
        //     原来那份场景写的是 2，窗底那根一直挂着的横条就是它，而它长得像一条进度条。
        //     内容宽度跟着窗宽走，横向本来就没有可滚的余地。
        var scroll = root?.GetNodeOrNull<ScrollContainer>("Margin/Stack/Scroll");
        if (scroll == null) problems.Add("窗里没有 Margin/Stack/Scroll 那个 ScrollContainer");
        else if (scroll.HorizontalScrollMode == ScrollContainer.ScrollMode.ShowAlways)
            problems.Add("横向滚轮是 SHOW_ALWAYS，窗底会一直挂着一根横条 —— 要的是 0（DISABLED）");

        // 19. 场景里不许有滑块。2026-10-06 所有者定夺「把进度条移除」，
        //     那 11 行从 HSlider 换成了 SpinBox（值域照旧从声明表去，见 Wire 那一段）。
        //     这一条查的是**整棵树**，不只那几个已知位置 —— 新加一行、随手挑了滑块，
        //     这一条当场报出来，而不是等一个人去说「我又看见一根进度条」。
        foreach (var node in Walk(root!))
            if (node is HSlider)
                problems.Add($"{root!.GetPathTo(node)}：场景里还有一根滑块（HSlider）—— " +
                             "那一族 2026-10-06 换成数字格了");

        return new SceneShape(problems, keys);
    }

    private void Bind()
    {
        // 一份审计，两个读者：这里印一份，自检里 SelfTest 收同一份。原来这两处各写各的判据，
        // 于是「自检全绿」和「开窗不炸」是两件事 —— Text 里那颗 Name 自检根本不查，而下面是硬
        // GetNode<Label>("Name")，少了它自检全绿、开窗那一刻抛。
        //
        // 审计印完之后下面一律走 GetNodeOrNull：缺东西不再分「抛」和「静默」两档，
        // 它是一行实测结果，然后这一行不接。
        foreach (var problem in Audit(this).Problems) Log("options", $"场景审计：{problem}");

        foreach (var node in Walk(this))
        {
            if (!node.HasMeta("setting")) continue;
            var key = node.GetMeta("setting").AsString();
            // 审计已经把这三条各印过一行了，这里只是不接这一行。
            if (!Settings.Declared(key)) continue;
            if (node is not Container row) continue;
            var box = row.GetNodeOrNull<VBoxContainer>("Text");
            var value = row.GetNodeOrNull<Control>("Value");
            var name = box?.GetNodeOrNull<Label>("Name");
            if (box == null || value == null || name == null) continue;
            var entry = new Row
            {
                Decl = Settings.Decl(key),
                Value = value,
                Name = name,
                Hint = box.GetNodeOrNull<Label>("Hint"),
                Unit = row.GetNodeOrNull<Label>("Unit"),
                Swatch = row.GetNodeOrNull<ColorRect>("Swatch/Fill"),   // 2026-09-30 起颜色格改用 ColorPickerButton，这一格恒为 null
                Browse = row.GetNodeOrNull<Button>("Browse"),
            };
            _rows.Add(entry);
            _byKey[key] = entry;
            Wire(entry);
        }
        // 「声明里有、场景里没这一行」是审计的第 9 条，它已经印过了 ——
        // 原来这里再印一遍，同一件事两处各说一次。
    }

    private void Wire(Row row)
    {
        var decl = row.Decl;
        switch (row.Value)
        {
            case SpinBox box:
                // 输入框不带上限：控件这一头只给下限，上限只在写回那一步管
                // （设计文档 `docs/design/config.md`「每个设置只声明一次」那条，；这个键声明的就是「from 1, no ceiling」）。
                // 把上限抄到控件自己身上，代价就是「打 151，屏幕上先变成一个你没写过的数」
                // 那一帧 —— 他真鼠标点出来的那一条。
                box.MaxValue = int.MaxValue;
                // 时间格（键名 *Ms / 休息流程那个 ms）上的数是**换算过的数**，所以：
                //   · 步长 1 —— 引擎按步长决定把那个数写成什么样（实测 <c>Step = 0.01</c>
                //     时屏幕上出现「3.0」，一个三秒写成「3.0 秒」）；打「3」是 3 秒还是
                //     3 分钟由右边那个单位词说了算，不必靠步长暗示；
                //   · 地板由 <see cref="ShowTime"/> 按**此刻那一档**换算（同一个键的
                //     「秒」档和「分钟」档地板不一样），声明给的下限一个字没改。
                if (decl.IsTime) box.Step = 1;
                // **非时间的整数格**（百分比、音量那一族）：框里那个数**就是存着的那个数** ——
                // 时间的格显示的是换算过的秒/分，那是另一回事（上面那条）。所以值域直接取
                // 声明表那一份。2026-10-06 这几行从滑块换成数字格之前，值域是滑块那一支
                // （`Load` 里的 `case HSlider`）从声明表去的；那一支跟着滑块一起删了之后，
                // 这一支要补上 —— 不然「打 500」会先写进副本、再被声明表夹回来，
                // 而框里还留着那个 500：存着的数是 100，屏幕上写着 500。
                else if (decl.Kind == SettingKind.Int)
                {
                    box.MinValue = decl.Min;
                    box.MaxValue = decl.Max;
                }
                break;

            case CheckBox or CheckButton or OptionButton:
                break;

            case LineEdit edit:
                edit.TextChanged += text => Commit(row, text);
                break;

            case ColorPickerButton pick:
                // 颜色这一格 2026-09-30 从「色块 + 一行 #008040 的 LineEdit」换成引擎
                // 自带的 ColorPickerButton：最核心的那一格原先长得最像调试项。
                // 仍然存 #RRGGBB（契约没动），所以设计文档 `docs/design/config.md`「导出与导入」那一节那一族不受影响。
                pick.ColorChanged += c => { if (!_filling) Put(row, Hex(c)); };
                break;

            default:
                Log("options", $"{decl.Key}：场景里那个控件是 {row.Value.GetType().Name}，接不上 {decl.Kind}");
                return;
        }
        if (row.Value is OptionButton option) Fill(option, row);
        Load(row);

        switch (row.Value)
        {
            case SpinBox box:
                box.ValueChanged += v => Put(row, row.Decl.IsTime
                    // 换回去要知道**此刻这一档**（右边那个单位词就是它），不能只看这个数：
                    // 同一个 3 既是 3 秒也是 3 分钟。档由上一次的显示定，写回之后立刻重定。
                    ? Settings.Unspelled((long)Math.Round(v), ScaleOf(row))
                    : (int)Math.Round(v));
                break;
            // 布尔那一格是 <c>CheckButton</c>（开关）而不是 <c>CheckBox</c>：CheckBox 的
            // 勾和空框是引擎自带的深色图标，主题改不了，暗色那张表里等于没画（实测结果在
            // <c>.scratch/tmp/relayout.py</c> 5a 那一步的注释里）。两者的 <c>Toggled</c> /
            // <c>SetPressedNoSignal</c> 都从 <c>BaseButton</c> 来，所以这里是同一个开关。
            case BaseButton toggle when toggle is CheckBox or CheckButton:
                toggle.Toggled += on => Put(row, on);
                break;
            case OptionButton opt:
                opt.ItemSelected += i => Choose(opt, row, i);
                break;
        }
        if (row.Browse != null)
        {
            var edit = (LineEdit)row.Value;
            row.Browse.Pressed += () => Pick(false, edit.Text, path =>
            {
                edit.Text = path;
                Put(row, path);
            }, FilterOf(row.Key));
        }
    }

    /// 把副本里的当前值灌进控件，只灌不回写：开窗那第一遍接的是初值（信号还没接上），
    /// 「恢复默认」和导入那两遍是接好之后再灌，所以这里一律走不发信号的那一头。
    /// 回写了几次由 <see cref="_writeBacks"/> 数着，自检里断言它是 0。
    private void Load(Row row)
    {
        var value = _copy.Map.Get(row.Key);
        switch (row.Value)
        {
            // 时间格不在这里灌 —— 它连着单位词和地板一起灌（<see cref="ShowTime"/>），
            // 拆成两处就有一处会忘了跟。
            case SpinBox box when !row.Decl.IsTime:
                var mins = (int)value;
                // 走不发信号那一条：写 box.Value = X 每变一个键就回写一次（实测 1 次 ——
                // 引擎只在值真的变了才发信号）。
                box.SetValueNoSignal(mins);
                // 引擎把里面那一行字留到晚一步才刷（实测：值都已经是 15 了，那一格还写着 '0'
                // —— 那个 0 是控件自己初始化的数）。屏幕上看到的是那一行字，所以同帧写掉。
                box.GetLineEdit().Text = mins.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case BaseButton toggle when toggle is CheckBox or CheckButton:
                toggle.SetPressedNoSignal((bool)value); break;
            case LineEdit edit when row.Decl.Kind == SettingKind.DelayList:
                // 一串分钟数：那一格是一个列表，不能当一个字符串灌（灌进去下一次夹取就炸）。
                // 那一行字由声明自己说（`SpellDelays`），界面这边不另摆一份写法。
                edit.Text = row.Decl.SpellDelays((List<int>)value); break;
            case LineEdit edit: edit.Text = (string)value; break;
            case ColorPickerButton pick: SetColorQuietly(pick, Fader.Rgb((string)value)); break;
        }
        if (row.Value is OptionButton option) SelectCurrent(option, row);
        ShowUnit(row);
    }

    /// 颜色格那颗控件要把 Godot 的 Color 写回 #RRGGBB。ToHtml(false) 已经是不带 # 的
    /// 大写六位，和声明表里那个默认写法一致，所以写回去的就是同一个形状。
    private static string Hex(Color c) => "#" + c.ToHtml(false).ToUpperInvariant();

    /// <summary>把颜色灌进取色器那颗按钮，<b>不回写副本</b>。
    ///
    /// 为什么不像 <c>SpinBox</c> 那样「设一个不发信号的孪生方法」：Godot 4 的
    /// <c>ColorPickerButton</c> <b>没有</b> <c>SetColorNoSignal</c>（<c>Range</c> /
    /// <c>BaseButton</c> 那几家有，它这一家没导出），C# 那一侧也没有
    /// <c>BlockSignals</c> 可用。所以走本文件自己的那面旗
    /// （<see cref="_filling"/>，接上信号之前灌初值用的就是它）：<c>ColorChanged</c> 那一路
    /// 看到旗立着就不回写。
    ///
    /// <b>为什么不能直接写 <c>pick.Color = …</c></b>：<see cref="Load"/> 那一条路上回写
    /// 必须是 0（自检里数 <c>_writeBacks</c>），而 <c>color_changed</c> 是不是会在程序赋值时
    /// 发出来，引擎文档没写死 —— 拿它当「不发」就是拿判据赌引擎的一个实现细节。</summary>
    private void SetColorQuietly(ColorPickerButton pick, Color color)
    {
        _filling = true;
        try
        {
            pick.Color = color;
        }
        finally
        {
            _filling = false;
        }
    }

    /// 这一格此刻是「秒」还是「分钟」。<b>只有时间格有这一问</b>，非时间格是 1（原值）。
    ///
    /// 它是<b>状态</b>而不是现算的：写回时那个数到底是 3 秒还是 3 分钟，只有上一趟显示
    /// 定下的那个档知道 —— 数字本身分不出来（两个 3 长得一样）。
    private int ScaleOf(Row row) =>
        _scale.TryGetValue(row, out var scale) ? scale : 1;

    /// 每一行上一次显示时定下的档。<see cref="Row"/> 是引用类型，当不了字典键的那一半，
    /// 所以这里按行的键名记 —— 一个键一行（审计第 10 条查过了）。
    private readonly Dictionary<Row, int> _scale = [];

    /// 时间格上摆的是人话单位的那一种，存的是毫秒 —— 设计文档 `docs/design/config.md`「界面上不出现毫秒」那条
    /// （<c>Settings.Spell</c> 是它唯一的一处口径）。**数、单位词、地板三样一起刷**：
    /// 跨过一分钟那一刻它们是一起变的，分开刷就会出现「写着分钟、上一格还剩 0.3」。
    ///
    /// 走不发信号那一头：这一步是「把值显示出来」，不是「人改了这个值」；
    /// 自检里数 <c>_writeBacks</c>，灌值那条路上它必须是 0。
    private void ShowTime(Row row, int ms)
    {
        if (row.Value is not SpinBox box) return;
        var (number, unitKey, scale) = Settings.Spell(ms);
        _scale[row] = scale;
        box.MinValue = row.Decl.ControlMin(scale);
        box.SetValueNoSignal(number);
        box.GetLineEdit().Text = Settings.SpellText(ms);
        if (row.Unit != null) row.Unit.Text = TrCounted(unitKey, number);
    }

    private void ShowUnit(Row row)
    {
        // 时间格：数、单位词、地板一起刷（跨过一分钟那一刻三样是一起变的）。
        if (row.Decl.IsTime && row.Value is SpinBox)
        {
            ShowTime(row, (int)_copy.Map.Get(row.Key));
            return;
        }
        if (row.Unit == null) return;
        // 数字框右边那个标签就是单位，数字自己在那格里（2026-10-06 之前滑块那一支是
        // 把数也塞进这一个标签：滑块只画轨道不画数，所以数得另写。滑块换成 SpinBox
        // 之后那一支走了 —— 数在框里，标签再写一遍就是同一个数印两遍）。
        row.Unit.Text = Unit(row, " ");
    }

    /// OptionButton 的可选值：声明里有可选值就用它。<c>flow</c> 那个键指的是 flows 里的名字，
    /// 所以列表来自那张表 —— 用户自己加的休息流程也要能在下拉里看见。
    private List<string> ChoicesOf(Row row) =>
        row.Decl.Choices.Length > 0 ? [.. row.Decl.Choices] : [.. _copy.Flows.Keys];

    private void Fill(OptionButton option, Row row)
    {
        option.Clear();
        var choices = ChoicesOf(row);
        var translatable = row.Decl.Choices.Length > 0;
        foreach (var choice in choices)
            option.AddItem(translatable ? Tr(choice) : choice);   // flow 的名字是用户的数据，不翻
    }

    private void SelectCurrent(OptionButton option, Row row)
    {
        var choices = ChoicesOf(row);
        var now = (string)_copy.Map.Get(row.Key);
        var at = choices.IndexOf(now);
        if (at < 0)
        {
            // 文档里写了一个可选值之外的东西（自己起的 flow 名）。列表里给它留一格，
            // 不然下拉显示的是第一条，而副本里存的是另一条 —— 那是个会骗人的控件。
            choices.Add(now);
            option.AddItem(now);
            at = choices.Count - 1;
        }
        option.Select(at);
    }

    private void Choose(OptionButton option, Row row, long index)
    {
        var choices = ChoicesOf(row);
        if ((int)index >= choices.Count) return;      // 那是给认不出的值留的额外一格
        Put(row, choices[(int)index]);
    }

    /// 控件写回副本。走 <see cref="SettingMap.Set"/>，所以过一遍声明的夹取和取值校验。
    private void Put(Row row, object value)
    {
        _writeBacks++;   // 自检里数这一发：「把副本灌进控件」那一条路上它必须是 0
        _copy.Map.Set(row.Key, value);
        if (row.Value is ColorPickerButton pick) pick.Color = Fader.Rgb((string)_copy.Map.Get(row.Key));
        ShowUnit(row);
        // 配色那个键是**即时**生效的（设计文档 `docs/design/config.md`「设置窗的配色是一个键」）：在窗里改一下就换，
        // 不必关窗重开，也不必先按保存 —— 换的是「这一版长什么样」，不是磁盘上那份文档。
        if (row.Key == UiTheme.Key) ApplyUiTheme();
        RefreshPreview();
        Status();
        // 窗里这一份变了，窗头那颗钮上「我在哪个生活模式」那一档就可能变了。判据在
        // LifeModeStore.StandingOf（拿这一份和每个生活模式那一份比一遍）—— 这里只管问。
        RefreshLifeModeWhere();
    }

    private void Commit(Row row, string text)
    {
        // 一串分钟数：那一格是一行逗号分隔的字，读成值走声明自己的读法（`ReadDelays`）——
        // 认不出的那一项丢掉、一项都认不出就回出厂那一串，所以界面上不可能写进一个
        // 文档里读不回来的值（`SettingMap.Set` 之后还会再过一遍夹取）。
        if (row.Decl.Kind == SettingKind.DelayList)
        {
            Put(row, row.Decl.ReadDelays(text));
            return;
        }
        if (row.Decl.Kind != SettingKind.HexColor)
        {
            Put(row, text);
            return;
        }
        var asWritten = text.Length == 6 ? "#" + text : text;
        if (!row.Decl.IsColor(asWritten))
        {
            Say($"{Tr("颜色要写成 #RRGGBB")}：{text}", true);
            return;
        }
        Put(row, asWritten.ToUpperInvariant());
    }

    // ---- 文字：每一条都出自那张表 -------------------------------------------------

    /// 换语言之后再来一遍就行：所有文字都在这一处写。
    private void Texts()
    {
        var zh = TranslationServer.GetLocale() != "en";
        Title = Tr("Keeper 设置");
        GetNode<Label>("Margin/Stack/Header/Title").Text = Tr("Keeper 设置");
        GetNode<Label>($"{ListPath}/Title").Text = Tr("休息清单");
        GetNode<Label>($"{ListPath}/Hint").Text = Tr("休息时显示在绿幕上，可以勾选");
        GetNode<LineEdit>($"{ListPath}/Add/Text").PlaceholderText = Tr("写点什么…");
        GetNode<Button>($"{ListPath}/Add/Btn").Text = Tr("添加一项");
        GetNode<Label>(FlowsPath + "/Hint").Text = Tr("flows.hint");
        GetNode<Button>(FlowsPath + "/Pick/NewBtn").Text = Tr("新增一条");
        GetNode<Button>(FlowsPath + "/Pick/DelBtn").Text = Tr("删掉这一条");
        GetNode<Button>(FlowsPath + "/AddStep/Btn").Text = Tr("加一步");
        // 作息表那一区：表头上那一句提示 + 底下那颗钮。字段那几行的标签在
        // BlockRow 里逐行灌（它们按声明表派生），所以整列重灌的那一句也排在这里。
        GetNode<Label>(RoutinePath + "/Hint").Text = Tr("routine.hint");
        GetNode<Button>(RoutinePath + "/AddBlock/Btn").Text = Tr("加一个时间块");
        // 勿扰那一格：两段说明、两个钮、字段标签和单位词（都在各自那一叠里逐行灌，
        // 同作息那一区），这里灌的是**不在任何一叠里**的那几处。
        GetNode<Label>(DndPath + "/WindowsHint").Text = Tr("dnd.hint.windows");
        GetNode<Label>(DndPath + "/AppsHint").Text = Tr("dnd.hint.apps");
        GetNode<Button>(DndPath + "/AddWindow/Btn").Text = Tr("加一个时段");
        GetNode<Button>(DndPath + "/AddApp/Btn").Text = Tr("加一个名字");
        var foot = GetNode<HBoxContainer>("Margin/Stack/Footer");
        foot.GetNode<Button>("Preview").Text = Tr("预览遮罩");
        foot.GetNode<Button>("Export").Text = Tr("导出…");
        foot.GetNode<Button>("Import").Text = Tr("导入…");
        foot.GetNode<Button>("Defaults").Text = Tr("恢复默认");
        foot.GetNode<Button>("Save").Text = Tr("保存");
        // 遮罩实时预览那一块里的样例字。预览的**形状**在主题里，颜色和字号按设置刷
        // （RefreshPreview），只有这句话是文字，所以在这里换语言。
        GetNode<Label>(PreviewPath + "/Sample").Text = Tr("遮罩预览");

        foreach (var (node, title) in SectionTitles)
            GetNode<Label>($"{FormPath}/{node}/Body/Title").Text = Tr(title);

        // 「历史」那一颗钮的字也在这一行上（窗是重建的，换语言时 Texts 再跑一遍）。
        // **它是唯一一颗不是设置行的钮**：别的按钮的字在各自那一段接线里设，
        // 这一颗归 <see cref="HistoryPath"/>，所以它的字也归这一段。
        GetNode<Button>(HistoryPath).Text = Tr("打开休息历史…");

        // 窗头那一行：换语言时搜索框的占位字要跟着换（窗是重建的，Texts 又跑一遍）。
        // 域标签那四颗标签的字也在这一行上，同样是文字，同样要跟着换 ——
        // 它们是 TabBar 里的 tab，换语言时逐个重新 SetTabTitle（不重建 TabBar：
        // 重建会把选中那一格冲回 0，而「我在哪个域」是界面状态，不该被换语言拨动）。
        var bar = GetNodeOrNull<TabBar>("Margin/Stack/Nav/Domains");
        if (bar != null)
            for (var i = 0; i < Domains.Length && i < bar.TabCount; i++)
                bar.SetTabTitle(i, Tr(Domains[i].Title));
        // 所以这里只剩搜索。
        var search = GetNodeOrNull<LineEdit>("Margin/Stack/Nav/Search");
        if (search != null) search.PlaceholderText = Tr("搜索设置");

        // 「预设生活模式…」在设置窗这一侧的入口**是生活模式菜单里的那一条**
        //（<see cref="RefreshLifeModeMenu"/> 的 `PresetsId`）——
        // 2026-10-08 合并掉了窗头那颗常驻钮（它和那一条是同一个动作的两处入口，
        // 而设计文档 `docs/design/preset-life-mode.md`「入口」那一节只要设置窗这一处，一颗就够）。**GetNodeOrNull** 而非 GetNode：
        // 那个路径现在没有节点，问的是「它还在不在」，而不在正是这一版要的样子。
        // 开着的那一扇画廊整列重灌（列是 Duplicate 出来的，两句话和出处都在上面）。
        if (_gallery is not null && IsInstanceValid(_gallery)) _gallery.Texts();

        // 生活模式那一族：窗头那颗按钮的字、菜单里那几项、起名那扇小窗上的字。
        // 菜单从这一处重建（它那几项是名单 + 几条我们的措辞，换语言要跟着换），
        // 而生活模式名**不翻** —— 那是用户自己起的词。小窗上那几处字跟着它这一趟要干什么
        // 而变（PromptTexts），所以也归这一处管。
        PromptTexts();
        RefreshLifeModeMenu();

        foreach (var row in _rows)
        {
            row.Name.Text = Tr(row.Key);
            if (row.Hint != null)
            {
                // 提示这一行就是 settings.jsonc 里写在键上面的那一句，同一个来源。
                row.Hint.Text = zh ? row.Decl.CommentZh : row.Decl.CommentEn;
            }
            if (row.Browse != null) row.Browse.Text = Tr("浏览…");
            if (row.Value is OptionButton option) Refill(option, row);
            ShowUnit(row);
        }
        // flows 那一区整列重灌一遍：步上的标签（第 N 步、挥得散、上移…）都是文字，
        // 换语言得跟着换，而重灌比逐个记着改要不容易漏。副本不受影响。
        // flows 那张表跟着副本一起换
        if (_flowBox != null) RefreshFlows();
        // 作息表那张表同一条处置：字段标签、单位词、那几个钮上的字都是文字，
        // 换语言得跟着换。副本不受影响。
        if (_blockBox != null) RefreshBlocks();
        // 勿扰那一格同一条处置：字段标签、星期几那七个勾选框、那几个钮上的字都是文字。
        if (_dndWindowBox != null) RefreshDnd();
        // 「当前前台」那一条实测结果也带一个字（标签来自语言表），换语言得跟着换。
        if (_dndForeground != null) RefreshDndForeground();
        PaintRoles();   // 文字换完再刷一遍外观：角色是覆写，换语言不换主题也该重刷
        RefreshPreview();
        Status();
    }

    /// 换语言后下拉里那几个词要跟着换，但选中项不能跳。
    private void Refill(OptionButton option, Row row)
    {
        var picked = option.Selected;
        Fill(option, row);
        if (picked >= 0) option.Select(Math.Min(picked, option.ItemCount - 1));
    }

    /// 分区：场景里的节点名 → 语言表里的那一条标题。标题是文字，所以走表；节点名不是，
    /// 它只是场景里的位置。
    ///
    /// 每一区在场景里都是 <b>Rule / Title / 各行</b> 那个次序：Rule 是分割线，Title 是
    /// 小一号灰字的小标题（13 号、灰）。它和上面那些行的名字（15 号、深字）差一档，
    /// 一眼就分得出「这是个头」和「这是一条设置」—— 改之前那头和行一样大，看着就是
    /// 又一条设置（他 2026-09-30 说这个界面「甚至无法抵达平均水平」）。
    ///
    /// <b>这是分区名的正本</b>（工单 05）。分区名同时是三样东西：
    /// ① 这一份里的节点名（= 场景里的卡片节点名，<see cref="SectionOf"/> 从节点路径反推时
    /// 拿到的就是它）；② <see cref="Domains"/> 里那些域收哪几个分区；
    /// ③ <see cref="KeepsOwnContent"/> 那几个自带内容的分区。
    /// 那三样各有各的读者，<b>改一个分区名要三样一起改</b> —— 所以下面两样都从这一份取，
    /// 那一面是字面量写的「改名要记得三处」。
    private static readonly (string Node, string Title)[] SectionTitles =
    [
        // 次序 = 场景里那张卡摆出来的次序（2026-10-06 按「最常改的放上面」重排过，
        // 见 <see cref="Domains"/> 那段）。改这一份的时候场景里也要一起搬 ——
        // 域切换只改 visible，摆出来的先后是场景里 VBoxContainer 的子节点次序。
        ("Time", "时间"), ("List", "休息清单"), ("Pomodoro", "番茄钟"),
        ("Image", "背景图片"), ("Sound", "提示音"),
        ("Dnd", "勿扰"), ("Flows", "休息流程"),
        // 遮罩排在「休息提醒」那一格的最后：它整张是高级，默认视图下不出现。
        ("Fader", "遮罩"),
        ("Calendar", "桌面日历"),
        ("Routine", "作息"),
        // 「历史」这一格（2026-10-08，工单 03）。它和前几格有一处不同：**它一条设置键都没有** ——
        // 它装的是读数（休息记录），入口是一颗开窗的钮。而「一天一行、点开看那一天」那一份
        // 是 <c>HistoryWindow</c> 和 <c>HistoryDays</c> 的事，声明表一个格子都不占
        // （设计文档 `docs/design/records.md`「历史不进生活模式」那一条）。
        ("History", "历史"),
        ("System", "系统"),
    ];

    /// <b>分区节点名它认不认得</b>（工单 05 的反向验证那一发要用）。
    /// 从 <see cref="SectionTitles"/> 取，不另抄一份名单 ——
    /// 抄一份的话，改名时忘了改它，那一条就变成一个永远答「认得」的判据。
    internal static bool KnowsSection(string node) =>
        SectionTitles.Any(s => s.Node == node);

    /// 那一区的标题（从正本取）。认不出来的分区给空串，调用方自己当「没有这一区」。
    internal static string TitleOfSection(string node) =>
        SectionTitles.FirstOrDefault(s => s.Node == node).Title ?? "";

    /// 域：顶部 TabBar 上那几格，每一格带着它收哪几个<b>分区</b>。分区名是场景里的节点名。
    ///
    /// 为什么是「分区归域」而不是「键归域」：分区是场景里那一张卡片，域切换只改 <c>visible</c>，
    /// 自检里驱动这扇窗的那几组按的还是原来那条路。<b>代价</b>是归域的粒度停在分区这一层 ——
    /// 想把单独一行挪到<b>别的域</b>，得先把它所在的那张卡片拆开，这是为「一处路径不破」付的钱。
    ///
    /// 同一个域内部的挪动不受这条限制：2026-10-01「选哪条休息流程」那一行就从自己那张卡片
    /// 搬进了「休息流程」那张卡片（那张旧卡片整个退休了），**方向是往细粒度那一侧** ——
    /// 选哪条和编辑那些条本来就是同一件事的两半，分成两张卡是断开一件本来连着的事。
    /// 「一处路径不破」那个目的仍然守着：自检里驱动这扇窗的那几组走的是原来那条路，
    /// 量的是<b>可见性</b>，不是节点路径。行跨卡之后 <see cref="SectionOf"/> 跟着改，
    /// 搜索和域切换都还认得它。
    ///
    /// 几个域的切分是按词表那两块（<c>GLOSSARY.md</c>「遮罩与休息」/「桌面日历」）加上一格
    /// 作息表和系统杂项；休息那一侧收着好几张卡片，所以它内部还需要靠搜索来找（Search 那一条线）。
    ///
    /// 「作息」是<b>自己一格</b>而不是休息那一侧的一张卡片：作息表管的是一整天的安排
    /// （`docs/design/daily-routine.md`「作息表是一张按现实时间走的表」），
    /// 它既不是一次休息的参数、也不是日历的样子，而那一格里另有两样完全不相干的东西
    /// （番茄钟、背景图片）。收在一起的后果是「休息提醒」那一格名不副实。
    /// <b>次序按「最常改的放上面」排</b>（2026-10-06 所有者定夺，原话：「按照人类的使用习惯，
    /// 最常改的应该放在上面，背景和提示音也太低下了」）。所以这一格是：
    /// 时间 → 休息清单 → 番茄钟 → 背景图片 → 提示音 → 勿扰。
    /// 休息清单排第二是因为它是这格唯一天天碰的东西；番茄钟在它后面一格（托盘里已经能开能停，
    /// 设置里拧的是专注段多长，不常动）。背景图片和提示音原来排在最后两格，现在提到中间。
    ///
    /// **遮罩（Fader）不在这一格里了**：2026-10-06 同一次定夺把它整张收进高级 ——
    /// 「没有人天天改遮罩颜色的」，所以它现在只在开了窗头那颗「显示高级设置」开关之后才出现，
    /// 而默认视图下这一格看不到它那张卡（那一族的声明行全部是 <c>advanced</c>，
    /// 自检 Audit 第 17 条对着声明表数的就是这件事）。
    private static readonly (string Id, string Title, string[] Sections)[] Domains =
    [
        ("break", "休息提醒", ["Time", "List", "Pomodoro", "Image", "Sound", "Dnd", "Fader", "Flows"]),
        // 「桌面日历」「作息」两格收的那一个分区，从 <see cref="SectionTitles"/> 取（工单 05）——
        // 写死在这儿的话，改一个分区名要记得改两处，而漏一处就是「侧栏那一格归到了
        // 不存在的分区」（那一族审计逐格断言每格归到哪一区，它一个字都不许改）。
        ("calendar", TitleOfSection("Calendar"), [SectionNode("Calendar")]),
        ("routine", TitleOfSection("Routine"), [SectionNode("Routine")]),
        // 「历史」和前两格同一形状：它自己一个分区，收的分区名从正本查。
        // **这一格一条设置键都没有** —— 契约说它装的是读数不是设置，所以声明表不占格子，
        // 而「不许有只在界面里的设置」那条通则对它不适用：托盘那一颗和这一颗开的是同一扇窗，
        // 那扇窗不改任何配置。
        ("history", TitleOfSection("History"), [SectionNode("History")]),
        ("system", "系统", ["System"]),
    ];

    /// 分区节点名从正本查（工单 05）。认不出来当场说清，不给一个空串糊过去 ——
    /// 一个空分区名会让那一格什么都不收，而症状是「界面上少一格」，看不出是名字写错了。
    ///
    /// **按节点名查**（形参就是节点名）：调用方给的是正本第一列那个值，
    /// 而正本里节点名是唯一的（标题理论上可能重名，节点名不会）。
    private static string SectionNode(string node)
    {
        if (!KnowsSection(node))
            throw new InvalidOperationException($"正本里没有节点名「{node}」那一区 —— 分区名改了，收它的域没收跟上");
        return node;
    }

    /// <summary>域的 id（<c>break</c>/<c>calendar</c>/<c>routine</c>/<c>system</c>）→ 第几格。
    /// 声明表里的 <see cref="SettingDecl.Domain"/> 就是这个 id，<see cref="Audit"/> 第 14 条拿它对账。</summary>
    private static int DomainId(string id)
    {
        for (var i = 0; i < Domains.Length; i++)
            if (Domains[i].Id == id) return i;
        return -1;
    }

    /// <summary>一个分区归哪个域 —— 自检里第 13 条拿它对着场景核。<b>不认识的分区返回 -1</b>，
    /// 那不是一个域，那是一条要报出来的问题（分区名打错字、或者新加了一个忘了归域）。</summary>
    private static int DomainOf(string section)
    {
        for (var i = 0; i < Domains.Length; i++)
            if (Domains[i].Sections.Contains(section)) return i;
        return -1;
    }

    /// 扫场景里每一行（带 <c>metadata/setting</c> 的那个 HBox），交出
    /// <b>（键名，声明，所在分区，行节点）</b>。自检里第 14 条拿它对账，
    /// 第 17 条要问「这一行此刻在不在屏幕上」—— 为那个多带一项行节点出去，
    /// 好让读的人不用自己再从键名反查一遍（那份反查要认节点名，而名字里带点是转义过的）。
    private static IEnumerable<(string Key, SettingDecl Decl, string Section, Node Row)> SectionedRows(Node root)
    {
        var form = root.GetNodeOrNull<VBoxContainer>(FormPath);
        if (form == null) yield break;
        // 2026-09-30：Form 下面一层是卡片，行在卡片里的 Body 竖排下面。
        foreach (var card in form.GetChildren().OfType<Control>())
        {
            var body = Body(root, card.Name);
            if (body == null) continue;
            foreach (var row in body.GetChildren())
            {
                if (!row.HasMeta("setting")) continue;
                var key = row.GetMeta("setting").AsString();
                if (!Settings.Declared(key)) continue;
                yield return (key, Settings.Decl(key), card.Name, row);
            }
        }
    }

    /// 2026-09-30 起分区是<b>卡片</b>：<c>Form/&lt;分区&gt;</c> 是一张 PanelContainer
    /// （<c>theme_type_variation = "Card"</c>，颜色和圆角全在 <c>Theme_light.tres</c> /
    /// <c>Theme_dark.tres</c> 里），卡片里那一竖排叫 <c>Body</c>。原来 44 行一模一样地
    /// 摆在一片白底上、只有一道细线分着区 —— 那一道线是这扇窗第三种分法。
    /// 卡片把「这一片是另一个区」这件事说清楚，两种配色各一套，不用在代码里画。
    /// 分割线（<c>Rule</c>）随卡片一起退休，原来 <c>ApplyRules</c> 那套
    /// 「第一个在场的分区不带线」也一起（判据换成 <see cref="CardShown"/>）。
    private const string FormPath = "Margin/Stack/Scroll/Form";

    /// <summary>一个分区那张卡片（<c>Form/&lt;分区&gt;</c>）。运行期只改它的
    /// <c>Visible</c>，里面那些行跟着走；自检里那几条判据拿它当「这一区在不在」。
    /// <paramref name="root"/> 给 null（还没挂上树的场景实例）时交回 null ——
    /// 「没有树根就没有这一区」是一条实测结果，不是异常。</summary>
    private static Control? Card(Node? root, string section) =>
        root?.GetNodeOrNull<Control>($"{FormPath}/{section}");

    /// <summary>卡片里那一竖排（标题 + 那些行）。分区自己不带行，行全在这儿。</summary>
    private static VBoxContainer? Body(Node? root, string section) =>
        root?.GetNodeOrNull<VBoxContainer>($"{FormPath}/{section}/Body");

    // 搜索和域标签是可见性的两支。**域标签那一支 2026-10-07 接回来了** ——
    // 2026-10-06 那一发把它当成「把选项收起来」的东西整格删了，而所有者要的是
    // 「域标签不许被藏」，两者不是一回事（那一段误读的来由见 <see cref="BuildNav"/>）。
    private string _query = "";

    /// <summary>窗头域标签选中的是第几格（0 起）。<b>不落盘</b> —— 界面状态不进配置
    /// （和「显示高级设置」那一条同一个道理，那一颗 2026-10-06 起退休了）。
    /// <c>-1</c> 是「还没接上」：<see cref="BuildNav"/> 跑完就是 0。</summary>
    private int _domain = -1;

    /// <summary>窗头那一行：左边是域标签，右边是生活模式、预设、搜索。
    ///
    /// **2026-10-07 更正**：2026-10-06 那一发把 <c>Margin/Stack/Nav/Domains</c> 那个 TabBar
    /// 连同接线一起删了，理由记成「休息提醒、桌面日历 切换那里不可出现切换」。<b>那句话读反了</b> ——
    /// 域标签就是那排切换，藏的是「不许藏域标签」，不是「不许有切换」。删掉之后 11 张分区卡
    /// 在一条 3752 像素的滚动里从上到下排开，没有跳转入口，滚到的人以为「桌面日历」不见了。
    /// 场景文件窗头那一段的注释也写着同一个误读，两处都更正了。
    ///
    /// **「一眼看到一切」约束的是行，不是卡片。** 域切换换的是「这一格摆哪几张卡」，
    /// 一格里面的行一条都不收。「显示高级设置」那颗开关 2026-10-06 起退休，不回来：
    /// 它按下去就是在藏行，而藏行正是所有者两次都点过头的那个形状。
    ///
    /// 分域那张表（<see cref="Domains"/>）是这一格唯一的真相：标签摆哪几张卡全从它来，
    /// <see cref="Audit"/> 第 13、14 条拿它核「这一条键归哪一格」和「这一格收哪几张卡片」两头
    /// 对不对得上。</summary>
    private void BuildNav()
    {
        // 域标签。**它不许被藏**：常驻窗头最左，四格一次全摆出来，没有折叠、没有开关、
        // 没有「要滚到才看见」。它只决定「这一格摆哪几张卡片」，不决定某一格里的行露不露。
        var bar = GetNode<TabBar>("Margin/Stack/Nav/Domains");
        foreach (var (_, title, _) in Domains) bar.AddTab(Tr(title));
        _domain = 0;
        bar.TabChanged += i =>
        {
            _domain = (int)i;
            ApplyVisibility();
            Log("options", $"换到第 {_domain + 1} 个域「{Tr(Domains[_domain].Title)}」："
                           + $"{string.Join('、', Domains[_domain].Sections)}");
        };

        // 搜索框排在这一行最右，也就是窗头的右上角。撑开的是域标签那一格。
        var search = GetNode<LineEdit>("Margin/Stack/Nav/Search");
        search.TextChanged += text =>
        {
            _query = text;
            ApplyVisibility();
            Log("options", $"搜索 '{text}'：可见的行 {VisibleRows()}");
        };
        ApplyVisibility();
    }

    /// 是不是声明成「高级」那一档。<b>收的是声明而不是控件</b>。
    ///
    /// <b>它现在不再决定任何东西在不在屏幕上</b>（<see cref="ApplyVisibility"/> 里没有这一支了）：
    /// 2026-10-06 之后**档位不再决定任何东西在不在屏幕上**（<see cref="ApplyVisibility"/> 里没有这一支）：
    /// 这一格里的叶子键一条都不收。这一族剩下来只做两件事 —— 声明表里 <c>tier</c> 拼错要报出来
    /// （<see cref="Audit"/> 第 15 条），以及自检要能说清「这一条是普通那一档还是高级那一档」。
    /// 档位本身仍然写在文档里，跟着 <c>settings.jsonc</c> 走。
    ///
    /// <b>「显示高级设置」那颗开关，2026-10-06 退休，2026-10-07 确认不回来。</b>
    /// 它的来由和被否掉的更早那一版都记在这里（免得下一次又照着记忆抄回来）：窗头一颗、
    /// 默认收着 28 条，是 2026-09-29 到 09-30 那一轮的定夺，理由是「44 条一屏走完 13 行太挤」；
    /// 每一区一颗的更早一版（2026-09-30 上午）是一眼看就否掉的。
    /// 它和域标签是两件事：域标签 2026-10-07 接回来了（藏的是「不许藏域标签」），
    /// 这颗开关不回来（它按下去就是在藏行）。
    /// 真判据在自检里（<see cref="NavSelfTest"/>「全在场」那一组）。
    private static bool IsAdvanced(SettingDecl decl) => decl.Tier == "advanced";

    private static bool IsAdvanced(Row row) => IsAdvanced(row.Decl);

    /// 档位取值认不认得。自检里第 15 条用它 —— 拼错的那一种会让这一条在**文档里**归不到任何一档，
    /// 而文档是要跟着走很久的。
    private static bool TierKnown(SettingDecl decl) => decl.Tier is "common" or "advanced";

    /// <summary>可见性只有这一处说了算，它有两支：域标签和搜索。<b>没有第三支。</b>
    ///
    /// <b>域标签管卡片，不管行。</b> 不在当前这一格的分区整张收掉，一格里面的行一条都不收 ——
    /// 界线就这一条：域切换换的是「这一格摆哪几张卡」，它不是「把行藏起来」。
    /// 2026-10-06 那一发把域切换当成第三支（藏东西的）连根拔了，2026-10-07 接回来：
    /// 域标签不许被藏，是所有者点过头的；藏行不许，是同一个所有者点过头的另一件事。
    ///
    /// <b>搜索压过域。</b> 搜索词非空时结果跨域摆出来，标签停在原处不跳（<see cref="ApplyVisibility"/>）。
    ///
    /// 搜索为空时一行都不筛；搜到东西时，一个<b>一行都没剩下</b>的分区连卡片一起收掉 ——
    /// 留着空标题会让人以为那一块还有内容，而它其实已经被筛空了（2026-09-29 那一发搜「音量」，
    /// 屏幕上还挂着「时间」「遮罩」「休息生活模式」三个空标题）。
    ///
    /// 那几区<b>自带内容</b>的分区（休息流程、清单、作息、勿扰）搜到东西时仍留在场上，
    /// 因为搜索筛不掉它们自己那一块（见 <see cref="KeepsOwnContent"/>）。</summary>
    private void ApplyVisibility()
    {
        // 搜索非空时它压过域：这是刻意选的。域切换是「浏览那一格」，搜索是「找那一条」——
        // 人在「休息提醒」那一格敲「日历」，若还按域筛，会一条都搜不到，于是又得出
        // 「选项不见了」那一个结论（2026-10-07 所有者报的正是这个症状）。
        // 搜出来的结果跨域摆出来，标签停在原处不跳 —— 它是筛选，不是导航。
        var searching = _query.Length > 0;
        var mine = _domain >= 0 && _domain < Domains.Length ? Domains[_domain].Sections : [];

        foreach (var (node, _) in SectionTitles)
        {
            var card = Card(this, node);
            if (card == null) continue;

            // 不在当前这一格的卡片整张收掉 —— 域切换的作用只到这一层，
            // 它不碰卡片里任何一行（那才是「藏东西」，两者的界线在这里）。
            if (!searching && !mine.Contains(node))
            {
                card.Visible = false;
                foreach (var row in _rows)
                    if (row.Value?.GetParent() is Control c && SectionOf(c) == node) c.Visible = false;
                continue;
            }

            // 先按行筛，再看这一区还剩不剩东西。
            var anyLeft = false;
            foreach (var row in _rows)
            {
                if (row.Value?.GetParent() is not Control control) continue;
                if (SectionOf(control) != node) continue;
                // 空词等于全中，所以「开窗这一格每一行都在屏幕上」不是这一支的特例，
                // 是同一个算术在空词上的结果（<see cref="RowMatches"/>）。
                control.Visible = RowMatches(row, _query);
                if (control.Visible) anyLeft = true;
            }
            // 一个纯设置行的区被搜空了就连卡片一起收 —— 不留一张空卡片在屏幕上。
            // KeepsOwnContent 那几区（休息流程、清单、作息、勿扰）自带内容，搜索筛不掉它们那一块，
            // 所以它们留在场上。
            var drop = !anyLeft && !KeepsOwnContent.Contains(node);
            card.Visible = !drop;
        }
        var title = _domain >= 0 && _domain < Domains.Length ? Tr(Domains[_domain].Title) : "—";
        Log("options", $"可见性：域 {_domain}（{title}） 搜索 '{_query}' → 可见的行 {VisibleRows()}");
    }

    /// 这一处原来还收一道分割线（「第一个在场的分区不带它」）—— 分割线随卡片一起退休了。
    /// 那条判据本身的来由留在 <see cref="NavSelfTest"/> 里（它量的是「区与区之间看得见吗」，
    /// 现在由卡片在不在树上来量），别在别处再抄一遍。

    /// 这几个分区里有搜索筛不掉的东西（休息流程那一区的步编辑器和「选哪一条」那行、清单那几行、
    /// 作息表那一区的时间块编辑器、勿扰那一区的两个编辑器）：搜到东西时它们仍留在场上，
    /// 而一个纯设置行的区被筛空了会整区收掉。
    ///
    /// <b>「历史」也在这一份里，而它和前几个不是同一种理由</b>：前几个是有编辑器，
    /// 这一格是**一条设置行都没有**（审计里「一棵设置行都没有 = 一张点不动的空卡片」那条
    /// 会照样报它）。它自己那颗钮是它的内容，而搜索只筛设置行 —— 不登记在这儿，
    /// 搜别的词的时候这一格就整张收掉，用户以为「历史」不见了（和 2026-10-07
    /// 域标签那一次是同一族症状：某一格不见了，而它没有被藏，是被筛掉了）。
    ///
    /// <b>从 <see cref="SectionTitles"/> 取</b>（工单 05）：这一份原来写的是节点名字面量，
    /// 改一个分区名要记得改两处。现在写在这里的每一个都要**先在正本里查得到**，
    /// 查不到当场抛 —— 症状本来是「界面上少一格」，看不出是名字写错了
    ///（空串会让那一格什么都不收，而搜索仍把内容留在场上，看着像搜索坏了）。
    ///
    /// <b>为什么是属性不是字段</b>：静态字段求值的先后由声明次序决定，
    /// 读一个还没求值的静态字段会拿到 null（实测过一次：`NullReferenceException`
    /// 冲进 `Audit`，整组自检没跑）。改成按需读取，谁先谁后都不影响。</summary>
    private static string[] KeepsOwnContent =>
        new[] { "Flows", "List", "Routine", "Dnd", "History" }.Select(SectionNode).ToArray();

    /// 这一行挂在哪个分区底下 —— 从节点路径反推，场景里没有第二份映射。
    /// <b>往上两层</b>：行 → Body（卡片里那一竖排）→ 卡片 → Form。
    /// <b>为什么不是一层</b>：2026-09-30 之前是 行 → 分区 → Form，一层就够；分区变成卡片
    /// 之后中间多了 Body 那一竖排，只取一层拿到的是「Body」——于是每一行都算成
    /// 「不属于任何分区」，搜索一条也筛不掉（这个坑上一次踩过，见下面那段的旧账）。
    private static string SectionOf(Node row)
    {
        var body = row.GetParent();
        if (body == null) return "";
        var card = body.GetParent();
        return card is Control ? card.Name.ToString() : body.Name.ToString();
    }

    /// 这一行配不配得上搜索词。空词 = 全中；否则键名、标签、提示、单位四样里任一样里有它就算。
    /// 键名也参与匹配，所以敲 <c>opacity</c> 能找到「最大不透明度」那一行 ——
    /// 中文标签和英文键名是两套词，只按其中一套筛会有一半的人搜不到。
    private bool RowMatches(Row row, string query)
    {
        return MatchWhere(row, query) != "";
    }

    /// 哪一栏命中了 —— 自检里报出来用。搜不到就是空串，搜得到就是 "键名"/"标签"/"提示"/"单位"。
    /// <b>为什么要有这一层</b>：2026-09-29 那一发「搜 opacity，音量那一行还在」就是靠它定住的 ——
    /// 只报「不该在」，下一个实测结果的人只能再去猜是哪一栏撞上了词。
    private string MatchWhere(Row row, string query)
    {
        if (query.Length == 0) return "空词";
        var q = query.ToLowerInvariant();
        if (row.Key.ToLowerInvariant().Contains(q)) return "键名";
        if (row.Name.Text.ToLowerInvariant().Contains(q)) return "标签";
        if (row.Hint != null && row.Hint.Text.ToLowerInvariant().Contains(q)) return "提示";
        if (row.Unit != null && row.Unit.Text.ToLowerInvariant().Contains(q)) return "单位";
        return "";
    }

    /// <summary>测量程序用的：切到第几个域。
    ///
    /// 走的是 TabBar 自己的 <c>current_tab</c>（和真点出来的是同一条接线），
    /// 不直接改 <c>_domain</c> —— 后者只改字段，控件不会跟着排版。
    /// <c>TabChanged</c> 在赋 <c>current_tab</c> 时自己会发，所以可见性在那条接线上刷。</summary>
    public void GoDomain(int domain)
    {
        var bar = GetNodeOrNull<TabBar>("Margin/Stack/Nav/Domains");
        if (bar == null || domain < 0 || domain >= Domains.Length) return;
        bar.CurrentTab = domain;
        bar.EmitSignal(TabBar.SignalName.TabChanged, (long)domain);
        ApplyVisibility();
        Log("options", $"测量程序换到第 {domain + 1} 个域「{Tr(Domains[domain].Title)}」");
    }

    /// <summary>窗头域标签现在选中的是第几格（0 起）。测量程序和自检读它。</summary>
    public int DomainIndex() => _domain;

    /// <summary>测量程序用的：设置窗里搜一个词。和真打字同一条路（<c>TextChanged</c>）。</summary>
    public void GoSearch(string query)
    {
        var edit = GetNodeOrNull<LineEdit>("Margin/Stack/Nav/Search");
        if (edit == null) return;
        edit.Text = query;
        _query = query;
        ApplyVisibility();
        Log("options", $"测量程序搜 '{query}'：可见的行 {VisibleRows()}");
    }

    // ---- 生活模式：窗头那颗按钮、它展开的菜单、起名那扇小窗 ------------------------
    //
    // 这一族和上面那个 flows 那一区**不是一回事**，词汇表里也是两组词（GLOSSARY.md
    // 「休息流程」的禁用词就是「生活模式」）：那一区改的是配置里那张 flows 表（一条休息流程 = 一串步），
    // 这一族管的是「一整套配置的命名快照」—— 存一份、切一份。
    // 规矩和取舍全在设计文档 `docs/design/life-mode.md`「生活模式是一整套配置的快照，它住在一份名单和一份文档上」那一节。

    /// <summary>把键盘焦点落在窗头那颗「生活模式：…」上，并让它张开菜单。
    ///
    /// <b>托盘那颗子菜单的「管理生活模式…」落到这里</b>：托盘那一层只管「切」，
    /// 新建要输名字、覆盖要确认、删掉要点头 —— 那三件都在这扇窗里，所以托盘最后一条
    /// 做的不是「打开设置」那么笼统的一件事，而是<b>打开设置并落在生活模式这一族上</b>。
    /// 落在窗头第一格（休息提醒那一格）的话，用户还得自己再找一次生活模式在哪。
    ///
    /// 走的是那颗钮自己的 <c>GrabFocus</c> + <c>ShowPopup</c>（和真点它一下是同一条接线），
    /// 不直接改字段 —— 改字段控件不会跟着排版。
    /// 菜单不在这儿张开：<c>PopupMenu</c> 自己就是屏上一扇窗，而托盘那条调用发生在
    /// <c>TrackPopupMenu</c> 刚退回来的那一帧，弹层要活到下一帧才看得见，所以只给焦点，
    /// 张开交给用户在托盘那一侧再点一下（或者测量程序的 <see cref="ShowLifeModeRing"/>）。</summary>
    public void FocusLifeModeButton()
    {
        var button = GetNodeOrNull<MenuButton>(LifeModePath);
        if (button == null)
        {
            Log("options", "窗头没有那颗生活模式钮，焦点没地方落");
            return;
        }
        button.GrabFocus();
        Log("options", $"焦点落在窗头那颗「{button.Text}」上（{LifeModePath}）");
    }

    /// <summary>测量程序用的：把窗头那颗钮的菜单<b>张开</b>（<c>--lifemode-menu</c>）。
    /// 走的是 <c>MenuButton.ShowPopup</c>，和真点那颗钮是同一条路 —— 直接
    /// <c>popup.Visible = true</c> 量到的是引擎给的位置，不是用户点出来的那一个。
    ///
    /// 上半每一条是<b>子菜单</b>（「切换」/「保存并切换」那两项就住在里面），而子菜单要
    /// 指针悬上去才张得开 —— 测量程序没人悬，所以这里把上半第一条的子菜单也自己摆出来
    /// （<c>Popup</c> 收的是<b>屏上</b>那个矩形，见 <see cref="LifeModeRingWindows"/>）。
    /// 摆不出来就在日志里说一句：那半张图缺什么，测量程序不假装它有。</summary>
    public void ShowLifeModeRing()
    {
        var button = GetNodeOrNull<MenuButton>(LifeModePath);
        if (button == null)
        {
            Log("options", "测量程序：窗头没有那颗生活模式钮，菜单张不开");
            return;
        }
        button.ShowPopup();
        var popup = button.GetPopup();
        var shown = popup.Visible;
        var sub = popup.GetItemSubmenuNode(0);
        if (sub is Window subWindow)
        {
            // 摆在爹那一项的右边：菜单本体已经落在屏上了，子菜单摆在它右边外侧一格。
            var beside = new Rect2I(popup.Position.X + popup.Size.X, popup.Position.Y,
                subWindow.Size.X, subWindow.Size.Y);
            subWindow.Popup(beside);
            Log("options", $"测量程序：菜单本体可见={shown} 屏上 {popup.Position} 大小 {popup.Size}；" +
                           $"子菜单摆在 {subWindow.Position} 大小 {subWindow.Size} 可见={subWindow.Visible}");
        }
        else
        {
            Log("options", $"测量程序：菜单本体可见={shown}，上半第一条没有子菜单 —— 「两个动作」那一半没拍到");
        }
    }

    /// <summary>测量程序用的：菜单那一圈是**屏上另外几扇窗**（<c>PopupMenu</c> 自己就是
    /// <c>Window</c>），抓帧那一块矩形只装得下设置窗自己。抓的时候把矩形撑到装得下它们 ——
    /// 量的是像素，不是「<c>Visible=True</c>」（那一族假象记在 CODING_STANDARDS.md 第 2 条）。</summary>
    public List<Window> LifeModeRingWindows()
    {
        var out_ = new List<Window>();
        var button = GetNodeOrNull<MenuButton>(LifeModePath);
        var popup = button?.GetPopup();
        if (popup != null && popup.Visible) out_.Add(popup);
        for (int i = 0; i < (popup?.ItemCount ?? 0); i++)
            if (popup!.GetItemSubmenuNode(i) is Window sub && sub.Visible) out_.Add(sub);
        return out_;
    }

    /// <summary>测量程序用的：把**域内**的一格拧成一个和生活模式那一份不一样的值 ——
    /// 「当前位置」于是从「生活模式：X」翻成「不在任何生活模式里」
    /// （<see cref="RefreshLifeModeWhere"/> 比的是<b>窗里这一份</b>）。
    /// 走的是那颗 <c>SpinBox</c> 自己的 <c>Value</c>，和真拧它是同一条接线。
    ///
    /// 只拧<b>域内</b>的一格：域外那几条切生活模式的时候一个字节都不动，拧它们量不出档位变化。</summary>
    public void LoosenLifeModeOneValue()
    {
        foreach (var row in _rows)
        {
            if (row.Value is not SpinBox box) continue;
            if (!LifeModeStore.InLifeMode(row.Decl)) continue;
            box.Value = Math.Min(box.MaxValue, box.Value + 3);
            Log("options", $"测量程序：把「{row.Key}」拧成了 {box.Value} —— 窗里这一份和生活模式那一份对不上了");
            return;
        }
        Log("options", "测量程序：一个域内的 SpinBox 都没有，那一档拧不出来");
    }

    internal const string LifeModePath = "Margin/Stack/Nav/LifeMode";

    /// <summary>生活模式管理器（<c>Presets</c> 那一扇）上那颗「预设生活模式…」按钮的路径。
    ///
    /// **2026-10-08 合并**：这一族原先是<b>窗头一颗常驻钮 + 生活模式菜单里一条</b>两处入口，
    /// 而它们开的是同一扇窗（<see cref="OpenPresetsGallery"/>）。所有者定合并成一颗 ——
    /// 删掉窗头那颗，入口就是菜单里那一条（<see cref="RefreshLifeModeMenu"/> 里的
    /// <c>PresetsId</c>），于是这个路径在<b>设置窗这一侧</b>已经没有节点了
    /// （<c>Options.tscn</c> 的 <c>Margin/Stack/Nav</c> 那一行现在只有域标签、
    /// 生活模式、搜索三颗）。
    ///
    /// <b>契约那一句没有被破坏</b>（设计文档 `docs/design/preset-life-mode.md`「入口」那一节：「画廊的入口在生活模式管理器和设置窗」）——
    /// 管理器那一颗还在（不归这一扇管），设置窗这一处是菜单里那一条，
    /// 动作还是同一个（<see cref="OpenPresetsGallery"/>）。判据是
    /// <see cref="Presets.EntriesSelfTest"/>/<see cref="NavSelfTest"/> 那两处。</summary>
    internal const string PresetsPath = "Margin/Stack/Nav/Presets";

    internal const string NamePath = "Name";
    internal const string NameTextPath = NamePath + "/Margin/Stack/Text";
    internal const string NameErrPath = NamePath + "/Margin/Stack/Err";
    internal const string NameOkPath = NamePath + "/Margin/Stack/Buttons/Ok";
    internal const string NameCancelPath = NamePath + "/Margin/Stack/Buttons/Cancel";

    // ---- 自检里按菜单的那几把尺子（flows-menu / flows-unreadable 两组共用）---------
    //
    // 按的是**写着这句**的那一项，词取自语言表（`CODING_STANDARDS.md` 第 6 条：手抄一份
    // 副本，标签改过字它就悄悄量起「没人搜的词」来）。上半那几条按在**它自己的子菜单**上：
    // 设计文档 `docs/design/life-mode.md`「切换有两个动作」那一条让一个目标带两个动作，动作在子菜单身上。
    //
    // **它们是类上的方法而不是某一组里的局部函数**：两组都要按菜单（flows-menu 量两个动作
    // 和三项管理，flows-unreadable 量「按那一个读不通的名字」），抄第二份就会有一份悄悄过期。

    internal static void PressItem(PopupMenu menu, string label)
    {
        for (int i = 0; i < menu.ItemCount; i++)
        {
            if (menu.IsItemSeparator(i) || menu.GetItemText(i) != label) continue;
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, menu.GetItemId(i));
            return;
        }
        throw new Exception($"菜单里没有「{label}」那一项");
    }

    internal static bool HasItem(PopupMenu menu, string label) => Where(menu, label) >= 0;

    /// 菜单里那一项的下标，找不到是 -1。**菜单里不数项**：判据问的是「那一条在不在」，
    /// 不是「该有 N 项」。
    internal static int Where(PopupMenu menu, string label)
    {
        for (int i = 0; i < menu.ItemCount; i++)
            if (!menu.IsItemSeparator(i) && menu.GetItemText(i) == label) return i;
        return -1;
    }

    internal static void PressSub(PopupMenu top, int which, string label)
    {
        var sub = top.GetItemSubmenuNode(which);
        if (sub == null) throw new Exception($"菜单上半第 {which + 1} 项没有子菜单");
        PressItem(sub, label);
    }

    /// <summary>按**那个生活模式**的「切换」。上半那一项的字就是生活模式名（用户自己起的词，不翻），
    /// 所以按名字找得到它 —— <b>不按下标</b>：下标是名单的顺序，而这一组的名单里有一个
    /// 读不通的、一个文件不在的，按下标就变成「按名单里第几个」而不是「按那一个」。</summary>
    private static void PressSwitchFor(PopupMenu top, string modeName) =>
        PressSub(top, Where(top, modeName), "切换");

    /// 菜单里下半截那几项的号。上面每一项是一个**子菜单**（<b>一个目标、两个动作</b>），
    /// 所以它自己那一个号没有用 —— 引擎给的是 -1，而这里也传 -1：万一引擎把「按了子菜单
    /// 那一项」也往上递一次，这个 -1 落进下面那个 <c>if</c> 里什么也不做（动作在子菜单
    /// 自己身上接线，见 <see cref="BuildLifeModeMenu"/>）。
    /// 菜单里不数项（设计文档 `docs/design/life-mode.md`「菜单里不数项」那条）：项数是从名单上数出来的。</summary>
    private const long SaveAsId = -2;
    private const long OverwriteId = -3;
    private const long DeleteId = -4;

    /// 「从一份文件收一个生活模式进来…」那一项的号（工单 05）。**它不在托盘那一侧** ——
    /// 托盘只做「切」，新建要输名字、覆盖要确认，都不进那个模态循环，而收进来要开一次
    /// 文件框、再问一次名字（设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那条：设置窗这一侧管，托盘那
    /// 一侧只切）。</summary>
    private const long TakeInId = -5;

    /// 「预设生活模式…」那一项的号 —— 窗头那颗生活模式按钮展开的菜单下半（设计文档 `docs/design/preset-life-mode.md`「入口」那一节
    /// 的<b>设置窗这一处</b>：画廊的入口在生活模式管理器和设置窗；托盘不加入口）。和
    /// <see cref="TakeInId"/> 挨着：两个都是「往名单里多一条」的动作。
    private const long PresetsId = -6;

    /// 子菜单里那两项的号。<b>两栏共用一个目标</b>：设计文档 `docs/design/life-mode.md`「切换有两个动作」那条 ——
    /// 「切换」把窗里没保存的改动作废并在状态栏说一声，「保存并切换」先把窗里这份按保存
    /// 落到磁盘再切，保存不成就说清为什么并且<b>不切</b>。</summary>
    private const long SwitchId = 0;
    private const long SaveAndSwitchId = 1;

    /// 「删掉这一条」那一项在弹出菜单里的位置。<b>不是一份状态</b>，是刚摆上去那一项的
    /// 下标（菜单重建时重摆一遍）；灰不灰由 <see cref="RefreshLifeModeWhere"/> 现问。</summary>
    private int _deleteItem = -1;

    /// 起名那扇小窗这一趟是来干什么的。**界面状态，不落盘**（同「显示高级设置」那一条），
    /// 而且只活到这一扇小窗关掉为止 —— 确定那一下走的是它（<see cref="AcceptLifeModeName"/>）。</summary>
    private enum NameAsk { Save, Overwrite, Delete, TakeIn }

    private NameAsk _asking = NameAsk.Save;

    /// 「收一个生活模式进来」这一趟挑中的那个文件。<b>界面状态，不落盘</b>，而且只活到那一扇
    /// 小窗关掉为止 —— 确定那一下走的是它（<see cref="TakeInFrom"/>），按取消就清掉。
    /// **不许拿它当第二份真相**：它是用户刚点出来的那一个，不是我们记得的某一个。</summary>
    private string? _takeInFrom;

    /// <summary>窗头那颗按钮 + 它展开的菜单。接线接一次，<see cref="RefreshLifeModeMenu"/> 只重建
    /// 菜单里那几项 —— 那几项是名单（数据），不是接线。
    ///
    /// <b>菜单的形状是「上半切、下半管」</b>（设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那条）：上半是
    /// 名单里那几条，<b>每一条是一个子菜单、两个动作</b>（切换 / 保存并切换）—— 一个目标
    /// 两个动作，而弹出菜单能原样摆出来的形状里，子菜单就是那一个；下半一条分隔线加
    /// 存为新生活模式 / 覆盖当前生活模式 / 删掉这一条。「打开管理器」是收一个生活模式进来那张工单的，
    /// 现在不摆 —— 摆一颗按了没反应的项比不摆更糟。
    ///
    /// 菜单项的字在 <see cref="Texts"/> 里换语言（<see cref="RefreshLifeModeMenu"/> 从那里调一次），
    /// 生活模式名**不翻**：那是用户自己起的词（同休息流程的名字那一族）。</summary>
    private void BuildLifeModeMenu()
    {
        _modeButton = GetNode<MenuButton>(LifeModePath);
        // 按的是菜单自己发的那一个 id —— 和真鼠标点在菜单项上是同一条接线。
        // （MenuButton 自己的 ItemSelected 在这一版 C# 里没有暴露，所以接它的 PopupMenu。）
        _modeButton.GetPopup().IdPressed += id =>
        {
            if (id == SaveAsId) { AskLifeModeName(NameAsk.Save); return; }
            if (id == OverwriteId) { AskLifeModeName(NameAsk.Overwrite); return; }
            if (id == DeleteId) { AskLifeModeName(NameAsk.Delete); return; }
            if (id == TakeInId) { AskTakeInFrom(); return; }
            if (id == PresetsId) { OpenPresetsGallery(); return; }
            // 其余那些号是名单里那几条的子菜单 —— 动作在子菜单自己身上（两个动作共用一个
            // 目标），这里不接。上面那一条注释说清为什么这一支不能「顺手也切一下」。
        };
        var prompt = GetNode<Window>(NamePath);
        GetNode<Button>(NameOkPath).Pressed += AcceptLifeModeName;
        // 取消 = 这一趟结束了：那一份文件也就没挑过了（下一趟重新挑，不拿上一次那个）。
        // **这一发没有被自检量到**（2026-10-01 反向验过：去掉这一句自检是绿的 —— 到
        // <see cref="TakeInFrom"/> 的唯一一条路是「刚交完一个文件路径」，所以一个陈旧的
        // 路径根本走不到那里）。留着是卫生，不是判据；判据里放一条量不到的东西比不放更糟
        // （它会一直绿着，像在守着什么）。
        GetNode<Button>(NameCancelPath).Pressed += () => { _takeInFrom = null; prompt.Hide(); };
        // 回车和确定是同一件事（对话框里都这样），走的是 LineEdit 自己发的那一个信号。
        GetNode<LineEdit>(NameTextPath).TextSubmitted += _ => AcceptLifeModeName();
        // 管理器那一处：那一扇自己树上有一颗常驻的钮（不归这一扇管）。
        // 设置窗这一侧的入口是**生活模式菜单里那一条**（`PresetsId`，见上面
        // `RefreshLifeModeMenu` 那一发）—— 2026-10-08 合并掉了窗头那颗常驻钮，
        // 它和那一条开的是同一扇窗（<see cref="OpenPresetsGallery"/>），
        // 而设计文档 `docs/design/preset-life-mode.md`「入口」那一节只要设置窗这一处。所以这里**不再**接一颗窗头按钮的 Pressed。
        RefreshLifeModeMenu();
    }

    /// <summary>打开预设生活模式的画廊（<see cref="Presets"/>）。
    ///
    /// <b>开着就重新列一次，不另开一扇</b>：收进来的那一份还在那一个目录里，而程序升级换了那一个
    /// 目录之后，那一扇窗上摆的还是旧的列 —— 重列是「这一族是现问的」那一条
    /// （和托盘菜单现做、名单现读同款）。两处入口都走这一条路，所以「两处入口」是<b>两处
    /// 按得到</b>，不是两扇窗。
    ///
    /// <b>那一扇是设置窗的子窗</b>（和起名那扇小窗同一种挂法）：它的配色跟着设置窗那一个
    /// <c>uiTheme</c> 键，收进来一个之后菜单要重建（<see cref="Presets.Took"/>）——
    /// 「界面状态不进配置」那一条在这里是「那一扇窗不进配置」。</summary>
    public void OpenPresetsGallery()
    {
        if (_gallery is not null && IsInstanceValid(_gallery))
        {
            _gallery.Refresh();
            return;
        }
        if (GD.Load<PackedScene>(Presets.ScenePath)?.Instantiate<Presets>() is not Presets made)
        {
            // 读不出来要说：摆一颗按了没反应的项比不摆更糟（同 RefreshLifeModeMenu 里
            // 「问不到就抛」那一条的理由），而这里是「按了开不起来」那一档。
            Say(Tr("预设生活模式的画廊开不出来"), true);
            Log("options", $"{Presets.ScenePath} 实例化不出来，画廊开不了");
            return;
        }
        made.DataDir = DataDir;
        made.PresetDir = PresetLifeModes.Dir;
        made.UiThemeValue = (string)_copy.Map.Get(UiTheme.Key) ?? "light";
        made.Log = Log;
        // 收进来一个之后名单上多了一条，菜单得重建 —— 而「当下站着的是哪一个」是逐键比出来的
        // （StandingOf），所以钮上的字也会跟着走。
        made.Took += () => { RefreshLifeModeMenu(); Log("options", "收进来一个预设生活模式，菜单重建了"); };
        AddChild(made);
        _gallery = made;
    }

    /// <summary>当下开着的那一扇画廊（还没有开过是 null）。<b>给自检里的判据读</b> ——
    /// 「按了那一项有没有开窗」量的是接线，而接线的结果就是这一个对象在不在树上
    /// （<c>Presets.EntriesSelfTest</c>）。</summary>
    internal Presets? Gallery => _gallery;

    private Presets? _gallery;

    /// 菜单从磁盘上那份名单重建。名单读不通是一档状态（<see cref="LifeModeIndex.Unreadable"/>）：
    /// 那一族当成没有，菜单上半就是空的，而**磁盘上那份名单一个字节都不动**
    /// （设计文档 `docs/design/life-mode.md`「读不通有三档」那条第一档）。
    ///
    /// **菜单里不数项**：上半几项来自名单（数据），下半是我们自己的那几句 ——
    /// **项数从表上数，不写死在这里**（设计文档 `docs/design/life-mode.md`「菜单里不数项」那条）。</summary>
    private void RefreshLifeModeMenu()
    {
        if (_modeButton == null) return;
        _modeIndex = LifeModeIndex.Load(LifeModeStore.ListPathOf(DataDir));
        var popup = _modeButton.GetPopup();
        popup.Clear();
        for (int i = 0; i < _modeIndex.Entries.Count; i++)
        {
            // 名字抄进闭包：子菜单是每趟现造的，菜单里那一条和它的动作要说的是同一个名字。
            var name = _modeIndex.Entries[i].Name;
            var sub = new PopupMenu();
            sub.AddItem(Tr("切换"), (int)SwitchId);
            sub.AddItem(Tr("保存并切换"), (int)SaveAndSwitchId);
            // 按的是子菜单自己发的那一个 id —— 和真鼠标点在那一项上是同一条接线。
            sub.IdPressed += id =>
            {
                if (id == SwitchId) SwitchLifeMode(name);
                else if (id == SaveAndSwitchId) SaveAndSwitch(name);
            };
            // **子菜单是运行期现造的，所以走 AddSubmenuNodeItem**：另一个
            // AddSubmenuItem 的第二个参数是那份 PopupMenu 资源的**路径**（一个 String），
            // 而 SetItemSubmenu 也是。接节点的那两个才是给现造的东西用的。
            popup.AddSubmenuNodeItem(name, sub, -1);   // 用户起的名字，不翻
        }
        if (_modeIndex.Entries.Count > 0) popup.AddSeparator();   // 一条都没有就别留一条空的分隔线
        popup.AddItem(Tr("存为新生活模式"), (int)SaveAsId);
        popup.AddItem(Tr("盖回一个生活模式…"), (int)OverwriteId);
        popup.AddItem(Tr("删掉这一条"), (int)DeleteId);
        popup.AddItem(Tr("从一份文件收一个生活模式进来…"), (int)TakeInId);
        popup.AddItem(Tr("预设生活模式…"), (int)PresetsId);
        // 「删掉这一条」的位置**从菜单上按名字问回来，不按下标数**。它上面那几句还会加
        // （设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那一节那份菜单形状里还留着「打开管理器」），`ItemCount - 1` / `- 2` 这种
        // 写法在加一项的那天就灰错项，而自检全绿 —— 判据是按字找的，量不到下标。
        //
        // 问不到就**抛**，不当 -1 静悄悄跳过：那等于把「它该灰的那一条灰不着」变成
        // 一句没人说的话，而自检是绿的（同 Attach 挂两次抛一个理由：那不是一档状态，
        // 是写错了）。
        _deleteItem = Where(popup, Tr("删掉这一条")) is var at && at >= 0
            ? at
            : throw new InvalidOperationException(
                $"生活模式菜单里没有「{Tr("删掉这一条")}」这一项，删掉它的那一条灰不着");
        RefreshLifeModeWhere();
        // **名单读不通要摆出来，而摆出来的是「名单」**（设计文档 `docs/design/life-mode.md`「读不通有三档，各说各的」头一档）。
        // 「这一族当成没有」是**行为**（菜单上半空着），「一个生活模式都没有」是**另一句话** ——
        // 后者就是把一份坏文件说成用户没存过，而自检里看着是绿的（01/02 那一版就是那样）。
        // 常驻的那一行，不是等按了保存那一刻（同 ShowDocError 那一条的理由：读者在按之前）。
        if (_modeIndex.Unreadable) Say(ListUnreadableSaid(), true);
    }

    /// <summary>窗头那颗按钮上写着的那一档（设计文档 `docs/design/life-mode.md`「「我现在在哪个生活模式」不落盘」那条）：
    /// 一个生活模式都没有写「还没有生活模式」，和某一个生活模式一致写「生活模式：那个名字」，
    /// 都不一致写「不在任何生活模式里」，<b>而名单读不通自己一档</b>写「生活模式名单读不通」。
    ///
    /// <b>最后那一档是这一族最容易假绿的地方</b>：名单坏掉时它读回来是<b>一份空名单</b>
    /// （<see cref="LifeModeIndex.Load"/> 立起 <see cref="LifeModeIndex.Unreadable"/>），
    /// 于是「空」那一档天然成立，界面上写「还没有生活模式」自检照样绿 —— 而屏幕上那句话
    /// 把一份坏文件说成了「你没存过生活模式」。所以「读不通」是<b>问出来的一档</b>，不是空出来的。
    ///
    /// <b>三档是从「比」里来的，没有第二份真相</b>：<see cref="LifeModeStore.StandingOf"/> 拿
    /// <b>窗里这一份</b>（不是磁盘上那份，也不是一个自己维护的脏标志）和每个生活模式那一份
    /// 逐键比一遍 —— 所以在窗里拧一个数就立刻变成「不在任何生活模式里」，而那半句正是
    /// 「界面状态不进配置」：它一个字节都没写进 <c>settings.jsonc</c>。
    ///
    /// 「删掉这一条」灰不灰也在这里问出来（问的是同一件事：现在站着的那一个是谁），
    /// 不在别处再存一份「当前是哪一个」。</summary>
    private void RefreshLifeModeWhere()
    {
        if (_modeButton == null) return;
        var spot = LifeModeStore.StandingOf(DataDir, _copy, _modeIndex);
        _modeButton.Text = spot.At switch
        {
            LifeModeStore.Standing.None => Tr("还没有生活模式"),
            LifeModeStore.Standing.In => Tr("生活模式：{0}").Replace("{0}", spot.Name ?? ""),
            LifeModeStore.Standing.ListUnreadable => Tr("生活模式名单读不通"),
            _ => Tr("不在任何生活模式里"),
        };
        if (_deleteItem >= 0)
            _modeButton.GetPopup().SetItemDisabled(_deleteItem, spot.At != LifeModeStore.Standing.In);
    }

    /// <summary>「名单读不通」那一句，<b>一开窗就在</b>（<see cref="RefreshLifeModeMenu"/> 那一处
    /// 摆的，同 <see cref="ShowDocError"/> 的理由：读者在按下保存之前）。
    ///
    /// 它说的是<b>名单</b>，不是「整个配置坏了」：活的那一份这时读得通，配置好好的，
    /// 坏的只有生活模式这一族 —— 而那一族<b>当成没有</b>（菜单上半空着）。**磁盘上那一份一个
    /// 字节都不动**，这一句里也说了：人得自己先把它修好。</summary>
    private string ListUnreadableSaid() =>
        Tr("生活模式名单读不通，一个生活模式也没认出来；磁盘上那一份没有动过");

    /// <summary>切换没成时状态栏摆的那一句。<b>四档各有各的一句</b>（设计文档 `docs/design/life-mode.md`「读不通有三档，各说各的」那一节），
    /// 话由界面说 —— <see cref="LifeModeStore.Switched.Detail"/> 是给日志的那一句，
    /// 它带着<b>路径</b>，而设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那条不许它上屏幕
    /// （<see cref="ScreenSelfTest"/> 那一族）。
    ///
    /// <b>第二档点名，第三档不点名</b>：读不通的是**哪一个生活模式**要说清（说清名字才找得到
    /// 坏在哪儿）；而「名字在名单里、文件不在」说的是<b>名单和磁盘对不上</b>，那一档同样要点
    /// 那个名字，但说的是另一件事 —— <b>不静悄悄补一个默认</b>，所以那半句也写在屏幕上。
    /// 四句互不相同，自检里对着它们各判一次（<see cref="LifeModeUnreadableSelfTest"/>）。</summary>
    private string SwitchWhy(LifeModeStore.Switched done, string name) => done.Stopped switch
    {
        LifeModeStore.Stop.ListUnreadable => ListUnreadableSaid(),
        LifeModeStore.Stop.LifeModeUnreadable => Fill(Tr("生活模式「{0}」读不通，所以没有切"), name),
        LifeModeStore.Stop.NoFile => Fill(Tr("名单里有「{0}」，可它那一份已经不在了 —— 没有替它补上一个"), name),
        _ => Fill(Tr("名单里没有「{0}」，所以没有切"), name),
    };

    /// 语言表里带 <c>{0}</c> 的那一条，把那个洞填上。<b>不另设一份格式化</b>。</summary>
    private static string Fill(string template, string name) => template.Replace("{0}", name);

    /// 测量程序和菜单都走这一条：起名那扇小窗开在设置窗正中间。回车和确定都收在
    /// <see cref="AcceptLifeModeName"/> 那一处。<paramref name="why"/> 决定它这一趟问什么：
    /// 起一个名、盖回哪一个、还是把哪一个删掉。</summary>
    public void AskLifeModeName() => AskLifeModeName(NameAsk.Save);

    /// <summary>测量程序那一发：把「收一个生活模式进来」<b>问名字的那一档</b>开起来，抓的是那一帧
    /// （工单 05 的收工条件之一）。走的是 <see cref="TakeInFrom"/> 那同一条路 —— 只是把
    /// 「文件框交路径」这一步替掉成测量程序递进来的那个（真鼠标点那个文件框是 OS 那一层，
    /// 自检里和抓帧都量不到，同「只有真菜单才走得到的那半步」那一族）。</summary>
    public void AskLifeModeNameForTakeIn(string source)
    {
        _takeInFrom = source;
        AskLifeModeName(NameAsk.TakeIn);
    }

    private void AskLifeModeName(NameAsk why)
    {
        _asking = why;
        var prompt = GetNode<Window>(NamePath);
        var text = GetNode<LineEdit>(NameTextPath);
        var err = GetNode<Label>(NameErrPath);
        // 预填的是**当前**那一个（现问，不记）：最常见的一路是「按一下就行」，
        // 而一个自己记着的名字就是第二份真相 —— 它会和你刚切走的那一条对不上。
        var spot = LifeModeStore.StandingOf(DataDir, _copy, _modeIndex);
        var here = spot.At == LifeModeStore.Standing.In ? spot.Name : null;
        switch (why)
        {
            case NameAsk.Delete:
                // 删掉撤不回来，所以这一档把「会发生什么」摆在小窗里，而且**没有输入格**：
                // 名字现问（<see cref="ForgetLifeMode"/>）—— 摆一份自己记着的名字，
                // 用户看见的和真删掉的可能不是同一个。
                err.Text = here == null
                    ? Tr("你不在任何生活模式里，没有可删的")
                    : $"{Tr("删掉以后它就不在了，它的编号不会拿去给下一个新生活模式用")}「{here}」";
                err.Visible = true;
                text.Visible = false;
                break;
            case NameAsk.TakeIn:
                // 收进来的这一档**没有预填**：这一趟要起的是一个新名字，而眼下站着的那一个
                // （`here`）不是它的名字 —— 预填它等于「收进来就变成你现在这个生活模式」。
                err.Visible = false;
                text.Visible = true;
                text.Text = "";
                break;
            default:
                err.Visible = false;
                text.Visible = true;
                text.Text = why == NameAsk.Overwrite ? here ?? "" : "";
                break;
        }
        PromptTexts();
        // 自己摆位置而不是 PopupCentered：那一发把窗摆在**屏**正中间，而这一扇是跟着设置窗
        // 走的（和 _Ready 里摆设置窗自己同一个理由：引擎给的位置是 (0,0)）。
        //
        // **坐标是屏幕那个原点，不是设置窗客户区** —— 2026-10-01 拿客户区那个原点写过一次，
        // 量到的屏上矩形是 (167,292)-(633,503)：整扇小窗落在设置窗左边那一块空地上，而实测结果
        // 「可见=True 句柄=3」仍然是绿的。**可见不等于在屏幕上那个位置上看得见**
        // （CODING_STANDARDS.md 第 2 条那一族）。改这一行之前先去看 App 那一发印的
        // 「屏上矩形」那一句。
        prompt.Position = Position + new Vector2I((Size.X - prompt.Size.X) / 2, (Size.Y - prompt.Size.Y) / 2);
        prompt.Popup();
        if (text.Visible)
        {
            text.GrabFocus();
            text.SelectAll();     // 预填的那一个按一下就换掉，不用先删干净
        }
        Log("options", $"起名那扇小窗（{why}）：屏上 {prompt.Position} 大小 {prompt.Size}，" +
                       $"弹出之后可见={prompt.Visible} 句柄={prompt.GetWindowId()}");
    }

    /// 小窗上那几处字：标题、占位字、确定那一颗。**跟着这一趟要干什么而变**
    /// （<see cref="_asking"/>），所以换语言时也要重摆一遍（<see cref="Texts"/> 调它）。</summary>
    private void PromptTexts()
    {
        var prompt = GetNodeOrNull<Window>(NamePath);
        if (prompt == null) return;
        prompt.Title = Tr(_asking switch
        {
            NameAsk.Overwrite => "盖回哪个生活模式",
            NameAsk.Delete => "删掉这个生活模式",
            _ => "给这个生活模式起个名",
        });
        GetNode<LineEdit>(NameTextPath).PlaceholderText = Tr("生活模式名");
        GetNode<Button>(NameOkPath).Text = Tr(_asking switch
        {
            NameAsk.Overwrite => "盖回去",
            NameAsk.Delete => "删掉",
            // 这一颗按下去是「收进来」，不是「存」—— 存的是窗里这一份，收的是刚才挑的那一份。
            NameAsk.TakeIn => "收进来",
            _ => "确定",
        });
        GetNode<Button>(NameCancelPath).Text = Tr("取消");
    }

    private void AcceptLifeModeName()
    {
        switch (_asking)
        {
            case NameAsk.Overwrite: CoverInto(GetNode<LineEdit>(NameTextPath).Text); return;
            case NameAsk.Delete: ForgetLifeMode(); return;
            case NameAsk.TakeIn: TakeInFrom(GetNode<LineEdit>(NameTextPath).Text); return;
            default: SaveNew(GetNode<LineEdit>(NameTextPath).Text); return;
        }
    }

    /// <summary>「从一份文件收一个生活模式进来…」：先开文件框挑那一份，挑中了再问名字。
    ///
    /// **次序是「先挑文件、后起名」**，理由和 <see cref="TakeInFrom"/> 一样：名字是**那一份**的
    /// 名字，文件还没挑就没有可起名的对象（反过来就是先逼你起一个名，再让你反悔换一个文件）。
    /// 挑中的那个路径只是<b>界面状态</b>，活到那一扇小窗关掉为止（<see cref="_takeInFrom"/>）——
    /// 它不落盘，也没有第二份副本。
    ///
    /// 扩展名列表和「导入…」那一颗是<b>同一份</b>：收进来的和导出去的是同一种文档，
    /// 而四个文件框入口都不许列全部文件（见 <see cref="Pick"/>）。</summary>
    private void AskTakeInFrom() =>
        Pick(false, "", path =>
        {
            _takeInFrom = path;
            AskLifeModeName(NameAsk.TakeIn);
        }, ["*.jsonc;*.json"]);

    /// <summary>把刚才挑的那一份<b>原样</b>收成一个新生活模式（<see cref="LifeModeStore.TakeIn"/>）。
    ///
    /// <b>收进来的那一刻不顶掉你现在这份配置</b>：这一路<b>只碰 <c>flows/</c> 底下那两样</b>，
    /// 活的那一份（<c>settings.jsonc</c>）一个字节都不动，窗里这一份也不动 —— 所以窗头那颗钮
    /// 还写着原来那个生活模式，而「切过去」是菜单上半另一个动作（<see cref="SwitchLifeMode"/>）。
    /// 这正是这一族和「导入…」分开的全部理由：导入是<b>整个换成活的</b>，收进来是<b>收着</b>。
    ///
    /// 没成的三档和 <see cref="SaveNew"/> 一样由界面说（<see cref="LifeModeStore.Verdict"/>）：
    /// 名字没过、名单读不通、那一份读不通，各说各的；<b>重名那一档指向「盖回」</b>，而判重
    /// 住在 <see cref="LifeModeStore"/> 那一层（判两遍就是两份，两份里过期的那一份不响）。
    /// 没成时<b>小窗不关</b>，理由就摆在小窗里。</summary>
    private void TakeInFrom(string wanted)
    {
        var source = _takeInFrom;
        if (source == null)
        {
            // 到不了这一格（文件框没交路径就不会来），真到了就照实说，而不是默默收别的什么。
            var none = Tr("还没有挑要收进来的那一份文件");
            Say(none, true);
            Log("options", $"收生活模式「{wanted}」没成：{none}");
            return;
        }
        var took = LifeModeStore.TakeIn(DataDir, wanted, source);
        if (took.Error != null)
        {
            var text = took.Collided
                ? $"{took.Error} —— {Tr("要盖回去就按「盖回一个生活模式…」")}"
                : took.Error;
            var why = GetNode<Label>(NameErrPath);
            why.Text = text;
            why.Visible = true;
            Say(text, true);
            Log("options", $"收生活模式「{wanted}」没成（{source}）：{took.Error}；磁盘和界面都没动");
            return;
        }
        GetNode<Window>(NamePath).Hide();
        _takeInFrom = null;
        RefreshLifeModeMenu();
        // 那半句是这一档的**行为**（不是「切过去了」）：现在站着的那一个没变，你什么时候切是
        // 另一件事。同 <see cref="UnsavedNote"/>，没有可说的就不加。
        Say($"{Tr("已收下生活模式")}「{wanted}」，{Tr("你现在这份配置没动过")}", false);
        Log("options", $"收生活模式「{wanted}」：{source} 逐字节搬进 {LifeModeStore.PathOf(DataDir, took.Taken!)}，" +
                       "活的那一份和窗里这一份都没动，切不切是另一个动作");
    }

    /// 存为一个新生活模式。存的是<b>窗里这一份</b>（<see cref="_copy"/>）——
    /// 「我现在在哪个生活模式」也是拿它比出来的（<see cref="LifeModeStore.StandingOf"/>），存另一份
    /// 的话刚存完那颗钮上就写着「不在任何生活模式里」，而那说的是「改动」，不是「你在哪儿」。
    ///
    /// 名字没过时**小窗不关**、理由就摆在小窗里：关掉再让人从底下那一行红色小字里找理由，
    /// 等于让人自己记住刚才是哪个键。</summary>
    private void SaveNew(string wanted)
    {
        var lost = Settings.Diff(Live, _copy);
        var saved = LifeModeStore.SaveAs(DataDir, wanted, _copy);
        if (saved.Error != null)
        {
            // **重名那一档指向「盖回」**，而「盖回」那一项在菜单下半。判重不在这里 ——
            // 它住在 LifeModeStore（<see cref="LifeModeStore.Verdict.Taken"/>），因为收一个生活模式
            // 进来（工单 05）走的是这一族的另一个入口，那一侧要说的是同一句。
            var text = saved.Collided
                ? $"{saved.Error} —— {Tr("要盖回去就按「盖回一个生活模式…」")}"
                : saved.Error;
            var why = GetNode<Label>(NameErrPath);
            why.Text = text;
            why.Visible = true;
            Say(text, true);
            Log("options", $"存生活模式「{wanted}」没成：{saved.Error}");
            return;
        }
        GetNode<Window>(NamePath).Hide();
        RefreshLifeModeMenu();
        Say($"{Tr("已存为新生活模式")}「{wanted}」" + UnsavedNote(lost), false);
        Log("options", $"存生活模式「{wanted}」：写进了 {LifeModeStore.DirOf(DataDir)}，" +
                       $"改动 {(lost.Length == 0 ? "没有" : lost)}");
    }

    /// 盖回一个**已经有的**生活模式：写进<b>同一个文件</b>，名单上那一条和它的编号都不动
    /// （<see cref="LifeModeStore.Overwrite"/>）。名字不在名单里就是没有 —— 这一路不新发一条。</summary>
    private void CoverInto(string wanted)
    {
        var over = LifeModeStore.Overwrite(DataDir, wanted, _copy);
        if (over.Error != null)
        {
            var why = GetNode<Label>(NameErrPath);
            why.Text = over.Error;
            why.Visible = true;
            Say(over.Error, true);
            Log("options", $"盖回生活模式「{wanted}」没成：{over.Error}");
            return;
        }
        GetNode<Window>(NamePath).Hide();
        RefreshLifeModeMenu();
        Say($"{Tr("已盖回生活模式")}「{wanted}」", false);
        Log("options", $"盖回生活模式「{wanted}」：写进了 {LifeModeStore.PathOf(DataDir, over.Taken!)}，" +
                       "名单和编号都没动");
    }

    /// 删掉当前那个生活模式。<b>编号不回收</b>：下一个新生活模式拿更大的号（设计文档 `docs/design/life-mode.md`「名单是一份索引」那一节：
    /// 文件名由索引分配递增号，且永不复用。实施在 <see cref="LifeModeStore.Delete"/>，那里数的是「目录里
    /// 还在的那几份」）。名字**现问**而不是用小窗里那份 —— 摆一份自己记着的名字，
    /// 用户看见的和真删掉的可能不是同一个。</summary>
    private void ForgetLifeMode()
    {
        var spot = LifeModeStore.StandingOf(DataDir, _copy, _modeIndex);
        if (spot.At != LifeModeStore.Standing.In || spot.Name == null)
        {
            // 菜单里那一条这时候是灰的（RefreshLifeModeWhere 问的是同一件事），到不了这一格；
            // 真到了就照实说，而不是默默删掉别的什么。
            var why = Tr("你不在任何生活模式里，没有可删的");
            Say(why, true);
            Log("options", $"没有可删的：{why}");
            return;
        }
        var (error, _, left) = LifeModeStore.Delete(DataDir, spot.Name);
        if (error != null)
        {
            Say($"{Tr("删掉没成")}：{error}", true);
            Log("options", $"删掉生活模式「{spot.Name}」没成，名单和磁盘都没动：{error}");
            return;
        }
        GetNode<Window>(NamePath).Hide();
        RefreshLifeModeMenu();
        Say(left == null
                ? $"{Tr("已删掉生活模式")}「{spot.Name}」"
                : $"{Tr("已从名单里删掉")}「{spot.Name}」—— {left}", left != null);
        Log("options", $"删掉生活模式「{spot.Name}」：{(left == null ? "磁盘上那份也走了" : left)}，" +
                       "它的编号不再发出去");
    }

    /// 「切换」：把窗里没保存的改动作废，切过去，并在状态栏<b>说一声丢了什么</b>
    /// （设计文档 `docs/design/life-mode.md`「切换有两个动作」那一条的前半句）。**这一档不许偷偷先存** ——
    /// 要先存的是「保存并切换」，那是另一个动作。</summary>
    private void SwitchLifeMode(string name)
    {
        var lost = Settings.Diff(Live, _copy);
        var done = LifeModeStore.SwitchTo(DataDir, State, name);
        if (!done.Ok)
        {
            Say(SwitchWhy(done, name), true);
            Log("options", $"切换到生活模式「{name}」没成（{done.Stopped}），磁盘和界面都没动：{done.Detail}");
            return;
        }
        AdoptRead(done.State!);
        Say($"{Tr("已切到生活模式")}「{name}」" + UnsavedNote(lost), false);
        Log("options", $"切换到生活模式「{name}」：被换掉的那一份留在 {SettingsStore.BackupPath(DataDir)}，" +
                       $"窗里没保存的改动 {(lost.Length == 0 ? "没有" : lost)}");
    }

    /// 「保存并切换」：先把窗里这一份<b>按保存</b>落到磁盘再切（契约同一条的后半句）。
    /// <b>保存不成就说清为什么并且不切</b> —— 比如磁盘上那份读不通（<see cref="State.Error"/>），
    /// 那时候 Save 拒绝落笔，而切过去是拿写好的文档换的：先切等于替用户把那份文档盖掉。</summary>
    private void SaveAndSwitch(string name)
    {
        var why = SaveNow();
        if (why != null)
        {
            Say($"{Tr("保存没成，所以没有切")}：{why}", true);
            Log("options", $"保存并切换到生活模式「{name}」：保存没成，磁盘和界面都没动：{why}");
            return;
        }
        var done = LifeModeStore.SwitchTo(DataDir, State, name);
        if (!done.Ok)
        {
            Say(SwitchWhy(done, name), true);
            Log("options", $"保存成、切到生活模式「{name}」没成（{done.Stopped}），界面停在刚存的那一份上：{done.Detail}");
            return;
        }
        AdoptRead(done.State!);
        Say($"{Tr("已保存并切到生活模式")}「{name}」", false);
        Log("options", $"保存并切换到生活模式「{name}」：窗里这一份先按保存落了盘，" +
                       $"被换掉的那一份留在 {SettingsStore.BackupPath(DataDir)}");
    }

    /// 窗里那一份没保存的改动没算进去时补的那半句。空的那半句什么都不加 ——
    /// 没有改动可说的时候说「改动没算进去」是句废话。</summary>
    private string UnsavedNote(string lost) =>
        lost.Length == 0 ? "" : $"（{Tr("窗里没保存的改动没有算进去")}）";

    /// 此刻**在屏幕上**的行数：问的是这一行在不在可见的树上（<c>IsVisibleInTree</c>），
    /// 不是它自己那颗 <c>Visible</c>。区别在域切换和搜索之后立刻显形 —— 分区被收掉时
    /// 分区里每一行自己的 <c>Visible</c> 仍是 true，于是只数自己会一直报 44，
    /// 读起来像「切域没生效」。这一发就是被那个实测结果带偏的。
    private int VisibleRows() => _rows.Count(r => r.Value?.GetParent() is Control c && c.IsVisibleInTree());

    /// 自检里问的那个：这一行此刻在不在屏幕上。走的是 <c>IsVisibleInTree</c>，不是行自己的
    /// <c>Visible</c>，也不是复算一遍 <see cref="RowMatches"/> —— 前者在域切换后会误报
    /// （分区收掉了、分区里每行自己还是 visible），后者问的就不是「窗现在什么样」
    /// 而是「窗该怎么样」，两者不一致时恰好会互相掩护。
    private bool RowShown(string key) =>
        _byKey.TryGetValue(key, out var row) && row.Value?.GetParent() is Control c && c.IsVisibleInTree();

    /// <summary>自检里问的那个：这个分区此刻在不在屏幕上。走的是卡片自己的 <c>Visible</c>，
    /// 不是复算一遍 <see cref="ApplyVisibility"/> 的条件 —— 后者问的是「窗该怎么样」，
    /// 两者不一致时恰好会互相掩护（域切换之后最明显）。</summary>
    private bool SectionShown(string node) => Card(this, node)?.Visible ?? false;

    /// <summary>自检里问的那个：这一区此刻是不是一张<b>真的卡片</b>在屏幕上。
    /// 走的是 <c>IsVisibleInTree</c>，理由和 <see cref="RowShown"/> 那条一样：收掉的那一区
    /// 卡片自己那颗 <c>Visible</c> 仍然是 true。
    ///
    /// 这一条替掉的是原来那个 <c>RuleShown</c>（顶上那一区该不该带分割线）。分割线随卡片
    /// 一起退休了 —— 卡片自己带边框，间距由 <c>Form</c> 的 separation 给，所以「区与区
    /// 之间要看得见」这一条现在由 <b>卡片在不在树上</b> 来量。</summary>
    public bool CardShown(string node) =>
        Card(this, node) is { } card && card.IsVisibleInTree();

    /// 此刻界面上**一共有多少行**（不看在不在屏幕上）—— 自检里「全在场」那条的基线。
    ///
    /// <b>不写死条数</b>：写死的那一份就是会腐烂的副本（`CODING_STANDARDS.md` 取证方法论
    /// 第 6 条），加一条键之后它就在量一个已经不是产品的数。这里现问。
    private int RowCount => _rows.Count;

    /// 界面上那些行的键名。自检里逐条问「你在不在屏幕上」时用 —— 同样不手抄：
    /// 手抄的清单漏了新加的那条，那一组照样绿，而它该报的正是「漏了」。
    public List<string> RowKeys() => _rows.Select(r => r.Key).ToList();

    /// 某一行挂在哪张卡片下面（分区名）。自检逐格量「那一格名下有多少行」时用：
    /// 它按行节点反查，不看 <see cref="Domains"/> 那张表 —— 两份各自独立才对得上账。
    public string SectionOfKey(string key)
    {
        var row = _rows.FirstOrDefault(r => r.Key == key);
        return row?.Value?.GetParent() is Control control ? SectionOf(control) : "";
    }

    /// 这几个分区里一共有着多少<b>常规</b>行（不看在不在屏幕上）。
    ///
    /// **2026-10-07 这一族退休了**：它原来只服务 <see cref="Audit"/> 第 17 条
    /// （一区不许按「整张全高级」设计，否则界面上是一张空卡片），而那条判据的前提是
    /// 「显示高级设置」那颗开关还在、默认收着 28 条。那颗开关 2026-10-06 退休、
    /// 2026-10-07 确认不回来，可见性不再按档位筛（<see cref="ApplyVisibility"/>），
    /// 「整张全高级」也不再是任何可见性的原因 —— 遮罩那一卡全部行都是 <c>advanced</c>，
    /// 现在照样整张摆着。留着它就是留一个量着已经不存在的那件事的函数。
    private int CommonInDomain(string[] sections) => _rows.Count(r =>
        !IsAdvanced(r) && r.Value?.GetParent() is Control c && sections.Contains(SectionOf(c)));

    /// 自检里那句话里的「命中在哪一栏」。
    private string MatchWhy(string key, string query) =>
        _byKey.TryGetValue(key, out var row) ? MatchWhere(row, query) : "没有这一行";

    /// 那一行<b>屏幕上那个标签</b>的字（<see cref="MatchWhere"/> 拿来匹配的「标签」那一栏）。
    /// 自检里当搜索词用 —— 见 <see cref="NavSelfTest"/> 里那一发：手写死的词是判据自己存的
    /// 一份副本，标签改过字它就悄悄量起「没人搜的词」来。
    public string LabelOf(string key) =>
        _byKey.TryGetValue(key, out var row) ? row.Name.Text : "";


    /// 单位那一列：没有这一条就不写（比如没有单位的那个 flow 名）。
    /// 带数的一律走 <see cref="TrCounted"/> 那一条规则（<c>activityTimeoutMin.unit</c>
    /// 就是这样的一条：英文写成 <c>minutes</c>，数是 1 的时候要 <c>minute</c>）。
    private string Unit(Row row, string gap)
    {
        var key = row.Key + ".unit";
        if (key == Tr(key)) return "";
        var value = _copy.Map.Get(row.Key);
        var count = value is int i ? (long)i : value is long l ? l : 2;   // 数不出来的按「复数」那一档
        return gap + TrCounted(key, count);
    }

    private double _sinceStatus;

    /// 控件往副本回写了几次。<see cref="Load(Row)"/> 那一条路上它该一动不动 ——
    /// 自检里数这一发（界面上不印东西，免得把日志灌满）。
    private int _writeBacks;

    /// 有几格「显示的字」和「它自己的值」不一致。屏幕上看到的就是这个差。
    /// 自检里那一发问的是它（逐帧的临时测量程序已经撤掉：它给的实测结果是「灌完之后 6 帧全 0」，
    /// 也就是说旧数不在之字形上，是按下那一瞬引擎把越界的输入夹回来画的一帧）。
    ///
    /// <b>按数比，不按字比</b>：时间格上那个数可以是 1.5（1.5 分钟），把它截成 int 再
    /// ToString 出来就是「1」，于是每一格时间都会报一条假的。屏幕上看到的是那行字，
    /// 而它和值相等的意思是「同一个数」，不是「同一串字符」。
    private List<string> StaleCells()
    {
        var stale = new List<string>();
        foreach (var row in _rows.Where(r => r.Value is SpinBox))
        {
            var box = (SpinBox)row.Value;
            var shown = box.GetLineEdit().Text;
            if (!double.TryParse(shown, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var text))
                stale.Add($"{row.Key}：那一格里的字 '{shown}' 不是数");
            else if (Math.Abs(text - box.Value) > 0.001)
                stale.Add($"{row.Key}：值 {box.Value} 字 '{shown}'");
        }
        return stale;
    }

    /// 抬头那一行是在走的数，没人碰窗口的时候也得走：一秒重算一次。
    /// 只刷那一行 —— 副本和控件都不在这条路上，所以刷不出「改了没保存」的错觉。
    /// 顺带刷勿扰那一格的「当前前台」实测结果：用户切到别的程序再切回来，那一行要跟着换
    /// （那正是它存在的理由 —— 照着完整路径把 exe 名写进名单）。
    public override void _Process(double delta)
    {
        _sinceStatus += delta;
        if (_sinceStatus < 1.0) return;
        _sinceStatus = 0;
        Status();
        if (_dndForeground != null) RefreshDndForeground();
    }

    private void Status()
    {
        var (next, records) = Measurement();
        // 「距下次淡入」也是一段时间，走同一条显示口径（<c>Settings.Spell</c>）：
        // 够一分钟就写分钟，不够就写秒。原来的 <c>{next:F1} 分钟</c> 会印出「0.8 分钟」
        // —— 一个界面上从来没有过的单位。
        //
        // 2026-10-06：中间那半「健康度 NN%」去掉了（所有者定夺「把健康度跟进度条移除」）。
        // 底下算它的那一份还留着（<c>History.HealthPercent</c>），记录照旧写 ——
        // 「界面不许有」是界面这一侧的事。中间那半删掉之后，这一行是两件事并排。
        var (number, unit, _) = Settings.Spell((long)Math.Round(next * 60_000));
        GetNode<Label>("Margin/Stack/Header/Status").Text =
            $"{Tr("距下次淡入")} {number:0.##} {TrCounted(unit, number)} · " +
            $"{Tr("休息记录")} {records}";
    }

    /// 窗口下面那一行：保存的结果、读不通的原因。红色只表示「这次没成」。
    private void Say(string text, bool bad)
    {
        var msg = GetNode<RichTextLabel>("Margin/Stack/Msg");
        msg.Text = bad ? $"[color=#c62828]{text}[/color]" : text;
        msg.Visible = text.Length > 0;
    }

    /// <summary>抬头那一行底下常驻的那句「磁盘上那份读不通」。<b>常驻</b>是这条要求的本体：
    /// 契约写的是（设计文档 `docs/design/config.md`「读的时候宽容，写的时候是编辑」那一节）「the Options window says what failed」
    /// 且「the app runs on defaults until the file reads again」—— 两句话的读者都在按下保存之前，所以它得
    /// 窗一开就看得见，不是等按下保存那一刻。这一边原来只有
    /// <see cref="Save"/> 里那一句，坏在那儿的时候人不知道坏了（设计文档 `docs/design/config.md`「解析失败是一个状态，不是崩溃」那条，
    /// 要的是「窗口说清哪里读不了」，没说什么时候才说）。</summary>
    private void ShowDocError()
    {
        var banner = GetNode<Label>("Margin/Stack/Header/Error");
        banner.Text = State?.Error == null ? "" : $"{Tr("磁盘上那份读不通")}：{State.Error}";
        banner.Visible = State?.Error != null;
    }

    // ---- 休息清单：它是记录，改一条就落盘 -----------------------------------------

    private void BuildChecklist()
    {
        var rows = GetNode<VBoxContainer>(ListPath + "/Rows");
        var template = rows.GetNode<Control>("Template");
        foreach (var child in rows.GetChildren().Where(c => c != template).ToList()) child.QueueFree();

        foreach (var todo in Checklist.Items)
        {
            var line = (HBoxContainer)template.Duplicate();
            line.Visible = true;
            var done = line.GetNode<CheckBox>("Done");
            done.SetPressedNoSignal(todo.Done);
            done.Toggled += on =>
            {
                todo.Done = on;
                SaveChecklist();
            };
            var text = line.GetNode<LineEdit>("Text");
            text.Text = todo.Text;
            text.TextChanged += written =>
            {
                // 把已有的一行删空 = 删掉那一条。这一条规矩滤两个地方：界面上这一行跟着没
                // （就是这里），落盘前再滤一次（<c>Todos.Save</c>）。
                // 所以这里只管把那一行收掉、重建。
                if (written.Trim().Length == 0)
                {
                    Checklist.Items.Remove(todo);
                    SaveChecklist();
                    BuildChecklist();
                    return;
                }
                todo.Text = written;
                SaveChecklist();
            };
            var del = line.GetNode<Button>("Del");
            del.Text = Tr("删除");
            del.Pressed += () =>
            {
                Checklist.Items.Remove(todo);
                SaveChecklist();
                BuildChecklist();
            };
            rows.AddChild(line);
        }
    }

    private void SaveChecklist()
    {
        ChecklistChanged();
        Log("options", $"清单落盘：{Checklist.Items.Count} 条");
    }

    /// 测量程序用的那两条：清单那几行（那一块模板不算）和其中第 n 行。
    /// 行是 <see cref="BuildChecklist"/> 从模板 Duplicate 出来的，名字是引擎自取的，所以按父子找不按名字。
    /// <b>正在 QueueFree 的那些要剔掉</b>：重搭那一手是「先排队删、再加新的」，所以删一行之后
    /// GetChildren 一次会数到 3 条（旧的两条还挂在树上等这一帧过完）—— 上一版就把这一发读成了
    /// 「该行没收掉」，那是测量程序读错，不是行为错。
    private static List<Control> ChecklistRows(Options win)
    {
        var box = win.GetNode<VBoxContainer>(ListPath + "/Rows");
        var template = box.GetNode<Control>("Template");
        return box.GetChildren().OfType<Control>()
            .Where(c => c != template && !c.IsQueuedForDeletion()).ToList();
    }

    private static Control ChecklistRow(Options win, int at) => ChecklistRows(win)[at];
    private static int ChecklistRowCount(Options win) => ChecklistRows(win).Count;

    // ---- flows 那张表：一条休息流程 = 一串 step ----------------------------------

    /// <summary>「哪一条在编辑」那一颗下拉、名字那一格、以及底下那几颗钮。
    /// 接一次线；<see cref="RefreshFlows"/> 和 <see cref="RebuildSteps"/> 只刷不接，
    /// 所以刷新一次不会多接一份处理器。</summary>
    private void BuildFlowEditor()
    {
        _picking = GetNode<OptionButton>(FlowsPath + "/Pick/Which");
        _naming = GetNode<LineEdit>(FlowsPath + "/Pick/Name");
        _flowBox = GetNode<VBoxContainer>(FlowsPath + "/Steps");
        _stepTemplate = _flowBox.GetNode<Control>("Template");
        _total = GetNode<Label>(FlowsPath + "/AddStep/Total");
        _picking.ItemSelected += i =>
        {
            if (_filling) return;
            var names = _copy.Flows.Keys.ToList();
            if ((int)i < names.Count) { _editing = names[(int)i]; RebuildSteps(); }
        };
        _naming.TextChanged += RenameTo;
        GetNode<Button>(FlowsPath + "/Pick/NewBtn").Pressed += NewFlow;
        GetNode<Button>(FlowsPath + "/Pick/DelBtn").Pressed += DeleteFlow;
        GetNode<Button>(FlowsPath + "/AddStep/Btn").Pressed += AddStep;
        // 正在编辑的那一条：跟着「走哪条休息流程」那一行开，它才是这个人关心的那一条。
        _editing = _copy.Flows.ContainsKey(_copy.Flow) ? _copy.Flow : "";
        RefreshFlows();
    }

    private const string FlowsPath = FormPath + "/Flows/Body";
    private const string RoutinePath = FormPath + "/Routine/Body";
    private const string DndPath = FormPath + "/Dnd/Body";
    private const string ListPath = FormPath + "/List/Body";
    private const string PreviewPath = FormPath + "/Fader/Body/Preview";

    /// 正在编辑的那一条休息流程；名字没了（删光了、或者文档里本来就没有）就是 null。
    private Flow? Edited =>
        _copy.Flows.TryGetValue(_editing, out var flow) ? flow : null;

    /// 名称那一列 + 名字那一格 + 步列表。程序往控件里灌值的时候立着 <c>_filling</c>：
    /// 不然 <c>TextChanged</c> 会把「把当前名字放回格子里」读成一次改名。
    private void RefreshFlows()
    {
        _filling = true;
        var names = _copy.Flows.Keys.ToList();
        if (!names.Contains(_editing)) _editing = names.FirstOrDefault() ?? "";
        _picking.Clear();
        foreach (var name in names) _picking.AddItem(name);   // 用户起的名字，不翻
        _picking.Select(names.IndexOf(_editing));             // 表空了就是 -1：那一格没有选中
        _naming.Text = _editing;
        _naming.Editable = _editing.Length > 0;
        _naming.PlaceholderText = Tr("这条叫什么");
        _filling = false;
        RebuildSteps();
        ReloadFlowChoices();
    }

    /// 每一步一叠控件，值从副本里那条休息流程上灌。改字段不回这张表（不然正在打的字会跳），
    /// 加、删、挪、换一条都回。
    private void RebuildSteps()
    {
        for (var i = _flowBox.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _flowBox.GetChild(i);
            if (child == _stepTemplate) continue;
            _flowBox.RemoveChild(child);
            // 当场 Free 会杀掉「正在发 Pressed 信号」的那一颗按钮 —— 引擎为此报了一条
            // Object was freed while a signal is being emitted。先 RemoveChild 已经让它
            // 离开树（画面和命中都不再有它），对象留到这一帧结束再死。
            child.QueueFree();
        }
        var flow = Edited;
        foreach (var (step, index) in flow == null
                     ? Array.Empty<(Step, int)>()
                     : flow.Steps.Select((s, i) => (s, i)))
            _flowBox.AddChild(StageRow(step, index, flow!.Steps.Count));
        ModeMessage();
    }

    /// 一叠 = 一步。字段名就是 <see cref="Settings.StepDeclarations"/> 里那几个：
    /// 范围、默认值、可选值都从声明取，写回也走 <see cref="Step.Set"/>，
    /// 所以这个文件里没有出现第二个「3600000」，也没有第二个「clock,trail,health」。
    private Control StageRow(Step step, int index, int count)
    {
        var line = (Control)_stepTemplate.Duplicate();
        line.Visible = true;
        var head = line.GetNode<HBoxContainer>("Head");
        head.GetNode<Label>("Which").Text = Counted("第 N 步", index + 1);

        // 每一步站多久也是一段时间，走和设置行同一条显示口径（契约：
        // 「休息流程编辑器每一步那个 ms 格走同一条路」）。档存在这个闭包里 —— 写回时
        // 那个数到底是秒还是分钟，只有上一次显示定下的那一档知道。
        var ms = head.GetNode<SpinBox>("Ms");
        var unit = head.GetNode<Label>("Unit");
        var decl = Settings.Decl(Step.KeyPrefix + "ms");
        var scale = 1;
        void Show(int value)
        {
            var (number, unitKey, s) = Settings.Spell(value);
            scale = s;
            ms.MinValue = decl.ControlMin(s);
            ms.SetValueNoSignal(number);
            ms.GetLineEdit().Text = Settings.SpellText(value);
            unit.Text = TrCounted(unitKey, number);
        }
        ms.MaxValue = int.MaxValue;   // 同一条：数字格不带上限，上限只在写回那一步
        ms.Step = 1;                   // 步长 1：屏幕上是「3」而不是「3.0」
        Show(step.Ms);
        ms.ValueChanged += moved => { step.Set("ms", Settings.Unspelled((long)Math.Round(moved), scale)); Show(step.Ms); ModeMessage(); };

        Flag(line, "Dismissible", "挥得散", step, "dismissible");
        Flag(line, "Interactive", "接鼠标", step, "interactive");
        Flag(line, "Sound", "放声音", step, "sound");
        LayoutButton(line, step, index);

        var up = head.GetNode<Button>("Up");
        up.Text = Tr("上移");
        up.Disabled = index == 0;
        up.Pressed += () => MoveStep(index, index - 1);
        var down = head.GetNode<Button>("Down");
        down.Text = Tr("下移");
        down.Disabled = index == count - 1;
        down.Pressed += () => MoveStep(index, index + 1);
        var del = head.GetNode<Button>("Del");
        del.Text = Tr("删掉这一步");
        del.Pressed += () =>
        {
            Edited?.Steps.Remove(step);
            RebuildSteps();
            Log("options", $"{_editing}：删掉第 {index + 1} 步（现在 {Edited?.Steps.Count} 步）");
        };
        return line;
    }

    private void Flag(Control line, string node, string label, Step step, string field)
    {
        var box = line.GetNode<CheckBox>($"Flags/{node}");
        box.Text = Tr(label);
        box.SetPressedNoSignal((bool)step.Get(field));
        // 勾掉一颗也是改「窗里这一份」：窗头那颗钮上「我在哪个生活模式」要跟着重新问一遍。
        box.Toggled += on => { step.Set(field, on); RefreshLifeModeWhere(); };
    }

    /// 这一步的**布局**：一排里只剩「摆这一屏…」那颗钮，按下去开编辑器窗（程序第五扇窗）。
    ///
    /// **原来这里是两样东西，2026-10-06 一起退休**：一排按 <c>steps[].content</c> 声明的可选值
    /// 现造的勾选框（<see cref="Contents"/>），和 <c>steps[].text</c> 那一行自由文本框
    /// （<see cref="Words"/>）。那两样现在都是**布局里控件**了 —— 内容不再是步上的一个名单，
    /// 「显示什么、在哪、多大」全在布局里，而布局要在自己的窗里拖。
    ///
    /// **那两个键在声明表里留着不撤**（<c>Settings.StepDeclarations</c>）：老配置里写着它们，
    /// 要读得进来、还要翻得出布局（<see cref="StepLayout.Translate"/>）。撤掉声明就是一份老文件
    /// 突然报错 —— 那不是退休一件东西，是砸掉别人的文件。但**界面上不再有它们的控件**：
    /// 声明留着是给读的那一份用的，不是给改的那一份用的。
    private void LayoutButton(Control line, Step step, int index)
    {
        line.GetNode<Label>("Show/Label").Text = Tr("这一步的屏");
        var lay = line.GetNode<Button>("Show/Lay");
        lay.Text = Tr("摆这一屏…");
        // 格子里的颗数是「这一步摆了几颗控件」—— 不是一行字，是一眼看得见的数量。
        lay.TooltipText = Tr("在编辑器里拖：决定这一步遮罩上有什么、在哪、多大");
        lay.Pressed += () => OpenLayoutEditor(step, index);
    }

    /// 开编辑器窗。已经开着就改它对着的那一步，不叠第二扇（用户按好几次只会以为程序卡了）。
    private void OpenLayoutEditor(Step step, int index)
    {
        var flow = Edited;
        // 「照抄上一步的布局」要拿到前一步 —— 第一步没有前一步，编辑器自己会说清。
        var previous = flow != null && index > 0 ? flow.Steps[index - 1] : null;
        if (_layoutEditor == null || !GodotObject.IsInstanceValid(_layoutEditor))
        {
            // 回调给的是「编辑器改完了」：编辑器改的是**窗里这一份副本**，
            // 和 `ms` / `interactive` 那几格同一种身份，所以走同一个
            // ModeMessage —— 底下那行实测结果和「我在哪个生活模式」都要跟着重新问一遍。
            _layoutEditor = LayoutEditor.Open(this, step, previous, _copy.UiTheme,
                (tag, msg) => Log("options", $"{tag}：{msg}"),
                () => { RebuildSteps(); ModeMessage(); });
            return;
        }
        _layoutEditor.Retarget(step, previous, $"{_editing} · {index + 1}/{flow?.Steps.Count}");
    }

    private LayoutEditor? _layoutEditor;

    private void MoveStep(int from, int to)
    {
        var steps = Edited?.Steps;
        if (steps == null || to < 0 || to >= steps.Count) return;
        (steps[from], steps[to]) = (steps[to], steps[from]);
        RebuildSteps();
    }

    private void AddStep()
    {
        var flow = Edited;
        if (flow == null) return;
        flow.Steps.Add(new Step());     // 默认值一条都不在这里写：Step 的构造从声明表取
        RebuildSteps();
        Log("options", $"{_editing}：加了一步（现在 {flow.Steps.Count} 步）");
    }

    /// 新增一条 = 把正在编辑的这条抄一份。抄而不是从空里造，是因为没人想从五格空白开始
    /// 配一条休息流程；抄完的名字带一个没人用过的序号，改名那一格马上就能改。
    private void NewFlow()
    {
        var name = FreeLifeModeName(Tr("新休息流程"));
        _copy.Flows[name] = Edited?.Clone() ?? new Flow { Steps = [new Step()] };
        _editing = name;
        RefreshFlows();
        Log("options", $"新增一条休息流程：{name}（抄自 {Edited?.Steps.Count ?? 0} 步的那一条）");
    }

    private string FreeLifeModeName(string baseName)
    {
        var taken = _copy.Flows.Keys.ToList();
        for (var n = 1; ; n++)
            if (!taken.Contains($"{baseName} {n}")) return $"{baseName} {n}";
    }

    private void DeleteFlow()
    {
        if (! _copy.Flows.ContainsKey(_editing)) return;
        var gone = _editing;
        _copy.Flows.Remove(gone);
        // 「走哪条休息流程」指着刚删的那一条：跟着挑一条还在的。放着不管的话它不报错，
        // 只是下一次淡入悄悄走了 fadetop —— 那正是 Settings.Steps() 的回落规矩。
        if (_copy.Flow == gone) _copy.Flow = _copy.Flows.Keys.FirstOrDefault() ?? Settings.FlowFadeTop;
        _editing = "";
        RefreshFlows();
        Log("options", $"删掉一条休息流程：{gone}（还剩 {_copy.Flows.Count} 条）");
    }

    /// <summary>改名：按每一次击键走（<c>TextChanged</c>），所以中间状态允许是空的或半截的 ——
    /// 空的那一格不改（把名字清空不像改名，像手滑），撞名的也不改，只说一声。
    /// 副本里换名字保留原来的位置；「走哪条」如果指的就是这一条，跟着一起改。</summary>
    private void RenameTo(string written)
    {
        if (_filling) return;
        var wanted = written.Trim();
        if (wanted.Length == 0 || wanted == _editing) return;
        if (_copy.Flows.ContainsKey(wanted))
        {
            Say($"{Tr("已经有这条休息流程")}：{wanted}", true);
            return;
        }
        var old = _editing;
        var renamed = new Dictionary<string, Flow>();
        foreach (var (name, flow) in _copy.Flows) renamed[name == old ? wanted : name] = flow;
        _copy.Flows = renamed;
        _editing = wanted;
        if (_copy.Flow == old) _copy.Flow = wanted;
        RefreshFlows();
        Status();
        Log("options", $"休息流程改名：{old} → {wanted}");
    }

    /// 「走哪条休息流程」那一行的可选值就是这张表的行名，所以表一变它要重新灌一次。
    private void ReloadFlowChoices()
    {
        if (!_byKey.TryGetValue("flow", out var row) || row.Value is not OptionButton option) return;
        Fill(option, row);
        SelectCurrent(option, row);
    }

    /// 底下那一行实测结果：几步、写到的时长一共多少。它顺手管「这一条一步都没有」那一档 ——
    /// 那是契约里合法的一种（读作 fadetop），界面得说而不是报错。
    private void ModeMessage()
    {
        // 休息流程那一族改的也是「窗里这一份」，所以那一档要跟着重新问一遍
        //（增删步、挪步、改步长都走这里）。
        RefreshLifeModeWhere();
        var flow = Edited;
        if (flow == null)
        {
            _total.Text = Tr("这张表里还没有一条休息流程");
            return;
        }
        var held = flow.Steps.Where(s => s.Ms > 0).Sum(s => (long)s.Ms);
        // 共多少时间也是一段时间，走同一条显示口径 —— 这里原来写死「毫秒」两个字。
        var (number, unit, _) = Settings.Spell(held);
        _total.Text = $"{Counted("共 N 步", flow.Steps.Count)} · {number:0.##} {TrCounted(unit, number)}" +
                      (flow.Steps.Count == 0 ? $"（{Tr("一步都没有，它就只淡进来")}）" : "");
    }

    /// 语言表里带 {n} 的那几条：数字写在表外面，两条语言栏各自决定它落在哪一边
    /// （zh「第 {n} 步」、en "Step {n}"）。和关于窗口那行「版本 {v}」是同一个写法。
    ///
    /// <b>数词和名词的搭配走 <see cref="TrCounted"/> 那个规则</b>，不在这里另判一遍。
    private string Counted(string key, long n) => TrCounted(key, n).Replace("{n}", n.ToString());

    /// <summary>数词和名词的搭配，<b>规则只有这一句</b>：<b>数是 1 就取 <c>&lt;键&gt;.one</c>
    /// 那一条</b>，否则取本条。
    ///
    /// 起因是抓英文帧量到的两处：休息流程那张卡片底下写着「<b>1 minutes</b>」，而步数那一句
    /// 写着「<b>Stretches</b>」还把 <c>{n}</c> 整个丢了（中文那一条是「共 {n} 步」，
    /// 英文那一条当初写成了光秃秃一个 <c>Stretches</c>）。两处都只有英文错 —— 中文没有单复数。
    ///
    /// <b>为什么不按语言分支</b>：那要在代码里问「现在是哪种语言」，而语言是会变的
    /// （运行期切语言免费，托盘菜单每次打开现造）。这条规则问的是<b>数</b>，不问语言 ——
    /// 中文那几行 <c>.one</c> 的文字和本行一样（<c>分钟</c> / <c>分钟</c>），
    /// 那不是「第二份真相」，是一条语言资源本来就有单复数两栏（英语要，中文不要）。
    ///
    /// <b>取不到 <c>.one</c> 就用本条</b>：这样加一条带数的新文案时不必记得再配一个单数。</summary>
    private static string TrCounted(string key, long n)
    {
        var one = key + ".one";
        return n == 1 && Lang.Keys().Contains(one)
            ? TranslationServer.Translate(one)
            : TranslationServer.Translate(key);
    }

    // ---- 作息表那一区：时间块编辑器 -----------------------------------------------
    //
    // 形状全在场景里（`Routine/Body/Blocks/Template`），这里只接线。**字段那一行是按
    // `Settings.BlockDeclarations` 一条一条摆的**，不是按一张手抄的清单 ——
    // 声明表里加一个字段，界面上多一行、自检里那条形状判据多查一处，三处都从同一份来。

    /// 下拉里那一格「空」：到点那一次走「这次休息走哪一条」指着的那一行
    /// （契约：没写就走…）。它是**我们自己的一条措辞**，所以语言表里有那一条 ——
    /// 拿空字符串冒充一个选项的话，屏上是一个点不开又看不出是什么的下拉格。
    private const string BlockFlowNone = "blocks[].flow.none";

    /// 块行里那个字段的那一格：<c>Fields/&lt;字段名&gt;</c>，字段名按场景里节点名的写法
    /// 首字母大写 —— 和 <see cref="ModeEditorShape"/> 里 <c>Flags/&lt;字段名&gt;</c> 同一处置。
    /// <b>只有这一处做这个换法</b>：场景、接线和自检里那三条都走它，字段名改了不用改三处。
    private static string BlockField(SettingDecl decl) =>
        $"Fields/{char.ToUpperInvariant(decl.Key[0])}{decl.Key[1..]}";

    /// 作息表编辑器上那些<b>不带声明</b>的词。字段那几行的标签不在这里 ——
    /// 它们的键是 <see cref="TimeBlock.KeyPrefix"/> + 字段名，从
    /// <see cref="Settings.BlockDeclarations"/> 派生（自检里那一条判据也是那么问的），
    /// 这里列的是**声明表推不出来**的那几个词。
    private static readonly string[] BlockEditorLabels =
    [
        "routine.hint", "第 N 块", "共 N 块", "上移", "下移", "删掉这一块",
        "加一个时间块", "还没有时间块", BlockFlowNone,
        "blocks[].unit.hour", "blocks[].unit.minute",
    ];

    /// 接线，不是造控件。范围、单位词、默认值、可选值全部从
    /// <see cref="Settings.BlockDeclarations"/> 取 —— 场景里一个数字都不写
    /// （<see cref="Audit"/> 第 12 条），时刻那两格的范围也是从声明里的
    /// 一天中的第几分钟换算出来的，不是抄一遍 0–1439。
    private void BuildBlockEditor()
    {
        _blockBox = GetNode<VBoxContainer>(RoutinePath + "/Blocks");
        _blockTemplate = _blockBox.GetNode<Control>("Template");
        _blockTotal = GetNode<Label>(RoutinePath + "/AddBlock/Total");
        GetNode<Button>(RoutinePath + "/AddBlock/Btn").Pressed += AddBlock;
        RefreshBlocks();
    }

    /// 一个时间块一叠控件。改字段不回这张表（不然正在打的字会跳），加、删、挪回 ——
    /// 和休息流程那一区 <see cref="RebuildSteps"/> 同一处置。
    private void RefreshBlocks()
    {
        for (var i = _blockBox.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _blockBox.GetChild(i);
            if (child == _blockTemplate) continue;
            _blockBox.RemoveChild(child);
            // 当场 Free 会杀掉「正在发 Pressed 信号」的那一颗按钮 —— 引擎为此报了一条
            // Object was freed while a signal is being emitted。先 RemoveChild 已经让它
            // 离开树，对象留到这一帧结束再死。
            child.QueueFree();
        }
        var count = _copy.Blocks.Count;
        for (var index = 0; index < count; index++)
            _blockBox.AddChild(BlockRow(_copy.Blocks[index], index, count));
        BlockMessage();
    }

    /// 一叠 = 一个时间块。字段名就是 <see cref="Settings.BlockDeclarations"/> 里那几个，
    /// 标签从语言表按<b>同一个前缀</b>取（<see cref="TimeBlock.KeyPrefix"/> + 字段名），
    /// 所以这一段里没有一个字段名是写死的。
    private Control BlockRow(TimeBlock block, int index, int count)
    {
        var line = (Control)_blockTemplate.Duplicate();
        line.Visible = true;
        var head = line.GetNode<HBoxContainer>("Head");
        head.GetNode<Label>("Which").Text = Counted("第 N 块", index + 1);

        foreach (var decl in Settings.BlockDeclarations)
        {
            var row = line.GetNode<HBoxContainer>(BlockField(decl));
            row.GetNode<Label>("Tag").Text = Tr(TimeBlock.KeyPrefix + decl.Key);
            switch (decl.Kind)
            {
                case SettingKind.Int: BlockClock(row, block, decl); break;
                case SettingKind.Bool: BlockFlag(row, block, decl); break;
                default: BlockText(row, block, decl); break;
            }
        }

        var up = head.GetNode<Button>("Up");
        up.Text = Tr("上移");
        up.Disabled = index == 0;
        up.Pressed += () => MoveBlock(index, index - 1);
        var down = head.GetNode<Button>("Down");
        down.Text = Tr("下移");
        down.Disabled = index == count - 1;
        down.Pressed += () => MoveBlock(index, index + 1);
        var del = head.GetNode<Button>("Del");
        del.Text = Tr("删掉这一块");
        del.Pressed += () =>
        {
            _copy.Blocks.Remove(block);
            RefreshBlocks();
            Log("options", $"作息表：删掉第 {index + 1} 块（现在 {_copy.Blocks.Count} 块）");
        };
        return line;
    }

    /// 一个时刻字段 = 「几点」+「几分」两格，<b>不是一天里的第几分钟</b>：那个数是内部表示
    /// （文档里存的是它），摆到界面上要人自己做除法，而这一族在屏幕上只有两种单位 ——
    /// 时刻和分钟（设计文档 `docs/design/config.md`「界面上不出现毫秒」那一条的时间那一半）。
    ///
    /// 范围从声明里那个「一天中的第几分钟」换算到两格（<see cref="Routine.HourOf"/> /
    /// <see cref="Routine.MinuteOf"/>），合起来走 <see cref="Routine.At"/> ——
    /// 60 只在 <see cref="Routine"/> 那儿写一次，这里一个数都不重抄。
    private void BlockClock(HBoxContainer row, TimeBlock block, SettingDecl decl)
    {
        var hour = row.GetNode<SpinBox>("Hour");
        var minute = row.GetNode<SpinBox>("Minute");
        hour.MinValue = Routine.HourOf(decl.Min);
        hour.MaxValue = Routine.HourOf(decl.Max);
        minute.MinValue = Routine.MinuteOf(decl.Min);
        minute.MaxValue = Routine.MinuteOf(decl.Max);
        hour.Step = 1;
        minute.Step = 1;     // 步长 1：屏幕上是「22」而不是「22.0」
        // 单位词：这一族在屏幕上只有两种单位（「点」和「分」），两条词在语言表里各写一次。
        row.GetNode<Label>("HourUnit").Text = Tr("blocks[].unit.hour");
        row.GetNode<Label>("MinuteUnit").Text = Tr("blocks[].unit.minute");
        // 灌值和写回**分成两步**：开局那一次只灌不写 —— 两个数格这时候还是场景里的 0–100、
        // 值是 0，先写就把这一块存着的时刻整个抹成 00:00 了。
        void Show()
        {
            var stored = (int)block.Get(decl.Key);
            hour.SetValueNoSignal(Routine.HourOf(stored));
            minute.SetValueNoSignal(Routine.MinuteOf(stored));
        }
        void Write()
        {
            block.Set(decl.Key, Routine.At((int)hour.Value, (int)minute.Value));
            // 摆回去的是**夹取之后**的那个数（夹取是声明表那一步的事），
            // 所以拧过头那一格自己回到范围内，而不是停在拧出去的那个数上。
            Show();
            BlockMessage();
        }
        Show();
        hour.ValueChanged += _ => Write();
        minute.ValueChanged += _ => Write();
    }

    /// 一个开关字段。写回走 <see cref="TimeBlock.Set"/>，所以夹取和默认值仍然是声明表那一份。
    private void BlockFlag(HBoxContainer row, TimeBlock block, SettingDecl decl)
    {
        var box = row.GetNode<CheckBox>("Edit");
        box.SetPressedNoSignal((bool)block.Get(decl.Key));
        box.Toggled += on =>
        {
            block.Set(decl.Key, on);
            BlockMessage();
        };
    }

    /// 一个文字字段。**场景放什么控件就是什么控件**，代码只按控件分两支 ——
    /// 和「选哪条休息流程」那一行同一处置（<see cref="ChoicesOf"/>）：
    /// 下拉里摆的是休息流程的名字（用户的数据，不翻），加一格「跟着设置里那一条」当空值；
    /// 文本框就是自由文本。
    private void BlockText(HBoxContainer row, TimeBlock block, SettingDecl decl)
    {
        var editor = row.GetNode<Control>("Edit");
        switch (editor)
        {
            case OptionButton option:
                option.Clear();
                option.AddItem(Tr(BlockFlowNone));
                foreach (var name in _copy.Flows.Keys) option.AddItem(name);
                var current = (string)block.Get(decl.Key);
                // 文档里写了一个当下这张表里没有的名字（下拉里没这一格）——给它留一格，
                // 不然下拉显示的是第一条而副本里存的是另一条，那是个会骗人的控件
                // （同 SelectCurrent）。
                var names = _copy.Flows.Keys.ToList();
                var at = current.Length == 0 ? 0 : names.IndexOf(current) + 1;
                if (at < 0)
                {
                    option.AddItem(current);
                    at = option.ItemCount - 1;
                }
                option.Select(at);
                option.ItemSelected += i =>
                {
                    if (_filling) return;
                    block.Set(decl.Key, (int)i == 0 ? "" : option.GetItemText((int)i));
                    BlockMessage();
                };
                break;
            case LineEdit text:
                _filling = true;
                text.Text = (string)block.Get(decl.Key);
                _filling = false;
                // **占位字不摆**：左边那一列已经写着这个字段叫什么（而且是同一个键
                // 翻出来的那一句），再摆一遍等于屏上并排两遍同一句话 —— 2026-10-02
                // 那一帧里「是哪一种」在左边、右边空格里又来一遍，看着像排版坏了。
                // 按每一次击键走：中间状态允许是空的（把名字清空就是清空，不是手滑）。
                text.TextChanged += written =>
                {
                    if (_filling) return;
                    block.Set(decl.Key, written);
                    BlockMessage();
                };
                break;
        }
    }

    private void MoveBlock(int from, int to)
    {
        var blocks = _copy.Blocks;
        if (to < 0 || to >= blocks.Count) return;
        (blocks[from], blocks[to]) = (blocks[to], blocks[from]);
        RefreshBlocks();
    }

    /// 新增一块 = 从声明表里那些默认值来（<see cref="TimeBlock"/> 的构造就是从那儿取的），
    /// 所以这一族没有第二个 1439、也没有第二个「起止相同」。
    ///
    /// <b>起止相同的那一块是整整一天</b>（契约：止不晚于起就是跨午夜，等号读作跨一整天），
    /// 所以新加的那一块在表里是「全天」—— 用户拧一下起止就是自己要的那一段。
    private void AddBlock()
    {
        _copy.Blocks.Add(new TimeBlock());
        RefreshBlocks();
        Log("options", $"作息表：加了一个时间块（现在 {_copy.Blocks.Count} 块）");
    }

    /// 底下那一句实测结果：几个时间块、一共盖住多少分钟。分钟走的是和设置行同一条显示口径
    /// （<see cref="Settings.Spell"/> 的单位那两条 <c>unit.minute</c>/<c>unit.second</c>）。
    ///
    /// <b>空表那一档要说出来</b>（「还没有时间块」），不是一个空列表：这是用户第一次打开
    /// 这一格看到的东西，而「这一族什么都没配」是合法的一种状态。
    private void BlockMessage()
    {
        var blocks = _copy.Blocks;
        if (blocks.Count == 0)
        {
            _blockTotal.Text = Tr("还没有时间块");
            return;
        }
        var held = blocks.Sum(b => Routine.Until(b.EndMinute, b.StartMinute));
        _blockTotal.Text = $"{Counted("共 N 块", blocks.Count)} · {held} {TrCounted("unit.minute", held)}";
    }

    // ---- 勿扰那一区：时段编辑器 + 名单编辑器 ---------------------------------
    //
    // 形状全在场景里（`Dnd/Body/Windows/Template` 和 `Dnd/Body/Rows/Template`），这里只接线。
    // **字段那一行是按 `Settings.WindowDeclarations` 一条一条摆的**，不是按一张手抄的清单 ——
    // 声明表里加一个字段，界面上多一行、自检里那条形状判据多查一处，三处都从同一份来
    // （和作息那一区同一处置，理由也在那里）。

    /// 时段行里那个字段的那一格：<c>Fields/&lt;字段名&gt;</c>，字段名按场景里节点名的写法
    /// 首字母大写（和 <see cref="BlockField"/> 同一处置）。那一处换法在这里是**第二次**，
    /// 两次换的是同一个规则 —— 各自一处是为了两边各自读得懂，不是一份要抽出去的共用的东西。
    private static string WindowField(SettingDecl decl) =>
        $"Fields/{char.ToUpperInvariant(decl.Key[0])}{decl.Key[1..]}";

    /// 勿扰编辑器上那些<b>不带声明</b>的词（理由同 <see cref="BlockEditorLabels"/>）：
    /// 字段那几行的标签不在这里 —— 它们的键是 <see cref="DndWindow.KeyPrefix"/> + 字段名。
    private static readonly string[] DndEditorLabels =
    [
        "dnd.hint.windows", "dnd.hint.apps", "dnd.fore", "第 N 条", "共 N 条",
        "上移", "下移", "删掉这一条", "加一个时段", "还没有时段", "删掉这个名字",
        "加一个名字", "还没有名字",
    ];

    /// 接线，不是造控件。范围、单位词、默认值、可选值全部从
    /// <see cref="Settings.WindowDeclarations"/> 和 <see cref="Dnd.WeekdayKeys"/> 取 ——
    /// 场景里一个数字都不写（<see cref="Audit"/> 第 12 条）。
    private void BuildDndEditor()
    {
        _dndWindowBox = GetNode<VBoxContainer>(DndPath + "/Windows");
        _dndWindowTemplate = _dndWindowBox.GetNode<Control>("Template");
        _dndWindowTotal = GetNode<Label>(DndPath + "/AddWindow/Total");
        _dndAppBox = GetNode<VBoxContainer>(DndPath + "/Rows");
        _dndAppTemplate = _dndAppBox.GetNode<Control>("Template");
        _dndAppTotal = GetNode<Label>(DndPath + "/AddApp/Total");
        _dndForeground = GetNode<Label>(DndPath + "/Foreground");
        GetNode<Button>(DndPath + "/AddWindow/Btn").Pressed += AddDndWindow;
        GetNode<Button>(DndPath + "/AddApp/Btn").Pressed += AddDndApp;
        RefreshDnd();
    }

    /// 一条时段一叠控件。改字段不回这张表（不然正在打的字会跳），加、删、挪回 ——
    /// 和时间块那一区 <see cref="RefreshBlocks"/> 同一处置。
    private void RefreshDnd()
    {
        for (var i = _dndWindowBox.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _dndWindowBox.GetChild(i);
            if (child == _dndWindowTemplate) continue;
            _dndWindowBox.RemoveChild(child);
            // 当场 Free 会杀掉「正在发 Pressed 信号」的那一颗按钮（同上面两区那一句）。
            child.QueueFree();
        }
        var count = _copy.DndWindows.Count;
        for (var index = 0; index < count; index++)
            _dndWindowBox.AddChild(DndWindowRow(_copy.DndWindows[index], index, count));
        // 名单那几行也一起重灌：这一族在界面上是**一张卡片里的两段**，重灌只灌一段
        // 会让「时段改了、名单那几行还挂在上一份上」—— 两段的性质一样（加、删、改都回表）。
        RefreshDndApps();
        DndMessage();
    }

    /// 一叠 = 一条时段。字段名就是 <see cref="Settings.WindowDeclarations"/> 里那几个，
    /// 标签从语言表按<b>同一个前缀</b>取（<see cref="DndWindow.KeyPrefix"/> + 字段名），
    /// 所以这一段里没有一个字段名是写死的。
    private Control DndWindowRow(DndWindow window, int index, int count)
    {
        var line = (Control)_dndWindowTemplate.Duplicate();
        line.Visible = true;
        var head = line.GetNode<HBoxContainer>("Head");
        head.GetNode<Label>("Which").Text = Counted("第 N 条", index + 1);

        foreach (var decl in Settings.WindowDeclarations)
        {
            var row = line.GetNode<HBoxContainer>(WindowField(decl));
            row.GetNode<Label>("Tag").Text = Tr(DndWindow.KeyPrefix + decl.Key);
            switch (decl.Kind)
            {
                case SettingKind.Int: DndClock(row, window, decl); break;
                default: DndDays(row, window, decl); break;
            }
        }

        var up = head.GetNode<Button>("Up");
        up.Text = Tr("上移");
        up.Disabled = index == 0;
        up.Pressed += () => MoveDndWindow(index, index - 1);
        var down = head.GetNode<Button>("Down");
        down.Text = Tr("下移");
        down.Disabled = index == count - 1;
        down.Pressed += () => MoveDndWindow(index, index + 1);
        var del = head.GetNode<Button>("Del");
        del.Text = Tr("删掉这一条");
        del.Pressed += () =>
        {
            _copy.DndWindows.Remove(window);
            RefreshDnd();
            Log("options", $"勿扰：删掉第 {index + 1} 条（现在 {_copy.DndWindows.Count} 条）");
        };
        return line;
    }

    /// 一个时刻字段 = 「几点」+「几分」两格 —— 和时间块那一格<b>同一段代码的同一套算术</b>
    /// （<see cref="Routine.HourOf"/> / <see cref="Routine.MinuteOf"/> / <see cref="Routine.At"/>），
    /// 范围从声明里那个「一天中的第几分钟」换算过来。**单位词也复用作息那一族那两条**
    /// （语言表里的 <c>blocks[].unit.hour</c> / <c>blocks[].unit.minute</c>）：「点」「分」是
    /// 一个概念的两个词，抄第二遍就是「同一个词说两次」。
    private void DndClock(HBoxContainer row, DndWindow window, SettingDecl decl)
    {
        var hour = row.GetNode<SpinBox>("Hour");
        var minute = row.GetNode<SpinBox>("Minute");
        hour.MinValue = Routine.HourOf(decl.Min);
        hour.MaxValue = Routine.HourOf(decl.Max);
        minute.MinValue = Routine.MinuteOf(decl.Min);
        minute.MaxValue = Routine.MinuteOf(decl.Max);
        hour.Step = 1;
        minute.Step = 1;     // 步长 1：屏幕上是「22」而不是「22.0」
        row.GetNode<Label>("HourUnit").Text = Tr("blocks[].unit.hour");
        row.GetNode<Label>("MinuteUnit").Text = Tr("blocks[].unit.minute");
        // 灌值和写回**分成两步**（同 <see cref="BlockClock"/>：开局那一次只灌不写）。
        void Show()
        {
            var stored = (int)window.Get(decl.Key);
            hour.SetValueNoSignal(Routine.HourOf(stored));
            minute.SetValueNoSignal(Routine.MinuteOf(stored));
        }
        void Write()
        {
            window.Set(decl.Key, Routine.At((int)hour.Value, (int)minute.Value));
            Show();
            DndMessage();
        }
        Show();
        hour.ValueChanged += _ => Write();
        minute.ValueChanged += _ => Write();
    }

    /// 星期几那一格：<b>七个勾选框按 <see cref="Dnd.WeekdayKeys"/> 现造</b>，所以求值那一份和
    /// 界面上这一份是同一份（和「摆什么」那一排同一做法）。每个勾选框就叫它那个词
    /// —— 别在名字里放冒号（<c>NodePath</c> 里冒号是「子资源.属性」那一段的记法）。
    ///
    /// <b>写回按声明里的顺序，不按点的先后</b>：一份文档里同一条时段不该有两种星期几的顺序。
    private void DndDays(HBoxContainer row, DndWindow window, SettingDecl decl)
    {
        var box = row.GetNode<HBoxContainer>("Edit");
        var template = box.GetNode<CheckBox>("Template");
        var chosen = (List<string>)window.Get(decl.Key);
        for (var i = 0; i < Dnd.WeekdayKeys.Length; i++)
        {
            var key = Dnd.WeekdayKeys[i];
            var made = (CheckBox)template.Duplicate();
            made.Name = new StringName(key);
            made.Visible = true;
            // 词从语言表里**已经有**的那七条星期取（周一、周二……），不自己造一套。
            made.Text = Dnd.WeekdayText(i);
            made.SetPressedNoSignal(chosen.Contains(key));
            made.Toggled += _ =>
            {
                var picked = Dnd.WeekdayKeys.Where(k => box.GetNode<CheckBox>(k).ButtonPressed).ToList();
                window.Set(decl.Key, picked);
                DndMessage();
            };
            box.AddChild(made);
        }
    }

    private void MoveDndWindow(int from, int to)
    {
        var windows = _copy.DndWindows;
        if (to < 0 || to >= windows.Count) return;
        (windows[from], windows[to]) = (windows[to], windows[from]);
        RefreshDnd();
    }

    /// 新增一条 = 从声明表里那些默认值来（<see cref="DndWindow"/> 的构造就是从那儿取的），
    /// 所以这一族没有第二个 1439。**起止相同的那一条读作整整一天**（止不晚于起 = 跨午夜），
    /// 而星期几是声明给的默认（周一）—— 用户拧一下就是自己要的那一段。
    private void AddDndWindow()
    {
        _copy.DndWindows.Add(new DndWindow());
        RefreshDnd();
        Log("options", $"勿扰：加了一条时段（现在 {_copy.DndWindows.Count} 条）");
    }

    private void AddDndApp()
    {
        var add = GetNode<LineEdit>(DndPath + "/AddApp/Text");
        var name = add.Text.Trim();
        if (name.Length == 0) return;
        _copy.DndApps.Add(name);
        add.Text = "";
        RefreshDnd();
        Log("options", $"勿扰名单：加了一个名字（现在 {_copy.DndApps.Count} 个）");
    }

    /// 底下那两句实测结果：时段一共盖住多少分钟、名单上有几个名字。
    /// 分钟走的是和设置行同一条显示口径（和 <see cref="BlockMessage"/> 同一条路）。
    /// **空表那一档要说出来**，不是一个空列表 —— 「这一族什么都没配」是合法的一种状态。
    private void DndMessage()
    {
        // 休息流程和名单改的也是「窗里这一份」，所以窗头那颗钮上「我在哪个生活模式」要重新问一遍。
        RefreshLifeModeWhere();
        var windows = _copy.DndWindows;
        _dndWindowTotal.Text = windows.Count == 0
            ? Tr("还没有时段")
            : $"{Counted("共 N 条", windows.Count)} · " +
              $"{windows.Sum(w => Routine.Until(w.EndMinute, w.StartMinute))} " +
              $"{TrCounted("unit.minute", windows.Sum(w => Routine.Until(w.EndMinute, w.StartMinute)))}";
        _dndAppTotal.Text = _copy.DndApps.Count == 0
            ? Tr("还没有名字")
            : Counted("共 N 条", _copy.DndApps.Count);
    }

    /// 名单那几行：一行一个名字，一个文本框加一颗删掉的钮（和休息清单那几行同一形状）。
    /// **空的那一行就是删掉那一条** —— 和清单那一条同一处置（界面上这一行跟着没，落盘前再滤一次：
    /// <c>Settings.ClampToDeclarations</c> 那一段去空行）。
    private void RefreshDndApps()
    {
        for (var i = _dndAppBox.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _dndAppBox.GetChild(i);
            if (child == _dndAppTemplate) continue;
            _dndAppBox.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var name in _copy.DndApps.ToList())
        {
            var line = (Control)_dndAppTemplate.Duplicate();
            line.Visible = true;
            var edit = line.GetNode<LineEdit>("Edit");
            _filling = true;
            edit.Text = name;
            _filling = false;
            edit.TextChanged += written =>
            {
                if (_filling) return;
                var at = _copy.DndApps.IndexOf(name);
                if (at < 0) return;
                if (written.Trim().Length == 0)
                {
                    _copy.DndApps.RemoveAt(at);
                    RefreshDnd();
                    return;
                }                _copy.DndApps[at] = written;
                DndMessage();
            };
            var del = line.GetNode<Button>("Del");
            del.Text = Tr("删掉这个名字");
            del.Pressed += () =>
            {
                _copy.DndApps.Remove(name);
                RefreshDnd();
            };
            _dndAppBox.AddChild(line);
        }
    }

    /// 「当前前台是哪个程序」那一条实测结果：<b>完整路径</b>，供用户照着名单上那一个名字写
    /// （整名精确匹配，而 <c>chrome.exe</c> 全家一个名，路径才是核对用的那一格 ——
    /// ADR-0006「应用认人用 exe 名」那一节）。
    ///
    /// <b>这一格不走 <see cref="Foreground.Describe"/>。</b> 那个句子是**日志实测结果**，和
    /// <see cref="Fullscreen.Measurement"/> 同一档 —— 那是给人和排查看的诊断，措辞只在中文那一栏有。
    /// 这一格是<b>界面</b>，所以 Keeper 自己的措辞每一条都从语言表取（设计文档 `docs/design/ui-text.md` 那一节）；
    /// 走表的那几个词是「前台应用」「路径」「问不到」，而 <c>exe 名</c> 和<b>完整路径</b>是
    /// 「文档里的数据」，原样显示、不进语言表。
    /// 「问不到」那一档照实说，不印一个空字符串。
    private void RefreshDndForeground()
    {
        var measurement = ForegroundApp();
        _dndForeground.Text = measurement.Known
            ? $"{Tr("dnd.fore")}：{Tr("dnd.fore.app")}={measurement.Exe} {Tr("dnd.fore.path")}={measurement.Path}"
            : $"{Tr("dnd.fore")}：{Tr("dnd.fore.none")}";
    }

    /// 时刻界面上摆的是「几点 / 几分」，那两个数格是**一个**值的两半。
    /// 自检里对着磁盘那一份量（<see cref="ScreenSelfTest"/>）—— 量的是「摆的是时刻、
    /// 不是一天里的第几分钟」，而「拧一个数格另一个跟着跳」是那一族另一组的事。
    internal static List<(Control Row, DndWindow Window, int Index)> DndWindowRows(Options win) =>
        win._dndWindowBox.GetChildren()
            .Where(c => c != win._dndWindowTemplate).Cast<Control>()
            .Select((c, index) => (c, win._copy.DndWindows[index], index)).ToList();

    /// 名单那几行的文本框（按行序，和副本里那一份名单一一对应）。
    internal static List<LineEdit> DndAppRows(Options win) =>
        win._dndAppBox.GetChildren()
            .Where(c => c != win._dndAppTemplate).Cast<Control>()
            .Select(c => c.GetNode<LineEdit>("Edit")).ToList();

    // ---- 「历史」那一格 ----------------------------------------------------------

    /// 「历史」那一格里那颗开窗的钮。
    ///
    /// <b>路径写在场景里，规矩和别处一样</b>：抄一份路径字符串就是「改了场景名忘了改这里」
    /// 那一族，而症状是窗上什么都没有、不报错。<see cref="HistoryPath"/> 是这一族唯一的出处。
    internal const string HistoryPath = FormPath + "/History/Body/Open";

    /// 「历史」那一颗。<b>它和托盘那一条调的是同一个动作</b>（<c>App.OpenHistory</c>）——
    /// 设计文档 `docs/design/records.md`「两个入口，同一份数据」那一条：两处各写一个「开窗」就是两扇窗、两次读文件，
    /// 而那种不一致在界面上看不出是哪一边错了。「已经开着就提到前面」那个判断在
    /// <c>App</c> 那一侧，所以这一格不自己开窗、不自己判断。
    public Action OpenHistory = () => { };

    /// 那一颗的接线。按的是它自己的 <c>pressed</c>，和真鼠标点出来的是同一条路。
    private void WireHistoryCard() =>
        GetNode<Button>(HistoryPath).Pressed += OpenHistory;

    // ---- 底下那排按钮 -------------------------------------------------------------

    private void WireFooter()
    {
        var foot = GetNode<HBoxContainer>("Margin/Stack/Footer");
        foot.GetNode<Button>("Preview").Pressed += PreviewRequested;
        foot.GetNode<Button>("Save").Pressed += Save;
        foot.GetNode<Button>("Defaults").Pressed += Defaults;
        foot.GetNode<Button>("Export").Pressed += () =>
            Pick(true, Settings.FileName, ExportTo, ["*.jsonc"]);
        foot.GetNode<Button>("Import").Pressed += () =>
            Pick(false, "", ImportFrom, ["*.jsonc;*.json"]);
        GetNode<Button>(ListPath + "/Add/Btn").Pressed += AddTodo;

        var pick = GetNode<FileDialog>("Pick");
        pick.FileSelected += path =>
        {
            var then = _picked;
            _picked = null;
            then?.Invoke(path);
        };
        pick.Canceled += () => _picked = null;
    }


    private void AddTodo()
    {
        var add = GetNode<LineEdit>(ListPath + "/Add/Text");
        var text = add.Text.Trim();
        if (text.Length == 0) return;
        Checklist.Items.Add(new Todos.Todo { Text = text });
        add.Text = "";
        SaveChecklist();
        BuildChecklist();
    }

    /// 一个文件框三条用处：给某个 .file 键挑路径、导出、导入。
    /// 一律开在设置所在的那个目录 —— 导出导的就是那份文档，导入进的也是给它的另一份。
    /// 不管这一手的话文件框停在 <c>res://</c>：打包之后那在 pck 里面，列出来的是引擎自己的东西。
    /// 一个文件框四条用处，各自的扩展名列表和这里能放的必须是同一份，四个入口不许都列全部
    /// 文件。图那一档没有 <c>gif</c>：引擎运行时不认这一档，实测结果在
    /// <see cref="FaderPicture.Formats"/> 上面那段。
    private static string[] FilterOf(string key) => key switch
    {
        "image.file" => ["*." + string.Join(";*.", FaderPicture.Formats)],
        "sound.file" => ["*." + string.Join(";*.", Sound.Formats.Select(f => f.TrimStart('.')))],
        _ => ["*.*"],
    };

    private void Pick(bool save, string from, Action<string> then, string[]? filter = null)
    {
        var pick = GetNode<FileDialog>("Pick");
        _picked = then;
        pick.FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile;
        pick.Title = Tr(save ? "导出到" : "选一个文件");
        // 每次都现设：这一个 FileDialog 四条入口共用，上一次留下的列表会串到下一次头上。
        pick.Filters = filter ?? ["*.*"];
        // CurrentPath 一次给全（目录 + 文件名）。分两次给会被它把目录吃掉：先 CurrentPath=目录、
        // 再 CurrentFile="settings.jsonc"，实测那一下文件框开到了上一级目录的 settings.jsonc 上。
        // .file 那一行是「就着现在选中的那个文件挑」，导出/导入才是设置所在的那个目录 ——
        // 不管这一手的话文件框停在 res://，打包之后那在 pck 里面，列出来的是引擎自己的东西。
        pick.CurrentPath = save ? $"{DataDir}/{Settings.FileName}"
            : from.Length > 0 && System.IO.Path.IsPathRooted(from) ? from
            : DataDir;
        pick.Visible = true;
        pick.GrabFocus();
    }

    private void ExportTo(string path)
    {
        var failed = SettingsStore.Export(DataDir, State, path);
        Say(failed == null ? $"{Tr("已导出")} {path}" : $"{Tr("导出没成")}：{failed}", failed != null);
        Log("options", $"导出 {path}：{failed ?? "OK"}");
    }

    private void ImportFrom(string path)
    {
        var written = SettingsStore.Import(DataDir, State, path);
        if (written.Error != null)
        {
            Say($"{Tr("导入没成")}：{written.Error}", true);
            Log("options", $"导入 {path} 没成，磁盘和内存都没动");
            return;
        }
        // 导入还回来一份<b>新</b>的状态，里面 Current 指向另一个 Settings 对象。
        // 接过来再往上递 —— 原来这一步是「Import 就地换了 State.Current，所以活的设置要有人
        // 重新拿一次」，靠的是每个持有者记得；现在它是返回值，接不住就编译不过。
        AdoptRead(written.State);
        Say($"{Tr("已导入")} {path}" +
            (State.NewerThanThisBuild ? $"（{Tr("这份文档的格式版本比本程序新")}）" : ""), false);
        Log("options", $"导入 {path}：被取代的那份留在 {SettingsStore.BackupPath(DataDir)}");
    }

    /// <summary>一份新的读取结果接过来之后的收尾：换掉 <see cref="State"/>、往上递、
    /// 重灌界面。<b>导入和切生活模式是同一条路的两个入口</b>，所以这一步也只有一处 ——
    /// 两处各写一遍，改了一处忘了另一处就是「导入换掉了活设置、切生活模式没换」。
    ///
    /// <see cref="Load"/> 里面已经跟着换了那一句「磁盘上那份读不通」和配色
    /// （导入进来的那一份可能读不通，配色也可能是另一份）。</summary>
    private void AdoptRead(SettingsStore.Read state)
    {
        State = state;
        Applied?.Invoke(State);
        _copy = Live.Clone();
        Load();
    }

    private void Defaults()
    {
        _copy = new Settings();
        Load();
        Say(Tr("每个键都回到了声明表里的默认值，按保存才写进文件"), false);
    }

    /// 副本换了，控件里的值重新灌一遍：范围、选中项、文字还是从声明取的那一份。
    private void Load()
    {
        foreach (var row in _rows)
        {
            if (row.Value is OptionButton option) Fill(option, row);
            Load(row);
        }
        // flows 那张表跟着副本一起换（「恢复默认」和导入都换了它），所以那一区整个重灌，
        // 并且把「正在编辑哪一条」重新对到新副本上 —— 旧名字在新表里可能根本没有。
        if (_flowBox != null)
        {
            _editing = _copy.Flows.ContainsKey(_copy.Flow) ? _copy.Flow : "";
            RefreshFlows();
        }
        // 作息表那张表跟着副本一起换（恢复默认和导入都换了它），所以那一区整个重灌。
        if (_blockBox != null) RefreshBlocks();
        // 勿扰那两张表同一条处置（时段表 + 名单 + 总开关：恢复默认和导入都换了它们）。
        if (_dndWindowBox != null) RefreshDnd();
        Status();
        RefreshPreview();
        // 副本整个换了（开窗 / 切生活模式 / 导入 / 恢复默认），那一档重新问一遍。
        RefreshLifeModeWhere();
        // 导入进来的那一份读不通，抬头那一句要跟着换掉；读通了就要跟着消失。
        ShowDocError();
        // 配色也是「恢复默认 / 导入」会换掉的那一份（默认值是亮色），所以跟着换。
        ApplyUiTheme();
    }

    /// 底排那颗「保存」。报喜报忧都归它，<b>落盘那一段是 <see cref="SaveNow"/></b> ——
    /// 「保存并切换」要拿同一段落盘，而那一档自己在失败时说话（多一句「所以没有切」）。</summary>
    private void Save()
    {
        var changed = Settings.Diff(Live, _copy);
        var why = SaveNow();
        Say(why ?? (changed.Length == 0 ? $"{Tr("已保存")}（{Tr("没有改动")}）" : Tr("已保存")), why != null);
        Log("options", why == null ? $"保存：改动 {(changed.Length == 0 ? "没有" : changed)}" : $"保存没成：{why}");
    }

    /// <summary>窗里这一份按保存落到磁盘。<b>没成的时候回来一句说给人看的话，一句都不往
    /// 状态栏上摆</b> —— 底排那颗钮要把它摆上去（红的），而「保存并切换」要在它后面接上
    /// 「所以没有切」并且<b>不切</b>（设计文档 `docs/design/life-mode.md`「切换有两个动作」那一条的后半句）。
    ///
    /// 读不通的时候拒绝落笔（设计文档 `docs/design/config.md`「解析失败是一个状态，不是崩溃」那条），而那正是
    /// 「保存并切换」不切的那一档：切过去是拿写好的文档换的，先切等于替用户盖掉那份。</summary>
    private string? SaveNow()
    {
        if (State.Error != null) return $"{Tr("磁盘上那份读不通，所以没有保存")}：{State.Error}";

        Live.AdoptValues(_copy);
        var written = SettingsStore.Save(DataDir, State);
        State = written.State;
        if (written.Error != null)
        {
            // 值已经搬进活的设置了，但磁盘没写成 —— 说清楚，别让人以为文件跟着屏幕走了。
            return $"{Tr("保存没成")}：{written.Error}（{Tr("界面上的值还在，文件没动")}）";
        }
        // 比的是「换之前那个语言」：Applied 里 App 已经把 locale 换过去了，
        // 再拿 _copy.Language 和当下比就永远相等，窗口会留着旧语言（上一版就是这么露馅的）。
        var before = TranslationServer.GetLocale();
        Applied?.Invoke(State);
        if (before != TranslationServer.GetLocale()) Texts();
        return null;
    }


    /// <summary>测量程序那一发用的：把 flows 那一区滚到窗口里来。它在表单中段，不滚的话
    /// 抓到的永远是抬头那几行 —— 而这一区要的恰恰是一张能拿眼睛看的帧。</summary>
    public void RevealFlows() => RevealSection("Flows");

    /// <summary>测量程序那一发用的：把<b>指定那一张卡片</b>滚到窗口里来。
    ///
    /// 「不滚的话抓到的永远是抬头那几行」对每一张中段的卡片都成立（作息表那一张、勿扰那一张
    /// 都在中段），而每一族各写一个 <c>Reveal…</c> 就是同一个动作抄几遍 —— 所以这里是一个
    /// 按分区节点名滚的口，<see cref="RevealFlows"/> 只是它的一个调用。
    /// 分区名认不出来就是 0（滚到顶），不抛 —— 测量程序不该因为一个名字打错就整发失败。</summary>
    public void RevealSection(string node)
    {
        var scroll = GetNode<ScrollContainer>("Margin/Stack/Scroll");
        // 滚到**卡片**（分区自己）而不是卡片里那一竖排：测量程序量的是「这一片」在哪儿。
        var at = Card(this, node)?.Position.Y ?? 0;
        scroll.ScrollVertical = (int)System.Math.Max(0, at - 12);
        Log("options", $"滚到「{node}」那一张卡片：它在表单里第 {at:F0} 像素，滚到 {scroll.ScrollVertical}");
    }

    /// <summary>布局的实测结果：<b>有没有哪个控件伸出窗口外面</b>，以及 flows 那一区的步摆在哪儿。
    /// 这条只能在跑起来那一头发（<c>App.tscn -- --options-shot</c>）：控件的矩形要排版跑完几帧
    /// 才是真的，无头自检里全是 0。
    ///
    /// 它存在的原因是踩过的坑：中文按 WORD 折行会把整行撑宽，头两发 <c>--options-shot</c> 的帧
    /// 看着像裁剪 bug，
    /// 其实是折行档选错了（<c>CODING_STANDARDS.md</c> autowrap 那一条）。flows 那一区是新加的
    /// —— 一步一叠、横着一排勾选框 —— 同一种撑法在那儿更容易，所以那一列单独报最右边到哪儿。
    /// 最后板斧仍然是拿眼睛看那一帧。</summary>
    public string LayoutMeasurement()
    {
        var outOfBounds = new List<string>();
        foreach (var node in Walk(this).OfType<Control>())
        {
            if (!node.IsVisibleInTree()) continue;
            var r = node.GetGlobalRect();
            // 只问横向。纵向不问：整份表单是装在 ScrollContainer 里的，它本来就该比窗口高
            // （第一发按两条都问，读出 244 个「伸出窗口」，那 244 个全是滚动区里的正常内容）。
            // 横向伸出才是那个真的坑：控件跑到窗口外面，人看不见也点不到。
            if (r.End.X > Size.X + 0.5f || r.Position.X < -0.5f)
                outOfBounds.Add($"{Leaf(node)}：从 {r.Position.X:F0} 到 {r.End.X:F0} 高 {r.Size.Y:F0}" +
                                $"（窗口宽 {Size.X}）");
        }
        var widest = 0f;
        var seen = 0;
        if (_flowBox != null)
            foreach (var child in Walk(_flowBox).OfType<Control>())
            {
                if (child == _stepTemplate || !child.IsVisibleInTree()) continue;
                if (child.GetGlobalRect().Size.X <= 0) continue;
                seen++;
                widest = Math.Max(widest, child.GetGlobalRect().End.X);
            }
        var stacks = Edited?.Steps.Count ?? 0;
        return $"布局：窗口 {Position}+{Size.X}x{Size.Y}，正在编辑的「{_editing}」有 {stacks} 步，" +
               $"步那一列里看得见且有宽度的节点 {seen} 个，那一列最右边到 {widest:F0}，" +
               $"伸出窗口的控件 {outOfBounds.Count} 个" +
               (outOfBounds.Count == 0 ? "" : $"：{string.Join("；", outOfBounds.Take(6))}") +
               $"；{ColumnMeasurement()}；滚条 {ScrollMeasurement()}";
    }

    /// <summary>三列的实测结果：看得见的那些行，<b>标签左缘、控件右缘、单位左缘各是同一条线吗</b>。
    ///
    /// 形状那一半（三格都在、次序对不对）由 <see cref="Audit"/> 第 20 条在无头自检里问，
    /// 那一半不需要排版；这一半问的是排版之后的位置，只在跑起来那一发才有 —— 无头自检里
    /// <see cref="ScreenSelfTest"/> 刚 AddChild 就问，窗还没排过版，量到的全是 0。
    ///
    /// 「量不到」这件事要说出来而不是当没事：一串 0 拿去比相等是永远相等的那种相等。
    /// 所以量不到的时候直接报「量不到」，让实测结果自己承认它这一发没干活。</summary>
    private string ColumnMeasurement()
    {
        var rows = Walk(this).OfType<GridContainer>()
            .Where(r => r.HasMeta("setting") && r.IsVisibleInTree()).ToList();
        var measured = rows.Where(r => r.Size.X > 0).ToList();
        if (measured.Count == 0) return $"三列：看得见的行 {rows.Count} 条，可是没有一条有宽度 —— 这一发量不到（三列的形状归自检里第 20 条管）";
        var at = new Dictionary<string, (float Text, float ValueEnd, float UnitStart, float ValueMin, float RowEnd, float TextMin)>(StringComparer.Ordinal);
        foreach (var r in measured)
        {
            var value = r.GetNodeOrNull<Control>("Value");
            at[r.GetMeta("setting").AsString()] = (
                r.GetNodeOrNull<Control>("Text")?.GetGlobalRect().Position.X ?? 0,
                value?.GetGlobalRect().End.X ?? 0,
                r.GetNodeOrNull<Control>("Unit")?.GetGlobalRect().Position.X ?? 0,
                value?.GetCombinedMinimumSize().X ?? 0,
                r.GetGlobalRect().End.X,
                r.GetNodeOrNull<Control>("Text")?.GetCombinedMinimumSize().X ?? 0);
        }
        var texts = at.Values.Select(v => v.Text).ToList();
        var valueEnds = at.Values.Select(v => v.ValueEnd).ToList();
        var unitStarts = at.Values.Select(v => v.UnitStart).ToList();
        var straight = texts.Max() - texts.Min() < 0.5f
                       && valueEnds.Max() - valueEnds.Min() < 0.5f
                       && unitStarts.Max() - unitStarts.Min() < 0.5f;
        // 没共线的时候**说得出是哪几行、还带上分辨「谁撑的」要的那几个数**：那一格控件自身的
        // 下限宽（超过列宽就是它把这一行撑宽）、以及这一行自己的右缘（和别的不一样就是父级
        // 宽度不同，不是格子的问题）。只报一个差值的话，收到实测结果的人得自己再猜一遍 ——
        // 而这一族最常见的两种原因**只靠差值是分不开的**（量过的两次结论正好相反：
        // 第一次猜「控件下限太宽」，实测结果里那一列全是 150，假设是错的）。
        var odd = straight ? ""
            : "；没共线的是 " + string.Join("、", at.Where(kv =>
                    kv.Value.Text > texts.Min() + 0.5f
                    || kv.Value.ValueEnd > valueEnds.Min() + 0.5f
                    || kv.Value.ValueEnd < valueEnds.Max() - 0.5f
                    || kv.Value.UnitStart > unitStarts.Min() + 0.5f)
                .Select(kv => $"{kv.Key}（值到 {kv.Value.ValueEnd:F0}、这一行到 {kv.Value.RowEnd:F0}、" +
                              $"那一格控件自身的下限宽 {kv.Value.ValueMin:F0}、标签块的下限宽 {kv.Value.TextMin:F0}）"));
        return $"三列：量到 {measured.Count} 条（有宽度的），标签左缘 {texts.Min():F0}-{texts.Max():F0}、" +
               $"控件右缘 {valueEnds.Min():F0}-{valueEnds.Max():F0}、单位左缘 {unitStarts.Min():F0}-{unitStarts.Max():F0}" +
               (straight ? " → 三条都是一条线" : " → **没共线**（这个差值就是标签歪掉的那几像素）") +
               odd;
    }

    /// <summary>两根滚条此刻的样子：<b>在不在屏幕上、多宽、占了多高的比例</b>。
    ///
    /// 为什么单列一条实测结果：「那根条好不好看」在像素里是一根几像素宽的浅灰竖条，量它靠看；
    /// 而「它在不在」是可以量的，而这一条量出来过一次反直觉的结果 —— 把它的主题配成
    /// 空轨道 + 浅灰抓手之后，抓帧里<b>右边那根整个不见了</b>（改之前它在，是引擎默认那套
    /// 深色轨道 + 深色抓手，白底上像一根黑线）。所以先量清楚它到底被画出来没有，
    /// 再决定要不要留它。抓帧看的是「像不像个东西」，这一条量的是「在不在」——
    /// 两者不一致过（假阴性那一族：功能对、像素不对）。</summary>
    private string ScrollMeasurement()
    {
        var scroll = GetNode<ScrollContainer>("Margin/Stack/Scroll");
        var v = scroll.GetVScrollBar();
        var h = scroll.GetHScrollBar();
        var grab = v.GetThemeStylebox("grabber");
        return $"竖 在屏={v.IsVisibleInTree()} 宽={v.Size.X:F0} 高={v.Size.Y:F0} " +
               $"抓手主题最小宽={(grab?.GetMinimumSize().X ?? -1):F0} " +
               $"值域={v.MinValue:F0}..{v.MaxValue:F0} 当前={v.Value:F0}；" +
               $"横 在屏={h.IsVisibleInTree()} 生活模式={scroll.HorizontalScrollMode}";
    }

    /// 说实测结果要说清是哪儿：带上一层名字，比一串同名控件好认。
    private static string Leaf(Node node) =>
        node.GetParent() == null ? $"{node.Name}" : $"{node.GetParent().Name}/{node.Name}";

    /// 自检用的两只手。按钮按下去走的是它自己的 pressed 信号，和鼠标点出来的是同一条接线。
    private void PressFooter(string which) => GetNode<Button>($"Margin/Stack/Footer/{which}")
        .EmitSignal(BaseButton.SignalName.Pressed);

    /// 把「选中了这个文件」那一个信号交给文件框。
    private void Choose(string path) => GetNode<FileDialog>("Pick")
        .EmitSignal(FileDialog.SignalName.FileSelected, path);

    /// 声明那一种 ↔ 场景里那一种控件，对得上才算。颜色那一格 2026-09-30 换成了引擎自带的
    /// <c>ColorPickerButton</c>（原来是一颗 <c>LineEdit</c> 收 <c>#RRGGBB</c>），所以这一行
    /// 跟着换 —— <b>两处都要改</b>：换掉控件而忘了这里，审计第 4 条会红；只改这里而忘了换控件，
    /// 界面上一行写着 <c>#008040</c> 的文本框还在而自检是绿的。
    private static bool KindFits(SettingDecl decl, Control value) => decl.Kind switch
    {
        SettingKind.Bool => value is CheckBox or CheckButton,
        SettingKind.Int => value is SpinBox,
        SettingKind.HexColor => value is ColorPickerButton,
        SettingKind.Choice => value is OptionButton,
        SettingKind.Text => value is LineEdit or OptionButton,
        // 一串分钟数也是一行字、不是一颗数字格 —— 值是**一串**（逗号分隔），没有值域那回事，
        // 而它的行正是靠这一条钉住「别把它摆成 SpinBox」的。
        SettingKind.DelayList => value is LineEdit,
        _ => false,
    };

    private static IEnumerable<Node> Walk(Node from)
    {
        foreach (var child in from.GetChildren())
        {
            yield return child;
            foreach (var deeper in Walk(child)) yield return deeper;
        }
    }
}
