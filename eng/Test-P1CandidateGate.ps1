[CmdletBinding()]
param(
    [Parameter()][string] $CandidateSha = '',
    [Parameter()][string] $OldExitGateReport = 'artifacts/p1-platform/p1-exit-gate.json',
    [Parameter()][string] $ExpandedExitGateReport = 'artifacts/p1-expanded/p1-expanded-exit-gate.json',
    [Parameter()][string] $CoverageReport = 'artifacts/conformance/p1-coverage-v1.json',
    [Parameter()][string] $ExpandedWindowsDifferentialReport = 'artifacts/p1-expanded/windows-x64/p1-differential-v3.json',
    [Parameter()][string] $ExpandedLinuxDifferentialReport = 'artifacts/p1-expanded/linux-x64/p1-differential-v3.json',
    [Parameter()][string] $ExpandedWindowsPlatformReport = 'artifacts/p1-expanded/windows-x64/p1-platform-v2.json',
    [Parameter()][string] $ExpandedLinuxPlatformReport = 'artifacts/p1-expanded/linux-x64/p1-platform-v2.json',
    [Parameter()][string] $ClosureRecordPath = 'docs/p1-completion.json',
    [Parameter()][string] $EvidencePath = 'artifacts/p1-candidate/p1-candidate-gate.json',
    [Parameter()][ValidateRange(1024, 33554432)][int] $MaximumReportBytes = 16MB
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Test-P1CandidateGate.ps1 requires PowerShell 7 or newer.' }
. (Join-Path $PSScriptRoot 'P1EvidenceValidation.ps1')

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$started = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$checks = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$harnessError = $null
$gateStatuses = [ordered]@{
    'P1-GATE.01' = 'blocked'; 'P1-GATE.02' = 'blocked'; 'P1-GATE.03' = 'blocked';
    'P1-GATE.04' = 'blocked'; 'P1-GATE.05' = 'blocked'; 'P1-GATE.06' = 'blocked'
}
$candidate = if ([string]::IsNullOrWhiteSpace($CandidateSha)) { $env:GITHUB_SHA } else { $CandidateSha }
if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:CI_COMMIT_SHA }

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Evidence paths must not be empty.' }
    $full = [IO.Path]::GetFullPath($Path, $root)
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence path escapes the repository root: '$Path'."
    }
    return $full
}

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string] $Gate,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][ValidateSet('passed', 'failed', 'blocked')][string] $Status,
        [Parameter(Mandatory = $true)][string] $Message
    )
    [void]$checks.Add([pscustomobject][ordered]@{ Gate = $Gate; Name = $Name; Status = $Status; Message = $Message })
}

function Read-BoundedJson {
    param([Parameter(Mandatory = $true)][string] $Name, [Parameter(Mandatory = $true)][string] $Path)
    $record = [ordered]@{ Name = $Name; Path = $Path.Replace('\', '/'); Sha256 = $null; Bytes = $null; Status = 'blocked'; Summary = $null }
    try { $full = Resolve-RepositoryPath $Path } catch { $record.Summary = $_.Exception.Message; [void]$inputs.Add([pscustomobject]$record); return $null }
    $record.Path = [IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
    if (-not [IO.File]::Exists($full)) { $record.Summary = 'Evidence report was not found.'; [void]$inputs.Add([pscustomobject]$record); return $null }
    try {
        if (([IO.FileInfo]::new($full)).Length -gt $MaximumReportBytes) { throw "Evidence report exceeds the $MaximumReportBytes-byte bound." }
        $bytes = [IO.File]::ReadAllBytes($full)
        $record.Bytes = $bytes.Length
        if ($bytes.Length -lt 1 -or $bytes.Length -gt $MaximumReportBytes) { throw "Evidence report exceeds the $MaximumReportBytes-byte bound." }
        $record.Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        $document = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $record.Status = 'loaded'
        [void]$inputs.Add([pscustomobject]$record)
        return $document
    }
    catch {
        $record.Summary = $_.Exception.Message
        [void]$inputs.Add([pscustomobject]$record)
        return $null
    }
}

function Test-Candidate {
    param([Parameter(Mandatory = $true)][string] $Name, [Parameter(Mandatory = $true)][object] $Document)
    if ([string]::IsNullOrWhiteSpace($candidate)) { return 'blocked: candidate SHA is not supplied.' }
    if ($candidate -notmatch '^[0-9a-fA-F]{40,64}$') { return 'failed: candidate SHA is not a 40-64 character hexadecimal commit identifier.' }
    if ($null -eq $Document.PSObject.Properties['candidateSha'] -or [string]::IsNullOrWhiteSpace([string]$Document.candidateSha)) { return 'blocked: candidate SHA provenance is missing.' }
    if ([string]$Document.candidateSha -cne $candidate) { return 'failed: candidate SHA does not match the requested candidate.' }
    return $null
}

function Test-Summary {
    param([Parameter(Mandatory = $true)][object] $Document, [Parameter(Mandatory = $true)][int] $Denominator)
    if ($null -eq $Document.PSObject.Properties['summary']) { return 'summary is missing.' }
    $summary = $Document.summary
    $required = @('status', 'denominator', 'executed', 'passed', 'failed', 'blocked', 'skipped')
    foreach ($property in $required) { if ($null -eq $summary.PSObject.Properties[$property]) { return "summary.$property is missing." } }
    if ([string]$summary.status -ne 'passed') { return "summary status is '$($summary.status)'." }
    if ([int]$summary.denominator -ne $Denominator -or [int]$summary.executed -ne $Denominator -or [int]$summary.passed -ne $Denominator) { return 'summary does not close its fixed denominator.' }
    if ([int]$summary.failed -ne 0 -or [int]$summary.blocked -ne 0 -or [int]$summary.skipped -ne 0) { return 'summary contains failure, blocked, or skipped rows.' }
    return $null
}

function Test-ExpandedManifest {
    param([Parameter(Mandatory = $true)][object] $Document, [Parameter(Mandatory = $true)][string] $Profile)
    $manifestPath = Join-Path $root 'tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v1-manifest.json'
    if (-not [IO.File]::Exists($manifestPath)) { return @('blocked: expanded suite manifest is missing.') }
    try {
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        if ($manifestBytes.Length -lt 1 -or $manifestBytes.Length -gt 256KB) { return @('blocked: expanded suite manifest exceeds its byte bound.') }
        $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        $suite = @($manifest.suites | Where-Object { $_.profile -ceq $Profile })
        if ($suite.Count -ne 1) { return @('blocked: expanded suite manifest profile is missing or duplicated.') }
        $suite = $suite[0]
        $errors = [Collections.Generic.List[string]]::new()
        if ($null -eq $Document.PSObject.Properties['manifest']) { [void]$errors.Add('blocked: report manifest provenance is missing.') }
        else {
            if ([string]$Document.manifest.sha256 -cne ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)))) { [void]$errors.Add('manifest hash does not match the frozen expanded suite.') }
            if ([int]$Document.manifest.version -ne [int]$manifest.version -or [int]$Document.manifest.denominator -ne [int]$suite.denominator -or $Document.manifest.validated -ne $true) { [void]$errors.Add('manifest version, denominator, or validation marker is stale.') }
        }
        $expectedById = @{}
        foreach ($fixture in @($suite.cases)) { $expectedById[[string]$fixture.id] = $fixture }
        $seen = @{}
        foreach ($case in @($Document.cases)) {
            if ($null -eq $case.PSObject.Properties['id']) { [void]$errors.Add('case ID is missing from the expanded report.'); continue }
            $id = [string]$case.id
            if ($seen.ContainsKey($id)) { [void]$errors.Add("duplicate expanded case ID '$id'."); continue }
            $seen[$id] = $true
            if (-not $expectedById.ContainsKey($id)) { [void]$errors.Add("unexpected expanded case ID '$id'."); continue }
            $expected = $expectedById[$id]
            if ($null -eq $case.PSObject.Properties['sourceSha256'] -or [string]$case.sourceSha256 -cne [string]$expected.sourceSha256) { [void]$errors.Add("case '$id' source hash is stale or missing.") }
            if ($null -eq $case.PSObject.Properties['expectationSha256'] -or [string]$case.expectationSha256 -cne [string]$expected.expectationSha256) { [void]$errors.Add("case '$id' expectation hash is stale or missing.") }
        }
        if ($seen.Count -ne $expectedById.Count) { [void]$errors.Add('expanded report case IDs are incomplete.') }
        return @($errors.ToArray())
    }
    catch { return @('blocked: expanded suite manifest could not be validated: ' + $_.Exception.Message) }
}

function Test-Report {
    param(
        [Parameter(Mandatory = $true)][string] $Gate,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter()][object] $Document,
        [Parameter(Mandatory = $true)][string] $EvidenceKind,
        [Parameter(Mandatory = $true)][string] $Profile,
        [Parameter(Mandatory = $true)][int] $Denominator,
        [Parameter()][string] $RuntimeIdentifier = '',
        [Parameter()][int] $CaseDenominator = -1
    )
    if ($null -eq $Document) { Add-Check $Gate $Name 'blocked' 'Evidence report is missing or unreadable.'; return 'blocked' }
    $errors = [Collections.Generic.List[string]]::new()
    if ($null -ne ($candidateError = Test-Candidate $Name $Document)) { [void]$errors.Add($candidateError) }
    $actualEvidenceKind = if ($null -ne $Document.PSObject.Properties['evidenceKind']) { [string]$Document.evidenceKind } else { '' }
    $actualProfile = if ($null -ne $Document.PSObject.Properties['profile']) { [string]$Document.profile } else { '' }
    if ($actualEvidenceKind -cne $EvidenceKind) { [void]$errors.Add("evidenceKind is '$actualEvidenceKind'.") }
    if ($actualProfile -cne $Profile) { [void]$errors.Add("profile is '$actualProfile'.") }
    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier) -and ($null -eq $Document.PSObject.Properties['platform'] -or $null -eq $Document.platform.PSObject.Properties['runtimeIdentifier'] -or [string]$Document.platform.runtimeIdentifier -cne $RuntimeIdentifier)) { [void]$errors.Add('runtime identifier does not match the required native platform.') }
    if ($null -ne ($summaryError = Test-Summary $Document $Denominator)) { [void]$errors.Add($summaryError) }
    if ($CaseDenominator -ge 0) {
        if ($null -eq $Document.PSObject.Properties['cases'] -or @($Document.cases).Count -ne $CaseDenominator) { [void]$errors.Add('case denominator does not match the fixed suite.') }
        elseif (@($Document.cases | Where-Object { $null -eq $_.PSObject.Properties['status'] -or $_.status -ne 'passed' }).Count -gt 0) { [void]$errors.Add('one or more cases did not pass.') }
    }
    if ($Profile -in @('p1-differential-v3', 'p1-platform-v2')) {
        foreach ($manifestError in @(Test-ExpandedManifest $Document $Profile)) { [void]$errors.Add($manifestError) }
        foreach ($bindingError in @(Test-P1ExpandedEvidence $Document $root $Profile $RuntimeIdentifier $candidate)) { [void]$errors.Add($bindingError) }
    }
    elseif ($CaseDenominator -lt 0) {
        $gateRows = if ($null -ne $Document.PSObject.Properties['Gates']) { @($Document.Gates) } elseif ($null -ne $Document.PSObject.Properties['Checks']) { @($Document.Checks) } else { @() }
        if (@($gateRows).Count -ne $Denominator) { [void]$errors.Add('gate/check denominator does not match the fixed report.') }
        elseif (@($gateRows | Where-Object { $_.Status -ne 'passed' }).Count -gt 0) { [void]$errors.Add('one or more gate/check rows did not pass.') }
    }
    $status = if ($errors.Count -eq 0) { 'passed' } elseif (@($errors | Where-Object { $_ -like 'blocked:*' }).Count -gt 0 -or $null -eq $Document) { 'blocked' } else { 'failed' }
    $message = if ($errors.Count -eq 0) { 'Evidence report closes its fixed denominator and provenance.' } else { $errors -join '; ' }
    Add-Check $Gate $Name $status $message
    return $status
}

function Combine-Status {
    param([Parameter(Mandatory = $true)][string[]] $Statuses)
    if (@($Statuses | Where-Object { $_ -eq 'failed' }).Count -gt 0) { return 'failed' }
    if (@($Statuses | Where-Object { $_ -eq 'blocked' }).Count -gt 0) { return 'blocked' }
    return 'passed'
}

try {
    if ([string]::IsNullOrWhiteSpace($candidate)) { Add-Check 'P1-GATE.05' 'candidate-sha' 'blocked' 'Candidate SHA is unavailable; pass -CandidateSha or set GITHUB_SHA/CI_COMMIT_SHA.' }
    elseif ($candidate -notmatch '^[0-9a-fA-F]{40,64}$') { Add-Check 'P1-GATE.05' 'candidate-sha' 'failed' 'Candidate SHA is not a 40-64 character hexadecimal commit identifier.' }
    else { Add-Check 'P1-GATE.05' 'candidate-sha' 'passed' 'Candidate SHA format is valid.' }

    $old = Read-BoundedJson 'old-exit-gate' $OldExitGateReport
    $expandedExit = Read-BoundedJson 'expanded-exit-gate' $ExpandedExitGateReport
    $coverage = Read-BoundedJson 'coverage' $CoverageReport
    $wd = Read-BoundedJson 'expanded-windows-differential' $ExpandedWindowsDifferentialReport
    $ld = Read-BoundedJson 'expanded-linux-differential' $ExpandedLinuxDifferentialReport
    $wp = Read-BoundedJson 'expanded-windows-platform' $ExpandedWindowsPlatformReport
    $lp = Read-BoundedJson 'expanded-linux-platform' $ExpandedLinuxPlatformReport

    $coverageStatus = Test-Report 'P1-GATE.01' 'coverage' $coverage 'p1-requirement-coverage' 'p1-coverage-v1' 40 -CaseDenominator 160
    $requirementErrors = @(Test-P1RequirementClosure $root)
    if ($requirementErrors.Count -gt 0) { Add-Check 'P1-GATE.01' 'designated-leaf-closure' 'blocked' ($requirementErrors -join '; '); $coverageStatus = Combine-Status @($coverageStatus, 'blocked') }
    if ($null -ne $coverage -and ($null -eq $coverage.PSObject.Properties['summary'] -or $null -eq $coverage.summary.PSObject.Properties['caseDenominator'] -or [int]$coverage.summary.caseDenominator -ne 160)) { Add-Check 'P1-GATE.01' 'coverage-cases' 'failed' 'Coverage case denominator must equal 160.'; $coverageStatus = 'failed' }
    $diffWindows = Test-Report 'P1-GATE.02' 'expanded-windows-differential' $wd 'p1-source-borrow-drop-differential' 'p1-differential-v3' 32 'win-x64' -CaseDenominator 32
    $diffLinux = Test-Report 'P1-GATE.02' 'expanded-linux-differential' $ld 'p1-source-borrow-drop-differential' 'p1-differential-v3' 32 'linux-x64' -CaseDenominator 32
    $platformWindows = Test-Report 'P1-GATE.03' 'expanded-windows-platform' $wp 'p1-platform-coreclr-ilverify-native-aot' 'p1-platform-v2' 24 'win-x64' -CaseDenominator 24
    $platformLinux = Test-Report 'P1-GATE.03' 'expanded-linux-platform' $lp 'p1-platform-coreclr-ilverify-native-aot' 'p1-platform-v2' 24 'linux-x64' -CaseDenominator 24
    $oldStatus = Test-Report 'P1-GATE.04' 'old-exit-gate' $old 'p1-complete-exit-gate' 'p1-differential-v2' 6
    $expandedExitStatus = Test-Report 'P1-GATE.04' 'expanded-exit-gate' $expandedExit 'p1-expanded-complete-exit-gate' 'p1-expanded-v1' 4

    $gateStatuses = [ordered]@{
        'P1-GATE.01' = $coverageStatus
        'P1-GATE.02' = (Combine-Status @($diffWindows, $diffLinux))
        'P1-GATE.03' = (Combine-Status @($platformWindows, $platformLinux))
        'P1-GATE.04' = (Combine-Status @($oldStatus, $expandedExitStatus, $coverageStatus))
    }
    $candidateStatus = if ([string]::IsNullOrWhiteSpace($candidate)) { 'blocked' } elseif ($candidate -notmatch '^[0-9a-fA-F]{40,64}$') { 'failed' } else { 'passed' }
    $gateFiveStatus = Combine-Status @($gateStatuses.Values + @($platformWindows, $platformLinux, $candidateStatus))
    Add-Check 'P1-GATE.05' 'native-candidate-aggregate' $gateFiveStatus $(if ($gateFiveStatus -eq 'passed') { 'Both native x64 platform reports and all prerequisite gates bind to one candidate SHA.' } else { 'Native platform, candidate SHA, or prerequisite evidence is incomplete.' })
    $closureStatus = 'blocked'
    try {
        $closureFull = Resolve-RepositoryPath $ClosureRecordPath
        if (-not [IO.File]::Exists($closureFull)) { Add-Check 'P1-GATE.06' 'closure-record' 'blocked' 'P1 closure record is missing.' }
        else {
            if (([IO.FileInfo]::new($closureFull)).Length -gt $MaximumReportBytes) { Add-Check 'P1-GATE.06' 'closure-record' 'blocked' 'P1 closure record exceeds its byte bound.' }
            else {
                $closure = [IO.File]::ReadAllText($closureFull) | ConvertFrom-Json -Depth 32
                $closureErrors = @(Test-P1ClosureRecord $closure $candidate $root @($inputs.ToArray()))
                if ($closureErrors.Count -gt 0) { $closureStatus = if (@($closureErrors | Where-Object { $_ -like 'blocked:*' }).Count -gt 0) { 'blocked' } else { 'failed' }; Add-Check 'P1-GATE.06' 'closure-record' $closureStatus ($closureErrors -join '; ') }
                elseif ($gateFiveStatus -ne 'passed') { Add-Check 'P1-GATE.06' 'closure-record' 'blocked' 'P1-GATE.05 is not passed; closure cannot be published.' }
                else { $closureStatus = 'passed'; Add-Check 'P1-GATE.06' 'closure-record' 'passed' 'Structured closure binds all leaves, reports, native runs, documents and cleanup.' }
            }
        }
    }
    catch { Add-Check 'P1-GATE.06' 'closure-record' 'blocked' $_.Exception.Message }
    $gateStatuses['P1-GATE.05'] = $gateFiveStatus
    $gateStatuses['P1-GATE.06'] = $closureStatus
}
catch { $harnessError = $_.Exception.Message; Add-Check 'harness' 'harness' 'blocked' $harnessError }

$clock.Stop()
$gateRows = @(foreach ($entry in $gateStatuses.GetEnumerator()) {
    $rowChecks = @($checks | Where-Object Gate -eq $entry.Key)
    [pscustomobject][ordered]@{ Id = $entry.Key; Status = $entry.Value; Checks = @($rowChecks).Count }
})
$failed = @($gateRows | Where-Object Status -eq 'failed').Count
$blocked = @($gateRows | Where-Object Status -eq 'blocked').Count
$passed = @($gateRows | Where-Object Status -eq 'passed').Count
$status = if ($harnessError -or $blocked -gt 0) { 'blocked' } elseif ($failed -gt 0) { 'failed' } else { 'passed' }
$fullEvidence = Resolve-RepositoryPath $EvidencePath
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullEvidence)) | Out-Null
$report = [ordered]@{
    SchemaVersion = 1; EvidenceKind = 'p1-candidate-sha-gate'; Profile = 'p1-candidate-gate-v1'; CandidateSha = $candidate
    Summary = [ordered]@{ Status = $status; ExitCode = if ($status -eq 'passed') { 0 } elseif ($status -eq 'failed') { 1 } else { 2 }; Denominator = 6; Executed = @($gateRows).Count; Passed = $passed; Failed = $failed; Blocked = $blocked; Skipped = 0 }
    Gates = @($gateRows); Checks = @($checks.ToArray()); Inputs = @($inputs.ToArray())
    Execution = [ordered]@{ StartedAtUtc = $started; FinishedAtUtc = [DateTimeOffset]::UtcNow; ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds; MaximumReportBytes = $MaximumReportBytes }
    HarnessError = $harnessError
}
$temporary = $fullEvidence + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
try { [IO.File]::WriteAllText($temporary, ($report | ConvertTo-Json -Depth 16)); [IO.File]::Move($temporary, $fullEvidence, $true) }
finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
Write-Output ([IO.File]::ReadAllText($fullEvidence))
if ($status -eq 'passed') { exit 0 } elseif ($status -eq 'failed') { exit 1 } else { exit 2 }
