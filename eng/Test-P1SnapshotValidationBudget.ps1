[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CandidateSha,
    [Parameter(Mandatory)][string]$SnapshotPath,
    [Parameter(Mandatory)][string]$ResultPath,
    [ValidateSet('Tiny','All')][string]$Mode='Tiny',
    [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$overall=[Diagnostics.Stopwatch]::StartNew()
$rows=[Collections.Generic.List[object]]::new()
$maximum=if($Mode -eq 'Tiny'){1}else{6}
if([IO.File]::Exists($ResultPath) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ResultPath)))){throw 'Fresh report in existing exclusive directory required'}
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$snapshotBytes=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SnapshotPath,$root))
if($snapshotBytes.Length -gt 33554432){throw 'Snapshot32MiB input bound'}
$snapshot=ConvertFrom-P1StrictJson $snapshotBytes
function Check([string]$Name,[scriptblock]$Action){
 $CancellationToken.ThrowIfCancellationRequested()
 if($rows.Count -ge $maximum -or $overall.Elapsed.TotalSeconds -ge 75){throw 'Controls6/75s bound'}
 $failure=$null
 try{& $Action}catch{$failure=$_.Exception.Message}
 $rows.Add([pscustomobject]@{name=$Name;passed=($null -eq $failure);failure=$failure})
}
Check 'real-snapshot-starts-budget-after-import' {
 if($null -ne $script:P1SnapshotValidationClock){throw 'Import spent validation budget before any actual Git check'}
 $errors=@(Test-P1SnapshotEvidence $snapshot $root $CandidateSha)
 if($errors.Count -ne 0){throw ($errors -join '; ')}
 if($null -eq $script:P1SnapshotValidationClock -or -not $script:P1SnapshotValidationClock.IsRunning -or $script:P1SnapshotValidationProcesses.Count -ne 4){throw 'Actual snapshot did not start its bounded four-command validation'}
}
if($Mode -eq 'All'){
 Check 'second-real-snapshot-shares-clock-and-command-ledger' {
  $originalClock=$script:P1SnapshotValidationClock
  $errors=@(Test-P1SnapshotEvidence $snapshot $root $CandidateSha)
  if($errors.Count -ne 0 -or -not [object]::ReferenceEquals($originalClock,$script:P1SnapshotValidationClock) -or $script:P1SnapshotValidationProcesses.Count -ne 8){throw 'Repeated snapshot reset its shared budget or erased command records'}
 }
 Check 'expired-shared-time-rejects-before-process-launch' {
  $savedClock=$script:P1SnapshotValidationClock;$before=$script:P1SnapshotValidationProcesses.Count
  try{
   # Deterministic negative guard input, never a real process/evidence ledger.
   $script:P1SnapshotValidationClock=[pscustomobject]@{Elapsed=[TimeSpan]::FromSeconds(110);ElapsedMilliseconds=110000}
   $rejected=$false
   try{$null=Invoke-P1ValidationGit $root @('rev-parse','HEAD')}catch{if($_.Exception.Message -notmatch 'Snapshot Git item/input/time bound exceeded'){throw};$rejected=$true}
   if(-not $rejected -or $script:P1SnapshotValidationProcesses.Count -ne $before){throw 'Expired batch ran a command or restarted its clock'}
  }finally{$script:P1SnapshotValidationClock=$savedClock}
 }
 Check 'fixed-command-count-rejects-before-process-launch' {
  $savedRows=$script:P1SnapshotValidationProcesses
  try{
   $script:P1SnapshotValidationProcesses=[Collections.Generic.List[object]]::new()
   for($i=0;$i -lt 24 -and $overall.Elapsed.TotalSeconds -lt 75;$i++){$CancellationToken.ThrowIfCancellationRequested();$script:P1SnapshotValidationProcesses.Add([pscustomobject]@{guardOnly=$true})}
   if($script:P1SnapshotValidationProcesses.Count -ne 24){throw 'Negative guard setup deadline'}
   $rejected=$false
   try{$null=Invoke-P1ValidationGit $root @('rev-parse','HEAD')}catch{if($_.Exception.Message -notmatch 'Snapshot Git item/input/time bound exceeded'){throw};$rejected=$true}
   if(-not $rejected -or $script:P1SnapshotValidationProcesses.Count -ne 24){throw 'Command24 limit was changed or reset'}
  }finally{$script:P1SnapshotValidationProcesses=$savedRows}
 }
 Check 'stale-tree-still-rejects' {
  $copy=ConvertFrom-P1StrictJson $snapshotBytes;$copy.treeSha='0000000000000000000000000000000000000000'
  $errors=@(Test-P1SnapshotEvidence $copy $root $CandidateSha)
  if(-not ($errors -join '; ').Contains('Actual Git candidate tree differs')){throw 'Stale tree accepted after timing repair'}
 }
 Check 'stale-working-source-hash-still-rejects' {
  $copy=ConvertFrom-P1StrictJson $snapshotBytes;$copy.files[0].sha256=('0'*64)
  $errors=@(Test-P1SnapshotEvidence $copy $root $CandidateSha)
  if(-not ($errors -join '; ').Contains('Source snapshot compiler-input SHA-256 is stale')){throw 'Stale source hash accepted after timing repair'}
 }
}
$actualProcesses=@($script:P1SnapshotValidationProcesses.ToArray())
$report=[ordered]@{schemaVersion=1;evidenceKind='p1-snapshot-validation-budget-utility';mode=$Mode;candidateSha=$CandidateSha;snapshotSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($snapshotBytes));sourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')).Hash;expectedControls=$maximum;controls=$rows.ToArray();actualValidationProcesses=$actualProcesses;maximumGitCommands=24;maximumValidationSeconds=110;maximumControls=6;maximumUtilitySeconds=75;elapsedSeconds=$overall.Elapsed.TotalSeconds;negativeClockAndCountAreUtilityInputs=$true;generatedProgramExecuted=0;formalJointExecuted=0;fullP1Closure=$false;status=if($rows.Count -eq $maximum -and @($rows|Where-Object passed -EQ $false).Count -eq 0){'passed'}else{'failed'}}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath),($report|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
if($report.status -ne 'passed'){throw 'Snapshot budget controls failed; original diagnostics retained'}
Write-Output ($report.status+' '+$rows.Count+' controls; '+$actualProcesses.Count+' actual Git calls')
