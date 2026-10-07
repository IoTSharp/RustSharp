# P1 原生 Drop 传输组件

[English](p1-drop-transport-v1.md) | 简体中文

## 交付

✅ 已完成本组件的本地工具验收。原生写入器和聚合验证器将实际打开的制品字节与原始
回执、源码快照及零警告/错误的 Release 构建逐项对账。保持固定 28 个生成案例
（26 个精确匹配、两个继承差异）、30 份原始 PE ILVerify 报告、28 个 Native AOT
案例、两个 callable 制品/七个 callable 案例以及 36 份有序进程台账。原始验证器的
阶段、路径、哈希、长度、版本 10.0.11 及验证后保留路径的哈希必须一致。

读取器拒绝仓库路径逃逸和父链接，对实际打开字节计算哈希，核对长度/EOF，并保留取消
及最终单调时限检查。限制为 512 个见证、共享读取 2GiB、总计 90 秒、每文件 10 秒、
捕获 JSON 32MiB 和每见证 512MiB。发布使用独占 CreateNew 临时文件和不能覆盖旧证据
的原子移动。入口要求 Actions 仓库、job、SHA、run 和 attempt 字段一致；这些检查
绑定上下文字段，并不能独立认证 GitHub 服务器响应。

## 验收

候选 `c950e0164719bdcf6309e944c2e0078d2ee5eafe`、tree
`08ec300868804497bc985f547bd8679f88e8f5cf` 绑定 707 个源码输入及全部四份新脚本。
SDK 10.0.401 Release 构建通过，零警告/错误。Windows 和 Ubuntu 各通过 tiny1
及固定 12 项工具控制。这些控制包含模拟验证器元数据；真实生成程序、原始 PE
ILVerify 检查、Native AOT 程序和正式 joint 控制的执行数均为零。工具固定 12 项与
正式 joint12 分母独立。两个真实入口进程均拒绝缺少所需 Actions 上下文的调用。
四份 PowerShell AST 检查通过。1203 个注册 harness 案例仅完成清单核对，未执行。

[验收归档](evidence/p1/drop-transport-v1-archive.json) 保留源码和原始报告哈希。
主监督者检查全部 13 个已记录的 Windows 启动进程身份均已不存在；四个独占工具沙箱
及其链接均已移除。完整命令参数和记录的父进程身份保留在资源复核中。回收验证明确为
`parent-exit-only`，且 `descendantContainmentVerified=false`。

## 剩余闭环

🚧 进行中：将原生写入器、聚合验证器及必需的正式 joint12 门禁接入 CI，再在原生
Windows/Linux 上执行和对账同一已推送 SHA。当前工作流没有 `drop-native` job，
且未调用这些传输入口。真实计时器延迟/I/O 故障注入与可信完整后代隔离仍开放。
本组件不关闭任何 P1 门禁、P2 叶项、父项或阶段。P2 仍为 8/101 ✅ 已完成叶项。
