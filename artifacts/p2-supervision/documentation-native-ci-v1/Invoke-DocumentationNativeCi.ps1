[CmdletBinding()]
param()
Set-StrictMode -Version 3
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
if([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -ne 'X64'){throw 'Native x64 required'}
$actualRid=if($IsWindows){'win-x64'}elseif($IsLinux){'linux-x64'}else{throw 'Unsupported OS'}
if($env:TOOL_RID -ne $actualRid){throw 'Native OS/RID mismatch'}
$head=(& git rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $head -ne $env:CANDIDATE_SHA){throw 'Exact candidate SHA mismatch'}
$root=Join-Path $PWD ('artifacts/p2-doc-native/'+$actualRid)
if(Test-Path -LiteralPath $root){throw 'Fresh output required'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$bin=Join-Path $root 'bin'
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$paths=@(& git ls-files)
if($LASTEXITCODE -ne 0 -or $paths.Count -gt 20000){throw 'Source inventory failed/bound exceeded'}

$deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(21))
function Get-SourceHashes([bool]$CheckCancellation=$true) {
 $clock=[Diagnostics.Stopwatch]::StartNew()
 foreach($path in $paths){if($CheckCancellation){$deadline.Token.ThrowIfCancellationRequested()};if($clock.Elapsed.TotalSeconds -gt 30){throw 'Source hash wall bound'};[ordered]@{path=$path;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash}}
}
function Run-Case([string]$name,[string[]]$arguments,[int]$seconds){
 $deadline.Token.ThrowIfCancellationRequested()
 Write-Host ('Starting '+$name+' at '+[DateTimeOffset]::UtcNow.ToString('o'))
 & (Join-Path $PWD 'eng/Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList $arguments -TimeoutSeconds $seconds -CapturePrefix (Join-Path $root $name) -MaximumOutputBytes 33554432 -CancellationToken $deadline.Token
}
$eight=@('default-virtual-rejected','optin-virtual-two-members','optin-root-plus-members','existing-target-unchanged','dependency-family-rejected','feature-family-rejected','output-byte-budget','real-child-deadline')
$twenty=@('initial-success-hashes','nested-modules-source-hashes','existing-target-unchanged','missing-manifest','virtual-workspace-unsupported','dependency-unsupported','feature-unsupported','multiple-targets-unsupported','invalid-arguments','invalid-option','noncargo-input','unsafe-module-path','missing-module','malformed-source','pre-cancel','mid-workspace-cancel','deadline-after-workspace','unsafe-output-parent','unsafe-documentation-scheme','missing-documentation-symbol')
$specs=@(@{file='workspace-cli-eight.json';switch='--p2-doc-workspace-cli-component';names=$eight;prefix='P2-08.03 workspace CLI process '},@{file='command-api-twenty.json';switch='--p2-doc-cli-component';names=$twenty;prefix='P2-08.03 CLI '})
foreach($spec in $specs){$cases=@(foreach($name in $spec.names){@{name=($spec.prefix+$name);status='not-run';error='Candidate command has not started.'}});@{denominator=$spec.names.Count;executed=0;passed=0;failed=0;notRun=$spec.names.Count;cases=$cases;fullLeafAccepted=$false;fullP2Closure=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root $spec.file) -Encoding utf8}
$before=@()
$status='NOT_RUN';$failure=$null;$actualSdk=$null
try{
 $before=@(Get-SourceHashes)
 Run-Case 'sdk-version' @('--version') 10
 $actualSdk=([IO.File]::ReadAllText((Join-Path $root 'sdk-version.stdout.log'))).Trim()
 if($actualSdk -ne '10.0.400'){throw 'Actual SDK does not match pinned global.json10.0.400'}
 Run-Case 'restore' @('restore','RustSharp.slnx') 300
 Run-Case 'fresh-build' @('build','RustSharp.slnx','-c','Release','--no-restore','--no-incremental','-m:1',('-p:OutDir='+$bin+[IO.Path]::DirectorySeparatorChar)) 600
 $tests=Join-Path $bin 'RustSharp.Tests.dll'
 foreach($spec in $specs){
  $report=Join-Path $root $spec.file
  # Runner requires a new caller-owned report. Preserve NOT_RUN original before replacement.
  $pending=$report+'.not-run-original.json'
  [IO.File]::Move($report,$pending)
  try{Run-Case $spec.file @($tests,$spec.switch,$report) 200}
  finally{if(-not(Test-Path -LiteralPath $report)){[IO.File]::Copy($pending,$report)}}
  $result=Get-Content -Raw -LiteralPath $report|ConvertFrom-Json
  $actualNames=@($result.cases|ForEach-Object{[string]$_.name});$expectedNames=@($spec.names|ForEach-Object{$spec.prefix+$_})
  $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  for($index=0;$index -lt $expectedNames.Count -and $index -lt 20;$index++){$deadline.Token.ThrowIfCancellationRequested();if($index -ge $actualNames.Count -or -not $seen.Add($actualNames[$index]) -or $actualNames[$index] -cne $expectedNames[$index] -or $result.cases[$index].status -cne 'passed' -or ($null -ne $result.cases[$index].PSObject.Properties['error'] -and -not [string]::IsNullOrEmpty([string]$result.cases[$index].error))){throw 'Duplicate, unknown or reordered fixed case identity'}}
  if($result.denominator -ne $spec.names.Count -or $result.executed -ne $spec.names.Count -or $result.passed -ne $spec.names.Count -or $result.failed -ne 0 -or $result.notRun -ne 0 -or $result.cases.Count -ne $spec.names.Count){throw 'Full fixed component denominator failed'}
 }
 $status='COMPONENTS_PASSED'
}catch{$status='FAILED';$failure=$_.ToString();throw}
finally{
 try{
  $after=@();$bindingError=$null;try{$after=@(Get-SourceHashes $false)}catch{$bindingError=$_.ToString()}
  $artifacts=@();try{$artifacts=@(foreach($name in @('RustSharp.Tests.dll','RustSharp.Tests.pdb','rsc.dll','rsc.pdb','RustSharp.Compiler.dll','RustSharp.Compiler.pdb')){$path=Join-Path $bin $name;if(Test-Path -LiteralPath $path){@{path=$name;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash}}})}catch{$bindingError=[string]$bindingError+$_.ToString()}
  $stable=($before|ConvertTo-Json -Depth 5 -Compress) -eq ($after|ConvertTo-Json -Depth 5 -Compress)
  $bindingInvalid=(-not [string]::IsNullOrEmpty([string]$bindingError)) -or $artifacts.Count -ne 6 -or -not $stable
  if($bindingInvalid){$status='FAILED';$failure=[string]$failure+' Binding incomplete: error='+[string]$bindingError+'; binaries='+$artifacts.Count+'/6; sourcesStable='+$stable}
  @{schemaVersion=1;candidateSha=$head;rid=$actualRid;runId=$env:GITHUB_RUN_ID;status=$status;failure=$failure;bindingError=$bindingError;powerShell=$PSVersionTable.PSVersion.ToString();dotnet=$dotnet;actualSdk=$actualSdk;sourceBefore=$before;sourceAfter=$after;sourcesStable=$stable;binaries=$artifacts;fullLeafAccepted=$false;fullP2Closure=$false;originalContainmentPhaseAccepted=$false;cleanupContract='Invoke-BoundedProcess metadata preserves parent-exit-only limitation; not original full-containment proof'}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $root 'candidate-binding.json') -Encoding utf8
  if($bindingInvalid){throw ('Mandatory candidate binding failed: '+$failure)}
 }finally{$deadline.Dispose()}
}