using System;
using System.Collections.Generic;
using System.Globalization;

namespace Keeper;

/// <summary>农历日名、月名和节日。
///
/// 数从 .NET 的 <see cref="ChineseLunisolarCalendar"/> 取，名字是这里的两张表。为什么分开：
/// 那个类上 <c>GetDayName</c> / <c>GetMonthsName</c> 是 <c>protected</c>，从外头够不着
/// （2026-09-26 拿一发临时程序试过，编译就过不去），所以名字只能自己带。数不用自己带 ——
/// 它对得上三条公开的历法事实：2026-09-11 是八月初一、2026-09-24 是八月十四、
/// 2026-09-25 是中秋节（八月十五），BCL 在那三天报回来的是 8-01、8-14、8-15。
///
/// <b>24 节气不做。</b>契约里没有这一格：<c>.scratch/keeper-contract/spec.md</c>「Out of Scope」说的是那一格写农历或节日，
/// 按「别自己发明需求」处置。他要是想要：底不是没有（太阳黄经每 15° 一定气，加一份
/// 《中国天文年历》的日期表就能立起来），但那是一次<b>改契约</b>，不是补一个漏。
/// 宁可少一行字，不要错一行字。
///
/// 界面上「农历」那两个字走语言表（<c>Tr</c>），这里只留名字本身 —— 那是数据。
/// 英文那一侧不写农历日名，写的是「农历」前缀后面跟数字月日，见 <see cref="Short(DateOnly)"/>。</summary>
public static class Lunar
{
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] DayNames =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    ];

    /// 正月和冬月、腊月是常用叫法，不是「一月」「十一月」「十二月」—— 所以只有 1、11、12
    /// 这三格和数字叫法不同。
    private static readonly string[] MonthNames =
        ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];

    /// 一个农历年的第一天在公历上哪天。春节、除夕都从这一发数出来，
    /// 所以不写「2 月第几个星期六」那种猜法。
    public static DateOnly NewYear(int lunarYear)
    {
        var probe = new DateTime(lunarYear - 1, 12, 1);
        while (true)
        {
            if (Calendar.GetYear(probe) == lunarYear && Calendar.GetMonth(probe) == 1 && Calendar.GetDayOfMonth(probe) == 1)
                return new DateOnly(probe.Year, probe.Month, probe.Day);
            probe = probe.AddDays(1);
        }
    }

    /// 除夕：农历年最后一天的第二天就是下一年春节，所以除夕是春节前一天。
    public static DateOnly NewYearsEve(int lunarYear) => NewYear(lunarYear + 1).AddDays(-1);

    /// 固定按公历过的节日。这一张是一份定死的常见节日清单，不是全集：
    /// 全是「公历日期定死」那一型的写法。母亲节、父亲节那种「某月第几个星期日」没进
    /// 这张表 —— 契约没管这一条，算它们还要另一套星期规则。
    private static readonly (int Month, int Day, string Name)[] SolarFestivals =
    [
        (1, 1, "元旦"), (3, 8, "妇女节"), (3, 12, "植树节"), (5, 1, "劳动节"), (5, 4, "青年节"),
        (6, 1, "儿童节"), (7, 1, "建党节"), (8, 1, "建军节"), (9, 10, "教师节"), (10, 1, "国庆节"),
    ];

    /// 按农历月日过的节日。闰月里不过（闰五月的「端午」不存在），所以判的时候闰月位要空。
    private static readonly (int Month, int Day, string Name)[] LunarFestivals =
    [
        (1, 1, "春节"), (1, 15, "元宵节"), (2, 2, "龙抬头"), (5, 5, "端午节"),
        (7, 7, "七夕"), (8, 15, "中秋节"), (9, 9, "重阳节"), (12, 8, "腊八节"),
    ];

    /// 那一天过的节日名，没有就返回空串。一天可能撞上两个（2026-09-25 是中秋，也可能
    /// 某天既是公历节又是农历节），所以按「农历的优先」只报一个：格子只放得下一行。
    public static string Festival(DateOnly day)
    {
        var lunar = LunarDateOf(day);
        if (!lunar.IsLeap)
            foreach (var (month, monthDay, name) in LunarFestivals)
                if (lunar.Month == month && lunar.Day == monthDay) return name;
        if (NewYearsEve(lunar.Year) == day) return "除夕";
        foreach (var (month, monthDay, name) in SolarFestivals)
            if (day.Month == month && day.Day == monthDay) return name;
        return "";
    }

    public readonly record struct LunarDate(int Year, int Month, int Day, bool IsLeap);

    /// 公历那天在农历上落在哪儿。<c>IsLeap</c> 是「这一格在不在闰月里」——
    /// <see cref="ChineseLunisolarCalendar.GetMonth"/> 把闰月算成第 N+1 个月，
    /// 所以要拿 <c>GetLeapMonth</c> 对一下才知道是「七月」还是「闰六月」。
    /// 2025 年那一发实测：闰月位是 7，所以第 7 格本身是闰六月、第 8 格才是七月
    /// （2025-07-25 落在第 7 格 → 闰六月初一）。
    public static LunarDate LunarDateOf(DateOnly day)
    {
        var dt = day.ToDateTime(TimeOnly.MinValue);
        var year = Calendar.GetYear(dt);
        var month = Calendar.GetMonth(dt);
        var leap = Calendar.GetLeapMonth(year);
        var isLeap = leap > 0 && month == leap;
        // 闰月占掉一格，它自己和它后面的每一格都要往回错一格才是月名。
        var named = leap > 0 && month >= leap ? month - 1 : month;
        return new LunarDate(year, named, Calendar.GetDayOfMonth(dt), isLeap);
    }

    /// 格子里那一小行：初一那格写月名（截图里 9-11 那格是「八月」不是「初一」），
    /// 有节日写节日，其余写日名。
    public static string Cell(DateOnly day)
    {
        var festival = Festival(day);
        if (festival.Length > 0) return festival;
        var parts = LunarDateOf(day);
        return parts.Day == 1 ? MonthName(parts) : DayNames[parts.Day - 1];
    }

    public static string MonthName(LunarDate parts) =>
        (parts.IsLeap ? "闰" : "") + MonthNames[parts.Month - 1];

    /// 「农历八月十四」那一整串。英文那一侧的「农历」前缀另有出处，见类型注释。
    public static string Long(DateOnly day)
    {
        var parts = LunarDateOf(day);
        return MonthName(parts) + DayNames[parts.Day - 1];
    }

    /// 抬头那一行的短形：农历月日两个数，英文侧用得上（"8/14"）。
    public static string Short(DateOnly day)
    {
        var parts = LunarDateOf(day);
        return $"{parts.Month}/{parts.Day}";
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf LunarDates = new(
        () => "农历通过：日名月名对得上那几条历法事实，闰月和节日各判对一次",
        "农历", SelfTest);

    /// 自检。底是几条公开的历法事实：2026-09-11 是八月初一、2026-09-24 是八月十四、
    /// 2026-09-25 是中秋节（八月十五），再加两条常识（2026 年春节 2 月 17 日、元宵 3 月 3 日）——
    /// 常识那两条是为了万一 BCL 那侧的表哪天换了实现，这里能先响，而不是等桌面上写错字被人看见。
    public static List<string> SelfTest()
    {
        var problems = new List<string>();

        void Expect(DateOnly day, string cell, string longForm, string festival)
        {
            var gotCell = Cell(day);
            if (gotCell != cell) problems.Add($"{day:yyyy-MM-dd} 格子里该写「{cell}」，写的是「{gotCell}」");
            var gotLong = Long(day);
            if (gotLong != longForm) problems.Add($"{day:yyyy-MM-dd} 农历该是「{longForm}」，读出来「{gotLong}」");
            var gotFestival = Festival(day);
            if (gotFestival != festival)
                problems.Add($"{day:yyyy-MM-dd} 节日该是「{festival}」，读出来「{gotFestival}」");
        }

        // 2026 年 9 月那一组农历对照
        Expect(new DateOnly(2026, 9, 11), "八月", "八月初一", "");          // 农历八月初一 → 格子写月名
        Expect(new DateOnly(2026, 9, 24), "十四", "八月十四", "");           // 抬头那一行的「今天」
        Expect(new DateOnly(2026, 9, 25), "中秋节", "八月十五", "中秋节");   // 农历八月十五
        Expect(new DateOnly(2026, 9, 10), "教师节", "七月廿九", "教师节");
        Expect(new DateOnly(2026, 10, 1), "国庆节", "八月廿一", "国庆节");
        Expect(new DateOnly(2026, 9, 28), "十八", "八月十八", "");
        Expect(new DateOnly(2026, 9, 30), "二十", "八月二十", "");

        // 两条常识，给 BCL 那侧兜底
        if (NewYear(2026) != new DateOnly(2026, 2, 17)) problems.Add($"2026 年春节算成 {NewYear(2026):yyyy-MM-dd}");
        Expect(new DateOnly(2026, 3, 3), "元宵节", "正月十五", "元宵节");
        // 除夕是「过完这一年的那一天」，所以它是下一年春节的前一天：农历 2025 年那一天
        // 落在公历 2026-02-16，而 2026 年那一个月的最后一天在 2027 年。
        if (NewYearsEve(2025) != new DateOnly(2026, 2, 16))
            problems.Add($"农历 2025 年的除夕算成 {NewYearsEve(2025):yyyy-MM-dd}，该是 2026-02-16");
        if (NewYearsEve(2026) != new DateOnly(2027, 2, 5))
            problems.Add($"农历 2026 年的除夕算成 {NewYearsEve(2026):yyyy-MM-dd}，该是 2027-02-05");
        Expect(new DateOnly(2026, 2, 16), "除夕", "腊月廿九", "除夕");

        // 闰月：2025 年闰六月，所以那一年有两个「六月」，第二个得写成闰六月，
        // 而端午只在真五月初五（2025 年是 5 月 31 日），不该在闰五月里再过一个。
        var leap = Calendar.GetLeapMonth(2025);
        if (leap != 7) problems.Add($"2025 年的闰月位读成 {leap}，该是 7（闰六月）");
        var parts = LunarDateOf(new DateOnly(2025, 7, 25));
        if (!parts.IsLeap || MonthName(parts) != "闰六月")
            problems.Add($"2025-07-25 该落在闰六月，读出来 {parts} → {MonthName(parts)}");
        if (Festival(new DateOnly(2025, 5, 31)) != "端午节")
            problems.Add($"2025-05-31 该是端午节，读出来「{Festival(new DateOnly(2025, 5, 31))}」");
        if (Festival(new DateOnly(2025, 7, 25)).Length != 0)
            problems.Add("闰六月初一被当成了节日，格子会凭空多一行");

        // 名字表自己：三十天的大月才有「三十」，二十九天的小月读到 29 为止
        if (DayNames.Length != 30) problems.Add($"日名表 {DayNames.Length} 条，该是 30");
        if (MonthNames.Length != 12) problems.Add($"月名表 {MonthNames.Length} 条，该是 12");
        return problems;
    }
}
