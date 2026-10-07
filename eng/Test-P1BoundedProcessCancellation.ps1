[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WrapperPath,
    [Parameter(Mandatory)] [string] $ResultPath,
    [Parameter()] [ValidateSet('Tiny', 'All')] [string] $Mode = 'All'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7+ is required.' }
$pwshPath = 'C:\Program Files\PowerShell\7\pwsh.exe'
if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { throw 'Configured PowerShell 7 path is missing.' }
$wrapper = (Resolve-Path -LiteralPath $WrapperPath).Path
$output = [IO.Path]::GetFullPath($ResultPath)
if (Test-Path -LiteralPath $output) { throw 'Refusing to overwrite prior control evidence.' }
$taskId = [Guid]::NewGuid().ToString('N')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sandbox = [IO.Path]::GetFullPath((Join-Path $tempRoot ('rustsharp-cancel-' + $taskId)))
if (Test-Path -LiteralPath $sandbox) { throw 'Exclusive sandbox unexpectedly exists.' }
$null = [IO.Directory]::CreateDirectory($sandbox)
$utf8 = [Text.UTF8Encoding]::new($false)
$ownerPath = Join-Path $sandbox 'owner.json'
$owner = [ordered]@{ TaskId = $taskId; Pid = $PID; Path = $sandbox; Script = $PSCommandPath }
[IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json), $utf8)
$childPath = Join-Path $sandbox 'child.ps1'
$child = @'
param([string] $Mode, [string] $Marker, [int] $ParentPid)
$ErrorActionPreference = 'Stop'
$self = [Diagnostics.Process]::GetCurrentProcess()
try {
    $receipt = [ordered]@{
        Pid = $PID; ParentPid = $ParentPid
        StartedAt = [DateTimeOffset]::new($self.StartTime.ToUniversalTime()).ToString('O')
        FilePath = [Environment]::ProcessPath; CommandLine = [Environment]::CommandLine
        Arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-Mode', $Mode, '-Marker', $Marker, '-ParentPid', [string]$ParentPid)
    }
    [IO.File]::WriteAllText($Marker, ($receipt | ConvertTo-Json -Depth 5))
} finally { $self.Dispose() }
switch ($Mode) {
    'normal' { [Console]::Out.WriteLine('normal stdout'); [Console]::Error.WriteLine('normal stderr'); exit 0 }
    'nonzero' { [Console]::Error.WriteLine('nonzero evidence'); exit 23 }
    'output' { [Console]::Out.Write(('x' * 65536)); [Console]::Out.Flush(); exit 0 }
    default {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        # Reviewed finite loop: 150 items AND 15s wall-clock; child never spawns another process.
        for ($tick = 0; $tick -lt 150 -and $clock.ElapsedMilliseconds -lt 15000; $tick++) { Start-Sleep -Milliseconds 100 }
        exit 0
    }
}
'@
[IO.File]::WriteAllText($childPath, $child, $utf8)
$cases = @(if ($Mode -eq 'Tiny') { 'normal' } else { 'pre-cancel', 'midrun', 'normal', 'nonzero', 'timeout', 'output' })
$results = [Collections.Generic.List[object]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$caseClock = [Diagnostics.Stopwatch]::StartNew()
$startedAt = [DateTimeOffset]::Now.ToString('O')
$primaryError = $null

try {
    # Maximum 6 cases plus the monotonic 120s suite deadline; no retries.
    for ($caseIndex = 0; $caseIndex -lt $cases.Count; $caseIndex++) {
        if ($caseClock.ElapsedMilliseconds -ge 120000) { throw 'Control suite exceeded its 120 second deadline.' }
        $caseName = $cases[$caseIndex]
        $marker = Join-Path $sandbox ($caseName + '.launched.json')
        $prefix = Join-Path $sandbox $caseName
        $childMode = switch ($caseName) { 'midrun' { 'wait' } 'timeout' { 'wait' } 'pre-cancel' { 'normal' } default { $caseName } }
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $childPath, '-Mode', $childMode, '-Marker', $marker, '-ParentPid', [string]$PID)
        $cancellation = [Threading.CancellationTokenSource]::new()
        $errorMessage = $null
        $ledger = $null
        $receipt = $null
        $rootGone = $false
        $passed = $false
        $measurement = [Diagnostics.Stopwatch]::StartNew()
        try {
            if ($caseName -eq 'pre-cancel') { $cancellation.Cancel() }
            if ($caseName -eq 'midrun') { $cancellation.CancelAfter(2500) }
            $timeout = if ($caseName -eq 'timeout') { 1 } else { 10 }
            $maximumOutput = if ($caseName -eq 'output') { 1024 } else { 1048576 }
            try {
                $null = & $wrapper -FilePath $pwshPath -ArgumentList $arguments -WorkingDirectory $sandbox -CapturePrefix $prefix -TimeoutSeconds $timeout -MaximumOutputBytes $maximumOutput -CancellationToken $cancellation.Token
            } catch { $errorMessage = $_.Exception.Message }
            if (Test-Path -LiteralPath ($prefix + '.process.json')) { $ledger = Get-Content -LiteralPath ($prefix + '.process.json') -Raw | ConvertFrom-Json }
            if (Test-Path -LiteralPath $marker) { $receipt = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json }
            if ($null -ne $ledger) {
                try {
                    $observed = [Diagnostics.Process]::GetProcessById([int]$ledger.Pid)
                    try { $rootGone = $observed.HasExited -or $observed.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$ledger.StartedAt).UtcTicks }
                    finally { $observed.Dispose() }
                } catch [ArgumentException] { $rootGone = $true }
            }
            $validLedger = $null -ne $ledger -and $ledger.ParentPid -eq $PID -and $ledger.FilePath -eq $pwshPath -and $ledger.RootProcessExited -and $ledger.CleanupVerification -eq 'parent-exit-only' -and $ledger.IdentityVerification -eq 'os-creation-time' -and $ledger.CleanupFailures.Count -eq 0 -and ($ledger.Arguments | ConvertTo-Json -Compress) -eq ($arguments | ConvertTo-Json -Compress) -and $rootGone
            if ($null -ne $receipt) {
                $validLedger = $validLedger -and $receipt.Pid -eq $ledger.Pid -and ([DateTimeOffset]$receipt.StartedAt).UtcTicks -eq ([DateTimeOffset]$ledger.StartedAt).UtcTicks -and $receipt.ParentPid -eq $PID -and ($receipt.Arguments | ConvertTo-Json -Compress) -eq ($arguments | ConvertTo-Json -Compress)
            }
            $passed = switch ($caseName) {
                'pre-cancel' { $errorMessage -match 'cancel' -and $null -eq $ledger -and $null -eq $receipt -and -not (Test-Path -LiteralPath ($prefix + '.stdout.log')) }
                'midrun' { $validLedger -and $null -ne $receipt -and $errorMessage -match 'cancel' -and $measurement.ElapsedMilliseconds -lt 12000 }
                'normal' { $validLedger -and $null -ne $receipt -and $null -eq $errorMessage -and $ledger.ExitCode -eq 0 -and (Get-Content -LiteralPath ($prefix + '.stdout.log') -Raw) -match 'normal stdout' -and (Get-Content -LiteralPath ($prefix + '.stderr.log') -Raw) -match 'normal stderr' }
                'nonzero' { $validLedger -and $null -ne $receipt -and $errorMessage -match 'code 23' -and $ledger.ExitCode -eq 23 -and $ledger.Failure -match 'code 23' -and (Get-Content -LiteralPath ($prefix + '.stderr.log') -Raw) -match 'nonzero evidence' }
                'timeout' { $validLedger -and $errorMessage -match 'timeout' -and $ledger.Failure -match 'timeout' }
                'output' { $validLedger -and $null -ne $receipt -and $errorMessage -match 'output budget' -and $ledger.Failure -match 'output budget' }
            }
        }
        finally {
            $cancellation.Cancel()
            $cancellation.Dispose()
            if ($null -ne $ledger -and $null -eq $receipt -and -not $rootGone) { $cleanupFailures.Add('Child remained alive without a receipt; ownership unverified, no fallback kill performed.') }
            # Tests launch no descendants. Fallback kills only an exact OS identity with matching full argv and OS parent.
            if ($null -ne $receipt -and -not $rootGone) {
                try {
                    $owned = [Diagnostics.Process]::GetProcessById([int]$receipt.Pid)
                    try {
                        $os = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + [int]$receipt.Pid) -OperationTimeoutSec 2
                        if ($owned.StartTime.ToUniversalTime().Ticks -eq ([DateTimeOffset]$receipt.StartedAt).UtcTicks -and $os.ParentProcessId -eq $PID -and $os.CommandLine -eq $receipt.CommandLine -and $owned.MainModule.FileName -eq $pwshPath) {
                            $owned.Kill($true)
                            if (-not $owned.WaitForExit(3000)) { throw 'Owned control child cleanup timed out.' }
                        } else { throw 'Control process ownership could not be verified; no termination performed.' }
                    } finally { $owned.Dispose() }
                } catch [ArgumentException] { }
                catch { $cleanupFailures.Add($_.Exception.Message) }
            }
        }
        $results.Add([ordered]@{ Name = $caseName; Passed = [bool]$passed; ElapsedMilliseconds = $measurement.ElapsedMilliseconds; Failure = $errorMessage; RootExitedObserved = $rootGone; Ledger = $ledger; ChildReceipt = $receipt })
    }
}
catch { $primaryError = $_ }
finally {
    # Only delete our exclusive flat sandbox after verifying its owner and every immediate child.
    try {
        if ($cleanupFailures.Count -gt 0) { throw 'Preserving sandbox because process cleanup has unresolved failures.' }
        $recordedOwner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
        $sandboxItem = Get-Item -LiteralPath $sandbox
        if ($recordedOwner.TaskId -ne $taskId -or $recordedOwner.Pid -ne $PID -or $recordedOwner.Path -ne $sandbox -or [IO.Path]::GetDirectoryName($sandbox) -ne $tempRoot.TrimEnd('\') -or ($sandboxItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Sandbox ownership validation failed.' }
        $children = @(Get-ChildItem -LiteralPath $sandbox -Force)
        if ($children.Count -gt 32) { throw 'Sandbox cleanup exceeded the 32 file bound.' }
        $cleanupClock = [Diagnostics.Stopwatch]::StartNew()
        for ($fileIndex = 0; $fileIndex -lt $children.Count; $fileIndex++) {
            if ($cleanupClock.ElapsedMilliseconds -ge 5000) { throw 'Sandbox cleanup deadline exceeded.' }
            if ($children[$fileIndex].PSIsContainer -or ($children[$fileIndex].Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($children[$fileIndex].FullName) -ne $sandbox) { throw 'Unexpected sandbox object; refusing deletion.' }
        }
        for ($fileIndex = 0; $fileIndex -lt $children.Count; $fileIndex++) {
            if ($cleanupClock.ElapsedMilliseconds -ge 5000) { throw 'Sandbox cleanup deadline exceeded.' }
            Remove-Item -LiteralPath $children[$fileIndex].FullName -Force
        }
        Remove-Item -LiteralPath $sandbox
    } catch { $cleanupFailures.Add($_.Exception.Message) }
    $passedCount = @($results | Where-Object { $_.Passed }).Count
    $report = [ordered]@{
        SchemaVersion = 1; StartedAt = $startedAt; FinishedAt = [DateTimeOffset]::Now.ToString('O')
        Mode = $Mode; ExpectedControls = $cases.Count; ObservedControls = $results.Count; PassedControls = $passedCount
        WrapperPath = $wrapper; WrapperSha256 = (Get-FileHash -LiteralPath $wrapper -Algorithm SHA256).Hash
        CleanupVerification = 'parent-exit-only'; DescendantCleanupProven = $false; PhaseClosed = $false
        Sandbox = $sandbox; SandboxRemoved = -not (Test-Path -LiteralPath $sandbox)
        CleanupFailures = @($cleanupFailures.ToArray()); Controls = @($results.ToArray())
        PrimaryFailure = if ($null -ne $primaryError) { $primaryError.Exception.Message } else { $null }
    }
    try {
        $reportBytes = $utf8.GetBytes(($report | ConvertTo-Json -Depth 10))
        $reportStream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $reportStream.Write($reportBytes, 0, $reportBytes.Length) } finally { $reportStream.Dispose() }
    } catch { $cleanupFailures.Add('Control evidence publication failed: ' + $_.Exception.Message) }
}
if ($null -ne $primaryError) { $PSCmdlet.ThrowTerminatingError($primaryError) }
if ($results.Count -ne $cases.Count -or $passedCount -ne $cases.Count -or $cleanupFailures.Count -gt 0) { throw "Bounded process controls failed: $passedCount/$($cases.Count)." }
Write-Host "Controls passed: $passedCount/$($cases.Count); parent exit only; sandbox removed."
