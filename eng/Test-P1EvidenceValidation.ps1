[CmdletBinding()]
param([ValidateRange(1, 60)][int] $TimeoutSeconds = 30)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
. (Join-Path $PSScriptRoot 'P1EvidenceValidation.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$watch = [Diagnostics.Stopwatch]::StartNew()
$checks = 0
function Assert-Rejected($Errors, [string] $Pattern) {
    $script:checks++
    if ($script:checks -gt 16 -or $watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Evidence test budget exhausted.' }
    if (@($Errors | Where-Object { $_ -match $Pattern }).Count -eq 0) { throw "Expected rejection: $Pattern; observed: $($Errors -join '; ')" }
}
$manifestBytes = [IO.File]::ReadAllBytes((Join-Path $root 'tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v1-manifest.json'))
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
$suite = @($manifest.suites | Where-Object profile -EQ 'p1-platform-v2')[0]
$candidate = 'a' * 40
$compilerHash = 'B' * 64
$process = [ordered]@{ commandLine='synthetic test only'; processId=1; parentProcessId=2; startedAtUtc='2026-10-03T00:00:00Z'; exitCode=0; termination='exited'; cleanupIncomplete=$false; outputTruncated=$false; outputReadTimedOut=$false; outputDrainTimedOut=$false; outputReadLimitReached=$false; outputMatches=$true }
$rows = @(foreach ($fixture in $suite.cases) {
    [ordered]@{ id=$fixture.id; sourceSha256=$fixture.sourceSha256; expectationSha256=$fixture.expectationSha256; status='passed'; semanticClosureEligible=$true; coreClrCompile=$process; coreClrRun=$process; ilVerify=[ordered]@{ status='passed'; succeeded=$true; process=$process }; nativeAot=[ordered]@{ status='passed'; succeeded=$true; outputMatches=$true; hostCleanupIncomplete=$false; publish=$process; run=$process } }
})
$template = [ordered]@{ candidateSha=$candidate; evidenceKind='p1-platform-coreclr-ilverify-native-aot'; profile='p1-platform-v2'; semanticClosureEligible=$true; compilerSha256=$compilerHash; compiler=[ordered]@{ sha256=$compilerHash }; platform=[ordered]@{ runtimeIdentifier='win-x64'; oracle='rustc 1.98.0 (synthetic)'; sdk='10.0.401'; runtime='10.0.12' }; manifest=[ordered]@{ version=1; denominator=24; validated=$true; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)) }; summary=[ordered]@{ status='passed'; denominator=24; executed=24; passed=24; failed=0; blocked=0; skipped=0 }; execution=[ordered]@{ startedAtUtc='2026-10-03T00:00:00Z'; finishedAtUtc='2026-10-03T00:00:01Z'; deadlineExpired=$false }; cleanup=[ordered]@{ completed=$true }; cases=$rows }
function New-Report { return ($template | ConvertTo-Json -Depth 16 | ConvertFrom-Json) }
function Validate($Report) { return @(Test-P1ExpandedEvidence $Report $root 'p1-platform-v2' 'win-x64' $candidate) }
$report = New-Report
$report.platform | Add-Member -NotePropertyMembers @{ observedRuntimeIdentifier='win-x64'; nativeExecution=$true; architecture='X64' }
$template.platform.observedRuntimeIdentifier = 'win-x64'
$template.platform.nativeExecution = $true
$template.platform.architecture = 'X64'
Assert-Rejected (Validate $report) 'frozen label-only placeholder'
if (@((Validate $report) | Where-Object { $_ -notmatch 'frozen label-only placeholder' }).Count -ne 0) { throw 'The intact synthetic envelope should fail only its actual placeholder sources.' }
$report.semanticClosureEligible = $false
Assert-Rejected (Validate $report) 'not eligible for semantic closure'
$report = New-Report; $report.cases[0].nativeAot = $null
Assert-Rejected (Validate $report) 'Native AOT evidence is incomplete'
$report = New-Report; $report.cases[0].coreClrRun.outputMatches = $false
Assert-Rejected (Validate $report) 'CoreCLR output was not verified'
$report = New-Report; $report.cases[0].coreClrCompile.cleanupIncomplete = $true
Assert-Rejected (Validate $report) 'process cleanup is incomplete'
$report = New-Report; $report.cases[0].coreClrCompile.outputReadLimitReached = $true
Assert-Rejected (Validate $report) 'output evidence is incomplete'
$report = New-Report; $report.cases[0].coreClrCompile.PSObject.Properties.Remove('outputReadLimitReached')
Assert-Rejected (Validate $report) 'must be a boolean false marker'
$report = New-Report; $report.cases[0].sourceSha256 = 'C' * 64
Assert-Rejected (Validate $report) 'sourceSha256 is stale'
$report = New-Report; $report.cases[1].id = $report.cases[0].id
Assert-Rejected (Validate $report) 'duplicate expanded case'
$report = New-Report; $report.candidateSha = 'd' * 40
Assert-Rejected (Validate $report) 'Candidate SHA is invalid or mismatched'
$report = New-Report; $report.platform.oracle = 'unavailable'
Assert-Rejected (Validate $report) 'rustc 1.98.0 version is missing'
$closure = [pscustomobject]@{ candidateSha=$candidate; gate='P1-GATE.05' }
Assert-Rejected @(Test-P1ClosureRecord $closure $candidate $root @()) '85 implementation leaves'
Assert-Rejected @(Test-P1ClosureRecord $closure $candidate $root @()) 'two native CI run/artifact'
Assert-Rejected @(Test-P1ClosureRecord $closure $candidate $root @()) 'Closure document'
$openRoadmap = [IO.File]::ReadAllText((Join-Path $root 'docs/roadmap/P1.md')).Replace('✅ Complete', '🚧 In progress')
Assert-Rejected @(Test-P1RequirementClosure $root $openRoadmap) 'designated leaf.*remains open'
Write-Output "✅ Complete: $checks evidence rejection checks; synthetic inputs were not saved as candidate evidence."
