[CmdletBinding()]
param(
    [Parameter()]
    [string] $CompilerPath,

    [Parameter()]
    [string] $EvidenceDirectory,

    [Parameter()]
    [ValidateSet(1, 10)]
    [int] $MaximumCases = 10,

    [Parameter()]
    [ValidateRange(30, 600)]
    [int] $DeadlineSeconds = 300,

    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $RuntimeVersion = '10.0.11'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'This suite requires PowerShell 7 or newer.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$verifier = Join-Path $PSScriptRoot 'Invoke-ILVerify.ps1'
$bounded = Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$pwsh = (Get-Process -Id $PID).Path
if ([string]::IsNullOrWhiteSpace($CompilerPath)) { $CompilerPath = Join-Path $root 'src/RustSharp.Cli/bin/Release/net10.0/rsc.dll' }
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
if (-not [IO.File]::Exists($CompilerPath)) { throw 'An already built production compiler is required; this suite never builds or restores tools.' }
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $root ('artifacts/p1-supervision/ilverify-runtime-reference-' + [Guid]::NewGuid().ToString('N'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if ([IO.Directory]::Exists($EvidenceDirectory)) { throw 'EvidenceDirectory must be new so existing user reports cannot be overwritten.' }
[void][IO.Directory]::CreateDirectory($EvidenceDirectory)
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent ('rustsharp-ilverify-reference-' + [Guid]::NewGuid().ToString('N'))))
if (-not $ownedRoot.StartsWith($temporaryParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The unique test fixture escaped its temporary parent.'
}
[void][IO.Directory]::CreateDirectory($ownedRoot)
$clock = [Diagnostics.Stopwatch]::StartNew()
$startedAt = [DateTimeOffset]::UtcNow
$cases = [Collections.Generic.List[object]]::new()
$suiteProcesses = [Collections.Generic.List[object]]::new()
$failure = $null
$cleanupComplete = $false
$cancelState = [pscustomobject]@{ Value = $false }
$cancelHandler = [ConsoleCancelEventHandler]{ param($sender, $eventArgs) $eventArgs.Cancel = $true; $cancelState.Value = $true }
[Console]::add_CancelKeyPress($cancelHandler)

function Assert-Budget {
    if ($cancelState.Value -or $clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Suite cancelled or exceeded its whole-run deadline.' }
}

function Run-OwnedProcess {
    param([string] $Executable, [string[]] $Arguments, [string] $CapturePrefix, [bool] $MayFail = $false)
    Assert-Budget
    $remaining = [Math]::Max(1, [Math]::Min(30, [Math]::Ceiling($DeadlineSeconds - $clock.Elapsed.TotalSeconds)))
    $processError = $null
    try {
        & $bounded -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $ownedRoot -TimeoutSeconds $remaining -CapturePrefix $CapturePrefix -MaximumOutputBytes 2097152
    }
    catch { $processError = $_.Exception.Message }
    $metadataPath = $CapturePrefix + '.process.json'
    if (-not [IO.File]::Exists($metadataPath)) { throw 'A child command failed without process ownership metadata.' }
    $metadata = [IO.File]::ReadAllText($metadataPath) | ConvertFrom-Json
    [void]$suiteProcesses.Add($metadata)
    if (-not $metadata.CleanupComplete) { throw 'An owned child process reported incomplete cleanup.' }
    if ($null -ne $processError -and -not $MayFail) { throw $processError }
    return $metadata
}

function Copy-Input {
    param([string] $Name, [string] $Seed)
    $caseRoot = Join-Path $ownedRoot $Name
    [void][IO.Directory]::CreateDirectory($caseRoot)
    $input = Join-Path $caseRoot 'input.dll'
    [IO.File]::Copy($Seed, $input, $false)
    return $input
}

function Write-WrongVersionRuntime {
    param([string] $Seed, [string] $Destination)
    $bytes = [IO.File]::ReadAllBytes($Seed)
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $pe = [Reflection.PortableExecutable.PEReader]::new($memory)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $rva = $pe.PEHeaders.CorHeader.MetadataDirectory.RelativeVirtualAddress
        $metadataFileOffset = $null
        if ($pe.PEHeaders.SectionHeaders.Length -gt 96) { throw 'Version mutation fixture exceeds its section count bound.' }
        $sectionClock = [Diagnostics.Stopwatch]::StartNew()
        foreach ($section in $pe.PEHeaders.SectionHeaders) {
            if ($sectionClock.Elapsed.TotalSeconds -ge 2) { throw 'Version mutation metadata budget expired.' }
            if ($rva -ge $section.VirtualAddress -and $rva -lt ($section.VirtualAddress + $section.SizeOfRawData)) {
                $metadataFileOffset = $section.PointerToRawData + $rva - $section.VirtualAddress
            }
        }
        if ($null -eq $metadataFileOffset) { throw 'Version mutation could not find the actual PE metadata section.' }
        $assemblyTableOffset = [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($metadata, [Reflection.Metadata.Ecma335.TableIndex]::Assembly)
        # ECMA-335 Assembly row: uint32 hash algorithm, then uint16 major/minor/build/revision.
        $majorOffset = $metadataFileOffset + $assemblyTableOffset + 4
        $oldMajor = [BitConverter]::ToUInt16($bytes, $majorOffset)
        $newMajor = [BitConverter]::GetBytes([UInt16]($oldMajor + 1))
        $bytes[$majorOffset] = $newMajor[0]
        $bytes[$majorOffset + 1] = $newMajor[1]
    }
    finally { $pe.Dispose(); $memory.Dispose() }
    [IO.File]::WriteAllBytes($Destination, $bytes)
}

function Write-LegacyTypedLir {
    param([string] $Destination)
    Assert-Budget
    $compilerDirectory = [IO.Path]::GetDirectoryName($CompilerPath)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $compilerDirectory 'RustSharp.Syntax.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $compilerDirectory 'RustSharp.CodeGen.IL.dll'))
    $instructions = [RustSharp.CodeGen.IL.ClrLirInstruction[]]@(
        [RustSharp.CodeGen.IL.ClrLirLoadInt32]::new(0),
        [RustSharp.CodeGen.IL.ClrLirReturn]::new()
    )
    $block = [RustSharp.CodeGen.IL.ClrLirBlock]::new('entry', $instructions)
    $method = [RustSharp.CodeGen.IL.ClrLirMethod]::new('Main', [RustSharp.CodeGen.IL.ClrLirType]::I32,
        [RustSharp.CodeGen.IL.ClrLirType[]]@(), [RustSharp.CodeGen.IL.ClrLirLocal[]]@(),
        [RustSharp.CodeGen.IL.ClrLirBlock[]]@($block), $null, $null, $null, $null)
    if (-not $method.Validate().IsValid) { throw 'The legacy typed LIR fixture failed actual production validation.' }
    $emit = ([RustSharp.CodeGen.IL.ClrLirAssemblyEmitter]).GetMethod('Emit', [Type[]]@(
        [RustSharp.CodeGen.IL.ClrLirMethod], [string], [RustSharp.CodeGen.IL.RustSharpMetadataDocument]
    ))
    if ($null -eq $emit) { throw 'The exact typed LIR Emit(method, name, metadataDocument) overload was not found.' }
    $generated = $emit.Invoke($null, [object[]]@($method, 'Legacy.TypedLir', $null))
    [IO.File]::WriteAllBytes($Destination, $generated.PeImage)
    [IO.File]::WriteAllText([IO.Path]::ChangeExtension($Destination, '.runtimeconfig.json'), $generated.RuntimeConfigJson, [Text.UTF8Encoding]::new($false))
    $lirText = (@('Main() -> I32', 'entry:', '  LoadInt32 0', '  Return') -join [char]10) + [char]10
    [IO.File]::WriteAllText([IO.Path]::ChangeExtension($Destination, '.clrlir.txt'), $lirText, [Text.UTF8Encoding]::new($false))
}

function Write-ConflictingReferencePe {
    param([string] $Seed, [string] $Destination)
    Assert-Budget
    $bytes = [IO.File]::ReadAllBytes($Seed)
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $pe = [Reflection.PortableExecutable.PEReader]::new($memory)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if ($metadata.AssemblyReferences.Count -gt 512) { throw 'Conflicting reference fixture exceeds its metadata row bound.' }
        $runtimeRows = [Collections.Generic.List[int]]::new()
        $rowClock = [Diagnostics.Stopwatch]::StartNew()
        foreach ($handle in $metadata.AssemblyReferences) {
            if ($rowClock.Elapsed.TotalSeconds -ge 2) { throw 'Conflicting reference fixture metadata deadline expired.' }
            $reference = $metadata.GetAssemblyReference($handle)
            if ($metadata.GetString($reference.Name) -ceq 'RustSharp.Runtime') {
                [void]$runtimeRows.Add([Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$handle))
            }
        }
        if ($runtimeRows.Count -lt 2) { throw 'The production PE must contain two Runtime AssemblyRef rows to exercise duplicate reconciliation.' }
        $rowSize = [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($metadata, [Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef)
        $tableOffset = [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($metadata, [Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef)
        $majorOffset = $pe.PEHeaders.MetadataStartOffset + $tableOffset + ($runtimeRows[1] - 1) * $rowSize
        $major = [BitConverter]::ToUInt16($bytes, $majorOffset)
        $replacement = [BitConverter]::GetBytes([UInt16]($major + 1))
        $bytes[$majorOffset] = $replacement[0]
        $bytes[$majorOffset + 1] = $replacement[1]
    }
    finally { $pe.Dispose(); $memory.Dispose() }
    [IO.File]::WriteAllBytes($Destination, $bytes)
}

try {
    $seedRoot = Join-Path $EvidenceDirectory 'generated'
    [void][IO.Directory]::CreateDirectory($seedRoot)
    $runtimeSeed = Join-Path $seedRoot 'runtime-required.dll'
    $legacySeed = Join-Path $seedRoot 'legacy.dll'
    $runtimeFile = Join-Path $seedRoot 'RustSharp.Runtime.dll'
    # Runtime input uses production source-to-PE compilation; the legacy fixture uses the actual typed LIR emitter.
    $null = Run-OwnedProcess $dotnet @($CompilerPath, 'compile', (Join-Path $root 'samples/safe-core.rs'), '--profile', 'safe-core-primitives-v1', '--output', $runtimeSeed) (Join-Path $EvidenceDirectory 'compile-runtime')
    Write-LegacyTypedLir $legacySeed
    if (-not [IO.File]::Exists($runtimeFile)) { throw 'Production compilation did not emit its required runtime sidecar.' }
    $runtimeHash = (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash

    $catalog = @(
        [pscustomobject]@{ Id='runtime-auto'; ExpectedSuccess=$true; Diagnostic=''; Mode='auto' },
        [pscustomobject]@{ Id='runtime-missing'; ExpectedSuccess=$false; Diagnostic='RSIL1001:'; Mode='missing' },
        [pscustomobject]@{ Id='runtime-corrupt'; ExpectedSuccess=$false; Diagnostic='RSIL1002:'; Mode='corrupt' },
        [pscustomobject]@{ Id='runtime-wrong-identity'; ExpectedSuccess=$false; Diagnostic='RSIL1003:'; Mode='identity' },
        [pscustomobject]@{ Id='runtime-wrong-version'; ExpectedSuccess=$false; Diagnostic='RSIL1003:'; Mode='version' },
        [pscustomobject]@{ Id='runtime-hash-mismatch'; ExpectedSuccess=$false; Diagnostic='RSIL1004:'; Mode='hash' },
        [pscustomobject]@{ Id='runtime-size-limit'; ExpectedSuccess=$false; Diagnostic='RSIL1002:'; Mode='size' },
        [pscustomobject]@{ Id='runtime-explicit'; ExpectedSuccess=$true; Diagnostic=''; Mode='explicit' },
        [pscustomobject]@{ Id='legacy-no-runtime'; ExpectedSuccess=$true; Diagnostic=''; Mode='legacy' },
        [pscustomobject]@{ Id='legacy-ignores-unreferenced-sidecar'; ExpectedSuccess=$true; Diagnostic=''; Mode='legacy-corrupt' }
    )
    # MaximumCases=1 is the required small-input trial before the fixed ten-case batch.
    for ($index = 0; $index -lt $MaximumCases -and $index -lt 10; $index++) {
        Assert-Budget
        $fixture = $catalog[$index]
        $isLegacy = $fixture.Mode.StartsWith('legacy', [StringComparison]::Ordinal)
        $seed = if ($isLegacy) { $legacySeed } else { $runtimeSeed }
        $input = Copy-Input $fixture.Id $seed
        $sidecar = Join-Path ([IO.Path]::GetDirectoryName($input)) 'RustSharp.Runtime.dll'
        $reportPath = Join-Path $EvidenceDirectory ($fixture.Id + '.json')
        $arguments = @('-NoLogo', '-NoProfile', '-File', $verifier, '-AssemblyPath', $input, '-EvidencePath', $reportPath,
            '-ToolWorkingDirectory', $ownedRoot, '-RuntimeVersion', $RuntimeVersion, '-TimeoutSeconds', '30')
        switch ($fixture.Mode) {
            'auto' { [IO.File]::Copy($runtimeFile, $sidecar); $arguments += @('-RuntimeReferenceSha256', $runtimeHash) }
            'missing' { }
            'corrupt' { [IO.File]::WriteAllBytes($sidecar, [byte[]]@(0,1,2,3)) }
            'identity' { [IO.File]::Copy($legacySeed, $sidecar) }
            'version' { Write-WrongVersionRuntime $runtimeFile $sidecar }
            'hash' { [IO.File]::Copy($runtimeFile, $sidecar); $arguments += @('-RuntimeReferenceSha256', ('0' * 64)) }
            'size' {
                $oversized = [IO.File]::Open($sidecar, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try { $oversized.SetLength(16MB + 1) } finally { $oversized.Dispose() }
            }
            'explicit' { $arguments += @('-AdditionalReferencePath', $runtimeFile) }
            'legacy' { }
            'legacy-corrupt' { [IO.File]::WriteAllBytes($sidecar, [byte[]]@(0,1,2,3)) }
        }
        $process = Run-OwnedProcess $pwsh $arguments (Join-Path $EvidenceDirectory ($fixture.Id + '.process')) $true
        if (-not [IO.File]::Exists($reportPath)) { throw ($fixture.Id + ' failed without verifier evidence.') }
        $evidence = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
        $expectedExit = if ($fixture.ExpectedSuccess) { 0 } else { 1 }
        $matches = $process.ExitCode -eq $expectedExit -and $evidence.Succeeded -eq $fixture.ExpectedSuccess
        $difference = $null
        $additionalChecks = @()
        if (-not $fixture.ExpectedSuccess) {
            $matches = $matches -and $evidence.Failure.StartsWith($fixture.Diagnostic, [StringComparison]::Ordinal) -and $null -eq $evidence.VerifyProcess
        }
        elseif ($isLegacy) {
            $matches = $matches -and -not $evidence.Verification.RuntimeReference.Declared -and $null -eq $evidence.Verification.RuntimeReference.Path
        }
        else {
            $runtime = $evidence.Verification.RuntimeReference
            $matches = $matches -and $runtime.Declared -and $runtime.Validated -and $runtime.Sha256 -ceq $runtimeHash -and $runtime.Sha256AfterVerification -ceq $runtimeHash
        }
        if ($fixture.Mode -eq 'version') {
            # This fixed version case also checks a second, bounded input with conflicting declared AssemblyRefs.
            $conflictingInput = Join-Path ([IO.Path]::GetDirectoryName($input)) 'conflicting-input.dll'
            Write-ConflictingReferencePe $runtimeSeed $conflictingInput
            $conflictingReport = Join-Path $EvidenceDirectory 'runtime-wrong-version-conflicting-pe.json'
            $conflictingArguments = @('-NoLogo', '-NoProfile', '-File', $verifier, '-AssemblyPath', $conflictingInput,
                '-EvidencePath', $conflictingReport, '-ToolWorkingDirectory', $ownedRoot, '-RuntimeVersion', $RuntimeVersion, '-TimeoutSeconds', '30')
            $conflictingProcess = Run-OwnedProcess $pwsh $conflictingArguments (Join-Path $EvidenceDirectory 'runtime-wrong-version-conflicting-pe.process') $true
            $conflictingEvidence = [IO.File]::ReadAllText($conflictingReport) | ConvertFrom-Json
            $conflictMatches = $conflictingProcess.ExitCode -eq 1 -and -not $conflictingEvidence.Succeeded -and
                $conflictingEvidence.Failure.StartsWith('RSIL1003: Input PE declares conflicting', [StringComparison]::Ordinal) -and $null -eq $conflictingEvidence.VerifyProcess
            $matches = $matches -and $conflictMatches
            $additionalChecks = @([ordered]@{
                id='conflicting-declared-runtime-versions'; passed=$conflictMatches; actualFailure=$conflictingEvidence.Failure
                inputSha256=(Get-FileHash -LiteralPath $conflictingInput -Algorithm SHA256).Hash
                verifierReport=[IO.Path]::GetFileName($conflictingReport)
                verifierReportSha256=(Get-FileHash -LiteralPath $conflictingReport -Algorithm SHA256).Hash
            })
        }
        if (-not $matches) { $difference = 'Actual verifier execution or stable preflight diagnostic differs from its fixed expectation: ' + $evidence.Failure }
        [void]$cases.Add([ordered]@{
            id=$fixture.Id; status=if ($matches) { 'passed' } else { 'failed' }; expectedSuccess=$fixture.ExpectedSuccess
            expectedDiagnostic=$fixture.Diagnostic; actualExitCode=$process.ExitCode; actualFailure=$evidence.Failure
            inputSha256=(Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash; verifierReport=[IO.Path]::GetFileName($reportPath)
            verifierReportSha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash; difference=$difference
            additionalChecks=$additionalChecks
        })
        Write-Host ($fixture.Id + ': ' + $(if ($matches) { 'PASS' } else { 'FAIL' }))
    }
}
catch { $failure = $_.Exception.Message }
finally {
    [Console]::remove_CancelKeyPress($cancelHandler)
    $resolvedOwned = [IO.Path]::GetFullPath($ownedRoot)
    if ($resolvedOwned.StartsWith($temporaryParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedOwned).StartsWith('rustsharp-ilverify-reference-', [StringComparison]::Ordinal)) {
        try { if ([IO.Directory]::Exists($resolvedOwned)) { Remove-Item -LiteralPath $resolvedOwned -Recurse -Force }; $cleanupComplete = -not [IO.Directory]::Exists($resolvedOwned) }
        catch { $failure = 'Owned fixture cleanup failed: ' + $_.Exception.Message }
    }
    else { $failure = 'Owned fixture cleanup refused because the resolved path escaped its verified parent.' }
}
$passed = @($cases | Where-Object { $_.status -eq 'passed' }).Count
$failed = @($cases | Where-Object { $_.status -eq 'failed' }).Count
$succeeded = $null -eq $failure -and $cases.Count -eq $MaximumCases -and $failed -eq 0 -and $cleanupComplete
$report = [ordered]@{
    schemaVersion=1; evidenceKind='p1-ilverify-runtime-reference-generated-pe'; leafId='P1-GATE.03'; candidateSha=$null
    startedAtUtc=$startedAt.ToString('O'); completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
    platform=[ordered]@{ runtimeIdentifier=[Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier; powershell=$PSVersionTable.PSVersion.ToString(); dotnet=$dotnet }
    compiler=[ordered]@{ path=$CompilerPath; sha256=(Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash }
    limits=[ordered]@{ maximumCases=10; selectedCases=$MaximumCases; deadlineSeconds=$DeadlineSeconds; timeoutSeconds=30; maximumOutputBytes=2097152; runtimeReferenceBytes=16777216 }
    summary=[ordered]@{ denominator=10; selected=$MaximumCases; executed=$cases.Count; passed=$passed; failed=$failed; skipped=0; notExecuted=10-$cases.Count; fullSuite=$MaximumCases -eq 10; succeeded=$succeeded }
    cases=@($cases); processes=@($suiteProcesses); cleanup=[ordered]@{ completed=$cleanupComplete; ownedFixture=$ownedRoot }; failure=$failure
}
$reportPath = Join-Path $EvidenceDirectory 'report.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Evidence: $reportPath"
if (-not $succeeded) { Write-Error ($failure ?? 'One or more runtime reference cases failed.'); exit 1 }
exit 0
