# Data-only original IL/token audit. Never load or execute retained assemblies.
[CmdletBinding()]
param([switch]$Trial,[string]$OutputPath,[string]$TrialReportPath,[ValidateRange(1,120)][int]$TimeoutSeconds=60,[string]$CancelFile)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
$repo=Split-Path $PSScriptRoot -Parent
$packet=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v31'
[IO.Directory]::CreateDirectory($packet)|Out-Null
if(-not $OutputPath){$OutputPath=Join-Path $packet $(if($Trial){'tiny1.json'}else{'semantic1.json'})}
$output=[IO.Path]::GetFullPath($OutputPath)
if(-not $output.StartsWith($packet+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or [IO.File]::Exists($output)){throw 'Exclusive new output required'}
$clock=[Diagnostics.Stopwatch]::StartNew();$checks=[Collections.Generic.List[object]]::new();$issues=[Collections.Generic.List[object]]::new();$rows=[Collections.Generic.List[object]]::new();$controls=[Collections.Generic.List[object]]::new();$failure=$null
$hostIdentity=Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5
$parentChain=[Collections.Generic.List[object]]::new();$pp=$hostIdentity.ParentProcessId
for($p=0;$p -lt 4 -and $pp -gt 0 -and $clock.Elapsed.TotalSeconds -lt 20;$p++){if($CancelFile -and [IO.File]::Exists($CancelFile)){break};$hp=Get-CimInstance Win32_Process -Filter "ProcessId=$pp" -OperationTimeoutSec 3;if(-not $hp){break};$parentChain.Add(($hp|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine));$pp=$hp.ParentProcessId}
function Bound {if($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds){throw 'Wall timeout'};if($CancelFile -and [IO.File]::Exists($CancelFile)){throw 'Cancelled'};if($checks.Count -gt 30000){throw 'Check count cap'}}
function Hash([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'Input32MiB cap'};return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function ReadJson([string]$p){Bound;if(([IO.FileInfo]::new($p)).Length -gt 33554432){throw 'JSON32MiB cap'};return [IO.File]::ReadAllText($p)|ConvertFrom-Json -Depth 100 -DateKind String}
function Check([string]$id,$expected,$actual){Bound;$ok=([string]$expected -ceq [string]$actual);$r=[pscustomobject]@{id=$id;expected=$expected;actual=$actual;passed=$ok};$checks.Add($r);if(-not $ok){$issues.Add($r)}}
function AssemblyId($m,$a,[bool]$definition=$false){
 $key=if($definition){$m.GetBlobBytes($a.PublicKey)}else{$m.GetBlobBytes($a.PublicKeyOrToken)}
 $token=if($key){[Convert]::ToHexString($key)}else{''}
 if($key.Length -and ($definition -or (([int]$a.Flags -band 1) -ne 0))){$digest=[Security.Cryptography.SHA1]::HashData($key);$last=[byte[]]$digest[12..19];[Array]::Reverse($last);$token=[Convert]::ToHexString($last)}
 return ($m.GetString($a.Name)+'|'+$a.Version+'|'+$m.GetString($a.Culture)+'|'+$token)
}
function EntityType($m,$h,[int]$depth=0){
 Bound;$h=[Reflection.Metadata.EntityHandle]$h;if($depth -ge 32){throw 'Type scope depth cap'}
 switch([string]$h.Kind){
 'TypeDefinition'{$d=$m.GetTypeDefinition([Reflection.Metadata.TypeDefinitionHandle]$h);return (AssemblyId $m ($m.GetAssemblyDefinition()) $true)+'::'+$m.GetString($d.Namespace)+'.'+$m.GetString($d.Name)}
 'TypeReference'{$d=$m.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$h);$scope=$d.ResolutionScope;switch([string]$scope.Kind){'AssemblyReference'{$id=AssemblyId $m ($m.GetAssemblyReference([Reflection.Metadata.AssemblyReferenceHandle]$scope))}'TypeReference'{$id=EntityType $m $scope ($depth+1)}'ModuleDefinition'{$id=AssemblyId $m ($m.GetAssemblyDefinition()) $true}default{throw ('Unreviewed type scope '+$scope.Kind)}};return $id+'::'+$m.GetString($d.Namespace)+'.'+$m.GetString($d.Name)}
 'TypeSpecification'{$d=$m.GetTypeSpecification([Reflection.Metadata.TypeSpecificationHandle]$h);$s=@{b=$m.GetBlobBytes($d.Signature);i=0};$v=SigType $s $m;if($s.i -ne $s.b.Length){throw 'TypeSpec trailing bytes'};return $v}
 default{throw ('Invalid type handle '+$h.Kind)}
 }
}
function TypeName($m,[int]$token){$row=$token -shr 2;if($row -lt 1){throw 'Nil signature token'};switch($token -band 3){0{$h=[Reflection.Metadata.Ecma335.MetadataTokens]::TypeDefinitionHandle($row)}1{$h=[Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle($row)}2{$h=[Reflection.Metadata.Ecma335.MetadataTokens]::TypeSpecificationHandle($row)}default{throw 'Signature token tag invalid'}};return EntityType $m $h}
function Canonical($s){return ($s.header.ToString()+'|'+$s.genericArity+'|'+$s.parameterCount+'|'+$s.returnType+'|'+([string]::Join(';',[string[]]$s.parameters)))}
function MethodToken($m,[int]$token,[int]$depth=0){
 Bound;if($depth -ge 16 -or ($token -band 16777215) -lt 1){throw 'Nil/deep method token'}
 $h=[Reflection.Metadata.Ecma335.MetadataTokens]::EntityHandle($token)
 switch([string]$h.Kind){
 'MethodDefinition'{$d=$m.GetMethodDefinition([Reflection.Metadata.MethodDefinitionHandle]$h);return [pscustomobject]@{token=$token;kind='MethodDefinition';type=(EntityType $m ($d.GetDeclaringType()));name=$m.GetString($d.Name);signature=(Signature $m $d.Signature);attributes=[string]$d.Attributes;methodSpec=$null}}
 'MemberReference'{$d=$m.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$h);$sig=Signature $m $d.Signature;if($sig.kind -eq 6){throw 'Call token names a field'};return [pscustomobject]@{token=$token;kind='MemberReference';type=(EntityType $m $d.Parent);name=$m.GetString($d.Name);signature=$sig;attributes=$null;methodSpec=$null}}
 'MethodSpecification'{$d=$m.GetMethodSpecification([Reflection.Metadata.MethodSpecificationHandle]$h);$base=MethodToken $m ([Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($d.Method)) ($depth+1);$sig=Signature $m $d.Signature;if($sig.kind -ne 10 -or $sig.parameterCount -ne $base.signature.genericArity){throw 'MethodSpec arity mismatch'};return [pscustomobject]@{token=$token;kind='MethodSpecification';type=$base.type;name=$base.name;signature=$base.signature;attributes=$base.attributes;methodSpec=$sig}}
 default{throw ('Invalid call token table '+$h.Kind)}
 }
}
function DecodeIL($m,[byte[]]$bytes){
 if($bytes.Length -gt 1048576){throw 'IL1MiB cap'};$calls=[Collections.Generic.List[object]]::new();$instructions=0;$at=0
 # Each iteration consumes at least one opcode byte; <=65536 instructions, wall/cancel bound.
 for($n=0;$n -lt 65536 -and $at -lt $bytes.Length;$n++){
  Bound;$offset=$at;$code=[int]$bytes[$at++];if($code -eq 254){if($at -ge $bytes.Length){throw 'Truncated extended opcode'};$code=65024+[int]$bytes[$at++]}
  if(-not $script:opcodes.ContainsKey($code)){throw ('Invalid IL opcode '+$code)};$op=$script:opcodes[$code];$operand=$at
  $width=switch([string]$op.OperandType){'InlineNone'{0}'ShortInlineBrTarget'{1}'ShortInlineI'{1}'ShortInlineVar'{1}'InlineVar'{2}'InlineI'{4}'InlineBrTarget'{4}'InlineField'{4}'InlineMethod'{4}'InlineSig'{4}'InlineString'{4}'InlineTok'{4}'InlineType'{4}'ShortInlineR'{4}'InlineI8'{8}'InlineR'{8}'InlineSwitch'{if($at+4 -gt $bytes.Length){throw 'Truncated switch count'};$count=[BitConverter]::ToUInt32($bytes,$at);if($count -gt 65536){throw 'Switch arity cap'};4+4*$count}default{throw ('Unsupported operand '+$op.OperandType)}}
  if($at+$width -gt $bytes.Length){throw ('Truncated operand at '+$offset)}
  if($op.Name -in @('call','callvirt','newobj')){$token=[BitConverter]::ToInt32($bytes,$at);$target=MethodToken $m $token;$calls.Add([pscustomobject]@{offset=$offset;operandOffset=$operand;opcode=$op.Name;token=$token;target=$target})}
  $at+=$width;$instructions++
 }
 if($at -ne $bytes.Length){throw 'IL instruction cap/nonconsumption'}
 return [pscustomobject]@{bytes=$bytes.Length;instructions=$instructions;fullyConsumed=$true;calls=@($calls.ToArray())}
}
try{
 $helper=Join-Path $repo 'eng/Audit-P1RetainedPayloadV3.ps1';Check 'immutable signature decoder' '583B6FA32D203FD6FB55BE1578A91E6678D96CE2AE052EB288486D08674525CE' (Hash $helper)
 $ts=$null;$es=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$ts,[ref]$es);if($es.Count){throw 'Prior decoder invalid'}
 foreach($name in @('Byte','Compressed','SigType','Signature')){Bound;$nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object {$_.Name -ceq $name});if($nodes.Count -ne 1){throw 'Decoder absent'};. ([scriptblock]::Create($nodes[0].Extent.Text))}
 $prior=Join-Path $repo 'artifacts/p1-supervision/ci-followup-v30/root-semantic1.json';Check 'V4 root report immutable' '926CE02EDF486B6B15504372FB57B979A7832B5B4B31B5891420E6B8D5050E72' (Hash $prior);$baseline=ReadJson $prior
 Check 'V4 checks reused' 1012 $baseline.checks.Count;Check 'V4 source original denominator' 78 $baseline.peCount;Check 'candidate' 'afb0988bb74e698a685adde93bacdd2b82b8c932' $baseline.candidateSha;Check 'tree' '387d2ae7af9bfbd1a963dff481de3968fb7c24ae' $baseline.treeSha;Check 'run' '37710305713' $baseline.runId;Check 'attempt' 1 $baseline.attempt
 if($issues.Count){throw 'Immutable prerequisites differ'}
 $script:opcodes=@{};$fields=[Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static');if($fields.Count -gt 512){throw 'Opcode catalogue cap'}
 foreach($field in $fields){Bound;$op=$field.GetValue($null);$script:opcodes[([int]$op.Value -band 65535)]=$op}
 if(-not $Trial){$tiny=ReadJson $TrialReportPath;if(-not $tiny.trialOnly -or $tiny.status -cne 'passed' -or $tiny.peCount -ne 1 -or $tiny.negativeControls.Count -ne 2){throw 'Tiny1/two real byte mutation controls prerequisite'}}
 $limit=if($Trial){1}else{78}
 for($i=0;$i -lt $limit -and $i -lt 78;$i++){
  Bound;$original=$baseline.originals[$i];$label=$original.label;Check ($label+' actual original PE SHA') $original.originalPeSha256 (Hash $original.originalPePath)
  $fs=[IO.File]::OpenRead($original.originalPePath);$pe=[Reflection.PortableExecutable.PEReader]::new($fs)
  try{
   $m=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);if($m.MethodDefinitions.Count -gt 4096 -or $m.TypeDefinitions.Count -gt 4096){throw 'Metadata item cap'}
   $methods=[Collections.Generic.List[object]]::new();$types=[Collections.Generic.List[object]]::new();$bindings=[Collections.Generic.List[object]]::new()
   foreach($th in $m.TypeDefinitions){Bound;$td=$m.GetTypeDefinition($th);$fieldsOut=[Collections.Generic.List[object]]::new();foreach($fh in $td.GetFields()){Bound;if($fieldsOut.Count -ge 4096){throw 'Field count cap'};$fd=$m.GetFieldDefinition($fh);$fieldsOut.Add([pscustomobject]@{name=$m.GetString($fd.Name);attributes=[string]$fd.Attributes;offset=$fd.GetOffset();signature=(Signature $m $fd.Signature)})};$layout=$td.GetLayout();$types.Add([pscustomobject]@{type=(EntityType $m $th);attributes=[string]$td.Attributes;baseType=if($td.BaseType.IsNil){$null}else{EntityType $m $td.BaseType};packing=$layout.PackingSize;size=$layout.Size;genericParameters=$td.GetGenericParameters().Count;fields=@($fieldsOut.ToArray())})}
   foreach($dh in $m.MethodDefinitions){Bound;$d=$m.GetMethodDefinition($dh);$target=MethodToken $m ([Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($dh));$decoded=$null;if($d.RelativeVirtualAddress){$body=$pe.GetMethodBody($d.RelativeVirtualAddress);$decoded=DecodeIL $m $body.GetILBytes();Check ($label+' IL consumed '+$target.name) $true $decoded.fullyConsumed
     foreach($call in $decoded.calls){Bound;$t=$call.target;$prefix=($t.type -split '::',2)[0];$asmName=($prefix -split '\|',2)[0]
      if($asmName -in @('SourceProducer','SourceWrapper')){
       $sameCase=$label.Substring(0,$label.LastIndexOf(' '));$targets=@($baseline.originals|Where-Object {$_.label.StartsWith($sameCase+' ') -and [IO.Path]::GetFileNameWithoutExtension($_.originalPePath) -ceq $asmName});if($targets.Count -ne 1){throw 'Exact original producer absent'};$producer=$targets[0];Check ($label+' call producer SHA') $producer.originalPeSha256 (Hash $producer.originalPePath)
       $pf=[IO.File]::OpenRead($producer.originalPePath);$pp=[Reflection.PortableExecutable.PEReader]::new($pf)
       try{$pm=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pp);Check ($label+' exact call AssemblyRef') (AssemblyId $pm ($pm.GetAssemblyDefinition()) $true) $prefix;$matches=[Collections.Generic.List[object]]::new();foreach($ph in $pm.MethodDefinitions){Bound;if($pm.MethodDefinitions.Count -gt 4096){throw 'Producer method cap'};$pd=MethodToken $pm ([Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($ph));if($pd.type -ceq $t.type -and $pd.name -ceq $t.name -and (Canonical $pd.signature) -ceq (Canonical $t.signature) -and $pd.attributes.Contains('Public')){$matches.Add($pd)}};Check ($label+' actual IL import '+$t.name+'@'+$call.offset) 1 $matches.Count;$bindings.Add([pscustomobject]@{method=$target.name;offset=$call.offset;opcode=$call.opcode;target=$t;producerSha256=$producer.originalPeSha256;producerPath=$producer.originalPePath;exactMatches=$matches.Count})}finally{$pp.Dispose();$pf.Dispose()}
      }
     }
     if($Trial -and $controls.Count -eq 0 -and $decoded.calls.Count){$call=$decoded.calls[0];$mut=[byte[]]$body.GetILBytes().Clone();[Array]::Copy([BitConverter]::GetBytes([int]100663296),0,$mut,$call.operandOffset,4);$reject=$false;try{$null=DecodeIL $m $mut}catch{$reject=$true};Check 'negative actual IL nil MethodDef token bytes' $true $reject;$controls.Add([pscustomobject]@{mutation='actualILTokenBytes';offset=$call.operandOffset;rejected=$reject;originalsModified=$false})
      $refs=@($m.AssemblyReferences);if(-not $refs.Count){throw 'Actual AssemblyRef mutation unavailable'};$ar=$m.GetAssemblyReference($refs[0]);$raw=$m.GetBlobBytes($ar.PublicKeyOrToken);$badVersion=[Version]::new($ar.Version.Major+1,$ar.Version.Minor,$ar.Version.Build,$ar.Version.Revision);$mutated=[pscustomobject]@{Name=$ar.Name;Version=$badVersion;Culture=$ar.Culture;PublicKeyOrToken=$ar.PublicKeyOrToken;Flags=$ar.Flags};$reject=(AssemblyId $m $ar) -cne (AssemblyId $m $mutated);Check 'negative actual AssemblyRef version mutation' $true $reject;$controls.Add([pscustomobject]@{mutation='decodedActualAssemblyReferenceVersion';original=(AssemblyId $m $ar);mutated=(AssemblyId $m $mutated);rejected=$reject;originalsModified=$false})
     }
    };$methods.Add([pscustomobject]@{target=$target;genericParameters=$d.GetGenericParameters().Count;il=$decoded})
   }
   $rows.Add([pscustomobject]@{label=$label;originalPePath=$original.originalPePath;originalPeSha256=$original.originalPeSha256;assemblyIdentity=(AssemblyId $m ($m.GetAssemblyDefinition()) $true);types=@($types.ToArray());methods=@($methods.ToArray());actualProducerCalls=@($bindings.ToArray());classValueTypeEquated=$false;fullAbiSemanticsAccepted=$false})
  }finally{$pe.Dispose();$fs.Dispose()}
  if($i%10 -eq 0){Write-Output ('IL originals '+($i+1)+'/'+$limit)}
 }
 if($Trial){Check 'real negative controls' 2 $controls.Count}
}catch{$failure=$_.Exception.Message}
finally{
 $sourceHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
 $report=[ordered]@{schemaVersion=5;taskId='P1-GATE.03.CISEM-v1';status=if($failure-or$issues.Count){'failed'}else{'passed'};trialOnly=[bool]$Trial;sourceSha256=$sourceHash;candidateSha='afb0988bb74e698a685adde93bacdd2b82b8c932';treeSha='387d2ae7af9bfbd1a963dff481de3968fb7c24ae';runId='37710305713';attempt=1;caseDenominator=52;backendMatrixDenominator=24;backendPeDenominator=14;sourcePackagesEach=19;sourcePeEach=39;sourceRetainedEach=785;backendRetainedEach=103;sourceFileDenominator=1572;backendFileDenominator=234;physicalFileDenominator=1806;peCount=$rows.Count;checks=@($checks.ToArray());issues=@($issues.ToArray());failure=$failure;originals=@($rows.ToArray());negativeControls=@($controls.ToArray());reusedV4Checks=1012;reusedV4ReportSha256='926CE02EDF486B6B15504372FB57B979A7832B5B4B31B5891420E6B8D5050E72';completeSemanticsAccepted=$false;formalJointAccepted=$false;fullP1Closure=$false;fullP2Closure=$false;pending=@('NOT_ACCEPTED: full source-MIR expected call/ABI/generic/layout semantics','NOT_ACCEPTED: backend14/24cell/cases52 semantic expectations','NOT_ACCEPTED: generic substitution and field/layout equivalence','NOT_ACCEPTED: real PE AssemblyRef byte mutation negative','NOT_ACCEPTED: independent root acceptance and full containment');expandedSdk='10.0.400';productionSdk='10.0.401';maximumSeconds=$TimeoutSeconds;elapsedMilliseconds=$clock.ElapsedMilliseconds;maximumItems=78;externalChildrenLaunched=0;networkRequests=0;objectsDeleted=0;hostIdentity=($hostIdentity|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine);parentChain=@($parentChain.ToArray());finishedAt=[DateTimeOffset]::Now.ToString('O')}
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 100));if($bytes.Length -gt 16777216){throw 'Output16MiB cap'};$o=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$o.Write($bytes)}finally{$o.Dispose()}
}
if($failure){throw $failure};if($issues.Count){throw ('IL issues='+$issues.Count)}
Write-Output ('pe='+$rows.Count+' checks='+$checks.Count)
