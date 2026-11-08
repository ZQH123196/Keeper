using System;
using System.Threading;

namespace Keeper;

/// <summary>单实例这道锁。托盘那一颗图标的全部来历就是「一个进程挂一份」，所以
/// 「托盘里冒出两个 Keeper」从来不是托盘画重了，是有两发 Keeper 在跑 —— 2026-09-27 那次
/// 量到屏幕上确实两颗，进程表里也确实是两发（PID 27244、12988，同一份 <c>--data</c> 目录，
/// 隔了 49 分钟起来），而 <c>src/</c> 里一个守卫都没有。
///
/// <b>为什么是具名互斥体，不是 PID 锁文件</b>：锁归内核持有，进程一没它就自动放开 ——
/// 正常退、崩溃、还是开发时一发 <c>taskkill /F</c> 强杀，全都同一个结果。锁文件是这条规矩
/// 的反面：强杀之后它还留在盘上，下一发读到的是一把没人持有的锁，于是「重启没反应」，
/// 而那些残留 PID 隔几天还可能被系统分给别的程序。这一台机器上量过：强杀掉的那一发
/// 连托盘图标都是 shell 自己收掉的（通知区实测结果从 2 颗到 0 颗），没有残影要清，
/// 所以剩下的唯一一步就是别让第二发起起来。互斥体名字那一条记在 CODING_STANDARDS.md（<c>Local\</c> 前缀）。
///
/// <b>名字带 <c>Local\</c> 前缀</b>：不写前缀时 .NET 把裸名字当成 <c>Global\</c>，那是整个系统
/// 一把锁 —— 一路 RDP 上去，控制台上那一发就把远程会话里这一发挡住了。托盘界面本来就是
/// 每个登录会话一份，所以锁也跟着每个会话一份。</summary>
internal static class Instance
{
    public const string MutexName = @"Local\Keeper.Tray.Instance";

    /// 拿着锁的那一半句柄。留着引用是要它和进程同生共死：一旦托管对象被 GC 收走，
    /// 锁就在程序还活着的时候开了 —— 那正是这个文件要挡住的那件事。
    private static Mutex? _held;

    /// 这一发是不是第一个起来的。<b>必须先于托盘构造</b>：图标是 NIM_ADD 那一刻挂上去的，
    /// 等发现已经有人在跑再把图标收掉，中间那一瞬就是两颗。
    public static bool TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // 锁不是我们的：关掉自己这一半句柄就好，绝不能 ReleaseMutex —— 那会把
            // 正在跑的那一发的锁还给内核，第三发就趁虚进来了。
            mutex.Dispose();
            return false;
        }
        _held = mutex;
        return true;
    }

    /// <summary>把「有人又启动了一发」递给正在跑的那一发：按托盘那扇窗的<b>类名</b>找到它，
    /// 发一条我们自己定的消息。递不到也照样要退 —— 这一发不该有图标，
    /// 哪怕正在跑的那一发已经僵到不认消息了。
    ///
    /// <b>只认类名，标题那一位递真 <c>null</c></b>：2026-09-27 这一发量过，对着
    /// <c>("KeeperTray", "")</c> 问是找不到的（空串是「标题等于空」，不是「不比标题」），
    /// 而按标题找还得先弄清那扇窗上到底写着什么 —— 类名是 <see cref="Tray"/> 自己注册的，稳。</summary>
    public static bool HandOff(out string measurement)
    {
        var hwnd = Win32.FindWindow(Tray.ClassName, null);
        if (hwnd == IntPtr.Zero)
        {
            measurement = $"锁被占着，可按类名 '{Tray.ClassName}' 找不到那扇窗 —— 递不出去";
            return false;
        }
        var posted = Win32.PostMessage(hwnd, Win32.WmSecondInstance, IntPtr.Zero, IntPtr.Zero);
        measurement = $"句柄 0x{hwnd.ToInt64():X} PostMessage={posted}";
        return posted;
    }
}
