using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DebugProbe;

/// <summary>一扇顶层窗的读数。</summary>
/// <param name="Handle">句柄。每一轮启动都不一样，不许写死。</param>
/// <param name="ClassName">窗口类名。实测出来的才写进代码，见研究笔记。</param>
/// <param name="Title">标题。托盘那一族和找日历那一阵要靠它缩小范围。</param>
/// <param name="ProcessId">属主进程。</param>
/// <param name="Visible"><c>IsWindowVisible</c> 的读数。</param>
/// <param name="Rect">屏幕坐标下的矩形。</param>
internal sealed record WindowInfo(
    IntPtr Handle,
    string ClassName,
    string Title,
    int ProcessId,
    bool Visible,
    RectI Rect);

/// <summary>一个整数矩形。<c>RECT</c> 的托管那份。</summary>
internal readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;

    internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>那一块有没有<b>整块</b>落在这个矩形里。空的那一块答否。</summary>
    internal bool Contains(RectI other) =>
        other.Width > 0 && other.Height > 0 &&
        Contains(other.Left, other.Top) && Contains(other.Right - 1, other.Bottom - 1);

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>一个点。</summary>
internal readonly record struct PointI(int X, int Y);

/// <summary>一块显示器的读数。<c>MONITORINFO</c> 的托管那份。</summary>
internal readonly record struct MonitorInfo(RectI Monitor, RectI Work, bool IsPrimary)
{
    internal int Width => Monitor.Width;
    internal int Height => Monitor.Height;
    internal int WorkWidth => Work.Width;
    internal int WorkHeight => Work.Height;
}

/// <summary>
/// Win32 那一层的声明。量的是<b>别的进程</b>眼里的屏幕 ——
/// 和引擎在同一个进程里量到的是「引擎以为的那一份」，那一份不作数。
/// </summary>
internal static class Windows
{
    private const int MaxClassLength = 256;
    private const int MaxTitleLength = 512;

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2。进程起来第一件事就要设它，
    // 不设的话在 125% 缩放的屏上抓到的矩形和屏幕上那扇对不上，差值会差一个倍数。
    internal static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    internal delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameRaw(IntPtr handle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextRaw(IntPtr handle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr handle);

    /// <summary><c>GWL_EXSTYLE</c>。读另一扇窗的扩展样式，和进程无关。</summary>
    private const int GwlExStyle = -20;

    /// <summary><c>WS_EX_LAYERED</c>。**这一位就是「这一扇正被借去当日历」的标记** ——
    /// 壳那一堆子窗里只有它会被加上这一位（产品借走它之后加的），
    /// 而判据里少了它就会数出别的窗来。</summary>
    private const long WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrRaw(IntPtr handle, int index);

    /// <summary>那一扇带不带 <c>WS_EX_LAYERED</c>。0 句柄答假。</summary>
    internal static bool IsLayered(IntPtr handle) =>
        handle != IntPtr.Zero && (GetWindowLongPtrRaw(handle, GwlExStyle).ToInt64() & WsExLayered) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(PointNative point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr handle, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetTopWindow(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>本进程那枚令牌的句柄。<c>GetCurrentProcess</c> 给的是伪句柄，
    /// 令牌那一族 API 要的是真的那个，得自己开。</summary>
    private static IntPtr ProcessToken()
    {
        OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out IntPtr token);
        return token;
    }

    private static IntPtr OpenTokenOf(int processId)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == IntPtr.Zero) return IntPtr.Zero;
        OpenProcessToken(process, 0x0008, out IntPtr token);
        CloseHandle(process);
        return token;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    /// <summary>
    /// <c>PROCESS_BASIC_INFORMATION</c>。字段按 x64 上的实际偏移摆，<b>不能省字段</b> ——
    /// 父进程号在第 40 字节，少摆一个字段就读到 PEB 地址上去了（读出来的是「读不到父进程」）。
    /// 这不是推理，是结构体的真实布局。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParentInfo
    {
        internal IntPtr ExitStatus;          // 0
        internal IntPtr PebBaseAddress;      // 8
        internal IntPtr AffinityMask;        // 16
        internal IntPtr BasePriority;        // 24
        internal IntPtr UniqueProcessId;     // 32
        internal IntPtr InheritedFromProcess; // 40
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, ref ParentInfo info, int length, out int returnLength);

    /// <summary>
    /// 父进程那一枚句柄。<paramref name="check"/> 带一句自校验出来：
    /// 结构体里那个「本进程进程号」要等于真的本进程进程号 —— 不等就是布局错了，
    /// 那时后面读到的每一个数都是别的字段，**读数像模像样而全是错的**。
    /// </summary>
    private static IntPtr ParentProcess(out string check)
    {
        var info = new ParentInfo();
        if (NtQueryInformationProcess(GetCurrentProcess(), 0, ref info, Marshal.SizeOf<ParentInfo>(), out _) != 0)
        {
            check = "结构体那一问失败，";
            return IntPtr.Zero;
        }

        int self = System.Diagnostics.Process.GetCurrentProcess().Id;
        check = info.UniqueProcessId.ToInt64() == self ? string.Empty : "结构体布局对不上（本进程那一栏读错了），";
        return info.InheritedFromProcess;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendMessageTimeout(
        IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(PointNative point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorNative info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorNative
    {
        internal int Size;
        internal RectNative Monitor;
        internal RectNative Work;
        internal int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        internal int X;
        internal int Y;
    }

    /// <summary>进程起来第一件事调它。返回 false 时不是失败 —— 已经被谁设过了也算。</summary>
    internal static void MakeDpiAware()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    internal static string ClassOf(IntPtr handle)
    {
        var buffer = new StringBuilder(MaxClassLength);
        int n = GetClassNameRaw(handle, buffer, MaxClassLength);
        return n <= 0 ? string.Empty : buffer.ToString(0, Math.Min(n, MaxClassLength - 1));
    }

    internal static string TitleOf(IntPtr handle)
    {
        var buffer = new StringBuilder(MaxTitleLength);
        int n = GetWindowTextRaw(handle, buffer, MaxTitleLength);
        return n <= 0 ? string.Empty : buffer.ToString(0, Math.Min(n, MaxTitleLength - 1));
    }

    internal static int ProcessIdOf(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out uint pid);
        return (int)pid;
    }

    internal static RectI RectOf(IntPtr handle)
    {
        if (!GetWindowRect(handle, out RectNative raw)) return new RectI(0, 0, 0, 0);
        return new RectI(raw.Left, raw.Top, raw.Right, raw.Bottom);
    }

    internal static IntPtr At(int x, int y) => WindowFromPoint(new PointNative { X = x, Y = y });

    /// <summary>父窗。<c>SetParent</c> 挂进桌面层之后，这一扇的爹是壁纸层那几扇之一。</summary>
    internal static IntPtr ParentOf(IntPtr handle) => GetParent(handle);

    /// <summary>那一扇的根窗。命中测试要分「那一扇归谁」和「根归谁」，靠的就是这一对。</summary>
    /// <summary>
    /// 本进程那一枚令牌的完整性级别。
    ///
    /// <para>2026-10-12 量到的一发：注入的按键<b>一条都进不了系统输入队列</b>，
    /// 而 <c>SendInput</c> 仍答「插进去 N 条」。低完整性进程发出来的按键被系统挡在门外，
    /// 是这一族能解释那个形状的其中一条 —— 所以这一问要能自己答，
    /// 别靠「应该是中完整性吧」那种印象。</para>
    /// </summary>
    internal static string IntegrityLevel()
    {
        string mine = IntegrityOf(ProcessToken());
        string parent = ParentIntegrity();
        string explorer = ExplorerIntegrity();
        string product = ProductIntegrity();
        return $"本进程 {mine}；父进程 {parent}；explorer {explorer}；产品 {product}";
    }

    /// <summary>本进程这一枚令牌的完整性 RID（0x1000 低 / 0x2000 中 / 0x3000 高），
    /// 读不到答 −1。<b>入口那一问用它，不看 <see cref="IntegrityLevel"/> 那句话</b> ——
    /// 那一族所有子命令都要读别的进程，读不成时不能让它继续跑出假读数。
    ///
    /// <b>为什么需要这一问 —— 2026-10-13 一次实测</b>：仓库目录
    /// <c>Keeper</c> 上有两条<b>显式</b>（非继承）的 ACE，给
    /// <c>Everyone</c> 和一个能力 SID（<c>S-1-4-963651649-261417728</c>）
    /// 留了 <c>DeleteSubdirectoriesAndFiles</c> / <c>Write</c>。Windows 把低权限主体能改的
    /// 目录当<b>不可信位置</b>，<b>从这里启动的 exe 一律降成低完整性运行</b>。
    /// 同一个二进制放在 <c>%TEMP%</c> 下答 0x2000，放在 <c>bin</c> 下答 0x1000 ——
    /// <b>和程序本身无关，是路径上的 ACL 决定的</b>。
    /// 父目录 <c>Desktop\project</c> 没有那两条 ACE，所以它答 0x2000。
    internal static int RidOfSelf() => RidOf(ProcessToken());

    /// <summary>桌面图标层那一扇的进程（<c>explorer.exe</c>）的完整性 RID。
    /// 这一族要读的那个进程 —— <b>读数只有和它一样高才作数</b>。</summary>
    internal static int RidOfExplorer()
    {
        foreach (WindowInfo w in TopLevelWindows())
            if (w.ClassName == Find.ProgmanClass) return RidOf(OpenTokenOf(w.ProcessId));
        return -1;
    }

    /// <summary>完整性 RID 的裸读数。读不到答 −1。</summary>
    private static int RidOf(IntPtr token)
    {
        if (token == IntPtr.Zero) return -1;
        const int TokenIntegrityLevel = 25;
        int length = 0;
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
        if (length == 0) return -1;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length)) return -1;
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(sid, 1);
            return Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>父进程是谁、它那一枚令牌的完整性级别。
    /// 子进程的级别 Normally 从父进程继承，所以这一栏是「本进程那一栏读错了没有」的对照。</summary>
    private static string ParentIntegrity()
    {
        string self;
        IntPtr parent = ParentProcess(out self);
        // 0 是 Idle，不是任何一个真进程。照实报「读不到」，不把 0 当成一个叫 Idle 的父进程报出去。
        int id = parent == IntPtr.Zero ? 0 : ProcessIdOf(parent);
        if (id == 0) return $"{self}读不到（父进程号读出来是 0，0 是 Idle 不是进程）";
        return $"{self}{ProductProcess.NameOf(id)}（pid {id}）{IntegrityOf(OpenTokenOf(id))}";
    }

    /// <summary>
    /// <c>TokenElevationType</c> 那一条。<b>它分不出完整性级别</b>（量于 10-12）：
    /// 低完整性（apphost）和中完整性（<c>dotnet exec</c>）两种跑法下它都答 3。
    /// 所以它留在这里是为了留证据，<b>不是</b>用来当完整性级别的读数的 ——
    /// 一个两种级别都答同一个数的读数，拿它当判据就是把「没量到」说成「量到了」。
    /// 完整性级别那一栏以 SID 里数出来的 RID 为准（那一栏有三处行为对照撑着）。
    /// </summary>
    internal static string ElevationType()
    {
        const int TokenElevationTypeClass = 18;
        int length = 0;
        // 第一遍一定答假、错误码 122（缓冲区不够）—— 那是它在告诉我要多大，不是不给读。
        // 把这一遍的返回值当失败，就是把「答对了尺寸」读成「读不到」。
        GetTokenInformation(ProcessToken(), TokenElevationTypeClass, IntPtr.Zero, 0, out length);
        if (length < sizeof(int))
        {
            return $"读不到，错误码 {Marshal.GetLastWin32Error()}";
        }

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(ProcessToken(), TokenElevationTypeClass, buffer, length, out _))
            {
                return $"读不到，错误码 {Marshal.GetLastWin32Error()}";
            }

            return Marshal.ReadInt32(buffer) switch
            {
                1 => "1（默认，即中或高）",
                2 => "2（完整）",
                3 => "3（受限，即低完整性那种令牌）",
                var other => $"{other}（不认识）",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 产品那一份的完整性级别。<b>它跟量具不是同一级别，这件事要量出来，
    /// 不能从「管道标签取自建它的进程」反推。</b>
    /// </summary>
    private static string ProductIntegrity()
    {
        List<int> pids = ProductProcess.RunningProductIds();
        return pids.Count == 0
            ? "产品没在跑"
            : string.Join("、", pids.Select(id => $"pid {id} {IntegrityOf(OpenTokenOf(id))}"));
    }

    private static string ExplorerIntegrity()
    {
        foreach (WindowInfo w in TopLevelWindows())
        {
            if (w.ClassName != "Progman") continue;
            return $"{IntegrityOf(OpenTokenOf(w.ProcessId))}";
        }

        return "读不到 explorer";
    }

    private static string IntegrityOf(IntPtr token)
    {
        const int TokenIntegrityLevel = 25;
        int length = 0;
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
        if (length == 0) return $"读不到，错误码 {Marshal.GetLastWin32Error()}";
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out length))
            {
                return $"读不到，错误码 {Marshal.GetLastWin32Error()}";
            }

            IntPtr sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(sid, 1);
            int rid = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            string name = rid switch
            {
                0x0000 => "不受信",
                0x1000 => "低",
                0x2000 => "中",
                0x3000 => "高",
                0x4000 => "系统",
                _ => "未知",
            };
            return $"{name}（RID 0x{rid:X}）";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static IntPtr RootOf(IntPtr handle) => GetAncestor(handle, 2 /* GA_ROOT */);

    /// <summary>
    /// z 序最上面那几扇，从上往下数。
    ///
    /// <para><b>方向是量出来的</b>：<c>GW_HWNDNEXT</c> 往<b>下</b>一扇。
    /// 「Win+D 之后有没有生效」这一问问的就是这个次序 ——
    /// 这一版 Windows 的「显示桌面」不是把每一扇最小化，是把桌面端到前面来，
    /// 于是 <c>IsIconic</c> 那一问在这一族上根本没有判别力。</para>
    /// </summary>
    internal static List<WindowInfo> ZOrderTop(int count)
    {
        var found = new List<WindowInfo>();
        IntPtr at = GetTopWindow(IntPtr.Zero);
        for (int i = 0; at != IntPtr.Zero && i < count; i++)
        {
            found.Add(Describe(at));
            at = GetWindow(at, 2 /* GW_HWNDNEXT */);
        }

        return found;
    }

    /// <summary>那一扇所在那块显示器的读数。矩形对不上就是 DPI 没设对。</summary>
    internal static MonitorInfo MonitorOf(IntPtr handle)
    {
        return ReadMonitor(MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */));
    }

    /// <summary>主屏那块显示器。</summary>
    internal static MonitorInfo PrimaryMonitor()
    {
        return ReadMonitor(MonitorFromPoint(new PointNative { X = 0, Y = 0 }));
    }

    private static MonitorInfo ReadMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return new MonitorInfo(default, default, false);
        var info = new MonitorNative { Size = Marshal.SizeOf<MonitorNative>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return new MonitorInfo(default, default, false);
        }

        return new MonitorInfo(
            new RectI(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            new RectI(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            (info.Flags & 1 /* MONITORINFOF_PRIMARY */) != 0);
    }

    /// <summary>枚举屏幕上所有顶层窗。</summary>
    internal static List<WindowInfo> TopLevelWindows()
    {
        var found = new List<WindowInfo>();
        EnumWindowsProc callback = (handle, _) =>
        {
            found.Add(Describe(handle));
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    /// <summary>枚举本进程之外的顶层窗。真窗，不含本进程自己那几扇。</summary>
    internal static List<WindowInfo> TopLevelWindowsOutside(int processId)
    {
        return TopLevelWindows().Where(w => w.ProcessId != processId).ToList();
    }

    internal static List<WindowInfo> ChildWindows(IntPtr parent)
    {
        var found = new List<WindowInfo>();
        EnumWindowsProc callback = (handle, _) =>
        {
            found.Add(Describe(handle));
            return true;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    /// <summary>一层层往下数。图标层是子窗的子窗，只数一层会漏。</summary>
    internal static List<WindowInfo> Descendants(IntPtr root, int maxDepth = 8)
    {
        var found = new List<WindowInfo>();
        Walk(root, 0, found, maxDepth);
        return found;
    }

    private static void Walk(IntPtr parent, int depth, List<WindowInfo> found, int maxDepth)
    {
        if (parent == IntPtr.Zero || depth >= maxDepth) return;
        foreach (WindowInfo child in ChildWindows(parent))
        {
            found.Add(child);
            Walk(child.Handle, depth + 1, found, maxDepth);
        }
    }

    /// <summary>
    /// 桌面图标那一层。类名 <c>SysListView32</c>，是壁纸层的<b>子窗</b> ——
    /// <c>FindWindow</c> 只找顶层窗，走那一条路只会答「没有」。
    /// </summary>
    internal static WindowInfo? IconLayer()
    {
        foreach (WindowInfo layer in TopLevelWindows().Where(w => Find.IsWallpaperLayer(w.ClassName)))
        {
            WindowInfo? icon = Descendants(layer.Handle)
                .FirstOrDefault(w => string.Equals(w.ClassName, Find.IconViewClass, StringComparison.Ordinal));
            if (icon != null) return icon;
        }

        return null;
    }

    /// <summary>
    /// 图标有几条。这一问是 <c>LVM_GETITEMCOUNT</c>，一条消息就答得出来，
    /// 不传指针，所以<b>跨进程问得到</b>。位置那一条问法不一样，见工单 07。
    /// </summary>
    internal static int IconCount(IntPtr iconLayer)
    {
        const uint LvmGetItemCount = 0x1004;
        IntPtr result = IntPtr.Zero;
        bool ok = SendMessageTimeout(iconLayer, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero, 0, 2000, out result);
        LastIconCountError = ok ? 0 : Marshal.GetLastWin32Error();
        return ok ? (int)result.ToInt64() : -1;
    }

    /// <summary>同一条 <c>LVM_GETITEMCOUNT</c>，走 <c>SendMessage</c> 那条老路（不走超时那一支）。
    ///
    /// <para>这是分「整族消息都不通」和「超时那一支特殊」的那一发 ——
    /// 2026-10-12 <c>SendMessageTimeout</c> 答错误码 5，而 <c>WM_NULL</c> 通。
    /// 两条路哪一种通，落到读法上就不一样（见工单 07）。</para>
    ///
    /// <para><b>只许在诊断那一发里用</b>：<c>SendMessage</c> 不带超时，
    /// 对方不答就一直等。</para>
    /// </summary>
    internal static string IconCountPlain(IntPtr iconLayer)
    {
        const uint LvmGetItemCount = 0x1004;
        return $"SendMessage 那条路：{SendMessage(iconLayer, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt64()}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// <c>LVM_HITTEST</c> = <b>0x1014</b>（<c>um/winuser.h</c>，10.0.26100.0）。
    /// <c>lParam</c> 是<b>客户区</b>坐标（<c>MAKELPARAM</c> 拼的两个 16 位）。
    /// 这一条<b>原样返回</b>：它到底答什么，光看文档说不准 —— 量。
    ///
    /// <para><b>另一条 <c>0x1032</c> 一并问，是为了找一条能用的替代读法。</b>
    /// 产品那一问（<c>src/Win32.cs</c> 的 <c>IconHit</c>）把 <c>LVM_HITTEST</c> 的返回值
    /// 非负当成「那一点上有一个图标」，而 2026-10-12 量到它<b>处处答 0</b> ——
    /// 于是那一句恒为真，闸门恒闭。知道坏在哪还不够，得知道<b>哪一条能答</b>。
    /// <b>0x1032 这个数是候选，不是抄来的</b>，所以它和别的几颗一起原样打出来，
    /// 由读数说它是什么。</para>
    /// </summary>
    internal const uint LvmHitTest = 0x1014;

    /// <summary>候选消息号，身份待量。</summary>
    internal const uint LvmCandidate1032 = 0x1032;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr handle, ref PointNative point);

    /// <summary>
    /// 图标层对屏幕那一点的命中测试，<b>原样把两把尺子的数摆出来</b>：
    /// <c>SendMessage</c> 那条老路和 <c>SendMessageTimeout</c> 那条带超时的路各问一遍，
    /// 再把换出来的客户区坐标、还有两条候选消息的返回值也报出来。
    ///
    /// <para><b>不在这一个方法里下结论</b>：返回值算「有图标」还是「没图标」由读数说，
    /// 这里只负责把数原样拿出来。</para>
    /// </summary>
    internal static string IconHitRaw(IntPtr iconLayer, int x, int y)
    {
        var screen = new PointNative { X = x, Y = y };
        var client = screen;
        if (!ScreenToClient(iconLayer, ref client))
        {
            return $"屏幕 ({x},{y}) → ScreenToClient 答假（错误码 {Marshal.GetLastWin32Error()}）";
        }

        var lp = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
        long plain = SendMessage(iconLayer, LvmHitTest, IntPtr.Zero, lp).ToInt64();

        string timed;
        bool ok = SendMessageTimeout(iconLayer, LvmHitTest, IntPtr.Zero, lp, 0, 2000, out IntPtr result);
        timed = ok ? result.ToInt64().ToString() : $"不通（错误码 {Marshal.GetLastWin32Error()}）";

        long candidate = SendMessage(iconLayer, LvmCandidate1032, IntPtr.Zero, lp).ToInt64();
        int count = IconCount(iconLayer);

        return $"屏幕 ({x},{y}) → 客户区 ({client.X},{client.Y})；" +
               $"LVM_HITTEST 原样返回值：SendMessage {plain}、SendMessageTimeout {timed}；" +
               $"候选 0x1032 原样返回值 {candidate}；同一次对话里 LVM_GETITEMCOUNT 答 {count}" +
               $"（这一颗答得出来才说明消息真的送到了这一扇）";
    }

    /// <summary>上一次 <see cref="IconCount"/> 的 Win32 错误码。读数为负时看它。</summary>
    internal static int LastIconCountError { get; private set; }

    /// <summary>
    /// 往一扇别的进程的窗发一条最无害的消息，问「消息这一条路通不通」。
    /// <c>WM_NULL</c> 不带任何参数、不改任何状态 —— 它答不出来就只说明消息送不到。
    /// </summary>
    internal static string PingMessage(IntPtr handle)
    {
        IntPtr result = IntPtr.Zero;
        bool ok = SendMessageTimeout(handle, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0, 2000, out result);
        return ok ? $"通（回 {result.ToInt64()}）" : $"不通，错误码 {Marshal.GetLastWin32Error()}";
    }

    /// <summary>
    /// 桌面图标那一扇（<c>SHELLDLL_DefView</c>）—— 日历那一扇的宿主。
    ///
    /// <para>判据是「类名 + 可见 + 满屏」，和产品侧 <c>src/DesktopLayer.cs</c> 的
    /// <c>DesktopLayer.CalendarHost</c>（一份快照加纯问句，卡片 04）是同一套
    /// （满屏那一栏是宽 ≥ 1000、高 ≥ 600）。<b>枚举的是 <c>Progman</c> 的全部后代</b> ——
    /// 图标层是子窗的子窗，只数一层会漏。</para>
    ///
    /// <para><b>多于一扇就答没挑中</b>，不猜是哪一扇。挑错了宿主，后面那一问量的
    /// 就是另一棵树。</para>
    /// </summary>
    internal static WindowInfo? IconLayerHost()
    {
        IntPtr progman = DesktopWindow();
        if (progman == IntPtr.Zero) return null;

        const int fullWidth = 1000;
        const int fullHeight = 600;
        List<WindowInfo> shaped = Descendants(progman)
            .Where(w => string.Equals(w.ClassName, Find.IconLayerHostClass, StringComparison.Ordinal))
            .Where(w => w.Visible && w.Rect.Width >= fullWidth && w.Rect.Height >= fullHeight)
            .ToList();

        return shaped.Count == 1 ? shaped[0] : null;
    }

    /// <summary>
    /// 图标层那一层的所有 <c>SysListView32</c>。挑图标层不能只认第一扇。
    /// </summary>
    internal static List<WindowInfo> IconLayerCandidates()
    {
        var found = new List<WindowInfo>();
        foreach (WindowInfo layer in TopLevelWindows().Where(w => Find.IsWallpaperLayer(w.ClassName)))
        {
            found.AddRange(Descendants(layer.Handle)
                .Where(w => string.Equals(w.ClassName, Find.IconViewClass, StringComparison.Ordinal)));
        }

        return found;
    }

    /// <summary>
    /// 前台那一扇连同它的完整性级别。<b>这一栏决定注入那一族那些读数挂在哪条原因上</b>：
    /// 注入的按键是发给前台那一扇的，前台比注入者高的话 UIPI 会把它吃掉。
    /// 读不到就照实说读不到。
    /// </summary>
    internal static string ForegroundDescription()
    {
        IntPtr handle = GetForegroundWindowRaw();
        if (handle == IntPtr.Zero) return $"读不到句柄，错误码 {Marshal.GetLastWin32Error()}";

        int pid = ProcessIdOf(handle);
        string name = ProductProcess.NameOf(pid);
        return $"{ClassOf(handle)}（pid {pid} {name}）{IntegrityOf(OpenTokenOf(pid))}";
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    internal static extern IntPtr GetForegroundWindowRaw();

    /// <summary>
    /// 把某一扇端到前台。<b>和 <c>SetForegroundWindow</c> 分开是有原因的</b>：
    /// 后者受「前台锁」限制，一个刚起来的进程常常调不动；
    /// 产品自己那一侧用的是这一对（<c>src/DesktopLayer.cs</c> 的 <c>GrabForTyping</c>），
    /// 量具用同一对，判据才不会两边不一样。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void SwitchToThisWindow(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    /// <summary>桌面那一扇（<c>Progman</c>）。拿它当「把前台挪走」那一档的目标。</summary>
    internal static IntPtr DesktopWindow()
    {
        foreach (WindowInfo w in TopLevelWindows())
        {
            if (w.ClassName == "Progman") return w.Handle;
        }

        return IntPtr.Zero;
    }

    /// <summary>端到前台。<b>返回的是「试过了」不是「成了」</b> —— 前台锁会挡，
    /// 而调用那一发会挡这一下。成没成由调用方<b>自己再读一遍前台</b>判，
    /// 不由这一句来报（量于 10-12：对着 <c>Progman</c> 调了这一发，前台一格没动）。</summary>
    internal static void BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        SwitchToThisWindow(handle, true);
    }

    /// <summary>
    /// 「别的程序那一扇」：屏上一扇看得见的、<b>不是产品</b>的普通应用窗。
    /// 拿来当「把前台挪到产品以外」那一档的目标。
    /// </summary>
    internal static IntPtr ForeignAppWindow(int productPid)
    {
        WindowInfo? pick = TopLevelWindows()
            .Where(w => w.Visible && w.ProcessId != productPid &&
                        w.ClassName is "Chrome_WidgetWin_1" or "ApplicationFrameWindow" or "CabinetWClass")
            .OrderByDescending(w => w.Rect.Width * w.Rect.Height)
            .FirstOrDefault();
        return pick?.Handle ?? IntPtr.Zero;
    }

    internal static WindowInfo Describe(IntPtr handle) => new(
        handle,
        ClassOf(handle),
        TitleOf(handle),
        ProcessIdOf(handle),
        IsWindowVisible(handle),
        RectOf(handle));
}