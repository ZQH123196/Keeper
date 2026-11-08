using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>
/// 一份 JSONC 文档的语法树，文本里每一个字符都归某个字段所有，所以没被改动的部分
/// 一个字节都不会变。设计文档 `docs/design/config.md`里「读的时候宽容，写的时候是编辑」那一条就是这一份：
/// System.Text.Json 读注释免费，把注释写回去得自己来。
///
/// 只改真正变了的值；文档里没有的键追加在它所在对象的末尾，缩进跟着上一个键。
/// 注释、空行、键顺序、以及本程序从没听过的键，全部留在原地。
/// </summary>
public sealed class JsoncDoc
{
    /// <summary>写进 <c>settings.jsonc</c> 的那一段文本用的写法：<b>中文要还是中文</b>。
    /// System.Text.Json 默认把非 ASCII 转成 <c>\uXXXX</c> —— 那是给网络传输的写法，而这份文档
    /// 是给人手工编辑的（设计文档 `docs/design/config.md`「读的时候宽容，写的时候是编辑」那条），一个改出来的休息流程名不该变成一串十六进制。
    /// 同一条规矩 <see cref="Todos"/> 和 <see cref="CalendarNotes"/> 那两份记录早就守着。
    /// 只有「写出去」这一侧用它；读的那一侧两种写法都认，所以旧的转义串照样读得回来。</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Written = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Obj _root;
    private readonly string _head;
    private readonly string _tail;

    private JsoncDoc(Scanner scan)
    {
        _root = scan.ScanDocument();
        _head = scan.Head;
        _tail = scan.Tail;
    }

    /// 解析失败是一个返回值，不是异常 —— 读不通的那份文档必须原样留在磁盘上。
    public static bool TryParse(string text, out JsoncDoc? doc, out string? error)
    {
        doc = null;
        error = null;
        try
        {
            if (text.Trim().Length == 0)
            {
                error = "文档是空的";
                return false;
            }
            doc = new JsoncDoc(new Scanner(text));
            return true;
        }
        catch (SyntaxProblem e)
        {
            error = e.Message;
            return false;
        }
    }

    /// 文档的值视图。读设置和判断「这个值变没变」都看它，不看文本。
    public JsonObject Values
    {
        get
        {
            _root.Rebuild();
            return (JsonObject)_root.Json!;
        }
    }

    /// 文件上那个不属于设置的格式版本；没写就是 0。
    public int DocumentVersion =>
        Values.TryGetPropertyValue(Settings.VersionKey, out var stamp) &&
        stamp is JsonValue value && value.TryGetValue(out int n) ? n : 0;

    /// 把一份值写进文档：只有不同的才动，其余字节原地不动。
    public void Patch(JsonNode values)
    {
        _root.Rebuild();
        if (values is JsonObject root) PatchInto(_root, root);
    }

    /// 原样写回去：根对象之外的两段空白和注释也在内。
    public string ToText() => _head + _root.Render() + _tail;

    /// <param name="owned">这个对象名下的行是不是 Keeper 自己的数据 —— 只有
    /// <c>flows</c> 那一张表是。表里少了一行是真的删了一条休息流程，要跟着删；
    /// 别处（顶层、<c>image</c> 和 <c>calendar</c> 那些组）values 里没有的键是用户自己加的，
    /// 设计文档 `docs/design/config.md`「读的时候宽容，写的时候是编辑」那条要它原地不动。</param>
    private static void PatchInto(Obj obj, JsonObject values, bool owned = false)
    {
        // 改名先办：一行的注释和它在表里的位置都属于那一行，不属于那个名字。
        if (owned) RetitleRows(obj, values);
        foreach (var (key, value) in values)
        {
            var member = obj.Find(key);
            if (member == null)
            {
                obj.Append(key, value, stageProse: owned);
                continue;
            }
            // 嵌套组往下递归，而不是整个替掉：用户在这个组里自己加的键要留下。
            if (value is JsonObject child && member.Value is Obj nested)
            {
                PatchInto(nested, child, key == Settings.ModesKey);
                continue;
            }
            // 一条记录的列表也往下递归，逐元素按位置对：整个替掉会把写在元素上面的注释一起换掉，
            // 而 flows 那张表里 step 的说明恰恰是这么写的（首启文档只写在第一条上面，
            // 因为那一篇讲的是所有 step）。改第二步的时长不该动第一条的说明。
            if (value is JsonArray records && member.Value is Arr list && list.IsRecords)
            {
                PatchRecords(list, records, member.ValueIndent + "  ");
                continue;
            }
            if (JsonNode.DeepEquals(member.Value.Json, value)) continue;
            member.Value = Build.FromJson(value, member.ValueIndent + "  ");
        }
        if (owned)
            foreach (var gone in obj.Members.Where(m => !values.ContainsKey(m.Key))
                         .Select(m => m.Key).ToList())
                obj.Remove(gone);
        obj.Rebuild();
    }

    /// <summary>flows 那张表上「改一个名字」不该是「删一行、再加一行」。
    ///
    /// 一行上面那几行注释、和它在这张表里的位置，都属于<b>那一行</b>，不属于那个名字：旧写法
    /// 让改名走「新键找不到 → 追加到末尾（拿到的是声明里那两行字段说明）」加「旧键没人要 →
    /// 删掉（连它自己的注释一起走）」，于是用户写在那一行上面的话在他改个名字之后消失了 ——
    /// 2026-09-27 那一发的规矩记在 <c>CODING_STANDARDS.md</c>「改名的匹配规则」，就是这一发。
    ///
    /// 怎么认出改名？这张表除了名字<b>没有别的身份</b>可认，所以配对只有两档，一档比一档弱：
    /// ① 新键的内容和某一行没了的旧键一模一样 → 判它是那一行换了名字；
    /// ② 剩下的按位置一行对一行，前提是两边一样多（改名在副本里是就地换键，顺序就是原位置）。
    /// 数量对不上就照旧走追加和删 —— 那是真的新增和真的删除，不该猜。
    ///
    /// <b>②那一档会认错</b>，说清楚：同一次保存里删掉一条又新建一条长得不一样的，且两边数量
    /// 正好相等，就会被当成改名，把旧行的注释和位置搬到新行上。改之前那一条路是「注释丢了、
    /// 行挪到末尾」，两种都不好看；界面里「删一条」和「新增一条」各自都有独立一步，
    /// 一次保存里同时做完只有一种情形常见 —— 改名，而那正是 ② 想认的那个人。
    /// 只在 <c>owned</c>（也就是 flows）那一张表上做：别的组里 values 少一个键，那个键
    /// 是用户自己加的，设计文档 `docs/design/config.md`「读的时候宽容，写的时候是编辑」那条要它原地不动。</summary>
    private static void RetitleRows(Obj obj, JsonObject values)
    {
        var added = values.Where(v => obj.Find(v.Key) == null).Select(v => v.Key).ToList();
        var dropped = obj.Members.Where(m => !values.ContainsKey(m.Key)).ToList();
        // ① 内容对得上的先配：改名而没动别的东西时逐字段一样，这一档不猜。
        foreach (var key in added.ToList())
        {
            var want = values[key];
            var member = dropped.Find(m => JsonNode.DeepEquals(m.Value.Json, want));
            if (member == null) continue;
            Pair(member, key); added.Remove(key); dropped.Remove(member);
        }
        // ② 剩下的按**位置**配：两边一样多就一行对一行。改名在副本里是就地换键
        // （Options.RenameTo 重建字典时保住了顺序），所以顺序就是那一行原来站的位置。
        if (added.Count == dropped.Count)
            for (var i = 0; i < added.Count; i++) Pair(dropped[i], added[i]);
        return;

        static void Pair(Member member, string key)
        {
            member.Key = key;
            member.KeyRaw = JsonSerializer.Serialize(key, Written);
        }
    }

    /// <summary>一步 step 的列表和它该有的样子对齐：同位置的元素逐个字段比，多出来的从末尾删，
    /// 少下来的照最后一行的缩进追加。删和加都只动末尾 —— 行与行之间没有键可认，
    /// 按位置对是唯一不猜的做法（界面上「加一步」「删掉这一步」也都是往末尾走）。</summary>
    private static void PatchRecords(Arr list, JsonArray records, string indent)
    {
        var shared = Math.Min(list.Elements.Count, records.Count);
        for (var i = 0; i < shared; i++)
            if (records[i] is JsonObject row && list.Elements[i].Value is Obj element)
                PatchInto(element, row);
        while (list.Elements.Count > records.Count) list.RemoveAt(list.Elements.Count - 1);
        for (var i = list.Elements.Count; i < records.Count; i++) list.Append(records[i], indent);
        list.Rewrap();
        list.Rebuild();
    }

    // ---- 树 -----------------------------------------------------------------

    internal abstract class Node
    {
        /// 值视图。约定：这里的节点永远没有父节点，谁要放进容器谁先 DeepClone 一份。
        public JsonNode? Json;
        public abstract string Render();

        /// 容器节点重算自己的值视图；叶子在构造时就已经算好了。
        public virtual void RebuildIfContainer() { }
    }

    internal sealed class Lit : Node
    {
        public Lit(string raw, JsonNode? json) { Raw = raw; Json = json; }
        public string Raw;
        public override string Render() => Raw;
    }

    internal sealed class Obj : Node
    {
        public List<Member> Members = [];
        public string AfterLast = "";

        public Obj() => Json = new JsonObject();

        public Member? Find(string key) => Members.Find(m => m.Key == key);

        public override string Render()
        {
            var sb = new StringBuilder("{");
            foreach (var m in Members)
                sb.Append(m.Head).Append(m.KeyRaw).Append(m.Colon)
                  .Append(m.Value.Render()).Append(m.Tail)
                  .Append(m.Comma ? "," : "");
            return sb.Append(AfterLast).Append('}').ToString();
        }

        /// <summary>追加一个新键：放在最后一个键之后、闭合括号之前，缩进抄上一个键的。
        /// <paramref name="stageProse"/> 只有 flows 那张表名下的新行会是 true ——
        /// 界面上新加（或改名）的那一条休息流程，第一步上面也带上 step 那几个字段的说明，
        /// 和首启文档写出来的那两条一样：新行不该比手写的矮一截。</summary>
        public void Append(string key, JsonNode? value, bool stageProse = false)
        {
            string indent;
            if (Members.Count > 0)
            {
                var last = Members[^1];
                last.Comma = true;
                indent = last.Indent;
            }
            else
            {
                indent = Whitespace.IndentBefore(AfterLast);
                if (!AfterLast.Contains('\n')) AfterLast = "\n" + indent;
            }
            Members.Add(new Member
            {
                Head = "\n" + indent,
                Key = key,
                KeyRaw = JsonSerializer.Serialize(key, Written),
                Colon = ": ",
                Value = Build.FromJson(value, indent + "  ", stageProse),
            });
        }

        public void Rebuild()
        {
            foreach (var member in Members) member.Value.RebuildIfContainer();
            var o = new JsonObject();
            foreach (var m in Members) o[m.Key] = m.Value.Json?.DeepClone();
            Json = o;
        }

        /// <summary>删掉名下一行。<b>只有 flows 那张表走得到这里</b>（见
        /// <see cref="PatchInto"/> 的 owned 那一步）：表里少了一行是真的删了一条休息流程。
        /// 写在它上面的那两行注释跟着走 —— 那两行说的就是这一行。逗号理一遍，
        /// 最后一行不该带逗号，不然剩下的那份读不通。</summary>
        public bool Remove(string key)
        {
            var at = Members.FindIndex(m => m.Key == key);
            if (at < 0) return false;
            Members.RemoveAt(at);
            Rewrap();
            return true;
        }

        /// 逗号跟着「是不是最后一行」重算；每行自己的 Head/Tail 一个字符不动。
        public void Rewrap()
        {
            for (var i = 0; i < Members.Count; i++) Members[i].Comma = i < Members.Count - 1;
        }

        public override void RebuildIfContainer() => Rebuild();
    }

    internal sealed class Arr : Node
    {
        public List<Element> Elements = [];
        public string AfterLast = "";

        public Arr() => Json = new JsonArray();

        public override string Render()
        {
            var sb = new StringBuilder("[");
            foreach (var e in Elements)
                sb.Append(e.Head).Append(e.Value.Render()).Append(e.Tail)
                  .Append(e.Comma ? "," : "");
            return sb.Append(AfterLast).Append(']').ToString();
        }

        /// 是不是「一条一条记录」的列表（元素里有对象或列表）。标量列表是一组可选项，
        /// 写成一行，那种没有逐元素的注释可保，走整个替掉那条老路。
        public bool IsRecords => Elements.Any(e => e.Value is Obj or Arr);

        /// 往末尾加一行记录，缩进抄上一行的（上一行都没有就用调用给的 indent，那是这个数组
        /// 里第一行该有的位置）。注释不写 —— 界面上加出来的那一步，说明写在第一步上面。
        public void Append(JsonNode? value, string indent)
        {
            var at = Elements.Count > 0 ? Whitespace.IndentBefore(Elements[^1].Head) : indent;
            if (at.Length == 0) at = indent;
            Elements.Add(new Element
            {
                Head = "\n" + at,
                Value = Build.FromJson(value, at + "  "),
            });
        }

        public void RemoveAt(int index) => Elements.RemoveAt(index);

        /// 逗号跟着「是不是最后一行」重算，和 <see cref="Obj.Rewrap"/> 同一件事。
        public void Rewrap()
        {
            for (var i = 0; i < Elements.Count; i++) Elements[i].Comma = i < Elements.Count - 1;
        }

        public void Rebuild()
        {
            foreach (var element in Elements) element.Value.RebuildIfContainer();
            var a = new JsonArray();
            foreach (var e in Elements) a.Add(e.Value.Json?.DeepClone());
            Json = a;
        }

        public override void RebuildIfContainer() => Rebuild();
    }

    internal static class Whitespace
    {
        /// 一段文本末尾那一行的缩进：最后一个换行之后、到第一个非空白为止。
        public static string IndentBefore(string trivia)
        {
            int line = trivia.LastIndexOf('\n');
            var tail = line < 0 ? trivia : trivia[(line + 1)..];
            int n = 0;
            while (n < tail.Length && (tail[n] == ' ' || tail[n] == '\t')) n++;
            return tail[..n];
        }

        public static string Outdent(string indent) =>
            indent.EndsWith("  ", StringComparison.Ordinal) ? indent[..^2]
            : indent.EndsWith('\t') ? indent[..^1]
            : "";
    }

    internal class Member
    {
        /// <summary>键之前的一切：换行、缩进、写在它上面的注释。<b>可改</b>是因为
        /// <see cref="Build.AnnotateFirstStep"/> 要给新写出来的那条休息流程的第一步补上说明。</summary>
        public required string Head { get; set; }
        /// <summary>键名。<b>可改</b>是因为 flows 那张表上改名是就地换个标题
        /// （<see cref="RetitleRows"/>）：那一行的注释和位置都跟着它走，只有名字换掉。</summary>
        public required string Key { get; set; }
        /// 键的写法原样保留，含用户自己写的转义。改名那一步换的就是这一份。
        public required string KeyRaw { get; set; }
        /// 键和值之间的一切，包括那个冒号。
        public required string Colon { get; init; }
        public required Node Value { get; set; }
        /// 值和它后面那个逗号之间的一切。通常是空，也可能是行尾注释。
        public string Tail { get; set; } = "";
        public bool Comma { get; set; }

        /// 这个键那一行的缩进，新写进去的内容照着它排。
        public string Indent => Whitespace.IndentBefore(Head);

        /// 值落在哪一行就跟着哪一行的缩进。
        public string ValueIndent => Whitespace.IndentBefore(Head + Colon);
    }

    internal class Element
    {
        public required string Head { get; init; }
        public required Node Value { get; set; }
        public string Tail { get; set; } = "";
        public bool Comma { get; set; }
    }

    // ---- 新写出来的内容 -------------------------------------------------------

    /// <summary>文档里没有的、或者被改掉的那些值，由这里生成文本；缩进跟着所在的键。
    /// <paramref name="stageProse"/> 说的是「这一发生成的是 flows 里的一条休息流程」：
    /// 自带那两条的第一步上面有两行字段说明，界面上加出来的那条也要有。</summary>
    private static class Build
    {
        public static Node FromJson(JsonNode? value, string indent, bool stageProse = false)
        {
            switch (value)
            {
                case JsonObject o:
                    var obj = new Obj();
                    foreach (var (key, child) in o)
                        obj.Members.Add(new Member
                        {
                            Head = "\n" + indent,
                            Key = key,
                            KeyRaw = JsonSerializer.Serialize(key, Written),
                            Colon = ": ",
                            Value = FromJson(child, indent + "  ",
                                stageProse && key == Flow.StepsKey),
                        });
                    for (int i = 0; i < obj.Members.Count - 1; i++) obj.Members[i].Comma = true;
                    obj.AfterLast = obj.Members.Count > 0 ? "\n" + Whitespace.Outdent(indent) : "";
                    obj.Rebuild();
                    return obj;

                case JsonArray a when a.Any(item => item is JsonObject or JsonArray):
                    // 一条记录的列表要能用手加上一个元素，所以一行一个。
                    var list = new Arr();
                    foreach (var item in a)
                        list.Elements.Add(new Element
                        {
                            Head = "\n" + indent,
                            Value = FromJson(item, indent + "  "),
                        });
                    for (int i = 0; i < list.Elements.Count - 1; i++) list.Elements[i].Comma = true;
                    list.AfterLast = "\n" + Whitespace.Outdent(indent);
                    if (stageProse) AnnotateFirstStep(list);
                    list.Rebuild();
                    return list;

                case JsonArray a:
                    // 标量的列表是一组可选项，不是逐行编辑的记录，所以写成一行。
                    var inline = new Arr();
                    foreach (var item in a)
                        inline.Elements.Add(new Element
                        {
                            Head = inline.Elements.Count == 0 ? "" : " ",
                            Value = FromJson(item, indent),
                        });
                    for (int i = 0; i < inline.Elements.Count - 1; i++) inline.Elements[i].Comma = true;
                    inline.Rebuild();
                    return inline;

                default:
                    return new Lit(value?.ToJsonString(Written) ?? "null", value?.DeepClone());
            }
        }

        /// <summary>界面上新加（或改名）的那条休息流程：第一步上面补上 step 那几个字段的说明，
        /// 和首启文档写出来那两条一模一样 —— 说明仍然出自 <see cref="Settings.StepDeclarations"/>，
        /// 这里不抄第二份文字。设计文档 `docs/design/config.md`「每个设置只声明一次」那一节说的就是这一行该长什么样。</summary>
        private static void AnnotateFirstStep(Arr steps)
        {
            if (steps.Elements.FirstOrDefault(e => e.Value is Obj)?.Value is not Obj first) return;
            foreach (var member in first.Members)
            {
                var decl = Settings.StepDeclarations.FirstOrDefault(d => d.Key == member.Key);
                if (decl == null) continue;
                var pad = member.Indent;
                member.Head = $"\n{pad}// {decl.CommentZh}\n{pad}// {decl.CommentEn}\n{pad}";
            }
        }
    }

    // ---- 读 -----------------------------------------------------------------

    internal sealed class SyntaxProblem(string message) : Exception(message);

    internal sealed class Scanner(string text)
    {
        private readonly string _s = text;
        private int _p;

        /// 整份文档在根对象之外的那两段空白和注释。文件末尾那个换行就在 Tail 里，
        /// 丢了它，写回来的文本就不是原来那份了。
        public string Head { get; private set; } = "";
        public string Tail { get; private set; } = "";

        public Obj ScanDocument()
        {
            int start = _p;
            SkipTrivia();
            Head = _s[start.._p];
            var node = ScanValue();
            start = _p;
            SkipTrivia();
            Tail = _s[start.._p];
            if (_p != _s.Length) throw Fail("这个位置之后还有内容");
            return node as Obj ?? throw Fail("文档的根必须是一个对象");
        }

        /// 进来时 _p 停在值的第一个字符上；出去时停在值的最后一个字符之后。
        private Node ScanValue()
        {
            int mark = _p;
            switch (At('{') ? '{' : At('[') ? '[' : PeekOrEnd())
            {
                case '{':
                    return ScanObject();
                case '[':
                    return ScanArray();
                case '"':
                    ScanStringToken();
                    return new Lit(_s[mark.._p], ParseJson(_s[mark.._p]));
                case 't' or 'f' or 'n':
                    ScanWord();
                    break;
                default:
                    ScanNumber();
                    break;
            }
            return new Lit(_s[mark.._p], ParseJson(_s[mark.._p]));
        }

        /// 单个 token 交给 System.Text.Json 去判，它拒绝的就是我们也不认的。
        private JsonNode? ParseJson(string raw)
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                throw Fail($"这一段不是一个值：{raw}");
            }
        }

        private Obj ScanObject()
        {
            var obj = new Obj();
            Expect('{');
            int headMark = _p;
            SkipTrivia();
            if (AtEnd) throw Fail("对象没有闭合的 }");
            if (At('}'))
            {
                obj.AfterLast = _s[headMark.._p];
                _p++;
                return obj;
            }

            while (true)
            {
                var head = _s[headMark.._p];
                var keyRaw = ScanKeyToken();
                int colonMark = _p;
                SkipTrivia();
                Expect(':');
                SkipTrivia();
                var colon = _s[colonMark.._p];
                var value = ScanValue();

                int tailMark = _p;
                SkipTrivia();
                if (!At(','))
                {
                    if (!At('}')) throw Fail("这里需要一个 , 或者 }");
                    obj.Members.Add(new Member
                    {
                        Head = head, Key = keyRaw.Key, KeyRaw = keyRaw.Raw, Colon = colon,
                        Value = value,
                    });
                    obj.AfterLast = _s[tailMark.._p];
                    _p++;
                    return obj;
                }

                obj.Members.Add(new Member
                {
                    Head = head, Key = keyRaw.Key, KeyRaw = keyRaw.Raw, Colon = colon,
                    Value = value, Tail = _s[tailMark.._p], Comma = true,
                });
                _p++;
                headMark = _p;
                SkipTrivia();
                if (AtEnd) throw Fail("对象没有闭合的 }");
                if (At('}'))
                {
                    obj.AfterLast = _s[headMark.._p];
                    _p++;
                    return obj;
                }
            }
        }

        private Arr ScanArray()
        {
            var list = new Arr();
            Expect('[');
            int headMark = _p;
            SkipTrivia();
            if (AtEnd) throw Fail("数组没有闭合的 ]");
            if (At(']'))
            {
                list.AfterLast = _s[headMark.._p];
                _p++;
                return list;
            }

            while (true)
            {
                int valueMark = _p;
                var value = ScanValue();
                int tailMark = _p;
                SkipTrivia();
                if (!At(','))
                {
                    if (!At(']')) throw Fail("这里需要一个 , 或者 ]");
                    list.Elements.Add(new Element { Head = _s[headMark..valueMark], Value = value });
                    list.AfterLast = _s[tailMark.._p];
                    _p++;
                    return list;
                }

                list.Elements.Add(new Element
                {
                    Head = _s[headMark..valueMark], Value = value,
                    Tail = _s[tailMark.._p], Comma = true,
                });
                _p++;
                headMark = _p;
                SkipTrivia();
                if (AtEnd) throw Fail("数组没有闭合的 ]");
                if (At(']'))
                {
                    list.AfterLast = _s[headMark.._p];
                    _p++;
                    return list;
                }
            }
        }

        private (string Raw, string Key) ScanKeyToken()
        {
            if (!At('"')) throw Fail("键名要用引号括起来");
            var raw = ScanStringToken();
            try
            {
                return (raw, JsonSerializer.Deserialize<string>(raw) ?? "");
            }
            catch (JsonException)
            {
                throw Fail("这个键的写法不是一个 JSON 字符串");
            }
        }

        /// 引号到引号，原样切出来。转义留给上层解，因为写回去的时候要用原文。
        private string ScanStringToken()
        {
            int mark = _p;
            Expect('"');
            while (true)
            {
                if (AtEnd) throw Fail("字符串没有闭合");
                char c = _s[_p];
                if (c == '"') { _p++; break; }
                if (c == '\\') _p++;
                _p++;
            }
            return _s[mark.._p];
        }

        private void ScanNumber()
        {
            int mark = _p;
            if (At('-') || At('+')) _p++;
            while (_p < _s.Length && "0123456789.eE+-".Contains(_s[_p])) _p++;
            if (_p == mark) throw Fail("这里应该是一个值");
        }

        private void ScanWord()
        {
            foreach (var word in new[] { "true", "false", "null" })
                if (_p + word.Length <= _s.Length && _s.AsSpan(_p, word.Length).SequenceEqual(word))
                {
                    _p += word.Length;
                    return;
                }
            throw Fail("这里应该是一个值");
        }

        private void SkipTrivia()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (char.IsWhiteSpace(c)) { _p++; continue; }
                if (c != '/' || _p + 1 >= _s.Length) break;
                if (_s[_p + 1] == '/')
                {
                    while (_p < _s.Length && _s[_p] != '\n') _p++;
                    continue;
                }
                if (_s[_p + 1] == '*')
                {
                    int end = _s.IndexOf("*/", _p + 2, StringComparison.Ordinal);
                    if (end < 0) throw Fail("注释没有结尾 */");
                    _p = end + 2;
                    continue;
                }
                throw Fail("这里只有一个 /，不是注释");
            }
        }

        private bool AtEnd => _p >= _s.Length;

        private bool At(char c) => !AtEnd && _s[_p] == c;

        private char PeekOrEnd() => AtEnd ? throw Fail("文档在这里断掉了") : _s[_p];

        private void Expect(char c)
        {
            if (!At(c)) throw Fail($"这里需要一个 {c}");
            _p++;
        }

        private SyntaxProblem Fail(string what)
        {
            int line = 1, column = 1;
            for (int i = 0; i < _p && i < _s.Length; i++)
                if (_s[i] == '\n') { line++; column = 1; } else column++;
            return new SyntaxProblem($"{what}（行 {line}，列 {column}）");
        }
    }
}
