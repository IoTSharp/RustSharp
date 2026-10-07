# P1 production receipt diagnostics

English | [简体中文](p1-receipt-diagnostic-v1_zh.md)

## Delivery and acceptance

✅ Complete for this diagnostic repair: a failed report validator with no
validated hash preserves its original error and explains that successful
validation is unavailable. A validator that passed but produced a different
report hash still reports changed bytes. Equal hashes produce no false alarm.
The existing four-stage requirement and receipt closure predicate remain intact.

Candidate `7621c378b3d622cece4c815564130bd99412c2a2`, tree
`ae5a1e406e4a95a9871a73afef97a12ba4b1d971`, binds 696 source inputs.
PowerShell 7.6.6 passes the one-case trial, all three frozen diagnostic controls
and all eight inherited CI-publication controls. The diagnostic controls extract
the exact candidate, budget and closure expressions from the real production
script. They use synthetic hashes and deliberately retain `closed=false`;
they do not execute a producer or accept a phase gate. Both exclusive control
sandboxes were removed. The manual P1 workflow runs these controls as preflight
on Windows and Linux.

The [acceptance archive](evidence/p1/receipt-diagnostic-v1-archive.json) records
actual source hashes, report hashes and source-snapshot binding. Original reports
and launcher identities remain in `artifacts/p1-supervision/receipt-diagnostic-root-v1/`.
The [three-control report](evidence/p1/receipt-diagnostic-v1-controls.json)
preserves the observed before/after messages and their explicit utility scope.

## Reproduction and remaining closure

✅ Complete for the hidden-directory cleanup repair: run `37699582503` at
`264416fc3898b63e1e6ed54d4c75c45c432572f6` passed all three Linux utility
assertions, then failed to inspect its hidden `.utility-owned-*` sandbox.
Both exact sandbox operations now use `-Force`, preserving the task owner,
PID, absolute path, reparse-point, five-file and five-second cleanup checks.
Candidate `b630a6873383feb1ec09dafd970cca187fb0fc5c` binds 703 inputs.
Windows and Ubuntu PowerShell 7.6.6 each pass trial 1/1 and fixed controls 3/3,
with all four sandboxes removed. The [cleanup archive](evidence/p1/receipt-cleanup-v1-archive.json)
retains hashes and process observations. These are utility checks; fresh native
Windows/Linux CI at the pushed repair SHA remains required.

Create an exclusive output directory, then run with PowerShell 7:

```powershell
./eng/Test-P1ProductionReceiptDiagnostics.ps1 -ResultPath artifacts/receipt-controls/controls.json
```

🚧 In progress: same-pushed-SHA native CI, NativeV5 receipt and joint-gate
integration, independent descendant containment and all six P1 phase gates.
This repair does not establish a real artifact mismatch or close a P2 leaf.
