# 回归测试运行器证据 v2

[English](regression-harness-v2.md) | 简体中文

状态：✅ 已完成。共享 Release 构建为零警告/错误，九项隔离 C# harness 检查和脚本控制
全部通过。P1/P2 阶段门禁仍未收口。

## 版本化容量

P1 和 P2 保留每项注册测试的独立执行。已验收的 P1 修复后，原 1024 项注册容量
只剩一个位置；仅 P2-04.02 就新增 38 项已声明的清单用例。因此运行器为
`p1-regression-registration-inventory` 和 `p1-full-regression-harness` 同时写出
schema 版本 `2`，有限注册上限为 4096。第 1 版读取规则仍保留 1024 上限；未知版本拒绝。

此变更调整容量，不调整验收分母。精确注册清册、顺序、ID、哈希和程序集身份仍来自
新鲜 Release `--list` 调用。完整测试必须在所需候选／树和原生 RID 上执行这些
注册项全部用例，无失败、跳过、未执行、取消或不完整清理。第 2 版报告声明
`bounds.maximumTests: 4096`；报告和新鲜清册必须使用相同 schema 版本。时间上限
仍为每个 worker 1–300 秒、每套测试 1–1800 秒，并支持取消和自有进程清理。
旧夹具／配置档分母和历史证据不变。

## Worker 清理证据

正常子进程退出可以记录 `processTreeCleanupAttempted: false`。干净退出后不需要
强制终止。覆盖映射现在接受这种真实结果，同时要求成功退出的 worker、记录的
PID／父进程／启动时间／命令参数、零退出码，无截断／排空超时／限量输出，且
`processTreeCleanupIncomplete: false`。此检查不替代完整测试、来源绑定、新鲜构建
清册或原生 ILVerify／AOT 要求。

## 验证

`P1HarnessCapacityTests` 有四项注册：1025／4096 项仅列举的单元夹具，4097 项溢出／
虚增闭环拒绝，来自 `docs/evidence/p1/native-v2.harness.json` 的实际保留正常 worker，
以及它的失败／输出／清理突变。虚构注册列表属于单元夹具，从不声称生成程序或平台执行。

```powershell
pwsh -NoProfile -File eng/Test-P1HarnessCapacity.ps1 -MaximumChecks 1 -DeadlineSeconds 5
pwsh -NoProfile -File eng/Test-P1HarnessCapacity.ps1 -MaximumChecks 9 -DeadlineSeconds 30
pwsh -NoProfile -File eng/Test-P1SuiteGateValidation.ps1 -MaximumChecks 43 -TimeoutSeconds 45
```

使用 PowerShell 7 或更高版本。所有生成夹具循环同时具有固定项目数和墙钟上限；
先运行最小单元试验，再执行完整脚本批次。脚本控制检查通过 9/9 项容量检查、43/43 项
继承控制检查和九项 Linux 报告控制检查。这些内存单元控制检查不是阶段闭环证据。
绑定源码的 9/9 C# 报告已归档至 `docs/evidence/p1/harness-capacity-v2.harness.json`，
原始和 LF 归档哈希见 `docs/evidence/p1/harness-capacity-v2-archive.json`。候选为
`e3dcfb631054b2d6e5c400523d72aaa29faa929d`；新鲜 v2 清册包含 1073 项注册。
这份过滤后的修复检查不属于完整测试套件证据。
