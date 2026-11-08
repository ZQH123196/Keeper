using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Keeper;

/// <summary>日历的每日备注。它是记录而不是配置，所以整体重写，不走 JSONC 那条原位编辑的规矩
/// （取向和代价见 <c>docs/adr/0002-settings-and-records.md</c>「记录借 JSONC 的读，不借它的写」那一节），也不进
/// <c>settings.jsonc</c> 那个文档。格式仍是 JSONC 那一种：读的时候容忍注释和尾逗号。
///
/// 形状是在这里定的：一把 ISO 日期作键的平对象，和 <see cref="Todos"/> 同一族写法：两格缩进、
/// 中文不转义、LF 换行、末尾一个换行。键排序写出去，所以「同样 contents 同样字节」这件事不用记
/// 插入顺序 —— 一天一条，读回来还是那一份。
///
/// 读的时候宽容（设计文档 `docs/design/config.md`里「读的时候宽容」「解析失败是一个状态，不是崩溃」那两条，搬到记录这一侧）：读不通不是崩溃，是
/// 一条都没有，并且把 <see cref="Unreadable"/> 立起来。<b>第一次往这么一份文件上写之前先留
/// 一份 <c>.bak</c></b> —— 备注是人写的字，不是程序能重算出来的东西，抹了就没了。</summary>
public sealed class CalendarNotes : RecordDoc
{
    public const string FileName = "calendar.jsonc";

    /// 老版那份的名字。首启逐字节改名成 <see cref="FileName"/>，见 <see cref="Records.RenameLegacy"/>。
    public const string LegacyFileName = "calendar.json";

    /// 一天一条。键是 <c>yyyy-MM-dd</c>（不带时区，日历说的是「桌上那一天」）。
    public SortedDictionary<string, string> Notes { get; set; } = [];

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd");

    public string Get(DateOnly day) => Notes.TryGetValue(Key(day), out var text) ? text : "";

    /// 写一条。空字符串就是「这一天没有备注了」，所以键跟着没 —— 文件里不留一条空值，
    /// 那样读起来像坏掉而不像被擦掉。返回这次调用有没有真的动过内容。
    public bool Set(DateOnly day, string text)
    {
        var key = Key(day);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            if (!Notes.Remove(key)) return false;
            Dirty = true;
            return true;
        }
        if (Notes.TryGetValue(key, out var was) && was == trimmed) return false;
        Notes[key] = trimmed;
        Dirty = true;
        return true;
    }

    /// 这一份有没有被改过。<see cref="App"/> 那侧用它决定要不要落盘 —— 备注是几分钟
    /// 才动一次的东西，不值得每帧问一次磁盘。
    public bool Dirty { get; set; }

    /// <summary>读：借 JSONC 的宽容，读不通不是崩溃 —— 一条都没有，并把
    /// <see cref="RecordDoc.Unreadable"/> 立起来交给调用方说。
    ///
    /// 那一族那半纪律（宽容读、读不通立旗、写前先留底）<b>住在
    /// <see cref="RecordDoc"/> 上</b>，四份共用一份实施。自检的前身这一份是那一族里最早写出来的那处
    /// （<see cref="Save"/> 里那几行照的就是它），所以另外三份照着它抄了一遍；现在是反过来。
    ///
    /// <b>这一份的形状是它自己的</b>：根节点是一张以 ISO 日期作键的平对象，两格缩进、中文不转义。
    /// </summary>
    public static CalendarNotes Load(string path) =>
        Tolerantly(() =>
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Shape);
            var notes = new CalendarNotes();
            foreach (var (key, value) in read ?? [])
                if (LooksLikeADay(key) && value.Trim().Length > 0) notes.Notes[key] = value.Trim();
            return notes;
        });

    private static bool LooksLikeADay(string key) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    /// <summary>启动路径：文件还没有就是「一条备注都没写过」，不写种子（日历没有「首启该有什么」，
    /// 那和清单的三条不一样）。存在但读不通，把旗立起来交给调用方说。</summary>
    public static CalendarNotes Open(string path)
    {
        if (!File.Exists(path)) return new CalendarNotes();
        var notes = Load(path);
        return notes;
    }

    public void Save(string path)
    {
        WriteTo(path, Text());
        Dirty = false;
    }

    /// 写出去的那一份字节。单独拆出来是给 <see cref="SelfTest"/> 看的：它要能对着一个
    /// 手写的期望串比，而不必绕一趟磁盘。
    public string Text() =>
        JsonSerializer.Serialize(Notes, Shape).Replace("\r\n", "\n") + "\n";

    private static readonly JsonSerializerOptions Shape = Jsonc(new JsonSerializerOptions
    {
        WriteIndented = true,
        // 默认的转义器会把中文写成 \uXXXX：那和 todos.jsonc 那一族不是一个长相，
        // 而人迟早会拿编辑器开这份文件。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf NotesDoc = new(
        () => "备注记录通过：写出去和定下来那份字节相同、擦掉就是没了、读不通的先留 .bak、" +
              "带注释和尾逗号的读得回来",
        "备注记录", SelfTest);

    /// <summary>自检不碰 %APPDATA%（那是用户现在写着的备注，不是测试数据）。它比的是写出去的
    /// 字节对一个手抄的期望串：形状是在这里定的，所以「它真是这个形状」由这一发自己证明。
    ///
    /// <b>这一组原先没有 <c>Check</c> 包着</b>，一个异常冲出整组、冲出整个 <c>--check</c>，
    /// 后头三十几组一条都没报，而无头那边不退出（下面那段注释记着撞到它的那一发）。
    /// 现在整个身子在 <see cref="Checks.Check"/> 里面：异常收成一条问题，后面那些组照跑。</summary>
    public static List<string> SelfTest()
    {
        var checks = new Checks("calendar");
        var dir = checks.Root!;
        var path = Path.Combine(dir, FileName);
        checks.Check("备注那一族的落盘纪律", () =>
        {
            var day = new DateOnly(2026, 9, 26);
            var notes = new CalendarNotes();
            notes.Set(day, "去眼科复查");
            notes.Set(new DateOnly(2026, 10, 3), "  带病历  ");   // 两头空白自己擦掉
            var written = notes.Text();
            const string Expected = """
                {
                  "2026-09-26": "去眼科复查",
                  "2026-10-03": "带病历"
                }
                """ + "\n";
            if (written != Expected) checks.Fail($"写出去的形状和定下来的那份不一样：{written.Replace("\n", "\\n")}");

            notes.Save(path);
            var back = Load(path);
            if (back.Unreadable) checks.Fail("自己写的那一份读不通");
            if (back.Get(day) != "去眼科复查") checks.Fail($"写「去眼科复查」读回来是「{back.Get(day)}」");
            if (back.Get(new DateOnly(2026, 10, 3)) != "带病历") checks.Fail("两头空白没擦，或者读丢了");
            if (back.Notes.Count != 2) checks.Fail($"两条读回来 {back.Notes.Count} 条");

            // 空字符串是「擦掉」，不是「留一条空的」
            var copy = Load(path);
            if (!copy.Set(day, "   ")) checks.Fail("写空白没算成改动");
            if (copy.Notes.ContainsKey(Key(day))) checks.Fail("擦掉之后键还在，界面上会是一格空的");
            if (copy.Set(day, "")) checks.Fail("本来就没有的那条，擦它不该报「动了」");

            // 格式是 JSONC 那一种：人手加的注释和尾逗号不许把整份备注读成空的。
            var annotated = Path.Combine(dir, "annotated.jsonc");
            File.WriteAllText(annotated, """
                {
                  // 我给自己留的话
                  "2026-09-26": "去眼科复查", /* 块注释也认 */
                }
                """, Utf8NoBom);
            var withComments = Load(annotated);
            if (withComments.Unreadable) checks.Fail("带注释的一份被当成读不通");
            if (withComments.Get(day) != "去眼科复查") checks.Fail("带注释的那一份没把备注读回来");
            // 这一条得能失败：同一份内容按严格 JSON 的默认选项读必须失败，不然这一发什么都没量。
            try
            {
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(annotated),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                checks.Fail("这条判据不会失败：不带 JSONC 那两旗也读得通");
            }
            catch (System.Text.Json.JsonException) { }

            // 读不通的文件：一条都没有 + 旗立着 + 不许静悄悄抹掉
            File.WriteAllText(path, "{ 这不是一个对象");
            var broken = Open(path);
            if (!broken.Unreadable) checks.Fail("读不通的那份没把旗立起来");
            if (broken.Notes.Count != 0) checks.Fail("读不通却读出了东西");
            broken.Set(new DateOnly(2026, 1, 2), "新的一条");
            broken.Save(path);
            // **读 .bak 之前先问它在不在**：这一行以前没有 Check 包着（当时整个自检方法是一个
            // try/finally），所以一个 FileNotFoundException 会冲出整组、冲出整个 --check，
            // 无头那边就不退出了 —— 实测（2026-10-01 反向验证时撞到）：把 RecordDoc.WriteTo 里
            // 留底那一段拿掉，这一行就是那个把自检整个挂死的实测结果，后头三十几组一条都没报。
            // **整个身子现在在 Checks.Check 里面**（见上面那个方法的注释），所以那种异常收成
            // 一条问题、后面那些组照跑 —— 那一发的坑仍在这儿记着，它量的是「为什么这一行要问」。
            // 形状那一句要配一条**别的路也能走通**的判据（CODING_STANDARDS 第 5 条），
            // 所以这里用 else 而不是 early-return：留底没发生报一条，发生了才比字节。
            if (!File.Exists(path + RecordDoc.BackupSuffix)) checks.Fail("往一份读不通的文件上写，没先留 .bak");
            else if (File.ReadAllText(path + RecordDoc.BackupSuffix) != "{ 这不是一个对象") checks.Fail(".bak 里那份不是原来那串字节");
            if (Open(path).Unreadable) checks.Fail("留过底、写过之后，旗该放下来");

            // 文件不存在：那是「没用过」，不是「坏掉」，也不写种子
            File.Delete(path);
            var fresh = Open(path);
            if (fresh.Unreadable) checks.Fail("文件不存在被当成了读不通");
            if (File.Exists(path)) checks.Fail("文件还没有就凭空写了一份空的出去");
        });
        return checks.Report();
    }
}
