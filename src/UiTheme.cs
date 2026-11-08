using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>设置窗那一族的配色：<b>哪两张主题文件</b>、<b>一个角色名怎么变成节点上的样子</b>。
///
/// <b>为什么是这一族而不是某一扇窗的私事</b>：画廊（<see cref="Presets"/>）和设置窗
/// （<see cref="Options"/>）是同一族的两扇窗，而「暗色那一版是同一份场景、同一份 Theme 条目换一套
/// 色值，不许有只在暗色里才有的那一个节点」（设计文档 `docs/design/config.md`「配置这份文档」里 <c>uiTheme</c> 那条）。
/// 两张表和「角色怎么刷」各抄一份就是两份真相，而过期的那一份不响 —— 换配色时会有一扇窗悄悄
/// 留在旧配色上，自检里没有一条判据管它（屏幕的颜色不在自检的范围内）。
///
/// <b>角色靠显式刷，不靠 <c>theme_type_variation</c></b>：这一版（4.7.2）**变体完全不生效**，
/// 两遍都抓过帧（实测结果在 <c>docs/research/godot-4-7-2-ui-appearance-measurements.md</c> 第一节）。
/// 刷的是<b>覆写</b>，所以换主题和换语言之后都要再跑一遍。
///
/// <b>一个色号都不在代码里</b>：值全部从当前挂上那张 <see cref="Theme"/> 上读回来。</summary>
internal static class UiTheme
{
    /// <summary>声明表里那个键。<b>常量</b>：按它取主题文件的那一行就在下面，抄一份键名就是
    /// 「改键名忘了改这里」那一种 —— 而改键名的场合正是「这个键不见了，主题再也换不掉」。</summary>
    public const string Key = "uiTheme";

    /// <summary>两张主题文件。<b>只有这两条路径</b>，取值来自声明表的
    /// <see cref="Key"/>，默认亮色（明确要的：<c>light</c> / <c>dark</c>，不做跟随系统的
    /// <c>auto</c> —— 那是另一条取向，要做就加一个可选值，不动默认）。
    ///
    /// 为什么是两个<b>独立</b>的 .tres 而不是场景里的 sub_resource：换主题要能在运行期
    /// 按路径加载再挂上，而 sub_resource 只能从场景里按名字刨出来。拆出来之后
    /// 「有哪些主题」就是这一张两行的表。</summary>
    public static readonly (string Value, string Path)[] Table =
    [
        ("light", "res://src/Theme_light.tres"),
        ("dark", "res://src/Theme_dark.tres"),
    ];

    /// <summary>主题里那些<b>角色</b>（<c>RowName</c> / <c>HintLabel</c> / <c>PrimaryButton</c>…）
    /// 和它们各自的基础类型 —— <b>登记处只有这一张表</b>。
    ///
    /// <b>这一张表不负责挂</b>：<see cref="Paint"/> 走的是场景里 <c>metadata/role</c> 那一遍，
    /// 所以加一个角色只要在两张 .tres 里各加一支、在这张表里加一行；表里的 <c>Base</c> 那一栏
    /// 是给人读的（哪个基础类型该有那一支），自检里那条判据（<see cref="Options.Audit"/> 第 19 条）
    /// 按<em>场景里挂出来的</em>角色名去问两张主题，不按这张表 —— 场景里拼错一个角色名才是那一族
    /// 要抓的事。</summary>
    public static readonly (string Role, string Base)[] Roles =
    [
        ("WindowTitle", "Label"), ("StatusLabel", "Label"), ("ErrorLabel", "Label"),
        ("SectionTitle", "Label"), ("HintLabel", "Label"), ("UnitLabel", "Label"),
        ("RowName", "Label"),
        ("PrimaryButton", "Button"), ("GhostButton", "Button"),
        ("Card", "PanelContainer"), ("Page", "Panel"), ("Preview", "PanelContainer"),
    ];

    /// <summary>按 <paramref name="want"/> 取那张主题。<b>认不出的取值退回亮色</b>而不是报错：
    /// 声明的夹取已经把它收到可选值里，而这一行还要在<b>读文档之前</b>就能跑（首启那份文档里
    /// 可能还没有这个键）。
    ///
    /// <b>读不出来还回 null</b>，不静悄悄给一份引擎默认：后果是整扇窗退回引擎默认那套
    /// （Label 白字、Panel 深灰），截图上看得出「不是我要的配色」，看不出「主题没加载上」——
    /// 自检也没有一条判据管它。话由调用方说。</summary>
    public static Theme? Load(string? want)
    {
        var path = Table.FirstOrDefault(t => t.Value == want).Path;
        if (path == null) path = Table[0].Path;
        return GD.Load<Theme>(path);
    }

    /// <summary>挂在窗上、刷一遍角色。<b>换的是配色，不是形状</b>：行数、位置、每行几个格子
    /// 和亮色那一版一模一样（两张表是同一份模板生成的）。</summary>
    /// <returns>挂上没挂上（读不出来还回 false，<see cref="Load"/> 那一条纪律）。</returns>
    public static bool Apply(Window win, string? want)
    {
        var loaded = Load(want);
        if (loaded == null) return false;
        win.Theme = loaded;
        Paint(win);
        return true;
    }

    /// <summary>把主题里那些角色刷到 <paramref name="root"/> 那棵树上的节点上。
    /// 走的是场景里 <c>metadata/role</c> 挂出来的那一遍，所以<b>窗自己那些没挂角色的节点要由
    /// 调用方点名</b>（设置窗的窗头三行、每行的三格、窗底五颗是结构决定的，见
    /// <see cref="Text"/> / <see cref="Box"/>）。</summary>
    public static void Paint(Node root)
    {
        if (root is not Window win) return;
        var theme = win.Theme;
        if (theme == null) return;
        foreach (var node in Descendants(root))
        {
            if (!node.HasMeta("role")) continue;
            var role = node.GetMeta("role").AsString();
            switch (node)
            {
                case Label label: Text(theme, label, role); break;
                case Button button: Box(theme, button, role); break;
                // 面板两类（PanelContainer 和 Panel 两种）。**别只写 PanelContainer**：
                // `Page` 是 Panel，两者互不为子类，漏掉哪一类那一类就退回引擎默认那套深灰 ——
                // 而抓帧里看得出「底色不对」，看不出「漏了一类」。
                default: Panel(theme, node, role); break;
            }
        }
    }

    /// <summary>一颗文字控件按一个角色刷字色和字号。主题里没这个角色就<b>不硬刷</b>。</summary>
    public static void Text(Theme? theme, Control? c, string role)
    {
        if (c == null || theme == null) return;
        if (!theme.HasColor("font_color", role)) return;
        c.AddThemeColorOverride("font_color", theme.GetColor("font_color", role));
        c.AddThemeFontSizeOverride("font_size", theme.GetFontSize("font_size", role));
    }

    /// <summary>一颗按钮按一个角色刷五个状态的底和字。</summary>
    public static void Box(Theme? theme, Control? c, string role)
    {
        if (c == null || theme == null) return;
        foreach (var slot in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            if (theme.HasStylebox(slot, role))
                c.AddThemeStyleboxOverride(slot, theme.GetStylebox(slot, role));
        Text(theme, c, role);
    }

    /// <summary>一块面板按一个角色刷底。</summary>
    public static void Panel(Theme? theme, Node? c, string role)
    {
        if (theme == null || c is not Control control) return;
        if (!theme.HasStylebox("panel", role)) return;
        control.AddThemeStyleboxOverride("panel", theme.GetStylebox("panel", role));
    }

    /// <summary>树上除根以外那些节点（广度优先，够用了 —— 这一族只问「哪些节点挂了角色」）。</summary>
    private static IEnumerable<Node> Descendants(Node root)
    {
        var queue = new Queue<Node>();
        foreach (var child in root.GetChildren()) queue.Enqueue(child);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var child in node.GetChildren()) queue.Enqueue(child);
        }
    }
}
