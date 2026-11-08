using System;
using System.Collections.Generic;
using System.IO;

namespace Keeper;

/// <summary>「前台是哪个应用」那一件（工单 02）。只回答「现在占着前台的是谁」，不回答
/// 「现在能不能打扰」—— 后者是消费方的事（契约 <c>.scratch/keeper-contract/spec.md</c>
/// 「检测那一族」那一节；取向在 <c>docs/adr/0006-detection-module-boundary.md</c>）。
///
/// 取数就是工单 01 在这台机器上量过的那条链：<c>GetForegroundWindow</c> →
/// <c>GetWindowThreadProcessId</c> → <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> →
/// <c>QueryFullProcessImageNameW</c>（那几发在 <c>src/Win32.cs</c> 里，包装是
/// <c>Win32.ExePathOf</c>；**不重写一遍**）。<b>取数搭 App 那一侧 0.2 秒那一发的轮询底盘</b>：
/// 不发事件、不开线程（ADR-0006「前台取数搭现有轮询底盘」那一节）。
///
/// <b>认人按可执行文件名</b>：跨安装位置稳、人读得懂；完整路径只进实测结果供核对。全路径当匹配键
/// 和窗口类名当匹配键都被否掉了，同名冲突（<c>chrome.exe</c> 全家一个名）认下来 —— 理由同在
/// ADR-0006「应用认人用 exe 名」那一节。
///
/// <b>「问不到」就是问不到</b>：没有前台窗、别的用户 / 系统进程那一档（<c>OpenProcess</c>
/// 回 ACCESS_DENIED）、以及 UWP 那一扇窗一被最小化两路都读不到东西 —— 三种都交回空的一发，
/// <b>不拿兄弟那一格顶替，也不编一个值</b>。消费方要分「问不到」和「不在名单上」，靠的是
/// <see cref="Measurement.Known"/>，不是 <see cref="Matches"/> 给的 false。
///
/// <b>这一件里没有 <c>SHQueryUserNotificationState</c> 那一族</b>，而且是有意的：无边框全屏那一档
/// 几何判据和它给的是两个答案，独立的取材（工单 01 的取向）是<b>几何为主、QUNS 只当旁证</b>，
/// 旁证留在测量程序（<c>src/ForegroundProbe.cs</c>）里并排印，不进产品路径、不进 Scheduler、不进自检。</summary>
public static class Foreground
{
    /// <summary>一发实测结果：此刻占着前台的是谁。<b>三个格子空 = 问不到</b>，不是「没有前台应用」。
    ///
    /// <paramref name="Exe"/> 是匹配键（可执行文件名），<paramref name="Path"/> 是完整路径
    /// （印进日志供核对，<b>不当匹配键</b>），<paramref name="Aumid"/> 是 UWP 的第二身份：
    /// 它和 exe 名**并列**交出去，不是一个顶替另一个 —— 工单 01 量到 AUMID 在顶层那扇窗上
    /// 读得出来、在本体窗上读不出来，所以这一格问的始终是顶层那扇窗。</summary>
    public readonly record struct Measurement(string? Exe, string? Path, string? Aumid)
    {
        /// 认得出它是谁。<see cref="Matches"/> 给 false 的两种原因（不在名单上 / 问不到）靠它分开。
        public bool Known => Exe != null;
    }

    /// <summary>此刻占着前台的是谁。一发四五个 P/Invoke，所以搭 0.2 秒那一发，不问第二次「多久问一次」。
    ///
    /// <b>UWP 那一档先换窗再问身份</b>：顶层那扇窗的 pid 属于 <c>ApplicationFrameHost.exe</c>，
    /// 「顶层 pid → exe 路径」在那里**安静地给错答案**（不是问不出，是问出了别人的名字）——
    /// 一个「查不到就跳过」的降级逻辑在这里压根不触发。见 <see cref="UwpBodyWindow"/>。</summary>
    public static Measurement Now()
    {
        var top = Win32.GetForegroundWindow();
        if (top == IntPtr.Zero) return default;

        var body = top;
        string? aumid = null;
        if (IsUwpFrame(Win32.ClassName(top)))
        {
            body = UwpBodyWindow(top);
            // 找不到就是问不到。不拿顶层那扇的 exe 顶替：那正是它给错答案的那一格，
            // 而「一个『ApplicationFrameHost』」读出来会像一条真的前台应用实测结果。
            if (body == IntPtr.Zero) return default;
            var id = Win32.WindowAumid(top).Aumid;
            aumid = id.Length > 0 ? id : null;
        }

        Win32.GetWindowThreadProcessId(body, out var pid);
        var (opened, _, path) = Win32.ExePathOf(pid);
        // 打开就被拒那一档也走这一格：实测结果里 lsass / winlogon / wininit / services 四个一律
        // ACCESS_DENIED（survey 第 1.1 节）。测量程序那一侧还留着「退到 pid、再退到窗口标题」的两级
        // 降级实测结果，而产品这一件要的是一个可执行文件名，半档实测结果当不得名字用。
        if (!opened || path.Length == 0) return default;
        return new Measurement(Path.GetFileName(path), path, aumid);
    }

    /// <summary>在框架窗里找应用本体窗：<b>pid 不同于框架进程</b>的那个子窗。
    ///
    /// 枚举**不中途停**：那一发实测有四个子窗，停在第一个会把「第一个子窗」当成规则。
    /// 本体窗（<see cref="IsAppBody"/>）优先认；不是它也照样记下来 —— 「pid 不同于框架进程」
    /// 才是量到的那条判据，类名只让它排前面。
    ///
    /// <b>为什么走枚举、焦点窗那条不接</b>（工单 01 的定案）：本机两条给出<b>完全同一个句柄</b>，
    /// 所以选它不是因为「更准」，而是因为<b>它不退化</b> —— 那一扇窗一被最小化，两条路
    /// <b>都</b>读不到东西。既然最坏情况一样，就选不额外依赖 <c>GetGUIThreadInfo</c> 语义的那一条。
    /// 这是有条件的定案：只在一个应用、一个瞬间上量过，所以上面那一格写的是「问不到就是问不到」。</summary>
    private static IntPtr UwpBodyWindow(IntPtr frame)
    {
        Win32.GetWindowThreadProcessId(frame, out var framePid);
        var found = IntPtr.Zero;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out var pid);
            if (pid == 0 || pid == framePid) return true;   // 框架自己那些子窗
            if (found == IntPtr.Zero || IsAppBody(Win32.ClassName(child))) found = child;
            return true;   // 枚举完整棵子树
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>前台那一扇是不是 <c>ApplicationFrameWindow</c>：是的话，前台是一个被
    /// <c>ApplicationFrameHost</c> 托管的商店应用，这一问要往它的子窗里找本体窗。</summary>
    public static bool IsUwpFrame(string className) => className == UwpFrameClass;

    /// <summary>UWP 应用本体窗那一格：<c>Windows.UI.Core.CoreWindow</c>。
    ///
    /// <b>同一个类名，<see cref="Fullscreen"/> 那张 shell 名单里要排除、这里要采信</b>：锁屏那一档
    /// 的前台窗<b>就是</b>它，而几何判据在锁屏期间给 False 是靠「排除」答对的；UWP 场景下它
    /// 是应用本体，这里要「采信」。两个语义各问各的判据，<b>不是把一张名单改一改</b>
    /// （工单 01 的第三条取向）。所以 <c>src/detection/Fullscreen.cs</c> 那张名单一个字都没动。</summary>
    public static bool IsAppBody(string className) => className == UwpBodyClass;

    private const string UwpFrameClass = "ApplicationFrameWindow";
    private const string UwpBodyClass = "Windows.UI.Core.CoreWindow";

    /// <summary>名单那一行和实测结果这一格对不对得上：<b>整名精确匹配、大小写不敏感</b>。
    ///
    /// <b>子串不认</b>：Stretchly 官方文档自己列了子串的误伤坑（survey 第 1 节），不抄。
    /// 全路径当匹配键也不认（ADR-0006「应用认人用 exe 名」那一节）—— 它只进实测结果供核对。
    ///
    /// 这是个<b>纯函数</b>（不碰 Win32、不读配置），所以自检里新加的那一组判的就是它。
    /// 实测结果是问不到那一档时给 false，而那<b>不等于「不在名单上」</b>：分那两种的是
    /// <see cref="Measurement.Known"/>。</summary>
    public static bool Matches(string? entry, string? exe) =>
        entry != null && exe != null && string.Equals(entry, exe, StringComparison.OrdinalIgnoreCase);

    /// <summary>那一行实测结果：和 <see cref="Fullscreen.Measurement"/> 同款句法，把「问不到」说清楚，
    /// 而不是印一个空字符串 —— 那读起来像「前台没有应用」。
    ///
    /// <b>它是日志实测结果，措辞只在中文那一栏有</b>，和 <see cref="Fullscreen.Measurement"/>、以及
    /// 日志里其余各行同一档。界面上要同一句话的那一处（设置窗勿扰那一格）<b>不走这里</b>，它从语言表
    /// 取 Keeper 自己的措辞、把 exe 名和完整路径当数据原样拼 —— 设计文档 `docs/design/ui-text.md` 那一节。
    /// 所以要往界面上放东西，先问那一句是不是 Keeper 自己的措辞。</summary>
    public static string Describe(Measurement measurement) =>
        !measurement.Known
            ? "前台应用：问不到（没有前台窗、打开被拒、或那一扇窗读不出身份）"
            : $"前台应用={measurement.Exe} 路径={measurement.Path}" +
              $"{(measurement.Aumid == null ? "" : $" AUMID={measurement.Aumid}")}";

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Match = new(
        () => "前台应用认人通过：整名精确认得出、大小写不敏感（两个方向各判一次）、子串不认（名单少后缀、" +
              "实测结果多后缀、另一个进程的开头）、名单写全路径不认、别的应用不认；问不到的那一发不冒充匹配，" +
              "而「问不到」和「不在名单上」靠 Known 分得开",
        "前台应用认人", SelfTest);

    /// <summary>匹配那一族的自检：<b>纯算术</b>，不碰 Win32、不碰磁盘、更不碰 %APPDATA%。
    /// 量的是匹配键那几条规矩（整名精确、大小写不敏感、子串不认、全路径不当键），
    /// 外加「问得到 / 问不到」这一发分得开。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        void Expect(string what, bool got, bool want)
        {
            if (got != want) problems.Add($"前台应用认人「{what}」读到了 {got}，该是 {want}");
        }

        // 1. 整名精确 —— 同一个可执行文件名认得出。
        Expect("整名", Matches("chrome.exe", "chrome.exe"), true);
        // 2. 大小写不敏感：名单是人手写的，大小写不该决定认不认得出（两个方向各判一次）。
        Expect("名单那一行大写", Matches("CHROME.EXE", "chrome.exe"), true);
        Expect("读回来的那个大写", Matches("chrome.exe", "Chrome.exe"), true);
        // 3. 子串不认：少一个后缀、多一个后缀、家族名 —— 三条都是 Stretchly 文档自述的那类误伤。
        Expect("子串：名单少后缀", Matches("chrome", "chrome.exe"), false);
        Expect("子串：读回来的那个多后缀", Matches("chrome.exe", "chrome.exe.old"), false);
        Expect("子串：另一个进程的开头", Matches("chrome", "GoogleChrome.exe"), false);
        // 4. 全路径只进实测结果，不当匹配键：挪一次安装、升一次版就让名单全体失效。
        Expect("名单写全路径", Matches(@"C:\Program Files\Google\Chrome\chrome.exe", "chrome.exe"), false);
        // 5. 别的应用不认。
        Expect("另一个应用", Matches("chrome.exe", "CalculatorApp.exe"), false);
        // 6. 问不到不算匹配；而「问不到」和「不在名单上」分得开，靠的是 Known，不是这个 false。
        Expect("实测结果问不到", Matches("chrome.exe", null), false);
        Expect("名单那一行是空的", Matches("", "chrome.exe"), false);
        Expect("问不到那一发 Known 是 false", new Measurement(null, null, null).Known, false);
        Expect("问得到那一发 Known 是 true", new Measurement("chrome.exe", @"C:\x\chrome.exe", null).Known, true);

        return problems;
    }
}
