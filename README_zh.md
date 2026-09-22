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
本地门槛通过 350/350 项回归、32/32 项固定用例，以及独立源码和本地 Cargo 包示例
各自的 ILVerify 与 Windows x64 Native AOT。

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
- [路线图](ROADMAP_zh.md)：里程碑、验收条件和已记录证据。
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
