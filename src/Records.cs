using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Keeper;

/// <summary>Keeper 自己写的那几份文档在磁盘上的<b>名字</b>这一层。
///
/// 只有一件事住在这里：老名字那一份在首次启动逐字节改名成现名字。改名是搬文件不是重写文件 ——
/// <see cref="File.Move"/> 之后一个字节都不动，所以注释、空行、顺序、用户自己加的键全跟着走，
/// 这一发不需要懂得读任何一种格式。
///
/// 四份都归这一条（设计文档 `docs/design/config.md`「老名字逐字节改名」那一节：老版把这些文档叫 <c>settings.json</c>、
/// 新版第一次启动逐字节改名成带注释的那一份），
/// 加上记录那三份同名的老法）。记录那三份为什么也是 JSONC、代价是什么，见
/// <c>docs/adr/0002-settings-and-records.md</c>「记录借 JSONC 的读，不借它的写」那一节。
///
/// 两份都在的时候<b>哪一份都不许动</b>，但要说出来 —— 照改了名字的老键那一族的处置
/// （<see cref="SettingMap.RetiredKeys"/>：留着、不悄悄生效、要说出来，见 ADR-0002「陌生的键不许有代价」那一节）。</summary>
public static class Records
{
    /// (老名字, 现名字)。这一份清单是「哪些文件要改名」唯一的去处，界面上不出现。
    public static (string Legacy, string Current)[] Pairs =>
    [
        (SettingsStore.LegacyFileName, Settings.FileName),
        (History.LegacyFileName, History.FileName),
        (Todos.LegacyFileName, Todos.FileName),
        (CalendarNotes.LegacyFileName, CalendarNotes.FileName),
    ];

    /// (老名字, 现名字) 的<b>目录</b>，和 <see cref="Pairs"/> 同一趟里跑。
    ///
    /// 生活模式那几份住的那个子目录老版本叫 <c>flows</c>，现在叫 <c>lifemodes</c>。改名的缘由
    /// 不是「名字不好听」，是<b>两个字面撞过</b>：<c>flows</c> 同时也是配置里那张**休息流程表**的
    /// 键名（<see cref="Settings.ModesKey"/>），于是「一个生活模式」和「一张休息流程表」在磁盘上
    /// 顶着同一个词。改完之后这一族在磁盘上不撞了，配置那份文档的键一个字没动。
    public static (string Legacy, string Current)[] DirPairs =>
    [
        (LifeModeIndex.LegacyFolderName, LifeModeIndex.FolderName),
    ];

    /// <summary>首启那一趟：把还在老名字上的文档搬成现名字，逐字节。还回<b>要说给人听的那几句</b>
    /// —— 改成了什么、或者哪一份两份都在。空列表就是什么都没得搬（正常的新装和第二次启动）。
    /// <b>目录也一起搬</b>（<see cref="DirPairs"/>），纪律和文件那一半一样。</summary>
    public static List<string> RenameLegacy(string dir)
    {
        var said = new List<string>();
        if (!Directory.Exists(dir)) return said;

        foreach (var (legacy, current) in Pairs)
        {
            var from = Path.Combine(dir, legacy);
            var to = Path.Combine(dir, current);
            if (!File.Exists(from)) continue;

            if (File.Exists(to))
            {
                // 两份都在：不猜哪一份更新，也不覆盖 —— 覆盖是要抹掉一份真文档。
                said.Add($"{legacy} 和 {current} 两份都在：都不动，程序读 {current} 那一份。" +
                         $"{legacy} 要自己确认过没用了再删");
                continue;
            }

            try
            {
                var before = File.ReadAllBytes(from);
                File.Move(from, to);
                // 「逐字节」是契约里的词，所以要读到而不是推到：搬完立刻拿新名字那份的字节比一遍。
                var after = File.ReadAllBytes(to);
                if (!after.SequenceEqual(before))
                {
                    // 理论上到不了这一发（Move 不重写内容）。真到了就把搬回去，别让老名字那份丢在半路。
                    File.Move(to, from, overwrite: false);
                    said.Add($"{legacy} 改名成 {current} 之后字节不一样，已经搬回老名字：这一发没改名");
                    continue;
                }
                said.Add($"{legacy} 逐字节改名成了 {current}（{after.Length} 个字节，一个没动）");
            }
            catch (Exception e)
            {
                // 改名失败不是启动失败：老名字那一份还在原处，程序读不到现名字那一份就是「没有文档」，
                // 走首启那条路。说出来，别静默。
                said.Add($"{legacy} 没能改名成 {current}：{e.Message}");
            }
        }

        foreach (var (legacy, current) in DirPairs)
        {
            var from = Path.Combine(dir, legacy);
            var to = Path.Combine(dir, current);
            if (!Directory.Exists(from)) continue;

            if (Directory.Exists(to))
            {
                // 和文件那一半同一处置：两份都在时不猜哪一份更新。目录更不能合并 ——
                // 合并就是拿一份盖掉另一份。
                said.Add($"{legacy}/ 和 {current}/ 两份都在：都不动，程序读 {current}/ 那一目录。" +
                         $"{legacy}/ 要自己确认过没用了再删");
                continue;
            }

            try
            {
                // 目录没法「读出字节比一遍」，比的是**里面每一样东西的相对名和字节**：
                // 少一个文件、多一个文件、或者哪一个的内容动了，都算不是逐字节。
                var before = Manifest(from);
                Directory.Move(from, to);
                var after = Manifest(to);
                if (!Same(before, after))
                {
                    Directory.Move(to, from);
                    said.Add($"{legacy}/ 改名成 {current}/ 之后里面不一样，已经搬回老名字：这一发没改名");
                    continue;
                }
                said.Add($"{legacy}/ 逐字节改名成了 {current}/（{after.Count} 样东西，{Bytes(before)} 个字节，一个没动）");
            }
            catch (Exception e)
            {
                said.Add($"{legacy}/ 没能改名成 {current}/：{e.Message}");
            }
        }
        return said;
    }

    /// 目录里每一样东西：相对名 → 那一份的字节。空的子目录不进这张表 ——
    /// 目录改名搬的是整棵树，而 <c>Directory.Move</c> 不重写任何内容。
    private static SortedDictionary<string, byte[]> Manifest(string dir)
    {
        var out_ = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            out_[Path.GetRelativePath(dir, path)] = File.ReadAllBytes(path);
        return out_;
    }

    private static bool Same(SortedDictionary<string, byte[]> a, SortedDictionary<string, byte[]> b) =>
        a.Count == b.Count
        && a.All(pair => b.TryGetValue(pair.Key, out var other) && other.SequenceEqual(pair.Value));

    private static int Bytes(SortedDictionary<string, byte[]> a) => a.Values.Sum(v => v.Length);

    /// <summary>读的那一侧认哪一个名字：现名字不在而老名字还在，就读老名字那一份。
    ///
    /// 只给<b>只读的测量程序</b>用（<c>FadeRun</c> 那一发量的是淡入，不许动用户的文档）——
    /// 改名是 <see cref="RenameLegacy"/> 那一趟，住在启动路径里。少了这一发，一次都没启动过新版
    /// 的机器上，测量程序读回来的轨迹和清单是<b>空的</b>，而那看起来像「用户一次都没休息过」。</summary>
    public static string ReadPath(string dir, string current, string legacy)
    {
        var now = Path.Combine(dir, current);
        if (File.Exists(now)) return now;
        var was = Path.Combine(dir, legacy);
        return File.Exists(was) ? was : now;
    }

    /// <summary>这一组自己的绿话和红话，跟着判据住（工单 03）。</summary>
    internal static readonly CriterionOf Rename = new(
        () => "首启改名通过：老名字逐字节搬成现名字、两份都在都不动且说出来、改完名立刻读得回来；" +
              "目录那一半同样逐字节（相对名 + 内容）、两个目录都在时哪一个都不动、改完名那一族还读得到",
        "首启改名", SelfTest);

    /// 自检里的判据（组名 <c>rename</c>）。跑在临时目录里，一条都不碰 %APPDATA%。
    public static List<string> SelfTest()
    {
        var checks = new Checks("rename");
        var root = checks.Root!;

        checks.Check("四份老名字逐字节改名的现名字，老名字没了", () =>
        {
            var dir = Path.Combine(root, "all-legacy");
            Directory.CreateDirectory(dir);
            var bytes = new Dictionary<string, byte[]>();
            foreach (var (legacy, _) in Pairs)
            {
                // 每份内容都不一样，而且都带着注释 —— 逐字节要连那一行注释一起搬过去才算数。
                var text = $"// {legacy} 自己的说明\n{{ \"stamp\": \"{legacy}\" }}\n";
                var path = Path.Combine(dir, legacy);
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
                bytes[legacy] = File.ReadAllBytes(path);
            }

            var said = RenameLegacy(dir);
            if (said.Count != Pairs.Length)
                throw new Exception($"四份都该说一句，实际说了 {said.Count} 句：{string.Join("；", said)}");
            foreach (var (legacy, current) in Pairs)
            {
                if (File.Exists(Path.Combine(dir, legacy))) throw new Exception($"{legacy} 还留在原地");
                var moved = File.ReadAllBytes(Path.Combine(dir, current));
                if (!moved.SequenceEqual(bytes[legacy]))
                    throw new Exception($"{legacy} → {current} 不是逐字节：搬过去 {moved.Length} 个字节，原来 {bytes[legacy].Length} 个");
                if (!said.Any(s => s.Contains(legacy) && s.Contains(current)))
                    throw new Exception($"没说清 {legacy} 改成了 {current}");
            }
            // 第二趟：老名字都不在了，一句都不该再说（首启只改一次名）。
            if (RenameLegacy(dir).Count != 0) throw new Exception("第二次启动还在说改名");
        });

        checks.Check("两份都在时哪一份都不动，而且说出来", () =>
        {
            var dir = Path.Combine(root, "both");
            Directory.CreateDirectory(dir);
            var (legacy, current) = Pairs[0];
            File.WriteAllText(Path.Combine(dir, legacy), "{ \"who\": \"legacy\" }\n");
            File.WriteAllText(Path.Combine(dir, current), "{ \"who\": \"current\" }\n");
            var before = File.ReadAllText(Path.Combine(dir, legacy));
            var nowText = File.ReadAllText(Path.Combine(dir, current));

            var said = RenameLegacy(dir);
            if (said.Count != 1) throw new Exception($"两份都在该说一句，实际 {said.Count} 句");
            if (!said[0].Contains("两份都在")) throw new Exception($"那句没说清是两份都在：{said[0]}");
            if (File.ReadAllText(Path.Combine(dir, legacy)) != before) throw new Exception("老名字那份被动了");
            if (File.ReadAllText(Path.Combine(dir, current)) != nowText) throw new Exception("现名字那份被盖了");
        });

        checks.Check("只有现名字那份时什么都不做", () =>
        {
            var dir = Path.Combine(root, "current-only");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Pairs[0].Current), "{ }\n");
            var before = File.ReadAllText(Path.Combine(dir, Pairs[0].Current));
            if (RenameLegacy(dir).Count != 0) throw new Exception("没有老名字那份也说了话");
            if (File.ReadAllText(Path.Combine(dir, Pairs[0].Current)) != before) throw new Exception("现名字那份被改了");
        });

        // --- 目录那一半：生活模式那几份住的那个子目录（老名字 flows/ → 现名字 lifemodes/）---

        // 表空了就直接说「改名那一条没接上」，而不是让下面三发各自在 DirPairs[0] 上炸成
        // 数组越界 —— 那句话量的是数组的下标，不是这一族的首启改名（2026-10-01 反向验证时撞到过）。
        (string Legacy, string Current) DirPair() =>
            DirPairs.Length > 0
                ? DirPairs[0]
                : throw new Exception("生活模式那个子目录的改名不在 DirPairs 里 —— 这一族的首启改名没接上");

        checks.Check("老目录逐字节改成新目录：老目录没了、里面每一样东西的字节相同", () =>
        {
            var dir = Path.Combine(root, "dir-legacy");
            var (legacy, current) = DirPair();
            var old = Path.Combine(dir, legacy);
            Directory.CreateDirectory(Path.Combine(old, "sub"));
            // 每一份内容都不一样，而且都带着注释 —— 逐字节要连那一行注释一起搬过去才算数。
            File.WriteAllText(Path.Combine(old, "list.jsonc"),
                "// 名单自己的说明\n{ \"nextNumber\": 2 }\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(old, "0001.jsonc"),
                "// 第一份\n{ \"fadeInMs\": 9 }\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(old, "sub", "0002.jsonc"),
                "{ \"keep\": \"中文和 emoji 都在\" }\n", new System.Text.UTF8Encoding(false));

            var said = RenameLegacy(dir);
            if (Directory.Exists(old)) throw new Exception($"{legacy}/ 还留在原地");
            var fresh = Path.Combine(dir, current);
            if (!Directory.Exists(fresh)) throw new Exception($"{current}/ 没有搬出来");
            var want = Manifest(fresh);
            if (want.Count != 3) throw new Exception($"搬过去 {want.Count} 样东西，原来 3 样");
            if (!said.Any(s => s.Contains(legacy) && s.Contains(current)))
                throw new Exception($"没说清 {legacy}/ 改成了 {current}/");

            // 内容没变：逐字读回来对一遍（清单是 Manifest 自己量的，这里从磁盘重新读）。
            if (File.ReadAllText(Path.Combine(fresh, "0001.jsonc"))
                != "// 第一份\n{ \"fadeInMs\": 9 }\n") throw new Exception("0001.jsonc 搬过去内容不一样");
            if (File.ReadAllText(Path.Combine(fresh, "sub", "0002.jsonc"))
                != "{ \"keep\": \"中文和 emoji 都在\" }\n") throw new Exception("子目录里那一份搬过去不一样");

            // 第二趟：老目录不在了，一句都不该再说（首启只改一次名）。
            if (RenameLegacy(dir).Count != 0) throw new Exception("第二次启动还在说改名");
        });

        checks.Check("两个目录都在时哪一个都不动，而且说出来", () =>
        {
            var dir = Path.Combine(root, "dir-both");
            var (legacy, current) = DirPair();
            Directory.CreateDirectory(Path.Combine(dir, legacy));
            Directory.CreateDirectory(Path.Combine(dir, current));
            File.WriteAllText(Path.Combine(dir, legacy, "0001.jsonc"), "{ \"who\": \"legacy\" }\n");
            File.WriteAllText(Path.Combine(dir, current, "0001.jsonc"), "{ \"who\": \"current\" }\n");

            var said = RenameLegacy(dir);
            if (said.Count != 1) throw new Exception($"两份都在该说一句，实际 {said.Count} 句");
            if (!said[0].Contains("两份都在")) throw new Exception($"那句没说清是两份都在：{said[0]}");
            if (File.ReadAllText(Path.Combine(dir, legacy, "0001.jsonc")) != "{ \"who\": \"legacy\" }\n")
                throw new Exception("老目录那份被动了");
            if (File.ReadAllText(Path.Combine(dir, current, "0001.jsonc")) != "{ \"who\": \"current\" }\n")
                throw new Exception("现目录那份被盖了");
        });

        checks.Check("目录改名之后那一族还读得到（承重处：搬完不是一堆没人认的字节）", () =>
        {
            var dir = Path.Combine(root, "dir-then-read");
            var (legacy, _) = DirPair();
            Directory.CreateDirectory(Path.Combine(dir, legacy));
            var index = new LifeModeIndex { NextNumber = 2 };
            index.Add("写作");
            index.Save(Path.Combine(dir, legacy, LifeModeIndex.FileName));

            RenameLegacy(dir);
            // 程序自己认的是现名字那个目录：名单读得回来、那一条也在。
            var got = LifeModeIndex.Load(LifeModeStore.ListPathOf(dir));
            if (got.Unreadable) throw new Exception("改完名之后名单读不通");
            if (got.Entries.Count != 1 || got.Entries[0].Name != "写作")
                throw new Exception("改完名读回来的不是那一条");
        });

        checks.Check("目录还没有就什么都不是（不凭空造目录）", () =>
        {
            var dir = Path.Combine(root, "no-such-dir");
            if (RenameLegacy(dir).Count != 0) throw new Exception("目录不存在还报了东西");
            if (Directory.Exists(dir)) throw new Exception("改名那一发凭空造了目录");
        });

        checks.Check("读的那一侧认现名字：改完名立刻读得回来", () =>
        {
            var dir = Path.Combine(root, "then-read");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Todos.LegacyFileName),
                "[ { \"text\": \"倒杯水\", } ]\n", new System.Text.UTF8Encoding(false));
            RenameLegacy(dir);
            var todos = Todos.Load(Path.Combine(dir, Todos.FileName));
            if (todos.Unreadable) throw new Exception("改完名的清单读不通");
            if (todos.Items.Count != 1 || todos.Items[0].Text != "倒杯水") throw new Exception("改完名读回来的不是那一条");
        });

        checks.Check("只读的测量程序认哪个名字：老名字还在就读那一份，而且不许顺手改名", () =>
        {
            var dir = Path.Combine(root, "read-path");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Todos.LegacyFileName), "[ { \"text\": \"倒杯水\" } ]\n");

            var picked = ReadPath(dir, Todos.FileName, Todos.LegacyFileName);
            if (Path.GetFileName(picked) != Todos.LegacyFileName)
                throw new Exception($"现名字那份不在，该读老名字，实际读了 {Path.GetFileName(picked)}");
            if (Todos.Load(picked).Items.Count != 1) throw new Exception("读老名字那一份没把清单读回来");
            if (File.Exists(Path.Combine(dir, Todos.FileName)))
                throw new Exception("只读的一发把老名字那份改了名");

            // 两份都在：认现名字（启动那一趟已经改过名，老名字是残留）。
            File.WriteAllText(Path.Combine(dir, Todos.FileName), "[ ]\n");
            if (Path.GetFileName(ReadPath(dir, Todos.FileName, Todos.LegacyFileName)) != Todos.FileName)
                throw new Exception("两份都在时该认现名字");

            // 两份都不在：还回现名字那一条路径 —— 读它的是「文件还没有」那一档，不是「坏掉」。
            var empty = Path.Combine(root, "nothing-here");
            Directory.CreateDirectory(empty);
            if (Path.GetFileName(ReadPath(empty, Todos.FileName, Todos.LegacyFileName)) != Todos.FileName)
                throw new Exception("两份都不在时该指现名字");
        });

        return checks.Report();
    }
}
