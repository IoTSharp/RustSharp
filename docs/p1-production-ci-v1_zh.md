[English](p1-production-ci-v1.md) | [简体中文](p1-production-ci-v1_zh.md)

# P1 原生生产 CI v1

🚧 进行中：`.github/workflows/p1-expanded.yml` 新增独立的 Windows x64 与 Linux x64 原生生产任务和实物聚合门禁。验收前必须完成同 SHA 的真实 CI 执行；P1 仍未闭环。

每个原生任务使用 SDK 10.0.401 构建并保留候选源码／构建绑定。同时安装 SDK 10.0.400，以满足普通工具还原时的仓库 pin。四个串行阶段先执行并实物验证全部 19 个源码包／39 个原始 PE，再执行并实物验证六个精确后端夹具／七个原始 PE。每个 host 必须关闭本机 12 个后端单元。报告、保留字节、验证器 stdout、原始 runner／dotnet 字节、12 个实现源码及所属进程记录按 run／attempt 专属产物名上传。

`eng/Test-P1ProductionCiGate.ps1` 要求可信上游任务成功、两个完整原生回执绑定同一候选／tree／run／attempt、候选源码字节一致且所有运输产物哈希匹配。它调用生产后端矩阵实物验证器，要求全部 24 个单元闭合。既有 suite gate 仍要求每平台原有七份报告及 15 项检查，最终生产门禁对它们取合取。保留 `fullP1Closure=false`，因为语言、完整测试及其余 P1 阶段义务仍需联合核对。

原生编排限制为 3,900 秒／四阶段，job 为 90 分钟。聚合验证限制为 480 秒／两个 host，job 为 30 分钟。每个长期子进程记录 PID、父进程、开始时间、参数、退出和清理结果，回收任务所属进程树；保留审阅产物。编排支持取消，搜索使用明确清单。

✅ 已完成：两个脚本通过 PowerShell 7 AST 检查。四项[本地拒绝控制](evidence/p1/production-ci-guards-v1.json)确认拒绝缺失 Actions 上下文、失败上游任务和缺失新鲜实物构建。四个所属子进程均已退出并回收。这些控制使用刻意不完整的本地上下文，不能证明原生 CI 成功；独立真实 CI 验收仍为 ⏳ 计划中。
