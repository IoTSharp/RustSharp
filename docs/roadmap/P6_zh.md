# P6：平台、原生库与分发的可验收任务

[English](P6.md) | 简体中文 | [返回路线图](../../ROADMAP_zh.md)

本清单保留原有 P6 范围。所有叶子均为 ⏳ 计划中；具名目录、配置档、运行器、命令接口和报告都是计划交付物，不是已有实现或执行证据。每个父任务要求全部叶子通过；`P6-GATE` 是 P6-01 至 P6-08 与四个门禁叶子的汇总。P6-08 属于 1.0 退出集合，它依赖适用 P4/P5 门禁和 P6-02 至 P6-07，**不得反向依赖 P6-GATE**。普通叶子只通过列出的边继承前置依赖，不以本阶段门禁作为开工前提。

父 ID 指对应全部叶子，阶段 `P2-GATE` 等指完整阶段汇总。表内文件/目录限定修改所有权；共享发布、ABI 或 CI 接口的修改应串行集成。版本和范围决策先冻结 API/ABI、RID/OS/工具链与数值阈值，后实现固定清单和运行器；未来扩展新建版本，不通过缩减已有分母、临时排除失败平台或放宽预算结项。

证据键 `suite-v1#group` 指计划的 `tests/roadmap/suite-v1.json` 与具名分组；清单逐项固定用例 ID、输入哈希、预期结果、正/负/边界/预算分类及精确分母。计划在 `tools/RustSharp.Conformance` 增加 `--manifest <path> --group <group>`，此未来入口不能冒充当前可执行命令。运行器实现归属对应表内单元并由一致性入口分派。本地报告计划在 `artifacts/p6/local/<suite>/<group>.json`，CI 上传同结构报告并提供稳定运行/制品链接；忽略目录中的报告无需强制提交。

每份报告记录最终 SHA、工具/配置档/ABI/依赖版本、锁定哈希、原生宿主架构与 RID、命令、分母、失败原因、超时、最大尝试/退避、PID/父 PID/启动时间与清理结果。所有进程、轮询、样本和重试都有限且可取消，只清理确认归属的对象。本地与 CI 分别给出证据，真实原生执行不由交叉编译、模拟或其他架构结果替代。缺失运行器、签名服务或服务凭据属于明确阻塞，不是通过或跳过。每个功能叶子必须提供对应正向、负向、边界及预算用例。

<a id="p6-01"></a>

## P6-01: 三个独立兼容性契约

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-01.01 | ⏳ 计划中 | 冻结版本轴与变更分类；负责 `docs/adr/release-contract-versions.md`。 | P2-GATE, P3-05 | ADR 区分 Rust# 元数据、公共 .NET 与 C ABI 兼容性，列出支持的生产者/消费者版本对，并固定有限基线/夹具限制；不得以一个版本号推断三者均兼容。 | `p6-api-v1#policy` |
| P6-01.02 | ⏳ 计划中 | 元数据 schema 演进；负责 `src/RustSharp.CodeGen.IL/MetadataCompatibility.cs`、`tests/compatibility/metadata/`。 | P6-01.01 | 新旧生产者/消费者夹具接受已记录的可选扩展，并以稳定诊断拒绝必需字段删除、不兼容 MIR/配置档更改、损坏及超大元数据。 | `p6-api-v1#metadata` |
| P6-01.03 | ⏳ 计划中 | 公共 .NET API 基线；负责 `tests/compatibility/dotnet/`、`eng/api/DotNetBaseline.cs`。 | P6-01.01 | 真实消费者接受声明的新增成员，并拒绝签名、可见性、布局及调用契约破坏；基线输出确定，且受固定成员/程序集数量限制。 | `p6-api-v1#dotnet` |
| P6-01.04 | ⏳ 计划中 | C ABI 基线；负责 `tests/compatibility/c-abi/`、`eng/api/CAbiBaseline.cs`。 | P6-01.01 | 头文件/导出/布局/调用约定夹具区分每个支持的 ABI；兼容扩展通过，符号删除、偏移/宽度/所有权变化在消费者执行前失败。 | `p6-api-v1#c-abi` |
| P6-01.05 | ⏳ 计划中 | 对账独立兼容性决策；负责 `tests/compatibility/contracts/`。 | P6-01.02, P6-01.03, P6-01.04 | 每个轴的故意破坏性夹具仅在对应基线失败，有效扩展通过全部受影响消费者，缺失/重复基线或版本对条目不得被忽略。 | `p6-api-v1#reconcile` |

<a id="p6-02"></a>

## P6-02: 原生库与 C ABI 所有权

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-02.01 | ⏳ 计划中 | 冻结导出 ABI 配置档；负责 `docs/profiles/native-library-v1.json`。 | P6-01 | 固定精确导出、标量/布局类型、调用约定、RID、缓冲区所有权、错误码、回调/线程规则与数值资源限制；Rust ABI 和未列出的导出仍被拒绝。 | `p6-native-library-v1#profile` |
| P6-02.02 | ⏳ 计划中 | 发出原生导出与头文件；负责 `src/RustSharp.CodeGen.IL/NativeExports.cs`、`src/RustSharp.Compiler/NativeLibraryPublisher.cs`。 | P6-02.01 | 生成头文件、PE/ELF/Mach-O 导出表与降低后的参数/返回布局一致；重复导出、非法可见性/布局和不支持签名在原生链接前失败。 | `p6-native-library-v1#exports` |
| P6-02.03 | ⏳ 计划中 | 缓冲区分配与所有权；负责 `src/RustSharp.Runtime/Interop/ExportBuffers.cs`。 | P6-02.02 | 调用者/被调用者分配释放、null/空缓冲区、长度/容量边界及对齐遵循明确契约；非法句柄、重复释放和整数溢出受控处理，不损坏内存。 | `p6-native-library-v1#buffers` |
| P6-02.04 | ⏳ 计划中 | 错误与 panic 边界；负责 `src/RustSharp.Runtime/Interop/ExportErrors.cs`。 | P6-02.02 | 成功/错误载荷所有权与清理明确；托管异常不得跨 C 边界展开；panic/非法输入路径在有界子进程中产生冻结状态或文档声明的 abort。 | `p6-native-library-v1#errors` |
| P6-02.05 | ⏳ 计划中 | 回调与生命周期注册；负责 `src/RustSharp.Runtime/Interop/ExportCallbacks.cs`。 | P6-02.03, P6-02.04 | 注册/调用/注销、null 回调、重入及调用期间注销符合配置档；委托严格按需保持根，晚到回调安全失败且不泄漏注册。 | `p6-native-library-v1#callbacks` |
| P6-02.06 | ⏳ 计划中 | 线程与并发契约；负责 `src/RustSharp.Runtime/Interop/ExportThreads.cs`。 | P6-02.05 | 外部线程进入、线程亲和句柄、并发调用与关闭竞态遵循冻结规则；有界压力测试证明无释放后使用、死锁或线程局部错误串扰。 | `p6-native-library-v1#threads` |
| P6-02.07 | ⏳ 计划中 | 真实 C 与 C# 消费者；负责 `samples/c-abi/`、`tests/native-consumers/c/`、`csharp/`。 | P6-02.03, P6-02.04, P6-02.05, P6-02.06 | 两个消费者分别编译，加载生成的原生库，在每个声明 RID 上传递标量/结构体/缓冲区/错误并调用回调；能检测生成头文件不符。 | `p6-native-library-v1#consumers` |
| P6-02.08 | ⏳ 计划中 | 泄漏与发布失败恢复；负责 `tests/native-consumers/lifecycle/`。 | P6-02.07 | 重复加载/使用/卸载及取消/失败发布遵守句柄/分配/进程预算；报告证明恰好一次清理，并保留调用者自有文件/进程。 | `p6-native-library-v1#lifecycle` |

<a id="p6-03"></a>

## P6-03: Windows 与 Linux ARM64 运行器

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-03.01 | ⏳ 计划中 | 冻结 ARM64 原生运行器矩阵；负责 `docs/profiles/arm64-runners-v1.json`。 | P0-17, P6-01 | 固定原生 win-arm64/linux-arm64 的 OS、SDK、链接器、ABI/原生依赖版本、配置档清单及构建/运行数值预算；模拟器或 x64 宿主输出不能满足原生证据。 | `p6-arm64-v1#matrix` |
| P6-03.02 | ⏳ 计划中 | Windows ARM64 运行器与制品执行；负责 `.github/workflows/native-win-arm64.yml`。 | P6-03.01 | 原生 Windows ARM64 以零警告/零错误构建 Release、验证架构/IL 并执行全部声明 CoreCLR/AOT 用例；错误架构制品与缺失原生资产明确失败。 | `p6-arm64-v1#win-arm64` |
| P6-03.03 | ⏳ 计划中 | Linux ARM64 运行器与制品执行；负责 `.github/workflows/native-linux-arm64.yml`。 | P6-03.01 | 原生 Linux ARM64 构建并执行冻结 CoreCLR/AOT 语料；检查 ELF 架构、加载器/原生依赖及 libc 基线，包含缺失库与超时清理负例。 | `p6-arm64-v1#linux-arm64` |
| P6-03.04 | ⏳ 计划中 | ARM64 一致性与可复现性；负责 `tests/platforms/arm64/`、`eng/platforms/Arm64Gate.cs`。 | P6-03.02, P6-03.03 | 固定 ID 将运行时效果、诊断、布局、Drop 和资源限制与声明 x64 基线比较；独立干净原生重跑归档匹配的源码/锁定/SHA 来源，零缺失用例。 | `p6-arm64-v1#parity` |

<a id="p6-04"></a>

## P6-04: macOS x64 与 ARM64 运行器

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-04.01 | ⏳ 计划中 | 冻结 macOS 原生运行器矩阵；负责 `docs/profiles/macos-runners-v1.json`。 | P6-01 | 固定原生 osx-x64/osx-arm64 的 OS/部署目标、SDK/链接器、ABI、原生依赖和预算；功能测试制品不依赖发布签名/公证凭据。 | `p6-macos-v1#matrix` |
| P6-04.02 | ⏳ 计划中 | macOS x64 运行器；负责 `.github/workflows/native-osx-x64.yml`。 | P6-04.01 | 原生 x64 构建零警告 Release、验证 Mach-O 架构并运行声明 CoreCLR/AOT 语料；加载器路径与不支持部署目标错误产生有界诊断并完成清理。 | `p6-macos-v1#osx-x64` |
| P6-04.03 | ⏳ 计划中 | macOS ARM64 运行器；负责 `.github/workflows/native-osx-arm64.yml`。 | P6-04.01 | 原生 ARM64 无翻译地执行固定语料；架构/原生资产、CoreCLR/AOT 行为及取消/失败构建清理均有独立于 x64 的证据。 | `p6-macos-v1#osx-arm64` |
| P6-04.04 | ⏳ 计划中 | macOS 一致性与重跑证据；负责 `tests/platforms/macos/`、`eng/platforms/MacOsGate.cs`。 | P6-04.02, P6-04.03 | 每个冻结用例对账路径/IO/网络/TLS/布局/Drop 行为及已记录平台差异；干净原生重跑保持相同分母，翻译或交叉编译通过不得替代。 | `p6-macos-v1#parity` |

<a id="p6-05"></a>

## P6-05: 供应链、AOT 分析与签名包

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-05.01 | ⏳ 计划中 | 冻结依赖/签名策略；负责 `docs/profiles/release-supply-chain-v1.json`。 | P2-05, P6-01 | 固定精确直接/传递/原生依赖允许列表、来源标识、许可证策略、签名身份/算法、信任根及包数量/大小限制；未知输入按拒绝处理。 | `p6-supply-chain-v1#policy` |
| P6-05.02 | ⏳ 计划中 | 强制裁剪与 AOT 可达性分析；负责 `eng/release/AnalyzeAot.cs`。 | P6-05.01 | 每个声明发布闭包构建均零分析警告且无一揽子抑制；引入动态代码、不支持反射或未批准依赖的夹具以可归因诊断失败。 | `p6-supply-chain-v1#analysis` |
| P6-05.03 | ⏳ 计划中 | 确定性未签名打包；负责 `eng/release/Package.cs`。 | P6-05.01 | 两个干净构建使用相同锁定输入产生相同未签名包字节与规范内容；覆盖路径/时间戳/顺序变化、重复资产及大小限制违规。 | `p6-supply-chain-v1#deterministic-package` |
| P6-05.04 | ⏳ 计划中 | 签名与签名验证；负责 `eng/release/SignPackage.cs`。 | P6-05.02, P6-05.03 | 受信签名绑定精确包摘要/配置档/RID；篡改、错误签名者、过期/撤销/不受信凭据及签名服务不可用遵循冻结失败策略；密钥不进入日志。 | `p6-supply-chain-v1#signatures` |
| P6-05.05 | ⏳ 计划中 | SBOM 与构建来源；负责 `eng/release/Provenance.cs`。 | P6-05.04 | 签名证明对账源码 SHA、锁定文件、构建身份、工具、原生运行器、全部依赖哈希与输出摘要；缺失组件或声明不符独立于签名有效性而失败。 | `p6-supply-chain-v1#provenance` |
| P6-05.06 | ⏳ 计划中 | 消费者包验证与对抗语料；负责 `src/RustSharp.Cli/PackageVerification.cs`、`tests/release/packages/`。 | P6-05.05 | 新消费者按冻结信任策略离线验证有效包；畸形归档、路径穿越、解压膨胀、未知配置档及哈希/签名/来源不符在资源界限内失败。 | `p6-supply-chain-v1#verification` |

<a id="p6-06"></a>

## P6-06: 分离的工作负载与编译器预算

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-06.01 | ⏳ 计划中 | 冻结工作负载与数值阈值；负责 `benchmarks/profiles/release-gates-v1.json`。 | P2-GATE | 列出有限工作负载/RID/运行时模式、硬件类别、预热/重复次数、统计/噪声规则以及明确延迟、吞吐、内存、启动、大小和编译器时间/工作量预算；无待定阈值或 rustc 性能对等声明。 | `p6-budgets-v1#thresholds` |
| P6-06.02 | ⏳ 计划中 | 延迟与吞吐测量；负责 `benchmarks/RustSharp.Benchmarks/LatencyThroughput.cs`。 | P6-06.01 | 冻结工作负载按指定并发报告所需百分位与操作数/时间；空闲/过载对照及注入变慢验证单位、有界运行时长和回归检测。 | `p6-budgets-v1#latency-throughput` |
| P6-06.03 | ⏳ 计划中 | 内存与分配预算；负责 `benchmarks/RustSharp.Benchmarks/Memory.cs`。 | P6-06.01 | 峰值/稳态托管/原生内存与分配指标使用声明采样间隔；注入保留/泄漏及接近限制负载触发正确预算失败，不依赖最终 GC 清理。 | `p6-budgets-v1#memory` |
| P6-06.04 | ⏳ 计划中 | 冷/热启动预算；负责 `benchmarks/RustSharp.Benchmarks/Startup.cs`。 | P6-06.01 | 按冻结缓存条件测量 CoreCLR/AOT 进程启动至就绪；缺失就绪、启动失败及接近时限夹具产生有界失败样本，不从分母消失。 | `p6-budgets-v1#startup` |
| P6-06.05 | ⏳ 计划中 | 代码与包大小预算；负责 `benchmarks/RustSharp.Benchmarks/CodeSize.cs`。 | P6-06.01 | 按声明纳入规则测量 IL/原生代码、总二进制及分发包大小；注入泛型膨胀和重复资产超出对应阈值并指出贡献制品。 | `p6-budgets-v1#code-size` |
| P6-06.06 | ⏳ 计划中 | 编译器时间/工作量/资源预算；负责 `benchmarks/RustSharp.Benchmarks/CompilerResources.cs`。 | P6-06.01 | 冻结源码系列覆盖解析、trait、单态化、MIR 与发出；深度/工作量/输出/堆/时间的上限减一、上限、上限加一用例确定终止并清理自有资源。 | `p6-budgets-v1#compiler` |
| P6-06.07 | ⏳ 计划中 | 历史比较与基准门禁；负责 `benchmarks/RustSharp.Benchmarks/BudgetGate.cs`。 | P6-06.02, P6-06.03, P6-06.04, P6-06.05, P6-06.06 | 同时检查同硬件/配置档历史基线与冻结绝对预算；硬件不兼容、缺失样本、过量噪声或超过重试次数阻塞证据，历史改善不得掩盖绝对预算失败。 | `p6-budgets-v1#history-gate` |

<a id="p6-07"></a>

## P6-07: 升级、回滚与发布操作

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-07.01 | ⏳ 计划中 | 冻结并实现支持的升级路径；负责 `docs/profiles/upgrade-v1.json`、`tests/release/upgrade/`。 | P6-01, P6-05 | 有限的工具链、包、元数据与锁定格式起止版本对可升级可运行应用；不支持版本对在变更前失败；中断升级保留可恢复的旧安装。 | `p6-release-ops-v1#upgrade` |
| P6-07.02 | ⏳ 计划中 | 回滚与迁移恢复；负责 `tests/release/rollback/`、`docs/release-rollback.md`。 | P6-07.01 | 每条声明可逆路径在升级失败后恢复旧可执行文件/配置/锁定状态；不可逆格式/数据转换在执行前诊断并附经测试恢复说明。 | `p6-release-ops-v1#rollback` |
| P6-07.03 | ⏳ 计划中 | 缓存失效；负责 `tests/release/cache/`、`src/RustSharp.Compiler/ArtifactCacheVersioning.cs`。 | P6-07.01 | 编译器/配置档/源码/锁定/RID/ABI 变化精确使受影响缓存失效；过期/篡改/并发写入制品绝不执行，驱逐/取消遵守字节/数量限制与自有路径。 | `p6-release-ops-v1#cache` |
| P6-07.04 | ⏳ 计划中 | 诊断兼容性；负责 `tests/release/diagnostics/`。 | P6-07.01 | 版本化黄金用例在支持升级间保持代码/严重性/范围/参数；有意破坏性变更具有迁移记录，区域/路径变化及超长消息不得破坏机器可读输出稳定性。 | `p6-release-ops-v1#diagnostics` |
| P6-07.05 | ⏳ 计划中 | 原子发布暂存与提升；负责 `eng/release/Promote.cs`。 | P6-07.02, P6-07.03, P6-07.04 | 仅经验证制品经过冻结的暂存/提升/撤回状态机；重复提交、摘要不符与中断提升留下已记录可重试状态，不覆盖无关发布。 | `p6-release-ops-v1#promotion` |
| P6-07.06 | ⏳ 计划中 | 发布操作演练与手册；负责 `tests/release/recovery/`、`docs/release-operations.md`、`docs/release-operations_zh.md`。 | P6-07.05 | 隔离发布演练以有界重试执行安装/升级/失败/回滚/撤回；双语命令复现结果并记录清理、凭据边界与恢复所有权。 | `p6-release-ops-v1#drill` |

<a id="p6-08"></a>

## P6-08: 1.0 候选发布证据

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-08.01 | ⏳ 计划中 | 冻结 1.0 发布清单；负责 `docs/profiles/release-1.0-v1.json`。 | P4-GATE, P5-GATE, P6-02, P6-03, P6-04, P6-05, P6-06, P6-07 | 列举每个声明语言/库/生态系统/输出/RID/版本组合、前置报告与分母；具名适用 P4/P5 门禁，冻结排除项，任何必需父范围不得消失。 | `p6-release-1.0-v1#inventory` |
| P6-08.02 | ⏳ 计划中 | 语言/所有权/Drop 差分对账；负责 `tests/release/conformance/language/`。 | P6-08.01 | 全部发布配置档的编译通过/失败/运行通过/借用/Drop 用例 ID 针对固定 Rust 预言工具运行；每个差异有事先批准的配置档处置，零跳过及零未解释差异。 | `p6-release-1.0-v1#language` |
| P6-08.03 | ⏳ 计划中 | 库/生态系统/应用对账；负责 `tests/release/conformance/applications/`。 | P6-08.01 | 每个声明 API/feature/配置档单元格映射到通过的可执行用例与代表性应用，包含 Web/数据库配置档；服务不可用或缺失清单阻塞候选发布。 | `p6-release-1.0-v1#applications` |
| P6-08.04 | ⏳ 计划中 | 输出与原生平台对账；负责 `tests/release/conformance/outputs/`。 | P6-08.01 | 托管可执行文件/库与声明的 AOT 可执行文件/C ABI 库在每个声明原生 RID 上通过 IL/元数据/PDB 及真实消费者门禁；架构/运行时/SHA 不符使聚合失败。 | `p6-release-1.0-v1#outputs` |
| P6-08.05 | ⏳ 计划中 | 独立干净候选复现；负责 `eng/release/ReproduceCandidate.cs`。 | P6-08.02, P6-08.03, P6-08.04 | 第二次干净原生运行重放固定输入与清单，未签名输出/分母一致且预算通过；签名封装差异按 P6-05 验证，不虚假要求签名字节相同。 | `p6-release-1.0-v1#reproduction` |
| P6-08.06 | ⏳ 计划中 | 签名候选报告与拒绝测试；负责 `tools/RustSharp.Conformance/Release/CandidateGate.cs`。 | P6-08.05 | 签名报告绑定最终 SHA、每个分母/版本/RID、全部排除项及不可变 CI 来源；缺失/重复/篡改证据、任何失败/跳过或未批准差异均拒绝候选发布。 | `p6-release-1.0-v1#signed-gate` |

<a id="p6-gate"></a>

## P6-GATE: 有限退出门禁

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P6-GATE.01 | ⏳ 计划中 | 对账前置与候选证据；负责 `tests/roadmap/p6-exit-v1.json`。 | P2-GATE, P4-GATE, P5-GATE, P6-01, P6-02, P6-03, P6-04, P6-05, P6-06, P6-07, P6-08 | 固定清单引用每个必需叶子与适用阶段门禁、同 SHA 候选发布、原生 RID 证据及明确排除项；候选叶子均不依赖 P6-GATE。 | `p6-exit-v1#inventory` |
| P6-GATE.02 | ⏳ 计划中 | 验证完整原生证据与清理；负责 `eng/release/Collect-P6Evidence.cs`。 | P6-GATE.01 | 全部声明运行通过零警告/零错误 Release、零失败/零跳过一致性与资源预算；报告证明进程/服务/临时对象归属清理，不以交叉编译替代执行。 | `p6-exit-v1#native-evidence` |
| P6-GATE.03 | ⏳ 计划中 | 验证签名可复现发布包；负责 `tests/release/final-bundle/`。 | P6-GATE.02 | 全新安装与验证消费精确候选摘要、签名与来源；重跑说明可重建证据，缺失制品、失效链接或版本不一致阻塞结项。 | `p6-exit-v1#bundle` |
| P6-GATE.04 | ⏳ 计划中 | 发布 1.0 门禁记录与同步状态；负责成对路线图/门面文档的 P6 部分。 | P6-GATE.03 | 可审阅发布记录标明不可变 CI 运行/制品、最终远端 SHA、分母、全部 RID 结果及清理；仅在每个必需叶子和门禁通过后双语同步变更 P6 状态。 | `p6-exit-v1#publication` |
