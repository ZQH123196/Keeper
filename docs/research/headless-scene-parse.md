# 手改 `.tscn` 之后怎么验引擎解析

问的是「手改 `.tscn` 之后只有引擎解析才算数」这条判据怎么跑。2026-10-07 在本机量。
规程里那一条在 `../../CODING_STANDARDS.md`。

## 怎么量的

样本造在 `.scratch/parse-probe/`（`.scratch/` 已在 `.gitignore` 上），**版本库里的场景一个没
动**。理由是 `export-single-file.md` 那一节记过一次教训：引擎无头起来会扫一遍文件结构，
当场给缺 `.uid` / `.import` 的文件补上 —— 拿 `src/` 里的真场景去量，量完会多出几个文件，
得分不清是自己动的还是引擎动的。

样本用 `src/About.tscn`（`load_steps=5`，1 个 ext_resource + 3 个 sub_resource），
四份都只改一处：

| 样本 | 改了什么 | ext/sub 实数 |
| --- | --- | --- |
| `a-good.tscn` | 逐字节照抄，当对照 | 1 / 3 |
| `b-loadsteps-low.tscn` | `load_steps` 5 改成 3（**算少**） | 1 / 3 |
| `c-subres-missing.tscn` | 砍掉最后一个 `[sub_resource]` 块，`load_steps` 改成 9（**算多**） | 1 / 2 |
| `d-extres-missing.tscn` | 删掉 `[ext_resource ... id="1"]` 那一行，`load_steps` 改成 4，正文 `script = ExtResource("1")` 照留 | 0 / 3 |

跑法（一发一个进程，各带 60 秒上限）：

```
Godot_v4.7.2-stable_mono_win64_console.exe --headless --quit --path . res://.scratch/parse-probe/<样本>
```

**`--quit` 不能省**：不带它，引擎解析完就进主循环，进程不自己退（这一发量到的是 60 秒
超时后杀掉）。

## 实测结果

| 样本 | 退出码 | stderr | 引擎说了什么 |
| --- | --- | --- | --- |
| `a-good` | **0** | **0 字节** | 无 —— 对照成立 |
| `b-loadsteps-low` | **0** | **0 字节** | **一个字不报** |
| `c-subres-missing` | **1** | 387 字节 | `Parse Error` + `Failed loading resource` + `Failed loading scene` |
| `d-extres-missing` | **1** | 419 字节 | 同上三条，措辞是 `Parse error. [Resource file ...]` |

两份报出来的那几行照抄：

```
ERROR: res://.scratch/parse-probe/c-subres-missing.tscn:47 - Parse Error: .
   at: _printerr (scene/resources/resource_format_text.cpp:41)
ERROR: Failed loading resource: res://.scratch/parse-probe/c-subres-missing.tscn.
   at: _load (core/io/resource_loader.cpp:317)
ERROR: Failed loading scene: res://.scratch/parse-probe/c-subres-missing.tscn.
   at: start (main/main.cpp:4767)
```

```
ERROR: Parse Error: Parse error. [Resource file res://.scratch/parse-probe/d-extres-missing.tscn:56]
   at: _parse_node_tag (scene/resources/resource_format_text.cpp:293)
ERROR: Failed loading resource: res://.scratch/parse-probe/d-extres-missing.tscn.
   at: _load (core/io/resource_loader.cpp:317)
ERROR: Failed loading scene: res://.scratch/parse-probe/d-extres-missing.tscn.
   at: start (main/main.cpp:4767)
```

**判据**：**退出码非 0，且 stderr 里有 `Parse Error`。** 两份报出来的都同时满足这两条，
两份静默过的都两条都不满足。

## `load_steps` 那一行是死的

问的是「`b` 明明把 `load_steps` 算少了，引擎为什么不报」。答：**因为引擎根本不看这一行。**

它只是给编辑器算进度条用的一个提示数。真正的资源条数引擎自己数。所以：

| 改法 | 结果 |
| --- | --- |
| 算少（5 改成 3） | 静默通过 |
| 算多（5 改成 9） | 静默通过 —— `c` 那一发同时砍了子资源，报错的是子资源不是它 |

**活证据**：`src/Options.tscn` 压根**没有** `load_steps` 这一行（头部是
`[gd_scene format=3 uid="uid://bwodi0n24kgw7"]`），而它每天都在跑，照常开设置窗。
仓库里 8 份场景，只有这一份是那样写的。

`b` 与 `a` 逐字节不同（SHA256 不同），头一行确实是 `[gd_scene load_steps=3 format=3]` ——
**它是真的改了**，引擎确实没报。

## 结论：这条判据抓得到什么，抓不到什么

| 错法 | headless 抓得到 | 判据怎么写 |
| --- | --- | --- |
| 子资源内容缺失 | **是** | 判据成立 |
| `[ext_resource]` 声明缺失 | **是** | 判据成立 |
| `load_steps` 算错 | **否** | **如实写「量过，引擎不报」** |

`CODING_STANDARDS.md` 原来那句话点的是「构建查不出 `load_steps` 和子资源引用有没有算错，
只有引擎解析才算」。这一族实测下来：**`load_steps` 那一半当初就点错了** —— 引擎解析也算
不出，而它压根不影响解析结果。真正只能靠引擎解析的是子资源引用那两种。

## 这条判据的代价

**它只答「解析过没过」，不答「结构对不对」** —— 引擎解析成功之后节点名对不对，这条不管。
上面那条 `--headless --quit` 跑法不连编辑器，退出码和 stderr 能当判据用。

## 没量的

- 场景在**编辑器里**改坏（不是手改 `.tscn`）会报什么 —— 编辑器自己有校验，不在这条跑法里。
- `.tscn` 里**属性名写错**（不是引用缺失）报不报。
- 换一份场景（`Presets.tscn` 有 2 个 ext_resource）结果是否一样。
- 这一族全部来自这一台机器、一个引擎版本（4.7.2 stable mono）。换个版本要重量一遍。

**可复测**：照「怎么量的」那一节在 `.scratch/` 下再造四份，跑同一发，退出码和 stderr 对上面
那张表。