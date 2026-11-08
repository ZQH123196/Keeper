using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 一发：图标层认不认屏幕上那一点落在一个图标上。
///
/// <para><b>这一发是被逼出来的</b>（2026-10-12）：产品把桥喂进去的两发点击丢了，
/// 理由那一行写的是「那一点上有一个桌面图标」，而量具用 UI Automation 读图标层，
/// 15 颗全在 <c>x ∈ [0,92]</c> —— 被丢的那一点在 (2048,688)，
/// 离最近的一颗图标有一千九百多像素。<b>两把尺子对不上，就必须先弄清哪一把错了。</b></para>
///
/// <para><b>判据是原样返回值，不是文档</b>。<c>LVM_HITTEST</c> 答什么，
/// 这一发不按头文件推，直接问出来：同一点问两遍（<c>SendMessage</c> 和
/// <c>SendMessageTimeout</c> 两路），再和 <c>UI Automation</c> 读的矩形摆在一起。
/// 两把尺子在哪一点上开始分家，分家那一行的返回值就是答案。</para>
///
/// <para>产品那一问把返回值<b>非负</b>当「有一个图标」
/// （<c>Win32.IconAt</c> / <c>IconHit</c>，见 <c>src/Win32.cs</c>）。
/// 这一发把两种读法都印出来，让读数自己挑是哪一种，
/// 不在这一份里替产品改判据 —— 改了要说清改在哪、为什么。</para>
///
/// <para>出处：工单 <c>09</c>（双击进编辑态）那一发的前置排查。</para>
/// </summary>
internal static class IconHit
{
    internal static int Run(string[] args)
    {
        List<(int X, int Y)> asked = Points(args);

        WindowInfo? layer = Windows.IconLayer();
        if (layer == null)
        {
            Console.Out.WriteLine("图标层读不到 —— 壁纸层底下没有枚举到 SysListView32，这一发答不了");
            return 1;
        }

        Console.Out.WriteLine($"图标层：0x{layer.Handle.ToInt64():X} pid={layer.ProcessId} 矩形={layer.Rect}");
        Console.Out.WriteLine($"完整性级别：{Windows.IntegrityLevel()}");
        Console.Out.WriteLine();

        // 第二把尺子：UI Automation 读的图标矩形。它是 07 已经选定的那一条读法，
        // 「这一点在不在某颗图标上」由它来判，两把尺子对着看。
        List<Icons.IconBox> boxes = Icons.ReadAll(out string note);
        Console.Out.WriteLine(note);
        foreach (Icons.IconBox box in boxes)
        {
            Console.Out.WriteLine($"  矩形 {box.Rect} 中心 ({box.Center.X},{box.Center.Y}) 「{box.Name}」");
        }

        Console.Out.WriteLine();

        if (asked.Count == 0) asked = DefaultPoints(boxes);
        if (asked.Count == 0)
        {
            Console.Out.WriteLine("既没给点、又读不到图标 —— 没有可问的点，这一发答不了");
            return 1;
        }

        int disagreements = 0;
        foreach ((int x, int y) in asked)
        {
            string raw = Windows.IconHitRaw(layer.Handle, x, y);
            long? returned = ReturnedNumber(raw);

            bool byUia = boxes.Any(b => b.Rect.Contains(x, y));
            bool byProduct = returned is { } r && r >= 0;      // 产品那一问：非负 = 有一个图标
            bool byFlip = returned is { } f && f < 0;          // 反过来的读法：负数 = 有一个图标

            string verdict = byUia == byProduct
                ? "两把尺子一致"
                : "**两把尺子不一致**";
            if (byUia != byProduct) disagreements++;

            string boxName = boxes.FirstOrDefault(b => b.Rect.Contains(x, y)).Name ?? "不在任何一颗图标上";

            Console.Out.WriteLine(
                $"{raw}；UI Automation 说「{(byUia ? "在" : "不在")}图标上」（{boxName}）；" +
                $"产品那一问（非负=有图标）判「{(byProduct ? "有图标" : "没图标")}」；" +
                $"反过来（负数=有图标）判「{(byFlip ? "有图标" : "没图标")}」—— {verdict}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(disagreements == 0
            ? "这一发答出来了：两把尺子每一行都对上，产品那一问的判据在这一批点上是对的。"
            : $"这一发答出来了：**{disagreements} 行对不上** —— 产品那一问在这一批点上有错，" +
              "错在哪由上面每一行的两个返回值说。");

        return 0;
    }

    /// <summary>命令行给的点，成对读。<code>icon-hit 100 200 300 400</code>。</summary>
    private static List<(int X, int Y)> Points(string[] args)
    {
        var found = new List<(int X, int Y)>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (int.TryParse(args[i], out int x) && int.TryParse(args[i + 1], out int y)) found.Add((x, y));
        }

        return found;
    }

    /// <summary>
    /// 没给点就用这一批。<b>一半是「准保在图标上」，一半是「离所有图标都很远」</b> ——
    /// 只有两边都有，判据才知道自己站在哪一边。
    /// </summary>
    private static List<(int X, int Y)> DefaultPoints(List<Icons.IconBox> boxes)
    {
        var points = new List<(int X, int Y)>();
        foreach (Icons.IconBox box in boxes.Take(2)) points.Add((box.Center.X, box.Center.Y));
        points.Add((2048, 688));   // 产品丢掉桥那两发点击的那一点（量于 10-12）
        points.Add((500, 1000));
        points.Add((1200, 60));
        points.Add((300, 1200));
        return points;
    }

    /// <summary>从 <see cref="Windows.IconHitRaw"/> 那一行里把原样返回值抠出来。
    /// 抠不出来就答 null —— <b>不拿 0 当返回值</b>，0 是一个合法的返回值。</summary>
    private static long? ReturnedNumber(string raw)
    {
        const string marker = "SendMessageTimeout ";
        int at = raw.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        string tail = raw[(at + marker.Length)..];
        int stop = tail.IndexOfAny(new[] { '、', '；', ' ', '（' });
        if (stop < 0) return null;
        return long.TryParse(tail[..stop], out long value) ? value : null;
    }
}