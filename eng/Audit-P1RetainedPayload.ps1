# Retained CI bytes only. Never loads or executes an original assembly/program.
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$OutputPath,
    [switch]$Trial,
    [ValidateRange(1,120)][int]$TimeoutSeconds = 90,
    [string]$TrialReportPath,
    [string]$CancelFile
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$packet = Join-Path $repo 'artifacts/p1-supervision/ci-followup-v27'
if (-not $OutputPath) { $OutputPath = Join-Path $packet $(if ($Trial) {'tiny1.json'} else {'semantic-attempt1.json'}) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not $output.StartsWith($packet + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be in exclusive v27 packet.' }
if ([IO.File]::Exists($output)) { throw 'Immutable attempt exists; choose a new output.' }
[IO.Directory]::CreateDirectory($packet) | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$checks = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$hashes = [Collections.Generic.List[object]]::new()
$headers = [Collections.Generic.List[object]]::new()
$cases = [Collections.Generic.List[object]]::new()
$components = [Collections.Generic.List[object]]::new()
$pending = [Collections.Generic.List[object]]::new()
$candidate = 'afb0988bb74e698a685adde93bacdd2b82b8c932'
$tree = '387d2ae7af9bfbd1a963dff481de3968fb7c24ae'
$failure = $null
$hostProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -OperationTimeoutSec 5
$parentChain = [Collections.Generic.List[object]]::new()
$parentPid = $hostProcess.ParentProcessId
# Reviewed comparisons; at most four parent identities and 120 seconds total.
for ($i=0; $i -lt 4 -and $parentPid -gt 0 -and $clock.Elapsed.TotalSeconds -lt 20 -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds; $i++) {
    if ($CancelFile -and [IO.File]::Exists($CancelFile)) {break}
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$parentPid" -OperationTimeoutSec 5
    if (-not $parent) { break }
    $parentChain.Add(($parent | Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine))
    $parentPid = $parent.ParentProcessId
}
function Bound {
    if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Audit wall-clock deadline.' }
    if ($CancelFile -and [IO.File]::Exists($CancelFile)) { throw 'Audit cancelled by cancel file.' }
    if ($checks.Count -ge 30000 -or $hashes.Count -ge 4096 -or $headers.Count -ge 512 -or $cases.Count -ge 128) { throw 'Audit record bound.' }
}
function Check([string]$label, $expected, $actual) {
    Bound
    $expectedText=ConvertTo-Json -InputObject $expected -Depth 100 -Compress
    $actualText=ConvertTo-Json -InputObject $actual -Depth 100 -Compress
    $ok = $expectedText -ceq $actualText
    if ($expectedText.Length -gt 1024) { $expected=[ordered]@{canonicalCharacters=$expectedText.Length;canonicalSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($expectedText)))} }
    if ($actualText.Length -gt 1024) { $actual=[ordered]@{canonicalCharacters=$actualText.Length;canonicalSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($actualText)))} }
    $row = [pscustomobject]@{label=$label;passed=$ok;expected=$expected;actual=$actual}
    $checks.Add($row)
    if (-not $ok) { $issues.Add($row) }
}
function ReadJson([string]$path) {
    Bound
    $file = [IO.FileInfo]::new($path)
    if (-not $file.Exists -or $file.Length -gt 67108864) { throw 'Missing/oversize JSON: ' + $path }
    return [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 100 -DateKind String
}
function Hash([string]$path) {
    Bound
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
function Normal([string]$value) { return $value.Replace("`r`n","`n").Replace("`r","`n") }
function Bind([string]$label,$artifact,$map,[string]$prefix) {
    Bound
    if (-not $artifact -or -not $artifact.path) { throw 'Missing artifact binding: ' + $label }
    $portable = ([string]$artifact.path).Replace('\','/')
    $at = $portable.IndexOf($prefix,[StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'Original path prefix absent: ' + $label + ': ' + $portable }
    $key = $portable.Substring($at+$prefix.Length)
    if (-not $map.ContainsKey($key)) { throw 'Retained physical member absent: ' + $key }
    $row = $map[$key]
    Check ($label+' physical SHA256') ([string]$artifact.sha256).ToUpperInvariant() $row.freshSha256
    $lengthProperty=$artifact.PSObject.Properties['length']
    if ($null -ne $lengthProperty) { Check ($label+' physical length') ([long]$lengthProperty.Value) ([long]$row.bytes) }
    return $row.path
}
function Header([string]$label,[string]$path,[bool]$managed,[string]$rid) {
    Bound
    $stream = [IO.File]::OpenRead($path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $head = $reader.ReadBytes(64)
        if ($head.Length -lt 64) { throw 'Short machine header.' }
        if ($head[0] -eq 0x4d -and $head[1] -eq 0x5a) {
            $offset = [BitConverter]::ToInt32($head,60)
            if ($offset -lt 64 -or $offset -gt 1048576 -or $offset+256 -gt $stream.Length) { throw 'Invalid PE header offset.' }
            $stream.Position=$offset
            $pe=$reader.ReadBytes(256)
            Check ($label+' PE signature') '50450000' ([Convert]::ToHexString($pe[0..3]))
            $machine=[BitConverter]::ToUInt16($pe,4)
            $magic=[BitConverter]::ToUInt16($pe,24)
            $directories=if ($magic -eq 0x20b) {136} elseif ($magic -eq 0x10b) {120} else {throw 'Invalid PE optional header magic.'}
            $clrRva=[BitConverter]::ToUInt32($pe,$directories+14*8)
            $clrSize=[BitConverter]::ToUInt32($pe,$directories+14*8+4)
            Check ($label+' CLR directory') $managed ($clrRva -gt 0 -and $clrSize -ge 72)
            if ($managed) { Check ($label+' managed I386/PE32') $true ($machine -eq 0x14c -and $magic -eq 0x10b) }
            else { Check ($label+' native Windows AMD64/PE32+') $true ($rid -ceq 'win-x64' -and $machine -eq 0x8664 -and $magic -eq 0x20b) }
            $headers.Add([pscustomobject]@{label=$label;path=$path;sha256=Hash $path;format='PE';machine=$machine;optionalMagic=$magic;clrRva=$clrRva;clrSize=$clrSize;managed=$managed})
        } elseif ([Convert]::ToHexString($head[0..3]) -ceq '7F454C46') {
            $machine=[BitConverter]::ToUInt16($head,18)
            Check ($label+' Linux ELF64 little endian AMD64') $true (-not $managed -and $rid -ceq 'linux-x64' -and $head[4] -eq 2 -and $head[5] -eq 1 -and $machine -eq 62)
            $headers.Add([pscustomobject]@{label=$label;path=$path;sha256=Hash $path;format='ELF';machine=$machine;elfClass=$head[4];byteOrder=$head[5];managed=$false})
        } else { throw 'Unknown original machine header: '+$path }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Process([string]$label,$p) {
    Bound
    Check ($label+' actual identity/complete output') $true ($p.pid -gt 0 -and $p.parentPid -gt 0 -and -not [string]::IsNullOrWhiteSpace($p.commandLine) -and $p.termination -ceq 'Exited' -and $p.outputTruncated -ceq $false -and $p.outputReadTimedOut -ceq $false -and $p.outputDrainTimedOut -ceq $false -and $p.outputReadLimitReached -ceq $false -and $p.cleanupIncomplete -ceq $false)
}
function Verifier([string]$label,$v,$original,$map,[string]$prefix,[string]$rid) {
    $path=Bind ($label+' original verifier') $v.report $map $prefix
    $raw=ReadJson $path
    Check ($label+' verifier success') $true ($v.status -ceq 'passed' -and $raw.Succeeded -ceq $true -and -not $raw.Failure -and $raw.VerifyProcess.ExitCode -eq 0 -and $raw.VerifyProcess.ProcessTreeCleanupIncomplete -ceq $false)
    Check ($label+' verifier assembly hash') $original.sha256 $raw.Assembly.Sha256
    Check ($label+' verifier target runtime') $rid $raw.Environment.RuntimeIdentifier
    if ($v.rawEvidence) { Check ($label+' retained raw verifier equals embedded') (ConvertTo-Json $v.rawEvidence -Depth 100 -Compress) (ConvertTo-Json $raw -Depth 100 -Compress) }
    Process ($label+' outer verifier process') $v.process
    Check ($label+' outer verifier exit') 0 $v.process.exitCode
}
try {
    $inventoryRoots=@('ci-followup-v25','ci-followup-v26')
    $inventoryData=[Collections.Generic.List[object]]::new()
    for ($g=0;$g -lt 2;$g++) {
        $inventoryPath=Join-Path $repo ('artifacts/p1-supervision/'+$inventoryRoots[$g]+'/payload-static-inventory.json')
        $inv=ReadJson $inventoryPath
        Check ($inventoryRoots[$g]+' candidate') $candidate $inv.candidateSha
        Check ($inventoryRoots[$g]+' tree') $tree $inv.treeSha
        Check ($inventoryRoots[$g]+' run') '37710305713' $inv.runId
        Check ($inventoryRoots[$g]+' attempt') 1 $inv.attempt
        Check ($inventoryRoots[$g]+' denominator') $(if ($g -eq 0) {1572} else {234}) @($inv.files).Count
        $limit=if ($Trial) {1} else {@($inv.files).Count}
        # tiny1 runs this exact loop first; fixed maximum1806 files/120s, cancellation per item.
        for ($f=0;$f -lt $limit -and $f -lt 1806;$f++) {
            Bound
            $row=$inv.files[$f]
            $allowed=[IO.Path]::GetFullPath((Join-Path $repo ('artifacts/p1-supervision/'+$inventoryRoots[$g]+'/originals/')))
            $physical=[IO.Path]::GetFullPath($row.path)
            if (-not $physical.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Inventory path escapes retained originals.' }
            $fresh=Hash $physical
            Check ($inventoryRoots[$g]+'/'+$row.platform+'/'+$row.name+' bytes') ([long]$row.bytes) ([long]([IO.FileInfo]::new($physical)).Length)
            Check ($inventoryRoots[$g]+'/'+$row.platform+'/'+$row.name+' hash') $row.sha256 $fresh
            $row|Add-Member -NotePropertyName freshSha256 -NotePropertyValue $fresh
            $hashes.Add($row)
            if ($f % 200 -eq 0) { Write-Output ('verified '+$inventoryRoots[$g]+' files '+$f+'/'+$limit) }
        }
        $inventoryData.Add($inv)
        if ($Trial) { break }
    }
    if ($Trial) { return }
    if (-not $TrialReportPath) { $TrialReportPath=Join-Path $packet 'tiny1.json' }
    $tiny=ReadJson $TrialReportPath
    if (-not $tiny.trialOnly -or $tiny.status -cne 'passed' -or $tiny.filesHashed -ne 1) { throw 'Passed actual tiny1 prerequisite absent.' }
    for ($g=0;$g -lt 2;$g++) {
        for ($r=0;$r -lt 2;$r++) {
            Bound
            $group=if ($g -eq 0) {'source'} else {'backend'}
            $platform=if ($r -eq 0) {'windows-x64'} else {'linux-x64'}
            $rid=if ($r -eq 0) {'win-x64'} else {'linux-x64'}
            $prefix='artifacts/'+$(if ($g -eq 0) {'p1-source-package/'} else {'p1-backend/'})+$platform+'/'
            $map=@{}
            foreach ($row in $inventoryData[$g].files) { Bound; if ($row.platform -ceq $platform) { if ($map.ContainsKey($row.name)) {throw 'Duplicate member.'};$map[$row.name]=$row } }
            $reportName=if ($g -eq 0) {'source-package-v2.json'} else {'backend-coverage-v1.json'}
            $report=ReadJson $map[$reportName].path
            $receiptPath=Join-Path $repo ('artifacts/p1-supervision/ci-followup-v23/originals/production-receipt-'+$(if ($r -eq 0) {'windows'} else {'linux'})+'/native-receipt.json')
            $receipt=ReadJson $receiptPath
            $binding=if ($g -eq 0) {$receipt.reports.sourcePackage} else {$receipt.reports.backend}
            Check ($group+'/'+$rid+' physical report receipt binding') $binding.sha256 $map[$reportName].freshSha256
            Check ($group+'/'+$rid+' identity') $true ($report.candidateSha -ceq $candidate -and $report.candidateTreeSha -ceq $tree -and $report.targetRuntimeIdentifier -ceq $rid -and $report.hostRuntimeIdentifier -ceq $rid)
            Check ($group+'/'+$rid+' retained count') $(if ($g -eq 0) {785} else {103}) @($report.retainedArtifacts).Count
            foreach ($artifact in $report.retainedArtifacts) { $null=Bind ($group+'/'+$rid+' retained') $artifact $map $prefix }
            $buildBinding=if ($g -eq 0) {$report.releaseBuild.artifact} else {$report.releaseBuild}
            $build=ReadJson (Bind ($group+'/'+$rid+' build') $buildBinding $map $prefix)
            Check ($group+'/'+$rid+' production build') $true ($build.candidateSha -ceq $candidate -and $build.succeeded -ceq $true -and $build.sdkVersion -ceq '10.0.401' -and $build.configuration -ceq 'Release' -and $build.summary.warnings -eq 0 -and $build.summary.errors -eq 0)
            $snapBinding=if ($g -eq 0) {$report.sourceSnapshot.artifact} else {$report.sourceSnapshot}
            $snapshot=ReadJson (Bind ($group+'/'+$rid+' snapshot') $snapBinding $map $prefix)
            Check ($group+'/'+$rid+' candidate snapshot708') $true ($snapshot.verified -ceq $true -and $snapshot.candidateSha -ceq $candidate -and $snapshot.treeSha -ceq $tree -and @($snapshot.files).Count -eq 708)
            foreach ($input in $report.implementationInputs) {
                $null=Bind ($group+'/'+$rid+' implementation '+$input.name) $input.retainedAssembly $map $prefix
                Check ($group+'/'+$rid+' loaded/release/retained '+$input.name) $true ($input.loadedAssembly.sha256 -ceq $input.releaseOutput.sha256 -and $input.releaseOutput.sha256 -ceq $input.retainedAssembly.sha256)
            }
            $manifest=ReadJson (Bind ($group+'/'+$rid+' manifest') $report.retainedManifest $map $prefix)
            if ($g -eq 0) {
                Check ('source/'+$rid+' package denominator') 19 @($report.cases).Count
                Check ('source/'+$rid+' manifest denominator') 19 @($manifest.cases).Count
                $peCount=0;$nativeCount=0
                for ($c=0;$c -lt @($report.cases).Count -and $c -lt 19;$c++) {
                    Bound;$case=$report.cases[$c];$contract=$manifest.cases[$c];$label='source/'+$rid+'/'+$case.id
                    Check ($label+' contract identity/output/outcome') $true ($case.id -ceq $contract.id -and $case.expectedOutput -ceq $contract.expectedOutput -and $case.expectedOutcome -ceq $contract.expectedOutcome -and $case.status -ceq 'passed' -and $case.independentArtifactsIdentical -ceq $true -and @($case.independentBuilds).Count -eq 2)
                    foreach ($role in @('Producer','Consumer','Wrapper')) {
                        $original=$case.('original'+$role)
                        if ($original) {
                            $physical=Bind ($label+' '+$role) $original $map $prefix
                            Header ($label+' '+$role) $physical $true $rid;$peCount++
                            Verifier ($label+' '+$role) $case.($role.ToLowerInvariant()+'IlVerify') $original $map $prefix $rid
                            for ($b=0;$b -lt 2;$b++) {
                                $buildCase=$case.independentBuilds[$b];$roleName=$role.ToLowerInvariant();$retained=$buildCase.('retainedOriginal'+$role)
                                $null=Bind ($label+' build'+$b+' '+$role) $retained $map $prefix
                                Check ($label+' build'+$b+' '+$role+' original equality') $original.sha256 $retained.sha256
                                Check ($label+' build'+$b+' '+$role+' success') $true $buildCase.$roleName.success
                            }
                        }
                    }
                    Process ($label+' CoreCLR') $case.coreClr;Process ($label+' AOT') $case.nativeRun
                    Check ($label+' CoreCLR output') $case.expectedOutput (Normal $case.coreClr.stdout)
                    Check ($label+' AOT output') $case.expectedOutput (Normal $case.nativeRun.stdout)
                    $success=$case.expectedOutcome -ceq 'success'
                    Check ($label+' managed outcome') $success ($case.coreClr.exitCode -eq 0)
                    Check ($label+' AOT outcome') $success ($case.nativeRun.exitCode -eq 0)
                    $native=Bind ($label+' native') $case.nativeExecutable $map $prefix
                    Header ($label+' AOT') $native $false $rid;$nativeCount++
                    Check ($label+' native publication clean') $true ($case.nativePublish.exitCode -eq 0 -and $case.nativeWarningCount -eq 0 -and $case.hostCleanup.incomplete -ceq $false -and $case.hostCleanup.existsAfterCleanup -ceq $false)
                    $cases.Add([pscustomobject]@{group=$group;rid=$rid;id=$case.id;expectedOutcome=$case.expectedOutcome;expectedOutput=$case.expectedOutput;managedExit=$case.coreClr.exitCode;nativeExit=$case.nativeRun.exitCode;managedStderr=$case.coreClr.stderr;nativeStderr=$case.nativeRun.stderr;semanticOutcomeClassifierAccepted=$success})
                    if (-not $success) { $pending.Add([pscustomobject]@{id=$label;gap='Nonzero exit and expected stdout verified; independent unwound/aborted/double-panic stderr classifier and language approval still required.'}) }
                }
                Check ('source/'+$rid+' original PE39') 39 $peCount
                Check ('source/'+$rid+' original AOT19') 19 $nativeCount
                $components.Add([pscustomobject]@{group=$group;rid=$rid;packages=19;originalPE=$peCount;originalAOT=$nativeCount;retained=785;sdk=$build.sdkVersion;completeSemanticsAccepted=$false})
            } else {
                Check ('backend/'+$rid+' fixed matrix24') 24 @($report.cells).Count
                Check ('backend/'+$rid+' all7 witnesses') 7 @($report.witnesses).Count
                $witnessMap=@{}
                for ($w=0;$w -lt @($report.witnesses).Count -and $w -lt 7;$w++) {
                    Bound;$wit=$report.witnesses[$w];$label='backend/'+$rid+'/'+$wit.id;$witnessMap[$wit.id]=$wit
                    Header ($label+' original PE') (Bind ($label+' PE') $wit.originalPe $map $prefix) $true $rid
                    Verifier ($label+' ILVerify') $wit.ilVerify $wit.originalPe $map $prefix $rid
                    Process ($label+' managed') $wit.managedRun;Process ($label+' AOT') $wit.nativeAot.run
                    Check ($label+' managed output') $wit.expectedManagedOutput (Normal $wit.managedRun.stdout)
                    Check ($label+' managed exit') $wit.expectedManagedExitCode $wit.managedRun.exitCode
                    Check ($label+' native output') $wit.expectedNativeOutput (Normal $wit.nativeAot.run.stdout)
                    Check ($label+' native exit') 0 $wit.nativeAot.run.exitCode
                    Header ($label+' AOT') (Bind ($label+' native') $wit.nativeAot.executable $map $prefix) $false $rid
                    $null=Bind ($label+' registration source') $wit.registrationSource $map $prefix
                    if ($wit.source) {$null=Bind ($label+' rust source') $wit.source $map $prefix}
                    Check ($label+' AOT publish/cleanup') $true ($wit.nativeAot.publish.exitCode -eq 0 -and $wit.nativeAot.hostCleanupIncomplete -ceq $false -and $wit.nativeAot.hostDirectoryExists -ceq $false)
                    $cases.Add([pscustomobject]@{group=$group;rid=$rid;id=$wit.id;witnessKind=$wit.witnessKind;managedExit=$wit.managedRun.exitCode;nativeExit=$wit.nativeAot.run.exitCode})
                }
                $local=0;$blocked=0
                for ($c=0;$c -lt @($report.cells).Count -and $c -lt 24;$c++) {
                    Bound;$cell=$report.cells[$c]
                    if ($cell.runtimeIdentifier -ceq $rid) {
                        $local++;Check ('backend/'+$rid+' cell '+$cell.id) 'passed' $cell.status
                        foreach ($id in $cell.witnessIds) {Check ('backend/'+$rid+' witness link '+$id) $true $witnessMap.ContainsKey($id)}
                    } else {$blocked++;Check ('backend/'+$rid+' preserved foreign cell '+$cell.id) 'blocked' $cell.status}
                }
                Check ('backend/'+$rid+' local12') 12 $local;Check ('backend/'+$rid+' foreign12') 12 $blocked
                $components.Add([pscustomobject]@{group=$group;rid=$rid;matrixDenominator=24;localCells=$local;blockedForeignCells=$blocked;witnesses=7;retained=103;sdk=$build.sdkVersion;completeSemanticsAccepted=$false})
            }
            Write-Output ('audited '+$group+'/'+$rid+' checks='+$checks.Count+' issues='+$issues.Count)
        }
    }
} catch { $failure=$_.Exception.Message }
finally {
    $document=[ordered]@{schemaVersion=1;taskId='P1-GATE.03.CISEM-v1';status=if ($failure -or $issues.Count) {'failed'} else {'passed'};trialOnly=[bool]$Trial;candidateSha=$candidate;treeSha=$tree;runId='37710305713';attempt=1;sourceFileDenominator=1572;backendFileDenominator=234;sourcePackagesEach=19;sourcePeEach=39;sourceRetainedEach=785;backendMatrixDenominator=24;backendRetainedEach=103;filesHashed=$hashes.Count;checks=@($checks.ToArray());issues=@($issues.ToArray());failure=$failure;physicalFiles=@($hashes.ToArray());headers=@($headers.ToArray());cases=@($cases.ToArray());components=@($components.ToArray());pending=@($pending.ToArray());completeSemanticsAccepted=$false;completeDescendantContainmentVerified=$false;formalJointAccepted=$false;fullP1Closure=$false;fullP2Closure=$false;expandedSdk='10.0.400';productionSdk='10.0.401';externalChildrenLaunched=0;networkRequests=0;downloadedProgramsExecuted=0;objectsDeleted=0;maximumSeconds=$TimeoutSeconds;elapsedMilliseconds=$clock.ElapsedMilliseconds;maximumOutputBytes=33554432;hostIdentity=($hostProcess|Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine);parentChain=@($parentChain.ToArray());parentChainComplete=($parentPid -eq 0);finishedAt=[DateTimeOffset]::Now.ToString('O')}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($document|ConvertTo-Json -Depth 100))
    if ($bytes.Length -gt 33554432) {throw 'Audit output exceeds fixed32MiB cap.'}
    $outputStream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {$outputStream.Write($bytes)} finally {$outputStream.Dispose()}
}
if ($failure) {throw $failure}
if ($issues.Count) {throw ('Semantic audit issues='+$issues.Count)}
Write-Output ('checks='+$checks.Count+';files='+$hashes.Count+';headers='+$headers.Count+';cases='+$cases.Count)
