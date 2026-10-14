[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FilePath,
    [string[]] $ArgumentList = @(),
    [Parameter(Mandatory)] [string] $WorkingDirectory,
    [string] $CapturePrefix,
    [ValidateRange(1,3600)] [int] $TimeoutSeconds = 300,
    [ValidateRange(1024,67108864)] [long] $MaximumOutputBytes = 16777216,
    [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None,
    [Parameter(Mandatory)] [string] $ContainmentAssemblyPath,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string] $ContainmentAssemblySha256,
    [Parameter(Mandatory)] [string] $CallerLeaseRoot,
    [Parameter(Mandatory)] [ValidatePattern('^[a-fA-F0-9]{32}$')] [string] $CallerLease,
    [Parameter(Mandatory)] [ValidateRange(1,20)] [int] $MaximumActiveProcesses,
    [Parameter(Mandatory)] [ValidateRange(1,1024)] [int] $MaximumTotalProcesses,
    [ValidateRange(1,1000)] [int] $ContainmentPollingMilliseconds = 10
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$CancellationToken.ThrowIfCancellationRequested()
if (-not $IsWindows) { throw 'FAIL_CLOSED: Linux atomic escape-resistant containment capability is UNKNOWN; no target launched.' }
$preparationClock=[Diagnostics.Stopwatch]::StartNew()
foreach ($path in @($FilePath,$WorkingDirectory,$ContainmentAssemblyPath,$CallerLeaseRoot)) {
    $CancellationToken.ThrowIfCancellationRequested(); if($preparationClock.Elapsed.TotalSeconds -ge 5){throw 'Four-path preparation wall bound.'}
    if (-not [IO.Path]::IsPathFullyQualified($path) -or [IO.Path]::GetFullPath($path) -cne $path) { throw 'Exact absolute executable, working directory, assembly and lease paths are required.' }
}
if ($MaximumTotalProcesses -lt $MaximumActiveProcesses -or $ArgumentList.Count -gt 24) { throw 'Original process/argument bounds are invalid.' }
if (-not [IO.File]::Exists($FilePath) -or -not [IO.File]::Exists($ContainmentAssemblyPath) -or -not [IO.Directory]::Exists($WorkingDirectory) -or -not [IO.Directory]::Exists($CallerLeaseRoot)) { throw 'Required executable/assembly/directory is missing.' }
$actualHash = (Get-FileHash -LiteralPath $ContainmentAssemblyPath -Algorithm SHA256).Hash
if ($actualHash -cne $ContainmentAssemblySha256.ToUpperInvariant()) { throw 'Containment assembly hash mismatch; no target launched.' }
$assembly = [Reflection.Assembly]::LoadFrom($ContainmentAssemblyPath)
if ([IO.Path]::GetFullPath($assembly.Location) -cne $ContainmentAssemblyPath -or (Get-FileHash -LiteralPath $assembly.Location -Algorithm SHA256).Hash -cne $actualHash) { throw 'Previously loaded containment assembly provenance conflicts; use a fresh PowerShell process.' }
$null = [RustSharp.ProcessContainment.ProcessAudit]::VerifyRoot($CallerLeaseRoot,$CancellationToken)
$captureFiles=@()
if($CapturePrefix){
 if(-not [IO.Path]::IsPathFullyQualified($CapturePrefix) -or [IO.Path]::GetFullPath($CapturePrefix) -cne $CapturePrefix -or [IO.Path]::GetDirectoryName($CapturePrefix) -cne $CallerLeaseRoot){throw 'CapturePrefix must be an exact absolute prefix directly inside the verified caller lease root.'}
 $captureFiles=@(($CapturePrefix+'.stdout.log'),($CapturePrefix+'.stderr.log'),($CapturePrefix+'.process.json'))
 foreach($path in $captureFiles){$CancellationToken.ThrowIfCancellationRequested();if($preparationClock.Elapsed.TotalSeconds -ge 5){throw 'Three-capture preparation wall bound.'};if(Test-Path -LiteralPath $path){throw 'CapturePrefix CreateNew paths must be fresh.'}}
}
$reportPath = Join-Path $CallerLeaseRoot 'actualreport.json'
$metadataPath = Join-Path $CallerLeaseRoot 'process.json'
foreach ($path in @($reportPath,$metadataPath)) { $CancellationToken.ThrowIfCancellationRequested();if($preparationClock.Elapsed.TotalSeconds -ge 5){throw 'Two-report preparation wall bound.'};if (Test-Path -LiteralPath $path) { throw 'Fresh exclusive report paths are required.' } }
$started = [DateTimeOffset]::UtcNow
$report = $null
$invocationFailure = $null
try {
    $report = [RustSharp.ProcessContainment.FullTreeRunner]::Run($FilePath,[string[]]$ArgumentList,$WorkingDirectory,$CallerLeaseRoot,$CallerLease,$MaximumActiveProcesses,$MaximumTotalProcesses,$TimeoutSeconds,$MaximumOutputBytes,$CancellationToken,$ContainmentPollingMilliseconds)
} catch { $invocationFailure = $_ }
$finished = [DateTimeOffset]::UtcNow
if ($null -ne $report) { $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $reportPath -Encoding utf8 }
$rawBytes = 0L
$captureClock=[Diagnostics.Stopwatch]::StartNew()
foreach ($name in @('stdout.bin','stderr.bin')) { if($captureClock.Elapsed.TotalSeconds -ge 5){throw 'Two-file final byte audit wall bound.'};$path = Join-Path $CallerLeaseRoot $name; if (Test-Path -LiteralPath $path) { $rawBytes += (Get-Item -LiteralPath $path).Length } }
$errors = @()
if ($null -ne $report) { $errors = @($report.Errors) }
$outcome = 'runner-failure'
if ($errors -match 'OperationCanceledException') { $outcome = 'cancelled' }
elseif ($errors -match 'TimeoutException') { $outcome = 'timeout' }
elseif ($null -ne $report -and $report.AggregateOutputBudgetExceeded) { $outcome = 'output-limit' }
elseif ($null -ne $report -and $report.ExecutionSucceeded) { $outcome = 'success' }
elseif ($null -ne $report -and $report.NaturalRootExitObserved -and $report.ForcedJobReap) { $outcome = 'forced-descendant-reap' }
elseif ($null -ne $report -and $null -ne $report.RootExitCode -and $report.RootExitCode -ne 0) { $outcome = 'nonzero-exit' }
$metadata = [ordered]@{
    StartedAt=$started.ToString('O'); FinishedAt=$finished.ToString('O'); FilePath=$FilePath; Arguments=@($ArgumentList); WorkingDirectory=$WorkingDirectory
    ParentPid=$PID; ParentCommandLine=[Environment]::CommandLine; CallerLease=$CallerLease; CallerLeaseRoot=$CallerLeaseRoot
    AssemblyPath=$ContainmentAssemblyPath; AssemblySha256=$actualHash; TimeoutSeconds=$TimeoutSeconds; MaximumOutputBytes=$MaximumOutputBytes
    MaximumActiveProcesses=$MaximumActiveProcesses; MaximumTotalProcesses=$MaximumTotalProcesses; PollingMilliseconds=$ContainmentPollingMilliseconds
    CleanupVerification='full-windows-owned-job'; DescendantContainmentVerified=($null -ne $report -and $report.FullContainmentVerified)
    CleanupComplete=($null -ne $report -and $report.FullContainmentVerified); RootProcessExited=($null -ne $report -and $null -ne $report.Root -and $report.Root.WaitReturn -eq 0)
    ExitCode=$(if ($null -ne $report) {$report.RootExitCode} else {$null}); PrimaryOutcome=$outcome
    NaturalExecutionSucceeded=($null -ne $report -and $report.ExecutionSucceeded -and $rawBytes -le $MaximumOutputBytes)
    RawOutputBytes=$rawBytes; OutputBudgetMode='native-running-aggregate-plus-per-stream'; AggregateOutputBudgetExceeded=($rawBytes -gt $MaximumOutputBytes)
    AggregateOutputBudgetObservedWhileRunning=($null -ne $report -and $report.AggregateOutputBudgetObservedWhileRunning); CapturePrefix=$CapturePrefix
    ActualReportPath=$(if ($null -ne $report) {$reportPath} else {$null}); StdoutPath=(Join-Path $CallerLeaseRoot 'stdout.bin'); StderrPath=(Join-Path $CallerLeaseRoot 'stderr.bin')
    PrimaryErrors=$errors; InvocationFailure=$(if ($null -ne $invocationFailure) {$invocationFailure.Exception.ToString()} else {$null})
    ProductionOptIn=$true; LinuxCapability='UNKNOWN'; OriginalP1ScopePassed=$false; FullP1Closure=$false
}
$metadata | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $metadataPath -Encoding utf8
$captureFailure=$null
if($CapturePrefix){
 try{
  $copyClock=[Diagnostics.Stopwatch]::StartNew()
  for($index=0;$index -lt 3;$index++){
   if($copyClock.Elapsed.TotalSeconds -ge 5){throw 'Three-file capture publication wall bound.'}
   $sourcePath=@($metadata.StdoutPath,$metadata.StderrPath,$metadataPath)[$index]
   $inputStream=$null;$outputStream=$null
   try{$inputStream=[IO.File]::OpenRead($sourcePath);$outputStream=[IO.FileStream]::new($captureFiles[$index],[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);$inputStream.CopyTo($outputStream)}finally{if($null -ne $outputStream){$outputStream.Dispose()};if($null -ne $inputStream){$inputStream.Dispose()}}
  }
 }catch{$captureFailure=$_}
}
Write-Output ([pscustomobject]$metadata)
if ($null -ne $invocationFailure) { if($null -ne $captureFailure){$invocationFailure.Exception.Data['CapturePublicationFailure']=$captureFailure.Exception.ToString()};$PSCmdlet.ThrowTerminatingError($invocationFailure) }
if (-not $metadata.NaturalExecutionSucceeded) { $failure=[InvalidOperationException]::new("Contained invocation did not complete naturally: $outcome. Actual report: $reportPath");if($null -ne $captureFailure){$failure.Data['CapturePublicationFailure']=$captureFailure.Exception.ToString()};throw $failure }
if($null -ne $captureFailure){$PSCmdlet.ThrowTerminatingError($captureFailure)}
