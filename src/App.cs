using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>程序本身：读设置、装输入钩子、每 0.2 秒推一次状态机、按它的话举起和放下遮罩、
/// 把这一次记进 history。
///
/// ```
/// Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/App.tscn
/// ```
///
/// 托盘接上了（<see cref="Tray"/>），所以退出有了三条路：托盘的「退出」、<c>--seconds</c>、Esc。
/// 托盘的「设置…」开的是 <see cref="Options"/>，「桌面日历」切换的是 <see cref="Calendar"/>，
/// 「关于 Keeper」开的是 <see cref="About"/> —— 六行没有灰的了。
///
/// 托盘上<b>永远只该有一颗 Keeper</b>：一颗图标就是一个进程的 NIM_ADD，所以两颗的意思是
/// 跑了两发。第一发起来时先拿 <see cref="Instance"/> 那把具名互斥体，拿不到的那一发在建托盘
/// 之前就退，并且把「有人又点了一次」递给正在跑的那一发（它把设置窗提到前面来）。
///
/// 那一族测量程序（实拍帧、真点击注入、扫桌面、自己发鼠标事件）搬到了 <see cref="Shots"/> ——
/// 2026-10-06 之前它住在这个 class 上，2318 行里约 1160 行是它，而两边共享同一扇日历窗、
/// 同一份桌面快照、同一只真鼠标，读一个不懂另一个。搬完这一侧只剩六处调用：
/// <c>Shots.From</c>（启动那一趟认旗）、<c>Pump</c>（每帧）、<c>Raised</c> /
/// <c>StageEntered</c>（遮罩的几句话）、<c>NoteEscape</c>（Esc 收尾那一对计数）、
/// <c>RestoreDesk</c>（退出前把扫下去的桌面还回去）。
///
/// 这一侧仍然是产品的组合口。下面这几个开关是给量的时候用的，不是给用户的：
/// <c>--streak 20</c> 把「连续用多久该提醒」从默认值改成 20 秒 —— 不然验一次要等默认值那么久，
/// <c>--options</c> 一起来就把设置窗开在屏幕上，<c>--data &lt;目录&gt;</c> 指到临时目录，
/// 免得把用户现在的记录改了；<c>--registry</c> 是它的反面：明说这一跑允许动 HKCU 的登录项；
/// <c>--anyway</c> 是单实例那道锁的反面：明说这一跑允许和另一发并排（那样托盘上就会有两颗，
/// 只有在「同时量两发的实测结果」这种非并排不可的测量程序上才该用它），
/// <c>--probe-port</c> 则把产品主动开的那一道口开出来（<see cref="ProbePort"/>）——
/// 不带它的时候连管道都不建。</summary>
public partial class App : Node
{
    private static string LogPath => LogWriter.AppPath;   // 留着当路径的出处；写日志走 LogWriter
    private const double StepMs = 200;

    // `_machine` 要拿 `_activity` 才能在「真歇过一次」之后把「这一段连续使用」清零。
    // 而 C# 的字段初始化器不能引用另一个实例字段（CS0236），所以这一处在构造函数里接。
    private readonly Activity _activity = new();
    private readonly Scheduler _machine;
    private readonly List<Fader> _faders = [];
    private Settings _settings = new();
    private SettingsStore.Read _read = new();
    private Tray? _tray;
    private History _history = new();

    /// 一次淡入一份声音（不是每块屏一份），建在 <see cref="_Ready"/> 里。
    private Sound _sound = null!;

    private Todos _todos = new();
    private string _todosPath = "";
    private string _dataDir = SettingsStore.LiveDir();
    private double _sinceStep;
    private bool _waitingForLand;

    /// 上一次问「有人占着独占全屏吗」的实测结果。留着只为了翻面那一行日志，判定本身
    /// 每发都现问（<c>Detection.Now</c> 里问的那一发）。
    private bool _blocked;

    /// 上一次问「占着前台的是哪个应用」的实测结果，同样只为翻面那一行日志。比较的是
    /// <c>Exe</c> 那一格（<see cref="Foreground.Measurement.Known"/> 为 false 时是 null），
    /// 所以「窗换了但还是同一个应用」不报，而「问得到变成问不到」要报。
    private string? _foregroundApp;

    /// <b>上一发完整的前台实测结果</b>（三格都在）。留着有两个用处：托盘上勿扰那一行的实测结果
    /// 现问（不缓存判定），而设置窗里那一条「当前前台」实测结果要走同一个钩子 —— 那一扇窗不
    /// 自己问系统（问出来的永远是它自己那个 exe），所以它读的是 App 这一侧这一发。
    private Foreground.Measurement _foreground;

    /// <summary>现实时间在一处进来的那一个（卡片 02，设计文档 `docs/design/daily-routine.md`「按现实时间走」那条）。
    /// <c>ProcessFrame</c> 每帧第一句刷新它，这一帧之内所有要现实时间的地方读的都是它 ——
    /// 同一帧里两次 <c>DateTime.Now</c> 跨分钟边界时，托盘、状态机和遮罩会各拿到一份分钟。
    /// 初值在字段声明处取一次真钟：第一帧之前托盘菜单就可能被右键现做，读到 <c>default</c>
    /// 就是「0001-01-01 00:00」那一块。</summary>
    private DateTime _wallNow = DateTime.Now;

    /// <summary>假钟覆盖（测量程序 <c>--fader-shot … --routine</c> 那一族用的口子）。
    /// null = 走真钟；非 null = <c>ProcessFrame</c> 刷新 <see cref="_wallNow"/> 时用它，
    /// 不再问系统。设置它的唯一入口是 <see cref="Shots.Hooks.SetWallNow"/>。</summary>
    private DateTime? _wallNowOverride;

    /// 上一发勿扰成不成立。只为翻面那一行日志：每 0.2 秒印一次会把日志刷废，
    /// 而「这一发为什么变了」要的就是那一次翻面。
    private bool _dndOn;

    /// 到点自己退，是给测量程序用的开关，不是产品行为。默认关着：托盘在 2026-09-25 接上之后，
    /// 直接跑 App.tscn 两分钟就自己没了，那是测量程序在动，不是程序该动。<c>--seconds 20</c> 还在。
    private double _runSeconds;

    /// 实拍帧 + 注入那一族测量程序。<b>这一侧和它之间只有六处调用</b>：<c>_Ready</c> 里
    /// <c>Shots.From</c> 一次、<c>_Process</c> 里 <c>Pump</c> 一次、<c>OnRaised</c> /
    /// <c>OnStepEntered</c> 各一句、<c>EndOnEscape</c> 一句、<c>Quit</c> 一句。
    /// 它要的东西全在 <see cref="Shots.Hooks"/> 那一份里 —— 这一侧不递自己的私有字段进去，
    /// 而它也摸不着这一侧的私有状态（除那一份钩子）。
    private Shots? _shots;

    /// 产品主动开的那一道口（<see cref="ProbePort"/>，工单 05）。**只有三处碰它**：
    /// <c>_Ready</c> 里认旗开一次、<c>_Process</c> 里 <c>Pump</c> 一次、<c>Quit</c> 里关一次。
    /// 不带 <see cref="ProbePort.Flag"/> 时它是 null —— 那时连管道都不建。
    private ProbePort? _port;

    /// 状态机和「这一段连续使用」那份实测结果在这里接起来 —— 见字段上那一段的原话：
    /// 真歇过一次之后，状态机靠它把累加的那个数清零，不接就静悄悄不发生。
    public App() => _machine = new(_activity);

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        // 单实例这道锁走在一切前面，尤其走在托盘前面：那颗图标是 NIM_ADD 那一刻挂上去的，
        // 等发现已经有人在跑再把它收掉，中间那一瞬屏幕上就是两颗。
        // 规矩和它为什么是互斥体而不是锁文件，写在 src/Instance.cs。
        if (!Instance.TryAcquire())
        {
            if (Index(args, "--anyway") >= 0)
                Log("app", "单实例：锁被另一发占着，--anyway 明说这一跑允许并排 —— 照旧起（托盘上会有两颗）");
            else
            {
                var handed = Instance.HandOff(out var where);
                HideOwnWindows();   // 引擎那扇主窗在这一发里已经起来了：不留图标，也就别留窗
                Log("app", $"单实例：另一发 Keeper 在跑，这一发让路 —— 递给它 {where}（交了={handed}）");
                GetTree().Quit(0);
                return;
            }
        }
        var streak = Index(args, "--streak");
        var seconds = Index(args, "--seconds");
        var data = Index(args, "--data");
        if (data >= 0 && data + 1 < args.Length) _dataDir = args[data + 1];
        if (seconds >= 0 && seconds + 1 < args.Length) double.TryParse(args[seconds + 1], out _runSeconds);

        // 首启那一趟先把老名字那几份逐字节改名（设计文档 `docs/design/config.md`「老名字逐字节改名」那一节，
        // 加上记录那三份同名的老法）。它必须在任何一次读之前，否则老名字那份读不到，程序会以为
        // 「没有文档」而走首启那条路 —— 那就是把用户的设置当没用过。
        foreach (var said in Records.RenameLegacy(_dataDir))
            Log("records", said);

        var read = SettingsStore.Load(_dataDir);
        _settings = read.Current;
        _read = read;
        _history = History.Load(Path.Combine(_dataDir, History.FileName));
        if (_history.Unreadable)
            Log("app", $"{History.FileName} 读不通：休息记录一条都不显示，第一次写回去之前先留一份 .bak");
        _todosPath = Path.Combine(_dataDir, Todos.FileName);
        _todos = Todos.Open(_todosPath);
        if (_todos.Unreadable)
            Log("app", $"{_todosPath} 读不通：清单一条都不显示，第一次写回去之前先留一份 .bak");
        _calendarPath = Path.Combine(_dataDir, CalendarNotes.FileName);
        _calendarNotes = CalendarNotes.Open(_calendarPath);
        if (_calendarNotes.Unreadable)
            Log("calendar", $"{_calendarPath} 读不通：日历上一条备注都不显示，第一次写回去之前先留一份 .bak");
        var lang = Lang.Apply(_settings.Language);
        if (lang.Count > 0) Log("lang", $"语言表有 {lang.Count} 条问题：{string.Join("；", lang)}");
        Log("app",
            $"目录={_dataDir} 设置读={(read.Error == null ? "通" : "不")}" +
            $"（{read.Error ?? "OK"}）连续 {read.Current.ActivityTimeoutMin} 分钟提醒一次，" +
            $"flow={read.Current.Flow}，已存 {_history.Records.Count} 条记录，" +
            $"清单 {_todos.Items.Count} 条，语言={TranslationServer.GetLocale()}，" +
            $"凝实后{(read.Current.StayOnTopAfterLanding ? "继续压着别的窗口" : "让别的窗口盖得住")}");
        // 那一组改了名字的老键：值不生效是设计，不是坏了。不说出来，人只会以为日历没读进设置。
        foreach (var retired in read.Retired)
            Log("settings", $"文档里有一条改了名字的老键：{retired} —— 老那一条原样留在文件里、不生效，" +
                            "这一组回默认（老键是像素、新键是工作区的份额，所以不硬映射）");

        if (streak >= 0 && streak + 1 < args.Length && double.TryParse(args[streak + 1], out var owed))
        {
            _machine.OverrideOwedMs((long)owed * 1000);
            Log("app", $"闹钟改成 {owed} 秒 —— 测量程序开关，不是设置");
        }
        _calendarForced = Index(args, "--calendar") >= 0;
        _calendarFeed = Index(args, "--feed") >= 0;
        if (_calendarFeed) Log("calendar", "测量程序开关：--feed 开着，把桥整个关掉（量「关掉之后格子点不动」那一发 —— 今天这一扇带 WS_EX_TRANSPARENT，桥关着就是真的点不动）");
        _autostartLive = Index(args, "--registry") >= 0 || data < 0;
        ReconcileAutostart("启动");

        _machine.Raised += OnRaised;
        _machine.StageEntered += OnStepEntered;
        _machine.Lowered += OnLowered;
        // 一次淡入一份声音，不是每块屏一份 —— 全应用就这一个播放器。
        _sound = new Sound(message => Log("sound", message));
        AddChild(_sound);
        var parked = HideOwnWindows();

        // 托盘建在藏窗口之后：反过来的话它自己也会被那一发 SW_HIDE 扫到。
        // 那些动作和问句一次交齐（TrayHooks），起步那张图标也在这里定 —— 原来构造里写死 Idle，
        // 下面要连调两次 SyncTray() 才盖得掉，其中一次纯为这件事。
        _tray = new Tray(new Tray.TrayHooks(
                Show: TogglePreview,
                Calendar: ToggleCalendar,
                Auto: ToggleAutoFade,
                Options: OpenOptions,
                About: OpenAbout,
                Exit: Quit,
                // 第二发起来又被锁挡回去的那一发递过来的一条：Keeper 没有主窗，所以「它在这儿」
                // 这件事由设置窗来说 —— OpenOptions 本来就拒绝开第二扇，重复递也只有一扇。
                SecondLaunch: OpenOptions,
                // 番茄钟是托盘里的一次运行期开/关，和上面那颗「进入休息」同一族 —— 它问的是
                // 「现在跑不跑」，不是「这台机器该怎么配」。配的那两个键在设置窗里。
                Pomodoro: TogglePomodoro,
                // 开机启动和上面两颗不同族：它翻的是**这台机器的配置**，不是这一发此刻在做什么。
                // 托盘里省掉的是「先去设置窗翻到「系统」那一格」这一趟，键和那一格是同一个。
                Autostart: ToggleAutostart,
// 「休息历史…」：和设置窗那一格开的是**同一个动作**（<c>OpenHistory</c>），
                // 而那个动作本身拒绝开第二扇 —— 设计文档 `docs/design/records.md`「两个入口，同一份数据」那一条。
                History: OpenHistory,
                // 生活模式那三样（工单 04）。**托盘这一层只做「切」**：新建要输名字、覆盖要确认、
                // 删掉要点头，那三件都在设置窗里，所以子菜单下半是「管理生活模式…」而不是三件套。
                SwitchLifeMode: SwitchTrayMode,
                ManageLifeModes: ManageLifeModesFromTray,
                // 名单**在打开菜单那一刻现问**（菜单是现做的，所以名单也是现问的）：
                // 存了、删了、切了之后下一次右键就该是新的那一份，而托盘自己不存第二份真相。
                LifeModes: TrayLifeModes,
                AutoFadeOn: () => _settings.AutoFade,
                PreviewOn: () => _machine.SnapshotOf(_settings).Preview,
                // 「在不在屏幕上」问的是宿主那一族的意图位（卡片 03 · S28：藏放归
                // CalendarHost.Wanted，档位归 CalendarHost.StateNow，托盘那一侧只读前者）。
                CalendarOn: () => _calendar != null && IsInstanceValid(_calendar) && _calendar.Host.Wanted,
                PomodoroOn: () => _machine.SnapshotOf(_settings).Pomodoro,
                // 开机启动那一行打不打勾，问的是活的那一份意图。菜单是右键现做的，所以
                // 这一问拿到的永远是当下那一刻 —— 刚才在设置窗改过也照样。
                AutostartOn: () => _settings.Autostart,
                // 专注段还剩多久。休息已经抬起来时给 0 —— 那时候屏幕上就有钟了。
                PomodoroLeftMs: () => _machine.PomodoroFocusLeftMs(_activity.NowMs),
                // 此刻这个时间块叫什么；没有作息表、此刻不在任何块里（或者整张表关着）就是 null，
                // 而 null 是「托盘上那一行不出现」（规则在 Tray.BlockRowName 一处，自检里量的是同一个方法）。
                // 分钟取自 _wallNow（卡片 02：现实时间只从 App 一处进来）。
                BlockName: () => Tray.BlockRowName(_settings, Routine.MinuteOfDay(_wallNow)),
                // 勿扰那一行：成立时托盘上多出来一行，不成立时那一行整个不在（不是空着的一行）。
                // **现问**（和上面那行同一处置）：菜单是右键现做的，规则是用户现配的。
                // 时刻同样是本帧缓存的 _wallNow，和 0.2 秒那一发的求值同一分钟。
                DndWhy: () => Tray.DndWhy(_settings, _wallNow, _foreground),
                // 「下次休息还有多久」那一行（2026-10-08）：菜单顶上第一行，答的是
                // 「再连续用下去多久它会来」—— 契约说触发量的是**连续**输入，
                // 而这件事不看屏幕看不出来，所以它得在菜单里说一声。
                // 规矩全在 `Scheduler.BreakLeftMs` 一处（哪几档不出现也在那里），
                // 这里只递当下那一刻，让状态机自己取「这一段走了多久」和那两档旗标。
                BreakLeftMs: () => _machine.BreakLeftMs(_settings, _activity.NowMs),
                // 「休息历史」那个悬停子菜单的上半：今天的那些（工单 desktop-comfort/04）。
                // **现问**，和上面那几行同一处置 —— 记录随时会多一条（这一发本身就在记一条），
                // 而子菜单是摆菜单那一刻现做的。
                //
                // **归天和顺序全在 `HistoryDays` 一族里**，这里只把它给的那一份**翻成从旧到新**
                // （所有者定的次序；`HistoryDays` 给的是倒序）。翻这一次，不在两处各翻一次。
                Today: () =>
                {
                    // 「今天」那两问用同一个时刻（卡片 02）：本帧缓存的 _wallNow。
                    // 归天和取那一天要是各问一次真钟，跨午夜那一刻会问出两个「今天」。
                    var now = new DateTimeOffset(_wallNow);
                    var reading = HistoryDays.Reading.Of(_history, now);
                    if (reading.Unreadable)
                        return new Tray.TodayList([], true);
                    var today = HistoryDays.DayOf(now);
                    var mine = reading.Days.FirstOrDefault(d => d.Date == today);
                    if (mine == null) return new Tray.TodayList([], false);
                    var entries = mine.Entries
                        .Reverse()
                        .Select(e => new Tray.TodayEntry(e.At, e.Taken))
                        .ToList();
                    return new Tray.TodayList(entries, false);
                }),
            TrayMode(),
            message => Log("tray", message), key => Tr(key));
        _activity.Start();
        // 「点得到的那几块」那一刀交给钩子按坐标判（2026-10-10 所有者定夺「永远都不能抢
        // 鼠标，这是 bug」）：拆掉收鼠标的那一扇窗之后，清单和那颗钮要有个新出处。
        // **这一发只判坐标**：委托跑在全机输入的同步路径上（`Activity` 那段注释），
        // 它答「吃 / 不吃」，勾的动作走 `_Process` 里下一帧那一句。
        // 它在这儿装而不是在 `RaiseFaders` 那一族里 —— 钩子从起就在，问的是「哪一块屏
        // 有没有收鼠标的那一块」，而那个问法每一帧都成立。
        _activity.MouseSwallower = at =>
        {
            foreach (var fader in _faders)
            {
                if (!GodotObject.IsInstanceValid(fader)) continue;
                if (fader.Swallows(at)) return true;
            }
            return false;
        };
        ReconcileCalendar("启动");

        // 那一族测量程序就在这一句话里认旗、排程。**次序在它那边排好了** —— 生活模式必须在
        // 开窗之前种好（名单是窗 _Ready 时读的），所以这一句放在 `OpenOptions`（下面那个
        // `--options`）之后是照抄搬之前的位置，不是我图省事挪的。
        _shots = Shots.From(args, new Shots.Hooks(
            DataDir: _dataDir,
            Read: () => _read,
            Live: _settings,
            Input: _activity,
            Machine: _machine,
            Faders: () => _faders,
            Options: () => _options,
            Calendar: () => _calendar,
            About: () => _about,
            Tray: () => _tray,
            CalendarNotes: () => _calendarNotes,
            CalendarPath: () => _calendarPath,
            CalendarForced: () => _calendarForced,
            Screens: Screens,
            SettingsApplied: SettingsApplied,
            OpenOptions: OpenOptions,
            OpenAbout: OpenAbout,
            TogglePreview: TogglePreview,
            Quit: Quit,
            // 工单 01：测量程序伸手进产品的那九处，改由 Hooks 递过去。**每一处都要产品的
            // 知识**（哪一格是几号、输入框摆在哪儿、控件排完版那个矩形多大、壁纸源图在哪儿），
            // 所以这九句只有产品答得出来。这一族那边一个 `Calendar.` / `Fader.` 都不写。
            //
            // **每一句现问，不缓存**：日历窗会被 `ReconcileCalendar` 重建，而日历不常在、
            // 遮罩按屏起落 —— 缓存一份就等于把「这一发问的是哪一扇窗」定死在组装那一刻。
            // 问的是「此刻那一扇」，答不出（没开 / 已释放）就是 null，调用方自己当没这一发。
            CalendarProbe: () =>
            {
                var c = _calendar;
                if (c == null || !IsInstanceValid(c)) return null;
                return new Shots.CalendarProbe(
                    CellProbe: c.CellProbe,
                    BeginEdit: c.BeginEditFromProbe,
                    PushClick: c.PushClickFromProbe,
                    EditorPlacement: c.EditorPlacementMeasurement,
                    Type: c.TypeFromProbe,
                    SubmitEnter: c.SubmitEnterFromProbe);
            },
            FaderProbe: () =>
            {
                var f = _faders.Count > 0 ? _faders[0] : null;
                if (f == null || !IsInstanceValid(f)) return null;
                return new Shots.FaderProbe(Checklist: f.ChecklistMeasurement, Clicks: f.SwallowMeasurement);
            },
            // 卡片 02 的假钟口：测量程序 `--fader-shot … --routine` 举遮罩前设一个假时刻，
            // 抓完设回 null（真钟）。非 null 时把本帧的 _wallNow 一并落到假值 ——
            // 举遮罩就发生在这一次 Pump 回调里，`Raise → Enter → BuildBlock` 全程同步，
            // 等下一帧开头再刷就晚了一帧（块名那一行会按真的上一分钟算成「不在块里」）。
            SetWallNow: wallNow =>
            {
                _wallNowOverride = wallNow;
                if (wallNow is { } fake) _wallNow = fake;
            }));
        if (Index(args, "--options") >= 0) OpenOptions();   // 测量程序开关：不开托盘也够得着设置窗
        // 产品主动开的那一道口（工单 05）。**旗子不在就不构造**：这一句不走，口一个句柄都不占。
        // 朝向和 `Shots` 相反 —— 那一族是产品伸手出去问屏，这一道是独立进程从外面问进来，
        // 接话在下面 `_Process` 里那一行，不另起线程。
        _port = Index(args, ProbePort.Flag) >= 0
            ? ProbePort.Open(new ProbePort.Hooks(Calendar: () => _calendar, Faders: () => _faders))
            : null;
        // 钩子那条线程的号印在这里，是工单 desktop-comfort/02 的判据：
        // **它必须和主线程的号不一样** —— 一样就是退回 2026-10-12 之前那版，
        // 而那一版的代价是全机鼠标跟着主线程排队（`WH_MOUSE_LL` 在全机输入的同步路径上）。
        Log("app", $"钩子装上了：{_shots.JiggleNote()}；" +
                   $"钩子线程={_activity.HookThreadId} 主线程={System.Environment.CurrentManagedThreadId} " +
                   $"（这两个不一样才对了）；藏起 {parked} 个本进程已有的窗口（引擎的主窗口）");
    }

    // ---- 设置窗 ------------------------------------------------------------------

    private Options? _options;

    /// <summary>托盘「设置…」那一条。已经开着就把它提到前面来，不再开第二个 —— 两个窗口编辑
    /// 同一份副本之外的东西，谁后保存谁赢，那是给人看的假象。
    /// 布局实测结果在 <see cref="Options.LayoutMeasurement"/>，这里只负责把窗开出来。</summary>
    private void OpenOptions()
    {
        if (_options != null && IsInstanceValid(_options))
        {
            _options.GrabFocus();
            Log("options", "已经开着一扇，把它提到前面");
            return;
        }
        // 开窗那一段走 `OptionsHost` —— 和九组自检同一份实施（次序、State/DataDir/Checklist
        // 三样必须在挂上去之前赋好，见那一类的注释）。这一侧的两处不同：
        //
        // · **目录是用户那份配置，本类一个字节都不删**（`ForDir`），而自检那一族删的是
        //   自己的临时目录。
        // · **这扇窗故意不放** —— 再按一次是提到前面（上面那两行），不是重开一扇；
        //   它的生死归 `_options` 归零那一刻，不归 `OptionsHost`。
        var host = OptionsHost.ForDir(_dataDir);
        Options win;
        try
        {
            win = host.Open(_read, this, w =>
            {
                w.ChecklistChanged = SaveTodos;
                w.Log = Log;
                w.PreviewRequested = TogglePreview;
                // 「历史」那一颗和托盘「休息历史…」是**同一个动作**（契约「两个入口，
                // 同一份数据」）。递给同一个方法，而不是再写一个「开窗」——
                // 两处各开一次就是两扇窗、两次读文件。
                w.OpenHistory = OpenHistory;
                w.Measurement = StatusLine;
                w.Applied = SettingsApplied;
                // 勿扰那张 exe 名单要一个「当前前台是哪个程序」的实测结果，而**设置窗自己问系统问出来的
                // 永远是它自己那个 exe**（它此刻就是前台）。所以这一格走 App 那一侧 0.2 秒一发的实测结果
                // （同一发，ADR-0006「前台取数搭现有轮询底盘」那一节）：用户切到别的程序再切回来，
                // 那一行实测结果就换成了那个程序。
                w.ForegroundApp = () => _foreground;
            },
                // **递 `_todos` 进去，而不是让窗自己 `Load` 一份**：`_todos` 是 App 手上的那一份
                // （首启读出来、之后每次改动都跟着保存）。窗再读一次就是**第二个对象** ——
                // 界面上勾掉一格，`_todos` 一个字节不动，保存也存的是另一个。
                checklist: _todos);
        }
        catch (Exception e)
        {
            Log("options", $"设置窗开不了：{e.GetType().Name} {e.Message}");
            return;
        }
        _options = win;
    }

    /// <summary>设置窗把值搬回活设置之后：托盘的图标和 tooltip 要重新问一次，登录项要和文档
    /// 对一次账。<paramref name="read"/> 是设置窗当下那一份读取结果 —— <b>导入会还回一份新的</b>
    /// （里面 <see cref="SettingsStore.Read.Current"/> 指向另一个 <see cref="Settings"/> 对象），
    /// 所以这一份由它递上来。原来这里写的是「导入还可能换掉 <c>Read.Current</c> 指向的那个对象」，
    /// 于是下面那一行靠的是<b>记得</b>去重拿一次；<c>Read</c> 不可变之后，忘不掉。</summary>
    private void SettingsApplied(SettingsStore.Read read)
    {
        _read = read;
        _settings = read.Current;
        var lang = Lang.Apply(_settings.Language);
        if (lang.Count > 0) Log("lang", $"语言表有 {lang.Count} 条问题：{string.Join("；", lang)}");
        SyncTray();
        ReconcileAutostart("设置刚应用");
        ReconcileCalendar("设置刚应用");
    }

    // ---- 托盘那颗「生活模式」子菜单 ------------------------------------------------
    //
    // **这一族在托盘上只做「切」**（设计文档 `docs/design/life-mode.md`「两个入口，同一个形状」：形状一样，
    // 能做的事是子集）。切是唯一那件**不需要输名字也不需要确认**的动作 ——
    // 新建要起名、覆盖要点头、删掉也要点头，所以那三件留在设置窗里，
    // 子菜单下半只留一条「管理生活模式…」把它们领过去。

    /// <summary>「生活模式：X」那一条：切到 X。
    ///
    /// 走的是 <see cref="LifeModeStore.SwitchTo"/> —— 和设置窗里那一条**同一个方法**，
    /// 所以「读不通有几档」「备份留在哪」这些语义在两个入口上不会漂。
    /// 切成了就交给 <see cref="SettingsApplied"/>：托盘图标、tooltip、登录项、
    /// 日历那几样都是设置的一部分，换了一套配置它们都要重新问一次。
    ///
    /// <b>没切成只进日志</b>：托盘没有状态栏，而 <c>Stop</c> 那四档的话是写给
    /// 设置窗那行状态栏的（<c>Options.SwitchWhy</c>）。在这里复述一遍就是两处各说各的；
    /// 弹一个气泡是新产品功能，不在这一张里。</summary>
    private void SwitchTrayMode(string name)
    {
        var done = LifeModeStore.SwitchTo(_dataDir, _read, name);
        if (!done.Ok)
        {
            Log("tray", $"切换到生活模式「{name}」没成（{done.Stopped}），配置和界面都没动：{done.Detail}");
            return;
        }
        SettingsApplied(done.State!);
        Log("tray", $"切换到生活模式「{name}」：被换掉的那一份留在 {SettingsStore.BackupPath(_dataDir)}");
    }

    /// <summary>「管理生活模式…」那一条：开设置窗，并且<b>焦点落在窗头那颗生活模式钮上</b>。
    ///
    /// 焦点这一下是这张子菜单存在的理由：只把设置窗开出来，焦点落在休息提醒那一格，
    /// 用户还得自己找到「生活模式：…」在哪 —— 那等于托盘这一层白点了一次。
    /// 已经开着就只提焦点，不开第二扇（和 <see cref="OpenOptions"/> 同一条规矩）。</summary>
    private void ManageLifeModesFromTray()
    {
        OpenOptions();
        if (_options != null && IsInstanceValid(_options)) _options.FocusLifeModeButton();
    }

    /// <summary>菜单现问的那一份：<b>名单 + 我现在在不在哪一个里</b>。
    ///
    /// 「在不在」是<b>算出来</b>的（<see cref="LifeModeStore.StandingOf"/> 把活的那一份和名单上
    /// 每一份比一遍），不是记下来的 —— 改了配置又改回来的那种情况（「 Elsewhere 」那一档）
    /// 只有比才判得出，记一份就永远停在最后一个被切过去的那一条上。
    ///
    /// <b>名单读不通单立一档</b>，不当成「一个生活模式都没有」：那一份是用户自己写的，
    /// 而空名单读起来就是「你没存过生活模式」（设计文档 `docs/design/life-mode.md`「读不通有三档」那条，03 在设置窗那一侧
    /// 量的就是这一对假象，托盘这一侧不能重犯）。名单交给托盘的是<b>空的</b>加上那一档 ——
    /// 托盘摆的是一条灰的「生活模式名单读不通」，不是一排点不动的名字。</summary>
    private Tray.LifeModeList TrayLifeModes()
    {
        var index = LifeModeIndex.Load(LifeModeStore.ListPathOf(_dataDir));
        if (index.Unreadable) return new Tray.LifeModeList([], null, true);
        var spot = LifeModeStore.StandingOf(_dataDir, _settings, index);
        return new Tray.LifeModeList(index.Entries.Select(e => e.Name).ToList(),
            spot.At == LifeModeStore.Standing.In ? spot.Name : null, false);
    }

    // ---- 关于窗口 ----------------------------------------------------------------

    private About? _about;

    /// 托盘第六行。之前这一行灰着：不是菜单缺了一项，是那一侧的窗根本没建。
    /// 和 <see cref="OpenOptions"/> 同一个理由 —— 已经开着就不开第二扇。
    private void OpenAbout()
    {
        if (_about != null && IsInstanceValid(_about))
        {
            _about.GrabFocus();
            Log("about", "已经开着一扇，把它提到前面");
            return;
        }
        if (GD.Load<PackedScene>("res://src/About.tscn")?.Instantiate<About>() is not About win)
        {
            Log("about", "res://src/About.tscn 实例化不出来，关于窗口开不了");
            return;
        }
        win.Log = Log;
        win.Closed = closed => { if (ReferenceEquals(_about, closed)) _about = null; };
        _about = win;
        AddChild(win);
    }

    // ---- 休息历史窗 --------------------------------------------------------------

    private HistoryWindow? _historyWindow;

    /// 托盘「休息历史…」和设置窗「历史」那一颗**共同调这一个方法**（契约
    /// 「两个入口，同一份数据」那一条）。所以「已经开着就提到前面，不开第二扇」这件事
    /// 只有一份实施 —— 两处各判一次就有可能出现「托盘开着的那一扇、设置窗开的另一扇」，
    /// 而那种不一致在界面上看不出是哪一边错了。
    ///
    /// 和 <see cref="OpenAbout"/> / <see cref="OpenOptions"/> 同一个理由：重复递也只有一扇。
    /// 数据**不在这里读**：窗问的是 <see cref="_history"/> 这一个对象（首启读出来、
    /// 之后每次记下都跟着更新的那一份），两处入口看的是同一个数。
    private void OpenHistory()
    {
        if (_historyWindow != null && IsInstanceValid(_historyWindow))
        {
            _historyWindow.GrabFocus();
            Log("history", "已经开着一扇，把它提到前面");
            return;
        }
        if (GD.Load<PackedScene>("res://src/HistoryWindow.tscn")?.Instantiate<HistoryWindow>()
            is not HistoryWindow win)
        {
            Log("history", "res://src/HistoryWindow.tscn 实例化不出来，休息历史开不了");
            return;
        }
        win.Log = Log;
        win.Source = () => _history;
        win.Closed = closed => { if (ReferenceEquals(_historyWindow, closed)) _historyWindow = null; };
        _historyWindow = win;
        AddChild(win);
    }

    // ---- 桌面日历 ----------------------------------------------------------------

    private Calendar? _calendar;
    private bool _calendarFeed;
    private CalendarNotes _calendarNotes = new();
    private string _calendarPath = "";
    private bool _calendarForced;

    /// <summary>开日历。已经开着就不开第二扇 —— 两扇窗挂进同一层壁纸、写同一份备注，
    /// 谁后写谁赢，那是和 <see cref="OpenOptions"/> 同一个理由。</summary>
    private void OpenCalendar()
    {
        if (_calendar != null && IsInstanceValid(_calendar)) return;
        _calendar = new Calendar(_settings, 0, _calendarNotes, _activity, SaveCalendarNotes,
            message => Log("calendar", message));
        if (_calendarFeed) _calendar.BridgeOn(false);
        AddChild(_calendar);
        Log("calendar", $"开了：备注 {_calendarNotes.Notes.Count} 条在 {_calendarPath}，" +
                        $"桥{(_calendarFeed ? "关着（--feed：量「关掉之后格子点不动」那一发）" : "开着（这一扇带 WS_EX_TRANSPARENT，鼠标穿过去，桥是格子唯一的入口，见 Calendar._bridgeOn）")}");
    }

    private void CloseCalendar()
    {
        if (_calendar == null || !IsInstanceValid(_calendar)) return;
        _calendar.CommitAndSetDown("要关了");   // 正在打字的话先把那一格落盘，别丢人写了一半的字
        _calendar.QueueFree();
        _calendar = null;
        Log("calendar", "关了");
    }

    /// 托盘「桌面日历」那一条。
    private void ToggleCalendar()
    {
        if (_calendar != null && IsInstanceValid(_calendar)) _calendar.Toggle();
        else OpenCalendar();
    }

    /// <summary>文档里 <c>calendar.enabled</c> 说的就是这扇窗在不在。设置窗刚应用、和启动那一次，
    /// 都走这一发对账 —— 和 autostart 那条 <see cref="ReconcileAutostart"/> 是同一个形状：
    /// 意图只有一个出处，现场跟着它摆。
    ///
    /// <b>已经开着的那一扇也要动</b>：把新的那份设置递给它、再重新量一遍。这一步以前没有 ——
    /// 所以在设置窗里拧了那几条比例、按了保存，屏幕上得重启才看得到，而「比例在配置里可调」
    /// 说的就是改了就看到。</summary>
    private void ReconcileCalendar(string when)
    {
        var want = _settings.Calendar.Enabled || _calendarForced;
        var have = _calendar != null && IsInstanceValid(_calendar);
        if (want && !have) { OpenCalendar(); Log("calendar", $"{when}：文档说开着，开一扇"); }
        else if (!want && have) { CloseCalendar(); Log("calendar", $"{when}：文档说关着，收掉"); }
        else if (have)
        {
            _calendar!.UseSettings(_settings);
            _calendar.Relayout(when);
        }
        else Log("calendar", $"{when}：文档说{(want ? "开着" : "关着")}，现场一致，没动");
    }

    private void SaveCalendarNotes()
    {
        try
        {
            _calendarNotes.Save(_calendarPath);
            Log("calendar", $"备注写回 {_calendarPath}（现在 {_calendarNotes.Notes.Count} 条）");
        }
        catch (Exception e)
        {
            Log("calendar", $"备注写不回去 {_calendarPath}: {e.Message}");
        }
    }

    // ---- 登录项 ------------------------------------------------------------------

    private bool _autostartLive;

    /// <summary>把 HKCU 的登录项改成文档里 <c>autostart</c> 说的那样。
    ///
    /// 带 <c>--data</c> 的测量程序跑默认跳过这一步：那种运行不该在别人的下次登录里种一个进程，
    /// 而验这一半要的就是「真的写了、真的删了」，所以另给一个 <c>--registry</c> 明说。</summary>
    /// 托盘那一下开机启动。<b>它只改意图，不碰注册表</b> ——
    /// 改完那一个键就交给 <see cref="ReconcileAutostart"/>，和启动时、设置窗刚应用时
    /// 走的是同一个动作（设计文档 `docs/design/tray.md`「开机启动的意图在文档里，注册表是它的效果」那一条）。
    /// 托盘要是自己写注册表，那就是第二个能说了算的地方。
    ///
    /// 勾跟着 <see cref="SyncTray"/> 走，而菜单本来就是现做的：下一次右键现问一次，
    /// 所以刷新这一发只是为了图标和 tooltip，不是因为菜单缓存了什么。
    private void ToggleAutostart()
    {
        _settings.Autostart = !_settings.Autostart;
        var written = SettingsStore.Save(_dataDir, _read);
        _read = written.State;
        SyncTray();
        Log("tray", $"开机自动启动改成 {_settings.Autostart}，写回 {(_read.Error ?? "OK")}" +
                    (written.Error == null ? "" : $"（没写进去：{written.Error}）"));
        ReconcileAutostart("托盘切的那一下");
    }

    private void ReconcileAutostart(string when)
    {
        if (!_autostartLive)
        {
            Log("autostart", $"{when}：测量程序跑，注册表没动（要验它加 --registry）");
            return;
        }
        var command = Autostart.Command(OS.GetExecutablePath(), ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\'),
            OS.HasFeature("editor"));
        Log("autostart", $"{when}：文档说 {_settings.Autostart} → {Autostart.Sync(_settings.Autostart, command)}");
    }

    /// 抬头那一行：还要多久才淡入，取「这一段还没走够」和「被挥散之后的等待」里较短的那个。
    /// 算式整个收在 <see cref="Scheduler.NextFadeLeftMs"/> 里，这里只剩换算单位和记录数。
    private (double NextMin, int Records) StatusLine() =>
        (_machine.NextFadeLeftMs(_settings, _activity.NowMs) / 60_000.0, _history.Records.Count);

    /// 引擎一启动就自带一个主窗口，而 Keeper 的形状是「只有托盘 + 一个不出现的窗口」。
    /// 它挂在那儿不只是难看：2026-09-25 那两发注入里,清单和按钮那几个点的命中测试归的就是它 ——
    /// 它盖在遮罩之上,「点不动」有一半是它造成的。Window.Hide() 从 C# 是空操作,要藏就走 SW_HIDE
    /// （CODING_STANDARDS.md <c>Window.Hide()</c> 那一条）。FadeRun 早就这么藏过并且照样淡入,所以这一手不新。
    private static int HideOwnWindows()
    {
        var hidden = 0;
        foreach (var hwnd in Win32.OwnTopLevelWindows())
            if (Win32.IsWindowVisible(hwnd) && Win32.ShowWindow(hwnd, Win32.SwHide)) hidden++;
        return hidden;
    }

    private static int Index(string[] args, string wanted) => Array.IndexOf(args, wanted);

    public override void _Process(double delta)
    {
        // 这一帧从进来到出去的整段时间（工单 05 那一行判据量的就是它）。
        // **只在口开着的时候才有人接这两个数** —— 不开时它自己两个都拿不到，
        // 所以「开着和关着的差」量的其实是「开着时的整帧耗时」减「不开时的整帧耗时」，
        // 后一半要另跑一发明不开旗子的，那一发的数才是底。
        var frameStarted = _port != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            ProcessFrame(delta);
        }
        finally
        {
            if (_port != null) _port.SampleFrame(System.Diagnostics.Stopwatch.GetTimestamp() - frameStarted);
        }
    }

    /// <summary>那一帧的正文。原来就长在 <c>_Process</c> 里，拆出来只是为了外面套得上计时。</summary>
    private void ProcessFrame(double delta)
    {
        // 现实时间这一族在一处进来（卡片 02）：这一句排在全帧最前、`_shots.Pump` 之前 ——
        // 测量程序那一族排的回调和产品的帧读的是**同一份** `_wallNow`。
        // 有假钟覆盖用它（`Shots.Hooks.SetWallNow`），没有就问真钟。
        _wallNow = _wallNowOverride ?? DateTime.Now;
        // 这一族测量程序的那三件（排程的钟、自己发鼠标移动、`--calendar` 每 5 秒报桥的数）
        // 都在这一句话里。产品的帧不替它做任何别的事。
        _shots?.Pump(delta);
        // 这一道口的接话也在这一句话里（工单 05）：**不另起线程** —— 产品每帧本来就在跑，
        // 而 `Pump` 三档都不等（没连上就问一次连上没有、没字节就直接回）。
        // 外面套的那一下量的是「口开着的时候产品那一帧花了多少」——工单 05 那一行判据量的
        // 就是它，而量它就得在这一句外面量，从里面量不到。
        var portStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _port?.Pump();
        if (_port != null) _port.SampleFrame(System.Diagnostics.Stopwatch.GetTimestamp() - portStarted);
        // **勾清单那一下在下一帧做，不在钩子回调里当场做**（2026-10-10）：那个回调挂在
        // 全机输入的同步路径上，在里面翻一颗控件的状态就是把所有人的输入都拖慢一次。
        // 所以它放在 `_Process` 的最前头、`StepMs` 那一道早退**之前** —— 勾清单不跟着
        // 状态机那 200 毫秒的节拍走，屏幕上看到的就是按下去的那一帧。
        RouteSwallowed();
        _sinceStep += delta * 1000;
        if (_sinceStep < StepMs) return;
        _sinceStep = 0;

        // 「别的程序正占着独占全屏」这一发从 2026-09-26 起是真的了：状态机那一侧（<c>Scheduler.Step</c>）
        // 早就认这个参数，缺的一直是这里 —— 之前永远传 false。
        // 只在它翻面的那一刻报一行：每 0.2 秒印一次会把日志刷废，而「这一发为什么没来」
        // 要的就是那一次翻面。
        // **这一段不自己去问那两发**：输入三问、独占全屏、前台应用都由检测那一族装进聚合
        // （<c>Detection.Now</c>），这一段只判翻面、只递聚合。
        // 勿扰那一格是**业务求值**出来的（时段表 + 名单），不是一族实测结果，所以它在 App 这一侧
        // 拿设置求（<c>Dnd.Of</c>），然后和那几格一起递进同一个聚合（取向在 ADR-0006
        // 「勿扰是业务功能」那一节）。**前台那一发只问一次**：求值要用它，聚合里也是同一格。
        var foreground = Detection.ForegroundApp();
        _foreground = foreground;
        var dnd = Dnd.Of(_settings, _wallNow, foreground);
        if (dnd.On != _dndOn)
        {
            _dndOn = dnd.On;
            Log("dnd", dnd.On ? $"{Dnd.Describe(dnd)}（托盘上多出来的那一行也是它）" : "勿扰不成立了");
        }
        var detected = Detection.Now(_activity, foreground, dnd);
        if (detected.OwnsScreen != _blocked)
        {
            _blocked = detected.OwnsScreen;
            Log("fullscreen", $"{(_settings.BlockWhenFullscreen ? "躲开" : "不躲（blockWhenFullscreen 关着）")}" +
                              $"：{Detection.FullscreenMeasurement()}");
        }
        // 前台应用那一发同样只报翻面那一瞬（每 0.2 秒印一次会把日志刷废）。
        // 这一行实测结果就是工单 02 要的那一条：前台 exe 名 + 完整路径，句法和
        // <c>Fullscreen.Measurement</c> 同款；「问不到」那一种也照实说，不印一个空字符串。
        if (detected.ForegroundApp.Exe != _foregroundApp)
        {
            _foregroundApp = detected.ForegroundApp.Exe;
            Log("foreground", Detection.ForegroundMeasurement(detected.ForegroundApp));
        }
        _machine.Step(_activity.NowMs, detected, _settings,
                      // 现实时间上的那一刻由这一侧给（作息表那一族的接缝，设计文档 `docs/design/daily-routine.md`「按现实时间走」那条）。
                      // 给的是「一天里的第几分钟」，而那一族自己只做算术和判定。
                      // 时刻是这一帧开头缓存的 _wallNow（卡片 02：现实时间只从这一处进来）。
                      wallMinute: Routine.MinuteOfDay(_wallNow));
        // 托盘 tooltip 上那个「还剩 m:ss」得自己走：菜单是右键现做的，tooltip 不是 ——
        // 它挂在图标上，不递一发 NIM_MODIFY 就一直是刚才那一句。所以盯着**显示出来的那一句**
        // 变没变，变了才递一次（每帧递是白白地打扰 shell）。
        RefreshPomodoroTip();
        // Esc 是「提前结束休息」的另一只手（键盘那半只长在低级钩子上，见 Activity.Judge 那段
        // 注释）。闸门只有一个：这一步收不收鼠标；淡入那几步本来还允许挥散，没有「结束」可做。
        if (_activity.TakeEscape()) EndOnEscape();
        if (_waitingForLand && AllLanded()) CloseFaders();

        if (_runSeconds > 0 && _activity.NowMs / 1000.0 > _runSeconds) Quit();
    }

    /// <summary>把钩子吃掉的那一下发给收它的那一扇遮罩：落在清单哪一行，还是那颗钮。
    ///
    /// <b>读一次清一次</b>（<c>Activity.TakeSwallowed</c>），所以一按只勾一次；
    /// 多屏同时收着的时候只发给第一扇答「那一块含它」的 —— 那一块是屏上不相交的两块，
    /// 一个点落不进两块。
    ///
    /// <b>一块都没接住就是异常读数</b>：钩子刚答了「吃」，没有一扇认领，那说明
    /// 「吃的那个数」和「认领的那个数」不是同一个。它照样报一行，因为静悄悄地丢掉
    /// 一次点击是最难查的那一种。
    private void RouteSwallowed()
    {
        if (_activity.TakeSwallowed() is not { } at) return;
        var to = new Vector2I(at.X, at.Y);
        foreach (var fader in _faders)
        {
            if (!GodotObject.IsInstanceValid(fader) || !fader.Swallows(at)) continue;
            // **读数在路由之前取**：点在那颗钮上时 `Route` 会立刻收掉整段休息，
            // 而收尾那一发把那一块清空了 —— 之后再读就永远读到 (0,0)。
            var region = fader.SwallowRect;
            fader.Route(to);
            Log("app", $"钩子吃掉 ({at.X},{at.Y})：走了 {region} 那一条");
            return;
        }
        Log("app", $"钩子吃掉 ({at.X},{at.Y})：一块遮罩都没认领 —— 那一发被白吃了");
    }

    /// <summary>Esc 那一下要不要收尾：只有此刻站着的是<b>收了鼠标的那一步</b>才算数 ——
    /// 淡入那几步本来还允许挥散，那里没有「结束休息」这件事可做。走法是把那颗钮的出口走同一条：
    /// 同一次「按休息记账」（<c>Scheduler.EndBreak</c>），不多第二种收尾。
    ///
    /// <b>判定在这一侧，计数在 <see cref="Shots"/> 那一侧</b>：这一族要读「收尾过几次、
    /// 路过几次」这一对数（<c>--esc</c> 那一发），而「该不该收尾」是产品的事 —— 把它也算进去
    /// 就是让测量程序认识遮罩那一族。所以这一次只把结论递过去。
    ///
    /// 为什么用钩子而不是给窗子改样式：要让窗拿到键盘就得动它的可聚焦性，而这一侧不动窗 ——
    /// <c>.scratch/keeper-contract/spec.md</c>
    /// 那句「换档不碰窗口」和 <c>## Solution</c> 那句「不能被聚焦，所以不抢你的光标和按键」都留在原地，
    /// 而我们已经装着 <c>WH_KEYBOARD_LL</c>：Esc 本来就路过这个进程，谁的焦点也没被拿走。
    /// 代价说清楚：这一段里<b>任何人</b>按 Esc 都会收尾（不按窗口的焦点算），不只是点对遮罩的人 ——
    /// 而收鼠标的是「点得到的那几块」那一小块（清单和那颗钮），焦点压根不在它那里，所以按窗口的焦点算，
    /// 这一段里就只剩「焦点在谁那里」和「能不能收尾」两件不相干的事。</summary>
    private void EndOnEscape()
    {
        var interactive = false;
        foreach (var fader in _faders)
            if (GodotObject.IsInstanceValid(fader) && fader.Interactive) interactive = true;
        // 遮罩正在落下去的那几帧里，窗还有效、步还是 interactive 那一步，但状态机已经不在
        // Shown 了 —— 那一扇已经收了的休息不该被再结束一次。
        if (interactive && _machine.SnapshotOf(_settings).Phase != Scheduler.Phase.Shown) interactive = false;
        // **遮罩站着的时候，按下 Esc 就收掉 —— 不看来路**（2026-10-12 所有者定夺，
        // 原话：「按任何按钮都跟一直挥动鼠标是一样的效果」）。
        //
        // 这一段上午短暂地收窄过（「只认落在收鼠标那一块里的 Esc」），**那是错的，已撤** ——
        // 它把一个无条件动作改成了有条件的，于是鼠标不在遮罩上时按 Esc 收不掉，
        // 而「按下必须收掉」就是这个出口的全部意义。
        //
        // 上面那段注释记着这个出口的来路（遮罩拿不到焦点，所以只能靠全局钩子接）。
        if (!interactive)
        {
            _shots?.NoteEscape(false);
            Log("app", $"Esc 路过：这一步不收鼠标，什么都不做（遮罩 {_faders.Count} 扇）");
            return;
        }
        _shots?.NoteEscape(true);
        Log("app", "Esc 收到：走「提前结束休息」那同一条路");
        _machine.EndBreak(_activity.NowMs, _settings);
    }

    // ---- 状态机的话 → 窗口和记录 ----------------------------------------------

    private void OnRaised(Scheduler.StageNow now)
    {
        var step = now.Step;
        var screens = Screens();
        // **上一批还在退就不建新的**（工单 desktop-comfort/05）：`CloseFaders` 那一刻退了，
        // 而这里可能紧接着又来一次淡入。旧的那几扇还在屏上淡出时再叠一批新上去，
        // 就是「闪现的休息阶段」—— 契约「换步不碰窗：不闪、不重举」那一条。
        //
        // **直接返回，不静悄悄丢掉**：屏上不会多一扇，而状态机已经举起来了，
        // 所以这一发由 `CloseFaders` 在下一帧补建（`_waitingForLand` 那条链会走到它）。
        if (_faders.Count > 0)
        {
            Log("app", $"淡入：上一批遮罩还在退（{_faders.Count} 扇），这一发让路，等它走完再举");
            _pendingRaise = now;
            return;
        }
        _pendingRaise = null;
        _landed.Clear();

        foreach (var screen in screens)
        {
            // 块名和时钟要的现实时间由这一侧递（卡片 02）：读的是本帧开头缓存的 _wallNow，
            // 和状态机、托盘那两行是同一分钟。
            var fader = new Fader(_settings, screen, _todos, SaveTodos, () => _wallNow,
                message => Log("fader", message));
            // 按下去的人已经休息了一会儿，所以这条走 EndBreak 而不是「挥散」：它按休息记账。
            fader.EndBreakRequested += () =>
            {
                Log("app", $"收到「提前结束休息」，屏 {screen}");
                _machine.EndBreak(_activity.NowMs, _settings);
            };
            // 落定那一刻交给声音：sound.afterLanding 开着（默认）时，淡入途中那几段不响，
            // 到这一发才响。每块屏都递一发不要紧，Sound.Land 自己认得「已经落过定」。
            fader.Settled += () => _sound.Land(_settings);
            // 「这一块屏淡出走完了」走事件。2026-09-29 之前 AllLanded() 是每 0.2 秒去读
            // fader.Opacity > 0.001f —— 绕到接口后面读实施，而 Fader.Landed 那条事件本来就在那儿
            // （FadeRun.cs 订的就是它）。
            fader.Landed += () => _landed.Add(fader);
            AddChild(fader);
            fader.Raise(now);
            _faders.Add(fader);
        }
        Log("app",
            // 「这一段已经走了」是触发量的那个数（现实时间），「连续已经用了」是另一族实测结果 ——
            // 两个都印，是为了让日志能分清「提醒为什么来」和「人多久没停过」。
            $"淡入：{screens.Count} 块屏，这一段已经走了 {_machine.ElapsedMs(_activity.NowMs) / 1000} 秒，" +
            $"连续已经用了 {_activity.ContinuousMs / 1000} 秒，" +
            $"静了 {_activity.IdleMs / 1000} 秒才落进来；step {now.Number}/{now.Total} " +
            $"content={string.Join('+', step.Content)}" +
            // 到点那一次抬出来的：它是块切换的「光的提示」，不是休息 —— 所以这里把是哪一块
            // 点名，而收尾那一侧（OnLowered）记的是「不进记录」。
            // **问的是抬起这一刻的事实，所以不查快照里的 LastOutcome**：
            // 那是 <c>CloseOut</c> 落下那一刻才写的那一档，而这一刻它还指着上一次落下那一次。
            (_machine.SnapshotOf(_settings).BlockRaise is { } block ? $"；块到点：{block.Name}" +
                $"（{Routine.Clock(block.StartMinute)}–{Routine.Clock(block.EndMinute)}，" +
                $"走 {(block.Flow.Length == 0 ? $"flow={_settings.Flow}" : block.Flow)}）" : ""));
        _sound.Enter(step, _settings);
        // **让路补建的那一次要把收尾这道旗重新置上**（工单 desktop-comfort/05）：
        // 那一发是被 `OnRaised` 挡回去的，所以 `OnLowered` 没参与过 —— 而它才是
        // 置 `_waitingForLand` 的那一个（`OnLowered` → `Leave` → `Landed` → `CloseFaders`）。
        // 不补这一句，屏上这一扇会举起来，但**永远不会收尾**（`_Process` 里那条
        // `if (_waitingForLand && AllLanded())` 一直不成立）。
        if (_resumedPendingRaise) _waitingForLand = true;
        // 测量程序那一族自己看这一步收不收鼠标：`--esc` 那一发要一道对照（淡入途中先交一发
        // Esc 当对照，该被忽略）。判定在那边，计数也在那边 —— 这一侧不问它。
        _shots?.Raised(step.Interactive);
        SyncTray();
    }

    private void OnStepEntered(Scheduler.StageNow now)
    {
        if (now.Total == 0) return;
        var step = now.Step;
        foreach (var fader in _faders)
            fader.Enter(now);
        _sound.Enter(step, _settings);
        Log("app",
            $"进 step {now.Number}/{now.Total}：" +
            $"content={string.Join('+', step.Content)} " +
            $"interactive={step.Interactive}（窗口没动）");
        // 收到鼠标的那一步到了手，测量程序那一族自己排那两发（`--clicks` 真点两下、
        // `--esc` 交一个真的 Esc）。排一次就收掉旗子，免得下一步又排一遍 —— 那是那边的事。
        _shots?.StageEntered(step.Interactive);
    }

    private void OnLowered(bool taken)
    {
        _waitingForLand = _faders.Count > 0;
        foreach (var fader in _faders) fader.Leave();
        _sound.Leave(_settings);
        SyncTray();
        // **这一发是哪一种，由状态机交出来的那一档说了算**（快照里的 LastOutcome）。
        // 原来是拿 <c>BlockRaise</c> 自己拼的：那一格里住着「预览」和「进入下个时间段」
        // 两种，而只有那一族分得清（2026-10-14）。命中「进入下个时间段」那一档时走点名的
        // 那一条，别让日志把换块的提示说成「算休息过了」。
        var snapshot = _machine.SnapshotOf(_settings);
        switch (snapshot.LastOutcome)
        {
            case Scheduler.Outcome.BlockHandover when snapshot.BlockRaise is { } block:
                Log("app", $"淡出：到点那一次的提示（「{block.Name}」），不记成休息，下一次淡入的等待一个字没动");
                return;
            default:
                // 休息过、跳过，还有预览 —— 预览压根不是休息，落下来也在这一行按这一发
                // 收尾的判定说，不在这里报它记不记（记不记是 CloseFaders 那一行的事）。
                // 「下一次最早多久、连着取消几次」由 Retry 一次交出（卡片 01 的读口）。
                var (skipped, nextAttemptInMs) = _machine.Retry(_activity.NowMs);
                Log("app",
                    $"淡出：{(taken ? "算休息过了" : "算跳过")}，下一次最早在 " +
                    $"{nextAttemptInMs / 1000} 秒后（连着被挥散 {skipped} 次）");
                return;
        }
    }

    /// 勾一下就落盘。清单是记录，所以整体重写，不必等这次休息结束 —— 人在遮罩上勾完就切走
    /// 干别的了，那时候程序还不知道自己下一次什么时候才起来。
    private void SaveTodos()
    {
        try
        {
            _todos.Save(_todosPath);
        }
        catch (Exception e)
        {
            Log("app", $"清单写不回去 {_todosPath}: {e.Message}");
        }
    }

    /// 每一块屏都淡出走完了没有。读的是 <see cref="Fader.Landed"/> 收下的一套，
    /// 不是它们的 <c>Opacity</c> —— 见 <see cref="OnRaised"/> 里那一条订阅的理由。
    private readonly HashSet<Fader> _landed = [];

    /// 这一发是不是 `CloseFaders` 补建的让路那一次（不是状态机第一次举的那一次）。
    /// 只有补建的那一次要自己补置 `_waitingForLand`。
    private bool _resumedPendingRaise;

    /// `OnRaised` 让路的那一次（工单 desktop-comfort/05）：屏上还有旧的几扇在退时
    /// 状态机已经举起来了，那一发先不建，旧的几扇退完由 `CloseFaders` 补上。
    private Scheduler.StageNow? _pendingRaise;

    private bool AllLanded()
    {
        foreach (var fader in _faders)
            if (!_landed.Contains(fader)) return false;
        return true;
    }

    private void CloseFaders()
    {
        // **先等旧的那几扇真没了，再清 `_faders`**（2026-10-12，工单 desktop-comfort/05）。
        //
        // 原来这里是 `foreach (…) QueueFree(); _faders.Clear();` —— 而 `QueueFree` 是
        // **排队**的，那一扇要到帧末才真的销毁。`OnRaised` 不等人：只要紧接着来一次淡入，
        // 新的遮罩立刻就建出来了，而**旧的那一扇还在屏上淡出** —— 两扇重叠，
        // 就是所有者报的「闪现的休息阶段」。
        //
        // **等的是淡出走完之后**（设计文档 `docs/design/fader-window.md`「斜坡按帧走，淡出走完窗才落下」那一节：淡出走完窗才落下），不是「淡出刚开始」。
        // `fader.Landed` 收齐就是淡出走完那一刻（`OnLowered` → `Leave` → `Landed`）；
        // 屏上那一扇在那之后才排队销毁。所以这里等两件事都成真。
        foreach (var fader in _faders)
            if (GodotObject.IsInstanceValid(fader) && !_landed.Contains(fader))
            {
                Log("app", "这一批遮罩还在淡出没走完：这一发不建新的，等它走完（原来这里会闪）");
                return;
            }
        foreach (var fader in _faders) fader.QueueFree();
        _faders.Clear();
        _landed.Clear();
        _waitingForLand = false;
        SyncTray();

        // **把 `OnRaised` 让路的那一次补上**（工单 desktop-comfort/05）：那一发当时屏上
        // 还有旧的几扇在退，所以没建。旧的几扇现在退了，这一发才轮到它。
        if (_pendingRaise is { } pending)
        {
            _pendingRaise = null;
            _resumedPendingRaise = true;
            Log("app", "淡入：上一批退完了，把刚才让路的那一次举起来");
            OnRaised(pending);
            _resumedPendingRaise = false;
            return;
        }

        // **写记录的唯一闸门：只有「休息过」和「跳过」两档进记录**（`Scheduler.Outcome`）。
        // 另两档早退 —— 预览压根不是休息，「进入下个时间段」是作息表换块的那次提示。
        // 原来这一行读的是可空的 <c>bool?</c>：<c>null</c> 把那两档压成一格，
        // 这一侧得回头去问 <c>BlockRaise</c> 才说得出是哪一种（2026-10-14）。
        var outcome = _machine.SnapshotOf(_settings).LastOutcome;
        if (outcome is not (Scheduler.Outcome.Taken or Scheduler.Outcome.Skipped))
        {
            Log("app", "这一次不进记录（预览，或者块到点那一次的提示 —— 都不是休息）");
            return;
        }
        var taken = outcome == Scheduler.Outcome.Taken;
        _history.Push(new History.Record
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Taken = taken,
        });
        var path = Path.Combine(_dataDir, History.FileName);
        try
        {
            Directory.CreateDirectory(_dataDir);
            _history.Save(path);
            Log("app", $"记下了一次{(taken ? "休息" : "跳过")}：{path}（现在 {_history.Records.Count} 条）");
        }
        catch (Exception e)
        {
            Log("app", $"记不下来 {path}: {e.Message}");
        }
    }

    private List<int> Screens()
    {
        var count = DisplayServer.GetScreenCount();
        if (!_settings.AllMonitors) count = 1;
        var list = new List<int>();
        for (var screen = 0; screen < count; screen++) list.Add(screen);
        return list;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }) Quit();
    }

    // ---- 托盘那三颗点得动的 -----------------------------------------------------

    /// 预览不是一段休息，所以「进入休息」按下去的那一次不进记录；收掉的如果是闹钟自己
    /// 举起来的真休息，那就和遮罩上那颗「提前结束休息」同一条路，按休息记账。
    private void TogglePreview()
    {
        if (_machine.SnapshotOf(_settings).Phase == Scheduler.Phase.Shown) _machine.EndBreak(_activity.NowMs, _settings);
        else _machine.RaisePreview(_activity.NowMs, _settings);
    }

    private void ToggleAutoFade()
    {
        _settings.AutoFade = !_settings.AutoFade;
        var written = SettingsStore.Save(_dataDir, _read);
        _read = written.State;
        SyncTray();
        Log("tray", $"自动淡入改成 {_settings.AutoFade}，写回 {(_read.Error ?? "OK")}" +
                    (written.Error == null ? "" : $"（没写进去：{written.Error}）"));
    }

    /// 托盘那一下「番茄钟」。开和关都是运行期的事，不落盘 ——
    /// 落盘的是 <c>pomodoro.workMin</c>（专注段多长）和 flows 里那一行（休息什么形状），
    /// 它们在设置窗里改。「跑不跑」是这一发此刻在做什么，不是这台机器该怎么配置。
    private void TogglePomodoro()
    {
        _machine.TogglePomodoro(_activity.NowMs, _settings);
        _pomodoroTip = null;   // 换了一档，tooltip 上那半句得重新算
        SyncTray();
        var snapshot = _machine.SnapshotOf(_settings);
        if (!snapshot.Pomodoro)
        {
            Log("tray", $"番茄钟停了，已经走了 {snapshot.PomodoroRounds} 轮；" +
                        $"连续输入那条触发按休息延迟的第一格之后再接手");
            if (snapshot.Phase == Scheduler.Phase.Shown)
                Log("tray", "停的时候脚下这一轮休息还在走 —— 停的是专注段，不是已经抬起来的那次休息");
            return;
        }
        var row = _settings.Flows.ContainsKey(Settings.FlowPomodoro)
            ? $"flows 里的 {Settings.FlowPomodoro}"
            : $"flows 里没有 {Settings.FlowPomodoro} 这一行，回落到 flow 指着的那条";
        Log("tray", $"番茄钟开始：专注 {_settings.Pomodoro.WorkMin} 分钟一轮，休息走 {row} " +
                    $"那 {_settings.PomodoroSteps().Count} 步");
    }

    /// tooltip 上「还剩 m:ss」那半句上一次是什么。null = 还没算过（第一帧必递一次）。
    private string? _pomodoroTip;

    /// tooltip 上那半句**换档**了没有（跑着 ↔ 没跑）。每秒变的那半句不报 —— 一次番茄钟跑
    /// 二十分钟就是一千二百行日志，报的是噪音不是实测结果。翻面才是托盘上唯一一句会变的文案
    /// 换了一档的时刻，也就是唯一值得留一行的时刻。
    private bool _pomodoroTipShown;

    private void RefreshPomodoroTip()
    {
        if (_tray == null) return;
        var left = _machine.PomodoroFocusLeftMs(_activity.NowMs);
        var shown = left > 0 ? Tray.Clock(left) : "";
        if ((left > 0) != _pomodoroTipShown)
        {
            _pomodoroTipShown = left > 0;
            Log("tray", left > 0
                ? $"tooltip 换成番茄钟那一句：{Tr("Keeper — 番茄钟")}  {shown}"
                : "tooltip 换回休息提醒那一句（番茄钟停了，或者休息已经抬起来 —— 那时候屏幕上有钟）");
        }
        if (shown == _pomodoroTip) return;
        _pomodoroTip = shown;
        SyncTray();
    }

    /// 该挂哪一张：遮罩在屏上是 Fading，否则 autoFade 关着就是 Disabled。
    /// 抽出来是因为<b>起步那一次要在构造之前就知道答案</b> —— 原来构造里写死 Idle，
    /// 然后连调两次 SyncTray() 去盖它，其中一次纯为这件事，而 NIM_ADD 那一下已经挂过一张错的了。
    private Tray.Mode TrayMode() =>
        _machine.SnapshotOf(_settings).Phase == Scheduler.Phase.Shown ? Tray.Mode.Fading
            : _settings.AutoFade ? Tray.Mode.Idle : Tray.Mode.Disabled;

    private void SyncTray() => _tray?.SetMode(TrayMode());

    private void Quit()
    {
        // 排在最前面：不管这一发是怎么走到退出的，桌上那些窗先还回去。
        // 扫桌面那一路的状态在测量程序那边（`--sweep` 才会扫过），没扫过它自己就返回了。
        _shots?.RestoreDesk("退出");
        // 借来的那一扇也要还回去。**不还就是把壳那一扇永久改了** ——
        // 不还回去那一扇就带着日历那一块的矩形和层叠样式留在桌面层里。
        if (_calendar is { } calendar && IsInstanceValid(calendar))
            Log("calendar", $"退出 {calendar.Release()}");
        // 三条退出路（托盘的「退出」、`--seconds`、Esc）都走这一句，所以口关在这一句就够了。
        _port?.Dispose();
        _port = null;
        _tray?.Dispose();
        _tray = null;
        _activity.Stop();
        Log("app", "退出");
        GetTree().Quit(0);
    }

    /// <summary>写一行日志。实施在 <see cref="LogWriter"/> 里 —— 2026-09-29 之前这一份和
    /// 测量程序、遮罩测量程序那两份是三份各写各的，而两道保护（落单的代理对换成 '?'、编码钉成
    /// 不带 BOM 的 UTF-8）只有这一份有。那两道堵的是 2026-09-27 那次「一行日志把启动断在半路」，
    /// 理由写在 <see cref="LogWriter"/> 的类型注释里。</summary>
    private static void Log(string step, string message) => LogWriter.ToApp(step, message);
}
