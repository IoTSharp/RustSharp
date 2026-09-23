# P0 颗粒化记录：已完成纵向架构

[English](P0.md) | 简体中文 | [主路线图](../../ROADMAP_zh.md)

所有行均在**原 P0 纵向切片与可行性范围内**为 ✅ 已完成。这是十七项已完成父任务的追溯分解，不是新增实现声明、重新测试或重开 P0。每个父任务是其全部子项的聚合；`P0-GATE` 是三项历史门禁的聚合。叶子具有自身无环依赖，无需等待所属阶段门禁。

证据来源是现有主路线图中提交 `286f139` 的记录，采集于 2026-09-02 至 2026-09-04，使用 .NET SDK 10.0.400 与运行时 10.0.11。**W** 表示 [Windows 运行 33857817622](https://github.com/IoTSharp/RustSharp/actions/runs/33857817622)，**L** 表示 [Linux 运行 33857817620](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620)，**H** 表示双方历史 CI 归档及主路线图已记录证据。这些引用予以保留，本次文档修改没有重新下载或运行。

每个平台归档记录 14 文件/七 JSON 报告、73/73 可执行测试、4/4 纵向差分、6/6 语法、6/6 名称解析、4/4 IO 冒烟、独立 ILVerify 与原生执行。另行记录的本地 74/74 是较晚本地计数，不是 CI 分母。本地 WSL SDK 不匹配与缺少 sqlite3 在本地分别跳过/阻塞；真正的原生 CI 报告提供 Linux/SQLite 成功证据。没有把本地跳过改称成功。

下列具名源码/测试文件是历史交付物当前可检查位置，其当前内容可能含后续工作。八项 CLR LIR 与八项所有权用例、泛型/trait 模型及托管混合探针仅证明有界可行性，不关闭 P1 源码级语义、生成析构器失败处理、后续 API 配置档或新平台。并发发布冲突加固、文件系统别名处理与外部锁定制品恢复仍属于主路线图边界下的后续门禁。

已记录工具命令为 `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore`；它是可执行工具，不是 `dotnet test` 发现。现有证据标识工具/配置档/平台、固定计数与有界进程/清理信息。被忽略的 `artifacts/` 路径指已记录可复现输出，不是新提交报告。本次文档工作不修改 `global.json`。

<a id="p0-01"></a>

## P0-01: 解决方案与项目边界

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-01.01 | ✅ 已完成 | `RustSharp.slnx`, `src/`, `tests/RustSharp.Tests/` — 建立编译器/CLI/语法/IL/测试项目边界。 | — | 解决方案列出约定项目；依赖遵守 C# 编译器/直接 IL 架构。 | `dotnet sln RustSharp.slnx list`；历史源码树。 |
| P0-01.02 | ✅ 已完成 | `global.json`, `Directory.Build.props`, `Directory.Packages.props` — 固定 .NET 10 构建与中央包配置。 | P0-01.01 | 已记录 SDK 10.0.400 Release 构建零警告/错误；SDK 固定值保持不变。 | H：干净 Release 构建日志。 |

<a id="p0-02"></a>

## P0-02: 已接受架构决策

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-02.01 | ✅ 已完成 | `docs/adr/0001-language-baseline.md`, `docs/adr/0002-csharp-compiler-and-il-output.md`, `docs/adr/0003-first-vertical-slice.md` — 接受语言、实现/输出与纵向切片决策。 | — | ADR 标记 Accepted 并固定 Rust 1.98/Edition 2024、C#/.NET 10、直接 IL 与首个可执行范围。 | 三份已接受 ADR；原 P0-02 证据。 |
| P0-02.02 | ✅ 已完成 | `docs/adr/0004-ownership-mir-spike.md`, `docs/adr/0005-generic-trait-spike.md`, `docs/adr/0006-managed-hybrid-runtime-spike.md` — 接受有界所有权、泛型与运行时可行性契约。 | P0-02.01 | 三份 ADR 均为 Accepted，区分可行性模型与完整 Rust 语义。 | 三份已接受探索 ADR；原 P0-02 证据。 |

<a id="p0-03"></a>

## P0-03: 首个兼容性配置档

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-03.01 | ✅ 已完成 | `docs/compatibility.md` — 命名并冻结纵向切片语言边界。 | P0-02 | 配置档为 Rust 1.98.0/Edition 2024 上的 vertical-slice-v1，仅含 main 与字面量 println 语句。 | 兼容性契约与 ADR 0003。 |
| P0-03.02 | ✅ 已完成 | `docs/compatibility.md`, `tools/RustSharp.Conformance/fixtures/` — 明确排除项与初始正负示例。 | P0-03.01 | 不支持源码得到诊断；不承诺 Rust ABI/rlib/repr(Rust) 与完整语言/库兼容。 | 初始四夹具目录；P0-11 提供差分结果。 |

<a id="p0-04"></a>

## P0-04: 有界进程与清理

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-04.01 | ✅ 已完成 | `src/RustSharp.Compiler/BoundedProcessRunner.cs` — 限制子进程执行并收集进程/输出证据。 | P0-01 | 区分正常退出、超时、取消与输出上限；记录 PID/启动/父进程/命令/耗时及两个输出流。 | `tests/RustSharp.Tests/BoundedProcessTests.cs`；H。 |
| P0-04.02 | ✅ 已完成 | `src/RustSharp.Compiler/BoundedProcessRunner.cs`, `eng/Invoke-BoundedProcess.ps1` — 并发读取输出并回收自有子进程树。 | P0-04.01 | 已记录退出/超时/取消用例不因输出死锁并报告自有子进程清理；根进程冒烟助手仍是较窄工具。 | H 中有界进程超时/取消/子进程用例。 |

<a id="p0-05"></a>

## P0-05: 窄解析器与诊断

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-05.01 | ✅ 已完成 | `src/RustSharp.Syntax/`, `tests/RustSharp.Tests/SyntaxTests.cs` — 解析 main、字面量 println、注释与转义。 | P0-03 | 有效首切片输入及嵌套注释/字符串用例产生预期语法。 | 语法 main/println 与字符串/注释解码测试；H。 |
| P0-05.02 | ✅ 已完成 | `src/RustSharp.Syntax/`, `tests/RustSharp.Tests/SyntaxTests.cs` — 稳定损坏源码诊断与源码范围。 | P0-05.01 | 窄配置档内缺失分隔符/分号、无效转义/行尾与尾随词元以稳定代码/范围失败。 | 原语法/转义/注释回归；H。 |

<a id="p0-06"></a>

## P0-06: 直接 PE 与 Portable PDB

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-06.01 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/IlAssemblyEmitter.cs`, `src/RustSharp.Compiler/CompilerDriver.cs` — 发射并写入可执行 PE 与运行时配置。 | P0-05 | hello.dll 含托管入口及 runtimeconfig；输出直接来自 IL 发射，无生成 C# 程序逻辑。 | `tests/RustSharp.Tests/EmissionTests.cs`；H。 |
| P0-06.02 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/IlAssemblyEmitter.cs`, `tests/RustSharp.Tests/EmissionTests.cs` — 发射关联源码的 Portable PDB 与确定性字节。 | P0-06.01 | 非空 PDB 包含文档、序列点与原始字节校验和；相同输入重复产生相同 PE/PDB。 | PDB 文档/范围/哈希与确定性发射测试；H。 |

<a id="p0-07"></a>

## P0-07: 元数据、验证器与确定性

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-07.01 | ✅ 已完成 | `tests/RustSharp.Tests/EmissionTests.cs` — 检查发射元数据/IL/PDB 与磁盘确定性。 | P0-06 | 读取器与已记录 ilspy 检查解析入口、IL 栈/令牌和序列点；重复 PE/PDB/runtimeconfig 字节一致。 | PEReader/MetadataReader/ilspy 与磁盘发射证据；H。 |
| P0-07.02 | ✅ 已完成 | `.config/dotnet-tools.json`, `eng/Invoke-ILVerify.ps1` — 运行固定版本独立 IL 验证。 | P0-07.01 | dotnet-ilverify 10.0.11 使用明确 System.Private.CoreLib/运行时引用并退出零；命令/哈希/进程/清理证据归档。 | 已记录 `artifacts/p0/hello.ilverify.json`；H。 |

<a id="p0-08"></a>

## P0-08: CoreCLR 可执行切片

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-08.01 | ✅ 已完成 | `samples/hello.rs`, `src/RustSharp.Compiler/CompilationOutput.cs` — 产出可运行首切片 DLL/配置组合。 | P0-06 | 生成 hello 输出使用其发射配置在 .NET 10 加载。 | 已记录 check/compile 与 CoreCLR 调用；H。 |
| P0-08.02 | ✅ 已完成 | `samples/hello.rs`, `eng/` — 检查准确运行输出与退出状态。 | P0-08.01 | CoreCLR 退出零，准确打印 Hello from Rust# 加平台换行。 | 已记录 `dotnet artifacts/p0/hello.dll`；H。 |

<a id="p0-09"></a>

## P0-09: Windows x64 Native AOT

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-09.01 | ✅ 已完成 | `src/RustSharp.Compiler/NativeAotPublisher.cs`, `tests/RustSharp.Tests/NativeAotTests.cs` — 通过有界独占临时 SDK 宿主发布。 | P0-04, P0-08 | 原生发布将警告视为错误，自有宿主清理前不能报告成功；不引入生成 C# 程序逻辑。 | 发布器清理结果回归与 Windows 发布日志；H。 |
| P0-09.02 | ✅ 已完成 | `eng/Invoke-WindowsNativeAotProbe.ps1` — 验证并执行原生 AMD64 PE。 | P0-09.01 | 原生 PE32+ AMD64 执行退出零并准确输出 Hello from Rust#；记录进程与临时宿主清理。 | W：含 PID/父进程与格式检查的原生发布/运行报告。 |

<a id="p0-10"></a>

## P0-10: Linux x64 Native AOT

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-10.01 | ✅ 已完成 | `eng/Invoke-LinuxNativeAotProbe.sh`, `.github/workflows/linux-native-aot.yml` — 在原生 Ubuntu 运行器发布 x86-64 ELF。 | P0-09 | 已记录 Ubuntu 24.04 x64 运行使用固定 SDK 并产出原生 Linux 制品。 | L：原生构建与 ELF 格式证据。 |
| P0-10.02 | ✅ 已完成 | `eng/Invoke-LinuxNativeAotProbe.sh` — 执行 Linux 输出并对账有界清理。 | P0-10.01 | ELF 退出零，文本与 CoreCLR 完全一致且清理完整；本地 WSL SDK 跳过不计为 Linux 成功。 | L：有界原生执行与清理报告。 |

<a id="p0-11"></a>

## P0-11: rustc 差分工具

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-11.01 | ✅ 已完成 | `tools/RustSharp.Conformance/Program.cs`, `tools/RustSharp.Conformance/fixtures/` — 定义并运行四用例纵向差分目录。 | P0-03, P0-04 | 两项运行通过与两项编译失败用例对比 rustc +1.98.0，结果/输出一致。 | 已记录 `artifacts/conformance/vertical-slice-v1.json`；H：4/4。 |
| P0-11.02 | ✅ 已完成 | `tools/RustSharp.Conformance/Program.cs` — 发射有界机器可读差分证据。 | P0-11.01 | 报告标识配置档、分母、rustc/工具链版本、诊断、上限、进程元数据与清理；不隐含省略用例。 | H：版本化一致性 JSON 与零失败/跳过用例。 |

<a id="p0-12"></a>

## P0-12: 类型化 CLR LIR 可行性

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-12.01 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/ClrLir.cs`, `tests/RustSharp.Tests/ClrLirTests.cs` — 在 IL 发射前验证局部值、调用、分支与返回。 | P0-07 | 栈/调用类型不匹配、无效目标、合流不一致与不可达块用例在 PE 发射前拒绝。 | 八用例 CLR LIR 系列；H。 |
| P0-12.02 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/ClrLirEmitter.cs`, `tests/RustSharp.Tests/ClrLirTests.cs` — 发射确定性分支字节码并运行分支 PE。 | P0-12.01 | 有效控制流发射可重复字节，其真实生成 PE 返回预期结果。 | 确定性字节码与分支 PE 执行用例；H。 |

<a id="p0-13"></a>

## P0-13: 所有权 MIR 可行性

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-13.01 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/OwnershipMir.cs`, `tests/RustSharp.Tests/OwnershipTests.cs` — 证明 Copy/Move 与共享/可变借用状态迁移。 | P0-12 | 拥有值移动消耗源，Copy 仍可使用，共享借用共存，可变重叠及借用中移动拒绝。 | 已记录八用例所有权系列；H。 |
| P0-13.02 | ✅ 已完成 | `src/RustSharp.CodeGen.IL/OwnershipMir.cs`, `tests/RustSharp.Tests/OwnershipTests.cs` — 证明显式 NLL 结束、逃逸拒绝与逆序 Drop。 | P0-13.01 | 小型 MIR 在显式借用结束后允许 owner 复用，拒绝逃逸引用并记录确定性逆序 Drop。 | NLL/逃逸/析构顺序用例；H；ADR 0004。 |

<a id="p0-14"></a>

## P0-14: 有界泛型/trait 可行性

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-14.01 | ✅ 已完成 | `src/RustSharp.Semantics/GenericModel.cs`, `tests/RustSharp.Tests/VerticalProofTests.cs` — 在可行性模型中确定性闭合 Option<i32>。 | P0-12 | 重复单态化产生相等闭合类型与稳定文本形式。 | GenericOptionAsync；H；ADR 0005。 |
| P0-14.02 | ✅ 已完成 | `src/RustSharp.Semantics/`, `tests/RustSharp.Tests/VerticalProofTests.cs` — 求解有界精确/泛化 trait 子集。 | P0-14.01 | 精确求解成功，缺失/歧义用例诊断且深度/工作量上限终止；完整 trait 一致性不属于此探索。 | TraitResolutionAsync；H；ADR 0005。 |

<a id="p0-15"></a>

## P0-15: 托管混合与静态互操作

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-15.01 | ✅ 已完成 | `src/RustSharp.Runtime/ManagedHybrid.cs`, `tests/RustSharp.Tests/VerticalProofTests.cs` — 证明 owner/borrow 保护、DropScope 与显式固定。 | P0-13 | 共享/独占规则与析构后使用拒绝成立；逆序清理与固定释放通过运行时可行性探针。 | ManagedBorrow/OwnerUseAfterDrop/DropScope/PinnedArray 用例；H。 |
| P0-15.02 | ✅ 已完成 | `src/RustSharp.Runtime/ManagedHybrid.cs`, `docs/adr/0006-managed-hybrid-runtime-spike.md` — 证明显式静态泛型 .NET 调用边界。 | P0-15.01 | ManagedInterop.Call 使用 IManagedCall 且无反射/运行时代码生成；这是运行时映射证明而非完整编译器生命周期降低。 | VerticalProofTests 的 ManagedInteropAsync；H；ADR 0006。 |

<a id="p0-16"></a>

## P0-16: 文件/TCP/异步/SQLite 探针

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-16.01 | ✅ 已完成 | `tools/RustSharp.Smoke/Program.cs` — 执行有界文件、回环 TCP 与异步探针。 | P0-13, P0-15 | 文件往返、TCP 交换与异步完成/取消通过且不使用反射代码生成；临时资源回收。 | H：p0-io file-roundtrip/loopback-tcp/async-completion-cancellation。 |
| P0-16.02 | ✅ 已完成 | `tools/RustSharp.Smoke/Program.cs`, `.github/workflows/` — 执行参数化 SQLite 与严格四探针门禁。 | P0-16.01 | 两个 CI 平台执行 SQLite 事务且 4/4 通过、零失败/跳过；本地缺少 sqlite3 仍明确阻塞。 | H：4/4 冒烟；W 安装/验证 SQLite 3.53.4。 |

<a id="p0-17"></a>

## P0-17: 双平台 CI 与归档

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-17.01 | ✅ 已完成 | `.github/workflows/windows-p0.yml`, `.github/workflows/linux-native-aot.yml` — 在已记录 SHA 运行干净 Windows/Linux x64 CI。 | P0-07, P0-10, P0-11 | 提交 286f139 通过两个已记录工作流；各含测试、差分、冒烟、验证器与原生执行。 | W 与 L 工作流结论及提交来源。 |
| P0-17.02 | ✅ 已完成 | `.github/workflows/`, `eng/` — 归档并检查固定平台证据包。 | P0-17.01 | 每个归档含 14 文件/七 JSON 报告；双方均有 73/73 测试、4/4 纵向差分、4/4 IO 冒烟与验证器/AOT 证据。 | W/L 归档；12 个共享语法/名称解析源码哈希一致。 |

<a id="p0-gate"></a>

## P0-GATE: 历史阶段退出

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P0-GATE.01 | ✅ 已完成 | `ROADMAP.md`, `ROADMAP_zh.md` — 对账历史纵向切片验收集合。 | P0-01, P0-02, P0-03, P0-04, P0-05, P0-06, P0-07, P0-08, P0-09, P0-10, P0-11, P0-12, P0-13, P0-14, P0-15, P0-16, P0-17 | 全部原 P0 制品/决策与可执行可行性探针具有已记录证据；73 项 CI 集合与四项纵向预言集合在 286f139 通过。 | H：73/73 测试与 4/4 纵向差分；原 P0 完成记录。 |
| P0-GATE.02 | ✅ 已完成 | `eng/Invoke-WindowsNativeAotProbe.ps1`, `eng/Invoke-LinuxNativeAotProbe.sh`, `eng/Invoke-ILVerify.ps1` — 接受已记录双平台 IL/CoreCLR/Native AOT 证明。 | P0-GATE.01 | 真实 Windows AMD64 PE 与 Linux x86-64 ELF 执行纵向切片且输出准确；IL 验证与自有宿主/进程清理通过。 | W/L 原生/验证器/进程报告，不使用本地跳过探针。 |
| P0-GATE.03 | ✅ 已完成 | `ROADMAP.md`, `ROADMAP_zh.md`, `docs/compatibility.md` — 保留 P0 完成及其明确范围边界。 | P0-GATE.02 | 已记录干净构建与 14 文件归档仅关闭 P0；完整源码级借用/Drop、语言/库配置档与后续 RID 门禁仍独立负责。 | 历史 286f139 完成记录与兼容性非承诺。 |
