namespace Keeper;

/// <summary>检测那一族每一帧的<b>装配口</b> —— <c>App</c> 只调这一个。
///
/// 装配在这一层，取数在各自那一族里：输入三问由 <paramref name="input"/> 那一侧给（它本来就在
/// 0.2 秒那一发的轮询里），独占全屏问 <see cref="Fullscreen.OwnsScreen"/>、前台应用问
/// <see cref="ForegroundApp"/>。<b>政策不在这一层</b>（<c>docs/adr/0006-detection-module-boundary.md</c>
/// 「模块只出实测结果，政策在消费方」那一节）：翻倍、让位、静默全在 <see cref="Scheduler"/> 那一侧，
/// 它拿这个聚合和 settings 的钥匙合成行为，和今天 <c>blocked</c> + <c>blockWhenFullscreen</c> 同款。
///
/// <b>为什么 <paramref name="foreground"/> 和 <paramref name="dnd"/> 是递进来而不是这里问的</b>：
/// 勿扰要读用户配置（时段表、exe 名单），而这一层<b>不认识声明表</b> —— 放进模块就是让感知层
/// 多一条通向磁盘的路（同一张卡里「否掉的：条件求值进模块」那条）。所以前台那一发先在
/// <c>App</c> 那一侧问出来（<see cref="ForegroundApp"/>），勿扰位由 <c>App</c> 拿设置和它求值
/// （<see cref="Dnd"/>），两样都在这里<b>装配进同一个聚合</b> —— 装配仍然只有这一处。
///
/// <b>「独占全屏那一发为什么没来」那一行日志要读的正是同一处取材</b>，所以它跟着
/// <see cref="FullscreenMeasurement"/> 走 —— 实测结果一个地方出，日志那一句话原样。
///
/// <c>internal</c> 和 <see cref="Fullscreen"/> 同一档：装配口是给 <c>App</c> 调的，不是对外的实测结果。</summary>
internal static class Detection
{
    /// <summary>这一帧的实测结果聚合。搭 <c>App._Process</c> 那 0.2 秒一发，不发事件、不开线程
    /// （同卡「前台取数搭现有轮询底盘」那一节：<c>GetForegroundWindow</c> 那一发本来就在问）。</summary>
    public static Detected Now(Input input, Foreground.Measurement foreground, Dnd.Measurement dnd) => new(
        input.IdleMs, input.ContinuousMs, input.BusyMs,
        Fullscreen.OwnsScreen(), foreground, dnd);

    /// 前台应用那一发（<c>App</c> 那一侧问，然后递给 <see cref="Now"/>）。**问一次**：
    /// 勿扰要用它求值，聚合里也是同一格，所以这一发只有一个出处。
    public static Foreground.Measurement ForegroundApp() => Foreground.Now();

    /// <summary>独占全屏那一发为什么是这个答案（一行日志的实测结果）。
    /// <b>不进聚合</b>：聚合是状态机读的，这一行是人和日志读的，两边各有各的形状。</summary>
    public static string FullscreenMeasurement() => Fullscreen.Measurement();

    /// <summary>前台应用那一行实测结果（<paramref name="measurement"/> 是聚合里那一格）。
    /// <b>句法归这一族管</b>：怎么把一发实测结果写成一句话，是模块的事 ——
    /// 所以 <c>App</c> 那一段递实测结果、不自己拼句子（这一格和上面那一格同款，<b>不进聚合</b>）。</summary>
    public static string ForegroundMeasurement(Foreground.Measurement measurement) => Foreground.Describe(measurement);
}
