[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $SourcePath = 'samples/hello.rs',

    [Parameter(Position = 1)]
    [string] $OutputDirectory = 'artifacts/p0/windows-x64-aot',

    [Parameter(Position = 2)]
    [string] $EvidencePath = 'artifacts/p0/windows-x64-aot.json',

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int] $PublishTimeoutSeconds = 300,

    [Parameter()]
    [ValidateRange(1, 300)]
    [int] $RunTimeoutSeconds = 30,

    [Parameter()]
    [ValidateSet('vertical-slice-v1', 'safe-core-primitives-v1')]
    [string] $Profile = 'vertical-slice-v1',

    [Parameter()]
    [ValidateLength(0, 4096)]
    [string] $ExpectedStandardOutput = ('Hello from Rust#' + [char] 10)
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Invoke-WindowsNativeAotProbe.ps1 requires PowerShell 7 or newer.'
}

$maximumOutputCharacters = 262144
$pollMilliseconds = 50
$terminationGraceMilliseconds = 5000
$maximumCleanupAttempts = 40
$cleanupTimeout = [TimeSpan]::FromSeconds(5)
$maximumCommandLineArgumentCharacters = 32767
$maximumExecutableBytes = 536870912
$script:CancellationRequested = [pscustomobject] @{ Value = $false }
$processRecords = [Collections.Generic.List[object]]::new()

$startedAt = [DateTimeOffset]::UtcNow
$status = 'failed'
$reason = 'probe-did-not-complete'
$failure = $null
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceFullPath = $null
$outputFullPath = $null
$evidenceFullPath = $null
$evidencePathValidated = $false
$expectedExecutablePath = $null
$actualExecutablePath = $null
$publishLogPath = $null
$runLogPath = $null
$executableValidation = $null
$dotnetPath = $null
$dotnetVersion = $null
$versionProbe = $null
$publishResult = $null
$runResult = $null
$tempDirectory = $null
$tempCleanupDiagnostic = $null
$outputDirectoryPreexisting = $false
$isWindowsRuntime = [OperatingSystem]::IsWindows()
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$runtimeIdentifier = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $ParameterName
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$ParameterName must not be empty."
    }

    try {
        return [IO.Path]::GetFullPath($Path, $BasePath)
    }
    catch {
        throw "$ParameterName is not a valid path: $($_.Exception.Message)"
    }
}

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Directory
    )

    $relativePath = [IO.Path]::GetRelativePath($Directory, $Path)
    if ($relativePath -eq '.') {
        return $true
    }
    if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath -eq '..') {
        return $false
    }

    $parentPrefix = '..' + [IO.Path]::DirectorySeparatorChar
    $alternateParentPrefix = '..' + [IO.Path]::AltDirectorySeparatorChar
    return -not $relativePath.StartsWith($parentPrefix, [StringComparison]::Ordinal) -and
        -not $relativePath.StartsWith($alternateParentPrefix, [StringComparison]::Ordinal)
}

function Assert-SafeEvidencePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $OutputDirectory,
        [Parameter(Mandatory = $true)][string[]] $GeneratedPaths
    )

    if ($GeneratedPaths.Count -gt 16) {
        throw 'Generated path validation exceeded the 16 item safety limit.'
    }

    if ([string]::Equals($Path, $Source, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'EvidencePath must not be the source path.'
    }
    if (Test-PathWithinDirectory -Path $Path -Directory $OutputDirectory) {
        throw 'EvidencePath must not be the output directory or a path inside it.'
    }
    if (Test-PathWithinDirectory -Path $OutputDirectory -Directory $Path) {
        throw 'OutputDirectory must not be nested beneath the EvidencePath file path.'
    }

    foreach ($generatedPath in $GeneratedPaths) {
        if ([string]::Equals($Path, $generatedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "EvidencePath conflicts with a generated probe artifact: '$generatedPath'."
        }
    }

    if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) {
        throw "EvidencePath already exists and will not be overwritten: '$Path'."
    }
}

function Quote-Argument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Length -gt $maximumCommandLineArgumentCharacters) {
        throw "Command-line argument exceeds the $maximumCommandLineArgumentCharacters character display limit."
    }

    if ($Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new($Value.Length + 2)
    [void] $builder.Append([char] 34)
    $pendingBackslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char] 92) {
            $pendingBackslashes++
            continue
        }

        if ($character -eq [char] 34) {
            [void] $builder.Append([char] 92, ($pendingBackslashes * 2) + 1)
            [void] $builder.Append([char] 34)
        }
        else {
            if ($pendingBackslashes -ne 0) {
                [void] $builder.Append([char] 92, $pendingBackslashes)
            }
            [void] $builder.Append($character)
        }

        $pendingBackslashes = 0
    }

    if ($pendingBackslashes -ne 0) {
        [void] $builder.Append([char] 92, $pendingBackslashes * 2)
    }
    [void] $builder.Append([char] 34)
    return $builder.ToString()
}

function Format-CommandLine {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [Parameter()][string[]] $Arguments = @()
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
        [Parameter()][string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int] $TimeoutSeconds
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
    $commandLine = Format-CommandLine -FileName $FileName -Arguments $Arguments
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
    $processId = $null
    $termination = 'FailedToStart'
    $exitCode = $null
    $cleanupAttempted = $false
    $cleanupIncomplete = $false
    $cleanupDiagnostics = [Collections.Generic.List[string]]::new()
    $drainTimedOut = $false
    $killIssued = $false
    $stopwatch = [Diagnostics.Stopwatch]::new()
    $startedAtProcess = [DateTimeOffset]::UtcNow
    $stdoutPending = $false
    $stderrPending = $false
    $stdoutReadTask = $null
    $stderrReadTask = $null

    try {
        if (-not $process.Start()) {
            throw "Failed to start '$FileName'."
        }

        $started = $true
        $stopwatch.Start()
        $processId = $process.Id
        [void] $processRecords.Add([pscustomobject] @{
                ProcessId = $processId
                ParentProcessId = $PID
                StartedAt = $startedAtProcess
                FileName = $FileName
                Arguments = @($Arguments)
                CommandLine = $commandLine
                WorkingDirectory = $WorkingDirectory
                TimeoutSeconds = $TimeoutSeconds
            })
        Write-Host "START PID=$processId PARENT=$PID AT=$($startedAtProcess.ToString('O')) COMMAND=$commandLine"

        $stdoutBuffer = [char[]]::new(8192)
        $stderrBuffer = [char[]]::new(8192)
        $stdoutReadTask = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrReadTask = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        $stdoutPending = $true
        $stderrPending = $true
        $timeoutMilliseconds = [int64] $TimeoutSeconds * 1000
        $drainStartedAt = $null
        $maximumPolls = [int] [Math]::Min(
            [int]::MaxValue,
            [Math]::Ceiling(($timeoutMilliseconds + $terminationGraceMilliseconds) / [double] $pollMilliseconds) + 20)

        for ($poll = 0; $poll -lt $maximumPolls; $poll++) {
            if ($script:CancellationRequested.Value -and $termination -eq 'FailedToStart') {
                $termination = 'Cancelled'
                $drainStartedAt = [Diagnostics.Stopwatch]::GetTimestamp()
            }

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

            $hasExited = $false
            try {
                $hasExited = $process.HasExited
            }
            catch {
                [void] $cleanupDiagnostics.Add("Could not inspect process state: $($_.Exception.Message)")
            }

            if ($termination -eq 'FailedToStart') {
                if ($script:CancellationRequested.Value) {
                    $termination = 'Cancelled'
                }
                elseif ($hasExited) {
                    $termination = 'Exited'
                    $exitCode = $process.ExitCode
                }
                elseif ($stopwatch.ElapsedMilliseconds -ge $timeoutMilliseconds) {
                    $termination = 'TimedOut'
                }

                if ($termination -ne 'FailedToStart') {
                    $drainStartedAt = [Diagnostics.Stopwatch]::GetTimestamp()
                }
            }

            if ($termination -in @('TimedOut', 'Cancelled') -and -not $killIssued) {
                $cleanupAttempted = $true
                try {
                    if (-not $hasExited) {
                        $process.Kill($true)
                    }
                }
                catch [InvalidOperationException] {
                    [void] $cleanupDiagnostics.Add("Process termination race: $($_.Exception.Message)")
                }
                catch [ComponentModel.Win32Exception] {
                    [void] $cleanupDiagnostics.Add("Process tree termination failed: $($_.Exception.Message)")
                    $cleanupIncomplete = $true
                }
                catch [PlatformNotSupportedException] {
                    [void] $cleanupDiagnostics.Add('Process-tree termination is not supported.')
                    $cleanupIncomplete = $true
                }
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
                        $cleanupAttempted = $true
                        $cleanupIncomplete = $true
                        [void] $cleanupDiagnostics.Add(
                            'Output streams did not close after the root process exited; a descendant may still hold an inherited pipe handle.')
                    }
                    break
                }
            }

            [void] $process.WaitForExit($pollMilliseconds)
        }

        if ($termination -eq 'FailedToStart') {
            $termination = 'TimedOut'
            $cleanupAttempted = $true
            $cleanupIncomplete = $true
            [void] $cleanupDiagnostics.Add('Process did not reach a terminal state within the bounded poll count.')
        }

        if ($termination -ne 'Exited' -and $started) {
            $cleanupAttempted = $true
            if (-not $killIssued) {
                try {
                    if (-not $process.HasExited) {
                        $process.Kill($true)
                    }
                }
                catch {
                    [void] $cleanupDiagnostics.Add("Process tree termination failed: $($_.Exception.Message)")
                    $cleanupIncomplete = $true
                }
                $killIssued = $true
            }

            try {
                if (-not $process.WaitForExit($terminationGraceMilliseconds)) {
                    $cleanupIncomplete = $true
                    [void] $cleanupDiagnostics.Add(('Process did not exit within ' + $terminationGraceMilliseconds + 'ms after termination.'))
                }
            }
            catch {
                $cleanupIncomplete = $true
                [void] $cleanupDiagnostics.Add("Could not wait for process termination: $($_.Exception.Message)")
            }
        }
    }
    catch {
        if ($started) {
            $cleanupAttempted = $true
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
            }
            catch {
                [void] $cleanupDiagnostics.Add("Process cleanup after error failed: $($_.Exception.Message)")
                $cleanupIncomplete = $true
            }
            try {
                if (-not $process.WaitForExit($terminationGraceMilliseconds)) {
                    $cleanupIncomplete = $true
                }
            }
            catch {
                $cleanupIncomplete = $true
            }
        }

        [void] $cleanupDiagnostics.Add("Process invocation failed: $($_.Exception.Message)")
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }

    $readDiagnostics = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($stdoutState.ReadError)) {
        [void] $readDiagnostics.Add("stdout read: $($stdoutState.ReadError)")
    }
    if (-not [string]::IsNullOrWhiteSpace($stderrState.ReadError)) {
        [void] $readDiagnostics.Add("stderr read: $($stderrState.ReadError)")
    }
    foreach ($diagnostic in $readDiagnostics) {
        [void] $cleanupDiagnostics.Add($diagnostic)
    }

    [pscustomobject] @{
        ProcessId = $processId
        ParentProcessId = $PID
        StartedAt = $startedAtProcess
        CommandLine = $commandLine
        WorkingDirectory = $WorkingDirectory
        ExitCode = $exitCode
        Termination = $termination
        ElapsedMilliseconds = [int64] $stopwatch.Elapsed.TotalMilliseconds
        StandardOutput = $stdoutState.Builder.ToString()
        StandardError = $stderrState.Builder.ToString()
        StandardOutputReadError = $stdoutState.ReadError
        StandardErrorReadError = $stderrState.ReadError
        OutputReadFailed = $readDiagnostics.Count -ne 0
        StandardOutputTruncated = [bool] $stdoutState.Truncated
        StandardErrorTruncated = [bool] $stderrState.Truncated
        OutputDrainTimedOut = $drainTimedOut
        CleanupAttempted = $cleanupAttempted
        CleanupIncomplete = $cleanupIncomplete
        CleanupDiagnostic = if ($cleanupDiagnostics.Count -eq 0) { $null } else { $cleanupDiagnostics -join ' ' }
    }
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
        StandardOutputReadError = $Result.StandardOutputReadError
        StandardErrorReadError = $Result.StandardErrorReadError
        OutputReadFailed = $Result.OutputReadFailed
        StandardOutputTruncated = $Result.StandardOutputTruncated
        StandardErrorTruncated = $Result.StandardErrorTruncated
        OutputDrainTimedOut = $Result.OutputDrainTimedOut
        CleanupAttempted = $Result.CleanupAttempted
        CleanupIncomplete = $Result.CleanupIncomplete
        CleanupDiagnostic = $Result.CleanupDiagnostic
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
        $json = $Evidence | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $Path, $false)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Try-DeleteDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastException = $null
    for ($attempt = 0; $attempt -lt $maximumCleanupAttempts -and $clock.Elapsed -lt $cleanupTimeout; $attempt++) {
        if (-not [IO.Directory]::Exists($Path)) {
            return $null
        }

        try {
            [IO.Directory]::Delete($Path, $true)
        }
        catch {
            $lastException = $_.Exception
        }

        if (-not [IO.Directory]::Exists($Path)) {
            return $null
        }

        Start-Sleep -Milliseconds 50
    }

    $detail = if ($null -eq $lastException) { 'directory still exists' } else { $lastException.Message }
    return "Temporary probe cleanup failed after $maximumCleanupAttempts attempts or $($cleanupTimeout.TotalSeconds) seconds: $detail"
}

function Get-AssemblyName {
    param([Parameter(Mandatory = $true)][string] $Path)

    $candidate = [IO.Path]::GetFileNameWithoutExtension($Path)
    $builder = [Text.StringBuilder]::new()
    foreach ($character in $candidate.ToCharArray()) {
        if ($builder.Length -ge 128) {
            break
        }

        if ([char]::IsAsciiLetterOrDigit($character) -or $character -in @('.', '_', '-')) {
            [void] $builder.Append($character)
        }
        else {
            [void] $builder.Append('_')
        }
    }

    if ($builder.Length -eq 0) {
        return 'program'
    }

    return $builder.ToString()
}

function Get-WindowsX64NativePeEvidence {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = $null
    $peReader = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $length = $stream.Length
        if ($length -le 0 -or $length -gt $maximumExecutableBytes) {
            throw "Native AOT executable length $length is outside the allowed range 1..$maximumExecutableBytes bytes."
        }

        $peReader = [Reflection.PortableExecutable.PEReader]::new($stream)
        $headers = $peReader.PEHeaders
        $peHeader = $headers.PEHeader
        if ($null -eq $peHeader) {
            throw 'Native AOT executable does not contain a PE optional header.'
        }

        $machine = $headers.CoffHeader.Machine
        $magic = $peHeader.Magic
        $characteristics = $headers.CoffHeader.Characteristics
        $subsystem = $peHeader.Subsystem
        $isExecutableImage = ($characteristics -band [Reflection.PortableExecutable.Characteristics]::ExecutableImage) -ne 0
        $isDll = ($characteristics -band [Reflection.PortableExecutable.Characteristics]::Dll) -ne 0
        $hasCorHeader = $null -ne $headers.CorHeader
        $hasMetadata = $peReader.HasMetadata
        $entryPointRva = $peHeader.AddressOfEntryPoint

        if ($machine -ne [Reflection.PortableExecutable.Machine]::Amd64 -or
            $magic -ne [Reflection.PortableExecutable.PEMagic]::PE32Plus -or
            $subsystem -ne [Reflection.PortableExecutable.Subsystem]::WindowsCui -or
            -not $isExecutableImage -or
            $isDll -or
            $entryPointRva -eq 0 -or
            $hasCorHeader -or
            $hasMetadata) {
            throw "Native AOT executable failed Windows x64 native PE validation (machine=$machine, magic=$magic, subsystem=$subsystem, executable=$isExecutableImage, dll=$isDll, entryPointRva=$entryPointRva, corHeader=$hasCorHeader, metadata=$hasMetadata)."
        }

        $stream.Position = 0
        $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        return [ordered] @{
            Format = 'PE'
            Machine = $machine.ToString()
            Magic = $magic.ToString()
            Subsystem = $subsystem.ToString()
            IsExecutableImage = $isExecutableImage
            IsDll = $isDll
            EntryPointRva = $entryPointRva
            HasCorHeader = $hasCorHeader
            HasMetadata = $hasMetadata
            Length = $length
            Sha256 = $sha256
        }
    }
    catch [BadImageFormatException] {
        throw "Native AOT executable is not a valid PE image: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $peReader) {
            $peReader.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Stop-Blocked {
    param(
        [Parameter(Mandatory = $true)][string] $BlockReason,
        [Parameter(Mandatory = $true)][string] $Message
    )

    throw "BLOCKED:$BlockReason$([Environment]::NewLine)$Message"
}

$cancelHandler = [ConsoleCancelEventHandler] {
    param($sender, $eventArgs)
    $eventArgs.Cancel = $true
    $script:CancellationRequested.Value = $true
}

try {
    $sourceFullPath = Resolve-FullPath $SourcePath $repoRoot 'SourcePath'
    $outputFullPath = Resolve-FullPath $OutputDirectory $repoRoot 'OutputDirectory'
    $evidenceFullPath = Resolve-FullPath $EvidencePath $repoRoot 'EvidencePath'
    $assemblyName = Get-AssemblyName $sourceFullPath
    $expectedExecutablePath = Join-Path $outputFullPath "$assemblyName.NativeAotHost.exe"
    $publishLogPath = Join-Path $outputFullPath 'windows-aot-publish.log'
    $runLogPath = Join-Path $outputFullPath 'windows-aot-run.log'
    Assert-SafeEvidencePath `
        -Path $evidenceFullPath `
        -Source $sourceFullPath `
        -OutputDirectory $outputFullPath `
        -GeneratedPaths @($expectedExecutablePath, $publishLogPath, $runLogPath)
    $evidencePathValidated = $true
    if (-not $isWindowsRuntime -or
        $osArchitecture -ne [Runtime.InteropServices.Architecture]::X64 -or
        $processArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        Stop-Blocked `
            'native-windows-x64-required' `
            "Windows x64 Native AOT evidence requires a Windows kernel with x64 OS and process architectures (isWindows=$isWindowsRuntime, osArchitecture=$osArchitecture, processArchitecture=$processArchitecture, runtimeIdentifier=$runtimeIdentifier)."
    }
    if (-not [IO.File]::Exists($sourceFullPath)) {
        throw "Source file does not exist: '$sourceFullPath'."
    }

    if ([IO.Directory]::Exists($outputFullPath)) {
        $outputDirectoryPreexisting = $true
        $enumerator = [IO.Directory]::EnumerateFileSystemEntries($outputFullPath).GetEnumerator()
        try {
            if ($enumerator.MoveNext()) {
                throw "Output directory must be empty: '$outputFullPath'."
            }
        }
        finally {
            $enumerator.Dispose()
        }
    }
    else {
        [IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
    }

    $dotnetCommands = @(Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($dotnetCommands.Count -ne 1) {
        Stop-Blocked 'dotnet-not-found' 'The dotnet executable was not found on PATH.'
    }
    $dotnetPath = [string] $dotnetCommands[0].Source
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
        Stop-Blocked 'dotnet-path-unavailable' 'The dotnet command did not expose an executable path.'
    }

    [Console]::add_CancelKeyPress($cancelHandler)
    $versionProbe = Invoke-BoundedProcess `
        -FileName $dotnetPath `
        -Arguments @('--version') `
        -WorkingDirectory $repoRoot `
        -TimeoutSeconds 30
    $dotnetVersion = (($versionProbe.StandardOutput -split '\r?\n' | Select-Object -First 1).Trim())
    if ($versionProbe.Termination -ne 'Exited' -or
        $versionProbe.ExitCode -ne 0 -or
        $versionProbe.CleanupIncomplete -or
        $versionProbe.OutputDrainTimedOut -or
        $versionProbe.OutputReadFailed -or
        $versionProbe.StandardOutputTruncated -or
        $versionProbe.StandardErrorTruncated) {
        Stop-Blocked 'dotnet-sdk-unavailable' "The pinned .NET SDK could not be executed: $($versionProbe.CleanupDiagnostic)"
    }
    if ($dotnetVersion -ne '10.0.400') {
        Stop-Blocked 'dotnet-sdk-version-mismatch' "Expected .NET SDK 10.0.400, found '$dotnetVersion'."
    }

    $tempDirectory = Join-Path ([IO.Path]::GetTempPath()) "rustsharp-windows-aot-$PID-$([Guid]::NewGuid().ToString('N'))"
    [IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $projectPath = Join-Path $repoRoot 'src/RustSharp.Cli'
    $publishArguments = @(
        'run', '--project', $projectPath, '--configuration', 'Release', '--no-restore', '--',
        'publish', $sourceFullPath, '--runtime', 'win-x64', '--output', $outputFullPath,
        '--profile', $Profile,
        '--timeout', $PublishTimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    $publishResult = Invoke-BoundedProcess `
        -FileName $dotnetPath `
        -Arguments $publishArguments `
        -WorkingDirectory $repoRoot `
        -TimeoutSeconds $PublishTimeoutSeconds
    if ($publishResult.Termination -ne 'Exited' -or
        $publishResult.ExitCode -ne 0 -or
        $publishResult.CleanupIncomplete -or
        $publishResult.OutputDrainTimedOut -or
        $publishResult.OutputReadFailed -or
        $publishResult.StandardOutputTruncated -or
        $publishResult.StandardErrorTruncated) {
        throw "Native AOT publish failed (termination=$($publishResult.Termination), exitCode=$($publishResult.ExitCode))."
    }

    if (-not [IO.File]::Exists($expectedExecutablePath)) {
        throw "Native AOT executable was not produced at '$expectedExecutablePath'."
    }
    $actualExecutablePath = $expectedExecutablePath
    $executableValidation = Get-WindowsX64NativePeEvidence $actualExecutablePath
    [IO.File]::WriteAllText(
        $publishLogPath,
        $publishResult.StandardOutput + $publishResult.StandardError,
        [Text.UTF8Encoding]::new($false))

    $runResult = Invoke-BoundedProcess `
        -FileName $actualExecutablePath `
        -Arguments ([string[]]::new(0)) `
        -WorkingDirectory $outputFullPath `
        -TimeoutSeconds $RunTimeoutSeconds
    $newLine = [Environment]::NewLine
    $actualOutput = $runResult.StandardOutput.Replace($newLine, [string][char] 10).Replace([string][char] 13, [string][char] 10)
    $expectedOutput = $ExpectedStandardOutput.Replace($newLine, [string][char] 10).Replace([string][char] 13, [string][char] 10)
    if ($runResult.Termination -ne 'Exited' -or
        $runResult.ExitCode -ne 0 -or
        $runResult.CleanupIncomplete -or
        $runResult.OutputDrainTimedOut -or
        $runResult.OutputReadFailed -or
        $runResult.StandardOutputTruncated -or
        $runResult.StandardErrorTruncated -or
        $actualOutput -ne $expectedOutput) {
        throw "Native AOT executable did not produce the expected bounded result (termination=$($runResult.Termination), exitCode=$($runResult.ExitCode), output='$actualOutput')."
    }

    [IO.File]::WriteAllText(
        $runLogPath,
        $runResult.StandardOutput + $runResult.StandardError,
        [Text.UTF8Encoding]::new($false))
    $status = 'passed'
    $reason = 'native-aot-output-verified'
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('BLOCKED:', [StringComparison]::Ordinal)) {
        $payload = $message.Substring(8)
        $separator = $payload.IndexOf([Environment]::NewLine, [StringComparison]::Ordinal)
        if ($separator -ge 0) {
            $reason = $payload.Substring(0, $separator)
            $failure = $payload.Substring($separator + [Environment]::NewLine.Length)
        }
        else {
            $reason = 'environment-blocked'
            $failure = $payload
        }
        $status = 'blocked'
    }
    else {
        $failure = $message
        $status = 'failed'
        if ($reason -eq 'probe-did-not-complete') {
            $reason = 'probe-failed'
        }
    }
}
finally {
    [Console]::remove_CancelKeyPress($cancelHandler)
    if ($null -ne $tempDirectory) {
        $tempCleanupDiagnostic = Try-DeleteDirectory $tempDirectory
        if ($null -ne $tempCleanupDiagnostic -and $status -eq 'passed') {
            $status = 'failed'
            $reason = 'temporary-cleanup-failed'
            $failure = $tempCleanupDiagnostic
        }
    }

    $completedAt = [DateTimeOffset]::UtcNow
    $executableLength = if ($null -ne $actualExecutablePath -and [IO.File]::Exists($actualExecutablePath)) {
        ([IO.FileInfo]::new($actualExecutablePath)).Length
    }
    else {
        $null
    }
    $evidence = [ordered] @{
        SchemaVersion = 1
        Profile = $Profile
        ExpectedStandardOutput = $ExpectedStandardOutput
        Status = $status
        Reason = $reason
        Failure = $failure
        StartedAt = $startedAt.ToString('O')
        CompletedAt = $completedAt.ToString('O')
        Source = [ordered] @{
            Path = $sourceFullPath
        }
        Output = [ordered] @{
            Directory = $outputFullPath
            PreexistingDirectory = $outputDirectoryPreexisting
            ExpectedExecutable = $expectedExecutablePath
            Executable = $actualExecutablePath
            ExecutableLength = $executableLength
            NativePeValidation = $executableValidation
        }
        Environment = [ordered] @{
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            OperatingSystem = [Environment]::OSVersion.VersionString
            IsWindows = $isWindowsRuntime
            OsArchitecture = $osArchitecture.ToString()
            ProcessArchitecture = $processArchitecture.ToString()
            RuntimeIdentifier = $runtimeIdentifier
            DotnetPath = $dotnetPath
            DotnetVersion = $dotnetVersion
            PublishTimeoutSeconds = $PublishTimeoutSeconds
            RunTimeoutSeconds = $RunTimeoutSeconds
        }
        VersionProbe = Convert-ProcessEvidence $versionProbe
        PublishProcess = Convert-ProcessEvidence $publishResult
        RunProcess = Convert-ProcessEvidence $runResult
        ProcessRecords = @($processRecords | ForEach-Object {
                [ordered] @{
                    ProcessId = $_.ProcessId
                    ParentProcessId = $_.ParentProcessId
                    StartedAt = $_.StartedAt.ToString('O')
                    FileName = $_.FileName
                    Arguments = @($_.Arguments)
                    CommandLine = $_.CommandLine
                    WorkingDirectory = $_.WorkingDirectory
                    TimeoutSeconds = $_.TimeoutSeconds
                }
            })
        Cleanup = [ordered] @{
            TemporaryDirectory = $tempDirectory
            Diagnostic = $tempCleanupDiagnostic
        }
    }

    if ($evidencePathValidated -and $null -ne $evidenceFullPath) {
        try {
            Write-Evidence $evidenceFullPath $evidence
            Write-Host "Evidence: $evidenceFullPath"
        }
        catch {
            $evidenceWriteError = $_.Exception.Message
            [Console]::Error.WriteLine("Could not write Native AOT evidence: $evidenceWriteError")
            $status = 'failed'
            $reason = 'evidence-write-failed'
            $failure = $evidenceWriteError
        }
    }
}

if ($status -eq 'passed') {
    Write-Output "Windows x64 Native AOT passed. Evidence: $evidenceFullPath"
    exit 0
}
if ($status -eq 'blocked') {
    Write-Warning "Windows x64 Native AOT blocked: $failure. Evidence: $evidenceFullPath"
    exit 2
}

[Console]::Error.WriteLine("Windows x64 Native AOT failed: $failure")
exit 1
