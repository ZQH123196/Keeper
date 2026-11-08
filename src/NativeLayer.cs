using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Keeper;

/// <summary>看门狗那一发的五种答案。<b>卡片 03 · S24</b>：以前调用方在返回的那一行里
/// 比「不用动」三个字认「宿主没变」，字符串一改判据就静悄悄失效；现在认这一枚枚举。</summary>
internal enum WatchKind { NotAttached, Lifted, NoMove, HostMissing, Reattached }

/// <summary>看门狗那一发的结果：<b>分类走 <see cref="Kind"/>，那一行实测结果原样走 <see cref="Note"/></b>。
/// <see cref="Note"/> 逐字等于换类型前 <c>Watch()</c> 返回的那一行。</summary>
internal readonly record struct WatchResult(WatchKind Kind, string Note);

/// <summary>推进去那一发的结果。<b><see cref="Attached"/> 答「那一扇在不在」、
/// <see cref="Ok"/> 答 <c>UpdateLayeredWindow</c> 答没答真、<see cref="Note"/> 是那一行实测结果</b>
/// —— 逐字等于换类型前 <c>Push()</c> 返回的那一行。以前调用方在返回的那一行里比「答真」
/// 认推进成功，两个码空间（这一行里的字和成败）混在一份字符串里。</summary>
internal readonly record struct PushOutcome(bool Attached, bool Ok, string Note);

/// <summary>
/// 日历那一块像素<b>推进图标层底下那一扇</b>的那一族：借窗、挂接、推像素、重挂。
///
/// <para><b>为什么不是引擎自己那一扇窗</b>：引擎那一扇挂进壳的父子链之后<b>不画</b> ——
/// 三个爹各试一次、逼窗口重建/逼交换链重建/强制重绘三条逼法都试过，全是 0 像素变化。
/// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>
/// 「把 Godot 这个因素摘掉之后，结论翻过来了」那一节。取向在
/// <c>docs/adr/0007-the-calendar-window-is-under-the-icons.md</c>。</para>
///
/// <para><b>本机量到的读数</b>（2026-10-15，工单 28 第 0 步）：层叠子窗排在图标列表<b>之下</b>，
/// <c>UpdateLayeredWindow</c> 打一张纯色 DIB 进去，那一块 <b>19200 / 19200</b> 个采样像素
/// <b>全变</b>，而那一种颜色占了 19200 / 19200 颗；不挂进去连抓两帧 <b>0 / 19200</b> 变。
/// <b><c>destDc</c> 必须传 0</b> —— 传屏的 DC 那一档读 0 个像素变（见
/// <c>src/tools/DebugProbe/Layered.cs</c> 的对照那一发）。</para>
///
/// <para><b>三个不能省的</b>：
/// <list type="bullet">
/// <item><b>先发 <c>0x052C</c></b>：不发桌面就不拆，没有可挂的画布。</item>
/// <item><b>z 序排在 <c>SysListView32</c> 之下</b>：这就是「图标压在日历上面」唯一被量到的排法。</item>
/// <item><b>看门狗</b>：睡眠唤醒、换分辨率、换 DPI 之后壳会重建这几层，父子关系会断。</item>
/// </list></para>
///
/// <para><b>日历那一扇是借壳自己的窗</b> —— 图标层底下那个 <c>SysHeader32</c>
/// （壳自己放着、平时藏着不用的一扇）。<b>产品不自己造那一扇</b>，2026-10-15 排掉了四条：
/// 自建顶层窗 <c>SetParent</c> 挂进图标层，<c>UpdateLayeredWindow</c> 连推十几发都答真，
/// 屏上 0 像素变，而同一扇窗抬成顶层就画得出来；带着 <c>WS_EX_LAYERED</c> 建子窗，
/// <c>CreateWindowEx</c> 当场答 0、错误码也是 0；不带层叠建出来、<c>SetParent</c> 之后再补这一位，
/// 读回来是 0，<c>UpdateLayeredWindow</c> 答假、错误码 87；产品<b>自己进程内</b>推进去一块不透明纯色，
/// 答真、屏上照样 0 像素。<b>借来的那一扇同一套动作四发全过</b>（对照过、反向验 0 / 19200、
/// 正档 19200 / 19200、还原逐字相同）—— 量具在 <c>src/tools/DebugProbe/Layered.cs</c>，
/// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>。
/// <b>两条路剩下的差别只有一条：那一扇是壳自己造的</b>，不是换过爹的。</para>
///
/// <para><b>借来的那一扇要还</b>：不拆（<c>DestroyWindow</c> 一发就是拆掉壳的东西），
/// 把扩展样式、矩形、可见性和 z 序原样还回去 —— 见 <see cref="Detach"/>。</para>
internal sealed class NativeLayer
{
    // ---- 这一族要摸的 Win32 ---------------------------------------------------
    //
    // 声明全部收在 `Win32.cs` 一份里（卡片 04 · S17）：这里只留策略常量。
    // 样式位、`Win32.UlwAlpha`、`AcSrcAlpha`、`Point` / `Size` / `Blend` / `BitmapInfo`
    // 用的都是 `Win32` 已有那份，不在这里抄第二份。

    /// <summary>推进去那一发要交的那份 <c>BLENDFUNCTION</c>。
    /// <b>生产走这一份，自检拧的也是这一份</b> —— 别在 <see cref="Update"/> 里另写一个字面量，
    /// 那样判据就管不到它。</summary>
    private static Win32.Blend PushBlend() =>
        new() { Flags = Win32.AcSrcAlpha, ConstantAlpha = 0xFF, AlphaFormat = Win32.AcSrcAlpha };

    /// <summary>判据：这份 blend 有没有让<b>源图自己的 alpha 通道</b>参与混。
    /// <b>纯算术</b>，自检拿 <see cref="PushBlend"/> 的读数拧一遍。</summary>
    ///
    /// <para><b>全 0 加 <c>ConstantAlpha = 0xFF</c> 就答假</b> ——
    /// 那一档是「按 <c>SourceConstantAlpha</c> 混」，源图的 alpha 一个字节都不参与，
    /// 整块按不透明画。真机读到屏上 57% 的像素是 <c>(0,0,0)</c>，而推进去的字节里
    /// <c>alpha=94</c> 占了 76 万颗。</para></summary>
    internal static bool BlendTellsSourceAlpha(byte op, byte flags, byte constantAlpha, byte alphaFormat) =>
        op == 0 && flags == Win32.AcSrcAlpha && constantAlpha == 0xFF && alphaFormat == Win32.AcSrcAlpha;

    /// <summary>「放出来」那一发要发的那一条 <c>ShowWindow</c>：<b>放出来、不激活</b>。
    /// <b>判据本体，纯算术</b>，而产品走的是同一份（<see cref="ShowAgain"/> 逐条发它）——
    /// 自检钉的就是这一份。</summary>
    ///
    /// <para><b>原来这里是两条：先藏、再放</b>，那是产品自己加的，为了救 <c>Win+D</c>
    /// 之后那一档（对已经看得见的窗发「放出来」是空动作，而那一扇当时就是看得见的）。
    /// <b>量具借窗那一族没有这一格</b>：它借的那一扇本来就藏着，
    /// 从来没有过「已经看得见、还要藏回去」的时候（<c>src/tools/DebugProbe/Layered.cs</c>
    /// 的 <c>Arm</c>：排进图标列表之下、加 <c>WS_EX_LAYERED</c>、放出来、推进去，四发全过）。
    /// 产品照那一族改成只放不藏，<b>排掉「藏那一下把层叠面丢了」这一问</b>。</para>
    ///
    /// <para><b>放的那一条不许是 <c>SW_SHOW</c></b>：那一条会激活日历、抢前台 ——
    /// 契约「无感」第 4 条明写它不抢前台（<c>.scratch/keeper-contract/spec.md</c>）。
    /// 同族另一条判据（自检里那一格）盯的就是这一位。</para>
    public static IReadOnlyList<int> ShowAgainCommands { get; } =
        new[] { Win32.SwShowNoActivate };

    /// <summary>看门狗的周期（秒）。<b>平时是空转</b>，只在宿主变了的那一次才动手。</summary>
    public const int WatchdogSeconds = 30;

    /// <summary>补推的周期（秒）：宿主没变、画面也没变，到点照样把<b>挂接那一整套</b>
    /// 再走一遍，再推一次像素。
    ///
    /// <para><b>为什么要有这一发</b>：层叠子窗缓存的那张面归 DWM 管，而壳重画桌面
    /// （<c>Win+D</c> 让 <c>Progman</c> 整块重画）会把那张面丢掉 —— <b>那一扇还挂着、
    /// 还 <c>vis=1</c>，屏上一个像素都没有</b>。产品平时只在「画面变了」的时候推，
    /// 而内容一个字没改，所以<b>永远没人补推</b>，日历就一直空到有人翻月、选日或者过午夜。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>
    /// 「层叠子窗只推一次」那一节。</para>
    ///
    /// <para><b>这一发不是「推进去」，是「重新挂 + 推进去」</b>：只推进去救不回来 ——
    /// 2026-10-15 真机那一发，<c>MinimizeAll</c> 之后 22 秒里补推了四发以上，
    /// 屏上那一块一直是 153.15（<b>没有日历</b>，日历在的时候是 94.51）。
    /// <b>而把日历救回来的那一发，产品一行日志都没有</b> —— 没推进去、没重新挂，
    /// 动手的是壳那一侧（点的是桌面，重画的是桌面）。
    /// 所以「重新挂」是<b>按「把那一扇重新露出来」这一族里产品唯一有的排法选的，
    /// 真机那一发还要验</b>，不是量出来的。读数在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c> 那一节。</para>
    ///
    /// <para><b>为什么是 5 秒，两头都有理由</b>：
    /// <list type="bullet">
    /// <item><b>下限</b>：1 秒就是每帧读回。读回是同步的，整块要读 5,652,480 字节
    /// 再就地转 BGRA（逐像素 1,413,120 次），在帧里做完就是那一帧停住那么久 ——
    /// 仓库里「读回是同步的，每帧做一次就是每帧卡一次」那一条直接否掉。</item>
    /// <item><b>上限</b>：那一块是 1024x1380，停在屏上的空白超过几秒就看得见；
    /// 而 <see cref="WatchdogSeconds"/> 那个 30 秒太长，空白会停满 30 秒。</item>
    /// </list>
    /// 5 秒在两头之间：每小时 720 次补推（平均每秒不到一次读回），
    /// 而空白最长 5 秒。</para></summary>
    public const int RepushSeconds = 5;

    /// <summary>补推累计多少发记一行日志。<b>成功那一路不记日志</b>（一天一万七千多行
    /// 会把应用日志淹掉），而「补推有没有在跑」在产品里就只剩这一行 ——
    /// 没有它，「跑过」和「没跑过」在屏上和日志里长得一模一样。
    /// 周期不变的话 <see cref="RepushLogEvery"/> × <see cref="RepushSeconds"/> 就是 60 秒
    /// （自检里钉的就是这一格）。</summary>
    public const int RepushLogEvery = 12;

    // ---- 这一族的实测结果 ----------------------------------------------------

    /// <summary>借之前那一扇的样子。<b>拆不得，只能还</b> —— 那一扇是壳的窗。</summary>
    /// <param name="Style">窗口样式（<c>WS_CHILD</c> 那一位在不在）。
    /// <b>必须一起记下来</b>：<see cref="Lift"/> 会把它改成 <c>WS_POPUP</c>，
    /// 退出时只还原扩展样式而漏了这一位，壳那一扇就留在顶层当浮窗了（2026-10-16 补的）。</param>
    private sealed record Borrowed(IntPtr Handle, long ExStyle, Win32.Rect Rect, bool Visible, IntPtr Parent, IntPtr Below, long Style);

    private IntPtr _hwnd;

    /// <summary>借之前读下来的那一份。<b>是「那一扇是借来的」这唯一的凭据</b> ——
    /// 还的时候照着它还，而拆不拆也由它答（<see cref="ShouldDestroyOnRelease"/>）。</summary>
    private Borrowed? _borrowed;

    private IntPtr _defView;
    private IntPtr _iconList;
    private Win32.Rect _placed;
    private string _lastMeasurement = "还没挂过";

    /// <summary>那一扇现在抬到顶层了没有（编辑态）。<b>抬起期间不排在图标层之下</b> ——
    /// 它那时是顶层窗，图标也压不住它，而那是契约「无感」第 2 条写明的代价。</summary>
    private bool _lifted;

    /// <summary>那一扇挂好了没有。<b>「日历看得见」现在不归引擎管</b> ——
    /// 那一扇不出像素就是黑的，所以这一句是那一族唯一的凭据。</summary>
    public bool Attached => _hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd);

    /// <summary>日历那一扇的句柄（0 = 还没借到）。<b>编辑态抬的是它</b>，
    /// 不是引擎那一扇 —— 见 <see cref="EditingWindow"/>。<b>那一扇是壳的窗</b>，
    /// 属主进程不是我们（日志那一行会写出来）。</summary>
    public nint Handle => _hwnd;

    /// <summary>最近一次挂接/重挂的那一行实测结果。日志和失败模式都读它。</summary>
    public string Measurement => _lastMeasurement;

    /// <summary>建那一扇原生窗并挂进图标层。<paramref name="where"/> 是屏幕坐标那一块。
    /// 宿主由 <see cref="DesktopLayer.CalendarHost"/> 那一份快照答（卡片 04 · S19）。</summary>
    public string Attach(Win32.Rect where) => Place(DesktopLayer.CalendarHost(), where);

    /// <summary>到点那一发：<b>把挂接那一整套再走一遍，宿主没变也照样走</b>，
    /// 走完再推一次像素（推那一发在 <c>Calendar</c> 那边，它要引擎的视口）。
    ///
    /// <para><b>为什么不是只推进去</b>：真机读数（2026-10-15，工单 28 收尾）——
    /// <c>MinimizeAll</c> 之后 <c>UpdateLayeredWindow</c> 答真、屏上却一个像素没有，
    /// 22 秒里补推四发以上全是这样（日志那一段是空的，因为成功不记行）。
    /// <b>推是必要条件，不是充分条件</b>：屏上那一块能不能回来，取决于壳那边还认不认
    /// 这一扇。<b>这一发就是去碰那一扇</b> —— 样式、<c>SetParent</c>、
    /// <c>SetWindowPos</c> 排在图标列表之下、<c>ShowWindow</c>，
    /// 走的是产品里唯一量到过的排法。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>
    /// 「层叠子窗只推一次」那一节。</para>
    ///
    /// <para><b>和 <see cref="Watch"/> 是两件事</b>：看门狗问「壳拆了重搭没有」，
    /// 它答「宿主没变」的时候那一扇确实不用动；这一发问的是「DWM 手里那张面还在不在」，
    /// 而<b>宿主一块没动的时候那张面照样会被壳重画丢掉</b> ——
    /// 所以 <see cref="ShouldRepushReattach"/> 压根不看宿主那一位。</para></summary>
    public string Reattach(Win32.Rect where)
    {
        var host = DesktopLayer.CalendarHost();
        // 宿主变了没有 —— **这一位只进判据、不改动作**：变了要重挂，没变**照样**要重挂。
        bool changed = host.DefView != _defView || host.IconList != _iconList;
        if (!ShouldRepushReattach(Attached, changed))
            return $"那一扇已经不在了（宿主={host.Measurement}），到点这一发没重挂";

        return $"重新挂（宿主变了={changed}）：{Place(host, where)}";
    }

    /// <summary>挂接那一整套：样式 → <c>SetParent</c> → <c>SetWindowPos</c> 排在
    /// <c>SysListView32</c> 之下 → <c>ShowWindow</c> → 推进去。
    /// <paramref name="host"/> 已经找好了 —— 挂接（<see cref="Attach"/>）和到点重挂
    /// （<see cref="Reattach"/>）走的是同一段动作，所以它住在这里，不在两处各抄一份。</summary>
    private string Place(DesktopLayer.Host host, Win32.Rect where)
    {
        if (host.DefView == IntPtr.Zero)
        {
            _lastMeasurement = $"图标层读不出来（{host.Measurement}），这一发没挂成";
            return _lastMeasurement;
        }

        // 坐标是父窗客户区坐标，所以屏幕坐标要换一次。
        var client = new Win32.Point { X = where.Left, Y = where.Top };
        bool converted = Win32.ScreenToClient(host.DefView, ref client);

        if (!Borrow(host, out var why))
        {
            _lastMeasurement = $"借不到日历那一扇（{why}）";
            return _lastMeasurement;
        }

        // **样式和爹只在这一扇换过爹的时候才动**：抬到顶层那一发（<see cref="Lift"/>）
        // 把它改成了 `WS_POPUP`、爹也换成了 0，落回来要反着改回去。
        // 借来的那一扇本来就在图标层底下，这两发是空动作 —— 而空动作不是「无害的空动作」。
        if (Win32.GetParent(_hwnd) != host.DefView)
        {
            long style = Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle).ToInt64();
            Win32.SetWindowLongPtr(_hwnd, Win32.GwlStyle, (IntPtr)((style | Win32.WsChild) & ~Win32.WsPopup));
            // `SetParent` 是父子关系那一步（`SetWindowPos` 不是）。
            Win32.SetParent(_hwnd, host.DefView);
        }

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle).ToInt64();
        // **停着那一档只保留 `WS_EX_LAYERED`** —— 和量具借窗那一族逐字一样。
        // 那三位（穿鼠标 / 不激活 / 工具窗）在停着这一档是多余的：
        // 图标层那个 `SysListView32` 铺满整块屏、又在日历上面，命中测试本来就到不了这一扇
        // （`docs/adr/0007-the-calendar-window-is-under-the-icons.md`「点得动靠钩子」那一节量到的）。
        //
        // `SetWindowLongPtr` 的**返回值**是换之前那一位 —— 它答 0 而读回来也是 0，
        // 说明这一位压根没换上（错误码一并打出来，别把两处失败混成一句）。
        long exBefore = Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, (IntPtr)(ex | Win32.WsExLayered)).ToInt64();
        long exError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

        // ---- 挪不挪这一扇：只在真的要挪的时候才发 SetWindowPos ----
        //
        // **对着已经看得见的一扇挪完就立刻推进去，屏上一个像素都不出**（2026-10-16 真机读数：
        // 产品和量具各量了一次，两次都是推进去答真、0 像素变）。`SetWindowPos` 和 `ShowWindow`
        // 打在**壳那一侧线程**上：那一挪还没在那边落地，紧接着的 `UpdateLayeredWindow`
        // 就按旧位置落了层叠面；而下一发又先挪再推 —— 于是每一发都落空。
        // 量具那一族量到过的次序是<b>藏着挪、放出来、推进去</b>
        // （<c>src/tools/DebugProbe/Layered.cs</c> 的 <c>Arm</c>：那一扇本来就藏着，
        // <c>SetWindowPos</c> 排在 <c>ShowWindow</c> 前面）。
        Win32.GetWindowRect(_hwnd, out var was);
        bool placeChanged = was.Left != client.X || was.Top != client.Y ||
                            was.Width != where.Width || was.Height != where.Height ||
                            Win32.GetParent(_hwnd) != host.DefView ||
                            Win32.GetWindow(_hwnd, Win32.GwHwndPrev) != host.IconList;
        bool visible = Win32.IsWindowVisible(_hwnd);

        // **挪之前先藏**：只在「真的要挪、而它此刻看得见」这一档（判据 HideBeforeMove）。
        // 挪完再放出来（ShouldShow 那一格）。
        if (HideBeforeMove(placeChanged, visible)) Win32.ShowWindow(_hwnd, Win32.SwHide);
        if (placeChanged)
        {
            // **hWndInsertAfter 必须是同一层里的兄弟**：那一扇挂在图标列表底下的时候，
            // 传图标列表（它是爹）是非法的，那一档只摆位置、不动 z 序。
            bool underIconLayer = Win32.GetParent(_hwnd) == host.DefView;
            Win32.SetWindowPos(_hwnd, underIconLayer ? host.IconList : IntPtr.Zero,
                client.X, client.Y, where.Width, where.Height,
                Win32.SwpFrameChanged | Win32.SwpNoActivate |
                (underIconLayer ? 0u : Win32.SwpNoZorder));
        }

        // 放出来那一发只在它藏着的时候发：对已经看得见的窗发「放出来」是空动作。
        var again = ShouldShow(visible) ? ShowAgain() : "本来就看得见，不发放出来那一发";
        // **放不出来就再发 `SWP_SHOWWINDOW` 那一路**：`ShowWindow` 是发给那一扇自己那条线程的，
        // 它答完了可见状态一位没动（2026-10-16 新壳读数：样式 `|CHILD`、`可见=False`）。
        // `SetWindowPos` 带 `SWP_SHOWWINDOW` 是另一条路，它不挪也不改 z 序。
        again += ShowByFlag();

        _defView = host.DefView;
        _iconList = host.IconList;
        _placed = where;
        _lifted = false;

        // **读回来对一遍**，不拿 SetWindowPos 的返回值当读数 —— 那一发答「发出去了没有」，
        // 不答「排成什么样了」。
        Win32.GetWindowRect(_hwnd, out var now);
        _lastMeasurement =
            $"借来壳自己那一扇 0x{_hwnd:X}/{DesktopLayer.BorrowedClass}（属主 pid {ProductOwner()}，" +
            $"借之前 扩展样式=0x{_borrowed?.ExStyle:X} 矩形={Describe(_borrowed?.Rect ?? where)} " +
            $"可见={(_borrowed?.Visible == true ? "1" : "0")}）；" +
            $"挂进图标层：爹=0x{host.DefView:X}/{DesktopLayer.DefViewClass} 图标列表=0x{host.IconList:X} " +
            $"排在它之下；坐标换算 ({where.Left},{where.Top})→({client.X},{client.Y}) 成功={converted}；" +
            $"重新露出来={again}；" +
            $"矩形={Describe(now)} 可见={Win32.IsWindowVisible(_hwnd)} " +
            $"样式={Win32.DescribeStyle(Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle))} " +
            $"扩展样式={Win32.DescribeExStyle(Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle))}" +
            $"（换之前那一位=0x{exBefore:X} 错误码={exError}）；{RankAgainst(_hwnd, host.IconList)}";
        return _lastMeasurement;
    }

    /// <summary>那一扇归哪个进程。<b>答案通常是壳，不是我们</b> ——
    /// 日志里那一行要写明这一扇到底是谁的，省得下一轮去产品进程里找它。</summary>
    private int ProductOwner()
    {
        Win32.GetWindowThreadProcessId(_hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>挪那一扇之前要不要先藏它。<b>判据本体，纯算术</b>，自检拿真假各拧一遍：
    /// 只有「<b>真的要挪、而它此刻看得见</b>」这一档先藏。
    ///
    /// <para><b>理由是量出来的</b>：对着看得见的一扇挪完就推进去，屏上一个像素都不出
    ///（2026-10-16 真机，产品和量具各一次，两次都推进去答真、0 像素变）。
    /// 量具那一族量到过的次序是<b>藏着挪、放出来、推进去</b>
    ///（`src/tools/DebugProbe/Layered.cs` 的 `Arm`：那一扇本来就藏着，`SetWindowPos`
    /// 排在 `ShowWindow` 前面）。</para></summary>
    public static bool HideBeforeMove(bool placeChanged, bool visible) => placeChanged && visible;

    /// <summary>放出来那一发发不发。<b>判据本体，纯算术</b>，自检拿真假各拧一遍：
    /// 只在那一扇藏着的时候发 —— 对已经看得见的窗发「放出来」是空动作。</summary>
    public static bool ShouldShow(bool visible) => !visible;

    /// <summary>放出来那一发的第二路：<c>SetWindowPos</c> 带 <c>SWP_SHOWWINDOW</c>。
    /// 不挪、不改尺寸、不改 z 序，只放出来。<b>已经看得见就不发</b>（那是空动作）。
    ///
    /// <para><b>这一路才是真放出来的那一路</b>（2026-10-16 新壳真机读数）：
    /// <c>ShowWindow</c> 那一发发完，可见的样式位<b>一位没动</b>
    ///（读回来 <c>|CHILD</c>，没有 <c>|VISIBLE</c>，<c>IsWindowVisible</c> 答假），
    /// 而 <c>SWP_SHOWWINDOW</c> 那一发发完立刻答「可见=True、样式 <c>|CHILD|VISIBLE</c>」。
    /// <b>带着这一位，日历才第一次出现在屏上</b>（帧在
    /// <c>.scratch/debug-tool/shots/clean-after.png</c>）。</para></summary>
    private string ShowByFlag()
    {
        if (Win32.IsWindowVisible(_hwnd)) return "本来就看得见，不发 SWP_SHOWWINDOW 那一路";
        bool sent = Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoZorder |
            Win32.SwpNoActivate | Win32.SwpShowWindow);
        return $"SWP_SHOWWINDOW 那一路：发出去={sent} 错误码={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} " +
               $"发之后可见={Win32.IsWindowVisible(_hwnd)} " +
               $"样式={Win32.DescribeStyle(Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle))}";
    }

    /// <summary>「放出来」那一发的第一路：<c>ShowWindow</c> 发
    /// <c>SW_SHOWNOACTIVATE</c>，并把它发之前那一扇的可见状态读回来。
    ///
    /// <para><b>放的那一条不许是 <c>SW_SHOW</c></b>：那一条会激活日历、抢前台 ——
    /// 契约「无感」第 4 条明写它不抢前台（<c>.scratch/keeper-contract/spec.md</c>）。
    /// 差分那一发量过：换成 <c>SW_SHOW</c> 也一样放不出来（可见那位一位没动），
    /// 而它多一个抢前台的坏处 —— 2026-10-16。</para>
    ///
    /// <para><b>读回来的是状态，不是成功</b>：<c>ShowWindow</c> 答真不答假，
    /// 所以「发出去了没有」和「有没有生效」在这条上分不开 —— 判据落在读回来的状态上。</para></summary>
    private string ShowAgain()
    {
        bool before = Win32.IsWindowVisible(_hwnd);
        Win32.ShowWindow(_hwnd, ShowAgainCommands[^1]);
        return $"放出来那一条发之前={(before ? "看得见" : "藏着")} 发之后={Win32.IsWindowVisible(_hwnd)}";
    }

    /// <summary>编辑态：把那一扇<b>从图标层摘下来</b>，排成顶层窗，给键盘焦点。
    ///
    /// <para><b>抬的是日历那一扇，不是引擎那一扇</b> —— 人看见的是它，
    /// 而引擎那一扇整个编辑期都是藏着的（<c>Park</c> 那一发 <c>SW_HIDE</c>）。
    /// 抬错那一扇的后果实测到了：键盘焦点落在隐藏窗上，一个字也进不来。</para>
    ///
    /// <para><b>像素照旧推进</b>：<c>UpdateLayeredWindow</c> 对顶层窗一样成立，
    /// 而 <see cref="Push"/> 的坐标一直是屏幕坐标，所以抬落期间那一块不会黑。</para></summary>
    public string Lift(Win32.Rect where)
    {
        if (!Attached)
        {
            _lastMeasurement = "日历那一扇不在，抬起这一发没做（引擎那一扇一直藏着，不抬它）";
            return _lastMeasurement;
        }

        // 摘下来：**先改样式、再换爹**，次序和 <see cref="Attach"/> 那一条一样
        // （那个次序是量出来的，见 `docs/research/desktop-layer-and-window-measurements.md`）。
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlStyle, (IntPtr)StyleForLift(Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle).ToInt64()));
        Win32.SetParent(_hwnd, IntPtr.Zero);
        // 扩展样式：**WS_EX_LAYERED 要留着**（摘了推不进像素），而那两位要去掉 ——
        // WS_EX_TRANSPARENT 让鼠标穿过去，WS_EX_NOACTIVATE 让它永远拿不到键盘焦点。
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, (IntPtr)ExStyleForEditing(Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle).ToInt64()));
        // 顶层窗的坐标就是屏幕坐标，换算那一步不用了（它在 <see cref="Attach"/> 里，那是子窗才要的）。
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, where.Left, where.Top, where.Width, where.Height,
            Win32.SwpFrameChanged | Win32.SwpNoActivate);
        Win32.ShowWindow(_hwnd, Win32.SwShowNoActivate);

        _lifted = true;
        _placed = where;
        Win32.GetWindowRect(_hwnd, out var now);
        _lastMeasurement =
            $"抬到顶层：爹=0x{Win32.GetParent(_hwnd):X}（抬起就该是 0）矩形={Describe(now)} " +
            $"可见={Win32.IsWindowVisible(_hwnd)} " +
            $"样式={Win32.DescribeStyle(Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle))} " +
            $"扩展样式={Win32.DescribeExStyle(Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle))}";
        return _lastMeasurement;
    }

    /// <summary>编辑结束：按<b>和抬起同一套动作</b>挂回图标层、排 <c>SysListView32</c> 之下。
    ///
    /// 走的是 <see cref="Attach"/> 那一条（它重新找宿主），所以落回去之后看门狗照旧工作 ——
    /// 宿主变了会重挂，找的是新一扇。</summary>
    public string SetDown(Win32.Rect where) => $"落回图标层：{Attach(where)}";

    /// <summary>编辑态抬起的是哪一扇。<b>日历那一扇</b>；它不在就一个都不抬 ——
    /// 引擎那一扇一直藏着，抬它等于把键盘焦点给一扇看不见的窗。纯算术，自检拧得到。</summary>
    public static nint EditingWindow(nint engineWindow, nint nativeWindow) =>
        nativeWindow != IntPtr.Zero ? nativeWindow : IntPtr.Zero;

    /// <summary>抬起那一发的样式：<b>WS_CHILD 摘掉、WS_POPUP 补上</b>。
    /// 一扇 <c>WS_CHILD</c> 永远拿不到键盘焦点（契约「无感」第 2 条）。</summary>
    public static long StyleForLift(long style) => (style & ~Win32.WsChild) | Win32.WsPopup;

    /// <summary>落回去那一发的样式：<see cref="StyleForLift"/> 反着来。</summary>
    public static long StyleForSetDown(long style) => (style | Win32.WsChild) & ~Win32.WsPopup;

    /// <summary>编辑态的扩展样式：<b><c>WS_EX_LAYERED</c> 留着</b>（摘了推不进像素），
    /// <c>WS_EX_TRANSPARENT</c> 和 <c>WS_EX_NOACTIVATE</c> 去掉（鼠标要穿得进来、焦点要落得下）。</summary>
    public static long ExStyleForEditing(long ex) =>
        ex & ~(Win32.WsExTransparent | Win32.WsExNoActivate);

    /// <summary>摆着那一档的扩展样式：把编辑态摘掉那两位补回来。</summary>
    public static long ExStyleForParked(long ex) =>
        ex | (Win32.WsExTransparent | Win32.WsExNoActivate);

    /// <summary>把一块像素推进去。<b>只在画面真变了的时候调</b> —— 读回是同步的，
    /// 每帧做一次就是每帧卡一次。
    /// <para><b>返回 <see cref="PushOutcome"/>（卡片 03 · S24）</b>：成败走枚举字段，
    /// 那一行实测结果逐字走 <see cref="PushOutcome.Note"/>。</para></summary>
    public PushOutcome Push(byte[] bgra, int width, int height, Win32.Rect where)
    {
        if (!Attached)
        {
            _lastMeasurement = "还没挂上，这一发没有像素推进去（日历那一块会是黑的）";
            return new PushOutcome(false, false, _lastMeasurement);
        }

        if (bgra.Length < width * height * 4)
        {
            _lastMeasurement = $"读回来的字节数不够（{bgra.Length} < {width * height * 4}），这一发没有推进去";
            return new PushOutcome(true, false, _lastMeasurement);
        }

        // **尺寸或位置变了要重建**，不能拿老的推：UpdateLayeredWindow 的 psize 说的是
        // 源位图多大，拿老尺寸推出去的是错的那一块。
        if (where.Width != _placed.Width || where.Height != _placed.Height)
        {
            if (_lifted)
            {
                // 抬起那一发它是顶层窗：**坐标就是屏幕坐标，不换算、也不排在图标层之下**。
                Win32.SetWindowPos(_hwnd, IntPtr.Zero, where.Left, where.Top, where.Width, where.Height,
                    Win32.SwpFrameChanged | Win32.SwpNoActivate | Win32.SwpNoZorder);
            }
            else
            {
                var host = DesktopLayer.CalendarHost();
                var client = new Win32.Point { X = where.Left, Y = where.Top };
                Win32.ScreenToClient(host.DefView == IntPtr.Zero ? _defView : host.DefView, ref client);
                Win32.SetWindowPos(_hwnd, _iconList, client.X, client.Y, where.Width, where.Height,
                    Win32.SwpFrameChanged | Win32.SwpNoActivate);
            }
            _placed = where;
        }

        (bool ok, string note) = Update(bgra, width, height, where);
        _lastMeasurement = $"推进去 {width}x{height}：{note} 矩形={Describe(_placed)}";
        return new PushOutcome(true, ok, _lastMeasurement);
    }

    private (bool Ok, string Note) Update(byte[] bgra, int width, int height, Win32.Rect where)
    {
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr memory = Win32.CreateCompatibleDC(screen);
        // BITMAPINFO 用 Win32 那一份形状（头 + 空表：32 位 BI_RGB 的表本来就是空的）。
        var info = new Win32.BitmapInfo
        {
            Header = new Win32.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<Win32.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr bitmap = Win32.CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            Win32.DeleteDC(memory);
            Win32.ReleaseDC(IntPtr.Zero, screen);
            return (false, $"造不出 DIB，错误码 {Marshal.GetLastWin32Error()}");
        }

        IntPtr previous = IntPtr.Zero;
        try
        {
            previous = Win32.SelectObject(memory, bitmap);
            Marshal.Copy(bgra, 0, bits, Math.Min(bgra.Length, width * height * 4));

            var dest = new Win32.Point { X = where.Left, Y = where.Top };
            var source = new Win32.Point { X = 0, Y = 0 };
            var size = new Win32.Size { Width = width, Height = height };
            // **blend 三个字段都要答对，答错就成不透明**：源图是 32 位带 alpha 的 DIB，
            // 所以 BlendFlags 和 AlphaFormat 都填 AC_SRC_ALPHA（1），ConstantAlpha 只在被
            // BlendFlags 忽略时才起作用（0xFF 不改）。全 0 那一次推进出去的是一整块纯黑。
            // 这一份由 PushBlend 交出来，自检拧的是同一个函数（见 BlendTellsSourceAlpha）。
            var blend = PushBlend();

            // **destDc 传 0**。这一位是量出来的：传屏的 DC 那一档读 0 个像素变，
            // 传 0 才画得出来（2026-10-15，对照那一发，见 docs/research/ 那一节）。
            bool ok = Win32.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dest, ref size,
                memory, ref source, 0, ref blend, Win32.UlwAlpha);
            return (ok, ok ? "答真" : $"**答假**，错误码 {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            if (previous != IntPtr.Zero) Win32.SelectObject(memory, previous);
            Win32.DeleteObject(bitmap);
            Win32.DeleteDC(memory);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>看门狗：壳拆了重搭之后自己挂回去。<b>宿主没变就一个字节都不动。</b>
    /// 这一发不许省 —— 睡眠唤醒、换分辨率、换 DPI 之后父子关系会断，
    /// 只做一次的话那一扇就再也回不来。
    ///
    /// <para><b>抬起那一发不重挂</b>：那时它是顶层窗、不在图标层里，
    /// 挂回去等于把正在编辑的那一块按回壁纸底下。<b>落回去之后照旧</b> ——
    /// <see cref="SetDown"/> 走的就是 <see cref="Attach"/>，宿主变了会重挂。</para>
    ///
    /// <para><b>返回 <see cref="WatchResult"/>（卡片 03 · S24）</b>：分类走 <see cref="WatchKind"/>，
    /// 那一行实测结果逐字走 <see cref="WatchResult.Note"/>。</para></summary>
    public WatchResult Watch()
    {
        if (!Attached) return new WatchResult(WatchKind.NotAttached, "没挂着（这一扇已经被拆掉了）");
        if (_lifted) return new WatchResult(WatchKind.Lifted, "抬着（编辑态），这一发不重挂");
        var host = DesktopLayer.CalendarHost();
        bool same = host.DefView == _defView && host.IconList == _iconList;
        if (same && Win32.IsWindowVisible(_hwnd)) return new WatchResult(WatchKind.NoMove, "宿主没变，不用动");

        if (host.DefView == IntPtr.Zero)
            return new WatchResult(WatchKind.HostMissing, $"壳拆了重搭而图标层还没出来（{host.Measurement}），这一发没重挂");

        var moved = Attach(_placed);
        return new WatchResult(WatchKind.Reattached, $"重挂：{moved}");
    }

    /// <summary>把日历那一扇还回去。<b>借来的那一扇不拆，只还原</b> ——
    /// 它是壳的窗，<c>DestroyWindow</c> 一发就是拆掉壳的东西，
    /// 而判据在 <see cref="ShouldDestroyOnRelease"/>。</summary>
    public string Detach()
    {
        if (_hwnd == IntPtr.Zero) return "本来就没有挂着";

        Borrowed? before = _borrowed;
        bool borrowed = before is not null;

        if (ShouldDestroyOnRelease(borrowed))
        {
            Win32.ShowWindow(_hwnd, Win32.SwHide);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _borrowed = null;
            _lastMeasurement = "拆掉了自己造的那一扇";
            return _lastMeasurement;
        }

        // 还的次序：**先藏 → 再还扩展样式 → 再还矩形和 z 序 → 原来看得见才放出来**。
        // 扩展样式要在放出来之前还回去 —— 带着 `WS_EX_LAYERED` 放出来的那一下，
        // 壳会拿层叠面去合成一扇本来不画东西的窗。
        Win32.ShowWindow(_hwnd, Win32.SwHide);
        long exBefore = Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, (IntPtr)before!.ExStyle).ToInt64();
        // **窗口样式也要还**（在换爹之前）：抬起那一发把它改成了 `WS_POPUP`。
        // 漏了这一位，壳那一扇就留在顶层当浮窗 —— 那一扇是壳的，不是我们的。
        long styleBefore = Win32.SetWindowLongPtr(_hwnd, Win32.GwlStyle, (IntPtr)before.Style).ToInt64();
        Win32.SetParent(_hwnd, before.Parent);
        bool placed = Win32.SetWindowPos(_hwnd, before.Below, before.Rect.Left, before.Rect.Top,
            before.Rect.Width, before.Rect.Height, Win32.SwpFrameChanged | Win32.SwpNoActivate);
        if (before.Visible) Win32.ShowWindow(_hwnd, Win32.SwShowNoActivate);

        // **读回来对一遍**：还回去的是不是借之前那一份，不拿 SetWindowPos 的返回值当读数。
        Win32.GetWindowRect(_hwnd, out var now);
        long exNow = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExStyle).ToInt64();
        long styleNow = Win32.GetWindowLongPtr(_hwnd, Win32.GwlStyle).ToInt64();
        bool same = exNow == before.ExStyle && Win32.IsWindowVisible(_hwnd) == before.Visible &&
                    styleNow == before.Style;
        _lastMeasurement =
            $"还了借来的那一扇 0x{_hwnd:X}/{DesktopLayer.BorrowedClass}：扩展样式={Win32.DescribeExStyle(new IntPtr(exNow))}" +
            $"（借之前 0x{before.ExStyle:X}，换之前读回 0x{exBefore:X}）" +
            $"矩形={Describe(now)}（借之前={Describe(before.Rect)}）" +
            $"可见={Win32.IsWindowVisible(_hwnd)}（借之前={(before.Visible ? "1" : "0")}）" +
            $"样式=0x{styleNow:X}（借之前 0x{before.Style:X}，换之前读回 0x{styleBefore:X}）" +
            $"爹=0x{Win32.GetParent(_hwnd):X}（借之前 0x{before.Parent:X}）" +
            $"还原矩形和 z 序成功={placed}；{(same ? "样式和可见性跟借之前一样" : "**样式或可见性跟借之前不一样**")}；" +
            "一扇都没有拆";
        _hwnd = IntPtr.Zero;
        _borrowed = null;
        return _lastMeasurement;
    }

    /// <summary>还的时候拆不拆。<b>借来的不拆</b> —— 那一扇是壳的窗，
    /// <c>DestroyWindow</c> 一发就是拆掉壳的东西。<b>纯算术</b>，自检拿真假各拧一遍，
    /// 而生产那一侧（<see cref="Detach"/>）答的就是它。</summary>
    public static bool ShouldDestroyOnRelease(bool borrowedFromShell) => !borrowedFromShell;

    /// <summary>借不借那一扇。<b>纯算术</b>，自检拿真假各拧一遍：
    /// 只借图标层的<b>直接</b>子窗、还活着、而它不是图标列表本身
    ///（<c>EnumChildWindows</c> 数的是全部后代，图标层底下还挂着别的窗）。</summary>
    public static bool Borrowable(bool directChild, bool alive, bool isIconList) =>
        directChild && alive && !isIconList;

    /// <summary>已经借过的那一扇还活着就接着用，句柄烂了才重借。<b>纯算术</b>。</summary>
    public static bool ReuseBorrowed(bool alreadyBorrowed, bool alive) => alreadyBorrowed && alive;

    /// <summary>类名，<b>读不到回空串</b> —— 拿空串去比类名名单，比不中就是「不是那一扇」，
    /// 不兜底、不猜。声明只住 <see cref="Win32"/> 一份。</summary>
    private static string ClassOf(IntPtr handle)
    {
        var text = new System.Text.StringBuilder(256);
        int n = Win32.GetClassName(handle, text, 256);
        return n <= 0 ? string.Empty : text.ToString(0, Math.Min(n, 255));
    }

    /// <summary>这一扇排在图标列表的哪一侧。<b>方向是量出来的</b>：
    /// <c>GW_HWNDPREV</c> 往上一扇 = 上面。</summary>
    private static string RankAgainst(IntPtr hwnd, IntPtr iconList)
    {
        if (iconList == IntPtr.Zero) return "图标层读不出来，排不了";
        var at = Win32.GetWindow(hwnd, Win32.GwHwndPrev);
        for (var steps = 0; at != IntPtr.Zero && steps < 64; steps++)
        {
            if (at == iconList) return $"在图标列表之下（往上第 {steps + 1} 扇撞上它 —— 图标压在日历上面）";
            at = Win32.GetWindow(at, Win32.GwHwndPrev);
        }
        return "**往上走没撞上图标列表 —— 排错了**";
    }

    private static string Describe(Win32.Rect rect) =>
        $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) {rect.Width}x{rect.Height}";

    // ---- 借窗 -----------------------------------------------------------------

    /// <summary>借壳自己那一扇：<b>图标层的直接子窗里那个 <c>SysHeader32</c></b>。
    ///
    /// <para><b>借之前先把它的样子逐项读下来</b>（扩展样式、矩形、可见性、爹、z 序邻居）——
    /// 还的时候照着还，而拆不拆也由「它是借来的」这一位答（<see cref="ShouldDestroyOnRelease"/>）。
    /// <b>那一扇是壳的窗</b>：<c>DestroyWindow</c> 一发就是拆掉壳的东西。</para>
    ///
    /// <para><b>为什么借而不自己造</b>：理由和那四条读数在类注释里。自建的窗挂进图标层之后
    /// <c>UpdateLayeredWindow</c> 答真而屏上 0 像素变，借来的那一扇同一套动作四发全过 ——
    /// <b>差别只剩一条：那一扇是壳自己造的</b>。</para>
    ///
    /// <para><b>借过的那一扇还活着就接着用</b>（<see cref="ReuseBorrowed"/>）——
    /// 句柄烂了（睡眠唤醒、explorer 重启）才去重借。</para></summary>
    private bool Borrow(DesktopLayer.Host host, out string why)
    {
        why = "";

        if (ReuseBorrowed(_hwnd != IntPtr.Zero, _hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd)))
        {
            why = "借过的那一扇还活着，接着用";
            return true;
        }

        var seen = new List<string>();
        IntPtr picked = IntPtr.Zero;
        // **EnumChildWindows 数的是全部后代**（图标层底下还挂着别的窗），所以每扇都自己看一遍：
        // 爹就是图标层（直接子窗）、还活着、不是图标列表本身 —— 三样都答真才借（判据 Borrowable）。
        Win32.EnumChildWindows(host.DefView, (hwnd, _) =>
        {
            if (ClassOf(hwnd) != DesktopLayer.BorrowedClass) return true;
            IntPtr parent = Win32.GetParent(hwnd);
            // **挂在图标层底下就算**：图标层的直接子窗，或者图标列表的子窗
            //（2026-10-16 explorer 重启之后那一扇换了个爹，本机现在量到的是后者）。
            bool direct = parent == host.DefView || parent == host.IconList;
            bool alive = Win32.IsWindow(hwnd);
            bool iconList = hwnd == host.IconList;
            seen.Add($"0x{hwnd:X}（直接子窗={(direct ? "是" : "否")} 活着={(alive ? "是" : "否")} " +
                     $"图标列表={(iconList ? "是" : "否")}）");
            if (picked == IntPtr.Zero && Borrowable(direct, alive, iconList)) picked = hwnd;
            return true;
        }, IntPtr.Zero);

        if (picked == IntPtr.Zero)
        {
            why = $"图标层 0x{host.DefView:X} 底下类名是 {DesktopLayer.BorrowedClass} 的窗有 {seen.Count} 扇：" +
                  $"{(seen.Count == 0 ? "一扇也没有" : string.Join("、", seen))}，" +
                  "三样都答真的（直接子窗 + 活着 + 不是图标列表）零扇";
            return false;
        }

        Win32.GetWindowRect(picked, out var rect);
        _borrowed = new Borrowed(picked, Win32.GetWindowLongPtr(picked, Win32.GwlExStyle).ToInt64(), rect,
            Win32.IsWindowVisible(picked), Win32.GetParent(picked),
            Win32.GetWindow(picked, Win32.GwHwndPrev),
            Win32.GetWindowLongPtr(picked, Win32.GwlStyle).ToInt64());
        _hwnd = picked;
        return true;
    }

    // ---- 自检要拧的那几处（纯算术，不碰窗） ------------------------------------

    /// <summary>这一组在自检里登��的那两句话（绿话/红话）。判据是 <see cref="SelfTest"/>。</summary>
    internal static readonly CriterionOf Criterion = new(
        () => "日历那一扇（借壳自己的 SysHeader32）那一族通过：宿主换了/窗被拆了/那一扇不可见都认得出、" +
              "宿主齐了才重挂、宿主没变不重挂、到点就补推（宿主没变、画面也没变也推：壳重画会把那张面丢掉）、" +
              "到点那一发连着重新挂（只推进去救不回来，而宿主没变正是那一档）、" +
              "挪之前先藏（挪那一发打在壳那一侧线程上，还没落地就推进去会落在旧位置）、" +
              "放出来那一发只在藏着的时候发（对已经看得见的窗发「放出来」是空动作）、" +
              "放的那一条不激活（不许抢前台）、" +
              "不到点不补推、画面没变不读回（读回是同步的，每帧做一次就是每帧卡一次）、" +
              "补推累计按分钟记一行（成功不记日志，这一行是跑没跑唯一的凭据）、" +
              "尺寸变了要重建、一个像素都没推进去不答「看得见」、" +
              "只借图标层的直接子窗、活着、且不是图标列表本身、借过的那一扇还活着就接着用、" +
              "借来的那一扇只还不拆（拆的是壳的窗）、" +
              "编辑态抬的是那一扇（引擎那一扇一直藏着）、抬起摘掉 WS_CHILD 补上 WS_POPUP、" +
              "抬起留着 WS_EX_LAYERED 而摘掉那两位（穿鼠标/不激活）、" +
              "推进去那份 blend 让源图自己的 alpha 参与（否则格子里那层半透的黑落成一块黑板）",
        "日历那一扇", SelfTest);

    /// <summary>宿主变了没有 —— 看门狗的判据本体。<b>纯算术</b>，自检拿假句柄各拧一遍。
    ///
    /// <para><b>三种都算「变了」</b>：图标层换了一扇、那一扇被藏起来了、
    /// <b>而那一扇已经被拆掉了</b>（句柄烂了 —— 睡眠唤醒、explorer 重启之后就是它）。
    /// 第三种最容易漏：它和「宿主没变」在别的代码里长得一模一样，
    /// 而漏了它的那一次，日历就再也回不来。</para>
    ///
    /// <para><b>这一问只管「重挂」</b>：宿主没变就一个字节都不动。
    /// <b>到点补推是另一发</b>（<see cref="ShouldRepush"/>，<c>CalendarHost.HostTick</c> 里那一段）——
    /// 宿主没变、那一扇还看得见，壳照样会把层叠子窗缓存的那张面丢掉。
    /// 别把那一发删了当「看门狗已经覆盖」。</para></summary>
    public static bool HostChanged(bool attached, IntPtr wasDefView, IntPtr nowDefView,
        bool nowVisible, bool windowAlive) =>
        attached && (!windowAlive || !nowVisible || wasDefView != nowDefView);

    /// <summary>这次该不该重挂。<b>和 <see cref="HostChanged"/> 分开</b>：宿主变了要重挂，
    /// 而宿主压根还没出来（拆了重搭、图标层还没长回来）时<b>不重挂</b> ——
    /// 重挂到一个空的宿主上是把日历钉在一个空壳里。</summary>
    public static bool ShouldReattach(bool hostChanged, IntPtr nowDefView, IntPtr nowIconList) =>
        hostChanged && nowDefView != IntPtr.Zero && nowIconList != IntPtr.Zero;

    /// <summary>读回要不要做。<b>只在画面真变了的时候</b> —— 读回是同步的。</summary>
    public static bool ShouldReadBack(bool pixelsChanged, bool sizeChanged) =>
        pixelsChanged || sizeChanged;

    /// <summary>到点要不要补推一次 —— <b>补推的判据本体，纯算术</b>，自检拿假值各拧一遍
    /// （不碰窗、不读像素）。
    ///
    /// <para><b>为什么不在 <see cref="HostChanged"/> 那一问里顺带答</b>：宿主没变、
    /// 那一扇还看得见、日历内容一个字没改 —— 壳照样可能把层叠子窗缓存的那张面丢掉。
    /// <c>Win+D</c> 让壳重画桌面就是这一档（读数在
    /// <c>docs/research/desktop-layer-and-window-measurements.md</c>
    /// 「层叠子窗只推一次」那一节）。拿「宿主没变」当「不用推」，那一块就一直空着。</para>
    ///
    /// <para><b>判据里没有宿主那一位，这是有意的</b>：它钉的就是「宿主没变、到点也推」。
    /// 另一头「不到点不推」由 <c>sincePush &lt; periodSeconds</c> 那一格钉住 ——
    /// 不到点就推，等于回到每帧读回。</para>
    ///
    /// <para><paramref name="attached"/> 答假时不补推：没有接收方，推进去的那一行只会是
    /// 「还没挂上」，白写一行日志。</para>
    ///
    /// <para><b>这一问只答「什么时候」</b>：到点之后做哪几件是
    /// <see cref="ShouldRepushReattach"/> 那一问（重新挂 + 推进去，不是只推进去）。</para></summary>
    public static bool ShouldRepush(bool attached, double sincePush, int periodSeconds) =>
        attached && sincePush >= periodSeconds;

    /// <summary>到点那一发要不要**重新挂**那一扇 —— 判据本体，纯算术，自检拿假句柄各拧一遍
    /// （不碰窗、不读像素）。
    ///
    /// <para><b>答案和宿主变没变无关，这是有意的</b>：宿主没变、那一扇还看得见、矩形也对，
    /// 壳照样会把层叠子窗缓存的那张面丢掉（<c>Win+D</c> 就是这一档）。
    /// <b>拿 <see cref="HostChanged"/> 答真当「不用重挂」，那一块就一直空着</b> ——
    /// 真机读数：22 秒里补推四发以上，日历那一块一直是 153.15（没有日历）。
    /// 读数在 <c>docs/research/desktop-layer-and-window-measurements.md</c>「层叠子窗只推一次」那一节。</para>
    ///
    /// <para><b>答假的唯一一档</b>是「那一扇已经被拆掉了」：没有接收方，
    /// 重新挂和推进去都只是白动 —— 那种情况归 <see cref="Watch"/>。</para></summary>
    public static bool ShouldRepushReattach(bool attached, bool hostChanged) => attached;

    /// <summary>到点补推累计到第几发记一行。<b>判据本体，纯算术</b>：第一发记，
    /// 之后每 <see cref="RepushLogEvery"/> 发记一次 —— 成功那一路不记日志，
    /// 这一行是「补推有没有在跑」在产品里唯一的凭据。</summary>
    public static bool ShouldLogRepush(int repushes) => repushes % RepushLogEvery == 1;

    /// <summary>「日历看得见」那一件现在归谁。<b>这一族答的是像素推进去没有</b>，
    /// 不是「引擎画了没有」—— 以前引擎画就一定看得见，现在原生窗不出像素就是黑的。</summary>
    public static bool CalendarIsVisible(bool attached, bool pushed, int pixelsReceived) =>
        attached && pushed && pixelsReceived > 0;

    public static List<string> SelfTest()
    {
        var bad = new List<string>();
        if (HostChanged(false, (nint)0x100, (nint)0x200, true, true))
            bad.Add("没挂着却答「宿主变了」");
        if (HostChanged(true, (nint)0x100, (nint)0x100, false, true) == false)
            bad.Add("宿主没变而那一扇不可见了，没有认出来");
        if (HostChanged(true, (nint)0x100, (nint)0x100, true, true))
            bad.Add("宿主没变、可见、窗还在，却答「变了」");
        if (HostChanged(true, (nint)0x100, (nint)0x200, true, true) == false)
            bad.Add("图标层换了一扇，没认出来");
        if (HostChanged(true, (nint)0x100, (nint)0x100, true, false) == false)
            bad.Add("窗已经被拆掉了，没认出来");

        if (ShouldReattach(true, IntPtr.Zero, (nint)0x200))
            bad.Add("宿主还没出来却要重挂 —— 那会把日历钉进一个空壳");
        if (ShouldReattach(true, (nint)0x100, IntPtr.Zero))
            bad.Add("图标层读不出来却要重挂");
        if (ShouldReattach(true, (nint)0x100, (nint)0x200) == false)
            bad.Add("宿主齐了却不重挂");
        if (ShouldReattach(false, (nint)0x100, (nint)0x200))
            bad.Add("宿主没变却要重挂 —— 那是每秒白动一次");

        if (ShouldReadBack(false, false))
            bad.Add("画面没变、尺寸没变，却读回 —— 每帧卡一次");
        if (ShouldReadBack(true, false) == false)
            bad.Add("画面变了却不读回");
        if (ShouldReadBack(false, true) == false)
            bad.Add("尺寸变了却不读回（拿老 DIB 推出去的是错的那一块）");

        // **推进去那份 blend 要让源图自己的 alpha 参与**。这一格量具量不到 ——
        // `run.cmd layered` 推的是不透明纯色，硬边和按 alpha 混对它没有分别，
        // 所以「19200 / 19200 个像素变」答的是「推进去了」，不是「alpha 被认了」。
        // 真机读到的是屏上 57% 的像素 (0,0,0)，而推进去的字节里 alpha=94 占了 76 万颗。
        var blend = PushBlend();
        if (!BlendTellsSourceAlpha(blend.Operation, blend.Flags, blend.ConstantAlpha, blend.AlphaFormat))
            bad.Add("推进去那份 blend 没让源图自己的 alpha 参与 —— 整块按不透明画，"
                  + "格子里那层半透的黑落成一块黑板（2026-10-17 实测）");

        // **到点补推**：宿主没变、画面也没变，壳照样可能把层叠子窗缓存的那张面丢掉
        // （`Win+D` 让壳重画桌面就是这一档）。所以到点就推，不问画面变没变。
        // **判据里压根没有宿主那一位** —— 这就是「宿主没变、到点也推」钉住的方式。
        //
        // 拿生产那一个周期本身来拧，不在自检里抄一份数字：常数改了这两格跟着改。
        if (ShouldRepush(true, RepushSeconds, RepushSeconds) == false)
            bad.Add("到点了却不补推 —— 壳重画之后那张面没了，而内容没变就永远没人补推，" +
                    "日历一直空到有人翻月、选日或者过午夜");
        if (ShouldRepush(true, RepushSeconds - 0.001, RepushSeconds))
            bad.Add("还没到点就补推 —— 读回是同步的，每秒一次就是每秒卡一次");
        if (ShouldRepush(true, 0, RepushSeconds))
            bad.Add("刚推过就又补推 —— 那和每帧读回是一件事");
        if (ShouldRepush(false, RepushSeconds, RepushSeconds))
            bad.Add("那一扇没挂着却补推 —— 没有接收方，推进去的那一行只是白写");

        // **到点那一发是「重新挂 + 推进去」，不是只推进去**。只推进去救不回来：
        // 真机读到 `MinimizeAll` 之后 22 秒里补推四发以上（每一发都答真），
        // 屏上一直是 153.15（没有日历）。**这一格钉的是「宿主没变也照样走」** ——
        // 宿主没变恰恰是那一档的常态：宿主一块没动，DWM 手里那张面被壳重画丢了。
        // 宿主变了当然更要走。
        if (ShouldRepushReattach(true, false) == false)
            bad.Add("宿主没变却不重新挂 —— 只推进去救不回被壳丢掉的那张面，" +
                    "而宿主没变恰恰是那一档的常态（22 秒里补推四发以上，屏上一直是 153.15）");
        if (ShouldRepushReattach(true, true) == false)
            bad.Add("宿主换了却不重新挂");
        if (ShouldRepushReattach(false, true) || ShouldRepushReattach(false, false))
            bad.Add("那一扇已经被拆掉了却要重新挂 —— 没有接收方");

        // **补推累计的那一行按分钟来**：成功那一路不记日志，这一行是
        // 「补推有没有在跑」在产品里唯一的凭据 —— 没有它，跑过和没跑过长得一模一样。
        if (ShouldLogRepush(1) == false) bad.Add("补推头一发不记 —— 刚起产品那一次没人看得出跑没跑");
        if (ShouldLogRepush(2)) bad.Add("补推第二发就记 —— 这一行是按分钟来的，一天一万多行会把日志淹掉");
        if (ShouldLogRepush(RepushLogEvery + 1) == false)
            bad.Add("补推记日志的间隔不对 —— 这一行按分钟来");
        // **抄成局部变量再比**：两个常量直接比会被编译器折成 false，
        // 于是这一格报 CS0162「无法访问的代码」—— 和上面那一格一样的坑。
        var logEvery = RepushLogEvery;
        var repushSeconds = RepushSeconds;
        if (logEvery * repushSeconds != 60)
            bad.Add("补推记日志那一行不是每分钟一次（周期和计数对不上）");

        // 补推要比重挂那一族密。**抄成局部变量再比**：两个常量直接比会被编译器折成
        // false，于是这一格报 CS0162「无法访问的代码」。
        var repush = RepushSeconds;
        var watchdog = WatchdogSeconds;
        if (repush >= watchdog)
            bad.Add("补推的周期不比重挂那一族短 —— 空白会停满一个看门狗周期，" +
                    "而看门狗只在宿主变了的那一次才动");

        // **放出来那一发：只发一条，而且那一条不激活、也不先藏。**
        //
        // **先藏那一格是产品自己加的**（为了救 `Win+D` 之后那一档），而量具借窗那一族
        // 没有这一格 —— 那一扇本来就藏着，它从来没有过「已经看得见还要藏回去」的时候。
        // 真机读到：先藏再放那一族推进去答真、屏上 0 像素变（2026-10-16）。
        // 少这一格就退回那一发，藏完不放则那一扇从此不出像素。
        //
        // 拿生产自己走的那份来拧（`ShowAgain` 逐条发它），不抄一份在这里。
        var shows = ShowAgainCommands;
        if (shows.Count != 1 || shows[0] != Win32.SwShowNoActivate)
            bad.Add("放出来那一发不是单独一条「放出来、不激活」—— 量具借窗那一族逐字是那一条，" +
                    "而产品原来多发的那一条 SW_HIDE 把层叠面丢了（推进去答真、屏上 0 像素变）");
        // **不许发 SW_HIDE**：藏完要靠下一发放回来，而那一发是 5 秒一次。
        for (var i = 0; i < shows.Count; i++)
            if (shows[i] == Win32.SwHide)
            bad.Add("放出来那一发发了 SW_HIDE —— 藏完要等下一发才放得回来，" +
                    "而藏那一下把层叠面丢了（2026-10-16 真机读数）");
        // **不许发 SW_SHOW**：那一条会激活日历、抢前台（契约「无感」第 4 条）。
        foreach (var show in shows)
            if (show == Win32.SwShow)
                bad.Add("放出来那一发发了 SW_SHOW —— 它会把日历激活、抢前台，" +
                        "契约「无感」第 4 条明写它不抢前台");

        if (CalendarIsVisible(true, true, 0))
            bad.Add("一个像素都没推进去却答「日历看得见」");
        if (CalendarIsVisible(false, true, 100))
            bad.Add("没挂着却答「日历看得见」");
        if (CalendarIsVisible(true, false, 100))
            bad.Add("没推进去却答「日历看得见」");
        if (CalendarIsVisible(true, true, 100) == false)
            bad.Add("挂着、推进去了、收到像素了，却答「看不见」");

        // **挪之前先藏**：只有「真的要挪、而它此刻看得见」这一档先藏。
        // 少一格就回到「对着看得见的一扇挪完立刻推进去」那一档 —— 那一挪打在壳那一侧线程上，
        // 还没落地，推就把层叠面落在旧位置，之后每一发都落空（2026-10-16 真机，0 像素变）。
        if (HideBeforeMove(true, true) == false)
            bad.Add("真的要挪而它看得见，却不先藏 —— 那一挪还没在壳那一侧落地，" +
                    "紧接着推进去就把层叠面落在旧位置，之后每一发都落空");
        if (HideBeforeMove(true, false))
            bad.Add("它本来就藏着，却还发一条 SW_HIDE —— 多发的那一条是把量具量过的次序打乱");
        if (HideBeforeMove(false, true))
            bad.Add("不用挪却把那一扇藏了 —— 挪不动的那几发全是空动作，白白让层叠面丢一次");

        // **放出来那一发只在它藏着的时候发**：对已经看得见的窗发「放出来」是空动作。
        if (ShouldShow(true))
            bad.Add("那一扇本来就看得见，却还发「放出来」—— 那是空动作，可见状态一位没动");
        if (ShouldShow(false) == false)
            bad.Add("那一扇藏着却不放出来 —— 日历从此不出像素");

        // **借来的那一扇**：只借图标层的直接子窗、还活着、不是图标列表本身。
        // 少一格就借错 —— 借错就是把壳的东西挪走，而图标列表那一扇挪走之后图标就没了。
        if (Borrowable(false, true, false))
            bad.Add("不是图标层的直接子窗也借 —— EnumChildWindows 数的是全部后代，" +
                    "借错那一扇就是把壳的东西挪走");
        if (Borrowable(true, false, false))
            bad.Add("句柄已经烂了还借 —— 睡眠唤醒、explorer 重启之后就是这样");
        if (Borrowable(true, true, true))
            bad.Add("把图标列表本身借走了 —— 那一扇是桌面图标，挪走它图标就没了");
        if (Borrowable(true, true, false) == false)
            bad.Add("图标层的直接子窗、活着、不是图标列表，却借不到 —— 借不到就没有日历");

        // **借过的那一扇还活着就接着用**：重借就是再找一扇壳的窗挪走，而句柄烂了还接着用
        // 就是往一个不存在的窗上推像素。
        if (ReuseBorrowed(false, true))
            bad.Add("没借过却答「接着用借过的那一扇」—— 句柄是零，样样都不对");
        if (ReuseBorrowed(true, false))
            bad.Add("借过的那一扇句柄已经烂了却还接着用 —— 往一个不存在的窗上推像素");
        if (ReuseBorrowed(true, true) == false)
            bad.Add("借来的那一扇还活着却要重借 —— 重借就是再挪一扇壳的窗");

        // **借来的那一扇只还不拆**：那一扇是壳的窗，DestroyWindow 一发就是拆掉壳的东西。
        if (ShouldDestroyOnRelease(true))
            bad.Add("还的时候要拆掉借来的那一扇 —— 那是壳的窗，拆了就是拆掉壳的东西");

        // **编辑态抬的是日历那一扇**。引擎那一扇一直藏着（Park 那一发 SW_HIDE），
        // 抬它等于把键盘焦点给一扇看不见的窗 —— 人看见的是日历那一扇。
        if (EditingWindow((nint)0x100, (nint)0x200) != (nint)0x200)
            bad.Add("编辑态抬的是引擎那一扇 —— 人看见的是日历那一扇，抬错的那一扇一直藏着");
        if (EditingWindow((nint)0x100, IntPtr.Zero) != IntPtr.Zero)
            bad.Add("日历那一扇不在却还去抬引擎那一扇 —— 那一扇是藏着的，抬了焦点也落不进去");

        // 抬起 = WS_CHILD 摘掉、WS_POPUP 补上：一扇 WS_CHILD 永远拿不到键盘焦点。
        const long popup = 0x80000000, child = 0x40000000;
        if ((StyleForLift(popup | child) & child) != 0)
            bad.Add("抬起那一发还留着 WS_CHILD —— 子窗永远拿不到键盘焦点");
        if ((StyleForLift(child) & popup) == 0)
            bad.Add("抬起那一发没补上 WS_POPUP");
        // 落回去和抬起反着来。
        if ((StyleForSetDown(popup | child) & popup) != 0)
            bad.Add("落回去那一发还留着 WS_POPUP —— 子窗不许是顶层窗");
        if ((StyleForSetDown(popup) & child) == 0)
            bad.Add("落回去那一发没补上 WS_CHILD");

        // 扩展样式：**WS_EX_LAYERED 摘了就推不进像素**（那一块直接黑），
        // 而 WS_EX_TRANSPARENT（鼠标穿过去）和 WS_EX_NOACTIVATE（永远不激活）必须摘掉。
        long parked = Win32.WsExLayered | Win32.WsExTransparent | Win32.WsExNoActivate | Win32.WsExToolwindow;
        long editing = ExStyleForEditing(parked);
        if ((editing & Win32.WsExLayered) == 0)
            bad.Add("抬起那一发把 WS_EX_LAYERED 摘了 —— UpdateLayeredWindow 不成立，那一块是黑的");
        if ((editing & Win32.WsExTransparent) != 0)
            bad.Add("抬起那一发还留着 WS_EX_TRANSPARENT —— 鼠标穿过去，编辑态点不中");
        if ((editing & Win32.WsExNoActivate) != 0)
            bad.Add("抬起那一发还留着 WS_EX_NOACTIVATE —— 它永远拿不到键盘焦点");
        if ((ExStyleForParked(editing) & (Win32.WsExTransparent | Win32.WsExNoActivate)) !=
            (Win32.WsExTransparent | Win32.WsExNoActivate))
            bad.Add("落回去那一发没把穿鼠标/不激活那两位补回来 —— 落回去之后点不中日历");
        return bad;
    }
}
