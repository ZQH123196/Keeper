using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>实拍帧 + 注入那一族<b>测量程序</b> —— 跑在产品那个进程里（<c>App.tscn</c> 带旗），
/// 产品那一侧只剩一次调用。
///
/// <b>为什么搬出来</b>：这一族原来住在 <c>App</c> 里，和产品的组合口挤在同一个 class 上
/// —— 2318 行里约 1160 行是它。两边共享同一扇日历窗、同一份桌面快照、同一只真鼠标，
/// 于是读一个不懂另一个。搬完产品的组合口第一次能从头到尾读下来，而「这一行是产品还是
/// 测量程序」从文件清单上就答得出来。
///
/// <b>它碰得到什么，全写在 <see cref="Hooks"/> 那一份里</b>：产品递过来的每一个钩子都在那儿，
/// 名单之外的东西一个都摸不着 —— 这一族不许伸手进产品的私有字段，那和没搬一样。
///
/// <b>两个方向</b>：产品这一侧调 <see cref="From"/>（认旗、排程）、<see cref="Pump"/>（每帧）、
/// <see cref="Raised"/> / <see cref="StageEntered"/>（遮罩的几句话，问「你那一发要不要排」）、
/// <see cref="RestoreDesk"/>（退出前把扫下去的桌面还回去）。这一族自己不再回头找产品。
///
/// <b>旗的认法一个字没改</b>：<c>--shot</c> / <c>--options-shot</c> / <c>--fader-shot</c> /
/// <c>--about</c> / <c>--presets-shot</c> / <c>--tray-submenu-shot</c> / <c>--tray-menu</c> /
/// <c>--tap</c> / <c>--lift</c> / <c>--relayout</c> / <c>--double</c> / <c>--edit</c> /
/// <c>--sweep</c> / <c>--feed</c> / <c>--flows</c> / <c>--section</c> / <c>--search</c> /
/// <c>--domain</c> / <c>--name-prompt</c> / <c>--lifemode-menu</c> /
/// <c>--lifemode-loose</c> / <c>--lifemode-list-broken</c> / <c>--lifemode-takein</c> /
/// <c>--clicks</c> / <c>--esc</c> / <c>--jiggle</c> / <c>--routine</c>，每一个的用法和用处都写在
/// 搬过去的那一段注释里，一个字没删。
///
/// <b>一处摘掉又接回来的是 2026-10-07 的 <c>--domain</c>（域标签接回去了）</b>：
/// 2026-10-06 它跟着 <c>Margin/Stack/Nav/Domains</c> 那个 TabBar 一起删了 —— 2026-10-06 那一次
/// 删的是「不许有切换」，而所有者要的是「不许藏域标签」，读反了。2026-10-07 TabBar 接回来，
/// <c>--domain</c> 跟着接回来：抓某一格得先站到那一格上。
/// <b><c>--advanced</c> 不回来</b> ——「显示高级设置」那颗开关 2026-10-06 退休、
/// 2026-10-07 确认不回来。留着一个指向不存在控件的旗子，敲上去什么也不发生，比没有更费时间。
///
/// <b>那几行排程的相对次序也是照抄的</b>，因为其中一处带着一条规矩：生活模式必须在开窗
/// <b>之前</b>种好（名单是窗 <c>_Ready</c> 时读的）。见 <see cref="From"/> 里那一段。</summary>
internal sealed class Shots
{
    // ---- 产品递过来的钩子 --------------------------------------------------------
    //
    // 这一份是这一族和产品之间唯一的 seam。两边各自要什么，看得清清楚楚：
    // 拿得到的是「哪一扇窗、活的那一份设置、钩子数了几个、遮罩举起几扇」，做得到的是
    // 「开窗、按预览、把设置应用回去、退出」—— 而**产品自己的私有字段一个都不在这儿**。
    //
    // 「活的那一份设置」给的是对象本身而不是一份拷贝：这一族有几发要就地拧几格
    // （`--relayout` 拧两条比例、`--routine …` 拧出一个块并开 autoFade），拧的是内存里那一份、
    // 走的是产品自己的应用那条路，文档一个字节不动 —— 这是测量程序的形状，不是产品行为。
    internal sealed record Hooks(
        string DataDir,
        Func<SettingsStore.Read> Read,
        Settings Live,
        Activity Input,
        Scheduler Machine,
        Func<List<Fader>> Faders,
        Func<Options?> Options,
        Func<Calendar?> Calendar,
        Func<About?> About,
        Func<Tray?> Tray,
        Func<CalendarNotes> CalendarNotes,
        Func<string> CalendarPath,
        Func<bool> CalendarForced,
        Func<List<int>> Screens,
        Action<SettingsStore.Read> SettingsApplied,
        Action OpenOptions,
        Action OpenAbout,
        Action TogglePreview,
        Action Quit,
        // ---- 工单 01：测量程序伸手进产品的那九处，改由这一份递过去 ------------------
        //
        // 搬之前这一族直接调 `Calendar` / `Fader` 上那几条 `*FromProbe` / `*Measurement`。
        // 收进这一份之后 `Shots.cs` 一个 `Calendar.` / `Fader.` 都不写，产品成员的名字
        // 全部住在这张名单里 —— seam 还是**一道**，只是清单变长了。
        //
        // **名单不是「顺手加的」**（这条规矩：产品上多一个成员，先问这一族是不是
        // 真用得上）。逐处答过，这九处每一处都要产品的知识：哪一格是几号、输入框摆在
        // 哪儿、控件排版之后那个矩形多大、壁纸源图在哪儿 —— 换任何一处产品都不再认得
        // 「这一发要什么」，所以每一处都得产品自己答。
        //
        // **形状**：和上面那几行一样是「问一句」，不是「递一个产品对象进去」。上面
        // `Calendar()` 递的是整扇窗（这一族要窗的矩形、桥的数），这里递的是七件具体的事 ——
        // 每一件都是一个动作或一句话，产品答完就把实现收回去。
        Func<CalendarProbe?> CalendarProbe,
        Func<FaderProbe?> FaderProbe,
        // ---- 卡片 02：现实时间只从 App 一处进来 ------------------------------------
        //
        // 设置 App 那一侧的假钟覆盖（<c>App._wallNowOverride</c>）：null = 还原真钟。
        // 只有 `RaiseAtBoundary` 这一族用得上 —— 它举遮罩前要递一个「块开始那一刻」的假时刻，
        // 让遮罩上的块名和时钟与状态机看到同一分钟；抓完必须还原（S10）。
        Action<DateTime?> SetWallNow);

    /// <summary>日历那一侧，测量程序要它做的七件事（工单 01）。
    ///
    /// 为什么是六件而不是「递一扇 <c>Calendar</c> 进去」：<see cref="Hooks"/> 上面
    /// <c>Calendar()</c> 那一行已经递了整扇窗，而这一族伸手要用的是<b>产品知识</b> ——
    /// 「<see cref="Calendar.CellProbe"/> 这一格中心那一点归谁」这一问，只有拿着格子表和
    /// 那扇窗句柄才答得出来。产品答完这句话，实现就留在产品那边了。
    /// <para><b>2026-10-12 从七件减到六件</b>：<c>Alignment</c> 那一件撤了。
    /// 它量的是「我们画出来的那一点和壁纸源图上那一个像素是不是同一个颜色」——
    /// 而我们从 2026-10-12 起不再自己画壁纸（每像素透明回到 DWM 合成），
    /// 所以没有「我们自己画的那一份」可比了。这一族现在只留下
    /// <c>Shots.AlphaMeasurementAllThree</c>，它量的正是今天要的那件事：底下那片桌面透不透得过来。</para></summary>
    internal sealed record CalendarProbe(
        Func<DateOnly, (Win32.Point At, bool Mine, string Measurement)> CellProbe,
        Action BeginEdit,
        Func<DateOnly, bool> PushClick,
        Action EditorPlacement,
        Action<string> Type,
        Action SubmitEnter);

    /// <summary>遮罩那一侧，测量程序要它做的两件事（工单 01）：清单那一片、收鼠标的那一块。
    ///
    /// 同样不是「递一扇 <c>Fader</c> 进去」：这两句要摸 <c>_rows</c> 摆完版之后的矩形、
    /// 要 <c>_swallow</c> 那一个数的内部 —— 都是产品这一侧的私有状态。</summary>
    internal sealed record FaderProbe(
        Func<string> Checklist,
        Func<string> Clicks);

    private readonly Hooks _hooks;

    private Shots(Hooks hooks) => _hooks = hooks;

    /// <summary>产品启动那一趟调这一次：认旗、按原来的次序把该排的都排上。
    ///
    /// <b>次序不是排版</b>，有两处带理由：
    /// 一、生活模式必须在开窗<b>之前</b>种好 —— 名单是窗 <c>_Ready</c> 时读的，种晚了
    ///   窗头那颗钮上写的是「还没有生活模式」；
    /// 二、<c>--lifemode-list-broken</c> 那一份名单也必须在开窗之前坏（同一句话）。
    /// 搬之前这两句在 <c>App._Ready</c> 里，搬过来还是这个次序。
    ///
    /// 一处次序变了、且只影响日志：<c>App._Ready</c> 那句「钩子装上了：…；藏起 N 个窗」从
    /// 「日历那几发排完之后、遮罩那一发排之前」挪到了<b>所有排程之后</b>。它是启动小结，
    /// 屏幕上和文件里的先后无所谓。</summary>
    public static Shots From(string[] args, Hooks hooks)
    {
        var shots = new Shots(hooks);
        var jiggle = Index(args, "--jiggle");

        // 这一族只认自己排程要用的那几个旗。`--data` / `--streak` / `--seconds` 归产品那一边
        // （前者是它读文档的目录，后两者改的是状态机），这一族要目录时问 `Hooks.DataDir`。
        if (jiggle >= 0 && jiggle + 1 < args.Length)
            shots._jiggleUntilMs = long.Parse(args[jiggle + 1]) * 1000;
        shots._calendarForced = Index(args, "--calendar") >= 0;
        shots._calendarTap = Index(args, "--tap") >= 0;
        shots._iconTap = Index(args, "--icon-tap") >= 0;
        shots._calendarSweep = Index(args, "--sweep") >= 0;
        shots._clickArmed = Index(args, "--clicks") >= 0;
        shots._escArmed = Index(args, "--esc") >= 0;
        if (Index(args, "--feed") >= 0)
            Log("calendar", "测量程序开关：--feed 开着，把桥拧回喂的那一侧（复测「一次点击两路事件」那一发）");
        if (shots._calendarSweep)
            Log("calendar", "测量程序开关：--sweep 开着，打那两发之前先把整张桌面扫一次，跑完还原并对账");
        if (shots._calendarForced)
            Log("calendar", "测量程序开关：--calendar 开着，文档说关着也开一扇，并且每 5 秒报一次桥的数");
        if (shots._calendarTap)
            Log("calendar", "测量程序开关：--tap 开着，2.5 秒后往今天那一格打两发真的点击（先问归不归我们）");

        // ---- 日历那几发 ------------------------------------------------------------
        if (shots._calendarTap) shots.ScheduleCalendarTap();
        if (shots._iconTap) shots.ScheduleIconTap();
        if (Index(args, "--lift") >= 0) shots.ScheduleCalendarLift();
        if (Index(args, "--relayout") >= 0) shots.ScheduleCalendarRelayout();
        if (Index(args, "--double") >= 0) shots.ScheduleCalendarDouble();
        if (Index(args, "--edit") >= 0) shots.ScheduleCalendarEdit(
            Index(args, "--edit") + 1 < args.Length ? args[Index(args, "--edit") + 1] : "keeper-calendar-edit.png");
        if (Index(args, "--shot") >= 0) shots.ScheduleCalendarShot(
            Index(args, "--shot") + 1 < args.Length ? args[Index(args, "--shot") + 1] : "keeper-calendar-shot.png");

        // ---- 遮罩那一发 ------------------------------------------------------------
        if (Index(args, "--fader-shot") >= 0) shots.ScheduleFaderShot(
            Index(args, "--fader-shot") + 1 < args.Length ? args[Index(args, "--fader-shot") + 1] : "keeper-fader-shot.png",
            // `--routine`：这一发量的是**块到点那一次**抬起来的那扇遮罩（工单 03）。
            // 它和「进入休息」那一发是同一条抓帧的路，只是举起来的那一次换成了到点 ——
            // 走的是触发本身（递两帧现实时间），不是直接调 RaiseBlock。
            Index(args, "--routine") >= 0);

        // ---- 关于窗口那一发 --------------------------------------------------------
        if (Index(args, "--about") >= 0) shots.ScheduleAboutShot(
            Index(args, "--about") + 1 < args.Length ? args[Index(args, "--about") + 1] : "keeper-about-shot.png");

        // ---- 托盘那两发 -----------------------------------------------------------
        var trayMenuAt = Index(args, "--tray-menu");
        if (trayMenuAt >= 0 && trayMenuAt + 1 < args.Length) shots.ScheduleTrayMenu(args[trayMenuAt + 1]);
        // 托盘子菜单的实拍帧（工单 04）。**这一发是那一张收工的一半**：自检量的是接线，
        // 「那张子菜单在屏幕上真的能张开」只有像素答得了（契约 194 行写着这一张
        // 不能只靠自检收工）。它配 `--lifemode-menu` 用（先把几个生活模式种出来，否则上半是空的）。
        var trayShotAt = Index(args, "--tray-submenu-shot");
        if (trayShotAt >= 0) shots.ScheduleTraySubmenuShot(
            trayShotAt + 1 < args.Length ? args[trayShotAt + 1] : "keeper-tray-submenu.png",
            // 第二个参数是要张开哪一个子菜单的名字（2026-10-12 加）。给了就认它，
            // 没给还是「生活模式」—— 旧命令一个字不用改。
            trayShotAt + 2 < args.Length ? args[trayShotAt + 2] : "生活模式");

        // ---- 设置窗那一族 ---------------------------------------------------------
        if (Index(args, "--flows") >= 0) shots.RevealFlowsAndStay();
        // 生活模式那一族的测量程序旗子：**在设置窗开起来之前**往临时目录里种几个生活模式 ——
        // 名单是窗 _Ready 时读的，种晚了那颗钮上写的是「还没有生活模式」。
        // 抓帧那一发配 --options-shot 用（种在 --data 指的临时目录里）。
        // `--tray-submenu-shot` 也用这几个：托盘那份名单是**菜单弹开那一瞬间现问**的，
        // 而上面那一发只把时刻排到 1.5 秒之后、种生活模式是紧接着同步跑的 —— 所以名单赶得上。
        var modeMenuAt = Index(args, "--lifemode-menu");
        var modeLooseAt = Index(args, "--lifemode-loose");
        var modeListAt = Index(args, "--lifemode-list-broken");
        var modeTakeInAt = Index(args, "--lifemode-takein");
        if (modeMenuAt >= 0 || modeLooseAt >= 0 || modeListAt >= 0 || trayShotAt >= 0 || modeTakeInAt >= 0)
            shots.SeedLifeModes(several: modeMenuAt >= 0 || trayShotAt >= 0);
        // 「名单读不通」那一档：种完之后**把名单截断**（那一份是这一发唯一要坏的东西，
        // 生活模式那几份原封不动），于是窗头那颗钮写「生活模式名单读不通」、状态栏那一句也在 ——
        // 抓的是「那一句到底长什么样、摆在哪一行」。**它也必须在开窗之前坏**（同上面那一条）。
        if (modeListAt >= 0) shots.BreakLifeModeList();
        // 「从一份文件收一个生活模式进来」那一档（工单 05）。种几个生活模式**并**在同一个目录里
        // 写一份「别人给的那一份」—— 它是生活模式那一份的格式，所以收进来的那一档抓的是
        // 「问名字的那一扇」长什么样、摆在哪一行，而那颗确定钮上写的是「收进来」不是「确定」。
        // 文件框那半步（真鼠标点那个框）测量程序替掉：它是 OS 那一层，同「只有真菜单才走得到
        // 的那半步」那一族，所以这一发量的是**问名字那一档**。
        if (modeTakeInAt >= 0) shots.SeedTakeIn();
        if (Index(args, "--options-shot") >= 0) shots.OptionsShot(
            Index(args, "--options-shot") + 1 < args.Length ? args[Index(args, "--options-shot") + 1] : "keeper-options-shot.png",
            modeMenuAt >= 0, modeLooseAt >= 0,
            modeTakeInAt >= 0 ? shots.TakeInSample : null);
        // 预设生活模式的画廊那一帧（工单 05）。亮色 / 暗色各一帧靠 `--data` 指的那一份临时
        // 设置里的 `uiTheme`（同一个键、同一条接线）。抓的是**画廊自己**那一块矩形。
        var presetsShotAt = Index(args, "--presets-shot");
        if (presetsShotAt >= 0) shots.SchedulePresetsShot(
            presetsShotAt + 1 < args.Length ? args[presetsShotAt + 1] : "keeper-presets-shot.png");
        return shots;
    }

    // ---- 产品每帧调一次 ----------------------------------------------------------
    //
    // 三件都在这一帧：排程的钟（`Chase` 和 `StepAfter` 排的那些步）、自己发鼠标移动、
    // `--calendar` 那一发每 5 秒报一次桥的数。三件都是测量程序，产品那一帧什么都不多做。

    public void Pump(double delta)
    {
        Jiggle();
        RunSteps();
        ReportCalendar(delta);
    }

    /// 遮罩刚抬起来。`--esc` 那一发要一道<b>对照</b>：淡入途中这一步还不收鼠标，
    /// Esc 交进去该被**忽略**。不配这一发，「收尾过=1」那个数证明不了闸门在起作用
    /// —— 它也可能是什么步都收尾。
    public void Raised(bool interactive)
    {
        // `--clicks` 在这里也排一次（2026-10-10 补）：原来它只在 `StageEntered` 里排，
        // 而**只有一个步的休息流程根本没有换步** —— 默认那份 `todoList` 就是一个步，
        // 于是那一发从来没排上过（2026-10-10 真跑量到的：`进 step` 一行都没有，
        // 而遮罩那一侧读数是 `收没收=True`，东西明明在）。
        // 「排一次就收旗子」在两处都一样，所以排到一处就够了。
        if (_clickArmed && interactive) ArmClicks("举起那一步就收鼠标");
        if (_escArmed && !interactive && _hooks.Faders().Count > 0)
            StepAfter(500, () => Log("esc",
                $"第一步还不收鼠标，先交一发 Esc 当对照：{Win32.PressEscape()}"));
    }

    private void ArmClicks(string when)
    {
        _clickArmed = false;
        Log("click", $"{when}：排上那两发");
        ScheduleClicks();
    }

    /// 遮罩进步了。`--clicks` 和 `--esc` 那两发在这一步才排得上（收鼠标的那一步），
    /// 排一次就把旗子收掉 —— 免得下一步又排一遍。
    public void StageEntered(bool interactive)
    {
        if (_clickArmed && interactive) ArmClicks("换步之后收鼠标");
        if (_escArmed && interactive && _hooks.Faders().Count > 0)
        {
            _escArmed = false;
            ScheduleEsc(_hooks.Faders()[0]);
        }
    }

    /// 测量程序：把 <c>DataDir</c> 里那份<b>名单</b>截断，制造「名单读不通」那一档。
    /// 只坏这一份 —— 生活模式那几份原封不动，所以那一帧量的是「名单坏了」而不是「生活模式族空了」。
    /// 坏之前先把它逐字节读下来留着，好在这一跑收场时对账（测量程序自己不留垃圾）。</summary>
    private void BreakLifeModeList()
    {
        var list = LifeModeStore.ListPathOf(_hooks.DataDir);
        if (!File.Exists(list))
        {
            Log("lifemodes", $"测量程序：{list} 还不存在（没种成生活模式？），这一发量不到「名单读不通」");
            return;
        }
        File.WriteAllText(list, "{ \"entries\": [ { \"name\": \"半截\", ");
        Log("lifemodes", $"测量程序：把名单截断了（{list}）—— 这一帧里钮上写「生活模式名单读不通」，" +
                     "生活模式那几份一个字节都没动");
    }

    /// 测量程序：在 <c>DataDir</c>（<c>--data</c> 指的临时目录）里种几个生活模式。
    /// 存的是<b>活的那一份</b>，所以种完之后窗头那颗钮写「生活模式：X」——
    /// 「不在任何生活模式里」那一档是 <c>--lifemode-loose</c> 那一发要的，它种完再拧一格
    /// （<see cref="Options.LoosenLifeModeOneValue"/>）。种不了就在日志里说，不假装。</summary>
    private void SeedLifeModes(bool several)
    {
        Directory.CreateDirectory(_hooks.DataDir);
        foreach (var name in several ? new[] { "写作", "😀 画画 🎨", "长文" } : new[] { "写作" })
        {
            var saved = LifeModeStore.SaveAs(_hooks.DataDir, name, _hooks.Live);
            Log("lifemodes", $"测量程序：种了一个生活模式「{name}」" +
                         (saved.Error == null ? $"（{saved.Taken?.File}）" : $"没成：{saved.Error}"));
        }
    }

    /// <summary>测量程序（工单 05）：种一个生活模式，再在同一个目录里放一份「别人给的那一份」，
    /// 路径记在 <see cref="TakeInSample"/> —— 抓帧那一发把它递给起名那扇小窗
    /// （<see cref="Options.AskLifeModeNameForTakeIn"/>），抓的是**问名字的那一档**。
    ///
    /// 那一份走的是<b>导出</b>（和生活模式那一份同一个渲染器、同一个格式），再塞进去一行注释和
    /// 一个 Keeper 没听过的键 —— 收进来的那一份带着它们，而这一帧之后收不收成、逐字节相同
    /// 那几件事是自检里的事，这一发只管「问名字的那一扇长什么样」。</summary>
    private void SeedTakeIn()
    {
        TakeInSample = Path.Combine(_hooks.DataDir, "from-somebody-else.jsonc");
        if (SettingsStore.Export(_hooks.DataDir, SettingsStore.Load(_hooks.DataDir), TakeInSample) != null)
        {
            Log("lifemodes", $"测量程序：把「别人给的那一份」导到 {TakeInSample} 没成，问名字那一档量不到");
            TakeInSample = null;
            return;
        }
        var text = File.ReadAllText(TakeInSample);
        var root = text.IndexOf('{');
        if (root >= 0)
            File.WriteAllText(TakeInSample, text.Insert(root + 1,
                "\n  // 别人那份上的注释\n  \"aKeyIKnowNothingAbout\": { \"keep\": true },"));
        Log("lifemodes", $"测量程序：放了一份「别人给的那一份」{TakeInSample}（{new FileInfo(TakeInSample).Length} 字节）");
    }

    /// 测量程序那一发问名字那一档时递进去的那个文件（<see cref="SeedTakeIn"/> 记的）。
    private string? TakeInSample { get; set; }

    /// <summary>测量程序那一发：开设置窗，等排版跑完几帧，把它那块矩形抓成文件，
    /// 顺带报一条<b>布局</b>实测结果 —— 有没有哪个控件伸出窗口外面。
    ///
    /// flows 那一区是新加的（一步一叠，横着一排勾选框），而这种形状只有像素能定：
    /// 中文那一行要是把整行撑宽，无头自检里一个字都读不出来 —— 设置窗那两发
    /// 「看着像裁剪 bug、其实是折行档选错」的坑踩过一次（实测结果在 CODING_STANDARDS.md 代码级规矩 autowrap
    /// 那一条）。
    /// 走的是 <see cref="Hooks.OpenOptions"/> 那同一条路，所以托盘点进来的形状和这一发是同一个。</summary>
    private void OptionsShot(string path, bool modeMenu, bool modeLoose, string? takeIn = null)
    {
        _hooks.OpenOptions();
        var opened = _hooks.Options();
        if (opened == null || !GodotObject.IsInstanceValid(opened))
        {
            Log("options", "设置窗没开起来，这一帧没得抓");
            _hooks.Quit();
            return;
        }
        var win = opened;
        // 测量程序的形状：这一发要滚到哪一张卡片、搜不搜词，由旗子给。
        // **2026-10-06 之后没有「切域」这一发了** —— 域切换和「显示高级设置」那颗开关一起
        // 退休了（<c>Options.BuildNav</c>），44 条叶子键在开窗时就全在场。所以
        // <c>--domain</c> 和 <c>--advanced</c> 这两个旗子也一起删了，不是留着当摆设。
        var args = OS.GetCmdlineUserArgs();
        var searchAt = Index(args, "--search");
        string search = searchAt >= 0 && searchAt + 1 < args.Length ? args[searchAt + 1] : "";
        // 起名那扇小窗的旗子：给了就把生活模式起名那扇小窗开起来。抓的是**设置窗那一块矩形**，
        // 而小窗摆在设置窗正中间（Options.AskLifeModeName 自己摆的位置），所以它落在那一块里，
        // 一张 PNG 同时拍到两者。用途和「控件的样子要当回事」那条规矩在 CODING_STANDARDS.md 第 9 条。
        var nameAt = Index(args, "--name-prompt");
        // 滚到哪一张卡片（分区节点名）。「中段那几张不滚就拍不到」对每一族都成立
        // （作息表那一张、勿扰那一张都在中段），而每一族各加一个旗子就是同一个动作抄几遍 ——
        // 所以这里一个旗子走 `Options.RevealSection`，和没给旗子时走的那条 `RevealFlows` 同款。
        var sectionAt = Index(args, "--section");
        var section = sectionAt >= 0 && sectionAt + 1 < args.Length ? args[sectionAt + 1] : "";
        // 切到第几个域（0 起）。**2026-10-07 恢复**：域标签接回来以后，抓某一格就得能先站到那一格上
        // —— 域切换不回来的时候这面旗子没有对象，域标签接回来它又有了。
        // 显式给了 `--domain` 就照给的来（域 0 也不例外），没给才走下面那条默认。
        var domainAt = Index(args, "--domain");
        var domain = domainAt >= 0 && domainAt + 1 < args.Length ? int.Parse(args[domainAt + 1]) : -1;
        Log("options", "测量程序那一发：1.5 秒后" +
                       (domain >= 0 ? $"切到第 {domain + 1} 个域" :
                        search.Length > 0 ? $"搜 '{search}'" :
                        section.Length > 0 ? $"滚到「{section}」那一张卡片" : "滚到 flows 那一区") +
                       (nameAt >= 0 ? "、开起名那扇小窗" : "") +
                       (modeMenu ? "、张开生活模式菜单那一圈" : "") +
                       (modeLoose ? "、拧一格让那颗钮写「不在任何生活模式里」" : "") +
                       (takeIn != null ? "、开「收一个生活模式进来」问名字那一档" : "") +
                       $" → 400 毫秒后把这扇窗举到前面 → 再 300 毫秒抓那一块矩形成 {path}，抓完就退");
        Chase(
            (1_500, "滚到那一张卡片", () =>
            {
                // 给了 --domain 就先站到那一格上（走 TabBar 自己的 current_tab，和真点出来同款）。
                if (domain >= 0) win.GoDomain(domain);
                // 没给 --section 时走原来那一发 —— 滚到休息流程那一区。给了就滚到它点名的那一张。
                if (section.Length > 0) win.RevealSection(section);
                else if (search.Length > 0) win.GoSearch(search);
                else win.RevealFlows();
                // 起名那扇小窗：走的是菜单那一条路（Options.AskLifeModeName），不是另开一个窗。
                if (nameAt >= 0) win.AskLifeModeName();
                // 「从一份文件收一个生活模式进来」问名字的那一档（工单 05）。走的是
                // Options.AskLifeModeNameForTakeIn —— 和真鼠标点完文件框之后那一条同一条路，
                // 只是「文件框交路径」那一步由测量程序递进去（那个框是 OS 那一层）。
                if (takeIn != null) win.AskLifeModeNameForTakeIn(takeIn);
            }),
            (400, "举到前面", () =>
            {
                // 和关于那一发同一个理由：这一扇窗是定时器开的，没人点过，前台锁让 IDE 那一扇
                // 压得住它 —— 第一发就是这么抓到别人的一份 markdown 文档（窗自己 可见=True，
                // 像素却不是它的）。测量程序取巧用 TOPMOST，托盘点开那一路不需要。
                var mine = Win32.OwnWindowNamed(win.Title);
                if (mine == IntPtr.Zero)
                {
                    Log("options", "举到前面：按标题认不出这扇窗，那一帧可能抓到别人的桌面");
                    return;
                }
                var ok = Win32.SetWindowPos(mine, Win32.HwndTopMost, 0, 0, 0, 0,
                    Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
                var centre = new Win32.Point
                {
                    X = win.Position.X + win.Size.X / 2,
                    Y = win.Position.Y + win.Size.Y / 2,
                };
                var hit = Win32.GetAncestorRoot(Win32.WindowFromPoint(centre));
                Log("options", $"举到前面：HWND=0x{mine.ToInt64():X} 交了={ok}（测量程序用 TOPMOST）" +
                               $"中心命中=0x{hit.ToInt64():X} '{Win32.WindowText(hit)}' 是我们自己={hit == mine}");
                // 设置窗那些**看得见的子窗**（起名那扇小窗就是一个）在**爹之后**再顶一次。
                // 顺序是这件事的本体：topmost 那一层里后顶的压在先顶的上面，所以先顶子窗的话
                // 它立刻被自己的爹压回去 —— 2026-10-01 第一次抓那一帧就是这么拍的：小窗
                // 「可见=True 句柄=3」，像素上却没有它，中心命中的还是爹。
                // 按自己的标题找，和爹走同一段路；这里不点名哪一族（测量程序不该知道界面上有哪几扇）。
                foreach (var sub in win.GetChildren().OfType<Window>().Where(w => w.Visible))
                {
                    var handle = Win32.OwnWindowNamed(sub.Title);
                    if (handle == IntPtr.Zero)
                    {
                        Log("options", $"举到前面：按标题认不出子窗 '{sub.Title}'，那一帧里它可能被爹压住");
                        continue;
                    }
                    var raised = Win32.SetWindowPos(handle, Win32.HwndTopMost, 0, 0, 0, 0,
                        Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
                    // 顺便把**屏上那个矩形**印出来：子窗摆在爹的哪个坐标系里、它到底盖没盖住
                    // 中心那一个点，量的是矩形而不是「可见=True」（那一发 2026-10-01 吃过一次：
                    // 可见=True、句柄=3、像素上却没有它）。
                    Win32.GetWindowRect(handle, out var where);
                    var now = Win32.GetAncestorRoot(Win32.WindowFromPoint(centre));
                    Log("options", $"举到前面：子窗 '{sub.Title}' HWND=0x{handle.ToInt64():X} 交了={raised} " +
                                   $"屏上矩形=({where.Left},{where.Top})-({where.Right},{where.Bottom}) " +
                                   $"爹的屏上矩形=({win.Position.X},{win.Position.Y})-({win.Position.X + win.Size.X},{win.Position.Y + win.Size.Y}) " +
                                   $"中心现在命中='{Win32.WindowText(now)}'");
                }
            }),
            (200, "张开生活模式菜单 / 拧一格", () =>
            {
                // 排在「举到前面」**之后**：弹层是屏上另外几扇窗，而 owned window 压在
                // 自己爹上面 —— 先张再举的话那一圈就落在设置窗底下去了。
                if (modeMenu) win.ShowLifeModeRing();
                if (modeLoose) win.LoosenLifeModeOneValue();
                // 设置窗刚才被顶成了 TOPMOST，而弹层是**后**建的那几扇普通窗 ——
                // 普通窗压在 topmost 下面，于是菜单只露出伸出设置窗右边的那一条
                // （那一帧 2026-10-01 量到：本体可见=True 屏上 (1570,399)，像素上只剩一条）。
                // 这是测量程序取巧的副作用不是产品行为（托盘点开那一路窗不是 topmost），
                // 所以这里把弹层也顶一次，顺序是「爹先顶、子后顶」。
                if (modeMenu) RaiseLifeModeRing(win.LifeModeRingWindows());
            }),
            (300, "抓那一帧", () =>
            {
                var rect = new Rect2I(win.Position.X, win.Position.Y, win.Size.X, win.Size.Y);
                // 菜单那一圈是屏上另外几扇窗（PopupMenu 自己就是 Window），落在设置窗那块
                // 矩形外面的时候那一块抓不到它 —— 矩形撑到装得下它们。量的是像素，
                // 不是「Visible=True」（那一族假象记在 CODING_STANDARDS.md 第 2 条）。
                foreach (var ring in win.LifeModeRingWindows())
                    rect = rect.Merge(new Rect2I(ring.Position.X, ring.Position.Y, ring.Size.X, ring.Size.Y));
                var image = DisplayServer.ScreenGetImageRect(rect);
                if (image == null || image.IsEmpty())
                {
                    Log("options", $"这一帧没读到东西（矩形={rect}），文件没写成");
                    _hooks.Quit();
                    return;
                }
                var err = image.SavePng(path);
                Log("options", $"那一帧：{path} 矩形={rect} 读回={image.GetWidth()}x{image.GetHeight()} 错误码={err}");
                Log("options", win.LayoutMeasurement());
                _hooks.Quit();
            }));
    }

    /// <summary>把菜单那一圈（屏上另外几扇 <c>PopupMenu</c>）举到设置窗之上。
    /// <b>按屏上那个矩形认，不按标题</b> —— 弹层的标题是引擎给的，<c>OwnWindowNamed</c>
    /// 那一族在这里认不出来。用「中心那一点命中谁」收尾：那是像素上的实测结果，
    /// 「<c>Visible=True</c>」不算（那一族假象记在 CODING_STANDARDS.md 第 2 条）。</summary>
    private static void RaiseLifeModeRing(List<Window> ring)
    {
        var ours = Win32.OwnTopLevelWindows();
        foreach (var w in ring)
        {
            int x = w.Position.X, y = w.Position.Y, right = x + w.Size.X, bottom = y + w.Size.Y;
            var handle = IntPtr.Zero;
            foreach (var hwnd in ours)
            {
                if (!Win32.IsWindowVisible(hwnd) || !Win32.GetWindowRect(hwnd, out var r)) continue;
                if (r.Left == x && r.Top == y && r.Right == right && r.Bottom == bottom) { handle = hwnd; break; }
            }
            if (handle == IntPtr.Zero)
            {
                Log("options", $"举弹层：屏上 {w.Position} 大小 {w.Size} 那个矩形在我们自己的顶层窗里没有对应的，" +
                               "它压在设置窗底下 —— 这一帧里那一圈缺一块\n" + Win32.DescribeOwnTopLevels());
                continue;
            }
            var ok = Win32.SetWindowPos(handle, Win32.HwndTopMost, 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
            var centre = new Win32.Point { X = x + w.Size.X / 2, Y = y + w.Size.Y / 2 };
            var hit = Win32.GetAncestorRoot(Win32.WindowFromPoint(centre));
            Log("options", $"举弹层：0x{handle.ToInt64():X} '{Win32.WindowText(handle)}' 屏上 {w.Position} 大小 {w.Size} " +
                           $"交了={ok} 中心命中=0x{hit.ToInt64():X} '{Win32.WindowText(hit)}' 是它={hit == handle}");
        }
    }

    /// <summary>测量程序开关：<c>--flows</c> 开设置窗、滚到休息流程那一区、举到前面，然后<b>不退</b>。
    /// 那一区在表单中段，不滚就看不见，而「那三颗要真鼠标点」欠的就是能看见的目标 ——
    /// 和 <c>--options-shot</c> 走同一条 <see cref="Options.RevealFlows"/> 与同一次 TOPMOST，
    /// 只是把抓帧那一步换成「留着，让人点」。收场走托盘的退出。</summary>
    private void RevealFlowsAndStay()
    {
        _hooks.OpenOptions();
        var opened = _hooks.Options();
        if (opened == null || !GodotObject.IsInstanceValid(opened))
        {
            Log("options", "设置窗没开起来，这一发没得点");
            return;
        }
        var win = opened;
        StepAfter(1_500, () =>
        {
            win.RevealFlows();
            StepAfter(400, () =>
            {
                var mine = Win32.OwnWindowNamed(win.Title);
                var ok = mine != IntPtr.Zero && Win32.SetWindowPos(mine, Win32.HwndTopMost, 0, 0, 0, 0,
                    Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
                Log("options", $"测量程序：休息流程那一区已经滚出来、窗举到前面（交了={ok}），" +
                               "这一发留着给真鼠标点那三颗 —— 收场点托盘的退出");
            });
        });
    }

    // ---- 预设生活模式的画廊那一发（工单 05 的实拍帧）---------------------------------
    //
    // `Chase`、TOPMOST 那一套、以及「按标题认自己的窗」那些理由都和
    // <see cref="ScheduleAboutShot"/> 同一族（那一发的注释写着为什么非要 TOPMOST），这里不重述。
    //
    // **抓的是画廊自己那一块矩形**（不是设置窗那一块）：这一张要回答的问题是「画廊那几列长得
    // 像不像一个东西」——每列的名字、那颗「收进来」是不是一个按钮、出处那一行有没有被裁掉
    // （CODING_STANDARDS.md 第 9 条：控件的样子和它的功能一样要当回事，而那一版功能全对、
    // 自检全绿而它是 flat 的按钮）。设置窗整扇拍下来会把那一列挤成十几个像素，看不出形状。
    //
    // **亮色 / 暗色各一帧**靠 `--data` 指的那一份临时设置里的 `uiTheme`：画廊的配色跟着设置窗
    // 同一个键（`UiTheme` 那一族），所以这是**同一条接线**上的两种取值，不是两套排版。
    private void SchedulePresetsShot(string path)
    {
        _hooks.OpenOptions();
        var opened = _hooks.Options();
        if (opened == null || !GodotObject.IsInstanceValid(opened))
        {
            Log("presets", "设置窗没开起来，画廊是它底下的子窗，这一帧没得抓");
            _hooks.Quit();
            return;
        }
        var win = opened;
        var theme = _hooks.Live.Map.Get("uiTheme");
        Log("presets", $"测量程序：1.5 秒后按生活模式管理器上那颗「预设生活模式…」把画廊开起来（配色 uiTheme={theme}）" +
                       $" → 300 毫秒后把它举到前面 → 200 毫秒后抓它自己那一块矩形成 {path}，抓完就退");
        void Lift()
        {
            // **爹先顶、子后顶**（设置窗那一发量到过：topmost 那层里后顶的压在先顶的上面，
            // 顺序反了子窗立刻被自己的爹压回去）。
            foreach (var one in new Window?[] { win, win.Gallery })
            {
                if (one == null || !one.Visible) continue;
                var hwnd = Win32.OwnWindowNamed(one.Title);
                if (hwnd == IntPtr.Zero)
                {
                    Log("presets", $"举到前面：按标题认不出 '{one.Title}' 那一扇，那一帧可能抓到别人的桌面");
                    continue;
                }
                var ok = Win32.SetWindowPos(hwnd, Win32.HwndTopMost, 0, 0, 0, 0,
                    Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
                var centre = new Vector2I(one.Position.X + one.Size.X / 2, one.Position.Y + one.Size.Y / 2);
                var hit = Win32.GetAncestorRoot(Win32.WindowFromPoint(new Win32.Point { X = centre.X, Y = centre.Y }));
                Log("presets", $"举到前面：'{one.Title}' HWND=0x{hwnd.ToInt64():X} 交了={ok} " +
                               $"大小={one.Size} 中心命中='{Win32.WindowText(hit)}'");
            }
        }
        Chase(
            (1_500, "开画廊", win.OpenPresetsGallery),
            (300, "举到前面", Lift),
            (200, "那一帧", () => GrabPresetsFrame(path)),
            (400, "退", _hooks.Quit));
    }

    private void GrabPresetsFrame(string path)
    {
        var gallery = _hooks.Options()?.Gallery;
        if (gallery == null || !GodotObject.IsInstanceValid(gallery))
        {
            Log("presets", "画廊没开起来（或已经被关掉），这一帧没得抓");
            return;
        }
        // 布局那一行实测结果：有没有哪个控件伸出窗外 —— 抓帧只管像素，而「像不像一个东西」要靠眼睛
        // （CODING_STANDARDS.md 第 9 条）。这里量的是**摆得下摆不下**那一半。
        // 比的是**窗内那个坐标系**：`GetGlobalRect` 对 `Window` 来说给的是画布坐标，不是屏上坐标
        // （第一版拿它和屏上位置比，于是每一个控件都报「伸到窗外」—— 量的是两套坐标系）。
        var inside = new Rect2(Vector2.Zero, gallery.Size);
        var escapes = new List<string>();
        foreach (var node in ShownIn(gallery))
        {
            if (node is not Control control) continue;
            var at = control.GetGlobalRect();
            // 自己比自己：`HasPoint` 不含右边缘，而 Page / Margin 铺满整扇窗 —— 用它会把那两颗报成"伸出去"。
            if (at.Position.X < inside.Position.X || at.Position.Y < inside.Position.Y ||
                at.End.X > inside.End.X || at.End.Y > inside.End.Y)
                escapes.Add($"{control.Name} 伸到 {at} 之外（窗内该在 {inside} 里）");
        }
        Log("presets", $"布局：伸出窗外 {escapes.Count} 个{(escapes.Count > 0 ? "：" + string.Join("；", escapes) : "")}" +
                       $"；列数={gallery.Rows.Count} 配色={gallery.UiThemeValue}");
        // 那一条分隔线**画没画**：抓帧上眼睛看不出 1 像素的东西，而「有没有那条线」是这一族的
        // 一个布局问题 —— 所以这里报它自己解析出来的那一支（色 + 有没有可见的宽度）。
        foreach (var rule in gallery.GetChildren().SelectMany(ShownIn).OfType<HSeparator>())
        {
            var bar = rule.GetThemeStylebox("separator");
            var line = bar?.Get("bg_color").AsColor() ?? Colors.Transparent;
            Log("presets", $"分隔线 {rule.GetPath()}：解析到的 separator={bar?.ResourceName ?? "（没有）"} " +
                           $"底色={line} 高={rule.Size.Y} 宽={rule.Size.X}（眼睛看不出 1 像素的东西，所以报实测结果）");
        }
        var rect = new Rect2I(gallery.Position, gallery.Size);
        var image = DisplayServer.ScreenGetImageRect(rect);
        if (image == null || image.IsEmpty())
        {
            Log("presets", $"这一帧没读到东西（矩形={rect}），文件没写成");
            return;
        }
        var err = image.SavePng(path);
        Log("presets", $"抓帧：{rect} {image.GetWidth()}×{image.GetHeight()} → {path} 错误码={err}");
    }

    /// <summary>测量程序那一发：把托盘菜单里的一行递回那扇窗的 WndProc，派发就落在它真的位置上。
    ///
    /// 加这一发的理由只有一条：「退出码 1 差在托盘那一路」那句说了两轮了，而那一发的关键不是
    /// 「谁按的」，是 <c>Quit()</c> 站在<b>托盘自己那扇窗的栈上</b>跑 —— <c>_tray.Dispose()</c> destroy
    /// 的正是脚下这一扇。这个栈位不在这儿复现，就只能写「等一只手」，而等手的东西不算欠账
    /// （AGENTS.md）。差的只有弹出那一层菜单，分发用的是同一个 <c>Dispatch</c>。</summary>
    private void ScheduleTrayMenu(string which)
    {
        Log("tray", $"排上测量程序那一发：递 '{which}' 进托盘的 WndProc，1.5 秒后动手");
        Chase((1_500, "递那一发", () =>
        {
            var posted = _hooks.Tray()?.ProbeMenu(which) ?? false;
            Log("tray", $"测量程序：递 '{which}' 结果={posted}（递完这一发就看退出码了）");
        }));
    }

    /// <summary>测量程序那一发：把托盘菜单真的弹出来，用真鼠标张开「生活模式」那一颗的子菜单，
    /// 把两层弹层抓成一帧，然后退。
    ///
    /// **这一发是工单 04 收工的一半** —— 自检（<c>flows-tray</c>）量的是接线：
    /// 子菜单挂上没有、勾打在哪一条、id 递回来落到哪一行。「那张子菜单在屏幕上真的
    /// 张得开、点得中」是<b>弹层</b>那一半，无头里没有屏幕，量不到（契约 194 行
    /// 明写这一张不能只靠自检收工）。
    ///
    /// 排程为什么只有一步：<see cref="Tray.ShootSubmenu"/> 自己管中间那几毫秒 ——
    /// <c>TrackPopupMenu</c> 弹着的时候主循不跑（<c>Chase</c> 是主线程的，
    /// 在这后面再排步就是排一串不执行的时刻），挪光标和抓帧都在它自己起的那条线程上。</summary>
    ///
    /// <paramref name="which"/> 是要张开哪一个子菜单的名字（2026-10-12 加）。托盘上有两个
    /// 带箭头的子菜单，原来这一发钉死在「生活模式」上，而**「休息历史」那一个的顺序要能拍出来**
    /// （所有者：「打开休息历史…」得放最上面）。两个子菜单形状不同，钉死一个就等于
    /// 另一个永远只有自检、没有实物帧。</summary>
    private void ScheduleTraySubmenuShot(string path, string which = "生活模式")
    {
        Log("tray", $"排上实拍帧那一发：1.5 秒后弹托盘菜单，真鼠标挪到「{which}」上把子菜单张开，抓 {path}");
        Chase((1_500, "弹菜单 / 张子菜单 / 抓帧", () => _hooks.Tray()?.ShootSubmenu(path, which, _hooks.Quit)));
    }

    /// 落在图标上的那一下（工单 26 的反向那一半）。
    ///
    /// **和 <c>--tap</c> 方向正好相反**：那一发打在今天那一格上、该喂进日历；
    /// 这一发打在图标正中间，**该只有图标响应**：桥那第三问答「那一点上有图标」→
    /// 闸门关 → 喂=0、丢=4、层还停在 Parked（日历不抬起来）。
    /// 双击那一发照 <see cref="ScheduleCalendarTap"/> 的形状（两下隔 120 毫秒），
    /// 因为要验的那一条写的就是「双击图标去打开它时不许顺手把日历抬起来」。
    ///
    /// <para>挑哪一颗：优先挑<b>落在日历这一块里</b>、而且名字不是本程序自己那一颗的 ——
    /// 双击 <c>Keeper.exe</c> 会起第二发出来，量出来的东西里就多了一个变量。
    /// 挑到哪一颗原样写进读数，不让人回头猜。</para></summary>
    private void ScheduleIconTap()
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar()))
        {
            Log("calendar", "--icon-tap：日历没开着，这一发取消");
            return;
        }

        // 挑图标这一步**排在 2.5 秒之后**：日历刚摆上时图标位置还没读到
        // （读是异步的，见 `DesktopIcons.RequestRefresh`），当场挑只会挑到「还没读过」。
        Chase((2_500, "挑图标 + 双击", () => IconTapRun()));
    }

    private void IconTapRun()
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar))
        {
            Log("calendar", "--icon-tap：日历没开着，这一发取消");
            return;
        }

        var icons = DesktopIcons.Current.Icons;
        if (icons.Count == 0)
        {
            Log("calendar", $"--icon-tap：图标位置没读到（{DesktopIcons.Current.Note}），这一发取消");
            return;
        }

        var own = calendar.ScreenRect;
        var inside = icons.Where(i => i.Rect.Left >= own.Left && i.Rect.Right <= own.Right &&
                                      i.Rect.Top >= own.Top && i.Rect.Bottom <= own.Bottom).ToList();
        // `Icon` 是结构体，`FirstOrDefault` 没挑到时那个 `Name` 是 null 而不是空串 ——
        // 判据一律走 IsNullOrEmpty（`IsKeeper` 那一句同理）。
        //
        // 挑选的次序：先挑**日历这一块里**的（那一发才量得到闸门第三问 —— 落在块外的话
        // 第二问就拒了，第三问根本没被问）。块里只有本程序自己那一颗时照样挑它
        // （用户报的就是压在日历底下那一颗），但把「会起第二发」写进读数。
        var pick = inside.FirstOrDefault(i => !IsKeeper(i.Name));
        if (string.IsNullOrEmpty(pick.Name)) pick = inside.FirstOrDefault();
        var caveat = "";
        if (string.IsNullOrEmpty(pick.Name))
        {
            pick = icons.FirstOrDefault(i => !IsKeeper(i.Name));
            caveat = "（日历这一块里没有图标，挑了块外那一颗：第二问就会拒，量不到第三问）";
        }

        if (string.IsNullOrEmpty(pick.Name))
        {
            Log("calendar", "--icon-tap：桌面上没有可打的那一颗图标，这一发取消");
            return;
        }

        if (IsKeeper(pick.Name)) caveat = "（挑到的是本程序自己那一颗：双击会起第二发，这一发要配 --anyway）";

        var at = new Win32.Point
        {
            X = (pick.Rect.Left + pick.Rect.Right) / 2,
            Y = (pick.Rect.Top + pick.Rect.Bottom) / 2,
        };
        var where = inside.Contains(pick) ? "在日历这一块里" : "在日历这一块之外（闸门第二问就会拒）";
        Log("calendar", $"打「{pick.Name}」正中间 ({at.X},{at.Y})（{pick.Rect}，{where}）{caveat}，" +
                        "双击两下各隔 120 毫秒，该只有图标响应、日历不抬起来");

        Chase(
            (200, "双击之前", () => HookCounters("双击之前")),
            (400, "第一下", () => Click(at, "第一下")),
            (150, "第二下（隔 120 毫秒，落在双击窗口内）", () => Click(at, "第二下")),
            (900, "双击之后", () =>
            {
                ReportCalendarNow("双击之后（该 喂=0、层还停在 Parked、控件收到一个没长）");
                PutCalendarDown("双击之后");
            }),
            (600, "还原", () => RestoreDesk("排程走到")));
    }

    /// 这一发挑图标那一颗是本程序自己（`Keeper.exe`）吗。是的话不双击它 ——
    /// 双击会起第二发，量出来的东西里就多了一个变量。</summary>
    private static bool IsKeeper(string? name) =>
        name is not null && name.Contains("Keeper", StringComparison.OrdinalIgnoreCase);

    /// 真点一下那一点。和 <see cref="TapCell"/> 一样走 <c>SendInput</c> 的绝对坐标，
    /// 所以它过得了我们自己那条低级钩子 —— 桥那一发就靠它量。
    private void Click(Win32.Point at, string what)
    {
        var sent = Win32.ClickAt(at);
        Log("calendar", $"{what}：目标=({at.X},{at.Y}) SendInput 交了 {sent}/3 发");
    }

    // ---- 测量程序：往日历的一格上打一发真的点击 ----------------------------------------
    //
    // 上一轮量的是命中测试（闸门该不该开），那一轮之前量的是「喂进去的那一型点不点得动」，
    // 两边都不是桥本身。这一发才是：一次真的 WM_LBUTTONUP（SendInput 绝对坐标，所以过我们
    // 自己那条低级钩子）落在嵌进壁纸的那扇窗上，看它跨不跨得过那条进程边界。
    //
    // 规矩和遮罩那一段 <see cref="Inject"/> 一样：先问「这一点归不归我们」，不归就取消。
    // 注入的那一发是真的，会落到当时在那一层的东西上。

    private bool _calendarTap;

    /// 落在图标上的那一下那一发（工单 26 的反向那一半）。同一段排程钟，见 <see cref="ScheduleIconTap"/>。
    private bool _iconTap;

    // 扫桌面那一路的状态。还原有两条路：排程里一条，加一条不在帧上的 15 秒兜底 ——
    // 中间那 900 毫秒主循是睡着的，不能拿「排程还走得到」当还原的前提。
    private bool _calendarSweep;
    private bool _calendarForced;
    private bool _swept;
    private int _restored;
    private List<(nint Hwnd, string Name, bool Iconic)>? _deskBefore;
    private System.Threading.Timer? _restoreDeadline;

    /// <summary>排上测量程序那一发。
    ///
    /// 每一步跑完才排下一步，间隔从「上一步真跑完」那一刻数。一次排到底的写法不行：
    /// 桌面扫那一下会把主循拖住一秒以上（shell 的动画加那 900 毫秒的等待），排好的时刻
    /// 全部变成「已经过期」，于是点击和它的实测结果在同一帧里连着跑 —— 而低级钩子是在我们自己
    /// 这条线程上回调的，那一刻它还没被叫到，读出来是「桥一发没喂」的假失败。
    /// 2026-09-26 第二跑就是这么废的：第一发和它的实测结果只差 1 毫秒。<see cref="ScheduleClicks"/>
    /// 那段注释里「每一下之后留 400 毫秒」立的正是这条规矩，我排成绝对时刻等于把它忘了。</summary>
    private void ScheduleCalendarTap()
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar())) return;
        Log("calendar", $"排上测量程序那一发：{(_calendarSweep ? "先扫桌面，" : "")}挪光标 + 三发（第一发该只换选中，后两发该抬起），2.5 秒后动手");
        // 第一发打在「今天减三天」那一格，第二发打在同一个位置：换选中这件事才读得出来
        // （打在已选中的今天上，选中前后是同一个日期，那行实测结果就是空的）。
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);
        Chase(
            (2_500, "扫桌面", () => { if (_calendarSweep) Sweep(); }),
            (700, "第一发之前", () => HookCounters("第一发之前")),
            (200, "挪光标", () => MoveCursorToCell(day, "挪光标")),
            (400, "第一发", () => TapCell(day, "第一发")),
            (700, "第一发之后", () => ReportCalendarNow("第一发之后（该只换选中，不该抬起）")),
            (150, "第二发", () => TapCell(day, "第二发")),
            (120, "第三发（和第二发隔 120 毫秒，落在双击窗口内）", () => TapCell(day, "第三发")),
            (800, "三发之后", () => ReportCalendarNow("三发之后（该抬起打字了）。" +
                "实测结果一律排在两发之后：上一跑把实测结果排在两发中间，两发就隔开了 941 毫秒，超过双击窗口")),
            (600, "放回", () => PutCalendarDown("双击之后")),
            (600, "还原", () => RestoreDesk("排程走到")),
            (1_800, "收尾", () => PutCalendarDown("测量程序收尾")),
            (800, "放回去之后", () => ReportCalendarNow("放回去之后（该回到那块像素）")));
    }

    /// 一条一步的链：上一步跑完才排下一步，所以间隔永远从「上一步真跑完」那一刻数起。
    private readonly Queue<(int DelayMs, string What, Action Run)> _chase = new();

    /// 树上那些**真的露着**的节点（广度优先）。**「在树上」不等于「画出来了」**，而
    /// <c>Visible</c> 是<b>逐节点</b>的：列表里那个 <c>Template</c> 自己 <c>visible = false</c>，
    /// 而它底下的 <c>Head</c> / <c>Name</c> / <c>Take</c> / <c>Note</c> 各自的 <c>Visible</c> 仍然是
    /// true —— 2026-10-02 第一版就是那样，于是实测结果里混进了「从没排过版」的那一支（宽 1 像素）。
    /// 所以这里带着祖先的可见性一起走（CODING_STANDARDS.md 第 2 条那一族）。
    private static IEnumerable<Node> ShownIn(Node root)
    {
        var queue = new Queue<(Node Node, bool Shown)>();
        foreach (var child in root.GetChildren())
            queue.Enqueue((child, child is not CanvasItem item || item.Visible));
        while (queue.Count > 0)
        {
            var (node, shown) = queue.Dequeue();
            if (shown) yield return node;
            foreach (var child in node.GetChildren())
                queue.Enqueue((child, shown && (child is not CanvasItem item || item.Visible)));
        }
    }

    private void Chase(params (int DelayMs, string What, Action Run)[] steps)
    {
        foreach (var step in steps) _chase.Enqueue(step);
        NextChase();
    }

    private void NextChase()
    {
        if (_chase.Count == 0) return;
        var (delay, _, run) = _chase.Dequeue();
        StepAfter(delay, () =>
        {
            run();
            NextChase();
        });
    }

    private readonly List<(long AtMs, Action Run)> _steps = [];

    /// 排一步「从现在起 n 毫秒之后」。
    private void StepAfter(double ms, Action run) => _steps.Add(((long)(_hooks.Input.NowMs + ms), run));

    private void RunSteps()
    {
        while (_steps.Count > 0 && _hooks.Input.NowMs >= _steps[0].AtMs)
        {
            var (_, run) = _steps[0];
            _steps.RemoveAt(0);
            run();
        }
    }

    /// 钩子那两个计数在打之前先读一遍：打完再读只看到「多了几发」，看不到**少掉的那一发按下**。
    /// 2026-09-26 那一跑两发点击各只喂进去一半，就是靠这一行前后对出来的。
    private void HookCounters(string when)
    {
        Log("calendar", $"{when}：钩子收到 按下={_hooks.Input.ButtonDownEvents} 松开={_hooks.Input.ButtonUpEvents}，" +
                        $"桥 {_hooks.Calendar()?.BridgeReport() ?? "没开着"}");
    }

    /// 带 <c>--sweep</c> 才动桌面。扫的是整张桌面、不止压在我们这块矩形上的那几扇，
    /// 所以名单先印出来再动手 —— 和测量程序里 <c>WallpaperHitTest</c> 那一段同一个规矩。
    /// 走完这一发之后桌面是最小化状态，后面每一步都指着 <see cref="RestoreDesk"/> 收尾；
    /// 那条 15 秒兜底是给「主循在这中间停了」那种意外留的。
    private void Sweep()
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return;
        Log("calendar", $"压在我们这块矩形上、会被扫的窗（那一发扫的是整张桌面，不止这些）：" +
                        $"{DesktopLayer.OverRect(calendar.ScreenRect)}");
        _deskBefore = DesktopLayer.VisibleTopLevelWindows();
        if (!Win32.ShowDesktop(false))
        {
            Log("calendar", "扫这一发没成（Shell.Application 那个动词没叫动），桌面没动过 —— 下面每发都不算数");
            return;
        }
        _swept = true;
        _restoreDeadline = new System.Threading.Timer(_ =>
        {
            if (System.Threading.Interlocked.Exchange(ref _restored, 1) == 0) Win32.ShowDesktop(true);
        }, null, 15_000, System.Threading.Timeout.Infinite);
        // 阻塞等 shell 扫完，而不是等帧：这会儿我们自己的主窗也可能被最小化了，
        // 拿自己的帧去等别人的动画，等的东西本身可疑。
        System.Threading.Thread.Sleep(900);
    }

    /// <summary>不点鼠标也能读的那一发：把窗抬出来、读焦点那一行、再放回去。
    ///
    /// 为什么另开一条：抬起动作本身要靠双击触发，而双击要 500 毫秒之内两发 ——
    /// <c>--tap</c> 那一发的排程差一点就踩到这个间隔，而且它得先让桌面露出来才打得到格子。
    /// 焦点和输入法那一条欠着的实测结果不该等这两件事凑齐。这一发不碰桌面、不碰别人的窗。</summary>
    private void ScheduleCalendarLift()
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar())) return;
        Log("calendar", "测量程序那一发：1.5 秒后直接抬起（不点鼠标），读焦点，再放回");
        Chase(
            (1_500, "抬起之前", () => HookCounters("抬起之前")),
            (300, "抬起", () => _hooks.CalendarProbe()?.BeginEdit()),
            (900, "抬起之后", () => ReportCalendarNow("抬起之后（该是 Editing，日志里该有一行焦点实测结果）")),
            (600, "放回", () => PutCalendarDown("测量程序抬起之后")),
            (900, "放回之后", () => ReportCalendarNow("放回之后（该回到那块像素）")));
    }

    /// <summary>测量程序那一发：不重启、不点鼠标，把活设置里的两条比例拧一档，走的是设置窗
    /// 「应用」那一条路（<see cref="Hooks.SettingsApplied"/> → 换那份设置 + 重新量一遍），
    /// 读的是屏幕上那块矩形跟不跟。
    ///
    /// 为什么用矩形而不是用一帧：这一发要答的是「改了就看到」，那是「窗有没有跟着挪」这一问，
    /// Win32 那块矩形就是答案；布局长成什么样是 <c>--shot</c> 那一条链的事（那条教训：
    /// 布局的实测结果在截图里）。而且这一发不扫桌面 —— 不动别人家的窗也能读到它要读的那一条。</summary>
    private void ScheduleCalendarRelayout()
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar())) return;
        var work = DisplayServer.ScreenGetUsableRect(0);
        Log("calendar", $"测量程序那一发：1.5 秒后把 widthPercent/heightPercent 拧到 45%/80%，" +
                        $"走设置窗那条「应用」，读矩形跟不跟（工作区 {work.Size}）");
        Chase(
            (1_500, "拧之前", () =>
            {
                var at = _hooks.Calendar()!.ScreenRect;
                Log("calendar", $"拧之前 矩形={at} 宽占 {at.Width * 100 / work.Size.X}% " +
                                $"高占 {at.Height * 100 / work.Size.Y}%");
            }),
            (300, "拧过之后", () =>
            {
                _hooks.Live.Calendar.WidthPercent = 45;
                _hooks.Live.Calendar.HeightPercent = 80;
                _hooks.SettingsApplied(_hooks.Read());
            }),
            (900, "应用之后", () =>
            {
                var at = _hooks.Calendar()!.ScreenRect;
                Log("calendar", $"应用之后 矩形={at} 宽占 {at.Width * 100 / work.Size.X}% " +
                                $"高占 {at.Height * 100 / work.Size.Y}%（该是 45% 和 80%）");
            }),
            (400, "收尾", _hooks.Quit));
    }

    /// <summary>测量程序那一发：把日历在屏幕上占的那块矩形抓成一帧，抓完就退。
    ///
    /// 为什么这一发要留在产品里，而不是拿一段外部脚本凑：上一轮 <c>--check</c> 十四发全绿，
    /// 屏幕上却是四条布局缺陷 —— 教训就是这么来的：「自检全绿不算验收，截图才是布局的实测结果」。
    /// 布局这一档唯一的实测结果是像素。而进程内这一发（<c>DisplayServer.ScreenGetImageRect</c>）
    /// 和引擎自己的坐标口径一致；外部那份 <c>CopyFromScreen</c> 在 125% 这块屏上会差 1.25 倍，
    /// 得先 <c>SetProcessDPIAware()</c> 才对得上矩形 —— 两个坑一条记在 <c>docs/research/probe-and-criteria-pitfalls.md</c>
    /// 的 DPI 口径，一条记在 <c>docs/research/desktop-layer-and-window-measurements.md</c> 的进程内抓屏。
    ///
    /// 三档各跑一次（<c>--data</c> 指一份临时设置，把 <c>calendar.style</c> 挨个拧过去）
    /// 就是这一轮加的截图判据。
    ///
    /// <b>带上 <c>--sweep</c> 才有能看的帧</b>：日历住在壁纸那一层，任何一扇普通窗口都压得住它
    /// （2026-09-26 上午那一发量到的两档之一），所以桌面上开着 Chrome 时抓回来的那一帧是 Chrome。
    /// 扫桌面那一动的是别人家的窗，因此照 <see cref="Sweep"/> 那一条规矩走：名单先印、
    /// 15 秒兜底、抓完还原并对账。</summary>
    private void ScheduleCalendarShot(string path)
    {
        Log("calendar", $"测量程序那一发：1.5 秒后把日历那块矩形抓成 {path}" +
                        $"{(_calendarSweep ? "（先扫桌面，抓完还原）" : "（桌面不扫：那上面压着别人的窗就不算布局的实测结果）")}，抓完就退");
        StepAfter(1_500, () =>
        {
            var calendar = _hooks.Calendar();
            if (calendar == null || !GodotObject.IsInstanceValid(calendar))
            {
                Log("calendar", "日历没开着，这一帧没得抓");
                _hooks.Quit();
                return;
            }
            if (_calendarSweep) Sweep();
            // 先抓帧，再读透明度。反过来的话那一帧是脏的：AlphaMeasurement 那一条链要抬窗、
            // 要动 GWL_EXSTYLE（那是它要问的两条退路），跑完这扇窗已经不是产品开机时那个形状了 ——
            // 2026-09-26 那一发就是这么读到一张纯灰的「那一帧」，上面连字都没有。
            var rect = calendar.ScreenRect;
            var image = DisplayServer.ScreenGetImageRect(
                new Rect2I(rect.Left, rect.Top, rect.Width, rect.Height));
            if (image == null || image.IsEmpty())
            {
                Log("calendar", $"这一帧没读到东西（矩形={rect}），文件没写成");
                if (_calendarSweep) RestoreDesk("抓帧失败");
                _hooks.Quit();
                return;
            }
            var err = image.SavePng(path);
            Log("calendar", $"那一帧：{path} 矩形={rect} 读回={image.GetWidth()}x{image.GetHeight()} 错误码={err}");
            AlphaMeasurementAllThree();
            if (_calendarSweep) RestoreDesk("抓完那一帧");
            _hooks.Quit();
        });
    }

    /// <summary>测量程序那一发：这块矩形上「什么都没画」的那一点，读到的是底下那片桌面还是我们自己的底？
    ///
    /// 这一发是 2026-09-26 那一帧 outline 逼出来的：outline 那一档不填任何底色，屏幕上却是一块
    /// 1792x966 的<b>实心黑</b>压在桌面上。要么窗口的每像素透明在挂进壁纸之后不成立了，
    /// 要么是我们自己这一侧画了一块底 —— 这两条的修法完全不同，所以不能靠猜。
    ///
    /// 判据：同一点读两次，一次窗开着、一次 <c>SW_HIDE</c> 之后。两次一样 → 那一层真的透；
    /// 藏起来是壁纸、开着是黑 → 不透，而且黑来自我们这一侧。读点用的是进程内的
    /// <see cref="Frames.Shot(Win32.Point)"/>，外部那份实测结果在 125% 这块屏上坐标是虚的。
    ///
    /// <b>2026-10-12 换了一族之后这一发更有用</b>：现在要求的就是「底下那片桌面透过来」
    /// （图标能透过来、日历压不住它）。判据不用改，三档的名字从
    /// 「嵌着 → 抬到顶层 → 再放回去」改成「摆着 → 抬起打字 → 放回最底下」。
    /// <b>三档都透</b>才是今天该读到的结果；只有「摆着」不透，说明 <c>WS_EX_LAYERED</c>
    /// 那一族没补上，或者自己这一侧又画了底。</summary>
    private void AlphaMeasurementAllThree()
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return;
        AlphaMeasurement("摆着（顶层、压在桌面正上方）");
        _hooks.CalendarProbe()?.BeginEdit();
        AlphaMeasurement("抬起打字");
        calendar.CommitAndSetDown("测量程序读完透明度");
        AlphaMeasurement("放回最底下");
    }

    private void AlphaMeasurement(string when)
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return;
        // 先让它画完一帧。2026-10-12 实测：抬起 → 放回最底下之后紧接着读，读到的是抬起那一帧的像素
        // （差=77），等一帧之后再读才是今天那一档该有的读数。别把「拍早了」读成「不透明了」。
        System.Threading.Thread.Sleep(350);
        var hwnd = DesktopLayer.HandleOf(calendar);
        if (hwnd == IntPtr.Zero)
        {
            Log("calendar", $"透明度实测结果（{when}）：拿不到这扇窗的 HWND，这一发没做");
            return;
        }
        var rect = calendar.ScreenRect;
        var points = new[]
        {
            ("抬头那一带", new Win32.Point { X = rect.Left + rect.Width / 2, Y = rect.Top + 10 }),
            ("左上那格", new Win32.Point { X = rect.Left + 100, Y = rect.Top + 200 }),
            ("右下角", new Win32.Point { X = rect.Right - 100, Y = rect.Bottom - 100 }),
        };
        var plan = points.Select(p => new Frames.Want(p.Item1, p.Item2)).ToArray();
        var shown = Frames.Shot(plan);
        // 实测结果之前先问那一点归谁 —— 和「发键之前必须确认前台是目标」是同一条规矩。
        // 这一发要是有一扇普通窗口压在我们上面（全屏游戏、或者桌面没扫干净），
        // 「窗开着」和「藏起来」读到的是同一份别人的像素，差=0 会假绿成「透」。
        // 假绿还有第二条路，2026-09-29 才堵上：两发都**没读到**的时候差也是 0，原来那一份
        // SampleScreen 把 CLR_INVALID 折成 (0,0,0)，接口上分不出「读到黑」和「没读到」。
        // 现在 Frames.Diff 把这一档单还一个 Unreadable，下面那一行判据先问它。
        var owners = points.Select(p => DesktopLayer.OwnerOf(hwnd, p.Item2)).ToArray();
        Win32.ShowWindow(hwnd, Win32.SwHide);
        System.Threading.Thread.Sleep(250);
        var hidden = Frames.Shot(plan);
        Win32.ShowWindow(hwnd, Win32.SwShowNoActivate);
        System.Threading.Thread.Sleep(250);
        for (var i = 0; i < points.Length; i++)
        {
            var verdict = Frames.Diff(shown[i], hidden[i], 3);
            // 「那一点归不归我们」这一问的答法 2026-10-12 换了。原来只认
            // `OwnerOf` 答 `Ours`；而这一扇带 WS_EX_TRANSPARENT 之后鼠标穿过去，
            // `OwnerOf` 在我们自己的矩形里也答「归壁纸层」—— 那一问恒为假，
            // 于是三行实测结果全被打成「这一发不算数」，把「差=0 透得过来」这条真结果也一起埋了。
            // 现在分两问：**有没有别人的窗压在上面**（那一问 `OwnerOf` 仍然答得准），
            // **那一点在不在我们这块矩形里**（那一问归我们自己的那一发）。
            var covered = owners[i].Owner is WindowTier.Owner.AppWindow or WindowTier.Owner.NoWindow;
            var ours = !covered && DesktopLayer.OverCells(rect, points[i].Item2);
            Log("calendar", $"透明度实测结果（{when}）{points[i].Item1} ({points[i].Item2.X},{points[i].Item2.Y})：" +
                            $"{owners[i].Measurement}；在不在我们矩形里={DesktopLayer.OverCells(rect, points[i].Item2)}；" +
                            $"窗开着={shown[i]} 藏起来={hidden[i]} " +
                            $"差={Frames.GapText(shown[i], hidden[i])} " +
                            (covered ? "→ 有别人的窗压在那一点，这一发不算数" :
                             !ours ? "→ 那一点不在我们矩形里，这一发不算数" :
                             verdict == Frames.Verdict.Unreadable ? "→ 有一点没读到，这一发不算数" :
                             verdict == Frames.Verdict.Same ? "→ 那一点是透的（底下那片桌面原样透过来）"
                                                          : $"→ 差 {Frames.GapText(shown[i], hidden[i])}：" +
                                                            (Frames.Gap(shown[i], hidden[i]) <= 40
                                                                ? "格子的半透底和底下那片桌面混出来的差，是应该有的（抬头那一带差=0 才是纯透）"
                                                                : "这一块是不透明的，读到的是我们这一侧的底")));
        }

        // 2026-10-12 撤掉了原来接在后面那两条退路实验（手动补 WS_EX_LAYERED、再上一道颜色键）。
        // 它们量的是「SetParent 之后每像素透明没了」那个故障，而今天这一族挂进壁纸那一版已经没有了：
        // 日历是顶层窗，WS_EX_LAYERED 从一开始就补上了，差=0 就是答案，没有退路要试。
        // 那一发的实测结果留在 docs/research/desktop-layer-and-window-measurements.md。
    }

    // ---- 遮罩那一帧 ----------------------------------------------------------------

    private List<int> _shotScreens = [];
    private List<Image?> _beforeFrames = [];

    /// <summary>测量程序那一发：让<b>到点那一次</b>真的抬起来，
    /// 走的是<b>触发本身</b>，不是直接调 <see cref="Scheduler.RaiseBlock"/> ——
    /// 递两帧现实时间（一帧在到点前一分钟、一帧在到点那一刻），两帧之间没有一帧 <c>_Process</c>
    /// （它们在同一个回调里连着跑），所以跨过边界的那一分钟被认出来。
    ///
    /// <b>举遮罩前把 App 的现实时间缓存拧成假时刻</b>（卡片 02 的 <c>SetWallNow</c>）：
    /// <c>fake</c> 是块开始那一刻（跨午夜取次日 00:00）。不拧的话，本帧的 <c>_wallNow</c> 还是
    /// 到点前那一分钟，而 <c>Raise → Enter → BuildBlock</c> 全程同步 —— 遮罩上的块名会按
    /// 「不在块里」算成空白，时钟也指着上一分钟。两帧 <c>Step</c> 仍显式递
    /// <c>before</c> / <c>start</c>，不经缓存。抓完 <c>FaderShot</c> 由排程那一头还原（null = 真钟）。
    ///
    /// <b>种出来的那一块只在这一发活着的内存里</b>：不写文档、盘上那份一个字不动，退出即消失。
    /// <c>autoFade</c> 与 <c>blockWhenFullscreen</c> 是到点那一次的两把钥匙，而这一台机器上
    /// 它们是什么值不是这一发要量的，所以就地拧成开着并把拧过这件事印出来（不假装是文档里的值）。
    ///
    /// 抓的是<b>遮罩本身</b>：块名那一条画的就是种下去的「上午」—— 这一发的量具变化
    /// （卡片 02 之前它读真分钟，恒为空白）。</summary>
    private void RaiseAtBoundary()
    {
        var settings = _hooks.Live;
        var machine = _hooks.Machine;
        var realNow = DateTime.Now;
        var start = (Routine.MinuteOfDay(realNow) + 1) % Routine.MinutesPerDay;
        var block = new TimeBlock
        {
            StartMinute = start, EndMinute = (start + 3 * Routine.MinutesPerHour) % Routine.MinutesPerDay,
            Name = "上午", Fade = true, YieldsToInput = true, Flow = "",
        };
        settings.Blocks = [block];
        settings.RoutineEnabled = true;
        settings.AutoFade = true;
        settings.BlockWhenFullscreen = false;
        // 假时刻 = 块开始那一刻；跨过午夜的那一档取次日 00:00。
        var fake = start == 0
            ? realNow.Date.AddDays(1)
            : realNow.Date.AddMinutes(start);
        _hooks.SetWallNow(fake);
        Log("fader", $"假钟：SetWallNow({fake:yyyy-MM-dd HH:mm:ss}) —— 块「{block.Name}」开始那一刻，" +
                     $"遮罩的块名和时钟从 App 的现实时间缓存读的就是它；抓完还原真钟");
        var before = (start + Routine.MinutesPerDay - 1) % Routine.MinutesPerDay;
        // 那个 0.2 秒一发（产品的 _Process 那一发）照常取真实测结果；这一发只递假时钟帧，所以
        // 聚合由 Detected.Fake 照着假输入拼，独占全屏那一格照原义给 false
        // （上一行刚把 blockWhenFullscreen 拧成关着，这一发量的就是「拧着」那一路）。
        machine.Step(_hooks.Input.NowMs, Detected.Fake(_hooks.Input), settings, before);
        machine.Step(_hooks.Input.NowMs, Detected.Fake(_hooks.Input), settings, start);
        var snap = machine.SnapshotOf(settings);
        Log("fader", $"到点那一次：块「{block.Name}」{Routine.Clock(block.StartMinute)}–{Routine.Clock(block.EndMinute)}，" +
                     $"递了 {Routine.Clock(before)} 和 {Routine.Clock(start)} 两帧，状态机={snap.Phase}，" +
                     $"记成块到点={snap.BlockRaise != null}；autoFade 拧成 {settings.AutoFade}、" +
                     $"blockWhenFullscreen 拧成 {settings.BlockWhenFullscreen}（就这一发活着的内存，文档没动）");
    }

    private void ScheduleFaderShot(string path, bool atBoundary = false)
    {
        var settings = _hooks.Live;
        Log("app", $"测量程序那一发：600 毫秒后先按「盖之前」抓一份同一块矩形当基准，再{(atBoundary ? "让到点那一次抬起来" : "举一次预览")}，" +
                   $"淡入 {settings.FadeInMs} 毫秒走完 + 700 毫秒（清单那一档该已经进 step 2）之后抓 {path}");
        StepAfter(600, () =>
        {
            _shotScreens = _hooks.Screens();
            _beforeFrames = _shotScreens
                .Select(s => (Image?)DisplayServer.ScreenGetImageRect(ScreenRectOf(s)))
                .ToList();
            if (atBoundary) RaiseAtBoundary(); else _hooks.TogglePreview();
            if (_hooks.Faders().Count == 0)
            {
                Log("app", "预览没举出任何一扇遮罩 —— 这一帧没得抓，先对一下上面有没有「淡入：N 块屏」那一行");
                _hooks.Quit();
                return;
            }
            Log("app", $"盖之前的基准：{string.Join(" / ", _beforeFrames.Select(DescribeFrame))}" +
                       $"（屏 {string.Join(',', _shotScreens)}），淡入走 {settings.FadeInMs} 毫秒");
            // 斜坡**走的过程中**那两帧。之前这一发只有落定之后那一张，「不许跳步」那条判据
            // 一直只有 _Process 里的 alpha 实测结果，屏幕上从来没有过中间那一帧（2026-09-27 补）。
            StepAfter(settings.FadeInMs * 0.4, () => RampShot(path, "四成"));
            StepAfter(settings.FadeInMs * 0.75, () => RampShot(path, "七成五"));
            StepAfter(settings.FadeInMs + 700, () =>
            {
                FaderShot(path);
                // 到点那一族举遮罩前拧了假钟（RaiseAtBoundary），落定帧抓完就还原 ——
                // 之后的淡出、退出和日志时间戳都回到真钟那一路。null = 真钟。
                if (atBoundary)
                {
                    _hooks.SetWallNow(null);
                    Log("fader", "假钟还原：SetWallNow(null) —— App 的现实时间缓存下一帧起重新问真钟");
                }
            });
        });
    }

    /// <summary>斜坡中途那一帧：抓下来、报这一刻的 alpha 和单帧最大跳，不动任何东西。
    /// 判据是「这一帧的深浅对得上这一刻的 alpha」，所以它和落定那一发读的是同一组数。</summary>
    private void RampShot(string path, string tag)
    {
        var faders = _hooks.Faders();
        if (faders.Count == 0 || _shotScreens.Count == 0) return;
        var rect = ScreenRectOf(_shotScreens[0]);
        var image = DisplayServer.ScreenGetImageRect(rect);
        if (image == null || image.IsEmpty())
        {
            Log("fader", $"斜坡 {tag}：这一帧没读到东西（矩形={rect}），文件没写成");
            return;
        }
        // 文件名要跟着传进来的那一份走：只拿 GetFileNameWithoutExtension 会把目录丢掉，
        // 那一发中间那两帧就落在进程的当前目录里（2026-09-27 真栽了：仓库根目录多了六张
        // PNG 和它们的 .import）。
        var dir = Path.GetDirectoryName(path);
        var file = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir,
            $"{Path.GetFileNameWithoutExtension(path)}-ramp-{tag}{Path.GetExtension(path)}");
        var fader = faders[0];
        Log("fader", $"斜坡 {tag}：{file} 矩形={rect} alpha={fader.Opacity:F3} " +
                     $"走了 {fader.RampFrames} 帧 单帧最大跳={fader.MaxAlphaStep:F4} " +
                     $"主色={FrameDominant(Rgba(image))} 错误码={image.SavePng(file)}");
    }

    /// 落定之后那一帧：逐屏抓、逐屏报两条判据，然后把遮罩收掉再退。
    private void FaderShot(string path)
    {
        var settings = _hooks.Live;
        var machine = _hooks.Machine;
        var faders = _hooks.Faders();
        // 当下这一步由快照交出（卡片 01 的读口），这一族不再自己索引 Plan。
        var stage = machine.SnapshotOf(settings).Stage;
        var step = stage.Step;
        Log("fader", $"抓帧这一刻：step {stage.Number}/{stage.Total} " +
                     $"content={(step == null ? "—" : string.Join('+', step.Content))} " +
                     $"interactive={step?.Interactive} " +
                     $"清单那一片：{(_hooks.FaderProbe() is { } fp ? fp.Checklist() : "这一发没起遮罩")}");
        var count = Math.Min(_shotScreens.Count, faders.Count);
        for (var i = 0; i < count; i++)
        {
            var screen = _shotScreens[i];
            var rect = ScreenRectOf(screen);
            var image = DisplayServer.ScreenGetImageRect(rect);
            if (image == null || image.IsEmpty())
            {
                Log("fader", $"屏 {screen}：这一帧没读到东西（矩形={rect}），文件没写成");
                continue;
            }
            var file = count > 1
                ? $"{Path.GetFileNameWithoutExtension(path)}-屏{screen}{Path.GetExtension(path)}" : path;
            var err = image.SavePng(file);
            var bytes = Rgba(image);
            var pixels = image.GetWidth() * image.GetHeight();
            var alpha = faders[i].Opacity;
            Log("fader", $"那一帧：{file} 矩形={rect} 读回={image.GetWidth()}x{image.GetHeight()} 错误码={err} " +
                         $"alpha={alpha:F3} 主色={FrameDominant(bytes)} 文档底色={settings.BackgroundColor} " +
                         $"凝实上限={settings.MaxOpacityPercent}%");
            var before = i < _beforeFrames.Count ? _beforeFrames[i] : null;
            if (before == null || before.IsEmpty() ||
                before.GetWidth() != image.GetWidth() || before.GetHeight() != image.GetHeight())
            {
                Log("fader", "判据一、二：基准那一份没抓到或者对不上尺寸，这一屏不算数");
                continue;
            }
            // 基准也落一份盘：判据是「拿这一帧比那一份算出来的」，那一份不留下来，
            // 下一轮就只能信我算的数，而这一轮的教训恰恰是判据自己也要能被复看。
            // 落在同一只目录里 —— 第一版只接了文件名，于是那两份「盖之前」掉进了工程的根目录。
            var baseFile = Path.ChangeExtension(file, null) + "-盖之前.png";
            Log("fader", $"基准那一份：{baseFile} 错误码={before.SavePng(baseFile)}");
            var beforeBytes = Rgba(before);
            // 「这一处该是什么色」里的底色：开了 image 就不是纯色底，是那张图在那一点上的像素
            // （图没盖到的地方，比如 fit 留的那两条边，才是文档里那个 backgroundColor）。
            // 不带这一层，判据一把图本身数成字 —— 2026-09-26 那一发 613‰ 就是这么来的。
            var fader = faders[i];
            var flat = Fader.Rgb(settings.BackgroundColor);
            Color Overlay(int x, int y) =>
                fader.PicturePixel(new Vector2I(rect.Position.X + x, rect.Position.Y + y)) ?? flat;
            var ink = FrameResidual(bytes, beforeBytes, image.GetWidth(), alpha, Overlay);
            Log("fader", $"判据一的底色那一层：图={(settings.Image.Enabled ? settings.Image.Position : "没开")} " +
                         $"文档底色={settings.BackgroundColor} 图上的点={settings.Image.File}");
            // 包围盒按「位置 + 大小」写：Godot 打印 Rect2I 本来就是 `(位置), (大小)`，
            // 我第一版把它读成了两个角点，于是把「盖住了清单」误读成「清单那块没算进去」。
            Log("fader", $"判据一（字上没上来）：和「盖之前那份按 alpha 混上底色」算出来的该是之色比，" +
                         $"差过 {InkThreshold} 的 {ink.Ink} 个（{ink.Ink * 1000.0 / Math.Max(1, pixels):F2}‰），" +
                         $"对得上的 {ink.Clean} 个，那一块从 ({ink.Box.Position.X},{ink.Box.Position.Y}) 起 " +
                         $"{ink.Box.Size.X}x{ink.Box.Size.Y}" +
                         (ink.Ink == 0 ? " → 一个都不差：字压根没上来了"
                                       : $"（桌面上这 3.7 秒里自己动过的地方也算在里面，最后一板斧是拿眼睛看）\n" +
                                         $"      第一个对不上的点：{ink.First}"));
            var diff = FrameDifference(beforeBytes, bytes);
            Log("fader", $"判据二（盖没盖上）：和盖之前那一份比，一模一样 {diff.Same} 个 / " +
                         $"变了 {diff.Changed} 个（{diff.Changed * 100 / Math.Max(1, pixels)}%），" +
                         $"单通道最大差 {diff.Max}" +
                         (diff.Same * 2 > pixels ? " → 一多半没盖上，这一帧不是布局的实测结果" : " → 这块矩形整个被盖住了"));
        }
        foreach (var frame in _beforeFrames) frame?.Dispose();
        _beforeFrames.Clear();
        // 预览再按一次就是收掉 —— 但只在它还开着的时候按。2026-09-27 那一发：淡入那 3.7 秒里
        // 桌面上动了一下鼠标，第一步是可挥散的，于是遮罩在抓帧之前就没了；这一发再按一次
        // TogglePreview 等于**又举起一扇**（日志里那发「淡入：连续已经用了 4 秒」就是这么来的）。
        // 挥散不是测量程序的错，但它必须说出来，不然「两帧都落盘了」这句话是假的。
        if (faders.Count > 0)
        {
            _hooks.TogglePreview();
            Log("fader", $"两帧都落盘了，{settings.FadeOutMs} 毫秒淡出走完之后退");
        }
        else
            Log("fader", "抓帧这一刻桌上一扇遮罩都没有（多半是淡入途中被挥散了）——" +
                         "中间那两帧仍然在，落定那一张没有；这一跑不算数，重跑或者按住鼠标");
        StepAfter(settings.FadeOutMs + 800, _hooks.Quit);
    }

    private static Rect2I ScreenRectOf(int screen)
    {
        var at = DisplayServer.ScreenGetPosition(screen);
        var size = DisplayServer.ScreenGetSize(screen);
        return new Rect2I(at.X, at.Y, size.X, size.Y);
    }

    private static string DescribeFrame(Image? image) =>
        image == null || image.IsEmpty() ? "空" : $"{image.GetWidth()}x{image.GetHeight()}";

    /// 判据数色差时，差在 12 个台阶以内算「同一种颜色」：比淡入斜坡那一帧一步（3 秒斜坡实测 4~8 步）还大，
    /// 所以剩下的那点斜坡不会被数成「字」，而字形那种实心差动辙上百。
    private const int InkThreshold = 12;

    private static byte[] Rgba(Image image)
    {
        image.Convert(Image.Format.Rgba8);
        return image.GetData();
    }

    /// <summary>这一帧里压倒性那个色：每通道 32 档的直方图里最高的那一格，取档心。
    /// 它回答的是「屏幕上这块绿是不是文档里那个绿混出来的」。</summary>
    private static string FrameDominant(byte[] data)
    {
        var buckets = new int[32768];
        for (var i = 0; i + 3 < data.Length; i += 4)
            buckets[((data[i] >> 3) << 10) | ((data[i + 1] >> 3) << 5) | (data[i + 2] >> 3)]++;
        var best = 0;
        for (var b = 1; b < buckets.Length; b++)
            if (buckets[b] > buckets[best]) best = b;
        return $"({((best >> 10) & 31) * 8 + 4},{((best >> 5) & 31) * 8 + 4},{(best & 31) * 8 + 4})";
    }

    /// <summary>判据一：逐点问「这一处<b>该</b>是什么色」—— 基准是盖之前那一份按实测 alpha
    /// 混上文档底色算出来的，<b>不是</b>设置里那个 RGB，也不是这一帧的主色。
    ///
    /// 为什么主色不行：遮罩是半透的（这台机器 <c>maxOpacityPercent</c> 75%），它底下是一张本来
    /// 就不均匀的桌面。拿主色当基准，Chrome 那一片白和壁纸那一点蓝全都被数成「字」——
    /// 2026-09-26 这一发第一次就是这么读出 34% 的，那个数不是布局，是桌面。
    /// 这一条和「判据不能拿有东西压着的点比」那一条是同一个坑，只是这次压在上面的
    /// 不是我的半透底，是他正开着的浏览器。
    ///
    /// <b>还脏的地方</b>：桌面在这 3.7 秒里自己动过的地方（视频、闪动的光标、钟）一样会被数进来。
    /// 所以这条只报数目和包围盒，最后一板斧仍然是拿眼睛看那一帧。</summary>
    private static (int Ink, int Clean, Rect2I Box, string First) FrameResidual(
        byte[] after, byte[] before, int wide, float alpha, Func<int, int, Color> overlay)
    {
        var keep = 1f - alpha;
        var ink = 0;
        var clean = 0;
        var found = false;
        Rect2I box = default;
        string first = "";
        var high = after.Length / 4 / Math.Max(1, wide);
        for (var y = 0; y < high; y++)
            for (var x = 0; x < wide; x++)
            {
                var p = (y * wide + x) * 4;
                var bg = overlay(x, y);
                // 「该是什么色」这一发**不**走 Frames.Blend：这里是 (int)(x + 0.5f) 的截断，
                // 而 Blend 走 Math.Round 的 banker's rounding —— 在半格上两者差 1。搬比色不搬这一条，
                // 搬家不动实测结果是这一发的验收。
                var dr = (int)(before[p] * keep + bg.R8 * alpha + 0.5f);
                var dg = (int)(before[p + 1] * keep + bg.G8 * alpha + 0.5f);
                var db = (int)(before[p + 2] * keep + bg.B8 * alpha + 0.5f);
                var d = Frames.Gap(after, p, dr, dg, db);
                if (d <= InkThreshold)
                {
                    clean++;
                    continue;
                }
                ink++;
                // 第一个对不上的点带出来：判据只报数目的话，「图算错了」和「字没上来」分不开
                // （2026-09-26 那一发 841‰ 就是这么读出来的 —— 数是对的，模型是错的）。
                if (first.Length == 0)
                    first = $"({x},{y}) 该=({dr},{dg},{db}) 读到=({after[p]},{after[p + 1]},{after[p + 2]}) " +
                            $"底={Fmt2(bg)} 混前=({before[p]},{before[p + 1]},{before[p + 2]})";
                box = found ? box.Expand(new Vector2I(x, y)) : new Rect2I(x, y, 1, 1);
                found = true;
            }
        return (ink, clean, box, first);
    }

    private static string Fmt2(Color c) => $"({c.R8},{c.G8},{c.B8})";

    /// <summary>同一块矩形两份实测结果的逐点差：一模一样多少个、变了多少个、单通道最大差多少。
    /// 「一模一样」就是遮罩没盖上的那些点 —— 所以这一条是 <c>maxOpacityPercent</c> 在屏幕上的实测结果。</summary>
    private static (int Same, int Changed, int Max) FrameDifference(byte[] before, byte[] after)
    {
        var same = 0;
        var changed = 0;
        var max = 0;
        for (var i = 0; i + 3 < after.Length && i + 3 < before.Length; i += 4)
        {
            var d = Frames.Gap(after, i, before, i);
            if (d == 0) same++;
            else
            {
                changed++;
                if (d > max) max = d;
            }
        }
        return (same, changed, max);
    }

    /// <summary>不碰桌面的双击那一条链：往同一格连推两发（隔 120 毫秒），读抬起，再读放回。
    ///
    /// 和 <see cref="ScheduleCalendarTap"/> 的分别是哪一段：<c>--tap</c> 量的是「一次真的
    /// OS 点击进不进得来」，那一条已经量完了（进得来，桥不用喂）；剩下没读到的是
    /// <b>双击判据和抬起那条路</b>，那是窗里侧的事，<c>PushInput</c> 不做命中测试，
    /// 桌面上压着什么都不影响 —— 于是这一发不必再扫一次桌面。</summary>
    private void ScheduleCalendarDouble()
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar())) return;
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);
        Log("calendar", "测量程序那一发：1.5 秒后往同一格连推两发（不碰桌面），读抬起、读放回");
        Chase(
            (1_500, "推之前", () => ReportCalendarNow("推之前")),
            (300, "第一发", () => _hooks.CalendarProbe()?.PushClick(day)),
            (120, "第二发", () => _hooks.CalendarProbe()?.PushClick(day)),
            (700, "两发之后", () => ReportCalendarNow("两发之后（该是 Editing，上面该有一行焦点实测结果）")),
            (600, "放回", () => PutCalendarDown("双击之后")),
            (700, "放回之后", () => ReportCalendarNow("放回之后（该回到那块像素）")));
    }

    /// <summary>测量程序那一发：格子那一档的编辑整条，从选中到落盘到放回，中间不靠手。
    ///
    /// 欠的是「合起来没跑过」：抬起、输入框摆进被选中的那一格、提交、放回这四步各自在
    /// widget 那一档或测量程序上成立过，1792x966 那一档上没人连着走过一次。
    ///
    /// <b>字是灌进输入框的，不是键盘打的</b>（为什么，<c>Calendar.TypeFromProbe</c> 里写着：
    /// 脚本发的键在这台机器上时好时坏，不能拿它当证据）。所以这一发证的是
    /// 摆位 + 提交那一条链，输入法那一发仍然欠着他本人 —— 别把这两件事混成一句「打字通了」。
    ///
    /// 跑法要带 <c>--data</c> 指一份临时目录：那一份 <c>calendar.jsonc</c> 是测量程序的草稿纸，
    /// 他真那份一个字节都不动。链子最后把这一条擦回去，再读一次「回到 0 条」。</summary>
    private void ScheduleCalendarEdit(string path)
    {
        if (_hooks.Calendar() == null || !GodotObject.IsInstanceValid(_hooks.Calendar())) return;
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);
        Log("calendar", $"测量程序那一发：1.5 秒后在格子那一档连着走一遍编辑 —— 选中 {day:MM-dd} → 抬起 → " +
                        $"读输入框摆位 → 灌字 → **抓一帧（编辑态：抬起之后、提交之前）** → 推回车 → 读落盘 → 擦回去" +
                        $"{(_calendarSweep ? "（先扫桌面，抓完还原）" : "（桌面不扫：那一帧可能抓到别人的窗）")}");
        Chase(
            (1_500, "扫桌面", () => { if (_calendarSweep) Sweep(); }),
            (700, "起手", () => Log("calendar",
                $"起手：样式={_hooks.Live.Calendar.Style} 矩形={_hooks.Calendar()!.ScreenRect} " +
                $"备注 {_hooks.CalendarNotes().Notes.Count} 条在 {_hooks.CalendarPath()}")),
            (300, "选中那一格", () => _hooks.CalendarProbe()?.PushClick(day)),
            (400, "抬起", () => _hooks.CalendarProbe()?.BeginEdit()),
            (700, "读摆位", () => _hooks.CalendarProbe()?.EditorPlacement()),
            (200, "灌字", () => _hooks.CalendarProbe()?.Type("测量程序一发")),
            // 抓帧排在**回车之前**：这一发的目标改成「编辑态长什么样」（2026-10-06 那一轮）。
            // 原来它排在提交之后，而回车那一发已经把窗放回壁纸层了 —— 拍到的是嵌着的样子，
            // 抬起那一段的边界、输入框的底和字一概不在画面里。判据（布局那一发「伸出窗外 0 个」）
            // 照样绿，所以「拍完只用眼睛看」那条规矩是唯一拦住这个的那一条。
            (500, "抓那一帧（编辑态）", () => GrabCalendarFrame(path)),
            (200, "推回车", () => _hooks.CalendarProbe()?.SubmitEnter()),
            (900, "提交之后", () =>
            {
                ReportCalendarNow("提交之后（该回到 Embedded 那一块像素）");
                NoteFileMeasurement(day);
            }),
            (500, "擦回去·选中", () => _hooks.CalendarProbe()?.PushClick(day)),
            (400, "擦回去·抬起", () => _hooks.CalendarProbe()?.BeginEdit()),
            (300, "擦回去·灌空", () => _hooks.CalendarProbe()?.Type("")),
            (300, "擦回去·回车", () => _hooks.CalendarProbe()?.SubmitEnter()),
            (900, "擦完之后", () => NoteFileMeasurement(day)),
            (400, "还原", () => { if (_calendarSweep) RestoreDesk("排程走到"); }),
            (600, "收尾", _hooks.Quit));
    }

    /// 落盘那一条实测结果：重读磁盘上这一份，不看内存里那份 —— 要证的正是「写到文件里去了」。
    private void NoteFileMeasurement(DateOnly day)
    {
        var path = _hooks.CalendarPath();
        var onDisk = CalendarNotes.Open(path);
        var text = File.Exists(path) ? File.ReadAllText(path).Trim() : "（文件不在）";
        Log("calendar", $"落盘实测结果：{path} 现在 {onDisk.Notes.Count} 条，" +
                        $"{day:MM-dd} 那条=「{onDisk.Get(day)}」；文件={text}");
    }

    /// 只抓一帧，不跑 <see cref="AlphaMeasurementAllThree"/> 那一条链 —— 那一条要抬窗、要动样式位，
    /// 跑完屏幕上已经不是刚提交完那一格的样子了（那条时序教训）。
    private void GrabCalendarFrame(string path)
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return;
        var rect = calendar.ScreenRect;
        var image = DisplayServer.ScreenGetImageRect(
            new Rect2I(rect.Left, rect.Top, rect.Width, rect.Height));
        if (image == null || image.IsEmpty())
        {
            Log("calendar", $"这一帧没读到东西（矩形={rect}），文件没写成");
            return;
        }
        var err = image.SavePng(path);
        Log("calendar", $"那一帧：{path} 矩形={rect} 读回={image.GetWidth()}x{image.GetHeight()} 错误码={err}");
        image.Dispose();
    }

    /// <summary>测量程序那一发：开关于窗口 → 抓一帧 → 退。
    ///
    /// 为什么这一发要存在：这一扇窗是「代码写了、自检也全绿、没人拿眼睛看过」那一类里新添的一个，
    /// 而 <see cref="Options"/> 那一发留下过两条只在屏幕上才看得见的教训 —— 场景根带着
    /// <c>visible = false</c> 就永远不出现，中文按词折行会把窗撑破。<c>--check</c> 看不见这两条。
    /// 抓帧走进程内那一条（<c>ScreenGetImageRect</c>），和外部的截屏在 125% 上差 1.25 倍那个坑无关。</summary>
    private void ScheduleAboutShot(string path)
    {
        Log("about", $"测量程序那一发：1.2 秒后开关于窗口，读两次「这一处归谁」，把它举到前面，再抓一帧（{path}）");
        void Lift()
        {
            var about = _hooks.About();
            if (about == null || !GodotObject.IsInstanceValid(about)) return;
            // 引擎的 Window 没有 move_to_front 这一发（4.7.2 的 C# 绑定里没有），而「举到前面」
            // 正是 SetWindowPos 用 HWND_TOP 那一下。不碰 AlwaysOnTop 那个旗标 —— 遮罩那一发量过：
            // 窗已经显示出来之后再赋它，引擎会把 WS_VISIBLE 清掉。
            var hwnd = Win32.OwnWindowNamed(about.Title);
            if (hwnd == IntPtr.Zero)
            {
                Log("about", "举到前面：按标题认不出这扇窗，那一帧可能抓到别人的桌面");
                return;
            }
            // HWND_TOP 不够：这一发是定时器开的，没有人点过，前台锁让 IDE 那一扇压得住我们
            // （2026-09-26 那一发读到「命中=Chrome_WidgetWin_1 'Qoder CN'」，而窗自己 可见=True）。
            // 所以要 TOPMOST 才抓得到我们这一帧 —— 这是测量程序的取巧，不是产品行为：托盘点开的
            // 那一路有 SetForegroundWindow，落在最前面是自然的。
            var ok = Win32.SetWindowPos(hwnd, Win32.HwndTopMost, 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
            Log("about", $"举到前面：HWND=0x{hwnd.ToInt64():X} 交了={ok}（测量程序用 TOPMOST，理由写在代码里）");
        }
        Chase(
            (1_200, "开窗", _hooks.OpenAbout),
            (400, "命中（没人点过）", () => AboutHit("刚开完")),
            (300, "举到前面", Lift),
            (400, "命中（举过之后）", () => AboutHit("举到前面之后")),
            (300, "那一帧", () => GrabAboutFrame(path)),
            (400, "退", _hooks.Quit));
    }

    /// 这一扇窗正中间那个物理点归谁。<b>为什么先问这一句再抓帧</b>：测量程序是定时器开的，没有
    /// 人点过，所以前台锁让它落在别人底下（和日历那条「没人点的抬起拿不到前台」是同一件事）。
    /// 不先问这一句，抓回来的那一块像素是别人的窗，看着就像「关于窗口画错了」。
    private void AboutHit(string when)
    {
        var about = _hooks.About();
        if (about == null || !GodotObject.IsInstanceValid(about)) { Log("about", $"{when}：窗口没了"); return; }
        var at = about.Position + about.Size / 2;
        var hit = Win32.WindowFromPoint(new Win32.Point { X = at.X, Y = at.Y });
        var root = Win32.GetAncestorRoot(hit);
        var mine = root == Win32.OwnWindowNamed(about.Title);
        Log("about", $"{when}：中心={at} 命中=0x{root.ToInt64():X} '{Win32.ClassName(root)}' " +
                     $"'{Win32.WindowText(root)}' 是关于我们自己={mine} 可见={about.IsVisible()} 大小={about.Size}");
    }

    private void GrabAboutFrame(string path)
    {
        var about = _hooks.About();
        if (about == null || !GodotObject.IsInstanceValid(about))
        {
            Log("about", "窗口没开起来，这一帧没得抓");
            return;
        }
        var rect = new Rect2I(about.Position, about.Size);
        var image = DisplayServer.ScreenGetImageRect(rect);
        if (image == null || image.IsEmpty())
        {
            Log("about", $"这一帧没读到东西（矩形={rect}），文件没写成");
            return;
        }
        var err = image.SavePng(path);
        Log("about", $"那一帧：{path} 矩形={rect} 读回={image.GetWidth()}x{image.GetHeight()} 错误码={err} " +
                     $"可见={about.IsVisible()} 标题='{about.Title}'");
        image.Dispose();
    }

    /// 把桌面还回去，并且给 shell 留把窗收回来的时间。<c>UndoMinimizeALL</c> 是异步的：
    /// 贴着它返回就拍对账，读出来的全是「拍早了」（2026-09-26 那一发就是这么报了 2 扇没回来，
    /// 一分钟后去看那两扇都在）。这一发补的那条对账，原来一直欠着。
    /// <b>产品退出那一路也走这里</b> —— 扫下去的是别人家的窗，不管这一发是怎么走到退出的，
    /// 桌上那些窗都得还回去。没扫过（`_swept` 假）就直接返回。
    public void RestoreDesk(string because)
    {
        if (!_swept) return;
        _swept = false;
        _restoreDeadline?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        if (System.Threading.Interlocked.Exchange(ref _restored, 1) == 0)
        {
            if (!Win32.ShowDesktop(true))
                Log("calendar", "还原这一发没成 —— 桌上那些窗还最小化着，按一次 Win+D 把它们叫回来");
        }
        else Log("calendar", "还原已经被那条 15 秒兜底叫过 —— 主循在这一发中间停过，排程那几步的实测结果作废");
        System.Threading.Thread.Sleep(1_500);
        Log("calendar", $"还原对账（{because}）：{DesktopLayer.Reconcile(_deskBefore ?? [])}");
    }

    /// 抬起打字那一段的收尾：不靠人按回车，测量程序自己把窗放回去，顺便读「回到那块像素没有」。
    /// 「现在层」读的是 <see cref="CalendarHost.StateNow"/>（卡片 03 · S28：档位从
    /// <c>Calendar</c> 搬进 <c>CalendarHost</c>，那三档读数 <c>Hidden</c> / <c>Parked</c> /
    /// <c>Editing</c> 逐字不变）。
    private void PutCalendarDown(string because)
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar)) return;
        Log("calendar", $"收尾：{because}，现在层={calendar.Host.StateNow}");
        calendar.CommitAndSetDown(because);
    }

    private void TapCell(DateOnly day, string what)
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar))
        {
            Log("calendar", $"{what}：日历没开着，这一发取消");
            return;
        }
        var (at, mine, measurement) = _hooks.CalendarProbe()?.CellProbe(day)
            ?? (default, false, "日历没开着，这一发取消");
        if (!mine)
        {
            Log("calendar", $"{what}：取消 —— 那一点不归日历\n      {measurement}\n      {Win32.DescribeOwnTopLevels()}");
            return;
        }
        var sent = Win32.ClickAt(new Win32.Point { X = at.X, Y = at.Y });
        Win32.GetCursorPos(out var cursor);
        Log("calendar", $"{what}：目标=({at.X},{at.Y}) SendInput 交了 {sent}/3 发，光标现在=({cursor.X},{cursor.Y})\n" +
                        $"      {measurement}");
    }

    /// <summary>只把光标放到那一格中心，不发点击。
    ///
    /// 一条只改一个变量的对照。2026-09-26 那一跑 <c>--tap</c> 的第一发：格子的计数加了
    /// （鼠标 按下=1/松开=1），选中却没换，第二、三发才换 —— 而 <see cref="Win32.ClickAt"/>
    /// 是把 move+down+up 塞在同一批 <c>SendInput</c> 里的。先挪、隔 400 毫秒再点，
    /// 就能分清「第一发点不动」是产品形状还是测量程序那一批事件的形状。</summary>
    private void MoveCursorToCell(DateOnly day, string what)
    {
        var calendar = _hooks.Calendar();
        if (calendar == null || !GodotObject.IsInstanceValid(calendar))
        {
            Log("calendar", $"{what}：日历没开着，这一发取消");
            return;
        }
        var (at, mine, measurement) = _hooks.CalendarProbe()?.CellProbe(day)
            ?? (default, false, "日历没开着，这一发取消");
        if (!mine)
        {
            Log("calendar", $"{what}：取消 —— 那一点不归日历\n      {measurement}");
            return;
        }
        var moved = Win32.MoveTo(at);
        Win32.GetCursorPos(out var cursor);
        Log("calendar", $"{what}：光标挪到 ({at.X},{at.Y})，交了 {moved} 发，没点\n" +
                        $"      {measurement}");
    }

    private void ReportCalendarNow(string when)
    {
        var calendar = _hooks.Calendar();
        Log("calendar", $"{when}：{(calendar == null || !GodotObject.IsInstanceValid(calendar) ? "日历没开着" : calendar.BridgeReport())}；" +
                        $"钩子收到 按下={_hooks.Input.ButtonDownEvents} 松开={_hooks.Input.ButtonUpEvents} " +
                        $"最近=[{_hooks.Input.LastButtonEvent}]");
    }

    // ---- 测量程序：自己点两下，把「点不动」那两个候选分开 ----------------------------
    //
    // 一个是「注入根本没进这个进程」，一个是「进了系统但引擎不把鼠标送给没有焦点的窗」。
    // 分开它只要三行实测结果成对看：钩子记下的按下数、这个视口的 GuiInput 记下的数、
    // Pressed/Toggled 有没有 fire。所以这里发的每一下都先问「这个点归谁」再打两侧计数。
    //
    // 注入只用 SendInput 的绝对坐标：SetCursorPos 不产生低级钩子事件，用了就把第一个候选
    // 弄成假象（上一轮那个测量程序的 MOVE 位移是 0，真位移是 SetCursorPos 给的，就是这么栽的）。

    private bool _clickArmed;
    private bool _escArmed;

    /// Esc 那只手的两个计数：收尾过几次、路过但被忽略几次。测量程序读的是这两个数，不是日志。
    private int EscEnded;
    private int EscIgnored;

    /// <summary>Esc 那一下的产品侧判定走完之后，递一个「收尾了没有」回来。
    ///
    /// <b>计数住在这一族，判定住在产品那一侧</b> —— 那边问「此刻站着的是不是收了鼠标的那一步」，
    /// 答「是」就调一次这个。这一族不判定，它只在 <c>--esc</c> 那一发里读这一对数：
    /// 「收尾过=1」那个数得配上「先有一发被忽略的」才说明闸门在起作用。</summary>
    public void NoteEscape(bool ended)
    {
        if (ended) EscEnded++;
        else EscIgnored++;
    }

    /// 测量程序那一发（<c>--esc</c>）：到了收鼠标那一步，交一个<b>真的</b> Esc 进系统 ——
    /// 按下 + 松开两批 <c>SendInput</c>，走的正是我们自己那把低级钩子，和真人按是同一条路，
    /// 所以这一发不需要任何人动手。读三个数：钩子看见几次、收尾没有、窗落没落。
    /// 每一下之后留 400 毫秒，理由和 <see cref="Inject"/> 一样：钩子在我们自己这个线程上回调。
    private void ScheduleEsc(Fader fader)
    {
        var at = _hooks.Input.NowMs;
        Log("esc", $"排上一发 Esc：{at}ms 起");
        _steps.AddRange([
            (at + 300, () => Log("esc",
                $"按之前：钩子看见={_hooks.Input.EscapeEvents} 收尾过={EscEnded} 忽略了={EscIgnored} " +
                $"这一步收鼠标={(GodotObject.IsInstanceValid(fader) && fader.Interactive)} " +
                $"遮罩可见={(GodotObject.IsInstanceValid(fader) && fader.IsVisible())}")),
            (at + 800, () => Log("esc", $"交 Esc 进系统：{Win32.PressEscape()}")),
            (at + 1300, () => Log("esc",
                $"按之后：钩子看见={_hooks.Input.EscapeEvents} 收尾过={EscEnded} 忽略了={EscIgnored} " +
                $"遮罩可见={(GodotObject.IsInstanceValid(fader) && fader.IsVisible())} 状态机={_hooks.Machine.SnapshotOf(_hooks.Live).Phase}")),
        ]);
    }

    /// 收鼠标的那一步一到手就排这四步：点第一格、报一次数、点那颗提前结束、再报一次数。
    /// 先勾后结束 —— 结束那一下会把遮罩撤掉，反过来排第二发就没地方点了。
    /// 每一下之后留 400 毫秒，是因为钩子在我们自己这个线程上回调 —— SendInput 返回的那一刻
    /// 它还没被叫到，立刻读到的数会是假的。
    ///
    /// 矩形一律在开点的那一刻读，不在排程的那一刻读：清单行是这一帧刚 AddChild 的，排版还没跑
    /// （那条实测结果在 <c>docs/research/probe-and-criteria-pitfalls.md</c>：「新加控件的位置要等 3 帧排版」）。上一轮就在排程时读了，拿到 (1192,637) 而按钮真在
    /// (1280,903) —— 点了一个根本没那个控件的地方。
    private void ScheduleClicks()
    {
        var faders = _hooks.Faders();
        if (faders.Count == 0) return;
        var fader = faders[0];
        var at = _hooks.Input.NowMs;
        // 两点排在**斜坡走完之后**：「点得到的那几块」是遮罩完全浮现之后才量出来的（契约
        // 「收鼠标的时刻是遮罩完全浮现之后」），而遮罩从举起那一刻起按帧走一条
        // `fadeInMs` 的斜坡。排在斜坡里的话那两点压根还没进那一块，`Inject` 会自己取消
        // —— 安全，但这一发什么也没量到。留 800 毫秒给它量出来和排版跑完。
        var after = _hooks.Live.FadeInMs + 800;
        Log("click", $"排上两发：{at + after}ms 起（淡入 {after}ms 之后），点之前各读一次矩形");
        _steps.AddRange([
            (at + after, () => Inject(fader, fader.InteractivePoints().FirstRow, "第一格")),
            (at + after + 400, () => Counters(fader, "点第一格之后")),
            (at + after + 1500, () => Inject(fader, fader.InteractivePoints().Exit, "提前结束那颗")),
            (at + after + 1900, () => Counters(fader, "点按钮之后")),
        ]);
    }

    private void Inject(Fader fader, Vector2I at, string what)
    {
        // **闸门从「这一点命中测试归不归我们」换成「这一点在不在 Keeper 收鼠标的那一块里」**
        //（2026-10-10）：铺满那一扇一辈子穿过，命中测试上**永远**不归我们 ——
        // 拿旧那一问当闸门，这一发会永远自己取消自己。
        // 现在答「在那一块里」是对的：落在里面的那一下由钩子吃掉，底下的程序收不到；
        // 不在里面的那一下才真的会落到别人的窗上，而那一发就不该打。
        if (!fader.Swallows(new Win32.Point { X = at.X, Y = at.Y }))
        {
            Log("click", $"{what}：目标=({at.X},{at.Y}) 这一点不在 Keeper 收鼠标的那一块里 —— " +
                         $"这一发取消，发出去就是点别人的窗口\n" +
                         $"      {fader.SwallowMeasurement()}\n      {Win32.DescribeOwnTopLevels()}");
            return;
        }
        var sent = Win32.ClickAt(new Win32.Point { X = at.X, Y = at.Y });
        Win32.GetCursorPos(out var cursor);
        Log("click", $"{what}：目标=({at.X},{at.Y}) SendInput 交了 {sent}/3 发，光标现在=({cursor.X},{cursor.Y})\n" +
                     $"      {fader.SwallowMeasurement()}\n      {Win32.DescribeOwnTopLevels()}");
    }

    private void Counters(Fader fader, string when)
    {
        Log("click", $"{when}：钩子 移动={_hooks.Input.MoveEvents} 按下={_hooks.Input.ButtonDownEvents} " +
                     $"松开={_hooks.Input.ButtonUpEvents} 最近=[{_hooks.Input.LastButtonEvent}] " +
                     $"吃掉过={_hooks.Input.SwallowCount} 最后一发=[{_hooks.Input.LastSwallow}] " +
                     $"引擎说遮罩可见={fader.IsVisible()} 遮罩有焦点={fader.HasFocus()}");
    }

    /// <c>--calendar</c> 那一发的实测结果：每 5 秒把桥两侧的三个数各报一遍。
    /// 钩子那侧「收到了几发」和窗那侧「控件收到几发」中间，隔的就是那条进程边界 ——
    /// 上一轮量的是命中测试（闸门该不该开），这一发量的才是闸门开了之后东西到没到。
    private void ReportCalendar(double delta)
    {
        if (!_calendarForced && !_hooks.CalendarForced()) return;
        _sinceCalendarReport += delta;
        if (_sinceCalendarReport < 5) return;
        _sinceCalendarReport = 0;
        var calendar = _hooks.Calendar();
        Log("calendar", $"桥：{(calendar == null || !GodotObject.IsInstanceValid(calendar) ? "没开着" : calendar.BridgeReport())}；" +
                        $"钩子收到 按下={_hooks.Input.ButtonDownEvents} 松开={_hooks.Input.ButtonUpEvents} " +
                        $"最近=[{_hooks.Input.LastButtonEvent}]");
    }

    // ---- 自己发鼠标移动（--jiggle）-------------------------------------------------
    //
    // 每 300 毫秒发一个 ±1 像素的鼠标移动：够密，所以那一段输入算「一直在用」；
    // 够轻，不会点到下面的东西，也不会往别人焦点里打字。

    private double _sinceCalendarReport;
    private long _jiggleUntilMs;
    private int _jiggles;

    private void Jiggle()
    {
        if (_jiggleUntilMs == 0 || _hooks.Input.NowMs > _jiggleUntilMs) return;
        var sent = Win32.SendInput(1, [new Win32.Input
        {
            Type = 0,
            Mouse = new Win32.MouseInput
            {
                Dx = _jiggles % 2 == 0 ? 1 : -1, Dy = 0, Flags = Win32.MOUSEEVENTF_MOVE,
            },
        }], System.Runtime.InteropServices.Marshal.SizeOf<Win32.Input>());
        _jiggles++;
        if (sent == 0) Log("app", "SendInput 返回 0，输入没发出去");
    }

    /// 产品启动那一行日志里那半句「输入靠真的键盘鼠标 / 自己发鼠标事件到 N 秒」。
    public string JiggleNote() => _jiggleUntilMs == 0
        ? "输入靠真的键盘鼠标"
        : $"自己发鼠标事件到 {_jiggleUntilMs / 1000} 秒";

    // ---- 两个小工具 ---------------------------------------------------------------

    private static int Index(string[] args, string wanted) => Array.IndexOf(args, wanted);

    /// <summary>写一行日志。走的是<b>同一个出口</b>（<see cref="LogWriter.ToApp"/>）——
    /// 这一族和产品、测量程序那几份都往那儿写，2026-09-29 之前是三份各写各的，
    /// 而那两道保护（落单的代理对换成 '?'、编码钉成不带 BOM 的 UTF-8）只有一份有。
    /// 理由写在 <see cref="LogWriter"/> 的类型注释里。</summary>
    private static void Log(string step, string message) => LogWriter.ToApp(step, message);
}
