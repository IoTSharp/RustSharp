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
if (($rid -ceq 'win-x64') -ne $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Independent native x64 execution is required.' }
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_SHA -cne $CandidateSha -or $env:GITHUB_RUN_ID -cne $RunId -or
    $env:GITHUB_RUN_ATTEMPT -cne $RunAttempt -or $env:GITHUB_REPOSITORY -cne $Repository -or $env:GITHUB_JOB -cne 'drop-native') { throw 'Expected authenticated native Actions context is missing.' }
$directory = Join-Path $root ('artifacts/p1-drop-ci/' + $PlatformName)
$dropDirectory = 'artifacts/p1-drop/' + $PlatformName
$dropReport = $dropDirectory + '/native-v5.json'
$aotReport = $dropDirectory + '/native-aot-v1.json'
$callableReport = $dropDirectory + '/callable-v1.json'
$buildPath = 'artifacts/p1-drop-ci/' + $PlatformName + '/release-build.json'
$snapshotPath = 'artifacts/p1-drop-ci/' + $PlatformName + '/source-snapshot.json'
$assembly = 'tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll'
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow
$stages = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
$reports = [ordered]@{}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$rustc = (Get-Command rustc -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$pwsh = if ($IsWindows) { 'C:\Program Files\PowerShell\7\pwsh.exe' } else { (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$tree = $null
$oldSdk = $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION
$oldDotnet = $env:RUSTSHARP_DOTNET_PATH
$oldRustc = $env:RUSTSHARP_RUSTC_PATH
function Guard([switch]$Closeout) {
    if(-not $Closeout){$CancellationToken.ThrowIfCancellationRequested()}
    $limit=if($Closeout){3600}else{3540}
    if($clock.Elapsed.TotalSeconds -ge $limit){throw ('Native Drop exceeded its '+$limit+' second execution/closeout bound.')}
}
$capturedReports=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$captureUsage=[pscustomobject]@{bytes=0L;reads=0}
$originalPeBindings=[Collections.Generic.List[object]]::new()
function Resolve-OwnedPath([string]$Path,[string]$Scope='',[switch]$Closeout){
    Guard -Closeout:$Closeout; $full=[IO.Path]::GetFullPath($Path,$root)
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    $prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not $full.StartsWith($prefix,$comparison)){throw 'Evidence path escaped repository.'}
    if($Scope){$scopePrefix=[IO.Path]::GetFullPath($Scope,$root).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar;if(-not $full.StartsWith($scopePrefix,$comparison)){throw 'Physical witness escaped its dedicated scope.'}}
    $cursor=$full;$parentClock=[Diagnostics.Stopwatch]::StartNew()
    for($depth=0;$depth -lt 32 -and $parentClock.Elapsed.TotalSeconds -lt 2;$depth++){
        Guard -Closeout:$Closeout; if([IO.File]::Exists($cursor)-or[IO.Directory]::Exists($cursor)){if(([IO.File]::GetAttributes($cursor)-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Evidence traversed a file or parent link.'}}
        if($cursor.TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($root.TrimEnd([IO.Path]::DirectorySeparatorChar),$comparison)){return $full}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    throw 'Evidence parent check exceeded chain/time bounds.'
}
function Read-CapturedReport([string]$Relative,[long]$MaximumBytes=4194304,[switch]$Closeout){
    Guard -Closeout:$Closeout
    if($MaximumBytes -lt 1 -or $MaximumBytes -gt 33554432 -or $captureUsage.reads -ge 128){throw 'Evidence capture byte/count limit exceeded.'}
    $captureUsage.reads++
    $full=Resolve-OwnedPath $Relative -Closeout:$Closeout
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$memory=[IO.MemoryStream]::new()
    $token=if($Closeout){[Threading.CancellationToken]::None}else{$CancellationToken}
    $cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($token)
    $limit=if($Closeout){3600}else{3540}; $cts.CancelAfter([TimeSpan]::FromSeconds([Math]::Min(10,[Math]::Max(0.001,$limit-$clock.Elapsed.TotalSeconds))))
    $readClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        $null=Resolve-OwnedPath $full -Closeout:$Closeout; if($stream.Length -lt 1 -or $stream.Length -gt $MaximumBytes){throw 'Opened report exceeds byte bound.'}
        $buffer=[byte[]]::new(65536);$complete=$false;$maximumReads=[int][Math]::Ceiling($MaximumBytes/65536.0)+1
        for($readIndex=0;$readIndex -lt $maximumReads -and $readClock.Elapsed.TotalSeconds -lt 10 -and $clock.Elapsed.TotalSeconds -lt 3600;$readIndex++){
            Guard -Closeout:$Closeout; $cts.Token.ThrowIfCancellationRequested()
            $read=$stream.ReadAsync($buffer,0,$buffer.Length,$cts.Token).GetAwaiter().GetResult()
            Guard -Closeout:$Closeout; $cts.Token.ThrowIfCancellationRequested(); if($readClock.Elapsed.TotalSeconds -ge 10){throw 'Report capture exceeded read/time bounds.'}
            if($read -eq 0){$complete=$true;break}
            $captureUsage.bytes+=$read
            if($memory.Length+$read -gt $MaximumBytes -or $captureUsage.bytes -gt 2147483648){throw 'Actual report bytes exceeded file/shared capture bound.'}
            $memory.Write($buffer,0,$read)
        }
        if(-not $complete){throw 'Report capture exceeded read/time bounds.'}
        $bytes=$memory.ToArray();$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        $relative=[IO.Path]::GetRelativePath($root,$full).Replace('\','/')
        if($capturedReports.ContainsKey($relative)-and $capturedReports[$relative].sha256 -cne $hash){throw 'Previously validated report bytes changed before receipt binding.'}
        $value=[pscustomobject]@{document=(ConvertFrom-P1StrictJson $bytes);sha256=$hash;bytes=$bytes.Length;repositoryPath=$relative}
        Guard -Closeout:$Closeout; $cts.Token.ThrowIfCancellationRequested(); if($readClock.Elapsed.TotalSeconds -ge 10){throw 'Report capture exceeded read/time bounds.'}
        $capturedReports[$relative]=$value
        return $value
    }finally{$stream.Dispose();$memory.Dispose();$cts.Dispose()}
}
function Read-Report([string]$Relative,[long]$MaximumBytes=4194304){
    return (Read-CapturedReport $Relative $MaximumBytes).document
}
function Hash-OpenedPe([IO.FileStream]$Stream){
    Guard;$Stream.Position=0
    if($Stream.Length -lt 1 -or $Stream.Length -gt 268435456){throw 'Opened original PE exceeds 256 MiB.'}
    $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken);$cts.CancelAfter([TimeSpan]::FromSeconds([Math]::Min(10,[Math]::Max(0.001,3540-$clock.Elapsed.TotalSeconds))))
    $readClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        $buffer=[byte[]]::new(65536);$actual=0L;$complete=$false
        for($block=0;$block -lt 4097 -and $readClock.Elapsed.TotalSeconds -lt 10 -and $clock.Elapsed.TotalSeconds -lt 3540;$block++){
            Guard; $cts.Token.ThrowIfCancellationRequested();$read=$Stream.ReadAsync($buffer,0,$buffer.Length,$cts.Token).GetAwaiter().GetResult()
            Guard; $cts.Token.ThrowIfCancellationRequested(); if($readClock.Elapsed.TotalSeconds -ge 10){throw 'Original PE hash exceeded count/time bounds.'}
            if($read -eq 0){$complete=$true;break};$actual+=$read;$captureUsage.bytes+=$read
            if($actual -gt 268435456 -or $captureUsage.bytes -gt 2147483648){throw 'Actual PE bytes exceeded file/shared capture bound.'};$hash.AppendData($buffer,0,$read)
        }
        if(-not $complete){throw 'Original PE hash exceeded count/time bounds.'}
        $result=[pscustomobject]@{sha256=[Convert]::ToHexString($hash.GetHashAndReset());bytes=$actual}
        Guard; $cts.Token.ThrowIfCancellationRequested(); if($readClock.Elapsed.TotalSeconds -ge 10){throw 'Original PE hash exceeded count/time bounds.'}
        return $result
    }finally{$hash.Dispose();$cts.Dispose()}
}
function Verify-OriginalPe([string]$Path,[string]$ExpectedHash,[string]$StageName,[string]$Evidence,[int]$Timeout){
    Guard
    if($originalPeBindings.Count -ge 30 -or $ExpectedHash -cnotmatch '^[a-fA-F0-9]{64}$'){throw 'Original PE denominator/hash changed.'}
    $full=Resolve-OwnedPath $Path 'artifacts/p1-drop'
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        $before=Hash-OpenedPe $stream
        if($before.sha256 -cne $ExpectedHash){throw 'Original PE bytes differ from native report.'}
        Launch $StageName $pwsh @('-NoLogo','-NoProfile','-File','eng/Invoke-ILVerify.ps1','-AssemblyPath',$full,'-TimeoutSeconds','20','-EvidencePath',$Evidence) $Timeout
        $capture=Read-CapturedReport $Evidence;$verifier=$capture.document
        $after=Hash-OpenedPe $stream
        $current=[IO.FileStream]::new((Resolve-OwnedPath $full 'artifacts/p1-drop'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{$pathAfter=Hash-OpenedPe $current}finally{$current.Dispose()}
        $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
        if($verifier.Succeeded -cne $true -or $verifier.Assembly.Sha256 -cne $ExpectedHash -or $verifier.Tool.Version -cne '10.0.11' -or
            -not ([IO.Path]::GetFullPath([string]$verifier.Assembly.Path,$root)).Equals($full,$comparison) -or $verifier.Assembly.Length -ne $before.bytes -or
            $after.sha256 -cne $before.sha256 -or $after.bytes -ne $before.bytes -or $pathAfter.sha256 -cne $before.sha256 -or $pathAfter.bytes -ne $before.bytes){
            throw 'Original PE verifier identity/version/opened bytes did not close.'
        }
        $originalPeBindings.Add([pscustomobject]@{stage=$StageName;path=[IO.Path]::GetRelativePath($root,$full).Replace('\','/');sha256=$before.sha256;bytes=$before.bytes;sha256AfterVerification=$after.sha256;retainedPathSha256After=$pathAfter.sha256;verifierReport=[ordered]@{repositoryPath=$capture.repositoryPath;sha256=$capture.sha256};verifierVersion='10.0.11';physicalBytesVerified=$true})
    }finally{$stream.Dispose()}
}
function Launch([string]$Name,[string]$File,[string[]]$Arguments,[int]$Timeout){
    Guard
    if($stages.Count -ge 36){throw 'Fixed native Drop stage count exceeded.'}
    $prefix=Join-Path $directory $Name
    $row=[ordered]@{name=$Name;file=$File;arguments=$Arguments;status='blocked';process=$null;error=$null};$stages.Add($row)
    Write-Host ('P1 native Drop stage: '+$Name)
    try{
        & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $File -ArgumentList $Arguments -WorkingDirectory $root -TimeoutSeconds ([Math]::Min($Timeout,[Math]::Max(1,3540-[int]$clock.Elapsed.TotalSeconds))) -CapturePrefix $prefix -MaximumOutputBytes 4194304 -CancellationToken $CancellationToken
    }catch{$row.error=$_.Exception.Message;$failures.Add($Name+': '+$row.error)}
    try{
        # Preserve the actual ledger even if the owned launch failed or was cancelled.
        $capture=Read-CapturedReport ([IO.Path]::GetRelativePath($root,$prefix)+'.process.json') 65536 -Closeout
        $ledger=$capture.document;$row.process=$ledger
        $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
        if($ledger.ExitCode -ne 0-or$ledger.CleanupComplete -cne $true-or$ledger.Failure-or$ledger.Pid -le 0-or$ledger.ParentPid -ne $PID-or
            -not ([IO.Path]::GetFullPath([string]$ledger.FilePath)).Equals([IO.Path]::GetFullPath($File),$comparison)-or
            ($ledger.Arguments|ConvertTo-Json -Depth 8 -Compress)-cne($Arguments|ConvertTo-Json -Depth 8 -Compress)-or
            [DateTimeOffset]::Parse($ledger.FinishedAt)-lt[DateTimeOffset]::Parse($ledger.StartedAt)){throw 'Owned process argv/identity/exit/cleanup did not close.'}
        if($null -eq $row.error){$row.status='passed'}
    }catch{$message=$_.Exception.Message;$row.error=if($row.error){$row.error+'; ledger: '+$message}else{$message};$failures.Add($Name+' ledger: '+$message)}
    # A later cancellation/deadline cannot erase the already captured original failure.
    Guard
}
function Publish-NativeReceipt([object]$Receipt,[string]$Destination){
    Guard -Closeout
    $destination=Resolve-OwnedPath $Destination -Closeout
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Receipt|ConvertTo-Json -Depth 32))
    if($bytes.Length -gt 4194304){throw 'Native receipt exceeded four MiB.'}
    $temporary=$destination+'.tmp-'+$PID+'-'+[Guid]::NewGuid().ToString('N')
    $owned=$false;$stream=$null;$cts=$null;$primaryFailure=$null;$cleanupFailures=[Collections.Generic.List[Exception]]::new()
    try{
        $temporary=Resolve-OwnedPath $temporary -Closeout
        $cts=[Threading.CancellationTokenSource]::new()
        $cts.CancelAfter([TimeSpan]::FromSeconds([Math]::Min(10,[Math]::Max(0.001,3600-$clock.Elapsed.TotalSeconds))))
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$owned=$true
        $maximumWrites=[int][Math]::Ceiling($bytes.Length/65536.0);$writeClock=[Diagnostics.Stopwatch]::StartNew()
        for($block=0;$block -lt $maximumWrites-and$block -lt 64-and$writeClock.Elapsed.TotalSeconds -lt 10;$block++){
            Guard -Closeout;$cts.Token.ThrowIfCancellationRequested();$offset=$block*65536;$count=[Math]::Min(65536,$bytes.Length-$offset)
            $null=$stream.WriteAsync($bytes,$offset,$count,$cts.Token).GetAwaiter().GetResult()
            Guard -Closeout; $cts.Token.ThrowIfCancellationRequested(); if($writeClock.Elapsed.TotalSeconds -ge 10){throw 'Receipt write exceeded count/time bound.'}
        }
        if($block -ne $maximumWrites){throw 'Receipt write exceeded count/time bound.'}
        $null=$stream.FlushAsync($cts.Token).GetAwaiter().GetResult()
        Guard -Closeout; $cts.Token.ThrowIfCancellationRequested(); if($writeClock.Elapsed.TotalSeconds -ge 10){throw 'Receipt write exceeded count/time bound.'}
        $stream.Dispose();$stream=$null
        $null=Resolve-OwnedPath $temporary -Closeout;$null=Resolve-OwnedPath $destination -Closeout
        Guard -Closeout; $cts.Token.ThrowIfCancellationRequested(); if($writeClock.Elapsed.TotalSeconds -ge 10){throw 'Receipt write exceeded count/time bound.'}
        [IO.File]::Move($temporary,$destination,$false);$owned=$false
    }catch{$primaryFailure=$_.Exception}
    finally{
        if($null -ne $stream){try{$stream.Dispose()}catch{$cleanupFailures.Add($_.Exception)}}
        if($null -ne $cts){try{$cts.Dispose()}catch{$cleanupFailures.Add($_.Exception)}}
        if($owned){
            try{
                # Independent bounded cleanup ignores execution cancellation/deadline.
                $parentClock=[Diagnostics.Stopwatch]::StartNew();$cursor=[IO.Path]::GetFullPath($temporary);$verified=$false
                $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
                if(-not $cursor.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,$comparison)){throw 'Owned receipt cleanup escaped repository.'}
                for($depth=0;$depth -lt 32-and$parentClock.Elapsed.TotalSeconds -lt 2;$depth++){
                    if(([IO.File]::Exists($cursor)-or[IO.Directory]::Exists($cursor))-and([IO.File]::GetAttributes($cursor)-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Owned receipt cleanup traversed a link.'}
                    if($cursor.TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($root.TrimEnd([IO.Path]::DirectorySeparatorChar),$comparison)){$verified=$true;break}
                    $cursor=[IO.Path]::GetDirectoryName($cursor)
                }
                if(-not $verified){throw 'Owned receipt cleanup exceeded parent/count budget.'}
                if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
                if([IO.File]::Exists($temporary)){throw 'Owned receipt temporary still exists after cleanup.'}
            }catch{$cleanupFailures.Add($_.Exception)}
        }
    }
    if($cleanupFailures.Count -gt 0){
        $all=[Collections.Generic.List[Exception]]::new();if($null -ne $primaryFailure){$all.Add($primaryFailure)}
        $all.AddRange($cleanupFailures)
        throw [AggregateException]::new('Native receipt failed with owned cleanup errors.',$all.ToArray())
    }
    if($null -ne $primaryFailure){throw $primaryFailure}
}
try {
    $directory=Resolve-OwnedPath $directory
    if([IO.Directory]::Exists($directory)){throw 'An exclusive fresh native Drop receipt directory is required.'}
    [IO.Directory]::CreateDirectory($directory)|Out-Null
    $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION='10.0.401'
    $env:RUSTSHARP_DOTNET_PATH=$dotnet
    $env:RUSTSHARP_RUSTC_PATH=$rustc
    Launch 'fresh-release' $pwsh @('-NoLogo','-NoProfile','-File','eng/Invoke-P1ReleaseBuild.ps1','-CandidateSha',$CandidateSha,'-SdkVersion','10.0.401','-DotNetPath',$dotnet,'-ReportPath',$buildPath) 750
    Launch 'snapshot' $pwsh @('-NoLogo','-NoProfile','-File','eng/Get-P1SourceSnapshot.ps1','-CandidateSha',$CandidateSha,'-EvidencePath',$snapshotPath,'-DeadlineSeconds','90') 100
    $build = Read-Report $buildPath
    $snapshot = Read-Report $snapshotPath
    $errors=@(Test-P1BuildEvidence $build $CandidateSha $rid $root) + @(Test-P1SnapshotEvidence $snapshot $root $CandidateSha)
    if ($errors.Count -gt 0) { throw ($errors -join '; ') }
    $tree=$snapshot.treeSha
    Launch 'generated-v5' $dotnet @($assembly,'--p1-drop-native-v5',$dropReport,$rustc,$dotnet,'28') 270
    Launch 'generated-physical-validation' $dotnet @($assembly,'--validate-p1-drop-generated',$dropReport) 30
    $drop = Read-Report $dropReport
    if ($drop.profile -cne 'p1-drop-closure-v5' -or $drop.summary.denominator -ne 28 -or $drop.summary.executed -ne 28 -or $drop.summary.passed -ne 26 -or $drop.summary.contractDifferences -ne 2 -or
        $drop.summary.failed -ne 0 -or $drop.summary.blocked -ne 0 -or $drop.summary.skipped -ne 0 -or $drop.summary.expectedContractSatisfied -ne $true -or $drop.cleanup.completed -ne $true) { throw 'NativeV5 fixed generated contract did not close.' }
    # Original PE ILVerify: 28 fixed inputs, 300-second shared bound. Loop exit
    # uses comparison, fixed count and wall time; one fixture must be smoke-tested
    # by the supervisor before integrating this loop.
    $verifyClock=[Diagnostics.Stopwatch]::StartNew()
    $verifyCases=@($drop.cases)
    if ($verifyCases.Count -ne 28) { throw 'Original PE verifier denominator changed.' }
    for ($index=0;$index -lt 28 -and $verifyClock.Elapsed.TotalSeconds -lt 300;$index++) {
        Guard
        $verifierEvidence='artifacts/p1-drop-ci/'+$PlatformName+'/original-pe-'+$index.ToString('D2',[Globalization.CultureInfo]::InvariantCulture)+'.json'
        Verify-OriginalPe ([string]$verifyCases[$index].rustSharpCompile.outputPath) ([string]$verifyCases[$index].rustSharpCompile.outputSha256) ('original-pe-'+$index.ToString('D2',[Globalization.CultureInfo]::InvariantCulture)) $verifierEvidence ([Math]::Min(30,[Math]::Max(1,300-[int]$verifyClock.Elapsed.TotalSeconds)))
    }
    if ($index -ne 28) { throw 'Original PE verifier exceeded its fixed count/time bound.' }
    Launch 'native-aot' $dotnet @($assembly,'--p1-drop-native-aot',$dropReport,$aotReport,'28') 930
    Launch 'callable' $dotnet @($assembly,'--p1-drop-call-interface','create',$callableReport,'2') 930
    $aot = Read-Report $aotReport
    $callable = Read-Report $callableReport 33554432
    if ($aot.summary.expectedContractSatisfied -ne $true -or $aot.summary.denominator -ne 28 -or $aot.summary.executed -ne 28 -or $aot.summary.passed -ne 28 -or $aot.summary.failed -ne 0 -or $aot.summary.blocked -ne 0 -or $aot.summary.skipped -ne 0 -or $aot.isWsl -ne $false -or
        $aot.targetRuntimeIdentifier -cne $rid -or $aot.processArchitecture -cne 'X64' -or $aot.cleanup.completed -ne $true -or $aot.execution.deadlineExpired -ne $false -or $null -ne $aot.harnessError -or
        $aot.inputDifferentialReportSha256 -cne $capturedReports[$dropReport].sha256 -or
        $callable.summary.expectedContractSatisfied -ne $true -or $callable.summary.denominator -ne 7 -or $callable.summary.artifactDenominator -ne 2 -or $callable.summary.requestedArtifacts -ne 2 -or $callable.summary.passed -ne 7 -or $callable.summary.failed -ne 0 -or $callable.summary.blocked -ne 0 -or $callable.summary.skipped -ne 0 -or
        $callable.cleanup.completed -ne $true -or $callable.execution.deadlineExpired -ne $false -or $null -ne $callable.harnessError) { throw 'Native AOT or callable fixed suite did not close.' }
    $callableClock=[Diagnostics.Stopwatch]::StartNew()
    $callableArtifacts=@($callable.artifacts)
    if($callableArtifacts.Count -ne 2){throw 'Callable original PE denominator changed.'}
    for($index=0;$index -lt 2 -and $callableClock.Elapsed.TotalSeconds -lt 60;$index++){
        Guard
        $verifierEvidence='artifacts/p1-drop-ci/'+$PlatformName+'/callable-original-pe-'+$index.ToString('D2',[Globalization.CultureInfo]::InvariantCulture)+'.json'
        Verify-OriginalPe ([string]$callableArtifacts[$index].generatedAssemblyPath) ([string]$callableArtifacts[$index].generatedAssemblySha256) ('callable-original-pe-'+$index.ToString('D2',[Globalization.CultureInfo]::InvariantCulture)) $verifierEvidence ([Math]::Min(30,[Math]::Max(1,60-[int]$callableClock.Elapsed.TotalSeconds)))
    }
    if($index -ne 2){throw 'Callable original PE verifier count/time bound expired.'}
} catch { $failures.Add('native-drop: ' + $_.Exception.Message) }
finally {
    $env:RUSTSHARP_NATIVE_AOT_SDK_VERSION=$oldSdk
    $env:RUSTSHARP_DOTNET_PATH=$oldDotnet
    $env:RUSTSHARP_RUSTC_PATH=$oldRustc
}
foreach ($item in @(@{name='build';path=$buildPath},@{name='snapshot';path=$snapshotPath},@{name='generated';path=$dropReport},@{name='aot';path=$aotReport},@{name='callable';path=$callableReport})) {
    try { $captured=Read-CapturedReport $item.path 33554432 -Closeout; $reports[$item.name]=[ordered]@{repositoryPath=$captured.repositoryPath;sha256=$captured.sha256} }
    catch { $reports[$item.name]=$null;$failures.Add($_.Exception.Message) }
}
$closed=$failures.Count -eq 0 -and $originalPeBindings.Count -eq 30 -and $stages.Count -eq 36 -and @($stages | Where-Object status -CNE 'passed').Count -eq 0 -and $clock.Elapsed.TotalSeconds -lt 3600
$receipt=[ordered]@{
    schemaVersion=1;evidenceKind='p1-native-drop-ci-receipt';candidateSha=$CandidateSha;candidateTreeSha=$tree;runtimeIdentifier=$rid
    ci=[ordered]@{repository=$Repository;runId=$RunId;runAttempt=$RunAttempt;job=$env:GITHUB_JOB;runnerOs=$env:RUNNER_OS}
    fixedDenominators=[ordered]@{generated=28;exactRustcMatches=26;frozenDifferences=2;originalPeIlVerify=30;nativeAot=28;callableArtifacts=2;callableCases=7}
    reports=$reports;originalPeBindings=@($originalPeBindings.ToArray());stages=@($stages.ToArray());failures=@($failures.ToArray())
    summary=[ordered]@{status=if($closed){'passed'}else{'blocked'};nativeExecutionComplete=$closed;fullP1LanguageGateApproved=$false;fullP1Closure=$false}
    execution=[ordered]@{startedAtUtc=$started.ToString('O');finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');maximumStages=36;deadlineSeconds=3600}
}
Publish-NativeReceipt $receipt (Join-Path $directory 'native-receipt.json')
if (-not $closed) { exit 2 }
