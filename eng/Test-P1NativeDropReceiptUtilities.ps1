param([Parameter(Mandatory)][string]$ResultPath, [ValidateSet('Tiny','All')][string]$Mode='All')
$ErrorActionPreference='Stop';Set-StrictMode -Version 3.0
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PS7 required.'}
$dryOverall=[Diagnostics.Stopwatch]::StartNew()
$draft=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ResultPath));$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));if(-not[IO.Directory]::Exists($draft)-or[IO.File]::Exists($ResultPath)){throw 'Fresh result path and existing output parent required.'};$maximumControls=if($Mode-eq'Tiny'){1}else{8}
$sourcePath=Join-Path $PSScriptRoot 'Invoke-P1NativeDropEvidence.ps1'
$tokens=$null;$parseErrors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($sourcePath,[ref]$tokens,[ref]$parseErrors)
if(@($parseErrors).Count -gt 0){throw 'Native draft syntax failed before dry controls.'}
$wanted=@('Guard','Resolve-OwnedPath','Read-CapturedReport','Read-Report','Hash-OpenedPe','Publish-NativeReceipt')
$functions=@($ast.FindAll({param($node)$node-is[Management.Automation.Language.FunctionDefinitionAst]-and$node.Name-in$wanted},$true))
if($functions.Count -ne 6){throw 'Expected six bounded utility functions.'}
for($index=0;$index -lt 6-and$dryOverall.Elapsed.TotalSeconds -lt 5;$index++){. ([scriptblock]::Create($functions[$index].Extent.Text))}
$strictPath=Join-Path $repository 'eng/P1SuiteEvidenceValidation.ps1'
$strictAst=[Management.Automation.Language.Parser]::ParseFile($strictPath,[ref]$tokens,[ref]$parseErrors)
$strict=@($strictAst.FindAll({param($node)$node-is[Management.Automation.Language.FunctionDefinitionAst]-and$node.Name-ceq'ConvertFrom-P1StrictJson'},$true))
if($strict.Count -ne 1){throw 'Strict parser helper not uniquely addressable.'}
. ([scriptblock]::Create($strict[0].Extent.Text))
$sandbox=Join-Path $draft ('dry-owned-'+$PID+'-'+[Guid]::NewGuid().ToString('N'))
if(-not $sandbox.StartsWith($draft+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or[IO.Directory]::Exists($sandbox)){throw 'Dry sandbox must be fresh and owned.'}
[IO.Directory]::CreateDirectory($sandbox)|Out-Null
$root=$sandbox;$clock=[Diagnostics.Stopwatch]::StartNew();$CancellationToken=[Threading.CancellationToken]::None
$capturedReports=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal);$captureUsage=[pscustomobject]@{bytes=0L;reads=0}
$fixture=Join-Path $sandbox 'tiny.json';$proof=Join-Path $sandbox 'proof.json';$pe=Join-Path $sandbox 'bytes.bin'
$createdFiles=[Collections.Generic.List[string]]::new();$rows=[Collections.Generic.List[object]]::new()
function Create-Owned([string]$Path,[byte[]]$Bytes){$stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$createdFiles.Add($Path);try{$stream.Write($Bytes,0,$Bytes.Length)}finally{$stream.Dispose()}}
function Control([string]$Name,[string]$ExpectedMessage,[scriptblock]$Action){
    if($Mode-eq'Tiny'-and$rows.Count-ge1){return}
    if($rows.Count -ge 8-or$dryOverall.Elapsed.TotalSeconds -ge 45){throw 'Dry controls exceeded fixed count/45-second budget.'}
    $errorMessage=$null;$passed=$false
    try{&$Action;if(-not $ExpectedMessage){$passed=$true}}
    catch{$errorMessage=$_.Exception.Message;if($ExpectedMessage-and$errorMessage-match$ExpectedMessage){$passed=$true}}
    $rows.Add([pscustomobject]@{name=$Name;passed=$passed;expectedRejection=[bool]$ExpectedMessage;observed=$errorMessage})
}
$cleanupErrors=[Collections.Generic.List[string]]::new()
try{
    Create-Owned $fixture ([Text.Encoding]::UTF8.GetBytes('{"value":1}'))
    Create-Owned $pe ([byte[]]@(1,2,3,4))
    Control 'captured-hash-exact' '' {$capture=Read-CapturedReport 'tiny.json' 1024;if($capture.sha256-cne[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('{"value":1}')))-or$capture.document.value-ne1){throw 'Captured bytes/hash differ.'}}
    Control 'opened-byte-bound' 'Opened report exceeds byte bound' {$null=Read-CapturedReport 'tiny.json' 2}
    Control 'escaped-path-rejection' 'escaped repository' {$null=Resolve-OwnedPath ([IO.Path]::GetFullPath((Join-Path $root '..')))}
    $cancel=[Threading.CancellationTokenSource]::new()
    try{$cancel.Cancel();$CancellationToken=$cancel.Token;Control 'cancelled-read' 'canceled|cancelled' {$null=Read-CapturedReport 'tiny.json' 1024}}finally{$CancellationToken=[Threading.CancellationToken]::None;$cancel.Dispose()}
    Control 'same-opened-byte-hash' '' {$stream=[IO.FileStream]::new($pe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);try{$a=Hash-OpenedPe $stream;$b=Hash-OpenedPe $stream;if($a.sha256-cne$b.sha256-or$a.bytes-ne4){throw 'Opened-stream hash changed.'}}finally{$stream.Dispose()}}
    Control 'closeout-after-execution-deadline' '' {$clock=[pscustomobject]@{Elapsed=[TimeSpan]::FromSeconds(3545)};try{$null=Read-CapturedReport 'tiny.json' 1024 -Closeout}catch{throw}finally{$clock=[Diagnostics.Stopwatch]::StartNew()}}
    Control 'closeout-deadline-rejection' '3600 second' {$clock=[pscustomobject]@{Elapsed=[TimeSpan]::FromSeconds(3600)};try{Guard -Closeout}finally{$clock=[Diagnostics.Stopwatch]::StartNew()}}
    Control 'existing-proof-preserved' '' {
        $dryReceipt=@{evidenceKind='dry-control-only';runtimeExecuted=0;fullP1Closure=$false}
        Publish-NativeReceipt $dryReceipt $proof;$createdFiles.Add($proof)
        $before=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($proof)))
        $rejected=$false;try{Publish-NativeReceipt $dryReceipt $proof}catch{$rejected=$true}
        $after=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($proof)))
        $temporaries=@([IO.Directory]::EnumerateFiles($sandbox,'proof.json.tmp-*',[IO.SearchOption]::TopDirectoryOnly)|Select-Object -First 2)
        if(-not $rejected-or$before-cne$after-or$temporaries.Count-ne0){throw 'Earlier proof changed or owned temporary remained.'}
    }
}finally{
    for($index=0;$index -lt $createdFiles.Count-and$index -lt 3-and$dryOverall.Elapsed.TotalSeconds -lt 55;$index++){
        $full=[IO.Path]::GetFullPath($createdFiles[$index]);if(-not $full.StartsWith($sandbox+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Dry cleanup escaped owned sandbox.'}
        try{if([IO.File]::Exists($full)){[IO.File]::Delete($full)}}catch{$cleanupErrors.Add($_.Exception.Message)}
    }
    try{[IO.Directory]::Delete($sandbox,$false)}catch{$cleanupErrors.Add($_.Exception.Message)}
}
$report=[ordered]@{schemaVersion=1;status=if($rows.Count-eq$maximumControls-and@($rows|Where-Object passed -EQ $false).Count-eq0-and$cleanupErrors.Count-eq0){'passed'}else{'failed'};sourcePath=$sourcePath;sourceSha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash;dryOnly=$true;runtimeExecuted=0;phaseClosed=$false;nativeIlVerifyExecuted=0;nativeAotExecuted=0;controls=$rows.ToArray();count=$rows.Count;maximumControls=$maximumControls;maximumSeconds=60;elapsedSeconds=$dryOverall.Elapsed.TotalSeconds;cleanupErrors=$cleanupErrors.ToArray();ownedSandbox=$sandbox;sandboxRemoved=-not[IO.Directory]::Exists($sandbox);externalChildrenCreated=0}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),($report|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
$report|ConvertTo-Json -Depth 12
if($report.status-ne'passed'){exit 1}