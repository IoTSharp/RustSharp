# P1 生产回执诊断

[English](p1-receipt-diagnostic-v1.md) | 简体中文

## 交付与验收

本诊断修复为 ✅ 已完成：报告验证器失败且缺少验证后哈希时，保留原始错误并说明
成功验证不可用。验证器通过但报告哈希不同，仍报告字节变化。哈希相同时不误报。
保留原有四阶段要求及回执关闭谓词。

候选 `7621c378b3d622cece4c815564130bd99412c2a2`、tree
`ae5a1e406e4a95a9871a73afef97a12ba4b1d971` 绑定 696 个源码输入。
PowerShell 7.6.6 通过一项极小试跑、全部三项固定诊断控制及全部八项既有 CI 发布控制。
诊断控制从真实生产脚本提取准确候选、预算及关闭表达式。它们使用合成哈希并有意
保留 `closed=false`，未执行 producer 或接受阶段门禁。两个独占控制沙箱均已移除。
手动 P1 工作流在 Windows 和 Linux 上预先运行这些控制。

[验收归档](evidence/p1/receipt-diagnostic-v1-archive.json) 记录实际源码哈希、报告
哈希及源码快照绑定。原始报告及启动进程身份保留于
`artifacts/p1-supervision/receipt-diagnostic-root-v1/`。
[三项控制报告](evidence/p1/receipt-diagnostic-v1-controls.json) 保留实际观察到的修复
前后消息及明确的工具控制范围。

## 复现与剩余闭环

隐藏目录清理修复为 ✅ 已完成：`264416fc3898b63e1e6ed54d4c75c45c432572f6`
的运行 `37699582503` 通过全部三项 Linux 工具断言，随后检查隐藏
`.utility-owned-*` 沙箱时失败。两个准确沙箱操作现均使用 `-Force`，保留任务
归属、PID、绝对路径、重解析点、五文件及五秒清理检查。候选
`b630a6873383feb1ec09dafd970cca187fb0fc5c` 绑定 703 个输入。
Windows 与 Ubuntu PowerShell 7.6.6 各通过极小试跑 1/1 和固定控制 3/3，
四个沙箱全部移除。[清理归档](evidence/p1/receipt-cleanup-v1-archive.json)
保留哈希和进程观察。这些是工具控制；仍须在推送后的修复 SHA 上取得新的
Windows/Linux 原生 CI 证据。

创建独占输出目录，然后使用 PowerShell 7 运行：

```powershell
./eng/Test-P1ProductionReceiptDiagnostics.ps1 -ResultPath artifacts/receipt-controls/controls.json
```

🚧 进行中：同一推送 SHA 的原生 CI、NativeV5 回执及 joint 门禁集成、独立后代进程
约束及六个 P1 阶段门禁。本修复不证明实际制品发生不匹配，也不关闭任何 P2 叶项。
