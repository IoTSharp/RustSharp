[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string] $PlatformName,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][ValidateSet('success','failure','cancelled')][string] $UpstreamStatus,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$relativeDirectory = "artifacts/p1-expanded/$PlatformName"
$directory = Join-Path $root $relativeDirectory
$null = [IO.Directory]::CreateDirectory($directory)
$clock = [Diagnostics.Stopwatch]::StartNew()
$rid = if ($PlatformName -ceq 'windows-x64') { 'win-x64' } else { 'linux-x64' }
$bindings = [ordered]@{}
$inventory = [Collections.Generic.List[object]]::new()
$missing = [Collections.Generic.List[string]]::new()
$reportFiles = [ordered]@{
    coverage='p1-coverage-v1.json'; expandedDifferential='p1-differential-v3.json'; expandedPlatform='p1-platform-v2.json';
    build='release-build.json'; harness='regression-harness.json'; baselineAudit='immutable-regression-audit.json'; sourceSnapshot='source-snapshot.json'
}
$denominators = @{ coverage=40; expandedDifferential=32; expandedPlatform=24; build=2; harness=464; baselineAudit=60; sourceSnapshot=1 }
$nativeHost = ($PlatformName -ceq 'windows-x64' -and $IsWindows) -or ($PlatformName -ceq 'linux-x64' -and $IsLinux)
$nativeExecution = $nativeHost -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ceq 'X64'
$blocked = $UpstreamStatus -cne 'success' -or -not $nativeExecution
# Seven known report files plus the report-set; no directory scan or retry.
foreach ($key in $reportFiles.Keys) {
    if ($clock.Elapsed.TotalSeconds -ge 15) { throw 'CI publication exceeded its 15-second deadline.' }
    $path = Join-Path $directory $reportFiles[$key]
    if (-not [IO.File]::Exists($path)) {
        $missing.Add($key)
        $blocked = $true
        # This envelope describes absent evidence. It does not invent process
        # outcomes, case execution or semantic compile-fail success.
        [ordered]@{ schemaVersion=1; evidenceKind='p1-unavailable-ci-evidence'; candidateSha=$CandidateSha; runtimeIdentifier=$rid; requestedReport=$key; succeeded=$false; summary=@{ status='blocked'; denominator=$denominators[$key]; executed=0; passed=0; failed=0; blocked=$denominators[$key]; skipped=0 }; harnessError="The native job did not produce '$($reportFiles[$key])'; upstream status: $UpstreamStatus. Inspect the retained bounded launch/preflight diagnostics." } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    }
    $info = [IO.FileInfo]::new($path)
    if ($info.Length -lt 1 -or $info.Length -gt 32MB) { throw "Report '$key' exceeds its nonempty 32 MiB byte bound." }
    try {
        $value = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 64
        $summary = $value.PSObject.Properties['summary']
        if ($summary -and $summary.Value.PSObject.Properties['status'] -and $summary.Value.status -cne 'passed') { $blocked = $true }
        if ($value.PSObject.Properties['succeeded'] -and $value.succeeded -cne $true) { $blocked = $true }
    } catch { $blocked = $true }
    $relative = "$relativeDirectory/$($reportFiles[$key])"
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $bindings[$key] = [ordered]@{ path=$relative; sha256=$hash }
    $inventory.Add([ordered]@{ name=$key; path=$relative; sha256=$hash; bytes=$info.Length })
}
$setPath = Join-Path $directory 'p1-suite-report-set.json'
[ordered]@{ schemaVersion=1; evidenceKind='p1-suite-report-set'; candidateSha=$CandidateSha; runtimeIdentifier=$rid; reports=$bindings } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $setPath -Encoding utf8
$inventory.Add([ordered]@{ name='reportSet'; path="$relativeDirectory/p1-suite-report-set.json"; sha256=(Get-FileHash -LiteralPath $setPath -Algorithm SHA256).Hash; bytes=([IO.FileInfo]::new($setPath)).Length })
$runUrl = "https://github.com/$Repository/actions/runs/$RunId"
$artifactName = "p1-expanded-$PlatformName-$RunId-$RunAttempt"
[ordered]@{ schemaVersion=1; evidenceKind='p1-ci-publication'; candidateSha=$CandidateSha; platformName=$PlatformName; runtimeIdentifier=$rid; observedRuntimeIdentifier=[Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); upstreamStatus=$UpstreamStatus; status=if($blocked){'blocked'}else{'passed'}; nativeExecution=$nativeExecution; run=@{ repository=$Repository; id=$RunId; attempt=$RunAttempt; url=$runUrl; attemptUrl="$runUrl/attempts/$RunAttempt" }; artifact=@{ name=$artifactName; runUrl=$runUrl; downloadCommand="gh run download $RunId --repo $Repository --name $artifactName" }; missingReports=@($missing.ToArray()); reports=@($inventory.ToArray()); publishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'ci-publication.json') -Encoding utf8
Write-Host "CI publication inventory: $runUrl/attempts/$RunAttempt; artifact $artifactName; missing reports $($missing.Count)."
