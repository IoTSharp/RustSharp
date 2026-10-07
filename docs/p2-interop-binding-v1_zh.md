# P2-06.02：显式 .NET 导入元数据绑定

[English](p2-interop-binding-v1.md) | 简体中文

状态：✅ 已完成声明语法和锁定元数据绑定。父配置档 `dotnet-interop-v1` 保留
36 个用例，本叶子恰好保留 14 个。规范化契约 SHA-256 为
`AFA13CE319C44DB2E0A9BF6F81DE65D8929656AFEC20AF241C6AB03D5B689C56`。

## API 与语法

`DotNetImportBinding.Bind(source, sourcePath, references, cancellationToken)` 和
`CompilerDriver.CheckDotNetImports` 使用生产 Rust lexer 解析冻结的属性/extern
语法。每个声明必须具有 assembly、type、member 和精确 signature 标识。
重复别名、歧义重载、不可访问成员及不支持签名返回带源码路径和范围的诊断，
不暴露部分绑定。该入口接收声明；全程序检查及生成导入调用属于 P2-06.03。

## 锁定元数据与资源限额

`DotNetReferenceLock` 锁定路径、名称、版本和 SHA-256。绑定读取实际 PE 元数据，
不加载引用中的代码。结果保留定义程序集、引用 hash、MVID、方法 token、闭合签名
和泛型实参。TypeRef scope 及真实 forwarder 必须匹配锁定的名称、版本、culture、
content flags 和 public key/token。BCL 类型解析到锁定 CoreLib；检查公开可见性、
真实基类身份和 CLASS/VALUETYPE 形态。SHA-1 仅用于 ECMA-335 strong-name token；
PE 完整性使用 SHA-256。

限额为 32 个引用、每引用 16777216 字节、256 个候选方法、16 个泛型实参及
256 个参数。源码不超过 262144 UTF-8 字节、65536 个 token/trivia 和 32 层
delimiter/type 递归。解析、读取及元数据遍历共享 200000 操作和十秒预算。
读取携带调用者及期限取消，检查真实字节数/增长，并在开流后复核父路径链接。
同步 OS 开流、属性读取及删除在文件系统无响应时依赖外围进程超时提供硬期限。
验收使用隔离进程并记录其清理。

## 已验收证据

最终候选 `29a8edaa6d63f1b7601159dedbe04decbeef989a`，tree
`f4709c042009cf8da47a837dbfdb0014519f05ce`，新鲜 SDK 10.0.401 Release
零警告/零错误，单例试跑、全部 14 个绑定注册及全部 8 个继承契约检查均通过。
注册总数 1140 不代表完整 harness 已执行。报告、源码/构建 hash、保留的独立
`InteropFixtures.dll`、运行时引用 hash 和十个已退出进程台账位于
`artifacts/p2-supervision/interop-v4/`。
[归档](evidence/p2/P2-06.02-archive.json) 记录原件与规范化报告 hash；
[绑定报告](evidence/p2/P2-06.02.harness.json) 记录真实用例执行。
此前失败构建及修复前通过记录均保留。

固定测试覆盖全部 14 个映射用例、真实 BCL 转发、Counter Read/Release 适配器、
泛型约束及独立生成的负向 PE scope/shape。临时 PE 文件通过 CreateNew 确立归属
并验证清理；清理失败保留原始错误。合成 NuGet adapter PE 仅检查签名绑定。
继续明确保持 `RuntimeEvidence=false`。

## 父任务剩余义务

P2-06 仍为 🚧 进行中。生成导入调用及 ownership/null/error/release 执行属于
P2-06.03；普通消费者导出属于 P2-06.04；真实固定 NuGet 还原/调用及 AOT
可达性属于 P2-06.05；Windows/Linux CoreCLR、原始 PE ILVerify 和 Native AOT
属于 P2-06.06。这些义务和 P1-GATE 仍全部开放。
