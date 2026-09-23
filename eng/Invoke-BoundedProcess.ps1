[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FilePath,

    [Parameter()]
    [string[]] $ArgumentList = @(),

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 300,

    [Parameter()]
    [string] $WorkingDirectory = (Get-Location).Path,

    [Parameter()]
    [string] $CapturePrefix,

    [Parameter()]
    [ValidateRange(1024, 67108864)]
    [long] $MaximumOutputBytes = 16777216
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$terminationWaitMilliseconds = 5000

$resolvedWorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
$command = Get-Command -Name $FilePath -ErrorAction Stop
$process = $null
$captureStreams = @()
$captureTasks = @()
$metadata = $null
$failure = $null
$captureBase = $null

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $command.Source
    $startInfo.WorkingDirectory = $resolvedWorkingDirectory
    $startInfo.UseShellExecute = $false
    # Keep output attached to the invoking terminal. The production runner
    # owns bounded capture; this helper only provides a bounded smoke wait.
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    if ($CapturePrefix) {
        $captureBase = [IO.Path]::GetFullPath($CapturePrefix, $resolvedWorkingDirectory)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($captureBase))
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $captureStreams = @(
            [IO.File]::Open($captureBase + '.stdout.log', [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read),
            [IO.File]::Open($captureBase + '.stderr.log', [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        )
    }

    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedAt = [DateTimeOffset]::Now

    if (-not $process.Start()) {
        throw "Failed to start '$($command.Source)'."
    }

    $metadata = [ordered]@{
        Pid = $process.Id
        ParentPid = $PID
        StartedAt = $startedAt.ToString('O')
        FilePath = $command.Source
        Arguments = $ArgumentList
        WorkingDirectory = $resolvedWorkingDirectory
        TimeoutSeconds = $TimeoutSeconds
        MaximumAttempts = 1
        MaximumOutputBytes = $MaximumOutputBytes
        CapturePrefix = $captureBase
    }
    Write-Host ($metadata | ConvertTo-Json -Compress)
    if ($captureBase) {
        $captureTasks = @(
            $process.StandardOutput.BaseStream.CopyToAsync($captureStreams[0]),
            $process.StandardError.BaseStream.CopyToAsync($captureStreams[1])
        )
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # Both iteration count and elapsed time bound the wait. The smallest
    # supported trial is TimeoutSeconds=1; each wait yields for at most 1s.
    for ($attempt = 0; $attempt -lt $TimeoutSeconds; $attempt++) {
        if ($process.WaitForExit([Math]::Min(1000, [Math]::Max(1, ($TimeoutSeconds * 1000) - [int]$clock.ElapsedMilliseconds)))) { break }
        if ($captureBase -and (($captureStreams[0].Length + $captureStreams[1].Length) -gt $MaximumOutputBytes)) {
            throw "Process $($process.Id) exceeded the $MaximumOutputBytes byte output budget."
        }
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
    }
    if (-not $process.HasExited) {
        throw "Process $($process.Id) exceeded the $TimeoutSeconds second timeout."
    }
    if ($captureTasks.Count -gt 0 -and -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$captureTasks, $terminationWaitMilliseconds)) {
        throw 'Captured output pipes did not close within the termination grace period.'
    }
    if ($captureBase -and (($captureStreams[0].Length + $captureStreams[1].Length) -gt $MaximumOutputBytes)) {
        throw "Process $($process.Id) exceeded the $MaximumOutputBytes byte output budget."
    }
    if ($process.ExitCode -ne 0) {
        throw "Process $($process.Id) exited with code $($process.ExitCode)."
    }
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit($terminationWaitMilliseconds)
        }
        if ($null -ne $metadata) {
            $metadata['FinishedAt'] = [DateTimeOffset]::Now.ToString('O')
            $metadata['ExitCode'] = if ($process.HasExited) { $process.ExitCode } else { $null }
            $metadata['CleanupComplete'] = $process.HasExited
            $metadata['Failure'] = $failure
            if ($captureBase) {
                $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($captureBase + '.process.json') -Encoding utf8
            }
            Write-Host ($metadata | ConvertTo-Json -Compress)
        }
        $process.Dispose()
    }
    foreach ($stream in $captureStreams) { $stream.Dispose() }
}
