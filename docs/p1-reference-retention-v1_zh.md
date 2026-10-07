# P1 验证器引用留存修复

[English](p1-reference-retention-v1.md) | 简体中文

## 交付

本修复为 ✅ 已完成：Windows 源码包验证器使用可取消的异步 64KiB 读取留存真实
引用字节。每批保留原有十秒截止和 512 路径上限；每个引用保留 512MiB 大小界限和
8193 次读取上限。核对长度及 EOF，不跨用例复用哈希。最终单调时钟检查可避免计时器
取消延迟时仍接受超过截止的结果。

## 验收

候选 `b404f0dd62cb0368159c319e67ba98e6542eb2c4`、tree
`11523f2ca44103bcb96e86ddc53a1b53b9eb6328` 绑定 694 个源码输入。
新鲜 SDK 10.0.401 Release 构建零警告/零错误。一项极小试跑、四项引用控制及十二项
既有源码包控制均通过。最终真实 Windows x64 producer 通过全部 19 个源码包、39 项
原始 PE ILVerify、19 个 CoreCLR 程序及 19 个 Native AOT 程序。严格物理制品验证器
对该平台报告返回 `Valid=true`、`ArtifactContentVerified=true` 和
`SatisfiesGate=true`。失败、阻塞和未执行均为零。

[验收归档](evidence/p1/reference-retention-v1-archive.json) 保留原始报告哈希、准确
源码绑定及验证器证明。物理报告保留于
`artifacts/p1-supervision/reference-retention-root-v3/` 和
`artifacts/p1-source-package/reference-retention-full-v3.json`。root 在退出后核对
100 个已记录 producer 身份及自有源码包/AOT 临时目录。此前构建失败和被替代的 v2
检查均保留。worker 独立读取最终构建及过滤控制；其最后审阅调用解析失败，因此 root
补全其余绑定、producer 和清理检查。未执行计时器延迟故障注入。

## 剩余闭环

🚧 进行中：新鲜同一推送 SHA 的 Windows/Linux CI、完整 1144 项测试、NativeV5
回执/joint 门禁集成及独立后代进程约束。此本地平台验证器不关闭六个 P1 阶段门禁或
任何 P2 叶项。P2 仍为 8/101 个 ✅ 已完成叶项。
