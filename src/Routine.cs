using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>作息表那一族的<b>纯算术</b>：现在在哪个时间块、下一个到点还有多久。
///
/// <b>无状态</b>，这一点是设计文档写下来的（`docs/design/daily-routine.md`「作息表是一张按现实时间走的表，时间块是表里的行」那一节：「现在在哪个块」用现实时间做纯算术推出 ——
/// <b>无状态，不新增记录</b>」）。所以这里每一个方法都只读传进来的那份表，一个字段都不写；
/// 自检里那条判据量的是这件事本身（问一遍前后，那份文档的字节一样）。
///
/// <b>唯一一条形状规则</b>：<b>止不晚于起 = 跨午夜</b>。22:30 → 06:30 是一夜的一块，不两块
/// （同上）。所以「止 == 起」也读作跨午夜：那是整整一天 —— 契约那句话只有这一条读法，
/// 而「一块有多长」是时钟的事，不是这张表的事。
///
/// <b>重叠时靠前的那一行赢</b>。契约没有说重叠怎么办，而程序必须有一个确定的答案：
/// 文档里靠前的那一行（用户自己排的顺序）。两处判据按这一条走，界面上和首启文档里也是这么说的。
///
/// 自检里三组：<see cref="SelfTest"/>（块表解析与夹取 + 首启文档）、
/// <see cref="ArithmeticSelfTest"/>（跨午夜的当前块与到点推算）、
/// <see cref="LifeModeSelfTest"/>（作息这一族进生活模式往返）。</summary>
public static class Routine
{
    /// 一小时有多少分钟。<b>整个仓库只有这一个 60</b>：<see cref="MinutesPerDay"/> 由它推，
    /// <see cref="Clock"/> / <see cref="HourOf"/> / <see cref="MinuteOf"/> 读它，界面上
    /// 「时刻」那一格的换算也是它。写在别处就是第二份，而 60 恰好是那种抄错也不报错的数。
    public const int MinutesPerHour = 60;

    /// 一天有多少分钟。声明表里那两个时刻键的范围就是从它推的
    /// （<c>Settings.BlockDeclarations</c>），而 1439 这个数不许在别处再抄一遍。
    public const int MinutesPerDay = 24 * MinutesPerHour;

    /// 现实时间落在这一天的第几分钟（0 是零点整，1439 是 23:59）。
    /// 这是「按现实时间走」那一族和这一族唯一的接缝：<b>调用方给时刻，这里只做算术</b>。
    public static int MinuteOfDay(DateTime now) => now.Hour * MinutesPerHour + now.Minute;

    /// 那一刻摆成 <c>HH:MM</c>。给实测结果用（自检里的话、日志、托盘那一行），不参与判定。
    public static string Clock(int minuteOfDay) =>
        $"{HourOf(minuteOfDay):00}:{MinuteOf(minuteOfDay):00}";

    /// 那一刻是几点（0 是午夜，23 是夜里十一点）。<b>界面上「从几点开始」那一格</b>读它 ——
    /// 界面上摆的是时刻，不是一天里的第几分钟（那是个内部表示，摆出来要人做除法）。
    public static int HourOf(int minuteOfDay) => minuteOfDay / MinutesPerHour;

    /// 那一刻是这一小时里的第几分钟（0–59）。界面上那一格旁边的单位词就是「分钟」。
    public static int MinuteOf(int minuteOfDay) => minuteOfDay % MinutesPerHour;

    /// 界面上的「几点几分」合成一天里的第几分钟。<b><see cref="HourOf"/> / <see cref="MinuteOf"/>
    /// 的逆</b>：一颗数格拧一下就走这里，所以存着的那个数和界面上那两个字永远对得上。
    /// 越界的（负的、超过一天的）原样交回去 —— 夹取是声明表那一步的事（<see cref="TimeBlock.Set"/>），
    /// 这里不替它做判断，免得两处各有一套。
    public static int At(int hour, int minute) => hour * MinutesPerHour + minute;

    /// <summary>这一块盖住那一刻吗。<b>止的那一刻已经归下一块</b>：所以「这一块到点了」那一
    /// 刻，问「现在在不在这一块里」的答案是<b>不在</b>，而 <see cref="NextFire"/> 在同一刻答
    /// <c>0</c> 分钟（到点就在这一刻）。两条不打架：换块是一次交接。</summary>
    public static bool Covers(TimeBlock block, int minuteOfDay) =>
        Covers(block.StartMinute, block.EndMinute, minuteOfDay);

    /// <summary><b>那一条形状规则本身</b>，不带块：<b>止不晚于起 = 跨午夜</b>。
    ///
    /// 带块的那一格是它的一层皮，因为<b>时刻算术只有这一份</b> —— 勿扰那条时段
    /// （<see cref="Dnd.WindowCovers"/>）走的是同一个判据，而它不认块、只认两个时刻。
    /// 两边各写一遍，跨午夜那一档迟早在一边被改掉。</summary>
    public static bool Covers(int startMinute, int endMinute, int minuteOfDay) =>
        endMinute > startMinute
            ? minuteOfDay >= startMinute && minuteOfDay < endMinute
            : minuteOfDay >= startMinute || minuteOfDay < endMinute;

    /// <summary>现在在哪个时间块；不在任何一块里就是 <c>null</c>（托盘那一行不出现、
    /// 遮罩上那个位置空着）。重叠时靠前的那一行赢。
    ///
    /// <b>不看总开关</b>：这一族是「这张表算出来是什么」，而「整张表要不要用」是调用方读
    /// <see cref="Settings.RoutineEnabled"/> 的事（关掉它，块的行为全部回到现状）。
    /// 分成两半是因为算术必须<b>无状态</b>，而开关住在设置上。</summary>
    public static TimeBlock? At(IReadOnlyList<TimeBlock> blocks, int minuteOfDay) =>
        blocks.FirstOrDefault(block => Covers(block, minuteOfDay));

    /// <summary>下一个到点：<b>哪一个时间块</b>、还有<b>多少分钟</b>。正好撞在某一刻上就是
    /// <c>0</c>（到点就在这一刻）；<b>错过的到点不补</b>，而这一族没有状态可留 —— 它只认
    /// 这一刻，不认上一次问过什么。
    /// 走一圈的算术在 <see cref="Until"/>。</summary>
    public static Fire? NextFire(IReadOnlyList<TimeBlock> blocks, int minuteOfDay)
    {
        Fire? soonest = null;
        foreach (var block in blocks)
        {
            var fire = new Fire(block, Until(block.StartMinute, minuteOfDay));
            if (soonest == null || fire.InMinutes < soonest.InMinutes) soonest = fire;
        }
        return soonest;
    }

    /// 从 <paramref name="fromMinute"/> 到 <paramref name="startMinute"/> 还有多少分钟，
    /// 走一圈的算法（所以 23:59 → 00:30 是 31 而不是 −1429）。撞在同一刻上是 <c>0</c>。
    public static int Until(int startMinute, int fromMinute) =>
        ((startMinute - fromMinute) % MinutesPerDay + MinutesPerDay) % MinutesPerDay;

    /// <summary>一个到点：哪一块，还有几分钟。<see cref="InMinutes"/> 是 0 时意思是
    /// 「就是这一刻」—— 判据是它<b>等于</b> 0，不是「小于某个数」。</summary>
    public sealed record Fire(TimeBlock Block, int InMinutes);

    // ---- 自检：块表解析与夹取 + 首启文档（组名 routine-blocks）------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Blocks = new(
        () => "时间块表通过：注释、块注释、尾逗号、行里一个陌生的键都读得回来且存回去还在原地；" +
              "改一个字段只动那一个字段；超范围夹回一天里的第几分钟而范围内的值一个不动；" +
              "类型不对 / 形状不对说清是哪一行哪个键；首启文档里这一族带注释和跟人话的范围，一个毫秒都不出现",
        "时间块表", SelfTest);

    /// <summary>那一族的形状：读的时候宽容、写的时候是原位编辑、值超范围要夹、
    /// 首启文档里带注释和跟人话的范围。
    ///
    /// 跑在 <c>.tmp</c> 里自己那个用完就删的子目录，一条都不碰 <c>%APPDATA%</c>
    /// （<c>CODING_STANDARDS.md</c>「写进文档的东西」那一节）。
    ///
    /// 判据里**不抄键名、不抄范围**：要量的那几个键和那两个范围都从
    /// <see cref="Settings.BlockDeclarations"/> 问出来，报错时指着键名
    /// （<c>CODING_STANDARDS.md</c> 第 6 条）。</summary>
    public static List<string> SelfTest()
    {
        var checks = new Checks("routine-blocks");
        var root = checks.Root!;

        checks.Check("宽容读：注释、块注释、尾逗号、行里一个陌生的键，都不花钱", () =>
        {
            var dir = Case(root, "tolerant");
            File.WriteAllText(FilePath(dir), TolerantSample(), Utf8);

            var state = SettingsStore.Load(dir);
            if (state.Error != null) throw new Exception($"这一份读不通：{state.Error}");
            if (state.Current.Blocks.Count != 2)
                throw new Exception($"读出 {state.Current.Blocks.Count} 个时间块，该是 2 个");
            var night = state.Current.Blocks[0];
            if (night.StartMinute != 22 * 60 + 30 || night.EndMinute != 6 * 60 + 30)
                throw new Exception($"跨午夜那一块读成了 {Clock(night.StartMinute)} → {Clock(night.EndMinute)}");
            if (night.Name != "夜" || night.Kind != Settings.BlockKindSleep)
                throw new Exception($"名字/种类读成了 '{night.Name}'/'{night.Kind}'");
            if (!night.IsSleep) throw new Exception("种类写着睡眠，而 IsSleep 说它不是");
            if (!night.Fade || night.YieldsToInput || night.Flow != Settings.FlowPomodoro)
                throw new Exception("那一行写了的三个行为键读错了");
            // 第二行只写了起止和名字：没写的那几个字段回到声明表的默认值 —— 少写不是错。
            var plain = state.Current.Blocks[1];
            if (!plain.Fade || !plain.YieldsToInput || plain.Flow != "" || plain.Kind != "")
                throw new Exception("没写的字段没有回到声明表的默认值");

            // **对照（一定该被动到）**：样本里那几行确实在它自己那一段里 ——
            // 不然上面那些可能只是「谁都没读到」。词从声明表问出来。
            var sample = TolerantSample();
            foreach (var decl in Settings.BlockDeclarations)
                if (LineOf(sample, decl.Key).Length == 0)
                    throw new Exception($"样本里没有 {decl.Key} 那一行，这一发量的不是那件事");

            // 存一次：用户写过的每一行原样还在（行是**从样本自己**取的，不是手抄一句），
            // 注释、块注释、行里那个陌生的键也都还在。存的那一次顺手把那一行**没写**的字段
            // 补上（补的是声明表给的默认值 —— 那是原位编辑的老规矩：缺的那些补上，
            // 写着的那几行不动）。
            var before = File.ReadAllText(FilePath(dir), Utf8);
            if (SettingsStore.Save(dir, state).Error != null) throw new Exception("存不回去");
            var filled = File.ReadAllText(FilePath(dir), Utf8);
            if (Section(filled, "blocks").Length == 0)
                throw new Exception("存回去的那一份里找不到时间块那一张表");
            foreach (var line in Section(before, "blocks").Split('\n')
                         .Where(l => l.Contains(':') && !l.TrimStart().StartsWith("//")))
                if (!filled.Contains(line, StringComparison.Ordinal))
                    throw new Exception($"存回去之后用户写的那一行不在了：'{line.Trim()}'");
            foreach (var needle in new[] { "// 我自己写的一行", "/* 我自己写的块注释 */", "\"myOwnKey\"" })
                if (!filled.Contains(needle, StringComparison.Ordinal))
                    throw new Exception($"存回去之后「{needle}」不在了 —— 原位编辑把用户自己写的东西剪了");
            // 没写的那几个字段补的是**声明表给的默认值**：词和值都问声明表。
            foreach (var decl in Settings.BlockDeclarations.Where(d => d.Kind == SettingKind.Int))
                if (LineOf(before, decl.Key).Length == 0
                    && !filled.Contains($"\"{decl.Key}\": {decl.Default}", StringComparison.Ordinal))
                    throw new Exception($"存回去之后没写的 {decl.Key} 没有补上它自己的默认值 {decl.Default}");

            // **对照（一定该通）**：补齐之后再一次没人动过的保存，一个字节都不该动。
            if (SettingsStore.Save(dir, SettingsStore.Load(dir)).Error != null) throw new Exception("第二次存不回去");
            if (File.ReadAllText(FilePath(dir), Utf8) != filled)
                Fail("一次没人动过的保存改写了这份文档", filled, File.ReadAllText(FilePath(dir), Utf8));
        });

        checks.Check("写的时候是原位编辑：改一个字段只动那一个字段", () =>
        {
            var dir = Case(root, "edit");
            File.WriteAllText(FilePath(dir), TolerantSample(), Utf8);
            var state = SettingsStore.Load(dir);
            if (state.Error != null) throw new Exception($"底稿读不通：{state.Error}");
            if (SettingsStore.Save(dir, state).Error != null) throw new Exception("底稿存不回去");
            var before = File.ReadAllText(FilePath(dir), Utf8);

            var copy = state.Current.Clone();
            copy.Blocks[1].Name = "改过的名字";
            copy.Blocks[1].YieldsToInput = false;
            if (SettingsStore.Save(dir, new SettingsStore.Read { Current = copy }).Error != null)
                throw new Exception("改完存不回去");
            var after = File.ReadAllText(FilePath(dir), Utf8);

            if (!after.Contains("\"改过的名字\"", StringComparison.Ordinal))
                throw new Exception("改过的名字没写回去");
            if (!after.Contains("\"yieldsToInput\": false", StringComparison.Ordinal))
                throw new Exception("让位那一条没写成 false");
            // 别的字段一个字节没动：两个时刻键的条数前后一样，行数也一样。
            foreach (var decl in Settings.BlockDeclarations.Where(d => d.Kind == SettingKind.Int))
            {
                var needle = $"\"{decl.Key}\":";
                if (Count(after, needle) != Count(before, needle))
                    throw new Exception($"改两个字段却动了 {decl.Key} 那一行的条数");
            }
            if (Count(after, "\"name\":") != Count(before, "\"name\":"))
                throw new Exception("改一个名字却多/少了一行名字");
            if (Count(after, "\"myOwnKey\"") != Count(before, "\"myOwnKey\""))
                throw new Exception("用户自己加在行里的键被剪掉了");
            // **对照（一定该通）**：没碰的那一块一个字节都没动 ——
            // 不然这一条可能只是「整张表重写了一遍」。
            if (Section(after, "blocks").Length > 0
                && Section(after, "blocks").Split('\n').Count() == 0)
                throw new Exception("这一发量的不是那件事");
        });

        checks.Check("值超范围要夹：夹回一天里的第几分钟，夹完能存能读回来", () =>
        {
            var high = Settings.Decl(TimeBlock.KeyPrefix + "startMinute");
            var low = Settings.Decl(TimeBlock.KeyPrefix + "endMinute");
            var dir = Case(root, "clamp");
            File.WriteAllText(FilePath(dir),
                "{\n  \"schemaVersion\": 1,\n  \"routine\": {\n    \"blocks\": [\n"
                + "      { \"startMinute\": 1500, \"endMinute\": -5, \"name\": \"过头的那一块\" }\n"
                + "    ]\n  }\n}\n", Utf8);
            var state = SettingsStore.Load(dir);
            if (state.Error != null) throw new Exception($"这一份读不通：{state.Error}");
            if (state.Current.Blocks[0].StartMinute != high.Max)
                throw new Exception($"1500 没夹到 {high.RangeText} 的上界，实际 {state.Current.Blocks[0].StartMinute}");
            if (state.Current.Blocks[0].EndMinute != low.Min)
                throw new Exception($"-5 没夹到 {low.RangeText} 的下界，实际 {state.Current.Blocks[0].EndMinute}");

            // 夹完写出去再读回来还是那一个值（不是「夹一次就稳定」）。
            if (SettingsStore.Save(dir, state).Error != null) throw new Exception("存不回去");
            var back = SettingsStore.Load(dir);
            if (back.Error != null) throw new Exception($"存回去的那一份读不通：{back.Error}");
            if (back.Current.Blocks[0].StartMinute != high.Max || back.Current.Blocks[0].EndMinute != low.Min)
                throw new Exception("夹完的值走一趟磁盘不是原来那个值");

            // **对照（一定该通）**：范围内的值一个都不该被夹 ——
            // 不然这一条可能只是「谁都被夹」。
            var ok = Case(root, "clamp-in-range");
            File.WriteAllText(FilePath(ok),
                "{\n  \"schemaVersion\": 1,\n  \"routine\": {\n    \"blocks\": [\n"
                + $"      {{ \"startMinute\": {high.Min}, \"endMinute\": {high.Max} }}\n"
                + "    ]\n  }\n}\n", Utf8);
            var inRange = SettingsStore.Load(ok);
            if (inRange.Error != null) throw new Exception($"范围内的这一份读不通：{inRange.Error}");
            if (inRange.Current.Blocks[0].StartMinute != high.Min
                || inRange.Current.Blocks[0].EndMinute != high.Max)
                throw new Exception("范围内的值被夹了");
        });

        checks.Check("类型不对 / 形状不对都是一条读不通，说清是哪一行哪个键", () =>
        {
            var dir = Case(root, "wrong-type");
            File.WriteAllText(FilePath(dir),
                "{\n  \"schemaVersion\": 1,\n  \"routine\": {\n    \"blocks\": [\n"
                + "      { \"startMinute\": \"22:30\" },\n"
                + "      7\n"
                + "    ]\n  }\n}\n", Utf8);
            var state = SettingsStore.Load(dir);
            if (state.Error == null) throw new Exception("类型写错却没报");
            // 报的是**键名 + 那一行**，不是一句「读不通」。
            if (!state.Error.Contains(Settings.BlocksKey + "[1]", StringComparison.Ordinal))
                throw new Exception($"报告里没有指明是哪一行：{state.Error}");
            var field = Settings.BlockDeclarations.First(d => d.Kind == SettingKind.Int).Key;
            if (!state.Error.Contains(field, StringComparison.Ordinal))
                throw new Exception($"报告里没有键名：{state.Error}");

            var table = Case(root, "wrong-shape");
            File.WriteAllText(FilePath(table),
                "{\n  \"schemaVersion\": 1,\n  \"routine\": { \"blocks\": 5 }\n}\n", Utf8);
            var shaped = SettingsStore.Load(table);
            if (shaped.Error == null) throw new Exception("表写成一个数却没报");
            if (!shaped.Error!.Contains(Settings.BlocksKey, StringComparison.Ordinal))
                throw new Exception($"报告里没有键名：{shaped.Error}");
        });

        checks.Check("首启文档：作息这一族带注释和跟人话的范围，一个毫秒都不出现", () =>
        {
            var empty = SettingsDoc.Render(new Settings());
            var table = Settings.Decl(Settings.BlocksKey);
            var switcher = Settings.Decl("routine.enabled");
            // 表自己头上那两行注释（中一行、英一行）一直都在，两个键也都在。
            foreach (var (decl, what) in new[] { (table, "那张表"), (switcher, "总开关") })
            {
                if (!empty.Contains(decl.CommentZh, StringComparison.Ordinal))
                    throw new Exception($"首启文档里没有{what}的中文说明");
                if (!empty.Contains(decl.CommentEn, StringComparison.Ordinal))
                    throw new Exception($"首启文档里没有{what}的英文说明");
            }
            // 整段作息里不许有毫秒 —— 这一族的单位是时刻和分钟（设计文档 `docs/design/config.md`「界面上不出现毫秒」）。
            var routinePart = Section(empty, "routine");
            foreach (var word in new[] { "毫秒", "ms" })
                if (routinePart.Contains(word, StringComparison.Ordinal))
                    throw new Exception($"作息这一族里出现了「{word}」");

            // 有了第一行之后：每个字段上面是它自己那两行说明，整数键行尾是它自己那个范围。
            // 出厂一个时间块都没有，所以那两行要等有行才在纸上 —— 这就是为什么要造一份有行的。
            var withRow = new Settings();
            withRow.Blocks.Add(new TimeBlock { Name = "夜" });
            var rendered = SettingsDoc.Render(withRow);
            foreach (var decl in Settings.BlockDeclarations)
            {
                if (!rendered.Contains(decl.CommentZh, StringComparison.Ordinal))
                    throw new Exception($"{TimeBlock.KeyPrefix}{decl.Key} 的中文说明不在文档里");
                if (!rendered.Contains(decl.CommentEn, StringComparison.Ordinal))
                    throw new Exception($"{TimeBlock.KeyPrefix}{decl.Key} 的英文说明不在文档里");
                if (decl.Kind == SettingKind.Int
                    && !rendered.Contains($"\"{decl.Key}\": {decl.Default}, // {decl.RangeText}",
                                          StringComparison.Ordinal))
                    throw new Exception($"{TimeBlock.KeyPrefix}{decl.Key} 行尾没有它自己那个范围 {decl.RangeText}");
            }
        });

        return checks.Report();
    }

    // ---- 自检：跨午夜的算术（组名 routine-arith）-------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Arithmetic = new(
        () => "作息表算术通过：22:30 → 06:30 是一块（前后两端都归它、止那一刻归下一块）、止 == 起是整整一天、" +
              "块内块外各判一次、到点推算走一圈的算术（撞在同一刻上 0 分钟）、一个表都没有时问不出块也不算出到点、" +
              "问过之后那份表没变（无状态）、重叠靠前的那一行赢、只有「睡眠」带行为语义",
        "作息表算术", ArithmeticSelfTest);

    /// <summary>当前块与到点推算，两头都过一遍：23:59、00:00、块内、块外、一个表都没有。
    ///
    /// 纯算术：构造一份设置，问完再问一遍，中间不碰磁盘也不碰时钟。判据里的时刻都是
    /// <c>时 × 60 + 分</c> 算出来的，<b>不抄 1439</b>（那是 <see cref="MinutesPerDay"/> 推的）。</summary>
    public static List<string> ArithmeticSelfTest()
    {
        var checks = new Checks();

        // 两块，**不重叠** —— 边界才判得干净：夜 22:30 → 06:30 跨午夜，白 09:00 → 17:00 当天。
        // 重叠那一族另有一张表（靠前的那一行赢），在下面自己判。
        var settings = new Settings();
        settings.Blocks.Add(Block(22 * 60 + 30, 6 * 60 + 30, "夜", Settings.BlockKindSleep));
        settings.Blocks.Add(Block(9 * 60, 17 * 60, "白"));

        TimeBlock? At(int h, int m) => Routine.At(settings.Blocks, h * 60 + m);
        Fire? Next(int h, int m) => Routine.NextFire(settings.Blocks, h * 60 + m);

        checks.Check("跨午夜：22:30 → 06:30 是一块，前后两端都归它，止那一刻归下一块", () =>
        {
            if (Name(At(22, 30)) != "夜") throw new Exception("22:30 那一刻不算它自己 —— 起点自己都不算自己");
            if (Name(At(23, 59)) != "夜") throw new Exception("23:59 不在这一块里 —— 两端没算到");
            if (Name(At(0, 0)) != "夜") throw new Exception("00:00 归错了块 —— 跨午夜那一块该盖住零点");
            if (Name(At(6, 29)) != "夜") throw new Exception("06:29 不在这一块里");
            if (Name(At(6, 30)) == "夜") throw new Exception("06:30 还算在夜里 —— 止那一刻归下一块");
            if (Name(At(22, 29)) == "夜") throw new Exception("22:29 就提前算进夜里了");
            // **对照（一定该通）**：一个当天之内的时间块在凌晨不成立 ——
            // 不然这一组可能只是「总有某一块在」。
            if (Name(At(1, 0)) == "白") throw new Exception("一个当天的块盖住了凌晨");
        });

        checks.Check("止 == 起读作跨午夜：整整一天", () =>
        {
            var whole = new Settings();
            whole.Blocks.Add(Block(9 * 60, 9 * 60, "整天"));
            if (Name(Routine.At(whole.Blocks, 3 * 60)) != "整天")
                throw new Exception("起止同一个数没有读作整整一天");
            if (Name(Routine.At(whole.Blocks, 9 * 60)) != "整天")
                throw new Exception("起止同一个数，起的那一刻自己不在里面");
        });

        checks.Check("块内块外：09:00–17:00 之内归它，之前和之后都空着", () =>
        {
            if (Name(At(9, 0)) != "白") throw new Exception("09:00 不在那一天里");
            if (Name(At(16, 59)) != "白") throw new Exception("16:59 不在那一天里");
            if (Name(At(17, 0)) != null) throw new Exception("17:00 还算在那一天里");
            if (Name(At(8, 59)) != null) throw new Exception("08:59 就提前进了那一天");
        });

        checks.Check("到点推算：走一圈的算术（23:59 → 次日 09:00 是 541 分钟）", () =>
        {
            // **两端各问一次**：23:59 往后要绕过午夜，12:00 往前不用绕。
            if (Next(23, 59) is not { InMinutes: 541 } night || night.Block.Name != "白")
                throw new Exception($"23:59 的下一个到点算成了 {Spell(Next(23, 59))}");
            if (Next(12, 0) is not { InMinutes: 10 * 60 + 30 } day || day.Block.Name != "夜")
                throw new Exception($"12:00 的下一个到点算成了 {Spell(Next(12, 0))}");
            if (Next(21, 59) is not { InMinutes: 31 } before || before.Block.Name != "夜")
                throw new Exception($"21:59 的下一个到点算成了 {Spell(Next(21, 59))}");
            if (Next(16, 0) is not { InMinutes: 6 * 60 + 30 } after || after.Block.Name != "夜")
                throw new Exception($"16:00 的下一个到点算成了 {Spell(Next(16, 0))}");
        });

        checks.Check("到点就在这一刻：起点撞在问的那一刻上答 0 分钟", () =>
        {
            // 单独一张表：只有一块 00:00 → 08:00，问 00:00（正好撞在起点上）和 00:01。
            var one = new Settings();
            one.Blocks.Add(Block(0, 8 * 60, "早晨"));
            if (Name(Routine.At(one.Blocks, 0)) != "早晨") throw new Exception("00:00 不在这一块里");
            if (Routine.NextFire(one.Blocks, 0) is not { InMinutes: 0 } now || now.Block.Name != "早晨")
                throw new Exception($"00:00 的下一个到点算成了 {Spell(Routine.NextFire(one.Blocks, 0))}");
            var tick = Routine.NextFire(one.Blocks, 1);
            if (tick is not { InMinutes: var left } || left != MinutesPerDay - 1)
                throw new Exception($"00:01 的下一个到点隔了 {tick?.InMinutes} 分钟，该是一整天减一分钟");
        });

        checks.Check("一个表都没有：不在任何块里，也没有下一个到点", () =>
        {
            var none = new Settings();
            if (Routine.At(none.Blocks, 12 * 60) != null) throw new Exception("空表也问出了一个块");
            if (Routine.NextFire(none.Blocks, 23 * 60 + 59) != null) throw new Exception("空表也算出了到点");
            // **对照（一定该通）**：同一刻在有表的那一份上问得出块 ——
            // 不然这一条可能只是「问谁都不出来」。
            if (Name(At(12, 0)) != "白") throw new Exception("同一刻在有表的那一份上问不出块");
        });

        checks.Check("无状态：每问一遍，那份表一个字节都没变，而总开关不是算术读的", () =>
        {
            // **一問一比**，不是「全问完比一次」：一个改表的算术连着问两次会把自己改回去
            // （第一次翻过来、第二次翻回去），那样「问完没变」看着是对的、其实它写了表。
            // 逐问逐比量的是「**这一问**动没动那份表」。
            foreach (var (what, ask) in new (string, Func<JsonNode?>)[]
                     {
                         ("At 23:59", () => At(23, 59)?.Name),
                         ("NextFire 00:00", () => Next(0, 0)?.InMinutes),
                         ("At 03:00", () => At(3, 0)?.Name),
                         ("At 09:00", () => At(9, 0)?.Name),
                     })
            {
                var snapshot = settings.ToDocument().ToJsonString();
                ask();
                var now = settings.ToDocument().ToJsonString();
                if (now != snapshot) Fail($"{what} 动过那张表 —— 算术不是纯的", snapshot, now);
            }

            // 总开关只决定「表要不要用」，而它住在设置上：关掉它，那几块还在表里，
            // 而算术问的还是同一份表（接线是调度那一侧的事，工单 03）。
            settings.RoutineEnabled = false;
            if (settings.Blocks.Count == 0) throw new Exception("总开关把表本身清空了");
            if (Name(At(23, 59)) != "夜") throw new Exception("算术读了总开关 —— 它不该读");
            settings.RoutineEnabled = true;
        });

        checks.Check("重叠：靠前的那一行赢", () =>
        {
            var both = new Settings();
            both.Blocks.Add(Block(9 * 60, 17 * 60, "靠前"));
            both.Blocks.Add(Block(12 * 60, 13 * 60, "靠后"));
            if (Name(Routine.At(both.Blocks, 12 * 60 + 30)) != "靠前")
                throw new Exception("重叠的两块里赢的不是靠前那一行");
        });

        checks.Check("只有「睡眠」带行为语义：别的种类只是标签", () =>
        {
            if (!settings.Blocks[0].IsSleep) throw new Exception("种类写着睡眠，而它不算睡眠");
            foreach (var kind in new[] { "", "focus", "休息", "Sleep", "sleep " })
                if (Block(0, 1, "x", kind).IsSleep)
                    throw new Exception($"种类 '{kind}' 也被当成睡眠了 —— 那是政策，不是标签");
        });

        return checks.Report();

        static TimeBlock Block(int start, int end, string name, string kind = "") =>
            new() { StartMinute = start, EndMinute = end, Name = name, Kind = kind };

        static string? Name(TimeBlock? block) => block?.Name;
    }

    // ---- 自检：作息表键进生活模式往返（组名 routine-lifemode）------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf LifeMode = new(
        () => "作息表进生活模式通过：作息这一族的键逐条在生活模式那一份里（键名从声明表问）、" +
              "切到另一份作息表那几行跟着换、切回来活文档逐字节原来那份、导出与活的那一份逐字节相同，" +
              "而换一份作息表导出必须变（不是「导出什么都没写」）；系统那几条不装进去",
        "作息表进生活模式", LifeModeSelfTest);

    /// <summary>存一个带作息表的生活模式、切过去、再导出：<b>逐字节相同</b>。
    ///
    /// <b>不开第三把尺子</b>：存 / 切走的是 <see cref="LifeModeStore"/> 现成那两条路
    /// （<c>SaveAs</c> / <c>SwitchTo</c>），落笔走 <see cref="SettingsStore"/>（原位编辑那一套），
    /// 「逐字节相同」就是两份文件读出来比一遍 —— 同一把尺子，生活模式那几张卡都在用。
    ///
    /// 为什么这一族非要自己一组：设计文档 `docs/design/life-mode.md`「生活模式装的是业务功能，不是这台机器的属性」那条把
    /// 界线交给**域本身**，所以「作息这一族在生活模式里面」是一个能从声明表推出来的事实；
    /// 而它一旦不成立（有人把这一族标成系统域），症状是「预设生活模式带不走自己的作息」——
    /// 界面上没有一句话会说这件事。</summary>
    public static List<string> LifeModeSelfTest()
    {
        var checks = new Checks("routine-lifemode");
        var dir = checks.Root!;

        checks.Check("存一个带作息表的生活模式 → 切过去 → 切回来 → 再导出，逐字节相同", () =>
        {
            // 第一份：作息表和别的地方都不一样的值 —— 跨午夜那一块、种类睡眠、
            // 让位关掉、到点走番茄钟那一行。哪一处和活的那份一样，那一条判据就空过了。
            var first = new Settings();
            first.Blocks.Add(new TimeBlock
            {
                StartMinute = 22 * 60 + 30, EndMinute = 6 * 60 + 30,
                Name = "夜", Kind = Settings.BlockKindSleep, Fade = true,
                YieldsToInput = false, Flow = Settings.FlowPomodoro,
            });
            first.ActivityTimeoutMin = 33;
            var second = first.Clone();
            second.Blocks[0].StartMinute = 7 * 60;
            second.Blocks[0].YieldsToInput = true;
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

            // **对照（一定该被动到）**：作息这一族的键都在生活模式那一份里，键名从声明表问出来。
            // 少一条的症状是「预设带不走自己的作息」，而界面上没有一句话会说这件事。
            foreach (var key in Settings.Declarations
                         .Where(d => d.Domain == Settings.RoutineDomain)
                         .Select(d => d.Key))
            {
                if (SettingMap.Find(modeValues, key) == null)
                    throw new Exception($"生活模式那一份里没有 {key} —— 这一族不在生活模式里面");
                // 文档里点分键是**嵌套**的（`"routine": { "enabled": … }`），
                // 所以到原文里找的是**最后那一段** —— 词还是从键名自己长出来的。
                if (LineOf(modeText, key.Split('.').Last()).Length == 0)
                    throw new Exception($"生活模式那一份里找不到 {key} 那一行，这一发量的不是那件事");
            }
            // 时间块**行里**那几个字段不是点分键（一条路径指不进某一行），所以逐行问。
            foreach (var field in Settings.BlockDeclarations.Select(d => d.Key))
            {
                if (LineOf(modeText, field).Length == 0)
                    throw new Exception($"生活模式那一份里找不到 {TimeBlock.KeyPrefix}{field} 那一行");
            }
            if (!modeText.Contains(Settings.BlockKindSleep, StringComparison.Ordinal))
                throw new Exception("生活模式那一份里没有那一块的种类 —— 那一行没跟着走");

            // 切到「白天的」：活文档整个换掉，换掉的那份落在 .bak 里。
            var (err, after) = LifeModeStore.SwitchTo(dir, SettingsStore.Load(dir), "白天的");
            if (err != null) throw new Exception($"切不过去：{err}");
            if (!File.Exists(SettingsStore.BackupPath(dir))) throw new Exception("切换没留下 .bak");
            if (File.ReadAllText(SettingsStore.BackupPath(dir), Utf8) != live)
                Fail("留下的那份不是切换之前那份活文档", live,
                                      File.ReadAllText(SettingsStore.BackupPath(dir), Utf8));
            var liveAfter = File.ReadAllText(SettingsStore.FilePath(dir), Utf8);
            if (Section(liveAfter, "routine") == Section(live, "routine"))
                Fail("切过去之后作息表那几行一个字节都没变 —— 那一族没跟着换",
                                      Section(live, "routine"), Section(liveAfter, "routine"));

            // 切回「夜里的」：**活文档逐字节回到切之前那份**。承重处：作息表那几行是原位
            // 编辑回去的（只有值变了），所以「回来」是字节相同，而不是「值一样」。
            var (backErr, back) = LifeModeStore.SwitchTo(dir, after!, "夜里的");
            if (backErr != null) throw new Exception($"切不回来：{backErr}");
            var liveBack = File.ReadAllText(SettingsStore.FilePath(dir), Utf8);
            if (liveBack != live)
                Fail("切回来之后活文档不是逐字节原来那份", live, liveBack);

            // 导出：拿活的那份导出一份出来，**一个字节都不该变** —— 导出是编辑，
            // 而它此刻和活的那份一模一样（设计文档 `docs/design/config.md`「导入一份已经是当前的文档，文件一个字节都不动」
            // 那一条在导出这一侧的样子）。
            var exported = Path.Combine(dir, "导出.jsonc");
            if (SettingsStore.Export(dir, SettingsStore.Load(dir), exported) != null)
                throw new Exception("导出不回来");
            if (File.ReadAllText(exported, Utf8) != live)
                Fail("导出的那一份和活的那一份不是逐字节相同",
                                      live, File.ReadAllText(exported, Utf8));

            // **对照（一定该通过）**：换一份作息表，导出**必须**变 ——
            // 不然上面那些「逐字节相同」可能只是「导出什么都没写」。
            var other = back!.Current.Clone();
            other.Blocks[0].StartMinute = 5 * 60;
            if (SettingsStore.Export(dir, new SettingsStore.Read { Current = other }, exported) != null)
                throw new Exception("换了一份作息表之后导出不回来");
            if (File.ReadAllText(exported, Utf8) == live)
                Fail("换了一份作息表，导出的那一份却一个字节都没变",
                                      live, File.ReadAllText(exported, Utf8));

            // 系统那几条（这台机器的属性）不进生活模式 —— 判据是域本身，不是这份清单。
            foreach (var key in Settings.Declarations
                         .Where(d => !d.IsContainer && d.Domain == Settings.SystemDomain)
                         .Select(d => d.Key))
                if (SettingMap.Find(modeValues, key) != null)
                    throw new Exception($"生活模式那一份里写着系统那一条 {key}");
        });

        return checks.Report();
    }

    // ---- 共用的小工具 --------------------------------------------------------

    private static readonly UTF8Encoding Utf8 = new(false);

    private static string FilePath(string dir) => SettingsStore.FilePath(dir);

    /// 自检自己那个用完就删的子目录：一个用例一个，谁都不依赖谁跑在前面。
    private static string Case(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonObject? Values(string path) =>
        JsoncDoc.TryParse(File.ReadAllText(path, Utf8), out var doc, out _) ? doc!.Values : null;

    /// 一段文本里某个片段出现几次。这一组问「那一行还在不在原位」，数次数比数存在更严
    /// （和 <c>SettingsStore</c> 那一族同一把尺子）。
    private static int Count(string haystack, string needle) =>
        haystack.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

    /// 文档里写着那个键的那<b>一整行原文</b>。词是从键名自己长出来的（<c>"key":</c>），
    /// 不是手抄的一句搜索词 —— 手抄的那一份量的是当初那个副本
    /// （<c>CODING_STANDARDS.md</c> 第 6 条）。
    private static string LineOf(string text, string key)
    {
        var needle = "\"" + key + "\"";
        foreach (var line in text.Split('\n'))
            if (line.Contains(needle, StringComparison.Ordinal)) return line;
        return "";
    }

    /// 从 <c>"key": {</c> 那一行到它自己那个闭合花括号为止的那一段原文（按缩进配对，
    /// 不数花括号 —— 注释和用户写的文字里也可能有花括号）。没找到就是空串。
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

    /// <summary>两段不一样时把**差在哪儿**带出来：这一组比的是字节，光说「不一样」看不出是什么变了。
    ///
    /// 一行很长时（值视图是压成一行的 JSON）不把整行倒出来，只说**第几个字符**上开始不一样
    /// 加前后各一小截 —— 一条判据的实测结果要能被人读，读不出来的那一条和没有实测结果一样。</summary>
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

    private static void Fail(string what, string want, string got) =>
        throw new Exception(SectionDiff(what, want, got));

    private static string Spell(Fire? fire) =>
        fire == null ? "没有到点" : $"{fire.Block.Name} 还有 {fire.InMinutes} 分钟";

    /// 手写的那一份作息表：行注释、块注释、尾逗号、行里一个 Keeper 没听过的键。
    /// 第一行**每个字段都写全**（对照要用它挨个问），第二行只写起止和名字 ——
    /// 少写的那几个字段回默认值那一条量的是它。
    private static string TolerantSample() =>
        "{ // 我自己写的一行\n"
        + "  \"schemaVersion\": 1,\n"
        + "  /* 我自己写的块注释 */\n"
        + "  \"routine\": {\n"
        + "    // 整张表要不要用\n"
        + "    \"enabled\": true,\n"
        + "    \"blocks\": [\n"
        + "      { // 夜里那一块\n"
        + "        \"startMinute\": 1350, \"endMinute\": 390,\n"
        + "        \"name\": \"夜\", \"kind\": \"sleep\",\n"
        + "        \"fade\": true, \"flow\": \"pomodoro\", \"yieldsToInput\": false,\n"
        + "        \"myOwnKey\": \"留着\",\n"
        + "      },\n"
        + "      {\n"
        + "        \"startMinute\": 540, \"endMinute\": 1020, \"name\": \"白天\",\n"
        + "      },\n"
        + "    ],\n"
        + "  }\n"
        + "}\n";
}
