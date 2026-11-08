# gif 那一档：引擎认不认，和「装一个库」量下来的五条实测结果

2026-09-28 为回答「从 c# 那边直接安装一个库不行么」现量的。测量程序在 `%TEMP%\keeper-gif-spike\`
（两个临时工程：一份 GDScript、一份 `Godot.NET.Sdk/4.7.2` 的 C# 工程），这一轮删。

引擎运行时**没有** gif 解码器（`Godot.Image.LoadFromFile()` 对两份已知好的 gif 回 `null` +
`Error 15` = `ERR_FILE_UNRECOGNIZED`），而本地缓存里躺着三个装得了的解码器，**且 Godot 的 .NET
宿主把它们都加载起来了**。

**五条实测结果**：

1. **这台机器不联网**：`curl https://api.nuget.org/v3/index.json` → **HTTP 000**；可 `dotnet restore`
   照样成，因为 `~/.nuget/packages` 里已经有 `system.drawing.common`（4.7.0 / 4.7.3 / 8.0.10 / 8.0.20）、
   `sixlabors.imagesharp`（**只有 2.1.10**）、`magick.net.core` + `magick.net-q16-hdri-anycpu`
   （14.0.0 / 14.7.0 / 14.9.1）。
2. **判「引擎认不认」不能拿我自己写的坏文件判**：第一发用 PowerShell GDI+ 造的那几张是坏的
   （`test.bmp` → 30、`test.jpg` → 16 = `ERR_FILE_CORRUPT`），所以换成已知好的一份 —— 用 ImageSharp
   写两帧 gif（第 0 帧红、第 1 帧蓝，写出去读回来 `frames=2` 两帧像素各对），GDI+ 也照同样判据写一份
   `gdi2.gif`。这两份喂 `Godot.Image.LoadFromFile()`：**两份都回 `null` + `Failed to load image.
   Error 15`**；同一跑里 `a.png` / `a.jpg` / `a.bmp` 各读到 8x8，`save_webp()` 回 0 且读得回来 ——
   于是 15 和 16 是两件事：**16 是「认得格式但这份坏了」，15 是「根本没认领这个格式」**，gif 属于后者。
   `ClassDB.class_has_method("Image","save_gif") = false`（`save_png` 是 `true`），另一头
   `AnimatedTexture` 这个类是存在的（`class_exists` 为真）。顺带把「浏览…」那张文件框格式列表
   （jpg/jpeg/png/bmp/gif/webp）里另外几种判了一遍：**png / jpg / bmp
   各读到 8x8，webp 写得出也读得回（`save_webp()` 回 0），只有 gif 到不了**；jpeg 没单独测
   （和 jpg 同一个解码器）。
3. **装进来的库在引擎进程里加载得了**：临时 C# 工程 `dotnet build` 0 错误，`--headless` 跑起来打印
   `宿主里拿到 ImageSharp 类型 = SixLabors.ImageSharp`、`宿主里拿到 System.Drawing =
   System.Drawing.Common`。DLL 落在输出目录、走 `<工程>.deps.json` 那条探测，**不用改
   `runtimeconfig`**（Keeper 那份只写 `Microsoft.NETCore.App 8.0.0`，`rollForward=LatestMinor`），
   也不用 `Microsoft.WindowsDesktop.App` —— GDI+ 那侧 P/Invoke 的是系统的 `gdiplus.dll`。
   （那套共享框架这台机器上 3.1–10.0 都装着，但这条路没用它。）
4. **动画那一半只有 ImageSharp 到得了**：GDI+（`System.Drawing.Common` 8.0.20）对**两份**两帧文件都只
   读到 `frames=1`，取到的像素是**最后一帧** `(0,0,255)` —— 包括它自己刚写出来的那一份；为什么没查，
   **别顺着推「GDI+ 不支持动画」**。ImageSharp 读同一两份是 `frames=2` + 两帧像素各对。
5. 三条路的代价（都量了，不是估价）：`System.Drawing.Common` 8.0.20 = **9.5 MB** 缓存、nuspec 写
   `MIT`、到得了**不动画的那一帧**（本轮它给的是合成后的最后一帧 `(0,0,255)` 而不是首帧 ——
   **到底挑哪一帧没查，别按「首帧」写代码**）；`SixLabors.ImageSharp` 2.1.10 = **31 MB** 缓存、
   nuspec `Apache-2.0` 但 `requireLicenseAcceptance=true`、到得了**动画**（两帧都读对，是这三条里唯一
   读对帧数的），代价是它带 **`NU1902`（GHSA-rxmq-m78w-7wmc，中）**，而这台机器只能拿到 2.1.10 ——
   升不到 3.x；`Magick.NET-Q16-HDRI-AnyCPU` 14.9.1 = **320 MB** 缓存、
   `runtimes/win-x64/native/Magick.Native-Q16-HDRI-x64.dll` 在包里（离线装得了），但那是给一个常驻托盘
   的程序添一层几十 MB 的原生依赖。所以「装一个库」不是不行，
   是**换一层依赖进来**。

取向的先例和代价在 **ADR-0004「引擎解不了的格式」那一节**；上面这三条代价就是现成报价，谁想改口说一句就走。

（旧取向表里 gif 那一行曾长期承诺报价记在本文件，而那些数一直空着，2026-09-28 才补上。）

