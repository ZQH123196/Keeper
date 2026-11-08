using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>勿扰那一族的<b>纯求值</b>：拿设置和检测的实测结果，算出「此刻勿扰成不成立，因为哪一条」。
///
/// <b>它是业务功能，不是检测的一部分</b>（<c>docs/adr/0006-detection-module-boundary.md</c>
/// 「勿扰是业务功能，不是检测的一部分」那一节）：检测只给实测结果（输入三问、独占全屏、前台应用是谁），
/// 而「时段表 + 名单」是这一族自己的配置 —— 放进模块就是让感知层有一条通向声明表的路。
/// 所以求值住在这里，判定句法住在 <see cref="Scheduler"/> 那一侧，托盘那一半句住在
/// <see cref="Tray"/>（理由和「此刻在哪个块」那一行一样）。
///
/// <b>无状态</b>，和 <see cref="Routine"/> 同一档（它同样是现实时间的纯算术）：一个字段都不写，
/// 「现在几点」由调用方给进来。所以自检里那条判据量的是这件事本身。
///
/// <b>只有两条形状规则，都是从作息表照抄的读法，不是另发明的一套</b>：
/// <list type="bullet">
/// <item><b>止不晚于起 = 跨午夜</b>，判据是 <see cref="Routine.Covers(int,int,int)"/> 那一族 ——
/// 时刻算术只有一份（这一族<b>不写</b>第二份）。</item>
/// <item><b>跨午夜整段挂在开始的那一天</b>：「周一 22:30 → 06:30」盖住的是周一夜到周二晨
/// （设计文档 `docs/design/dnd.md`「勿扰是用户定义的静默规则」那一节）。于是凌晨那一半要问的是<b>前一天</b>的星期几 ——
/// 这一条是这一族和作息表唯一的一处分别（作息表没有星期几），而它住在
/// <see cref="WindowCovers"/> 一处。</item>
/// </list>
///
/// <b>名单匹配复用 <see cref="Foreground.Matches"/></b>，不写第二份：整名精确、大小写不敏感、
/// 子串不认 —— 那几条规矩已经在 <c>foreground</c> 那一组里各判过一次，
/// 这里再写一份就是同一套判据的第二份真相。
///
/// <b>「不成立」和「问不到」分得开</b>：前台那一发问不到时（<see cref="Foreground.Measurement.Known"/>
/// 为 false）<see cref="Listed"/> 答 false，而那<b>不等于「不在名单上」</b> —— 分那两种的是 Known。
///
/// 自检三组里的两组在这里（组名 dnd-arith、dnd-lifemode）；第三组 dnd-silence 在
/// <see cref="Scheduler"/>（它量的是状态机那一侧的守卫，走假时钟）。</summary>
public static class Dnd
{
    /// <summary>星期几那七个词，<b>周一在前</b>。<b>界面上的七个勾选框和求值读的都是这一份</b>
    /// （声明表 <see cref="Settings.WindowDeclarations"/> 里 <c>weekdays</c> 的可选值就是它），
    /// 所以「界面上勾的那一格」和「算的时候认的那一天」不可能对不上。
    ///
    /// 和 <see cref="System.DayOfWeek"/> 的序**故意不一样**（那一族是周日在前）：这一列是给人
    /// 按顺序看的，<see cref="KeyOf"/> / <see cref="DayOf"/> 那一对换算是<b>唯一</b>的接缝。</summary>
    public static readonly string[] WeekdayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// 那一列里第 n 个词（0 = 周一）。界面上那七个勾选框就是照它摆的，**节点名**取的就是这个词
    /// —— 和「摆什么」那一排同一做法（<c>Options.DndDays</c>）。<b>节点名是配置里那一个词</b>，
    /// 摆给用户看的那几个字是另一件事（<see cref="WeekdayText"/>）。
    public static string WeekdayName(int at) => WeekdayKeys[at];

    /// 一个词是星期几；认不出的词<b>交回 false</b> 而不是猜一个 ——
    /// 「一列里混进一个认不出的词」是用户敲错了，猜一个等于让一段时段在别的日子上生效。
    public static bool DayOf(string key, out DayOfWeek day)
    {
        var at = IndexOf(key);
        // WeekdayKeys 是周一起的，DayOfWeek 是周日起的，差一天（周一 = 1，周日 = 0）。
        day = (DayOfWeek)(at < 0 ? 0 : (at + 1) % 7);
        return at >= 0;
    }

    /// 配置里那个词是<b>第几个</b>星期几（0 = 周一），认不出的是 −1。
    public static int IndexOf(string key) => Array.IndexOf(WeekdayKeys, key);

    /// 星期几是哪一个词（<see cref="IndexOf"/> 的逆）。<see cref="DayOfWeek"/> 的取值都在这七个词里。
    public static string KeyOf(DayOfWeek day) => WeekdayKeys[((int)day + 6) % 7];

    /// <summary>界面上那七个勾选框各自摆的那几个字（第 <paramref name="at"/> 个，0 = 周一）。
    ///
    /// <b>词是日历那一族那七条</b>（<see cref="Calendar.WeekdayKey"/>）—— 语言表里本来就有
    /// 周一…周日，而「星期几」是一个概念、一个词：这一族不另起一套 <c>dnd.weekday.mon</c>。
    /// 「一个概念只用一个词」，两个族各列七个键就是同一个概念说了两遍。</summary>
    public static string WeekdayText(int at) => Lang.T(Calendar.WeekdayKey(at, mondayFirst: true));

    /// 第 <paramref name="at"/> 个词（0 = 周一）在<b>语言表里</b>的那一条键。
    /// 界面上那七个勾选框和自检里「那七个词在不在表里」都问它 —— 「一个概念一个词」：
    /// 星期几的名字是语言表里<b>已经有</b>的那七条（周一…周日），这一族不另起一套。
    public static string WeekdayKey(int at) => KeyOf((DayOfWeek)((at + 1) % 7));

    /// <summary>一发给消费方的实测结果：<b>成立不成立，因为哪一条</b>。
    ///
    /// <b>两个「因为」是两种东西</b>：<see cref="Window"/> 是时段表里那一行（界面上要说出
    /// 几点到几点、星期几），<see cref="Exe"/> 是名单里那个 exe 名（用户自己写的一个词，
    /// 原样显示，不翻 —— 设计文档 `docs/design/ui-text.md` 那条界线）。两个都是 null 才是「不成立」。
    ///
    /// 界面上那半句由 <see cref="Tray.DndWhy"/> 拼（语言表 + 数据），所以这一格交出去的是
    /// <b>结构</b>而不是一句话：同一发实测结果在 zh 和 en 下要摆出两句不同的话。</summary>
    public readonly record struct Measurement(bool On, DndWindow? Window, string? Exe)
    {
        /// 不成立那一发就是 default —— 消费方拿 <see cref="On"/> 判，不用再问别的。
        public static Measurement Off => default;
    }

    /// <summary>求值：<b>先时段、后名单</b>，成立的第一个就是交出去的那一条。
    ///
    /// <b>为什么时段排在前面</b>：一条时段是用户排进一天框里的那句话，名单是「我在这个程序里
    /// 不想被打扰」—— 前者是「这段时间」，后者是「这段时间的这个程序」，两个都成立时说前者
    /// 更好懂（托盘那一行印的是「22:30 → 06:30」而不是「chrome.exe」）。这不是政策，
    /// 只是「说哪一条」的那一句取谁，而两个都成立时静默的效果完全一样。</summary>
    public static Measurement Of(Settings settings, DateTime now, Foreground.Measurement foreground) =>
        Of(settings, Routine.MinuteOfDay(now), now.DayOfWeek, foreground);

    /// 求值，<b>时刻和星期几由调用方给</b>（假时钟那一族和测量程序用的就是这一格）：
    /// 「现在几点」是这一族唯一的接缝，判据全在里面。
    public static Measurement Of(Settings settings, int minuteOfDay, DayOfWeek today,
        Foreground.Measurement foreground)
    {
        // 总开关住在设置上，算术不读它（和 Routine.At 不读 routine.enabled 同一处置）——
        // 所以「关掉之后时段和名单都还在」这句话是这句 return 兑现的。
        if (!settings.DndEnabled) return Measurement.Off;
        foreach (var window in settings.DndWindows)
            if (WindowCovers(window, minuteOfDay, today)) return new Measurement(true, window, null);
        if (Listed(settings.DndApps, foreground.Exe)) return new Measurement(true, null, foreground.Exe);
        return Measurement.Off;
    }

    /// <summary>这一刻在不在这一段时段里。<b>跨午夜整段挂在开始的那一天</b>。
    ///
    /// 读法是这样拼起来的：<b>不跨午夜</b>（止晚于起）的那一段，问的是「这一刻的星期几在不在
    /// 那一列里」加 <see cref="Routine.Covers(int,int,int)"/> 那一刻里没有它。
    ///
    /// <b>跨午夜</b>（止不晚于起）的那一段是两块半：头半（22:30 往后）算<b>开始那一天</b>，
    /// 尾半（06:30 之前）仍然算<b>同一次</b>的时段 —— 所以问的是<b>前一天</b>的星期几。
    /// 这就是设计文档 `docs/design/dnd.md`「规则两种成员」那一节那个例子的形状：跨午夜的时段挂在开始那一天，而周二 01:00
    /// 那一刻的星期几是周二、这一段问的却是周一。
    ///
    /// <b>止的那一刻已经出去了</b>（<see cref="Routine.Covers"/> 那一条规矩：止归下一段），
    /// 所以 06:30 整不成立、06:29 成立。</summary>
    public static bool WindowCovers(DndWindow window, int minuteOfDay, DayOfWeek today)
    {
        if (window.EndMinute > window.StartMinute)
            return OnDay(window, today) && Routine.Covers(window.StartMinute, window.EndMinute, minuteOfDay);
        if (minuteOfDay >= window.StartMinute) return OnDay(window, today);
        return minuteOfDay < window.EndMinute && OnDay(window, Yesterday(today));
    }

    /// 这一段在不在这一天。**空的那一列一个都不在** —— 那是一条永远不成立的时段，
    /// 不是「每天」。
    private static bool OnDay(DndWindow window, DayOfWeek day) =>
        window.Weekdays.Contains(KeyOf(day));

    /// 前一天。<see cref="DayOfWeek"/> 的值 0–6，周日（0）的前一天是周六（6）。
    public static DayOfWeek Yesterday(DayOfWeek day) => (DayOfWeek)(((int)day + 6) % 7);

    /// <summary>名单里有没有这个前台应用。<b>判据是 <see cref="Foreground.Matches"/></b>
    /// （整名精确、大小写不敏感、子串不认）—— 这一族<b>不写第二份</b>：那几条规矩在
    /// <c>foreground</c> 那一组里各判过一次，抄一份就是会过期的那一份。
    ///
    /// <b>问不到的那一发答 false，而那不等于「不在名单上」</b>：分那两种的是
    /// <see cref="Foreground.Measurement.Known"/>（这一族是纯函数，拿不到那一格，所以这里只答
    /// 「名单对不对得上」这一个问题）。</summary>
    public static bool Listed(IReadOnlyList<string> apps, string? exe) =>
        apps.Any(entry => Foreground.Matches(entry, exe));

    /// 那一行实测结果（进日志）。<b>句法和 <see cref="Foreground.Describe"/> 同款</b>：
    /// 「不成立」要说出来，不印一个空串；同时把成立的那一条说清（时段几点到几点 / 名单里哪个 exe）。
    /// 界面上那半句是另一回事（要翻语言），见 <see cref="Tray.DndWhy"/>。
    public static string Describe(Measurement measurement) =>
        !measurement.On
            ? "勿扰：不成立（总开关关着，或者时段没盖住、名单里没有它）"
            : measurement.Window is { } window
                ? $"勿扰：成立，因为时段 {Routine.Clock(window.StartMinute)}–{Routine.Clock(window.EndMinute)}" +
                  $"（{Days(window)}）"
                : $"勿扰：成立，因为名单里的 {measurement.Exe}";

    /// 那一段落在星期几（实测结果用）。空的那一列照实说「哪一天都没写」，不编一个词。
    public static string Days(DndWindow window) =>
        window.Weekdays.Count == 0
            ? "哪一天都没写"
            : string.Join(" ", window.Weekdays.Select(DayLabel));

    private static string DayLabel(string key)
    {
        var at = IndexOf(key);
        return at < 0 ? key : WeekdayText(at);
    }

    // ---- 自检：时段算术与名单匹配（组名 dnd-arith）---------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Arithmetic = new(
        () => "勿扰求值通过：星期几勾了哪一天就只在那一天成立、跨午夜整段挂在开始那一天（周一 22:30 → 06:30 " +
              "盖住周一夜到周二晨、止的那一刻已经出去）、起止同一个数是整整一天、空表谁也不成立而关掉总开关也一样、" +
              "名单整名精确、大小写不敏感、子串不认、问不到的那一发不冒充匹配；问过之后那份设置没变（纯函数）",
        "勿扰求值", ArithmeticSelfTest);

    /// <summary>四条判据各判一次，外加「无状态」：<b>星期几边界</b>、<b>跨午夜挂开始那一天</b>、
    /// <b>空表</b>、<b>名单匹配是纯函数</b>。
    ///
    /// 纯算术：造一份设置，问完再问一遍，中间不碰磁盘也不碰真时钟。判据里的时刻都是
    /// <c>时 × 60 + 分</c> 算出来的，<b>不抄 1439</b>（那是 <see cref="Routine.MinutesPerDay"/> 推的）；
    /// 星期几是 <see cref="DateTime"/> 自己的取值，<b>不抄 0–6</b>（换算走
    /// <see cref="DayOf"/> / <see cref="KeyOf"/> 那一对）。
    ///
    /// <b>每一组都带对照</b>：只问「盖住了吗」的判据，一份空表或者一份七个词全勾的表照样全绿。</summary>
    public static List<string> ArithmeticSelfTest()
    {
        var checks = new Checks();
        // 两条时段，**不重叠** —— 边界才判得干净：
        // 夜里 22:30 → 06:30 跨午夜（只挂周一），下午 14:00 → 15:00 当天（只挂周三）。
        var settings = new Settings();
        settings.DndWindows.Add(Window(Routine.At(22, 30), Routine.At(6, 30), "mon"));
        settings.DndWindows.Add(Window(Routine.At(14, 0), Routine.At(15, 0), "wed"));
        var fore = new Foreground.Measurement("chrome.exe", @"C:\x\chrome.exe", null);

        bool On(int hour, int minute, DayOfWeek day) =>
            Dnd.Of(settings, Routine.At(hour, minute), day, fore).On;

        checks.Check("星期几：勾了哪一天就只在那一天成立，周二 23:00 那一档不成立", () =>
        {
            if (!On(23, 0, DayOfWeek.Monday))
                throw new Exception("周一 23:00 不在「周一那条」里 —— 星期几这一列没被读");
            if (On(23, 0, DayOfWeek.Tuesday))
                throw new Exception("周二 23:00 也算 —— 那一列只有周一");
            if (!On(14, 30, DayOfWeek.Wednesday))
                throw new Exception("周三 14:30 不在「周三那条」里");
            if (On(14, 30, DayOfWeek.Monday))
                throw new Exception("周一 14:30 也算 —— 那是周三那条");
            // **对照（一定该通）**：同一刻、同一份表，只把星期几换回去 —— 问「不在那一天」时
            // 不成立，而表里那一条并没有因此消失。
            if (On(23, 0, DayOfWeek.Tuesday) == On(23, 0, DayOfWeek.Monday))
                throw new Exception("换一天问出了同一个答案 —— 星期几这一列压根没被读");
        });

        checks.Check("跨午夜挂开始那一天：周一 22:30 → 06:30 盖住周一夜到周二晨", () =>
        {
            if (!On(22, 30, DayOfWeek.Monday)) throw new Exception("起点自己不成立");
            if (!On(23, 59, DayOfWeek.Monday)) throw new Exception("周一夜里那一段不成立");
            // 关键那一格：00:00 的星期几是**周二**，而这一段问的仍然是周一。
            if (!On(0, 0, DayOfWeek.Tuesday))
                throw new Exception("周二 00:00 不在周一那条里 —— 跨午夜没挂在开始那一天");
            if (!On(6, 29, DayOfWeek.Tuesday)) throw new Exception("周二 06:29 不在周一那条里");
            if (On(6, 30, DayOfWeek.Tuesday))
                throw new Exception("06:30 还算在里面 —— 止的那一刻已经出去了");
            if (On(22, 29, DayOfWeek.Monday))
                throw new Exception("周一 22:29 就提前成立了");
            // **另一半**：跨午夜的**头半**仍然只看开始那一天。周二 22:30 是新的一段，
            // 而这一段没勾周二 —— 不成立。
            if (On(22, 30, DayOfWeek.Tuesday))
                throw new Exception("周二夜里也算 —— 那一列只有周一，所以只有周一夜到周二晨");
            // **对照（一定该通）**：同一条时段、同一刻，只把星期几从周一换成周四，
            // 两头都不成立（头半那一格、尾半那一格都要试）。
            if (On(23, 0, DayOfWeek.Thursday) || On(1, 0, DayOfWeek.Thursday))
                throw new Exception("换成一个没勾的星期几之后仍然成立 —— 判的是别的什么");
        });

        checks.Check("当天之内那一段：起止两端、止的那一刻、以及不晚于起读作跨午夜", () =>
        {
            if (!On(14, 0, DayOfWeek.Wednesday)) throw new Exception("起点自己不成立");
            if (!On(14, 59, DayOfWeek.Wednesday)) throw new Exception("14:59 不在里面");
            if (On(15, 0, DayOfWeek.Wednesday)) throw new Exception("止的那一刻还在里面");
            if (On(13, 59, DayOfWeek.Wednesday)) throw new Exception("提前成立了");
            // 起止同一个数 = 整整一天（和作息表同一读法，判据是 Routine.Covers 那一族），
            // 而**整整一天也还是跨午夜**：它挂在那一天，所以凌晨那一格算**前一天**起的那一次。
            var all = new Settings();
            all.DndWindows.Add(Window(Routine.At(9, 0), Routine.At(9, 0), Dnd.WeekdayKeys));
            if (!Dnd.Of(all, Routine.At(3, 0), DayOfWeek.Friday, fore).On)
                throw new Exception("起止同一个数没有读作整整一天（凌晨那一格）");
            if (!Dnd.Of(all, Routine.At(9, 0), DayOfWeek.Friday, fore).On)
                throw new Exception("起止同一个数，起的那一刻自己不成立");
            if (!Dnd.Of(all, Routine.At(3, 0), DayOfWeek.Saturday, fore).On)
                throw new Exception("整整一天盖不住次日凌晨 —— 跨午夜那一半没跟着走");
            // **「挂在开始那一天」在整整一天上的样子**：只勾了周五的那一条，盖住周五 09:00 往后
            // 和**周六** 09:00 之前，而周五凌晨那一格属于周四起的那一次（没勾，所以不成立）。
            var fri = new Settings();
            fri.DndWindows.Add(Window(Routine.At(9, 0), Routine.At(9, 0), "fri"));
            if (!Dnd.Of(fri, Routine.At(23, 0), DayOfWeek.Friday, fore).On)
                throw new Exception("周五夜里不成立（那是它自己起的那一次）");
            if (!Dnd.Of(fri, Routine.At(3, 0), DayOfWeek.Saturday, fore).On)
                throw new Exception("周六凌晨不成立 —— 跨午夜没挂在开始那一天");
            if (Dnd.Of(fri, Routine.At(3, 0), DayOfWeek.Friday, fore).On)
                throw new Exception("周五凌晨成立 —— 那一格该算周四起的那一次，而周四没勾");
        });

        checks.Check("空表：一条时段都没有、名单也是空的，谁也不成立；而关掉总开关也一样不成立", () =>
        {
            var none = new Settings();
            foreach (var day in EachDay())
                if (Dnd.Of(none, Routine.At(2, 0), day, fore).On)
                    throw new Exception($"空表也求出了成立（{day}）");
            // **对照（一定该通）**：同一份有内容的表，那一刻问得出成立 ——
            // 不然这一条可能只是「问谁都不成立」。
            if (!On(23, 0, DayOfWeek.Monday))
                throw new Exception("对照不成立：同一刻在有内容的那一份上也问不出成立");
            // 名单空着 = 名单不认任何前台应用（问得到、问不到都不认）。
            if (Dnd.Of(none, Routine.At(2, 0), DayOfWeek.Monday,
                    new Foreground.Measurement("chrome.exe", @"C:\x\chrome.exe", null)).On)
                throw new Exception("名单是空的却成立了");
            // 总开关关着：时段和名单都还在表里，而求值答「不成立」。
            var off = new Settings();
            off.DndWindows.Add(Window(Routine.At(0, 0), Routine.At(23, 59), "mon"));
            off.DndApps.Add("chrome.exe");
            off.DndEnabled = false;
            if (Dnd.Of(off, Routine.At(2, 0), DayOfWeek.Monday, fore).On)
                throw new Exception("总开关关着却成立了");
            if (off.DndWindows.Count == 0 || off.DndApps.Count == 0)
                throw new Exception("总开关把时段和名单本身清空了");
            // **对照**：拧开总开关，同一份、同一刻就成立了 —— 量的是开关不是别的什么。
            off.DndEnabled = true;
            if (!Dnd.Of(off, Routine.At(2, 0), DayOfWeek.Monday, fore).On)
                throw new Exception("拧开总开关之后同一刻不成立 —— 对照不成立");
        });

        checks.Check("名单：整名精确、大小写不敏感、子串不认（判据是 Foreground.Matches 那一族）", () =>
        {
            var apps = new List<string> { "chrome.exe", "explorer.exe" };
            var named = (string? exe) => Dnd.Listed(apps, exe);
            if (!named("chrome.exe")) throw new Exception("名单里那个认不出");
            if (!named("CHROME.EXE")) throw new Exception("大小写不敏感这一条不成立");
            if (named("chrome")) throw new Exception("子串也认 —— 那正是要否掉的那一档");
            if (named("notepad.exe")) throw new Exception("不在名单上的也认");
            // **问不到那一发不冒充匹配**（而那不等于「不在名单上」：分那两种的是 Known）。
            if (named(null)) throw new Exception("问不到的那一发认成了名单上那个");
            if (new Foreground.Measurement(null, null, null).Known)
                throw new Exception("问不到的那一发说 Known");
            // **对照（一定该通）**：换成一个在名单上的名字，同一个判据答 true ——
            // 不然上面那些可能只是「什么都不认」。
            if (!named("EXPLORER.EXE")) throw new Exception("对照不成立：名单里第二个名字认不出");
            // 一个空名单对任何名字都答 false。
            if (Dnd.Listed([], "chrome.exe")) throw new Exception("空名单也认");
            // 名单里那一行是空的：不是「什么都不认」，是「那一行什么都不匹配」。
            if (Dnd.Listed([""], "chrome.exe")) throw new Exception("空的名单行也认");
        });

        checks.Check("一个窗也没有、但名单里有那个 exe：成立，而且「因为」是名单不是时段", () =>
        {
            var byApp = new Settings();
            byApp.DndApps.Add("chrome.exe");
            var read = Dnd.Of(byApp, Routine.At(2, 0), DayOfWeek.Monday, fore);
            if (!read.On) throw new Exception("名单里那个没让勿扰成立");
            if (read.Window != null) throw new Exception("「因为」说的是时段，而那一刻一条时段都没有");
            if (read.Exe != fore.Exe) throw new Exception($"「因为」说的是 {read.Exe}，该是 {fore.Exe}");
            // 时段和名单都成立时交出去的是**时段**（那一句话的顺序，理由在 Of 上面）。
            var both = new Settings();
            both.DndWindows.Add(Window(Routine.At(0, 0), Routine.At(23, 59), "mon"));
            both.DndApps.Add("chrome.exe");
            var which = Dnd.Of(both, Routine.At(2, 0), DayOfWeek.Monday, fore);
            if (which.Window == null || which.Exe != null)
                throw new Exception("两个都成立时交出去的不是时段那一条");
            // 一个前台应用都不在名单上：一个都不成立。
            var other = new Foreground.Measurement("notepad.exe", @"C:\x\notepad.exe", null);
            if (Dnd.Of(both, Routine.At(2, 0), DayOfWeek.Monday, other).On &&
                both.DndWindows[0].Weekdays.Count == 0)
                throw new Exception("时段一个都不在却成立了");
        });

        checks.Check("无状态：每问一遍，那份设置一个字节都没变", () =>
        {
            // **一問一比**：一个改表的求值连着问两次会把自己改回去（第一次翻过来、
            // 第二次翻回去），那样「问完没变」看着是对的、其实它写了表。
            foreach (var (what, ask) in new (string, Func<bool>)[]
                     {
                         ("周一 23:00", () => On(23, 0, DayOfWeek.Monday)),
                         ("周三 14:30", () => On(14, 30, DayOfWeek.Wednesday)),
                         ("周二 00:00", () => On(0, 0, DayOfWeek.Tuesday)),
                         ("关掉的那一发", () => Dnd.Of(new Settings(), 0, DayOfWeek.Monday, fore).On),
                     })
            {
                var snapshot = settings.ToDocument().ToJsonString();
                ask();
                var now = settings.ToDocument().ToJsonString();
                if (now != snapshot) checks.Fail($"{what} 动过那张表 —— 求值不是纯的");
            }
        });

        return checks.Report();

        static DndWindow Window(int start, int end, params string[] days) =>
            new() { StartMinute = start, EndMinute = end, Weekdays = [.. days] };

        static IEnumerable<DayOfWeek> EachDay()
        {
            for (int i = 0; i < 7; i++) yield return (DayOfWeek)i;
        }
    }

    // ---- 自检：勿扰这一族进生活模式往返（组名 dnd-lifemode）--------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf LifeMode = new(
        () => "勿扰进生活模式通过：那三个键逐条在生活模式那一份里（键名从常量问）、时段表行里那几个字段和名单" +
              "上那个 exe 名都跟着走、切到另一份那几行跟着换而域外那几条一个字节没动、" +
              "切回来活文档逐字节原来那份、导出与活的那一份逐字节相同，而换一份规则导出必须变",
        "勿扰进生活模式", LifeModeSelfTest);

    /// <summary>存一个带勿扰规则的生活模式、切过去、再导出：<b>逐字节相同</b>。
    ///
    /// <b>不开第三把尺子</b>：存 / 切走的是 <see cref="LifeModeStore"/> 现成那两条路
    /// （<c>SaveAs</c> / <c>SwitchTo</c>），落笔走 <see cref="SettingsStore"/>（原位编辑那一套），
    /// 「逐字节相同」就是两份文件读出来比一遍 —— 同一把尺子，作息表那一族已经这么量过一次。
    ///
    /// 为什么这一族非要自己一组：设计文档 `docs/design/life-mode.md`「生活模式装的是业务功能，不是这台机器的属性」那条把
    /// 界线交给<b>域本身</b>，而「勿扰这一族在生活模式里面」是从声明表能推出来的一个事实；
    /// 它一旦不成立（有人把这一族标成系统域），症状是「预设生活模式带不走自己的静默规则」——
    /// 界面上没有一句话会说这件事。键名从 <see cref="Settings"/> 那三个常量问出来，不抄清单。</summary>

    public static List<string> LifeModeSelfTest()
    {
        var checks = new Checks("dnd-lifemode");
        var dir = checks.Root!;

        checks.Check("存一个带勿扰规则的生活模式 → 切过去 → 切回来 → 再导出，逐字节相同", () =>
        {
            // 第一份：时段表、名单、总开关三样都和别的地方不一样 —— 哪一处和活的那份一样，
            // 那一条判据就空过了。
            var first = new Settings();
            first.DndWindows.Add(new DndWindow
            {
                StartMinute = Routine.At(22, 30), EndMinute = Routine.At(6, 30),
                Weekdays = ["mon", "tue", "wed", "thu", "fri"],
            });
            first.DndWindows.Add(new DndWindow
            {
                StartMinute = Routine.At(14, 0), EndMinute = Routine.At(15, 0),
                Weekdays = ["wed"],
            });
            first.DndApps.Add("chrome.exe");
            first.DndEnabled = true;
            first.ActivityTimeoutMin = 33;
            var second = first.Clone();
            second.DndWindows[0].StartMinute = Routine.At(7, 0);
            second.DndWindows[0].Weekdays = ["sat", "sun"];
            second.DndApps = ["explorer.exe"];
            second.DndEnabled = false;
            second.ActivityTimeoutMin = 44;

            var state = SettingsStore.Save(dir, new SettingsStore.Read { Current = first });
            if (state.Error != null) throw new Exception($"底稿没存成：{state.Error}");
            var live = File.ReadAllText(SettingsStore.FilePath(dir), Utf8);
            if (LifeModeStore.SaveAs(dir, "夜里的", first).Error != null) throw new Exception("第一个生活模式没存成");
            if (LifeModeStore.SaveAs(dir, "白天的", second).Error != null) throw new Exception("第二个生活模式没存成");

            var index = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            var modePath = LifeModeStore.PathOf(dir, index.Find("夜里的")!);
            var modeText = File.ReadAllText(modePath, Utf8);
            var modeValues = Values(modePath);
            if (modeValues == null) throw new Exception($"{modePath} 读不通");

            // **对照（一定该被动到）**：勿扰那三个键都在生活模式那一份里，键名从
            // <see cref="Settings"/> 那三个常量问出来（不手抄一份清单）。
            foreach (var key in DndKeys())
            {
                if (SettingMap.Find(modeValues, key) == null)
                    throw new Exception($"生活模式那一份里没有 {key} —— 这一族不在生活模式里面");
                // 文档里点分键是**嵌套**的（`"dnd": { "enabled": … }`），所以到原文里找的是
                // **最后那一段** —— 词还是从键名自己长出来的。
                if (LineOf(modeText, key.Split('.').Last()).Length == 0)
                    throw new Exception($"生活模式那一份里找不到 {key} 那一行，这一发量的不是那件事");
            }
            // 时段表**行里**那几个字段不是点分键（一条路径指不进某一行），所以逐行问。
            foreach (var field in Settings.WindowDeclarations.Select(d => d.Key))
            {
                if (LineOf(modeText, field).Length == 0)
                    throw new Exception($"生活模式那一份里找不到 {DndWindow.KeyPrefix}{field} 那一行");
            }
            if (!modeText.Contains("chrome.exe", StringComparison.Ordinal))
                throw new Exception("生活模式那一份里没有名单上那个 exe —— 那一列没跟着走");
            if (!modeText.Contains(Settings.DndWindowsKey.Split('.').Last(), StringComparison.Ordinal))
                throw new Exception("生活模式那一份里没有那张时段表");

            // 切到「白天的」：活文档整个换掉，换掉的那份落在 .bak 里。
            // 切到「白天的」之前先把活的那一份读一遍：**域外那几条「没动」量的是切换前后**，
            // 而切换之后再去读就只剩一份了（量不出「没动」，只量得出「现在是什么」）。
            var before = SettingsStore.Load(dir);
            var (err, after) = LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "白天的");
            if (err != null) throw new Exception($"切不过去：{err}");
            if (!File.Exists(SettingsStore.BackupPath(dir))) throw new Exception("切换没留下 .bak");
            if (File.ReadAllText(SettingsStore.BackupPath(dir), Utf8) != live)
                Fail("留下的那份不是切换之前那份活文档", live,
                                      File.ReadAllText(SettingsStore.BackupPath(dir), Utf8));
            var liveAfter = File.ReadAllText(SettingsStore.FilePath(dir), Utf8);
            if (Section(liveAfter, "dnd") == Section(live, "dnd"))
                Fail("切过去之后勿扰那几行一个字节都没变 —— 那一族没跟着换",
                                      Section(live, "dnd"), Section(liveAfter, "dnd"));

            // 切过去之后**活的那一份**里，三个键都成了它那份值（不是「文件变了、内存里没变」）。
            var after_ = after!.Current;
            if (after_.DndEnabled)
                throw new Exception("切过去之后总开关还开着，而那一份里它关着");
            if (after_.DndWindows.Count != first.DndWindows.Count)
                throw new Exception($"切过去之后时段表有 {after_.DndWindows.Count} 条，该是 {first.DndWindows.Count}");
            if (after_.DndWindows[0].StartMinute != Routine.At(7, 0) ||
                after_.DndWindows[0].Weekdays.Count != 2)
                throw new Exception($"切过去之后第一条时段是 {Routine.Clock(after_.DndWindows[0].StartMinute)}" +
                                    $" / {after_.DndWindows[0].Weekdays.Count} 个星期几");
            if (after_.DndApps.Count != 1 || after_.DndApps[0] != "explorer.exe")
                throw new Exception($"切过去之后名单上是 [{string.Join(" / ", after_.DndApps)}]");

            // **域外那几条一个字节不动**（这一族是「休息提醒」那一域，而系统那几条是这台机器的属性）。
            // 比的是**活的那一份**（<c>Load</c> 现读一遍，不拿内存里那一份）——
            // 「写出去的那份对」和「内存里那份没被动」是两件事。
            var afterState = SettingsStore.Load(dir);
            foreach (var key in OutOfDomainKeys())
                if (afterState.Current.Map.Get(key)?.ToString() != before.Current.Map.Get(key)?.ToString())
                    throw new Exception($"切过去之后域外的 {key} 动了");
            // 对照：域内**别的**一条（不在勿扰这一族里的）确实跟着换了 ——
            // 不然上面那些「域外没动」可能只是「什么都没换」。
            if (after_.ActivityTimeoutMin == before.Current.ActivityTimeoutMin)
                throw new Exception("切过去之后域内别的键也没换 —— 「域外没动」量的是「什么都没换」");

            // 切回「夜里的」：**活文档逐字节回到切之前那份**。承重处：那几个键是原位编辑回去的
            // （只有值变了），所以「回来」是字节相同，而不是「值一样」。
            var (backErr, back) = LifeModeStore.SwitchTo(dir, afterState, "夜里的");
            if (backErr != null) throw new Exception($"切不回来：{backErr}");
            var liveBack = File.ReadAllText(SettingsStore.FilePath(dir), Utf8);
            if (liveBack != live)
                Fail("切回来之后活文档不是逐字节原来那份", live, liveBack);

            // 导出：拿活的那份导出一份出来，**一个字节都不该变**。
            var exported = Path.Combine(dir, "导出.jsonc");
            if (SettingsStore.Export(dir, SettingsStore.Load(dir), exported) != null)
                throw new Exception("导出不回来");
            if (File.ReadAllText(exported, Utf8) != live)
                Fail("导出的那一份和活的那一份不是逐字节相同",
                                      live, File.ReadAllText(exported, Utf8));

            // **对照（一定该通过）**：换一份勿扰规则，导出**必须**变。
            var other = back!.Current.Clone();
            other.DndApps.Add("notepad.exe");
            if (SettingsStore.Export(dir, new SettingsStore.Read { Current = other }, exported) != null)
                throw new Exception("换了一份勿扰规则之后导出不回来");
            if (File.ReadAllText(exported, Utf8) == live)
                Fail("换了一份勿扰规则，导出的那一份却一个字节都没变",
                                      live, File.ReadAllText(exported, Utf8));
        });

        return checks.Report();
    }

    // ---- 共用的小工具 ---------------------------------------------------------

    private static readonly UTF8Encoding Utf8 = new(false);

    /// 这一族的三个键（键形和分域定在契约那一节）。判据和界面上那两族都从这一处问键名，
    /// 不手抄一份清单（<c>CODING_STANDARDS.md</c>「写进文档的东西」那一节：数字和条数都不许抄）。
    public static IEnumerable<string> DndKeys()
    {
        yield return Settings.DndEnabledKey;
        yield return Settings.DndWindowsKey;
        yield return Settings.DndAppsKey;
    }

    /// 域外那几条（系统那域）—— 同样从声明表问（作息那一族的自检是同一处做法）。
    private static IEnumerable<string> OutOfDomainKeys() =>
        Settings.Declarations
            .Where(d => !d.IsContainer && d.Domain == Settings.SystemDomain)
            .Select(d => d.Key);

    private static JsonObject? Values(string path) =>
        JsoncDoc.TryParse(File.ReadAllText(path, Utf8), out var doc, out _) ? doc!.Values : null;

    /// 文档里写着那个键的那<b>一整行原文</b>。词是从键名自己长出来的，不是手抄的一句搜索词。
    private static string LineOf(string text, string key)
    {
        var needle = "\"" + key + "\"";
        foreach (var line in text.Split('\n'))
            if (line.Contains(needle, StringComparison.Ordinal)) return line;
        return "";
    }

    /// 从 <c>"key": {</c> 那一行到它自己那个闭合花括号为止的那一段原文（同作息那一族的尺子）。
    private static string Section(string text, string key)
    {
        var lines = text.Split('\n');
        var head = "\"" + key + "\":";
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(head, StringComparison.Ordinal)) continue;
            var pad = new string(' ', lines[i].TakeWhile(char.IsWhiteSpace).Count());
            var collected = new List<string> { lines[i] };
            for (int j = i + 1; j < lines.Length; j++)
            {
                collected.Add(lines[j]);
                if (lines[j].TrimEnd() == pad + "}") break;
            }
            return string.Join('\n', collected);
        }
        return "";
    }

    /// 两段不一样时把**差在哪儿**带出来：这一组比的是字节，光说「不一样」看不出是什么变了
    /// （和 <see cref="Routine"/> 那一族同一把尺子）。
    private static void Fail(string what, string want, string got) =>
        throw new Exception(SectionDiff(what, want, got));

    private static string SectionDiff(string what, string want, string got)
    {
        var a = want.Split('\n');
        var b = got.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : "（没有这一行）";
            var right = i < b.Length ? b[i] : "（没有这一行）";
            if (left == right) continue;
            var at = 0;
            while (at < left.Length && at < right.Length && left[at] == right[at]) at++;
            var from = Math.Max(0, at - 30);
            var to = at + 60;
            return left.Length <= 120 && right.Length <= 120
                ? $"{what}：第 {i + 1} 行 '{left.Trim()}' → '{right.Trim()}'"
                : $"{what}：第 {i + 1} 行第 {at} 个字符上开始不一样 —— '…{left[from..Math.Min(left.Length, to)].Trim()}'"
                  + $" → '…{right[from..Math.Min(right.Length, to)].Trim()}'";
        }
        return what;
    }
}
