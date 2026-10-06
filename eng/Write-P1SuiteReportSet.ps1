[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory)][string] $ReportDirectory,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [ValidateRange(1,60)][int] $TimeoutSeconds = 30
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$directory = [IO.Path]::GetFullPath($ReportDirectory, $root)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $directory.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Report directory escapes the repository.' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$names = [ordered]@{ coverage='p1-coverage-v1.json'; expandedDifferential='p1-differential-v3.json'; expandedPlatform='p1-platform-v2.json'; build='release-build.json'; harness='regression-harness.json'; baselineAudit='immutable-regression-audit.json'; sourceSnapshot='source-snapshot.json' }
$bindings = [ordered]@{}
foreach ($entry in $names.GetEnumerator()) {
    if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Report-set creation deadline expired.' }
    $full = Join-Path $directory $entry.Value
    if (-not [IO.File]::Exists($full) -or ([IO.FileInfo]::new($full)).Length -gt 16MB) { throw "Report is missing or oversized: $($entry.Key)" }
    $bindings[$entry.Key] = @{ path=[IO.Path]::GetRelativePath($root,$full).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
}
$record = [ordered]@{ schemaVersion=1; evidenceKind='p1-suite-report-set'; candidateSha=$CandidateSha; runtimeIdentifier=$RuntimeIdentifier; reports=$bindings }
$path = Join-Path $directory 'p1-suite-report-set.json'
$temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try { [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 8)); [IO.File]::Move($temporary,$path,$true) }
finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
Write-Output "Bound seven P1 suite reports: $path"
