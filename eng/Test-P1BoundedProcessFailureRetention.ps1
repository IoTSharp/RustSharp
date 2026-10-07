[CmdletBinding()]
param(
    [string] $WrapperPath = (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1'),
    [Parameter(Mandatory)][string] $ResultPath,
    [ValidateSet('Tiny','All')][string] $Mode = 'All'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$wrapper = (Resolve-Path -LiteralPath $WrapperPath).Path
$output = [IO.Path]::GetFullPath($ResultPath)
if ([IO.File]::Exists($output)) { throw 'Refusing to overwrite control evidence.' }
$directory = $output + '.owned-' + [Guid]::NewGuid().ToString('N')
$null = [IO.Directory]::CreateDirectory($directory)
$pwsh = 'C:\Program Files\PowerShell\7\pwsh.exe'
$child = Join-Path $directory 'child.ps1'
$script = @'
param([string] $Receipt, [int] $Code, [int] $ParentPid)
$self = [Diagnostics.Process]::GetCurrentProcess()
try {
    $identity = @{ Pid=$PID; ParentPid=$ParentPid; StartedAt=$self.StartTime.ToUniversalTime().ToString('O'); FilePath=[Environment]::ProcessPath; CommandLine=[Environment]::CommandLine }
    [IO.File]::WriteAllText($Receipt,($identity | ConvertTo-Json))
} finally { $self.Dispose() }
exit $Code
'@
[IO.File]::WriteAllText($child,$script,[Text.UTF8Encoding]::new($false))
$codes = @(if ($Mode -ceq 'Tiny') { 0 } else { 0;23 })
$clock = [Diagnostics.Stopwatch]::StartNew()
$results = [Collections.Generic.List[object]]::new()
$retainedError = $null
try {
    # Reviewed comparisons: one/two cases AND 30 seconds; no retries or descendants.
    for ($index=0; $index -lt $codes.Count; $index++) {
        if ($clock.Elapsed.TotalSeconds -ge 30) { throw 'Failure controls exceeded 30 seconds.' }
        $code=$codes[$index]
        $prefix=Join-Path $directory ('case-' + $code)
        $receiptPath=$prefix + '.identity.json'
        $lock=$null
        $caught=$null
        $observedGone=$false
        $identity=$null
        $args=@('-NoLogo','-NoProfile','-NonInteractive','-File',$child,'-Receipt',$receiptPath,'-Code',[string]$code,'-ParentPid',[string]$PID)
        try {
            $lock=[IO.File]::Open($prefix + '.process.json',[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            try { $null=& $wrapper -FilePath $pwsh -ArgumentList $args -WorkingDirectory $directory -CapturePrefix $prefix -TimeoutSeconds 5 } catch { $caught=$_ }
            $identity=[IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
            try {
                $p=[Diagnostics.Process]::GetProcessById([int]$identity.Pid)
                try { $observedGone=$p.HasExited -or $p.StartTime.ToUniversalTime().Ticks -ne ([DateTime]::Parse($identity.StartedAt)).ToUniversalTime().Ticks } finally { $p.Dispose() }
            } catch [ArgumentException] { $observedGone=$true }
            $cleanup=@(if ($null -ne $caught) { $caught.Exception.Data['BoundedProcessCleanupFailures'] })
            $passed=$null -ne $caught -and $observedGone -and $identity.ParentPid -eq $PID -and $identity.FilePath -eq $pwsh
            if ($code -eq 0) { $passed=$passed -and $caught.Exception -is [AggregateException] -and $caught.Exception.InnerExceptions.Count -gt 0 }
            else { $passed=$passed -and $caught.Exception.Message -match 'code 23' -and $cleanup.Count -gt 0 -and $null -ne $cleanup[0] }
            $results.Add(@{Name=if($code -eq 0){'cleanup-failure-reported'}else{'primary-exit-preserved'};Passed=$passed;Error=if($caught){$caught.Exception.Message}else{$null};CleanupErrorCount=$cleanup.Count;Child=$identity;Arguments=$args;RootExitedObserved=$observedGone})
        } finally { if ($null -ne $lock) { $lock.Dispose() } }
    }
} catch { $retainedError=$_ }
$report=@{SchemaVersion=1;Mode=$Mode;ExpectedControls=$codes.Count;ObservedControls=$results.Count;PassedControls=@($results | Where-Object Passed).Count;WrapperPath=$wrapper;WrapperSha256=(Get-FileHash -LiteralPath $wrapper -Algorithm SHA256).Hash;Controls=@($results.ToArray());PrimaryFailure=if($retainedError){$retainedError.Exception.Message}else{$null};RetainedDirectory=$directory;DescendantCleanupProven=$false;PhaseClosed=$false}
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
if ($retainedError) { $PSCmdlet.ThrowTerminatingError($retainedError) }
if ($report.PassedControls -ne $report.ExpectedControls) { throw 'Failure retention controls did not pass.' }
Write-Host ('Failure retention controls passed: ' + $report.PassedControls + '/' + $report.ExpectedControls)
