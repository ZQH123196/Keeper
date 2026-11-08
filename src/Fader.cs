using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>一块屏一个遮罩：无边框、透明、置顶、鼠标穿过、拿不走键盘焦点。
///
/// <b>这一扇一辈子只负责画</b>：鼠标穿过从头到尾是真的，休息流程里写着「接鼠标」的那一步
/// 也不例外。2026-10-10 所有者定夺「永远都不能抢鼠标，这是 bug」，而 Windows 上穿透是
/// 整扇窗一个开关 —— 所以「点得到的那几块」不再有窗：<see cref="ClickRegion"/> 那一块矩形
/// 交给全局钩子按坐标判，落在里面的左键按下被吃掉，底下的程序那一扇窗照常收得到。
/// 走向和代价在 <c>docs/adr/0003</c>，契约在
/// `docs/design/hit-testing.md`「点得到的那几块靠全局钩子按坐标判」那一节。
///
/// <b>屏上摆什么、摆哪、摆多大，读的是这一步自己的布局</b>（<see cref="StepLayout"/>，
/// 设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节）。<c>content</c> 和 <c>text</c> 两个成员
/// 2026-10-06 退休：那一族的老名字由 <see cref="StepLayout.Translate"/> 读一次翻成布局，
/// 翻完之后它们只是陌生的键。<b>这一发里没有一处拿它们当权威</b>。
/// 份额换成像素、字号换成框的一个份额，那一段算术在 <see cref="StepLayout.Place"/> ——
/// 纯函数、不读窗，所以自检能在无头里问它；这一边只负责把算出来的结果摆到控件上。
///
/// 淡入是按帧推 modulate 的 alpha。这里要写清一件事：<b>引擎的 Window 没有 Modulate</b>
/// —— 只有 CanvasItem 有（GodotSharp.xml 里没有 P:Godot.Window.Modulate，测量程序量 alpha 时
/// 改的也是那个 ColorRect）。所以斜坡推在装内容的那个 Control 根上：窗口每一像素都是它画的，
/// 乘在它身上和乘在窗口上是同一件事。<c>docs/research/godot-csharp-binding.md</c>「<c>Window</c> 没有
/// <c>Modulate</c>」那一条按字面做不到，效果按这一句实现。
///
/// 实测说中间 alpha 混的是 8 位编码值，不是线性光，所以一条直线斜坡本身就差不多是均匀的
/// 视觉变化，不用 gamma 表。
///
/// 两条不许跳的规矩落在这里：举起窗口的那一帧 alpha 是 0（「出现」不是从空里蹦出来），
/// 淡出必须走完才放窗（「离开」也不是被拔掉）。
///
/// 窗口是一次淡入造一个、淡出走完就还回去 —— 引擎里没有「窗口必须先预付」那一档成本，
/// 所以也不必每块屏常驻一个隐藏窗口等着被举起来。</summary>
public partial class Fader : Window
{
    public const string TitlePrefix = "keeper-fader";

    private readonly Settings _settings;
    private readonly Action<string> _log;
    private readonly Todos _todos;
    private readonly Action _todosChanged;
    private readonly Control _content;
    private readonly ColorRect _fill;
    private readonly FaderPicture _picture;
    private readonly Label _clock;
    private readonly CenterContainer _side;
    private readonly VBoxContainer _checklist;
    private readonly Label _block;
    private readonly Label _empty;
    /// 「还剩 m:ss」。2026-10-06 从页脚那个 HBox 搬进中间那一列的正当中
    /// （布局在 <c>FaderContent.tscn</c>，那个容器连同它一起退休了 —— 它原来只包这一行，
    /// 而侧面上那颗钮要收得到点击，那一块只算点得到的那几块，中间这一族不该留一个空容器）。
    /// 2026-10-06 晚些时候它自己成一个独立控件：布局给的份额是绝对的一份，容器替不了。
    private readonly Label _remain;
    /// 「提前结束休息」那颗钮外面那一层（CenterContainer）：布局给的是一个框，
    /// 而这颗钮的宽度是字加内边距算出来的，撑不满一个框 —— 套一层就是把
    /// 「按自己的宽度摆在框的正当中」交给引擎。
    private readonly CenterContainer _end;
    private readonly Button _endBtn;
    private readonly List<CheckRow> _rows = [];
    /// 布局里的**文字**控件：几个都行（设计文档 `docs/design/fader-layout.md`「文字和图不限个数」）。所以它们不是场景里
    /// 摆着的节点，是换步时按布局现搭、用完就还回去的那一族 —— 和清单那几行
    /// （<see cref="BuildChecklist"/>）同一个做法，那一族本来就是现搭的。
    private readonly List<Label> _texts = [];
    /// 布局里的**图**控件，和 <see cref="_texts"/> 同理。
    private readonly List<FaderPicture> _images = [];
    /// <b>收鼠标的那一块</b>（屏幕坐标）。空 = 这一步什么也不收。
    /// 它不是一扇窗：窗上罩进去的每一像素底下都点不到，而所有者定的是「永远都不能抢鼠标」
    /// （2026-10-10）。这一块交给全局钩子按坐标判（<see cref="Swallows"/>），
    /// 别处（<c>App._Process</c>）拿 <see cref="Route"/> 把吃掉的那一下发回来。
    /// 形状是「意图」也是「事实」—— 这一族没有窗了，不再有「引擎照不照意图摆」那一份差。
    private Rect2I _swallow;
    private ImageTexture _boxOff;
    private ImageTexture _boxOn;
    /// 这一扇窗上那几处尺寸按屏高算出来的那一份（<see cref="Measure"/>）。
    private readonly Metrics _m;
    /// 这一块屏的系统 DPI，按百分之一。字号那根地板乘的是它（<see cref="StepLayout.FontFloor"/>），
    /// 不是屏的分辨率 —— 换一块分辨率不同的屏不该换掉「再小就读不出来」那一档。
    private readonly int _dpiPercent;
    /// 清单那一行此刻该多大。清单的行是换步时现搭的，而它们量自己的时候手上只有这一个数 ——
    /// 布局给的是「清单那个框」，框里那些行跟着它走（今天它们是同一档字号）。
    private int _listFont;
    /// 清单那一列的宽，屏宽的百分之一。排版还没跑（自检是 headless 的）时折行上限按它算，
    /// 一个由配置算出来的数，不依赖引擎什么时候排版。
    private int _checklistSharePercent = (int)(SideShare * 100);
    /// 字的那一份颜色，<see cref="InkAgainst"/> 对过底之后的那一份。清单和那排格子在换档时
    /// 才搭，所以它们要拿这一份，不能再拿设置里那一份。
    private readonly Color _ink;

    private double _elapsedMs;
    private double _stepAtMs;
    private long _stepMs;
    private Step? _step;
    private bool _leaving;
    private bool _firstFrameDone;
    private bool _settled;
    private bool _landedFired;
    private int _reportInFrames;
    private float _lastAlpha;

    /// alpha 走到 0，淡出该走的时间也走完了。
    public event Action? Landed;

    /// 淡入那条斜坡走完了、遮罩落在屏幕上那一刻 —— 契约里「落定之后」指的就是它。
    /// <c>sound.afterLanding</c> 开着时，声音等的就是这一发（App.cs 接的）。
    public event Action? Settled;

    /// 「提前结束休息」被按了。按按钮的人已经休息了一会儿，所以这一条按休息记账，
    /// 不算拒绝——<see cref="Scheduler.EndBreak"/> 里定的是同一个意思。
    public event Action? EndBreakRequested;

    public float Opacity => _content.Modulate.A;

    /// 斜坡走了多少帧、单帧最大跳了多少。「不许跳」要的是一个数，不是一句断言。
    public int RampFrames { get; private set; }
    public float MaxAlphaStep { get; private set; }

    /// 这块屏正中那个物理点，取样用。
    public Vector2I Centre => Position + Size / 2;

    /// <summary>测量程序那一发要的：屏幕那一点上，<c>image</c> 那一层画的是什么色；
    /// 没开图或那一点在图外就还回 <c>null</c>（那时字底下压着的是纯色底）。
    /// 判据一「这一处该是什么色」缺的就是这个数 —— 没有它，一张图会被数成一片字。</summary>
    public Color? PicturePixel(Vector2I screenPoint) =>
        _picture.PixelAt(new Vector2(screenPoint.X - Position.X, screenPoint.Y - Position.Y));

    /// <summary>遮罩上的块名和时钟要的现实时间（卡片 02）：<b>时刻由构造方给</b>，这一族自己
    /// 不问 <c>DateTime.Now</c>。产品那一侧递的是 <c>App._wallNow</c> 每帧缓存那一份 ——
    /// 于是同一帧里托盘、状态机和遮罩看到的是同一分钟。</summary>
    private readonly Func<DateTime> _now;

    public Fader(Settings settings, int screen, Todos todos, Action todosChanged,
                 Func<DateTime> now, Action<string>? log = null)
    {
        _settings = settings;
        _todos = todos;
        _todosChanged = todosChanged;
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _log = log ?? (_ => { });

        Title = TitleOf(screen);
        Borderless = true;
        Transparent = true;
        // 置顶要在别的一切之前设完：CODING_STANDARDS.md「样式位一次设完」那一条记的就是
        // 那一发「parent 掉过一次」的嫌疑，它出在
        // 设完 top-most 又去改别的东西。规矩是样式位一次设完，之后不碰 —— 淡入这一段整个守着
        // 这条。之后只有 <see cref="Settle"/> 那一处例外，它改的也不是遮罩的样式，改的是
        // 「还要不要压着别的窗口」这个意图，而且只在窗口已经画完之后改一次。
        AlwaysOnTop = true;
        Unresizable = true;
        Unfocusable = true;
        var position = DisplayServer.ScreenGetPosition(screen);
        var size = DisplayServer.ScreenGetSize(screen);
        Position = position;
        Size = size;

        var content = GD.Load<PackedScene>("res://src/FaderContent.tscn").Instantiate() as Control
            ?? throw new InvalidOperationException("FaderContent.tscn 的根不是 Control");
        _content = content;
        _fill = content.GetNode<ColorRect>("Fill");
        _picture = content.GetNode<FaderPicture>("Picture");
        // 中间那三颗是 Content 的直接子节点：布局给的份额是绝对的一份，容器替不了。
        _clock = content.GetNode<Label>("Clock");
        _block = content.GetNode<Label>("Block");
        _remain = content.GetNode<Label>("Remain");
        // 清单在侧面那一块（2026-10-06 所有者定夺：「休息时应该在中间显示语录时间，
        // 侧面显示 todolist」）。它外面那一层 CenterContainer 是「清单内部怎么摆」，
        // 「清单摆在哪」由布局说了算 —— 锚点在 <see cref="Apply"/> 那一发按份额写。
        _side = content.GetNode<CenterContainer>("Side");
        _checklist = content.GetNode<VBoxContainer>("Side/Stack/Checklist");
        _empty = content.GetNode<Label>("Side/Stack/Empty");
        // 那颗钮在侧面那一块里，不在页脚里（2026-10-06 从页脚搬过来）：收鼠标的那一块
        // 按点得到的东西量矩形，钮和清单分处两块的话它要算住它们中间那大半块屏。
        _end = content.GetNode<CenterContainer>("End");
        _endBtn = content.GetNode<Button>("End/EndBtn");
        AddChild(content);

        _fill.Color = Rgb(settings.BackgroundColor);
        // 图要在 Show() 之前读进来，为的是「别先露出纯色底、
        // 过一会儿才补上图」。引擎读文件是同步的，所以这一条只要顺序对就白送 —— 而顺序就是
        // 这里：构造函数在 Raise() 之前，Raise() 里才 Show()。
        // 它也不跟着换档藏露（设计文档 `docs/design/routine.md`：它不是某个 step 的 content），所以这里
        // 一次画好，Enter() 那一串 Visible=false 里没有它。
        if (settings.Image.Enabled && settings.Image.File.Length > 0 &&
            _picture.Set(settings.Image.File, settings.Image.Position, _fill.Color, _log))
        {
            // 纯色底那一层让位：图那一层自己把它画上了 —— 两笔在同一层里，整窗那条 alpha
            // 斜坡才只乘一次。留着 Fill 的话两层各乘一次，合成出来是不透明度 1-(1-a)²，
            // 屏幕上那道绿会透进图里 19%（2026-09-26 拿判据一反解出来的那一发）。
            _fill.Visible = false;
        }
        var text = Rgb(settings.ForegroundColor);
        // 字底下压着亮图的时候，白字就读不出来了（2026-09-27 待办里那条「清单白字压在亮图上」）。
        // 这里不换设置里那一份颜色，只在「差得不够」时把它翻到对比的那一头：整窗那条 alpha 斜坡
        // 把字和底一起乘，桌面那一份在两者之差里抵消掉了，所以能不能读出来只看 |字 - 底| 够不够。
        text = InkAgainst(text, settings.BackgroundColor);
        _ink = text;   // 清单那几行是换档时才搭的，拿的必须是这一份换过档的，不是设置里那一份
        _m = Measure(size.Y, settings.TextScalePercent, TextScale(screen));
        _dpiPercent = DpiPercent(screen);
        // **字号不再在这里定**。2026-10-06 之前它们按屏高的几个比例写死在这一发（ClockRatio
        // 那一族），现在字号是**布局给的框的一个份额**，每一步各自一份 —— 换句话说
        // 「这一屏多大」问的不再是「屏多高」，而是「用户把那个框摆多大」。真正的算术在
        // <see cref="StepLayout.Place"/>，地板是日历那一根（<see cref="StepLayout.FontFloor"/>）。
        // 下面这几发留下的是**颜色**：颜色不是布局的一份，它是设置里那两格。
        _clock.AddThemeColorOverride("font_color", text);
        // 块名和那一步自带的文字（工单 04）。块名和健康度原本一样跟着小字那一档，
        // 健康度 2026-10-06 退休之后它就是那一族里唯一的小字。
        // 自由文字是给人读的内容，跟着清单那一行 —— 和 ADR-0005「遮罩文字正本清源」
        // 那一节的界线一致：前者是状态实测结果，后者是步自己带的内容。
        _block.AddThemeColorOverride("font_color", text);
        // 「清单是空的」那一行按清单那一行的字号，不按页脚那一种：它替的是清单那一块。
        _empty.AddThemeColorOverride("font_color", text);
        _endBtn.Text = Tr("提前结束休息");
        // 前景色得加在每一颗控件自己身上：theme 是按类型查的，加在父容器上管不到 CheckBox 和
        // Button —— 日历那一档踩过同一个坑（docs/research/godot-csharp-binding.md theme override 那一条）。
        // 这一处是 2026-09-26 第一次看遮罩那一帧读出来的：清单要的是白字白勾、那颗钮要描边，
        // 而引擎给的是默认那套深字灰底，压在绿上几乎读不出来。
        _endBtn.AddThemeColorOverride("font_color", text);
        _endBtn.AddThemeColorOverride("font_hover_color", text);
        _endBtn.AddThemeColorOverride("font_pressed_color", text);
        _endBtn.AddThemeColorOverride("font_focus_color", text);
        _endBtn.AddThemeColorOverride("font_disabled_color", text);
        _endBtn.AddThemeStyleboxOverride("normal", Outline(text, 0.55f));
        _endBtn.AddThemeStyleboxOverride("hover", Outline(text, 0.9f));
        _endBtn.AddThemeStyleboxOverride("pressed", Outline(text, 0.9f));
        _endBtn.AddThemeStyleboxOverride("disabled", Outline(text, 0.3f));
        _endBtn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _remain.AddThemeColorOverride("font_color", text);
        // 勾的那两颗图标也是：引擎默认那两张是深色的，压在遮罩上读不出来。带两张图进 pck
        // 不如照前景色现画一张 —— 它必须跟着 foregroundColor 走，也必须跟着**清单那一行的
        // 字号**走（那一方块和清单那一行的字同高），而那一行的大小是布局给的，所以这两个
        // 图标在换步那一发按 <see cref="BuildChecklist"/> 量出来的那一档重画。
        _listFont = _m.RowFont;
        _boxOff = Square(text, ticked: false, _m.TickSide);
        _boxOn = Square(text, ticked: true, _m.TickSide);
        _endBtn.Pressed += () =>
        {
            _log("按钮：Pressed 发出来了");
            EndBreakRequested?.Invoke();
        };

        // **这一扇只负责画**：鼠标穿透从头到尾是真的（见构造函数末尾那一条）。
        // 铺满的一层只要吃住命中测试，清单和那颗钮就全点不动
        // （CODING_STANDARDS.md「代码级规矩」mouse_filter 那一条踩过同一个坑）。
        _content.MouseFilter = Control.MouseFilterEnum.Ignore;

        // 鼠标穿过：六条路里实测只有这一条和手设 WS_EX_TRANSPARENT 真的让窗口不再拥有它
        // 盖住的那个点。走引擎那条。**这一扇永远是它**，从淡入到淡出、连休息流程里写着
        // 「接鼠标」的那一步也不例外 —— 2026-10-10 之后没有第二扇窗了，
        // 「收鼠标」是全局钩子按坐标判的那一刀（<see cref="Swallows"/>）。
        MousePassthrough = true;

        _content.Modulate = new Color(1, 1, 1, 0f);
        _log($"屏 {screen}：位置={position} 大小={size} 引擎答回来={Size} " +
             $"scale={DisplayServer.ScreenGetScale(screen)} dpi={DisplayServer.ScreenGetDpi(screen)} " +
             $"字号按屏高={_m} 系统量的屏={Win32.GetSystemMetrics(0)}x{Win32.GetSystemMetrics(1)} " +
             $"虚拟桌面={Win32.GetSystemMetrics(78)}x{Win32.GetSystemMetrics(79)}");
    }

    public static string TitleOf(int screen) => $"{TitlePrefix}-{screen}";

    /// 字号该乘多少。实测 ScreenGetScale() 在 125% 的屏上报 1，所以文字不跟系统缩放走 ——
    /// 那正是 textScalePercent 这个键存在的原因（docs/research/desktop-layer-and-window-measurements.md DPI 那一条）。这里把引擎愿意给的
    /// 两个数都拿来，哪个是真的由跑起来那行日志说。
    private static float TextScale(int screen)
    {
        var dpi = DisplayServer.ScreenGetDpi(screen);
        return dpi <= 0 ? 1f : dpi / 96f;
    }

    /// 这一块屏的系统 DPI，百分之一。<b>它是字号那根地板的唯一乘数</b>
    /// （<see cref="StepLayout.FontFloor"/> 乘的就是它）。
    /// 无头没有真屏幕、引擎答回 0，那里当 100 —— 不然无头里那根地板会算成 1，
    /// 自检读到的是「字小到读不出来」而不是「比例算错了」。
    internal static int DpiPercent(int screen)
    {
        var dpi = DisplayServer.ScreenGetDpi(screen);
        return dpi <= 0 ? 100 : (int)MathF.Round(dpi);
    }

    /// <summary>那一族尺寸的<b>初值</b>：清单一行的字号和勾上那颗方块的边长。
    ///
    /// 2026-09-27 之前它们是写死的像素（钟 64、清单行 20、格子 14x14），换一块屏就不成比例了；
    /// 后来换成按屏高的几个比例（<see cref="ClockRatio"/> 那一族）。<b>2026-10-06 起
    /// 遮罩上每一处字号都来自布局给的那个框</b>，这一份只剩两处读者：
    /// 勾上那两颗现画的图标（构造那一发，画的时候还没有布局）和这一份自己的自检。
    /// 换句话说屏上画出来的号一个都不从这儿出 —— 出处是 <see cref="StepLayout.Place"/>。</summary>
    public readonly record struct Metrics(int ClockFont, int RemainFont, int RowFont, int SmallFont, int TickSide);

    /// <summary>默认布局（<see cref="StepLayout.Default"/>）在这么高的一块屏上摆出来的
    /// 那几个字号。<b>初值和预览的出处，不是在屏上画的那一份</b> —— 画的时候每一处字号
    /// 都是布局给的框算出来的（<see cref="Apply"/> → <see cref="StepLayout.Place"/>）。
    /// 留这一发是因为两处还要它：构造那一发勾上那两颗现画的图标（那时还没有布局），
    /// 以及设置窗里遮罩那一块的预览（<c>Options.cs</c>，它要的是「默认布局摆出来什么样」）。
    ///
    /// 它问的是<b>默认布局</b>而不是一串写死的比例 —— 所以「回到默认布局」摆出来多大，
    /// 预览里就是多大，一个数只写一次（2026-10-06 之前这里是
    /// <c>ClockRatio</c> / <c>RemainRatio</c> / <c>SmallRatio</c> 三个数，
    /// 而默认布局那份是照这三个数倒推的，两处迟早会歪）。</summary>
    public static Metrics Measure(int screenHeightPx, int textScalePercent, float dpiScale)
    {
        var t = textScalePercent / 100f;
        // 地板那一根是日历那一根（<see cref="Calendar.MinFontAt96"/>），不是另起一个数 ——
        // 2026-10-06 之前这一发写的是 12，而设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节
        // 定的是「那根地板和日历那份是同一根」。
        var dpiPercent = (int)MathF.Round(dpiScale * 100);
        int Of(string kind) => DefaultFont(screenHeightPx, dpiPercent, kind, t);
        return new Metrics(Of(StepLayout.KindClock), Of(StepLayout.KindRemain),
            Of(StepLayout.KindChecklist), Of(StepLayout.KindBlock), Of(StepLayout.KindText));
    }

    /// 默认布局里那一种控件的字号（乘上 <paramref name="textScalePercent"/>）。
    /// **声明里没有的那种就退回清单那一档**：一个预览里多画一族不比少画一族糟，
    /// 而这里要的是「预览大致像」，屏上真正的出处是布局。
    private static int DefaultFont(int screenHeightPx, int dpiPercent, string kind, float t)
    {
        var shape = StepLayout.Default().FirstOrDefault(c => c.Kind == kind);
        var font = shape?.Place(1920, screenHeightPx, dpiPercent).FontPx ?? 0;
        return (int)(font * t);
    }

    /// <summary>清单那一行同档的字号上限，按<b>屏高</b>的份额（2026-10-08 加）。
    ///
    /// <b>这一族出过什么</b>：默认布局里清单那一格摆的是满高框（<c>HeightPercent = 100</c>），
    /// 而字号是<b>框高的一个份额</b>（<see cref="StepLayout.Place"/>）—— 于是
    /// <c>fontPercent</c> 写 12 就是屏高的 12%：1440 高的屏上清单一行 173 像素，
    /// 连着那颗钮把侧栏撑到 1193 像素高。实测（<c>FadeRun</c> 那一发，屏 2560x1440）：
    /// <c>第一行=(1501,129) (1042,463)</c>、收鼠标的那一块 <c>矩形=(1495,123) (1054,1193)</c>。
    /// 两个症状从这一个数来：字大到读不出来，而那一块按那几行的矩形算，
    /// 于是算住了屏上近三分之一，**底下的程序在那一片点不到**。
    ///
    /// <b>上限为什么按「屏高的份额」比，而不是比某一份的像素数</b>：这一族的字号是
    /// **框高的份额**，而清单那一格是满高框 —— 所以它的字号在数值上就等于屏高的百分之几。
    /// 换一块屏字号就该跟着屏高走（设计文档 `docs/design/fader-layout.md`「位置和大小存成屏宽屏高的份额」），
    /// 钉死一个像素数在别的屏上必然假红。
    ///
    /// <b>尺子是「那一步的文字」那一档</b>，不是块名：清单那一行和那一步的文字今天同档
    /// （`StepLayout.Default()` 里两者的注释都写着「清单那一行和这一步自带的文字 4.3%」），
    /// 块名是更小的一档（2.8%）—— 拿块名当尺子会把清单那一行判红（第二版就栽在这里：
    /// 尺子 40、清单修好后 57，红的是产品而那一版已经修好了）。
    /// 拿**文字**那一档算出来的号当尺子，它随屏走、随框高走，而拧清单拧不动它。
    private static int SmallestLineFontOn(int screenWidthPx, int screenHeightPx)
    {
        var shape = StepLayout.Default().FirstOrDefault(c => c.Kind == StepLayout.KindText);
        return shape?.Place(screenWidthPx, screenHeightPx).FontPx ?? 0;
    }

    /// 淡入的开始。第一帧什么都不画满，斜坡从下一帧起算 —— 那条「不许跳」要的就是这个顺序。
    public void Raise(Scheduler.StageNow now)
    {
        // 「有没有落定」要在 <see cref="Enter"/> 之前清掉：那一发要读它来决定收不收鼠标
        // （见 Enter 那段注释），而它记着上一次那一扇的答案。顺序反了的话，同一扇窗
        // 第二次被举起时，淡入的头一帧就按「已经落定」算，鼠标在遮罩还看不见的时候被收走。
        _settled = false;
        Enter(now);
        _elapsedMs = 0;
        _leaving = false;
        _firstFrameDone = false;
        _landedFired = false;
        _reportInFrames = 0;
        _clockAtMs = 0;
        RampFrames = 0;
        MaxAlphaStep = 0;
        _content.Modulate = new Color(1, 1, 1, 0f);
        _swallow = ClickRegion.None;
        // **那两位在 `Show()` 之前补**（2026-10-11 这一族量了三轮才落在这一处）：
        // 引擎的 `Window` 一进场景树原生窗就有了，`Show()` 只是让它可见 ——
        // 所以这里已经有 HWND 认得出来。
        //
        // **为什么不能留到 `Show()` 之后**（`--style` 那几发量的）：
        // 那一扇是 2560×1440，把它切成 layered 要重开一整张合成面，**同步**做在首帧前面 ——
        // 首帧因此被拖到约 294 毫秒，而斜坡按 `delta` 走，于是「举起约一帧之后」
        // alpha 读到 0.098~0.103 而不是 0.0000，契约那句「淡入淡出永远不许跳」当场破掉
        // （FadeRun「the fade does not appear in a jump」由 holds 变 DOES NOT HOLD，
        // 历史每一发都是 `alpha=0.0000`）。**去掉 `SWP_FRAMECHANGED` 也不解决**
        // （读数 0.0981）—— 贵的是那一位本身，不是那一发重算。
        ArmClickThrough();
        Show();
    }

    /// <summary>手动补上 <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>，让底下<b>别的进程</b>也收得到点击。
    ///
    /// <para><b>为什么引擎那条旗标不够</b>（2026-10-11 实测，档位对照在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c>）：
    /// <see cref="Window.MousePassthrough"/> 走的是 <c>WM_NCHITTEST</c>，
    /// 而 <c>HTTRANSPARENT</c> <b>只跨同一个线程</b>。这一扇答 -1（「不是我的」），
    /// 同一线程里下面那扇收得到，**跨进程收不到** —— 那一发就没了。
    /// 读数：接收方在另一个进程、是一扇普通顶层窗、遮罩确实盖住那一点，
    /// 连着 16 发真的左键<b>全部 0/1</b>，同布置无遮罩 1/1；
    /// 补上这两位之后 <b>37 发全部 1/1</b>，而遮罩画出来的东西<b>一个字没变</b>
    /// （同一采样点两档都读到 <c>a=0.750 (8,104,56)</c>）。
    ///
    /// <para><b>为什么必须伸手到 Win32</b>：这是根 <c>AGENTS.md</c>「引擎能做的就用引擎，
    /// 够不着的才伸到 Win32」里<b>够不着的那一档</b> —— 引擎在册的每一条 passthrough 路线
    /// 都是 <c>WM_NCHITTEST</c>，没有一条能跨进程。ADR-0003「穿透走引擎旗标」那一节
    /// 当年量的是<b>进程内</b>的命中测试，那个口径看不见这一条。
    ///
    /// <para><b>只发一次，之后不碰</b>：这一族踩过「样式位要一次设完，之后不碰」——
    /// 设完再改别的东西会让引擎把窗整个藏掉。<b>位置也只有一处能放</b>：<see cref="Raise"/>
    /// 里 <c>Show()</c> <b>之前</b>那一行 —— 之后切 layered 要重开一整张合成面，
    /// 同步拖住首帧（读数和为什么都在那里那一段）。
    private void ArmClickThrough()
    {
        var hwnd = OwnWindow();
        if (hwnd == IntPtr.Zero)
        {
            _log("穿透那两位：认不出自己的窗口，这一步没做");
            return;
        }
        var before = Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
        Win32.SetWindowLong(hwnd, Win32.GwlExStyle, (nint)(before | ClickThroughBits));
        // **不发 SWP_FRAMECHANGED**（2026-10-11 这一族当场撞的）：那一发强制重算整扇窗，
        // 而这一扇是 2560×1440 —— 首帧因此被拖到几百毫秒，而斜坡按 `delta` 走，
        // 于是「举起约一帧之后」alpha 读到 0.1027 而不是 0.0000：
        // **契约那句「淡入淡出永远不许跳」当场破掉**（FadeRun
        // 「the fade does not appear in a jump」由 holds 变 DOES NOT HOLD，
        // 历史每一发都是 `alpha=0.0000`）。那两位是扩展样式位，命中测试每一条路径都现读，
        // 不需要重算才生效 —— 跨进程那一头照旧全通（读数见下面那一段）。
        var read = Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
        _log($"穿透那两位：写之前={before:X} 读回来={read:X}（{Win32.DescribeExStyle(read)}）" +
             $"到位={ClickThroughBitsOn(read)}");
    }

    /// <summary>补上去的那两位的正本。别的族（<c>FadeRun</c> 的样式档位、自检）都问它。</summary>
    public const long ClickThroughBits = Win32.WsExLayered | Win32.WsExTransparent;

    /// <summary>一份扩展样式位上，那两位还在不在。</summary>
    public static bool ClickThroughBitsOn(long exStyle) =>
        (exStyle & ClickThroughBits) == ClickThroughBits;

    /// 开始淡出。走法不变：一个 step 都不动，只有 alpha 在动。
    public void Leave()
    {
        Disarm();
        if (_leaving) return;
        _leaving = true;
        _elapsedMs = 0;
        RampFrames = 0;
        MaxAlphaStep = 0;
        _lastAlpha = Opacity;
    }

    /// 进下一个 step：不碰窗口，只改画的东西和鼠标的用途。
    /// 收的是状态机交出来的那一份 <see cref="Scheduler.StageNow"/>，不是一个裸 <see cref="Step"/> ——
    /// 「这一步留多久」只有一个出处（<see cref="Scheduler.StageMs"/>），这一侧不再自己算第二遍。
    public void Enter(Scheduler.StageNow now)
    {
        var step = now.Step;
        // **这一扇的穿透不归这一发管**：它从头到尾都是穿过的，2026-10-10 之后也没有第二扇窗。
        // 所以「收不收鼠标」在这里只决定一件事：<see cref="_swallow"/> 那一块该是空还是量出来。
        //
        // 那一块量出来的时刻是**遮罩完全浮现之后**，不是进这一步那一刻（2026-10-06 所有者定夺，
        // 原话：「没有前 3 秒这一说！在遮罩完全浮现后可勾」）。第一步由 <see cref="Settle"/>
        // 那一帧量；换步是落定之后才发生的，那时要立刻换鼠标的用途（契约「换步不碰窗：
        // 只变画的东西和鼠标的用途」），由 <see cref="_reportInFrames"/> 那三帧之后量 ——
        // 排版要跑完才量得到真矩形。
        _step = step;   // Esc 那只手要知道现在这一步收不收鼠标（见 Interactive）
        _stepMs = now.HoldsMs ?? 0;
        _stepAtMs = _elapsedMs;
        // 屏上有什么、摆哪、摆多大：这一发**只读布局**（<see cref="Apply"/>）。
        // 换档是「换成这一档要画的」，不是「再叠一层」—— 布局里没有的那些一颗都不画。
        Apply(step);
        if (_remain.Visible) TickRemain();
        // 不收鼠标的那一步：那一块立刻清空。收鼠标的那一步等三帧（排版跑完）再量。
        if (step.Interactive) _reportInFrames = 3;
        else Disarm();
    }

    /// <summary>把这一步的<b>布局</b>摆到屏上。这是「屏上有什么」唯一的出处。
    ///
    /// 换档是**只画这些**：布局里没有的控件一律不画（2026-10-06 之前是
    /// <c>step.Content</c> 那个名单，而那一族 2026-10-06 退休了 ——
    /// 老名字由 <see cref="StepLayout.Translate"/> 读一次翻成布局，翻完它们只是陌生的键）。
    ///
    /// <b>次序就是叠的次序</b>：布局那个数组里越靠后越在上面（设计文档 `docs/design/fader-layout.md`「允许重叠，重叠时后来摆的在上」那一节），所以 <see cref="Restack"/> 照数组的次序重排子节点。
    /// <c>Fill</c> 和 <c>Picture</c> 不在里面 —— 它们是顶层 <c>image.*</c> 那一层，
    /// 永远在底下（构造函数那一发）。
    private void Apply(Step step)
    {
        // 先全收一遍：布局里没有的那些不画，而「先全收」才是「只画这些」的实现方式
        // （和今天 <see cref="Enter"/> 那六行 Visible=false 同一个做法）。
        _clock.Visible = false;
        _remain.Visible = false;
        _block.Visible = false;
        _side.Visible = false;
        _empty.Visible = false;
        _end.Visible = false;
        // 按钮跟着它外面那一层一起收（2026-10-08）：上面那一发放的是这一颗，
        // 所以这里也得收 —— 少了这一行，换到不摆那颗钮的一步时它会留在屏上。
        _endBtn.Visible = false;
        foreach (var label in _texts) label.Visible = false;
        foreach (var image in _images) image.Visible = false;
        // 换步要把上一次现搭的那几个文字/图控件还回去 —— 布局改了几个，它们就该跟着是几个。
        // 清单那几行同理（<see cref="BuildChecklist"/> 自己收自己那几行）。
        FreeSpare();

        var stacked = new List<Control>();
        foreach (var shape in step.Layout)
        {
            var placed = shape.Place(Size.X, Size.Y, _dpiPercent);
            switch (shape.Kind)
            {
                case StepLayout.KindClock:
                    _clock.Text = TimeText();
                    Put(_clock, shape, placed);
                    stacked.Add(_clock);
                    break;
                // **没有 `ms` 就没有倒计时，布局里摆没摆它都不出现**（设计文档 `docs/design/fader-layout.md`「遮罩的布局」
                // 那一节，不给用户选）。所以可见性这一问读的是 `step.Ms > 0`，
                // 和布局里有没有这个控件是两件事 —— 摆在而 `ms` 是 0 的那一份照旧不画。
                //
                // 「还剩」那个**数**读的是 HoldsMs，跟状态机的判定同源。这一行以前读裸
                // step.Ms，而 StageMs 对第一步取 max(ms, fadeInMs) —— 于是「第一步留多久」
                // 有两套公式：ms 写得比淡入还短的时候，屏幕上数到 0 了而状态机还没让位
                //（2026-09-29 之前）。按 HoldsMs 判可见性会让「第一步 ms:0」那一类休息
                // 流程新长出一行倒计时，那是改界面，不是搬家。
                case StepLayout.KindRemain:
                    if (step.Ms > 0) { Put(_remain, shape, placed); stacked.Add(_remain); }
                    break;
                case StepLayout.KindChecklist:
                    // 清单那几行的字号跟着**清单那个框**，而它们是换步时才搭的 ——
                    // 所以字号先定下来，搭行的时候才有数可拿。
                    _listFont = placed.FontPx;
                    _checklistSharePercent = (int)Math.Round(placed.WidthPercent);
                    _side.Visible = true;
                    Put(_side, shape, placed, keepBox: true);
                    BuildChecklist();
                    stacked.Add(_side);
                    break;
                // 块名是布局里**唯一动态**的一个控件：它不存任何字，此刻的名字由作息表
                // 替它算（<see cref="Tray.BlockRowName"/>：总开关 + <see cref="Routine.At"/> +
                // <see cref="TimeBlock.Name"/> 三件事在那一个方法里合成）。没有作息表、
                // 不在任何块里、或总开关关着，它答空 —— 那一档和「有名字」是同一种空，
                // 屏幕上那一行不出现（不是一行空的）。所以它是**摆着也不一定画**的那一种。
                case StepLayout.KindBlock:
                    BuildBlock();
                    if (_block.Visible) { Put(_block, shape, placed); stacked.Add(_block); }
                    break;
                // **摆不摆是布局说了算，点不点得动才是 `interactive` 说了算**（契约：
                // 「清单看得见但点不动是一种合法状态」）。2026-10-06 之前这一颗跟着
                // `interactive` 藏露，那是 `content` 时代的老规矩：那时「有这颗钮」等于
                // 「这一步收鼠标」。老配置翻成布局时（<see cref="StepLayout.Translate"/>）
                // 仍然照 `interactive` 翻，所以老休息流程看到的样子和今天一样。
                case StepLayout.KindEnd:
                    _end.Visible = true;
                    Put(_end, shape, placed, keepBox: true);
                    // **按钮自己也要放出来**（2026-10-08 补）。`Put` 放的是传进去的那一个
                    // 节点，而这里传的是**外面那一层** `CenterContainer`（`_end`）；
                    // 按钮是它的子节点，在 `FaderContent.tscn` 里写着 `visible = false`
                    // （那一棵树是**控件池**，每颗都先收着，等布局说画哪几颗）。
                    // 只放外面那一层的结果是：**框在、按钮不画** —— 屏上那一块位置留着，
                    // 而「提前结束休息」那颗钮一个字都看不见。清单那一族没栽这一发是因为
                    // `BuildChecklist` 自己放了 `_checklist` 和 `_empty`（见那两行），
                    // 这一族没有第二处放它，所以**只有它不见了**。
                    // 这正是 CODING_STANDARDS.md 取证方法论第 2 条那一族：
                    // 「在树上」不等于「画出来了」—— 框在树上、按钮也在树上。
                    _endBtn.Visible = true;
                    _endBtn.AddThemeFontSizeOverride("font_size", placed.FontPx);
                    stacked.Add(_end);
                    break;
                case StepLayout.KindText:
                    // 文字控件不限个数（设计文档 `docs/design/fader-layout.md`「文字和图不限个数」），所以它们是现搭的
                    // —— 和清单那几行同一个做法，那一族本来就现搭。
                    var words = new Label
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        // 档 2（WORD_SMART）：档 1（WORD）会把英文单词劈成两半，量到的
                        // 实测结果是「each a share of th / e work area」，档 2 在中英文底下都对
                        //（CODING_STANDARDS.md「About 那一扇的 autowrap_mode」那一条记着同一发）。
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                    };
                    words.AddThemeColorOverride("font_color", _ink);
                    words.AddThemeFontSizeOverride("font_size", placed.FontPx);
                    words.Text = Clip(shape.Text ?? "");
                    _content.AddChild(words);
                    _texts.Add(words);
                    // 摆好之后**再**按有没有字定可见 —— 顺序反了就把「摆了文字但没写内容」
                    // 那一档读成「可见的一行空字」（块名「此刻不在任何块里就空着」是同一种空）。
                    Put(words, shape, placed);
                    words.Visible = words.Text.Length > 0;
                    if (words.Visible) stacked.Add(words);
                    break;
                // 图控件装的是一个路径加五种摆法之一；**空路径就是没有图**（默认布局里
                // 那个铺满整屏的图控件路径就是空的）。摆法那五个名字和顶层 `image.position`
                // 同一份（<see cref="StepLayout.KnownFit"/>），不认得的退到默认那一档。
                case StepLayout.KindImage:
                    var file = shape.File ?? "";
                    if (file.Length > 0)
                    {
                        var image = new FaderPicture { MouseFilter = Control.MouseFilterEnum.Ignore };
                        image.Set(file, StepLayout.KnownFit(shape.Position ?? ""), _fill.Color, _log);
                        _content.AddChild(image);
                        _images.Add(image);
                        Put(image, shape, placed, keepBox: true);
                        if (image.Visible) stacked.Add(image);
                    }
                    break;
                // 认不出的种类不在这一发里 —— <see cref="StepLayout.IsKind"/> 在读文档和
                // 夹取那两处已经收掉了。这里这一行只是不让自己在一个不认识的种类上
                // 静悄悄地什么都不画（那正是「health 和 trail」2026-10-06 之前的教训：
                // 早退成 `default: return` 之后，配置里 health 排在 clock 前面就整屏空）。
                default:
                    _log($"布局：认不出的控件 {shape.Kind}，这一行不画");
                    break;
            }
        }
        Restack(stacked);
    }

    /// 把一个控件摆到布局给的那一块，并按那一块算字号。
    ///
    /// <b>锚点写份额、offset 全 0</b>：引擎按父节点的尺寸算锚点，所以换一块屏换的是画面，
    /// 不是那份布局 —— 一个数都不写死在这一发里。
    ///
    /// <paramref name="keepBox"/> 是「那一层容器不要跟着摆满」：清单那一列和那颗钮外面
    /// 那一层管的是**列内部**怎么摆（几行就多高、钮按自己的宽度居中），框是给它们的边界，
    /// 子控件要从框里自己算。
    private void Place(Control node, StepLayout.Placed placed, bool keepBox = false)
    {
        node.AnchorLeft = (float)(placed.XPercent / 100);
        node.AnchorRight = (float)((placed.XPercent + placed.WidthPercent) / 100);
        node.AnchorTop = (float)(placed.YPercent / 100);
        node.AnchorBottom = (float)((placed.YPercent + placed.HeightPercent) / 100);
        node.OffsetLeft = node.OffsetTop = node.OffsetRight = node.OffsetBottom = 0;
        node.GrowHorizontal = Control.GrowDirection.Both;
        node.GrowVertical = Control.GrowDirection.Both;
        if (keepBox) node.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
    }

    private void Put(Control node, StepLayout shape, StepLayout.Placed placed, bool keepBox = false)
    {
        Place(node, placed, keepBox);
        node.AddThemeFontSizeOverride("font_size", placed.FontPx);
        // **摆上去就是「这一档要画的」**，所以这里负责把它露出来。
        // 少了这一行的话：Apply 开头把六颗全收成 false，而 switch 里只有清单那一列
        // 和那颗钮自己写了 `Visible = true` —— 时钟、倒计时、块名摆进布局也还是
        // 收着的，屏上什么都不出来。自检里「这一步写了 ms，布局里摆的倒计时该出现」
        // 那一问查的就是这个（第一版漏了它，量出来倒计时可见=False）。
        // 块名那一颗特殊：它是「摆着也不一定画」的那一种（此刻不在任何块里就空），
        // 所以它由 <see cref="BuildBlock"/> 决定，switch 那一条里再放出来。
        if (node != _block) node.Visible = true;
    }

    /// <summary>把上一次现搭而这一步用不上的文字/图控件还回去。
    ///
    /// <b>为什么不留着重排</b>：留着的意思是「布局里删了一个控件，它还在屏上」——
    /// 而设计文档 `docs/design/fader-layout.md`「六种都能改大小，都能删」那一节明写这一条，删了就是少一样。清单那几行早就是这个做法
    ///（<see cref="BuildChecklist"/> 开头那一发 QueueFree），这一族跟着它，
    /// 而不是给文字控件单开一套「复用」的规矩。</summary>
    private void FreeSpare()
    {
        Spare(_texts);
        Spare(_images);
    }

    private static void Spare<T>(List<T> nodes) where T : Node
    {
        foreach (var node in nodes) node.QueueFree();
        nodes.Clear();
    }

    /// <summary>照布局那个数组的次序重排子节点 —— <b>次序就是叠的次序</b>。
    ///
    /// <c>Fill</c> 和 <c>Picture</c> 固定占前两个（顶层 <c>image.*</c> 那一层永远在底下），
    /// 摆上去的那些从第三个开始按数组次序排。引擎里「后来的盖住先前的」，
    /// 而 <c>MoveChild</c> 就是那一发 —— 不需要自己算 z 序再写一套绘制顺序。</summary>
    private void Restack(List<Control> stacked)
    {
        for (var i = 0; i < stacked.Count; i++) _content.MoveChild(stacked[i], i + 2);
    }

    /// <summary>字底下压着的是亮的还是暗的 —— 亮到和字只差一档，那一行就读不出来了
    /// （2026-09-27 待办里那条「清单白字压在亮图上读不出来」）。
    ///
    /// 判据是<b>差</b>不是绝对亮度：整窗那一条 alpha 斜坡把字和底一起乘，桌面透进来的那一份在
    /// 两者之差里抵消，所以屏幕上分不分得开只取决于 |字 - 底|（再乘一个常数）。图没开的时候底
    /// 就是设置里那一份纯色，那条路上这一发不会改任何东西。</summary>
    private Color InkAgainst(Color ink, string flatHex)
    {
        var flat = Rgb(flatHex);
        double sum = 0;
        var n = 0;
        // 中心那一块就是字堆着的地方（钟、清单、页脚都在这一带）；采样步长按 24 像素，
        // 一块 2560x1440 上取 60x20 = 1200 个点，够稳也不慢。
        // 摆图的那一层这时候还没排版（Size 还是 0），所以把窗的尺寸递进去。
        for (var y = Size.Y * 0.34f; y < Size.Y * 0.68f; y += 24)
            for (var x = Size.X * 0.28f; x < Size.X * 0.72f; x += 24)
            {
                var c = _picture.PixelAt(new Vector2(x, y), Size) ?? flat;
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                n++;
            }
        if (n == 0) return ink;
        var bg = (float)(sum / n);
        var fg = 0.299f * ink.R + 0.587f * ink.G + 0.114f * ink.B;
        if (MathF.Abs(fg - bg) >= 0.35f) return ink;
        var flipped = bg > 0.5f ? new Color(0.10f, 0.11f, 0.13f) : new Color(0.96f, 0.97f, 1f);
        _log($"字色换档：设置里那份 {ink} 和底下的 (平均 {bg:F2}) 差 {MathF.Abs(fg - bg):F2}，" +
             $"不够读 → 换成 {flipped}");
        return flipped;
    }

    /// <summary>清单的一行：引擎那颗 CheckBox（<b>只有方块、没有字</b>）+ 一段 RichTextLabel。
    ///
    /// 为什么不是一颗带文字的 CheckBox：勾完那一笔要划掉，而 CheckBox 不认 bbcode。
    /// 2026-09-27 他定的就是这一条（「换 RichTextLabel，并把命中给整行」）。代价是文字那一块
    /// 不再是那颗控件自己的了，所以命中搬到这一行上：点在字上、点在方块上、点在两者中间那条
    /// 缝里，都算「勾这一条」—— 判据是 <see cref="RowHit"/>，自检里数的是同一发。</summary>
    private sealed partial class CheckRow : HBoxContainer
    {
        public required CheckBox Box { get; init; }
        public required RichTextLabel Text { get; init; }
        /// 那一行本来写的是什么（不带 bbcode）。划掉是在它外面套一层 [s]，所以每次换状态
        /// 都从这一份重画，不在已经带标记的那一份上再加。
        public required string Plain { get; init; }
        /// 整行收到几次「按下且是左键」。测量程序用它证「点字 = 点勾」这一发真的走通了。
        public int RowHits;
        /// 划掉那一笔自己画：bbcode 的 [s] 要字体自带 strikethrough 那两条度量，
        /// 引擎那份默认字体没有，于是 [s] 包着的那一段什么都不会画（2026-09-27 那一发帧上
        /// 读到的：淡下去了，那一横没有）。自己画一条，粗细跟着字号走，和字体无关。
        public bool Struck;
        public Color Ink = Colors.White;
        public int Thickness = 3;

        public override void _Draw()
        {
            if (!Struck) return;
            var y = Text.Position.Y + Text.Size.Y / 2;
            DrawLine(new Vector2(Text.Position.X, y), new Vector2(Text.Position.X + Text.Size.X, y),
                Ink, Thickness);
        }
    }

    private void BuildChecklist()
    {
        // 勾上那两颗图标跟着**清单那一行的字号**走（那一方块和旁边的字同高），
        // 而那一行的大小是布局给的框算出来的 —— 所以换步要现画一次。
        // 带两张图进 pck 不如照前景色现画：它必须跟着 foregroundColor 走。
        _boxOff = Square(_ink, ticked: false, _listFont);
        _boxOn = Square(_ink, ticked: true, _listFont);
        foreach (var row in _rows) row.QueueFree();
        _rows.Clear();
        var text = _ink;   // 换过档的那一份：图开着且底下压着亮图时，它不是设置里那一份白
        for (var i = 0; i < _todos.Items.Count; i++)
        {
            var todo = _todos.Items[i];
            var box = new CheckBox();
            box.AddThemeIconOverride("unchecked", _boxOff);
            box.AddThemeIconOverride("checked", _boxOn);
            // 直接赋 ButtonPressed 会把 Toggled 发出去，那等于每次淡入都把清单重写一遍。
            box.SetPressedNoSignal(todo.Done);

            var line = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                // 命中交给整行：这一层不吃，点在字上才会冒到 CheckRow 那里去。
                MouseFilter = Control.MouseFilterEnum.Ignore,
                // 宽度自己算给自己：RichTextLabel 的最小宽度是 0，而这一列的宽度是「children 的
                // 最小宽度里最大的那个」——钟那一行 189 像素的字号把列撑到 ~500 宽，于是七字的
                // 清单在 2026-09-27 那一发帧上折成了两行。按字号量一遍那一行要多宽，量到多少
                // 就占多少；超过屏宽八成才让它折（折行用 Arbitrary，中文没有空格）。
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            };
            line.AddThemeColorOverride("default_color", text);
            // 字号要加在 RichTextLabel 自己的类型项上：<c>font_size</c> 是 Label 那一条，
            // 而 RichTextLabel 读的是 <c>normal_font_size</c> —— 给 _checklist 那一份覆盖
            // 传不到它身上（2026-09-27 那一发实物帧读出来的：方块按屏高放大了，字没有）。
            line.AddThemeFontSizeOverride("normal_font_size", _listFont);
            line.AddThemeFontSizeOverride("regular_font_size", _listFont);
            // RichTextLabel 上没有 GetFont，而 Font 上量字长那一条在 C# 绑定里的签名和 GDScript
            // 不一样（这一发不再猜第二次）——所以宽度按字宽估：CJK 一个方块字占一个 em，
            // 拉丁字母按 0.55 个算。**判据不是这个估算，是那一发实物帧上没有折行**（估算错了
            // 最坏是多占一点宽度，列宽跟着它走，不会把字切掉）。
            var wanted = todo.Text.Sum(c => c > 0x2E80 ? 1.0 : 0.55) * _listFont;
            // 折行上限是**侧面那一列的宽度**，不是屏宽八成 —— 清单 2026-10-06 搬进侧面
            // 之后列只有屏宽的 34%（左右锚点 0.62 / 0.96，见 FaderContent.tscn 那一节），
            // 还按八成算的话那几行会横着溢出屏幕右边。列宽取容器排版后的实测值；
            // 还没排版（Size 是 0）就按屏宽乘同一个比例，比例只有这一处定义。
            var column = _side.Size.X > 1 ? _side.Size.X : Size.X * _checklistSharePercent / 100f;
            line.CustomMinimumSize = new Vector2(
                (float)Math.Min(wanted + 4, column * 0.94), 0);

            var row = new CheckRow { Box = box, Text = line, Plain = todo.Text };
            row.AddChild(box);
            row.AddChild(line);
            // 方块和字之间那一条缝取一个字的四分之一（下面那颗 TickSide / 4）。
            row.AddThemeConstantOverride("separation", Math.Max(4, _listFont / 4));
            // 命中的事归 <see cref="Route"/>（钩子那一刀之后那一帧），这一行不再自己挂 GuiInput ——
            // 铺满那扇永远穿过来，点到行上的事件到不了它的视口，留着一个永不 fire 的
            // 处理函数就是留一个永远读 0 的判据。整行包住方块和字那条规矩没变：路由那一发
            // 判的是整行的矩形（见 Route），所以点字、点方块、点中间那条缝都算这一行。
            var index = i;
            box.Toggled += on =>
            {
                _todos.Items[index].Done = on;
                Dim(row, on);
                _todosChanged();
                _log($"第 {index + 1} 条{(on ? "勾上了" : "取消")}：{todo.Text}");
            };
            Dim(row, todo.Done);
            _checklist.AddChild(row);
            _rows.Add(row);
        }
        _checklist.Visible = _rows.Count > 0;
        // 一条都没有的时候那句「清单是空的」顶上（那一把键中英文两栏都在语言表里）。
        // 这一边原来整块 _checklist
        // 一藏就什么都没有了 —— 人在休息里看见的是一块纯色，不像「没用过」，像坏了。
        _empty.Text = Tr("清单是空的");
        _empty.Visible = _rows.Count == 0;
    }

    /// 当前时间块的名字（<c>content</c> 里的 <c>block</c>，工单 04）。
    ///
    /// <b>答案只有一处出处</b>：<see cref="Tray.BlockRowName"/> —— 托盘那一行显示的
    /// 是同一个方法答的同一个值，两处不可能对不上（那一族三个读法：总开关、
    /// <see cref="Routine.At"/>、<see cref="TimeBlock.Name"/>，全在那个方法里合成）。
    /// 空 = 那一行不出现，而不是一行空的。
    private void BuildBlock()
    {
        var name = Tray.BlockRowName(_settings, Routine.MinuteOfDay(_now()));
        _block.Text = name ?? "";
        _block.Visible = !string.IsNullOrEmpty(name);
    }

    /// <summary>侧面那一列占屏宽的几成 —— <b>今天那一屏的清单有多宽</b>。
    ///
    /// 2026-10-06 起清单摆哪、摆多宽由布局说了算（<see cref="StepLayout.Default"/> 里
    /// 清单那个控件的 <c>xPercent</c> / <c>widthPercent</c>），这一发留在两处：
    /// 排版还没跑的时候折行上限按它算（headless 的自检量不到真宽度），以及自检里
    /// 钉住「默认布局摆出来的列宽还是不是今天那个数」。
    /// 所以它是**今天那一屏的数**，不是「清单永远这么宽」的规矩。
    ///
    /// <b>2026-10-12 改成 0.26</b>（所有者：「休息阶段 todolist 的位置和大小太难看」）：
    /// 默认布局那一格同时从 0.62/0.96 挪到了 <c>0.70 / 0.96</c>，列宽跟着从 34% 收到 26%。
    /// 这一个数是那一格的<b>初值</b>，屏幕上摆出来的以布局为准（<see cref="StepLayout.Default"/>）——
    /// 两处不许再写歪，自检里有一问专门钉着它们一不一样。</summary>
    public const float SideShare = 0.26f;

    /// <summary>遮罩上一个文字控件的字数上限。<b>一个数，住在模型上</b>
    /// （<see cref="StepLayout.TextCap"/>，两个地方都要用它），这里不再抄第二份。
    /// 取 120 的实测结果在 <c>docs/research/images/fader-mask-text-wrap.png</c>：
    /// 2560×1440 那块屏上八成列宽是 2048px，一行放得下 33 个汉字（54 字那份折成两行，
    /// 第一行正好 33 字），所以 120 字是四行上下，正好是一段话的长度。</summary>
    public static int TextCap => StepLayout.TextCap;

    /// <summary>超过上限的那一份截到上限，末尾留一个省略号。
    ///
    /// <b>为什么在画的时候截而不是不让人写</b>：文档是手写的，绕过界面的那一份照样能进来；
    /// 而「静悄悄地少画几个字」比「明明白白一个省略号」更难发现。截的时候说清楚它截了，
    /// 自检里 <see cref="ContentSelfTest"/> 量的就是这一档。
    ///
    /// <b>按字符数，不按字节、不按显示宽度</b>：一个汉字算一个。界面上那颗输入格
    /// <c>MaxLength</c> 数的是同一个口径（引擎的 <c>LineEdit.MaxLength</c> 也是按字符），
    /// 所以「界面上打得进去的字数」和「遮罩上画得出来的字数」是同一个数。</summary>
    public static string Clip(string text)
    {
        var cap = TextCap;
        if (text.Length <= cap) return text;
        return text[..(cap - 1)] + "…";
    }

    /// 整行的命中归谁：方块、字、还是中间那条缝。三处都问一遍同一个判据 —— 都该收到
    /// 一发按下，且那一发把 <c>Done</c> 翻了面。那一行本来就是一行字加一个方块，
    /// 所以让整行包住那颗 input，点哪都算。
    public string RowHit()
    {
        if (_rows.Count == 0) return "清单是空的，这一发没东西可点";
        var row = _rows[0];
        var before = row.RowHits;
        var wasDone = _todos.Items[0].Done;
        var boxRect = OnScreen((Rect2I)row.Box.GetGlobalRect());
        var textRect = OnScreen((Rect2I)row.Text.GetGlobalRect());
        var gap = new Rect2I(textRect.Position.X - 4, textRect.Position.Y, 4, textRect.Size.Y);
        var sb = new System.Text.StringBuilder();
        foreach (var (what, rect) in new (string, Rect2I)[]
                 { ("方块", boxRect), ("字", textRect), ("缝", gap) })
        {
            var centre = new Vector2I(rect.Position.X + rect.Size.X / 2, rect.Position.Y + rect.Size.Y / 2);
            var mine = OwnerOf(centre).Mine;
            sb.Append($" {what}=({centre.X},{centre.Y}) 归遮罩={mine}");
        }
        return $"第一行 方块/字/缝 三处各问一次：{sb} 整行收到={row.RowHits - before} " +
               $"勾={wasDone}→{_todos.Items[0].Done}";
    }

    /// 勾完那一条当场退到后面去：淡下去，再划掉。划那一笔在 2026-09-27 之前只有淡 —— 那时
    /// 文字还在 CheckBox 身上，而 CheckBox 不认 bbcode；换成 RichTextLabel 之后 [s] 还是不画
    /// （默认字体没有那两条度量），所以那一横由 <see cref="CheckRow._Draw"/> 自己画。
    private void Dim(CheckRow row, bool done)
    {
        row.Modulate = done ? new Color(1, 1, 1, 0.45f) : Colors.White;
        row.Text.Text = row.Plain;
        row.Struck = done;
        row.Ink = _ink;
        row.Thickness = Math.Max(2, _listFont / 16);
        row.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_reportInFrames > 0 && --_reportInFrames == 0) ReportExit();
        if (!_firstFrameDone)
        {
            // 这一帧是把 a=0 的那张画出去，斜坡还没开始走。
            _firstFrameDone = true;
            return;
        }
        _elapsedMs += delta * 1000;

        var target = _settings.MaxOpacityPercent / 100f;
        WatchVisibility();
        if (!_leaving)
        {
            var arrived = Math.Min(1.0, _elapsedMs / _settings.FadeInMs);
            Track(target * (float)arrived);
            if (arrived >= 1.0) Settle();
            TickClock();
            return;
        }

        var left = Math.Min(1.0, _elapsedMs / _settings.FadeOutMs);
        Track(target * (1 - (float)left));
        // 只发一次。2026-09-29 之前 left >= 1.0 之后**每一帧**都发一遍，那时候唯一的订阅方
        // （FadeRun.cs）赋的是一个 bool，重复发无害；App 那一边要数「几块屏都落定了」，
        // 一个重复发的事件数不出来 —— 一块屏落定，帧数够多也能把计数凑满。
        if (left >= 1.0 && !_landedFired)
        {
            _landedFired = true;
            Landed?.Invoke();
        }
    }

    /// 凝实之后每半秒问一次「这个窗口还在不在」，一次问三口独立的：引擎说的、
    /// IsWindowVisible 说的、这个窗口自己的 WS_VISIBLE 位的。
    ///
    /// 为什么要拆三口：2026-09-25 这一发读到 OS说可见=False，可屏幕上那层绿一直还在
    /// （前后四秒两张截屏一模一样）。要么窗口真藏了而截屏在骗人，要么 IsWindowVisible
    /// 在骗人 —— 它数的是「自己和父窗口都可见」，而 WS_VISIBLE 位只数自己。
    private long _nextWatchMs;

    private void WatchVisibility()
    {
        if (!_settled || _leaving) return;
        var now = (long)Time.GetTicksMsec();
        if (now < _nextWatchMs) return;
        _nextWatchMs = now + 500;
        var hwnd = OwnWindow();
        if (hwnd == IntPtr.Zero)
        {
            _log("盯着：认不出自己的窗口了");
            return;
        }
        var style = Win32.GetWindowLong(hwnd, Win32.GwlStyle);
        var parent = Win32.GetWindowLongPtr(hwnd, Win32.GwlHwndParent);
        var hit = Win32.WindowFromPoint(new Win32.Point { X = Centre.X, Y = Centre.Y });
        _log($"盯着：引擎={IsVisible()} OS说可见={Win32.IsWindowVisible(hwnd)} " +
             $"自己的位={Win32.DescribeStyle(style)} 父/主=0x{parent.ToInt64():X} " +
             $"中心这点命中=0x{hit.ToInt64():X} 是={hit == hwnd} alpha={Opacity:F3} 要求顶层={AlwaysOnTop}");
    }

    /// 遮罩上那个钟不是装饰，休息的人要看它：一分钟不动的话，它就不是钟。
    /// 同一秒里顺手把「还剩」也走一次 —— 清单那一段没有钟，只有「还剩」，
    /// 所以这两个不能挤在同一个 `_clock.Visible` 判断后面（第一版就是这么漏的）。
    private double _clockAtMs;

    private void TickClock()
    {
        if (_elapsedMs - _clockAtMs < 1000) return;
        _clockAtMs = _elapsedMs;
        if (_clock.Visible) _clock.Text = TimeText();
        if (_remain.Visible) TickRemain();
    }

    /// 「还剩 m:ss」。基准是这一步自己的 ms 和进这一步那一刻的表针，不向状态机要数：
    /// 那一步多长是数据（flows 表里的 ms），而它真的从哪一毫秒开始只有这扇窗自己知道。
    private void TickRemain()
    {
        var left = _stepMs - (long)(_elapsedMs - _stepAtMs);
        if (left < 0) left = 0;
        var seconds = (int)(left / 1000);
        _remain.Text = $"{Tr("还剩")} {seconds / 60}:{seconds % 60:00}";
    }

    /// 淡入落定的那一次只走一遍：把「还要不要压着别的窗口」交给设置。
    ///
    /// 这里踩到一个引擎的坑，2026-09-25 实测：窗口已经 Show() 出来之后再给 <c>AlwaysOnTop</c>
    /// 赋值，引擎会把 <c>WS_VISIBLE</c> 清掉 —— 赋值前 OS说可见=True，赋值后=False，而且赋成
    /// 它本来就有的 true 也一样。窗口没了，交互那一步的点击就全落到它底下的程序上：
    /// 这就是「清单点不动」的根，不是焦点、也不是注入不通。所以让位一律不碰引擎那个旗标。
    ///
    /// <b>不是 private，因为自检要从这儿落定</b>：收鼠标的那一刻就在这一发里（见上面
    /// <see cref="Enter"/> 那段），而自检是同步的、跑不到 <see cref="_Process"/> 的斜坡。
    /// 它 private 的时候那一组只问得到「淡入途中穿过」，问不到「落定之后收」——
    /// 而后者才是这条规矩的正面。收到 internal 是这条缝，不给引擎那份工具面（见
    /// <c>docs/adr/0007</c>「测量程序和它那道缝」）。
    internal void Settle()
    {
        if (_settled) return;
        _settled = true;
        // 收鼠标的那一块在量，不在 Enter（见 Enter 那段注释）：遮罩完全浮现之后才收，
        // 淡入途中那一段打字照常用 —— 抢在斜坡还没走完时收，抢走的是人正在打字的那只手。
        // 排版到这里早就跑完了，量得到的是真矩形。
        Arm();
        // 契约里「落定之后」说的那一刻就是这一行，所以它排在 StayOnTopAfterLanding 那个
        // 早退之前 —— 那一档只决定窗还顶不置顶，不该决定声音响不响。
        Settled?.Invoke();
        if (_settings.StayOnTopAfterLanding)
        {
            _log($"凝实：{RampFrames} 帧 单帧最大跳 {MaxAlphaStep:F4} 要求顶层=True（引擎旗标没碰）");
            return;
        }
        DropTopMost();
    }

    /// HWND_NOTOPMOST 的意思就是「非顶层那一叠的顶上」，所以不需要「晚 3 帧再举一次」那一发
    /// —— 那一发追的是误诊：当时以为遮罩掉到底下，其实是窗口整个被藏了（隐藏窗口照样排在 z 序里，
    /// 所以 举回顶上=True 当时看着像是修好了）。
    private void DropTopMost()
    {
        var hwnd = OwnWindow();
        if (hwnd == IntPtr.Zero)
        {
            _log("让位：认不出自己的窗口，这条就没法做");
            return;
        }
        var ok = Win32.SetWindowPos(hwnd, Win32.HwndNotTopMost, 0, 0, 0, 0,
            Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
        // **这里不许收鼠标**（2026-10-10 真屏那一发撞到并已修）：`stayOnTopAfterLanding`
        // 默认关着，所以这一发紧跟在 `Settle` 的 `Arm` 之后跑 —— 在这里清空，刚量出来的
        // 那一块当场就没了，清单和那颗钮点不动。
        // 让位该改的只是「还要不要压着别的窗」这一个意图，而那一块不是窗：它归钩子按坐标判，
        // 底下那一扇铺满退到别人窗底下之后它照样照原样判 —— 和有窗那一版让点击窗跟着退到
        // 非置顶那一档是同一件事，不是把它收起来。
        _log($"让位：HWND=0x{hwnd.ToInt64():X} 改顶层位={ok} OS说可见={Win32.IsWindowVisible(hwnd)} " +
             $"引擎说可见={IsVisible()}\n" +
             $"      扩展={Win32.DescribeExStyle(Win32.GetWindowLong(hwnd, Win32.GwlExStyle))} 穿过={MousePassthrough}\n" +
             $"      收鼠标的那一块={_swallow}\n" +
             "      " + Win32.Above(hwnd));
    }

    /// 引擎不给我们 HWND，但自己是这个进程 —— 按进程数一遍顶层窗口、再按标题认出自己那一个。
    /// 这是根 AGENTS.md「引擎能做的就用引擎，够不着的才伸到 Win32」里那一类。
    /// 这一发原来在这里有一份私有的，<c>About</c> 那一发又加了一份 <see cref="Win32.OwnWindowNamed"/>，
    /// 两份一模一样 —— 现在只剩 Win32 那一份。
    private IntPtr OwnWindow() => Win32.OwnWindowNamed(Title);

    /// 钩子吃掉一下左键之后交给这一发：那一块是清单的哪一行、还是那颗钮。判据是
    /// <b>控件摆完版之后的矩形</b> —— 点在字上、点在方块上、点在两者中间那条缝里都算
    /// 勾这一行（那一族判据是 <see cref="RowHit"/>）。返回「这一发点到了东西没有」：
    /// 落在那一块里却没点到，那是那一块罩多了，日志里说清是哪一种。
    ///
    /// <b>调用方是 <c>App._Process</c>，不在钩子回调里</b>：那个回调挂在全机输入的
    /// 同步路径上（<see cref="detection.Activity.MouseSwallower"/>），在里面翻一颗控件的
    /// 状态就是把所有人的输入都拖慢一次。所以那一刀只判坐标、只说「吃」，勾留给下一帧。
    internal bool Route(Vector2I at)
    {
        foreach (var row in _rows)
        {
            if (!OnScreen((Rect2I)row.GetGlobalRect()).HasPoint(at)) continue;
            row.RowHits++;
            Toggle(row);
            return true;
        }
        if (OnScreen((Rect2I)_endBtn.GetGlobalRect()).HasPoint(at))
        {
            _log("按钮：钩子那一刀按在按钮上");
            EndBreakRequested?.Invoke();
            return true;
        }
        _log($"收鼠标的那一块：({at.X},{at.Y}) 落在里面但没点到东西 —— 那一块罩多了");
        return false;
    }

    /// 勾选这一件事只有这一个出处：换面就发 <c>Toggled</c>，落盘、划掉那一笔、记一行日志
    /// 都在那个信号后面（<see cref="BuildChecklist"/> 里那一发）。整行的命中归
    /// <see cref="Route"/> 一处，点方块和点字走的是同一路。
    private void Toggle(CheckRow row) => row.Box.ButtonPressed = !row.Box.ButtonPressed;

    /// 现在这一步<b>已经收走</b>鼠标没有 —— 判据是<b>那一块真的量出来了</b>，不是这一步
    /// 声明了什么。声明收鼠标的一步，淡入途中那几秒还没量（见 <see cref="Enter"/> 和
    /// <see cref="Settle"/>），所以那道闸门得跟着它走 —— 斜坡没走完就按 Esc，那一下不该把
    /// 休息收尾。「哪一步」是 <see cref="Enter"/> 存的，「收没收」是 <see cref="Settle"/> 落的。
    public bool Interactive => _step?.Interactive == true && _settled && Armed;

    /// 清单那一片此刻在屏上是什么样子。测量程序那一发读这一行：一条都没有的时候该露的是
    /// 那句「清单是空的」，不是「整块什么都没有」——后者看着像坏了，不像没用过。
    public string ChecklistMeasurement() =>
        $"行={_rows.Count} 清单块可见={_checklist.Visible} 那一句可见={_empty.Visible} 文字='{_empty.Text}' "
        + $"钮可见={_endBtn.Visible} 钮='{_endBtn.Text}' | {SwallowMeasurement()}";

    /// 交互那一步两个可点的东西在屏幕上的位置。注入要用它，人要手点也是点这两个数。
    public (Vector2I Exit, Vector2I FirstRow) InteractivePoints()
    {
        var exit = OnScreen((Rect2I)_endBtn.GetGlobalRect());
        var first = _rows.Count > 0 ? OnScreen((Rect2I)_rows[0].GetGlobalRect()) : default;
        return (CentreOf(exit), CentreOf(first));
    }

    private static Vector2I CentreOf(Rect2I r) => r.Position + r.Size / 2;

    /// 把「点得到的那几块」量出来交给钩子。矩形按清单那几行和那颗钮<b>摆完版之后</b>的矩形量。
    /// 三帧之后才量得到真值（新加的清单行会把那一竖排撑高，居中的容器要再居中一次），
    /// 所以换步那一发由 <see cref="_reportInFrames"/> 顺带量（见 <see cref="ReportExit"/>），
    /// 落定那一发直接量 —— 淡入那几秒里排版早跑完了。
    ///
    /// <b>不是 private，因为自检要问「量出来了没有」</b>：headless 的自检跑不到
    /// <see cref="_Process"/> 里那三帧，而「换步之后要重量」那一半正是这一发管的事。
    internal void Arm()
    {
        if (_step?.Interactive != true)
        {
            Disarm();
            return;
        }
        var rect = MeasureSwallowRect();
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
        {
            _log($"收鼠标的那一块：量到的矩形是空的（{rect}，钮可见={_endBtn.Visible} 行={_rows.Count}），这一发没收");
            return;
        }
        _swallow = rect;
        _log($"收鼠标的那一块={rect}（{SwallowMeasurement()}）");
    }

    /// 这一刻不收鼠标。<b>清空是空的</b>，而空矩形含不了任何点 —— 所以
    /// <see cref="Swallows"/> 对它恒答「不吃」，钩子那一刀也就不会落下。
    internal void Disarm() => _swallow = ClickRegion.None;

    /// <summary>屏幕上这一点要不要从底下程序手里拿走左键按下。
    ///
    /// <b>纯算术</b>：问的是 <see cref="_swallow"/> 含不含这一点，不问引擎、不问帧 ——
    /// 所以自检能在无头里拿几组坐标直接问它。这一发是钩子回调里唯一允许做的事
    /// （<see cref="detection.Activity.MouseSwallower"/>），别在这里加别的事。
    ///
    /// <b>internal</b>：那个坐标类型本身是 internal，public 方法拿它当参数编译器会报
    /// （<c>CS0051</c>）。读者是钩子回调（<c>App._Ready</c> 装的那一个）和
    /// <c>App.RouteSwallowed</c>，都在同一份程序集里。</summary>
    internal bool Swallows(Win32.Point at) =>
        _swallow.Size.X > 0 && _swallow.Size.Y > 0 && _swallow.HasPoint(new Vector2I(at.X, at.Y));

    /// 「点得到的那几块」该量成的那一块（屏幕坐标）。两档：
    ///
    /// **量得到的那一档**是引擎摆完版之后那几个控件自己的矩形（<see cref="OnScreen"/>
    /// 那一发同样的加法）—— 不是手算的布局：手算的那一份会过期，控件自己摆出来的不会。
    /// 两件事在这一档里：<b>掐紧</b>（多算一像素，底下的程序就少一像素点得到）
    /// 和<b>别算出屏</b>（出了屏的那一半是勾不到的）。
    ///
    /// **量不到的那一档**是排版还没跑（自检是 headless 的，挂上去就走；量出来的
    /// <c>GetGlobalRect</c> 高度是 1）。这一档退回按锚点算 —— 和
    /// <see cref="BuildChecklist"/> 里 <c>column</c> 那一发同一个做法：一个由配置算出来的数，
    /// 不依赖引擎什么时候排版。两档宁可取大的：量不到时不收，清单一点都点不动；
    /// 算大一点只多挡几像素底下的程序，而自检里那条「不许超过四分之一块屏」会兜着它。
    private Rect2I MeasureSwallowRect() =>
        MergeOf().Intersection(new Rect2I(Position.X, Position.Y, Size.X, Size.Y));

    /// 要罩的那一块，<b>没裁</b>。自检问「罩没罩住点得到的东西」问的是它 ——
    /// 裁那一步是为了别罩出屏（屏在休息中间变小的时候），而 headless 下压根没有屏可问
    /// （`DisplayServer.GetScreenCount()` 答 0，2026-10-08 重新量的，实测结果在
    /// `docs/research/click-window-headless-measurement.md`），
    /// 裁完什么都不剩，那一头归非 headless 那一发量。
    ///
    /// <b>并矩形那一段的正本在 <see cref="ClickRegion.RectOf"/></b>（工单 02）：
    /// 编辑器（<c>LayoutEditor.Clicked</c>）和产品这一侧调的是同一段算术。</summary>
    internal Rect2I SwallowMerge() => MergeOf();

    private Rect2I MergeOf()
    {
        var box = OnScreen((Rect2I)_endBtn.GetGlobalRect());
        foreach (var row in _rows) box = box.Merge(OnScreen((Rect2I)row.GetGlobalRect()));
        if (box.Size.X > 1 && box.Size.Y > 1) return ClickRegion.RectOf([box]);

        var share = _side.AnchorRight - _side.AnchorLeft;
        var width = (int)(Size.X * share);
        // 一行按两倍行高估（字号 × 1.6，中文方块字高 ≈ 0.94em 再加行距），钮和它上方
        // 那一行字按三倍行高估。这两个数只是让退路那一档有个正数，不是布局的正本 ——
        // 布局的正本是引擎摆出来的那个矩形，上面那一档量到就用它。
        var height = Math.Max(1, _rows.Count) * (_listFont * 2) + _listFont * 3 + ClickRegion.Pad * 2;
        var left = Position.X + (int)(Size.X * _side.AnchorLeft);
        var top = Position.Y + Math.Max(0, (Size.Y - height) / 2);
        return new Rect2I(left, top, width, height);
    }

    /// 「点得到的那几块」这一刻有没有量出来。自检和实拍帧读它 —— 判据是这一样，
    /// 不是「这一步声明了 interactive」（声明的那一半在 <see cref="Enter"/>，落定在
    /// <see cref="Settle"/>，中间隔着淡入那几秒）。
    public bool Armed => _swallow.Size.X > 0 && _swallow.Size.Y > 0;

    /// 「点得到的那几块」此刻是<b>那一块</b>（屏幕坐标）。
    ///
    /// <b>只有一份，不再分「想要的」和「引擎摆出来的」</b>：有窗那一版要分，是因为引擎
    /// 答回来的窗矩形和本进程算出来的意图可能不是同一块（headless 下更会量成 100%）。
    /// 这一族没有窗了，钩子读的就是本进程算出来的这一个数，差不存在了。</summary>
    public Rect2I SwallowRect => _swallow;

    /// 自检报错时要指着读到的数：清单一共几行、每行和那颗钮各自摆在哪、要罩的那一块是多大。
    /// 「罩住整块屏的 100%」那句话不指着数就只是一句结论 —— 而这一族出过两次
    /// 「结论对、测量程序不量这个」的亏。
    internal string SwallowMeasurement() =>
        $"要罩={_swallow} 屏={new Rect2I(Position.X, Position.Y, Size.X, Size.Y)} " +
        $"收没收={Armed} 行={_rows.Count} 钮可见={_endBtn.Visible} " +
        $"第一行={(_rows.Count > 0 ? OnScreen((Rect2I)_rows[0].GetGlobalRect()).ToString() : "没有")} " +
        $"钮={OnScreen((Rect2I)_endBtn.GetGlobalRect())}";

    /// 那颗钮摆完版之后的屏幕矩形。自检问「那一块罩住它没有」—— 罩不住就是
    /// 「提前结束休息」那颗点不动。
    internal Rect2I EndRectFor() => OnScreen((Rect2I)_endBtn.GetGlobalRect());

    /// 清单那几行有没有全落在 <paramref name="area"/> 里。逐行问，不用行号也不用序号 ——
    /// 加一行少一行，这一发自己跟着变。headless 里量不出东西（容器要等帧才排版，
    /// 每一行的矩形是 0），所以现在只有非 headless 那一发用得上它。
    internal bool RowsInside(Rect2I area) =>
        _rows.All(r => area.Encloses(OnScreen((Rect2I)r.GetGlobalRect())));

    /// 清单第一行排完版之后的屏幕矩形（没有行就是空矩形）。
    /// 工单 02 用它量「headless 里这一行的矩形是不是真的 0」—— 那一问原先是读来的。
    internal Rect2I FirstRowRect() =>
        _rows.Count > 0 ? OnScreen((Rect2I)_rows[0].GetGlobalRect()) : new Rect2I();

    /// <summary>清单每一行摆完版之后的屏幕矩形，按行的次序；没有行就是空的。
    ///
    /// <para><b>为什么单独问这一句</b>：测量程序要量的是「清单摆完之后每一条占多大」，
    /// 而那几行住在私有的 <c>_rows</c> 里 —— 现有成员只答得出第一行
    /// （<see cref="FirstRowRect"/>）和第一行与第二行之间那条缝（<see cref="RowGap"/>）。</para>
    ///
    /// <para><b>量的是控件自己摆出来的那个矩形</b>（<see cref="OnScreen"/> 那一发同样的加法），
    /// 不是手算的布局：新加一行会把那一竖排撑高、居中的容器要再居中一次，
    /// 手算的那一份量到的是上一帧。</para>
    ///
    /// <para><b>量不到时那一行是空矩形</b>，不是 0 宽高以外的样子：<see cref="OnScreen"/>
    /// 在尺寸为零时原样返回，那是它分「排版还没跑」和「排版跑完了」的现成办法
    /// （headless 下容器要等帧才排版，见 <see cref="RowsInside"/> 那段）。</para></summary>
    internal IReadOnlyList<Rect2I> RowRects() =>
        _rows.Select(r => OnScreen((Rect2I)r.GetGlobalRect())).ToList();

    /// 清单行与行之间那条缝有多宽（行框高 − 字高）。没有两行就答 0。
    ///
    /// **为什么单独问一句**：那一块多算进去的一圈（<see cref="ClickRegion.Pad"/>）
    /// 就是为了让这条缝点得到 —— 缝底下那一格看着像清单，点下去却被 Keeper 吃了鼠标。
    /// 所以那一圈该多大由这一条说了算，判据要能从**行自己**量出来，
    /// 不能拿 `Pad` 反推（那就成了拿被量的数当尺子）。
    internal int RowGap()
    {
        if (_rows.Count < 2) return 0;
        var first = OnScreen((Rect2I)_rows[0].GetGlobalRect());
        var second = OnScreen((Rect2I)_rows[1].GetGlobalRect());
        var gap = second.Position.Y - first.End.Y;
        return Math.Max(0, gap);
    }

    /// 出口的位置要等排版跑完几帧才是真的（新加的清单行会把那一竖排撑高，居中的容器要再
    /// 居中一次），所以这一发在收了鼠标的那个 step 之后第三帧；它报的是屏幕坐标：手要点它，
    /// 「这个点归谁」那条测量程序也要用它。<b>「点得到的那几块」也在这一发量出来</b> ——
    /// 换步是落定之后才发生的，那时它要立刻换上收鼠标的用途，而它的矩形同样要等排版。
    private void ReportExit()
    {
        Arm();
        var exit = OnScreen((Rect2I)_endBtn.GetGlobalRect());
        var first = _rows.Count > 0 ? OnScreen((Rect2I)_rows[0].GetGlobalRect()) : default;
        var hwnd = OwnWindow();
        var hit = Win32.WindowFromPoint(new Win32.Point { X = Centre.X, Y = Centre.Y });
        var root = Win32.GetAncestorRoot(hit);
        _log($"出口：按钮=({exit.Position.X},{exit.Position.Y})+({exit.Size.X}x{exit.Size.Y}) " +
             $"第一格=({first.Position.X},{first.Position.Y})+({first.Size.X}x{first.Size.Y})\n" +
             $"      点：中心={Centre} 命中=0x{hit.ToInt64():X} '{Win32.ClassName(hit)}' " +
             $"顶层=0x{root.ToInt64():X} '{Win32.WindowText(root)}' 是遮罩自己={root == hwnd} " +
             $"遮罩 HWND=0x{hwnd.ToInt64():X} 遮罩可见={Win32.IsWindowVisible(hwnd)} 穿过={MousePassthrough}\n" +
             $"      {SwallowMeasurement()}");
    }

    /// 控件的全局矩形是窗口内的坐标，加窗口位置才是屏幕上的位置。
    private Rect2I OnScreen(Rect2I local) =>
        local.Size == Vector2I.Zero ? local : new Rect2I(local.Position + Position, local.Size);

    /// 屏幕上一个点归谁。注入之前先问这一句：点错地方的整个实验就是脏的，而且那一下会真的
    /// 点到那个别的窗口上去（DPI 那一类的假实测结果这个项目里已经上过一次当）。
    /// <b>命中判定本体在 <see cref="DesktopLayer.OwnerAt"/></b>（卡片 04 · S20，owner-of
    /// 只有一处）；这一发只做转调和拼字，`mine` 仍按「根窗 == 我们自己那扇」算。</summary>
    public (bool Mine, string Measurement) OwnerOf(Vector2I at)
    {
        var own = OwnWindow();
        var o = DesktopLayer.OwnerAt(own, new Win32.Point { X = at.X, Y = at.Y });
        // 2026-10-10 之后本进程**只有一扇窗，而它一辈子穿过** —— 所以这一问恒答「不是我们的」。
        // 它留着是因为测量程序 `--clicks` 那一发打点之前要问一句「会不会点错地方」，
        // 而那个闸门现在问的是 <see cref="Swallows"/>：落在那一块里的点由钩子接走，
        // 不落在里面的点才会真的落到别人的窗上（所以那一发的取消条件换成了它，
        // 见 <c>Shots.Inject</c>）。
        var mine = o.Root == own;
        return (mine, $"({at.X},{at.Y}) 归 0x{o.Root:X} '{Win32.WindowText(o.Root)}' 命中测试上是我们的={mine} " +
                      $"（遮罩 0x{own.ToInt64():X} 永远穿过）Keeper 按坐标接走={Swallows(new Win32.Point { X = at.X, Y = at.Y })}");
    }

    private void Track(float alpha)
    {
        if (RampFrames > 0) MaxAlphaStep = MathF.Max(MaxAlphaStep, MathF.Abs(alpha - _lastAlpha));
        _lastAlpha = alpha;
        _content.Modulate = new Color(1, 1, 1, alpha);
        RampFrames++;
    }

    private string TimeText()
    {
        var now = _now();
        return _settings.Clock24Hour ? now.ToString("HH:mm") : now.ToString("h:mm tt");
    }

    /// 那颗「提前结束休息」是描边而不是实心按钮：里面是空的，只有一圈比底色淡一点的边。
    /// 引擎默认那套是灰底深字 —— 压在遮罩上既不是这个样子也读不出来。
    /// <c>strength</c> 是那条边吃多少前景色：平时淡、悬停和按下实。
    private static StyleBoxFlat Outline(Color color, float strength)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = new Color(color.R, color.G, color.B, color.A * strength),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(0);
        style.ContentMarginLeft = 16;
        style.ContentMarginRight = 16;
        style.ContentMarginTop = 8;
        style.ContentMarginBottom = 8;
        return style;
    }

    /// <summary>清单上那颗勾：<b>没勾是圆角描边的空框，勾上之后框填满、上面浮一道白</b>。
    /// 现画而不带资源，是因为这两张必须跟着 <c>foregroundColor</c> 走，而那是个设置。
    ///
    /// <b>2026-10-12 改的形状</b>（所有者：「控件也太难看」）。改之前是<b>实心方块</b>，
    /// 直角、占满、没勾和勾上的差别只在填充深浅上 —— 在一块半透明遮罩上，那两个状态
    /// 远看是同一块白砖。改成描边之后两档一眼分得开，形状也退到背景前面去。
    ///
    /// <b>为什么没勾那一档还留一层淡底</b>（前景色的 16%）：纯描边那一圈在亮底图上是隐形的，
    /// 而遮罩那一层是半透明的，底下压的图是不可预知的 —— 这一层淡底是给亮底图留的。
    /// <b>圆角和线宽都按 <c>side</c> 走</b>，不写死像素数：换一块屏、拧一次字号，同一个形状。
    /// 描边那一圈用有符号距离算，边界那半像素顺手当锯齿压掉，斜边才不毛。
    /// </summary>
    /// <summary>勾的那两颗图标现画一张：照前景色，且跟着清单那一行的字号一起大 ——
    /// 那一方块要和它旁边的字同高，所以 <c>side</c> 取那一行的字号，
    /// 不是引擎默认那 16 像素。</summary>
    private static ImageTexture Square(Color color, bool ticked, int side)
    {
        side = Math.Max(8, side);
        var img = Image.CreateEmpty(side, side, false, Image.Format.Rgba8);
        img.Fill(Colors.Transparent);
        var mid = side / 2f;
        var round = side * 0.22f;      // 圆角半径
        var stroke = Math.Max(2f, side * 0.085f);   // 描边那一圈的宽
        var fill = ticked ? color : new Color(color.R, color.G, color.B, color.A * 0.16f);
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var d = RoundRect(x + 0.5f, y + 0.5f, mid, mid, mid, mid, round);
            // 到形状边界的距离：<=0 在里面，>0 在外面。拿它和半个像素比一比就是覆盖率，
            // 那一圈斜边因此不毛边（原来那一版是逐像素填的，放大之后全是台阶）。
            var cover = ticked
                ? Math.Clamp(0.5f - d, 0f, 1f)
                : Math.Clamp(Math.Min(0.5f - d, d + stroke + 0.5f), 0f, 1f);
            if (cover > 0f) img.SetPixel(x, y, new Color(fill.R, fill.G, fill.B, fill.A * cover));
        }
        if (!ticked) return ImageTexture.CreateFromImage(img);

        // 那一笔勾按 16 像素那一张的比例放大（(3,8)→(5,11)→(11,5) 那几个点）。
        var k = side / 16f;
        void Dot(float x, float y)
        {
            var (px, py) = (x * k, y * k);
            var half = Math.Max(0.75f, 0.9f * k);
            for (var yy = 0; yy < side; yy++)
            for (var xx = 0; xx < side; xx++)
            {
                var dx = xx + 0.5f - px;
                var dy = yy + 0.5f - py;
                var hit = Math.Clamp(half + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                if (hit <= 0f) continue;
                var was = img.GetPixel(xx, yy);
                img.SetPixel(xx, yy, new Color(
                    was.R + (1f - was.R) * hit, was.G + (1f - was.G) * hit,
                    was.B + (1f - was.B) * hit, MathF.Max(was.A, hit)));
            }
        }
        for (var i = 0; i < 3; i++) Dot(3 + i, 8 + i);
        for (var i = 0; i < 7; i++) Dot(5 + i, 10 - i);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>点 (<paramref name="x"/>, <paramref name="y"/>) 到那个圆角矩形的<b>有符号距离</b>：
    /// 在里面是负的（越深越负），在外面是正的，边界上正好是 0。</summary>
    private static float RoundRect(float x, float y, float cx, float cy, float hw, float hh, float r)
    {
        var qx = MathF.Abs(x - cx) - (hw - r);
        var qy = MathF.Abs(y - cy) - (hh - r);
        var ax = MathF.Max(qx, 0f);
        var ay = MathF.Max(qy, 0f);
        return MathF.Sqrt(ax * ax + ay * ay) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    /// #RRGGBB。声明表里 HexColor 那条已经保证了长度和前缀，这里只把它变成颜色。
    public static Color Rgb(string html) => new(        Convert.ToInt32(html.Substring(1, 2), 16) / 255f,
        Convert.ToInt32(html.Substring(3, 2), 16) / 255f,
        Convert.ToInt32(html.Substring(5, 2), 16) / 255f);

    /// <summary>自检里那一组：<b>布局</b>摆出来的那一屏（设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」）。
    ///
    /// <b>名字还叫 <c>ContentSelfTest</c>，问的已经不是 <c>content</c> 了</b>：
    /// 那一族 2026-10-06 退休，而 <c>Probe.cs</c> 按这个名字挂这一发（它不归这一侧改）。
    /// 所以名字留着，**问的东西换了**：原来是「<c>content</c> 里勾了什么」，
    /// 现在是「这一步的布局里摆了什么、摆出来是什么样」。
    ///
    /// <b>真起一扇遮罩</b>、走真的 <see cref="Enter"/>，然后读控件自己身上的文字和
    /// 可见性 —— 判据建在离事实最近的那一侧（<c>CODING_STANDARDS.md</c> 第 2 条：「在树上」
    /// 不等于「画出来了」，这里问的正是后者）。
    ///
    /// <b>只碰内存</b>：不读也不写任何文件，作息表那张表是现造的一行 <see cref="TimeBlock"/>，
    /// %APPDATA% 一个字节都不动。
    ///
    /// <b>每一档都配一条对照</b>（一定该通）：同一个控件摆出来时它必须可见 ——
    /// 不然上面那些「该空的都空了」也可能只是「那一颗从来没被接上」。
    ///
    /// <b>读数走 <see cref="SwallowMeasurement"/> 出去，不混进问题表</b>：那一行量的是
    /// 「headless 到底量得到什么」，它不是「有问题」。混进 `problems` 会让退出码永远非 0 ——
    /// 而退出码是这一个仓库里唯一决定门的那一位（`SelfCheck.ExitCode`）。
    /// `SelfCheck` 的 `suffix` 那一档就是为它留的（`wallpaper-source` 那一组在用）。
    private static string _probeReading = "";

    /// 现问实测结果：headless 下那一块量得到什么、量不到什么（工单 02 重新量的那一发）。
    /// 完整的实测结果与结论在 <c>docs/research/click-window-headless-measurement.md</c>。
    /// **不叫 `SwallowMeasurement`**：那一条是实例成员（01 收进 `Hooks` 的那两条之一），
    /// 它报的是「这一块要算多大」；这一条报的是「这一族在 headless 下量得到什么」。
    public static string ClicksProbeReading() =>
        $"headless 重新去量：{_probeReading}";

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOnHost Content = new(
        () => "遮罩上的文字与块名通过：勾了文字就原样画（带换行也照原样）、超上限截到上限并留省略号、" +
              "正好在上限上不动；勾了块名就画此刻的时间块名，不在块里 / 没有表 / 总开关关着时那一行不出现；" +
              "时钟问的是构造递进来的假钟，同一假时刻在 24 小时制两档下是两句不同的话；" +
              "content 里没有的那一个，两颗都不露；中间那一列的次序是 钟 → 倒计时 → 块名 → 语录、" +
              "倒计时居中，而它那一档字号比钟大（1440 和 2560 两块屏上同一个比例，小字那一档不许跟着一起放大）；" +
              "健康度和休息足迹那一族已经不在树上，而写了这两个成员名的老配置照旧读得进来、不报错",
        "遮罩上的文字与块名", ContentSelfTest, ClicksProbeReading);

    public static List<string> ContentSelfTest(Node parent)
    {
        _probeReading = "";
        var problems = new List<string>();
        // 假钟（卡片 02）：这一组不读现实时间 —— 块名表按这个假时刻造，遮罩问的也是同一份假钟。
        // 可变：时钟那几条判据要拧它的时刻。
        var fakeNow = new DateTime(2026, 1, 1, 9, 30, 0);
        var minute = Routine.MinuteOfDay(fakeNow);
        // 块名从这一组自己造的表里取（不手抄）：作息表在内存里，没有文件可读。
        var settings = new Settings();
        settings.Blocks =
        [
            new TimeBlock
            {
                StartMinute = minute, EndMinute = (minute + 1) % Routine.MinutesPerDay,
                Name = "自检里那一块", Fade = true, YieldsToInput = true, Flow = "",
            },
        ];
        settings.RoutineEnabled = true;
        var todos = new Todos();

        Fader? fader = null;
        try
        {
            fader = new Fader(settings, 0, todos, () => { }, () => fakeNow);
            parent.AddChild(fader);

            // 「摆出来看」的那一个控件：拿**默认布局**里那一种的份额（今天这一屏），
            // 需要改的地方在那个闭包里改。所以自检量的是「默认布局摆出来是什么样」，
            // 而不是一份另抄的数（判据里存一份会腐烂的副本是
            // CODING_STANDARDS.md 取证方法论第 6 条那一条病）。
            StepLayout Shape(string kind, Action<StepLayout>? edit = null)
            {
                var shape = StepLayout.Default().FirstOrDefault(c => c.Kind == kind)?.Clone()
                    ?? new StepLayout { Kind = kind };
                edit?.Invoke(shape);
                return shape;
            }

            (bool Text, string TextOn, bool Block, string BlockOn, bool Remain) Show(
                int ms, params StepLayout[] controls)
            {
                // 一步新的：别的字段全走默认值，只有布局是这一发摆的。
                var one = new Step { Ms = ms, Layout = [.. controls] };
                // StageNow 那一组量的是「还剩」和进步那一刻，和测量程序那一发用同一个构造器。
                fader!.Enter(new Scheduler.StageNow(one, ms > 0 ? ms : null, 0, 1, true));
                var text = fader._texts.FirstOrDefault();
                return (text?.Visible ?? false, text?.Text ?? "",
                        fader._block.Visible, fader._block.Text, fader._remain.Visible);
            }

            void Not(string what, bool ok,
                (bool Text, string TextOn, bool Block, string BlockOn, bool Remain) got)
            {
                if (!ok)
                    problems.Add($"{what}（文字 可见={got.Text} '{got.TextOn}' / 块名 可见={got.Block} " +
                                 $"'{got.BlockOn}' / 倒计时 可见={got.Remain}）");
            }

            var sample = "今天只写完这一节。";

            // ---- 对照先行：两个控件都摆出来时都该露 ---------------------------------
            var bothOn = Show(0, Shape(StepLayout.KindText, s => s.Text = sample),
                                  Shape(StepLayout.KindBlock));
            if (!bothOn.Text) problems.Add("对照不成立：文字那一颗接都没接上，后面那些「该空」都不算数");
            if (!bothOn.Block) problems.Add("对照不成立：块名那一颗接都没接上，后面那些「该空」都不算数");
            Not("对照：布局里摆了文字和块名，那两颗都该在屏上", bothOn.Text && bothOn.Block, bothOn);

            // ---- 文字：原样画、该空的空着 ------------------------------------------------
            if (bothOn.TextOn != sample)
                problems.Add($"文字该原样是「{sample}」，屏上是「{bothOn.TextOn}」");

            var noText = Show(0, Shape(StepLayout.KindBlock));
            Not("布局里没有文字，那一颗还露着 —— 换档是「换成这一档要画的」", !noText.Text, noText);

            var blank = Show(0, Shape(StepLayout.KindText, s => s.Text = ""));
            Not("摆了文字但没写内容，那一颗该是空的",
                !blank.Text && blank.TextOn.Length == 0, blank);

            // 换行照原样：不替用户改写（折行是引擎那一档 autowrap 做的事，不是这里替它折）。
            var two = "第一行\n第二行";
            if (Show(0, Shape(StepLayout.KindText, s => s.Text = two)).TextOn != two)
                problems.Add("带换行的那一份没原样画出来");

            // **文字不限个数**（设计文档 `docs/design/fader-layout.md`「文字和图不限个数」）：摆两个就该有两个节点，
            // 而且字各归各的 —— 不许第二个盖掉第一个。
            Show(0, Shape(StepLayout.KindText, s => s.Text = "第一段"),
                       Shape(StepLayout.KindText, s => s.Text = "第二段"));
            if (fader!._texts.Count != 2 || fader._texts[0].Text != "第一段" ||
                fader._texts[1].Text != "第二段")
                problems.Add($"布局里摆了两个文字控件，屏上有 {fader._texts.Count} 个，" +
                             $"第一个「{fader._texts.FirstOrDefault()?.Text}」第二个「{fader._texts.ElementAtOrDefault(1)?.Text}」" +
                             " —— 文字控件不限个数，第二个也不许盖掉第一个");
            // 反过来：摆三个撤成两个之后不留着第三个（「六种都能改大小，都能删」）。
            var spare = fader._texts.Count;
            Show(0, Shape(StepLayout.KindText, s => s.Text = "只剩一个"));
            if (fader._texts.Count != 1 || fader._texts.Count > spare)
                problems.Add($"布局里删掉一个文字控件之后屏上还剩 {fader._texts.Count} 个 —— 删了就是少一样");

            // 上限：截到那一个数，末尾一个省略号。
            var cap = TextCap;
            var over = new string('字', cap + 30);
            var clipped = Show(0, Shape(StepLayout.KindText, s => s.Text = over)).TextOn;
            if (clipped.Length != cap)
                problems.Add($"{over.Length} 字截完是 {clipped.Length} 个字符，该是上限 {cap}");
            if (!clipped.EndsWith("…", StringComparison.Ordinal))
                problems.Add("截完的那一份末尾没有省略号");
            // 对照：正好在上限上，一个字都不动（截断只该在真的超出去的时候发生）。
            var exact = new string('字', cap);
            if (Show(0, Shape(StepLayout.KindText, s => s.Text = exact)).TextOn != exact)
                problems.Add("正好等于上限的那一份被动了 —— 截断只在超出去的时候发生");

            // ---- 块名：此刻的块名；空着的三档 ------------------------------------------
            if (bothOn.BlockOn != settings.Blocks[0].Name)
                problems.Add($"屏上的块名是「{bothOn.BlockOn}」，该是「{settings.Blocks[0].Name}」");

            var noBlockShape = Show(0, Shape(StepLayout.KindText, s => s.Text = sample));
            Not("布局里没有块名，那一颗还露着", !noBlockShape.Block, noBlockShape);

            // 「此刻不在任何块里」：把那张表挪到别处（纯内存，文档一个字没动）。
            settings.Blocks[0].StartMinute = (minute + 5) % Routine.MinutesPerDay;
            settings.Blocks[0].EndMinute = (minute + 9) % Routine.MinutesPerDay;
            var outside = Show(0, Shape(StepLayout.KindBlock));
            Not("此刻不在任何块里，那一颗该是空的 —— 「空着」是不出现，不是一行空的",
                !outside.Block && outside.BlockOn.Length == 0, outside);
            settings.Blocks[0].StartMinute = minute;
            settings.Blocks[0].EndMinute = (minute + 1) % Routine.MinutesPerDay;

            // 压根没有表。
            var table = settings.Blocks;
            settings.Blocks = [];
            var none = Show(0, Shape(StepLayout.KindBlock));
            Not("压根没有作息表，块名那一颗该是空的", !none.Block, none);
            settings.Blocks = table;

            // 总开关关着 —— 「这张表要不要用」的那一半（问法和托盘那一行同一个方法）。
            settings.RoutineEnabled = false;
            var off = Show(0, Shape(StepLayout.KindBlock));
            Not("作息表总开关关着，块名那一颗该是空的", !off.Block, off);
            settings.RoutineEnabled = true;

            // 对照（一定该通）：上面那几档都问完，把开关拧回来同一发必须重新露出来 ——
            // 不然「该空的都空了」也可能只是「它一次都没露过」。
            var back = Show(0, Shape(StepLayout.KindBlock));
            Not("对照：开关拧回来之后块名重新出现",
                back.Block && back.BlockOn == settings.Blocks[0].Name, back);

            // ---- 时钟：假时刻 15:05 的两档（卡片 02）------------------------------------
            // 以前时钟直接读现实时间，判据钉不住格式 —— 下午三点还是凌晨，屏幕说了算。
            // 现在它问构造递进来的假钟：Clock24Hour 开着是「15:05」，关着以「3:05」开头
            //（上午/下午那个词随语言走，不钉死它），而两档必须不是同一句 —— 同一句就是
            // 那一格旗标压根没被读。
            var clockWas = settings.Clock24Hour;
            fakeNow = new DateTime(2026, 1, 1, 15, 5, 0);
            settings.Clock24Hour = true;
            Show(0, Shape(StepLayout.KindClock));
            var clock24 = fader!._clock.Text;
            settings.Clock24Hour = false;
            Show(0, Shape(StepLayout.KindClock));
            var clock12 = fader._clock.Text;
            settings.Clock24Hour = clockWas;
            // 假钟拧回块名那几条用的 09:30（作息表那张表就是照它造的）。
            fakeNow = new DateTime(2026, 1, 1, 9, 30, 0);
            if (clock24 != "15:05")
                problems.Add($"时钟 Clock24Hour 开着该是「15:05」，屏上是「{clock24}」");
            if (!clock12.StartsWith("3:05", StringComparison.Ordinal))
                problems.Add($"时钟 Clock24Hour 关着该以「3:05」开头，屏上是「{clock12}」");
            if (clock24 == clock12)
                problems.Add("时钟两档是同一句 —— clock24Hour 那一格没被读");

            // ---- 倒计时：没有 `ms` 就没有它，布局里摆没摆都一样 ---------------------
            // 设计文档 `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节写死了这一条不给用户选，
            // 所以这一问量的是**布局摆着而 `ms` 是 0** 那一档 —— 那是最容易写错的一档：
            // 摆了就画是顺手的，而「没有到期时刻就没有倒计时」才是契约的话。
            var noMs = Show(0, Shape(StepLayout.KindRemain));
            Not("这一步没有 ms（没有到期时刻），布局里摆的倒计时不该出现",
                !noMs.Remain, noMs);
            var withMs = Show(60_000, Shape(StepLayout.KindRemain));
            Not("这一步写了 ms，布局里摆的倒计时该出现", withMs.Remain, withMs);
            var msNoShape = Show(60_000);
            Not("这一步写了 ms 而布局里没摆倒计时，不许自己长出那一行", !msNoShape.Remain, msNoShape);

            // ---- 声明表那一族：布局里那七种 -----------------------------------------
            //
            // **这一问原来问的是 `content` 的可选值里有没有 text / block**
            //（2026-10-06 之前 `content` 是权威）。那一族退休之后同一件事要问的是
            // **布局的声明**：`kind` 那一条的可选值必须认得遮罩上画的那七种，
            // 而 `steps[].layout` 那个键必须声明过 —— 不然这一步的屏上摆什么
            // 在文档里读不回来，「老配置自动长出一份布局」那条就无从谈起。
            var choices = Settings.Decl(StepLayout.KeyPrefix + "kind").Choices;
            foreach (var want in StepLayout.Kinds)
                if (Array.IndexOf(choices, want) < 0)
                    problems.Add($"{StepLayout.KeyPrefix}kind 的可选值里没有 {want} —— " +
                                 "遮罩上摆它的那一档永远走不到");
            if (choices.Length != StepLayout.Kinds.Length)
                problems.Add($"布局的 kind 认得 {StepLayout.Kinds.Length} 种，而声明表里写了 {choices.Length} 个可选值 —— " +
                             "两份名单对不上（模型那一份是遮罩画的那一族，声明表那一份是文档认得的）");
            if (!Settings.Declared(Step.KeyPrefix + Step.LayoutKey))
                problems.Add($"{Step.KeyPrefix}{Step.LayoutKey} 没有声明 —— 这一步屏上摆什么在文档里读不回来");
            // 老成员名照旧**认得**（老配置照旧读得进来、翻得出布局，那是
            // StepLayout.Translate 那一份的事），但**不在布局里给它们留位置**。
            // 这一条查的是那七个名字本身，不是查某一处分支 —— 节点留着而没人画，
            // 那是一份摆在那儿骗人的布局。
            foreach (var retired in new[] { "health", "trail" })
                if (Array.IndexOf(StepLayout.Kinds, retired) >= 0 ||
                    Array.IndexOf(choices, retired) >= 0)
                    problems.Add($"布局里给 {retired} 留了位置 —— 健康度和休息足迹 2026-10-06 从界面上撤了，" +
                                 "认得但不变成控件");

            // ---- 「提前结束休息」那颗钮：摆了就画，不摆就收（2026-10-08 补）----------
            // **这一族为什么原先一条判据都没有**：上面那几条问的是「那一块罩没罩住那颗钮」，
            // 而那一问量的是**矩形**（`EndRectFor` / `SwallowMerge`），不是画没画 ——
            // 一颗 `visible = false` 的按钮照样有矩形、照样被算住，那几条全绿，
            // 而屏上那颗钮一个字都看不见。**这就是取证方法论第 2 条那一条**：
            // 「在树上」不等于「画出来了」。实测（`FadeRun` 那一发）：
            // `钮可见=False 钮=(1587, 820), (128, 55)` —— 框在、位置对、被算住，
            // 就是不画。
            Show(60_000, Shape(StepLayout.KindEnd));
            if (!fader!._end.Visible)
                problems.Add("布局里摆了「提前结束休息」那一颗，外面那个框没收起来");
            if (!fader._endBtn.Visible)
                problems.Add("布局里摆了「提前结束休息」那一颗，按钮自己不画 —— " +
                             "外面那个框在树上、那一块也罩住了它，而屏上那颗钮看不见");
            // 对照（一定该通）：不摆那一颗时按钮必须收掉 —— 否则换步会把它留在屏上。
            Show(60_000, Shape(StepLayout.KindClock));
            if (fader._end.Visible || fader._endBtn.Visible)
                problems.Add($"布局里没有「提前结束休息」那一颗，它还留着（框={fader._end.Visible} " +
                             $"按钮={fader._endBtn.Visible}）—— 换步该收掉它");

            // ---- 清单那一块：摆在那儿，换档时跟着收 ------------------------------------
            // 这几条钉的是**布局说了算**的两件事：**摆了清单那一块才在**（不摆就整块收掉，
            // 不留空框），而「清单是空的」那一行跟着清单一起显隐 —— 它替的是清单那一块。
            Show(0, Shape(StepLayout.KindChecklist));
            if (!fader!._side.Visible)
                problems.Add("布局里摆了清单，侧面那一块没收起来 —— 布局退回中间那一竖排去了");
            // **列宽是今天那个数**（2026-10-06 拍帧抓到的那一条）：侧栏那一对锚点是用
            // offset_* 摆的时候，`offset_left = -0.34` 想表达「屏宽的 34%」而 offset 是像素，
            // 那一列塌成负 0.34 像素宽 —— 清单那几行「可见」却一格没画出来，而上面那条
            // 藏露判据照样是绿的（「在树上」不等于「画出来了」）。
            //
            // **量锚点，不量排版后的矩形**：`GetGlobalRect` 要等布局跑完才有真值，
            // 而自检是 headless 的、这一组只挂上去就走（没等几帧），量出来的高度是 1
            // —— 那不是「这一列塌了」，是还没排版。要问的是「这一列在屏幕上有没有宽度」，
            // 而那一列的宽度就是 `AnchorRight - AnchorLeft` 乘屏宽：一个**由配置算出**的数，
            // 不依赖引擎什么时候排版。反向验证过：把那两个锚点捏成一样，这一发当次就报。
            var share = fader._side.AnchorRight - fader._side.AnchorLeft;
            if (share <= 0)
                problems.Add($"清单那一块的左右锚点算出的宽度是屏宽的 {share:P1} —— " +
                             $"那一块是塌的（左 {fader._side.AnchorLeft} 右 {fader._side.AnchorRight}），"
                             + "清单那几行可见却画不出来");
            else if (Math.Abs(share - SideShare) > 0.001f)
                problems.Add($"清单那一块占屏宽 {share:P1}，而默认布局摆的是 {SideShare:P1} —— " +
                             "「回到默认布局」和今天那一屏对不上");
            // 那一块得真的在屏幕内，别横着伸出窗外（左右缘都要是屏宽里的比例）。
            if (fader._side.AnchorRight > 1.001f || fader._side.AnchorLeft < -0.001f)
                problems.Add($"清单那一块的锚点落在屏外（左 {fader._side.AnchorLeft} 右 {fader._side.AnchorRight}）");
            Show(0, Shape(StepLayout.KindClock));
            if (fader._side.Visible)
                problems.Add("布局里没有清单，侧面那一块还露着 —— 换档只该画这一档要画的");
            // 对照：清单空的时候该露的是「清单是空的」那一行（BuildChecklist 里那一句）。
            var emptySide = new Todos();
            var fader2 = new Fader(settings, 0, emptySide, () => { }, () => fakeNow);
            parent.AddChild(fader2);
            fader2.Enter(new Scheduler.StageNow(
                new Step { Layout = [Shape(StepLayout.KindChecklist)] }, null, 0, 1, true));
            if (!fader2._side.Visible || !fader2._empty.Visible)
                problems.Add($"清单一条都没有时该露「清单是空的」那一行（侧面可见={fader2._side.Visible} " +
                             $"那一行可见={fader2._empty.Visible}）");
            if (fader2._checklist.Visible)
                problems.Add("清单一条都没有，那几行还占着地方");
            fader2.QueueFree();

            // ---- 默认布局：次序是「今天那一屏原样」，字号是框的一个份额 ----------------
            //
            // 2026-10-06 之前这一族问的是「中间那一列从上到下是 Clock → Remain → Block → Text」
            //（场景里那一列的兄弟序）。那一条连同两列一起撤了：次序不再是写死的排版，
            // **它降级成默认布局的那一份**（设计文档 `docs/design/fader-layout.md`「遮罩的布局」那一节）。所以这一发读的是
            // **默认布局按上缘排出来的次序**，不是场景树上那几个节点的兄弟序 ——
            // 判据要问的是「默认布局是不是今天那一屏」，而那一屏的正本是布局那份数。
            var middle = StepLayout.Default()
                .Where(c => c.Kind is StepLayout.KindClock or StepLayout.KindRemain
                    or StepLayout.KindBlock or StepLayout.KindText)
                .OrderBy(c => c.YPercent)
                .Select(c => c.Kind).ToList();
            var wanted = new[]
            {
                StepLayout.KindClock, StepLayout.KindRemain, StepLayout.KindBlock, StepLayout.KindText,
            };
            if (!middle.SequenceEqual(wanted))
                problems.Add($"默认布局里中间那一族从上到下是 {string.Join(" → ", middle)}，该是 " +
                             $"{string.Join(" → ", wanted)} —— 「今天那一屏原样」的那一份次序");
            // 每一处字号都要够读，而且不许是零。这一条走 <see cref="StepLayout.Place"/> 那条
            // 纯算术，**不**读窗自己手上那一份（headless 下窗的 Size.Y 是 0，量到的是地板，
            // 不是比例；「在屏幕上真的更大」那一半归 `--shot` 那一发的实拍帧，
            // CODING_STANDARDS.md 取证方法论第 5 条）。
            foreach (var shape in StepLayout.Default())
            {
                if (!shape.HasWords) continue;   // 图控件没有字号
                var placed = shape.Place(1920, 1440, 100);
                if (placed.FontPx < placed.FontFloor)
                    problems.Add($"{shape.Kind} 在 1440 高的屏上算出来 {placed.FontPx} 号，" +
                                 $"比地板 {placed.FontFloor} 还小 —— 框太小字会读不出来");
                if (placed.WidthPx <= 0 || placed.HeightPx <= 0)
                    problems.Add($"{shape.Kind} 摆出来是空的（{placed.WidthPx}x{placed.HeightPx}）—— 控件不许是零");

                // **清单那一行的字号不许比小字那一档大**（2026-10-08 补）。上面那几条只钉住下限
                // （比地板小就红、控件不许是零），而这一族出过一次「清单 173 像素」
                // 而自检全绿 —— 173 大于地板，两条都过。**一个只写下限的判据放得过上限**，
                // 而这一族真正的毛病是：清单那一格摆的是**满高框**，字号又是**框高的份额**，
                // 两者相乘就等于「屏高的百分之几」。清单一大，那几行连着那颗钮撑成一大块，
                // 收鼠标的那一块按那几行的矩形算 —— **底下的程序在那一大片就点不到了**，
                // 而契约要的是「铺满那扇窗永远穿透鼠标，Keeper 永远不占住鼠标那一块屏幕」。
                //
                // **只问清单那一格**，别的一格不管：钟、倒计时、时间块名、文字各自有各自的
                // 字号（钟那一档本来就比小字大得多），把它们拉进这一族量的是别的事
                // —— 第二版就犯过，撤回修复之后红的是 `clock` 和 `text` 而不是清单。
                //
                // **尺子是「那一步的文字」那一档**（清单那一行今天和它同档）：块名更小一档，
                // 拿块名当尺子会把清单那一行判红（第二版就栽在这里），
                // 拿钟当尺子则太松 —— 173 < 188，撤回修复之后全绿（第一版）。
                // 尺子那两格不在清单那一格里，所以拧清单拧不动它
                //（尺子若从被量的那一族现算，那一条恒真）。
                if (shape.Kind != StepLayout.KindChecklist) continue;
                var small = SmallestLineFontOn(1920, 1440);
                if (placed.FontPx > small)
                    problems.Add($"{shape.Kind} 在 1440 高的屏上算出来 {placed.FontPx} 号，" +
                                 $"比那一步的文字那一档（{small} 号）还大 —— " +
                                 "它那一格是满高框而字号按框高算，字一大就把清单撑成一大块，" +
                                 "点得到的那几块也跟着变一大块，底下的程序在那一大片就点不到");
            }
            // 换一块屏换的是画面，不是那份布局：同一份份额在另一块屏上摆出来的框按比例变，
            // 而**次序和左右那一列的宽度不变**。
            //
            // **这一问问的是「份额」这个性质，不是「宽高比固定」** —— 第一版问的是
            // 宽高比在两块屏上一致，那是一个错的期望：份额是**分别**对屏宽和屏高的，
            // 所以屏的宽高比一变，摆出来的框的宽高比**就该跟着变**（设计文档 `docs/design/fader-layout.md`「位置和大小存成屏宽屏高的份额，不是像素」那一节的正话是「换一块分辨率不同的屏，每一块屏上摆的位置都是对的」）。第一版读出来「变了」就报红，量的是自己的期望错，
            // 不是布局错。
            //
            // 份额这一族真正要成立的是两件：**份额本身换屏不动**（存进去读回来一样），
            // **换算出来的像素按屏的比例变**。所以下面这两问才是判据。
            var tall = StepLayout.Default().First(c => c.Kind == StepLayout.KindClock).Place(3840, 2560, 100);
            var wide = StepLayout.Default().First(c => c.Kind == StepLayout.KindClock).Place(1920, 1440, 100);
            if (tall.XPercent != wide.XPercent || tall.WidthPercent != wide.WidthPercent ||
                tall.YPercent != wide.YPercent || tall.HeightPercent != wide.HeightPercent)
                problems.Add("同一份布局在两块屏上读出来的份额不一样 —— 存的是份额，换屏只该改像素");
            // 换算那一步按屏的比例走：宽翻一倍，宽出来的那一块也翻一倍（份额不变）。
            if (Math.Abs((double)tall.WidthPx / wide.WidthPx - 2.0) > 0.01)
                problems.Add($"屏宽翻倍，摆出来的宽度是 {tall.WidthPx / wide.WidthPx:F3} 倍 —— "
                             + "份额换算没跟着屏宽走");
            if (Math.Abs((double)tall.HeightPx / wide.HeightPx - 2560.0 / 1440.0) > 0.01)
                problems.Add($"屏高按 1440→2560 走，摆出来的高度是 {tall.HeightPx / wide.HeightPx:F3} 倍 —— "
                             + "份额换算没跟着屏高走");

            // ---- 铺满那扇一辈子穿过，「点得到的那几块」交给钩子 ------------------------------
            // 设计文档 `docs/design/fader-window.md`「穿透靠那两位旗标，不靠引擎的旗标」那一节（铺满那扇一辈子是穿过的）
            // 和 `docs/design/hit-testing.md`「点得到的那几块靠全局钩子按坐标判」那一节（底下的程序照常收得到那一下）。
            // 这一组只问进程内那一层：铺满那扇那颗旗标、那一块量没量
            // 出来、那块罩不罩得住点得到的东西、判定用的那个数对几组坐标答得对不对。
            // OS 那一层「Windows 认不认钩子那一刀」无头里没有，归 `--swallow` 那一发
            // （实测结果在 `docs/research/desktop-layer-and-window-measurements.md`
            // 「钩子能不能吃掉那一档」那一节，`CODING_STANDARDS.md`「取证方法论」第 4 条）。
            //
            // 撤过的两版：一版是「整块屏收鼠标」（铺满那扇一按不穿，底下每一像素都点不到），
            // 一版是「拆成两扇、点击窗罩住那一小块」（那一扇窗罩进去的每一像素底下都点不到 ——
            // 2026-10-10 所有者定夺「永远都不能抢鼠标，这是 bug」）。
            //
            // **这一组比有窗那一版多问一样**：判定用的那个数（`Swallows`）是纯算术，
            // 所以自检能拿坐标直接问它 —— 有窗那一版的「点不点得动」在无头里根本问不了。
            var mouse = new Fader(settings, 0, todos, () => { }, () => fakeNow);
            parent.AddChild(mouse);
            var grabStep = new Step { Interactive = true, Layout = [Shape(StepLayout.KindChecklist)] };
            var grab = new Scheduler.StageNow(grabStep, 60_000, 0, 1, true);
            mouse.Raise(grab);
            if (!mouse.MousePassthrough || mouse.Armed || mouse.Interactive)
                problems.Add($"淡入途中该是「铺满那扇穿过、什么都还没收」（穿过={mouse.MousePassthrough} " +
                             $"那一块={mouse.SwallowRect} 交互={mouse.Interactive}）");
            mouse.Settle();
            if (!mouse.MousePassthrough)
                problems.Add("落定之后铺满那扇不穿了 —— 那一族不摆窗，铺满那一辈子穿过");
            if (!mouse.Interactive)
                problems.Add("落定之后那一步该收得到鼠标（那一块没量出来）");
            // 那一块要罩得住点得到的那颗钮。**面积那一头自检量不了**：
            // headless 下 `DisplayServer.GetScreenCount()` 答 **0**（2026-10-08 重新量的，
            // 原先这里写的是「报的是 1x1」，那句是读来的；实测结果在
            // `docs/research/click-window-headless-measurement.md`）——
            // 没有任何屏可问，占比那一头连分母都没有，拿它当分母读出来永远是 100%。
            // 清单那几行的矩形在 headless 里是 0（容器要等几帧才排版，这一组不跑帧），
            // 所以「罩没罩住每一行」在这里量不出东西 —— 而那一问在零面积时**恒真**，
            // 是假阳性，问它之前必须先排掉零（下面 `clicks-probe` 那一段排了）。
            // headless 量得住、也真会红的是：矩形非空、罩得住那颗钮、判定对坐标答得对。
            var wants = mouse.SwallowMerge();
            if (wants.Size.X <= 0 || wants.Size.Y <= 0)
                problems.Add($"那一块在而它罩的是空的（{mouse.SwallowMeasurement()}）—— 它不收任何鼠标");
            else if (!wants.Encloses(mouse.EndRectFor()))
                problems.Add($"那一块 {wants} 没罩住那颗钮（{mouse.SwallowMeasurement()}）—— "
                             + "「提前结束休息」点不动");
            // **判定本身**：块里那一点吃、块外那一点不吃。这一问是 2026-10-10 新有的，
            // 有窗那一版没有对应的一条（那时判据问的是「引擎答回来的窗矩形」，
            // 而 headless 的 DisplayServer 压根不认）。
            //
            // **点从那一块自己身上取**，不拿那颗钮的中心代替 —— 无头下屏只有 1x1
            // （`屏=(0,0) (1,1)`），裁完那一块也是 1x1，而钮的矩形不是（`钮=(0,0) (128,39)`）：
            // 两者压根不在同一片坐标上，拿钮的中心去问一块 1x1 的东西，量的是测量程序的毛病。
            // 「那一块罩不罩得住那颗钮」是上面那条 `wants.Encloses(...)` 管的，两问各管各的。
            var region = mouse.SwallowRect;
            var on = new Win32.Point
            {
                X = region.Position.X + region.Size.X / 2, Y = region.Position.Y + region.Size.Y / 2,
            };
            if (mouse.Armed && !mouse.Swallows(on))
                problems.Add($"那一块 {region} 的正中间 ({on.X},{on.Y}) 没有被接走 —— " +
                             $"清单和那颗钮点不动（{mouse.SwallowMeasurement()}）");
            // 对照（一定该不吃）：离那一块 4000 像素的点不在任何一块屏上，
            // 而 Keeper 收鼠标只收那一块 —— 它要是答「吃」，屏上就有一大片点不到。
            var far = new Win32.Point { X = on.X, Y = on.Y + 4000 };
            if (mouse.Swallows(far))
                problems.Add($"离那一块 4000 像素那一点 ({far.X},{far.Y}) 也被接走了 —— " +
                             $"底下那一大片全被占了（{mouse.SwallowMeasurement()}）");
            // 换步只换鼠标的用途：换走立刻还回去，换回来要等排版跑完那三帧才量 ——
            // 提前量就是拿一个还没排好版的矩形出去，罩歪了还不该罩的地方点不到。
            mouse.Enter(new Scheduler.StageNow(
                new Step { Layout = [Shape(StepLayout.KindClock)] }, null, 1, 2, true));
            if (mouse.Armed)
                problems.Add("换到不收鼠标的那一步，那一块还收着 —— 底下那一块该立刻还回去");
            if (mouse.Swallows(new Win32.Point { X = on.X, Y = on.Y }))
                problems.Add("换走之后那一块的正中间还被接走 —— 收不收鼠标只该由那一块说话");
            mouse.Enter(grab);
            if (mouse.Armed)
                problems.Add("换步那一发就把那一块量出来了 —— 排版还没跑完，量的是没排好版的矩形");
            mouse.Arm();
            if (!mouse.Armed || !mouse.Interactive)
                problems.Add($"三帧之后该把那一块量出来（那一块={mouse.SwallowRect} " +
                             $"收没收={mouse.Interactive}）—— 这一步清单点不动");
            // 对照（一定该通）：不收鼠标的那一步，从头到尾铺满那扇都穿过、那一块都是空的。
            var still = new Fader(settings, 0, todos, () => { }, () => fakeNow);
            parent.AddChild(still);
            still.Raise(new Scheduler.StageNow(
                new Step { Layout = [Shape(StepLayout.KindClock)] }, 60_000, 0, 1, true));
            still.Settle();
            still.Arm();
            if (!still.MousePassthrough || still.Armed)
                problems.Add("对照不成立：不收鼠标的那一步该是「铺满那扇穿过、那一块是空的」—— "
                             + "上面那几问量的可能只是「它一次都没收过」");
            still.QueueFree();
            mouse.QueueFree();

            // ---- 工单 02：重新去量「headless 量不到」那一句（2026-10-08）------------
            //
            // 上面那段注释和工单 02 都写着「这一族的 `--check` 量不到，headless 的屏是 1x1」。
            // 那是**读来的**，不是量出来的 —— 所以这里把它当没量过，重新量一遍，
            // 量到什么印什么（`Log` 那一句在自检跑完之后的实测结果里）。
            //
            // **为什么要重新量**：CODING_STANDARDS.md「取证方法论」第 5 条 ——
            // 实测结果说「不通」的时候先怀疑自己的测量程序。这一族为「量不到」这个结论
            // 付过一次代价的近邻：它自己也记着「这一族出过两次『结论对、测量程序不量这个』的亏」。
            // 量到什么印什么 —— **不借日志**：自检里那扇窗的日志笔是空的，而
            // CODING_STANDARDS.md 第 5 条后半段记的就是这个（「实测结果压根没出来，
            // 屏幕上只剩那句结论」）。借 `problems` 那一条：它一定印得出来
            // （`SelfCheck.Run` 把它接在红话后面）。
            // 量行缝要**两条以上**清单（一条量不出缝），所以这一族自己造一份，不动上面那份
            var probeTodos = new Todos();
            probeTodos.Items.Add(new Todos.Todo { Text = "行缝第一条" });
            probeTodos.Items.Add(new Todos.Todo { Text = "行缝第二条" });
            var probe = new Fader(settings, 0, probeTodos, () => { }, () => fakeNow);
            parent.AddChild(probe);
            try
            {
                probe.Raise(new Scheduler.StageNow(grabStep, 60_000, 0, 1, true));
                probe.Settle();
                probe.Arm();
                var screenCount = DisplayServer.GetScreenCount();
                var screen = screenCount > 0 ? DisplayServer.ScreenGetSize(0) : new Vector2I(0, 0);
                var measured = probe.SwallowMerge();
                // 工单 02 要的那一发「把 `ClickRegion.Pad` 拧成错值时判据要当次就红」——
                // **这一发量不到，2026-10-08 第二发量过：`Pad` 6 → 60 与 6 → 1 两发都是
                // 54 组 0 条问题、退出码 0。** 下面是量出来的原因，不是推测：
                //
                // **期望里不许有 `Pad`**。第一版写的是「`RectOf` 的结果 = 手算 `.Grow(Pad)`」——
                // 那一条在 `Pad=60` 下照样全绿：期望和算法读的是同一个错值，恒等。
                // 那是 CODING_STANDARDS 取证方法论第 6 条那一条病的另一个形状
                // （判据量的是当初那份副本，副本跟着一起坏了）。
                //
                // 现在成立的那一条是**性质**：每一圈就是 `Pad`，而 `Pad` 本身是配置里那一个数
                // —— 所以这一问问的是「长出来的那一圈是不是就是 `Pad`」，不是「和某个
                // 手算值一致」。**但它同样挡不住拧 `Pad`**：问的两头都读 `Pad`，
                // 拧大拧小都是恒等式 —— **它量的是「那一圈按 `Pad` 长出来了」，
                // 而 `Pad` 该是几，这一组答不出来。**
                //
                // 本来该抓住 `Pad` 的那一条是下面那条缝的判据（罩少了缝就点得到），
                // 而它在 headless 里**恒不触发**：`<see cref="RowGap"/>` 要两行排完版才答得出来，
                // 没有排版就是 0，而条件是 `lineGap > 0`。
                //
                // **所以「拧 `Pad` 当次就红」这一发要拿到非 headless 那一发里去**
                // —— 别在 `--check` 里找它，那儿它不会响。
                var pair = new[]
                {
                    new Rect2I(0, 0, 100, 40), new Rect2I(20, 60, 100, 40),
                };
                var mergedOnly = pair[0].Merge(pair[1]);
                var got = ClickRegion.RectOf(pair);
                var grewX = got.Size.X - mergedOnly.Size.X;
                var grewY = got.Size.Y - mergedOnly.Size.Y;
                if (grewX != ClickRegion.Pad * 2 || grewY != ClickRegion.Pad * 2)
                    problems.Add($"那一块长出来的那一圈不对：并完是 {mergedOnly}，" +
                                 $"长完是 {got} —— 左右各多 {(double)grewX / 2}、上下各多 {(double)grewY / 2}，" +
                                 $"该是 Pad={ClickRegion.Pad} 每一边");
                // **钉住那一圈该多大**：这一圈要让清单行与行之间那条缝点得到，
                // 而那条缝是 `BuildChecklist` 摆出来的（行框高 − 字高）。
                // **从行控件自己量**，不从 `Pad` 反推 —— 反推就是拿被量的那个数当尺子。
                var lineGap = probe.RowGap();
                if (lineGap > 0 && ClickRegion.Pad < lineGap)
                    problems.Add($"那一块多算进去的一圈是 {ClickRegion.Pad}，" +
                                 $"而清单行与行之间那条缝有 {lineGap} 宽 —— 缝没算进去，" +
                                 $"缝底下那一格看着像清单，点下去却落到了别人的程序上");
                // 退路那一档：没喂进去就是空 —— 「这一步没有点得到的东西」。
                if (ClickRegion.RectOf([]) != new Rect2I())
                    problems.Add("一颗都没喂进去时该是空矩形 —— 那就是「这一步没有点得到的东西」");
                // 一颗：那一圈照样长（不是只有并起来才长）。
                if (ClickRegion.RectOf([pair[0]]) != pair[0].Grow(ClickRegion.Pad))
                    problems.Add("只喂一颗时那一圈也照样长 —— 那一块多算进去的一圈不是并出来的");
                // 三行读数分开说，不合成一句结论（哪一头量得了、哪一头量不了，逐条钉住）：
                // 1) 屏 —— 量到的是**屏数 0**（headless 下 DisplayServer 根本没有屏可问，
                //    原来的注释写的是「报的是 1x1」，那一句是读来的，量出来不是那么回事）：
                //    占比那一头连分母都没有，量不了；
                // 2) 要罩的那一块 —— 非空、罩得住那颗钮，这一头量得了（上头几条判据已经在问）；
                // 3) 每一行 —— `Encloses` 对**零面积矩形恒真**，所以一行只要还没排出版
                //    形状，这一问对它就是白问。而 `RowsInside` 遍历全部行，
                //    **这里排掉的只有第一行** —— 其余各行有没有零面积，这一发没量
                //    （2026-10-08 第二发量到第一行不是零面积，见
                //    docs/research/click-window-headless-measurement.md「重量的那一发」：
                //    第一发记的「第一行是零面积」已经不复现）。
                //    所以这一问答 True 只能说明「第一行确实罩住了」。
                var firstRow = probe.FirstRowRect();
                var rowsDegenerate = firstRow.Size.X <= 0 || firstRow.Size.Y <= 0;
                if (!rowsDegenerate && !probe.RowsInside(measured))
                    problems.Add($"那一块 {measured} 没罩住清单的行（钮={probe.EndRectFor()}）—— 点不动");
                _probeReading = $"屏数={screenCount} 屏0={screen}" +
                             $"（屏数是 0，不是 1x1 —— 没有任何屏可问，占比那一头连分母都没有）；" +
                             $"要罩={measured} 非空={measured.Size.X > 0 && measured.Size.Y > 0} " +
                             $"罩得住那颗钮={measured.Encloses(probe.EndRectFor())} 钮={probe.EndRectFor()}；" +
                             $"收的那一块={probe.SwallowRect}" +
                             $"（**裁过屏的那一份**：无头下遮罩那扇窗自己就是 1x1，" +
                             $"所以它退化成 1x1 —— 而上面那个「要罩」是**没裁**的那一份。" +
                             $"第一版拿钮的中心去问裁完的那一块，两者在不同坐标片上，量的是测量程序的毛病；" +
                             $"现在坐标那一问从裁完的那一块自己身上取点）；" +
                             $"第一行={firstRow} 是零面积={rowsDegenerate} " +
                             $"「全部行落在里面」={probe.RowsInside(measured)}" +
                             $"（零面积时这一问恒真，是假阳性 —— 排版没跑完，" +
                             $"「罩没罩住每一行」那一头在自检里量不了）";
            }
            finally
            {
                probe.QueueFree();
            }
        }
        catch (Exception e)
        {
            problems.Add($"遮罩上布局那一组抛了：{e.GetType().Name} {e.Message}");
        }
        finally
        {
            if (fader != null && IsInstanceValid(fader)) fader.QueueFree();
        }
        return problems;
    }
}
