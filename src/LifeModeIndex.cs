using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Keeper;

/// <summary>生活模式那份<b>名单（索引）</b>：名字 → 那个文件叫什么。
///
/// 它和「生活模式那一份文档」是<b>两个东西</b>，别混成一个类：那份文档是一份配置（<c>Settings</c>
/// 那一族，格式和「导出」完全一样），这一份只是一张对照表，形状是
/// <c>{"nextNumber":2,"entries":[{"name":"写作","file":"0001.jsonc"}]}</c>。
///
/// <b>它当记录那一族对待</b>（取向和代价见 <c>docs/adr/0002-settings-and-records.md</c>
/// 「记录借 JSONC 的读，不借它的写」那一节）：
/// <list type="bullet">
/// <item>读的时候借 JSONC 的容错 —— <c>//</c>、<c>/* */</c> 和尾逗号都认（那两旗在
/// <see cref="Shape"/> 里，和 <see cref="History"/> / <see cref="Todos"/> 同一族）。</item>
/// <item>写的时候程序整体重写：注释不承诺活得过下一次保存。</item>
/// <item>第一次往<b>读不通</b>的那份上写之前先留一份 <c>.bak</c>。</item>
/// </list>
///
/// <b>读不通是一个状态，不是崩溃</b>（同 <see cref="History.Unreadable"/>）：<see cref="Unreadable"/>
/// 立起来，调用方把这一族当成没有，而 <b>不许覆盖磁盘上那份</b>（设计文档 `docs/design/life-mode.md`「读不通有三档」那条
/// 第一档：名单读不通 → 生活模式这一族当成没有，并且不覆盖磁盘上那份）。
/// 它<b>不落盘</b>：少这一旗，下一次 <see cref="Save"/> 会往用户的名单里写出一个
/// <c>"unreadable":false</c>。</summary>
public sealed class LifeModeIndex : RecordDoc
{
    /// 生活模式那几份住在这个子目录里。分开住是为了别和 <c>settings.jsonc</c> 混在一堆 ——
    /// 那一堆是「程序现在用的」，这个子目录是「几份快照」，用户翻目录时不该分不清。
    ///
    /// 老版本把它叫 <c>flows</c>，第一次启动逐字节改名成现在这个（<see cref="Records.DirPairs"/>
    /// 那一条，和 <see cref="Records.RenameLegacy"/> 同一趟）。
    public const string FolderName = "lifemodes";

    /// 老名字。<b>只有改名那一趟读它</b>，别处一律问 <see cref="FolderName"/>。
    public const string LegacyFolderName = "modes";

    /// 名单那一份的文件名。它不是生活模式那一份：生活模式那一份是 <c>0001.jsonc</c> 那种带号的名字
    /// （<see cref="LifeModeNumbers"/>），这份是固定的一个。
    public const string FileName = "list.jsonc";

    /// 一条：名字，和它装在哪一份文件里。<b>文件是一个相对名</b>，不是整条路径 ——
    /// 换一台机器、换目录位置时它照样能用（绝对路径存进文档就成了一份迁不走的配置）。
    public sealed record Entry(string Name, string File);

    public List<Entry> Entries { get; set; } = [];

    /// <summary>下一个该发的号。<b>删掉一条不往回收</b>：这个数只增不减，所以「删掉 0003
    /// 之后再建一个」拿到的是比 3 更大的号，而不会是 3 顶上那份已经被别的名字占过。
    ///
    /// 它是**记下来的那份号的上界**，不是「现有条目里最大的号 + 1」——
    /// 后面那一种在删掉最大的那一条之后会把号发回去，而那正是契约说的「永不复用」要挡的事。</summary>
    public int NextNumber { get; set; } = LifeModeNumbers.First;

    /// <b>「读不通」的那一面住在 <see cref="RecordDoc"/> 上</b>（<see cref="RecordDoc.Unreadable"/>）——
    /// 名单在这一族里的地位是「当记录那一族对待」（见类型注释），所以纪律共用一份，
    /// 而它自己那份形状（根节点是对象、缩进、不转义中文与 emoji）留在这里。
    ///
    /// 读不到就是一份空名单：第一次跑本来就没有这个文件。
    /// <b>「文件不在」和「文件读不通」是两档</b>，不能混：混了的话第一次存生活模式就会被
    /// 「名单读不通」挡住，而这个程序从来还没有那份名单。
    /// 认了注释和尾逗号 —— 少了这两旗，人手加一行注释会把整份名单读成空的，
    /// 而那看起来像「一个生活模式都没有」。
    public static LifeModeIndex Load(string path)
    {
        if (!File.Exists(path)) return new LifeModeIndex();
        return Tolerantly(() =>
        {
            var got = JsonSerializer.Deserialize<LifeModeIndex>(File.ReadAllText(path), Shape) ?? new LifeModeIndex();
            // 名单是手改得动的，所以「记着的号」不认账时以文件里那些为准：宁可跳号，
            // 也不发一个已经被占出去的号。**只往大里推**（永不复用）。
            foreach (var entry in got.Entries)
                if (LifeModeNumbers.TryNumberIn(entry.File, out var used)) got.RaiseTo(used);
            if (got.NextNumber < LifeModeNumbers.First) got.NextNumber = LifeModeNumbers.First;
            return got;
        });
    }

    /// 整体重写。和 <see cref="History.Save"/> 同一族：读得懂的那份就是从它读出来的，
    /// 所以只有「这份我们没读懂」时才需要先留 <c>.bak</c> —— 没有这一发，
    /// 一次读不通加一次保存就是抹掉一份用户自己写的名单。那一半在
    /// <see cref="RecordDoc.WriteTo"/> 上。
    public void Save(string path) => WriteTo(path, JsonSerializer.Serialize(this, Shape));

    public Entry? Find(string name) => Entries.FirstOrDefault(e => e.Name == name);

    public bool Contains(string name) => Find(name) != null;

    /// <summary>加一条并发号。<b>调用方要先判重</b>（<see cref="LifeModeStore.SaveAs"/> 那一侧）——
    /// 这一发只管发号，重名是「名单里一个名字只有一个文件」那条，判在能说清「撞上谁了」的地方。</summary>
    public Entry Add(string name)
    {
        var entry = new Entry(name, LifeModeNumbers.FileNameOf(NextNumber));
        NextNumber++;
        Entries.Add(entry);
        return entry;
    }

    public bool Remove(string name)
    {
        var entry = Find(name);
        // 号码不回收：NextNumber 留着，于是下一个拿更大的号（见类型注释）。
        return entry != null && Entries.Remove(entry);
    }

    /// <summary>「已经用过的最大号不许再发一遍」这条规矩的<b>唯一一处实施</b>：
    /// 传进来的 <paramref name="floor"/> 是「磁盘上已经有的最大号」，它只把
    /// <see cref="NextNumber"/> 往大里推，永不往小里拉。
    ///
    /// 为什么磁盘上那些也要算：名单是人手改得动的，也可能被人删掉而生活模式那几份还在 ——
    /// 只看名单的话，删掉名单之后新生活模式会从 1 重新数起，<b>盖掉</b>磁盘上还在的那一份
    /// （「档案里有、桌面上没有」那对假象的另一种写法）。号从两份里更大的那个后面接着数，
    /// 就撞不上。</summary>
    public void RaiseTo(int floor)
    {
        if (floor + 1 > NextNumber) NextNumber = floor + 1;
    }

    /// 目录里现存的那些号，大的那个。<b>认不出的文件名不当成号</b>（<c>list.jsonc</c> 自己、
    /// 用户自己放进去的别的文件），所以这一发问的是「有没有编号过的生活模式那一份」，
    /// 不是「目录里有几个文件」。
    public int HighestNumberOnDisk(string folder)
    {
        var top = LifeModeNumbers.First - 1;
        if (!Directory.Exists(folder)) return top;
        foreach (var file in Directory.EnumerateFiles(folder))
            if (LifeModeNumbers.TryNumberIn(Path.GetFileName(file), out var used) && used > top) top = used;
        return top;
    }

    // 编码那一行是「这是给人看的名单」：默认选项会把中文和 emoji 写成 \uXXXX。
    // 「借 JSONC 的读」那两旗由 RecordDoc.Jsonc 盖上来（和 History / Todos / CalendarNotes 同一份）。
    private static readonly JsonSerializerOptions Shape = Jsonc(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    // ---- 自检里的判据（组名 flows）------------------------------------------------
    //
    // 整份跑在 .tmp 里自己那个用完就删的子目录，一条都不碰 %APPDATA%。
    // 「切换」那一族在 LifeModeStore.SwitchSelfTest，那是另一组。

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Index = new(
        () => "生活模式名单通过：名字（中文 / emoji / 标点 / 满 80 个字符）→ 文件名 → 名字逐字回来、" +
              "号永不复用（删掉最大的那一条也不复用）、目录里剩下的那几份也算数、" +
              "读不通立旗且写前先留 .bak、写出去再读回来再写出去逐字节相同",
        "生活生活模式名单", SelfTest);

    public static List<string> SelfTest()
    {
        var checks = new Checks("lifemodeindex");
        var root = checks.Root!;

        checks.Check("名字 → 文件名 → 名字一个来回：中文、emoji、标点、空格、满 80 个字符", () =>
        {
            var dir = Path.Combine(root, "roundtrip");
            Directory.CreateDirectory(dir);
            var names = new[]
            {
                "写作", "写代码 · 深度", "😀🎯 emoji", "a/b\\c:d*e?f\"g<h>i|j", " 前后有空格  ",
                new string('长', LifeModeStore.MaxNameLength),
            };
            var index = new LifeModeIndex();
            var files = new List<string>();
            foreach (var name in names) files.Add(index.Add(name).File);

            var list = Path.Combine(dir, LifeModeIndex.FileName);
            index.Save(list);
            var back = LifeModeIndex.Load(list);
            if (back.Unreadable) throw new Exception("刚写出去的一份读不通");
            if (back.Entries.Count != names.Length)
                throw new Exception($"读回来 {back.Entries.Count} 条，该是 {names.Length} 条");
            var text = File.ReadAllText(list);

            // 名字**逐字**回来，而且文件是发出来的那一个 —— 名字不进路径，所以路径上那一串
            // 字里一个都不该是名字里的（emoji 那种带代理对的也一样）。
            for (int i = 0; i < names.Length; i++)
            {
                var got = back.Find(names[i])?.Name;
                if (got != names[i])
                    throw new Exception($"第 {i + 1} 条名字回来成了 '{got}'，该是 '{names[i]}'");
                var entry = back.Entries[i];
                if (entry.File != files[i])
                    throw new Exception($"'{names[i]}' 指的文件是 {entry.File}，该是 {files[i]}");
                if (!LifeModeNumbers.TryNumberIn(entry.File, out var number) || number != i + LifeModeNumbers.First)
                    throw new Exception($"{entry.File} 读不回成第 {i + 1} 个号");
                // 名单是人手改得动的：中文和标点**原样**写着，不是 \uXXXX。
                // **emoji 那一档量不到**：实测 System.Text.Json 把代理对一律写成
                // \uD83D\uDE00，连 UnicodeRanges.All 的编码器也一样（2026-10-01 在
                // net8.0 上量的）—— 这不是编码器配错了，是它本来的写法。名字逐字回来
                // 靠的是上面那一条，与写出去长什么样无关。
                // 名单是人手改得动的：中文和标点**原样**写着，不是 \uXXXX。
                // 两个量不到的例外，都是 JSON 本身的规矩而不是这份代码的选择：
                // **emoji**（实测 System.Text.Json 把代理对一律写成 \uD83D\uDE00，
                // 连 UnicodeRanges.All 的编码器也一样 —— 2026-10-01 在 net8.0 上量的）
                // 和 `"` / `\`（JSON 里这两个字非转义不可）。名字逐字回来靠的是上面那一条，
                // 与写出去长什么样无关。
                var name = names[i];
                if (!char.IsSurrogate(name[0]) && !name.Contains('"') && !name.Contains('\\')
                    && !text.Contains(name, StringComparison.Ordinal))
                    throw new Exception($"名单文件里没有原样写着 '{name}'（被转义成 \\u 了？）");
            }
            // 同一个名字再来一次：名单里只该有一条，而发号照样往前。
            if (back.Contains(names[0]) && back.Entries.Count(e => e.Name == names[0]) != 1)
                throw new Exception("同一个名字在名单里不止一条");
        });

        checks.Check("号永不复用：删掉最大的那一条之后，新拿的号比它大", () =>
        {
            var index = new LifeModeIndex();
            var first = index.Add("一");
            var second = index.Add("二");
            var third = index.Add("三");
            var before = LifeModeNumbers.TryNumberIn(third.File, out var thirdNumber)
                ? thirdNumber : throw new Exception($"文件名 {third.File} 读不回成号");
            if (!index.Remove("三")) throw new Exception("删不掉那一条");
            var again = index.Add("又一个");
            if (!LifeModeNumbers.TryNumberIn(again.File, out var againNumber))
                throw new Exception($"再发的那一个 {again.File} 读不回成号");
            if (againNumber <= before)
                throw new Exception($"删掉 {third.File} 之后新发的号是 {againNumber}，不大于它的 {before} —— 号被复用了一次");
            if (index.Entries.Count != 3) throw new Exception($"名单该剩 3 条，实际 {index.Entries.Count} 条");
            // 名单记着的那一份也还在（手改名单把号改小的那种）。
            index.NextNumber = 1;
            index.RaiseTo(before);
            if (index.NextNumber <= before)
                throw new Exception($"RaiseTo({before}) 之后下一个该发的号是 {index.NextNumber}，不大于 {before}");
            if (second.File == first.File) throw new Exception("两条指同一个文件");
        });

        checks.Check("目录里还留着的那几份也算数：名单没了，新生活模式也不盖掉它们", () =>
        {
            var dir = Path.Combine(root, "orphan");
            Directory.CreateDirectory(dir);
            // 「用户删掉了名单，而生活模式那几份还在」：磁盘上有 0001 / 0002，名单那个文件不在。
            // （不在 ≠ 读不通：「文件不在」是一份空名单，见 Load —— 混了的话第一次存生活模式
            //  就会被「名单读不通」挡住，而这个程序从来还没有那份名单。）
            File.WriteAllText(Path.Combine(dir, LifeModeNumbers.FileNameOf(1)), "{}");
            File.WriteAllText(Path.Combine(dir, LifeModeNumbers.FileNameOf(2)), "{}");
            var index = LifeModeIndex.Load(Path.Combine(dir, LifeModeIndex.FileName));
            if (index.Unreadable) throw new Exception("名单不在却被当成读不通");
            if (index.Entries.Count != 0) throw new Exception("名单不在却读出了条目");
            index.RaiseTo(index.HighestNumberOnDisk(dir));
            if (index.NextNumber <= 2)
                throw new Exception($"磁盘上最大的是 2，新的号该比它大，实际发 {index.NextNumber}");
            var entry = index.Add("新的");
            if (File.Exists(Path.Combine(dir, entry.File)))
                throw new Exception($"新发的 {entry.File} 已经有一份在磁盘上了 —— 会盖掉一份真文档");
        });

        checks.Check("带注释和尾逗号的名单读得回来", () =>
        {
            var dir = Path.Combine(root, "annotated");
            Directory.CreateDirectory(dir);
            var list = Path.Combine(dir, LifeModeIndex.FileName);
            File.WriteAllText(list, """
                // 我自己加的说明
                {
                  /* 块注释也认 */
                  "entries": [ { "name": "喝水", "file": "0001.jsonc", }, ],
                }
                """);
            var got = LifeModeIndex.Load(list);
            if (got.Unreadable) throw new Exception("带注释的一份被当成读不通");
            if (got.Find("喝水")?.File != "0001.jsonc") throw new Exception("带注释的那份没读出那一条");
            // 同一份内容按默认的严格 JSON 选项读会失败：这一发证的正是那两旗在起作用。
            try
            {
                JsonSerializer.Deserialize<LifeModeIndex>(File.ReadAllText(list),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                throw new Exception("这条判据不会失败：不带那两旗也读得通，那它什么都没量");
            }
            catch (JsonException) { }
        });

        checks.Check("读不通的立旗，写回去之前先留 .bak；读得通的不去盖它", () =>
        {
            var dir = Path.Combine(root, "broken");
            Directory.CreateDirectory(dir);
            var list = Path.Combine(dir, LifeModeIndex.FileName);
            var broken = "{ \"entries\": [ { \"name\": \"半截\", ";
            File.WriteAllText(list, broken);

            var got = LifeModeIndex.Load(list);
            if (!got.Unreadable) throw new Exception("截断的一份读不通却没立旗");
            if (File.ReadAllText(list) != broken) throw new Exception("读的一趟动了文件");
            got.Add("新的");
            got.Save(list);
            if (!File.Exists(list + RecordDoc.BackupSuffix)) throw new Exception("第一次写回读不通那份之前没留 .bak");
            if (File.ReadAllText(list + RecordDoc.BackupSuffix) != broken) throw new Exception("留下的那份不是原样");
            if (LifeModeIndex.Load(list).Unreadable) throw new Exception("写回去的那一份自己读不通");
            if (File.ReadAllText(list).Contains("unreadable"))
                throw new Exception("写出去的那份把「读不通」这个状态也写进去了 —— 它不落盘");

            // 第二发不再留底：读得懂的那份本来就是从它读出来的。
            var again = File.GetLastWriteTimeUtc(list + RecordDoc.BackupSuffix);
            LifeModeIndex.Load(list).Save(list);
            if (File.GetLastWriteTimeUtc(list + RecordDoc.BackupSuffix) != again) throw new Exception("读得通的一份也去盖了 .bak");
        });

        checks.Check("写出去再读回来，再写出去：两份逐字节相同", () =>
        {
            var dir = Path.Combine(root, "stable");
            Directory.CreateDirectory(dir);
            var list = Path.Combine(dir, LifeModeIndex.FileName);
            var index = new LifeModeIndex();
            index.Add("一");
            index.Add("二");
            index.Save(list);
            var once = File.ReadAllBytes(list);
            LifeModeIndex.Load(list).Save(list);
            var twice = File.ReadAllBytes(list);
            if (!once.SequenceEqual(twice))
                throw new Exception($"读回来再写出去的那一份不一样：{once.Length} 字节 → {twice.Length} 字节");
        });

        return checks.Report();
    }
}
