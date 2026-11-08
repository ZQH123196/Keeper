using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DebugProbe;

/// <summary>
/// 产品那一份进程。跑源码时是引擎那个 exe，打包后是 <c>Keeper</c>。
///
/// <para>不写死句柄 —— 句柄每次启动都不一样，写死就绑死在一次运行上。
/// 这一族认的是<b>进程 + 窗口类 + 尺寸</b>三样，见 <see cref="Find"/>。</para>
/// </summary>
internal static class ProductProcess
{
    private static readonly string[] Names =
    {
        "Keeper",
        "Godot_v4.7.2-stable_mono_win64",
    };

    /// <summary>一个进程名。取不到就是取不到，不猜。</summary>
    internal static string NameOf(int processId)
    {
        try
        {
            using Process p = Process.GetProcessById(processId);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            // 枚举到的那一刻那个进程退了。这不是失败。
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    internal static bool IsProduct(int processId) =>
        Names.Contains(NameOf(processId), StringComparer.OrdinalIgnoreCase);

    /// <summary>现在在跑的产品进程。跑源码时可能有好几个进程组，这里一个都不漏。</summary>
    internal static List<int> RunningProductIds()
    {
        return Windows.TopLevelWindows()
            .Select(w => w.ProcessId)
            .Where(IsProduct)
            .Distinct()
            .ToList();
    }
}