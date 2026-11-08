using System;

namespace DebugProbe;

/// <summary>
/// 每一发结束时固定输出四行：一行不多，一行不少。
///
/// <para>四行的次序是这一族活下来的原因 —— 这一族栽过的地方，
/// 一半是「拿了一堆读数，事后说不清哪条对应哪个结论」。所以判据那一行
/// <b>在开跑之前就写下来</b>（<see cref="Begin"/> 一构造就印），
/// 跑完了再补的那种不算数。</para>
///
/// <para>「配的图」那一行还没出图时写「无」，不许留空。</para>
///
/// <para>用法：<c>using (Shot shot = Shot.Begin(名字, 判据)) { ... shot.Finish(结果, 路径); }</c>。
/// 忘了调 <see cref="Finish"/> 也不会少那一行 —— <see cref="Dispose"/> 会替它写「中止」，
/// 因为「跑崩了也照样是四行」比「崩了半截就没了」好查。</para>
/// </summary>
internal sealed class Shot : IDisposable
{
    private const string NoShot = "无";
    private bool _finished;

    private Shot(string name, string criterion)
    {
        // 判据写在开跑之前。这一行印出去的下一刻才开始量。
        Console.Out.WriteLine($"这一发：{name}");
        Console.Out.WriteLine($"判据：  {criterion}");
    }

    /// <summary>开一发。返回的东西一 <c>Dispose</c> 就结束这一发。</summary>
    internal static Shot Begin(string name, string criterion)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名字不能空。", nameof(name));
        if (string.IsNullOrWhiteSpace(criterion)) throw new ArgumentException("判据不能空。", nameof(criterion));
        return new Shot(name, criterion);
    }

    /// <summary>写后两行，然后这一发就结束了。</summary>
    internal void Finish(string result, string screenshot)
    {
        if (_finished) throw new InvalidOperationException("这一发已经结束了。");
        _finished = true;
        Console.Out.WriteLine($"结果：  {result}");
        Console.Out.WriteLine($"配的图：{(string.IsNullOrWhiteSpace(screenshot) ? NoShot : screenshot)}");
    }

    public void Dispose()
    {
        // 半路崩了也补齐后两行。四行格式在出错的那一发上照样成立。
        if (_finished) return;
        _finished = true;
        Console.Out.WriteLine("结果：  中止 —— 这一发没跑完");
        Console.Out.WriteLine($"配的图：{NoShot}");
    }
}