[English](formatter.md) | [中文](formatter_zh.md)

# 无损格式化器 — P2-08.02

🚧 进行中：已接入实现通过59/59固定用例、18/18既有控制及Windows/Ubuntu真实CLI各8/8；Windows定向CTRL+C及实际异步I/O中断仍开放；Linux运行时补证结果见下文。

## 固定范围

CLI 限制诊断修复在候选 `eb0598f39c3797e29b3c9d0332a179a03aeac9f1`、tree
`ee500132a72b60022f2c689ac74dc2038d67cdbc`（703 个源码输入）为 ✅ 已完成：
源码增长现报告 `RSTF0001` 并退出 1，避免未捕获异常。新鲜 Release 零警告、
零错误；固定 formatter 59/59 及 Windows 标准 CLI 8/8 通过。真实 Ubuntu 控制
通过源码增长、源码变化、定向 SIGINT 取消（退出 130）及共享托管调度截止检查
（退出 1）。独立自有 FIFO worker 控制通过外部终止截止检查；它不是正常 CLI
取消或 I/O 中断证明。六个 Linux CLI 身份及自有夹具均已不存在。
参见[运行时归档](../evidence/p2/P2-08.02-limit-v1-archive.json)。保留原退出 134
的竞争失败及未命中事件。Windows 定向 CTRL+C、实际异步 I/O 中断和阶段门禁
仍开放；P2-08.02 保持 🚧 进行中，`leafClosed=false`。

不可变的 `tools/RustSharp.Conformance/fixtures/p2-tooling-v1-manifest.json` 定义 49 个真实源码格式化用例（34 个接受、15 个拒绝）及 10 个格式化场景。格式化器分母为 **59**，整个工具契约分母仍为 **91**。不替换任何语料源码、预期、源码哈希或诊断。`P2-08.02-mapping.json` 记录全部冻结用例及精确契约哈希。

## API 与行为

```csharp
RustFormatter.Format(string source, string sourcePath,
    FormatterOptions? options = null,
    CancellationToken cancellationToken = default);
```

`FormatterResult` 包含 `Success`、`Changed`、`FormattedSource` 和 `Diagnostics`。格式化器使用真实 safe-core 解析器解析输入，布局其无损词法条目，重新解析输出，并验证词元种类/拼写、位于相同词元边界的注释/文档/BOM/shebang 精确拼写，以及不包含位置的类型化 AST 指纹。测试独立比较每个公开 AST 属性，保留原始字符串、节点类型、标志、子节点顺序和附着关系，仅忽略 `TextSpan` 值。

布局按花括号深度使用四个空格、生成 LF 分隔符，以及一个最终换行。字面量及注释文本绝不解码、裁剪或规范化。属性词元树负载作为不透明源码保留，因为 AST 暴露其精确 `ArgumentsText`；内部空白保持原样。保留现有标点的相邻关系，防止运算符和泛型分隔符悄然合并。AST 验证拒绝任何剩余的语义变化。

畸形或不支持的输入返回已有解析诊断和未改变的原文。布局/解析/时间/输出失败返回 `RSTF0001` 和未改变的原文。保留验证失败返回 `RSTF1001` 和未改变的原文。`CheckOnly` 遇格式差异返回 `RSTF1002`、`Success=false`、`Changed=true` 及原文；规范输入通过。调用方取消抛出 `OperationCanceledException`。

## CLI 与边界

```text
rsc fmt source.rs
rsc fmt directory
rsc fmt --check source.rs
rsc fmt --check directory
```

CLI 使用不会消耗 BOM 的严格 UTF-8 解码，拒绝无效编码或链接源码条目。确定性发现受 1,024 个文件、100,000 个目录条目、十秒和总输入/输出 16 MiB 限制。跳过生成构建目录 `.git`、`bin`、`obj` 和 `target`。每份文档受 1,000,000 个 UTF-16 输入及生成输出字符、250,000 个词元、500,000 个 trivia 条目、1,000,000 个布局条目和 16 MiB UTF-8 输出限制。词法分析、解析、布局及 AST 验证共享操作截止时间并响应取消。

所有文档必须通过后才能开始发布源码。检查模式绝不创建输出文件。普通模式准备以 GUID 命名的同目录临时文件，再次核对原始字节，并逐份原子重命名已验证文档。后续 I/O 失败或取消可能留下此前成功的逐文件替换；跨文件事务性属于 P2-08.06，此处不作该声明。实际读取逐块计数字节并共享截止时间，再次核对源码及祖先链接、原始字节、属性和 Unix 权限。原始 Unix 权限在暂存写入前设置，并在 flush 后恢复和核对。仅在 `CreateNew` 成功后登记精确临时文件归属。`finally` 内的清理具有独立的 20 秒调度预算，最多处理 1,024 个归属路径；在 `finally` 外聚合错误，同时保留此前的失败或取消。同步 OS 打开/元数据/flush/重命名/删除调用需要外部 worker 截止时间。

## 验收与证据

```text
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter P2-08.02 --timeout 30 --deadline 120
```

59 个单元覆盖全部真实语法结果和十个冻结场景：基本黄金输出、普通/文档/嵌套注释、字面量拼写、CRLF 边界、BOM/shebang、检查差异、幂等性和精确资源边界。预算单元还测试预取消、极小超时和 UTF-8 输出字节。检查模式 API 证据必须补充真实 CLI 进程退出码及保留的前后文件哈希。运行闭环要求 59/59 零跳过报告、编译程序集哈希及候选 SHA 绑定、CLI 成功写入、检查模式字节不变、畸形/不支持输入字节不变，以及本任务进程/临时文件清理。仅通过静态补丁检查不能完成该叶子任务。

补充 CLI 控制是对同一冻结义务的独立进程/文件证据，不增加或替换 59 个已注册单元中的任何一个。控制脚本最多调度十个控制：Windows 上八个，Unix 默认七个，或启用可执行权限验证时 Unix 上八个。覆盖检查差异、无效 UTF-8、畸形/不支持输入、发布前目录拒绝、Windows 只读写入失败、BOM 格式化、规范检查及可选真实 Unix 权限。它不证明运行时取消/截止时间强制执行或源码增长竞争；闭环前仍需这些控制。最终候选 `103d9a5b952cf852468dab511b244e67193895ae` 绑定703输入，SDK 10.0.401 Release零警告/零错误。参见[部分验收](../evidence/p2/P2-08.02-progress.json)；不关闭叶项，未执行完整1203项测试。
