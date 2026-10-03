# P1-08 Drop 与 panic 契约 v1

[English](p1-drop-contract-v1.md) | 简体中文

本文固定生成清理证据使用的有限状态转移契约。它有意与 Rust 运行时的进程策略
分开：编译器决定哪些 place 仍存活，宿主决定如何呈现 abort。

## Place 状态与事件

每个拥有值的 place 从 `uninitialized` 开始。初始化成功后变为 `live`。整体移动使其
变为 `moved`，移动单个字段使父值变为 `partially_moved`；这两种状态都不会把已移动
值再次安排析构。离开作用域或 unwind 会把 live 值消费为 `dropped`。abort 不执行
unwind 清理。

| 用例 ID | 起始状态 | 事件 | 目标状态 | 立即 Drop | 终止 |
| --- | --- | --- | --- | --- | --- |
| DROP-INIT | uninitialized | initialize | live | 否 | 否 |
| DROP-MOVE | live | move | moved | 否 | 是 |
| DROP-PARTIAL | live | partial_move | partially_moved | 否 | 否 |
| DROP-REPLACE | live | assign_replace | live | 是（旧值） | 否 |
| DROP-SCOPE | live | scope_exit | dropped | 是 | 是 |
| DROP-RETURN | live | return_move | moved | 否 | 是 |
| DROP-UNWIND | live | panic_unwind | dropped | 是 | 是 |
| DROP-ABORT | live | panic_abort | live | 否 | 否 |
| DROP-UNINIT-UNWIND | uninitialized | panic_unwind | dropped | 否 | 是 |
| DROP-MOVED-UNWIND | moved | panic_unwind | dropped | 否 | 是 |
| DROP-DROPPED-UNWIND | dropped | panic_unwind | dropped | 否 | 是 |
| DROP-UNINIT-EXIT | uninitialized | scope_exit | dropped | 否 | 是 |
| DROP-MOVED-EXIT | moved | scope_exit | dropped | 否 | 是 |
| DROP-PARTIAL-EXIT | partially_moved | scope_exit | dropped | 是（仍存活字段） | 是 |
| DROP-DROPPED-EXIT | dropped | scope_exit | dropped | 否 | 是 |
| DROP-DROPPED-ABORT | dropped | panic_abort | dropped | 否 | 是 |

生成的局部值按声明逆序清理。递归聚合 drop glue 不同：先调用外层析构器，再按声明顺序
调用字段/元素。未激活的 enum payload 以及已移动或未初始化的字段均跳过。

## panic 与析构失败

正常清理会按顺序尝试所有剩余 live 值。首次析构失败会被保留，后续失败不会替换它。
panic unwind 期间首次析构失败属于双重 panic：清理停止，同时保留原始 panic 与析构
异常，结果为 `Aborted`。`RustPanicBoundary` 报告此结果但不调用
`Environment.FailFast`，因此有界测试宿主可以检查结果并按自身策略终止进程。abort panic
保持 drop scope 不变，并且不调用 unwind `Drop`。

## 稳定用例 ID

`DROP-*` ID 是 P1-08.01 的不可变转移分母。机器可读快照由
`SafeCoreMirDropContract.Snapshot()` 生成，profile 为
`safe-core-drop-contract-p1-v1`；任何行或失败策略变化都必须升级 profile 版本并同步
更新夹具。
