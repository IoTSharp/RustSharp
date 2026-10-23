# Read retained originals as metadata only; no assembly loading or execution.
[CmdletBinding()]
param([switch]$Trial,[string]$OutputPath,[string]$TrialReportPath,[ValidateRange(1,120)][int]$TimeoutSeconds=60,[string]$CancelFile)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
$repo=Split-Path $PSScriptRoot -Parent
$packet=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v30'
[IO.Directory]::CreateDirectory($packet)|Out-Null
if(-not $OutputPath){$OutputPath=Join-Path $packet $(if($Trial){'tiny1.json'}else{'semantic1.json'})}
$output=[IO.Path]::GetFullPath($OutputPath)
if(-not $output.StartsWith($packet+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or [IO.File]::Exists($output)){throw 'Exclusive new output required'}
$clock=[Diagnostics.Stopwatch]::StartNew();$checks=[Collections.Generic.List[object]]::new();$issues=[Collections.Generic.List[object]]::new();$rows=[Collections.Generic.List[object]]::new();$controls=[Collections.Generic.List[object]]::new();$failure=$null
$hostIdentity=Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5
function Bound {if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){throw 'Wall timeout'};if($CancelFile -and [IO.File]::Exists($CancelFile)){throw 'Cancelled'}}
function Hash([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'Input32MiB cap'};return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function ReadJson([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'JSON32MiB cap'};return [IO.File]::ReadAllText($p)|ConvertFrom-Json -Depth 100 -DateKind String}
function Check([string]$id,$expected,$actual){Bound;$ok=([string]$expected -ceq [string]$actual);$r=[pscustomobject]@{id=$id;expected=$expected;actual=$actual;passed=$ok};$checks.Add($r);if(-not $ok){$issues.Add($r)}}
function ContractType([string]$t){switch($t){'Void'{return 'void'}'Any'{return 'object'}'I32'{return 'i4'}'Bool'{return 'bool'}default{if($t -match '^Value\((?:[^:]+::)?(?<n>[^)]+)\)$'){return 'value:'+$Matches.n};throw ('Unreviewed contract type '+$t)}}}
function ActualType([string]$t){if($t -match '^(?:class|valuetype)\(RustSharp\.Generated\.Values\.(?<n>[^)]+)\)$'){return 'value:'+$Matches.n};return $t}
function ContractSignature([string]$s){$parts=$s.Split('->');if($parts.Count -ne 2){throw 'Invalid contract signature'};$types=if($parts[0]){@($parts[0].Split(',')|ForEach-Object {ContractType $_})}else{@()};return (($types -join ',')+'->'+(ContractType $parts[1]))}
function ActualSignature($s){return ((@($s.parameters|ForEach-Object {ActualType $_}) -join ',')+'->'+(ActualType $s.returnType))}
function MatchFunction($function,$defs){$found=@($defs|Where-Object {$_.type -ceq 'RustSharp.Generated.Program' -and $_.name -ceq $function.name});if($found.Count -ne 1){return $false};$d=$found[0];$public=$d.attributes.Contains('Public');return ($public -eq [bool]$function.isPublic -and (ContractSignature $function.signature) -ceq (ActualSignature $d.signature))}
try {
 $helper=Join-Path $repo 'eng/Audit-P1RetainedPayloadV3.ps1'
 Check 'immutable decoder source' '583B6FA32D203FD6FB55BE1578A91E6678D96CE2AE052EB288486D08674525CE' (Hash $helper)
 if($issues.Count){throw 'Decoder source mismatch'}
 $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
 if($errors.Count){throw 'Prior decoder syntax invalid'}
 # Exactly five inspected pure decoder functions; no prior audit top-level execution.
 $wanted=@('Byte','Compressed','TypeName','SigType','Signature')
 foreach($name in $wanted){Bound;$nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object {$_.Name -ceq $name});if($nodes.Count -ne 1){throw 'Decoder function missing'};. ([scriptblock]::Create($nodes[0].Extent.Text))}
 $prior=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v29/root-semantic1.json'
 Check 'root accepted original report' '5DC697DFAD6BB8ADC4793A21B1798F62D1D2DB0754F2E51F821DFF500257F90E' (Hash $prior)
 $baseline=ReadJson $prior
 Check 'candidate' 'afb0988bb74e698a685adde93bacdd2b82b8c932' $baseline.candidateSha
 Check 'tree' '387d2ae7af9bfbd1a963dff481de3968fb7c24ae' $baseline.treeSha
 Check 'run' '37710305713' $baseline.runId
 Check 'attempt' 1 $baseline.attempt
 Check 'source PE denominator' 78 @($baseline.metadata|Where-Object {$_.label.StartsWith('source/')} ).Count
 $inventory=ReadJson (Join-Path $repo 'artifacts/p1-supervision/ci-followup-v25/payload-static-inventory.json')
 $metadataFiles=@($inventory.files|Where-Object {$_.name -match '(producer|consumer|wrapper)-metadata-build-0\.json$'})
 Check 'metadata source denominator' 78 $metadataFiles.Count
 if(-not $Trial){$tiny=ReadJson $TrialReportPath;if(-not $tiny.trialOnly -or $tiny.status -cne 'passed' -or $tiny.peCount -ne 1 -or $tiny.negativeControls.Count -ne 4){throw 'Actual tiny and four negative controls prerequisite required'}}
 $limit=if($Trial){1}else{78}
 # Reviewed comparisons: at most78 originals,4096methods/refs,256contracts each;60s/cancel per item.
 for($i=0;$i -lt $limit -and $i -lt 78;$i++) {
  Bound;$file=$metadataFiles[$i];Check ($file.name+' metadata original SHA') $file.sha256 (Hash $file.path);$meta=ReadJson $file.path
  $match=[regex]::Match($file.name,'/(?<case>[^/]+)/(?<role>producer|consumer|wrapper)-metadata-build-0\.json$');if(-not $match.Success){throw 'Unrecognized metadata path'}
  $caseId=$match.Groups['case'].Value;$role=$match.Groups['role'].Value;$rid=if($file.platform -ceq 'windows-x64'){'win-x64'}else{'linux-x64'};$label='source/'+$rid+'/'+$caseId+' '+(Get-Culture).TextInfo.ToTitleCase($role)
  $original=@($baseline.metadata|Where-Object {$_.label -ceq $label});if($original.Count -ne 1){throw 'Exact source original absent'};$original=$original[0]
  Check ($label+' actual original PE SHA') $original.sha256 (Hash $original.path)
  $fs=[IO.File]::OpenRead($original.path);$pe=[Reflection.PortableExecutable.PEReader]::new($fs)
  try {
   $m=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);if($m.MethodDefinitions.Count -gt 4096 -or $m.MemberReferences.Count -gt 4096 -or $meta.functions.Count -gt 256){throw 'Metadata count cap'}
   $defs=[Collections.Generic.List[object]]::new();$refs=[Collections.Generic.List[object]]::new()
   foreach($th in $m.TypeDefinitions){Bound;$td=$m.GetTypeDefinition($th);$tn=$m.GetString($td.Namespace)+'.'+$m.GetString($td.Name);foreach($dh in $td.GetMethods()){Bound;$d=$m.GetMethodDefinition($dh);$defs.Add([pscustomobject]@{type=$tn;name=$m.GetString($d.Name);attributes=[string]$d.Attributes;signature=Signature $m $d.Signature})}}
   foreach($fn in $meta.functions){Bound;Check ($label+' export '+$fn.sourceQualifiedName+' '+$fn.name) $true (MatchFunction $fn $defs)}
   Check ($label+' exact generated function surface') $meta.functions.Count @($defs|Where-Object {$_.type -ceq 'RustSharp.Generated.Program'}).Count
   $sourceFiles=@($inventory.files|Where-Object {$_.platform -ceq $file.platform -and $_.name -like ('*/'+$caseId+'/build-0/'+$role+'.rs')});if($sourceFiles.Count -ne 1){throw 'Original source identity absent'}
   Check ($label+' contract source SHA') $meta.sourceSha256 (Hash $sourceFiles[0].path)
   foreach($rh in $m.MemberReferences){Bound;$r=$m.GetMemberReference($rh);if($r.Parent.Kind -eq [Reflection.Metadata.HandleKind]::TypeReference){$tr=$m.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$r.Parent);if($tr.ResolutionScope.Kind -eq [Reflection.Metadata.HandleKind]::AssemblyReference){$ar=$m.GetAssemblyReference([Reflection.Metadata.AssemblyReferenceHandle]$tr.ResolutionScope);$asm=$m.GetString($ar.Name);if($asm -in @('SourceProducer','SourceWrapper')){$target=@($baseline.metadata|Where-Object {$_.label.StartsWith('source/'+$rid+'/'+$caseId+' ') -and $_.assembly -ceq $asm});if($target.Count -ne 1){throw 'Original imported assembly identity absent'};$target=$target[0];Check ($label+' producer hash '+$asm) $target.sha256 (Hash $target.path);$type=$m.GetString($tr.Namespace)+'.'+$m.GetString($tr.Name);$name=$m.GetString($r.Name);$sig=Signature $m $r.Signature;$definition=@($target.definitions|Where-Object {$_.type -ceq $type -and $_.name -ceq $name -and (ActualSignature $_.signature) -ceq (ActualSignature $sig) -and $_.attributes.Contains('Public')});Check ($label+' actual import '+$asm+'::'+$type+'::'+$name) 1 $definition.Count;$refs.Add([pscustomobject]@{assembly=$asm;originalProducerPath=$target.path;originalProducerSha256=$target.sha256;type=$type;name=$name;signature=$sig})}}}}
   if($Trial){$fn=$meta.functions[0];foreach($mutation in @('missing','type','visibility','call-replacement')){Bound;$copy=$fn|ConvertTo-Json -Depth 10|ConvertFrom-Json -Depth 10;switch($mutation){'missing'{$copy.name='__absent__'}'type'{$copy.signature='I32->Bool'}'visibility'{$copy.isPublic=-not $copy.isPublic}'call-replacement'{$copy.name='__replaced_call__'}};$rejected=-not (MatchFunction $copy $defs);Check ('negative '+$mutation) $true $rejected;$controls.Add([pscustomobject]@{mutation=$mutation;rejected=$rejected;originalsModified=$false})}}
   $rows.Add([pscustomobject]@{label=$label;originalPePath=$original.path;originalPeSha256=$original.sha256;metadataPath=$file.path;metadataSha256=$file.sha256;sourcePath=$sourceFiles[0].path;sourceSha256=$meta.sourceSha256;functions=@($meta.functions);actualImports=@($refs.ToArray());fullMirSemanticsAccepted=$false})
  }finally{$pe.Dispose();$fs.Dispose()}
  if($i%10 -eq 0){Write-Output ('signature originals '+($i+1)+'/'+$limit)}
 }
}catch{$failure=$_.Exception.Message}
finally {
 $report=[ordered]@{schemaVersion=4;taskId='P1-GATE.03.CISEM-v1';status=if($failure-or$issues.Count){'failed'}else{'passed'};trialOnly=[bool]$Trial;sourceSha256=Hash $PSCommandPath;candidateSha='afb0988bb74e698a685adde93bacdd2b82b8c932';treeSha='387d2ae7af9bfbd1a963dff481de3968fb7c24ae';runId='37710305713';attempt=1;caseDenominator=52;backendMatrixDenominator=24;sourcePackagesEach=19;sourcePeEach=39;sourceRetainedEach=785;backendRetainedEach=103;sourceFileDenominator=1572;backendFileDenominator=234;peCount=$rows.Count;checks=@($checks.ToArray());issues=@($issues.ToArray());failure=$failure;originals=@($rows.ToArray());negativeControls=@($controls.ToArray());completeSemanticsAccepted=$false;formalJointAccepted=$false;fullP1Closure=$false;fullP2Closure=$false;pending=@('Full source-to-MIR expected semantics including call replacement/ABI/layout/generic contracts','Backend14 originals/24cells semantic expectations','Instruction-level original call operands and upstream source visibility','Independent root acceptance and original containment');expandedSdk='10.0.400';productionSdk='10.0.401';maximumSeconds=$TimeoutSeconds;elapsedMilliseconds=$clock.ElapsedMilliseconds;maximumItems=78;externalChildrenLaunched=0;networkRequests=0;objectsDeleted=0;hostIdentity=($hostIdentity|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine);finishedAt=[DateTimeOffset]::Now.ToString('O')}
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 100));if($bytes.Length -gt 8388608){throw 'Output8MiB cap'};$o=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$o.Write($bytes)}finally{$o.Dispose()}
}
if($failure){throw $failure};if($issues.Count){throw ('Signature issues='+$issues.Count)}
Write-Output ('pe='+$rows.Count+' checks='+$checks.Count)
