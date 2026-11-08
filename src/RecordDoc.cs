using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keeper;

/// <summary>记录那一族的落盘纪律：<b>宽容读、读不通立旗、第一次往读不通的那份上写之前先留
/// 一份 <c>.bak</c></b>。为什么是这三条，实测结果级的理由和被否掉的路在
/// <c>docs/adr/0002-settings-and-records.md</c>「记录借 JSONC 的读，不借它的写」那一节；
/// 这一份只管实施它的那一半。
///
/// <b>为什么是四份</b>：<see cref="History"/> / <see cref="Todos"/> / <see cref="CalendarNotes"/>
/// 是「记录」那三份（<c>GLOSSARY.md</c>「记录」条）；<see cref="LifeModeIndex"/> 是生活模式那份<b>名单</b>，
/// 它「当记录那一族对待」（那句话在 <see cref="LifeModeIndex"/> 自己的类型注释里，ADR-0002 那一节
/// 记的是同一件事：记录借 JSONC 的读、不借它的写）。
/// 四处的根节点类型、缩进、编码、末尾换行、首启种子、「文件不在」那一档各不一样，
/// 所以<b>形状各留各的</b>；收进来的只有那一半「不许默默抹掉一份我们没读懂的文件」。
///
/// <b>那一半为什么值得收</b>：它坏掉的症状是「一次读不通 + 一次保存 = 记录没了」，
/// 而那正是 ADR-0002 记下来要堵的那一发。抄四份的代价是：改这条协议要动四处，
/// 而<b>忘掉一处的症状和没改一样</b> —— 界面上看不出来，只在真出事那天才知道。
///
/// <b>读的那一半不收</b>：四处「怎么把一份文件读成自己」差得太远（根节点是对象还是数组、
/// 读回来还要不要对一遍号、键要不要筛），硬压成一种签名就得给每一份递一个它不读的
/// <c>host</c> —— 那是把接口做宽，不是做深（同 <c>SelfCheck</c> 类型注释里那一句）。
///
/// <b>留底失败照旧吞掉</b>（<see cref="WriteTo"/> 里那半个 <c>catch (IOException)</c>）：
/// 那一版是纯重构，行为一个字节不变，所以这里原样留着。**它是已知的一处隐患** ——
/// 留底失败时原文件仍会被覆盖，而那正是这一族要防的那一发。用户确认这一版不动，
/// 记在这儿是免得下一轮又把它当成新发现重新讨论一遍。
/// </summary>
public abstract class RecordDoc
{
    /// <summary>留底那一份的后缀。<b>只有这一处</b>：自检的前身是四份各自手写的 <c>path + ".bak"</c>，
    /// 连自检里那几条判据也各写了一遍这个字面量（15 处）。收拢时连判据一起换成这一个常量 ——
    /// 判据量的是「有没有留底」，量的却是一份**手抄的后缀名**，那正是 CODING_STANDARDS 第 6 条
    /// 说的「判据里存了一份会腐烂的副本」。</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>上一次读取读到了一份<b>存在但讲不通</b>的文件。调用方要把它说给人听，
    /// 别让人以为「没有记录」是实话（同 ADR-0002「读不通是一个状态，不是崩溃」）。
    ///
    /// <b>不落盘</b>：它是这一次读取的<b>状态</b>，不是记录里的数据。所以
    /// <c>[JsonIgnore]</c> 声明在这个基类上 —— 「它不许被序列化」成了<b>定义</b>，
    /// 而不是每份记得加一次。抄四份的时候只有 <see cref="History"/> 和 <see cref="LifeModeIndex"/>
    /// 记得加，<see cref="Todos"/> 和 <see cref="CalendarNotes"/> 靠「根节点不是自己」
    /// 才侥幸没把它写出去 —— 那是运气，不是规矩。</summary>
    [JsonIgnore]
    public bool Unreadable { get; private set; }

    /// <summary>写出去的字节一律是<b>无 BOM 的 UTF-8</b>，而且<b>这一句现在管着四份</b> ——
    /// 编码是这一族的共同纪律，不再是每份自己挑的。
    ///
    /// 自检的前身有三份各写一份私有的 <c>UTF8Encoding(false)</c>，而 <see cref="History"/> 用的是
    /// <c>File.WriteAllText(path, contents)</c> 那个<b>隐式</b>默认。收拢时 <see cref="History"/>
    /// 那一条也改成了显式 —— <b>那一改是量过的</b>：同一个字串走隐式和走显式
    /// <c>UTF8Encoding(false)</c>，写出来逐字节相同（无 BOM），量的时候台面是 .NET 10；
    /// 项目是 <c>net8.0</c>，那边靠的是同一份文档契约（.NET Core 2.1 起
    /// <c>File.WriteAllText</c> 的默认就是无 BOM 的 UTF-8）—— <b>那半句是推的，不是量的</b>
    /// （CODING_STANDARDS 第 12 条：推的和量的要分开写）。
    ///
    /// 从今往后「没有 BOM」由自检里 <c>record-doc</c> 那一组钉着，而它量的是 <c>Sample</c>、
    /// 不量 <see cref="History"/> —— <b>之所以仍然管得住，正是因为编码搬进了这一行</b>：
    /// 四份走的都是这同一个 <c>File.WriteAllText(path, bytes, Utf8NoBom)</c>，换一份形状
    /// 也换不掉这一行。换目标框架那天它会先响。</summary>
    protected static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>还一份空的、并把旗立起来。<b>读不通不是崩溃</b>，所以这里是「还一份空的」，
    /// 不是抛 —— 空对象和坏文件在界面上长得一模一样，那面旗是这一族唯一的一处「不要凭印象」。</summary>
    protected static T Flagged<T>() where T : RecordDoc, new()
    {
        var doc = new T();
        doc.Unreadable = true;
        return doc;
    }

    /// <summary>读那一侧的共同形状：调用方只交「怎么把一份文件读成 T」那几行，
    /// <b>读不通的那一半在这里</b>（<c>catch</c> 住一切、还一份空的、把旗立起来）。
    ///
    /// <b>为什么 catch 住一切</b>：读不通有好几种（截断、键名不对、根节点类型不对、
    /// 文件不在、权限不够），对这一族来说它们的<b>处置是同一句</b> —— 一条都没有 + 说清楚。
    /// 分类是调用方的事，兜底是这一份的事。</summary>
    protected static T Tolerantly<T>(Func<T> read) where T : RecordDoc, new()
    {
        try { return read(); }
        catch (Exception) { return Flagged<T>(); }
    }

    /// <summary>把「借 JSONC 的读」那两旗盖到一份<b>调用方自己摆好的</b>选项上。
    ///
    /// 默认选项连 <c>//</c> 都不认（本机实测：<c>'/' is an invalid start of a value</c>），
    /// 少这两旗，人手加的一行注释就把整份记录读成空的 —— 而那看起来像「一直都没用过」。
    /// 只收这两旗，不收别的：缩进、转义器、键名策略四份各不相同，归它们自己。</summary>
    protected static JsonSerializerOptions Jsonc(JsonSerializerOptions shape)
    {
        shape.ReadCommentHandling = JsonCommentHandling.Skip;
        shape.AllowTrailingCommas = true;
        return shape;
    }

    /// <summary>写那一侧的共同形状，也就是这一族真正不许忘的那一半：
    /// <b>建目录 → 读不通就先留一份 <c>.bak</c> 并把旗放下来 → 写字节</b>。
    ///
    /// 每份记录只交它自己那份字节（<c>bytes</c>）；形状、编码、末尾换行、落笔前那次筛选
    /// 全归它自己，因为那几样四份确实不一样。
    ///
    /// 旗放下来的位置在 <c>File.Exists</c> 那个 <c>if</c> 里面，所以<b>文件不在时旗一直立着</b>。
    /// 四份现在都把「文件不在」和「读不通」分开了，而<b>分开的那个位置各不相同</b>：
    /// <see cref="LifeModeIndex"/> 在 <c>Load</c> 开头，<see cref="CalendarNotes"/> 和
    /// <see cref="Todos"/> 在 <c>Open</c> 里（它们各自还有首启种子那一档），
    /// <see cref="History"/> 也在 <c>Load</c> 开头（2026-10-01 补上的：那之前
    /// <c>File.ReadAllText</c> 的 <c>FileNotFoundException</c> 被一并兜住，于是首启那一次
    /// <c>App</c> 印了一句「读不通」而其实没坏 —— 那一条的实测结果记在 <see cref="History.Load"/> 上）。
    /// 那一档是四档各量的实测结果，不是一条共同纪律，所以纪律里不碰它。</summary>
    protected void WriteTo(string path, string bytes)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent != null) Directory.CreateDirectory(parent);
        // 先留底再写：只有「这份文件我们没读懂」时才需要，读得懂的本来就是我们从它读出来的。
        // 没有这一发，一次读不通加一次保存就是抹记录。留底失败照旧吞掉（类型注释写着为什么）。
        if (Unreadable && File.Exists(path))
        {
            try { File.Copy(path, path + BackupSuffix, overwrite: true); } catch (IOException) { }
            Unreadable = false;
        }
        File.WriteAllText(path, bytes, Utf8NoBom);
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Discipline = new(
        () => "记录落盘纪律通过：读不通立旗且第一次写前留原样 .bak、旗跟着放下来、读得通的不去盖它、" +
              "写出去没有 BOM、目录还不存在也照样写、旗不落盘",
        "记录落盘纪律", DisciplineSelfTest);

    /// <summary>自检里的判据（组名 <c>record-doc</c>）：这一族那半共同的纪律<b>量一次</b>。
    /// 四份各自那几组量的是「各自接上了没有」和「各自的形状」—— 接线那一半不许省，
    /// 所以那四组都留着；这一组量的则是<b>纪律本身</b>，它从前散在四处、每处只量自己那一处。
    ///
    /// <b>它不叫 <c>SelfTest</c></b>：那一族每一份都有自己的 <c>SelfTest</c>（量各自的形状），
    /// 基类上再挂一个同名的，只会让四份各多一条「隐藏继承的成员」警告，而那条警告说的
    /// 恰恰是真问题——共享纪律和一份记录的形状不是同一件事，不该用同一个动词。
    ///
    /// 它用一份 <see cref="Sample"/> 当测量程序，量的是那一族共同的几个动作。
    /// 「文件不在」那一档<b>不</b>在这一组里：四份都分开，但<b>分开的那个位置各不相同</b>
    /// （<see cref="LifeModeIndex"/> 和 <see cref="History"/> 在 <c>Load</c> 开头，
    /// <see cref="CalendarNotes"/> 和 <see cref="Todos"/> 在 <c>Open</c> 里），
    /// 那是一条一档一档量的实测结果，不是一条共同纪律。</summary>
    public static List<string> DisciplineSelfTest()
    {
        var checks = new Checks("recorddoc");
        var root = checks.Root!;

        checks.Check("读不通的立旗，第一次写回去之前先留 .bak，旗跟着放下来", () =>
        {
            var path = Path.Combine(root, "broken.jsonc");
            var broken = "{ \"items\": [ \"半截\", ";
            File.WriteAllText(path, broken);

            var got = Sample.ReadIt(path);
            if (!got.Unreadable) throw new Exception("截断的一份读不通却没立旗");
            if (got.Items.Count != 0) throw new Exception("读不通却读出了东西");
            if (File.ReadAllText(path) != broken) throw new Exception("读的一趟动了文件");

            got.Items.Add("新的一条");
            got.WriteIt(path);
            if (!File.Exists(path + BackupSuffix)) throw new Exception("第一次写回读不通那份之前没留 .bak");
            if (File.ReadAllText(path + BackupSuffix) != broken) throw new Exception("留下的那份不是原样");
            if (got.Unreadable) throw new Exception("留过底、写过之后旗该放下来");
            if (Sample.ReadIt(path).Items.Count != 1) throw new Exception("写回去的那份没把自己读回来");
        });

        checks.Check("读得通的那份不去盖 .bak", () =>
        {
            var path = Path.Combine(root, "readable.jsonc");
            var sample = new Sample { Items = ["一"] };
            sample.WriteIt(path);

            // 先造一个「读不通 → 留底」，让 .bak 有一个被写出来的时间戳；
            // 然后读得通地再写两发，量的是那两发有没有去盖它。
            File.WriteAllText(path, "{ 坏的");
            Sample.ReadIt(path).WriteIt(path);
            var stamp = File.GetLastWriteTimeUtc(path + BackupSuffix);
            if (stamp == default) throw new Exception("前一发没有留下 .bak，后一条就量不到");

            for (var i = 0; i < 2; i++)
            {
                var next = Sample.ReadIt(path);
                next.Items.Add($"第 {i + 2} 条");
                next.WriteIt(path);
            }
            // 比内容不比条数：比条数的话，「写出去的是空记录」和「一条都没写」是同一个数。
            // 留底那一发写的是空记录（读不通嘛），所以这里头是「第 2 条」「第 3 条」。
            var back = Sample.ReadIt(path).Items;
            if (back.Count != 2 || back[0] != "第 2 条" || back[1] != "第 3 条")
                throw new Exception($"两发读得通的写没都落下去，读回来是 [{string.Join(",", back)}]");
            if (File.GetLastWriteTimeUtc(path + BackupSuffix) != stamp)
                throw new Exception("读得通的一份也去盖了 .bak");
        });

        checks.Check("写出去的那一份没有 BOM", () =>
        {
            // 自检里那几条「写出去的形状」是拿 File.ReadAllText 对着比 —— 而 ReadAllText
            // 默认把 BOM 吃掉，所以**对着读回来的字比不出 BOM**。这一条比的是字节。
            var path = Path.Combine(root, "bom.jsonc");
            new Sample { Items = ["中文"] }.WriteIt(path);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                throw new Exception("写出去的那一份前面有 BOM");
            if (bytes.Length == 0) throw new Exception("一个字节都没写出去");
        });

        checks.Check("目录还不存在时也照样写", () =>
        {
            var path = Path.Combine(root, "not", "yet", "there.jsonc");
            var sample = new Sample { Items = ["一"] };
            sample.WriteIt(path);
            if (!File.Exists(path)) throw new Exception("父目录不存在，写出去的那份不在");
            if (Sample.ReadIt(path).Items.Count != 1) throw new Exception("读不回来");
        });

        checks.Check("旗不落盘", () =>
        {
            var path = Path.Combine(root, "flag.jsonc");
            new Sample { Items = ["一"] }.WriteIt(path);
            if (File.ReadAllText(path).Contains("unreadable", StringComparison.OrdinalIgnoreCase))
                throw new Exception("写出去的那一份把「读不通」这个状态也写进去了 —— 它不落盘");
        });

        return checks.Report();
    }

    /// <summary>测量程序：一份最小的记录，长得和 <see cref="History"/> 同一族（根节点是<b>对象</b>、
    /// 借 JSONC 的读、整体重写），而它自己只交「读成什么」和「写出哪几个字」。
    ///
    /// 读写那两处必须是<b>同一个根节点类型</b> —— 写对象、实测结果组的话，读回来的是一个
    /// 「读不通」（被 <c>Tolerantly</c> 兜住，于是旗是立着的、看着还挺像在工作）。
    /// 那不是假绿：旗立着的时候「立了旗」那几条照样过，只有数条目的那几条才报。</summary>
    private sealed class Sample : RecordDoc
    {
        public List<string> Items { get; set; } = [];

        private static readonly JsonSerializerOptions Shape = Jsonc(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        public static Sample ReadIt(string path) =>
            Tolerantly(() => new Sample
            {
                Items = JsonSerializer.Deserialize<Sample>(File.ReadAllText(path), Shape)?.Items ?? [],
            });

        public void WriteIt(string path) => WriteTo(path, JsonSerializer.Serialize(this, Shape));
    }
}
