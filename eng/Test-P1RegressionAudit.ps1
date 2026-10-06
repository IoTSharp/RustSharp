[CmdletBinding()]
param(
    [Parameter()][string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $ReportPath,
    [Parameter()][ValidateRange(1, 300)][int] $DeadlineSeconds = 120
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$reportFullPath = [IO.Path]::GetFullPath($ReportPath, $root)
# Last immutable baseline introduction: regression v1/v2=8/24, differential v2=16, platform v1=12.
# This identity is deliberately fixed, never selected from the candidate or current HEAD.
$baselineCommit = '23279d93267a814c643baddc29c72918ff0fda0b'
$fixturePrefix = 'tools/RustSharp.Conformance/fixtures/'
$platformScript = 'eng/Invoke-P1PlatformEvidence.ps1'
$maximumFiles = 512
$maximumCases = 60
$clock = [Diagnostics.Stopwatch]::StartNew()
$processRecords = [Collections.Generic.List[object]]::new()
$suiteRecords = [Collections.Generic.List[object]]::new()
$auditErrors = [Collections.Generic.List[string]]::new()
$ownedTemporaryPath = $null
$candidateTreeSha = $null
$checkedFiles = 0
$sourceMatches = $false
$harnessError = $null
$startedAt = [DateTimeOffset]::UtcNow
$gitExecutable = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

function Assert-Budget {
    if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Immutable regression audit deadline expired.' }
}

function Invoke-AuditGit {
    param([Parameter(Mandatory)][string[]] $Arguments, [string] $InputText)
    Assert-Budget
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $gitExecutable
    $process.StartInfo.WorkingDirectory = $root
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    $started = $false
    $record = $null
    try {
        if (-not $process.Start()) { throw 'Could not start the owned audit Git process.' }
        $started = $true
        $record = [ordered]@{
            processId = $process.Id; parentProcessId = $PID; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            executable = $process.StartInfo.FileName; arguments = $Arguments; workingDirectory = $root
            commandLine = $process.StartInfo.FileName + ' ' + (($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
            timeoutSeconds = [Math]::Min(15, [Math]::Max(1, $DeadlineSeconds - [int]$clock.Elapsed.TotalSeconds))
            exitCode = $null; cleanupComplete = $false
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        # Both count and wall deadline bound this wait; one-second yielding permits cancellation.
        for ($attempt = 0; $attempt -lt $record.timeoutSeconds; $attempt++) {
            Assert-Budget
            if ($process.WaitForExit(1000)) { break }
        }
        if (-not $process.HasExited) { throw 'Owned audit Git command exceeded its time bound.' }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw 'Git output did not drain.' }
        if (($stdout.Result.Length + $stderr.Result.Length) -gt 2097152) { throw 'Git output exceeded the audit budget.' }
        if ($process.ExitCode -ne 0) { throw "Git audit command failed: $($stderr.Result)" }
        return $stdout.Result
    }
    finally {
        if ($started) {
            # The Process object and recorded start identity identify this script's child only.
            if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
            $record.exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
            $record.cleanupComplete = $process.HasExited
            $processRecords.Add($record)
        }
        $process.Dispose()
    }
}

function Get-TreeMap {
    param([string] $Commit)
    $text = Invoke-AuditGit -Arguments @('ls-tree', '-r', '-z', $Commit, '--', $fixturePrefix, $platformScript)
    $entries = @($text.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    if ($entries.Count -gt $maximumFiles) { throw 'Legacy tree inventory exceeds its file bound.' }
    $map = @{}
    foreach ($entry in $entries) {
        Assert-Budget
        if ($entry -notmatch '^100[0-9]{3} blob ([a-f0-9]{40})\t(.+)$') { throw 'Malformed legacy tree entry.' }
        if ($map.ContainsKey($Matches[2])) { throw 'Duplicate tree path.' }
        $map[$Matches[2]] = $Matches[1]
    }
    return $map
}

function Get-TextHash {
    param([string] $Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

try {
    $candidateCommit = (Invoke-AuditGit -Arguments @('rev-parse', ($CandidateSha + '^{commit}'))).Trim()
    if ($candidateCommit -ine $CandidateSha) { throw 'Candidate identity must refer to a commit.' }
    $candidateTreeSha = (Invoke-AuditGit -Arguments @('rev-parse', ($CandidateSha + '^{tree}'))).Trim()
    if ($candidateTreeSha -notmatch '^[a-f0-9]{40}$') { throw 'Candidate tree identity is invalid.' }
    $baseline = Get-TreeMap -Commit $baselineCommit
    $candidate = Get-TreeMap -Commit $CandidateSha
    $suiteSpecs = @(
        @{ profile = 'safe-core-regression-v1'; manifest = 'safe-core-regression-manifest.json'; denominator = 8 },
        @{ profile = 'safe-core-regression-v2'; manifest = 'safe-core-regression-v2-manifest.json'; denominator = 24 },
        @{ profile = 'p1-differential-v2'; manifest = 'p1-differential-v2-manifest.json'; denominator = 16 },
        @{ profile = 'p1-platform-v1'; manifest = 'p1-differential-v2-manifest.json'; denominator = 12 }
    )
    $relevantPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $relevantPaths.Add($platformScript)
    $platformIds = $null
    $baselineScript = Invoke-AuditGit -Arguments @('show', ($baselineCommit + ':' + $platformScript))
    $candidateScript = Invoke-AuditGit -Arguments @('show', ($CandidateSha + ':' + $platformScript))
    $selectionPattern = '(?s)\$fixedRunPassIds\s*=\s*@\((.*?)\)'
    if ($baselineScript -notmatch $selectionPattern) { throw 'Pinned platform selection is missing.' }
    $baselineSelection = $Matches[1]
    if ($candidateScript -notmatch $selectionPattern) { throw 'Candidate platform selection is missing.' }
    $candidateSelection = $Matches[1]
    $idPattern = "'([a-z0-9-]+)'"
    $baselinePlatformIds = @([regex]::Matches($baselineSelection, $idPattern) | ForEach-Object { $_.Groups[1].Value })
    $platformIds = @([regex]::Matches($candidateSelection, $idPattern) | ForEach-Object { $_.Groups[1].Value })
    $platformIdsUnchanged = $baselinePlatformIds.Count -eq 12 -and $platformIds.Count -eq 12 -and
        ($baselinePlatformIds -join "`n") -ceq ($platformIds -join "`n")

    foreach ($spec in $suiteSpecs) {
        Assert-Budget
        $path = $fixturePrefix + $spec.manifest
        $null = $relevantPaths.Add($path)
        if (-not $baseline.ContainsKey($path) -or -not $candidate.ContainsKey($path)) { throw "Missing frozen manifest $path" }
        $baselineJson = Invoke-AuditGit -Arguments @('show', ($baselineCommit + ':' + $path))
        $candidateJson = Invoke-AuditGit -Arguments @('show', ($CandidateSha + ':' + $path))
        $old = $baselineJson | ConvertFrom-Json -Depth 24
        $new = $candidateJson | ConvertFrom-Json -Depth 24
        $oldCases = @($old.cases)
        $newCases = @($new.cases)
        if ($spec.profile -eq 'p1-platform-v1') {
            $oldCases = @($oldCases | Where-Object { $_.id -in $baselinePlatformIds })
            $newCases = @($newCases | Where-Object { $_.id -in $platformIds })
        }
        if ($oldCases.Count -ne $spec.denominator -or $newCases.Count -gt $maximumCases) { throw 'Pinned suite denominator or candidate case bound is invalid.' }
        $oldIds = @($oldCases | ForEach-Object { $_.id })
        $newIds = @($newCases | ForEach-Object { $_.id })
        $idsUnchanged = $newCases.Count -eq $spec.denominator -and ($oldIds -join "`n") -ceq ($newIds -join "`n") -and
            @($newIds | Select-Object -Unique).Count -eq $spec.denominator
        if ($spec.profile -eq 'p1-platform-v1') { $idsUnchanged = $idsUnchanged -and $platformIdsUnchanged }
        $cases = [Collections.Generic.List[object]]::new()
        foreach ($oldCase in $oldCases) {
            Assert-Budget
            $newCase = @($newCases | Where-Object { $_.id -ceq $oldCase.id })
            $sourcePath = $fixturePrefix + $oldCase.file
            $null = $relevantPaths.Add($sourcePath)
            $oldExpectation = $oldCase | ConvertTo-Json -Depth 24 -Compress
            $newExpectation = if ($newCase.Count -eq 1) { $newCase[0] | ConvertTo-Json -Depth 24 -Compress } else { '' }
            $expectationUnchanged = $oldExpectation -ceq $newExpectation
            $sourceUnchanged = $baseline.ContainsKey($sourcePath) -and $candidate.ContainsKey($sourcePath) -and
                $baseline[$sourcePath] -ceq $candidate[$sourcePath]
            $cases.Add([ordered]@{
                id = $oldCase.id; source = $oldCase.file; expectationUnchanged = $expectationUnchanged; sourceUnchanged = $sourceUnchanged
                baselineSourceGitBlob = if ($baseline.ContainsKey($sourcePath)) { $baseline[$sourcePath] } else { $null }
                candidateSourceGitBlob = if ($candidate.ContainsKey($sourcePath)) { $candidate[$sourcePath] } else { $null }
                baselineExpectationSha256 = Get-TextHash $oldExpectation; candidateExpectationSha256 = Get-TextHash $newExpectation
                status = if ($expectationUnchanged -and $sourceUnchanged) { 'passed' } else { 'failed' }
            })
        }
        $manifestUnchanged = $baseline[$path] -ceq $candidate[$path]
        $expectationsUnchanged = @($cases | Where-Object { -not $_.expectationUnchanged }).Count -eq 0
        $sourcesUnchanged = @($cases | Where-Object { -not $_.sourceUnchanged }).Count -eq 0
        $suiteRecords.Add([ordered]@{
            profile = $spec.profile; denominator = $spec.denominator; manifestPath = $path
            baselineManifestGitBlob = $baseline[$path]; candidateManifestGitBlob = $candidate[$path]
            manifestUnchanged = $manifestUnchanged; caseIdsUnchanged = $idsUnchanged
            expectationsUnchanged = $expectationsUnchanged; sourcesUnchanged = $sourcesUnchanged; cases = @($cases)
        })
        if (-not ($manifestUnchanged -and $idsUnchanged -and $expectationsUnchanged -and $sourcesUnchanged)) {
            $auditErrors.Add("Frozen suite changed: $($spec.profile)")
        }
        Write-Host "Audited $($spec.profile): $($spec.denominator) frozen cases."
    }

    $paths = @($relevantPaths | Sort-Object)
    if ($paths.Count -gt $maximumFiles) { throw 'Relevant legacy paths exceed their fixed bound.' }
    $inputBuilder = [Text.StringBuilder]::new()
    foreach ($path in $paths) {
        Assert-Budget
        $full = [IO.Path]::GetFullPath($path, $root)
        $relative = [IO.Path]::GetRelativePath($root, $full)
        if ($relative.StartsWith('..', [StringComparison]::Ordinal) -or -not [IO.File]::Exists($full) -or $path.Contains('"')) {
            throw "Unsafe or missing working legacy source: $path"
        }
        $null = $inputBuilder.Append('"').Append($full.Replace('\', '/')).Append('"').Append("`n")
    }
    $hashes = @((Invoke-AuditGit -Arguments @('hash-object', '--stdin-paths') -InputText $inputBuilder.ToString()).Split(
        [char[]]"`r`n", [StringSplitOptions]::RemoveEmptyEntries))
    if ($hashes.Count -ne $paths.Count) { throw 'Git omitted a relevant working source hash.' }
    for ($index = 0; $index -lt $paths.Count -and $index -lt $maximumFiles; $index++) {
        Assert-Budget
        $checkedFiles++
        if (-not $candidate.ContainsKey($paths[$index]) -or $hashes[$index] -cne $candidate[$paths[$index]]) {
            $auditErrors.Add("Working legacy content differs from candidate: $($paths[$index])")
        }
    }
    $sourceMatches = $auditErrors.Count -eq 0
}
catch { $harnessError = $_.Exception.Message; $auditErrors.Add($harnessError) }
finally {
    $passed = 0
    foreach ($suite in $suiteRecords) { $passed += @($suite.cases | Where-Object { $_.status -eq 'passed' }).Count }
    $cleanupComplete = @($processRecords | Where-Object { -not $_.cleanupComplete }).Count -eq 0
    $succeeded = $suiteRecords.Count -eq 4 -and $passed -eq 60 -and $sourceMatches -and $auditErrors.Count -eq 0 -and $cleanupComplete
    $report = [ordered]@{
        schemaVersion = 1; evidenceKind = 'p1-immutable-regression-audit'; candidateSha = $CandidateSha.ToLowerInvariant()
        baselineCommit = $baselineCommit; succeeded = $succeeded; startedAtUtc = $startedAt.ToString('O')
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); deadlineExpired = $clock.Elapsed.TotalSeconds -ge $DeadlineSeconds
        bounds = @{ maximumSuites = 4; maximumCases = 60; maximumFiles = $maximumFiles; deadlineSeconds = $DeadlineSeconds; processTimeoutSeconds = 15 }
        sourceProvenance = @{ candidateSha = $CandidateSha.ToLowerInvariant(); candidateTreeSha = $candidateTreeSha
            candidateMatchesWorkingTree = $sourceMatches; checkedFileCount = $checkedFiles; errors = @($auditErrors) }
        summary = @{ suites = $suiteRecords.Count; denominator = 60; passed = $passed; failed = 60 - $passed }
        suites = @($suiteRecords); processes = @($processRecords); cleanupComplete = $cleanupComplete; harnessError = $harnessError; errors = @($auditErrors)
    }
    $directory = [IO.Path]::GetDirectoryName($reportFullPath)
    $null = [IO.Directory]::CreateDirectory($directory)
    $ownedTemporaryPath = Join-Path $directory ('.p1-regression-audit-' + [Guid]::NewGuid().ToString('N') + '.json.tmp')
    try {
        [IO.File]::WriteAllText($ownedTemporaryPath, ($report | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($ownedTemporaryPath, $reportFullPath, $true)
    }
    finally {
        if ([IO.File]::Exists($ownedTemporaryPath) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ownedTemporaryPath)) -eq $directory) {
            Remove-Item -LiteralPath $ownedTemporaryPath
        }
    }
}
if (-not $report.succeeded) { Write-Error -ErrorAction Continue ($auditErrors -join '; '); exit 1 }
Write-Host '✅ Complete: 60/60 immutable baseline cases preserved across four suites.'
