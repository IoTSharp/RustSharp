# Data-only exact-candidate declaration audit; retained PE images are never executed or loaded.
[CmdletBinding()]param([switch]$Trial,[string]$OutputPath,[string]$TrialReportPath,[ValidateRange(1,120)][int]$TimeoutSeconds=60,[string]$CancelFile)
$ErrorActionPreference='Stop';if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
$repo=Split-Path $PSScriptRoot -Parent;$packet=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v39';[IO.Directory]::CreateDirectory($packet)|Out-Null
if(-not $OutputPath){$OutputPath=Join-Path $packet $(if($Trial){'tiny1.json'}else{'semantic1.json'})};$output=[IO.Path]::GetFullPath($OutputPath)
if(-not $output.StartsWith($packet+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or [IO.File]::Exists($output)){throw 'Exclusive new output required'}
$clock=[Diagnostics.Stopwatch]::StartNew();$checks=[Collections.Generic.List[object]]::new();$rows=[Collections.Generic.List[object]]::new();$controls=[Collections.Generic.List[object]]::new();$pending=[Collections.Generic.List[string]]::new();$failure=$null
function Bound{if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){throw 'Wall timeout'};if($CancelFile -and [IO.File]::Exists($CancelFile)){throw 'Cancelled'};if($checks.Count -ge 30000){throw 'Check cap'}}
function Hash([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'Input32MiB cap'};(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Json([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'JSON32MiB cap'};[IO.File]::ReadAllText($p)|ConvertFrom-Json -Depth 100 -DateKind String}
function Check([string]$id,$expected,$actual){Bound;$checks.Add([pscustomobject]@{id=$id;expected=$expected;actual=$actual;passed=([string]$expected -ceq [string]$actual)})}
function ScopedBase($m,$h){$h=[Reflection.Metadata.EntityHandle]$h;if($h.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference){return ('Unexpected:'+ $h.Kind)};$t=$m.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$h);$s=[Reflection.Metadata.EntityHandle]$t.ResolutionScope;if($s.Kind -ne [Reflection.Metadata.HandleKind]::AssemblyReference){return ('UnexpectedScope:'+$s.Kind)};$a=$m.GetAssemblyReference([Reflection.Metadata.AssemblyReferenceHandle]$s);return ($m.GetString($a.Name)+'|'+$a.Version+'|'+$m.GetString($a.Culture)+'|'+[Convert]::ToHexString($m.GetBlobBytes($a.PublicKeyOrToken))+'|'+[int]$a.Flags+'::'+$m.GetString($t.Namespace)+'.'+$m.GetString($t.Name))}
function Shape([byte[]]$bytes){
 $st=[IO.MemoryStream]::new($bytes,$false);$pe=[Reflection.PortableExecutable.PEReader]::new($st)
 try{
  $m=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
  if($m.TypeDefinitions.Count -gt 4096 -or $m.TypeReferences.Count -gt 4096 -or $m.FieldDefinitions.Count -gt 4096 -or $m.MethodDefinitions.Count -gt 4096){throw 'Metadata4096 cap'}
  $types=[Collections.Generic.List[object]]::new();$objectCoded=$null
  foreach($rh in $m.TypeReferences){Bound;if((ScopedBase $m $rh) -ceq 'System.Runtime|10.0.0.0||B03F5F7F11D50A3A|0::System.Object'){$objectCoded=4*[Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$rh)+1}}
  foreach($th in $m.TypeDefinitions){
   Bound;$t=$m.GetTypeDefinition($th);if($m.GetString($t.Namespace) -cne 'RustSharp.Generated.Values'){continue};$fs=[Collections.Generic.List[object]]::new()
   foreach($fh in $t.GetFields()){
    Bound;$f=$m.GetFieldDefinition($fh);$sig=$m.GetBlobBytes($f.Signature)
    if($sig.Length -gt 4096){throw 'Field signature cap'}
    $fs.Add([pscustomobject]@{name=$m.GetString($f.Name);attributes=[int]$f.Attributes;signature=[Convert]::ToHexString($sig);signatureBytes=$sig;blobOffset=[Reflection.Metadata.Ecma335.MetadataTokens]::GetHeapOffset([Reflection.Metadata.BlobHandle]$f.Signature);row=[Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$fh)})
   }
   $types.Add([pscustomobject]@{name=$m.GetString($t.Name);namespace=$m.GetString($t.Namespace);attributes=[int]$t.Attributes;baseType=(ScopedBase $m $t.BaseType);row=[Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.EntityHandle]$th);baseToken=[Reflection.Metadata.Ecma335.MetadataTokens]::GetToken([Reflection.Metadata.EntityHandle]$t.BaseType);fields=@($fs.ToArray())})
  }
  return [pscustomobject]@{types=@($types.ToArray());objectCoded=$objectCoded;metadataOffset=$pe.PEHeaders.MetadataStartOffset;blobHeapOffset=[Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetHeapMetadataOffset($m,[Reflection.Metadata.Ecma335.HeapIndex]::Blob);typeOffset=[Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($m,[Reflection.Metadata.Ecma335.TableIndex]::TypeDef);typeRowSize=[Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($m,[Reflection.Metadata.Ecma335.TableIndex]::TypeDef)}
 }finally{$pe.Dispose();$st.Dispose()}
}
function ExpectedField([string]$type,$record){
 switch -CaseSensitive ($type){'I32'{return '0608'}'Bool'{return '0602'}'Any'{return '061C'}}
 if($type -cmatch '^Value\((mir_value_[a-z0-9]+)\)$'){
  $target=$Matches[1];$index=-1
  for($j=0;$j -lt $record.valueTypes.Count -and $j -lt 256;$j++){Bound;if($record.valueTypes[$j].name -ceq $target){$index=$j;break}}
  if($index -ge 0){$coded=($index+3)*4;if($coded -lt 128){return ('0611'+$coded.ToString('X2'))};if($coded -lt 16384){return ('0611'+(($coded -shr 8) -bor 128).ToString('X2')+($coded -band 255).ToString('X2'))}}
 }
 $pending.Add('NOT_ACCEPTED: independently scoped field '+$type);return $null
}
try{
 $sourcePath=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v36/root-source-contract1.json';Check 'V10 immutable root source contract' '52107D57245719E366336A99758D8BE5A1A46BA8CC526FB481F4FD879C6B99FA' (Hash $sourcePath);$source=Json $sourcePath;Check 'V10 source checks' 9 $source.checks.Count;Check 'V10 source issues' 0 $source.issues.Count;Check 'V10 source status' 'passed' $source.status
 $required=@('private static readonly Version RuntimeAssemblyVersion = new(10, 0, 0, 0);','[0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a]','encoder.Int32();','encoder.Boolean();','encoder.Object();','encoder.Type(handle, isValueType: true);','MetadataTokens.TypeDefinitionHandle(index + 3)','flags: default,','culture: default,');for($q=0;$q -lt $required.Count -and $q -lt 9;$q++){Check ('independent emitter mapping '+$q) $true ($source.emitterSourceText.Contains($required[$q],[StringComparison]::Ordinal))}
 $expectPath=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v35/frozen-valueType-expectations.json';Check 'V9 frozen expectations' 'FE1A4A025BE5E71E0917385D284DCD81506D5D4B6F59135AAB4751930AB14CFE' (Hash $expectPath);$expected=@(Json $expectPath);Check 'metadata denominator' 78 $expected.Count
 $baselinePath=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v33/root-semantic1.json';Check 'V7 accepted inventory' '69A3FC4EFBE77F85B0BEA6B6D2E84BCA0A0C5E135A2D0265640D08A5541F7730' (Hash $baselinePath);$baseline=Json $baselinePath;Check 'PE denominator' 78 $baseline.originals.Count
 if(@($checks.ToArray()|Where-Object passed -EQ $false).Count){throw 'Independent prerequisite mismatch'}
 if(-not $Trial){$tiny=Json $TrialReportPath;if($tiny.status -cne 'passed' -or -not $tiny.trialOnly -or $tiny.peCompared -ne 1 -or $tiny.negativeControls.Count -ne 3 -or $tiny.sourceSha256 -cne (Hash $PSCommandPath)){throw 'Same-source tiny1 plus3 realbyte controls required'}}
 $limit=if($Trial){1}else{78};$typeCount=0;$fieldCount=0;$nonzero=0;$zero=0;$trialOriginalIndex=1
 # Reviewed comparison exits: <=78 PE, <=4096 metadata rows, <=256 expected fields; wall/cancel in every iteration. Tiny1 precedes full78.
 for($item=0;$item -lt $limit -and $item -lt 78;$item++){Bound;$i=if($Trial){$trialOriginalIndex}else{$item};$e=$expected[$i];$role=[IO.Path]::GetFileName($e.metadataPath).Split('-')[0];$file='Source'+([Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($role))+'.dll';$path=Join-Path ([IO.Path]::GetDirectoryName($e.metadataPath)) $file;$matches=@($baseline.originals|Where-Object originalPePath -CEQ $path);if($matches.Count -ne 1){throw 'Original PE binding absent'};$original=$matches[0];Check ($i.ToString()+' frozen metadata') $e.metadataSha256 (Hash $e.metadataPath);Check ($i.ToString()+' PE before') $original.originalPeSha256 (Hash $path);$bytes=[IO.File]::ReadAllBytes($path);$a=Shape $bytes;Check ($i.ToString()+' exact generated type count') $e.valueTypes.Count $a.types.Count;if($e.valueTypes.Count -eq 0){$zero++}else{$nonzero++}
  for($k=0;$k -lt $e.valueTypes.Count -and $k -lt 256;$k++){Bound;$v=$e.valueTypes[$k];$t=$a.types[$k];$tag=$i.ToString()+'/'+$k;Check ($tag+' TypeDef order') ($k+3) $t.row;Check ($tag+' name') $v.name $t.name;Check ($tag+' namespace') 'RustSharp.Generated.Values' $t.namespace;Check ($tag+' exact flags') 1048841 $t.attributes;Check ($tag+' exact scoped ValueType base') 'System.Runtime|10.0.0.0||B03F5F7F11D50A3A|0::System.ValueType' $t.baseType;Check ($tag+' field count') $v.fields.Count $t.fields.Count;$typeCount++
   for($j=0;$j -lt $v.fields.Count -and $j -lt 256;$j++){Bound;Check ($tag+'/'+$j+' field order/name') $v.fields[$j].name $t.fields[$j].name;Check ($tag+'/'+$j+' field Public') 6 $t.fields[$j].attributes;$sig=ExpectedField $v.fields[$j].type $e;if($sig){Check ($tag+'/'+$j+' exact signature/classification') $sig $t.fields[$j].signature};$fieldCount++}
  }
  if($Trial){
   if($e.valueTypes.Count -lt 1 -or $e.valueTypes[0].fields.Count -lt 1 -or $e.valueTypes[0].fields[0].type -cne 'I32'){throw 'Frozen tiny index1 requires independent first I32 field'}
   $t=$a.types[0];$f=$t.fields[0];$typeAt=$a.metadataOffset+$a.typeOffset+($t.row-1)*$a.typeRowSize;$baseAt=$typeAt+$a.typeRowSize-6
   if($a.objectCoded -le 0 -or $a.objectCoded -gt 255 -or $a.typeRowSize -lt 14 -or $baseAt+2 -ge $bytes.Length){throw 'Tiny scopedBase small-table prerequisite'}
   Check 'original BaseType two-byte row binding' ((($t.baseToken -band 16777215)*4)+1) ([BitConverter]::ToUInt16($bytes,$baseAt))
   $blobAt=$a.metadataOffset+$a.blobHeapOffset+$f.blobOffset
   if($blobAt -lt 0 -or $blobAt+3 -ge $bytes.Length -or $f.signature -cne '0608' -or $bytes[$blobAt] -ne 2 -or $bytes[$blobAt+1] -ne 6 -or $bytes[$blobAt+2] -ne 8){throw 'Frozen legal I32 field signature blob prerequisite'}
   $mutations=@([pscustomobject]@{kind='TypeFlags';offset=$typeAt;after=($bytes[$typeAt] -bxor 1)},[pscustomobject]@{kind='BaseType';offset=$baseAt;after=[byte]$a.objectCoded},[pscustomobject]@{kind='FieldSignature';offset=($blobAt+2);after=2})
   foreach($mut in $mutations){
    Bound;$clone=[byte[]]$bytes.Clone();$clone[$mut.offset]=[byte]$mut.after;$changed=Shape $clone
    $actualChanged=switch($mut.kind){'TypeFlags'{[string]$changed.types[0].attributes}'BaseType'{$changed.types[0].baseType}'FieldSignature'{$changed.types[0].fields[0].signature}}
    $independentExpected=switch($mut.kind){'TypeFlags'{'1048841'}'BaseType'{'System.Runtime|10.0.0.0||B03F5F7F11D50A3A|0::System.ValueType'}'FieldSignature'{ExpectedField $e.valueTypes[0].fields[0].type $e}}
    $reject=$actualChanged -cne $independentExpected;Check ('actual legal clone '+$mut.kind) $true $reject;Check ('actual single byte changed '+$mut.kind) $true ($clone[$mut.offset] -ne $bytes[$mut.offset]);Check ('clone same generated type count '+$mut.kind) $a.types.Count $changed.types.Count
    if($mut.kind -ceq 'FieldSignature'){Check 'mutated field remains legal Boolean signature' '0602' $actualChanged}
    $controls.Add([pscustomobject]@{mutation=$mut.kind;originalIndex=$i;originalPePath=$path;originalPeSha256=$original.originalPeSha256;predictedAbsoluteByteOffset=$mut.offset;before=$bytes[$mut.offset];after=$clone[$mut.offset];expectedIndependentBinding=$independentExpected;actualMutatedBinding=$actualChanged;validMetadataParsed=$true;rejectedByIndependentBinding=$reject;changedByteCount=1;originalsModified=$false;cloneSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($clone))})
   }
   Check 'three actual metadata-byte controls' 3 $controls.Count
  }
  Check ($i.ToString()+' PE after') $original.originalPeSha256 (Hash $path);$rows.Add([pscustomobject]@{originalIndex=$i;originalPePath=$path;originalPeSha256=$original.originalPeSha256;metadataPath=$e.metadataPath;metadataSha256=$e.metadataSha256;sourceSha256=$e.sourceSha256;actualTypes=$a.types;expectedTypes=$e.valueTypes});if($item%10 -eq 0){Write-Output ('Declaration originals '+($item+1)+'/'+$limit)}
 }
 if(-not $Trial){Check 'actual78 records' 78 $rows.Count;Check 'actual88 types' 88 $typeCount;Check 'actual76 fields' 76 $fieldCount;Check 'actual56 nonzero' 56 $nonzero;Check 'actual22 zero' 22 $zero}
}catch{$failure=$_.Exception.Message}
finally{
 # No Bound calls in finally: preserve primary timeout/cancellation and always publish its report.
 $issues=@($checks.ToArray()|Where-Object passed -EQ $false);$report=[ordered]@{schemaVersion=13;taskId='P1-GATE.03.CISEM-v1';status=if($failure-or$issues.Count){'failed'}elseif($pending.Count){'not_accepted'}else{'passed'};trialOnly=[bool]$Trial;sourceSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;candidateSha='afb0988bb74e698a685adde93bacdd2b82b8c932';treeSha='387d2ae7af9bfbd1a963dff481de3968fb7c24ae';runId='37710305713';attempt=1;sourcePeDenominator=78;valueTypeDenominator=88;fieldDenominator=76;nonzeroDenominator=56;zeroDenominator=22;backendPeDenominator=14;backendCellDenominator=24;caseDenominator=52;expectationOrigin='V9 frozen metadata + exact-candidate V10 emitter; no actual PE derives expectations';peCompared=$rows.Count;checks=@($checks.ToArray());issues=$issues;failure=$failure;originals=@($rows.ToArray());negativeControls=@($controls.ToArray());pending=@($pending.ToArray());fullAbiSemanticsAccepted=$false;completeSemanticsAccepted=$false;fullP1Closure=$false;fullP2Closure=$false;expandedSdk='10.0.400';productionSdk='10.0.401';maximumItems=78;maximumSeconds=$TimeoutSeconds;elapsedMilliseconds=$clock.ElapsedMilliseconds;externalChildrenLaunched=0;networkRequests=0;objectsDeleted=0;finishedAt=[DateTimeOffset]::Now.ToString('O')};$out=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 100));$f=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write);try{$f.Write($out)}finally{$f.Dispose()}
}
if($failure){throw $failure};if($issues.Count){throw 'Independent declaration mismatch'}
