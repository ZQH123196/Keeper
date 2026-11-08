using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>首启那份文档由声明表渲染出来：每个键上面两行注释（中一行、英一行），整数键
/// 后面跟着它自己的范围，flows 那张表整个写出来好让人改。
///
/// 这里没有第二份默认值，也没有第二份范围 —— 值来自 <see cref="Settings.ToDocument"/>，
/// 文字来自 <see cref="Settings.Declarations"/>。这一条链里没有中间产物：没有 schema 文件，
/// 表单、首启文档、夹取直接读同一份声明（根 AGENTS.md「丢掉：schema 链」；
/// 设计文档 `docs/design/config.md`「每个设置只声明一次」那条要的就是这个效果）。
///
/// flows 表用「行」的排法：行名是用户起的，没有哪一行讲得清自己是干什么的，所以那篇注释
/// 写在表自己头上。step 的字段只在每条休息流程的第一个 step 上写一遍，因为那一篇讲的是
/// 所有 step。
///
/// 作息表那张时间块表是另一种排法：行是**记录**（和 step 一样是一行一个），所以第一行
/// 上面写字段的说明、整数键行尾写范围 —— 出厂一个时间块都没有，于是那篇说明要等有了第一行
/// 才在纸上，而表自己头上那两行注释一直都在。
/// </summary>
public static class SettingsDoc
{
    private const string Header =
        "// Keeper\n" +
        "// 配置值用 JSON 书写，// 之后是注释。注释以及 Keeper 不认识的键，保存时都会原样保留。\n" +
        "// Values are JSON; comments — and any keys Keeper does not know — survive every save.\n" +
        "//\n" +
        "// 手工编辑后需重启 Keeper 才生效；在设置窗口里保存则立即生效。\n" +
        "// A hand edit needs a restart, since the file is read once at startup.\n\n";

    /// 那个格式版本不是设置，所以它不在声明表里；文档要给它自己的两行注释。
    private static readonly SettingDecl VersionProse = new()
    {
        Key = Settings.VersionKey, Kind = SettingKind.Text, Default = "",
        CommentZh = "写出这份文档的 Keeper 用哪个配置格式；版本更高的文件可能含本程序不认识的设置。",
        CommentEn = "Which format wrote this document — a newer number may hold settings unknown here.",
    };

    private readonly record struct Style(bool Comments, bool Roomy)
    {
        public static readonly Style Document = new(true, true);
        public static readonly Style Row = new(false, false);

        /// 一行记录的第一个元素，带着那篇写给所有元素看的注释。
        public static readonly Style FirstRow = new(true, false);
    }

    public static string Render(Settings settings) => Render(settings, null);

    /// <summary>和 <see cref="Render"/> <b>同一段渲染器</b>，只是只写 <paramref name="include"/>
    /// 收着的那几条键。生活模式那一份用它 —— 系统那域是这台机器的属性，不装进生活模式
    /// （设计文档 `docs/design/life-mode.md`「生活模式是一整套配置的快照」那一节）。
    ///
    /// <b>判据是 <paramref name="include"/>，不是一份键名清单</b>：清单在加第 4 个系统级设置时
    /// 就会过期，而过期得没有声音。加一条域为「系统」的声明，生活模式那一份自动就不写它。
    ///
    /// <b>没有声明的键一律留下</b>：判据是域，而一个 Keeper 没听过的键没有域 ——
    /// 「陌生的键不许有代价」那条在这里的形状就是「剪不掉它」。</summary>
    public static string RenderScoped(Settings settings, Func<SettingDecl, bool> include) =>
        Render(settings, include);

    private static string Render(Settings settings, Func<SettingDecl, bool>? include)
    {
        var sb = new StringBuilder(Header);
        sb.Append("{\n");
        Body(sb, settings.ToDocument(), depth: 0, Style.Document, "", include);
        sb.Append("}\n");
        return sb.ToString();
    }

    /// 这一条写不写。<paramref name="key"/> 是点分的（和 <c>Prose</c> 收的是同一种），
    /// 认不出声明的键留下。
    private static bool Kept(string key, Func<SettingDecl, bool>? include) =>
        include == null || Prose(key) is not { } decl || include(decl);

    /// 一个对象里的每一行。depth 是这个对象自己所在的缩进层级，里面的键比它深一层。
    private static void Body(
        StringBuilder sb, JsonObject fields, int depth, Style style, string prefix,
        Func<SettingDecl, bool>? include = null)
    {
        var members = fields.ToList().Where(m => Kept(prefix + m.Key, include)).ToList();
        for (int i = 0; i < members.Count; i++)
        {
            var (key, value) = members[i];
            var prose = key == Settings.VersionKey ? VersionProse : Prose(prefix + key);
            if (i > 0 && style.Roomy) sb.Append('\n');
            // 一张表名下的行没有注释可写，行里有什么样的键谁也不知道。
            Member(sb, key, value, depth + 1, style, prose,
                inner: prose?.Kind == SettingKind.ModeTable ? Style.Row : style,
                innerPrefix: prose?.Kind == SettingKind.ModeTable ? "" : prefix + key + ".",
                last: i == members.Count - 1, include: include);
        }
    }

    /// 一个键：它上面的注释、它的值、值后面那个范围、以及它后面那个逗号。
    private static void Member(
        StringBuilder sb, string key, JsonNode? value, int depth, Style style,
        SettingDecl? prose, Style inner, string innerPrefix, bool last,
        Func<SettingDecl, bool>? include = null)
    {
        var pad = Pad(depth);
        if (style.Comments) WriteProse(sb, pad, prose);
        string? range = null;

        switch (value)
        {
            case JsonObject nested:
                sb.Append(pad).Append(Quote(key)).Append(": ");
                Object(sb, nested, depth, inner, innerPrefix, include);
                break;

            case JsonArray list when list.Any(item => item is JsonObject):
                // 一条记录的列表要能用手加上一个元素，所以一行一个，注释写在第一行上面。
                // 行里字段的说明去**那张表自己的声明表**里问：休息流程的步问 StepDeclarations，
                // 时间块问 BlockDeclarations，勿扰那条时段问 WindowDeclarations（都按 KeyPrefix 拼）。
                // 写死一个前缀的话，另一张表的字段一个说明都拿不到 —— 而这一篇是界面和首启
                // 文档共用的那一份。
                var recordPrefix = prose?.Kind switch
                {
                    SettingKind.BlockTable => TimeBlock.KeyPrefix,
                    SettingKind.WindowTable => DndWindow.KeyPrefix,
                    _ => Step.KeyPrefix,
                };
                sb.Append(pad).Append(Quote(key)).Append(": [\n");
                var records = list.ToList();
                for (int i = 0; i < records.Count; i++)
                {
                    sb.Append(Pad(depth + 1));
                    Object(sb, (JsonObject)records[i]!, depth + 1,
                        i == 0 ? Style.FirstRow : Style.Row, recordPrefix);
                    sb.Append(i == records.Count - 1 ? "\n" : ",\n");
                }
                sb.Append(pad).Append(']');
                break;

            case JsonArray list:
                // 标量的列表是一组可选项，不是一串记录，所以写成一行。
                sb.Append(pad).Append(Quote(key)).Append(": [")
                  .Append(string.Join(", ", list.Select(item => item!.ToJsonString(JsoncDoc.Written))))
                  .Append(']');
                break;

            default:
                sb.Append(pad).Append(Quote(key)).Append(": ")
                  .Append(value?.ToJsonString(JsoncDoc.Written) ?? "null");
                if (prose is { Kind: SettingKind.Int }) range = prose.RangeText;
                break;
        }
        if (!last) sb.Append(',');
        // 范围写在行尾，是为了让改的人一眼看见夹取的那两个数——它和夹取读的是同一条声明。
        if (range != null) sb.Append(" // ").Append(range);
        sb.Append('\n');
    }

    /// 一个对象写成多行：开括号跟着键，闭合括号和键对齐。
    private static void Object(
        StringBuilder sb, JsonObject fields, int depth, Style style, string prefix,
        Func<SettingDecl, bool>? include = null)
    {
        sb.Append("{\n");
        Body(sb, fields, depth, style, prefix, include);
        sb.Append(Pad(depth)).Append('}');
    }

    private static void WriteProse(StringBuilder sb, string pad, SettingDecl? decl)
    {
        if (decl == null) return;
        sb.Append(pad).Append("// ").Append(decl.CommentZh).Append('\n');
        sb.Append(pad).Append("// ").Append(decl.CommentEn).Append('\n');
    }

    private static SettingDecl? Prose(string key)
    {
        try
        {
            return Settings.Decl(key);
        }
        catch (KeyError)
        {
            return null;
        }
    }

    private static string Pad(int depth) => new(' ', depth * 2);

    /// 键的写法。这份文档里的键都是我们自己生成的，但写出去的东西不该假设这一点。
    private static string Quote(string key) => JsonSerializer.Serialize(key, JsoncDoc.Written);
}
