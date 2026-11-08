using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>预设生活模式：**随安装自带的一个只读目录**，里面每一份就是一份完整的配置文档
/// （设计文档 `docs/design/preset-life-mode.md`「预设生活模式是内置的只读模板，收进来才算自己的」那一节，
/// ）。
///
/// <b>只读是结构上的，不是靠自觉</b>：这个类里<b>没有一个写</b> —— 目录用引擎自己的
/// <see cref="DirAccess"/> 列、文件用 <see cref="FileAccess"/> 读，而「收一个」把读进来的
/// <b>字节</b>交给生活模式那一条现成的路（<see cref="LifeModeStore.TakeIn"/>），那条路只往
/// <b>用户那一份</b>写。打包之后这个目录在 pck 里，pck 本来就是只读的 ——
/// 「程序升级永不写它、也永不碰用户收进来的副本」（契约同一条）于是不是一条自律，是一件
/// 装出来就成立的事。
///
/// <b>为什么读走引擎而不走 System.IO</b>：量到的（2026-10-02，见
/// <c>docs/research/preset-life-flows-readonly-directory.md</c>）—— <c>FileAccess</c> 和
/// <c>DirAccess</c> 认 <c>res://</c>，也认<b>绝对路径</b>（自检里那个用完就删的临时目录就是绝对路径，
/// 所以同一段代码在装好的程序和自检里走的是同一条）；<c>System.IO</c> 只认后者，打包之后
/// <c>res://presets/</c> 在 pck 里、<c>File.ReadAllBytes</c> 够不着。
///
/// <b>名字就是文件名去掉后缀</b>：预设不是第二种实体（<c>docs/adr/0005-…md</c>「预设生活模式不是
/// 第二种实体」），所以它既没有一份独立的名单，也没有一个自己的名字字段 —— 目录里那一份文件
/// 叫什么，它就叫什么，而「收一个」拿这个名字去过 <see cref="LifeModeStore.ValidateName"/> 那一道判据。
///
/// <b>「读得通」的定义只有这一处</b>（<see cref="Why"/>）：讲得通、读得出一个对象、而且开头写明了
/// 是哪一版格式 —— 后两条就是「一份完整的配置文档」那句话（设计文档 `docs/design/config.md`「第一键是 <c>schemaVersion</c>」）。
/// 读不通的那一份<b>照样列出来</b>，只是收不进来：一份坏文件说成「一个都没有」和一份坏名单
/// 说成「你没存过」是同一对假象（<c>CODING_STANDARDS.md</c> 第 5 条那一族）。</summary>
public static class PresetLifeModes
{
    /// <summary>随安装带来的那一个目录。<b>装在 <c>res://</c> 里</b>，所以打包之后它在 pck 里面，
    /// 而 pck 是只读的 —— 「程序永不写它」是装法给的那一份保证，不是代码自律
    /// （实测结果同 <see cref="PresetLifeModes"/> 的类型注释）。</summary>
    public const string Dir = "res://presets";

    /// <summary>一份预设生活模式。<b>不是新的一种实体</b>：它就是那份文件，
    /// <see cref="Name"/> 是文件名去掉后缀（见类型注释那一段）。
    ///
    /// <paramref name="Note"/> 是给界面上那一行看的：读得通时是<b>出处</b>
    /// （<see cref="Provenance"/> —— 注释即考证，ADR-0005），读不通时是<b>为什么收不进来</b>。
    /// 两者不会同时出现，而读不通那一档照样把这一行列出来。</summary>
    public sealed record Preset(string Name, string Path, bool Readable, string Note);

    /// <summary>那个目录当前是什么样子。<b>目录整个不在</b>自己一档（<see cref="Missing"/>），
    /// 它和「目录在、里面那一份读不通」是两件事，界面上要各说各的
    /// （设计文档 `docs/design/preset-life-mode.md`「读不通有几档，各说各的」那一族）。</summary>
    public sealed record Gallery(IReadOnlyList<Preset> Presets, bool Missing)
    {
        /// 一个都没列出来（目录不在，或目录在而里面没有一份预设）。**和「读不通」不是一档**：
        /// 后者照样列得出行。
        public bool None => Presets.Count == 0;
    }

    /// <summary>列那个目录。<b>只读</b>：列的是文件名，读的是每一份的前几行判读不读得通，
    /// 一个字节都不写回去。
    ///
    /// 排序按文件名（<see cref="StringComparer.Ordinal"/>）—— 菜单里不数项，而「画廊里第几行」
    /// 不该跟着文件系统的枚举顺序漂。
    ///
    /// <paramref name="dir"/> 默认是装在程序里的那一个；自检里递一个用完就删的临时目录，
    /// 于是<b>判据用的预设目录也是它自己那一份</b>（<c>CODING_STANDARDS.md</c>：每一组只跑自己
    /// 那个用完就删的目录，<c>%APPDATA%</c> 一个字节都不碰，而装在程序里的那一份是仓库里的资产，
    /// 不是测试数据）。</summary>
    public static Gallery Load(string? dir = null)
    {
        var where = Path(dir ?? Dir);
        var found = new List<Preset>();
        // 目录不在是自己一档，不是「一个预设都没有」：说得出是哪一件事，界面上才说得清。
        if (DirAccess.Open(where) is not { } handle) return new Gallery(found, Missing: true);

        foreach (var file in handle.GetFiles()
                     .Where(f => f.EndsWith(LifeModeNumbers.Suffix, StringComparison.Ordinal))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = FileAccess.GetFileAsString($"{where}/{file}");
            var stem = file[..^LifeModeNumbers.Suffix.Length];
            var bad = Why(text);
            found.Add(new Preset(stem, $"{where}/{file}", bad == null, bad ?? Provenance(text)));
        }
        return new Gallery(found, Missing: false);
    }

    /// <summary>这一份的字节。<b>读不出来是 null</b>，不是空数组：空数组是一个「空文件」，
    /// 而空文件是 <see cref="Why"/> 那一档（收不进来），两件事不该混成一件。</summary>
    public static byte[]? BytesOf(Preset preset)
    {
        using var file = FileAccess.Open(preset.Path, FileAccess.ModeFlags.Read);
        return file?.GetBuffer((long)file.GetLength());
    }

    /// <summary>这一份读不读得通；读得通还回 null，读不通还回一句<b>说给人听</b>的话。
    ///
    /// <b>那句话里不许有配置文件内部的名字</b>（设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那条）：
    /// 所以「开头没写明是哪一版格式」是照人话说的，而 <see cref="Settings.VersionKey"/> 那个键名
    /// 一个字都不进这一句 —— 它由屏幕那一组判（<c>Options.ScreenSelfTest</c>，它连画廊那几行
    /// 一起扫）。解析器自己给的理由（<see cref="JsoncDoc"/>）是同一份纪律：它报的是「哪一行少了
    /// 一个括号」那种话。</summary>
    public static string? Why(string text)
    {
        if (text.Trim().Length == 0) return "它是空的";
        if (!JsoncDoc.TryParse(text, out var doc, out var error)) return error;
        if (!doc!.Values.ContainsKey(Settings.VersionKey))
            return "它不像一份完整的配置文档：开头没有写明是哪一版格式";
        return null;
    }

    /// <summary>出处那一行 —— 文档顶上那一段 <c>//</c> 注释里的第一句非空的话。
    ///
    /// <b>为什么是它</b>：出处是写在注释里的（<c>GLOSSARY.md</c>「预设生活模式」那一条、
    /// ADR-0005「考证的取向」），而注释在文档里、不在界面上，所以界面要显示出处就得**从文档
    /// 里读出来**。这是同一份东西的第二个读者，不是第二份真相 —— 改注释就改了界面上的那一行。
    ///
    /// 读法有边界，说清楚：只看第一个 <c>{</c> 之前、顶上那一段连续的 <c>//</c> 行（空行断开），
    /// 取第一句非空的。带 <c>/* */</c> 的块注释和后面的段落不取 —— 那是格式说明和逐块出处，
    /// 那一行摆不下，也不该被截半句当出处。</summary>
    public static string Provenance(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) return "";        // 顶上的那一段到空行为止
            if (line.StartsWith('{')) return "";    // 还没有注释就是没有出处
            if (!line.StartsWith("//")) continue;
            var said = line[2..].Trim();
            if (said.Length > 0) return said;
        }
        return "";
    }

    /// <summary>交给引擎之前把路径写成引擎认的那一种：<b>斜杠</b>。
    ///
    /// 量到的（2026-10-02）：<c>DirAccess.open</c> 认 <c>C:/…</c>，而 <c>System.IO.Path.Combine</c>
    /// 在这台机器上给的是 <c>C:\…</c> —— 所以自检里那些临时目录不换斜杠就列不出来，
    /// 而症状是「目录不在」（一个空画廊），不报任何错。取值在 <c>docs/research/</c> 那一份里。</summary>
    private static string Path(string dir) => dir.Replace('\\', '/');
}
