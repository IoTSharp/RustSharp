[CmdletBinding()]
param(
    [string] $CandidateSha = '',
    [switch] $CreateCandidate,
    [string] $EvidencePath = 'artifacts/p1-10/source-snapshot.json',
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [ValidateRange(1, 120)][int] $DeadlineSeconds = 90,
    [ValidateRange(1, 8192)][int] $MaximumFiles = 4096
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$clock = [Diagnostics.Stopwatch]::StartNew()
$processes = [Collections.Generic.List[object]]::new()
$files = [Collections.Generic.List[object]]::new()
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('rustsharp-p1-snapshot-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryRoot)
$oldIndex = $env:GIT_INDEX_FILE
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$errorMessage = $null
$tree = $null
$candidateRef = $null

function Invoke-SnapshotGit {
    param([string[]] $Arguments, [string] $InputText = '')
    if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds -or $processes.Count -ge 12) { throw 'Snapshot execution bound exceeded.' }
    $info = [Diagnostics.ProcessStartInfo]::new($git)
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $record = [ordered]@{ pid = $null; parentPid = $PID; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); filePath = $git; arguments = $Arguments; workingDirectory = $root; timeoutSeconds = 30; exitCode = $null; cleanupComplete = $false }
    try {
        if (-not $process.Start()) { throw 'Git process could not start.' }
        $record.pid = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText.Length -gt 1048576) { throw 'Git input byte budget exceeded.' }
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        $remaining = [Math]::Max(1, [Math]::Min(30000, [int](($DeadlineSeconds * 1000) - $clock.ElapsedMilliseconds)))
        if (-not $process.WaitForExit($remaining)) { throw 'Git process deadline exceeded.' }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)) { throw 'Git output drain deadline exceeded.' }
        $record.exitCode = $process.ExitCode
        if ($stdout.Result.Length -gt 4194304 -or $stderr.Result.Length -gt 1048576) { throw 'Git output byte budget exceeded.' }
        if ($process.ExitCode -ne 0) { throw "Git failed: $($stderr.Result)" }
        return $stdout.Result.TrimEnd("`r", "`n")
    }
    finally {
        if ($null -ne $record.pid -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $record.cleanupComplete = $null -ne $record.pid -and $process.HasExited
        $record.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        [void]$processes.Add([pscustomobject]$record)
        $process.Dispose()
    }
}

try {
    if ($CreateCandidate) {
        if ($CandidateSha) { throw 'CreateCandidate and CandidateSha are mutually exclusive.' }
        $env:GIT_INDEX_FILE = Join-Path $temporaryRoot 'index'
        $parent = Invoke-SnapshotGit -Arguments @('rev-parse', 'HEAD')
        $null = Invoke-SnapshotGit -Arguments @('read-tree', $parent)
        # The temporary index preserves the user's HEAD and staging area. Old
        # task scratch trees are excluded; ignored build artifacts stay ignored.
        $null = Invoke-SnapshotGit -Arguments @('add', '-A', '--', '.', ':!tmp')
        # Attribute changes can alter raw fixture storage while cached file
        # metadata still appears unchanged. Reapply the declared filters in
        # this task-owned index before binding the actual candidate blobs.
        $null = Invoke-SnapshotGit -Arguments @('add', '--renormalize', '--', '.', ':!tmp')
        $tree = Invoke-SnapshotGit -Arguments @('write-tree')
        $CandidateSha = Invoke-SnapshotGit -Arguments @('commit-tree', $tree, '-p', $parent, '-m', 'P1-10 local validation candidate snapshot')
        $candidateRef = 'refs/codex/p1-10-candidates/' + $CandidateSha
        $null = Invoke-SnapshotGit -Arguments @('update-ref', $candidateRef, $CandidateSha)
        $env:GIT_INDEX_FILE = $oldIndex
    }
    if ($CandidateSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'An actual 40-character Git candidate commit is required.' }
    $commit = Invoke-SnapshotGit -Arguments @('rev-parse', ($CandidateSha + '^{commit}'))
    if ($commit -ine $CandidateSha) { throw 'Candidate identity must refer to an actual commit.' }
    $tree = Invoke-SnapshotGit -Arguments @('rev-parse', ($CandidateSha + '^{tree}'))
    $entries = (Invoke-SnapshotGit -Arguments @('-c', 'core.quotepath=false', 'ls-tree', '-r', $CandidateSha)) -split "`n"
    if ($entries.Count -gt $MaximumFiles) { throw 'Candidate file denominator exceeds the bound.' }
    $selected = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Source enumeration deadline exceeded.' }
        if ($entry -notmatch '^100(?:644|755) blob ([0-9a-f]{40})\t(.+)$') { throw 'Unsupported candidate tree entry.' }
        $blob = $Matches[1]; $path = $Matches[2]
        if ($path -notmatch '^(src/|tests/|tools/|eng/|\.github/|\.config/|samples/|[^/]+\.(props|targets|slnx|json)$|\.gitattributes$|\.gitignore$)') { continue }
        if ($path.Contains('"') -or $path.Contains("`r") -or $path.Contains("`n")) { throw 'Source paths must have a simple line representation.' }
        [void]$selected.Add([pscustomobject]@{ path = $path; gitBlobOid = $blob })
    }
    if ($selected.Count -lt 1) { throw 'Candidate contains no compiler inputs.' }
    $workingPaths = (Invoke-SnapshotGit -Arguments @('-c', 'core.quotepath=false', 'ls-files', '--cached', '--others', '--exclude-standard')) -split "`n"
    if ($workingPaths.Count -gt $MaximumFiles) { throw 'Working input inventory exceeds the file bound.' }
    $workingInputPaths = @($workingPaths | Where-Object { $_ -match '^(src/|tests/|tools/|eng/|\.github/|\.config/|samples/|[^/]+\.(props|targets|slnx|json)$|\.gitattributes$|\.gitignore$)' } | Sort-Object -Unique)
    $candidateInputPaths = @($selected.path | Sort-Object -Unique)
    if ($workingInputPaths.Count -ne $candidateInputPaths.Count -or @(Compare-Object $workingInputPaths $candidateInputPaths -CaseSensitive).Count -ne 0) { throw 'Candidate and working compiler-input inventories differ.' }
    $pathInput = (($selected | ForEach-Object { '"' + $_.path + '"' }) -join "`n") + "`n"
    $actualOids = (Invoke-SnapshotGit -Arguments @('hash-object', '--stdin-paths') -InputText $pathInput) -split "`n"
    if ($actualOids.Count -ne $selected.Count) { throw 'Candidate input denominator differs from working files.' }
    for ($index = 0; $index -lt $selected.Count -and $index -lt $MaximumFiles; $index++) {
        if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Source hashing deadline exceeded.' }
        $item = $selected[$index]
        if ($actualOids[$index] -cne $item.gitBlobOid) { throw "Candidate source differs from working file: $($item.path)" }
        $full = [IO.Path]::GetFullPath($item.path, $root)
        if (([IO.FileInfo]::new($full)).Length -gt 4194304) { throw 'Source file byte bound exceeded.' }
        $bytes = [IO.File]::ReadAllBytes($full)
        [void]$files.Add([pscustomobject]@{ path = $item.path; gitBlobOid = $item.gitBlobOid; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) })
    }
}
catch { $errorMessage = $_.Exception.Message }
finally {
    $env:GIT_INDEX_FILE = $oldIndex
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolvedTemporary) -cne $tempParent -or -not [IO.Path]::GetFileName($resolvedTemporary).StartsWith('rustsharp-p1-snapshot-', [StringComparison]::Ordinal)) { throw 'Temporary snapshot cleanup ownership check failed.' }
    if ([IO.Directory]::Exists($resolvedTemporary)) { [IO.Directory]::Delete($resolvedTemporary, $true) }
}
$passed = $null -eq $errorMessage
$report = [ordered]@{
    schemaVersion = 1; evidenceKind = 'p1-candidate-source-snapshot'; candidateSha = $CandidateSha; treeSha = $tree; candidateRef = $candidateRef
    verified = $passed; scope = 'compiler-test-fixture-tooling-inputs'; fullP1Closure = $false
    files = @($files.ToArray()); summary = @{ status = if ($passed) { 'passed' } else { 'blocked' }; denominator = $files.Count; verified = $files.Count; mismatched = if ($passed) { 0 } else { 1 } }
    sourceProvenance = @{ candidateSha = $CandidateSha; candidateTreeSha = $tree; treeSha = $tree; candidateContentVerified = $passed; candidateMatchesWorkingTree = $passed; checkedFileCount = $files.Count; errors = @($errorMessage | Where-Object { $null -ne $_ }) }
    processes = @($processes.ToArray()); execution = @{ deadlineSeconds = $DeadlineSeconds; maximumFiles = $MaximumFiles; elapsedMilliseconds = $clock.ElapsedMilliseconds }; harnessError = $errorMessage
}
$fullEvidence = [IO.Path]::GetFullPath($EvidencePath, $root)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullEvidence))
$temporaryEvidence = $fullEvidence + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try { [IO.File]::WriteAllText($temporaryEvidence, ($report | ConvertTo-Json -Depth 12)); [IO.File]::Move($temporaryEvidence, $fullEvidence, $true) }
finally { if ([IO.File]::Exists($temporaryEvidence)) { [IO.File]::Delete($temporaryEvidence) } }
if (-not $passed) { throw $errorMessage }
Write-Output "Source snapshot verified: $CandidateSha ($($files.Count) compiler/test/tooling inputs)."
