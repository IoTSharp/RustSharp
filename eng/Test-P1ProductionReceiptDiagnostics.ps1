[CmdletBinding()]
param(
    [Parameter()] [string] $ProductionScriptPath = (Join-Path $PSScriptRoot 'Invoke-P1ProductionNativeEvidence.ps1'),
    [Parameter()] [string] $PatchDraftPath = (Join-Path $PSScriptRoot 'fixtures/p1-receipt-diagnostic-v1.patch'),
    [Parameter(Mandatory)] [string] $ResultPath,
    [Parameter()] [ValidateSet('Tiny', 'All')] [string] $Mode = 'All',
    [Parameter()] [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$suiteClock = [Diagnostics.Stopwatch]::StartNew()
$suiteSeconds = 30
$maximumSeconds = 10
$clock = [Diagnostics.Stopwatch]::StartNew()
$startedAt = [DateTimeOffset]::Now.ToString('O')
$output = [IO.Path]::GetFullPath($ResultPath)
if (Test-Path -LiteralPath $output) { throw 'Refusing to overwrite prior utility evidence.' }
$delivery = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($delivery)) { throw 'Result parent directory must already exist.' }
$taskId = [Guid]::NewGuid().ToString('N')
$sandbox = [IO.Path]::GetFullPath((Join-Path $delivery ('.utility-owned-' + $taskId)))
if (Test-Path -LiteralPath $sandbox) { throw 'Exclusive sandbox already exists.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$results = [Collections.Generic.List[object]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$primaryError = $null
$provenance = [ordered]@{}
$sandboxCreated = $false
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$cases = @(if ($Mode -eq 'Tiny') { 'success-equalhash' } else { 'success-equalhash'; 'failed-validator-nullhash'; 'passed-validator-changedhash' })

function Check-UtilityBudget {
    $CancellationToken.ThrowIfCancellationRequested()
    if ($suiteClock.Elapsed.TotalSeconds -ge $suiteSeconds) { throw 'Offline utility deadline expired.' }
}
function Get-TextHash([string] $Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($Text)))
}
function Read-BoundedText([string] $Path) {
    Check-UtilityBudget
    $absolute = (Resolve-Path -LiteralPath $Path).Path
    if ((Get-Item -LiteralPath $absolute).Length -gt 1048576) { throw 'Utility input exceeds 1MiB.' }
    return [IO.File]::ReadAllText($absolute).Replace("`r`n", "`n")
}

try {
    $CancellationToken.ThrowIfCancellationRequested()
    $productionPath = (Resolve-Path -LiteralPath $ProductionScriptPath).Path
    $patchPath = (Resolve-Path -LiteralPath $PatchDraftPath).Path
    $productionHash = (Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash
    $patchHash = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash
    $source = Read-BoundedText $productionPath
    $patch = Read-BoundedText $patchPath
    $patchLines = @($patch -split "`n")
    if ($patchLines.Count -gt 128) { throw 'Diagnostic draft exceeds the 128-line extraction bound.' }
    $oldLines = [Collections.Generic.List[string]]::new()
    $newLines = [Collections.Generic.List[string]]::new()
    # Reviewed comparisons: <=128 lines plus monotonic deadline and caller cancellation.
    for ($lineIndex = 0; $lineIndex -lt $patchLines.Count; $lineIndex++) {
        Check-UtilityBudget
        $line = $patchLines[$lineIndex]
        if ($line.StartsWith('-') -and -not $line.StartsWith('---')) { $oldLines.Add($line.Substring(1)) }
        if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) { $newLines.Add($line.Substring(1)) }
    }
    if ($oldLines.Count -ne 2 -or $newLines.Count -lt 10 -or $newLines.Count -gt 32) { throw 'Unexpected diagnostic draft shape.' }
    $baselineText = [string]::Join("`n", $oldLines.ToArray())
    $candidateText = [string]::Join("`n", $newLines.ToArray())
    $oldPosition = $source.IndexOf($baselineText, [StringComparison]::Ordinal)
    $candidatePosition = $source.IndexOf($candidateText, [StringComparison]::Ordinal)
    if ($oldPosition -ge 0) {
        if ($source.LastIndexOf($baselineText, [StringComparison]::Ordinal) -ne $oldPosition) { throw 'Baseline fragment is not unique.' }
        $baselineText = $source.Substring($oldPosition, $baselineText.Length)
        $sourceState = 'pre-fix-source-fragment-extracted'
        $candidatePresent = $false
    } elseif ($candidatePosition -ge 0) {
        if ($source.LastIndexOf($candidateText, [StringComparison]::Ordinal) -ne $candidatePosition) { throw 'Candidate fragment is not unique.' }
        $candidateText = $source.Substring($candidatePosition, $candidateText.Length)
        $sourceState = 'candidate-source-fragment-extracted'
        $candidatePresent = $true
    } else { throw 'Neither exact diagnostic fragment is present in the real production script.' }
    $budgetPattern = [regex]::new('(?ms)^function Check-Budget\(\[int\] \$Reserve = 0\) \{\n.*?^\}', [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(1))
    $closedPattern = [regex]::new('(?m)^\$closed = .+$', [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(1))
    $budgetMatch = $budgetPattern.Match($source)
    $closedMatch = $closedPattern.Match($source)
    if (-not $budgetMatch.Success -or -not $closedMatch.Success) { throw 'Real production budget/closeout fragments were not found.' }
    $budgetText = $budgetMatch.Value
    $closedText = $closedMatch.Value
    $provenance = [ordered]@{
        ProductionScriptPath = $productionPath
        ProductionScriptSha256 = $productionHash
        PatchDraftPath = $patchPath
        PatchDraftSha256 = $patchHash
        SourceFragmentState = $sourceState
        CandidatePresentInRealSource = $candidatePresent
        BaselineFragmentSha256 = Get-TextHash $baselineText
        CandidateFragmentSha256 = Get-TextHash $candidateText
        BudgetFragmentSha256 = Get-TextHash $budgetText
        CloseoutFragmentSha256 = Get-TextHash $closedText
        BaselineFragment = $baselineText
        CandidateFragment = $candidateText
        BudgetFragment = $budgetText
        CloseoutFragment = $closedText
    }
    $null = [IO.Directory]::CreateDirectory($sandbox)
    $sandboxCreated = $true
    [IO.File]::WriteAllText((Join-Path $sandbox 'owner.json'), ([ordered]@{ TaskId = $taskId; Pid = $PID; Path = $sandbox } | ConvertTo-Json), $utf8)
    $fragmentNames = @('baseline.fragment.ps1', 'candidate.fragment.ps1', 'budget.fragment.ps1', 'closed.fragment.ps1')
    $fragmentTexts = @($baselineText, $candidateText, $budgetText, $closedText)
    for ($fragmentIndex = 0; $fragmentIndex -lt 4; $fragmentIndex++) {
        Check-UtilityBudget
        [IO.File]::WriteAllText((Join-Path $sandbox $fragmentNames[$fragmentIndex]), $fragmentTexts[$fragmentIndex], $utf8)
    }
    $baselineBlock = [ScriptBlock]::Create($baselineText)
    $candidateBlock = [ScriptBlock]::Create($candidateText)
    $closeoutBlock = [ScriptBlock]::Create($closedText)
    . ([ScriptBlock]::Create($budgetText))

    function Invoke-FragmentFixture([ScriptBlock] $Fragment, [string] $CaseName) {
        Check-UtilityBudget
        $hashA = 'A' * 64
        $hashB = 'B' * 64
        $stages = @(
            @{name = 'utility-source-producer'; status = 'passed'; reportSha256 = $null; error = $null},
            @{name = 'utility-source-validator'; status = 'passed'; reportSha256 = $hashA; error = $null},
            @{name = 'utility-backend-producer'; status = 'passed'; reportSha256 = $null; error = $null},
            @{name = 'utility-backend-validator'; status = 'passed'; reportSha256 = $hashA; error = $null}
        )
        $reports = @{sourcePackage = @{sha256 = $hashA}; backend = @{sha256 = $hashA}}
        if ($CaseName -eq 'failed-validator-nullhash') {
            $stages[1].status = 'failed'
            $stages[1].reportSha256 = $null
            $stages[1].error = 'utility validator failure'
        } elseif ($CaseName -eq 'passed-validator-changedhash') { $stages[1].reportSha256 = $hashB }
        $failures = [Collections.Generic.List[string]]::new()
        $ready = $false
        $closed = $false
        try { . $Fragment } catch { $failures.Add($_.Exception.Message) }
        # Execute the exact real closure expression. ready=false keeps every offline fixture closed=false.
        . $closeoutBlock
        return [ordered]@{ Failure = if ($failures.Count -gt 0) { $failures[0] } else { $null }; Closed = [bool]$closed; Ready = $ready; ValidatorStatus = $stages[1].status; ValidatedHash = $stages[1].reportSha256; PublishedHash = $reports.sourcePackage.sha256 }
    }
    # Exactly one tiny trial or three fixed controls, plus 30s and cancellation; no retries or child processes.
    for ($caseIndex = 0; $caseIndex -lt $cases.Count; $caseIndex++) {
        Check-UtilityBudget
        $caseName = $cases[$caseIndex]
        $before = Invoke-FragmentFixture $baselineBlock $caseName
        $after = Invoke-FragmentFixture $candidateBlock $caseName
        $passed = -not $before.Closed -and -not $after.Closed -and -not $after.Ready
        switch ($caseName) {
            'success-equalhash' { $passed = $passed -and $null -eq $before.Failure -and $null -eq $after.Failure -and $after.ValidatedHash -ceq $after.PublishedHash }
            'failed-validator-nullhash' { $passed = $passed -and $before.Failure -cmatch 'Validated report bytes changed' -and $after.Failure -cmatch 'successful validation report hash is unavailable' -and $after.Failure -cmatch 'utility validator failure' -and $after.Failure -cnotmatch 'validated report bytes changed' -and $null -eq $after.ValidatedHash -and $after.ValidatorStatus -ceq 'failed' }
            'passed-validator-changedhash' { $passed = $passed -and $before.Failure -cmatch 'Validated report bytes changed' -and $after.Failure -cmatch 'validated report bytes changed' -and $after.Failure -cnotmatch 'hash is unavailable' -and $after.ValidatorStatus -ceq 'passed' -and $after.ValidatedHash -cne $after.PublishedHash }
        }
        $results.Add([ordered]@{ Name = $caseName; Passed = [bool]$passed; Baseline = $before; Candidate = $after })
    }
    if ((Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash -cne $productionHash -or (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash -cne $patchHash) { throw 'Utility input bytes changed during evaluation.' }
}
catch { $primaryError = $_ }
finally {
    if ($sandboxCreated) {
        try {
            $item = Get-Item -LiteralPath $sandbox
            $owner = [IO.File]::ReadAllText((Join-Path $sandbox 'owner.json')) | ConvertFrom-Json
            if ($owner.TaskId -cne $taskId -or $owner.Pid -ne $PID -or $owner.Path -cne $sandbox -or [IO.Path]::GetDirectoryName($sandbox) -cne $delivery -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Exclusive utility sandbox ownership mismatch.' }
            $files = @(Get-ChildItem -LiteralPath $sandbox -Force)
            if ($files.Count -gt 5) { throw 'Utility sandbox exceeds five owned files.' }
            $cleanupClock = [Diagnostics.Stopwatch]::StartNew()
            for ($fileIndex = 0; $fileIndex -lt $files.Count; $fileIndex++) {
                if ($cleanupClock.ElapsedMilliseconds -ge 5000) { throw 'Utility cleanup inspection deadline expired.' }
                if ($files[$fileIndex].PSIsContainer -or ($files[$fileIndex].Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetDirectoryName($files[$fileIndex].FullName) -cne $sandbox) { throw 'Unexpected utility sandbox object; no broad deletion.' }
            }
            for ($fileIndex = 0; $fileIndex -lt $files.Count; $fileIndex++) {
                if ($cleanupClock.ElapsedMilliseconds -ge 5000) { throw 'Utility cleanup deletion deadline expired.' }
                Remove-Item -LiteralPath $files[$fileIndex].FullName -Force
            }
            Remove-Item -LiteralPath $sandbox
        } catch { $cleanupFailures.Add($_.Exception.Message) }
    }
}
$passedCount = @($results | Where-Object { $_.Passed }).Count
$report = [ordered]@{
    SchemaVersion = 1; EvidenceKind = 'offline-receipt-diagnostic-utility'; Mode = $Mode
    StartedAt = $startedAt; FinishedAt = [DateTimeOffset]::Now.ToString('O')
    ExpectedControls = $cases.Count; ObservedControls = $results.Count; PassedControls = $passedCount
    UtilityScriptPath = $PSCommandPath; UtilityScriptSha256 = $scriptHash
    Provenance = $provenance; Controls = @($results.ToArray())
    SyntheticUtilityHashes = $true; ActionsExecuted = $false; ProducersExecuted = $false; FormalGateAccepted = $false; PhaseClosed = $false
    Sandbox = $sandbox; SandboxRemoved = -not (Test-Path -LiteralPath $sandbox); CleanupFailures = @($cleanupFailures.ToArray())
    PrimaryFailure = if ($null -ne $primaryError) { $primaryError.Exception.Message } else { $null }
}
try {
    $bytes = $utf8.GetBytes(($report | ConvertTo-Json -Depth 12))
    $stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
} catch { $cleanupFailures.Add('Evidence publication failed: ' + $_.Exception.Message) }
if ($null -ne $primaryError) { $PSCmdlet.ThrowTerminatingError($primaryError) }
if ($results.Count -ne $cases.Count -or $passedCount -ne $cases.Count -or $cleanupFailures.Count -gt 0) { throw "Receipt diagnostic utility controls failed: $passedCount/$($cases.Count)." }
Write-Host "Offline receipt diagnostic controls passed: $passedCount/$($cases.Count); all closed=false."
