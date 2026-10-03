# 63a02a5 之后的 P1 证据审计

[English](p1-evidence-audit.md) | 简体中文 · [P1 任务](roadmap/P1_zh.md)

状态：🚧 进行中。本审计记录 `63a02a51a971b4144870330c9f2a1f87e34f7d7a` 之后的推进，不是 P1 完成记录。P1 和六个 P1-GATE 叶子均未关闭。本地工作区验证不能证明一个新的已推送候选 SHA。

## 冻结语料的限制

不可变扩展清单包含 32 个差分源码和 24 个平台源码。检查发现，后 16 个差分源码及后 12 个平台源码只有 `// frozen P1 fixture:` 注释和输出用例名称的 `println!`。它们的源码哈希仍然有效，但没有执行名称所指的语义。通过这些输入只证明固定输入可以执行，不能关闭其所有权、Drop、包或后端要求。

| 套件 | 真实源码场景 | 仅输出名称的占位输入 | 固定分母 |
| --- | --- | --- | --- |
| `p1-differential-v3` | 16 | 16 | 32 |
| `p1-platform-v2` | 12 | 12 | 24 |

差分占位项：`borrow-aggregate-copy`、`borrow-partial-move`、`borrow-nll-branch`、`borrow-reborrow-escape`、`borrow-loop-join`、`borrow-index-projection`、`borrow-deref-projection`、`borrow-call-return`、`borrow-move-reinit`、`borrow-budget-limit`、`drop-aggregate-fields`、`drop-partial-move`、`drop-assignment-replacement`、`drop-temporary-scope`、`drop-unwind-nested`、`drop-double-panic`。

平台占位项：`aggregate-struct-drop`、`aggregate-enum-drop`、`slice-unsize`、`pattern-capture`、`panic-unwind-generated`、`panic-abort-generated`、`generic-import-call`、`byref-import-call`、`metadata-contract`、`mir-projection`、`mir-family`、`source-package`。

原清单及源码保持不变。两个运行器把语义关闭资格与进程结果分开记录。`eng/P1EvidenceValidation.ps1` 还检查真实冻结源码，因此篡改报告中的资格标记也不能把这些占位输入变成关闭证据。P1 关闭前需要以新的不可变套件版本提供完整语义源码和明确预期；不会静默替换或减少现有分母。

## 并行文件归属

| 分工 | 独占实施文件 | 范围及尚未关闭的边界 |
| --- | --- | --- |
| Linux 平台 | `P1ExpandedPlatformRunner.cs`、`Invoke-P1PlatformEvidence.ps1`、专属平台测试 | 真实输出、宿主/工具/哈希来源及 CoreCLR/ILVerify/AOT 进程证据；缺失的原生前置仍为 ⛔ 已阻塞。 |
| 源码包 | `RustSharpMetadata.cs`、`ClrLirAssemblyEmitter.cs`、`CompilerDriver.cs`、`P1SourcePackageContractTests.cs` | 有界标量源码 producer/consumer 契约与对账；聚合/引用/Drop 源码导入仍为 🚧 进行中。 |
| 所有权/Drop | `P1ExpandedDifferentialRunner.cs`、分配的所有权/Drop 语义及测试 | 精确语义诊断与真实冻结源码覆盖；完整 P1-07/P1-08 差分关闭仍为 🚧 进行中。 |
| 候选门禁 | 候选/扩展门禁脚本、工作流、共享测试注册及成对文档 | 拒绝缺失、陈旧、占位和不完整原生证据；P1-GATE 仍为 ⏳ 计划中。 |

共享默认构建输出由监督智能体串行写入。新增测试文件分别归属不同智能体；共享测试注册及文档更新串行集成。

## 环境与验证

仓库固定 SDK `10.0.400`。Windows 已安装 SDK `10.0.401`，位于 `C:\Program Files\dotnet\sdk\10.0.401`；PowerShell 为 `7.6.6`，位于 `C:\Program Files\PowerShell\7\pwsh.exe`。任务专属 `artifacts/p1-followup-gate/sdk/global.json` 选择已安装 SDK 进行本地验证，不修改仓库固定版本。基线 Release 构建通过，零警告、零错误。

WSL `Ubuntu` 为原生 x86_64，具有 .NET runtime `10.0.12`、SDK `8.0.131`、`9.0.115`、`10.0.112`，GCC `13.3.0` 位于 `/usr/bin/gcc`，Clang `18.1.3` 位于 `/usr/bin/clang`。其 PATH 没有 `pwsh`、`rustc` 或 `rustup`，也未安装 SDK `10.0.400`。这些环境观察不能证明完整 Linux 原生门禁。本轮没有安装工具。

`eng/Test-P1EvidenceValidation.ps1` 的 13 项有界拒绝检查通过，覆盖占位资格、缺失 Native AOT、未核验输出、未完成进程清理、陈旧源码哈希、重复 ID、候选 SHA 不匹配、缺失 oracle 版本、不完整关闭记录及尚未完成的需求指定叶子。合成报告仅存在于内存，不会保存为候选证据。缺失输入的候选运行生成六个阻塞门禁，退出码为 `2`。

证据目录为 `artifacts/p1-followup-gate`、`artifacts/p1-followup-linux`、`artifacts/p1-followup-package` 和 `artifacts/p1-followup-ownership`。最终构建/测试及原生报告计数必须读取真实制品；基础设施检查通过不能关闭语言契约。

## 候选关闭契约

扩展执行证据必须绑定候选 SHA、实际编译器哈希、清单/文件哈希、固定 ID、观察到的原生 x64 宿主、工具版本、输出、子进程记录、deadline 及资源清理。CoreCLR 与 Native AOT 都必须核对输出；仅退出成功不够。预期所有权拒绝必须命中诊断，不能由未支持降低或设施错误替代。

`docs/p1-completion.json` 必须是结构化 JSON，具有 schema version `1`、候选 SHA、状态原值 `complete` 和门禁 `P1-GATE.05`。它必须把全部 85 个已完成实施叶子绑定到具名输入报告，记录每份输入报告的哈希、两个原生运行 URL/制品及其候选 SHA、干净差异/资源清理证据，以及 `docs/p1-completion.md` 和 `docs/p1-completion_zh.md` 的哈希。两份路线图必须展示相同的已完成叶子。文本仅包含 SHA 和门禁名称不足以验收。本审计不创建完成记录。

手动扩展工作流保留前置及失败/阻塞执行报告，发布两份聚合报告，并在执行或关闭失败时返回非零退出码。上传步骤变绿不代表语义门禁通过。

## 验证快照

最终 SDK 10.0.401 Release 构建通过，零警告、零错误。泛型身份回归通过，P1-09 为 11/11，平台证据为 3/3，扩展所有权/平台证据为 10/10。固定差分运行器通过 32/32，其中 16 项是所有权/Drop 场景，16 项是占位输入；实际编译器 SHA 为 `3274B533146A6104A3F8FA014216FA6A284A5B9F061BEA0728EA59DC03130D2A`，并与冻结清单声明分开记录。该报告来自脏工作区探索运行，没有候选 SHA，语义关闭资格仍为 false。

Linux x64 最终版本的 CoreCLR 24/24 编译及输出匹配通过，但由于宿主缺少 rustc 和 ILVerify，24 行全部保持阻塞。`borrow-write-read` 单项 Native AOT smoke 以精确 `9\n9\n` 输出和零退出码通过，临时目录及进程已清理。单项 smoke 不能关闭 24 项原生分母。使用当前报告的候选聚合退出码为 `2`，六个 P1-GATE 均为阻塞。

完整 728 项测试以 190 秒有界运行，执行到无关的 scalar 清理竞态后超时；进程树已回收。定向 P1 套件和 fault-path Drop 测试通过。一次超时运行生成的 Drop 目录因自动审批复核拒绝递归和显式清理调用而保留；其归属、UUID、文件、哈希及无活动引用进程已记录在 `artifacts/p1-followup-ownership/orphan-cleanup.json`。该保留资源使我们不能宣称完整 harness 干净通过，现按证据报告而不静默删除。
