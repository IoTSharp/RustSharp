[CmdletBinding()]
param([ValidateRange(1,60)][int] $TimeoutSeconds=45, [ValidateRange(1,43)][int] $MaximumChecks=43)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clock=[Diagnostics.Stopwatch]::StartNew()
$checks=0
$candidate='a'*40; $tree='b'*40
function Assert-SuiteResult([object[]] $Errors,[string] $Pattern='') {
    if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds -or $script:checks -ge $MaximumChecks) { throw 'Suite validation test bounds exhausted.' }
    if ($Pattern) { if (@($Errors | Where-Object { $_ -match $Pattern }).Count -eq 0) { throw "Expected rejection '$Pattern': $($Errors -join '; ')" } }
    elseif ($Errors.Count -ne 0) { throw "Positive control failed: $($Errors -join '; ')" }
    $script:checks++
}
function Copy-Fixture($Fixture) { return $Fixture | ConvertTo-Json -Depth 24 | ConvertFrom-Json }
$provenance=[ordered]@{candidateSha=$candidate;candidateTreeSha=$tree;candidateMatchesWorkingTree=$true;checkedFileCount=1;errors=@()}
$ids=@(for ($index=0;$index -lt 968 -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds;$index++) { 'synthetic-'+$index })
$inventory=[ordered]@{schemaVersion=1;evidenceKind='p1-regression-registration-inventory';runtimeIdentifier='win-x64';buildConfiguration='Release';assemblySha256='B'*64;registeredDenominator=968;registeredIds=$ids;registeredIdsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ids -join "`n")))}
$build=[ordered]@{testsAssemblySha256='B'*64;registrationInventory=$inventory}
$worker=@{startedProcess=@{processId=1;parentProcessId=2;startedAt='2026-10-06T00:00:00Z';commandLine='synthetic worker only'};exitCode=0;termination=0;succeeded=$true;outputTruncated=$false;outputReadTimedOut=$false;outputDrainTimedOut=$false;outputReadLimitReached=$false;processTreeCleanupIncomplete=$false}
$harness=[ordered]@{schemaVersion=1;evidenceKind='p1-full-regression-harness';candidateSha=$candidate;fullSuite=$true;suiteSucceeded=$true;buildConfiguration='Release';processIsolated=$true;cleanupComplete=$true;runtimeIdentifier='win-x64';deadlineExpired=$false;cancelled=$false;harnessError=$null;sourceProvenance=$provenance;assemblySha256='B'*64;registeredIds=$ids;registeredIdsSha256=$inventory.registeredIdsSha256;summary=@{registeredDenominator=968;selected=968;executed=968;passed=968;failed=0;skipped=0;notExecuted=0};cases=@(for ($index=0;$index -lt 968 -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds;$index++) { @{id=$ids[$index];status='passed';durationMilliseconds=1;error=$null;process=$worker} })}
Assert-SuiteResult @(Test-P1HarnessEvidence (Copy-Fixture $harness) $candidate $tree '' $build)
if ($MaximumChecks -eq 1) { Write-Output '✅ Complete: one bounded positive-control trial.'; return }
$value=Copy-Fixture $harness; $value.fullSuite=$false
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'Full Release harness'
$value=Copy-Fixture $harness; $value.cases=$value.cases[0..462]
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'fixed registered denominator'
$value=Copy-Fixture $harness; $value.cases[1].id=$value.cases[0].id
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'case.*duplicated'
$value=Copy-Fixture $harness; $value.summary.notExecuted=1
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'notExecuted must be zero'
$value=Copy-Fixture $harness; $value.sourceProvenance.candidateTreeSha='c'*40
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'source/tree provenance'
$value=Copy-Fixture $harness; $value.registeredIdsSha256='F'*64
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'identity hash is stale'
$value=Copy-Fixture $harness; $value.cases[0].process=$null
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'worker process identity/result is incomplete'
$value=Copy-Fixture $harness; $value.registeredIds=$value.registeredIds[0..463]; $value.cases=$value.cases[0..463]
$value.registeredIdsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.registeredIds -join "`n")))
foreach ($field in @('registeredDenominator','selected','executed','passed')) { $value.summary.$field=464 }
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'complete fresh Release registration inventory'
$value=Copy-Fixture $harness; $value.assemblySha256='F'*64
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'assembly differs from the fresh Release'
$value=Copy-Fixture $harness; $value.registeredIds[0]='invented'; $value.cases[0].id='invented'
$value.registeredIdsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.registeredIds -join "`n")))
Assert-SuiteResult @(Test-P1HarnessEvidence $value $candidate $tree '' $build) 'complete fresh Release registration inventory'
Assert-SuiteResult @(Test-P1HarnessEvidence (Copy-Fixture $harness) $candidate $tree '' $null) 'inventory or tests assembly binding is missing'
$value=Copy-Fixture $build; $value.registrationInventory.registeredIdsSha256='F'*64
Assert-SuiteResult @(Test-P1HarnessEvidence (Copy-Fixture $harness) $candidate $tree '' $value) 'inventory identity hash is stale'
$value=Copy-Fixture $build; $value.registrationInventory=$null
Assert-SuiteResult @(Test-P1HarnessEvidence (Copy-Fixture $harness) $candidate $tree '' $value) 'inventory or tests assembly binding is missing'
$value=Copy-Fixture $build; $value.testsAssemblySha256='F'*64
Assert-SuiteResult @(Test-P1HarnessEvidence (Copy-Fixture $harness) $candidate $tree '' $value) 'tests assembly binding is missing or invalid'

$bytes=[IO.File]::ReadAllBytes((Join-Path $root 'tools/RustSharp.Conformance/fixtures/p1-coverage-v1-manifest.json'))
$manifest=ConvertFrom-P1StrictJson $bytes
$coverage=[ordered]@{schemaVersion=1;evidenceKind='p1-requirement-coverage';profile='p1-coverage-v1';candidateSha=$candidate;manifestVersion=1;ledger='p1-exit-scope-v1';status='passed';manifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));summary=@{denominator=40;executed=40;passed=40;failed=0;blocked=0;skipped=0;caseDenominator=160};requirements=$manifest.requirements;cases=$manifest.cases}
Assert-SuiteResult @(Test-P1CoverageEvidence (Copy-Fixture $coverage) $root $candidate)
$value=Copy-Fixture $coverage; $value.cases[0].backends=@('harness')
$value.cases[4].backends=@('harness')
Assert-SuiteResult @(Test-P1CoverageEvidence $value $root $candidate) 'backend contract changed'
$value=Copy-Fixture $coverage; $value.requirements[1].id=$value.requirements[0].id
Assert-SuiteResult @(Test-P1CoverageEvidence $value $root $candidate) 'requirement.*stale, duplicated'
$value=Copy-Fixture $coverage; $value.cases[0].sourceSha256='F'*64
Assert-SuiteResult @(Test-P1CoverageEvidence $value $root $candidate) 'sourceSha256 is stale'
$value=Copy-Fixture $coverage; $value.summary.denominator=39
Assert-SuiteResult @(Test-P1CoverageEvidence $value $root $candidate) 'fixed denominator 40'
$value=Copy-Fixture $coverage; $value.manifestSha256='F'*64
Assert-SuiteResult @(Test-P1CoverageEvidence $value $root $candidate) 'manifest/ledger binding is stale'

$baseline=[ordered]@{schemaVersion=1;evidenceKind='p1-immutable-regression-audit';candidateSha=$candidate;succeeded=$true;baselineCommit='23279d93267a814c643baddc29c72918ff0fda0b';sourceProvenance=$provenance;summary=@{suites=4;denominator=60;passed=60;failed=0};suites=@()}
foreach ($pair in @(@('safe-core-regression-v1',8),@('safe-core-regression-v2',24),@('p1-differential-v2',16),@('p1-platform-v1',12))) {
    $baseline.suites += @{profile=$pair[0];denominator=$pair[1];manifestUnchanged=$true;caseIdsUnchanged=$true;expectationsUnchanged=$true;sourcesUnchanged=$true;cases=@(for ($index=0;$index -lt $pair[1];$index++) { @{id='synthetic-'+$index;status='passed'} })}
}
Assert-SuiteResult @(Test-P1BaselineEvidence (Copy-Fixture $baseline) $candidate $tree)
$value=Copy-Fixture $baseline; $value.suites[0].sourcesUnchanged=$false
Assert-SuiteResult @(Test-P1BaselineEvidence $value $candidate $tree) 'sourcesUnchanged is false'
$value=Copy-Fixture $baseline; $value.suites[1].profile=$value.suites[0].profile
Assert-SuiteResult @(Test-P1BaselineEvidence $value $candidate $tree) 'unexpected/duplicate suite'
$value=Copy-Fixture $baseline; $value.suites[0].cases[1].id=$value.suites[0].cases[0].id
Assert-SuiteResult @(Test-P1BaselineEvidence $value $candidate $tree) 'case is duplicated'

$build=[ordered]@{schemaVersion=1;evidenceKind='p1-release-build';candidateSha=$candidate;configuration='Release';succeeded=$true;testsAssemblyPath='tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll';testsAssemblySha256='B'*64;registrationInventory=$inventory;registrationInventoryPath='synthetic-only';registrationInventorySha256='C'*64;registrationInventoryProcess=@{ExitCode=0;CleanupComplete=$true;Pid=1;ParentPid=2;StartedAt='2026-10-06T00:00:00Z';FilePath='synthetic-only';Arguments=@('/synthetic/tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll','--list')};summary=@{warnings=0;errors=0};steps=@(@{name='restore';succeeded=$true;stdoutPath='synthetic-only';stderrPath='synthetic-only';process=@{ExitCode=0;CleanupComplete=$true;Pid=1;ParentPid=2;StartedAt='2026-10-06T00:00:00Z';FilePath='synthetic-only'}},@{name='build';succeeded=$true;stdoutPath='synthetic-only';stderrPath='synthetic-only';process=@{ExitCode=0;CleanupComplete=$true;Pid=1;ParentPid=2;StartedAt='2026-10-06T00:00:00Z';FilePath='synthetic-only'}})}
Assert-SuiteResult @(Test-P1BuildEvidence (Copy-Fixture $build) $candidate)
$value=Copy-Fixture $build; $value.summary.warnings=1
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'warnings must be zero'
$value=Copy-Fixture $build; $value.steps[1].process.CleanupComplete=$false
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'bounded process envelope'
$value=Copy-Fixture $build; $value.candidateSha='d'*40
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'candidate is invalid'
$value=Copy-Fixture $build; $value.registrationInventoryProcess.Arguments[1]='--filter'
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'inventory process/artifact binding is incomplete'
$value=Copy-Fixture $build; $value.registrationInventorySha256='invalid'
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'inventory process/artifact binding is incomplete'
$value=Copy-Fixture $build; $value.registrationInventory.registeredIds[1]=$value.registrationInventory.registeredIds[0]
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'invalid or duplicate IDs'
$value=Copy-Fixture $build; $value.registrationInventory=$null
Assert-SuiteResult @(Test-P1BuildEvidence $value $candidate) 'inventory or tests assembly binding is missing'

foreach ($json in @('{"candidateSha":"a","candidateSha":"b"}','{"Summary":{},"summary":{}}','{"nested":{"id":1,"id":2}}')) {
    $failure=$null
    try { $null=ConvertFrom-P1StrictJson ([Text.Encoding]::UTF8.GetBytes($json)) } catch { $failure=$_.Exception.Message }
    Assert-SuiteResult @($failure) 'Duplicate JSON property'
}
Assert-SuiteResult @(Test-P1SnapshotEvidence @{schemaVersion=1;verified=$true;candidateSha=$candidate;evidenceKind='p1-candidate-source-snapshot';scope='other'} $root $candidate) 'snapshot identity/verification'

$errors=[Collections.Generic.List[string]]::new()
$negative=[ordered]@{expectedOutcome='compile-fail';rustcCompile=@{commandLine='synthetic only';processId=1;parentProcessId=2;startedAtUtc='2026-10-06T00:00:00Z';exitCode=1;termination='exited';cleanupIncomplete=$false;outputTruncated=$false;outputReadTimedOut=$false;outputDrainTimedOut=$false;outputReadLimitReached=$false;standardError='tool unavailable';standardOutput=''};rustSharpCheck=$null;rustSharpCompile=$null;rustcRun=$null;rustSharpRun=$null}
Test-P1DifferentialCase $negative 'borrow-fail-mut-alias' $errors
Assert-SuiteResult @($errors.ToArray()) 'expected semantic diagnostic E0499'
$expandedBytes=[IO.File]::ReadAllBytes((Join-Path $root 'tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v2-manifest.json'))
$expanded=ConvertFrom-P1StrictJson $expandedBytes
$fixtures=@($expanded.suites | Where-Object profile -CEQ 'p1-platform-v2')[0].cases
$platform=[ordered]@{schemaVersion=1;candidateSha=$candidate;evidenceKind='p1-platform-coreclr-ilverify-native-aot';profile='p1-platform-v2';semanticClosureEligible=$true;compilerSha256='B'*64;compiler=@{sha256='B'*64};platform=@{runtimeIdentifier='win-x64';observedRuntimeIdentifier='win-x64';nativeExecution=$true;architecture='X64';oracle='rustc 1.98.0 (synthetic)';sdk='10.0.401';runtime='10.0.12'};manifest=@{version=2;denominator=24;validated=$true;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expandedBytes))};summary=@{status='passed';denominator=24;executed=24;passed=24;failed=0;blocked=0;skipped=0};execution=@{startedAtUtc='2026-10-06T00:00:00Z';finishedAtUtc='2026-10-06T00:00:01Z';deadlineExpired=$false};cleanup=@{completed=$true};cases=@()}
foreach ($fixture in $fixtures) {
    $output=Get-P1ExpectedPlatformOutput $fixture.id
    $process=@{commandLine='synthetic only';processId=1;parentProcessId=2;startedAtUtc='2026-10-06T00:00:00Z';exitCode=0;termination='exited';cleanupIncomplete=$false;outputTruncated=$false;outputReadTimedOut=$false;outputDrainTimedOut=$false;outputReadLimitReached=$false;standardOutput=$output;standardError='';outputMatches=$true}
    $platform.cases+=@{id=$fixture.id;sourceSha256=$fixture.sourceSha256;expectationSha256=$fixture.expectationSha256;status='passed';semanticClosureEligible=$true;coreClrCompile=$process;coreClrRun=$process;ilVerify=@{status='passed';succeeded=$true;process=$process};nativeAot=@{status='passed';succeeded=$true;outputMatches=$true;hostCleanupIncomplete=$false;publish=$process;run=$process}}
}
Assert-SuiteResult @(Test-P1ExpandedEvidence (Copy-Fixture $platform) $root 'p1-platform-v2' 'win-x64' $candidate)
$value=Copy-Fixture $platform; $value.cases[0].nativeAot.run.standardOutput='incorrect-output'
Assert-SuiteResult @(Test-P1ExpandedEvidence $value $root 'p1-platform-v2' 'win-x64' $candidate) 'exact CoreCLR/Native AOT frozen output'
$value=Copy-Fixture $platform; $value.cases[0].ilVerify.process.outputTruncated=$true
Assert-SuiteResult @(Test-P1ExpandedEvidence $value $root 'p1-platform-v2' 'win-x64' $candidate) 'output evidence is incomplete'
$value=Copy-Fixture $platform; $value.platform.nativeExecution=$false
Assert-SuiteResult @(Test-P1ExpandedEvidence $value $root 'p1-platform-v2' 'win-x64' $candidate) 'Observed native x64 host'
$value=Copy-Fixture $platform; $value.cases=$value.cases[0..22]
Assert-SuiteResult @(Test-P1ExpandedEvidence $value $root 'p1-platform-v2' 'win-x64' $candidate) 'case denominator mismatch'
$linuxEnvelope=@{name='linux-x64';runtimeIdentifier='ubuntu.24.04-x64';nativeHost=$true;operatingSystem='Ubuntu 24.04.4 LTS';osArchitecture='X64';processArchitecture='X64';oracle='rustc 1.98.0 (synthetic)'}
$linuxVersions=@{sdkVersion='10.0.112';dotnet='10.0.12';rustc='rustc 1.98.0 (synthetic)'}
$linuxControls=@(
    @{name='actual-Ubuntu';rid='ubuntu.24.04-x64';os='Ubuntu 24.04.4 LTS';pattern=''},
    @{name='generic-Linux-Ubuntu';rid='linux-x64';os='Ubuntu 24.04.4 LTS';pattern=''},
    @{name='generic-Linux-Debian';rid='linux-x64';os='Debian GNU/Linux 12 (bookworm)';pattern=''},
    @{name='generic-Linux-kernel';rid='linux-x64';os='Linux 6.6.87.2-microsoft-standard-WSL2';pattern=''},
    @{name='generic-Linux-Unix';rid='linux-x64';os='Unix 6.6.87.2';pattern=''},
    @{name='wrong-RID';rid='ubuntu.24.04-arm64';os='Ubuntu 24.04.4 LTS';pattern='Native runtime identifier mismatch'},
    @{name='Windows-OS';rid='ubuntu.24.04-x64';os='Microsoft Windows 10.0.26300';pattern='Observed native x64 host provenance'},
    @{name='wrong-distribution-version';rid='ubuntu.24.04-x64';os='Ubuntu 22.04.5 LTS';pattern='Observed native x64 host provenance'},
    @{name='non-native-host';rid='ubuntu.24.04-x64';os='Ubuntu 24.04.4 LTS';pattern='Observed native x64 host provenance'}
)
$linuxChecks=0
foreach ($control in $linuxControls) {
    if ($linuxChecks -ge 9 -or $clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Linux envelope control item/time bound exceeded.' }
    $envelope=Copy-Fixture $linuxEnvelope
    $envelope.runtimeIdentifier=$control.rid; $envelope.operatingSystem=$control.os
    if ($control.name -ceq 'non-native-host') { $envelope.nativeHost=$false }
    $errors=[Collections.Generic.List[string]]::new()
    Test-P1DifferentialPlatformEvidence $envelope $linuxVersions 'linux-x64' $errors
    if ($control.pattern) { if (@($errors | Where-Object { $_ -match $control.pattern }).Count -eq 0) { throw ('Missing Linux envelope rejection: '+$control.name) } }
    elseif ($errors.Count -gt 0) { throw ('Linux envelope positive control failed: '+$control.name+'; '+($errors -join '; ')) }
    $linuxChecks++
}
Write-Output "✅ Complete: $checks bounded P1 suite evidence controls and rejection checks; $linuxChecks Linux envelope controls; synthetic data was kept in memory and cannot serve as candidate evidence."
