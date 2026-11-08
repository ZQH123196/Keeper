using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Keeper;

/// <summary>休息记录。它是记录而不是配置：整体重写，不走 JSONC 那条原位编辑的规矩
/// （取向和代价见 <c>docs/adr/0002-settings-and-records.md</c>「记录借 JSONC 的读，不借它的写」那一节）。
///
/// 但格式仍然是 JSONC 那一种 —— 读的时候容忍 <c>//</c>、<c>/* */</c> 和尾逗号，所以人手加一行注释
/// 不会把整份记录读成空的。写出去的是紧凑 JSON（合法的 JSONC），注释不承诺活得过下一次保存。
///
/// 文件的形状：<c>{"records":[{"atUnixMs":…,"taken":…}]}</c>，紧凑、camelCase ——
/// 写它和读它的是同一份代码，形状就是这一条。</summary>
public sealed class History : RecordDoc
{
    public const string FileName = "history.jsonc";

    /// 老版那份的名字。首启逐字节改名成 <see cref="FileName"/>，见 <see cref="Records.RenameLegacy"/>。
    public const string LegacyFileName = "history.json";

    private const int MaxRecords = 500;

    public List<Record> Records { get; set; } = [];

    public sealed class Record
    {
        public long AtUnixMs { get; set; }
        public bool Taken { get; set; }
    }

    /// <b>「读不通」的那一面住在 <see cref="RecordDoc"/> 上</b>（<see cref="RecordDoc.Unreadable"/>、
    /// 读那一侧的 <c>Tolerantly</c>、写那一侧的 <c>WriteTo</c>），四份记录共用一份实施。
    /// 这一份只留自己那几样：根节点是对象、紧凑不缩进、不转义中文。
    ///
    /// <b>「文件不在」和「文件读不通」是两档</b>，和 <see cref="LifeModeIndex.Load"/> 一样要在
    /// 开头分开（同 <see cref="CalendarNotes.Open"/> / <see cref="Todos.Open"/> 那一族）：
    /// 第一次跑本来就没有这个文件，那不是坏掉。
    ///
    /// <b>不分开的那一版量到过（2026-10-01）</b>：<c>File.ReadAllText</c> 对不存在的文件抛
    /// <c>FileNotFoundException</c>，被 <see cref="RecordDoc.Tolerantly"/> 一并兜住，于是旗立起来
    /// —— 而首启那一次 <c>App</c> 会照着旗印一句「<c>history.jsonc</c> 读不通：休息记录一条都不显示，
    /// 第一次写回去之前先留一份 <c>.bak</c>」。没坏，也就没有可留的底，那一句是假话。
    /// 它的后一半还有实害：旗立着而文件还没写出来时，第一次 <see cref="Save"/> 因为
    /// <c>File.Exists</c> 为假跳过了留底、旗也不落，于是同一会话里第二次 <see cref="Save"/>
    /// 把我们自己刚写的那一份复制成了 <c>.bak</c> —— 一个用户从没要过的底稿。
    ///
    /// 读不到就是一份空记录，不是错误。
    /// 名字大小写不敏感这一条不是方便：文件里的键是 camelCase，而 C# 的属性是 PascalCase，
    /// 少了它整份记录会安静地读成空 —— 自检拿磁盘上那份真的文件当底稿，就是为了抓这个。
    ///
    /// <b>原来这里 catch 完静默还一份空的</b>，而下一次 <see cref="Save"/> 就把空记录写回磁盘 ——
    /// 「读不通」在那一条路上等于「记录被抹掉」，而且没有一处说过。现在它立
    /// <see cref="RecordDoc.Unreadable"/>。
    public static History Load(string path) =>
        File.Exists(path)
            ? Tolerantly(() => JsonSerializer.Deserialize<History>(File.ReadAllText(path), Shape) ?? new History())
            : new History();

    public void Push(Record record)
    {
        Records.Add(record);
        if (Records.Count > MaxRecords) Records.RemoveRange(0, Records.Count - MaxRecords);
    }

    /// 健康指数：提示过的休息里，用户真休息了多少。一条记录都没有的时候是 100，
    /// 因为「还没被问过」不是「一次都没休息」。
    /// <para>2026-10-06：界面上不再显示它（遮罩那一行和设置窗窗头那一行都撤了），
    /// 这一份留着 —— 它是记录的一个读法，底下写记录的逻辑和状态机自检都还在用它。
    /// 「界面不许有」是界面那一侧的事，不该顺手把记录侧算法一起删掉。</para>
    public int HealthPercent() => HealthOf(Records.Count(r => r.Taken), Records.Count);

    /// <summary>健康指数那一份算法，<b>正本住这里</b>。<paramref name="taken"/> 是休息过的次数，
    /// <paramref name="prompted"/> 是提示过的次数。
    ///
    /// <para>**为什么抽出来**：一天那一行的健康度（<see cref="HistoryDays"/>）要的是同一个算法，
    /// 而同一个概念有两份算法时，改一处漂一次 —— 症状是「界面上那个数和产品里那个数不一样」，
    /// 而那种不一致在界面上看不出是哪一边错了。抽出来之后两处调的是这一段算术，
    /// 界面上不一致的那一种就不可能发生（不是「小心别写错」，是写不出来）。</para>
    ///
    /// <para>一条记录都没有时是 <b>100</b>，因为「还没被问过」不是「一次都没休息」
    /// （<see cref="HealthPercent"/> 那段记着为什么）。<paramref name="prompted"/> 为 0 走同一档。</para>
    public static int HealthOf(int taken, int prompted) =>
        prompted == 0 ? 100 : (int)Math.Round(100.0 * taken / prompted);

    /// 整体重写。「先留底再写」那一半在 <see cref="RecordDoc.WriteTo"/> 上，
    /// 这一份只交它写出去的那几个字。
    public void Save(string path) => WriteTo(path, JsonSerializer.Serialize(this, Shape));

    private static readonly JsonSerializerOptions Shape = Jsonc(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    });

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Rest = new(
        () => "休息记录通过：带注释和尾逗号读得回来、读不通的立旗且写前先留 .bak、写出去还是紧凑那一份、" +
              "文件不在是没用过而不是读不通（也不凭空留 .bak）",
        "休息记录", SelfTest);

    /// 自检里的判据（组名 <c>history</c>）。每条都两头夹：该读回来的读回来，真坏的要说出来且不抹。
    public static List<string> SelfTest()
    {
        var checks = new Checks("history");
        var root = checks.Root!;

        checks.Check("带注释和尾逗号的记录读得回来", () =>
        {
            var path = Path.Combine(root, "annotated.jsonc");
            File.WriteAllText(path, """
                // 我自己的说明
                {
                  /* 块注释也认 */
                  "records": [ { "atUnixMs": 1, "taken": true }, { "atUnixMs": 2, "taken": false }, ]
                }
                """);
            var got = History.Load(path);
            if (got.Unreadable) throw new Exception("带注释的一份被当成读不通");
            if (got.Records.Count != 2) throw new Exception($"读回来 {got.Records.Count} 条，该是 2 条");
            if (!got.Records[0].Taken || got.Records[1].Taken) throw new Exception("taken 读错了");
            // 同一份内容按默认的严格 JSON 选项读会失败：这一发证的正是那两旗在起作用。
            try
            {
                JsonSerializer.Deserialize<History>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                throw new Exception("这条判据不会失败：不带那两旗也读得通，那它什么都没量");
            }
            catch (JsonException) { }
        });

        checks.Check("读不通的立旗，第一次写回去之前先留 .bak", () =>
        {
            var path = Path.Combine(root, "broken.jsonc");
            var broken = "{ \"records\": [ { \"atUnixMs\": 1, \"taken\": ";
            File.WriteAllText(path, broken);
            var got = History.Load(path);
            if (!got.Unreadable) throw new Exception("截断的一份读不通却没立旗");
            if (File.ReadAllText(path) != broken) throw new Exception("读的一趟动了文件");
            got.Push(new History.Record { AtUnixMs = 7, Taken = true });
            got.Save(path);
            if (!File.Exists(path + RecordDoc.BackupSuffix)) throw new Exception("第一次写回读不通那份之前没留 .bak");
            if (File.ReadAllText(path + RecordDoc.BackupSuffix) != broken) throw new Exception("留下的那份不是原样");
            if (History.Load(path).Records.Count != 1) throw new Exception("写回去的那份没把自己读回来");
            // 第二发不再留底：读得懂的本来就是我们从它读出来的。
            var again = File.GetLastWriteTimeUtc(path + RecordDoc.BackupSuffix);
            var next = History.Load(path);
            next.Push(new History.Record { AtUnixMs = 8, Taken = false });
            next.Save(path);
            if (File.GetLastWriteTimeUtc(path + RecordDoc.BackupSuffix) != again) throw new Exception("读得通的一份也去盖了 .bak");
        });

        checks.Check("文件不在是「没用过」，不是「读不通」：旗不立，也不留一个没人要的 .bak", () =>
        {
            // 这一条是 2026-10-01 反向验证时量出来的实测结果：立了旗之后，首启那一次 App 会照着
            // 旗印一句「读不通…先留 .bak」，而首启本来就没有那个文件。实测的后一半更实：
            // 旗立着 → 第一次 Save 时 File.Exists 为假，跳过留底、旗也不落 → 同一会话里
            // 第二次 Save 把自己刚写的那份复制成了 .bak。两条都在下面量。
            var path = Path.Combine(root, "never-existed.jsonc");
            if (File.Exists(path)) throw new Exception("测量程序前提不成立：那个文件居然在");

            var fresh = History.Load(path);
            if (fresh.Unreadable) throw new Exception("文件不存在被当成了读不通");
            if (File.Exists(path + RecordDoc.BackupSuffix)) throw new Exception("读一个不存在的文件不该留下底稿");

            // 接着把那一串两发写走，看会不会凭空多出一个 .bak。
            fresh.Push(new History.Record { AtUnixMs = 1, Taken = true });
            fresh.Save(path);
            if (File.Exists(path + RecordDoc.BackupSuffix)) throw new Exception("第一次写就留下了 .bak —— 那一份是程序自己刚写出来的");
            // **同一个对象 push 完再存**。写成 History.Load(path).Push(...) 换一行
            // History.Load(path).Save(...) 的话那是两个对象，push 的那条被丢掉了，
            // 而这一发还在绿 —— 那种绿量的是别的东西。
            var next = History.Load(path);
            next.Push(new History.Record { AtUnixMs = 2, Taken = false });
            next.Save(path);
            if (File.Exists(path + RecordDoc.BackupSuffix))
                throw new Exception("读得通的两发写下去，凭空多了一个 .bak");
            if (History.Load(path).Records.Count != 2)
                throw new Exception("两发写下去只读到一条 —— push 的那条没存进去");
        });

        checks.Check("写出去还是那个形状", () =>
        {
            var path = Path.Combine(root, "shape.jsonc");
            var h = new History();
            h.Push(new History.Record { AtUnixMs = 1700000000000, Taken = true });
            h.Save(path);
            var text = File.ReadAllText(path);
            if (text != """{"records":[{"atUnixMs":1700000000000,"taken":true}]}""")
                throw new Exception($"写出去的形状变了：{text}");
            if (text.Contains("\r\n")) throw new Exception("紧凑的一份里不该有换行");
        });

        checks.Check("名字定了 jsonc、老的 json 是改名那条的来路", () =>
        {
            if (FileName != "history.jsonc") throw new Exception($"名字是 {FileName}");
            if (LegacyFileName != "history.json") throw new Exception($"老名字是 {LegacyFileName}");
        });

        return checks.Report();
    }
}
