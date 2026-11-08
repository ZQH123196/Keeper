using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DebugProbe;

/// <summary>
/// 工单 09 第二发：遮罩起来之后，罩住的那一块和罩不住的那一块，<b>两半分别报</b>。
///
/// <para><b>「底下那个程序收不收得到」要一个真程序来答</b>，不是一个命中测试。
/// 所以这一发当场造一扇自己注册的接收窗（<see cref="ReceiverWindow"/>），
/// 它的窗口过程数 <c>WM_LBUTTONDOWN</c> —— 点下去数到了，就是底下那个程序真收到了，
/// 不用猜，也不用拿「钩子看见系统里过了这一发」当证据（那一发只证明按键发出去了，
/// 不证明谁收到了）。</para>
///
/// <para><b>两半分开报，不合成一句</b>：罩住的那一块点下去该归遮罩，
/// 罩不住的那一块点下去该归底下那个程序。两句是两句，
/// 合成一句会把「遮罩收了鼠标」和「遮罩之外一切照旧」说成一件事。</para>
///
/// <para><b>没有罩不住的那一块时要照实说没有</b>（量于 10-13）：这台机器只有一块屏，
/// 而遮罩那扇是 (0,0)-(2560,1440) —— 整块屏都罩住了。
/// 那一半缺前提就报缺前提，不拿一个罩住的点冒充罩不住的点。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/09-product-own-actions.md</c>。</para>
/// </summary>
internal static class Cover
{
    internal static int Run(string[] args)
    {
        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "遮罩起来之后：罩住的那一块和罩不住的那一块，两半分别报",
            "「收没收鼠标」和「清单那一片」走 05 那道口问产品（那两条只有产品答得出）；" +
            "两半的归属用命中测试判，谁真收到了用当场造的接收窗的窗口过程数 —— " +
            "钩子只证明按键发出去了，不证明谁收到了");

        using Port? port = Port.Open();
        if (port == null)
        {
            shot.Finish(
                $"中止 —— 接不上产品那道口，「收没收鼠标」和「清单那一片」只有产品答得出。" +
                $"试过的管路：{Port.Attempts}。{Port.LastFailure}",
                "无");
            return Find.Refused;
        }

        // **等遮罩自己起来，不靠猜几秒**（量于 10-13）：`--streak 5` 让它静 5 秒就抬，
        // 而从起产品到它抬起来这一段在 10 秒到 25 秒之间飘（两跑分别读到 10.5 秒和 16 秒那一行）。
        // 猜秒数的话，要么跑得太早答「没收鼠标」而中止，要么太晚它已经落了。
        int wait = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--wait" && int.TryParse(args[i + 1], out int seconds)) wait = seconds;
        }

        string swallow = port.Ask("swallow");
        for (int waited = 0; wait > 0 && waited < wait * 4 &&
                            !swallow.Contains("swallow=1", StringComparison.Ordinal); waited++)
        {
            System.Threading.Thread.Sleep(250);
            if (port.Disconnect) break;
            swallow = port.Ask("swallow");
        }

        string checklist = port.Ask("checklist");
        if (port.Disconnect)
        {
            shot.Finish("中止 —— 问「swallow」或「checklist」之后口断了，这一发答不了", "无");
            return Find.Refused;
        }

        if (!swallow.Contains("swallow=1", StringComparison.Ordinal))
        {
            shot.Finish(
                $"中止 —— 产品答「{swallow}」，遮罩没收鼠标，这一发的前提没有。" +
                "「遮罩起来」这件事先要成立（起产品带 `--streak 5`，静 5 秒它自己会抬起来）",
                "无");
            return Find.Refused;
        }

        RectI screen = Windows.PrimaryMonitor().Monitor;

        // 遮罩那一扇自己有多大 —— 「罩不住的那一块在不在」这一问全看它。
        WindowInfo? overlay = Windows.TopLevelWindows()
            .Where(w => ProductProcess.RunningProductIds().Contains(w.ProcessId) && w.Visible)
            .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height)
            .FirstOrDefault();
        string overlayText = overlay == null
            ? "**屏上找不到产品的任何一扇可见顶层窗**"
            : $"产品最大的一扇可见顶层窗 0x{overlay.Handle.ToInt64():X}/{overlay.ClassName}" +
              $"（pid {overlay.ProcessId}）矩形={overlay.Rect}，占整块屏的 " +
              $"{(overlay.Rect.Width * (long)overlay.Rect.Height * 100 / Math.Max(1, screen.Width * (long)screen.Height))}%";

        // 先铺一层 18x10 的归谁分布，再决定两半点哪儿。
        // **不先看分布就挑点，挑出来的点可能压根不在遮罩那一层上** ——
        // 而「点下去谁收到」这个问题，前提是那个点归谁已经被量过了。
        List<Covering> grid = Grid(screen, 18, 10);
        string spread = string.Join("；", grid.GroupBy(c => $"{c.Owner}（pid {c.Pid}）")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()} 格"));

        List<Covering> onOverlay = grid.Where(c => c.OnOverlay).ToList();
        List<Covering> notOnOverlay = grid.Where(c => !c.OnOverlay).ToList();

        using var covered = new ReceiverWindow(new RectI(0, 0, 0, 0), "罩住的那一块");
        using var free = new ReceiverWindow(new RectI(0, 0, 0, 0), "罩不住的那一块");

        Covering? inside = onOverlay.Count > 0 ? onOverlay[onOverlay.Count / 2] : null;
        Covering? outside = notOnOverlay.Count > 0 ? notOnOverlay[notOnOverlay.Count / 2] : null;

        if (inside != null) covered.MoveTo(SpotAround(inside.Value));
        if (outside != null) free.MoveTo(SpotAround(outside.Value));
        bool coveredUp = inside != null && covered.Show();
        bool freeUp = outside != null && free.Show();

        // 装一只低级鼠标钩子：它数的是「这一发真的发出去了」。
        // **它不证明谁收到了** —— 收到那一句靠接收窗的窗口过程。
        using var hook = new Input.MouseHook();
        if (!hook.Installed)
        {
            shot.Finish($"中止 —— 低级鼠标钩子装不上，错误码 {hook.InstallError}，这一发答不了", "无");
            return Find.Refused;
        }

        // 图标位置：点下去之前读一遍当底。遮罩占满单屏，而图标在它底下 ——
        // 这一问顺带答「遮罩起来之后图标还在不在原位」，是 SPEC 第四节要的。
        // 读得到读不到都照实报，读不到时 ComparePlaces 那一句自己会说这一问不作数。
        List<Icons.IconBox> placesBefore = Icons.ReadPlaces("点下去之前");

        (int X, int Y) coveredCentre = covered.Center;
        int coveredGot = -1;
        string oneText;
        if (inside == null || !coveredUp)
        {
            oneText =
                "第一半不作数 —— 采样里**没有一格归遮罩那一扇**，点下去收不到的那一下就分不清" +
                "是遮罩挡的还是别的窗压在它上面。缺前提（不放行一个答不了的读数）";
        }
        else
        {
            Input.ClickAt(coveredCentre.X, coveredCentre.Y, 200);
            hook.PumpFor(600);
            coveredGot = covered.DownCount;
            covered.Clear();
            oneText =
                $"第一半：罩住的那一块 ({coveredCentre.X},{coveredCentre.Y}) —— 点之前命中测试答 " +
                $"{inside.Value.Owner}（pid {inside.Value.Pid}，产品进程），" +
                $"底下那扇接收窗摆在 {covered.Rect} 中心，压在遮罩之下；点下去它收到 {coveredGot} 次按下 —— " +
                $"{(coveredGot == 0 ? "**收不到，遮罩把这一下吃了**" : "**照样收到了**")}";
        }

        int outsideGot = -1;
        (int X, int Y)? outsideCentre = null;
        string twoText;
        if (outside == null || !freeUp)
        {
            twoText = "第二半不作数 —— 这块屏上没有一格是不归遮罩的（缺前提）";
        }
        else if (overlay != null && overlay.Rect.Width >= screen.Width && overlay.Rect.Height >= screen.Height)
        {
            // 遮罩盖满整块屏的时候，「罩不住的那一块」不存在。
            // **拿一个罩住的点去冒充它，得到的读数是另一件事** —— 这里照实说缺前提。
            freeUp = false;
            twoText =
                $"第二半**不作数（缺前提）**：遮罩那一扇 {overlay.Rect} 自己就盖满整块屏，" +
                "这块屏上没有「罩不住的那一块」可点。判据那一问在这台机器上答不了，" +
                "不拿一个罩住的点冒充";
        }
        else
        {
            outsideCentre = free.Center;
            Input.ClickAt(outsideCentre.Value.X, outsideCentre.Value.Y, 200);
            hook.PumpFor(600);
            outsideGot = free.DownCount;
            free.Clear();
            // 点完再量一次那一格归谁。**这一问不可省**：接收窗没收到时，
            // 「是遮罩挡的」和「z 序把它压在别人下面」是两件完全不同的事，
            // 而只报「没收到」会让人去查错的那一件。
            IntPtr at = Windows.At(outsideCentre.Value.X, outsideCentre.Value.Y);
            string after = at == free.Handle
                ? "点完那一格命中测试答的正是接收窗自己"
                : $"点完那一格命中测试答 {Windows.ClassOf(at)}（pid {Windows.ProcessIdOf(at)}），不是接收窗";

            twoText =
                $"第二半：罩不住的那一块 ({outsideCentre.Value.X},{outsideCentre.Value.Y}) —— 点之前命中测试答 " +
                $"{outside.Value.Owner}（pid {outside.Value.Pid}，不是产品进程）；" +
                $"底下那扇接收窗摆在 {free.Rect} 中心，点下去它收到 {outsideGot} 次按下；{after} —— " +
                $"{(outsideGot > 0
                    ? "**底下那个程序收得到**"
                    : "**这一格收不到，而且不是遮罩挡的**（那一格归的是别的窗，量的是 z 序不是遮罩）")}";
        }

        int presses = hook.Presses;
        hook.PumpFor(200);

        // 点完之后再读一遍图标位置，两下点完算一个动作段。
        List<Icons.IconBox> placesAfter = Icons.ReadPlaces("两下点完之后");
        string places = Icons.ComparePlaces(placesBefore, placesAfter);

        // 留档：两扇接收窗的位置 + 遮罩那一块的归谁，一行说清。
        Frame frame = Screen.Grab(screen);
        frame.OwnerText =
            $"遮罩在：{swallow}；清单：{checklist}；" +
            $"罩住的那一块点下去：接收窗收到 {coveredGot} 次按下，" +
            $"钩子这一段数到 {presses} 次按下；" +
            (outsideCentre == null
                ? "罩不住的那一块：这块屏上没有"
                : $"罩不住的那一块点下去：接收窗收到 {outsideGot} 次按下");
        string path = Path.Combine(outDir, "cover-two-halves.png");
        frame.SavePng(path);

        // 判的是两半**各自**答没答上来。哪一半缺前提就不算通过 ——
        // 「一半没量到」和「一半通过了」在读数上要分得开。
        bool halfOne = coveredGot == 0;
        bool halfTwo = outsideGot > 0;
        bool answered = coveredGot >= 0 && outsideGot >= 0;

        shot.Finish(
            $"产品答「{swallow}」、清单「{checklist}」（这两条走 05 那道口，只有产品答得出）。" +
            $"屏 {screen} 上采样 {grid.Count} 格，归谁分布是 {spread}；" +
            $"其中归遮罩那一扇的 {onOverlay.Count} 格、不归的 {notOnOverlay.Count} 格。" +
            $"{overlayText} —— 这台机器只有一块屏，" +
            (overlay != null && overlay.Rect.Width >= screen.Width && overlay.Rect.Height >= screen.Height
                ? "**遮罩那一扇自己就盖满整块屏，所以「罩不住的那一块」不存在**，第二半缺前提。"
                : "") +
            $"{oneText}。{twoText}。钩子这一段数到 {presses} 次按下 —— " +
            "它只证明按键发出去了，不证明谁收到了；收到那一句是接收窗的窗口过程数出来的。" +
            $"图标位置：{places}。两半的档合在一张里，带一行归谁",
            path);

        return answered && halfOne && halfTwo ? 0 : Find.Refused;
    }


    /// <summary>以那一点为中心摆一扇 220x220 的接收窗，并<b>裁进屏内</b>。
    /// 裁不裁是这一发的一个坑：不裁的话靠边那一格会造出一扇跑到屏外去的窗，
    /// 那扇窗收不到任何点击，而读数会说「底下那个程序收不到」—— 那是量具自己造的错。</summary>
    private static RectI SpotAround(Covering at)
    {
        const int size = 220;
        RectI screen = Windows.PrimaryMonitor().Monitor;
        int left = Math.Clamp(at.X - size / 2, screen.Left, Math.Max(screen.Left, screen.Right - size));
        int top = Math.Clamp(at.Y - size / 2, screen.Top, Math.Max(screen.Top, screen.Bottom - size));
        return new RectI(left, top, size, size);
    }
    private readonly record struct Covering(int X, int Y, string Owner, int Pid, bool OnOverlay);

    /// <summary>按格子扫一遍屏，报每一格命中测试答的是谁。
    /// 「归不归遮罩」判的是<b>进程 + 类名</b>，不按标题 —— 标题会变。</summary>
    private static List<Covering> Grid(RectI screen, int columns, int rows)
    {
        var found = new List<Covering>();
        List<int> pids = ProductProcess.RunningProductIds();

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int x = screen.Left + (int)((column + 0.5) * screen.Width / columns);
                int y = screen.Top + (int)((row + 0.5) * screen.Height / rows);
                IntPtr at = Windows.At(x, y);
                string owner = Windows.ClassOf(at);
                int pid = Windows.ProcessIdOf(at);
                found.Add(new Covering(x, y, owner, pid, pids.Contains(pid)));
            }
        }

        return found;
    }
}

/// <summary>
/// 量测程序自己造的一扇<b>接收窗</b>：它的窗口过程数 <c>WM_LBUTTONDOWN</c>。
///
/// <para><b>为什么不用钩子当凭据</b>：钩子挂在全机输入上，点一下它就响 ——
/// 它答的是「这一发发出去了」，不是「底下那个程序收到了」。
/// 收没收到只有那一扇窗自己的窗口过程知道。</para>
///
/// <para><b>它摆成普通顶层窗、不置顶</b>：置顶的话它会压过遮罩，
/// 那一发量到的是 z 序而不是遮罩。</para>
/// </summary>
internal sealed class ReceiverWindow : IDisposable
{
    internal const string ClassName = "DebugProbeRecv";
    private const string WindowTitle = "量具自己造的接收窗";

    private const uint WsPopup = 0x8000_0000;
    private const uint WsVisible = 0x1000_0000;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;

    /// <summary>按下的次数。<b>窗口过程在另一个线程的消息循环里写、这边读</b>，
    /// 所以先读一次再答，不加锁 —— 那一发的两半都是同一个进程、同一段消息泵，
    /// 顺序由 <c>PumpFor</c> 定住。</summary>
    private static int _down;
    private static int _up;

    private readonly WindowProc _proc;
    private RectI _rect;
    private readonly string _label;
    private IntPtr _hwnd;
    private ushort _atom;

    internal ReceiverWindow(RectI rect, string label)
    {
        _rect = rect;
        _label = label;
        _proc = OnWindowProc;   // 委托必须留着一个字段
    }

    internal RectI Rect => _rect;

    /// <summary>这一扇的句柄。「点完那一格命中测试答的是不是我」那一问要它。</summary>
    internal IntPtr Handle => _hwnd;

    internal (int X, int Y) Center => (_rect.Left + _rect.Width / 2, _rect.Top + _rect.Height / 2);

    internal int DownCount => _down;
    internal int UpCount => _up;

    internal void Clear() => _down = _up = 0;

    /// <summary>换到别的位置。<b>先挪后造</b> —— 造起来再挪的话那一帧会闪。</summary>
    internal void MoveTo(RectI rect) => _rect = rect;

    internal bool Show()
    {
        var info = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = ClassName,
            // 看得见的底色：留档那一帧要分得出「这一扇在那儿」和「那一扇不在」。
            Background = GetStockObject(6 /* LTGRAY_BRUSH */),
        };

        _atom = RegisterClassEx(ref info);
        if (_atom == 0) return false;

        _hwnd = CreateWindowEx(
            0, ClassName, $"{WindowTitle}（{_label}）", WsPopup | WsVisible,
            _rect.Left, _rect.Top, _rect.Width, _rect.Height,
            IntPtr.Zero, IntPtr.Zero, info.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;

        SetWindowPos(_hwnd, IntPtr.Zero, _rect.Left, _rect.Top, _rect.Width, _rect.Height, 0x0040);
        CoverWindow.Pump(400);
        return Windows.IsWindowVisible(_hwnd);
    }

    private static IntPtr OnWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 数按下和松开。**两件都数**：只数按下的话，一次按住不放到底也算收到了。
        if (message == WmLButtonDown) _down++;
        if (message == WmLButtonUp) _up++;
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            CoverWindow.Pump(200);
        }

        if (_atom != 0)
        {
            UnregisterClass(ClassName, GetModuleHandle(null));
            _atom = 0;
        }

        GC.KeepAlive(_proc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProc;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal string? MenuName;
        internal string ClassName;
        internal IntPtr IconSmall;
    }

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}