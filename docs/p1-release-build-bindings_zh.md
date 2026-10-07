# P1 Release 构建源码与产物绑定

[English](p1-release-build-bindings.md) | 简体中文

✅ 已完成：候选 `dfdd76155934e286b85979a28b053ce8ffc10547` 的新鲜源码与保留产物绑定检查通过，使用 SDK `10.0.401`，涵盖 662 个编译输入与 1,093 个注册项。Release 构建零警告、零错误，九项绑定检查全部通过。本修复不关闭 P1/P2 阶段门禁。

`eng/Invoke-P1ReleaseBuild.ps1` 在 SDK 探测之前，以及 restore、Release 构建和新鲜测试注册之后，核验实际候选 Git 提交/树与编译输入字节。两份原始快照报告均通过 `sourceSnapshots` 保留哈希绑定。两份快照必须描述相同输入；仅有候选标签不能验证构建。

报告在其 `build-artifacts` 目录保留 `rsc.dll`、`RustSharp.Tests.dll` 和六个固定实现依赖（`RustSharp.Compiler`、`RustSharp.Conformance`、`RustSharp.Runtime`、`RustSharp.Semantics`、`RustSharp.CodeGen.IL`、`RustSharp.Syntax`）。新鲜路径与 SHA-256 继续记录；`compilerArtifactPath`、`testsAssemblyArtifactPath` 和各依赖的 `artifactPath` 标识保留的原始字节。双平台聚合因而能够验证下载产物，无需用聚合宿主的二进制替代。

传入仓库根目录的 `Test-P1BuildEvidence` 要求实际保留文件、规范路径、无文件系统链接、匹配哈希、时间顺序正确的两份实际 Git 快照、精确依赖清册以及所选 SDK/解决方案构建命令。历史 schema 1 结构检查仍可读取；缺少这些绑定时不能通过物理验证。

生成器有六个有界编排步骤和 715 秒期限。每个 DLL 限制为 32 MiB。快照验证最多允许 24 个自有 Git 子进程和 110 秒，覆盖双平台聚合的六份快照。每个子进程保留 PID、启动时间、命令、父进程和清理证据。保留文件属于审查产物，不是可丢弃的临时文件。

针对新鲜报告及其实际候选，先运行最小控制，再运行九项检查：

```powershell
pwsh -NoProfile -File eng/Test-P1ReleaseBuildBindings.ps1 -ReportPath artifacts/p1-supervision/fresh/release-build.json -CandidateSha <SHA> -MaximumChecks 1 -DeadlineSeconds 30
pwsh -NoProfile -File eng/Test-P1ReleaseBuildBindings.ps1 -ReportPath artifacts/p1-supervision/fresh/release-build.json -CandidateSha <SHA> -MaximumChecks 9 -DeadlineSeconds 120
```

八项拒绝控制仅在内存中改变报告对象，覆盖过期可执行字节、缺失/重复依赖、缺失/倒序快照、过期快照字节、伪造候选和替换构建命令。每项独立控制均在捕获输出中保留进程记录；不会修改编译输出，也不声称平台执行。

证据：[Release 报告](evidence/p1/release-build-bindings.release.json)、[九项控制](evidence/p1/release-build-bindings.controls.log)、[所属进程清理](evidence/p1/release-build-bindings.process.json)及[原始／归档哈希](evidence/p1/release-build-bindings.archive.json)。原始快照与八个保留 DLL 位于 `artifacts/p1-supervision/continuation-v3/`；控制检查只在内存中突变报告对象。
