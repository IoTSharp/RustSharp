[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$TaskRoot,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ReviewedManifestSha256,
 [Parameter(Mandatory)][string]$ReviewedLiveHostIdentityPath,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ReviewedLiveHostIdentitySha256,
 [ValidateSet('resume-no-signal')][string]$Mode='resume-no-signal'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}
if(-not [IO.Path]::IsPathFullyQualified($TaskRoot)){throw 'Absolute task root required'}
$taskRoot=[IO.Path]::GetFullPath($TaskRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if($taskRoot.Equals($PSScriptRoot,[StringComparison]::OrdinalIgnoreCase) -or $taskRoot.StartsWith($PSScriptRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime artifact root must be separate from tracked fixture sources'}
$taskRootClock=[Diagnostics.Stopwatch]::StartNew();$taskRootNode=[IO.DirectoryInfo]::new($taskRoot)
for($taskRootDepth=0;$taskRootNode -ne $null -and $taskRootDepth -lt 64 -and $taskRootClock.Elapsed.TotalSeconds -lt 5;$taskRootDepth++){if(-not $taskRootNode.Exists -or ($taskRootNode.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Existing non-reparse root ancestry required'};$taskRootNode=$taskRootNode.Parent}
if($taskRootNode -ne $null){throw 'Root ancestry64-node/5-second bound'}

$taskResult=Join-Path $taskRoot 'root-result.json'
if(Test-Path -LiteralPath $taskResult){throw 'One retained root runtime attempt only; never overwrite'}
$taskManifest=Join-Path $taskRoot 'manifest.json'
$taskDeadline=[DateTime]::UtcNow.AddSeconds(120)
$taskCancellation=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
$taskReport=[ordered]@{status='🚧 In progress / 🚧 进行中';mode=$Mode;controllerProcessesCreated=0;helperProcessesCreated=0;cliProcessesCreated=0;signalSent=$false;cancelPassed=$false;noSignalPassed=$false;leafClosed=$false;systemAncestorsComplete=$false;fixedLeafDenominator=59;fixedParentDenominator=91;cleanupFailures=@();publicationFailures=@();maximumRuntimeChecks=20;maximumSeconds=120}
$taskController=$null;$taskOuter=$null;$taskIoFiles=0;$taskIoBytes=0L;$taskIoChunks=0;$taskCleanupPublication=$false
function Read-TaskBytes([string]$Path){
 $taskReadClock=[Diagnostics.Stopwatch]::StartNew();$taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([IO.Path]::IsPathFullyQualified($Path)) 'Absolute I/O path';$taskReadPath=[IO.Path]::GetFullPath($Path)
 Test-TaskRequirement ((([IO.File]::GetAttributes($taskReadPath)) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -eq 0) 'Ordinary I/O file only';$script:taskIoFiles++;Test-TaskRequirement ($script:taskIoFiles -le 100) 'Shared100 file bound'
 $taskInput=$null;$taskOutput=$null
 try{$taskInput=[IO.FileStream]::new($taskReadPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,16384,[IO.FileOptions]::SequentialScan);Test-TaskRequirement ($taskInput.Length -le 1048576) 'Input1MiB bound';$taskOutput=[IO.MemoryStream]::new();$taskBuffer=[byte[]]::new(16384)
  for($taskChunk=0;$taskChunk -lt 66;$taskChunk++){$taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([DateTime]::UtcNow -lt $taskDeadline -and $taskReadClock.Elapsed.TotalSeconds -lt 30) 'Read deadline';$taskCount=$taskInput.Read($taskBuffer,0,$taskBuffer.Length);$taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([DateTime]::UtcNow -lt $taskDeadline -and $taskReadClock.Elapsed.TotalSeconds -lt 30) 'Read return deadline';if($taskCount -eq 0){return ,$taskOutput.ToArray()};$script:taskIoChunks++;$script:taskIoBytes+=$taskCount;Test-TaskRequirement ($script:taskIoChunks -le 1200 -and $script:taskIoBytes -le 16777216 -and ($taskOutput.Length+$taskCount) -le 1048576) 'Shared chunk/byte/file bound';$taskOutput.Write($taskBuffer,0,$taskCount)};throw 'Read66 chunks bound'
 }finally{if($taskInput){$taskInput.Dispose()};if($taskOutput){$taskOutput.Dispose()}}
}
function Read-TaskText([string]$Path){[Text.UTF8Encoding]::new($false,$true).GetString((Read-TaskBytes $Path))}
function Publish-TaskText([string]$Path,[string]$Text,[bool]$Fresh){
 $taskWriteClock=[Diagnostics.Stopwatch]::StartNew();$taskExact=[IO.Path]::GetFullPath($Path);Test-TaskRequirement ($taskExact.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)) 'Publication within exclusive root';$taskWriteBytes=[Text.UTF8Encoding]::new($false).GetBytes($Text);Test-TaskRequirement ($taskWriteBytes.Length -le 1048576) 'Output1MiB bound';$taskTemporary=$taskExact+'.'+[Guid]::NewGuid().ToString('N')+'.owned-write';$taskWriteStream=$null
 $taskPrimary=$null;$taskSecondary=[Collections.Generic.List[Exception]]::new();$taskSecondaryRows=@();$taskCreated=$false;$taskPublished=$false;$taskRemoved=$null
 try{$taskWriteStream=[IO.FileStream]::new($taskTemporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,16384,[IO.FileOptions]::WriteThrough);$taskCreated=$true;for($taskOffset=0;$taskOffset -lt $taskWriteBytes.Length;$taskOffset+=16384){if(-not $taskCleanupPublication){$taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([DateTime]::UtcNow -lt $taskDeadline) 'Publication lifetime'};Test-TaskRequirement ($taskWriteClock.Elapsed.TotalSeconds -lt 5) 'Publication5s';$taskWriteStream.Write($taskWriteBytes,$taskOffset,[Math]::Min(16384,$taskWriteBytes.Length-$taskOffset))};$taskWriteStream.Flush($true);$taskWriteStream.Dispose();$taskWriteStream=$null;[IO.File]::Move($taskTemporary,$taskExact,(-not $Fresh));$taskPublished=$true;$taskRemoved=$true}
 catch{$taskPrimary=$_.Exception}
 finally{if($taskWriteStream){try{$taskWriteStream.Dispose()}catch{$taskSecondary.Add($_.Exception);$taskSecondaryRows+=@{stage='stream-close';error=$_.ToString()}}};if($taskCreated -and -not $taskPublished){try{$taskResolved=[IO.Path]::GetFullPath($taskTemporary);Test-TaskRequirement ($taskResolved.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase) -and $taskResolved.EndsWith('.owned-write',[StringComparison]::Ordinal)) 'Exact owned publication temporary';Test-TaskRequirement ((([IO.File]::GetAttributes($taskResolved)) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -eq 0) 'Owned temporary ordinary file';[IO.File]::Delete($taskResolved);Test-TaskRequirement (-not [IO.File]::Exists($taskResolved)) 'Owned temporary still present';$taskRemoved=$true}catch{$taskRemoved=$false;$taskSecondary.Add($_.Exception);$taskSecondaryRows+=@{stage='owned-temporary-cleanup';error=$_.ToString()}}}}
 if($taskPrimary -or $taskSecondary.Count -gt 0){$taskPublicationRecord=@{target=$taskExact;temporary=$taskTemporary;published=$taskPublished;temporaryCreated=$taskCreated;temporaryRemoved=$taskRemoved;primary=if($taskPrimary){$taskPrimary.ToString()}else{$null};secondary=$taskSecondaryRows};$taskReport.publicationFailures+=@($taskPublicationRecord);$taskAll=[Collections.Generic.List[Exception]]::new();if($taskPrimary){$taskAll.Add($taskPrimary)};foreach($taskError in $taskSecondary){$taskAll.Add($taskError)};throw [AggregateException]::new('Atomic publication primary/cleanup failures retained',$taskAll)}
}
function Save-TaskResult {Publish-TaskText $taskResult ($taskReport|ConvertTo-Json -Depth 24) (-not [IO.File]::Exists($taskResult))}
function Test-TaskRequirement([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason}}
function Get-TaskHash([string]$Path){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Read-TaskBytes $Path)))}
function Convert-TaskIdentity($Row){[FormatterPrivateConsoleFixture.ProcessIdentity]::new([uint32]$Row.Pid,[long]$Row.CreationFileTime,[uint32]$Row.ParentPid,[string]$Row.CommandLine)}
function Confirm-TaskIdentity($Row){$taskIdentity=Convert-TaskIdentity $Row;Test-TaskRequirement ([FormatterPrivateConsoleFixture.ProcessAudit]::Read($taskIdentity.Pid).Equals($taskIdentity)) 'Exact PID/native FILETIME/OS command/direct parent mismatch';return $taskIdentity}
function Confirm-TaskOuter($Expected){
 $taskSnapshot=$taskOuter.Inspect();$taskReport.lastOuterSnapshot=$taskSnapshot;Save-TaskResult
 Test-TaskRequirement ($taskSnapshot.Coherent -and -not $taskSnapshot.Overflow -and $taskSnapshot.Failures.Count -eq 0 -and $taskSnapshot.Total -eq $Expected.Count -and $taskSnapshot.Active -eq $Expected.Count -and $taskSnapshot.Assigned -eq $Expected.Count -and $taskSnapshot.Listed -eq $Expected.Count -and $taskSnapshot.Members.Count -eq $Expected.Count) 'Strict cumulative/current outer job count/failures; unexpected members retained'
 foreach($taskExpected in $Expected){Test-TaskRequirement (@($taskSnapshot.Members|Where-Object {$_.Equals($taskExpected)}).Count -eq 1) 'Exact outer job membership missing'}
}
function Write-TaskGate([string]$Name,[string]$Digest){$taskGate=Join-Path $taskRoot $Name;Test-TaskRequirement (-not (Test-Path -LiteralPath $taskGate)) 'Fresh immutable root gate required';Publish-TaskText $taskGate ($taskNonce+'|'+$Digest) $true}
try{
 Save-TaskResult
 Test-TaskRequirement ((Get-TaskHash $taskManifest) -eq $ReviewedManifestSha256) 'Root-reviewed exact manifest hash required'
 $taskFrozen=Read-TaskText $taskManifest|ConvertFrom-Json -Depth 32
 Test-TaskRequirement ($taskFrozen.PSObject.Properties.Name -contains 'runtimeBinding') 'Required nested runtimeBinding; a flat externalFiles alias is not sufficient'
 Test-TaskRequirement ($taskFrozen.PSObject.Properties.Name -contains 'packageFiles') 'Required packageFiles inventory'
 $taskBindingNames=@('candidateSha','treeSha','dotnet','cli','cliSha256','buildReport','buildReportSha256','sourceHashes','externalFiles')
 foreach($taskRequiredField in $taskBindingNames){Test-TaskRequirement ($taskFrozen.runtimeBinding.PSObject.Properties.Name -contains $taskRequiredField) ('Missing consumed runtimeBinding field: '+$taskRequiredField)}
 Test-TaskRequirement ($taskFrozen.runtimeBinding.candidateSha -match '^[a-fA-F0-9]{40}$' -and $taskFrozen.runtimeBinding.treeSha -match '^[a-fA-F0-9]{40}$') 'Exact caller-reviewed candidate/tree binding shape required'
 Test-TaskRequirement (@($taskFrozen.runtimeBinding.sourceHashes).Count -eq 3 -and @($taskFrozen.runtimeBinding.externalFiles).Count -eq 13 -and @($taskFrozen.packageFiles).Count -gt 0) 'Exact source3/external13 and nonempty actual package inventories'
 foreach($taskRequiredPath in @($taskFrozen.runtimeBinding.dotnet,$taskFrozen.runtimeBinding.cli,$taskFrozen.runtimeBinding.buildReport)){Test-TaskRequirement ([IO.Path]::IsPathFullyQualified($taskRequiredPath)) 'Consumed binding paths must be absolute'}
 foreach($taskRequiredHash in @($taskFrozen.runtimeBinding.cliSha256,$taskFrozen.runtimeBinding.buildReportSha256)){Test-TaskRequirement ($taskRequiredHash -match '^[A-Fa-f0-9]{64}$') 'Consumed binding SHA256 shape'}
 Test-TaskRequirement ($taskFrozen.packageFiles.Count -le 40 -and $taskFrozen.runtimeBinding.externalFiles.Count -le 20) 'Bounded frozen hash inventories'
 foreach($taskFile in $taskFrozen.packageFiles){
  $taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([DateTime]::UtcNow -lt $taskDeadline) 'Package verification deadline'
  Test-TaskRequirement ([IO.Path]::GetFileName($taskFile.name) -eq $taskFile.name) 'Top-level frozen package filenames only'
  Test-TaskRequirement ((Get-TaskHash (Join-Path $taskRoot $taskFile.name)) -eq $taskFile.sha256) ('Frozen package changed: '+$taskFile.name)
 }
 foreach($taskFile in $taskFrozen.runtimeBinding.externalFiles){$taskCancellation.Token.ThrowIfCancellationRequested();Test-TaskRequirement ([DateTime]::UtcNow -lt $taskDeadline) 'External binding deadline';Test-TaskRequirement ((Get-TaskHash $taskFile.path) -eq $taskFile.sha256) ('Actual production/dependency bytes changed: '+$taskFile.path)}
 Test-TaskRequirement ((Get-TaskHash $ReviewedLiveHostIdentityPath) -eq $ReviewedLiveHostIdentitySha256) 'Exact separately root-reviewed live Codex host identity artifact'
 $taskRunBindings=@($taskFrozen.packageFiles|Where-Object name -eq 'Run.ps1')
 Test-TaskRequirement ($taskRunBindings.Count -eq 1 -and (Get-TaskHash $PSCommandPath) -eq $taskRunBindings[0].sha256) 'Actual executing tracked/source-copy Run bytes must match frozen package'
 Add-Type -Path (Join-Path $taskRoot 'Launcher.dll')
 Test-TaskRequirement ([FormatterPrivateConsoleFixture.ProcessAudit]::VerifyRoot($taskRoot,$taskCancellation.Token) -eq $taskRoot) 'Native-library exact non-reparse artifact root'
 $taskHostRow=Read-TaskText $ReviewedLiveHostIdentityPath|ConvertFrom-Json
 $taskHost=Confirm-TaskIdentity $taskHostRow
 Test-TaskRequirement ($taskHost.Pid -ne $PID -and $taskHost.CommandLine.Length -gt 0) 'Reviewed external live host boundary required'
 $taskOwner=[FormatterPrivateConsoleFixture.ProcessAudit]::Read([uint32]$PID)
 $taskBoundary=[FormatterPrivateConsoleFixture.ProcessAudit]::ToBoundary([uint32]$PID,$taskHost,$taskCancellation.Token)
 $taskReport.owner=$taskOwner;$taskReport.taskBoundaryChain=$taskBoundary
 Test-TaskRequirement ($taskBoundary.BoundaryVerified -and -not $taskBoundary.SystemAncestorsComplete) 'Owner to exact live host path verified; upstream system ancestors explicitly incomplete'
 $taskNonce=[Guid]::NewGuid().ToString('N');$taskRequest=Join-Path $taskRoot 'trial-request.json'
 Test-TaskRequirement (-not (Test-Path -LiteralPath $taskRequest)) 'Fresh trial request required'
 foreach($taskStage in @('task-lease.json','ready-helper.json','ready-console.json','inner-proof-console.json','go-console-inner.txt','go-console-outer.txt','ready-cli.json','inner-proof-cli.json','go-helper.txt','go-cli-inner.txt','go-cli-outer.txt','controller-result.json','helper-result.json')){Test-TaskRequirement (-not (Test-Path -LiteralPath (Join-Path $taskRoot $taskStage))) 'Fresh retained stage names required'}
 $taskLeasePath=Join-Path $taskRoot 'task-lease.json'
 Publish-TaskText $taskLeasePath (@{nonce=$taskNonce;root=$taskRoot;owner=$taskOwner;manifestSha256=$ReviewedManifestSha256}|ConvertTo-Json -Depth 8) $true
 $taskLeaseSha=Get-TaskHash $taskLeasePath
 $taskSources=@{};foreach($taskSource in $taskFrozen.runtimeBinding.sourceHashes){$taskSources[$taskSource.path]=$taskSource.sha256}
 $taskPayload=@{Root=$taskRoot;Nonce=$taskNonce;Owner=$taskOwner;LeaseSha256=$taskLeaseSha;ConhostImage=(Join-Path ([Environment]::SystemDirectory) 'conhost.exe');Mode=$Mode;ManifestSha256=$ReviewedManifestSha256;Dotnet=$taskFrozen.runtimeBinding.dotnet;Cli=$taskFrozen.runtimeBinding.cli;CliSha256=$taskFrozen.runtimeBinding.cliSha256;BuildReport=$taskFrozen.runtimeBinding.buildReport;BuildReportSha256=$taskFrozen.runtimeBinding.buildReportSha256;CandidateSha=$taskFrozen.runtimeBinding.candidateSha;Host=$taskHost;SourceHashes=$taskSources}
 Publish-TaskText $taskRequest ($taskPayload|ConvertTo-Json -Depth 16) $true
 $taskOuter=[FormatterPrivateConsoleFixture.JobLease]::new();$taskReport.outerJobInitiallyEmpty=$taskOuter.Inspect();Save-TaskResult
 $taskPersist=[Action[FormatterPrivateConsoleFixture.LaunchReceipt]]{param($receipt)$taskReport.controllerLaunch=$receipt;$taskReport.controllerProcessesCreated=[int]($receipt.Pid -ne 0);Save-TaskResult}
 $taskArguments=[Collections.Generic.List[string]]::new();foreach($taskArgument in @((Join-Path $taskRoot 'Helper.dll'),'--role','controller','--request',$taskRequest)){$taskArguments.Add($taskArgument)}
 $taskController=[FormatterPrivateConsoleFixture.OwnedSuspendedProcess]::Start([string]$taskFrozen.runtimeBinding.dotnet,$taskArguments,$taskRoot,$true,$taskOuter,$taskPersist)
 Confirm-TaskOuter @($taskController.Identity);$taskController.Recheck();$taskController.Resume();$taskReport.controllerLaunch=$taskController.Receipt;Save-TaskResult
 $taskHelperGate=$false;$taskConsoleGate=$false;$taskCliGate=$false;$taskControllerDone=$false
 for($taskCheck=0;$taskCheck -lt 20 -and [DateTime]::UtcNow -lt $taskDeadline;$taskCheck++){
  $taskCancellation.Token.ThrowIfCancellationRequested()
  if(-not $taskHelperGate -and (Test-Path -LiteralPath (Join-Path $taskRoot 'ready-helper.json'))){
   $taskReady=Read-TaskText (Join-Path $taskRoot 'ready-helper.json')|ConvertFrom-Json -Depth 16
   Test-TaskRequirement ($taskReady.nonce -eq $taskNonce) 'Helper stage nonce mismatch'
   $taskStageController=Confirm-TaskIdentity $taskReady.controller;$taskHelper=Confirm-TaskIdentity $taskReady.helper
   Test-TaskRequirement ($taskStageController.Equals($taskController.Identity) -and $taskHelper.ParentPid -eq $taskController.Identity.Pid) 'Controller/helper exact direct parent chain'
   Confirm-TaskOuter @($taskController.Identity,$taskHelper);$taskController.Recheck();[void](Confirm-TaskIdentity $taskReady.helper)
   Write-TaskGate 'go-helper.txt' (Get-TaskHash (Join-Path $taskRoot 'ready-helper.json'));$taskHelperGate=$true;$taskReport.helperProcessesCreated=1;$taskReport.helperIdentity=$taskHelper;Save-TaskResult
  }
  if($taskHelperGate -and -not $taskConsoleGate -and (Test-Path -LiteralPath (Join-Path $taskRoot 'inner-proof-console.json'))){
   $taskConsoleProof=Read-TaskText (Join-Path $taskRoot 'inner-proof-console.json')|ConvertFrom-Json -Depth 16
   $taskConsoleReady=Read-TaskText (Join-Path $taskRoot 'ready-console.json')|ConvertFrom-Json -Depth 16
   $taskConsoleHash=Get-TaskHash (Join-Path $taskRoot 'ready-console.json')
   Test-TaskRequirement ($taskConsoleProof.nonce -eq $taskNonce -and $taskConsoleReady.nonce -eq $taskNonce -and $taskConsoleProof.readyConsoleSha256 -eq $taskConsoleHash -and $taskConsoleReady.beforeConsoleQuery.RawCount -eq 0 -and $taskConsoleReady.beforeConsoleQuery.Win32LastError -eq 6 -and $taskConsoleReady.beforeConsoleQuery.Members.Count -eq 0 -and $taskConsoleReady.afterConsoleQuery.RawCount -eq 1 -and $taskConsoleReady.afterConsoleQuery.Members.Count -eq 1 -and $taskConsoleReady.beforeConsoleMembers.Count -eq 0 -and $taskConsoleReady.afterConsoleMembers.Count -eq 1 -and $taskConsoleReady.afterConsoleMembers[0] -eq $taskHelper.Pid) 'Exact detached0/allocated1 console stages; HWND owner is never conhost evidence'
   $taskConsoleHost=Confirm-TaskIdentity $taskConsoleProof.conhost.Identity
   Test-TaskRequirement ((Confirm-TaskIdentity $taskConsoleProof.controller).Equals($taskController.Identity) -and (Confirm-TaskIdentity $taskConsoleProof.helper).Equals($taskHelper) -and $taskConsoleHost.ParentPid -eq $taskHelper.Pid -and $taskConsoleHost.CreationFileTime -ge $taskHelper.CreationFileTime) 'Complete native conhost/helper/controller identities and new direct child'
   $taskConsoleImage=[FormatterPrivateConsoleFixture.ProcessAudit]::ImagePath($taskConsoleHost)
   Test-TaskRequirement ($taskConsoleImage.Equals((Join-Path ([Environment]::SystemDirectory) 'conhost.exe'),[StringComparison]::OrdinalIgnoreCase) -and $taskConsoleImage.Equals($taskConsoleProof.conhost.ImagePath,[StringComparison]::OrdinalIgnoreCase) -and (Get-TaskHash $taskConsoleImage) -eq $taskConsoleProof.conhost.Sha256) 'Exact native image and real conhost bytes'
   Test-TaskRequirement ($taskConsoleProof.innerJob.Coherent -and -not $taskConsoleProof.innerJob.Overflow -and $taskConsoleProof.innerJob.Failures.Count -eq 0 -and $taskConsoleProof.innerJob.Total -eq 2 -and $taskConsoleProof.innerJob.Active -eq 2 -and $taskConsoleProof.innerJob.Assigned -eq 2 -and $taskConsoleProof.innerJob.Listed -eq 2 -and $taskConsoleProof.innerJob.Members.Count -eq 2) 'Full pre-CLI inner helper+conhost Job total2/current2'
   foreach($taskExpected in @($taskHelper,$taskConsoleHost)){Test-TaskRequirement (@($taskConsoleProof.innerJob.Members|Where-Object {(Convert-TaskIdentity $_).Equals($taskExpected)}).Count -eq 1) 'Exact pre-CLI inner full member proof'}
   Confirm-TaskOuter @($taskController.Identity,$taskHelper,$taskConsoleHost)
   $taskReport.conhostEvidence=$taskConsoleProof.conhost;$taskReport.innerConsoleProofSha256=Get-TaskHash (Join-Path $taskRoot 'inner-proof-console.json');Save-TaskResult
   Write-TaskGate 'go-console-outer.txt' $taskConsoleHash;$taskConsoleGate=$true
  }
  if($taskConsoleGate -and -not $taskCliGate -and (Test-Path -LiteralPath (Join-Path $taskRoot 'inner-proof-cli.json'))){
   $taskProof=Read-TaskText (Join-Path $taskRoot 'inner-proof-cli.json')|ConvertFrom-Json -Depth 16
   $taskReady=Read-TaskText (Join-Path $taskRoot 'ready-cli.json')|ConvertFrom-Json -Depth 16
   $taskReadyHash=Get-TaskHash (Join-Path $taskRoot 'ready-cli.json')
   Test-TaskRequirement ($taskProof.nonce -eq $taskNonce -and $taskReady.nonce -eq $taskNonce -and $taskProof.readyCliSha256 -eq $taskReadyHash) 'Exact CLI stage nonce and digest'
   $taskCli=Confirm-TaskIdentity $taskReady.cli;$taskProofHelper=Confirm-TaskIdentity $taskProof.helper;$taskProofController=Confirm-TaskIdentity $taskProof.controller
   Test-TaskRequirement ($taskProofController.Equals($taskController.Identity) -and $taskProofHelper.Equals($taskHelper) -and $taskCli.ParentPid -eq $taskHelper.Pid -and (Convert-TaskIdentity $taskProof.cli).Equals($taskCli)) 'Exact CLI/helper/controller identities'
   Test-TaskRequirement ($taskProof.innerJob.Coherent -and -not $taskProof.innerJob.Overflow -and $taskProof.innerJob.Failures.Count -eq 0 -and $taskProof.innerJob.Total -eq 3 -and $taskProof.innerJob.Assigned -eq 3 -and $taskProof.innerJob.Listed -eq 3 -and $taskProof.innerJob.Active -eq 3 -and $taskProof.innerJob.Members.Count -eq 3) 'Strict cumulative/current inner owner proof counts'
   Test-TaskRequirement ((Confirm-TaskIdentity $taskProof.conhost.Identity).Equals($taskConsoleHost)) 'Same root-reviewed native console host before CLI resume'
   foreach($taskExpected in @($taskHelper,$taskConsoleHost,$taskCli)){Test-TaskRequirement (@($taskProof.innerJob.Members|Where-Object {(Convert-TaskIdentity $_).Equals($taskExpected)}).Count -eq 1) 'Exact inner member identity proof'}
   Confirm-TaskOuter @($taskController.Identity,$taskHelper,$taskConsoleHost,$taskCli);$taskController.Recheck();[void](Confirm-TaskIdentity $taskReady.helper);[void](Confirm-TaskIdentity $taskReady.cli)
   $taskReport.innerProofSha256=Get-TaskHash (Join-Path $taskRoot 'inner-proof-cli.json');$taskReport.cliIdentity=$taskCli;$taskReport.cliProcessesCreated=1;Save-TaskResult
   Write-TaskGate 'go-cli-outer.txt' $taskReadyHash;$taskCliGate=$true
  }
  if($taskController.HasExited()){$taskControllerDone=$taskController.Wait(1,$taskCancellation.Token);$taskReport.controllerExitCode=$taskController.ExitCode;$taskReport.controllerExitObserved=$true;Save-TaskResult;break}
  if(($taskCheck % 3) -eq 0){$taskReport.runtimeCheck=$taskCheck;$taskReport.lastObservedUtc=[DateTime]::UtcNow.ToString('o');Save-TaskResult;Write-Information ('Fixture bounded no-signal check '+$taskCheck) -InformationAction Continue}
  Start-Sleep -Milliseconds 5000
 }
 Test-TaskRequirement ($taskControllerDone -and $taskController.ExitCode -eq 0 -and $taskHelperGate -and $taskConsoleGate -and $taskCliGate) 'Natural controller exit zero with both reviewed gates required'
 $taskControllerResult=Read-TaskText (Join-Path $taskRoot 'controller-result.json')|ConvertFrom-Json -Depth 24
 $taskHelperResult=Read-TaskText (Join-Path $taskRoot 'helper-result.json')|ConvertFrom-Json -Depth 24
 Test-TaskRequirement ($taskControllerResult.noSignalPassed -and $taskHelperResult.noSignalPassed -and -not $taskControllerResult.signalSent -and -not $taskHelperResult.signalSent -and $taskHelperResult.cliExitCode -eq 0 -and $taskHelperResult.fixtureRemoved) 'Exact independent no-signal worker and owned cleanup evidence required'
 $taskReport.controllerExitCode=$taskController.ExitCode;$taskReport.workerResultHashes=@{controller=Get-TaskHash (Join-Path $taskRoot 'controller-result.json');helper=Get-TaskHash (Join-Path $taskRoot 'helper-result.json')};$taskReport.noSignalPassed=$true
 $taskReport.status='✅ No-signal path verified; cancellation open / ✅ 无信号路径已验证；取消未闭环'
}catch{
 $taskReport.status='⛔ Blocked / ⛔ 已阻塞';$taskReport.failure=$_.ToString()
 if($_.Exception.GetType().FullName -eq 'FormatterPrivateConsoleFixture.LaunchFailure'){$taskReport.structuredLaunchFailure=$_.Exception.Receipt}
}finally{
 $taskCleanupPublication=$true
 if($taskOuter){try{$taskReport.outerJobCleanup=$taskOuter.Reap([Threading.CancellationToken]::None)}catch{$taskReport.cleanupFailures+=($_.ToString());$taskReport.noSignalPassed=$false}}
 if($taskController){try{$taskController.Dispose();$taskReport.controllerCleanup=$taskController.Receipt;$taskReport.controllerExitCode=$taskController.Receipt.ActualExitCode;if(-not $taskController.Receipt.OwnProcessReclaimed -or $taskController.Receipt.CleanupErrors.Count -ne 0 -or $taskController.Receipt.PublicationErrors.Count -ne 0 -or $taskController.Receipt.ActualExitCode -ne 0){$taskReport.noSignalPassed=$false}}catch{$taskReport.cleanupFailures+=($_.ToString());$taskReport.noSignalPassed=$false}}
 if($taskOuter){try{$taskOuter.Dispose();$taskReport.outerJobHandleClosed=$taskOuter.Cleanup.HandleClosed;if(-not $taskOuter.Cleanup.AccountingZero -or $taskOuter.Cleanup.HandleClosed -ne $true -or $taskOuter.Cleanup.Final.Total -ne 4 -or $taskOuter.Cleanup.Errors.Count -ne 0){$taskReport.noSignalPassed=$false}}catch{$taskReport.cleanupFailures+=($_.ToString());$taskReport.noSignalPassed=$false}}
 $taskReport.status=if($taskReport.noSignalPassed){'✅ No-signal path verified; cancellation open / ✅ 无信号路径已验证；取消未闭环'}else{'⛔ Blocked / ⛔ 已阻塞'};$taskReport.ioBudget=@{files=$script:taskIoFiles;bytes=$script:taskIoBytes;chunks=$script:taskIoChunks};$taskCancellation.Dispose();$taskReport.finishedAt=Get-Date -Format o;try{Save-TaskResult}catch{$taskReport.noSignalPassed=$false;$taskReport.status='⛔ Final publication failed / ⛔ 最终落盘失败';$taskReport.finalPublicationFailure=$_.ToString();$taskReport|ConvertTo-Json -Depth 24|Write-Output;throw}
}
if(-not $taskReport.noSignalPassed){throw 'Reusable fixture no-signal proof did not pass; original evidence retained; no automatic runtime retry'}
$taskReport|ConvertTo-Json -Depth 24
