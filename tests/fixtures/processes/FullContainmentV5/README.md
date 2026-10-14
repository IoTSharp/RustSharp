# FullContainmentV5 process fixture

English | [简体中文](README_zh.md)

Status: ✅ Complete for the bounded Windows fixture component. The seven source/project/control files are exact byte copies of the frozen v5 candidate. Its original Windows component controls are ✅ Complete at 6/6; the supervisor has also accepted a fresh strict build and all six controls from this new fixture path. This fixture is not connected to the production runner and does not close P1-GATE.04 or P1. Linux capability remains `UNKNOWN` and Linux targets are rejected before launch.

## Source and evidence

`SOURCE-MANIFEST.json` binds all seven copied files to their original SHA-256 values and original manifest `365D8B03CDA99E881B58F17D3B77EA3C80A3AF2963007DF1870C1C4FC5D574F6`. Source behavior, namespace `RustSharp.FullContainmentV5` and assembly `FullContainmentV5` are unchanged. No `bin`, `obj` or native output is included.

The original supervisor evidence is under `artifacts/p1-supervision/resume-root-v45/`: `native-held`, `native-root-first-exit`, `native-timeout-three`, `native-cancel-three`, `native-transient-member` and `native-inventory`. These original results establish only the original candidate component. The fresh strict build, DLL/PDB hash binding and all six controls from this source path are recorded in docs/evidence/p1/full-containment-windows-fixture-v1.json. Earlier failed candidates and their original reports remain preserved.

## Fixed controls and limits

There are exactly six controls: five runtime controls and one structural negative. `CONTROLS.json` is the fixed denominator. The active and cumulative process caps remain three; each raw output stream is limited to 65,536 bytes. A control needs a fresh exact absolute caller lease root. Run at most six cases within a 100-second batch budget, with bounded owned capture, cancellation and cleanup.

| Case | Required result |
| --- | --- |
| `inventory-mutation` | Structural cumulative-accounting mismatch is rejected; zero native targets. |
| `held-three-natural` | Three retained identities; natural execution and complete containment pass. |
| `root-first-exit` | Natural root exit; descendant Job reap and complete containment pass; execution remains false. |
| `timeout-three` | Timeout is retained; complete containment passes; execution remains false. |
| `cancel-three` | Cancellation is retained; complete containment passes; execution remains false. |
| `transient-member` | Actual cumulative count two with an incomplete identity inventory; Job zero passes while full containment and execution remain false. |

Do not increase the cap or filter `conhost` or any other member. If the transient child is actually captured, keep the failed negative control. Job zero alone is insufficient. Every claimed member needs the original retained handle, creation time, command and parent identity, terminal exit and handle-close proof. Unknown identities, PID reuse and unexplained counters continue to reject complete containment.

## Build and execute

The supervisor owns compilation and native execution. Use the repository's strict settings, SDK 10.0.400 in CI or explicitly recorded local SDK 10.0.402, fresh exclusive build outputs, one build worker and no shared compiler. Do not change the repository SDK pin. Example MSBuild response-file content, evaluated from the repository root:

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

Invoke the known absolute SDK MSBuild entry point through an owned bounded capture. Then use the DLL host, starting with `inventory-mutation` and `held-three-natural`, and retain every raw report:

```text
dotnet <exclusive-build-root>/bin/FullContainmentV5.dll control <case-name> <fresh-exact-absolute-lease-root>
```

Each runtime invocation has a finite deadline and preserves separate binary stdout/stderr. Each lease retains `OWNER.json`, `control.json`, `report.json` and fixture identity receipts. The mutation control emits `control.json` without launching native targets. Keep primary timeout/cancellation outcomes separate from containment proof.

## Cleanup and remaining scope

The root is suspended, assigned to an exclusive kill-on-close Job without breakaway, then resumed. Root and fixture descendants use `DETACHED_PROCESS`. The candidate preserves full cumulative identity accounting and same-handle exit-race checks. After a verified owned Job reap, it waits for retained process handles to become terminal instead of repeating root termination; ordinary unproved cleanup preserves termination errors. Terminal waits are bounded to 120 checks/3,000 ms per member and 10,000 ms for the retained-member proof stage. Job reaping has its own 20-check/10-second limit.

Linux requires separately established atomic admission, an exclusively owned escape-resistant cgroup/manager boundary and complete identity/cleanup evidence; a process group is insufficient. Linux support, production integration, current-SHA native CI and the remaining P1 gates are independent work. `OriginalP1ScopePassed`, production integration and `fullP1Closure` remain false.
