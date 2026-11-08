# 打包：产出一个单文件 exe

一句话：**两个引擎选项 + 四样引擎之外要有的东西**。这七步不用手动走 —— 双击根目录的
`export-to-desktop.bat` 就够：它照这七步跑一遍，产物直接盖到桌面上同名那一个，
并且多带两条判据 —— 日志里没有 `Running dotnet publish`（没编译 C#），或者 savepack
没收 `mono/publish/x86_64/Keeper.dll`（编译了但没进包），它就不往桌面拷。
**那两种产物的退出码照样是 0**，所以退出码不作数（两条判据各自的实测结果在 research 那一份）。

导出那一发要二十秒到几分钟，所以那个脚本每两秒半重画一次屏幕：一条计时器、引擎当前
那一步、一根进度条。没有它分不清是在跑还是卡住了（实测结果在 research 那一份）。

产物是**一个** `Keeper.exe`，拷到哪台 Windows 上双击就能跑，
那台机器不需要装 .NET。下面逐步写的是每一步在做什么、缺了会怎样。

体积由什么构成、那两个选项在引擎二进制里的位置、这一族里两条假实测结果，都在
[`research/export-single-file.md`](research/export-single-file.md)。**这一份只写步骤。**

## 产物长什么样

| 预设 | 产物 | 起不起来 |
| --- | --- | --- |
| 不开第 5 步那两个选项 | 一个**文件夹**：引擎 exe + `Keeper.pck` + `Keeper.dll` + 一整套 .NET 运行时文件 | 起得来 |
| 开那两个选项 | **一个文件** | 起得来 |

体积与字节数那些实测结果在 [`research/export-single-file.md`](research/export-single-file.md)。

总字节数一样 —— 单文件换来的只是「只拷一个文件」，不是「更小」。
（只开其中一个会得到一份**没有 C# 的 Keeper**，那张表在第 5 步。）

## 第 1 步：引擎和模板在不在

```
Godot：~/Desktop/software/Godot_v4.7.2-stable_mono_win64/
模板：%APPDATA%\Godot\export_templates\4.7.2.stable.mono\   （要 mono 那一支，文件名带 .mono）
.NET ：SDK 8.0.425 和 9.0.318 都在（`dotnet build` 走 8.0 那一支就够）
```

**这一份是装机事实的正本**（复核阶段那条自检命令要用的引擎可执行文件是同一个，只是路径不写死）。

两处容易以为齐了其实没齐的：

- **模板要 `windows_release` 和 `windows_debug` 两套都在**（本机两套都有，各还带一份 `_console`）。
  缺一套时导出那一步才报，而报错不指着模板本身。
- **引擎目录里有两个 exe**，跑测量程序要 `_console.exe` 那一支 —— 只有它带 `--headless`。
  另一支是给托盘那个日常 exe 用的。

## 第 2 步：`Keeper.sln`

**为什么排第一位**：Godot 导出 C# 工程前要跑 `dotnet publish`，那一步要一份解决方案文件。
缺了它，**导出不会失败** —— 退出码 0、`[ DONE ] savepack`，C# 那一半静默不编译，出来的
exe 起来就崩。日志里是这么写的：

```
ERROR: Export .NET Project: This project contains C# files but no solution file
was found at the following path: ...\Keeper.sln
```

`Keeper.sln` 在仓库里（2026-10-07 起 —— 之前它在 `.gitignore` 上而 `packaging.md` 这一节说它在
仓库里，两份文件在打架，干净 clone 上会卡在这一步）。删了就按这两行造：

```
dotnet new sln -n Keeper
dotnet sln Keeper.sln add Keeper.csproj
```

**怎么知道成了**：导出日志里出现 `Running dotnet publish` 那一段。**判据不要搜
`dotnet_publish_project`** —— 引擎那行输出带 ANSI 转义码，步名被劈成 `dotnet_publish` +
转义码 + `_project`，照步名去搜永远搜不到（实测结果在 research 那一份）。开第 5 步那两个选项
之后产物目录里**没有** `Keeper.dll`（它进了包），所以只剩日志这一条判据。别看退出码。

## 第 3 步：定主场景

`project.godot` 的 `[application]` 段加一行：

```
run/main_scene="res://src/App.tscn"
```

缺了它，起来的 exe 第一行是
`Error: Can't run project: no main scene defined in the project.`。
平时在编辑器里按 F5 跑的是**当前打开的场景**，所以这条在编辑器里永远看不出来。

## 第 4 步：四个源文件改成 Keep File

`src/tray/idle.png`、`src/tray/fading.png`、`src/tray/disabled.png`、
`locale/keeper.csv` —— 这四个是**按源文件路径**读的（`Tray.Icon` 一族、`Lang.CsvPath`），
而它们都被引擎 import 成了资源（png → `.ctex`，csv → `.translation`）。导出包里只有转换
后的那一份和 `.import`，**源文件不在包里**，于是运行期 `Image.Load` 和读 csv 直接抛。

把这四个 `.import` 的内容换成：

```
[remap]

importer="keep"
```

**别用 `include_filter` 顶**。`include_filter="locale/*.csv, src/tray/*.png"` 配上去，
产物还是 181.44 MB，stderr 里那两条 C# 异常一条不少。改 `importer="keep"` 之后产物
181.45 MB、stderr 干净。（实测结果在 research 那一份）

**顺带记一笔**：csv 改 Keep 之后，引擎不再从它生成 `keeper.zh.translation` /
`keeper.en.translation`。运行期不读那两份（`Lang.Apply` 自己解析 csv 装进
`TranslationServer`），但它们留在仓库里从此不再更新 —— 删不删是他的事。

## 第 5 步：一份导出预设

那份导出预设在 `export_presets.cfg`（在仓库里）。全文：

```ini
[preset.0]

name="win-x64-single"
platform="Windows Desktop"
runnable=true
advanced_options=false
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter="addons/*, docs/*, *.md"
export_path=""
patches=PackedStringArray()
encryption_include_filters=""
encryption_exclude_filters=""
seed=0
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.0.options]

custom_template/debug=""
custom_template/release=""
debug/export_console_wrapper=0
binary_format/embed_pck=true
texture_format/bptc=true
texture_format/s3tc=true
texture_format/etc2_astc=false
binary_format/architecture="x86_64"
codesign/enable=false
codesign/timestamp=true
codesign/timestamp_server_url=""
codesign/digest_algorithm=1
codesign/description=""
codesign/custom_options=PackedStringArray()
application/modify_resources=true
application/icon="res://icon.svg"
application/console_wrapper_icon=""
application/icon_interpolation=4
application/file_version=""
application/product_version=""
application/company_name="Keeper"
application/product_name="Keeper"
application/file_description="Keeper"
application/copyright=""
application/trademarks=""
application/export_angle=0
application/export_d3d12=0
application/d3d12_agility_sdk_multiarch=true
ssh_remote_deploy/enabled=false
ssh_remote_deploy/host="user@host_ip"
ssh_remote_deploy/port="22"
ssh_remote_deploy/extra_args_ssh=""
ssh_remote_deploy/extra_args_scp=""
ssh_remote_deploy/run_script=""
ssh_remote_deploy/cleanup_script=""
dotnet/include_scripts_content=false
dotnet/include_debug_symbols=false
dotnet/embed_build_outputs=true
```

**两个关键选项**，其余都是默认值（列全是为了让这份文件能被 diff 看懂）：

- `binary_format/embed_pck=true` —— PCK 烤进 exe
- `dotnet/embed_build_outputs=true` —— `dotnet publish` 的**全部**产物（.NET 运行时 +
  `Keeper.dll`）收进 PCK

`exclude_filter` 那一行是**压体积的，别删**：`docs/` 下放进实物帧（png）时，
引擎会当成资源导进包（本机实测包里 9 个 `.ctex` = 8 张 checkbox + 项目图标；托盘那三张改
Keep File 之后不再产生 `.ctex`，那 16 MB 的帧也已被 `docs/*` 挡住）。`docs/research/images/`
那些帧 2026-11-08 已移出仓库，这条过滤仍然要留。

**两个选项要一起开，而且只开一个不是「少收一点」**（2026-10-02 本机各量过一发）：

| `embed_pck` | `embed_build_outputs` | 产物 |
| --- | --- | --- |
| 关 | 关 | 188 个文件 / 181.4 MB，起得来 |
| 开 | 关 | **1 个文件 / 104.50 MB，但里面没有 C# 那一半** —— `Keeper.dll` 既不在 exe 里也不在旁边，起来就崩 |
| 开 | 开 | 1 个文件 / 181.45 MB，起得来 |

104.50 MB 那个就是导出模板的 104.4 MB 加上一个小包 —— 一份**没有 C# 的 Keeper**。所以
`embed_build_outputs` 不是可选项。

.NET 的 `coreclr.dll` 是原生库，操作系统 loader 只能从磁盘映射 —— 能不能把它从包里拉起来，
是这套办法唯一真正的关口（2026-10-02 读到能，见 research 那一份）。

## 第 6 步：导出

```powershell
New-Item -ItemType Directory -Force .\.scratch\build | Out-Null
~\Desktop\software\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe `
    --headless --path . --export-release "win-x64-single" .\.scratch\build\Keeper.exe
```

产物落在 `.scratch/` 里是因为那个目录已经在 `.gitignore` 上。换别处也行，只要那个目录也在。
**这一步走引擎自己的 CLI**（`--headless --export-release`）。**编辑器的 CLI 和带界面的编辑器
是两个东西**：导出要的是前者，不装插件、不开窗口。

**这一发会顺手往源目录里补文件**：编辑器无头起来先扫一遍文件结构，脚本缺 `.uid`、图片缺
`.import` 的当场补在源文件旁边。2026-10-05 那一发补了 4 个 `src/*.cs.uid` 和 1 个
`docs/screenshots/*.png.import`（那一发时帧还在 `docs/screenshots/`，2026-10-20 搬到
`docs/research/images/`，2026-11-08 移出仓库；实测结果在 research 那一份）。`exclude_filter` 挡的是**进不进
包**，挡不住编辑器扫 —— 那 16 MB 帧当时也不进 exe。

## 第 7 步：按测量程序跑法验一次

别直接双击 —— 那一发会用真实的 `%APPDATA%\Keeper`，也不会自己退。按 `src/App.cs` 头上
那批开关跑：隔离数据目录、定时自退、注册表不动。

**验桌面上那一份，别验 `.scratch\build` 里那一份** —— 后者在项目目录里，
2026-10-06 本机连着三发都起不来（同一份 exe 拷到项目外就正常，实测结果在
research 那一份）。

**先 `cd` 到仓库根**，再跑下面那段 —— 落点改成仓库根的 `.tmp\`，不落系统临时目录
（理由在 `CODING_STANDARDS.md`「临时产出的落点」那一节）。

```powershell
$root = (Get-Location).Path
$data = Join-Path $root '.tmp\keeper-verify'
$err  = Join-Path $root '.tmp\keeper-export.err'
New-Item -ItemType Directory -Force $data | Out-Null
$p = Start-Process -FilePath "$env:USERPROFILE\Desktop\Keeper.exe" `
     -ArgumentList @('--','--data',$data,'--seconds','8') -PassThru `
     -RedirectStandardError $err
$p.WaitForExit(45000)
$p.ExitCode
```

**判据**（四条都要）：

1. `$p.ExitCode` = 0
2. `.tmp\keeper-export.err` 是**空的** —— `_Ready` 里抛的 C# 异常只走 stderr，不进应用日志
3. `%TEMP%\keeper-app.log` 末尾读得到 `托盘：起挂 NIM_ADD=True` 和 `钩子装上了`，最后一行是 `退出`
4. 产物目录里只有 1 个文件

判据 3 那份日志**留在 `%TEMP%`**，和别的不一样：桌面那一份 exe 身上没有 `project.godot`，
它那份程序没有仓库可落，退回系统临时目录（同一节里写着为什么）。验完那一发之后
系统临时目录被清理掉也不要紧 —— 这一份只是这一次验产物的凭据。

**这一步的两个坑，都是本机真栽过的**：

- `--` 是**分隔符**。少了它，`--data` / `--seconds` 会被引擎吃掉，测量程序参数一个也递不进去 ——
  程序照跑不误，只是不隔离、也不自退，看起来一切正常。
- **已经有另一发 Keeper 在跑时，这一发会「让路」**：照样 exit 0、stderr 也是空的，但日志里
  只有 `单实例：另一发 Keeper 在跑，这一发让路`，没有托盘那两行。判据 3 就是拿来分辨这两种的。
  非要并排量的开关是 `--anyway`（`src/App.cs` 头上写着它只在「同时量两发的实测结果」时用，
  代价是那几秒里托盘上有两颗图标）。

## 这份流程没量的那一块

上面那几发 2026-10-02 是在**仓库的一份副本**上导出的（`%TEMP%\keeper-exp`），因为量的时候
编辑器正开着 `.godot/`。**编辑器开着的时候在原地导出会不会跟它抢，本机没量过** —— 抢了就把
编辑器关掉再导。

**编辑器关着的时候在原地导出，量过了**（2026-10-05，走根目录那个
`export-to-desktop.bat`）：`dotnet publish` 跑了，`savepack` 走完，产物一个文件，
第 7 步那四条判据全过，stderr 空。实测结果在 research 那一份。

另外，这一族实测结果全部来自这一台机器、一块 `GTX 750`、125% 缩放。换个引擎版本要重量一遍。
