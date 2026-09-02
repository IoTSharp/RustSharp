[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $AssemblyPath,

    [Parameter()]
    [string] $ReferenceDirectory,

    [Parameter()]
    [string[]] $ReferencePath = @(),

    [Parameter()]
    [string] $SystemModule = 'System.Private.CoreLib',

    [Parameter()]
    [string] $SystemModulePath,

    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $RuntimeVersion = '10.0.11',

    [Parameter()]
    [string] $EvidencePath,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 120,

    [Parameter()]
    [switch] $Restore
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Invoke-ILVerify.ps1 requires PowerShell 7 or newer.'
}

$toolPackageId = 'dotnet-ilverify'
$toolVersion = '10.0.11'
$maximumAssemblyBytes = 256MB
$maximumReferenceAssemblies = 512
$maximumOutputCharacters = 262144
$pollMilliseconds = 250
$terminationGraceMilliseconds = 5000
$script:CancellationRequested = [pscustomobject] @{ Value = $false }
$processRecords = [Collections.Generic.List[object]]::new()
$restoreResult = $null
$verifyResult = $null
$failureMessage = $null
$assemblyFullPath = $null
$assemblyHash = $null
$evidenceFullPath = $null
$repositoryRoot = $null
$dotnetPath = $null
$dotnetRoot = $null
$referenceDirectoryFullPath = $null
$referenceFiles = @()
$systemModulePathFull = $null
$manifestFullPath = $null
$startedAt = [DateTimeOffset]::UtcNow
$succeeded = $false

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ParameterName
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$ParameterName must not be empty."
    }

    try {
        return [IO.Path]::GetFullPath($Path)
    }
    catch [Exception] {
        throw "$ParameterName is not a valid path: $($_.Exception.Message)"
    }
}

function Quote-Argument {
    param([Parameter(Mandatory = $true)][string] $Value)

    if ($Value -notmatch '[\s\"]') {
        return $Value
    }

    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Format-CommandLine {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $parts = [Collections.Generic.List[string]]::new()
    [void] $parts.Add((Quote-Argument $FileName))
    foreach ($argument in $Arguments) {
        [void] $parts.Add((Quote-Argument $argument))
    }

    return $parts -join ' '
}

function Append-BoundedChars {
    param(
        [Parameter(Mandatory = $true)][pscustomobject] $State,
        [Parameter(Mandatory = $true)][char[]] $Buffer,
        [Parameter(Mandatory = $true)][int] $Count
    )

    if ($Count -le 0) {
        return
    }

    $remaining = $maximumOutputCharacters - $State.Builder.Length
    if ($remaining -le 0) {
        $State.Truncated = $true
        return
    }

    $take = [Math]::Min($remaining, $Count)
    [void] $State.Builder.Append($Buffer, 0, $take)
    if ($take -lt $Count) {
        $State.Truncated = $true
    }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][int] $TimeoutMilliseconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $commandLine = Format-CommandLine $FileName $Arguments
    $stdoutState = [pscustomobject] @{
        Builder = [Text.StringBuilder]::new()
        Truncated = $false
        ReadError = $null
    }
    $stderrState = [pscustomobject] @{
        Builder = [Text.StringBuilder]::new()
        Truncated = $false
        ReadError = $null
    }
    $started = $false
    $termination = 'FailedToStart'
    $exitCode = $null
    $cleanupIncomplete = $false
    $drainTimedOut = $false
    $stopwatch = [Diagnostics.Stopwatch]::new()
    $startedAtProcess = [DateTimeOffset]::UtcNow
    $processId = $null

    try {
        if (-not $process.Start()) {
            throw "Failed to start '$FileName'."
        }

        $started = $true
        $stopwatch.Start()
        $processId = $process.Id
        $processRecord = [pscustomobject] @{
            ProcessId = $processId
            ParentProcessId = $PID
            StartedAt = $startedAtProcess
            FileName = $FileName
            Arguments = @($Arguments)
            CommandLine = $commandLine
            WorkingDirectory = $WorkingDirectory
            TimeoutMilliseconds = $TimeoutMilliseconds
        }
        [void] $processRecords.Add($processRecord)
        Write-Host ("START PID=$processId PARENT=$PID AT=$($startedAtProcess.ToString('O')) COMMAND=$commandLine")
        $stdoutBuffer = [char[]]::new(8192)
        $stderrBuffer = [char[]]::new(8192)
        $stdoutReadTask = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrReadTask = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        $stdoutPending = $true
        $stderrPending = $true
        $killIssued = $false
        $drainStartedAt = $null
        $pollMilliseconds = 50
        $maximumPolls = [Math]::Ceiling(($TimeoutMilliseconds + $terminationGraceMilliseconds) / [double] $pollMilliseconds) + 20

        for ($poll = 0; $poll -lt $maximumPolls; $poll++) {
            if ($stdoutPending -and $stdoutReadTask.IsCompleted) {
                try {
                    $count = $stdoutReadTask.GetAwaiter().GetResult()
                    if ($count -eq 0) {
                        $stdoutPending = $false
                    }
                    else {
                        Append-BoundedChars $stdoutState $stdoutBuffer $count
                        $stdoutReadTask = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
                    }
                }
                catch {
                    $stdoutPending = $false
                    $stdoutState.ReadError = $_.Exception.Message
                }
            }

            if ($stderrPending -and $stderrReadTask.IsCompleted) {
                try {
                    $count = $stderrReadTask.GetAwaiter().GetResult()
                    if ($count -eq 0) {
                        $stderrPending = $false
                    }
                    else {
                        Append-BoundedChars $stderrState $stderrBuffer $count
                        $stderrReadTask = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
                    }
                }
                catch {
                    $stderrPending = $false
                    $stderrState.ReadError = $_.Exception.Message
                }
            }

            $hasExited = $process.HasExited
            if ($termination -eq 'FailedToStart') {
                if ($script:CancellationRequested.Value) {
                    $termination = 'Cancelled'
                }
                elseif ($hasExited) {
                    $termination = 'Exited'
                    $exitCode = $process.ExitCode
                }
                elseif ($stopwatch.ElapsedMilliseconds -ge $TimeoutMilliseconds) {
                    $termination = 'TimedOut'
                }

                if ($termination -ne 'FailedToStart') {
                    $drainStartedAt = [Diagnostics.Stopwatch]::GetTimestamp()
                }
            }

            if ($termination -ne 'FailedToStart' -and $termination -ne 'Exited' -and -not $killIssued) {
                try {
                    if (-not $hasExited) {
                        $process.Kill($true)
                    }
                }
                catch [InvalidOperationException] { }
                catch [System.ComponentModel.Win32Exception] { }
                $killIssued = $true
            }

            if ($termination -ne 'FailedToStart' -and -not $stdoutPending -and -not $stderrPending) {
                break
            }

            if ($termination -ne 'FailedToStart' -and $null -ne $drainStartedAt) {
                $drainElapsed = ([Diagnostics.Stopwatch]::GetTimestamp() - $drainStartedAt) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                if ($drainElapsed -ge $terminationGraceMilliseconds) {
                    if ($termination -ne 'Exited') {
                        $cleanupIncomplete = $true
                    }
                    else {
                        $drainTimedOut = $true
                    }
                    break
                }
            }

            [void] $process.WaitForExit($pollMilliseconds)
        }

        if ($termination -eq 'FailedToStart') {
            $termination = 'TimedOut'
            $cleanupIncomplete = $true
        }
    }
    catch {
        if ($started -and $null -ne $processId) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
            }
            catch [InvalidOperationException] { }
            catch [System.ComponentModel.Win32Exception] { }
            try {
                if (-not $process.WaitForExit($terminationGraceMilliseconds)) {
                    $cleanupIncomplete = $true
                }
            }
            catch [InvalidOperationException] { }
        }

        throw
    }
    finally {
        $process.Dispose()
    }

    return [pscustomobject] @{
        ProcessId = $processId
        ParentProcessId = $PID
        StartedAt = $startedAtProcess
        CommandLine = $commandLine
        WorkingDirectory = $WorkingDirectory
        ExitCode = $exitCode
        Termination = $termination
        ElapsedMilliseconds = [int] $stopwatch.ElapsedMilliseconds
        StandardOutput = $stdoutState.Builder.ToString()
        StandardError = $stderrState.Builder.ToString()
        StandardOutputTruncated = [bool] $stdoutState.Truncated
        StandardErrorTruncated = [bool] $stderrState.Truncated
        StandardOutputReadError = $stdoutState.ReadError
        StandardErrorReadError = $stderrState.ReadError
        OutputDrainTimedOut = $drainTimedOut
        ProcessTreeCleanupIncomplete = $cleanupIncomplete
    }
}

function Get-ReferenceFiles {
    param(
        [Parameter()][string] $Directory,
        [Parameter()][string[]] $Paths,
        [Parameter(Mandatory = $true)][string] $RequiredSystemModulePath
    )

    $files = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($Directory)) {
        $enumerator = [IO.Directory]::EnumerateFiles($Directory, '*.dll', [IO.SearchOption]::TopDirectoryOnly).GetEnumerator()
        try {
            for ($index = 0; $index -le $maximumReferenceAssemblies; $index++) {
                if (-not $enumerator.MoveNext()) {
                    break
                }

                [void] $files.Add([IO.Path]::GetFullPath([string] $enumerator.Current))
            }
        }
        finally {
            $enumerator.Dispose()
        }

        if ($files.Count -gt $maximumReferenceAssemblies) {
            throw "Reference directory contains more than $maximumReferenceAssemblies DLLs."
        }
    }

    foreach ($path in @($Paths)) {
        if ($files.Count -ge $maximumReferenceAssemblies) {
            throw "More than $maximumReferenceAssemblies reference assemblies were supplied."
        }

        $resolved = Resolve-FullPath $path 'ReferencePath'
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Reference assembly does not exist: '$resolved'."
        }
        if ([IO.Path]::GetExtension($resolved) -ne '.dll') {
            throw "Reference assembly must be a .dll file: '$resolved'."
        }

        [void] $files.Add($resolved)
    }

    if (-not (Test-Path -LiteralPath $RequiredSystemModulePath -PathType Leaf)) {
        throw "The system module reference does not exist: '$RequiredSystemModulePath'."
    }
    if (-not ($files | Where-Object { $_ -eq $RequiredSystemModulePath })) {
        [void] $files.Add($RequiredSystemModulePath)
    }

    $unique = @($files | Sort-Object -Unique)
    if ($unique.Count -gt $maximumReferenceAssemblies) {
        throw "More than $maximumReferenceAssemblies unique reference assemblies were supplied."
    }
    if ($unique.Count -eq 0) {
        throw 'At least one reference assembly is required.'
    }

    return $unique
}

function Convert-ProcessEvidence {
    param([Parameter()][object] $Result)

    if ($null -eq $Result) {
        return $null
    }

    return [ordered] @{
        ProcessId = $Result.ProcessId
        ParentProcessId = $Result.ParentProcessId
        StartedAt = $Result.StartedAt.ToString('O')
        CommandLine = $Result.CommandLine
        WorkingDirectory = $Result.WorkingDirectory
        ExitCode = $Result.ExitCode
        Termination = $Result.Termination
        ElapsedMilliseconds = $Result.ElapsedMilliseconds
        StandardOutput = $Result.StandardOutput
        StandardError = $Result.StandardError
        StandardOutputTruncated = $Result.StandardOutputTruncated
        StandardErrorTruncated = $Result.StandardErrorTruncated
        OutputDrainTimedOut = $Result.OutputDrainTimedOut
        ProcessTreeCleanupIncomplete = $Result.ProcessTreeCleanupIncomplete
    }
}

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Evidence
    )

    $directory = [IO.Path]::GetDirectoryName($Path)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "Evidence path has no parent directory: '$Path'."
    }
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    $temporaryPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Evidence | ConvertTo-Json -Depth 10
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($temporaryPath, $json, $utf8)
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

$cancelHandler = [ConsoleCancelEventHandler] {
    param($sender, $eventArgs)
    $eventArgs.Cancel = $true
    $script:CancellationRequested.Value = $true
}

try {
    $assemblyFullPath = Resolve-FullPath $AssemblyPath 'AssemblyPath'
    if (-not (Test-Path -LiteralPath $assemblyFullPath -PathType Leaf)) {
        throw "Input assembly does not exist: '$assemblyFullPath'."
    }
    $assemblyInfo = Get-Item -LiteralPath $assemblyFullPath
    if ($assemblyInfo.Length -gt $maximumAssemblyBytes) {
        throw "Input assembly exceeds the $maximumAssemblyBytes byte limit."
    }
    if ([IO.Path]::GetExtension($assemblyFullPath).ToLowerInvariant() -notin @('.dll', '.exe')) {
        throw "Input assembly must be a .dll or .exe file: '$assemblyFullPath'."
    }

    $evidenceFullPath = if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        $assemblyFullPath + '.ilverify.json'
    }
    else {
        Resolve-FullPath $EvidencePath 'EvidencePath'
    }
    if ([string]::Equals($assemblyFullPath, $evidenceFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'EvidencePath must not overwrite the input assembly.'
    }

    $manifestFullPath = Resolve-FullPath (Join-Path $PSScriptRoot '..\.config\dotnet-tools.json') 'Tool manifest'
    if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
        throw "Tool manifest does not exist: '$manifestFullPath'."
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestFullPath | ConvertFrom-Json
    $manifestTool = $manifest.tools.'dotnet-ilverify'
    if ($null -eq $manifestTool -or [string] $manifestTool.version -ne $toolVersion) {
        throw "Tool manifest must pin $toolPackageId $toolVersion."
    }
    $manifestCommands = @($manifestTool.commands | ForEach-Object { [string] $_ })
    if ($manifestCommands -notcontains 'ilverify') {
        throw "Tool manifest must expose the 'ilverify' command."
    }

    $dotnetCommand = @(Get-Command -Name dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1)
    if ($dotnetCommand.Count -ne 1) {
        throw 'The dotnet executable was not found on PATH.'
    }
    $dotnetPath = [string] $dotnetCommand[0].Source
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
        throw 'The dotnet command has no executable source path.'
    }

    $dotnetRoot = if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        Resolve-FullPath $env:DOTNET_ROOT 'DOTNET_ROOT'
    }
    else {
        $dotnetBinaryDirectory = Split-Path -Parent $dotnetPath
        $candidateRoot = Resolve-FullPath $dotnetBinaryDirectory 'dotnet executable directory'
        if (Test-Path -LiteralPath (Join-Path $candidateRoot 'shared') -PathType Container) {
            $candidateRoot
        }
        else {
            # Linux distributions commonly expose /usr/bin/dotnet while keeping
            # the shared frameworks under /usr/share/dotnet.
            $parentRoot = Split-Path -Parent $candidateRoot
            $sharedRoot = Join-Path $parentRoot 'share/dotnet'
            if (Test-Path -LiteralPath (Join-Path $sharedRoot 'shared') -PathType Container) {
                $sharedRoot
            }
            else {
                $candidateRoot
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($ReferenceDirectory) -and @($ReferencePath).Count -eq 0) {
        $ReferenceDirectory = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$RuntimeVersion"
    }
    if (-not [string]::IsNullOrWhiteSpace($ReferenceDirectory)) {
        $referenceDirectoryFullPath = Resolve-FullPath $ReferenceDirectory 'ReferenceDirectory'
        if (-not (Test-Path -LiteralPath $referenceDirectoryFullPath -PathType Container)) {
            throw "Reference directory does not exist: '$referenceDirectoryFullPath'."
        }
    }

    if ([string]::IsNullOrWhiteSpace($SystemModule) -or $SystemModule -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$') {
        throw "SystemModule must be a simple assembly name: '$SystemModule'."
    }
    if ([string]::IsNullOrWhiteSpace($SystemModulePath)) {
        if ([string]::IsNullOrWhiteSpace($referenceDirectoryFullPath)) {
            throw 'SystemModulePath is required when ReferenceDirectory is not supplied.'
        }
        $SystemModulePath = Join-Path $referenceDirectoryFullPath ($SystemModule + '.dll')
    }
    $systemModulePathFull = Resolve-FullPath $SystemModulePath 'SystemModulePath'
    $referenceFiles = @(Get-ReferenceFiles $referenceDirectoryFullPath $ReferencePath $systemModulePathFull)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($assemblyFullPath)
        try {
            $assemblyHash = [Convert]::ToHexString($sha256.ComputeHash($stream))
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }

    $repositoryRoot = Resolve-FullPath (Join-Path $PSScriptRoot '..') 'Repository root'
    [Console]::add_CancelKeyPress($cancelHandler)
    if ($Restore) {
        $restoreArguments = @(
            'tool', 'restore', '--tool-manifest', $manifestFullPath, '--disable-parallel'
        )
        $restoreResult = Invoke-BoundedProcess -FileName $dotnetPath -Arguments $restoreArguments -WorkingDirectory $repositoryRoot -TimeoutMilliseconds ($TimeoutSeconds * 1000)
        if ($restoreResult.Termination -ne 'Exited' -or
            $restoreResult.ExitCode -ne 0 -or
            $restoreResult.ProcessTreeCleanupIncomplete -or
            $restoreResult.OutputDrainTimedOut -or
            $restoreResult.StandardOutputTruncated -or
            $restoreResult.StandardErrorTruncated -or
            -not [string]::IsNullOrWhiteSpace($restoreResult.StandardOutputReadError) -or
            -not [string]::IsNullOrWhiteSpace($restoreResult.StandardErrorReadError)) {
            throw "dotnet tool restore failed with termination '$($restoreResult.Termination)' and exit code '$($restoreResult.ExitCode)'."
        }
    }

    $verifyArguments = [Collections.Generic.List[string]]::new()
    [void] $verifyArguments.Add('-s')
    [void] $verifyArguments.Add($SystemModule)
    foreach ($referenceFile in $referenceFiles) {
        [void] $verifyArguments.Add('-r')
        [void] $verifyArguments.Add($referenceFile)
    }
    [void] $verifyArguments.Add('--statistics')
    [void] $verifyArguments.Add($assemblyFullPath)

    $allVerifyArguments = @('tool', 'run', 'ilverify', '--') + $verifyArguments.ToArray()
    $verifyResult = Invoke-BoundedProcess -FileName $dotnetPath -Arguments $allVerifyArguments -WorkingDirectory $repositoryRoot -TimeoutMilliseconds ($TimeoutSeconds * 1000)
    $succeeded = $verifyResult.Termination -eq 'Exited' -and
        $verifyResult.ExitCode -eq 0 -and
        -not $verifyResult.ProcessTreeCleanupIncomplete -and
        -not $verifyResult.OutputDrainTimedOut -and
        -not $verifyResult.StandardOutputTruncated -and
        -not $verifyResult.StandardErrorTruncated -and
        [string]::IsNullOrWhiteSpace($verifyResult.StandardOutputReadError) -and
        [string]::IsNullOrWhiteSpace($verifyResult.StandardErrorReadError)
    if (-not $succeeded) {
        throw "ILVerify failed with termination '$($verifyResult.Termination)' and exit code '$($verifyResult.ExitCode)'."
    }
}
catch {
    $failureMessage = $_.Exception.Message
    $succeeded = $false
}
finally {
    [Console]::remove_CancelKeyPress($cancelHandler)
    $completedAt = [DateTimeOffset]::UtcNow
    $evidence = [ordered] @{
        SchemaVersion = 1
        StartedAt = $startedAt.ToString('O')
        CompletedAt = $completedAt.ToString('O')
        Succeeded = $succeeded
        Failure = $failureMessage
        Assembly = [ordered] @{
            Path = $assemblyFullPath
            Length = if ($null -ne $assemblyFullPath -and (Test-Path -LiteralPath $assemblyFullPath -PathType Leaf)) { (Get-Item -LiteralPath $assemblyFullPath).Length } else { $null }
            Sha256 = if ($null -ne $assemblyHash) { $assemblyHash } else { $null }
        }
        Tool = [ordered] @{
            PackageId = $toolPackageId
            Version = $toolVersion
            Command = 'ilverify'
            ManifestPath = $manifestFullPath
            RestoreRequested = [bool] $Restore
        }
        Environment = [ordered] @{
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            OperatingSystem = [Environment]::OSVersion.VersionString
            ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            RuntimeIdentifier = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
            DotnetPath = $dotnetPath
            DotnetRoot = $dotnetRoot
        }
        Verification = [ordered] @{
            SystemModule = $SystemModule
            SystemModulePath = $systemModulePathFull
            ReferenceDirectory = $referenceDirectoryFullPath
            ReferenceFiles = @($referenceFiles)
            RuntimeVersion = $RuntimeVersion
            TimeoutSeconds = $TimeoutSeconds
        }
        RestoreProcess = Convert-ProcessEvidence $restoreResult
        VerifyProcess = Convert-ProcessEvidence $verifyResult
        ProcessRecords = @($processRecords | ForEach-Object {
            [ordered] @{
                ProcessId = $_.ProcessId
                ParentProcessId = $_.ParentProcessId
                StartedAt = $_.StartedAt.ToString('O')
                FileName = $_.FileName
                Arguments = @($_.Arguments)
                CommandLine = $_.CommandLine
                WorkingDirectory = $_.WorkingDirectory
                TimeoutMilliseconds = $_.TimeoutMilliseconds
            }
        })
    }

    if ($null -ne $evidenceFullPath) {
        try {
            Write-Evidence $evidenceFullPath $evidence
            Write-Host "Evidence: $evidenceFullPath"
        }
        catch {
            $evidenceError = $_.Exception.Message
            Write-Error "Could not write ILVerify evidence: $evidenceError"
            if ($succeeded) {
                $succeeded = $false
                $failureMessage = $evidenceError
            }
        }
    }
}

if (-not $succeeded) {
    if ([string]::IsNullOrWhiteSpace($failureMessage)) {
        $failureMessage = 'ILVerify did not complete successfully.'
    }
    Write-Error $failureMessage
    exit 1
}

Write-Output 'ILVerify succeeded.'
exit 0
