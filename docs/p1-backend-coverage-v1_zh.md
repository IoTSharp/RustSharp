[English](p1-backend-coverage-v1.md) | [简体中文](p1-backend-coverage-v1_zh.md)

# P1 精确后端缺口见证 v1

🚧 进行中：运行器与验证器定义精确缺失的后端证据；原生执行和阶段门禁仍是独立义务。

冻结的 `p1-gate-coverage-v1-manifest.json` 指定六个注册测试，需要在原生 Windows x64 和 Linux x64 上运行 ILVerify 与 Native AOT。分母为 **6 × 2 × 2 = 24** 个单元。本 profile 不修改原有 160 行目录、回归测试分母或独立的 `p1-platform-v2` 套件。

| 需求 | 归属叶任务 | 见证 | 预期标准输出 |
| --- | --- | --- | --- |
| P1-REQ-009 | P1-06.06 | `repeated-array-once` | `seed\n7\n7\nseed\n` |
| P1-REQ-012 | P1-06.09 | `mutable-subslice-call-return` | `3\n42\n42\n` |
| P1-REQ-014 | P1-06.11 | `mutable-closure-captures` | `5\n9\n9\n` |
| P1-REQ-015 | P1-06.12 | `inline-const-loop` | `21\n` |
| P1-REQ-020 | P1-06.17 | `signed-scalar-ops` | `-3\n-2\n2\n7\n5\n-7\n-2147483648\n-2\n10\n` |
| P1-REQ-022 | P1-06.19 | `nested-place-owner` | `41\n` |

每行保留精确的原始 case ID、归属叶任务、注册源码及冻结的归一化 SHA-256。前五项使用原 Rust 源字符串。第六项的原注册为手写 MIR 程序 `NestedProgram(1)`：具名字段 → 元组成员 → 动态数组索引 → 可变引用 → 间接写入 41 → 读取原始所有者。其原始生成 PE 返回 41，因此托管执行预期退出码 41 且标准输出为空；原生宿主打印真实返回值。补充的 Rust 程序执行相同投影并打印 41。原始 MIR 和补充源码均通过两个后端后，才能关闭第六行的任一单元；仅声明等价不能关闭。

## 执行与来源

profile 为 `p1-backend-coverage-v1`；Rust 编译采用 `safe-core-mir-p1-v2` 及 `legacy-v1` Drop 清理。这六个见证没有引入 Drop 行为。`P1BackendCoverageRunner.RunAsync(root, report, rid, maximumFixturesToExecute, options, cancellationToken)` 接受有限的 1–6 项选择。`Options` 必须提供真实候选提交、源码快照报告和新鲜 Release 构建报告。先运行一项验证有界流程，再运行全部六项。部分选择不能关闭分母。

运行器通过 `ValidateCandidateInputsAsync` 在执行前后实际验证 Git/源码快照和新鲜 Release 构建。六个实际加载的实现 DLL 必须匹配新鲜 Release 指纹及其保留字节。编译生成的 PE、PDB、运行配置、复制的运行时、原注册源码、ILVerify 原始报告、Native AOT 宿主源码/项目/输入程序集和真实原生可执行文件保留在 `artifacts/p1-backend` 下。

ILVerify 使用固定的 `dotnet-ilverify` 10.0.11 及独立校验的运行时 SHA-256 验证原始生成 PE，不请求工具还原或安装。Native AOT 发布同一个 PE 和运行时，保留生成的 `Main`，要求零警告，验证原生 AMD64 PE 或 Linux x64 ELF，并执行发布二进制核对固定输出。进程记录保留 PID、父 PID、启动时间、参数、命令行、退出、输出及进程树清理结果。

执行采用串行方式：最多六项/七个原始 PE，套件截止 1,200 秒，执行进程 30 秒，ILVerify 包装进程 180 秒，发布 600 秒，均关联取消。仅回收本任务独占工具目录与发布宿主；审阅产物保留。Windows Shell 使用 PowerShell 7。不支持或其他原生主机的单元保持 ⛔ 已阻塞。WSL 或交叉编译不能产生另一个操作系统的原生执行报告。

## 闭环

`ValidateNativeReport` 仅检查结构和绑定，不能关闭门禁。`ValidateNativeReportAsync` 还检查保留文件的真实内容，并重复实际本机候选/构建验证。共享的 240 秒验证截止包含候选验证进程（最多 120 秒）和全部保留字节/语义检查，取消会传递给该进程。执行前后来源验证进程各自采用同样的 120 秒上限；进程证据最多允许五秒输出/清理宽限。完整的原生报告关闭本机 12 个单元，其余 12 个保持 ⛔ 已阻塞。

`ValidateClosedMatrixAsync(root, windowsReport, linuxReport, candidateSha, candidateTreeSha, cancellationToken)` 要求两个独立原生报告使用同一个候选/树，在两个报告共用的一个 240 秒截止内验证保留内容并核对精确的 24 个唯一单元。下载产物保留报告中的原始路径；物理验证仅把指定的保留证据目录映射到下载报告旁。缺少文件、重复 JSON 属性、缩减选择、警告、原始 PE/运行时字节变化、进程或清理不完整、模拟宿主行为均拒绝闭环。后端闭环本身不声明 P1 语言或聚合所有权已经完成。

✅ 已完成：运行器与验证器实现在候选 `dfdd76155934e286b85979a28b053ce8ffc10547` 上通过 8/8 隔离覆盖检查。[执行报告](evidence/p1/backend-validator.harness.json)与[原始／归档哈希](evidence/p1/backend-validator.archive.json)保留验证结果。原生 24 单元矩阵仍为 🚧 进行中。

生产 CLI 仅在对应实物门禁满足时返回零：

```text
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-backend-coverage-candidate <report> <win-x64|linux-x64> <1..6> <candidateSHA> <source-snapshot.json> <release-build.json>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-backend-native <report> <candidateSHA> <treeSHA> <win-x64|linux-x64>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-backend-matrix <windowsReport> <linuxReport> <candidateSHA> <treeSHA>
```

成功的单 fixture 试跑保留 2 个通过、22 个阻塞单元，返回 2。完整本机成功运行关闭 12 个本地单元、保留 12 个异平台阻塞单元，返回 0；只有独立双报告验证器能够关闭全部 24 个单元。
