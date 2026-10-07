# P2 监督者检查点

[English](P2-HANDOFF.md) | 简体中文 | [P2 契约](P2_zh.md)

## 授权与接续

用户授权监督子智能体实现全部 15 个 P2 父任务、94 个实施叶子和 7 个门禁叶子，
随后将 P1 全部未完成的实现、集成和退出门禁要求一并纳入。每验收一个任务就单独
提交一次本地代码。`rustsharp-p2` heartbeat 每 30 分钟接续本会话，名称为
RustSharp P1/P2 子智能体监督与逐任务提交。派单前核对当前文件、Git 历史和存活
智能体；接续现有派单，避免重复。监督者负责共享注册入口、双语状态、串行构建、
验收和提交。在代码可供本地审查且检查通过后，完成 P1/P2 门禁明确要求的已推送
SHA／CI 验证。本授权不包含产品发布、部署、付费安装或向外部发送消息。

每次 heartbeat 的工作预算为 25 分钟，最多三个工作子智能体。每次委派都重复机器级
PowerShell 7、有界循环／搜索／命令、进程身份与清理、工具发现、临时路径归属以及
禁止 Graphify 的规则。保留原有未跟踪的 `tmp/`。

## 前置项审计

P2 为 🚧 进行中。P1-GATE.01–.03 为 🚧 进行中，.04–.06 仍为 ⏳ 计划中；
不声明 P1/P2 阶段闭环。
源码提交 `0c5d80f623ab263682ed36c47f3e9182e23374f9` 的公开检查均为 ❌ 失败：

| 检查 | 证据 |
| --- | --- |
| Windows P0 | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927965> |
| P1 平台门禁 | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927863> |
| Linux Native AOT | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927846> |

P1 平台作业的 native 子项通过，但完整退出门禁失败。Linux 作业的原始类型差分
14/14 通过；后续 ILVerify 因引用集遗漏 `RustSharp.Runtime` 失败。
P1-GATE.01 需要绑定候选提交的语义覆盖；.02 需要
解决尚未批准的 Linux 展开输出差异；.03 需要同一 SHA 的源码包执行；.04 需要远程
套件证据；.05 需要同一远程 SHA 的全部必需工作流通过；.06 需要目前缺失的双语完成
报告。历史 P1-10 的 15/15 结果明确记录 `fullP1Closure: false`。保留这些验收要求。
P2-04.01、P2-06.01 和 P2-08.01 依赖已验收的 P1 父项，可独立推进。
core/alloc/std 任务链等待 P1-GATE。

## 当前所有权与后续工作

| 叶子 | 状态 | 所有者与预留文件 |
| --- | --- | --- |
| P2-04.01 | ✅ 已完成 | `cargo_p2`：`CargoContract*.cs`、`P2CargoContractTests.cs`、`p2-cargo-v1-manifest.json`；10/10 隔离验证器测试和 78 条冻结记录。 |
| P2-06.01 | ✅ 已完成 | `prerequisite_audit`：`DotNetInteropContract*.cs`、`P2InteropContractTests.cs`、`p2-dotnet-interop-v1-manifest.json`、双语 ADR 0010。 |
| P2-08.01 | ✅ 已完成 | `tooling_p2`：`ToolingContract*.cs`、`P2ToolingContractTests.cs`、`p2-tooling-v1-manifest.json`。 |

每个合同通过后，单独提交对应叶子的文件与证据／状态更新。先将工作智能体转到
Linux 原始类型一致性失败、未批准的生成 Drop 展开差异，以及 P1 覆盖／源码包证据。
随后预留互不重叠的文件，分派 P2-04.02（清单加载器）、P2-06.02（绑定）和
P2-08.02（无损格式化）。
清单中的后续用例清册不证明实现用例已经执行。继承的测试运行器限制注册数为 1024；
新增 P2 目标时，不得悄悄修改冻结的 P1 分母或突破此上限。

## 本地验证与资源台账

PowerShell 为 `C:\Program Files\PowerShell\7\pwsh.exe`，版本 7.6.6。
本机 .NET SDK 为 10.0.401，驱动路径为
`C:\Program Files\dotnet\sdk\10.0.401\dotnet.dll`；仓库保留 10.0.400 固定版本。
现有显式 SDK 驱动方式允许本地验证，无需修改固定版本。一秒有界 SDK 试运行通过，
串行 Release 基线构建以零警告／零错误通过。PID 35176 和 93044 均已退出，
`CleanupComplete: true`；元数据和日志保留于 `artifacts/p2-supervision/`。
本任务没有创建临时源码树。

`eng/Invoke-P2ContractEvidence.ps1` 为隔离的过滤合同验证记录固定分母、清单哈希、
源码候选、工具／RID、有界进程台账、清理和失败原因。报告区分验证器测试与冻结的
后续实现分母，并保留 `fullP2Closure: false`。源码候选必须匹配整个工作树；
先等待工作智能体冻结文件，再生成候选快照。报告与哈希来源证明保留于
`artifacts/p2-contracts/`。

首批合同通过 36/36 定向测试，以及额外的 10/10 + 8/8 + 18/18 隔离、绑定源码的
验证。验证候选为 `41e14c1587d4366007fa108b0f5f6ba023da6c77`
（641 个编译器／测试／工具输入），保留于
`refs/codex/p1-10-candidates/41e14c1587d4366007fa108b0f5f6ba023da6c77`。
可迁移的叶子报告与记录原始／归档哈希的 LF 规范化运行器记录归档于 `docs/evidence/p2/`。
Release 构建零警告／零错误；当前注册数为 1005（继承的 969 项 P1 注册与 36 项 P2
注册）。全部构建／测试子进程已退出，快照助手已删除其专属索引。

已下载、校验哈希并以只读方式检查原始 Linux ILVerify 制品，未执行其中内容。
选出的报告／来源证明保留于 `artifacts/p1-supervision/cargo-ci-download/`。
自动审批拒绝了组合 ZIP 提取／删除，仅返回 `blocked by policy`；安全只读提取成功。
40,140,758 字节的 `archive.stdout.log` 仍保留于该目录；不得绕过拒绝或声称 ZIP 已删除。
三个下载／网络进程 PID 52444、23432 和 42364 均已退出。

## P1 整改派单

已验收的 P2 提交为 `3aa7c00`（P2-04.01）、`867a62a`（P2-06.01）和
`2075ebe`（P2-08.01）。这些清单是设计清册；后续实现叶子仍未关闭。
三个工作智能体已明确重新分派到 P1：

| 工作 | 状态 | 预留所有权 |
| --- | --- | --- |
| ILVerify 运行时引用 | 🚧 进行中 | `cargo_p2`：`eng/Invoke-ILVerify.ps1`、`eng/Test-ILVerifyRuntimeReference.ps1`；真实生成 PE、缺失／错误 sidecar 和旧案例。 |
| native Drop/展开闭环 | 🚧 进行中 | `prerequisite_audit`：CLR LIR/发射器及程序集元数据、MIR Drop 降低、`CompilerDriver.cs`、`GeneratedPanic.cs`、Drop 差分运行器、新 native-v2/v5 配置档／文档／测试。 |
| 精确 P1 需求／证据映射 | 🚧 进行中 | `tooling_p2`：`P1GateCoverageContract.cs`、`p1-gate-coverage-v1-manifest.json`、`P1GateCoverageContractTests.cs`；最多十项新增注册。 |

保留旧 v1 Drop 和 v4 运行器入口。Native-v2 必须是具名、可观察的生产选择，保留父／子
异常链并通过真实生成程序检查；不授权输出字符串特判或批准新差异。门禁映射必须使用
冻结的可执行、仅检查及拒绝分类与真实证据，不能用 40/160 清册或通用平台样本替代
缺失覆盖。REQ034 和 REQ040 是聚合／平台边界，不得引入前置依赖环。
映射验收并单独提交后，将 `tooling_p2` 转到十九项源码包运行器的同 SHA 来源绑定和
严格验证器。root 负责共享门禁／报告／CI／CLI 注册及串行验证。
