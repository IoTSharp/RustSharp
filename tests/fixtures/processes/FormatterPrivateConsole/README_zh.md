[English](README.md) | [简体中文](README_zh.md)

# Windows 私有控制台夹具

✅ 本组件已通过冻结实现字节的全新 SDK10.0.401 编译（零诊断）及一次真实无信号 tiny。三个自然退出码均为零，inner3/outer4 归零，所有清理/发布错误为空。证据：[便携验收](../../../../docs/evidence/p2/formatter-private-console-port-v1.json)。P2-08.02 仍为 🚧 进行中。历史 v9 结果单独保留。该 Windows x64 夹具保留实际原生 PID/FILETIME/命令/父进程身份、映像哈希、严格 inner1/2/3 与 outer1/2/3/4 Job 清单和完整清理。它不关闭59/91 formatter 案例、Windows CTRL+C 或实际 formatter 挂起文件 I/O。

root 在拥有的 Job 内以配置的 SDK10 csc 先编译固定名 Launcher.dll（Native.cs + Launcher.cs），再编译引用 Launcher.dll 的 Helper.dll（Helper.cs）。两个 csproj 同样描述源码拆分；项目构建必须保留仓库 warning/analyzer 规则。root 选择独立新建绝对 ArtifactRoot，将精确源码/Run.ps1/runtimeconfig 复制至其中以形成字节闭包，创建最终不可变 manifest 和绑定源码的编译报告。packageFiles 最多40个顶层真实文件，排除可变运行结果。嵌套 runtimeBinding 有 candidateSha/treeSha/dotnet/cli/cliSha256/buildReport/buildReportSha256/sourceHashes3/externalFiles13；candidate/tree 为调用方审查的40位 hex。生产构建报告必须实际在该候选成功，所有真实源码/二进制哈希必须匹配。执行不固化机器特定仓库、候选、artifact 或 conhost 路径；conhost 使用实际 OS SystemDirectory。

调用 Run.ps1 时传 -TaskRoot、-ReviewedManifestSha256、-ReviewedLiveHostIdentityPath 和 -ReviewedLiveHostIdentitySha256。仅支持 -Mode resume-no-signal。artifact root 必须存在、位于此 tracked 源码目录之外、不含 reparse 祖先且保留阶段名均为新建。实际执行的 Run.ps1 字节必须与冻结包匹配。新建 task-lease.json 关联精确 root/nonce/manifest/原生 owner 身份；controller/helper 在任何子工作前核对同一 lease 和实际 live owner。运行后代保持在核实的拥有 Job 中；上游系统祖先仍明确不完整。

实际 GetConsoleProcessList raw count/Win32 last error/member 数组在 allocation 前后及 cleanup 时落盘。分配前须为零/ERROR_INVALID_HANDLE6，分配后精确 helper1，live console 精确 helper+CLI2，最终 console 为 helper1。conhost 身份取自完整 Job 证据，绝不由 HWND owner 推断。创建 CLI 前 root 独立核 conhost 原生 image/hash/身份及 outer3；resume CLI 前核完整 inner3/outer4。passed 谓词拒绝任何 child CleanupErrors/PublicationErrors、Job Errors、非零自然退出、失败 accounting 或 handle close。fixture .lease 保存完整原生身份，仅删除精确拥有的 ordinary entries，未知条目保留。不允许 worker 安装、发信号或按进程名终止。