[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows-x64', 'linux-x64')]
    [string] $PlatformName,

    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,

    [Parameter()]
    [string] $OutputDirectory = '',

    [Parameter()]
    [string] $EvidencePath = '',

    [Parameter()]
    [ValidateRange(1, 300)]
    [int] $PublishTimeoutSeconds = 300,

    [Parameter()]
    [ValidateRange(1, 60)]
    [int] $RunTimeoutSeconds = 10,

    [Parameter()]
    [ValidateRange(1, 30)]
    [int] $TotalTimeoutMinutes = 30,

    [Parameter()]
    [string] $SdkVersion = '10.0.401'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Invoke-P1PlatformEvidence.ps1 requires PowerShell 7 or newer.'
}

$scriptRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $scriptRoot 'tools/RustSharp.Conformance/fixtures/p1-differential-v2-manifest.json'
$cliPath = Join-Path $scriptRoot 'src/RustSharp.Cli/bin/Release/net10.0/rsc.dll'
$ilVerifyScript = Join-Path $scriptRoot 'eng/Invoke-ILVerify.ps1'
$maximumCases = 12
$maximumOutputCharacters = 262144
$terminationGraceMilliseconds = 5000
$maximumCleanupAttempts = 40
$cleanupTimeout = [TimeSpan]::FromSeconds(5)
$startedAt = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$processRecords = [Collections.Generic.List[object]]::new()
$caseReports = [Collections.Generic.List[object]]::new()
$runDirectory = $null
$runDirectoryCreated = $false
$baseDirectoryCreated = $false
$cleanupDiagnostic = $null
$manifest = $null
$manifestSha256 = $null
$harnessError = $null
$deadlineExpired = $false
$preflight = [ordered]@{ dotnet = $null; rustc = $null; ilverify = $null }
$compilerSha256 = $null
$reportFullPath = $null
$baseDirectoryFullPath = $null
$status = 'blocked'
$runCases = @()

$fixedRunPassIds = @(
    'borrow-shared',
    'borrow-mutable-reborrow',
    'drop-return-order',
    'drop-early-return',
    'borrow-write-read',
    'borrow-shared-after-update',
    'borrow-mutable-reborrow-chain',
    'borrow-shared-reborrow',
    'drop-reverse-locals',
    'drop-nested-scopes',
    'drop-return-reverse',
    'drop-branch-return'
)

function Resolve-PathFromRoot {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A path argument cannot be empty.' }
    return [IO.Path]::GetFullPath($Path, $scriptRoot)
}

function Test-Within {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Directory)
    $relative = [IO.Path]::GetRelativePath($Directory, $Path)
    return $relative -eq '.' -or (-not [IO.Path]::IsPathRooted($relative) -and
        -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -and
        -not $relative.StartsWith('..' + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::Ordinal))
}

function Quote-Argument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Format-CommandLine {
    param([Parameter(Mandatory = $true)][string] $FileName, [Parameter()][string[]] $Arguments = @())
    $parts = [Collections.Generic.List[string]]::new()
    [void] $parts.Add((Quote-Argument $FileName))
    foreach ($argument in $Arguments) { [void] $parts.Add((Quote-Argument $argument)) }
    return $parts -join ' '
}

function Invoke-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [Parameter()][string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][ValidateRange(1, 300)][int] $TimeoutSeconds,
        [Parameter()][hashtable] $EnvironmentOverrides = @{}
    )
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($key in $EnvironmentOverrides.Keys) {
        $startInfo.Environment[$key] = [string]$EnvironmentOverrides[$key]
    }
    foreach ($argument in $Arguments) { [void] $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $commandLine = Format-CommandLine $FileName $Arguments
    $startedAtProcess = [DateTimeOffset]::UtcNow
    $termination = 'failed-to-start'
    $exitCode = $null
    $cleanupAttempted = $false
    $cleanupIncomplete = $false
    $cleanupDiagnosticProcess = $null
    $outputReadTimedOut = $false
    $stdout = ''
    $stderr = ''
    $processId = 0
    $elapsedMilliseconds = 0
    $processClock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw "Failed to start '$FileName'." }
        $processId = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $processExited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $processExited) {
            $termination = 'timed-out'
            $cleanupAttempted = $true
            try { $process.Kill($true) }
            catch { $cleanupIncomplete = $true; $cleanupDiagnosticProcess = $_.Exception.Message }
            if (-not $process.WaitForExit($terminationGraceMilliseconds)) {
                $cleanupIncomplete = $true
                $cleanupDiagnosticProcess = 'Process tree did not exit within termination grace period.'
            }
        }
        else {
            $termination = 'exited'
            $exitCode = $process.ExitCode
        }
        if (-not $stdoutTask.Wait($terminationGraceMilliseconds) -or -not $stderrTask.Wait($terminationGraceMilliseconds)) {
            $outputReadTimedOut = $true
        }
        $stdout = if ($stdoutTask.IsCompleted) { $stdoutTask.GetAwaiter().GetResult() } else { '' }
        $stderr = if ($stderrTask.IsCompleted) { $stderrTask.GetAwaiter().GetResult() } else { '' }
    }
    catch {
        $termination = 'failed-to-start'
        $stderr = $_.Exception.Message
        $cleanupDiagnosticProcess = $_.Exception.Message
    }
    finally {
        $processClock.Stop()
        $elapsedMilliseconds = $processClock.Elapsed.TotalMilliseconds
        if ($stdout.Length -gt $maximumOutputCharacters) { $stdout = $stdout.Substring(0, $maximumOutputCharacters) }
        if ($stderr.Length -gt $maximumOutputCharacters) { $stderr = $stderr.Substring(0, $maximumOutputCharacters) }
        if ($processId -and -not $process.HasExited) {
            $cleanupAttempted = $true
            try { $process.Kill($true); $null = $process.WaitForExit($terminationGraceMilliseconds) }
            catch { $cleanupIncomplete = $true; $cleanupDiagnosticProcess = $_.Exception.Message }
        }
        [void] $processRecords.Add([pscustomobject][ordered]@{
            ProcessId = $processId
            ParentProcessId = $PID
            StartedAtUtc = $startedAtProcess
            CommandLine = $commandLine
            WorkingDirectory = $WorkingDirectory
            TimeoutSeconds = $TimeoutSeconds
            ExitCode = $exitCode
            Termination = $termination
            ElapsedMilliseconds = $elapsedMilliseconds
            CleanupAttempted = $cleanupAttempted
            CleanupIncomplete = $cleanupIncomplete
            CleanupDiagnostic = $cleanupDiagnosticProcess
        })
        $process.Dispose()
    }
    [pscustomobject]@{
        CommandLine = $commandLine
        ProcessId = if ($processId) { $processId } else { 0 }
        ParentProcessId = $PID
        StartedAtUtc = $startedAtProcess
        ExitCode = $exitCode
        Termination = $termination
        StandardOutput = $stdout
        StandardError = $stderr
        OutputTruncated = $stdout.Length -ge $maximumOutputCharacters -or $stderr.Length -ge $maximumOutputCharacters
        OutputReadTimedOut = $outputReadTimedOut
        CleanupAttempted = $cleanupAttempted
        CleanupIncomplete = $cleanupIncomplete
        CleanupDiagnostic = $cleanupDiagnosticProcess
        ElapsedMilliseconds = $elapsedMilliseconds
        Succeeded = $termination -eq 'exited' -and $exitCode -eq 0 -and -not $cleanupIncomplete -and -not $outputReadTimedOut
    }
}

function Get-ToolSource {
    param([Parameter(Mandatory = $true)][string] $Name)
    $command = Get-Command -Name $Name -CommandType Application -ErrorAction Stop
    $path = [string]$command.Path
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = [string]$command.Source
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Resolved application '$Name' did not expose a usable path."
    }
    return $path
}

function Add-ProcessToCase {
    param([Parameter(Mandatory = $true)][object] $Case, [Parameter(Mandatory = $true)][string] $Name, [Parameter(Mandatory = $true)][object] $Result)
    $Case[$Name] = $Result
}

function Normalize-Output {
    param([AllowNull()][string] $Value)
    return ($Value ?? '').Replace("`r`n", "`n", [StringComparison]::Ordinal)
}

function Get-ShortDiagnostic {
    param([AllowNull()][string] $Value)
    if ([string]::IsNullOrEmpty($Value)) { return $null }
    if ($Value.Length -le 512) { return $Value }
    return $Value.Substring(0, 512) + '...'
}

function Get-ProcessOutputText {
    param([AllowNull()][object] $Result)
    if ($null -eq $Result) { return '' }
    return [string]$Result.StandardOutput
}

function Convert-ProcessSummary {
    param([AllowNull()][object] $Result)
    if ($null -eq $Result) { return $null }
    return [ordered]@{
        CommandLine = $Result.CommandLine
        ProcessId = $Result.ProcessId
        ParentProcessId = $Result.ParentProcessId
        StartedAtUtc = $Result.StartedAtUtc
        ExitCode = $Result.ExitCode
        Termination = $Result.Termination
        Succeeded = $Result.Succeeded
        StandardOutput = $Result.StandardOutput
        StandardError = $Result.StandardError
        OutputTruncated = $Result.OutputTruncated
        OutputReadTimedOut = $Result.OutputReadTimedOut
        CleanupAttempted = $Result.CleanupAttempted
        CleanupIncomplete = $Result.CleanupIncomplete
        CleanupDiagnostic = $Result.CleanupDiagnostic
    }
}

function Get-RemainingTimeoutSeconds {
    param([Parameter(Mandatory = $true)][int] $RequestedSeconds)
    $remaining = ($TotalTimeoutMinutes * 60) - $clock.Elapsed.TotalSeconds
    if ($remaining -le 0) {
        $script:deadlineExpired = $true
        return 1
    }
    return [Math]::Max(1, [Math]::Min($RequestedSeconds, [int][Math]::Floor($remaining)))
}

function Test-ExactSequence {
    param([Parameter(Mandatory = $true)][object[]] $Actual, [Parameter(Mandatory = $true)][object[]] $Expected)
    if ($Actual.Count -ne $Expected.Count) { return $false }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals([string]$Actual[$index], [string]$Expected[$index], [StringComparison]::Ordinal)) { return $false }
    }
    return $true
}

function Remove-TaskDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)
    if (-not [IO.Directory]::Exists($Path)) { return $null }
    $cleanupClock = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    for ($attempt = 0; $attempt -lt $maximumCleanupAttempts -and $cleanupClock.Elapsed -lt $cleanupTimeout; $attempt++) {
        try { [IO.Directory]::Delete($Path, $true) }
        catch { $last = $_.Exception.Message }
        if (-not [IO.Directory]::Exists($Path)) { return $null }
        Start-Sleep -Milliseconds 50
    }
    return "Cleanup failed after $maximumCleanupAttempts attempts/$($cleanupTimeout.TotalSeconds)s: $last"
}

try {
    if (($PlatformName -eq 'windows-x64') -ne [OperatingSystem]::IsWindows()) {
        throw "Platform '$PlatformName' does not match the current operating system."
    }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw 'P1 platform evidence requires an x64 runner.'
    }
    if ($OutputDirectory -eq '') { $OutputDirectory = "artifacts/p1-platform/$PlatformName" }
    if ($EvidencePath -eq '') { $EvidencePath = "artifacts/p1-platform/$PlatformName.json" }
    $baseDirectoryFullPath = Resolve-PathFromRoot $OutputDirectory
    $reportFullPath = Resolve-PathFromRoot $EvidencePath
    if (Test-Within -Path $reportFullPath -Directory $baseDirectoryFullPath) {
        throw 'EvidencePath must not be inside OutputDirectory.'
    }
    if ([IO.File]::Exists($reportFullPath)) { throw "EvidencePath already exists: '$reportFullPath'." }
    $baseExisted = [IO.Directory]::Exists($baseDirectoryFullPath)
    [IO.Directory]::CreateDirectory($baseDirectoryFullPath) | Out-Null
    $baseDirectoryCreated = -not $baseExisted
    $runDirectory = Join-Path $baseDirectoryFullPath ('.run-p1-platform-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $runDirectoryCreated = $true

    if (-not [IO.File]::Exists($manifestPath)) { throw "Fixed P1 v2 manifest not found: '$manifestPath'." }
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -gt 262144) { throw 'Fixed P1 v2 manifest exceeds its byte limit.' }
    $manifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes))
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    if ($manifest.profile -ne 'p1-differential-v2' -or $manifest.version -ne 2 -or $manifest.denominator -ne 16) {
        throw 'The fixed P1 v2 manifest contract is invalid.'
    }
    $runCases = @($manifest.cases | Where-Object {
        $expectation = if ($null -eq $_.PSObject.Properties['expectation']) { 'run-pass' } else { [string]$_.expectation }
        $expectation -eq 'run-pass'
    })
    if ($runCases.Count -ne $maximumCases -or -not (Test-ExactSequence @($runCases.id) $fixedRunPassIds)) {
        throw 'The fixed P1 v2 run-pass denominator or order is invalid.'
    }
    $dotnetPath = Get-ToolSource 'dotnet'
    $pwshPath = (Get-ToolSource 'pwsh')
    $rustcPath = Get-ToolSource 'rustc'
    if (-not [IO.File]::Exists($cliPath)) { throw "Release RustSharp CLI was not found: '$cliPath'." }
    $compilerSha256 = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash
    $dotnetVersionResult = Invoke-TrackedProcess $dotnetPath @('--version') $scriptRoot (Get-RemainingTimeoutSeconds 30)
    $rustcVersionResult = Invoke-TrackedProcess $rustcPath @('+1.98.0', '--version') $scriptRoot (Get-RemainingTimeoutSeconds 30)
    $ilverifyVersionResult = Invoke-TrackedProcess $dotnetPath @('tool', 'run', 'ilverify', '--', '--version') $scriptRoot (Get-RemainingTimeoutSeconds 30)
    $preflight.dotnet = $dotnetVersionResult
    $preflight.rustc = $rustcVersionResult
    $preflight.ilverify = $ilverifyVersionResult
    if (-not $dotnetVersionResult.Succeeded -or -not $rustcVersionResult.Succeeded -or
        -not $ilverifyVersionResult.Succeeded -or -not $rustcVersionResult.StandardOutput.StartsWith('rustc 1.98.0 (', [StringComparison]::Ordinal)) {
        throw 'Required tool preflight did not prove dotnet, ILVerify, and rustc 1.98.0 availability.'
    }
    foreach ($fixture in $runCases) {
        if ($clock.Elapsed.TotalMinutes -ge $TotalTimeoutMinutes) { $deadlineExpired = $true; break }
        $caseClock = [Diagnostics.Stopwatch]::StartNew()
        $caseDirectory = Join-Path $runDirectory ([string]$fixture.id)
        [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
        $sourcePath = Join-Path $scriptRoot ('tools/RustSharp.Conformance/fixtures/' + [string]$fixture.file)
        $sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
        $sourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes))
        $assemblyName = [IO.Path]::GetFileNameWithoutExtension($sourcePath)
        $managedPath = Join-Path $caseDirectory ($assemblyName + '.dll')
        $ilverifyEvidencePath = Join-Path $caseDirectory 'ilverify.json'
        $publishDirectory = Join-Path $caseDirectory 'native-aot'
        $case = [ordered]@{
            Id = [string]$fixture.id
            Source = [string]$fixture.file
            SourceSha256 = $sourceSha256
            ExpectedOutput = [string]$fixture.expectedOutput
            Status = 'failed'
            Difference = $null
        }
        try {
            $compile = Invoke-TrackedProcess $dotnetPath @($cliPath, 'compile', $sourcePath, '--output', $managedPath, '--profile', 'safe-core-mir-p1-v2') $scriptRoot (Get-RemainingTimeoutSeconds 300)
            Add-ProcessToCase $case 'CoreClrCompile' $compile
            if (-not $compile.Succeeded) { throw 'RustSharp CoreCLR compilation failed.' }
            $ilverify = Invoke-TrackedProcess $pwshPath @('-NoLogo', '-NoProfile', '-File', $ilVerifyScript, '-AssemblyPath', $managedPath, '-EvidencePath', $ilverifyEvidencePath, '-TimeoutSeconds', '120') $scriptRoot (Get-RemainingTimeoutSeconds 150)
            Add-ProcessToCase $case 'ILVerify' $ilverify
            if (-not $ilverify.Succeeded -or -not [IO.File]::Exists($ilverifyEvidencePath)) { throw 'ILVerify did not produce successful evidence.' }
            $ilverifyReport = [IO.File]::ReadAllText($ilverifyEvidencePath) | ConvertFrom-Json
            if ($ilverifyReport.Succeeded -ne $true) {
                $ilverifyFailure = if ($null -eq $ilverifyReport.Failure) { 'no failure detail' } else { [string]$ilverifyReport.Failure }
                throw "ILVerify did not report Succeeded=true: $ilverifyFailure"
            }
            $case['ILVerifyEvidence'] = [ordered]@{
                Status = 'passed'
                Succeeded = [bool]$ilverifyReport.Succeeded
                Sha256 = (Get-FileHash -LiteralPath $ilverifyEvidencePath -Algorithm SHA256).Hash
            }
            $coreRun = Invoke-TrackedProcess $dotnetPath @($managedPath) $caseDirectory (Get-RemainingTimeoutSeconds $RunTimeoutSeconds)
            Add-ProcessToCase $case 'CoreClrRun' $coreRun
            $coreOutput = Normalize-Output $coreRun.StandardOutput
            if (-not $coreRun.Succeeded -or $coreOutput -ne (Normalize-Output ([string]$fixture.expectedOutput))) { throw "CoreCLR output differed: '$coreOutput'." }
            [IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
            $publish = Invoke-TrackedProcess $dotnetPath @($cliPath, 'publish', $sourcePath, '--runtime', $RuntimeIdentifier, '--output', $publishDirectory, '--profile', 'safe-core-mir-p1-v2', '--timeout', $PublishTimeoutSeconds.ToString()) $scriptRoot (Get-RemainingTimeoutSeconds $PublishTimeoutSeconds) @{ RUSTSHARP_NATIVE_AOT_SDK_VERSION = $SdkVersion }
            Add-ProcessToCase $case 'NativeAotPublish' $publish
            $executableName = $assemblyName + '.NativeAotHost' + $(if ($RuntimeIdentifier -eq 'win-x64') { '.exe' } else { '' })
            $nativeExecutable = Join-Path $publishDirectory $executableName
            if (-not $publish.Succeeded -or -not [IO.File]::Exists($nativeExecutable)) { throw 'Native AOT publish did not produce the expected executable.' }
            $nativeRun = Invoke-TrackedProcess $nativeExecutable @() $publishDirectory (Get-RemainingTimeoutSeconds $RunTimeoutSeconds)
            Add-ProcessToCase $case 'NativeAotRun' $nativeRun
            $nativeOutput = Normalize-Output $nativeRun.StandardOutput
            if (-not $nativeRun.Succeeded -or $nativeOutput -ne (Normalize-Output ([string]$fixture.expectedOutput))) { throw "Native AOT output differed: '$nativeOutput'." }
            $case.Status = 'passed'
            $case.Difference = $null
        }
        catch {
            $case.Difference = Get-ShortDiagnostic $_.Exception.Message
        }
        $case.ElapsedMilliseconds = $caseClock.Elapsed.TotalMilliseconds
        [void] $caseReports.Add([pscustomobject]$case)
    }
}
catch {
    $harnessError = Get-ShortDiagnostic $_.Exception.Message
}
finally {
    for ($missingIndex = $caseReports.Count; $missingIndex -lt [Math]::Min($runCases.Count, $maximumCases); $missingIndex++) {
        $missing = $runCases[$missingIndex]
        $reason = if ($deadlineExpired) { 'P1 platform evidence total deadline expired before this case started.' } elseif ($harnessError) { $harnessError } else { 'P1 platform evidence stopped before this case started.' }
        [void] $caseReports.Add([pscustomobject][ordered]@{
            Id = [string]$missing.id
            Source = [string]$missing.file
            SourceSha256 = $null
            ExpectedOutput = [string]$missing.expectedOutput
            Status = 'blocked'
            Difference = $reason
        })
    }
    if ($runDirectoryCreated) { $cleanupDiagnostic = Remove-TaskDirectory $runDirectory }
    if ($baseDirectoryCreated -and [IO.Directory]::Exists($baseDirectoryFullPath) -and @([IO.Directory]::EnumerateFileSystemEntries($baseDirectoryFullPath)).Count -eq 0) {
        [IO.Directory]::Delete($baseDirectoryFullPath)
    }
    $clock.Stop()
    $passed = @($caseReports | Where-Object Status -eq 'passed').Count
    $failed = @($caseReports | Where-Object Status -eq 'failed').Count
    $blocked = @($caseReports | Where-Object Status -eq 'blocked').Count
    $skipped = @($caseReports | Where-Object Status -eq 'skipped').Count
    if ($harnessError -or $deadlineExpired -or $cleanupDiagnostic -or $blocked -gt 0) { $status = 'blocked' }
    elseif ($failed -gt 0 -or $caseReports.Count -ne $maximumCases) { $status = 'failed' }
    else { $status = 'passed' }
    if ($null -ne $reportFullPath) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFullPath)) | Out-Null
        $report = [ordered]@{
            SchemaVersion = 1
            EvidenceKind = 'p1-platform-coreclr-ilverify-native-aot'
            Profile = 'p1-differential-v2'
            Platform = [ordered]@{
                Name = $PlatformName
                RuntimeIdentifier = $RuntimeIdentifier
                OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
                Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
                ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
                RuntimeIdentifierObserved = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
            }
            Manifest = [ordered]@{ Path = [IO.Path]::GetRelativePath($scriptRoot, $manifestPath).Replace('\', '/'); Sha256 = $manifestSha256; Denominator = 12; Validated = $null -ne $manifest }
            Compiler = [ordered]@{ CliPath = $cliPath; Sha256 = $compilerSha256; Profile = 'safe-core-mir-p1-v2' }
            ToolVersions = [ordered]@{ Dotnet = (Get-ProcessOutputText $preflight.dotnet).Trim(); Rustc = (Get-ProcessOutputText $preflight.rustc).Trim(); ILVerify = (Get-ProcessOutputText $preflight.ilverify).Trim(); SdkVersion = $SdkVersion }
            Preflight = [ordered]@{ Dotnet = Convert-ProcessSummary $preflight.dotnet; Rustc = Convert-ProcessSummary $preflight.rustc; ILVerify = Convert-ProcessSummary $preflight.ilverify }
            Limits = [ordered]@{ CaseCount = 12; PublishTimeoutSeconds = $PublishTimeoutSeconds; RunTimeoutSeconds = $RunTimeoutSeconds; TotalTimeoutMinutes = $TotalTimeoutMinutes; MaximumOutputCharacters = $maximumOutputCharacters; MaximumCleanupAttempts = $maximumCleanupAttempts; CleanupTimeoutSeconds = $cleanupTimeout.TotalSeconds }
            Summary = [ordered]@{ Status = $status; ExitCode = if ($status -eq 'passed') { 0 } elseif ($status -eq 'failed') { 1 } else { 2 }; Denominator = 12; Executed = $caseReports.Count; Passed = $passed; Failed = $failed; Blocked = $blocked; Skipped = $skipped }
            Cases = @($caseReports.ToArray())
            ProcessEvidence = @($processRecords.ToArray())
            Execution = [ordered]@{ StartedAtUtc = $startedAt; FinishedAtUtc = [DateTimeOffset]::UtcNow; ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds; DeadlineExpired = $deadlineExpired }
            Cleanup = [ordered]@{ Completed = $null -eq $cleanupDiagnostic; Diagnostic = $cleanupDiagnostic }
            HarnessError = $harnessError
        }
        $temporaryReport = $reportFullPath + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
        try {
            [IO.File]::WriteAllText($temporaryReport, ($report | ConvertTo-Json -Depth 12))
            [IO.File]::Move($temporaryReport, $reportFullPath)
        }
        finally { if ([IO.File]::Exists($temporaryReport)) { [IO.File]::Delete($temporaryReport) } }
    }
}

if ($null -ne $reportFullPath -and [IO.File]::Exists($reportFullPath)) {
    Write-Output ([IO.File]::ReadAllText($reportFullPath))
}
if ($status -eq 'passed') { exit 0 }
if ($status -eq 'failed') { exit 1 }
exit 2
