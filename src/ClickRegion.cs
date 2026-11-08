using System.Collections.Generic;
using Godot;

namespace Keeper;

/// <summary>「点得到的那几块」那一块矩形 —— <b>Keeper 唯一会从底下程序手里拿走鼠标的那一块</b>。
///
/// <b>它是一块矩形，不是一扇窗</b>。2026-10-10 所有者定夺「永远都不能抢鼠标，这是 bug」，
/// 而 Windows 上鼠标穿透是整扇窗一个开关 —— 任何一扇窗罩进去的每一像素底下都点不到。
/// 所以这一族不摆窗：全局钩子按坐标判（<see cref="detection.Activity.MouseSwallower"/>），
/// 落在这一块里的<b>左键按下</b>被吃掉，其余的一律放给底下的程序。取向在
/// <c>docs/adr/0003</c>，契约在 <c>.scratch/keeper-contract/spec.md</c>
/// 「点得到的那几块靠全局钩子按坐标判，底下照常收得到那一下」那一节，
/// 「返回非零 = 真的吃掉」那一发的实测结果在
/// <c>docs/research/desktop-layer-and-window-measurements.md</c>
/// 「钩子能不能吃掉那一档」那一节。
///
/// <para><b>这一块该有多大</b>：把那几颗<b>点得到的控件</b>自己摆出来的矩形并起来，
/// 再长出 <see cref="Pad"/> 那一圈。<b>这里是那一份算法的正本</b>（工单 02）。</para>
///
/// <para><b>为什么住这一族</b>：这一族本来有两份算法 —— 编辑器一份（<c>LayoutEditor</c>
/// 摆完版之后自己算）、产品一份（<c>Fader</c> 从控件真实的矩形再并一遍），
/// 两份各自都对，但量的不是同一件事（编辑器那份拿的是排版之前的矩形）。
/// 两份各写一遍，改一处就漂一次 —— 那正是本 spec 要在代码这一侧收掉的那条。
/// **正本住这一族**：该由哪一块收鼠标是这一族的定义，不是编辑器的、也不是遮罩的。</para>
///
/// <para><b>为什么是纯函数</b>：它只并矩形，不问引擎、不问屏、不问帧 —— 所以编辑器和产品
/// 都能调它，且两边调的是同一段算术。<b>各自喂各自的矩形</b>，那一份差别不是算法里的，
/// 是喂进去的东西不同（编辑器喂它摆的框，产品喂引擎排完版的控件矩形），
/// 那一层差别写在各自的调用点上（<c>LayoutEditor.Clicked</c> 的注释）。</para></summary>
public static class ClickRegion
{
    /// <summary>多罩进去的那一圈。清单那几行之间有行距，掐到零的话行与行之间那条缝底下的程序
    /// 点得到，而人看着那一列会以为那一块也是清单 —— 看着能点、点下去是底下的程序，
    /// 比少罩一像素更坏。
    ///
    /// 2026-10-10 换判据之后它没变短：收鼠标的是钩子，而钩子吃掉的<b>正是这一圈里的那一下</b>
    /// —— 圈大一点就是多点不到别人的一格，和有窗那一版是同一笔账。</summary>
    public const int Pad = 6;

    /// <summary>把 <paramref name="boxes"/> 并起来，再长出 <see cref="Pad"/> 那一圈。</summary>
    public static Rect2I RectOf(IEnumerable<Rect2I> boxes)
    {
        var merged = new Rect2I();
        var any = false;
        foreach (var one in boxes)
        {
            merged = any ? merged.Merge(one) : one;
            any = true;
        }
        return any ? merged.Grow(Pad) : new Rect2I();
    }

    /// <summary>一块空矩形（一个数都不写死，两处读者各自问它要）。

    /// 「这一步没有点得到的东西」就答这个 —— 空矩形含不了任何点，所以
    /// <see cref="Keeper.Fader.Swallows"/> 对它恒答「不吃」。</summary>
    public static Rect2I None => new();
}