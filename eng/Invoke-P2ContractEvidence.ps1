[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^P2-(04|06|08)\.01$')][string] $LeafId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $ManifestPath,
    [Parameter(Mandatory)][ValidateLength(1, 256)][string] $Filter,
    [Parameter(Mandatory)][ValidateRange(1, 64)][int] $ExpectedTests,
    [Parameter(Mandatory)][ValidateRange(1, 1024)][int] $ContractCases,
    [string] $ReportDirectory = 'artifacts/p2-contracts',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $SdkVersion = '10.0.401',
    [ValidateRange(1, 300)][int] $DeadlineSeconds = 120
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = [IO.Path]::GetFullPath($ManifestPath, $root)
$directory = [IO.Path]::GetFullPath($ReportDirectory, $root)
foreach ($path in @($manifest, $directory)) {
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'P2 contract inputs and reports must be inside the repository.'
    }
}
if (-not [IO.File]::Exists($manifest) -or ([IO.FileInfo]::new($manifest)).Length -gt 1MB) {
    throw 'The manifest is absent or exceeds the one MiB bound.'
}
$manifestDocument = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -Depth 24
$manifestIdentity = if ($null -ne $manifestDocument.PSObject.Properties['leafId']) {
    $manifestDocument.leafId
} else { $manifestDocument.taskId }
if ($manifestIdentity -cne $LeafId -or $manifestDocument.denominator -ne $ContractCases) {
    throw 'The leaf identity and case count must equal the frozen manifest.'
}
[void][IO.Directory]::CreateDirectory($directory)
$prefix = Join-Path $directory $LeafId
$reportPath = $prefix + '.json'
$harnessPath = $prefix + '.harness.json'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$tests = Join-Path $root 'tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll'
$arguments = @($tests, '--filter', $Filter, '--timeout', '30', '--deadline', [string]$DeadlineSeconds,
    '--report', $harnessPath, '--candidate-sha', $CandidateSha)
$started = [DateTimeOffset]::UtcNow
$parentChain = [Collections.Generic.List[object]]::new()
if ([OperatingSystem]::IsWindows()) {
    $parentClock = [Diagnostics.Stopwatch]::StartNew()
    $ancestorId = $PID
    # At most 16 ancestors and ten seconds; each local CIM query is bounded.
    for ($ancestorIndex = 0; $ancestorIndex -lt 16 -and $ancestorId -gt 0 -and $parentClock.Elapsed.TotalSeconds -lt 10; $ancestorIndex++) {
        $ancestor = Get-CimInstance Win32_Process -Filter "ProcessId = $ancestorId" -OperationTimeoutSec 2
        if ($null -eq $ancestor) { break }
        $parentChain.Add(@{ processId = $ancestor.ProcessId; parentProcessId = $ancestor.ParentProcessId
            createdAt = $ancestor.CreationDate.ToString('O'); commandLine = $ancestor.CommandLine })
        if ($ancestor.ParentProcessId -eq $ancestorId) { break }
        $ancestorId = [int]$ancestor.ParentProcessId
    }
}
$failure = $null
$harness = $null
$process = $null
$manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
try {
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList $arguments `
        -WorkingDirectory $root -TimeoutSeconds ($DeadlineSeconds + 10) -MaximumOutputBytes 4MB -CapturePrefix ($prefix + '.run')
    if (-not [IO.File]::Exists($harnessPath) -or ([IO.FileInfo]::new($harnessPath)).Length -gt 4MB) {
        throw 'The bounded harness did not produce a valid report.'
    }
    $harness = [IO.File]::ReadAllText($harnessPath) | ConvertFrom-Json -Depth 24
    $summary = $harness.summary
    if ($harness.candidateSha -cne $CandidateSha -or $harness.filter -cne $Filter -or
        $harness.buildConfiguration -cne 'Release' -or -not $harness.processIsolated -or
        -not $harness.sourceProvenance.candidateMatchesWorkingTree -or -not $harness.cleanupComplete -or
        $harness.deadlineExpired -or $harness.cancelled -or $harness.harnessError -or
        $summary.selected -ne $ExpectedTests -or $summary.executed -ne $ExpectedTests -or
        $summary.passed -ne $ExpectedTests -or $summary.failed -ne 0 -or $summary.skipped -ne 0 -or
        $summary.notExecuted -ne 0 -or $harness.cases.Count -ne $ExpectedTests) {
        throw 'Fixed-denominator contract evidence or source/process provenance did not pass.'
    }
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne $manifestHash) {
        throw 'The scope manifest changed during validation.'
    }
} catch { $failure = $_.Exception.Message }
finally {
    $processPath = $prefix + '.run.process.json'
    if ([IO.File]::Exists($processPath)) { $process = [IO.File]::ReadAllText($processPath) | ConvertFrom-Json }
    $success = $null -eq $failure -and $null -ne $harness -and $null -ne $process -and $process.CleanupComplete
    $report = [ordered]@{
        schemaVersion = 1; evidenceKind = 'p2-frozen-contract'; leafId = $LeafId
        candidateSha = $CandidateSha; manifestPath = [IO.Path]::GetRelativePath($root, $manifest).Replace('\', '/')
        manifestSha256 = $manifestHash; scope = 'manifest-design-and-validator'; fullP2Closure = $false
        contractCases = $ContractCases; implementationCasesExecuted = 0
        tools = @{ sdkVersion = $SdkVersion; powerShellVersion = $PSVersionTable.PSVersion.ToString(); dotnetPath = $dotnet }
        runtimeIdentifier = if ($null -ne $harness) { $harness.runtimeIdentifier } else { $null }
        command = @{ executable = $dotnet; arguments = $arguments; workingDirectory = $root }
        denominator = $ExpectedTests
        passed = if ($null -ne $harness) { $harness.summary.passed } else { 0 }
        failed = if ($null -ne $harness) { $harness.summary.failed } else { $ExpectedTests }
        skipped = 0
        executed = if ($null -ne $harness) { $harness.summary.executed } else { 0 }
        notExecuted = if ($null -ne $harness) { $harness.summary.notExecuted } else { $ExpectedTests }
        summary = @{ status = if ($success) { 'passed' } else { 'failed' }; denominator = $ExpectedTests
            passed = if ($null -ne $harness) { $harness.summary.passed } else { 0 }
            failed = if ($null -ne $harness) { $harness.summary.failed } else { $ExpectedTests }
            skipped = 0 }
        bounds = @{ maximumCommands = 1; maximumTests = 64; caseTimeoutSeconds = 30
            deadlineSeconds = $DeadlineSeconds; maximumOutputBytes = 4MB; maximumManifestBytes = 1MB }
        timeResourceLimits = @{ maximumCommands = 1; maximumTests = 64; caseTimeoutSeconds = 30
            deadlineSeconds = $DeadlineSeconds; maximumOutputBytes = 4MB; maximumManifestBytes = 1MB }
        processId = if ($null -ne $process) { $process.Pid } else { $null }
        processStartedAtUtc = if ($null -ne $process) { $process.StartedAt } else { $null }
        parentProcessId = if ($null -ne $process) { $process.ParentPid } else { $null }
        parentChain = @($parentChain.ToArray())
        cleanup = @{ complete = $null -ne $process -and $process.CleanupComplete; temporaryReportRemoved = $true }
        process = $process; cleanupComplete = $null -ne $process -and $process.CleanupComplete
        sourceProvenance = if ($null -ne $harness) { $harness.sourceProvenance } else { $null }
        provenance = @{ kind = 'git-candidate-and-hashed-isolated-harness'; candidateSha = $CandidateSha
            harnessReport = [IO.Path]::GetRelativePath($root, $harnessPath).Replace('\', '/')
            harnessSha256 = if ([IO.File]::Exists($harnessPath)) { (Get-FileHash -LiteralPath $harnessPath -Algorithm SHA256).Hash } else { $null } }
        startedAtUtc = $started.ToString('O'); finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        failureReasons = @($failure | Where-Object { $null -ne $_ })
    }
    $temporary = $reportPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($report | ConvertTo-Json -Depth 24))
        [IO.File]::Move($temporary, $reportPath, $true)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
if (-not $success) { throw ($failure ?? 'The owned process did not exit cleanly.') }
Write-Output "P2 contract evidence: $LeafId ($ExpectedTests/$ExpectedTests validator tests; $ContractCases frozen implementation cases)."
