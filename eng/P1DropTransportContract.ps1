function Assert-P1DropTransportDeadline {
    param([Diagnostics.Stopwatch]$Clock,[double]$Seconds,[Threading.CancellationToken]$CancellationToken,[string]$Operation)
    $CancellationToken.ThrowIfCancellationRequested()
    if($Clock.Elapsed.TotalSeconds -ge $Seconds){throw ($Operation+' exceeded its monotonic deadline.')}
}
function Resolve-P1DropTransportPath {
    param([string]$Root,[string]$Path,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    $clock=[Diagnostics.Stopwatch]::StartNew();$rootFull=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $full=[IO.Path]::GetFullPath($Path,$rootFull);$comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    if(-not $full.StartsWith($rootFull+[IO.Path]::DirectorySeparatorChar,$comparison)){throw 'Transport path escaped the repository.'}
    $cursor=$full
    for($depth=0;$depth -lt 32 -and $clock.Elapsed.TotalSeconds -lt 2;$depth++){
        Assert-P1DropTransportDeadline $clock 2 $CancellationToken 'Transport parent verification'
        if([IO.File]::Exists($cursor)-or[IO.Directory]::Exists($cursor)){
            if(([IO.File]::GetAttributes($cursor)-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Transport path traversed a file or parent link.'}
        }
        Assert-P1DropTransportDeadline $clock 2 $CancellationToken 'Transport parent verification'
        if($cursor.Equals($rootFull,$comparison)){return $full}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    throw 'Transport parent verification exceeded its bounded chain/time.'
}
function Get-P1DropOriginalRepositoryPath {
    param([string]$Path,[string]$PlatformName)
    $normalized=$Path.Replace('\','/');$scope='artifacts/p1-drop/'+$PlatformName+'/'
    if($normalized.StartsWith($scope,[StringComparison]::Ordinal)){$relative=$normalized}
    else{
        $marker='/'+$scope;$position=$normalized.IndexOf($marker,[StringComparison]::Ordinal)
        if($position -lt 0 -or $normalized.IndexOf($marker,$position+1,[StringComparison]::Ordinal)-ge 0){throw 'Original Drop path has no unique repository identity.'}
        $origin=$normalized.Substring(0,$position)
        if($origin -cnotmatch '^(?:[A-Za-z]:/|/)' -or $origin -cmatch '(^|/)\.\.(/|$)'){throw 'Original Drop path has an invalid native origin.'}
        $relative=$normalized.Substring($position+1)
    }
    if($relative -cmatch '(^|/)\.\.?(/|$)|:|\\' -or $relative.EndsWith('/')){throw 'Original Drop path escaped its frozen native scope.'}
    return $relative
}
function Read-P1DropTransportBytes {
    param([string]$Root,[string]$Path,[long]$MaximumBytes,[switch]$Capture,[object]$Usage,
        [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None,
        [Diagnostics.Stopwatch]$OverallClock,[int]$DeadlineSeconds=90)
    if($MaximumBytes -lt 1 -or $MaximumBytes -gt 536870912 -or ($Capture-and$MaximumBytes -gt 33554432)){throw 'Transport read byte bound is invalid.'}
    $clock=[Diagnostics.Stopwatch]::StartNew();$cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken)
    $cts.CancelAfter([TimeSpan]::FromSeconds(10));$stream=$null;$memory=$null;$header=[IO.MemoryStream]::new();$hash=$null
    if($null -eq $Usage){$Usage=[pscustomobject]@{bytes=0L}}
    function CheckRead {
        Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport read'
        if($null -ne $OverallClock){Assert-P1DropTransportDeadline $OverallClock $DeadlineSeconds $cts.Token 'Transport overall validation'}
    }
    try{
        CheckRead;$full=Resolve-P1DropTransportPath $Root $Path $cts.Token
        $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,65536,[IO.FileOptions]::Asynchronous)
        CheckRead;$null=Resolve-P1DropTransportPath $Root $full $cts.Token;$openedLength=$stream.Length
        if($openedLength -lt 1 -or $openedLength -gt $MaximumBytes){throw 'Transport opened file exceeded its byte bound.'}
        $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        if($Capture){$memory=[IO.MemoryStream]::new()}
        $buffer=[byte[]]::new(65536);$actual=0L;$complete=$false;$maximumReads=[int][Math]::Ceiling($MaximumBytes/65536.0)+1
        for($block=0;$block -lt $maximumReads -and $clock.Elapsed.TotalSeconds -lt 10;$block++){
            CheckRead;$read=$stream.ReadAsync($buffer,0,$buffer.Length,$cts.Token).GetAwaiter().GetResult();CheckRead
            if($read -eq 0){$complete=$true;break}
            $actual+=$read;$Usage.bytes+=$read
            if($actual -gt $MaximumBytes -or $Usage.bytes -gt 2147483648){throw 'Actual transport bytes exceeded the file/shared bound.'}
            $hash.AppendData($buffer,0,$read)
            if($Capture){$memory.Write($buffer,0,$read)}
            if($header.Length -lt 65536){$header.Write($buffer,0,[int][Math]::Min($read,65536-$header.Length))}
        }
        if(-not $complete -or $actual -ne $openedLength){throw 'Transport file grew, shrank or exceeded read-count/time bounds.'}
        $result=[pscustomobject]@{path=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Root),$full).Replace('\','/');sha256=[Convert]::ToHexString($hash.GetHashAndReset());bytes=$actual;content=if($Capture){$memory.ToArray()}else{$null};header=$header.ToArray()}
        CheckRead;return $result
    }finally{if($null-ne$stream){$stream.Dispose()};if($null-ne$memory){$memory.Dispose()};if($null-ne$hash){$hash.Dispose()};$header.Dispose();$cts.Dispose()}
}
function Write-P1DropTransportProof {
    param([string]$Root,[string]$Destination,[object]$Document,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    $clock=[Diagnostics.Stopwatch]::StartNew();$cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken);$cts.CancelAfter([TimeSpan]::FromSeconds(10))
    $owned=$false;$stream=$null;$temporary=$null;$primary=$null;$cleanup=[Collections.Generic.List[Exception]]::new()
    try{
        Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication'
        $destination=Resolve-P1DropTransportPath $Root $Destination $cts.Token
        $relative=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Root),$destination).Replace('\','/')
        if($relative -cnotmatch '^artifacts/(?:p1-drop-ci/(?:windows-x64|linux-x64)/transport-index\.json|p1-candidate/drop-transport-(?:windows-x64|linux-x64)\.json)$'){throw 'Transport proof destination escaped its fixed output scope.'}
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Document|ConvertTo-Json -Depth 32))
        if($bytes.Length -lt 1 -or $bytes.Length -gt 4194304){throw 'Transport proof exceeded four MiB.'}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
        $null=Resolve-P1DropTransportPath $Root $destination $cts.Token
        $temporary=$destination+'.owned-'+[Guid]::NewGuid().ToString('N');$null=Resolve-P1DropTransportPath $Root $temporary $cts.Token
        Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication'
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::Asynchronous);$owned=$true
        $maximumWrites=[int][Math]::Ceiling($bytes.Length/65536.0)
        for($block=0;$block -lt $maximumWrites -and $block -lt 64 -and $clock.Elapsed.TotalSeconds -lt 10;$block++){
            Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication';$offset=$block*65536
            $null=$stream.WriteAsync($bytes,$offset,[Math]::Min(65536,$bytes.Length-$offset),$cts.Token).GetAwaiter().GetResult()
            Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication'
        }
        if($block -ne $maximumWrites){throw 'Transport proof exceeded write-count/time bounds.'}
        $null=$stream.FlushAsync($cts.Token).GetAwaiter().GetResult();Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication'
        $stream.Dispose();$stream=$null;$null=Resolve-P1DropTransportPath $Root $temporary $cts.Token;$null=Resolve-P1DropTransportPath $Root $destination $cts.Token
        Assert-P1DropTransportDeadline $clock 10 $cts.Token 'Transport proof publication';[IO.File]::Move($temporary,$destination,$false);$owned=$false
    }catch{$primary=$_.Exception}
    finally{
        if($null-ne$stream){try{$stream.Dispose()}catch{$cleanup.Add($_.Exception)}}
        if($owned){try{$verified=Resolve-P1DropTransportPath $Root $temporary ([Threading.CancellationToken]::None);if([IO.File]::Exists($verified)){[IO.File]::Delete($verified)};if([IO.File]::Exists($verified)){throw 'Owned transport temporary still exists.'}}catch{$cleanup.Add($_.Exception)}}
        try{$cts.Dispose()}catch{$cleanup.Add($_.Exception)}
    }
    if($cleanup.Count -gt 0){$all=[Collections.Generic.List[Exception]]::new();if($null-ne$primary){$all.Add($primary)};$all.AddRange($cleanup);throw [AggregateException]::new('Transport publication failed with owned cleanup errors.',$all.ToArray())}
    if($null-ne$primary){throw $primary}
}
function Test-P1DropOriginalPeBinding {
    param([object]$Binding,[object]$Verifier,[object]$Witness,[string]$ExpectedStage,[string]$OriginalPath,[string]$ExpectedHash,[string]$PlatformName)
    $expectedPath=Get-P1DropOriginalRepositoryPath $OriginalPath $PlatformName
    $verifierPath=Get-P1DropOriginalRepositoryPath ([string]$Verifier.Assembly.Path) $PlatformName
    $originMatches=$true;$originalNormalized=$OriginalPath.Replace('\','/');$verifierNormalized=([string]$Verifier.Assembly.Path).Replace('\','/')
    if($originalNormalized -cmatch '^(?:[A-Za-z]:/|/)'){
        $comparison=if($PlatformName -ceq 'windows-x64'){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
        $originMatches=$originalNormalized.Equals($verifierNormalized,$comparison)
    }
    if($Binding.stage -cne $ExpectedStage -or $Binding.path -cne $expectedPath -or $Witness.path -cne $expectedPath -or $Binding.sha256 -cne $ExpectedHash -or $Witness.sha256 -cne $ExpectedHash -or
        $Binding.sha256AfterVerification -cne $ExpectedHash -or $Binding.retainedPathSha256After -cne $ExpectedHash -or $Binding.bytes -le 0 -or $Binding.bytes -gt 268435456 -or $Binding.bytes -ne $Witness.bytes -or
        $Binding.verifierVersion -cne '10.0.11' -or $Binding.physicalBytesVerified -cne $true -or $Verifier.Succeeded -cne $true -or $Verifier.Assembly.Sha256 -cne $ExpectedHash -or
        $Verifier.Assembly.Length -ne $Witness.bytes -or $Verifier.Tool.Version -cne '10.0.11' -or $verifierPath -cne $expectedPath -or -not $originMatches){throw 'Original PE/verifier stage/path/hash/length/version/opened-byte identity did not reconcile.'}
}
function Invoke-P1DropTransportContract {
    param([string]$Root,[string]$ReceiptPath,[string]$CandidateSha,[string]$PlatformName,[string]$RunId,[string]$RunAttempt,[object]$TransportIndex=$null,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    $Root=[IO.Path]::GetFullPath($Root);$clock=[Diagnostics.Stopwatch]::StartNew();$usage=[pscustomobject]@{bytes=0L};$files=[Collections.Generic.List[object]]::new();$seen=@{}
    $processFiles=[Collections.Generic.List[object]]::new();$verifierReports=[Collections.Generic.List[object]]::new();$originalPeBindings=[Collections.Generic.List[object]]::new()
    $rid=if($PlatformName -ceq 'windows-x64'){'win-x64'}elseif($PlatformName -ceq 'linux-x64'){'linux-x64'}else{throw 'Unknown native Drop platform.'}
    function Guard{Assert-P1DropTransportDeadline $clock 90 $CancellationToken 'Drop transport'}
    function Need([bool]$Condition,[string]$Message){Guard;if(-not$Condition){throw $Message}}
    function Captured([string]$Path,[int]$MaximumBytes=16777216){
        Guard;$capture=Read-P1DropTransportBytes -Root $Root -Path $Path -MaximumBytes $MaximumBytes -Capture -Usage $usage -CancellationToken $CancellationToken -OverallClock $clock
        $value=ConvertFrom-P1StrictJson $capture.content;Guard;return [pscustomobject]@{value=$value;sha256=$capture.sha256;path=$capture.path;bytes=$capture.bytes}
    }
    function Add-Witness([string]$Kind,[string]$CaseId,[string]$OriginalPath,[string]$ExpectedHash){
        Guard;Need ($ExpectedHash -cmatch '^[a-fA-F0-9]{64}$') 'Transport witness hash is missing.'
        $relative=Get-P1DropOriginalRepositoryPath $OriginalPath $PlatformName
        if($null-ne$TransportIndex){$match=@($TransportIndex.files|Where-Object{$_.kind -ceq $Kind -and $_.caseId -ceq $CaseId -and $_.originalPath.Replace('\','/') -ceq $OriginalPath.Replace('\','/') -and $_.sha256 -ceq $ExpectedHash});Need ($match.Count -eq 1 -and $match[0].path -ceq $relative) 'Original witness was omitted, duplicated, relocated or substituted during transport.'}
        if($seen.ContainsKey($relative)){Need ($seen[$relative].sha256 -ceq $ExpectedHash -and $seen[$relative].kind -ceq $Kind -and $seen[$relative].caseId -ceq $CaseId) 'A retained path has conflicting identities.';return}
        Need ($files.Count -lt 512) 'Transport exceeded 512 unique witnesses.'
        $bound=if($Kind -ceq 'original-pe'){268435456}else{536870912}
        $capture=Read-P1DropTransportBytes -Root $Root -Path $relative -MaximumBytes $bound -Usage $usage -CancellationToken $CancellationToken -OverallClock $clock
        Need ($capture.sha256 -ceq $ExpectedHash) 'Retained witness bytes changed.'
        if($Kind -cin @('native-aot','callable-native')){
            $header=$capture.header;$length=$header.Length;$valid=$false
            if($rid -ceq 'linux-x64'){$valid=$length -ge 64 -and $header[0]-eq127 -and $header[1]-eq69 -and $header[2]-eq76 -and $header[3]-eq70 -and $header[4]-eq2 -and $header[5]-eq1 -and [BitConverter]::ToUInt16($header,18)-eq62}
            elseif($length -ge 64 -and $header[0]-eq77 -and $header[1]-eq90){$offset=[BitConverter]::ToInt32($header,60);$valid=$offset -ge 64 -and $offset -le $length-6 -and [BitConverter]::ToUInt32($header,$offset)-eq17744 -and [BitConverter]::ToUInt16($header,$offset+4)-eq34404}
            Need $valid 'A native executable has the wrong actual target machine.'
        }
        $row=[pscustomobject]@{kind=$Kind;caseId=$CaseId;originalPath=$OriginalPath;path=$capture.path;sha256=$capture.sha256;bytes=$capture.bytes};$files.Add($row);$seen[$relative]=$row
    }
    if($null-ne$TransportIndex){Need ($TransportIndex.schemaVersion -eq1 -and $TransportIndex.evidenceKind -ceq 'p1-drop-native-transport-index' -and @($TransportIndex.files).Count -le512 -and @($TransportIndex.processFiles).Count -eq36 -and @($TransportIndex.reports).Count -eq5 -and @($TransportIndex.sources).Count -eq3 -and @($TransportIndex.originalPeBindings).Count -eq30 -and @($TransportIndex.verifierReports).Count -eq30 -and $TransportIndex.fullP1Closure -ceq $false -and $TransportIndex.fullP1LanguageGateApproved -ceq $false -and @($TransportIndex.errors).Count -eq0) 'The native transport index is incomplete or claims premature closure.'}
    $capturedReceipt=Captured $ReceiptPath 4194304;$receipt=$capturedReceipt.value
    Need ($receipt.evidenceKind -ceq 'p1-native-drop-ci-receipt' -and $receipt.candidateSha -ceq $CandidateSha -and $receipt.candidateTreeSha -cmatch '^[a-f0-9]{40}$' -and $receipt.runtimeIdentifier -ceq $rid -and $receipt.ci.repository -ceq 'IoTSharp/RustSharp' -and [string]$receipt.ci.runId -ceq $RunId -and [string]$receipt.ci.runAttempt -ceq $RunAttempt -and $receipt.ci.job -ceq 'drop-native' -and $receipt.summary.nativeExecutionComplete -ceq $true -and $receipt.summary.fullP1LanguageGateApproved -ceq $false -and $receipt.summary.fullP1Closure -ceq $false -and @($receipt.failures).Count -eq0) 'The original native receipt is incomplete or mismatched.'
    Need ($receipt.fixedDenominators.generated -eq28 -and $receipt.fixedDenominators.exactRustcMatches -eq26 -and $receipt.fixedDenominators.frozenDifferences -eq2 -and $receipt.fixedDenominators.originalPeIlVerify -eq30 -and $receipt.fixedDenominators.nativeAot -eq28 -and $receipt.fixedDenominators.callableArtifacts -eq2 -and $receipt.fixedDenominators.callableCases -eq7 -and @($receipt.stages).Count -eq36 -and @($receipt.originalPeBindings).Count -eq30) 'Frozen native Drop/PE verifier denominators changed.'
    $reports=@{};$bindings=[Collections.Generic.List[object]]::new()
    foreach($name in @('build','snapshot','generated','aot','callable')){Guard;$binding=$receipt.reports.$name;$capture=Captured $binding.repositoryPath 33554432;Need ($capture.sha256 -ceq $binding.sha256) 'Original native report bytes differ from receipt.';$reports[$name]=$capture.value;$bindings.Add([pscustomobject]@{name=$name;path=$capture.path;sha256=$capture.sha256})}
    Need ($reports.snapshot.schemaVersion -eq 1 -and $reports.snapshot.evidenceKind -ceq 'p1-candidate-source-snapshot' -and $reports.snapshot.verified -ceq $true -and $reports.snapshot.candidateSha -ceq $CandidateSha -and $reports.snapshot.treeSha -ceq $receipt.candidateTreeSha) 'Original native snapshot candidate/tree/verification did not reconcile.'
    Need ($reports.build.schemaVersion -eq 1 -and $reports.build.evidenceKind -ceq 'p1-release-build' -and $reports.build.candidateSha -ceq $CandidateSha -and $reports.build.configuration -ceq 'Release' -and $reports.build.succeeded -ceq $true -and $reports.build.summary.warnings -eq 0 -and $reports.build.summary.errors -eq 0 -and $reports.build.sourceProvenance.candidateMatchesWorkingTree -ceq $true -and $reports.build.sourceProvenance.candidateSha -ceq $CandidateSha -and $reports.build.sourceProvenance.candidateTreeSha -ceq $receipt.candidateTreeSha) 'Original native Release identity/result/source candidate did not reconcile.'
    Need ($reports.generated.runtimeIdentifier -ceq $rid -and $reports.generated.hostContract.platform -ceq $PlatformName -and $reports.generated.hostContract.nativeRuntimeIdentifier -ceq $rid -and $reports.generated.hostContract.fullP1Closure -ceq $false -and $reports.generated.hostContract.fullP1LanguageGateApproved -ceq $false) 'Original generated report platform/host contract did not reconcile.'
    Need ($reports.generated.profile -ceq 'p1-drop-closure-v5' -and $reports.generated.summary.denominator -eq28 -and $reports.generated.summary.executed -eq28 -and $reports.generated.summary.passed -eq26 -and $reports.generated.summary.contractDifferences -eq2 -and $reports.generated.summary.failed -eq0 -and $reports.generated.summary.blocked -eq0 -and $reports.generated.summary.skipped -eq0 -and $reports.generated.summary.expectedContractSatisfied -ceq $true -and @($reports.generated.cases).Count -eq28 -and $reports.aot.summary.expectedContractSatisfied -ceq $true -and $reports.aot.summary.denominator -eq28 -and $reports.aot.summary.executed -eq28 -and $reports.aot.summary.passed -eq28 -and @($reports.aot.cases).Count -eq28 -and $reports.callable.summary.expectedContractSatisfied -ceq $true -and $reports.callable.summary.denominator -eq7 -and $reports.callable.summary.artifactDenominator -eq2 -and $reports.callable.summary.passed -eq7 -and @($reports.callable.artifacts).Count -eq2) 'Actual v5/AOT/callable reports reduced or failed their exact cases.'
    foreach($case in @($reports.generated.cases)){Guard;Add-Witness 'source' $case.id $case.sourcePath $case.sourceSha256;Add-Witness 'original-pe' $case.id $case.rustSharpCompile.outputPath $case.rustSharpCompile.outputSha256;Add-Witness 'oracle' $case.id $case.rustcArtifact.path $case.rustcArtifact.sha256}
    foreach($case in @($reports.aot.cases)){Guard;Add-Witness 'native-aot' $case.id $case.nativeExecutablePath $case.nativeExecutableSha256;Add-Witness 'runtime' $case.id $case.runtimeAssemblyPath $case.runtimeAssemblySha256}
    foreach($artifact in @($reports.callable.artifacts)){Guard;Add-Witness 'source' $artifact.id $artifact.sourcePath $artifact.sourceSha256;Add-Witness 'original-pe' $artifact.id $artifact.generatedAssemblyPath $artifact.generatedAssemblySha256;Add-Witness 'runtime' $artifact.id $artifact.runtimeAssemblyPath $artifact.runtimeAssemblySha256;Add-Witness 'callable-host' $artifact.id $artifact.hostSourcePath $artifact.hostSourceSha256;Add-Witness 'callable-native' $artifact.id $artifact.nativeExecutablePath $artifact.nativeExecutableSha256}
    $expectedOriginals=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt28 -and $clock.Elapsed.TotalSeconds -lt90;$i++){Guard;$case=@($reports.generated.cases)[$i];$expectedOriginals.Add([pscustomobject]@{stage=('original-pe-'+$i.ToString('D2'));id=$case.id;path=$case.rustSharpCompile.outputPath;sha256=$case.rustSharpCompile.outputSha256})}
    for($i=0;$i -lt2 -and $clock.Elapsed.TotalSeconds -lt90;$i++){Guard;$artifact=@($reports.callable.artifacts)[$i];$expectedOriginals.Add([pscustomobject]@{stage=('callable-original-pe-'+$i.ToString('D2'));id=$artifact.id;path=$artifact.generatedAssemblyPath;sha256=$artifact.generatedAssemblySha256})}
    Need ($expectedOriginals.Count -eq30) 'Original PE expectation construction exceeded its count/time bounds.'
    $reportSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($expected in $expectedOriginals){
        Guard;$row=@($receipt.originalPeBindings|Where-Object stage -CEQ $expected.stage);Need ($row.Count -eq1) 'An original PE verifier stage was missing, duplicated or substituted.';$row=$row[0]
        $expectedReport='artifacts/p1-drop-ci/'+$PlatformName+'/'+$expected.stage+'.json'
        Need ($row.verifierReport.repositoryPath -ceq $expectedReport -and $reportSeen.Add($expectedReport) -and $row.verifierReport.sha256 -cmatch '^[A-Fa-f0-9]{64}$') 'Original verifier report path/hash was missing, duplicated or substituted.'
        $capture=Captured $expectedReport 4194304;Need ($capture.sha256 -ceq $row.verifierReport.sha256) 'Original ILVerify report bytes differ from their native receipt.'
        $relative=Get-P1DropOriginalRepositoryPath $expected.path $PlatformName;$witness=$seen[$relative]
        Test-P1DropOriginalPeBinding $row $capture.value $witness $expected.stage $expected.path $expected.sha256 $PlatformName
        $verifierReports.Add([pscustomobject]@{stage=$expected.stage;path=$capture.path;sha256=$capture.sha256})
        $originalPeBindings.Add([pscustomobject]@{stage=$expected.stage;caseId=$expected.id;path=$relative;sha256=$witness.sha256;bytes=$witness.bytes;verifierReport=[ordered]@{path=$capture.path;sha256=$capture.sha256};verifierVersion='10.0.11';physicalBytesVerified=$true})
    }
    for($i=0;$i -lt36 -and $clock.Elapsed.TotalSeconds -lt90;$i++){
        Guard;$stage=@($receipt.stages)[$i]
        $expectedStage=if($i -lt4){@('fresh-release','snapshot','generated-v5','generated-physical-validation')[$i]}elseif($i -lt32){'original-pe-'+($i-4).ToString('D2')}elseif($i -lt34){@('native-aot','callable')[$i-32]}else{'callable-original-pe-'+($i-34).ToString('D2')}
        Need ($stage.name -ceq $expectedStage) 'Original stage name/order differs from the fixed36 native ledger inventory.'
        $path='artifacts/p1-drop-ci/'+$PlatformName+'/'+$stage.name+'.process.json';$capture=Captured $path 65536;$process=$capture.value
        Need ($stage.status -ceq 'passed' -and ($stage.process|ConvertTo-Json -Depth 16 -Compress) -ceq ($process|ConvertTo-Json -Depth 16 -Compress) -and $process.Pid -gt0 -and $process.ParentPid -gt0 -and $process.ExitCode -eq0 -and $process.CleanupComplete -ceq $true -and -not $process.Failure) 'Original process ledger was incomplete or substituted.'
        $processFiles.Add([pscustomobject]@{path=$capture.path;sha256=$capture.sha256})
    }
    Need ($processFiles.Count -eq36 -and $originalPeBindings.Count -eq30 -and $verifierReports.Count -eq30 -and @($files|Where-Object kind -CEQ 'original-pe').Count -eq30 -and @($files|Where-Object kind -CEQ 'native-aot').Count -eq28 -and @($files|Where-Object kind -CEQ 'callable-native').Count -eq2) 'Fixed retained PE/native/callable/process/verifier inventory was reduced.'
    $sources=[Collections.Generic.List[object]]::new()
    foreach($path in @('eng/P1DropTransportContract.ps1','eng/Write-P1DropTransport.ps1','eng/Test-P1DropTransport.ps1')){
        Guard;$capture=Read-P1DropTransportBytes -Root $Root -Path $path -MaximumBytes 1048576 -Usage $usage -CancellationToken $CancellationToken -OverallClock $clock
        $snapshot=@($reports.snapshot.files|Where-Object path -CEQ $path);Need ($snapshot.Count -eq1 -and $snapshot[0].sha256 -ceq $capture.sha256) 'Transport implementation does not bind to the original candidate snapshot.';$sources.Add([pscustomobject]@{path=$path;sha256=$capture.sha256})
    }
    if($null-ne$TransportIndex){
        Need ($TransportIndex.candidateSha -ceq $CandidateSha -and $TransportIndex.candidateTreeSha -ceq $receipt.candidateTreeSha -and $TransportIndex.runtimeIdentifier -ceq $rid -and [string]$TransportIndex.runId -ceq $RunId -and [string]$TransportIndex.runAttempt -ceq $RunAttempt -and @($TransportIndex.files).Count -eq $files.Count) 'Native index was reduced, stale or platform/run mismatched.'
        foreach($pair in @(@{name='files';actual=$files.ToArray()},@{name='reports';actual=$bindings.ToArray()},@{name='processFiles';actual=$processFiles.ToArray()},@{name='sources';actual=$sources.ToArray()},@{name='originalPeBindings';actual=$originalPeBindings.ToArray()},@{name='verifierReports';actual=$verifierReports.ToArray()})){Guard;Need (($TransportIndex.($pair.name)|ConvertTo-Json -Depth 16 -Compress) -ceq ($pair.actual|ConvertTo-Json -Depth 16 -Compress)) 'Transported report/process/source/original-verifier bindings differ from actual original bytes.'}
        Need (($TransportIndex.receipt|ConvertTo-Json -Depth 8 -Compress) -ceq ([ordered]@{path=$capturedReceipt.path;sha256=$capturedReceipt.sha256}|ConvertTo-Json -Depth 8 -Compress)) 'Transported native receipt binding changed.'
    }
    Guard
    return [ordered]@{schemaVersion=1;evidenceKind=if($null-eq$TransportIndex){'p1-drop-native-transport-index'}else{'p1-drop-transport-validation'};candidateSha=$CandidateSha;candidateTreeSha=$receipt.candidateTreeSha;runtimeIdentifier=$rid;runId=$RunId;runAttempt=$RunAttempt;receipt=[ordered]@{path=$capturedReceipt.path;sha256=$capturedReceipt.sha256};reports=$bindings.ToArray();files=$files.ToArray();processFiles=$processFiles.ToArray();sources=$sources.ToArray();originalPeBindings=$originalPeBindings.ToArray();verifierReports=$verifierReports.ToArray();originalPeVerifierReports=30;artifactContentVerified=$true;actualNativeMachineVerified=$true;originalPeCount=30;nativeExecutableCases=28;callableArtifacts=2;callableCases=7;processLedgers=36;sourceBoundValidator=$true;processCleanupVerification='parent-exit-only';descendantContainmentVerified=$false;errors=@();fullP1Closure=$false;fullP1LanguageGateApproved=$false;bounds=[ordered]@{maximumFiles=512;maximumBytes=2147483648;deadlineSeconds=90;fileDeadlineSeconds=10;maximumJsonBytes=33554432}}
}
