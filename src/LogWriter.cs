using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Keeper;

/// <summary>写实测结果的笔。
///
/// <b>为什么要有这个类</b> —— 2026-09-29 之前有三份实施：<c>App.Log</c>、<c>Probe.Log</c>、
/// <c>FadeRun.Log</c>。三份里只有 App 那一份带着两道保护：把落单的代理对换成 <c>'?'</c>，
/// 以及把编码钉成不带 BOM 的 UTF-8。另外两份是裸的 <c>File.AppendAllText(path, line)</c> ——
/// 而不带 encoding 的那一发在 Mono 上走系统 ANSI 码页（这台机器 936/GBK），
/// 对写不出的字符是<b>「抛」不是「换」</b>。
///
/// 这不是假设的风险，是出过的事：2026-09-27 那一跑就是被一行日志断在 <c>_Ready</c> 半路的 ——
/// 托盘收件窗的标题读回来一个落单的代理对（<c>\uDC7F</c>），整个启动断在那里，测量程序那一跑挂着不动。
/// 崩是堵住了（欠账工单 <c>.scratch/keeper-debt/issues/06-tray-window-title-garbled.md</c> 记着根因还没查），而堵它的两道保护当时只补在了
/// 三份里的一份上。<c>Probe</c> 和 <c>FadeRun</c> 写的是<b>同一份</b> <c>keeper-probe.log</c>，
/// 两边都照样印别人的窗标题（<c>Win32.ClassName</c> / <c>WindowText</c>）——
/// 也就是同一份文件里，两道保护一份有一份没有。
///
/// 顺带收掉一处小的：三份的 step 栏宽各写各的（App 8、Probe 20、FadeRun 16），
/// 而 Probe 和 FadeRun 写的是同一份文件，于是那一份文件里两种栏宽。统一成 20。
/// <b>自检读的正是这一栏，而 Probe 原来就是 20，所以自检的实测结果一个字不变。</b></summary>
internal static class LogWriter
{
    /// step 那一栏的宽度。取最宽那个，所以谁都不截断。
    private const int StageWidth = 20;

    public static string AppPath => Path.Combine(Tmp.Root, "keeper-app.log");
    public static string ProbePath => Path.Combine(Tmp.Root, "keeper-probe.log");

    /// 不写 BOM：那两份日志和设置文档一样，第一行就是时间戳，BOM 会把读它的人绊一下。
    /// 第二个 false 是「写不出的字符不要抛」—— 但真正堵住那一发的是
    /// <see cref="StripUnpairedSurrogates"/>，编码这一道是第二层，两头都堵。
    private static readonly UTF8Encoding Encoding = new(false, false);

    public static void ToApp(string step, string message) => Write(AppPath, step, message);

    /// 测量程序和遮罩测量程序共用这一份文件：「四组判据」和「四条不许」能在同一处按时间读下来。
    public static void ToProbe(string step, string message) => Write(ProbePath, step, message);

    private static void Write(string path, string step, string message)
    {
        var line = StripUnpairedSurrogates($"{DateTime.Now:HH:mm:ss.fff} {step,-StageWidth} {message}");
        File.AppendAllText(path, line + "\n", Encoding);
        GD.Print(line);
    }

    /// 把落单的代理对换成 '?'。合法的代理对原样留下 —— 那是有人真的往清单里写了一个 emoji。
    private static string StripUnpairedSurrogates(string text)
    {
        if (!text.Any(char.IsSurrogate)) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else sb.Append(char.IsSurrogate(c) ? '?' : c);
        }
        return sb.ToString();
    }

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Logging = new(
        () => "日志那一条路通过：落单的代理对换成 ?、合法的 emoji 原样、没代理对的一行一个字不动",
        "日志那一条路", SelfTest);

    /// <summary>这一组原来住在 <c>Probe.LoggingSelfTest</c> 里，是自检里唯一一组判据不住在被测
    /// 模块旁边的（那张工单里写过「再长一条就该搬去自己交一份」）。现在实施搬进了
    /// <c>LogWriter</c>，判据跟着搬过来，三条断言一个字没改。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        var lone = StripUnpairedSurrogates("标题='K\uDC7Fo'");
        if (lone != "标题='K?o'") problems.Add($"落单的代理对换成了 '{lone}'，该是 '标题=?o' 那一档");
        var pair = StripUnpairedSurrogates("清单 😀 一条");
        if (pair != "清单 😀 一条") problems.Add($"合法的代理对被拆了：'{pair}'");
        var plain = StripUnpairedSurrogates("普通一行");
        if (plain != "普通一行") problems.Add($"没有代理对的一行被改了：'{plain}'");
        return problems;
    }
}
