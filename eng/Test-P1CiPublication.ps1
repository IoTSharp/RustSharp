[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string] $EvidencePath = 'artifacts/p1-candidate/ci-publication-reconciliation.json',
    [ValidateSet('success','failure','cancelled','skipped')][string] $NativeJobResult = 'success'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$clock = [Diagnostics.Stopwatch]::StartNew()
$checks = [Collections.Generic.List[object]]::new()
$requiredNames = @('coverage','expandedDifferential','expandedPlatform','build','harness','baselineAudit','sourceSnapshot','reportSet')
foreach ($platform in @('windows-x64','linux-x64')) {
    $diagnostic = $null
    $status = 'failed'
    try {
        $descriptorPath = Join-Path $root "artifacts/p1-expanded/$platform/ci-publication.json"
        if (-not [IO.File]::Exists($descriptorPath)) { $status = 'blocked'; throw 'Native publication inventory is missing.' }
        if (([IO.FileInfo]::new($descriptorPath)).Length -gt 256KB) { throw 'Publication descriptor exceeds 256 KiB.' }
        $descriptor = [IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json -Depth 16
        $rid = if ($platform -ceq 'windows-x64') { 'win-x64' } else { 'linux-x64' }
        $runUrl = "https://github.com/$Repository/actions/runs/$RunId"
        $artifactName = "p1-expanded-$platform-$RunId-$RunAttempt"
        if ($descriptor.schemaVersion -ne 1 -or $descriptor.evidenceKind -cne 'p1-ci-publication' -or $descriptor.candidateSha -cne $CandidateSha -or $descriptor.platformName -cne $platform -or $descriptor.runtimeIdentifier -cne $rid -or $descriptor.nativeExecution -cne $true) { throw 'Publication source/platform identity is mismatched.' }
        $nativeRidMatches = if($rid -ceq 'win-x64'){$descriptor.observedRuntimeIdentifier -ceq 'win-x64'}else{$descriptor.observedRuntimeIdentifier -cmatch '^(linux|ubuntu(?:\.\d+\.\d+)?)-x64$'}
        if (-not $nativeRidMatches -or $descriptor.architecture -cne 'X64') { throw 'Publication observed native host is mismatched.' }
        if ($descriptor.run.repository -cne $Repository -or [string]$descriptor.run.id -cne $RunId -or [string]$descriptor.run.attempt -cne $RunAttempt -or $descriptor.run.url -cne $runUrl -or $descriptor.run.attemptUrl -cne "$runUrl/attempts/$RunAttempt" -or $descriptor.artifact.name -cne $artifactName -or $descriptor.artifact.runUrl -cne $runUrl) { throw 'Publication run/attempt/artifact provenance is mismatched.' }
        if (@($descriptor.reports).Count -ne 8) { throw 'Publication must bind exactly seven reports and one report-set.' }
        $seen = @{}
        foreach ($file in $descriptor.reports) {
            if ($clock.Elapsed.TotalSeconds -ge 15) { throw 'Publication reconciliation exceeded its 15-second deadline.' }
            if ($file.name -cnotin $requiredNames -or $seen.ContainsKey($file.name)) { throw 'Publication contains duplicate or unknown report names.' }
            $seen[$file.name] = $true
            $expectedPrefix = "artifacts/p1-expanded/$platform/"
            if (-not ([string]$file.path).StartsWith($expectedPrefix, [StringComparison]::Ordinal) -or $file.path.Contains('..') -or $file.path.Contains('\')) { throw 'Publication report escapes its platform directory.' }
            $full = [IO.Path]::GetFullPath([string]$file.path, $root)
            if (-not [IO.File]::Exists($full)) { $status='blocked'; throw "Published report '$($file.name)' was not downloaded." }
            $info = [IO.FileInfo]::new($full)
            if ($info.Length -lt 1 -or $info.Length -gt 32MB -or $info.Length -ne $file.bytes -or $file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ine $file.sha256) { throw "Published report '$($file.name)' has stale bytes/hash." }
        }
        $set = [IO.File]::ReadAllText((Join-Path $root "artifacts/p1-expanded/$platform/p1-suite-report-set.json")) | ConvertFrom-Json -Depth 12
        foreach ($name in $requiredNames[0..6]) {
            $published = @($descriptor.reports | Where-Object name -CEQ $name)[0]
            if ($set.reports.$name.path -cne $published.path -or $set.reports.$name.sha256 -ine $published.sha256) { throw "Report-set binding '$name' does not match its publication." }
        }
        if ($NativeJobResult -cne 'success' -or $descriptor.status -cne 'passed' -or $descriptor.upstreamStatus -cne 'success' -or @($descriptor.missingReports).Count -ne 0) { $status='blocked'; throw "Native job/publication is unavailable or unsuccessful ($NativeJobResult/$($descriptor.upstreamStatus))." }
        $status = 'passed'
    } catch { $diagnostic = $_.Exception.Message }
    $checks.Add([ordered]@{ name=$platform; status=$status; diagnostic=$diagnostic })
}
$failed = @($checks | Where-Object { $_.status -ceq 'failed' }).Count
$blocked = @($checks | Where-Object { $_.status -ceq 'blocked' }).Count
$status = if($blocked -gt 0){'blocked'}elseif($failed -gt 0){'failed'}else{'passed'}
$report = [IO.Path]::GetFullPath($EvidencePath, $root)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report))
[ordered]@{ schemaVersion=1; evidenceKind='p1-ci-publication-reconciliation'; candidateSha=$CandidateSha; summary=@{ status=$status; denominator=2; passed=2-$failed-$blocked; failed=$failed; blocked=$blocked; skipped=0 }; checks=@($checks.ToArray()); maximumPlatforms=2; maximumReportsPerPlatform=8; deadlineSeconds=15 } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8
if($status -ceq 'blocked'){exit 2}elseif($status -ceq 'failed'){exit 1}
