[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $WindowsReceipt,
    [Parameter(Mandatory)][string] $LinuxReceipt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,20}$')][string] $RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,6}$')][string] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][string] $NativeJobResult,
    [Parameter(Mandatory)][string] $SuiteJobResult,
    [Parameter(Mandatory)][string] $EvidencePath,
    [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow
$maximumSeconds = 480
$failures = [Collections.Generic.List[string]]::new()
$hosts = [Collections.Generic.List[object]]::new()
$matrix = $null
$tree = $null
$sourceNames = @(
    '.github/workflows/p1-expanded.yml', 'eng/Invoke-P1ProductionNativeEvidence.ps1', 'eng/Test-P1ProductionCiGate.ps1',
    'eng/Invoke-BoundedProcess.ps1', 'eng/Invoke-P1ReleaseBuild.ps1', 'eng/Get-P1SourceSnapshot.ps1', 'eng/P1SuiteEvidenceValidation.ps1',
    'tools/RustSharp.Conformance/Program.cs', 'tools/RustSharp.Conformance/P1SourcePackagePlatformRunner.cs',
    'tools/RustSharp.Conformance/P1SourcePackageEvidenceValidator.cs', 'tools/RustSharp.Conformance/P1BackendCoverageRunner.cs',
    'tools/RustSharp.Conformance/P1BackendCoverageEvidence.cs'
)
$assembly = 'tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll'
$manifest = 'tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json'
$fixed = [ordered]@{sourcePackages=19;sourceOriginalPe=39;backendFixtures=6;backendLocalCells=12;backendMatrixCells=24;legacySuiteChecks=15;legacySuiteReportsPerPlatform=7}

function Check-Budget([int] $Reserve = 0) {
    $CancellationToken.ThrowIfCancellationRequested()
    if ($clock.Elapsed.TotalSeconds -ge ($maximumSeconds - $Reserve)) { throw 'Production CI aggregate deadline expired.' }
}
function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Child-Path([string] $Relative, [switch] $Closeout) {
    $pathClock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $Closeout) { Check-Budget }
    Require ($Relative -cmatch '^[A-Za-z0-9_.\-/]+$' -and -not $Relative.StartsWith('/') -and -not $Relative.Contains('//')) 'Invalid relative artifact path.'
    $parts = @($Relative.Split('/'))
    Require ($parts.Count -le 32) 'Artifact path component bound exceeded.'
    $current = $root
    foreach ($part in $parts) {
        if ($pathClock.Elapsed.TotalSeconds -ge 5) { throw 'Path validation exceeded five seconds.' }
        if (-not $Closeout) { Check-Budget }
        Require ($part -cne '.' -and $part -cne '..' -and $part.Length -gt 0) 'Artifact traversal is forbidden.'
        $current = [IO.Path]::Combine($current,$part)
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            Require (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Redirected artifact paths are forbidden.'
        }
    }
    return $current
}
function File-Hash([string] $Relative, [long] $MaximumBytes = 134217728, [switch] $AllowEmpty) {
    $path = Child-Path $Relative
    Require ([IO.File]::Exists($path)) ('Required physical artifact missing: ' + $Relative)
    $length = ([IO.FileInfo]::new($path)).Length
    Require ($length -le $MaximumBytes -and ($AllowEmpty -or $length -gt 0)) ('Physical artifact byte bound failed: ' + $Relative)
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Check-Budget
    return $hash
}
function Read-Json([string] $Relative, [long] $MaximumBytes = 33554432) {
    $null = File-Hash $Relative $MaximumBytes
    return ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes((Child-Path $Relative)))
}
function Bound-Artifact($Binding, [string] $ExpectedPath, [long] $MaximumBytes = 33554432, [switch] $AllowEmpty) {
    Require ($Binding.repositoryPath -ceq $ExpectedPath -and $Binding.sha256 -cmatch '^[A-Fa-f0-9]{64}$') 'Receipt artifact identity/hash is invalid.'
    Require ((File-Hash $ExpectedPath $MaximumBytes -AllowEmpty:$AllowEmpty) -ieq $Binding.sha256) ('Transported receipt artifact bytes are stale: ' + $ExpectedPath)
}
function Same-Json($Left, $Right) {
    $a = [Text.Json.Nodes.JsonNode]::Parse(($Left | ConvertTo-Json -Depth 48 -Compress))
    $b = [Text.Json.Nodes.JsonNode]::Parse(($Right | ConvertTo-Json -Depth 48 -Compress))
    return [Text.Json.Nodes.JsonNode]::DeepEquals($a,$b)
}
function Check-Retained($Report, [string] $ReportPath, [int] $MaximumItems, [ref] $HostBytes) {
    $originalRoot = ([string]$Report.cleanup.retainedEvidenceDirectory).Replace('\','/').TrimEnd('/')
    $leaf = $originalRoot.Substring($originalRoot.LastIndexOf('/') + 1)
    Require ($leaf -cmatch '^[A-Za-z0-9-]{1,96}$') 'Invalid retained directory identity.'
    $sibling = $ReportPath.Substring(0,$ReportPath.LastIndexOf('/')) + '/' + $leaf
    $rows = @($Report.retainedArtifacts)
    Require ($rows.Count -ge 1 -and $rows.Count -le $MaximumItems) 'Retained artifact denominator/upper bound failed.'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $rows.Count -and $index -lt $MaximumItems; $index++) {
        Check-Budget
        $row = $rows[$index]
        $portable = ([string]$row.path).Replace('\','/')
        Require ($portable.StartsWith($originalRoot + '/', [StringComparison]::Ordinal) -and $row.sha256 -cmatch '^[A-Fa-f0-9]{64}$') 'Retained artifact escaped its original root or lost its hash.'
        $relative = $portable.Substring($originalRoot.Length + 1)
        Require ($seen.Add($relative)) 'Duplicate transported retained artifact identity.'
        $physical = $sibling + '/' + $relative
        $path = Child-Path $physical
        Require ([IO.File]::Exists($path)) 'Transported retained artifact is missing.'
        $HostBytes.Value += ([IO.FileInfo]::new($path)).Length
        Require ($HostBytes.Value -le 2147483648) 'Native host retained bytes exceeded 2 GiB.'
        Require ((File-Hash $physical) -ieq $row.sha256) 'Transported retained artifact bytes differ from native validation.'
        if (($index % 128) -eq 0) { Write-Host ('Physical retained artifact ' + $index + '/' + $rows.Count + ': ' + $ReportPath) }
    }
    return $rows.Count
}
function Check-Host([string] $ReceiptPath, [string] $Platform, [string] $Rid, [string] $RunnerOs) {
    Check-Budget
    $prefix = 'artifacts/p1-production/' + $Platform
    Require ($ReceiptPath -ceq ($prefix + '/native-receipt.json')) 'Receipt must occupy its fixed native directory.'
    $receipt = Read-Json $ReceiptPath 4194304
    Require ($receipt.schemaVersion -eq 1 -and $receipt.evidenceKind -ceq 'p1-production-native-ci-receipt' -and $receipt.candidateSha -ceq $CandidateSha -and
        $receipt.candidateTreeSha -ceq $tree -and $receipt.platformName -ceq $Platform -and $receipt.runtimeIdentifier -ceq $Rid) 'Native receipt identity/candidate/tree/RID mismatch.'
    Require ($receipt.ci.repository -ceq $Repository -and $receipt.ci.runId -ceq $RunId -and $receipt.ci.runAttempt -ceq $RunAttempt -and
        $receipt.ci.job -ceq 'production-native' -and $receipt.ci.workflowSha -ceq $CandidateSha -and $receipt.ci.githubSha -ceq $CandidateSha -and $receipt.ci.runnerOs -ceq $RunnerOs) 'Native receipt is outside this authenticated Actions run/attempt.'
    Require ($receipt.summary.satisfiesNativeGate -ceq $true -and $receipt.summary.status -ceq 'passed' -and @($receipt.failures).Count -eq 0) 'Native job did not close its production gate.'
    Require (Same-Json $receipt.fixedDenominators $fixed) 'Frozen source/backend/suite denominators changed.'
    $begin = [DateTimeOffset]::Parse($receipt.execution.startedAtUtc)
    $finish = [DateTimeOffset]::Parse($receipt.execution.finishedAtUtc)
    Require ($finish -ge $begin -and ($finish - $begin).TotalSeconds -lt 3900 -and $receipt.execution.maximumStages -eq 4 -and $receipt.execution.timeoutSeconds -eq 3900) 'Native receipt execution bounds are invalid.'
    $sources = @($receipt.sources)
    Require ($sources.Count -eq 12) 'All 12 production implementation sources must be retained.'
    $seenSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt 12; $index++) {
        Check-Budget
        $row = $sources[$index]
        Require ($sourceNames -ccontains $row.repositoryPath -and $seenSources.Add($row.repositoryPath) -and $row.retainedPath -ceq ($prefix + '/sources/' + $row.repositoryPath)) 'Native retained source inventory was reduced or substituted.'
        Require ((File-Hash $row.repositoryPath 1048576) -ieq $row.sha256 -and (File-Hash $row.retainedPath 1048576) -ieq $row.sha256) 'Native source bytes differ from the same-SHA aggregate checkout.'
    }
    $sourcePath = 'artifacts/p1-source-package/' + $Platform + '/source-package-v2.json'
    $backendPath = 'artifacts/p1-backend/' + $Platform + '/backend-coverage-v1.json'
    $buildPath = $prefix + '/release-build.json'
    $snapshotPath = $prefix + '/source-snapshot.json'
    Bound-Artifact $receipt.reports.sourcePackage $sourcePath
    Bound-Artifact $receipt.reports.backend $backendPath 16777216
    Bound-Artifact $receipt.reports.releaseBuild $buildPath 4194304
    Bound-Artifact $receipt.reports.sourceSnapshot $snapshotPath 4194304
    $source = Read-Json $sourcePath
    $backend = Read-Json $backendPath 16777216
    $build = Read-Json $buildPath 4194304
    $snapshot = Read-Json $snapshotPath 4194304
    Require ($source.schemaVersion -eq 2 -and $source.candidateSha -ceq $CandidateSha -and $source.candidateTreeSha -ceq $tree -and $source.targetRuntimeIdentifier -ceq $Rid -and
        $source.summary.denominator -eq 19 -and $source.summary.passed -eq 19 -and @($source.cases).Count -eq 19) 'Source-package report identity/denominator mismatch.'
    Require ($source.evidenceKind -ceq 'p1-source-package-original-pe-coreclr-ilverify-native-aot' -and $source.profile -ceq 'p1-source-packages-v1' -and
        $source.manifestSha256 -ceq '72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8' -and
        $source.summary.failed -eq 0 -and $source.summary.blocked -eq 0 -and $source.summary.notExecuted -eq 0 -and
        $source.summary.maximumSelectedCases -eq 19 -and $source.summary.succeeded -ceq $true -and $source.cleanup.completed -ceq $true) 'Frozen complete source-package manifest/summary/cleanup is required.'
    $originalPeCount = 0
    for ($caseIndex = 0; $caseIndex -lt 19; $caseIndex++) {
        Check-Budget
        $case = @($source.cases)[$caseIndex]
        Require ($case.status -ceq 'passed' -and $null -ne $case.producerIlVerify -and $null -ne $case.consumerIlVerify) 'Original producer/consumer PE verification was omitted.'
        $originalPeCount += 2
        if ($null -ne $case.PSObject.Properties['wrapperIlVerify']) { Require ($null -ne $case.wrapperIlVerify) 'Original wrapper PE verification was omitted.'; $originalPeCount++ }
    }
    Require ($originalPeCount -eq 39) 'The source package original-PE verification denominator changed.'
    Require ($backend.candidateSha -ceq $CandidateSha -and $backend.candidateTreeSha -ceq $tree -and $backend.targetRuntimeIdentifier -ceq $Rid) 'Backend report native identity mismatch.'
    Require ($build.sdkVersion -ceq '10.0.401' -and $build.succeeded -ceq $true -and $build.summary.warnings -eq 0 -and $build.summary.errors -eq 0 -and
        $build.candidateSha -ceq $CandidateSha -and $build.sourceProvenance.candidateTreeSha -ceq $tree -and $snapshot.candidateSha -ceq $CandidateSha -and $snapshot.treeSha -ceq $tree -and $snapshot.verified -ceq $true) 'Native source/fresh build provenance mismatch.'
    Require ((Same-Json $source.sourceSnapshot.raw $snapshot) -and (Same-Json $source.releaseBuild.raw $build) -and
        (Same-Json $backend.sourceSnapshotRaw $snapshot) -and (Same-Json $backend.releaseBuildRaw $build)) 'Embedded native input reports differ from retained original bytes.'
    Require ($receipt.implementation.retainedRunnerPath -ceq ($prefix + '/RustSharp.Conformance.dll') -and $receipt.implementation.retainedDotnetPath -ceq ($prefix + '/dotnet-host')) 'Retained native implementation paths changed.'
    Require ((File-Hash $receipt.implementation.retainedRunnerPath) -ieq $receipt.implementation.runnerSha256 -and
        (File-Hash $receipt.implementation.retainedDotnetPath) -ieq $receipt.implementation.dotnetSha256) 'Retained native runner/dotnet executable hashes are stale.'
    foreach ($report in @($source,$backend)) {
        Check-Budget
        $runner = @($report.implementationInputs | Where-Object { $_.name -ceq 'RustSharp.Conformance.dll' })
        Require ($runner.Count -eq 1 -and $runner[0].loadedAssembly.sha256 -ieq $receipt.implementation.runnerSha256 -and
            $runner[0].retainedAssembly.sha256 -ieq $receipt.implementation.runnerSha256) 'Native production validator runner differs from the executed implementation.'
    }
    $expectedArguments = @(
        ,@($assembly,'--p1-source-package-candidate',$manifest,$sourcePath,'19',$CandidateSha,$snapshotPath,$buildPath)
        ,@($assembly,'--validate-p1-source-package-candidate',$sourcePath,$CandidateSha,$tree,$Rid)
        ,@($assembly,'--p1-backend-coverage-candidate',$backendPath,$Rid,'6',$CandidateSha,$snapshotPath,$buildPath)
        ,@($assembly,'--validate-p1-backend-native',$backendPath,$CandidateSha,$tree,$Rid)
    )
    $stageNames = @('source-run','source-validate','backend-run','backend-validate')
    $timeouts = @(1240,130,1220,250)
    $stages = @($receipt.stages)
    Require ($stages.Count -eq 4) 'Native receipt requires all four production execution/validation stages.'
    $ownedParent = $null
    $previousFinish = $begin
    for ($index = 0; $index -lt 4; $index++) {
        Check-Budget
        $stage = $stages[$index]
        $capture = $prefix + '/' + $stageNames[$index]
        Require ($stage.name -ceq $stageNames[$index] -and $stage.status -ceq 'passed' -and -not $stage.error -and $stage.timeoutSeconds -eq $timeouts[$index] -and
            $stage.dotnetSha256 -ieq $receipt.implementation.dotnetSha256 -and (Same-Json @($stage.arguments) $expectedArguments[$index])) 'Native production stage identity/arguments/bounds changed.'
        Bound-Artifact $stage.process ($capture + '.process.json') 65536
        Bound-Artifact $stage.stdout ($capture + '.stdout.log') 16777216 -AllowEmpty
        Bound-Artifact $stage.stderr ($capture + '.stderr.log') 16777216 -AllowEmpty
        $process = Read-Json ($capture + '.process.json') 65536
        $processBegin = [DateTimeOffset]::Parse($process.StartedAt)
        $processFinish = [DateTimeOffset]::Parse($process.FinishedAt)
        Require ($process.Pid -gt 0 -and $process.ParentPid -gt 0 -and $process.CleanupComplete -ceq $true -and $process.ExitCode -eq 0 -and -not $process.Failure -and
            $process.FilePath -ceq $stage.filePath -and (Same-Json @($process.Arguments) @($stage.arguments)) -and
            $process.TimeoutSeconds -eq $timeouts[$index] -and $process.MaximumAttempts -eq 1 -and
            $processBegin -ge $previousFinish -and $processFinish -ge $processBegin -and $processFinish -le $finish -and
            ($processFinish - $processBegin).TotalSeconds -le ($timeouts[$index] + 10)) 'Native process ownership/exit/cleanup/chronology is incomplete.'
        if ($null -eq $ownedParent) { $ownedParent = $process.ParentPid }
        Require ($process.ParentPid -eq $ownedParent) 'Native stages do not share their owned orchestration parent.'
        $previousFinish = $processFinish
        if ($index -eq 1 -or $index -eq 3) {
            $proof = Read-Json ($capture + '.stdout.log') 1048576
            $reportBinding = if ($index -eq 1) { $receipt.reports.sourcePackage } else { $receipt.reports.backend }
            Require ($stage.reportSha256 -ieq $reportBinding.sha256 -and $proof.Valid -ceq $true -and $proof.ArtifactContentVerified -ceq $true -and @($proof.Errors).Count -eq 0) 'Actual native artifact validator proof/report hash is incomplete.'
            if ($index -eq 1) { Require ($proof.SatisfiesGate -ceq $true -and $stage.gate -ceq 'SatisfiesGate') 'Actual native source-package gate failed.' }
            else { Require ($proof.SatisfiesNativeGate -ceq $true -and $proof.ClosedCells -eq 12 -and $stage.gate -ceq 'SatisfiesNativeGate') 'Actual native backend gate failed.' }
        }
    }
    [long]$physicalBytes = 0
    $sourceCount = Check-Retained $source $sourcePath 1024 ([ref]$physicalBytes)
    $backendCount = Check-Retained $backend $backendPath 192 ([ref]$physicalBytes)
    return [ordered]@{platformName=$Platform;runtimeIdentifier=$Rid;receiptPath=$ReceiptPath;receiptSha256=(File-Hash $ReceiptPath 4194304);sourceRetainedArtifacts=$sourceCount;backendRetainedArtifacts=$backendCount;retainedBytes=$physicalBytes;nativeGateVerified=$true}
}

try {
    Require ($env:GITHUB_ACTIONS -ceq 'true' -and $env:GITHUB_JOB -ceq 'production-gate' -and $env:GITHUB_SHA -ceq $CandidateSha -and
        $env:GITHUB_WORKFLOW_SHA -ceq $CandidateSha -and $env:GITHUB_RUN_ID -ceq $RunId -and $env:GITHUB_RUN_ATTEMPT -ceq $RunAttempt -and $env:GITHUB_REPOSITORY -ceq $Repository) 'Aggregate gate requires this authenticated same-SHA Actions context.'
    Require ($NativeJobResult -ceq 'success' -and $SuiteJobResult -ceq 'success' -and
        $env:NATIVE_JOB_RESULT -ceq $NativeJobResult -and $env:SUITE_JOB_RESULT -ceq $SuiteJobResult) 'Both trusted upstream job results must match the successful workflow needs context.'
    $freshBuild = Read-Json 'artifacts/p1-candidate/production-aggregate-release-build.json' 4194304
    Require ($freshBuild.sdkVersion -ceq '10.0.401') 'Aggregate validator requires a fresh SDK401 Release build.'
    $buildErrors = @(Test-P1BuildEvidence $freshBuild $CandidateSha 'linux-x64' $root)
    Require ($buildErrors.Count -eq 0) ('Fresh aggregate physical build binding failed: ' + ($buildErrors -join '; '))
    $tree = $freshBuild.sourceProvenance.candidateTreeSha
    Require ($tree -cmatch '^[a-fA-F0-9]{40}$') 'Aggregate candidate tree is missing.'
    $hosts.Add((Check-Host $WindowsReceipt 'windows-x64' 'win-x64' 'Windows'))
    $hosts.Add((Check-Host $LinuxReceipt 'linux-x64' 'linux-x64' 'Linux'))
    Check-Budget 255
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $matrixArguments = @($assembly,'--validate-p1-backend-matrix','artifacts/p1-backend/windows-x64/backend-coverage-v1.json','artifacts/p1-backend/linux-x64/backend-coverage-v1.json',$CandidateSha,$tree)
    $prefix = 'artifacts/p1-candidate/production-matrix-validation'
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList $matrixArguments -WorkingDirectory $root -TimeoutSeconds 250 -CapturePrefix $prefix
    $matrix = Read-Json ($prefix + '.stdout.log') 1048576
    $matrixProcess = Read-Json ($prefix + '.process.json') 65536
    Require ($matrix.Valid -ceq $true -and $matrix.ArtifactContentVerified -ceq $true -and $matrix.SatisfiesMatrixGate -ceq $true -and $matrix.ClosedCells -eq 24 -and @($matrix.Errors).Count -eq 0 -and
        $matrixProcess.ExitCode -eq 0 -and $matrixProcess.CleanupComplete -ceq $true -and -not $matrixProcess.Failure -and $matrixProcess.ParentPid -eq $PID) 'Actual transported backend matrix validator did not close all 24 cells.'
    Check-Budget
} catch { $failures.Add($_.Exception.Message) }

$closed = $failures.Count -eq 0 -and $hosts.Count -eq 2 -and $null -ne $matrix -and $clock.Elapsed.TotalSeconds -lt $maximumSeconds
$evidence = [ordered]@{
    schemaVersion=1;evidenceKind='p1-production-ci-gate';candidateSha=$CandidateSha;candidateTreeSha=$tree
    ci=[ordered]@{repository=$Repository;runId=$RunId;runAttempt=$RunAttempt;workflowSha=$env:GITHUB_WORKFLOW_SHA;nativeJobResult=$NativeJobResult;suiteJobResult=$SuiteJobResult}
    fixedDenominators=$fixed;nativeHosts=@($hosts.ToArray());backendMatrix=$matrix
    summary=[ordered]@{status=if($closed){'passed'}else{'blocked'};satisfiesGate=$closed;sourceNativePlatforms=if($closed){2}else{0};sourcePackagesPerPlatform=19;sourceOriginalPePerPlatform=39;backendClosedCells=if($closed){24}else{0};fullP1Closure=$false}
    execution=[ordered]@{startedAtUtc=$started.ToString('O');finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');maximumNativeHosts=2;maximumSourceArtifactsPerHost=1024;maximumBackendArtifactsPerHost=192;maximumRetainedBytesPerHost=2147483648;timeoutSeconds=$maximumSeconds}
    failures=@($failures.ToArray())
}
$destination = Child-Path $EvidencePath -Closeout
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
Require (-not [IO.File]::Exists($destination)) 'Existing aggregate evidence is preserved.'
$staging = $destination + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
try { [IO.File]::WriteAllText($staging,($evidence | ConvertTo-Json -Depth 48)); [IO.File]::Move($staging,$destination,$false) }
finally { if ([IO.File]::Exists($staging)) { [IO.File]::Delete($staging) } }
Write-Host ('Production CI gate: ' + $closed + '; evidence=' + $EvidencePath)
if (-not $closed) { exit 1 }
exit 0
