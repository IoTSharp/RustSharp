[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $WindowsReportSet,
    [Parameter(Mandatory)][string] $LinuxReportSet,
    [string] $EvidencePath = 'artifacts/p1-10/p1-suite-gate.json',
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [ValidateRange(1024,33554432)][int] $MaximumReportBytes = 16MB,
    [ValidateRange(10,300)][int] $TimeoutSeconds = 120
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow
$checks = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$documents = @{}
$requirementBindings = [Collections.Generic.List[object]]::new()
$harnessError = $null
$required = @('coverage','expandedDifferential','expandedPlatform','build','harness','baselineAudit','sourceSnapshot')

function Resolve-SuitePath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Evidence path is missing.' }
    $full = [IO.Path]::GetFullPath($Path, $root)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $full.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Evidence path escapes repository root.' }
    return $full
}
function Read-SuiteInput([string] $Name, [string] $Path, [string] $ExpectedHash = '') {
    if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds -or $inputs.Count -ge 16) { throw 'Suite aggregation item/time bound exceeded.' }
    $full = Resolve-SuitePath $Path
    if (-not [IO.File]::Exists($full)) { throw 'blocked: evidence report is missing: ' + $Name }
    if (([IO.FileInfo]::new($full)).Length -gt $MaximumReportBytes) { throw 'Report byte bound exceeded: ' + $Name }
    $bytes = [IO.File]::ReadAllBytes($full)
    if ($bytes.Length -gt $MaximumReportBytes) { throw 'Report byte bound exceeded: ' + $Name }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($ExpectedHash -and ($ExpectedHash -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedHash -ine $hash)) { throw 'Report-set hash binding is stale: ' + $Name }
    $inputs.Add([pscustomobject]@{name=$Name;path=[IO.Path]::GetRelativePath($root,$full).Replace('\','/');sha256=$hash;bytes=$bytes.Length})
    return ConvertFrom-P1StrictJson $bytes
}
function Add-SuiteCheck([string] $Name, [object[]] $Errors) {
    $status = if ($Errors.Count -eq 0) { 'passed' } elseif (@($Errors | Where-Object { $_ -like 'blocked:*' }).Count -gt 0) { 'blocked' } else { 'failed' }
    $checks.Add([pscustomobject]@{name=$Name;status=$status;errors=@($Errors)})
}

try {
    foreach ($hostSpec in @(@{rid='win-x64';path=$WindowsReportSet},@{rid='linux-x64';path=$LinuxReportSet})) {
        $rid = $hostSpec.rid
        $set = $null; $setErrors = @()
        try {
            $set = Read-SuiteInput ($rid + '-reportSet') $hostSpec.path
            $reports = Get-P1Property $set 'reports'
            if ((Get-P1Property $set 'schemaVersion') -ne 1 -or (Get-P1Property $set 'evidenceKind') -cne 'p1-suite-report-set' -or
                (Get-P1Property $set 'candidateSha') -cne $CandidateSha -or (Get-P1Property $set 'runtimeIdentifier') -cne $rid -or
                $null -eq $reports -or @($reports.PSObject.Properties).Count -ne 7 -or
                @($reports.PSObject.Properties.Name | Where-Object { $_ -cnotin $required }).Count -gt 0) { throw 'Report-set identity, platform or seven-report contract is invalid.' }
        }
        catch { $setErrors = @($_.Exception.Message) }
        # Fixed seven items on each host. Missing set members remain required.
        foreach ($name in $required) {
            $errors = @($setErrors)
            $document = $null
            try {
                if ($setErrors.Count -eq 0) {
                    $binding = Get-P1Property (Get-P1Property $set 'reports') $name
                    $document = Read-SuiteInput ($rid + '-' + $name) ([string](Get-P1Property $binding 'path')) ([string](Get-P1Property $binding 'sha256'))
                    if ([string]::IsNullOrWhiteSpace([string](Get-P1Property $binding 'sha256'))) { throw 'Report-set SHA-256 binding is missing.' }
                    if ((Get-P1Property $document 'candidateSha') -cne $CandidateSha) { throw 'Report candidate SHA does not match the actual candidate.' }
                    $documents[$rid + '-' + $name] = $document
                }
            }
            catch { $errors += $_.Exception.Message }
            if ($null -ne $document) {
                $snapshot = $null
                # Snapshot validation is delayed until all bounded inputs have
                # been loaded, allowing either report-set property order.
                switch ($name) {
                    'coverage' { $errors += @(Test-P1CoverageEvidence $document $root $CandidateSha) }
                    'expandedDifferential' { $errors += @(Test-P1ExpandedEvidence $document $root 'p1-differential-v3' $rid $CandidateSha) }
                    'expandedPlatform' { $errors += @(Test-P1ExpandedEvidence $document $root 'p1-platform-v2' $rid $CandidateSha) }
                    'build' { $errors += @(Test-P1BuildEvidence $document $CandidateSha $rid $root) }
                }
            }
            Add-SuiteCheck ($rid + '-' + $name) $errors
        }
    }
    $bindingErrors = [Collections.Generic.List[string]]::new()
    foreach ($rid in @('win-x64','linux-x64')) {
        $snapshotKey = $rid + '-sourceSnapshot'
        if (-not $documents.ContainsKey($snapshotKey)) { $bindingErrors.Add('blocked: actual candidate source snapshot is missing.'); continue }
        $snapshot = $documents[$snapshotKey]
        $tree = [string](Get-P1Property $snapshot 'treeSha')
        $snapshotErrors = @(Test-P1SnapshotEvidence $snapshot $root $CandidateSha)
        if ($snapshotErrors.Count -gt 0) { $bindingErrors.AddRange([string[]]$snapshotErrors) }
        foreach ($name in @('harness','baselineAudit')) {
            $key = $rid + '-' + $name
            if (-not $documents.ContainsKey($key)) { $bindingErrors.Add('blocked: candidate verification report is missing: ' + $key); continue }
            $errors = if ($name -ceq 'harness') { @(Test-P1HarnessEvidence $documents[$key] $CandidateSha $tree $rid $documents[$rid + '-build']) } else { @(Test-P1BaselineEvidence $documents[$key] $CandidateSha $tree) }
            $check = @($checks | Where-Object name -CEQ $key)[0]
            $check.errors = @($check.errors) + @($errors)
            if ($check.errors.Count -gt 0) { $check.status = 'failed' }
        }
        $buildKey = $rid + '-build'
        if ($documents.ContainsKey($buildKey)) {
            $compilerHash=[string](Get-P1Property $documents[$buildKey] 'compilerSha256')
            if ($compilerHash -notmatch '^[a-fA-F0-9]{64}$') { $bindingErrors.Add('Fresh Release compiler SHA-256 is missing: '+$rid) }
            foreach ($name in @('expandedDifferential','expandedPlatform')) {
                $key=$rid+'-'+$name
                if (-not $documents.ContainsKey($key) -or (Get-P1Property $documents[$key] 'compilerSha256') -ine $compilerHash) { $bindingErrors.Add('Expanded execution did not use the freshly built candidate compiler: '+$key) }
            }
        }
        $snapshotCheck = @($checks | Where-Object name -CEQ $snapshotKey)[0]
        $snapshotCheck.errors = @($snapshotCheck.errors) + $snapshotErrors
        if ($snapshotCheck.errors.Count -gt 0) { $snapshotCheck.status = 'failed' }
    }
    if ($documents.ContainsKey('win-x64-sourceSnapshot') -and $documents.ContainsKey('linux-x64-sourceSnapshot') -and
        (Get-P1Property $documents['win-x64-sourceSnapshot'] 'treeSha') -cne (Get-P1Property $documents['linux-x64-sourceSnapshot'] 'treeSha')) { $bindingErrors.Add('Windows/Linux candidate trees differ.') }
    if (@($checks | Where-Object status -CNE 'passed').Count -gt 0) { $bindingErrors.Add('blocked: required backend/build/harness/coverage evidence is incomplete.') }
    if ($documents.ContainsKey('win-x64-coverage') -and $documents.ContainsKey('linux-x64-coverage')) {
        $coverage = $documents['win-x64-coverage']
        $requirements = @(Get-P1Property $coverage 'requirements')
        $inventory = @(Get-P1Property $coverage 'cases')
        if ($requirements.Count -ne 40 -or $inventory.Count -ne 160) { $bindingErrors.Add('Frozen requirement/backend inventory denominator is incomplete.') }
        else { for ($index=0; $index -lt 40 -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds; $index++) {
            $requirement = $requirements[$index]
            $cases = @($inventory | Where-Object requirementId -CEQ $requirement.id)
            $backends = @($cases.backends | Sort-Object -Unique)
            $backendBindings = [Collections.Generic.List[object]]::new()
            foreach ($backend in $backends) {
                $names = switch ($backend) {
                    'harness' { @('win-x64-harness','linux-x64-harness') }
                    'rustc-1.98' { @('win-x64-expandedDifferential','linux-x64-expandedDifferential') }
                    'coreclr' { @('win-x64-expandedPlatform','linux-x64-expandedPlatform') }
                    'ilverify' { @('win-x64-expandedPlatform','linux-x64-expandedPlatform') }
                    'windows-x64-aot' { @('win-x64-expandedPlatform') }
                    'linux-x64-aot' { @('linux-x64-expandedPlatform') }
                    default { $bindingErrors.Add('Unknown frozen backend: ' + $backend); @() }
                }
                $backendBindings.Add([pscustomobject]@{backend=$backend;reports=@($names)})
            }
            # The catalogue's four category rows declare required backends;
            # they are not 160 extra executable fixtures. The named expanded
            # suite denominators and full harness above prove this suite scope.
            $requirementBindings.Add([pscustomobject]@{id=$requirement.id;leaf=$requirement.leaf;classification=$requirement.classification;inventoryCaseIds=@($cases.id);requiredBackends=$backends;backendBindings=@($backendBindings.ToArray());bindingScope='frozen suite contract';languageClosureClaimed=$false})
        } }
    }
    if ($requirementBindings.Count -ne 40) { $bindingErrors.Add('blocked: not all 40 frozen requirements received backend report bindings.') }
    Add-SuiteCheck 'fixed-requirement-backend-binding' @($bindingErrors.ToArray())
}
catch { $harnessError = $_.Exception.Message }

# Fail-closed finalization retains the denominator even on malformed evidence.
for ($index=$checks.Count; $index -lt 15 -and $clock.Elapsed.TotalSeconds -lt ($TimeoutSeconds + 5); $index++) {
    Add-SuiteCheck ('unvalidated-' + $index) @('blocked: aggregation could not validate required evidence: ' + $harnessError)
}
$failed = @($checks | Where-Object status -CEQ 'failed').Count
$blocked = @($checks | Where-Object status -CEQ 'blocked').Count
$passed = @($checks | Where-Object status -CEQ 'passed').Count
$status = if ($failed -gt 0) { 'failed' } elseif ($blocked -gt 0 -or $checks.Count -ne 15 -or $harnessError) { 'blocked' } else { 'passed' }
$report = [ordered]@{
    schemaVersion=1; evidenceKind='p1-versioned-suite-aggregate'; profile='p1-suite-gate-v1'; candidateSha=$CandidateSha
    scope='P1-10 frozen suite conjunction'; fullP1Closure=$false
    summary=[ordered]@{status=$status;exitCode=if($status -ceq 'passed'){0}elseif($status -ceq 'failed'){1}else{2};denominator=15;executed=$checks.Count;passed=$passed;failed=$failed;blocked=$blocked;skipped=0}
    denominators=[ordered]@{requirements=40;coverageInventoryCases=160;expandedDifferentialPerPlatform=32;expandedPlatformPerPlatform=24;immutableBaselines=60;minimumFullHarnessPerPlatform=464}
    checks=@($checks.ToArray());inputs=@($inputs.ToArray());requirementBindings=@($requirementBindings.ToArray())
    execution=[ordered]@{startedAtUtc=$started.ToString('O');finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');timeoutSeconds=$TimeoutSeconds;maximumInputReports=16;maximumReportBytes=$MaximumReportBytes;candidateValidationProcesses=@($script:P1SnapshotValidationProcesses.ToArray())}
    harnessError=$harnessError
}
$destination = Resolve-SuitePath $EvidencePath
if (@($inputs | Where-Object { (Resolve-SuitePath $_.path) -ceq $destination }).Count -gt 0) { throw 'Aggregate output must not overwrite an input.' }
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
$staging = $destination + '.tmp-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
try { [IO.File]::WriteAllText($staging, ($report | ConvertTo-Json -Depth 24)); [IO.File]::Move($staging,$destination,$true) }
finally { if ([IO.File]::Exists($staging)) { [IO.File]::Delete($staging) } }
Write-Output ($report | ConvertTo-Json -Depth 24)
exit $report.summary.exitCode
