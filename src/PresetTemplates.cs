using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Keeper;

/// <summary>自检：随安装带来的那几份<b>有出处的生活模式</b>逐份过一遍契约
/// （工单 06，`.scratch/archive/keeper-routine/issues/06-first-batch-presets.md`）。
///
/// <b>它量什么、不量什么</b>：
/// <list type="bullet">
/// <item>量**装在程序里那一个目录**（<see cref="PresetLifeModes.Dir"/>）—— **只读它，一个字节都不写**
/// （和 <see cref="Presets.SelfTest"/> 里那一发同一条纪律，理由见 <see cref="PresetLifeModes"/>）。</item>
/// <item>量**每一份**对照契约过的那几件事：不装「系统」那几条键；每个块<b>显式</b>写了让位键；
/// 要静默的块种类是「睡眠」那个取值；遮罩用 <c>text</c> 而块名不在两处重复；每一块都带着
/// 「演绎」或者一条来源。</item>
/// <item>量<b>逐份的整趟</b>：收进来 → 切过去 → 在睡眠那一块里静默 → 到点抬一次 → 导出 → 再收回来
/// 逐字节相同。**四份各走一遍**。</item>
/// </list>
///
/// <b>为什么名单是从目录里现问的</b>：这一族没有自己的名单（预设不是第二种实体，
/// <c>docs/adr/0005-…</c>「预设生活模式不是第二种实体」），而判据里存一份会腐烂的名字清单
/// 正是 <c>CODING_STANDARDS.md</c> 第 6 条要防的那一种 —— 加一份预设却忘了改这里，自检照样绿。
/// 所以**目录里有几份就跑几份**；「恰好四份」是出厂状态，不是判据。
///
/// <b>词从声明表问</b>：「系统」那几条键是问 <see cref="Settings.Declarations"/> 那一域的，
/// 块行七个字段是问 <see cref="Settings.BlockDeclarations"/>，「睡眠」那一个种类取值是问
/// <see cref="Settings.BlockKindSleep"/> —— 三处都不手抄（加了第 4 个系统级设置会自动跟着走）。</summary>
public static class PresetTemplates
{
    /// <summary>不该留在出处那句话里的标记 —— 它们是**写给人看的**，而那句话是**画给人看的</b>。
    ///
    /// 只收 markdown 那一族的着重/引用标记，不收标点：出处那句话本来就要用《》、：这些，
    /// 拦掉它们会把「照着条文原话写」这件事也拦了。反引号也在这儿 —— 配置文件内部的名字本来
    /// 就**不许**出现在界面上（同 <c>CODING_STANDARDS.md</c>「界面上不出现配置文件内部的名字」
    /// 那一族），出处那一行是界面的一部分，带反引号等于把键名印在屏幕上。</summary>
    private static readonly string[] MarkdownMarks = ["**", "__", "`", "~~"];

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Templates = new(
        () => "随安装那几份有出处的生活模式通过：目录里列得出、每份读得通、每份写了出处；" +
              "逐份对照契约（不装「系统」那几条键、每个块显式写了让位键、要静默的块种类是「睡眠」、" +
              "遮罩用 text 而块名不重写、每块带「演绎」或来源、声明演绎的那几份逐块标了）；" +
              "逐份走整趟（收进来 → 切过去 → 睡眠块里静默且摘掉种类就抬 → 块走完抬一次 → 导出 → 再收回来" +
              "逐字节相同，回环之后那张作息表和七个块字段都跟着走）；" +
              "只读：整趟走完随安装那一个目录一个字节没动",
        "随安装那几份有出处的生活模式", SelfTest, Measurements);

    /// <summary>随安装带来的那几份：一份一份过契约，再一份一份走整趟。</summary>
    public static List<string> SelfTest()    {
        var checks = new Checks();
        Readouts.Clear();

        var gallery = PresetLifeModes.Load();
        checks.Check($"列出 {PresetLifeModes.Dir}", () =>
        {
            if (gallery.Missing) throw new Exception($"{PresetLifeModes.Dir} 不在 —— 随安装没带那几份");
            if (gallery.None) throw new Exception($"{PresetLifeModes.Dir} 里一个都没有");
            foreach (var preset in gallery.Presets)
            {
                if (!preset.Readable) throw new Exception($"「{preset.Name}」读不通：{preset.Note}");
                if (preset.Note.Trim().Length == 0)
                    throw new Exception($"「{preset.Name}」顶上没写出处 —— 注释即考证（ADR-0005）");
            }
        });

        // 界面上那一行出处是**原样**摆的（画在画廊那一行下面的灰字 `note.Text = preset.Note`，
        // 一颗普通的 `Label`），所以出处那句话得是**能直接读出来的话**，不能是写给人看的标记。
        //
        // 2026-10-02 实拍抓到：四份里有三份在出处那一句里用了 markdown 的 `**…**` 当着重号，
        // 于是屏幕上原样印着四个星号（`docs/research/images/presets-four-light-zh.png` 当年的那一帧）。
        // 自检里那些判据量的是「出处读得回来、有没有写」，没有一条量「摆出来好不好看」——
        // 正是 `CODING_STANDARDS.md` 第 9 条那一类：**拍完要拿眼睛看**。
        //
        // **为什么是判据而不是「以后注意」**：出处那句话是**注释**，而注释按契约就是界面上原样显示的
        // 那一行（声明表那一族同一条纪律）。一份模板将来换个人写，markdown 着重号会长回去。
        checks.Check("出处那句话是直接读得出来的话，没有 markdown 标记留在屏幕上", () =>
        {
            foreach (var preset in gallery.Presets)
            {
                foreach (var mark in MarkdownMarks)
                {
                    var at = preset.Note.IndexOf(mark, StringComparison.Ordinal);
                    if (at >= 0)
                        throw new Exception(
                            $"「{preset.Name}」的出处那句话里有 {mark}（第 {at + 1} 个字起）—— "
                            + $"出处是原样画在画廊那一行下面的，{mark} 会原样印在屏幕上。"
                            + $"要着重就写进句子（「演绎重构」四个字本身已经够重），别写标记。整句是：{preset.Note}");
                }
            }
        });

        // 装在程序里那一个目录**开跑之前的逐字节实测结果**：整趟走完必须一模一样。
        var before = Shot(gallery);

        foreach (var preset in gallery.Presets)
        {
            var text = ReadOf(preset);
            checks.Check($"「{preset.Name}」对照契约过一遍", () => AgainstContract(preset.Name, text));
            checks.Check($"「{preset.Name}」收进来 → 切过去 → 睡一块 → 抬一次 → 导出 → 再收回来", () =>
                RoundTrip(preset));
        }

        checks.Check("只读：整趟走完，随安装那一个目录一个字节没动", () =>
        {
            if (Shot(gallery) != before)
                throw new Exception($"{PresetLifeModes.Dir} 被写了 —— 它是只读的那一份");
        });
        return checks.Report();
    }

    /// <summary>那一跑**现问**出来的实测结果（自检把它当 <c>Suffix</c> 追在后面印，
    /// 和过没过无关）。<b>四份各一行</b>，写的是这一跑量到的字节数和那一刻的名字 ——
    /// 文档里**不抄**这些数（<c>CODING_STANDARDS.md</c>「数字不要抄进文档」那一段）。</summary>
    public static string Measurements() => string.Join("；", Readouts);

    private static readonly List<string> Readouts = [];

    // ---- 契约那几件事 ------------------------------------------------------------

    /// <summary>一份模板对照契约过一遍。
    ///
    /// **逐条对应工单 06 的验收项**，每一条都能失败：词全部从声明表问，报错时指着**键名**
    /// （<c>CODING_STANDARDS.md</c> 第 6 条）。注释那一半量的是**原文**而不是解析结果 ——
    /// 出处和「演绎」住在注释里，而解析器把注释扔了。</summary>
    private static void AgainstContract(string name, string text)
    {
        if (!JsoncDoc.TryParse(text, out var parsed, out var error))
            throw new Exception($"这一份读不通：{error}");
        if (parsed == null) throw new Exception("读不出一个对象");
        var doc = parsed.Values;   // 值视图（解析器把注释扔了，逐块出处那一半量的是原文）

        // 1. 不装「系统」那几条键。**判据是域本身**：那份清单是问出来的，不是抄的。
        foreach (var decl in Settings.Declarations.Where(d => d.Domain == Settings.SystemDomain))
            if (doc.ContainsKey(decl.Key))
                throw new Exception($"它装了 {decl.Key} —— 「系统」那域的键一律不进生活模式");

        // 块表住在那一个组底下（`routine.blocks`）—— 组名**从键名推**，不手抄。
        var routineGroup = Settings.BlocksKey[..Settings.BlocksKey.LastIndexOf('.')];
        var blocksLeaf = Leaf(Settings.BlocksKey);
        if (doc[routineGroup]?[blocksLeaf] is not System.Text.Json.Nodes.JsonArray rows)
            throw new Exception($"它没有一张 {routineGroup}.{blocksLeaf} —— 预设是「一份完整的配置文档 + 一张作息表」");
        if (rows.Count == 0) throw new Exception("那张表一个块都没有");

        var routine = Section(text, blocksLeaf);
        if (routine.Length == 0) throw new Exception("原文里找不到那张表，逐条那一半量不了");

        // 2. **每个块都显式写了让位键**。ADR-0005：没有默认政策，漏写就碎。
        //    数的是原文里那几行 —— 解析之后「写没写」和「值是什么」分不开了。
        var yieldKey = Settings.Decl(TimeBlock.KeyPrefix + "yieldsToInput").Key;
        var yieldLines = Lines(routine, $"\"{yieldKey}\":");
        if (yieldLines != rows.Count)
            throw new Exception($"{rows.Count} 个块，原文里 {yieldKey} 写了 {yieldLines} 次 —— " +
                                $"每一个都要显式写（ADR-0005：没有默认政策）");

        // 3. 要静默的块种类标「睡眠」；而每一份至少有一块是那样。
        var sleepKind = Settings.BlockKindSleep;
        var named = 0;
        // 字段名**从声明表问**，不手抄（这一份文件顶上那句话就是这个意思，2026-10-02 复核
        // 时发现下面两行原来是写死的 `"kind"` / `"name"` —— 加一个块字段或改一个键名，
        // 这两条会**默默量错那一份**）。`RowText` 收的是摘掉 `blocks[].` 前缀之后的那个裸名。
        var kindField = BlockFieldName("kind");
        var nameField = BlockFieldName("name");
        foreach (var row in rows.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var kind = RowText(row, kindField);
            if (kind != sleepKind) continue;
            named++;
            if (!RowText(row, nameField).Trim().StartsWith("//", StringComparison.Ordinal))
            {
                // 名字不得为空 —— 一块没有名字，托盘那一行和遮罩上都空着。
                if (RowText(row, nameField).Trim().Length == 0)
                    throw new Exception("有一块标着睡眠，而它没有名字");
            }
        }
        if (named == 0)
            throw new Exception($"它一个块都没标 {sleepKind} —— 没有静默的那一块，「要到点才抬」演不出来");

        // 4. 遮罩用 `text`，而**块名不在两处重复存**（ADR-0005「字不在两处存」）。
        var textKey = Settings.Decl(Step.KeyPrefix + "text").Key;
        var words = Steps(doc, textKey);
        if (words.Count == 0)
            throw new Exception($"它没有一步的 {textKey} 写了字 —— 遮罩上的字走的就是那一半");
        var blockNames = rows.OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(r => RowText(r, nameField).Trim()).Where(n => n.Length > 0).ToList();
        foreach (var (step, said) in words)
        {
            // **对照（一定该被动到）**：这一步确实在遮罩的 content 里摆着「文字」，
            // 而 content 里没有它的，屏幕上画不出这几个字 —— 不先问这一句，
            // 上面那条可能只是「文档里有个没人读的字」。
            if (!StageShows(step, "text"))
                throw new Exception($"有一步 {textKey} 写了字，可它的 content 里没摆「文字」 —— 画不出来");
            foreach (var blockName in blockNames)
                if (said.Contains(blockName, StringComparison.Ordinal))
                    throw new Exception($"遮罩上的字「{said}」里有块名「{blockName}」—— " +
                                        $"字在两处存了（块名只有作息表一份真相）");
        }

        // 5. 每一块都带着「演绎」或者一条来源。
        var blockStarts = Count(routine, $"\"{Settings.Decl(TimeBlock.KeyPrefix + "startMinute").Key}\":");
        if (blockStarts != rows.Count)
            throw new Exception($"表里 {rows.Count} 个块，原文里数得出 {blockStarts} 个起点");
        var marked = routine.Split('\n')
            .Count(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && (l.Contains("演绎", StringComparison.Ordinal)
                            || l.Contains("来源", StringComparison.Ordinal)));
        if (marked < blockStarts)
            throw new Exception($"{blockStarts} 个块里只有 {marked} 块写了「演绎」或「来源」—— " +
                                $"逐块出处是这一份模板的验收项（ADR-0005「考证的取向」）");

        // 6. 声明自己是「演绎重构」的那一份，**每一块**都标了。
        if (Head(text).Contains("演绎重构", StringComparison.Ordinal))
        {
            var inRoutine = routine.Split('\n')
                .Count(l => l.Contains("演绎", StringComparison.Ordinal));
            if (inRoutine < blockStarts)
                throw new Exception($"它自己写着「演绎重构」，可表里只有 {inRoutine}/{blockStarts} 块标了" +
                                    $"「演绎」—— 整份都是演绎就得逐块标");
        }
    }

    // ---- 整趟：收 → 切 → 睡 → 抬 → 导出 → 再收 --------------------------------

    /// <summary>一份模板走完整的一趟，逐段可核对。
    ///
    /// **跑在 <c>.tmp</c> 里自己那一个用完就删的目录**（<c>CODING_STANDARDS.md</c>「临时产出的落点」
    /// 那一节），**一个字节都不碰 <c>%APPDATA%</c>，也不碰装在程序里那一个预设目录**。
    /// 「收进来」走的是 <see cref="LifeModeStore.TakeIn"/> 那**唯一一份**实现。</summary>
    private static void RoundTrip(PresetLifeModes.Preset preset)
    {
        var dir = OptionsHost.TempDir("preset-templates");
        try
        {
            var bytes = PresetLifeModes.BytesOf(preset)
                ?? throw new Exception("那一份的字节读不出来");

            // 收进来 —— 走导入那条路（递增号、撞名、逐字节搬都在那一个方法里）。
            var took = LifeModeStore.TakeIn(dir, preset.Name, bytes);
            if (took.Error != null) throw new Exception($"收不进来：{took.Error}");

            // 切过去。
            var state = SettingsStore.Load(dir);
            if (state.Error != null) throw new Exception($"收完活的那一份读不通：{state.Error}");
            var switched = LifeModeStore.SwitchTo(dir, state, preset.Name);
            if (!switched.Ok) throw new Exception($"切不过去：{switched.Detail}");

            var live = SettingsStore.Load(dir);
            if (live.Error != null) throw new Exception($"切完活的那一份读不通：{live.Error}");
            var table = live.Current;
            if (!table.RoutineEnabled) throw new Exception("切过去之后作息表整个关着 —— 这一趟演不出来");

            // 睡一块（静默）→ 到点抬一次。这两发跑的是**切过去之后活的那一份**，
            // 也就是真会生效的那张表，不是模板文本里抄出来的数。
            var (inside, boundary) = SilenceThenRaise(preset.Name, table);

            // 导出 → 再收回来 → 逐字节相同。
            var outPath = Path.Combine(dir, $"roundtrip-{preset.Name}.jsonc");
            if (SettingsStore.Export(dir, live, outPath) != null)
                throw new Exception("导不出来");
            var back = LifeModeStore.TakeIn(dir, $"{preset.Name}-回环", File.ReadAllBytes(outPath));
            if (back.Error != null) throw new Exception($"导出的那一份收不回来：{back.Error}");

            var entry = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir)).Find($"{preset.Name}-回环")
                ?? throw new Exception("收回来了却不在名单上");
            var again = File.ReadAllBytes(LifeModeStore.PathOf(dir, entry));
            var exported = File.ReadAllBytes(outPath);
            if (!again.SequenceEqual(exported))
                throw new Exception($"再收回来和导出的那一份不是逐字节相同：{exported.Length} 字节 vs " +
                                    $"{again.Length} 字节");

            // **对照（一定该被动到）**：那张作息表跟着走了 —— 只搬标量键的实现照样能让
            // 上面那条「逐字节相同」绿着，而这一份模板的全部内容就在那张表里。
            var carried = File.ReadAllText(LifeModeStore.PathOf(dir, entry), Utf8);
            if (!carried.Contains($"\"{Leaf(Settings.BlocksKey)}\"", StringComparison.Ordinal))
                throw new Exception("回环之后作息表那一张没跟着走 —— 逐字节相同量的是标量键，不是这一份模板");
            var slept = table.Blocks.First(b => b.IsSleep);
            foreach (var field in Settings.BlockDeclarations)
            {
                var key = Leaf(TimeBlock.KeyPrefix + field.Key);
                if (!carried.Contains($"\"{key}\"", StringComparison.Ordinal))
                    throw new Exception($"回环之后块行里的 {key} 没了（键名从声明表问的）");
            }
            if (!carried.Contains(slept.Name, StringComparison.Ordinal))
                throw new Exception($"回环之后睡眠块的块名「{slept.Name}」不在那一份里 —— 表没跟着走");

            // 那一跑现问出来的实测结果：四份各一行（导出的字节数 = 再收进来的字节数，
            // 加上「睡在哪一刻、几点抬」这两个真会生效的数）。**不抄进任何文档。**
            Readouts.Add($"{preset.Name} {table.Blocks.Count}块 睡{Routine.Clock(inside)}静默→" +
                         $"{Routine.Clock(boundary)}抬 「{slept.Name}」" +
                         $" 导出{exported.Length}B=再收{again.Length}B");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>切过去之后那张表：<b>睡眠那一块里静默</b>，<b>块走完抬一次</b>。
    ///
    /// 每一发都配一条<b>对照（一定该通）</b>：同一张表、同一串输入，只把睡眠那一行的种类摘掉 ——
    /// 不配它，这一组可能只是「遮罩压根没起来」。时刻全部用
    /// <see cref="Routine"/> 那一族算（<b>不抄 1439、720 那一类数</b>）。</summary>
    private static (int Inside, int Boundary) SilenceThenRaise(string who, Settings table)
    {
        var sleep = table.Blocks.FirstOrDefault(b => b.IsSleep)
            ?? throw new Exception("这张表里没有标着睡眠的块");
        // 「块走完」那一刻：<see cref="Routine.Covers"/> 说止的那一刻已经归下一块了。
        var boundary = sleep.EndMinute;
        // 睡一小时之后 —— 跨午夜的那一块也成立，因为加法走的是一天。
        var inside = (sleep.StartMinute + Routine.MinutesPerHour) % Routine.MinutesPerDay;
        if (Routine.At(table.Blocks, inside)?.Name != sleep.Name)
            throw new Exception($"睡了一小时那一刻（{Routine.Clock(inside)}）生效的不是睡眠那一块 —— " +
                                $"这一发量的不是「在睡眠块里」");

        Settings Awake()
        {
            var copy = table.Clone();
            copy.Blocks.First(b => b.IsSleep).Kind = "";   // 摘掉那一个带行为语义的取值
            return copy;
        }

        // 这一段要**超过「这次休息走哪一条」指着的那条阈值**才该抬，而那个分钟数是
        // **切过去之后活的那一份上读出来的**（不抄默认值那一类数）—— 头一版照抄了自检里那份
        // 判据的 70 秒，而模板把阈值设成 15 分钟，于是对照那一发压根抬不起来
        // （`CODING_STANDARDS.md` 第 5 条：实测结果说「不通」先怀疑自己的测量程序）。
        //
        // 2026-10-21 所有者定夺之后，触发按**现实时间**走 —— 所以这一段量的还是那个分钟数，
        // 只是它现在数的是「这一段走了多久」，跟「连续输入」再没有关联。
        var busyMs = (long)table.ActivityTimeoutMin * Routine.MinutesPerHour * 1_000L + 1_000L;

        // 之一：睡眠那一块里，这一段走了很久也不抬。
        var quietInput = new FakeInput(0);
        quietInput.Type(busyMs);
        quietInput.Advance(1_500);
        var quiet = new Scheduler();
        quiet.Step(quietInput.Now, Detected.Fake(quietInput), table, inside);
        if (quiet.SnapshotOf(table).Phase != Scheduler.Phase.Hidden)
            throw new Exception($"在睡眠那一块里（{Routine.Clock(inside)}）这一段走了 " +
                                $"{table.ActivityTimeoutMin} 分钟，遮罩还是起来了");

        // **对照（一定该通）**：同一张表、同一串输入，种类摘掉就抬。
        var loudInput = new FakeInput(0);
        loudInput.Type(busyMs);
        loudInput.Advance(1_500);
        var loud = new Scheduler();
        var awake = Awake();
        loud.Step(loudInput.Now, Detected.Fake(loudInput), awake, inside);
        if (loud.SnapshotOf(awake).Phase != Scheduler.Phase.Shown)
            throw new Exception("对照不成立：种类摘掉之后同一发也不抬 —— 量的是别的");

        // 之二：块走完那一刻抬一次。
        var edge = new Scheduler();
        var edgeInput = new FakeInput(0);
        edge.Step(0, Detected.Fake(edgeInput), table, (boundary + Routine.MinutesPerDay - 1) % Routine.MinutesPerDay);
        if (edge.SnapshotOf(table).Phase != Scheduler.Phase.Hidden)
            throw new Exception($"还没到边界（{Routine.Clock(boundary)}）就先抬了");
        edge.Step(1_000, Detected.Fake(edgeInput), table, boundary);
        var edgeSnap = edge.SnapshotOf(table);
        if (edgeSnap.Phase != Scheduler.Phase.Shown)
            throw new Exception($"块走完那一刻（{Routine.Clock(boundary)}）没有抬");
        if (edgeSnap.BlockRaise?.Name != Routine.At(table.Blocks, boundary)?.Name)
            throw new Exception($"抬起的是「{edgeSnap.BlockRaise?.Name}」，那一刻生效的是" +
                                $"「{Routine.At(table.Blocks, boundary)?.Name}」");
        return (inside, boundary);
    }

    // ---- 小工具 ------------------------------------------------------------------

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>点分键名去掉组前缀（<c>routine.blocks</c> → <c>blocks</c>）——
    /// 块行和步行在文档里是**裸叶子名**，带前缀的那一份只活在声明表里。</summary>
    private static string Leaf(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];

    private static string ReadOf(PresetLifeModes.Preset preset)
    {
        using var file = Godot.FileAccess.Open(preset.Path, Godot.FileAccess.ModeFlags.Read);
        return file?.GetAsText() ?? "";
    }

    /// <summary>文件顶上那一段连续的 <c>//</c>（空行断开之前）。和
    /// <see cref="PresetLifeModes.Provenance"/> 读的是同一段，但**取全部**而不是第一句。</summary>
    private static string Head(string text)
    {
        var taken = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0 && taken.Length > 0) break;
            if (raw.TrimStart().StartsWith('{')) break;
            if (raw.TrimStart().StartsWith("//")) taken.Append(raw).Append('\n');
        }
        return taken.ToString();
    }

    /// <summary>某个键在原文里那一段（从它那一行到配平的那个括号）。
    /// 块表和休息流程表都是嵌套的，量「逐块写没写」只能看原文，解析之后那层信息就没了。</summary>
    private static string Section(string text, string leafKey)
    {
        var at = text.IndexOf($"\"{leafKey}\"", StringComparison.Ordinal);
        if (at < 0) return "";
        var depth = 0;
        for (var i = text.IndexOf('[', at) < 0 ? at : text.IndexOf('[', at); i < text.Length; i++)
        {
            if (text[i] == '[' || text[i] == '{') depth++;
            else if (text[i] == ']' || text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[at..(i + 1)];
            }
        }
        return text[at..];
    }

    private static int Count(string text, string needle) =>
        text.Split(needle).Length - 1;

    /// <summary>那一段里以某个键开头的行数（<b>数的是「写了几次」</b>，不是「有几行碰巧包含」）。</summary>
    private static int Lines(string section, string prefix) =>
        section.Split('\n').Count(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));

    private static string RowText(System.Text.Json.Nodes.JsonObject row, string field) =>
        row[field] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var said) ? said : "";

    /// <summary>每一步里写了字的：步自己（用来报错）+ 字（量重不重）。</summary>
    private static List<(System.Text.Json.Nodes.JsonObject Step, string Said)> Steps(
        System.Text.Json.Nodes.JsonObject doc, string textKey)
    {
        var found = new List<(System.Text.Json.Nodes.JsonObject, string)>();
        if (doc[Settings.ModesKey] is not System.Text.Json.Nodes.JsonObject flows) return found;
        foreach (var flow in flows)
        {
            if (flow.Value?["steps"] is not System.Text.Json.Nodes.JsonArray steps) continue;
            foreach (var one in steps.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var said = RowText(one, textKey);
                if (said.Trim().Length > 0) found.Add((one, said));
            }
        }
        return found;
    }

    /// <summary>这一步的 content 里摆不摆某个成员。成员名**从声明表那份可选值问**，
    /// 不手抄 —— 将来 content 加了别名，这一条自动跟着走。</summary>
    private static bool StageShows(System.Text.Json.Nodes.JsonObject step, string member)
    {
        if (step["content"] is not System.Text.Json.Nodes.JsonArray content) return false;
        return content.OfType<System.Text.Json.Nodes.JsonValue>()
            .Any(v => v.TryGetValue<string>(out var said) && said == member);
    }

    /// <summary>时间块某个字段的**裸名**（摘掉 <c>blocks[].</c> 前缀）—— 从
    /// <see cref="Settings.BlockDeclarations"/> 问，问不到就当场说清楚是哪一个。
    ///
    /// <b>为什么要有这个函数</b>：块行在文档里是裸名，而声明表里的键带前缀。原来这几处
    /// 写死成 <c>"kind"</c> / <c>"name"</c>，而这一份文件顶上那句话说的正是「三处都不手抄」
    /// —— 写死的和那句话对不上。改一个键名或加一个块字段，那两行会**默默量错那一份**
    /// （<c>CODING_STANDARDS.md</c> 第 6 条：判据里存一份会腐烂的副本）。</summary>
    private static string BlockFieldName(string bare)
    {
        var key = TimeBlock.KeyPrefix + bare;
        if (!Settings.Declared(key))
            throw new Exception($"{TimeBlock.KeyPrefix}{bare} 不在 {nameof(Settings.BlockDeclarations)} 里 —— " +
                                "判据问的是声明表，而这一处问不到");
        return bare;
    }

    /// <summary>那个目录的逐字节实测结果：<b>每一个文件</b>（不过滤后缀 —— 往里写一个缓存文件
    /// 也得红），文件名 + 长度 + 内容。
    ///
    /// <b>这里必须自己列目录，不能拿 <c>gallery.Presets</c> 那一列当尺子</b>
    /// （2026-10-02 复核时改的）：<c>PresetLifeModes.Load()</c> 只收
    /// <c>LifeModeNumbers.Suffix</c>（<c>*.jsonc</c>）那一种，而那一列是**开跑之前**取的。
    /// 拿它当尺子有两个洞：往那个目录里写一个**别的后缀**的文件它压根看不见；跑的过程中
    /// **多出来一个 <c>*.jsonc</c>** 它也看不见（列表是旧的）。工单 05 的反向验证 R1 已经
    /// 撞上过一次同一个形状的漏 —— 那一轮 <c>Presets.SelfTest</c> 自己那份尺子只数
    /// <c>*.jsonc</c>，程序写进去一个 <c>cache</c> 文件而自检照样绿，当场改成
    /// <c>Directory.GetFiles(dir)</c> 不过滤。<b>而这里是同一件事的第二份，它没跟着改</b> ——
    /// 同一件事在两个文件里各写一遍，多出来的那一份一定会过期，而过期得没有声音。
    /// 所以这一处与 <see cref="Presets"/> 那一处**读同一个形状**：卡片 05 · S14 起两处都读
    /// <see cref="DirBytes.Shot"/>，形状只有一份；这里只留「目录不在」那一档的说法。</summary>
    private static string Shot(PresetLifeModes.Gallery gallery)
    {
        var dir = Godot.ProjectSettings.GlobalizePath(PresetLifeModes.Dir);
        if (!Directory.Exists(dir)) return gallery.Missing ? "目录不在" : $"目录不在但 Missing={gallery.Missing}";
        return DirBytes.Shot(dir);
    }
}
