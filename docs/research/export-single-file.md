# 单文件打包：这一族实测结果

问的是「Godot 4.7.2 mono 能不能把 Keeper 打成**一个** exe，以及引擎给不给那两把钥匙」。
2026-10-02 在本机量，答案在第一节，步骤在 [`../packaging.md`](../packaging.md)。

## 怎么量的

在 `%TEMP%\keeper-exp` 开了一份仓库副本（不带 `.godot/`），headless 导出四发。导出走引擎 CLI 的
`--headless --export-release`。

## 实测结果

| 实测结果 | 值 |
| --- | --- |
| 单文件产物 | **1 个文件，181.45 MB**（最小那份预设 190,263,392 字节） |
| 不开那两把钥匙的产物 | **188 个文件，181.4 MB**（`Keeper.exe` 104.35 MB + `Keeper.pck` 0.11 MB + `Keeper.dll` 0.75 MB + 185 个 .NET 运行时文件） |
| **只开 `embed_pck`、不开 `embed_build_outputs`** | **1 个文件，104.50 MB，里面没有 C# 那一半** —— `Keeper.dll` 既不在 exe 里也不在旁边 |
| 导出模板 `windows_release_x86_64.exe` | **104.4 MB** |
| 包里 `.ctex` 条数 | 9（8 张 checkbox + 项目图标；托盘那三张改 Keep File 之后不再产生 `.ctex`，改之前是 12） |
| PCK 进 exe 的证据 | exe 末尾 1024 字节里有 `GDPC` |
| 两个选项名从哪来 | 分块扫 `Godot_v4.7.2-stable_mono_win64.exe`（173 MB）里的字面量：`dotnet/embed_build_outputs`、`binary_format/embed_pck` |
| .NET 运行时从包里加载 | **能** —— 托盘 `NIM_ADD=True`、日历挂上 `SHELLDLL_DefView`、`--seconds 8` 到点自退、exit 0、stderr 空 |

模板那 104.4 MB 是这一族体积的地板：目标机装不装 .NET 都跟它无关，.NET 运行时是
`dotnet publish` 另外铺的（约 77 MB）。第三行那个 104.50 MB 几乎就是模板本身 —— 一份
**没有 C# 的 Keeper**，它比另两个产物都小，所以「看起来更小的那个」正是坏掉的那个。

## 两条假实测结果

**`include_filter` 配上了就以为源文件会进包 —— 不会。** `include_filter="locale/*.csv,
src/tray/*.png"` 配好之后导出发，产物还是 181.44 MB，stderr 里
`读不到 res://src/tray/idle.png` 和 `语言表有 1 条问题：res://locale/keeper.csv 不存在`
这两条一条不少。打包日志逐行看下来，包里只有 `res://src/tray/idle.png.import` 和那个
`.ctex`，**源文件从来没进去过**。真正动的是 `importer="keep"`：那四发改完之后产物
181.45 MB、stderr 干净。

**缺 `Keeper.sln` 时导出报的是 warning，不是 error。** 退出码 0，`[ DONE ] savepack`，
一切看着都对，但 C# 那一半静默没编译，产物目录里连 `Keeper.dll` 都没有。判断得看日志里
有没有 `Running dotnet publish`，或者产物里有没有 `Keeper.dll` —— **别看退出码**。

## 2026-10-05 补的几发（为了给双击那条路配判据）

问题：根目录那个 `export-to-desktop.bat` 要判断「这一发到底编译了 C# 没有」。
下面三条是配判据时量到的。

| 实测结果 | 值 |
| --- | --- |
| **判据要搜的那一段** | `Running dotnet publish`（`dotnet_publish_project` 搜不到，原因见下一节） |
| 缺 `Keeper.sln` 时日志里有没有 `Running dotnet publish` | **没有**；同一份日志里 `dotnet_publish`、`Publishing .NET project` 也都没有 |
| 缺 `Keeper.sln` 那一发的产物 | 111,475,888 字节 —— 又是那个**没有 C# 的 Keeper** |
| 缺 `Keeper.sln` 那一发的收尾两行 | `WARNING: Project export for preset "win-x64-single" completed with warnings.` |
| 把 `dotnet/embed_build_outputs` 关掉 | 日志里**有** `Running dotnet publish`、**没有** `mono/publish/x86_64/Keeper.dll`；产物 109,558,816 字节，外加一个 `data_Keeper_windows_x86_64` 目录 |
| 判据要两条，各拦一族 | 只搜 `Running dotnet publish` 拦的是**没编译**（缺 sln 那一族）；再搜 savepack 收没收 `mono/publish/x86_64/Keeper.dll` 拦的是**编译了但没进包**（`embed_build_outputs` 被关那一族）。只留前一条，坏的那种会被盖到桌面上，起来就崩 |
| 编辑器**关着**在**原地**导出 | **成功** —— `dotnet publish` 跑了，`[ DONE ] savepack`，产物 1 个文件 |
| 原地这一发的耗时 | 10.4 秒（含拷到桌面和全部判据；首发因为四个 `.import` 改了要重导入，量到的是几分钟） |
| 产物大小 | 190,486,880 字节（2026-10-02 那条 190,263,392 已经涨了，别拿它当期望值） |
| 第 7 步那四条判据（对**桌面**上那一份量的） | exit 0 / stderr 0 字节 / `托盘：起挂 NIM_ADD=True 图标=Idle`、`钩子装上了`、末行 `退出` / 产物目录 1 个文件 —— **四条全过** |
| `--data` 有没有隔离住 | `%APPDATA%\Keeper` 那四份文件的写入时间全在 8-23 到 10-2，一份没动 |

**照步名去搜，永远搜不到。** 引擎那行输出带 ANSI 转义码，步名被劈开：

```
[   0% ] ESC[90mESC[1mESC[7mdotnet_publishESC[0mESC[22mESC[27m_projectESC[22mESC[27m | ESC[0mESC[1mESC[39mRunning dotnet publish
```

所以 `findstr /c:"dotnet_publish_project"` 在**成功**的那份日志上也返回「没有」。
`Running dotnet publish` 是唯一没被转义码劈开的那一段。

**编辑器开着的时候在原地导出，仍然没量过**（下面「没量的」那条还在）。

## 导出那一发会往源目录里补文件

问的是「跑一次导出，`src/` 底下怎么多出 4 个 `.cs.uid`，`docs/screenshots/` 下怎么多出
一个 `.png.import`」。**不是导出把产物写错了地方** —— `--export-release` 起的是**编辑器**
（无头那一支），打包之前先跑编辑器的文件结构扫描，扫描时**给缺的一律补上**：
脚本缺 `<脚本名>.uid`，图片缺 `<图片名>.import`，都写在源文件旁边。
（那一发量的是引擎补 `.import` 这件事，与目录叫什么无关。`docs/screenshots/` 这个目录
2026-10-20 整个删掉了，那些帧 2026-11-08 也移出了仓库。）

| 实测结果 | 值 |
| --- | --- |
| 那 5 个文件的写入时间 | 全是 `2026-10-05 12:24:00`，**同一秒**，落在那一发导出的窗口里 |
| 版本库里已跟踪的 `src/*.cs.uid` | 41 个，写入时间 9-24 到 10-2 —— 一份都没被那一发改过（**只补它没有的**） |
| 为什么偏偏是那 4 个 `.cs` | `Dnd.cs` / `ForegroundProbe.cs` / `detection/Detection.cs` / `detection/Foreground.cs` 是最新的几个，编辑器从没扫过它们。45 个 `.cs` 里其余 41 个都早有 `.uid` |
| 为什么偏偏是那张 png | 那一发时 `docs/screenshots/` 下 50 张已跟踪的 png 里，只有 `options-dnd-zh.png` 没有 `.import`，兄弟文件全都有且都进了版本库（那个目录 2026-10-20 删掉了，帧 2026-11-08 移出了仓库）|
| 直接反验（删掉再导一发） | 引擎自己打了 `WARNING: Missing .uid file for path "res://src/detection/Detection.cs". The file was re-created from cache.`，两个文件都在导出发起后 6 秒重新出现 |
| `.cs.uid` 重新生成后内容 | **逐字节相同**（`uid://tch6o4gvbeqc` 原样回来，那句 "from cache" 就是它） |
| `.png.import` 重新生成后内容 | **不一样** —— 963 字节同大小，只差 `uid=`：旧 `uid://bbr8e2ftt46f5` → 新 `uid://bvdspfx850gdw` |
| `exclude_filter` 挡不挡得住扫描 | **挡不住**。`docs/*` 挡的是**进不进包**；同一发日志里还有 `reimport \| options-dnd-zh.png`，编辑器照样扫了它。那 16 MB 帧仍然不进 exe（包内 `.ctex` 仍是 9 个） |
| 改源码会不会换掉 `.uid` | **不会**。给 `src/App.cs` 加一行空注释再导一发（那一发走的是真编译），`src/App.cs.uid` 仍是 `uid://bue732c3jiifd`，20 字节，写入时间还是 09-25 10:42:53 |

**推论两条**：`.cs.uid` 删了会原样回来，也可以放心让引擎重建；`.png.import` 删了会换一个
uid —— 被场景按 uid 引用的图，删了它的 `.import` 就等于换了一个资源。这张 png 只是文档里的
实物帧，没有任何东西引用它，所以现在没坏。

**这些文件不进版本库**（2026-10-06 改的规矩：**换台机器能重新产出的都不进代码库**）。
`.uid` / `.import` / `.translation` 三族都上了 `.gitignore`，已经在库里的 266 份也
`git rm --cached` 了。**四份例外**是那三个托盘 png 加 `locale/keeper.csv`：它们的内容是
`importer="keep"`，**引擎自己造不出来**（它只会写默认的 `importer="texture"`），
所以属于配置不属于产物，少了它运行期读不到源文件（实测结果在 `../packaging.md` 第 4 步）。

## 产物在项目目录里起不来（2026-10-06 04:01）

问的是「按 `../packaging.md` 第 7 步验产物时那一发起来了没有」。答：桌面那份起得来，
`.scratch\build` 里那一份起不来 —— 同一份字节，差的是它从哪个目录跑。

| 实测结果 | 值 |
| --- | --- |
| 产物放在桌面上跑 | **起来** —— 退出码 0、stderr 0 字节、日志里 `托盘：起挂 NIM_ADD=True`、`钩子装上了`、末行 `退出` |
| 同一份 exe 拷到 `%TEMP%\neutral` 跑 | **起来** —— 退出码 0、stderr 0 字节 |
| 同一份 exe 在 `.scratch\build` 里跑（第一发） | **0xC0000005 访问违例**。stderr：`Condition "da->erase_contents_recursive() != OK" is true. at: _GodotSharpDirs`、`open_dynamic_library → ERR_FILE_NOT_FOUND`、`.NET: Failed to load hostfxr` |
| 同一份 exe 在 `.scratch\build` 里跑（后两发） | `.NET` 起来了，但写 `user://` 全被拒：`UnauthorizedAccessException: Access to the path ... todos.jsonc is denied` 和 `... keeper-app.log is denied`，stderr 4877 行，进程不会按时自退 |
| 同一时刻同一台机器上桌面那份 | 正常 —— 所以不是 `%TEMP%` 权限变了 |
| 跑完仓库的 `.godot\mono\temp` | 完好（242 个文件，`Keeper.dll` 在）—— 没有伤到开发那一套 |
| 根因 | **没查出来**。看起来是引擎按 exe 所在目录解析 .NET 那一套的临时目录时，撞上了项目自己那份 |

**所以第 7 步改成验桌面上那一份**（`../packaging.md` 第 7 步照着改了）。
这一条只是这一台机器上这一天的实测结果，根因没量过。

## 导出那一发什么时候不自己退出

问的是「那一发明明 savepack 打包打完了，怎么还等它退」。

| 实测结果 | 值 |
| --- | --- |
| 有两发挂着不退 | 日志里 `Started` 和 `[ DONE ]` 条数**对得上**（导出那一步是真做完了），进程 CPU **0.00 秒**，一直不退，超过 5 分钟 |
| 哪几发会这样 | 两次挂住的都是**真编译过 C#** 的那一发；但真编译也会正常退（另一发 48 秒自己退了），**不是每次必挂** |
| 同时活着的还有谁 | `dotnet` 进程（18 秒 CPU）。**推**：MSBuild 的常驻节点把管道写端攥着不放，引擎等的是管道 EOF —— **这一条是推的，没单独隔离验过** |
| 产物那时候完没完 | 完的。190,492,128 字节，字节数连着三次一样之后拷过去能起来 |
| 改源码和只顶 csproj 时间戳的差别 | 改 `.cs` 那一发日志里多一步 `update_scripts_classes`（共 5 步），只顶时间戳的那几发是 4 步 |

**所以等引擎退出不能作数**（退出码和「进程还在不在」都不算）。根目录那个
`export-to-desktop.bat` 改成两样当它做完：干净日志里出现 `[ DONE ] savepack`，
且产物的字节数连着三次一样；然后 `taskkill` 掉那个 `_console` 进程。
**只停 `_console` 那一支** —— 编辑器和日常那个 exe 不叫这个名字。
照这么改之后：18.4 秒 exit 0，桌面那份与产物逐字节相同，第 7 步四条判据全过。
（那个脚本在屏幕上给什么样、它走过的每一条路，见本文件「双击那条路的屏幕」那一节。）

### 配这条判据时碰到的三件事（每一件都把成功那一发报成了失败）

1. **`[ DONE ] savepack` 在原始日志里不是连着的**：`[ DONE ]` 和 `savepack` 中间夹着
   ANSI 码（`ESC[39m` + 空格 + `ESC[1m`）。`findstr /r` 的正则也跨不过去（那套正则太弱）。
   **改法**：把转义码去掉落一份干净的日志，判据搜干净那份。
2. **问「进程还在不在」别用 `tasklist`**：它把镜像名截到 **25 个字符**
   （`Godot_v4.7.2-stable_mono_`），而 Godot 那个名字 40 多个字，拿全名去 `find` 永远
   匹配不上 —— 照全名写的那一版每一轮都判成「引擎没了」，3.5 秒就报了假失败。
   `Get-Process -Name` 才准（pwsh 和 5.1 都验过）。`Keeper.exe` 只有 10 个字符，截不断，
   所以查它用 `tasklist` 没事。
3. **次序不能反**：**打完引擎常常立刻就退**。先问「进程还在吗」再问「打完没有」，
   那一发 22 秒就成功退出的好导出，被报成「引擎自己退了，日志里没有 savepack 打完那一行」
   —— 而那一次的日志里 4 步全 DONE，产物 190,492,640 字节好好地在那儿。
   **先看打完标记，再问进程存活。**

（这三条都是「判据里的搜索串和次序得在真日志上验过」——
[`../../CODING_STANDARDS.md`](../../CODING_STANDARDS.md) 取证方法论那几节讲的就是这个。）

## 双击那条路的屏幕（2026-10-06）

问的是「`export-to-desktop.bat` 跑起来之后，屏幕上能不能看出它还在跑」。那一发要
二十秒到几分钟，屏幕上原来只有一句「别关这个窗口」。2026-10-06 把它改成每 2 秒半
重画一次：一条计时器、引擎当前那一步、一根进度条。

**怎么量的**：不起真的引擎。拿 `cmd.exe` 复制一份改名当假引擎，让它照真日志的样子
往 stdout 慢慢写，产物字节数慢慢长，打完 `[ DONE ] savepack` 之后挂着不退。测试副本
只改三样：路径、假引擎、`pause`；探测、判据、判产物那几段是原文。

| 实测结果 | 值 |
| --- | --- |
| 重画一次隔多久 | 约 2.5 秒（一发探测约 0.4 秒，加上 `ping -n 3` 约 2 秒） |
| 计时器数的是哪一样 | 现实时间。数的是 `%TEMP%\keeper-export-start.txt` 的写入时间到此刻 |
| 那个 0 点什么时候落的 | 第一发探测的时候 |
| 引擎当前那一步从哪来 | 日志里最后一行 `[ NN% ] 步名 \| 说明` |
| 步名到中文 | 表里五个：`扫文件结构` `注册全局类` `扫动作` `编译 C#` `打包` |
| 表里没有的步名 | 原样打出来，不猜 |
| 假引擎跑完整条路 | 31 秒，退出码 0，产物 90,100,000 字节 —— **那是替身自己写的数，不是真产物** |

### 真引擎那一发（03:59）

在仓库里原地跑，编辑器关着，没有另一发 Keeper 在跑。

| 实测结果 | 值 |
| --- | --- |
| 整条路 | **32 秒**（脚本自己报的是第 31 秒），退出码 0 |
| 真产物 | 190,495,984 字节 |
| 桌面那一份和产物逐字节相同 | **是**，两边 SHA256 都是 `F86841E0…E66897` |
| 屏幕上真出现过的步 | `扫文件结构  16%`（底下是 `[####--------------------]` 和 `正在加载全局类名……`）、`编译 C#  0%`（底下是 `Running dotnet publish`） |
| `打包` 那一步在屏幕上露过面没有 | **那一发没有** —— 它在计时器走到 25 秒之前就过去了，落在探测的空档里 |
| 引擎进程有没有留下 | 没有，`taskkill` 停掉了。（跑完之后确实还有一个引擎进程，查了命令行是另一个仓库 `demoFactory` 起的，与这一发无关） |

### 走过的几条路

| 路 | 屏幕上最后一句 | 退出码 | 往桌面拷了吗 |
| --- | --- | --- | --- |
| 跑通 | `好了。桌面上那一份是刚才这一版。` | 0 | 拷了 |
| 引擎自己退了 | `4 失败：引擎自己退了，日志里没有 savepack 打完那一行。` | 1 | 没拷 |
| 等超时 | `4 失败：等了很久它也没打完。` | 1 | 没拷 |
| 那一发没编译 C# | `5 失败：这一发没编译 C#，产物是坏的。没往桌面拷。` | 1 | 没拷 |
| .NET 那一半没进包 | `5 失败：.NET 那一半没进包，产物起来就崩。没往桌面拷。` | 1 | 没拷 |
| 探测失灵 | `4 失败：探测失灵 —— 那一发 PowerShell 没起来。` | 1 | 没拷 |
| 有一发 Keeper 在跑 | `3 失败：有一发 Keeper 正在跑。` | 1 | 没拷 |
| 没有引擎 | `1 失败：没找到引擎。` | 1 | 没拷 |
| 没有解决方案 | `2 失败：仓库里没有 Keeper.sln。` | 1 | 没拷 |

### 改那个脚本时踩的五个坑

每一个都把对的那一发报成了坏的那一发。

1. **文件得是 CRLF**。写成 LF 之后批处理报的是 `'N' is not recognized ...`
   这种半截话 —— 块语句和标签在 LF 上定位不到。`.gitattributes` 里已经写着
   `*.bat text eol=crlf`。
2. **`for /f` 的命令里一个百分号都不能有**。子 cmd 把值里的 `%` 当成变量引用的
   开头，从那儿往后一整段被吃掉。表现是 PowerShell 报
   `The string is missing the terminator`。正则里要写百分号就用 `\x25`。
3. **`set` 要在它读的那个变量定下来之后**。`CONSOLE` 是第一样检查里 `for %%F`
   定下来的；探测代码写在文件顶上就取到空的 `Get-Process -Name ''`。
4. **行尾那个 `%` 会被吃掉**。`echo   %PCT%%` 打出来没有百分号。百分号单独放
   一个变量（`set "PC=%%"`），打的时候写 `%PCT%%PC%`。
5. **`goto` 从 `call` 进来的子程序跳出去，只跳一层**。在 `:probe` 里
   `goto probe-failed`，`:fail` 那个 `exit /b 1` 只退得掉 `:probe` 这一层，
   外面的循环接着跑，屏幕上的秒数冻在 0 上一直装还在跑。**失灵要判在调用点上。**

**没量的**：窗口最小化时标题栏上那个秒数看不看得见；重画那一下闪不闪；排版在真控制台
窗口里的样子。假引擎要往 stdout 写，不要自己开日志文件 —— 外层那个 `>` 重定向占着它，
替身自己再开一次就会撞上「文件正被另一进程使用」。

## 2026-10-21 补的几发（在仓库原地导的，编辑器开着）

问的是两件：产物现在多大，以及**编辑器开着的时候在原地导出会不会跟它抢**。

| 实测结果 | 值 |
| --- | --- |
| 单文件产物 | **284,558,168 字节 = 271.38 MB**（2026-10-02 记的 181.45 MB 已过期；2026-10-21 08:46 那一发 284,556,632 字节，差的是那一次改的代码） |
| 包里存的文件 | 614 个，其中 **486 个在 `.godot/` 下** |
| `.godot/` 磁盘分布 | `mono` 181.57 MB、`imported` 87.59 MB、`shader_cache` 2.42 MB、`editor` 0.38 MB |
| `imported/` 里的大头 | 126 个 `fader-*` 共 **73.68 MB** |
| 源 png 进包了吗 | **没有** —— 包里 `docs/` 下 0 个文件，`exclude_filter="docs/*"` 挡住了 |
| 导出耗时 | 23 秒 |
| 编辑器开着时原地导出 | **没抢** —— 产物正常，四条判据全过 |
| 第 7 步那四条判据 | 全过：exit 0、stderr 空、日志有托盘与钩子那两行、最后一行是「退出」、目录里 1 个文件 |

**体积是 `.godot/` 整个进了包。** `export_filter="all_resources"` 会把 `.godot/` 也算成资源，
而 `exclude_filter` 里只有 `addons/*, docs/*, *.md` —— `.godot/` 没被挡住。

**帧的转换产物跟着 `.godot/` 进了包，而源文件没有。**
当时 `docs/research/images/` 下那些帧的源 png 被 `docs/*` 挡住了，
但引擎给它们生成的 `.ctex` 落在 `.godot/imported/` 里 —— 那个路径不含 `docs/`，
于是绕过了排除。**`exclude_filter` 挡的是源文件路径，挡不住转换产物。**

**没动它。** 修法有两条：把那些 png 改成 Keep File（托盘那三张就是这么处理的，
在 `docs/packaging.md` 第 4 步），或者在 `exclude_filter` 里加 `.godot/*` ——
后者有让运行时加载不到贴图的风险，要单独验一轮。哪一条都还没做。

## 判据

- 单文件起没起来：看 `%TEMP%\keeper-app.log` 里有没有 `托盘：起挂 NIM_ADD=True`。
- **「起起来了」和「让路了」长得几乎一样** —— 都 exit 0、stderr 都是空的。分辨只能靠上面
  那一行托盘实测结果在不在（那一发让路的日志里只有 `单实例：另一发 Keeper 在跑，这一发让路`）。

## 没量的

- 单文件比文件夹版启动快还是慢。
- 换一台机器、一块显卡、一个 DPI 之后的启动实测结果。

**可复测**：照 [`../packaging.md`](../packaging.md) 的七步在另一份副本上走一遍，读上面
那几条实测结果；测量程序的开关和那条 `--` 分隔符的规矩写在 `src/App.cs` 开头的注释里。
