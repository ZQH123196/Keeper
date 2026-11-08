using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DebugProbe;

/// <summary>
/// 桌面量测工具 —— 独立进程，纯 C#，不开引擎。
///
/// <para>它要发真的 Win+D、要读<b>别的进程</b>眼里的 z 序、要看清屏幕上谁压着谁。
/// 这三件都要直接摸 Win32，而和引擎在同一个进程里量到的是「引擎以为的那一份」——
/// 那一份不作数。</para>
///
/// <para>代价：它拿不到引擎自己渲染的那张纹理。「引擎画成什么样」那一问只能看屏。
/// 这一族栽过的三次全都栽在「屏幕上真实的那一份」上，所以这条代价认下来。</para>
///
/// <para>出处：<c>.scratch/debug-tool/SPEC.md</c>。取向在 <c>docs/adr/</c>，
/// 实测结果在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。</para>
/// </summary>
internal static class Program
{
    private const string Version = "1.0.0";

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* 控制台不支持就算了 */ }

        // 第二件事：**自己站的位置对不对**。这一族每一发都要读别的进程，
        // 而仓库目录上那两条 ACL 会让从这里启动的 exe 降成低完整性 ——
        // 那样读数全是「读不到」，而「读不到」看起来和「这条路走不通」一模一样。
        // 换地方重跑自己，换不成就当场拒收（理由和读数在 Relaunch.cs）。
        int relayed = Relaunch.Run(args);
        if (relayed != Relaunch.Continue) return relayed;

        // 进程起来第一件事。不设的话，在缩放的屏上抓到的矩形和屏幕上那扇对不上。
        Windows.MakeDpiAware();

        // 有时候量具不是从一个控制台起来的（桌面快捷方式、经 explorer 起）。
        // 那时那四行会刷进一个没人看得见的窗口，所以留一个落盘的口子。
        string? logPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--log") logPath = args[i + 1];
        }

        if (logPath != null) LogTo = logPath;

        if (args.Length == 0 || (args.Length == 2 && args[0] == "--log"))
        {
            Usage();
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "selftest" => SelfTest(),
                "find" => Find.Run(args.Skip(1).ToArray()),
                "clean" => Clean.Run(args.Skip(1).ToArray()),
                "input-probe" => InputProbe.Run(args.Skip(1).ToArray()),
                "shot" => FrameShot.Run(args.Skip(1).ToArray()),
                "ask" => Ask.Run(),
                "icons" => Icons.Run(args.Skip(1).ToArray()),
                "icon-hit" => IconHit.Run(args.Skip(1).ToArray()),
                "tray" => Tray.Run(args.Skip(1).ToArray()),
                "cover" => Cover.Run(args.Skip(1).ToArray()),
                "refuse" => Refuse.Run(args.Skip(1).ToArray()),
                "drag" => DragRoundTrip.Run(args.Skip(1).ToArray()),
                "embed" => Embed.Run(args.Skip(1).ToArray()),
                "layered" => Layered.Run(args.Skip(1).ToArray()),
                "sweep" => Sweep.Run(args.Skip(1).ToArray()),
                "edit" => EditProbe.Run(args.Skip(1).ToArray()),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"这一发崩了：{ex.GetType().Name}：{ex.Message}");
            return 3;
        }
    }

    /// <summary>那一行给了，那四行就同时落一份在这个文件里。</summary>
    private static string? LogTo
    {
        get => _logTo;
        set
        {
            _logTo = value;
            // 赋 null 就是「不落盘」，那一支要把写回原样那个控制台。
            if (value == null)
            {
                return;
            }

            Console.SetOut(new TeeWriter(Console.Out, value));
        }
    }

    private static string? _logTo;

    private static void Usage()
    {
        Console.Out.WriteLine("用法：DebugProbe <子命令>");
        Console.Out.WriteLine("子命令：");
        Console.Out.WriteLine("  selftest   打印版本和它自己的 exe 路径");
        Console.Out.WriteLine("  find       找日历那扇窗，并报出每个点归谁");
        Console.Out.WriteLine("  shot       抓一帧，和一张基线帧比，日历那一块上有没有内容（--baseline 指基线帧）");
        Console.Out.WriteLine("  clean      发真的 Win+D 清场");
        Console.Out.WriteLine("  input-probe  输入那一路通不通");
        Console.Out.WriteLine("  ask       过那道口，问产品那几句");
        Console.Out.WriteLine("  icons     读桌面图标层：条目数、每条的矩形、读法哪一条通");
        Console.Out.WriteLine("  refuse    先验尺子自己：当场造一扇窗压上去，看尺子抓不抓得住");
        Console.Out.WriteLine("  drag      把一枚桌面图标拖出去再拖回来，必须回到原像素");
        Console.Out.WriteLine("  embed     把日历那一扇挂进桌面层，连读若干秒，看产品每秒那一下怎么动它");
        Console.Out.WriteLine("  layered   层叠子窗打进图标层画不画（工单 28 第 0 步：UpdateLayeredWindow 那一条路）");
        Console.Out.WriteLine("  sweep     Win+D 前后各抓一帧，比矩形、可见、z 序和同一个点");
    }

    private static int Unknown(string name)
    {
        Console.Error.WriteLine($"没有这个子命令：{name}");
        Usage();
        return 2;
    }

    /// <summary>
    /// 只证明三件事：它自己能起来；起来的时候没有引擎那一扇窗；Keeper 没在跑也不卡住。
    /// 这一发不量任何产品行为。
    /// </summary>
    private static int SelfTest()
    {
        using Shot shot = Shot.Begin(
            "selftest",
            "不依赖产品进程 —— Keeper 没在跑时这一发照样答完，不卡住也不崩");

        var watch = Stopwatch.StartNew();
        List<WindowInfo> own = Windows.TopLevelWindows()
            .Where(w => w.ProcessId == Environment.ProcessId).ToList();
        List<WindowInfo> others = Windows.TopLevelWindowsOutside(Environment.ProcessId);
        bool productRunning = others.Any(w => ProductProcess.IsProduct(w.ProcessId));
        watch.Stop();

        string exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        int engineWindows = own.Count(w => w.ClassName.Contains("Godot", StringComparison.OrdinalIgnoreCase));

        shot.Finish(
            $"版本 {Version}；本进程 DebugProbe；exe {exe}；" +
            $"本进程顶层窗 {own.Count} 扇（其中引擎窗 {engineWindows} 扇）；" +
            $"Keeper 在跑：{(productRunning ? "是" : "否")}；" +
            $"用时 {watch.ElapsedMilliseconds} 毫秒",
            "无");

        return 0;
    }
}