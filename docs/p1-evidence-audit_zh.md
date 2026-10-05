# P1 证据审计

[English](p1-evidence-audit.md) | 简体中文 · [P1 任务](roadmap/P1_zh.md)

状态：🚧 进行中。本审计记录 `63a02a51a971b4144870330c9f2a1f87e34f7d7a` 及 `e601789ec7d6e5803e1a15f543ad6159c585a466` 之后的推进，不是 P1 完成记录。P1 和六个 P1-GATE 叶子均未关闭。本地工作区验证不能证明新推送候选 SHA 的原生平台证据。

## 冻结语料与当前闭环

当前扩展清单为 `tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v2-manifest.json`（清单版本 2；差分套件版本 4；平台套件版本 2），固定 32 个差分源码和 24 个平台源码。每一行差分和平台记录现均有可执行语义源码；此前 6 个 Drop 及 12 个平台占位项均已通过真实场景闭环。后端证据仍逐行独立绑定。

| 套件 | 可执行语义用例 | 语义占位项 | 固定分母 |
| --- | --- | --- | --- |
| `p1-differential-v3` | 32 | 0 | 32 |
| `p1-platform-v2` | 24 | 0 | 24 |

`artifacts/p1-next-session/p1-differential-v3-final2.json` 记录针对 rustc 1.98.0 的 32/32 个进程通过、`borrowSemanticClosure` 20/20 及 Drop 语义闭环 6/6。`artifacts/p1-next-session/p1-platform-v2-final-closed4.json` 记录 CoreCLR、ILVerify 和 Windows Native AOT 24/24 通过、`semanticClosureEligible=true`、绑定有效且清理完成。这些本地报告没有候选 SHA；候选发布聚合仍是独立的 P1 门禁。

扩展校验器仍会拒绝缺失、陈旧或不完整的语义/后端记录。进程输出通过但缺少必需后端证据，不能关闭用例。

## 并行文件归属

| 分工 | 独占实施文件 | 范围及尚未关闭的边界 |
| --- | --- | --- |
| Linux 平台 | `P1ExpandedPlatformRunner.cs`、`Invoke-P1PlatformEvidence.ps1`、专属平台测试 | 真实输出、宿主/工具/哈希来源及 CoreCLR/ILVerify/AOT 进程证据；缺失的原生前置仍为 ⛔ 已阻塞。 |
| 源码包 | `RustSharpMetadata.cs`、`ClrLirAssemblyEmitter.cs`、`CompilerDriver.cs`、`P1SourcePackageContractTests.cs` | 有界标量源码 producer/consumer 契约与对账；聚合/引用/Drop 源码导入仍为 🚧 进行中。 |
| 所有权/Drop | `P1ExpandedDifferentialRunner.cs`、分配的所有权/Drop 语义及测试 | 精确语义诊断与真实冻结源码覆盖；P1-07 和 6 个 Drop 差分用例为 ✅ 已完成，其余 P1-08 叶子保持独立。 |
| 候选门禁 | 候选/扩展门禁脚本、工作流、共享测试注册及成对文档 | 拒绝缺失、陈旧、占位和不完整原生证据；P1-GATE 仍为 ⏳ 计划中。 |

共享默认构建输出由监督智能体串行写入。新增测试文件分别归属不同智能体；共享测试注册及文档更新串行集成。

## 环境与验证

仓库固定 SDK `10.0.400`。Windows 已安装 SDK `10.0.401`，位于 `C:\Program Files\dotnet\sdk\10.0.401`；PowerShell 为 `7.6.6`，位于 `C:\Program Files\PowerShell\7\pwsh.exe`。任务专属 `artifacts/p1-followup-gate/sdk/global.json` 选择已安装 SDK 进行本地验证，不修改仓库固定版本。基线 Release 构建通过，零警告、零错误。

WSL `Ubuntu` 为原生 x86_64，具有 .NET runtime `10.0.12`、SDK `8.0.131`、`9.0.115`、`10.0.112`，GCC `13.3.0` 位于 `/usr/bin/gcc`，Clang `18.1.3` 位于 `/usr/bin/clang`。其 PATH 没有 `pwsh`、`rustc` 或 `rustup`，也未安装 SDK `10.0.400`。这些环境观察不能证明完整 Linux 原生门禁。本轮没有安装工具。

`eng/Test-P1EvidenceValidation.ps1` 的 15 项有界拒绝检查通过，覆盖占位资格、缺失 Native AOT、未核验输出、未完成进程清理、陈旧源码哈希、重复 ID、候选 SHA 不匹配、缺失 oracle 版本、不完整关闭记录及尚未完成的需求指定叶子。合成报告仅存在于内存，不会保存为候选证据。缺失输入的候选运行生成六个阻塞门禁，退出码为 `2`。

证据目录为 `artifacts/p1-followup-gate`、`artifacts/p1-followup-linux`、`artifacts/p1-followup-package` 和 `artifacts/p1-followup-ownership`。最终构建/测试及原生报告计数必须读取真实制品；基础设施检查通过不能关闭语言契约。

## 候选关闭契约

扩展执行证据必须绑定候选 SHA、实际编译器哈希、清单/文件哈希、固定 ID、观察到的原生 x64 宿主、工具版本、输出、子进程记录、deadline 及资源清理。CoreCLR 与 Native AOT 都必须核对输出；仅退出成功不够。预期所有权拒绝必须命中诊断，不能由未支持降低或设施错误替代。

`docs/p1-completion.json` 必须是结构化 JSON，具有 schema version `1`、候选 SHA、状态原值 `complete` 和门禁 `P1-GATE.05`。它必须把全部 85 个已完成实施叶子绑定到具名输入报告，记录每份输入报告的哈希、两个原生运行 URL/制品及其候选 SHA、干净差异/资源清理证据，以及 `docs/p1-completion.md` 和 `docs/p1-completion_zh.md` 的哈希。两份路线图必须展示相同的已完成叶子。文本仅包含 SHA 和门禁名称不足以验收。本审计不创建完成记录。

手动扩展工作流保留前置及失败/阻塞执行报告，发布两份聚合报告，并在执行或关闭失败时返回非零退出码。上传步骤变绿不代表语义门禁通过。

## 历史验证快照

之前的探索快照 `p1-differential-v3-v2-current.json` 作为历史证据保留；它早于 Drop 和平台语义源码闭环。当前报告及其有界结果已记录在上方的闭环章节。Linux 主机缺少本任务所需的 rustc/ILVerify，因此本轮 Windows 报告作为完整平台证据来源；候选发布聚合仍是独立门禁。

完整可执行测试工具通过 784/784，失败和跳过均为零。有界运行器为每个差分和平台用例记录进程归属、deadline、输出上限和清理。当前 v2 清单不再含占位行。

## e601789 之后的契约加固

2026-10-03 至 2026-10-04 的后续任务获取了 `origin/master`，确认干净基线为 `e601789ec7d6e5803e1a15f543ad6159c585a466`。三个子智能体分别负责独立实施/测试文件；共享测试注册、构建输出及成对文档串行整合。

| 叶子 | 已实现契约 | 证据及剩余边界 |
| --- | --- | --- |
| P1-07.12 | 显式所有权证据复用自动适配的选项校验，执行逐函数局部值、块、语句及所有权效果限制。语义拒绝保留原始诊断消息、路径和范围。 | `P1OwnershipResourceContractTests` 检查精确边界、整体与逐函数限制、取消、共享操作量/时间预算及诊断传播。完整所有权求解器和证据类别语料仍为 🚧 进行中。 |
| P1-09.03, P1-09.07 | 元数据解析拒绝未知成员及缺失/null 核心集合；嵌套构造器必需字段不能静默采用默认值。精确 CLR 标识优先于源码别名，歧义特化别名不能解析。源码标量契约要求显式 schema 和完整条款。 | `P1SourcePackageContractTests` 在消费者发射前篡改真实源码 PE 元数据，并保留有效源码特化/旧 LIR 行为。源码聚合/引用/Drop 契约仍为 🚧 进行中。 |
| P1-10.07 | 扩展平台报告把可信固定 ID、源码/期望哈希及预期输出绑定到宿主/工具/前置和 CoreCLR/ILVerify/Native AOT 进程证据。严格 JSON 结构及有界校验拒绝重复或畸形记录。 | `P1EvidenceBindingTests` 及平台契约测试拒绝替换或不完整证据；24/24 报告的 `semanticClosureEligible=true` 且绑定有效。候选发布仍是独立门禁。 |

差分源码哈希及报告现已绑定本工作树中的全部 32 个可执行语义夹具，包括 6 个 Drop 用例；平台清单绑定 24 个语义源码。Windows 验证使用已安装 SDK `10.0.401`；完整平台报告包含 ILVerify 与 Native AOT 证据。

独立复核还发现，进程可以在报告区间内开始，却声明延伸到区间之外的持续时间。校验现在检查完整区间，容许一秒调度偏差；嵌入 ILVerify 子进程必须同时落在启动器和报告区间内。新增四个变异覆盖这些情况，不改变测试分母。

✅ 本地验证已完成：Release 构建零警告/零错误；完整工具通过 784/784，失败/跳过均为零（`artifacts/p1-next-session/full-tests-p1-closed.log`）。扩展差分报告通过 32/32，其中借用 20/20、Drop 6/6；平台报告通过 CoreCLR、ILVerify 和 Native AOT 24/24，绑定有效且清理完成。所有权黄金、扩展证据和资源契约测试均已注册并包含在本次运行中。候选发布仍独立于这些本地结果。

`roadmap-final` 和 `audit-parity.json` 核对成对标题、表格、任务/状态标记、代码事实及链接；`git diff --check` 通过。本轮进程记录均报告清理完成，`cleanup.json` 区分了一个被无关进程复用的 PID，记录本任务没有存活进程。空任务目录已移除；验证制品及前一轮已记录的遗留目录保持保留。
