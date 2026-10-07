# ADR 0010：版本化 .NET 导入与普通类库导出边界

[English](0010-dotnet-interop-v1.md) | 简体中文

决策状态：接受为 `P2-06.01` 设计契约。`P2-06.02` 至 `P2-06.06` 的实现仍为
⏳ 计划中；本 ADR 没有记录已执行的互操作或平台验收。

## 背景

P0-15 与 ADR 0006 已证明显式静态 `ManagedInterop.Call<TInput,TResult>` 运行时映射。
P1-09 提供源码包元数据与所有权契约。这些证据不能证明源码级 .NET 导入、普通 C#
消费者或任意 NuGet 兼容性。P2-06 要求双向调用、精确成员选择、所有权/空值/异常
边界，以及两个原生 x64 平台。

## 决策

实现之前冻结 [`dotnet-interop-v1`](../../tools/RustSharp.Conformance/fixtures/p2-dotnet-interop-v1-manifest.json)。
清单命名 8 个成员契约、7 个类型映射、12 个家族与 36 个用例契约（每个家族 3 个），
包含不支持情形的结果。规范化契约 SHA-256 为
`AFA13CE319C44DB2E0A9BF6F81DE65D8929656AFEC20AF241C6AB03D5B689C56`。
`DotNetInteropContract.Validate` 检查这份不可变设计身份，并单独输出原始清单哈希。
已验证记录属于清单审计；`RuntimeEvidence` 始终为 `false`。改变成员、用例 ID、
诊断、平台或限制，必须创建新配置版本与明确的新工作。

### 语法与成员身份

未来的源码导入形式是显式的：

```rust
#[dotnet_import(assembly = "InteropFixtures", type = "InteropFixtures.Math",
                member = "Add", signature = "System.Int32(System.Int32,System.Int32)")]
extern "dotnet" {
    pub fn add(left: i32, right: i32) -> i32;
}
```

普通 .NET 导出形式指定精确 CLR 名称：

```rust
#[dotnet_export(namespace = "RustSharp.Interop", type = "Exports", member = "Add")]
pub fn add(left: i32, right: i32) -> i32 { left + right }
```

这些形式是 P2-06.02/.04 的语法承诺，目前编译器尚未接受该语法。程序集/引用锁定、
声明类型、成员、调用形式、完整签名与封闭泛型参数共同标识唯一方法。公共静态方法
直接绑定；构造函数、属性和实例行为必须经过具体命名的可复用静态适配器。V1 包含
`System.Math.Abs(i32)`、fixture Add/Identity/Counter 适配器、普通公共 Add/Pair
导出以及固定版本的 StringSegment 适配器。重复别名、歧义重载、不可访问成员、
未列出签名和开放泛型分别返回 `RSDN1002`、`RSDN1004`、`RSDN1003`、`RSDN1005`、
`RSDN1006`。没有隐式装箱或重载选择。封闭特化之前检查元数据泛型约束，不进行
运行时泛型构造。

### 类型、所有权、空值与异常

`i32`、`bool` 与仅返回位置的 `()` 分别映射为 `System.Int32`、`System.Boolean`、
`System.Void`。公共 `Pair` 恰有两个公共顺序布局的 i32 字段，CLR 名称固定。
`dotnet::String` 使用显式 UTF-8/UTF-16 转换；无效 UTF-8 返回已声明的转换错误。
字符串表示不得在边界两侧静默复用。

`dotnet::Object<T>` 表示拥有所有权的托管句柄，通过显式适配器元数据规定消费和
释放。`&dotnet::Object<T>` 是调用范围内不可逃逸的共享借用；
`&mut dotnet::Object<T>` 是独占借用。源码移动、别名、逃逸、逆序清理及恰好一次
Drop 检查继续由生产语义流水线负责。GC 生命周期不能替代释放契约。保留/逃逸借用
或不一致的释放契约返回 `RSDN1007`。既有 Rust# 源码包元数据保持 P1 ABI；普通
.NET 签名需要显式适配器及独立的发射清单。

`Option<dotnet::Object<T>>` 通过已声明的空值适配器映射可空句柄。非空句柄在
入口/返回位置检查 null，并按照声明的错误策略引发 `RustDotNetNullViolation`。
缺少可空元数据不能静默获得非空行为。`#[dotnet_error(result)]` 只把清单声明的
异常类型映射为稳定的 `Result<T, dotnet::Error>` 类别，包括 Overflow；本地化
异常消息不能作为 oracle。`#[dotnet_error(panic)]` 进入现有 unwind/abort 配置，
保留 Drop 和双 panic 行为。未列出异常遵循该 panic 配置；不隐含回调支持。

### NuGet、AOT 与排除项

首个 NuGet 成员是可复用的公共静态 StringSegment.Length 适配器，使用
`Microsoft.Extensions.Primitives` 版本 `10.0.0`。执行之前，P2-05 必须提供不可变
包/引用 SHA-256 锁定及完整兼容依赖闭包；本 ADR 不编造包哈希，也不证明该包的 AOT
兼容性。缺失、变化或不兼容的身份返回 `RSDN1008`。适配器必须执行真实的固定版本
API；仅宿主模拟或生成 C# 程序逻辑不能满足 P2-06.05。

反射发现、`RequiresUnreferencedCode`、`RequiresDynamicCode`、Reflection.Emit、
运行时 MakeGenericMethod 及传递动态依赖在发布之前返回 `RSDN1009`。V1 排除
非托管/COM/原生 ABI、裸指针、可变参数、委托/函数指针回调、隐式事件绑定及隐式
实例分派，诊断为 `RSDN1010`。未知成员仍需返回固定诊断；这些排除项没有移除父任务
要求的任何家族。发射继续经过 typed MIR、已验证 CLR LIR、直接 PE 和 Portable PDB。
既有显式运行时与 `ClrLirExternalCall` 是基础；所需的新源码/类型/实例能力必须
分别实现。

## 验收与证据

`test:interop-contract` 注册 8 个 harness 测试。清单命令筛选 `P2 interop contract`，
每个测试限制 10 秒，总 deadline 为 120 秒。这些测试验证 36 条设计记录与变异拒绝，
执行的导入/导出语义用例为零。验证最多接受 262144 UTF-8 字节、深度 16、36 条记录
与 2000 毫秒；解析之前和每次有界记录迭代都检查取消。未来绑定/运行时限制为
32 个引用程序集、每程序集 16777216 元数据字节、256 个候选成员、16 个泛型参数及
256 个边界参数。未来用例限制 30 秒，suite deadline 为 1200 秒，每用例输出最多
1048576 字节。

| 叶子任务 | 后续必需证据 |
| --- | --- |
| P2-06.02 | 精确源码语法、元数据绑定与诊断/span 用例。 |
| P2-06.03 | 生成程序的所有权、空值、转换、异常及 Drop 用例。 |
| P2-06.04 | 普通方法/字段元数据、独立 C# 编译器/运行时消费者，以及确定性 PE/PDB。 |
| P2-06.05 | 真实锁定 NuGet 适配器调用及动态可达性的拒绝。 |
| P2-06.06 | 在原生 `win-x64`、`linux-x64` 上，双向通过 CoreCLR、原始 PE ILVerify 及实际 Native AOT。 |

每份运行时报告必须绑定用例/成员 ID、配置与清单哈希、commit/编译器/引用/包哈希、
工具版本、原始 PE 身份、真实原生宿主/RID 和命令。检查真实输出、退出码与诊断；
基础设施失败不能算作语义拒绝。所有必需单元都必须记账，失败、阻塞、跳过及被抑制的
AOT 警告均为零。保留有界输出/deadline、PID/启动时间/父进程/命令、完整任务所属
进程清理，并只核对任务拥有的临时路径。P1-GATE 与完整 P2 父任务/门禁证据仍是
独立要求。

## 影响

源码导入/导出及边界实现可按固定、可审查的契约推进。既有公共 API 与 P1 分母保持
不可变。新增方法、类型、空值/异常适配器、平台组合或回调，需要新的清单版本；设计
验证成功不能改变这些能力的实现状态。
