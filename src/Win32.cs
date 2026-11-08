using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Keeper;

/// Win32 surface the probe needs. This is the whole reason for the C# side of
/// the plan: every one of these is a line here rather than a GDExtension.
internal static class Win32
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;

    /// 所有者窗口那一位。取它必须走 GetWindowLongPtrW：GetWindowLongW 在 64 位上返回的是
    /// 32 位 LONG，句柄会被截。
    public const int GwlHwndParent = -8;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const long WsChild = 0x40000000;
    public const long WsVisible = 0x10000000;
    public const long WsPopup = 0x80000000;
    public const long WsOverlappedWindow = 0x00CF0000;

    public const long WsExWindowEdge = 0x00000100;
    public const long WsExLayered = 0x00080000;
    public const long WsExTransparent = 0x00000020;

    /// <c>WS_EX_NOACTIVATE</c> —— 这一扇**不吃激活**：点它不会把前台抢过来，
    /// 而它<b>照样留在命中测试里</b>（那一档就是给「要收鼠标但不许抢前台」用的）。
    ///
    /// <para><b>产品那一族要的是相反的那一头</b>：遮罩那一扇是
    /// <c>Unfocusable</c>（引擎那一侧翻译成这一位 + 焦点那一套），因为不许打断人正在打的
    /// 那一句；这一扇是量具，要的是「收得到点击、但别动别人家的前台」——
    /// 2026-10-10 <c>--swallow</c> 那一发撞到最后一道时读到的：全屏 Chrome 压在上面，
    /// <c>TOPMOST</c> 和 <c>HWND_TOP</c> 各顶一次都仍命中它。</para>
    public const long WsExNoActivate = 0x08000000;
    public const long WsExToolwindow = 0x00000080;
    public const long WsExTopmost = 0x00000008;
    public const long WsExAppwindow = 0x00040000;
    public const long WsExComposited = 0x00000020;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZorder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    /// -1 是 HWND_TOPMOST（一直压在别的窗口上面），不是 HWND_TOP（0，只在顶层那一叠里排到最前）。
    public static readonly IntPtr HwndTopMost = new(-1);
    public static readonly IntPtr HwndNotTopMost = new(-2);

    /// **HWND_TOP（0）** —— 只在<b>顶层那一叠</b>里排到最前，不像 <see cref="HwndTopMost"/>
    /// 那样从此压着一切。
    ///
    /// <para><b>它和 <see cref="HwndTopMost"/> 是一对，不是一个替代品</b>
    /// （2026-10-10 实测撞的，`--swallow` 那一发）：只顶 <c>TOPMOST</c> 时实测读到
    /// 「那个点上是谁='Chrome_WidgetWin_1'」，而接收方在那一摞里明明带 <code>|TOPMOST</code>
    /// —— 那一摞顶端还压着别家的 <c>TOPMOST</c>（实测那一档是
    /// <c>XamlExplorerHostIslandWindow '任务切换'</c>）。<b>Windows 按「那一摞的顶端 + 可见」
    /// 挑赢家</b>，所以还得再顶一次 <c>HWND_TOP</c>。<see cref="DesktopLayer.Lift"/> 那一族
    /// 早就在用这一条（它的注释写着「after 传 0 就是 HWND_TOP」）。</para>
    public static readonly IntPtr HwndTop = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X; public int Y; }

    /// <summary><c>SIZE</c>。<see cref="UpdateLayeredWindow"/> 的 <c>psize</c> 说的是
    /// 源位图多大，不是窗多大 —— 拿老尺寸推出去的是错的那一块。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Size { public int Width; public int Height; }

    /// <summary><c>BLENDFUNCTION</c>。<c>Pack = 1</c>：它本来就是四个字节，不垫就换一副样子。</summary>
    ///
    /// <para><b>两个字段都得填 <see cref="AcSrcAlpha"/></b>：<c>BlendFlags = 0</c>（源图没有 alpha、按
    /// <c>SourceConstantAlpha</c> 混）配上 <c>ConstantAlpha = 0xFF</c>，就是<b>整块不透明</b>。
    /// 那一处曾经是全 0 + <c>0xFF</c>：推进去的 <c>alpha=94</c> 被整块当成 255，
    /// 格子里那层 42% 的黑（读回实测是 <c>0,0,0,94</c>）落下来就是<b>纯黑块</b>
    /// （2026-10-16 实测，屏上帧 57% 的像素是 <c>0,0,0</c>）。</para>
    ///
    /// <para><b>为什么量具当初没发现</b>：<c>src/tools/DebugProbe/Layered.cs</c> 那一发推的是
    /// <b>不透明纯色</b>（<c>{B,G,R,0xFF}</c>），blend 同样全 0 —— 硬边和按 alpha 混
    /// 对它没有分别，所以「19200 / 19200 个像素变」只答得出<b>推进去了</b>，答不出 alpha 有没有被认。</para>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Blend
    {
        public byte Operation;
        public byte Flags;
        public byte ConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        /// <summary>两块矩形是不是同一块（四个边逐个比）。
        ///
        /// <c>Rect</c> 是普通结构体：默认那个 <c>Equals</c> 比的确实是四个字段，
        /// 但它走反射加装箱，而这两问每秒都要走 —— 所以自己写一遍逐边比较。
        /// 不去实现 <c>IEquatable</c> 是因为「两块矩形一样」和「两块矩形都能当键用」
        /// 在这个仓库里不是一件事（当键用的地方一处都没有）。</summary>
        public bool SameAs(Rect other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public override string ToString() =>
            $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern nint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern nint SetWindowLong(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    /// <summary>图标层那一扇（<c>SHELLDLL_DefView</c>），也就是 shell 自己认的「桌面内容」。
    ///
    /// Win7 及以前它是 <c>Progman</c> 的子窗；Win8 以后它挂在某个 <c>WorkerW</c> 底下，
    /// 而 <c>Progman</c> 只剩一块空壳。所以两条路都要走一遍 —— 这是社区里
    /// 「让窗口逃脱 Win+D」的通用写法里那一段查找（<c>FindShellDefViewWnd</c>）。
    ///
    /// <b>为什么用它当 owner</b>：跨进程改不了桌面那一叠的 z 序（<c>SetWindowPos</c>
    /// 顶上去，Explorer 每次都抢回来，2026-10-12 实测）。但 owner 关系是我们进程内部的
    /// 合法调用，shell 认，Win+D 就绕开日历。owner <b>不是</b> 父子关系，所以
    /// <c>WS_EX_LAYERED</c> 那一条不受影响 —— 这一句是推测，所以 <c>--check</c> 里有一组
    /// 专门盯它有没有失效。</summary>
    public static IntPtr GetShellDefView()
    {
        // 先走 Win7 及以前那条：Progman 直接挂着 DefView。
        var progman = FindWindow("Progman", "Program Manager");
        if (progman != IntPtr.Zero)
        {
            var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return defView;
        }
        // 再走 Win8 以后那条：一扇一扇 WorkerW 找过去。
        for (var worker = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
             worker != IntPtr.Zero;
             worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
        {
            var defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return defView;
        }
        return IntPtr.Zero;
    }

    /// <summary>换掉顶层窗的 owner（<c>GWL_HWNDPARENT</c> 在顶层窗上读写的<b>就是</b> owner，
    /// 不是父子关系；父子关系那一发是 <see cref="SetParent"/>）。
    ///
    /// <b>必须走 <c>SetWindowLongPtrW</c>，不能走 <c>SetWindowLongW</c></b>：这一条是
    /// 2026-10-12 一次实测逼出来的。owner 是一位<b>句柄</b>，而 <c>SetWindowLongW</c>
    /// 的第三个位是 32 位 <c>LONG</c>，64 位句柄会被截断 —— 换了之后读回来还是 <c>0x0</c>，
    /// 日志打「owner=0x0（换成功=True）」，那个 True 还是假的：<c>SetWindowLongW</c> 返回
    /// 「换之前的值」，原来没人认领就是 0，拿 0 当失败判据答不出真假
    /// （<see cref="GetWindowLongPtr"/> 那一条注释写的就是同一件事的取用侧）。
    /// 从进程外用 <c>SetWindowLongPtrW</c> 打一发，日历 owner 从 <c>0x0</c> 变成
    /// <c>0x100FC</c>（<c>SHELLDLL_DefView</c>），读回来一样 —— 机制成立。</summary>
    public static bool SetOwner(IntPtr hWnd, IntPtr owner)
    {
        SetWindowLongPtr(hWnd, GwlHwndParent, owner);
        // 不拿返回值判成败（上面那条注释说了它答不出真假），改读回来对一遍。
        return GetWindowLongPtr(hWnd, GwlHwndParent) == owner;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// 句柄还指着一扇活着的窗。日历每秒问一次这一发，是因为它的爹是 Explorer 的窗：
    /// Explorer 一重启，<c>SHELLDLL_DefView</c> 是新一扇，挂在旧爹底下的那扇一起被销毁。
    /// <c>IsWindowVisible</c> 答不了这件事（窗没了它返回 false 和窗藏起来返回 false 是同一个数）。
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// 窗是不是在最简化那一态。<c>IsWindowVisible</c> 对最小化的窗照样返回 true（WS_VISIBLE
    /// 没被清），而 <c>GetWindowRect</c> 在这种窗上也不可靠 —— 实测过一扇被 shell 扫走的窗
    /// 矩形仍是原处，所以「被最小化了吗」必须问这一发，不能从矩形猜。
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point pt);

    /// <summary><c>SystemParametersInfo</c> 只在这里服务 <see cref="DesktopWallpaperMeasurement"/> 那一发，
    /// 所以它<b>不是公开的</b>。那个 <c>action</c> 号写错一位，同一次调用就从「读」变成「改」——
    /// 公开它等于把「改桌面壁纸」递到每个调用方手里，而仓库里只需要读。
    /// 要加别的 SPI 动作，另开一个具名包装，不许有第二个调用方直接拿号来调这一发。</summary>
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param,
        StringBuilder vparam, uint winIni);

    /// <summary><c>SPI_GETDESKWALLPAPER</c> = <b>0x0073</b>（10.0.26100.0 的 <c>winuser.h</c> 逐条读过）。
    /// <b><c>0x0014</c> 是它隔壁那一颗 <c>SPI_SETDESKWALLPAPER</c></b>：本仓库把它抄成 GET 抄过一次，
    /// 于是每次「问当前是哪一张」都变成一次「把壁纸设成空字符串」—— 桌面当场变黑，一秒一次
    /// （<c>Calendar.Tick</c> 里 <c>Wallpaper.Refresh</c>）。取向和实测结果记在
    /// <c>docs/research/win32-constants-and-structs.md</c> 那张「记错的常量」表里，别拿记忆改这一行。</summary>
    public const uint SpiGetDesktopWallpaper = 0x0073;

    /// SPI_GETDESKWALLPAPER 要的缓冲区容量，字符数。文档给的下限是 MAX_PATH；Windows 10 起
    /// 长路径可以超过 260，这里给 520。
    public const int WallpaperPathChars = 520;

    /// <summary>「系统现在铺的那一张」那一问的**原始实测结果**：Windows 答没答应（返回值），
    /// 和缓冲区里那几个字。这一发只读不改。
    ///
    /// 分成两个数回是有原因的：GET 那一发返回 false 时缓冲区是空的，只看字符串分不开
    /// 「它回了个空路径」和「它根本没理这一发」—— 而这两种的下一步不一样。
    ///
    /// GET 那一发的 <c>uiParam</c> 是<b>缓冲区容量</b>（写 0 等于告诉 Windows「缓冲区一格都没有」，
    /// 那一问就答不上来），<c>fWinIni</c> 对它无意义所以是 0。</summary>
    public static (bool Replied, string Raw) DesktopWallpaperMeasurement()
    {
        var buffer = new StringBuilder(WallpaperPathChars);
        var replied = SystemParametersInfo(SpiGetDesktopWallpaper, WallpaperPathChars, buffer, 0);
        return (replied, buffer.ToString());
    }

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const uint LwaColorkey = 0x01;
    public const uint LwaAlpha = 0x02;

    /// <summary><c>ULW_ALPHA</c> —— <see cref="UpdateLayeredWindow"/> 按 blend 那一支混。</summary>
    public const uint UlwAlpha = 0x00000002;

    /// <summary><c>BLENDFUNCTION</c> 里那一位「用源图自己的 alpha 通道」——
    /// <c>BlendFlags</c> 和 <c>AlphaFormat</c> 都是它。两个字段都得填它的理由在
    /// <see cref="Blend"/> 那一条注释里。</summary>
    public const byte AcSrcAlpha = 0x01;

    /// <summary>把一块 DIB 直接打成层叠窗的面。<b>整窗一条都不画</b>时它答真、屏上 0 像素变 ——
    /// 那一族为什么必须 <c>destDc</c> 传 0、为什么 blend 两位都要 <see cref="AcSrcAlpha"/>，
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>，实施在
    /// <c>NativeLayer.Update</c>。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(
        IntPtr window, IntPtr destDc, ref Point dest, ref Size size,
        IntPtr srcDc, ref Point source, uint colorKey, ref Blend blend, uint flags);

    /// 遮罩不许抢键盘焦点，所以「前台窗口是谁」是一个能在跑的时候问出来的判据：
    /// 举起遮罩前后是同一个窗口，那条承诺就没破。
    /// （这一句原本和 <c>SPI_GETDESKWALLPAPER</c> 挤在同一段注释里，于是那一发读起来像
    /// 「<c>SPI_GETHIGHCONTRAST</c> 那一族的读操作」—— 号抄成 SET 就是从那一段开始的。）
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// 把一扇窗端到前台来。<b>和 <see cref="SetForegroundWindow"/> 不是一条规矩</b>：
    /// 后者在「调用方自己不掌握前台」时会被 Windows 直接拒掉（2026-09-26 实测：
    /// 日历抬出来打字那一发，引擎报输入框有焦点，而前台仍是 Chrome），
    /// 而这一发是给「用户动作换来的切换」用的，非前台进程也叫得动。
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    public const int SwHide = 0;
    public const int SwShow = 5;

    /// 摆出来但不抢焦点。托盘把日历重新挂回去的那一发要的是这个：
    /// <c>SW_SHOW</c> 会把窗激活，而人那时候正点在别的地方。
    public const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    /// <c>MonitorFromWindow</c> 的旗标：拿离这个窗中心最近的那块屏。另两个是
    /// <c>0</c>（ intersecting，否则 NULL）和 <c>1</c>（永远主屏）。
    /// <c>Fullscreen</c> 要的是「这个窗占着<b>它自己那块</b>屏」，所以是 2 —— 副屏上的全屏
    /// 用 1 去问会拿到主屏的矩形，那一发就永远判不成全屏。
    public const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    // ---- showing the desktop, without a hand and without a keystroke ------------
    //
    /// Explorer 自己的「显示桌面」动词，走 `Shell.Application` 晚绑定。
    ///
    /// 不注入 Win+D 那一发的理由：Keeper 自己那条 `WH_KEYBOARD_LL` 钩子正在跑，注入的按键先过它，
    /// 读到的行为里混进它的反应；这一发不碰输入队列，所以量的是 shell 做了什么，不是钩子放过去了什么。
    ///
    /// **别把 `UndoMinimizeALL` 读成可靠的逆运算。** 2026-09-26 那三跑之后去取窗口状态，桌面上
    /// 好几扇仍是 `isMinimized=true`，而动手之前没拍整张桌面的原状态，所以那一次到底还回没回来
    /// 对不了账。对账那一发（`DesktopLayer.VisibleTopLevelWindows` + `Reconcile`）写了，还没跑。
    ///
    /// CA1416 在这里没有对手：这个工程整面都是 Win32（托盘、低级钩子、挂进壁纸的 SetParent），
    /// 没有第二个平台会跑到这一行，所以不为一行警告给整面加平台注解。
#pragma warning disable CA1416
    public static bool ShowDesktop(bool undo)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return false;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            if (shell == null) return false;
            // 这两个动词不收参数，null 就是「无参」。
            type.InvokeMember(undo ? "UndoMinimizeALL" : "MinimizeAll",
                BindingFlags.InvokeMethod, null, shell, null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }
#pragma warning restore CA1416

    // ---- sending input, so the streak can be measured without a hand ----------
    //
    // MOUSEEVENTF_MOVE with a relative ±1 pixel jitters the cursor in place. That is
    // the least intrusive real input event we can produce: the low-level hook sees it,
    // and nothing under the pointer gets clicked or typed into.
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// 绝对坐标：Dx/Dy 不是位移而是位置，且要带上 VIRTUALDESK 才是铺满整个虚拟桌面
    /// （不带就只在这块屏的 0..65535 里，多屏时点不到副屏）。
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input[] events, int size);

    /// INPUT 的联合体里 MOUSEINPUT 就是最大的那个成员，所以后面不需要补位；
    /// cbSize 一律交给 Marshal 算，不写死。
    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    // ---- 键盘那一发 ---------------------------------------------------------------
    //
    /// <summary>KBDLLHOOKSTRUCT：<c>WH_KEYBOARD_LL</c> 回调里 lParam 指的那一块。
    /// <c>wParam</c> 只是消息号（按下 0x0100 / 松开 0x0101），虚拟键在<b>这一块</b>里。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public static KbdLlHookStruct ReadKeyboardHook(nint lParam) =>
        Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

    public const int WmKeydown = 0x0100;
    public const int WmKeyup = 0x0101;
    public const int WmSysKeydown = 0x0104;
    public const uint VkEscape = 0x1B;
    public const uint KeyEVENTFKeyUp = 0x0002;

    /// <summary><c>KEYBDINPUT</c> 垫到和 <c>MOUSEINPUT</c> 一样宽（x64 上那个联合体是 32 字节、
    /// <c>INPUT</c> 是 40）。这一点不讲究就白跑：<c>SendInput</c> 的 <c>cbSize</c> 对不上时它
    /// <b>返回 0 而且不改任何输入</b>，同一类坑是 <c>WNDCLASSEX</c> 那两个指针字段被写成数组、
    /// <c>cbSize</c> 多了 512、报 87 那一发（见 docs/research/win32-constants-and-structs.md 开头那一段）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct KbdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public uint Type;     // INPUT_KEYBOARD = 1
        public KbdInput Kbd;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")]
    public static extern uint SendKeyboardInput(uint count, KeyboardInput[] events, int size);

    private static uint Key(ushort vk, uint flags) => SendKeyboardInput(1,
        [new KeyboardInput { Type = 1, Kbd = new KbdInput { Vk = vk, Flags = flags } }],
        Marshal.SizeOf<KeyboardInput>());

    /// <summary>测量程序那一发：交一个真的 Esc 进系统（按下 + 松开两批 <c>SendInput</c>，绝对走
    /// 低级钩子那条路，所以我们自己那把 <c>WH_KEYBOARD_LL</c> 一定看得见它）。
    /// 尺寸一并报回来 —— 发 0 和结构体尺寸对不上是同一副样子，不留这一行就只能猜。
    /// 钩子在我们自己这个线程上回调，所以发完要隔一会儿再实测结果（和 <see cref="ClickAt"/> 同理）。</summary>
    public static string PressEscape()
    {
        var down = Key((ushort)VkEscape, 0);
        var up = Key((ushort)VkEscape, KeyEVENTFKeyUp);
        return $"sizeof(INPUT)={Marshal.SizeOf<Input>()} sizeof(键盘那一发)={Marshal.SizeOf<KeyboardInput>()} " +
               $"按下={down}/1 松开={up}/1";
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// 物理屏幕坐标 → SendInput 那 0..65535 的归一化坐标。虚拟桌面的左边在多屏下不是 0，
    /// 所以减的是它而不是 0；这一步错了就整个实验白做（点到的不是那个控件）。
    public static Point ToNormalized(Point physical)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);
        return new Point
        {
            X = (physical.X - left) * 65535 / width,
            Y = (physical.Y - top) * 65535 / height,
        };
    }

    /// 按下左键（不松开）。<see cref="ReleaseAt"/> 是它的另一半。
    ///
    /// 为什么拖要拆成「按下 / 一格一格挪 / 松开」三段而不是一次交进去：
    /// 一次 <c>SendInput</c> 交十几发，引擎在**同一帧**里把按下、那些移动、松开全收走，
    /// 于是 GUI 那个拖动状态机根本没进「正在拖」那一档 —— 读出来是「拿起那一发都没调到」。
    /// 真人手拖天然跨很多帧，所以引擎那一层是按跨帧设计的。
    public static uint PressAt(Point physical)
    {
        var at = ToNormalized(physical);
        var flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTDOWN;
        return SendInput(1, [new Input { Type = 0, Mouse = new MouseInput { Dx = at.X, Dy = at.Y, Flags = flags } }],
                         Marshal.SizeOf<Input>());
    }

    /// 松开左键。
    public static uint ReleaseAt(Point physical)
    {
        var at = ToNormalized(physical);
        var flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTUP;
        return SendInput(1, [new Input { Type = 0, Mouse = new MouseInput { Dx = at.X, Dy = at.Y, Flags = flags } }],
                         Marshal.SizeOf<Input>());
    }

    /// 一整趟拖一次交进去（按下 → 中间那些移动 → 松开）。
    ///
    /// **这一发的坑**：事件全在**同一帧**里进去，引擎来不及进「正在拖」那一档，
    /// 所以拿它量拖放读出来是「拿起那一发都没调到」。要量拖就分开交
    /// （<see cref="PressAt"/> → 逐格 <see cref="MoveTo"/> → <see cref="ReleaseAt"/>），
    /// 每一段之间让出一帧。这一发留着是因为它一次交完、便于数「交了几发」。
    public static uint DragFromTo(Point from, Point to, out Point normalisedFrom, int steps = 12)
    {
        var start = ToNormalized(from);
        normalisedFrom = from;
        var baseFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        var events = new List<Input>
        {
            new() { Type = 0, Mouse = new MouseInput { Dx = start.X, Dy = start.Y, Flags = baseFlags } },
            new() { Type = 0, Mouse = new MouseInput { Dx = start.X, Dy = start.Y, Flags = baseFlags | MOUSEEVENTF_LEFTDOWN } },
        };
        for (var i = 1; i <= Math.Max(1, steps); i++)
        {
            var at = ToNormalized(new Point
            {
                X = (int)Math.Round(from.X + (to.X - from.X) * i / (double)Math.Max(1, steps)),
                Y = (int)Math.Round(from.Y + (to.Y - from.Y) * i / (double)Math.Max(1, steps)),
            });
            events.Add(new Input { Type = 0, Mouse = new MouseInput { Dx = at.X, Dy = at.Y, Flags = baseFlags } });
        }
        var end = ToNormalized(to);
        events.Add(new Input { Type = 0, Mouse = new MouseInput { Dx = end.X, Dy = end.Y, Flags = baseFlags | MOUSEEVENTF_LEFTUP } });
        return SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<Input>());
    }

    /// 只把光标移到那个点上：绝对坐标、带位移，所以低级钩子一定看得见它 ——
    /// 这一发是「事件到底进没进系统」的判据。<c>SetCursorPos</c> 不能用，它不产生钩子事件。
    public static uint MoveTo(Point physical) => SendMouse(physical, MOUSEEVENTF_MOVE);

    /// 在那个点上按一下左键：移动 + 按下 + 松开三发一起交进去，一次 SendInput。
    /// 在那个点上按一下左键。
    ///
    /// <b>三发分三批交，每批之间隔 40 毫秒</b>（2026-10-10 真跑量出来的一发）。原来是一次
    /// `SendInput` 把移动 + 按下 + 松开一起塞进去 —— **那一发的读数是 `SendInput 交了 3/3 发`，
    /// 而钩子那一侧读到的是「移动 3、按下 0、松开 1」**：系统全收了，而低级钩子把按下那一发
    /// 丢了，于是这一族问的那句话（「钩子吃没吃」）压根没被问到。
    /// 这一族为「发出去的没全到」栽过好几次（`docs/research/probe-and-criteria-pitfalls.md`
    /// 「旧 DLL 冒充通过」那一族旁边），所以分批不是洁癖。
    public static uint ClickAt(Point physical)
    {
        var at = ToNormalized(physical);
        var flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint sent = 0;
        foreach (var one in new[] { flags, flags | MOUSEEVENTF_LEFTDOWN, flags | MOUSEEVENTF_LEFTUP })
        {
            sent += SendInput(1, [new Input
            {
                Type = 0, Mouse = new MouseInput { Dx = at.X, Dy = at.Y, Flags = one },
            }], Marshal.SizeOf<Input>());
            Thread.Sleep(40);
        }
        return sent;
    }

    private static uint SendMouse(Point physical, uint flags)
    {
        var at = ToNormalized(physical);
        return SendInput(1, [new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                Dx = at.X, Dy = at.Y, Flags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        }], Marshal.SizeOf<Input>());
    }

    // ---- measurement what the screen actually shows ------------------------------
    //
    /// The composited desktop, at a physical pixel. Read from inside this process
    /// on purpose: an external DPI-unaware reader gets virtualised coordinates and
    /// measures the wrong pixel — the artifact that made the plan's DPI row wrong.
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hDC, int x, int y);

    public readonly record struct Pixel(byte R, byte G, byte B)
    {
        public override string ToString() => $"({R},{G},{B})";
    };

    // SampleScreen 和 ChannelDifference 搬到 Frames 去了（2026-09-29）。搬的理由不是整齐：
    // 原来这一份把 GetPixel 的 CLR_INVALID 折成 new Pixel(0,0,0)，也就是把「没读到」伪装成
    // 「读到黑」，而测量程序自己那份折成 (-1,-1,-1) —— 同一件事两份实施、处理相反。折成一个合法
    // 颜色之后，下游只看差的判据在两发都没读到时差是 0，会印成「那一点是透的」。
    // 现在「读不到」是 Frames.Sample 上的一档，不是一个颜色。这三个 extern 留着：
    // Frames 够的就是它们，而「碰 Win32 的地方少几处、每处带名字」是这一发的取向。

    /// Asking another process's control for a count is a message with no pointers
    /// in it, so it crosses the process boundary fine — which is exactly what
    /// lets the probe ask the icon layer what it thinks is selected.
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint SendMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    /// <summary><c>SendMessageTimeoutW</c>：带超时的跨进程发消息，专为「对方可能挂着」的路。
    /// <c>SMTO_ABORTIFHUNG</c>（0x0002）让 Explorer 卡住时这一发不把我们拖死；
    /// 返回值答「答没答真」，不答「收没收到」—— 调用方要把这两件事分开记。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SendMessageTimeout(
        IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    public const uint LvmGetItemCount = 0x1004;
    public const uint LvmGetSelectedCount = 0x1032;

    /// <summary><c>LVM_GETITEMSPACING</c> = <b>0x1020</b>，<c>LVM_GETICONSPACING</c> = <b>0x1033</b>
    /// （<c>um/winuser.h</c> 10300 行起，10.0.26100.0）。
    ///
    /// <b>为什么只带这两个不带指针的</b>：<c>LVM_GETITEMRECT</c>（0x100E）要一块跨进程共享内存
    /// 才能回话，而项目那一条明令不许走它 ——「一次错就把 Explorer 带走」。
    /// 这两个只回一个 <c>int</c>，跨进程直接就答上了，量不到那个风险。
    public const uint LvmGetItemSpacing = 0x1020;
    public const uint LvmGetIconSpacing = 0x1033;

    /// <summary>图标层里图标格子的间距（<c>x</c> 宽、<c>y</code> 高）。
    /// <c>x</c> 那一半就是一列图标占多宽 —— 桌面图标默认排成竖排一列，
    /// 所以日历只要不覆盖左边这么宽，图标就整个透过来。</summary>
    public static (int X, int Y) DesktopIconSpacing(nint iconView)
    {
        var item = SendMessage(iconView, LvmGetItemSpacing, IntPtr.Zero, IntPtr.Zero).ToInt32();
        return (item & 0xFFFF, (item >> 16) & 0xFFFF);
    }

    /// <summary><c>LVM_HITTEST</c> = <b>0x1014</b>（<c>um/winuser.h</c>，10.0.26100.0）。
    /// <c>lParam</c> 是<b>客户区</b>坐标（<c>MAKELPARAM</c> 拼的两个 16 位），
    /// <c>wParam</c> 是要问的那几位（<c>LVHT_*</c>，<c>0</c> = 问「在不在图标上」）。</summary>
    public const uint LvmHitTest = 0x1014;

    /// <summary><c>LVM_HITTEST</c> 的 <c>wParam</c> 位档（<c>um/commctrl.h</c>，10.0.26100.0）：
    /// <c>LvhtOnIcon</c> 管图标那一块、<c>LvhtOnLabel</c> 管它下面那几行字，
    /// <c>LvhtOnItemIcon</c> / <c>LvhtOnItemLabel</c> 是头文件另给的两档，
    /// <c>LvhtOnItem</c> 是前两位合起来，也就是<b>整颗</b>。
    ///
    /// <b>头文件只说哪一位管哪一块，哪一位答得动是本机量出来的</b>，
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>「日历让开图标那几列」那一节。
    /// 别拿 <c>commctrl.h</c> 推读数：<see cref="IconAt"/> 写死的那个 <c>0</c>
    /// 在头文件里正是 <c>LvhtOnItem</c>，而它在这台机器上<b>七档位处处答 0</b>（那一节量了 1508 个点）。</summary>
    public const int LvhtOnIcon = 0x0002, LvhtOnLabel = 0x0004, LvhtOnItem = LvhtOnIcon | LvhtOnLabel,
        LvhtOnItemIcon = LvhtOnIcon | 0x0020, LvhtOnItemLabel = LvhtOnLabel | 0x0040,
        LvhtOnItemState = LvhtOnItemIcon | LvhtOnItemLabel | 0x0080,
        LvhtOnItemTooltip = LvhtOnItemIcon | LvhtOnItemLabel | 0x0100;

    /// <summary>图标层答的<b>原样</b>返回值，问法（<paramref name="flags"/>）由调用方给。
    ///
    /// <see cref="IconAt"/> 是这一问把 <paramref name="flags"/> 写死成 0 的那一份，
    /// 留着是因为鼠标桥第三问在用它（<c>DesktopLayer.OnIcon</c>）；
    /// 这一份不折成一位 —— 折法由读数说，见上面那条注释指的那一份实测结果。</summary>
    public static int IconHitRaw(nint iconView, Point at, int flags)
    {
        if (iconView == IntPtr.Zero) return -1;
        var client = at;
        if (!ScreenToClient(iconView, ref client)) return -1;
        var lp = (nint)((client.Y << 16) | (client.X & 0xFFFF));
        return SendMessage(iconView, LvmHitTest, (nint)flags, lp).ToInt32();
    }

    [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

    // ---- 无障碍那一族：这一台机器上量过了，答不出位置（2026-10-14）--------------------
    //
    // 这一族**留在代码里是不成立的**：读法选型的读数在
    // `docs/research/desktop-layer-and-window-measurements.md`「日历让开图标那几列」。
    // 三步读数：`AccessibleObjectFromWindow`（`oleacc.dll`）答 0（拿到了那一扇）、
    // `accChildCount` 答 6；**`accLocation` 对 0 到 6 每一个序号都答 E_INVALIDARG**；
    // `accNavigate` 那一发**当场崩进程**（崩在 `CSharpInstanceBridge.Call`）。
    // `IDispatch` 晚绑定那一版（不手写虚表）在第一句就抛
    // 「Exception has been thrown by the target of an invocation」。
    // 所以产品侧**不许**再拿这一族去换图标位置 —— 上面几行是那三步的记录，不是可用的实现。
    // `DebugProbe icons` 用的是另一条读法（UI Automation，WindowsDesktop 程序集），那条通，
    // 但它是量具，不是产品 —— 见同一份文档里那一段。

    /// <summary>屏幕上这一点，图标层认不认它落在一个图标上。
    ///
    /// <b>它答的是「这一点归不归图标」，不是「那一层在不在那一点下面」。</b>
    /// 返回值是 <c>LVM_HITTEST</c> 的原样：<b>非负</b>是那个图标的序号，
    /// 负数是 <c>LVHT_*</c> 那一族（<c>LVHT_NOWHERE</c> = -1 表示那一点没东西）。
    /// 只回一个 <c>int</c>、不回指针，所以跨进程直接答得上（理由同上面那几颗）。
    ///
    /// <b>为什么必须有这一颗</b>：日历那扇窗现在带 <c>WS_EX_TRANSPARENT</c>，
    /// 鼠标<b>穿过去</b>落到图标层，于是 <c>WindowFromPoint</c> 在日历底下答的也是图标层 ——
    /// 「那一点归图标层」和「那一点上有一个图标」被混成一件事了。
    /// <b>不分开问就会出这一种错</b>：<b>双击图标去打开它，顺手把日历抬起来打字</b>。
    ///
    /// <b>2026-10-14 补一句实测结果</b>：上面那句「非负 = 那个图标的序号」是头文件的意思，
    /// <b>这台机器上不成立</b> —— 整块图标层按 50 像素取样、1508 个点，
    /// <c>wParam</c> 七档（见上面 <see cref="IconHitRaw"/>）一律答 0，
    /// **压在图标身上的点也一样答 0**。也就是这一问恒为真，鼠标桥那道闸门恒闭。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>「日历让开图标那几列」，
    /// 那一发（`--icon-columns`）可以原样重跑。改成什么读法能答、或者怎么绕过它，
    /// 还没定 —— 别拿这一颗当「图标在哪」的答案。</summary>
    public static int IconAt(nint iconView, Point at) => IconHitRaw(iconView, at, 0);

    /// <summary><see cref="IconAt"/> 的那一问压成一位：<c>true</c> = 那一点上有一个图标。</summary>
    public static bool IconHit(nint iconView, Point at) => IconAt(iconView, at) >= 0;


    // ---- the low-level input hooks ------------------------------------------
    //
    // These are the two hooks Activity.cs installs for the working streak, so the
    // probe exercises them for real: they see the click whether or not our window does.

    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;
    /// <summary><c>WM_NCHITTEST</c> —— 问一扇窗「屏幕点这一点归不归你」。
    ///
    /// <para><b>这个数是量过的，不是抄的</b>：2026-10-10 晚，从进程外
    /// <see cref="SendMessage"/> 这一扇引擎的顶窗，五个点读回来全是 <b>-1</b>
    /// （<c>HTTRANSPARENT</c>），而同一时刻 <c>WindowFromPoint</c> 在
    /// <b>进程内</b>读回的是别人的窗 —— 两把尺子对打之后才敢写下这个数。
    /// 记错的常量表在 <c>docs/research/win32-constants-and-structs.md</c>。</para>
    public const uint WmNchitTest = 0x0084;
    /// <c>HTTRANSPARENT</c> —— 「这一点不是我的，往下问」。
    /// <b>只跨同一个线程</b>：同线程里下一扇收得到，跨进程收不到。
    public const int HtTransparent = -1;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonDown = 0x0204;
    public const int WmRButtonUp = 0x0205;
    public const int WmMouseWheel = 0x020A;

    /// MOUSEHOOKSTRUCT 的 flags 里这两位说明「这一发是注入进来的」，用来在日志里
    /// 把我们自己发的那几下和真的手点分开 —— 不分开的下一次实测结果就是脏的。
    /// 值取自 WinUser.h（10.0.26100.0 那一份）：0x01 和 0x02。第一次写这里时记成了
    /// 0x10/0x20，于是 SendInput 发的点击在日志里显示「注入=False」—— 一条测量程序假象。
    public const uint LlmhfInjected = 0x0001;
    public const uint LlmhfLowerIlInjected = 0x0002;

    public static string MouseMessageName(int wParam) => wParam switch
    {
        WmMouseMove => "移动",
        WmLButtonDown => "左键按下",
        WmLButtonUp => "左键松开",
        WmRButtonDown => "右键按下",
        WmRButtonUp => "右键松开",
        WmMouseWheel => "滚轮",
        _ => $"0x{wParam:X4}",
    };

    public static MouseLlHookStruct ReadMouseHook(nint lParam) =>
        Marshal.PtrToStructure<MouseLlHookStruct>(lParam);

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseLlHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hHook);

    // ---- 专用钩子线程（2026-10-12，工单 desktop-comfort/02）-----------------------------
    //
    // `WH_MOUSE_LL` 在**全机输入的同步路径**上：钩子回调在哪个线程上跑，那条线程一卡，
    // 全机鼠标一起等。所以它不能挂在主线程上 —— 下面这四条是给它一条自己线程用的。

    /// <summary><c>GetMessageW</c>。专用线程的循环就是它 —— <c>LowLevelHooksTimeout</c>
    /// 认的是「这条线程多久没泵消息」，没有这个循环钩子会被系统悄悄摘掉。</summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>内核对象那个「等它真的结束」的句柄（一个未命名的 event）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern bool SetEvent(IntPtr handle);

    /// <summary><c>MSG</c>：<c>GetMessage</c> 要往里填的那一个（<c>POINT</c> 复用
    /// <see cref="Point"/>，那已经是这个仓库里的形状）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    /// <summary>叫一条 <c>GetMessage</c> 循环收摊的那一发。</summary>
    public const uint WmQuit = 0x0012;

    /// <summary><c>WAIT_OBJECT_0</c> = 0，<c>WAIT_TIMEOUT</c> = 258 —— 判「等到了」还是「没等到」。</summary>
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(IntPtr hHook, int nCode, nint wParam, nint lParam);

    public static string ClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "none";
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) == 0 ? "?" : sb.ToString();
    }

    public static string WindowText(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "none";
        var sb = new StringBuilder(512);
        return GetWindowText(hWnd, sb, sb.Capacity) == 0 ? "" : sb.ToString();
    }

    /// <summary>The top-level window a handle belongs to, so a hit test on a child
    /// can be compared against our own HWND.</summary>
    public static IntPtr GetAncestorRoot(IntPtr hWnd) => GetAncestor(hWnd, GaRoot);

    /// <summary>The real parent, ignoring the owner relationship.
    ///
    /// <para><b>为什么不能拿 <c>GetParent</c> 当这一问</b>：<c>GetParent</c> 对一扇顶层窗
    /// 答的是它的 <b>owner</b>，不是真爹 —— 而 <c>DesktopLayer.Park</c> 顶层那一族
    /// 正好把 owner 设成了 <c>SHELLDLL_DefView</c>，于是顶层和挂进 <c>DefView</c>
    /// 两种状态下 <c>GetParent</c> 答的是同一个数，这一问就没法分了。
    /// <c>GA_PARENT</c> 只认真爹，顶层窗一律答 0。</para>
    ///
    /// <para><b>这一问用来分两种摆法</b>：答 0 是顶层（要抢 z 序），答 <c>DefView</c>
    /// 是挂进桌面层（不抢，见 <c>DesktopLayer.Park</c>）。</para></summary>
    public static IntPtr GetAncestorParent(IntPtr hWnd) => GetAncestor(hWnd, GaParent);


    /// <summary>Every top-level window this process owns. Taken before the faders are
    /// built and again after, the difference is which handles are the faders' — no
    /// guessing by title, which the engine rewrites while the editor runs it.</summary>
    public static List<IntPtr> OwnTopLevelWindows()
    {
        var found = new List<IntPtr>();
        var pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid == pid) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>按标题认出自己进程里的某一扇窗。引擎不给 HWND，而「这一点归谁」「把它举到
    /// 前面」每一发实测结果都要一个，所以只能自己数。<c>Fader</c> 里那份是同一件事的私有版本；
    /// 这里给出来是因为测量程序那一发也要举一次关于窗口。标题匹配在本进程内是精确的 ——
    /// 只有主窗会被引擎在编辑器里加上 <c> (DEBUG)</c> 后缀。</summary>
    public static IntPtr OwnWindowNamed(string title)
    {
        foreach (var hwnd in OwnTopLevelWindows())
            if (WindowText(hwnd) == title) return hwnd;
        return IntPtr.Zero;
    }

    /// <summary>按标题认出**别的进程**里的一扇顶层窗。<b>认得出来的场合只有测量程序</b> ——
    /// 产品那一族要的每一扇窗都在自己进程里（<see cref="OwnWindowNamed"/> 那一族）。
    ///
    /// <para><b>为什么需要它</b>：<c>--swallow</c> 那一发的两端必须分属两个进程
    /// （钩子在一头、系统路由的落点在另一头），而引擎不给别的进程的 HWND，
    /// 只能自己数一遍顶层窗。**按标题精确匹配**，而那一族的标题是测量程序自己写的
    /// （带进程号，所以重跑时不会认到上一次残留的那一扇）。</para>
    ///
    /// <para><b>可见性要问</b>：认到一扇已经关掉的窗和认不到一样是错的，而
    /// <c>EnumWindows</c> 只数顶层、不管它是不是还画着。</para>
    public static IntPtr AnyProcessWindowNamed(string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (WindowText(hwnd) != title) return true;
            found = hwnd;
            return false;      // 认到了就停
        }, IntPtr.Zero);
        return found;
    }

    /// 本进程自己有哪几个顶层窗口、各占多大。命中测试读回来的那个 HWND 是不是我们的，
    /// 靠这一行定死 —— 上一轮把引擎主窗口当成「别人抢走了那个点」，就是因为没印它。
    public static string DescribeOwnTopLevels()
    {
        var into = new StringBuilder();
        foreach (var hwnd in OwnTopLevelWindows())
        {
            GetWindowRect(hwnd, out var r);
            into.Append($"\n      我们自己：0x{hwnd:X} '{WindowText(hwnd)}' " +
                        $"类='{ClassName(hwnd)}' 可见={IsWindowVisible(hwnd)} 矩形=({r.Left},{r.Top})-({r.Right},{r.Bottom})");
        }
        return into.Length == 0 ? "      我们自己：一个顶层窗口都没有" : into.ToString().TrimStart();
    }

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GaRoot = 2;
    private const uint GaParent = 1;

    /// <summary>Style bits as names, because nobody reads 0x000C0000 in a log.
    /// GWL_STYLE and GWL_EXSTYLE share no values, so decoding one with the other's
    /// names invents flags that are not there.</summary>
    public static string DescribeStyle(nint style)
    {
        var s = (long)style;
        var names = new StringBuilder();
        AppendIf(names, s, WsPopup, "POPUP");
        AppendIf(names, s, WsChild, "CHILD");
        AppendIf(names, s, WsVisible, "VISIBLE");
        AppendIf(names, s, WsOverlappedWindow, "OVERLAPPEDWINDOW");
        return names.Length == 0 ? "0" : names.ToString();
    }

    public static string DescribeExStyle(nint exStyle)
    {
        var s = (long)exStyle;
        var names = new StringBuilder();
        AppendIf(names, s, WsExTopmost, "TOPMOST");
        AppendIf(names, s, WsExTransparent, "TRANSPARENT");
        AppendIf(names, s, WsExLayered, "LAYERED");
        AppendIf(names, s, WsExToolwindow, "TOOLWINDOW");
        AppendIf(names, s, WsExAppwindow, "APPWINDOW");
        AppendIf(names, s, WsExWindowEdge, "WINDOWEDGE");
        return names.Length == 0 ? "0" : names.ToString();
    }

    // ---- Z order ------------------------------------------------------------

    public const uint GwHwndFirst = 0;
    public const uint GwHwndLast = 1;
    public const uint GwHwndNext = 2;
    public const uint GwHwndPrev = 3;

    /// 第一个子窗。同一个调用返回的顺序是 z 序，**最上面那一个先**，所以拿它数
    /// 「我们排在 <c>SysListView32</c> 前面还是后面」，不必读任何私有字段。
    ///
    /// <b>5，不是 4</b> —— <c>um/winuser.h</c>（10.0.26100.0）10300-10305 行：
    /// <c>GW_HWNDFIRST 0 / GW_HWNDLAST 1 / GW_HWNDNEXT 2 / GW_HWNDPREV 3 / GW_OWNER 4 / GW_CHILD 5</c>。
    /// 第一版按记忆写成 4，那一发读到的是「owner 窗」= 0，兄弟表整个是空的，
    /// 差点把「读不出先后」当成结论记下来。和 <c>LLMHF_INJECTED</c> 那一行是同一个坑。
    public const uint GwChild = 5;

    /// 把一扇窗塞到它自己那个兄弟堆的最底下（对 <c>SHELLDLL_DefView</c> 的子窗来说就是
    /// 压到桌面图标那一层下面）。日历那一档要是该让开图标，动的是这一发，不是 <c>HwndNotTopMost</c>。
    ///
    /// <b>2026-10-08：产品那一侧真的调它了</b>（<c>DesktopLayer.Embed</c> 末尾）——
    /// 之前只有 <c>Probe</c> 的 <c>--grid</c> 那一发用过它（而且只当对照），
    /// 产品一直缺这一发，于是日历刚挂上去就压在图标上面。实测两档都在那一发里。
    public static readonly IntPtr HwndBottom = new(1);

    /// 客户区坐标 → 屏幕坐标。桌面图标的位置存在 <c>SysListView32</c> 的客户区里，
    /// 拿它问「那一点归谁」之前必须换一次口径。
    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    /// <summary>整个 z 序<b>最顶上</b>那一扇顶层窗。传 <see cref="IntPtr.Zero"/> 拿的是全局那一张，
    /// 不是「某一个句柄自己的那一叠」。
    ///
    /// 要它是为了从最顶上往下走一遍、认出「当前真正画着桌面的那一扇」（见
    /// <c>DesktopLayer.DesktopTop</c>）。没有这颗就只能按类名猜是哪一扇，
    /// 而同一台机器上 Progman 和 WorkerW 同时在，猜中哪一扇决定日历会不会被压住。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    /// The windows stacked above us, nearest first. Answers "who is really on top
    /// at this point" without any guesswork about our own coordinates.
    public static string Above(IntPtr hWnd)
    {
        var into = new StringBuilder();
        var at = GetWindow(hWnd, GwHwndPrev);
        var counted = 0;
        while (at != IntPtr.Zero && counted < 8)
        {
            if (IsWindowVisible(at))
            {
                into.Append($"\n      above: 0x{at:X} '{ClassName(at)}'");
                counted++;
            }
            at = GetWindow(at, GwHwndPrev);
        }
        return counted == 0 ? "      above: nothing" : into.ToString().TrimStart();
    }

    private static void AppendIf(StringBuilder into, long value, long bit, string name)
    {
        // OVERLAPPEDWINDOW is a bundle, so only say it when the bundle is exact.
        if (name == "OVERLAPPEDWINDOW")
        {
            if ((value & WsOverlappedWindow) == WsOverlappedWindow) into.Append("|").Append(name);
            return;
        }
        if ((value & bit) == bit) into.Append("|").Append(name);
    }

    // ---- the tray -------------------------------------------------------------
    //
    // Godot 4.7.2 has no tray API, so this is Shell_NotifyIcon reached straight from
    // managed code. The constant names below are taken from the SDK headers
    // (10.0.26100.0: shellapi.h, winuser.h) rather than from memory — the injected-flag
    // miss recorded in docs/research/win32-constants-and-structs.md is why that rule is there.

    public const uint WmUser = 0x0400;

    /// 托盘回调消息号由我们自己选（这里 WM_USER+1），lParam 带的是那个鼠标消息本身。
    /// 不调 NIM_SETVERSION，shell 就按老版通知图标的协议发，Windows 10/11 照发。
    public const uint WmTrayIcon = WmUser + 1;

    public const uint WmContextMenuMsg = 0x007B;
    public const uint WmLButtonUpMsg = 0x0202;
    public const uint WmRButtonUpMsg = 0x0205;
    public const uint WmDestroyMsg = 0x0002;

    public const uint NifMessage = 0x0001;
    public const uint NifIcon = 0x0002;
    public const uint NifTip = 0x0004;

    public const uint NimAdd = 0x00;
    public const uint NimModify = 0x01;
    public const uint NimDelete = 0x02;

    /// 第二发启动时递给「已经在那儿的那一发」的消息号（WM_USER+2，紧挨着托盘回调那一条）。
    /// 不走 WM_COMMAND，所以收它的这扇窗不必是命令窗，也不必是前台。
    public const uint WmSecondInstance = WmUser + 2;

    /// 测量程序专用的消息号（WM_USER+3）。把 `TrackPopupMenu` 返回的那个 id 递回托盘那扇窗，
    /// 让它走**和真菜单同一条分发**：收消息的是托盘自己的 WndProc，所以回调落在 WndProc 的栈上，
    /// 「`Quit()` 里 `_tray.Dispose()` 正踩在自己那扇窗的栈上」这一发才复现得出来。
    /// 差的只有菜单本身 —— 没有真的弹出那一层，也就没有 shell 的命中。见 `--tray-menu`。
    public const uint WmTrayMenuProbe = WmUser + 3;

    /// <summary><c>WM_CANCELMODE</c> = <b>0x001F</b>（10.0.26100.0 <c>winuser.h</c>）。
    /// <c>TrackPopupMenu</c> 见到它就收摊，递回来的是 <b>0</b>（没选中任何一项）。
    ///
    /// <b>测量程序关菜单走这一发，不走 Esc</b>：Esc 是「往菜单里塞一个键」，
    /// 而这一发是「叫菜单关掉」。2026-10-01 那次抓帧按 Esc 收场，
    /// 递回来的是 <b>1</b>（那一发顺手选中了「进入休息」），测量程序的收场成了产品动作。</summary>
    public const uint WmCancelMode = 0x001F;

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// "TaskbarCreated" 那一条广播的消息号：<b>每个 session 现问，不能写死</b>。
    /// explorer 重启（或者整个 shell 换掉）之后，托盘里那份图标跟着没了，而且没有任何东西
    /// 会来通知拥有者 —— 只有这一发广播会来，收到就得重新挂一次。
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint IconId;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    public delegate nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// 两栏类名/菜单名都是指针（LPCWSTR），不是内联数组 —— 按 261 个 WCHAR 的数组写，cbSize
    /// 就比原生多 512 字节，RegisterClassEx 直接回 87（ERROR_INVALID_PARAMETER）。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr Proc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WindowClassEx cls);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        IntPtr exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary><c>CallWindowProcW</c>：子类化收完自己的那一段，把消息转回原来那个窗口过程。</summary>
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern nint CallWindowProc(nint proc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const uint MfString = 0x0000;
    public const uint MfGrayed = 0x0001;
    public const uint MfChecked = 0x0008;
    public const uint MfSeparator = 0x0800;

    /// <summary><c>MF_POPUP</c>：这一项挂的是<b>一颗子菜单</b>，不是一条命令。
    /// **对着 SDK 头读的**（10.0.26100.0 <c>winuser.h:10455</c> <c>#define MF_POPUP 0x00000010L</c>），
    /// 不凭记忆 —— 托盘菜单的勾、灰、分隔线那几颗原来就是这么来的，同一族的规矩见
    /// <c>docs/research/win32-constants-and-structs.md</c>。</summary>
    /// 「这一项是子菜单」那个标志。**对着 <c>winuser.h</c> 逐字读的，不是凭记忆**
    /// （实测结果在 <c>docs/research/win32-constants-and-structs.md</c>）。</summary>
    public const uint MfPopup = 0x0010;

    /// TPM_RETURNCMD：点哪一项由这个调用直接返回，不走 WM_COMMAND，所以窗口不用是命令窗。
    public const uint TpmReturnCmd = 0x0100;
    public const uint TpmNonotify = 0x0080;
    public const uint TpmRightButton = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    /// <summary>追加一条<b>能接菜单句柄</b>的菜单项：<paramref name="id"/> 是 <c>UINT_PTR</c>
    /// （<c>winuser.h:7692</c>，<c>AppendMenuW</c> 的第三个形参逐字读的），所以
    /// <c>MF_POPUP</c> 那一项递进来的是子菜单的 <c>HMENU</c>。
    ///
    /// <b>为什么不能复用上面那条</b>：它的形参写死成 <c>uint</c>，而 64 位上一个
    /// <c>HMENU</c> 的高 32 位不是零 —— 递过去会被截掉，症状是「子菜单永远是空的或者
    /// 根本不弹」，而 <c>AppendMenu</c> 照样返回 true。<b>这一族是这一张工单里
    /// 「原生那一侧要补两样」的第二样</b>，实测结果和被否掉的那条路记在
    /// <c>docs/research/win32-constants-and-structs.md</c>。</summary>
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuHandle(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    // ---- measurement a menu back, so the wiring can be measured without a popup ---
    //
    // **下面这五发是给「自检」量接线用的，不是给产品用的。** 弹出菜单这一层无头量不到
    // （`TrackPopupMenu` 是模态的，要一个真的屏幕），但「子菜单挂上去了没有」「勾打在哪一条」
    // 是**菜单句柄自己的属性**，`AppendMenu` 之后立刻问得出来 —— 这一族量的是
    // 「我们递进去的那棵树对不对」，弹出那一半交给实拍帧（工单 04 的来历段）。
    // 签名对着 10.0.26100.0 `winuser.h` 的 7550-7652 与 7637-7639 逐条读。

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr menu);

    /// <summary>第 <paramref name="pos"/> 项挂的那颗子菜单（没有子菜单就是 0）。
    /// <c>winuser.h:7637</c>。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetSubMenu(IntPtr menu, int pos);

    /// <summary>那一项的状态位：<c>MF_CHECKED</c> / <c>MF_GRAYED</c> / <c>MF_SEPARATOR</c> 都在里面
    /// （问不出来时它回 <c>0xFFFFFFFF</c>，那一档要当「问不到」而不是「什么都没设」）。
    /// <c>winuser.h:7575</c>。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetMenuState(IntPtr menu, uint id, uint flags);

    /// <summary>按位置问（<c>MF_BYPOSITION</c>），拿回菜单项上那个字符串。<c>winuser.h:7560</c>。</summary>
    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)]
    public static extern int GetMenuString(
        IntPtr menu, uint id, StringBuilder text, int max, uint flags);

    /// <summary>第 <paramref name="pos"/> 项带的那个 id。<b>它对哪几项不适用</b>：
    /// 分隔线和 <c>MF_POPUP</c> 那一项都回 <c>0xFFFFFFFF</c>（<c>winuser.h:7644</c> 写着
    /// "returns -1 if the menu item does not have an identifier"）—— 所以「读到 -1」
    /// 在这里不是失败，是这一项本来就不递 id。子菜单那一项的地址要去问
    /// <see cref="GetSubMenu"/>，两者分开问才是那一项的完整实测结果。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetMenuItemID(IntPtr menu, int pos);

    /// <c>MF_BYPOSITION</c>：下面那两发按<b>位置</b>问，不按 id 问（<c>winuser.h:10439</c>）。</summary>
    public const uint MfByPosition = 0x0400;

    /// <summary>第 <paramref name="pos"/> 项在<b>屏幕上</b>占的那块矩形。
    /// <c>winuser.h:8049</c>，<paramref name="owner"/> 可以是 0（弹出来的菜单不归谁）。
    ///
    /// **只有菜单正显示着的时候才答得上来** —— 它答的是 Windows 自己排出来的那一块，
    /// 不是我们递进去的什么。这一发是抓帧那一发的<b>尺子</b>：菜单开着的时候主循被
    /// <c>TrackPopupMenu</c> 卡住（量过的），所以读矩形和挪光标都得走另一条线程。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMenuItemRect(IntPtr owner, IntPtr menu, uint pos, out Rect item);

    // ---- making an HICON out of pixels, so the glyphs stay PNGs ---------------

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    /// 32 位 BI_RGB 的表是空的，所以只递头就够了。
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>内存 DC：给 <see cref="CreateDIBSection"/> 的位图当宿主，
    /// <see cref="UpdateLayeredWindow"/> 的 srcDc 就是它。用完 <see cref="DeleteDC"/> 收。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr dc);

    /// <summary>把位图选进 DC，返回换下来的那一张 —— 收场前要把换下来的选回去。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    /// 1 位的 AND 遮罩。给全 0 就是「每个像素都不挖洞」，透明度全交给 32 位那张里的 alpha。
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    /// 托盘槽位的宽度。100% 缩放下是 16 而不是 32，所以图标要按它缩放，不是按 PNG 画布的尺寸。
    public static int SmallIconWidth => GetSystemMetrics(49);

    /// BGRA 一行一行倒着写进 DIB：CreateDIBSection 给的负高度之外的行序是自下而上的，
    /// 而 Godot 的像素缓冲是自上而下的 —— 弄反了图标会上下颠倒。
    public static IntPtr IconFromBgra(int width, int height, byte[] bgra)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = height,
                Planes = 1, BitCount = 32, Compression = 0,
            },
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var color = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection 失败");
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, y * width * 4, bits + (height - 1 - y) * width * 4, width * 4);
            }
            var mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
            var shape = new IconInfo
            {
                IsIcon = true, HotspotX = (uint)width / 2, HotspotY = (uint)height / 2,
                Mask = mask, Color = color,
            };
            var icon = CreateIconIndirect(ref shape);
            DeleteObject(mask);
            DeleteObject(color);
            if (icon == IntPtr.Zero) throw new InvalidOperationException("CreateIconIndirect 失败");
            return icon;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // ---- 输入法 ------------------------------------------------------------------
    //
    // 「抬起打字那一发中文输入法进不进得来」这一条，日志本来只能读到「输入框有焦点」——
    // 焦点不等于拼音上屏。但 OS 在这一刻给没给这扇窗一个 IME 上下文、那个上下文在中文档
    // 还是英文档，是读得出来的，而这两行恰好把「我们这扇窗没接上输入法」和「这台机器的
    // 输入法本来就在英文档」分开。所以把这一发加进抬起那一条实测结果里。
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr himc);
    [DllImport("imm32.dll")] public static extern bool ImmGetConversionStatus(IntPtr himc, out uint flags, out uint conversion);
    [DllImport("imm32.dll")] public static extern bool ImmGetOpenStatus(IntPtr himc);

    const uint ImeCmodeNative = 0x1;
    const uint ImeCmodeEnglish = 0x2;
    const uint ImeCmodeNoConversion = 0x4;

    /// <summary>这扇窗此刻的输入法状态，一句话。读不出的那几种（没有上下文）也照实说，
    /// 因为「<c>ImmGetContext</c> 给了 0」本身就是这条实测结果要的答案之一。</summary>
    public static string ImeReport(IntPtr hwnd)
    {
        var himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return "IME 上下文=没给（0）";
        try
        {
            var open = ImmGetOpenStatus(himc);
            if (!ImmGetConversionStatus(himc, out var flags, out var conversion))
                return $"IME 上下文=0x{himc:X} 开着={open} 转换状态读不出";
            var mode = (conversion & ImeCmodeNative) != 0 ? "中文档"
                : (conversion & ImeCmodeEnglish) != 0 ? "英文档"
                : (conversion & ImeCmodeNoConversion) != 0 ? "不转换"
                : $"0x{conversion:X}";
            return $"IME 上下文=0x{himc:X} 开着={open} 档={mode}";
        }
        finally
        {
            ImmReleaseContext(hwnd, himc);
        }
    }

    // ---- 注册表 ------------------------------------------------------------------
    //
    // 只需要 HKCU 下的一个字符串值，所以直接 advapi32，不引 Microsoft.Win32.Registry
    // 那个包：它在 net8.0 上是 NuGet 依赖，而这台机器上的构建是离线的。

    /// HKEY_CURRENT_USER。0x80000003 是 HKEY_USERS —— 拿它开 `Software\...` 会在
    /// RegOpenKeyExW 上回一个 87（ERROR_INVALID_PARAMETER），而且读的时候静悄悄返回 null。
    private static readonly nint HkeyCurrentUser = new(unchecked((int)0x80000001));
    private const int KeyRead = 0x20019;
    private const int KeyWrite = 0x20006;
    private const int RegSz = 1;
    private const int RegExpandSz = 2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorSuccess = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(nint hKey, string subKey, int options, int samDesired, out nint phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyExW(nint hKey, string subKey, int reserved, string? class_,
        int options, int samDesired, nint security, out nint phkResult, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(nint hKey, string valueName, int reserved, int type, byte[] data, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(nint hKey, string valueName, nint reserved,
        out int type, byte[]? data, ref int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValueW(nint hKey, string valueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteKeyW(nint hKey, string subKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(nint hKey);

    /// 打开一个键。create 那一发只在「这一步本来就要建它」时才把不存在的键建出来 ——
    /// 删一条不该顺手留下一个空键。失败的话返回码和一句话，让调用方分得清
    /// 「那里没有东西」（2）和「什么都没保证」（别的码）。
    private static string Open(string subKey, int wanted, bool create, out nint key, out int code)
    {
        code = RegOpenKeyExW(HkeyCurrentUser, subKey, 0, wanted, out key);
        if (code != ErrorFileNotFound || !create)
            return code == ErrorSuccess ? "" : $"RegOpenKeyExW(HKCU\\{subKey}, {wanted:X}) = {code}";
        code = RegCreateKeyExW(HkeyCurrentUser, subKey, 0, null, 0,
            KeyWrite | KeyRead, nint.Zero, out key, out _);
        return code == ErrorSuccess ? "" : $"RegCreateKeyExW(HKCU\\{subKey}, {KeyWrite | KeyRead:X}) = {code}";
    }

    /// 读一个字符串值。没有这个值返回 null —— 「没有」和「是空串」在登录项上是两件事。
    /// REG_EXPAND_SZ 也读：别的安装器写登录项常用它，那一串照样是 Windows 会执行的一条。
    public static string? RegReadString(string subKey, string valueName)
    {
        if (Open(subKey, KeyRead, false, out var key, out _) != "") return null;
        try
        {
            var size = 0;
            if (RegQueryValueExW(key, valueName, nint.Zero, out _, null, ref size) != ErrorSuccess) return null;
            var buffer = new byte[size];
            if (RegQueryValueExW(key, valueName, nint.Zero, out var type, buffer, ref size) != ErrorSuccess) return null;
            if (type is not (RegSz or RegExpandSz)) return null;
            // 终止的那个 NUL 有时在返回的字节数里，有时不在 —— 削掉，别让它进比较。
            return Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public static string? RegWriteString(string subKey, string valueName, string value)
    {
        var open = Open(subKey, KeyWrite, true, out var key, out _);
        if (open != "") return open;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            var code = RegSetValueExW(key, valueName, 0, RegSz, bytes, bytes.Length);
            return code == ErrorSuccess ? null : $"写 HKCU\\{subKey}\\{valueName} 返回 {code}";
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// 删一个值。键或值本来不在就算删成了 —— 调用方要的是「那里没有东西」。
    public static string? RegDeleteValue(string subKey, string valueName)
    {
        var open = Open(subKey, KeyWrite, false, out var key, out var code);
        if (code == ErrorFileNotFound) return null;
        if (open != "") return open;
        try
        {
            code = RegDeleteValueW(key, valueName);
            return code is ErrorSuccess or ErrorFileNotFound ? null : $"删 HKCU\\{subKey}\\{valueName} 返回 {code}";
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// 整个键删掉。自检用它把自己建出来的那个临时键收走 —— 谁建的谁清。
    public static string? RegDeleteKey(string subKey)
    {
        var code = RegDeleteKeyW(HkeyCurrentUser, subKey);
        return code is ErrorSuccess or ErrorFileNotFound ? null : $"删 HKCU\\{subKey} 返回 {code}";
    }

    // ---- 前台应用身份（工单 01）------------------------------------------------
    //
    // 这一族是为「前台是哪个应用」那一条链加的：GetForegroundWindow →
    // GetWindowThreadProcessId（上面已有）→ OpenProcess → QueryFullProcessImageNameW。
    // 每一发的返回值和 GetLastError 都落实测结果，因为 ACCESS_DENIED 那一档本身就是结论
    // （别的用户 / 系统进程连查询权都拿不到），而只有错误码能说清是哪一档。
    //
    // **权限号不许凭记忆写**：PROCESS_QUERY_LIMITED_INFORMATION = 0x1000，
    // PROCESS_QUERY_INFORMATION = 0x0400。拿错一位的后果是「查别的用户进程时看起来通」，
    // 所以两个都列出来，量的时候只发 LIMITED。

    /// <summary>只查属性、够 <c>QueryFullProcessImageNameW</c> 用的那一个查询权。
    /// 另一个 <c>PROCESS_QUERY_INFORMATION = 0x0400</c> 要的更多，这里不列，免得有人拿去用。</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    /// <summary><c>CharSet.Unicode</c> 是<b>必须</b>的：不给的话 <c>StringBuilder</c> 按 ANSI
    /// （每字一字节）传下去，而 W 那一支按宽字符写，520 字节的缓冲区被当宽缓冲区写爆 ——
    /// 测量程序那一次直接是访问冲突（2026-10-04 实测，exe 路径只印出一个 "C"）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>kernel32 的 <c>SetLastError</c>：把本线程的错误码写成给定的值。
    /// 托管那一侧<b>只有读的</b>（<c>Marshal.GetLastWin32Error</c>），没有可写的那个 ——
    /// 要清旧码就得走这一发。</summary>
    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint code);

    /// <summary>exe 全路径的容量，字符数。给 520 而不是 MAX_PATH，长路径那一档不截。</summary>
    public const int ExePathChars = 520;

    /// <summary>一个进程的 exe 全路径 —— 「前台是哪个应用」那条链的最后一发。
    ///
    /// 返回值把三档分得开：<b>打开成了但问不出路径</b>（<paramref name="code"/> 是 0 而
    /// 路径空）、<b>打开就被拒</b>（句柄 0、code 是 5）、<b>问通了</b>（路径有字）。
    /// 合并成一个字符串会把这三档说成一档，而它们要走的后续动作不一样 ——
    /// 「问不通」要退到窗口标题那一层，「被拒」要退到 pid 那一层。
    ///
    /// 用完就关句柄：<c>OpenProcess</c> 漏关会在长跑的测量程序里慢慢吃掉这一进程的句柄。
    /// </summary>
    public static (bool Opened, int Code, string Path) ExePathOf(uint processId)
    {
        // 先清 0 再问：上一次的错误码留在那儿的话，「成了」也会印出一个旧的失败码。
        // 清它的正路是 kernel32 那个同名导出（托管侧没有可写的那个，Marshal 上只有读的）。
        SetLastError(0);
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return (false, System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "");
        try
        {
            var buffer = new StringBuilder(ExePathChars);
            uint size = ExePathChars;
            var asked = QueryFullProcessImageNameW(handle, 0, buffer, ref size);
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return (true, asked ? 0 : code, asked ? buffer.ToString() : "");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- UWP 身份那两条路 ------------------------------------------------------
    //
    // 前台是商店应用时 GetForegroundWindow 给的那扇顶层窗属于 ApplicationFrameHost，
    // 不是应用自己。两条正路各自要的发散在这里：一条枚举子窗找 pid 不同的那个，
    // 一条问前台线程的焦点窗（GetGUIThreadInfo 传 0）。

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    /// <summary>前台线程那几只句柄，外加光标那一个矩形。
    ///
    /// <b>末尾是 <c>RECT rcCaret</c>，不是第七个句柄。</b> 2026-10-04 第一版照着「七个 HWND」写，
    /// 结构体是 64 字节，于是 <c>cbSize</c> 交上去 64，<c>GetGUIThreadInfo</c> 每一发都回
    /// <b>ERROR_INVALID_PARAMETER(87)</b> —— 包括对普通窗口也一样，看上去像「这一族在 Win11 上不通」。
    /// 正本是本机 SDK 头 <c>winuser.h</c>：六个 HWND 之后跟一个 <c>RECT</c>，
    /// 所以正确的 <c>cbSize</c> 是 72。**这个族的实测结果说「不通」时先量 <c>cbSize</c>**。
    /// <c>cbSize</c> 必填，且必须等于本结构体的大小（它随 x86/x64 变宽），
    /// 所以交给 <c>Marshal.SizeOf</c> 算，不写死。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public uint CbSize;
        public uint Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public Rect RcCaret;
    }

    /// <summary>idThread 传 0 就是问前台线程（文档原话）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    /// <summary>UWP 应用自己那个进程有没有包身份，以及包的全名。
    /// <c>GetPackageFullName</c> 在没有包身份时回 <c>APPMODEL_ERROR_NO_PACKAGE = 15700</c>，
    /// 而不是 0 —— 那个数是这一族唯一能认出「这不是 UWP」的信号，所以原样交出去。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? fullName);

    /// <summary>没有包身份时 <c>GetPackageFullName</c> 给的那个码（<c>APPMODEL_ERROR_NO_PACKAGE</c>）。</summary>
    public const int AppModelErrorNoPackage = 15700;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>。<b>第一发回 122 是正常的一步，不是失败</b>：
    /// <c>GetPackageFullName</c> 的两发形状是「第一发长度给 0、缓冲区给 NULL，回 122 并把长度写回来；
    /// 第二发给够长度才回 0」。2026-10-04 第一版把「第一发非 0」一律当失败，
    /// 于是一台装成商店包的应用（那一次是记事本）读出来是 <c>包名码=122 包名=&lt;空&gt;</c> ——
    /// 看着像「它没有包身份」，其实有，只是没问到。</summary>
    public const int ErrorInsufficientBuffer = 122;

    /// <summary>一个进程的包全名（<c>家族名</c>，也就是 AUMID 里 <c>!</c> 前半段）。
    /// 没有包身份时是 <c>NO_PACKAGE</c>，别把它读成「打开失败」。
    ///
    /// <b>句柄是它自己的那个进程</b>，不是当前进程：<c>hProcess</c> 传 0 的文档语义是
    /// 「问当前进程」，而测量程序要问的是<b>前台那个</b>进程 —— 传 0 会把测量程序自己（一个没有包身份的
    /// 控制台 exe）的答案当成前台的答案，而那个答案恒是 <c>NO_PACKAGE</c>。
    /// 用的是和 exe 路径同一个查询权：<c>GetPackageFullName</c> 接受 LIMITED。</summary>
    public static (int Code, string FullName) PackageFullNameOf(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return ((int)System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "<句柄没开>");
        try
        {
            // 两发形状：第一发长度给 0、缓冲区给 NULL，回 ERROR_INSUFFICIENT_BUFFER 并把长度写回来；
            // 第二发给够长度才回 0。**122 是正常的一步**，当失败处理就会把「有包身份」读成「没有」。
            uint length = 0;
            var first = GetPackageFullName(handle, ref length, null);
            if (first == AppModelErrorNoPackage) return (first, "NO_PACKAGE");
            if (first != ErrorInsufficientBuffer || length == 0) return (first, first == 0 ? "<空>" : "");
            var buffer = new StringBuilder((int)length);
            var second = GetPackageFullName(handle, ref length, buffer);
            return (second, second == 0 ? buffer.ToString() : "");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>窗口自己的 AUMID（<c>PKEY_AppUserModel_ID</c>）。非商店应用的窗通常没有这一项。
    ///
    /// 走 <c>IPropertyStore</c>（COM）而不是猜：<c>SHGetPropertyStoreForWindow</c> 是
    /// shell32 的正路。**实测结果里要连 HRESULT 一起看** —— 那一格空的既可能是「这扇窗没有
    /// AUMID」，也可能是 COM 这一层自己没接上，而这两者的下一步不一样，所以
    /// <paramref name="storeReplied"/> 和 <paramref name="valueCode"/> 分开交。
    ///
    /// <b>它是三个参数的</b>（<c>hwnd, riid, ppv</c>），签名取自本机 SDK 头（见上面那一发）。
    /// </summary>
    public static (bool StoreReplied, int StoreCode, int ValueCode, string Aumid) WindowAumid(IntPtr hWnd)
    {
        var riid = PropertyStoreGuid;
        var raw = IntPtr.Zero;
        var storeCode = ShellGetPropertyStoreForWindow(hWnd, ref riid, ref raw);
        if (storeCode != 0 || raw == IntPtr.Zero) return (false, storeCode, 0, "");
        object? com = null;
        try
        {
            // CA1416 在这里没有对手：这一整个类只服务 Windows，见上面 ShowDesktop 那段同一句话。
#pragma warning disable CA1416
            com = Marshal.GetObjectForIUnknown(raw);
#pragma warning restore CA1416
            var store = (IPropertyStore)com;
            var value = new PropVariant();
            var valueCode = store.GetValue(ref AumidKey, out value);
            if (valueCode != 0) return (true, storeCode, valueCode, "");
            // 先把字拷出来再清：清完那个指针就没了，而清是**必须**的 ——
            // 不清就是每个前台窗口漏一块 COM 分配，长跑的测量程序会慢慢吃掉一堆。
            var aumid = value.AsString();
            PropVariantClear(ref value);
            return (true, storeCode, 0, aumid);
        }
        catch (Exception)
        {
            // COM 这一层抛了也不能让测量程序断在半路：实测结果缺这一格要能看出来，而不是整轮没印。
            return (true, storeCode, -1, "");
        }
        finally
        {
            // CA1416 在这里没有对手：这一整个类只服务 Windows，见上面 ShowDesktop 那段同一句话。
#pragma warning disable CA1416
            if (com != null) Marshal.ReleaseComObject(com);
#pragma warning restore CA1416
        }
    }

    /// <summary><c>IID_IPropertyStore</c>。这一发是 <c>riid</c> 那个入参，要的正是这个 IID。</summary>
    private static Guid PropertyStoreGuid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary><c>PKEY_AppUserModel_ID</c>。GUID 和 pid 都照 <c>propkey.h</c> 抄，
    /// 不从别的地方推 —— pid 错一位会安静地读出别的那一项。</summary>
    private static PropertyKey AumidKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    /// <summary><b>三个参数</b>：<c>hwnd, riid, ppv</c>。
    ///
    /// 2026-10-04 这一发栽了两次，两次都是凭印象写签名，代价是测量程序直接访问冲突（一发实测结果都没出来）：
    /// 第一版按「hwnd, flags, store」三个写（把第二个当成了旗标），第二版「修」成四个参数
    /// （hwnd, flags, riid, ppv）。<b>正本是本机的 SDK 头</b>
    /// <c>Windows Kits\10\Include\10.0.26100.0\um\shellapi.h</c>：
    /// <c>SHSTDAPI SHGetPropertyStoreForWindow(_In_ HWND hwnd, _In_ REFIID riid, _Outptr_ void** ppv);</c>
    /// —— 三个参数，第二个就是 riid，<b>没有旗标</b>。写这族声明之前先读那一行，别照记忆改。
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "SHGetPropertyStoreForWindow")]
    private static extern int ShellGetPropertyStoreForWindow(
        IntPtr hWnd, ref Guid riid, ref IntPtr propertyStore);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary><c>PROPVARIANT</c>。只取这一族要的那两个字段（类型 + 那个指针），
    /// 结构体本身按 16 字节垫 —— x64 上它就是 16 字节，少垫会被 COM 当成读坏了堆。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] private nint _pointer;

        /// <summary><c>VT_LPWSTR = 31</c>：值是那个指针指着的一串宽字符，归这一层所有。
        /// 别的类型一律说成空 —— 猜一种类型的布局就是把实测结果写成编的。</summary>
        public string AsString() =>
            Type == 31 && _pointer != IntPtr.Zero ? Marshal.PtrToStringUni(_pointer) ?? "" : "";
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        // IUnknown 那三个不算进签名，方法要从第一个真正属于这个接口的（GetCount）开始。
        int GetCount(out uint count);

        int GetAt(uint index, out PropertyKey key);

        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // ---- 勿扰状态（QUNS）--------------------------------------------------------
    //
    // 官方语义和已知坑在 docs/research/detection-module-api-survey.md 第 3 节。
    // 这里只管把它叫出来、把值译成它自己的名字，**不替它加一层判断** ——
    // 「QUNS_BUSY 算不算独占全屏」是实测结果出来之后才定的事。
    //
    // **它只有一个参数。** 2026-10-04 第一版照着「传 hwnd、或者传 0 两种形状」写成两个参数，
    // 跑起来是 EntryPointNotFoundException。正本是本机 SDK 头
    // `Windows Kits\10\Include\10.0.26100.0\um\shellapi.h`：
    // `SHSTDAPI SHQueryUserNotificationState(_Out_ QUERY_USER_NOTIFICATION_STATE *pquns);`
    // —— 没有 hwnd，所以「传 0 还是传前台那扇窗」这个问题**不存在**，别再去设计那一发对照。

    // 这五个数**全部取自本机 SDK 头** `shellapi.h` 那一段枚举，逐条抄的。
    // 2026-10-04 第一版写成 0..4（照着「枚举从 0 开始」的想当然），跑出来是 `QUNS_?(5)`
    // —— 而 5 正是 QUNS_ACCEPTS_NOTIFICATIONS，也就是文档说「只有它才发 UI」的那一档。
    // 差一位的后果是**永远判不成「可以发通知」**：结论看着像在跑，其实整族偏了一格。
    // 那一段还有两个不在任何网页文档列表里的值（QUNS_QUIET_TIME、QUNS_APP），
    // 少抄一个就等于「认不出来的数」，所以两个都列上。
    public const int QunsNotPresent = 1;              // 屏保 / 锁屏 / 非活动快速用户切换
    public const int QunsBusy = 2;                     // 启发式：有全屏程序
    public const int QunsRunningD3dFullScreen = 3;     // 独占模式 D3D
    public const int QunsPresentationMode = 4;         // 演示设置（笔记本那个功能）
    public const int QunsAcceptsNotification = 5;      // 可以自由发通知
    public const int QunsQuietTime = 6;                // OOBE 静默期
    public const int QunsApp = 7;                      // 应用模式

    /// <summary>导出名必须写死成 <c>SHQueryUserNotificationState</c>：C# 这一族的方法名开头是
    /// Shell（<c>SHQuery…</c>），而导出名开头是 SH，交给 marshaller 去拼只会得到
    /// EntryPointNotFoundException。dumpbin /exports shell32.dll 逐条看过，导出确实在。</summary>
    [DllImport("shell32.dll", EntryPoint = "SHQueryUserNotificationState")]
    private static extern int ShellQueryUserNotificationState(out int state);

    /// <summary>问一次勿扰状态，答出 HRESULT 和「Windows 给的那个数」两样。
    ///
    /// HRESULT 是**返回值**不是 <c>GetLastError</c>（这一发不设 <c>SetLastError</c>），
    /// 拿 <c>GetLastError</c> 去读它会读出上一发留下的旧码。</summary>
    public static (int HResult, int State) QueryUserNotificationState()
    {
        var hresult = ShellQueryUserNotificationState(out var state);
        return (hresult, state);
    }

    /// <summary>那个数自己的名字。<b>取自本行，不另存一份副本</b>；认不出来的数原样
    /// 报出来（它可能是文档里没有的新值，硬译成一个近似的名就是编实测结果）。</summary>
    public static string NotificationStateName(int state) => state switch
    {
        QunsNotPresent => "QUNS_NOT_PRESENT",
        QunsBusy => "QUNS_BUSY",
        QunsRunningD3dFullScreen => "QUNS_RUNNING_D3D_FULL_SCREEN",
        QunsPresentationMode => "QUNS_PRESENTATION_MODE",
        QunsAcceptsNotification => "QUNS_ACCEPTS_NOTIFICATION",
        QunsQuietTime => "QUNS_QUIET_TIME",
        QunsApp => "QUNS_APP",
        _ => $"QUNS_?({state})",
    };
}
