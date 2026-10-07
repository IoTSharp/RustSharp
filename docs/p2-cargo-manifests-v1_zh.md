[English](p2-cargo-manifests-v1.md) | [简体中文](p2-cargo-manifests-v1_zh.md)

# Cargo manifest 加载：显式版本边界

P2-04.02 实现严格的 `cargo-v1` manifest 与本地包发现契约。既有源代码编译继续使用原有的源链接方言。调用方通过 API 名称选择方言；验证失败时不会在两种方言之间回退。

## 入口

| API | 方言 | 行为 |
| --- | --- | --- |
| `CargoWorkspace.Load` | 旧版源链接 | 保留隐式依赖别名、包目录之外共享的相对库路径、原有单一主目标行为，以及原有的未知元数据处理方式。 |
| `CargoWorkspace.LoadV1` | `cargo-v1` | 执行冻结的键和值清单、重命名依赖的显式包身份、源路径包含检查、文件系统链接拒绝、精确版本、全部目标和确定性包发现。 |

两种方法都保留 `string manifestPath`、可选的 `CargoWorkspaceOptions` 和末尾的 `CancellationToken` 参数，因此既有调用方保留其 API 与行为。新的 P2 manifest 测试显式调用 `LoadV1`。

```csharp
// 既有源链接调用方保持兼容。
CargoWorkspaceResult legacy = CargoWorkspace.Load(manifestPath,
    cancellationToken: cancellationToken);

// 显式选择封闭的 cargo-v1 契约。
CargoWorkspaceResult strict = CargoWorkspace.LoadV1(manifestPath,
    cancellationToken: cancellationToken);
```

如果依赖别名是 `dep`，其 manifest 声明的包名是 `generic-lib`，旧版 `Load` 接受 `dep = { path = "../library" }`。`LoadV1` 则要求 `dep = { path = "../library", package = "generic-lib" }`。库的 `path = "../shared.rs"` 通过 `Load` 仍然有效，而通过 `LoadV1` 会被拒绝并报告 `RSCARGO1005`。这些兼容输入在既有 38 个 P2 场景内验证，并与未修改的 P1 泛型包继承测试共同形成回归覆盖。

## 严格 manifest 契约

不可变的 [cargo-v1 清单](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) 包含分布于 P2-04.02 至 P2-04.06 的 78 个用例。[P2-04.02 运行时用例映射](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifests-cases.json) 将恰好 38 个独立注册的测试绑定到 manifest、工作区、目标、依赖、诊断和预算场景。清单验证与运行时执行分开记录。

`LoadV1` 解析严格 UTF-8，允许单个前导 BOM、单行基本/字面字符串、布尔值、字符串数组、允许的表、多个 `[[bin]]` 表和扁平依赖表。格式错误或重复声明会在原始字符跨度处报告诊断。不支持的多行值、点号赋值、未知键/表和不支持的依赖来源会明确失败。

包根、虚拟工作区根和组合根都有明确表示，不会凭空创建包。`RootManifestPath` 标识请求的 manifest；虚拟根的 `RootPackageOrNull` 为 null，`IsVirtualWorkspace` 描述该根；调用方试图把虚拟根当作包使用时，`RootPackage` 会抛出异常。成功的空虚拟工作区属于有效元数据。编译调用方必须先为虚拟工作区选择真实包。

旧版 `Load` 要求真实根包。只有工作区声明的 manifest 会返回 `RSCARGO1005` 且不提供包图；调用方可以显式使用 `LoadV1` 检查工作区元数据。既有编译与 CLI 入口因此仍会明确拒绝该输入，不会在方言之间回退。

精确成员/排除项按照实际平台归一化并去重；成员按 manifest 路径的序数顺序排列。包按名称/版本/manifest 路径的序数顺序排列。库先于二进制目标，二进制目标按名称排序。`SourcePath` 保留既有优先级：显式库路径、按名称排序的首个显式二进制目标、推断的 `src/main.rs`，然后是推断的 `src/lib.rs`。

依赖的 `features`、`optional`、`default-features`、目标 cfg 表达式、包 feature 数组和二进制目标的 `required-features` 均保留为元数据。P2-04.02 尚未实现 feature 激活、cfg 选择、lock 持久化或工作区生成程序；这些内容属于 P2-04.03 至 P2-04.06。

## 边界与失败

| 边界 | `Load` 上限 | `LoadV1` 上限 |
| --- | --- | --- |
| 包数量 | 256 | 64 |
| 每份 manifest 字节数 | 4,000,000 | 1,000,000 |
| 操作数 | 100,000 | 20,000 |
| 图深度 | 256 | 32 |
| 整次加载期限 | 10 秒 | 10 秒 |

两个入口均默认最多 64 个包、每份 manifest 1,000,000 字节和 20,000 次操作。`LoadV1` 还限制每包最多 64 条依赖边、32 个目标、128 个 feature 和 1,024 条 feature 元数据边。其 cfg 深度选项保留给后续选择实现。更小的正数期限和工作预算用于边界测试。

加载器执行有界文件读取，在分配内存之前及读取之后检查输入长度，释放每个输入句柄，并且不创建进程、网络请求或输出文件。调用方取消时传播 `OperationCanceledException`；预算和期限失败使用 `RSCARGO1004`；失败时不返回可用的部分包图或根包。

## 验证

完成 Release 构建后，分别运行严格 manifest 场景和继承的兼容回归：

```powershell
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "P2 cargo manifests" --timeout 30 --deadline 180
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "generic package" --timeout 60 --deadline 300
```

严格场景的分母保持 38。路径场景创建真实文件/目录/manifest 链接并验证拒绝行为；平台缺少链接权限时不会跳过该必需场景。所有 fixture 写入、清理操作和搜索迭代均受数量与墙钟时间边界约束。

✅ 已完成：候选 `dfdd76155934e286b85979a28b053ce8ffc10547` 通过全部 38 个隔离的真实文件系统场景，以及 5 项泛型包、3 项旧工作区和 2 项包输入兼容测试。新鲜 Release 构建零警告、零错误，使用 SDK `10.0.401` 绑定 662 个源码输入。[叶任务报告](evidence/p2/P2-04.02.json)、[隔离执行](evidence/p2/P2-04.02.harness.json)与[原始／归档哈希](evidence/p2/P2-04.02.archive.json)保留验收证据。P2 阶段门禁仍未关闭。
