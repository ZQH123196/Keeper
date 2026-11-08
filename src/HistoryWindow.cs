using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>休息历史：把 <see cref="History"/> 那份记录摆成「一天一行，点开看那一天」。
///
/// <para><b>数据从 <see cref="Source"/> 问，不自己读文件</b> —— 设计文档 `docs/design/records.md`「两个入口，同一份数据」
/// 那一条：
/// 托盘和设置窗两处开的是同一扇窗，而这一扇问的是 <c>App</c> 手里那一个对象。
/// **两处各读一次文件就是两份事实**，而那种不一致在界面上看不出是哪一边错了。</para>
///
/// <para><b>读法是 <see cref="HistoryDays"/> 那一份</b>（按本机时区归天、跨午夜归落下那一刻、
/// 天数倒序），这一扇不重算一遍 —— 同一个概念有两个算法，改一处漂一次。</para>
///
/// <para><b>三档，三种长得不一样的样子</b>：有记录摆那些天；一条记录都没有摆「还没记过休息」；
/// <b>读不通摆「休息记录读不通」，不是空列表</b> —— 空列表读起来是「你没休息过」，
/// 而磁盘上那份读不通的人明明记过（理由同 <c>LifeModeIndex</c> 那一档，
/// 在 <c>docs/adr/0002-settings-and-records.md</c>）。</para>
///
/// <para><b>只有两层</b>：上面一天一行（日期 + 提示了几次 / 其中休息过了几次），
/// 点开才是那天的每一次（几点、休息过了还是跳过了）。不按生活模式分、不按休息流程分、不做趋势图。</para>
///
/// <para><b>界面在编辑器里搭成场景，运行期不生成控件</b>（ADR-0001 那一族）：
/// 一天一行是引擎给的 <see cref="Tree"/> 里的一颗 item，点开看那几条是它的子 item ——
/// 不 owner-draw、不 <c>_draw</c> 手画表格。场景在 <c>res://src/HistoryWindow.tscn</c>。</para>
///
/// <para><b>「读不通」那一句里不摆文件名</b>：设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」是通则，
/// 而文件名就是那一类的正主。所以那句话说的是<b>哪一份记录</b>（「休息记录」），
/// 不说它叫什么、也不说它在哪 —— 那是日志那一侧的事（<c>App._Ready</c> 印的那一行带路径）。</para></summary>
public partial class HistoryWindow : Window
{
    /// 树那条路。场景里节点名一处，自检和 <see cref="Fill"/> 各问它一次 —— 抄一份路径字符串
    /// 就是「改场景名忘了改这里」那一族，而症状是窗上什么都没画、不报错。
    internal const string DaysPath = "Margin/Stack/Scroll/Days";
    internal const string BlankPath = "Margin/Stack/Blank";
    internal const string ScrollPath = "Margin/Stack/Scroll";

    public Action<string, string> Log = (_, _) => { };

    /// 关掉之后要还回去的那一句（<c>App</c> 留着 <c>_history</c> 的引用，靠这个知道自己该清空）。
    public Action<HistoryWindow>? Closed;

    /// <b>App 手里那一份</b>，不是自己去读文件。两个入口开的是同一扇窗，
    /// 所以这里也只有一份数据可问。
    public Func<History> Source = () => new();

    public override void _Ready()
    {
        Title = Tr("休息历史");
        GetNode<Label>("Margin/Stack/Head").Text = Tr("一天的休息记录");
        CloseRequested += Close;
        Fill();

        // 和「设置…」「关于 Keeper」同一个理由：引擎给的新窗位置是 (0,0)，那在这台机器上
        // 等于把标题栏推到屏幕外面去，所以自己摆到主屏工作区正中。
        var work = DisplayServer.ScreenGetUsableRect(0);
        Position = new Vector2I(work.Position.X + (work.Size.X - Size.X) / 2,
            work.Position.Y + (work.Size.Y - Size.Y) / 2);
        // 场景根 visible = false，OS 窗到这一行才建（那一闪见类型注释）。
        Show();
        GrabFocus();
        Log("history", $"开窗：可见={IsVisible()} 位置={Position} 大小={Size} 标题='{Title}' " +
                       $"天={RowCount()} 行={ItemCount()}");
    }

    /// <summary>把 <see cref="Source"/> 那一份读成 <see cref="HistoryDays.Reading"/> 再摆出来。
    ///
    /// <b>读一次、摆一次</b>：三档的判都在这里分，而「读不通」和「一条都没有」必须走两条不同的路
    /// —— 合成一句 <c>if (Days.Count == 0)</c> 就把「读不通」读成了「你没休息过」，
    /// 那正是契约点名要避开的那个假象。</summary>
    public void Fill()
    {
        var reading = HistoryDays.Reading.Of(Source());
        var blank = GetNode<Label>(BlankPath);
        var scroll = GetNode<ScrollContainer>(ScrollPath);
        var tree = GetNode<Tree>(DaysPath);
        tree.Clear();

        if (reading.Unreadable)
        {
            Say(blank, scroll, Tr("休息记录读不通") + "\n" +
                Tr("这一份程序没有抹掉，也没有往里写新的一条。"));
            return;
        }
        if (reading.Days.Count == 0)
        {
            Say(blank, scroll, Tr("还没记过休息"));
            return;
        }

        blank.Visible = false;
        scroll.Visible = true;
        // **根要自己建**：<c>CreateItem()</c> 不带参数建的是「根」，而 Godot 一棵树只有一个根 ——
        // 连着调三次不会得到三根，会得到「一根 + 挂在它底下的东西」，那正是判据第一次量到的
        // 「摆出 3 天，该是 1 天」。所以根建一次，天一律挂在根底下。
        var root = tree.CreateItem();
        foreach (var day in reading.Days)
        {
            var row = tree.CreateItem(root);
            row.SetText(0, DayLine(day));
            // **默认全收着**：一眼看完一周要点的是那几行数，不是几百行时刻
            //（设计文档 `docs/design/records.md`「一层是最近若干天，点某一天才展开那天的每一次」）。
            row.Collapsed = true;
            foreach (var entry in day.Entries)
            {
                var line = tree.CreateItem(row);
                line.SetText(0, EntryLine(entry));
            }
        }
    }

    /// 摆「没有行可摆」的那一句：句子在，树整个收起来。<b>两句都摆在树外面</b> ——
    /// 塞进树里的后果是搜索和展开会把那句话当成一行记录。
    private static void Say(Label blank, ScrollContainer scroll, string text)
    {
        blank.Text = text;
        blank.Visible = true;
        scroll.Visible = false;
    }

    /// 顶层那几行（一天一行）。自检读的是这个数，不是整棵树里 item 的总数。
    public int RowCount()
    {
        var tree = GetNodeOrNull<Tree>(DaysPath);
        return tree?.GetRoot() == null ? 0 : tree.GetRoot().GetChildCount();
    }

    /// 那几天加起来一共几次。<b>和 <see cref="RowCount"/> 是两件事</b>：一层收着的时候树上
    /// 仍然挂着全部子 item，所以「项数」量的是摆出来的次数，不是看见的行数。
    public int ItemCount()
    {
        var tree = GetNodeOrNull<Tree>(DaysPath);
        var root = tree?.GetRoot();
        if (root == null) return 0;
        var total = 0;
        for (var i = 0; i < root.GetChildCount(); i++) total += root.GetChild(i).GetChildCount();
        return total;
    }

    /// <summary>一天那一行的字：日期 + 提示了几次 + 其中休息过了几次。
    ///
    /// <b>日期按本机时区归的那一天</b>（<see cref="HistoryDays.DayOf"/> 的正本在那一族里），
    /// 时刻按人话那一档写、**不带毫秒**（设计文档 `docs/design/config.md`「界面上不出现毫秒」那条通则）。
    /// <b>日期和时刻不翻</b>：那是数据，而语言表只装 Keeper 自己的措辞。
    /// </summary>
    public static string DayLine(HistoryDays.Day day) =>
        $"{day.Date:yyyy-MM-dd}　" +
        Lang.T("提示 {0} 次 · 休息过 {1} 次")
            .Replace("{0}", day.Prompted.ToString(CultureInfo.InvariantCulture))
            .Replace("{1}", day.Taken.ToString(CultureInfo.InvariantCulture));

    /// 那天某一次：几点 + 休息过了还是跳过了。<b>只有这两个字段</b> —— 不记时长
    /// （2026-10-08 所有者定夺），所以界面上看不出这次实际歇了多久。
    public static string EntryLine(HistoryDays.Entry entry) =>
        $"{entry.At:HH:mm}　" + Lang.T(entry.Taken ? "休息过了" : "跳过了");

    private void Close()
    {
        Log("history", "关了");
        Closed?.Invoke(this);
        QueueFree();
    }

    /// Esc 关这一扇，和设置窗一样 —— 焦点在这里时那一下不该顺手把整个程序带走。
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }

    // ---- 自检 ---------------------------------------------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 02）。</summary>
    internal static readonly CriterionOf Shown = new(
        () => "休息历史小窗通过：三档长得不一样（有记录摆天、一条都没有说「还没记过休息」、" +
              "读不通说「休息记录读不通」而不是空列表）、一天一行带那两个数、点开是那天那几次（几点 + 休息/跳过）、" +
              "只有两层、界面上没有毫秒也不出现记录文件的名字",
        "休息历史小窗", SelfTest);

    /// <summary>造一扇窗出来摆那一档。<b>不挂进树</b>：<c>--check</c> 是无头的，
    /// 这一族量的是「树上摆了什么」，不是屏上那一帧 —— 像素那一半在
    /// <c>docs/research/live-frame-measurements.md</c> 那一族的手法里，
    /// 要真渲染才跑得动（无头用的是哑渲染器，节点在、像素读不出来）。
    /// 「读不通那一档和空列表长得不一样」就是这么量的：真窗各摆一档，抓两帧比。</summary>
    private static HistoryWindow New(History history)
    {
        if (GD.Load<PackedScene>("res://src/HistoryWindow.tscn")?.Instantiate<HistoryWindow>()
            is not HistoryWindow win)
            throw new Exception("res://src/HistoryWindow.tscn 实例化不出来");
        win.Source = () => history;
        return win;
    }

    /// <summary>场景里那个树摆成某一档之后，三样读数：摆出来的天数、摆出来的次数、
    /// <c>Blank</c> 上那句话（<c>null</c> = 收着，什么都没说）。
    ///
    /// <b>取的是第一行</b>：读不通那一档摆的是两行（标题 + 「没有抹掉，也没有写」那半句），
    /// 而判据要问的是**那一句标题是不是那一句** —— 拿整段比会把「多了一行解释」读成
    /// 「说错了」，而多一行解释不是错。</summary>
    internal static (int Rows, int Items, string? Said) Look(HistoryWindow win)
    {
        var blank = win.GetNode<Label>(BlankPath);
        var said = blank.Visible
            ? blank.Text.Split('\n')[0].TrimEnd('\r')
            : null;
        return (win.RowCount(), win.ItemCount(), said);
    }

    private static History OneDay()
    {
        var h = new History();
        var day = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
        h.Push(new History.Record { AtUnixMs = day.ToUnixTimeMilliseconds(), Taken = true });
        h.Push(new History.Record { AtUnixMs = day.AddHours(2).ToUnixTimeMilliseconds(), Taken = false });
        h.Push(new History.Record { AtUnixMs = day.AddHours(5).ToUnixTimeMilliseconds(), Taken = true });
        return h;
    }

    /// <summary>造一份<b>真的</b>读不通（走 <see cref="History.Load"/>，不手工塞旗），
    /// 用完就删那个临时目录 —— 手工造旗量的是「旗为真时窗摆什么」，
    /// 而「旗怎么立的」是 <see cref="History"/> 那一组量的，两边不重抄。</summary>
    private static History ReadAsUnreadable()
    {
        var root = System.IO.Path.Combine(Tmp.Root,
            $"keeper-history-window-{System.Environment.ProcessId}");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var path = System.IO.Path.Combine(root, "broken.jsonc");
            System.IO.File.WriteAllText(path, "{ \"records\": [ { \"atUnixMs\": 1, \"taken\": ");
            return History.Load(path);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// 自检里的判据（组名 <c>history-window</c>）。**不碰 %APPDATA%**：读不通那一档用一个
    /// 临时目录里真的坏掉的一份。
    public static List<string> SelfTest()
    {
        var checks = new Checks("history-window");
        // **先装语言表**：没装上的时候 <c>Lang.T(键)</c> 原样吐回键名，下面那几条
        // 「摆的是不是那一句」就变成在问「键名对不对」—— 量不到界面上的措辞。
        if (Lang.Apply("zh").Count > 0) return ["语言表没装上，这一组量的不是界面上那些字"];

        checks.Check("有记录：一天一行，那天那几次挂在它底下", () =>
        {
            var win = New(OneDay());
            win.Fill();
            var (rows, items, said) = Look(win);
            if (said != null) throw new Exception($"有记录却摆了「{said}」那一档");
            if (rows != 1) throw new Exception($"摆出 {rows} 天，该是 1 天");
            if (items != 3) throw new Exception($"摆出 {items} 次，该是 3 次");

            var tree = win.GetNode<Tree>(DaysPath);
            var day = tree.GetRoot().GetChild(0);
            var line = day.GetText(0);
            if (!line.Contains("2026-10-08"))
                throw new Exception($"那一天那一行没有日期：{line}");
            if (!line.Contains("提示 3 次") || !line.Contains("休息过 2 次"))
                throw new Exception($"那一天那一行没有那两个数：{line}");
            if (!day.Collapsed) throw new Exception("一天那一行默认是展开的 —— 一周几十行会铺一屏时刻");
            if (day.GetChildCount() != 3) throw new Exception("那天那几次没挂在它底下");
            // 两层，不做第三层：时刻那一层底下不该再有东西。
            if (day.GetChild(0).GetChildCount() != 0) throw new Exception("做了第三层");
        });

        checks.Check("没记录说「还没记过休息」，不是一张空表", () =>
        {
            var win = New(new History());
            win.Fill();
            var (rows, items, said) = Look(win);
            if (said != Lang.T("还没记过休息")) throw new Exception($"摆的是「{said ?? "(什么都没说)"}」");
            if (rows != 0 || items != 0) throw new Exception("没记录却摆出了行");
        });

        checks.Check("读不通说「休息记录读不通」，和「还没记过」不是同一句", () =>
        {
            var history = ReadAsUnreadable();
            if (!history.Unreadable) throw new Exception("这一发的前提不成立：那份居然读得通");

            var broken = New(history);
            broken.Fill();
            var (rows, items, said) = Look(broken);
            if (said != Lang.T("休息记录读不通"))
                throw new Exception($"读不通那一档摆的是「{said ?? "(什么都没说)"}」");
            // **这一条是这一族要防的那一个假象**：两句一样的话，两种真相读起来没有分别。
            if (said == Lang.T("还没记过休息")) throw new Exception("读不通被读成了「没休息过」");
            if (rows != 0 || items != 0) throw new Exception("读不通却摆出了行");
            // 只说一句「读不通」人也不知道自己的记录还在不在，所以下半句要告诉它
            // 「没有抹掉，也没有往里写」—— 而那是这一档和「没休息过」最要紧的差别。
            var full = broken.GetNode<Label>(BlankPath).Text;
            if (!full.Contains(Lang.T("这一份程序没有抹掉，也没有往里写新的一条。")))
                throw new Exception("读不通那一档没说你那份记录还在不在");
        });

        checks.Check("界面上没有毫秒，也没有记录文件的名字", () =>
        {
            // 那两句里不许出现文件名（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」是通则），
            // 而时刻那一行不许出现「毫秒」——两者都是被写成文字的形状。
            var said = Lang.T("休息记录读不通") + Lang.T("还没记过休息") +
                       HistoryDays.DayOf(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero))
                           .ToString("yyyy-MM-dd");
            if (said.Contains(History.FileName, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"界面上出现了记录文件的名字：{History.FileName}");
            if (said.Contains("毫秒", StringComparison.Ordinal)) throw new Exception("界面上出现了「毫秒」");
        });

        checks.Check("一天那一次只摆时刻和结果两样（不记时长）", () =>
        {
            var at = new DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.FromHours(8));
            var taken = EntryLine(new HistoryDays.Entry(at, true));
            if (!taken.Contains("14:30")) throw new Exception($"时刻没摆出来：{taken}");
            if (!taken.Contains(Lang.T("休息过了"))) throw new Exception($"结果没摆出来：{taken}");
            var skipped = EntryLine(new HistoryDays.Entry(at, false));
            if (!skipped.Contains(Lang.T("跳过了"))) throw new Exception($"跳过那一档没摆出来：{skipped}");
            if (taken == skipped) throw new Exception("休息过了和跳过了摆成同一句 —— 那两种结果分不出来");
        });

        return checks.Report();
    }

    // ---- 两个入口（工单 03）------------------------------------------------------

    /// <summary>两个入口那一组（组名 <c>history-entry</c>）。</summary>
    ///
    /// <b>这一组量的是「两处入口在不在、都通到那一个动作上」</b>；工单要的
    /// 「问句要的是同一个 HWND」那一发是<b>只能靠活程序量的</b> —— 它要一个真的
    /// <c>App</c> 开着、真的按两处、看两个 HWND 是不是同一个。
    /// 无头自检里没有 <c>App</c>（那一组量的是部件之间的接线，不是产品的运行期状态），
    /// 那一发的实测结果记在 <c>.scratch/rest-history/issues/03-two-entries.md</c>。</summary>
    internal static readonly CriterionOnHost Entry = new(
        () => "休息历史两个入口通过：托盘「休息历史…」那一行在、id 就是那一个、按下去只触发它自己；" +
              "设置窗窗头「历史」那一格常驻在（不许被藏）、那一格里的卡片在场；" +
              "两处通到的是同一个动作（都落到那一个计数器上）",
        "休息历史两个入口", EntrySelfTest);

    /// 一份只够摆菜单、其余全落空的钩子。<b>除了 <see cref="History"/> 之外的每一项都记下自己</b> ——
    /// 判据要问的是「按那一行触发的是不是它自己」，而那只在别的项各记各的名字时才问得出来。
    private static Tray.TrayHooks Hooks(List<string> fired) => new(
        Show: () => fired.Add("进入休息"), Calendar: () => fired.Add("桌面日历"),
        Auto: () => fired.Add("自动淡入"), Options: () => fired.Add("设置"),
        About: () => fired.Add("关于"), Exit: () => fired.Add("退出"),
        SecondLaunch: () => fired.Add("又一发"), Pomodoro: () => fired.Add("番茄钟"),
        Autostart: () => fired.Add("开机自动启动"), History: () => fired.Add("休息历史"),
        SwitchLifeMode: _ => fired.Add("切生活模式"), ManageLifeModes: () => fired.Add("管理生活模式"),
        AutoFadeOn: () => false, PreviewOn: () => false, CalendarOn: () => false,
        PomodoroOn: () => false, AutostartOn: () => false, PomodoroLeftMs: () => 0,
        LifeModes: () => new Tray.LifeModeList([], null, false),
        BlockName: () => null, DndWhy: () => null, BreakLeftMs: () => null, Today: () => new Tray.TodayList([], false));

    /// 自检里的判据（组名 <c>history-entry</c>）。**不碰 %APPDATA%**：设置窗开在临时目录里
    /// （和 <c>nav</c> 那一组同一个 <see cref="OptionsHost"/>）。
    private static List<string> EntrySelfTest(Node parent)
    {
        var checks = new Checks();
        var fired = new List<string>();
        var host = OptionsHost.InTempDir("history-entry");
        try
        {
            System.IO.Directory.CreateDirectory(host.Dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(host.Dir, Settings.FileName),
                $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion}\n}}\n");
            var state = SettingsStore.Load(host.Dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return checks.Report(); }
            if (Lang.Apply(state.Current.Language).Count > 0)
                return ["语言表本身有问题，这一组量的不是界面上那些字"];

            // **这条判据跟着 2026-10-12 那次改动重写了**（工单 desktop-comfort/04）：
            // 顶层那一颗从「点了就开窗」改成**挂一颗子菜单**（悬停展开今天的那些），
            // 所以顶层那一行**没有 id 了**（Windows 对 MF_POPUP 读的 id 恒是 -1），
            // 而开窗那一颗搬进了子菜单的下半。判据跟着量子菜单里那颗 ——
            // 「递那个 id 触发的正是开历史那一发」这件事一点没变。
            checks.Check("托盘「休息历史」挂一颗子菜单，子菜单里「打开休息历史…」按下去只触发它自己", () =>
            {
                var hooks = Hooks(fired);
                var tr = (string key) => Lang.T(key);
                var texts = Tray.ReadRows(hooks, tr);
                var at = texts.FindIndex(t => t == tr("休息历史"));
                if (at < 0)
                    throw new Exception($"托盘上没有「休息历史」那一行（读回来的是：{string.Join(" / ", texts)}）");

                using var tree = Tray.Build(hooks, tr);
                // 顶层那一行是子菜单，所以它不递 id（恒 -1）—— 这一条是判据的一部分：
                // 它红了就说明子菜单没挂上，退回了「点一下开窗」那版。
                var topId = Win32.GetMenuItemID(tree.Root, at);
                if (topId != unchecked((uint)-1))
                    throw new Exception($"顶层「休息历史」那一行递的是 {topId}，它该挂子菜单（id 恒是 -1）");

                // 子菜单里今天一条都没有（这批是空记录）—— 该摆一条灰的，而不是空列表。
                // 顶上第一颗是「打开休息历史…」，记录那几行在它下面。
                var subRows = Tray.ReadSubRows(hooks, tr, "history");
                if (!subRows.Any(t => t.StartsWith(tr("今天还没有休息"), StringComparison.Ordinal)))
                    throw new Exception($"子菜单上半没摆「今天还没有休息」那一行（读回来：{string.Join(" / ", subRows)}）");

                // 动作在子菜单**最上面**（2026-10-12 所有者定夺「放在最上面的」）：
                // 找到那颗，读回它的 id，递那个 id。**次序是判据的一部分** ——
                // 只问「有没有」的话，它掉回底下那一版这条照样是绿的。
                var opened = subRows.FindIndex(t => t.StartsWith(tr("打开休息历史"), StringComparison.Ordinal));
                if (opened < 0)
                    throw new Exception($"子菜单里没有「打开休息历史…」那一颗（读回来：{string.Join(" / ", subRows)}）");
                if (opened != 0)
                    throw new Exception($"「打开休息历史…」该是子菜单顶上第一颗（下标 0），它在第 {opened} 颗" +
                                        $"（读回来：{string.Join(" / ", subRows)}）");
                var sub = tree.Submenus["history"];
                var id = Win32.GetMenuItemID(sub, opened);
                if (id != Tray.IdHistory)
                    throw new Exception($"「打开休息历史…」那一颗的 id 是 {id}，该是 {Tray.IdHistory}");

                fired.Clear();
                Tray.DispatchTo(id, "自检", hooks, _ => { });
                if (fired.Count != 1 || fired[0] != "休息历史")
                    throw new Exception($"按那一颗触发的是「{string.Join("/", fired)}」，该是且只是「休息历史」");

                // 对照（一定该通）：别的 id 走别的动作。少了这一发，这一族可能在
                // 「递什么都落到历史那一行」的状态下照样是绿的。
                fired.Clear();
                Tray.DispatchTo(Tray.IdHistory == 4 ? 5u : 4u, "自检", hooks, _ => { });
                if (fired.Count != 1 || fired[0] == "休息历史")
                    throw new Exception("对照不成立：递别的 id 也触发成了「休息历史」");
            });

            checks.Check("设置窗窗头「历史」那一格常驻在，那一格的卡片在场", () =>
            {
                var win = host.Open(state, parent, w => w.OpenHistory = () => fired.Add("休息历史"));
                var bar = win.GetNodeOrNull<TabBar>("Margin/Stack/Nav/Domains");
                if (bar == null) throw new Exception("窗头没有域标签那一格");
                var at = -1;
                for (var i = 0; i < bar.TabCount; i++)
                    if (bar.GetTabTitle(i) == Lang.T("历史")) at = i;
                if (at < 0) throw new Exception("窗头那一排标签上没有「历史」");

                // 切到那一格，卡片要在场上。**发的是它自己发的那一个信号** ——
                // 真点在标签上走的是同一条（那一族在 nav 那一组里量过）。
                bar.EmitSignal(TabBar.SignalName.TabChanged, at);
                var card = win.GetNodeOrNull<Control>("Margin/Stack/Scroll/Form/History");
                if (card == null) throw new Exception("场景里没有「历史」那一张卡片");
                if (!card.Visible) throw new Exception("切到「历史」那一格，卡片不在场上");
            });

            checks.Check("两处入口通到的是同一个动作", () =>
            {
                var win = host.Open(state, parent, w => w.OpenHistory = () => fired.Add("休息历史"));
                // **同一个计数器**：产品里那一个动作就是 `App.OpenHistory`，两处递的是同一个方法。
                // 两处各写一个「开窗」在这里是量不出来的 —— 量得出来的是「两处都落到这一个上」。
                fired.Clear();
                win.GetNode<Button>(Options.HistoryPath).EmitSignal(Button.SignalName.Pressed);
                Tray.DispatchTo(Tray.IdHistory, "自检", Hooks(fired), _ => { });
                if (fired.Count != 2 || fired[0] != "休息历史" || fired[1] != "休息历史")
                    throw new Exception(
                        $"两处入口落到 {fired.Count} 次（{string.Join("/", fired)}），该是两处各落一次、都是那一个动作");
            });

            checks.Check("这一格一条设置键都不加（声明表一个格子都不占）", () =>
            {
                var win = host.Open(state, parent);
                var rows = win.GetNode("Margin/Stack/Scroll/Form/History/Body")
                    .GetChildren().OfType<Control>().Where(c => c.HasMeta("setting")).ToList();
                if (rows.Count != 0)
                    throw new Exception($"「历史」那一格里带着 {rows.Count} 行设置 —— 它装的是读数不是设置");
            });
        }
        catch (Exception e) { checks.Fail($"这一组自己炸了：{e.Message}"); }
        finally
        {
            host.Dispose();
            checks.Problems.AddRange(host.Problems);
        }
        return checks.Report();
    }
}
