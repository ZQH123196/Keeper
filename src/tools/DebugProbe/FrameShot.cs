using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 抓一帧，回答「日历那一块上有没有日历的内容」。
///
/// <para><b>判据是「同一块地方、另一时刻」的两帧比</b>，尺子在 <see cref="PanelDiff"/>：
/// 现在这一帧，和一张<b>存下来的、日历不在屏上时的基线帧</b>。
/// 外面那一大片纯壁纸只用来验基线还有效没有，不作判据 ——
/// 拿它比颜色就是原来那个判据，照片壁纸上永远答「画出来了」。</para>
///
/// <para><b>为什么非要有基线帧。</b>日历是半透明的，压在照片上；
/// 照片本身的颜色没有一样、不重复的规律，格子线、纹理、颜色重复这几样都量过，
/// 照片全能冒充（读数在 <c>docs/research/</c>）。
/// 唯一能把「日历画的」和「壁纸本来就有的」分开的，是同一块地方在没有日历时的那一份。</para>
///
/// <para><b>缺基线帧就中止</b>，不退回任何一条单帧的猜法 ——
/// 宁可答不出，不拿一个在照片壁纸上必然答错的读数顶。</para>
///
/// <para><b>离线那一支</b>（<c>--frame</c>）把存下来的帧读回来跑同一把尺子。
/// 它只在核对判据时用：屏上那一问永远走 <see cref="Screen.Grab"/>，
/// 不拿磁盘上的旧帧冒充屏上的读数。</para>
/// </summary>
internal static class FrameShot
{
    /// <summary>日历矩形里面缩一圈，让开边上那一条描边。</summary>
    private const int InnerInsetPercent = 12;

    /// <summary>
    /// 「外面那片 + 日历那块」这一整条，日历矩形在屏幕上的位置就是它。
    /// <b>抓帧、存帧、读基线三处走同一个函数</b> —— 几何写在两处，
    /// 迟早有一处对不上，而对不上的时候读数是「基线作废」，不是「日历没画」。
    /// </summary>
    internal static RectI WideRegion(RectI calendarRect)
    {
        RectI inner = Inset(calendarRect, InnerInsetPercent);
        RectI outer = new(inner.Left - inner.Width, inner.Top, inner.Left, inner.Bottom);
        return new(outer.Left, inner.Top, calendarRect.Right, inner.Bottom);
    }

    /// <summary>
    /// 验尺子那一支画面板用的白纱：<b>1−α</b>。
    /// 照着真有日历的那几帧上拟合出来的斜率写（<c>shot --frame … --baseline …</c> 那一行），
    /// 不是现编的。
    /// </summary>
    private const double ControlVeilKeep = 0.686;

    /// <summary>
    /// 验尺子那一支画上去的不透明字和色块占面板多大比例。
    /// 照着真有日历的那一帧量出来的：扣掉白纱之后，日历那一块里
    /// <b>差过 96 级的还有 13.304%</b>（<c>V2-desktop.png</c> 那一行读数）。
    /// </summary>
    private const double ControlInkShare = 0.133;

    private const string Criterion =
        "拿同一块地方的两帧比：现在这一帧，和一张存下来的、日历不在屏上时的基线帧。" +
        "外面那一大片纯壁纸必须和基线几乎逐格一样（基线作废就拒收，不猜）；" +
        "日历那一块先按通道拟合一条直线扣掉那层白纱，扣完还有百分之几没扣干净 —— " +
        "超过门限判「画出来了」，低于门限判「没画出来」。不拿旁边那一片的颜色当基线";

    internal static int Run(string[] args)
    {
        string outDir = DefaultOutDir();
        bool checkOnly = false;
        string? framePath = null;
        string? baselinePath = null;
        RectI? measureRect = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--out") outDir = args[i + 1];
            if (args[i] == "--frame") framePath = args[i + 1];
            if (args[i] == "--baseline") baselinePath = args[i + 1];
            if (args[i] == "--rect") measureRect = ParseRect(args[i + 1]);
        }

        if (args.Contains("--check-diff", StringComparer.Ordinal)) checkOnly = true;

        using Shot shot = Shot.Begin(
            checkOnly
                ? "验这把尺子自己"
                : framePath != null
                    ? $"离线跑一把：{framePath}"
                    : "抓一帧，日历那一块上有没有内容",
            checkOnly
                ? "两条对照用<b>同一张只剩壁纸的图</b>：一条是它自己（必须判「没画出来」），" +
                  "一条是它上面照着量出来的白纱和字画出一块面板（必须判「画出来了」）。" +
                  "这两条正是原来那一把栽的地方 —— 照片壁纸上，旁边那一片和这一片的壁纸本来就不同"
                : Criterion);

        // block 是「外面那片 + 日历那块」这一整条，control 和 panel 都是它自己的坐标。
        // 外面那片不进判据，只验基线还有效没有。
        Frame block;
        RectI control;
        RectI panel;
        RectI? baselineRegion;
        string owners;
        string savedPath = framePath ?? Path.Combine(outDir, "calendar-frame.png");

        if (framePath != null)
        {
            Frame loaded = Frame.FromPng(framePath);
            RectI asked = measureRect ?? new RectI(0, 0, loaded.Width, loaded.Height);
            RectI controlRect = new(asked.Left - asked.Width, asked.Top, asked.Left, asked.Bottom);
            RectI whole = new(controlRect.Left, controlRect.Top, asked.Right, asked.Bottom);
            block = loaded.Slice(whole);
            control = new(0, 0, controlRect.Width, controlRect.Height);
            panel = new(controlRect.Width, 0, controlRect.Width + asked.Width, block.Height);
            // 基线给的是整屏帧时，按<b>同一块地方</b>裁下来 —— 位置必须一样，不许挪。
            baselineRegion = whole;
            owners = $"离线读的那一帧 {framePath} 里的 {asked}（{loaded.Width}×{loaded.Height} 里裁出来的）";
        }
        else
        {
            WindowInfo? calendar = Find.CalendarWindow();
            if (calendar == null)
            {
                // **把「为什么找不到」一起交出来**：只答「找不到」的话，
                // 「宿主没挑中」「宿主底下没有那一扇」「那一扇不在屏内」三件事长得一模一样。
                shot.Finish("中止 —— 找不到日历那一扇，没有被测矩形。" + Find.Measure().Text, "无");
                return Find.Refused;
            }

            RectI rect = calendar.Rect;
            RectI inner = Inset(rect, InnerInsetPercent);
            RectI outer = new(inner.Left - inner.Width, inner.Top, inner.Left, inner.Bottom);
            RectI whole = WideRegion(rect);

            // 归谁先读一遍，再抓帧，再读一遍 —— 抓帧那一瞬屏幕上是谁，前后都摊开。
            List<Find.PointOwner> before = Find.ProbeOwners(calendar);
            Frame grabbed = Screen.Grab(whole);
            List<Find.PointOwner> after = Find.ProbeOwners(calendar);
            owners = OwnersText(before, after);

            string path = Path.Combine(outDir, "calendar-frame.png");
            grabbed.OwnerText = owners;
            grabbed.SavePng(path);

            List<Find.PointOwner> blocking = after.Where(o => o.Tier == WindowTier.Owner.AppWindow).ToList();
            if (blocking.Count > 0)
            {
                shot.Finish(
                    $"拒收 —— 这一帧的读数来自别家的窗，不许拿它推导机制。{owners}；" +
                    $"压着的是 {string.Join("、", blocking.Select(b => $"{b.ClassName}（pid {b.ProcessId}）").Distinct())}",
                    path);
                return Find.Refused;
            }

            block = grabbed;
            control = new(0, 0, outer.Width, outer.Height);
            panel = new(outer.Width, 0, outer.Width + inner.Width, block.Height);
            baselineRegion = null;
            savedPath = path;
        }

        if (checkOnly) return CheckControls(shot, block, control, panel, owners);

        if (baselinePath == null)
        {
            shot.Finish(
                "中止 —— 这一发没有基线帧，没法答。" +
                "判据是「同一块地方、另一时刻」的两帧比：没有日历不在时的那一份，" +
                "就没有能把「日历画的」和「壁纸本来就有的」分开的东西。" +
                "发一次 <clean>（它会发真的 Win+D）拿一份基线帧，或者用 --baseline 指一份已经有的。" +
                "不许退回任何一条单帧的猜法 —— 照片壁纸上那些猜法都答错",
                savedPath);
            return Find.Refused;
        }

        Frame baseline = Baseline(Frame.FromPng(baselinePath), block, baselineRegion, out string how);
        PanelDiff diff = PanelDiff.Measure(block, baseline, control, panel);
        string baselineText = $"基线帧 {baselinePath}（{how}）";

        if (diff.Misaligned)
        {
            shot.Finish(
                $"拒收 —— {baselineText}不是「外面那片 + 日历那块」这一整条：这一条 {block.Width}×{block.Height}，" +
                $"基线是 {baseline.Width}×{baseline.Height}。外面那片是验基线用的，缺了它就答不出「壁纸换没换」",
                savedPath);
            return Find.Refused;
        }

        shot.Finish(
            $"{owners}；{baselineText}；{diff.Reading()}；" +
            (diff.Stale ? "判：基线作废（外面那一大片和基线对不上，壁纸多半换过了），这一发拒收" :
             diff.Drawn ? "判：画出来了" : "判：没画出来"),
            savedPath);

        return diff.Stale ? Find.Refused : 0;
    }

    /// <summary>
    /// 验这把尺子自己。<b>两条对照都要过</b>，而且用的是<b>同一张图</b>：
    /// <b>阴性</b>是「只剩壁纸」那一张自己对自己（必须判「没画出来」），
    /// <b>阳性</b>是在同一张「只剩壁纸」的图上照着量出来的白纱和字画出一块面板
    /// （必须判「画出来了」）。两条的基线都是那一张只剩壁纸的图，
    /// 唯一的差别就是面板。
    ///
    /// <para><b>「只剩壁纸」那一张是拿同一帧里外面那片搬出来的</b> ——
    /// 外面那片是这一帧里确定没有被日历压过的地方。
    /// 不发 Win+D、不动鼠标、不碰产品。</para>
    /// </summary>
    private static int CheckControls(Shot shot, Frame block, RectI control, RectI panel, string owners)
    {
        Frame wallpaper = WallpaperOnly(block, control, panel);
        Frame drawn = PaintPanel(wallpaper, panel);

        PanelDiff negative = PanelDiff.Measure(wallpaper, wallpaper, control, panel);
        PanelDiff positive = PanelDiff.Measure(drawn, wallpaper, control, panel);
        bool negativeOk = !negative.Stale && !negative.Drawn;
        bool positiveOk = !positive.Stale && positive.Drawn;
        bool deterministic = negative.Reading() == PanelDiff.Measure(wallpaper, wallpaper, control, panel).Reading();

        shot.Finish(
            $"{owners}；整条 {block.Width}×{block.Height}；" +
            $"阴性（整条只剩壁纸、日历不在，基线就是它自己）：{negative.Reading()} —— " +
            $"{(negativeOk ? "判「没画出来」，对" : "这一发不合格")}；" +
            $"阳性（同一张图上照着 1−α = {ControlVeilKeep:0.000} 画上白纱、再画上占 {ControlInkShare:0.0%} 的字）：" +
            $"{positive.Reading()} —— {(positiveOk ? "判「画出来了」，对" : "这一发不合格")}；" +
            $"同一张图上数两遍读数 {(deterministic ? "一模一样" : "不一样，这一发不合格")}",
            "无");

        return negativeOk && positiveOk && deterministic ? 0 : Find.Refused;
    }

    /// <summary>
    /// 把日历那一块整片换成同一帧里紧挨着的那一片壁纸 —— 这一帧里唯一确定没被日历压过的地方。
    /// 两片一样高、一样宽（外面那片就是照着里面那片取的），所以逐行搬得动。
    /// </summary>
    private static Frame WallpaperOnly(Frame block, RectI control, RectI panel)
    {
        var copy = new byte[block.Pixels.Length];
        Array.Copy(block.Pixels, copy, block.Pixels.Length);
        int columns = Math.Min(control.Width, panel.Width);
        int rows = Math.Min(control.Height, panel.Height);

        for (int y = 0; y < rows; y++)
        {
            int from = (y * block.Width + control.Left) * 4;
            int to = (y * block.Width + panel.Left) * 4;
            Array.Copy(block.Pixels, from, copy, to, columns * 4);
        }

        return new Frame(block.Where, copy);
    }

    /// <summary>
    /// 在「只剩壁纸」的那一张上画一块面板：先按 <see cref="ControlVeilKeep"/> 压一层白纱，
    /// 再按 <see cref="ControlInkShare"/> 画上不透明的白块（七列六行，每格一块）。
    /// 两个数都照着真有日历的那几帧量出来，不是现编的。
    ///
    /// <para><b>为什么要画不透明的白块，不能只画白纱。</b>
    /// 只画白纱的话，那条拟合出来的直线正好把白纱扣掉，扣完什么都不剩 ——
    /// <b>一块只有白纱、连一个字都没有的面板，这一把量不出来</b>。
    /// 真日历上永远有字和不透明的格子底色，所以量的是它们。这是这把尺子已知的边界，
    /// 写在这里，不是留给下一个人踩。</para>
    /// </summary>
    private static Frame PaintPanel(Frame frame, RectI panel)
    {
        var copy = new byte[frame.Pixels.Length];
        Array.Copy(frame.Pixels, copy, frame.Pixels.Length);

        int width = panel.Width;
        int height = panel.Height;
        int cells = 7 * 6;
        int perCell = Math.Max(1, (int)(width * (long)height * ControlInkShare / cells));
        int boxSide = Math.Max(1, (int)Math.Sqrt(perCell));

        for (int row = 0; row < 6; row++)
        {
            for (int column = 0; column < 7; column++)
            {
                int x0 = panel.Left + width * column / 7 + width / 7 / 6;
                int y0 = panel.Top + height * row / 6 + height / 6 / 4;
                for (int dy = 0; dy < boxSide && y0 + dy < panel.Bottom; dy++)
                {
                    for (int dx = 0; dx < boxSide && x0 + dx < panel.Right; dx++)
                    {
                        int at = ((y0 + dy) * frame.Width + (x0 + dx)) * 4;
                        copy[at] = 255;
                        copy[at + 1] = 255;
                        copy[at + 2] = 255;
                    }
                }
            }
        }

        // 白纱：整块压成同一个比例 —— 日历就是半透明的，这一层照着量出来的 1−α 压。
        for (int y = panel.Top; y < panel.Bottom; y++)
        {
            for (int x = panel.Left; x < panel.Right; x++)
            {
                int at = (y * frame.Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    copy[at + channel] = (byte)Math.Round(copy[at + channel] * ControlVeilKeep + 255 * (1 - ControlVeilKeep));
                }
            }
        }

        return new Frame(frame.Where, copy);
    }

    /// <summary>
    /// 基线帧要么就是「外面那片 + 日历那块」这一整条，
    /// 要么是和这一帧一样大的整屏图 —— 那就按<b>同一块地方</b>裁下来。
    /// 两种都答得出；都不是的话留给 <see cref="PanelDiff"/> 拒收。
    /// </summary>
    private static Frame Baseline(Frame loaded, Frame block, RectI? region, out string how)
    {
        if (loaded.Width == block.Width && loaded.Height == block.Height)
        {
            how = $"{loaded.Width}×{loaded.Height}，就是这一整条";
            return loaded;
        }

        if (region is RectI where && loaded.Width > where.Width && loaded.Height > where.Height)
        {
            Frame sliced = loaded.Slice(where);
            how = $"{loaded.Width}×{loaded.Height}，按同一块地方 {where} 裁下来的";
            return sliced;
        }

        how = $"{loaded.Width}×{loaded.Height}，不是这一整条（这一条是 {block.Width}×{block.Height}）";
        return loaded;
    }

    /// <summary>留档的目录。工单 06 的拒收那一帧和这里落在同一个目录 ——
    /// 证尺子抓得住的那一帧，和平常抓的那些帧要放在一起才找得回来。</summary>
    ///
    /// <para><b>两条都得走，哪条先走到哪条算数</b>：
    /// <b>当前目录</b>先走 —— 量具搬到 <c>%TEMP%</c> 之后（为的是躲开仓库目录的 ACL，
    /// 见 <see cref="Relaunch"/>），exe 自己那条路已经指不到仓库了；
    /// <b>exe 自己那条路</b>后走 —— 直接在仓库里跑 exe 时当前目录可能在别处。</para>
    ///
    /// <para><b>找的时候不建目录</b>（2026-10-13 实测出来的毛病）：原来每往上一层
    /// 就先建一个 <c>.scratch/debug-tool/shots</c> 再看这一层对不对，仓库里因此多出
    /// <b>7 个</b>空的 <c>.scratch</c>。现在<b>找到哪一层就在哪一层建</b>，只建一个；
    /// 一处也没找到就跟 exe 放一起，不去别处乱建。</para>
    internal static string DefaultOutDir()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string? repo = RepoRootFrom(start);
            if (repo is null) continue;
            string dir = Path.Combine(repo, ".scratch", "debug-tool", "shots");
            Directory.CreateDirectory(dir);
            return dir;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(beside);
        return beside;
    }

    /// <summary>从这一层往上找仓库根。判据是那份 <c>SPEC.md</c> 在不在 ——
    /// 找的时候一个目录都不建。<b>找不到答 null</b>，不拿一个猜出来的路径顶上。</summary>
    private static string? RepoRootFrom(string start)
    {
        DirectoryInfo? dir = new(start);
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".scratch", "debug-tool", "SPEC.md"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary><c>--rect</c> 那几个数：<c>x,y,w,h</c>，指的是日历那一块。</summary>
    private static RectI ParseRect(string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
            w <= 0 || h <= 0)
        {
            throw new ArgumentException($"--rect 要四个数 x,y,w,h，收到的是「{text}」。");
        }

        return new RectI(x, y, x + w, y + h);
    }

    private static RectI Inset(RectI rect, int percent)
    {
        int dx = rect.Width * percent / 100;
        int dy = rect.Height * percent / 100;
        return new RectI(rect.Left + dx, rect.Top + dy, rect.Right - dx, rect.Bottom - dy);
    }

    /// <summary>抓帧前后各读一遍归谁。两遍一样才说明抓的那一瞬和读到的那一瞬是同一个世界。</summary>
    internal static string OwnersText(List<Find.PointOwner> before, List<Find.PointOwner> after)
    {
        string describe(IReadOnlyList<Find.PointOwner> points) =>
            string.Join("、", points.GroupBy(p => p.Tier).OrderBy(g => g.Key).Select(g =>
                $"{Name(g.Key)} {g.Count()} 格" +
                (g.Key == WindowTier.Owner.AppWindow ? $"（{string.Join("、", g.Select(p => p.ClassName).Distinct())}）" : string.Empty)));

        return $"归谁：抓帧前 {describe(before)}｜抓帧后 {describe(after)}";
    }

    private static string Name(WindowTier.Owner owner) => owner switch
    {
        WindowTier.Owner.Ours => "归日历自己",
        WindowTier.Owner.WallpaperLayer => "归壁纸层",
        WindowTier.Owner.SystemWindow => "归系统窗",
        WindowTier.Owner.AppWindow => "归别家窗",
        _ => "什么都没有",
    };
}
