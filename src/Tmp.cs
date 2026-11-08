using System;
using System.IO;
using Godot;

namespace Keeper;

/// <summary>临时产物的落点。<b>规矩本身</b>在 <c>CODING_STANDARDS.md</c>「临时产出的落点」那一节
/// （根目录 <c>.tmp</c>，低价值缓存再往下一层 <c>.tmp/cache</c>，都因为系统临时目录会被自动清理）；
/// 这一份只回答「这一发算出哪个目录」。<b>谁都要算这个路径就问它，不要自己拼
/// <c>Path.GetTempPath()</c></b> —— 自己拼的那一份不知道自己为什么落到那儿。</summary>
///
/// <para><b>取不到项目目录时退回系统临时目录</b>，那是打包后的那一份 exe：
/// 它身上没有 <c>project.godot</c>，<c>res://</c> 指的是 pck 而不是仓库，于是
/// <c>GlobalizePath("res://")</c> 给的是 exe 所在的那个目录（桌面）。让那一份把
/// <c>keeper-app.log</c> 写到桌面上新建一个 <c>.tmp\</c> 比退回系统临时目录更招人烦，
/// 而它那一份日志只在 <c>docs/packaging.md</c> 验产物时看一眼，不值得为它改规矩。</para>
internal static class Tmp
{
    /// <summary>临时产物的根：仓库根下的 <c>.tmp</c>。<b>建好了才交出去</b>，
    /// 所以调用方拿到的那个目录一定在。</summary>
    public static string Root => _root.Value;

    /// <summary>低价值缓存的落点：<c>.tmp/cache</c>。<b>删了不心疼的那一类</b>
    /// 放这儿，别的临时产出放 <see cref="Root"/> 底下，别混。</summary>
    public static string Cache => _cache.Value;

    private static readonly Lazy<string> _root = new(() => Directory.CreateDirectory(Resolve()).FullName);

    private static readonly Lazy<string> _cache =
        new(() => Directory.CreateDirectory(Path.Combine(_root.Value, "cache")).FullName);

    /// <summary>哪一份 <c>project.godot</c> 在，就说明 <c>res://</c> 指着仓库；否则那份程序不是从仓库跑的。</summary>
    private static string Resolve()
    {
        var dir = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
        if (dir.Length > 0 && File.Exists(Path.Combine(dir, "project.godot")))
            return Path.Combine(dir, ".tmp");
        return Path.GetTempPath();
    }
}
