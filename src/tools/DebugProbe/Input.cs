using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DebugProbe;

/// <summary>
/// 系统那一层的输入。<b>发真的按键和真的鼠标，不是调一个内部函数</b> ——
/// 用户按的是键，量具要发的是同一个键。
///
/// <para>出处：<c>.scratch/debug-tool/SPEC.md</c> 第四节「模拟哪些动作」。</para>
/// </summary>
internal static class Input
{
    /// <summary><c>SendInput</c> 那一路发的那一枚键：<c>VK_F12</c> = <b>0x7E</b>。
    ///
    /// <para><b>原来这一处的注释写着「F13/F14」</b>，而这两个数是
    /// <c>0x7E</c> / <c>0x7D</c> —— 那是 <c>F12</c> 和 <c>F11</c>。注释和常量对不上，
    /// 照注释去查键码表会查错一行（2026-10-13 改）。</para>
    ///
    /// <para>选它们是因为键盘上挨着的那几个功能键多半没人按，
    /// 免得这一发撞上真人在打字。<b>选哪一枚只是「多半没人按」，不是「不会有人按」</b> ——
    /// 真撞上了钩子那一栏的实测结果里会混进真人那一发，所以判据按 vk 码认、不按名字认。</para>
    /// </summary>
    internal const ushort F12Key = 0x7E;

    /// <summary><c>keybd_event</c> 那一路发的那一枚：<c>VK_F11</c> = <b>0x7D</b>。
    /// 两枚挨着，两路走的是不同的键 —— 这一族要答的是「两路哪一种通」。</summary>
    internal const ushort F11Key = 0x7D;

    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventExtended = 0x0001;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    /// <summary><c>MOUSEEVENTF_RIGHTDOWN</c> / <c>RIGHTUP</c>。
    /// 值和左边那一对一样是量出来的（<c>winuser.h</c>，<c>INPUT_MOUSE</c> 那一段）。
    /// <b>托盘那一发要的就是右键</b>：产品没设版本号，shell 按老协议发，
    /// 左键切预览、右键出菜单（<c>src/Tray.cs</c> 的 <c>OnMessage</c>）。</summary>
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    /// <summary>
    /// <c>MSG</c>。<b>摆在两个钩子共用的那一层</b> —— 各摆一份的话，
    /// 改了布局只改到一半，那一半的读数会像模像样而全是错的。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        internal IntPtr Handle;
        internal uint Message;
        internal IntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X;
        internal int Y;
    }

    /// <summary>
    /// 低级钩子靠消息泵吃饭：不泵，钩子永远不会响。
    /// <b>必须用 <c>PeekMessage</c>，不能用 <c>GetMessage</c></b> ——
    /// 后者没有消息时是阻塞的，而调用方整段是靠外层时限兜着的（量于 10-12）。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    // 钩子那三样也摆在共用的一层：键盘钩子和鼠标钩子都要用，
    // 各摆一份的话 P/Invoke 的签名改了一边就分岔，而分岔的那一边不报错。

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, IntPtr callback, IntPtr module, uint threadId);

    /// <summary>两个钩子的回调签名一样，各自声明了一份委托类型。
    /// 这里按 <see cref="IntPtr"/> 收，两边传的都是自己那份委托的函数指针 ——
    /// 委托对象本身由各自的字段抓着，不会被回收。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    private const ushort VkLeftWin = 0x5B;
    private const ushort VkD = 0x44;
    private const ushort ScanLeftWin = 0x5B;
    private const ushort ScanD = 0x46;

    /// <summary>上一次 <see cref="SendInput"/> 真插进去几条。0 就是一条都没进去。</summary>
    internal static uint LastInserted { get; private set; }

    /// <summary>上一次 <see cref="SendInput"/> 的 Win32 错误码。</summary>
    internal static int LastError { get; private set; }

    /// <summary>这一发走哪一条路：虚拟键，还是扫描码。两个都要量，不许猜。</summary>
    internal enum Style
    {
        VirtualKey,
        ScanCode,

        /// <summary>一条一条分开送，每一条之间停一停。</summary>
        Split,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    /// <summary>
    /// 发一次真的 Win+D。
    ///
    /// <para>Win+D 有动画，所以发完不等于落定 —— 等它稳下来是另一件事，
    /// 见 <see cref="Clean"/>，那一发不等就去读会把动画中途读成「窗没了」。</para>
    /// </summary>
    internal static string SendWinD(Style style = Style.VirtualKey)
    {
        // 分成四发送出去：四条一次塞进去的时候，Win 那一条和 D 那一条落在同一趟处理里，
        // 全局热键那一层认不出来 —— 「插进去 4 条」而 z 序一动不动，就是这个形状。
        if (style == Style.Split)
        {
            uint total = 0;
            foreach ((ushort key, uint flags) in new (ushort, uint)[]
                     {
                         (VkLeftWin, 0),
                         (VkD, 0),
                         (VkD, KeyEventKeyUp),
                         (VkLeftWin, KeyEventKeyUp),
                     })
            {
                total += Send(new[] { Key(key, flags, KeyEventExtended, Style.VirtualKey) });
                Thread.Sleep(60);
            }

            LastInserted = total;
            LastError = 0;
            return $"走分开一条路：四发分开送，一共插进去 {total} 条，每发之间停 60 毫秒";
        }

        ushort winKey = style == Style.ScanCode ? ScanLeftWin : VkLeftWin;
        ushort dKey = style == Style.ScanCode ? ScanD : VkD;
        uint winFlag = style == Style.ScanCode ? KeyEventScanCode : 0;
        uint dFlag = style == Style.ScanCode ? KeyEventScanCode : 0;

        // 按下 → 按下 → 松开 → 松开，四条一起发。Win 那一条带扩展位（它是左 Win，不是小键盘那颗）。
        var inputs = new[]
        {
            Key(winKey, 0, winFlag | KeyEventExtended, style),
            Key(dKey, 0, dFlag, style),
            Key(dKey, KeyEventKeyUp, dFlag, style),
            Key(winKey, KeyEventKeyUp, winFlag | KeyEventExtended, style),
        };
        uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        LastInserted = inserted;
        LastError = inserted == 0 ? Marshal.GetLastWin32Error() : 0;
        return $"走{Describe(style)}一条路：送 {inputs.Length} 条，插进去 {inserted} 条，错误码 {LastError}";
    }

    /// <summary>「按住 A 再按一下 B」那一种组合键。</summary>
    /// <summary>「按住 A 再按一下 B」那一种组合键。不带修饰键时就是单键一下。</summary>
    internal static uint SendKeyPair(ushort modifier, ushort key)
    {
        if (modifier == 0)
        {
            return Send(new[] { Key(key, 0, 0, Style.VirtualKey), Key(key, KeyEventKeyUp, 0, Style.VirtualKey) });
        }

        return Send(new[]
        {
            Key(modifier, 0, KeyEventExtended, Style.VirtualKey),
            Key(key, 0, 0, Style.VirtualKey),
            Key(key, KeyEventKeyUp, 0, Style.VirtualKey),
            Key(modifier, KeyEventKeyUp, KeyEventExtended, Style.VirtualKey),
        });
    }

    /// <summary>发一枚键，走 <c>keybd_event</c> 那条老路。
    ///
    /// <para>它是这一族的<b>对照</b>：<c>SendInput</c> 答「插进去 N 条」而钩子一声不响时，
    /// 光凭那一发答不出「钩子坏了」还是「<c>SendInput</c> 那条路被挡了」。
    /// 两条路各发一枚同一个键，哪一条在钩子上响，答案就落在哪一条上。</para>
    /// </summary>
    internal static void SendKeyLegacy(ushort key)
    {
        keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event((byte)key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    // ---- 那一路通不通（带对照） ------------------------------------------------------

    /// <summary>注入的按键有没有真的走过系统输入队列。</summary>
    /// <param name="detail">摊开的读数：完整性级别、两条路各自记到几条。</param>
    /// <returns>走过了才算 true。</returns>
    internal static bool Arrives(out string detail)
    {
        List<string> viaLegacy;
        List<string> viaSendInput;
        bool legacyArrived;
        bool sendInputArrived;
        using (KeyboardHook hook = new())
        {
            if (!hook.Installed)
            {
                detail = $"低级键盘钩子装不上，错误码 {hook.InstallError}，这一问答不了";
                return false;
            }

            SendKeyLegacy(F11Key);
            hook.PumpFor(700);
            viaLegacy = hook.Seen;
            hook.Clear();

            SendKeyPair(0, F12Key);
            hook.PumpFor(700);
            viaSendInput = hook.Seen;
            // **按 vk 码认，不按名字认。** 钩子记下来的是 `vk=126（注入）`，
            // 它只带 vk 号、从来不带这一枚键的名字 —— 拿名字去比，比的是一个钩子从不产出的串，
            // 于是这一发<b>永远答「没被系统认」</b>，它从来没可能答对过（量于 10-12）。
            legacyArrived = Saw(viaLegacy, F11Key);
            sendInputArrived = Saw(viaSendInput, F12Key);
        }

        detail =
            $"本进程完整性级别 {Windows.IntegrityLevel()}；" +
            $"对照（keybd_event 发 F11 vk={F11Key}）" +
            $"钩子记到 {viaLegacy.Count} 条、其中认出来 {viaLegacy.Count(s => Saw(new[] { s }, F11Key))} 条：{Show(viaLegacy)}；" +
            $"被测（SendInput 发 F12 vk={F12Key}）" +
            $"钩子记到 {viaSendInput.Count} 条、其中认出来 {viaSendInput.Count(s => Saw(new[] { s }, F12Key))} 条：{Show(viaSendInput)}";
        return legacyArrived && sendInputArrived;
    }

    /// <summary>钩子记到的那几条里有没有这一枚。钩子记的格式是 <c>vk=126（注入）</c>，
    /// 所以认的是前缀上的 vk 号 —— 认错格式和认错名字一样，都会让这一发永远答不出来。</summary>
    private static bool Saw(IEnumerable<string> seen, ushort vk) =>
        seen.Any(s => s.StartsWith($"vk={(int)vk}", StringComparison.Ordinal));

    private static string Show(List<string> seen) =>
        seen.Count == 0 ? "一条也没有" : string.Join("、", seen.Take(8));

    /// <summary>
    /// 一个低级鼠标钩子。它只做一件事：把走过的鼠标事件记下来。
    ///
    /// <para><b>它是为工单 07 那一行判据存在的</b>：那一行要证「发的是真事件，
    /// 不是往图标层发消息」。往图标层发消息的话，图标层会动，但钩子一声不响 ——
    /// <b>动了 + 钩子响</b>才算真事件走过了系统输入队列。</para>
    ///
    /// <para>记的是 <c>注入</c> 位（<c>LLMHF_INJECTED</c>），不记「真人物理按的那一种」——
    /// 机器自己发不出后者，所以这一族分不开「注入」和「真人」，得分开的地方另有对照。</para>
    /// </summary>
    internal sealed class MouseHook : IDisposable
    {
        private const int WhMouseLl = 14;
        private const uint WmMouseMove = 0x0200;
        private const uint WmLeftButtonDown = 0x0201;
        private const uint WmLeftButtonUp = 0x0202;
        private const uint LlmhfInjected = 0x0001;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseLlHookStruct
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        // 装钩子、卸钩子、往下传那三条走共用那一层，见 Input 上面的那三份声明。

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        private readonly HookProc _callback;
        private readonly IntPtr _handle;
        private readonly List<string> _seen = new();

        internal MouseHook()
        {
            _callback = OnMouse;
            _handle = SetWindowsHookEx(WhMouseLl, Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero, 0);
            if (_handle == IntPtr.Zero) InstallError = Marshal.GetLastWin32Error();
        }

        internal bool Installed => _handle != IntPtr.Zero;

        internal int InstallError { get; }

        /// <summary>按下和松开的条数。<b>移动不记</b> —— 分步移动那一发会记几百条移动，
        /// 而判据要问的是「有没有真的按下去、松开来」。</summary>
        internal int Presses { get; private set; }

        internal int Releases { get; private set; }

        internal int Moves { get; private set; }

        internal void Reset()
        {
            lock (_seen)
            {
                _seen.Clear();
                Presses = 0;
                Releases = 0;
                Moves = 0;
            }
        }

        internal void PumpFor(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                PumpOnce();
                Thread.Sleep(10);
            }
        }

        /// <summary>抽一遍就走，不等。<b>拖放过程中要靠它</b> ——
        /// 钩子的回调跑在装它的线程上，不抽就永远不响。</summary>
        internal void PumpOnce()
        {
            while (PeekMessage(out NativeMessage message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                DispatchMessage(ref message);
            }
        }

        private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
                uint message = (uint)wParam.ToInt64();
                string tag = (info.Flags & LlmhfInjected) != 0 ? "注入" : "非注入";
                lock (_seen)
                {
                    switch (message)
                    {
                        case WmLeftButtonDown:
                            Presses++;
                            _seen.Add($"down({info.X},{info.Y}){tag}");
                            break;
                        case WmLeftButtonUp:
                            Releases++;
                            _seen.Add($"up({info.X},{info.Y}){tag}");
                            break;
                        case WmMouseMove:
                            Moves++;
                            break;
                    }
                }
            }

            return CallNextHookEx(_handle, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) UnhookWindowsHookEx(_handle);
            GC.KeepAlive(_callback);
        }
    }

    /// <summary>
    /// 一个低级键盘钩子。它只做一件事：把走过的按键记下来。
    ///
    /// <para><b>低级钩子靠消息泵吃饭</b> —— 不泵，钩子永远不会响，
    /// 而「钩子一声不响」和「按键没进来」看着是同一个数。判据要靠
    /// <see cref="Arrives"/> 那一发对照才分得开。</para>
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            internal uint VkCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        private readonly HookProc _callback;
        private readonly IntPtr _handle;
        private readonly List<string> _seen = new();

        internal KeyboardHook()
        {
            _callback = OnKey;
            _handle = SetWindowsHookEx(WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero, 0);
            if (_handle == IntPtr.Zero) InstallError = Marshal.GetLastWin32Error();
        }

        internal bool Installed => _handle != IntPtr.Zero;

        internal int InstallError { get; }

        internal List<string> Seen
        {
            get
            {
                lock (_seen)
                {
                    return new List<string>(_seen);
                }
            }
        }

        internal void Clear()
        {
            lock (_seen)
            {
                _seen.Clear();
            }
        }

        internal void PumpFor(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                while (PeekMessage(out NativeMessage message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    DispatchMessage(ref message);
                }

                Thread.Sleep(10);
            }
        }

        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown))
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                bool injected = (info.Flags & 0x10) != 0;
                lock (_seen)
                {
                    _seen.Add($"vk={info.VkCode}{(injected ? "（注入）" : "（真人）")}");
                }
            }

            return CallNextHookEx(_handle, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) UnhookWindowsHookEx(_handle);
            GC.KeepAlive(_callback);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static NativeInput Key(ushort key, uint flags, uint extra, Style style) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)(style == Style.ScanCode ? 0 : key),
                ScanCode = (ushort)(style == Style.ScanCode ? MapVirtualKey(key, 0) : 0),
                Flags = flags | extra,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static string Describe(Style style) => style == Style.ScanCode ? "扫描码" : "虚拟键";

    // ---- 鼠标 -------------------------------------------------------------------

    private static readonly IntPtr VirtualDesktop = new(-1);

    /// <summary>把鼠标移到屏幕某一点，走绝对坐标那条路。</summary>
    internal static void MoveTo(int x, int y)
    {
        (int vx, int vy, int vw, int vh) = VirtualScreen();
        int nx = vw <= 1 ? 0 : Math.Clamp(x * 65535 / (vw - 1), 0, 65535);
        int ny = vh <= 1 ? 0 : Math.Clamp(y * 65535 / (vh - 1), 0, 65535);
        _ = vx;
        _ = vy;
        Send(new[] { MouseMoveEvent(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk) });
    }

    /// <summary>
    /// 虚拟桌面那一片。绝对坐标那一路按<b>整块虚拟桌面</b>归一化，
    /// 不按主屏 —— 多一块屏的时候按主屏算出来的落点会偏。
    /// </summary>
    internal static (int X, int Y, int Width, int Height) VirtualScreen()
    {
        const int SmXVirtualScreen = 76;
        const int SmYVirtualScreen = 77;
        const int SmCxVirtualScreen = 78;
        const int SmCyVirtualScreen = 79;
        return (
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    internal static void LeftDown() => Send(new[] { MouseButtonEvent(MouseLeftDown) });

    internal static void LeftUp() => Send(new[] { MouseButtonEvent(MouseLeftUp) });

    /// <summary>点一下：移到、按、松。<paramref name="settle"/> 是点完停多久，单位毫秒。</summary>
    internal static void ClickAt(int x, int y, int settle)
    {
        MoveTo(x, y);
        Thread.Sleep(settle);
        LeftDown();
        Thread.Sleep(settle);
        LeftUp();
    }

    /// <summary>右键一下：移到、按、松。托盘菜单是右键出来的，左键走的是另一条路。</summary>
    internal static void RightClickAt(int x, int y, int settle)
    {
        MoveTo(x, y);
        Thread.Sleep(settle);
        Send(new[] { MouseButtonEvent(MouseRightDown) });
        Thread.Sleep(settle);
        Send(new[] { MouseButtonEvent(MouseRightUp) });
    }

    /// <summary>
    /// 按下 → <b>分几步挪</b> → 松开。
    ///
    /// <para>不许一步跳过去。图标层认的是轨迹，一步跳过去它不认这次拖放，
    /// 结果是「拖了，没动」—— 而那个结果看着像「产品挡了拖放」。
    /// 停多久从实测来，落进研究笔记。</para>
    /// </summary>
    internal static string Drag(int fromX, int fromY, int toX, int toY, int steps, int settle, Action? pump = null)
    {
        // 每一步都要抽一次消息：低级鼠标钩子的回调是**装它的那个线程**上跑的，
        // 拖的过程里不抽，钩子一声都不响 —— 而「钩子没响」和「没发出去」在读数上一样。
        //
        // <para><b>睡之前抽一次、睡醒再抽一次</b>（量于 10-12）：只抽一次的话，
        // 事件落在睡眠中间那一下会等不到钩子线程去应答，系统等过
        // <c>LowLevelHooksTimeout</c> 就把这一下从钩子链上丢掉。
        // 症状是「松开记到了，按下没记到」—— 两个数不对等，而那一栏正是用来证真事件的。</para>
        void Pump()
        {
            pump?.Invoke();
            Thread.Sleep(settle);
            pump?.Invoke();
        }

        MoveTo(fromX, fromY);
        Pump();
        LeftDown();
        Pump();
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
            Pump();
        }

        LeftUp();
        Pump();
        return $"从 ({fromX},{fromY}) 按下，分 {steps} 步挪到 ({toX},{toY}) 再松开，每一步之间停 {settle} 毫秒";
    }

    private static NativeInput MouseMoveEvent(int x, int y, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                X = x,
                Y = y,
                MouseData = data,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static NativeInput MouseButtonEvent(uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput { Flags = flags, ExtraInfo = IntPtr.Zero },
        },
    };

    /// <summary>
    /// 送一批事件。<b>一条都没插进去要当成读数报出来</b> ——
    /// 2026-10-12 头一次跑 Win+D 时就是它答「插进去 0 条」，而那一发看着像「Win+D 没用」。
    /// </summary>
    private static uint Send(NativeInput[] inputs)
    {
        uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        LastInserted = inserted;
        LastError = inserted == 0 ? Marshal.GetLastWin32Error() : 0;
        return inserted;
    }

    /// <summary>鼠标现在在哪。发完要知道落在哪儿，才知道拖放有没有走完。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    internal static PointI Cursor()
    {
        GetCursorPos(out PointNative p);
        return new PointI(p.X, p.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        internal int X;
        internal int Y;
    }
}