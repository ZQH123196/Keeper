using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 工单 09 第三发：双击某格日历，判有没有进编辑态。<b>分两段报</b>。
///
/// <para><b>为什么要分两段</b>：日历那一扇在摆着的时候是挂在壁纸层里的，
/// 那种状态的窗<b>永远拿不到键盘焦点</b>。抬起编辑态才是为了打字。
/// 把两段合成一段，读数会变成「进得去但不聚焦」——
/// 而产品其实是分两段解决的，那句读数会把代价说错。</para>
///
/// <para><b>「进没进编辑态」是产品知识</b>，从屏幕上看不出来，所以这一问走
/// <c>05</c> 那道口（<c>editmode</c> 动词）。
/// <b>「焦点端过来没有」不走那道口</b> —— 判据是 Win32 的前台那一扇，
/// 不看引擎那份「输入框有焦点」（那份在非前台进程上会撒谎）。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/09-product-own-actions.md</c>。</para>
/// </summary>
internal static class EditProbe
{
    /// <summary>两下之间停多久。<b>要短于系统的双击间隔</b>，否则那是两次单击，
    /// 而「摆着的时候单击不进编辑态」—— 那一发就答成了另一件事。</summary>
    private const int DoubleClickGap = 80;

    private const int Settle = 150;

    internal static int Run(string[] args)
    {
        // `--watch N` 那一档：不点鼠标，只盯着产品，问它自己什么时候进了编辑态。
        // 给的是「抬起那一段」—— 由产品自带的 `--lift` 开关抬起，量具负责看焦点。
        int watch = ReadInt(args, "--watch", 0);
        if (watch > 0) return Watch(watch, ReadInt(args, "--wait", 0), ReadText(args, "--away") ?? "app");

        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "双击某格日历进编辑态（分两段报）",
            "收着的那一段：在摆着的时候点一格，答「拿不到焦点」是正常的；" +
            "抬着的那一段：抬起之后答 GrabForTyping 到底把 Win32 前台端过来没有。" +
            "两段不许合成一段 —— 合成一段会把代价说错。" +
            "另外：动作前后各读一次图标位置");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish("中止 —— 找不到日历那一扇，这一发答不了", "无");
            return Find.Refused;
        }

        using Port? port = Port.Open();
        if (port == null)
        {
            shot.Finish(
                $"中止 —— 接不上产品那道口，「进没进编辑态」只有产品答得出。" +
                $"试过的管路：{Port.Attempts}。{Port.LastFailure}",
                "无");
            return Find.Refused;
        }

        int cx = 0, cy = 0;
        string which = string.Empty;
        string why;

        // ---- 先挑一个「不在任何一颗桌面图标上」的点 --------------------------------
        //
        // **这一步是被量出来的（2026-10-12）**：日历那一扇现在点不中（`WS_EX_TRANSPARENT`），
        // 点击全靠产品自己的鼠标桥喂；而桥的闸门有一问是「那一点上不能有桌面图标」
        // —— 有就归 shell。量测程序先前取的是日历矩形的正中心 (2048,690)，
        // 而桌面上真有一颗 `Keeper.exe` 图标占着 (2046,670)-(2138,763)，
        // 正中心就落在它上面，于是产品如实把两发点击让给了 shell：
        // **那是闸门在正常工作，不是产品坏了**。换一个不压着图标的点才是这一发该问的。
        //
        // 候选点从左上往右下排，第一个「不压着图标、且产品答得出是哪一格」的就是它。
        (cx, cy, why) = PickCell(calendar, port, out which);

        // 图标位置在动作之前读一遍当底。这一发挑的点已经保证不压着图标，
        // 而「挑的时候不压着」不等于「点完之后没动过」—— 那一问要靠前后两读。
        List<Icons.IconBox> placesBefore = Icons.ReadPlaces("双击之前");

        string beforePath = Path.Combine(outDir, "edit-before.png");
        Archive(calendar, port, beforePath, "点之前");

        // ---- 段一：收着的那一段 ------------------------------------------------
        string editingBefore = port.Ask("editmode");
        string frontBefore = Windows.ForegroundDescription();
        bool isOursBefore = IsOurCalendar(frontBefore);

        // **点之前先问一次鼠标桥**（2026-10-13，05 那道口加的第五个动词）。
        // 有了它，「点击为什么没进去」这一句就在读数里，不必去翻产品日志 ——
        // 而翻日志拿到的那一行不带时间戳对齐，很容易和屏上那一下对不上。
        string bridgeBefore = port.Ask("mouse");

        // ---- 先把鼠标停在那���格上，等产品认出「在格子上」，再点 ------------------
        //
        // **这一步不能省（量于 10-12）**：摆着的日历带 `WS_EX_LAYERED | WS_EX_TRANSPARENT`，
        // 它是**故意点不中的**，物理点击会从它身上穿过去。产品靠自己的鼠标桥
        // （`MouseBridge.ShouldFeed`：摆着 + 在格子上 + 那一点没有桌面图标）
        // 把点击重新喂进引擎。
        // 「移动 + 立刻按下」是不行的 —— 产品按帧认位置，还没认出「在格子上」
        // 那一发点击就白点了，症状是「前台端过来了、编辑态没进去」。
        Input.MoveTo(cx, cy);
        System.Threading.Thread.Sleep(800);
        string frontMoved = Windows.ForegroundDescription();
        string atCell = Windows.ClassOf(Windows.At(cx, cy));

        Input.ClickAt(cx, cy, Settle);
        System.Threading.Thread.Sleep(DoubleClickGap);
        Input.ClickAt(cx, cy, Settle);
        System.Threading.Thread.Sleep(900);

        string afterPath = Path.Combine(outDir, "edit-after.png");
        Archive(calendar, port, afterPath, "点之后");

        // ---- 段二：抬着的那一段 ------------------------------------------------
        string editingAfter = port.Ask("editmode");
        string frontAfter = Windows.ForegroundDescription();
        bool isOursAfter = IsOurCalendar(frontAfter);
        string bridgeAfter = port.Ask("mouse");

        string gate = GateSentence(bridgeBefore, bridgeAfter);

        string segmentOne =
            $"**收着的那一段**：点之前问产品「{editingBefore}」；Win32 前台 {frontBefore} —— " +
            $"{(isOursBefore ? "已经是日历那一扇" : "**不是日历那一扇**（这种状态下拿不到焦点是正常的）")}";

        string segmentTwo =
            $"**抬着的那一段**：先把鼠标停在那一点（命中测试答 {atCell}），等产品的鼠标桥认出「在格子上」；" +
            $"再双击 {cx},{cy} 之后问产品「{editingAfter}」；" +
            $"Win32 前台 {frontAfter} —— " +
            (isOursAfter
                ? "**GrabForTyping 把焦点端过来了**"
                : "**焦点没端过来**（引擎那份「输入框有焦点」不算数，判据是 Win32 前台这一扇）") +
            $"。{gate}";

        bool entered = editingAfter.Contains("editing=1", StringComparison.Ordinal);
        bool gotFocus = isOursAfter;

        shot.Finish(
            $"问的那一点 ({cx},{cy}) 产品答「{which}」—— {why}。" +
            $"{segmentOne}。{segmentTwo}。" +
            $"两段分别报，没有合成一句。判：{(entered ? "进得去" : "**没进去**")}，" +
            $"{(gotFocus ? "并且焦点端过来了" : "焦点没端过来")}。" +
            "点那一格就是产品自己说的那一格，没有反推。" +
            $"图标位置：{Icons.ComparePlaces(placesBefore, Icons.ReadPlaces("双击之后"))}。" +
            "前后两帧都留了档，各带一行归谁",
            afterPath);

        return entered && gotFocus ? 0 : Find.Refused;
    }

    /// <summary>挑一个该问的点：日历矩形之内、<b>不压着任何一颗桌面图标</b>、
    /// 且产品答得出是哪一格。三条全过才算数。</summary>
    /// <param name="why">为什么挑了它 —— 原样进读数，别人要能核。</param>
    private static (int X, int Y, string Why) PickCell(WindowInfo calendar, Port port, out string which)
    {
        // 桌面图标那一层用 UI Automation 读（工单 07 选定的那一条读法）。
        // **不反推产品的状态** —— 问的是「屏幕上有没有图标」，那是量具自己的读数。
        List<Icons.IconBox> icons = Icons.ReadAll(out string note);
        Console.Out.WriteLine($"  图标层：{note}，{icons.Count} 颗");

        var candidates = new List<(int X, int Y, string Why)>();
        RectI rect = calendar.Rect;
        var tried = new List<string>();

        // 先看正中心 —— 它被图标压住的话要说出来，那是这一发为什么换点的原因。
        int mx = rect.Left + rect.Width / 2, my = rect.Top + rect.Height / 2;
        Icons.IconBox? onCenter = icons.FirstOrDefault(b => b.Rect.Contains(mx, my));
        if (onCenter != null)
        {
            tried.Add($"正中心 ({mx},{my}) 压在图标「{onCenter.Value.Name}」{onCenter.Value.Rect} 上");
        }

        foreach ((int x, int y) in Grid(rect))
        {
            if (x == mx && y == my) continue;
            if (icons.Any(b => b.Rect.Contains(x, y))) continue;
            string answer = port.Ask("cell", x.ToString(), y.ToString());
            if (answer.StartsWith("ok ", StringComparison.Ordinal))
            {
                which = answer;
                string skipped = tried.Count > 0 ? $"换点的理由：{string.Join("；", tried)}。" : string.Empty;
                return (x, y, skipped + "这一点不压着任何一颗桌面图标，产品也答得出是哪一格");
            }

            tried.Add($"({x},{y}) 产品答「{answer}」");
            if (tried.Count >= 6) break;
        }

        which = "没挑出来";
        return (mx, my,
            "**没挑出一个不压着图标的点**，用的是正中心 " +
            $"（{string.Join("；", tried)}）—— 这一发按正中心问，判据要照实说它缺前提");
    }

    /// <summary>在矩形里按行排开的候选点。<b>往中间那一带靠</b>，
    /// 图标都排在边上，中间那一带最不容易压着。</summary>
    private static IEnumerable<(int X, int Y)> Grid(RectI rect)
    {
        double[] fractions = { 0.5, 0.35, 0.65, 0.2, 0.8 };
        foreach (double fy in fractions)
        {
            foreach (double fx in fractions)
            {
                yield return (
                    rect.Left + (int)(rect.Width * fx),
                    rect.Top + (int)(rect.Height * fy));
            }
        }
    }

    /// <summary>
    /// 两次 <c>mouse</c> 读数差出来的闸门那句话。
    ///
    /// <para><b>为什么读「丢了几个」而不只是「丢没丢」</b>：丢掉的条数和双击发下去的两发
    /// 对上，说明闸门是<b>每一发都关着</b>；对不上说明只丢了一半 ——
    /// 那两种症状指向的地方不一样，而一句「闸门关着」两种都盖住了。</para>
    /// </summary>
    private static string GateSentence(string before, string after)
    {
        int lost = Port.Counter(after, "丢=") - Port.Counter(before, "丢=");
        int fed = Port.Counter(after, "喂=") - Port.Counter(before, "喂=");
        string reason = after.Length > 0 && after.Length < 400 ? after : "（读数太长，不原样贴）";

        if (fed > 0) return $"两次读数之间闸门喂进去了 {fed} 发，点了也是白点之外那一路收得到。{reason}";
        if (lost > 0) return $"闸门那一头：双击那两发**一发都没喂进去**（丢 {lost} 发，喂 0 发）—— 点进去的东西根本没到日历里。{reason}";
        return $"闸门那一头：丢 {lost} 发、喂 {fed} 发。{reason}";
    }


    private static string? ReadText(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out int value)) return value;
        }

        return fallback;
    }

    /// <summary>
    /// <c>--watch</c> 那一档：**不点鼠标**，只盯着产品什么时候自己进了编辑态。
    ///
    /// <para><b>为什么要有这一档</b>（量于 2026-10-12）：双击那一半量出来了 ——
    /// 产品的鼠标桥把点击全丢了（闸门恒闭，根因是 <c>LVM_HITTEST</c> 处处答 0，
    /// 记在 <c>docs/research/desktop-layer-and-window-measurements.md</c>）。
    /// 那一半坏了不该把另一半也带下水：<b>「抬起之后焦点端过来没有」和产品答得出来，
    /// 而抬起这个动作产品自己带着开关</b>（<c>--lift</c>）。这一档就量那一半。</para>
    ///
    /// <para><b>两段还是分开报</b>，和双击那一档同一个规矩。判据是 Win32 的前台那一扇，
    /// 不是引擎那份「输入框有焦点」。</para>
    /// </summary>
    private static int Watch(int seconds, int waitSeconds, string away)
    {
        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "抬起那一段：产品自己抬起来，答 GrabForTyping 把焦点端过来没有",
            "收着的那一段：摆着的时候看 Win32 前台是谁；抬着的那一段：产品一进编辑态，"+
            "立刻读 Win32 前台是谁。两段不许合成一段 —— 合成一段会把代价说错");

        Port? port = null;
        for (int waited = 0; port == null && waited <= waitSeconds * 10; waited++)
        {
            port = Port.Open();
            if (port == null) System.Threading.Thread.Sleep(100);
        }

        if (port == null)
        {
            shot.Finish(
                $"中止 —— 等了 {waitSeconds} 秒还是接不上产品那道口。" +
                $"试过的管路：{Port.Attempts}。{Port.LastFailure}",
                "无");
            return Find.Refused;
        }

        using (port)
        {
            // **先把前台挪走，再读「收着的那一段」**（量于 2026-10-12）：
            // 产品刚起来的时候前台本来就是它自己那一扇，那一栏读出来「已经是日历」
            // 没有对照 —— 看不出是抬起把它端过来的，还是它一直就在前面。
            // 挪走这一步<b>自己再读一遍前台来判成没成</b>：前台锁会挡，
            // 而那一发不报成功 —— 报成功而实际没动，后面的对照就是假的。
            IntPtr target = away switch
            {
                "desktop" => Windows.DesktopWindow(),
                "app" => Windows.ForeignAppWindow(Port.ConnectedPid),
                _ => IntPtr.Zero,
            };
            Windows.BringToFront(target);
            System.Threading.Thread.Sleep(400);
            string frontAway = Windows.ForegroundDescription();
            bool movedAway = target != IntPtr.Zero && !IsOurCalendar(frontAway);
            string awayText = target == IntPtr.Zero
                ? "没有指定挪到哪一扇，前台保持原样"
                : $"要把前台挪到 0x{target.ToInt64():X}/{Windows.ClassOf(target)}；" +
                  $"挪完读回来是 {frontAway} —— {(movedAway ? "挪走了" : "**没挪走**（前台锁挡了）")}";

            string before = port.Ask("editmode");
            string frontBefore = Windows.ForegroundDescription();
            bool isOursBefore = IsOurCalendar(frontBefore);

            // 盯着问。**问到 editing=1 的那一轮就把前台读下来** —— 抬起只持续几百毫秒，
            // 错过那一轮就只剩「事后」，而事后读到的前台说明不了抬起时焦点在谁那儿。
            string during = "没问到";
            string frontDuring = "没读到";
            bool isOursDuring = false;
            int samples = 0;
            var timeline = new List<string>();

            for (int tick = 0; tick < seconds * 20; tick++)
            {
                string edit = port.Ask("editmode");
                samples++;
                if (port.Disconnect) { during = "断"; break; }

                if (edit.Contains("editing=1", StringComparison.Ordinal))
                {
                    during = edit;
                    frontDuring = Windows.ForegroundDescription();
                    isOursDuring = IsOurCalendar(frontDuring);
                    timeline.Add($"第 {tick} 次问（{(tick * 50)} 毫秒）：{edit}；前台 {frontDuring}");
                    break;
                }

                if (tick % 20 == 0) timeline.Add($"第 {tick} 次问（{(tick * 50)} 毫秒）：{edit}");
                System.Threading.Thread.Sleep(50);
            }

            Frame frame = Screen.Grab(FrameRect());
            frame.OwnerText =
                $"抬起那一段：摆着时问产品「{before}」，前台 {frontBefore}；" +
                $"盯了 {samples} 次，进编辑态时问产品「{during}」，前台 {frontDuring}。" +
                $"判据是 Win32 前台那一扇，不是引擎那份「输入框有焦点」";
            string path = Path.Combine(outDir, "edit-lifted.png");
            frame.SavePng(path);

            shot.Finish(
                $"先把前台挪走（--away {away}）：{awayText}。" +
                $"收着的那一段：摆着时问产品「{before}」；Win32 前台 {frontBefore} —— " +
                $"{(isOursBefore ? "已经是日历那一扇（那样这一段没有对照）" : "**不是日历那一扇**（摆着的时候拿不到焦点是正常的）")}。" +
                $"抬着的那一段：每 50 毫秒问一次，共问了 {samples} 次；" +
                $"产品一进编辑态立刻读 Win32 前台 —— 问的是「{during}」，前台 {frontDuring}，" +
                $"{(isOursDuring ? "**GrabForTyping 把焦点端过来了**" : "**焦点没端过来**")}。" +
                $"抬起是产品自带的 <c>--lift</c> 开关做的，量具没点鼠标。" +
                $"时间线：{string.Join("；", timeline)}。帧留了档，带一行归谁",
                path);

            // 判的是「抬起那一段」；收着的那一段只是对照，它没有对照时这一发照样算数。
            return isOursDuring ? 0 : Find.Refused;
        }
    }

    private static RectI FrameRect() => Find.CalendarWindow()?.Rect ?? Windows.PrimaryMonitor().Work;

    /// <summary>Win32 前台那一扇是不是产品的日历。
    /// <b>按进程和类名判，不按标题判</b> —— 标题会变，类名是量出来的。</summary>
    private static bool IsOurCalendar(string foreground)
    {
        IntPtr handle = Windows.GetForegroundWindowRaw();
        if (handle == IntPtr.Zero) return false;
        return ProductProcess.IsProduct(Windows.ProcessIdOf(handle)) &&
               string.Equals(Windows.ClassOf(handle), "Engine", StringComparison.Ordinal);
    }

    private static void Archive(WindowInfo calendar, Port port, string path, string when)
    {
        List<Find.PointOwner> points = Find.ProbeOwners(calendar);
        Frame frame = Screen.Grab(calendar.Rect);
        frame.OwnerText = $"{when}：归谁 {string.Join("、", points.GroupBy(p => p.Tier).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()} 格"))}；产品答 {port.Ask("editmode")}";
        frame.SavePng(path);
    }
}
