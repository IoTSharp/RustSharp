# RustSharp

[English](README.md) | 简体中文

RustSharp 是一个使用 C# 和 .NET 10 编写的实验性 Rust 1.98 / Edition 2024
语言实现。`rsc` 编译器读取 `.rs` 源文件、执行 RustSharp 语言分析，并生成
ECMA-335 程序集；这些程序集既可在 .NET 上运行，也可参与 .NET Native AOT
发布流程。

RustSharp 不使用手写 IL 作为实现语言。编译器和工具链由 C# 项目构成；IL
是编译器的输出。

## 当前里程碑

第一个纵向切片刻意只支持很小的源代码配置：

```rust
fn main() {
    println!("Hello from Rust#");
}
```

已记录的 Windows 和 Linux x64 证据表明，同一个生成的程序集既能在 CoreCLR 上运行，
也能作为 .NET 10 Native AOT 可执行文件运行。直接生成 PE、Portable PDB、
确定性输出、独立 IL 验证以及带类型的 CLR LIR 证据均在 `ROADMAP_zh.md` 中跟踪。
固定到 rustc 1.98 的差分测试工具已为全部四个夹具（两个 run-pass 和两个
compile-fail）记录本地证据，因此对于声明的 `vertical-slice-v1` 分母，P0-11
为 ✅ 已完成。提交 `286f139` 上的 P0 门禁现在为 ✅ 已完成：[Windows 运行
`33857817622`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817622)
和 [Linux 运行
`33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620)
分别归档了 73/73 可执行测试工具、4/4 纵向一致性、6/6 安全核心语法、6/6
安全核心名称解析、独立 IL 验证、原生 x64 AOT 执行，以及包含 SQLite 的 4/4 I/O
冒烟证据。因此，P0-10、P0-16 和 P0-17 均为 ✅ 已完成。
不受支持的 Rust 语法会产生带源位置的诊断，而不会被悄然赋予 C# 语义。

P1 前端工作处于 🚧 进行中，其中 P1-01 现为 ✅ 已完成。无损词法分析器的第 2 版
验收清单包含 24 个夹具，并强制要求 Rust 1.98.0 / Edition 2024 / Unicode 17.0.0
的 22 类词法映射。它覆盖源码前导、标识符、全部字面量族及后缀、生命周期、trivia、
标点、分隔符、词元树、保留形式和格式错误输入诊断。BOM/shebang 处理、注释/CRLF
边界、非十进制浮点拒绝，以及取消、超时和迭代式词元树构建均已验证。全部 24 个用例
的精确证据和源码重建检查通过；P1-01 可执行回归工具记录了 103/103 项测试通过。
类别分母及其与语义或 rustc 差分一致性的区别见[词法契约](docs/lexical-profile.md)。
`SafeCoreSyntax` 现已发布第 3 版解析器验收清单，包含 18 个必需类别，并为每个解析
成功夹具保存精确 AST 快照。它覆盖递归导入、受限可见性、文档属性、生命周期/类型/
const 泛型、默认值及 `where`、trait/impl 与关联项、函数类型、限定路径、结构体
表达式、丰富模式、`match`、循环、闭包和 `let`-`else`，并覆盖解析取消/超时及有界
恢复。语法基准集合与明确排除项见[语法契约](docs/syntax-profile.md)。新解析语法
若尚未实现语义，会在成功降低 HIR 前返回 `RSN1007`。
这仍包括匿名 `const` 项、表达式/模式中的绝对路径，以及尚不支持的结构化 AST 扩展
和非文档 Rust 属性。
诊断代码和源码范围会经 HIR 降低保留到编译器检查/编译结果，编译会在写入输出产物前
拒绝这些输入。
有界的 `SafeCoreNameResolution` 原型目前跨独立
的类型/值命名空间收集模块、项和局部符号，
并解析具有代表性的导入与限定路径。它的十项基线测试工具用例覆盖类型/值命名空间与限定
路径、可见性、重复/歧义/未解析名称、导入环、声明顺序与合法遮蔽、拒绝通过限定路径
访问函数局部变量/结构体字段/枚举泛型参数、Unicode 标识符规范化、Unicode 补充平面
标识符，以及导入嵌套限制。此前记录的本地可执行测试工具通过了 74/74 项测试。有界的
`SafeCoreHirLowering` 原型目前会把成功的语法与名称解析结果转换为确定性的、名称已
绑定的扁平 HIR arena。这些前端阶段现在已接入下方按需启用的基础类型可执行配置。
P1-02 对声明的语法配置为 ✅ 已完成。

P1-03 现支持分组和 glob 导入、分组中的 `self` 别名、匿名 `_` 导入，以及
`pub(crate)`、`pub(self)`、`pub(super)`、`pub(in ancestor)` 可见性。在基础类型配置下，
`CheckFile`/`CompileFile` 和 CLI 文件命令会从 `foo.rs` 或 `foo/mod.rs` 加载已声明的外部
模块，并支持嵌套布局。
诊断保留各原文件路径和范围；Portable PDB 保留原始源码校验和与函数位置。字符串形式的
编译器 API 仍以 `RSN1007` 拒绝外部模块。glob 解析在固定点预算内处理传递导入和循环，
区分类型/值命名空间的遮蔽，对规范目标去重，并在名称被使用时诊断歧义。
显式 `use` 导入现会独立解析类型和值绑定，包括别名及重导出链。同一规范目标对外合并为
一个 `Both` 绑定；不同目标则在 HIR `ImportGroup` 中保留为独立绑定。缺失的命名空间
分支既不会引入歧义，也不会遮蔽该命名空间中的 glob 绑定。有界固定点解析适用于所有导入。
受支持位置的源码文档注释
会保留到 HIR，执行时惰性忽略；显式 `#[doc]` 和其他尚未求值的属性会被拒绝。前导
前导 `::` 在需要外部预导入表查找时仍是明确的配置档边界，并返回 `RSN1007`。
P1-03 对声明的语义/HIR 和包配置档已 ✅ 已完成：`Cargo.toml` 已接入 `rsc check`、
`build`/`compile`、`run` 和 `publish`，支持有界的本地 `path` 依赖、确定性源码发现、
循环/限制诊断，并清晰拒绝注册表依赖。Cargo feature、锁文件语义、宏展开和更广泛的属性
求值仍属于后续配置档工作。见[模块契约](docs/module-profile.md)了解支持的路径、可见性规则
和资源预算。

带文档注释的三文件[模块示例](samples/modules/main.rs) 使用
`arithmetic::{self as math, *}`，输出 `42` 和 `true`：

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- check samples/modules/main.rs --profile safe-core-primitives-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- compile samples/modules/main.rs --profile safe-core-primitives-v1 --output artifacts/p1-03/modules.dll
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/modules/main.rs --profile safe-core-primitives-v1
```

此前 2026-09-08 的文件模块批次验证为 ✅ 已完成：Release 构建零警告/错误、171/171 项
可执行回归、49/49 项语法用例（34/34 份 AST 快照、18/18 类映射）、PowerShell 证据
检查器、6/6 项名称解析，以及对照 rustc 1.98.0 的 14/14 项基础类型差分。
CLI 检查/编译及生成模块示例的执行得到 `42` 和 `true`；独立 ILVerify 验证通过。
报告位于 `artifacts/p1-03/`，包括 `modules.ilverify.json`。另有 12 项模块规则的
rustc 元数据编译抽查作为补充证据，不代表完整模块差分清单。P1-03 对声明的配置档已 ✅ 已完成；更广泛的差分覆盖仍在计划中。

此前 2026-09-08 的 glob/文档注释续批对该次增量为 ✅ 已完成：Release 构建零警告/错误、
180/180 项可执行回归、49/49 项语法用例（34/34 份 AST 快照、18/18 类映射）、
PowerShell 证据检查器、6/6 项名称解析，以及对照 rustc 1.98.0 的 14/14 项基础类型
差分。模块示例再次通过 CLI 检查/编译，输出 `42` 和 `true`，并通过独立 ILVerify。
[验证摘要](artifacts/p1-03/glob-documentation-validation.json) 记录本批结果。
新增 12 项 glob 的 rustc 元数据编译抽查中，10 项接受、2 项拒绝；它是可执行测试的
补充，当时未扩大六用例名称解析清单。P1-03 对声明的配置档已 ✅ 已完成；更广泛的差分覆盖仍在计划中。

当前 `safe-core-name-resolution` 验收清单从此前六用例基线扩展为 25 个用例，明确规定
分组/self/匿名导入、glob 优先级和歧义、固定点导入链与循环、可见性、独立类型/值导入，
以及绝对导入和属性的 `RSN1007` 边界所对应的精确绑定与诊断范围。此前的 6/6 报告覆盖
各自原有基准集合。本次增量 ✅ 已完成：Release 构建零警告/错误，可执行回归通过 186/186，扩展名称解析清单通过 25/25，PowerShell 7 有界证据检查器验证通过。这不代表 P1-03 语义/HIR 门禁已完成。

## 可执行安全核心配置

P1 处于 🚧 进行中。使用 `--profile safe-core-primitives-v1` 可以编译内联或从文件加载的模块/导入、
非泛型函数、`i32`/`bool`、带初始化器的 `let` 绑定、`mut` 赋值、调用、代码块、
`if`/`else`、返回、带溢出检查的 `+`/`-`/`*`、比较以及短路布尔运算。C# 流水线现在将
名称绑定 HIR 和基础类型检查连接到经过验证的 CLR LIR、直接生成的 IL 程序集以及
Portable PDB 函数入口映射。它不会把 Rust 程序逻辑转译为 C#。

`println!` 接受不带大括号的普通字符串字面量，或带一个整数/布尔参数的 `"{}"`。
整数输出使用固定格式；布尔输出使用小写。[safe-core.rs](samples/safe-core.rs) 示例
输出 `Safe core on .NET`、`42` 和 `true`。运行或发布方式如下：

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/safe-core.rs --profile safe-core-primitives-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- publish samples/safe-core.rs --profile safe-core-primitives-v1 --runtime win-x64 --output artifacts/p1/windows-x64-aot
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98
```

差分套件声明了 14 个夹具：5 个运行通过用例和 9 个编译失败用例，rustc 显式启用溢出
检查。默认配置仍是 `vertical-slice-v1`。引用、借用/NLL 检查、ADT、泛型、完整的
类型化 MIR、确定性 Drop、类库和 Cargo 构建仍属于后续 P1/P2 工作。不支持的构造会在
输出前产生诊断。运行时整数溢出触发托管异常；不宣称兼容 Rust panic/展开语义。
精确配置和工作量限制见 [ADR 0007](docs/adr/0007-safe-core-primitives.md)，验收证据
见 [ROADMAP_zh.md](ROADMAP_zh.md)。

此前可执行批次 ✅ 已完成：91/91 项可执行回归、14/14 项基础类型差分、ILVerify 和 Windows
x64 Native AOT 示例。此配置的 Linux Native AOT 为 ⏳ 计划中；完整 P1 退出门槛仍为
🚧 进行中。

## 命令

```text
rsc check <source.rs>
rsc compile <source.rs> --output <program.dll>
rsc run <source.rs>
rsc publish <source.rs> --runtime win-x64 --output <directory>
```

在源码检出目录中，可通过 CLI 项目运行等价命令：

```text
dotnet run --project src/RustSharp.Cli -- check samples/hello.rs
dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll
dotnet run --project src/RustSharp.Cli -- publish samples/hello.rs --runtime win-x64 --output artifacts/p0/aot
```

当前测试套件是一个有界可执行测试工具（尚无测试 SDK 或筛选适配器）。运行方式如下：

```text
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore
```

独立 IL 门禁使用固定版本的 `dotnet-ilverify` 工具。首次恢复本地工具清单后，编译
示例并运行有界验证脚本：

```text
dotnet tool restore --tool-manifest .config/dotnet-tools.json
dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll
pwsh -NoProfile -File eng/Invoke-ILVerify.ps1 -AssemblyPath artifacts/p0/hello.dll -Restore -EvidencePath artifacts/p0/hello.ilverify.json
```

该脚本提供 .NET 10 运行时引用程序集，限制进程执行时间和捕获输出，清理归属明确的
进程树，并写入机器可读的证据文件。`.config/dotnet-tools.json` 将
`dotnet-ilverify` 固定为 10.0.11 版。

完成 Release 解决方案构建后，rustc 差分测试工具会记录带版本的报告；当所请求的
`rustc 1.98.x` 参照实现不可用时，
以退出码 2 结束：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile vertical-slice-v1 --oracle rustc-1.98
```

该工具在版本探测和夹具编译时都调用固定的 `rustc +1.98.0` 工具链，因此当前默认
活动工具链不会悄然改变参照实现。

清单驱动的 safe-core 词法验收配置将有界报告写入
`artifacts/conformance/safe-core-lexing.json`：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-lexing
```

第 2 版要求全部 24 个用例匹配精确的词元、trivia、词元树、诊断、范围和源码重建结果，
并提供完整的 22 类映射。Windows 与 Linux CI 校验当前清单哈希、基线、类别映射、
用例 ID 和全部分母。基于已记录的本地验收证据，P1-01 为 ✅ 已完成；此处不宣称新的
远程 CI 运行已经通过。报告仍属于 RustSharp 词法分析器验收证据，与 rustc 差分和
运行时一致性分开衡量。完整 P1 里程碑仍处于 🚧 进行中。

独立的 safe-core 语法配置发布了 49 个解析器验收用例，并写入
`artifacts/conformance/safe-core-syntax.json`：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax
pwsh -NoProfile -File eng/Test-SyntaxEvidence.ps1
```

第 3 版报告只衡量 RustSharp 解析器验收结果，强制要求全部 18 类映射；34 个解析
成功用例必须匹配精确 AST 快照，15 个拒绝用例必须匹配诊断代码和源码文本。
Windows/Linux CI 校验当前清单、夹具和快照哈希、用例 ID、结果及诊断范围。
语法验收不代表 rustc 差分或运行时一致性。

此前 P1-02 证据：2026-09-08 在 Windows x64、.NET SDK 10.0.400/runtime 10.0.11 上为
✅ 已完成：Release 构建零警告/错误、141/141 项可执行回归、49/49 项语法用例、
34/34 份 AST 快照、18/18 类映射、24/24 项词法用例、6/6 项名称解析及对照 rustc
1.98.0 的 14/14 项基础类型差分。证据检查器接受当前源码，并拒绝过期清单/快照哈希。
这些是本地结果，不宣称新的远程 CI 运行已通过。完整 P1 里程碑仍为 🚧 进行中。

此前 2026-09-08 的 `RSN1007` 边界验证为 ✅ 已完成：Release 构建零警告/错误、
145/145 项可执行回归、49/49 项语法用例（34/34 份 AST 快照、18/18 类映射）、
PowerShell 7 证据检查器，以及 6/6 项名称解析。该边界验证未重跑 rustc 差分和词法专项套件。

包含 25 个用例的名称解析验收配置会写入
`artifacts/conformance/safe-core-name-resolution.json`：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution
```

该报告只覆盖声明的进程内解析器/名称解析分母，并不构成 rustc 差分证据或运行时
一致性证据。同一个可执行测试工具也会测试 HIR 降低，它同样已接入按需启用的
`safe-core-primitives-v1` 编译器路径。

Linux Native AOT 探测器用于原生 Linux x64 运行器，并确保输出目录只归一次调用
独占：

```text
bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300
```

当主机不是原生 Linux x64 环境时，探测器以退出码 77 结束，并生成结构化的
`skipped` 证据；WSL 结果不视为原生 CI 证明。

`build` 接受 `.rs` 源文件或 `Cargo.toml`；`compile` 仍是兼容别名。安全核心配置档中的
Cargo 加载范围限定为包元数据和本地 `path` 依赖。

Native AOT 原型要求其输出目录由一次发布调用独占。并发发布、文件系统别名冲突处理，
以及输出文件被外部进程锁定时的恢复处理，仍属于后续加固工作。

P0 语义/运行时和 I/O 探测器可以独立运行：

```text
dotnet run --project tools/RustSharp.Smoke -c Release -- --profile p0-io
```

冒烟报告涵盖文件往返读写、环回 TCP、异步完成与取消，以及在有界 `sqlite3`
可执行文件可用时执行的参数化 SQLite 事务。`src/RustSharp.Semantics` 和
`src/RustSharp.Runtime` 是有界泛型/trait 解析和托管混合所有权/互操作的可行性
边界；它们的可执行用例作为主测试工具的一部分运行。

兼容性契约见 `docs/compatibility.md`，约束实现的架构决策见 `docs/adr`。
