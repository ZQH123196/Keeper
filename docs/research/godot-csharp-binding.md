# Godot 4.7.2 的 C# 绑定：这一版引擎答了什么

问的是「某个 API 在这个引擎版本里存不存在、要什么前提」。合同那一头是装机里的 `GodotSharp/Api/Release/GodotSharp.xml`，先 grep 它再下结论。

**`Godot_v4.7.2/.../GodotSharp/Api/Release/GodotSharp.xml` 就是这个引擎版本的 API 合同。**
猜某个方法存不存在、需要什么前提之前先 grep 它，别猜。web fetch 走坏的那一轮就是它离线答的
passthrough 语义。

- **装机 dll 和 XML 会不一致**：XML 里有 `Input.ParseInputEvent`，这一份 dll 没有
  （`Engine.GetSingleton("Input")` 拿到对象照样 CS1061）。以 dll 为准，XML 只用来排除。
- **`Window` 没有 `Modulate`**，只有 `CanvasItem.Modulate`（XML 里没有 `P:Godot.Window.Modulate`）。
  斜坡推在装内容的那个 `Control` 根上：窗口每一像素都是它画的，乘在它身上和乘在窗口上是同一件事
  （`src/Fader.cs` 里把乘数挂上窗根 `Control` 那一段）。
- **`display/window/subwindows/embed_subwindows` 这一键是承重的**：默认的 `true` 让子窗口嵌进主窗口画，
  遮罩于是**没有自己的 HWND**（两次跑出 `HWND=none`）；多屏每块一扇遮罩只有 `false` 那条路走得通，
  代码里现造的子窗才成为真窗（自己句柄、能被命中、能挂进壁纸）。这条开关是 `Window.Popup()` 的文档说的。
  键在 `project.godot`，**为什么设它以前没写在任何文档里**。
- 名字与记法一族（4.7.2 的 C# 绑定）：`Window` 没有 `move_to_front`（只有 `GrabFocus`/`Show`），举窗只能
  自己 `SetWindowPos`；没有 `FocusModePreset`；`Viewport` 没有 `Position`；交集叫 `Rect2.Intersection`（无
  `Intersect`）；`Node` 用 `GetParent()` 没有 `Parent`；可见性问 `IsVisibleInTree()`；`Control.Size` 是
  `Vector2` 而 `Window.Size` 是 `Vector2I`；`Rect2I.Merge` 不收 `Vector2I`；交焦点那发叫 `Viewport` 上的
  `GuiReleaseFocus()`，`ReleaseFocus()` 编译不过。
- **引擎那几条读文件的接口在 C# 侧是静态工厂、回新对象、读不通回 `null`**
  （`Image.LoadFromFile`、`AudioStreamMP3/OggVorbis/Wav.LoadFromBuffer`）。按 GDScript 记法写直接被
  CS0176 / CS0019 拒；`AudioStreamWav.LoadFromBuffer` 还多一个绑定没给默认值的 `Dictionary` 参数。
  `Win32` 是 `internal`，所以签名带 `Win32.Rect` 的类必须也 `internal`（CS0051）。
- **`Godot` 命名空间和 `System` 撞**：`Environment`、`Path` 都有歧义，`System.Environment.ProcessId`
  要写全 —— 代码里那些一直写全的 `System.IO.Path` 不是洁癖。
- **`Node._input` 是树级广播，分不清事件归哪个窗**；`Control.GuiInput` 才按视口分发，而且**根控件须
  `MouseFilter = Pass`**，否则落在子控件上的事件不上冒。
- **theme 的 override 加在父容器上管不到子控件的类型**，这一条栽过两次：日历把颜色 override 下在
  `Button` 上而字在它的子 `Label`，永远默认白；遮罩上清单的字/勾/钮三处同一个根。**加在它自己那一颗上
  类型才对得上**。
- **`Control` 不肯比自己的最小高度更矮**：给 `LineEdit` 14 px 它按最小 26 画、窗不裁，于是框画到面板下沿
  之外 12 px。换零边距零底色的 stylebox 把最小压到 18、再给页脚留 22 才装得下。
- `Flat = true` 不画 `normal`；去掉 flat 之后默认主题那颗 1 px 影子会跟进来，得再塞一张 `StyleBoxEmpty`
  给 `shadow`。
- **C# 不给自定义 struct 合成 `==`**：`Win32.Rect` 是 struct，自检写 `a != b` 直接 CS0019，比矩形走
  `Wallpaper.Same`。
- **`System.Text.Json` 有三处要对落盘的字节下手**：`history.json` 是 camelCase 写的，而默认 reader
  分大小写，读回来是空的 —— 修法是把读也交给同一份 `Shape` 加 `PropertyNameCaseInsensitive`；
  `todos.json` 根节点是**数组**，`WriteIndented` 换 CRLF 而磁盘上的文件是 LF；默认转义器把非 ASCII
  写成 `\uXXXX`。三处都改（`Replace("\r\n","\n") + "\n"`、`UnsafeRelaxedJsonEscaping`、大小写不敏感的读）
  才谈得上「自己写的文件自己逐字节读得回来」。
- **默认 `JsonSerializerOptions` 不认 JSONC**（2026-09-29 本机实测，`dotnet run` 一发定死）：`// 注释`
  开头报 `'/' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.`，尾逗号报
  `The JSON object contains a trailing comma at the end which is not supported in this mode`。同一份内容加上
  `ReadCommentHandling = Skip` + `AllowTrailingCommas = true` 两旗就读得回来，**对象根和数组根都一样**
  （数组根那一发是 `todos.jsonc` 那个 `[{…},]` 实测的）。所以记录那一族不需要新库，需要的是那两旗。
- **`JsoncDoc` 那份原位编辑器的根只能是对象**：`Values` 往 `(JsonObject)_root.Json!` 硬 cast，数组根进不去。
  这是它的适用范围，不是它的缺陷 —— 记录那一族（清单是数组根）因此走 `System.Text.Json` 的读旗，
  代价是没有原位编辑。取向记在 `docs/adr/0002-settings-and-records.md`「记录借 JSONC 的读，不借它的写」那一节。
- **往参与序列化的类上加运行时状态属性，会把它写进用户那份文档**：`History` 加了一个
  `public bool Unreadable` 之后，`Save`（`JsonSerializer.Serialize(this, Shape)`）写出的是
  `{"records":[…],"unreadable":false}`。拦住它的正是自检里那条「写出去还是那个形状」的期望串比对
  —— 比的是磁盘上真字节，不是内存里的对象，所以它当场就报（2026-09-29）。修法是 `[JsonIgnore]`，
  而 `Todos` / `CalendarNotes` 那两个同名的旗天然没这件事：它们序列化的是 `Items` / `Notes` 那份容器，
  不是 `this`。
- **`SettingMap` 的 step 键是裸的**，前缀只活在 `ByKey` 索引里：`step.Set("ms")` 对，写
  `"steps[].ms"` 报 `Sequence contains no matching element`。
- **`NodePath` 的冒号是「子资源.属性」记法**：控件取名 `c:clock` 之后 `GetNode("c:clock")` 找不到东西，
  当场 null → NRE。
- **`Window.MoveToForeground()` 对拿不到焦点的窗不管用**（实测结果没动）。遮罩的 HWND 要 `Show()` 之后的
  **下一帧**才数得到（`FindOurWindows`），别在 `Raise()` 里数。`Window.GetWindowId()` **不是** HWND。
- **窗进树那一刻就按引擎给的位置建出 OS 窗、站着等 `_Ready` 跑完**：这一趟 `_Ready` 走了 128 毫秒
  （第二发 52），那期间的黑块 = 建好还没画过的窗。根节点 `visible=false` + 末尾 `Show()` 之后同一行读成
  `没露面（句柄 -1，-1 即「还没有 OS 窗」）… 露面时句柄=2 位置=(950, 300)`。
- **无效 `Rid` 印出来是 `-1` 不是 `0`**：拿 `born == 0` 当「还没建」的哨兵会把没建读成已建。判建没建
  最稳问 `IsVisible()`。
- **Godot 印 `Rect2I` 是 `(位置), (大小)`，不是两个角点**：按角点读会把「那块实盖 x 624..1411、
  y 179..813」读成「判据没抓到清单」。包围盒一律分开写「从 (x,y) 起 WxH」。
- **C# 侧的 `Translation` 只有 `Locale` 和 `AddMessage(key,msg,comment)`**，没有 `LoadFromCSV` /
  `load_from_string` → CSV 自己解析；引擎自带的 .csv 导入要编辑器先 import 一遍，`*.translation`
  运行时不读。
- **`--headless` 下那四组实测结果全是 false**（压根没有窗口），已在 `Verdict()` 里挡住；**退出码 0 只在无头
  那条上量过**，别外推到窗口模式。

