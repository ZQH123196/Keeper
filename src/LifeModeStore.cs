using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Keeper;

/// <summary>生活模式这一族在磁盘上的样子：一个子目录，里头一份<b>名单</b>（<see cref="LifeModeIndex"/>）
/// 和每条生活模式一份<b>配置文档</b>。生活模式那一份文档不是另一族：它的格式和「导出」完全一样
/// （设计文档 `docs/design/life-mode.md`「一个生活模式 = 一份完整的配置文档」那条），
/// 所以「值超范围要夹」「读的时候宽容」「陌生的键不许有代价」「导出→导入→导出逐字节相同」
/// 这一整套规矩白送 —— 为生活模式另立一份格式就是为同一件事建第二条实现。
///
/// <b>这一族装什么、不装什么</b>：装的是「Keeper 干什么、干到什么程度」，不装「这台机器上的
/// Keeper 是什么样」。界线<b>从声明表读</b>（<see cref="SettingDecl.Domain"/>，
/// <see cref="InLifeMode"/>），不是一份手抄的键名清单 —— 以后再加一个系统级设置，它自动在生活模式外面，
/// 不靠人记得划（设计文档 `docs/design/life-mode.md`「生活模式装的是业务功能，不是这台机器的属性」那条）。
///
/// <b>切一个生活模式 = 整份换掉</b>：走 <see cref="SettingsStore.ImportScope"/>（也就是导入那一条路），
/// 被换掉的那份留在 <c>settings.jsonc.bak</c>。「半个生活模式是不存在的」—— 进不来的键回默认值，
/// 域在生活模式外面的那几条一个字节都不动。
///
/// <b>「我现在在哪个生活模式」不落盘</b>：这一族不记当前是哪一个（那是界面状态，和「显示高级设置」
/// 那一条同一个道理），所以开窗拿活的那一份和每个生活模式那一份比一遍就行。</summary>
public static class LifeModeStore
{
    /// 生活模式名上限。路径有长度上限，而名字是任何合法 utf-8 —— emoji 和代理对都算一个字符，
    /// 所以这里是<b>字符</b>数不是字节数。
    public const int MaxNameLength = 80;

    public static string DirOf(string dataDir) => Path.Combine(dataDir, LifeModeIndex.FolderName);

    public static string ListPathOf(string dataDir) => Path.Combine(DirOf(dataDir), LifeModeIndex.FileName);

    public static string PathOf(string dataDir, LifeModeIndex.Entry entry) =>
        Path.Combine(DirOf(dataDir), entry.File);

    /// 生活模式那一份是给人看的文档，写出去不带 BOM —— 名字里有中文、有 emoji，
    /// 而第一行是注释，带 BOM 会让别的工具把那个 <c>//</c> 当内容。
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>这一条装不装进生活模式。<b>判据是域本身</b>，所以它是一句问域的话，
    /// 不是一份键名清单（见类型注释）。<see cref="SettingDecl.Domain"/> 默认 <c>break</c>，
    /// 那正是「休息那一侧是大多数键的家」；<c>flows</c> 那张休息流程表也在里面
    /// （设计文档 `docs/design/life-mode.md`「其余两域连同那张休息流程表全部进」）。</summary>
    public static bool InLifeMode(SettingDecl decl) => decl.Domain != Settings.SystemDomain;

    /// <summary>生活模式名过不过得去。<b>null 即过</b>，不合法时还一句说给人听的话（界面上原样印）。
    ///
    /// 设计文档 `docs/design/life-mode.md`「生活模式名是任何合法 utf-8，上限 80 个字符（路径有长度），不许空、不许全是空格」那条。
    /// <b>重名不在这里判</b>（<see cref="SaveAs"/> 判，它要连「已经叫这个名的那一条」一起说）；
    /// 这一层 02 的「重名拦截」和「覆盖」还要再用一遍，所以它是独立的一个函数而不是
    /// 内联在按钮里。</summary>
    public static string? ValidateName(string name)
    {
        if (name.Trim().Length == 0) return "生活模式名不能是空的";
        if (name.Length > MaxNameLength)
            return $"生活模式名最多 {MaxNameLength} 个字符，现在 {name.Length} 个";
        return null;
    }

    /// <summary>存这一族之前过的那一组判据的结果。
    ///
    /// <b>为什么不是两个裸值</b>：重名那一档界面上要说的那半句和别的档不一样 ——
    /// 它得<b>指向「覆盖」</b>（设计文档 `docs/design/life-mode.md`「重名当场拦住并指向覆盖」那条），而「覆盖」是界面上
    /// 的一项、住在语言表里。所以这一层交出去的是<b>事实</b>
    /// （<see cref="Taken"/>：名单里叫这个名字的那一条是谁），话由界面说。
    ///
    /// <b>两个元素的 Deconstruct 是给 01 那批调用点留的</b>：<c>var (error, index) =
    /// SaveAs(...)</c> 照旧成立，而第三个字段是新长出来的那一格。
    ///
    /// <b>判据住在这一层，界面不许自己再判一次重名</b>：工单 05「收一个生活模式进来」走的是
    /// 这一族的另一个入口，它要撞上名时说的话和这里一模一样 —— 判在回调里就是两份，
    /// 而两份里过期的那一份不响。</summary>
    public sealed record Verdict(string? Error, LifeModeIndex? Index, LifeModeIndex.Entry? Taken)
    {
        /// 撞了重名（不是别的没成）。界面上对这一档改说「指向覆盖」，别的档各说各的。
        public bool Collided => Error != null && Taken != null;

        public void Deconstruct(out string? error, out LifeModeIndex? index)
        {
            error = Error;
            index = Index;
        }
    }

    /// <summary>存为一个新生活模式：磁盘上多出一份完整的配置文档，名单里多出这一条。
    ///
    /// <b>存的是递进来的那一份</b>（设置窗那边递的是<b>窗里这一份</b>，见
    /// <see cref="Options.AcceptLifeModeName"/>）—— 「我现在在哪个生活模式」是拿<b>窗里这一份</b>
    /// 和每个生活模式那一份比出来的（<see cref="StandingOf"/>，设计文档 `docs/design/life-mode.md`「「我现在在哪个生活模式」不落盘」那一节），存另一份的话刚存完按钮上就写着「不在任何生活模式里」，而那半句
    /// 「窗里没保存的改动没有算进去」说的是改动、不是「你现在在哪儿」。
    ///
    /// <b>先写文档再写名单</b>：名单写坏了而文档已经在，是一份没人指着的文件（说得出）；
    /// 反过来是名单指着一个不存在的文件（说不清，而且会被当成「文件丢了」那一档）。
    ///
    /// <b>名单读不通就整个不存</b>：设计文档 `docs/design/life-mode.md`「读不通有三档，各说各的」那一节—— 读不通的那份是人手写的，覆盖它就是抹掉。<b>重名也在这一层拦</b>：
    /// <paramref name="Verdict.Taken"/> 就是撞上那一条，界面拿它指向「覆盖」。</summary>
    public static Verdict SaveAs(string dataDir, string name, Settings snapshot)
    {
        if (ValidateName(name) is { } bad) return new Verdict(bad, null, null);

        var listPath = ListPathOf(dataDir);
        var index = LifeModeIndex.Load(listPath);
        if (index.Unreadable) return new Verdict("生活模式名单读不通，所以没有存", null, null);
        if (index.Find(name) is { } taken) return new Verdict($"名单里已经有「{name}」了", null, taken);

        // 编号从两份里更大的那个后面接着数：名单记着的那一份，和磁盘上还在的那几份。
        // 少看一份，删掉名单之后新生活模式就会盖掉一份真文档（LifeModeIndex.HighestNumberOnDisk）。
        index.RaiseTo(index.HighestNumberOnDisk(DirOf(dataDir)));
        var entry = index.Add(name);

        var (written, path) = WriteLifeModeFile(dataDir, entry, snapshot);
        if (written != null)
        {
            // 文档没写成：那一条是假的，从名单里撤掉（号码照样作废 —— 永不复用不管这一发成没成）。
            index.Entries.Remove(entry);
            return new Verdict(written, null, null);
        }

        try
        {
            index.Save(listPath);
        }
        catch (Exception e)
        {
            return new Verdict($"生活模式那一份写成了（{entry.File}），但名单没写进去，所以它还没有名字：{e.Message}", null, null);
        }
        return new Verdict(null, index, null);
    }

    /// <summary>盖回一个<b>已经有的</b>生活模式：把手上这一份写进<b>同一个文件</b>，名单上那一条
    /// 和它的编号都不动。
    ///
    /// <b>它不是「另存一份再改名」</b> —— 那样会在编号那一族里留下一条没人要的记录，而
    /// 「盖回去」在用户那边是一个动作。设计文档 `docs/design/life-mode.md`「生活模式名…重名当场拦住并指向覆盖」那一段的
    /// 另一半就是它：重名那一档已经把「撞上的是哪一条」交出去了，界面顺着那句话指向这里。
    ///
    /// 名单里没有这个名字就是没有 —— 那是「存为新生活模式」那一路，不在这里悄悄变出来。</summary>
    public static Verdict Overwrite(string dataDir, string name, Settings snapshot)
    {
        if (ValidateName(name) is { } bad) return new Verdict(bad, null, null);

        var listPath = ListPathOf(dataDir);
        var index = LifeModeIndex.Load(listPath);
        if (index.Unreadable) return new Verdict("生活模式名单读不通，所以没有盖", null, null);
        if (index.Find(name) is not { } entry) return new Verdict($"名单里没有「{name}」，所以没有盖", null, null);

        var (written, _) = WriteLifeModeFile(dataDir, entry, snapshot);
        return written != null
            ? new Verdict(written, null, entry)
            : new Verdict(null, index, entry);
    }

    /// <summary>从一份文件<b>收</b>一个生活模式进来：那一份<b>逐字节</b>搬进一个新号，名单上多一条。
    /// <b>活的那一份一个字节都不动</b> —— 收进来的那一刻不顶掉你现在这份配置（契约
    /// 「生活模式是一整套配置的快照」那一节，工单 05 的承重处）。你什么时候切是另一件事
    /// （<see cref="SwitchTo"/>），这样从别人那儿拿到一份配置时你能先收着，回头对比着切。
    ///
    /// <b>搬的是字节，不是渲染出来的</b>：<see cref="WriteLifeModeFile"/> 那一族是<b>渲染</b>一份
    /// <see cref="Settings"/>（每个键上面两行注释、顺序固定），而这一路<b>原样搬一份文件</b> ——
    /// 注释、空行、顺序、用户自己加的键全跟着走。理由就是「导出一个生活模式再收回来逐字节相同」：
    /// 走渲染器的话注释和陌生的键当场就没了，而那正是「一个生活模式 = 导出的那一份」白送的那一整套
    /// 规矩（设计文档 `docs/design/life-mode.md`「一个生活模式 = 一份完整的配置文档」那条）。**注释和陌生的键是这一族的资产，
    /// 不是可以重排的排版。**
    ///
    /// <b>域在生活模式外面的那几条（<see cref="InLifeMode"/> 不收的）原样留在搬进来那一份里</b>，而
    /// 它们<b>切的时候照旧一个字节都不动</b>：<see cref="SwitchTo"/> 走
    /// <see cref="SettingsStore.ImportScope"/>，那一条是<b>按域过滤</b>的。所以在收进来的这一份里
    /// 剪掉它们会毁掉「逐字节相同」，而留着它们只是让那一份<b>多几行不生效的键</b> ——
    /// 设计文档 `docs/design/life-mode.md`「系统那域的键一律不进生活模式」那一半由<b>切</b>那一侧兑现，不由收进来这一侧兑现。
    ///
    /// <b>读得通才收</b>：一份读不通的文件进不了名单 —— 否则是用户还没看见的「某一个生活模式那一份
    /// 读不通」（设计文档 `docs/design/life-mode.md`「读不通有三档」第二档），而那一档的代价是「别的照常能切、这一条点不动」。
    /// 当场说清比收下一个坏生活模式划算。只查「讲不讲得通」，不查格式版本（设计文档 `docs/design/config.md`「格式」那一节：两种情况都会夹取值，新文件不可能把程序带进它处理不了的状态）。
    ///
    /// <b>判重就在这一层，和 <see cref="SaveAs"/> 同一道判据</b>：工单 05 的「来历」段写着
    /// 「没有它，收进来这一件就是一个绕过重名检查的口子」—— 所以两处都交出
    /// <see cref="Verdict.Taken"/>，界面上那半句（指向「盖回」）也一样。
    /// <b>先搬文件再写名单</b>，搬不成就把那一条从名单上撤掉（号照样作废，同 <see cref="SaveAs"/>）。</summary>
    public static Verdict TakeIn(string dataDir, string name, string source)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception e)
        {
            return new Verdict($"那一份没读进来：{e.Message}", null, null);
        }
        return TakeIn(dataDir, name, bytes);
    }

    /// <summary>同一个 <see cref="TakeIn(string,string,string)"/>，只是<b>那一份已经在手里</b>。
    ///
    /// <b>为什么开这一条形态而不是开第二份实现</b>：从文件收一份进来时读的是
    /// <see cref="File.ReadAllBytes"/>，而从<b>装在程序里的</b>那一份收进来时读的是引擎的
    /// <see cref="FileAccess"/> —— 打包之后预设目录在 pck 里，<see cref="File"/> 够不着
    /// （实测结果在 <c>docs/research/preset-life-flows-readonly-directory.md</c>）。两条读法之后
    /// 是<b>同一件事</b>：发号、判重、搬字节、写名单、留备份。所以判据、递增号、撞名拦阻
    /// 那一整套规矩仍然只有这一处实现，递增号分配 / 备份 / 逐字节回环也仍然只有一把尺子
    /// （设计文档 `docs/design/preset-life-mode.md`「收一个进来走现成的导入那条路」那条）。</summary>
    /// <param name="bytes">那一份的<b>字节</b>。不在这儿读文件，所以读不进来的那一档归调用点说
    /// （<see cref="TakeIn(string,string,string)"/> 那一支把 <see cref="Exception"/> 的话递上来，
    /// 画廊那一支把 <c>null</c> 当「读不出来」）。</param>
    public static Verdict TakeIn(string dataDir, string name, byte[] bytes)
    {
        if (ValidateName(name) is { } bad) return new Verdict(bad, null, null);

        var listPath = ListPathOf(dataDir);
        var index = LifeModeIndex.Load(listPath);
        if (index.Unreadable) return new Verdict("生活模式名单读不通，所以没收", null, null);
        if (index.Find(name) is { } taken) return new Verdict($"名单里已经有「{name}」了", null, taken);

        var text = Utf8.GetString(bytes);
        if (text.Trim().Length == 0) return new Verdict("那一份读不通，所以没收：它是空的", null, null);
        if (!JsoncDoc.TryParse(text, out _, out var error))
            return new Verdict($"那一份读不通，所以没收：{error}", null, null);

        // 编号从两份里更大的那个后面接着数（同 SaveAs：少看一份就会盖掉磁盘上还在的那一份）。
        index.RaiseTo(index.HighestNumberOnDisk(DirOf(dataDir)));
        var entry = index.Add(name);

        var path = PathOf(dataDir, entry);
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent != null) Directory.CreateDirectory(parent);
            // **原样搬字节**，不过渲染器（理由见类型注释那一段）。
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            index.Entries.Remove(entry);   // 那一条是假的，从名单上撤掉（号照样作废 —— 永不复用）
            return new Verdict($"生活模式那一份没写成：{e.Message}", null, null);
        }

        try
        {
            index.Save(listPath);
        }
        catch (Exception e)
        {
            return new Verdict($"生活模式那一份收进来了（{entry.File}），但名单没写进去，所以它还没有名字：{e.Message}", null, null);
        }
        return new Verdict(null, index, entry);
    }

    /// <summary>删掉一个生活模式：名单上那一条走人、磁盘上那一份也删掉，**编号不回收** ——
    /// 下一个新生活模式拿更大的号（设计文档 `docs/design/life-mode.md`「文件名由索引分配递增号，且永不复用」那条）。
    ///
    /// <b>先写名单再删文件</b>：反过来（先删文件）而名单写失败的话，磁盘上留下的是一份
    /// 没人指着的文档 —— 用户在界面上看不见、也删不掉。而先写名单最坏是那个文件删不掉，
    /// 它<b>还在那儿</b>，号照样作废（<see cref="LifeModeIndex.HighestNumberOnDisk"/> 数的就是
    /// 目录里还在的那几份，所以下一个新生活模式还是撞不上它）。
    ///
    /// 第三格是「那个文件还在」那一句：不是失败（名单上已经没有它了），但得说出来。</summary>
    public static (string? Error, LifeModeIndex? Index, string? LeftBehind) Delete(string dataDir, string name)
    {
        var listPath = ListPathOf(dataDir);
        var index = LifeModeIndex.Load(listPath);
        if (index.Unreadable) return ("生活模式名单读不通，所以没有删", null, null);
        if (index.Find(name) is not { } entry) return ($"名单里没有「{name}」", null, null);
        if (!index.Remove(name)) return ($"名单里没有「{name}」", null, null);

        try
        {
            index.Save(listPath);
        }
        catch (Exception e)
        {
            return ($"名单没写出去，所以没有删：{e.Message}", null, null);
        }

        var path = PathOf(dataDir, entry);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e)
        {
            return (null, index, $"{path} 还在磁盘上：{e.Message}");
        }
        return (null, index, null);
    }

    /// <summary>一颗生活模式那一份怎么落盘。<b>存和盖走同一条</b> —— 「盖回去」写的是同一个文件，
    /// 渲染器也是同一个（和「导出」那一份完全一样，设计文档 `docs/design/life-mode.md`「一个生活模式 = 一份完整的配置文档」）。</summary>
    private static (string? Error, string Path) WriteLifeModeFile(
        string dataDir, LifeModeIndex.Entry entry, Settings snapshot)
    {
        var path = PathOf(dataDir, entry);
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent != null) Directory.CreateDirectory(parent);
            // 和导出同一条渲染器：每个键上面两行注释，域在生活模式外面的那几条不写进去。
            File.WriteAllText(path, SettingsDoc.RenderScoped(snapshot, InLifeMode), Utf8);
        }
        catch (Exception e)
        {
            return ($"生活模式那一份没写成：{e.Message}", path);
        }
        return (null, path);
    }

    /// <summary>「我现在在哪个生活模式」的那一档。<b>不落盘</b>（设计文档 `docs/design/life-mode.md`「「我现在在哪个生活模式」不落盘」
    /// 那条）：拿手上这一份和每个生活模式那一份<b>比一遍</b>，一致就是那一个，不一致就是不在
    /// 任何一个里 —— 和「显示高级设置」同一个道理，界面状态不进配置。
    ///
    /// <b>比的是值，不是字节</b>（<see cref="SameAs"/>）：用户在生活模式那一份上手加了一行
    /// 注释、动了一个空行，那不该让他「变成不在这个生活模式里」。
    ///
    /// <b>名单读不通自己一档</b>（<see cref="Standing.ListUnreadable"/>），不是
    /// <see cref="Standing.None"/>：那是一份<b>空</b>名单（你还没存过）和一份<b>坏</b>名单
    /// （你存过，而它读不出来）两件事，而写成同一句就是<b>把一份坏文件说成「一个生活模式都没有」</b>
    /// —— 自检里看着是绿的（01/02 那一版就是那样），屏幕上是一次静悄悄的回退。
    /// 设计文档 `docs/design/life-mode.md`「读不通有三档」那一条的头一档：那一族当成没有，<b>并且不覆盖磁盘上那份</b>。</summary>
    public static Spot StandingOf(string dataDir, Settings shown, LifeModeIndex? index = null)
    {
        var list = index ?? LifeModeIndex.Load(ListPathOf(dataDir));
        if (list.Unreadable) return new Spot(Standing.ListUnreadable, null);
        if (list.Entries.Count == 0) return new Spot(Standing.None, null);
        foreach (var entry in list.Entries)
        {
            var path = PathOf(dataDir, entry);
            if (!File.Exists(path)) continue;      // 名单里有、磁盘上没有：算「不在任何一个里」
                                                     // （<see cref="Stop.NoFile"/> 那一档是切换
                                                     //  的时候照实说，不在这里悄悄补一个默认）
            if (SameAs(shown, path)) return new Spot(Standing.In, entry.Name);
        }
        return new Spot(Standing.Elsewhere, null);
    }

    /// 四档见 <see cref="StandingOf"/>。<see cref="In"/> 那一档带着是哪一条，
    /// <see cref="ListUnreadable"/> 那一档是<b>名单</b>坏了（不是「一个生活模式都没有」）。</summary>
    public enum Standing { None, In, Elsewhere, ListUnreadable }

    public sealed record Spot(Standing At, string? Name);

    /// <summary>手上这一份和某一个生活模式那一份<b>是不是同一套配置</b>。
    ///
    /// <b>比哪些键是从声明表问出来的</b>（<see cref="InLifeMode"/>），不抄一份清单 ——
    /// 以后再加一个系统级设置，这一条自动跟着它走（<c>CODING_STANDARDS.md</c> 第 6 条：
    /// 判据和实现都不许存一份会腐烂的副本）。组（<see cref="SettingKind.Group"/>）自己不写值，
    /// 跳过；那张休息流程表（<see cref="SettingKind.ModeTable"/>）整张比。
    ///
    /// 读不通的那一份<b>不是</b>「一致」：它比不了，而「比不了」不能读成「一样」
    /// （<c>CODING_STANDARDS.md</c> 第 2 条那一族）。</summary>
    private static bool SameAs(Settings shown, string modePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(modePath, Utf8);
        }
        catch (IOException)
        {
            return false;
        }
        if (!JsoncDoc.TryParse(text, out var doc, out _) || doc == null) return false;
        var mine = shown.ToDocument();
        foreach (var decl in Settings.Declarations)
        {
            if (decl.Kind == SettingKind.Group || !InLifeMode(decl)) continue;
            if (!JsonNode.DeepEquals(Pick(mine, decl.Key), Pick(doc!.Values, decl.Key))) return false;
        }
        return true;
    }

    /// <summary>切一个生活模式没成的<b>哪一档</b>。设计文档 `docs/design/life-mode.md`「读不通有三档，各说各的」那一条说的就是
    /// 头三档（「生活模式是一整套配置的快照…」那一节）。
    ///
    /// <b>为什么是事实而不是一句现成的话</b>：话由界面说（同 <see cref="Verdict"/> 的道理），
    /// 因为 <see cref="Switched.Detail"/> 带着<b>路径</b> —— 那是给日志看的，而设计文档 `docs/design/config.md`「界面上不许出现配置文件内部的名字」那一节不许它上屏幕。**四档互不相同**：名单读不通 ≠ 某一个生活模式那
    /// 一份读不通 ≠ 名单里有而文件不在，而最后那一档说的根本不是「读不通」。</summary>
    public enum Stop
    {
        /// <summary><b>名单</b>读不通：生活模式这一族当成没有，<b>磁盘上那一份一个字节都不动</b>。</summary>
        ListUnreadable,
        /// <summary><b>某一个生活模式那一份</b>读不通：不切它，说清是<b>哪个名字</b>，别的照常能切
        /// —— 一个坏的不拖累全部。</summary>
        LifeModeUnreadable,
        /// <summary>名字在名单里而文件不在：照实说，<b>不静悄悄补一个默认</b>。</summary>
        NoFile,
        /// <summary>名单里压根没有这个名字（不是读不通那一族里的任何一档）。</summary>
        NotInList,
    }

    /// <summary>切一个生活模式的结果。<b>两元素的 <c>Deconstruct</c> 是给 01/02 那些调用点留的</b>：
    /// <c>var (error, state) = SwitchTo(...)</c> 照旧成立（自检里那几条判据一个字没动），
    /// 而 <see cref="Stopped"/> 是这一族新长出来的那一格 —— 三档要分得开，判据问的就是它。</summary>
    public sealed record Switched(Stop? Stopped, string Detail, SettingsStore.Read? State)
    {
        public bool Ok => Stopped == null;

        public void Deconstruct(out string? error, out SettingsStore.Read? state)
        {
            error = Ok ? null : Detail;
            state = State;
        }
    }

    /// <summary>切到一个生活模式：活的那一份里只保留系统那几条，其余全部换成生活模式里的值，
    /// 被换掉的那份留下备份。<b>读不通有三档，各说各的</b>（<see cref="Stop"/>），一条都不动磁盘。</summary>
    public static Switched SwitchTo(string dataDir, SettingsStore.Read state, string name)
    {
        var listPath = ListPathOf(dataDir);
        var index = LifeModeIndex.Load(listPath);
        if (index.Unreadable)
            return new Switched(Stop.ListUnreadable, $"生活模式名单读不通，所以没有切（{listPath}）", null);
        var entry = index.Find(name);
        if (entry == null) return new Switched(Stop.NotInList, $"名单里没有「{name}」", null);
        var path = PathOf(dataDir, entry);
        if (!File.Exists(path))
            return new Switched(Stop.NoFile, $"名单里有「{name}」，磁盘上没有 {entry.File}", null);

        var written = SettingsStore.ImportScope(dataDir, state, path, InLifeMode);
        return written.Error != null
            ? new Switched(Stop.LifeModeUnreadable, written.Error, null)
            : new Switched(null, "", written.State);
    }

    // ---- 自检：管理那一组（组名 flows-manage）-----------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Manage = new(
        () => "生活模式管理通过：重名当场拦住并把撞上那一条交出去、盖回去写进同一个文件同一个号、" +
              "删掉之后下一个新生活模式拿更大的号、「我现在在哪个生活模式」那三档各判一次",
        "生活生活模式管理", ManageSelfTest);

    /// <summary>盖回去 / 删掉 / 重名当场拦住并指向覆盖 / 删掉之后编号不复用 /
    /// 「我现在在哪个生活模式」那三档。
    ///
    /// 全跑在 .tmp 里自己那个用完就删的子目录，一条都不碰 %APPDATA%
    /// （<c>CODING_STANDARDS.md</c>「写进文档的东西」那一节）。判据里**不数项、不抄键名**：
    /// 要比的那几条键从 <see cref="Settings.Declarations"/> 问出来。
    ///
    /// 每一条都配了对照（「一定该被动到」那一侧，见 <c>CODING_STANDARDS.md</c> 第 5 条）：
    /// 重名要配「没撞名的那个名字照样放行」，删掉要配「别的两条一个字节没动」，
    /// 位置那一档要配「刚存下的那一个被认出来」。</summary>

    public static List<string> ManageSelfTest()
    {
        var checks = new Checks("lifemodes-manage");
        var root = checks.Root!;

        checks.Check("重名当场拦住，并把撞上那一条交出去（界面拿它指向「覆盖」）", () =>
        {
            var dir = Path.Combine(root, "dup");
            Directory.CreateDirectory(dir);
            var started = Started();
            SettingsStore.Save(dir, new SettingsStore.Read { Current = started });
            var (err, index) = SaveAs(dir, "写作", started);
            if (err != null) throw new Exception($"第一次存没成：{err}");
            var path = PathOf(dir, index!.Entries[0]);
            var before = File.ReadAllText(path);

            var again = SaveAs(dir, "写作", started);
            if (again.Error == null) throw new Exception("重名被收了");
            if (!again.Collided) throw new Exception("重名那一次没把「撞上的是哪一条」交出来");
            if (again.Taken!.File != index.Entries[0].File) throw new Exception("交出来的那一条不是撞上的那一条");
            if (File.ReadAllText(path) != before) throw new Exception("重名那一次把原来那份改了");
            if (LifeModeIndex.Load(ListPathOf(dir)).Entries.Count != 1) throw new Exception("重名那一次名单多了一条");

            // **对照**：同一个名单里没撞名的那个名字必须放行 —— 否则这一条可能只是「谁都不让存」。
            var other = SaveAs(dir, "画画", started);
            if (other.Error != null) throw new Exception($"没撞名的那个名字也被拒了：{other.Error}");
            if (other.Collided) throw new Exception("没撞名的那个名字也被说成撞了");
        });

        checks.Check("盖回去：同一个文件、同一个号、名单一条不多，而内容是递进来的那一份", () =>
        {
            var dir = Path.Combine(root, "over");
            Directory.CreateDirectory(dir);
            var started = Started();
            SettingsStore.Save(dir, new SettingsStore.Read { Current = started });
            if (SaveAs(dir, "写作", started).Error != null) throw new Exception("存第一个没成");
            var second = started.Clone();
            second.FadeInMs = 9_000;
            second.Calendar.Corner = "bottomLeft";
            if (SaveAs(dir, "画画", second).Error != null) throw new Exception("存第二个没成");

            var before = LifeModeIndex.Load(ListPathOf(dir));
            var entry = before.Find("写作")!;
            var text = File.ReadAllText(PathOf(dir, entry));
            var over = Overwrite(dir, "写作", second);
            if (over.Error != null) throw new Exception($"盖回去没成：{over.Error}");
            if (over.Taken == null) throw new Exception("盖回去没把「盖的是哪一条」交出来");

            var after = LifeModeIndex.Load(ListPathOf(dir));
            if (after.Entries.Count != before.Entries.Count) throw new Exception("盖回去之后名单的条数变了");
            if (after.Find("写作")!.File != entry.File) throw new Exception("盖回去之后那条指的是别的文件 —— 那是新发了一份，不是盖");
            if (after.NextNumber != before.NextNumber) throw new Exception("盖回去却发了一个号");
            if (File.ReadAllText(PathOf(dir, entry)) == text) throw new Exception("盖回去之后那个文件一个字节没变");
            // **对照**：盖进去的是递进来的那一份，不是原来那一份。
            if (StandingOf(dir, second, after).At != Standing.In) throw new Exception("盖回去之后，递进来的那份和那个生活模式还是不一致");
            if (StandingOf(dir, started, after).At == Standing.In) throw new Exception("盖回去之后，原来那份还和那个生活模式一致 —— 盖的其实是另一份");

            // 名单里没有的那个名字盖不成：一条都不许新发出来。
            var nope = Overwrite(dir, "没有过这一条", second);
            if (nope.Error == null) throw new Exception("名单里没有的名字也盖成了");
            if (LifeModeIndex.Load(ListPathOf(dir)).Entries.Count != after.Entries.Count) throw new Exception("盖不成却动了名单");
        });

        checks.Check("删掉一个生活模式：名单和磁盘都少一条，别的两条一个字节没动，下一个新生活模式拿更大的号", () =>
        {
            var dir = Path.Combine(root, "del");
            Directory.CreateDirectory(dir);
            var started = Started();
            SettingsStore.Save(dir, new SettingsStore.Read { Current = started });
            foreach (var name in new[] { "一", "二", "三" })
                if (SaveAs(dir, name, started).Error != null) throw new Exception($"存「{name}」没成");

            var before = LifeModeIndex.Load(ListPathOf(dir));
            // 删**号最大的那一条**，不是中间那一条：号发回去的实现（「删完把 NextNumber
            // 拨回剩下的最大值 + 1」）只有在这一种删法下才露得出来 —— 删中间那条时，
            // 名单里剩下的更大号会把新号顶上去，两边看着都是「没复用」。
            var top = before.Find("三")!;
            if (!LifeModeNumbers.TryNumberIn(top.File, out var wasNumber)) throw new Exception($"{top.File} 读不回成号");
            var path = PathOf(dir, top);

            var (err, after, left) = Delete(dir, "三");
            if (err != null) throw new Exception($"删不掉：{err}");
            if (left != null) throw new Exception($"名单里已经没有它了，可是 {left}");
            if (File.Exists(path)) throw new Exception("名单里少了一条，磁盘上那份还在");
            if (after!.Entries.Count != before.Entries.Count - 1) throw new Exception($"名单该剩 {before.Entries.Count - 1} 条，实际 {after.Entries.Count}");
            // **对照**：别的两条一个字没动 —— 不然这一条可能只是「名单被清空了」。
            foreach (var keep in new[] { "一", "二" })
                if (after.Find(keep)?.File != before.Find(keep)?.File)
                    throw new Exception($"删掉「三」把「{keep}」也动了");

            // **编号永不复用**：下一个新生活模式拿的比刚删掉的那个号大。
            if (SaveAs(dir, "又一个", started).Error != null) throw new Exception("删完之后新存没成");
            var fresh = LifeModeIndex.Load(ListPathOf(dir)).Find("又一个")!;
            if (!LifeModeNumbers.TryNumberIn(fresh.File, out var nowNumber) || nowNumber <= wasNumber)
                throw new Exception($"删掉 {top.File}（{wasNumber} 号）之后新发的号是 {fresh.File} —— 号被复用了一次");
        });

        checks.Check("「我现在在哪个生活模式」那三档：没有 / 在某一个里 / 不在任何生活模式里", () =>
        {
            var dir = Path.Combine(root, "where");
            Directory.CreateDirectory(dir);
            var started = Started();
            SettingsStore.Save(dir, new SettingsStore.Read { Current = started });

            // 一个生活模式都还没有 —— 界面上写的是「还没有生活模式」，不是「不在任何生活模式里」。
            if (StandingOf(dir, started).At != Standing.None) throw new Exception("一个生活模式都没有，却说站在某一个里");

            var (err, index) = SaveAs(dir, "写作", started);
            if (err != null) throw new Exception($"存没成：{err}");
            // **对照**：刚存下的那一个必须被认出来 —— 否则「不在任何生活模式里」永远是绿的。
            var spot = StandingOf(dir, started);
            if (spot.At != Standing.In) throw new Exception($"刚存下的那一个没有被认出来（{spot.At}）");
            if (spot.Name != "写作") throw new Exception($"认出来的是 '{spot.Name}'，该是 '写作'");

            // 拧一个数 —— 只改一份副本，磁盘上那个文件一个字不动。
            var turned = started.Clone();
            turned.ActivityTimeoutMin = started.ActivityTimeoutMin + 7;
            if (File.ReadAllText(PathOf(dir, index!.Entries[0])) == "") throw new Exception("那个文件是空的，这一发量的不是那件事");
            if (StandingOf(dir, turned).At != Standing.Elsewhere)
                throw new Exception("手上这份和生活模式那一份已经不一样了，却还说站在那一个里");

            // 比的是值不是字节：用户自己加的一行注释，不该把他踢出这个生活模式。
            var edited = PathOf(dir, index.Entries[0]);
            File.WriteAllText(edited, "// 我自己加的一行\n" + File.ReadAllText(edited));
            if (StandingOf(dir, started).At != Standing.In) throw new Exception("生活模式那一份上多了一行注释，就说不在这儿了");

            // 域外那几条不参与比较：它们是这台机器的属性，不装在生活模式里。
            var mine = started.Clone();
            mine.Language = started.Language == "en" ? "zh" : "en";
            if (StandingOf(dir, mine).At != Standing.In) throw new Exception("只改了域外那几条，却说不在这个生活模式里了 —— 域界线不成立");
        });

        return checks.Report();
    }

    // ---- 自检：切换那一组（组名 flows-switch）-----------------------------------

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Switch = new(
        () => "切生活模式通过：生活模式那一份是一份完整的配置文档且不装系统那几条、切过去之后其余每一键都成了它那份值、" +
              "系统那几条逐字节没动、备份在、切回来还是原来那份；空名字 / 全空格 / 重名 / 名单读不通当场不给",
        "切生活生活模式", SwitchSelfTest);

    /// <summary>存 → 切 → 切回来，跑在自己那个用完就删的临时目录里。
    ///
    /// 判据分两头夹：<b>域外那三条一个字节都没动</b>（不是「值一样」，是活文档里那三行逐字节
    /// 相同），<b>其余每一键都成了生活模式里的值</b>（不是「有一个键变了」），被换掉的那份在
    /// <c>.bak</c> 里，切回来还是原来那份。
    ///
    /// 判据里**不数键、不抄键名**：要留住的那些从 <see cref="Settings.Declarations"/> 里
    /// <see cref="InLifeMode"/> == false 的那几条来，范围要换的那些从同一张表里来 —— 加一条系统级
    /// 设置，这几条判据自动跟着它走，而一份手抄的清单会静静地过期（<c>CODING_STANDARDS.md</c> 第 6 条）。</summary>

    public static List<string> SwitchSelfTest()
    {
        var checks = new Checks("lifemodeswitch");
        var dir = checks.Root!;
        var live = Path.Combine(dir, "live");
        Directory.CreateDirectory(live);

        checks.Check("存为新生活模式 → 切过去 → 切回来", () =>
        {
            var first = Started();
            // 先把活的那一份落盘 —— 后面量的「切之前那份原文」就是它。
            SettingsStore.Save(live, new SettingsStore.Read { Current = first });
            var before = File.ReadAllText(SettingsStore.FilePath(live));
            var state = SettingsStore.Load(live);
            if (state.Error != null) throw new Exception($"底稿读不通：{state.Error}");

            var (err, _) = SaveAs(live, "😀 画画 🎨", first);
            if (err != null) throw new Exception($"存第一个生活模式没成：{err}");
            var second = first.Clone();
            second.FadeInMs = 9_000;
            second.Calendar.Corner = "bottomLeft";
            second.Calendar.Enabled = false;
            second.Flows["fadetop"].Steps[0].Ms = 1_000;
            // 域外那三条**故意设成和活的那一份不一样的值**（这正是一个在别的机器上存出来的
            // 生活模式会带着的那种值）。于是「切过去之后那三行没动」这一条能失败：域界线一坏，
            // 这三个值就跟着搬进活文档，那三行当场变了 —— 只在两个值相同时量，它是空过的。
            second.Language = "zh";
            second.UiTheme = "light";
            second.Autostart = false;
            var (err2, _) = SaveAs(live, "写作", second);
            if (err2 != null) throw new Exception($"存第二个生活模式没成：{err2}");
            // **从磁盘上重新读一遍**名单：上面两个返回值是两次各自的那一份（第二次是在第一次
            // 的基础上加的），拿它们数条目数的是第一次那一份 —— 量的不是「存完磁盘上有几条」。
            var index = LifeModeIndex.Load(ListPathOf(live));
            if (index.Entries.Count != 2) throw new Exception($"名单该有 2 条，实际 {index.Entries.Count}");

            // 生活模式那一份 = 一份完整的配置文档，而且系统那几条不写在里面。
            foreach (var entry in index.Entries)
            {
                var path = PathOf(live, entry);
                var doc = Parsed(path);
                if (doc == null) throw new Exception($"{entry.File} 读不通");
                if (doc.DocumentVersion != Settings.SchemaVersion) throw new Exception($"{entry.File} 没带格式版本");
                foreach (var systemKey in OutOfLifeModeKeys())
                    if (Pick(doc.Values, systemKey) != null)
                        throw new Exception($"{entry.File} 里写着系统那一条 {systemKey} —— 它不装在生活模式里");
                if (Pick(doc.Values, "fadeInMs") == null) throw new Exception($"{entry.File} 不像一份完整的配置文档");
            }

            // 切到第二个生活模式
            var (err3, after) = SwitchTo(live, state, "写作");
            if (err3 != null) throw new Exception($"切换没成：{err3}");
            var afterText = File.ReadAllText(SettingsStore.FilePath(live));
            if (SettingsStore.DiffOf(first, after!.Current) == "")
                throw new Exception("切过去之后活的那一份和原来一模一样：那条生活模式压根没换上");

            // 其余每一键都成了生活模式里的值 —— 对的是**文档**，不是内存里那几个属性。
            var modeDoc = Parsed(PathOf(live, index.Entries[1]))!;
            var liveDoc = Parsed(SettingsStore.FilePath(live))!;
            var swapped = 0;
            foreach (var path in SettingsStore.DocumentPaths(liveDoc.Values)
                         .Where(p => !OutOfLifeModeKeys().Contains(p) && p != Settings.ModesKey))
            {
                var got = Pick(liveDoc.Values, path);
                var want = Pick(modeDoc.Values, path);
                if (got == null || want == null || !JsonNode.DeepEquals(got, want))
                    throw new Exception($"切过去之后活文档里 {path} 是 {got?.ToJsonString()}，" +
                                        $"生活模式那一份是 {want?.ToJsonString()} —— 没换成");
                swapped++;
            }
            if (swapped == 0) throw new Exception("一条键都没量到，这一发量的不是「整份换掉」");

            // 系统那三条：一个字节都没动。**对的是那几行原文**，不是值相等。
            foreach (var key in OutOfLifeModeKeys())
            {
                var was = LineOf(before, key);
                if (was.Length == 0)
                    throw new Exception($"活文档里找不到 {key} 那一行，这一发量的不是那件事");
                var now = LineOf(afterText, key);
                if (now != was)
                    throw new Exception($"切过去之后系统那一条 {key} 变了：'{was.Trim()}' → '{now.Trim()}'");
            }
            // 休息流程那张表也在生活模式里（设计文档 `docs/design/life-mode.md`「连同那张休息流程表全部进」）。
            if (after.Current.Flows["fadetop"].Steps[0].Ms != 1_000)
                throw new Exception("切过去之后那张休息流程表还是原来那份");

            // 被换掉的那份留下备份，而且就是切换之前那份原文
            if (!File.Exists(SettingsStore.BackupPath(live))) throw new Exception("切换没留下 .bak");
            if (File.ReadAllText(SettingsStore.BackupPath(live)) != before)
                throw new Exception("留下的那份不是切换之前那份活文档");

            // 切回来还是原来那份
            var (err4, back) = SwitchTo(live, after, "😀 画画 🎨");
            if (err4 != null) throw new Exception($"切回来没成：{err4}");
            var diff = SettingsStore.DiffOf(first, back!.Current);
            if (diff.Length > 0) throw new Exception($"切回来之后不是原来那份：{diff}");
        });

        checks.Check("空名字和全是空格的名字当场不给，磁盘一个字节都不动", () =>
        {
            var started = Started();
            SettingsStore.Save(live, new SettingsStore.Read { Current = started });
            SaveAs(live, "先存一个", started);      // 先存一个：子目录在，好数它里面有几个文件
            var before = Directory.GetFileSystemEntries(DirOf(live)).Length;
            foreach (var name in new[] { "", "   ", "\t" })
            {
                var (err, index) = SaveAs(live, name, started);
                if (err == null) throw new Exception($"「{name}」这个名字被收了");
                if (index != null) throw new Exception("名字没过，名单却还回来了");
            }
            if (ValidateName(new string('x', LifeModeStore.MaxNameLength + 1)) == null)
                throw new Exception($"满 {LifeModeStore.MaxNameLength + 1} 个字符的名字被收了");
            if (ValidateName(new string('x', LifeModeStore.MaxNameLength)) != null)
                throw new Exception($"正好 {LifeModeStore.MaxNameLength} 个字符的名字被拒了");
            if (Directory.GetFileSystemEntries(DirOf(live)).Length != before)
                throw new Exception("名字没过，生活模式那一份却写出去了");
        });

        checks.Check("重名当场拦住，不新发一份也不改原来那条", () =>
        {
            var dir3 = Path.Combine(dir, "duplicate");
            Directory.CreateDirectory(dir3);
            var started = Started();
            SettingsStore.Save(dir3, new SettingsStore.Read { Current = started });
            var (err, index) = SaveAs(dir3, "写作", started);
            if (err != null) throw new Exception($"第一次存没成：{err}");
            var path = PathOf(dir3, index!.Entries[0]);
            var before = File.ReadAllText(path);
            var (err2, again) = SaveAs(dir3, "写作", started);
            if (err2 == null) throw new Exception("重名被收了");
            if (again != null) throw new Exception("重名，名单却还回来了");
            if (index.Entries.Count != 1) throw new Exception($"名单该还是 1 条，实际 {index.Entries.Count} 条");
            if (File.ReadAllText(path) != before) throw new Exception("重名那一次把原来那份改了");
        });

        checks.Check("名单读不通：这一族当成没有，磁盘上那份原样留着", () =>
        {
            var dir2 = Path.Combine(dir, "broken-list");
            Directory.CreateDirectory(dir2);
            var started = Started();
            SettingsStore.Save(dir2, new SettingsStore.Read { Current = started });
            var listPath = ListPathOf(dir2);
            Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
            var broken = "{ \"entries\": [ { \"name\": \"半截\", ";
            File.WriteAllText(listPath, broken);

            var (err, index) = SaveAs(dir2, "新的", started);
            if (err == null) throw new Exception("名单读不通还是存进去了");
            if (index != null) throw new Exception("名单读不通，名单却还回来了");
            if (File.ReadAllText(listPath) != broken) throw new Exception("读不通的那份被覆盖了");

            var (err2, _) = SwitchTo(dir2, SettingsStore.Load(dir2), "新的");
            if (err2 == null) throw new Exception("名单读不通却切成功了");
            if (Directory.EnumerateFiles(Path.GetDirectoryName(listPath)!)
                .Any(f => LifeModeNumbers.TryNumberIn(Path.GetFileName(f), out _)))
                throw new Exception("名单读不通却写出了一份生活模式那一份");
        });

        checks.Check("名单里有、磁盘上没有：照实说，不静悄悄补一个默认", () =>
        {
            var dir2 = Path.Combine(dir, "missing-file");
            Directory.CreateDirectory(dir2);
            var started = Started();
            var state = SettingsStore.Save(dir2, new SettingsStore.Read { Current = started });
            var (err, index) = SaveAs(dir2, "写作", started);
            if (err != null) throw new Exception($"存没成：{err}");
            File.Delete(PathOf(dir2, index!.Entries[0]));
            var before = File.ReadAllText(SettingsStore.FilePath(dir2));
            var (err2, _) = SwitchTo(dir2, state.State, "写作");
            if (err2 == null) throw new Exception("文件不在磁盘上却切成功了");
            if (File.ReadAllText(SettingsStore.FilePath(dir2)) != before) throw new Exception("没切成却动了活文档");
        });

        return checks.Report();

        // 域外那几条键：**从声明表问出来**，不是问 `InLifeMode`，也不是抄一份清单。
        // 这一处曾经写成 `!InLifeMode(d)`，而那正是被测的那条判据 —— 它一坏（域界线失效），
        // 这份名单就变成空的，于是「生活模式那一份不装系统那几条」和「切过去之后那几行没动」
        // 两条一起成了空过（2026-10-01 把它塞回去试过，当场没报）。判据的**事实**那一半
        // 要住在离事实最近的那一侧（声明表的域），被测的只是**行为**。
        static List<string> OutOfLifeModeKeys() =>
            Settings.Declarations
                .Where(d => !d.IsContainer && d.Domain == Settings.SystemDomain)
                .Select(d => d.Key).ToList();
    }

    /// <summary>一份和出厂那几个值都不一样、而且<b>哪一域都动过</b>的设置。
    /// 域外那几条特意给成「默认值之外的」：它们要是留在默认值上，任何一条「切过去之后
    /// 它没变」的判据都会白过（那正是把值设成非默认的原因）。</summary>
    private static Settings Started()
    {
        var settings = new Settings();
        settings.ActivityTimeoutMin = 40;
        settings.FadeInMs = 3_000;
        settings.Calendar.Corner = "topRight";
        settings.Calendar.Enabled = false;
        // 系统那三条：界面语言、设置窗自己的配色、开机启动。
        settings.Language = "en";
        settings.UiTheme = "dark";
        settings.Autostart = true;
        return settings;
    }

    private static JsoncDoc? Parsed(string path) =>
        JsoncDoc.TryParse(File.ReadAllText(path), out var doc, out _) ? doc : null;

    /// <summary>点分键在一个对象里的值（<c>calendar.corner</c> 走两层），
    /// 和 <c>SettingMap</c> 读值走的是同一种写法。</summary>
    private static JsonNode? Pick(JsonObject root, string dotted)
    {
        JsonNode? at = root;
        foreach (var part in dotted.Split('.'))
        {
            if (at is not JsonObject node || !node.TryGetPropertyValue(part, out at)) return null;
        }
        return at;
    }

    /// <summary>活文档里写着那个键的那<b>一整行原文</b>。词是从键名自己长出来的
    /// （<c>"key":</c>），不是手抄的一句搜索词 —— 手抄的那一份量的是当初那个副本
    /// （<c>CODING_STANDARDS.md</c> 第 6 条）。</summary>
    private static string LineOf(string text, string key)
    {
        var needle = "\"" + key + "\"";
        foreach (var line in text.Split('\n'))
            if (line.Contains(needle, StringComparison.Ordinal)) return line;
        return "";
    }
}
