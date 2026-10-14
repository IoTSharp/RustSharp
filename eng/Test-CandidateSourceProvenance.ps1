[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssemblyPath,[Parameter(Mandatory)][string]$ExpectedAssemblySha256,[Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell7 required'}
$owned='D:\GitHub\RustSharp\artifacts\p2-supervision\formatter-round43\'
if([IO.Path]::GetFullPath($OutputRoot) -cne $OutputRoot -or -not $OutputRoot.StartsWith($owned,[StringComparison]::OrdinalIgnoreCase)){throw 'Owned absolute output required'}
if((Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash -cne $ExpectedAssemblySha256){throw 'Exact assembly hash required'}
[string]$fixture=Join-Path $OutputRoot 'fixture-repo'
if(Test-Path -LiteralPath $fixture){throw 'Fresh fixture required'}
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'src'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'tests\nested'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'artifacts\caller'))
$source=Join-Path $fixture 'src\main.rs'
[IO.File]::WriteAllText($source,"fn main() {}`n",[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $fixture 'tests\nested\placeholder.txt'),'retained',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $fixture '.gitignore'),"artifacts/`n",[Text.UTF8Encoding]::new($false))
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$type=$assembly.GetType('RustSharp.Tests.CandidateSourceProvenance',$true)
$flags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Public
$git=$type.GetMethod('GitAsync',$flags);$verify=$type.GetMethod('VerifyAsync',$flags)
$rowType=$type.GetNestedType('ProcessEvidence',[Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Public)
$evidence=[Activator]::CreateInstance([Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType([Type[]]@($rowType)))
$clock=[Diagnostics.Stopwatch]::StartNew();$cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
$rows=[Collections.Generic.List[object]]::new();$calls=0;$failure=$null;$task=$null
function Invoke-Bounded([Reflection.MethodInfo]$method,[object[]]$arguments){
 if($script:calls -ge 8 -or $clock.Elapsed.TotalSeconds -ge 60){throw 'Eight calls/60second bound'}
 $script:calls++;$cancel.Token.ThrowIfCancellationRequested()
 $script:task=$method.Invoke($null,$arguments)
 if(-not $script:task.Wait(15000)){$cancel.Cancel();try{[void]$script:task.Wait(5000)}catch{};throw '15second call bound'}
 return $script:task.GetAwaiter().GetResult()
}
function Invoke-Git([string[]]$arguments){Invoke-Bounded $git ([object[]]@($arguments,$null,$fixture,$evidence,$cancel.Token))}
function Verify-Row([string]$id,[bool]$expected,[string]$expectedError){
 $result=Invoke-Bounded $verify ([object[]]@([string]$script:sha,[string](Join-Path $fixture 'artifacts\caller'),$cancel.Token))
 $passed=($result.CandidateMatchesWorkingTree -eq $expected -and $result.CheckedFileCount -eq 3)
 if($expectedError){$passed=$passed -and @($result.Errors|Where-Object{$_ -like $expectedError}).Count -eq 1}
 $rows.Add([ordered]@{id=$id;expectedMatch=$expected;expectedError=$expectedError;passed=$passed;actual=$result})
 Write-Information ('control '+$id+' passed='+$passed) -InformationAction Continue
}
try{
 # Single git-init trial precedes four fixed provenance controls.
 [void](Invoke-Git @('init','--quiet'))
 [void](Invoke-Git @('add','--','.gitignore','src/main.rs','tests/nested/placeholder.txt'))
 [void](Invoke-Git @('-c','user.name=RustSharp fixture','-c','user.email=fixture@invalid','commit','--quiet','-m','bounded provenance fixture'))
 $script:sha=[string](Invoke-Git @('rev-parse','HEAD'));$script:sha=$script:sha.Trim()
 $rootResult=Invoke-Bounded $verify ([object[]]@($script:sha,$fixture,$cancel.Token))
 $rows.Add([ordered]@{id='clean-root';expectedMatch=$true;passed=($rootResult.CandidateMatchesWorkingTree -and $rootResult.CheckedFileCount -eq 3);actual=$rootResult})
 Verify-Row 'clean-nested' $true ''
 [IO.File]::WriteAllText($source,"fn main() { let x = 1; }`n",[Text.UTF8Encoding]::new($false))
 Verify-Row 'dirty-tracked-outside-cwd' $false 'Working source differs from candidate: src/main.rs'
 [IO.File]::WriteAllText($source,"fn main() {}`n",[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $fixture 'tests\untracked.cs'),'// untracked compiler input',[Text.UTF8Encoding]::new($false))
 Verify-Row 'untracked-compiler-outside-cwd' $false 'Working compiler input is absent from candidate: tests/untracked.cs'
}catch{$failure=$_.Exception.ToString()}
finally{
 $cancel.Cancel();if($task -and -not $task.IsCompleted){try{[void]$task.Wait(5000)}catch{}};$cancel.Dispose()
 $record=[ordered]@{taskId='P2-08.02.provenance-cwd-regression';state=if($failure -or $rows.Count -ne 4 -or @($rows|Where-Object{-not $_.passed}).Count -ne 0){'FAIL'}else{'PASS'};denominator=4;executed=$rows.Count;passed=@($rows|Where-Object{$_.passed}).Count;assemblyPath=$AssemblyPath;assemblySha256=$ExpectedAssemblySha256;fixture=$fixture;fixtureRetainedAsEvidence=$true;cases=$rows.ToArray();setupProcessEvidence=@($evidence);elapsedMilliseconds=$clock.ElapsedMilliseconds;failure=$failure;fullP2Closure=$false}
 $json=$record|ConvertTo-Json -Depth 14
 [IO.File]::WriteAllText((Join-Path $OutputRoot 'regression.json'),$json,[Text.UTF8Encoding]::new($false));$json
}
if($record.state -ne 'PASS'){throw 'Four-control provenance regression failed; retain originals'}
