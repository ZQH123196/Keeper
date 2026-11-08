using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>布局编辑器：<b>一扇单独的大窗</b>，画布按屏的比例显示，用户在画布上把一步
/// <b>布局</b>里的每一个控件拖到要它站的位置（契约
/// `docs/design/fader-layout.md`「遮罩的布局是每一步自己那一份」那一节；
/// 词汇表 <c>GLOSSARY.md</c> 的「布局」和「刻度线」）。
///
/// <b>这一族要做的七件事，全在契约那一节里</b>：所见即所得（画布和屏一个比例）、
/// 拖的时候浮出<b>刻度线</b>并吸住、<b>允许重叠</b>并给两颗换层的钮、六种都能改大小和删、
/// 一颗「回到默认布局」、一颗「照抄上一步的布局」、<b>不做撤销</b>。
///
/// <b>引擎给的和要自己写的，分界是 ADR-0001「拖放走引擎的虚函数，刻度线和改大小手柄自己画」
/// 那一节，本机实测结果在 <c>docs/research/godot-drag-and-snap.md</c> 第八节</b>：
/// <list type="bullet">
/// <item>拖放走 <c>_GetDragData</c> / <c>_CanDropData</c> / <c>_DropData</c> 加
/// <c>SetDragPreview</c>（8.1、8.2、8.3：源侧落侧都量得到，<c>at_position</c> 是控件内的点）。
/// <b>喂法那两条是量出来的教训，本窗一条注入代码都不写</b>：喂系统那一层而不是
/// <c>PushInput</c>、一次别把十几发全交进去（8.3）—— 交互这一侧靠的是真人手拖，
/// 那两条落在测量程序身上。</item>
/// <item>刻度线和改大小手柄<b>引擎没有</b>（前面那三节：五个类搜过，零命中），
/// 所以 <see cref="LayoutOverlay"/> 自己算自己画。门限照 8.5 / 8.6 用的那一个数
/// （<see cref="SnapThreshold"/>）—— 换一个它就等于换一个没量过的数。</item>
/// <item>画布是<b>真 <see cref="Control"/></b>、摆出来的那几颗是<b>真 <see cref="Panel"/></b>（8.7），
/// 只有刻度线和手柄那一层是自绘的。</item>
/// <item>拖动那一档要<b>自己 <c>QueueRedraw</c></b>（8.7 量到的是「不主动重画时上一次画的还在」，
/// 不是「位置自动跟着变」）。本窗每一处动了位置都显式重画。</item>
/// <item>引擎那个整像素吸附<b>不用拧</b>（8.4：对 <c>Control.Position</c> 没有影响）——
/// 存的是份额，份额转出来是连续数。</item>
/// </list>
///
/// <b>不撤销</b>：布局是一份文档不是一串动作（契约同一节）。所以这一扇窗里没有撤销栈，
/// 有一颗「回到默认布局」把整份放回原位。
///
/// <b>存的是屏宽屏高的份额（百分数），不是像素</b>：存像素的话换一块分辨率不同的屏，
/// 每一块屏上摆的位置都是错的。<b>份额和范围住在 <see cref="StepLayout"/> 那一侧</b> ——
/// 这一扇窗只做两件事：<see cref="ToPixels"/> 把它摆到画布上、<see cref="ToPercents"/>
/// 把画布上的那一块写回去，两头都走 <see cref="StepLayout.Place"/>（不许出屏、不许是零那条
/// 规矩就写在那儿）。
///
/// <b>叠的次序住在列表里</b>（<see cref="StepLayout"/> 的类型注释：越靠后越在上面，
/// 设计文档 `docs/design/fader-layout.md`「重叠时后来摆的在上」）。所以「往前一层」「往后一层」就是把一行在列表里挪一格，
/// <b>没有第二个 Z 字段</b>。
///
/// <b>遮罩亮着的时候改的，等下一次休息才用上</b>：这一扇窗只写配置里那一份
/// <see cref="Step.Layout"/>，不碰正在走的那一趟（设计文档 `docs/design/layout-editor.md`「保存的时机」那一节：改的等下一次休息才用上）。所以窗里常驻一句把这件事说出来。
///
/// <b>开窗的形状</b>见 <see cref="Open"/>：<c>Options</c> 递一步进来，再递它的前一步
/// （「照抄上一步的布局」要知道照谁）。
///
/// <b>这一扇窗里不生成整窗界面</b>（ADR-0001「不建 schema 链，界面在编辑器里搭成场景」）：
/// 位置全在 <c>res://src/LayoutEditor.tscn</c> 里。<b>只有三样是代码造的</b>，各有各的理由：
/// 画布、手柄那一层、摆出来的那几颗 —— 它们各自挂的脚本是<b>这一个文件里的另外三个类</b>，
/// 而场景文件按类名认脚本，一个文件只有一个类名挂得上去。手柄那一层是本机实测要自己画的
/// （ADR-0001 把自绘留给「控件确实画不出的那部分」）；摆出来的那几颗是<b>随数据长出来的东西</b>，
/// 和设置窗里时间块那几行、步那几行同一形状（<c>Options</c> 那一族）。</summary>
public partial class LayoutEditor : Window
{
    public const string ScenePath = "res://src/LayoutEditor.tscn";

    // ---- 门限：那几个数各有一次实测结果，不许随手换 ---------------------------------

    /// <summary>吸住的门限，画布像素。<b>照 8.5 那一发用的数</b>：差 2 吸住、差 10 不吸，
    /// 门限 6。换成别的就等于换一个没量过的数，而那一族没有第二发。</summary>
    public const float SnapThreshold = 6f;

    /// <summary>改大小手柄的边带，画布像素。<b>同样照 8.6 那一发的数</b> ——
    /// 那一发量的就是「事件落在不在边上、门限 6」。</summary>
    public const float HandleBand = 6f;

    /// <summary>手柄那一颗方块边长。只管看得见 —— 8.6 的判据是命中，不是好看。</summary>
    public const float HandleSize = 9f;

    /// <summary>一个控件在<b>画布上</b>最小能有多小（像素）。再小就点不中、也看不出它是什么。
    /// <b>和模型那份下限不是同一件事</b>：<see cref="StepLayout.MinSizeShare"/> 管的是
    /// 「存进配置不许是零」，这一条管的是「这一手还抓得住」。谁赢由
    /// <see cref="StepLayout.Place"/> 那一层的夹取说话。</summary>
    public const float MinSide = 12f;

    // ---- 场景上的位置（位置在 .tscn 里，这里只按名字问） --------------------------------

    private const string TitlePath = "Margin/Stack/Title";
    private const string StatusPath = "Margin/Stack/Status";
    private const string ErrPath = "Margin/Stack/Err";
    private const string NotePath = "Margin/Stack/Note";
    private const string ForwardPath = "Margin/Stack/Bar/Forward";
    private const string BackPath = "Margin/Stack/Bar/Back";
    private const string DelPath = "Margin/Stack/Bar/Del";
    private const string DefaultsPath = "Margin/Stack/Bar/Defaults";
    private const string CopyPath = "Margin/Stack/Bar/CopyPrev";
    private const string AddTextPath = "Margin/Stack/Bar/AddText";
    private const string AddImagePath = "Margin/Stack/Bar/AddImage";
    private const string ClosePath = "Margin/Stack/Bar/Close";
    private const string PropsPath = "Margin/Stack/Props";
    private const string NonePath = "Margin/Stack/Props/None";
    private const string WordsPath = "Margin/Stack/Props/Words";
    private const string WordsNamePath = "Margin/Stack/Props/Words/Name";
    private const string WordsValuePath = "Margin/Stack/Props/Words/Value";
    private const string FitPath = "Margin/Stack/Props/Fit";
    private const string FitNamePath = "Margin/Stack/Props/Fit/Name";
    private const string FitValuePath = "Margin/Stack/Props/Fit/Value";
    private const string FontPath = "Margin/Stack/Props/Font";
    private const string FontNamePath = "Margin/Stack/Props/Font/Name";
    private const string FontValuePath = "Margin/Stack/Props/Font/Value";
    private const string AreaPath = "Margin/Stack/CanvasHost/Area";

    /// <summary>控件上除了 <c>kind</c> 之外的八个字段。<b>新摆一颗的那一份从声明表问它们
    /// 各自的默认值</b>（<see cref="Settings.StepLayoutDeclarations"/>）—— 这一族不抄第二个数
    /// （ADR-0001「不建 schema 链」：一个限制只声明一次）。</summary>
    private static readonly string[] Fields =
    [
        "xPercent", "yPercent", "widthPercent", "heightPercent", "fontPercent",
        "text", "file", "position",
    ];

    // ---- 设置窗递进来的东西 -----------------------------------------------------------

    /// <summary>正在摆哪一步的布局。<b>这一扇窗不读配置</b>（和 <see cref="Options"/> 那一族
    /// 一样：界面上一个窗不该知道配置住在哪），它改的是递进来的那一份 —— 设置窗给的是
    /// 它的<b>工作副本</b>，所以关窗不保存也回得去。</summary>
    public Step Editing = null!;

    /// <summary>它的<b>前一步</b>，同一个休息流程里的那一步。「照抄上一步的布局」照的是它。
    /// 没有前一步（这是第一步）时是空的，那一颗钮照实说照不了。</summary>
    public Step? Before;

    /// <summary>摆的是哪一步，写给人看的（<c>Options</c> 递，比如「todoList · 2/3」）。空 = 不写。</summary>
    public string Subject = "";

    /// <summary>配色跟着设置窗那一个 <c>uiTheme</c> 键走（和 <see cref="Presets"/> 同一种挂法）。</summary>
    public string UiThemeValue = "light";

    public Action<string, string> Log = (_, _) => { };

    /// <summary>布局动了一下就叫一次。<b>不记状态</b>：设置窗那一侧要不要重建什么、要不要标脏，
    /// 是它的事。</summary>
    public Action? Changed;

    // ---- 这一扇窗自己的几样 ---------------------------------------------------------

    private Control _area = null!;
    private LayoutCanvas _canvas = null!;
    private LayoutOverlay _overlay = null!;
    /// <summary>界面上摆出来的那几颗，<b>下标就是 <see cref="Step.Layout"/> 里的下标</b> ——
    /// 次序住在列表里（越靠后越在上面），所以这里不许另排一次。</summary>
    private readonly List<PlacedBox> _boxes = [];
    private int _selected = -1;

    /// <summary>程序往控件里灌值的那一小段。和 <see cref="Options"/> 同一个理由：
    /// 控件的 <c>TextChanged</c> 不分「人打的」和「我灌的」，不立这一面旗，
    /// 刚填进去的字会被读成一次改动。</summary>
    private bool _filling;

    // 拖动和改大小正在进行的那几件。**拖放本身是引擎在驱动**（ADR-0001 那一条），
    // 这一侧只记状态：哪一颗在拖、手在它里面的哪一点、松手之后要不要收手。
    private int _drag = -1;
    private Vector2 _grab;
    private ulong _dragFrame;
    private int _resizing = -1;
    private Handle _resizeHandle = Handle.None;

    /// <summary>此刻有没有一颗在拖。落侧每一格都问一句，不是我们的拖就拒掉 ——
    /// 不然把别的什么往画布上拖也会摆进去一颗。</summary>
    internal bool Dragging => _drag >= 0;

    /// <summary>此刻有没有一颗在改大小。</summary>
    internal bool Resizing => _resizing >= 0;

    // ==================================================================================
    // 纯算术：不碰一扇窗就能算出来。自检那一族问的就是这一段（--check 里没有窗）。
    // ==================================================================================

    /// <summary>改大小手柄在哪一格。边带之外是 <see cref="Handle.None"/>。</summary>
    public enum Handle { None, NW, N, NE, E, SE, S, SW, W }

    /// <summary>这一档吸住了没有，吸到了哪两条线上（<c>null</c> = 那一条轴上没吸）。</summary>
    public readonly record struct Guides(bool Snapped, float? Vertical, float? Horizontal);

    /// <summary>算完的结果：吸过的那一块，和为了画刻度线要的那两条线。</summary>
    public readonly record struct SnapResult(Rect2 Box, Guides Guides);

    /// <summary>份额（百分数）→ 画布上的那一块。<b>走 <see cref="StepLayout.Place"/></b>：
    /// 「不许出屏、不许是零」那两条规矩写在那儿，编辑器不重写一遍。
    /// 画布和屏同一个比例，所以拿画布宽高当屏宽高算出来的是同一个份额。</summary>
    public static Rect2 ToPixels(double xPercent, double yPercent, double widthPercent,
        double heightPercent, Vector2 canvas)
    {
        var at = StepLayout.Place(xPercent, yPercent, widthPercent, heightPercent, 0, (int)canvas.X, (int)canvas.Y);
        return new Rect2((float)at.LeftPx, (float)at.TopPx, (float)at.WidthPx, (float)at.HeightPx);
    }

    /// <summary>画布上的那一块 → 份额（百分数，存进配置的那一头）。
    /// <b>夹取仍然由 <see cref="StepLayout.Place"/> 说了算</b>：框不许伸出画布、不许是零。
    /// 画布读不出来（<c>0×0</c>）时四个零 —— 宁可给一个一眼看得出的错值，
    /// 也不要给一个看着合理的错值。</summary>
    public static (int X, int Y, int W, int H) ToPercents(Rect2 box, Vector2 canvas)
    {
        if (canvas.X <= 0f || canvas.Y <= 0f) return (0, 0, 0, 0);
        var at = StepLayout.Place(box.Position.X / canvas.X * 100, box.Position.Y / canvas.Y * 100,
            box.Size.X / canvas.X * 100, box.Size.Y / canvas.Y * 100, 0, (int)canvas.X, (int)canvas.Y);
        return ((int)Math.Round(at.XPercent), (int)Math.Round(at.YPercent),
            (int)Math.Round(at.WidthPercent), (int)Math.Round(at.HeightPercent));
    }

    /// <summary>点落在不在这个矩形里面。<b>和「落在不在边带里」是两件事</b> ——
    /// 8.6 那一发量的就是这一对：正点是「在矩形内、不在边带」，边带上那一点两样都真，
    /// 而矩形外那一点两样都假。</summary>
    public static bool Inside(Vector2 at, Vector2 size) =>
        at.X >= 0 && at.Y >= 0 && at.X <= size.X && at.Y <= size.Y;

    /// <summary>点落在哪个手柄上。角先判：角是两条边带交叉的那一格，拉宽高比拉边更常用。
    /// <b>边带画在矩形外边一点也算</b>（拖右边缘时人手的常态），所以这一问只看离哪条边近，
    /// 不看在不在矩形里面。</summary>
    public static Handle HandleAt(Vector2 at, Vector2 size, float band)
    {
        var left = at.X <= band;
        var right = at.X >= size.X - band;
        var top = at.Y <= band;
        var bottom = at.Y >= size.Y - band;
        if (!left && !right && !top && !bottom) return Handle.None;
        if (top && left) return Handle.NW;
        if (top && right) return Handle.NE;
        if (bottom && right) return Handle.SE;
        if (bottom && left) return Handle.SW;
        if (top) return Handle.N;
        if (right) return Handle.E;
        if (bottom) return Handle.S;
        return Handle.W;
    }

    /// <summary>一次平移吸住没有、吸到哪。<b>吸的是契约点名的那些</b>：屏的中线、屏的四边、
    /// 别的控件的边和它们的中间线（<c>spec.md</c>「拖的时候浮出刻度线」那一段）。
    ///
    /// <b>三条边各对一次</b>（起点对起点、终点对终点、中点对中点）。8.5 那一发量的是
    /// 「起点对每一条线」那一种（它把「中心落在屏中线上」那一档读成「起点吸到中线」），
    /// 三条边是它的超集：那五档读数仍然成立；而「这一颗的右边对那一颗的右边」这一种
    /// 不在那一发里量过，是按契约那句话补上的。
    ///
    /// <b>自己不在自己的目标里</b>：<paramref name="others"/> 只放别的控件（8.5 的量具也是
    /// 这么摆的 —— 自己吸自己等于没动，那一档会读成「吸附不成立」）。
    ///
    /// 门限内吸、门外不吸，<b>两头都是一种读数</b>（8.5 的判据）；严格小于，和那一发一致。</summary>
    public static SnapResult SnapRect(Rect2 box, Rect2 bounds, IReadOnlyList<Rect2> others,
        float threshold = SnapThreshold)
    {
        var lines = Lines(bounds, others);
        var x = Edge3(box.Position.X, box.Size.X, lines.Xs, threshold, out var vLine);
        var y = Edge3(box.Position.Y, box.Size.Y, lines.Ys, threshold, out var hLine);
        var moved = new Rect2(new Vector2(x, y), box.Size);
        return new SnapResult(moved, new Guides(vLine != null || hLine != null, vLine, hLine));
    }

    /// <summary>一次改大小吸住没有、吸到哪。<b>只有被拖的那几条边去吸</b>：改大小的时候动的是边，
    /// 不是整个框（整个框跟着动是平移，那是 <see cref="SnapRect"/> 那一族）。
    /// 最小边长在吸之前先夹住 —— 一个控件小到点不中就没法再改回去了。
    ///
    /// <paramref name="grab"/> 是手在哪（画布像素），<paramref name="at"/> 是这一颗此刻站着的
    /// 那一块。角手柄两条边都动，边带手柄只动一条。</summary>
    public static SnapResult ResizeTo(Rect2 start, Handle handle, Vector2 grab, Rect2 at,
        Rect2 bounds, IReadOnlyList<Rect2> others, float threshold = SnapThreshold,
        float min = MinSide)
    {
        if (handle == Handle.None) return new SnapResult(at, new Guides(false, null, null));
        var lines = Lines(bounds, others);
        var left = start.Position.X;
        var top = start.Position.Y;
        var right = start.End.X;
        var bottom = start.End.Y;
        float? vLine = null;
        float? hLine = null;
        if (handle is Handle.NW or Handle.W or Handle.SW)
            left = Edge1(Math.Min(grab.X, right - min), lines.Xs, threshold, out vLine);
        if (handle is Handle.NE or Handle.E or Handle.SE)
            right = Edge1(Math.Max(grab.X, left + min), lines.Xs, threshold, out vLine);
        if (handle is Handle.NW or Handle.N or Handle.NE)
            top = Edge1(Math.Min(grab.Y, bottom - min), lines.Ys, threshold, out hLine);
        if (handle is Handle.SW or Handle.S or Handle.SE)
            bottom = Edge1(Math.Max(grab.Y, top + min), lines.Ys, threshold, out hLine);
        var box = new Rect2(new Vector2(left, top), new Vector2(right - left, bottom - top));
        return new SnapResult(box, new Guides(vLine != null || hLine != null, vLine, hLine));
    }

    /// <summary>在一块地方里摆出<b>和屏一个比例</b>的那一块（能放多大放多大），摆在正当中。
    /// 这一条就是设计文档 `docs/design/layout-editor.md`「单独一扇窗，和屏差不多大」那一节的算法：画布按比例缩，两边的空处是窗口的边。</summary>
    public static Rect2 ScreenFit(Vector2 area, Vector2 screen)
    {
        if (area.X <= 0f || area.Y <= 0f || screen.X <= 0f || screen.Y <= 0f)
            return new Rect2(Vector2.Zero, area);
        var size = screen * Math.Min(area.X / screen.X, area.Y / screen.Y);
        return new Rect2((area - size) / 2f, size);
    }

    /// <b>「点得到的那几块」哪一块的正本不在这里</b>（工单 02）：是
    /// <see cref="ClickRegion.RectOf"/>。这一族原来自己也写了一份并矩形的算术，
    /// 而它和产品那份量的不是同一件事（这里喂的是摆的框，产品那份喂的是引擎排完版的
    /// 控件矩形）—— 两份各自都对，合在一处就漂一次。现在两边调同一段。

    /// <summary>一根轴上的目标线：屏（或画布）的三条，加上每个别的控件的三条。
    /// 契约点名的是「中线、四边、别的控件的边和它们的中间线」—— 起点、中点、终点那三个数
    /// 就是这三样。</summary>
    private static (List<float> Xs, List<float> Ys) Lines(Rect2 bounds, IReadOnlyList<Rect2> others)
    {
        var xs = new List<float> { bounds.Position.X, bounds.Position.X + bounds.Size.X / 2f, bounds.End.X };
        var ys = new List<float> { bounds.Position.Y, bounds.Position.Y + bounds.Size.Y / 2f, bounds.End.Y };
        foreach (var one in others)
        {
            xs.Add(one.Position.X);
            xs.Add(one.Position.X + one.Size.X / 2f);
            xs.Add(one.End.X);
            ys.Add(one.Position.Y);
            ys.Add(one.Position.Y + one.Size.Y / 2f);
            ys.Add(one.End.Y);
        }
        return (xs, ys);
    }

    /// <summary>三条边（起点、中点、终点）各去对一遍目标线，取门限内最近的那一条。
    /// 返回吸过之后的<b>起点</b>。</summary>
    private static float Edge3(float origin, float size, List<float> lines, float threshold,
        out float? line)
    {
        var best = origin;
        var dist = threshold;
        line = null;
        foreach (var at in lines)
            foreach (var which in new[] { 0f, 0.5f, 1f })
            {
                var want = at - size * which;
                var gap = Math.Abs(want - origin);
                if (gap >= dist) continue;
                dist = gap;
                best = want;
                line = at;
            }
        return best;
    }

    /// <summary>一条边去对目标线。<paramref name="value"/> 是这条边此刻的位置，返回吸过之后的位置。</summary>
    private static float Edge1(float value, List<float> lines, float threshold, out float? line)
    {
        var best = value;
        var dist = threshold;
        line = null;
        foreach (var at in lines)
        {
            var gap = Math.Abs(at - value);
            if (gap >= dist) continue;
            dist = gap;
            best = at;
            line = at;
        }
        return best;
    }

    // ==================================================================================
    // 开窗、接线
    // ==================================================================================

    /// <summary>开这一扇窗。<b>工厂函数只造不挂</b>（<c>CODING_STANDARDS.md</c>「代码级规矩」
    /// 那一条：引擎对被拒的 <c>AddChild</c> 只印一行就咽下去，窗照样显示，所以挂一律由调用方做）。
    ///
    /// <paramref name="parent"/> 是设置窗 —— 和画廊（<see cref="Presets"/>）同一种挂法：
    /// 这一扇是设置窗的子窗，配色跟着设置窗那一个键走。<b>读不出来就说读不出来</b>，
    /// 不静悄悄开一扇没配色的窗。</summary>
    public static LayoutEditor? Open(Node parent, Step editing, Step? before, string uiTheme,
        Action<string, string> log, Action? changed = null)
    {
        if (GD.Load<PackedScene>(ScenePath)?.Instantiate<LayoutEditor>() is not LayoutEditor made)
        {
            log("layout", $"{ScenePath} 实例化不出来，布局编辑器开不了");
            return null;
        }
        made.Editing = editing;
        made.Before = before;
        made.UiThemeValue = uiTheme;
        made.Log = log;
        made.Changed = changed;
        parent.AddChild(made);
        return made;
    }

    /// <summary>换一步接着摆。<b>不换窗</b>：窗是那一扇，窗里摆的是哪一步是另一件事
    /// （「照抄上一步的布局」要的就是前一步，而前一步会随着人在设置窗里挪步而变）。</summary>
    public void Retarget(Step editing, Step? before, string subject)
    {
        Editing = editing;
        Before = before;
        Subject = subject;
        Rebuild();
    }

    public override void _Ready()
    {
        // 排在建控件之前：第一帧排版就要按这一版配色来（和 Options._Ready 同一个理由）。
        UiTheme.Apply(this, UiThemeValue);
        GetNode<Button>(ForwardPath).Pressed += () => Shift(1);
        GetNode<Button>(BackPath).Pressed += () => Shift(-1);
        GetNode<Button>(DelPath).Pressed += Remove;
        GetNode<Button>(DefaultsPath).Pressed += Defaults;
        GetNode<Button>(CopyPath).Pressed += CopyPrevious;
        GetNode<Button>(AddTextPath).Pressed += () => Add(StepLayout.KindText);
        GetNode<Button>(AddImagePath).Pressed += () => Add(StepLayout.KindImage);
        GetNode<Button>(ClosePath).Pressed += Dismiss;
        GetNode<LineEdit>(WordsValuePath).TextChanged += _ =>
        {
            if (_filling || _selected < 0) return;
            // 文字控件装的是一段话，图控件装的是一个路径 —— **两格分开是因为它们是两个字段**，
            // 不是为了界面上少一行（设计文档 `docs/design/fader-layout.md`「六种控件」那一节里文字和图是两行）。
            var model = _boxes[_selected].Model;
            var typed = GetNode<LineEdit>(WordsValuePath).Text;
            if (model.Kind == StepLayout.KindImage) model.File = typed;
            else model.Text = typed;
            Mark();
        };
        var fit = GetNode<OptionButton>(FitValuePath);
        foreach (var name in FitNames) fit.AddItem(Tr(name));
        fit.ItemSelected += index =>
        {
            if (_filling || _selected < 0) return;
            _boxes[_selected].Model.Position = StepLayout.FitModes[index];
            Mark();
        };
        GetNode<SpinBox>(FontValuePath).ValueChanged += value =>
        {
            if (_filling || _selected < 0) return;
            _boxes[_selected].Model.FontPercent = (int)value;
            Mark();
        };

        // 画布和上面那层：代码造的（类型注释末尾那一段写了为什么）。次序是 画布 → 手柄那层
        // → 摆出来的控件，Rebuild 里把最后那一层再 MoveChild 上去（它要盖在控件上面，8.7）。
        _area = GetNode<Control>(AreaPath);
        // **画布的四个锚点全是 0**（新控件的初值），不给它 FullRect：锚点拉满的两端不相等时
        // 引擎会在排版那一步把 Size 改回去，而我摆位算出来的正是那个 Size
        //（实拍这一发就撞上了：引擎印「anchors will have their size overridden after _ready()」，
        // 画布会被重新拉满整块，画布按屏比例缩的那一条就没了）。位置和尺寸只由
        // <see cref="FitCanvas"/> 给。
        _canvas = new LayoutCanvas(this) { MouseFilter = Control.MouseFilterEnum.Stop };
        _overlay = new LayoutOverlay(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _area.AddChild(_canvas);
        _canvas.AddChild(_overlay);
        _area.Resized += FitCanvas;
        FitCanvas();

        // 摆在主屏的工作区：这一扇「和屏差不多大」（设计文档 `docs/design/layout-editor.md`「编辑器是单独一扇窗，和屏差不多大」）。
        // 引擎给的位置是 (0,0)（同 Presets._Ready 那一段的理由），所以位置和尺寸都在这里给。
        var work = DisplayServer.ScreenGetUsableRect(0);
        Position = work.Position;
        Size = work.Size;
        CloseRequested += Dismiss;
        Show();   // 场景里 visible=false：OS 窗到这一行才建
        Rebuild();
    }

    /// <summary>换语言之后把字重灌一遍（和 <see cref="Presets.Texts"/> 同一个理由：
    /// <c>UiTheme</c> 的角色是覆写，换语言也要重刷）。<b>五颗摆法的名字重排一遍</b> ——
    /// 那五颗 <see cref="OptionButton"/> 的项是按次序加的。</summary>
    public void Texts()
    {
        GetNode<Label>(TitlePath).Text = Tr("布局");
        GetNode<Label>(StatusPath).Text = Status;
        GetNode<Label>(NotePath).Text = Tr("遮罩亮着的时候这里改的，要等下一次休息才用上");
        GetNode<Label>(NonePath).Text = Tr("选一颗控件就能改它的字、摆法和字号");
        GetNode<Button>(ForwardPath).Text = Tr("往前一层");
        GetNode<Button>(BackPath).Text = Tr("往后一层");
        GetNode<Button>(DelPath).Text = Tr("删掉");
        GetNode<Button>(DefaultsPath).Text = Tr("回到默认布局");
        GetNode<Button>(CopyPath).Text = Tr("照抄上一步的布局");
        GetNode<Button>(AddTextPath).Text = Tr("摆一个文字");
        GetNode<Button>(AddImagePath).Text = Tr("摆一张图");
        GetNode<Button>(ClosePath).Text = Tr("关掉");
        GetNode<Label>(FitNamePath).Text = Tr("摆法");
        GetNode<Label>(FontNamePath).Text = Tr("字号");
        var fit = GetNode<OptionButton>(FitValuePath);
        for (var i = 0; i < FitNames.Length && i < fit.ItemCount; i++)
            fit.SetItemText(i, Tr(FitNames[i]));
        UiTheme.Paint(this);
        PaintBoxes();
        Props();
    }

    /// <summary>关掉这一扇窗。<b>不给设置窗递东西</b>：摆出来的东西在
    /// <see cref="Step.Layout"/> 上，那一份是设置窗的工作副本，关窗不保存回得去
    /// （同 <see cref="Options"/> 那一族的那一条纪律）。</summary>
    public void Dismiss() => QueueFree();

    // ==================================================================================
    // 画布上那几颗
    // ==================================================================================

    /// <summary>屏有多大（像素）。<b>份额就是这一头</b>（设计文档 `docs/design/fader-layout.md`「位置和大小存成屏宽屏高的份额」）
    /// —— 0 号屏：遮罩在每块屏上按同一份份额各摆一份，所以编辑器对着 0 号屏摆就够。</summary>
    private static Vector2 ScreenSize()
    {
        var size = DisplayServer.ScreenGetSize(0);
        return new Vector2(size.X, size.Y);
    }

    /// <summary>画布按屏的比例摆一次，全部控件按份额重摆一遍。
    /// <b>重摆之后自绘那一层要显式重画</b>（8.7：引擎重排不抹掉它，但也不会替我们跟着变）。</summary>
    private void FitCanvas()
    {
        var fit = ScreenFit(_area.Size, ScreenSize());
        _canvas.Position = fit.Position;
        _canvas.Size = fit.Size;
        foreach (var box in _boxes) Place(box);
        _overlay.QueueRedraw();
    }

    /// <summary>把一份布局摆到画布上。<b>次序就是列表的次序</b>（越靠后越在上面，
    /// 设计文档 `docs/design/fader-layout.md`「允许重叠，重叠时后来摆的在上」）—— 树上后加的画在上面，所以按列表次序加。
    /// <b>不做撤销</b>：重建一遍就是重建一遍，不留上一个状态。</summary>
    public void Rebuild()
    {
        foreach (var box in _boxes)
        {
            _canvas.RemoveChild(box);
            box.QueueFree();
        }
        var was = Selected()?.Model;
        _boxes.Clear();
        _selected = -1;
        GetNode<Control>(ErrPath).Visible = false;
        if (Editing?.Layout == null)
        {
            Say(Tr("没有一步可摆"), true);
            return;
        }
        foreach (var model in Editing.Layout)
        {
            var box = new PlacedBox(this, model) { MouseFilter = Control.MouseFilterEnum.Stop };
            _canvas.AddChild(box);
            _boxes.Add(box);
            Place(box);
        }
        // 自绘那一层压在最上面（刻度线和手柄要看得见）。
        _canvas.MoveChild(_overlay, _canvas.GetChildCount() - 1);
        PaintBoxes();
        _overlay.QueueRedraw();
        if (was != null) Select(was);
        Props();
        Say(Status, false);
        Log("layout", $"重摆：{_boxes.Count} 颗（" +
                       string.Join("、", _boxes.Select(b => NameOf(b.Model.Kind))) + "）");
    }

    /// <summary>一颗控件从份额摆到画布像素上。<b>份额 → 像素只在这一处转</b>，
    /// 反过来在 <see cref="Drop"/> 和 <see cref="EndResize"/> 那两处转回去。</summary>
    private void Place(PlacedBox box) => box.Rect = ToPixels(box.Model.XPercent, box.Model.YPercent,
        box.Model.WidthPercent, box.Model.HeightPercent, _canvas.Size);

    /// <summary>把画布上的那一块写回份额。<b>像素 → 份额只在这一处转</b>，夹取由
    /// <see cref="ToPercents"/> 走 <see cref="StepLayout.Place"/>。</summary>
    private void Store(PlacedBox box)
    {
        var (x, y, w, h) = ToPercents(box.Rect, _canvas.Size);
        box.Model.XPercent = x;
        box.Model.YPercent = y;
        box.Model.WidthPercent = w;
        box.Model.HeightPercent = h;
    }

    /// <summary>按当前这一版配色刷一遍每一颗：<b>真控件还是真控件</b>，底和边是从主题上
    /// 读回来的（<c>UiTheme</c> 那一条：一个色号都不在代码里）。</summary>
    private void PaintBoxes()
    {
        var chosen = Selected();
        foreach (var box in _boxes)
        {
            var picked = box == chosen;
            var style = new StyleBoxFlat { BgColor = new Color(Accent.R, Accent.G, Accent.B, 0.05f) };
            style.BorderColor = new Color(Ink.R, Ink.G, Ink.B, picked ? 0.95f : 0.4f);
            style.SetBorderWidthAll(picked ? 2 : 1);
            style.SetCornerRadiusAll(6);
            style.ContentMarginLeft = 6;
            style.ContentMarginTop = 2;
            style.ContentMarginRight = 6;
            style.ContentMarginBottom = 2;
            box.AddThemeStyleboxOverride("panel", style);
            box.Caption.Text = CaptionOf(box.Model);
            UiTheme.Text(Theme, box.Caption, "HintLabel");
        }
    }

    /// <summary>控件那一行写什么：种类，字和图再加它自己装的那一份的头几个字
    /// （一个图控件装的是一个路径，路径长，一行放不下）。<b>不翻</b>路径和文字本身 ——
    /// 那是用户自己写的那一份（<c>GLOSSARY.md</c>「界面语言」：用户自己写的东西不进语言表）。</summary>
    internal string CaptionOf(StepLayout model)
    {
        var name = NameOf(model.Kind);
        var own = model.Kind switch
        {
            StepLayout.KindText => model.Text,
            StepLayout.KindImage => model.File,
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(own)) return name;
        var head = own.Length <= 18 ? own : own[..18] + "…";
        return $"{name} · {head}";
    }

    /// <summary>刻度线和手柄的强调色 —— 主题上那颗钮的底色（两张主题里各自那一支，
    /// 换配色不用改代码）。读不出来就回落到窗的字色，不静悄悄给一个看不见的颜色。
    /// <b>只有 <see cref="StyleBoxFlat"/> 有 <c>bg_color</c></b>，所以先问类型再读值 ——
    /// 读到别的那一类上就没有这一栏可读了。</summary>
    private Color Accent =>
        Theme?.GetStylebox("normal", "PrimaryButton") is StyleBoxFlat flat
            ? flat.BgColor
            : Ink;

    /// <summary>控件的边框取窗标题那一栏的字色（亮的那一版是近黑、暗的那一版是近白，
    /// 两边都在自己的底上看得见）。</summary>
    private Color Ink =>
        Theme != null && Theme.HasColor("font_color", "WindowTitle")
            ? Theme.GetColor("font_color", "WindowTitle")
            : Colors.Black;

    // ---- 拖放：引擎驱动的那一半 ---------------------------------------------------

    /// <summary>一颗被拿起来了（<c>PlacedBox._GetDragData</c> 叫过来）。
    /// <paramref name="at"/> 是<b>那颗控件里</b>的那一点（8.2 量到的：<c>at_position</c>
    /// 是控件内的点，不是 <c>INF</c>）。按住哪一块就跟着哪一块走，不按左上角走。</summary>
    internal bool BeginDrag(PlacedBox box, Vector2 at)
    {
        var index = _boxes.IndexOf(box);
        if (index < 0) return false;
        Select(box);
        _drag = index;
        _grab = at;
        // 让一帧再谈「松手了没」：<c>GuiIsDragging</c> 是拖动真正起来之后才为真的，
        // 拿来判「这一趟取消了」不能比起拖那一格还早。
        _dragFrame = Engine.GetProcessFrames();
        Log("layout", $"拿起 {NameOf(box.Model.Kind)} 那一颗，控件内的点是 ({at.X:F0},{at.Y:F0})");
        return true;
    }

    /// <summary>手挪到哪（<c>_CanDropData</c> 每一格叫一次，<paramref name="at"/> 是画布内的点）。
    /// <b>活的那一颗跟着走</b>，所以屏幕上看到的就是吸住之后的位置 —— 刻度线不是提示，
    /// 是这一档的结果本身。</summary>
    internal void DragTo(Vector2 at)
    {
        if (_drag < 0) return;
        var box = _boxes[_drag];
        var hit = SnapRect(new Rect2(at - _grab, box.Size), new Rect2(Vector2.Zero, _canvas.Size), Others(_drag));
        box.Rect = hit.Box;
        Guide(hit.Guides);
    }

    /// <summary>松手，落在合法落点上。<b>松手那一格也要吸</b>：最后一格不补上，
    /// 控件会在松手的那一格跳回没吸住的位置。</summary>
    internal void Drop(Vector2 at)
    {
        if (_drag < 0) return;
        DragTo(at);
        var box = _boxes[_drag];
        Store(box);
        _drag = -1;
        _resizing = -1;
        foreach (var one in _boxes) one.MouseFilter = Control.MouseFilterEnum.Stop;
        Guide(new Guides(false, null, null));
        Mark();
        Say($"{Tr("选中了")}{NameOf(box.Model.Kind)}", false);
        Log("layout", $"{NameOf(box.Model.Kind)} 落在 {Shares(box.Model)}");
    }

    /// <summary>这一趟拖到一半放掉了（松手的地方不是合法落点）。<b>撤回到动手之前那一份</b>：
    /// 没落成就不该留下半个动作 —— 而「不做撤销」说的是没有用户看得见的撤销栈，
    /// 不是留一个半截动作。</summary>
    internal void DragGone()
    {
        if (_drag < 0) return;
        var box = _boxes[_drag];
        _drag = -1;
        _resizing = -1;
        foreach (var one in _boxes) one.MouseFilter = Control.MouseFilterEnum.Stop;
        Place(box);
        Guide(new Guides(false, null, null));
        Say(Status, false);
    }

    /// <summary>每一帧问一句「还在拖吗」。<c>GuiIsDragging</c> 是本机实测读到为真的那一个
    /// （8.1 量到 C# 侧就这个名字）。<b>让一帧</b>是为了不和起拖那一格抢。</summary>
    internal void WatchDrag()
    {
        if (_drag < 0 || Engine.GetProcessFrames() <= _dragFrame) return;
        if (!GetViewport().GuiIsDragging()) DragGone();
    }

    /// <summary>除了第 <paramref name="skip"/> 颗之外的矩形 —— <b>自己不在自己的目标里</b>
    /// （8.5 的量具就是这么摆的：自己吸自己等于没动，那一档会读成「吸附不成立」）。</summary>
    private List<Rect2> Others(int skip) =>
        _boxes.Where((_, i) => i != skip).Select(b => b.Rect).ToList();

    /// <summary>把这一档算出来的两条线交给自绘那一层，并叫它重画。
    /// <b>显式重画是这一族的一条纪律</b>（8.7）。</summary>
    private void Guide(Guides guides)
    {
        _overlay.Vertical = guides.Vertical;
        _overlay.Horizontal = guides.Horizontal;
        _overlay.QueueRedraw();
    }

    // ---- 改大小 --------------------------------------------------------------------

    /// <summary>一颗开始改大小（<c>PlacedBox._GuiInput</c> 判到手柄上叫过来）。</summary>
    internal void BeginResize(PlacedBox box, Handle handle, Vector2 at)
    {
        _resizing = _boxes.IndexOf(box);
        _resizeHandle = handle;
        // 改大小期间**全部控件都不吃命中**：手一离开控件，事件就该落到画布那一档
        // （见 LayoutCanvas._GuiInput 那一段的注释）。只把手上那一颗放掉不够 ——
        // 手压着别的控件拖的时候，移动事件会被那颗吃掉，改大小就断在半路。
        foreach (var one in _boxes) one.MouseFilter = Control.MouseFilterEnum.Ignore;
        Log("layout", $"{NameOf(box.Model.Kind)} 开始改大小，手柄 {handle}，起点 ({at.X:F0},{at.Y:F0})");
    }

    /// <summary>手在画布上的哪一点（<c>LayoutCanvas._GuiInput</c> 交过来的，坐标是画布内的点）。</summary>
    internal void Resize(Vector2 at)
    {
        if (_resizing < 0) return;
        var box = _boxes[_resizing];
        var hit = ResizeTo(box.Rect, _resizeHandle, at, box.Rect, new Rect2(Vector2.Zero, _canvas.Size),
            Others(_resizing));
        box.Rect = hit.Box;
        Guide(hit.Guides);
    }

    /// <summary>松手，这一趟改大小落定。</summary>
    internal void EndResize()
    {
        if (_resizing < 0) return;
        var box = _boxes[_resizing];
        Store(box);
        _resizing = -1;
        _resizeHandle = Handle.None;
        foreach (var one in _boxes) one.MouseFilter = Control.MouseFilterEnum.Stop;
        Guide(new Guides(false, null, null));
        Mark();
        Say($"{Tr("选中了")}{NameOf(box.Model.Kind)}", false);
        Log("layout", $"{NameOf(box.Model.Kind)} 改成 {Shares(box.Model)}");
    }

    // ---- 工具栏那几颗 -------------------------------------------------------------

    /// <summary>往前一层 / 往后一层。<b>只换一层</b>，不是「顶到最上面」—— 重叠多起来之后
    /// 「顶上去」会把次序一次打乱，而换一层是能想得回来的动作。
    /// <b>次序住在列表里</b>（越靠后越在上面），所以这一族就是把那一行挪一格，
    /// 没有第二个 Z 字段可写（<see cref="StepLayout"/> 的类型注释那条）。</summary>
    private void Shift(int by)
    {
        if (_selected < 0) { Say(Tr("先选一颗控件"), true); return; }
        var to = _selected + by;
        if (to < 0 || to >= _boxes.Count) return;
        var model = _boxes[_selected].Model;
        // 后加的画在上面，所以「往前一层」= 往后挪一格。
        Editing.Layout.RemoveAt(_selected);
        Editing.Layout.Insert(to, model);
        Rebuild();
        Select(model);
        Say($"{Tr("选中了")}{NameOf(model.Kind)}", false);
        Changed?.Invoke();
        Log("layout", $"{NameOf(model.Kind)} 换了一层，现在在第 {to + 1} 层（共 {_boxes.Count} 层）");
    }

    /// <summary>删掉选中的那一颗。<b>删掉就是少一样</b>（设计文档 `docs/design/fader-layout.md`「六种都能改大小，都能删」）。
    /// 剩下的次序不动 —— 删中间一颗不重排，那会把别的控件的层也改了。</summary>
    private void Remove()
    {
        if (_selected < 0) { Say(Tr("先选一颗控件"), true); return; }
        var model = _boxes[_selected].Model;
        Editing.Layout.RemoveAt(_selected);
        Rebuild();
        Changed?.Invoke();
        Say($"{Tr("删掉")}{NameOf(model.Kind)}", false);
        Log("layout", $"删掉 {NameOf(model.Kind)}，还剩 {_boxes.Count} 颗");
    }

    /// <summary>回到默认布局：<b>整份换成默认那一份</b>（设计文档 `docs/design/fader-layout.md`「六种都能改大小，都能删」那一节：
    /// 编辑器里给一颗「回到默认布局」把它们全放回原位）。默认那一份住在 <see cref="StepLayout.Default"/> ——
    /// 「默认布局就是今天这一屏原样」那一节是它那一份的事，这里不重摆一次。</summary>
    private void Defaults()
    {
        Editing.Layout = StepLayout.Default();
        Rebuild();
        Changed?.Invoke();
        Say(Tr("回到默认布局"), false);
        Log("layout", $"回到默认布局：{_boxes.Count} 颗");
    }

    /// <summary>照抄上一步的布局。<b>没有前一步就照实说照不了</b> —— 摆一份默认的假装是抄来的，
    /// 用户看不出来，而那正是「一份读不通的说成「一个都没有」」那一族假象。
    /// <b>逐个 <see cref="StepLayout.Clone"/></b>：同一个引用的话，拖这一边会把那一边也拖了。</summary>
    private void CopyPrevious()
    {
        if (Before?.Layout == null)
        {
            Say(Tr("前面没有一步，照抄不了"), true);
            Log("layout", "照抄上一步：前面没有一步，没抄");
            return;
        }
        Editing.Layout = Before.Layout.Select(c => c.Clone()).ToList();
        Rebuild();
        Changed?.Invoke();
        Say(Tr("照抄了上一步的布局"), false);
        Log("layout", $"照抄上一步的布局：{_boxes.Count} 颗");
    }

    /// <summary>摆一个新的控件。文字和图<b>不限个数</b>，其余每种最多一个
    /// （设计文档 `docs/design/fader-layout.md`「文字和图不限个数…其余每种最多一个」）。那一条的判据在
    /// <see cref="StepLayout.Sanitize"/> 那一侧，这里只管摆出来 ——
    /// 所以工具栏上只有「摆一个文字」「摆一张图」两颗：别的五种都是独一份的，
    /// 摆第二颗没有意义（两个时钟永远显示同一个时刻）。</summary>
    private void Add(string kind)
    {
        var model = Fresh(kind);
        Editing.Layout.Add(model);
        Rebuild();
        Select(model);
        Changed?.Invoke();
        Say($"{Tr("摆了一个")}{NameOf(kind)}", false);
    }

    /// <summary>新摆一颗的那一份。<b>八个字段全从声明表问默认值</b>
    /// （<see cref="Settings.StepLayoutDeclarations"/>）—— 这一族不抄第二个数
    /// （ADR-0001「不建 schema 链」：一个限制只声明一次，界面读同一份）。
    /// 加过的地方是 <see cref="StepLayout.TextCap"/> 那一条：文字那一格的上限就是
    /// 布局里那个文字控件的上限，同一个数。</summary>
    private static StepLayout Fresh(string kind)
    {
        var one = new StepLayout();
        one.Set("kind", kind);
        foreach (var field in Fields) one.Set(field, Settings.Decl(StepLayout.KeyPrefix + field).Default);
        return one;
    }

    // ---- 选中那一颗自己的那几个字段 ------------------------------------------------

    private PlacedBox? Selected() => _selected >= 0 && _selected < _boxes.Count ? _boxes[_selected] : null;

    /// <summary>这一颗是不是选中的那一个（<c>PlacedBox</c> 问的：手柄只画在它身上，
    /// 所以也只判在它身上）。</summary>
    internal bool Chosen(PlacedBox box) => Selected() == box;

    /// <summary>它在第几颗（拖放那一族把序号带在拖的东西里）。</summary>
    internal int IndexOf(PlacedBox box) => _boxes.IndexOf(box);

    internal void Select(PlacedBox box) => Select(box.Model);

    private void Select(StepLayout model)
    {
        _selected = _boxes.FindIndex(b => b.Model == model);
        if (_selected < 0) Deselect();
        else
        {
            PaintBoxes();
            Props();
            _overlay.QueueRedraw();
        }
    }

    private void Deselect()
    {
        _selected = -1;
        PaintBoxes();
        Props();
        _overlay.QueueRedraw();
    }

    /// <summary>那一栏（字、摆法、字号）摆的是选中那一颗的哪几个字段。
    /// <b>没选中就只露 None 那一行</b>：一排灰着的输入框摆在那里，读起来像「这栏坏了」。
    /// <b>哪几个字段算这个控件的，问模型不问这里</b>（<see cref="StepLayout.HasWords"/>）——
    /// 图没有字这一格照在那一问里。</summary>
    private void Props()
    {
        var box = Selected();
        GetNode<Control>(NonePath).Visible = box == null;
        GetNode<Control>(WordsPath).Visible =
            box is { Model.Kind: StepLayout.KindText or StepLayout.KindImage };
        GetNode<Control>(FitPath).Visible = box is { Model.Kind: StepLayout.KindImage };
        GetNode<Control>(FontPath).Visible = box?.Model.HasWords == true;
        if (box == null) return;
        var model = box.Model;
        GetNode<Label>(WordsNamePath).Text =
            model.Kind == StepLayout.KindImage ? Tr("路径") : Tr("文字");
        _filling = true;
        GetNode<LineEdit>(WordsValuePath).Text = model.Kind == StepLayout.KindImage ? model.File : model.Text;
        GetNode<OptionButton>(FitValuePath).Selected =
            Math.Max(0, Array.IndexOf(StepLayout.FitModes, model.Position));
        GetNode<SpinBox>(FontValuePath).Value = model.FontPercent;
        _filling = false;
    }

    // ---- 抬头那两行 ---------------------------------------------------------------

    /// <summary>抬头下面那一句：<b>为什么这块画布是这么大</b>。契约给了理由
    /// （差十来个像素到屏上就是差一大截），那句话是给用户看的那一份；
    /// 屏有多大也一并写出来 —— 存的是份额，而份额的分母就是它。</summary>
    public string Status
    {
        get
        {
            var screen = ScreenSize();
            var shape = $"{Tr("画布和屏一个比例")}（{(int)screen.X}×{(int)screen.Y}）";
            return Subject.Length == 0 ? shape : $"{Subject} · {shape}";
        }
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

    /// <summary>改完一处：重画那一层、叫一声设置窗。<b>不做撤销</b>（契约：不撤销），
    /// 所以这一族没有「再撤一步」那条路。
    /// <b>名字那一行跟着走</b>：文字控件和图控件装的那一份是用户自己写的，
    /// 边打字边看那一行跟着变，才知道打进去的是不是自己要的。</summary>
    private void Mark()
    {
        var box = Selected();
        if (box != null) box.Caption.Text = CaptionOf(box.Model);
        _overlay.QueueRedraw();
        Changed?.Invoke();
    }

    /// <summary>控件的名字，界面上摆出来的那一份。<b>读的是布局里那一种</b>，
    /// 不读界面上别的标签 —— 这一族自己写进语言表，不借用别处的措辞。
    /// <b>倒计时那一族代码里叫 <c>remain</c>、界面上写「还剩」</b>
    /// （<c>GLOSSARY.md</c>「倒计时」：界面上那三个字仍然写「还剩」，那一条说的是它在
    /// 代码和文档里叫什么）—— 所以这一行借的是语言表里已有的那一条，不另起一个。</summary>
    public static string NameOf(string kind) => kind switch
    {
        StepLayout.KindClock => Lang.T("时钟"),
        StepLayout.KindRemain => Lang.T("还剩"),
        StepLayout.KindChecklist => Lang.T("休息清单"),
        StepLayout.KindBlock => Lang.T("时间块名"),
        StepLayout.KindEnd => Lang.T("提前结束休息"),
        StepLayout.KindText => Lang.T("文字"),
        StepLayout.KindImage => Lang.T("图"),
        // 不认识的种类**原样写出来**，不给它安一个别的控件的名字 ——
        // 「这是图」这句话要是落在一个不是图的东西上，屏幕上就找不出错在哪了。
        _ => kind,
    };

    /// <summary>图那五种摆法的<b>名字</b>，和 <see cref="StepLayout.FitModes"/> 按次序配对。
    /// <b>可选值那一列住在模型里</b>（顶层 <c>image.position</c> 读的是同一份），这里只补
    /// 五个字 —— 两处各写一份就是两份真相（ADR-0001「不建 schema 链」）。</summary>
    private static readonly string[] FitNames = ["填满", "适应", "拉伸", "平铺", "居中"];

    /// <summary>日志里说「这一颗在哪」。<b>只报份额</b>：像素是这一族换算出来的那一头，
    /// 报它会让人以为存的是像素。</summary>
    private static string Shares(StepLayout model) =>
        $"x={model.XPercent} y={model.YPercent} w={model.WidthPercent} h={model.HeightPercent} " +
        $"字号={model.FontPercent}";

    // ---- 自绘那一层和自检要问的那几样 ----------------------------------------------

    /// <summary>画布是多大（画布像素）。自绘那一层按它画屏的四边和中线。</summary>
    internal Vector2 CanvasSize => _canvas?.Size ?? Vector2.Zero;

    /// <summary>选中那一颗在画布上的那一块，没选就空。自绘那一层按它画手柄。</summary>
    internal Rect2 SelectedRect() => Selected()?.Rect ?? new Rect2();

    /// <summary>刻度线那一族要的那一条强调色。</summary>
    internal Color GuideColor => Accent;

    /// <summary>手柄和控件边框那一族的字色。</summary>
    internal Color HandleColor => Ink;

    /// <summary>界面上此刻摆着的那几颗（按下标就是列表次序，也就是叠的次序）。
    /// 自检和抓帧读它，而不是自己在树上找 —— 引擎给 <c>Duplicate</c> 出来的名字是自取的
    /// （同 <see cref="Presets.Rows"/> 那一段的注释）。<b>按类型是 internal</b>：
    /// <see cref="PlacedBox"/> 那颗类型是这一族内部的（它挂的脚本是这一个文件里的第二个类），
    /// 对外要读的是 <see cref="Rects"/> 和 <see cref="SelectedIndex"/> 那两样。</summary>
    internal IReadOnlyList<PlacedBox> Boxes => _boxes;

    /// <summary>界面上此刻摆着的那几块的矩形（画布像素），按叠的次序。
    /// <b>「在树上」不等于「摆对了地方」</b>（<c>CODING_STANDARDS.md</c> 那一条），所以判据读
    /// 摆出来的矩形，不读节点在不在。</summary>
    public IReadOnlyList<Rect2> Rects => _boxes.Select(b => b.Rect).ToList();

    /// <summary>选中了第几颗（按下标），没选是 -1。</summary>
    public int SelectedIndex => _selected;

    /// <summary>「点得到的那几块」那一块。<b>只算点得到的那两颗</b>（休息清单和「提前结束休息」），
    /// 而且这一步 <c>interactive</c> 关着时不算 —— 「清单看得见但点不动是一种合法状态」
    /// （契约 <c>interactive</c> 那一格）。
    ///
    /// <b>算法不再写在这里</b>（工单 02）：并矩形那一段是正本
    /// <see cref="ClickRegion.RectOf"/>，这一族和产品调的是同一段算术。
    ///
    /// <b>和编辑器画的那一块差在哪，写在明处</b>：这里喂的是<b>编辑器摆的框</b>，
    /// 产品那一份喂的是<b>引擎排完版之后控件自己的矩形</b>（`Fader` 那一侧）。
    /// 编辑器摆的就是引擎要摆的那个位置，两份量的是同一片 —— 但编辑器这一发
    /// 没有引擎排版可等，而产品那一侧要等三帧（`Fader.MeasureSwallowRect` 那一段）。
    /// 差的那一块：清单那几行之间有行距，编辑器量的是行框的外沿、引擎量的是控件的实占。</summary>
    internal Rect2 Clicked
    {
        get
        {
            if (Editing == null || !Editing.Interactive) return new Rect2();
            var boxes = _boxes
                .Where(b => b.Model.Kind is StepLayout.KindChecklist or StepLayout.KindEnd)
                .Select(b => new Rect2I((Vector2I)b.Rect.Position, (Vector2I)b.Rect.Size))
                .ToList();
            var merged = ClickRegion.RectOf(boxes);
            return new Rect2(merged.Position, merged.Size);
        }
    }
}

/// <summary>画布：<b>真 <see cref="Control"/></b>，摆出来的那几颗控件是它的真
/// <see cref="Panel"/> 子节点（本机实测 8.7 答掉的那一问：真控件还是自绘不是二选一，
/// 真控件和自绘是两层）。
///
/// 它身上是拖放的<b>落侧</b>（<c>_CanDropData</c> / <c>_DropData</c>）和<b>改大小的续命</b>。
/// <b>改大小的移动和松手落在这里</b>，不在控件上：手柄在边上，手压着往里拖的时候手早就在
/// 控件外面了，而控件收不到自己矩形外的移动。所以 <see cref="LayoutEditor.BeginResize"/>
/// 期间**全部控件都不吃命中**，事件一路落到这一颗上，坐标是<b>画布内的点</b> ——
/// 全程用控件内的坐标，不用虚化坐标（<c>CODING_STANDARDS.md</c>「代码级规矩」那一族里
/// 「虚化坐标 + 别的窗盖上来」那一发假读数的教训）。
///
/// **为什么落侧在画布上而不是在窗上**：<c>Window</c> 不是 <c>Control</c>，拿不到那三个虚函数
/// （8.1 量到它们在 <c>Control</c> 上）。而引擎是往<b>指针底下那一颗</b>问的，
/// 所以除了画布，每一颗摆出来的控件上也要各问一遍（见 <see cref="PlacedBox"/>）。</summary>
internal partial class LayoutCanvas : Control
{
    private readonly LayoutEditor _owner;

    public LayoutCanvas(LayoutEditor owner) => _owner = owner;

    public override void _Process(double delta) => _owner.WatchDrag();

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.Int || !_owner.Dragging) return false;
        _owner.DragTo(atPosition);
        return true;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.Int) return;
        _owner.Drop(atPosition);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_owner.Resizing)
        {
            // 改大小：手柄压在边上，手一动就跑到这一颗上了。
            if (@event is InputEventMouseMotion motion) _owner.Resize(motion.Position);
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            {
                _owner.EndResize();
                AcceptEvent();
            }
            return;
        }
        // 按在空白处：什么也不该发生。**吃掉这一发** —— 让它继续走就是给引擎一个可以起拖的
        // 地方，而画布上什么都没有可拖。
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            AcceptEvent();
    }
}

/// <summary>画布上面那<b>一层自绘</b>的：刻度线、「点得到的那几块」那一块、改大小手柄。
///
/// <b>引擎没有这一层</b>（<c>docs/research/godot-drag-and-snap.md</c> 第三节：五个类搜过，
/// 零命中），所以自己算自己画（ADR-0001「拖放走引擎的虚函数，刻度线和改大小手柄自己画」那一节）。
///
/// <b>它不吃命中</b>（<c>mouse_filter = Ignore</c>）：这一层只是画，命中全是底下那些真控件的事
/// —— 自绘换来的命中和 DPI 缩放全得自己管，而能点的那一段最要点得动（同 ADR-0001
/// 「界面默认用真控件」那一节的同一句话）。
///
/// <b>引擎重排不会抹掉这一层</b>（8.7 量到的），<b>但位置不会自己跟着变</b> ——
/// 所以每一次动了位置都由外面显式 <c>QueueRedraw</c>。</summary>
internal partial class LayoutOverlay : Control
{
    private readonly LayoutEditor _owner;

    /// <summary>这一档吸住的那条竖线的位置（画布内的 x），<c>null</c> = 那一轴上没吸。</summary>
    public float? Vertical { get; set; }

    /// <summary>这一档吸住的那条横线（画布内的 y）。</summary>
    public float? Horizontal { get; set; }

    public LayoutOverlay(LayoutEditor owner) => _owner = owner;

    public override void _Draw()
    {
        var size = _owner.CanvasSize;
        if (size.X <= 0 || size.Y <= 0) return;
        var accent = _owner.GuideColor;
        var ink = _owner.HandleColor;
        var faint = new Color(ink.R, ink.G, ink.B, 0.18f);
        var live = new Color(accent.R, accent.G, accent.B, 0.95f);

        // 屏的四边和中线：常驻。画布按屏的比例显示，那么画布的边就是屏的边、中线也是屏的
        // 中线 —— 这是契约点名要吸的其中两条，先让它们看得见。
        DrawRect(new Rect2(Vector2.Zero, size), faint, false, 1);
        DrawLine(new Vector2(size.X / 2f, 0), new Vector2(size.X / 2f, size.Y), faint, 1);
        DrawLine(new Vector2(0, size.Y / 2f), new Vector2(size.X, size.Y / 2f), faint, 1);

        // 「点得到的那几块」那一块（那两个控件摆到哪，它就算到哪，**编辑器里画出来给他看得见**）。
        // 画成虚线：它不是一颗控件，是一块矩形的边界。2026-10-10 之后它不再有窗 ——
        // 交给全局钩子按坐标判（ADR-0003「点得到的那几块交给钩子按坐标判」那一节）。
        var clicked = _owner.Clicked;
        if (clicked.Size.X > 1 && clicked.Size.Y > 1)
            Dashed(clicked, new Color(accent.R, accent.G, accent.B, 0.7f));

        // 这一档吸住的那两条线：从画布的一边通到另一边，全长。
        if (Vertical != null)
            DrawLine(new Vector2(Vertical.Value, 0), new Vector2(Vertical.Value, size.Y), live, 1);
        if (Horizontal != null)
            DrawLine(new Vector2(0, Horizontal.Value), new Vector2(size.X, Horizontal.Value), live, 1);

        // 手柄只在选中那一颗身上画 —— 每一颗都摆八个手柄，这一块就看不见画布了。
        var box = _owner.SelectedRect();
        if (box.Size.X <= 0 || box.Size.Y <= 0) return;
        foreach (var (at, handle) in Handles(box))
        {
            var side = handle is LayoutEditor.Handle.N or LayoutEditor.Handle.S
                ? new Vector2(LayoutEditor.HandleSize, 2f)
                : new Vector2(2f, LayoutEditor.HandleSize);
            DrawRect(new Rect2(at - side / 2f, side), live, true);
        }
    }

    /// <summary>虚线边框。**画的是一扇窗的边界，不是控件的选中态** ——
    /// 那个形状和控件描边长得不一样，人才分得清自己在看什么。</summary>
    private void Dashed(Rect2 box, Color colour)
    {
        const float dash = 5f;
        const float step = 10f;
        for (var x = box.Position.X; x < box.End.X; x += step)
        {
            var to = Math.Min(x + dash, box.End.X);
            DrawLine(new Vector2(x, box.Position.Y), new Vector2(to, box.Position.Y), colour, 1);
            DrawLine(new Vector2(x, box.End.Y), new Vector2(to, box.End.Y), colour, 1);
        }
        for (var y = box.Position.Y; y < box.End.Y; y += step)
        {
            var to = Math.Min(y + dash, box.End.Y);
            DrawLine(new Vector2(box.Position.X, y), new Vector2(box.Position.X, to), colour, 1);
            DrawLine(new Vector2(box.End.X, y), new Vector2(box.End.X, to), colour, 1);
        }
    }

    /// <summary>八个子柄落在哪（画布内的点 + 是哪一个）。<b>纯算术</b>，命中判定在
    /// <see cref="LayoutEditor.HandleAt"/> —— 画的和判的是同一套位置，
    /// 不然画出来的地方和点得动的地方会错开。</summary>
    internal static IEnumerable<(Vector2 At, LayoutEditor.Handle Handle)> Handles(Rect2 box)
    {
        var mid = box.Position + box.Size / 2f;
        yield return (box.Position, LayoutEditor.Handle.NW);
        yield return (new Vector2(mid.X, box.Position.Y), LayoutEditor.Handle.N);
        yield return (new Vector2(box.End.X, box.Position.Y), LayoutEditor.Handle.NE);
        yield return (new Vector2(box.End.X, mid.Y), LayoutEditor.Handle.E);
        yield return (box.End, LayoutEditor.Handle.SE);
        yield return (new Vector2(mid.X, box.End.Y), LayoutEditor.Handle.S);
        yield return (new Vector2(box.Position.X, box.End.Y), LayoutEditor.Handle.SW);
        yield return (new Vector2(box.Position.X, mid.Y), LayoutEditor.Handle.W);
    }
}

/// <summary>摆出来的那一颗控件：<b>真 <see cref="Panel"/></b>，位置和大小由份额换出来
/// （本机实测 8.7：画布是真控件、子控件是真 Panel，焦点、主题、命中全走引擎）。
///
/// 它身上是拖放的<b>源侧</b>（<c>_GetDragData</c>，8.2 量到：调得到、<c>at_position</c>
/// 是控件内的点）、<b>落侧</b>（引擎往指针底下那一颗问，而指针底下可能是另一颗）、
/// 以及手柄的命中。
///
/// <b>源和落两侧共用一份实现</b>：C# 不许一颗同时 override 两次（测量程序里那一份也是
/// 这么写的），而两侧问的是同一件事 —— 问「这一趟是不是我们的拖」。</summary>
internal partial class PlacedBox : Panel
{
    private readonly LayoutEditor _owner;

    /// <summary>它在配置里那一份。<b>这一颗改的就是它</b> —— 界面上不存第二份
    /// （ADR-0001「不建 schema 链」：一份事实只活在一处）。</summary>
    public StepLayout Model { get; }

    /// <summary>控件自己那块底上写着它是什么（种类，字和图再加装的那一份的头几个字）。
    /// <b>不吃命中</b>：名字那一行不是一颗能点的控件。</summary>
    public Label Caption { get; }

    /// <summary>它在画布上的那一块（画布内的像素）。<b>外面显式给位置</b>：
    /// 引擎重排不会替我们跟着变（8.7 量到的那一条）。</summary>
    public Rect2 Rect
    {
        get => new(Position, Size);
        set
        {
            Position = value.Position;
            Size = value.Size;
        }
    }

    public PlacedBox(LayoutEditor owner, StepLayout model)
    {
        _owner = owner;
        Model = model;
        Caption = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AddChild(Caption);
        // **名字那一行拉满这一颗**（锚点是它的，跟着这一颗改大小一起走）；
        // 这一颗自己的四个锚点全留在 0 —— 它是画布上一块由份额算出来的矩形，
        // 引擎不许替它重排（同 FitCanvas 那一段的注释）。
        Caption.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (!_owner.BeginDrag(this, atPosition)) return default;
        // 拖动预览是引擎画的那一份（8.2：<c>SetDragPreview</c> 调得到）。它只画一个框：
        // **活的那一颗自己是跟着手走的**，再来一块填的预览就是同一件东西画两遍。
        var ghost = new Panel { CustomMinimumSize = Size, MouseFilter = Control.MouseFilterEnum.Ignore };
        var edge = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
        edge.BorderColor = _owner.GuideColor;
        edge.SetBorderWidthAll(1);
        ghost.AddThemeStyleboxOverride("panel", edge);
        SetDragPreview(ghost);
        return Variant.From(_owner.IndexOf(this));
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.Int || !_owner.Dragging) return false;
        // 落点在这一颗的坐标系上，换成画布的再交出去。
        _owner.DragTo(Position + atPosition);
        return true;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.Int) return;
        _owner.Drop(Position + atPosition);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press)
            return;
        var handle = _owner.Chosen(this)
            ? LayoutEditor.HandleAt(press.Position, Size, LayoutEditor.HandleBand)
            : LayoutEditor.Handle.None;
        if (handle == LayoutEditor.Handle.None)
        {
            // **不吃这一发**：起拖靠的就是引擎收到这一发（8.3 量到的那一趟：按下 → 逐格挪 →
            // 松开）。在这里 AcceptEvent 的话，拖放那一段就轮不到引擎处理了。
            _owner.Select(this);
            return;
        }
        _owner.BeginResize(this, handle, press.Position);
        AcceptEvent();
    }
}
