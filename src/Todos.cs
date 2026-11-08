using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Keeper;

/// <summary>休息清单。它是记录而不是配置，所以整体重写，不走 JSONC 那条原位编辑的规矩
/// （取向和代价见 <c>docs/adr/0002-settings-and-records.md</c>「记录借 JSONC 的读，不借它的写」那一节）；
/// 格式仍是 JSONC 那一种：读的时候容忍注释和尾逗号，写出去的是合法的紧凑 JSONC，注释不承诺活得过下一次保存。
///
/// 文件的字节形状定死：一个数组，每项 <c>{"text":…,"done":…}</c>，两格缩进、
/// camelCase、中文不转义、末尾一个换行。<see cref="Save"/>
/// 里那两处一次 Replace 和一次 Encoder 不是讲究，是让它每一回都写出<b>同一形状</b>：
/// <see cref="System.Text.Json"/> 的缩进换行是 CRLF，这份文件要的是 LF。
///
/// 根节点是<b>数组</b> —— 这一条决定了它不能用 <see cref="JsoncDoc"/> 读：那份原位编辑器的根
/// 只能是对象（<c>Values</c> 往 <c>JsonObject</c> 上硬 cast）。所以记录这一族用
/// <see cref="System.Text.Json"/> 的读旗换 JSONC 的宽容，代价是没有原位编辑。</summary>
public sealed class Todos : RecordDoc
{
    public const string FileName = "todos.jsonc";

    /// 老版那份的名字。首启逐字节改名成 <see cref="FileName"/>，见 <see cref="Records.RenameLegacy"/>。
    public const string LegacyFileName = "todos.json";

    public List<Todo> Items { get; set; } = [];

    public sealed class Todo
    {
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }

    /// 读不到就是一条都没有：不是错误，但也不能就这么开着清单那一档。谁来补第一轮的三条，
    /// 是 <see cref="Open"/> 的事。根节点是一个数组，不是包一层的对象 —— 上面定的就是那个形状。
    ///
    /// <b>原来这里 catch 完静默还一份空的</b>，下一次保存就把空清单写回磁盘 —— 清单是人勾出来的，
    /// 抹了就没了。现在读不通那一半由 <see cref="RecordDoc.Tolerantly"/> 兜着，立起
    /// <see cref="RecordDoc.Unreadable"/>，写之前先留底。
    ///
    /// 那一族那半纪律（宽容读、读不通立旗、写前先留底）<b>住在 <see cref="RecordDoc"/> 上</b>，
    /// 四份共用一份实施。自检的前身这一份的旗是 <c>public bool Unreadable { get; set; }</c>，
    /// 而全仓没有一处从外面写它 —— 一份能被外面随手立起来的旗，迟早会在没人读它的时候
    /// 把用户那份清单先留底。现在它和另外三份一样，只有 <see cref="RecordDoc"/> 上那一个读的口子。
    public static Todos Load(string path) =>
        Tolerantly(() => new Todos
        {
            Items = JsonSerializer.Deserialize<List<Todo>>(File.ReadAllText(path), Shape) ?? [],
        });

    /// <summary>启动路径：读那份文件，磁盘上还没有就先写三条自己说明自己的种子。
    /// 空着的清单看着像坏了，不像没用过。</summary>
    public static Todos Open(string path)
    {
        if (!File.Exists(path))
        {
            var seeds = new Todos { Items = FirstRun() };
            seeds.Save(path);
            return seeds;
        }
        return Load(path);
    }

    public static List<Todo> FirstRun() =>
    [
        new() { Text = "站起来看看远处" },
        new() { Text = "倒杯水" },
        new() { Text = "转动肩膀和脖子" },
    ];

    /// <summary>整体重写那几条。写之前把 trim 为空的那一条丢掉，并把滤过的那份同时换进内存 ——
    /// 同一条规矩滤两个头：落盘在这里滤，界面上那一行在同一条 filter 里跟着没
    /// （<c>Options.BuildChecklist</c> 那一段）。留着它不是数据坏了，是休息里多出一个没字的方块
    /// （<c>Fader.BuildChecklist</c> 按条数摆行，
    /// 它不知道那条是空的）。</summary>
    public void Save(string path)
    {
        Items.RemoveAll(t => t.Text.Trim().Length == 0);
        WriteTo(path, JsonSerializer.Serialize(Items, Shape).Replace("\r\n", "\n") + "\n");
    }

    private static readonly JsonSerializerOptions Shape = Jsonc(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 默认的转义器会把中文写成 \uXXXX：写出去要是 \uXXXX，屏幕上那三行就没法读了。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Checklist = new(
        () => "休息清单通过：数组根带注释读得回来、读不通的先留底、写出去仍是两格缩进加 LF 和一个尾换行",
        "休息清单", SelfTest);

    /// 自检里的判据（组名 <c>todos</c>）。两头夹：带注释的读得回来，真坏的不许抹。
    public static List<string> SelfTest()
    {
        var checks = new Checks("todos");
        var root = checks.Root!;

        checks.Check("带注释和尾逗号的清单读得回来", () =>
        {
            var path = Path.Combine(root, "annotated.jsonc");
            File.WriteAllText(path, """
                [
                  // 第一条是站起来看看远处
                  { "text": "站起来看看远处", "done": true },
                  { "text": "倒杯水", "done": false }, /* 块注释也认 */
                ]
                """, new System.Text.UTF8Encoding(false));
            var got = Todos.Load(path);
            if (got.Unreadable) throw new Exception("带注释的一份被当成读不通");
            if (got.Items.Count != 2) throw new Exception($"读回来 {got.Items.Count} 条，该是 2 条");
            if (!got.Items[0].Done || got.Items[1].Done) throw new Exception("done 读错了");
            if (got.Items[0].Text != "站起来看看远处") throw new Exception("中文读串了");
            // 这一条得能失败：同一份内容按严格 JSON 的默认选项读必须失败，不然这一发什么都没量。
            try
            {
                JsonSerializer.Deserialize<List<Todos.Todo>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                throw new Exception("这条判据不会失败：不带那两旗也读得通");
            }
            catch (JsonException) { }
        });

        checks.Check("读不通的立旗，第一次写回去之前先留 .bak", () =>
        {
            var path = Path.Combine(root, "broken.jsonc");
            var broken = "[ { \"text\": \"甲\", ";
            File.WriteAllText(path, broken, new System.Text.UTF8Encoding(false));
            var got = Todos.Load(path);
            if (!got.Unreadable) throw new Exception("截断的一份读不通却没立旗");
            if (File.ReadAllText(path) != broken) throw new Exception("读的一趟动了文件");
            got.Items.Add(new Todos.Todo { Text = "乙" });
            got.Save(path);
            if (!File.Exists(path + RecordDoc.BackupSuffix)) throw new Exception("第一次写回读不通那份之前没留 .bak");
            if (File.ReadAllText(path + RecordDoc.BackupSuffix) != broken) throw new Exception("留下的那份不是原样");
            if (Todos.Load(path).Items.Count != 1) throw new Exception("写回去的那份没把自己读回来");
        });

        checks.Check("写出去还是那个形状", () =>
        {
            var path = Path.Combine(root, "shape.jsonc");
            var todos = new Todos { Items = [new Todos.Todo { Text = "倒杯水", Done = true }] };
            todos.Save(path);
            var text = File.ReadAllText(path);
            if (text != "[\n  {\n    \"text\": \"倒杯水\",\n    \"done\": true\n  }\n]\n")
                throw new Exception($"写出去的形状变了：{text.Replace("\n", "\\n")}");
            if (text.Contains("\\u")) throw new Exception("中文被转义成了 \\uXXXX");
            if (text.Contains("\r\n")) throw new Exception("缩进换行没折成 LF");
        });

        checks.Check("名字定了 jsonc、老的 json 是改名那条的来路", () =>
        {
            if (FileName != "todos.jsonc") throw new Exception($"名字是 {FileName}");
            if (LegacyFileName != "todos.json") throw new Exception($"老名字是 {LegacyFileName}");
        });

        return checks.Report();
    }
}
