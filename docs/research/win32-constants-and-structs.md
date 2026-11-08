# Win32 常量与结构体：不许凭记忆写

问的是「这颗常量、这个结构体的真实数值和尺寸」。

共同形状是**报错码会撒谎**：`RegOpenKeyExW` 拿 `HKEY_USERS` 去开 `Software\…` 回 **87
（ERROR_INVALID_PARAMETER）**而不是 2；`WNDCLASSEX` 两个名字字段是指针，写成 `[1 + MAX_PATH]`
WCHAR 数组让 `cbSize` 多 512，`RegisterClassEx` 也回 87。**87 = 结构体尺寸或根键错，不是权限、
不是键不存在。**判据：写 `DllImport` 常量前对着 SDK 头（10.0.26100.0：`winuser.h`、`shellapi.h`）
逐条读。这一族抓到过下面这几颗（数目就数这张表，手抄的计数过期过两次）：

| 那颗 | 记成 | 实际 | 记错之后的样子 | 出处 |
| --- | --- | --- | --- | --- |
| `HKEY_CURRENT_USER` | `0x80000003` | **`0x80000001`**（`0x80000003` 是 `HKEY_USERS`） | 「键不存在时顺手建出来」根本没跑到，而读的那侧把任何打开失败折成 `null`，同一条错误一边报 87 一边看着像「本来没有」 | SDK 头 + 实测 |
| `LLMHF_INJECTED` | `0x10` | **`0x01`**（`LLMHF_LOWER_IL_INJECTED` 是 `0x02`） | 刚自己注入的事件读成 `注入=False`，看着像「注入进不来」 | SDK 头 |
| `GW_CHILD` | `4` | **`5`**（`HWNDFIRST 0 / LAST 1 / NEXT 2 / PREV 3 / OWNER 4 / CHILD 5`） | 兄弟表整个是空的，差点把「读不出先后」当结论记下来 | 实测 |
| `MONITOR_DEFAULTTONEAREST` | `1` | **`2`**（`1` 是 `DEFAULTTOPRIMARY`） | 这台一块屏两发实测结果一样所以没露馅；拿到副屏「我这扇窗在哪块屏」就答成主屏。`Probe.cs` 里拿显示器那发传的还是 1 | 实测 |
| `WM_USER+1` | `0x400` | **`0x401`** | 投 `0x400` 读到「回调没进来」，差点当成托盘又坏了 | 实测 |
| `SendInput` 的 `cbSize` | `KEYBDINPUT` 自然尺寸就当尺寸 | **自然 24、联合体得占 32、`INPUT` 才是 40**，`KbdInput` 不垫就少 8 字节 | `cbSize` 对不上时 `SendInput` **返回 0 而且什么都不发**，日志里和「发了但窗收不到」同一副样子 —— 所以 `Win32.PressEscape()` 把两个尺寸一起印 | 实测 |
| `SPI_GETDESKWALLPAPER` | `0x0014` | **`0x0073`**（`0x0014` 是隔壁那颗 `SPI_SETDESKWALLPAPER`） | 「问系统现在铺的是哪一张」变成「把壁纸设成缓冲区里那个空串」—— **桌面当场刷黑，一秒一次**（`Calendar.Tick` → `Wallpaper.Refresh` → `CurrentFile`）。它没露馅有两层：设完照样返回 TRUE，路径由注册表那一支补上，所以 `_paper.Source` 一直是对的、自绘那层也对得上；只有拿眼睛看桌面才看得见。同一发还带出 GET 的另一半规矩：`uiParam` 要的是**缓冲区容量**，写 0 那一问根本答不上来 | 2026-09-28 他报「壁纸变黑了」；号在 10.0.26100.0 `winuser.h` 和 `SystemParametersInfoW` 那张表里各读一遍 |

这一族的形状是**读和改共用一发 API，只差一个号**。所以修的不止那颗号：`SystemParametersInfo` 在
`Win32` 里改私有，只包一个具名的 GET（`DesktopWallpaperMeasurement`），调用方递不进号。留在自检里的判据是
**「档案里有、桌面上没有」那一对**（`wallpaper-source` 那一组）—— 断「该 SPI 那一支答话」是不行的，
纯色桌面也回空，两种来路分不开；而注册表里那张图还在、系统却说当前没有壁纸，只有被设成空这一种来路。
2026-09-28 修完那一跑读到的仍然是这一对：损伤活在会话里，`fWinIni=0` 没写档案（注册表 `Wallpaper`
仍是 `…\轮播\jf-brou-358069-unsplash.jpg`），只有重设一次壁纸才结。**所以那一组是桌面恢复了才绿。**
(他报的；`--check` 那一跑实测结果同上)

还有四条不属同一族但同类形状：

- **`GetWindowTextW` 不写 null 结尾**，`StringBuilder.ToString()` 把每个标题读成第一个字符 ——
  我因此「看见」桌面上没有 Keeper 窗口，而它一直在。
- **同一扇遮罩窗的样式位，从自己进程读不回来**：外部进程读到 `样式=|POPUP|VISIBLE 扩展=|TOPMOST|TOOLWINDOW`，
  而 `GetWindowLong(GWL_EXSTYLE)` 在我们自己进程里读回 **`0`**（连 `TOOLWINDOW` 都没有）。别拿样式位当
  「引擎状态」的证据；穿透只看 `WindowFromPoint`。
- **`WindowFromPoint` 回来的是子窗口**，要比 `GA_ROOT`；类名 `Engine` 的那颗子窗正是引擎自己的渲染面。
- **低级钩子挂在全机输入的同步路径上，回调里不许写日志**（一次磁盘写拖慢所有人一次），只置一面旗 /
  只攒计数，打印交 `_Process`。Esc 那一发就是这么实现的（`src/Activity.cs` 的 Esc 钩子那一段）。
- **`SetForegroundWindow` 在「没人点过」时叫不动**（读回 `False 前台是 Chrome`），而引擎照报
  `输入框有焦点=True`；加了 `SwitchToThisWindow`、由真点击触发那一发才读到 `True`。**同一动作两条路
  两种焦点实测结果，写结论之前要说清是哪一路**（抬起：`SetForegroundWindow=False`；真双击那一路才 `True`）。
- **托盘槽位尺寸问 `SM_CXSMICON`**（索引 49），这台 125% 要 20 px，别信 PNG 自己的 32 画布。
- **`LVM_GETSELECTEDCOUNT` 这类不带指针的 ListView 消息能跨进 Explorer，但「个数」判不了
  「我的点击落没落」** —— 重选任一图标仍是 1，它不能当命中证据。
- **`GetMenuItemID` 对「两种不递 id 的项」给的是两个不同的数**（工单 04，2026-10-01 实测，
  10.0.26100.0）：`MF_POPUP` 那一项读回 **`0xFFFFFFFF`（-1，文档说的那个）**，
  而 `MF_SEPARATOR` 那一项读回 **`0`** —— 头文件上没写分隔线这一档（文档那句
  "returns -1 if the menu item does not have an identifier" 只覆盖得住 `MF_POPUP`）。
  **别把「所有不递 id 的项都读回 -1」当结论**：写成那样，第一发自检里就红了（值是真读出来的，
  自检里那一条判据当场改成了「分发表里没有一行是 id 0」）。而 0 正是 `TrackPopupMenu` 收在
  Esc 那一个数，所以托盘的分发表（`src/Tray.cs`）把「`Run == null`」和「没有一行 id 是 0」
  当两道守卫 —— 分隔线那一项和 Esc 递的是同一个数。
- **`MF_POPUP` 的 id 会不会被截掉，**运行期量不出来**（工单 04，2026-10-01 实测）。
  `AppendMenuW` 第三个形参是 `UINT_PTR`，而 `Win32.cs` 里按 `uint` 收的那条在 64 位上确实会
  截掉高 32 位 —— 所以专门给子菜单加了一条指针宽的形态。**但把代码换回按 `uint` 递之后，
  自检里那一组照样是绿的**：这一发里 menu 句柄读出来是 `0x13740C6F`，**高 32 位本来就是 0**，
  截掉它是个空操作，没有一个发子菜单的实测结果能分出两件事。
  也就是说「句柄没被截」是一个**量不出来的结论** —— 自检里最终判的是**形参的宽度**
  （`typeof(Win32).GetMethod("AppendMenuHandle").GetParameters()[2].ParameterType == typeof(nint)`，
  编译期事实、被称错就当场坏），而运行期那一发只判「读回来的句柄就是建那一个」。
  真出问题的症状不在实测结果里：子菜单不弹或者弹出来是空的 —— **那正是自检量不到、
  只能靠实拍帧和真点击发现的那一半**。

