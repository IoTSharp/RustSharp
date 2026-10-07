[English](p1-source-package-evidence-v2.md) | [简体中文](p1-source-package-evidence-v2_zh.md)

# 绑定候选提交的源码包证据 v2

状态：🚧 进行中。实现运行器和验证器并不代表 P1-GATE.03 已闭环。闭环要求针对同一个实际候选提交及 tree，在 `win-x64` 和 `linux-x64` 两个平台产生并通过核验的新证据。

## 冻结范围

现有 `tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json` 保持不变，SHA-256 为 `72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8`。全部 19 个源码包用例及其冻结源码 hash、程序集身份、导出函数、输出轨迹和退出结果都是必需条件。18 个用例各有两个原始 PE，一个用例有三个，因此每个 RID 必须完成 39 个原始 PE 的 ILVerify 验证。

| 用例 ID | 包数量 |
| --- | --- |
| scalar-positional-copy | 2 |
| reference-shared-mutable-origin | 2 |
| slice-owner-mutation-length | 2 |
| aggregate-identity-projection-move | 2 |
| drop-ownership-transfer-exactly-once | 2 |
| imported-unwind-cleans-caller | 2 |
| imported-abort-skips-caller-drop | 2 |
| tuple-array-producer-clr-layout | 2 |
| source-named-tuple-unit-constructors | 2 |
| composite-return-reference-origins | 2 |
| composite-parameter-reference-path | 2 |
| projected-return-reference-origin | 2 |
| static-parameter-promoted-reference | 2 |
| enum-variants-construct-match | 2 |
| enum-active-static-reference-payload | 2 |
| imported-unwind-caller-double-panic | 2 |
| three-package-tuple-array-unit-owner | 3 |
| owned-tuple-partial-move-caller-drop | 2 |
| owned-tuple-partial-move-unwind | 2 |

## 显式策略与兼容性

`P1SourcePackagePlatformRunner.RunAsync` 保留 LegacyV1 源码发射行为和 schema 1，历史报告不能作为新门禁的闭环证据。`RunCandidateAsync` 要求显式提供候选 SHA、源码快照路径和 Release 构建报告路径，并生成 schema 2。每个 MIR v2 用例采用 `NativeV2`，记录 `native-v2`；primitives 用例采用 `LegacyV1`，记录 `legacy-v1`。

候选入口重新执行 `eng/P1SuiteEvidenceValidation.ps1` 中的 `Test-P1SnapshotEvidence` 和 `Test-P1BuildEvidence`。Release 报告必须保留构建前后的实际 Git 源码快照、最新测试程序集及注册清单、`rsc.dll` 和六个实现程序集。实际加载模块、最新 Release 输出和保留副本的 hash 必须一致。SDK 来自 `releaseBuild.sdkVersion`；临时 `tooling/global.json` 和使用后恢复的进程内 `RUSTSHARP_NATIVE_AOT_SDK_VERSION` 选择该 SDK，不修改仓库 `global.json`。

## 必需执行与保留字节

每个用例独立执行两次源码到 PE 的构建，PE 和 PDB 字节都必须一致。producer、consumer 及可选 wrapper 的元数据必须与冻结源码字节相符，保留元数据必须与 PE 内嵌文档一致。验证器从这些原始 PE 重新读取实际导入 MemberRef 签名，以及命名类型和结构类型的 owner 证明。

CoreCLR 执行原始 consumer PE。ILVerify 10.0.11 使用实际 runtime 和包引用图验证每个原始 producer、consumer 及可选 wrapper。schema 2 在两个平台上都记录 framework 和包引用 hash；Windows 还保留并核验嵌套 ILVerify 进程及 runtime 身份、hash 证明。

NativeAOT 发布的宿主源码必须精确为：

```csharp
namespace RustSharp.NativeAotHost;
internal static class EntryPoint { private static void Main() => global::RustSharp.Generated.Program.Main(); }
```

进程启动回调在 publisher 清理独占临时目录前，捕获实际项目、`Program.cs`、consumer、producer、可选 wrapper 和 runtime。验证器重新读取项目及输入 hash，要求实际产物为原生 x64 PE 或 ELF，核对真实 publish/run 命令及零 warning，并根据冻结预期重新分类 stdout、stderr 和退出码。

所有保留产物都必须列入报告 evidence 目录内的有界 hash 清单。缺失文件、过期 hash、替换原始 PE、虚构源码绑定、模拟宿主代码、缩小分母和未完整回收的进程树都会拒绝闭环。所属进程的启动和结果记录必须对应，源码包和 native host 临时目录必须已被清理。

## 验证入口与执行边界

`P1SourcePackageEvidenceValidator.Validate` 只检查 JSON 结构和绑定，其 `ArtifactContentVerified` 始终为 false；合成单元测试夹具不能通过门禁。`ValidateFileAsync(repositoryRoot, reportPath, Expectation(candidateSha, candidateTreeSha, runtimeIdentifier), token)` 重新读取保留实物和实际 Git、构建输入。只有同时要求 `Valid` 与 `ArtifactContentVerified` 的 `SatisfiesGate` 才能作为验收结果。

报告上限为 32 MiB，JSON 最大深度为 48，最多 524,288 个 token。保留清单最多 1,024 个文件，单文件最多 128 MiB，总量最多 512 MiB。实物验证和候选验证子进程各有 120 秒上限，并支持取消。源码包执行保留 20 分钟 suite、180 秒普通进程、600 秒 publish 时限，以及 156 个进程上限。成功、失败和取消路径都会回收独占临时目录及所属进程树；保留证据继续供审阅使用。

✅ 已完成：运行器与验证器实现在候选 `dfdd76155934e286b85979a28b053ce8ffc10547` 上通过 12/12 隔离契约控制。[执行报告](evidence/p1/source-package-validator.harness.json)与[原始／归档哈希](evidence/p1/source-package-validator.archive.json)保留验证结果。这些控制没有执行完整的 19 用例原生套件；P1-GATE.03 仍为 🚧 进行中。

生产 CLI 核验实际保留字节，仅在 `SatisfiesGate` 为真时返回零：

```text
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-source-package-candidate <manifest> <report> <1..19> <candidateSHA> <source-snapshot.json> <release-build.json>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-source-package-candidate <report> <candidateSHA> <treeSHA> <win-x64|linux-x64>
```

✅ 已完成：Windows 原生执行在候选 `93e60b2e3881bb13e5a836271739aaae944e13af`、tree `adcf297f7780b2ada00e5f5f745c501f714a926c` 上通过全部 19 个用例及 39 个原始 PE 的 ILVerify 验证。CoreCLR 和 NativeAOT 实际执行生成程序，满足冻结的输出和退出结果。生产实物验证器报告 `Valid=true`、`ArtifactContentVerified=true`、`SatisfiesGate=true`，且没有错误。[原生报告](evidence/p1/source-package-native-win-v1.json)、[验证回执](evidence/p1/source-package-native-win-v1.validation.json)与[原始／归档哈希](evidence/p1/source-package-native-win-v1.archive.json)保留本地证明。运行器与验证器均以零退出并完成回收；所属临时目录已不存在，原始审阅产物保留于 `artifacts/p1-source-package/`。仍需独立 Linux 执行及同 SHA 的 CI 聚合；P1-GATE.03 保持 🚧 进行中。
