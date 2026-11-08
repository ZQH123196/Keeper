using System;
using Godot;

namespace Keeper;

/// <summary>关于窗口：托盘第六行，360x260，一屏话：这颗程序是干什么的、什么版本、照着谁做的。
///
/// 这里没有一条文字是自己写的 —— 四句全从 <c>locale/keeper.csv</c> 取（契约里界面语言那一条，
/// 设计文档 `docs/design/ui-text.md` 要的就是这个：界面里每一条文字出自同一张表）。版本号也不写死：
/// 它读 <c>project.godot</c> 的 <c>application/config/version</c>，那样「改了版本忘了改关于」
/// 这一类事没有下一次。
///
/// 摆位和 <see cref="Options"/> 同一个理由：引擎给的新窗口位置是 (0,0)，那在这台机器上等于
/// 把标题栏推到屏幕外面去，所以自己摆到主屏工作区正中。</summary>
public partial class About : Window
{
    public Action<string, string> Log = (_, _) => { };

    /// 关掉之后要还回去的那一句（App 留着 _about 的引用，靠这个知道自己该清空）。
    public Action<About>? Closed;

    public override void _Ready()
    {
        Title = Tr("关于 Keeper");
        GetNode<Label>("Center/Stack/Tagline").Text = Tr("休息提醒，自然而然");
        GetNode<Label>("Center/Stack/Body").Text = Tr(
            "当你在键盘前坐了太久，Keeper 会把屏幕缓缓染成一片颜色；等你确实回到工作上，它又缓缓退去。"
            + "它不拦截任何操作，也不要求你做什么——只是换掉屋里的光。");
        GetNode<Label>("Center/Stack/Meta").Text =
            $"{Tr("版本 {v}").Replace("{v}", Version())}\n" +
            Tr("交互方式参照 fadetop.com 的 FadeTop");

        CloseRequested += Close;

        var work = DisplayServer.ScreenGetUsableRect(0);
        Position = new Vector2I(work.Position.X + (work.Size.X - Size.X) / 2,
            work.Position.Y + (work.Size.Y - Size.Y) / 2);
        // 和设置窗同一条：场景根 visible = false，OS 窗到这一行才建，建出来就在正中间那一个点上。
        // 设置窗那一发是量出来的（_Ready 走 128 毫秒，那一段时间里屏幕上是一扇什么都没画的黑窗），
        // 这一扇搭的东西少到几乎看不见同一发 —— 但同一类隐患不该留两份，所以它跟着同一招。
        Show();
        GrabFocus();
        Log("about", $"开窗：可见={IsVisible()} 位置={Position} 大小={Size} 标题='{Title}' 版本={Version()}");
    }

    /// <c>project.godot</c> 里那一行是唯一的一份版本：<c>application/config/version</c>。
    /// 没写这一行时引擎答回来的是空串，那就说「不知道」，不编一个数出来。
    public static string Version()
    {
        var v = Godot.ProjectSettings.GetSetting("application/config/version").AsString();
        return v.Length > 0 ? v : "—";
    }

    private void Close()
    {
        Log("about", "关了");
        Closed?.Invoke(this);
        QueueFree();
    }

    /// Esc 关这一扇，和设置窗一样 —— 焦点在这里时那一下不该顺手把整个程序带走
    /// （<c>App._UnhandledKeyInput</c> 里 Esc 是退出）。
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Close();
        GetViewport().SetInputAsHandled();
    }
}
