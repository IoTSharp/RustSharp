# FullContainmentV5 进程夹具

[English](README.md) | 简体中文

状态：✅ 已完成有界 Windows 夹具组件。七份源码/项目/控制文件均为冻结 v5 候选的逐字节副本。原路径 Windows 组件控制已 ✅ 完成，结果为 6/6；监督者也已验收新夹具路径的全新严格构建和全部六项控制。本夹具尚未接入生产执行器，不关闭 P1-GATE.04 或 P1。Linux 能力保持 `UNKNOWN`，Linux 目标在启动前即被拒绝。

## 来源与证据

`SOURCE-MANIFEST.json` 将七份复制文件绑定到原始 SHA-256，以及原清单 `365D8B03CDA99E881B58F17D3B77EA3C80A3AF2963007DF1870C1C4FC5D574F6`。源码行为、命名空间 `RustSharp.FullContainmentV5` 和程序集 `FullContainmentV5` 均未改变。不包含 `bin`、`obj` 或 native 输出。

监督者原始证据位于 `artifacts/p1-supervision/resume-root-v45/`：`native-held`、`native-root-first-exit`、`native-timeout-three`、`native-cancel-three`、`native-transient-member` 和 `native-inventory`。这些原始结果仅证明原路径候选组件。新源码路径的严格构建、DLL/PDB 哈希绑定和全部六项控制记录于 docs/evidence/p1/full-containment-windows-fixture-v1.json。此前失败候选及其原始报告继续保留。

## 固定控制与限制

固定六项控制包含五项运行时控制和一项结构负例。`CONTROLS.json` 是固定分母。活动与累计进程上限均保持三；每个原始输出流限制为 65,536 字节。每项控制需要全新、精确的调用者租约绝对根路径。整批最多六项、墙钟预算 100 秒，并使用有界归属捕获、取消和清理。

| 用例 | 必需结果 |
| --- | --- |
| `inventory-mutation` | 拒绝结构性的累计计数不一致；不启动 native 目标。 |
| `held-three-natural` | 保留三个身份；自然执行和完整 containment 通过。 |
| `root-first-exit` | 根进程自然退出；后代 Job 回收和完整 containment 通过；执行结果保持 false。 |
| `timeout-three` | 保留超时；完整 containment 通过；执行结果保持 false。 |
| `cancel-three` | 保留取消；完整 containment 通过；执行结果保持 false。 |
| `transient-member` | 实际累计计数为二且身份库存不完整；Job 归零通过，但完整 containment 和执行结果保持 false。 |

不得提高上限，也不得过滤 `conhost` 或任何其他成员。若实际捕获到了瞬时子进程，必须保留该负控失败。仅 Job 归零不足以证明完整 containment。每个被证明成员都需要原始保留句柄、创建时间、命令与父身份、终态退出及句柄关闭证明。未知身份、PID 复用和无法解释的计数继续拒绝完整 containment。

## 构建与执行

监督者负责编译和 native 执行。使用仓库严格设置、CI 中的 SDK 10.0.400 或明确记录的本机 SDK 10.0.402，以及全新独占构建输出、单构建 worker 和非共享编译器。不得修改仓库 SDK 固定值。以下为在仓库根目录解析的 MSBuild 响应文件示例：

```text
tests/fixtures/processes/FullContainmentV5/FullContainmentV5.csproj
-t:Restore,Build
-p:Configuration=Release
-p:BaseIntermediateOutputPath=<exclusive-build-root>/obj/
-p:OutputPath=<exclusive-build-root>/bin/
-p:AppendTargetFrameworkToOutputPath=false
-p:UseSharedCompilation=false
-m:1
-nodeReuse:false
-warnaserror
```

通过有界归属捕获调用已知绝对路径的 SDK MSBuild 入口。随后使用 DLL 宿主，从 `inventory-mutation` 和 `held-three-natural` 开始，并保留全部原始报告：

```text
dotnet <exclusive-build-root>/bin/FullContainmentV5.dll control <case-name> <fresh-exact-absolute-lease-root>
```

每次运行时调用具有有限截止时间，并分别保留二进制 stdout/stderr。每个租约保留 `OWNER.json`、`control.json`、`report.json` 和夹具身份证据。mutation 控制仅输出 `control.json`，不启动 native 目标。主要超时/取消结果与 containment 证明必须区分。

## 清理与剩余范围

根进程先挂起，加入禁止 breakaway、关闭即回收的独占 Job，再恢复运行。根与夹具后代使用 `DETACHED_PROCESS`。候选保留完整累计身份计数和同句柄退出竞争检查。已证明专属 Job 回收后，等待保留进程句柄进入终态，避免重复终止根进程；普通未证明清理仍保留终止错误。每个成员的终态等待最多 120 次/3,000 ms，保留成员证明阶段合计最多 10,000 ms。Job 回收另有 20 次/10 秒限制。

Linux 需要独立建立原子入组、独占且防逃逸的 cgroup/manager 边界及完整身份/清理证据；进程组不足以满足合同。Linux 支持、生产集成、当前 SHA 的原生 CI 和其余 P1 门禁均为独立工作。`OriginalP1ScopePassed`、生产集成及 `fullP1Closure` 保持 false。
