# P1 进程包装器取消修复

[English](p1-process-wrapper-v1.md) | 简体中文

## 交付

本修复为 ✅ 已完成：`eng/Invoke-BoundedProcess.ps1` 接受调用方的
`CancellationToken`，拒绝已取消的启动，在等待及输出排空时检查取消，并在清理
同时失败时保留原始错误。两个捕获流分别登记。台账在可用时记录 OS 创建时间、
完整参数及父进程身份；身份观察失败明确记为未核验。等待同时受有限检查次数和
单调时钟截止时间约束。

## 验收

Windows x64 的 PowerShell 7.6.6 通过单项试跑及全部六项固定控制（启动前取消、
运行中取消、正常退出、退出 23、超时、输出超限），还通过独立的单项故障试跑和
两项故障控制。独占持有进程台账文件使清理失败：退出 0 报告聚合错误；退出 23
保留原始错误并附带清理失败。既有八项发布契约控制也通过。这些是工具控制，
不是生成语言程序或原生平台闭环证据。

候选 `2e38438404e112b6afe3ae39d635cc5d670f55cc`、tree
`8ecfd1bcd19e004f5d3d503eb0a7b22bbe84d5bb` 绑定全部 693 个编译器输入。
[验收归档](evidence/p1/process-wrapper-v1-archive.json) 保留报告哈希和准确源码
哈希。首次失败试跑日志保留在 `artifacts/p1-supervision/process-wrapper-root-v1/`；
其标量数组测试错误在成功故障试跑之前已修正。没有修改 C# 源码。

独立审阅发现单个捕获异常被解包为标量的问题。最终源码明确保留数组，并重跑六项
及两项控制。本轮未注入单个捕获流故障；该分支只有静态审阅。root 对账最终 23 个
已记录的进程身份，均已不存在。

## 复现

在 Windows 上使用 PowerShell 7，提供全新的结果路径：

```powershell
./eng/Test-P1BoundedProcessCancellation.ps1 -WrapperPath "$PWD/eng/Invoke-BoundedProcess.ps1" -ResultPath "$PWD/artifacts/cancel-controls.json"
./eng/Test-P1BoundedProcessFailureRetention.ps1 -ResultPath "$PWD/artifacts/failure-controls.json"
```

六项控制运行器删除其独占临时沙箱。故障运行器把独占夹具、身份及捕获输出保留在
报告旁，作为审阅制品。两个运行器的子进程夹具都不再创建后代。

## 剩余闭环

🚧 进行中：独立后代 containment/回收、生产者层面的 token 传播、NativeV5 回执和
联合门禁集成，以及原生 Windows/Linux 证据。`CleanupComplete` 和
`RootProcessExited` 仅核验根进程退出；`CleanupVerification=parent-exit-only`
明确该限制。异步捕获会检测输出预算超限，但不保证磁盘写入的硬上限。本修复不
关闭任何 P1 阶段门禁或 P2 叶项。
