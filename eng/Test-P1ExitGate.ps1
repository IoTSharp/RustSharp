[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $WindowsPlatformReport,

    [Parameter(Mandatory = $true)]
    [string] $LinuxPlatformReport,

    [Parameter(Mandatory = $true)]
    [string] $WindowsDifferentialReport,

    [Parameter(Mandatory = $true)]
    [string] $LinuxDifferentialReport,

    [Parameter(Mandatory = $true)]
    [string] $WindowsRegressionReport,

    [Parameter(Mandatory = $true)]
    [string] $LinuxRegressionReport,

    [Parameter()]
    [string] $EvidencePath = 'artifacts/p1-exit-gate/p1-exit-gate.json',

    [Parameter()]
    [ValidateRange(1, 32)]
    [int] $MaximumReportBytes = 16MB
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Test-P1ExitGate.ps1 requires PowerShell 7 or newer.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$startedAt = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$validation = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$status = 'blocked'
$harnessError = $null
$reportFullPath = $null
$specifications = @()

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Report paths must not be empty.'
    }

    return [IO.Path]::GetFullPath($Path, $repositoryRoot)
}

function Add-Failure {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][ValidateSet('failed', 'blocked')][string] $State,
        [Parameter(Mandatory = $true)][string] $Message
    )

    [void] $validation.Add([pscustomobject][ordered]@{
        Input = $InputName
        Status = $State
        Message = $Message
    })
}

function Read-BoundedJson {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $fullPath = Resolve-RepositoryPath $Path
    $inputRecord = [ordered]@{
        Name = $InputName
        Path = [IO.Path]::GetRelativePath($repositoryRoot, $fullPath).Replace('\', '/')
        Sha256 = $null
        Bytes = $null
        Status = 'blocked'
        Summary = $null
    }

    if (-not [IO.File]::Exists($fullPath)) {
        $inputRecord.Summary = 'Evidence report was not found.'
        [void] $inputs.Add([pscustomobject]$inputRecord)
        Add-Failure $InputName 'blocked' "Evidence report was not found: '$fullPath'."
        return $null
    }

    try {
        $bytes = [IO.File]::ReadAllBytes($fullPath)
        $inputRecord.Bytes = $bytes.Length
        if ($bytes.Length -gt $MaximumReportBytes) {
            throw "Evidence report exceeds the $MaximumReportBytes-byte bound."
        }

        $inputRecord.Sha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($bytes))
        $document = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $inputRecord.Status = 'loaded'
        [void] $inputs.Add([pscustomobject]$inputRecord)
        return [pscustomobject]@{ Document = $document; Path = $fullPath }
    }
    catch {
        $inputRecord.Summary = $_.Exception.Message
        [void] $inputs.Add([pscustomobject]$inputRecord)
        Add-Failure $InputName 'failed' "Evidence report could not be read: $($_.Exception.Message)"
        return $null
    }
}

function Validate-CountSummary {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][object] $Summary,
        [Parameter(Mandatory = $true)][int] $Denominator,
        [Parameter(Mandatory = $true)][string] $ExpectedStatus
    )

    $required = @('Status', 'Denominator', 'Executed', 'Passed', 'Failed', 'Blocked', 'Skipped')
    $missingProperty = $false
    foreach ($property in $required) {
        if ($null -eq $Summary.PSObject.Properties[$property]) {
            Add-Failure $InputName 'failed' "Summary is missing '$property'."
            $missingProperty = $true
        }
    }

    if ($missingProperty) { return }
    $summaryState = if ([string]$Summary.Status -eq 'blocked') { 'blocked' } else { 'failed' }
    if ([string]$Summary.Status -ne $ExpectedStatus) {
        $state = $summaryState
        Add-Failure $InputName $state "Summary status was '$($Summary.Status)', expected '$ExpectedStatus'."
    }
    if ([int]$Summary.Denominator -ne $Denominator) {
        Add-Failure $InputName $summaryState "Summary denominator was $($Summary.Denominator), expected $Denominator."
    }
    if ([int]$Summary.Executed -ne $Denominator) {
        Add-Failure $InputName $summaryState "Summary executed $($Summary.Executed), expected $Denominator."
    }
    if ([int]$Summary.Passed -ne $Denominator) {
        Add-Failure $InputName $summaryState "Summary passed $($Summary.Passed), expected $Denominator."
    }
    foreach ($zeroField in @('Failed', 'Blocked', 'Skipped')) {
        if ([int]$Summary.$zeroField -ne 0) {
            $countState = if ($summaryState -eq 'blocked' -or $zeroField -eq 'Blocked') { 'blocked' } else { 'failed' }
            Add-Failure $InputName $countState "Summary $($zeroField.ToLowerInvariant()) was $($Summary.$zeroField), expected zero."
        }
    }
}

function Validate-PlatformReport {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][object] $Loaded
    )

    $document = $Loaded.Document
    if ($document.EvidenceKind -ne 'p1-platform-coreclr-ilverify-native-aot') {
        Add-Failure $InputName 'failed' "Unexpected EvidenceKind '$($document.EvidenceKind)'."
    }
    if ($document.Profile -ne 'p1-differential-v2') {
        Add-Failure $InputName 'failed' "Unexpected profile '$($document.Profile)'."
    }
    if ($null -eq $document.Platform -or $null -eq $document.Platform.Name) {
        Add-Failure $InputName 'failed' 'Platform metadata is missing.'
    }
    else {
        $expectedPlatform = if ($InputName -eq 'windows-platform') { 'windows-x64' } else { 'linux-x64' }
        $expectedRuntime = if ($InputName -eq 'windows-platform') { 'win-x64' } else { 'linux-x64' }
        if ([string]$document.Platform.Name -ne $expectedPlatform) {
            Add-Failure $InputName 'failed' "Platform name was '$($document.Platform.Name)', expected '$expectedPlatform'."
        }
        if ([string]$document.Platform.RuntimeIdentifier -ne $expectedRuntime) {
            Add-Failure $InputName 'failed' "Runtime identifier was '$($document.Platform.RuntimeIdentifier)', expected '$expectedRuntime'."
        }
    }
    if ($null -ne $document.Manifest -and [int]$document.Manifest.Denominator -ne 12) {
        Add-Failure $InputName 'failed' "Manifest denominator was $($document.Manifest.Denominator), expected 12."
    }
    $summary = $document.Summary
    if ($null -eq $summary) {
        Add-Failure $InputName 'failed' 'Platform summary is missing.'
        return
    }
    Validate-CountSummary $InputName $summary 12 'passed'
    if ($null -ne $document.Cases -and @($document.Cases).Count -ne 12) {
        Add-Failure $InputName 'failed' "Platform case count was $(@($document.Cases).Count), expected 12."
    }
    foreach ($case in @($document.Cases)) {
        if ($case.Status -ne 'passed') {
            $caseState = if ($case.Status -eq 'blocked') { 'blocked' } else { 'failed' }
            Add-Failure $InputName $caseState "Platform case '$($case.Id)' status was '$($case.Status)'."
        }
    }
}

function Validate-DifferentialReport {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][object] $Loaded
    )

    $document = $Loaded.Document
    if ($document.profile -ne 'p1-differential-v2') {
        Add-Failure $InputName 'failed' "Unexpected profile '$($document.profile)'."
    }
    if ([int]$document.schemaVersion -ne 2) {
        Add-Failure $InputName 'failed' "Unexpected differential schema '$($document.schemaVersion)'."
    }
    if ($null -eq $document.oracle -or $document.oracle.available -ne $true -or
        ([string]$document.oracle.version -notlike 'rustc 1.98.0 (*)')) {
        Add-Failure $InputName 'blocked' 'rustc 1.98.0 oracle evidence is unavailable or mismatched.'
    }
    $summary = $document.summary
    if ($null -eq $summary) {
        Add-Failure $InputName 'failed' 'Differential summary is missing.'
        return
    }
    Validate-CountSummary $InputName $summary 16 'passed'
    if ([int]$summary.borrowDenominator -ne 10) {
        Add-Failure $InputName 'failed' "Borrow denominator was $($summary.borrowDenominator), expected 10."
    }
    if ([int]$summary.dropDenominator -ne 6) {
        Add-Failure $InputName 'failed' "Drop denominator was $($summary.dropDenominator), expected 6."
    }
    if ($null -ne $document.cases -and @($document.cases).Count -ne 16) {
        Add-Failure $InputName 'failed' "Differential case count was $(@($document.cases).Count), expected 16."
    }
    foreach ($case in @($document.cases)) {
        if ($case.status -ne 'passed') {
            $caseState = if ($case.status -eq 'blocked') { 'blocked' } else { 'failed' }
            Add-Failure $InputName $caseState "Differential case '$($case.id)' status was '$($case.status)'."
        }
    }
}

function Validate-RegressionReport {
    param(
        [Parameter(Mandatory = $true)][string] $InputName,
        [Parameter(Mandatory = $true)][object] $Loaded
    )

    $document = $Loaded.Document
    if ($document.evidenceKind -ne 'safe-core-typed-mir-regression') {
        Add-Failure $InputName 'failed' "Unexpected evidence kind '$($document.evidenceKind)'."
    }
    if ($document.profile -ne 'safe-core-regression-v2') {
        Add-Failure $InputName 'failed' "Unexpected profile '$($document.profile)'."
    }
    if ([int]$document.schemaVersion -ne 2) {
        Add-Failure $InputName 'failed' "Unexpected regression schema '$($document.schemaVersion)'."
    }
    $summary = $document.summary
    if ($null -eq $summary) {
        Add-Failure $InputName 'failed' 'Regression summary is missing.'
        return
    }
    Validate-CountSummary $InputName $summary 24 'passed'
    $coverage = $summary.coverage
    if ($null -eq $coverage) {
        Add-Failure $InputName 'failed' 'Regression coverage is missing.'
    }
    else {
        foreach ($expected in @{
            'compile-pass' = 1; 'compile-fail' = 6; 'run-pass' = 13; 'differential' = 4;
            legacy = 8; 'typed-mir' = 12; borrow = 2; drop = 2
        }.GetEnumerator()) {
            if ($null -eq $coverage.PSObject.Properties[$expected.Key] -or [int]$coverage.($expected.Key) -ne $expected.Value) {
                Add-Failure $InputName 'failed' "Regression coverage '$($expected.Key)' did not equal $($expected.Value)."
            }
        }
    }
    if ($null -eq $document.cases -or @($document.cases).Count -ne 24) {
        Add-Failure $InputName 'failed' "Regression case count was $(@($document.cases).Count), expected 24."
    }
    foreach ($case in @($document.cases)) {
        if ($case.status -ne 'passed') {
            $caseState = if ($case.status -eq 'blocked') { 'blocked' } else { 'failed' }
            Add-Failure $InputName $caseState "Regression case '$($case.id)' status was '$($case.status)'."
        }
    }
}

try {
    $reportFullPath = Resolve-RepositoryPath $EvidencePath
    $specifications = @(
        [pscustomobject]@{ Name = 'windows-platform'; Path = $WindowsPlatformReport; Kind = 'platform' },
        [pscustomobject]@{ Name = 'linux-platform'; Path = $LinuxPlatformReport; Kind = 'platform' },
        [pscustomobject]@{ Name = 'windows-differential-v2'; Path = $WindowsDifferentialReport; Kind = 'differential' },
        [pscustomobject]@{ Name = 'linux-differential-v2'; Path = $LinuxDifferentialReport; Kind = 'differential' },
        [pscustomobject]@{ Name = 'windows-safe-core-regression-v2'; Path = $WindowsRegressionReport; Kind = 'regression' },
        [pscustomobject]@{ Name = 'linux-safe-core-regression-v2'; Path = $LinuxRegressionReport; Kind = 'regression' }
    )

    foreach ($specification in $specifications) {
        $loaded = Read-BoundedJson $specification.Name $specification.Path
        if ($null -eq $loaded) { continue }
        if ($specification.Kind -eq 'platform') {
            Validate-PlatformReport $specification.Name $loaded
        }
        elseif ($specification.Kind -eq 'differential') {
            Validate-DifferentialReport $specification.Name $loaded
        }
        else {
            Validate-RegressionReport $specification.Name $loaded
        }
    }
}
catch {
    $harnessError = $_.Exception.Message
    Add-Failure 'harness' 'blocked' $harnessError
}
finally {
    $clock.Stop()
    $gates = @($specifications | ForEach-Object {
        $gateFailures = @($validation | Where-Object Input -eq $_.Name)
        $gateStatus = if (@($gateFailures | Where-Object Status -eq 'failed').Count -gt 0) {
            'failed'
        }
        elseif (@($gateFailures | Where-Object Status -eq 'blocked').Count -gt 0) {
            'blocked'
        }
        else {
            'passed'
        }
        [pscustomobject][ordered]@{
            Name = $_.Name
            Kind = $_.Kind
            Status = $gateStatus
            ValidationCount = $gateFailures.Count
        }
    })
    $failed = @($gates | Where-Object Status -eq 'failed').Count
    $blocked = @($gates | Where-Object Status -eq 'blocked').Count
    $passed = @($gates | Where-Object Status -eq 'passed').Count
    if ($harnessError) { $status = 'blocked' }
    elseif ($failed -gt 0) { $status = 'failed' }
    elseif ($blocked -gt 0) { $status = 'blocked' }
    else { $status = 'passed' }

    if ($null -ne $reportFullPath) {
        $parentDirectory = [IO.Path]::GetDirectoryName($reportFullPath)
        if ($null -eq $parentDirectory) { throw 'EvidencePath must have a parent directory.' }
        [IO.Directory]::CreateDirectory($parentDirectory) | Out-Null
        $report = [ordered]@{
            SchemaVersion = 1
            EvidenceKind = 'p1-complete-exit-gate'
            Profile = 'p1-differential-v2'
            Summary = [ordered]@{
                Status = $status
                ExitCode = if ($status -eq 'passed') { 0 } elseif ($status -eq 'failed') { 1 } else { 2 }
                Denominator = 6
                Executed = $gates.Count
                Passed = $passed
                Failed = $failed
                Blocked = $blocked
                Skipped = 0
            }
            Gates = $gates
            Inputs = @($inputs.ToArray())
            Validation = @($validation.ToArray())
            Execution = [ordered]@{
                StartedAtUtc = $startedAt
                FinishedAtUtc = [DateTimeOffset]::UtcNow
                ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds
                MaximumReportBytes = $MaximumReportBytes
            }
            HarnessError = $harnessError
        }
        $temporaryPath = $reportFullPath + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
        try {
            [IO.File]::WriteAllText($temporaryPath, ($report | ConvertTo-Json -Depth 20))
            [IO.File]::Move($temporaryPath, $reportFullPath)
        }
        finally {
            if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
        }
        Write-Output ([IO.File]::ReadAllText($reportFullPath))
    }
}

if ($status -eq 'passed') { exit 0 }
if ($status -eq 'failed') { exit 1 }
exit 2
