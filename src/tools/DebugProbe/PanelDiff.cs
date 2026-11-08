using System;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 两帧同一块地方比一遍：现在这一帧，和一张<b>存下来的、日历不在屏上时的基线帧</b>。
///
/// <para><b>为什么不能拿旁边那一片当基线。</b>
/// 「内片比外面那片多出什么颜色」那个判据，前提是「没画出来 = 内片是纯壁纸色」。
/// 纯色壁纸上成立，照片壁纸上不成立 —— 这一台机器的壁纸是狗和湖：
/// 内片落在狗身上、外片落在湖面和山上，两处的壁纸本来就不同，
/// 于是内片永远有大量「外面没有的颜色」，日历不在也答「画出来了」。
/// 2026-10-15 真机那一发就这样答错的（内片 28.88% 的格子「多出颜色」，那一帧上根本没有日历）。</para>
///
/// <para><b>基线帧是同一块地方、另一时刻</b>，底下是同一张壁纸。
/// 外面那一大片<b>不作判据，只验基线还有效没有</b> —— 它的活是「壁纸有没有换」：
/// 换了的话，怎么扣都扣不干净，而扣不干净的那一大片和「日历画上去了」在读数上长得一模一样。
/// 所以那一大片必须<b>逐格和基线一样</b>，否则当场拒收。</para>
///
/// <para><b>内片先扣掉那层白纱。</b>日历是半透明的，压在壁纸上是<b>压暗成同一个比例</b>：
/// <c>现在 = (1−α)×基线 + α×面板色</c>。不扣掉它，扣掉的是壁纸，不是日历。
/// 扣法是在内片上按通道最小二乘拟合一条直线（斜率就是 1−α，截距就是 α×面板色），
/// 扣完剩下的才是日历自己写上去的字和色块。</para>
///
/// <para><b>三档，不许猜。</b>外面没扣干净 = 基线作废，当场拒收；
/// 外面干净而内片没扣干净 = 画出来了；两片都扣干净 = 没画出来。</para>
/// </summary>
internal sealed class PanelDiff
{
    /// <summary>扣掉白纱之后，三个通道加起来差过这么多级，就算一格没扣干净。</summary>
    internal const int ResidualLevels = 24;

    /// <summary>
    /// 内片里没扣干净的格数占百分之几就算判「画出来了」。
    ///
    /// <para><b>量出来的，不是拍脑袋写的。</b>拿 80 几帧真帧扫了一遍，基线还成立的那些里：
    /// <b>没有日历的读数最高 1.701%</b>（那几帧是桌面图标挪到了日历那块里面），
    /// <b>有日历的读数最低 40.624%</b>。中间这一段是空的，门限取 8%：
    /// 比最坏的那种「没画出来」高 4.7 倍，比最小的「画出来了」低 5.1 倍。</para>
    /// </summary>
    internal const double DrawnPercent = 8.0;

    /// <summary>
    /// 外面那一大片没扣干净的格数超过这个比例，就是基线作废。
    ///
    /// <para>同样量出来的：<b>同一张壁纸下外面那一片最高 3.159%</b>（图标挪了位置），
    /// <b>壁纸换过之后最低 15.548%</b>。中间这一段是空的，门限取 8%。</para>
    /// </summary>
    internal const double StalePercent = 8.0;

    private static readonly int[] Steps = { 3, 6, 12, 24, 48, 96 };

    private readonly int _width;
    private readonly int _height;
    private readonly bool _misaligned;
    private readonly double _slopes;
    private readonly double _controlPercent;
    private readonly double _panelPercent;
    private readonly int[] _controlAbove;
    private readonly int[] _panelAbove;
    private readonly int _controlTotal;
    private readonly int _panelTotal;

    private PanelDiff(
        int width, int height, bool misaligned, double slopes,
        double controlPercent, double panelPercent,
        int[] controlAbove, int[] panelAbove, int controlTotal, int panelTotal)
    {
        _width = width;
        _height = height;
        _misaligned = misaligned;
        _slopes = slopes;
        _controlPercent = controlPercent;
        _panelPercent = panelPercent;
        _controlAbove = controlAbove;
        _panelAbove = panelAbove;
        _controlTotal = controlTotal;
        _panelTotal = panelTotal;
    }

    internal int Width => _width;

    internal int Height => _height;

    /// <summary>两块不是同一块地方，没法比。</summary>
    internal bool Misaligned => _misaligned;

    /// <summary>外面那一大片没扣干净的百分比 —— 基线还有效没有，看这一个。</summary>
    internal double ControlPercent => _controlPercent;

    /// <summary>内片没扣干净的百分比 —— 判「画出来了」看这一个。</summary>
    internal double PanelPercent => _panelPercent;

    internal bool Stale => _misaligned || _controlPercent >= StalePercent;

    internal bool Drawn => !Stale && _panelPercent >= DrawnPercent;

    /// <summary>
    /// 比一遍。<paramref name="control"/> 是外面那一大片纯壁纸（验基线），
    /// <paramref name="panel"/> 是日历那一块（判画没画出来）。两块都要在这一帧里。
    /// </summary>
    internal static PanelDiff Measure(Frame live, Frame baseline, RectI control, RectI panel)
    {
        var none = new int[Steps.Length];
        if (live.Width != baseline.Width || live.Height != baseline.Height)
        {
            return new PanelDiff(live.Width, live.Height, true, 0, 100, 100, none, none, 1, 1);
        }

        RectI controlClipped = Frame.Intersect(control, new RectI(0, 0, live.Width, live.Height));
        RectI panelClipped = Frame.Intersect(panel, new RectI(0, 0, live.Width, live.Height));
        if (controlClipped.Width < 2 || panelClipped.Width < 2)
        {
            return new PanelDiff(live.Width, live.Height, true, 0, 100, 100, none, none, 1, 1);
        }

        // 外面那一大片没被日历压过，拿它和基线<b>逐格</b>比：不扣、不拟合，一格一格地对。
        int[] controlAbove = Count(live, baseline, controlClipped, Identity, out int controlCells);

        // 白纱那条直线在<b>内片</b>上拟合：只有被压过的那一片才压得出这个斜率。
        var lines = new (double Slope, double Offset)[3];
        for (int channel = 0; channel < 3; channel++)
        {
            lines[channel] = Fit(live, baseline, panelClipped, channel);
        }

        int[] panelAbove = Count(live, baseline, panelClipped, lines, out int panelCells);

        int limit = Array.IndexOf(Steps, ResidualLevels);
        double slopes = lines.Average(line => line.Slope);

        return new PanelDiff(
            live.Width, live.Height, false, slopes,
            controlAbove[limit] * 100.0 / controlCells, panelAbove[limit] * 100.0 / panelCells,
            controlAbove, panelAbove, controlCells, panelCells);
    }

    private static readonly (double Slope, double Offset)[] Identity = { (1.0, 0.0), (1.0, 0.0), (1.0, 0.0) };

    /// <summary>一条读数，够当场复核：外面那一片、内片、门限扫一遍，都摆出来。</summary>
    internal string Reading()
    {
        string control = string.Join("、", new[] { 3, 6, 12, 48, 96 }
            .Select(level => $"{level} 级 {_controlAbove[Array.IndexOf(Steps, level)] * 100.0 / _controlTotal:0.000}%"));
        string panel = string.Join("、", new[] { 3, 6, 12, 48, 96 }
            .Select(level => $"{level} 级 {_panelAbove[Array.IndexOf(Steps, level)] * 100.0 / _panelTotal:0.000}%"));

        return $"整块 {_width}×{_height}；白纱把壁纸压暗成 1−α = {_slopes:0.000}（三个通道的平均）；" +
               $"外面那一大片（验基线）{_controlTotal} 格里没对上的 {_controlPercent:0.000}%——门限扫一遍：{control}；" +
               $"日历那一块（判画没画）{_panelTotal} 格里扣掉白纱之后没扣干净的 {_panelPercent:0.000}%——门限扫一遍：{panel}；" +
               $"外面超过 {StalePercent:0.0}% 就是基线作废、这一发拒收；内片超过 {DrawnPercent:0.00}% 才算画出来了";
    }

    /// <summary>按给定的那几条直线扣掉，再数差过各道门限的格数。</summary>
    private static int[] Count(
        Frame live, Frame baseline, RectI region, (double Slope, double Offset)[] lines, out int cells)
    {
        var above = new int[Steps.Length];
        cells = region.Width * region.Height;
        // 每一格只归进<b>最高那一档</b>，最后从高到低累加 ——
        // 报出来的是「差过 k 级」的累计数，不是一档一档的数。
        var exact = new int[Steps.Length];
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                int at = (y * live.Width + x) * 4;
                int sum = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    // BGRA —— 和 <see cref="Frame"/> 内部那一份一样，通道 0 是蓝。
                    double now = live.Pixels[at + (2 - channel)];
                    double was = baseline.Pixels[at + (2 - channel)];
                    sum += (int)Math.Abs(now - (lines[channel].Slope * was + lines[channel].Offset));
                }

                for (int s = Steps.Length - 1; s >= 0; s--)
                {
                    if (sum > Steps[s])
                    {
                        exact[s]++;
                        break;
                    }
                }
            }
        }

        for (int s = Steps.Length - 2; s >= 0; s--) exact[s] += exact[s + 1];
        return exact;
    }

    /// <summary>每个通道单独拟合一条直线：<c>现在 = a×基线 + b</c>。斜率就是 1−α。</summary>
    private static (double Slope, double Offset) Fit(Frame live, Frame baseline, RectI region, int channel)
    {
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        int cells = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                int at = (y * live.Width + x) * 4;
                double was = baseline.Pixels[at + (2 - channel)];
                double now = live.Pixels[at + (2 - channel)];
                sumX += was;
                sumY += now;
                sumXY += was * now;
                sumXX += was * was;
                cells++;
            }
        }

        double denominator = sumXX - sumX * sumX / cells;
        if (Math.Abs(denominator) < 1e-9) return (1.0, 0.0);
        double slope = (sumXY - sumX * sumY / cells) / denominator;
        return (slope, (sumY - slope * sumX) / cells);
    }
}
