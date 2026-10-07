[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ownedParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$fixtureRoot = Join-Path $ownedParent ('p1-ci-contract-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
$clock = [Diagnostics.Stopwatch]::StartNew()
$pwsh = if ($IsWindows) { 'C:\Program Files\PowerShell\7\pwsh.exe' } else { (Get-Command pwsh -ErrorAction Stop).Source }
$sha = '1234567890123456789012345678901234567890'
$checks = [Collections.Generic.List[string]]::new()
$names = @('p1-coverage-v1.json','p1-differential-v3.json','p1-platform-v2.json','release-build.json','regression-harness.json','immutable-regression-audit.json','source-snapshot.json')

function Reset-Fixture {
    if ($clock.Elapsed.TotalSeconds -ge 60) { throw 'Contract fixture deadline expired.' }
    foreach ($platform in @('windows-x64','linux-x64')) {
        $directory = Join-Path $fixtureRoot "artifacts/p1-expanded/$platform"
        $null = [IO.Directory]::CreateDirectory($directory)
        foreach ($name in $names) {
            [ordered]@{ fixtureOnly=$true; candidateSha=$sha; succeeded=$true; summary=@{status='passed'} } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory $name) -Encoding utf8
        }
        & (Join-Path $PSScriptRoot 'Write-P1CiPublication.ps1') -RepositoryRoot $fixtureRoot -CandidateSha $sha -PlatformName $platform -RunId 100 -RunAttempt 2 -Repository IoTSharp/RustSharp -UpstreamStatus success
        # Offline fixtures model a pair of native hosts. They never leave this
        # owned directory or stand in for actual conformance evidence.
        $path = Join-Path $directory 'ci-publication.json'
        $descriptor = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 16
        $descriptor.nativeExecution = $true
        $descriptor.observedRuntimeIdentifier = if($platform -ceq 'windows-x64'){'win-x64'}else{'linux-x64'}
        $descriptor.architecture = 'X64'
        $descriptor.status = 'passed'
        $descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    }
}

function Invoke-Check([string] $Name, [int] $ExpectedExit, [string] $NativeResult = 'success') {
    if ($clock.Elapsed.TotalSeconds -ge 60 -or $checks.Count -ge 8) { throw 'Contract validation exceeded its eight-check/60-second bound.' }
    $prefix = Join-Path $fixtureRoot ('check-' + $checks.Count)
    $arguments = @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-P1CiPublication.ps1'),'-RepositoryRoot',$fixtureRoot,'-CandidateSha',$sha,'-RunId','100','-RunAttempt','2','-Repository','IoTSharp/RustSharp','-NativeJobResult',$NativeResult)
    try { & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $pwsh -ArgumentList $arguments -TimeoutSeconds 10 -CapturePrefix $prefix } catch { Write-Verbose $_.Exception.Message }
    $process = [IO.File]::ReadAllText($prefix + '.process.json') | ConvertFrom-Json
    if ($process.ExitCode -ne $ExpectedExit -or $process.CleanupComplete -cne $true) { throw "Contract '$Name' expected exit $ExpectedExit, observed $($process.ExitCode); cleanup $($process.CleanupComplete)." }
    $checks.Add($Name)
}

try {
    # Tiny first trial: no native reports, so publication reconciliation blocks.
    Invoke-Check 'missing-inventories-block' 2
    Reset-Fixture
    Invoke-Check 'valid-paired-publication' 0
    [IO.File]::AppendAllText((Join-Path $fixtureRoot 'artifacts/p1-expanded/windows-x64/regression-harness.json'), ' ')
    Invoke-Check 'downloaded-byte-tamper-rejected' 1
    Reset-Fixture
    $path = Join-Path $fixtureRoot 'artifacts/p1-expanded/windows-x64/ci-publication.json'
    $descriptor = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 16
    $descriptor.run.attempt = '1'
    $descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    Invoke-Check 'stale-run-attempt-rejected' 1
    Reset-Fixture
    $path = Join-Path $fixtureRoot 'artifacts/p1-expanded/linux-x64/ci-publication.json'
    $descriptor = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 16
    $descriptor.reports[0].path = 'artifacts/p1-expanded/linux-x64/../../source-snapshot.json'
    $descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    Invoke-Check 'platform-path-escape-rejected' 1
    Reset-Fixture
    Invoke-Check 'cancelled-native-job-blocks' 2 'cancelled'
    Reset-Fixture
    $missing = Join-Path $fixtureRoot 'artifacts/p1-expanded/windows-x64/p1-differential-v3.json'
    [IO.File]::Delete($missing)
    & (Join-Path $PSScriptRoot 'Write-P1CiPublication.ps1') -RepositoryRoot $fixtureRoot -CandidateSha $sha -PlatformName windows-x64 -RunId 100 -RunAttempt 2 -Repository IoTSharp/RustSharp -UpstreamStatus failure
    $fallback = [IO.File]::ReadAllText($missing) | ConvertFrom-Json
    if ($fallback.summary.status -cne 'blocked' -or $fallback.summary.denominator -ne 32 -or $fallback.summary.executed -ne 0 -or $fallback.summary.blocked -ne 32 -or $fallback.summary.passed -ne 0 -or $fallback.summary.skipped -ne 0) { throw 'Unavailable infrastructure was incorrectly counted as semantic success or skip.' }
    # As in Reset-Fixture, retain the modeled Windows host identity on Linux.
    # Preserve the unavailable report, failed upstream and blocked status.
    # This fixture is never an actual native execution receipt.
    $path = Join-Path $fixtureRoot 'artifacts/p1-expanded/windows-x64/ci-publication.json'
    $descriptor = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 16
    $descriptor.nativeExecution = $true
    $descriptor.observedRuntimeIdentifier = 'win-x64'
    $descriptor.architecture = 'X64'
    $descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    Invoke-Check 'unavailable-report-preserved-as-blocked' 2
    Reset-Fixture
    $path = Join-Path $fixtureRoot 'artifacts/p1-expanded/linux-x64/ci-publication.json'
    $descriptor = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 16
    $descriptor.reports[1].name = $descriptor.reports[0].name
    $descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8
    Invoke-Check 'duplicate-publication-binding-rejected' 1
    Write-Output ([ordered]@{schemaVersion=1;evidenceKind='p1-ci-publication-contract';succeeded=$true;passed=$checks.Count;maximumChecks=8;deadlineSeconds=60;checks=@($checks.ToArray())} | ConvertTo-Json -Depth 4)
} finally {
    # The unique directory is owned by this invocation. Resolve and verify its
    # parent/name before recursive removal; delivery reports live elsewhere.
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -cne $ownedParent -or [IO.Path]::GetFileName($resolved) -notmatch '^p1-ci-contract-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the owned contract fixture directory.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
