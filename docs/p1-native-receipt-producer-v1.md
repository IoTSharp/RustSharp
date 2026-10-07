# P1 NativeV5 receipt producer

English | [简体中文](p1-native-receipt-producer-v1_zh.md)

## Integrated implementation

🚧 In progress: `eng/Invoke-P1NativeDropEvidence.ps1` is integrated for a future
authenticated `drop-native` Actions job. It retains the frozen 28 generated
cases, 30 original PE verifications, 28 AOT programs, two callable artifacts,
seven callable cases and 36 stage records. It passes caller cancellation to
the shared process wrapper and captures the exact opened report/PE bytes.
Report reads share the strict parser's 32MiB bound. Reads, hashing, writes and
flushes check cancellation and the same ten-second monotonic deadline before
accepting or publishing their result. Exclusive receipt publication preserves
an existing destination and reclaims only its owned temporary.

## Utility acceptance

✅ Complete for the frozen utility checks: PowerShell 7.6.6 passes the one-case
trial and all eight controls against functions extracted from the real producer.
They verify captured-byte hashes, byte/path rejection, pre-cancellation,
opened-stream consistency, the explicit closeout window, its deadline rejection,
and preservation of existing proof. Synthetic bytes and an injected elapsed
clock are labelled utility inputs; no native child, ILVerify or AOT executes.

Candidate `d53a3cf3352f21de325de8f07bc842d8e1dcfe84`, tree
`24f010b510e54105c891fc6d947a9a8082a75482`, binds 698 source inputs.
The [utility archive](evidence/p1/native-receipt-producer-v1-archive.json)
records source/report hashes and the owned sandbox/launcher checks. Reproduce
with an exclusive existing output directory and a fresh result path:

```powershell
./eng/Test-P1NativeDropReceiptUtilities.ps1 -ResultPath artifacts/native-receipt-controls/fixed.json
```

## Remaining acceptance

🚧 In progress: workflow job/upload wiring, original 30 verifier-report transport,
atomic transport publication, the mandatory joint packet and twelve genuine
evidence rejection controls, and fresh same-SHA native execution on both platforms.
The production context is not simulated to bypass its authentication checks.
Process `CleanupComplete` remains parent-exit-only; independent descendant
containment and final C# I/O-clock integration remain open. Neither this producer
integration nor its utility controls close a P1 phase gate or P2 leaf.
