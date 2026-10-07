[English](p2-cargo-features-v1.md) | [简体中文](p2-cargo-features-v1_zh.md)

# Cargo features v1

✅ 已完成：P2-04.03 在 Windows x64 上通过全部 11 个固定 feature 场景。P2-04 和 P2 阶段保持 🚧 进行中。

## 入口与输出

`CargoFeatureResolver.ResolveV1` 仅加载严格的 `cargo-v1` 清单。读取、解析、校验和 feature 展开共享一份规范化的 `CargoLoadBudget`。`CargoWorkspace.Load` 保留既有源码链接方言；本 API 不接收该图，也不会回退到该方言。

```csharp
CargoFeatureResolutionResult result = CargoFeatureResolver.ResolveV1(
    "Cargo.toml",
    new CargoFeatureOptions
    {
        NoDefaultFeatures = true,
        Requests = [new CargoFeatureRequest("fast")],
    },
    cancellationToken: cancellationToken);
```

成功结果包含严格工作区、选定根包、排序后的激活包身份、feature 名称及激活依赖边。边记录保留依赖别名与目标身份。`OperationsConsumed` 表示共享预算消耗。失败结果只包含诊断与空的激活/边数组，工作区及根包均为空。取消会传播 `OperationCanceledException`。

包根默认选择自身包。虚拟根必须指定 `RootPackageIdentity`，例如 `library@0.1.0`。显式选择必须是工作区根或成员的精确身份；传递依赖不能作为工作区成员被选择。

## Feature 语义

| 输入 | 含义 |
| --- | --- |
| 本地 feature 名称 | 激活该显式或隐式 feature 及其闭包。 |
| `dep:name` | 激活指定的可选本地依赖。 |
| `dependency/feature` | 激活该依赖边及其导出的 feature。 |
| `dependency?/feature` 或其他表达式 | 以 `RSCARGO1008` 拒绝。 |

根包存在 `default` 时默认激活，除非 `NoDefaultFeatures` 为 true。显式请求 `default` 仍会激活它。每条激活依赖边独立贡献声明的 `features`，以及启用且存在时的依赖包 `default` feature。某条边的默认标记为 false 不会删除其他边的贡献。禁用根包默认 feature 不会禁用依赖默认 feature。

非可选依赖随所属包激活。可选依赖保持未激活，直到隐式同名 feature、`dep:name` 或 `dependency/feature` 激活其依赖边。包内任一 feature 定义出现显式 `dep:name` 时，就会抑制该依赖的隐式同名 feature，即便包含它的 feature 未激活。同名显式 feature 定义优先于隐式定义。

Feature 合并采用精确的 `name@version` 包身份。空 feature 数组合法，其激活结果会保留给后续 cfg 选择。输出使用序数排序，不受依赖声明顺序影响。所有定义和依赖 feature 请求均校验，包括未激活定义；未知名称、别名、导出及循环均在成功前拒绝。迭代 DFS 检测 feature 循环。真正跨包循环会更早作为包循环以 `RSCARGO1003` 失败；依赖 feature 展开进入下游本地循环时报告 `RSCARGO1008`。

## 限制与诊断

冻结上限保持为 64 个包、1,000,000 清单字节、每包 64 条依赖、每包 32 个目标、每包 128 个显式/隐式 feature、1,024 条原始 feature 边、图深度 32、20,000 次操作以及整个解析十秒。调用方更低的限制仍然生效。Feature 请求数不超过配置的 feature 边数量。表达式受清单输入上限约束。队列和 DFS 处理使用明确的操作次数边界，并检查相同取消/期限预算。

解析器的 1,024 条原始边上限作用于整个加载图，包括 feature 数组成员、生成的可选依赖隐式 feature 引用及依赖声明的 feature 请求。重复源码引用在激活去重前消耗该上限。默认贡献及工作队列步骤消耗共享操作预算。清单加载器另外保留既有的每包元数据检查。

未知或不支持的 feature 表达式、feature 循环产生 `RSCARGO1008`；预算耗尽产生 `RSCARGO1004`。清单诊断保留既有代码与优先级。每个 feature 数组成员和依赖 feature 请求均保留原始带引号 token 的跨度，包括 CRLF/Unicode 偏移。选项请求可以提供 `SourcePath` 和 `Span`；缺省来源使用根清单和零跨度。

条件依赖表需要 P2-04.04 的 cfg 选择阶段，目前以 `RSCARGO1005` 明确拒绝。本阶段不选择源码 cfg 项、目标、required-features 或 RID，也不写锁文件或编译消费者。

## 验证与集成

[不可变清单](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) 保留 78 个场景。[Feature 运行场景映射](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-feature-cases.json) 将恰好 11 项 P2-04.03 注册绑定到 `P2CargoFeatureTests.All`。

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter "P2 cargo features" --timeout 30 --deadline 180
```

测试创建独占的真实文件临时夹具，在二十秒场景期限内限制为 512 次写入、1,024 个目录和深度 16。清理仅删除已跟踪文件及空目录，并使用独立的二十秒期限。独占重新打开检查验证清单句柄已释放。测试包含 128/129 个 feature、1,024/1,025 条原始引用、精确操作次数、极短期限及调用方取消控制。解析不会创建构建、还原、网络、子进程或持久输出。

模型／解析器、共享加载预算和源码位置已接入 `src/RustSharp.Compiler`，`P2CargoFeatureTests.All` 已在 `Program.cs` 注册。`eng/Invoke-P2CargoFeatureEvidence.ps1` 要求真实匹配的源码候选和新鲜保留的 Release 构建，才允许关闭固定 11 场景分母。候选 `8a250d1bd1baf58bb75242e6d85172bed7fc01e4` 通过 SDK 10.0.401 的零 warning／error 构建、单场景试跑、11/11 feature 场景、38/38 清单场景和既有 5+3+2 包兼容性场景。Release 清册有 1,104 项注册；过滤验证不代表完整测试闭环。[叶子报告](evidence/p2/P2-04.03.json)、[隔离执行](evidence/p2/P2-04.03.harness.json)与[归档哈希](evidence/p2/P2-04.03.archive.json)保留证据。边预算场景另行确认：两个包共 512+512 条引用成功，600+600 条拒绝，且每包均低于 loader 元数据上限。所有所属启动器和隔离子进程均退出并完成回收，夹具目录与快照临时索引已删除。初步构建失败报告保留于 `artifacts/p2-supervision/features-v1/` 和 `features-v2/`，供审阅使用。
