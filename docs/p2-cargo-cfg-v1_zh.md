[English](p2-cargo-cfg-v1.md) | [简体中文](p2-cargo-cfg-v1_zh.md)

# Cargo cfg v1

✅ 已完成：P2-04.04 的十二个固定 cfg 场景均在已集成的解析器和源码工作区上执行通过。P2 父项和阶段门禁仍未收口。

## 显式入口

```csharp
CargoCfgResolutionResult selected = CargoCfgResolver.ResolveV1(
    "Cargo.toml", new CargoCfgOptions { RuntimeIdentifier = "linux-x64" },
    new CargoFeatureOptions { Requests = [new CargoFeatureRequest("fast")] },
    cancellationToken: cancellationToken);
CargoCfgEnvironment environment = selected.EnvironmentFor("app@0.1.0");
SafeCoreWorkspaceResult sources = SafeCoreWorkspace.LoadWithCfgV1(
    selected.Targets[0].SourcePath, environment,
    cancellationToken: cancellationToken);
```

`CargoCfgResolver.ResolveV1` 选择严格本地 `cargo-v1` 包及根目标。`CargoCfgSourceSelector.Select` 在生产绑定前投影源码字符串。`SafeCoreWorkspace.LoadWithCfgV1` 在发现外部模块前对每个真实源码文件应用相同投影。既有 `CargoWorkspace.Load`、`GenericPackageWorkspace.Load` 和 `SafeCoreWorkspace.Load` 保留版本化行为。将选定的多包源码接入锁定消费者发射仍属于 P2-04.05/P2-04.06。

成功结果暴露选定根包、激活 feature/依赖图、根目标及每包环境。失败结果只包含诊断，feature 图/根包/平台为空，目标数组为空。源码失败结果暴露空投影字符串。取消传播 `OperationCanceledException`。显式 `TargetName` 只返回该目标；选择不存在或 feature 未启用的二进制目标时报告 `RSCARGO1009`。

## 冻结谓词与包最终化

| 谓词 | 含义 |
| --- | --- |
| `windows`、`unix` | 分别为 Windows 和 Linux 标记。 |
| `target_os = "windows"`、`target_os = "linux"` | 精确匹配获准操作系统。 |
| `target_arch = "x86_64"` | 两种获准 RID 的架构。 |
| `feature = "name"` | 判断所属包最终合并 feature 是否包含名称。 |
| `all(...)`、`any(...)`、`not(...)` | 全部/任一谓词；not 恰好接收一个。空 all 为 true，空 any 为 false。 |

仅接受 `win-x64/windows/x86_64` 和 `linux-x64/linux/x86_64`。显式 OS/架构矛盾值及不支持的 RID 在包选择前失败。不支持的谓词名称/值、格式错误条件和非法参数数量报告 `RSCARGO1009`。解析在布尔短路求值前校验全部成员，未知谓词不能藏在 true/false 成员之后。合法但未激活的 feature 名称求值为 false。

严格包加载已用 `RSCARGO1003` 拒绝依赖循环。Feature 贡献只从依赖者传播到依赖。因此选择器采用序数稳定、依赖者优先的拓扑顺序：收齐全部入边 feature/默认贡献，完成该包本地闭包，求值其条件依赖表，再将选定边的请求 feature/默认 feature 贡献给下游。菱形图在条件选择前合并全部入边贡献。因此支持 `not(feature = "name")`，无需临时激活或重置预算。

可选依赖还要求隐式 feature、`dep:name` 或 `dependency/feature` 产生激活意图。条件边保留 path/package/version/features/optional/default-features 元数据。同一别名的多条声明在选中后独立贡献；导出 feature 引用按其声明目标校验。最终公开依赖身份去重并按序数排序。二进制目标要求的全部 feature 均须激活。

## 源码项与限制

源码 cfg 使用相同解析器/求值器和包环境。投影求值编译单元/内联模块前导属性、模块级项、关联项、结构体字段、枚举变体和函数参数。它将 cfg 属性及禁用声明屏蔽为空格，保留原始 UTF-16 长度及每个 CR/LF。被移除字段/变体/参数之后的逗号一并屏蔽。文档、注释、字符串字面量及启用代码保持原始内容。禁用外部模块在文件系统检查和打开前消失。原始源码映射文档及字节仍供诊断和 PDB 映射使用。

未知/格式错误源码 cfg 定位完整原始属性；清单 cfg 诊断定位原始带引号路径分量。TOML 转义解码不会改变该跨度。源码语法在投影前解析；禁用声明仍须使用获准语法，名称/类型绑定则仅看到投影后声明。

清单解析、feature 校验/最终化和 cfg 选择共享一份 20,000 次操作/十秒预算。源码投影有自己的显式有界调用；工作区入口在全部源码投影和模块加载间共享一份 Cargo 预算。既有源码工作区另保留文件/字节/模块限制。不声称消费者编译在独立 API 调用间共享持久实时预算。

冻结上限包括 cfg 深度 16、64 个包、每包 128 个 feature、全图 1,024 条原始 feature 边及图深度 32。谓词根深度为一：十五层 `not` 加一个标记通过；十六层 `not` 加一个标记以 `RSCARGO1004` 拒绝。队列、token、声明及屏蔽循环均有项目/源码边界和取消/期限检查。解析不会创建进程、网络请求、锁或可执行输出。

## 固定验证与集成

[冻结清单](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) 保留 78 个场景。[Cfg 运行映射](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-cfg-cases.json) 保留 P2-04.04 全部 12 个 ID、顺序及主要诊断，并通过 `P2CargoCfgTests.All` 注册。

```powershell
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "P2 cargo cfg" --timeout 30 --deadline 180
```

测试采用独占真实文件夹具，场景及独立清理期限均为二十秒，限制 512 次写入、1,024 个跟踪目录及深度 16。清理只删除跟踪文件及空目录；独占重新打开检查验证句柄释放。源码检查调用生产泛型/MIR 绑定器，覆盖禁用的未解析声明、嵌套字段/变体/关联项、启用非法声明及保持未打开的缺失外部模块。十二项场景分母保持不变。

候选 `b75bdf5d68bb8a3a50ee4aaaca9500bb1da6fa4a` 通过全新 SDK 10.0.401 Release 零警告/错误构建、单案例试运行、12/12 cfg 场景、11/11 feature 案例、38/38 清单案例及 3+5+2 旧版兼容性案例。Release 注册清册为 1,116 项；这些筛选检查不宣称完整测试集闭环。负向 feature 菱形图验证每个传入贡献均在 `not(feature)` 选择依赖之前完成汇合。Trait 关联项在泛型 profile 中检查；MIR 保留既有 trait 拒绝边界，筛选后的枚举变体则在 MIR 中检查。

`eng/Invoke-P2CargoCfgEvidence.ps1` 在接受固定分母前要求候选源码和保留的全新构建字节匹配。[叶报告](evidence/p2/P2-04.04.json)、[隔离执行](evidence/p2/P2-04.04.harness.json)和[归档哈希](evidence/p2/P2-04.04.archive.json)保留验收证据。全部 71 条隔离执行记录及所属启动器均退出；夹具根目录和快照索引已回收。初步失败保留于 `artifacts/p2-supervision/cfg-v1/` 和 `cfg-v2/`。仓库 SDK pin 保持原值；显式 10.0.401 driver 记录实际使用的已安装验证工具。
