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
    [string] $WorkingDirectory = (Get-Location).Path
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$terminationWaitMilliseconds = 5000

$resolvedWorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
$command = Get-Command -Name $FilePath -ErrorAction Stop
$process = $null

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $command.Source
    $startInfo.WorkingDirectory = $resolvedWorkingDirectory
    $startInfo.UseShellExecute = $false
    # Keep output attached to the invoking terminal. The production runner
    # owns bounded capture; this helper only provides a bounded smoke wait.
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false

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
    }
    Write-Host ($metadata | ConvertTo-Json -Compress)

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        if (-not $process.WaitForExit($terminationWaitMilliseconds)) {
            throw "Process $($process.Id) did not exit within the $terminationWaitMilliseconds millisecond termination grace period."
        }

        throw "Process $($process.Id) exceeded the $TimeoutSeconds second timeout."
    }

    if ($process.ExitCode -ne 0) {
        throw "Process $($process.Id) exited with code $($process.ExitCode)."
    }
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit($terminationWaitMilliseconds)
        }

        $process.Dispose()
    }
}
