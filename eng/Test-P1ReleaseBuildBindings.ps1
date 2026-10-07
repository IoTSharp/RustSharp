[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ReportPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $CandidateSha,
    [ValidateRange(1,9)][int] $MaximumChecks=9,
    [ValidateRange(1,180)][int] $DeadlineSeconds=120,
    [string] $RepositoryRoot=(Join-Path $PSScriptRoot '..')
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root=[IO.Path]::GetFullPath($RepositoryRoot)
$clock=[Diagnostics.Stopwatch]::StartNew()
$report=ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ReportPath,$root)))
$checks=@(
    @{ name='actual-release-source-and-physical-artifacts'; mutate={param($value)}; pattern='' },
    @{ name='stale-tests-assembly'; mutate={param($value) $value.testsAssemblySha256='0'*64}; pattern='physical SHA-256 is stale' },
    @{ name='missing-implementation-dependency'; mutate={param($value) $value.implementationAssemblies=$value.implementationAssemblies[0..4]}; pattern='denominator must be six' },
    @{ name='duplicate-implementation-dependency'; mutate={param($value) $value.implementationAssemblies[1]=$value.implementationAssemblies[0]}; pattern='inventory differs' },
    @{ name='unbracketed-build'; mutate={param($value) $value.sourceSnapshots=@($value.sourceSnapshots[0])}; pattern='exactly two source snapshots' },
    @{ name='stale-source-snapshot-bytes'; mutate={param($value) $value.sourceSnapshots[0].sha256='0'*64}; pattern='physical SHA-256 is stale' },
    @{ name='forged-source-candidate'; mutate={param($value) $value.sourceProvenance.candidateSha='0'*40}; pattern='source/tree provenance is missing or mismatched' },
    @{ name='wrong-build-command'; mutate={param($value) $value.steps[1].process.Arguments[1]='--list'}; pattern='command does not bind' },
    @{ name='inverted-source-snapshot-order'; mutate={param($value) $value.sourceSnapshots=@($value.sourceSnapshots[1],$value.sourceSnapshots[0])}; pattern='order/name differs' }
)
# At most nine checks and a caller-selected wall deadline. The one-case trial
# runs this identical positive path before the mutation batch; no files mutate.
for ($index=0; $index -lt $MaximumChecks -and $index -lt $checks.Count; $index++) {
    if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Release binding checks exceeded their deadline.' }
    $check=$checks[$index]
    $value=$report | ConvertTo-Json -Depth 48 | ConvertFrom-Json -Depth 48
    & $check.mutate $value
    # Each control is an independent validation envelope. Retain its complete
    # ledger in the bounded capture before starting the next 24-call envelope.
    $script:P1SnapshotValidationProcesses=[Collections.Generic.List[object]]::new()
    $script:P1SnapshotValidationClock=[Diagnostics.Stopwatch]::StartNew()
    try { $errors=@(Test-P1BuildEvidence $value $CandidateSha '' $root) }
    finally { [ordered]@{check=$check.name;processes=@($script:P1SnapshotValidationProcesses.ToArray())}|ConvertTo-Json -Depth 8 -Compress|Write-Output }
    if ($check.pattern) {
        if (@($errors | Where-Object { $_ -match $check.pattern }).Count -eq 0) { throw ('Expected rejection for '+$check.name+': '+($errors -join '; ')) }
    } elseif ($errors.Count -ne 0) { throw ('Actual Release evidence failed: '+($errors -join '; ')) }
    if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Release binding checks exceeded their deadline.' }
    Write-Output ('PASS '+$check.name)
}
Write-Output ('✅ Complete: '+$MaximumChecks+'/'+$MaximumChecks+' bounded physical/source binding checks; mutation controls are not phase closure evidence.')
