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

早期 P1 前端工作也处于 🚧 进行中。无损词法分析器现在针对已声明的标识符、字面量、
trivia、分隔符、词元树和格式错误输入诊断提供清单驱动的有界验收配置。该配置核对精确
的词元/trivia/词元树证据以及无损源码重建，但其声明的语料并不是完整的 Rust 1.98
词法一致性套件。早期 `SafeCoreSyntax` 模型/解析器以稳定的 `RSP` 诊断处理具有代表性的
模块、项、语句、表达式、模式、类型、泛型和属性。有界的
`SafeCoreNameResolution` 原型目前跨独立的类型/值命名空间收集模块、项和局部符号，
并解析具有代表性的导入与限定路径。它的九项测试工具用例覆盖类型/值命名空间与限定
路径、可见性、重复/歧义/未解析名称、导入环、声明顺序与合法遮蔽、拒绝通过限定路径
访问函数局部变量/结构体字段/枚举泛型参数、Unicode 标识符规范化，以及导入嵌套
限制。本地可执行测试工具的 74/74 项测试全部通过。有界的
`SafeCoreHirLowering` 原型目前会把成功的语法与名称解析结果转换为确定性的、名称已
绑定的扁平 HIR arena。P1-01、P1-02 和 P1-03 仍处于 🚧 进行中，因为它们的依赖项、
完整配置分母、多文件加载和生产编译器集成仍未完成。

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

rustc 差分测试工具会记录带版本的报告；当所请求的 `rustc 1.98.x` 参照实现不可用时，
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

每个已声明夹具都必须与其精确词元、trivia、词元树、诊断、范围和源码重建结果匹配。
Windows 与 Linux CI 还要求清单、汇总和实际执行用例的分母完全一致。这只属于
RustSharp 词法分析器验收证据，并不构成 rustc 差分、运行时或完整 Rust 1.98 词法
一致性证据，因此 P1-01 仍为 🚧 进行中。

独立的 safe-core 语法配置通过当前包含六个用例的解析器验收清单，并写入
`artifacts/conformance/safe-core-syntax.json`：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax
```

该 6/6 报告只衡量 RustSharp 解析器验收结果，并不构成 rustc 差分证据或运行时一致性
证据。

包含六个用例的名称解析验收配置会写入
`artifacts/conformance/safe-core-name-resolution.json`：

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution
```

该报告只覆盖声明的进程内解析器/名称解析分母，并不构成 rustc 差分证据或运行时
一致性证据。同一个可执行测试工具也会测试早期 HIR 降低原型。这两个原型目前都尚未
进入生产编译器路径。

Linux Native AOT 探测器用于原生 Linux x64 运行器，并确保输出目录只归一次调用
独占：

```text
bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300
```

当主机不是原生 Linux x64 环境时，探测器以退出码 77 结束，并生成结构化的
`skipped` 证据；WSL 结果不视为原生 CI 证明。

`build` 和 Cargo 工作区命令为后续里程碑中的 ⏳ 计划项；当前纵向原型命令为
`compile`。

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
