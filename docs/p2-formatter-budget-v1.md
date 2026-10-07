# Formatter parser budget race repair

English | [简体中文](p2-formatter-budget-v1_zh.md)

## Delivery

✅ Complete for the deadline race repair. When a formatter budget expired between
two clock samples, the first check could succeed and the second sample could
produce a nonpositive parser timeout. CI run `37702329159` at `4d33334` exposed
this in Linux `P2-08.02 format-budget`. Remaining now checks cancellation,
calculates the budget from one elapsed sample and rejects that same value when
it is nonpositive. The existing timeout handler returns RSTF0001 and preserves
the original source. Parser option validation and all timing limits remain intact.

## Acceptance

Candidate `0004e750d80900d2881d7ea680ce9a1b26228aec`, tree
`8c0e49054af4bff4da25c3a880ea19450744e68e`, binds 707 compiler inputs. Fresh
SDK 10.0.401 Release passes with zero warnings/errors. Windows passes tiny1,
formatter59 and a separate MIR or-pattern diagnostic1. Ubuntu passes tiny1 and
formatter59 using the portable assembly built on Windows; this is local Ubuntu
runtime verification, not native Linux CI build evidence. The existing budget
case includes 32 bounded one-tick timeout assertions, without changing the
formatter59 or parent91 denominators. Registration remains 1203.

The [acceptance archive](evidence/p2/formatter-budget-v1-archive.json) retains
source/report hashes. Root checked 70 recorded Windows identities and 62 Ubuntu
identities absent. These checks cover recorded launchers and workers; they do
not establish trusted containment of every descendant.

## Remaining closure

🚧 In progress for P2-08.02: real Windows directed cancellation, actual async
I/O interruption and complete same-pushed-SHA acceptance remain required. P2
remains 8/101 ✅ Complete leaves; no P1 gate, parent or phase closes.

The old CI run failed both expanded jobs at 1202/1203 with zero unexecuted
cases. Windows formatter59 passed; its separate MIR binding or-pattern test
failed with a type-analysis work/nesting/time-limit diagnostic after 6.0166s.
The fresh local diagnostic1 passes, but no Windows CI failure repair or cause
is claimed. Preserve the failed reports and follow up on the new SHA.
