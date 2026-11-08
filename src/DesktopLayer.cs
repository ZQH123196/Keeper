using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>桌面层：日历那一扇窗怎么待在桌面上（顶层、半透明、点不中、压在最底下），
/// 以及「屏幕上那一点归谁」。
///
/// <b>2026-10-12 改了一族（所有者定夺：「桌面日历具有壁纸的性质」）</b>。
/// 改之前那扇窗 <c>SetParent</c> 进 <c>SHELLDLL_DefView</c> 当子窗，于是它退出顶层窗列表：
/// <c>Win+D</c> 扫不走它、它永远在所有普通窗之下。
/// 今天那一发实测：图标层是不透明的，把日历排到它底下或排到 <c>Progman</c> 里
/// <c>DefView</c> 底下，日历整个看不见（<c>docs/research/desktop-layer-and-window-measurements.md</c>
/// 「图标层在上面还是在下面」那一节，2026-10-12 复测）。所以「图标永远在日历上面」这条路量死了。
///
/// <b>2026-10-15 又改了一族（工单 28）</b>：日历改由一扇<b>原生层叠子窗</b>画 ——
/// 爹是图标层 <c>SHELLDLL_DefView</c>、排在 <c>SysListView32</c> 之下（<c>NativeLayer</c>，
/// 取向在 <c>docs/adr/0007-the-calendar-window-is-under-the-icons.md</c>）。
/// <b>引擎那一扇不再是给人看的那一扇，退成离屏渲染的载体</b>：摆着的时候它一直藏着
/// （<c>Calendar.Park</c> 那一发 <c>SW_HIDE</c>），像素推进原生子窗去。
/// 所以下面「顶层 + 每秒顶 z 序」那一族（<see cref="Park"/> 配它自己那个
/// 「每秒顶回最底下」）2026-10-15 整体退场，<c>Park</c> 只剩测量程序在用
/// （<c>Probe.cs</c>）。每一秒那一发现在只管一件：引擎那一扇不许看得见
/// （<see cref="ShouldHideEngineWindow"/>）。
///
/// <b>2026-10-12 那一版走的那条路（已退场，读数留着）</b>：日历是一扇**顶层**窗，补上
/// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>，
/// 再 <c>SetWindowPos(HWND_BOTTOM)</c> 压到最底下。于是三件事同时成立：
/// 每像素透明由 DWM 合成（图标从格子底下透过来）、鼠标直接穿到图标层（图标随便拖）、
/// 它压在所有普通窗之下（别的窗盖得住它）。
///
/// <b>代价两条，写在明处</b>（<b>2026-10-15 第 1 条那一族退场</b>，读数留着）：
/// <list type="number">
/// <item><c>Win+D</c> 会让整扇看不见。<b>实测它<b>不是</b>被最小化</b>：按 Win+D 前后
/// 4.2 秒，<c>GetWindowRect</c> / <c>IsWindowVisible</c> / <c>IsIconic</c> /
/// <c>DWMWA_CLOAKED</c> 四样一个字节都没变。Show Desktop 的做法不是最小化每一扇，是
/// <b>把桌面本身端到前面来</b>，于是日历沉到壁纸层底下。
/// 当时那一族每一秒问一次 <see cref="Where"/> 并把自己重新插回 <see cref="DesktopTop"/> 上面。</item>
/// <item>它点不中，所以「双击某一格写备注」拿不到自己那扇窗的鼠标事件，
/// 改由本来就装着的 <c>WH_MOUSE_LL</c> 按坐标判（<see cref="MouseBridge"/>）。</item>
/// </list>
///
/// <b>「挂进壁纸」那一族留着的部分</b>：<see cref="Look"/> / <see cref="Find"/> 认桌面层那几扇窗
/// （<c>Win+D</c> 对账、扫桌面还原都要它），以及 <see cref="WindowTier.Owner"/> 那五档命中判定
/// （判据本体在 <c>src/Shared/WindowTier.cs</c>，两个工程共用那一份源码）。</summary>
internal static class DesktopLayer
{
    public const string ProgmanClass = "Progman";
    public const string DefViewClass = "SHELLDLL_DefView";
    public const string WorkerWClass = "WorkerW";

    /// 壁纸层那三种类名。命中测试读出它们之一，意思是「那一点露到了壁纸」，不是「被谁挡住了」。
    public static bool IsWallpaperLayer(string rootClass) =>
        rootClass is ProgmanClass or WorkerWClass or DefViewClass;

    /// 桌面图标那一层（<c>DefView</c> 的子窗）。测量程序要问它「选中了几条」来判断一发点击
    /// 有没有真落在图标上，所以这一颗的名字也归这里管，不散在各处。
    public const string IconViewClass = "SysListView32";

    /// <summary>借来的那一扇的类名：<b>图标层底下壳自己那个 <c>SysHeader32</c></b>。
    /// 它平时藏着、不显示任何东西，借它来当日历那一块的画面。
    /// <b>这是量出来的</b> —— 量具那一族（<c>src/tools/DebugProbe/Layered.cs</c>，
    /// 工单 28 第 0 步）借的就是它，四发全过。
    /// <b>类名名单全在这里</b>（卡片 04 · S17）：Progman、DefView、WorkerW、图标层、借来的那一种，
    /// 一份名单一个出处，别处只指不抄。</summary>
    public const string BorrowedClass = "SysHeader32";

    /// <summary>枚举桌面窗那一趟要不要跳过这扇：shell 自己那几扇加图标那一层。它们不参与最小化，
    /// 列进清单里只是吓唬人。
    ///
    /// <b>这颗问的不是 <see cref="Fullscreen.IsShellForeground"/> 那一问</b>：那一档问「前台那一扇
    /// 是不是 shell 铺满整块屏的窗」，所以它认任务栏和 Win11 那扇 CoreWindow，而<b>不</b>认
    /// <c>SHELLDLL_DefView</c> 和图标层（它们从来当不上前台）。两张名单故意不相等 ——
    /// 谁把它们「统一」成一份，<c>--check</c> 的 <c>fullscreen</c> 那一组当场报。</summary>
    public static bool IsShellDesktopWindow(string className) =>
        IsWallpaperLayer(className) || className is IconViewClass;

    /// <summary>壁纸层那三扇的实测结果：Progman、挂在它底下的 DefView、以及 Win11 那个
    /// 托管着 DefView 的 WorkerW sibling。<see cref="Find"/> 从这三者里挑一个当爹，
    /// 而测量程序要的是「挑之前看见了什么」—— 所以挑和看分成两发，看的这一份是正本。
    ///
    /// WorkerW 那一档<b>只在 DefView 没找到时才非零</b>（问句里的那条退路）：这台机器上
    /// DefView 直接挂在 Progman 底下，印出来的 <c>workerw=0x0</c> 就是这么来的。
    /// <see cref="Read"/> 顶层那一遍是无条件的，这一档的取舍在纯问句里 —— 交出去的数不变。</summary>
    public readonly record struct Layer(nint Progman, nint DefView, nint WorkerW)
    {
        /// 挂进去该挂哪一扇。三条退路的顺序是 DefView → 托管它的 WorkerW → Progman 自己。
        public nint Parent => DefView != IntPtr.Zero ? DefView
            : WorkerW != IntPtr.Zero ? WorkerW : Progman;
    }

    /// <summary>宿主长什么样。<b>宿主变了就是壳拆了重搭</b>，看门狗靠它认出来。
    /// 判据是句柄，<b>不写死</b> —— 每一轮启动都不一样。
    /// （卡片 04 · S18 从 <c>NativeLayer</c> 搬来，字段不变；宿主那一问的找法只剩
    /// <see cref="CalendarHost"/> 一份。）</summary>
    public readonly record struct Host(IntPtr Progman, IntPtr DefView, IntPtr IconList, string Measurement);

    // ---- 桌面层查找只有一份快照（卡片 04）--------------------------------------
    //
    // <b>读桌面只有一处（<see cref="Read"/>），答问题的是三个纯问句。</b>
    // 壁纸层那一问和日历宿主那一问的筛选条件本来就不同（ADR-0003 / ADR-0007）：
    // 前者不筛可见、不筛满屏，后者要「可见 + 满屏」；<c>0x052C</c> 那一发
    // 只在宿主那一问（ensureSplit 为真）才发。两问不许合并。

    /// <summary>快照里的一扇桌面后代窗：句柄、类名、可见、矩形、父窗。
    /// <see cref="Read"/> 记的是全部后代，问句自己筛。</summary>
    internal readonly record struct DesktopWindow(
        nint Handle, string Class, bool Visible, Win32.Rect Rect, nint Parent);

    /// <summary>快照里的一扇顶层窗，按 z 序记。<c>DefViewChild</c> 只在类是
    /// <c>WorkerW</c> 时才顺手记（它的直接孩子 <c>SHELLDLL_DefView</c>，否则 0）——
    /// 壁纸层那一问的第三条退路要认「托管着 DefView 的那一扇」。</summary>
    internal readonly record struct TopLevelWindow(nint Handle, string Class, nint DefViewChild);

    /// <summary>一次桌面读取的结果。<see cref="SplitAttempted"/> 记「这一发有没有发
    /// <c>0x052C</c>」、<see cref="SplitSent"/> 记「它答没答真」—— 宿主那一问的日志要这两样，
    /// 壁纸层那一问不发这一发所以两个都是假。</summary>
    internal readonly record struct Snapshot(
        nint Progman,
        IReadOnlyList<DesktopWindow> Descendants,
        IReadOnlyList<TopLevelWindow> TopLevel,
        bool SplitAttempted,
        bool SplitSent);

    /// <summary>桌面拆开那条未公开消息。<b>不发就没有画布</b> —— 之前那一轮
    /// 「没有可挂的壁纸层」就是这么来的，属于找法错，不是机器不支持。
    /// <b>只在宿主那一问发</b>（<see cref="Read"/> 的 ensureSplit 为真）；
    /// 桌面已经是拆开的状态时它是空转，读了也没关系。</summary>
    private const uint SplitDesktop = 0x052C;
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>读一遍桌面。五步：认 Progman → （要拆就）发 <c>0x052C</c> →
    /// 数 Progman 的全部后代 → 按 z 序数顶层窗（WorkerW 顺手记它的 DefView 孩子）→ 交快照。
    /// <b>顺序是先拆桌面再枚举顶层</b>：拆桌面会拆出新的 <c>WorkerW</c>，
    /// 顶层那一遍必须排在它后面。</summary>
    internal static Snapshot Read(bool ensureSplit)
    {
        var progman = Win32.FindWindow(ProgmanClass, null);

        var splitAttempted = false;
        var splitSent = false;
        if (ensureSplit && progman != IntPtr.Zero)
        {
            splitAttempted = true;
            splitSent = Win32.SendMessageTimeout(progman, SplitDesktop, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, 3000, out _);
        }

        var descendants = new List<DesktopWindow>();
        if (progman != IntPtr.Zero)
            Win32.EnumChildWindows(progman, (hwnd, _) =>
            {
                Win32.GetWindowRect(hwnd, out var rect);
                descendants.Add(new DesktopWindow(
                    hwnd, ClassOf(hwnd), Win32.IsWindowVisible(hwnd), rect, Win32.GetParent(hwnd)));
                return true;
            }, IntPtr.Zero);

        var topLevel = new List<TopLevelWindow>();
        Win32.EnumWindows((hwnd, _) =>
        {
            var cls = ClassOf(hwnd);
            var defViewChild = cls == WorkerWClass
                ? Win32.FindWindowEx(hwnd, IntPtr.Zero, DefViewClass, null)
                : IntPtr.Zero;
            topLevel.Add(new TopLevelWindow(hwnd, cls, defViewChild));
            return true;
        }, IntPtr.Zero);

        return new Snapshot(progman, descendants, topLevel, splitAttempted, splitSent);
    }

    /// <summary>壁纸层那一问（纯函数，不再读桌面）。三条退路照旧：
    /// Progman 非零时 DefView 取「父窗是 Progman、类名是 SHELLDLL_DefView」的第一扇；
    /// Progman 为零时取顶层里第一扇 SHELLDLL_DefView；
    /// DefView 为零时 WorkerW 取顶层里第一扇「带 DefView 孩子的 WorkerW」。
    /// <b>这一问不筛可见、不筛满屏</b> —— 和日历宿主那一问的筛选条件本来就不同，不许合并。</summary>
    internal static Layer WallpaperLayerOf(Snapshot snapshot)
    {
        nint defView = IntPtr.Zero;
        if (snapshot.Progman != IntPtr.Zero)
        {
            foreach (var w in snapshot.Descendants)
                if (w.Parent == snapshot.Progman && w.Class == DefViewClass) { defView = w.Handle; break; }
        }
        else
        {
            foreach (var w in snapshot.TopLevel)
                if (w.Class == DefViewClass) { defView = w.Handle; break; }
        }

        var workerW = IntPtr.Zero;
        if (defView == IntPtr.Zero)
            foreach (var w in snapshot.TopLevel)
                if (w.Class == WorkerWClass && w.DefViewChild != IntPtr.Zero) { workerW = w.Handle; break; }

        return new Layer(snapshot.Progman, defView, workerW);
    }

    /// <summary>「桌面那一扇」那一问（纯函数，不再读桌面）：Progman 非零就是它；
    /// 否则取顶层 z 序里第一扇壁纸层；都没有答零。</summary>
    internal static nint DesktopTopOf(Snapshot snapshot)
    {
        if (snapshot.Progman != IntPtr.Zero) return snapshot.Progman;
        // 这一台机器上 Progman 不该读不出来。真读不出来时退回顶层里第一扇壁纸层，
        // 好过把日历排到 IntPtr.Zero 旁边（那会排在整个 z 序的最底下）。
        foreach (var w in snapshot.TopLevel)
            if (IsWallpaperLayer(w.Class)) return w.Handle;
        return IntPtr.Zero;
    }

    /// <summary>日历宿主那一问（纯函数，不再读桌面）：<b>候选按「类名 + 可见 + 满屏」挑</b>，
    /// 不按「顶层里没有 DefView 的那个 WorkerW」挑 —— 那一挑法会挑中别的进程
    /// 留下的 <c>170x47</c> 空壳（2026-10-15 踩过，读数在 docs/research/ 那一节）。
    /// 也不按名字取第一扇：多一扇就当没挑中，不猜。
    /// <b>IconList 取选中 DefView 的直接子窗里第一扇 SysListView32</b>；没有也照旧返回宿主，
    /// <c>图标层=0x0</c>。</summary>
    internal static Host CalendarHostOf(Snapshot snapshot)
    {
        if (snapshot.Progman == IntPtr.Zero)
            return new Host(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "Progman 读不出来");

        var splitNote = !snapshot.SplitAttempted ? "没发"
            : snapshot.SplitSent ? "已发" : "发了但答假";

        var candidates = new List<DesktopWindow>();
        foreach (var w in snapshot.Descendants)
            if (w.Class == DefViewClass) candidates.Add(w);

        nint defView = IntPtr.Zero;
        foreach (var w in candidates)
        {
            if (!w.Visible) continue;
            if (w.Rect.Width < 1000 || w.Rect.Height < 600) continue;
            defView = w.Handle;
            break;
        }

        if (defView == IntPtr.Zero)
            return new Host(snapshot.Progman, IntPtr.Zero, IntPtr.Zero,
                $"后代里没有一扇「SHELLDLL_DefView + 可见 + 满屏」（拆桌面={splitNote}，候选 {candidates.Count} 扇）");

        nint iconList = IntPtr.Zero;
        foreach (var w in snapshot.Descendants)
            if (w.Parent == defView && w.Class == IconViewClass) { iconList = w.Handle; break; }

        return new Host(snapshot.Progman, defView, iconList,
            $"Progman=0x{snapshot.Progman:X} DefView=0x{defView:X} 图标层=0x{iconList:X} 拆桌面={splitNote}");
    }

    public static Layer Look() => WallpaperLayerOf(Read(ensureSplit: false));

    /// <summary>挂进去的爹。这台机器上的形状是 `SHELLDLL_DefView` 直接挂在 `Progman` 底下
    /// （2026-09-26 实测，没有 `WorkerW` sibling）；另一种形状是 DefView 挂在某个 WorkerW 底下。
    /// 三条退路照测量程序那一份抄，后两条在这台机器上从没走过 —— 别当成已经验过的分支。
    ///
    /// <b>要 DefView 本身的那两处不要用这一发</b>：图标层是 DefView 的子窗（拿 WorkerW 去
    /// <c>FindWindowEx</c> 找不到它），而 <c>WallpaperHitTest</c> 那一条是 <c>SetParent</c> 进
    /// DefView —— 这一发在两条退路上还回来的分别是 WorkerW 和 Progman，用它等于换了另一件事做。
    /// 那两处走 <see cref="Look"/> 拿 <see cref="Layer.DefView"/>。</summary>
    public static nint Find() => Look().Parent;

    /// <summary>日历宿主那一问的入口：读一遍快照（带拆桌面）再交给纯问句。
    /// <b>全仓宿主查找只剩这一发</b>（卡片 04 · S18/S19）—— <c>NativeLayer</c> 里那一份
    /// 找法（旧 <c>FindHost</c>）删掉了，<c>DebugProbe</c> 是量具、不算第二份实施（ADR-0004）。</summary>
    public static Host CalendarHost() => CalendarHostOf(Read(ensureSplit: true));

    /// <summary>类名，<b>读不到回空串</b> —— 拿空串去比类名名单比不中，就是「不是那一扇」，
    /// 不兜底、不猜。快照里每一扇的类名都用它记。</summary>
    private static string ClassOf(nint handle)
    {
        var text = new System.Text.StringBuilder(256);
        int n = Win32.GetClassName(handle, text, 256);
        return n <= 0 ? string.Empty : text.ToString(0, Math.Min(n, 255));
    }

    /// 引擎那侧报出来的窗 id 换不成 HWND，所以从窗口树上取；取不到就是 0，
    /// 调用方要把「没有 HWND」当成一个实测结果，不是当成一个异常。
    public static nint HandleOf(Window window)
    {
        if (!window.IsInsideTree()) return IntPtr.Zero;
        var fromEngine = DisplayServer.WindowGetNativeHandle(
            DisplayServer.HandleType.WindowHandle, window.GetWindowId());
        return (nint)fromEngine;
    }

    /// <summary>挂进去之前那三样。抬回来的时候要一样一样放回去，少一样就回不到原来那个像素。
    /// 矩形存的是**屏幕坐标**那一份：挂进去之后 `GetWindowRect` 仍在说屏幕坐标，
    /// 但样式位已经不是 `WS_POPUP` 了，所以还原要拿它重新按顶层窗摆一次。</summary>
    public readonly record struct Before(
        nint Hwnd, long Style, long ExStyle, Win32.Rect Screen, nint Parent)
    {
        public bool Valid => Hwnd != IntPtr.Zero;

        public override string ToString() =>
            $"hwnd=0x{Hwnd:X} 样式={Win32.DescribeStyle((nint)Style)} 矩形={Screen} 爹=0x{Parent:X}";
    }

    public static Before Capture(nint hwnd)
    {
        Win32.GetWindowRect(hwnd, out var rect);
        return new Before
        {
            Hwnd = hwnd,
            Style = (long)Win32.GetWindowLong(hwnd, Win32.GwlStyle),
            ExStyle = (long)Win32.GetWindowLong(hwnd, Win32.GwlExStyle),
            Screen = rect,
            Parent = Win32.GetParent(hwnd),
        };
    }

    /// <summary>桌面那一扇<b>顶层</b>窗：<b>就是 <c>Progman</c></b>。
    ///
    /// <b>2026-10-13 换了挑法</b>，从「z 序里最顶上那一扇壁纸层窗」改成<b>直接认 <c>Progman</c></b>。
    /// 换的理由是<b>鼠标往哪儿落</b>：日历带 <c>WS_EX_TRANSPARENT</c>，鼠标穿过它之后找的是
    /// <b>它下面那一扇</b>。要落到图标层（<c>SHELLDLL_DefView</c> → <c>SysListView32</c>），
    /// 「它下面那一扇」这条链上必须有 <c>Progman</c>。
    ///
    /// <b>原来那一版挑中的是一扇空的 <c>WorkerW</c></b>（一次实测）：这台机器上 12 扇
    /// <c>WorkerW</c> <b>全是不可见的空窗</b>，它们在 z 序链里的位置和图标层没有任何关系。
    /// 日历插到其中一扇之上，鼠标穿过去落在空窗上 —— <b>图标层收不到</b>。
    /// 症状就是「日历外面能拖图标、日历那一块拖不动」。
    ///
    /// <summary>为什么不用 <see cref="Layer.Parent"/>：它挑的是 <c>DefView</c>，而那是 <c>Progman</c> 的
    /// <b>子窗</b>。<c>SetWindowPos</c> 的 after 位按顶层窗那一叠排，给一个子窗没有定义。
    /// （卡片 04 · S18：这一问也走那一份快照，退路在纯问句 <see cref="DesktopTopOf"/> 里。）</summary>
    public static nint DesktopTop() => DesktopTopOf(Read(ensureSplit: false));

    /// <summary>把日历那一扇摆到桌面那一叠的<b>最底下</b>：顶层、半透明、点不中、不吃激活、不进 Alt-Tab。
    ///
    /// <b>不挂进壁纸层了</b> —— 理由和那天的实测结果见类型注释。
    /// 所以这里没有 <c>SetParent</c>，坐标直接用屏幕坐标。
    ///
    /// <b>四个扩展样式位各管一件事，缺一个就有一种坏形状</b>：
    /// <list type="bullet">
    /// <item><c>WS_EX_LAYERED</c> —— 每像素透明由 DWM 合成。没有它，格子的半透是对着
    /// 引擎自己的 clear 颜色混，底下什么都看不见。</item>
    /// <item><c>WS_EX_TRANSPARENT</c> —— 鼠标穿过去落到图标层。这是跨进程的那一位
    /// （<c>WM_NCHITTEST</c> 的 <c>HTTRANSPARENT</c> 只跨同一个线程，量过）。</item>
    /// <item><c>WS_EX_NOACTIVATE</c> —— 点它不抢前台。</item>
    /// <item><c>WS_EX_TOOLWINDOW</c> —— 不进 Alt-Tab、不挂任务栏。</item>
    /// </list>
    ///
    /// <b><paramref name="before"/> 必须在补这几位之前捕获</b> —— 它是抬回来那一段的还原底本
    /// （<see cref="Lift"/>），带着这几位捕获回去就再也摘不掉了。
    ///
    /// 返回一行实测结果（爹、两个样式位、矩形、它在那一叠的哪一格），调用方直接打进日志。</summary>
    public static string Park(in Before before)
    {
        Win32.SetParent(before.Hwnd, IntPtr.Zero);
        Win32.SetWindowLong(before.Hwnd, Win32.GwlStyle, (nint)before.Style);
        var ex = (long)before.ExStyle;
        Win32.SetWindowLong(before.Hwnd, Win32.GwlExStyle,
            (nint)(ex | Win32.WsExLayered | Win32.WsExTransparent | Win32.WsExNoActivate | Win32.WsExToolwindow));

        // 2026-10-12：owner 换成图标层那一扇（`SHELLDLL_DefView`）。这一发才是 Win+D 扫不走的
        // 真正原因 —— 光靠下面那个 `SetWindowPos` 顶不住：跨进程改不了桌面那一叠的 z 序，
        // 实测每顶一次 Explorer 就抢回去一次，日志里「收之后 Progman 还在上面」每秒刷一次。
        // owner 关系是我们进程内部的合法调用，shell 认，所以改成认图标层的。
        // **owner 不是父子关系**（那是 SetParent 那一发），所以四个扩展样式位该在还在。
        var owner = Win32.GetShellDefView();
        var ownerOk = owner != IntPtr.Zero && Win32.SetOwner(before.Hwnd, owner);

        var under = DesktopTop();
        Win32.SetWindowPos(before.Hwnd, under, before.Screen.Left, before.Screen.Top,
            before.Screen.Width, before.Screen.Height,
            Win32.SwpFrameChanged | Win32.SwpNoActivate | Win32.SwpShowWindow);
        Win32.GetWindowRect(before.Hwnd, out var now);
        return $"爹=0x{Win32.GetParent(before.Hwnd):X}（顶层就是 0） owner=0x{Win32.GetWindowLongPtr(before.Hwnd, Win32.GwlHwndParent):X}" +
               $"（换成功={ownerOk}）压在桌面 {Win32.ClassName(under)} 正上方 " +
               $"样式={Win32.DescribeStyle(Win32.GetWindowLong(before.Hwnd, Win32.GwlStyle))} " +
               $"扩展样式={Win32.DescribeExStyle(Win32.GetWindowLong(before.Hwnd, Win32.GwlExStyle))} " +
               $"矩形={now} 和要的一样={now.Left == before.Screen.Left && now.Top == before.Screen.Top} " +
               Where(before.Hwnd);
    }

    /// <summary>它现在落在哪一格。<b>判据只有一句：从自己往下走，撞不撞得上任意一扇壁纸层。</b>
    ///
    /// <b>撞得上 = 在位</b>（桌面层在我们下面：别的窗盖得住我们，我们盖得住桌面）。
    /// <b>撞不上 = 沉了</b>（桌面层被翻到我们上面去了，整扇看不见）。
    ///
    /// <b>这一句是照着动作写的</b>：<see cref="DesktopTop"/> 把日历插到 <c>Progman</c>
    /// 正上方，所以往下走<b>必然</b>撞得上某一扇壁纸层 —— <b>它不可能恒假</b>。
    ///
    /// <b>前面换掉的两版各栽在哪里 —— 换掉的判据都拿链上某一扇的「身份」当答案</b>：
    /// <list type="number">
    /// <item><b>问「壁纸层那一扇有没有被翻到我们上面去了」</b>（往上找）：<b>动作恰好就是把日历
    /// 插到壁纸层某一扇的正上方</b>，判据永远命中刚插进去的那一扇。于是每一秒答「不在最底下」、
    /// 每秒重发一次 <c>SetWindowPos</c>，而<b>收之前和收之后逐字相同</b> —— 一次运行 180 次。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。</item>
    /// <item><b>问「往下第一扇壁纸层是不是 <c>Progman</c>」</b>：实测顶层链一共 300 扇，
    /// <c>Progman</c> 在<b>第 181 扇</b>，而 12 扇 <c>WorkerW</c> 全在<b>第 66 到 168 扇</b> ——
    /// <b><c>WorkerW</c> 全都比 <c>Progman</c> 高</b>。所以往下走撞上的必然是那些
    /// <c>WorkerW</c> 里的某一扇，<b>永远不是 <c>Progman</c></b>，于是那一版又变成每秒重插。</item>
    /// </list>
    /// <b>要问的是「桌面层在不在我下面」，不是「下面那一扇叫什么」。</b>
    ///
    /// <b>顺带答掉一件事</b>：链上往下 20 多扇全是零尺寸的系统窗（<c>IME</c>、
    /// <c>MSCTFIME UI</c>）和不可见的托盘窗，<b>所以「正下方紧挨着的是谁」和「桌面那一扇」
    /// 不是同一件事</b> —— 中间隔着多少扇都不影响这件事成立。</summary>
    public static string Where(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return "没有 HWND，读不出它落在哪一格";

        var at = Win32.GetWindow(hwnd, Win32.GwHwndNext);
        for (var steps = 0; at != IntPtr.Zero && steps < 600; steps++)
        {
            var cls = Win32.ClassName(at);
            if (IsWallpaperLayer(cls))
                return $"在最底下（往下第 {steps + 1} 扇撞上壁纸层 {cls} —— " +
                       "桌面层在我们下面，别的窗盖得住我们，我们盖得住桌面）";
            at = Win32.GetWindow(at, Win32.GwHwndNext);
        }
        return "不在最底下：往下走完 600 扇都没撞上壁纸层 —— " +
               "桌面层被翻到我们上面去了，整扇看不见（Show Desktop 就是这么干的）";
    }

    /// <summary>摆着的时候引擎那一扇该做什么 —— <b>判据本体，纯算术</b>，
    /// 自检四档各拧一遍（不碰窗）。
    ///
    /// <para><b>摆着的时候它该是藏着的</b>（<c>Calendar.Park</c> 那一发 <c>SW_HIDE</c>）。
    /// 它是离屏渲染的载体：日历的像素推进图标层里那扇原生子窗（<c>NativeLayer</c>），
    /// 人看见的是那一扇。所以「它看得见」本身就是一件要修的事 —— 谁把它
    /// <c>ShowWindow</c> 出来了，就藏回去。</para>
    ///
    /// <para><b>为什么「藏着」不再是坏形状</b>（2026-10-15 真机坐实）：换掉的那一版判据
    /// （<c>IsAtBottom</c>）认的是「<c>IsWindowVisible</c> 答 false = 被谁藏起来了，要收回」，
    /// 于是每一秒把刚藏好的窗 <c>ShowWindow</c> 出来 → 它成了一扇顶层可见窗 →
    /// <c>MinimizeAll</c> 只扫<b>可见的</b>顶层窗，紧接着把它最小化。而那一发的收回
    /// 只碰它、不碰图标层里那扇原生子窗 —— 于是按 <c>Win+D</c> 再撤销之后，
    /// 日历整扇收走就再也回不来。藏着就扫不着，这是新方向下该成立的那一件。</para>
    ///
    /// <para>没在摆着的时候（编辑态、托盘藏起态）一个字都不动：编辑态里人看见的是
    /// 被抬到顶层的那扇原生子窗（<c>NativeLayer.Lift</c>），引擎那一扇照样藏着。</para></summary>
    public static bool ShouldHideEngineWindow(bool parked, bool engineWindowVisible) =>
        parked && engineWindowVisible;

    /// <summary>每一秒那一发：引擎那一扇摆着的时候不许看得见，谁把它显示出来就藏回去。
    ///
    /// <para><b>托盘、<c>Explorer</c> 重启、别的程序抢前台之后还归它管的只剩这一件。</b>
    /// 位置那一族（压在桌面正上方、每秒顶一次 z 序）连同它的判据一起退场了 ——
    /// 理由和读数在 <see cref="ShouldHideEngineWindow"/> 那一段。</para>
    ///
    /// <para>已经藏着的时候一个字节都不动（<c>Calendar.Tick</c> 每秒问一次）。
    /// 返回 <c>null</c> 表示「什么都不用动」，非 <c>null</c> 是那一行实测结果。</para></summary>
    public static string? KeepEngineWindowHidden(nint hwnd, bool parked)
    {
        if (hwnd == IntPtr.Zero) return null;
        var visible = Win32.IsWindowVisible(hwnd);
        if (!ShouldHideEngineWindow(parked, visible)) return null;
        Win32.GetWindowRect(hwnd, out var before);
        Win32.ShowWindow(hwnd, Win32.SwHide);
        return $"引擎那一扇被谁显示出来了，藏回去（藏之前 可见={visible} 最小化={Win32.IsIconic(hwnd)} " +
               $"矩形={before} 藏之后可见={Win32.IsWindowVisible(hwnd)}）";
    }

    /// <summary>这一扇的 owner 是不是桌面那几扇（<c>Progman</c> / <c>WorkerW</c> /
    /// <c>SHELLDLL_DefView</c>）之一。<b>这一问只进日志，不参与「要不要收回来」的判断。</b>
    ///
    /// 2026-10-12 试过拿它短路掉那一族每秒顶 z 序的路，理由是「有 owner 时
    /// <see cref="Where"/> 永远答不在最底下，每秒白发一次 <c>SetWindowPos</c>」。
    /// <b>那是拿一个更严重的 bug 换一个小 bug</b>：owner 只保证「这一扇不在桌面子底下」，
    /// <b>不保证「在所有别的应用窗之下」</b> —— 有了 owner 它照样是顶层窗，z 序仍然归调用方管。
    /// 短路之后实测日历压在别的应用窗上面，比原来「Win+D 之后看不见」更严重。
    ///
    /// <b>两件事必须分开做</b>：「不被 Win+D 扫走」靠 owner（shell 保证有 owner 的窗跟着
    /// owner 走）；「待在所有 app 窗之下」靠那一族每秒把它按回 <see cref="DesktopTop"/> 上面。
    /// 少任何一件都不对。
    ///
    /// <b>2026-10-15：这一族连同 <c>KeepAtBottom</c> 一起退场了，所以这一问现在没有调用方</b>
    /// —— 新方向下引擎那一扇是离屏渲染的载体（见 <see cref="ShouldHideEngineWindow"/>），
    /// 「谁看它」「它在 z 序哪一格」都不归它管了。留着是因为它记着 2026-10-12 那一次
    /// 实测（owner 只管一半）；<b>要删就连上面那三段一起删</b>。
    ///
    /// <para><b>删不删归所有者定，agent 不替所有者做这个决定</b>（工单 28 收尾，2026-10-15
    /// 又搜过一遍：除了这一处定义，仓库里没有第二处提到它）。
    /// <b>要留就在注释里说清为什么留，要删就整段删</b>，别留一扇没人叫的门。</para></summary>
    public static bool OwnsTheDesktop(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var owner = Win32.GetWindowLongPtr(hwnd, Win32.GwlHwndParent);
        if (owner == IntPtr.Zero) return false;
        var root = Win32.GetAncestorRoot(owner);
        return IsWallpaperLayer(Win32.ClassName(root)) ||
               IsWallpaperLayer(Win32.ClassName(owner));
    }
    /// <summary>把日历真的挂进桌面层：<c>SetParent</c> 到 <paramref name="parent"/>、改成
    /// <c>WS_CHILD</c>、z 序排在 <paramref name="below"/> 之下。
    ///
    /// <b>这一发是 2026-10-12 量出来的另一条路</b>，走社区里桌面挂件的标准做法
    /// （Lively / Wallpaper Engine 那一族）：挂进桌面层就成了子窗，于是天生在所有
    /// 应用窗之下、天生不被 Win+D 扫走、图标层在它上面照常拖放 ——
    /// <b>那三件一次解决，不需要每秒顶 z 序</b>。相比 <see cref="Park"/> 那一族
    /// （顶层 + owner + 每秒顶 z 序）少两处会失效的机制。
    ///
    /// <b>坐标要换一次</b>：成了子窗之后 <c>SetWindowPos</c> 的 x/y 是<b>父窗客户区</b>坐标，
    /// 不是屏幕坐标（<see cref="Lift"/> 抬回顶层时也是反过来那一换）。
    ///
    /// <b>本机的形状</b>：<c>Progman</c> 带 <c>WS_EX_NOREDIRECTIONBITMAP</c>（raised desktop 模式，
    /// 实测扩展样式 <c>0x200080</c>），而 <c>SHELLDLL_DefView</c> 直接挂在 <c>Progman</c> 底下、
    /// 没有 <c>WorkerW</c> sibling —— 枚举 12 扇 <c>WorkerW</c>，子窗全是空的。
    /// 所以这条路走的是 raised desktop 那半：挂 <c>Progman</c>、z 序排到 <c>DefView</c> 之下。
    ///
    /// <b>两条判据坑</b>（都咬过，量具写在这儿省得下次再踩）：
    /// ① <c>SW_HIDE</c> 不能当像素基准 —— 藏起来之后 Explorer 不立刻补画，那一块读出来是
    ///    底色，而「日历嵌着读不到东西」在旧笔记本上也是同一个数，**两把尺子长得一模一样**。
    /// ② <c>FindWindow</c> 只找顶层窗，图标层是子窗所以答 0，得 <c>EnumChildWindows</c>。</summary>
    public static string EmbedInto(in Before before, nint parent, nint below)
    {
        if (parent == IntPtr.Zero) return "桌面层那一扇读不出来，嵌不了";
        Win32.SetParent(before.Hwnd, parent);
        // WS_CHILD 补上、WS_POPUP 摘掉 —— 研究文档记着「真正让它落到某一格的是改样式」，
        // 而摘/补样式必须带 SWP_FRAMECHANGED，不带的话 Windows 会判成最小化。
        Win32.SetWindowLong(before.Hwnd, Win32.GwlStyle,
            (nint)(((long)before.Style | Win32.WsChild) & ~Win32.WsPopup));
        var top = new Win32.Point { X = before.Screen.Left, Y = before.Screen.Top };
        if (!Win32.ScreenToClient(parent, ref top))
            return $"爹=0x{Win32.GetParent(before.Hwnd):X} 但屏幕坐标换不成客户区坐标";
        Win32.SetWindowPos(before.Hwnd, below, top.X, top.Y,
            before.Screen.Width, before.Screen.Height,
            Win32.SwpFrameChanged | Win32.SwpNoActivate);
        Win32.GetWindowRect(before.Hwnd, out var now);
        return $"嵌进去了：爹=0x{Win32.GetParent(before.Hwnd):X}/{Win32.ClassName(Win32.GetParent(before.Hwnd))} " +
               $"排在 0x{below:X}/{Win32.ClassName(below)} 之下 客户区=({top.X},{top.Y}) " +
               $"屏幕矩形={now} 和要的一样={now.Left == before.Screen.Left && now.Top == before.Screen.Top} " +
               $"可见={Win32.IsWindowVisible(before.Hwnd)} 最小化={Win32.IsIconic(before.Hwnd)} " +
               $"扩展样式={Win32.DescribeExStyle(Win32.GetWindowLong(before.Hwnd, Win32.GwlExStyle))}";
    }

    /// <summary>抬回顶层，放回 <see cref="Before.Screen"/> 那块像素。
    ///
    /// 和测量程序那一发有一处**故意**的不同：测量程序 `LiftOutOfWallpaper` 抬回去时重新压了一层
    /// `HWND_TOPMOST`，那是遮罩要的；日历抬出来只是要能打字，不该压着别人的窗口，
    /// 所以 z 序给 `HWND_TOP`（`SetWindowPos` 的 after 传 0 就是留在原地，不动 z）。
    /// 焦点那一发另说，见 <see cref="GrabForTyping"/>。</summary>
    public static string Lift(in Before before)
    {
        Win32.SetParent(before.Hwnd, IntPtr.Zero);
        // owner 也要撤：它归图标层管着的时候，抬起编辑态那一扇永远排不进桌面上方
        // （有 owner 的窗必须待在 owner 之上，图标层在桌面底下）。抬起来是要打字的，
        // 那一扇得是完全独立的顶层窗。
        Win32.SetOwner(before.Hwnd, IntPtr.Zero);
        Win32.SetWindowLong(before.Hwnd, Win32.GwlStyle, (nint)before.Style);
        Win32.SetWindowLong(before.Hwnd, Win32.GwlExStyle, (nint)before.ExStyle);
        Win32.SetWindowPos(before.Hwnd, IntPtr.Zero, before.Screen.Left, before.Screen.Top,
            before.Screen.Width, before.Screen.Height, Win32.SwpFrameChanged | Win32.SwpNoZorder);
        Win32.GetWindowRect(before.Hwnd, out var now);
        return $"回到 ({before.Screen.Left},{before.Screen.Top}) 现在是 ({now.Left},{now.Top}) " +
               $"一样={now.Left == before.Screen.Left && now.Top == before.Screen.Top} " +
               $"owner=0x{Win32.GetWindowLongPtr(before.Hwnd, Win32.GwlHwndParent):X}（抬起就该是 0） " +
               $"样式={Win32.DescribeStyle(Win32.GetWindowLong(before.Hwnd, Win32.GwlStyle))}";
    }

    /// <summary>抬出来打字的那一发：把它端到前台并要焦点。一扇 <c>WS_CHILD</c> 永远拿不到键盘焦点
    /// （契约那条，<c>.scratch/keeper-contract/spec.md</c>，<c>## User Stories</c>，抬起编辑态存在的全部理由），所以这一发必须在
    /// <see cref="Lift"/> 之后，不能拿它代替那一步。
    ///
    /// 两条动词都要，理由是一条实测结果：2026-09-26 只叫 <c>SetForegroundWindow</c> 的那一发，
    /// 引擎报「输入框有焦点=True」，而 Win32 报前台仍是 Chrome —— 非前台进程叫不动它，
    /// 而引擎内那份焦点不等于 OS 的键盘焦点，字进不来。<c>SwitchToThisWindow</c>
    /// 是给「用户动作换来的切换」用的，非前台进程也叫得动。返回那一行是给日志读的。</summary>
    public static string GrabForTyping(nint hwnd)
    {
        // after 传 0 就是 HWND_TOP：只在「顶层那一叠」里排到最前，不像 HWND_TOPMOST 那样
        // 从此压着一切。日历抬起打字要的是前者。
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpShowWindow);
        Win32.SwitchToThisWindow(hwnd, true);
        var set = Win32.SetForegroundWindow(hwnd);
        var foreground = Win32.GetForegroundWindow();
        return $"SetForegroundWindow={set} 前台=0x{foreground:X}/{Win32.ClassName(foreground)} " +
               $"前台是我们={foreground == hwnd}";
    }

    /// <summary>那一点上有没有挡着的东西。五档，不是两档 —— 2026-09-26 那两发实测结果合起来
    /// 才是鼠标桥的判据：归我们才喂，归盖住它的那个 app 窗就不喂。
    ///
    /// <see cref="WindowTier.Owner.WallpaperLayer"/> 单列一档，是因为「桌面扫干净之后那一点露到壁纸」和
    /// 「被别人的窗挡住」在屏幕上看着是一回事、在桥上完全是两回事：前者我们仍占着那块地方，
    /// 后者不是。判据写成 `hit == own || root == own` 也是这个来由 —— 挂进 DefView 之后
    /// `GetAncestor(GA_ROOT)` 报的是 `Progman`，只有 `hit` 那一个是我们的窗。
    ///
    /// <b>五档的档名、判据、中文说法三样都在 <see cref="WindowTier"/></b>
    /// （<c>src/Shared/WindowTier.cs</c>）—— 那一份是两个工程共用的源码，测量程序
    /// （<c>DebugProbe.csproj</c>）调的是同一个类型。这里留着这一发，是为了 <c>--check</c>
    /// 能拿假句柄把五档各拧一遍：真的 `WindowFromPoint` 在无头里没有实测结果。</summary>
    public static WindowTier.Owner TierOf(nint hit, nint root, string rootClass, nint own) =>
        WindowTier.TierOf(hit, root, rootClass, own, IsWallpaperLayer(rootClass));

    /// <summary>屏幕上那一点的 owner-of 全量答案：分档、命中窗、根窗、根类名、
    /// 给人对着看的那一行实测结果。</summary>
    internal readonly record struct PointOwner(
        WindowTier.Owner Owner, nint Hit, nint Root, string RootClass, string Measurement);

    /// <summary><b>全仓唯一一处 owner-of 实施</b>（卡片 04 · S20）：
    /// <c>WindowFromPoint</c> + <c>GetAncestorRoot</c> + 分档只在这一发做。
    /// <see cref="OwnerOf"/> 和 <c>Fader.OwnerOf</c> 都转调它；
    /// <c>Probe</c> / <c>Shots</c> / <c>FadeRun</c> 里其余直接调 <c>WindowFromPoint</c> 的
    /// 地方是各自的测量句式，不是 owner-of 的重复实现，不动。
    ///
    /// <para>实测结果里为什么把 `hit` 和 `root` 都印出来：2026-09-26 那一发就是靠
    /// 「命中窗是我们的、根却是 Progman」这一对分清「归我这扇窗」和「归壁纸层」的 ——
    /// 只印一个数，那两档看着是同一行。</para></summary>
    internal static PointOwner OwnerAt(nint own, Win32.Point at)
    {
        var hit = Win32.WindowFromPoint(at);
        var root = hit == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestorRoot(hit);
        var rootClass = Win32.ClassName(root);
        var tier = TierOf(hit, root, rootClass, own);
        return new PointOwner(tier, hit, root, rootClass,
            $"点 ({at.X},{at.Y}) 归 0x{hit:X}/{Win32.ClassName(hit)}，" +
            $"根是 0x{root:X}/{rootClass} → {Name(tier)}");
    }

    /// <summary>屏幕上那一点归谁，连带一行给人对着看的实测结果。
    /// <b>转调 <see cref="OwnerAt"/>，交出其中两样</b> —— 现有调用方零改动。</summary>
    public static (WindowTier.Owner Owner, string Measurement) OwnerOf(nint own, Win32.Point at)
    {
        var o = OwnerAt(own, at);
        return (o.Owner, o.Measurement);
    }

    /// 实测结果里那一档的中文说法。判据用枚举，日志用这一句，两边共用一个来源。
    /// 那一句现在住在 <see cref="WindowTier.Name"/> —— 两个工程共用的那一份源码里。
    public static string Name(WindowTier.Owner owner) => WindowTier.Name(owner);

    /// 一块矩形的中心，屏幕坐标。桥拿它问「这一点归谁」，格子拿它排备注那行。
    public static Win32.Point Center(Win32.Rect rect) =>
        new() { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 };

    // ---- 扫桌面那三发：名单、快照、对账 ------------------------------------------
    //
    // 这三份**只有一处实施**了（2026-09-29）。原来测量程序里有一份逐字相同的
    // （VisibleTopLevels / Reconcile / SweepPreflight），旁边写着「两边各留一份是有意的：
    // 测量程序要能脱离产品复现它自己那批实测结果」。那条理由把**判据独立**和**取证动作独立**
    // 混成了一件：判据该独立（先印名单、不骑帧的 15 秒兜底、Interlocked 只还一次、
    // Sleep 900+1500 之后才对账 —— 那些规矩写在正本里也照样是正本自己的规矩，测量程序调用它
    // 一条都不丢），取证动作不该再造一份。而这几条里两条握着用户的桌面，
    // 两份实施就是两份还原路径。删除测试也正着问过：把那三份删掉，复杂度不会在调用方
    // 长回来 —— 正本就在同一个进程里，而且测量程序的 :786 那一条还原对账一直在调它。

    /// 整张桌面的可见顶层窗，连它们当下最没最小化。扫之前拍一张，还原之后拿它对着看
    /// （<see cref="Reconcile"/>）——「还原那一发动词返回了 true」和「他的桌面回到原样」
    /// 不是同一件事。只列 <c>IsWindowVisible</c> 的那些：最小化的窗 <c>WS_VISIBLE</c>
    /// 那位还留着，所以照样在表里。
    /// （卡片 04 · S18 改名：它只是一份「可见顶层窗的清单」，和桌面层查找的
    /// <see cref="Snapshot"/> 不是一回事，别混称「快照」。）
    public static List<(nint Hwnd, string Name, bool Iconic)> VisibleTopLevelWindows()
    {
        var mine = (uint)System.Environment.ProcessId;
        var list = new List<(nint, string, bool)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == mine || !Win32.IsWindowVisible(hwnd)) return true;
            var cls = Win32.ClassName(hwnd);
            if (IsShellDesktopWindow(cls)) return true;
            var title = Win32.WindowText(hwnd);
            list.Add((hwnd, $"'{(title.Length == 0 ? "无题" : title)}'/{cls}", Win32.IsIconic(hwnd)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// 扫之前开着、还原之后还最小化着的那几扇 —— 就是这一发动词欠下的账。
    /// 调用方得先给 shell 留把窗收回来的时间（<c>UndoMinimizeALL</c> 是异步的），
    /// 不然读出来的全是「拍早了」：2026-09-26 第一跑就是这么报了 2 扇没回来，
    /// 一分钟后去看那两扇早就在了。
    public static string Reconcile(List<(nint Hwnd, string Name, bool Iconic)> before)
    {
        var now = VisibleTopLevelWindows();
        var byHandle = new Dictionary<nint, bool>();
        foreach (var w in now) byHandle[w.Hwnd] = w.Iconic;
        var left = before.Where(w => !w.Iconic && byHandle.TryGetValue(w.Hwnd, out var iconic) && iconic)
                         .Select(w => w.Name).ToList();
        var gone = before.Where(w => !byHandle.ContainsKey(w.Hwnd)).Select(w => w.Name).ToList();
        var opened = before.Count(w => !w.Iconic);
        return $"扫之前 {opened} 扇是开着的，还原之后仍最小化 {left.Count} 扇" +
               (left.Count == 0 ? "" : $"：{string.Join("、", left.Take(12))}") +
               (gone.Count == 0 ? "" : $"；另有 {gone.Count} 扇从可见列表里没了：{string.Join("、", gone.Take(6))}");
    }

    /// 压在这块矩形上、会被扫的窗。**这一份只列压着的**：那一发扫的是整张桌面，
    /// 名单比实际影响范围小，别把它读成「只会碰到这几扇」。
    public static string OverRect(Win32.Rect own)
    {
        var mine = (uint)System.Environment.ProcessId;
        var list = new List<string>();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == mine || !Win32.IsWindowVisible(hwnd)) return true;
            var cls = Win32.ClassName(hwnd);
            if (IsShellDesktopWindow(cls)) return true;
            if (!Win32.GetWindowRect(hwnd, out var r)) return true;
            if (!(r.Left < own.Right && own.Left < r.Right && r.Top < own.Bottom && own.Top < r.Bottom))
                return true;
            var title = Win32.WindowText(hwnd);
            list.Add($"'{(title.Length == 0 ? "无题" : title)}'/{cls} pid={pid}");
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) return "一个都没有（这块地方本来就露天到壁纸层）";
        var shown = list.Take(12).ToList();
        return string.Join("、", shown) + (list.Count > shown.Count ? $" …另加 {list.Count - shown.Count} 扇" : "");
    }

    // ---- 鼠标桥那三问（2026-10-12）--------------------------------------------
    //
    // 日历那一扇带 WS_EX_TRANSPARENT 之后，「那一点归谁」这个问法整个塌了：
    // 鼠标穿过去落到图标层，于是无论那一点上有没有图标，命中测试答的都是「图标层」。
    // 而这恰好是「图标透过来 + 点得穿」能成立的同一个原因 —— 不塌就说明没穿透。
    // 所以判据改成三问，每一问都答一件不同的事。

    /// <summary>图标层那一扇（<c>DefView</c> 的 <c>SysListView32</c> 子窗）。取不到就是 0，
    /// 调用方把 0 当「这一台机器上没有图标层」，别当异常。
    ///
    /// 窗口名 <c>"FolderView"</c> 是量到的，不是猜的（2026-09-26 那几发一直在用）。</summary>
    public static nint IconLayer() =>
        Win32.FindWindowEx(Look().DefView, IntPtr.Zero, IconViewClass, "FolderView");

    /// <summary>那一点在不在我们这一扇窗的矩形里。<b>不查有没有画着东西</b> ——
    /// 格子之间那些空隙也在矩形里，而空隙那一块同样该收点击（双击空隙 = 取消编辑，
    /// 见 <c>Calendar.CommitAndSetDown</c>）。
    ///
    /// 这一问顶掉了原来那个「<c>WindowFromPoint</c> 答的是我们」——
    /// 那一问在 <c>WS_EX_TRANSPARENT</c> 下恒为假，闸门就永远关着。</summary>
    public static bool OverCells(Win32.Rect own, Win32.Point at) =>
        at.X >= own.Left && at.X < own.Right && at.Y >= own.Top && at.Y < own.Bottom;

    /// <summary>那一点上有没有一个桌面图标。
    ///
    /// <b>这一问是「图标能随意拖放」这条要求的关键</b>：鼠标已经穿过去了，图标层<b>本来</b>
    /// 就收得到按下、松开和拖动。桥要是不问这一句就再喂日历一发，同一次点击就有两处响应 ——
    /// 最难看的是双击图标去打开它，顺手把日历抬起来打字。
    ///
    /// <para><b>读法是 UI Automation（<see cref="DesktopIcons"/>）</b>，不是 <c>LVM_HITTEST</c>：
    /// 那一问本机恒答 0（「非负」恒为真），闸门于是恒闭、日历格子一个也点不动。
    /// 换掉读法之后这一闸门自然修好。读数在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c>「图标位置读法选型（产品侧）」。
    /// <c>Win32.IconHit</c>（原来那一份）留在 <c>src/Win32.cs</c> 里，它是
    /// <c>--icon-columns</c> 那一发量的对象，也是反向验时换回去的那一份。</para>
    ///
    /// <para><b>从没读到过好的一份时当「那一点上有图标」</b>，也就是闸门关着。
    /// 宁可不吃这一次点击，也不要抢双击图标去打开它
    /// （契约 <c>.scratch/keeper-contract/spec.md</c>「## Solution」那一条）。这一档原来答的是
    /// 反过来（读不出来当「没有图标」、照常喂），而它和「日历不许抢双击图标」直接冲突
    /// （工单 27 根因 4）；被否掉的取舍见研究文档那一节。</para>
    ///
    /// <para><b>这一问在钩子那一刻答，结果跟着那一发排队</b>（<see cref="MouseBridge.OnMouseHook"/>），
    /// 不在抽干那一刻重问：按下和松开之间左键从按着变成松开，两问的答案正相反着。</para></summary>
    public static bool OnIcon(Win32.Point at) => DesktopIcons.OnIcon(at);

    // ---- 自检：桌面层那一份快照（卡片 04 · S21）---------------------------------
    //
    // 三个问句都是纯函数，判据造假快照就能判死「两问的筛选条件被并成一问」
    // 和「Progman 为零时那三条退路走错」。`Read` 本身不进自检 —— 它碰真桌面，
    // 由真机那一发（run.cmd find）覆盖。

    /// <summary>这一组在自检里登记的那两句话（绿话/红话）。判据是 <see cref="SelfTest"/>。</summary>
    internal static readonly CriterionOf Criterion = new(
        () => "桌面层那一份快照那一族通过：壁纸层那一问、桌面那一扇那一问、日历宿主那一问都只对同一份快照作答；" +
              "壁纸层那一问不筛可见、不筛满屏，宿主那一问按「类名 + 可见 + 满屏」筛，两问的筛选条件本来就不同；" +
              "两扇满屏 DefView 取第一扇、第二扇底下的图标列表不算；Progman 为零时壁纸层那一问走顶层退路、" +
              "宿主那一问一律答空；没有图标列表也照旧交出宿主、图标层答 0x0",
        "桌面层那一份快照", SelfTest);

    /// <summary>造一扇快照里的后代窗。</summary>
    private static DesktopWindow Kid(nint handle, string cls, nint parent, bool visible, Win32.Rect rect) =>
        new(handle, cls, visible, rect, parent);

    /// <summary>造一扇快照里的顶层窗（按 z 序，排先的在前）。</summary>
    private static TopLevelWindow Top(nint handle, string cls, nint defViewChild = 0) =>
        new(handle, cls, defViewChild);

    private static Snapshot Snap(nint progman, DesktopWindow[] kids, TopLevelWindow[] tops) =>
        new(progman, kids, tops, progman != IntPtr.Zero, true);

    public static List<string> SelfTest()
    {
        var bad = new List<string>();
        var full = new Win32.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var small = new Win32.Rect { Left = 40, Top = 40, Right = 840, Bottom = 540 };
        const long progman = 0x100, defView = 0x200, iconList = 0x300, workerW = 0x400;

        // 1. Progman + 直接子窗 DefView + IconList：三问各答各的。
        {
            var s = Snap((nint)progman,
                [Kid((nint)defView, DefViewClass, (nint)progman, true, full),
                 Kid((nint)iconList, IconViewClass, (nint)defView, true, full)],
                [Top((nint)progman, ProgmanClass), Top(0x900, "App")]);
            var layer = WallpaperLayerOf(s);
            if (layer.DefView != (nint)defView || layer.WorkerW != IntPtr.Zero)
                bad.Add("一：壁纸层那一问没答直接挂在 Progman 底下的 DefView，或 WorkerW 不该非零却非零");
            var host = CalendarHostOf(s);
            if (host.DefView != (nint)defView || host.IconList != (nint)iconList)
                bad.Add($"一：宿主那一问没答出 DefView + IconList（DefView=0x{host.DefView:X} 图标层=0x{host.IconList:X}）");
            if (host.Measurement !=
                $"Progman=0x{progman:X} DefView=0x{defView:X} 图标层=0x{iconList:X} 拆桌面=已发")
                bad.Add($"一：成功那一行的日志文字变了：「{host.Measurement}」");
            if (DesktopTopOf(s) != (nint)progman)
                bad.Add("一：Progman 非零时桌面那一扇不该是别的");
        }

        // 2. DefView 不可见：壁纸层那一问仍答它（不筛可见）；宿主那一问答空。
        {
            var s = Snap((nint)progman,
                [Kid((nint)defView, DefViewClass, (nint)progman, false, full)],
                [Top((nint)progman, ProgmanClass)]);
            if (WallpaperLayerOf(s).DefView != (nint)defView)
                bad.Add("二：壁纸层那一问跟着筛起可见来了 —— 两问的筛选条件本来就不同");
            var host = CalendarHostOf(s);
            if (host.DefView != IntPtr.Zero || !host.Measurement.Contains("没有一扇「SHELLDLL_DefView + 可见 + 满屏」", StringComparison.Ordinal))
                bad.Add($"二：不可见的 DefView 被宿主那一问收下了，或理由没答「没有一扇可见 + 满屏」（「{host.Measurement}」）");
        }

        // 3. DefView 不够满屏：壁纸层那一问仍答它；宿主那一问答空。
        {
            var s = Snap((nint)progman,
                [Kid((nint)defView, DefViewClass, (nint)progman, true, small)],
                [Top((nint)progman, ProgmanClass)]);
            if (WallpaperLayerOf(s).DefView != (nint)defView)
                bad.Add("三：壁纸层那一问跟着筛起满屏来了 —— 两问的筛选条件本来就不同");
            if (CalendarHostOf(s).DefView != IntPtr.Zero)
                bad.Add("三：不够满屏的 DefView 被宿主那一问收下了");
        }

        // 4. 两扇满屏 DefView：取第一扇；第二扇底下的图标列表不算。
        {
            var s = Snap((nint)progman,
                [Kid((nint)defView, DefViewClass, (nint)progman, true, full),
                 Kid(0x201, DefViewClass, (nint)progman, true, full),
                 Kid(0x301, IconViewClass, 0x201, true, full)],
                [Top((nint)progman, ProgmanClass)]);
            var host = CalendarHostOf(s);
            if (host.DefView != (nint)defView || host.IconList != IntPtr.Zero)
                bad.Add($"四：没取第一扇 DefView，或把第二扇底下的图标列表算进来了" +
                        $"（DefView=0x{host.DefView:X} 图标层=0x{host.IconList:X}）");
        }

        // 5. Progman 为零、顶层 WorkerW 带 DefView 孩子：壁纸层答 WorkerW；宿主答空；桌面那一扇答 WorkerW。
        {
            var s = Snap(IntPtr.Zero,
                [Kid(0x2FF, DefViewClass, (nint)workerW, true, full)],
                [Top(0x900, "App"), Top((nint)workerW, WorkerWClass, 0x2FF)]);
            var layer = WallpaperLayerOf(s);
            if (layer.WorkerW != (nint)workerW || layer.Parent != (nint)workerW)
                bad.Add("五：托管 DefView 的 WorkerW 那条退路没走对（没答 WorkerW，或 Parent 不是它）");
            var host = CalendarHostOf(s);
            if (host.DefView != IntPtr.Zero || host.Measurement != "Progman 读不出来")
                bad.Add($"五：Progman 为零时宿主那一问没短接成「Progman 读不出来」（「{host.Measurement}」）");
            if (DesktopTopOf(s) != (nint)workerW)
                bad.Add("五：Progman 为零时桌面那一扇该取顶层 z 序里第一扇壁纸层（WorkerW）");
        }

        // 6. Progman 为零、顶层直接有一扇 DefView：壁纸层答它；桌面那一扇答它；宿主答空。
        {
            var s = Snap(IntPtr.Zero,
                [],
                [Top(0x500, DefViewClass)]);
            if (WallpaperLayerOf(s).DefView != (nint)0x500)
                bad.Add("六：Progman 为零时壁纸层那一问没从顶层里认出 DefView");
            if (DesktopTopOf(s) != (nint)0x500)
                bad.Add("六：Progman 为零时桌面那一扇该取顶层里第一扇壁纸层（DefView）");
            if (CalendarHostOf(s).DefView != IntPtr.Zero)
                bad.Add("六：Progman 为零时宿主那一问照样得答空");
        }

        // 7. 没有图标列表：宿主仍答 DefView，图标层=0x0。
        {
            var s = Snap((nint)progman,
                [Kid((nint)defView, DefViewClass, (nint)progman, true, full)],
                [Top((nint)progman, ProgmanClass)]);
            var host = CalendarHostOf(s);
            if (host.DefView != (nint)defView || host.IconList != IntPtr.Zero ||
                !host.Measurement.Contains("图标层=0x0", StringComparison.Ordinal))
                bad.Add($"七：没有图标列表时没照旧交出宿主（DefView=0x{host.DefView:X} " +
                        $"图标层=0x{host.IconList:X} 「{host.Measurement}」）");
        }

        return bad;
    }
}
