using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Keeper;

/// <summary>一扇真的设置窗，连同它那个用完就删的临时目录 —— <b>开、挂、放，次序是 interface 的一部分</b>。
///
/// <b>为什么要有这个类</b> —— 2026-10-05 之前，同一段「实例化 <c>Options.tscn</c> →
/// 赋 <see cref="Options.State"/> / <see cref="Options.DataDir"/> /
/// <see cref="Options.Checklist"/> → <c>parent.AddChild</c>」写在<b>十个</b>地方：
/// 九组自检各一份，产品的 <see cref="App"/> 那一侧又一份；而收尾
/// （<c>RemoveChild</c> + <c>QueueFree</c> + 删目录）又各写各的。
///
/// <b>次序是这件事的一半</b>，不是碰巧：<c>_Ready</c> 的第一件事就问
/// <see cref="Options.Live"/>（接线、每一行的文字、那颗钮的菜单都在那里跑），
/// 所以那几样必须在<b>挂上去之前</b>赋好 —— 挂上去以后再补就晚了，而症状是
/// 「窗是空的」或者「某一区压根没建」，界面上看不出是哪一样漏了。
/// 这一句原本在 <c>Options.cs</c> 里出现了三遍，三遍各带一份注释；
/// 现在<b>次序由这一类保证</b>，注释只留这一份。
///
/// <b>收尾只剩一处，而且它<b>会</b>说话</b>：目录删不掉时 <see cref="Problems"/> 里多一条，
/// 那一组就红。**这一条是那些 <c>.tmp\keeper-*-selfcheck-*</c> 孤儿目录换来的** ——
/// 2026-09-30 到 10-01 之间每一跑在临时区里留一个，界面上一个症状都没有
/// （<c>CODING_STANDARDS.md</c>「取证方法论」那一条：实测结果压根没出来）。
/// 当时的修法只落在九处中的一处上，其余八处各留一份内联写法 ——
/// 收拢到这一处之后，删不掉就成了红话，而不是一份没人看的垃圾。
///
/// <b>三步，每步一个名字</b>（自检那一种）：
/// <code>
/// var host = OptionsHost.InTempDir("screen");   // 造目录，Dir 就是这一组的那个
/// File.WriteAllText(Path.Combine(host.Dir, Settings.FileName), ...);
/// var win = host.Open(state, parent);            // 开窗并挂上，次序在这里定死
/// ...量...
/// host.Dispose();                              // 放窗 + 删目录
/// </code>
/// <b>为什么是三步而不是一步</b>：那一组得先往目录里写底稿，窗的 <c>_Ready</c> 读的就是它 ——
/// 这一份顺序是事实，开窗那一刻目录必须已经在（见 <see cref="InTempDir"/> 那段）。
///
/// <b>产品那一种</b>：目录是用户那份配置，<b>本类一个字节都不删</b>，所以
/// <see cref="ForDir"/> 那一支不碰目录、<see cref="Dispose"/> 也只放窗。
/// 而且 <see cref="App"/> 那边<b>故意不放</b> —— 它复用同一扇窗（再按一次是提到前面，
/// 不是重开一扇），那扇窗的生死归 <c>App</c> 自己。
///
/// <b>不数几处调用方</b>：加一组自检只改登记那一行，不改这里任何注释
/// （和「菜单里不数项」「组数不抄在注释里」同一条口径，见 <c>CODING_STANDARDS.md</c>）。</summary>
internal sealed class OptionsHost
{
    /// <summary>临时目录的路径。产品那一种下它是用户那份配置所在的目录，<b>本类不碰它</b>。</summary>
    public string Dir { get; }

    /// <summary>收尾时攒的问题。<b>调用方在 <c>finally</c> 之后把它们并进自己那份</b> ——
    /// 收尾失败不能变成一条静悄悄的垃圾。</summary>
    public List<string> Problems { get; } = [];

    /// <summary>本类是否负责删掉 <see cref="Dir"/>。产品那一种是 false。</summary>
    private readonly bool _ownsDir;

    /// <summary>放过没有。<b>让 <see cref="Dispose"/> 幂等</b>，见那段注释。</summary>
    private bool _disposed;

    /// <summary>开过的那几扇。**一组可能要开不止一扇**（<c>flows-menu</c> 一组两扇，
    /// <c>flows-unreadable</c> 一组按档开好几扇），所以放的时候按这一份清单走。</summary>
    private readonly List<Options> _opened = [];

    private OptionsHost(string dir, bool ownsDir)
    {
        Dir = dir;
        _ownsDir = ownsDir;
    }

    /// <summary>自检那一种：一个<b>只属于这一组</b>的临时目录，放掉时连目录一起没。
    ///
    /// <paramref name="tag"/> 是这一组自己的名字，目录名带上进程号 ——
    /// 两发自检同时跑不会互相删对方的目录，而同一组重跑时上一回的残留先被清掉。
    ///
    /// <b>目录在窗造出来之前就建好，而且这一组得先往里面写底稿</b>：
    /// 窗的 <c>_Ready</c> 读的就是那份配置，所以「目录已经在」是开窗的前提；
    /// 这一组要写的又是同一份文档，于是 <see cref="Dir"/> 和「这一组写东西的那个目录」
    /// <b>字面上是同一个路径</b>，而不是两个各自算出来的。
    ///
    /// <b>它不碰 <c>%APPDATA%</c></b>：目录一律在临时区（<c>CODING_STANDARDS.md</c>
/// 「写进文档的东西」那一节那条规矩）。</summary>
    public static OptionsHost InTempDir(string tag) => new(TempDir(tag), ownsDir: true);

    /// <summary>产品那一种：目录是用户那份配置，<b>本类不删它</b>。
    /// 要放那扇窗的时候调 <see cref="Dispose"/>，或者像 <see cref="App"/> 那样留着复用。</summary>
    public static OptionsHost ForDir(string dir) => new(dir, ownsDir: false);

    /// <summary>开一扇窗、挂到 <paramref name="parent"/> 上；<b>次序在这一处定死</b>。
    ///
    /// <paramref name="wire"/> 在挂上去之前跑，所以它接的那几样钩子（<c>Measurement</c> /
    /// <c>Log</c> / <c>ChecklistChanged</c> / <c>PreviewRequested</c> / <c>Applied</c> /
    /// <c>ForegroundApp</c>）和 <see cref="Options.State"/> 一样属于「<c>_Ready</c> 跑之前」——
    /// 抬头那一行在 <c>_Ready</c> 里已经刷过一次，而 <c>Log</c> 的默认值是空委托
    /// （<c>CODING_STANDARDS.md</c>「取证方法论」那一条记着这一发查了半轮）。
    /// 可空：产品那一侧全都给了，自检里多数只给 <c>Log</c> 或 <c>Measurement</c>。
    ///
    /// <b>窗造不出来就抛</b>，而抛出去那一组会把它记成「这一组自己炸了」——
    /// 那正是它要说的（<c>res://src/Options.tscn</c> 读不出来时，十处各印一句一模一样的，
    /// 而其中九处把它当成一条普通的红话返回，压根不说是哪一处在造窗）。
    ///
    /// <b><paramref name="checklist"/> 默认 null = 本类去那个目录里读</b>；产品那一侧递
    /// <c>_todos</c> 进来，那是因为它手上有那一份（首启读出来、之后每次改动都跟着保存的），
    /// 而本类读出来的是<b>第二个对象</b>，界面上勾掉一格它一个字节不动。
    /// **默认那一次读在产品那一边是白费的**（读同一个文件、结果当场被覆盖），
    /// 所以产品递自己的那一份 —— 不是为了省一次读，是为了那份对象是<b>活着的那一份</b>。</summary>
    public Options Open(SettingsStore.Read state, Node parent, Action<Options>? wire = null,
        Todos? checklist = null)
    {
        if (GD.Load<PackedScene>("res://src/Options.tscn")?.Instantiate<Options>() is not Options win)
            throw new Exception($"res://src/Options.tscn 实例化不出来（目录 {Dir}）");
        // **次序**：下面这四样必须在挂上去**之前**赋好 —— `_Ready` 的第一件事就问 `Live`。
        win.State = state;
        win.DataDir = Dir;
        win.Checklist = checklist ?? Todos.Load(System.IO.Path.Combine(Dir, Todos.FileName));
        wire?.Invoke(win);
        parent.AddChild(win);   // _Ready 在这一步里跑：绑定、文字、那颗钮的菜单都在那里
        _opened.Add(win);
        return win;
    }

    /// <summary>放掉开过的每一扇窗；<b>自检那一种顺带把自己的临时目录删掉</b>。
    ///
    /// <b>删不掉要说话</b>：临时区里那些孤儿目录就是静悄悄留下的 ——
    /// 界面上一个症状都没有，下一个会话没人知道它在那儿。删不掉的原因（文件被占）
    /// 原样进 <see cref="Problems"/>，由调用方并进自己那份问题里。
    ///
    /// <b>不抛</b>：收尾跑在 <c>finally</c> 里，抛出去会把这一组真正量到的东西盖掉 ——
    /// 那时候要的是「这一组量到了什么」，不是「窗没放干净」。
    ///
    /// <b>幂等</b>：跑两遍只放一次窗、删一次目录，<see cref="Problems"/> 也不重复添 ——
    /// 那一组在 <c>try</c> 里提前 <c>return problems</c>（交出去的是<b>同一个列表对象</b>，
    /// 所以 <c>finally</c> 里并进去的条目照样看得见）之后 <c>finally</c> 还会再跑一次，
    /// 而那一组可能已经自己 <c>Dispose</c> 过了。
    ///
    /// <b>调用方的一条约束</b>：<see cref="Problems"/> 必须并进<b>那一份要交出去的列表对象</b>，
    /// 所以别写 <c>return problems.Concat([...])</c> —— 那是新列表，收尾的条目就漏了。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var win in _opened)
        {
            if (!GodotObject.IsInstanceValid(win)) continue;
            // 先摘再放：引擎对被拒的 `QueueFree` 不报（窗有爹时那一条），
            // 而「挂在树上没被放掉」这件事下一组看不见。摘不掉就不摘，直接放。
            if (win.GetParent() != null) win.GetParent().RemoveChild(win);
            win.QueueFree();
        }
        _opened.Clear();
        if (!_ownsDir) return;
        try
        {
            if (System.IO.Directory.Exists(Dir)) System.IO.Directory.Delete(Dir, true);
        }
        catch (System.IO.IOException e) { Problems.Add($"临时目录没删干净 {Dir}：{e.Message}"); }
        catch (UnauthorizedAccessException e) { Problems.Add($"临时目录没删干净 {Dir}：{e.Message}"); }
    }

    // ---- 临时目录那一族：跟窗的接线是两件事，所以各管各的 --------------------------
    //
    // 「每一组只跑自己那个用完就删的临时目录」是 `CODING_STANDARDS.md`「写进文档的东西」
    // 那一节的话；**它原来挂在 `Options` 上**（两个 internal 静态方法），而 `Presets` /
    // `PresetTemplates` 也在用 —— 临时目录那一族跟设置窗没有关系，挂在窗那一类上
    // 纯粹因为第一份自检写在 `Options.cs` 里。搬家。
    //
    // **下面这两个是给「不开窗、只要一个目录」的调用方准备的**：那两族自己开自己的
    // 窗（`Presets` 开 `Presets.tscn`、`PresetTemplates` 一扇窗也不开），所以它们要的
    // 只是「造一个目录」这一半，走不了 `InTempDir`（那个会连窗一起管）。要用 `InTempDir`
    // 的走那一支，收尾才有实测结果。

    /// <summary>造一个只属于这一组的临时目录，<b>旧的先清掉再建</b>。
    ///
    /// <b>为什么先删</b>：目录名是 <c>keeper-*-selfcheck-{进程号}</c>，而进程号<b>会复用</b> ——
    /// 上一发跑完留下的目录被下一发同名进来，于是「存一个生活模式」撞上残留名单里那一条，
    /// 自检报的是「名单里已经有那一条了」，而它量的其实是自己上一次的残留。
    /// 2026-10-01 量到过：<c>screen</c> 那一组偶发红，而临时区里已经积了几十个这种目录。
    /// <b>留着不删不只是脏 —— 它给「自检全绿」这句话造了一个可能为假的场合。</b>
    ///
    /// <b>「先删再建」在收尾这一侧是另一半</b>：用完就删。而<b>删不掉静悄悄吞掉</b>
    /// （这一侧）—— 「用完就删」是规矩，而它删不掉时那一发量的是自己那份目录，不是
    /// 「目录清得掉」。<b>真要说话的是 <see cref="Dispose"/></b>：那儿删不掉才是这一组
    /// 自己的问题，所以它进 <see cref="Problems"/>。</summary>
    public static string TempDir(string tag)
    {
        var dir = System.IO.Path.Combine(Tmp.Root,
            $"keeper-{tag}-selfcheck-{System.Environment.ProcessId}");
        DropDir(dir);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>用完就删。<b>吞掉失败</b> —— 它被 <see cref="TempDir"/> 自己调在前面
    /// （那里删不掉是正常的：上一跑可能还开着），所以它不报。</summary>
    public static void DropDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- 自检这一族：量的是「收拢」本身，不是窗上那些字 --------------------------------
    //
    // **为什么要有这一组** —— 收拢那件事做完，九处调用方各自少了几行，而「次序对不对」
    // 「目录删没删掉」这两件事<b>谁都不再自己量</b>了：那一句注释从十处收到一处，
    // 于是「次序写错了」从「三处注释互相提醒」变成「一处注释提醒」——
    // **注释不会报红**。所以「次序」得在这一组里变成一条能失败的判据。
    //
    // 这一组量四样，每样都能失败：
    // 1. **次序**：挂上去那一刻 `State` / `DataDir` / `Checklist` 三样已经在了
    //    （`_Ready` 问的就是它们；挂上去以后再补就晚了）。
    // 2. **`wire` 跑在挂上去之前**：它接的钩子在 `_Ready` 期间就生效。
    // 3. **收尾真的把窗放了**：放完那扇窗不在树上了（`IsInstanceValid` 仍 true 是
    //    引擎的延迟释放，所以问的是「有没有爹」而不是「还有效没有」）。
    // 4. **收尾真的把目录删了**，而删不掉会说话（`Problems` 里多一条）。

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOnHost Host = new(
        () => "设置窗宿主通过：三样在挂上去之前接好、wire 跑在 _Ready 之前（窗内实测结果接得到）、" +
              "收尾把窗从树上摘掉、临时目录连文件一起删掉、ForDir 不碰用户那份、" +
              "目录名带进程号且旧的先清掉（残留不在了）、" +
              "清单默认从目录读而递进去的就是递进去那一个对象",
        "设置窗宿主", SelfTest);

    /// <summary>组名 <c>options-host</c>。走 <c>Probe.cs</c> 那一行登记。</summary>
    public static List<string> SelfTest(Node parent)
    {
        var checks = new Checks();

        checks.CheckTyped("开窗那一段：挂上去之前那三样已经在树上", () =>
        {
            var host = InTempDir("host-order");
            var state = SettingsStore.Load(host.Dir);
            if (state.Error != null) { checks.Fail($"底稿读不通：{state.Error}"); return; }
            if (Lang.Apply(state.Current.Language).Count > 0)
            {
                checks.Fail("语言表没装上，这一组量的不是屏幕上那些字");
                return;
            }
            // 走真路径：让 `Open` 自己挂，我们只在它挂完之后问三样。
            var holder = new Node { Name = "OrderHolder" };
            parent.AddChild(holder);
            var seen = host.Open(state, holder);
            foreach (var (name, ok) in new (string, bool)[]
            {
                ("State", seen.State != null),
                ("DataDir", seen.DataDir == host.Dir),
                ("Checklist", seen.Checklist != null),
            })
                if (!ok) checks.Fail($"开窗之后 {name} 不是接好的那一份");
            if (seen.GetParent() != holder) checks.Fail("窗没挂在递进去的那个宿主上");
            // **对照**：路径确实来自 `host`，不是默认路径凑出来的。
            if (host.Dir.Length == 0) checks.Fail("对照：这一组自己的临时目录是空的路径");
            if (!seen.DataDir.StartsWith(Tmp.Root, StringComparison.OrdinalIgnoreCase))
                checks.Fail($"对照：这一组的目录不在临时区：{seen.DataDir}（不许碰 %APPDATA%）");
            host.Dispose();
            checks.Problems.AddRange(host.Problems);
        });

        checks.Check("清单：默认从目录读，而递进去的那一份就是递进去的那一个对象", () =>
        {
            var host = InTempDir("host-checklist");
            // **对照先摆**：磁盘上确实有那一行。少了它，下面那句「没读到」可能只是
            // 判据问错了内容（文件里压根没有那句话），那就成了空过。
            var path = System.IO.Path.Combine(host.Dir, Todos.FileName);
            new Todos { Items = [new Todos.Todo { Text = "这一组自己写的那一条" }] }.Save(path);
            if (!System.IO.File.ReadAllText(path).Contains("这一组自己写的那一条"))
                checks.Fail("对照：那一行压根没落盘，尺子坏了");
            var state = SettingsStore.Load(host.Dir);
            var holder = new Node { Name = "ChecklistHolder" };
            parent.AddChild(holder);
            // **默认那一份**：不递 `checklist`，本类去目录里读。
            var read = host.Open(state, holder);
            if (read.Checklist == null) checks.Fail("不递清单的时候窗上那一份是空的");
            else if (!read.Checklist.Items.Any(i => i.Text == "这一组自己写的那一条"))
                checks.Fail("不递清单的时候本类没从目录里读到那一条 —— 它压根没读");
            host.Dispose();
            host.Dispose();   // 幂等

            // **递进去的那一份**：产品那一侧走的正是这一支。
            var host2 = InTempDir("host-checklist2");
            var state2 = SettingsStore.Load(host2.Dir);
            var holder2 = new Node { Name = "ChecklistHolder2" };
            parent.AddChild(holder2);
            var mine = new Todos { Items = [new Todos.Todo { Text = "App 手上的那一份" }] };
            var got = host2.Open(state2, holder2, checklist: mine);
            // **同一个对象**，不是「内容一样的那一个」—— 内容一样的话，界面上勾掉一格
            // 而 App 那一份不动，症状是「清单存不下去」，界面上看不出根因。
            if (!ReferenceEquals(got.Checklist, mine))
                checks.Fail("递进去的清单不是递进去的那一个对象 —— 本类又读了一份");
            host2.Dispose();
            holder.QueueFree();
            holder2.QueueFree();
        });

        checks.Check("wire 跑在挂上去之前：它接的钩子在 _Ready 期间就生效", () =>
        {
            var host = InTempDir("host-wire");
            var state = SettingsStore.Load(host.Dir);
            var holder = new Node { Name = "WireHolder" };
            parent.AddChild(holder);
            var lines = new List<string>();
            // `wire` 里接的 `Log` 能不能接到 `_Ready` 期间那几行 —— 那就是「跑在挂上去之前」
            // 的证据。挂上去之后接就晚一帧（`Options.NavSelfTest` 那一组量的正是那几行，
            // 它是同一个事实的产品侧实测结果）。
            var win = host.Open(state, holder, w => w.Log = (_, line) => lines.Add(line));
            if (!lines.Any(l => l.Contains("可见性")))
                checks.Fail("wire 接的 Log 没接到 _Ready 期间那几行 —— 它跑晚了，或者没接上");
            if (win.Log == null) checks.Fail("Log 是 null，不是 wire 接的那一个");
            host.Dispose();
            if (lines.Count == 0) checks.Fail("对照：这一组一条窗内实测结果都没接到，判据量的是空");
            holder.QueueFree();
        });

        checks.Check("收尾把窗放了：放完那扇窗不在树上了", () =>
        {
            var host = InTempDir("host-drop");
            var state = SettingsStore.Load(host.Dir);
            var holder = new Node { Name = "DropHolder" };
            parent.AddChild(holder);
            var win = host.Open(state, holder);
            if (win.GetParent() == null) checks.Fail("开窗之后那扇窗压根没挂在树上，这一组量的是别的东西");
            host.Dispose();
            // 问的是「有没有爹」：`IsInstanceValid` 在引擎的延迟释放下仍报 true，
            // 问它就量不到「放没放掉」（`CODING_STANDARDS.md`「代码级规矩」那一条同款）。
            if (win.GetParent() != null) checks.Fail("收尾之后那扇窗还在树上");
            // **幂等**：放两遍只放一次（这一组可能在 try 里自己 Dispose 过）。
            host.Dispose();
            if (host.Problems.Count > 0) checks.Problems.AddRange(host.Problems);
            holder.QueueFree();
        });

        checks.Check("收尾把目录删了，而且删不掉会说话", () =>
        {
            var host = InTempDir("host-dir");
            var dir = host.Dir;
            if (!System.IO.Directory.Exists(dir))
            {
                checks.Fail("这一组自己的临时目录压根没建出来，这一组量的是别的东西");
                return;
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "留一个文件.txt"), "留着");
            host.Dispose();
            if (System.IO.Directory.Exists(dir))
                checks.Fail($"收尾之后临时目录还在：{dir}（文件还在的时候目录删不掉，那一条得说话）");
            // **对照**：目录里有一个文件时 <c>Delete(recursive)</c> 该删得掉 ——
            // 上面那一条量的是「删了没有」，这一条量的是「尺子没坏」。
            if (host.Problems.Count > 0)
                checks.Fail($"目录里只有一个文件，删它却报了问题：{string.Join("；", host.Problems)}");
        });

        checks.Check("产品那一种：ForDir 不删别人的目录", () =>
        {
            // **产品那一份目录不许被动** —— 自检全绿的场合里，用户的配置是最碰不得的一样。
            var temp = TempDir("host-fordir");
            System.IO.File.WriteAllText(System.IO.Path.Combine(temp, "用户在写的东西.jsonc"), "{}");
            var host = ForDir(temp);
            host.Dispose();
            if (!System.IO.File.Exists(System.IO.Path.Combine(temp, "用户在写的东西.jsonc")))
                checks.Fail("ForDir 那一支把目录删了 —— 产品那一份是用户正在写着的配置");
            if (host.Problems.Count > 0)
                checks.Fail($"ForDir 那一支不该有问题，却报了：{string.Join("；", host.Problems)}");
            DropDir(temp);
            // **对照**：上面量的是「`ForDir` 那一支不删」；这一条量的是「`DropDir` 删得掉」
            // —— 而它刚刚删掉的就是那一个目录。少了这一条，上面那条可能是因为
            // `DropDir` 本来就删不掉才「没删」（尺子坏了），那就成了假阴性。
            if (System.IO.Directory.Exists(temp)) checks.Fail("对照：DropDir 没删掉，尺子坏了");
            // **而 `ForDir` 那一支此刻什么也不做** —— 那个目录已经没了，说明刚才那三行
            // 确实是 `DropDir` 干的，不是 `ForDir` 顺手带走的。
        });

        checks.Check("临时目录那一族：名字带进程号、旧的先清掉", () =>
        {
            // 名字带进程号 → 两发自检同时跑不会互相删对方的目录。
            var one = TempDir("host-tag");
            var two = TempDir("host-tag");
            if (one != two) checks.Fail("同一个 tag 两次造出两个不同路径，进程号没进目录名");
            if (!one.Contains(System.Environment.ProcessId.ToString()))
                checks.Fail($"目录名里没有进程号：{one}");
            // **「旧的先清掉」是能失败的那一半**：往那个目录里塞一个标记，再造一次同名的 ——
            // 标记没了才算数。少了那一句 `DropDir`，标记就在，于是「自检全绿」这句话
            // 有了一个可能为假的场合（进程号会复用）。
            System.IO.File.WriteAllText(System.IO.Path.Combine(one, "上一发的残留"), "x");
            var again = TempDir("host-tag");
            if (System.IO.File.Exists(System.IO.Path.Combine(again, "上一发的残留")))
                checks.Fail("再造一次同名的，上一发的残留还在 —— 「先删再建」没了");
            // **对照**：上面那一条量的是「残留被清掉了」，它也可能是因为
            // `TempDir` 压根没建目录（于是那个文件当然「不在」）。所以正面问一句
            // **目录在不在** —— 它在，上面那条才算量到了东西。
            if (!System.IO.Directory.Exists(again)) checks.Fail("对照：目录没建出来，尺子坏了");
            DropDir(again);
            if (System.IO.Directory.Exists(again)) checks.Fail("对照：DropDir 没删掉，尺子坏了");
        });

        return checks.Report();
    }
}
