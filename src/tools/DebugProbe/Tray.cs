using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace DebugProbe;

/// <summary>
/// 工单 09 第一发：托盘菜单。
///
/// <para><b>这一发量的是菜单的项顺序</b>，不是「菜单有没有出来」。
/// 出来没出来从屏幕上看得见，项顺序要看那一扇菜单窗答的是什么 ——
/// 所以判据是：从那一扇弹出菜单窗上读到的那几项，
/// 「打开休息历史…」在<b>子菜单的第 0 项</b>（契约里定的是最上面）。</para>
///
/// <para><b>读法用 UI Automation</b>，和工单 07 同一个理由：一条问句问完一整层，
/// 句子里没有跨进程指针。<c>GetMenuItemInfo</c> 那一族收别人的 <c>HMENU</c> 要跨进程，
/// 而它没有带进程号的那一档 —— 那一族问不出来（问出来的那几颗要往对方地址空间里放指针）。
/// UIA 答得出「这一扇菜单底下有哪几个 <c>MenuItem</c>、叫什么、排第几」。</para>
///
/// <para><b>为什么自己点开而不靠产品那个 <c>--tray-menu</c> 开关</b>：
/// 那个开关是产品自己排程自己拍帧，量具这一发要的是<b>从外面点开、外面读</b>。
/// 两个都留着，产品那一发在 <c>src/Shots.cs</c> 里。</para>
///
/// <para>出处：<c>.scratch/debug-tool/issues/09-product-own-actions.md</c>。</para>
/// </summary>
internal static class Tray
{
    internal static int Run(string[] args)
    {
        bool listOnly = args.Contains("--list");
        string outDir = FrameShot.DefaultOutDir();

        using Shot shot = Shot.Begin(
            listOnly
                ? "通知区里有哪几颗图标"
                : "托盘菜单：抬出来、读项顺序、判「打开休息历史…」在不在子菜单第 0 项",
            "菜单那一项从弹出菜单窗上读，判据是「打开休息历史…」在子菜单的第 0 项；" +
            "摆出来那一帧要看得清项顺序，也要带一行归谁；" +
            "每一次鼠标动作前后各读一次图标位置");

        WindowInfo? taskbar = Windows.TopLevelWindows()
            .FirstOrDefault(w => w.ClassName == "Shell_TrayWnd");
        if (taskbar == null)
        {
            shot.Finish("中止 —— 屏上没有任务栏那一扇（Shell_TrayWnd），托盘那一发答不了", "无");
            return Find.Refused;
        }

        List<(string Name, RectI Rect)> icons = NotificationArea(taskbar);
        Console.Out.WriteLine($"  通知区一共 {icons.Count} 颗：");
        foreach ((string name, RectI rect) in icons)
        {
            Console.Out.WriteLine($"    {rect} 「{name}」");
        }

        if (listOnly)
        {
            shot.Finish(
                $"任务栏 {taskbar.Handle}（pid {taskbar.ProcessId}）；通知区读到 {icons.Count} 颗：" +
                string.Join("；", icons.Select(i => $"「{i.Name}」{i.Rect}")),
                "无");
            return 0;
        }

        (int X, int Y, string Name)? ours = PickOurs(icons);
        if (ours == null)
        {
            // 可见那一档里没有，就去溢出那一档找 —— 「没找到」得说清是没找还是找错了地方。
            icons = OpenOverflow(taskbar, "Keeper");
            Console.Out.WriteLine($"  溢出那一档打开之后读到 {icons.Count} 颗：");
            foreach ((string name, RectI rect) in icons)
            {
                Console.Out.WriteLine($"    {rect} 「{name}」");
            }

            ours = PickOurs(icons);
        }

        if (ours == null)
        {
            shot.Finish(
                "中止 —— 通知区里没有认出产品的图标。这一发答不了。" +
                $"读到的那几颗：{string.Join("；", icons.Select(i => $"「{i.Name}」{i.Rect}"))}",
                "无");
            return Find.Refused;
        }

        Console.Out.WriteLine($"  点开「{ours.Value.Name}」{ours.Value.X},{ours.Value.Y}");

        // 图标位置这一问在动作之前先读一遍 —— 点通知区不该挪桌面图标，
        // 而「不该」要靠前后两读来证，不是靠说不该。
        List<Icons.IconBox> placesBefore = Icons.ReadPlaces("点开之前");

        // 先把鼠标停上去再点：和日历那一发同一个理由（产品按帧认位置）。
        // **右键，不是左键**（量于 10-12）：产品没给托盘图标设版本号，shell 按老协议发，
        // 左键切预览、右键出菜单（`src/Tray.cs` 的 `OnMessage`）。左键点完屏上什么菜单都没有，
        // 而那一帧里弹出菜单窗一扇也没有 —— 不说清用的哪一颗，读数会让人去查菜单那一族。
        Input.MoveTo(ours.Value.X, ours.Value.Y);
        System.Threading.Thread.Sleep(400);
        Input.RightClickAt(ours.Value.X, ours.Value.Y, 200);
        System.Threading.Thread.Sleep(800);

        List<WindowInfo> menus = PopupMenus();
        List<Icons.IconBox> placesAfterClick = Icons.ReadPlaces("点开之后");
        string placesClick = Icons.ComparePlaces(placesBefore, placesAfterClick);
        if (menus.Count == 0)
        {
            shot.Finish(
                $"点开「{ours.Value.Name}」之后<b>没有弹出菜单窗</b>（屏上找不到类名 #32768 的可见窗）。" +
                "这一发答不出来。通知区读到的那几颗：" +
                string.Join("；", icons.Select(i => $"「{i.Name}」{i.Rect}")),
                "无");
            return Find.Refused;
        }

        WindowInfo menu = menus[0];
        List<string> top = ReadMenuItems(menu.Handle);
        Console.Out.WriteLine($"  菜单窗 {menu.Handle} {menu.Rect}，{top.Count} 项");

        // 「打开休息历史…」**不在菜单顶上** —— 它是「休息历史」那一行挂出来的子菜单的顶上第一颗
        //（src/Tray.cs 的 LifeModeRows 那段写着这件事，是 2026-10-12 所有者定的次序）。
        // 所以这一发读两级：先把菜单那一级的项读下来，再把鼠标移到「休息历史」那一行上把它展开，
        // 再读子菜单那一级。**两级的项序都要摆出来**，只看一级答不了这一条判据。
        string historyRow = top.FirstOrDefault(t => t.Contains("休息历史", StringComparison.Ordinal)) ?? string.Empty;
        WindowInfo? sub = null;
        List<string> subItems = new List<string>();

        if (historyRow.Length > 0)
        {
            RectI row = ItemRect(menu.Handle, historyRow);
            if (row.Width > 0)
            {
                Console.Out.WriteLine($"  「{historyRow}」在 {row}，把鼠标移上去把它展开");
                Input.MoveTo(row.Left + row.Width / 2, row.Top + row.Height / 2);
                System.Threading.Thread.Sleep(800);

                List<WindowInfo> after = PopupMenus();
                foreach (WindowInfo candidate in after.Where(m => m.Handle != menu.Handle))
                {
                    List<string> items = ReadMenuItems(candidate.Handle);
                    if (items.Any(t => t.Contains("打开休息历史", StringComparison.Ordinal)))
                    {
                        sub = candidate;
                        subItems = items;
                        break;
                    }
                }
            }
        }

        int index = subItems.FindIndex(t => t.Contains("打开休息历史", StringComparison.Ordinal));

        // 子菜单那一下是第二次鼠标动作，同样读一遍图标位置 —— 悬停去展开也是动作。
        List<Icons.IconBox> placesAfterSub = Icons.ReadPlaces("子菜单读完之后");
        string placesSub = Icons.ComparePlaces(placesBefore, placesAfterSub);

        var shots = new List<string>();
        shots.Add(Capture(menu, top, "菜单那一级", outDir));
        if (sub != null) shots.Add(Capture(sub, subItems, "子菜单那一级", outDir));

        shot.Finish(
            $"点开通知区里的「{ours.Value.Name}」{ours.Value.X},{ours.Value.Y}，" +
            $"弹出菜单窗 0x{menu.Handle.ToInt64():X}（pid {menu.ProcessId}）矩形={menu.Rect}，" +
            $"{top.Count} 项，顺序是 {string.Join(" → ", top.Select((t, i) => $"第 {i} 项「{t}」"))}。" +
            (historyRow.Length == 0
                ? "**菜单里没有「休息历史」那一行** —— 子菜单那一级没量到。"
                : sub == null
                    ? $"指到「{historyRow}」之后**子菜单那一扇没找到**（" +
                      $"屏上那几扇 #32768：{string.Join("、", PopupMenus().Select(m => $"0x{m.Handle.ToInt64():X}{m.Rect}"))}）—— 这一级答不出来。"
                    : $"指到「{historyRow}」之后子菜单窗 0x{sub.Handle.ToInt64():X} 矩形={sub.Rect}，" +
                      $"{subItems.Count} 项，顺序是 {string.Join(" → ", subItems.Select((t, i) => $"第 {i} 项「{t}」"))}；" +
                      $"「打开休息历史…」在第 {index} 项 —— 判：{(index == 0 ? "**是子菜单第 0 项**" : "**不是第 0 项**")}") +
            $"。两级的帧都留了档，各带一行归谁：{string.Join("；", shots)}。" +
            $"图标位置那两问：右键点开那一下之后 —— {placesClick}；子菜单读完那一下之后 —— {placesSub}",
            shots.Count > 0 ? shots[0] : "无");

        // 收尾：把菜单收掉，别把屏幕留在弹着菜单的状态里。
        Input.SendKeyPair(0x1B, 0x1B);

        return index == 0 ? 0 : Find.Refused;
    }

    /// <summary>弹出菜单那几扇。类名 <c>#32768</c> —— Win32 菜单窗就是这一类。
    /// 子菜单是<b>另一扇同类的窗</b>，所以这里返回的是一份，不是一扇。</summary>
    private static List<WindowInfo> PopupMenus()
    {
        return Windows.TopLevelWindows()
            .Where(w => w.Visible && w.ClassName == "#32768")
            .OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height)
            .ToList();
    }

    /// <summary>抓一帧，带一行归谁。</summary>
    private static string Capture(WindowInfo menu, List<string> items, string which, string outDir)
    {
        Frame frame = Screen.Grab(Expand(menu.Rect, 120));
        frame.OwnerText =
            $"{which}：菜单窗 0x{menu.Handle.ToInt64():X}（pid {menu.ProcessId}）矩形={menu.Rect}；" +
            $"读到 {items.Count} 项：" + string.Join("、", items.Select((t, i) => $"第 {i} 项「{t}」"));
        string path = System.IO.Path.Combine(
            outDir, which == "菜单那一级" ? "tray-menu.png" : "tray-submenu.png");
        frame.SavePng(path);
        return path;
    }

    /// <summary>菜单里某一项的矩形。答不出就是空矩形 —— 那和「在原点」不是同一件事。</summary>
    private static RectI ItemRect(IntPtr menu, string name)
    {
        try
        {
            AutomationElement element = AutomationElement.FromHandle(menu);
            if (element == null) return new RectI(0, 0, 0, 0);
            AutomationElement? item = element.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.NameProperty, name)));
            if (item == null) return new RectI(0, 0, 0, 0);
            return ToRect(item.Current.BoundingRectangle);
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"  读菜单项的矩形抛异常了：{ex.GetType().Name}：{ex.Message}");
            return new RectI(0, 0, 0, 0);
        }
    }

    /// <summary>通知区里的图标。读法是 UIA：任务栏底下那一层按钮。</summary>
    private static List<(string Name, RectI Rect)> NotificationArea(WindowInfo taskbar)
    {
        var found = new List<(string, RectI)>();
        try
        {
            AutomationElement bar = AutomationElement.FromHandle(taskbar.Handle);
            if (bar == null) return found;

            AutomationElementCollection all = bar.FindAll(
                TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement node in all)
            {
                string type = node.Current.ControlType.ProgrammaticName;
                if (type is not ("ControlType.Button" or "ControlType.CheckBox" or "ControlType.MenuItem")) continue;
                Rect rect = node.Current.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (rect.Top < taskbar.Rect.Top) continue;   // 通知区在任务栏下沿那一带
                found.Add((node.Current.Name ?? string.Empty, ToRect(rect)));
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"  读通知区抛异常了：{ex.GetType().Name}：{ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// 认出产品那一颗。名字带产品名的先选。
    ///
    /// <para><b>它多半不在通知区直接那一档里</b>（量于 10-12）：
    /// 任务栏底下 UIA 读到 16 颗，可见的那一档里一颗产品图标都没有，
    /// 只有一个「显示隐藏的图标」那颗尖角。所以认不出来的时候先点开那一档再找 ——
    /// <b>「找不到」要说清是没找还是找错了地方</b>，这两件在读数上不是同一句。</para>
    /// </summary>
    private static (int X, int Y, string Name)? PickOurs(List<(string Name, RectI Rect)> icons)
    {
        foreach ((string name, RectI rect) in icons)
        {
            if (name.Contains("Keeper", StringComparison.OrdinalIgnoreCase))
            {
                return (rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, name);
            }
        }

        return null;
    }

    /// <summary>点开「显示隐藏的图标」那一档，再从 <b>UIA 根</b>重找一遍产品那颗。
    ///
    /// <para><b>为什么从根找而不是从任务栏底下找</b>（量于 10-12）：点开那一档之后，
    /// 任务栏底下那一层一颗新的都没有 —— 溢出面板是<b>另一扇窗</b>
    /// （XAML 那一族，不是 <c>Shell_TrayWnd</c> 的子树），从任务栏那棵树上找不到它。
    /// 工单 07 那条「图标在 DefView 的子窗堆里还是顶层」是同一族的坑：一个读法只管一棵子树。</para>
    /// </summary>
    private static List<(string Name, RectI Rect)> OpenOverflow(WindowInfo taskbar, string wanted)
    {
        RectI? chevron = null;
        foreach ((string name, RectI rect) in NotificationArea(taskbar))
        {
            if (name.Contains("显示隐藏的图标", StringComparison.Ordinal) ||
                name.Contains("Notification Chevron", StringComparison.OrdinalIgnoreCase))
            {
                chevron = rect;
                break;
            }
        }

        // **上一跑可能已经把它开着**（那一发跑完不收它）。开着还去点尖角就是把它**点关**，
        // 症状是「第二轮之后一颗都找不到」—— 而那和「它没开过」在读数上长得一样。
        // 所以先问它开着没有，开着就直接用。
        WindowInfo? overflow = OverflowWindow();
        if (overflow is { Visible: true })
        {
            Console.Out.WriteLine($"  溢出那一档已经开着：0x{overflow.Handle.ToInt64():X}/{overflow.ClassName} {overflow.Rect}，不点尖角");
            return SearchDesktop(wanted);
        }

        if (chevron == null)
        {
            Console.Out.WriteLine("  任务栏底下没有「显示隐藏的图标」那一档的尖角，找不到溢出那一档");
            return new List<(string, RectI)>();
        }

        int cx = chevron.Value.Left + chevron.Value.Width / 2;
        int cy = chevron.Value.Top + chevron.Value.Height / 2;
        Console.Out.WriteLine($"  点开溢出那一档：尖角 {chevron.Value} 中心 ({cx},{cy})");

        HashSet<IntPtr> before = Windows.TopLevelWindows().Select(w => w.Handle).ToHashSet();
        Input.MoveTo(cx, cy);
        System.Threading.Thread.Sleep(400);
        Input.ClickAt(cx, cy, 200);
        System.Threading.Thread.Sleep(900);

        List<WindowInfo> appeared = Windows.TopLevelWindows().Where(w => !before.Contains(w.Handle)).ToList();
        Console.Out.WriteLine($"  点开之后新出现的顶层窗 {appeared.Count} 扇：" +
            (appeared.Count == 0
                ? "一扇也没有"
                : string.Join("；", appeared.Select(w => $"0x{w.Handle.ToInt64():X}/{w.ClassName}（pid {w.ProcessId}）{w.Rect}"))));

        // 点开那一档有没有反应，**留一帧自己看**：读数说「没有新窗」，
        // 而那一档也许是一扇已经存在的窗换了内容，也可能压根没开出来 —— 屏幕上那一张分得开。
        Frame evidence = Screen.Grab(Windows.PrimaryMonitor().Monitor);
        evidence.OwnerText =
            $"点「显示隐藏的图标」尖角 ({cx},{cy}) 之后 {900} 毫秒拍的一帧；" +
            $"新出现的顶层窗 {appeared.Count} 扇";
        evidence.SavePng(System.IO.Path.Combine(
            FrameShot.DefaultOutDir(), "tray-overflow.png"));

        List<WindowInfo> overflowish = Windows.TopLevelWindows()
            .Where(w => w.ClassName.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase) ||
                        w.ClassName.Contains("Xaml", StringComparison.OrdinalIgnoreCase) ||
                        w.ClassName.Contains("Taskbar", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.Out.WriteLine($"  屏上与通知区/任务栏有关的顶层窗 {overflowish.Count} 扇：" +
            (overflowish.Count == 0 ? "一扇也没有"
                : string.Join("；", overflowish.Select(w => $"0x{w.Handle.ToInt64():X}/{w.ClassName}（pid {w.ProcessId}）可见={(w.Visible ? 1 : 0)}{w.Rect}"))));

        List<(string Name, RectI Rect)> hits = SearchDesktop(wanted);
        Console.Out.WriteLine($"  从 UIA 根往下找「{wanted}」，找到 {hits.Count} 颗：" +
            (hits.Count == 0 ? "一颗也没有" : string.Join("；", hits.Select(h => $"「{h.Name}」{h.Rect}"))));
        return hits;
    }

    /// <summary>溢出那一扇（<c>TopLevelWindowForOverflowXamlIsland</c>）。
    /// 这个类名是**量出来的**，不是抄来的 —— 它不是任务栏的子树，
    /// 按任务栏底下那一棵树找会一颗都找不到（量于 10-12）。</summary>
    private static WindowInfo? OverflowWindow()
    {
        return Windows.TopLevelWindows()
            .FirstOrDefault(w => w.ClassName == "TopLevelWindowForOverflowXamlIsland");
    }

    /// <summary>从 UIA 根往下找名字含这一段的按钮。<b>整棵树都走</b>，
    /// 不预设它在任务栏底下 —— 溢出面板不在那儿（量于 10-12）。</summary>
    private static List<(string Name, RectI Rect)> SearchDesktop(string fragment)
    {
        var hits = new List<(string, RectI)>();
        try
        {
            AutomationElement root = AutomationElement.RootElement;
            var queue = new Queue<(AutomationElement Node, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                (AutomationElement node, int depth) = queue.Dequeue();
                if (depth > 6) continue;

                AutomationElementCollection children;
                try
                {
                    children = node.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                }
                catch (Exception)
                {
                    continue;   // 有一支枚举不到（跨完整性被挡）；那一支跳过，不当成「底下没有」
                }

                foreach (AutomationElement child in children)
                {
                    queue.Enqueue((child, depth + 1));
                    string name = child.Current.Name ?? string.Empty;
                    if (!name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                    string type = child.Current.ControlType.ProgrammaticName;
                    if (type is not ("ControlType.Button" or "ControlType.CheckBox" or "ControlType.MenuItem" or "ControlType.Text")) continue;
                    Rect rect = child.Current.BoundingRectangle;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    hits.Add(($"{type.Replace("ControlType.", string.Empty)}「{name}」", ToRect(rect)));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"  从根找抛异常了：{ex.GetType().Name}：{ex.Message}");
        }

        return hits;
    }


    /// <summary>菜单窗底下那几项，按菜单里的次序。</summary>
    private static List<string> ReadMenuItems(IntPtr handle)
    {
        var items = new List<string>();
        try
        {
            AutomationElement menu = AutomationElement.FromHandle(handle);
            if (menu == null) return items;

            AutomationElementCollection children = menu.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            foreach (AutomationElement child in children)
            {
                items.Add(child.Current.Name ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"  读菜单项抛异常了：{ex.GetType().Name}：{ex.Message}");
        }

        return items;
    }

    private static RectI ToRect(Rect rect) => new(
        (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
        (int)Math.Round(rect.Right), (int)Math.Round(rect.Bottom));

    /// <summary>菜单常常比那一扇窗自己画的矩形大一点 —— 截图那一块要把它整个包住。</summary>
    private static RectI Expand(RectI rect, int by)
    {
        int left = Math.Max(0, rect.Left - by);
        int top = Math.Max(0, rect.Top - by);
        var work = Windows.PrimaryMonitor().Work;
        return new RectI(left, top, Math.Min(work.Right, rect.Right + by), Math.Min(work.Bottom, rect.Bottom + by));
    }
}