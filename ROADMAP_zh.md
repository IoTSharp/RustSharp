# Rust# 实现路线图

[English](ROADMAP.md) | 简体中文

> Rust# 是一个使用 C# 和 .NET 10 编写的编译器与语言工具链。它将兼容 Rust 的
> 源代码编译为 ECMA-335 IL 和 Portable PDB 文件。IL 是编译器输出，而不是实现
> 编译器所使用的语言。同一个生成程序必须能够在 CoreCLR 上运行，并且对于受支持的
> 配置档，必须通过 .NET 10 Native AOT 工具链。

本路线图将已商定的产品范围转化为以证据为依据的工作项。只有通过退出门槛后，
里程碑才会推进；阶段名称和 90 天规划窗口表示依赖顺序与团队容量，而不是对发布
日期的承诺。

## 状态与范围

### 状态图例

| 标记 | 含义 |
| --- | --- |
| `✅ 已完成` | 仓库中包含所述制品或已接受的决策，并且所述证据已经过检查。 |
| `🚧 进行中` | 已有实现或工作已经开始，但其验收证据或退出门槛尚未完成。 |
| `⏳ 计划中` | 尚无符合条件的实现证据。 |
| `⛔ 已阻塞` | 某项硬依赖尚未通过门槛；下游工作可以进行设计，但不能宣布完成。 |

状态标记记录的是仓库证据，而不是意图。仅有一个代码文件并不能证明运行时行为、
IL 有效性、AOT 兼容性或语义兼容性。

### 已商定的产品边界

| 领域 | 决策 |
| --- | --- |
| 产品与 CLI | 语言名称为 **Rust#**；命令名称为 **`rsc`**；源文件使用 `.rs`。 |
| 语言基线 | Rust 1.98.0、Edition 2024，通过明确的兼容性配置档交付。 |
| 编译器实现 | C# 和 .NET 10。生产路径中不进行 Rust 到 C# 的转译。 |
| 编译器输出 | 使用 `System.Reflection.Metadata` 发出确定性的 ECMA-335 程序集和 Portable PDB 文件。 |
| 内存模型 | 采用托管混合存储，由编译器强制执行移动、借用、生命周期、别名和确定性 `Drop` 语义。GC 不会削弱 Rust 安全规则。 |
| 输出 | 普通 .NET 可执行文件/库、Native AOT 可执行文件，以及显式导出 C ABI 的 Native AOT 库。 |
| 平台顺序 | 首先支持 Windows/Linux x64；通过 x64 门槛后，再支持 Windows/Linux ARM64 和 macOS x64/ARM64。原生制品在受支持的原生 CI 运行器上构建。 |
| 包 | 兼容 Cargo 的清单概念和 `Cargo.toml`；Rust# 包通过经过 AOT 审核的 NuGet 源分发。 |
| .NET 互操作 | 为兼容 AOT 的 NuGet 库提供明确且版本化的 .NET 互操作边界；具体语法在实现前通过 ADR 冻结。 |
| 宏 | 首先实现内置宏和 `macro_rules!`；过程宏稍后在有界的进程外宿主中实现。 |
| `unsafe` | 在已声明的配置档中支持裸指针、`repr(C)`、C FFI、联合体和固定布局。在另行规定之前，不包括 Rust ABI、任意内部函数、不受限制的 `transmute` 和内联汇编。 |
| 应用 API | 文件、网络、异步、HTTP、TLS、WebSocket 和数据库访问。 |
| 兼容性库 | 为 `tokio`、`reqwest`、`axum`、`sqlx`、`tiberius` 和 `sea-orm` 提供精确版本的 API 配置档；`diesel` 属于后续工作。这些是 Rust# 对指定配置档的实现，并不承诺上游源代码无需修改即可编译。 |
| 开发者工具 | `rsc new/check/build/run/test/fmt/doc/publish`、还原、LSP、VS Code 和 Portable PDB 调试。Visual Studio 和 Rider 集成属于后续工作。 |

### 当前仓库基线

以下状态基于仓库内容和下方记录的验证运行。某行标记为已完成，表示所述证据存在；
这并不意味着后续语言或库配置档已经完成。

| ID | 状态 | 证据 |
| --- | --- | --- |
| BASE-01 | ✅ 已完成 | 已存在 `.slnx`、集中式构建/包文件、.NET 10 项目、CLI/编译器/语法/代码生成边界、示例以及测试项目骨架。 |
| BASE-02 | ✅ 已完成 | ADR 0001 固定 Rust 1.98/Edition 2024；ADR 0002 固定 C#/.NET 10 和 IL 输出；ADR 0003 固定首个纵向切片。 |
| BASE-03 | ✅ 已完成 | `docs/compatibility.md` 定义了初始 `vertical-slice-v1` 配置档和明确的非兼容边界。 |
| BASE-04 | ✅ 已完成 | `BoundedProcessRunner` 实现有界执行、进程元数据、输出限制、取消和自有进程树清理；`eng/Invoke-BoundedProcess.ps1` 是有界的根进程冒烟测试辅助脚本。可执行测试工具记录了超时、取消、输出限制和子进程用例。 |
| BASE-05 | ✅ 已完成 | 解析器识别范围有限的 `fn main()`/`println!(string)` 配置档并发出稳定的源码诊断；纵向切片语法以及转义/注释回归用例在可执行测试工具中通过。 |
| BASE-06 | ✅ 已完成 | 下文记录了直接发出 PE/Portable PDB、元数据检查、CoreCLR 执行、Windows x64 Native AOT 执行、磁盘输出确定性检查以及独立 ILVerify 运行。 |

## 架构与依赖规则

生产流水线如下：

```text
Cargo.toml / .rs
  -> lexer and token trees
  -> parser and macro expansion
  -> AST -> HIR and name resolution
  -> type inference and trait solving
  -> typed MIR and control-flow analysis
  -> move, borrow, lifetime, and Drop checking
  -> generic monomorphization and layout
  -> CLR-oriented low-level IR
  -> System.Reflection.Metadata emitter
  -> ECMA-335 PE + Portable PDB + Rust# metadata
  -> CoreCLR or .NET 10 Native AOT
```

Rust# 包元数据必须携带 CLR 元数据无法表达的语言信息，包括 trait 实现、单态化所需的
泛型主体、兼容性配置档标识以及相关 MIR 契约。

硬性阶段依赖为 `P0 -> P1 -> P2 -> P3`、`P3 -> P4`、`P3 -> P5` 和
`P2 -> P6`；最终达到 1.0 就绪，需完成适用的 P4、P5 和 P6 配置档。
工作可以提前制作原型，但只要前置门槛仍未通过，依赖它的阶段就不能通过。

由 `rsc`、测试基础设施或构建脚本启动的每个外部进程，都必须具有有限的项目数量上限
和墙钟超时，支持取消，记录 PID/启动时间/命令/父进程，并在等价于 `finally` 的路径中
仅清理其自有进程树和临时文件。

纵向 Native AOT 发布器要求输出目录由当前调用独占。对同一输出目录的并发发布、
文件系统别名冲突加固，以及对被外部锁定的已提交制品进行恢复，属于后续门槛；
它们不是首个切片的兼容性声明。

## P0：验证纵向架构

P0 在扩展语言表面之前，证明所选架构能够端到端工作。这是第一个 90 天规划窗口；
各批次保持足够小，以便独立评审和合并。

当前仓库使用有界的可执行测试工具，而不是测试 SDK/适配器。其验收命令为：

`dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore`

为未来筛选器列出的 `dotnet test` 命令，要等测试 SDK 和可筛选的一致性测试套件引入后
才能执行；它们并不是对当前测试工具能力的声明。

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P0-01 | ✅ 已完成 | 创建 .NET 10 解决方案和项目边界。 | 无 | `dotnet sln RustSharp.slnx list` | 列出语法、IL 代码生成、编译器、CLI 和测试项目。 |
| P0-02 | ✅ 已完成 | 记录语言、编译器/输出和首个切片的决策。 | 无 | `Get-ChildItem docs/adr/*.md` | ADR 0001-0006 均存在，并且每个都注明 `Status: Accepted`。 |
| P0-03 | ✅ 已完成 | 定义首个版本化兼容性配置档。 | P0-02 | `Get-Content docs/compatibility.md` | 明确说明 `vertical-slice-v1`、Rust 1.98.0、Edition 2024 以及不作出的承诺。 |
| P0-04 | ✅ 已完成 | 完成并测试有界进程执行和自有资源清理。 | P0-01 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 有界可执行用例通过，包括退出、超时、取消、输出限制、并发输出排空和自有子进程清理；进程记录包含 PID、启动时间、命令、父进程和已用时间。 |
| P0-05 | ✅ 已完成 | 稳定适用于 `fn main()` 加字面量 `println!` 的有限词法分析器/解析器及诊断。 | P0-03 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 有效示例可成功解析；格式错误的分隔符、嵌套注释、转义、行尾和尾随词元均以稳定的代码和范围报告失败。 |
| P0-06 | ✅ 已完成 | 直接从 C# 发出可执行 PE 和 Portable PDB。 | P0-05 | `dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll` | 在不生成 C# 程序逻辑的情况下产生 `hello.dll`、运行时配置和非空 PDB；发射器测试还证明，同一输入重复发出的字节完全相同。 |
| P0-07 | ✅ 已完成 | 验证元数据、IL 栈正确性和确定性输出。 | P0-06 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` 加 `pwsh -NoProfile -File eng/Invoke-ILVerify.ps1` | PE/元数据/PDB 读取器、`ilspycmd` 和磁盘确定性输出测试解析出预期入口点、序列点、IL 栈/词元，以及字节完全相同的 PE/PDB/runtimeconfig 文件。固定版本的独立 `dotnet-ilverify` 10.0.11 运行使用显式 `System.Private.CoreLib`/运行时引用，以退出码 0 结束，并归档 JSON 证据。 |
| P0-08 | ✅ 已完成 | 在 CoreCLR 上运行生成的程序集。 | P0-06 | `dotnet artifacts/p0/hello.dll` | 退出码为 0，stdout 恰好为 `Hello from Rust#` 加平台换行符。此运行时冒烟测试门槛独立于 P0-07 中可选的独立 IL 验证器。 |
| P0-09 | ✅ 已完成 | 完成有界 Native AOT 发布适配器，并在 Windows x64 上运行原生可执行文件。 | P0-04, P0-08 | `dotnet run --project src/RustSharp.Cli -- publish samples/hello.rs --runtime win-x64 --output artifacts/p0/aot` | 发布以退出码 0 结束，未观察到 AOT/裁剪警告（警告视为错误）；原生可执行文件打印预期文本；发布器在报告成功前移除其自有宿主目录。 |
| P0-10 | ✅ 已完成 | 在 Linux x64 原生运行器上重复可执行切片。 | P0-09 | `bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300` | [Linux 运行 `33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620) 生成并运行了 x86-64 ELF，退出码为 0，文本与 CoreCLR 完全相同；其有界证据记录了完整的自有资源清理。 |
| P0-11 | ✅ 已完成 | 构建 rustc 1.98 差异/一致性测试工具。 | P0-03, P0-04 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile vertical-slice-v1 --oracle rustc-1.98` | 该工具调用 `rustc +1.98.0`，发出包含通过/失败/运行输出、诊断、工具版本、超时和配置档基准集合的机器可读报告，本地 4 用例基准集合通过。 |
| P0-12 | ✅ 已完成 | 验证局部变量、调用、分支和返回的类型化 IR 可行性。 | P0-07 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 八个 CLR LIR 用例在可执行测试工具中通过；栈/类型验证在发出 PE 前拒绝无效 IR，有效的分支/控制流 PE 以预期结果运行。 |
| P0-13 | ✅ 已完成 | 在小型 MIR 上验证移动、共享/可变借用、非词法生命周期和确定性 `Drop`。 | P0-12 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 八个有界所有权用例通过；验证了移动后使用、重叠可变借用、引用逃逸、显式 NLL 结束和声明逆序 Drop。 |
| P0-14 | ✅ 已完成 | 验证泛型以及有界 trait 解析子集。 | P0-12 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | `Option<i32>` 以确定性方式单态化；精确、缺失和歧义的有界 trait 用例在深度/工作量限制下通过。 |
| P0-15 | ✅ 已完成 | 验证托管混合运行时映射和明确的 .NET 互操作边界。 | P0-13 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 共享/独占托管借用、所有者/drop 作用域以及显式静态泛型互操作调用在不使用反射或动态代码的情况下通过。 |
| P0-16 | ✅ 已完成 | 在不使用基于反射的代码生成的情况下，验证文件、TCP、异步和 SQLite 纵向示例。 | P0-13, P0-15 | `dotnet run --project tools/RustSharp.Smoke -- --profile p0-io` | 已记录的 Windows 和 Linux 运行均通过全部 4/4 个有界探测，包括参数化 SQLite；失败/跳过项均为零，且无清理诊断。 |
| P0-17 | ✅ 已完成 | 添加 Windows/Linux x64 CI 并归档门槛证据。 | P0-07, P0-10, P0-11 | CI 工作流加有界测试工具 | 提交 `286f139` 通过了 [Windows 运行 `33857817622`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817622) 和 [Linux 运行 `33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620)；两者各自上传了含 14 个文件的平台归档，覆盖可执行测试、IL/一致性测试、冒烟测试和 Native AOT 证据。 |

### 已记录的纵向切片证据

以下本地和 CI 证据于 2026-09-02 至 2026-09-04 使用 Windows 和 Linux x64
上的 .NET SDK 10.0.400 收集。本地生成的二进制文件和日志位于被忽略的
`artifacts/` 目录下，可以使用下列命令重新生成；最终的平台归档附加在已记录的
GitHub Actions 运行中。

- Release 解决方案构建完成，零警告、零错误。
- 提交 `286f139` 通过了 P0-17 中链接的 Windows 和 Linux x64 工作流。每个平台制品包含 14 个文件和七份可解析的 JSON 报告；两者都记录了 73/73 可执行测试、4/4 纵向一致性、6/6 安全核心语法、6/6 安全核心名称解析、成功的独立 IL 验证和 4/4 I/O 冒烟探测。安全核心语法和名称解析语料中的全部 12 个 `.rs` 源 SHA-256 值在两平台间一致。
- `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` 完成 74/74 项测试，包括五个有界词法分析用例、13 个安全核心语法用例、十个安全核心名称解析用例、四个安全核心 HIR 降低用例、磁盘确定性输出和 IL 健全性门槛、八个类型化 CLR LIR 用例、八个所有权 MIR 用例，以及有界泛型/运行时用例。
- `dotnet run --project src/RustSharp.Cli -- check samples/hello.rs` 和 `dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs` 成功完成。打包/安装 CLI 工具后可以使用 `rsc` 工具名称；不假定它在源码检出环境的 PATH 中。
- 生成的 DLL 在 CoreCLR 上运行并打印 `Hello from Rust#`。
- Windows x64 Native AOT 发布完成，未观察到 AOT/裁剪警告（发布使用 `-warnaserror`）；生成的可执行文件运行并打印 `Hello from Rust#`。
- `eng/Invoke-WindowsNativeAotProbe.ps1` 完成有界的本地和 CI Windows x64 发布/运行，并得到 `status=passed`、精确的 `Hello from Rust#` 输出、记录的 PID/父 PID 元数据、原生 PE32+ AMD64 验证，以及完整的临时宿主清理。
- `PEReader`、`MetadataReader` 和 `ilspycmd --ilcode` 检查确认了托管入口点、生成的 IL、Portable PDB 文档、序列点和源校验和行为（包括 UTF-8 BOM 输入）。
- 本地 `.config/dotnet-tools.json` 清单还原了固定版本的 `dotnet-ilverify` 10.0.11 工具。它以 `System.Private.CoreLib` 作为系统模块，并使用 .NET 10.0.11 运行时引用目录，验证了 `artifacts/p0/hello.dll`。该进程以退出码 0 结束并报告 `All Classes and Methods ... Verified`；`eng/Invoke-ILVerify.ps1` 将命令、PID/启动时间、引用、有界输出、SHA-256、环境和清理状态归档到 `artifacts/p0/hello.ilverify.json`。
- 由于 WSL 安装的是 SDK 10.0.111，而非固定的 10.0.400，Linux x64 探测在此 Windows 宿主上记录有界的 `skipped` 结果。与此独立的是，已记录的 Linux 运行 `33857817620` 在原生 Ubuntu 24.04 x64 上执行，生成 x86-64 ELF，以退出码 0 输出精确的 `Hello from Rust#`，并归档完整的进程与清理证据。
- 一致性测试工具在 `artifacts/conformance/vertical-slice-v1.json` 生成通过报告：`rustc +1.98.0` 报告 `rustc 1.98.0`，全部四个用例（两个 run-pass 和两个 compile-fail）都以匹配的结果和输出执行。报告记录了预言工具/工具链版本、限制、进程元数据和清理结果。
- `safe-core-name-resolution` 验收报告通过其六用例清单，并记录精确的基准集合、有界解析器/名称解析器限制、预期诊断和路径解析、源哈希，以及 `name-resolution-acceptance` 证据范围。它明确将 rustc 一致性和运行时一致性都记录为 false。
- 所有权 MIR 探索性实现和有界泛型/运行时探测均包含在 73 用例可执行测试工具中。所有权部分记录移动/借用/NLL/Drop 跟踪；泛型和托管混合探测验证确定性的 `Option<i32>` 闭包、有界 trait 解析、独占借用、逆序清理、固定和静态互操作。
- `dotnet run --project tools/RustSharp.Smoke -c Release --no-restore -- --profile p0-io` 记录机器可读报告。未安装 `sqlite3` 的本地宿主仍明确处于 ⛔ 已阻塞（`blocked`）状态，为 3 项通过、1 项跳过；两个已记录的 CI 平台均执行 SQLite 事务并通过 4/4。Windows CI 在运行未放宽的严格门禁前安装并验证 SQLite 3.53.4。

当前测试项目有意保持为可执行测试工具；它不声称支持 `dotnet test` 发现。在已记录的
提交 `286f139` 上，P0-04 至 P0-17 均在干净 CI 构建中满足其可观察结果，因此 P0
阶段为 ✅ 已完成。P1 语言配置档工作仍独立受其已发布分母和生产管线集成的门禁约束。

### 首个 90 天批次顺序

| 批次 | 包含的 ID | 适合合并的成果 |
| --- | --- | --- |
| B01 | P0-04 | 有界进程行为具备确定性测试和清理证据。 |
| B02 | P0-05 | 当前语法配置档具备通过/失败用例和稳定诊断。 |
| B03 | P0-06, P0-07 | 一个直接生成的 IL/PDB 制品具备确定性且可验证。 |
| B04 | P0-08 | 发出的程序在 CoreCLR 上运行并产生精确输出。 |
| B05 | P0-09 | Windows x64 Native AOT 通过，且没有 AOT/裁剪警告。 |
| B06 | P0-10, P0-17 | Linux x64 对等性和首个双平台 CI 门槛可见。 |
| B07 | P0-11 | 差异测试报告已版本化且可复现。 |
| B08 | P0-12 | 类型化 CLR 低级 IR 可阻止格式错误的 IL 进入发出阶段。 |
| B09 | P0-13 | 已证明所有权、借用、NLL 和 Drop 的可行性。 |
| B10 | P0-14 | 已证明泛型单态化和初始 trait 求解器。 |
| B11 | P0-15 | 托管混合存储和明确的 .NET 互操作可经受 AOT。 |
| B12 | P0-16 | 文件、TCP、异步和 SQLite 的端到端探索性实现通过。 |

提交 `286f139` 满足 P0 退出规则：P0-04 至 P0-17 在有记录的干净构建中全部通过，
因此 P0 为 ✅ 已完成。未来任何借用/Drop、可验证 IL 或 Native AOT 回归，都会在
P1 进一步扩展语法前触发 ADR 评审。

## P1：实现安全语言核心

`RustLexer.cs` 和 `RustLexingModels.cs` 中已有一个早期 P1-01 原型，它保留
词元/trivia 范围、构建嵌套词元树，并限制格式错误输入的诊断。版本化的
`safe-core-lexing` 清单现在为已声明的标识符、字面量、trivia、分隔符、词元树和
非法形式发布有界的词法分析器验收分母。其报告在声明的限制内核对精确证据、范围和
无损源码重建。当前实现和清单按优先顺序覆盖已审计的本批行为：字面量后缀归入同一个
字面量词元；原始生命周期和包括 `'0` 在内以数字开头的生命周期形式；Edition 2024
受保护字符串（guarded strings）；以及保留前缀。这只属于 RustSharp 词法分析器验收
证据，并不构成 rustc 差分或运行时一致性证据，而且该语料也不是完整的 Rust 1.98
词法分母。纵向切片解析器仍是默认路径；按需启用的基础类型配置现在已在生产路径中
使用安全核心词法/语法分析器。完整词法分母仍未完成，因此 P1-01 保持 🚧 进行中。

P1-02 也已有早期的有界 `SafeCoreSyntax` 模型/解析器，覆盖代表性的模块、项、语句、
表达式、模式、类型、泛型和属性，并提供稳定的 `RSP` 诊断。一个六用例
`safe-core-syntax` 清单和下方验收命令生成
`artifacts/conformance/safe-core-syntax.json`。当前报告通过 6/6 个用例，且仅作为
解析器验收证据，而不是 rustc 差异或运行时一致性证据。P1-02 保持 🚧 进行中，因为
它的 P1-01 依赖仍未完成，完整语法基准集合也尚未发布。

P1-03 现在基于该语法模型提供了有界的 `SafeCoreNameResolution` 原型。它在独立的
类型/值命名空间中收集模块、导入、项、泛型、参数和局部符号，并实现别名、限定路径、
`crate`/`self`/`super`、可见性、重复/歧义/未解析名称、导入循环诊断、规范的原始标识符
和 NFC 规范化标识符，以及明确的工作量限制。其九个本地测试工具用例覆盖命名空间/
符号收集、导入和限定路径、重复/歧义名称、可见性和未解析名称、导入循环、声明顺序和
合法遮蔽、拒绝对函数局部变量进行限定访问、结构体字段和枚举泛型参数、Unicode 规范化，
以及符号/导入嵌套限制。一个六用例 `safe-core-name-resolution` 清单发布当前的有界
验收基准集合。`SafeCoreHirLowering` 还将成功的树降低为确定性的平面 arena，其中包含
已绑定的声明/引用符号；四个测试工具用例覆盖确定性 ID、代表性节点形态、依赖失败、
Unicode 等价绑定和明确的工作量限制。这些属于前端证据；基础类型配置现在已将这些
阶段接入编译器。工作区/多文件模块加载、完整的安全核心基准集合和更广泛的诊断覆盖
仍未完成。

### P1 首个可执行批次

按需启用的 `safe-core-primitives-v1` 配置遵循
[ADR 0007](docs/adr/0007-safe-core-primitives.md)：C# 前端 -> 名称绑定 HIR ->
基础类型检查 -> 经过验证的 CLR LIR -> 直接生成 IL/Portable PDB -> CoreCLR 或
Native AOT。它支持内联模块/导入、i32/bool 函数、带初始化器的局部变量、可变性、
调用、条件分支、返回、带检查的算术运算、比较、短路逻辑和受限的内置打印。
不支持的类型和具有所有权的构造会被拒绝。完整的类型化 MIR、借用/NLL 和确定性
Drop 仍是独立门槛。

基础类型回归测试工具和包含 14 个用例的 rustc 1.98.0 差分套件检查运行行为与输出前
拒绝。验收命令如下：

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98 --report artifacts/p1/safe-core-primitives-v1.json
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- compile samples/safe-core.rs --profile safe-core-primitives-v1 --output artifacts/p1/safe-core.dll
pwsh -NoProfile -File eng/Invoke-ILVerify.ps1 -AssemblyPath artifacts/p1/safe-core.dll -EvidencePath artifacts/p1/safe-core.ilverify.json
```

Windows/Linux CI 现在包含基础类型差分和 IL 门槛；Windows 还包含基础类型 Native
AOT 探测器。这些工作流修改需要新的 CI 运行。此配置的 Linux Native AOT 和完整 P1
退出门槛仍未完成。此前记录的 P0 CI 证据不能验证这些新修改。

2026-09-06 在 Windows x64 上使用 .NET SDK 10.0.400、运行时 10.0.11 和 rustc
1.98.0 记录的本地证据：本批次 91/91 项可执行回归、14/14 项基础类型差分、4/4 项
纵向差分、6/6 项语法和 6/6 项名称解析验收均为 ✅ 已完成。Release 解决方案构建
零警告、零错误。`artifacts/p1/safe-core.ilverify.json` 记录独立 ILVerify 验证通过。
`artifacts/p1/windows-x64-aot-final.json` 记录不带 CLR 头的原生 AMD64 PE、退出码 0、
未观察到发布警告且没有清理诊断。CoreCLR 和 Native AOT 都输出 `Safe core on .NET`、
`42` 和 `true`。完整 P1 里程碑仍为 🚧 进行中。

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P1-01 | 🚧 进行中 | 为 Rust 1.98 词法形式实现无损词元化和词元树。 | P0 门槛 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-lexing` | 当前实现和清单覆盖字面量后缀单词元化、原始生命周期和包括 `'0` 在内以数字开头的生命周期形式、Edition 2024 受保护字符串（guarded strings）以及保留前缀；每个用例都必须与精确的词元、trivia、词元树、诊断、范围和源码重建证据匹配。按需启用的基础类型配置已接入生产路径；完整 Rust 1.98 词法分母仍未完成。 |
| P1-02 | 🚧 进行中 | 解析安全核心配置档中的模块、项、语句、表达式、模式、类型、泛型和属性。 | P1-01 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax` | 已发布语法配置档基准集合中的每个用例都具有预期解析结果；明确拒绝不支持的语法。 |
| P1-03 | 🚧 进行中 | 将 AST 降低为 HIR，并实现模块、命名空间、可见性、导入和名称解析。 | P1-02 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution`<br>`dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 保留六用例内存验收基准集合和四个 HIR 用例；基础类型编译现在使用 HIR 和规范化的导入目标。多文件工作区加载仍未完成。 |
| P1-04 | 🚧 进行中 | 实现原始类型、元组、数组、切片、引用、函数、ADT 和 never 类型，以及推断/强制转换规则。 | P1-03 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | 基础类型配置已实现 i32/bool/unit、直接函数签名、局部推断、可变性、条件/返回检查和发散控制流。聚合/引用类型与完整推断/强制转换基准集合仍未完成。 |
| P1-05 | ⏳ 计划中 | 实现泛型替换、单态化、impl 一致性和版本化 trait 求解器子集。 | P0-14, P1-04 | `dotnet test RustSharp.slnx -c Release --filter GenericsAndTraits` | 泛型函数/类型发出封闭且 AOT 可达的主体；重叠、歧义和缺失约束会以可预测方式失败。 |
| P1-06 | ⏳ 计划中 | 定义类型化 MIR、CFG 验证、脱糖和源码映射。 | P1-04 | `dotnet test RustSharp.slnx -c Release --filter Mir` | MIR 快照具有确定性；无效边/类型被拒绝；诊断映射回 `.rs` 范围。 |
| P1-07 | ⏳ 计划中 | 为该配置档实现移动路径、借用检查、非词法生命周期、再借用和逃逸分析。 | P0-13, P1-06 | `dotnet run --project tools/RustSharp.Conformance -- --profile safe-core-borrow` | 所有已声明的借用编译通过/失败用例都与 rustc 结果匹配，且不会在 CLR 规则下静默接受被拒绝的构造。 |
| P1-08 | ⏳ 计划中 | 实现作用域清理、确定性 `Drop`、展开/中止配置档行为和 panic 边界。 | P1-06, P1-07 | `dotnet test RustSharp.slnx -c Release --filter DropAndPanic` | 正常/提前返回/分支/panic 路径在 CoreCLR 和 AOT 上按指定顺序恰好运行一次析构函数。 |
| P1-09 | 🚧 进行中 | 通过 CLR LIR 发出带有 Rust# 跨包元数据的安全核心程序。 | P0-07, P1-05, P1-08 | `rsc build tests/programs/safe-core/Cargo.toml`（未来完整门槛；当前基础类型命令见上文） | 基础类型多函数 IL/PDB 发射已接入。泛型/所有权降低、跨包元数据和独立消费者编译仍未完成。 |
| P1-10 | 🚧 进行中 | 建立编译通过、编译失败、运行通过和差异回归测试套件。 | P0-11, P1-09 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98` | 初始 14 用例基准集合包含 5 个运行通过用例和 9 个编译失败用例。完整安全核心和借用/Drop 差分基准集合仍未完成。 |

当版本化安全核心配置档在 CoreCLR 以及 Windows/Linux x64 Native AOT 上通过，且该
配置档内的借用/Drop 行为不存在未解决的语义差异时，P1 才能退出。

## P2：交付核心库和可用工具链

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P2-01 | ⏳ 计划中 | 实现以 Rust 命名的 `core` 原语、`Option`、`Result`、格式化、比较、哈希和迭代器基础。 | P1 门槛 | `rsc test library/core/Cargo.toml` | 配置档清单中的公共名称/签名存在，且行为测试在 CoreCLR/AOT 上通过。 |
| P2-02 | ⏳ 计划中 | 使用托管混合模型为 `Box`、`Vec`、`String`、`Rc`、`Arc` 和集合实现 `alloc` 配置档。 | P2-01 | `rsc test library/alloc/Cargo.toml` | 所有权、容量、索引、迭代、Drop、线程安全和分配限制测试通过。 |
| P2-03 | ⏳ 计划中 | 实现 `std::io`、`std::fs`、`std::path`、环境、时间、进程、线程、同步和 `std::net` 配置档。 | P2-02 | `rsc test library/std/Cargo.toml` | 文件/目录操作、流、路径、进程取消、同步、TCP/UDP 和 DNS 示例在受支持的 x64 平台上通过。 |
| P2-04 | ⏳ 计划中 | 解析兼容 Cargo 的包/工作区清单、feature、目标 `cfg`、锁定数据和依赖图。 | P1-03 | `rsc check tests/workspaces/basic/Cargo.toml --locked` | 解析具有确定性；feature 合并和受支持的 `cfg` 用例与已记录的 Cargo 子集匹配；对不支持的键给出清晰诊断。 |
| P2-05 | ⏳ 计划中 | 从受控 NuGet 源还原带有完整性、目标/配置档和 AOT 元数据的 Rust# 包。 | P2-04 | `rsc restore tests/workspaces/packages/Cargo.toml --locked` | 精确的包可以可复现地还原；不兼容的配置档/RID/AOT 包在编译前失败。 |
| P2-06 | ⏳ 计划中 | 冻结并实现版本化的 `extern "dotnet"` 风格互操作和普通 .NET 库输出。 | P0-15, P1-09 | `rsc build tests/interop/dotnet/Cargo.toml --target dotnet-library` | C# 消费者调用生成的库；Rust# 调用 AOT 安全的 NuGet API；不支持的反射/动态代码路径产生诊断。 |
| P2-07 | ⏳ 计划中 | 实现 `rsc new/check/build/run/test/publish` 和依赖还原，并提供稳定的退出码和诊断。 | P2-04, P2-05 | `rsc test tests/cli/Cargo.toml` | 每个命令都有成功/失败黄金测试、取消、有限超时，且不会泄漏自有进程/文件。 |
| P2-08 | ⏳ 计划中 | 实现格式化程序、文档生成器、增量缓存键和确定性构建。 | P1-02, P1-09 | `rsc fmt --check tests/programs; rsc doc tests/programs/Cargo.toml; rsc build tests/programs --locked` | 格式化具有幂等性，文档链接正确，未更改的构建复用有效制品，干净输出可复现。 |
| P2-09 | ⏳ 计划中 | 实现 LSP、VS Code 集成和 Portable PDB 单步调试。 | P1-03, P1-06, P2-07 | `dotnet test RustSharp.slnx -c Release --filter LanguageServer` | 打开/更改/诊断/补全/定义/重命名测试通过，调试器从生成代码单步执行到预期 `.rs` 行。 |
| P2-10 | ⏳ 计划中 | 为 Windows/Linux x64 发布首个有文档记录的 SDK/包/配置档集合。 | P2-01 至 P2-09 | `rsc publish samples/file-server/Cargo.toml --runtime win-x64 --locked` | 干净机器可以只使用有文档记录的输入来还原、构建、测试、调试和 AOT 发布示例。 |

当新用户可以创建包、使用已声明的 `core`/`alloc`/`std` API、使用兼容的 NuGet
依赖项、进行调试，并且无需未记录的步骤即可为 Windows 和 Linux x64 发布同一应用时，
P2 才能退出。

## P3：添加宏、异步和有界 unsafe/FFI

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P3-01 | ⏳ 计划中 | 实现内置宏以及 `macro_rules!` 词元树匹配、卫生性、展开限制和诊断。 | P1-01, P2 门槛 | `rsc test tests/macros/macro-rules/Cargo.toml` | 已声明的展开/卫生性用例通过；递归/词元限制会终止并给出带源码信息的诊断。 |
| P3-02 | ⏳ 计划中 | 定义并实现进程外过程宏协议和 SDK。 | P3-01, P0-04 | `rsc test tests/macros/proc/Cargo.toml` | 派生/属性/函数式示例正常工作；崩溃、超时、过量输出和取消均得到隔离与清理。 |
| P3-03 | ⏳ 计划中 | 将 `async`/`.await` 降低为显式状态机，并在不生成运行时代码的情况下桥接 `Future`、`Waker`、取消和 .NET `Task`。 | P1-06, P2-02 | `rsc test tests/async/core/Cargo.toml` | 完成、挂起、取消、错误、Drop 和并发用例在 CoreCLR/AOT 上与异步配置档匹配。 |
| P3-04 | ⏳ 计划中 | 实现应用程序库所需的精确版本 `tokio` 兼容性配置档。 | P3-03, P2-03 | `rsc test compat/tokio/Cargo.toml --features declared-profile` | 配置档清单中列出的运行时、任务、计时器、同步、IO 和网络成员通过；报告省略的 feature。 |
| P3-05 | ⏳ 计划中 | 实现有界的 `unsafe`、布局、裸指针、联合体、固定和 C FFI 配置档。 | P1-08, P2-06 | `rsc test tests/unsafe-ffi/Cargo.toml` | 受支持的 `repr(C)` 布局和 C 调用与原生测试夹具匹配；明确拒绝被排除的内部函数/汇编/Rust ABI。 |
| P3-06 | ⏳ 计划中 | 实现 AOT 安全的 TLS 原语和证书/平台抽象。 | P3-03, P2-03 | `rsc test tests/tls/Cargo.toml` | 本地受信任/不受信任、主机名、协议、取消和释放用例通过，且不使用基于反射的序列化或动态代码。 |

当异步 IO、已声明的 `tokio` 配置档、宏隔离、TLS 和有界 unsafe/C ABI 配置档，
在两个受支持的 x64 平台上的 CoreCLR 和 Native AOT 下都通过时，P3 才能退出。

## P4：交付 HTTP 和 WebSocket 兼容性配置档

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P4-01 | ⏳ 计划中 | 实现公共配置档所需的内部精确版本 `http`/`hyper`/`tower` 表面。 | P3 门槛 | `rsc test compat/http-stack/Cargo.toml` | 请求/响应、消息体、中间件、背压、取消、HTTP/1.1 和已声明的 HTTP/2 用例通过。 |
| P4-02 | ⏳ 计划中 | 实现选定的 `reqwest` 客户端 API/feature 配置档。 | P4-01, P3-06 | `rsc test compat/reqwest/Cargo.toml --features declared-profile` | 清单中列出的 HTTP、TLS、重定向、流式传输、超时、代理和序列化适配器通过。 |
| P4-03 | ⏳ 计划中 | 实现选定的 `axum` 服务器 API/feature 配置档。 | P4-01, P3-04 | `rsc test compat/axum/Cargo.toml --features declared-profile` | 路由、提取器、响应、中间件、状态、错误、优雅关闭和并发示例通过。 |
| P4-04 | ⏳ 计划中 | 实现客户端/服务器 WebSocket 配置档。 | P4-01, P3-06 | `rsc test tests/websocket/Cargo.toml` | 升级、文本/二进制、分片、ping/pong、关闭、TLS、取消和大小限制测试通过。 |
| P4-05 | ⏳ 计划中 | 发布具有代表性的 AOT Web 应用和兼容性报告。 | P4-02 至 P4-04 | `rsc publish samples/web-api/Cargo.toml --runtime linux-x64 --locked` | HTTP API 和 WebSocket 示例通过负载/取消冒烟测试；报告列出测试的精确 API/feature 和已知缺口。 |

当已发布的 Web 兼容性配置档（而非整个上游 crate 生态系统）通过其 API 清单以及具有
代表性的 Windows/Linux x64 Native AOT 应用时，P4 才能退出。

## P5：交付数据库和 ORM 兼容性配置档

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P5-01 | ⏳ 计划中 | 在受支持的 .NET 数据库提供程序之上定义 AOT 安全的提供程序边界，不生成运行时模型。 | P3 门槛, P2-06 | `rsc test tests/database/provider-contract/Cargo.toml` | 连接、命令、类型化值、取消、释放、错误映射和事务契约测试通过。 |
| P5-02 | ⏳ 计划中 | 为 SQLite、PostgreSQL 和 MySQL 实现选定的 `sqlx` 配置档。 | P5-01, P3-04 | `rsc test compat/sqlx/Cargo.toml --features sqlite,postgres,mysql` | 参数化 CRUD、连接池、事务、流式传输、迁移、类型映射、超时和回滚用例针对固定的服务器版本通过。 |
| P5-03 | ⏳ 计划中 | 使用有界数据库模式元数据/快照添加 `sqlx` 编译时查询验证。 | P5-02, P3-02 | `rsc check tests/database/sqlx-checked/Cargo.toml --locked` | 有效查询可从固定快照离线编译；无效 SQL/类型/列用例以稳定的源码诊断失败。 |
| P5-04 | ⏳ 计划中 | 为 SQL Server 实现选定的 `tiberius` 配置档。 | P5-01, P3-04 | `rsc test compat/tiberius/Cargo.toml --features declared-profile` | 参数化 CRUD、连接池集成、事务、流式传输、取消和 SQL Server 类型用例通过。 |
| P5-05 | ⏳ 计划中 | 在受支持的驱动程序之上实现选定的 `sea-orm` 配置档。 | P5-02, P5-04, P3-02 | `rsc test compat/sea-orm/Cargo.toml --features declared-profile` | 生成的/静态实体、关系、CRUD、事务、迁移和 AOT 可达性在已声明的提供程序中通过。 |
| P5-06 | ⏳ 计划中 | 为每个受支持的提供程序发布数据库示例和兼容性矩阵。 | P5-02 至 P5-05 | `rsc publish samples/database-api/Cargo.toml --runtime win-x64 --locked` | SQLite/PostgreSQL/MySQL/SQL Server 示例在已声明的 CI 服务下运行；报告公开驱动程序/服务器/API/feature 版本和已知缺口。 |
| P5-07 | ⏳ 计划中 | 在 `sea-orm` 稳定后评估 `diesel` 并为其制定配置档。 | P5 门槛 | `rsc check probes/diesel/Cargo.toml` | 书面的可行性/配置档决策记录所需的类型系统、宏、后端和 AOT 工作；不会仅凭探测就作出支持声明。 |

当全部四种数据库引擎针对各自已发布配置档通过参数化查询、连接池、事务、取消、迁移和
Native AOT 门槛，且 ORM 报告声明精确的受支持 API/feature 时，P5 才能退出。

## P6：加固平台、原生库和分发

| ID | 状态 | 工作项 | 硬依赖 | 验收命令 | 可观察结果 |
| --- | --- | --- | --- | --- | --- |
| P6-01 | ⏳ 计划中 | 分别冻结 Rust# 内部元数据、公共 .NET 和 C ABI 的版本控制策略。 | P2 门槛, P3-05 | `dotnet test RustSharp.slnx -c Release --filter ApiCompatibility` | 基线为每项契约独立检测不兼容更改，并允许有文档记录的仅扩展更改。 |
| P6-02 | ⏳ 计划中 | 发出具有显式 C ABI 导出、所有权、错误、回调和线程契约的 Native AOT 库。 | P6-01 | `rsc publish samples/c-abi/Cargo.toml --kind native-library --runtime win-x64` | C 和 C# 原生消费者调用导出，安全交换缓冲区/错误，并通过泄漏/生命周期测试。 |
| P6-03 | ⏳ 计划中 | 添加 Windows/Linux ARM64 原生构建和测试运行器。 | P0-17, P6-01 | `rsc publish samples/hello/Cargo.toml --runtime linux-arm64 --locked` | ARM64 制品以原生方式构建和运行；CoreCLR/AOT 一致性报告与已声明的平台配置档匹配。 |
| P6-04 | ⏳ 计划中 | 添加 macOS x64 和 ARM64 原生构建和测试运行器。 | P6-01 | `rsc publish samples/hello/Cargo.toml --runtime osx-arm64 --locked` | 不依赖签名/公证的测试制品以原生方式运行，并发布平台一致性证据。 |
| P6-05 | ⏳ 计划中 | 强制执行裁剪/AOT 分析、依赖项允许列表、确定性打包、签名和来源证明。 | P2-05, P6-01 | `dotnet build RustSharp.slnx -c Release /warnaserror; rsc verify-package artifacts/packages/*` | 不经抑制即可实现零分析器警告；包验证标识、哈希、来源、目标配置档和可复现性。 |
| P6-06 | ⏳ 计划中 | 为每个工作负载建立性能、内存、启动、代码大小和编译器资源预算。 | P2 门槛 | `dotnet run --project benchmarks/RustSharp.Benchmarks -- --profile release-gates` | 结果与签入的预算和历史基线比较；回归会明确失败，但不声称与 rustc 性能对等。 |
| P6-07 | ⏳ 计划中 | 验证升级、回滚、缓存失效、诊断稳定性和发布操作。 | P6-01, P6-05 | `dotnet test RustSharp.slnx -c Release --filter ReleaseEngineering` | 受支持的升级路径正常工作，不兼容的配置档更改明确失败，回滚已有文档记录，陈旧制品无法复用。 |
| P6-08 | ⏳ 计划中 | 针对所有已声明的语言、库、生态系统、输出和平台配置档，运行端到端 1.0 候选门槛。 | 适用的 P4/P5 门槛, P6-02 至 P6-07 | `rsc conformance --release-profile 1.0 --fail-on-difference` | 签名报告标识每个基准集合/版本/RID，没有无法解释的配置档内失败，并列出所有排除项。 |

只有发布证据可以在原生运行器上复现，P6 和适用的应用配置档门槛才能退出。一台宿主为
另一个 RID 生成文件，不足以证明目标受支持。

## 兼容性度量

兼容性按版本化配置档度量，绝不使用“兼容 Rust”或“兼容 crate”之类没有限定条件的声明。

| 维度 | 基准集合与指标 | 门槛 |
| --- | --- | --- |
| 语法 | 配置档中包含的具名 Rust 1.98/Edition 2024 语料用例。 | 每个已声明用例都得到预期解析/诊断结果；列出被排除的语法。 |
| 安全语义 | 具名的编译通过、编译失败和运行通过用例，与固定版本 rustc 1.98 比较。 | 配置档内不存在无法解释的结果差异。 |
| 所有权/借用/Drop | 专门针对别名、生命周期、移动、再借用、逃逸和析构顺序的语料。 | 所有配置档用例都与预言工具一致，或符合已批准且记录在案的 Rust# 差异。 |
| 诊断 | Rust# 代码、严重性、主要范围和稳定的消息参数。 | 黄金测试通过；除非配置档另有说明，否则不要求与 rustc 的确切措辞相同。 |
| 公共库 API | 生成的配置档清单中的符号和 feature 组合。 | 每个列出的成员都存在且其行为契约测试通过；发布清单覆盖率百分比和排除项。 |
| 生态系统 API | 受上游精确名称/版本启发的配置档、选定的 feature、代表性应用和公共 API 清单。 | 所有列出的用例通过；这并不意味着上游 crate 源码或所有 feature 都能工作。 |
| 运行时对等性 | 退出码、stdout/stderr、异常/panic 行为、Drop 跟踪和外部效果。 | CoreCLR 和 Native AOT 对每个受支持的 RID/配置档保持一致。 |
| IL/PDB | IL 验证、确定性元数据、序列点和调试器场景。 | 验证无错误；PDB 源码导航通过已声明场景。 |
| Native AOT | 分析器/发布警告、动态代码可达性、启动冒烟测试和制品执行。 | 不抑制任何 AOT/裁剪警告；原生制品在其目标上运行。 |
| 性能 | 具有延迟、吞吐量、内存、启动、代码大小和编译资源预算的版本化工作负载。 | 预算回归会失败；不承诺与 rustc 的全面性能对等。 |

每份一致性报告必须记录编译器提交、Rust# 配置档、rustc 预言工具版本、.NET SDK/运行时
版本、RID、包锁定哈希、测试基准集合、超时和排除项。迁移到后续 Rust 稳定版时，会创建
新的配置档和迁移计划；不会静默更改 Rust 1.98 配置档。

## 明确不作出的承诺

- 在版本化配置档明确包含某项 feature 之前，Rust# 不承诺支持完整的 Rust 语言、标准库或 crates.io 生态系统。
- Rust# 不承诺 `tokio`、`reqwest`、`axum`、`sqlx`、`tiberius`、`sea-orm` 或任何传递 crate 的原始源代码可以不经修改地编译。它实现并测试精确命名的 API/feature 配置档。
- Rust ABI、`.rlib`、rustc 私有元数据、`repr(Rust)` 布局兼容性，以及链接任意由 rustc 生成的对象，都不是受支持的契约。
- 生产编译器不会将程序逻辑转换为 C#，不会使用 `Reflection.Emit`，也不要求生成运行时代码。按照 ADR 0003 的记录，生成的 C# 宿主可以暂时只提供 .NET SDK Native AOT 项目边界。
- 托管存储不会让无效的 Rust 别名或生命周期行为变为有效代码。GC 可以回收存储，而 Rust# 仍会发出活动配置档所要求的确定性 `Drop` 行为。
- AOT 支持不包括需要不受支持的反射、运行时代码生成或无法验证的原生依赖项的 NuGet 包，除非提供了明确的适配器/配置档。
- 不能从编译成功推断跨平台支持。每个 RID 都需要原生执行门槛。
- 内联汇编、不受限制的 `transmute`、全部编译器内部函数、完整的 unsafe Rust 语义、Visual Studio/Rider 集成和 `diesel` 不属于早期核心 MVP。

## 风险与决策触发条件

| 风险 | 早期证据 | 缓解措施 | 决策触发条件 |
| --- | --- | --- | --- |
| 借用/NLL 行为偏离 Rust | 差异编译失败语料发现错误接受或错误拒绝。 | 保持类型化 MIR 明确；先扩充用例再扩展语法；按配置档隔离已批准的差异。 | 如果 P0 所有权探索性实现无法表达所需规则，则停止扩展语法。 |
| CLR 引用无法安全表达 Rust 生命周期/布局用例 | 固定、内部引用、逃逸或 Drop 测试在 CoreCLR 与 AOT 之间有差异。 | 在经过检查的抽象后使用句柄/偏移量或非托管存储；禁止不支持的形式。 | 在引入 unsafe/运行时例外之前编写 ADR。 |
| 泛型单态化导致代码大小或 AOT 可达性增长 | P0/P1 泛型示例超过记录的大小/时间预算。 | 规范化替换；在语义允许的情况下共享安全主体；明确表示可达性。 | 在接受不可预测的运行时泛型回退之前缩小配置档。 |
| Trait 求解变得无界或不兼容 | 歧义/一致性用例超时或与 rustc 不一致。 | 对求解器子集进行版本化，添加深度/工作量预算，缓存规范目标，并诊断不支持的目标。 | 不要将不受支持的关联类型/GAT 行为标为兼容。 |
| 生成的 IL 在 CoreCLR 上有效，但被 AOT 拒绝或更改 | ILVerify 或 AOT 门槛失败。 | 在发出前验证 CLR LIR，并针对每个降低系列测试两个引擎。 | 将对等性失败视为后端阻塞项，而不是采用库变通方案。 |
| 兼容性库范围在没有基准集合的情况下增长 | 在没有清单或代表性程序的情况下声明新 API/feature。 | 固定精确配置档，发布覆盖范围/缺口，并安排依赖顺序（Web/DB 层之前先实现 `tokio`）。 | 拒绝没有限定条件的 crate 兼容性声明。 |
| NuGet 依赖项破坏裁剪/AOT | 出现分析器警告、动态代码注释或运行时失败。 | 维护允许列表和适配器/源生成元数据；验证包闭包。 | 在警告和执行门槛通过前排除该依赖项/配置档。 |
| 跨平台行为发生漂移 | 原生 RID 报告在 IO、套接字、TLS、路径或数据库类型方面不同。 | 使用原生运行器、平台特定测试夹具和明确的 `cfg` 配置档。 | 不要仅凭交叉编译就宣传某个 RID。 |
| 工具进程泄漏或挂起 | CI 超时后留下子进程/临时文件或丢失日志。 | 要求每项子进程功能都遵守有界运行器契约并提供清理测试。 | 缺少所有权元数据或清理证据时阻止合并。 |

## 团队构成与运作模式

建议团队由三至五名具有编译器经验的工程师组成。五人可以减少串行瓶颈；三人可以通过
合并角色推进，但应减少同时进行的配置档数量。

| 职责 | 主要重点 |
| --- | --- |
| 语言前端 | 词法分析器/解析器、宏、HIR、名称解析、诊断、rustc 差异语料。 |
| 语义 | 类型系统、trait 求解器、类型化 MIR、所有权/借用/NLL、Drop 和 panic 行为。 |
| 后端/运行时 | CLR LIR、元数据/IL/PDB、托管混合运行时、Native AOT、C/.NET 互操作。 |
| 库/生态系统 | `core`/`alloc`/`std`、异步/网络/TLS、HTTP、数据库、精确兼容性配置档。 |
| 工具/质量 | `rsc`、Cargo/NuGet 解析、一致性测试基础设施、LSP/VS Code、CI、发布证据。 |

如果团队只有三名工程师，则将前端与工具职责合并，并将库/生态系统与运行时职责合并，
同时为语义保留明确的负责人。兼容性库工作不应超前于其所依赖的语言/运行时门槛。

## 如何推进路线图

1. 选择所有硬依赖状态均为 `✅ 已完成` 的第一个 `⏳ 计划中` 工作项。
2. 在扩展其表面之前，添加测试和机器可读证据。
3. 在干净工作树上以有界执行方式运行准确的验收命令。
4. 记录工具/配置档版本，并且只保留有意生成的制品。
5. 只有可观察结果和所属阶段门槛都满足后，才将状态标记为 `✅ 已完成`；否则保持 `🚧 进行中` 并记录缺口。
6. 当范围发生变化时，先更新兼容性配置档和 ADR，再更新实现与本路线图。

接下来处于 🚧 进行中的门槛是 P1-01（无损词法分析）、P1-02（安全核心语法）、
P1-03（HIR 与名称解析）、P1-04（类型）、P1-09（IL 发射）和 P1-10（差分回归）。
P0-10、P0-16 和 P0-17 现在基于已记录的双平台证据均为
✅ 已完成；后续语言配置档声明仍受完整 HIR/MIR 和差异测试套件的门槛约束。
