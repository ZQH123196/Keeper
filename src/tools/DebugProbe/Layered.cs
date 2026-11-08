using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DebugProbe;

/// <summary>
/// 工单 28 第 0 步：<b>层叠子窗打进图标层画不画</b>。
///
/// <para><b>这一发量的是哪一条</b>：2026-10-15 量到「壳会画图标列表之下的 GDI 子窗」
/// （壳自己的 <c>SysHeader32</c>，8560 / 8560 个采样像素全变）。那是<b>窗类背景刷</b>
/// 那条路，也就是 GDI。<b>层叠子窗走的是 DWM 那一路，是另一条路</b>，本机还没量过。
/// 这一发只管后一条。后面四步全建在这一条上，量不到就停手。</para>
///
/// <para><b>量法</b>：把壳自己那个隐藏的 <c>SysHeader32</c> 摆到图标列表之下，
/// 给它 <c>WS_EX_LAYERED</c>，用 <c>UpdateLayeredWindow</c> 打一张<b>纯色 DIB</b> 进去，
/// 抓帧比那一块的每一个采样像素。</para>
///
/// <para><b>为什么借壳自己的窗，不在自己进程里建窗挂过去</b>：
/// 2026-10-15 那一版探针在<b>自己进程里 <c>CreateWindowExW</c> 建子窗</b>、
/// 再 <c>SetParent</c> 到别的进程的 <c>DefView</c> 上，把 pwsh 整个打崩（<c>0xC0000409</c>），
/// 之后 explorer 换了一个进程。<b>那条路不再走</b>。借壳自己的窗既安全，又答同一个问题。
/// 用完原样还原（样式位、矩形、可见性逐项读回来对）。</para>
///
/// <para><b>四发都要有，缺一发这一发就不算数</b>：
/// <list type="number">
/// <item><b>对照</b>：同样的 <c>UpdateLayeredWindow</c> 打在本进程自己的一扇层叠顶层窗上 ——
/// 必须画出来。这一发把「图标层画不画」和「我们那一段 <c>UpdateLayeredWindow</c> 用错了」分开。</item>
/// <item><b>反向验</b>：那一块什么都不动，连抓两帧 —— 必须 <b>0 个像素变</b>。
/// 壁纸在轮播、光标在闪，那两种都靠这一发排掉。</item>
/// <item><b>正档</b>：层叠子窗打进图标层 —— 判据是<b>每一个采样像素都变</b>，不看均值。</item>
/// <item><b>还原</b>：拆掉之后抓的那一帧和发之前那一帧逐像素相同。</item>
/// </list></para>
///
/// <para><b>光标要先挪开</b>：抓帧连光标那一块一起抓，而光标会动 ——
/// 它不动的话「每一个采样像素都变」这一条永远差着几颗。挪开、量完<b>挪回原处</b>，
/// 两头的坐标都报出来。</para>
///
/// <para>出处：<c>.scratch/keeper-debt/issues/28-calendar-window-under-the-icons.md</c>
/// 第 0 步。取向在 <c>docs/adr/0007-the-calendar-window-is-under-the-icons.md</c>。</para>
/// </summary>
internal static class Layered
{
    /// <summary>那一块。左边那一带 —— 日历窗（顶层）盖着右边，不盖这里。</summary>
    private static readonly RectI DefaultSpot = new(200, 600, 840, 720);

    /// <summary>抽稀采样的步长。<b>判的是每一个采样像素，不是均值</b> ——
    /// 均值会被壁纸拖平（全屏一张图，均值差零点几）。</summary>
    private const int Stride = 2;

    // ---- 这一族要摸的 Win32（测量程序自己声明，不和产品那份共用） ----------------

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <summary><c>HWND_TOPMOST</c> = −1。对照那一扇要顶到最上面，
    /// 才不会被图标层压住 —— 那一发的判据是「我们这段代码用对没有」，不是「谁盖住谁」。</summary>
    private static readonly IntPtr HwndTopMost = new(-1);

    /// <summary>桌面拆开那条消息。<b>不发就找不到画布</b>（工单 28 第 1 步）。</summary>
    private const uint SplitDesktop = 0x052C;

    /// <summary>抽帧那一族用得到的窗类背景刷档位。</summary>
    private const uint UlwAlpha = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendMessageTimeout(
        IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window, IntPtr destDc, ref NativePoint dest, ref NativeSize size,
        IntPtr srcDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WindowClassEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle, string className, string? title, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        internal int Width;
        internal int Height;
    }

    /// <summary><c>BLENDFUNCTION</c>。四个字节，全 0 就退化成硬边 —— 这一发要纯色，硬边就够。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte Operation;
        internal byte Flags;
        internal byte ConstantAlpha;
        internal byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
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
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal IntPtr IconSmall;
    }

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>那一块上打了什么颜色。<b>纯色、不透明</b> ——
    /// 半透明的话「变了」和「变了一半」分不开。</summary>
    private static readonly (byte B, byte G, byte R) Fill = (0x00, 0xE0, 0xE0);

    // ---- 那一发 -------------------------------------------------------------

    internal static int Run(string[] args)
    {
        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            "层叠子窗打进图标层画不画（工单 28 第 0 步）",
            "借壳自己的 SysHeader32：排进图标列表之下 + WS_EX_LAYERED + UpdateLayeredWindow 打一张纯色 DIB，" +
            "抓帧比那一块的每一个采样像素（不看均值）。四发都要有：对照（本进程同一段 UpdateLayeredWindow " +
            "必须画得出来）、反向验（什么都不动、连抓两帧必须 0 个像素变）、正档（每一个采样像素都变）、" +
            "还原（拆掉之后那一帧和发之前逐像素相同）");

        Console.Out.WriteLine($"完整性级别：{Windows.IntegrityLevel()}");
        Console.Out.WriteLine();

        // ---- 第 0 发：桌面拆开。不发就没有画布（工单 28 第 1 步）。 ------------
        IntPtr progman = Windows.DesktopWindow();
        if (progman == IntPtr.Zero)
        {
            shot.Finish("中止 —— 读不到 Progman（桌面那一扇），这一发答不了", "无");
            return Find.Refused;
        }

        bool split = SendMessageTimeout(
            progman, SplitDesktop, IntPtr.Zero, IntPtr.Zero,
            0x0002 /* SMTO_ABORTIFHUNG */, 3000, out _);
        Console.Out.WriteLine($"向 Progman 0x{progman.ToInt64():X} 发 0x052C（拆开桌面）：{SplitText(split)}");

        // ---- 找宿主：按「类名 + 可见 + 满屏」挑，不按「哪个 WorkerW 没 DefView」挑。 ----
        Host host = Host.Pick(progman);
        if (host.DefView == IntPtr.Zero)
        {
            shot.Finish(
                $"中止 —— Progman 的后代里没有一扇「类名 SHELLDLL_DefView + 可见 + 满屏」的窗。" +
                $"候选：{host.Candidates}。这一发答不了",
                "无");
            return Find.Refused;
        }

        Console.Out.WriteLine(host.Describe());

        WindowInfo? header = host.Child(host.DefView, "SysHeader32");
        WindowInfo? iconList = host.Child(host.DefView, "SysListView32");
        if (header is null || iconList is null)
        {
            shot.Finish(
                $"中止 —— 图标层那一扇底下没有要找的子窗：SysHeader32={(header is null ? "没有" : "有")}、" +
                $"SysListView32={(iconList is null ? "没有" : "有")}。这一发答不了",
                "无");
            return Find.Refused;
        }

        // ---- 挑那一块：图标之上、app 窗之下都没有，才配拿来量。 --------------------
        RectI spot = PickSpot(out string spotNote);
        Console.Out.WriteLine($"量那一块：{spot} —— {spotNote}");
        Console.Out.WriteLine($"采样步长 {Stride}；那一块上要采 {SampleCount(spot)} 颗。");
        Console.Out.WriteLine();

        // 光标先挪开：抓帧连它一起抓，它在动的话正档那条永远差着几颗。
        GetCursorPos(out NativePoint cursor);
        bool moved = false;
        if (spot.Contains(cursor.X, cursor.Y))
        {
            moved = SetCursorPos(4, 4);
            Console.Out.WriteLine($"光标原来在 ({cursor.X},{cursor.Y})、落在那一块里 —— 挪到 (4,4)，成功={moved}。");
        }
        else
        {
            Console.Out.WriteLine($"光标在 ({cursor.X},{cursor.Y})、不落在那一块里，不挪。");
        }

        try
        {
            return Measure(shot, host, header, iconList, spot, outDir);
        }
        finally
        {
            if (moved)
            {
                SetCursorPos(cursor.X, cursor.Y);
                Console.Out.WriteLine($"光标挪回 ({cursor.X},{cursor.Y})。");
            }
        }
    }

    private static int Measure(
        Shot shot, Host host, WindowInfo header, WindowInfo iconList, RectI spot, string outDir)
    {
        // ---- 拆之前那一扇是什么样子。还原要对的就是这一份。 --------------------
        Shape before = Shape.Of(header.Handle);
        Console.Out.WriteLine($"发之前 SysHeader32：{before.Describe()}；图标列表 {iconList.Handle.ToInt64():X}。");
        Console.Out.WriteLine();

        try
        {
            // ---- 第 1 发（基准）：什么都不动，抓一帧。 ---------------------------
            Frame baseFrame = Screen.Grab(spot);

            // ---- 反向验：连抓两帧，必须 0 个像素变。 ---------------------------
            Frame againBase = Screen.Grab(spot);
            (int quietChanged, int samples) = Diff(baseFrame, againBase, spot);
            Console.Out.WriteLine($"反向验（什么都不动，连抓两帧）：{quietChanged} / {samples} 个采样像素变了 —— " +
                                  $"{(quietChanged == 0 ? "0 个，符合" : "**不是 0，这一发要重来**")}");

            // ---- 对照：同一段 UpdateLayeredWindow，打在本进程自己的一扇层叠顶层窗上。 ----
            ControlResult control = ControlArm(spot, outDir);
            Console.Out.WriteLine($"对照（本进程那扇层叠顶层窗，同一段 UpdateLayeredWindow）：{control.Text}");
            Console.Out.WriteLine();

            // 正档的基准要重新抓一次：对照那一发在本进程开过一扇窗。
            Frame beforeArm = Screen.Grab(spot);

            // ---- 正档：把它排进图标列表之下 + WS_EX_LAYERED + UpdateLayeredWindow。 ----
            ArmResult arm = Arm(header, iconList, spot);
            Frame afterFrame = Screen.Grab(spot);
            (int changed, int total) = Diff(beforeArm, afterFrame, spot);
            (int mine, int colourTotal) = CountColour(afterFrame, spot);
            Console.Out.WriteLine($"正档读数：{arm.Describe()}");
            Console.Out.WriteLine($"正档抓帧：{changed} / {total} 个采样像素变了 —— " +
                                  $"{(changed == total && total > 0 ? "**每一个都变**" : "**没有全变**")}；" +
                                  $"其中是本进程那一种颜色的 {mine} / {colourTotal} 颗");

            string afterPath = Path.Combine(outDir, "layered-after.png");
            afterFrame.OwnerText =
                $"发之前 {quietChanged} / {samples} 变；正档 {changed} / {total} 变；" +
                $"本进程那一种颜色 {mine} / {colourTotal} 颗；{arm.Describe()}";
            afterFrame.SavePng(afterPath);

            // ---- 拆掉、还原，再抓一帧。 ----------------------------------------
            Restore(header, before);
            Frame restored = Screen.Grab(spot);
            (int backChanged, _) = Diff(beforeArm, restored, spot);
            Shape after = Shape.Of(header.Handle);
            bool shapeBack = after == before;
            Console.Out.WriteLine($"拆掉之后 SysHeader32：{after.Describe()}；" +
                                  $"{(shapeBack ? "和发之前逐字相同" : "**和发之前不一样**")}");
            Console.Out.WriteLine($"拆掉之后抓的那一帧：{backChanged} / {samples} 个采样像素和发之前不同 —— " +
                                  $"{(backChanged == 0 ? "0 个，还原干净" : "**不是 0，有东西没拆干净**")}");

            string restoredPath = Path.Combine(outDir, "layered-restored.png");
            restored.OwnerText = $"拆掉之后那一帧；{backChanged} / {samples} 与发之前不同；{after.Describe()}";
            restored.SavePng(restoredPath);

            bool controlOk = control.Passed;
            // 判据不止「变了」：还要**是本进程打进去的那一种颜色**。
            // 只判「变了」的话，块上别的动画也能把它顶到全变，而那推出的是另一件事。
            bool armOk = changed == total && total > 0 && mine == colourTotal && colourTotal > 0;
            bool quietOk = quietChanged == 0;
            bool restoreOk = shapeBack && backChanged == 0;
            bool answered = arm.Updated;

            string verdict = !answered
                ? "**这一发量不到** —— UpdateLayeredWindow 答假（" + arm.Describe() + "），" +
                  "像素这一问没发生，本机「层叠子窗在图标层画不画」答不出来。按工单 28 第 0 步：**停手，不往上盖东西**。"
                    : !controlOk
                        ? "**这一发不作数** —— 对照那一发没过（" + control.Text + "）。" +
                          "对照要答的是「我们这一段 UpdateLayeredWindow 用对没有」，它没过的话，" +
                          "正档那一发的读数分不清是壳不画还是量具自己坏着。"
                        : armOk
                            ? $"**量到了：层叠子窗打进图标层画得出来。**{total} 个采样像素全变，" +
                              $"其中本进程打进去的那一种颜色 {mine} / {colourTotal} 颗。后面四步可以往上走。"
                            : $"**量不到：层叠子窗打进图标层画不出来。**{changed} / {total} 个采样像素变了、" +
                              $"本进程那一种颜色 {mine} / {colourTotal} 颗。按工单 28 第 0 步：**停手，不往上盖东西**。";

            shot.Finish(
                $"宿主：{host.Describe()}；量那一块 {spot}，采样 {samples} 颗（步长 {Stride}）。" +
                $"对照：{control.Text}。反向验：{quietChanged} / {samples} 变。" +
                $"正档：{changed} / {total} 变、本进程那一种颜色 {mine} / {colourTotal} 颗。" +
                $"还原：样式三项{(shapeBack ? "一样" : "不一样")}、像素 {backChanged} / {samples} 变。" +
                $"四发里对照 {(controlOk ? "过" : "不过")}、反向验 {(quietOk ? "过" : "不过")}、" +
                $"正档 {(armOk ? "过" : "不过")}、还原 {(restoreOk ? "过" : "不过")}。{verdict}",
                afterPath);

            return answered && controlOk && quietOk && armOk && restoreOk ? 0 : Find.Refused;
        }
        finally
        {
            // 崩在这一发中间也要还原 —— 借的是壳自己的窗。
            Restore(header, before);
        }
    }

    // ---- 四发 ---------------------------------------------------------------

    /// <summary>对照：同一段 <c>UpdateLayeredWindow</c> 打在本进程自己的一扇层叠顶层窗上。
    /// 这一发不通的话，正档那一发答「画不出来」就分不清是壳不画还是我们用错了。
    ///
    /// <para><b>判据用 <see cref="ControlResult.Passed"/>，不用「读数那一行里有没有某句话」</b> ——
    /// 拿自己印出来的那句话当判据，量的是当初那份副本，而措辞一改判据就跟着绿掉
    /// （本仓库规矩：判据里不许存一份会腐烂的副本）。</para></summary>
    private static ControlResult ControlArm(RectI spot, string outDir)
    {
        IntPtr instance = GetModuleHandle(null);
        var info = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(Keep),
            Instance = instance,
            ClassName = ControlClass,
        };

        ushort atom = RegisterClassEx(ref info);
        if (atom == 0)
        {
            return new(false, false,
                $"**这一发自己就答不了** —— RegisterClassEx 答 0，错误码 {Marshal.GetLastWin32Error()}", 0, 0);
        }

        IntPtr window = IntPtr.Zero;
        try
        {
            window = CreateWindowEx(
                (int)0x00080000 /* WS_EX_LAYERED */ | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */,
                ControlClass, "测量程序自己那扇层叠窗",
                0x80000000 /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */,
                spot.Left, spot.Top, spot.Width, spot.Height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                return new(false, false,
                    $"**这一发自己就答不了** —— CreateWindowEx 答 0，错误码 {Marshal.GetLastWin32Error()}", 0, 0);
            }

            // **必须顶到最上面**。2026-10-15 这一发两趟读出 19200 和 0 两个数，
            // 读数才指出来：那一扇可见=1、矩形也对，可那一格的命中测试答的是
            // <c>SysListView32</c> —— **图标层压在它上面**，它的像素压根露不出来。
            // 不顶到最上面的话，这一发量的是「图标层盖没盖住我」，不是
            // 「我们这一段 UpdateLayeredWindow 用对没有」。
            SetWindowPos(window, HwndTopMost, spot.Left, spot.Top, spot.Width, spot.Height,
                SwpNoActivate | SwpFrameChanged);

            // **先抽一会儿消息**。造出来只是登记，不抽它不会真的落到屏幕上 ——
            // 2026-10-15 第一版忘了这一句，对照那一发读到 0 / 19200，
            // 而正档读到 19200 / 19200：那个「对照不过」量的是本进程的消息泵，
            // 不是机器。（`Refuse.CoverWindow.Pump` 那一处有同样的说明，2026-09 量的。）
            Pump(400);

            // **对照那一扇自己要看一眼**：可见不看得见、矩形在哪、那一格的命中测试答谁。
            // 读数不对的话这一发答的不是它要答的那件事。
            string selfState =
                $"对照那一扇自己：可见={(Windows.IsWindowVisible(window) ? "1" : "0")} 矩形={Windows.RectOf(window)} " +
                $"中心命中={Windows.ClassOf(Windows.At(spot.Left + spot.Width / 2, spot.Top + spot.Height / 2))}";
            Console.Out.WriteLine(selfState);

            // **两条 destDc 各量一遍**，看是哪一条画得出来。
            // 这一发要答的是「我们这一段 UpdateLayeredWindow 用对没有」，
            // 而「用对没有」本身就包括 destDc 该怎么传 —— 照抄文档的那一条未必是这一台。
            (bool zeroOk, int zeroChanged, int total0) = TryPush(window, spot, IntPtr.Zero, "传 0");
            (bool screenOk, int screenChanged, _) = TryPush(window, spot, GetDC(IntPtr.Zero), "传屏的 DC");
            Console.Out.WriteLine(
                $"对照那一扇推完之后：可见={(Windows.IsWindowVisible(window) ? "1" : "0")} 矩形={Windows.RectOf(window)}");

            string path = Path.Combine(outDir, "layered-control.png");
            Frame after = Screen.Grab(spot);
            (int mine, _) = CountColour(after, spot);
            after.OwnerText =
                $"对照那一发：destDc 传 0 → {zeroChanged} / {total0} 变（答{(zeroOk ? "真" : "假")}）；" +
                $"destDc 传屏的 DC → {screenChanged} 变（答{(screenOk ? "真" : "假")}）；本进程那一种颜色 {mine} 颗";
            after.SavePng(path);

            int best = Math.Max(zeroChanged, screenChanged);
            if (best == 0)
            {
                return new(false, zeroOk && screenOk,
                    $"两条 destDc 都读 0 个采样像素变（传 0：{zeroChanged}，传屏的 DC：{screenChanged}）—— 对照不过",
                    zeroChanged, screenChanged);
            }

            // 「对照过」= 至少有一条把**每一个**采样像素都变了，且那些像素就是打进去的那一种颜色。
            bool zeroFull = zeroOk && zeroChanged == total0 && total0 > 0;
            bool screenFull = screenOk && screenChanged > 0 && screenChanged == total0;
            string text = (zeroFull ? "**destDc 传 0 那一档画得出来**" : "**只有传屏的 DC 那一档画得出来**") +
                          $"；{selfState}；本进程那一种颜色 {mine} 颗 —— 对照过（{Path.GetFileName(path)}）";

            return new(zeroFull || screenFull, zeroOk && screenOk, text, zeroChanged, screenChanged);
        }
        finally
        {
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClass(ControlClass, instance);
            GC.KeepAlive(Keep);
        }
    }

    /// <summary>对照那一发的实测结果。<b><see cref="Passed"/> 是判据，
    /// <see cref="Text"/> 只是给人看的那一句</b> —— 判据不从那句话里认。</summary>
    private readonly record struct ControlResult(bool Passed, bool BothReturnedTrue, string Text, int ZeroChanged, int ScreenChanged);

    private const string ControlClass = "DebugProbeLayered";

    /// <summary>推一次、抓一帧、数有几颗变了。<b>抓帧那一瞬之前要抽消息</b> ——
    /// 不抽，DWM 那一层还没拿到新像素，抓回来的是旧帧。</summary>
    private static (bool Updated, int Changed, int Total) TryPush(
        IntPtr window, RectI spot, IntPtr destDc, string destText)
    {
        Frame before = Screen.Grab(spot);
        (bool updated, string note) = Push(window, spot, destDc, destText);
        Pump(300);
        (int changed, int total) = Diff(before, Screen.Grab(spot), spot);
        return (updated, changed, total);
    }

    // 窗口过程用不到：一个没有背景刷的窗类不会被 WM_PAINT 找上门。
    private static readonly WindowProc Keep = (window, message, w, l) => DefWindowProcStub(window, message, w, l);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static IntPtr DefWindowProcStub(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 交给系统那一扇。量测程序的消息泵不在这个线程上，DefWindowProc 答完就够。
        return message == 0x0011 /* WM_SETFOCUS */ || message == 0x000F /* WM_PAINT */
            ? IntPtr.Zero
            : DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>正档那一发。返回的 <c>Updated</c> 是「<c>UpdateLayeredWindow</c> 答了真」——
    /// 答假的话像素那一问压根没发生，这一发作废。</summary>
    private static ArmResult Arm(WindowInfo header, WindowInfo iconList, RectI spot)
    {
        IntPtr handle = header.Handle;
        var notes = new List<string>();

        // 屏幕坐标 → 图标层的客户区坐标。成了子窗之后 SetWindowPos 的 x/y 是父窗客户区坐标。
        var client = new NativePoint { X = spot.Left, Y = spot.Top };
        bool converted = ScreenToClient(iconList.Handle, ref client);
        notes.Add($"坐标换算 ({spot.Left},{spot.Top})→({client.X},{client.Y}) 成功={converted}");

        // hWndInsertAfter 传图标列表 = 排在它下面（图标压在日历上面）。
        bool placed = SetWindowPos(
            handle, iconList.Handle, client.X, client.Y, spot.Width, spot.Height,
            SwpNoActivate | SwpFrameChanged);
        notes.Add($"排进图标列表之下：成功={placed}，错误码 {Marshal.GetLastWin32Error()}");

        // 样式位：加 WS_EX_LAYERED。<b>读回来对一遍</b> —— SetWindowLongPtr 返回的是换之前的值，
        // 拿返回值当「换上了没有」是 2026-10-12 踩过的坑。
        long exBefore = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        long wanted = exBefore | WsExLayered;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)wanted);
        long exAfter = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        notes.Add($"WS_EX_LAYERED：换之前 0x{exBefore:X}、读回来 0x{exAfter:X}" +
                  $"{(exAfter == wanted ? "（换上并读回确认）" : "**读回来对不上，没换上**")}");

        ShowWindow(handle, SwShow);

        // destDc 传 0 —— 对照那一发量出来的：顶层窗传屏的 DC 那一版读 0 个像素变，
        // 传 0 才画得出来。子窗跨进程拿不到父窗的 DC，本来也只能传 0。
        (bool updated, string note) = Push(handle, spot, IntPtr.Zero, "传 0");
        notes.Add($"UpdateLayeredWindow：{note}");

        return new ArmResult(updated, string.Join("；", notes));
    }

    private readonly record struct ArmResult(bool Updated, string Text)
    {
        internal string Describe() => Text;
    }

    /// <summary>造一张纯色 DIB，用 <c>UpdateLayeredWindow</c> 打进去。
    ///
    /// <para><b><paramref name="destDc"/> 传不传屏的 DC，两条都量</b>（2026-10-15）。
    /// 子窗那一路传 0 画得出来，而顶层窗传屏的 DC 那一版读到 0 —— 那一版只量到了
    /// 「传错 destDc 画不出来」，量不到「层叠子窗画不出来」。这个参数怎么传是量出来的，
    /// 不是照文档抄的。</para></summary>
    private static (bool Updated, string Note) Push(IntPtr window, RectI spot, IntPtr destDc, string destText)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = spot.Width,
            Height = -spot.Height,   // 负数 = 从上往下填
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr bitmap = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
            return (false, $"造不出那一块 DIB，错误码 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            IntPtr previous = SelectObject(memory, bitmap);

            // 整块填成同一个颜色。BGRA。
            var pixel = new byte[] { Fill.B, Fill.G, Fill.R, 0xFF };
            var row = new byte[spot.Width * 4];
            for (int i = 0; i < spot.Width; i++) Array.Copy(pixel, 0, row, i * 4, 4);
            for (int y = 0; y < spot.Height; y++)
            {
                Marshal.Copy(row, 0, bits + y * spot.Width * 4, row.Length);
            }

            var dest = new NativePoint { X = spot.Left, Y = spot.Top };
            var source = new NativePoint { X = 0, Y = 0 };
            var size = new NativeSize { Width = spot.Width, Height = spot.Height };
            var blend = new BlendFunction { ConstantAlpha = 0xFF };

            bool ok = UpdateLayeredWindow(
                window, destDc, ref dest, ref size,
                memory, ref source, 0, ref blend, UlwAlpha);

            SelectObject(memory, previous);
            return (ok, ok
                ? $"答真（destDc={destText}）"
                : $"**答假**，错误码 {Marshal.GetLastWin32Error()}（destDc={destText}）");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>还原到发之前那一副样子。<b>逐项读回来</b>，不拿返回值当成了。</summary>
    private static void Restore(WindowInfo header, Shape before)
    {
        IntPtr handle = header.Handle;
        ShowWindow(handle, SwHide);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoZorder | SwpNoActivate | SwpFrameChanged);
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)before.ExStyle);
    }

    // ---- 找宿主 ---------------------------------------------------------------

    /// <summary>桌面那几扇。宿主<b>按「类名 + 可见 + 满屏」挑</b>，不按
    /// 「顶层里没有 DefView 的那个 WorkerW」挑 —— 后者会挑中别的进程留下的 170×47 空壳
    /// （2026-10-15 踩过）。</summary>
    private sealed class Host
    {
        private Host(IntPtr defView, List<WindowInfo> iconLayers, List<WindowInfo> headers, string candidates)
        {
            DefView = defView;
            IconLayers = iconLayers;
            Headers = headers;
            Candidates = candidates;
        }

        internal IntPtr DefView { get; }

        private List<WindowInfo> IconLayers { get; }

        private List<WindowInfo> Headers { get; }

        internal string Candidates { get; }

        internal static Host Pick(IntPtr progman)
        {
            MonitorInfo screen = Windows.PrimaryMonitor();
            var shaped = new List<string>();
            IntPtr defView = IntPtr.Zero;
            var iconLayers = new List<WindowInfo>();
            var headers = new List<WindowInfo>();

            foreach (WindowInfo w in Windows.Descendants(progman))
            {
                bool full = w.Rect.Width >= screen.Monitor.Width * 9 / 10 &&
                            w.Rect.Height >= screen.Monitor.Height * 9 / 10;
                bool shown = w.Visible && full;
                shaped.Add($"{w.ClassName} 0x{w.Handle.ToInt64():X} 可见={(w.Visible ? 1 : 0)} 满屏={(full ? 1 : 0)} 矩形={w.Rect}");

                if (w.ClassName != "SHELLDLL_DefView") continue;
                if (!shown) continue;
                if (defView != IntPtr.Zero) continue;   // 多一扇就当没挑中，不猜

                defView = w.Handle;
                foreach (WindowInfo child in Windows.Descendants(w.Handle))
                {
                    if (child.ClassName == "SysListView32") iconLayers.Add(child);
                    if (child.ClassName == "SysHeader32") headers.Add(child);
                }
            }

            return new Host(defView, iconLayers, headers,
                shaped.Count == 0 ? "一扇后代也没有" : string.Join("；", shaped.Take(12)));
        }

        /// <summary>那一类子窗。<b>挑 z 序最顶上那扇</b>，不按名字取第一扇。</summary>
        internal WindowInfo? Child(IntPtr parent, string className)
        {
            List<WindowInfo> all = Windows.ChildWindows(parent)
                .Where(w => w.ClassName == className).ToList();
            return all.Count == 0 ? null : all[0];
        }

        internal string Describe()
        {
            string lists = string.Join("、", IconLayers.Select(w => $"0x{w.Handle.ToInt64():X} 矩形={w.Rect}"));
            string heads = string.Join("、", Headers.Select(w => $"0x{w.Handle.ToInt64():X} 矩形={w.Rect}"));
            return $"图标层 0x{DefView.ToInt64():X}（SHELLDLL_DefView）；底下的图标列表：{lists}；" +
                   $"底下的 SysHeader32：{heads}";
        }
    }

    /// <summary>一扇窗发之前那一副样子。<b>还原完要拿它逐项对</b>。</summary>
    private readonly record struct Shape(bool Visible, RectI Rect, long ExStyle)
    {
        internal static Shape Of(IntPtr handle) => new(
            Windows.IsWindowVisible(handle),
            Windows.RectOf(handle),
            GetWindowLongPtr(handle, GwlExStyle).ToInt64());

        internal string Describe() =>
            $"可见={(Visible ? 1 : 0)} 矩形={Rect} 扩展样式=0x{ExStyle:X}";
    }

    // ---- 挑那一块 + 逐像素比 ---------------------------------------------------

    /// <summary>挑一块<b>图标之上、app 窗之下都没有</b>的地方。
    /// 有 app 窗压着的话，正档读到的变化可能全是那扇窗的。
    ///
    /// <para><b>判「压着」只问命中测试，不问矩形相交</b>（这是本仓库量出来的规矩，
    /// 读数在研究文档「判「谁压着谁」要问命中测试」那一节）：屏上有一批
    /// <c>Cua.AgentCursorOverlay</c> / <c>Windows.UI.Core.CoreWindow</c> 那样的窗，
    /// 它们的矩形铺满整块屏却<b>不吃命中测试</b>、像素一个也不画。拿矩形相交去挑，
    /// 每一块都会被它们挡下来，而那一发就永远答不了。</para></summary>
    private static RectI PickSpot(out string note)
    {
        List<string> tried = new();
        foreach (RectI candidate in new[]
                 {
                     DefaultSpot, new(200, 300, 840, 420), new(200, 900, 840, 1020),
                     new(60, 60, 700, 180), new(60, 1080, 700, 1200),
                 })
        {
            List<string> blockers = HitBlockers(candidate);
            if (blockers.Count == 0)
            {
                note = "按命中测试问，那一块上面既没有桌面图标、也没有 app 窗压着";
                return candidate;
            }

            tried.Add($"{candidate}：{string.Join("、", blockers.Take(3))}");
        }

        note = "**五处都有东西压着，这一块不干净** —— " + string.Join("；", tried);
        throw new InvalidOperationException("挑不出一块干净的矩形。" + note);
    }

    /// <summary>那一块里按命中测试问出来、归别家窗的那些格。<b>边和角都问</b> ——
    /// 只问中心会漏掉「只压住一条边」那一种。</summary>
    private static List<string> HitBlockers(RectI candidate)
    {
        List<string> blockers = new();
        List<Icons.IconBox> icons = Icons.ReadAll(out _);

        for (int i = 0; i <= 4; i++)
        {
            for (int j = 0; j <= 2; j++)
            {
                int x = candidate.Left + candidate.Width * i / 4;
                int y = candidate.Top + candidate.Height * j / 2;
                IntPtr hit = Windows.At(x, y);
                IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Windows.RootOf(hit);
                string rootClass = Windows.ClassOf(root);

                // 桌面图标：命中测试答的是图标层，可它画不画在那一颗上要问 UIA 的矩形。
                if (Find.IsWallpaperLayer(rootClass) && icons.Any(b => b.Rect.Contains(x, y)))
                {
                    string name = icons.First(b => b.Rect.Contains(x, y)).Name;
                    if (!blockers.Contains($"桌面图标「{name}」")) blockers.Add($"桌面图标「{name}」");
                    continue;
                }

                if (hit == IntPtr.Zero || Find.IsWallpaperLayer(rootClass)) continue;
                if (WindowTier.IsSystemShell(rootClass)) continue;

                string who = $"{Windows.ClassOf(hit)}（pid {Windows.ProcessIdOf(hit)}）";
                if (!blockers.Contains(who)) blockers.Add(who);
            }
        }

        return blockers;
    }

    private static bool Overlaps(RectI a, RectI b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>那一块上有多少颗采样像素。<b>和 <see cref="Diff"/> / <see cref="CountColour"/>
    /// 走的是同一个上界</b>（<c>for (v = 0; v &lt; 边长; v += Stride)</c>，也就是<b>进一法</b>）。
    ///
    /// <para>原先预检那一行自己算了一遍「整除再加一」，2026-10-15 那一跑里它报 19581、
    /// 同一跑后面各处都是 19200 —— <b>同一个数在一跑里出现两个值</b>，读的人没法判断哪一个
    /// 是采样真数。改成现问这一处。</para></summary>
    private static int SampleCount(RectI spot) =>
        ((spot.Width + Stride - 1) / Stride) * ((spot.Height + Stride - 1) / Stride);

    /// <summary>两帧逐采样像素比。<b>数的是有几颗变了，不是差多少</b> ——
    /// 判据是「每一个采样像素都变」，均值会被壁纸拖平。</summary>
    private static (int Changed, int Total) Diff(Frame before, Frame after, RectI spot)
    {
        int changed = 0;
        int total = 0;
        for (int y = 0; y < spot.Height; y += Stride)
        {
            int row = (y + spot.Top - before.Where.Top) * before.Width;
            if (y + spot.Top >= before.Where.Bottom || y + spot.Top >= after.Where.Bottom) continue;

            for (int x = 0; x < spot.Width; x += Stride)
            {
                int px = x + spot.Left;
                if (px >= before.Where.Right || px >= after.Where.Right) continue;

                int at = (row + px - before.Where.Left) * 4;
                if (before.Pixels[at] != after.Pixels[at] ||
                    before.Pixels[at + 1] != after.Pixels[at + 1] ||
                    before.Pixels[at + 2] != after.Pixels[at + 2])
                {
                    changed++;
                }

                total++;
            }
        }

        return (changed, total);
    }

    private static string SplitText(bool ok) => ok
        ? "答真，桌面拆开了"
        : $"答假，错误码 {Marshal.GetLastWin32Error()}（桌面未必拆开）";

    /// <summary>抽一会儿消息。<b>不抽，窗不会真的落到屏幕上</b> ——
    /// 造出来只是登记，要消息循环走一遍（DWM 那一层才拿得到它）。
    /// 走 <see cref="CoverWindow.Pump"/>，那一处是这个仓库里量过的那一份。</summary>
    private static void Pump(int milliseconds) => CoverWindow.Pump(milliseconds);

    /// <summary>数那一块上有多少颗采样像素是<b>本进程打进去的那一种颜色</b>。
    ///
    /// <para>「变了」和「变成我打的那个颜色」不是同一句话。这一问分开它们 ——
    /// 块上要是别人的动画在动，它也会让「变了」成立，而它推出的是另一件事。</para></summary>
    private static (int Mine, int Total) CountColour(Frame frame, RectI spot)
    {
        int mine = 0;
        int total = 0;
        for (int y = 0; y < spot.Height; y += Stride)
        {
            int py = y + spot.Top;
            if (py < frame.Where.Top || py >= frame.Where.Bottom) continue;
            for (int x = 0; x < spot.Width; x += Stride)
            {
                int px = x + spot.Left;
                if (px < frame.Where.Left || px >= frame.Where.Right) continue;

                int at = ((py - frame.Where.Top) * frame.Width + px - frame.Where.Left) * 4;
                if (frame.Pixels[at] == Fill.B && frame.Pixels[at + 1] == Fill.G && frame.Pixels[at + 2] == Fill.R) mine++;
                total++;
            }
        }

        return (mine, total);
    }
}
