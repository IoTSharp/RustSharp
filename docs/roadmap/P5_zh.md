# P5：数据库与 ORM 的可验收任务

[English](P5.md) | 简体中文 | [返回路线图](../../ROADMAP_zh.md)

本清单细化原有 P5 范围，不扩大兼容性承诺。所有叶子均为 ⏳ 计划中；表中的实现目录、配置档、清单、运行器和报告是计划交付物，并非已有执行证据。父任务完成要求其全部叶子通过。阶段汇总节点 `P5-GATE` 要求 P5-01 至 P5-06 与四个门禁叶子全部通过。P5-07 保持原来的 P5 退出后评估范围，**不属于 P5 退出阻塞集合**，也不承诺支持 diesel；这消除了原有门槛依赖的循环，而没有缩小数据库交付范围。

每个父任务的首个叶子继承主路线图的硬依赖；后续叶子按表内边传递继承。`P2-06` 等父 ID 指其全部叶子，`P3-GATE` 等指该阶段完整汇总门禁。文件/目录所有权限定到表中单元；共享接口或清单的更改须串行协调，不能以目录不同为由并行改同一文件。

证据列中的 `suite-v1#group` 固定指计划的 `tests/roadmap/suite-v1.json` 及其具名分组；该清单必须逐项列出不可变用例 ID、输入哈希、预期结果、正/负/边界/预算分类与精确分母。计划由 `tools/RustSharp.Conformance` 添加 `--manifest <path> --group <group>` 入口；当前不能把这个未来入口当作可执行命令。配套测试目录由交付物列限定。本地报告计划保存在 `artifacts/p5/local/<suite>/<group>.json`，原生 CI 上传同结构报告并给出稳定运行和制品链接；被忽略的制品不强制提交。版本/数值预算必须先以决策叶子冻结；后续扩展新增清单版本，不删除、放宽、替换既有用例，也不在结项时临时调整分母。

每份报告同时记录编译器 SHA、工具/提供程序/服务器版本、配置档、锁定哈希、平台/RID、命令、用例分母、失败原因、超时、最大尝试数和退避、PID/父 PID/开始时间及清理结果；运行器有取消、数值资源上限与仅自有资源清理。数据库不可用属于阻塞，不是跳过或通过。功能叶子必须提供对应正向、负向、边界和预算证据；本地通过与 CI 通过分别记录，所有声明的运行时单元均实际执行。

<a id="p5-01"></a>

## P5-01: 提供程序边界

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-01.01 | ⏳ 计划中 | 冻结提供程序/后端/服务器/API 与资源配置档；负责 `docs/profiles/database-provider-v1.json`。 | P3-GATE, P2-06 | SQLite、PostgreSQL、MySQL、SQL Server 四种引擎均固定 .NET 提供程序、原生资产、服务器版本、支持类型以及连接数/行数/字节数/时限的数值限制；排除项不得删减父任务契约。 | `p5-provider-v1#profile` |
| P5-01.02 | ⏳ 计划中 | 连接与命令所有权；负责 `src/RustSharp.Runtime/Database/Connection.cs`、`Command.cs`。 | P5-01.01 | 打开/关闭/重开和参数绑定正常；释放后使用、重复所有权和缺失参数稳定失败；空闲或失败命令的释放均恰好一次归还连接。 | `p5-provider-v1#connection-command` |
| P5-01.03 | ⏳ 计划中 | 类型化值与错误；负责 `src/RustSharp.Runtime/Database/Values.cs`、`Errors.cs`。 | P5-01.02 | 往返保持配置档声明的 null、数值极值、十进制精度、文本编码、时间戳与二进制数据；非法转换、溢出及提供程序错误保持稳定类别和原始原因。 | `p5-provider-v1#values-errors` |
| P5-01.04 | ⏳ 计划中 | 取消与释放；负责 `src/RustSharp.Runtime/Database/Cancellation.cs`。 | P5-01.02 | 预取消、执行中取消、超时及释放/取消竞态在冻结时限内终止；重复取消无副作用，且不残留任务自有连接、读取器或子进程。 | `p5-provider-v1#cancel-dispose` |
| P5-01.05 | ⏳ 计划中 | 事务状态机；负责 `src/RustSharp.Runtime/Database/Transaction.cs`。 | P5-01.03, P5-01.04 | 提交/回滚/释放和支持的隔离级别具有明确效果；重复结束、提交失败与取消会回滚或返回已记录的终态错误；锁在预算内释放。 | `p5-provider-v1#transaction` |
| P5-01.06 | ⏳ 计划中 | 提供程序适配器可达性；负责 `tests/database/provider-contract/` 及其 AOT 根。 | P5-01.03, P5-01.04, P5-01.05 | 每个冻结适配器在 CoreCLR 和 Native AOT 上运行连接/值/错误/取消/事务分组；不可达运行时模型生成、反射回退或裁剪/AOT 警告。 | `p5-provider-v1#aot-contract` |

<a id="p5-02"></a>

## P5-02: sqlx：SQLite、PostgreSQL 和 MySQL

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-02.01 | ⏳ 计划中 | 冻结精确 sqlx API/feature 与后端矩阵；负责 `docs/profiles/sqlx-v1.json`。 | P5-01, P3-04 | 清单标识三种引擎每个选定公共成员与 feature 组合，引用冻结的提供程序版本，并分配固定的正向、负向、边界和预算用例 ID。 | `p5-sqlx-v1#profile` |
| P5-02.02 | ⏳ 计划中 | SQLite 查询/类型适配器；负责 `compat/sqlx/sqlite/`。 | P5-02.01 | 参数化 CRUD 与全部映射值往返正常；null、空结果、唯一性及 busy/locked 错误符合契约；文件与内存数据库遵守字节/行数限制并完成释放。 | `p5-sqlx-v1#sqlite` |
| P5-02.03 | ⏳ 计划中 | PostgreSQL 查询/类型适配器；负责 `compat/sqlx/postgres/`。 | P5-02.01 | 针对固定服务器通过参数化 CRUD、服务器错误与配置档类型测试；Unicode/null/数值边界保持原值；错误类型、超大行及断连服务器在界限内失败。 | `p5-sqlx-v1#postgres` |
| P5-02.04 | ⏳ 计划中 | MySQL 查询/类型适配器；负责 `compat/sqlx/mysql/`。 | P5-02.01 | 参数化 CRUD 及有符号/无符号、十进制、时间、二进制映射在冻结 SQL 模式/字符集下通过；强制转换、截断与断连不得静默改变已接受结果。 | `p5-sqlx-v1#mysql` |
| P5-02.05 | ⏳ 计划中 | 连接池与流式集成；负责 `compat/sqlx/pool/`、`compat/sqlx/stream/`。 | P5-02.02, P5-02.03, P5-02.04 | 每种引擎覆盖池饱和、获取超时、背压、提前 Drop 流与取消；最大连接数/行数/缓冲字节数有界，释放后的连接可复用。 | `p5-sqlx-v1#pool-stream` |
| P5-02.06 | ⏳ 计划中 | 事务与回滚集成；负责 `compat/sqlx/transaction/`。 | P5-02.05 | 三种引擎的提交、回滚、隔离和声明的保存点保持数据库状态；语句错误、断连与取消证明回滚/无部分提交，或产生稳定的结果不确定错误。 | `p5-sqlx-v1#transaction` |
| P5-02.07 | ⏳ 计划中 | 迁移与可执行 API 覆盖；负责 `compat/sqlx/migrate/`、`tests/database/sqlx/`。 | P5-02.06 | 各引擎的有序迁移、重复运行、校验和不符与迁移中途失败均确定；完整清单在 CoreCLR/AOT 上通过，无缺失 API 用例、跳过的服务器或自有资源泄漏。 | `p5-sqlx-v1#migration-coverage` |

<a id="p5-03"></a>

## P5-03: 离线 SQL 检查

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-03.01 | ⏳ 计划中 | 冻结 schema 快照协议；负责 `docs/profiles/sqlx-schema-v1.json`。 | P5-02, P3-02 | 协议固定方言/提供程序/schema 哈希/查询哈希/参数与结果类型、规范顺序及 schema/查询数值预算；版本不符和不支持的元数据具有具名诊断。 | `p5-sqlx-checked-v1#snapshot-format` |
| P5-03.02 | ⏳ 计划中 | 有界快照获取；负责 `tools/RustSharp.Conformance/Database/SchemaSnapshot.cs`。 | P5-03.01 | 三种 sqlx 引擎对未变更 schema 生成逐字节一致的规范快照；认证失败、取消及元数据/字节限制均清理连接并排除密钥。 | `p5-sqlx-checked-v1#snapshot-capture` |
| P5-03.03 | ⏳ 计划中 | 离线查询/类型验证；负责 `compat/sqlx/macros/checked-query/`。 | P5-03.02 | 禁用网络后，有效查询仅使用锁定快照编译；非法 SQL、缺失列、参数数量/类型与可空结果不符均在稳定源码范围失败。 | `p5-sqlx-checked-v1#offline-check` |
| P5-03.04 | ⏳ 计划中 | 过期与对抗性限制；负责 `tests/database/sqlx-checked/fail/`、`budget/`。 | P5-03.03 | 缺失/篡改/过期快照绝不触发在线回退；上限减一、上限与上限加一的 schema/查询输入具有确定诊断、有界工作量且不覆盖非自有文件。 | `p5-sqlx-checked-v1#stale-budget` |
| P5-03.05 | ⏳ 计划中 | 宏来源与运行时一致性；负责 `tests/database/sqlx-checked/run/`。 | P5-03.03, P5-03.04 | 展开的查询绑定保留原始源码映射，在 CoreCLR/AOT 上与冻结 schema 的实际行一致；运行时 schema 变化返回声明错误，不破坏类型化值。 | `p5-sqlx-checked-v1#runtime-parity` |

<a id="p5-04"></a>

## P5-04: tiberius 与 SQL Server

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-04.01 | ⏳ 计划中 | 冻结 tiberius API/feature/服务器配置档；负责 `docs/profiles/tiberius-v1.json`。 | P5-01, P3-04 | 固定精确上游 API 标识、.NET 提供程序、SQL Server 版本、认证/TLS 模式、类型清单与池/流/时间数值预算；不支持的模式明确失败。 | `p5-tiberius-v1#profile` |
| P5-04.02 | ⏳ 计划中 | SQL Server 命令/类型适配器；负责 `compat/tiberius/query/`、`types/`。 | P5-04.01 | 参数化 CRUD 与全部声明类型（含 null、精度、Unicode 和二进制边界）往返正常；错误参数类型、重复键及服务器错误保留稳定类别。 | `p5-tiberius-v1#query-types` |
| P5-04.03 | ⏳ 计划中 | SQL Server 连接池与流；负责 `compat/tiberius/pool/`、`stream/`。 | P5-04.02 | 池饱和、读取器背压、空/多个声明结果集与提前 Drop 具有确定所有权；行数/字节限制和获取超时归还可复用连接。 | `p5-tiberius-v1#pool-stream` |
| P5-04.04 | ⏳ 计划中 | SQL Server 事务/取消；负责 `compat/tiberius/transaction/`。 | P5-04.03 | 提交、回滚、声明隔离级别/保存点与取消/断连竞态证明数据库效果；失败/已取消命令不得将失效连接当作健康连接归还池。 | `p5-tiberius-v1#transaction-cancel` |
| P5-04.05 | ⏳ 计划中 | SQL Server 迁移契约；负责 `tests/database/tiberius/migrations/`。 | P5-04.04 | 版本化迁移夹具覆盖首次应用、重复、校验和漂移及部分失败；回滚/恢复策略产生记录的 schema 状态并不残留未释放锁。 | `p5-tiberius-v1#migrations` |
| P5-04.06 | ⏳ 计划中 | SQL Server AOT/API 覆盖；负责 `tests/database/tiberius/aot/`。 | P5-04.02, P5-04.03, P5-04.04, P5-04.05 | 全部清单分组使用固定的真实 SQL Server，在 Windows/Linux x64 CoreCLR/AOT 上执行；认证/TLS 负例真实运行，服务器不可用不得计为通过。 | `p5-tiberius-v1#aot-coverage` |

<a id="p5-05"></a>

## P5-05: sea-orm

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-05.01 | ⏳ 计划中 | 冻结 sea-orm 实体/API/后端配置档；负责 `docs/profiles/sea-orm-v1.json`。 | P5-02, P5-04, P3-02 | 精确成员/feature 清单固定四种引擎的实体、关系基数、迁移与事务行为，并明确不支持组合及图/查询数值预算。 | `p5-sea-orm-v1#profile` |
| P5-05.02 | ⏳ 计划中 | 静态实体与生成元数据模型；负责 `compat/sea-orm/entity/`、`codegen/`。 | P5-05.01 | 等价静态/生成实体产生确定的字段/键/null 映射且无运行时生成；重复键、不支持字段及生成器限制给出稳定源码诊断。 | `p5-sea-orm-v1#entities` |
| P5-05.03 | ⏳ 计划中 | 实体 CRUD 与查询映射；负责 `compat/sea-orm/query/`。 | P5-05.02 | 参数化增删改查与选定筛选/分页在四种引擎上正常；空结果、冲突写入、null 与页边界具有明确结果。 | `p5-sea-orm-v1#crud` |
| P5-05.04 | ⏳ 计划中 | 关系与有界加载；负责 `compat/sea-orm/relation/`。 | P5-05.03 | 声明的一对一/一对多/多对多遍历保持键与可空性；缺失/重复关联键、环及图/查询限制，仅按冻结契约允许的方式失败或截断。 | `p5-sea-orm-v1#relations` |
| P5-05.05 | ⏳ 计划中 | 实体事务与迁移；负责 `compat/sea-orm/transaction/`、`migration/`。 | P5-05.03 | 多实体原子操作和有序 schema 迁移在各后端证明提交/回滚/取消效果；迁移漂移、依赖环与步骤中途失败遵循已记录恢复策略。 | `p5-sea-orm-v1#transaction-migration` |
| P5-05.06 | ⏳ 计划中 | ORM AOT 与清单对账；负责 `tests/database/sea-orm/`。 | P5-05.04, P5-05.05 | 每个选定 API/feature/后端单元格有可执行用例；生成/静态实体一致性、清理与图预算在 CoreCLR/AOT 上通过，无反射回退或缺失清单条目。 | `p5-sea-orm-v1#aot-inventory` |

<a id="p5-06"></a>

## P5-06: 数据库应用与公开矩阵

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-06.01 | ⏳ 计划中 | 构建四引擎应用夹具；负责 `samples/database-api/`。 | P5-02, P5-03, P5-04, P5-05 | 每种引擎一个声明的示例场景演示 CRUD、连接池、事务、取消和迁移；执行前明确预期效果与隔离服务/数据库所有权。 | `p5-database-apps-v1#fixtures` |
| P5-06.02 | ⏳ 计划中 | 原生 Windows x64 数据库运行器；负责 `.github/workflows/p5-windows.yml`。 | P5-06.01 | 四个应用针对固定服务在原生 win-x64 上发布并以 CoreCLR/AOT 运行；认证失败、超时和关闭证明清理，引擎/运行时单元格零跳过。 | `p5-database-apps-v1#win-x64` |
| P5-06.03 | ⏳ 计划中 | 原生 Linux x64 数据库运行器；负责 `.github/workflows/p5-linux.yml`。 | P5-06.01 | 四个应用在原生 linux-x64 上发布并以 CoreCLR/AOT 运行；相同场景 ID/预期效果与 Windows 一致，含取消及原生依赖解析。 | `p5-database-apps-v1#linux-x64` |
| P5-06.04 | ⏳ 计划中 | 发布兼容性与排除项矩阵；负责 `docs/database-compatibility.md`、`docs/database-compatibility_zh.md`。 | P5-06.02, P5-06.03 | 双语矩阵将每个引擎/提供程序/服务器/API/feature/版本/RID 单元格与不可变报告链接对账；缺失证据保持未完成，不推断上游源码兼容。 | `p5-database-apps-v1#matrix` |
| P5-06.05 | ⏳ 计划中 | 复现服务配置与失败清理；负责 `eng/database/`、`tests/database/cleanup/`。 | P5-06.04 | 干净运行器重放锁定配置并生成报告；启动失败、取消及重试耗尽只清理自有服务/数据库/进程，隐去密钥并保留诊断制品。 | `p5-database-apps-v1#reproduce-cleanup` |

<a id="p5-07"></a>

## P5-07: 退出后的 diesel 评估

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-07.01 | ⏳ 计划中 | 冻结有界评估问题；负责 `probes/diesel/profile-v1.json`。 | P5-GATE | P5 退出后固定候选上游版本、选定 API/后端、探针数量及时间/工作量限制；记录可行性问题，不新增受支持配置档。 | `p5-diesel-evaluation-v1#scope` |
| P5-07.02 | ⏳ 计划中 | 探索类型系统与宏；负责 `probes/diesel/language/`。 | P5-07.01 | 有限正/负探针标识所需 trait/泛型/宏特性与可复现诊断；不支持用例保留为明确发现，不作为跳过的成功。 | `p5-diesel-evaluation-v1#language` |
| P5-07.03 | ⏳ 计划中 | 探索后端/原生/AOT 可行性；负责 `probes/diesel/backend/`。 | P5-07.01 | 固定后端探针集记录原生依赖、所有权和 AOT 可达性及有界失败原因；任何探针结果都不构成实现或兼容性声明。 | `p5-diesel-evaluation-v1#backend` |
| P5-07.04 | ⏳ 计划中 | 记录可行性决策；负责 `docs/adr/diesel-profile.md`。 | P5-07.02, P5-07.03 | 决策对账所有探针并说明延期/拒绝或单独界定的后续实现、所需语言/宏/后端/AOT 工作和成本；P5 退出证据保持不变。 | `p5-diesel-evaluation-v1#decision` |

<a id="p5-gate"></a>

## P5-GATE: 有限退出门禁

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P5-GATE.01 | ⏳ 计划中 | 冻结完整退出清单；负责 `tests/roadmap/p5-exit-v1.json`。 | P3-GATE, P2-06, P5-01, P5-02, P5-03, P5-04, P5-05 | 清单列举全部必需 API/feature/后端用例及四引擎 × 两 RID × 两运行时应用矩阵（16 个单元格）；每格都有不可变输入与用例分母。 | `p5-exit-v1#inventory` |
| P5-GATE.02 | ⏳ 计划中 | 收集独立本地与原生 CI 证据；负责 `eng/database/Collect-P5Evidence.ps1`。 | P5-GATE.01, P5-06 | 全部冻结清单零失败/零跳过，Release 零警告/零错误，IL 与 AOT 输出有效；报告标识同一最终 SHA 并保留引擎/运行时一致性和清理证据。 | `p5-exit-v1#evidence` |
| P5-GATE.03 | ⏳ 计划中 | 拒绝不完整聚合证据；负责 `tools/RustSharp.Conformance/Database/P5ExitGate.cs`。 | P5-GATE.02 | 缺失/重复单元格、分母变化、版本/SHA/RID 错误、伪造原生执行、服务不可用及缺失清理记录均失败；聚合不得静默接受更小矩阵。 | `p5-exit-v1#aggregator-negative` |
| P5-GATE.04 | ⏳ 计划中 | 发布门禁来源并更新状态；负责成对路线图/门面文档的 P5 部分。 | P5-GATE.03 | 固定分母附带不可变 CI 运行/制品链接与可复现命令；仅在 P5-01 至 P5-06 及全部门禁叶子通过后，两种语言才同步变更状态。 | `p5-exit-v1#publication` |
