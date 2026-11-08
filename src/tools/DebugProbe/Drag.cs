using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 工单 07：发真的鼠标事件，把一枚桌面图标拖出去再拖回来。<b>必须回到原像素。</b>
///
/// <para><b>拖之前和拖之后走同一个读法读两次。</b>UI Automation 有两条路
/// （从句柄、从根），两条给的矩形不一样（量于 10-12：同一枚图标高 113 对 108）——
/// 混着比会得出「图标没回原像素」这种假结论。</para>
///
/// <para><b>回不去就停下，不补第二次。</b>连着拖会把图标拖到别处，越拖越乱。
/// 报错停下，好让所有者手动拖回去。</para>
///
/// <para><b>判「发的是真事件」靠钩子</b>：往图标层发消息也能让图标动，
/// 而那一种钩子一声不响。<b>动了 + 钩子记到按下和松开</b>才算真事件走过了系统输入队列。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/07-icon-drag-round-trip.md</c>。</para>
/// </summary>
internal static class DragRoundTrip
{
    /// <summary>分几步挪。一步跳过去图标层不认这次拖放，
    /// 结果是「拖了，没动」—— 而那个结果看着像「产品挡了拖放」。</summary>
    private const int Steps = 12;

    /// <summary>每一步之间停多久。</summary>
    private const int Settle = 40;

    /// <summary>读完之后等多久再读。拖放落到桌面层上是异步的，
    /// 读太快会读到还没落定的那一帧。</summary>
    private const int AfterDrag = 600;

    internal static int Run(string[] args)
    {
        string wanted = ReadArg(args, "--icon") ?? string.Empty;
        int rounds = ReadInt(args, "--rounds", 1);

        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "桌面图标真拖放往返",
            "发真的鼠标事件把一枚图标拖出去再拖回来，坐标必须和拖之前完全一致；" +
            "回不去就报错停下，不补第二次去凑");

        List<Icons.IconBox> before = Icons.ReadAll(out string where);
        if (before.Count == 0)
        {
            shot.Finish($"中止 —— {where}，桌面上一个图标都没读到，没法拖", "无");
            return Find.Refused;
        }

        Icons.IconBox target = wanted.Length == 0
            ? before[0]
            : before.FirstOrDefault(i => i.Name.Contains(wanted, StringComparison.Ordinal));

        if (target.Name.Length == 0)
        {
            shot.Finish(
                $"中止 —— 没找到叫「{wanted}」的图标。现在这几枚：{string.Join("、", before.Take(8).Select(i => i.Name))}",
                "无");
            return Find.Refused;
        }

        // **按下去之前先看那一点归谁。** 被别家的窗压着的时候，拖放照样发得出去
        // （钩子会响、图标却不动）—— 那个「不动」和「图标层不认拖放」在读数上一样。
        // 这一问把两者分开：压着就报压着，别报「拖不动」。
        IntPtr underTarget = Windows.At(target.Center.X, target.Center.Y);
        if (!Find.IsWallpaperLayer(Windows.ClassOf(Windows.RootOf(underTarget))) &&
            underTarget != IntPtr.Zero &&
            !IsIconLayerUnder(underTarget))
        {
            shot.Finish(
                $"中止 —— 「{target.Name}」的正中心 ({target.Center.X},{target.Center.Y}) 上压着 " +
                $"{Windows.ClassOf(underTarget)}（pid {Windows.ProcessIdOf(underTarget)}），不是图标层。" +
                "按下去的那一下会落在那一扇上，图标当然不动。先发一次 Win+D 把桌面端到前面再跑这一发",
                "无");
            return Find.Refused;
        }

        using Input.MouseHook hook = new();
        if (!hook.Installed)
        {
            shot.Finish($"中止 —— 低级鼠标钩子装不上，错误码 {hook.InstallError}，这一发答不了「发的是不是真事件」", "无");
            return Find.Refused;
        }

        string beforePath = System.IO.Path.Combine(outDir, "icon-before.png");
        Archive(before, target, beforePath);

        // ---- 「鼠标是真事件」那一问，走产品自己那道口 ------------------------------
        //
        // 钩子数到按下和松开，只证明**这一发在系统里过了**。
        // 工单 07 那条判据要的是另一半：**产品自己那一侧的读数也动了**。
        // 05 那道口的第五个动词 `mouse`（2026-10-13 所有者定）答的就是这个。
        //
        // **接不上就报接不上，不许静默跳过这一问** —— 「没量到」和「量到了没问题」
        // 在读数上长得一模一样。
        using Port? port = Port.Open();
        string mouseBefore = port?.Ask("mouse") ?? string.Empty;
        if (port == null)
        {
            shot.Finish(
                $"中止 —— 接不上产品那道口，「产品那一侧的鼠标桥读数也动了」这一问答不出来。" +
                $"试过的管路：{Port.Attempts}。{Port.LastFailure}",
                beforePath);
            return Find.Refused;
        }

        var roundReports = new List<string>();
        bool allBack = true;

        for (int round = 1; round <= rounds; round++)
        {
            // 每轮重新读一次起点 —— 上一轮的读数不能当下轮的起点用。
            List<Icons.IconBox> start = Icons.ReadAll(out _);
            Icons.IconBox from = start.FirstOrDefault(i => i.Name == target.Name);
            if (from.Name.Length == 0)
            {
                roundReports.Add($"第 {round} 轮：图标不见了，中止");
                allBack = false;
                break;
            }

            // 拖去一个空地方。选右侧一大片空地，别压到别的图标上。
            (int toX, int toY) = EmptySpot(start, from.Center);

            hook.Reset();
            string howOut = Input.Drag(from.Center.X, from.Center.Y, toX, toY, Steps, Settle, hook.PumpOnce);
            hook.PumpFor(AfterDrag);

            List<Icons.IconBox> middle = Icons.ReadAll(out _);
            Icons.IconBox moved = middle.FirstOrDefault(i => i.Name == from.Name);
            bool didMove = moved.Name.Length > 0 && moved.Rect != from.Rect;

            if (!didMove)
            {
                // 拖不动有几种完全不同的原因，而它们在这一支上长得一模一样。
                // 所以这里把能分的读数一次摆出来：钩子有没有响、按下那一点归谁、前台是谁。
                IntPtr under = Windows.At(from.Center.X, from.Center.Y);
                roundReports.Add(
                    $"第 {round} 轮：拖出去之后坐标没变（{from.Rect}），这一发拖不动。" +
                    $"钩子记到按下 {hook.Presses} 次、松开 {hook.Releases} 次、移动 {hook.Moves} 次；" +
                    $"按下那一点 ({from.Center.X},{from.Center.Y}) 命中 {Windows.ClassOf(under)}" +
                    $"（pid {Windows.ProcessIdOf(under)}）；前台 {Windows.ForegroundDescription()}");
                allBack = false;
                break;
            }

            // 拖回去。**只拖这一次** —— 拖完还不对就报错停，不再来一轮去凑。
            // 钩子**不在这里清零**：清了的话报出来的是后一趟的数，
            // 而那一栏要答的是「这一轮一共发了几次按下和松开」（量于 10-12：清一次就少一半）。
            Input.Drag(moved.Center.X, moved.Center.Y, from.Center.X, from.Center.Y, Steps, Settle, hook.PumpOnce);
            hook.PumpFor(AfterDrag);

            List<Icons.IconBox> after = Icons.ReadAll(out _);
            Icons.IconBox back = after.FirstOrDefault(i => i.Name == from.Name);

            int countDelta = after.Count - start.Count;
            bool samePixel = back.Name.Length > 0 && back.Rect == from.Rect;

            roundReports.Add(
                $"第 {round} 轮：拖出去（{howOut}）→ 挪到 {moved.Rect}；" +
                $"拖回去 → {back.Rect}；" +
                $"和拖之前比：{(samePixel ? "一致" : $"**不一致**（拖之前 {from.Rect}，现在 {back.Rect}）")}；" +
                $"钩子记到按下 {hook.Presses} 次、松开 {hook.Releases} 次、移动 {hook.Moves} 次；" +
                $"图标数 {start.Count} → {after.Count}" +
                (countDelta == 0 ? string.Empty : $"（**多了/少了 {-countDelta} 个**）"));

            if (!samePixel || countDelta != 0)
            {
                allBack = false;
                break;   // 停下，不补第二次
            }
        }

        List<Icons.IconBox> final = Icons.ReadAll(out _);
        string afterPath = System.IO.Path.Combine(outDir, "icon-after.png");
        Archive(final, final.FirstOrDefault(i => i.Name == target.Name), afterPath);

        Icons.IconBox end = final.FirstOrDefault(i => i.Name == target.Name);
        bool finalSame = end.Name.Length > 0 && end.Rect == target.Rect;

        // 产品那一侧的读数：这一趟按下去那么多次，它的数有没有跟着动。
        string mouseAfter = port!.Ask("mouse");
        int fedDelta = Port.Counter(mouseAfter, "喂=") - Port.Counter(mouseBefore, "喂=");
        int lostDelta = Port.Counter(mouseAfter, "丢=") - Port.Counter(mouseBefore, "丢=");
        bool productMoved = fedDelta != 0 || lostDelta != 0;
        string productSide =
            $"产品那一侧（问 `mouse`，05 那道口的第五个动词）：拖之前「{Trim(mouseBefore)}」，" +
            $"跑完「{Trim(mouseAfter)}」—— 喂 {fedDelta:+#;-#;0} 发、丢 {lostDelta:+#;-#;0} 发；" +
            (productMoved
                ? "**产品那一侧的读数动了** —— 这一发是真事件，产品自己的钩子看见了它"
                : "**产品那一侧的读数一格没动** —— 这一发要么没进系统，要么产品那边没在看");

        if (allBack && finalSame)
        {
            shot.Finish(
                $"{where}拖之前一共 {before.Count} 枚，「{target.Name}」在 {target.Rect}；" +
                $"{string.Join("；", roundReports)}。回到原像素，图标一个没多没少。" +
                $"{productSide}。" +
                $"拖之前和拖之后各留了一帧，两帧的归谁写在图里",
                afterPath);
            return productMoved ? 0 : Find.Refused;
        }

        shot.Finish(
            $"{where}{string.Join("；", roundReports)}。" +
            $"**没有回到原像素** —— 「{target.Name}」现在在 {end.Rect}（拖之前 {target.Rect}）。" +
            $"{productSide}。" +
            "工具已经停下，没有再补一次拖放去凑；请手动把它拖回去",
            afterPath);
        return Find.Refused;
    }

    /// <summary><c>mouse</c> 那一行太长时只留前半句和后半句的头几个字 ——
    /// 它带着「最近一发」那一整句判据，贴进四行输出会把结论淹掉。</summary>
    private static string Trim(string line)
    {
        if (line.Length <= 160) return line;
        int cut = line.IndexOf("最近一发[", StringComparison.Ordinal);
        return cut > 0 ? line[..cut].TrimEnd(' ') + " … 最近一发[…" : line[..160] + "…";
    }

    /// <summary>命中测试答的那一扇是不是图标层本身。点可能落在图标层的子窗口上，
    /// 所以往上一层看一眼，落在子树里就算。</summary>
    private static bool IsIconLayerUnder(IntPtr hit)
    {
        WindowInfo? layer = Windows.IconLayer();
        for (IntPtr at = hit; at != IntPtr.Zero; at = Windows.ParentOf(at))
        {
            if (layer != null && at == layer.Handle) return true;
        }

        return false;
    }

    /// <summary>
    /// 挑一个空地方当落点。<b>不许落在别的图标上</b> —— 落在上面的话，
    /// 拖回去的时候分不清是谁回了原位。
    /// </summary>
    private static (int X, int Y) EmptySpot(List<Icons.IconBox> icons, PointI from)
    {
        MonitorInfo monitor = Windows.PrimaryMonitor();
        for (int y = monitor.Work.Bottom - 120; y > from.Y; y -= 140)
        {
            for (int x = from.X + 240; x < monitor.Work.Right - 120; x += 140)
            {
                PointI spot = new(x, y);
                if (icons.All(i => !i.Rect.Contains(spot.X, spot.Y))) return (spot.X, spot.Y);
            }
        }

        return (from.X, Math.Max(monitor.Work.Top + 60, from.Y - 240));
    }

    /// <summary>留一帧，并且把归谁写进图里 —— 工单 07 要「拖之前和拖之后各留一帧，各带一行归谁」。</summary>
    private static void Archive(List<Icons.IconBox> icons, Icons.IconBox target, string path)
    {
        MonitorInfo monitor = Windows.PrimaryMonitor();
        RectI region = icons.Count == 0
            ? new RectI(0, 0, 400, 400)
            : new RectI(0, 0, Math.Min(1000, monitor.Work.Right), Math.Min(1200, monitor.Work.Bottom));
        Frame frame = Screen.Grab(region);
        frame.OwnerText = $"拖放读数：「{target.Name}」在 {target.Rect}；图标层共 {icons.Count} 枚";
        frame.SavePng(path);
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? raw = ReadArg(args, name);
        return raw != null && int.TryParse(raw, out int value) && value > 0 ? value : fallback;
    }
}
