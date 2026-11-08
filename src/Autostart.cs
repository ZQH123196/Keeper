using System;
using System.Collections.Generic;

namespace Keeper;

/// <summary>「登录时启动 Keeper」在 Windows 上的落地：`HKCU\Software\Microsoft\Windows\
/// CurrentVersion\Run` 里的一个字符串值。
///
/// 方向是单向的 —— <c>autostart</c> 那个键是意图，注册表是它的效果，程序不反过来读注册表
/// 决定行为（设计文档 `docs/design/config.md`「不许有只在界面里的设置」那一条，禁止第二个配置面）。所以这里只有
/// 「按意图把现场改成一致」这一条路，也就是 `Settings.cs` 那条声明注释里写的「启动时和它对账」。
///
/// 注册表不走 .NET 的 <c>Microsoft.Win32.Registry</c>：那是 net8.0 上的一个 NuGet 依赖，
/// 而这台机器上的构建是离线的 ——  advapi32 那四发和这个文件里其余的 Win32 一样，是一行 DllImport。
/// <see cref="Decide"/> 和 <see cref="SelfTest"/> 是分开的两件事，见那里。</summary>
internal static class Autostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Keeper";

    /// 登录时该跑的那一条。打包之后就是那个 exe；从编辑器或命令行跑的时候光交一个二进制
    /// 会开出一个编辑器，所以补上工程目录和入口场景。
    public static string Command(string exe, string projectDir, bool fromEditor)
    {
        // _console.exe 是个壳，它下面才是真正的游戏进程 —— 拿它当登录项会每次开机多一个控制台。
        var windowed = exe.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase)
            ? exe[..^"_console.exe".Length] + ".exe"
            : exe;
        return fromEditor
            ? $"\"{windowed}\" --path \"{projectDir}\" res://src/App.tscn"
            : $"\"{windowed}\"";
    }

    public enum Move
    {
        Nothing,
        Write,
        Delete,
    }

    /// 意图和现场之间只有这三个可能。「没有这个值」和「有一个空值」不是一回事，
    /// 所以 current 是可空的，而不是拿空串当缺席。
    public static Move Decide(bool wanted, string? current, string command) =>
        !wanted ? current is null ? Move.Nothing : Move.Delete
        : current == command ? Move.Nothing : Move.Write;

    /// 对一次账，返回那一句该进日志的话。
    public static string Sync(bool wanted, string command)
    {
        var current = Win32.RegReadString(RunKey, ValueName);
        switch (Decide(wanted, current, command))
        {
            case Move.Delete:
                return Win32.RegDeleteValue(RunKey, ValueName) ?? "登录项删掉了";
            case Move.Write:
                var failed = Win32.RegWriteString(RunKey, ValueName, command);
                return failed ?? $"登录项写上了：{command}";
            default:
                return $"登录项本来{(wanted ? "就在" : "没有")}，一个字没动";
        }
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf LoginItem = new(
        () => "登录项通过：六种意图各判对一次，写读删在临时键上真的走了一遍",
        "登录项", SelfTest);

    /// <summary>两件事分开证：意图和现场之间那张表是纯的，整个跑一遍不碰注册表；
    /// 碰注册表的那三只手（写、读、删）在 HKCU 下一个自带自灭的临时键里真走一遍 ——
    /// 登录项本身一个字节都不动，那是用户下次开机的事。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        const string cmd = "\"C:\\Keeper\\keeper.exe\"";

        foreach (var (wanted, current, expected) in new (bool, string?, Move)[]
        {
            (true, cmd, Move.Nothing),          // 已经在，而且就是这一条
            (true, null, Move.Write),           // 说要，那里没有
            (true, "\"C:\\老的那条.exe\"", Move.Write),   // 那里有一条别的
            (true, "", Move.Write),             // 空值不是「在」
            (false, null, Move.Nothing),        // 说不要，本来就没有 —— 不该去删
            (false, cmd, Move.Delete),          // 说不要，那里有一条
        })
        {
            var got = Decide(wanted, current, cmd);
            if (got != expected)
                problems.Add($"意图={wanted} 现场={(current == null ? "没有这个值" : $"\"{current}\"")} " +
                             $"该判 {expected}，判成了 {got}");
        }

        var editor = Command("C:\\Godot\\Godot_v4.7.2_console.exe", "D:\\project\\Keeper", true);
        if (!editor.StartsWith("\"C:\\Godot\\Godot_v4.7.2.exe\""))
            problems.Add($"编辑器那一跑没把 _console 的壳换掉，登录时会多一个控制台：{editor}");
        if (!editor.Contains("--path \"D:\\project\\Keeper\"") || !editor.EndsWith("res://src/App.tscn"))
            problems.Add($"编辑器那一跑少了工程目录或入口场景，登录时会开一个编辑器：{editor}");
        var shipped = Command("D:\\keeper\\keeper.exe", "D:\\project\\Keeper", false);
        if (shipped != "\"D:\\keeper\\keeper.exe\"")
            problems.Add($"打包之后该只交那一个 exe，交出去的是：{shipped}");

        // 三只手真的动一次。键名带 pid，收尾自己删掉自己建的那个键。
        var scratch = $@"Software\Keeper-selfcheck-{Environment.ProcessId}";
        try
        {
            if (Win32.RegReadString(scratch, ValueName) != null)
                problems.Add("临时键还没建就有值了，读的这条路不可信");
            var failed = Win32.RegWriteString(scratch, ValueName, cmd);
            if (failed != null) problems.Add(failed);
            else if (Win32.RegReadString(scratch, ValueName) != cmd)
                problems.Add($"写进去和读回来不是同一串：{Win32.RegReadString(scratch, ValueName)}");

            var longer = cmd + " --seconds 5";
            Win32.RegWriteString(scratch, ValueName, longer);
            if (Win32.RegReadString(scratch, ValueName) != longer)
                problems.Add("改一个已有的值没生效（写的时候没盖住旧的？）");

            failed = Win32.RegDeleteValue(scratch, ValueName);
            if (failed != null) problems.Add(failed);
            else if (Win32.RegReadString(scratch, ValueName) != null)
                problems.Add("删掉了还能读回来");
            if (Win32.RegDeleteValue(scratch, ValueName) != null)
                problems.Add("删一个不在的值该当作没发生，它报了失败");
        }
        finally
        {
            var left = Win32.RegDeleteKey(scratch);
            if (left != null) problems.Add($"自检的临时键没收干净：{left}");
        }
        return problems;
    }
}
