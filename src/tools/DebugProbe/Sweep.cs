using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 工单 08：用户报的那一条 —— <b>Win+D 之后，日历还在不在</b>。
///
/// <para>这一族量测的起因，也是修完之后的验收点。跑法五步：
/// 清场 → 抓一帧读归谁 → 发真的 Win+D → 等动画停 → 再抓一帧再读归谁。</para>
///
/// <para><b>两帧都要留档。</b>「之后」那一帧单独留着没用 ——
/// 「之前」那一帧是判断「变化」的唯一凭据，只留后面那一张就只剩一个快照。</para>
///
/// <para><b>顺带量两件，分别报</b>：日历有没有盖住别的应用窗；
/// 那件事在 Win+D 之后还成不成立。合成一句「z 序正常」就把两件事各答一半。</para>
///
/// <para><b>这一发只报读数，不替产品下判断。</b>「日历该不该收回来」是产品的事。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/08-win-d-keeps-calendar.md</c>。</para>
/// </summary>
internal static class Sweep
{
    /// <summary>量 z 序要看多远。够把底下那几扇数出来。</summary>
    private const int ZDepth = 120;

    /// <summary>取样点：日历矩形的中心偏上，避开最上面那一行标题。</summary>
    private static PointI SamplePoint(RectI rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    internal static int Run(string[] args)
    {
        int rounds = 1;
        int watchZ = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--rounds" && int.TryParse(args[i + 1], out int n) && n > 0) rounds = n;
            if (args[i] == "--watch-z" && int.TryParse(args[i + 1], out int w) && w > 0) watchZ = w;
        }

        if (watchZ > 0) return WatchZ(watchZ);

        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "Win+D 那一发——扫不扫得走日历",
            "前后各抓一帧、各读归谁，再比矩形、可见、z 序位置、同一个点的命中和图标位置；" +
            "连跑几轮读数要一致。顺带量「日历有没有盖住别的应用窗」，两件分别报");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish("中止 —— 找不到日历那一扇，这一发答不了", "无");
            return Find.Refused;
        }

        var roundReports = new List<string>();
        bool same = true;
        string beforePath = string.Empty;

        for (int round = 1; round <= rounds; round++)
        {
            Reading before = Read(calendar.Handle);

            if (round == 1)
            {
                beforePath = Path.Combine(outDir, "sweep-before.png");
                Archive(calendar.Handle, before, beforePath);
            }

            // 图标位置：Win+D 是会动桌面图标的那一发（它叫「显示桌面」就是干这个的），
            // 所以这一问必须每一轮都读，读的是「这一轮有没有把图标挪了」。
            // 原来这一发只比日历的矩形和可见，图标那一问缺着。
            List<Icons.IconBox> placesBefore = Icons.ReadPlaces($"第 {round} 轮发之前");

            Input.SendWinD();
            Thread_Settle();

            Reading after = Read(calendar.Handle);
            string afterPath = Path.Combine(outDir, "sweep-after.png");
            Archive(calendar.Handle, after, afterPath);

            List<Icons.IconBox> placesAfter = Icons.ReadPlaces($"第 {round} 轮发之后");
            string places = Icons.ComparePlaces(placesBefore, placesAfter);

            bool sameRect = before.Rect == after.Rect;
            bool sameVisible = before.Visible == after.Visible;
            bool sameZ = before.ZIndex == after.ZIndex;
            bool samePoint = before.AtPoint == after.AtPoint;

            roundReports.Add(
                $"第 {round} 轮：矩形 {before.Rect} → {after.Rect}（{(sameRect ? "不变" : "**变了**")}）；" +
                $"可见 {(before.Visible ? "1" : "0")} → {(after.Visible ? "1" : "0")}（{(sameVisible ? "不变" : "**变了**")}）；" +
                $"z 序第 {before.ZIndex} → 第 {after.ZIndex}（{(sameZ ? "不变" : "**变了**")}）；" +
                $"同一个点 ({SamplePoint(before.Rect).X},{SamplePoint(before.Rect).Y}) 命中 " +
                $"{before.AtPoint} → {after.AtPoint}（{(samePoint ? "同一个" : "**换了**")}）；" +
                $"这一带别家窗占着：发之前 {before.ForeignCells} 格、发之后 {after.ForeignCells} 格 —— " +
                (before.Clean && after.Clean
                    ? "两侧都干净，上面的读数作数"
                    : "**至少一侧不干净，z 序和命中这两项量到的是别家的窗进进出出，不作数**") +
                $"；图标位置：{places}");

            // 两侧都干净时，那两项才拿来判；不干净时只判矩形和可见，
            // 并把「这一项不作数」原样写进读数，不拿它当结论。
            if (!(sameRect && sameVisible)) same = false;
            if (before.Clean && after.Clean && !(sameZ && samePoint)) same = false;
        }

        // ---- 顺带两件：日历有没有盖住别的应用窗 ---------------------------------
        string coverNow = CoverReading("发 Win+D 之前");
        Input.SendWinD();
        Thread_Settle();
        string coverAfter = CoverReading("发 Win+D 之后");

        string verdict = same
            ? "矩形和可见前后一致 —— Win+D 没有把日历扫走" +
              (roundReports.Any(r => r.Contains("不作数"))
                  ? "；z 序和命中这两项因为一侧不干净，**没有当成结论**"
                  : "；z 序和命中也前后一致")
            : "**前后读数有变** —— 见上面逐项";

        shot.Finish(
            $"{verdict}。{string.Join("；", roundReports)}。" +
            $"两帧都留了档：「之前」{beforePath}、「之后」{Path.Combine(outDir, "sweep-after.png")}，各带一行归谁。" +
            $"顺带第一件（日历有没有盖住别的应用窗）：{coverNow}。" +
            $"顺带第二件（那件事在 Win+D 之后还成不成立）：{coverAfter}。" +
            "这两件分别报，不合成一句",
            beforePath);

        return same ? 0 : Find.Refused;
    }

    /// <summary>
    /// 连着读日历那一扇的 z 序位置，<b>不发 Win&D</b>。
    ///
    /// <para>这一格是被读数逼出来的（量于 10-13 01:0x）：发 Win+D 那一发读到的
    /// z 序位置在「第 90」和「-1（这一轮没数到它）」之间来回跳，两趟 sweep 都是这个样子。
    /// 那一句有两种读法 —— <b>Win+D 把它动了</b>，或者<b>它自己在动</b>。
    /// 分开这两者的办法只有一个：<b>不发 Win+D</b>，只连着读。</para>
    ///
    /// <para>连着相同的读成一档，档和档之间摆出来，这样「多久换一次」看得见 ——
    /// 只把一串数字摆出来，要人自己数。</para>
    /// </summary>
    private static int WatchZ(int samples)
    {
        using Shot shot = Shot.Begin(
            "连着读日历那一扇的 z 序位置（不发 Win+D）",
            "这一格只为分开一件事：z 序读数在跳，是 Win+D 动的还是它自己在动。" +
            "不发 Win+D，只连着读，把读数分档摆出来");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish("中止 —— 找不到日历那一扇，这一发答不了", "无");
            return Find.Refused;
        }

        const int GapMs = 250;
        var readings = new List<int>();
        for (int i = 0; i < samples; i++)
        {
            readings.Add(ZIndexOf(calendar.Handle));
            System.Threading.Thread.Sleep(GapMs);
        }

        var runs = new List<string>();
        int missCount = 0;
        for (int i = 0; i < readings.Count; i++)
        {
            int last = i;
            while (last + 1 < readings.Count && readings[last + 1] == readings[i]) last++;
            int value = readings[i];
            if (value < 0) missCount += last - i + 1;
            runs.Add(value < 0 ? $"数不到 ×{last - i + 1}" : $"第 {value} ×{last - i + 1}");
            i = last;
        }

        Console.Out.WriteLine($"  逐次读数：{string.Join(" → ", readings)}");

        bool steady = runs.Count == 1;
        shot.Finish(
            $"不发 Win+D，每 {GapMs} 毫秒读一次，共读 {samples} 次。" +
            $"分档：{string.Join(" → ", runs)}。" +
            $"其中「数不到」那几档合计 {missCount} 次。" +
            (steady
                ? "**一档到底 —— 不发 Win+D 它不换档**，所以发 Win+D 那一发读到的 90 ↔ -1 交替，" +
                  "是每发一次 Win+D 翻一档的后果。" +
                  "（顶上去之后多久自己落回来，这一格没量到 —— 要连着读并在中途发一次 Win+D 才行。）"
                : "**读数自己就在跳 —— 不发 Win+D 它也换档，所以那一发的跳动里有一部分不是 Win+D 造成的**") +
            "（-1 的意思是「这一轮没数到它」，不是「排在第 0 位」）",
            "无");

        return steady ? 0 : Find.Refused;
    }

    // ---- 那一发读的那些数 -------------------------------------------------------

    private readonly record struct Reading(RectI Rect, bool Visible, int ZIndex, string AtPoint, string Owners, int ForeignCells)
    {
        /// <summary>这一侧能不能拿来比。<b>不干净的一侧，z 序和命中都不可信</b> ——
        /// 它们量到的是别人的窗进进出出，不是日历自己动没动。</summary>
        internal bool Clean => ForeignCells == 0;
    }

    private static Reading Read(IntPtr calendar)
    {
        WindowInfo info = Windows.Describe(calendar);
        PointI sample = SamplePoint(info.Rect);
        IntPtr hit = Windows.At(sample.X, sample.Y);
        string at = hit == IntPtr.Zero
            ? "什么都没有"
            : $"{Windows.ClassOf(hit)}（{WindowTier.TierOf(hit, Windows.RootOf(hit), Windows.ClassOf(Windows.RootOf(hit)), calendar, Find.IsWallpaperLayer(Windows.ClassOf(Windows.RootOf(hit))))}）";
        List<Find.PointOwner> points = Find.ProbeOwners(info, 5);
        return new(
            info.Rect, info.Visible, ZIndexOf(calendar), at, OwnersText(points),
            points.Count(p => p.Tier == WindowTier.Owner.AppWindow));
    }

    /// <summary>那一扇在 z 序里排第几。<b>数不到就是数不到</b>，不写「第 0 位」——
    /// 第 0 位看着像「排在最上面」，而它其实是「这一轮没数到它」。</summary>
    private static int ZIndexOf(IntPtr handle)
    {
        List<WindowInfo> z = Windows.ZOrderTop(ZDepth);
        for (int i = 0; i < z.Count; i++)
        {
            if (z[i].Handle == handle) return i;
        }

        return -1;
    }

    private static string OwnersText(List<Find.PointOwner> points) =>
        string.Join("、", points.GroupBy(p => p.Tier).OrderBy(g => g.Key)
            .Select(g => $"{Name(g.Key)} {g.Count()} 格"));

    private static string Name(WindowTier.Owner owner) => owner switch
    {
        WindowTier.Owner.Ours => "归日历自己",
        WindowTier.Owner.WallpaperLayer => "归壁纸层",
        WindowTier.Owner.SystemWindow => "归系统窗",
        WindowTier.Owner.AppWindow => "归别家窗",
        _ => "什么都没有",
    };

    private static void Archive(IntPtr calendar, Reading reading, string path)
    {
        RectI rect = Windows.RectOf(calendar);
        Frame frame = Screen.Grab(rect);
        frame.OwnerText = reading.Owners + $"；命中 {reading.AtPoint}";
        frame.SavePng(path);
    }

    // ---- 顺带那两件 -------------------------------------------------------------

    /// <summary>
    /// 开一扇<b>普通</b>窗（不置顶）盖在日历那一带，看日历有没有抢到它前面。
    ///
    /// <para><b>置顶的窗答不了这一问</b> —— 置顶窗一定在上面，答「日历被盖住了」，
    /// 而那一句在问的是产品有没有抢到前面。</para></summary>
    private static string CoverReading(string when)
    {
        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null) return "中止 —— 找不到日历那一扇";

        using var cover = new CoverWindow(calendar.Rect, topmost: false);
        if (!cover.Show())
        {
            return $"{when}：那一扇没造起来，答不了";
        }

        int calendarZ = ZIndexOf(calendar.Handle);
        int coverZ = ZIndexOf(cover.Handle);
        List<Find.PointOwner> points = Find.ProbeOwners(calendar, 5);
        int foreign = points.Count(p => p.Tier == WindowTier.Owner.AppWindow);

        return $"{when}：盖上去一扇 {CoverWindow.ClassName}（z 序第 {coverZ}），" +
               $"日历在第 {calendarZ}；命中测试 {points.Count} 格里有 {foreign} 格归它 —— " +
               (calendarZ >= 0 && coverZ >= 0 && calendarZ < coverZ
                   ? "**日历抢到了前面**"
                   : calendarZ < 0 || coverZ < 0
                       ? "有一扇这一轮没数到，答不了"
                       : "**日历没有抢到前面，被压住了**");
    }

    /// <summary>等 Win+D 的动画停下来。动画没停就抓，读到的是中途那一帧。</summary>
    private static void Thread_Settle() => System.Threading.Thread.Sleep(1500);
}
