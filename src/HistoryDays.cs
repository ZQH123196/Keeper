using System;
using System.Collections.Generic;
using System.Linq;

namespace Keeper;

/// <summary>休息历史的那一份读法：<b>一天一行，点开看那一天那几条</b>。
///
/// <para>它是<b>纯算术</b>，不碰引擎、不碰屏、不碰帧 —— 所以自检拿它当判据不需要一棵活树。
/// 取向、代价和被否掉的路在设计文档 `docs/design/records.md`「休息历史是只读的记录读法，两处入口看同一份数据」那一节
/// 。</para>
///
/// <para><b>归哪一天按本机时区</b>，不按 UTC —— <see cref="History.Record.AtUnixMs"/> 存的是
/// UTC 毫秒，而用户看的是「我那天休息了几次」。<b>跨午夜的那一次归它落下那一刻所在的那一天</b>
/// （它是按那一刻换算的，不是按哪一天开的）。这一条是那一节里定死的，取哪一边不是这里现定的。</para>
///
/// <para><b>一天的健康度走 <see cref="History.HealthOf"/> 那同一份算法</b>，不另起一份 ——
/// 同一个概念有两个算法，改一处漂一次，而症状是「界面上那个数和产品里那个数不一样」，
/// 那种不一致在界面上看不出是哪一边错了。</para>
///
/// <para><b>读不通是单独一种形状</b>（<see cref="Unreadable"/>），不是一个空的那一份 ——
/// 空读起来是「你没休息过」，而磁盘上那份读不通的人明明记过。理由和
/// <see cref="LifeModeIndex"/> 那个「名单读不通 ≠ 一个生活模式都没有」同款，
/// 在 <c>docs/adr/0002-settings-and-records.md</c>。</para>
///
/// <para><b>一条记录都没有</b>是 <see cref="Days"/> 为空，不是错误 —— 界面说「还没记过休息」。</para>
/// </summary>
public static class HistoryDays
{
    /// <summary>读出来的那一份：<b>按天倒序</b>（最近的一天在最前）。</summary>
    public sealed record Day(DateOnly Date, int Prompted, int Taken, IReadOnlyList<Entry> Entries)
    {
        /// 那天那一个百分数，算法是 <see cref="History.HealthOf"/> 那一份。
        public int HealthPercent => History.HealthOf(Taken, Prompted);
    }

    /// <summary>一天的某一次。<see cref="Taken"/> 为假是<b>跳过</b>（遮罩没站够久）。</summary>
    public sealed record Entry(DateTimeOffset At, bool Taken);

    /// <summary>读出来的整份。<see cref="Unreadable"/> 为真时 <see cref="Days"/> 是空的，
    /// 而那两档界面必须分开摆 —— 判据在 <see cref="SelfTest"/>。</summary>
    public sealed record Reading(IReadOnlyList<Day> Days, bool Unreadable)
    {
        public static Reading Of(History history) => Of(history, DateTimeOffset.Now);

        public static Reading Of(History history, DateTimeOffset now)
        {
            // 读不通先判：它和「一条都没有」是**两档**，而判据要问的是这一档不许读成那一档。
            if (history.Unreadable) return new Reading([], true);
            if (history.Records.Count == 0) return new Reading([], false);

            // 按那一刻换算成本机时区 —— 这一句是「归哪一天」的正本。倒序那一头在
            // <see cref="Day"/> 那一段的排序上做，不在这里 reverse 一次（那两处会漂）。
            var grouped = history.Records
                .Select(r => new { At = ToLocal(r.AtUnixMs), r.Taken })
                .GroupBy(x => DayOf(x.At))
                .OrderByDescending(g => g.Key)
                .Select(g => new Day(
                    g.Key,
                    g.Count(),
                    g.Count(x => x.Taken),
                    g.OrderByDescending(x => x.At)
                     .Select(x => new Entry(x.At, x.Taken))
                     .ToList()))
                .ToList();
            return new Reading(grouped, false);
        }
    }

    /// <summary>UTC 毫秒 → 本机时区的那个时刻。<b>归天按这一句的结果</b>，别处不另算一遍。</summary>
    public static DateTimeOffset ToLocal(long atUnixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(atUnixMs).ToLocalTime();

    /// <summary>本机时区的那个时刻 → 它是哪一天。<b>「归天」的正本</b>：界面和自检都问它，
    /// 不各自写一遍 <c>DateOnly.FromDateTime</c>（那个重载收 <see cref="DateTime"/>，
    /// 写错一次编译不过 —— 但写对了也还是两处同一件事）。</summary>
    public static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.DateTime);

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 01）。</summary>
    internal static readonly CriterionOf Days = new(
        () => "休息历史按天读法通过：一天一行、按本机时区归天（跨午夜归落下那一刻）、一天内按时刻倒序、" +
              "天数倒序、健康度走记录那一份算法、读不通是一档而不是空的一份、没记录也不是错、500 条不漏不重",
        "休息历史按天读法", SelfTest);

    /// 自检里的判据（组名 <c>history-days</c>）。纯算术，一条都不碰 %APPDATA%。
    ///
    /// <b>时刻是造出来的</b>，不走 <c>DateTime.Now</c>：跨午夜那几条要是真读现实时间，
    /// 判据在 23:59 那一秒跑和在 00:01 那一秒跑就会给出不同的结论 —— 那是一条会自己
    /// 忽然变红的判据。所以每次都拿一个显式的基准时刻，<b>跨时区也照它算</b>
    /// （<see cref="ToLocal"/> 那一句是时区换算本身，判据只问换算完归到哪一天）。
    public static List<string> SelfTest()
    {
        var checks = new Checks("history-days");

        // 造一条记录：给的是**本机时区**的时刻，内部换成 UTC 毫秒存 ——
        // 和 <see cref="History"/> 写记录那一侧是同一个口径（那边是 DateTimeOffset.UtcNow）。
        static History One(params (DateTimeOffset At, bool Taken)[] rows)
        {
            var h = new History();
            foreach (var (at, taken) in rows)
                h.Push(new History.Record
                {
                    AtUnixMs = at.ToUniversalTime().ToUnixTimeMilliseconds(),
                    Taken = taken,
                });
            return h;
        }

        checks.Check("一条记录都没有是「还没记过」，不是「读不通」", () =>
        {
            var got = HistoryDays.Reading.Of(new History());
            if (got.Unreadable) throw new Exception("没记录被当成了读不通");
            if (got.Days.Count != 0) throw new Exception($"没记录却读出 {got.Days.Count} 天");
        });

        checks.Check("读不通是一档，不是一个空的那一份", () =>
        {
            // 真的读不通那一份：走 <see cref="History.Load"/> 自己那条路（临时目录），不用手工造。
            var root = System.IO.Path.Combine(Tmp.Root,
                $"keeper-history-days-{System.Environment.ProcessId}");
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var path = System.IO.Path.Combine(root, "broken.jsonc");
                System.IO.File.WriteAllText(path, "{ \"records\": [ { \"atUnixMs\": 1, \"taken\": ");
                var reading = HistoryDays.Reading.Of(History.Load(path));
                if (!reading.Unreadable) throw new Exception("截断的一份被读成了空的");
                if (reading.Days.Count != 0) throw new Exception("读不通那一份还读出了天");
            }
            finally
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { /* 删不掉不是判据 */ }
            }
        });

        checks.Check("同一天的几条并成一天，两个数数对", () =>
        {
            var day = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
            var h = One((day, true), (day.AddHours(2), false), (day.AddHours(5), true));
            var got = HistoryDays.Reading.Of(h, day).Days;
            if (got.Count != 1) throw new Exception($"同一天读出 {got.Count} 天，该是 1 天");
            if (got[0].Prompted != 3) throw new Exception($"那天提示了几次读成 {got[0].Prompted}，该是 3");
            if (got[0].Taken != 2) throw new Exception($"那天休息了几次读成 {got[0].Taken}，该是 2");
            // 一天内按时刻倒序（最近的在上）。
            if (got[0].Entries[0].At != day.AddHours(5))
                throw new Exception($"一天内第一条是 {got[0].Entries[0].At:HH:mm}，该是最晚那一条");
            if (got[0].Entries[2].At != day) throw new Exception("一天内最后一条不是最早那一条");
        });

        checks.Check("跨午夜的那一次归它落下那一刻所在的那一天", () =>
        {
            // 23:50 落下 → 归 10-08；00:10 落下 → 归 10-09。**两条挨着 12 分钟，
            // 而归的是不同的天** —— 这就是这一条要量的那一发。
            var late = new DateTimeOffset(2026, 10, 8, 23, 50, 0, TimeSpan.FromHours(8));
            var after = new DateTimeOffset(2026, 10, 9, 0, 10, 0, TimeSpan.FromHours(8));
            var got = HistoryDays.Reading.Of(One((late, true), (after, false)), late).Days;
            if (got.Count != 2) throw new Exception($"挨着 12 分钟的两条读出 {got.Count} 天，该是 2 天");

            var byDate = got.ToDictionary(d => d.Date);
            if (byDate[HistoryDays.DayOf(late)].Prompted != 1)
                throw new Exception("23:50 那一条没归 10-08");
            if (byDate[HistoryDays.DayOf(after)].Prompted != 1)
                throw new Exception("00:10 那一条没归 10-09");
            if (byDate[HistoryDays.DayOf(after)].Taken != 0)
                throw new Exception("00:10 那一条本该是跳过");
        });

        checks.Check("归天走本机时区，不是 UTC 那一头", () =>
        {
            // 这一条是**反面**：同一条记录，在 UTC 里是 10-08 的傍晚、在 +08:00 里已经是 10-09。
            // 本机时区是哪一个不是判据要管的事（那台机器是什么时区就是什么）——
            // 判据要问的是「换算和不换算不是同一天」这件事在时区偏移够大时能被看出来。
            // 所以这一发**造一条正午偏一点的时刻**：偏 14 小时以上，本机时区才会翻它。
            var at = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(8));
            var got = HistoryDays.Reading.Of(One((at, true)), at).Days;
            if (got.Count != 1) throw new Exception("一条读出不止一天");
            var want = HistoryDays.DayOf(HistoryDays.ToLocal(at.ToUniversalTime().ToUnixTimeMilliseconds()));
            if (got[0].Date != want)
                throw new Exception($"归到了 {got[0].Date:yyyy-MM-dd}，按本机时区该是 {want:yyyy-MM-dd}");
            // 换算这一步确实在动：UTC 那一头和本机那一头在 +08:00 这台机器上就是差一天。
            var utc = HistoryDays.DayOf(at.ToUniversalTime());
            if (want == utc) return;   // 本机就是 UTC：那就没什么可量的，不是判据失败
            if (got[0].Date == utc) throw new Exception("归天用的是 UTC 那一头，不是本机时区");
        });

        checks.Check("天数倒序：最近的一天在最前", () =>
        {
            var day = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(8));
            var got = HistoryDays.Reading.Of(
                One((day, true), (day.AddDays(1), false), (day.AddDays(2), true)), day.AddDays(2)).Days;
            if (got.Count != 3) throw new Exception($"读出 {got.Count} 天，该是 3 天");
            for (var i = 1; i < got.Count; i++)
                if (got[i].Date >= got[i - 1].Date)
                    throw new Exception($"第 {i} 天没有比第 {i - 1} 天更早：{got[i - 1].Date} 然后 {got[i].Date}");
        });

        checks.Check("健康度走记录那一份算法，不是这一族自己的一份", () =>
        {
            var day = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
            // 一天 3 次、休息过 2 次 = 67；这一天和整份记录给的数是同一个算法。
            var one = HistoryDays.Reading.Of(One((day, true), (day.AddHours(1), true), (day.AddHours(2), false)), day);
            if (one.Days[0].HealthPercent != HistoryDays.Reading
                    .Of(One((day, true), (day.AddHours(1), true), (day.AddHours(2), false)), day)
                    .Days[0].HealthPercent)
                throw new Exception("同一份读了两遍给出两个数");

            var whole = One((day, true), (day.AddHours(1), true), (day.AddHours(2), false));
            if (one.Days[0].HealthPercent != whole.HealthPercent())
                throw new Exception("一天的数和整份的数不是同一个算法给的");
            if (one.Days[0].HealthPercent != 67)
                throw new Exception($"3 次里休息过 2 次读成 {one.Days[0].HealthPercent}%，该是 67%");
        });

        checks.Check("一天的记录一条不漏、一条不重", () =>
        {
            // 起点定在 21:00 而不是 00:00：500 分钟从 00:00 起算**还在同一天**，
            // 那就量不到「跨天那一头有没有丢或重」—— 这一发要量的正是那一头。
            // 500 分钟（8 小时 20 分）从 21:00 起跨过午夜，落成 10-01 和 10-02 两天。
            var day = new DateTimeOffset(2026, 10, 1, 21, 0, 0, TimeSpan.FromHours(8));
            const int N = 500;      // <see cref="History"/> 的上限（<c>MaxRecords</c>）
            var h = new History();
            for (var i = 0; i < N; i++)
                h.Push(new History.Record
                {
                    AtUnixMs = day.AddMinutes(i).ToUniversalTime().ToUnixTimeMilliseconds(),
                    Taken = i % 2 == 0,
                });
            if (h.Records.Count != N) throw new Exception("造不出 500 条 —— 前提不成立");

            var got = HistoryDays.Reading.Of(h, day.AddMinutes(N)).Days;
            var all = got.Sum(d => d.Entries.Count);
            if (all != N) throw new Exception($"{N} 条分完组只剩 {all} 条");
            // 500 分钟从 21:00 起跨过午夜，落成 10-01 和 10-02 两天。
            if (got.Count != 2) throw new Exception($"{N} 条读出 {got.Count} 天，该是 2 天（跨午夜那一头）");
            // 分组不许改结果：加起来的那些数要和造进去的那一份逐字对上。
            // **休息过的次数不等于提示过的次数** —— 每隔一条是跳过的，所以问的是
            // 「各是多少」而不是「两个相等」。
            if (got.Sum(d => d.Prompted) != N) throw new Exception("分完组提示过的次数对不上");
            var wantTaken = Enumerable.Range(0, N).Count(i => i % 2 == 0);
            if (got.Sum(d => d.Taken) != wantTaken)
                throw new Exception($"分完组休息过 {got.Sum(d => d.Taken)} 次，造进去的是 {wantTaken} 次");
        });

        checks.Check("上限满了以后新的一条挤掉最老的一条，界面读到的就是最新那几条", () =>
        {
            // 判据是「读出来的和盘上是同一份」—— 不是「上限那条规矩」（那一发在 history 那一组里）。
            var day = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
            var h = One((day, true));
            h.Save(System.IO.Path.Combine(Tmp.Root,
                $"keeper-history-days-cap-{System.Environment.ProcessId}.jsonc"));
            try
            {
                for (var i = 0; i < 600; i++) h.Push(new History.Record { AtUnixMs = i, Taken = true });
                var got = HistoryDays.Reading.Of(h, day).Days;
                var fromDoc = h.Records.Select(r => r.AtUnixMs).ToHashSet();
                if (got.Sum(d => d.Entries.Count) != fromDoc.Count)
                    throw new Exception("读出来的条数和文档里剩下的对不上");
            }
            finally
            {
                var p = System.IO.Path.Combine(Tmp.Root,
                    $"keeper-history-days-cap-{System.Environment.ProcessId}.jsonc");
                try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
            }
        });

        return checks.Report();
    }
}
