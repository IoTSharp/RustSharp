[English](p1-10-implementation.md) | [简体中文](p1-10-implementation_zh.md)

# P1-10 版本化回归套件与证据聚合

状态：✅ 已完成，已在原生 Windows x64 和 Ubuntu WSL Linux x64 验证。P1-10 闭环版本化测试和报告契约；六个
P1-GATE 发布／语言检查保持独立。本地验证候选是真实 Git 提交对象，保留于
`refs/codex/p1-10-candidates/`，不移动 HEAD，也不修改用户暂存区。这不代表
候选已推送或已在 CI 运行。

## 实现与验收账本

| ID | 状态 | 实现与证据 |
| --- | --- | --- |
| P1-10.01 | ✅ 已完成 | 冻结覆盖目录：40 个需求和 160 个类别／后端清单行。目录验证与逐需求执行保持区别。 |
| P1-10.02 | ✅ 已完成 | 不可变扩展清单第 2 版固定差分 v3 为 32 项、平台 v2 为 24 项，包括源码和期望哈希。 |
| P1-10.03 | ✅ 已完成 | 实际编译通过／失败／运行结果，精确诊断及输出契约；基础设施失败、超时或缺失执行不能满足负例。 |
| P1-10.04 | ✅ 已完成 | 保留回归 v1/v2、差分 v2、平台 v1，固定分母为 8/24/16/12。 |
| P1-10.05 | ✅ 已完成 | 对全部冻结用例采集有界 rustc 1.98 差分进程证据，包括 PID、父进程、命令、开始时间、终止及清理。 |
| P1-10.06 | ✅ 已完成 | 原生 Windows/Linux CoreCLR、原始 PE ILVerify 与 Native AOT 执行器，保留固定结果及分母。 |
| P1-10.07 | ✅ 已完成 | 报告绑定源码、期望、清单、候选身份、原生宿主、工具版本、编译器哈希、截止时间与清理。 |
| P1-10.08 | ✅ 已完成 | `Test-P1SuiteGate.ps1` 独立验证两套各七份报告和 15 个固定聚合检查，核实真实候选 Git blob、完整输入清单及新构建编译器绑定；实际候选通过 15/15。 |
| P1-10.09 | ✅ 已完成 | 原生 CI 作业始终发布成功／失败证据；发布描述绑定运行 ID、尝试次数、精确 SHA、唯一产物名、相对文件及 SHA-256。八项离线拒绝契约覆盖缺失、过期、取消和篡改发布。尚未运行远程 CI。 |
| P1-10.10 | ✅ 已完成 | 完整 Release 工具逐一报告已注册用例，并以独立有界工作进程执行；两平台均通过 969/969。不可变审计将全部 60 个保留用例及夹具字节与基线提交 `23279d93267a814c643baddc29c72918ff0fda0b` 比较；两平台均通过 60/60。 |

## 报告与可复现验证

每个平台报告集恰好包含七份经哈希绑定的报告：覆盖目录、扩展差分、扩展平台、
Release 构建、完整回归工具、不可变基线审计和候选源码快照。聚合器检查两套报告
及其共享 Git 树；过滤运行不能关闭完整注册分母。`fullP1Closure: false` 明确
记录独立的 P1 门禁。

`Get-P1SourceSnapshot.ps1` 使用临时 Git 索引创建本地快照，并为完整的编译器、
测试、夹具、示例及工具输入清单核对经规范化的 Git blob ID 和原始 SHA-256。
文档可随后记录证据，属于编译器输入范围之外。新增未跟踪输入、源码缺失、
源码变化及把树对象伪装成提交均会拒绝。

P1 v3/platform v2 夹具通过显式 Git 属性保留已审核的原始字节，包括影响冻结哈希
和诊断位置的 CRLF。清单工作文件遵循已有 LF 规则；历史 60 项用例的源码、Git blob
和期望值保持不变。差分验证接收实际观察到的 Ubuntu RID 与操作系统元数据。生成
panic 的检查使用异常和 abort 诊断，因为 Linux 的普通 CLR 未处理异常和 abort 都
可能返回 134。

完整 harness 中的额外 Drop 清单版本化为 `p1-drop-closure-v4`，固定 28 项源码／
生成程序契约。Windows 记录 26 项精确一致和两项明确的正常清理差异；Linux 记录
25 项一致和三项差异。额外的 Linux 用例为 `unwind-own-drop-body-failure`：rustc
打印 `body/owner/bad`，Rust# 打印 `body/owner`，两者均报告双重 panic abort。
验证器以每个保留 rustc 可执行文件的 SHA-256 和 PE/ELF x64 头绑定原执行主机，
拒绝变更输出、平台标识、计数、进程参数和旧版本。两平台均通过 35 项篡改控制。
这项记录明确差异而不声明 P1 语言认可；`fullP1Closure` 和
`fullP1LanguageGateApproved` 均保持 false。

`Invoke-P1ReleaseBuild.ps1` 选择明确已安装的 SDK 驱动，记录 SDK 探针及还原／
构建进程，要求零警告／零错误，并计算新构建编译器的哈希。独立的有界
`RustSharp.Tests.dll --list` 调用采集真实注册 ID 与测试程序集哈希，不执行用例。
聚合器核对原始输出和嵌入清单，并要求 harness 的每个 ID、顺序与程序集哈希完全
匹配。真实 969 用例报告即使删减到 464 并同步自身全部计数及哈希，也会被独立的
构建清单拒绝。这是独立的构建采集锚，不提供密码学执行证明。它不修改
`global.json`。本机使用 Windows SDK 10.0.401 和 Ubuntu WSL SDK 10.0.112；
CI 使用仓库固定的 10.0.400。每份报告保留实际版本。

```powershell
pwsh -NoProfile -File eng/Get-P1SourceSnapshot.ps1 -CreateCandidate -EvidencePath artifacts/p1-10/source-snapshot.json
# Use the candidateSha from the actual snapshot record below.
pwsh -NoProfile -File eng/Invoke-P1ReleaseBuild.ps1 -CandidateSha <SHA> -SdkVersion 10.0.401 -ReportPath artifacts/p1-expanded/windows-x64/release-build.json
pwsh -NoProfile -File eng/Invoke-P1SuiteReports.ps1 -CandidateSha <SHA> -PlatformName windows-x64
pwsh -NoProfile -File eng/Get-P1SourceSnapshot.ps1 -CandidateSha <SHA> -EvidencePath artifacts/p1-expanded/windows-x64/source-snapshot.json
pwsh -NoProfile -File eng/Write-P1SuiteReportSet.ps1 -CandidateSha <SHA> -RuntimeIdentifier win-x64 -ReportDirectory artifacts/p1-expanded/windows-x64
# Repeat on native Linux with its installed SDK and linux-x64 paths/RID.
pwsh -NoProfile -File eng/Test-P1SuiteGate.ps1 -CandidateSha <SHA> -WindowsReportSet artifacts/p1-expanded/windows-x64/p1-suite-report-set.json -LinuxReportSet artifacts/p1-expanded/linux-x64/p1-suite-report-set.json -EvidencePath artifacts/p1-10/p1-suite-gate.json
pwsh -NoProfile -File eng/Test-P1EvidenceValidation.ps1
pwsh -NoProfile -File eng/Test-P1SuiteGateValidation.ps1
pwsh -NoProfile -File eng/Test-P1CiPublicationContract.ps1
pwsh -NoProfile -File eng/Test-Roadmap.ps1
git diff --check
```

## CI 发布与失败来源

[原生工作流](../.github/workflows/p1-expanded.yml) 检出精确候选和完整基线历史，
在构建前和执行后验证源码，即使准备、工具或执行失败，也上传全部报告及启动
诊断。缺失报告被明确记为阻塞，执行数和跳过数均为零。聚合器下载两份唯一
产物，核对发布运行／尝试次数／文件哈希及原生作业结果，成功或失败均发布
套件聚合。CI 运行 URL 由真实 GitHub 运行记录；本地契约夹具不能冒充此类运行。

## 新证据与资源审计

已验证候选为 `0a415e25c362f8a35c09cb9e1163f5ce30accf82`，树为
`e1448fc1ff7314f9c6fa5fd581642bbd3428ee95`。[候选快照](../artifacts/p1-10/final2-source-snapshot.json)、
[Windows 报告集](../artifacts/p1-expanded/windows-x64/p1-suite-report-set.json)
和 [Linux 报告集](../artifacts/p1-expanded/linux-x64/p1-suite-report-set.json)
绑定相同的 631 项编译器输入。两平台均在执行前后核实这些输入。
[聚合报告](../artifacts/p1-10/p1-suite-gate.json) 的 15 项检查全部通过，失败、
阻塞和跳过均为零。

| 实际候选验证 | Windows x64 | Linux x64 |
| --- | --- | --- |
| Release 构建 | ✅ 已完成：0 警告 / 0 错误 | ✅ 已完成：0 警告 / 0 错误 |
| 完整注册 harness | ✅ 已完成：969/969 | ✅ 已完成：969/969 |
| 差分 v3 | ✅ 已完成：32/32 | ✅ 已完成：32/32 |
| 平台 v2：CoreCLR / ILVerify / Native AOT | ✅ 已完成：24/24 | ✅ 已完成：24/24 |
| 不可变基线审计 | ✅ 已完成：60/60 | ✅ 已完成：60/60 |
| 执行前后源码输入验证 | ✅ 已完成：631/631 | ✅ 已完成：631/631 |

覆盖验证记录 40 个需求和 160 个清单行；这些行不代表 160 次独立语义执行。
拒绝验证通过 43 项套件控制、九项 Linux 宿主控制、15 项扩展证据控制和八项
CI 发布控制。[闭环记录](../artifacts/p1-10/closure.json) 绑定最终证据、工具版本
及文档检查。历史报告，包括先前的 RID 绑定尝试，单独保留。

[资源审计](../artifacts/p1-10/final-resource-audit.json) 检查最终控制进程身份、
报告中的进程清理以及任务自有 Linux 源码目录的移除。已安装工具和证据归档有意保留。
一份任务自有合成 Git 夹具 `.suite-snapshot-trial-4a993b82e4824ad1b920794d6b1f23d7`
仍保留在 `artifacts/p1-10`：自动审批以 `blocked by policy` 拒绝删除，
该夹具没有存活的任务进程。该保留夹具不能替代候选证据。远程 CI 发布及六个独立
P1-GATE 检查尚未声明完成。
