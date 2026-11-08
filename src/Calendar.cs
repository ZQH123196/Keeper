using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;

namespace Keeper;

/// <summary>桌面日历：一扇画在离屏视口上的窗，像素推进图标层底下借来的那一扇；
/// 三档画法共用同一套格子和同一套算术。
/// 宿主生命周期（Hidden / Parked / Editing 加意图位）不住在这一份里 —— 它在
/// <see cref="CalendarHost"/>，分工与设计在 <c>docs/design/calendar.md</c>
/// 「宿主生命周期」那一节。
///
/// ```
/// Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/App.tscn -- --calendar
/// ```
///
/// 四条前提全清了才动它（那四条的实测结果在 <c>docs/research/probe-and-criteria-pitfalls.md</c>「测量程序和判据的写法坑」那一条）：
/// 喂得进（触屏那一型，2026-09-25）、命中测试归我们、`Win+D` 扫不走、嵌着还自己出帧（后三条同一天）。
/// 桥的闸门按「那一点归不归我这扇窗」开，见 <see cref="MouseBridge"/>。
///
/// <b>三档从 <c>calendar.style</c> 派发</b>（设计文档 `docs/design/calendar.md`），这一条以前没人读：
/// 声明表的默认值是 <c>wallpaper</c>，而代码里只画 widget 那一档，所以文档说 wallpaper、
/// 屏幕上出的是 widget。现在 <see cref="Measure"/> 按样式
/// 走 <see cref="MeasureGrid"/> 或 <see cref="MeasureWidget"/>，屏幕上跟着变。
///
/// <list type="bullet">
/// <item><c>wallpaper</c> —— 整月铺在桌面上，每格把自己的备注写在格子<b>里面</b>。
/// 尺寸由 <c>widthPercent</c>/<c>heightPercent</c> 沿工作区两条边各取一条，
/// <c>corner</c> + <c>marginXPercent</c>/<c>marginYPercent</c> 定位（那两条也是百分比）。
/// 格子里三样：日号、农历或节日、备注。</item>
/// <item><c>outline</c> —— 同一套格子，<b>一颗底色都不填</b>：两档共用一套控件，只差
/// <see cref="Layout.Fills"/> 这一旗，所以 <c>_draw</c> 那一发不必为它多写一条。
/// <b>契约里那一档还有细线格</b>（设计文档 `docs/design/calendar.md` 的「bare rules and text」），今天实物读不到
/// 线 —— 那一发欠在 <c>.scratch/keeper-debt/issues/09-outline-rules-not-drawn.md</c>，补它才要写 <c>_draw</c>。</item>
/// <item><c>widget</c> —— 角落里那块小面板：格子里只有日号，农历和备注在格子下面那一行。
/// 布局是这一版定的：抬头 <c>‹ 2026年9月 ›</c>、
/// 周一到周日一行、月历格子（行数随月份走，2026 年 9 月那发是 5 行）、格子下面一行
/// 「9月24日周四 农历八月十四」，再下面一行是那一天的备注。尺寸由 <c>widgetWidthPercent</c>
/// 定：面板横向占工作区多少，格子和字再从它推。</item>
/// </list>
///
/// <b>这一份布局里没有写死的分辨率</b>（2026-09-26 下午改的）：以前 widget 那一档的基准是
/// 64x62 那套像素常量，格子那两档的抬头一带和字号是代码里的比例，离那个角的距离是 24 颗像素 ——
/// 换一块屏就得重挑一遍。现在链子是「工作区 × 配置里的百分比 → 面板 → 格子 → 字」，
/// 每一环都是比例，而比例全在 <c>settings.jsonc</c> 里可调（<see cref="CalendarOptions"/>）。
/// 只剩两根按像素算的线：字号那根「再小就读不出来」的下限（乘系统缩放），
/// 和「一行字不许挤过它那一行」的上限（按宽收，收不下就不画那一行）。
/// 换分辨率不许换比例这一条，<see cref="SelfTest"/> 在四块形状不同的工作区上各量一遍。
///
/// 格子那一档的抬头和 widget 不同：月份在左、<c>‹ ›</c> 挨着它，右边那一行是
/// 「今天是 2026年9月24日星期四 农历八月十四」—— 格子那两档的抬头就是这么排的，
/// 而 widget 那一档这一行在页脚。<b>页脚只有 widget 有</b>：格子那两档的备注住在格子里。
///
/// 编辑态是同一个窗的第二状态（契约 <c>.scratch/keeper-contract/spec.md</c>，<c>## User Stories</c>）：一扇 <c>WS_CHILD</c> 永远拿不到键盘焦点，
/// 所以也就永远不会有输入法 —— 双击一格，把窗抬回顶层、提到最前、给焦点，写完提交或点到别处
/// 再放回原来那块像素。桥在抬起那一段整条闭着，那时 OS 自己会投消息。格子那一档抬起的是
/// 一扇 1792x966 的窗，抬起那一段它盖住矩形以内的一切（2026-09-26 <c>--grid</c> 那一发读到的），
/// 输入框摆在被选中的那一格里。
///
/// 颜色这一侧是我定的，不是从契约来的：格子的底是同一种黑换三档不透明度（平日 / 周末 / 跨月），
/// 选中那格淡蓝底、今天那一格描一颗蓝边，字近白。这几个色值没有出处可读 ——
/// 「压白页反推 alpha、绝对色值读不出来」那条量过，记在 <c>docs/research/calendar-and-fader-geometry.md</c>
/// 「格子的底」那一条，
/// 而 <c>calendar.*</c>
/// 里管颜色的只有一处 <c>opacityPercent</c>。要反过来，只说一声。</summary>
public partial class Calendar : Window
{
    public const string WindowTitle = "keeper-calendar";

    // ---- 布局的那些比例 ---------------------------------------------------------------
    //
    // 这一节里没有一个像素。下面每个常量的单位都是百分之一，而且都写清了它是**谁**的百分之几：
    // 工作区 → 面板 → 格子 → 字，一环推一环。所以换一块屏换的是画面，不是那份配置 ——
    // 1280 的笔记本和 3840 的屏上，同一份设置长出的是同一张月历，只是各自大或小。
    // 唯一按像素算的两条线：「再小就读不出来」那根下限（<see cref="MinFontAt96"/>，乘的是系统
    // 缩放而不是分辨率），和「一行字不许挤过它那一行」那根上限（按宽收，收不下就不画那一行，
    // 见 <see cref="MonthLineChars"/> 那一条）。

    /// 一周七列，横着等分。
    public const int Columns = 7;

    /// 抬头那一整带里，月份那一行取 56%（=9/16），剩下的归星期那一行。
    public const int MonthLineShare = 56;

    /// widget 那一档：格子的**高是宽的** 97%（这一版定的取值）。
    public const int WidgetCellAspectPercent = 97;

    /// 一行字要多少高：Godot 那份默认字体一行是字号的 1.4 倍（13 号要 18 像素，13×1.4=18.2 往上
    /// 取 —— 2026-09-26 那一发量出来正是 18）。页脚那一行归日期和备注两行字，所以它按字给、不按格子给。
    public const int FontLinePercent = 140;

    /// 那颗 <c>LineEdit</c> 的最小高度是「它的字号乘二」（13 号那一发量出来是 26 像素）。
    /// 页脚按这一条留，就不会再长出「输入框画在面板下沿之外」那一发 —— 此前把页脚从 32
    /// 抬到 40 是在按像素追它，按字号留是把它一次追掉。
    public const int EditorHeightOfFontPercent = 200;

    /// 格子那两档的字：抬头那一整带的 36% 和 22%，格子的 15% / 11% / 11%。
    public const int GridTitleFontShare = 36, GridWeekdayFontShare = 22,
        GridNumberFontShare = 15, GridSubFontShare = 11, GridNoteFontShare = 11;

    /// widget 那一档的字：一整带（它就是一格高）的 29% 和 21%，格子的 24% 和 21%。
    /// 两档的比例不一样是分档定的，不是我懒得合：62 像素高的格子里摆 15% 那种 9 号字读不出来，
    /// 而 179 像素高的格子里摆的本来就是 15 号字那一档 —— 同一根比例套不住两种格子。
    public const int WidgetTitleFontShare = 29, WidgetWeekdayFontShare = 21,
        WidgetNumberFontShare = 24, WidgetFooterFontShare = 21;

    /// 各档自己的「小件」：<c>‹ ›</c> 那颗钮、圆角、格子里那道内边距、格子里日号那一行，
    /// 一律是抬头一带 / 格子宽 / 页脚高的百分比。
    public const int GridCellInsetPercent = 5, WidgetCellInsetPercent = 12,
        WidgetNavShare = 55, WidgetRadiusShare = 10, WidgetCellRadiusShare = 6,
        WidgetNumberTopShare = 19, WidgetNumberHeightShare = 32;

    /// 96 DPI 下「中文两个字还认得出来」那一档字号。引擎不跟系统缩放放字（<c>ScreenGetScale</c>
    /// 在 125% 的屏上报 1，实测结果在 <c>docs/research/desktop-layer-and-window-measurements.md</c> DPI 那一条），所以这根下限得自己
    /// 乘缩放 —— 它是这一节里唯一按像素算的一条线。
    public const int MinFontAt96 = 10;

    /// 一行字按「多少个汉字当量」收上限，配着 <see cref="FontLinePercent"/> 那条按高的收：
    /// 抬头那一行是「2026年9月」，「今天是…」那一行最长，格子里次行那一列最长是三个字的节日名，
    /// 而 widget 那一档的格子里摆的是两个数字的日号。
    /// 这几条是量出来的当量，不是拍的：<c>textPercent</c> 拧到 140% 那一帧上宽那一头没人管，
    /// 于是「二十」压在下一格的「2」上面、抬头那行「农历八月」直接出屏。
    public const int MonthLineChars = 7, TodayLineChars = 16, SubLineChars = 3,
        NumberLineChars = 2;


    /// 两次按下算「双击」的最大间隔。设计文档 `docs/design/calendar.md` 说双击得桥自己判 ——
    /// shell 不会替我们决定这件事，因为它根本不投这一发。500 毫秒是 Windows 默认的双击间隔。
    public const double DoubleClickMs = 500;

    /// <summary>手上这份设置。<b>不是 readonly</b>：导入还回来的是一份<b>新的</b>读取结果，
    /// 里面 <c>Current</c> 指向<b>另一个</b> <see cref="Settings"/> 对象（见
    /// <see cref="SettingsStore.Import"/>），App 那一侧跟着换，这扇窗也得跟着换 ——
    /// 不然它继续读旧的那一份，文档改了屏幕上看不出来。走 <see cref="UseSettings"/>。
    ///
    /// 2026-09-29 之前这一条写的理由是「<c>Read.Current</c> 是个可变字段，导入就地换掉它」，
    /// 还带着一个行号。那个字段现在不可变了，但「导入之后是另一个对象」这件事没变，
    /// 所以这个字段仍然不能 readonly —— 理由换了，结论没换。</summary>
    private Settings _settings;
    private readonly CalendarNotes _notes;
    private readonly Activity _activity;
    private readonly int _screen;

    /// <summary>系统缩放，100 = 100%。像素那两根下限（字号、输入框最小高度）乘的是它。
    /// 读的是 <c>ScreenGetDpi</c>；它对不对由 <see cref="Park"/> 那行日志里
    /// <c>GetDpiForWindow</c> 那一条对照着说（<c>docs/research/desktop-layer-and-window-measurements.md</c> DPI 那一条量过：
    /// <c>ScreenGetScale</c> 在 125% 的屏上报 1，所以缩放不能拿它当实测结果）。</summary>
    private readonly int _dpiPercent;
    private readonly Action<string> _log;
    private readonly Action _saved;
    private readonly MouseBridge _bridge;

    /// <summary>桥喂不喂。<b>2026-10-12 起默认喂</b>，理由是这一扇换了性质：
    /// 它现在带 <c>WS_EX_TRANSPARENT</c>（<see cref="DesktopLayer.Park"/>），鼠标<b>穿过去</b>
    /// 落到图标层，于是 OS 再也不会把鼠标消息投进这一扇 —— 格子收不到任何点击，
    /// 桥成了它唯一的入口。2026-09-26 那条「OS 自己会投，别喂」量的是挂进
    /// <c>SHELLDLL_DefView</c> 那一版，那一版不带 <c>WS_EX_TRANSPARENT</c>，
    /// 两路一起喂的后果是「一次点击算两次」。实测结果在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c>。
    ///
    /// <b>喂给格子的前提是「那一点不在图标上」</b>（<see cref="MouseBridge.ShouldFeed"/>）：
    /// 鼠标穿过去是给图标的（拖放、选中、双击打开全归 shell），桥再喂一发就是
    /// 一次点击两处响应 —— 双击图标去打开它，却顺手把日历抬起来打字。所以判据里有一问
    /// 「图标层那一点上有没有东西」。
    ///
    /// <c>--feed</c> 那一发还能把这一旗整个关掉，用来量「关掉之后格子收不到点击」，
    /// 关掉的那一版就是「真点不动」的样子。</summary>
    private bool _bridgeOn = true;

    /// <summary>一格。三档共用这一个形状：widget 那一档只用 <see cref="Number"/>，
    /// 格子那两档把 <see cref="Sub"/>（农历或节日）和 <see cref="Note"/>（那一天的备注）
    /// 也摆出来 —— 设计文档 `docs/design/calendar.md`「三档样式」那一节那句「每格把自己的备注写在格子里」说的就是这一发。</summary>
    private sealed record Cell(DateOnly Day, Button Box, Label Number, Label Sub, Label Note);

    private readonly List<Cell> _cells = [];
    private Control _root = null!;
    private Panel _panel = null!;
    private Label _monthLabel = null!;
    private Label _dateLine = null!;
    private Label _noteLine = null!;
    private LineEdit _editor = null!;
    /// 编辑区那一圈边界。格子那两档底下那一块底板是收着的（outline 要的是「什么都不填」），
    /// 所以它单独一颗；<c>ZIndex</c> 抬到格子上头，否则格子的底会盖住那一圈边。
    private Panel _frame = null!;
    private Button _prev = null!;
    private Button _next = null!;

    /// <summary>宿主生命周期那一族（卡片 03 · S27）。三档、意图位、看门狗和补推的计时
    /// 都搬进这里；引擎句柄搬进它的真 adapter（<c>DesktopHostOps</c>），这一侧从
    /// <see cref="CalendarHost.EngineHandle"/> 读。
    ///
    /// <para><b>日历那一块像素推给哪一扇</b>：<b>引擎自己那一扇窗不给人看</b> ——
    /// 它挂进壳的父子链之后一个像素都不画（三个爹各试一次、三种逼法都试过，全是 0 像素变化）。
    /// 日历照旧在场景里用真控件搭、照旧由引擎画到一块离屏视口上，像素经
    /// <see cref="CalendarHost.Push"/> 推进 <c>NativeLayer</c> 那一扇原生子窗 ——
    /// 取向在 <c>docs/adr/0007-the-calendar-window-is-under-the-icons.md</c>，
    /// 分工在 <c>docs/design/calendar.md</c>「宿主生命周期」那一节。</para>
    ///
    /// <para><b>代价写在明处</b>：「日历看得见」这件事不归引擎管 ——
    /// 原生子窗不出像素就是黑的，<see cref="NativeLayer.CalendarIsVisible"/> 那一问
    /// 是唯一的凭据。</para></summary>
    internal CalendarHost Host { get; }

    /// <summary>推给原生子窗的那一块（屏幕坐标）。<b>和 <see cref="_placedAt"/> 分开记</b>：
    /// 那一块是引擎窗摆的地方，而原生子窗可能挂在别的宿主底下。</summary>
    private Win32.Rect _pushedAt;

    /// <summary>上一次读回的字节数。<b>0 就是「一个字都没推进去」</b> ——
    /// 「日历看得见」那一问读它，而它是那个失败模式唯一的出口。</summary>
    private int _pushedBytes;

    /// <summary>画面真变了没有。<b>由 <see cref="Paint"/> 和 <see cref="ApplyLayout"/> 置上</b> ——
    /// 「变了没有」是引擎那一侧的事，而读回是同步的，每帧做一次就是每帧卡一次。</summary>
    private bool _dirty = true;

    private Action<Activity.InputEvent, long>? _onHook;
    private DateOnly _shownMonth;
    private DateOnly _selected;
    private DateOnly _today;
    private DateOnly? _lastPressed;
    private long _lastPressedMs;
    private long _lastClickId;
    private bool _focusRead;
    private double _sinceTick;
    /// 这一扇现在摆在屏幕上哪一块。<c>Tick</c> 每秒拿它和工作区比：
    /// 变了就重排一遍（换分辨率、换缩放、任务栏挪边都走这一条，工单 27 拆点 5）。
    private Win32.Rect _placedAt;
    private int _cellGui, _cellTaps, _cellReleases, _cellMouseDowns, _cellMouseUps;

    /// <summary>测量程序那一发：某一格中心的屏幕点，和「那一点归谁」的实测结果。
    ///
    /// 为什么先问归谁再打：注入的那一发点击是真的、会落到当时在那一层的东西上。
    /// 上一轮（<c>App.Inject</c> 给遮罩用的那一段）立的规矩就是这一条 —— 不归我们就取消，
    /// 发出去是点别人的窗口。</summary>
    internal (Win32.Point At, bool Mine, string Measurement) CellProbe(DateOnly day)
    {
        var slot = _cells.FirstOrDefault(c => c.Day == day);
        if (slot == null) return (default, false, $"这一月里没有 {day:yyyy-MM-dd} 这一格");
        var cell = slot.Box;
        Win32.GetWindowRect(Host.EngineHandle, out var rect);
        var local = cell.GetGlobalRect().GetCenter();
        var at = new Win32.Point { X = rect.Left + (int)local.X, Y = rect.Top + (int)local.Y };
        var (owner, measurement) = DesktopLayer.OwnerOf(Host.EngineHandle, at);
        // 「该不该打」问的是闸门那三问里的后两问，**不是命中测试**：这一扇带
        // `WS_EX_TRANSPARENT`，鼠标穿过去落到图标层，于是 `WindowFromPoint` 在它自己
        // 那块矩形上恒答「不归我们」—— 闸门换掉那一问的理由（见 `MouseBridge.ShouldFeed`）。
        // 原来这里拿 `owner == Ours` 当判据，结果是每一发都被自己取消掉。
        var overCells = DesktopLayer.OverCells(rect, at);
        var onIcon = overCells && DesktopLayer.OnIcon(at);
        return (at, overCells && !onIcon,
            $"{measurement}；格子={day:MM-dd} 窗矩形={rect} 格内点=({local.X:F0},{local.Y:F0}) 层={Host.StateNow} " +
            $"在我们这块矩形里={overCells} 图标={onIcon}");
    }

    /// <summary>测量程序那一问：屏幕上这一点落在哪一格 —— 那一格是几号、那一格在屏上占哪一块。
    ///
    /// <para><b>为什么方向和 <see cref="CellProbe"/> 相反</b>：那一发是「给一天，答那一点」，
    /// 这一发是「给一点，答是哪一天」。独立进程手上只有一扇窗的句柄，从那个方向答不出 ——
    /// 而反推出来的是猜的（ADR-0004 那一节否掉的正是「靠屏幕反推产品状态」）。</para>
    ///
    /// <para><b>为什么只能是这一份</b>：格子表是私有的，摆着的那一月也没有成员答得出来
    /// （<see cref="SelectedDay"/> 答的是「选中」，翻过月份之后它和摆着的那一月不是同一个值），
    /// 所以「一点 → 哪一格」除了这里没有第二个出处。</para>
    ///
    /// <para><b>屏幕坐标按 <see cref="CellProbe"/> 那一发同样的加法换</b>：控件的全局矩形
    /// 是窗内的那个，加窗自己在屏上的位置才是屏幕上的那一个。</para>
    ///
    /// <para><b>答不出就是 null</b>，两种：窗没在屏幕上（<see cref="CalendarHost.Wanted"/> 为 false ——
    /// 藏着的时候答一个日号，是把「那一格没露在屏上」说成了「那一格在那儿」），
    /// 或者这一点不落在任何一格里。</para></summary>
    internal (int Day, Rect2I Rect)? CellAt(Win32.Point at)
    {
        if (Host.EngineHandle == 0 || !Host.Wanted) return null;
        Win32.GetWindowRect(Host.EngineHandle, out var window);
        var origin = new Vector2I(window.Left, window.Top);
        var local = new Vector2I(at.X - window.Left, at.Y - window.Top);
        foreach (var cell in _cells)
        {
            var box = (Rect2I)cell.Box.GetGlobalRect();
            if (!box.HasPoint(local)) continue;
            return (cell.Day.Day, new Rect2I(box.Position + origin, box.Size));
        }
        return null;
    }

    internal string SelectedDay => _selected.ToString("yyyy-MM-dd");

    /// 这扇窗现在在屏幕上占的那块矩形。测量程序拿它问「哪些窗压在我身上、会被这一发扫到」。
    internal Win32.Rect ScreenRect
    {
        get
        {
            Win32.GetWindowRect(Host.EngineHandle, out var rect);
            return rect;
        }
    }

    /// <summary>桥那三个数和窗这一侧的三个计数，成对看：<c>喂=2 收到=4</c> 是喂了两发、
    /// 每发按下松开各算一次控件收到；<c>喂=0 丢=2</c> 是闸门关着（那一点不归我们，或者正抬着打字）。
    /// <c>--calendar</c> 那一发每 5 秒报一次。
    ///
    /// 末尾把图标那一问的读数一起带上：闸门第三问答得对不对、图标位置读到没有，
    /// 都在这一行里 —— 而那两件事各问各的话，分不开「闸门关着」和「图标没读出来」。</summary>
    public string BridgeReport() =>
        $"喂={_bridge.Fed} 点击={_bridge.FedClicks} 丢={_bridge.Dropped} 层={Host.StateNow} 控件收到={_cellGui}" +
        $"（触屏 按下={_cellTaps}/松开={_cellReleases}，鼠标 按下={_cellMouseDowns}/松开={_cellMouseUps}）" +
        $"选中={SelectedDay} 最近一发[{_bridge.LastDecision}]{IconCountReport()}";

    /// 图标那一问的读数：读到几颗图标、这一读答得怎么样。
    /// <b>纯读</b>，谁都能打这一行（含 <c>--icon-columns</c> 那一发）。
    /// 「读不出来」要原样摆出来 —— 那一档闸门关着，屏幕上分不出来，只有这一行分得出来。
    ///
    /// <para><b>2026-10-15 改名</b>：原先叫 <c>IconColumnsReport</c>，名字里的「列」是让位
    /// 那一族的说法（图标占住的那几列），那一族随方向翻掉一起撤了
    /// （方向与被否的路在 <c>docs/adr/0007-the-calendar-window-is-under-the-icons.md</c>）。这一行现在答的是图标颗数和图标位置
    /// 本身（<see cref="DesktopIcons.Current"/>），名字跟着答什么走。</para></summary>
    public string IconCountReport()
    {
        var answer = DesktopIcons.Current;
        return $"：图标{answer.Icons.Count} 颗（{answer.Note}）";
    }

    public Calendar(Settings settings, int screen, CalendarNotes notes, Activity activity,
        Action saved, Action<string>? log = null)
    {
        _settings = settings;
        _screen = screen;
        _notes = notes;
        _activity = activity;
        _saved = saved;
        _log = log ?? (_ => { });
        Host = new CalendarHost(new DesktopHostOps(this), _log);
        _bridge = new MouseBridge(this, () => _bridgeOn && Host.StateNow == CalendarHost.State.Parked, _log);

        Title = WindowTitle;
        Borderless = true;
        Transparent = true;
        // 2026-09-26 那一帧 outline 逼出来的一行，说的是对的意图：背景不该由引擎涂满。
        // 它真正管用的是今天这一族 —— 这一扇现在靠它把「没画东西的地方」留成全透明，
        // 底下那片桌面（壁纸 + 图标）才是透过来的。
        // 2026-09-26 记的那句「嵌着仍读到 (0,0,0)」属于挂进壁纸层那一版，2026-10-12 没有了。
        TransparentBg = true;
        Unresizable = true;
        // 和遮罩那一发相反：遮罩要永远拿不到焦点，日历抬起打字的那一段必须拿得到。
        // 嵌进壁纸那一段它照样拿不到 —— WS_CHILD 拿不到，不是我们设了不让它拿。
        Unfocusable = false;
        var now = DateOnly.FromDateTime(DateTime.Now);
        _today = now;
        _selected = now;
        _shownMonth = new DateOnly(now.Year, now.Month, 1);
        // 布局的每一处尺寸都是比例，只有那两根「读不出来就别写」的下限按像素算 —— 而它们乘的是
        // 系统缩放，不是分辨率。ScreenGetDpi 报不报真数由下面这行日志说，Park 之后再拿
        // GetDpiForWindow 对照一次。
        var dpi = DisplayServer.ScreenGetDpi(screen);
        _dpiPercent = dpi > 0 ? (int)(dpi * 100 / 96) : 100;
        _log($"日历：屏 {screen} 缩放实测结果 dpi={dpi} → 像素下限按 {_dpiPercent}% 算（引擎另有一条 ScreenGetScale，" +
             "在 125% 的屏上报 1，见 findings 第八节 DPI 那一条）");
        BuildUi();
        ApplyLayout();
    }


    // ---- 装窗 ------------------------------------------------------------------

    private void BuildUi()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_root);

        _panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        Attach(_panel);

        _prev = NavButton("‹");
        _next = NavButton("›");
        _monthLabel = new Label
        {
            HorizontalAlignment = Godot.HorizontalAlignment.Center,
            VerticalAlignment = Godot.VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // 字号那一头已经按宽收过（<see cref="MonthLineChars"/>），这一道是兜底：
            // 语言表换成更长的说法时，它不许画到别人的格子里去。
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        Attach(_prev);
        Attach(_monthLabel);
        Attach(_next);
        _prev.Pressed += () => ShiftMonth(-1);
        _next.Pressed += () => ShiftMonth(1);

        for (var column = 0; column < Columns; column++)
        {
            var label = new Label
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = Godot.HorizontalAlignment.Center,
                VerticalAlignment = Godot.VerticalAlignment.Center,
            };
            Attach(label);
            _weekLabels.Add(label);
        }

        _dateLine = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = Godot.HorizontalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _noteLine = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = Godot.HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            // 画在格子**之上**：同一层里后加的控件画在上面，而格子是 RebuildCells 每次新加的
            // （见 <see cref="RebuildCells"/>），所以这两颗是 BuildUi 里早挂的 —— 不抬 ZIndex
            // 它们就被格子盖住了。2026-10-06 那一轮拍帧量到的就是那件事（见 <see cref="_editor"/>）。
            ZIndex = AboveCells,
        };
        _editor = new LineEdit
        {
            PlaceholderText = Tr("写点什么…"),
            Visible = false,
            // 同一件事：输入框也必须画在格子之上。接线全对（字色、字号、底、边都量过了）
            // 而字在像素上读不出来，少的就是这一层画序。
            ZIndex = AboveCells,
        };
        _editor.TextSubmitted += text => CommitAndSetDown("回车提交");
        Attach(_dateLine);
        Attach(_noteLine);
        Attach(_editor);

        // 编辑区那一圈边界：只有抬起打字那一段露出来（<see cref="Paint"/> 那一段切可见性），
        // 摆着（没在打字）时它是零。它铺满 <see cref="_root"/> 而 _root 不吃命中，格子照旧点得动。
        _frame = new Panel
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = AboveCells - 5,
            Visible = false,
        };
        Attach(_frame);

        // 抬起打字那一段，焦点一离开就当作「点到别处去了」。这条和桥那条点判据是两路冗余：
        // 一路是引擎报焦点走了，一路是我们自己看见一次落在窗外的按下。哪一路先到都算。
        FocusExited += () => { if (Host.StateNow == CalendarHost.State.Editing) CommitAndSetDown("焦点走了"); };
    }

    private readonly List<Label> _weekLabels = [];

    /// 输入框、备注那一行、编辑区那一圈边画在格子之上的那一层。
    /// **格子那一层是 0**（<see cref="RebuildCells"/> 新加的 Button 没有另外抬过），
    /// 而同一层里**后加的画在上面** —— 所以 <c>BuildUi</c> 里挂的那几颗（备注、输入框）
    /// 默认都被格子压在下面，而这是「在树上」「摆在这一格里」两条判据都问不到的：
    /// 那两条量的是几何，不是前后。
    public const int AboveCells = 20;

    /// <summary>把一颗控件挂进 <see cref="_root"/>，并且<b>不许挂第二次</b>。
    ///
    /// 引擎对被拒的 <c>AddChild</c> 只在 stderr 印一行 <c>Can't add child … already has a
    /// parent</c>，然后什么都不做 —— 屏上一切照常，于是这一类 bug 能带着两发启动期错误活过
    /// 每一次「跑起来看看」。2026-09-27 那条就是这么来的：<see cref="NavButton"/> 自己已经
    /// 挂过一次，<see cref="BuildUi"/> 又各挂一遍，两发错误、没人看见。
    ///
    /// 所以这里把它变成一次抛异常：自检里跑得到（<see cref="BuildSelfTest"/>），真跑起来也是当场
    /// 而不是静默。判据用 <c>GetParent()</c> 而不是句柄那一类实测结果 —— 引擎里「还没有爹」就是 null。</summary>
    private void Attach(Node child)
    {
        if (child.GetParent() != null)
            throw new InvalidOperationException(
                $"'{child.Name}' 已经在 {(child.GetParent() as Node)?.Name} 手里，不该再往 _root 挂一次");
        _root.AddChild(child);
    }

    private Button NavButton(string text)
    {
        // 只造、不挂 —— 挂是调用那一段的事（Attach）。这一发在 2026-09-27 之前是「造完顺手挂」，
        // 而 BuildUi 那边又 AddChild 了一遍，两发引擎错误就是这么印出来的。
        return new Button
        {
            Text = text,
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
        };
    }

    /// <summary>造一格：一颗 Button 和三行字（日号、农历或节日、备注）。
    ///
    /// 三档都造这三行，谁用谁摆 —— 少造一行就要在 <see cref="RebuildCells"/> 里分叉，
    /// 而「换月份才重造格子」这一条是三条档共用的，不该被样式拆成两份。
    ///
    /// 日号是 Button 里的一颗 Label，不是 Button 自己的文字：theme override 下在 Button 上
    /// 屏幕上就永远是默认白（2026-09-26 那一发截图读出来的，见 <see cref="Paint"/>）。
    /// <c>Flat = false</c> 同一天量成：flat 那颗不画 normal 那一张 stylebox，而选中/今天的
    /// 底色正是从 normal 走的。</summary>
    private Cell DayCell(DateOnly day)
    {
        var box = new Button
        {
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
        };
        box.AddThemeStyleboxOverride("shadow", new StyleBoxEmpty());
        var number = CellLabel();
        var sub = CellLabel();
        var note = CellLabel();
        // 备注可能一行写不完，也可能一个字都没有（空 Label 不占地方，不必为它留行）。
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.ClipText = true;
        // 农历/节日那一列和日号同一格并排，裁一下比压到下一格好（见 <see cref="SubLineChars"/>）。
        sub.ClipText = true;
        box.AddChild(number);
        box.AddChild(sub);
        box.AddChild(note);
        var pressed = day;
        box.GuiInput += @event =>
        {
            // 桥喂进去的那一发到没到这颗钮，和桥自己报的「喂了几发」是两个数：
            // 前者是窗这一侧，后者是钩子那一侧。中间断在哪一段，只有这一对数能分开。
            //
            // 而且**要分事件身份数**。2026-09-26 那一跑读出「喂一发、松开两次」，而测量程序
            // （--push 套一层那一行）证明喂一发触屏只发一次 ButtonUp —— 剩下的那一发
            // 只能是 OS 自己投进来的鼠标消息。若真是这样，设计文档 `docs/design/calendar.md` 那句
            // 「shell 不跨进程边界投鼠标消息」就只对一半，桥得改成「系统没投的那几发才补」，
            // 不然一次点击两下。这一行分不开触屏和鼠标，就永远分不清是哪一路。
            _cellGui++;
            switch (@event)
            {
                case InputEventScreenTouch { Pressed: true }: _cellTaps++; break;
                case InputEventScreenTouch { Pressed: false }: _cellReleases++; break;
                case InputEventMouseButton { Pressed: true }: _cellMouseDowns++; break;
                case InputEventMouseButton { Pressed: false }: _cellMouseUps++; break;
            }
        };
        box.ButtonUp += () => OnCellUp(pressed);
        return new Cell(day, box, number, sub, note);
    }

    private static Label CellLabel() => new()
    {
        MouseFilter = Control.MouseFilterEnum.Ignore,
        HorizontalAlignment = Godot.HorizontalAlignment.Left,
        VerticalAlignment = Godot.VerticalAlignment.Top,
    };

    // ---- 布局 ------------------------------------------------------------------

    /// <summary>把整块面板按设置摆到工作区的那个角上，并把每个控件放到算出来的位置。
    ///
    /// 这一发里不许出现一个裸像素：几何一律从 <see cref="Layout"/> 上读，那里每个数都是
    /// 「工作区 → 面板 → 格子 → 字」这条链上的一环（见文件上面那一节常量）。面板多大、
    /// 字多大，都是 <see cref="CurrentLayout"/> 那一发按当月行数算出来的 —— 行数换月份会换，
    /// 所以每一发都得重新量，不在这里数格子有几行。
    ///
    /// 尺寸走 Win32 那一侧：这一扇是顶层窗，引擎的 <c>Position</c> 和 Win32 的
    /// <c>SetWindowPos</c> 说的是同一个屏幕坐标，但引擎那一侧不知道 z 序归谁管，
    /// 而补样式位、补位置、补 z 序要按同一个次序做（<see cref="Park"/>）。
    /// 所以改位置只走 <see cref="Place"/> 这一条路。</summary>
    private void ApplyLayout()
    {
        // 图标位置先要一份：鼠标桥闸门第三问答的就是它，而那一问在帧里答。
        // 这一发不阻塞：读在后台线程上（`DesktopIcons.RequestRefresh`）。
        DesktopIcons.RequestRefresh();

        var options = _settings.Calendar;
        var want = CurrentLayout();
        var rows = want.Rows;

        _root.Position = Vector2.Zero;
        _root.Size = new Vector2(want.Width, want.Height);
        // 编辑区那一圈边铺满整块：它画不画由 <see cref="Paint"/> 按层切，这里只管它多大。
        _frame.Position = Vector2.Zero;
        _frame.Size = _root.Size;
        _panel.Position = Vector2.Zero;
        _panel.Size = _root.Size;
        // 那一整块面板底是 widget 才有的东西。格子那两档不许有一块铺满的底板：
        // 它一 Stop 就把整块矩形的命中全吃了（测量程序那条「铺满一层的 ColorRect 把点击整块吃掉」
        // 是同一个形状），而且 outline 那一档要的是「什么都不填，字直接压在壁纸上」。
        _panel.Visible = !want.Grid;
        if (!want.Grid)
        {
            var box = new StyleBoxFlat
            {
                BgColor = new Color(0.13f, 0.13f, 0.14f, options.OpacityPercent / 100f),
            };
            box.SetCornerRadiusAll(want.Radius);
            _panel.AddThemeStyleboxOverride("panel", box);
        }

        // ---- 抬头：widget 是「‹ 月份 ›」铺满一行，格子是「‹ 月份 ›」靠左 + 右边一行「今天是…」
        var navWidth = want.NavWidth;
        var monthWidth = want.MonthWidth;
        _prev.Size = new Vector2(navWidth, want.HeaderH);
        _prev.Position = new Vector2(0, 0);
        _monthLabel.Position = new Vector2(navWidth, 0);
        _monthLabel.Size = new Vector2(monthWidth, want.HeaderH);
        _monthLabel.HorizontalAlignment = want.Grid
            ? Godot.HorizontalAlignment.Left : Godot.HorizontalAlignment.Center;
        _next.Size = new Vector2(navWidth, want.HeaderH);
        _next.Position = new Vector2(navWidth + monthWidth, 0);

        for (var column = 0; column < Columns; column++)
        {
            _weekLabels[column].Position = new Vector2(column * want.CellW, want.HeaderH);
            _weekLabels[column].Size = new Vector2(want.CellW, want.WeekdayH);
        }

        // 「今天是…」那一行：widget 在页脚居中，格子在抬头右端。同一个控件，两个位置。
        var footerTop = want.HeadBand + rows * want.CellH;
        var dateLineWidth = want.Grid ? want.Width / 2 - navWidth : want.Width;
        var dateLineX = want.Grid ? want.Width / 2 : 0;
        var dateLineY = want.Grid ? 0 : footerTop;
        _dateLine.Position = new Vector2(dateLineX, dateLineY);
        _dateLine.Size = new Vector2(dateLineWidth, want.Grid ? want.HeaderH : want.DateLineH);
        _dateLine.HorizontalAlignment = want.Grid
            ? Godot.HorizontalAlignment.Right : Godot.HorizontalAlignment.Center;
        _dateLine.VerticalAlignment = want.Grid
            ? Godot.VerticalAlignment.Center : Godot.VerticalAlignment.Top;

        // 页脚那一块（备注 + 输入框）只有 widget 用得上；格子那两档的备注住在格子里。
        _noteLine.Position = new Vector2(want.CellInset, footerTop + want.DateLineH);
        _noteLine.Size = new Vector2(want.Width - want.CellInset * 2, want.NoteBandH);
        _noteLine.Visible = !want.Grid && Host.StateNow != CalendarHost.State.Editing;
        _editor.Position = _noteLine.Position;
        _editor.Size = _noteLine.Size;

        Place(want.Screen);
        RebuildCells(want);
        Paint(want);
        if (Host.StateNow == CalendarHost.State.Editing) PlaceEditor(want);
    }


    /// <summary>抬起打字时把输入框摆到它该在的地方：widget 那一档在页脚那一行，
    /// 格子那一档在**被选中的那一格**里 —— 设计文档 `docs/design/calendar.md`「三档样式」那一节那句「每格把自己的备注写在格子里」，
    /// 编辑态就得落在同一格，不然写完看不到它写到哪去了。</summary>
    private void PlaceEditor(Layout want)
    {
        if (!want.Grid) return;
        var slot = _cells.FirstOrDefault(c => c.Day == _selected);
        if (slot == null) return;
        var inset = want.CellInset;
        // 日号 + 农历那一行多高，是从字号推出来的（一行字 1.4 倍字号），不是拿两个 Label
        // 的实际高度再加两颗像素 —— 那一发在换字号之后会说谎。
        var lineH = want.LineH;
        // 起点是**那一格**的位置，不是这扇窗的左上角：要求是「输入框摆在被选中的那一格里」，
        // 而这一行原来没有那个偏移 —— 读代码读出来的：那时它永远停在左上那一格
        // （31 那一格），选中第四行的 23 时框就跑到别人家里去了。补上偏移之后
        // <see cref="EditorPlacementMeasurement"/> 读到「摆在这一格里=True」，那一行是判据；
        // 修之前那一发没跑过，所以「原来摆错过」这一句是读代码读出来的，不是量出来的。
        // 格子和输入框都是 _root 的直接子节点，而 _root 的原点恒为 (0,0)，所以两个坐标可比。
        _editor.Position = slot.Box.Position + new Vector2(inset, inset + lineH + want.NoteGap);
        _editor.Size = new Vector2(want.CellW - inset * 2,
            Math.Max(want.MinEditHeight, want.CellH - inset * 2 - lineH - want.NoteGap));
    }

    /// <summary>把这扇窗摆到屏幕上那块矩形。</summary>
    ///
    /// <b>顶层窗的坐标就是屏幕坐标</b>，所以三档共用这一发。
    /// 2026-10-12 之前这一扇是壁纸层的子窗，那时子窗的坐标是爹的坐标，得减掉爹的原点
    ///（2026-09-26 那一发量的）；那一族退休之后这条路没有了。
    /// <c>SWP_NOZORDER</c> 让这一发只管位置大小，不动 z 序 —— 这一扇摆着的时候是藏着的，
    /// 它的 z 序不归日历管（那一族 2026-10-15 退场，判据在
    /// <see cref="DesktopLayer.ShouldHideEngineWindow"/>）。</summary>
    private void Place(Win32.Rect screen)
    {
        if (Host.EngineHandle == 0)
        {
            Position = new Vector2I(screen.Left, screen.Top);
            Size = new Vector2I(screen.Width, screen.Height);
        }
        else Win32.SetWindowPos(Host.EngineHandle, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height,
            Win32.SwpFrameChanged | Win32.SwpNoZorder | Win32.SwpNoActivate);
        // 记下「这一扇现在摆在哪儿」：`Tick` 每秒拿它和工作区比，变了就重排一遍
        // （换分辨率、换缩放、任务栏挪边 —— 工单 27 拆点 5）。
        _placedAt = screen;
    }

    /// <summary>把文字、字号、颜色、底色重新下一遍。换选中、翻月份、过午夜、改样式都走这一发，
    /// 而不重造控件 —— <c>QueueFree</c> 掉的按钮这一帧还在树里、还在收事件。
    ///
    /// <b>字号和颜色一律下在真正有字的那颗 Label 上</b>，不下在格子的 Button 上：Button 自己没有
    /// 文字（日号是它的子 Label），2026-09-26 那一发截图里所有日号都是同一个白色 —— 选中该是蓝的、
    /// 跨月该淡到 0.22，一条都没生效，因为 override 全打在没字的那颗上。
    ///
    /// 格子那两档的底色是同一种黑换三档不透明度（平日 / 周末那一列 / 跨月），选中走淡蓝、
    /// 今天描一颗蓝边。契约没有管这三档的颜色，所以它们是这一版自己定的：要成立的只有
    /// 「平日最实、周末次之、跨月最淡」那一条**相对**关系，绝对色值没有别的出处。</summary>
    private void Paint(Layout want)
    {
        // 画面变了 —— 下一帧那一发把像素推进原生子窗（工单 28 第 3、第 4 步）。
        _dirty = true;
        var options = _settings.Calendar;
        var text = new Color(1, 1, 1, 0.92f);
        var muted = new Color(1, 1, 1, 0.42f);
        var dimmed = new Color(1, 1, 1, 0.22f);
        var picked = new Color(0.35f, 0.6f, 1f, 1f);
        var festival = new Color(1f, 0.62f, 0.25f, 1f);
        var solid = options.OpacityPercent / 100f;
        // 编辑态：被编辑那一格之外一律压暗一档，两眼才落在该编的那一格上。
        // 压的是画在屏上的这一份颜色，<c>calendar.opacityPercent</c> 仍然只管嵌着时的底。
        var editing = Host.StateNow == CalendarHost.State.Editing;

        // 日历是**一整块**：格子、星期标签、底色全画，颜色自成一体，不按图标挖洞
        // （所有者 2026-10-14 定夺）。原先那一段让位（图标占的那几列里什么都不画）
        // 随那一族一起撤了。

        _monthLabel.Text = MonthTitle(_shownMonth);
        FontSize(_monthLabel, want.TitleFont, text);
        FontSize(_prev, want.TitleFont, muted);
        FontSize(_next, want.TitleFont, muted);
        // 同一行字，两个出处：widget 那一档说的是「被选中的那天」（页脚就在格子下面，跟着选中走），
        // 格子那一档说的是「今天」（抬头右端那一行，它不跟着选中动）。
        _dateLine.Text = want.Grid
            ? $"{Lang.T("今天是")} {DateLine(_today, options.ShowLunar)}"
            : DateLine(_selected, options.ShowLunar);
        FontSize(_dateLine, want.DateFont, Recede(text, editing, want.Grid ? _today == _selected : true));
        // 挤不下就不画这一行，而不是让它压到月份那半截上去（20% 宽那一发就是那一发）。
        _dateLine.Visible = want.DateFits;
        _noteLine.Text = _notes.Get(_selected);
        FontSize(_noteLine, want.FooterFont, Recede(muted, editing, want.Grid));
        // 输入框那一档的字就是它要替的那一行的字：格子里是备注那一行，widget 里是页脚那一行。
        _editor.AddThemeFontSizeOverride("font_size", want.Grid ? want.NoteFont : want.FooterFont);
        // 字色要自己下：引擎给 LineEdit 的是深色那一套，抬起之后底是深蓝（<see cref="Paint"/>
        // 那一段的实底），深色字压在上面等于没有 —— 2026-10-06 他双击一格报的就是
        // 「输入也几乎看不清」。选中和光标同样下，不然选中一段时字色又翻回深色。
        _editor.AddThemeColorOverride("font_color", text);
        _editor.AddThemeColorOverride("font_selected_color", new Color(1, 1, 1));
        _editor.AddThemeColorOverride("font_unedited_color", muted);
        _editor.AddThemeColorOverride("font_placeholder_color", muted);
        _editor.AddThemeColorOverride("caret_color", new Color(1, 1, 1));
        _editor.AddThemeColorOverride("selection_color", new Color(0.35f, 0.6f, 1f, 0.45f));
        // 嵌入态的输入框不要那颗带边框的盒子：它本来就是备注那一行换了个能打字的样子，
        // 备注就是压在面板上的一行字。默认主题那颗框还有 4 像素的内容边距，
        // 留着它就是把输入框顶出面板下沿的那 12 像素。
        var tight = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            DrawCenter = false,
            ShadowSize = 0,
        };
        tight.SetContentMarginAll(0);
        // 抬起那一段换一颗：实底加一圈亮边。同一颗全透明无边就是「看不出输入在哪」，
        // 也就是他那句「没有边界」的一半。边从内容边距里出，控件几何一个字没动，
        // 所以「输入框被顶出面板下沿」那一发不会回来。
        var boxed = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.08f, 0.13f, 0.97f),
            BorderColor = picked,
            DrawCenter = true,
            ShadowSize = 0,
        };
        boxed.SetBorderWidthAll(2);
        boxed.SetCornerRadiusAll(want.CellRadius > 2 ? want.CellRadius : 2);
        boxed.SetContentMarginAll(0);
        var shaped = Host.StateNow == CalendarHost.State.Editing ? boxed : tight;
        _editor.AddThemeStyleboxOverride("normal", shaped);
        _editor.AddThemeStyleboxOverride("focus", shaped);
        _editor.AddThemeStyleboxOverride("read_only", shaped);

        // 编辑区那一圈边界。抬起来的是一整块矩形（1792x966 那一片），而它是无边框的
        // （<see cref="Borderless"/>），屏幕上分不出「编辑窗到哪儿为止」—— 这是他那句
        // 「没有边界」的另一半。格子那两档底下那一块底板本来是收着的（outline 要的是
        // 「什么都不填」），所以边界是单独一颗控件，它只画边不画底。
        _frame.Visible = Host.StateNow == CalendarHost.State.Editing;
        if (_frame.Visible)
        {
            var edge = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                BorderColor = picked,
                DrawCenter = false,
                ShadowSize = 0,
            };
            edge.SetBorderWidthAll(2);
            edge.SetCornerRadiusAll(0);
            _frame.AddThemeStyleboxOverride("panel", edge);
        }

        for (var column = 0; column < Columns; column++)
        {
            _weekLabels[column].Text = WeekdayName(column, options.WeekStartsMonday);
            FontSize(_weekLabels[column], want.WeekdayFont, muted);
        }

        for (var index = 0; index < _cells.Count; index++)
        {
            var slot = _cells[index];
            var day = slot.Day;
            var inMonth = day.Month == _shownMonth.Month;
            var selected = day == _selected;
            var today = day == _today;
            var named = Lunar.Festival(day).Length > 0;

            slot.Number.Text = day.Day.ToString();
            FontSize(slot.Number, want.NumberFont, Recede(
                !inMonth ? dimmed : selected ? picked : today ? text : new Color(1, 1, 1, 0.75f),
                editing, selected));

            // 格子里那三行：日号和农历/节日同一行（「1 三十」这么排的），
            // 备注在它们下面。widget 那一档格子里只用日号 —— 契约把它的备注写在格子<b>下面</b>
            // 那一行（契约 <c>.scratch/keeper-contract/spec.md</c>，<c>## User Stories</c>），不在格子里。
            // 那一行多高是从字号推的（一行字 = 1.4 倍字号），不是「两个 Label 的高度再挑两颗像素」：
            // 前者跟着 textPercent 走，后者在换字号之后就说谎了。
            var lineH = want.LineH;
            if (want.Grid)
            {
                var numberWidth = want.NumberWidth;
                slot.Number.Position = new Vector2(want.CellInset, want.CellInset);
                slot.Number.Size = new Vector2(numberWidth, lineH);
                slot.Sub.Visible = options.ShowLunar && want.SubFits;
                slot.Sub.Text = Lunar.Cell(day);
                slot.Sub.Position = new Vector2(want.CellInset + numberWidth, want.CellInset);
                slot.Sub.Size = new Vector2(want.CellW - want.CellInset * 2 - numberWidth, lineH);
                FontSize(slot.Sub, want.SubFont, Recede(named ? festival : muted, editing, selected));
                slot.Note.Text = _notes.Get(day);
                slot.Note.Visible = slot.Note.Text.Length > 0 && Host.StateNow != CalendarHost.State.Editing;
                slot.Note.Position = new Vector2(want.CellInset, want.CellInset + lineH + want.NoteGap);
                slot.Note.Size = new Vector2(want.CellW - want.CellInset * 2,
                    Math.Max(0, want.CellH - want.CellInset * 2 - lineH - want.NoteGap));
                FontSize(slot.Note, want.NoteFont,
                    Recede(new Color(1, 1, 1, 0.68f), editing, selected));
            }
            else
            {
                slot.Sub.Visible = false;
                slot.Note.Visible = false;
                // 格子只有日号一个数，居中偏上一点，下面留白。
                // 那两行数都是格子高度的百分比（19% / 32%），不是十二颗二十颗像素。
                slot.Number.Position = new Vector2(0, want.NumberTop);
                slot.Number.Size = new Vector2(want.CellW, want.NumberHeight);
                slot.Number.HorizontalAlignment = Godot.HorizontalAlignment.Center;
            }

            // 编辑态下被编辑那一格换成实底加亮边：输入框要压在它上面，半透的底压着
            // 半透的壁图纸，屏幕上分不出「正在编哪一格」。只在格子那两档这么办 ——
            // widget 那一档输入框在页脚（见 <see cref="PlaceEditor"/>），格子上没有东西要压。
            var editingPicked = editing && selected && want.Grid;
            var box = new StyleBoxFlat { BgColor = CellBackground(want, editing, selected, today, inMonth,
                index % Columns, options.WeekStartsMonday, solid) };
            box.SetCornerRadiusAll(want.CellRadius);
            if (editingPicked)
            {
                box.SetBorderWidthAll(want.TodayBorder > 1 ? want.TodayBorder : 2);
                box.BorderColor = picked;
            }
            else if (today)
            {
                box.SetBorderWidthAll(want.TodayBorder);
                box.BorderColor = new Color(0.35f, 0.6f, 1f, editing ? 0.35f : 0.9f);
            }
            slot.Box.AddThemeStyleboxOverride("normal", box);
            slot.Box.AddThemeStyleboxOverride("hover", box);
            slot.Box.AddThemeStyleboxOverride("pressed", box);
        }
    }

    /// 哪几列是周末。周一开头那一型是第 5、6 列，周日开头那一型是第 0、6 列 ——
    /// 读的是列号，不是「这一列的星期几文字」，因为文字那一发已经由 <see cref="WeekdayName"/>
    /// 管了，两边各数一次就会在换 weekStartsMonday 那天走岔。周末那两列比平日深一档，靠的就是这一条。
    public static bool IsWeekendColumn(int column, bool mondayFirst) =>
        mondayFirst ? column is 5 or 6 : column is 0 or 6;

    /// <summary>一格那层底是什么颜色。<b>纯算术</b>，自检拿三档各拧一遍。
    ///
    /// <para><b>这一族不许有「按图标挖洞」那一档</b>（所有者 2026-10-14 定夺，方向刚翻）：
    /// 日历<b>一整块照画</b>，图标是<b>压在它上面</b>的，不靠「透过来」。
    /// 原来的让位那一族会在图标占住的那几列里画成全透明，而它现在是个洞 ——
    /// <see cref="SelfTest"/> 里「填底的档不许有洞」那一条就是钉它的判据。</para>
    ///
    /// <para><b>答全透明只在一个情况下是对的</b>：<c>want.Fills</c> 关着的那一档（outline）——
    /// 那一档按设计一格都不填，字直接压在壁纸上。除此之外任何一档、任何一天、任何一列、
    /// 图标在不在，都不许答出 alpha 0。</para>
    ///
    /// <para><b>为什么值得单独拧出来</b>：这一发只答颜色，而「那一块有没有洞」在屏上才看得见。
    /// 判死它靠的是「把洞装回去，自检当场报红」—— 那一条现在是最该钉死的
    /// （原先那个反向验第三发是绿的：装回去门照样全绿）。</para>
    internal static Color CellBackground(Layout want, bool editing, bool selected, bool today,
        bool inMonth, int column, bool mondayFirst, float solid)
    {
        var editingPicked = editing && selected && want.Grid;
        return editingPicked ? new Color(0.12f, 0.26f, 0.46f, 0.97f)
            : selected ? new Color(0.35f, 0.55f, 0.85f, 0.42f * solid)
            : want.Fills && inMonth && IsWeekendColumn(column, mondayFirst)
                ? new Color(0f, 0f, 0f, 0.50f * solid)
            : want.Fills && inMonth ? new Color(0f, 0f, 0f, 0.42f * solid)
            : want.Fills ? new Color(0f, 0f, 0f, 0.15f * solid)
            : new Color(0, 0, 0, 0);
    }

    private static void FontSize(Control control, int size, Color color)
    {
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", color);
    }

    /// 编辑态下把不是「正在编辑」的那些字压暗一档。
    ///
    /// 压的是画在屏上的这一份颜色，不改 <c>calendar.*</c> 里管颜色的那一键 ——
    /// 那一键管的是摆着时的底，而压暗是为了抬起那一段的两眼能落在该编的那一格上。
    private static Color Recede(Color color, bool editing, bool selected) =>
        editing && !selected ? new Color(color.R, color.G, color.B, color.A * 0.42f) : color;

    // ---- 这一月 ------------------------------------------------------------------

    /// <summary>月历那 7×N 格都是哪几天。前导和补白都算进来，所以返回值永远是 7 的倍数。
    ///
    /// 行数随月份走（2026 年 9 月 5 行、8 月 6 行），不是永远 6 行 —— 永远 6 行会让面板每个月
    /// 换一次高度以外的东西：空一行灰格子。</summary>
    public static List<DateOnly> MonthCells(int year, int month, bool mondayFirst)
    {
        var first = new DateOnly(year, month, 1);
        var weekday = (int)first.DayOfWeek;      // 0 = 周日
        var offset = mondayFirst ? (weekday + 6) % 7 : weekday;
        var days = DateTime.DaysInMonth(year, month);
        var rows = (offset + days + 6) / 7;
        var cells = new List<DateOnly>(rows * 7);
        for (var i = 0; i < rows * 7; i++) cells.Add(first.AddDays(i - offset));
        return cells;
    }

    /// 周一开头那一行的第七格是周日，周日开头那一行的第一格是周日 —— 两种都从星期几数起，
    /// 所以这里读的是「这一列在 <c>mondayFirst</c> 那一型下是星期几」，不是控件的顺序。
    ///
    /// 取文字走 <see cref="Lang.T"/> 而不是 <c>Tr()</c>：这一发要能在手里没有一扇窗的时候算，
    /// <c>--check</c> 就是那种调用方，而 <c>Tr()</c> 是 <see cref="Node"/> 的实例方法。
    public static string WeekdayName(int column, bool mondayFirst) =>
        Lang.T(WeekdayKey(column, mondayFirst));

    /// <summary>那一列那个星期几在语言表里的<b>那条键</b>。
    ///
    /// <b>键和文字都在这一处</b>（<see cref="WeekdayName"/> 是它的翻译）—— 别的族要
    /// 「星期几」这个词的时候问它：勿扰那一族的时段行上有七个勾选框（<c>Options.DndDays</c>），
    /// 而语言表里<b>本来就有</b>周一…周日那七条，它另起一套 <c>dnd.weekday.mon</c> 就是
    /// 同一个概念的第二份名字（<c>CODING_STANDARDS.md</c>「取证方法论」里「判据里不许存一份
    /// 会腐烂的副本」那一条）。</summary>
    public static string WeekdayKey(int column, bool mondayFirst)
    {
        var weekday = (column + (mondayFirst ? 1 : 0)) % 7;      // 0 = 周日
        return weekday == 0 ? "周日" : weekday == 1 ? "周一" : weekday == 2 ? "周二"
            : weekday == 3 ? "周三" : weekday == 4 ? "周四" : weekday == 5 ? "周五" : "周六";
    }

    /// 抬头那一行。格式串本身是语言表里的一条，所以英文侧换成 <c>MMMM yyyy</c> 就得到
    /// 「September 2026」，不必在代码里分叉。
    public static string MonthTitle(DateOnly month) =>
        new DateTime(month.Year, month.Month, 1)
            .ToString(Lang.T("yyyy年M月"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>「9月24日周四 农历八月十四」。农历那一截只在 <c>showLunar</c> 开着时挂上。
    ///
    /// widget 那一档这一行在页脚、跟着选中走；格子那两档它在抬头右端、说的是今天，
    /// 而农历同时还有第二处出处（每格那一行 <see cref="Lunar.Cell"/>）。
    ///
    /// 格式化不拿 <see cref="System.Globalization.CultureInfo.InvariantCulture"/> 直接走：
    /// 那样 <c>ddd</c> 得到的是英文日名，中文那一行会写成「9月26日<b>Sat</b>」——
    /// 2026-09-26 那一发截图里读出来的，而 <c>--check</c> 当时只盯农历和节日，没盯星期。
    /// 日名一律从语言表取，格式串仍是布局的唯一出处（英文侧 <c>ddd d MMMM</c> 跟着走）。</summary>
    public static string DateLine(DateOnly day, bool showLunar = true)
    {
        var text = day.ToString(Lang.T("M月d日ddd"), DayNamesFromTable());
        var festival = Lunar.Festival(day);
        if (festival.Length > 0) text += " " + festival;
        if (showLunar) text += $" {Lang.T("农历")}{Lunar.Long(day)}";
        return text;
    }

    /// 一份「日名来自语言表」的格式提供者。星期那一行（<see cref="WeekdayName"/>）用的就是
    /// 这六条键，所以这里复用，不再往语言表里加第二套同义键。
    private static System.IFormatProvider DayNamesFromTable()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        // .NET 的日名数组从周日数起，和 <see cref="WeekdayName"/> 那一发的 0 = 周日同一个序。
        var names = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }
            .Select(Lang.T).ToArray();
        culture.DateTimeFormat.DayNames = names;
        culture.DateTimeFormat.AbbreviatedDayNames = names;
        culture.DateTimeFormat.ShortestDayNames = names;
        return culture;
    }

    /// <summary>手里这批格子是**哪一月、哪一种形状**。形状没变就不重造。</summary>
    private string _cellsShape = "";

    private void RebuildCells(Layout want)
    {
        // 同一个月、同一种格子形状就只摆不造。造的那一步是 <c>QueueFree</c> 全部 + 重挂全部，
        // 而抬起打字那一段走的正是 <see cref="ApplyLayout"/>：所以过去每双击一次，
        // 那一帧 35 颗格子和它们的字全被撤掉再挂回来 —— 屏幕上那就是「闪一下」
        // （2026-10-06 他双击一格报的那一条）。换月份、换档位、换格子大小仍然要重造，
        // 那几种和「抬起」没关系。
        var shape = $"{_shownMonth:yyyy-MM}|{want.Rows}|{want.CellW}|{want.CellH}|{want.HeadBand}|{want.Grid}";
        if (shape == _cellsShape && _cells.Count == want.Rows * Columns) return;
        _cellsShape = shape;
        foreach (var slot in _cells) slot.Box.QueueFree();
        _cells.Clear();
        var cells = MonthCells(_shownMonth.Year, _shownMonth.Month, _settings.Calendar.WeekStartsMonday);
        for (var i = 0; i < cells.Count; i++)
        {
            var slot = DayCell(cells[i]);
            var row = i / Columns;
            var column = i % Columns;
            slot.Box.Position = new Vector2(column * want.CellW, want.HeadBand + row * want.CellH);
            slot.Box.Size = new Vector2(want.CellW, want.CellH);
            _root.AddChild(slot.Box);
            _cells.Add(slot);
        }
    }

    private void ShiftMonth(int by)
    {
        _shownMonth = _shownMonth.AddMonths(by);
        _shownMonth = new DateOnly(_shownMonth.Year, _shownMonth.Month, 1);
        _log($"日历：翻到 {_shownMonth:yyyy-MM}");
        ApplyLayout();
    }

    // ---- 点一下选一天，点两下改备注 ------------------------------------------------

    /// 一颗格子被松开。单击选它，两次落在同一格且间隔不到 <see cref="DoubleClickMs"/> 算双击。
    ///
    /// 桥开着（默认，见 <see cref="_bridgeOn"/>）这一段，双击按「桥喂进几次点击」数，不按 <c>ButtonUp</c> 数。
    /// 2026-09-26 那一跑：桥开着喂一发，格子上同时收到触屏 1/1 和鼠标 1/1 —— 一次物理点击
    /// 松开两次，按松开次数判就是「一点就抬」。同一 <c>FedClicks</c> 里重复发出来的那份丢掉。
    ///
    /// <b>抬起打字那一段桥整条闭着</b>（那时这一扇摘掉了 <c>WS_EX_TRANSPARENT</c>，OS 自己会投），
    /// 双击反过来只按时间判。
    ///
    /// 换选中只许重画、不许重造格子（<c>QueueFree</c> 的按钮这一帧还在树里、还在收事件）。
    private void OnCellUp(DateOnly day)
    {
        var now = (long)Time.GetTicksMsec();
        var click = _bridge.FedClicks;
        var feeding = _bridgeOn && Host.StateNow == CalendarHost.State.Parked;
        if (feeding && IsRepeatOfSameClick(click, _lastClickId)) return;   // 同一次物理点击的第二份松开
        var doubleTap = feeding
            ? click == _lastClickId + 1 && IsDoubleClick(_lastPressed, day, now, _lastPressedMs)
            : IsDoubleClick(_lastPressed, day, now, _lastPressedMs);
        _lastClickId = click;
        _lastPressed = day;
        _lastPressedMs = now;
        _selected = day;
        if (day.Month != _shownMonth.Month || day.Year != _shownMonth.Year)
        {
            _shownMonth = new DateOnly(day.Year, day.Month, 1);
            ApplyLayout();
        }
        else Paint(CurrentLayout());
        // 已经在编辑态：点哪一格就把编辑框挪到哪一格，不等第二下。
        // 过去这一段只有双击才往下走，而双击那一头在编辑态里又被 <see cref="BeginEdit"/>
        // 开头那道「没在摆着就什么也不做」挡住了 —— 两条合起来就是「双击了另一个日期切不过去」
        // （2026-10-06 他报的那一条）。单击也走同一条，是因为抬起那一段用户盯着的是
        // 「我要编的是那一天」，等第二下才动会以为没点着。
        if (Host.StateNow != CalendarHost.State.Editing && !doubleTap) return;
        _lastPressed = null;
        BeginEdit();
    }

    /// <summary>一次物理点击算不算双击。嵌着那一段还要外加一条「桥喂进来的必须是相邻两发」，
    /// 见 <see cref="OnCellUp"/> 里那段为什么不能按 <c>ButtonUp</c> 数。
    ///
    /// 判据拆成这一发纯函数，是因为 <c>--check</c> 能把「同一发里的两份松开」「隔得远的两发」
    /// 「换了格子」三种情况各拧一遍 —— 真的点击要占桌面，那一路留给 <c>--tap</c>。</summary>
    public static bool IsDoubleClick(DateOnly? last, DateOnly now, long nowMs, long lastMs) =>
        last.HasValue && last.Value == now && nowMs - lastMs <= DoubleClickMs;

    /// 同一次物理点击（id 没变）里重复发出来的松开，丢不丢。
    public static bool IsRepeatOfSameClick(long clickId, long lastClickId) => clickId == lastClickId;
    // ---- 摆着 / 抬出来打字 / 放回去 ------------------------------------------------
    //
    // 2026-10-12 之前这一节的标题是「挂进壁纸 / 抬出来打字 / 放回去」，第一段是
    // `SetParent` 进 `SHELLDLL_DefView`（`Embed`）。那一族撤了：日历现在是顶层窗，
    // 压在桌面那一扇的正上方（`Park`），不再有爹。

    public override async void _Ready()
    {
        // 等一帧：窗要先进树、真开出来，才有一个 HWND 可以摆。
        // 这一发和测量程序里那句「Parent 节点是 busy 的时候挂不上」是同一族时序。
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _onHook = (e, ms) =>
        {
            // 闸门第三问（那一点上有没有图标）在钩子那一刻就答好了，结果跟着这一发排队。
            // **钩子回调里一行日志都不写**（那条钩子挂在全机输入的同步路径上，而写日志要落盘）。
            _bridge.OnMouseHook(e, ms);
        };
        _activity.MouseObserver += _onHook;
        Park("摆上");
    }

    public override void _ExitTree()
    {
        // 钩子那条出口是挂在 Activity 上的，而 Activity 活得比这扇窗久：
        // 不摘的话日历没了之后每一发点击还会往一个 QueueFree 掉的窗里排。
        if (_onHook != null) _activity.MouseObserver -= _onHook;
        _onHook = null;
    }

    /// <summary>把这扇窗摆到桌面那一叠的最底下 —— <b>卡片 03 · S27 之后这一发只是包装</b>：
    /// 读句柄、置档、挂原生子窗、藏引擎窗、记宿主那两行，全在 <see cref="CalendarHost.Park"/> 里。
    ///
    /// <para><b>这一发留着的是 <c>Calendar</c> 那两样</b>（分工在
    /// <c>docs/design/calendar.md</c>「宿主生命周期」那一节）：桥的清空和那句闸门日志 ——
    /// 它们要 <c>_bridge</c>，归绘制这一侧；<b>摆上那一推</b>（<see cref="PushPixels"/>）要引擎的视口，也留在这里。</para>
    ///
    /// <para><b>桥的清空放在 <c>Host.Park</c> 答真之后</b>（照抄现状：读不到 HWND 时窗还是
    /// 普通顶层窗，排着的旧事件归它，不清）。</para>
    public void Park(string when)
    {
        if (!Host.Park(when, CurrentLayout().Screen)) return;
        _bridge.Clear();
        _log($"日历：{when} 闸门{(_bridgeOn ? "开（按坐标判）" : "关（测量程序）")}，到这一刻喂了 {_bridge.Fed} 发");
        PushPixels("摆上", force: true);
    }

    /// <summary>把日历那一块从视口读回来，推进原生层叠子窗（工单 28 第 3、第 4 步）。
    ///
    /// <para><b>只在画面真变了的时候读回</b> —— 读回是同步的，每帧做一次就是每帧卡一次。
    /// <b>判据就写在下面那一发里</b>（<c>!force && !_dirty && !sizeChanged</c>）：画面变了
    /// 或者尺寸变了才读。<see cref="NativeLayer.ShouldReadBack"/> 写的是同一句话，
    /// 而<b>全仓没有生产调用方</b> —— 只有自检在拧它。<b>别把它当接线的那一层</b>：
    /// 自检绿不等于产品照着它走。</para>
    ///
    /// <para><b>「日历看得见」现在不归引擎管</b>：原生子窗不出像素就是黑的，而这一发
    /// 是那个失败模式唯一的出口 —— 所以读不出像素时那一行实测结果会明说，
    /// 不许让「引擎画了」冒充「屏幕上看得见」。</para>
    ///
    /// <para><b>编辑态也推</b>：抬起那一发之后人看见的还是那一扇原生子窗
    /// （只是它在顶层了），而 <c>UpdateLayeredWindow</c> 对顶层窗一样成立 ——
    /// 只在摆着时推的话，编辑框里那半句字屏幕上就没有。</para>
    ///
    /// <para><paramref name="announce"/> 答假时成功那一行不记日志：补推每 5 秒一发，
    /// 全记下来一天就是一万七千多行，把应用日志淹掉。答假那一行无论什么时候都记 ——
    /// 那才是要查的事。</para></summary>
    private void PushPixels(string because, bool force = false, bool announce = true)
    {
        if (Host.StateNow == CalendarHost.State.Hidden) return;

        var at = _placedAt;
        bool sizeChanged = _pushedAt.Width != at.Width || _pushedAt.Height != at.Height;
        // **画面变没变是引擎那一侧说的**（_dirty，由 Paint / ApplyLayout 置上），
        // 不是「视口读得到像素」—— 后者恒真，照它判就是每帧读回一次。
        if (!force && !_dirty && !sizeChanged) return;
        _dirty = false;

        var image = GetTexture()?.GetImage();
        if (image is null || image.GetWidth() <= 0 || image.GetHeight() <= 0)
        {
            _pushedBytes = 0;
            _log($"日历：{because} 视口读不到像素（{image?.GetWidth()}x{image?.GetHeight()}），" +
                 "这一块没有推进去 —— **屏幕上那一块是黑的**");
            return;
        }

        // **顶部到底一行、转 BGRA** —— `UpdateLayeredWindow` 要的是 BGRA
        // （`ULW_ALPHA` + `AC_SRC_ALPHA`）。引擎的 `Image.Convert` 是就地改（返回 void），
        // 而它那份枚举里**没有 BGRA**（量过：只有 RGBA8/RGB8 那几个），所以就地换 R 和 B：
        // BGRA 与 RGBA 只差头两个字节的次序，逐像素换 4 次就够，不必再拷一份整张图。
        var rgba = Image.CreateFromData(image.GetWidth(), image.GetHeight(), false,
            Image.Format.Rgba8, image.GetData())
            ?? throw new InvalidOperationException("视口那一份拷不出来（CreateFromData 答 null），这一块是黑的");
        var pixels = rgba.GetData();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        var bytes = pixels;
        _pushedAt = at;
        _pushedBytes = bytes.Length;

        var pushed = Host.Push(bytes, image.GetWidth(), image.GetHeight(), at);
        bool visible = NativeLayer.CalendarIsVisible(pushed.Attached, pushed.Ok, _pushedBytes);
        if (announce || !visible)
            _log($"日历：{because} {pushed.Note}；看得见={visible}（{_pushedBytes} 字节）");
    }

    /// <summary>退出那一发：把<b>借来的那一扇</b>还回去。
    ///
    /// <para><b>不还就是把壳那一扇永久改了</b> —— 借是借，不还就是拿。退出三条路
    /// （托盘的「退出」、<c>--seconds</c>、Esc）都走它，所以口关在 <c>App.Quit</c> 那一处就够了。
    /// 实测在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。</para></summary>
    public string Release() => Host.Release();

    /// <summary>双击之后：摘掉那几位样式、端到最前、给焦点。</summary>
    ///
    /// <b>这一扇平时点不中</b>（<c>WS_EX_TRANSPARENT</c>，鼠标要穿过去落到图标层），
    /// 所以键盘焦点平时不在它这儿，输入法也就进不来。双击那一格之后要把这些补回来，
    /// 写完提交或点到别处再放回最底下。「编辑」是同一扇窗的第二个状态，不是第二扇窗。
    ///
    /// <b>抬起和摆位分成了两段</b>（<see cref="MoveEdit"/>）：抬起只在这一发里做一次，
    /// 编辑态里点/双击另一格走的是摆位那一段 —— 不必把窗先放回最底下再抬一次，
    /// 那两步连着跑既闪（两次样式位翻来覆去）又白搭一次焦点。</summary>
    public void BeginEdit()
    {
        if (Host.StateNow == CalendarHost.State.Hidden)
        {
            _log($"日历：现在这扇窗是藏着的（{Host.StateNow}），抬起这一发没做");
            return;
        }
        if (Host.StateNow == CalendarHost.State.Parked)
        {
            // 第一次抬起来：正在编辑的就是刚双击的那一天。这一个值和「选中」分着记 ——
            // 编辑态里点别处会让「选中」往前走，而编辑框里那半句字始终属于它自己那一天
            // （见 <see cref="MoveEdit"/>）。
            _editingDay = _selected;
            _bridge.Clear();
            // **抬的是原生子窗那一扇**（契约「无感」第 2 条：双击那一格要把它抬到顶层
            // 才收得到键盘焦点），那一问住在 <see cref="CalendarHost.Lift"/> 后面的 adapter 里。
            Host.Lift(CurrentLayout().Screen);
        }
        MoveEdit();
    }

    /// <summary>把编辑框摆到<b>正在编辑的那一天</b>那一格里，并灌进那一格已有的备注。
    ///
    /// 这一发同时是「编辑态里换一天」那一条路。已经在同一格上时它一个字节都不动：
    /// 把用户打了半句、还没提交的字冲掉，比不响应还糟。
    ///
    /// 换一天时先把原来那一格那半句字交出去 —— 契约那句「写到别处就提交」在这里一样成立，
    /// 点的是日历上另一格。提交走 <see cref="CalendarNotes.Set"/>，改了才通知保存。</summary>
    private void MoveEdit()
    {
        if (Host.StateNow != CalendarHost.State.Editing) return;
        if (_selected == _editingDay && _editor.Visible) return;
        if (_selected != _editingDay)
        {
            var handed = _notes.Set(_editingDay, _editor.Text);
            if (handed) _saved();
            _editingDay = _selected;
            _log($"日历：编辑换到 {_editingDay:MM-dd}" +
                 (handed ? "，原来那一格那半句字先交出去了" : ""));
        }
        _editor.Text = _notes.Get(_editingDay);
        _editor.Visible = true;
        _noteLine.Visible = false;
        ApplyLayout();
        _editor.GrabFocus();
        _editor.CallDeferred(LineEdit.MethodName.Edit);
        _focusRead = false;
        // 抬起之后紧跟着的那一发松开不算双击：桥喂的那份和 OS 投的那份不是一路到达，
        // 一次物理点击能发出两份。清掉上一次的记录，免得刚抬起来就被自己按回去。
        // 这一条是防的，2026-09-26 那几跑还没读出来它真的会发生 —— 别当成量成的结论。
        _lastPressed = null;
        _lastClickId = _bridge.FedClicks;
    }

    /// 正在编辑的那一天。编辑框里那半句字属于它，而编辑态里「选中」会跟着点击往前走，
    /// 所以这两个不是同一个值。
    private DateOnly _editingDay;

    /// 提交并放回原来那块像素。<paramref name="because"/> 只是日志里那句「哪一路先到的」。
    public void CommitAndSetDown(string because)
    {
        if (Host.StateNow != CalendarHost.State.Editing) return;
        // 提交的是「正在编辑的那一天」，不是此刻的「选中」：编辑态里点过另一格之后，
        // 这两个已经不是一个值了（<see cref="MoveEdit"/> 那一段把选中挪走了，
        // 而它已经把前一格那半句字交出去了，所以走到这里手上这一格才是它的）。
        var changed = _notes.Set(_editingDay, _editor.Text);
        _editor.Visible = false;
        _noteLine.Visible = true;
        // SetDown 的桥清空放在前面（卡片 03 照抄现状 —— Park 那一发放在答真之后，两路不一样）。
        _bridge.Clear();
        var when = $"放回最底下（{because}{(changed ? "，备注有改动" : "，没改")}）";
        if (Host.SetDown(when, CurrentLayout().Screen))
        {
            // 闸门日志和「摆上」那一推留在 Calendar（搬走之前它们就跟着 Park 走）。
            _log($"日历：{when} 闸门{(_bridgeOn ? "开（按坐标判）" : "关（测量程序）")}，到这一刻喂了 {_bridge.Fed} 发");
            PushPixels("摆上", force: true);
        }
        if (changed) _saved();
        ApplyLayout();
    }

    /// <summary>把桥的闸门人为关掉，只给测量程序用（<c>--tap</c> 那一跑的第二发）。
    ///
    /// 这一发是为了分开两个实测结果：桥喂进去的那一发，和系统自己可能投进来的那一发。
    /// 2026-09-26 那一跑一次点击让格子松开两次，而测量程序（<c>--push</c> 套一层那一行）
    /// 证明喂一发触屏只发一次松开 —— 那第二发只能来自 OS。关掉桥再打一次：
    /// 格子上还有事件，就是 OS 投的；什么都没有，才是设计文档 `docs/design/calendar.md` 说的那个形状。
    /// 只许在嵌着的那一段关，抬起打字那一段本来就不靠桥。</summary>
    internal void BridgeOn(bool on)
    {
        _bridgeOn = on;
        _bridge.Clear();
        _log($"桥的闸门人为{(on ? "开回" : "关掉")}（测量程序，不是设置）");
    }

    /// 测量程序那一发：不走双击，直接把窗抬出来读焦点那一条。见
    /// <see cref="App.ScheduleCalendarLift"/>。
    internal void BeginEditFromProbe() => BeginEdit();

    /// <summary>测量程序那一发：往某一格推一次点击（触屏按下+松开各一件），<b>不碰桌面</b>。
    ///
    /// 为什么能这么量：这一发要问的是「双击判据成不成立、抬起那条路走不走得通」，
    /// 那是窗里侧的事；事件从哪儿来已经由 <c>--tap</c> 那几跑量过了（OS 会投、桥不用喂）。
    /// <c>PushInput</c> 不做命中测试，所以桌面上压着什么都量得到这一发 —— 省掉一次扫桌面。
    /// 两发之间隔多少由调用方数，判据要的是 500 毫秒以内。</summary>
    internal bool PushClickFromProbe(DateOnly day)
    {
        var slot = _cells.FirstOrDefault(c => c.Day == day);
        if (slot == null) return false;
        var cell = slot.Box;
        var local = cell.GetGlobalRect().GetCenter();
        var at = new Vector2I((int)local.X, (int)local.Y);
        // 这一行印在推事件**之前**。上一轮它印在之后，于是「第二发抬起」在日志里看起来像
        // 「第一发就抬起」—— 抬起那一行（BeginEdit 里印的）挤在了第二发的推送行前面。
        // 那条时序教训的第三个变量：一行实测结果的位置也是它的实测结果的一部分。
        _log($"日历：测量程序推第 {++_probePushes} 发进格子 {day:MM-dd} @({at.X},{at.Y})（事件在这一行之后推）");
        PushInput(new InputEventScreenTouch { Position = at, Pressed = true }, true);
        PushInput(new InputEventScreenTouch { Position = at, Pressed = false }, true);
        return true;
    }

    private int _probePushes;

    /// <summary>测量程序那一发：输入框到底摆进了<b>哪一格</b>。
    ///
    /// 「抬起打字那一段输入框摆在被选中的那一格里」是这一档的要求，而那句话在格子那一档
    /// 从来没读过 —— 抬起、摆位、提交、放回这四步各自在 widget 那一档或测量程序上成立过，
    /// 合起来在 1792x966 上没有。这一发就是补那一条：拿输入框的全局矩形和被选中那一格的
    /// 全局矩形比，四个边都在里面才算「摆进了这一格」。</summary>
    internal void EditorPlacementMeasurement()
    {
        var slot = _cells.FirstOrDefault(c => c.Day == _selected);
        if (slot == null)
        {
            _log($"日历：输入框摆位实测结果：认不出被选中的那一格（{_selected:MM-dd}，格子 {_cells.Count} 颗）");
            return;
        }
        var cell = slot.Box.GetGlobalRect();
        var edit = _editor.GetGlobalRect();
        var inside = edit.Position.X >= cell.Position.X && edit.Position.Y >= cell.Position.Y
            && edit.End.X <= cell.End.X && edit.End.Y <= cell.End.Y;
        _log($"日历：输入框摆位：选中 {_selected:MM-dd} 那一格全局={cell}，输入框全局={edit}，" +
             $"摆在这一格里={inside}" +
             (inside ? "" : $" → 差在左上 ({edit.Position.X - cell.Position.X},{edit.Position.Y - cell.Position.Y})"));
    }

    /// <summary>测量程序那一发：把字<b>灌</b>进输入框，不装键盘。
    ///
    /// 为什么不走发键：脚本发的键在这台机器上时好时坏，不能拿它当证据。
    /// 所以这一发证的是「提交那一条链接不接得上」，<b>不是</b>输入法进不进得来 ——
    /// 那一条仍然要他本人在真键盘上。</summary>
    internal void TypeFromProbe(string text)
    {
        _editor.Text = text;
        _log($"日历：测量程序灌进输入框「{text}」（灌的，不是键盘打的；输入法那一发仍欠着他本人一发）");
    }

    /// 测量程序那一发：推一发真的回车进这个视口，走 <c>TextSubmitted</c> 那一条接线，
    /// 不直接调 <see cref="CommitAndSetDown"/> —— 要证的正是「回车到不到得了提交」。
    internal void SubmitEnterFromProbe()
    {
        _log("日历：测量程序推回车（按下+松开各一件，事件在这一行之后推）");
        PushInput(new InputEventKey
        {
            Pressed = true, Keycode = Key.Enter, PhysicalKeycode = Key.Enter,
        }, true);
        PushInput(new InputEventKey
        {
            Pressed = false, Keycode = Key.Enter, PhysicalKeycode = Key.Enter,
        }, true);
    }

    /// <summary>把这一扇窗读的那份设置换成手上这一份。导入会换对象（见 <see cref="_settings"/>），
    /// 而「比例在配置里可调」这条规则要的是**改了就看到**，所以换完对象紧接着 <see cref="Relayout"/>。</summary>
    public void UseSettings(Settings fresh) => _settings = fresh;

    /// <summary>设置刚应用：按新的那几条比例重新量一遍、重新摆一遍。
    ///
    /// 抬起打字那一段也走这一发 —— <see cref="ApplyLayout"/> 末尾会把输入框摆回被选中的那一格，
    /// 所以格子换了大小，正在打的那一格跟着换，不会留在旧的位置上。</summary>
    public void Relayout(string because)
    {
        ApplyLayout();
        _log($"日历：{because}，按新的比例重新量过（{Host.StateNow}，矩形 {ScreenRect}，" +
             $"宽 {_settings.Calendar.WidthPercent}% 高 {_settings.Calendar.HeightPercent}% " +
             $"一带 {_settings.Calendar.HeaderPercent}% 字 {_settings.Calendar.TextPercent}%）");
    }

    /// 托盘那颗「桌面日历」按下去：藏起来，或者重新挂上去。
    ///
    /// 藏这一发不读 <see cref="Window.Visible"/>，也不靠 <c>Hide()</c>：从 C# 调
    /// <c>Window.Hide()</c> 是空操作（<c>CODING_STANDARDS.md</c> 的 <c>Window.Hide()</c> 那一条，App 藏引擎主窗
    /// 走的就是 <c>SW_HIDE</c>），所以「在不在」这一旗得自己记 —— 它现在住在
    /// <see cref="CalendarHost.Wanted"/>，真的藏放走 <see cref="CalendarHost.Hide"/> /
    /// <see cref="CalendarHost.Unhide"/>。
    /// <b>Hide 这一路的桥清空放在前面</b>（卡片 03 照抄现状），
    /// 放回来那一路的清空跟着 <see cref="Park"/> 走。
    public void Toggle()
    {
        if (Host.Wanted)
        {
            _bridge.Clear();
            Host.Hide();
            return;
        }
        Host.Unhide();
        ApplyLayout();
        Park("重新摆上（托盘）");
    }


    // ---- 每秒钟那一发 -------------------------------------------------------------

    public override void _Process(double delta)
    {
        _bridge.Drain(Host.EngineHandle, OnHookEvent);
        FocusMeasurement();
        _sinceTick += delta;
        // 宿主那两个计时（看门狗、补推）归 CalendarHost，每帧递秒数（卡片 03 · S27）。
        Host.Advance(delta);
        // **画面变了才推进去**。判据是 _dirty —— 它由 Paint / ApplyLayout 那些真的改了
        // 画面的那一发置上。**不是每帧都读回**：读回是同步的，每帧做一次就是每帧卡一次。
        PushPixels("画面变了");
        if (_sinceTick < 1.0) return;
        _sinceTick = 0;
        Tick();
    }

    /// <summary>抬起打字那一发的实测结果：<b>下一帧</b>再读，因为 <c>GrabFocus</c> 和
    /// <c>Edit</c> 都是排队的，当场读一定是假的（这一族时序在测量程序里栽过一次）。
    ///
    /// 这一发要答的是那两条欠着的：「抬起之后能不能拿到键盘焦点」和「中文输入法进不进得来」。
    /// 焦点这一半这里读得到；输入法那一半读不到字，但读得到 OS 这一刻给没给这扇窗一个
    /// IME 上下文、它在哪个档（<see cref="Win32.ImeReport"/>）—— 那一条把「窗没接上输入法」
    /// 和「这台机器的输入法本来就在英文档」分开。拼音到底上没上屏，仍然要拿眼睛看。</summary>
    private void FocusMeasurement()
    {
        if (Host.StateNow != CalendarHost.State.Editing || _focusRead) return;
        _focusRead = true;
        var foreground = Win32.GetForegroundWindow();
        // 抬起那一段的字色、字号、底和边也读一遍：**像素上「看不见」有两种来路** ——
        // 字是暗色压在深底上，还是字号小到根本不在那儿。2026-10-06 那一轮拍帧看到的就是
        // 「输入框里那一行字读不出来」，而只说「看不清」分不出是哪一种，所以这里四个数一次读齐。
        var ink = _editor.GetThemeColor("font_color");
        var box = _editor.GetThemeStylebox("focus") as StyleBoxFlat;
        _log($"日历：抬起之后 输入框有焦点={_editor.HasFocus()} 整窗有焦点={HasFocus()} " +
             $"前台=0x{foreground:X}/{Win32.ClassName(foreground)} 前台是我们={foreground == Host.EngineHandle}；" +
             $"输入框局部={_editor.Position}+{_editor.Size} 它的最小={_editor.GetCombinedMinimumSize()} " +
             $"窗={Size}；" +
             $"字色={ink} 字号={_editor.GetThemeFontSize("font_size")} " +
             $"框 画中心={box?.DrawCenter} 底={box?.BgColor} 边宽={box?.GetBorderWidth(Side.Left)} " +
             $"边色={box?.BorderColor}；" +
             $"{Win32.ImeReport(Host.EngineHandle)}（拼音上没上屏这一行读不出来，要拿眼睛看）");
    }

    /// 桥抽出来的每一发，包括没喂出去的那些。抬起打字那一段靠这一发认「点到别处去了」。
    /// <paramref name="down"/> 是「按下还是松开」—— 原来这里收的是裸的 <c>msg</c> 再自己比一次
    /// <c>WmLButtonDown</c>，而「是不是左键」那一条判定 2026-09-29 起住在
    /// <see cref="Activity.Judge"/> 里，能到这儿来的已经只有左键了。
    private void OnHookEvent(bool down, Win32.Point at, WindowTier.Owner owner)
    {
        if (Host.StateNow != CalendarHost.State.Editing || !down) return;
        if (owner == WindowTier.Owner.Ours) return;
        CommitAndSetDown("点在外面");
    }

    private void Tick()
    {
        // 图标位置：**读不在这一帧里做** —— 跨进程 COM 那一下几毫秒到几十毫秒，在帧里做就是卡。
        // `DesktopIcons.RequestRefresh` 把它排到后台线程上，帧里只拿上一次那份快照。
        DesktopIcons.RequestRefresh();

        // 这一块该长成什么样（工作区 + 设置 + 当月行数 + 系统缩放，纯算术）。
        // 工作区比的是它摆的那一块。
        var want = CurrentLayout();

        // 工作区变了 → 重排一遍（工单 27 拆点 5）。换分辨率、换缩放、任务栏挪到别的边都走这一条；
        // **全仓原来没有这一条** —— 而它补的是一个真洞：工作区一变（换分辨率、换缩放、
        // 任务栏挪边），这一扇还摆在老地方、里面那些格子停在老尺寸上。
        // **本机没量到它发生过**：原先那条「系统把窗等比改小了」是拿 DPI 不感知进程
        // 读到的虚拟坐标当物理坐标（窗没变过），更正见
        // `docs/research/desktop-layer-and-window-measurements.md` 那一节里「假读数」那一段。
        if (!want.Screen.SameAs(_placedAt))
        {
            _log($"日历：工作区从 {_placedAt} 变成 {want.Screen}，按新的重排一遍");
            ApplyLayout();
        }

        // **宿主那一族的每秒**（卡片 03 · S27 整体搬进 CalendarHost）：看门狗 → 到点补推 →
        // 引擎窗隐藏那一问，次序、判据和日志都在 `CalendarHost.HostTick` 里，
        // 理由和实测结果写在那一份的注释上。到点要推像素时它递回一条 PushRequest，
        // 推那一发要引擎的视口和桥，所以留在这里。
        Host.HostTick(_placedAt, req => PushPixels(req.Because, req.Force, req.Announce));

        var now = DateOnly.FromDateTime(DateTime.Now);
        if (now == _today && Host.StateNow != CalendarHost.State.Parked) return;

        // 这一扇没了就重新摆一次（别的程序关掉了它、或者 Explorer 重启把它带走了）。
        // 判据（只认「摆着档 + 引擎窗没了」）在 CalendarHost.RecoverIfEngineGone；
        // 重摆走这里原来的 Park —— 闸门日志和「摆上」那一推才留在原地。
        if (Host.RecoverIfEngineGone()) Park("重摆");

        if (now == _today) return;
        var crossedMonth = now.Month != _today.Month || now.Year != _today.Year;
        _today = now;
        if (crossedMonth) _shownMonth = new DateOnly(now.Year, now.Month, 1);
        if (crossedMonth) ApplyLayout();
        else Paint(CurrentLayout());
        _log($"日历：过午夜了，今天换成 {now:yyyy-MM-dd}");
    }

    /// <summary>这一扇窗现在该长成什么样：那一块屏的工作区 + 手上这份设置 + 当月行数 + 系统缩放。
    /// 布局那几发算术全在这一条链上，所以换分辨率、换 DPI、翻月份、拧滑块走的是同一条路。
    ///
    /// <b>这里不减图标那一列了</b>：2026-10-12 之前减过，而那一列的宽度是猜的。
    /// 现在这一扇是顶层半透明、压在最底下，图标从格子底下透过来（<see cref="DesktopLayer"/>）。</summary>
    private Layout CurrentLayout()
    {
        var work = WorkArea();
        return Measure(work, _settings.Calendar,
            MonthCells(_shownMonth.Year, _shownMonth.Month, _settings.Calendar.WeekStartsMonday).Count
            / Columns, _dpiPercent);
    }

    /// <summary>那一块屏的工作区（任务栏那条不让出来 —— 日历压到任务栏上面去是没道理的事）。
    private Win32.Rect WorkArea()
    {
        var rect = DisplayServer.ScreenGetUsableRect(_screen);
        return new Win32.Rect
        {
            Left = rect.Position.X, Top = rect.Position.Y,
            Right = rect.Position.X + rect.Size.X, Bottom = rect.Position.Y + rect.Size.Y,
        };
    }

    // ---- 布局那发算术 --------------------------------------------------------------

    /// <summary>面板该摆在哪、多大、格子里那几行字各用多大的字。纯算术，不碰引擎也不碰 Win32，
    /// 所以 <c>--check</c> 能把四个角、三档样式、每一条比例的两端各拧一遍。
    ///
    /// <b>这一发里只有一个数是以像素为单位的</b>：<paramref name="dpiPercent"/>，就是系统缩放，
    /// 它只用来放大那根「字再小就读不出来」的下限。除此之外每一条都是百分比乘百分比 ——
    /// 所以换一块屏，进来的是那块屏的工作区，出去的是同一张月历，只是各自大或小。
    /// 取整一律往下取（整数除法），不四舍五入：桌面上没人看得出差半像素，而自检里差半像素
    /// 就是一条对不上的实测结果。
    ///
    /// 三档共用这一个形状：<see cref="Grid"/> 说「铺桌面那一型」（wallpaper / outline），
    /// <see cref="Fills"/> 说「格子里填不填底」—— outline 和另两档只差这一旗：那一档一颗
    /// 底色都不填。契约给它的那几条细线还没画，那一发在 <c>.scratch/keeper-debt/issues/09-outline-rules-not-drawn.md</c>。</summary>
    internal readonly record struct Layout(
        int X, int Y, int Width, int Height, int Rows, int CellW, int CellH,
        int HeaderH, int WeekdayH, int FooterH)
    {
        public Win32.Rect Screen => new() { Left = X, Top = Y, Right = X + Width, Bottom = Y + Height };

        /// 抬头那一整带（月份 + 星期两行）有多高。<see cref="HeaderH"/> 说的是月份那一行。
        public int HeadBand => HeaderH + WeekdayH;

        /// <c>‹ ›</c> 那颗钮各多宽：格子那一档是一行月份那么高，widget 那一档是抬头一带的 55%。
        public int NavWidth { get; init; }
        /// 月份那一行多宽：格子那一档让开右端那行「今天是…」，widget 那一档铺满中间。
        public int MonthWidth { get; init; }
        /// 面板那一角（只有 widget 画底板）。
        public int Radius { get; init; }
        /// 格子里那颗 stylebox 的圆角 —— 格子那两档是 0，格与格之间不留缝。
        public int CellRadius { get; init; }
        /// 格子里那道内边距。
        public int CellInset { get; init; }
        /// 格子里两行之间那道缝，是内边距的 25%。
        public int NoteGap => Math.Max(1, CellInset / 4);
        /// 格子里日号那一行有多高：一行字 = 1.4 倍字号。
        public int LineH => NumberFont * FontLinePercent / 100;
        /// 日号那一列让多宽（格子里那一行的另一半留给农历/节日）。widget 那一档整格都是日号的。
        public int NumberWidth { get; init; }
        /// 「今天是 9月26日周六 农历八月十六」那一行的字。它按宽收过，所以和星期那行的字不是一个数。
        public int DateFont { get; init; } = 13;

        /// <summary>格子里次行那一列装得下它的字吗。装不下就不画 —— 20% 宽那一发格子里只有
        /// 36 像素，日号和农历两列怎么摆都挤，裁掉一列比让「二十」压在下一格的「2」上面好。
        /// 字那一头已经按宽收过，所以这一旗平时该一直为真，只有「缩到下限还是放不下」的那一发
        /// 才会翻 —— 自检里那条「挤不下必须是因为已经贴着下限」就是钉这一句。</summary>
        public bool SubFits => !Grid ||
            SubFont * SubLineChars <= CellW - CellInset * 2 - NumberWidth;

        /// 「今天是…」那一行装不装得下（widget 那一档整幅宽都是它的）。同上，装不下就不画。
        public bool DateFits => DateFont * TodayLineChars <= (Grid ? Width / 2 - NavWidth : Width);
        /// widget 那一档：格子里日号往下多少、那一行多高（都是一格高的百分比）。
        public int NumberTop { get; init; }
        public int NumberHeight { get; init; }
        /// 页脚里日期那一行多高，和剩下给备注 / 输入框的那一条。
        public int DateLineH { get; init; }
        public int NoteBandH => FooterH - DateLineH;
        /// 那颗输入框最小要多高：它的字号乘二（13 号那一发量出来 26）。
        public int MinEditHeight { get; init; } = 26;
        /// 今天那一颗描边多粗。
        public int TodayBorder { get; init; } = 1;

        public bool Grid { get; init; }
        public bool Fills { get; init; } = true;
        public int TitleFont { get; init; } = 18;
        public int WeekdayFont { get; init; } = 13;
        public int NumberFont { get; init; } = 15;
        public int SubFont { get; init; } = 11;
        public int NoteFont { get; init; } = 13;
        public int FooterFont { get; init; } = 13;

        /// 这一发用的像素下限，只进实测结果日志：它说「字缩到哪一档就不许再缩了」。
        public int FontFloor { get; init; } = MinFontAt96;
    }

    internal static Layout Measure(Win32.Rect work, CalendarOptions options, int rows, int dpiPercent = 100) =>
        IsGrid(options.Style) ? MeasureGrid(work, options, rows, dpiPercent)
            : MeasureWidget(work, options, rows, dpiPercent);

    /// 铺满桌面那两档 —— wallpaper 和 outline 是同一套几何，只有「格子里填不填底」这一处不同。
    public static bool IsGrid(string style) => style is "wallpaper" or "outline";

    /// <summary>这一块该摆在工作区的哪个角上。离那个角多远是两条百分比（不是像素），
    /// 最后夹回工作区：比例拧到两端时（100% 的格子 + 1% 的边距）也不许把日历推出屏外。</summary>
    internal static (int X, int Y) Corner(Win32.Rect work, CalendarOptions options, int width, int height)
    {
        var marginX = work.Width * options.MarginXPercent / 100;
        var marginY = work.Height * options.MarginYPercent / 100;
        var x = options.Corner is "topRight" or "bottomRight"
            ? work.Right - width - marginX : work.Left + marginX;
        var y = options.Corner is "bottomLeft" or "bottomRight"
            ? work.Bottom - height - marginY : work.Top + marginY;
        return (Math.Max(work.Left, Math.Min(x, work.Right - width)),
            Math.Max(work.Top, Math.Min(y, work.Bottom - height)));
    }

    /// <summary>格子那两档该摆在哪、多大。
    ///
    /// 两条边各取工作区的一条百分比（<c>widthPercent</c> / <c>heightPercent</c>），所以换一块
    /// 形状的屏不用重挑一个数：带鱼屏上想只收紧横向，拧那一条就够了。`corner` +
    /// <c>marginXPercent</c>/<c>marginYPercent</c> 把这一块推到那个角上、再按工作区的百分比往里挤。
    /// 格子本身是宽 7 等分、高按行数等分。
    ///
    /// <b>抬头那一整带占面板高度多少，是配置里的 <c>headerPercent</c></b>，默认 7：抬头一带占
    /// 面板高约百分之七。键只收整数百分比，所以在 720 那一档「百分之七」是 50.4 颗像素，往下取整
    /// 成 50 —— 差的那一颗是这根键的粒度，不是布局走偏。
    ///
    /// <b>字号一律从抬头一带和格子高度推</b>，不写死：两条边都 20% 那一发格子只有 53 像素高，
    /// 照 widget 那套 15 号字摆，字比格子还高。每一发夹两根线：下面那根是「读不出来就别写」的
    /// 下限（这一节里唯一按像素算的一条，而且它乘的是系统缩放不是分辨率），上面那根是
    /// 「字不许把它的行撑破」—— 拧 <c>textPercent</c> 是想要大字，不是想要月份那行压在星期那行上面。
    /// 一格里的地方去掉两道内边距和那道缝之后要摆两行字（日号一行、备注一行），
    /// 一格里的地方去掉两道内边距和那道缝之后要摆两行字（日号一行、备注一行），
    /// 所以那一格里的字各按「剩下那点地方的一半除以 1.4」收。
    ///
    /// <b>横向那条百分比量的是整块工作区</b>，不减左边那一条图标列。
    /// 2026-10-12 之前这里先减掉，而那一列的宽度是猜的
    ///（<c>Win32.DesktopIconSpacing</c> 读的是 <c>LVM_GETITEMSPACING</c>，这台机器答 0，
    /// 于是退到写死的 110）。那一族整体撤掉了：这一扇改成顶层半透明、压在桌面正上方之后，
    /// 图标从格子底下透过来、鼠标也穿得过去，日历不再靠「躲开哪一条」来成全它。</summary>
    internal static Layout MeasureGrid(Win32.Rect work, CalendarOptions options, int rows, int dpiPercent = 100)
    {
        var floor = Math.Max(1, MinFontAt96 * dpiPercent / 100);
        var width = work.Width * options.WidthPercent / 100;
        var height = work.Height * options.HeightPercent / 100;
        var band = height * options.HeaderPercent / 100;
        var monthH = band * MonthLineShare / 100;
        var weekH = band - monthH;
        var cellH = rows > 0 ? (height - band) / rows : height;
        var cellW = width / Columns;
        var inset = cellW * GridCellInsetPercent / 100;
        // 格子里那点地方：宽是格子去掉两道内边距，高是去掉内边距和那道缝之后要摆两行字。
        var cellBody = Math.Max(1, cellW - inset * 2);
        var cellLines = Math.Max(0, cellH - inset * 2 - Math.Max(1, inset / 4));
        var (x, y) = Corner(work, options, width, height);
        // 日号那一行多高，决定了格子里还剩多少给备注 —— 所以它得先按上限收过一遍再推 LineH。
        var numberFont = Fit(cellH, GridNumberFontShare, cellLines / 2 * 100 / FontLinePercent);
        var noteFont = Fit(cellH, GridNoteFontShare, cellLines / 2 * 100 / FontLinePercent);
        // 日号和农历/节日同一行，各让一半宽；次行那一列最长是三个字的节日名，所以它的字
        // 按「剩下的宽除以三」收。不收的那一发就是 45% 宽 + 140% 字那一帧：「二十」压在
        // 下一格的「2」上面 —— 字按高度收完了，宽那一头还没人管。
        var numberWidth = Math.Min(numberFont * 3, cellBody / 2);
        var subFont = Fit(cellH, GridSubFontShare,
            Math.Min(cellLines / 2 * 100 / FontLinePercent, (cellBody - numberWidth) / SubLineChars));
        // 抬头那两行按宽收是同一个道理：月份那一行是「2026年9月」，「今天是…」那一行最长。
        //
        // **抬头左半边给月份、右半边给「今天是…」** —— 两边对半分，各占 `width / 2`。
        // 改之前左半边只给两格宽（`cellW * 2`），而格子是七等分来的，面板一窄
        // （默认横向 33%，2026-10-12）左半边只剩 232 像素，7 个字放不下，
        // 于是标题顶死、`textPercent` 拧到头也不变大。
        //
        // **对半分仍是比例**，没有写死像素；而「今天是…」那一行本来就短（16 个字符里
        // 它自己按 `TodayLineChars` 收），对半分给得起。
        var halfWidth = Math.Max(1, width / 2);
        var dateLineWidth = Math.Max(1, width / 2 - monthH);
        var headerFont = Fit(band, GridWeekdayFontShare, weekH * 100 / FontLinePercent);
        var titleCeiling = Math.Min(monthH * 100 / FontLinePercent, halfWidth / MonthLineChars);
        return new Layout(x, y, width, height, rows, cellW, cellH, monthH, weekH, 0)
        {
            Grid = true,
            Fills = options.Style != "outline",
            TitleFont = Fit(band, GridTitleFontShare, titleCeiling),
            WeekdayFont = headerFont,
            NumberFont = numberFont,
            NumberWidth = numberWidth,
            SubFont = subFont,
            NoteFont = noteFont,
            FooterFont = headerFont,
            DateFont = Fit(band, GridWeekdayFontShare,
                Math.Min(weekH * 100 / FontLinePercent, dateLineWidth / TodayLineChars)),
            CellInset = inset,
            NavWidth = monthH,
            MonthWidth = halfWidth,
            CellRadius = 0,
            MinEditHeight = noteFont * EditorHeightOfFontPercent / 100,
            TodayBorder = Math.Max(1, dpiPercent * 2 / 100),
            FontFloor = floor,
        };

        // 一条比例推出来的字号：先从它该比例的那个尺寸上推、再乘配置里的 textPercent，
        // 最后夹在「读得出来的下限」和「那一行装得下」之间。上限赢不过下限 —— 那种一发是
        // 这块地方本来就摆不下字，让字挤出去比把字缩到没有更好读。
        int Text(int of, int share) =>
            Math.Max(floor, of * share / 100 * options.TextPercent / 100);
        int Fit(int of, int share, int ceiling) => Math.Min(Math.Max(ceiling, floor), Text(of, share));
    }

    /// <summary>widget 那一档：一条边占工作区多少（<c>widgetWidthPercent</c>），剩下的全从它推 ——
    /// 格子是宽七等分、高是宽的 97%，抬头那一整带就是一格高 —— 「格子和字全从那一数推」是
    /// 设计文档 `docs/design/calendar.md` 那一族（布局算术），「抬头=一格高」是这一版在它里面选的推法，
    /// 页脚是两行字（日期那一行 1.4 倍字号，备注那一行至少是那颗输入框的最小高度）。
    ///
    /// 它不看 <c>headerPercent</c>：那一条说的是「抬头占桌面的多少」，而小面板的抬头按契约就是
    /// 一格高 —— 拿 7% 去卡它，月份那一行只有 8 像素高。两档各有各的参照，和它们各有各的
    /// 字号比例是同一件事（见 <see cref="WidgetTitleFontShare"/> 那条注释）。</summary>
    /// <summary>widget 那一档：一条边占工作区多少（<c>widgetWidthPercent</c>），剩下的全从它推 ——
    /// 格子是宽七等分、高是宽的 97%，抬头那一整带就是一格高 —— 「格子和字全从那一数推」是
    /// 设计文档 `docs/design/calendar.md` 那一族（布局算术），「抬头=一格高」是这一版在它里面选的推法，
    /// 页脚是两行字（日期那一行 1.4 倍字号，备注那一行至少是那颗输入框的最小高度）。
    ///
    /// 它不看 <c>headerPercent</c>：那一条说的是「抬头占桌面的多少」，而小面板的抬头按契约就是
    /// 一格高 —— 拿 7% 去卡它，月份那一行只有 8 像素高。两档各有各的参照，和它们各有各的
    /// 字号比例是同一件事（见 <see cref="WidgetTitleFontShare"/> 那条注释）。
    ///
    /// <b>装不下就按能装下的那一档收</b>：小面板的高是从宽推出来的（六行的月份比五行的高一截），
    /// 所以比例拧到最大时它可能比这块屏还高。那一发上先把宽度按高度收一次 ——
    /// 配置里那条百分比说的是意图，「整块面板在屏幕上」是底线。格子那两档不需要这一发：
    /// 它两条边各占一条百分比，本来就出不了屏。</summary>
    internal static Layout MeasureWidget(Win32.Rect work, CalendarOptions options, int rows, int dpiPercent = 100)
    {
        var floor = Math.Max(1, MinFontAt96 * dpiPercent / 100);
        var lineCount = Math.Max(1, rows);
        int Text(int of, int share) =>
            Math.Max(floor, of * share / 100 * options.TextPercent / 100);
        int Fit(int of, int share, int ceiling) => Math.Min(Math.Max(ceiling, floor), Text(of, share));

        // 面板多宽 → 其余每一条尺寸。推两遍的代价换的是「一个出处」：这一发是它唯一的算法，
        // 收不收宽度都走它，不在外面抄第二套。
        (int Width, int CellW, int CellH, int Band, int MonthH, int FooterFont,
            int DateLineH, int FooterH, int Height) Derive(int width)
        {
            var cellW = Math.Max(1, width / Columns);
            var cellH = cellW * WidgetCellAspectPercent / 100;
            var band = cellH;                    // 抬头那一整带 = 一格高
            var monthH = band * MonthLineShare / 100;
            // 页脚那一行的字按**宽**收：那行说的是「9月26日周四 农历八月十四」，最长的一发
            // 十六个汉字当量，而它不许被裁 —— 它是这一档唯一的实测结果出处。页脚的高是从这个字
            // 推的，所以收字等于收页脚，面板跟着缩，不会撑破。
            var footerFont = Fit(cellH, WidgetFooterFontShare, width / TodayLineChars);
            // 页脚按字给，不按格子给：日期那一行 + 备注（打字时是输入框）那一行。
            // 此前把页脚从 32 抬到 40 是在追那颗输入框的最小高度 —— 按字号留就不必再追。
            var dateLine = footerFont * FontLinePercent / 100;
            var footer = dateLine + Math.Max(footerFont * EditorHeightOfFontPercent / 100, dateLine);
            return (width, cellW, cellH, band, monthH, footerFont, dateLine, footer,
                band + lineCount * cellH + footer);
        }

        var shape = Derive(work.Width * options.WidgetWidthPercent / 100);
        // 收宽度到装得下为止。收一次往往不够：格子是「宽度除以七」推出来的，宽度差两三像素
        // 时格子一格都没缩，于是高度一动没动。所以这一发是个循环，并且每次至少缩一整列 ——
        // 严格变小，八格屏上几十遍一定停得下来（这里全是纯算术，跑几遍不值钱）。
        for (var guard = 0; guard < 64 && shape.Height > work.Height; guard++)
        {
            var next = (int)(shape.Width * (double)work.Height / shape.Height);
            shape = Derive(Math.Max(1, next < shape.Width ? next : shape.Width - Columns));
        }
        var (width, cellW, cellH, band, monthH, footerFont, dateLineH, footerH, height) = shape;
        var (x, y) = Corner(work, options, width, height);
        var weekH = band - monthH;
        return new Layout(x, y, width, height, rows, cellW, cellH, monthH, weekH, footerH)
        {
            // 抬头那两行的字各按自己那一行的高度收（见 <see cref="MeasureGrid"/> 里那条同样的话）。
            // 页脚那一行的字不收：页脚本来就是从它推出来的，字大页脚就跟着长。
            TitleFont = Fit(band, WidgetTitleFontShare, monthH * 100 / FontLinePercent),
            WeekdayFont = Fit(band, WidgetWeekdayFontShare, weekH * 100 / FontLinePercent),
            NumberFont = Fit(cellH, WidgetNumberFontShare, cellW / NumberLineChars),
            SubFont = Fit(cellH, WidgetNumberFontShare, cellW / NumberLineChars),
            NumberWidth = cellW,            // widget 的格子里只有日号，整格都是它的
            DateFont = footerFont,          // 页脚那一行说的是被选中的那天，整幅宽都是它的
            NoteFont = footerFont,
            FooterFont = footerFont,
            CellInset = cellW * WidgetCellInsetPercent / 100,
            NavWidth = band * WidgetNavShare / 100,
            MonthWidth = width - band * WidgetNavShare / 100 * 2,
            Radius = band * WidgetRadiusShare / 100,
            CellRadius = cellH * WidgetCellRadiusShare / 100,
            NumberTop = cellH * WidgetNumberTopShare / 100,
            NumberHeight = cellH * WidgetNumberHeightShare / 100,
            DateLineH = dateLineH,
            MinEditHeight = footerFont * EditorHeightOfFontPercent / 100,
            TodayBorder = Math.Max(1, dpiPercent / 100),
            FontFloor = floor,
        };
    }

    // ---- 自检 ------------------------------------------------------------------

    /// <summary>装窗那一条路的常驻自检（<c>--check</c> 的一组）。
    ///
    /// 来历是 2026-09-27 那一条：每次启动桌面日历报两发
    /// <c>Can't add child '@Button@6' to '@Control@3', already has a parent</c>，栈在
    /// <see cref="BuildUi"/> —— <see cref="NavButton"/> 造完顺手挂过一次，外面又各挂一遍。
    /// 引擎对被拒的那一发只印一行就咽下去，日历照显示，所以那两发活了一整轮没人看见。
    ///
    /// 无头里造一扇真的日历（构造就走 <see cref="BuildUi"/> → <see cref="ApplyLayout"/>），问四头：
    /// ① 那几颗各在 <see cref="_root"/> 手里；② 导航那一行的<b>兄弟序</b>是 ‹ 月份 › —— 旧写法
    /// 底下 ‹ › 都在月份之前，序就是「被拒的那一发」留在树上的痕迹，这一发是唯一看得见它的判据；
    /// ③ 周标签那七颗一颗不少（少一颗就是有一发挂没成）；④ <see cref="Attach"/> 对重复挂
    /// 当场抛 —— 把「引擎印一行然后什么都不做」换成「自检里红一条」，这一类 bug 不许再静默活着。</summary>
    public static List<string> BuildSelfTest(Node parent)
    {
        var problems = new List<string>();
        var dir = System.IO.Path.Combine(Tmp.Root,
            $"keeper-calendar-build-{System.Environment.ProcessId}");
        Calendar? win = null;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion},\n  \"calendar\": {{ \"enabled\": true, \"style\": \"wallpaper\" }}\n}}\n");
            var state = SettingsStore.Load(dir);
            if (state.Error != null) return [$"底稿读不通：{state.Error}"];
            win = new Calendar(state.Current, 0,
                CalendarNotes.Load(System.IO.Path.Combine(dir, CalendarNotes.FileName)),
                new Activity(), () => { }, _ => { });
            parent.AddChild(win);
            if (win._root == null || !IsInstanceValid(win._root))
                return ["装窗那一条没把 _root 搭出来，这一组没法跑"];

            foreach (var (what, node) in new (string, Node?)[]
            {
                ("底板", win._panel),
                ("‹ 那一颗", win._prev), ("› 那一颗", win._next),
                ("月份那一行", win._monthLabel), ("日期那一行", win._dateLine),
                ("备注那一行", win._noteLine), ("打字那个框", win._editor),
            })
                if (!ReferenceEquals(node?.GetParent(), win._root))
                    problems.Add($"{what}不在 _root 手里（读回来 {(node == null ? "null" : node.GetParent()?.GetName() ?? "没爹")}）");

            var order = win._root.GetChildren().Cast<Node>().ToList();
            var iPrev = order.IndexOf(win._prev);
            var iLabel = order.IndexOf(win._monthLabel);
            var iNext = order.IndexOf(win._next);
            if (!(iPrev >= 0 && iPrev < iLabel && iLabel < iNext))
                problems.Add($"导航那一行的兄弟序是 ‹={iPrev} 月份={iLabel} ›={iNext}，该是 ‹ 在月份前、月份在 › 前 —— " +
                             "旧写法里 ‹ › 是被工厂函数顺手挂的，所以都排在月份之前，那就是被拒那一发的痕迹");
            if (win._weekLabels.Count != Columns ||
                win._weekLabels.Any(l => !ReferenceEquals(l.GetParent(), win._root)))
                problems.Add($"周标签该有 {Columns} 颗且各在 _root 手里，读出来 {win._weekLabels.Count} 颗");

            // 反向那一发：明知故犯地挂第二次，要它抛。不抛就是这条判据白装。
            var twice = false;
            try { win.Attach(win._prev); }
            catch (InvalidOperationException) { twice = true; }
            if (!twice) problems.Add("把同一颗挂第二次没抛 —— Attach 那道守卫不成立，这一类 bug 还能静默活着");

            problems.AddRange(EditingJudges(win));
        }
        catch (Exception e)
        {
            problems.Add($"这一组自己炸了：{e.Message}");
        }
        finally
        {
            if (win != null) { parent.RemoveChild(win); win.QueueFree(); }
            try { System.IO.Directory.Delete(dir, true); } catch { /* 临时目录清不掉不是这一组的问题 */ }
        }
        return problems;
    }

    /// 连着两次落在同一格上 —— 那就是用户双击。判据走真的那条路（<see cref="OnCellUp"/> →
    /// <see cref="BeginEdit"/> → <see cref="MoveEdit"/>），不直接调 <see cref="MoveEdit"/>：
    /// 「双击判据接得上抬起」那一段正是要验的东西，跳过去就量不到它了。
    private static void DoubleTap(Calendar win, DateOnly day)
    {
        win.OnCellUp(day);
        win.OnCellUp(day);
    }

    /// <summary>编辑态那三件事的判据：换一天跟过去、抬起不再重造格子、抬起来看得清。
    ///
    /// 三条都是 2026-10-06 他亲手用出来的那三条（切不过去 / 闪一下 / 看不清），
    /// 而那三条过去没有一条能在这里被问到 —— <see cref="BuildSelfTest"/> 量的是装窗，
    /// 抬起那一段整条在跑起来那一发读（<c>--calendar</c>）。这一发不用桌面：
    /// 编辑态的每一件事都是窗里侧的状态（层、选中、编辑框的样式和位置），
    /// 引擎发不发消息、OS 给不给焦点都不参与，所以它能进 <c>--check</c>。
    ///
    /// 每一处都配一条对照：不许量成「编辑态什么都有」或者「永远不重造格子」那种
    /// 一头倒的形状（见 <c>CODING_STANDARDS.md</c> 取证方法论第 5 条）。</summary>
    private static List<string> EditingJudges(Calendar win)
    {
        var problems = new List<string>();
        var first = new DateOnly(2026, 9, 10);
        var second = new DateOnly(2026, 9, 17);
        var kept = "头一天写下的";

        // ---- 一：编辑态里换一天，编辑框跟过去，原来那半句字先交出去 -----------------
        // 「头一天写下的」只放进输入框，**不**先落盘：落盘那条判据量的正是「换一天时
        // 把它交出去」。先替它写进去的话，那条判据量到的是自己写的那一份，
        // 把提交那一段整个撤掉它照样绿（第一次反向验就是这么白装的）。
        win.Host.ForceStateForTest(CalendarHost.State.Editing);
        win._editingDay = first;
        win._editor.Text = kept;
        win._editor.Visible = true;
        DoubleTap(win, second);
        if (win._editingDay != second)
            problems.Add($"编辑态里双击另一格，正在编辑的还停在 {win._editingDay:MM-dd}，该是 {second:MM-dd}");
        if (win._notes.Get(first) != kept)
            problems.Add($"换一天把原来那一格那半句字冲掉了（那一格现在是「{win._notes.Get(first)}」）—— " +
                         "契约那句「写到别处就提交」在日历上也成立，点的是日历上另一格");
        if (win._editor.Text != win._notes.Get(second))
            problems.Add($"换一天之后输入框里是「{win._editor.Text}」，该装第二天那一份");

        // 对照：还在同一格上双击，一个字节都不许动 —— 冲掉用户打了半句还没提交的字，
        // 比不响应还糟。
        var sameCell = win._editor.Text;
        win._editor.Text = "改了还没提交";
        DoubleTap(win, second);
        if (win._editor.Text != "改了还没提交" || win._editingDay != second)
            problems.Add("在同一格上再双击，输入框里那半句字被冲掉了 —— 该是什么都不动");
        win._editor.Text = sameCell;

        // ---- 二：抬起那一次不许把整月格子重造一遍 ------------------------------------
        // 抬起走的正是 ApplyLayout，而过去那一步会把 35 颗格子 QueueFree 再挂回来 ——
        // 那一帧格子上什么都没有，屏幕上就是「闪一下」。同一个形状下不许换控件。
        var before = win._cells.Select(c => c.Box.GetInstanceId()).ToList();
        win.ApplyLayout();
        win.ApplyLayout();
        if (!win._cells.Select(c => c.Box.GetInstanceId()).SequenceEqual(before))
            problems.Add("同一个月份、同一个形状下 ApplyLayout 把格子重造了一遍 —— 抬起那一下闪就是这么来的");
        // 对照：换月份必须重造（不重造就成了「翻月份还是上个月那批格子」）。
        win._shownMonth = new DateOnly(2026, 8, 1);
        win.ApplyLayout();
        if (win._cells.Select(c => c.Box.GetInstanceId()).SequenceEqual(before))
            problems.Add("换了月份格子还是原来那批 —— 「同形状才不重造」被写成了「永远不重造」");
        win._shownMonth = new DateOnly(2026, 9, 1);
        win.ApplyLayout();

        // ---- 三：抬起来要看得见：输入框有实底、有边、字亮，其余格压暗 -----------------
        win.Host.ForceStateForTest(CalendarHost.State.Editing);
        win._editingDay = second;
        win.Paint(win.CurrentLayout());
        var typed = win._editor.GetThemeStylebox("focus") as StyleBoxFlat;
        if (typed == null || !typed.DrawCenter || typed.BgColor.A < 0.8f)
            problems.Add($"编辑态的输入框没有实底（不透明度 {typed?.BgColor.A.ToString("F2") ?? "读不到"}/1）—— " +
                         "他报的那句「输入也几乎看不清」就是这一条");
        if (typed == null || typed.GetBorderWidth(Side.Left) < 1)
            problems.Add("编辑态的输入框没有边 —— 「弹出的编辑窗口没有边界」是这一半");
        var ink = win._editor.GetThemeColor("font_color");
        if (ink.R + ink.G + ink.B < 2.4f)
            problems.Add($"编辑态输入框的字色是 {ink}，压在深底上读不出来");
        if (!win._frame.Visible)
            problems.Add("编辑区那一圈边在编辑态没有画出来 —— 抬起来那一整块矩形看不出到哪儿为止");
        var editing = win._cells.FirstOrDefault(c => c.Day == second);
        var editingStyle = editing?.Box.GetThemeStylebox("normal") as StyleBoxFlat;
        if (editingStyle == null || editingStyle.GetBorderWidth(Side.Left) < 1)
            problems.Add("编辑态下正在编辑的那一格没有描边 —— 那一整块里看不出是这一格");
        // 画序：输入框（和 widget 那一档的备注那一行）必须画在格子**之上**。
        // 这一条 2026-10-06 拍帧才发现：接线全对（字色 (1,1,1,0.92)、字号 19、实底、2 像素亮边
        // 都量过了），而字在像素上就是读不出来 —— 少的是画序。格子是 RebuildCells 每次新加的，
        // 同一层里后加的画在上面，而这两颗是 BuildUi 里挂的。
        // 「摆在这一格里=True」和「在树上」都问不到它：那两条量的是几何和存在，不是前后。
        var cellZ = editing?.Box.ZIndex ?? 0;
        if (win._editor.ZIndex <= cellZ)
            problems.Add($"输入框的画序在格子之下（它 {win._editor.ZIndex} / 格子 {cellZ}）—— "
                         + "格子是后加的，会把它整个盖住，字读不出来而接线看着全对");
        if (win._noteLine.ZIndex <= cellZ)
            problems.Add($"备注那一行的画序在格子之下（它 {win._noteLine.ZIndex} / 格子 {cellZ}）—— "
                         + "widget 那一档的备注一直被格子盖着");
        if (win._frame.ZIndex <= cellZ)
            problems.Add("编辑区那一圈边的画序在格子之下 —— 它会被格子的底压掉一圈");
        var other = win._cells.FirstOrDefault(c => c.Day == first);
        if (other == null)
            problems.Add($"认不出 {first:MM-dd} 那一格，编辑态那一格压暗没量成");
        else
        {
            var mark = other.Number.GetThemeColor("font_color");
            if (mark.A > 0.35f)
                problems.Add($"编辑态下没在编辑的那一格还是 {mark.A:F2} 不透明度 —— 两眼落不到该编的那一格上");
        }

        // 对照：摆着（没在打字）时那三样一样都不许有 —— 它本来是备注那一行换了个能打字的样子，
        // 而那一圈边只属于抬起那一段。
        win.Host.ForceStateForTest(CalendarHost.State.Parked);
        win.Paint(win.CurrentLayout());
        var plain = win._editor.GetThemeStylebox("focus") as StyleBoxFlat;
        if (plain != null && plain.DrawCenter)
            problems.Add("摆着时输入框也多了一颗实底 —— 它本来就该是备注那一行");
        if (win._frame.Visible)
            problems.Add("摆着时编辑区那一圈边还在 —— 它只属于抬起那一段");
        var otherBack = win._cells.FirstOrDefault(c => c.Day == first);
        if (otherBack != null && otherBack.Number.GetThemeColor("font_color").A <= 0.35f)
            problems.Add("摆着时格子的字还是压着的 —— 压暗只属于编辑态");
        return problems;
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOnHost Build = new(
        () => "日历装窗通过：八颗各在 _root 手里、导航那一行兄弟序是 ‹ 月份 ›、周标签七颗不少、挂第二次会抛；" +
              "编辑态换一天跟得过去（原来那半句字先交出去、同格再双击一个字都不动）、抬起不重造格子（同形状不动、换月份必须换）、" +
              "抬起来看得见（输入框实底 + 边 + 亮字、编辑区那一圈边在、该编那一格描边、其余格压暗；嵌着时三样一样都没有）",
        "日历装窗", BuildSelfTest);

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Arithmetic = new(
        () => "日历算术通过：格子 5/6 行各一次、三档各摆一次（widget 四角 + 格子四角）、" +
              "四块工作区上比例不变、每条比例拧到两端都不出屏、命中三档和闸门各判对、双击间隔夹得住、" +
              "**摆着的时候引擎那一扇是藏着的，可见就藏回去（藏着 Win+D 扫不着）**、" +
              "**填底的档任何一天、任何一列都不画成全透明（日历一整块，不按图标挖洞），" +
              "outline 那一档反过来一格都不填**",
        "日历", SelfTest);

    /// <summary>不碰窗、不碰桌面的那几发算术。真的命中测试和真的抬起要在跑起来那一发读
    /// （<c>--calendar</c>），这里只保证判据和布局自己讲得通。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        var work = new Win32.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1400 };
        var options = new Settings().Calendar;

        // 格子：2026 年 9 月，周一开头。9 月 1 日是周二，所以第一格是 8 月 31 日，5 行。
        var cells = MonthCells(2026, 9, true);
        if (cells.Count != 35) problems.Add($"2026-09 该是 5 行 35 格，读出来 {cells.Count} 格");
        if (cells[0] != new DateOnly(2026, 8, 31)) problems.Add($"第一格该是 8-31，是 {cells[0]}");
        if (cells[1] != new DateOnly(2026, 9, 1)) problems.Add($"第二格该是 9-01，是 {cells[1]}");
        if (cells[^1] != new DateOnly(2026, 10, 4)) problems.Add($"最后一格该是 10-04，是 {cells[^1]}");
        // 2026 年 8 月 1 日是周六，周一开头那一型下要 6 行 —— 面板换月份会换高度，是有意的。
        if (MonthCells(2026, 8, true).Count != 42) problems.Add("2026-08 该是 6 行");
        // 周日开头那一型：同一月第一格换成 8-30
        if (MonthCells(2026, 9, false)[0] != new DateOnly(2026, 8, 30)) problems.Add("周日开头那一型第一格不对");
        if (WeekdayName(0, true) != "周一" || WeekdayName(6, true) != "周日" ||
            WeekdayName(0, false) != "周日") problems.Add("星期那一行的列序和 weekStartsMonday 对不上");

        // ---- widget 那一档：一条边占工作区多少，其余全从它推 --------------------------
        //
        // 期望值是照着算术「18% 的 2560 = 460 宽」算的，不是抄旧那套像素常量的成品 448x412：
        // 旧的 448 是 64 像素一格乘七列写死出来的，换一块屏它就不再是 448 —— 那正是要修的东西。
        // 18% 比那套写死的宽 2.7%（460 对 448），格子和字号跟着一起放大，比例一格没走样。
        var one = MonthCells(2026, 9, true).Count / Columns;
        options.Style = "widget";
        var widgetMarginX = work.Width * options.MarginXPercent / 100;
        var widgetMarginY = work.Height * options.MarginYPercent / 100;
        foreach (var (corner, expect) in new[]
        {
            ("topLeft", (widgetMarginX, widgetMarginY)),
            ("topRight", (2560 - 460 - widgetMarginX, widgetMarginY)),
            ("bottomLeft", (widgetMarginX, 1400 - 422 - widgetMarginY)),
            ("bottomRight", (2560 - 460 - widgetMarginX, 1400 - 422 - widgetMarginY)),
        })
        {
            options.Corner = corner;
            var laid = Measure(work, options, one);
            if ((laid.X, laid.Y) != expect)
                problems.Add($"{corner} 那一角该在 {expect}，算出来 ({laid.X},{laid.Y})");
            if (laid.Width != 460 || laid.Height != 422)
                problems.Add($"18% 的那一发面板该是 460x422，是 {laid.Width}x{laid.Height}");
            // 页脚是按字给的（日期 18 + 输入框 26），所以那颗输入框再也不会画到面板下沿外面。
            if (laid.FooterH - laid.DateLineH < laid.MinEditHeight)
                problems.Add($"页脚装不下输入框：给 {laid.FooterH}，要 {laid.MinEditHeight}");
        }
        options.Corner = "topRight";
        // 边距拉到那条声明的最大（25%）：两块 25% 加一张 100% 宽的格子，必须夹回工作区里
        options.WidgetWidthPercent = 60;
        options.MarginXPercent = 25;
        options.MarginYPercent = 25;
        var pushed = Measure(work, options, one);
        if (pushed.X < work.Left || pushed.X + pushed.Width > work.Right ||
            pushed.Y < work.Top || pushed.Y + pushed.Height > work.Bottom)
            problems.Add($"边距 25% + 面板 60% 没夹回工作区：({pushed.X},{pushed.Y})+{pushed.Width}x{pushed.Height}");
        options.WidgetWidthPercent = 18;
        options.MarginXPercent = 1;
        options.MarginYPercent = 1;

        // ---- 格子那两档：一套几何，两种填法 ------------------------------------
        if (!IsGrid("wallpaper") || !IsGrid("outline") || IsGrid("widget") || IsGrid(null!))
            problems.Add("style 派发认错了档");

        // 底是算术：1280x720 上两条边各 100%、抬头取默认那一档 —— 格宽 1280/7 = 182.857 → 182，
        // 抬头 720 的 7% = 50.4 → 50，格高 (720-50)/5 = 134。整数除法一律往下取，所以差的那半
        // 颗像素是键的粒度，不是布局走偏。屏幕上那一侧的验收不在这条期望里，在 --calendar --shot
        // 抓的那一帧。
        var full = new Win32.Rect { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
        // 这一发不抄默认值，抄的是配置里那三条比例：两条边各 100%、抬头一带取默认那条。
        // 默认值改了这条期望要跟着改，而且是「一起改」不是「追」—— 布局没有别的出处。
        var baseGrid = new Settings().Calendar;
        baseGrid.Style = "wallpaper";
        baseGrid.Corner = "topLeft";
        baseGrid.MarginXPercent = 0;
        baseGrid.MarginYPercent = 0;
        baseGrid.WidthPercent = 100;
        baseGrid.HeightPercent = 100;
        var filled = MeasureGrid(full, baseGrid, one);
        if (filled.Width != 1280 || filled.Height != 720)
            problems.Add($"两条边都 100% 那一发该铺满 1280x720，是 {filled.Width}x{filled.Height}");
        if (filled.CellW != 182 || filled.CellH != 134 || filled.HeadBand != 50)
            problems.Add($"布局算术那一发：格子该是 182x134、抬头一带 50（底是 1280/7=182.857→182、" +
                         $"(720-50)/5=134），" +
                         $"读出来 {filled.CellW}x{filled.CellH}、一带 {filled.HeadBand}");
        if (filled.HeadBand + one * filled.CellH > filled.Height)
            problems.Add("格子行数乘出来比面板还高，最后一行会被裁掉");

        // outline 与 wallpaper 只差「填不填底」这一旗，几何必须一格不差
        baseGrid.Style = "outline";
        var bare = MeasureGrid(full, baseGrid, one);
        if (bare.Fills || !filled.Fills)
            problems.Add("outline 该不填底、wallpaper 该填底");
        if (bare.CellW != filled.CellW || bare.CellH != filled.CellH ||
            bare.HeadBand != filled.HeadBand ||
            (bare.X, bare.Y, bare.Width, bare.Height) != (filled.X, filled.Y, filled.Width, filled.Height))
            problems.Add("两档格子的几何走岔了");
        baseGrid.Style = "wallpaper";

        // **默认那一份（横向 100% × 纵向 70%）**：四条边跟着 corner 走。
        // 期望值**从 `half` 自己那两个键推出来**（`halfW` / `halfH`）——
        // 抄死数字的话默认值下次再改，这条就跟着过期，而它过期得没有声音。
        //
        // **那个 X 走 `Corner` 自己那一步，不重写一遍算术**（2026-10-12 改的）：
        // 默认横向 100% 之后，「工作区 − 宽度 − 边距」算出来是负数，而 `Corner` 会把它
        // 夹回工作区 —— 这里先前推一个负数当期望值，日历那一组立刻报四条假问题
        // （读出来 (0,14) 才是对的）。所以期望值就是**问 `Corner` 要它自己那份答案**。
        var half = new Settings().Calendar;
        half.Style = "wallpaper";
        var halfW = work.Width * half.WidthPercent / 100;
        var halfH = work.Height * half.HeightPercent / 100;
        foreach (var corner in new[] { "topLeft", "topRight", "bottomLeft", "bottomRight" })
        {
            half.Corner = corner;
            var (wantX, wantY) = Corner(work, half, halfW, halfH);
            var g = MeasureGrid(work, half, one);
            if ((g.X, g.Y) != (wantX, wantY))
                problems.Add($"格子 {corner} 那一角该在 ({wantX},{wantY})，算出来 ({g.X},{g.Y})");
            if (g.Width != halfW || g.Height != halfH)
                problems.Add($"默认那一份（{half.WidthPercent}%×{half.HeightPercent}%）该是 " +
                             $"{halfW}x{halfH}，是 {g.Width}x{g.Height}");
        }
        half.Corner = "topLeft";
        // 两条边各走各的：这块带鱼屏上想只收紧横向、纵向还铺到九成，就该只拧那一条
        var ultra = new Win32.Rect { Left = 0, Top = 0, Right = 3440, Bottom = 1440 };
        half.WidthPercent = 50;
        half.HeightPercent = 90;
        var wide = MeasureGrid(ultra, half, one);
        if (wide.Width != 1720 || wide.Height != 1296)
            problems.Add($"50%×90% 在 3440x1440 上该是 1720x1296，是 {wide.Width}x{wide.Height}");
        // 摆回默认那一份，**从声明表取**（不写死 70）：下一段量的是「默认值本身」，
        // 这里摆的值和默认值不一致的话，量到的就不是默认值了。
        half.WidthPercent = new Settings().Calendar.WidthPercent;
        half.HeightPercent = new Settings().Calendar.HeightPercent;

        // ---- 换分辨率不许换比例 ----------------------------------------------------
        //
        // 这一发是这次改动的正题：同一份设置在四块形状完全不同的工作区上，量出来必须还是
        // 那几条百分比 —— 面板占那条边的多少、格子的长宽比、字和格子的高度关系，
        // 一条都不许多一块屏就变。以前这些数是 64x62 那套像素常量，换个分辨率就得重挑。
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3440, 1440) })
        {
            var area = new Win32.Rect { Left = 0, Top = 0, Right = w, Bottom = h };
            var every = new Settings();
            every.ClampToDeclarations();
            foreach (var style in new[] { "wallpaper", "outline", "widget" })
            {
                every.Calendar.Style = style;
                var g = Measure(area, every.Calendar, one);
                var edge = style == "widget" ? every.Calendar.WidgetWidthPercent
                    : every.Calendar.WidthPercent;
                if (g.Width != area.Width * edge / 100)
                    problems.Add($"{style} 在 {w}x{h} 上宽不是工作区的 {edge}%：{g.Width}/{area.Width}");
                if (style != "widget" && g.Height != area.Height * every.Calendar.HeightPercent / 100)
                    problems.Add($"wallpaper 在 {w}x{h} 上高不是工作区的 {every.Calendar.HeightPercent}%：" +
                                 $"{g.Height}/{area.Height}");
                // 整块面板必须留在屏幕上：四个角各走一次
                foreach (var corner in new[] { "topLeft", "topRight", "bottomLeft", "bottomRight" })
                {
                    every.Calendar.Corner = corner;
                    var at = Measure(area, every.Calendar, one);
                    if (at.X < area.Left || at.Y < area.Top ||
                        at.X + at.Width > area.Right || at.Y + at.Height > area.Bottom)
                        problems.Add($"{style} 在 {w}x{h} 的 {corner} 出了屏：" +
                                     $"({at.X},{at.Y})+{at.Width}x{at.Height}");
                }
                every.Calendar.Corner = "topRight";
                // 格子高必须是正的、行数乘出来不许把最后一行挤出面板，字不许比格子高
                if (g.CellH <= 0 || g.HeadBand + one * g.CellH > g.Height)
                    problems.Add($"{style} 在 {w}x{h} 上格子高 {g.CellH}，抬头一带 {g.HeadBand}，" +
                                 $"面板 {g.Height} —— 最后一行会被裁掉");
                if (g.NumberFont > g.CellH || g.TitleFont > g.HeadBand)
                    problems.Add($"{style} 在 {w}x{h} 上字比格子高：日号 {g.NumberFont} 对格子 {g.CellH}");
                // 页脚那一整带（日期 + 输入框）不许把面板撑破
                if (!g.Grid && g.HeadBand + one * g.CellH + g.FooterH != g.Height)
                    problems.Add($"widget 在 {w}x{h} 上页脚和面板对不上：" +
                                 $"{g.HeadBand}+{one}x{g.CellH}+{g.FooterH} ≠ {g.Height}");
            }
        }
        // 每一条比例各拧到声明表的两端，在四块屏 × 三档上各量一遍（六行的月份，最高那一发）：
        // 面板必须还在屏幕里、格子高必须是正的、字必须还在它自己那一行里。
        // 这一发防的是「比例没有上界」那一类事故 —— 60% 宽的小面板在这块 1400 高的屏上本来
        // 会长出 1421 高，是 MeasureWidget 里那一次按高度收把它留下来的；收完之后它还在屏里。
        // 键名和范围都从声明表取，这里不写第二份数字。
        var sweep = new (string Key, Action<CalendarOptions, int> Set)[]
        {
            ("calendar.widthPercent", (o, v) => o.WidthPercent = v),
            ("calendar.heightPercent", (o, v) => o.HeightPercent = v),
            ("calendar.widgetWidthPercent", (o, v) => o.WidgetWidthPercent = v),
            ("calendar.headerPercent", (o, v) => o.HeaderPercent = v),
            ("calendar.marginXPercent", (o, v) => o.MarginXPercent = v),
            ("calendar.marginYPercent", (o, v) => o.MarginYPercent = v),
            ("calendar.textPercent", (o, v) => o.TextPercent = v),
        };
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (2560, 1400), (3440, 1440) })
        {
            var area = new Win32.Rect { Left = 0, Top = 0, Right = w, Bottom = h };
            foreach (var (key, set) in sweep)
            foreach (var bound in new[] { Settings.Decl(key).Min, Settings.Decl(key).Max })
            foreach (var style in new[] { "wallpaper", "outline", "widget" })
            foreach (var corner in new[] { "topLeft", "bottomRight" })
            {
                var every = new Settings().Calendar;
                every.Style = style;
                every.Corner = corner;
                set(every, bound);
                var g = Measure(area, every, 6);
                if (g.X < area.Left || g.Y < area.Top ||
                    g.X + g.Width > area.Right || g.Y + g.Height > area.Bottom)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，面板出了屏：" +
                                 $"({g.X},{g.Y})+{g.Width}x{g.Height}");
                if (g.CellH <= 0 || g.HeadBand + 6 * g.CellH > g.Height)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，格子高 {g.CellH}：" +
                                 $"抬头一带 {g.HeadBand} 加六行比面板 {g.Height} 还高");
                if (g.TitleFont * FontLinePercent / 100 > g.HeaderH + g.FontFloor)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，月份那一行装不下它的字：" +
                                 $"字 {g.TitleFont} 对行高 {g.HeaderH}");
                // 宽那一头：挤不下就不画（SubFits / DateFits 两旗），而「挤不下」只允许发生在
                // 字已经缩到下限的那一发上 —— 否则就是按比例收字那一步没生效。
                if (g.TitleFont * MonthLineChars > g.MonthWidth + g.FontFloor)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，月份那行宽了：" +
                                 $"字 {g.TitleFont}×{MonthLineChars} 对盒子 {g.MonthWidth}");
                // 宽那一头：挤不下就不画（SubFits / DateFits 两旗），而「挤不下」只允许发生在
                // 字已经缩到下限的那一发上 —— 否则就是按比例收字那一步没生效。
                if (g.Grid && !g.SubFits && g.SubFont > g.FontFloor)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，格子里那一列挤到下一格：" +
                                 $"字 {g.SubFont}×{SubLineChars} 对剩下的宽 " +
                                 $"{g.CellW - g.CellInset * 2 - g.NumberWidth}，而它还没缩到下限 {g.FontFloor}");
                if (!g.DateFits && g.DateFont > g.FontFloor)
                    problems.Add($"{style} 在 {w}x{h} 上把 {key} 拧到 {bound}，「今天是」那行宽了：" +
                                 $"字 {g.DateFont}×{TodayLineChars} 还没缩到下限 {g.FontFloor}");
            }
        }
        // 最小的那一端（两条边都 20%、六行的月份）：格子还要是正的，字掉到下限就不许再掉
        half.WidthPercent = Settings.Decl("calendar.widthPercent").Min;
        half.HeightPercent = Settings.Decl("calendar.heightPercent").Min;
        var tiny = MeasureGrid(work, half, 6);
        if (tiny.CellH <= 0 || tiny.NumberFont < tiny.FontFloor || tiny.SubFont < tiny.FontFloor ||
            tiny.NoteFont < tiny.FontFloor)
            problems.Add($"两条边都 20% 那一发缩过头了：格子 {tiny.CellH} 高、下限 {tiny.FontFloor}、" +
                         $"日号 {tiny.NumberFont} 字、次行 {tiny.SubFont} 字、备注 {tiny.NoteFont} 字");
        // 字的那条下限乘的是系统缩放，不是分辨率：125% 那一档上它必须比 100% 那一档高
        var small = MeasureGrid(work, half, 6);
        var scaled = MeasureGrid(work, half, 6, 125);
        if (scaled.FontFloor <= small.FontFloor)
            problems.Add($"像素下限没跟系统缩放走：100% 那发 {small.FontFloor}，125% 那发 {scaled.FontFloor}");
        // 字号那条乘数真在动字：textPercent 拧到声明的最大那一发，日号和标题都必须比默认那一发大
        // （默认那份的 style 本来就是 wallpaper，两发只差在 textPercent 这一条上）
        var bigText = new Settings().Calendar;
        bigText.TextPercent = Settings.Decl("calendar.textPercent").Max;
        var larger = MeasureGrid(work, bigText, one);
        var plain = MeasureGrid(work, new Settings().Calendar, one);
        if (larger.NumberFont <= plain.NumberFont || larger.TitleFont <= plain.TitleFont)
            problems.Add($"textPercent 拧到 {bigText.TextPercent} 字没跟着变大：日号 " +
                         $"{plain.NumberFont}→{larger.NumberFont}，标题 {plain.TitleFont}→{larger.TitleFont}");
        // 抬头那一条比例也真在动那一整带：拧到最大的一发，一带必须比默认那一发占得多
        var tallHead = new Settings().Calendar;
        tallHead.HeaderPercent = Settings.Decl("calendar.headerPercent").Max;
        var heady = MeasureGrid(work, tallHead, one);
        if (heady.HeadBand * 100 / heady.Height < tallHead.HeaderPercent - 1)
            problems.Add($"headerPercent 拧到 {tallHead.HeaderPercent}，抬头一带只占面板 " +
                         $"{heady.HeadBand * 100 / heady.Height}%（{heady.HeadBand}/{heady.Height}）");
        half.WidthPercent = new Settings().Calendar.WidthPercent;
        half.HeightPercent = new Settings().Calendar.HeightPercent;

        // 声明表里的默认值必须真走格子那两档 —— 这一条是「文档说 wallpaper、屏幕出 widget」
        // 那个形状的判据：它一红，就说明又一处键没人读了
        var shipped = new Settings();
        shipped.ClampToDeclarations();
        if (shipped.Calendar.Style != "wallpaper" || !IsGrid(shipped.Calendar.Style))
            problems.Add($"默认那一份 calendar.style 该是 wallpaper，读出来「{shipped.Calendar.Style}」");

        // 命中那五档仍然要判（它们回答「屏幕上那一点归谁」），但**不再拿它当闸门**：
        // 这一扇带 WS_EX_TRANSPARENT，鼠标穿过去之后，那一点命中的是图标层不是我们，
        // 所以拿 `Ours` 当闸门条件的话闸门会永远关着。判据的来由见 MouseBridge 的类型注释。
        // 五档判据本体在 src/Shared/WindowTier.cs（两个工程共用那一份源码），这一发钉住它每一档。
        var own = (nint)0x1234;
        var ours = DesktopLayer.TierOf(own, 0x9999, "Progman", own);
        var layer = DesktopLayer.TierOf(0x7777, 0x7777, "Progman", own);
        var system = DesktopLayer.TierOf(0xAAAA, 0xAAAA, "Shell_TrayWnd", own);
        var app = DesktopLayer.TierOf(0x8888, 0x9999, "Chrome_WidgetWin_1", own);
        if (ours != WindowTier.Owner.Ours || layer != WindowTier.Owner.WallpaperLayer ||
            system != WindowTier.Owner.SystemWindow || app != WindowTier.Owner.AppWindow)
            problems.Add($"命中五档判错：{ours} / {layer} / {system} / {app}");
        // 第五档那一族每一个类名都得认出，漏一个它就掉进「归 app 窗」。
        // 这八个别名同音不同类名的都不算（大小写不一样就是两扇窗）。
        var shellKinds = new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MSTaskListWClass",
            "NotifyIconOverflowWindow", "TrayNotifyWnd", "IME", "MSCTFIME UI",
            "WinUIDesktopWin32WindowClass" };
        var missed = shellKinds.Where(k => DesktopLayer.TierOf(0xBBBB, 0xBBBB, k, own) != WindowTier.Owner.SystemWindow);
        if (missed.Any())
            problems.Add($"第五档漏认这几类：{string.Join("、", missed)}");
        // 同音不同类名的不许算进第五档。
        if (DesktopLayer.TierOf(0xBBBB, 0xBBBB, "shell_traywnd", own) != WindowTier.Owner.AppWindow ||
            DesktopLayer.TierOf(0xBBBB, 0xBBBB, "IME ", own) != WindowTier.Owner.AppWindow)
            problems.Add("第五档的类名比较该是序数比较，大小写或前后多一个空格都不许算进去");

        // 闸门三问：摆着吗、在矩形里吗、那一点上有没有图标。八个组合各判一次，
        // 其中两条是硬要求 —— 不在矩形里不许喂、压着图标不许喂（拖放归 shell）。
        if (!MouseBridge.ShouldFeed(true, true, false)) problems.Add("摆着、在矩形里、没有图标 —— 闸门该开");
        if (MouseBridge.ShouldFeed(true, true, true)) problems.Add("那一点压着图标还喂 —— 图标的拖放、选中、双击打开全是 shell 的");
        if (MouseBridge.ShouldFeed(true, false, false)) problems.Add("不在我们这块矩形里还喂 —— 等于往日历外面点一下");
        if (MouseBridge.ShouldFeed(false, true, false)) problems.Add("抬起打字那一段桥必须闭着 —— 那时 OS 自己会投");
        if (MouseBridge.ShouldFeed(false, true, true)) problems.Add("抬起那一段、还压着图标：两道都不该喂");
        if (MouseBridge.ShouldFeed(false, false, true)) problems.Add("抬起、矩形外、压着图标：三道都不该喂");
        if (MouseBridge.ShouldFeed(true, false, true)) problems.Add("矩形外、压着图标：桥那一问必须先挡掉它");
        // 八个组合里唯一该开的只有第一个 —— 这一句把「其余七格全闭着」一并兜住。
        var opened = new[]
        {
            MouseBridge.ShouldFeed(true, true, false), MouseBridge.ShouldFeed(true, true, true),
            MouseBridge.ShouldFeed(true, false, false), MouseBridge.ShouldFeed(true, false, true),
            MouseBridge.ShouldFeed(false, true, false), MouseBridge.ShouldFeed(false, true, true),
            MouseBridge.ShouldFeed(false, false, false), MouseBridge.ShouldFeed(false, false, true),
        };
        if (opened.Count(x => x) != 1)
            problems.Add($"闸门八个组合里该开 {1} 个，实际开了 {opened.Count(x => x)} 个");

        if (DesktopLayer.TierOf(0, 0, "", own) != WindowTier.Owner.NoWindow)
            problems.Add("命中测试什么都没读到时该报「什么都没有」，不是「归 app 窗」");

        // 矩形那一问：边界含左和上、不含右和下（半开区间），压在边上算在里面。
        var box = new Win32.Rect { Left = 100, Top = 200, Right = 500, Bottom = 800 };
        if (!DesktopLayer.OverCells(box, new Win32.Point { X = 100, Y = 200 })) problems.Add("左上角那一点该算在里面");
        if (!DesktopLayer.OverCells(box, new Win32.Point { X = 499, Y = 799 })) problems.Add("右下角内侧那一点该算在里面");
        if (DesktopLayer.OverCells(box, new Win32.Point { X = 500, Y = 400 })) problems.Add("右边线上那一点该算在外面");
        if (DesktopLayer.OverCells(box, new Win32.Point { X = 400, Y = 800 })) problems.Add("下边线上那一点该算在外面");
        if (DesktopLayer.OverCells(box, new Win32.Point { X = 99, Y = 400 })) problems.Add("左边线外那一点该算在外面");

        // 2026-10-15 真机坐实的一条：摆着的时候引擎那一扇是**藏着**的。
        // 换掉的那一族把「藏着」当成「被谁藏起来了，要收回」，每一秒把它显示出来 ——
        // 它成了一扇顶层可见窗，`MinimizeAll` 只扫可见的顶层窗，紧接着把它最小化；
        // 收回只碰它不碰图标层里那扇原生子窗，于是日历整扇收走之后回不来。
        // 四档各判一次：只有「摆着 + 看得见」那一格要动。
        if (DesktopLayer.ShouldHideEngineWindow(true, false))
            problems.Add("摆着、引擎那一扇已经藏着 —— 什么都不用动（藏着的窗扫不着）");
        if (!DesktopLayer.ShouldHideEngineWindow(true, true))
            problems.Add("摆着、引擎那一扇看得见 —— 要藏回去，它是离屏渲染的载体");
        if (DesktopLayer.ShouldHideEngineWindow(false, true))
            problems.Add("没在摆着（编辑态/藏起态）不许动引擎那一扇 —— 编辑态里人看见的是被抬到顶层那扇原生子窗");
        if (DesktopLayer.ShouldHideEngineWindow(false, false))
            problems.Add("没在摆着、引擎那一扇也藏着 —— 什么都不用动");

        // 双击：同一格、间隔内才算；隔得远、换了格子都不算
        var day = new DateOnly(2026, 9, 24);
        if (!IsDoubleClick(day, day, 1_000, 600)) problems.Add("400 毫秒内同一格，该算双击");
        if (IsDoubleClick(day, day, 1_000, 400)) problems.Add("600 毫秒不该算双击");
        if (IsDoubleClick(day, new DateOnly(2026, 9, 25), 1_000, 900)) problems.Add("换了格子不该算双击");
        if (IsDoubleClick(null, day, 1_000, 900)) problems.Add("第一发没有前一次，不该算双击");
        // 一次物理点击会发出两份松开（引擎把喂进去的触屏又重映射出一份鼠标事件）：
        // id 没变的那一份必须丢掉，不然「一按就抬」。
        if (!IsRepeatOfSameClick(7, 7)) problems.Add("同一次点击的第二份松开没被认出来");
        if (IsRepeatOfSameClick(8, 7)) problems.Add("下一次点击被当成了同一次的副本，双击就永远判不出来了");

        // ---- 日历一整块：填底的档不许有洞（工单 28 验收「日历一整块」那一行）--------------
        //
        // 方向刚翻（图标压在日历上面，不挖洞），而原先让位那一族的代码就是
        // 「让位段答 true 时画成全透明」。所以这一条把 7 列 × 两种周头 × 各种状态各拧一遍：
        // **填底的档，任何一列、任何一天、图标在不在，都不许答出 alpha 0**。
        // 判的是颜色，不读语言表 —— 这一族量的是 alpha，不是屏上那句字。
        //
        // `solid` 拧两档：满的和**那一键的下限**（`calendar.opacityPercent` 的下限现问，
        // 不抄那个数 —— 抄了它改成别的值这里还绿）。最淡那一档也得留着底，
        // 拧到底就变成一个洞，而洞正是这一族作废的那件事。
        var dimmest = Settings.Decl("calendar.opacityPercent").Min / 100f;
        foreach (var style in new[] { "wallpaper", "widget" })
        foreach (var solidNow in new[] { 1f, dimmest })
        {
            var row = new Settings().Calendar;
            row.Style = style;
            var want = Measure(work, row, 5, 100);
            if (!want.Fills)
                problems.Add($"{style} 那一档该往格子里填底，读出来「填不填」那一旗是关的");
            for (var column = 0; column < Columns; column++)
            foreach (var monday in new[] { true, false })
            foreach (var inMonth in new[] { true, false })
            foreach (var editingNow in new[] { false, true })
            foreach (var selectedNow in new[] { false, true })
            foreach (var todayNow in new[] { false, true })
            {
                if (CellBackground(want, editingNow, selectedNow, todayNow, inMonth, column, monday, solidNow).A <= 0f)
                    problems.Add($"{style} 那一档第 {column} 列画成了全透明 —— " +
                                 "日历要一整块照画，不按图标挖洞（图标是压在它上面的）");
            }
        }

        // 对照：outline 那一档按设计**一格都不填**，字直接压在壁纸上。
        //
        // 反过来说也不许把它填满，否则「一整块」就成了「每一档都铺满」。两半都钉。
        var bareStyle = new Settings().Calendar;
        bareStyle.Style = "outline";
        var bareWant = Measure(work, bareStyle, 5, 100);
        if (bareWant.Fills)
            problems.Add("outline 那一档该一格都不填，读出来「填不填」那一旗是开的");
        for (var column = 0; column < Columns; column++)
        foreach (var monday in new[] { true, false })
        foreach (var inMonth in new[] { true, false })
            if (CellBackground(bareWant, false, false, false, inMonth, column, monday, 1f).A != 0f)
                problems.Add($"outline 那一档第 {column} 列不该填底 —— " +
                             "它要的就是什么都不填，字直接压在壁纸上");

        // 抬头那一行：农历跟着开关走，节日挂在日期后面
        var line = DateLine(new DateOnly(2026, 9, 24));
        if (!line.Contains("农历八月十四")) problems.Add($"抬头那行该带农历，读出来「{line}」");
        if (DateLine(new DateOnly(2026, 9, 24), showLunar: false).Contains("农历"))
            problems.Add("showLunar 关着，抬头那行还写农历");
        if (!DateLine(new DateOnly(2026, 9, 25)).Contains("中秋节"))
            problems.Add("那一天是中秋节，抬头那行没写");
        // 星期那一截走语言表，不走区域设置。这一条是 2026-09-26 那一发截图欠的：
        // 上面三条全绿，屏幕上却写着「9月26日Sat」—— 只有盯星期这一条的实测结果没写。
        // 它只在 zh 那一侧咬得住（en 两侧都是 Thu），而坏的那一侧正是默认那一侧。
        var weekday = DateLine(new DateOnly(2026, 9, 24), showLunar: false);
        if (!weekday.Contains(Lang.T("周四"))) problems.Add($"抬头那行的星期该来自语言表，读出来「{weekday}」");
        return problems;
    }
}
