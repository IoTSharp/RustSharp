# Formatter parser 预算竞态修复

[English](p2-formatter-budget-v1.md) | 简体中文

## 交付

✅ 已完成时限竞态修复。formatter 预算在两次时钟采样之间耗尽时，首次检查可能通过，
而第二次采样产生非正的 parser 超时。候选 `4d33334` 的 CI run `37702329159`
在 Linux `P2-08.02 format-budget` 中暴露了这一问题。Remaining 现在先检查取消，
从单次 elapsed 采样计算剩余预算，并在该值非正时拒绝。既有超时处理返回 RSTF0001
并保留原始源码。parser 选项验证和全部时限均保持原有标准。

## 验收

候选 `0004e750d80900d2881d7ea680ce9a1b26228aec`、tree
`8c0e49054af4bff4da25c3a880ea19450744e68e` 绑定 707 个编译器输入。新鲜
SDK 10.0.401 Release 构建通过，零警告/错误。Windows 通过 tiny1、formatter59
及独立的 MIR or-pattern 诊断1。Ubuntu 使用 Windows 构建的可移植程序集通过
tiny1 和 formatter59；这是本地 Ubuntu 运行验证，不是原生 Linux CI 构建证据。
既有预算案例包含 32 次有界的单 tick 超时断言，未改变 formatter59 或 parent91
分母。注册数仍为 1203。

[验收归档](evidence/p2/formatter-budget-v1-archive.json) 保留源码/报告哈希。
主监督者检查 70 个已记录的 Windows 身份和 62 个 Ubuntu 身份均已不存在。
这些检查覆盖已记录的启动进程和 worker，不能证明全部后代均被可信隔离。

## 剩余闭环

P2-08.02 为 🚧 进行中：仍需真实 Windows 定向取消、实际异步 I/O 中断及完整的
同一已推送 SHA 验收。P2 仍为 8/101 ✅ 已完成叶项；不关闭任何 P1 门禁、父项
或阶段。

旧 CI 的两个 expanded job 均为 1202/1203，未执行案例为零。Windows formatter59
通过；其独立 MIR binding or-pattern 测试运行 6.0166 秒后以类型分析工作量/嵌套/
时限诊断失败。新鲜本地诊断1通过，但不据此宣称 Windows CI 故障已修复或原因已确定。
保留失败报告，继续跟踪新 SHA。
