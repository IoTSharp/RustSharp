[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DifferentialWindowsReport,
    [Parameter(Mandatory = $true)][string] $DifferentialLinuxReport,
    [Parameter(Mandatory = $true)][string] $PlatformWindowsReport,
    [Parameter(Mandatory = $true)][string] $PlatformLinuxReport,
    [Parameter()][string] $EvidencePath = 'artifacts/p1-expanded/p1-expanded-exit-gate.json',
    [Parameter()][string] $CandidateSha = '',
    [Parameter()][ValidateRange(1024, 33554432)][int] $MaximumReportBytes = 16MB
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Test-P1ExpandedExitGate.ps1 requires PowerShell 7 or newer.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$started = [DateTimeOffset]::UtcNow
$checks = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$errorText = $null
$candidate = if ([string]::IsNullOrWhiteSpace($CandidateSha)) { $env:GITHUB_SHA } else { $CandidateSha }
if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:CI_COMMIT_SHA }
if (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidate -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'CandidateSha must be a 40-64 character hexadecimal commit identifier.'
}

function Read-Report([string] $name, [string] $path) {
    $full = [IO.Path]::GetFullPath($path, $root)
    if (-not [IO.File]::Exists($full)) { [void]$checks.Add([pscustomobject]@{ Name=$name; Status='blocked'; Message='Report is missing.' }); return $null }
    if (([IO.FileInfo]::new($full)).Length -gt $MaximumReportBytes) { [void]$checks.Add([pscustomobject]@{ Name=$name; Status='failed'; Message='Report exceeds byte bound.' }); return $null }
    $bytes = [IO.File]::ReadAllBytes($full)
    if ($bytes.Length -lt 1 -or $bytes.Length -gt $MaximumReportBytes) { [void]$checks.Add([pscustomobject]@{ Name=$name; Status='failed'; Message='Report exceeds byte bound.' }); return $null }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    [void]$inputs.Add([pscustomobject]@{ Name=$name; Path=[IO.Path]::GetRelativePath($root,$full).Replace('\','/'); Sha256=$hash; Bytes=$bytes.Length })
    try { return ConvertFrom-P1StrictJson $bytes } catch { [void]$checks.Add([pscustomobject]@{ Name=$name; Status='failed'; Message='Invalid JSON: ' + $_.Exception.Message }); return $null }
}

function Validate-Expanded([string] $name, [object] $report, [string] $profile, [int] $denominator, [string] $rid) {
    if ($null -eq $report) { return }
    $errors = @(Test-P1ExpandedEvidence $report $root $profile $rid $candidate)
    $blocked = @($errors | Where-Object { $_ -like 'blocked:*' }).Count -gt 0
    $status = if ($errors.Count -eq 0) { 'passed' } elseif ($blocked) { 'blocked' } else { 'failed' }
    $message = if ($errors.Count -eq 0) { 'Evidence report closes its fixed denominator.' } else { $errors -join '; ' }
    [void]$checks.Add([pscustomobject]@{ Name=$name; Status=$status; Message=$message })
}

try {
    Validate-Expanded 'windows-differential-v3' (Read-Report 'windows-differential-v3' $DifferentialWindowsReport) 'p1-differential-v3' 32 'win-x64'
    Validate-Expanded 'linux-differential-v3' (Read-Report 'linux-differential-v3' $DifferentialLinuxReport) 'p1-differential-v3' 32 'linux-x64'
    Validate-Expanded 'windows-platform-v2' (Read-Report 'windows-platform-v2' $PlatformWindowsReport) 'p1-platform-v2' 24 'win-x64'
    Validate-Expanded 'linux-platform-v2' (Read-Report 'linux-platform-v2' $PlatformLinuxReport) 'p1-platform-v2' 24 'linux-x64'
}
catch { $errorText = $_.Exception.Message; [void]$checks.Add([pscustomobject]@{ Name='harness'; Status='blocked'; Message=$errorText }) }

$failed = @($checks | Where-Object Status -eq 'failed').Count
$blocked = @($checks | Where-Object Status -eq 'blocked').Count
$passed = @($checks | Where-Object Status -eq 'passed').Count
$status = if ($errorText -or $blocked -gt 0) { 'blocked' } elseif ($failed -gt 0) { 'failed' } else { 'passed' }
$fullEvidence = [IO.Path]::GetFullPath($EvidencePath, $root)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullEvidence)) | Out-Null
$report = [ordered]@{ SchemaVersion=1; EvidenceKind='p1-expanded-complete-exit-gate'; Profile='p1-expanded-v1'; CandidateSha=$candidate; Summary=[ordered]@{ Status=$status; ExitCode=if($status -eq 'passed'){0}elseif($status -eq 'failed'){1}else{2}; Denominator=4; Executed=$checks.Count; Passed=$passed; Failed=$failed; Blocked=$blocked; Skipped=0 }; Checks=@($checks.ToArray()); Inputs=@($inputs.ToArray()); Execution=[ordered]@{ StartedAtUtc=$started; FinishedAtUtc=[DateTimeOffset]::UtcNow; MaximumReportBytes=$MaximumReportBytes }; HarnessError=$errorText }
$temp = $fullEvidence + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
try { [IO.File]::WriteAllText($temp, ($report | ConvertTo-Json -Depth 12)); [IO.File]::Move($temp, $fullEvidence, $true) } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
Write-Output ([IO.File]::ReadAllText($fullEvidence))
if ($status -eq 'passed') { exit 0 } elseif ($status -eq 'failed') { exit 1 } else { exit 2 }
