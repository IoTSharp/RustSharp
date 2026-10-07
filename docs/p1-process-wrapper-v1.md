# P1 process wrapper cancellation repair

English | [简体中文](p1-process-wrapper-v1_zh.md)

## Delivery

✅ Complete for this repair: `eng/Invoke-BoundedProcess.ps1` accepts a caller
`CancellationToken`, refuses a pre-cancelled launch, checks cancellation while
waiting and draining output, and retains primary errors when cleanup also fails.
Two capture streams are tracked separately. The ledger records OS creation time
when available, full arguments and parent identity; failed identity observation
is explicitly unverified. Waits use both finite checks and monotonic deadlines.

## Acceptance

PowerShell 7.6.6 on Windows x64 passed the one-case trial, all six frozen controls
(pre-cancel, midrun cancel, normal exit, exit 23, timeout, excess output), a separate
one-case failure trial, and both failure controls. Holding the process-ledger file
open makes cleanup fail: exit 0 reports an aggregate error; exit 23 preserves the
original error and attaches cleanup failures. The existing eight publication
contract controls also pass. These are utility controls, not generated-language
or native-platform closure evidence.

Candidate `2e38438404e112b6afe3ae39d635cc5d670f55cc`, tree
`8ecfd1bcd19e004f5d3d503eb0a7b22bbe84d5bb`, binds all 693 compiler inputs.
The [acceptance archive](evidence/p1/process-wrapper-v1-archive.json) retains
report hashes and exact source hashes. Original failed trial logs are preserved
under `artifacts/p1-supervision/process-wrapper-root-v1/`; its scalar-array test
bug was corrected before the successful failure trials. No C# source changed.

The independent review found scalar unpacking of a single capture exception.
The final source explicitly preserves an array and reran the six plus two
controls. A single capture-stream fault was not injected; that branch has static
review only. Root reconciled 23 final recorded process identities as absent.

## Reproduction

Run with PowerShell 7 on Windows, using fresh result paths:

```powershell
./eng/Test-P1BoundedProcessCancellation.ps1 -WrapperPath "$PWD/eng/Invoke-BoundedProcess.ps1" -ResultPath "$PWD/artifacts/cancel-controls.json"
./eng/Test-P1BoundedProcessFailureRetention.ps1 -ResultPath "$PWD/artifacts/failure-controls.json"
```

The six-control runner removes its exclusive temporary sandbox. The failure
runner retains its exclusive fixtures, identities and captured output beside
its report as review artifacts. Their child fixtures create no further descendants.

## Remaining closure

🚧 In progress: independent descendant containment/reclamation, producer-level
token propagation, the NativeV5 receipt and joint gate integration, and native
Windows/Linux evidence. `CleanupComplete` and `RootProcessExited` verify only the
root process; `CleanupVerification=parent-exit-only` makes that limit explicit.
Asynchronous capture detects output-budget excess, but does not promise a hard
disk-write ceiling. This repair closes no P1 phase gate or P2 leaf.
