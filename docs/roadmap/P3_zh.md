# P3 颗粒化任务：宏、异步与有界 unsafe/FFI

[English](P3.md) | 简体中文 | [主路线图](../../ROADMAP_zh.md)

所有行均为 ⏳ 计划中。本文分解现有六项 P3 承诺，不声称计划中的 API、命令、语料或报告已经存在。现有实现根目录为 `src/RustSharp.Syntax/`、`src/RustSharp.Semantics/`、`src/RustSharp.CodeGen.IL/`、`src/RustSharp.Compiler/`、`src/RustSharp.Runtime/` 和 `tools/RustSharp.Conformance/`。下列新子目录、配置档清单、SDK/宿主项目与测试工作区均为拟建交付物。列出的 `rsc test` 命令仅在 CLI 与夹具前置条件具备后可执行。

合同冻结叶子必须先产出非空版本化清单，明确精确 API/feature/协议、用例 ID、预期结果、数值资源预算、分母与哈希，随后实现叶子方可关闭。上游 crate 版本与 TLS/ABI 细节在该叶子确定，本文不猜测。扩大范围必须新增清单版本；不得为通过而删除或放宽既有用例。

每个叶子负责具名代码及其对应测试；在共享根目录并行编辑前必须明确文件分工。证据列描述待产出证据，并非已取得证据。每个叶子记录 `artifacts/roadmap/<ID>.json`（计划中的忽略输出），包含源码 SHA、清单/版本/哈希、实际/预期计数、工具版本、平台/RID、准确命令、有限超时/重试/项目上限、PID/启动时间/父进程、退出/失败原因与清理。CI 归档这些报告并提供稳定运行链接。合同叶子使用验证器/篡改测试；实现叶子使用正向、负向、边界与资源用例。全部承诺用例必须运行且零失败/跳过；计划验证不等于完成。

父 ID 表示其全部子项已有认可证据。叶子根据自身依赖与证据关闭，无需等待所属阶段门禁。`P3-GATE` 表示下方全部四项门禁。P2-GATE 是 P3 阶段的硬前置门禁。单项工作可在明确依赖成立后开展，但不能提前关闭阶段。

<a id="p3-01"></a>

## P3-01: 内置宏与声明式宏

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-01.01 | ⏳ 计划中 | `docs/profiles/macros-v1.json` — 冻结内置宏清单、片段语法、重复/卫生性规则与有限预算。 | P1-01, P2-GATE | 每种纳入形式均有具名正例、拒绝与限制用例；不支持片段有明确诊断；计数与哈希冻结。 | 清单模式与篡改测试；宏清单。 |
| P3-01.02 | ⏳ 计划中 | `src/RustSharp.Syntax/Macros/` — 实现词元树片段匹配与重复。 | P3-01.01 | 嵌套、分隔与零/一/多次重复匹配冻结语法；歧义或损坏捕获以有限匹配工作量拒绝。 | 匹配器黄金用例与工作量超限失败。 |
| P3-01.03 | ⏳ 计划中 | `src/RustSharp.Syntax/Macros/` — 实现作用域定义、卫生性与捕获替换。 | P3-01.02 | 定义/使用点名称、嵌套作用域与遮蔽按契约解析；意外捕获与重复绑定失败；展开确定。 | 卫生性语料与 rustc 1.98 结果对比。 |
| P3-01.04 | ⏳ 计划中 | `src/RustSharp.Compiler/`, `src/RustSharp.Semantics/` — 将内置宏与展开语法接入 HIR 和类型化 MIR。 | P3-01.03 | 每个冻结内置宏与展开结果使用正常编译链；不支持结果在发射前拒绝；无其他执行路径回退。 | 编译/运行语料与 HIR/MIR 快照。 |
| P3-01.05 | ⏳ 计划中 | `src/RustSharp.Syntax/Macros/`, `src/RustSharp.CodeGen.IL/` — 保留展开回溯与源码/PDB 映射。 | P3-01.04 | 嵌套定义/调用范围指向原始源码；损坏展开与缺失映射给出稳定诊断；重复输出逐字节一致。 | 诊断/PDB 黄金测试与 PE 确定性检查。 |
| P3-01.06 | ⏳ 计划中 | `tests/macros/macro-rules/`, `tools/RustSharp.Conformance/` — 执行展开限制并运行完整宏语料。 | P3-01.05 | 递归、词元数、匹配工作量、截止时间与取消在边界内通过、越界拒绝；全部冻结用例按规定运行或诊断。 | 计划：`rsc test tests/macros/macro-rules/Cargo.toml`；固定宏报告。 |

<a id="p3-02"></a>

## P3-02: 进程外过程宏

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-02.01 | ⏳ 计划中 | `docs/profiles/proc-macros-v1.json` — 冻结协议、SDK、信任边界与宿主资源契约。 | P3-01, P0-04 | 枚举版本化词元/范围/诊断消息、能力、环境输入及大小/时间上限；不兼容或损坏消息拒绝。 | 协议模式、兼容性与超大消息夹具。 |
| P3-02.02 | ⏳ 计划中 | `tools/RustSharp.MacroHost/` — 实现有界宿主握手、传输与生命周期。 | P3-02.01 | 每个请求有自有进程记录与确定结果；截断帧、协议不匹配、崩溃和超时不能挂起或破坏编译。 | 含 PID 与清理的宿主故障注入报告。 |
| P3-02.03 | ⏳ 计划中 | `src/RustSharp.MacroSdk/`, `tests/macros/proc/` — 提供派生、属性与函数式 SDK 入口。 | P3-02.02 | 每个冻结入口展开一个有效示例；无效注册、不支持能力和无效词元输出在宿主边界失败。 | 三类宏及 SDK 负向夹具。 |
| P3-02.04 | ⏳ 计划中 | `src/RustSharp.Compiler/`, `src/RustSharp.Syntax/Macros/` — 集成展开映射、诊断与确定性缓存键。 | P3-02.03 | 宿主输出重新进入 HIR/MIR；源码映射穿过嵌套调用；声明输入变化使缓存失效；损坏/过期缓存拒绝。 | 冷/热输出对比与源码映射篡改测试。 |
| P3-02.05 | ⏳ 计划中 | `tools/RustSharp.MacroHost/`, `tests/macros/proc/` — 执行隔离、取消与自有进程树清理。 | P3-02.02 | 过量输出、创建子进程、取消和禁止访问符合冻结信任边界；全部自有子进程与临时文件回收。 | 含截止时间/输出/清理记录的对抗宿主套件。 |
| P3-02.06 | ⏳ 计划中 | `tests/macros/proc/`, `tools/RustSharp.Conformance/` — 运行完整宏宿主集成分母。 | P3-02.04, P3-02.05 | 清单全部协议版本及三类宏均编译/运行；崩溃与取消无部分程序集或泄漏宿主；零跳过。 | 计划：`rsc test tests/macros/proc/Cargo.toml`；宏宿主报告。 |

<a id="p3-03"></a>

## P3-03: 异步降低与运行时桥接

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-03.01 | ⏳ 计划中 | `docs/profiles/async-v1.json` — 冻结 Future/Waker/Task、取消与 panic 契约。 | P1-06, P2-02 | 受支持异步形式、Send/线程边界、固定、再次 poll 行为与状态预算具有有限用例 ID；不支持形式有稳定拒绝规则。 | 异步清单与状态/所有权契约审阅。 |
| P3-03.02 | ⏳ 计划中 | `src/RustSharp.Semantics/` — 将异步函数与 await 降低为显式 MIR 状态。 | P3-03.01 | 立即完成与嵌套挂起保留局部值/控制流；不可达或无效迁移拒绝；状态/字段数遵守预算；快照确定。 | 状态机快照与无效迁移测试。 |
| P3-03.03 | ⏳ 计划中 | `src/RustSharp.Semantics/`, `src/RustSharp.CodeGen.IL/` — 跨挂起保留借用、移动、固定与 Drop。 | P3-03.02 | 捕获值生命周期符合规定；无效引用逃逸/移动拒绝；完成、取消和展开按顺序恰好清理已初始化字段一次。 | rustc 1.98 借用/Drop 语料与生成程序运行跟踪。 |
| P3-03.04 | ⏳ 计划中 | `src/RustSharp.Runtime/Async/` — 实现 Future poll 与 Waker 注册/分派。 | P3-03.02 | Ready/Pending 与注册前后唤醒有效；重复唤醒与并发注册不丢失进展或超过队列上限。 | 确定调度交错与唤醒预算测试。 |
| P3-03.05 | ⏳ 计划中 | `src/RustSharp.Runtime/Async/` — 桥接 .NET Task 完成、错误与取消。 | P3-03.03, P3-03.04 | 双向结果/错误/取消仅映射一次；已完成任务与完成/取消竞争符合冻结契约且不使用动态代码。 | Task 桥接竞争矩阵与 AOT 可达性检查。 |
| P3-03.06 | ⏳ 计划中 | `src/RustSharp.CodeGen.IL/` — 发射异步 CLR LIR、元数据与挂起源码映射。 | P3-03.05 | 全部受支持状态发射有效确定的 IL/PDB；无效栈/状态元数据在发射前拒绝；异步帧有声明的调试映射。 | ILVerify、元数据篡改与 PDB 单步测试。 |
| P3-03.07 | ⏳ 计划中 | `tests/async/core/`, `tools/RustSharp.Conformance/` — 运行完成、取消、panic 与并发集成。 | P3-03.06 | 冻结编译通过/失败/运行/Drop 分母通过；停滞 Future 在看门狗/取消下终止；CoreCLR 与 AOT 可观察行为一致。 | 计划：`rsc test tests/async/core/Cargo.toml`；异步报告。 |

<a id="p3-04"></a>

## P3-04: 精确版本 tokio 配置档

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-04.01 | ⏳ 计划中 | `compat/tokio/profile-v1.json` — 冻结上游精确版本、成员与 feature 组合。 | P3-03, P2-03 | 运行时/任务/计时器/同步/IO/网络成员、feature 闭包与不支持 API 均有限；每个纳入成员有行为与预算用例 ID。 | 锁定 API 清单与清单验证。 |
| P3-04.02 | ⏳ 计划中 | `compat/tokio/runtime/`, `compat/tokio/task/` — 实现运行时创建、调度与任务生命周期。 | P3-04.01 | spawn/join/abort/shutdown 遵守冻结线程模型；任务 panic、嵌套运行时误用与任务容量耗尽具有声明结果。 | 运行时/任务 API 用例与有界调度压力测试。 |
| P3-04.03 | ⏳ 计划中 | `compat/tokio/time/` — 实现声明的计时器与超时。 | P3-04.02 | sleep/interval/timeout 用例使用可控时钟；取消与漏 tick 符合策略；溢出与计时器容量限制确定拒绝。 | 虚拟时钟时间线与计时器边界用例。 |
| P3-04.04 | ⏳ 计划中 | `compat/tokio/sync/` — 实现声明的通道、锁与通知原语。 | P3-04.02 | 纳入原语保持顺序/所有权；关闭端点、取消等待者与争用结果明确；执行队列上限。 | 同步 API 分母与有限交错矩阵。 |
| P3-04.05 | ⏳ 计划中 | `compat/tokio/io/`, `compat/tokio/net/` — 实现声明的异步 IO 与网络成员。 | P3-04.02 | 部分读写、EOF、就绪与连接生命周期通过本地夹具；断连、超时、取消与缓冲限制无自有套接字/任务残留。 | 回环 IO/网络契约与清理报告。 |
| P3-04.06 | ⏳ 计划中 | `compat/tokio/`, `tools/RustSharp.Conformance/` — 对账 API 清单并执行完整 feature 矩阵。 | P3-04.03, P3-04.04, P3-04.05 | 每个冻结 API/feature 组合在两个运行时通过；不支持组合明确拒绝；无缺失、重复或跳过用例 ID。 | 计划：`rsc test compat/tokio/Cargo.toml --features declared-profile`；API 报告。 |

<a id="p3-05"></a>

## P3-05: 有界 unsafe、布局与 C FFI

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-05.01 | ⏳ 计划中 | `docs/profiles/unsafe-ffi-v1.json` — 冻结受支持 unsafe 操作、布局与 ABI 契约。 | P1-08, P2-06 | 类型、指针来源、调用约定、所有权、回调与错误边界具有有限用例；Rust ABI、无限制 transmute、内部函数与汇编排除项保持明确。 | 认可的布局/FFI 契约与拒绝清单。 |
| P3-05.02 | ⏳ 计划中 | `src/RustSharp.Semantics/`, `tests/unsafe-ffi/layout/` — 计算 repr(C)、联合体、对齐与打包布局。 | P3-05.01 | 冻结字段偏移/大小/对齐与原生 C 夹具一致；非法组合及算术/大小溢出在分配前拒绝。 | Windows/Linux 原生布局预言表。 |
| P3-05.03 | ⏳ 计划中 | `src/RustSharp.Semantics/`, `src/RustSharp.CodeGen.IL/` — 降低声明的裸指针操作与联合体访问。 | P3-05.02 | 有效类型化指针/地址操作与联合体访问可执行；在承诺范围内拒绝 unsafe 上下文/类型/逃逸违规；无效内存 UB 用例分类而不作为安全测试执行。 | 指针/联合体编译与已定义行为执行语料。 |
| P3-05.04 | ⏳ 计划中 | `src/RustSharp.Runtime/`, `src/RustSharp.Semantics/` — 实现托管/原生固定与生命周期保护。 | P3-05.03 | 声明指针在自有固定作用域内稳定；逃逸、提前解除固定与重复释放按规定拒绝或失败；取消/Drop 恰好释放一次。 | GC 压力固定测试与所有权/清理负例。 |
| P3-05.05 | ⏳ 计划中 | `src/RustSharp.CodeGen.IL/`, `tests/unsafe-ffi/native/` — 发射 C 导入并封送声明的标量/聚合/缓冲签名。 | P3-05.04 | 原生函数观察到准确 ABI 布局与缓冲长度；缺失符号、不支持约定/签名和所有权不匹配可预测失败。 | 原生 C 调用双方夹具、ILVerify 适用性账本。 |
| P3-05.06 | ⏳ 计划中 | `src/RustSharp.Runtime/`, `tests/unsafe-ffi/native/` — 实现声明的回调、线程与 panic 边界。 | P3-05.05 | 回调生命周期/线程契约成立；释放后回调与不支持线程用法按规定拒绝；panic 不穿过 C 边界展开。 | 回调/错误夹具与子进程 panic 边界测试。 |
| P3-05.07 | ⏳ 计划中 | `tests/unsafe-ffi/`, `tools/RustSharp.Conformance/` — 运行已定义行为 ABI 与排除项矩阵。 | P3-05.06 | 全部支持用例在两个 x64 平台/运行时与原生夹具一致；排除语法产生稳定诊断；分配、固定句柄与子进程回收。 | 计划：`rsc test tests/unsafe-ffi/Cargo.toml`；ABI 与资源报告。 |

<a id="p3-06"></a>

## P3-06: TLS 原语与平台证书

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-06.01 | ⏳ 计划中 | `docs/profiles/tls-v1.json` — 冻结 TLS 适配器、协议与证书验证配置档。 | P3-03, P2-03 | 明确提供程序/依赖精确版本、允许协议、信任/吊销/主机名规则与握手/缓冲预算；本路线图不暗示任何协议选择。 | TLS API/安全清单与夹具清单。 |
| P3-06.02 | ⏳ 计划中 | `src/RustSharp.Runtime/Tls/`, `tests/tls/certificates/` — 实现证书加载与信任验证。 | P3-06.01 | 本地受信任链通过；不受信任/过期/用途错误链按冻结策略失败；损坏/超大证书有界拒绝。 | 确定的本地 PKI 正负矩阵。 |
| P3-06.03 | ⏳ 计划中 | `src/RustSharp.Runtime/Tls/`, `tests/tls/` — 强制对端主机名与声明协议协商。 | P3-06.02 | 声明的 DNS/IP 名称与协议交集成功；不匹配、排除协议与降级用例以稳定错误关闭连接。 | 主机名/协议回环矩阵。 |
| P3-06.04 | ⏳ 计划中 | `src/RustSharp.Runtime/Tls/` — 实现有界异步握手与加密流。 | P3-06.03 | 分片读写与半关闭遵守契约；停滞握手、取消 IO 与超大缓冲在声明上限内终止且不复用数据。 | TLS 流、超时与缓冲上限夹具。 |
| P3-06.05 | ⏳ 计划中 | `src/RustSharp.Runtime/Tls/`, `tests/tls/` — 验证释放、平台适配器与 AOT 可达性。 | P3-06.04 | 成功/失败/取消恰好释放套接字与证书句柄一次；Windows/Linux 适配器声明结果等价；动态代码/反射序列化不可达。 | 句柄计数故障注入与 AOT 分析器报告。 |
| P3-06.06 | ⏳ 计划中 | `tests/tls/`, `tools/RustSharp.Conformance/` — 对本地端点执行完整 TLS 契约。 | P3-06.05 | 全部冻结信任/不信任、主机名、协商、取消与释放用例通过，不依赖公网服务且无跳过用例。 | 计划：`rsc test tests/tls/Cargo.toml`；TLS 契约报告。 |

<a id="p3-gate"></a>

## P3-GATE: 阶段退出

| ID | 状态 | 交付物 / 所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P3-GATE.01 | ⏳ 计划中 | `tools/RustSharp.Conformance/`, `docs/profiles/p3-exit-v1.json` — 冻结并对账完整 P3 退出分母。 | P2-GATE, P3-01, P3-02, P3-03, P3-04, P3-05, P3-06 | 合并全部已认可清单、API/feature 与通过/失败/运行/资源用例；拒绝缺失/重复用例、分母变化和缺失前置证据；零未解释 rustc 差异。 | 退出清单验证器测试与逐叶证据索引。 |
| P3-GATE.02 | ⏳ 计划中 | `.github/workflows/`, `eng/` — 在原生 Windows x64 运行完整 P3 门禁。 | P3-GATE.01 | Release 零警告/错误；固定 CoreCLR 与 Native AOT 分母零失败/跳过；可验证 IL 通过 ILVerify；unsafe IL 排除遵守冻结策略且不静默消失。 | Windows CI SHA/链接、原生运行日志与 IL 策略报告。 |
| P3-GATE.03 | ⏳ 计划中 | `.github/workflows/`, `eng/` — 在原生 Linux x64 运行完整 P3 门禁。 | P3-GATE.01 | 相同固定清单通过 CoreCLR/Native AOT 与 IL 验证策略，零失败/跳过且无 AOT/裁剪抑制；原生 ABI/TLS 夹具在本地运行。 | Linux CI SHA/链接、原生运行日志与 IL 策略报告。 |
| P3-GATE.04 | ⏳ 计划中 | `docs/roadmap/P3.md`, `docs/roadmap/P3_zh.md`, `ROADMAP.md`, `ROADMAP_zh.md` — 关闭 P3 前对账最终 SHA、兼容性声明与清理。 | P3-GATE.02, P3-GATE.03 | 同提交证据覆盖每个必需格、确定性输出与有界清理；双语 API/排除声明一致；报告篡改拒绝过期/缺失数据；此后才关闭 P3-GATE。 | 包含工具、清单、RID、命令、PID、超时与清理的最终证据索引。 |
