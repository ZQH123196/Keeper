using System;
using System.Collections.Generic;

namespace Keeper;

/// <summary>屏幕上读回来的那些点，以及拿它们比的规矩。
///
/// <b>为什么要有这个类</b> —— 一条实测结果，不是一条洁癖。这一件事在仓库里有<b>两份采样</b>
/// （<c>Win32.SampleScreen</c> 把 <c>GetPixel</c> 的失败值 <c>CLR_INVALID</c> 折成 <c>(0,0,0)</c>，
/// 而测量程序自己那份折成 <c>(-1,-1,-1)</c> 并记一行账 —— 对同一件事的处理<b>相反</b>）、
/// <b>四份比色</b>（<c>Win32.ChannelDifference</c>、测量程序的 <c>Err</c>、<c>App.FrameResidual</c>
/// 和 <c>FrameDifference</c> 里各自内联的那两条）、<b>三份混色</b>（测量程序的 8 位那份、测量程序的线性光那份、
/// <c>FadeRun.Mixed</c>）。
///
/// 把失败折成一个合法颜色，接口上就分不出「读到黑」和「没读到」。而下游的判据只看差：
/// <c>AlphaMeasurement</c> 那条「差 ≤ 3 ⇒ 那一点是透的」，在两发都没读到时差是 0，
/// 于是<b>印成「透的」</b>。<c>.scratch/keeper-debt/issues/08-frames-never-grabbed.md</c>、<c>09-outline-rules-not-drawn.md</c> 两张工单的判据全是「抓一帧再读像素」——
/// 那两条的验收面本身能假绿。这一发把「读不到」抬成接口上的一档，假绿就过不去了。
///
/// <b>差多少算「对上了」，那个数不在这里</b>：那是各个调用方自己的判据（日历那两条是 6 和
/// 「源图那一块的起伏 + 2」，透明度那条是 3，遮罩那两条是 8 和 <c>InkThreshold</c>），
/// 由调用方带名字递进来。<c>Frames</c> 只负责读和减。
///
/// 三份混色收成一份的那一步是<b>证过</b>的，不是看着像：见 <see cref="SelfTest"/> 里那片扫遍
/// 256×256×101 的断言 —— <c>FadeRun.Mixed</c> 走的是 <c>Fader.Rgb</c> 还回来的 <c>byte/255f</c>
/// 再乘回 255，跟测量程序那份直接用字节的写法在 float 下不是显然相等的，所以扫一遍钉死。
///
/// <b>为什么是 internal</b>：接口上露的是 <c>Win32.Point</c> 和 <c>Win32.Pixel</c>，而 <c>Win32</c>
/// 整个类是 internal 的。把它抬成 public 等于往 Win32 的公开面上再加两个类型 ——
/// 而「Win32 的公开面太宽」正是第二遍体检卡 8 要收的方向，不能一边收一边放。</summary>
internal static class Frames
{
    // ---- 一个点的实测结果 ---------------------------------------------------------

    /// <summary>一个点读回来是什么。<see cref="Read"/> 是假的那一档就是<b>没读到</b>，
    /// 这时候 <see cref="Pixel"/> 没有意义（是 <c>default</c>），任何拿它去减的判据都会
    /// 从 <see cref="Gap(Frames.Sample, Frames.Sample)"/> 那边拿到 <see cref="int.MaxValue"/>
    /// —— 也就是<b>失败关闭</b>：现有那些 <c>差 &lt;= N</c> 的写法一个都不用改，自动判不过。</summary>
    public readonly record struct Sample(Win32.Point At, bool Read, Win32.Pixel Pixel)
    {
        public static Sample Of(Win32.Point at, Win32.Pixel pixel) => new(at, true, pixel);
        public static Sample Unreadable(Win32.Point at) => new(at, false, default);

        /// <summary>印出来跟原来那份 <c>Win32.Pixel.ToString()</c> 逐字相同 —— 读到的是
        /// <c>(R,G,B)</c>，这样搬家不动任何一条已有的实测结果。没读到印 <c>(读不到)</c>，
        /// 那是<b>新的一档</b>：原来这一格印的是 <c>(0,0,0)</c>，跟真读到黑一模一样。</summary>
        public override string ToString() => Read ? Pixel.ToString() : "(读不到)";
    }

    /// <summary>取样计划里的一点：一个名字加一个屏幕坐标。名字是调用方带来的，
    /// 因为实测结果印出来要认得出是哪一点（「抬头那一带」「左上那格」），那是各个测量程序自己的话。</summary>
    public readonly record struct Want(string Where, Win32.Point At);

    // ---- 采样 -----------------------------------------------------------------

    /// <summary>合成之后的桌面在一个物理像素上是什么。<b>进程内读</b>是有意的：
    /// 外部那个 DPI-unaware 的读者拿到的是虚拟化的坐标，量的是另一个像素 ——
    /// 就是当年把方案里 DPI 那一行读错的那个假象。</summary>
    public static Sample Shot(Win32.Point at)
    {
        var dc = Win32.GetDC(IntPtr.Zero);
        try
        {
            return Decode(at, Win32.GetPixel(dc, at.X, at.Y));
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>一份计划里的每一点各读一次，顺序就是计划里的顺序。</summary>
    public static List<Sample> Shot(IEnumerable<Want> plan)
    {
        var read = new List<Sample>();
        foreach (var want in plan) read.Add(Shot(want.At));
        return read;
    }

    /// <summary><c>GetPixel</c> 还回来那个数怎么读：<c>0x00BBGGRR</c>，而 <c>CLR_INVALID</c>
    /// （全 1）是「读不到」。拆出来是因为这一条正是这个模块存在的理由，它得能被自检单独问到
    /// —— 自检里没有屏幕，但自检可以递一个 <c>0xFFFFFFFF</c> 进来。</summary>
    internal static Sample Decode(Win32.Point at, uint raw) => raw == 0xFFFFFFFF
        ? Sample.Unreadable(at)
        : Sample.Of(at, new Win32.Pixel((byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF)));

    // ---- 比色 -----------------------------------------------------------------

    /// <summary>一次比较的三种结论。第三档是这一发新加的：原来只有「差小」和「差大」，
    /// 而「两发都没读到」落在「差小」里，于是假绿。</summary>
    public enum Verdict { Same, Different, Unreadable }

    /// <summary>最大的单通道差，8 位刻度。屏幕实测结果不是精确的 —— DWM 会取整、桌面自己会抖动 ——
    /// 所以这是一个容差的量，不是「有几个不匹配」。</summary>
    public static int Gap(Win32.Pixel a, Win32.Pixel b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>有一边没读到就是 <see cref="int.MaxValue"/>，不是一个数 —— 这样任何
    /// <c>差 &lt;= N</c> 的判据都失败关闭。<b>不许</b>在这里退成 0：那就是原来那条假绿。</summary>
    public static int Gap(Sample a, Sample b) => a.Read && b.Read ? Gap(a.Pixel, b.Pixel) : int.MaxValue;

    /// <summary>读回来的那一点跟一个<b>算出来</b>的期望色比。期望色总是「读到了」的 ——
    /// 它是混色模型给的，不是屏幕给的，所以这一档只看 <paramref name="a"/>。</summary>
    public static int Gap(Sample a, Win32.Pixel expected) => a.Read ? Gap(a.Pixel, expected) : int.MaxValue;

    /// <summary>印在实测结果里的那个「差=N」。没读到时印「读不到」，不印 2147483647 ——
    /// 实测结果是人读的，一个哨兵值印出来等于没说。</summary>
    public static string GapText(Sample a, Sample b) =>
        a.Read && b.Read ? Gap(a.Pixel, b.Pixel).ToString() : "读不到";

    public static Verdict Diff(Sample a, Sample b, int tolerance) =>
        !a.Read || !b.Read ? Verdict.Unreadable
            : Gap(a.Pixel, b.Pixel) <= tolerance ? Verdict.Same : Verdict.Different;

    /// <summary>一帧 RGBA 缓冲里 <paramref name="offset"/> 那一点（三个通道，跳过 alpha）
    /// 与一个「该是什么色」的差。<c>App.FrameResidual</c> 那一条判据用的就是这一发。</summary>
    public static int Gap(byte[] rgba, int offset, int r, int g, int b) =>
        Math.Max(Math.Abs(rgba[offset] - r), Math.Max(Math.Abs(rgba[offset + 1] - g), Math.Abs(rgba[offset + 2] - b)));

    /// <summary>两帧 RGBA 缓冲同一点的差。<c>App.FrameDifference</c> 那一条用的就是这一发。</summary>
    public static int Gap(byte[] a, int offsetA, byte[] b, int offsetB) =>
        Math.Max(Math.Abs(a[offsetA] - b[offsetB]),
            Math.Max(Math.Abs(a[offsetA + 1] - b[offsetB + 1]), Math.Abs(a[offsetA + 2] - b[offsetB + 2])));

    // ---- 混色 -----------------------------------------------------------------

    /// <summary>两种混法。8 位那份是「把拿到的编码值直接线性插值」，线性光那份是
    /// 「解码到线性光、混、再编码回去」—— 同样的输入，在白色上差一整档。</summary>
    public enum Model { Srgb8, LinearLight }

    /// <summary>一个桌面通道值上盖一层 alpha 的 <paramref name="above"/>，混出来是哪个数。
    /// 取整走 <see cref="Math.Round(double)"/>（banker's rounding）—— 这是原来两份实施
    /// 各自的写法，搬家不改它。</summary>
    public static int BlendChannel(int below, int above, float alpha) =>
        (int)Math.Round(below * (1 - alpha) + above * alpha);

    public static int LinearChannel(int below, int above, float alpha) =>
        ToSrgb8(ToLinear(below) * (1 - alpha) + ToLinear(above) * alpha);

    public static Win32.Pixel Blend(Win32.Pixel below, Win32.Pixel above, float alpha, Model model)
    {
        Func<int, int, float, int> mix = model == Model.LinearLight ? LinearChannel : BlendChannel;
        return new Win32.Pixel(
            (byte)mix(below.R, above.R, alpha),
            (byte)mix(below.G, above.G, alpha),
            (byte)mix(below.B, above.B, alpha));
    }

    private static double ToLinear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static int ToSrgb8(double linear)
    {
        var c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(c, 0, 1) * 255);
    }

    // ---- 自检 -------------------------------------------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf PixelRuler = new(
        () => "读像素那一把尺子通过：CLR_INVALID 是「没读到」不是黑色，两发都没读到不判成「透的」，" +
              "比色跟原来两份逐字相同，三份混色收成一份且在 256×256×101 那片输入上逐点相同，" +
              "线性光那一份钉在 188",
        "读像素那一把尺子", SelfTest);

    /// <summary>这一组守的是<b>搬家没搬歪</b>，不是屏幕。屏幕那一侧的实测结果在各测量程序手里
    /// （<c>--shot</c> 的透明度链、<c>--fader-shot</c> 的遮罩、测量程序的 alpha 那一段），
    /// 自检里没有屏幕，所以自检只能问纯算术和「读不到」这一档的形状。
    ///
    /// 第 3 条那片扫描是这一组里最贵的一条（256×256×101 ≈ 660 万次），量的是
    /// <c>FadeRun.Mixed</c> 原来那条 <c>round(from*(1-a) + (byte/255f)*255f*a)</c>
    /// 跟合并之后这条 <c>round(from*(1-a) + byte*a)</c> 在整个输入域上逐点相同 ——
    /// float 下这两条不是显然相等的，看着像不算证。</summary>
    public static List<string> SelfTest()
    {
        var problems = new List<string>();
        var zero = new Win32.Point { X = 0, Y = 0 };

        // 1. CLR_INVALID 不再折成一个合法颜色，而黑还是黑 —— 这两条一起才是「分得出来」。
        var invalid = Decode(zero, 0xFFFFFFFF);
        if (invalid.Read) problems.Add("CLR_INVALID 读回来还算读到了，那一份假绿没堵住");
        var black = Decode(zero, 0x00000000);
        if (!black.Read || black.Pixel != new Win32.Pixel(0, 0, 0))
            problems.Add("真读到黑那一点没读成 (0,0,0)");

        // 2. 两发都没读到不许判成「相同」—— 这正是 AlphaMeasurement 那条假绿路的形状。
        var both = Diff(Sample.Unreadable(zero), Sample.Unreadable(zero), 3);
        if (both != Verdict.Unreadable) problems.Add($"两发都没读到判成了 {both}，差 0 又假绿成「透的」");
        if (Gap(Sample.Unreadable(zero), Sample.Unreadable(zero)) <= 3)
            problems.Add("没读到的差落在容差里，任何「差 ≤ N」的判据都会失败打开");

        // 3. 「读到黑」和「读不到」在接口上是两档。
        if (Diff(black, Sample.Unreadable(zero), 3) != Verdict.Unreadable)
            problems.Add("读到黑和读不到在接口上还是同一档");

        // 4. 「差多少算对上了」那个数是调用方递进来的，不是写在这里的。
        var a = Sample.Of(zero, new Win32.Pixel(10, 10, 10));
        var b = Sample.Of(zero, new Win32.Pixel(14, 10, 10));
        if (Diff(a, b, 3) != Verdict.Different || Diff(a, b, 6) != Verdict.Same)
            problems.Add("同一对实测结果，差 4，给 3 和给 6 判成同一档 —— 那个数没被递进来");

        // 5. 比色跟原来那两份（Win32.ChannelDifference 与测量程序的 Err）逐字相同。
        for (var r = 0; r <= 255; r += 17)
        for (var r2 = 0; r2 <= 255; r2 += 13)
        {
            var x = new Win32.Pixel((byte)r, (byte)(255 - r), (byte)(r / 2));
            var y = new Win32.Pixel((byte)r2, (byte)(255 - r2), (byte)(r2 / 2));
            var want = Math.Max(Math.Abs(x.R - y.R), Math.Max(Math.Abs(x.G - y.G), Math.Abs(x.B - y.B)));
            if (Gap(x, y) != want)
            {
                problems.Add($"比色在 {x}/{y} 上是 {Gap(x, y)}，原来那条公式给 {want}");
                r = 256;
                break;
            }
        }

        // 6. 三份混色收成一份：FadeRun 那条走 byte/255f 再乘回 255 的写法，跟直接用字节的
        //    这一份在整个输入域上逐点相同。
        var drifted = 0;
        for (var fill = 0; fill <= 255 && drifted == 0; fill++)
        {
            var asFloat = (fill / 255f) * 255f;   // Fader.Rgb 还回来的那一档，FadeRun 原来乘的就是它
            for (var below = 0; below <= 255 && drifted == 0; below++)
            for (var pct = 0; pct <= 100; pct++)
            {
                var alpha = pct / 100f;
                var was = (int)Math.Round(below * (1 - alpha) + asFloat * alpha);
                if (was == BlendChannel(below, fill, alpha)) continue;
                problems.Add($"混色在 below={below} fill={fill} alpha={alpha:F2} 上漂了：原来 {was}，现在 {BlendChannel(below, fill, alpha)}");
                drifted++;
            }
        }

        // 7. 线性光那一份钉一个数：黑上盖一半白的alpha，两种模型差一整档（128 对 188），
        //    所以这一条能抓住「模型被换掉」而抓不到「两个都错了」。
        var mid = Blend(new Win32.Pixel(0, 0, 0), new Win32.Pixel(255, 255, 255), 0.5f, Model.LinearLight);
        if (mid.R != 188) problems.Add($"线性光混一半白读到 {mid.R}，该是 188（8 位那份会给 128）");
        var naive = Blend(new Win32.Pixel(0, 0, 0), new Win32.Pixel(255, 255, 255), 0.5f, Model.Srgb8);
        if (naive.R != 128) problems.Add($"8 位混一半白读到 {naive.R}，该是 128");

        return problems;
    }
}
