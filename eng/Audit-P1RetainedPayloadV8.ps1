# Incremental metadata-byte controls. Retained assemblies are data, never loaded/executed.
[CmdletBinding()]
param([switch]$Trial,[string]$OutputPath,[string]$TrialReportPath,[ValidateRange(1,120)][int]$TimeoutSeconds=60,[string]$CancelFile)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
$repo=Split-Path $PSScriptRoot -Parent
$packet=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v34'
[IO.Directory]::CreateDirectory($packet)|Out-Null
if(-not $OutputPath){$OutputPath=Join-Path $packet $(if($Trial){'tiny1.json'}else{'semantic1.json'})}
$output=[IO.Path]::GetFullPath($OutputPath)
if(-not $output.StartsWith($packet+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or [IO.File]::Exists($output)){throw 'Exclusive new output required'}
$clock=[Diagnostics.Stopwatch]::StartNew();$checks=[Collections.Generic.List[object]]::new();$issues=[Collections.Generic.List[object]]::new();$rows=[Collections.Generic.List[object]]::new();$controls=[Collections.Generic.List[object]]::new();$failure=$null
$hostIdentity=Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5
$parentChain=[Collections.Generic.List[object]]::new();$parentPid=$hostIdentity.ParentProcessId
for($p=0;$p -lt 4 -and $parentPid -gt 0 -and $clock.Elapsed.TotalSeconds -lt 20;$p++){if($CancelFile -and [IO.File]::Exists($CancelFile)){break};$parent=Get-CimInstance Win32_Process -Filter "ProcessId=$parentPid" -OperationTimeoutSec 3;if(-not $parent){break};$parentChain.Add(($parent|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine));$parentPid=$parent.ParentProcessId}
function Bound {if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){throw 'Wall timeout'};if($CancelFile -and [IO.File]::Exists($CancelFile)){throw 'Cancelled'};if($checks.Count -ge 30000){throw 'Check cap'}}
function Hash([string]$path){Bound;if(([IO.FileInfo]::new($path)).Length -gt 33554432){throw 'Input32MiB cap'};return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function ReadJson([string]$path){Bound;if(([IO.FileInfo]::new($path)).Length -gt 33554432){throw 'JSON32MiB cap'};return [IO.File]::ReadAllText($path)|ConvertFrom-Json -Depth 100 -DateKind String}
function Check([string]$id,$expected,$actual){Bound;$ok=([string]$expected -ceq [string]$actual);$r=[pscustomobject]@{id=$id;expected=$expected;actual=$actual;passed=$ok};$checks.Add($r);if(-not $ok){$issues.Add($r)}}
function AssemblyId($m,$a,[bool]$definition=$false){
 $key=if($definition){$m.GetBlobBytes($a.PublicKey)}else{$m.GetBlobBytes($a.PublicKeyOrToken)};$token=if($key){[Convert]::ToHexString($key)}else{''}
 if($key.Length -and ($definition -or (([int]$a.Flags -band 1) -ne 0))){$digest=[Security.Cryptography.SHA1]::HashData($key);$last=[byte[]]$digest[12..19];[Array]::Reverse($last);$token=[Convert]::ToHexString($last)}
 return ($m.GetString($a.Name)+'|'+$a.Version+'|'+$m.GetString($a.Culture)+'|'+$token)
}
try{
 $prior=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v33/root-semantic1.json';Check 'immutable root V7 report' '69A3FC4EFBE77F85B0BEA6B6D2E84BCA0A0C5E135A2D0265640D08A5541F7730' (Hash $prior);$baseline=ReadJson $prior
 Check 'V7 status' 'passed' $baseline.status;Check 'V7 original source PE denominator' 78 $baseline.peCount;Check 'V7 reused IL checks' 1692 $baseline.checks.Count;Check 'V7 issues' 0 $baseline.issues.Count
 Check 'candidate' 'afb0988bb74e698a685adde93bacdd2b82b8c932' $baseline.candidateSha;Check 'tree' '387d2ae7af9bfbd1a963dff481de3968fb7c24ae' $baseline.treeSha;Check 'run' '37710305713' $baseline.runId;Check 'attempt' 1 $baseline.attempt
 if($issues.Count){throw 'Immutable prerequisites differ'}
 if(-not $Trial){$tiny=ReadJson $TrialReportPath;if(-not $tiny.trialOnly -or $tiny.status -cne 'passed' -or $tiny.peCount -ne 1 -or $tiny.negativeControls.Count -ne 1 -or $tiny.sourceSha256 -cne (Hash $PSCommandPath)){throw 'Same-source tiny byte control prerequisite'}}
 $limit=if($Trial){1}else{78}
 # Reviewed comparison exits: <=78 PE, <=256 AssemblyRef rows, <=32MiB inputs; wall/cancel on every row.
 for($i=0;$i -lt $limit -and $i -lt 78;$i++){
  Bound;$original=$baseline.originals[$i];Check ($original.label+' original PE hash before') $original.originalPeSha256 (Hash $original.originalPePath)
  $bytes=[IO.File]::ReadAllBytes($original.originalPePath);$stream=[IO.MemoryStream]::new($bytes,$false);$pe=[Reflection.PortableExecutable.PEReader]::new($stream)
  try{
   $m=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);if($m.AssemblyReferences.Count -gt 256){throw 'AssemblyRef count cap'}
   $refRows=[Collections.Generic.List[object]]::new();$producerControl=$false
   foreach($rh in $m.AssemblyReferences){
    Bound;$ar=$m.GetAssemblyReference($rh);$name=$m.GetString($ar.Name);if($name -notin @('SourceProducer','SourceWrapper')){continue}
    $casePrefix=$original.label.Substring(0,$original.label.LastIndexOf(' '));$targets=@($baseline.originals|Where-Object {$_.label.StartsWith($casePrefix+' ') -and [IO.Path]::GetFileNameWithoutExtension($_.originalPePath) -ceq $name});if($targets.Count -ne 1){throw 'Exact retained producer absent'};$producer=$targets[0]
    Check ($original.label+' producer original PE hash') $producer.originalPeSha256 (Hash $producer.originalPePath)
    $ps=[IO.File]::OpenRead($producer.originalPePath);$pp=[Reflection.PortableExecutable.PEReader]::new($ps)
    try{$pm=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pp);$expected=AssemblyId $pm ($pm.GetAssemblyDefinition()) $true;$actual=AssemblyId $m $ar;Check ($original.label+' exact AssemblyRef binds producer') $expected $actual}finally{$pp.Dispose();$ps.Dispose()}
    $row=[Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$rh)
    $tableOffset=[Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($m,[Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef)
    $rowSize=[Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($m,[Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef)
    $at=$pe.PEHeaders.MetadataStartOffset+$tableOffset+($row-1)*$rowSize
    if($at -lt 0 -or $at+8 -gt $bytes.Length -or $rowSize -lt 12){throw 'AssemblyRef original table bounds'}
    Check ($original.label+' original table major version bytes') $ar.Version.Major ([BitConverter]::ToUInt16($bytes,$at))
    if(-not $producerControl){
     $clone=[byte[]]$bytes.Clone();$before=$clone[$at];$clone[$at]=$before -bxor 1
     $cs=[IO.MemoryStream]::new($clone,$false);$cp=[Reflection.PortableExecutable.PEReader]::new($cs)
     try{$cm=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($cp);$changed=$cm.GetAssemblyReference([Reflection.Metadata.Ecma335.MetadataTokens]::AssemblyReferenceHandle($row));$mutated=AssemblyId $cm $changed;$rejected=$expected -cne $mutated;Check ($original.label+' negative actual PE AssemblyRef metadata byte binding') $true $rejected;Check ($original.label+' mutation parsed same producer name') $name ($cm.GetString($changed.Name));Check ($original.label+' actual single byte changed') $true ($clone[$at] -ne $bytes[$at]);$controls.Add([pscustomobject]@{label=$original.label;mutation='actualPeAssemblyRefMetadataByte';row=$row;table='AssemblyRef';tableRowSize=$rowSize;metadataOffset=$pe.PEHeaders.MetadataStartOffset;tableOffset=$tableOffset;absoluteByteOffset=$at;before=$before;after=$clone[$at];originalIdentity=$actual;expectedProducerIdentity=$expected;mutatedIdentity=$mutated;rejectedByExactProducerBinding=$rejected;validMetadataParsed=$true;changedByteCount=1;originalsModified=$false;cloneSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($clone))})}finally{$cp.Dispose();$cs.Dispose()}
     $producerControl=$true
    }
    $refRows.Add([pscustomobject]@{row=$row;identity=$actual;producerSha256=$producer.originalPeSha256;producerPath=$producer.originalPePath})
   }
   $rows.Add([pscustomobject]@{label=$original.label;originalPePath=$original.originalPePath;originalPeSha256=$original.originalPeSha256;exactProducerReferences=@($refRows.ToArray());byteNegativePresent=$producerControl;fullAbiSemanticsAccepted=$false;classValueTypeEquated=$false})
  }finally{$pe.Dispose();$stream.Dispose()}
  Check ($original.label+' original PE hash after clone disposal') $original.originalPeSha256 (Hash $original.originalPePath)
  if($i%10 -eq 0){Write-Output ('AssemblyRef originals '+($i+1)+'/'+$limit)}
 }
 if($Trial){Check 'tiny real byte negative count' 1 $controls.Count}else{Check 'full source PE denominator' 78 $rows.Count;Check 'full actual PE byte negative present' $true ($controls.Count -gt 0)}
}catch{$failure=$_.Exception.Message}
finally{
 # Direct hash has no Bound call: timeout/cancel must never replace the primary failure or suppress its report.
 $sourceHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
 $report=[ordered]@{schemaVersion=8;taskId='P1-GATE.03.CISEM-v1';status=if($failure-or$issues.Count){'failed'}else{'passed'};trialOnly=[bool]$Trial;sourceSha256=$sourceHash;candidateSha='afb0988bb74e698a685adde93bacdd2b82b8c932';treeSha='387d2ae7af9bfbd1a963dff481de3968fb7c24ae';runId='37710305713';attempt=1;caseDenominator=52;backendMatrixDenominator=24;backendPeDenominator=14;sourcePackagesEach=19;sourcePeEach=39;sourceRetainedEach=785;backendRetainedEach=103;sourceFileDenominator=1572;backendFileDenominator=234;physicalFileDenominator=1806;peCount=$rows.Count;checks=@($checks.ToArray());issues=@($issues.ToArray());failure=$failure;originals=@($rows.ToArray());negativeControls=@($controls.ToArray());reusedV7Checks=1692;reusedV7ReportSha256='69A3FC4EFBE77F85B0BEA6B6D2E84BCA0A0C5E135A2D0265640D08A5541F7730';completeSemanticsAccepted=$false;formalJointAccepted=$false;fullP1Closure=$false;fullP2Closure=$false;pending=@('NOT_ACCEPTED: full source-MIR expected call/ABI/panic semantics','NOT_ACCEPTED: actual class/valueType BaseType/layout/field semantics against frozen MIR','NOT_ACCEPTED: generic MethodSpec scoped substitution','NOT_ACCEPTED: backend14/24cell/cases52 semantic expectations','NOT_ACCEPTED: independent root acceptance and full containment');expandedSdk='10.0.400';productionSdk='10.0.401';maximumSeconds=$TimeoutSeconds;elapsedMilliseconds=$clock.ElapsedMilliseconds;maximumItems=78;externalChildrenLaunched=0;networkRequests=0;objectsDeleted=0;hostIdentity=($hostIdentity|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine);parentChain=@($parentChain.ToArray());finishedAt=[DateTimeOffset]::Now.ToString('O')}
 $out=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 100));if($out.Length -gt 16777216){throw 'Output16MiB cap'};$file=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$file.Write($out)}finally{$file.Dispose()}
}
if($failure){throw $failure};if($issues.Count){throw ('AssemblyRef issues='+$issues.Count)}
Write-Output ('pe='+$rows.Count+' checks='+$checks.Count+' byteControls='+$controls.Count)
