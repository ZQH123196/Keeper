using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DebugProbe;

/// <summary>
/// 清场：发真的 Win+D，清完再读一遍。
///
/// <para><b>清场从来不是「清空了」</b> —— Win+D 只收走普通应用窗，一直浮在最上面的
/// 那一类它收不走。所以发完要再读一遍，看还有谁压着。</para>
///
/// <para>收不走的窗<b>不去动它</b>：不关、不移、不最小化。那是所有者的界面，
/// 量具没有资格碰。这一发要打出中止前后那一扇的句柄、可见性、矩形，三项一样。</para>
///
/// <para><b>「收走没有」只问 <c>IsIconic</c></b>，不问 <c>IsWindowVisible</c>、
/// 也不拿矩形猜 -32000：一扇确实被扫走的窗，可见性仍报 true、矩形仍报原样。
/// 这条坑 <c>CODING_STANDARDS.md</c>「代码级规矩」里已经记过一次，这里照它办。</para>
///
/// <para>出处：<c>.scratch/debug-tool/SPEC.md</c> 第三节第 3 条。</para>
/// </summary>
internal static class Clean
{
    /// <summary>等落定的上限。到点还没稳就报「没稳下来」并中止，不许无限等。</summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    /// <summary>一扇普通应用窗的身份。判「收走没有」看的是 <c>Iconic</c>那一项。</summary>
    private readonly record struct AppWindow(string ClassName, long Handle, int ProcessId, bool Visible, bool Iconic, RectI Rect)
    {
        internal string Id => $"{ClassName}@0x{Handle:X}";
    }

    internal static int Run(string[] args)
    {
        int repeat = 1;
        Input.Style style = Input.Style.VirtualKey;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--scancode") style = Input.Style.ScanCode;
            if (args[i] == "--split") style = Input.Style.Split;
            if (args[i] == "--repeat" && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0) repeat = n;
        }

        using Shot shot = Shot.Begin(
            "清场（发真的 Win+D）",
            "发完 Win+D 之后读到两次读数一致才算落定；落定之后凡压在日历矩形上面的窗，" +
            "逐个报出类名并中止这一发，且不去动它 —— 中止前后那一扇的句柄、可见性、矩形三项必须一样");

        WindowInfo? calendar = Find.CalendarWindow();
        if (calendar == null)
        {
            shot.Finish("中止 —— 找不到日历那一扇，清了场也验不了。被测矩形：无", "无");
            return Find.Refused;
        }

        // 发之前先量量具自己：注入的按键要是没到系统手上，Win+D 根本没发出去，
        // 后面那一整套「收走了没有」量的是一件没发生过的事。
        if (!Input.Arrives(out string probe))
        {
            shot.Finish(
                $"中止 —— 注入的按键没到系统手上，Win+D 根本没发出去，所以这一发量不了「收没收得走」。{probe}。" +
                "这一发要在一个注入的按键能到系统手上的环境里跑才有意义",
                "无");
            return Find.Refused;
        }

        WindowInfo? iconLayer = Windows.IconLayer();
        int iconsBefore = iconLayer != null ? Windows.IconCount(iconLayer.Handle) : -1;

        // **位置那一问在发之前先读一遍**（工单 03 判据的最后一条：
        // 「反复发 Win+D 之后，所有者的桌面图标一个不少、位置一个没动」）。
        // 原来这一发只读条数 —— 而「条数一样」和「位置没动过」是两件不同的事，
        // 拖走一颗再放回去条数一样而位置变了。读法用 UI Automation（工单 07 选定的那一条）。
        List<Icons.IconBox> placesBefore = Icons.ReadPlaces("发之前");

        // **抓一帧当「发之前」的底**（SPEC 第三节第 1 条 + 第四节最后一句：
        // 每一次动作之后自动抓一帧、读那个点的归谁）。
        // 抓帧在发之前、发完之后各一次，两帧各带一行归谁 —— 只有这样
        // 「收走没有」那一栏才不会脱离它自己那一帧存在。
        List<Find.PointOwner> ownBefore = Find.ProbeOwners(calendar);
        // **基线帧要连外面那片一起存**（2026-10-15 改）。`shot` 的判据是
        // 「同一块地方、另一时刻」的两帧比，而外面那一大片纯壁纸只用来验
        // 「基线还有效没有」—— 缺了它，壁纸换没换就没人答，
        // 而「壁纸换过」和「日历画上去了」在读数上长得一模一样。
        // 所以这一发存的是「外面那片 + 日历那块」这一整条，不只是日历那块。
        Frame frameBefore = Screen.Grab(FrameShot.WideRegion(calendar.Rect));
        frameBefore.OwnerText = FrameShot.OwnersText(ownBefore, ownBefore);
        string pathBefore = Path.Combine(FrameShot.DefaultOutDir(), "clean-before.png");
        frameBefore.SavePng(pathBefore);

        var rounds = new List<string>();
        bool aborted = false;
        for (int round = 1; round <= repeat; round++)
        {
            OneRound(calendar, iconsBefore, round, repeat, style, out string report, out bool roundAborted);
            rounds.Add(report);
            if (roundAborted)
            {
                aborted = true;
                break;
            }
        }

        WindowInfo? after = Find.CalendarWindow();
        bool calendarStillVisible = after != null && after.Visible;
        int iconsAfter = iconLayer != null && Windows.IsWindow(iconLayer.Handle)
            ? Windows.IconCount(iconLayer.Handle)
            : -1;

        // 发完之后：再抓一帧、再读一次图标位置，两样对着发之前那一份比。
        // **基线帧就是这一帧**（2026-10-15 加）：另存一份 clean-baseline.png，
        // 拿它当 `run.cmd shot --baseline` 的那一份。
        WindowInfo? last = after ?? calendar;
        List<Find.PointOwner> ownAfter = Find.ProbeOwners(last);
        Frame frameAfter = Screen.Grab(FrameShot.WideRegion(last.Rect));
        frameAfter.OwnerText = FrameShot.OwnersText(ownBefore, ownAfter);
        string pathAfter = Path.Combine(FrameShot.DefaultOutDir(), "clean-after.png");
        frameAfter.SavePng(pathAfter);
        string pathBaseline = Path.Combine(FrameShot.DefaultOutDir(), "clean-baseline.png");
        frameAfter.SavePng(pathBaseline);

        List<Icons.IconBox> placesAfter = Icons.ReadPlaces("发之后");
        string places = Icons.ComparePlaces(placesBefore, placesAfter);

        shot.Finish(
            $"日历那一扇发 Win+D 之后仍可见={(calendarStillVisible ? "1" : "0")}；" +
            $"桌面图标发之前 {iconsBefore} 条、发之后 {iconsAfter} 条；图标位置：{places}；" +
            $"共跑 {rounds.Count} 轮 —— {string.Join("｜", rounds)}。" +
            $"两帧都留了档（发之前 {pathBefore}、发之后 {pathAfter}），各带一行归谁。" +
            $"基线帧另存一份 {pathBaseline}，拿它当 run.cmd shot --baseline 的那一份",
            pathAfter);

        return aborted ? Find.Refused : 0;
    }

    // 图标位置那一问走 Icons.ComparePlaces —— 同一问在清场、扫桌面、托盘菜单、
    // 遮罩抬落、双击几发里要用同一把尺子。原来这里有一份私有副本，
    // 口径和别处各写各的，已经删掉。

    private static void OneRound(
        WindowInfo calendar, int iconsBefore, int round, int repeat, Input.Style style,
        out string report, out bool aborted)
    {
        List<AppWindow> before = AppWindows();

        // 发之前先把被测矩形上压着的那些扇各读一次三项 —— 中止之后要拿它们当对照。
        List<Fingerprint> fingerprints = Find.ProbeOwners(calendar)
            .Where(o => o.Tier == WindowTier.Owner.AppWindow)
            .Select(o => Read(o.Handle))
            .ToList();

        var watch = Stopwatch.StartNew();
        string zOrderBefore = string.Join(" → ", Windows.ZOrderTop(6).Select(
            w => $"{w.ClassName}{(w.Visible ? "" : "（看不见）")}{(Windows.IsIconic(w.Handle) ? "（最小化）" : "")}"));
        string sent = Input.SendWinD(style);

        // 落定之前不许读。读到两次一样才继续 —— 不等就去读，
        // 会把动画中途读成「窗没了」，而那不是「收走了」。
        string? previous = null;
        int samples = 0;
        bool settled = false;
        while (watch.Elapsed < SettleLimit)
        {
            Thread.Sleep(Poll);
            samples++;
            string now = Signature(AppWindows());
            if (now == previous)
            {
                settled = true;
                break;
            }

            previous = now;
        }

        if (!settled)
        {
            report = $"第 {round} 轮中止：{SettleLimit.TotalSeconds:0} 秒内没读到两次一样的读数，没稳下来，不许拿中途那一帧当凭";
            aborted = true;
            return;
        }

        List<AppWindow> after = AppWindows();
        List<AppWindow> swept = after.Where(a => a.Iconic && before.Any(b => b.Handle == a.Handle && !b.Iconic)).ToList();
        List<AppWindow> returned = after.Where(a => !a.Iconic && before.Any(b => b.Handle == a.Handle && b.Iconic)).ToList();

        // 「Win+D 有没有生效」问的是 z 序，不是最小化 —— 这一版 Windows 的「显示桌面」
        // 是把桌面端到前面来，每一扇都还在自己原来的位置、IsIconic 一直是 0。
        string zBefore = zOrderBefore;
        string zAfter = string.Join(" → ", Windows.ZOrderTop(6).Select(
            w => $"{w.ClassName}{(w.Visible ? "" : "（看不见）")}{(Windows.IsIconic(w.Handle) ? "（最小化）" : "")}"));

        // 收不走的：命中测试答「压在被测矩形上面」的那些。矩形相交不算数 ——
        // 一扇最小化着的窗矩形照样盖着那一片，2026-10-12 头一次跑就撞上了这一条。
        List<Find.PointOwner> blocking = Find.ProbeOwners(calendar)
            .Where(o => o.Tier == WindowTier.Owner.AppWindow)
            .GroupBy(o => o.Handle)
            .Select(g => g.First())
            .ToList();

        if (blocking.Count > 0)
        {
            report = $"第 {round} 轮中止：落定 {watch.ElapsedMilliseconds} 毫秒 / {samples} 次采样；{sent}；" +
                     $"z 序发之前：{zBefore}｜落定之后：{zAfter}；" +
                     $"按 IsIconic 判收走 {swept.Count} 扇，但 {string.Join("、", blocking.Select(b => b.ClassName))} " +
                     $"还压在日历矩形 {calendar.Rect} 上面（命中测试那一档）—— 不去动它；" +
                     $"中止前后那一扇的读数：{Untouched(fingerprints, blocking)}";
            aborted = true;
            return;
        }

        // 这一轮量完要把桌面放回去，下一轮才从同一个起点起 ——
        // 不放回去的话第二轮发 Win+D 是「放回来」，两轮量的不是同一件事。
        if (repeat > 1)
        {
            Input.SendWinD(style);
            WaitBack(before);
        }

        report = $"第 {round} 轮：落定 {watch.ElapsedMilliseconds} 毫秒 / {samples} 次采样；{sent}；" +
                 $"按 IsIconic 判收走 {swept.Count} 扇" +
                 (swept.Count > 0 ? $"（{string.Join("、", swept.Take(6).Select(s => s.Id))}）" : "") +
                 $"；放回来 {returned.Count} 扇；发之前看得见的 app 窗 {before.Count(b => b.Visible && !b.Iconic)} 扇、" +
                 $"落定之后 {after.Count(a => a.Visible && !a.Iconic)} 扇" +
                 (repeat > 1 ? "（这一轮量完已放回原状，好让下一轮从同一起点起）" : "") +
                 (iconsBefore >= 0 ? $"；图标 {iconsBefore} 条不动" : "");
        aborted = false;
    }

    /// <summary>量完之后等桌面放回去。同样要等两次一样。</summary>
    private static void WaitBack(List<AppWindow> target)
    {
        var watch = Stopwatch.StartNew();
        string? previous = null;
        while (watch.Elapsed < SettleLimit)
        {
            Thread.Sleep(Poll);
            string now = Signature(AppWindows());
            if (now == previous) return;
            previous = now;
        }
    }

    private static string Signature(List<AppWindow> windows) =>
        string.Join("|", windows.Select(w => $"{w.Id}:{(w.Iconic ? "收走" : "在")}"));

    /// <summary>普通应用窗。壁纸层那一族和本进程自己那几扇不算在内。</summary>
    private static List<AppWindow> AppWindows()
    {
        return Windows.TopLevelWindows()
            .Where(w => !ProductProcess.IsProduct(w.ProcessId))
            .Where(w => !Find.IsWallpaperLayer(Windows.ClassOf(Windows.RootOf(w.Handle))))
            .Select(w => new AppWindow(
                w.ClassName,
                w.Handle.ToInt64(),
                w.ProcessId,
                w.Visible,
                Windows.IsIconic(w.Handle),
                w.Rect))
            .OrderBy(w => w.Handle)
            .ToList();
    }

    /// <summary>一扇窗的三项读数。中止前后各读一次，三项一样就是没动过。</summary>
    private readonly record struct Fingerprint(long Handle, string ClassName, bool Visible, bool Iconic, RectI Rect);

    /// <summary>按句柄直接读那一扇。<c>EnumWindows</c> 只枚举顶层窗，
    /// 而压在被测矩形上面的常常是子窗 —— 从顶层名单里翻会把「明明在」答成「读不到」。</summary>
    private static Fingerprint Read(IntPtr handle)
    {
        WindowInfo w = Windows.Describe(handle);
        return new Fingerprint(w.Handle.ToInt64(), w.ClassName, w.Visible, Windows.IsIconic(handle), w.Rect);
    }

    /// <summary>
    /// 打出那些窗<b>发之前</b>和<b>现在</b>的三项读数，用来证明量具没碰它。
    /// 两栏一样就是没动过 —— 有一项变了，那就是有人动了它。
    /// </summary>
    private static string Untouched(List<Fingerprint> before, List<Find.PointOwner> blocking)
    {
        return string.Join("；", blocking.Select(b =>
        {
            Fingerprint now = Read(b.Handle);
            Fingerprint? was = before.FirstOrDefault(f => f.Handle == b.Handle.ToInt64());
            string wasSeen = was == null
                ? "发之前那一帧命中测试没点到它"
                : $"发之前 可见={(was.Value.Visible ? "1" : "0")} 最小化={(was.Value.Iconic ? "1" : "0")} 矩形 {was.Value.Rect}";
            bool same = was != null && was.Value.Visible == now.Visible &&
                        was.Value.Iconic == now.Iconic && was.Value.Rect == now.Rect;
            return $"{b.ClassName}（pid {b.ProcessId}）句柄 0x{b.Handle.ToInt64():X}｜{wasSeen}｜" +
                   $"现在 可见={(now.Visible ? "1" : "0")} 最小化={(now.Iconic ? "1" : "0")} 矩形 {now.Rect}" +
                   $"｜三项{(was == null ? "没法比（发之前没点到它）" : same ? "一样，没动过" : "变了，有人动过它")}";
        }));
    }
}