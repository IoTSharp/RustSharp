[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $ReleaseBuildPath,
    [string] $ReportDirectory='artifacts/p2-cargo-features',
    [ValidateRange(30,300)][int] $DeadlineSeconds=180
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clock=[Diagnostics.Stopwatch]::StartNew()
$directory=[IO.Path]::GetFullPath($ReportDirectory,$root)
if (-not $directory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)) { throw 'Cargo evidence must stay inside the repository.' }
$null=[IO.Directory]::CreateDirectory($directory)
$manifestPath='tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json'
$mapPath='tools/RustSharp.Conformance/fixtures/p2-cargo-v1-feature-cases.json'
$manifestBytes=[IO.File]::ReadAllBytes((Join-Path $root $manifestPath))
$mapBytes=[IO.File]::ReadAllBytes((Join-Path $root $mapPath))
$manifest=ConvertFrom-P1StrictJson $manifestBytes
$map=ConvertFrom-P1StrictJson $mapBytes
$expectedIds=@($manifest.cases | Where-Object { $_.leafId -ceq 'P2-04.03' } | ForEach-Object { $_.id })
if ($expectedIds.Count -ne 11 -or $map.denominator -ne 11 -or $map.cases.Count -ne 11 -or
    $map.leafId -cne 'P2-04.03' -or (@($map.cases.id) -join "`n") -cne ($expectedIds -join "`n")) { throw 'Cargo fixed 11-case manifest binding differs.' }
$buildFullPath=[IO.Path]::GetFullPath($ReleaseBuildPath,$root)
if (-not $buildFullPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)) { throw 'Cargo build evidence must stay inside the repository.' }
$buildBytes=[IO.File]::ReadAllBytes($buildFullPath)
$build=ConvertFrom-P1StrictJson $buildBytes
$buildErrors=@(Test-P1BuildEvidence $build $CandidateSha '' $root)
if ($buildErrors.Count -ne 0) { throw ('Cargo fresh build binding failed: '+($buildErrors -join '; ')) }
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$harnessPath=Join-Path $directory 'P2-04.03.harness.json'
$prefix=Join-Path $directory 'P2-04.03-run'
$arguments=@('tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll','--filter','P2 cargo features',
    '--timeout','30','--deadline',[string]$DeadlineSeconds,'--report',$harnessPath,'--candidate-sha',$CandidateSha)
$failure=$null; $harness=$null; $process=$null
try {
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $root -TimeoutSeconds ($DeadlineSeconds+10) -CapturePrefix $prefix -MaximumOutputBytes 4MB
    $harness=ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes($harnessPath))
    if ($harness.candidateSha -cne $CandidateSha -or $harness.buildConfiguration -cne 'Release' -or
        $harness.assemblySha256 -cne $build.testsAssemblySha256 -or $harness.cleanupComplete -cne $true -or
        $harness.sourceProvenance.candidateMatchesWorkingTree -cne $true -or $harness.sourceProvenance.candidateTreeSha -cne $build.sourceProvenance.candidateTreeSha -or
        $harness.harnessError -or $harness.cancelled -or $harness.deadlineExpired -or $harness.cases.Count -ne 11 -or
        (@($harness.cases.id) -join "`n") -cne (@($map.cases.testName) -join "`n")) { throw 'Cargo actual execution IDs/build/source binding differs.' }
    foreach ($field in @('selected','executed','passed')) { if ($harness.summary.$field -ne 11) { throw 'Cargo execution must close all 11 cases.' } }
    foreach ($field in @('failed','skipped','notExecuted')) { if ($harness.summary.$field -ne 0) { throw 'Cargo execution must have no failures/skips/unexecuted cases.' } }
    foreach ($case in $harness.cases) {
        if ($case.status -cne 'passed' -or $case.error -or $null -eq $case.process -or
            $case.process.exitCode -ne 0 -or $case.process.termination -ne 0 -or $case.process.succeeded -cne $true -or
            $case.process.outputTruncated -or $case.process.outputReadTimedOut -or $case.process.outputDrainTimedOut -or
            $case.process.outputReadLimitReached -or $case.process.processTreeCleanupIncomplete -or
            $case.process.startedProcess.processId -le 0 -or $case.process.startedProcess.parentProcessId -le 0) {
            throw ('Cargo case lacks successful isolated execution: '+$case.id)
        }
    }
    if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds+120) { throw 'Cargo evidence orchestration deadline exceeded.' }
} catch { $failure=$_.Exception.Message }
finally {
    $processPath=$prefix+'.process.json'
    if ([IO.File]::Exists($processPath)) { $process=ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes($processPath)) }
    $passed=$null -eq $failure -and $null -ne $process -and $process.ExitCode -eq 0 -and $process.CleanupComplete -eq $true
    $report=[ordered]@{
        schemaVersion=1; evidenceKind='p2-cargo-feature-execution';leafId='P2-04.03';profile='cargo-v1';candidateSha=$CandidateSha
        manifestPath=$manifestPath;manifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes))
        caseMapPath=$mapPath;caseMapSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($mapBytes))
        scope='strict-load-v1-feature-resolution-filesystem-execution';fullP2Closure=$false;implementationCasesExecuted=if($harness){$harness.summary.executed}else{0}
        summary=@{status=if($passed){'passed'}else{'failed'};denominator=11;passed=if($passed){11}else{0};failed=if($passed){0}else{11};skipped=0}
        runtimeIdentifier=if($harness){$harness.runtimeIdentifier}else{$null};tools=@{sdkVersion=$build.sdkVersion;powerShellVersion=$PSVersionTable.PSVersion.ToString();dotnetPath=$dotnet}
        command=@{executable=$dotnet;arguments=$arguments;workingDirectory=$root};bounds=@{maximumTests=11;caseTimeoutSeconds=30;deadlineSeconds=$DeadlineSeconds;orchestrationDeadlineSeconds=$DeadlineSeconds+120}
        process=$process;cleanupComplete=$null -ne $process -and $process.CleanupComplete;sourceProvenance=if($harness){$harness.sourceProvenance}else{$null}
        freshBuild=@{path=[IO.Path]::GetRelativePath($root,$buildFullPath).Replace('\','/');sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buildBytes))}
        harness=@{path=[IO.Path]::GetRelativePath($root,$harnessPath).Replace('\','/');sha256=if([IO.File]::Exists($harnessPath)){(Get-FileHash -LiteralPath $harnessPath -Algorithm SHA256).Hash}else{$null}}
        failureReasons=@($failure | Where-Object {$null -ne $_})
    }
    [IO.File]::WriteAllText((Join-Path $directory 'P2-04.03.json'),($report|ConvertTo-Json -Depth 24).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw ($failure ?? 'Cargo harness did not exit cleanly.') }
Write-Output '✅ Complete: P2-04.03 executes 11/11 feature cases; phase closure remains open.'
