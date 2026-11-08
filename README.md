# Keeper

一台 Windows 上的休息提醒器：到点了把整块屏幕缓缓压进一种颜色，你明显回到工作之后就淡走；它另外还把一张月历摆在桌面上，像壁纸一样待着。什么都不拦、什么都不要求，只改光。

**Godot 4.7.2 mono + C#，只跑 Windows。** 那束光是为了什么、配置该长成什么样、哪些行为是定死的 —— 都在产品契约里，不在这一页。

## 文档在哪

**一条事实只活在一个文件里。哪一条该落在哪一份 —— 那张表在根 [`AGENTS.md`](AGENTS.md)「本项目的文档落点」一节**，本页不抄第二遍，只指路：

| 要找的 | 去处 |
| --- | --- |
| 程序该做什么、配置该长成什么样 | [`.scratch/keeper-contract/spec.md`](.scratch/keeper-contract/spec.md) |
| 一个术语叫什么、别的叫法为什么不用 | [`GLOSSARY.md`](GLOSSARY.md) |
| 定了什么取向、否掉了哪条路、为什么 | [`docs/adr/`](docs/adr/) |
| 一族怎么设计的：形状、机制、边界、算术 | [`docs/design/`](docs/design/)（一族一份） |
| 量到的实测结果、测量程序的假实测结果 | [`docs/research/`](docs/research/)（一题一份） |
| 复核阶段要照的规矩、代码级约束 | [`CODING_STANDARDS.md`](CODING_STANDARDS.md) |
| 怎么打成一个 exe、那几道坎 | [`docs/packaging.md`](docs/packaging.md) |
| 还欠着什么 | [`.scratch/keeper-debt/issues/`](.scratch/keeper-debt/issues/) |
| 做完的工单与它的实测结果 | [`.scratch/archive/`](.scratch/archive/) |
| 被否掉的需求 | 契约 [`.scratch/keeper-contract/spec.md`](.scratch/keeper-contract/spec.md)「Out of Scope」 |

## 跑一次自检

**唯一那份跑法在 [`CODING_STANDARDS.md`](CODING_STANDARDS.md)「跑一次自检」一节** —— 本页不抄第二条。

引擎、.NET SDK 和配置目录各在哪台机器上（装机事实，不背任务），记在 `CODING_STANDARDS.md` 同一节里。

## Credit

它复现的行为是 FadeTop（fadetop.com）的。Keeper 是独立重写，仅供学习，与之无关联。
