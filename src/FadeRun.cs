using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using System.Threading.Tasks;

namespace Keeper;

/// <summary>遮罩的测量程序：举一次真的淡入淡出，把设计文档 `docs/design/fader-window.md` 那四条「不许」变成日志里的数。
///
/// ```
/// Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/FadeRun.tscn
/// ```
///
/// 加 <c>-- --all</c> 每块屏一个遮罩，<c>-- --hold 4</c> 落定之后多停几秒。
/// 读设置和读休息记录都是只读，写只写 .tmp。日志追加在 .tmp\keeper-probe.log，
/// 和测量程序同一份，这样「四组判据」和「四条不许」能在同一处按时间读下来。</summary>
public partial class FadeRun : Node
{
    private static string LogPath => LogWriter.ProbePath;

    private readonly record struct Measurement(int Screen, string Where, Frames.Sample At);

    private readonly List<Fader> _faders = [];
    private Settings _settings = new();
    private History _history = new();
    private List<nint> _existing = [];
    private HashSet<nint> _ours = [];
    private List<Measurement> _baseline = [];
    private nint _foregroundBefore;
    private bool _landed;
    private readonly List<string> _verdicts = [];
    private readonly List<(int Screen, string Where, Vector2I Point)> _samplePoints = [];
    private bool _pointsChosen;
    private string _pointsWhy = "";
    private string _styleProfile = "none";

    public override async void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var hold = Array.IndexOf(args, "--hold");
        var holdSeconds = 3.0;
        if (hold >= 0 && hold + 1 < args.Length) double.TryParse(args[hold + 1], out holdSeconds);
        // **样式档位**：把候选那几位手动加到铺满那一扇上（见 StyleProfile）。
        var style = Array.IndexOf(args, "--style");
        if (style >= 0 && style + 1 < args.Length) _styleProfile = args[style + 1];

        Log("fade-begin",
            $"godot={Engine.GetVersionInfo()["string"]} pid={System.Environment.ProcessId} " +
            $"args={string.Join(' ', args)}");

        // 只读。休息记录拿磁盘上那份真的，所以遮罩上那一列清单是用户的真数据。
        var live = SettingsStore.LiveDir();
        var read = SettingsStore.Load(live);
        _settings = read.Current;
        if (read.Error != null) Log("fade", "设置那份读不通，照默认值跑：" + read.Error);
        var historyPath = Path.Combine(Tmp.Root,
            $"keeper-fade-history-{System.Environment.ProcessId}.jsonc");
        // 这一发只读，不改名：新版还没启动过的机器上，记录可能还挂在老名字上（见 Records.ReadPath）。
        var realHistory = Records.ReadPath(live, History.FileName, History.LegacyFileName);
        if (File.Exists(realHistory)) File.Copy(realHistory, historyPath, overwrite: true);
        _history = History.Load(historyPath);
        Log("fade",
            $"淡入 {read.Current.FadeInMs}ms → 淡出 {read.Current.FadeOutMs}ms，最暗 " +
            $"{read.Current.MaxOpacityPercent}%，底色 {read.Current.BackgroundColor}，flow={read.Current.Flow}；" +
            $"休息记录 {historyPath}");

        try
        {
            await ParkOurOwnWindows();
            RaiseFaders();
            // **样式档位紧跟着举起**（不多等一帧）：窗已经 `Show()` 了，
            // 而这一族踩过「设完样式再改别的」那一发，所以这一刀只能这么一刀切完。
            await StyleProfile();
            await MeasureOneFade(holdSeconds);
        }
        catch (Exception e)
        {
            Log("fade-fatal", e.ToString());
        }

        foreach (var verdict in _verdicts) Log("fade-verdict", verdict);
        Unpark();
        try { File.Delete(historyPath); } catch (IOException) { }
        GetTree().Quit(0);
    }

    // ---- 把注意力只留在遮罩上 ------------------------------------------------

    /// 引擎那个主窗口也属于本进程。它要还在那儿，「点得到下面吗」和「透得过桌面吗」两个
    /// 检查都会数到它自己头上，所以先藏起来 —— 顺手再量一次 SW_HIDE 真的藏得住。
    private async Task ParkOurOwnWindows()
    {
        _existing = Win32.OwnTopLevelWindows();
        _foregroundBefore = Win32.GetForegroundWindow();
        var parked = 0;
        foreach (var hwnd in _existing)
            if (Win32.IsWindowVisible(hwnd) && Win32.ShowWindow(hwnd, Win32.SwHide)) parked++;
        await Frame(0.4);
        Log("fade-park", $"藏起 {parked} 个本进程的窗口（引擎的主窗口），还看得见 {StillVisible()} 个");
        Report("a parked window is really gone from the pixel", StillVisible() == 0, $"{parked} 个都藏了");

        // 采样点在举起之前就定死：基线那一次读到的是桌面，还没有遮罩，
        // 可它和后面几次必须是同一个点。挑的时候只用活设置里第一步那份布局。
        _pointsChosen = ChooseSamplePoints(_settings.Steps()[0]);
        _baseline = Sample("桌面基线（还没有任何遮罩）");
    }

    private int StillVisible()
    {
        var visible = 0;
        foreach (var hwnd in _existing) if (Win32.IsWindowVisible(hwnd)) visible++;
        return visible;
    }

    private List<int> Screens()
    {
        var count = DisplayServer.GetScreenCount();
        if (!_settings.AllMonitors || Array.IndexOf(OS.GetCmdlineUserArgs(), "--all") < 0) count = 1;
        var list = new List<int>();
        for (var screen = 0; screen < count; screen++) list.Add(screen);
        return list;
    }

    /// 采样点向布局要，不按四分位取死点。
    ///
    /// 「alpha 按 8 位混」问的是**纯色底**混过去对不对 —— 那一点上除了 Fill 什么都不许有。
    ///
    /// **为什么不再取死点**（2026-10-07）：清单那一列默认占屏宽 62%~96%（`StepLayout.Default`），
    /// 3/4 宽那一点正落在它里头。实测读到的是白字压绿底 (207,231,219)，拿纯色底那个期望值
    /// 去比，逐通道差 143。红的是「那一点上有字」，不是混色错了 —— 1/4 那一处差 0，
    /// 混色实现本身对得上。（`StepLayout.cs` 里记的同一个 143 是清单框按内容高度摆那一版，
    /// 框改回满高之后这一点上仍然有字，所以那一条一直红着。）
    ///
    /// **躲哪些框**：这一步布局里每一个**会画东西**的控件。图控件路径为空时压根不画，不算 ——
    /// 默认布局里那个铺满整屏的图控件就是空路径，算进去就一处都找不着了。
    ///
    /// **取离屏心最近的那个**：边缘那几个像素可能落在遮罩窗口自己的边界上，屏心那一带稳当些。
    private static readonly int ClearMarginPx = 16;
    private const int ScanStepPx = 8;

    private bool ChooseSamplePoints(Step step)
    {
        _samplePoints.Clear();
        foreach (var screen in Screens())
        {
            var position = DisplayServer.ScreenGetPosition(screen);
            var size = DisplayServer.ScreenGetSize(screen);
            var busy = new List<Rect2I>();
            foreach (var shape in step.Layout)
            {
                if (shape.Kind == StepLayout.KindImage && string.IsNullOrEmpty(shape.File ?? "")) continue;
                var placed = shape.Place(size.X, size.Y);
                busy.Add(new Rect2I(
                    position + new Vector2I((int)placed.LeftPx, (int)placed.TopPx),
                    new Vector2I((int)placed.WidthPx, (int)placed.HeightPx))
                    .Grow(ClearMarginPx));
            }

            var centre = new Vector2(size.X / 2f, size.Y / 2f);
            var best = Vector2I.Zero;
            var bestDistance = double.MaxValue;
            for (var y = ClearMarginPx; y + ClearMarginPx < size.Y; y += ScanStepPx)
                for (var x = ClearMarginPx; x + ClearMarginPx < size.X; x += ScanStepPx)
                {
                    var point = position + new Vector2I(x, y);
                    var covered = false;
                    foreach (var box in busy)
                        if (box.HasPoint(point)) { covered = true; break; }
                    if (covered) continue;
                    var distance = new Vector2(x, y).DistanceTo(centre);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = point;
                }

            if (bestDistance == double.MaxValue)
            {
                _pointsWhy = $"屏{screen}（{size.X}×{size.Y}）上这一步的布局摆得满屏都是，" +
                    "找不出一处只剩纯色底的点，那一问答不了";
                return false;
            }
            _samplePoints.Add((screen, "纯色底", best));
            Log("fade-sample", $"屏{screen} 取样点 {best}，布局里那 {busy.Count} 个框都躲开了");
        }
        return true;
    }

    private void RaiseFaders()
    {
        var plan = _settings.Steps();
        // 测量程序自己造一份「现在在第一步」。产品那一份是状态机在 Raised 事件上递出来的，
        // 而这一发不走状态机 —— 它量的是遮罩那一步的形状，不是等一刻钟。
        // 「这一步留多久」仍然问 StageMs，不自己算第二遍。
        var first = new Scheduler.StageNow(plan[0], Scheduler.StageMs(plan, 0, _settings),
            0, plan.Count, plan.Count <= 1);

        foreach (var screen in Screens())
        {
            // 清单读 %APPDATA% 那份真的，一条都不写回去：这个测量程序量的是淡入，不是勾格子。
            var fader = new Fader(_settings, screen, Todos.Load(Records.ReadPath(
                    SettingsStore.LiveDir(), Todos.FileName, Todos.LegacyFileName)),
                () => { }, () => DateTime.Now, message => Log("fader", message));
            fader.Landed += () => _landed = true;
            AddChild(fader);
            fader.Raise(first);
            _faders.Add(fader);
        }

        Log("fade-raise",
            $"举起 {Screens().Count} 块屏，第一个 step：ms={plan[0].Ms} dismissible={plan[0].Dismissible} " +
            $"interactive={plan[0].Interactive} content={string.Join('+', plan[0].Content)}");
    }

    /// 遮罩的 HWND 要等操作系统真把窗口建出来 —— 那是 Show 之后的下一帧，所以在这里数，
    /// 不在举起的那一刻数。举起之后本进程多出来的那几个就是我们的：不靠标题猜 —— 编辑器在跑
    /// 的时候引擎会往标题后面加 " (DEBUG)"，这一点测量程序早就量过。
    ///
    /// **2026-10-10 不再要扣掉哪一扇**：原来这里有一扇收鼠标的点击窗，它也是我们的，
    /// 而「每块屏一扇窗」那几条判据数的是铺满那扇。拆掉它之后这一族一屏一扇，
    /// `now` 原样就是 `_ours`。
    private void FindOurWindows()
    {
        var now = new HashSet<nint>(Win32.OwnTopLevelWindows());
        foreach (var hwnd in _existing) now.Remove(hwnd);
        _ours = now;
        foreach (var hwnd in _ours) MakeToolWindow(hwnd);
        Log("fade-window",
            $"我们的 HWND={Describe(_ours)}，引擎说这块屏支持逐像素透明=" +
            $"{DisplayServer.IsWindowTransparencyAvailable()}");
        foreach (var hwnd in _ours)
        {
            Win32.GetWindowRect(hwnd, out var rect);
            Log("fade-window",
                $"0x{hwnd:X} '{Win32.ClassName(hwnd)}' 可见={Win32.IsWindowVisible(hwnd)} 矩形={rect} " +
                $"样式={Win32.DescribeStyle(Win32.GetWindowLong(hwnd, Win32.GwlStyle))} " +
                $"扩展={Win32.DescribeExStyle(Win32.GetWindowLong(hwnd, Win32.GwlExStyle))}");
            Log("fade-window", $"  它上面：{Win32.Above(hwnd)}");
        }
    }

    /// 一个真的在屏幕上的遮罩：看得见、不是 0 大小、扩展样式里有 TOPMOST。少这一条，
    /// 「像素里怎么没变化」就没有答案 —— 采样只能证明屏幕上没东西，证明不了窗口没上来。
    private bool AllOnScreen()
    {
        if (_ours.Count != _faders.Count) return false;
        var everyOne = true;
        foreach (var hwnd in _ours)
        {
            Win32.GetWindowRect(hwnd, out var rect);
            var ex = (long)Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
            everyOne &= Win32.IsWindowVisible(hwnd) && rect.Width > 100 && rect.Height > 100
                && (ex & Win32.WsExTopmost) != 0;
        }
        return everyOne;
    }

    private static string Describe(IEnumerable<nint> handles)
    {
        var into = new List<string>();
        foreach (var hwnd in handles) into.Add($"0x{hwnd:X}");
        return into.Count == 0 ? "none" : string.Join(' ', into);
    }

    /// 不进任务栏、不进 Alt+Tab。样式位只在这里动一次：在举起之后、在别的一切之前。
    private static void MakeToolWindow(nint hwnd)
    {
        var ex = (long)Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
        ex = ex | Win32.WsExToolwindow & ~Win32.WsExAppwindow;
        Win32.SetWindowLong(hwnd, Win32.GwlExStyle, (nint)ex);
    }

    // ---- 一次淡入淡出，四条判据 ----------------------------------------------

    private async Task MeasureOneFade(double holdSeconds)
    {
        await Frame(0.02);
        FindOurWindows();
        var opened = Sample("举起后约一帧");
        var alpha = _faders.Count == 0 ? -1f : _faders[0].Opacity;
        Report("the fade does not appear in a jump", alpha >= 0 && alpha <= 0.03f,
            $"举起约一帧之后 alpha={alpha:F4}");
        Report("every screen has a window of ours on it", _ours.Count == _faders.Count,
            $"举起 { _faders.Count} 块屏，拿到 { _ours.Count} 个 HWND");
        Report("those windows are really on screen", AllOnScreen(), "见 fade-window 那几行");
        Report("the desktop reads through while it is still clear",
            WorstDifference(opened, _baseline) <= 8, "a 还挂在 0 的时候采的");
        Focus("举起约一帧");
        Passthrough(_settings.Steps()[0], "淡入中", false);

        await Frame(_settings.FadeInMs / 2000.0);
        Sample("淡入到一半");

        await Frame(_settings.FadeInMs / 1000.0);
        var arrived = Sample("淡入到位");
        var top = _faders[0].Opacity;
        var target = _settings.MaxOpacityPercent / 100f;
        var step = _faders[0].MaxAlphaStep;
        Log("fade-ramp",
            $"到位：alpha={top:F4}，斜坡走了 {_faders[0].RampFrames} 帧，单帧最大跳 {step * 255:F1} 级");
        Report("the fade reaches maxOpacityPercent", MathF.Abs(top - target) < 0.02f,
            $"到位时 alpha={top:F4}，该是 {target:F2}");
        Report("the ramp moves a few levels per frame, never a chunk",
            _faders[0].RampFrames > 20 && step <= 0.06f,
            $"{_faders[0].RampFrames} 帧，单帧最大 {step * 255:F1} 级");
        // 挑不出纯色底的点时**先红**：MixError 拿到空列表会返回 0 —— 那是假绿。
        Report("alpha mixes on the 8-bit encoded values",
            _pointsChosen && MixError(arrived) <= 24,
            _pointsChosen
                ? $"按 8 位 lerp 算的期望值和采到的逐通道最大差 {MixError(arrived)}"
                : _pointsWhy);
        Focus("淡入到位");

        var plan = _settings.Steps();
        // 落定之后**先**量一次第一步 —— 这一发一定跑（下面那个换步那一段要 `plan.Count > 1`，
        // 而设置里只有一个步时它整段都不跑）。它是「该收的那一块量出来了没有」那一问唯一
        // 一定跑到的位置。
        Passthrough(plan[0], "落定之后", true);
        if (plan.Count > 1)
        {
            // 同上：测量程序自己造「现在在第二步」，留多久仍然问 StageMs。
            var second = new Scheduler.StageNow(plan[1], Scheduler.StageMs(plan, 1, _settings),
                1, plan.Count, 2 >= plan.Count);
            foreach (var fader in _faders) fader.Enter(second);
            Log("fade-step",
                $"进第二个 step：ms={plan[1].Ms} interactive={plan[1].Interactive} " +
                $"content={string.Join('+', plan[1].Content)}（不碰窗口：不闪、不重排、不抬起来）");
            Passthrough(plan[1], "落定之后那一步", true);
        }
        await Frame(holdSeconds);

        _landed = false;
        foreach (var fader in _faders) fader.Leave();
        await Frame(_settings.FadeOutMs / 2000.0);
        Sample("淡出到一半");
        await Frame(_settings.FadeOutMs / 1000.0);
        Report("the fade-out finishes before the window comes down", _landed,
            $"淡出走完之后 alpha={_faders[0].Opacity:F4}，Landed 收到={_landed}");

        foreach (var fader in _faders) fader.QueueFree();
        _faders.Clear();
        await Frame(0.5);
        var after = Sample("窗口还回去之后");
        Report("the desktop is itself again once the fade is gone",
            WorstDifference(after, _baseline) <= 8, "拿基线比的");
    }

    /// <summary>把候选那几位手动加到铺满那一扇上，并把「这一扇自己怎么说」读回来。
    ///
    /// <para><b>问句</b>：引擎那条 <c>MousePassthrough</c> 走的是 <c>WM_NCHITTEST</c>，
    /// 而 <c>HTTRANSPARENT</c> <b>只跨同一个线程</b>。2026-10-10 晚量到的读数：
    /// 遮罩确实盖住那一点（跨进程 z 序读数说那一扇在最上面），接收方在<b>另一个进程</b>里、
    /// 是<b>普通顶层窗</b>（置顶那一位已经退干净），连着 16 发真的左键
    /// **全部 0/1**，而同布置无遮罩是 1/1。**遮罩自己答 <c>WM_NCHITTEST</c> 是 -1
    /// —— 它在声明「不是我」，而跨进程时底下那扇什么也没拿到。**
    ///
    /// <para><b>这一发逐个试另外那几条路</b>：<c>WS_EX_TRANSPARENT</c>、
    /// <c>WS_EX_LAYERED</c>、两个一起。<b>跨进程那一头不由这一发答</b> ——
    /// 由 <c>Probe --passthrough-target --selfclick --plain</c>（另一个进程）答。
    /// 这一发只管把样式摆上去，并盯住两件会连带坏掉的事：<b>窗还在不在、遮罩还画不画得出来</b>
    /// （<c>WS_EX_LAYERED</c> 和引擎那份 DirectComposition 的每像素透明是不是打架）。
    private async Task StyleProfile()
    {
        var add = _styleProfile switch
        {
            "none" => 0L,
            "transparent" => Win32.WsExTransparent,
            "layered" => Win32.WsExLayered,
            "both" => Win32.WsExTransparent | Win32.WsExLayered,
            _ => -1L,
        };
        Log("fade-style", $"档位={_styleProfile} 要加的位=0x{add:X}");
        if (add < 0)
        {
            Log("fade-style", $"档位 '{_styleProfile}' 认不出 —— 原样跑");
            return;
        }
        // **`none` 那一档一步都不做**：这一发是从举起到举起来那几帧的**正中**插进来的，
        // 而 `SetWindowPos(..., SWP_FRAMECHANGED)` 要重算整扇 2560×1440 —— 首帧被拖到
        // 约 300 毫秒，而斜坡按 `delta` 走，于是「举起约一帧之后」alpha 读到 0.10 而不是
        // 0.0000，「the fade does not appear in a jump」当场由 holds 变 DOES NOT HOLD
        // （2026-10-11 撞的：**一度以为是产品那两位贵**，去掉重算那一发也不解决，
        // 最后是量具自己那一发）。**要什么都别动就得真的什么都别发。**
        if (add == 0)
        {
            Log("fade-style", "none：不发任何一发，样式位由产品自己那一段负责");
            return;
        }
        await Frame(0.2);
        // **按屏号对号入座**：`_faders` 是照 `Screens()` 的次序建的，
        // 而「屏号 → 那一扇窗的标题」只有这一份出处（`Fader.TitleOf`）。
        // 引擎没有「问一扇窗它在第几块屏」这一句，所以不在这儿自己算。
        var screens = Screens();
        for (var i = 0; i < _faders.Count && i < screens.Count; i++)
        {
            var fader = _faders[i];
            var screen = screens[i];
            var hwnd = Win32.OwnWindowNamed(Fader.TitleOf(screen));
            if (hwnd == IntPtr.Zero)
            {
                Log("fade-style", $"屏 {screen} 认不出自己的窗 —— 这一档量不成");
                continue;
            }
            var before = Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
            Win32.SetWindowLong(hwnd, Win32.GwlExStyle, (nint)(before | add));
            // **改完样式位要让它生效**：动的是窗口过程之外的东西，
            // 不发一发 SWP_FRAMECHANGED 的话读回来是「我写了」，屏上还是老样子。
            Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate | Win32.SwpFrameChanged);
            var after = Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
            // **问这一扇自己「这一点归不归你」**：它答 -1 也不等于底下的别的程序收得到
            // （同线程才是那样），所以这一句只是**它自己怎么说**，跨进程那一头在别处。
            var centre = fader.Centre;
            var lp = (nint)(((long)(uint)centre.Y << 16) | (uint)centre.X);
            var told = Win32.SendMessage(hwnd, Win32.WmNchitTest, IntPtr.Zero, lp).ToInt64();
            Log("fade-style",
                $"屏 {screen} 0x{hwnd.ToInt64():X} 扩展 写之前={before:X} 写进去={before | add:X} " +
                $"读回来={after:X} ({Win32.DescribeExStyle(after)}) OS说可见={Win32.IsWindowVisible(hwnd)} " +
                $"**问它自己** ({centre.X},{centre.Y}) 答={told}" +
                (told == Win32.HtTransparent ? "（HTTRANSPARENT：不是它的）" : ""));
        }
        await Frame(0.3);
    }

    /// 每一次采样都走**同一批点**（<see cref="ChooseSamplePoints"/> 挑一次就定死）：
    /// 「跟基线比」那几条两头必须是同一个点，点要是每回现挑、比的就不是同一处了。
    private List<Measurement> Sample(string when)
    {
        var alpha = _faders.Count == 0 ? 0 : _faders[0].Opacity;
        var read = new List<Measurement>();
        foreach (var (screen, where, point) in _samplePoints)
        {
            var pixel = Frames.Shot(new Win32.Point { X = point.X, Y = point.Y });
            read.Add(new Measurement(screen, where, pixel));
            Log("fade-sample", $"{when} 屏{screen} {where} a={alpha:F3} 采到 {pixel} 位置={point}");
        }
        return read;
    }

    /// 到位那一刻该看到什么：底色按 alpha 和桌面混。混的是 8 位编码值 —— 这一条是
    /// docs/research/probe-and-criteria-pitfalls.md「判据里那个本来该是什么」那一少量的（逐通道差 0），不是推的。
    /// 混色那份实施搬到 <see cref="Frames.Blend"/> 去了（2026-09-29）：原来这一份走的是
    /// <c>fill.R * 255</c>，也就是 <c>(byte/255f)*255f</c>，跟测量程序那份直接用字节的写法在 float 下
    /// 不是显然相等的 —— <c>Frames.SelfTest</c> 里那片 256×256×101 的扫描就是钉这一条的。
    private int MixError(List<Measurement> arrived)
    {
        var alpha = _settings.MaxOpacityPercent / 100f;
        var fill = Fader.Rgb(_settings.BackgroundColor);
        // Color 那三个通道是 byte/255f，乘回来在 float 下差不到 1e-4，四舍五入就是原来那个字节。
        // **不能**直接 (byte) 截断：那会在某些字节上少 1，于是搬家搬出一条新实测结果来。
        var above = new Win32.Pixel((byte)Math.Round(fill.R * 255f),
            (byte)Math.Round(fill.G * 255f), (byte)Math.Round(fill.B * 255f));
        var worst = 0;
        foreach (var measurement in arrived)
        {
            if (BaseOf(measurement) is not { } from) continue;
            // 没读到不是「误差 0」—— 原来那一份会拿 CLR_INVALID 折出来的 (0,0,0) 当桌面底色混，
            // 混出一个看着合法的期望值，再跟另一个 (0,0,0) 比出差 0。这一发让它失败关闭。
            if (!from.Read || !measurement.At.Read) return int.MaxValue;
            var expected = Frames.Blend(from.Pixel, above, alpha, Frames.Model.Srgb8);
            worst = Math.Max(worst, Frames.Gap(expected, measurement.At.Pixel));
        }
        return worst;
    }

    private Frames.Sample? BaseOf(Measurement measurement)
    {
        foreach (var item in _baseline)
            if (item.Screen == measurement.Screen && item.Where == measurement.Where) return item.At;
        return null;
    }

    private int WorstDifference(List<Measurement> a, List<Measurement> b)
    {
        var worst = 0;
        foreach (var measurement in a)
        {
            foreach (var item in b)
                if (item.Screen == measurement.Screen && item.Where == measurement.Where)
                    worst = Math.Max(worst, Frames.Gap(item.At, measurement.At));
        }
        return worst;
    }

    /// 「点得到的那几块点得动、底下照常用」这一族量的是两件事，不是一件：
    /// 铺满那扇正中间那一点**不**归我们（它一辈子穿过），而「点得到的那几块」那一块
    /// <b>不是任何一扇窗</b> —— 那一块归钩子按坐标判，所以它中间那一点在命中测试上
    /// 也不归我们。
    ///
    /// 撤过的那两版：先是「遮罩正中间那一点在收鼠标的那一步归我们」（那意味着底下每一像素
    /// 都点不到），后是「那一块归那一扇点击窗」（那一扇罩进去的每一像素底下也都点不到 ——
    /// 2026-10-10 所有者定夺「永远都不能抢鼠标，这是 bug」）。所以那两条都留着，
    /// 只是第二条换了个问法：**那一块上归不归我们，答案必须是「不归」** ——
    /// 归了就是又有一扇窗在罩着它，而那正是要治的病。
    private void Passthrough(Step step, string when, bool afterSettle)
    {
        var maskOwns = 0;
        var armed = 0;
        var regionOwnedByUs = 0;
        foreach (var fader in _faders)
        {
            var centre = new Win32.Point { X = fader.Centre.X, Y = fader.Centre.Y };
            var hit = Win32.WindowFromPoint(centre);
            var root = hit == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestorRoot(hit);
            if (_ours.Contains(root)) maskOwns++;
            Log("fade-click",
                $"{when}：铺满那扇正中间的点归 0x{hit:X} '{Win32.ClassName(hit)}'，顶层 " +
                $"0x{root:X}/{Win32.ClassName(root)} 是我们的={_ours.Contains(root)}");

            // 「点得到的那几块」那一块（空 = 这一步不收鼠标）。
            var region = fader.SwallowRect;
            Log("fade-click",
                $"{when}：点得到的那几块={region} 收没收={fader.Armed} 交互={fader.Interactive} " +
                fader.SwallowMeasurement());
            if (!fader.Armed) continue;
            armed++;
            var at = new Win32.Point
                { X = region.Position.X + region.Size.X / 2, Y = region.Position.Y + region.Size.Y / 2 };
            var inHit = Win32.WindowFromPoint(at);
            var inRoot = inHit == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestorRoot(inHit);
            var mine = _ours.Contains(inRoot);
            if (mine) regionOwnedByUs++;
            Log("fade-click",
                $"{when}：那一块正中间 ({at.X},{at.Y}) 归 0x{inRoot:X}/{Win32.ClassName(inRoot)} " +
                $"那一块上有我们的窗吗={mine}（那一块归钩子判，不归窗）");
        }
        Report("the fullscreen fade never owns a click: it is click-through in every step",
            maskOwns == 0, $"{maskOwns}/{_faders.Count} 块屏的铺满那扇正中间那一点归我们");
        ClickThroughBitsHeld(when);
        // 「那一块上不许有我们的窗」这一头**什么时候问都该成立**，它量的就是有窗那一版的病。
        //
        // 「那一块非空」这一头**只在落定之后问**，而且只在**这一步声明收鼠标**的时候问 ——
        // 第一版不问落没落定，2026-10-10 那一发就在「淡入中」量到了它：那时还没落定，
        // 那一块当然空，判据当场报红。**量到的是判据站错了位置，不是产品那一块没量出来。**
        var wanted = afterSettle && step.Interactive ? _faders.Count : 0;
        Report("only the hook takes the mouse: the click region is not a window of ours",
            regionOwnedByUs == 0 && armed == wanted,
            $"{regionOwnedByUs}/{_faders.Count} 块屏那一块上有我们的窗，{armed}/{wanted} 块屏那一块非空，" +
            $"这一步 interactive={step.Interactive} 落定之后={afterSettle}（{when}）");
    }


    /// <b>那两位还在不在</b>——<c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>。
    ///
    /// <para><b>它和上面那条不是同一件事</b>：上面那条量的是「<c>WindowFromPoint</c> 答不答
    /// 这一扇」（进程内口径，走 <c>WM_NCHITTEST</c>）；这一条量的是<b>那两位扩展样式位本身</b>。
    /// 跨进程底下收不收得到靠的是后者，而 2026-10-11 那次修复加的就是后者 ——
    /// <b>上面那条当时一路读绿，而底下 16 发真点击全部没到</b>。
    ///
    /// <para><b>它要抓的回归是「被清掉」</b>：这一族踩过「样式位一次设完、之后不碰」，
    /// 而引擎会不会在某一发（换步、落定、让位、某次重排）顺手把那两位抹回去，
    /// **只有把位读回来才答得出来**。位还在不等于点击一定通（跨进程那一头是
    /// <c>Probe --passthrough-target --selfclick --plain</c> 那一发量的），
    /// 位没了则一定不通 —— 所以这条是**必要条件那一头**，不是全部。
    private void ClickThroughBitsHeld(string when)
    {
        var screens = Screens();
        var held = 0;
        var detail = new List<string>();
        for (var i = 0; i < _faders.Count && i < screens.Count; i++)
        {
            var hwnd = Win32.OwnWindowNamed(Fader.TitleOf(screens[i]));
            if (hwnd == IntPtr.Zero)
            {
                detail.Add($"屏 {screens[i]} 认不出自己的窗");
                continue;
            }
            var ex = Win32.GetWindowLong(hwnd, Win32.GwlExStyle);
            if (Fader.ClickThroughBitsOn(ex)) held++;
            detail.Add($"屏 {screens[i]} {ex:X}({Win32.DescribeExStyle(ex)})");
        }
        Log("fade-click", $"{when}：那两位 = {string.Join(' ', detail)}");
        Report("the fullscreen fade keeps the cross-process click-through bits",
            held == _faders.Count && _faders.Count > 0,
            $"{held}/{_faders.Count} 块屏的扩展样式位上那两位还在（{string.Join(' ', detail)}）");
    }

    private void Focus(string when)
    {
        var now = Win32.GetForegroundWindow();
        Log("fade-focus",
            $"{when}：前台窗口 0x{now:X} '{Win32.ClassName(now)}'，举起之前是 0x{_foregroundBefore:X}/" +
            $"{Win32.ClassName(_foregroundBefore)}");
        Report("the fade never takes the keyboard focus",
            _foregroundBefore == IntPtr.Zero || now == _foregroundBefore || !_ours.Contains(now),
            $"前台从 0x{_foregroundBefore:X} 变成了 0x{now:X}（其中 0x{now:X} 是我们的={_ours.Contains(now)}）");
    }

    private void Report(string claim, bool holds, string because)
    {
        _verdicts.Add($"{claim}: {(holds ? "holds" : "DOES NOT HOLD")} — {because}");
    }

    private void Unpark()
    {
        foreach (var hwnd in _existing)
            if (!Win32.IsWindowVisible(hwnd)) Win32.ShowWindow(hwnd, Win32.SwShow);
    }

    private async Task Frame(double seconds)
    {
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void Log(string step, string message) => LogWriter.ToProbe(step, message);
}
