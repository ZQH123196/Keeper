namespace DebugProbe;

/// <summary>
/// 输入那一路通不通的自查。<b>不是量产品，是量量具自己</b> ——
/// 发出去的按键要是根本没到系统手上，「Win+D 没用」这句话就答错了对象。
///
/// <para>2026-10-12 头一次跑 <c>clean</c> 时栽在这儿：<c>SendInput</c> 答「插进去 4 条」，
/// 而 z 序一动不动。那一句说的是「系统不理 Win+D」还是「按键根本没进去」，
/// 当时分不出来 —— 缺的就是这一发。</para>
///
/// <para><b>判据不能靠开始菜单那一扇</b>：那一版就是这么写的，跑出来「进程 0 个」——
/// 这一台机器上 <c>StartMenuExperienceHost</c> 压根不常驻，于是判据永远答「没到」。
/// 量具坏了，机制没坏。</para>
///
/// <para>所以判据换成 <see cref="Input.Arrives"/>：自己挂一个低级键盘钩子，
/// 两条路各发一枚没人按的键。两条路都响才算这一族的量具自己合格。</para>
/// </summary>
internal static class InputProbe
{
    internal static int Run(string[] args)
    {
        // --wait N：请人在那 N 秒里按几个键。真人按的键和注入的键在钩子上是分得开的
        //（一个带「真人」标记、一个带「注入」标记）。这是唯一能把
        //「钩子坏了」和「注入被挡了」彻底分开的读数 —— 机器自己发不出真人那一种。
        int wait = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--wait" && int.TryParse(args[i + 1], out int n)) wait = n * 1000;
        }

        if (wait > 0) return WaitForHuman(wait);

        using Shot shot = Shot.Begin(
            "输入那一路通不通",
            "自己挂一个低级键盘钩子，两条路各发一枚没人按的键 —— 两条都响就说注入的按键真的走过了系统输入队列；" +
            "哪一条不响，「Win+D 没用」那句话就得改挂在哪一条头上");

        bool arrives = Input.Arrives(out string detail);

        // **注入的按键是发给前台那一扇的。** 前台那一扇的完整性级别比注入者高的话，
        // UIPI 会把那一下吃掉 —— 而吃掉了和「系统不理合成输入」在钩子上长得一模一样。
        // 不把这一句摆出来，「0 命中」会被挂到错的那条原因上。
        string front = Windows.ForegroundDescription();

        shot.Finish(
            $"{detail}；前台那一扇：{front}；结论：{(arrives ? "两条路都通，注入的按键走过了系统输入队列" : "注入的按键没被系统认 —— 发出去的按键没到系统手上")}",
            "无");

        return arrives ? 0 : Find.Refused;
    }

    private static int WaitForHuman(int milliseconds)
    {
        using Shot shot = Shot.Begin(
            "输入那一路通不通（等人按键）",
            $"在 {milliseconds / 1000} 秒里请人随手按几个键 —— 钩子记到「真人」那一条就说钩子是好的，"+
            "而同一段时间里注入的那一枚没被记到，就说明挡在注入那一路上的是系统，不在量具身上");

        bool arrives;
        string detail;
        using (Input.KeyboardHook hook = new())
        {
            if (!hook.Installed)
            {
                shot.Finish($"中止 —— 钩子装不上，错误码 {hook.InstallError}", "无");
                return Find.Refused;
            }

            Input.SendKeyPair(0, Input.F12Key);
            hook.PumpFor(milliseconds);
            arrives = Input.Arrives(out detail);
            detail = detail + $"；这一段钩子记到：{(hook.Seen.Count == 0 ? "一条也没有" : string.Join("、", hook.Seen))}";
        }

        // **注入的按键是发给前台那一扇的。** 前台那一扇的完整性级别比注入者高的话，
        // UIPI 会把那一下吃掉 —— 而吃掉了和「系统不理合成输入」在钩子上长得一模一样。
        // 所以这一句要摆出来，不然「0 命中」会被挂到错的那条原因上。
        string front = Windows.ForegroundDescription();

        shot.Finish(
            $"{detail}；前台那一扇：{front}；结论：{(arrives ? "两条路都通" : "注入的那一枚没被记到")}",
            "无");

        return arrives ? 0 : Find.Refused;
    }
}