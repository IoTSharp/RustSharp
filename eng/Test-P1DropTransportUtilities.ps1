param([Parameter(Mandatory)][string]$ResultPath,[ValidateSet('Tiny','All')][string]$Mode='All',[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
$ErrorActionPreference='Stop';Set-StrictMode -Version 3.0
if($PSVersionTable.PSVersion.Major -lt7){throw 'PS7 required.'}
$overall=[Diagnostics.Stopwatch]::StartNew();$draft=$PSScriptRoot
$output=[IO.Path]::GetFullPath($ResultPath,[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) 
if([IO.File]::Exists($output)){throw 'Prior utility report must not be overwritten.'}
$delivery=[IO.Path]::GetDirectoryName($output)
if(-not [IO.Directory]::Exists($delivery)){throw 'Utility result parent must already exist.'}
$maximumControls=if($Mode -ceq 'Tiny'){1}else{12}
. (Join-Path $draft 'P1DropTransportContract.ps1')
$sandbox=Join-Path $delivery ('utility-owned-'+[Guid]::NewGuid().ToString('N'))
if(-not $sandbox.StartsWith($delivery+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or[IO.Directory]::Exists($sandbox)){throw 'Fresh owned utility sandbox required.'}
[IO.Directory]::CreateDirectory($sandbox)|Out-Null
$created=[Collections.Generic.List[string]]::new();$rows=[Collections.Generic.List[object]]::new();$cleanupErrors=[Collections.Generic.List[string]]::new();$junction=$null;$junctionTarget=$null
function CreateOwned([string]$Relative,[byte[]]$Bytes){$full=Resolve-P1DropTransportPath $sandbox $Relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null;$stream=[IO.FileStream]::new($full,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$created.Add($full);try{$stream.Write($Bytes,0,$Bytes.Length)}finally{$stream.Dispose()};return $full}
function Check([string]$Name,[string]$Expected,[scriptblock]$Action){
    $CancellationToken.ThrowIfCancellationRequested()
    if($rows.Count -ge$maximumControls -or $overall.Elapsed.TotalSeconds -ge45){throw 'Utility controls exceeded12/45s.'}
    $observed=$null;$passed=$false;try{&$Action;if(-not$Expected){$passed=$true}}catch{$observed=$_.Exception.Message;if($Expected-and$observed-match$Expected){$passed=$true}}
    $rows.Add([pscustomobject]@{name=$Name;passed=$passed;expectedRejection=[bool]$Expected;observed=$observed})
}
try{
    $fixture=CreateOwned 'tiny.json' ([Text.Encoding]::UTF8.GetBytes('{"value":1}'))
    Check 'captured-opened-bytes' '' {$v=Read-P1DropTransportBytes -Root $sandbox -Path 'tiny.json' -MaximumBytes 64 -Capture;if($v.bytes-ne11 -or [Text.Encoding]::UTF8.GetString($v.content)-cne'{"value":1}'){throw 'Exact opened capture differs.'}}
    if ($Mode -ceq 'All') {
    Check 'opened-byte-bound' 'byte bound' {$null=Read-P1DropTransportBytes -Root $sandbox -Path 'tiny.json' -MaximumBytes 2 -Capture}
    Check 'shared-byte-bound' 'shared bound' {$null=Read-P1DropTransportBytes -Root $sandbox -Path 'tiny.json' -MaximumBytes 64 -Usage ([pscustomobject]@{bytes=2147483648L})}
    $cancel=[Threading.CancellationTokenSource]::new();try{$cancel.Cancel();Check 'pre-cancelled-read' 'canceled|cancelled' {$null=Read-P1DropTransportBytes -Root $sandbox -Path 'tiny.json' -MaximumBytes 64 -CancellationToken $cancel.Token}}finally{$cancel.Dispose()}
    Check 'path-escape' 'escaped the repository' {$null=Resolve-P1DropTransportPath $sandbox (Join-Path $sandbox '..')}
    $junctionTarget=Join-Path $sandbox 'link-target';[IO.Directory]::CreateDirectory($junctionTarget)|Out-Null;$linkedFile=CreateOwned 'link-target/tiny.json' ([byte[]]@(1))
    $junction=Join-Path $sandbox 'link-parent';$linkType=if($IsWindows){'Junction'}else{'SymbolicLink'};$null=New-Item -ItemType $linkType -Path $junction -Target $junctionTarget
    Check 'ancestor-reparse-rejection' 'parent link' {$null=Read-P1DropTransportBytes -Root $sandbox -Path 'link-parent/tiny.json' -MaximumBytes 64}
    $hash='A'*64;$binding=[pscustomobject]@{stage='original-pe-00';path='artifacts/p1-drop/windows-x64/case/program.dll';sha256=$hash;bytes=4;sha256AfterVerification=$hash;retainedPathSha256After=$hash;verifierVersion='10.0.11';physicalBytesVerified=$true}
    $witness=[pscustomobject]@{path=$binding.path;sha256=$hash;bytes=4}
    $verifier=[pscustomobject]@{Succeeded=$true;Assembly=[pscustomobject]@{Path='D:/a/RustSharp/RustSharp/'+$binding.path;Sha256=$hash;Length=4};Tool=[pscustomobject]@{Version='10.0.11'}}
    Check 'portable-original-path-identity' '' {Test-P1DropOriginalPeBinding $binding $verifier $witness 'original-pe-00' ('D:/a/RustSharp/RustSharp/'+$binding.path) $hash 'windows-x64'}
    Check 'verifier-length-tamper' 'did not reconcile' {$verifier.Assembly.Length=5;try{Test-P1DropOriginalPeBinding $binding $verifier $witness 'original-pe-00' $binding.path $hash 'windows-x64'}finally{$verifier.Assembly.Length=4}}
    Check 'verifier-version-tamper' 'did not reconcile' {$verifier.Tool.Version='10.0.10';try{Test-P1DropOriginalPeBinding $binding $verifier $witness 'original-pe-00' $binding.path $hash 'windows-x64'}finally{$verifier.Tool.Version='10.0.11'}}
    Check 'verifier-path-tamper' 'did not reconcile' {$verifier.Assembly.Path='E:/other/RustSharp/'+$binding.path;try{Test-P1DropOriginalPeBinding $binding $verifier $witness 'original-pe-00' ('D:/a/RustSharp/RustSharp/'+$binding.path) $hash 'windows-x64'}finally{$verifier.Assembly.Path='D:/a/RustSharp/RustSharp/'+$binding.path}}
    $destination='artifacts/p1-candidate/drop-transport-windows-x64.json';$full=Join-Path $sandbox $destination
    Check 'atomic-existing-proof-preserved' '' {
        Write-P1DropTransportProof $sandbox $destination @{evidenceKind='utility-only';runtimeExecuted=0;fullP1Closure=$false};$created.Add($full)
        $before=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($full)))
        $rejected=$false;try{Write-P1DropTransportProof $sandbox $destination @{different='utility-only'}}catch{$rejected=$true}
        $after=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($full)))
        if(-not$rejected-or$before-cne$after-or@([IO.Directory]::EnumerateFiles([IO.Path]::GetDirectoryName($full),'*.owned-*',[IO.SearchOption]::TopDirectoryOnly)|Select-Object -First 2).Count-ne0){throw 'Proof changed or temporary remained.'}
    }
    Check 'pre-cancelled-publish-no-final' '' {
        $c=[Threading.CancellationTokenSource]::new();$c.Cancel();$other='artifacts/p1-candidate/drop-transport-linux-x64.json';$rejected=$false
        try{try{Write-P1DropTransportProof $sandbox $other @{utility=$true} $c.Token}catch{$rejected=$true};if(-not$rejected-or[IO.File]::Exists((Join-Path $sandbox $other))){throw 'Cancelled publication created final proof.'}}finally{$c.Dispose()}
    }
    }
}finally{
    $cleanupClock=[Diagnostics.Stopwatch]::StartNew()
    if($null-ne$junction-and[IO.Directory]::Exists($junction)){try{$info=[IO.DirectoryInfo]::new($junction);if(($info.Attributes-band[IO.FileAttributes]::ReparsePoint)-eq0-or[IO.Path]::GetFullPath($info.LinkTarget)-cne$junctionTarget){throw 'Owned utility junction identity changed.'};[IO.Directory]::Delete($junction,$false)}catch{$cleanupErrors.Add($_.Exception.Message)}}
    for($i=0;$i -lt $created.Count -and $i -lt4 -and $cleanupClock.Elapsed.TotalSeconds -lt5;$i++){try{$full=Resolve-P1DropTransportPath $sandbox $created[$i];if([IO.File]::Exists($full)){[IO.File]::Delete($full)}}catch{$cleanupErrors.Add($_.Exception.Message)}}
    foreach($relative in @('artifacts/p1-candidate','artifacts','link-target')){if($cleanupClock.Elapsed.TotalSeconds-ge5){$cleanupErrors.Add('Owned directory cleanup deadline expired.');break};try{$full=Resolve-P1DropTransportPath $sandbox $relative;if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$false)}}catch{$cleanupErrors.Add($_.Exception.Message)}}
    try{[IO.Directory]::Delete($sandbox,$false)}catch{$cleanupErrors.Add($_.Exception.Message)}
}
$report=[ordered]@{schemaVersion=1;evidenceKind='p1-transport-utility-controls';mode=$Mode;status=if($rows.Count-eq$maximumControls-and@($rows|Where-Object passed -EQ $false).Count-eq0-and$cleanupErrors.Count-eq0){'passed'}else{'failed'};utilityOnly=$true;runtimeExecuted=0;nativeIlVerifyExecuted=0;nativeAotExecuted=0;formalJointControlsExecuted=0;fullP1Closure=$false;expectedControls=$maximumControls;maximumControls=12;maximumSeconds=60;elapsedSeconds=$overall.Elapsed.TotalSeconds;controls=$rows.ToArray();ownedSandbox=$sandbox;sandboxRemoved=-not[IO.Directory]::Exists($sandbox);cleanupErrors=$cleanupErrors.ToArray();externalChildrenCreated=0}
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 12));$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()};$report|ConvertTo-Json -Depth 12
if($report.status -cne 'passed'){exit 1}
