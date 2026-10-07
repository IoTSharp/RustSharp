[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string] $PlatformName,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,20}$')][string] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,6}$')][string] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$rid = if ($PlatformName -ceq 'windows-x64') { 'win-x64' } else { 'linux-x64' }
if (($rid -ceq 'win-x64') -ne $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'An independent native x64 runner is required.' }
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_SHA -cne $CandidateSha -or $env:GITHUB_RUN_ID -cne $RunId -or
    $env:GITHUB_RUN_ATTEMPT -cne $RunAttempt -or $env:GITHUB_REPOSITORY -cne $Repository -or $env:GITHUB_JOB -cne 'production-native') { throw 'Native evidence requires the expected authenticated Actions job context.' }

$maximumSeconds = 3900
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow
$relativeDirectory = 'artifacts/p1-production/' + $PlatformName
$directory = [IO.Path]::GetFullPath($relativeDirectory, $root)
$receiptPath = Join-Path $directory 'native-receipt.json'
$sourceReport = 'artifacts/p1-source-package/' + $PlatformName + '/source-package-v2.json'
$backendReport = 'artifacts/p1-backend/' + $PlatformName + '/backend-coverage-v1.json'
$buildPath = $relativeDirectory + '/release-build.json'
$snapshotPath = $relativeDirectory + '/source-snapshot.json'
$assembly = 'tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll'
$manifest = 'tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json'
$sourceNames = @(
    '.github/workflows/p1-expanded.yml', 'eng/Invoke-P1ProductionNativeEvidence.ps1', 'eng/Test-P1ProductionCiGate.ps1',
    'eng/Invoke-BoundedProcess.ps1', 'eng/Invoke-P1ReleaseBuild.ps1', 'eng/Get-P1SourceSnapshot.ps1', 'eng/P1SuiteEvidenceValidation.ps1',
    'tools/RustSharp.Conformance/Program.cs', 'tools/RustSharp.Conformance/P1SourcePackagePlatformRunner.cs',
    'tools/RustSharp.Conformance/P1SourcePackageEvidenceValidator.cs', 'tools/RustSharp.Conformance/P1BackendCoverageRunner.cs',
    'tools/RustSharp.Conformance/P1BackendCoverageEvidence.cs'
)
$sources = [Collections.Generic.List[object]]::new()
$stages = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
$reports = [ordered]@{}
$tree = $null
$implementation = $null
$ready = $false
$dotnet = ''
$pwsh = ''
$oldSdk = $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION
$oldDotnet = $env:RUSTSHARP_P1_DOTNET_PATH
$oldPwsh = $env:RUSTSHARP_P1_PWSH_PATH

function Check-Budget([int] $Reserve = 0) {
    $CancellationToken.ThrowIfCancellationRequested()
    if ($clock.Elapsed.TotalSeconds -ge ($maximumSeconds - $Reserve)) { throw 'Native production orchestration deadline expired.' }
}
function Child-Path([string] $Relative) {
    $full = [IO.Path]::GetFullPath($Relative, $root)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $full.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'A task path escaped the repository.' }
    return $full
}
function File-Binding([string] $Relative, [long] $MaximumBytes = 33554432) {
    Check-Budget
    $full = Child-Path $Relative
    if (-not [IO.File]::Exists($full) -or ([IO.FileInfo]::new($full)).Length -gt $MaximumBytes) { throw ('Required artifact missing/oversized: ' + $Relative) }
    return [ordered]@{ repositoryPath=$Relative.Replace('\','/'); sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
}
function Read-Json([string] $Relative, [long] $MaximumBytes = 33554432) {
    $null = File-Binding $Relative $MaximumBytes
    return ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes((Child-Path $Relative)))
}
function Invoke-Stage([string] $Name, [string[]] $Arguments, [int] $Timeout, [string] $Report = '', [string] $Gate = '') {
    Check-Budget 10
    $prefix = $relativeDirectory + '/' + $Name
    $stage = [ordered]@{ name=$Name; arguments=$Arguments; filePath=$dotnet; dotnetSha256=if($null -ne $implementation){$implementation.dotnetSha256}else{$null}; timeoutSeconds=$Timeout; process=$null; stdout=$null; stderr=$null; reportSha256=$null; gate=$Gate; status='blocked'; error=$null }
    $stages.Add($stage)
    Write-Host ('P1 production stage ' + $stages.Count + '/4: ' + $Name)
    try {
        if (-not $ready) { throw 'Fresh candidate build/preflight did not complete.' }
        $remaining = [Math]::Max(1, [Math]::Floor($maximumSeconds - $clock.Elapsed.TotalSeconds - 10))
        & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList $Arguments -WorkingDirectory $root -TimeoutSeconds ([Math]::Min($Timeout,$remaining)) -CapturePrefix $prefix
        $process = Read-Json ($prefix + '.process.json') 65536
        if ($process.ExitCode -ne 0 -or $process.CleanupComplete -ne $true -or $process.Failure -or $process.Pid -le 0 -or $process.ParentPid -ne $PID -or
            [DateTimeOffset]::Parse($process.FinishedAt) -lt [DateTimeOffset]::Parse($process.StartedAt)) { throw 'Owned process exit/identity/cleanup was incomplete.' }
        if ($Gate) {
            $proof = Read-Json ($prefix + '.stdout.log') 1048576
            if ($proof.Valid -ne $true -or $proof.ArtifactContentVerified -ne $true -or $proof.$Gate -ne $true) { throw 'The production artifact validator did not close its gate.' }
            if ($Gate -ceq 'SatisfiesNativeGate' -and $proof.ClosedCells -ne 12) { throw 'The backend local denominator was reduced.' }
            $stage.reportSha256 = (File-Binding $Report).sha256
        }
        $stage.status = 'passed'
    } catch { $stage.error=$_.Exception.Message; $failures.Add($Name + ': ' + $stage.error) }
    finally {
        foreach ($suffix in @('process.json','stdout.log','stderr.log')) {
            Check-Budget
            if ([IO.File]::Exists((Child-Path ($prefix + '.' + $suffix)))) {
                $name = switch ($suffix) { 'process.json' { 'process' } 'stdout.log' { 'stdout' } 'stderr.log' { 'stderr' } }
                $stage[$name] = File-Binding ($prefix + '.' + $suffix)
            }
        }
    }
}

try {
    if ([IO.Directory]::Exists($directory) -or [IO.File]::Exists((Child-Path $sourceReport)) -or [IO.File]::Exists((Child-Path $backendReport))) { throw 'Production CI destinations must be fresh; existing evidence is preserved.' }
    $null = [IO.Directory]::CreateDirectory($directory)
    $pwsh = if ($IsWindows) { 'C:\Program Files\PowerShell\7\pwsh.exe' } else { (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION = '10.0.401'
    $env:RUSTSHARP_P1_DOTNET_PATH = $dotnet
    $env:RUSTSHARP_P1_PWSH_PATH = $pwsh
    # 12 explicit files, no recursive discovery. Source bytes must be retained before execution.
    $sourceClock = [Diagnostics.Stopwatch]::StartNew()
    foreach ($name in $sourceNames) {
        Check-Budget
        if ($sources.Count -ge 12 -or $sourceClock.Elapsed.TotalSeconds -ge 30) { throw 'Source retention item/time limit exceeded.' }
        $binding = File-Binding $name 1048576
        $retained = $relativeDirectory + '/sources/' + $name
        $target = Child-Path $retained
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Child-Path $name),$target,$false)
        if ((File-Binding $retained 1048576).sha256 -cne $binding.sha256) { throw 'Retained source differs from candidate checkout bytes.' }
        $sources.Add([ordered]@{repositoryPath=$name;retainedPath=$retained;sha256=$binding.sha256})
    }
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $pwsh -ArgumentList @('-NoLogo','-NoProfile','-File','eng/Invoke-P1ReleaseBuild.ps1','-CandidateSha',$CandidateSha,'-SdkVersion','10.0.401','-ReportPath',$buildPath) -WorkingDirectory $root -TimeoutSeconds 750 -CapturePrefix ($relativeDirectory + '/release-launch')
    Check-Budget
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $pwsh -ArgumentList @('-NoLogo','-NoProfile','-File','eng/Get-P1SourceSnapshot.ps1','-CandidateSha',$CandidateSha,'-EvidencePath',$snapshotPath,'-DeadlineSeconds','90') -WorkingDirectory $root -TimeoutSeconds 100 -CapturePrefix ($relativeDirectory + '/snapshot-launch')
    $snapshot = Read-Json $snapshotPath 4194304
    $build = Read-Json $buildPath 4194304
    if ($snapshot.candidateSha -cne $CandidateSha -or $snapshot.verified -ne $true -or $snapshot.treeSha -cnotmatch '^[a-fA-F0-9]{40}$' -or
        $build.candidateSha -cne $CandidateSha -or $build.sdkVersion -cne '10.0.401' -or $build.succeeded -ne $true -or $build.summary.warnings -ne 0 -or $build.summary.errors -ne 0) { throw 'A verified same-SHA source snapshot and warning-free SDK401 Release build are required.' }
    $tree = $snapshot.treeSha
    $runner = File-Binding $assembly
    $retainedRunner = $relativeDirectory + '/RustSharp.Conformance.dll'
    $retainedDotnet = $relativeDirectory + '/dotnet-host'
    [IO.File]::Copy((Child-Path $assembly),(Child-Path $retainedRunner),$false)
    [IO.File]::Copy($dotnet,(Child-Path $retainedDotnet),$false)
    $implementation = [ordered]@{ runnerSha256=$runner.sha256; retainedRunnerPath=$retainedRunner; dotnetSha256=(File-Binding $retainedDotnet).sha256; retainedDotnetPath=$retainedDotnet }
    $ready = $true
} catch { $failures.Add('candidate-preflight: ' + $_.Exception.Message) }

try {
    # Fixed four invocations: 1,240 + 130 + 1,220 + 250 = 2,840 seconds.
    # Source indexing/cleanup gets 40 seconds after its 1,200-second suite bound.
    # Preflight reserves 750 build + 100 snapshot + 30 source retention; total 3,720, within 3,900.
    Invoke-Stage 'source-run' @($assembly,'--p1-source-package-candidate',$manifest,$sourceReport,'19',$CandidateSha,$snapshotPath,$buildPath) 1240
    Invoke-Stage 'source-validate' @($assembly,'--validate-p1-source-package-candidate',$sourceReport,$CandidateSha,[string]$tree,$rid) 130 $sourceReport 'SatisfiesGate'
    Invoke-Stage 'backend-run' @($assembly,'--p1-backend-coverage-candidate',$backendReport,$rid,'6',$CandidateSha,$snapshotPath,$buildPath) 1220
    Invoke-Stage 'backend-validate' @($assembly,'--validate-p1-backend-native',$backendReport,$CandidateSha,[string]$tree,$rid) 250 $backendReport 'SatisfiesNativeGate'
} catch { $failures.Add('orchestration: ' + $_.Exception.Message) }
finally {
    $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION=$oldSdk
    $env:RUSTSHARP_P1_DOTNET_PATH=$oldDotnet
    $env:RUSTSHARP_P1_PWSH_PATH=$oldPwsh
}

foreach ($entry in @(@{name='sourcePackage';path=$sourceReport},@{name='backend';path=$backendReport},@{name='releaseBuild';path=$buildPath},@{name='sourceSnapshot';path=$snapshotPath})) {
    try { $reports[$entry.name] = File-Binding $entry.path } catch { $reports[$entry.name]=$null; $failures.Add($_.Exception.Message) }
}
if ($ready) {
    try {
        if ((File-Binding $assembly).sha256 -cne $implementation.runnerSha256 -or
            (File-Binding $implementation.retainedRunnerPath).sha256 -cne $implementation.runnerSha256 -or
            (File-Binding $implementation.retainedDotnetPath).sha256 -cne $implementation.dotnetSha256) { throw 'Executed/retained validator implementation bytes changed.' }
        if ($stages.Count -ne 4 -or $stages[1].reportSha256 -cne $reports.sourcePackage.sha256 -or
            $stages[3].reportSha256 -cne $reports.backend.sha256) { throw 'Validated report bytes changed before CI receipt publication.' }
    } catch { $failures.Add($_.Exception.Message) }
}
$closed = $failures.Count -eq 0 -and $ready -and $stages.Count -eq 4 -and @($stages | Where-Object { $_.status -cne 'passed' }).Count -eq 0 -and $clock.Elapsed.TotalSeconds -lt $maximumSeconds
$receipt = [ordered]@{
    schemaVersion=1; evidenceKind='p1-production-native-ci-receipt'; candidateSha=$CandidateSha; candidateTreeSha=$tree; platformName=$PlatformName; runtimeIdentifier=$rid
    ci=[ordered]@{repository=$Repository;runId=$RunId;runAttempt=$RunAttempt;job=$env:GITHUB_JOB;workflowSha=$env:GITHUB_WORKFLOW_SHA;githubSha=$env:GITHUB_SHA;runnerOs=$env:RUNNER_OS}
    fixedDenominators=[ordered]@{sourcePackages=19;sourceOriginalPe=39;backendFixtures=6;backendLocalCells=12;backendMatrixCells=24;legacySuiteChecks=15;legacySuiteReportsPerPlatform=7}
    reports=$reports; implementation=$implementation; sources=@($sources.ToArray()); stages=@($stages.ToArray())
    summary=[ordered]@{status=if($closed){'passed'}else{'blocked'};satisfiesNativeGate=$closed}
    execution=[ordered]@{startedAtUtc=$started.ToString('O');finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');maximumStages=4;timeoutSeconds=$maximumSeconds}
    failures=@($failures.ToArray())
}
if (-not [IO.Directory]::Exists($directory)) { throw 'The exclusive receipt directory was not created; no evidence was overwritten.' }
$staging = $receiptPath + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
try { [IO.File]::WriteAllText($staging,($receipt | ConvertTo-Json -Depth 32)); [IO.File]::Move($staging,$receiptPath,$false) }
finally { if ([IO.File]::Exists($staging)) { [IO.File]::Delete($staging) } }
Write-Host ('Native production gate: ' + $closed + '; receipt=' + $receiptPath)
if (-not $closed) { exit 1 }
exit 0
