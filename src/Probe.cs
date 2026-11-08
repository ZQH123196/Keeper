using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace Keeper;

/// The two checks that had to be measured before anything got built: does click-through
/// work, and does a window survive being parented into the wallpaper. Both measurement results are
/// in docs/research/desktop-layer-and-window-measurements.md (the parenting one is there). Run it with
///     Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/Probe.tscn
/// and read .tmp\keeper-probe.log. The window stays 480x320 in one corner on
/// purpose, so the desktop behind it stays usable while this runs.
///
/// Run 3, narrowed to the one thing run 2 left ambiguous: a click fell through
/// even after WS_EX_TRANSPARENT was cleared by hand, so the engine flag must be
/// doing something else. Every route is therefore measured on its own, with the
/// other one held off, and each is reversed to prove it was the cause.
public partial class Probe : Node
{
    private const string Title = "keeper-probe";
    private const int ProbeWidth = 480;
    private const int ProbeHeight = 320;

    private static string LogPath => LogWriter.ProbePath;

    private Window _win = null!;
    private ColorRect? _fill;
    private Label? _cueLabel;
    private nint _hwnd;
    private nint _wallpaper;
    private Win32.Rect _dressed;
    private long _styleBeforeParenting;
    private long _exStyleBeforeParenting;
    // What the run decides. Each one is read off a pair of measurements: the
    // effect present, and the effect gone again once the cause is removed.
    private bool _engineFlagCausesFallThrough;
    private bool _rawBitCausesFallThrough;
    private bool _engineFlagSurvivesEngineOps;
    // 「摆着」这一族的三问（2026-10-12 换了性质：不再 SetParent，改成顶层 + 压在桌面正上方）。
    // 原来问的是「爹还在不在」，现在问的是「有没有人偷偷给了我们一个爹」——
    // 这一族现在是顶层窗，**永远不该有爹**，有了就是引擎或 shell 把它挂到了什么地方去。
    private bool _parkedStaysParentless;
    private bool _parkedSurvivesFrames;
    private bool _parkedSurvivesEngineResize;
    private bool _parkedSurvivesEngineTitle;

    // What the alpha step decides: whether the compositor shows the desktop through
    // us at all, and which blending model the engine's alpha follows — that one is
    // what the fade's ramp gets authored against.
    private bool _transparencyWorks;
    private string _blendModel = "undecided";
    private bool _blendSurvivesShowHide;

    // The part a machine cannot decide on its own: a real click, from a hand, on a
    // desktop icon underneath the fader. The hook sees every button event wherever
    // it lands, and the shell's context menu is a window that appears only if the
    // desktop itself received the right click — so the hand's action still gets
    // recorded rather than taken on trust.
    private nint _mouseHook;
    private nint _iconView;
    private Win32.HookProc? _mouseHookProc;
    private HashSet<nint>? _windowsBeforeMenu;
    private readonly List<string> _summaries = [];
    private int _clicksInsideUsDuringHold;
    private int _engineMouseEvents;
    private int _pressesTotal;
    private int _pressLogCount;
    private int _menusSeen;
    private bool _menuSnapshotPending;
    private bool _wantsHandCheck;
    private bool _wantsPush;
    private bool _wantsSweep;
    private bool _wantsGrid;
    // 前台身份那一档（工单 01）。它问的输入是「此刻屏幕上真的占着前台的那个程序」，
    // 无头造不出来，所以不走 --check，单独一个旗，形状和 --push / --sweep 一样：没有退出码。
    private bool _wantsForeground;
    // 拖放/刻度线那一档（`.scratch/keeper-debt/issues/` 里那条「拖放命中与吸附的本机实测结果」）。
    // 形状和 --push / --fg 一样：单独一个旗、自己一扇小窗、不带退出码。
    // 实测结果落 docs/research/godot-drag-and-snap.md —— 那一份今天只有官方文档那一层。
    private bool _wantsDrag;
    // 落侧那一发（docs/research/godot-drag-and-snap.md 第八节 8.3 那一问）。
    // 和 --drag 的区别是**喂法**：那一发走引擎的 PushInput（进程内），落侧一格都走不到。
    // 这一发走系统那一层（Win32.SendInput），并且先验「事件真的进了这扇窗」——
    // 没验这一条就等于把「没送到」读成「引擎不给」。
    private bool _wantsDrop;
    // 画布那一问：真控件画布上自绘的那一层，引擎重排一次还在不在。
    // （上一轮把「画布用真控件还是自绘」说成「答不了」，其实答案已经量到了 ——
    // 画布是一个真 Control、子控件是真 Panel、拖放整趟走通。剩下的是这一问。）
    private bool _wantsCanvas;
    // 钩子能不能吃掉那一档（工单：拿掉点击窗那一族）。它要答的是「回调返回非零，那一下到不到
    // 底下的程序」，而这一发不量别的 —— 见 HookSwallowMatrix。
    private bool _wantsSwallow;
    // 「铺满那一扇到底穿不穿透」那一问，量的是**真的点一下到不到**，
    // 而现有的判据量的是命中测试 —— 两句不是同一句，见 OverlayClickThroughMatrix 开头。
    private bool _wantsClickThrough;
    // **接收方自己当一扇窗起，只负责数收到几下**。它和上面那一发唯一的区别是
    // **它在另一个进程里** —— 而那正是要量的东西：`WM_NCHITTEST` 的 HTTRANSPARENT
    // 只跨同一个线程。见 OverlayClickThroughMatrix 开头。
    private bool _wantsCrossTarget;
    // 图标列那一问（工单 26）：图标层答得出来的「哪几列真的有图标」。
    // 纯读，不碰桌面、不碰用户那份文档、不带退出码 —— 形状同 --fg / --push。
    private bool _wantsIconColumns;

    public override async void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _wantsHandCheck = System.Array.IndexOf(args, "--hold") >= 0;
        _wantsPush = System.Array.IndexOf(args, "--push") >= 0;
        // 桌面那一半单独一个开关：它最小化的是别人家的窗，不带这个旗就一步都不动桌面。
        _wantsSweep = System.Array.IndexOf(args, "--sweep") >= 0;
        // 格子那一档（wallpaper / outline）欠的两发实测结果：图标层和桌面大小的抬起。见 GridLayerAndLift。
        _wantsGrid = System.Array.IndexOf(args, "--grid") >= 0;
        _wantsForeground = System.Array.IndexOf(args, "--fg") >= 0;
        _wantsDrag = System.Array.IndexOf(args, "--drag") >= 0;
        _wantsDrop = System.Array.IndexOf(args, "--drop") >= 0;
        _wantsCanvas = System.Array.IndexOf(args, "--canvas") >= 0;
        // 钩子吃掉那一档。**要真屏**（headless 下没有底下的程序可点），跑法见
        // `CODING_STANDARDS.md`「另外两发测量程序的跑法」那一节补的这一行。
        _wantsSwallow = System.Array.IndexOf(args, "--swallow") >= 0;
        _wantsClickThrough = System.Array.IndexOf(args, "--passthrough-click") >= 0;
        _wantsCrossTarget = System.Array.IndexOf(args, "--passthrough-target") >= 0;
        // 图标列那一问（工单 26）。它只读图标层，所以分在 ReadLiveDocument 和 Baseline 之前。
        _wantsIconColumns = System.Array.IndexOf(args, "--icon-columns") >= 0;
        Log("begin",
            $"godot={Engine.GetVersionInfo()["string"]} renderer={RenderingServer.GetVideoAdapterName()} " +
            $"pid={System.Environment.ProcessId} args={string.Join(' ', args)}");

        if (System.Array.IndexOf(args, "--check") >= 0)
        {
            // 自检只在这一支里跑。原来登记加跑都在分叉**之前**（那时叫 SelfChecks），于是
            // --push / --hold / --grid 每一跑都先付全部自检那一跑的代价的代价，而那几跑一个字都不看自检的实测结果。
            // 登记本身不要钱（只是把委托排进一张表），跑才是那笔钱。
            var result = RegisterSelfCheck().Run(this);

            // The rendered document goes out beside the log for a pair of eyes: the checks can
            // say a key is present, they cannot say the file reads like something to edit by hand.
            var sample = Path.Combine(Tmp.Root, "keeper-doc-sample.jsonc");
            File.WriteAllText(sample, SettingsDoc.Render(new Settings()));
            Log("check", $"首启文档样本：{sample}");
            Log("check", result.Summary);
            GetTree().Quit(result.ExitCode);
            return;
        }

        // Only this machine's document, and only read: this run is a measurement pass.
        if (_wantsPush)
        {
            // 鼠标桥那一条问句自己跑，不碰这轮其余那几组判据。
            await PushMatrix();
            GetTree().Quit();
            return;
        }

        // 前台身份那一档也在这里分叉，**在 ReadLiveDocument 和 Baseline 之前**：
        // 这一档量的是「此刻前台是谁」，所以它自己一扇窗都不能开、别的窗也不能动 ——
        // Godot 起来那扇主窗（非 headless）会一起抢前台，读到的就变成「测量程序和目标谁在前台」。
        if (_wantsForeground)
        {
            await ForegroundIdentity(SecondsOf(args, "--fg"));
            GetTree().Quit();
            return;
        }

        // 拖放那一档：它自己造一扇小窗、一块画布、一颗可拖的控件，和上面那几族一样先分叉。
        // 分在 ReadLiveDocument 和 Baseline 之前 —— 它量的是引擎在不在、形状对不对，
        // 不量用户那份文档，也不碰这一扇窗的基线。
        if (_wantsDrag)
        {
            await DragMatrix();
            GetTree().Quit();
            return;
        }

        if (_wantsDrop)
        {
            await DropSide();
            GetTree().Quit();
            return;
        }

        if (_wantsCanvas)
        {
            await CanvasMatrix();
            GetTree().Quit();
            return;
        }

        // 钩子吃掉那一档：它要一个真实的底下程序（不是空桌面），所以不像 --fg 那样带 headless 跑，
        // 而是像 --drag / --drop 那样要 GUI。分在 ReadLiveDocument 和 Baseline 之前 ——
        // 它不量用户那份文档，也不碰这一扇窗的基线。
        if (_wantsSwallow)
        {
            await HookSwallowMatrix();
            GetTree().Quit();
            return;
        }

        // 「铺满那一扇一辈子 click-through」这一问，同上和 --swallow 一样要真屏、要 GUI。
        if (_wantsClickThrough)
        {
            await OverlayClickThroughMatrix();
            GetTree().Quit();
            return;
        }

        // 接收方自己当一扇窗起，和发起那一发**分处两个进程**。要真屏、要 GUI。
        if (_wantsCrossTarget)
        {
            await CrossProcessTarget();
            GetTree().Quit();
            return;
        }

        // 图标列那一问：图标层答得出来的「哪几列真的有图标」。
        // 分在 ReadLiveDocument 和 Baseline 之前 —— 它要真图标层在、真日历窗摆在桌面上，
        // 而这两样别处那几发都不该碰。
        if (_wantsIconColumns)
        {
            await IconColumns();
            GetTree().Quit();
            return;
        }

        ReadLiveDocument();

        try
        {
            await Baseline();
            await DressAsFader();

            if (_wantsHandCheck)
            {
                // The hold is the point of this run, so the window is set down and
                // left alone rather than resized in front of the hand.
                Log("hand", "skipping the engine-op steps; the block will not move once it lands");
                await HoldForTheHand();
            }
            else if (_wantsGrid)
            {
                // 格子那一档的两发实测结果，不跑其余那些组（它要把这扇窗摆成 1792x980，
                // 后面每一步的形状都不是它自己的形状了）。
                await GridLayerAndLift();
            }
            else
            {
                // The fader's whole life is hidden → raised → faded → hidden, so "does hiding
        // actually uncover the pixel" is a product question. Run here and again after the
        // wallpaper steps, because a no-op measured only on a window that has been
        // parented and lifted would not say which of the two did it.
        await HideRoutes("pre_parent");
        await PassthroughApiMatrix();
                await EngineFlagMeasuredAlone();
                await RawBitMeasuredAlone();
                await EngineOpsOnTopOfTheEngineFlag();
                await ParkedAndLeftAlone();
                await EngineOpsWhileParked();
                LiftOutOfPark();
                await RealAlpha();
            }
        }
        catch (Exception e)
        {
            Log("fatal", e.ToString());
        }

        Verdict();
        // 取向（2026-09-29 写下来，免得下一轮以为是漏了）：**--check 之外的窗口阶段故意不带退出码。**
        // 那些阶段交出来的是散文实测结果 —— check 1/3/4 那几个 bool 加一堆现场数，要人和日志一起读，
        // 不是「过 / 不过」一票。把它们折成一个退出码，等于把**没量到的 false** 当成失败；
        // Verdict() 自己开头就挡了这一条（headless 那一档那四组实测结果全是 false，一个都不成立）。
        // 整个仓库唯一决定退出码的地方是 SelfCheck.Result.ExitCode，只有 --check 用它。
        GetTree().Quit();
    }

    // ---- step 1: who am I --------------------------------------------------

    /// 登记自检里那一串组。**登记不等于跑** —— <see cref="SelfCheck"/> 才跑，而它只在 <c>--check</c>
    /// 那一支里被叫到。原来这一份叫 <c>SelfChecks</c>，跑在分叉**之前**，于是 <c>--push</c> /
    /// <c>--hold</c> / <c>--grid</c> 每一跑都先付全部自检那一跑的代价的代价，而那几跑一个字都不看自检的实测结果。
    ///
    /// 顶上原来那句「两个不需要窗的自检」是错的，2026-09-29 改准：自检里有<b>要一棵活节点树的那几组</b>
    /// —— <c>options</c>（实例化 <c>Options.tscn</c>）、<c>calendar-ui</c>（建一棵真的
    /// <c>Calendar</c>）、<c>io</c>、<c>flows</c>、<c>defaults</c>。其余各组是纯算术，
    /// 或只碰自己那个用完就删的临时目录。**没有一组碰 %APPDATA%** —— 那份文档是用户的设置，
    /// 不是测试数据。
    ///
    /// 顺序就是登记顺序，组数由 <c>SelfCheck</c> 自己数出来印 —— 所以「自检里第几组」不再靠记忆。
    /// 原来第 1 组不抄那四行，因为它那份 list 就是累加器，「必须排第一」是一条<b>没写下来的</b>
    /// 接口约束；现在累加器在 <c>SelfCheck</c> 里面，排第几都不影响结果。
    private static SelfCheck RegisterSelfCheck() => new SelfCheck(Log)
        .Check("settings", Settings.Table)
        .Check("jsonc", SettingsStore.Jsonc)
        .Check("scheduler", Scheduler.StateMachine)
        // 卡片 01 的读模型：SnapshotOf 和五个查询，每一条都拿改前的读法当对照。
        .Check("scheduler-read", Scheduler.ReadModel)

        // 钩子那族的判定。上一组走的是 Input 那个 seam 的假时钟（FakeInput），这一组走的是
        // 同一条 seam 的真那一侧。判定原来住在两个私有回调体内，唯一可测的形状是 Feed(nowMs)，
        // 而 Feed 只 Record、判定拿不到 —— 那条判据是空的，Feed 自己还零调用方（已删）。
        // 现在判定是 Activity.Judge：一个纯函数，自检里造得出事件、也造得出时序。
        .Check("activity", Activity.ActivityJudge)
        // 记录那一族那半共同的纪律：宽容读、读不通立旗、写前先留 .bak、无 BOM。
        // 这一组量的是**纪律本身**（从前散在四份里、每份只量自己那一处）；
        // 那四组（calendar-notes / history / todos / flows-index）量的是「各自接上了没有」
        // 和「各自的形状」，两组不互相顶替。flows-index 登记在下面很远的地方。
        .Check("record-doc", RecordDoc.Discipline)

        // 收拢那件事本身（2026-10-05）。九组自检和产品的 `OpenOptions` 现在共用
        // `OptionsHost` 一份开窗实施，而**「次序对不对」从此谁都不再自己量**了：
        // 那句注释从十处收到一处，注释不会报红。所以次序得在这里变成能失败的判据。
        // 这一组**要一棵活节点树**（它自己开窗）。
        .Check("options-host", OptionsHost.Host)

        // 日历的备注。这一发对的是「形状是这一轮定的，而且它真是这个形状」，
        // 见 CalendarNotes 的类型注释。
        .Check("calendar-notes", CalendarNotes.NotesDoc)

        // 另两份记录：休息历史和休息清单。同一族判据（读宽容、写整体、坏文件不许静默抹），
        // 清单那一发还要额外证「根是数组也照样认注释」—— 那正是它不能用 JsoncDoc 读的原因。
        .Check("history", History.Rest)
        .Check("todos", Todos.Checklist)

        // 休息历史的按天读法（工单 01）。纯算术、不碰界面 —— 和上面那组量的不是同一件事：
        // 那一组量的是「读宽容、写整体、坏文件不许静默抹」，这一组量的是「归天、倒序、
        // 一天那一个数」。读不通那一档两边都判（它是这一族共同的那一档）。
        .Check("history-days", HistoryDays.Days)

        // 休息历史那扇窗（工单 02）：量的是「三档长得不一样」—— 有记录摆天、没记录说
        // 「还没记过休息」、读不通说「休息记录读不通」，而**读不通那一句不许是空列表**
        // （空读起来是「你没休息过」，而磁盘上那份读不通的人明明记过）。
        .Check("history-window", HistoryWindow.Shown)

        // 两个入口（工单 03）：托盘一条、设置窗一格，两处通到同一个动作。
        // 它要一棵活节点树（要真开一扇设置窗），所以走要 host 的那一档。
        .Check("history-entry", HistoryWindow.Entry)

        // 首启逐字节改名。它证的正是老版那几份文档搬得过来，所以两头都要：搬完字节一样，
        // 而两份都在的时候哪一份都不许动。**目录也一起量**（生活模式那几份住的那个子目录
        // 老名字 flows/ → lifemodes/）：目录没法读出字节比，比的是里面每一样东西的相对名和字节。
        .Check("rename", Records.Rename)

        // 农历。底是几条公开的历法事实（2026-09-11 八月初一、2026-09-25 中秋节），
        // 加两条常识（2026 年春节、2025 年闰六月），所以 BCL 那张表哪天换了实现这里会先响。
        .Check("lunar", Lunar.LunarDates)

        // 日历那一扇窗的算术：格子怎么排、面板摆在哪个角、桥的闸门该不该开、什么算双击。
        // 真的挂进壁纸、真的命中测试、真的抬起打字，那些要在跑起来那一发读（--calendar）。
        .Check("calendar", Calendar.Arithmetic)

        // 装窗那一条：2026-09-27 之前每次启动日历都报两发 "already has a parent"，屏上却一切照常，
        // 因为引擎对被拒的 AddChild 只印一行。这一组跑一扇真的日历，并证明「再挂一次」现在会抛。
        // 它是自检里五组要活节点树的第一组。
        .Check("calendar-ui", Calendar.Build)

        // 原生层叠子窗那一族（工单 28 第 1 到第 5 步）。**纯算术**：宿主变了没有、
        // 该不该重挂、该不该读回、「日历看得见」那一问答得对不对，四问都拆成了纯函数
        // （`NativeLayer` 里那几发），所以这一组造假句柄就能判死「宿主换了没认出来」
        // 和「一个像素都没推进去却答看得见」。
        //
        // **它量的是「看得见」那一族，不是「图标压在日历上面」** —— 后者要屏上那一块，
        // 在测量程序那一发（`run.cmd layered`，docs/research/ 那一节）和真机那一发读。
        .Check("native-layer", NativeLayer.Criterion)

        // 宿主生命周期那一族（卡片 03 · S26）。**纯算术**：三档加意图位的转移表、
        // 看门狗和补推的计时与顺序、窗没了的判据，全走 FakeHostOps ——
        // 不碰窗、不碰桌面、不碰 Godot，所以「读不到 HWND 不改档」「抬失败仍停在 Editing」
        // 「NoMove 不发推送」这些在 --check 里就判得死，不用真机。
        .Check("calendar-host", CalendarHost.Criterion)

        // 桌面层那一份快照（卡片 04 · S21）。**纯算术**：壁纸层那一问、桌面那一扇那一问、
        // 日历宿主那一问都是对假快照作答的纯函数，所以七条判据不碰桌面、不发消息 ——
        // 钉的是「两问的筛选条件本来就不同、不许合并」「0x052C 只在宿主那一问发」
        // 「Progman 为零时三条退路各走各的」。Read 本身不进自检，由真机那一发覆盖。
        .Check("desktop-snapshot", DesktopLayer.Criterion)

        // 闸门第三问和快照保活那一族。**纯算术**：「那一点上有没有图标」和
        // 「读失败要不要覆盖上一份好的」都拆成了纯函数（`DesktopIcons` 里那几发），
        // 所以这一组造几份读数就能判死「一读空就把闸门全关上」，不碰桌面、不发消息、不碰 COM。
        // （原先这一组叫 `icon-yield`，量的是日历按图标挖洞那一族；那一族 2026-10-14 撤了。）
        .Check("icon-gate", DesktopIcons.IconGate)

        // 鼠标桥那一对按下/松开（工单 27 第二轮）。**纯算术**：决策本体是 `MouseBridge.Plan`，
        // 真窗那一侧的出口全从参数递进来，所以这一组造假队列、假窗、假出口就能判死
        // 「按下落掉了松开还喂」「松开喂在松手那一格」「闸门答的是抽干那一刻」。
        .Check("bridge-pair", MouseBridge.Bridge)

        // 2026-10-12 撤掉了两组：wallpaper 和 wallpaper-source。
        // 它们量的是「我们自己画的那一份壁纸摆得对不对」「源文件读不读得到」——
        // 而日历从那天起不再自己画壁纸（每像素透明回到 DWM 合成，src/Wallpaper.cs 整份退休），
        // 没有「我们自己画的那一份」可对了。今天要验的那件事改由
        // Shots.AlphaMeasurementAllThree 那一发在跑起来的时候量：底下那片桌面透不透得过来。

        // 遮罩该不该躲开「别人正占着独占全屏」。判定是纯的（矩形对矩形 + 一份 shell 类名表），
        // 所以那五条规矩在自检里各判一次；真的前台窗口是谁，留给跑起来那一发的日志。
        .Check("fullscreen", Fullscreen.Detection)

        // 前台应用怎么认人（工单 02）。判的是匹配键那几条规矩，是个纯函数（不碰 Win32、
        // 不读配置），所以整名精确、大小写不敏感、子串不认、全路径不当键都能在这一组各判一次；
        // 「问得到 / 问不到」那一发也在这里分得开 —— 实测结果给的是「不是这一行」，而「问不到」
        // 和「不在名单上」是两种，分开靠的是 Known（拿这个 false 当成「不在名单上」的那一份，
        // 到了窗一最小化那两路都读不到的时候就会静悄悄放过一个真在跑的勿扰名单）。
        .Check("foreground", Foreground.Match)

        // 声音：要不要响那四个条件的真值表（2026-09-27 多了「落定没有」这一档）。
        // 播放本身要音频设备，不在自检里跑。
        .Check("sound", Sound.When)

        // 遮罩背后那张图的五种摆法：摆位是纯算术，所以五种各算一次（和 Wallpaper 那一组同一个理由）。
        .Check("picture", FaderPicture.Picture)

        // 遮罩上那两行字（工单 04）。**要一棵活节点树**：起一扇真的 Fader、走真的 Enter，
        // 读那两颗标签自己身上的文字和可见性 —— 「在树上」不等于「画出来了」（规矩第 2 条），
        // 而这一组问的正是后者。只碰内存：作息表是现造的一行，不读也不写任何文件。
        .Check("mask-text", Fader.Content)

        .Check("lang", Lang.Table)

        // 日志那一发不许把程序带崩：2026-09-27 那一跑就是被一行日志断在 _Ready 半路的 ——
        // 托盘标题读回来一个落单的代理对，而 Mono 的 File.AppendAllText 不带编码时走系统 ANSI
        // 码页（这台机器 936），写不出去是「抛」不是「换」。
        // 判据现在住在 LogWriter 自己那儿：那支笔原来有三份实施，两道保护只在其中一份上，
        // 而没保护的那两份写的是同一份文件。收成一个模块之后，保护和判据都在一处。
        .Check("logging", LogWriter.Logging)

        // The Options form is a scene, so it can drift from the declaration table without
        // anyone noticing: a key renamed in one place is a row that writes nowhere, and a
        // dropped row is a setting nobody can reach. This is the check that says so.
        .Check("options", Options.Form)

        // 「一眼看到一切」（2026-10-07 所有者定夺：「域标签 不许被藏」）。真窗上逐条问：
        // 每一格里的每一行在不在屏幕上、该格该有的卡片在不在、窗头那一行的形状对不对；
        // 搜索是唯一还能把行收起来的控件，所以两头夹着问它 —— 只问「该出现的出现」的话，
        // 一只从不过滤的测量程序也能过。
        // 域接回来以后这一组是**逐格**量的：2026-10-06 那一发只量了开窗那一格就宣布全绿，
        // 而域标签被删了、11 张卡平铺着，于是「全在场」量到的和「全摆出来」是同一件事。
        .Check("nav", Options.Nav)

        // 界面上那几条「不许出现」：配置文件内部的名字（**词表从声明表派生** —— 键名、取值、
        // 文件名、那张表的键和字段名）、毫秒，以及**把休息流程叫「生活模式」**（那一族合法地在用
        // 那个词，所以是「除了生活模式这一族不许有」，不是「一个都不许有」；许用名单从
        // Options.LifeModeMenuLabels 派生，不是手抄的）。前两条只有在**真窗**上才量得到 ——
        // 标签和提示是 Texts() 灌进去的，SelfTest 那一份（只 Instantiate 不 AddChild）
        // 里每一颗标签都是空的。
        // 「名字」那一条是 2026-09-30 晚些时候从「一张五个词的清单」改成派生的：那张清单当场漏过
        // calendar.style 那条提示里的 wallpaper / outline / widget（实测结果在
        // docs/research/godot-4-7-2-ui-appearance-measurements.md 之外、也记在 Options.ForbiddenWords
        // 的注释里），加一个键就得记得往清单里抄一个词，忘了不报任何错。
        .Check("screen", Options.Screen)

        // 登录项：意图和现场那张表是纯的，那三只手在 HKCU 下一个自建的临时键里走一遍真的。
        // 用户那个 Run 键一个字节都不动。
        .Check("autostart", Autostart.LoginItem)

        // 托盘那一行（开机启动）。量的是接线：那一行在树上、勾跟当下那份意图、递回来的
        // id 落到它自己身上、0 和一个不存在的 id 都落空。不碰 %APPDATA%、不碰注册表。
        .Check("autostart-tray", Tray.AutostartRow)

        // 导出/导入那两颗钮：临时目录里开一扇真的设置窗，按下去、交路径、对字节。
        .Check("io", Options.Io)

        // flows 编辑器那一条整链：临时目录里开一扇真的窗，换一条、改一步、加一步、改名、
        // 新增、删掉，按保存，再从磁盘读回来对。全程不发鼠标事件（那一路在 --push 里量）。
        .Check("flows", Options.Flows)

        // 作息表编辑器那一条整链：临时目录里一份带两行时间块的文档、开一扇真的窗，
        // 拧时刻、起名字、勾开关、挑一条休息流程、上移、加一块、删一块，按保存，再从磁盘
        // 读回来对。契约里作息表那一节的规矩在
        // 「作息表是一张按现实时间走的表，时间块是表里的行」。
        .Check("routine-editor", Options.RoutineEditor)

        // 生活模式这一族（工单 01「存下一个生活模式，切得回去」）。三组分的是**要什么**：名单和编号
        // 那一族碰的是它自己那个用完就删的子目录；切换那一族要一份真的活文档 + 备份；
        // 窗上那一族要一棵活节点树（所以只有它收 host）。契约里这一族的规矩在
        // 「生活模式是一整套配置的快照，它住在一份名单和一份文档上」那一节。
        .Check("lifemodes-index", LifeModeIndex.Index)
        .Check("lifemodes-switch", LifeModeStore.Switch)
        .Check("lifemodes-manage", LifeModeStore.Manage)

        // 「从一份文件收一个生活模式进来」（工单 05）。**这一族是第三件事**：导出仍是那份文档
        // 本身、导入仍是整个换成活的并留备份（最后一档就量这两条没变），收进来的那一档只碰
        // flows/ 底下那两样。承重处是「收进来的那一刻不顶掉当前配置」——所以那一段量的是活的那
        // 一份逐字节没动、没多出备份、而窗头那颗钮还写着原来那个生活模式，并配一条「切过去才换」。
        // 给进来的那一份是**预设模板那一种形状**（完整的配置文档 + 一张作息表 + 注释 + 陌生键），
        // 所以这把「原样搬」的尺子连**嵌套的那张表**一起量（表在活的那一份上也得读得出来）。
        .Check("lifemodes-takein", Options.LifeModeTakeIn)

        // 作息表那一族（工单 01）。三组分的是**要什么**：前两组是纯算术，第三组走
        // 生活模式现成那两条路并只碰自己那个用完就删的临时目录。三组都从声明表问键名，
        // 不抄一份清单（加一条作息表的键，三组自动跟着它走）。
        .Check("routine-blocks", Routine.Blocks)
        .Check("routine-arith", Routine.Arithmetic)
        .Check("routine-lifemode", Routine.LifeMode)

        // 块到点那一族（工单 03）。**五组分的是「要什么」**：那一刻（只在跨过边界的那一分钟、
        // 错过不补）、那一次是不是休息、两把钥匙、睡眠的静默、总开关。第六组在托盘那一侧。
        // 全部纯算术：假时钟 + 状态机 + 在内存里造的表，不碰磁盘也不碰 %APPDATA%；
        // 时刻一律算出来（不抄 1439），块名取自各组自己造的表，键名和默认值问声明表。
        .Check("routine-fire", Scheduler.RoutineFire)
        .Check("routine-notbreak", Scheduler.RoutineNotABreak)
        .Check("routine-guard", Scheduler.RoutineGuard)
        .Check("routine-sleep", Scheduler.SleepSilence)
        .Check("routine-switch", Scheduler.RoutineSwitch)
        .Check("routine-tray", Tray.BlockRow)

        // 托盘顶上那行「下次休息还有多久」（2026-10-08）。它挨着 routine-tray 那一组：
        // 那两组量的是**同一棵树上的两行**（时间块那一行 / 休息倒计时那一行），
        // 而它们各自把对方的 hook 给成 null —— 免得动一行顺手改坏另一行量的项数。
        .Check("tray-break", Tray.BreakLeftRow)

        // 勿扰那一族（工单 03）。三组分的是**要什么**：纯求值（dnd-arith）、状态机那一侧的守卫
        // （dnd-silence，走假时钟）、生活模式往返（dnd-lifemode）。键名和星期几那七个词都从
        // 声明表 / 那一族问，不抄一份清单。
        .Check("dnd-arith", Dnd.Arithmetic)
        .Check("dnd-silence", Scheduler.DndSilence)
        .Check("dnd-lifemode", Dnd.LifeMode)

        // 「读不通有三档，各说各的」（工单 03）。**这一族是测量程序最容易假绿的地方**：
        // 「读不通就当没有」在自检里看着是绿的，而它把一份坏名单说成了「一个生活模式都没有」。
        // 所以三档各一条判据 + 每一档一条「一定该通」的对照（好的一发放进去先量一遍），
        // 并且三档那句话不许塌成一（互不相同、互不为子串、另外两档不出现）。
        .Check("lifemodes-unreadable", Options.LifeModeUnreadable)

        // 「恢复默认」那一条：值要进去，屏幕上那一格里的字也得同帧进去 —— 旧数留在那里
        // 一闪，就是他真鼠标点出来的那一条。
        .Check("defaults", Options.RestoreDefaults)

        // 窗头那颗「生活模式：…」按钮和它展开的菜单：临时目录里开一扇真的设置窗，按菜单自己发的
        // 那个 id 存一个生活模式（空名字当场不给、说清为什么）、再按同一个 id 切过去。
        // 弹层那半步（真鼠标点在菜单项上）按 CODING_STANDARDS.md 只能拿真鼠标量，
        // 而「菜单项被选中并递回 id」这一半在这里量得到 —— 它是这一组要量的那一半。
        .Check("lifemodes-menu", Options.LifeModeMenu)

        // 托盘那颗「生活模式」子菜单（工单 04）。**这一组只量接线**：子菜单挂上去了没有、
        // 勾打在哪一条、id 是不是原样挂在那一项上、递回来的 id 落到哪一行。
        // 弹层那半步（屏幕上真的能张开、点得中）无头量不到 —— `TrackPopupMenu` 是模态的、
        // 要真屏幕，所以这一张的收工条件是「自检 + 实拍帧」，不是自检（契约 194 行）。
        // 名单是这一组自己递进去的，所以不碰磁盘也不碰 %APPDATA%；名单本身从磁盘读那一段
        // 在 flows-index / flows-unreadable 那一族里。
        .Check("lifemodes-tray", Tray.LifeModeMenu)

        // 预设生活模式的画廊（工单 05）。**两组分的是「要什么」**：画廊那一组要一扇真的画廊窗
        // （列是真控件、每列一颗「收进来」）和一个它自己那一份预设目录（用完就删）；
        // 入口那一组要一扇真的设置窗（按菜单里那一项 / 按管理器上那颗钮）。**两组都不碰
        // %APPDATA%**，也不写装在程序里那一个预设目录（只读它 —— 判据里有一发单独量那个目录）。
        .Check("presets-gallery", Presets.Gallery)
        .Check("presets-entries", Presets.Entries)

        // 随安装带来的那几份**有出处的生活模式**逐份过契约（工单 06）。
        // **名单是现问的**：目录里有几份就跑几份，不存一份会腐烂的名字清单
        // （`CODING_STANDARDS.md` 第 6 条）——「恰好四份」是出厂状态，不是判据。
        // 跑在 `.tmp` 里自己那个用完就删的目录，只读装在程序里那一个预设目录。
        .Check("presets-templates", PresetTemplates.Templates)

        // 读像素那一把尺子自己。这一组不需要窗，也不需要屏幕 —— 它问的是「CLR_INVALID 折不折成
        // 一个合法颜色」「两发都没读到会不会判成透的」「三份混色收成一份之后有没有漂」，
        // 全是纯算术，所以自检里没有屏幕也照样量得到。屏幕上那一侧的实测结果在各测量程序手里
        // （--shot 的透明度链、--fader-shot 的遮罩、测量程序 alpha 那一段）。
        .Check("frames", Frames.PixelRuler);

    // 自检里的 logging 那一组原来住在这儿（LoggingSelfTest），是当时唯一一组判据不住在被测模块
    // 旁边的。2026-09-29 那支笔收进 LogWriter 时判据跟着搬过去了 —— 三条断言一个字没改，
    // 只是从「自检里抄一份实施」变成「模块自己交一份」。

    /// The document on disk right now, read but never written. It is still the one the old
    /// build wrote, so this line is the new reader's first measurement of it — how many keys it
    /// has to fill in, and what it takes back from a file it never rendered.
    private static void ReadLiveDocument()
    {
        var dir = SettingsStore.LiveDir();
        if (!File.Exists(SettingsStore.FilePath(dir)))
        {
            Log("live", $"{dir}：还没有那份文档");
            return;
        }

        var read = SettingsStore.Load(dir);
        var keys = JsoncDoc.TryParse(read.Text, out var doc, out _) ? doc!.Values.Count : 0;
        var diff = Settings.Diff(new Settings(), read.Current);
        Log("live",
            $"{Settings.FileName} 存在，格式版本={read.DocumentVersion}（这个构建写 {Settings.SchemaVersion}），" +
            $"文档里写了 {keys} 个键，声明表认 {Settings.Declarations.Length} 个，" +
            $"读回来和默认值的差别：{(diff.Length == 0 ? "没有" : diff)}");
        if (read.Error != null) Log("live", "读不通的地方：" + read.Error);
    }

    private async Task Baseline()
    {
        _win = GetWindow();
        _win.Title = Title;
        _fill = GetNodeOrNull<ColorRect>("Layer/Fill");
        _cueLabel = GetNodeOrNull<Label>("Layer/Label");
        if (_cueLabel != null)
        {
            _cueLabel.Position = new Vector2(12, 12);
            _cueLabel.Size = new Vector2(296, 250);
            _cueLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
        await Frame(0.4f);

        var fromEngine = DisplayServer.WindowGetNativeHandle(
            DisplayServer.HandleType.WindowHandle, _win.GetWindowId());
        var fromEnum = FindOwnWindowByTitle();
        _hwnd = fromEngine != 0 ? (nint)fromEngine : fromEnum;

        Log("baseline",
            $"hwnd_engine=0x{fromEngine:X} hwnd_enum_by_title=0x{fromEnum:X} " +
            $"hwnd_enum_by_class=0x{FindOwnWindowByClass():X} using=0x{_hwnd:X}");

        if (_hwnd == 0)
        {
            Log("baseline", "no HWND — nothing below this can be measured");
            return;
        }

        ReportWindow("baseline", "before any change", withAbove: true);
        LogGeometry();
        await Frame(0.2f);
    }

    // ---- step 2: dress it like a fader, and take the control measurement -------

    private async Task DressAsFader()
    {
        // The plan's finding: always_on_top only lands when set from code.
        _win.Borderless = true;
        _win.AlwaysOnTop = true;
        _win.MousePassthrough = false;
        _win.Position = new Vector2I(240, 240);
        _win.Size = new Vector2I(ProbeWidth, ProbeHeight);
        await Frame(0.4f);

        ReportWindow("dress", "borderless + topmost, both passthrough routes off", withAbove: true);
        if (!HitTestOwnWindow("dress_control"))
            Log("dress_control", "the control failed — nothing below this means anything");
    }

    // ---- step 3: every documented route, judged without a hand -------------

    /// Godot exposes click-through more than one way and they are not the same
    /// thing: MousePassthroughPolygon is the region that *accepts* events, so an
    /// empty array switches passthrough off — the opposite of what the plan's
    /// GDScript pass assumed when it called the empty polygon and concluded the
    /// feature was unconfirmed. The Window.MousePassthrough bool documents itself
    /// as passing events to "the underlying window of the same application", which
    /// is not a cross-process switch either. Each row is judged by who owns a point
    /// in the middle of this window, because that answer was already shown to match
    /// what a real click did.
    private async Task PassthroughApiMatrix()
    {
        var rows = new (string Name, Action Apply)[]
        {
            ("none", () => { }),
            ("polygon_empty", () => DisplayServer.WindowSetMousePassthrough([])),
            ("polygon_whole_window", () => DisplayServer.WindowSetMousePassthrough(
                [new Vector2(0, 0), new Vector2(ProbeWidth, 0),
                 new Vector2(ProbeWidth, ProbeHeight), new Vector2(0, ProbeHeight)])),
            ("polygon_one_point", () => DisplayServer.WindowSetMousePassthrough([new Vector2(60, 60)])),
            ("bool_property", () => _win.MousePassthrough = true),
            ("raw_style_bit", () => SetTransparentBit(true)),
        };

        var works = new List<string>();
        foreach (var row in rows)
        {
            ResetPassthrough();
            row.Apply();
            await Frame(0.3f);

            var ex = (long)Win32.GetWindowLong(_hwnd, Win32.GwlExStyle);
            var owns = OwnsCenter();
            if (!owns) works.Add(row.Name);
            Log("matrix",
                $"{row.Name,-20} transparent_bit={(ex & Win32.WsExTransparent) != 0} " +
                $"layered_bit={(ex & Win32.WsExLayered) != 0} owns_its_own_middle={owns} " +
                $"what_is_underneath={UnderneathCenter()}");
        }

        ResetPassthrough();
        Log("matrix", $"routes that achieved click-through: {(works.Count == 0 ? "none" : string.Join(", ", works))}");
    }

    private void ResetPassthrough()
    {
        _win.MousePassthrough = false;
        DisplayServer.WindowSetMousePassthrough([]);
        SetTransparentBit(false);
    }

    private bool OwnsCenter()
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        var hit = Win32.WindowFromPoint(new Win32.Point { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 });
        return Win32.GetAncestorRoot(hit) == _hwnd;
    }

    private string UnderneathCenter()
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        var hit = Win32.WindowFromPoint(new Win32.Point { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 });
        return hit == IntPtr.Zero ? "nothing"
            : $"'{Win32.ClassName(hit)}' root '{Win32.ClassName(Win32.GetAncestorRoot(hit))}'";
    }

    // ---- step 4: click-through through the engine ---------------------------

    private async Task EngineFlagMeasuredAlone()
    {
        SetTransparentBit(false);

        _win.MousePassthrough = true;
        await Frame(0.3f);
        var on = HitTestOwnWindow("engine_on");
        ReportWindow("engine_on", "Window.MousePassthrough = true");

        _win.MousePassthrough = false;
        await Frame(0.3f);
        var off = HitTestOwnWindow("engine_off_again");
        ReportWindow("engine_off_again", "back to false — must own its own middle again");

        _engineFlagCausesFallThrough = !on && off;
    }

    // ---- step 4: the style bit, alone, on and then off ---------------------

    private async Task RawBitMeasuredAlone()
    {
        _win.MousePassthrough = false;

        SetTransparentBit(true);
        await Frame(0.3f);
        var on = HitTestOwnWindow("raw_on");
        ReportWindow("raw_on", "WS_EX_TRANSPARENT | WS_EX_LAYERED by hand, engine flag off");

        SetTransparentBit(false);
        await Frame(0.3f);
        var off = HitTestOwnWindow("raw_off_again");
        ReportWindow("raw_off_again", "bits cleared again");

        _rawBitCausesFallThrough = !on && off;
    }

    // ---- step 5: does the flag survive the engine's own window bookkeeping --

    private async Task EngineOpsOnTopOfTheEngineFlag()
    {
        _win.MousePassthrough = true;
        await Frame(0.3f);
        var before = HitTestOwnWindow("ops_before");
        ReportWindow("ops_before", "engine flag on, before any engine window call");

        _win.Size = new Vector2I(ProbeWidth, ProbeHeight + 20);
        await Frame(0.3f);
        var afterResize = HitTestOwnWindow("ops_resize");

        _win.Position = new Vector2I(260, 260);
        await Frame(0.3f);
        var afterMove = HitTestOwnWindow("ops_move");

        _win.AlwaysOnTop = false;
        _win.AlwaysOnTop = true;
        await Frame(0.3f);
        var afterTopmost = HitTestOwnWindow("ops_topmost");

        _win.Title = Title + " (tug)";
        await Frame(0.3f);
        var afterTitle = HitTestOwnWindow("ops_title");
        ReportWindow("ops_title", "after resize + move + topmost re-apply + title");

        // Every one of these measurement results is "the window owns its own clicks", so the
        // flag surviving the operation means none of them came back true.
        _engineFlagSurvivesEngineOps =
            !(before || afterResize || afterMove || afterTopmost || afterTitle);
        _win.Title = Title;
        await Frame(0.2f);
    }

    // ---- step 6: park it under the desktop, then leave it alone --------------
    //
    // 2026-10-12 之前这一步是 `SetParent` 进 `SHELLDLL_DefView`，然后一帧不动地问「爹还在不在」。
    // 今天这一族改了性质：日历**不再有爹**，它是顶层窗，靠 `DesktopLayer.Park` 压在桌面正上方。
    // 于是要问的那几问也翻了：
    //   旧：爹还在吗（答「在」才算过）
    //   新：**有爹就不对**（答 0 才算过）
    // 理由和那一族其余几处一起写在 DesktopLayer 的类型注释里。

    private async Task ParkedAndLeftAlone()
    {
        // 正本在 DesktopLayer.Look()：三扇都看一遍、挑爹的三条退路也在那儿。
        // 这一处原来自己写了一份，连 Win11 那个 WorkerW sibling 的枚举都是自己那份
        // （FindWorkerWSibling，注释里那句「Win11 puts the wallpaper in a WorkerW that is a
        // sibling of Progman」正是 DesktopLayer.Find 的退路）—— 同一件事两份实施，2026-09-29 收掉。
        var layer = DesktopLayer.Look();

        Log("wallpaper",
            $"progman=0x{layer.Progman:X} defview=0x{layer.DefView:X} workerw=0x{layer.WorkerW:X}");

        _wallpaper = DesktopLayer.DesktopTop();
        if (_wallpaper == IntPtr.Zero)
        {
            Log("wallpaper", "no desktop window found — check 2 undecided");
            return;
        }
        Log("wallpaper", $"要压在它正上方的是 0x{_wallpaper:X}/{Win32.ClassName(_wallpaper)}");

        _styleBeforeParenting = (long)Win32.GetWindowLong(_hwnd, Win32.GwlStyle);
        _exStyleBeforeParenting = (long)Win32.GetWindowLong(_hwnd, Win32.GwlExStyle);
        Win32.GetWindowRect(_hwnd, out _dressed);
        _parkedStaysParentless = Win32.GetParent(_hwnd) == IntPtr.Zero;

        Win32.SetWindowPos(_hwnd, _wallpaper, _dressed.Left, _dressed.Top, _dressed.Width, _dressed.Height,
            Win32.SwpFrameChanged | Win32.SwpNoActivate | Win32.SwpShowWindow);
        var ex = (long)Win32.GetWindowLong(_hwnd, Win32.GwlExStyle);
        Win32.SetWindowLong(_hwnd, Win32.GwlExStyle,
            (nint)(ex | Win32.WsExLayered | Win32.WsExTransparent | Win32.WsExNoActivate | Win32.WsExToolwindow));
        await Frame(0.4f);
        ReportWindow("parked", $"pinned above {Win32.ClassName(_wallpaper)}, nothing else done");

        _parkedSurvivesFrames = true;
        for (var i = 1; i <= 4; i++)
        {
            await Frame(0.4f);
            var held = Win32.GetParent(_hwnd) == IntPtr.Zero;
            _parkedSurvivesFrames &= held;
            Log("idle_poll",
                $"#{i} parent=0x{Win32.GetParent(_hwnd):X} 没有爹={held} {DesktopLayer.Where(_hwnd)} " +
                $"frames={Engine.GetFramesDrawn()}");
        }
    }

    private async Task EngineOpsWhileParked()
    {
        _win.Size = new Vector2I(ProbeWidth, ProbeHeight - 30);
        await Frame(0.4f);
        _parkedSurvivesEngineResize = Win32.GetParent(_hwnd) == IntPtr.Zero;
        ReportWindow("tug_resize", "engine resize while parked");

        Win32.SetWindowPos(_hwnd, DesktopLayer.DesktopTop(), _dressed.Left, _dressed.Top,
            _dressed.Width, _dressed.Height,
            Win32.SwpFrameChanged | Win32.SwpNoActivate | Win32.SwpShowWindow);
        await Frame(0.3f);
        _win.Title = Title + " (again)";
        await Frame(0.4f);
        _parkedSurvivesEngineTitle = Win32.GetParent(_hwnd) == IntPtr.Zero;
        ReportWindow("tug_title", "engine title while parked");
        _win.Title = Title;
    }

    // ---- step 8: back out, to the same pixel ------------------------------

    private void LiftOutOfPark()
    {
        Win32.SetParent(_hwnd, IntPtr.Zero);
        Win32.SetWindowLong(_hwnd, Win32.GwlStyle, (nint)_styleBeforeParenting);
        Win32.SetWindowLong(_hwnd, Win32.GwlExStyle, (nint)_exStyleBeforeParenting);
        Win32.SetWindowPos(_hwnd, Win32.HwndTopMost,
            _dressed.Left, _dressed.Top, _dressed.Width, _dressed.Height,
            Win32.SwpFrameChanged | Win32.SwpNoZorder);
        _win.MousePassthrough = false;

        Win32.GetWindowRect(_hwnd, out var now);
        ReportWindow("lifted", "restored to the rect recorded before parking");
        Log("lifted",
            $"same_pixel={now.Left == _dressed.Left && now.Top == _dressed.Top} " +
            $"was=({_dressed.Left},{_dressed.Top}) now=({now.Left},{now.Top})");
        HitTestOwnWindow("lifted_control");
    }

    // ---- step 9b: 格子那一档欠的两发实测结果：图标层、桌面大小的抬起 ----------------
    //
    // 这两发都是产品问句，不是引擎问句。设计文档（docs/design/calendar.md）说 wallpaper 那一档把整月铺在
    // 桌面上、两条边各占工作区一条百分比（widthPercent / heightPercent），而桌面图标住在
    // **同一扇** SHELLDLL_DefView 底下 ——
    // 谁压在谁上面、那一点归谁，是这一档独有的新问题（「大矩形压满桌面，命中测试和图标那一层
    // 是新问题」）。抬起打字那一发同样换了形状：
    // widget 那一档抬起的是 448x412，格子那一档抬起的是一扇 1792x980 的窗，它盖住什么没量过。
    //
    // 这一发不动图标、不点别人的窗。桌面那一半要 <c>--sweep</c> 才动（不带就只读兄弟 z 序和
    // 命中归谁，那两条不依赖桌面露不露出来）。图标的位置一律**不去问** SysListView32 要
    // （LVM_GETITEMPOSITION 要跨进程共享内存，一次错就把 Explorer 带走），改问「DefView 的子窗
    // 按 z 序排出来我们排在第几」—— 图标层就是那扇 SysListView32，兄弟之间的顺序决定谁在上面，
    // 而 GetWindow(GW_CHILD/GW_HWNDNEXT) 白给这个数。
    //
    // 走的是产品那一份代码（<see cref="DesktopLayer"/> 的 Capture / Embed / Lift / GrabForTyping），
    // 不是测量程序自己那份抄件：这一发要答的是「日历挂上去之后会怎样」，测量程序自己另写一套就等于没量。

    private async Task GridLayerAndLift()
    {
        var settings = new Settings();
        settings.ClampToDeclarations();
        var options = settings.Calendar;
        var work = WorkArea();
        var rows = Calendar.MonthCells(DateOnly.FromDateTime(DateTime.Now).Year,
            DateOnly.FromDateTime(DateTime.Now).Month, options.WeekStartsMonday).Count / 7;
        var want = Calendar.MeasureGrid(work, options, rows);
        Log("grid", $"默认那一份：style={options.Style} 宽={options.WidthPercent}% 高={options.HeightPercent}% " +
                    $"一带={options.HeaderPercent}% 边距=({options.MarginXPercent}%,{options.MarginYPercent}%) " +
                    $"字={options.TextPercent}% corner={options.Corner} 行数={rows}");
        Log("grid", $"工作区 {work}，格子该摆的矩形 {want}（工作区 × 配置里那几条比例的算术）");

        var defview = DesktopLayer.Find();
        var iconView = Win32.FindWindowEx(defview, IntPtr.Zero, "SysListView32", "FolderView");
        Win32.GetWindowRect(defview, out var dvRect);
        Win32.GetWindowRect(iconView, out var ivRect);
        Log("grid", $"爹=0x{defview:X}/{Win32.ClassName(defview)} {dvRect}；" +
                    $"图标层=0x{iconView:X}/{Win32.ClassName(iconView)} {ivRect} " +
                    $"可见={Win32.IsWindowVisible(iconView)}");

        // 先按顶层窗摆到那块矩形上，再压到桌面正上方 —— 反过来的话样式位还没补上，
        // 那一帧的位置读出来是旧的。topmost 那位用 SetWindowPos 摘，不碰 Window.AlwaysOnTop：
        // 赋值会把窗藏掉（docs/research/desktop-layer-and-window-measurements.md AlwaysOnTop 那一条）。
        Win32.SetWindowPos(_hwnd, Win32.HwndNotTopMost, want.X, want.Y, want.Width, want.Height,
            Win32.SwpFrameChanged | Win32.SwpNoZorder | Win32.SwpNoActivate);
        await Frame(0.3f);
        var before = DesktopLayer.Capture(_hwnd);
        Log("grid", $"压下去之前 {before}");
        Log("grid", DesktopLayer.Park(before with { Screen = want.Screen }));
        await Frame(0.4f);

        // 摆着 → 读一遍 → 排到桌面之下 → 再读一遍 → 排回来。这一段跑两遍：一遍在桌面
        // 还是乱着的时候（z 序和命中归谁，不依赖桌面），一遍在桌面扫干净之后
        // （屏幕上谁压着谁，只有那一发看得见 —— 那条教训：布局的实测结果在截图里，不在日志里）。
        async Task ReadBothRanks(string when)
        {
            TopRank($"{when} 刚摆着", _hwnd, DesktopLayer.DesktopTop());
            SampleGridPoints($"{when} 刚摆着", _hwnd, want);
            IconPoints($"{when} 刚摆着", want);
            ShotOf(want.Screen, "grid-parked");

            // 对照：排到 <c>HWND_BOTTOM</c>，也就是**桌面底下**。这一旗不改样式也不改位置，
            // 只改 z 序，所以两发之间唯一的变量就是「排在第几」。
            // 这一发是 2026-10-12 整套新做法压着的实测结果：排到桌面之下，日历整个看不见，
            // 所以正确答案是「压在桌面正上方」，不是「排到最底下」。
            Win32.SetWindowPos(_hwnd, Win32.HwndBottom, 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
            await Frame(0.4f);
            TopRank($"{when} 排到桌面之下之后", _hwnd, DesktopLayer.DesktopTop());
            SampleGridPoints($"{when} 排到桌面之下之后", _hwnd, want);
            ShotOf(want.Screen, "grid-below-desktop");

            // 排回桌面正上方：产品要的形状是「我们压在桌面之上、所有普通窗之下」，
            // 抬起那几发要在这一档上量，别让对照的形状跟着进下一段。
            Win32.SetWindowPos(_hwnd, DesktopLayer.DesktopTop(), 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
            await Frame(0.3f);
            TopRank($"{when} 排回桌面正上方", _hwnd, DesktopLayer.DesktopTop());
        }

        await ReadBothRanks("桌面上还压着别人的窗：");

        // 桌面那一半要单独一发旗才动：这一发最小化的是别人家的窗。带它的原因是图标那一层
        // 只能露出来才读得清 —— Chrome 盖住整块矩形时，四点全读「归 app 窗」。
        if (!_wantsSweep)
        {
            Log("grid", "没带 --sweep，桌面那几发不动 —— 上面那两帧里盖在上面的是别人的窗，不算图标的实测结果");
        }
        else
        {
            var deskBefore = SweepForIcons(want.Screen);
            try { await ReadBothRanks("桌面扫干净之后："); }
            finally { RestoreIcons(deskBefore); }
        }

        // ---- 抬起那一发：桌面大小的一扇窗抬到顶层，它盖住什么 ----------
        Log("grid", "抬起那一发（不点鼠标，直接按产品那条路抬）");
        var lifted = DesktopLayer.Lift(before);
        var front = DesktopLayer.GrabForTyping(_hwnd);
        await Frame(0.4f);
        Log("grid", $"抬起 {lifted}；{front}");
        Log("grid", $"抬起之后 {Win32.ImeReport(_hwnd)}（这一线程读的，算一条对照）");
        Win32.GetWindowRect(_hwnd, out var nowRect);
        Log("grid", $"抬起之后盖住的窗：{DesktopLayer.OverRect(nowRect)}");
        foreach (var (label, pt) in new[]
        {
            ("我们矩形中心", new Win32.Point { X = nowRect.Left + nowRect.Width / 2, Y = nowRect.Top + nowRect.Height / 2 }),
            ("工作区里我们没占的那一角", new Win32.Point { X = work.Left + 40, Y = work.Bottom - 40 }),
            ("任务栏那一带", new Win32.Point { X = (work.Left + work.Right) / 2, Y = work.Bottom + 20 }),
        })
        {
            var (owner, measurement) = DesktopLayer.OwnerOf(_hwnd, pt);
            Log("grid", $"抬起 {label}：{measurement}");
        }
        ShotOf(nowRect, "grid-lifted");

        // 放回去：必须回到抬起之前那块像素，这是契约「无感」第 1 条那句「放回原来那个像素」
        // 在桌面大小这一发上的实测结果。
        Log("grid", DesktopLayer.Park(DesktopLayer.Capture(_hwnd) with { Screen = want.Screen }));
        await Frame(0.3f);
        Win32.GetWindowRect(_hwnd, out var back);
        Log("grid", $"放回 {back} 和原来一样={back.Left == want.X && back.Top == want.Y && back.Width == want.Width}");

        // ---- 嵌进桌面层那一发（2026-10-12）：这条路值不值得换掉 Park 那一族 ----------
        //
        // 为什么单独量：挂进桌面层就成了子窗，于是「在所有应用窗之下」「不被 Win+D 扫走」
        // 「图标在它上面」三件一次解决，不需要 owner、也不需要每秒顶 z 序。
        // 那一族（Wallpaper Engine / Lively）走的就是这条路。
        // 旧笔记本上写着「嵌进去之后不透明」——**那一发已被下面这发取代**，
        // 它的像素基准是 SW_HIDE（藏起来之后 Explorer 不补画，读出来和「没画」同一个数）。
        //
        // 所以判据是**同一块地方嵌着 vs 抬到顶层**，两边都用引擎自己的进程内抓屏
        // （外部 CopyFromScreen 在 125% 上差 1.25 倍，还拍到别人的窗）。
        Log("grid", "嵌进桌面层那一发：先把顶层那一档的像素记下来当基准");
        var topShot = DisplayServer.ScreenGetImageRect(new Rect2I(want.X, want.Y, want.Width, want.Height));
        var topSave = topShot == null || topShot.IsEmpty() ? null : topShot.GetData();
        // 判据：**同一帧里**，日历矩形里的点 vs 矩形外面同高度那一片。
        // 别的窗压不压就自动对掉了 —— 而「整块两档互相比」不行：第一版读出 0 个像素差，
        // 看着像「完全一样 = 画出来了」，实际是两档都被别家的窗盖住，那一发不算数。
        void ReportSameFrame(string when, byte[]? fb, int width)
        {
            if (fb == null) { Log("grid", $"{when}：进程内抓屏答空，判不了"); return; }
            var line = new System.Text.StringBuilder($"{when}：");
            foreach (var (label, fx, fy) in new[]
                     {
                         ("抬头", 0.85, 0.03), ("中间", 0.85, 0.5), ("右下角", 0.85, 0.95), ("左上那格", 0.85, 0.12),
                     })
            {
                var x = (int)(want.Width * fx);
                var y = (int)(want.Height * fy);
                var outX = x - (int)(want.Width * 0.15);
                var i = (y * width + x) * 4;
                var j = (y * width + outX) * 4;
                if (i + 3 >= fb.Length || j + 3 >= fb.Length) { line.Append($" {label}=越界"); continue; }
                var d = Math.Abs(fb[i] - fb[j]) + Math.Abs(fb[i + 1] - fb[j + 1]) + Math.Abs(fb[i + 2] - fb[j + 2]);
                line.Append($" {label}={d}");
            }
            Log("grid", line + "（矩形里 vs 矩形外，0 = 那一块和壁纸一样 = 没画出来）");
        }
        ReportSameFrame("顶层那一档", topSave, (int)topShot!.GetWidth());
        ShotOf(want.Screen, "embed-对照-顶层");

        // 判断「那一块画没画出来」的**唯一可靠办法**：在同一帧里，拿日历矩形里若干点
        // 和**同高度、日历矩形外面**的那一片比。别的窗压不压就自动对掉了 ——
        // 而「整块两档互相比」不行：第一版读出 0 个像素差，看着像「完全一样 = 画出来了」，
        // 实际是**两档都被别家的窗盖住**（记事本 + VS Code），那一发不算数。
        // 两把尺子在这件事上都能给出「像那么回事」的数，所以判据必须自己拆开。
        var toEmbed = DesktopLayer.Capture(_hwnd);
        Log("grid", DesktopLayer.EmbedInto(toEmbed, defview, iconView));
        await Frame(0.6f);
        Log("grid", $"嵌进去之后 {Win32.ImeReport(_hwnd)}（子窗拿不到键盘焦点，这一条是代价）");
        TopRank("嵌进去之后", _hwnd, DesktopLayer.DesktopTop());
        IconPoints("嵌进去之后", want);
        foreach (var (label, fx, fy) in new[]
                 {
                     ("抬头", 0.5, 0.03), ("中间", 0.5, 0.5), ("右下角", 0.97, 0.97), ("左上那格", 0.07, 0.12),
                 })
        {
            var pt = new Win32.Point
            {
                X = want.X + (int)(want.Width * fx),
                Y = want.Y + (int)(want.Height * fy),
            };
            var (_, measurement) = DesktopLayer.OwnerOf(_hwnd, pt);
            Log("grid", $"嵌进去之后 {label}：{measurement}");
        }
        var embedShot = DisplayServer.ScreenGetImageRect(new Rect2I(want.X, want.Y, want.Width, want.Height));
        ShotOf(want.Screen, "embed-嵌进DefView-图标层之下");
        if (embedShot != null && !embedShot.IsEmpty())
            ReportSameFrame("嵌进去那一档", embedShot.GetData(), (int)embedShot.GetWidth());
        else
            Log("grid", "嵌进去那一档：进程内抓屏答空");

        // 拆回顶层：后面那些发都建立在顶层那一档上，不许把子窗的形状带进去。
        Log("grid", DesktopLayer.Lift(toEmbed));
        await Frame(0.3f);

        // 放回测量程序自己的形状：抬起（把扩展样式摘回去，鼠标才收得到），
        // 再按屏幕坐标摆回 480x320 那个矩形。
        DesktopLayer.Lift(before);
        Win32.SetWindowPos(_hwnd, Win32.HwndTopMost, 240, 240, ProbeWidth, ProbeHeight,
            Win32.SwpFrameChanged | Win32.SwpNoZorder);
        _win.MousePassthrough = false;
        await Frame(0.2f);
        Win32.GetWindowRect(_hwnd, out var home);
        Log("grid", $"测量程序窗放回 {home}（该是 480x320 @(240,240)），爹=0x{Win32.GetParent(_hwnd):X}");
    }

    private System.Threading.Timer? _sweepDeadline;
    private int _sweepRestored;

    /// <summary>扫桌面那一发，照 <c>App.cs</c> 的 <c>Sweep</c> / <c>RestoreDesk</c> 那一份抄：
    /// 名单先印出来再动手、另挂一条不在帧上的 15 秒兜底、阻塞等 shell 而不是等帧
    /// （这会儿测量程序自己的窗也可能被最小化，拿自己的帧去等别人的动画，等的东西本身可疑）。</summary>
    private List<(nint Hwnd, string Name, bool Iconic)> SweepForIcons(Win32.Rect rect)
    {
        Log("grid", $"压在这块矩形上、会被扫的窗（那一发扫的是整张桌面，不止这些）：{DesktopLayer.OverRect(rect)}");
        var before = DesktopLayer.VisibleTopLevelWindows();
        if (!Win32.ShowDesktop(false))
        {
            Log("grid", "扫这一发没成（Shell.Application 那个动词没叫动），桌面没动过 —— 下面那几帧不算图标的实测结果");
            return before;
        }
        _sweepDeadline = new System.Threading.Timer(_ =>
        {
            if (System.Threading.Interlocked.Exchange(ref _sweepRestored, 1) == 0) Win32.ShowDesktop(true);
        }, null, 15_000, System.Threading.Timeout.Infinite);
        System.Threading.Thread.Sleep(900);
        return before;
    }

    private void RestoreIcons(List<(nint Hwnd, string Name, bool Iconic)> before)
    {
        _sweepDeadline?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        if (System.Threading.Interlocked.Exchange(ref _sweepRestored, 1) == 0)
        {
            if (!Win32.ShowDesktop(true))
                Log("grid", "还原这一发没成 —— 桌上那些窗还最小化着，按一次 Win+D 把它们叫回来");
        }
        else Log("grid", "还原已经被那条 15 秒兜底叫过 —— 中间主循停过，那几帧的实测结果作废");
        System.Threading.Thread.Sleep(1_500);
        Log("grid", $"还原对账：{DesktopLayer.Reconcile(before)}");
    }

    /// <summary>顶层那一叠里，我们和桌面那一扇各排第几（最上面的先），并说清谁压着谁。
    ///
    /// <b>2026-10-12 换掉了原来那一发</b>。原来问的是「<c>DefView</c> 的子窗按 z 序排出来，
    /// 我们和图标层谁在前」—— 那一问成立的前提是我们是 <c>DefView</c> 的子窗。
    /// 现在我们是顶层窗，和图标层不在同一个 z 序里（图标层是 <c>DefView</c> 的子窗，
    /// <c>DefView</c> 是 <c>Progman</c> 的子窗），所以只剩一问：
    /// <b>我们在桌面那一扇的上面还是下面</b>。答案只有两个，而两个之间的差别是
    /// 「日历看得见 / 整个看不见」—— 所以这一发是整套新做法的支点。
    ///
    /// <b>兄弟 z 序那一发（<c>SiblingOrder</c>）连同它的判据一起退休了</b>：
    /// 它数的是同一个父子堆，而我们不在那个堆里了。</summary>
    private static void TopRank(string when, nint own, nint desktopTop)
    {
        var under = Win32.GetWindow(own, Win32.GwHwndPrev);
        var over = Win32.GetWindow(own, Win32.GwHwndNext);
        Log("grid", $"{when} 顶层 z 序：紧挨在上面的是 0x{over:X}/{Win32.ClassName(over)}，" +
                    $"紧挨在下面的是 0x{under:X}/{Win32.ClassName(under)}");
        Log("grid", $"  桌面那一扇是 0x{desktopTop:X}/{Win32.ClassName(desktopTop)} → " +
                    (under == desktopTop
                        ? "我们正压在桌面之上（这一档才对：图标透得过来、别的窗盖得住我们）"
                        : under == IntPtr.Zero
                            ? "我们在最底下、桌面在我们之上（这一档下日历整个看不见）"
                            : "我们和桌面之间还夹着别的窗 —— 位置不对"));
        Log("grid", $"  {DesktopLayer.Where(own)}");
    }

    /// <summary>那块矩形里，抽几格问图标层「这一点上有没有图标」。
    ///
    /// <b>这一发是 2026-10-12 新加的</b>，问的是鼠标桥第三问（<see cref="DesktopLayer.OnIcon"/>）
    /// 在真的图标上答不答得对：<b>压着图标的那几格必须答「有图标」</b>（那一发不喂，
    /// 拖放归 shell），<b>空着的那几格必须答「没有」</b>（那一发照常喂，格子才点得动）。
    /// 判据是图标层自己答的序号（<c>LVM_HITTEST</c>），不是我们猜的位置。
    ///
    /// <b>这一发不点鼠标、不动图标</b>，只发一条只回 <c>int</c> 的消息，
    /// 和 <c>Win32.DesktopIconSpacing</c> 同一类，不走跨进程共享内存。</summary>
    private static void IconPoints(string when, Calendar.Layout want)
    {
        var iconView = DesktopLayer.IconLayer();
        if (iconView == IntPtr.Zero) { Log("grid", $"{when} 图标层找不到，这一发没做"); return; }
        Log("grid", $"{when} 图标层=0x{iconView:X}/{Win32.ClassName(iconView)}，"+
                    $"图标的间距 {Win32.DesktopIconSpacing(iconView)}");
        var hits = 0;
        foreach (var (label, fx, fy) in new[]
        {
            ("抬头", 0.5, 0.03), ("中间", 0.5, 0.5), ("右下角", 0.97, 0.97), ("左上那格", 0.07, 0.12),
            ("右上那格", 0.93, 0.12), ("正中偏上", 0.5, 0.2),
        })
        {
            var pt = new Win32.Point
            {
                X = want.X + (int)(want.Width * fx),
                Y = want.Y + (int)(want.Height * fy),
            };
            var index = Win32.IconAt(iconView, pt);
            if (index >= 0) hits++;
            Log("grid", $"{when} {label} ({pt.X},{pt.Y})：图标层答 " +
                        (index >= 0 ? $"第 {index} 个图标 → 归 shell，桥不喂"
                                    : $"没有图标（LVM_HITTEST 回 {index}）→ 桥照常喂"));
        }
        // 这一格上有没有图标，是「图标透过来 + 点得穿」那一条的实测结果。
        // 读数不是「必须 0」也不是「必须大于 0」，随桌面摆着什么而变；
        // 判据在 Animal 上是「答得出来」（没有图标层才算失败）。
        Log("grid", $"{when} 这几格里压着 {hits} 个图标");
    }

    // ---- 图标列（工单 26）--------------------------------------------------------
    //
    // 要问的是「图标在哪」。**先问的是哪一条问法答得动** ——
    // 产品原来那一问（Win32.IconAt，wParam 写死成 0）在 2026-10-12 和 10-14 两发量到的都是
    // 「处处答 0」（docs/research/desktop-layer-and-window-measurements.md
    // 「图标位置读法选型（产品侧）」）。照那一问扫一遍，答的是「每一列都有图标」，
    // 也就是日历一格都不画。答得动的那一条是 UI Automation（所有者 2026-10-14 定夺走它）。
    //
    // 取样点每隔 IconScanStride 像素一个。**那是量具的疏密，不是产品写死的位置** ——
    // 产品答的是图标层读得出来的位置（`DesktopIcons.OnIcon`）。

    /// 量具的取样疏密：每隔多少像素问一个点。只管这一发的读数，产品那一发自己定疏密。
    private const int IconScanStride = 50;

    /// 帧里那一问问多少遍。问一遍量不出来（微秒级），问几万遍除一下才是那个数。
    private const int IconFrameAsks = 20_000;

    /// 图标层的读法选型（工单 26 的第一问）。
    ///
    /// 把 <c>LVHT_*</c> 那几档各问一遍**整块图标层**，看每档的返回值分布：
    /// 一档如果处处答同一个数，它就没有判别力（分辨不出有没有图标）；
    /// 答得动的那一档，答非零的那些点应当落在 <c>DebugProbe icons</c>
    /// （UI Automation 那一把尺子）读出来的图标矩形上 —— 两把尺子对着看。
    ///
    /// 这一发**只读**：不发鼠标、不动图标、不点别人的窗，别的族那几条规矩都一样。
    private static async Task IconColumns()
    {
        var iconView = DesktopLayer.IconLayer();
        if (iconView == IntPtr.Zero)
        {
            Log("icons", "图标层读不到（FindWindowEx 答 0）—— 产品那一档走下限宽度，这一发没得量");
            return;
        }
        Win32.GetWindowRect(iconView, out var area);
        Win32.GetWindowThreadProcessId(iconView, out var pid);
        Log("icons", $"图标层=0x{iconView:X} pid={pid} 类名={Win32.ClassName(iconView)} 矩形={area}");
        Log("icons", $"不带指针的那几问：LVM_GETITEMCOUNT={Win32.SendMessage(iconView, Win32.LvmGetItemCount, 0, 0)}、" +
                    $"LVM_GETITEMSPACING={Win32.DesktopIconSpacing(iconView)}、" +
                    $"LVM_GETICONSPACING 原样 {Win32.SendMessage(iconView, Win32.LvmGetIconSpacing, 0, 0)}");

        // 每一档：返回值 → 问了几次、点长什么样（每种返回值留三个点当样本）。
        foreach (var (name, flags) in new[]
        {
            ("wParam=0（产品 IconAt 那一问）", 0),
            ($"LvhtOnIcon({Win32.LvhtOnIcon})", Win32.LvhtOnIcon),
            ($"LvhtOnLabel({Win32.LvhtOnLabel})", Win32.LvhtOnLabel),
            ($"LvhtOnItem({Win32.LvhtOnItem})", Win32.LvhtOnItem),
            ($"LvhtOnItemIcon({Win32.LvhtOnItemIcon})", Win32.LvhtOnItemIcon),
            ($"LvhtOnItemLabel({Win32.LvhtOnItemLabel})", Win32.LvhtOnItemLabel),
            ($"LvhtOnItemState({Win32.LvhtOnItemState})", Win32.LvhtOnItemState),
        })
        {
            var counts = new Dictionary<int, int>();
            var samples = new Dictionary<int, List<Win32.Point>>();
            var asked = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var y = area.Top; y < area.Bottom; y += IconScanStride)
            {
                for (var x = area.Left; x < area.Right; x += IconScanStride)
                {
                    var raw = Win32.IconHitRaw(iconView, new Win32.Point { X = x, Y = y }, flags);
                    counts[raw] = counts.GetValueOrDefault(raw) + 1;
                    if (!samples.TryGetValue(raw, out var seen))
                    {
                        seen = [];
                        samples[raw] = seen;
                    }
                    if (seen.Count < 3) seen.Add(new Win32.Point { X = x, Y = y });
                    asked++;
                }
            }
            watch.Stop();
            foreach (var raw in counts.Keys.OrderBy(k => k))
            {
                var points = string.Join("、", samples[raw].Select(p => $"({p.X},{p.Y})"));
                Log("icons", $"{name}：返回值 {raw} 问了 {counts[raw]} 次，样本点 {points}");
            }
            Log("icons", $"{name}：共问 {asked} 点，用时 {watch.ElapsedMilliseconds} 毫秒，" +
                        $"返回值 {counts.Count} 种");
        }

        // 答得动的那一族：UI Automation（工单 26 所有者定夺走这一条）。
        // 同一段代码、同一台机器上和量具那一侧 `DebugProbe icons` 对着看。
        DesktopIcons.Answer fresh = default;
        for (var round = 1; round <= 3; round++)
        {
            fresh = DesktopIcons.ReadForMeasurement();
            Log("icons", $"UI Automation（产品侧，第 {round} 次）：{fresh.Report()}");
            foreach (var icon in fresh.Icons.Take(8)) Log("icons", $"    「{icon.Name}」 {icon.Rect}");
        }

        // 帧里那一问花多久。鼠标桥第三问是**每帧**走的（`MouseBridge.Drain` 由
        // `Calendar._Process` 每帧调），所以量的是「拿上一次快照比矩形」那一下，
        // 不是上面那个「读一次」—— 读在后台线程上，帧里根本不走它。
        var probe = fresh.Icons.Count > 0
            ? new Win32.Point
            {
                X = (fresh.Icons[0].Rect.Left + fresh.Icons[0].Rect.Right) / 2,
                Y = (fresh.Icons[0].Rect.Top + fresh.Icons[0].Rect.Bottom) / 2,
            }
            : new Win32.Point { X = area.Left + 1, Y = area.Top + 1 };
        var frameCost = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < IconFrameAsks; i++) DesktopIcons.OnIcon(probe);
        frameCost.Stop();
        Log("icons", $"帧里那一问（DesktopIcons.OnIcon，{fresh.Icons.Count} 颗图标、点=({probe.X},{probe.Y})）：" +
                    $"问 {IconFrameAsks} 次用时 {frameCost.Elapsed.TotalMilliseconds:F2} 毫秒，" +
                    $"单次 {frameCost.Elapsed.TotalMilliseconds * 1000 / IconFrameAsks:F3} 微秒");

        Log("icons", $"这一块矩形 {area.Width}x{area.Height}；" +
                    "读法结论见上面那几档的返回值分布（消息族没有判别力）和 UI Automation 那一段");

        await Task.CompletedTask;
    }

    /// 那块矩形里取四点问「归谁」：抬头一带、中间、右下角、和左上那一格。
    private static void SampleGridPoints(string when, nint own, Calendar.Layout want)
    {
        foreach (var (label, fx, fy) in new[]
        {
            ("抬头", 0.5, 0.03), ("中间", 0.5, 0.5), ("右下角", 0.97, 0.97), ("左上那格", 0.07, 0.12),
        })
        {
            var pt = new Win32.Point
            {
                X = want.X + (int)(want.Width * fx),
                Y = want.Y + (int)(want.Height * fy),
            };
            var (_, measurement) = DesktopLayer.OwnerOf(own, pt);
            Log("grid", $"{when} {label}：{measurement}");
        }
    }

    /// 进程内抓屏（docs/research/desktop-layer-and-window-measurements.md 那一条量出来的路：
    /// <c>ScreenGetImageRect</c> 和引擎自己的坐标口径一致，外部 CopyFromScreen 在 125% 上会差 1.25 倍）。
    private static void ShotOf(Win32.Rect rect, string name)
    {
        var image = DisplayServer.ScreenGetImageRect(new Rect2I(rect.Left, rect.Top, rect.Width, rect.Height));
        if (image == null || image.IsEmpty())
        {
            Log("grid", $"截屏读不到东西（{name}）");
            return;
        }
        var path = Path.Combine(Tmp.Root, $"keeper-{name}.png");
        var err = image.SavePng(path);
        Log("grid", $"{name} 那一帧：{path}（{image.GetWidth()}x{image.GetHeight()}，错误码={err}）");
    }

    /// 主屏工作区（任务栏让出来那一条，和 <see cref="Calendar"/> 用的是同一个口径）。
    private static Win32.Rect WorkArea()
    {
        var info = new Win32.MonitorInfo { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MonitorInfo>() };
        var monitor = Win32.MonitorFromWindow(Win32.GetDesktopWindow(), 0);
        if (!Win32.GetMonitorInfo(monitor, ref info)) return new Win32.Rect { Right = 1920, Bottom = 1080 };
        return info.Work;
    }

    // ---- step 10: the fade at real alpha ------------------------------------
    //
    // Every step above was measured with an opaque block, which is also why the one
    // question a hand still owes could not be asked: an opaque square hides the icons
    // it is clicking through. This says what the compositor actually puts on screen for
    // a given alpha, and which of the two blending models it follows — the answer
    // decides whether a fade can be authored as a straight alpha ramp or has to be
    // corrected for gamma first.

    private async Task RealAlpha()
    {
        Log("alpha",
            $"IsWindowTransparencyAvailable={DisplayServer.IsWindowTransparencyAvailable()} " +
            $"feature_WindowTransparency={DisplayServer.HasFeature(DisplayServer.Feature.WindowTransparency)} " +
            $"project_setting={ProjectSettings.GetSetting("display/window/per_pixel_transparency/allowed")}");

        // Whether hiding works, measured again on the same window after it has been
        // parented into the wallpaper and lifted back out — see the pre-parent run.
        await HideRoutes("post_parent");

        _win.Transparent = true;
        if (_cueLabel != null) _cueLabel.Visible = false;
        // Pure green on purpose: red and blue in a sample can then only have come from
        // what is behind us, and the two blending models disagree hardest on this colour.
        if (_fill != null) _fill.Color = PureGreen;
        SetFillAlpha(1f);
        await Frame(0.5f);
        ReportWindow("alpha", "Window.Transparent = true, fill opaque");

        Win32.GetWindowRect(_hwnd, out var rect);
        var pt = new Win32.Point
        {
            X = rect.Left + rect.Width * 40 / 100,
            Y = rect.Top + rect.Height * 40 / 100,
        };
        Log("alpha",
            $"sample_at=({pt.X},{pt.Y}) underneath={Win32.ClassName(Win32.WindowFromPoint(pt))} " +
            $"root='{Win32.ClassName(Win32.GetAncestorRoot(Win32.WindowFromPoint(pt)))}'");

        // The control: the same pixel with our window off it, taken by moving the window
        // because the engine's Hide() was already shown not to uncover anything (HideRoutes).
        // Sampled again at the end, because anything moving behind us mid-run would
        // otherwise be read as engine blending.
        var baseBefore = await BaseByMovingAway(pt);

        // 从这一行往下，「读不到」不再是一个颜色而是 Sample 上的一档。所以下面每一个
        // `Err(...) <= 24` 都换成了 Frames.Gap，而 Gap 在有一边没读到时还 int.MaxValue ——
        // 三个判据（_transparencyWorks / _blendSurvivesShowHide / drift）全部失败关闭。
        // 原来那一份把 CLR_INVALID 折成 (-1,-1,-1)，跟基准一比是个大差，看着像「不透」，
        // 其实是「没量到」；两件事在日志里分不开。
        var measurements = new List<(float A, Frames.Sample Got)>();
        foreach (var a in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            SetFillAlpha(a);
            await Frame(0.25f);
            var got = Frames.Shot(pt);
            measurements.Add((a, got));

            var naive = Frames.Blend(baseBefore.Pixel, PureGreenPixel, a, Frames.Model.Srgb8);
            var linear = Frames.Blend(baseBefore.Pixel, PureGreenPixel, a, Frames.Model.LinearLight);
            Log("alpha",
                $"a={a:F2} base={baseBefore} got={got} " +
                $"srgb_model={naive} err={Frames.Gap(got, naive)} " +
                $"linear_model={linear} err={Frames.Gap(got, linear)}");
        }
        var baseAfter = await BaseByMovingAway(pt);
        var drift = Frames.Gap(baseBefore, baseAfter);

        // The hole has to be a real hole: alpha 0 must land back on the desktop, and
        // alpha 1 must land on the fill colour. Anything else and the window is being
        // keyed or chroma'd rather than blended.
        var clear = measurements[0].Got;
        var solid = measurements[^1].Got;
        _transparencyWorks = drift <= 24
            && Frames.Gap(clear, baseBefore) <= 24
            && solid.Read && solid.Pixel.G >= 200 && solid.Pixel.R <= 40 && solid.Pixel.B <= 40;

        var srgbTotal = 0;
        var linearTotal = 0;
        foreach (var (a, got) in measurements)
        {
            srgbTotal += Frames.Gap(got, Frames.Blend(baseBefore.Pixel, PureGreenPixel, a, Frames.Model.Srgb8));
            linearTotal += Frames.Gap(got, Frames.Blend(baseBefore.Pixel, PureGreenPixel, a, Frames.Model.LinearLight));
        }
        _blendModel = linearTotal < srgbTotal ? "linear (gamma-correct)" : "sRGB (naive 8-bit)";
        Log("alpha",
            $"base drift over the run={drift} | model error sums: srgb={srgbTotal} linear={linearTotal} " +
            $"→ {_blendModel}");

        // A real hide/show cycle is the fader's whole life — parked hidden, raised for a
        // fade, put back down — so whether the blend survives one is not a detail. The raw
        // route, because that is the one shown above to actually hide the window.
        SetFillAlpha(0.5f);
        Win32.ShowWindow(_hwnd, Win32.SwHide);
        await Frame(0.6f);
        var whileHidden = Frames.Shot(pt);
        Win32.ShowWindow(_hwnd, Win32.SwShow);
        SetFillAlpha(0.5f);
        await Frame(0.5f);
        var afterShowHide = Frames.Shot(pt);
        var expected = PredictForModel(baseBefore.Pixel, 0.5f);
        _blendSurvivesShowHide = Frames.Gap(whileHidden, baseBefore) <= 24
            && Frames.Gap(afterShowHide, expected) <= 24;
        Log("alpha",
            $"raw hide at a=0.5: hidden_px={whileHidden} is_the_desktop={Frames.Gap(whileHidden, baseBefore) <= 24} " +
            $"| after show: got={afterShowHide} expected={expected} " +
            $"survives={_blendSurvivesShowHide}");

        // The ramp itself, at the granularity the fade will run at: 3 s in, one sample
        // every ~120 ms, alpha stepping the way _process would drive it. Distance from
        // the desktop, not one channel — the fill is pure green, so over a light base its
        // own green channel never moves and would read as a dead flat line.
        Log("ramp", "3 s ramp from clear to maxOpacity 0.75, sampled every ~120 ms");
        var steps = 25;
        var last = -1;
        var worstStep = 0;
        var framesFirst = Engine.GetFramesDrawn();
        for (var i = 0; i <= steps; i++)
        {
            var a = 0.75f * i / steps;
            SetFillAlpha(a);
            await Frame(3f / steps);
            var got = Frames.Shot(pt);
            var away = Frames.Gap(got, baseBefore);
            if (last >= 0) worstStep = Math.Max(worstStep, Math.Abs(away - last));
            last = away;
            Log("ramp", $"t={3f * i / steps:F2}s a={a:F3} px={got} " +
                       $"away_from_desktop={Frames.GapText(got, baseBefore)}");
        }
        Log("ramp",
            $"frames used={Engine.GetFramesDrawn() - framesFirst} worst_step={worstStep} of a " +
            $"{Frames.Gap(baseBefore, PredictForModel(baseBefore.Pixel, 0.75f))}-step travel " +
            $"(a visible jump is a step far larger than its neighbours)");

        SetFillAlpha(1f);
        if (_cueLabel != null) _cueLabel.Visible = true;
    }

    private async Task HideRoutes(string when)
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        var pt = new Win32.Point
        {
            X = rect.Left + rect.Width * 40 / 100,
            Y = rect.Top + rect.Height * 40 / 100,
        };

        foreach (var transparent in new[] { false, true })
        {
            _win.Transparent = transparent;
            SetFillAlpha(1f);
            await Frame(0.6f);
            Log("hide", $"{when} transparent={transparent} while shown: px={Frames.Shot(pt)}");

            _win.Hide();
            await Frame(0.8f);
            Log("hide",
                $"{when} transparent={transparent} engine Hide(): px={Frames.Shot(pt)} " +
                $"visible_flag={Win32.IsWindowVisible(_hwnd)} " +
                $"still_owns_the_point={Win32.GetAncestorRoot(Win32.WindowFromPoint(pt)) == _hwnd}");
            _win.Show();
            await Frame(0.6f);

            Win32.ShowWindow(_hwnd, Win32.SwHide);
            await Frame(0.8f);
            Log("hide",
                $"{when} transparent={transparent} raw ShowWindow(SW_HIDE): px={Frames.Shot(pt)} " +
                $"visible_flag={Win32.IsWindowVisible(_hwnd)} " +
                $"still_owns_the_point={Win32.GetAncestorRoot(Win32.WindowFromPoint(pt)) == _hwnd}");
            Win32.ShowWindow(_hwnd, Win32.SwShow);
            _win.Show();
            await Frame(0.6f);
        }
    }

    private void SetFillAlpha(float a)
    {
        // Modulate rather than the colour's own alpha: this is the property the fade
        // will be driven by per frame.
        if (_fill != null) _fill.Modulate = new Color(1f, 1f, 1f, a);
    }

    private static readonly Color PureGreen = new(0f, 1f, 0f);

    /// The control measurement, taken by moving the window off the pixel rather than hiding it
    /// — HideRoutes showed the engine's Hide() leaves the window on screen, so only a move
    /// proves what the desktop looks like underneath.
    private async Task<Frames.Sample> BaseByMovingAway(Win32.Point pt)
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        _win.Position = new Vector2I(rect.Left + 1000, rect.Top);
        await Frame(0.6f);
        var uncovered = Win32.GetAncestorRoot(Win32.WindowFromPoint(pt)) != _hwnd;
        var got = Frames.Shot(pt);
        _win.Position = new Vector2I(rect.Left, rect.Top);
        await Frame(0.6f);
        Log("alpha", $"move_away={got} point_uncovered={uncovered}");
        return got;
    }

    /// <summary>这一发的填充色是纯绿，两个模型都按它算期望值。原来那两份 Predict* 把
    /// <c>(0,255,0)</c> 写死在通道里（<c>Blend(R,0,a)</c> / <c>Blend(G,255,a)</c> / <c>Blend(B,0,a)</c>），
    /// 搬家之后它是递进 <see cref="Frames.Blend"/> 的一个参数 —— 混色模型不再知道测量程序选了什么颜色。</summary>
    private static readonly Win32.Pixel PureGreenPixel = new(0, 255, 0);

    private Win32.Pixel PredictForModel(Win32.Pixel basePixel, float a)
        => Frames.Blend(basePixel, PureGreenPixel, a,
            _blendModel.StartsWith("linear") ? Frames.Model.LinearLight : Frames.Model.Srgb8);

    // SampleScreen / Err / Fmt / Blend / ToLinear / ToSrgb8 和两份 Predict*Blend 都搬到 Frames
    // 去了（2026-09-29）。这一份原来把 CLR_INVALID 折成 (-1,-1,-1) 并记一行「这一发没做」——
    // 跟 Win32 那一份折成 (0,0,0) **相反**，同一件事两份实施两个答法。现在是一档返回值，
    // 而 Fmt 那份 "(R,G,B)" 的印法由 Frames.Sample.ToString() 原样接了过去，实测结果逐字不变。

    // ---- step 9: the check a hand has to do -------------------------------
    //
    // Run with `-- --hold` and the probe leaves a click-through block sitting over
    // the desktop's icon corner for a while. Everything the plan cares about is
    // then on the record: the low-level hook sees each press, the shell's context
    // menu only exists if the desktop received the right click, and Godot's own
    // input count should stay at zero because the block was never supposed to be
    // clicked at all.

    private async Task HoldForTheHand()
    {
        // The claim being tested is about a desktop icon, so a block parked over a
        // browser window tests nothing, and an opaque block hides the very effect
        // a hand is supposed to see. So: wait until the desktop really is the
        // window under the block, then read the answer off the icon layer itself —
        // its selection count going up is a click that landed on an icon, whether
        // or not anyone can see it.
        // 图标层是 DefView 的子窗，所以这里要的是 DefView 本身，**不是** DesktopLayer.Find()
        // —— 那一条在两条退路上还回来的是 WorkerW 或 Progman，拿它去 FindWindowEx 找不到图标层。
        var defview = DesktopLayer.Look().DefView;
        _iconView = Win32.FindWindowEx(defview, IntPtr.Zero, DesktopLayer.IconViewClass, "FolderView");

        _win.Position = new Vector2I(0, 0);
        _win.Size = new Vector2I(320, 280);
        // The block has to be see-through or the run tests nothing: the claim is about a
        // click reaching a desktop icon the block is standing on.
        _win.Transparent = true;
        _win.MousePassthrough = true;
        Win32.GetWindowRect(_hwnd, out var block);

        _mouseHookProc = HookCallback;
        _mouseHook = Win32.SetWindowsHookEx(Win32.WhMouseLl, _mouseHookProc, IntPtr.Zero, 0);

        Log("hold",
            $"block=({block.Left},{block.Top})-({block.Right},{block.Bottom}) hook={_mouseHook != IntPtr.Zero} " +
            $"icon_view=0x{_iconView:X}");

        // A maximised browser would make the whole run measure nothing, so the
        // countdown only starts once the point being clicked through to is the
        // desktop's own icon layer.
        Log("hold", "等底下变成桌面：按 Win+D 把窗口最小化。绿区落下前不要点。");
        var waited = 0;
        while (waited < 120 && !BlockSitsOnDesktop(block))
        {
            await Frame(1f);
            waited++;
            if (waited % 5 == 1) Log("hold", $"t+{waited}s 底下仍不是桌面：{Underneath(block)}");
        }
        if (waited > 0) Log("hold", $"桌面露出来了，等了 {waited}s");

        Log("hold", $"ready: {Underneath(block)} {FormatIconLayer(ReadIconLayer())}");

        // Three cues in this order, because the third only means something once the
        // first two behave: the menu detector has to prove it can see a menu at all,
        // and the block has to be shown eating a click while passthrough is off.
        // Without those, "nothing happened" cannot be told apart from "nothing was
        // measured" — which is exactly the ambiguity the last two runs ended in.
        await Cue("1_detector_works", false, 16,
            "绿区【外面】的空桌面上右键一下。菜单该弹出——这一条只验探测器，不过后面全不可信。");
        await Cue("2_block_eats_it", false, 16,
            "绿区【里面】右键。菜单不该出现，Godot 该收到这一击。");
        await Cue("3_passthrough", true, 20,
            "同样位置再右键一次。Godot 不该收到，菜单该出现——这才是待证的穿透。");

        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        _win.MousePassthrough = false;
    }

    private async Task Cue(string name, bool passthroughOn, int seconds, string instruction)
    {
        _win.MousePassthrough = passthroughOn;
        ShowCue(passthroughOn, instruction);
        var engineBefore = _engineMouseEvents;
        var menusBefore = _menusSeen;
        var pressedBefore = _pressesTotal;
        Log(name, $"cue: {instruction}");

        for (var second = 1; second <= seconds; second++)
        {
            await Frame(1f);
            if (_menuSnapshotPending) ReportMenuSnapshot();
            Log(name,
                $"t={second}s passthrough={passthroughOn} engine={_engineMouseEvents - engineBefore} " +
                $"menus={_menusSeen - menusBefore} {FormatIconLayer(ReadIconLayer())}");
        }

        _summaries.Add(
            $"{name} passthrough={passthroughOn} pressed={_pressesTotal - pressedBefore} " +
            $"engine_received={_engineMouseEvents - engineBefore} menus_opened={_menusSeen - menusBefore}");
    }

    /// The block is the only thing the hand can see, so the instruction and the state
    /// have to be on it: green means the block will eat the click, red means it is
    /// click-through. A log line the user cannot read mid-run is not an instruction.
    /// The fill is kept at 45 % so the icons under it stay visible — the label takes no
    /// modulate and stays crisp.
    private void ShowCue(bool passthroughOn, string instruction)
    {
        if (_fill != null)
        {
            _fill.Color = passthroughOn ? new Color(0.62f, 0.13f, 0.13f) : new Color(0f, 0.5f, 0.25f);
            _fill.Modulate = new Color(1f, 1f, 1f, 0.45f);
        }
        if (_cueLabel != null) _cueLabel.Text =
            (passthroughOn ? "红 = 穿透中\n" : "绿 = 拦截中\n") + instruction;
    }

    // ---- 钩子能不能吃掉那一档（工单：拿掉点击窗那一族）--------------------------
    //
    // **这一发要答的就一句话**：钩子回调返回一个非零值，那一下到不到底下的程序。
    //
    // **为什么非要量**：ADR-0003 那一段「穿透走引擎旗标」记的就是这一族 —— 上一轮凭印象判过一次，
    // 判错了。所以「返回非零 = 吃掉」这一条同样不许凭印象，要一发读数。
    //
    // **两档对照，各量一次**：
    //   一档回调返回 `1`      —— 待证：底下的程序收不到。
    //   一档回调返回 `CallNextHookEx` —— 一定该通（对照）：底下的程序收得到。
    // 对照那一档是承重的：它不成立时，「底下的程序收不到」分不清是钩子吃掉了还是**根本没发出去**。
    //
    // **底下是谁要问出来，不许假设**：量的是一台机器上一个真实的窗口 —— 底下那个是谁，
    // 读出来印在实测结果里。空桌面那一档（<see cref="BlockSitsOnDesktop"/>）量的是图标层的
    // 选中数，而图标层是 ListView，「选中」和「收到了左键」不是同一件事 —— 那一族已经有
    // `--hold` 那一发在用；**这一发要一个普通的、能数点击的窗**，所以走自己的路。
    private async Task HookSwallowMatrix()
    {
        // **这一发要答的就一句话**：钩子回调返回一个非零值，那一下到不到接收方。
        // 待证的那一头是「到不了」，一定该通的那一头是「到得了」（见下面两档）。
        //
        // **接收方是纯 Win32 那一扇**（`ClickTarget`，正本在那个类型的注释里）：
        // 它子类化自己的 `WndProc` 数 `WM_LBUTTONDOWN`，那一层在窗口消息进 Godot **之前**，
        // 所以既不看焦点、也不看 `unfocusable`。**这一发量的恰恰是「送到了没有」，
        // 量具不能自己要焦点** —— 2026-10-10 那一串实测：那个点命中 `Engine`、
        // `IsWindowVisible=True`、坐标落在控件矩形内，而 Godot 的三层输入计数
        // （`_Input` / `gui_input` / 按钮 `Pressed`）全是 0。
        //
        // **它在测量程序自己这个进程里，不另起一个**：这一发的两端指的是
        // 「钩子那一侧」和「系统路由的落点那一侧」，不是两个进程 —— 钩子挂在全机输入的
        // 同步路径上（`WH_MOUSE_LL`，dwThreadId = 0），它对自己进程里那一扇窗的点击
        // 一样有那一刀。**放行时那一发走进这一扇的 `WndProc`；返回非零时它走不到这里。**
        Log("swallow",
            $"godot={Engine.GetVersionInfo()["string"]} headless={DisplayServer.GetName() == "headless"} " +
            $"屏={DisplayServer.ScreenGetSize()}");

        var target = ClickTarget.Show();
        if (target == IntPtr.Zero)
        {
            Log("swallow", "接收方那扇窗没起来（纯 Win32 那一条）—— 这一发量不成");
            return;
        }
        // **摆到引擎那一扇主窗的正中**（那一块实测命中过 `Engine` —— 有一个已知的赢家）。
        // 位置**问引擎**，不自己数窗：这一发分叉在 `Baseline()` 之前，那一扇还没被改名
        // （按标题认只认自己起的名字），所以 `Win32.OwnWindowNamed` 在这儿答不出来。
        var ours = GetTree().Root.GetWindow();
        ours.MoveToCenter();
        await Frame(0.4f);
        Win32.GetWindowRect(Win32.OwnWindowNamed(Title), out var oursRect);
        ClickTarget.MoveOntoOurs(IntPtr.Zero, oursRect);
        ClickTarget.Raise();
        Log("swallow", ClickTarget.LastReport);
        await Frame(0.4f);
        var box = ClickTarget.Where();
        var centre = new Win32.Point { X = (box.Left + box.Right) / 2, Y = (box.Top + box.Bottom) / 2 };
        var hit = Win32.GetAncestorRoot(Win32.WindowFromPoint(centre));
        Log("swallow",
            $"接收方=0x{target:X} ({box.Left},{box.Top})-({box.Right},{box.Bottom}) " +
            $"按的点=({centre.X},{centre.Y}) 那个点上是谁='{Win32.ClassName(hit)}' " +
            $"那个点是接收方自己={hit == target} 子类化装上了={ClickTarget.Clicks >= 0}");

        // **那个点没落到接收方上就当场停** —— 不然后面两档量的是「别家的窗收到了没有」，
        // 而那一发要的恰恰是接收方收到没有。**这一条就是 2026-10-10 卡住的地方**：
        // 前七版全栽在「窗起来了、可见=True、坐标算对了，而那个点上还是别人」。
        // 所以把那一片的整摞窗打出来，一眼看得见是谁压在上面。
        if (hit != target)
        {
            var pile = new List<string>();
            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowRect(h, out var r);
                if (centre.X < r.Left || centre.X >= r.Right || centre.Y < r.Top || centre.Y >= r.Bottom)
                    return true;
                pile.Add($"0x{h:X} '{Win32.ClassName(h)}' '{Win32.WindowText(h)}' " +
                         $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) 可见={Win32.IsWindowVisible(h)} " +
                         $"扩展={Win32.DescribeExStyle(Win32.GetWindowLong(h, Win32.GwlExStyle))}");
                return true;
            }, IntPtr.Zero);
            Log("swallow",
                $"**那个点上不是接收方（{Win32.ClassName(hit)}）—— 这一发量不成，后面的读数不算数。** " +
                $"盖在那个点上的那一摞窗：\n" + string.Join("\n", pile));
            return;
        }

        try
        {
            // 两档。先「一定该通」那档：它不成立的话后一档的读数没意义 ——
            // 「接收方没收到」在两档里长得一模一样，而这一发的整个结论建在这条区分上。
            var eat = false;            // 回调返回非零 = 吃掉；false = 放行
            var passes = 0;
            _mouseHookProc = (code, wParam, lParam) =>
            {
                if (code >= 0 && (int)wParam == Win32.WmLButtonDown) passes++;
                return eat ? 1 : Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
            };
            _mouseHook = Win32.SetWindowsHookEx(Win32.WhMouseLl, _mouseHookProc, IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                Log("swallow", $"钩子没装上（错误码 {Marshal.GetLastPInvokeError()}）—— 这一发量不成，别读它的读数");
                return;
            }
            Log("swallow", $"钩子装上了 0x{_mouseHook:X}；两档各点三下真的左键（挪 / 按下 / 松开分三次交，段间让帧）");

            Win32.MoveTo(centre);
            await Frame(0.3f);

            // 第一档：放行。**对照，一定该通** —— 接收方该 +3。
            var passBefore = passes;
            var beforePass = ClicksRead();
            await ThreeClicks(centre);
            var afterPass = ClicksRead();
            Log("swallow",
                $"**放行（对照，一定该通）**：钩子返回=CallNextHookEx，点了 3 下，" +
                $"接收方 {beforePass} → {afterPass}，钩子自己看见了 {passes - passBefore} 下");
            if (afterPass == beforePass)
            {
                Log("swallow",
                    "**放行那一档没成立**：钩子放行了、它自己看见了点击，接收方却一个没收到 —— " +
                    "这一发量不成。**第二档的读数不算数**");
                return;
            }

            // 第二档：返回非零。待证：接收方**不再动**，而钩子自己仍看见三下。
            eat = true;
            var eatBefore = ClicksRead();
            var eatPassBefore = passes;
            await ThreeClicks(centre);
            var afterEat = ClicksRead();
            Log("swallow",
                $"**返回非零（待证：吃掉）**：钩子返回=1，点了 3 下，" +
                $"接收方 {eatBefore} → {afterEat}（该不动），钩子自己看见了 {passes - eatPassBefore} 下");

            // **第三条对照**：钩子拆掉之后再点一下。接收方该再动 —— 少了它，
            // 「第二档没动」分不清是钩子吃掉的还是接收方自己不动了。
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            var noHookBefore = ClicksRead();
            await ThreeClicks(centre, one: true);
            var afterNoHook = ClicksRead();
            Log("swallow",
                $"拆掉钩子之后的对照：接收方 {noHookBefore} → {afterNoHook}（该动 —— " +
                "不成立的话第二档那个「没动」就不是钩子干的）");

            _summaries.Add(
                $"swallow 放行档接收方收了 3 下（对照）；拆钩子之后仍收得到；" +
                $"返回非零档接收方**没**收到（{eatBefore}→{afterEat} 不动），" +
                $"而钩子那一侧自己仍看见了 {passes - eatPassBefore} 下左键按下 —— 返回非零 = 那一发真的被吃掉了");
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    /// <summary>把接收方自己起成一扇窗，就干一件事：数收到几下左键按下，然后一直报。
    ///
    /// <b>它存在的唯一理由是「在另一个进程里」</b>。<see cref="HookSwallowMatrix"/> 把接收方放在
    /// 测量程序自己这个进程里，所以它量不到跨进程这一头 ——
    /// <c>WM_NCHITTEST</c> 的 <c>HTTRANSPARENT</c> 只跨同一个线程，
    /// 同一线程里「遮罩说不归它」真的会落到下面那一扇，而跨进程不会。
    /// 这一扇 + 另一个进程里的遮罩，才问得出「底下别的程序收不收得到」。</summary>
    private async Task CrossProcessTarget()
    {
        // 引擎那扇主窗藏起来：这一发只要接收方一扇，多一扇就多一份 z 序上的干扰。
        var ours = GetTree().Root.GetWindow();
        ours.Hide();
        await Frame(0.3f);

        var screen = DisplayServer.ScreenGetSize();
        var target = ClickTarget.Show();
        if (target == IntPtr.Zero)
        {
            Log("passthrough-target", "接收方那扇窗没起来 —— 这一发量不成");
            return;
        }
        ClickTarget.MoveOntoOurs(IntPtr.Zero, new Win32.Rect
        {
            Left = screen.X / 2 - 250,
            Top = screen.Y / 2 - 100,
            Right = screen.X / 2 + 250,
            Bottom = screen.Y / 2 + 100,
        });
        ClickTarget.Raise();
        await Frame(0.5f);
        var where = ClickTarget.Where();
        var centre = new Win32.Point { X = (where.Left + where.Right) / 2, Y = (where.Top + where.Bottom) / 2 };
        Log("passthrough-target",
            $"起来了 hwnd=0x{target:X} 矩形={where} pid={System.Environment.ProcessId} " +
            $"按的点=({centre.X},{centre.Y}) {ClickTarget.LastReport}");

        // `--selfclick`：这一扇**自己点自己**。注入走的是仓库里已量通的那条
        // （<see cref="ThreeClicks"/>：挪/按下/松开分三次交、段间让帧），
        // 而「点完有没有收到」这一读数完全在接收方这一个进程里 ——
        // **外面那一扇遮罩在不在，是唯一变的那一个变量**。
        var self = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--selfclick") >= 0;
        var plain = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--plain") >= 0;
        if (plain) { ClickTarget.Raise(topmost: false); await Frame(0.4f); Log("passthrough-target", "普通顶层窗那一档：" + ClickTarget.LastReport); }

        // **把本进程自己其余的顶层窗全藏起来**（2026-10-10 晚这一族撞的）：
        // `GetTree().Root.GetWindow().Hide()` 不够 —— 引擎那扇主窗（`Keeper (DEBUG)`）
        // 还在命中测试里，而它的矩形正好盖住接收方。读数长这样：
        // `自己点自己：2 → 2 （收到 0/1）0x40097A 'Engine' 'Keeper (DEBUG)'`
        // —— 那一发量的是「点落到自己进程的主窗上」，**接收方压根没被罩住**。
        // **一件量具自己要摆在台面上**：它要是自己被压住了，读数不成立。
        foreach (var hwnd in Win32.OwnTopLevelWindows())
            if (hwnd != target && Win32.IsWindowVisible(hwnd)) Win32.ShowWindow(hwnd, Win32.SwHide);
        await Frame(0.3f);

        while (true)
        {
            await Frame(0.5f);
            if (!self)
            {
                Log("passthrough-target", $"收到={ClicksRead()}");
                continue;
            }
            var before = ClicksRead();
            Win32.MoveTo(centre);
            await Frame(0.3f);
            Win32.PressAt(centre);
            await Frame(0.15f);
            Win32.ReleaseAt(centre);
            await Frame(0.6f);
            var after = ClicksRead();
            // **每一发都印出「点上那一瞬是谁」**：这一发要的正是「遮罩盖住了接收方，
            // 而点击照样到了接收方」，所以那半句不是附注，是这一发的判据本身。
            var who = Who(centre);
            var covered = who.Contains(Fader.TitleOf(0));
            Log("passthrough-target", $"自己点自己：{before} → {after} " +
                                     $"（收到 {after - before} / 1）遮罩盖住这一点={covered} 点上是谁={who}");
        }
    }

    /// <summary>「铺满那一扇一辈子 click-through」这一问 —— **真的点一下，底下那一扇收不收得到**。
    ///
    /// <para><b>它和现有的判据问的不是同一件事</b>。ADR-0003「穿透走引擎旗标」那一节和
    /// <c>FadeRun.Passthrough</c> 判的都是「<c>WindowFromPoint</c> 回谁」——
    /// 那是<b>命中测试</b>。这一发判的是<b>点击消息到不到接收方的 <c>WndProc</c></b>
    /// （<see cref="ClickTarget"/> 数 <c>WM_LBUTTONDOWN</c>）。2026-10-10 晚量到的分歧正是这一句：
    /// 同一时刻、同一块屏、同一个 <c>(1280,720)</c>，本进程读到 Chrome、进程外读到遮罩自己。
    /// **两套读数在读数上长得一模一样，而「点得到点不到」只有真点一下才算数** ——
    /// 所以这一发把两者记在同一时刻，好让下一次不用再猜是哪一把尺子坏了。</para>
    ///
    /// <para><b>四档，后两档互相是对照</b>：
    /// 一、接收方自己在最前面 —— 一定该通，它不成立后面全不算数。
    /// 二、遮罩 <c>MousePassthrough = true</c>（产品那一份的旗标）—— 待证：收得到。
    /// 三、遮罩 <c>MousePassthrough = false</c> —— 一定该不通，它不成立的话
    ///    「收得到」就分不清是旗标给的还是底下那扇本来就在最前面。
    /// 四、旗标再拧回 true —— 把第二档重跑一次，收得到两次才算数。</para>
    private async Task OverlayClickThroughMatrix()
    {
        Log("passthrough",
            $"godot={Engine.GetVersionInfo()["string"]} headless={DisplayServer.GetName() == "headless"} " +
            $"屏={DisplayServer.ScreenGetSize()} pid={System.Environment.ProcessId}");

        var live = SettingsStore.LiveDir();
        var read = SettingsStore.Load(live);
        var settings = read.Current;
        if (read.Error != null) Log("passthrough", "设置那份读不通，照默认值跑：" + read.Error);

        // ---- 接收方：纯 Win32 那一扇，正本在 ClickTarget 的注释里 ------------------
        var target = ClickTarget.Show();
        if (target == IntPtr.Zero)
        {
            Log("passthrough", "接收方那扇窗没起来 —— 这一发量不成");
            return;
        }
        var screen = DisplayServer.ScreenGetSize();
        // 摆在屏幕正中一块看得见的矩形（不依赖引擎那扇主窗，这一发分叉在 Baseline 之前）。
        ClickTarget.MoveOntoOurs(IntPtr.Zero, new Win32.Rect
        {
            Left = screen.X / 2 - 210,
            Top = screen.Y / 2 - 130,
            Right = screen.X / 2 + 210,
            Bottom = screen.Y / 2 + 130,
        });
        ClickTarget.Raise();
        await Frame(0.4f);
        var where = ClickTarget.Where();
        var centre = new Win32.Point { X = (where.Left + where.Right) / 2, Y = (where.Top + where.Bottom) / 2 };
        Log("passthrough",
            $"接收方=0x{target:X} {where} 按的点=({centre.X},{centre.Y}) {ClickTarget.LastReport}");

        // ---- 第一档（一定该通）：没有遮罩，接收方自己收得到 ----------------------
        Win32.MoveTo(centre);
        await Frame(0.3f);
        var before = ClicksRead();
        await ThreeClicks(centre);
        var after = ClicksRead();
        Log("passthrough",
            $"**第一档 没有遮罩（对照，一定该通）**：接收方 {before} → {after}（该 +3）");
        if (after <= before)
        {
            Log("passthrough",
                "**第一档没成立** —— 量具自己收不到，下面三档的读数一律不算数");
            ClickTarget.Close();
            return;
        }

        // ---- 举起真的那一扇遮罩（产品那一份 `Fader`，不是复制品） -----------------
        var plan = settings.Steps();
        var fader = new Fader(settings, 0,
            Todos.Load(Records.ReadPath(live, Todos.FileName, Todos.LegacyFileName)),
            () => { }, () => DateTime.Now, message => Log("fader", message));
        AddChild(fader);
        fader.Raise(new Scheduler.StageNow(plan[0], Scheduler.StageMs(plan, 0, settings),
            0, plan.Count, plan.Count <= 1));
        // 等淡入走完（落定那一发才发 Arm，而这一问要的是**罩住整块屏**那一刻，不是收鼠标那一刻）
        await Frame((float)Math.Max(3.0, settings.FadeInMs / 1000.0 + 1.0));
        var ours = Win32.OwnWindowNamed(Fader.TitleOf(0));
        Log("passthrough",
            $"遮罩 HWND=0x{ours:X} 鼠标穿过={fader.MousePassthrough} 收鼠标的那一块={fader.SwallowRect} " +
            $"收没收={fader.Armed} 交互={fader.Interactive}");

        var atop = Win32.GetAncestorRoot(Win32.WindowFromPoint(centre)) == ours;
        Log("passthrough",
            $"那个点上是谁={Who(centre)} 是我们的遮罩={atop}" +
            (atop ? "" : " —— **量不成**：那一块不是遮罩在最前面，下面几档量的不是「底下被罩住」"));
        Log("passthrough", SweepGrid(ours, centre));

        try
        {
            // ---- 第二档（待证）：旗标 true，底下的接收方该照常收得到 ----------------
            fader.MousePassthrough = true;
            await Frame(0.5f);
            await ClickPass("第二档 遮罩 MousePassthrough=true", centre);
            await Frame(0.5f);

            // ---- 第三档（一定该不通）：旗标 false，底下的该收不到 -----------------
            fader.MousePassthrough = false;
            await Frame(0.5f);
            await ClickPass("第三档 遮罩 MousePassthrough=false", centre);
            await Frame(0.5f);

            // ---- 第四档：旗标再拧回 true，把第二档重跑一次 ----------------------
            fader.MousePassthrough = true;
            await Frame(0.5f);
            await ClickPass("第四档 再拧回 true", centre);
        }
        finally
        {
            fader.QueueFree();
            ClickTarget.Close();
        }
    }

    /// 挡数：点三下真的左键，读接收方收到几发。<paramref name="what"/> 只是印在日志上的那一句话。
    private async Task ClickPass(string what, Win32.Point centre)
    {
        Win32.MoveTo(centre);
        await Frame(0.3f);
        var before = ClicksRead();
        await ThreeClicks(centre);
        var after = ClicksRead();
        Log("passthrough",
            $"**{what}**：接收方 {before} → {after}（收到 {after - before} / 3）；" +
            $"点上是谁={Who(centre)}");
    }

    /// 那个点上是谁（顶层那个）。
    private static string Who(Win32.Point at)
    {
        var hit = Win32.WindowFromPoint(at);
        if (hit == IntPtr.Zero) return "none";
        var root = Win32.GetAncestorRoot(hit);
        return $"0x{root.ToInt64():X} '{Win32.ClassName(root)}' '{Win32.WindowText(root)}'";
    }

    /// 整块屏扫一遍，有几个点归遮罩自己。
    ///
    /// **要它是因为 2026-10-10 那一发分歧**：进程外的整屏扫描说「每一格都归遮罩」，
    /// 而同一个程序自己的中心点读数说「不是我们的」。两把尺子在同一时刻对打时，
    /// 这一发要的是**本进程自己的整屏读数** —— 至少从这一侧看，它答什么就记什么。
    private string SweepGrid(nint ours, Win32.Point skip)
    {
        var size = DisplayServer.ScreenGetSize();
        var origin = DisplayServer.ScreenGetPosition(0);
        var mine = 0;
        var total = 0;
        var under = new List<string>();
        for (var y = origin.Y + 60; y < origin.Y + size.Y - 40; y += 120)
            for (var x = origin.X + 80; x < origin.X + size.X - 40; x += 160)
            {
                var at = new Win32.Point { X = x, Y = y };
                if (at.X == skip.X && at.Y == skip.Y) continue;
                total++;
                var root = Win32.GetAncestorRoot(Win32.WindowFromPoint(at));
                if (root == ours) mine++;
                else
                {
                    var name = $"{Win32.ClassName(root)}/{Win32.WindowText(root)}";
                    if (!under.Contains(name)) under.Add(name);
                }
            }
        return $"整屏扫 {total} 个点（跳过接收方那个点）：归遮罩自己 {mine} 个；底下是 [{string.Join(", ", under)}]";
    }

    /// 接收方收到的次数。**读它就是读那一族的实测结果** —— 那一扇自己数自己收到的，
    /// 而我们这一侧只有钩子，两端分属两处。
    private static int ClicksRead() => ClickTarget.Clicks;

    /// 三下真的左键。**挪 / 按下 / 松开分三次交、每次之间让帧**
    /// （<see cref="DropSide"/> 那一族那条教训，2026-10-10 这一发又撞了一次：
    /// <see cref="Win32.ClickAt"/> 把三发塞进**同一批** `SendInput`，那一端一个都收不到）。
    private async Task ThreeClicks(Win32.Point centre, bool one = false)
    {
        for (var i = 0; i < (one ? 1 : 3); i++)
        {
            Win32.PressAt(centre);
            await Frame(0.15f);
            Win32.ReleaseAt(centre);
            await Frame(0.25f);
        }
    }

    private bool BlockSitsOnDesktop(Win32.Rect block)
    {
        var pt = new Win32.Point { X = block.Left + 60, Y = block.Top + 60 };
        var root = Win32.GetAncestorRoot(Win32.WindowFromPoint(pt));
        // **不是** DesktopLayer.IsWallpaperLayer：那一档还认 SHELLDLL_DefView，而这里问的是
        // 「根窗是不是 shell 那两个顶层之一」。GetAncestorRoot 拿到的是顶层，DefView 是子窗、
        // 当不了根，所以两者在这一发上答案相同 —— 但判据问的不是同一件事，别为了少一处字面量
        // 把它们并成一条。
        return Win32.ClassName(root) is DesktopLayer.ProgmanClass or DesktopLayer.WorkerWClass;
    }

    /// Every visible top-level window, whichever process owns it. Win11's shell menu
    /// is a XAML flyout rather than the classic #32768 window, and it is not
    /// guaranteed to belong to explorer's pid either, so the only honest detector is
    /// "what appeared that was not there a moment ago" — printed with its owner so a
    /// wrong guess is visible in the log rather than counted as a failure.
    private HashSet<nint> SnapshotVisibleTopLevels()
    {
        var seen = new HashSet<nint>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.IsWindowVisible(hwnd)) seen.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return seen;
    }

    private void ReportMenuSnapshot()
    {
        var before = _windowsBeforeMenu;
        _windowsBeforeMenu = null;
        _menuSnapshotPending = false;

        if (before == null) return;
        var appeared = SnapshotVisibleTopLevels();
        appeared.ExceptWith(before);
        if (appeared.Count == 0)
        {
            Log("menu", "the right press opened nothing");
            return;
        }

        var lines = new List<string>();
        foreach (var hwnd in appeared)
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            lines.Add($"0x{hwnd:X} '{Win32.ClassName(hwnd)}' '{WindowTitleOf(hwnd)}' pid={pid}");
        }
        _menusSeen++;
        Log("menu", $"the right press opened: {string.Join(" | ", lines)}");
    }

    /// items = how many icons the desktop has; selected = how many are picked.
    /// Read out of Explorer's own control, so it reports what the icon layer did
    /// with the click rather than what this process believes about its window.
    private (long Items, long Selected) ReadIconLayer()
    {
        if (_iconView == IntPtr.Zero) return (-1, -1);
        return (Win32.SendMessage(_iconView, Win32.LvmGetItemCount, 0, 0),
            Win32.SendMessage(_iconView, Win32.LvmGetSelectedCount, 0, 0));
    }

    private static string FormatIconLayer((long Items, long Selected) layer)
        => $"items={layer.Items} selected={layer.Selected}";

    private static string Underneath(Win32.Rect block)
    {
        var pt = new Win32.Point { X = block.Left + 40, Y = block.Top + 40 };
        var hit = Win32.WindowFromPoint(pt);
        return $"'{Win32.ClassName(hit)}' under a root of '{Win32.ClassName(Win32.GetAncestorRoot(hit))}'";
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message is Win32.WmLButtonDown or Win32.WmRButtonDown)
            {
                var data = Marshal.PtrToStructure<Win32.MouseLlHookStruct>(lParam);
                Win32.GetWindowRect(_hwnd, out var block);
                var inside = data.Pt.X >= block.Left && data.Pt.X < block.Right
                    && data.Pt.Y >= block.Top && data.Pt.Y < block.Bottom;
                if (inside) _clicksInsideUsDuringHold++;
                if (message == Win32.WmRButtonDown)
                {
                    // Snapshot before the press is delivered, so whatever it opens
                    // shows up as a difference rather than as a guess.
                    _pressesTotal++;
                    _windowsBeforeMenu = SnapshotVisibleTopLevels();
                    _menuSnapshotPending = true;
                }
                Log("press",
                    $"#{++_pressLogCount} {data.Pt.X},{data.Pt.Y} " +
                    $"{(message == Win32.WmRButtonDown ? "right" : "left")} inside_block={inside} " +
                    $"hit_test_says={Win32.ClassName(Win32.WindowFromPoint(data.Pt))}");
            }
        }
        return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.Pressed) _engineMouseEvents++;
    }

    private void Verdict()
    {
        // A headless run has no window at all, so every check reads false there — and a
        // false that was never measured is exactly the misleading log line this probe has
        // already been caught printing twice.
        if (DisplayServer.GetName() == "headless")
        {
            Log("verdict", "headless：本轮只跑设置自检，下面那四行实测结果都不成立，别拿去当结论");
            return;
        }

        // A hold run skips the automated steps, so it has no business printing
        // their verdicts — an unmeasured false would read as a failed check.
        if (!_wantsHandCheck)
        {
            Log("verdict",
                $"check 3 engine route: fall-through caused by MousePassthrough = {_engineFlagCausesFallThrough}, " +
                $"survives resize/move/topmost/title = {_engineFlagSurvivesEngineOps}");
            Log("verdict",
                $"check 3 raw route: fall-through caused by WS_EX_TRANSPARENT alone = {_rawBitCausesFallThrough}");
            Log("verdict",
                $"check 2: 摆着就一直没有爹 = {_parkedStaysParentless}, " +
                $"across frames = {_parkedSurvivesFrames}, " +
                $"across an engine resize = {_parkedSurvivesEngineResize}, " +
                $"across an engine title change = {_parkedSurvivesEngineTitle}");
            Log("verdict",
                $"check 1: desktop visible through the window = {_transparencyWorks}, " +
                $"alpha follows {_blendModel}, blend survives hide/show = {_blendSurvivesShowHide}");
        }
        else
        {
            foreach (var summary in _summaries) Log("verdict", summary);
            Log("verdict",
                "what each cue had to show: 1 opened a menu, 2 no menu and engine>0, " +
                "3 menu and engine=0. Cue 1 failing means the detector is blind, not the fader.");
        }
    }

    // ---- --fg: 前台是谁（工单 01）-------------------------------------------------
    //
    // 这一档是**测量程序自己**问 OS，不碰产品代码：产品那一侧（检测模块）是工单 02 的活。
    // 它印的每一发实测结果都要和「当时前台是谁」对齐，所以采样的密度取 0.2 秒 ——
    // 同一个间隔下，某一发实测结果就能定位到那一发是谁在前台，而不是「这几秒里大概是它」。

    /// <summary>旗后面跟的那个秒数。</summary>
    private static double SecondsOf(string[] args, string flag) =>
        ForegroundProbe.SecondsFrom(args, flag, 30);

    private async Task ForegroundIdentity(double seconds)
    {
        Log("fg",
            $"这一档量的是「此刻前台是谁」，跑 {seconds} 秒，每 {ForegroundProbe.SampleSeconds} 秒一发。" +
            $"display_server={DisplayServer.GetName()}" +
            $"（headless 时本进程一扇窗都没有，读到的前台只能是别人起的那个；非 headless 时" +
            $"Godot 自己那扇主窗会一起抢前台，所以那一发读的是测量程序和目标谁在前台）");

        // 放对照进去再开始：自己这个 pid 的 OpenProcess 一定开得了、路径一定问得出来。
        // 它不成立的话，下面每一发「问不出路径」都可能是测量程序坏了而不是那一档真的不通。
        Log("fg", ForegroundProbe.ControlRow());

        // 三档对象的 pid 扫描。单独跑是因为「别的用户 / 系统进程」那一档的窗当不了前台 ——
        // 前台链路上量不到它，只能直接拿 pid 去问 OpenProcess。
        Log("fg", "pid 扫描（量的是 OpenProcess 那一发本身，不经前台）：");
        foreach (var row in ForegroundProbe.PidSweepRows()) Log("fg", "  " + row);

        var samples = 0;
        var elapsed = 0.0;
        while (elapsed < seconds)
        {
            await Frame((float)ForegroundProbe.SampleSeconds);
            elapsed += ForegroundProbe.SampleSeconds;
            samples++;
            try
            {
                Log("fg", $"t={elapsed:F1}s {ForegroundProbe.Sample()}");
            }
            catch (Exception e)
            {
                // 一发实测结果抛了不能把整轮带走：这一发印不出来，下一发还能印，
                // 而「这一发抛了」这件事本身要留在日志里。
                Log("fg", $"t={elapsed:F1}s 这一发抛了：{e.GetType().Name} {e.Message}");
            }
        }

        // 一轮读完给一行合计。**不给结论**：这一档量到的是实测结果，结论要连着实测结果和日期写进
        // docs/research/detection-module-api-survey.md，由那里收（工单 01 的 ## Comments 只放定案）。
        Log("fg", $"这一档跑完：{samples} 发，覆盖 {seconds} 秒；量到的和量不到的写进 survey 第 1、3、6 节");

        // 取向（和 _Ready 末尾那一条同源）：**--check 之外的阶段故意不给退出码。**
        // 「某一档没量到」不是失败，而把它折成退出码等于把没量到的 false 当成失败。
    }

    // ---- --drag: 拖放三个虚函数、命中、刻度线的可算性、像素吸附开关 ----------------------
    //
    // docs/research/godot-drag-and-snap.md 记的是**官方文档**那一层，不是本机实测结果。
    // 它自己第 6 行就写着这一发还没跑。这一发跑的就是它列不出来的那四问：
    //   一、三个虚函数在 4.7 这颗引擎上真被调到没有，参数形状对不对（尤其 at_position）。
    //   二、拖动进行中能不能问出「现在在不在拖」和「拖的是什么」—— 文档里那两个 C# 名字
    //       （GuiIsDragging / GuiGetDragData）是**按命名约定推的**，那一发没有读到代码块。
    //   三、整像素吸附开关默认什么值、拧开拧关对 Control.Position 各是什么读数。
    //   四、手柄命中那一层能不能自己算：自己判「事件落在不在边上」和引擎的矩形差多少。
    // 前三问要一根真的鼠标，第四问纯算术。喂法走 PushInput，和 --push 那一族同一条路 ——
    // 那一族已经把「哪几型事件收得到」量出来了，不重问。

    /// 一颗能拖的控件。三个虚函数都在这儿，命中判定和刻度线**自己算自己画**，
    /// —— 引擎那一层在文档里查了五个类零命中（见那份调研），所以这一族量的是
    /// 「引擎给的那三个虚函数到底给不给」，不是「引擎给不给吸附」。
    private sealed partial class Draggable : Panel
    {
        public Action<string>? Note;
        public Action<string>? DropNote;
        public int DropTargetCanCalls, DropTargetDropCalls;
        public Vector2 LastTargetCanAt, LastTargetDropAt;
        public Variant LastTargetData;
        public int GetCalls, CanCalls, DropCalls, PreviewCalls;
        public Vector2 LastGetAt, LastCanAt, LastDropAt;
        public Variant LastData;

        public override Variant _GetDragData(Vector2 atPosition)
        {
            GetCalls++;
            LastGetAt = atPosition;
            Note?.Invoke($"_GetDragData at=({atPosition.X:F1},{atPosition.Y:F1}) " +
                        $"是inf={float.IsInfinity(atPosition.X) || float.IsInfinity(atPosition.Y)}");
            SetDragPreview(new Panel { CustomMinimumSize = new Vector2(40, 12) });
            PreviewCalls++;
            return Variant.From(new string("dragged"));
        }

        // 「源」和「目标」在这一颗上都实现了这两个虚函数 —— C# 不许一颗同时 override
        // 两次，所以源和目标共用一份实现，各自记各自的数。第一版只有源，
        // 于是落点又是源自己，_CanDropData/_DropData 读出来全是 0：拖回自己身上不算放下。
        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            CanCalls++;
            DropTargetCanCalls++;
            LastCanAt = LastTargetCanAt = atPosition;
            Note?.Invoke($"_CanDropData at=({atPosition.X:F1},{atPosition.Y:F1}) data={data.VariantType}");
            return true;
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            DropCalls++;
            DropTargetDropCalls++;
            LastDropAt = LastTargetDropAt = atPosition;
            LastData = LastTargetData = data;
            Note?.Invoke($"_DropData at=({atPosition.X:F1},{atPosition.Y:F1}) data={data}");
        }
    }

    /// 自己算刻度线的那一档：吸的是**别的控件的边和它们的中间线**。这一族不量「引擎给不给」
    /// （文档里查过，答「不给」），量的是**自己算得出来算不出来** —— 也就是说命中判定
    /// 能不能在份额坐标上做对。它和 Draggable 分开是因为一个量引擎、一个量自己。
    private sealed partial class GuideProbe : Control
    {
        public List<Rect2> Edges = [];
        public int LastGuideHits;
        public float SnapThreshold = 6f;

        /// 返回该吸到哪一条线上。没到门限就原样返回 —— 那一档「没吸住」也是一种读数。
        public Vector2 Snap(Vector2 pos, Vector2 ownSize, Rect2 bounds)
        {
            var guides = new List<(float line, Vector2 at)>();
            // 屏的四边和屏的中线。
            foreach (var f in new[] { 0f, 0.5f, 1f })
            {
                guides.Add((bounds.Size.X * f, new Vector2(bounds.Size.X * f, pos.Y)));
                guides.Add((bounds.Size.Y * f, new Vector2(pos.X, bounds.Size.Y * f)));
            }
            // 别的控件的边和它们的中间线。
            foreach (var e in Edges)
            {
                foreach (var f in new[] { 0f, 0.5f, 1f })
                {
                    guides.Add((e.Position.X + e.Size.X * f, new Vector2(e.Position.X + e.Size.X * f, pos.Y)));
                    guides.Add((e.Position.Y + e.Size.Y * f, new Vector2(pos.X, e.Position.Y + e.Size.Y * f)));
                }
            }

            var best = pos;
            var bestDist = SnapThreshold;
            var hits = 0;
            // 两条边各吸一次：左边对左边、上边对上边。手柄那一族量的是这个循环。
            foreach (var (line, _) in guides)
            {
                var left = line - ownSize.X / 2f;
                if (Math.Abs(left - pos.X) < bestDist) { bestDist = Math.Abs(left - pos.X); best = new Vector2(left, best.Y); hits++; }
            }
            bestDist = SnapThreshold;
            foreach (var (line, _) in guides)
            {
                var top = line - ownSize.Y / 2f;
                if (Math.Abs(top - pos.Y) < bestDist) { bestDist = Math.Abs(top - pos.Y); best = new Vector2(best.X, top); }
            }
            LastGuideHits = hits;
            return best;
        }

        public override void _Draw()
        {
            // 刻度线自己画：屏的中线、屏的四边、别的控件的边和它们的中间线。
            var col = new Color(1, 1, 1, 0.6f);
            DrawLine(new Vector2(Size.X / 2, 0), new Vector2(Size.X / 2, Size.Y), col, 1);
            DrawLine(new Vector2(0, Size.Y / 2), new Vector2(Size.X, Size.Y / 2), col, 1);
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(1, 1, 1, 0.35f), false, 1);
            foreach (var e in Edges)
            {
                DrawRect(e, new Color(0.4f, 0.8f, 1, 0.5f), false, 1);
                DrawLine(new Vector2(e.Position.X + e.Size.X / 2, 0),
                         new Vector2(e.Position.X + e.Size.X / 2, Size.Y), new Color(0.4f, 0.8f, 1, 0.25f), 1);
            }
        }
    }

    private async Task DragMatrix()
    {
        Log("drag",
            $"godot={Engine.GetVersionInfo()["string"]} headless={DisplayServer.GetName() == "headless"} " +
            $"embed_subwindows={ProjectSettings.GetSetting("display/window/subwindows/embed_subwindows")}");

        // ---- 第零问：四个成员在 C# 里到底叫什么 ----------------------------------------
        // 调研那份文档里 GuiIsDragging / GuiGetDragData 是**按 GDScript 的 snake_case 推的**，
        // 那一发没在类文档里读到 C# 代码块。这一问先问「这个名字能不能编出来」——
        // 编不出来就是文档推错了，写在那份文档里的用法得跟着改。
        foreach (var (label, act) in new (string, Func<object>)[]
                 {
                     ("Viewport.GuiIsDragging", () => GetTree().Root.GuiIsDragging()),
                     ("Viewport.GuiGetDragData", () => GetTree().Root.GuiGetDragData()),
                     ("Viewport.GuiCancelDrag", () => { GetTree().Root.GuiCancelDrag(); return "(void)"; }),
                     ("Viewport.GuiIsDragSuccessful", () => GetTree().Root.GuiIsDragSuccessful()),
                     ("Viewport.SetDragForwarding", () => typeof(Viewport).GetMethod("SetDragForwarding") != null),
                     ("Viewport.ForceDrag", () => typeof(Viewport).GetMethod("ForceDrag") != null),
                     ("Viewport.AccessibilityDrag", () => typeof(Viewport).GetMethod("AccessibilityDrag") != null),
                     ("Control.SetDragPreview", () => typeof(Control).GetMethod("SetDragPreview") != null),
                     ("Control._GetDragData", () => typeof(Control).GetMethod("_GetDragData") != null),
                     ("Control._CanDropData", () => typeof(Control).GetMethod("_CanDropData") != null),
                     ("Control._DropData", () => typeof(Control).GetMethod("_DropData") != null),
                 })
        {
            string got;
            try { got = Convert.ToString(act()) ?? "(null)"; }
            catch (Exception e) { got = "抛异常：" + e.GetType().Name; }
            Log("drag", $"名字 {label} → {got}");
        }

        var win = new Window
        {
            Title = "keeper-dragprobe",
            Position = new Vector2I(160, 160),
            Size = new Vector2I(640, 400),
        };
        await Frame(0.3f);
        GetTree().Root.AddChild(win);
        await Frame(0.6f);
        if (!win.IsInsideTree())
        {
            Log("drag", "这扇窗没进树，下面每一行都不成立 —— 先修这一步");
            return;
        }

        // ---- 整像素吸附：默认值 + 拧开拧关对 Position 的读数 ------------------------
        // 这一问纯读数，不喂鼠标。存的是份额的话，转出来是连续数，引擎再舍一遍就该看见了。
        var snapControl = new Control { Position = new Vector2(100.4f, 50.6f) };
        win.AddChild(snapControl);
        await Frame(0.3f);
        Log("drag", $"gui_snap_controls_to_pixels 默认={GetTree().Root.GuiSnapControlsToPixels} " +
                    $"摆上去 100.4 读到 {snapControl.Position.X}");
        GetTree().Root.GuiSnapControlsToPixels = false;
        snapControl.Position = new Vector2(100.4f, 50.6f);
        await Frame(0.3f);
        Log("drag", $"关掉之后摆 100.4 读到 {snapControl.Position.X}");
        GetTree().Root.GuiSnapControlsToPixels = true;
        snapControl.Position = new Vector2(100.4f, 50.6f);
        await Frame(0.3f);
        Log("drag", $"拧回来摆 100.4 读到 {snapControl.Position.X}");
        snapControl.QueueFree();

        // ---- 拖放三个虚函数：一整趟真拖，看每一步各被调到没有 ------------------------
        var canvas = new GuideProbe { Position = new Vector2(0, 0), Size = new Vector2(640, 400), MouseFilter = Control.MouseFilterEnum.Stop };
        var other = new Panel { Position = new Vector2(400, 40), Size = new Vector2(120, 60) };
        var piece = new Draggable
        {
            Position = new Vector2(100, 100),
            Size = new Vector2(80, 40),
            Note = s => Log("drag", $"  {s}"),
        };
        // 落下那一格要有一颗**不是源自己**的控件。第一版拿源自己当目标，
        // 读出来 _CanDropData/_DropData 全是 0 —— 拖回自己身上不算放下，
        // 那是量具摆错了，不是引擎不给。
        var target = new Draggable
        {
            Position = new Vector2(380, 260),
            Size = new Vector2(120, 60),
            Note = _ => { },
            DropNote = s => Log("drag", $"  {s}"),
        };
        canvas.Edges = [other.GetRect(), target.GetRect()];
        win.AddChild(canvas);
        canvas.AddChild(other);
        canvas.AddChild(piece);
        canvas.AddChild(target);
        await Frame(0.4f);

        var centre = piece.Position + piece.Size / 2f;
        Log("drag", $"控件摆={piece.Position}+{piece.Size} 中心={centre} " +
                    $"画布={canvas.Size} 别的那个={other.GetRect()}");

        var root = GetTree().Root;
        // 喂给**控件所在那扇窗**，不是根视口。第一版喂的是 root，读出来三个虚函数全是 0 ——
        // 那读的是「事件没进这扇窗」，不是「引擎不给」。--push 那一族量过同一件事：
        // 控件在代码造的子窗里时，事件得 PushInput 给那个 Window 当视口才收得到。
        root.PushInput(new InputEventMouseButton
        {
            Position = centre, ButtonIndex = MouseButton.Left, Pressed = true,
            ButtonMask = MouseButtonMask.Left,
        });
        await Frame(0.2f);
        Log("drag", $"喂根视口：_GetDragData={piece.GetCalls} 在拖={root.GuiIsDragging()}");

        piece.GetCalls = piece.CanCalls = piece.DropCalls = piece.PreviewCalls = 0;
        win.PushInput(new InputEventMouseButton
        {
            Position = centre, ButtonIndex = MouseButton.Left, Pressed = true,
            ButtonMask = MouseButtonMask.Left,
        });
        await Frame(0.2f);
        Log("drag", $"喂那扇窗：_GetDragData={piece.GetCalls} 在拖={root.GuiIsDragging()}");

        // 按下 → 挪一格 → 问「在不在拖」→ 松开。三样都要真的走到引擎那一条路上，
        // 不然读到的是「没喂进去」而不是「引擎不给」。
        // 终点落在那颗目标控件的正当中 —— 落点要和源分开，才问得出「引擎问没问目标」。
        var targetCentre = target.Position + target.Size / 2f;
        // **一格一格挪过去，不是一格跳过去。** GUI 的拖动状态机要看到中间那些移动才推进
        // ——第三版只发了一个终点运动，于是 _CanDropData 读到 0，而 _GetDragData 和
        // 「在拖=True」都已经在读数里成立：拖动起来了，是这一发没走完引擎那一段路。
        for (var step = 1; step <= 6; step++)
        {
            var p = centre + (targetCentre - centre) * (step / 6f);
            win.PushInput(new InputEventMouseMotion
            {
                Position = p, Relative = (targetCentre - centre) / 6f,
                ButtonMask = MouseButtonMask.Left,
            });
            await Frame(0.06f);
        }
        await Frame(0.2f);
        Log("drag", $"挪到目标正中 ({(targetCentre.X):F0},{(targetCentre.Y):F0}) 那一瞬 " +
                    $"在拖={root.GuiIsDragging()} 拖的东西={root.GuiGetDragData().VariantType}");

        // 落点这一格先把「引擎眼里那儿是什么」读出来，再问有没有问目标。
        // 少这一步的话，读出 _CanDropData=0 分不清是「没问」还是「问了但落点不在目标上」。
        // 第四版：把「引擎到底给不给」和「注入这条路走不走得完那一段」分开。
        // 落点那格读数成立（目标里=True、can_process=True、目标在树上=True）而
        // _CanDropData 仍是 0 —— 引擎的 drop 那一段不吃注入的移动，**这一发量不到它**。
        // 所以这里只记「量到了 / 没量到」，不记「引擎不给」。要答「引擎给不给」，
        // 得用真鼠标走一趟（那一发要人，那是 CODING_STANDARDS.md 里「要人 cue 的那几发」）。
        Log("drag", $"落点判定：引擎眼里的落点在目标内={target.GetRect().HasPoint(targetCentre)} " +
                    $"目标可处理={target.CanProcess()} → 注入这条路 _CanDropData={target.DropTargetCanCalls} " +
                    $"**没量到**，不是「引擎不给」（判据：CODING_STANDARDS.md「要人 cue 的那几发不进自检」）");

        win.PushInput(new InputEventMouseButton
        {
            Position = targetCentre, ButtonIndex = MouseButton.Left, Pressed = false,
        });
        await Frame(0.3f);
        Log("drag", $"松开之后 在拖={root.GuiIsDragging()} 成功={root.GuiIsDragSuccessful()} " +
                    $"目标那几发：_CanDropData={target.DropTargetCanCalls} _DropData={target.DropTargetDropCalls}");

        // 分开读，别把三个虚函数揉在一行里 —— 揉在一起就答不出「三个都给不给」。
        Log("drag", $"三个虚函数：_GetDragData={piece.GetCalls} _CanDropData={piece.CanCalls} " +
                    $"_DropData={piece.DropCalls} SetDragPreview={piece.PreviewCalls} " +
                    $"| 源侧 _GetDragData={piece.GetCalls}，目标侧 _CanDropData={target.DropTargetCanCalls} " +
                    $"_DropData={target.DropTargetDropCalls}（drop 那一半没量到，见下）");
        Log("drag", $"at_position：get=({piece.LastGetAt.X:F1},{piece.LastGetAt.Y:F1}) " +
                    $"can=({piece.LastCanAt.X:F1},{piece.LastCanAt.Y:F1}) " +
                    $"drop=({piece.LastDropAt.X:F1},{piece.LastDropAt.Y:F1})");

        // ---- 键盘发起的拖动：at_position 是不是 INF --------------------------------
        // 文档说键盘快捷键和 accessibility_drag 发起时 at_position 是 Vector2.INF。
        // 这一问用引擎自己那条路（AccessibilityDrag / ForceDrag）去撞，撞得到才算数。
        piece.GetCalls = piece.CanCalls = piece.DropCalls = 0;
        Log("drag", $"对照：float.IsInfinity(inf)={float.IsPositiveInfinity(1f / 0f)} " +
                    $"float.IsInfinity(-inf)={float.IsNegativeInfinity(-1f / 0f)} " +
                    $"命中判定若不分开这一支就会拿它去算");

        // ---- 刻度线：自己算得出来算不出来（纯算术，不喂鼠标）-------------------------
        // 摆的是**控件自己的左上角**，而刻度线算的是「控件中心对上线」。
        // 第一版把中心当位置喂进去，于是「正中」那一条读出来不动 —— 那是量具摆错了，
        // 不是吸附不成立。要读的是：中心差多少以内吸得住。
        var bounds = new Rect2(Vector2.Zero, canvas.Size);
        var half = piece.Size / 2f;
        foreach (var (name, centreOf) in new[]
                 {
                     ("中心正落在屏中线上", bounds.Size / 2f),
                     ("中心差 2 之内", bounds.Size / 2f + new Vector2(-2, 0)),
                     ("中心差 10 之外", bounds.Size / 2f + new Vector2(10, 0)),
                     ("中心贴左上角", Vector2.Zero),
                     ("中心对别的控件中线", other.Position + other.Size / 2f),
                 })
        {
            var p = centreOf - half;
            var snapped = canvas.Snap(p, piece.Size, bounds);
            Log("drag", $"刻度线 {name}：摆=({p.X:F1},{p.Y:F1}) → 吸到=({snapped.X:F1},{snapped.Y:F1}) " +
                        $"动了={Math.Abs(snapped.X - p.X) + Math.Abs(snapped.Y - p.Y) > 0.01f}");
        }

        // ---- 手柄命中：自己判边，和引擎那个矩形差多少 ------------------------------
        var handleBand = 6f;
        var inRect = new Rect2(Vector2.Zero, piece.Size);
        foreach (var (name, at) in new[]
                 {
                     ("正中", inRect.GetCenter()),
                     ("左边带上", new Vector2(2, inRect.Size.Y / 2)),
                     ("右上角", new Vector2(inRect.Size.X - 2, 2)),
                     ("外面一点", new Vector2(inRect.Size.X + 4, inRect.Size.Y / 2)),
                 })
        {
            var onLeftEdge = Math.Abs(at.X) <= handleBand;
            var onTopEdge = Math.Abs(at.Y) <= handleBand;
            Log("drag", $"手柄命中 {name} at=({at.X:F0},{at.Y:F0}) 左边带={onLeftEdge} 上边带={onTopEdge} " +
                        $"在矩形内={inRect.HasPoint(at)}");
        }

        win.QueueFree();
    }

    // ---- --drop: 落侧那一问，走系统那一层的真拖 -------------------------------------
    //
    // --drag 那一发量到了源侧（_GetDragData / SetDragPreview 各 1 次、GuiIsDragging=True），
    // 落侧 _CanDropData / _DropData 一格都走不到。那一发喂的是引擎的 PushInput（进程内），
    // 落点读数成立而计数是 0 —— 所以那一发只能答「注入这条路走不到落侧」，
    // **答不出「引擎给不给落侧」**。
    //
    // 这一发换喂法：Win32.SendInput，真系统鼠标事件（走窗口消息那一条路，
    // 和人手拖是同一条）。核心是**先验事件真的进了这扇窗**：
    // 落侧计数为 0 时分不清「没送到」和「引擎不给」，所以下面那条对照
    // （点一下源，看按钮自己的信号有没有动）是这一发的判据 —— 它一定该通。

    private async Task DropSide()
    {
        Log("drop",
            $"godot={Engine.GetVersionInfo()["string"]} headless={DisplayServer.GetName() == "headless"} " +
            $"屏={DisplayServer.ScreenGetSize()} 工作区={DisplayServer.ScreenGetUsableRect()}");

        var win = new Window
        {
            Title = "keeper-dropprobe",
            Position = new Vector2I(160, 120),
            Size = new Vector2I(640, 400),
        };
        await Frame(0.3f);
        GetTree().Root.AddChild(win);
        await Frame(0.8f);
        if (!win.IsInsideTree())
        {
            Log("drop", "这扇窗没进树，下面每一行都不成立 —— 先修这一步");
            return;
        }

        var canvas = new GuideProbe { Position = new Vector2(0, 0), Size = new Vector2(640, 400), MouseFilter = Control.MouseFilterEnum.Stop };
        var piece = new Draggable
        {
            Position = new Vector2(80, 80),
            Size = new Vector2(80, 40),
            Note = s => Log("drop", $"  源 {s}"),
            DropNote = s => Log("drop", $"  {s}"),
        };
        var target = new Draggable
        {
            Position = new Vector2(400, 260),
            Size = new Vector2(120, 60),
            Note = _ => { },
            DropNote = s => Log("drop", $"  {s}"),
        };
        canvas.Edges = [target.GetRect()];
        win.AddChild(canvas);
        canvas.AddChild(piece);
        canvas.AddChild(target);
        await Frame(0.5f);

        var root = GetTree().Root;
        // 真鼠标的落点要算成**屏幕物理坐标**。控件的全局矩形是视口坐标，
        // 加这扇窗在屏幕上的位置才是屏幕坐标 —— 这一步算错的话，
        // SendInput 把光标送到别的地方，下面每一行都不成立。
        var pieceScreen = piece.GetGlobalRect().GetCenter() + win.Position;
        var targetScreen = target.GetGlobalRect().GetCenter() + win.Position;
        Log("drop", $"窗 position={win.Position} size={win.Size} " +
                    $"源屏幕点=({pieceScreen.X:F0},{pieceScreen.Y:F0}) " +
                    $"目标屏幕点=({targetScreen.X:F0},{targetScreen.Y:F0})");

        // ---- 对照：系统这一层的点击，源那颗自己的信号到底动不动 --------------------
        // 这一发是这一整个量具的判据。真点击一定该触发 Button.Pressed；
        // 不动的话，后面「落侧是 0」读的是「事件没送到」，不是引擎的事。
        var clickProbe = new Button
        {
            Text = "对照:真点我",
            Position = new Vector2(80, 300),
            Size = new Vector2(160, 44),
        };
        var clicks = 0;
        clickProbe.Pressed += () => clicks++;
        canvas.AddChild(clickProbe);
        await Frame(0.4f);
        var clickScreen = clickProbe.GetGlobalRect().GetCenter() + win.Position;
        var sentClick = Win32.ClickAt(new Win32.Point { X = (int)clickScreen.X, Y = (int)clickScreen.Y });
        await Frame(0.6f);
        Log("drop", $"**对照**：真点一下那颗钮 SendInput 交了 {sentClick} 发，按钮 Pressed={clicks} " +
                    $"→ {(clicks > 0 ? "系统这一层的点击收得到，量具成立" : "系统这一层收不到，下面每一行都不成立")}");

        // ---- 真拖：源 → 目标 --------------------------------------------------------
        piece.GetCalls = piece.CanCalls = piece.DropCalls = piece.PreviewCalls = 0;
        target.DropTargetCanCalls = target.DropTargetDropCalls = 0;

        // 先把光标挪到源上面（不按键），让引擎先认一遍「鼠标在这儿」。
        var moved = Win32.MoveTo(new Win32.Point { X = (int)pieceScreen.X, Y = (int)pieceScreen.Y });
        await Frame(0.4f);

        // 分三段交，每一段之间让出一帧。第一版把 19 发一次 SendInput 全交进去，
        // 读出来连**拿起**那一发都没调到（源侧 0）—— 全在同一帧里进去，
        // GUI 的拖动状态机来不及进「正在拖」那一档。真人手拖天然跨很多帧，
        // 引擎那一层就是按跨帧设计的。量具错了，不是引擎不给。
        var fromPt = new Win32.Point { X = (int)pieceScreen.X, Y = (int)pieceScreen.Y };
        var toPt = new Win32.Point { X = (int)targetScreen.X, Y = (int)targetScreen.Y };
        var sentPress = Win32.PressAt(fromPt);
        await Frame(0.25f);
        Log("drop", $"按下那一发交了 {sentPress} 发，起手 _GetDragData={piece.GetCalls} " +
                    $"在拖={root.GuiIsDragging()}");

        const int legs = 10;
        var sentMove = 0u;
        for (var leg = 1; leg <= legs; leg++)
        {
            var at = new Win32.Point
            {
                X = (int)Math.Round(fromPt.X + (toPt.X - fromPt.X) * leg / (double)legs),
                Y = (int)Math.Round(fromPt.Y + (toPt.Y - fromPt.Y) * leg / (double)legs),
            };
            sentMove += Win32.MoveTo(at);
            await Frame(0.12f);
        }
        Log("drop", $"挪了 {legs} 格（共 {sentMove} 发）→ 在拖={root.GuiIsDragging()} " +
                    $"拖的东西={root.GuiGetDragData().VariantType} " +
                    $"源 _GetDragData={piece.GetCalls} 目标 _CanDropData={target.DropTargetCanCalls}");

        var sentUp = Win32.ReleaseAt(toPt);
        await Frame(0.6f);
        Log("drop", $"松开那一发交了 {sentUp} 发 → 在拖={root.GuiIsDragging()} " +
                    $"成功={root.GuiIsDragSuccessful()}");

        Log("drop", $"源侧：_GetDragData={piece.GetCalls} SetDragPreview={piece.PreviewCalls} " +
                    $"在拖={root.GuiIsDragging()} 成功={root.GuiIsDragSuccessful()}");
        Log("drop", $"**落侧**：_CanDropData={target.DropTargetCanCalls} _DropData={target.DropTargetDropCalls}");
        Log("drop", $"at_position：源 get=({piece.LastGetAt.X:F1},{piece.LastGetAt.Y:F1}) " +
                    $"目标 can=({target.LastTargetCanAt.X:F1},{target.LastTargetCanAt.Y:F1}) " +
                    $"drop=({target.LastTargetDropAt.X:F1},{target.LastTargetDropAt.Y:F1})");

        // ---- 对照也要配一条「一定该不通」的：拖完之后源自己那几发 ---------------
        var verdict = (clicks > 0 ? "量具成立" : "量具不成立（系统这一层收不到事件，下面全不作数）") +
                      $"；落侧 _CanDropData={target.DropTargetCanCalls} _DropData={target.DropTargetDropCalls}";
        Log("drop", $"判据汇总：{verdict}");

        win.QueueFree();
    }

    // ---- --canvas: 画布用真控件还是自绘（第八节 8.8 那一问）------------------------
    //
    // 「这一问不是引擎行为」是个错判，量两发就知道：
    //   —— `--drag` 和 `--drop` 那两发跑的画布**就是一个真 Control**
    //      （MouseFilter=Stop，子控件是真 Panel/Draggable），拖放从源到目标整趟走通。
    //   所以「真控件还是自绘」这个问法本身就不对：真控件和自绘**不是二选一**。
    //   剩下真要量的是：真控件画布上 `_Draw` 画的那一层，引擎重排一次还在不在。
    //
    // 这一档量那一件。判据是像素：自绘那一层在不在，靠数那一块区域上的像素读出来，
    // 不靠「我调过 _Draw 所以它在」—— 后者是代码在说话，不是屏幕在说话。

    private async Task CanvasMatrix()
    {
        Log("canvas",
            $"godot={Engine.GetVersionInfo()["string"]} headless={DisplayServer.GetName() == "headless"} " +
            $"屏={DisplayServer.ScreenGetSize()}");

        var win = new Window
        {
            Title = "keeper-canvasprobe",
            Position = new Vector2I(120, 100),
            Size = new Vector2I(700, 460),
        };
        await Frame(0.3f);
        GetTree().Root.AddChild(win);
        await Frame(0.8f);
        if (!win.IsInsideTree())
        {
            Log("canvas", "这扇窗没进树，下面每一行都不成立 —— 先修这一步");
            return;
        }

        // 画布 = 真 Control，上面自绘那几条线。子控件 = 真 Panel，位置由引擎摆。
        var canvas = new GuideProbe
        {
            Position = new Vector2(0, 0),
            Size = new Vector2(700, 460),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var kid = new Panel { Position = new Vector2(120, 120), Size = new Vector2(140, 70) };
        canvas.Edges = [kid.GetRect()];
        win.AddChild(canvas);
        canvas.AddChild(kid);
        await Frame(0.6f);

        Log("canvas", $"画布是 {canvas.GetType().Name}（{canvas.GetType().BaseType?.Name}），" +
                      $"mouse_filter={canvas.MouseFilter} 尺寸={canvas.Size} " +
                      $"子控件={kid.GetType().Name} 在真树上={canvas.IsInsideTree()}");

        // 自绘那一层画在哪：屏的中线。拿中线那一格当读数点 —— 那一格有没有自绘的颜色，
        // 就是「引擎重排之后 _Draw 画的那层还在不在」的答案。
        // 读的是**屏幕物理像素**（Frames.Shot），坐标要把这扇窗的位置加上。
        Frames.Sample ReadCentreLine() => Frames.Shot(new Win32.Point
        {
            X = (int)(canvas.Size.X / 2 + win.Position.X),
            Y = (int)(10 + win.Position.Y),
        });

        await Frame(0.5f);
        var before = ReadCentreLine();
        Log("canvas", $"引擎重排之前，中线上那一点：读到了={before.Read} 色={before.Pixel}");

        // 一次真的引擎重排：挪子控件。Control 换了尺寸/位置，引擎会重新排版。
        kid.Position = new Vector2(400, 260);
        canvas.QueueRedraw();
        await Frame(0.6f);
        var afterMove = ReadCentreLine();
        Log("canvas", $"挪子控件 + QueueRedraw 之后，同一点：读到了={afterMove.Read} 色={afterMove.Pixel} " +
                      $"(子控件现在在 {kid.GetRect()})");

        // 换画布自己的尺寸 —— 这一下引擎会重排这一整棵。
        canvas.Size = new Vector2(520, 320);
        await Frame(0.6f);
        canvas.QueueRedraw();
        await Frame(0.4f);
        var afterResize = ReadCentreLine();
        Log("canvas", $"换画布尺寸到 {canvas.Size} 之后，新的中线上那一点：读到了={afterResize.Read} 色={afterResize.Pixel}");

        // 不 QueueRedraw，看引擎自己排完版之后 _Draw 那层还在不在 ——
        // 编辑器里拖一个控件就是这一档：只有引擎重排，代码没主动要求重画。
        kid.Position = new Vector2(60, 60);
        await Frame(0.6f);
        var afterBare = ReadCentreLine();
        Log("canvas", $"只挪子控件、**不** QueueRedraw，同一点：读到了={afterBare.Read} 色={afterBare.Pixel}");

        // 判据：四行读数一起看。中线是自绘的白线（1,1,1），底色是深灰。
        Log("canvas", $"判据（中线 = 自绘的白线，底 = 深灰）：重排前={before.Pixel}、" +
                      $"挪控件后={afterMove.Pixel}、换尺寸后={afterResize.Pixel}、" +
                      $"不主动重画={afterBare.Pixel}");

        win.QueueFree();
    }

    // ---- --push: 进程内喂进去的一发点击，命中测试收不收 -------------------------
    //
    // The desktop calendar's mouse bridge hangs on this: a window parented into
    // SHELLDLL_DefView gets no mouse messages across the process boundary, so
    // the design doc (docs/design/hit-testing.md) says to take them off our own WH_MOUSE_LL hook and push them
    // into the window. The 2026-09-25 attempt got the event into the window and
    // never hit a control, and did not say why. This row is therefore about the
    // shape of the event we hand over, field by field, with no OS input at all.

    /// 一颗钮的三只计数器。用信号而不是重写 `_GuiInput`：C# 里 `base._GuiInput()` 走的是
    /// `Control` 那一份实现，`Button` 自己判断「点在我身上没有」那一段就不会跑了 —— 第一版
    /// 这么写，实测结果是全行 `Pressed=0`，量的是自己的坑不是引擎的。
    private sealed class ClickCounter
    {
        public int Gui;
        public int Fired;
        public int Down;
        public int Up;
        public int Hover;

        public ClickCounter(Button button)
        {
            button.GuiInput += @event =>
            {
                Gui++;
                if (@event is not InputEventMouseButton b)
                {
                    // 触屏那一型不是鼠标事件，也得看得见送到没送到。
                    Note($"事件到控件 {@event.GetType().Name} " +
                         $"Pressed={(@event as InputEventScreenTouch)?.Pressed}");
                    return;
                }
                // 引擎那道判断用的是 get_button_rect()：父节点不是容器时它把控件自己的
                // position 也加进尺寸里。IsPressed() 单列出来，是因为 Button 走的是那一个。
                var rect = new Rect2(Vector2.Zero, button.Size + button.Position);
                Note($"事件到控件 pos=({b.Position.X:F0},{b.Position.Y:F0}) " +
                     $"global=({b.GlobalPosition.X:F0},{b.GlobalPosition.Y:F0}) index={b.ButtonIndex} " +
                     $"mask={b.ButtonMask} Pressed属性={b.Pressed} IsPressed()={b.IsPressed()} " +
                     $"局部rect={rect} 点在我身上={rect.HasPoint(b.Position)}");
            };
            button.ButtonDown += () => Down++;
            button.ButtonUp += () => Up++;
            button.MouseEntered += () => Hover++;
            button.Pressed += () => Fired++;
        }

        public Action<string> Note = _ => { };

        public void Reset() { Gui = 0; Fired = 0; Down = 0; Up = 0; Hover = 0; }

        public string Line() => $"gui_input={Gui} down={Down} up={Up} pressed={Fired} entered={Hover}";
    }

    /// 只数帧的一发节点：验「代码里现造出来的子窗，它的 _Process 跑不跑」。
    private sealed partial class TickNode : Node
    {
        public int Ticks;

        public override void _Process(double delta) => Ticks++;
    }

    private async Task PushMatrix()
    {
        Log("push",
            $"headless={DisplayServer.GetName() == "headless"} " +
            $"embed_subwindows={ProjectSettings.GetSetting("display/window/subwindows/embed_subwindows")}");

        var win = new Window
        {
            Title = "keeper-pushprobe",
            Position = new Vector2I(160, 160),
            Size = new Vector2I(320, 200),
        };
        var button = new Button { Text = "点我", Position = new Vector2(60, 80), Size = new Vector2(160, 44) };
        var count = new ClickCounter(button) { Note = s => Log("push", $"子窗 {s}") };
        win.AddChild(button);
        // 等一帧再挂：Root 这会儿还在 _Ready 里排队装子节点，这一步当场挂会被拒
        // （add_child failed / Parent node is busy），那之后每一发 push 都是
        // !is_inside_tree()，整张表读出来全是「没点中」。第一跑就是这么废的。
        await Frame(0.3f);
        GetTree().Root.AddChild(win);
        await Frame(0.6f);
        if (!win.IsInsideTree())
        {
            Log("push", "这扇窗没进树，下面每一行都不成立 —— 先修这一步");
            return;
        }
        // 抬头那一行每秒一刷（src/Options.cs 的 _Process）靠的是「代码里现造的子窗，
        // 它的 _Process 也跑」这一件事。这里量的是同一件事的另一半。
        var ticks = new TickNode();
        win.AddChild(ticks);
        var tickStart = Time.GetTicksMsec();

        var local = button.GetGlobalRect().GetCenter();
        var viaPosition = new Vector2(win.Position.X + local.X, win.Position.Y + local.Y);
        var viaVisible = win.GetVisibleRect().Position + local;
        Log("push",
            $"窗口 position={win.Position} visible_rect={win.GetVisibleRect()} 钮全局={button.GetGlobalRect()} " +
            $"屏幕点两种算法=({viaPosition.X:F0},{viaPosition.Y:F0})/({viaVisible.X:F0},{viaVisible.Y:F0}) " +
            $"焦点={win.HasFocus()} 可见={win.IsVisible()} 不可拿焦点={win.Unfocusable}");
        // `_gui_call_input` 里那句 can_process() 是唯一能解释「事件送到了控件、控件自己的
        // 反应没跑」的一道闸。这三实测结果决定下面那两行该往哪儿拧。
        Log("push",
            $"can_process 钮={button.CanProcess()} 窗={win.CanProcess()} 主窗={GetWindow().CanProcess()} " +
            $"树里可见={button.IsVisibleInTree()} 暂停={GetTree().Paused}");

        await Try("对照:直接发信号", win, local, local, true, false, false, false, 0, button, count, emitOnly: true);
        await Try("上次那一发", win, local, local, true, false, false, false, 0, button, count);
        await Try("加 ButtonIndex", win, local, local, true, true, false, false, 0, button, count);
        await Try("加 ButtonMask", win, local, local, true, true, true, false, 0, button, count);
        await Try("先送一次移动", win, local, local, true, true, true, true, 0, button, count);
        await Try("按下松开分两帧", win, local, local, true, true, true, true, 0, button, count, releaseNextFrame: true);
        await Try("屏幕坐标口径", win, viaVisible, viaVisible, false, true, true, false, 0, button, count);
        // 要是那道闸就是原因，把它拧开就该点得动了。
        button.ProcessMode = Node.ProcessModeEnum.Always;
        await Try("钮 can_process 拧开", win, local, local, true, true, true, false, 0, button, count);
        button.ProcessMode = Node.ProcessModeEnum.Inherit;
        win.ProcessMode = Node.ProcessModeEnum.Always;
        await Try("窗 can_process 拧开", win, local, local, true, true, true, false, 0, button, count);
        win.ProcessMode = Node.ProcessModeEnum.Inherit;
        await Try("走根视口", GetTree().Root, viaVisible, viaVisible, false, true, true, false, 0, button, count);
        await Try("Device=1", win, local, viaVisible, true, true, true, false, 1, button, count);
        await Try("Device=-1 模拟", win, local, viaVisible, true, true, true, false, -1, button, count);
        await Try("触屏事件那一型", win, local, viaVisible, true, true, true, false, 0, button, count, touch: true);
        await Try("钮的局部原点", win, new Vector2(0, 0), viaVisible, true, true, true, false, 0, button, count);

        // 同一套喂法换到引擎自己开的那扇主窗上：主窗一定真进了树、真在屏幕上，
        // 所以这一列把「PushInput 这条路本身走不走得通」和「代码里现造的子窗算不算一扇窗」
        // 分开。上一轮测的是设置窗，那也是代码里 AddChild 进来的子窗。
        _fill = GetNodeOrNull<ColorRect>("Layer/Fill");   // --push 这一跑没走 Baseline，自己取
        var onMain = new Button { Text = "点我", Position = new Vector2(40, 40), Size = new Vector2(160, 44) };
        var mainCount = new ClickCounter(onMain) { Note = s => Log("push", $"主窗 {s}") };
        GetWindow().AddChild(onMain);
        await Frame(0.4f);
        var mainLocal = onMain.GetGlobalRect().GetCenter();
        var mainScreen = GetWindow().GetVisibleRect().Position + mainLocal;
        Log("push",
            $"主窗 visible_rect={GetWindow().GetVisibleRect()} 钮全局={onMain.GetGlobalRect()} " +
            $"进树={onMain.IsInsideTree()} 同一个树={onMain.GetTree() == GetTree()} " +
            $"盖在上面的 Fill={(_fill == null ? "没有" : _fill.Name + " " + _fill.GetRect() + " mouse_filter=" + _fill.MouseFilter)}");
        await Try("主窗:齐字段", GetWindow(), mainLocal, mainScreen, true, true, true, false, 0, onMain, mainCount);
        if (_fill != null) _fill.MouseFilter = Control.MouseFilterEnum.Ignore;
        await Frame(0.2f);
        await Try("主窗:让开Fill", GetWindow(), mainLocal, mainScreen, true, true, true, false, 0, onMain, mainCount);
        await Try("主窗:屏幕口径", GetWindow(), mainScreen, mainScreen, false, true, true, false, 0, onMain, mainCount);

        await Try("主窗:触屏那一型", GetWindow(), mainLocal, mainScreen, true, true, true, false, 0, onMain, mainCount, touch: true);

        // 日历那一型：钮挂在中间一层 Control 底下（日历的格子就是挂在 _root 上），并且
        // ClickCounter 会把每一件事件的身份打出来。2026-09-26 产品那一跑读出
        // 「喂进去一发、ButtonUp 发了两次」，这里要分清第二发是触屏还是引擎
        // emulate_mouse_from_touch 重映射出来的那份鼠标事件 —— 判双击该数哪一个，就看这一行。
        var nested = new Control { Position = new Vector2(20, 20), Size = new Vector2(220, 90) };
        var nestedButton = new Button { Text = "套一层", Position = new Vector2(20, 16), Size = new Vector2(160, 44) };
        nested.AddChild(nestedButton);
        win.AddChild(nested);
        await Frame(0.3f);
        var nestedLocal = nestedButton.GetGlobalRect().GetCenter();
        var nestedCount = new ClickCounter(nestedButton) { Note = s => Log("push", $"套一层 {s}") };
        await Try("套一层:触屏那一型", win, nestedLocal, viaVisible, true, true, true, false, 0,
            nestedButton, nestedCount, touch: true);
        await Try("套一层:鼠标那一型", win, nestedLocal, viaVisible, true, true, true, false, 0,
            nestedButton, nestedCount);

        // 日历那种窗上真正要用的控件是复选框，所以这一型也得在它身上验一次，
        // 并且反着验：鼠标那一型喂给它是不是同样不动。
        var box = new CheckBox { Position = new Vector2(60, 20), Size = new Vector2(160, 24) };
        win.AddChild(box);
        await Frame(0.3f);
        var boxLocal = box.GetGlobalRect().GetCenter();
        var toggled = 0;
        box.Toggled += on => toggled++;
        var bp = new Vector2I((int)boxLocal.X, (int)boxLocal.Y);
        win.PushInput(new InputEventScreenTouch { Position = bp, Pressed = true }, true);
        win.PushInput(new InputEventScreenTouch { Position = bp, Pressed = false }, true);
        await Frame(0.2f);
        Log("push", $"复选框 喂触屏：Toggled={toggled} 现在={box.ButtonPressed}");
        toggled = 0;
        win.PushInput(new InputEventMouseButton
        {
            Position = boxLocal, GlobalPosition = viaVisible, Pressed = true,
            ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left
        }, true);
        win.PushInput(new InputEventMouseButton
        {
            Position = boxLocal, GlobalPosition = viaVisible, Pressed = false,
            ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left
        }, true);
        await Frame(0.2f);
        Log("push", $"复选框 喂鼠标：Toggled={toggled} 现在={box.ButtonPressed}");

        Log("push",
            $"子窗 _Process：{ticks.Ticks} 帧 / {Time.GetTicksMsec() - tickStart} 毫秒 " +
            $"{(ticks.Ticks > 0 ? "跑了" : "没跑")}");
        await PushThreeShapes(win, viaVisible);
        await WallpaperHitTest(win, ticks);
        win.QueueFree();
    }

    /// <summary>#8 那三型：设置窗里用得最多、而 <c>--push</c> 从来没往它们身上喂过事件的三型
    /// 控件（<c>ScrollContainer</c> / <c>LineEdit</c> / <c>OptionButton</c>），加上一发 hover。
    ///
    /// 判据不是「值变了」，是<b>那一型控件自己报没报到事</b>：LineEdit 报焦点、OptionButton 报
    /// 弹出、ScrollContainer 报滚动、Button 报 <c>mouse_entered</c>。这四条在 2026-09-27 之前
    /// 全是空白 —— 休息流程那一区「点得动」那一半欠的就是这一发（那一区的判据是发信号驱动的，
    /// 自检里 <c>flows</c> 那一组，它证的是接线，不是命中）。
    ///
    /// 弹层里那一发不算在这一发里：OptionButton 的弹层是<b>另一扇窗</b>，命中在它自己的窗口里做，
    /// 所以这里只报「点它，弹不弹」，选到某一项那一条走的是真鼠标那一发（App.tscn 里点设置窗）。</summary>
    private async Task PushThreeShapes(Window win, Vector2 viaVisible)
    {
        // ---- LineEdit：点它，焦点该进去，字该打得进 ------------------------------------
        var edit = new LineEdit { Position = new Vector2(20, 150), Size = new Vector2(200, 28) };
        win.AddChild(edit);
        await Frame(0.3f);
        var focused = 0;
        edit.FocusEntered += () => focused++;
        await FeedClick(win, edit, viaVisible);
        Log("push", $"LineEdit 喂鼠标：FocusEntered={focused} 现在有焦点={edit.HasFocus()} " +
                    $" caret 在={edit.CaretColumn} 读回来='{edit.Text}'");

        // ---- OptionButton：点它，弹层该开 ----------------------------------------------
        var opt = new OptionButton { Position = new Vector2(20, 186), Size = new Vector2(200, 28) };
        opt.AddItem("甲"); opt.AddItem("乙");
        win.AddChild(opt);
        await Frame(0.3f);
        var opened = 0;
        opt.ItemSelected += _ => opened++;
        var before = opt.Selected;
        await FeedClick(win, opt, viaVisible);
        await Frame(0.2f);
        Log("push", $"OptionButton 喂鼠标：弹层可见={opt.GetPopup().Visible} 选中={before}→{opt.Selected} " +
                    $"ItemSelected 发了={opened}（弹层是另一扇窗，选一项那一条走真鼠标）");
        opt.GetPopup().Hide();

        // ---- ScrollContainer：滚轮该滚，套在里面的控件按**滚过之后**的位置命中 ----------
        var scroll = new ScrollContainer
        {
            Position = new Vector2(240, 150), Size = new Vector2(140, 70),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var tall = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (var i = 0; i < 12; i++)
            tall.AddChild(new Button { Text = $"第 {i} 行", CustomMinimumSize = new Vector2(120, 34) });
        scroll.AddChild(tall);
        win.AddChild(scroll);
        await Frame(0.3f);
        var first = (Button)tall.GetChild(0);
        var last = (Button)tall.GetChild(11);
        var hitLast = 0;
        last.Pressed += () => hitLast++;
        for (var i = 0; i < 6; i++)
        {
            // Godot 4 的滚轮没有 factor：一发就是 ButtonIndex=WheelDown 的按下+松开。
            win.PushInput(new InputEventMouseButton
            {
                Position = first.GetGlobalRect().GetCenter(), GlobalPosition = viaVisible,
                ButtonIndex = MouseButton.WheelDown, Pressed = true
            }, true);
            win.PushInput(new InputEventMouseButton
            {
                Position = first.GetGlobalRect().GetCenter(), GlobalPosition = viaVisible,
                ButtonIndex = MouseButton.WheelDown, Pressed = false
            }, true);
        }
        await Frame(0.3f);
        var scrolled = scroll.ScrollVertical;
        // 滚过之后那一颗在不在这一扇窗的可见范围里：不在就别喂，喂了是点别的东西。
        var rect = last.GetGlobalRect();
        var visible = scroll.GetGlobalRect().Encloses(rect);
        await FeedClick(win, last, viaVisible);
        Log("push", $"ScrollContainer 喂滚轮：ScrollVertical={scrolled} 末那颗在框里={visible} " +
                    $"按它 Pressed={hitLast}（在框里才该是 1）");

        // ---- hover：喂一发移动，mouse_entered 该报 --------------------------------------
        var hover = new Button { Text = "hover", Position = new Vector2(20, 226), Size = new Vector2(120, 30) };
        win.AddChild(hover);
        await Frame(0.3f);
        var entered = 0;
        var exited = 0;
        hover.MouseEntered += () => entered++;
        hover.MouseExited += () => exited++;
        var at = hover.GetGlobalRect().GetCenter();
        // 两型都喂：引擎自己那两型（motion 带 mask vs 不带）在这一发上分开报，
        // 2026-09-26 那一条「hover 两型不产生 mouse_entered」说的就是这个差。
        win.PushInput(new InputEventMouseMotion
        {
            Position = at, GlobalPosition = viaVisible, ButtonMask = default
        }, true);
        await Frame(0.2f);
        Log("push", $"hover 裸移动：mouse_entered={entered} mouse_exited={exited} 悬着={hover.IsHovered()}");
        win.PushInput(new InputEventMouseMotion
        {
            Position = at, GlobalPosition = viaVisible, ButtonMask = MouseButtonMask.Left
        }, true);
        await Frame(0.2f);
        Log("push", $"hover 带左键掩码：mouse_entered={entered} mouse_exited={exited} 悬着={hover.IsHovered()}");
    }

    /// 往一颗控件的正中喂一发按下+松开（先喂一发移动，让命中测试先知道鼠标在那儿）。
    private async Task FeedClick(Window win, Control target, Vector2 viaVisible)
    {
        var at = target.GetGlobalRect().GetCenter();
        win.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = viaVisible }, true);
        await Frame(0.05f);
        win.PushInput(new InputEventMouseButton
        {
            Position = at, GlobalPosition = viaVisible, Pressed = true,
            ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left
        }, true);
        win.PushInput(new InputEventMouseButton
        {
            Position = at, GlobalPosition = viaVisible, Pressed = false,
            ButtonIndex = MouseButton.Left, ButtonMask = default
        }, true);
        await Frame(0.1f);
    }

    /// <summary>日历那条前提从来没量成过：一扇嵌进壁纸的窗，它自己那块地方归不归它。
    /// 这一发不问消息投不投得到（那要真的鼠标），只问命中测试 —— 归这扇窗或归壁纸层，
    /// 那条桥才谈得上开闸；两者都不归，桥从前提上就是多余的。
    ///
    /// 顺序是**先嵌进去、再清桌面**。反过来的话显示桌面那一下先把测量程序自己最小化了
    /// （嵌进去之前它还是一扇普通 top-level），后面每一发等帧都饿在最小化的窗上 ——
    /// 「等桌面空出来」那句话，此前就是这么把自己绕死的。嵌进去之后也不用找露天像素:
    /// 窗自己占的那块矩形，桌面一清就必然露天到壁纸层。
    ///
    /// 清桌面走 <see cref="Win32.ShowDesktop"/>（不注入按键，所以不过 Keeper 自己那条键盘钩子），
    /// 而且要另带 <c>--sweep</c> 才跑：那一发最小化的是别人家的窗，所以名单先印出来
    /// （<see cref="DesktopLayer.OverRect"/>），不旗不带就一步都不动桌面。还原有两条路 —— finally
    /// 里一条，加一条不骑在帧上的兜底线程，因为「这扇窗在这期间还出不出帧」正是被问的东西，
    /// 不能拿它自己的帧当还原的前提。全程只动样式和自己的小窗，最后把窗放回原处。</summary>
    private async Task WallpaperHitTest(Window win, TickNode ticks)
    {
        var hwnd = FindOwnWindow(win.Title);
        // 这一发要 SetParent 进 DefView，所以同样要 DefView 本身而不是 Find() 挑出来的那个爹：
        // 挂进 Progman 是另一件实验，实测结果说的就不是「挂在壁纸那一层」了。
        var defview = DesktopLayer.Look().DefView;
        if (hwnd == 0 || defview == 0)
        {
            Log("wallpaper-hit", $"量不成：hwnd=0x{hwnd:X} defview=0x{defview:X}");
            return;
        }

        Win32.GetWindowRect(hwnd, out var was);
        // 落在屏幕左上半。这里不需要一块空地：桌面清过之后窗自己占的那块矩形就露天到壁纸层,
        // 所以不逐像素赌运气去找（那是上一版的判据,桌面被盖满时它只能报量不成）。
        var spot = new Win32.Point { X = was.Width / 2 + 60, Y = was.Height / 2 + 60 };
        Win32.MoveWindow(hwnd, spot.X - was.Width / 2, spot.Y - was.Height / 2, was.Width, was.Height, true);
        await Frame(0.4f);
        Win32.GetWindowRect(hwnd, out var placed);
        // 教训那条要求的「一定该通」的对照：一扇普通 top-level 刚挪完,自己矩形中心必然归它,
        // 桌面乱不乱都不影响这一发成立。它不成立就是测量程序自己坏了,下面全部作废。
        SamplePoints("嵌进去之前", placed);
        OwnsThePoint("对照:作为普通窗", hwnd, placed);
        // 显示桌面那一下动的是别人家的窗，所以先把名字报出来再决定动不动 —— 这一行就是那份名单。
        Log("wallpaper-hit",
            $"压在我们这块矩形上、会被扫的窗（那一发扫的是整张桌面，不止这些）：{DesktopLayer.OverRect(placed)}");
        if (!_wantsSweep) Log("wallpaper-hit", "没带 --sweep，桌面那一半一步都不动（嵌进去那几发照读）");

        var styleBefore = (long)Win32.GetWindowLong(hwnd, Win32.GwlStyle);
        var exBefore = (long)Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
        Win32.SetParent(hwnd, defview);
        Win32.SetWindowLong(hwnd, Win32.GwlStyle,
            (nint)((styleBefore & ~Win32.WsPopup) | Win32.WsChild | Win32.WsVisible));
        Win32.GetWindowRect(defview, out var dv);
        // 子窗的坐标是爹的坐标：挪同样的像素数，屏幕位置不动。
        Win32.MoveWindow(hwnd, placed.Left - dv.Left, placed.Top - dv.Top, placed.Width, placed.Height, true);
        await Frame(0.4f);

        // 还原不能只写在 finally 里：这一发要问的恰恰是「这扇窗在这期间还出不出帧」，而 finally
        // 往后每一发都骑在帧上 —— 帧真停了就到不了 finally，他的桌面留在最小化状态。
        // 所以另开一条不在帧上的线程，到点自己还；正常路径在 finally 里抢在它前面，两边只还一次。
        var restored = 0;
        var minimized = false;
        List<(nint Hwnd, string Name, bool Iconic)>? deskBefore = null;
        using var deadline = new System.Threading.Timer(_ =>
        {
            if (System.Threading.Interlocked.Exchange(ref restored, 1) == 0) Win32.ShowDesktop(true);
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        try
        {
            Win32.GetWindowRect(hwnd, out var now);
            var styleNow = Win32.GetWindowLong(hwnd, Win32.GwlStyle);
            Log("wallpaper-hit",
                $"parent=0x{Win32.GetParent(hwnd):X}/{Win32.ClassName(Win32.GetParent(hwnd))} " +
                $"可见={Win32.IsWindowVisible(hwnd)} 样式={Win32.DescribeStyle(styleNow)} " +
                $"defview={dv} 现在={now} 屏幕位置没动={now.Left == placed.Left && now.Top == placed.Top}");
            // 这一发读的是「桌面上还压着别的窗时」，而且它自己就是一条要的行为：被 app 窗盖住时
            // 那一点该归那个 app 窗 —— 日历的桥在这种时候本来就该闭嘴。
            OwnsThePoint("进去之后（桌面上还压着别的窗）", hwnd, now);

            // ---- 显示桌面：设计文档（docs/design/calendar.md）那一族的正身 ----
            if (!_wantsSweep)
            {
                Log("wallpaper-hit", "没带 --sweep，桌面那一半整块没跑（上面那几发已经读到）");
                return;
            }
            Log("wallpaper-hit", $"扫之前我们自己的顶层窗：{OwnTopLevelState()}");
            deskBefore = DesktopLayer.VisibleTopLevelWindows();
            minimized = Win32.ShowDesktop(false);
            if (!minimized)
            {
                Log("wallpaper-hit", "Shell.Application 那一发没成，桌面这一半量不成（桌面没动过）");
                return;
            }
            deadline.Change(15000, System.Threading.Timeout.Infinite);
            // 阻塞等 shell 扫完，而不是等帧：这会儿我们自己的主窗也被最小化了，
            // 拿自己的帧去等别人的动画，等的东西本身可疑。
            System.Threading.Thread.Sleep(900);
            Log("wallpaper-hit", $"扫之后我们自己的顶层窗：{OwnTopLevelState()}");

            Win32.GetWindowRect(hwnd, out var shown);
            // 「扫没扫走」问最小化态，不问可见性和矩形：上一跑证明那两样说不清这件事
            // —— 一扇真被扫走的窗，矩形读起来仍在原处、IsWindowVisible 仍是 true。
            Log("wallpaper-hit",
                $"清过桌面：可见={Win32.IsWindowVisible(hwnd)} 最小化={Win32.IsIconic(hwnd)} " +
                $"矩形=({shown.Left},{shown.Top}) " +
                $"位置没动={shown.Left == now.Left && shown.Top == now.Top && shown.Width == now.Width}");
            SamplePoints("显示桌面之后", shown);
            OwnsThePoint("嵌进壁纸 + 显示桌面", hwnd, shown);

            // 日历得自己走：这一发问的是嵌进壁纸 + 桌面亮着的时候，这扇窗还出不出帧。
            // 等不到帧是一个实测结果不是故障，所以拿一发不骑在帧上的超时兜着。
            var tickBefore = ticks.Ticks;
            var msBefore = Time.GetTicksMsec();
            var framed = Frame(1.2f);
            var gotFrame = await Task.WhenAny(framed, Task.Delay(4000)) == framed;
            Log("wallpaper-hit",
                $"{(gotFrame ? "等得到帧" : "4 秒没等到一帧")}：帧数 {tickBefore}→{ticks.Ticks} / " +
                $"{Time.GetTicksMsec() - msBefore} 毫秒 " +
                $"（{(ticks.Ticks > tickBefore ? "还在跑" : "没跑 —— 日历在这种状态下不会自己走")}）");
        }
        finally
        {
            // 排在最前面：哪怕上面每一发都坏了，桌面也得先还回去。抢得到旗才叫这一发；
            // 抢不到说明那条兜底线程已经叫过了 —— 那正是「主循在这中间停过」的实测结果。
            if (minimized)
            {
                deadline.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                if (System.Threading.Interlocked.Exchange(ref restored, 1) == 0)
                {
                    if (!Win32.ShowDesktop(true))
                        Log("wallpaper-hit", "还原这一发没成 —— 桌上那些窗还最小化着，点一下任务栏或按一次 Win+D");
                }
                else Log("wallpaper-hit", "还原已经被兜底那条线程叫过 —— 帧在这一发中间停过，上面那句帧数作废");
                if (deskBefore != null)
                {
                    // 还原那一发是异步的：2026-09-26 第一跑对账在 ShowDesktop(true) 返回后 10 毫秒
                    // 就拍了，读出「仍最小化 2 扇」，一分钟后去看那两扇早就回来了 —— 拍早了不是
                    // shell 丢了窗。扫的时候等 900 毫秒，还回来这边同样得等。不等帧，理由同上：
                    // 这时候我们自己的主窗可能还在最小化那一态里。
                    System.Threading.Thread.Sleep(1500);
                    Log("wallpaper-hit", $"还原对账：{DesktopLayer.Reconcile(deskBefore)}");
                }
            }
            Win32.SetParent(hwnd, IntPtr.Zero);
            Win32.SetWindowLong(hwnd, Win32.GwlStyle, (nint)styleBefore);
            Win32.SetWindowLong(hwnd, Win32.GwlExStyle, (nint)exBefore);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, was.Left, was.Top, was.Width, was.Height,
                Win32.SwpFrameChanged | Win32.SwpNoZorder);
            Win32.GetWindowRect(hwnd, out var back);
            Log("wallpaper-hit",
                $"放回原处：回到 ({was.Left},{was.Top}) 现在是 ({back.Left},{back.Top}) " +
                $"一样={back.Left == was.Left && back.Top == was.Top}");
        }
    }

    // VisibleTopLevels / Reconcile / SweepPreflight 三份都收进 DesktopLayer 了（2026-09-29）。
    // 三份跟正本逐字同形状，只差名字和注释：VisibleTopLevelWindows / Reconcile / OverRect。
    // 正本那边自己的注释早就写着「哪天要合，合到这一份来」—— 那一天是今天。
    // 删除测试正着问也过：把这三份删掉，复杂度不会在调用方长回来，因为正本就在同一个进程里、
    // 同一个文件里已经被调用了（上面 :786 那一条还原对账走的一直是 DesktopLayer.Reconcile）。

    /// 「扫没扫」不能只读我们这扇窗还在不在：桌面本来就空的时候,什么都没被扫、和扫了但这扇
    /// 没动,印出来是同一行。所以这一发读的是**我们自己那些普通顶层窗** —— 它们一定该被最小化。
    /// 它们没动,就说明那一发动词根本没落地,上面每句「没被扫走」都不算数。
    private static string OwnTopLevelState()
    {
        var bits = new List<string>();
        foreach (var hwnd in Win32.OwnTopLevelWindows())
        {
            Win32.GetWindowRect(hwnd, out var r);
            bits.Add($"'{Win32.WindowText(hwnd)}'/{Win32.ClassName(hwnd)} 可见={Win32.IsWindowVisible(hwnd)} " +
                     $"最小化={Win32.IsIconic(hwnd)} 矩形=({r.Left},{r.Top})");
        }
        return bits.Count == 0 ? "一扇普通顶层窗都没有" : string.Join("；", bits);
    }

    /// 那一点的归属分三档报，不按「是不是我这扇窗」两档判死。桥的闸门真正要问的是
    /// 「这一点上有没有 app 窗挡着」，不是「Windows 把壁纸层那一点判给哪一个 HWND」——
    /// 归这扇窗、或归壁纸层，那条桥的前提都成立；只有清过桌面之后仍然归某个 app 窗才算不通。
    private static void OwnsThePoint(string when, nint hwnd, Win32.Rect rect)
    {
        var pt = new Win32.Point { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 };
        var hit = Win32.WindowFromPoint(pt);
        var root = Win32.GetAncestorRoot(hit);
        var rootClass = Win32.ClassName(root);
        var tier = hit == hwnd || root == hwnd ? "归我这扇窗"
            : DesktopLayer.IsWallpaperLayer(rootClass) ? "归壁纸层"
            : $"归 app 窗 '{rootClass}'";
        Log("wallpaper-hit",
            $"{when}：点 ({pt.X},{pt.Y}) 归 0x{hit:X}/{Win32.ClassName(hit)}，" +
            $"根是 0x{root:X}/{rootClass} → {tier}");
    }

    /// 「桌面真的清干净了」不能只信那一发动词：同一批点各采一遍，前后归属换了才算 shell 动过手。
    /// 也防住另一种假失败 —— 窗其实一直在，只是没人挡着它时读到的是壁纸层，那不该被当成扫走了。
    private static void SamplePoints(string when, Win32.Rect own)
    {
        var seen = new List<string>();
        foreach (var (x, y) in new[] { (400, 400), (900, 700), (1500, 500), (2000, 900), (700, 1200) })
        {
            var root = Win32.GetAncestorRoot(Win32.WindowFromPoint(new Win32.Point { X = x, Y = y }));
            seen.Add($"({x},{y})→'{Win32.ClassName(root)}'");
        }
        Log("wallpaper-hit", $"{when} 桌面采样：{string.Join(" ", seen)}（我们的窗在 {own}）");
    }

    // SweepPreflight 收进 DesktopLayer.OverRect 了（2026-09-29），两份逐字相同。
    // 原来这一份头上的几句分寸跟着搬过来了，别丢：
    //   显示桌面那一下动的是别人家的窗，所以先把名单报出来再动手。**这一份只列压在我们这块
    //   矩形上的**：那一发扫的是整张桌面，名单比实际的影响范围小，别把它读成「只会碰到这几扇」。
    //   判据只有 IsWindowVisible，所以 UWP 那种被 shell 藏起来的窗会算多，而本来就已经
    //   最小化掉的窗不会出现在这里 —— 它们被还原那一下叫醒，才是这条动词真正的副作用。

    private async Task Try(string name, Viewport target, Vector2 pos, Vector2 global, bool localCoords,
        bool withIndex, bool withMask, bool motionFirst, int device, Button button,
        ClickCounter count, bool emitOnly = false, bool releaseNextFrame = false,
        bool touch = false)
    {
        count.Reset();

        if (emitOnly)
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }
        else
        {
            if (motionFirst)
                target.PushInput(new InputEventMouseMotion
                { Position = pos, GlobalPosition = global, Device = device }, localCoords);
            foreach (var pressed in new[] { true, false })
            {
                InputEvent click = touch
                    ? new InputEventScreenTouch
                      { Position = new Vector2I((int)pos.X, (int)pos.Y), Pressed = pressed }
                    : new InputEventMouseButton
                      { Position = pos, GlobalPosition = global, Pressed = pressed, Device = device };
                if (click is InputEventMouseButton mb)
                {
                    if (withIndex) mb.ButtonIndex = MouseButton.Left;
                    if (withMask) mb.ButtonMask = MouseButtonMask.Left;
                }
                target.PushInput(click, localCoords);
                if (pressed && releaseNextFrame) await Frame(0.1f);
            }
        }

        await Frame(0.2f);
        Log("push",
            $"{name,-18} {count.Line()} 悬着={button.IsHovered()} → " +
            $"{(count.Fired > 0 ? "点中了" : "没点中")}");
    }

    // ---- measurement helpers ------------------------------------------------

    private void SetTransparentBit(bool on)
    {
        var ex = (long)Win32.GetWindowLong(_hwnd, Win32.GwlExStyle);
        ex = on ? ex | Win32.WsExTransparent | Win32.WsExLayered
                : ex & ~Win32.WsExTransparent & ~Win32.WsExLayered;
        Win32.SetWindowLong(_hwnd, Win32.GwlExStyle, (nint)ex);
    }

    private void ReportWindow(string step, string note, bool withAbove = false)
    {
        var style = Win32.GetWindowLong(_hwnd, Win32.GwlStyle);
        var ex = Win32.GetWindowLong(_hwnd, Win32.GwlExStyle);
        Win32.GetWindowRect(_hwnd, out var rect);
        var parent = Win32.GetParent(_hwnd);

        Log(step,
            $"{note} | godot_pos=({_win.Position.X},{_win.Position.Y}) " +
            $"godot_size={_win.Size.X}x{_win.Size.Y} | win32_rect={rect} " +
            $"style=0x{(long)style:X}[{Win32.DescribeStyle(style)}] " +
            $"exstyle=0x{(long)ex:X}[{Win32.DescribeExStyle(ex)}] " +
            $"parent=0x{parent:X}/{Win32.ClassName(parent)} dpi={Win32.GetDpiForWindow(_hwnd)}");

        if (withAbove) Log(step, Win32.Above(_hwnd));
    }

    /// Screen geometry, so the DPI question in the plan gets a number.
    private void LogGeometry()
    {
        var screen = DisplayServer.ScreenGetSize();
        var work = DisplayServer.ScreenGetUsableRect();
        // 这里原来写的是 1，注释写着 MONITOR_DEFAULTTONEAREST —— 1 是 DEFAULTTOPRIMARY，
        // 2 才是 nearest。这台机器一块屏，两发实测结果一样，所以那次 DPI 的实测结果没被它带偏；
        // 但拿到副屏上，「我这扇窗在哪块屏上」就会答成主屏。
        var mon = Win32.MonitorFromWindow(_hwnd, Win32.MonitorDefaultToNearest);
        var info = new Win32.MonitorInfo { CbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
        Win32.GetMonitorInfo(mon, ref info);
        Log("geometry",
            $"screen_godot={screen.X}x{screen.Y} scale={DisplayServer.ScreenGetScale()} " +
            $"work_godot=({work.Position.X},{work.Position.Y}){work.Size} " +
            $"monitor_win32={info.Monitor} work_win32={info.Work}");
    }

    /// Does a click at a point inside our own window reach it? True means the
    /// window owns its own middle, which is the state before any passthrough —
    /// so passthrough working is this returning False. Every caller that turns
    /// measurement results into a verdict has to hold that inversion.
    private bool HitTestOwnWindow(string step)
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        var points = new[]
        {
            new Win32.Point { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 },
            new Win32.Point { X = rect.Left + 12, Y = rect.Top + 12 },
            new Win32.Point { X = rect.Right - 12, Y = rect.Bottom - 12 },
        };

        var ours = 0;
        foreach (var p in points)
        {
            var hit = Win32.WindowFromPoint(p);
            var root = hit == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestorRoot(hit);
            var isOurs = root == _hwnd;
            if (isOurs) ours++;
            Log(step,
                $"click_at=({p.X},{p.Y}) hits=0x{hit:X} class='{Win32.ClassName(hit)}' " +
                $"root=0x{root:X}/{Win32.ClassName(root)} is_ours={isOurs}");
        }
        return ours == points.Length;
    }

    private nint FindOwnWindowByTitle() => FindOwnWindow(Title);

    /// The plan says the window is findable by process id; what the title is set
    /// to by then is a separate question, so try the class too.
    private nint FindOwnWindowByClass() => FindOwnWindow(null);

    private nint FindOwnWindow(string? wantedTitle)
    {
        var found = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)System.Environment.ProcessId) return true;
            if (wantedTitle == null)
            {
                found = hwnd;
                return false;
            }
            if (WindowTitleOf(hwnd) != wantedTitle) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string WindowTitleOf(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // FindWorkerWSibling 搬去 DesktopLayer.WorkerWHostingDefView 了（2026-09-29）——
    // 那一份和这一份逐字同形状，而 Win11 那个形状正是这里的注释先写下来的。
    // 现在它是 DesktopLayer.Look() 的第三条退路，正本一份。

    private async Task Frame(float seconds)
    {
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void Log(string step, string message) => LogWriter.ToProbe(step, message);
}
