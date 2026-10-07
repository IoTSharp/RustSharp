# P1 NativeV5 回执生产器

[English](p1-native-receipt-producer-v1.md) | 简体中文

## 已接入实现

🚧 进行中：已接入 `eng/Invoke-P1NativeDropEvidence.ps1`，供后续经过认证的
`drop-native` Actions 作业使用。保留固定 28 项生成用例、30 项原始 PE 验证、28 个
AOT 程序、两个 callable 制品、七项 callable 用例及 36 个阶段记录。向共享进程包装器
传递调用方取消，并捕获准确打开的报告/PE 字节。报告读取与严格解析器统一为 32MiB
上限。读取、哈希、写入及 flush 在接受或发布结果前检查取消及同一十秒单调时钟
截止。独占回执发布保留既有目标，仅回收自身临时文件。

## 工具控制验收

固定工具控制为 ✅ 已完成：PowerShell 7.6.6 对从真实 producer 提取的函数通过一项
极小试跑及全部八项控制。核对捕获字节哈希、字节/路径拒绝、预先取消、打开流一致性、
显式收尾窗口、收尾截止拒绝及既有证明保留。合成字节及注入的 elapsed 时钟明确标为
工具输入，未执行原生子进程、ILVerify 或 AOT。

候选 `d53a3cf3352f21de325de8f07bc842d8e1dcfe84`、tree
`24f010b510e54105c891fc6d947a9a8082a75482` 绑定 698 个源码输入。
[工具验收归档](evidence/p1/native-receipt-producer-v1-archive.json) 记录源码/报告
哈希及自有沙箱/启动进程检查。准备既有独占输出目录和新结果路径后复现：

```powershell
./eng/Test-P1NativeDropReceiptUtilities.ps1 -ResultPath artifacts/native-receipt-controls/fixed.json
```

## 剩余验收

🚧 进行中：工作流作业/上传接线、原始 30 项验证器报告传输、原子传输发布、必需 joint
packet 及十二项真实证据拒绝控制，以及双平台新鲜同一 SHA 原生执行。不通过模拟
生产环境绕过认证检查。进程 `CleanupComplete` 仍仅证明父进程退出；独立后代进程
约束及最终 C# I/O 时钟接入仍开放。本次 producer 接入及工具控制均不关闭 P1 阶段
门禁或 P2 叶项。
