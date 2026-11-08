using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>界面文字的一张表，两个语言列。设计文档 `docs/design/ui-text.md` 那一族要的是：
/// 每一条出现在屏幕或托盘上的文字都出自同一处，任何一栏缺了就是一条自检失败，而不是
/// 屏幕上凭空少一行。
///
/// 表在 <c>res://locale/keeper.csv</c>，读进来交给引擎自己的 <see cref="Translation"/>，
/// 所以场景里和 C# 里都直接用 <c>Tr()</c> 取——界面里就只有这一份表。键就是中文原文，所以哪一条没翻，屏幕上露出的是中文
/// 而不是一个空字符串。
///
/// 为什么不走引擎的 CSV 导入：那条路要编辑器先跑一遍 import 才能把 .csv 变成 .translation，
/// 而这条路径上的东西现在全部要在无头里跑得动。解析器只有认引号这一段是自己写的。</summary>
public static class Lang
{
    public const string CsvPath = "res://locale/keeper.csv";

    /// 表头那两栏的 locale 名，和 settings 里 language 的可选值同一套。
    private static readonly string[] Locales = ["zh", "en"];

    /// 装进引擎，并把当前语言定下来。返回的是表本身的问题（缺栏、重复键），不是异常。
    public static List<string> Apply(string language)
    {
        var problems = new List<string>();
        ByLocale.Clear();
        foreach (var (key, values) in Read(problems))
        {
            foreach (var (locale, text) in values)
            {
                var table = Ensure(locale);
                table.AddMessage(key, text, "");
                if (!ByLocale.TryGetValue(locale, out var stash)) stash = ByLocale[locale] = new();
                stash[key] = text;
            }
        }
        TranslationServer.SetLocale(language);
        return problems;
    }

    /// 每个 locale 一个 Translation 对象，第二次跑（或托盘改了语言又跑一次）不要重复注册，
    /// 引擎会为这个警告。
    private static readonly Dictionary<string, Translation> Tables = new();

    /// <c>locale → 键 → 文字</c>。引擎那份 <c>Tr()</c> 是 <see cref="Node"/> 上的实例方法，
    /// 而日历的布局算术（抬头那一行、星期那一行）要能在手里没有一扇窗的时候也算得出来 ——
    /// <c>--check</c> 就是那种调用方。所以读表的时候顺手留一份，取不到就还回键本身
    /// （键是中文原文，露出中文比露出空白好，这一条和类型注释里那句是同一个理由）。
    private static readonly Dictionary<string, Dictionary<string, string>> ByLocale = new();

    /// 静态那一侧取文字。<see cref="Apply"/> 之前调用会原样还回键 —— 那不是 bug，
    /// 是无头自检里本来就没有语言可定。
    public static string T(string key) =>
        ByLocale.TryGetValue(TranslationServer.GetLocale(), out var table) && table.TryGetValue(key, out var text)
            ? text : key;

    private static Translation Ensure(string locale)
    {
        if (Tables.TryGetValue(locale, out var known)) return known;
        var table = new Translation();
        table.Locale = locale;
        TranslationServer.AddTranslation(table);
        Tables[locale] = table;
        return table;
    }

    /// 中日韩统一表意文字那一段。en 栏里出现它们就是漏翻。
    private static bool Cjk(char c) => c >= '一' && c <= '鿿';

    /// <summary>表里都有哪些键。设置窗那一组判据用它问「每个设置的标签在不在表里」—— 缺一条不
    /// 是崩，是那一行屏幕上露出键名，所以要在构建命令里就抓住。</summary>
    public static HashSet<string> Keys()
    {
        var problems = new List<string>();
        var keys = new HashSet<string>();
        foreach (var (key, _) in Read(problems)) keys.Add(key);
        return keys;
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Table = new(
        () => "语言表通过：每一条文字都有 zh 和 en 两栏（ADR-0001「语言表」那一节那条无头自检）",
        "语言表", SelfTest);

    /// <summary>表自己讲不讲得通：每个键两栏都在、都有内容、键不重复。设计文档 `docs/design/ui-text.md` 那一条
    /// 「编译期那道硬保证换成构建命令里跑的一条无头自检」说的就是它 —— 跑在 <c>--check</c> 里。</summary>
    public static List<string> SelfTest()
    {
        var found = new List<string>();
        var rows = Read(found);
        var seen = new HashSet<string>();
        foreach (var (key, values) in rows)
        {
            if (!seen.Add(key)) found.Add($"{CsvPath}：键「{key}」出现了两次");
            foreach (var locale in Locales)
            {
                if (!values.ContainsKey(locale)) continue; // 表里没这一栏在 Read 里已经报过
                var text = values[locale];
                if (text.Trim().Length == 0) found.Add($"{CsvPath}：「{key}」的 {locale} 是空的");
                // 漏翻的那一栏看着和翻过的一样，只是换语言时屏幕上露出中文。这一条抓的就是它。
                if (locale == "en" && text.Any(Cjk))
                    found.Add($"{CsvPath}：「{key}」的 en 栏还是中文：{text}");
            }
        }
        return found;
    }

    /// 读表。problems 收的是这次读出来发现的问题（坏行、缺栏），调用方决定怎么处理。
    private static List<(string Key, Dictionary<string, string> Values)> Read(
        List<string> problems)
    {
        var rows = new List<(string, Dictionary<string, string>)>();
        if (!Godot.FileAccess.FileExists(CsvPath))
        {
            problems.Add($"{CsvPath} 不存在，界面上没有一条文字取得到");
            return rows;
        }

        var text = Godot.FileAccess.GetFileAsString(CsvPath);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var columns = Array.Empty<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var fields = SplitCsvLine(lines[i]);
            if (i == 0)
            {
                columns = [.. fields];
                if (columns.Length < Locales.Length + 1)
                {
                    problems.Add($"{CsvPath} 表头该是 keys 加 {string.Join("、", Locales)}，读到的是 {lines[i]}");
                    return rows;
                }
                continue;
            }
            if (fields.Count != columns.Length)
            {
                problems.Add($"{CsvPath} 第 {i + 1} 行有 {fields.Count} 栏，表头是 {columns.Length} 栏");
                continue;
            }
            var values = new Dictionary<string, string>();
            for (var c = 1; c < columns.Length; c++)
                if (Array.IndexOf(Locales, columns[c]) >= 0) values[columns[c]] = fields[c];
            foreach (var locale in Locales)
                if (!values.ContainsKey(locale))
                    problems.Add($"{CsvPath}：表头没有 {locale} 这一栏，「{fields[0]}」取不到翻译");
            rows.Add((fields[0], values));
        }
        return rows;
    }

    /// 认引号的最小 CSV：一个双引号包着的字段里可以有逗号，写两个双引号表示一个双引号。
    /// 英文那栏迟早要写「it's fine, isn't it」，不认引号就是等着哪天静悄悄地错一栏。
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') { current.Append(c); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = false;
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': fields.Add(current.ToString()); current.Clear(); break;
                default: current.Append(c); break;
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
