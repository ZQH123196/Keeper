using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;

namespace Keeper;

/// <summary>桌面图标现在占哪几块矩形、那一点上有没有图标。<b>产品侧那一问走 UI Automation</b>
/// （工单 26，所有者 2026-10-14 定夺）。日历按图标挖洞那一族 2026-10-14 撤了，
/// 这一族现在只服务鼠标桥闸门第三问。
///
/// <para><b>为什么换读法</b>：原来那一问走 <c>LVM_HITTEST</c>，判「返回值非负 = 那一点上
/// 有图标」。本机整块图标层按 50 像素取样、<c>wParam</c> 七档各问 1508 点，
/// <b>返回值一律 0</b> —— 压在图标身上的点也一样。也就是那一问恒为真，
/// 鼠标桥那道闸门恒闭，日历格子一个也点不动。读数在
/// <c>docs/research/desktop-layer-and-window-measurements.md</c>
/// 「图标位置读法选型（产品侧）」那一节。被否掉的那几族（无障碍 <c>accLocation</c> /
/// <c>accNavigate</c>、<c>IDispatch</c> 晚绑定、带指针的 <c>LVM_GETITEMRECT</c>）
/// 也都在那一节里。</para>
///
/// <para><b>为什么是 UI Automation</b>：一条问句问完一整层，句子里没有任何跨进程指针，
/// 不需要 <c>ReadProcessMemory</c>。读法抄的是量具那一侧
/// <c>src/tools/DebugProbe/Icons.cs</c>（那一条是工单 07 选出来的，本机量通过）。
/// 它要的那一份在 <c>Microsoft.WindowsDesktop.App</c> 这个共用框架里，
/// 那是系统自带的一份程序集、不是 NuGet 包，所以「不引任何包」这条没被破；
/// 代价是产品工程的 TFM 从 <c>net8.0</c> 变成 <c>net8.0-windows</c> + <c>UseWPF</c>
/// （见 <c>Keeper.csproj</c> 和 ADR-0003 那一节）。</para>
///
/// <para><b>为什么读不在钩子里做</b>：鼠标桥第三问由 <c>MouseBridge.OnMouseHook</c> 每一发问一次
/// （跑在全机输入的同步路径上），而这一问是跨进程 COM，一次几毫秒到几十毫秒 ——
/// 在那儿做一次就把桌面搞卡（用户报的第二个症状就是卡）。所以这里
/// <b>读在一根后台线程上做（<see cref="RequestRefresh"/>），问的那一侧只拿上一次那份快照算</b>：
/// 每发那一下是几个矩形比较，见 <see cref="OnIcon"/>。</para>
///
/// <para><b>一读失败不许覆盖上一份好的</b>（工单 27 拆点 1）：这一问会**瞬时答空**
/// （跨进程 COM 抛异常、Explorer 正在忙）。<see cref="Keep"/> 就是那一道：
/// 抛异常、答 0 颗、答得比上一份少，三样都沿用上一份好的，只在 <see cref="Answer.Note"/>
/// 里写清这一读发生了什么。<b>从没有过好的一份</b>才轮到「答不出图标位置」那一档
/// （那一档 <see cref="OnIcon"/> 当「那一点上有图标」，闸门关着）。</para>
///
/// <para><b>闸门那第三问</b>：日历那一扇带 <c>WS_EX_TRANSPARENT</c>，鼠标穿过去落到
/// 图标层；桥要是不分「那一点上有没有图标」就再喂日历一发，同一次点击就有两处响应
/// —— 双击图标去打开它，顺手把日历抬起来打字。<b>宁可不吃这一次点击，也不抢双击图标</b>。
/// 它在钩子那一刻答、按下和松开配成对（<see cref="MouseBridge"/>）。</para>
///
/// <para><b>日历不再按图标挖洞</b>（所有者 2026-10-14 定夺）：原先这一族还喂给「让位」
/// 那一族 —— 让开图标占的那几列、按住那一段冻结读数、重画只跟让位段走。方向翻了，
/// 那一族撤干净了，这一族只留闸门要的那一份快照。
/// <para><b><see cref="Keep"/> 归闸门第三问</b>（工单 28 验收最后一行，2026-10-15 定）：
/// 让位那一族撤干净之后，<b>它是这一族唯一的消费方</b>，而它得留下有的是实事 ——
/// 读失败就把上一份好的扔掉的话，第三问处处答「那一点上有图标」，闸门处处关着，
/// <b>日历格子一个也点不动</b>。它原来的理由（读失败不许让日历退成整块铺回来）
/// 随让位那一族一起作废了，那一条不再钉着它。</para></summary>
internal static class DesktopIcons
{
    /// <summary>两次读之间隔多久（毫秒）。
    ///
    /// 2026-10-14 本机实测：一次读 **60 到 70 毫秒**（首发 165，那一发含 JIT 和装程序集）。
    /// 每帧做一次就是把桌面搞卡，<b>每秒做一次也还是每秒一次 70 毫秒的跨进程 COM</b>。
    /// 图标位置只在人拖图标 / 换桌面时才变，两秒的滞后谁也看不出来，而这一读不在帧里、
    /// 在后台线程上（<see cref="RequestRefresh"/>），所以这一档定成两秒。</summary>
    internal const int RefreshMs = 2000;

    private static readonly object Gate = new();
    private static Answer _answer = Answer.Unanswered;
    private static volatile bool _reading;
    private static long _lastReadMs;

    /// <summary>UI Automation 要的那几份程序集<b>不在进程的程序集列表里</b>，
    /// 所以要用的时候得自己装（<see cref="EnsureAssemblies"/>）。装不上就答「读不出来」——
    /// 闸门那第三问处处当「有图标」，日历格子一个也点不动。</summary>
    private static readonly object LoadGate = new();
    private static bool _triedLoad;
    private static bool _loaded;
    private static string? _folder;
    private static string _loadNote = "还没找过";

    /// <summary>装 UI Automation 要的那几份程序集。
    ///
    /// <para><b>为什么自己装</b>（量于 2026-10-14）：引擎起 C# 那一个运行时读的是
    /// <c>GodotPlugins.runtimeconfig.json</c>，那一份只列 <c>Microsoft.NETCore.App</c>；
    /// 项目自己那份 <c>Keeper.runtimeconfig.json</c> 就算列上了 <c>Microsoft.WindowsDesktop.App</c>，
    /// 进程的程序集列表也是启动那一刻定的。所以 <c>&lt;UseWPF&gt;</c> 那一样只够<b>编译</b>过，
    /// 一到运行期就 <c>FileNotFoundException: UIAutomationClient</c>。
    /// 那一发就是 <c>--icon-columns</c>（本文件顶上那条取向的实现）。</para>
    ///
    /// <para><b>从共用框架那一目录按路径装</b>：版本跟着本进程正在跑的那一份走
    /// （<c>System.Private.CoreLib</c> 旁边就是 <c>8.0.x</c>），不是写死一个版本号。</para></summary>
    private static bool EnsureAssemblies()
    {
        lock (LoadGate)
        {
            if (_triedLoad) return _loaded;
            _triedLoad = true;
            try
            {
                // 版本跟着本进程正在跑的那一份走（System.Private.CoreLib 旁边就是 8.0.x），
                // 不是写死一个版本号。
                var core = Path.GetDirectoryName(typeof(object).Assembly.Location)
                           ?? throw new FileNotFoundException("System.Private.CoreLib 的位置读不到");
                // ...\shared\Microsoft.NETCore.App\8.0.x\  →  ...\shared\Microsoft.WindowsDesktop.App\8.0.x\
                _folder = Path.GetFullPath(
                    Path.Combine(core, "..", "..", "Microsoft.WindowsDesktop.App", new DirectoryInfo(core).Name));

                // **Resolver 才是真解法，下面那三行主动装只是让「装没装上」当场答得出来。**
                // 目录里那些程序集互相引用的版本号和本进程要的**对不上**
                // （2026-10-14 实测：`UIAutomationClient` 8.0.0.0 要 `UIAutomationProvider` **10.0.0.0**，
                // 目录里那一份是 8.0.0.0）—— 默认绑定器只肯用更高的版本去满足更低的要，
                // 反过来算降级、直接拒。`Resolving` 事件的返回值运行时照单全收。
                AssemblyLoadContext.Default.Resolving += ResolveFromDesktopFramework;

                foreach (var name in new[] { "WindowsBase", "UIAutomationTypes", "UIAutomationClient" })
                {
                    var path = Path.Combine(_folder, name + ".dll");
                    if (File.Exists(path)) AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                }

                // 对照那一发：主动装完，再照编译期那份身份问一次，答得出来才算装上了。
                var wanted = new AssemblyName(
                    "UIAutomationClient, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                _loaded = AssemblyLoadContext.Default.LoadFromAssemblyName(wanted) != null;
                _loadNote = _loaded
                    ? $"UI Automation 那几份从 {_folder} 装上了（版本号对不上时由 Resolving 那一条兜住）"
                    : $"UI Automation 装不上：{_folder} 里没有可用的那一份";
            }
            catch (Exception ex)
            {
                _loadNote = $"UI Automation 装不上 —— {ex.GetType().Name}：{ex.Message}";
            }

            return _loaded;
        }
    }

    /// <summary>进程里缺哪一份，就去共用框架那一目录按简单名找一份给他。
    /// 返回的文件版本和要的那个对不上也照收 —— 那正是要解的那一件（见上面那条）。</summary>
    private static Assembly? ResolveFromDesktopFramework(AssemblyLoadContext context, AssemblyName name)
    {
        if (_folder == null || name.Name == null) return null;
        var path = Path.Combine(_folder, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    /// <summary>上一次读到的图标位置。<b>帧里只读这一份</b>，不触发读。</summary>
    internal static Answer Current => _answer;

    /// <summary>要一份新的。已经有一发在跑、或者上一次读完还不到
    /// <see cref="RefreshMs"/>，就什么都不做 —— <b>不排队</b>，排队等于把读拉成一串，
    /// 而那一串会一路堆到桌面卡住。</summary>
    internal static void RequestRefresh()
    {
        if (_reading) return;
        if (Environment.TickCount64 - Volatile.Read(ref _lastReadMs) < RefreshMs) return;
        lock (Gate)
        {
            if (_reading) return;
            _reading = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                // **答得少、答 0 颗、抛异常都不覆盖上一份好的**（工单 27 拆点 1）。
                _answer = Keep(_answer, ReadNow());
            }
            catch (Exception ex)
            {
                // 读图标这一族不许把产品带崩（抛出来的是跨进程 COM 的异常）。
                _answer = Keep(_answer, Answer.Unanswered with
                {
                    Note = $"这一读抛异常了 —— {ex.GetType().Name}：{ex.Message}",
                    Failed = true,
                });
            }
            finally
            {
                Volatile.Write(ref _lastReadMs, Environment.TickCount64);
                _reading = false;
            }
        }, null);
    }

    /// <summary>这一读要不要覆盖上一份好的。<b>纯算术</b>（所以判据能造几份读数喂进去），
    /// 而且<b>保住的是位置，不是答案</b>：沿用下来的那一份 <see cref="Answer.Icons"/>
    /// 原样留着，<see cref="Answer.Note"/> 改写成「沿用上一份好的（几颗）—— 这一读怎么了」。
    ///
    /// <para>三档：<list type="bullet">
    /// <item><b>从没有过好的一份</b> → 照收这一读。此时 <see cref="Answer.Answered"/>
    /// 为假，<see cref="OnIcon"/> 当「那一点上有图标」，闸门关着。</item>
    /// <item><b>这一读抛了异常 / 答 0 颗 / 答得比上一份少</b> → 沿用上一份好的。
    /// 「少」也沿用是因为读一半的名单会让闸门在那几颗图标之外多答一段。</item>
    /// <item>其余 → 收这一读。</item>
    /// </list>
    ///
    /// <para><b>它归闸门第三问</b>（工单 28 验收最后一行，2026-10-15 定）：让位那一族撤干净之后
    /// 它是这一族唯一的消费方，而它得留下有的是实事 —— 读图标失败就把上一份好的扔掉的话，
    /// 第三问处处答「有图标」，闸门处处关着，日历格子一个也点不动。
    /// 它原来的理由（不退成整块铺回来）随让位那一族一起作废了，那一条不再钉着它。</para></summary>
    internal static Answer Keep(Answer previous, Answer fresh)
    {
        if (!previous.Answered) return fresh;
        if (fresh.Answered && fresh.Icons.Count >= previous.Icons.Count) return fresh;
        var why = fresh.Failed ? "抛异常了"
            : fresh.Icons.Count == 0 ? "答 0 颗"
            : $"只答出 {fresh.Icons.Count} 颗（上一份 {previous.Icons.Count} 颗）";
        return previous with
        {
            Note = $"沿用上一份好的（{previous.Icons.Count} 颗）—— 这一读{why}：{fresh.Note}" +
                   $"（{fresh.Ms:F0} 毫秒）",
            Kept = true,
        };
    }

    /// <summary>那一点上有没有一个桌面图标。<b>纯算术</b>：几个矩形比较，不发消息、不碰 COM，
    /// 所以这一问在钩子那条同步路径上也放得下（实测读数在研究文档那一节）。
    ///
    /// <b>从没读到过好的一份时答 true</b>，也就是「那一点上有图标」：
    /// 宁可不吃这一次点击，也不抢双击图标去打开它（契约「## Solution」那一条）。
    /// 这一档原来答的是反过来，于是快照一空就把拖图标当成日历的点击（工单 27 根因 4）。
    ///
    /// <para><b>这一问在钩子那一刻答</b>（<see cref="MouseBridge.OnMouseHook"/>），结果跟着那一发排队，
    /// 抽干那一刻不再问第二遍 —— 问一次和答一次必须是同一时刻的状态。</para></summary>
    internal static bool OnIcon(Win32.Point at) => OnIconOf(_answer, at);

    /// <summary><see cref="OnIcon"/> 的算术本体。<b>纯算术</b>（不读静态那一份快照），
    /// 所以判据能拿一份假读数问它。</summary>
    internal static bool OnIconOf(Answer answer, Win32.Point at)
    {
        if (!answer.Answered) return true;
        foreach (var icon in answer.Icons)
        {
            if (icon.Rect.Left <= at.X && at.X < icon.Rect.Right &&
                icon.Rect.Top <= at.Y && at.Y < icon.Rect.Bottom) return true;
        }

        return false;
    }

    /// <summary>读一次图标位置。<b>这一发跑在后台线程上</b>（<see cref="RequestRefresh"/>），
    /// 所以它只做纯读取：认桌面那一扇、往下问一遍后代、筛 <c>ListItem</c>、取它的矩形。
    ///
    /// <para>为什么走「从根往下」而不是「从句柄往下」：桌面图标在 UIA 树上的位置<b>不是</b>
    /// 图标层的子节点 —— 那一扇只答得出一个 <c>Pane</c>。这条抄的是
    /// <c>src/tools/DebugProbe/Icons.cs</c> 的 <c>ReadFromRoot</c>。</para>
    ///
    /// <para><b>答得出来</b>的条件只有一件：这一读答出了至少一颗图标
    /// （<b>不是</b>「图标层那一扇窗在不在」—— 那是另一件事，把它当判据的话
    /// 一读抛异常就答「答出来了」，而手里是一份可能读了一半的名单）。
    /// 没答出来这一份就答「答不出图标位置」，闸门那第三问处处当「有图标」。</para></summary>
    private static Answer ReadNow()
    {
        var watch = Stopwatch.StartNew();

        // 那一族程序集不在进程里，先装。装不上就把这一句摆进读数，
        // 让人一眼看出「图标没读出来」不是「桌面上没有图标」。
        //
        // **这一发本身一个字都不许提 UI Automation 的类型**：编译器把那一族当类型记进这一个方法，
        // JIT 就得在进方法门那一刻把那些类型解析出来 —— 那一刻程序集还没装，
        // 于是抛的是 `FileNotFoundException`，`try` 根本还没进去（2026-10-14 撞的就是这一下）。
        if (!EnsureAssemblies())
        {
            watch.Stop();
            return new Answer(false, [], _loadNote, watch.Elapsed.TotalMilliseconds, true);
        }

        var (boxes, note, failed) = ReadByUia();
        watch.Stop();
        return new Answer(boxes.Count > 0, boxes, note, watch.Elapsed.TotalMilliseconds, failed);
    }

    /// <summary>UI Automation 那一问本体。**提 UIA 类型的只有这一个方法**，
    /// 而它只有在 <see cref="EnsureAssemblies"/> 跑过之后才被 JIT（见上面那条）。
    ///
    /// <b>抛异常时交出的是一份可能读了一半的名单</b>，所以第三档那个旗
    /// （<c>failed</c>）是给 <see cref="Keep"/> 看的：判据要能分得开
    /// 「这一读抛了什么」和「这一读答 0 颗」，而日志那句话写在 <see cref="Answer.Note"/> 里。</summary>
    private static (List<Icon> Icons, string Note, bool Failed) ReadByUia()
    {
        var boxes = new List<Icon>();
        try
        {
            var desktop = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Progman"));

            if (desktop == null) return (boxes, "根底下没有类名 Progman 的那一扇", false);

            var items = desktop.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement item in items)
            {
                var rect = item.Current.BoundingRectangle;
                // 空矩形是「这一条没落在屏幕上」，和「这一条落在原点」在数字上不是同一件事。
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                boxes.Add(new Icon(
                    item.Current.Name ?? string.Empty,
                    new Win32.Rect
                    {
                        Left = (int)Math.Round(rect.Left),
                        Top = (int)Math.Round(rect.Top),
                        Right = (int)Math.Round(rect.Right),
                        Bottom = (int)Math.Round(rect.Bottom),
                    }));
            }

            return (boxes, $"Progman 底下后代里的 ListItem {items.Count} 个", false);
        }
        catch (Exception ex)
        {
            // 异常那一句要带 HRESULT：跨进程读失败时 HRESULT 才是有信息量的那部分。
            return (boxes, $"抛异常了 —— {ex.GetType().Name}：{ex.Message}（HRESULT 0x{Marshal.GetHRForException(ex):X8}）", true);
        }
    }

    /// <summary>一颗图标。<b>名字只为读数认它用</b>（哪一颗被双击了、哪一颗压在哪一格底下），
    /// 位置那一半才是判据。</summary>
    internal readonly record struct Icon(string Name, Win32.Rect Rect);

    /// <summary>量测那一发（<c>--icon-columns</c>）用：<b>同步</b>读一次。
    ///
    /// 产品自己那一路读在后台线程上（<see cref="RequestRefresh"/>），所以帧里量不到它；
    /// 而那一发要量的正是「这一问本身花多久」，所以这里开一条同步的路。
    /// <b>它不发布</b>（不接 <see cref="Keep"/>）—— 量完不接管产品读到的那一份。</summary>
    internal static Answer ReadForMeasurement() => ReadNow();

    /// <summary>一份读数。<b>没读过就是 <see cref="Unanswered"/></b>（判据要能分得开
    /// 「还没读」「读了答不出来」「读了、桌面上就是没有图标」这三件）。
    ///
    /// <para><b><paramref name="Answered"/> 说的是「这一问答出了图标位置没有」</b>，
    /// 判据是答出的颗数 ≥ 1（<see cref="ReadNow"/>）。它<b>不是</b>「图标层那一扇窗在不在」——
    /// 拿后者当判据的话，一读抛异常也答「答出来了」，而那份名单可能只读到一半。</para></summary>
    internal readonly record struct Answer(
        bool Answered, List<Icon> Icons, string Note, double Ms,
        /// <summary>这一读抛异常了（区别于「答了 0 颗」—— 两者都答不出位置，来路不同）。</summary>
        bool Failed = false,
        /// <summary>这一份是<b>沿用上一份好的</b>：位置来自上一读，<see cref="Note"/> 说这一读怎么了。</summary>
        bool Kept = false)
    {
        /// <summary>还没读过。</summary>
        internal static Answer Unanswered => new(false, [], "还没读过", 0);

        /// <summary>读数那句话，原样进日志。<b>标签由调用方写</b> —— 各发要说清的上下文不一样。
        /// 沿用的那一份那一句自带颗数和用时，所以不再拼一遍。</summary>
        internal string Report() => Kept
            ? $"{Note}"
            : $"{Note}，{Icons.Count} 颗，用时 {Ms:F1} 毫秒" + (Answered ? "" : "（没答出图标位置）");
    }

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>闸门第三问和快照保活那一族的判据。<b>纯算术</b>：造几份读数喂进去，
    /// 问的是「读失败会不会把上一份好的扔掉」「那一点上有没有图标」。
    /// 不碰桌面、不发消息、不碰 COM —— 那一族每一问都拆成了纯函数就是为了这一发。</summary>
    public static List<string> GateSelfTest()
    {
        var checks = new Checks();

        // 假读数：三颗图标。
        var good = new Answer(true,
        [
            new Icon("左一", new Win32.Rect { Left = 1010, Top = 100, Right = 1100, Bottom = 200 }),
            new Icon("左二", new Win32.Rect { Left = 1050, Top = 220, Right = 1140, Bottom = 320 }),
            new Icon("右一", new Win32.Rect { Left = 1500, Top = 100, Right = 1590, Bottom = 200 }),
        ], "Progman 底下后代里的 ListItem 3 个", 62.0);
        var onOne = OnIconPoint(good);
        var blank = new Win32.Point { X = 1400, Y = 400 };    // 哪一颗图标都不在的那一点

        // ---- 一：读抛异常 / 答 0 颗 / 答得比上一份少，都不覆盖上一份好的 --------------
        checks.Check("快照保活", () =>
        {
            var threw = Keep(good, Answer.Unanswered with { Note = "抛异常了 —— InvalidOperationException", Failed = true });
            var empty = Keep(good, new Answer(false, [], "Progman 底下后代里的 ListItem 0 个", 70.0));
            var fewer = Keep(good, new Answer(true, [good.Icons[0]], "Progman 底下后代里的 ListItem 1 个", 70.0));

            foreach (var (what, kept) in new[] { ("抛异常了", threw), ("答 0 颗", empty), ("只答出一颗", fewer) })
            {
                if (kept.Icons.Count != good.Icons.Count)
                    checks.Fail($"{what}那一读把上一份好的覆盖了：手上只剩 {kept.Icons.Count} 颗，" +
                                "闸门第三问从这一读起就是按这一份答的");
                if (OnIconOf(kept, blank) || !OnIconOf(kept, onOne))
                    checks.Fail($"{what}那一读之后闸门第三问答错了 —— 坏读数不许接管上一份好的");
                if (!kept.Kept)
                    checks.Fail($"{what}那一读没有标出「沿用上一份好的」，日志里分不出它是哪一种");
            }

            // 三件事要分得开（读日志的人靠这句话）：沿用了什么、这一读怎么了、什么时候。
            if (!threw.Note.Contains("沿用上一份好的") || !threw.Note.Contains("抛异常了"))
                checks.Fail($"沿用那一读没写清「沿用了什么 + 这一读抛了什么」，读出来「{threw.Note}」");
            if (!empty.Note.Contains("答 0 颗"))
                checks.Fail($"沿用那一读没写清「这一读答 0 颗」，读出来「{empty.Note}」");

            // 从没有过好的一份时照收这一读 —— 收不住就是首读成功却答「没读到过」。
            if (Keep(Answer.Unanswered, good).Icons.Count != good.Icons.Count)
                checks.Fail("从没有过好的一份时照收这一读都收不住 —— 首读成功却答成了没读到过");

            // 反向验（必须当次就红）：不接 Keep、直接拿那份坏读数去问闸门，处处答「有图标」。
            // 拦住「这条判据量的是别的事」的就是这一行。
            var naive = Answer.Unanswered with { Note = "空读数" };
            if (!OnIconOf(naive, blank) || !OnIconOf(naive, onOne))
                checks.Fail("一份空读数直接拿去问闸门，第三问却答「那一点上没有图标」 —— " +
                            "这条判据量错了东西（正常路径必须先过 Keep）");
        });

        // ---- 二：闸门第三问：那一点上没有图标才喂 ------------------------------------
        checks.Check("闸门第三问", () =>
        {
            // 对照（一定该通）：读出来了、那一点上没图标 —— 闸门必须开。
            if (OnIconOf(good, blank) || !MouseBridge.ShouldFeed(true, true, OnIconOf(good, blank)))
                checks.Fail("读出来了、那一点上也确实没有图标，闸门却闭着 —— 日历格子一个也点不动");

            // 反向那一半：点落在图标上，闸门必须闭着。
            if (!OnIconOf(good, onOne) || MouseBridge.ShouldFeed(true, true, true))
                checks.Fail("那一点确实压在图标上，闸门却开着 —— 双击图标去打开它会把日历抬起来");

            // 「读不可信」只剩一档了：**从没有读到过好的一份**。那一档闸门闭着
            // （宁可不吃这一次点击，也不抢双击图标）。
            if (!OnIconOf(Answer.Unanswered, blank))
                checks.Fail("从没有读到过好的一份时第三问却答「没有图标」 —— 闸门开着喂下去，" +
                            "双击图标去打开它时日历会被抬起来打字");
        });

        return checks.Report();
    }

    /// 那一点落不落在这一份里任何一颗图标上（<see cref="OnIconOf"/> 的算术本体，
    /// 判据要能拿假的读数问它，所以这里不碰静态那一份）。
    private static bool On(Answer answer, Win32.Point at) => OnIconOf(answer, at);

    /// 假读数里「压在某一颗图标上」的那一点。判据要问「闸门那两档答不答一样」，
    /// 而那一问只有落在图标上、落在图标外两档都问一遍才量得到那一问的两头。
    private static Win32.Point OnIconPoint(Answer answer) => new()
    {
        X = (answer.Icons[0].Rect.Left + answer.Icons[0].Rect.Right) / 2,
        Y = (answer.Icons[0].Rect.Top + answer.Icons[0].Rect.Bottom) / 2,
    };

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 27）。<b>名叫闸门不叫让位</b>：
    /// 让位那一族 2026-10-14 撤了，剩下的是鼠标桥闸门第三问和快照保活。</summary>
    internal static readonly CriterionOf IconGate = new(
        () => "图标闸门通过：抛异常、答 0 颗、只答出一颗都沿用上一份好的，" +
              "而反向验那一发（不接保活）空读数处处答「有图标」；" +
              "从没有过好的一份时第三问答「有图标」、闸门闭着；" +
              "读出来了、那一点上没有图标闸门开着、压在图标上闭着",
        "图标闸门", GateSelfTest);
}