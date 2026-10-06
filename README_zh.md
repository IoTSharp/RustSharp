<p align="center">
  <img src="docs/static/img/rustsharp-logo.svg" width="120" alt="RustSharp 标志" />
</p>

<h1 align="center">RustSharp</h1>

<p align="center">面向 .NET 的 Rust 兼容语言工具链。</p>

<p align="center">
  <a href="README.md">English</a> | <a href="README_zh.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/IoTSharp/RustSharp/actions/workflows/windows-p0.yml"><img src="https://github.com/IoTSharp/RustSharp/actions/workflows/windows-p0.yml/badge.svg" alt="Windows x64 P0 证据" /></a>
  <a href="https://github.com/IoTSharp/RustSharp/actions/workflows/linux-native-aot.yml"><img src="https://github.com/IoTSharp/RustSharp/actions/workflows/linux-native-aot.yml/badge.svg" alt="Linux x64 Native AOT" /></a>
  <img src="https://img.shields.io/badge/.NET%20SDK-10.0.400-512BD4?logo=dotnet&logoColor=white" alt=".NET SDK 10.0.400" />
</p>

> **🚧 进行中：** RustSharp 仍处于实验阶段。兼容性由具名配置档定义，而不是宣称完整支持 Rust。超出配置档范围的源码会给出诊断，不会被静默赋予 C# 或 CLR 语义。

RustSharp 使用 C# 和 .NET 10 实现，目标是一个刻意限定范围的 Rust 1.98 / Edition 2024 语言实现。`rsc` 编译器读取 `.rs` 源文件及受支持的 `Cargo.toml` 包输入子集，生成 ECMA-335 程序集和 Portable PDB 文件；在已覆盖的配置档中，它可以在 CoreCLR 上运行，也可以通过 .NET Native AOT 发布。

## 当前可用范围

| 范围 | 当前支持 |
| --- | --- |
| `vertical-slice-v1` | 默认配置档：`fn main()` 加字面量 `println!` 语句。 |
| `safe-core-primitives-v1` | 可选配置档：有界文件模块和本地 `path` 包、非泛型函数、`i32` / `bool`、已初始化的可变局部变量、`if` / `else`、返回、受检查算术、比较、布尔运算符和 `println!`。 |
| `safe-core-types-v1` | 可选的仅检查类型配置档：基础数值类型、元组、数组、切片、引用、函数指针、非泛型 ADT、别名、模式/match、闭包、有界 const 求值、推断和有方向的强制转换。不检查借用，也不生成可执行输出。 |
| `safe-core-generics-v1` | 可选的可执行泛型配置档：刚性类型参数主体检查、显式/推断调用、标记 trait 约束和 impl 一致性、元组及泛型结构体，以及经 CLR LIR 输出 IL 和 Native AOT 的闭合主体特化。 |
| 输出 | 对已覆盖的配置档，直接生成 ECMA-335 和 Portable PDB，支持 CoreCLR 运行与 Native AOT 发布。 |

RustSharp 不是 `rustc` 的直接替代品。完整 Rust 兼容性、标准库对等、通用 Cargo 注册表解析、宏展开、所有权与借用检查、Rust ABI 兼容性以及任意 `unsafe` 代码，当前都不是承诺范围。精确边界见[兼容性契约](docs/compatibility.md)。

## 快速开始

请先安装 .NET SDK 10.0.400。仓库在 [global.json](global.json) 中固定该版本，并禁用了 roll-forward。只有运行差异一致性工作时才需要 Rust 1.98.0。

```text
git clone https://github.com/IoTSharp/RustSharp.git
cd RustSharp
dotnet restore RustSharp.slnx
dotnet build RustSharp.slnx -c Release --no-restore
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/safe-core.rs --profile safe-core-primitives-v1
```

该示例会输出一个由 RustSharp 编译的 `i32` / `bool` 小程序。

## CLI

已安装的 CLI 名称为 `rsc`。从源码检出运行时，可使用以下命令查看完整选项：

```text
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- --help
```

当前命令面如下：

```text
rsc check <source.rs|Cargo.toml> [--profile <name>]
rsc build <source.rs|Cargo.toml> [--output <program.dll>] [--profile <name>]
rsc compile <source.rs|Cargo.toml> [--output <program.dll>] [--profile <name>]
rsc run <source.rs|Cargo.toml> [--output <program.dll>] [--timeout <seconds>] [--profile <name>]
rsc publish <source.rs|Cargo.toml> [--runtime <rid>] [--output <directory>] [--timeout <seconds>] [--profile <name>]
```

`compile` 保留为 `build` 的兼容别名。

使用 `rsc check samples/type-system.rs --profile safe-core-types-v1` 检查类型系统示例。
该配置档接受 `check`；可执行命令会在创建输出前报告 `RSC0009`。
配置档范围及独立的生命周期/借用检查边界见[类型系统契约](docs/type-system-profile.md)。

P1-04 对这一已声明的单态类型契约为 ✅ 已完成。已记录的 Windows x64 门槛通过
265/265 项回归与十六个必需类别中的 96/96 项 rustc 差分用例，失败和跳过均为零。

P1-05 对已声明的有界契约为 ✅ 已完成。`safe-core-generics-v1` 通过名称绑定 HIR 检查泛型主体，
特化可达主体和聚合布局，并支持 `check`、`build`、`compile`、`run` 和 `publish`。
[泛型契约](docs/generic-profile.md) 定义了有界标记 trait 子集和固定的 32 用例 rustc
语料，其中包括八项执行比较和五项明确的配置档边界拒绝。
2026-09-19 记录的配置档门槛通过 350/350 项回归和 32/32 项固定用例，独立源码和本地
Cargo 包示例也分别通过 ILVerify 与 Windows x64 Native AOT。P1-05 合并后，可执行
测试工具在该合并后运行中注册并通过 377/377 项测试；这次补充运行使用已安装的 10.0.401 SDK
通过显式 MSBuild 完成，不替代已记录的 10.0.400 Native AOT 证据。

P1-06 对冻结的类型化 MIR 契约为 ✅ 已完成；P1-07 的源码所有权和生命周期范围也为
✅ 已完成；P1-08 的生成 Drop/panic 为 ✅ 已完成。P1-09 和 P1-10 基于已记录的本地证据为 ✅ 已完成；P1 阶段仍为 🚧 进行中。可选的 `safe-core-mir-p1-v2` 配置档在
带源码映射的 HIR → 类型化 MIR → CLR LIR 发射链路上增加结构化 `Copy` 重复数组、
具名及枚举布局、嵌套引用与含引用聚合、已检查常量与提升、模式及捕获闭包。v1 对
重复数组的拒绝契约保持不变。流水线具有确定性快照与 PE/PDB 检查、显式未支持诊断，
以及工作量、大小、深度、时间和取消边界。

共享/可变切片支持数组到切片的 unsizing、`.len()`、动态索引、子切片、写入及
参数/返回值。GC 拥有的引用句柄在嵌套投影、引用槽及切片范围间保持所有者标识。
[P1-06 清单](docs/p1-06-implementation_zh.md)将每个可执行类别映射到已注册测试，
并区分本地证据和完整平台门禁。

P1-08 基于 [Drop/panic 实现清单](docs/p1-08-implementation_zh.md)中的当前证据为
✅ 已完成。该清单记录经过检查的析构接收者、
类型化及共享 drop flag、聚合与临时值清理、unwind/abort 策略和可复用 panic
接口。历史 v3 验收清单包含 28 个源码差分用例，其中两项为显式正常清理契约差异，
以及通过两个原始 PE 产物在 CoreCLR 与 Native AOT 执行的七个直接调用接口用例。
已记录的 P1-08 Release 构建零警告/零错误，回归通过 894/894。差分记录 26 项 rustc 一致与
两项冻结契约差异；ILVerify 通过 30/30 原始 PE。Windows 与 Ubuntu WSL 均通过
Native AOT 28/28 与 callable 7/7。Linux 目标为 `linux-x64`，实际宿主为
`ubuntu.24.04-x64`。验证进程/宿主清理及保留的历史临时对象在记录中分别披露。

有界的复合借用/再借用来源、place/projection 模型及所有权证据现在贯穿类型化
MIR 与 CLR LIR 后端。共享引用复制会克隆借用，`&mut` 引用移动会转移借用，源码逃逸
会得到稳定的所有权诊断。所有权黄金目录固定四个借用负例的源码、消息、路径和范围。
扩展差分报告 `artifacts/p1-next-session/p1-differential-v3-final2.json` 的 32/32 个进程
用例通过，`borrowSemanticClosure` 为 20/20，六个 Drop 用例也已完成语义闭环（6/6）。
平台报告 `artifacts/p1-next-session/p1-platform-v2-final-closed4.json` 的 24 个语义用例
全部通过 CoreCLR、ILVerify 和 Windows Native AOT，绑定及清理证据完整。本地报告没有候选
SHA，因此候选发布门禁仍是独立的 P1 要求。

2026-10-05 的本地 Release 构建零错误/零警告，可执行测试工具使用已安装的 SDK 10.0.401 通过
784/784，失败/跳过均为零；仓库固定版本仍为 10.0.400。日志保留于
`artifacts/p1-next-session`。[后续审计](docs/p1-evidence-audit_zh.md) 记录所有权诊断、Drop
闭环、源码包元数据及平台证据绑定。P1-09 源码包契约基于已记录的本地证据为 ✅ 已完成：历史 Closure-9 Release 零警告/零错误，关键回归84/84、完整回归964/964；这些报告记录清单原始字节 SHA `BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`，在 Windows 与 Ubuntu WSL Native AOT/CoreCLR 均通过19/19及39/39个原始 PE。该历史 CRLF 原始字节哈希不同于当前规范化 LF 后的清单哈希。

P1-10 在本地候选 `0a415e25c362f8a35c09cb9e1163f5ce30accf82` 为 ✅ 已完成：Windows 与 Ubuntu Linux x64 分别通过零警告/零错误的 Release 构建、969/969 项完整测试、32/32 项扩展差分、24/24 项 CoreCLR/ILVerify/Native AOT 用例及 60/60 项不可变基线审计。严格聚合通过 15/15。[P1-10 实现记录](docs/p1-10-implementation_zh.md)区分 40 个需求/160 条清单记录的覆盖账本与真实语义执行，并记录 `p1-drop-closure-v4` 中额外的 Ubuntu rustc Drop 轨迹差异。该差异尚未获 P1 语言门禁批准。六个 P1-GATE 叶子仍为 ⏳ 计划中；此候选尚未运行远程 CI。

已记录的 `safe-core-regression-v1` 报告通过 8/8，失败和跳过均为零。
历史 `safe-core-regression-v2` 报告通过 24/24，包含 1 个编译通过、6 个编译失败、
13 个运行通过和 4 个差分用例；其中 rustc 1.98.0 进程记录的失败、阻塞和跳过均为零。
不可变的 v2 清单保持原样。`safe-core-regression-v3` 以新版本记录现已可执行的
or-pattern 和可变捕获预期，并增加综合 MIR 类别与投影示例：固定 26 用例
（1 个编译通过、4 个编译失败、17 个运行通过和 4 个差分），通过 26/26，失败、阻塞及
跳过均为零。两个示例的 CoreCLR 和 Windows x64 Native AOT 输出均与 rustc 1.98.0
一致，并通过 ILVerify 10.0.11，没有抑制诊断。
`p1-exit-gate-v1` 通过 5/5 个进程内库探针，并明确记录 `"nativeAot": false` 和
`"crossPlatform": false`。历史不可变的 `p1-differential-v2` 清单针对 rustc 1.98.0
执行 16/16 项（10 借用、6 Drop），失败、阻塞和跳过均为零。新的
`p1-platform.yml` 工作流在原生 Windows/Linux x64 runner 上固定 12 个运行通过用例，
并在每个平台运行 26 用例的 v3 回归套件，聚合覆盖 CoreCLR、ILVerify、Native AOT、差分和
回归证据的 6 份报告。[运行 35848782833](https://github.com/IoTSharp/RustSharp/actions/runs/35848782833)
已在历史提交 `23279d93267a814c643baddc29c72918ff0fda0b` 上通过全部 6 个门禁。
该运行不验证上述后续新增能力；P1 阶段仍因语义及最终提交证据缺口保持开放。

本地 hello 探测提供 ILVerify、CoreCLR 和 Windows x64 Native AOT 证据。
Linux x64 Native AOT hello 也在 Ubuntu WSL2 与 SDK 10.0.112 下运行；原生 Linux
探测器明确排除 WSL2 的原生主机声明。两者都不能证明完整 P1 语言范围。完成要求扩展后
的固定分母通过 CoreCLR、ILVerify、原生 Windows/Linux x64 AOT 和 rustc 1.98，
失败/跳过均为零，且 CI 对应最终推送的 SHA。[P1 缺口矩阵](docs/p1-gap-matrix.md)
将每项剩余要求关联到实现、测试、本地证据和 CI 证据；[类型化 MIR 契约](docs/typed-mir-profile.md)
定义当前实现边界。

使用以下命令运行泛型示例，输出 `42` 和 `true`：

```text
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/generics.rs --profile safe-core-generics-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run tests/workspaces/generics/Cargo.toml --profile safe-core-generics-v1
```

Cargo 示例通过本地依赖的泛型 `Container<T>` 和函数主体，以及为本地结构体实现的
外部标记 trait，产生相同输出。源码链接的泛型定义持久化至输出程序集的
`RustSharp.Generics.v1.json` 资源。

## 仓库导览

| 路径 | 用途 |
| --- | --- |
| [src](src) | 编译器、语法、语义、IL 代码生成、运行时和 CLI 项目。 |
| [samples](samples) | 用于运行和类型检查的小型 RustSharp 程序。 |
| [tests](tests) | 有界的可执行回归测试工具。 |
| [docs](docs) | 兼容性契约和架构决策。 |

## 文档

- [兼容性契约](docs/compatibility.md)：已声明的语言和运行时边界。
- [语言契约](docs/lexical-profile.md)：词法、[语法](docs/syntax-profile.md)、[模块](docs/module-profile.md)和[类型系统](docs/type-system-profile.md)配置档细节。
- [路线图](ROADMAP_zh.md)：父里程碑与已记录证据；[颗粒化执行计划](docs/roadmap/README_zh.md) 覆盖全部 P0～P6，包含 365 个实施叶子和 32 个独立门禁叶子。每个叶子单独声明交付、依赖、验收和证据，完成的子集可以独立结项，而不冒称整个阶段完成。
- [架构决策](docs/adr)：约束实现的决策，包括[安全核心基础类型配置档](docs/adr/0007-safe-core-primitives.md)。

## 开发

完成 Release 构建后，使用以下命令运行有界可执行测试工具：

```text
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-build --no-restore
```

本仓库当前不使用基于 Test SDK 的 `dotnet test` 测试套件。

## 参与贡献

提出语言行为变更前，请阅读对应的兼容性契约和 ADR。在同一项变更中保持实现、测试与受影响文档一致。

## 说明

[LICENSE-UNICODE](LICENSE-UNICODE) 已包含在本仓库中。其适用范围和条款以该文件的说明为准。
