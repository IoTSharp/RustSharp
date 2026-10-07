[English](p1-production-ci-v1.md) | [简体中文](p1-production-ci-v1_zh.md)

# P1 native production CI v1

🚧 In progress: `.github/workflows/p1-expanded.yml` adds independent native Windows x64 and Linux x64 production jobs and a physical aggregate gate. Actual same-SHA CI execution is required before acceptance; P1 remains open.

Each native job builds with SDK 10.0.401 and retains candidate source/build bindings. SDK 10.0.400 is also installed to honor the repository pin for ordinary tool restore. Four serial stages execute and physically validate all 19 source packages/39 original PEs, then all six exact backend fixtures/seven original PEs. Each host must close its 12 backend cells. Reports, retained bytes, validator stdout, original runner/dotnet bytes, 12 implementation sources and owned process ledgers are uploaded with run/attempt-specific artifact names.

`eng/Test-P1ProductionCiGate.ps1` requires successful trusted upstream jobs, both complete native receipts at the same candidate/tree/run/attempt, matching candidate source bytes and all transported artifact hashes. It invokes the production physical backend matrix validator and requires all 24 cells. The existing suite gate still requires its original seven reports per platform and 15 checks; the final production gate takes their conjunction. It retains `fullP1Closure=false` because language, complete harness and other P1 phase obligations still require reconciliation.

Native orchestration is bounded at 3,900 seconds/four stages, with 90-minute jobs. Aggregate validation is bounded at 480 seconds/two hosts, with a 30-minute job. Every long child records PID, parent, start, arguments, exit and cleanup; task-owned trees are reclaimed. Retained artifacts are preserved. Cancellation reaches orchestration; searches use explicit inventories.

✅ Complete: both scripts pass PowerShell 7 AST checks. Four [local rejection controls](evidence/p1/production-ci-guards-v1.json) confirm rejection of absent Actions context, failed upstream jobs and missing fresh physical build. All four owned child processes exited and were reclaimed. These controls use deliberately incomplete local contexts and do not establish native CI success. Independent real CI acceptance remains ⏳ Planned.
