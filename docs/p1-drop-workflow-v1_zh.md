# 原生 Drop CI 接线

[English](p1-drop-workflow-v1.md) | 简体中文

## 交付

运行验收为 🚧 进行中。手动 `p1-expanded.yml` 工作流现包含独立的 `drop-native`
矩阵，分别使用 Windows 2025 和 Ubuntu 24.04 x64。每个 job 选择 SDK 10.0.401、
rustc 1.98.0 和 ILVerify 10.0.11，执行既有 NativeV5 producer，再写入原始字节
传输索引。producer 和 writer 失败会中断正常步骤顺序。always-run 上传保留回执/
验证器/进程目录及全部 Drop 保留目录（含隐藏文件），制品名称包含 run/attempt。

生产聚合依赖 `drop-native`，将两个原生目录下载到原始仓库路径，再执行两个物理
传输验证器。它要求可信上游结果为 `success`，且两个验证器均通过。下载缺失将通过
下游验证器的错误保持可见。双平台批次固定两项、250秒预算和120秒进程限制；单次
读取仍保持 transport 更严格的取消与单调时限。

## 本地验证

[验收归档](evidence/p1/drop-workflow-v1-archive.json) 记录真实源码候选、Release
构建及原始工具报告哈希。

候选 `04a1a15f05f28f05471fe48816544600d0c5038f`、tree
`3a6946d9077ca956c6c9feb24bca66eb83510586` 绑定707个编译器输入；SDK10.0.401
Release 构建通过，零警告/错误。主监督者确认九个已记录启动身份均消失；这仅核对
已记录根进程。Ubuntu 的 YAML 6.0.1 解析实际工作流，拒绝重复 mapping
和 alias。tiny1 和固定8项工具控制核对新增依赖、保留字节上传、隐藏文件、原始下载
路径、失败传播和原生宿主矩阵；七份独立修改的工作流副本均被拒绝。六份替换表达式后的
PowerShell 代码块通过 AST 解析，未执行。这些是静态/工具检查，真实生成程序、
ILVerify、AOT 和正式 joint 控制的执行数均为零。

## 剩余闭环

🚧 进行中：提交并推送本次接线，执行新增原生 job，下载并核对原始报告及保留字节
哈希，再在该已推送 SHA 对账固定28/30/28/2/7案例和36阶段。保持两个继承的 Drop
差异，不批准任何新增语义差异。

正式 joint12、原始 legacy API/run 认证、独立策略授权来源和可信完整后代隔离仍是
独立开放要求。本次接线不生成 joint packet，不关闭任何 P1 门禁、P2 叶项、父项
或阶段。共享进程启动器仍报告 `parent-exit-only`。P2 仍为8/101 ✅ 已完成叶项。
