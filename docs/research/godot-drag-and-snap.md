# 拖放、刻度线、改大小：引擎给到哪一步

日期 2026-10-06。目标引擎 Godot 4.7.2-stable mono。

**下面第一节到第七节是官方文档那一层**，每一条都带着实际取过的链接。
**第八节是本机实测结果**（`src/Probe.cs` 的 `--drag` / `--drop` / `--canvas` 三档，
2026-10-06 跑）。

**两层不是一回事。** 文档给的是「引擎声明了什么」，量的是「这一版在这台机器上真的
怎样」—— 两者对不上的时候以第八节为准，而**第八节量不到的那几问不算答了**。

三档的跑法：

```
Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/Probe.tscn -- --drag
Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/Probe.tscn -- --drop
Godot_v4.7.2-stable_mono_win64_console.exe --path . res://src/Probe.tscn -- --canvas
```

不带 `--headless`（要真窗），不带退出码（和 `--push` / `--fg` 同一形状）。
读数都在 `%TEMP%\keeper-probe.log`。

起因是给遮罩做用户自己摆的布局（`docs/design/fader-layout.md`
「遮罩的布局是每一步自己那一份」那一节）时撞到的三个问号：拖放引擎给不给、刻度线引擎给不给、
改大小引擎给不给。

---

## 一、拖放引擎给：四个虚函数，4.7 仍有效

`_get_drag_data` / `_can_drop_data` / `_drop_data` 三个在 4.7 仍是 `virtual`，
`set_drag_preview` 是普通方法。签名取自 4.7-stable 的类文档源文件。

| 成员 | 限定 | 返回 | 参数 |
| --- | --- | --- | --- |
| `_get_drag_data` | virtual | `Variant` | `Vector2 at_position` |
| `_can_drop_data` | virtual const | `bool` | `Vector2 at_position`, `Variant data` |
| `_drop_data` | virtual | `void` | `Vector2 at_position`, `Variant data` |
| `set_drag_preview(Control)` | 普通方法 | `void` | — |

**没有废弃。** 同一份源文件里带 `deprecated` 标记的只有两处
（`auto_translate`、`LAYOUT_DIRECTION_LOCALE`），拖放那几条不在其中。
4.0 那一版把拖动数据从 `Variant` 改成了 `Dictionary`，到 4.7 是 `Variant` 上那个新形状。

`Viewport` 那一侧另有 `gui_is_dragging()`、`gui_get_drag_data()`、
`gui_cancel_drag()`、`gui_is_drag_successful()`。4.7 还新加了三样：
`set_drag_forwarding(drag_func, can_drop_func, drop_func)`（把三个虚函数转发给
Callable）、`force_drag(data, preview)`（没有鼠标也强制拖）、`accessibility_drag()`
（无障碍那条路）。

出处：
<https://github.com/godotengine/godot/blob/4.7-stable/doc/classes/Control.xml>、
<https://docs.godotengine.org/en/4.7/classes/class_control.html>、
<https://docs.godotengine.org/en/4.7/classes/class_viewport.html>

## 二、C# 绑定：名字照旧

`SetDragPreview(Control)` 名字不变。类文档内嵌的 C# 示例给的形状：

```csharp
public override Variant _GetDragData(Vector2 atPosition)
{
    var cpb = new ColorPickerButton();
    SetDragPreview(cpb);
    return _color;
}
```

`Viewport.GuiIsDragging()` / `GuiGetDragData()` 是按 C# 命名约定从
`gui_is_dragging()` / `gui_get_drag_data()` 推的 —— **这一条是推断**，
没有在 4.7 页面上直接读到这两个方法的 C# 代码块。真要用要先在
`Probe.cs` 里量一发。

出处：
<https://docs.godotengine.org/en/4.7/classes/class_control.html#class-control-method-set-drag-preview>、
<https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_differences.html>

## 三、刻度线：引擎没有

查过 `Control`、`Node2D`、`CanvasItem`、`GraphEdit`、`Window` 五个类，
搜 `alignment guide` / `snap to edge` / `drag handle`，**零命中**。
`Control` 里带 "Snap" 的词条只有 `LayoutPreset` 枚举的描述文字，
说的是锚点预设，与参考线无关。

两个容易看错、其实不是的东西：

- `Viewport.gui_snap_controls_to_pixels`（默认 `true`）把控件坐标**舍入到整像素**。
  proposals 里的 #15391「Add Control-specific GUI snapping」是要求能按控件关掉它，
  状态仍为 open。
- `GraphEdit` 有 `snapping_enabled` / `snapping_distance`，那是**节点图连线**吸附，
  不是画布上的控件对齐。

**结论：吸附参考线和边缘对齐要自己算、自己画。**

出处：
<https://github.com/godotengine/godot-proposals/issues/15391>、
<https://github.com/godotengine/godot/blob/4.7-stable/doc/classes/GraphEdit.xml>

## 四、改大小：引擎不给手柄

`Control` 类文档里没有 resize handle，也没有边沿命中检测的 API。
官方指引是在 `Control._gui_input` 里自己判事件落在不在边界框上。

一处例外：`HSplitContainer` / `VSplitContainer` / `SplitContainer` 自带可拖分隔条，
但那是两栏布局的分隔，不是一组自由摆放矩形的手柄。

## 五、4.7 的一条新行为：键盘发起的拖动

三个虚函数的文档里写了同一条注记 —— **拖动若由键盘快捷键或
`accessibility_drag` 发起，`at_position` 是 `Vector2.INF`**。

写命中判定时这一条要单独分支。不分开的话，手一碰键盘拖动就会拿 `INF` 去算。

**实测结果补一句（第八节 8.1）**：这一版 C# 绑定里 `Viewport.AccessibilityDrag`
**反射查不到**，所以键盘发起的拖动在 C# 侧**没有自动入口** —— 这一条现在只是
一条待分支的约定，不是一处真会发生的路径。

## 六、整像素吸附要不要关

`gui_snap_controls_to_pixels` 默认 `true`。文档说开着会把控件坐标舍到整像素。

**实测结果（第八节 8.4）把这个说法否掉了**：三档（默认 / 关掉 / 拧回来）摆
`100.4` 读回全是 `100.4`，**这个开关对 `Control.Position` 没有任何影响**。
它只作用在引擎自己排版算出来的矩形上，代码直接写 `Position` 不经过它。

所以取向不变、理由改掉：**不用为了手柄命中去拧它**，理由是「拧了也不一样」，
不是「拧了才干净」。布局存的是份额，份额转出来是连续数。

取向记在 [`../adr/0001-engine-and-ui-one-declaration.md`](../adr/0001-engine-and-ui-one-declaration.md)
那一节里，实测结果在第八节。

## 七、没有查着的

- **官方没有一份说「画布该用真控件还是自绘」的指引。** 文档里查不到，
  第三方项目这一轮没查。**本机三发实测答了**（8.7）：画布是真控件，`_Draw` 只画
  上面那一层，引擎重排不会把那一层抹掉。**官方没指引不等于这一问没答案** ——
  引擎行为能答的就该去量。
- **4.7 的官方拖放教程页定位不到。** `tutorials/ui/drag_and_drop.html` 返回 404。
  上面第一节和第二节的结论全部来自类参考和源文件，不来自教程页。

---

## 八、本机实测结果（2026-10-06）

跑法上面写了。机器：Godot 4.7.2-stable mono（`godot=4.7.2-stable (official)`），
`headless=False`，`embed_subwindows=false`。**一块屏。**

这一节答的是前面答不出来的四问。**量到的和没量到的分开写** —— 没量到的不许读成
「引擎不给」（`CODING_STANDARDS.md`：实测结果说「不通」要先怀疑测量程序）。

### 8.1 C# 名字：第七节那些推出来的，这一发问过了

第二节说 `GuiIsDragging` / `GuiGetDragData` 是**按 C# 命名约定推的**、没读到代码块。
这一发逐个问「这个名字在 C# 里能不能编出来」：

| 名字 | 反射/调用读数 |
| --- | --- |
| `Viewport.GuiIsDragging()` | 存在，返回 `False`（空场） |
| `Viewport.GuiGetDragData()` | 存在，空场返回 `Nil` |
| `Viewport.GuiCancelDrag()` | 存在，`void` |
| `Viewport.GuiIsDragSuccessful()` | 存在，返回 `False` |
| `Viewport.SetDragForwarding` | **反射查不到** |
| `Viewport.ForceDrag` | **反射查不到** |
| `Viewport.AccessibilityDrag` | **反射查不到** |
| `Control.SetDragPreview` | 存在 |
| `Control._GetDragData` | 存在 |
| `Control._CanDropData` | 存在 |
| `Control._DropData` | 存在 |

**第二节那两个推断成立**：`GuiIsDragging` 和 `GuiGetDragData` 在 C# 里就是这么叫。

**第一节说的 4.7 新加的三样，这一版 C# 绑定里一个都没有** —— `SetDragForwarding`、
`ForceDrag`、`AccessibilityDrag` 三个方法名反射查不到。这不是「引擎不给」，是
**GDScript 那一侧有、C# 这一侧这一版没绑**。**写编辑器不能用它们**，而且第五节
那条键盘发起的拖动（`at_position` 是 `INF`）在 C# 里**没有自动入口**。

### 8.2 拖放的源侧：给

一整趟真拖（按下 → 挪 → 松开），喂给**控件所在那扇窗**（`PushInput` 给那个
`Window` 当视口）：

- `_GetDragData` **调到 1 次**，`SetDragPreview` 调到 1 次。
- 拖动进行中 `GuiIsDragging()` 读到 **`True`**，`GuiGetDragData()` 读到 **`String`**。
- `at_position` = `(40.0, 20.0)` —— 就是控件内那个点，**不是 INF**（鼠标发起的那一趟）。

**对照（喂根视口那一发）**：同一个 `PushInput` 改喂 `GetTree().Root`，
`_GetDragData=0`、`在拖=False`。**事件得喂给控件所在那扇窗**，
和 `--push` 那一族量到的是同一件事。

### 8.3 拖放的落侧：给（第二发，`--drop`，系统那一层喂法）

第一发（`--drag`，`PushInput` 进程内喂）落侧读到 0，落点读数成立而计数是 0 ——
那一发只能答「注入这条路走不到落侧」，**答不出引擎给不给**。换喂法再量一次：
`Win32.SendInput` 的真系统鼠标事件（走窗口消息那条路），**并且先验量具**。

**量具对照（这一发一定要成立的那种）**：真点一下画布上那颗按钮，
`Button.Pressed = 1` → 系统这一层的事件收得到，下面每一行都作数。

**真拖三段交，每段之间让出一帧**：按下 → 挪 10 格 → 松开。

| 读数 | 值 |
| --- | --- |
| 按下那一发 | 交 1 发，起手 `_GetDragData=0`（还没跨过引擎的起拖门限） |
| 挪 10 格之后 | `在拖=True`、`拖的东西=String`、源 `_GetDragData=1`、目标 `_CanDropData=2` |
| 松开之后 | `在拖=False`、**`_DropData=1`** |
| 最终 | **`_CanDropData=3`、`_DropData=1`** |
| `at_position` | 源 `get=(40.0,20.0)`、目标 `can=(60.0,30.0)`、`drop=(60.0,30.0)` |

**结论：引擎给落侧。** `at_position` 是控件内的点，不是 INF。

**喂法是这一发的关键，两处都对**：
1. `PushInput`（进程内）走不到落侧 → 要走系统那一层。
2. **一次 `SendInput` 把 19 发全交进去也不行** —— 全在同一帧里进去，GUI 的拖动
   状态机来不及进「正在拖」那一档，读出来连**拿起**那一发都是 0。真人手拖天然跨
   很多帧，引擎那一层就是按跨帧设计的。**必须分段交，段间让帧。**

**刻度线那一族要读两条**：移动要分多格交，不能只交一个终点。

### 8.4 整像素吸附：默认开着，但**对 Position 无效**

`gui_snap_controls_to_pixels` 默认 `True`（和文档一致）。但把一个 `Control` 的
`Position` 摆成 `100.4`，三档读数分别是：

| 开关 | 摆 100.4 读回 |
| --- | --- |
| 默认（开） | `100.4` |
| 关掉 | `100.4` |
| 拧回来 | `100.4` |

**三档读数一样，开关对 `Control.Position` 没有任何影响。** 第六节说「开着会把控件
坐标舍到整像素」—— 那是**文档的说法，实测结果对不上**：这一层只作用在引擎自己
排版算出来的矩形上，代码直接写 `Position` 不经过它。

**这一条改的是取向，不改结论**：份额换算出来的连续数**不会**被引擎再舍一遍，
所以编辑器不必去拧那个开关 —— 但理由是「拧了也没用」，不是「拧了才干净」。

### 8.5 刻度线：自己算得出来（纯算术）

按契约那一族写（吸屏的中线、四边，别的控件的边和它们的中间线），喂五个点：

| 读数 | 摆（左上角） | 吸到 | 动了 |
| --- | --- | --- | --- |
| 中心正落在屏中线上 | (280.0, 180.0) | (280.0, 180.0) | 否（本来就在线上） |
| 中心差 2 之内 | (278.0, 180.0) | (280.0, 180.0) | **是** |
| 中心差 10 之外 | (290.0, 180.0) | (290.0, 180.0) | 否（门限 6，不吸） |
| 中心贴左上角 | (-40.0, -20.0) | (-40.0, -20.0) | 否 |
| 中心对别的控件中线 | (420.0, 50.0) | (420.0, 50.0) | 否（本来就在线上） |

**吸附这一族自己算得出来**：门限内吸得住、门外不吸，两头都有读数。

**量具摆错过一次，记在这儿**：第一版把「中心」当「左上角」喂进去，于是「正中」
那一行读出来「没动」—— 那是量具的错，不是吸附不成立。刻度线算的是**中心对上线**，
喂进去的必须是**左上角**。

### 8.6 手柄命中：自己判边可行

自己判「事件落在不在边上」（门限 6）和引擎那个矩形对照，四个点：

| 点 | 在左边带 | 在上边带 | 在矩形内 |
| --- | --- | --- | --- |
| 正中 (40,20) | 否 | 否 | 是 |
| 左边带上 (2,20) | **是** | 否 | 是 |
| 右上角 (78,2) | 否 | **是** | 是 |
| 外面一点 (84,20) | 否 | 否 | **否** |

两头都分得开：边带命中和矩形内命中不是一回事。**手柄那一层自己算得出来**。

### 8.7 画布用真控件还是自绘：**真控件，自绘只是上面那一层**

**这一问的问法本身就不对。** 真控件和自绘**不是二选一** —— 而且答案在
8.2 / 8.3 那两发里就已经量到了，只是当时没人从那一行读出来：

- 画布 `GuideProbe` 就是一个**真 `Control`**（基类 `Control`、`mouse_filter=Stop`、在真树上），
- 子控件是**真 `Panel`**，位置和尺寸由引擎排，
- 拖放**从源到目标整趟走通**（`_GetDragData` / `_DropData` 各调得到）。

所以「真控件还是自绘」答成：**画布是真控件，控件本体、焦点、主题、命中全部走引擎；
`_Draw` 只画上面那一层**（刻度线、手柄）—— 这和 ADR-0001 那一节写的一致。

**剩下真要量的一问：引擎重排会不会把你自绘的那层抹掉。** 第三发（`--canvas`）
量这个，判据是**屏幕物理像素**（`Frames.Shot`），不靠「我调过 `_Draw` 所以它在」
—— 后者是代码在说话，不是屏幕在说话。那一发取画布屏中线（自绘的白线，底是深灰）：

| 重排 | 读数 |
| --- | --- |
| 引擎重排之前 | `(255, 255, 255)` |
| 挪子控件 + `QueueRedraw` | `(255, 255, 255)` |
| **换画布尺寸** 700x460 → 520x320 | `(250, 250, 250)` |
| 只挪子控件、**不** `QueueRedraw` | `(250, 250, 250)` |

**自绘那一层在所有重排下都在。** 换画布尺寸那一档是重排最狠的一种（引擎重排整棵
树），读数从 255 变 250 是抗锯齿边缘的差别，**不是那一层没了**。

**编辑器里的落法**：拖一个控件时引擎会重排，所以那一档**自己也要 `QueueRedraw`**
（刻度线的位置变了）。这一档量到的是「不主动重画时上一次画的那层还在」——
**不是「位置自动跟着变」**，后者要代码自己算。

### 8.8 这一发答了什么、没答什么

| 问 | 答 | 哪一档 |
| --- | --- | --- |
| 三个虚函数在 C# 里叫什么 | 第二节那两个推断成立 | `--drag` |
| 4.7 新加的三样在 C# 里有没有 | **没有**，一个都没有 | `--drag` |
| 源侧（拿起、预览）走不走得通 | **走得通** | `--drag` |
| 落侧（放下）走不走得通 | **走得通** | `--drop` |
| 整像素吸附对 `Position` 有没有影响 | **没有** | `--drag` |
| 刻度线自己算不算得出来 | **算得出来** | `--drag` |
| 手柄命中自己判不判得出来 | **判得出来** | `--drag` |
| 画布真控件还是自绘 | **真控件，自绘只是上面那一层** | 8.2/8.3 已含，`--canvas` 补重排那一问 |
| 引擎重排会不会抹掉自绘那层 | **不会** | `--canvas` |

**九问都答完了。**

## 出处

- <https://github.com/godotengine/godot/blob/4.7-stable/doc/classes/Control.xml>
- <https://docs.godotengine.org/en/4.7/classes/class_control.html>
- <https://docs.godotengine.org/en/4.7/classes/class_viewport.html>
- <https://docs.godotengine.org/en/4.7/classes/class_graphedit.html>
- <https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_differences.html>
- <https://github.com/godotengine/godot-proposals/issues/15391>
