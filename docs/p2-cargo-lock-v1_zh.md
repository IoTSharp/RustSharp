[English](p2-cargo-lock-v1.md) | [简体中文](p2-cargo-lock-v1_zh.md)

# Cargo 锁定 v1

✅ 已完成：P2-04.05 通过全部十个冻结文件系统场景，P2-04.03 与 P2-04.04 已验收。P2-04 与 P2 阶段保持 🚧 进行中。

## 入口与模式

`CargoLockResolver.ResolveV1` 加载严格 `cargo-v1` 清单，解析真实路径依赖、统一 features、RID/cfg 与根目标。一个规范化 `CargoLoadBudget` 覆盖清单加载、cfg/feature 解析、图排序、序列化与锁校验。旧 Cargo 加载保留原有方言。该 API 不编译或运行消费者；源码到程序集的集成属于 P2-04.06。

```csharp
CargoLockResolutionResult result = CargoLockResolver.ResolveV1(
    "Cargo.toml",
    new CargoCfgOptions { RuntimeIdentifier = "win-x64" },
    new CargoFeatureOptions { Requests = [new("extra")] },
    new CargoLockOptions { Mode = CargoLockMode.Locked },
    cancellationToken: cancellationToken);
```

| 模式 | 行为 |
| --- | --- |
| `Plan` | 解析并返回已校验字节/顺序，不读写锁文件。 |
| `Locked` | 要求匹配的 `Cargo.lock` 与 `Cargo.lock.rustsharp.json`；只读，绝不修复或重写输入。 |
| `Update` | 校验完整图与两份输出，再暂存并替换文件对。 |

默认模式为 `Plan`。`Locked` 是后续 `--locked` 命令路由的编译器 API 边界；本叶项不声称 P2-07 CLI 分派已经存在。根选择遵循 feature/cfg 解析，包括显式虚拟工作区根身份。失败返回诊断、空计划且不声称成功写入。调用方取消传播 `OperationCanceledException`。

## 可移植图与依赖顺序

`Cargo.lock` 是 TOML `version = 4`，其 `[[package]]` 表仅含 `name`、`version` 和 `dependencies`。它记录全部发现的包与全部声明的路径依赖，包括未激活的 optional 与 cfg 依赖。激活状态与图身份分开。依赖标识使用真实包名、一个空格和精确版本；别名保留在解析元数据中。多个别名指向同一包时，锁文件只保留一个依赖标识。

序列化使用无 BOM UTF-8、LF、ordinal 包身份与 ordinal 依赖标识。显式输出空依赖数组，不含文件系统路径、registry source 或 checksum。重排声明、工作区成员与依赖输入会产生相同锁与元数据字节；将相同可移植图迁移至另一独占目录亦然。锁解析器接受冻结的单物理行 TOML 子集、注释、空白与首部 BOM；语义图匹配的输入在 `Locked` 模式下保持原样。未知表/键、source/checksum、格式错误、重复身份/边与不兼容身份均拒绝。

`DependencyOrder` 包含全部发现图。迭代拓扑遍历先输出依赖，再输出消费者；当前可就绪节点中最小的 ordinal `name@version` 优先。别名去重不能改变顺序。自环与多节点路径环都会拒绝，即便相关清单此前已被发现。结果不暴露部分成功计划。

## 解析元数据

`Cargo.lock.rustsharp.json` 是规范化 UTF-8/LF 伴随文件。schema version 1 包含大写 SHA-256 `activationFingerprint` 与确定性的 `resolution` 对象。指纹覆盖该对象精确 UTF-8 字节。内容包括 `cargo-v1`、RID、目标 OS/架构、所选根身份、已排序的激活包 feature 列表、已排序的激活依赖 owner/alias/target 记录与所选目标 kind/name 记录。路径与时间戳不进入可移植身份。

锁定模式比较已解析图与当前规范图字节，并要求伴随文件的精确规范化字节。缺失锁/伴随文件、包版本/依赖变更、所选 features、根/目标、RID/cfg 激活、不兼容 schema 与被修改指纹均以 `RSCARGO1010` 拒绝，且不修改任一文件。最终激活结果决定元数据，而非请求顺序或冗余请求拼写。

## 写入、限制与诊断

Update 模式仅在完整计划校验后独占创建 `.rustsharp-lock.write.guard`。它读取有界原有字节，在目标目录暂存两个唯一文件、flush，并在固定两文件提交之前核查取消/截止时间。每次替换使用同目录原子 rename。提交与回滚各有固定两次迭代与独立十秒期限。普通提交失败时，已替换文件回滚至保留的原字节。文件对并非单个文件系统原子事务；与提交重叠的读取可能拒绝瞬时不匹配，本叶项不承诺进程终止后的崩溃恢复。

guard 与最多两个暂存文件有明确归属，以及独立十秒清理期限。仅删除成功创建的独占名称；已有 guard 与其他文件保留。回滚/清理失败返回 `CleanupComplete = false`，不能通过本叶项门禁。锁路径或祖先中的文件系统链接拒绝。成功更新返回 `FilesWritten = true`；锁定/计划模式返回 false。

冻结上限保持 64 包、每包 64 依赖、图深度 32、20,000 操作、十秒以及全部继承的清单/feature/cfg 限制。`MaximumLockBytes` 默认且最高为 1,000,000 字节，分别约束图锁与伴随文件；调用方更低值有效。生成的两份输出在写入前校验。现有输入长度在 UTF-8 解码或 TOML 解析前校验。全部图/读取迭代都有固定项目上限并检查共享取消/截止时间预算。最后取消检查后，固定两文件提交与有界回滚不再中断。

锁不兼容/格式错误使用 `RSCARGO1010`；图环保留 `RSCARGO1003`；字节/操作/期限耗尽使用 `RSCARGO1004`。清单、feature 与 cfg 错误保留原诊断代码和源码跨度。锁 TOML 错误保留原始锁 token 跨度，包括 CRLF 偏移。解析不会创建子进程、build、restore 或网络请求。

## 固定验收与集成

[不可变契约](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) 保持 78 条记录。[叶项 case map](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-lock-cases.json) 按原顺序将十个冻结 P2-04.05 ID 映射到 `P2CargoLockTests.All`，预期诊断完全一致。

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter "P2 cargo lock" --timeout 30 --deadline 180
```

测试使用唯一真实文件系统夹具，限制 128 次写入、130 个跟踪文件、256 个目录、深度 16 与二十秒场景期限。清理使用独立二十秒期限，仅删除已核实独占根内的跟踪文件和空目录。断言精确锁哈希、锁定文件时间不变、文件句柄释放以及独占暂存/guard 不存在；测试已有 guard 保全与第二次替换失败的回滚。场景覆盖图/别名序列化、未激活节点发现、重排/迁移图、精确锁文件对、缺失/过期/不支持/重复/不兼容锁、真实环、精确/越界字节、无效 UTF-8、期限/操作预算与调用方取消。

已验收源码候选为 `064f5c54600b5a6b0c7feaafd564b2cdf2fb8421`（tree `88a7dcc0bf94b728dc9d788e2189424d40594844`）。新鲜 .NET SDK 10.0.401 Release 构建零警告/零错误，共 1,126 个注册项；注册与实际执行场景分开。win-x64 上 PowerShell 7.6.6 实际执行锁场景 10/10、cfg 12/12、features 11/11、清单 38/38 及旧场景 3+5+2，无失败、跳过或未执行必需场景。[叶项报告](evidence/p2/P2-04.05.json)、[harness](evidence/p2/P2-04.05.harness.json)及[归档索引](evidence/p2/P2-04.05.archive.json)保留精确候选、清单、工具、进程与哈希绑定。物理报告和新鲜构建输入保留于 `artifacts/p2-supervision/lock-v3/`。所属进程树均已退出；真实夹具测试验证暂存/guard 清理并保全已有对象。这些 API/文件系统结果不关闭后续消费者编译、CLI 路由或任一平台门禁。
