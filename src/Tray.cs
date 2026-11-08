using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace Keeper;

/// <summary>托盘。引擎没有托盘 API，所以这是从 C# 伸手进 shell 的那一处。
///
/// 窗口建在<b>主线程</b>上：一个 0×0 的 WS_POPUP，从不显示，只用来收 shell 的回调消息。
/// 不为它开一条线程去 GetMessage —— 引擎在自己的主循环里派发本线程的消息，低级输入钩子
/// 早就踩在这条事实上（<c>Activity.cs</c>）。这一手到底通不通是量出来的，见
/// docs/research/desktop-layer-and-window-measurements.md「引擎自己的主循环泵」那一条。
///
/// 菜单是每次右键现做的，所以「换了语言菜单还挂着旧语言」这种事在这一侧不存在 ——
/// 现做就是白送的（设计文档 `docs/design/tray.md` 要的就是重建那一下）。</summary>
public sealed class Tray : IDisposable
{
    public enum Mode { Idle, Fading, Disabled }

    /// 那扇 0×0 收件窗的类名 —— <see cref="Instance"/> 按它认出「正在跑的那一发」，
    /// 所以这一条是 <see cref="Tray"/> 对外的脸面，改它要同步那边。
    ///
    /// 标题<b>不</b>是脸面，别拿它去找窗：2026-09-27 量过，递进去 9 个字的中文标题，窗上读回来
    /// 是 <c>'K'</c> 带两个每次都不一样的乱码字（三跑三个样：<c>'K㤿o'</c>、<c>'K킟Þ'</c>、
    /// <c>'K๟'</c>），而同一发里按类名找是稳定找得到的。机制没查出来，记在
    /// .scratch/keeper-debt/issues/06-tray-window-title-garbled.md 那张工单；Keeper 自己一个字都不读这个标题，
    /// 所以先按「只认类名」办，不拿它当地址。
    public const string ClassName = "KeeperTray";
    private const string WindowTitle = "Keeper 托盘";
    private const uint IconId = 1;

    /// 菜单项的 id。<c>TPM_RETURNCMD</c> 会把它原样返回，所以这一份就是分发表的索引。
    private const uint IdShow = 1;
    private const uint IdCalendar = 2;
    private const uint IdAuto = 3;
    private const uint IdOptions = 4;
    private const uint IdAbout = 5;
    private const uint IdExit = 6;

    /// 番茄钟那一条排在「自动淡入」后面：它们是同一族 —— 一个是自动的开关，一个是把
    /// 自动的那一条换成按现实时间走。挨着放，扫菜单的时候不必找。
    private const uint IdPomodoro = 7;

    /// 「开机自动启动」那一条，排在「番茄钟」后面。它也属于「按一下就翻面」那一族，
    /// 而它翻的是**这台机器的配置**，不是这一发此刻在做什么（所以它不在最上面那两行里：
    /// 那两行是「当下是什么」）。
    ///
    /// 这一行<b>不直接写注册表</b>：它改的是 <c>settings.jsonc</c> 里那一个键，然后走
    /// <c>App.ReconcileAutostart</c> —— 和启动时、设置窗刚应用时同一个动作。契约
    /// 「开机启动的意图在文档里，注册表是它的效果」那一条记着这件事。</summary>
    private const uint IdAutostart = 9;

    /// 「管理生活模式…」那一条。它在<b>子菜单里</b>，而 <c>TPM_RETURNCMD</c> 递回来的是
    /// <b>被按的那一项</b>的 id，不管它在哪一层 —— 所以子菜单里那一项也要有自己的 id。
    private const uint IdManageLifeModes = 8;

    /// 「休息历史…」那一条（2026-10-08）。它挨着「设置…」和「关于 Keeper」——
    /// 那两颗是「开一扇窗」，这一颗也是，而它开的那一扇是<b>读记录</b>不是改配置。
    ///
    /// <b>internal 而不是 private</b>：<c>history-entry</c> 那一组要断言「这一行的 id 是这一个」，
    /// 而抄一份号进来就有一份会漂的真相（改了这里忘了那里，症状是「按了没反应」——
    /// 那一族在 <see cref="IdAutostart"/> 的注释里记着）。号段还是那几个手写的常量，
    /// **不进生活模式那 100 起的一段**：那一段是数据，那几颗是手写的。
    internal const uint IdHistory = 10;

    /// 生活模式那几项的 id 从这里往上发。**它是一个号段，不是一个项数**（设计文档 `docs/design/life-mode.md`「菜单里不数项」：
    /// 名单多长都不改这一行）。留这么一段是因为上面那几颗是手写的常量，而名单是数据 ——
    /// 两边撞号的话，递回来的 id 会落到另一行上，而症状是「按了没反应」或者「按了动别的」。
    /// 从 100 起就是让它们离那几颗足够远，实测结果里一眼能看出递回来的是哪一族。
    private const uint IdLifeModeFirst = 100;

    private const uint WmNull = 0;

    // 递进非托管代码的委托得有个托管引用留着，不然 GC 一收，shell 回调就是野指针。
    private readonly Win32.WindowProc _wndProc;
    private readonly Action<string> _log;
    private readonly Func<string, string> _tr;
    private readonly Dictionary<Mode, nint> _icons = [];
    private nint _hwnd;

    /// 屏幕上现在挂的是哪一张。图标要跟着设置走，<c>TaskbarCreated</c> 之后重挂也要照着它来，
    /// 所以这一份得留着 —— 否则 explorer 一重启，托盘就回到起步那张 Idle，「自动淡入关着」
    /// 那台机器上的灰色图标会翻回绿色。
    private Mode _mode = Mode.Idle;

    /// shell 广播「任务栏刚建好」用的消息号，构造时现问一次（每个 session 不一样）。
    private readonly uint _taskbarCreated;

    /// <summary>菜单要的那些动作和问句，<b>一次交齐</b>。
    ///
    /// 2026-09-29 之前这些是事后填的公有委托字段，而唯一那个调用方
    /// （<c>App._Ready</c>）把它们<b>全填了</b>。于是接口要求调用方知道全部，
    /// 而「没接怎么办」那两族分支（<c>enabled: XClicked != null</c> 和 <c>?? true</c> /
    /// <c>?? false</c>）<b>没有读者</b> —— 那段注释已经从讲现场变成讲历史。
    /// 收成一个值记录之后，完整性进类型：少一条编译不过，而不是运行期灰掉一行。
    /// Tray 是普通类，不是挂在场景上的脚本，所以进构造参数<b>没有引擎挡路</b>
    /// （<c>Options</c> / <c>About</c> 那几个事后字段是另一回事：<c>.tscn</c> 是引擎的形状，
    /// 那一侧不动）。
    ///
    /// <b>不数几样</b>：加一条动作或一条问句都不改这一行注释 —— 数出来的数字一过期
    /// 就是一句假话，读者还当现状（和「菜单里不数项」同一条口径）。要几样数那张表。
    ///
    /// <b>生活模式那几样</b>（<see cref="SwitchLifeMode"/> / <see cref="ManageLifeModes"/> /
    /// <see cref="LifeModes"/>）是工单 04 加的：子菜单里上半是名单（数据）、下半一条分隔线加
    /// 「管理生活模式…」。<b>托盘这一侧只做「切」</b> —— 新建要输名字、覆盖要确认，
    /// 那两条都在设置窗里（设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那条）。
    ///
    /// <b><see cref="DndWhy"/> 是勿扰那一行</b>（工单 03）：<c>null</c> = 那一行不出现，
    /// 一句话 = 那一行摆着「勿扰中 + 因为哪一条」—— 和 <see cref="BlockName"/> 同一形状
    /// （实测结果那一行要么带内容、要么不在那里），而 <see cref="PomodoroLeftMs"/> 是另一半形状
    /// （永远在，只是后半句可空）。
    ///
    /// <b><see cref="Autostart"/> / <see cref="AutostartOn"/> 是开机启动那两样</b>：一个动作、
    /// 一个问句，摆法和「自动淡入」那对一模一样。**这个动作的契约是不许直接写注册表** ——
    /// 它只许改那一个键，让调用方接着走对账。理由和写法在 <see cref="IdAutostart"/> 上面。
    ///
    /// <b><see cref="History"/> 是「休息历史…」那一条</b>（2026-10-08）：和「设置…」「关于 Keeper」
    /// 同一形状 —— 按一下开一扇窗，而<b>已经开着就提到前面</b>，那个「已经开着」的判断
    /// 在 <c>App</c> 那一侧，所以这一条交出去的是<b>同一个动作</b>，托盘和设置窗那两颗按钮
    /// 一起调它（设计文档 `docs/design/records.md`「两个入口，同一份数据」：两处各开一次就是两扇窗，
    /// 而那种不一致在界面上看不出是哪一边错了）。</summary>
    public sealed record TrayHooks(
        Action Show, Action Calendar, Action Auto, Action Options, Action About, Action Exit,
        Action SecondLaunch, Action Pomodoro, Action Autostart, Action History,
        Action<string> SwitchLifeMode, Action ManageLifeModes,
        Func<bool> AutoFadeOn, Func<bool> PreviewOn, Func<bool> CalendarOn,
        Func<bool> PomodoroOn, Func<bool> AutostartOn,
        Func<long> PomodoroLeftMs, Func<LifeModeList> LifeModes,
        Func<string?> BlockName, Func<string?> DndWhy, Func<long?> BreakLeftMs,
        Func<TodayList> Today);

    /// <summary>「生活模式」那一颗子菜单上半要的那一份。<b>名单是数据</b>，所以它在打开菜单那一刻
    /// 现问（和整份菜单现做是同一条规矩，换语言、换生活模式都不用重建）。
    ///
    /// <see cref="Current"/> 是<b>问出来的</b>（<c>LifeModeStore.StandingOf</c> 拿活的那一份和
    /// 每个生活模式那一份比一遍），不是托盘自己记的 —— 「我现在在哪个生活模式」不落盘，
    /// 托盘要是自己存一份那就是第二份真相。
    ///
    /// <see cref="Unreadable"/> 单独一档：<b>名单读不通 ≠ 一个生活模式都没有</b>。那一档
    /// 在子菜单上半摆一条**灰的**「生活模式名单读不通」，而不是摆一个空列表 ——
    /// 空列表读起来就是「你没存过生活模式」，而磁盘上那份读不通的人明明存过
    /// （工单 03 在设置窗那一侧量的就是这一对假象，托盘这一侧不能重犯）。</summary>
    public sealed record LifeModeList(IReadOnlyList<string> Names, string? Current, bool Unreadable);

    /// <summary>「休息历史」那个悬停子菜单上半要的那一份（工单 <c>desktop-comfort/04</c>）。
    ///
    /// <b>记录是数据，所以它现问</b> —— 和整份菜单现做、生活模式名单现问是同一条规矩。
    /// <b>从旧到新</b>（所有者定的次序，最旧那条在最上），而
    /// <see cref="HistoryDays.Reading"/> 给的那一份是<b>按时刻倒序</b> —— 两者相反，
    /// 所以这里翻过来，**翻的动作在这一发里做一次**，别在两处各翻一次（那两处会漂）。
    ///
    /// <b><see cref="Unreadable"/> 单独一档</b>：读不通 ≠ 今天没休息过，理由和
    /// <see cref="LifeModeList.Unreadable"/> 同款。</summary>
    public sealed record TodayList(IReadOnlyList<TodayEntry> Entries, bool Unreadable);

    /// <b>今天的一次休息</b>。<see cref="Taken"/> 为假是<b>跳过</b>（遮罩没站够久）——
    /// 那是所有者要显示的第二种，两种都显示。</summary>
    public sealed record TodayEntry(DateTimeOffset At, bool Taken);

    /// <summary>建出来那棵树上**顶层那些项的字**，一行一项（分隔线读到的是空串）。
    ///
    /// 自检那一族量「那一行在不在」时的尺子：<b>数的是项</b>，不搜字 —— 一个空着的
    /// 「时间块」照样是一项，只搜字会放过它（理由和 <see cref="BlockRowSelfTest"/> 那段注释
    /// 一样）。所以这里返回的是整份列表，「在不在」「项数差一」「那一项上是什么」三样都从它来，
    /// 而 <c>BlockRowSelfTest</c> 和 <c>Scheduler.DndSilenceSelfTest</c> 用的是<b>同一把</b>。</summary>
    public static List<string> ReadRows(TrayHooks hooks, Func<string, string> tr)
    {
        using var tree = Build(hooks, tr);
        var texts = new List<string>();
        for (var p = 0; p < Win32.GetMenuItemCount(tree.Root); p++) texts.Add(Str(tree.Root, p));
        return texts;
    }

    /// <summary>一颗<b>子菜单</b>上那些项的字，一行一项 —— <see cref="ReadRows"/> 的子菜单那一版。
    ///
    /// 「休息历史」那个悬停子菜单（工单 <c>desktop-comfort/04</c>）挂上去之后，
    /// 自检要读的就是它底下的那几行，而 <see cref="Str"/> 是私有的。
    /// **两处读法各有一个出处**：顶层那些项走 <see cref="ReadRows"/>，子菜单里那些走这一发
    /// —— 两处各写一遍「读一行字」的话，改了壳子的读法只改了一处，判据就在另一处读出假的。</summary>
    public static List<string> ReadSubRows(TrayHooks hooks, Func<string, string> tr, string alias)
    {
        using var tree = Build(hooks, tr);
        if (!tree.Submenus.TryGetValue(alias, out var sub) || sub == IntPtr.Zero)
            throw new Exception($"树上没有一颗叫「{alias}」的子菜单");
        var texts = new List<string>();
        for (var p = 0; p < Win32.GetMenuItemCount(sub); p++) texts.Add(Str(sub, p));
        return texts;
    }

    /// <summary>菜单里的<b>一颗子菜单</b>该有哪些项 —— 上半生活模式名单（当前那个打着勾），
    /// 下半一条分隔线加「管理生活模式…」（设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」那一条：
    /// 托盘那层子菜单的形状和设置窗那颗按钮展开出来的一样）。
    ///
    /// <b>上半是数据</b>，所以这一族是<b>现造</b>的：每个生活模式的项闭包住自己的名字，
    /// 勾是摆上去那一刻问的（<see cref="LifeModeList.Current"/>）。下半是我们自己的两句话。
    ///
    /// <b>名单读不通那一档</b>：上半不是空的，是一条灰的「生活模式名单读不通」，而且它
    /// <b>没有 id</b>（点不动）—— 摆一颗按了没反应的项比不摆更糟，而摆一个空列表
    /// 是把一份坏文件说成「你没存过」。这一条措辞和设置窗那一侧<b>共用同一个键</b>。
    ///
    /// <b>菜单里不数项</b>：项数从 <see cref="LifeModeList.Names"/> 数出来，日志里印的是
    /// 名单上那几条的名字，不是「上半三项」这种会抄过期的数字。</summary>
    private static List<Row> LifeModeRows(TrayHooks h)
    {
        var list = h.LifeModes();
        var rows = new List<Row>();
        // 读不通那一档**不列名单**：读不通的名单不是「一份可以照着切过去的名单」，
        // 而把它照常列出来就是给一份坏文件开出一排点得动的项（每点一下就是一次失败的切）。
        if (list.Unreadable)
            rows.Add(new Row(0, "生活模式名单读不通", ["unreadable"], null, null, false, Grayed: true));
        else
            for (var i = 0; i < list.Names.Count; i++)
            {
                // 名字抄进闭包：这一项和它的动作要说的是同一个名字，而名单随时会变（下一次右键
                // 就是新的一份），所以不能靠一个「按下标反查名字」的第二份表。
                // Raw: 生活模式名**不翻**（契约：用户自己起的名字不查语言表），所以这一行的 Key
                // 是数据不是键，摆的时候不过 _tr。
                var name = list.Names[i];
                rows.Add(new Row(IdLifeModeFirst + (uint)i, name, [$"生活模式:{name}"], x => x.SwitchLifeMode(name),
                    _ => name == list.Current, false, Raw: true));
            }
        rows.Add(new Row(IdManageLifeModes, "管理生活模式…", ["manage", "管理"], x => x.ManageLifeModes(), null,
            rows.Count > 0));
        return rows;
    }

    /// <summary>「休息历史」那个悬停子菜单（工单 <c>desktop-comfort/04</c>）。
    ///
    /// <b>形状和 <see cref="LifeModeRows"/> 同款</b>：上半是数据、下半分隔线加一颗自己的动作。
    /// 上半是<b>今天的记录</b>，从旧到新（所有者定的次序）。
    ///
    /// <b>2026-10-12 所有者定夺：动作那颗摆最上面</b>（原话：「『打开休息历史』这个按钮
    /// 我明明要求是放在最上面的！」）。所以现在是<b>顶上第一颗是动作，下面一条分隔线，
    /// 再往下才是今天那些记录</b>—— 和生活模式那一族是同一个排法。
    /// 原来动作在底下、记录在上半，那是第一次做这一族时的排法。
    ///
    /// <b>那几条记录点不动也不递 id</b> —— 它们是「现在是什么」，不是动作（和「时间块」
    /// 「勿扰中」那两行同一形状）。
    ///
    /// <b>两档空列表分得开</b>：读不通摆一条灰的「休息记录读不通」；今天一条都没有摆一条
    /// 灰的「今天还没有休息」。摆一个空列表是把两者一起说成「你没记过」。
    ///
    /// <b>菜单里不数项</b>：条数从 <see cref="TodayList.Entries"/> 数出来。</summary>
    private static List<Row> TodayRows(TrayHooks h)
    {
        var list = h.Today();
        var rows = new List<Row>();
        // **顶上第一颗就是动作**：开完整记录的那一扇（和点托盘那一行开的是同一扇，
        // 设计文档 `docs/design/records.md`「两个入口，同一份数据」）。它自己是菜单里唯一能按下去的一颗，
        // 所以放在最上面 —— 指过来第一眼看到的就是它。
        rows.Add(new Row(IdHistory, "打开休息历史…", ["history-all", "全部历史"], x => x.History(), null, false));
        var records = new List<Row>();
        if (list.Unreadable)
            records.Add(new Row(0, "休息记录读不通", ["unreadable"], null, null, false, Grayed: true));
        else if (list.Entries.Count == 0)
            records.Add(new Row(0, "今天还没有休息", ["empty", "今天"], null, null, false, Grayed: true));
        else
            // Raw: 时刻和「休息过了 / 跳过了」是排出来的字，不是语言表的键 —— 不过 _tr。
            foreach (var entry in list.Entries)
                records.Add(new Row(0, RowTextOf(entry), [$"today:{entry.At:HH:mm}"], null, null, false,
                    Grayed: true, Raw: true));
        // 记录那几行各自带上「前面有一条分隔线」，摆进去之后它们和顶上那颗分成两组。
        if (records.Count > 0)
        {
            records[0] = records[0] with { SeparatorBefore = true };
            rows.AddRange(records);
        }
        return rows;
    }

    /// 一条记录摆成的那一行字：时刻 + 休息过了还是跳过了。<b>不查语言表</b>
    /// —— 它和 <see cref="Row.Raw"/> 那一处是同一条规矩（数据不是键）。
    private static string RowTextOf(TodayEntry entry)
    {
        var clock = DateTimeOffset.Now.Date == entry.At.Date
            ? entry.At.ToString("HH:mm")
            : entry.At.ToString("MM-dd HH:mm");
        return $"{clock}　{(entry.Taken ? "休息过了" : "跳过了")}";
    }

    /// <summary>菜单上的一行。<b>整个菜单只有这一份枚举</b> —— 2026-09-29 之前是三份：
    /// <see cref="Build"/> 里一串 <c>Item</c>、<see cref="DispatchTo"/> 里一个 switch、
    /// <see cref="ProbeMenu"/> 里一个名字→id 的 switch。加一行要在三处各写一段，
    /// 而且三处的顺序可以各自漂。
    ///
    /// <b>不数几行</b>：<see cref="Row.Id"/> 上面的号段是给数出来的，不是给抄出来的。
    ///
    /// <paramref name="Grayed"/> 是这一行<b>点不动</b>（<c>MF_GRAYED</c>，仍然画得出来）。
    /// 它和 <c>Run == null</c> 是两件事：<c>Run == null</c> 说的是「这一行不递动作」，
    /// <c>Grayed</c> 说的是「按下去也不会有事发生」—— 摆灰的那一颗不递 id，
    /// 所以两条一起设，只设一条会出现「画成能按、其实按了没反应」或者反过来。
    ///
    /// <paramref name="Raw"/> 说 <paramref name="Key"/> 是<b>数据</b>（用户自己起的生活模式名），
    /// 不是语言表的键，摆的时候不过 <c>_tr</c>。它和「生活模式名不翻」那一条是同一件事。
    ///
    /// <paramref name="Submenu"/> 非空时这一行<b>挂的是一颗子菜单</b>：<c>Id</c> 就不递了
    /// （Windows 那边对 <c>MF_POPUP</c> 那一项读的 id 恒是 -1），要递的是子菜单的 <c>HMENU</c>，
    /// 走 <see cref="Win32.AppendMenuHandle"/> 那一条指针宽的形态。
    ///
    /// <paramref name="Run"/> 可以是 null，而 null 和 <see cref="Grayed"/> 是<b>两件事</b>：
    /// 子菜单那一行和「读不通」那一条都没有动作可递。</summary>
    private sealed record Row(uint Id, string Key, string[] Aliases, Action<TrayHooks>? Run,
        Func<TrayHooks, bool>? Checked, bool SeparatorBefore, Func<TrayHooks, string>? Note = null,
        bool Grayed = false, bool Raw = false, Func<TrayHooks, List<Row>>? Submenu = null);

    /// <summary>顶上那几行，<b>每次现问</b>：<see cref="TrayHooks.BlockName"/> 给了名字才有
    /// 「时间块」那一行，没有就<b>不摆</b> —— 「没有作息表、或此刻不在任何块里，这一行不出现」
    /// （设计文档 `docs/design/daily-routine.md`「作息表是一张按现实时间走的表」那一节）。它是<b>唯一一个会消失的行</b>：
    /// 番茄钟那一行永远在，只是后半句可能是空的（那样仍是菜单上的一行），开机启动那一行
    /// 永远在、只是勾有开没有，而「时间块」要么带着块名、要么不在那里。摆一个空的「时间块」
    /// 是第三种样子 —— 那会把「此刻不在任何块里」说成一行标题。
    ///
    /// 菜单是现做的（<see cref="Build"/>），所以「换语言免费」和「块换了名下一次右键就是新的」
    /// 在这一行上是同一件事，不另开一份要刷的缓存。</summary>
    private static List<Row> Rows(TrayHooks h)
    {
        var rows = new List<Row>();
        // **「还有多久进休息」摆在菜单顶上第一行**（2026-10-08）。它是菜单要答的第一个问题：
        // 「我现在该继续干，还是该歇了」—— 底下那几颗都是动作（能按下去），
        // 只有它答的是**当下是什么**，和「时间块」「勿扰中」那两行同一个形状
        // （要么带着内容、要么不在那里，不是空着的一行）。
        //
        // **为什么放顶上而不是跟着那两行**：那两行（时间块 / 勿扰中）是「静默」那一族的，
        // 不静默的时候它们不在；这一行是**常态**，它每一趟右键都在，而且问的是
        // 「下一次休息」，那正是开这个菜单的人最常要答的一个问题（契约：
        // 触发量的是这一段走了多久，而「还差多久」这件事不看屏幕看不出来）。
        // 顿号那一行之前不摆分隔线 —— 它不是「一组动作里的一颗」。
        var left = h.BreakLeftMs();
        if (left is not null)
            rows.Add(new Row(0, "break.left", ["break", "还剩", "休息"], null, null, false,
                x => "  " + Clock(x.BreakLeftMs()!.Value), Grayed: true));
        rows.AddRange(new List<Row>
        {
            new Row(IdShow, "进入休息", ["show", "遮罩"], h => h.Show(), h => h.PreviewOn(), false),
            new Row(IdCalendar, "桌面日历", ["calendar", "日历"], h => h.Calendar(), h => h.CalendarOn(), false),
            new Row(IdAuto, "自动淡入", ["auto", "自动淡入"], h => h.Auto(), h => h.AutoFadeOn(), true),
            new Row(IdPomodoro, "番茄钟", ["pomodoro", "番茄钟"],
                h => h.Pomodoro(), h => h.PomodoroOn(), false,
                h => h.PomodoroLeftMs() > 0 ? "  " + Clock(h.PomodoroLeftMs()) : ""),
            // 「开机自动启动」挨着「番茄钟」：它也是按一下就翻面的那一种。**键和设置窗
            // 那一行是同一个键**（语言表里同一个词），所以两处说的是同一件事、同一句话。
            new Row(IdAutostart, "autostart", ["autostart", "开机自动启动"],
                h => h.Autostart(), h => h.AutostartOn(), false),
        });
        // 「时间块」这一行和「番茄钟」挨着：它们是菜单顶上那两行**当下是什么**的实测结果，
        // 底下那些是能按的。Id 是 0 且 Run 是 null —— 它不递动作（分发表按 Run 认，
        // 所以 Esc 递回来的 0 落在这里也什么也不触发），而 Grayed 说清「这一行点不动」。
        var block = h.BlockName();
        if (!string.IsNullOrEmpty(block))
            rows.Add(new Row(0, "时间块", ["block", "时间块"], null, null, false,
                x => "  " + x.BlockName(), Grayed: true));
        // 「勿扰中」那一行和「时间块」挨着：它是**当下是什么**的实测结果，同样是「要么带着内容、
        // 要么不在那里」那一种（不成立的时候整个不出现，而不是摆一个空着的「勿扰中」）。
        // 设计文档 `docs/design/dnd.md`「勿扰生效时托盘提示带半句「勿扰中」——界面不说谎，静默本身要说一声」：
        // 静默的那一会儿菜单上要看得见，不然用户只看到「提醒怎么不来了」。
        var dnd = h.DndWhy();
        if (!string.IsNullOrEmpty(dnd))
            rows.Add(new Row(0, "勿扰中", ["dnd", "勿扰"], null, null, false,
                x => "  " + x.DndWhy(), Grayed: true));
        // 「生活模式」挨着「设置…」：生活模式是配置，托盘这一层只管切，新建和覆盖在那扇窗里。
        // 它的 Id 是 0 —— 它挂的是子菜单，Windows 那边对 MF_POPUP 读的 id 恒是 -1，递回来的 id
        // 只可能来自子菜单里面那几项（Run == null 守着这一行，Esc 递 0 也落空）。
        rows.Add(new Row(0, "生活模式", ["lifemodes", "生活生活模式"], null, null, true, Submenu: LifeModeRows));
        // 「休息历史」挨着「设置…」和「关于 Keeper」，**但它从 2026-10-12 起挂的是一颗子菜单**
        // （工单 desktop-comfort/04，所有者定夺「悬停就展开今天的那些」）：指过去就展开，
        // 不用点开一扇窗才能看到今天歇了没。子菜单里**顶上第一颗是「打开休息历史…」**
        // （所有者定夺「放在最上面」），下面一条分隔线，再往下才是今天的那些记录
        // （从旧到新）。开完整记录的还是原来那一扇（设计文档 `docs/design/records.md`「两个入口，同一份数据」）。
        // 它的 Id 是 0 —— 子菜单那一行不递 id（Windows 对 MF_POPUP 读的 id 恒是 -1），
        // 递回来的 id 只可能来自子菜单里面那几项，而里面除了动作那一颗 Run 都是 null。
        rows.Add(new Row(0, "休息历史", ["history", "历史"], null, null, true, Submenu: TodayRows));
        rows.Add(new Row(IdOptions, "设置…", ["options", "设置"], h => h.Options(), null, false));
        rows.Add(new Row(IdAbout, "关于 Keeper", ["about", "关于"], h => h.About(), null, false));
        rows.Add(new Row(IdExit, "退出", ["exit", "退出"], h => h.Exit(), null, true));
        return rows;
    }

    /// <summary>托盘「时间块」那一行要显示的名字；<c>null</c> = 那一行不出现。
    ///
    /// 三件事在这里合成一个答案，而它们各自只有一个出处：<b>总开关</b>（<see cref="Settings.RoutineEnabled"/>）、
    /// <b>此刻在哪个块</b>（<see cref="Routine.At"/>，纯算术、无状态）、<b>那一行叫什么</b>
    /// （<see cref="TimeBlock.Name"/>）。App 和自检里都走这一个方法，所以「此刻不在块里」和
    /// 「总开关关着」这两种「没有」在菜单上是同一种样子 —— 都是那一行不出现。</summary>
    public static string? BlockRowName(Settings settings, int minuteOfDay) =>
        settings.RoutineEnabled ? Routine.At(settings.Blocks, minuteOfDay)?.Name : null;

    /// <summary>托盘「勿扰中」那一行要显示的那半句；<c>null</c> = 那一行不出现。
    ///
    /// <b>三件事在这里合成一个答案</b>，和 <see cref="BlockRowName"/> 同一处置：
    /// <b>总开关</b>（<see cref="Settings.DndEnabled"/>）、<b>此刻成不成立、因为哪一条</b>
    /// （<see cref="Dnd.Of"/>，纯求值、无状态）、以及<b>那句话的措辞</b>。
    ///
    /// <b>为什么措辞住在托盘这一侧</b>：<see cref="Dnd"/> 交出来的是**结构**（哪一条时段 /
    /// 名单里哪个 exe），而界面上每一条 Keeper 自己的措辞都出自语言表，exe 名是用户写的数据、
    /// 原样显示（设计文档 `docs/design/ui-text.md` 那条界线）。所以这里是「语言表 + 数据」拼成的一句，
    /// 而 <see cref="Dnd.Describe"/> 是给日志看的那一句（不翻，句法和检测那一族同款）。
    ///
    /// <b>两种「因为」分开说</b>：时段那一半是几点到几点、星期几（数据 + 我们的词），
    /// 名单那一半就是那个 exe 名本身（纯数据，一个字都不加工）。</summary>
    public static string? DndWhy(Settings settings, DateTime now, Foreground.Measurement foreground) =>
        Dnd.Of(settings, now, foreground) is { On: true } measurement
            ? measurement.Window is { } window
                ? Lang.T("dnd.why.window")
                    .Replace("{0}", $"{Routine.Clock(window.StartMinute)}–{Routine.Clock(window.EndMinute)}")
                    .Replace("{1}", Dnd.Days(window))
                : Lang.T("dnd.why.app").Replace("{0}", measurement.Exe ?? "")
            : null;

    /// <summary>建出来的那棵树：顶层 + 每一颗子菜单。<b>它自己管着销毁</b>，
    /// 顺序是<b>先子后父</b> —— 父菜单一销毁，挂在它上面的子菜单句柄就作废了，
    /// 所以反过来销毁等于漏掉一层（工单 04 把欠账 03 那条记厚的那一层）。
    ///
    /// <paramref name="Submenus"/> 按那一行的<b>头一个别名</b>（也就是测量程序和自检寻址用的那个词）
    /// 交出子菜单句柄：自检要拿它问 <c>GetSubMenu</c> 读回的是不是同一个句柄 ——
    /// 指针被截掉的话这一发立刻对不上（那正是 <see cref="Win32.AppendMenuHandle"/>
    /// 存在的那条理由）。</summary>
    public sealed class MenuTree : IDisposable
    {
        private readonly List<nint> _children = [];
        private readonly Dictionary<string, nint> _submenus = [];

        internal MenuTree(nint root) => Root = root;

        /// <summary>顶层那颗，<c>TrackPopupMenu</c> 吃的就是它。</summary>
        public nint Root { get; }

        /// <summary>按别名交出子菜单句柄。表上没有的别名不在里面（不是空集合）。</summary>
        public IReadOnlyDictionary<string, nint> Submenus => _submenus;

        internal void Adopt(nint child, string name)
        {
            _children.Add(child);
            _submenus[name] = child;
        }

        public void Dispose()
        {
            foreach (var child in _children) Win32.DestroyMenu(child);
            _children.Clear();
            _submenus.Clear();
            if (Root != IntPtr.Zero) Win32.DestroyMenu(Root);
        }
    }

    /// <summary>把顶上那几行（<see cref="Rows"/>）<b>建</b>成一棵真的菜单树：不弹，只造。
    ///
    /// 真菜单（<see cref="ShowMenu"/>）和自检量接线（<see cref="LifeModeMenuSelfTest"/>）走的是
    /// **这一个方法** —— 「我们递进去的那棵树对不对」在两边是同一棵，
    /// 所以自检里那组实测结果量的就是产品跑的那棵树，而不是自检自己重摆一份像它的。
    ///
    /// <paramref name="tr"/> 是语言表那支笔；自检里递的是恒等函数，因为语言表自己
    /// 已经被 <c>lang</c> 那一组量过了，在这里再量一遍就是量两遍。
    ///
    /// <b>菜单里不数项</b>：这里不数、不印，项数由 <c>GetMenuItemCount</c> 问出来 ——
    /// 日志里出现「七项」这种数字，名单一长它就是一句假话。</summary>
    public static MenuTree Build(TrayHooks hooks, Func<string, string> tr)
    {
        var root = Win32.CreatePopupMenu();
        if (root == IntPtr.Zero) throw new InvalidOperationException("建托盘菜单失败");
        var tree = new MenuTree(root);
        foreach (var row in Rows(hooks))
        {
            if (row.SeparatorBefore) Win32.AppendMenu(root, Win32.MfSeparator, 0, null);
            // 现做的那半句（番茄钟的「还剩 18:42」、时间块的「晨间写作」）接在翻好的那一句后面。
            var text = Text(row, hooks, tr);
            if (row.Submenu == null)
            {
                Item(root, row.Id, text, row.Checked?.Invoke(hooks) ?? false, row.Grayed);
                continue;
            }
            var child = Win32.CreatePopupMenu();
            if (child == IntPtr.Zero) throw new InvalidOperationException("建生活模式子菜单失败");
            tree.Adopt(child, row.Aliases[0]);
            foreach (var sub in row.Submenu(hooks))
            {
                if (sub.SeparatorBefore) Win32.AppendMenu(child, Win32.MfSeparator, 0, null);
                Item(child, sub.Id, Text(sub, hooks, tr), sub.Checked?.Invoke(hooks) ?? false, sub.Grayed);
            }
            // 这一发就是「原生那一侧要补两样」的第二样：MF_POPUP 那一项的第三个形参是
            // 子菜单的 HMENU，而它是指针宽的（winuser.h:7692）。
            if (!Win32.AppendMenuHandle(root, Win32.MfPopup | Win32.MfString, child, text))
                throw new InvalidOperationException($"挂生活模式子菜单失败 {Marshal.GetLastPInvokeError()}");
        }
        return tree;
    }

    /// 那一行的字。<paramref name="Row.Raw"/> 那一行是名单上的名字，**不查语言表** ——
    /// 用户自己起的名字不翻（契约：生活模式名不翻）；其余那几行走 <paramref name="tr"/>。
    private static string Text(Row row, TrayHooks hooks, Func<string, string> tr) =>
        (row.Raw ? row.Key : tr(row.Key)) + (row.Note?.Invoke(hooks) ?? "");

    /// 摊平：顶层那几行 + 每一颗子菜单里那些行。<b>摊的是形状</b>，而名单那一族
    /// 现问现摊（和菜单现做是同一条规矩）—— 所以摊出来的表和真菜单里摆出来的那一棵同源，
    /// 不是第二份枚举。「时间块」那一行同样在摊的时候现问它在不在。
    private static List<Row> AllRows(TrayHooks h)
    {
        var flat = new List<Row>();
        foreach (var row in Rows(h))
        {
            flat.Add(row);
            if (row.Submenu != null) flat.AddRange(row.Submenu(h));
        }
        return flat;
    }

    /// 「还剩 m:ss」。和遮罩上那行「还剩」是同一个写法（<c>Fader.TickRemain</c>）：
    /// 那一处量的是「这一步还剩多久」，这一处量的是「这一轮专注还剩多久」—— 两件事不是一件事，
    /// 但给人看的样子该是同一种。不写 <c>hh</c>：没人把专注段调成两小时以上，
    /// 而托盘那点地方「1:30:00」更难扫。
    ///
    /// <b>2026-10-08：「还差多久进休息」那一行也用它，而那一行可以超过一小时</b>
    /// （<c>activityTimeoutMin</c> 上限是 120 分钟，声明表里那一档）。所以这里
    /// <b>小时那一头要写出来</b> —— 「还剩 90:00」读不出那是九十分钟，
    /// 读得出的是「还剩 1:30:00」。两处（专注段 / 休息倒计时）走的是同一个写法，
    /// 而它按值自己决定要不要写小时那一段，所以专注段那一行<b>一个字没变</b>
    /// （它的值到不了那一头 —— <c>pomodoro.workMin</c> 那个键的范围到不了两小时）。
    public static string Clock(long ms)
    {
        var seconds = (int)Math.Max(0, ms) / 1000;
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    private readonly TrayHooks _hooks;

    public Tray(TrayHooks hooks, Mode mode, Action<string> log, Func<string, string> text)
    {
        _hooks = hooks;
        _log = log;
        _tr = text;
        // 起步那一张就是该显示的那一张：NIM_ADD 就得给一个图标，而「该是哪一张」由设置决定，
        // 所以它进构造参数。2026-09-29 之前构造里写死 Idle，App 那边连调两次 SyncTray()，
        // 其中一次纯为盖掉构造函数选错的那张 —— 「autoFade 关着的机器不该先亮一分钟 Idle 图标」。
        _mode = mode;
        var side = Math.Max(16, Win32.SmallIconWidth);
        _icons[Mode.Idle] = Icon("res://src/tray/idle.png", side);
        _icons[Mode.Fading] = Icon("res://src/tray/fading.png", side);
        _icons[Mode.Disabled] = Icon("res://src/tray/disabled.png", side);

        _wndProc = OnMessage;
        var instance = Win32.GetModuleHandle(null);
        var cls = new Win32.WindowClassEx
        {
            Size = Marshal.SizeOf<Win32.WindowClassEx>(),
            Proc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance, ClassName = ClassName, SmallIcon = _icons[Mode.Idle],
        };
        if (Win32.RegisterClassEx(ref cls) == 0)
            throw new InvalidOperationException($"注册 '{ClassName}' 失败 {Marshal.GetLastPInvokeError()}");

        _hwnd = Win32.CreateWindowEx(IntPtr.Zero, ClassName, WindowTitle, (uint)Win32.WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"建托盘窗口失败 {Marshal.GetLastPInvokeError()}");

        // 消息号现问：shell 每次起（含 explorer 重启）都广播同一个字符串，号本身是这一趟算出来的。
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        _log($"托盘：句柄 0x{_hwnd:X} 槽位 {side}px 窗口 DPI {Win32.GetDpiForWindow(_hwnd)} " +
             $"TaskbarCreated={_taskbarCreated} 标题='{Win32.WindowText(_hwnd)}'（递进去 {WindowTitle.Length} 字）");
        Add("起挂");
    }

    /// <summary>把当前那张图标挂上去。先递一发 <c>NIM_DELETE</c> 再 <c>NIM_ADD</c>：shell 认的是
    /// (hWnd, uID) 这一对，而 <c>TaskbarCreated</c> 那一路我们并不知道它侧那张表是空的
    /// （explorer 刚重启完，条目跟着旧 shell 一起没了）还是脏的（自己刚挂过）—— 先删一道，
    /// 两条路都只留下一个条目，不必去猜是哪一种。删的那一发没记账（它返回 false 是常事），
    /// 只看 ADD 的脸色：起挂那一次读回 <c>NIM_ADD=True</c>（2026-09-27，测量程序那一跑）。
    ///
    /// 这一手也只可能挂出一条：「托盘里两个 Keeper」从来不是这里重了，是跑了两发进程，
    /// 见 <see cref="Instance"/>。</summary>
    private void Add(string when)
    {
        var data = Data(Win32.NifMessage | Win32.NifIcon | Win32.NifTip, _icons[_mode], TipFor(_mode));
        Win32.ShellNotifyIcon(Win32.NimDelete, ref data);
        var added = Win32.ShellNotifyIcon(Win32.NimAdd, ref data);
        _log($"托盘：{when} NIM_ADD={added} 图标={_mode}");
    }

    /// 三态跟着走：遮罩在屏上是 Fading，否则 autoFade 关着就是 Disabled。
    public void SetMode(Mode mode)
    {
        if (_hwnd == IntPtr.Zero) return;
        // 先记下要哪一张，再去改：改没改成是一瞬间的事，而「该是哪一张」是这一发的意图，
        // 后面重挂（TaskbarCreated）照着的是意图，不是上一次成功没有。
        _mode = mode;
        var data = Data(Win32.NifIcon | Win32.NifTip, _icons[mode], TipFor(mode));
        if (!Win32.ShellNotifyIcon(Win32.NimModify, ref data))
            _log($"托盘：换图标 {mode} 没换成");
    }

    private string TipFor(Mode mode)
    {
        // 番茄钟在跑的时候，tooltip 报的是「专注还剩多久」而不是「休息提醒」——
        // 一颗图标前只挂得住一句话，而用户在番茄钟跑着的时候最想知道的就是还剩多久。
        // 遮罩已经抬起来的那一步不走这一支：那时候屏幕上有钟，托盘再报一个数是两条打架的话。
        var left = _hooks.PomodoroLeftMs();
        if (left > 0) return $"{_tr("Keeper — 番茄钟")}  {Clock(left)}";
        return mode switch
        {
            Mode.Fading => _tr("Keeper — 休息一下"),
            Mode.Disabled => _tr("Keeper — 自动淡入已关闭"),
            _ => _tr("Keeper — 休息提醒"),
        };
    }

    private Win32.NotifyIconData Data(uint flags, nint icon, string tip) => new()
    {
        Size = Marshal.SizeOf<Win32.NotifyIconData>(), WindowHandle = _hwnd, IconId = IconId,
        Flags = flags, CallbackMessage = Win32.WmTrayIcon, Icon = icon, Tip = tip,
    };

    private nint OnMessage(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // 另一发 Keeper 起来了、被单实例那道锁挡回去的那一发递过来的一条。这一扇不建第二颗图标，
        // 只负责露面 —— 消息号是我们自己定的 WM_USER+2，不会和别的程序撞。
        if (msg == Win32.WmSecondInstance)
        {
            _log("托盘：又一发 Keeper 想起来，被单实例那道锁挡住了 —— 这一扇露面");
            _hooks.SecondLaunch();
            return 0;
        }
        // 测量程序那一条：把菜单项的 id 递回来，让它**在这扇窗的 WndProc 栈上**走一遍真分发。
        // 要判的就是这个栈位 —— `Quit()` 里的 `_tray.Dispose()` 正 destroy 的就是脚下这扇窗。
        if (msg == Win32.WmTrayMenuProbe)
        {
            Dispatch((uint)wParam, "测量程序");
            return 0;
        }
        // explorer 重启（或者崩了被拉起来）：托盘里那一份跟着旧 shell 一起没了，而且不会有人来
        // 补 —— shell 只发这一条广播，收到就得自己重挂。藏着的窗也收得到广播，
        // 所以「SW_HIDE 扫过这一扇」不构成漏收的理由。
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            _log("托盘：收到 TaskbarCreated（shell 刚重建），把图标挂回去");
            Add("重挂");
            return 0;
        }
        if (msg == Win32.WmTrayIcon && (uint)wParam == IconId)
        {
            // 没设版本号，shell 就按老版通知图标的协议发：lParam 里那发鼠标消息本身。左键切预览，右键出菜单。
            _log($"托盘：回调进来了 {(uint)lParam switch { Win32.WmLButtonUpMsg => "左键松开", _ => $"0x{(uint)lParam:X4}"}}");
            if ((uint)lParam == Win32.WmLButtonUpMsg) _hooks.Show();
            else if ((uint)lParam is Win32.WmRButtonUpMsg or Win32.WmContextMenuMsg) ShowMenu();
            return 0;
        }
        if (msg == Win32.WmDestroyMsg) RemoveIcon();
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        // 先把前台给自己。不给的话菜单会一直挂在屏幕上，直到你点了别的窗口才收。
        Win32.SetForegroundWindow(_hwnd);
        // 建树和弹它是两件事：树是 <see cref="Build"/> 造的（和自检量的是同一个方法），
        // 弹是 <c>TrackPopupMenu</c>，而它是模态的 —— 弹着的时候本进程的消息照走，
        // 只是 Godot 那一侧的主循不跑（量过的，见 docs/research/desktop-layer-and-window-measurements.md）。
        var tree = Build(_hooks, _tr);
        try
        {
            Win32.GetCursorPos(out var at);
            var picked = (uint)Win32.TrackPopupMenu(tree.Root,
                Win32.TpmReturnCmd | Win32.TpmNonotify | Win32.TpmRightButton,
                at.X, at.Y, 0, _hwnd, IntPtr.Zero);
            _log($"菜单：开在 ({at.X},{at.Y})，选中的是 {picked}");
            Dispatch(picked, "菜单");
        }
        finally
        {
            // **两层**：先子后父。父菜单一销毁，挂在它上面的子菜单句柄就作废了，
            // 所以反过来销毁等于漏掉一层（欠账 03 那条在工单 04 记厚了）。
            // 「退出」那一发在本进程朝退出走之后才到这里，所以这一段必须留在 finally ——
            // 它不是善后，是产品行为的一部分。
            tree.Dispose();
            // 递一发空消息，shell 才立刻重画菜单底下那块；不发的话会留一块残影。
            Win32.SendMessage(_hwnd, WmNull, 0, 0);
        }
    }

    /// <summary>那一条分发。真菜单（<see cref="ShowMenu"/>）和测量程序
    /// （<see cref="ProbeMenu"/>）走的是**同一个方法、同一个调用点**，所以「按下去发生了什么」
    /// 不需要两只手各证一遍 —— 差的只是有没有真的弹出那一层。</summary>
    private void Dispatch(uint picked, string from) => DispatchTo(picked, from, _hooks, _log);

    /// <summary>分发表本体，摊平之后按 id 找 —— 所以<b>子菜单里那一项和顶层那一项走的是同一张表</b>，
    /// 不需要第二份 switch（原来那份正是「加一行要改两处」的来源）。
    ///
    /// <b>子菜单自己那一行递不出 id</b>（<c>MF_POPUP</c> 那一项在 Windows 那边读的 id 恒是 -1，
    /// 它按下去是<b>张开</b>，不是选中），所以 <c>Run == null</c> 守着它：Esc 递回来的 0
    /// 和它那一行撞得上，没有这个守卫就会「按一下生活模式就开成管理」—— 而它压根不该发消息。
    ///
    /// <b>internal 而不是 private</b>：<c>history-entry</c> 那一组要问「递这一个 id 触发的是不是
    /// 那一个动作」—— 而问法只能是<b>真的递回去</b>，读菜单上的字答不了这件事。</summary>
    internal static void DispatchTo(uint picked, string from, TrayHooks hooks, Action<string> log)
    {
        foreach (var row in AllRows(hooks))
        {
            if (row.Run == null || row.Id != picked) continue;
            // 「退出」这一发一旦下去，本进程就朝退出走了，所以日志要先把「从哪儿来的」写清楚：
            // 从测量程序进来的那条落在托盘自己 WndProc 的栈上，和真菜单一样。
            if (picked == IdExit) log($"菜单：{from}递来「退出」，回调落在托盘这扇窗的栈上");
            row.Run(hooks);
            return;
        }
        log($"菜单：{from}递来的 id={picked} 没认出来");
    }

    /// <summary>测量程序那一发：把某个菜单项的 id 递回这扇窗，让它走 <see cref="Dispatch"/>。
    /// 用 <c>PostMessage</c> 不用 <c>SendMessage</c> —— 发的人不该站在自己的栈上收结果。
    /// 返回是否递出去了；递出去之后那一发什么时候真走到 <c>Quit()</c>，看日志里那行
    /// 「{from}递来…」。名字认的是摊平之后那张表里的别名，不是这里第二份 switch。
    ///
    /// <b>子菜单寻址的写法</b>：生活模式那几项走 <c>生活模式:名字</c>（「管理生活模式…」走 <c>manage</c>），
    /// 因为生活模式名是数据、要从名单上问出来，不能抄进这里。
    /// 递<b>子菜单那一行</b>（<c>modes</c>）是不递的 —— 它按下去是张开，不递 id，
    /// 所以这一发会先说清它是什么再退回来。</summary>
    public bool ProbeMenu(string which)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _log($"菜单测量程序：'{which}' 没递 —— 这扇窗已经没了");
            return false;
        }

        var want = which.Trim().ToLowerInvariant();
        var flat = AllRows(_hooks);
        Row? row = null;
        foreach (var candidate in flat)
            if (Array.IndexOf(candidate.Aliases, want) >= 0) row = candidate;
        if (row == null)
        {
            _log($"菜单测量程序：'{which}' 不是菜单里任何一行的名字（{Names(_hooks)}）");
            return false;
        }
        if (row.Run == null)
        {
            _log($"菜单测量程序：'{which}' 是子菜单那一行 —— 按下去是张开，它不递 id");
            return false;
        }

        _log($"菜单测量程序：递 '{which}'（id={row.Id}）进托盘这扇窗的 WndProc");
        return Win32.PostMessage(_hwnd, Win32.WmTrayMenuProbe, new IntPtr(row.Id), IntPtr.Zero);
    }

    /// 那句「不是菜单里任何一行的名字」后面括号里的一串，从摊平的表上数出来，不是手抄的。
    /// 生活模式那几项的别名是「生活模式:名字」，一句里印一长串名读不下去 —— 那一族折成<b>一个</b>
    /// 「生活模式」（不是三个：折成三个读起来像有三个不同的项）。
    private static string Names(TrayHooks h)
    {
        var names = new List<string>();
        var modeSaid = false;
        foreach (var row in AllRows(h))
        {
            if (!row.Aliases[0].StartsWith("生活模式:")) { names.Add(row.Aliases[0]); continue; }
            if (modeSaid) continue;
            modeSaid = true;
            names.Add("生活模式");
        }
        return string.Join("/", names);
    }

    /// <summary>测量程序那一发（<c>--tray-submenu-shot</c>）：真的把菜单弹出来，在<b>另一条线程</b>上
    /// 用真鼠标把子菜单张开，把两层弹层抓成一帧，然后收场。
    ///
    /// <b>为什么必须走另一条线程</b>：<c>TrackPopupMenu</c> 是模态的，弹着的时候 Godot
    /// 那一侧的主循不跑（量过的：菜单开着那 14.9 秒里 <c>_Process</c> 一次没跑，
    /// 见 docs/research/desktop-layer-and-window-measurements.md）。而 <c>Chase</c> 那条排程是
    /// 主线程的 —— 在这里排步等于排一串永远不执行的时刻。所以这一发自己起一条线程，
    /// 读矩形、挪光标、抓帧都在那条上。
    ///
    /// <b>为什么用真鼠标挪而不是合成方向键</b>：合成投递的方向键叫不动 <c>#32768</c>
    /// （同一份实测结果：注入 HOME / DOWN / END，一次没挪高亮，真点进那个矩形才动）。
    /// 挪光标走的是 <see cref="Win32.MoveTo"/>（<c>SendInput</c> 的绝对移动），它是事件，
    /// 而 <c>SetCursorPos</c> 不产生事件、子菜单也不会认。
    ///
    /// <b>抓帧那半能不能在另一条线程上跑，这一发自己答</b>：答不上来的话日志里那一行
    /// 就是「没写成」，而这一发不会假装它拍到了。
    /// <b>抓帧那半能不能在另一条线程上跑，这一发自己答</b>：答不上来的话日志里那一行
    /// 就是「没写成」，而这一发不会假装它拍到了。
    ///
    /// <b><paramref name="which"/> 是要张开哪一个子菜单</b>（2026-10-12 加，默认「生活模式」）。
    /// 托盘上有两个带箭头的子菜单，形状不同：一个是「写什么 + 切生活模式」，
    /// 另一个是「打开休息历史…」加今天那几条记录。钉死一个就等于另一个永远只有自检、
    /// 没有实物帧 —— 而顺序对不对这件事，恰恰是像素答得了、日志答不了的。</summary>
    public void ShootSubmenu(string path, string which, Action done)
    {
        Win32.SetForegroundWindow(_hwnd);
        var tree = Build(_hooks, _tr);
        // 按**字**找那一项的位置，不数项：菜单里不数项，序号会漂。
        var modePos = PosOfString(tree.Root, which);
        if (modePos < 0)
        {
            tree.Dispose();
            Win32.SendMessage(_hwnd, WmNull, 0, 0);
            _log($"测量程序：顶层没有一颗写着「{which}」的项，这一帧没得拍");
            done();
            return;
        }
        var report = "测量程序：还没开始拍";
        try
        {
            Win32.GetCursorPos(out var at);
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                report = await ShootOpenSubmenu(path, tree.Root, (uint)modePos);
                // 收场走 WM_CANCELMODE，不走 Esc（<see cref="Win32.WmCancelMode"/> 那条注释
                // 写着为什么）：Esc 是往菜单里塞一个键，而按 Esc 收场那一发递回来的是 1 ——
                // 顺手选中了「进入休息」，测量程序的收场成了产品动作。
                Win32.PostMessage(_hwnd, Win32.WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            });
            var picked = (uint)Win32.TrackPopupMenu(tree.Root,
                Win32.TpmReturnCmd | Win32.TpmNonotify | Win32.TpmRightButton,
                at.X, at.Y, 0, _hwnd, IntPtr.Zero);
            _log($"菜单：开在 ({at.X},{at.Y})，选中的是 {picked}");
            // **那一跳的对账**：按在子菜单某一项上，壳递回来的是不是一个我们真认得的号，
            // 以及它落到摊平那张表的哪一行。0 是「关掉了没选」（分隔线和 Esc 都是它），
            // 所以它单独说 —— 拿它当「点中了」就是量错了东西。
            var landed = AllRows(_hooks).FirstOrDefault(r => r.Id == picked);
            _log(picked == 0
                ? "菜单：递回来的是 0 —— 这一次是关掉没选，不是点中了某一项"
                : landed == null
                    ? $"菜单：递回来的是 {picked}，摊平的表上没有这一号 —— 认不得"
                    : $"菜单：按下去 → 壳递回来 {picked} → 摊平的表上落到「{landed.Aliases[0]}」");
        }
        finally
        {
            tree.Dispose();
            Win32.SendMessage(_hwnd, WmNull, 0, 0);
        }
        _log(report);
        done();
    }

    /// 菜单弹着的那几毫秒里做的事，全在另一条线程上：
    /// 读「生活模式」那一项的屏幕矩形 → 真鼠标挪上去（子菜单因此张开）→ 读子菜单的矩形 →
    /// 把两层弹层抓成一帧。
    private async Task<string> ShootOpenSubmenu(string path, nint root, uint modePos)
    {
        if (!Win32.GetMenuItemRect(IntPtr.Zero, root, modePos, out var item))
            return $"「生活模式」那一项的矩形没读到（错误码 {Marshal.GetLastPInvokeError()}），这一帧没得拍";
        var to = new Win32.Point { X = (item.Left + item.Right) / 2, Y = (item.Top + item.Bottom) / 2 };
        var moved = Win32.MoveTo(to);
        await Task.Delay(900);
        var sub = Win32.GetSubMenu(root, (int)modePos);
        var subCount = Win32.GetMenuItemCount(sub);

        // 两层弹层**都是屏上另外几扇窗**（类 `#32768`，本进程的）—— 只算菜单本体那一块的话，
        // 子菜单整个落在外面。所以范围是这两扇窗矩形的并集。
        // **按类名认，不按标题**：弹层的标题是系统给的，而且两条一模一样的空标题认不出谁是谁。
        var box = new Win32.Rect { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
        var layers = 0;
        foreach (var hwnd in Win32.OwnTopLevelWindows())
        {
            if (Win32.ClassName(hwnd) != "#32768" || !Win32.GetWindowRect(hwnd, out var r)) continue;
            layers++;
            box = new Win32.Rect
            {
                Left = Math.Min(box.Left, r.Left), Top = Math.Min(box.Top, r.Top),
                Right = Math.Max(box.Right, r.Right), Bottom = Math.Max(box.Bottom, r.Bottom),
            };
        }
        if (layers == 0) return $"自己进程里一扇 #32768 都没有 —— 菜单没弹出来，这一帧没得拍（{Win32.DescribeOwnTopLevels()}）";

        // 子菜单里那几项逐条读一遍 —— 「子菜单真张开了」和「子项带的 id 真是我们发的那个」，
        // 两件都在这一行里。**这是 id 递回的实测结果**：菜单本体递回来的就是这些数。
        // 它不是「Visible=True」（那一族假象记在 CODING_STANDARDS.md 第 2 条）。
        var itemBoxes = new List<string>();
        for (var i = 0; i < subCount; i++)
            itemBoxes.Add($"id={Win32.GetMenuItemID(sub, i)} " +
                          (Win32.GetMenuItemRect(IntPtr.Zero, sub, (uint)i, out var r) ? "@" + r.ToString() : "@没读到矩形") +
                          $" {(On(sub, i, Win32.MfChecked) ? "✓" : "")}{Str(sub, i)}{(On(sub, i, Win32.MfSeparator) ? "（分隔线）" : "")}");

        var image = DisplayServer.ScreenGetImageRect(
            new Rect2I(box.Left, box.Top, box.Width, box.Height));
        if (image == null || image.IsEmpty())
            return $"两扇弹层都在（{box}），这一帧却没读到像素 —— 抓帧在菜单弹着的时候跑不起来，文件没写成";
        var err = image.SavePng(path);
        var read = $"{path} {box} {image.GetWidth()}x{image.GetHeight()} 错误码={err}";
        image.Dispose();

        // **真按一下左键**（工单 04 收尾那一发）。到上面为止量的都是「菜单句柄上挂着什么」：
        // 每一项的矩形、每一项的 id。缺的那一环是**壳怎么把「按在那个矩形上」变成那个 id** ——
        // 而那正是 <c>TrackPopupMenu</c> 的返回值（<c>TpmReturnCmd</c>）。帧抓完、菜单还弹着，
        // 这一发点下去之后调用方那个 <c>TrackPopupMenu</c> 就带着 id 回来了。
        //
        // **按字挑那一项，不按序号**：子菜单里上半是生活模式那几条、下面有分隔线和「管理生活模式…」，
        // 序号会随生活模式条数漂（菜单里不数项）。挑第一个**不是分隔线、也不是管理生活模式**的那一条 ——
        // 点「管理生活模式…」会把设置窗开起来，那是产品动作，测量程序的收场不该是产品动作。
        var target = -1;
        var targetRect = new Win32.Rect();
        for (var i = 0; i < subCount; i++)
        {
            if (On(sub, i, Win32.MfSeparator) || On(sub, i, Win32.MfPopup)) continue;
            if (Str(sub, i) == _tr("管理生活模式…")) continue;
            if (!Win32.GetMenuItemRect(IntPtr.Zero, sub, (uint)i, out targetRect)) continue;
            target = i;
            break;
        }
        if (target < 0)
            return $"子菜单里找不到一条能按的（{subCount} 项），按下去那一环没量到；" +
                   $"挪到 ({to.X},{to.Y}) 发进系统 {moved}/1；子菜单 [{string.Join(" ", itemBoxes)}]；那一帧：{read}";

        var press = new Win32.Point
        {
            X = (targetRect.Left + targetRect.Right) / 2,
            Y = (targetRect.Top + targetRect.Bottom) / 2,
        };
        await Task.Delay(250);
        var sent = Win32.ClickAt(press);
        return $"挪到 ({to.X},{to.Y}) 发进系统 {moved}/1；子菜单 {subCount} 项 [{string.Join(" ", itemBoxes)}]；" +
               $"两扇弹层 {layers} 扇；" +
               $"**在第 {target} 项那个矩形 ({targetRect}) 上真按了一下左键 → 发进系统 {sent}/3**，" +
               $"它挂着 id={Win32.GetMenuItemID(sub, target)}，等 TrackPopupMenu 把这个数递回来对账；" +
               $"那一帧：{read}";
    }

    // ---- 自检：托盘子菜单那一组（组名 modes-tray）--------------------------------
    //
    // **这一组量的是「接线」，不是「弹层」** —— <c>TrackPopupMenu</c> 是模态的、要有真的屏幕，
    // 无头里弹不出来，所以「那张子菜单在屏幕上真的能张开、点得中」那一半量不到，
    // 留给实拍帧（工单 04 来历段，契约 194 行也写着这一张不能只靠自检收工）。
    // 这里量得到的是**菜单句柄自己的属性**：那棵树建出来没有、子菜单挂上去了没有、
    // 勾打在哪一条、id 是不是原样挂在那一项上、递回来的 id 落到哪一行。
    //
    // 全程不碰磁盘：名单是这一组自己递进去的（<see cref="TrayHooks.Modes"/> 那个问句），
    // 所以既不建临时目录也不碰 %APPDATA%。**名单从磁盘读那一段在 modes-index /
    // modes-unreadable 那一族里**，这一组不重复量它。
    //
    // 读回菜单用 <c>Build</c> 造的<b>同一棵</b>树（不是自检自己重摆一份像它的），
    // 所以这里判过的接线就是产品跑的那一棵。

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf LifeModeMenu = new(
        () => "托盘子菜单通过：那棵树建得起来、子菜单挂上而且读回来的句柄就是建那一个、" +
              "挂它那条形态的形参是指针宽、名字原样、" +
              "勾只在当前那一个上且跟着当下那份状态走、读不通那一条是灰的而不是空名单、" +
              "id 从菜单上读回来落到名单上那一条、顶层那几颗照旧、递错的 id 和 Esc 都不误触发、" +
              "**画廊没有在托盘这一侧加入口**（收进来的那几份只让托盘在子菜单里多「切」的那几行、" +
              "顶层项集逐字相同，表上也没有一行带画廊那一句话，而那份枚举确是现问的）",
        "托盘子菜单", LifeModeMenuSelfTest);

    /// <summary>托盘那颗「生活模式」子菜单：建树、挂子菜单、勾打在哪一条、id 递回来落到哪一行。
    ///
    /// 每一条都配了对照（「一定该被动到」那一侧，<c>CODING_STANDARDS.md</c> 第 5 条）：
    /// 挂上子菜单那一发要配「顶层那几颗命令项读的 id 真是自己的」，
    /// 读不通那一档要配「同样的名单当成读得通就列得出名字」，
    /// 分发表要配「递一个不存在的 id 什么也不触发」和「Esc 递的 0 什么也不触发」，
    /// 空名单要配「读不通那一档不是它」（03 量过的那一对假象）。
    ///
    /// <b>不数项</b>：要比的项数从 <see cref="LifeModeRows"/> 那张表上数出来，
    /// 绿话里也不出现「上半五项」这种数字。</summary>
    public static List<string> LifeModeMenuSelfTest()
    {
        var checks = new Checks();
        var tr = new Func<string, string>(s => s);

        // 假的那几样：把「按下去发生了什么」记下来，才判得出「只触发了该触发的那一个」。
        var fired = new List<string>();
        var switched = new List<string>();
        var managed = 0;
        var logs = new List<string>();
        TrayHooks Hooks(Func<LifeModeList> modes) => new(
            Show: () => fired.Add("遮罩"),
            Calendar: () => fired.Add("日历"),
            Auto: () => fired.Add("自动淡入"),
            Options: () => fired.Add("设置"),
            About: () => fired.Add("关于"),
            Exit: () => fired.Add("退出"),
            SecondLaunch: () => fired.Add("又一发"),
            Pomodoro: () => fired.Add("番茄钟"),
            Autostart: () => fired.Add("开机自动启动"),
History: () => fired.Add("休息历史"),
            SwitchLifeMode: name => { fired.Add($"切:{name}"); switched.Add(name); },
            ManageLifeModes: () => { fired.Add("管理生活模式"); managed++; },
            AutoFadeOn: () => false,
            PreviewOn: () => false,
            CalendarOn: () => false,
            PomodoroOn: () => false,
            AutostartOn: () => false,
            PomodoroLeftMs: () => 0,
            LifeModes: modes,
            // 这一组量的是生活模式那几行，所以「时间块」那一行不出现（它会自己现问，
            // 出现在不在是 routine-tray 那一组量的那一半）；「勿扰中」那一行同理。
            // 「下次休息还有多久」那一行也**刻意给 null**（同一条理由）：
            // 它在不在、写着什么数是 `tray-break` 那一组量的那一半，
            // 这一组量的是别的几行，让它在就等于把项数改了。
            BlockName: () => null,
            DndWhy: () => null,
            BreakLeftMs: () => null, Today: () => new TodayList([], false));

        var names = new List<string> { "工作", "画画", "写东西" };
        var good = () => new LifeModeList(names, "画画", false);

        // 对照先行：好的一发放进去先量一遍，后面每一条都拿它当尺子。
        checks.Check("对照：名单读得通的那一发，子菜单挂得上、名字原样、勾打在那一个上、id 原样挂着", () =>
        {
            var hooks = Hooks(good);
            using var tree = Build(hooks, tr);
            var rows = LifeModeRows(hooks);

            var top = tree.Root;
            var at = PosOfString(top, "生活模式");
            if (at < 0) throw new Exception("顶层没有一颗写着「生活模式」的项");
            var sub = Win32.GetSubMenu(top, at);
            if (sub == IntPtr.Zero) throw new Exception("「生活模式」那一项没挂上子菜单");
            if (sub != tree.Submenus["lifemodes"])
                throw new Exception($"子菜单句柄读回来对不上：建的是 0x{tree.Submenus["lifemodes"]:X}，" +
                                   $"读回来 0x{sub:X}");

            // **对照**：同一颗菜单上，顶层那几颗命令项的 id 读回来真是自己的 ——
            // 免得「id 递回」那几发量的是一个本来就恒为 -1 的东西。
            if (Win32.GetMenuItemID(top, 0) == uint.MaxValue)
                throw new Exception("顶层第一项读不回 id，这一组量的 id 递回是空的");

            var expect = rows.Count + rows.Count(r => r.SeparatorBefore);
            var got = Win32.GetMenuItemCount(sub);
            if (got != expect)
                throw new Exception($"子菜单上 {got} 项，表上 {rows.Count} 行加上分隔线该是 {expect} 项");

            for (var i = 0; i < names.Count; i++)
            {
                if (Str(sub, i) != names[i])
                    throw new Exception($"第 {i} 项的字是 '{Str(sub, i)}'，名单上那一条是 '{names[i]}'");
                if (Win32.GetMenuItemID(sub, i) != IdLifeModeFirst + (uint)i)
                    throw new Exception($"第 {i} 项的 id 是 {Win32.GetMenuItemID(sub, i)}，" +
                                       $"该是 {IdLifeModeFirst + (uint)i}");
            }

            var sep = names.Count;
            if (Str(sub, sep) != "") throw new Exception("分隔线那一项上有字");
            if (!On(sub, sep, Win32.MfSeparator)) throw new Exception("下半那条不是分隔线");
            // **分隔线那一项读回来的 id 是 0，不是文档写的 -1**（量到的，见
            // docs/research/win32-constants-and-structs.md「GetMenuItemID」那一条）——
            // 而 0 正是 Esc 递回来那一个。所以这里判的不是「它读回 -1」，
            // 而是**分发表里没有一行是 id 0**：那样 0 无论从哪条路来都落空。
            if (Win32.GetMenuItemID(sub, sep) != 0)
                throw new Exception($"分隔线那一项带着 id {Win32.GetMenuItemID(sub, sep)}，量到的是 0");
            foreach (var row in AllRows(hooks))
                if (row.Run != null && row.Id == 0)
                    throw new Exception($"「{row.Key}」那一行的 id 是 0 —— 分隔线和 Esc 递的正是这个数");
            if (Str(sub, sep + 1) != "管理生活模式…") throw new Exception("下半那一条不是「管理生活模式…」");
            if (Win32.GetMenuItemID(sub, sep + 1) != IdManageLifeModes)
                throw new Exception($"「管理生活模式…」的 id 是 {Win32.GetMenuItemID(sub, sep + 1)}，该是 {IdManageLifeModes}");

            // 勾**只**在当前那一个上：多的、少的都算坏。
            for (var i = 0; i < names.Count; i++)
            {
                var on = On(sub, i, Win32.MfChecked);
                if (on != (names[i] == "画画"))
                    throw new Exception($"第 {i} 项（{names[i]}）的打勾是 {on}，该是 {names[i] == "画画"}");
            }
            if (On(sub, sep + 1, Win32.MfChecked)) throw new Exception("「管理生活模式…」被打上了勾");
        });

        checks.Check("勾跟着当下那份状态走：当前那一个换了，勾跟着挪，挪之前那一个不残留", () =>
        {
            var moved = new List<string> { "工作", "画画", "写东西" };
            using var tree = Build(Hooks(() => new LifeModeList(moved, "写东西", false)), tr);
            var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
            if (On(sub, 1, Win32.MfChecked)) throw new Exception("挪走了原来那一个还留着勾");
            if (!On(sub, 2, Win32.MfChecked)) throw new Exception("新的那一个没打上勾");
        });

        checks.Check("读不通那一档：上半是一条灰的「生活模式名单读不通」，不列名字；同样的名单当成读得通就列得出", () =>
        {
            // 名单读不通、**而名单本身还非空** —— 这一发量的是「读不通那一档优先」，
            // 而不是一个空列表。空列表冒充「没有生活模式」是 03 在设置窗那一侧量过的一对假象。
            var hooks = Hooks(() => new LifeModeList(names, "画画", true));
            using var tree = Build(hooks, tr);
            var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
            if (Str(sub, 0) != "生活模式名单读不通") throw new Exception($"上半那一条是 '{Str(sub, 0)}'");
            if (!On(sub, 0, Win32.MfGrayed)) throw new Exception("那一条不是灰的");
            for (var i = 0; i < names.Count; i++)
                if (Str(sub, i) == names[i]) throw new Exception($"读不通那一档还把 '{names[i]}' 列了出来");
            if (On(sub, 0, Win32.MfChecked)) throw new Exception("那一条被打上了勾");

            // **对照**：一模一样的名单，`Unreadable: false` 就列得出名字 —— 证明上一发
            // 判的是「读不通那一档」本身，不是「这几句字碰巧没被摆上去」。
            using var ok = Build(Hooks(() => new LifeModeList(names, "画画", false)), tr);
            var okSub = Win32.GetSubMenu(ok.Root, PosOfString(ok.Root, "生活模式"));
            if (Str(okSub, 0) != names[0]) throw new Exception("读得通那一发没把名字列出来，对照不成立");
        });

        checks.Check("一个生活模式都没有：下半只剩「管理生活模式…」，而它和读不通那一档不是同一副样子", () =>
        {
            using var tree = Build(Hooks(() => new LifeModeList([], null, false)), tr);
            var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
            if (Win32.GetMenuItemCount(sub) != 1) throw new Exception("一个生活模式都没有，子菜单上不止那一项");
            if (Str(sub, 0) != "管理生活模式…") throw new Exception("剩的那一条不是「管理生活模式…」");

            // 对照：读不通那一档是「一条灰的 + 一条分隔线 + 管理生活模式…」，和这一发不是同一副样子。
            using var bad = Build(Hooks(() => new LifeModeList([], null, true)), tr);
            var badSub = Win32.GetSubMenu(bad.Root, PosOfString(bad.Root, "生活模式"));
            if (Win32.GetMenuItemCount(badSub) == Win32.GetMenuItemCount(sub))
                throw new Exception("读不通那一档和一个生活模式都没有长得一样（那正是 03 量过的那一对假象）");
        });

        checks.Check("子菜单那一行自己递不出 id，而顶层那几颗命令项的 id 原样挂着", () =>
        {
            using var tree = Build(Hooks(good), tr);
            var top = tree.Root;
            var at = PosOfString(top, "生活模式");
            if (Win32.GetMenuItemID(top, at) != uint.MaxValue)
                throw new Exception("子菜单那一项竟然递了 id（Windows 那边对 MF_POPUP 恒是 -1）");
            var options = PosOfString(top, "设置…");
            if (options < 0) throw new Exception("顶层没有「设置…」那一项");
            if (Win32.GetMenuItemID(top, options) != IdOptions)
                throw new Exception($"「设置…」读回来 id={Win32.GetMenuItemID(top, options)}，该是 {IdOptions}");
        });

        checks.Check("挂子菜单那条形态的形参是指针宽（64 位上 HMENU 的高 32 位不是零）", () =>
        {
            // **这一组量的是签名，不是实测结果** —— 这不是偷懒，是量出来的结论：
            // 把 <c>AppendMenuHandle</c> 换成按 <c>uint</c> 递的那条，自检<b>照样绿</b>。
            // 原因量到了（2026-10-01）：这台机器上 menu 句柄是 0x13740C6F，高 32 位就是 0，
            // 所以「截掉高 32 位」在这里是空操作，一个发子菜单的实测结果都分不出来。
            // 记在 docs/research/win32-constants-and-structs.md「GetMenuItemID」那一条旁边。
            //
            // 所以判据只能落在**形参的宽度**上 —— 那是被称错就会当场坏掉的那一处，
            // 而且它是编译期事实，量得到。（真出问题时它也不玄：子菜单永远不弹或者
            // 弹出来是空的，而那正是自检量不到、只能靠实拍帧和真点击发现的那一半。）
            var handle = typeof(Win32).GetMethod("AppendMenuHandle");
            if (handle == null) throw new Exception("Win32 上没有 AppendMenuHandle 那一条形态");
            var id = handle.GetParameters()[2].ParameterType;
            if (id != typeof(nint))
                throw new Exception($"AppendMenuHandle 第三个形参是 {id.Name}，该是指针宽（nint）—— " +
                                   "64 位上 HMENU 的高 32 位不是零，按 uint 递会被截掉");
        });

        checks.Check("id 段不撞：生活模式那几项的号和手写那几颗一个都不重", () =>
        {
            var seen = new Dictionary<uint, string>();
            var hooks = Hooks(good);
            foreach (var row in Rows(hooks))
            {
                if (row.Run == null) continue;
                if (seen.ContainsKey(row.Id)) throw new Exception($"id {row.Id} 顶层就重了（{seen[row.Id]} 和 {row.Key}）");
                seen[row.Id] = row.Key;
            }
            var rows = LifeModeRows(hooks);
            foreach (var row in rows)
            {
                if (row.Run == null) continue;
                if (seen.ContainsKey(row.Id))
                    throw new Exception($"id {row.Id} 撞上了顶层那一行「{seen[row.Id]}」，按下去会动别的");
                seen[row.Id] = row.Key;
            }
        });

        // ---- id 递回来落到哪一行 ----------------------------------------------------
        checks.Check("id 递回：菜单上读回来的那个 id 递进分发表，落到的就是名单上那一条", () =>
        {
            var hooks = Hooks(good);
            using var tree = Build(hooks, tr);
            var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
            var at = 2;
            fired.Clear(); switched.Clear(); managed = 0;
            DispatchTo(Win32.GetMenuItemID(sub, at), "自检", hooks, logs.Add);
            if (switched.Count != 1 || switched[0] != names[at])
                throw new Exception($"递了菜单上第 {at} 项的 id，切过去的却是「{string.Join("/", switched)}」");
            if (fired.Count != 1) throw new Exception($"顺带动了别的：{string.Join("/", fired)}");
        });

        checks.Check("id 递回：顶层那几颗照旧，「管理生活模式…」落到它自己那一件", () =>
        {
            var hooks = Hooks(good);
            using var tree = Build(hooks, tr);
            var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
            fired.Clear(); switched.Clear(); managed = 0;
            DispatchTo(IdOptions, "自检", hooks, logs.Add);
            if (fired.Count != 1 || fired[0] != "设置")
                throw new Exception($"递「设置…」的 id 触发的是 {string.Join("/", fired)}");
            fired.Clear(); managed = 0;
            DispatchTo(Win32.GetMenuItemID(sub, Win32.GetMenuItemCount(sub) - 1), "自检", hooks, logs.Add);
            if (managed != 1 || fired.Count != 1 || fired[0] != "管理生活模式")
                throw new Exception($"递「管理生活模式…」的 id 触发的是 {string.Join("/", fired)}");
        });

        checks.Check("点错 id 不误触发：递一个表上没有的 id，什么也不动；递 Esc 那一个 0，同样什么也不动", () =>
        {
            var hooks = Hooks(good);
            fired.Clear(); switched.Clear(); managed = 0;
            // 对照：先来一发真 id，证明这一组量的是「触发了没有」而不是「没触发」。
            DispatchTo(IdAbout, "自检", hooks, logs.Add);
            if (fired.Count != 1) throw new Exception("对照不成立：递一个真 id 什么都没触发");
            fired.Clear();
            DispatchTo(IdLifeModeFirst + 9999, "自检", hooks, logs.Add);
            if (fired.Count != 0 || switched.Count != 0 || managed != 0)
                throw new Exception($"递一个表上没有的 id 竟然触发了 {string.Join("/", fired)}");
            // Esc：TrackPopupMenu 收在 0，而「生活模式」那一行的 Id 也是 0 ——
            // 没有 Run == null 那个守卫，Esc 就会变成「按一下生活模式就开成管理」。
            DispatchTo(0, "自检", hooks, logs.Add);
            if (fired.Count != 0 || switched.Count != 0 || managed != 0)
                throw new Exception($"Esc 递的 0 竟然触发了 {string.Join("/", fired)}");
        });

        // ---- 画廊不加入口（工单 05）---------------------------------------------------
        //
        // 设计文档 `docs/design/preset-life-mode.md`「入口」那一节：画廊的入口在生活模式管理器和设置窗；
        // **托盘不加入口** —— 托盘在这一族照旧只做「切」。这一族是**减法**，而减法最容易在「加了新东西」那一次顺手把托盘也加一遍。
        //
        // 两发各管一件，缺一不可：
        //   1. **收进来之后，托盘只多了「切」的那几行** —— 现场是真的：临时目录里种出那一份只读
        //      目录、把它列出来、再拿那几份名字当作「用户已经收进来的生活模式」递进那份名单，
        //      然后比两棵树的**顶层项集**。设计文档 `docs/design/preset-life-mode.md`「入口」那一节说托盘在这一族照旧只做「切」，说的就是这件事：
        //      收进来的那几份在托盘上是**能切的生活模式**，而**没有多出「打开画廊」那一项**。
        //      <b>为什么不是「目录在 / 不在」两种现场</b>：托盘压根没有通往那个目录的接缝，
        //      所以那两种现场在托盘这一侧根本不是输入 —— 量它量的是「什么都没读」的那种相等
        //      （2026-10-02 第一版就是那一种，反向验证时它<b>没有红</b>，当场改成这一种）。
        //   2. **表上没有任何一行带画廊那一句话** —— 抓的是「不接数据、光加一行项」那种加法
        //      （1 抓不到它：那一份在两种现场下都摆着）。**那句话取自
        //      <see cref="Options.LifeModeMenuLabels"/> 那一份措辞表**（自检里能问的那一处），
        //      不在这里手抄一份 —— 抄一份就是会腐烂的副本。
        //   **对照**：那份枚举确是现问的（换一份名单，子菜单上半真的换了）—— 没有它，上面那一条
        //   的「顶层项集逐字相同」可能是「它压根不读任何东西」的那种相等
        //   （同 <c>CODING_STANDARDS.md</c> 第 5 条）。
        checks.Check("收进来的那几份只让托盘多了「切」的那几行，顶层项集一个字节没多", () =>
        {
            var dir = System.IO.Path.Combine(Tmp.Root,
                $"keeper-tray-presets-selfcheck-{System.Environment.ProcessId}");
            void Drop()
            {
                try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
                catch (System.IO.IOException) { }
            }
            // 顶层项集的实测结果：菜单句柄上**真的**读回来的每一项（字、id、灰不灰）。
            // 不是自检自己抄一份表 —— 抄一份就量的是那份副本（同 CODING_STANDARDS.md 第 6 条）。
            string Top(LifeModeList modes)
            {
                using var tree = Build(Hooks(() => modes), tr);
                var top = tree.Root;
                return string.Join("|", Enumerable.Range(0, Win32.GetMenuItemCount(top))
                    .Select(i => $"{i}:{Str(top, i)}:{Win32.GetMenuItemID(top, i)}:{On(top, i, Win32.MfGrayed)}"));
            }
            try
            {
                // **对照**：这份枚举确是现问的 —— 换一份名单，子菜单上半真的换了。
                var otherNames = new List<string> { "甲", "乙" };
                using (var other = Build(Hooks(() => new LifeModeList(otherNames, "乙", false)), tr))
                {
                    var otherSub = Win32.GetSubMenu(other.Root, PosOfString(other.Root, "生活模式"));
                    for (var i = 0; i < otherNames.Count; i++)
                        if (Str(otherSub, i) != otherNames[i])
                            throw new Exception($"换了一份名单，子菜单第 {i} 项是 '{Str(otherSub, i)}'，" +
                                               $"该是 '{otherNames[i]}' —— 这一份枚举不是现问的");
                }

                Drop();
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "一份预设.jsonc"),
                    $"{{\n  \"{Settings.VersionKey}\": {Settings.SchemaVersion}\n}}\n");
                // 现场一：还没收进来（那个目录里有一份，名单上只有手存的那几个）。
                var before = Top(new LifeModeList(names, "画画", false));
                // 现场二：**收进来了** —— 名字从那一份只读目录现问（不手抄），它们在名单上就是
                // 几条普通的生活模式。
                var took = PresetLifeModes.Load(dir).Presets.Select(p => p.Name).ToList();
                if (took.Count == 0) throw new Exception("那一份只读目录里一列都没列出来，收进来那一档量不到");
                var after = Top(new LifeModeList([.. names, .. took], "画画", false));
                if (before.Length == 0) throw new Exception("顶层项集读回来是空的 —— 那一发什么也没量");
                if (before != after)
                    throw new Exception($"收进来那几份之后顶层项集变了：收之前 {before}，收之后 {after} —— " +
                                       "托盘在这一族只该多「切」的那几行（在子菜单里），不该在顶层多一项");
                // 子菜单里那几行是**能切的生活模式**（id 落在生活模式那一段里），不是一颗不递动作的项。
                using (var tree = Build(Hooks(() => new LifeModeList([.. names, .. took], "画画", false)), tr))
                {
                    var sub = Win32.GetSubMenu(tree.Root, PosOfString(tree.Root, "生活模式"));
                    for (var i = 0; i < took.Count; i++)
                    {
                        var at = names.Count + i;
                        if (Str(sub, at) != took[i])
                            throw new Exception($"子菜单第 {at} 项是 '{Str(sub, at)}'，该是收进来的那一份 '{took[i]}'");
                        if (Win32.GetMenuItemID(sub, at) != IdLifeModeFirst + (uint)at)
                            throw new Exception($"收进来的「{took[i]}」那一项递不出生活的模式的 id");
                    }
                }
            }
            finally
            {
                Drop();
            }
        });
        checks.Check("托盘那一张表上没有任何一行带画廊那一句话（措辞从生活模式那一族的表上问，不手抄）", () =>
        {
            // 措辞从 <see cref="Options.LifeModeMenuLabels"/> **问**：那一族在界面上的每一句
            // 都在那儿，手抄一份就是会腐烂的副本（加一句忘了抄，判据立刻开始漏）。
            var galleryWords = Options.LifeModeMenuLabels
                .Where(key => key.Contains("预设", StringComparison.Ordinal)).ToList();
            if (galleryWords.Count == 0)
                throw new Exception("生活模式那一族的措辞表里一句带「预设」的都没有 —— " +
                                   "这一发量的「托盘没加那一项」，连它该长什么样都问不出来");
            var hooks = Hooks(good);
            foreach (var row in AllRows(hooks).Concat(LifeModeRows(hooks)))
            {
                // 生活模式名是**用户的数据**（Raw），用户管自己的生活模式叫「预设生活模式」是
                // 他的自由 —— 所以只查我们自己的措辞那一栏（Key 那一列）。
                if (row.Raw) continue;
                var said = galleryWords.FirstOrDefault(one => row.Key.Contains(one, StringComparison.Ordinal));
                if (said != null)
                    throw new Exception($"托盘上有「{row.Key}」那一行，而画廊那一句话是「{said}」");
            }
        });

        return checks.Report();
    }

    // ---- 自检：托盘「时间块」那一行（组名 routine-tray）-----------------------
    //
    // **这一组量的是「那一行在不在」和「那一行上是什么」，量的是树上真读回来的项** ——
    // 菜单是用 `Build` 造的同一棵树（和 lifemodes-tray 那一组同一个理由），
    // 所以判过的是产品跑的那一棵，不是自检自己摆的一份像它的。
    //
    // 「不出现」那一半最容易做成假象：只搜一句块名的话，一个**空着的**「时间块」照样过。
    // 所以判据数的是**项数**（少一个就是真的少一行）外加逐项过一遍字。

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf BlockRow = new(
        () => "托盘「时间块」那一行通过：在块里那一行带着块名、点不动也不递 id（Esc 的 0 落空）、" +
              "不在任何块里 / 没有表 / 总开关关着时那一行不出现（项数差一，不是空着的一行）、" +
              "其余那几颗一颗不少、番茄钟那一行照旧（永远在、跑着才有后半句、有块名时两行并排）",
        "托盘时间块那一行", BlockRowSelfTest);

    /// <summary>托盘「时间块」那一行：在块里带着块名，不在块里 / 没有表 / 总开关关着都不出现；
    /// 而番茄钟那一行照旧（永远在，只是后半句可能是空的）。</summary>
    public static List<string> BlockRowSelfTest()
    {
        var checks = new Checks();
        var tr = new Func<string, string>(s => s);
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        var fired = new List<string>();
        TrayHooks Hooks(Func<string?> block, long pomodoroLeft = 0) => new(
            Show: () => fired.Add("遮罩"),
            Calendar: () => fired.Add("日历"),
            Auto: () => fired.Add("自动淡入"),
            Options: () => fired.Add("设置"),
            About: () => fired.Add("关于"),
            Exit: () => fired.Add("退出"),
            SecondLaunch: () => fired.Add("又一发"),
            Pomodoro: () => fired.Add("番茄钟"),
            // 「开机自动启动」给成「没勾着」：它在树上、也递得出动作，但这一组量的不是它
            // （在不在、勾对不对由 autostart-tray 那一组量，两组量的是同一棵树上不同的两行）。
            // 动作照样记进 fired —— 万一有一发误落到它上面，这里看得见。
            Autostart: () => fired.Add("开机自动启动"),
History: () => fired.Add("休息历史"),
            SwitchLifeMode: name => fired.Add($"切:{name}"),
            ManageLifeModes: () => fired.Add("管理生活模式"),
            AutoFadeOn: () => false,
            PreviewOn: () => false,
            CalendarOn: () => false,
            PomodoroOn: () => pomodoroLeft > 0,
            AutostartOn: () => false,
            PomodoroLeftMs: () => pomodoroLeft,
            LifeModes: () => new LifeModeList([], null, false),
            BlockName: block,
            // 这一组量的是「时间块」那一行，所以「勿扰中」那一行不出现（在不在是
            // dnd-silence 那一组量的那一半，两组量的是同一棵树上不同的两行）；
            // 「下次休息还有多久」那一行同理（在不在是 tray-break 那一组量的）。
            DndWhy: () => null,
            BreakLeftMs: () => null, Today: () => new TodayList([], false));

        var settings = new Settings();
        settings.Blocks.Add(new TimeBlock
        {
            StartMinute = 9 * Routine.MinutesPerHour, EndMinute = 17 * Routine.MinutesPerHour,
            Name = "上午", Fade = true, YieldsToInput = true, Flow = "",
        });
        var inside = 10 * Routine.MinutesPerHour;
        var outside = 20 * Routine.MinutesPerHour;

        /// 那一行在不在（<b>带着名字</b>才算在）、项数多少、每一项的字是什么。<b>数的是项数</b>
        /// 「不出现」是少一行，不是一句字搜不到。那份列表由 <see cref="ReadRows"/> 出 ——
        /// 勿扰那一行在 dnd-silence 那一组量的是同一把尺子。
        (bool There, int Items, List<string> Texts) Read(TrayHooks hooks)
        {
            var texts = ReadRows(hooks, tr);
            // 标签 + 两个空格 + 块名（番茄钟那一行是同一种写法）。**带着名字才算那一行在**
            // ——「不出现」另有两条判据在数项数，而一个空着的「时间块」两条都躲不过。
            var row = $"{tr("时间块")}  ";
            return (texts.Any(t => t.StartsWith(row, StringComparison.Ordinal) && t.Length > row.Length),
                    texts.Count, texts);
        }

        checks.Check("对照：两种问法给的答案和菜单上那一行是一回事", () =>
        {
            // **对照先行**：块名是 `BlockRowName` 问出来的（App 走的是同一个方法），
            // 而菜单上那一行的字是「标签 + 两个空格 + 那个名字」。词从语言表问。
            var name = Tray.BlockRowName(settings, inside);
            Expect(name == settings.Blocks[0].Name, $"{Routine.Clock(inside)} 问出的块名是 '{name}'");
            var hooks = Hooks(() => Tray.BlockRowName(settings, inside));
            var read = Read(hooks);
            Expect(read.There, "菜单上没有那一行 —— 对照不成立");
            Expect(read.Texts.Any(t => t == $"{tr("时间块")}  {name}"),
                $"菜单上那一行的字是 '{(read.Texts.FirstOrDefault(t => t.StartsWith(tr("时间块"), StringComparison.Ordinal)) ?? "（没有）")}'，" +
                $"该是「{tr("时间块")}  {name}」");
        });

        checks.Check("在块里：那一行在；不在块里 / 没有表 / 总开关关着：那一行不在（项数差一）", () =>
        {
            var inBlock = Read(Hooks(() => Tray.BlockRowName(settings, inside)));
            var outsideBlock = Read(Hooks(() => Tray.BlockRowName(settings, outside)));
            var noTable = Read(Hooks(() => Tray.BlockRowName(new Settings(), inside)));
            settings.RoutineEnabled = false;
            var switchedOff = Read(Hooks(() => Tray.BlockRowName(settings, inside)));
            settings.RoutineEnabled = true;

            foreach (var (what, read) in new[] { ("不在任何块里", outsideBlock), ("压根没有表", noTable),
                                                 ("总开关关着", switchedOff) })
            {
                Expect(!read.There, $"{what}的时候菜单上还摆着「时间块」那一行 —— 整棵树读回来是：" +
                                    string.Join(" / ", read.Texts));
                // **项数**：一个空着的「时间块」照样有一项，所以数项数，不搜字。
                Expect(read.Items == inBlock.Items - 1,
                    $"{what}的时候菜单上有 {read.Items} 项，在块里有 {inBlock.Items} 项 —— 差的不是一");
                Expect(!read.Texts.Contains(tr("时间块"), StringComparer.Ordinal),
                    $"{what}的时候还留着一个空的「{tr("时间块")}」");
            }
            // **对照（一定该通）**：在块里那一发本身要成立，而且要的是「多出来的那一行」——
            // 拿同一棵树数，别信任何一句手抄的项数。
            Expect(inBlock.There && inBlock.Items == outsideBlock.Items + 1,
                $"在块里那一行没有多出来（{inBlock.Items} 项 vs {outsideBlock.Items} 项）");
        });

        checks.Check("总开关关着：托盘上那一行也不出现，而其余那几颗一颗不少", () =>
        {
            var on = Read(Hooks(() => Tray.BlockRowName(settings, inside)));
            settings.RoutineEnabled = false;
            var off = Read(Hooks(() => Tray.BlockRowName(settings, inside)));
            settings.RoutineEnabled = true;
            foreach (var (what, text) in new[] { ("进入休息", "进入休息"), ("桌面日历", "桌面日历"),
                                                 ("自动淡入", "自动淡入"), ("番茄钟", "番茄钟"),
                                                 ("设置…", "设置…"), ("关于 Keeper", "关于 Keeper"),
                                                 ("退出", "退出") })
                if (!on.Texts.Contains(text, StringComparer.Ordinal) || !off.Texts.Contains(text, StringComparer.Ordinal))
                    throw new Exception($"「{what}」那一行在总开关翻面的前后有一边不见了");
            Expect(off.Texts.Count == on.Texts.Count - 1,
                $"总开关关着时少了 {on.Texts.Count - off.Texts.Count} 行，该是 1（就那一行）");
        });

        checks.Check("番茄钟那一行照旧：永远在，跑着的时候带后半句、没跑的时候没有后半句", () =>
        {
            // 这一行是「永远在、后半句可空」，和「时间块」那一行是「要么带名字、要么不出现」
            // **两种不同的形状** —— 所以这一组把两种各量一遍，别让改「时间块」顺手改坏它。
            var quiet = Read(Hooks(() => Tray.BlockRowName(settings, inside), pomodoroLeft: 0));
            var running = Read(Hooks(() => Tray.BlockRowName(settings, inside), pomodoroLeft: 65_000));
            var expectSuffix = $"{tr("番茄钟")}  {Clock(65_000)}";
            if (!quiet.Texts.Contains(tr("番茄钟"), StringComparer.Ordinal))
                throw new Exception("番茄钟没跑的时候那一行不见了");
            if (quiet.Texts.Any(t => t.StartsWith(tr("番茄钟"), StringComparison.Ordinal) && t != tr("番茄钟")))
                throw new Exception($"番茄钟没跑的时候那一行还带着后半句：'{quiet.Texts.First(t => t.StartsWith(tr("番茄钟"), StringComparison.Ordinal))}'");
            if (!running.Texts.Contains(expectSuffix, StringComparer.Ordinal))
                throw new Exception($"番茄钟跑着的时候那一行是 '{string.Join(" / ", running.Texts.Where(t => t.StartsWith(tr("番茄钟"), StringComparison.Ordinal)))}'，" +
                                    $"该有一项是「{expectSuffix}」");
            // **对照（一定该通）**：项数一个都没多 —— 后半句是那一行自己的字，不是新的一行。
            Expect(quiet.Items == running.Items, $"番茄钟跑着的时候菜单上多了一项（{quiet.Items} → {running.Items}）");
            // **对照**：有块名的那一行和番茄钟那一行同时在，两行都在。
            Expect(running.Texts.Contains(expectSuffix, StringComparer.Ordinal)
                   && running.Texts.Any(t => t.StartsWith(tr("时间块"), StringComparison.Ordinal)),
                "有块名的时候番茄钟那一行不见了（两行是并排的，不是二选一）");
        });

        checks.Check("那一行点不动、也不递 id（Esc 递回来的 0 落在这里什么也不触发）", () =>
        {
            var hooks = Hooks(() => Tray.BlockRowName(settings, inside));
            using var tree = Build(hooks, tr);
            var at = PosOfString(tree.Root, $"{tr("时间块")}  {Tray.BlockRowName(settings, inside)}");
            Expect(at >= 0, "菜单上没有「时间块」那一行");
            if (!On(tree.Root, at, Win32.MfGrayed)) throw new Exception("「时间块」那一行不是灰的 —— 它是实测结果不是命令");
            if (On(tree.Root, at, Win32.MfChecked)) throw new Exception("「时间块」那一行被打上了勾");
            fired.Clear();
            DispatchTo(Win32.GetMenuItemID(tree.Root, at), "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"按「时间块」那一行触发了 {string.Join("/", fired)}");
            fired.Clear();
            DispatchTo(0, "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"Esc 递的 0 触发了 {string.Join("/", fired)}");
            // **对照（一定该通）**：同一颗菜单上别的命令项照旧能触发 ——
            // 不然上面那些可能只是「递什么都落空」。
            DispatchTo(IdOptions, "自检", hooks, _ => { });
            if (fired.Count != 1 || fired[0] != "设置") throw new Exception("对照不成立：递一个真 id 什么都没触发");
        });

        return checks.Report();
    }

    // ---- 自检：托盘「下次休息还有多久」那一行（组名 tray-break）----------------
    //
    // **这一族量什么**：那一行**在不在**、**它写着的数对不对**、以及它点不动。
    // 「在不在」和「时间块」「勿扰中」那两行同一个形状（要么带内容、要么不出现），
    // 而「对不对」问的是 `Scheduler.BreakLeftMs` 那一段减法 —— 它是纯算术，
    // 所以这里量得到（自检是 headless 的，而这一段不碰屏）。
    //
    // **量得到的是那一段减法**：它量的是「给我这一段走了多久，它算出来还差多久」。
    // 那个「走了多久」由 `Scheduler.ElapsedMs` 答（现实时间），产品那一侧每帧递一次。
    // 自检里量的是接缝那一半 —— `scheduler` 那一组量的是同一段减法在假时钟下的行为。

    /// <summary>这一组自己的绿话和红话，跟着判据住。</summary>
    internal static readonly CriterionOf BreakLeftRow = new(
        () => "托盘「下次休息还有多久」那一行通过：它摆在菜单顶上第一行、写的是「这一段还差多久到线」" +
              "（它报的和触发量的是同一个数 —— 2026-10-21 所有者定夺之后触发按现实时间走）；" +
              "这一段走得越久那个数越小、到线报 0；" +
              "自动淡入关着 / 番茄钟在跑 / 遮罩已经抬着时那一行整个不出现（项数差一，不是空着的一行）；" +
              "它点不动也不递 id（Esc 的 0 落空），底下那几颗命令项照旧能触发（对照）；" +
              "过了一小时那一档写法变成 hh:mm:ss 而底下一小时以内的那几档一个字没变",
        "托盘下次休息那一行", BreakLeftSelfTest);

    /// <summary>托盘「下次休息还有多久」那一行：量它写着的数、在哪几档不出现、点不动。</summary>
    public static List<string> BreakLeftSelfTest()
    {
        var checks = new Checks();
        var tr = new Func<string, string>(s => s);
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        var fired = new List<string>();
        var settings = new Settings();
        var owed = (long)settings.ActivityTimeoutMin * 60_000;

        TrayHooks Hooks(long? left) => new(
            Show: () => fired.Add("遮罩"), Calendar: () => fired.Add("日历"),
            Auto: () => fired.Add("自动淡入"), Options: () => fired.Add("设置"),
            About: () => fired.Add("关于"), Exit: () => fired.Add("退出"),
            SecondLaunch: () => fired.Add("又一发"), Pomodoro: () => fired.Add("番茄钟"),
            Autostart: () => fired.Add("开机自动启动"),
History: () => fired.Add("休息历史"),
            SwitchLifeMode: name => fired.Add($"切:{name}"), ManageLifeModes: () => fired.Add("管理"),
            AutoFadeOn: () => settings.AutoFade, PreviewOn: () => false, CalendarOn: () => false,
            PomodoroOn: () => false, AutostartOn: () => false,
            PomodoroLeftMs: () => 0, LifeModes: () => new LifeModeList([], null, false),
            BlockName: () => null, DndWhy: () => null, BreakLeftMs: () => left, Today: () => new TodayList([], false));

        (int Items, List<string> Texts) Read(long? left)
        {
            var texts = ReadRows(Hooks(left), tr);
            return (texts.Count, texts);
        }

        checks.Check("它写着的数是「这一段还差多久到线」，而这一段越久那个数越小", () =>
        {
            // 先问那一段减法本身（产品的规矩全在这一处，自检不能自己重算一遍）：
            var fresh = Scheduler.BreakLeftMs(settings, 0, false, false);
            var half = Scheduler.BreakLeftMs(settings, owed / 2, false, false);
            var atLine = Scheduler.BreakLeftMs(settings, owed, false, false);
            Expect(fresh == owed, $"刚开始的时候报 {fresh}，该是满的 {owed}（{owed / 60000} 分钟）");
            Expect(half == owed / 2, $"这一段走了半程报 {half}，该是 {owed / 2}");
            Expect(atLine == 0, $"到线了报 {atLine}，该是 0 —— 到线之后它是在等一个输入的空档，"
                                + "报一个正数会让人以为「还有这么久才来」");
            // 过了线也不该报负数。
            Expect(Scheduler.BreakLeftMs(settings, owed * 3, false, false) == 0,
                "过了线报了一个负数");
            // 菜单上写的就是这个数。**对照（一定该通）**：同一段算术，同一个数。
            var (_, texts) = Read(fresh);
            var expect = $"{tr("break.left")}  {Clock(fresh!.Value)}";
            Expect(texts.Contains(expect, StringComparer.Ordinal),
                $"菜单顶上那一行的字是 '{texts.FirstOrDefault() ?? "（没有）"}'，该有一项是「{expect}」");
        });

        checks.Check("它摆在菜单顶上第一行（在「进入休息」上面）", () =>
        {
            var (_, texts) = Read(owed);
            Expect(texts.Count > 0, "菜单上一项都没有");
            Expect(texts[0].StartsWith(tr("break.left"), StringComparison.Ordinal),
                $"菜单第一行是 '{texts[0]}'，该是「{tr("break.left")}」那一行");
            var at = texts.FindIndex(t => t.StartsWith(tr("进入休息"), StringComparison.Ordinal));
            Expect(at > 0, $"「进入休息」在第 {at} 项 —— 它该排在这一行下面");
        });

        checks.Check("自动淡入关着 / 番茄钟在跑 / 遮罩已经抬着：那一行整个不出现（项数差一）", () =>
        {
            // **对照先行**：这一行该出现的那一档本身要成立。
            var showing = Read(owed);
            var none = Read(null);
            Expect(showing.Texts[0].StartsWith(tr("break.left"), StringComparison.Ordinal),
                "对照不成立：给了数它还是不在顶上");
            Expect(none.Items == showing.Items - 1,
                $"那一行不出现时菜单上有 {none.Items} 项，在的时候有 {showing.Items} 项 —— 差的不是一");
            Expect(!none.Texts.Contains(tr("break.left"), StringComparer.Ordinal),
                "不出现的那一档里还留着一个空的「break.left」");
            // 三档规矩各问一次 —— 它们是**产品那一条规则**（Scheduler.BreakLeftMs），
            // 这里量的是那一处真的照那三档走（喂进去的数相同，只翻那三个旗标）。
            Expect(Scheduler.BreakLeftMs(new Settings { AutoFade = false }, 0, false, false) == null,
                "自动淡入关着时那一行还报数 —— 它不会自己来");
            Expect(Scheduler.BreakLeftMs(settings, 0, true, false) == null,
                "番茄钟在跑时那一行还报着数 —— 那时活动那条整个让位，节奏由现实时间定");
            Expect(Scheduler.BreakLeftMs(settings, 0, false, true) == null,
                "遮罩已经抬着时那一行还报数 —— 屏幕上就有钟了，托盘再报一个数是两条打架的话");
        });

        checks.Check("它点不动、也不递 id，而底下那几颗命令项照旧能触发（对照）", () =>
        {
            var hooks = Hooks(owed);
            using var tree = Build(hooks, tr);
            var at = PosOfString(tree.Root, $"{tr("break.left")}  {Clock(owed)}");
            Expect(at >= 0, "菜单顶上没有那一行");
            if (!On(tree.Root, at, Win32.MfGrayed)) throw new Exception("那一行不是灰的 —— 它是实测结果不是命令");
            if (On(tree.Root, at, Win32.MfChecked)) throw new Exception("那一行被打上了勾");
            fired.Clear();
            DispatchTo(Win32.GetMenuItemID(tree.Root, at), "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"按那一行触发了 {string.Join("/", fired)}");
            fired.Clear();
            DispatchTo(0, "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"Esc 递的 0 触发了 {string.Join("/", fired)}");
            DispatchTo(IdOptions, "自检", hooks, _ => { });
            Expect(fired.Count == 1 && fired[0] == "设置",
                $"对照不成立：递一个真 id 触发的是 {string.Join("/", fired)}");
        });

        checks.Check("过了一小时那一档要写 hh:mm:ss，而底下一小时以内的那几档一个字没变", () =>
        {
            // **这一条不许回归成「一律 m:ss」**：这个键的上限是 120 分钟，
            // 而「还剩 90:00」读不出那是九十分钟。
            Expect(Clock(90 * 60_000) == "1:30:00", $"90 分钟那一档写成了 '{Clock(90 * 60_000)}'");
            Expect(Clock(120 * 60_000) == "2:00:00", $"120 分钟那一档写成了 '{Clock(120 * 60_000)}'");
            // 对照：番茄钟那一行（到不了两小时）写法一个字没变。
            Expect(Clock(25 * 60_000) == "25:00", $"25 分钟那一档写成了 '{Clock(25 * 60_000)}'");
            Expect(Clock(65_000) == "1:05", $"65 秒那一档写成了 '{Clock(65_000)}'");
        });

        return checks.Report();
    }
    //
    // **这一组量的是「接线」**：那棵树建出来没有、那一行在不在、勾跟不跟当下那份意图、
    // 递回来的 id 落不落到它自己身上。量的是 <see cref="Build"/> 造的那一棵 ——
    // 和产品跑的是同一个方法（和 routine-tray / lifemodes-tray 同一个理由）。
    //
    // **量不到的是「托盘那一下有没有真的写注册表」**：注册表那一半是
    // `Autostart.SelfTest` 在一个自建的临时键上量的（登录项本身一个字节不动），
    // 而「托盘只改那一个键、然后交给对账」是 App.ToggleAutostart 那一行的形状 ——
    // 这一组不重演它，重演了就是量一份仿制品。
    //
    // 整组**不碰 %APPDATA%、不碰注册表**：钩子全在这一族自己造。

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf AutostartRow = new(
        () => "托盘「开机自动启动」那一行通过：意图开着那一行在且打着勾、id 原样挂着、按下去只触发它自己；" +
              "意图翻面勾跟着翻面而那一行一直在；0 和一个没人递的 id 都落空，「自动淡入」那颗照旧能触发（对照）",
        "托盘开机启动那一行", AutostartRowSelfTest);

    /// <summary>托盘「开机自动启动」那一行：那一行在、勾跟着当下那份意图、递回来的 id
    /// 落到它自己身上；意图翻面之后勾跟着翻面（菜单现做，没有要刷的缓存）；
    /// 而 0 和一个不存在的 id 都落空 —— 别的命令项照旧能触发（对照）。</summary>
    public static List<string> AutostartRowSelfTest()
    {
        var checks = new Checks();
        var tr = new Func<string, string>(s => s);
        void Expect(bool condition, string otherwise)
        {
            if (!condition) throw new Exception(otherwise);
        }

        var fired = new List<string>();
        var intent = false;
        TrayHooks Hooks() => new(
            Show: () => fired.Add("遮罩"),
            Calendar: () => fired.Add("日历"),
            Auto: () => fired.Add("自动淡入"),
            Options: () => fired.Add("设置"),
            About: () => fired.Add("关于"),
            Exit: () => fired.Add("退出"),
            SecondLaunch: () => fired.Add("又一发"),
            Pomodoro: () => fired.Add("番茄钟"),
            Autostart: () => fired.Add("开机自动启动"),
History: () => fired.Add("休息历史"),
            SwitchLifeMode: name => fired.Add($"切:{name}"),
            ManageLifeModes: () => fired.Add("管理生活模式"),
            AutoFadeOn: () => false,
            PreviewOn: () => false,
            CalendarOn: () => false,
            PomodoroOn: () => false,
            // **现问**：菜单每次右键现做，所以这一问拿的是当下那一刻，不是构造时那一份 ——
            // 这一条判的就是它（下面「翻面」那一发量的是真效果）。
            AutostartOn: () => intent,
            PomodoroLeftMs: () => 0,
            LifeModes: () => new LifeModeList([], null, false),
            // 「时间块」和「勿扰中」那两行不在（在不在是 routine-tray / dnd-silence 量的），
            // 而这一组要量的就是开机启动这一行。「下次休息还有多久」那一行同样不在
            // （在不在是 tray-break 量的）—— 底下几颗命令项的 id 一颗一颗对账，
            // 多一行在那里不影响，可它在那儿就等于这一组顺手量了它。
            BlockName: () => null, DndWhy: () => null, BreakLeftMs: () => null, Today: () => new TodayList([], false));

        checks.Check("对照：意图开着的时候那一行在、带着勾、id 原样挂着、按下去只触发它自己", () =>
        {
            intent = true;
            var hooks = Hooks();
            using var tree = Build(hooks, tr);

            // **按字找不按序号**：菜单里不数项，序号会漂。
            var at = PosOfString(tree.Root, tr("autostart"));
            Expect(at >= 0, $"顶层没有一颗写着「{tr("autostart")}」的项（读回来的是：{string.Join(" / ", ReadRows(hooks, tr))}）");
            if (!On(tree.Root, at, Win32.MfChecked))
                throw new Exception("意图开着，而那一行没打勾 —— 勾没跟着当下那份意图走");
            if (On(tree.Root, at, Win32.MfGrayed))
                throw new Exception("那一行被打成了灰的 —— 它点得动，灰的是「时间块」那种实测结果");
            var id = Win32.GetMenuItemID(tree.Root, at);
            if (id != IdAutostart) throw new Exception($"那一行的 id 是 {id}，该是 {IdAutostart}");

            fired.Clear();
            DispatchTo(id, "自检", hooks, _ => { });
            if (fired.Count != 1 || fired[0] != "开机自动启动")
                throw new Exception($"按那一行触发的是「{string.Join("/", fired)}」，该是且只是「开机自动启动」");
        });

        checks.Check("意图翻面：勾跟着翻面，而那一行一直在（它不是「时间块」那种会消失的行）", () =>
        {
            intent = false;
            var hooks = Hooks();
            using var off = Build(hooks, tr);
            var at = PosOfString(off.Root, tr("autostart"));
            Expect(at >= 0, "意图关着的时候那一行不见了 —— 它该一直在，只是没勾");
            if (On(off.Root, at, Win32.MfChecked))
                throw new Exception("意图关着，而那一行还留着勾");

            // **翻面那一下**：同一个钩子，把意图改过来重摆一次 —— 菜单现做，所以新的一棵
            // 该立刻带上勾。这条量的是「现问」，量的是真树上读回来的状态位。
            intent = true;
            using var on = Build(hooks, tr);
            var again = PosOfString(on.Root, tr("autostart"));
            Expect(again >= 0, "翻面之后那一行不见了");
            if (!On(on.Root, again, Win32.MfChecked))
                throw new Exception("意图翻成开着，重摆的树上那一行还是没勾 —— 菜单里有一份要刷的缓存");
        });

        checks.Check("0 和一个不存在的 id 都落空，而别的命令项照旧能触发（对照）", () =>
        {
            var hooks = Hooks();
            using var tree = Build(hooks, tr);

            // 分隔线和 Esc 递回来的正是 0：那一行不该有 id 0，不然按「生活模式」会开成别的东西。
            foreach (var row in AllRows(hooks))
                if (row.Run != null && row.Id == 0)
                    throw new Exception($"「{row.Key}」那一行的 id 是 0 —— 分隔线和 Esc 递的正是这个数");

            fired.Clear();
            DispatchTo(0, "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"Esc 递的 0 触发了「{string.Join("/", fired)}」");
            fired.Clear();
            DispatchTo(999, "自检", hooks, _ => { });
            if (fired.Count != 0) throw new Exception($"一个没人递的 id 触发了「{string.Join("/", fired)}」");
            // **对照（一定该通）**：同一颗菜单上别的命令项照旧能触发 ——
            // 不然上面那些可能只是「递什么都落空」。
            fired.Clear();
            DispatchTo(Win32.GetMenuItemID(tree.Root, PosOfString(tree.Root, tr("自动淡入"))), "自检", hooks, _ => { });
            if (fired.Count != 1 || fired[0] != "自动淡入")
                throw new Exception($"对照不成立：递「自动淡入」那个 id 触发的是「{string.Join("/", fired)}」");
        });

        return checks.Report();
    }

    /// 第 <paramref name="pos"/> 项上那个字符串（<c>MF_BYPOSITION</c>）。**分隔线读到的是空串** ——
    /// <c>GetMenuStringW</c> 对它返回 0，所以「这一项没有字」和「这一项的字是空」分不开，
    /// 而判据要的就是那一句（分隔线上不该有字）。
    private static string Str(nint menu, int pos)
    {
        var text = new StringBuilder(128);
        Win32.GetMenuString(menu, (uint)pos, text, 128, Win32.MfByPosition);
        return text.ToString();
    }

    /// 第 <paramref name="pos"/> 项上有没有 <paramref name="flag"/> 那一位（<c>MF_BYPOSITION</c>）。
    ///
    /// **写成一句而不是让调用方写 <c>State(...) &amp; X != 0</c>**：C# 里 <c>!=</c> 比 <c>&amp;</c> 紧，
    /// 那个写法编出来是「先比后与」，症状是 <c>uint &amp; bool</c> 编译不过；真编过去的话
    /// 它量的是别的东西（&amp;&amp; 和 &amp;&amp; 在 C# 里同优先级、从左到右）。
    private static bool On(nint menu, int pos, uint flag) =>
        (Win32.GetMenuState(menu, (uint)pos, Win32.MfByPosition) & flag) != 0;

    /// 写着 <paramref name="text"/> 的那一项在哪（<c>-1</c> 就是没有）。
    /// **按字找而不是按序号写死** —— 菜单里不数项，序号会漂。
    private static int PosOfString(nint menu, string text)
    {
        for (var p = 0; p < Win32.GetMenuItemCount(menu); p++)
            if (Str(menu, p) == text) return p;
        return -1;
    }

    private static void Item(nint menu, uint id, string text, bool isChecked, bool grayed)
    {
        // MF_GRAYED 是**这行点不动**，MF_SEPARATOR 是**这一行不是命令**：
        // 分隔线那一条走 AppendMenu 的 MF_SEPARATOR 形态（id 递 0），不经过这里。
        var flags = Win32.MfString | (isChecked ? Win32.MfChecked : 0u) | (grayed ? Win32.MfGrayed : 0u);
        Win32.AppendMenu(menu, flags, id, text);
    }

    /// PNG 从引擎的 VFS 读进来，自己摊成 32 位 BGRA：托盘要的是 HICON，引擎只给 Texture2D，
    /// 而纹理走 VRAM 压缩，图标那点 alpha 经不起再压一道。尺寸按 SM_CXSMICON 问来的那个槽位走，
    /// 100% 缩放下那是 16 而不是 PNG 自己的 32（<c>SM_CXSMICON</c> 那一条在
    /// <c>docs/research/win32-constants-and-structs.md</c>）。注意这条路读的是**原始文件**（<c>FileAccess.Open</c> 拿
    /// <c>res://src/tray/*.png</c>）：真要导出，preset 得把那三张当非资源文件带上，否则包里只有
    /// 引擎导入过的那份，这一发读到的是 null 并且抛「读不到」。
    /// <b>这一条没有实测结果</b> —— 这台机器上从没导出过，仓库里也没有 <c>export_presets.cfg</c>
    /// （2026-09-28 对着工作区核过）。
    private static nint Icon(string path, int side)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"读不到 {path}");
        var bytes = file.GetBuffer((long)file.GetLength());
        if (bytes.Length == 0) throw new InvalidOperationException($"读不到 {path}");
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        if (image.LoadPngFromBuffer(bytes) != Error.Ok) throw new InvalidOperationException($"{path} 解不开");
        image.Convert(Image.Format.Rgba8);
        if (image.GetWidth() != side || image.GetHeight() != side) image.Resize(side, side);
        var rgba = image.GetData();
        var bgra = new byte[rgba.Length];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        return Win32.IconFromBgra(side, side, bgra);
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = Data(0, IntPtr.Zero, "");
        Win32.ShellNotifyIcon(Win32.NimDelete, ref data);
    }

    public void Dispose()
    {
        RemoveIcon();
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        foreach (var icon in _icons.Values) Win32.DestroyIcon(icon);
        _icons.Clear();
    }
}
