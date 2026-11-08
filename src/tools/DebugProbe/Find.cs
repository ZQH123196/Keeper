using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 量具的第一件事：<b>看清屏幕上谁压着谁</b>。
///
/// <para>两个读法：找得到日历那扇窗吗；某个点归谁。两件事都做完了才有资格量别的。</para>
///
/// <para>设计出处：<c>.scratch/debug-tool/SPEC.md</c> 第五节（它怎么找日历窗）
/// 和第三节第 1、2 条（归谁那一行、拒收）。</para>
/// </summary>
internal static class Find
{
    /// <summary>拒收和中止的退出码。零是「这一发答完了」。</summary>
    internal const int Refused = 1;

    /// <summary>
    /// 日历那一扇的窗口类名：<c>SysHeader32</c>。<b>它不是产品自己造的窗</b> ——
    /// 产品借的是壳自己在图标层底下放着的一扇（平时藏着不用），理由和那几条读数
    /// 在 <c>src/NativeLayer.cs</c> 的类注释里。2026-10-15 量到的 <c>KeeperNativeLayer</c>
    /// 是旧写法（产品自己建窗挂进图标层），那一扇挂得上却不出像素，已退场。
    /// 2026-10-12 量到的 <c>Engine</c> 更早那一扇，也已退场。
    /// </summary>
    private const string CalendarClass = "SysHeader32";

    /// <summary>
    /// 日历那一扇的<b>宿主</b>类名。**它和类名、矩形一样是量出来的**，
    /// 而且判据里少一格就会量到别的东西：类名单独不够（别的进程也可能注册同一个类名），
    /// 矩形单独也不够。
    /// </summary>
    internal const string IconLayerHostClass = "SHELLDLL_DefView";

    private static readonly string[] WallpaperLayerClasses =
    {
        ProgmanClass,
        "WorkerW",
        "SHELLDLL_DefView",
    };

    /// <summary>桌面主窗那一扇的类名。<b>量出来的，不是抄的</b>（2026-10-13 实测：
    /// 这台机器上整条顶层链 300 扇里只有一扇 <c>Progman</c>，可见的那一扇在第 181 扇；
    /// 12 扇 <c>WorkerW</c> 全在第 66 到 168 扇，也就是<b>全都比它高</b>）。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。</summary>
    internal const string ProgmanClass = "Progman";

    /// <summary>桌面图标那一层的类名。</summary>
    internal const string IconViewClass = "SysListView32";

    internal static bool IsWallpaperLayer(string className) =>
        WallpaperLayerClasses.Contains(className, StringComparer.Ordinal);

    internal static int Run(string[] args)
    {
        if (args.Contains("--dump", StringComparer.Ordinal))
        {
            Dump();
            return 0;
        }

        return LocateAndReport();
    }

    // ---- 找那一扇 ---------------------------------------------------------------

    /// <summary>
    /// 按「窗口类 + 宿主 + <c>WS_EX_LAYERED</c> + 形状」找，不写死句柄。
    /// 句柄每次启动都不一样，写死就绑死在一次运行上。
    ///
    /// <para><b>枚举的是图标层那一扇的后代，不是顶层窗</b> —— 日历那一扇是子窗，
    /// <c>EnumWindows</c> 那一族走一遍根本不会撞见它。宿主那一扇的找法是
    /// <c>Windows.IconLayerHost</c>。</para>
    ///
    /// <para><b>判据里没有「属主是产品」那一格</b>：日历那一扇是<b>壳自己的窗</b>
    /// （产品借的 <c>SysHeader32</c>），属主进程是壳。改判据时别把那一格加回来 ——
    /// 加回来就一扇都找不到。</para>
    ///
    /// <para><b>四段各数一次</b>，不是只答「找到几扇」：找不到时报得出是哪一段筛掉的 ——
    /// 「宿主没找到」和「宿主底下没有那一扇」在只答「没有」的时候长得一模一样。</para>
    /// </summary>
    private static Candidates Search()
    {
        WindowInfo? host = Windows.IconLayerHost();
        if (host == null) return new(null, new List<WindowInfo>(), new List<WindowInfo>());

        List<WindowInfo> named = Windows.Descendants(host.Handle)
            .Where(w => string.Equals(w.ClassName, CalendarClass, StringComparison.Ordinal))
            // **宿主两样都收**：图标层的直接子窗，或者图标列表的子窗。
            // 产品那一侧（`NativeLayer.Borrow`）判的是同一件事 —— explorer 重启前后
            // 壳那一扇的爹不一样（2026-10-16）。两边判据要一样，不然产品借到了、量具找不到。
            .Where(w => string.Equals(Windows.ClassOf(Windows.ParentOf(w.Handle)), IconLayerHostClass, StringComparison.Ordinal)
                        || string.Equals(Windows.ClassOf(Windows.ParentOf(w.Handle)), IconViewClass, StringComparison.Ordinal))
            // **带 WS_EX_LAYERED 那一格是「这一扇正被借去当日历」的标记**：
            // 壳那一堆子窗里只有它会被加上这一位，而判据里少了它就会数出别的窗来。
            .Where(w => Windows.IsLayered(w.Handle))
            .ToList();

        List<WindowInfo> onScreen = named.Where(w => Windows.MonitorOf(w.Handle).Monitor.Contains(w.Rect)).ToList();
        return new(host, named, onScreen);
    }

    /// <summary>找那一扇的路上每一段读到的。<b>三段分开是给「找不到」那一支报理由用的。</b></summary>
    private readonly record struct Candidates(WindowInfo? Host, List<WindowInfo> Named, List<WindowInfo> OnScreen);

    /// <summary>
    /// 日历那一扇。找到零扇或多扇都答「没有」—— <b>不猜是哪一扇</b>。
    /// 找到多扇时那一发要拒收，而拒收的理由只有这一处能答。
    /// </summary>
    internal static WindowInfo? CalendarWindow()
    {
        Candidates found = Search();
        return found.OnScreen.Count == 1 ? found.OnScreen[0] : null;
    }

    /// <summary>被测矩形里每一格归谁。别家窗那一档就是拒收的理由。</summary>
    internal static List<PointOwner> ProbeOwners(WindowInfo calendar, int steps = 5) => ProbeGrid(calendar, steps);

    private static int LocateAndReport()
    {
        using Shot shot = Shot.Begin(
            "找日历那一扇窗",
            "按「窗口类 + 宿主 + WS_EX_LAYERED + 形状」找到日历那一扇（那一扇是壳自己的窗，" +
            "属主不是产品进程），并报出矩形里每一格归谁 —— " +
            "被测矩形里有任何一格归别家窗，当场拒收、报出那个类名、中止这一发");

        MeasureResult reading = Measure();
        shot.Finish(reading.Text, "无");
        return reading.Ok ? 0 : Refused;
    }

    /// <summary>量出来的读数。<b>量与打印分开</b>：工单 06 的拒收那一发要把同一把尺子
    /// 连着调两次（压着的时候一次、挪开之后一次），一次打印一组的口径在这儿就分不开了。</summary>
    internal readonly record struct MeasureResult(bool Ok, string Text);

    /// <summary>找那一扇、报每一格归谁。答不出来就答答不出来的理由，<b>不打印</b>。</summary>
    internal static MeasureResult Measure()
    {
        List<int> pids = ProductProcess.RunningProductIds();
        Candidates found = Search();

        if (pids.Count == 0)
        {
            return new(false,
                "找不到 —— Keeper 在跑：否。屏幕上现在有哪些窗：" + ScreenSummary() +
                "（报完就退，不重试到超时）");
        }

        if (found.Host == null)
        {
            return new(false,
                $"找不到 —— Keeper 在跑：是（pid {string.Join("、", pids)}）。" +
                $"日历那一扇开没有：没有 —— 它的宿主那一扇（{IconLayerHostClass}）按「类名 + 可见 + 满屏」" +
                "在 Progman 的后代里挑，挑出来的不是零扇就是多于扇，不猜是哪一扇。" +
                "屏幕上现在有哪些窗：" + ScreenSummary() + "（报完就退，不重试到超时）");
        }

        if (found.OnScreen.Count == 0)
        {
            return new(false,
                $"找不到 —— Keeper 在跑：是（pid {string.Join("、", pids)}）。日历那一扇开没有：没有 —— " +
                $"宿主那一扇 0x{found.Host.Handle.ToInt64():X}/{IconLayerHostClass} 挑出来了；" +
                $"图标层底下「{CalendarClass} + 带 WS_EX_LAYERED」的窗有 {found.Named.Count} 扇" +
                (found.Named.Count > 0 ? $"（{string.Join("、", found.Named.Select(Describe))}）" : string.Empty) +
                "，其中矩形整块落在它所在那块屏里的 0 扇。" +
                "屏幕上现在有哪些窗：" + ScreenSummary() + "（报完就退，不重试到超时）");
        }

        if (found.OnScreen.Count > 1)
        {
            return new(false,
                $"拒收 —— 找到 {found.OnScreen.Count} 扇都像日历那一扇，不猜是哪一扇：" +
                string.Join(" / ", found.OnScreen.Select(Describe)));
        }

        WindowInfo calendar = found.OnScreen[0];
        RectI rect = calendar.Rect;
        MonitorInfo monitor = Windows.MonitorOf(calendar.Handle);
        List<PointOwner> owners = ProbeOwners(calendar);

        // **格数和类名数是两件事，不许合成一句。** 压着的那一扇常常占满整个矩形 ——
        // 报「1 格」而它其实占了 25 格，读数会让人以为只压了一个角。
        int blockingCells = owners.Count(o => o.Tier == WindowTier.Owner.AppWindow);
        List<string> blockingClasses = owners.Where(o => o.Tier == WindowTier.Owner.AppWindow)
            .Select(o => $"{o.ClassName}（pid {o.ProcessId}）")
            .Distinct().ToList();

        string shape = DescribeShape(calendar, monitor);
        List<string> outside = ForeignWindowsClearOf(rect);

        // 拒收只在「压在被测矩形上面」这一种情况。被测矩形外面的窗要报出来，
        // 但那一扇不影响这一帧的读数 —— 两件事不许合成一句。
        if (blockingCells > 0)
        {
            return new(false,
                $"拒收 —— 日历那一扇句柄 0x{calendar.Handle.ToInt64():X} {shape}；" +
                $"矩形 {rect} 里采样 {owners.Count} 格，归别家窗的有 {blockingCells} 格，" +
                $"类名 {blockingClasses.Count} 种：{string.Join("、", blockingClasses)}；" +
                "这一帧的读数来自别家的窗，不许拿它推导机制。" +
                (outside.Count > 0 ? $"另有 {outside.Count} 扇在矩形外面（不影响这一帧）：{string.Join("、", outside)}" : ""));
        }

        return new(true,
            $"句柄 0x{calendar.Handle.ToInt64():X}；类名 {calendar.ClassName}；{shape}；" +
            $"可见={(calendar.Visible ? "1" : "0")}；矩形 {rect}；" +
            $"矩形里采样 {owners.Count} 格：归这扇 {owners.Count(o => o.Tier == WindowTier.Owner.Ours)} 格、" +
            $"归壁纸层 {owners.Count(o => o.Tier == WindowTier.Owner.WallpaperLayer)} 格、" +
            $"归系统窗 {owners.Count(o => o.Tier == WindowTier.Owner.SystemWindow)} 格、" +
            $"归别家窗 {owners.Count(o => o.Tier == WindowTier.Owner.AppWindow)} 格。" +
            (outside.Count > 0 ? $"另有 {outside.Count} 扇在矩形外面（不影响这一帧）：{string.Join("、", outside)}" : ""));
    }

    private static string DescribeShape(WindowInfo calendar, MonitorInfo monitor)
    {
        IntPtr parent = Windows.ParentOf(calendar.Handle);
        string parentClass = Windows.ClassOf(parent);
        RectI rect = calendar.Rect;
        bool hugsRight = rect.Right == monitor.Work.Right;
        bool hugsBottom = rect.Bottom == monitor.Work.Bottom;
        int widthPercent = monitor.WorkWidth == 0 ? 0 : rect.Width * 100 / monitor.WorkWidth;
        int heightPercent = monitor.WorkHeight == 0 ? 0 : rect.Height * 100 / monitor.WorkHeight;

        return
            $"爹 0x{parent.ToInt64():X}/{parentClass}；那块屏 {monitor.Monitor} 工作区 {monitor.Work}；" +
            $"尺寸 = 工作区宽的 {widthPercent}% × 高的 {heightPercent}%；" +
            $"贴右边={(hugsRight ? "是" : "否")} 贴下边={(hugsBottom ? "是" : "否")}";
    }

    private static string Describe(WindowInfo w) =>
        $"0x{w.Handle.ToInt64():X} 矩形={w.Rect} 可见={(w.Visible ? "1" : "0")} 标题={w.Title}";

    // ---- 归谁 -------------------------------------------------------------------
    //
    // 2026-10-14：这里原来自带一份 `Owner`（五档）、一份 `TierOf`，还有第四张系统窗类名名单。
    // 那份名单的注释写着「照产品那一侧 `DesktopLayer.SystemWindows` 抄的那几样」——
    // **全仓搜 `SystemWindows`，代码里零成员**，出处指空。而产品那一侧原来只有四档
    // （`NoWindow` / `Ours` / `WallpaperLayer` / `AppWindow`），第五档只有这份认得。
    //
    // 现在四样（`Owner` / `TierOf` / 那份名单 / `IsSystemShell`）都删了，改用产品那一份：
    // 类型是 `WindowTier.Owner`、判据是 `WindowTier.TierOf`，正本在 `src/Shared/WindowTier.cs`，
    // 两个工程编的是同一个文件（测量程序那一条 `<Compile Include>` 在 `DebugProbe.csproj` 里）。
    // 那边第五档 `SystemWindow` 的来由（任务栏那一族和输入法那几个辅助窗）随判据一起搬过去了。
    //
    // 留在这一侧不搬的是**壁纸层那三种类名的名单**（`IsWallpaperLayer`）：两边各留一份，
    // 答案各拿自己那份判，判完递进 `WindowTier.TierOf` 的 `wallpaperLayer` 那个参数。

    internal readonly record struct PointOwner(int X, int Y, WindowTier.Owner Tier, string ClassName, int ProcessId, IntPtr Handle);

    /// <summary>
    /// 在被测矩形里取一片采样点，逐格问归谁。
    /// 采样点是内片的网格 —— 边和角都要问到，只问中心会漏掉「只压住一条边」那一种。
    /// </summary>
    internal static List<PointOwner> ProbeGrid(WindowInfo calendar, int steps)
    {
        var found = new List<PointOwner>();
        RectI rect = calendar.Rect;
        for (int i = 1; i <= steps; i++)
        {
            for (int j = 1; j <= steps; j++)
            {
                int x = rect.Left + rect.Width * i / (steps + 1);
                int y = rect.Top + rect.Height * j / (steps + 1);
                IntPtr hit = Windows.At(x, y);
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Windows.RootOf(hit);
                string rootClass = Windows.ClassOf(root);
                found.Add(new PointOwner(
                    x,
                    y,
                    WindowTier.TierOf(hit, root, rootClass, calendar.Handle, IsWallpaperLayer(rootClass)),
                    Windows.ClassOf(hit),
                    hit == IntPtr.Zero ? 0 : Windows.ProcessIdOf(hit),
                    hit));
            }
        }

        return found;
    }

    /// <summary>
    /// 被测矩形<b>外面</b>那些看得见的别家窗。报出来，别装作没看见 ——
    /// 但它们碰不到这一帧的读数，所以不拒收。两件事不许合成一句。
    /// </summary>
    internal static List<string> ForeignWindowsClearOf(RectI rect)
    {
        return Windows.TopLevelWindows()
            .Where(w => w.Visible && !ProductProcess.IsProduct(w.ProcessId))
            .Where(w => !IsWallpaperLayer(Windows.ClassOf(Windows.RootOf(w.Handle))))
            .Where(w => !Overlaps(w.Rect, rect))
            .Select(w => $"{w.ClassName}（pid {w.ProcessId}，{w.Rect}）")
            .Distinct()
            .ToList();
    }

    private static bool Overlaps(RectI a, RectI b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static string ScreenSummary()
    {
        List<string> rows = Windows.TopLevelWindows()
            .Where(w => w.Visible)
            .Select(w => $"{w.ClassName}={w.Rect}")
            .Distinct()
            .ToList();
        return rows.Count == 0 ? "一扇也没有" : string.Join("、", rows.Take(12));
    }

    // ---- 原样打印 ---------------------------------------------------------------

    /// <summary>
    /// 把屏幕上所有顶层窗原样打出来。
    /// <b>定位条件不许凭印象写</b> —— 这一发就是拿来读出真实的类名和尺寸的。
    /// </summary>
    private static void Dump()
    {
        List<WindowInfo> all = Windows.TopLevelWindows();
        MonitorInfo monitor = Windows.PrimaryMonitor();

        Console.Out.WriteLine($"主屏：{(monitor.IsPrimary ? "是" : "否")} 整块={monitor.Monitor} 工作区={monitor.Work}");

        // 桌面图标那一层的所有候选。连条数一起打出来 —— 挑错一扇，条数就是 0，
        // 而 0 条和「桌面上真的没有图标」看着是同一个数。
        List<WindowInfo> iconCandidates = Windows.IconLayerCandidates();
        if (iconCandidates.Count == 0)
        {
            Console.Out.WriteLine("图标层：壁纸层底下没有 SysListView32");
        }

        // 日历那一扇是**子窗**，不在上面那份顶层清单里 —— 这一发不把它列出来，
        // 就分不清「产品没建那一扇」和「这一发只数顶层窗」。
        Candidates candidates = Search();
        Console.Out.WriteLine(candidates.Host == null
            ? $"日历宿主：{IconLayerHostClass} 按「类名 + 可见 + 满屏」在 Progman 的后代里挑，挑不出来"
            : $"日历宿主：0x{candidates.Host.Handle.ToInt64():X}/{IconLayerHostClass} 矩形={candidates.Host.Rect}");
        foreach (WindowInfo w in candidates.Named)
        {
            Console.Out.WriteLine(
                $"日历那一扇候选：0x{w.Handle.ToInt64():X} 爹={Windows.ClassOf(Windows.ParentOf(w.Handle))} " +
                $"属主 pid={w.ProcessId}（{(ProductProcess.IsProduct(w.ProcessId) ? "产品" : "不是产品 —— 那一扇是壳的")}） " +
                $"矩形={w.Rect} 在屏内={(Windows.MonitorOf(w.Handle).Monitor.Contains(w.Rect) ? "1" : "0")} 可见={(w.Visible ? "1" : "0")}");
        }
        if (candidates.Named.Count == 0)
        {
            Console.Out.WriteLine($"日历那一扇候选：零扇 —— 图标层底下没有「{CalendarClass} + 带 WS_EX_LAYERED」的窗");
        }

        foreach (WindowInfo icon in iconCandidates)
        {
            Console.Out.WriteLine(
                $"图标层候选：0x{icon.Handle.ToInt64():X} 爹={Windows.ClassOf(Windows.ParentOf(icon.Handle))} " +
                $"矩形={icon.Rect} 条数={Windows.IconCount(icon.Handle)} " +
                $"错误码={Windows.LastIconCountError} IsWindow={(Windows.IsWindow(icon.Handle) ? "1" : "0")} " +
                $"消息={Windows.PingMessage(icon.Handle)} {Windows.IconCountPlain(icon.Handle)}");
        }

        // 消息那一路通不通：拿别的进程的几扇窗对一遍。图标层答不通的时候，
        // 要分清是「这一扇特殊」还是「整条路都不通」。
        foreach (WindowInfo probe in all
                     .Where(w => w.ClassName is "Progman" or "Engine" or "Chrome_WidgetWin_1")
                     .GroupBy(w => w.ClassName).Select(g => g.First()))
        {
            Console.Out.WriteLine(
                $"消息对照：pid={probe.ProcessId} {probe.ClassName} 0x{probe.Handle.ToInt64():X} → {Windows.PingMessage(probe.Handle)}");
        }

        foreach (WindowInfo w in all.OrderByDescending(w => w.ProcessId))
        {
            string name = ProductProcess.NameOf(w.ProcessId);
            bool product = ProductProcess.IsProduct(w.ProcessId);
            string parent = Windows.ClassOf(Windows.ParentOf(w.Handle));
            Console.Out.WriteLine(
                $"pid={w.ProcessId,-6} {(product ? "【产品】" : "      ")} {name,-34} " +
                $"句柄=0x{w.Handle.ToInt64():X} 类名={w.ClassName,-28} 爹={parent,-18} " +
                $"可见={(w.Visible ? "1" : "0")} 矩形={w.Rect} 标题={w.Title}");
        }
    }
}