# 检测模块的 API 取证（网页一手来源）

**这一份是网页取证**：结论来自 Microsoft Learn 文档、Stack Exchange / GitHub 的一手帖子，逐条带出处。**不是本机 Probe 实测结果**——Probe 待测清单在文末第 6 节，本机实测结果出来后补记到对应各节。取证方式：web_fetch 直抓（SO 问题页本体被 Cloudflare 拦，正文经 Stack Exchange API `filter=withbody` 取得，引用 URL 用 SO 页面地址）；raw.githubusercontent.com、web.archive.org、eyeleo.com、help.dejal.com 不可达，那几家查不到的一律明说。服务日期：2026-10-04。对应取向在 `docs/adr/0006-detection-module-boundary.md`。

## 1. 前台进程身份

标准链路：`GetForegroundWindow` → `GetWindowThreadProcessId`（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)，返回线程 id，pid 是可选出参）→ `OpenProcess` → 取 exe 路径。

| | QueryFullProcessImageNameW | GetModuleFileNameEx |
|---|---|---|
| 所需权限 | `PROCESS_QUERY_INFORMATION` **或** `PROCESS_QUERY_LIMITED_INFORMATION`（[文档原文](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)） | `PROCESS_QUERY_INFORMATION` + `PROCESS_VM_READ`；Win10+ 且 hModule=NULL 时才降到 LIMITED（[文档原文](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getmodulefilenameexw)） |
| 可靠性 | 一次查询 | 文档自述可能失败或返回错误信息；**文档明确推荐改用 QueryFullProcessImageName**（"more efficient and more reliable"） |

**提权进程**：对管理员进程去掉 `PROCESS_VM_READ`、只用查询权打开即可成功——`PROCESS_QUERY_LIMITED_INFORMATION` 足以读提权进程的路径（SO [38450178](https://stackoverflow.com/questions/38450178/determine-a-processs-exe-path-including-elevated-processes) 采纳答案）。**别的用户会话**的进程：标准用户连 LIMITED 都会 ACCESS_DENIED，这是安全模型的按设计行为（SO [43389071](https://stackoverflow.com/questions/43389071/openprocess-error-access-denied-despite-process-query-limited-information)，David Heffernan 答）。

**UWP 那一族**：前台是计算器这类应用时，`GetForegroundWindow` 拿到的顶层窗（类名 `ApplicationFrameWindow`）属于 **ApplicationFrameHost.exe**，不是应用自己。两条解析路：① `EnumChildWindows` 找子窗里 pid 不同的那个（子窗类名 `Windows.UI.Core.CoreWindow`，即应用本体；SO [39702704](https://stackoverflow.com/questions/39702704/connecting-uwp-apps-hosted-by-applicationframehost-to-their-real-processes) 采纳答案，同型实现见 SO [32001621](https://stackoverflow.com/questions/32001621/how-to-get-the-application-name-from-hwnd-for-windows-10-store-apps-e-g-edge)）；② `GetGUIThreadInfo(0)`（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo)：idThread 传 NULL 返回前台线程信息）→ 取 `hwndFocus`/`hwndActive`。已知坑：UWP 全屏时 EnumChildWindows 会找错（RDP 全屏枚举到 SkypeApp，39702704 第三答）；GetGUIThreadInfo 在窗口失活瞬间可能给无效句柄（文档自带警告）。应用名的三条正路（GetPackageFullName（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname)，接受 LIMITED，无包身份回 `APPMODEL_ERROR_NO_PACKAGE`）、窗口 AUMID（`SHGetPropertyStoreForWindow` → `PKEY_AppUserModel_ID`，格式无文档）、WinRT `ProcessDiagnosticInfo`）都在 32001621 的答案里。**broker 进程（RuntimeBroker 等）没有应用身份**，从 pid 反推这条路对它不成立（同帖 Howard Kapustien 答案）。

**exe 基名的坑**：MSIX 执行别名装在 `%LOCALAPPDATA%\Microsoft\WindowsApps`（`wt.exe` 等 reparse 点，[Windows Terminal 文档](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments)、[AppExecutionAlias 扩展](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions)）——同名不同源会撞车。Stretchly 官方文档自述：Windows 上底层 API 只比对进程名、写路径无效、且是**子串匹配**（[hovancik.net/stretchly/about](https://hovancik.net/stretchly/about/)）——子串那一条我们不抄。

### 1.1 本机实测结果（2026-10-04，来源：本机 Probe 的 `--fg` 那一档，headless）

测量程序代码在 `src/ForegroundProbe.cs`，旗是 `--fg <秒>`，跑法在 `CODING_STANDARDS.md`「另外两发测量程序的跑法」那一节。**这一档跑 headless**：非 headless 时 Godot 会开一扇自己的主窗一起抢前台，读到的就变成「测量程序和目标谁在前台」。每发实测结果都带 `headless=` 那一栏，所以「那一发是哪种形状」不用回头猜。

**普通进程那一档通**：前台是记事本时，`GetForegroundWindow` → 类 `Notepad` → `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 打开成、错误码 0、`QueryFullProcessImageNameW` 读出 `C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe`。**注意这台机器上的记事本是装成商店包的**，而它的顶层窗类仍是 `Notepad`、pid 就是它自己 —— 「装成包」和「被 `ApplicationFrameHost` 托管」是两件事。

**系统进程那一档被拒（降级路径成立）**：`lsass` / `winlogon` / `wininit` / `services` 四个，`OpenProcess(LIMITED)` 一律回 `ACCESS_DENIED`（错误码 5），问不出 exe 路径。这比上面那段引的「别的用户会话」**更强一档**：发生在 **SYSTEM 身份**的进程上，标准用户连查询权都拿不到。降级路径因此是**三级**的：拿得到路径就用路径，拿不到但有 pid 就退到 pid，再没有就只剩窗口标题和类名。

**提权进程（管理员 cmd）这一档本轮量不到** —— 见第 6 节和工单 01 的 `## Comments`。**不拿普通进程那一档顶替**，两者的差别正是要量的东西。

**UWP：顶层那扇窗给的是错的答案，而且错得很稳。** 前台是商店版计算器时：顶层窗类 `ApplicationFrameWindow`，pid 属于 `C:\Windows\System32\ApplicationFrameHost.exe`，而 `GetPackageFullName` 对它回 `APPMODEL_ERROR_NO_PACKAGE`。所以「顶层 pid → exe 路径」在 UWP 上**不是问不出，是问出了别人的名字** —— 一个「查不到就跳过」的降级逻辑在这里不会触发，会安静地给出错的答案。

**两条 UWP 路并排，两条给出同一个句柄。** 枚举 `ApplicationFrameWindow` 的子窗（共 4 个）里 pid 不同于框架进程的那个 = `Windows.UI.Core.CoreWindow`，pid 11500，exe 是 `...\Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe\CalculatorApp.exe`；`GetGUIThreadInfo(0)` → `hwndFocus` = **完全同一个句柄**。在本机、在这一个应用上两条路没有分歧。**一处收发时序不成立**：那扇窗被最小化的那 155 发里，两条路都读不到东西（枚举子窗数从 4 掉到 3 且找不到异 pid 那个，焦点窗变成空）—— 与第 3 节那条「失活瞬间给无效句柄」是同一族，方向是**两个都给空**。

**窗 AUMID 要问顶层那扇窗，不要问本体窗。** 同一个 AUMID 在 `ApplicationFrameWindow` 上读得出来（`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`），在 `Windows.UI.Core.CoreWindow` 上读不出来。**「这一格空」要看得见是哪一种**：非 UWP 的窗（记事本、Tabby）那一格也是空，但 `SHGetPropertyStoreForWindow` 成功、只是那一项不存在 —— store 与取值两个码分开交，就是为了把「窗没有 AUMID」和「这一层没接上」分开。

**`GetPackageFullName` 的两发形状（测量程序自己栽过）**：第一发长度给 0、缓冲区给 NULL，回 `ERROR_INSUFFICIENT_BUFFER` 并把长度写回来；第二发给够长度才回成功。把第一发非 0 一律当失败，**包身份会整族读成「没有」** —— 2026-10-04 我自己的测量程序就栽在这一发上，实测结果是「记事本，包名码 122、包名空」，看着像「它没有包身份」，其实有。另外它的句柄是**目标进程自己的**那个：传 0 的文档语义是「问当前进程」，而测量程序要问的是前台那个 —— 传 0 会把测量程序自己（一个没有包身份的控制台 exe）的答案当成前台的答案，那个答案恒是「没有包」。

**CoreWindow 交叉点（本节与现有代码的真实交叉处）**：锁屏的前台窗**就是** `Windows.UI.Core.CoreWindow`（pid 31780，exe 在 `Microsoft.LockApp_...` 下，标题「Windows 默认锁屏界面」，矩形盖满整块屏）。它已经在 `src/Fullscreen.cs` 的 `ShellClasses` 里，而几何判定在锁屏期间实测给 **False**（可重复，连量十发都是 False）——「前台是不是 shell」这一问对锁屏**答得对**。但同一个类名在 UWP 场景下是**应用本体窗**，那一问会答错。**同一个类名，在「前台是不是 shell」这一问里要排除，在「前台是哪个应用」这一问里要采信** —— 这两个语义必须拆开，这一条是现有代码和检测模块的交叉点。

**几何判据和 QUNS 会不一致，别拿一个顶替另一个。** 浏览器全屏那一跑，QUNS 报 `QUNS_BUSY` 的同一时刻，`Fullscreen.OwnsScreen()` 报 **False** —— 因为那一刻 `GetForegroundWindow` 返回的是一扇**被最小化的**窗。两样量的是不同的东西（一个问系统状态、一个问那扇窗的矩形）。

**最小化的窗仍然可以是前台窗，而且顶不走。** 实测结果里出现过 `前台 = 某 'ApplicationFrameWindow' 可见=True 矩形=(-25600,-25600)-(-25441,-25573)`（最小化的经典位置），并且那一扇窗**谁都顶不走**：`SetForegroundWindow`（带 `AttachThreadInput` 那一套）对记事本、Chrome、设置一律回 False，`SwitchToThisWindow` 和 `LockSetForegroundWindow(LSFW_UNLOCK)` 都不行。所以「拿前台 pid 去问身份」这条路，在**切换刚发生的那一瞬**可能读到一扇已经不该在前台的窗 —— 实测结果要带「它此刻是不是被最小化」一起看。

**本机 SDK 头是签名的正本（这一族三条声明我全照记忆写错过）**。三条都在本机逐条读过：`SHGetPropertyStoreForWindow` 是**三个**参数、第二个就是 `REFIID`、**没有旗标**（我先按带旗标写、再「修」成四个参数，两次都是访问冲突，测量程序一发实测结果都没出来）；`SHQueryUserNotificationState` 是**一个**参数、**没有 hwnd**（所以「传 0 还是传前台那扇窗」那个对照**根本不存在**，别去设计它）；`GUITHREADINFO` 的末尾是 `RECT rcCaret` 而**不是第七个 HWND**（写错的话结构体偏小、`cbSize` 不对，`GetGUIThreadInfo` **每一发**都回 `ERROR_INVALID_PARAMETER`，包括对普通窗口 —— 看上去像「这一族在 Win11 上不通」，实际是自己的结构体错了）。另外 `QueryFullProcessImageNameW` 的 `CharSet` 必须给 Unicode，不给的话 `StringBuilder` 按 ANSI 传下去、W 那一支按宽字符写，缓冲区被写爆（测量程序直接访问冲突，路径只印出一个字符）。**写这一族的声明之前先读 `Windows Kits\10\Include` 下那两个头文件的对应行**，发数别抄进文档，也别照记忆改。


## 2. 前台变化：事件 vs 轮询

`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)）：回调线程 = 装钩子的线程，**该线程必须有消息循环**（文档原话）；`WINEVENT_OUTOFCONTEXT` 下事件同线程异步投递。经典翻车：不泵消息就收不到（SO [6855569](https://stackoverflow.com/questions/6855569/problem-in-capturing-active-window-changed-event)）。托管代码要用 GCHandle 钉住委托、防重入（文档有专节）。

已知坑（都有出处）：最小化双发、最大化不发、迟到（SO [41428985](https://stackoverflow.com/questions/41428985/winevent-event-system-foreground-invocation-sometimes-delayed)，复现报告）；服务进程里不回调（SO [25638164](https://stackoverflow.com/questions/25638164/setwineventhook-callback-does-not-work-when-app-is-a-service)）；锁屏解锁后同类钩子失灵（Workrave 1.11 在 Win11 上活动监控冻结、要重启才恢复：[rcaelers/workrave#588](https://github.com/rcaelers/workrave/issues/588)）。**提权窗口 / UAC 安全桌面切前台时事件是否被过滤：查不到一手证据**——必须进 Probe。CPU 开销对比无人量化（SO [41059514](https://stackoverflow.com/questions/41059514/performance-cost-of-setwineventhook-vs-polling-in-c) 有人问、无人答）。轮询的真实短板：快速 A→B→A 漏中间态、200 毫秒延迟；事件的真实短板：上述失灵场景。圈内实际做法是事件 + 低频轮询对账（Stretchly 的 DND 检查间隔 2000 毫秒，`monitorDndCheckInterval`，[官方文档](https://hovancik.net/stretchly/about/)）。

## 3. SHQueryUserNotificationState

QUNS 各值官方语义（[枚举文档](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)）：`QUNS_NOT_PRESENT` 屏保 / 锁定 / 非活动快速用户切换；`QUNS_BUSY`「A full-screen application is running or Presentation Settings are applied」；`QUNS_RUNNING_D3D_FULL_SCREEN` 明写**独占模式** D3D；`QUNS_PRESENTATION_MODE` 演示设置；`QUNS_APP`「A Windows Store app is running」（没说全屏）。函数页（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate)）两个关键 remarks：只在 `QUNS_ACCEPTS_NOTIFICATIONS` 时发 UI；**应用开始/退出全屏时没有任何通知**——这个 API 天生只能轮询。

已知不可靠：**全屏游戏回 `QUNS_BUSY` 而不是 D3D 值**（SO [1219454](https://stackoverflow.com/questions/1219454/detecting-full-screen-direct3d-applications) 采纳答案原话，2013 Win7 实测；提问者还注明该 API only works when the Explorer shell is running）。**无边框窗口化查不到专门的一手帖**——按官方语义推断大概率只触发 BUSY 启发式，是推断不是取证结论，必须实测。**Win11 勿扰不反映**（SO [76290200](https://stackoverflow.com/questions/76290200/windows-do-not-disturb-isnt-setting-notification-state)）。**Focus Assist 没有官方查询 API**（SO [53407374](https://stackoverflow.com/questions/53407374/is-there-a-way-to-detect-changes-in-focus-assist-formerly-quiet-hours-in-windo) 采纳答案：no officially documented way；社区路线 = 逆向工程的 WNF 状态 `WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED` + `NtQueryWnfStateData`，答案自警 not officially supported；Win11 另有 CloudStore 注册表 hack，作者自评 super unstable across Windows versions）。

**圈内最有力的一条**：Workrave 维护者不做自研全屏检测，让 Windows 在应用全屏时自动开 Focus Assist / DND，Workrave 挂 DND 联动（[rcaelers/workrave#410](https://github.com/rcaelers/workrave/issues/410) 关闭评论，rcaelers 本人，2023）。Workrave 1.11 已实现（[1.11.1 发布说明](https://workrave.org/2026/07/23/workrave-1-11-1-released/），对应 [#226](https://github.com/rcaelers/workrave/issues/226)/[#255](https://github.com/rcaelers/workrave/issues/255)）。

### 3.1 本机实测结果（2026-10-04，来源：本机 Probe 的 `--fg` 那一档，headless）

量法：`SHQueryUserNotificationState` 与 `Fullscreen.OwnsScreen()`（**产品自己那条几何判据**，只读不改）在同一发里并排印，间隔 0.2 秒。**两样不合成一个结论** —— 一个问系统状态，一个问那扇窗的矩形，各自只算自己那一个属性。本机主屏是 2048x1152，工作区 2048x1104（底下那一条是任务栏）。

| 场景 | 谁在前台 | QUNS | 几何判据 |
|---|---|---|---|
| 普通窗口（Tabby、计算器） | 那扇窗自己 | `QUNS_ACCEPTS_NOTIFICATION` | False |
| 最大化（记事本） | 记事本 | `QUNS_ACCEPTS_NOTIFICATION` | False |
| 锁屏 | `LockApp.exe` 的 `Windows.UI.Core.CoreWindow`，盖满整块屏 | `QUNS_NOT_PRESENT` | False |
| 浏览器全屏 | Chrome 盖满整块屏、屏心那个点归它 | `QUNS_BUSY` | 量到的不是它（见下） |
| **无边框全屏** | 一扇无边框的窗盖满整块屏 | **`QUNS_BUSY`** | True |

- **最大化不判独占全屏，这一格是量到的、不是推的**：记事本最大化后矩形 `(-7,-7)-(2055,1111)`，它让出底下那一条任务栏，所以**盖不满**整块屏，几何判据 False，QUNS 也只是 `ACCEPTS`。两样一致。**几何与 QUNS 在这一档上不打架。**
- **无边框全屏（本节那一格空白，量到了）**：做法是一扇无边框、盖满主屏的窗。实测结果是 **矩形盖满整块屏、几何判据 True、QUNS 回 `QUNS_BUSY`** —— **不是** `QUNS_RUNNING_D3D_FULL_SCREEN`。**限制要说清楚**：这扇窗是现造的，**不是哪个应用自己的全屏模式**。所以它证明的是「**`QUNS_BUSY` 的触发条件包含『一扇盖满整块屏的窗』**」，**不等于**「无边框全屏的游戏回什么」—— 后者要一台能起真应用的游戏或播放器才量得到（见下面「本轮量不到」）。
- **浏览器全屏：QUNS 那一半归因干净，前台身份那一半没量到。** 用一个临时 profile 起 Chrome 并直接进全屏：它实测盖满整块屏、屏心那个点归它，QUNS 连 12 发全是 `QUNS_BUSY`；把那一窗关掉之后同一个测量程序读到 `QUNS_ACCEPTS_NOTIFICATION`。**归因靠的是「拿掉原因再量一遍」那个对照**，不是靠时间重合。同一跑里 `GetForegroundWindow` 返回的仍是那扇被最小化的计算器，所以**几何那一列量的不是 Chrome** —— 两者不一致这件事本身记在第 1.1 节。
- **非全屏的商店应用不触发 `QUNS_APP`**：计算器是个窗口化的小窗（334x540），QUNS 仍是 `ACCEPTS_NOTIFICATION`，**不是** `QUNS_APP`。**枚举里那一档不要指望它表示「有个商店应用开着」。**
- **锁屏那一档顺带证了两件事**：QUNS 回 `QUNS_NOT_PRESENT`；而那扇锁屏窗的类名 `Windows.UI.Core.CoreWindow` 已经在 `Fullscreen.ShellClasses` 里，几何判据因此给 **False**（连量十发可重复）。锁屏刚开始的那一瞬有过一发 True，那一发是过渡态、不可重复，**不作为实测结果**。
- **枚举的编号是 1 开头的**（正本是 SDK 头那一段）。我照「枚举从 0 开始」写成 0..4，跑出来的是一个认不出来的数 —— 而正确答案正是文档说「只有它才发 UI」的那一档，差一位的后果是**永远判不成「可以发通知」**。那一段另有两个值不在任何网页文档的列表里，少抄一个就等于「认不出来的数」。**用枚举名，别用位置。**

**本轮量不到（不要拿别的场景顶替）**：

- **独占全屏**。这一台机器上没有一个能被我驱动进**真**独占 D3D 全屏面的东西：游戏要人在菜单里点，播放器/浏览器进的是无边框那一种（已量，见上）。**因此上面那条 2013 年 Win7 的实测结果（「全屏游戏回 `QUNS_BUSY` 而不是 D3D 值」）在这一台机器上没能重验** —— 既没被证实，也没被推翻，**不许照抄，也别当已被推翻**。要量它需要：一个能进独占全屏的应用，加一个能把它驱动进去的人。
- **Focus Assist 各档 / Win11 勿扰**。本轮没有切换系统那一档的勿扰状态，所以第 3 节最后那条「Win11 勿扰不反映」在本机**没有**新实测结果。


## 4. 暂停媒体 / 应用（欠账：自动暂停那一张工单的起点）

`WM_APPCOMMAND`（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-appcommand)）：命令码在 **lParam 高字**——`SendMessage(hwnd, WM_APPCOMMAND, 0, MAKELPARAM(0, APPCOMMAND_MEDIA_PLAY_PAUSE))`（塞错位置 Spotify 不理：SO [12449760](https://stackoverflow.com/questions/12449760/sendmessage-to-spotify-not-working-c)）。相关值：`APPCOMMAND_MEDIA_PAUSE = 47`（direct PAUSE，已暂停则不动）、`PLAY = 46`、`PLAY_PAUSE = 14`、`STOP = 13`。它本质是硬件事件通知格式；未处理时 DefWindowProc 沿子窗→父窗→shell hook 升级；程序化投递属 off-label 用法。**发给谁**：有实证的是发给目标应用自己的窗口（2012 年代 Spotify 类名 `SpotifyMainWindow`，12449760）；**广播 vs 发前台的差别没找到可靠一手帖；现代 Electron 版 Spotify 还响不响、浏览器视频处理不处理：都未取证**——Probe。

`SendInput`（[MS Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)）：串行注入系统输入流；VK 表只有 `VK_MEDIA_PLAY_PAUSE = 0xB3`（[virtual-key-codes](https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes)），**没有独立的 VK_MEDIA_PAUSE**。作用对象是「系统认为的当前媒体会话」。文档明写受 UIPI 约束，只能注入到等于或更低完整性级别（返回值不提示被拦）。有失败报告：SO [22437465](https://stackoverflow.com/questions/22437465/sendinput-vk-media-next-track-not-working)（VOLUME 生效、NEXT_TRACK 不生效，无答案）。

GSMTCS（`Windows.Media.Control`，Win10 1809+）：`RequestAsync()` → `GetSessions()` / `GetCurrentSession()`；每会话 `GetPlaybackInfo()`（PlaybackStatus、Controls）、`TryPauseAsync` 等一族、`SourceAppUserModelId`（[SessionManager](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager) / [Session](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession)）。覆盖面边界写在类定义第一句：**只含接了 SMTC 的会话**——多数老 Win32 不在列表里。`TryPauseAsync` 文档措辞是 Attempts，应用不响应就白发。Spotify / 浏览器视频各自管到多宽：未取证——Probe。**它白送一个检测信号**：PlaybackStatus 能答「现在有没有东西在放」。.NET 8 非打包桌面应用要用它得带 WinRT 投影（Microsoft.Windows.SDK.NET 一族），对单文件导出体积的影响**未取证**，要和 `docs/research/export-single-file.md` 那一题并测。

`NtSuspendProcess`：learn.microsoft.com 站内检索无任何 API 页（[检索接口](https://learn.microsoft.com/api/search?search=NtSuspendProcess&locale=en-us)只返回一条无关 Q&A），ntdll 未文档化导出；权限行为怪（按 `PROCESS_SUSPEND_RESUME` 打开反而失败：SO [70217703](https://stackoverflow.com/questions/70217703/process-not-suspending-ntsuspendprocess-what-is-going-on)）。**判死**：语义是「冻住进程」不是「暂停播放」，会冻住持有 GPU / 音频 / 共享锁的进程；媒体语义的正确层是 SMTC / 媒体键那一层。

## 5. 圈内叫法（命名供货，取向在 ADR-0006）

Workrave：三档 **operation mode：Normal / Quiet / Suspended**（[FAQ](https://workrave.org/faq/)、[菜单文档](https://workrave.org/docs/menus/operation-mode/)：Quiet = 继续计时但不打扰，Suspended = 连活动都不记）；「按应用静默」是开放中的 feature request（[#157](https://github.com/rcaelers/workrave/issues/157)），**不是已实现功能**——skipping / monitor rules 不是它的词，skip/postpone 指跳过那一次休息。Stretchly：`naturalBreaks`（**natural breaks**）、`monitorDnd`（**monitoring Do Not Disturb mode**）、`appExclusions`（**按进程名 pause/resume**）、`pauseForSuspendOrLock`（[官方文档](https://hovancik.net/stretchly/about/)；已知坑：Win10 升级来的用户可能永远处于 DND）。Time Out（macOS）：**natural break**（[官网](https://www.dejal.com/timeout/)）；按应用排除的确切叫法查不到（帮助站不可达）。EyeLeo：官网被拦，官方叫法查不到。f.lux：**"Disable for fullscreen apps"**（[v4 readme](https://justgetflux.com/news/pages/v4/readme/)）、**"disable by app"**；fullscreen disable 有多年 bug 史（曾 sometimes would disable for desktop）。SafeEyes（[官网](https://slgobinath.github.io/safeeyes/)）：**Smart Pause**；插件目录有 `donotdisturb` / `smartpause` / `mediacontrol`；`donotdisturb` 插件日志里叫 "Skip Fullscreen"（[#845](https://github.com/slgobinath/safeeyes/issues/845)）；坑：Spotify 播放音频也 inhibit idle、被当成看电影（[#847](https://github.com/slgobinath/safeeyes/pull/847)）。

**统称**：没有查到覆盖「感知用户状态 / 前台状态」的圈内统称。微软自己的伞形词是 API 名 **user notification state**。所有者定夺用「检测」（`GLOSSARY.md` 已落），勿扰取圈内词 DND / 勿扰。

## 6. Probe 待测清单（本机实测结果，出来后补记到对应各节）

> **进度（2026-10-04，工单 01）**：第 1、2、3 条量掉了，量不到的那几项在下面逐条标了原因；
> 第 4、5、6 条**不在工单 01 范围内**，原样留着。

1. `QueryFullProcessImageNameW` + `PROCESS_QUERY_LIMITED_INFORMATION` 三档对象各一发：普通进程、提权进程（管理员 cmd）、别的用户 / 系统进程（预期 ACCESS_DENIED，验证降级路径），`GetLastError` 落日志。
   **→ 实测结果在 1.1。两档量到**：普通进程（记事本）通；系统进程四个一律 `ACCESS_DENIED`，降级路径因此是三级。**提权进程那一档量不到**：起管理员进程要弹 UAC，这一轮没有人点。系统进程那一档之所以单独拿 pid 扫而不在前台链路上量，是因为**它的窗当不了前台** —— 这一条是量的时候才发现的。
2. UWP 身份两条路对拍（EnumChildWindows vs `GetGUIThreadInfo(0)`→hwndFocus），对象用商店版计算器 / 设置，外加全屏场景；同时跑 `GetPackageFullName` 和窗口 AUMID。**CoreWindow 交叉点**：`Windows.UI.Core.CoreWindow` 目前在 `src/Fullscreen.cs` 的 ShellClasses 里被当 shell 窗排除，它同时也是每个 UWP 应用的本体窗类——「前台是不是 shell」和「前台是哪个应用」两个语义必须拆开，这一条是现有代码和检测模块的真实交叉点。
   **→ 实测结果在 1.1，交叉点那条量到了**（锁屏那一扇）。两条路在本机给出**同一个**句柄。**两处没量到**：第二个对象（设置）—— 前台被一扇最小化的 `ApplicationFrameWindow` 占住，这一轮**顶不走**（`SetForegroundWindow` / `SwitchToThisWindow` / `LockSetForegroundWindow` 全试过），所以只拿到计算器一个对象；UWP 全屏场景 —— 同上。
3. `SHQueryUserNotificationState` 状态记录器：每 0.2 秒与 Fullscreen 的几何判定并排打印。场景：独占全屏、无边框窗口化、浏览器 F11、最大化（应不判全屏）、锁屏、Focus Assist 各档 / Win11 DND。重点：**无边框全屏时 QUNS 到底回什么**（第 3 节的空白）。
   **→ 实测结果在 3.1。量到**：最大化、锁屏、浏览器全屏、**无边框全屏（那个空白，答案是 `QUNS_BUSY`）**。**量不到**：独占全屏（这一台机器上没有能驱动进去的东西，2013 年那条 Win7 实测结果因此没能重验）、Focus Assist 各档 / Win11 勿扰（这一轮没切系统那一档）。
4. `EVENT_SYSTEM_FOREGROUND` 专线程钩子（新线程 + GetMessage 循环，委托钉 GCHandle）与轮询对拍：延迟、双发漏发、锁屏 / 解锁后要不要重挂、**提权窗口切前台收不收得到**（第 2 节的空白）。**不在工单 01 范围内，未动。**
5. 媒体控制三板斧真机过一遍（欠账「自动暂停」开工前）：WM_APPCOMMAND 发 Spotify 自己的窗口（先抓类名）vs 广播 vs 发前台；`SendInput` VK_MEDIA_PLAY_PAUSE（含提权前台时是否失效）；GSMTCS 枚举 + `TryPauseAsync` + `PlaybackStatus`，并把 `SourceAppUserModelId` 和前台 AUMID 对账。**不在工单 01 范围内，未动。**
6. 若做系统勿扰联动：Focus Assist 的非官方读法（WNF / 注册表）在目标 Windows 版本上的稳定性，当独立开关测，不焊在主路径上。**不在工单 01 范围内，未动。**

