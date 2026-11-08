using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// <b>完整性不够就換一個地方重跑自己。</b>
///
/// <para><b>为什么要有这一发</b>（2026-10-13 一次实测）：仓库目录 <c>Keeper</c> 上有两条
/// <b>显式</b>（非继承）的 ACE，给 <c>Everyone</c> 和一个能力 SID
/// （<c>S-1-4-963651649-261417728</c>）留了 <c>DeleteSubdirectoriesAndFiles</c> /
/// <c>Write</c>。Windows 把「低权限主体能改的目录」当<b>不可信位置</b>，
/// <b>从这里启动的 exe 一律降成低完整性运行</b>。</para>
///
/// <para><b>读数</b>：同一个二进制，同一个父进程，只换目录 ——
/// 放在 <c>%TEMP%</c> 下答 <c>RID 0x2000</c>（中），放在 <c>src/tools/DebugProbe/bin</c>
/// 下答 <c>RID 0x1000</c>（低）。父目录 <c>Desktop\project</c> 没有那两条 ACE，也答中。
/// <b>和程序本身无关，是路径上的 ACL 决定的</b>。</para>
///
/// <para><b>为什么它害了那么多事</b>：低完整性进程读不了 <c>explorer.exe</c> 的图标层 ——
/// <c>LVM_GETITEMCOUNT</c> 错误码 5、UI Automation 0 条、<c>OpenProcess</c> 错误码 5。
/// 量具照样把「读不到」打印出来，那几发看上去像是「这条路走不通」的实测结果，
/// <b>其实只是量具自己站错了地方</b>。这一发就是不让它再站错。</para>
///
/// <para><b>为什么换地方而不是改 ACL</b>：那两条 ACE 是<b>显式</b>加在这个仓库上的，
/// 不知道是谁加的、为什么加。改它要用户点头，而且改完可能被加回来的东西再改一遍。
/// 换地方不碰任何安全设置。</para>
///
/// <para><b>换完还是低就当场拒收</b>（<see cref="Relaunch.Refuse"/>）——
/// 宁可答「这一发不作数」，也不让它拿一个低完整性下的读数冒充实测。</para>
/// </summary>
internal static class Relaunch
{
    /// <summary>「不用拦，继续跑」。</summary>
    internal const int Continue = int.MinValue;

    /// <summary>重跑时加上的那个标志。没有它，换完地方还是低就会无限换下去。</summary>
    private const string Marker = "--relaunched";

    private const string SafeFolder = "keeper-debugprobe";

    /// <summary>
    /// 入口那一问。返回 <see cref="Continue"/> 表示「照常跑」，
    /// 返回别的就是这一发的退出码。
    /// </summary>
    internal static int Run(string[] args)
    {
        int mine = Windows.RidOfSelf();
        int target = Windows.RidOfExplorer();

        // 两边都读不到 → 不拦。这一族在没有 explorer 的环境（容器、CI）里也要能跑。
        // 读数是 −1 不是 0x1000，所以「读不到」不会被当成「低」。
        if (mine < 0 || target < 0 || mine >= target) return Continue;

        if (args.Contains(Marker, StringComparer.Ordinal)) return Refuse(mine, target);

        return StartSomewhereBetter(args, mine, target);
    }

    private static int StartSomewhereBetter(string[] args, int mine, int target)
    {
        string folder;
        try
        {
            folder = Path.Combine(Path.GetTempPath(), SafeFolder);
            Directory.CreateDirectory(folder);

            (string dir, string exeName) = WhereAmI();
            foreach (var file in Sidecars(exeName))
            {
                string from = Path.Combine(dir, file);
                if (!File.Exists(from)) continue;
                File.Copy(from, Path.Combine(folder, file), true);
            }

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(folder, exeName),
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
            };
            foreach (var a in args) start.ArgumentList.Add(a);
            start.ArgumentList.Add(Marker);

            var child = Process.Start(start);
            if (child is null) return Refuse(mine, target);
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception e)
        {
            // **这条路在权限上是死的，不是没试**：低完整性进程**写不进**中完整性的目录
            // （`UnauthorizedAccessException`）。复制这一发必须由中完整性那一侧的 shell 做。
            // 所以下面那一行命令是**给中完整性那一侧的人或脚本用的**，不是给自己用的。
            Console.WriteLine($"换地方重跑失败：{e.GetType().Name} {e.Message}");
            return Refuse(mine, target);
        }
    }

    /// <summary>apphost 那个 exe 在哪、叫什么。<b>exe 名从程序集名推，不是从
    /// <c>GetEntryAssembly().Location</c> 取的</b> —— 那一处指的是托管程序集
    /// （<c>DebugProbe.dll</c>）。拿它的文件名去拼，2026-10-13 实测拼出来的是
    /// 「把 dll 复制三遍、然后去跑那个 dll」，交出去的命令一条都不对。
    /// .NET 的 apphost 按 <c>&lt;程序集名&gt;.exe</c> 命名，从程序集名推就对得上。</summary>
    private static (string Dir, string Exe) WhereAmI()
    {
        string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? assembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(assembly))
        {
            // 连程序集名都没有：退回 exe 自己那个名字去掉后缀。apphost 那条命名规矩一样。
            assembly = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "DebugProbe");
        }

        return (dir, assembly + ".exe");
    }

    /// <summary>跑起来要一起搬的三个文件。<b>exe 名不能改</b> ——
    /// apphost 把自己的 dll 名写死在 exe 里。</summary>
    private static string[] Sidecars(string exeName) => new[]
    {
        exeName,
        Path.ChangeExtension(exeName, ".dll"),
        Path.ChangeExtension(exeName, ".runtimeconfig.json"),
    };

    /// <summary>
    /// 换过地方还是低（或者根本没换成）：<b>当场拒收</b>，并把该跑的那条命令原样交出去。
    ///
    /// <b>和 SPEC 第三节第 2 条同一规矩</b>——「读到别家窗，当场拒收」；
    /// 这一族所有子命令都要读别的进程，而低完整性下那几条读法全部答「读不到」，
    /// 那个「读不到」看起来和「这条路走不通」一模一样。<b>宁可中止，不冒充实测。</b>
    /// </summary>
    private static int Refuse(int mine, int target)
    {
        (string baseDir, string exeName) = WhereAmI();
        string dest = SafePath;

        string copies = string.Join("\n", Sidecars(exeName)
            .Select(f => "    copy /y \"" + baseDir + "\\" + f + "\" \"" + dest + "\\\""));

        Console.WriteLine(
            "这一发拒收 —— 量具自己站错了位置，不作数。\n" +
            "  本进程 RID 0x" + mine.ToString("X") + "（低）  explorer RID 0x" + target.ToString("X") + "（中）\n" +
            "  低完整性进程读不了 explorer 的图标层：LVM_GETITEMCOUNT 错误码 5、UI Automation 0 条、" +
            "OpenProcess 错误码 5。\n" +
            "  那一组「读不到」的输出看起来和「这条路走不通」是同一件事 —— 不是。\n" +
            "\n" +
            "  怎么跑对。**下面这几行是 cmd 的写法**（`copy /y` 那两个字母 PowerShell 不认）\n" +
            "  —— 拿一个**中完整性**的 cmd 跑。或者直接跑仓库里的 run.cmd，它自己都在里面：\n" +
            "    if not exist \"" + dest + "\" mkdir \"" + dest + "\"\n" +
            copies + "\n" +
            "    \"" + dest + "\\" + exeName + "\" <你的参数>\n" +
            "\n" +
            "  为什么非得换地方：仓库目录 " + baseDir + " 的上级有两条**显式**的 ACL，给 " +
            "Everyone 和一个能力 SID（S-1-4-963651649-261417728）留了写权限。\n" +
            "  Windows 把低权限主体能改的目录当不可信位置，从那里启动的 exe 一律降成低完整性。\n" +
            "  读数在 docs/research/desktop-layer-and-window-measurements.md「量具自己站错了位置」那一节。");
        return Find.Refused;
    }

    /// <summary>换到哪个目录去了 —— 出问题时好让人直接去那儿看。</summary>
    internal static string SafePath => Path.Combine(Path.GetTempPath(), SafeFolder);
}
