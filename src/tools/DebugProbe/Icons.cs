using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;

namespace DebugProbe;

/// <summary>
/// 桌面图标那一层：它在哪、它有几条、每一条的矩形在哪。
///
/// <para>这一发是给工单 07 选读法的。<b>选型不靠印象</b> —— 消息族那一族
/// （<c>LVM_GETITEMCOUNT</c>）在低完整性下已经量到答不出来，
/// 所以这里换一条路当场量：UI Automation。</para>
///
/// <para>为什么值得先试它：<b>一条问句问完一整层</b>，句子里没有任何跨进程指针，
/// 也不需要 <c>ReadProcessMemory</c>。工单 07 要的就是这一种。</para>
///
/// <para>UI Automation 那一份在 <c>UIAutomationClient.dll</c> 里，属于
/// <c>Microsoft.WindowsDesktop.App</c> 这个共用框架。那是系统自带的一份程序集，
/// 不是 NuGet 包，所以「不引任何包」这条没被破。代价是 exe 起来要机器上装着
/// 桌面运行时，换这一行的理由记在研究笔记里。</para>
///
/// <para>实测结果在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。</para>
/// </summary>
internal static class Icons
{
    internal static int Run(string[] args)
    {
        Console.Out.WriteLine($"完整性级别：{Windows.IntegrityLevel()}");
        Console.Out.WriteLine();

        // 按句柄去重。壁纸层底下不止一扇，枚举会把同一扇走两遍，
        // 而报两遍会让读数看着像「桌面上有两层图标」。
        List<WindowInfo> candidates = Windows.IconLayerCandidates()
            .DistinctBy(w => w.Handle)
            .ToList();

        if (candidates.Count == 0)
        {
            Console.Out.WriteLine("图标层候选：零扇 —— 壁纸层底下没有枚举到 SysListView32");
            return 1;
        }

        int readAny = 0;

        DesktopFolderReference();

        foreach (WindowInfo icon in candidates)
        {
            Console.Out.WriteLine(
                $"图标层候选：0x{icon.Handle.ToInt64():X} pid={icon.ProcessId} 矩形={icon.Rect} " +
                $"可见={(icon.Visible ? "1" : "0")} 爹={Windows.ClassOf(Windows.ParentOf(icon.Handle))}");

            // 对照：消息族那一族在低完整性下答成什么样，读数要一起摆出来。
            Console.Out.WriteLine($"  消息族：{Windows.PingMessage(icon.Handle)}；条数 {Windows.IconCount(icon.Handle)}" +
                                  $"（错误码 {Windows.LastIconCountError}）");

            List<IconBox> boxes = ReadByUia(icon.Handle, out string note);
            Console.Out.WriteLine($"  UI Automation（从句柄）：{note}");
            foreach (IconBox box in boxes.Take(6))
            {
                Console.Out.WriteLine($"    「{box.Name}」 矩形={box.Rect}");
            }

            if (boxes.Count > 6)
            {
                Console.Out.WriteLine($"    ……还有 {boxes.Count - 6} 条");
            }

            List<IconBox> rooted = ReadFromRoot(out string rootNote);
            Console.Out.WriteLine($"  UI Automation（从根）：{rootNote}");
            foreach (IconBox box in rooted.Take(6))
            {
                Console.Out.WriteLine($"    「{box.Name}」 矩形={box.Rect}");
            }

            if (rooted.Count > 6)
            {
                Console.Out.WriteLine($"    ……还有 {rooted.Count - 6} 条");
            }

            if (boxes.Count > 0 || rooted.Count > 0) readAny++;
        }

        UiaControl();
        RemoteMemory();

        Console.Out.WriteLine();
        Console.Out.WriteLine(readAny > 0
            ? $"这一发答出来了：{readAny} 扇图标层读得到条目。"
            : "这一发答不出来：没有任何一条读法读到图标条目。");

        return readAny > 0 ? 0 : 1;
    }

    /// <summary>一个图标条目。名字用来认它，矩形用来算它回不回原像素。</summary>
    internal readonly record struct IconBox(string Name, RectI Rect)
    {
        /// <summary>正中心。拖放按下要落在图标身上，不是落在它旁边的空地上。</summary>
        internal PointI Center => new(Rect.Left + Rect.Width / 2, Rect.Top + Rect.Height / 2);
    }

    /// <summary>
    /// 读一次图标位置，顺带把这一读数打出来。
    ///
    /// <para>几发都要读这一问，而打印口径各写一份就会各说各话（有的报条数、
    /// 有的只报一句「没变」）。所以读和打绑在一起，<paramref name="when"/> 原样进读数 ——
    /// 「第几轮」「发之前」「发之后」写进去，别人不用回头猜是哪一次读数。</para>
    /// </summary>
    internal static List<IconBox> ReadPlaces(string when)
    {
        List<IconBox> boxes = ReadAll(out string note);
        Console.Out.WriteLine($"  图标位置（{when}）：{note}，{boxes.Count} 颗");
        return boxes;
    }

    /// <summary>
    /// 动作前后各读一次图标位置，把「有没有被动过」那一问答成一句话。
    ///
    /// <para><b>按名字配对比矩形，不比条数。</b>条数一样而位置变了，是这一族最容易漏的一处 ——
    /// 拖走一颗再放回去，条数读出来一模一样。变了的那几颗要<b>摆出来</b>，
    /// 不只报一句「有变化」。</para>
    ///
    /// <para>SPEC 第四节要求「每一次动作之后……读图标位置」，而这一族有几发只读条数
    /// （清场、扫桌面、托盘菜单、遮罩抬落）。共用这一个函数是刻意的 ——
    /// 同一问在几发里要有同一把尺子。</para>
    ///
    /// <para><b>返回句不带「图标位置：」这几个字</b>，标签由各发自己写 ——
    /// 标签带着「哪一轮」「点开之前」这些上下文，各发才知道怎么叫它。
    /// 早先这一句自带标签，调用处又写一遍，读数里就成了「图标位置：图标位置：」。</para>
    /// </summary>
    internal static string ComparePlaces(List<IconBox> before, List<IconBox> after)
    {
        if (before.Count == 0 || after.Count == 0)
        {
            return $"读不到（发之前 {before.Count} 颗、发之后 {after.Count} 颗），这一问不作数";
        }

        var moved = new List<string>();
        var gone = new List<string>();
        foreach (IconBox b in before)
        {
            IconBox a = after.FirstOrDefault(x => x.Name == b.Name);
            if (a.Name.Length == 0) gone.Add($"「{b.Name}」{b.Rect}");
            else if (a.Rect != b.Rect) moved.Add($"「{b.Name}」{b.Rect} → {a.Rect}");
        }

        List<string> fresh = after.Where(a => before.All(b => b.Name != a.Name))
            .Select(a => $"「{a.Name}」{a.Rect}").ToList();

        if (moved.Count == 0 && gone.Count == 0 && fresh.Count == 0 && before.Count == after.Count)
        {
            return $"{before.Count} 颗逐颗比下来**一个没少、一个没动**";
        }

        return $"**动了** —— 共 {before.Count} → {after.Count} 颗；" +
               (moved.Count > 0 ? $"挪了 {moved.Count} 颗（{string.Join("；", moved)}）；" : string.Empty) +
               (gone.Count > 0 ? $"不见了 {gone.Count} 颗（{string.Join("；", gone)}）；" : string.Empty) +
               (fresh.Count > 0 ? $"多了 {fresh.Count} 颗（{string.Join("；", fresh)}）；" : string.Empty);
    }

    /// <summary>
    /// 读桌面图标层现在有哪几条。<b>拖放前后必须走同一个函数读两次</b> ——
    /// 从句柄和从根这两条路给的矩形不一样（量于 10-12：同一枚图标高 113 对 108），
    /// 混着比会得出「图标没回原像素」这种假结论。
    /// </summary>
    internal static List<IconBox> ReadAll(out string note)
    {
        WindowInfo? layer = Windows.IconLayer();
        if (layer == null)
        {
            note = "读不到图标层 —— 壁纸层底下没有枚举到 SysListView32";
            return new List<IconBox>();
        }

        note = $"图标层 0x{layer.Handle.ToInt64():X}（pid {layer.ProcessId}，{layer.Rect}）；";
        List<IconBox> found = ReadByUia(layer.Handle, out string more);
        note += more;
        return found;
    }

    /// <summary>
    /// 从 UIA 的根往下找。桌面图标在 UIA 树上的位置<b>不是</b>图标层的子节点 ——
    /// 那一扇只答得出一个 <c>Pane</c>。所以这一条才是 UIA 的正路：
    /// 根 → 那一扇桌面 → 整棵后代 → 筛 <c>ListItem</c>。
    ///
    /// <para>两个读法都要留着：一条答得出来才算数，答不出来要分清
    /// 「跨完整性被挡」和「那一扇底下本来就没有」。</para>
    /// </summary>
    private static List<IconBox> ReadFromRoot(out string note)
    {
        var found = new List<IconBox>();
        var names = new List<string>();
        try
        {
            AutomationElement root = AutomationElement.RootElement;
            AutomationElementCollection children = root.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                names.Add($"「{child.Current.Name}」/{child.Current.ClassName}/0x{child.Current.NativeWindowHandle:X}");
            }

            AutomationElement desktop = root.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Progman"));

            if (desktop == null)
            {
                note = $"根底下 {children.Count} 个子节点（{string.Join("、", names)}），" +
                       "其中没有类名 Progman 的那一扇";
                return found;
            }

            AutomationElementCollection items = desktop.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement item in items) found.Add(Box(item));

            // 普查：那一扇底下到底有什么。答「0 个 ListItem」之前，
            // 要先看得见整棵树 —— 只报一个 0，会分不清「没有条目」和「没找对地方」。
            AutomationElementCollection all = desktop.FindAll(
                TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            var census = new List<string>();
            foreach (AutomationElement node in all)
            {
                census.Add(
                    $"「{node.Current.Name}」{node.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty)}" +
                    $"/{node.Current.ClassName}/矩形={node.Current.BoundingRectangle}");
            }

            note = $"根底下 {children.Count} 个子节点；" +
                   $"Progman 那一扇底下后代 {all.Count} 个、其中 ControlType.ListItem {items.Count} 个";
            if (census.Count > 0) note += "；后代普查：" + string.Join("　", census.Take(8));
            return found;
        }
        catch (Exception ex)
        {
            note = $"抛异常了 —— {Describe(ex)}";
            return found;
        }
    }

    /// <summary>
    /// UI Automation 那一读法。读到几条都原样报出来，读到零条也报出来。
    ///
    /// <para>先问直接子节点，问到零条再往整棵后代树问一遍 —— 「图标条目是图标的
    /// 直接子节点」是没量过的假设，问一层答不出来就换一层，别只试一次就下结论。</para>
    /// </summary>
    private static List<IconBox> ReadByUia(IntPtr handle, out string note)
    {
        var found = new List<IconBox>();
        try
        {
            AutomationElement layer = AutomationElement.FromHandle(handle);
            if (layer == null)
            {
                note = "从句柄拿不到那一扇的元素（FromHandle 答了 null）";
                return found;
            }

            note =
                $"这一扇叫「{layer.Current.Name}」（{layer.Current.ControlType.ProgrammaticName}，" +
                $"类别名 {layer.Current.ClassName}）{Ancestors(layer)}";

            AutomationElementCollection children =
                layer.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            note += $"；直接子节点 {children.Count} 个";
            foreach (AutomationElement child in children) found.Add(Box(child));

            if (found.Count == 0)
            {
                AutomationElementCollection deep =
                    layer.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                note += $"；后代 {deep.Count} 个";
                foreach (AutomationElement child in deep) found.Add(Box(child));
            }

            return found;
        }
        catch (Exception ex)
        {
            note = $"抛异常了 —— {Describe(ex)}";
            return found;
        }
    }

    /// <summary>
    /// 往上数四层，把原生句柄和类名摆出来。
    /// UIA 树上有的层没有原生句柄，所以句柄和类名要一起看 ——
    /// 只看句柄会把「那一层不是一扇窗」误当成「那一层不存在」。
    /// </summary>
    private static string Ancestors(AutomationElement element)
    {
        var parts = new List<string>();
        AutomationElement current = element;
        for (int i = 0; i < 4 && current != null; i++)
        {
            AutomationElement up = TreeWalker.RawViewWalker.GetParent(current);
            if (up == null) break;
            parts.Add($"[{up.Current.ClassName}/0x{up.Current.NativeWindowHandle:X}]");
            current = up;
        }

        return parts.Count == 0 ? string.Empty : "；上一层链 " + string.Join(" ← ", parts);
    }

    /// <summary>
    /// 名字用来认图标，矩形用来算它回不回原像素。
    /// <b>空矩形单独标出来</b> —— 空矩形是「这一条没落在屏幕上」，
    /// 和「这一条落在原点」在数字上不是同一件事。
    /// </summary>
    private static IconBox Box(AutomationElement element)
    {
        Rect rect = element.Current.BoundingRectangle;
        bool empty = rect.Width <= 0 || rect.Height <= 0;
        return new IconBox(
            element.Current.Name ?? string.Empty,
            empty
                ? new RectI(0, 0, 0, 0)
                : new RectI(
                    (int)Math.Round(rect.Left),
                    (int)Math.Round(rect.Top),
                    (int)Math.Round(rect.Right),
                    (int)Math.Round(rect.Bottom)));
    }

    /// <summary>异常那一句要带 HRESULT。跨进程读失败时，HRESULT 才是有信息量的那部分。</summary>
    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}：{ex.Message}（HRESULT 0x{Marshal.GetHRForException(ex):X8}）";

    /// <summary>
    /// 桌面目录里有几个东西。这不是「图标有几条」，只是给「0 条」一个对照 ——
    /// 桌面上明明摆着东西而图标层读出 0 条，那就是读法坏了，不是桌面上没有。
    /// </summary>
    private static void DesktopFolderReference()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            int shown = Directory.Exists(dir)
                ? Directory.GetFiles(dir).Count(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                : -1;
            Console.Out.WriteLine($"桌面目录：{dir}；里面的文件（去掉 desktop.ini）{shown} 个 —— 这是对照，不是图标条数");
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"桌面目录读不到 —— {Describe(ex)}");
        }

        Console.Out.WriteLine(HideIcons());
        Console.Out.WriteLine();
    }

    /// <summary>
    /// 「显示桌面图标」那个开关。关了它，图标层就是空的 ——
    /// 这时候读出 0 条不是读法坏了，是桌面上本来就没摆。
    /// 这一句不问清楚，后面「0 条」会被当成读法坏了去查半天。
    /// </summary>
    private static string HideIcons()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            object? value = key?.GetValue("HideIcons");
            return value == null
                ? "显示桌面图标：注册表里没有这一项（按没关处理）"
                : $"显示桌面图标：HideIcons={value}（{(Convert.ToInt32(value) != 0 ? "关着" : "开着")}）";
        }
        catch (Exception ex)
        {
            return $"显示桌面图标：读不到 —— {Describe(ex)}";
        }
    }

    /// <summary>
    /// 对照面：UIA 枚举子节点这一族到底通不通。
    ///
    /// <para>拿「<c>EnumChildWindows</c> 数得出来、且数出来不为零」的几扇窗，
    /// 再问一遍 UIA 答几 个子节点。两边对着看，才分得清
    /// 「桌面那一扇底下真的没有条目」和「UIA 跨完整性枚举子节点整族被挡」。</para>
    /// </summary>
    private static void UiaControl()
    {
        Console.Out.WriteLine("对照面 —— EnumChildWindows 数的 vs UIA 答的：");

        List<WindowInfo> probes = Windows.TopLevelWindows()
            .Where(w => w.Visible && w.ClassName is "Chrome_WidgetWin_1" or "Progman" or "Shell_TrayWnd")
            .GroupBy(w => w.ClassName)
            .Select(g => g.First())
            .ToList();

        foreach (WindowInfo probe in probes)
        {
            int byEnum = Windows.ChildWindows(probe.Handle).Count;
            string byUia;
            try
            {
                AutomationElement element = AutomationElement.FromHandle(probe.Handle);
                byUia = element == null
                    ? "FromHandle 答 null"
                    : $"{element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition).Count} 个";
            }
            catch (Exception ex)
            {
                byUia = Describe(ex);
            }

            Console.Out.WriteLine($"  pid={probe.ProcessId,-6} {probe.ClassName,-22} 子节点：EnumChildWindows {byEnum} 个 vs UIA {byUia}");
        }

        Console.Out.WriteLine();
    }

    /// <summary>
    /// 往 explorer 的地址空间里动手，行不行得通。
    ///
    /// <para>工单 07 点过名的那一条路：<c>LVM_GETITEMRECT</c> 要往对方进程里写一块缓冲，
    /// 而写进去要 <c>OpenProcess(VM_WRITE)</c>。这一发只量「这扇门开不开」，
    /// 不去量怎么读写 —— 门不开的话，后面的算法一条都白写。</para>
    /// </summary>
    private static void RemoteMemory()
    {
        const int ProcessAllAccess = 0x1F0FFF;

        WindowInfo? explorer = Windows.TopLevelWindows()
            .FirstOrDefault(w => w.ClassName == "Progman");

        if (explorer == null)
        {
            Console.Out.WriteLine("跨进程内存：读不到 explorer");
            return;
        }

        IntPtr handle = OpenProcess(ProcessAllAccess, false, explorer.ProcessId);
        if (handle == IntPtr.Zero)
        {
            Console.Out.WriteLine(
                $"跨进程内存：OpenProcess(PROCESS_ALL_ACCESS, pid {explorer.ProcessId}) 答零，" +
                $"错误码 {Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            IntPtr remote = VirtualAllocEx(handle, IntPtr.Zero, 4096, 0x1000 /* MEM_COMMIT */, 0x04 /* PAGE_READWRITE */);
            if (remote == IntPtr.Zero)
            {
                Console.Out.WriteLine($"跨进程内存：门开了，但 VirtualAllocEx 答零，错误码 {Marshal.GetLastWin32Error()}");
                return;
            }

            byte[] four = { 0x01, 0x02, 0x03, 0x04 };
            IntPtr staging = Marshal.AllocHGlobal(four.Length);
            try
            {
                Marshal.Copy(four, 0, staging, four.Length);
                int written = 0;
                bool ok = WriteProcessMemory(handle, remote, staging, four.Length, out written);
                Console.Out.WriteLine(ok
                    ? $"跨进程内存：门开了，WriteProcessMemory 写进去 {written} 个字节"
                    : $"跨进程内存：门开了，WriteProcessMemory 答假，错误码 {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.FreeHGlobal(staging);
                VirtualFreeEx(handle, remote, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, int type, int protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr process, IntPtr address, IntPtr buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
