[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $FilePath,
    [Parameter()] [string[]] $ArgumentList = @(),
    [Parameter()] [ValidateRange(1, 3600)] [int] $TimeoutSeconds = 300,
    [Parameter()] [string] $WorkingDirectory = (Get-Location).Path,
    [Parameter()] [string] $CapturePrefix,
    [Parameter()] [ValidateRange(1024, 67108864)] [long] $MaximumOutputBytes = 16777216,
    [Parameter()] [System.Threading.CancellationToken] $CancellationToken = [System.Threading.CancellationToken]::None
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$terminationWaitMilliseconds = 5000
$pollMilliseconds = 200
$maximumWaitChecks = [int][Math]::Ceiling(($TimeoutSeconds * 1000.0) / $pollMilliseconds) + 1
$maximumDrainChecks = [int][Math]::Ceiling($terminationWaitMilliseconds / $pollMilliseconds) + 1
$process = $null
$captureStreams = [Collections.Generic.List[IO.Stream]]::new()
$captureTasks = @()
$captureCancellation = $null
$metadata = $null
$primaryError = $null
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
$captureBase = $null
$processStarted = $false
$rootExited = $false
$exitCode = $null

try {
    # Pre-cancellation cannot create capture files or launch a child.
    $CancellationToken.ThrowIfCancellationRequested()
    $resolvedWorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
    $command = Get-Command -Name $FilePath -ErrorAction Stop | Select-Object -First 1
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $command.Source
    $startInfo.WorkingDirectory = $resolvedWorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    if ($CapturePrefix) {
        $captureBase = [IO.Path]::GetFullPath($CapturePrefix, $resolvedWorkingDirectory)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($captureBase))
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        # Track each successful open independently so a second-open failure cannot leak the first.
        $captureStreams.Add([IO.File]::Open($captureBase + '.stdout.log', [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read))
        $captureStreams.Add([IO.File]::Open($captureBase + '.stderr.log', [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read))
        $captureCancellation = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken, [Threading.CancellationToken]::None)
    }
    $preparationClock = [Diagnostics.Stopwatch]::StartNew()
    # The supplied array fixes the maximum item count; preparation also has a wall-clock bound.
    for ($argumentIndex = 0; $argumentIndex -lt $ArgumentList.Count; $argumentIndex++) {
        $CancellationToken.ThrowIfCancellationRequested()
        if ($preparationClock.ElapsedMilliseconds -ge ($TimeoutSeconds * 1000)) { throw 'Process argument preparation exceeded the operation timeout.' }
        $startInfo.ArgumentList.Add($ArgumentList[$argumentIndex])
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $launchRequestedAt = [DateTimeOffset]::Now
    $CancellationToken.ThrowIfCancellationRequested()
    if (-not $process.Start()) { throw "Failed to start '$($command.Source)'." }
    $processStarted = $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # Retain PID and complete argv before querying optional OS identity so very short children retain a ledger.
    $metadata = [ordered]@{
        Pid = $process.Id
        ParentPid = $PID
        StartedAt = $launchRequestedAt.ToString('O')
        OSCreationTime = $null
        IdentityVerification = 'unverified'
        IdentityFailure = $null
        LaunchRequestedAt = $launchRequestedAt.ToString('O')
        FilePath = $command.Source
        Arguments = @($ArgumentList)
        ParentStartedAt = $null
        ParentCommandLine = [Environment]::CommandLine
        WorkingDirectory = $resolvedWorkingDirectory
        TimeoutSeconds = $TimeoutSeconds
        MaximumAttempts = 1
        MaximumOutputBytes = $MaximumOutputBytes
        CapturePrefix = $captureBase
        PollMilliseconds = $pollMilliseconds
        MaximumWaitChecks = $maximumWaitChecks
        CleanupVerification = 'parent-exit-only'
    }
    try {
        $actualStartedAt = [DateTimeOffset]::new($process.StartTime.ToUniversalTime()).ToString('O')
        $metadata['StartedAt'] = $actualStartedAt
        $metadata['OSCreationTime'] = $actualStartedAt
        $metadata['IdentityVerification'] = 'os-creation-time'
    } catch { $metadata['IdentityFailure'] = $_.Exception.Message }
    $parentProcess = $null
    try {
        $parentProcess = [Diagnostics.Process]::GetCurrentProcess()
        $metadata['ParentStartedAt'] = [DateTimeOffset]::new($parentProcess.StartTime.ToUniversalTime()).ToString('O')
    } catch { $metadata['ParentIdentityFailure'] = $_.Exception.Message }
    finally {
        if ($null -ne $parentProcess) {
            try { $parentProcess.Dispose() } catch { $cleanupErrors.Add($_.Exception) }
        }
    }
    Write-Host ($metadata | ConvertTo-Json -Depth 5 -Compress)
    if ($captureBase) {
        $captureTasks = @(
            $process.StandardOutput.BaseStream.CopyToAsync($captureStreams[0], 81920, $captureCancellation.Token),
            $process.StandardError.BaseStream.CopyToAsync($captureStreams[1], 81920, $captureCancellation.Token)
        )
    }
    # Reviewed comparison exit conditions: at most timeout/200ms + 1 checks, plus monotonic deadline.
    for ($check = 0; $check -lt $maximumWaitChecks; $check++) {
        $CancellationToken.ThrowIfCancellationRequested()
        if ($captureBase -and (($captureStreams[0].Length + $captureStreams[1].Length) -gt $MaximumOutputBytes)) {
            throw "Process $($process.Id) exceeded the $MaximumOutputBytes byte output budget."
        }
        $remaining = ($TimeoutSeconds * 1000) - [int]$clock.ElapsedMilliseconds
        if ($remaining -le 0) { break }
        $exited = $process.WaitForExit([Math]::Min($pollMilliseconds, $remaining))
        $CancellationToken.ThrowIfCancellationRequested()
        if ($exited) { break }
    }
    if (-not $process.HasExited) { throw "Process $($process.Id) exceeded the $TimeoutSeconds second timeout." }
    if ($captureTasks.Count -gt 0) {
        $drainClock = [Diagnostics.Stopwatch]::StartNew()
        $allCapture = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$captureTasks)
        for ($check = 0; $check -lt $maximumDrainChecks; $check++) {
            $CancellationToken.ThrowIfCancellationRequested()
            if ($captureBase -and (($captureStreams[0].Length + $captureStreams[1].Length) -gt $MaximumOutputBytes)) {
                throw "Process $($process.Id) exceeded the $MaximumOutputBytes byte output budget."
            }
            if ($allCapture.IsCompleted) { break }
            $remaining = $terminationWaitMilliseconds - [int]$drainClock.ElapsedMilliseconds
            if ($remaining -le 0) { break }
            $null = $allCapture.Wait([Math]::Min($pollMilliseconds, $remaining))
        }
        $CancellationToken.ThrowIfCancellationRequested()
        if (-not $allCapture.IsCompleted) { throw 'Captured output pipes did not close within the termination grace period.' }
        $allCapture.GetAwaiter().GetResult()
    }
    if ($captureBase -and (($captureStreams[0].Length + $captureStreams[1].Length) -gt $MaximumOutputBytes)) {
        throw "Process $($process.Id) exceeded the $MaximumOutputBytes byte output budget."
    }
    if ($process.ExitCode -ne 0) { throw "Process $($process.Id) exited with code $($process.ExitCode)." }
}
catch { $primaryError = $_ }
finally {
    # Cleanup ignores caller cancellation, preserves every failure, and never rethrows over the primary error.
    if ($processStarted) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit($terminationWaitMilliseconds)) { throw 'Root process did not exit within the cleanup grace period.' }
            }
        }
        catch { $cleanupErrors.Add($_.Exception) }
        try {
            $rootExited = $process.HasExited
            if ($rootExited) { $exitCode = $process.ExitCode }
        } catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($null -ne $captureCancellation) {
        try { $captureCancellation.Cancel() } catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($captureTasks.Count -gt 0) {
        try {
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$captureTasks, $terminationWaitMilliseconds)) {
                throw 'Capture cleanup did not finish within the cleanup grace period.'
            }
        }
        catch {
            # CopyToAsync cancellation is expected during cleanup; other faults remain evidence.
            $caught = $_.Exception
            if ($caught.InnerException -is [AggregateException]) { $caught = $caught.InnerException }
            $exceptions = @(if ($caught -is [AggregateException]) { $caught.Flatten().InnerExceptions } else { $caught })
            for ($errorIndex = 0; $errorIndex -lt $exceptions.Count; $errorIndex++) {
                if ($exceptions[$errorIndex] -isnot [OperationCanceledException]) { $cleanupErrors.Add($exceptions[$errorIndex]) }
            }
        }
    }
    for ($streamIndex = 0; $streamIndex -lt $captureStreams.Count; $streamIndex++) {
        try { $captureStreams[$streamIndex].Dispose() } catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($null -ne $captureCancellation) {
        try { $captureCancellation.Dispose() } catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($null -ne $process) {
        try { $process.Dispose() } catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($null -ne $metadata) {
        try {
            $metadata['FinishedAt'] = [DateTimeOffset]::Now.ToString('O')
            $metadata['ExitCode'] = $exitCode
            $metadata['RootProcessExited'] = $rootExited
            # Compatibility field: this has always described the root only, never all descendants.
            $metadata['CleanupComplete'] = $rootExited
            $metadata['Failure'] = if ($null -ne $primaryError) { $primaryError.Exception.Message } else { $null }
            $metadata['CleanupFailures'] = @($cleanupErrors.ToArray() | ForEach-Object { $_.Message })
            if ($captureBase) {
                try { $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($captureBase + '.process.json') -Encoding utf8 }
                catch { $cleanupErrors.Add($_.Exception) }
            }
            $metadata['CleanupFailures'] = @($cleanupErrors.ToArray() | ForEach-Object { $_.Message })
            Write-Host ($metadata | ConvertTo-Json -Depth 5 -Compress)
        }
        catch { $cleanupErrors.Add($_.Exception) }
    }
}

if ($null -ne $primaryError) {
    if ($cleanupErrors.Count -gt 0) { $primaryError.Exception.Data['BoundedProcessCleanupFailures'] = $cleanupErrors.ToArray() }
    $PSCmdlet.ThrowTerminatingError($primaryError)
}
if ($cleanupErrors.Count -gt 0) { throw [AggregateException]::new('Bounded process cleanup failed.', $cleanupErrors.ToArray()) }
