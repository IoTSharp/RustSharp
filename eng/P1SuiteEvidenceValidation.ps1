# P1-10 suite evidence is deliberately independent of P1-GATE publication.
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'P1EvidenceValidation.ps1')
$script:P1SnapshotValidationProcesses = [Collections.Generic.List[object]]::new()
$script:P1SnapshotValidationClock = $null

function Invoke-P1ValidationGit([string] $Root, [string[]] $Arguments, [string] $InputText = '') {
    # A native candidate checks its main and two build snapshots (12 calls).
    # The two-RID suite checks six snapshots (24); all share a 110-second bound.
    # Importing helpers before a fresh build must not spend that validation
    # budget. Start it with the first real Git check and never restart it.
    if ($null -eq $script:P1SnapshotValidationClock) { $script:P1SnapshotValidationClock = [Diagnostics.Stopwatch]::StartNew() }
    if ($script:P1SnapshotValidationProcesses.Count -ge 24 -or $script:P1SnapshotValidationClock.Elapsed.TotalSeconds -ge 110 -or $Arguments.Count -gt 12 -or $InputText.Length -gt 1MB) { throw 'Snapshot Git item/input/time bound exceeded.' }
    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $info = [Diagnostics.ProcessStartInfo]::new($git)
    $info.WorkingDirectory=$Root; $info.UseShellExecute=$false
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.RedirectStandardInput=$true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo=$info
    $record=[ordered]@{pid=$null;parentPid=$PID;startedAtUtc=$null;filePath=$git;arguments=$Arguments;workingDirectory=$Root;exitCode=$null;cleanupComplete=$false}
    try {
        # Record the launch clock before Start. A short-lived Git process may
        # exit before Linux can retrieve its operating-system StartTime.
        $record.startedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
        if (-not $process.Start()) { throw 'Cannot start candidate Git verification.' }
        $record.pid=$process.Id
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText); $process.StandardInput.Close()
        $remaining=[Math]::Max(1,[Math]::Min(10000,[int](110000-$script:P1SnapshotValidationClock.ElapsedMilliseconds)))
        if (-not $process.WaitForExit($remaining)) { throw 'Candidate Git verification timed out.' }
        $drainRemaining=[Math]::Max(1,[Math]::Min(2000,[int](110000-$script:P1SnapshotValidationClock.ElapsedMilliseconds)))
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),$drainRemaining)) { throw 'Candidate Git output drain timed out.' }
        $record.exitCode=$process.ExitCode
        if ($stdout.Result.Length -gt 4MB -or $stderr.Result.Length -gt 1MB) { throw 'Candidate Git output bound exceeded.' }
        if ($process.ExitCode -ne 0) { throw 'Candidate Git object verification failed.' }
        return $stdout.Result.TrimEnd("`r","`n")
    }
    finally {
        # This exact Process instance was started above; PID/start/path and
        # structured arguments are retained before any tree termination.
        if ($null -ne $record.pid -and -not $process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(5000) }
        $record.cleanupComplete=$null -ne $record.pid -and $process.HasExited
        $record.finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
        $script:P1SnapshotValidationProcesses.Add([pscustomobject]$record)
        $process.Dispose()
    }
}

function Test-P1SnapshotEvidence($Report, [string] $Root, [string] $CandidateSha) {
    $errors=[Collections.Generic.List[string]]::new()
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        if ($CandidateSha -notmatch '^[a-fA-F0-9]{40}$' -or (Get-P1Property $Report 'schemaVersion') -ne 1 -or
            (Get-P1Property $Report 'evidenceKind') -cne 'p1-candidate-source-snapshot' -or (Get-P1Property $Report 'candidateSha') -cne $CandidateSha -or
            (Get-P1Property $Report 'verified') -cne $true -or (Get-P1Property $Report 'scope') -cne 'compiler-test-fixture-tooling-inputs') { return @('Actual source snapshot identity/verification is invalid.') }
        $objects=(Invoke-P1ValidationGit $Root @('rev-parse',($CandidateSha + '^{commit}'),($CandidateSha + '^{tree}'))) -split "`n"
        if ($objects.Count -ne 2 -or $objects[0] -cne $CandidateSha) { return @('An actual Git candidate commit object is required.') }
        $tree=$objects[1]
        if ((Get-P1Property $Report 'treeSha') -cne $tree) { return @('Actual Git candidate tree differs from source snapshot.') }
        $entries=(Invoke-P1ValidationGit $Root @('-c','core.quotepath=false','ls-tree','-r',$CandidateSha)) -split "`n"
        if ($entries.Count -gt 8192) { return @('Candidate tree exceeds file bound.') }
        $scope='^(src/|tests/|tools/|eng/|\.github/|\.config/|samples/|[^/]+\.(props|targets|slnx|json)$|\.gitattributes$|\.gitignore$)'
        $expected=[Collections.Generic.List[object]]::new()
        foreach ($entry in $entries) {
            if ($clock.Elapsed.TotalSeconds -ge 45) { throw 'Snapshot enumeration deadline exceeded.' }
            if ($entry -notmatch '^100(?:644|755) blob ([a-f0-9]{40})\t(.+)$') { throw 'Candidate tree has unsupported file entry.' }
            $path=$Matches[2]; $blob=$Matches[1]
            if ($path -match $scope) { $expected.Add([pscustomobject]@{path=$path;gitBlobOid=$blob}) }
        }
        $files=@(Get-P1Property $Report 'files')
        if ($files.Count -lt 1 -or $files.Count -ne $expected.Count -or (Get-P1Property (Get-P1Property $Report 'summary') 'denominator') -ne $expected.Count -or
            (Get-P1Property (Get-P1Property $Report 'summary') 'verified') -ne $expected.Count -or (Get-P1Property (Get-P1Property $Report 'summary') 'mismatched') -cne 0) { return @('Source snapshot must close the actual Git compiler-input denominator.') }
        $working=(Invoke-P1ValidationGit $Root @('-c','core.quotepath=false','ls-files','--cached','--others','--exclude-standard')) -split "`n"
        if ($working.Count -gt 8192) { throw 'Working source inventory exceeds file bound.' }
        $workingInputs=@($working | Where-Object { $_ -match $scope } | Sort-Object -Unique)
        if ($workingInputs.Count -ne $expected.Count -or @(Compare-Object $workingInputs @($expected.path | Sort-Object) -CaseSensitive).Count -gt 0) { throw 'Working source inventory differs from actual candidate.' }
        $actualOids=(Invoke-P1ValidationGit $Root @('hash-object','--stdin-paths') (($expected.path | ForEach-Object { '"'+$_+'"' }) -join "`n")) -split "`n"
        if ($actualOids.Count -ne $expected.Count) { throw 'Working candidate blob denominator differs.' }
        for ($index=0; $index -lt $expected.Count -and $index -lt 8192 -and $clock.Elapsed.TotalSeconds -lt 45; $index++) {
            $item=$files[$index]; $known=$expected[$index]
            if ((Get-P1Property $item 'path') -cne $known.path -or (Get-P1Property $item 'gitBlobOid') -cne $known.gitBlobOid -or $actualOids[$index] -cne $known.gitBlobOid) { $errors.Add('Source snapshot file/blob binding differs: '+$known.path); continue }
            $full=[IO.Path]::GetFullPath($known.path,$Root)
            if (-not [IO.File]::Exists($full) -or ([IO.FileInfo]::new($full)).Length -gt 4MB -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ine (Get-P1Property $item 'sha256')) { $errors.Add('Source snapshot compiler-input SHA-256 is stale: '+$known.path) }
        }
        if ($index -ne $expected.Count) { $errors.Add('Snapshot validation exceeded file/time bounds.') }
    }
    catch { $errors.Add('Actual candidate source verification failed: '+$_.Exception.Message) }
    return @($errors.ToArray())
}

function ConvertFrom-P1StrictJson([byte[]] $Bytes) {
    if ($Bytes.Length -lt 1 -or $Bytes.Length -gt 32MB) { throw 'JSON byte bound exceeded.' }
    $options = [Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 48
    $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes), $options)
    try {
        $stack = [Collections.Generic.Stack[Text.Json.JsonElement]]::new()
        $stack.Push($document.RootElement)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $visited = 0
        for ($iteration = 0; $iteration -lt 262144 -and $stack.Count -gt 0 -and $clock.Elapsed.TotalSeconds -lt 10; $iteration++) {
            $node = $stack.Pop(); $visited++
            if ($node.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $node.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) { throw 'Duplicate JSON property: ' + $property.Name }
                    if ($stack.Count -ge 262144) { throw 'JSON token bound exceeded.' }
                    $stack.Push($property.Value)
                }
            }
            elseif ($node.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                if ($node.GetArrayLength() -gt 16384) { throw 'JSON array item bound exceeded.' }
                foreach ($element in $node.EnumerateArray()) {
                    if ($stack.Count -ge 262144) { throw 'JSON token bound exceeded.' }
                    $stack.Push($element)
                }
            }
        }
        if ($stack.Count -gt 0 -or $visited -ge 262144) { throw 'JSON token/time bound exceeded.' }
        return [Text.Encoding]::UTF8.GetString($Bytes) | ConvertFrom-Json -Depth 48
    }
    finally { $document.Dispose() }
}

function Test-P1SourceProvenance($Report, [string] $CandidateSha, [string] $TreeSha, [Collections.Generic.List[string]] $Errors) {
    $source = Get-P1Property $Report 'sourceProvenance'
    if ((Get-P1Property $source 'candidateSha') -cne $CandidateSha -or
        (Get-P1Property $source 'candidateTreeSha') -cne $TreeSha -or
        (Get-P1Property $source 'candidateMatchesWorkingTree') -cne $true -or
        @((Get-P1Property $source 'errors')).Count -gt 0 -or
        (Get-P1Property $source 'checkedFileCount') -le 0) { $Errors.Add('Actual candidate source/tree provenance is missing or mismatched.') }
}

function Test-P1FixedSummary($Report, [int] $Denominator, [Collections.Generic.List[string]] $Errors) {
    $summary = Get-P1Property $Report 'summary'
    foreach ($name in @('denominator', 'executed', 'passed')) {
        $value = Get-P1Property $summary $name
        if (($value -isnot [int] -and $value -isnot [long]) -or $value -ne $Denominator) { $Errors.Add("summary.$name must close fixed denominator $Denominator.") }
    }
    foreach ($name in @('failed', 'blocked', 'skipped')) {
        $value = Get-P1Property $summary $name
        if (($value -isnot [int] -and $value -isnot [long]) -or $value -ne 0) { $Errors.Add("summary.$name must be integer zero.") }
    }
}

function Test-P1CoverageEvidence($Report, [string] $Root, [string] $CandidateSha) {
    $errors = [Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ((Get-P1Property $Report 'schemaVersion') -ne 1 -or (Get-P1Property $Report 'evidenceKind') -cne 'p1-requirement-coverage' -or
            (Get-P1Property $Report 'profile') -cne 'p1-coverage-v1' -or (Get-P1Property $Report 'status') -cne 'passed' -or
            (Get-P1Property $Report 'candidateSha') -cne $CandidateSha) { $errors.Add('Coverage identity/status/candidate is invalid.') }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $Root 'tools/RustSharp.Conformance/fixtures/p1-coverage-v1-manifest.json'))
        $manifest = ConvertFrom-P1StrictJson $bytes
        if ((Get-P1Property $Report 'manifestSha256') -ine [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -or
            (Get-P1Property $Report 'manifestVersion') -ne 1 -or (Get-P1Property $Report 'ledger') -cne 'p1-exit-scope-v1') { $errors.Add('Coverage frozen manifest/ledger binding is stale.') }
        Test-P1FixedSummary $Report 40 $errors
        if ((Get-P1Property (Get-P1Property $Report 'summary') 'caseDenominator') -ne 160) { $errors.Add('Coverage case denominator must be 160.') }
        $requirements = @(Get-P1Property $Report 'requirements'); $cases = @(Get-P1Property $Report 'cases')
        if ($requirements.Count -ne 40 -or $cases.Count -ne 160) { $errors.Add('Coverage requires exactly 40 requirements and 160 inventory cases.'); return @($errors.ToArray()) }
        for ($index = 0; $index -lt 40 -and $clock.Elapsed.TotalSeconds -lt 15; $index++) {
            foreach ($field in @('id', 'classification', 'leaf')) {
                if ((Get-P1Property $requirements[$index] $field) -cne $manifest.requirements[$index].$field) { $errors.Add("Coverage requirement $index $field is stale, duplicated or missing.") }
            }
        }
        for ($index = 0; $index -lt 160 -and $clock.Elapsed.TotalSeconds -lt 15; $index++) {
            $expected = $manifest.cases[$index]; $actual = $cases[$index]
            foreach ($field in @('id','requirementId','category','source','sourceSha256','expectation')) {
                if ((Get-P1Property $actual $field) -cne $expected.$field) { $errors.Add("Coverage case $index $field is stale, duplicated or missing.") }
            }
            if ((@(Get-P1Property $actual 'backends') -join ',') -cne (@($expected.backends) -join ',')) { $errors.Add("Coverage case $index backend contract changed.") }
            $full = [IO.Path]::GetFullPath($expected.source, $Root)
            if (-not [IO.File]::Exists($full) -or ([IO.FileInfo]::new($full)).Length -gt 1MB -or
                (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ine $expected.sourceSha256) { $errors.Add("Coverage case $index frozen source hash is stale.") }
        }
        if ($index -ne 160 -or $clock.Elapsed.TotalSeconds -ge 15) { $errors.Add('Coverage validation exceeded item/time bounds.') }
    }
    catch { $errors.Add('Malformed coverage evidence: ' + $_.Exception.Message) }
    return @($errors.ToArray())
}

function Test-P1RegistrationInventory($Inventory, [string] $AssemblyHash, [string] $Rid = '') {
    $errors = [Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $schema = Get-P1Property $Inventory 'schemaVersion'
    $maximumRegistrations = if ($schema -eq 2) { 4096 } else { 1024 }
    if ($null -eq $Inventory -or $schema -notin @(1,2) -or
        (Get-P1Property $Inventory 'evidenceKind') -cne 'p1-regression-registration-inventory' -or
        (Get-P1Property $Inventory 'buildConfiguration') -cne 'Release' -or
        $AssemblyHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-P1Property $Inventory 'assemblySha256') -ine $AssemblyHash) {
        $errors.Add('Fresh Release registration inventory or tests assembly binding is missing or invalid.')
        return @($errors.ToArray())
    }
    $observedRid = [string](Get-P1Property $Inventory 'runtimeIdentifier')
    if ($Rid -and (($Rid -ceq 'win-x64' -and $observedRid -cne 'win-x64') -or
        ($Rid -ceq 'linux-x64' -and $observedRid -cnotmatch '^(linux|ubuntu(?:\.\d+\.\d+)?)-x64$'))) { $errors.Add('Registration inventory native host does not match the required platform.') }
    $ids = @(Get-P1Property $Inventory 'registeredIds')
    if ($ids.Count -lt 464 -or $ids.Count -gt $maximumRegistrations -or (Get-P1Property $Inventory 'registeredDenominator') -ne $ids.Count) {
        $errors.Add('Fresh Release registration inventory denominator is missing or invalid.')
        return @($errors.ToArray())
    }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ids -join "`n")))
    if ((Get-P1Property $Inventory 'registeredIdsSha256') -ine $hash) { $errors.Add('Fresh Release registration inventory identity hash is stale.') }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index=0; $index -lt $ids.Count -and $index -lt $maximumRegistrations -and $clock.Elapsed.TotalSeconds -lt 5; $index++) {
        if ($ids[$index] -isnot [string] -or [string]::IsNullOrWhiteSpace($ids[$index]) -or $ids[$index].Length -gt 4096 -or -not $seen.Add($ids[$index])) { $errors.Add('Fresh Release registration inventory contains invalid or duplicate IDs.') }
    }
    if ($index -ne $ids.Count) { $errors.Add('Registration inventory validation exceeded item/time bounds.') }
    return @($errors.ToArray())
}

function Test-P1HarnessEvidence($Report, [string] $CandidateSha, [string] $TreeSha, [string] $Rid = '', $BuildReport = $null) {
    $errors = [Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $schema = Get-P1Property $Report 'schemaVersion'
    $maximumRegistrations = if ($schema -eq 2) { 4096 } else { 1024 }
    if ($schema -notin @(1,2) -or (Get-P1Property $Report 'evidenceKind') -cne 'p1-full-regression-harness' -or
        (Get-P1Property $Report 'candidateSha') -cne $CandidateSha -or (Get-P1Property $Report 'fullSuite') -cne $true -or
        (Get-P1Property $Report 'suiteSucceeded') -cne $true -or (Get-P1Property $Report 'buildConfiguration') -cne 'Release' -or
        (Get-P1Property $Report 'processIsolated') -cne $true -or (Get-P1Property $Report 'cleanupComplete') -cne $true -or
        (Get-P1Property $Report 'deadlineExpired') -cne $false -or (Get-P1Property $Report 'cancelled') -cne $false -or
        -not [string]::IsNullOrEmpty([string](Get-P1Property $Report 'harnessError'))) { $errors.Add('Full Release harness identity, result, deadline or candidate is invalid.') }
    $observedRid=[string](Get-P1Property $Report 'runtimeIdentifier')
    if ($Rid -and (($Rid -ceq 'win-x64' -and $observedRid -cne 'win-x64') -or ($Rid -ceq 'linux-x64' -and $observedRid -cnotmatch '^(linux|ubuntu(?:\.\d+\.\d+)?)-x64$'))) { $errors.Add('Full harness native host does not match the required platform.') }
    Test-P1SourceProvenance $Report $CandidateSha $TreeSha $errors
    $ids = @(Get-P1Property $Report 'registeredIds'); $cases = @(Get-P1Property $Report 'cases')
    $count = $ids.Count
    if ($schema -eq 2 -and (Get-P1Property (Get-P1Property $Report 'bounds') 'maximumTests') -ne 4096) { $errors.Add('Version 2 harness must declare its 4096 registration bound.') }
    if ($count -lt 464 -or $count -gt $maximumRegistrations -or $cases.Count -ne $count -or
        (Get-P1Property (Get-P1Property $Report 'summary') 'registeredDenominator') -ne $count) { $errors.Add('Full harness fixed registered denominator is missing or incomplete.'); return @($errors.ToArray()) }
    $idsHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ids -join "`n")))
    if ((Get-P1Property $Report 'registeredIdsSha256') -ine $idsHash) { $errors.Add('Full harness registered identity hash is stale.') }
    $inventory = Get-P1Property $BuildReport 'registrationInventory'
    if ((Get-P1Property $inventory 'schemaVersion') -ne $schema) { $errors.Add('Harness and fresh registration inventory schema versions differ.') }
    $assemblyHash = [string](Get-P1Property $BuildReport 'testsAssemblySha256')
    foreach ($inventoryError in @(Test-P1RegistrationInventory $inventory $assemblyHash $Rid)) { $errors.Add($inventoryError) }
    $expectedIds = @(Get-P1Property $inventory 'registeredIds')
    if ((Get-P1Property $Report 'assemblySha256') -ine $assemblyHash -or $assemblyHash -notmatch '^[a-fA-F0-9]{64}$') { $errors.Add('Full harness assembly differs from the fresh Release tests assembly.') }
    if ($count -ne $expectedIds.Count -or (Get-P1Property $inventory 'registeredIdsSha256') -ine $idsHash -or
        ($ids -join "`n") -cne ($expectedIds -join "`n")) { $errors.Add('Full harness does not execute the complete fresh Release registration inventory.') }
    $summary = Get-P1Property $Report 'summary'
    foreach ($name in @('selected','executed','passed')) { if ((Get-P1Property $summary $name) -ne $count) { $errors.Add("Full harness summary.$name does not close all registered tests.") } }
    foreach ($name in @('failed','skipped','notExecuted')) { if ((Get-P1Property $summary $name) -cne 0) { $errors.Add("Full harness summary.$name must be zero.") } }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $count -and $clock.Elapsed.TotalSeconds -lt 15; $index++) {
        $id = [string]$ids[$index]; $case = $cases[$index]
        if ([string]::IsNullOrWhiteSpace($id) -or -not $seen.Add($id) -or (Get-P1Property $case 'id') -cne $id -or
            (Get-P1Property $case 'status') -cne 'passed' -or -not [string]::IsNullOrEmpty([string](Get-P1Property $case 'error'))) { $errors.Add("Full harness case '$id' is missing, duplicated, reordered or unsuccessful.") }
        $process=Get-P1Property $case 'process'; $start=Get-P1Property $process 'startedProcess'
        $termination=Get-P1Property $process 'termination'
        if ($null -eq $process -or (Get-P1Property $process 'exitCode') -cne 0 -or
            ($termination -cne 0 -and $termination -cne 'Exited') -or (Get-P1Property $process 'succeeded') -cne $true -or
            (Get-P1Property $start 'processId') -le 0 -or (Get-P1Property $start 'parentProcessId') -le 0 -or
            [string]::IsNullOrWhiteSpace([string](Get-P1Property $start 'startedAt')) -or
            [string]::IsNullOrWhiteSpace([string](Get-P1Property $start 'commandLine'))) { $errors.Add("Full harness case '$id' worker process identity/result is incomplete.") }
        foreach ($flag in @('outputTruncated','outputReadTimedOut','outputDrainTimedOut','outputReadLimitReached','processTreeCleanupIncomplete')) { if ((Get-P1Property $process $flag) -cne $false) { $errors.Add("Full harness case '$id' $flag must be false.") } }
    }
    if ($index -ne $count) { $errors.Add('Harness validation exceeded item/time bounds.') }
    return @($errors.ToArray())
}

function Test-P1BaselineEvidence($Report, [string] $CandidateSha, [string] $TreeSha) {
    $errors = [Collections.Generic.List[string]]::new()
    if ((Get-P1Property $Report 'schemaVersion') -ne 1 -or (Get-P1Property $Report 'evidenceKind') -cne 'p1-immutable-regression-audit' -or
        (Get-P1Property $Report 'candidateSha') -cne $CandidateSha -or (Get-P1Property $Report 'succeeded') -cne $true -or
        (Get-P1Property $Report 'baselineCommit') -cne '23279d93267a814c643baddc29c72918ff0fda0b') { $errors.Add('Immutable baseline audit identity/result/candidate is invalid.') }
    Test-P1SourceProvenance $Report $CandidateSha $TreeSha $errors
    $summary = Get-P1Property $Report 'summary'
    foreach ($name in @('denominator','passed')) { if ((Get-P1Property $summary $name) -ne 60) { $errors.Add("Immutable audit summary.$name must be 60.") } }
    if ((Get-P1Property $summary 'failed') -cne 0 -or (Get-P1Property $summary 'suites') -ne 4) { $errors.Add('Immutable audit must preserve four suites without failures.') }
    $expected = [ordered]@{'safe-core-regression-v1'=8; 'safe-core-regression-v2'=24; 'p1-differential-v2'=16; 'p1-platform-v1'=12}
    $suites = @(Get-P1Property $Report 'suites')
    if ($suites.Count -ne 4) { $errors.Add('Immutable audit suite denominator mismatch.'); return @($errors.ToArray()) }
    $seen = @{}
    foreach ($suite in $suites) {
        $profile = [string](Get-P1Property $suite 'profile')
        if (-not $expected.Contains($profile) -or $seen.ContainsKey($profile)) { $errors.Add('Immutable audit has unexpected/duplicate suite.'); continue }
        $seen[$profile] = $true
        foreach ($name in @('manifestUnchanged','caseIdsUnchanged','expectationsUnchanged','sourcesUnchanged')) { if ((Get-P1Property $suite $name) -cne $true) { $errors.Add("Immutable '$profile' $name is false or missing.") } }
        $cases = @(Get-P1Property $suite 'cases')
        if ((Get-P1Property $suite 'denominator') -ne $expected[$profile] -or $cases.Count -ne $expected[$profile]) { $errors.Add("Immutable '$profile' case denominator changed."); continue }
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($case in $cases) { if (-not $ids.Add([string](Get-P1Property $case 'id')) -or (Get-P1Property $case 'status') -cne 'passed') { $errors.Add("Immutable '$profile' case is duplicated or unsuccessful.") } }
    }
    return @($errors.ToArray())
}

function Test-P1BuildEvidence($Report, [string] $CandidateSha, [string] $Rid = '', [string] $Root = '') {
    $errors = [Collections.Generic.List[string]]::new()
    if ((Get-P1Property $Report 'schemaVersion') -ne 1 -or (Get-P1Property $Report 'evidenceKind') -cne 'p1-release-build' -or
        (Get-P1Property $Report 'candidateSha') -cne $CandidateSha -or (Get-P1Property $Report 'configuration') -cne 'Release' -or
        (Get-P1Property $Report 'succeeded') -cne $true) { $errors.Add('Release build identity/result/candidate is invalid.') }
    $summary = Get-P1Property $Report 'summary'
    foreach ($name in @('warnings','errors')) { if ((Get-P1Property $summary $name) -cne 0) { $errors.Add("Release build $name must be zero.") } }
    foreach ($inventoryError in @(Test-P1RegistrationInventory (Get-P1Property $Report 'registrationInventory') ([string](Get-P1Property $Report 'testsAssemblySha256')) $Rid)) { $errors.Add($inventoryError) }
    $inventoryProcess = Get-P1Property $Report 'registrationInventoryProcess'
    $inventoryArguments = @(Get-P1Property $inventoryProcess 'Arguments')
    if ((Get-P1Property $Report 'registrationInventorySha256') -notmatch '^[a-fA-F0-9]{64}$' -or
        [string]::IsNullOrWhiteSpace([string](Get-P1Property $Report 'registrationInventoryPath')) -or
        (Get-P1Property $Report 'testsAssemblyPath') -cne 'tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll' -or
        (Get-P1Property $inventoryProcess 'ExitCode') -cne 0 -or (Get-P1Property $inventoryProcess 'CleanupComplete') -cne $true -or
        (Get-P1Property $inventoryProcess 'Pid') -le 0 -or (Get-P1Property $inventoryProcess 'ParentPid') -le 0 -or
        [string]::IsNullOrWhiteSpace([string](Get-P1Property $inventoryProcess 'StartedAt')) -or
        [string]::IsNullOrWhiteSpace([string](Get-P1Property $inventoryProcess 'FilePath')) -or
        $inventoryArguments.Count -ne 2 -or $inventoryArguments[1] -cne '--list' -or
        -not ([string]$inventoryArguments[0]).Replace('\','/').EndsWith('/tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll', [StringComparison]::Ordinal)) { $errors.Add('Fresh Release registration inventory process/artifact binding is incomplete.') }
    if ($Root) {
        foreach ($bindingError in @(Test-P1ReleaseArtifactBindings $Report $CandidateSha $Root)) { $errors.Add($bindingError) }
        try {
            $inventoryPath = [IO.Path]::GetFullPath([string](Get-P1Property $Report 'registrationInventoryPath'), $Root)
            $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
            if (-not $inventoryPath.StartsWith([IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar, $comparison) -or
                -not [IO.File]::Exists($inventoryPath) -or ([IO.FileInfo]::new($inventoryPath)).Length -gt 1MB) { throw 'Fresh inventory raw evidence is missing, oversized or outside the repository.' }
            $inventoryBytes = [IO.File]::ReadAllBytes($inventoryPath)
            if ($inventoryBytes.Length -gt 1MB -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inventoryBytes)) -ine (Get-P1Property $Report 'registrationInventorySha256')) { throw 'Fresh inventory raw evidence SHA-256 binding is stale.' }
            $rawInventory = ConvertFrom-P1StrictJson $inventoryBytes
            $embedded = Get-P1Property $Report 'registrationInventory'
            foreach ($field in @('schemaVersion','evidenceKind','runtimeIdentifier','buildConfiguration','assemblySha256','registeredDenominator','registeredIdsSha256')) {
                if ((Get-P1Property $rawInventory $field) -cne (Get-P1Property $embedded $field)) { throw 'Fresh inventory raw evidence differs from the embedded inventory.' }
            }
            if ((@(Get-P1Property $rawInventory 'registeredIds') -join "`n") -cne (@(Get-P1Property $embedded 'registeredIds') -join "`n")) { throw 'Fresh inventory raw registration IDs differ from the embedded inventory.' }
        }
        catch { $errors.Add('Fresh Release registration raw evidence failed: '+$_.Exception.Message) }
    }
    $steps = @(Get-P1Property $Report 'steps')
    if ($steps.Count -ne 2 -or @($steps | Where-Object { (Get-P1Property $_ 'name') -ceq 'restore' }).Count -ne 1 -or
        @($steps | Where-Object { (Get-P1Property $_ 'name') -ceq 'build' }).Count -ne 1) { $errors.Add('Release build requires exactly restore and build process evidence.') }
    else { foreach ($step in $steps) { if ((Get-P1Property $step 'succeeded') -cne $true -or
        $null -eq (Get-P1Property $step 'process') -or
        [string]::IsNullOrWhiteSpace([string](Get-P1Property $step 'stdoutPath')) -or
        [string]::IsNullOrWhiteSpace([string](Get-P1Property $step 'stderrPath'))) { $errors.Add('Release build process/log evidence is missing or unsuccessful.') }
        $process = Get-P1Property $step 'process'
        if ((Get-P1Property $process 'ExitCode') -cne 0 -or (Get-P1Property $process 'CleanupComplete') -cne $true -or
            (Get-P1Property $process 'Pid') -le 0 -or (Get-P1Property $process 'ParentPid') -le 0 -or
            [string]::IsNullOrWhiteSpace([string](Get-P1Property $process 'StartedAt')) -or
            [string]::IsNullOrWhiteSpace([string](Get-P1Property $process 'FilePath'))) { $errors.Add('Release build bounded process envelope is incomplete.') }
    } }
    return @($errors.ToArray())
}

function Test-P1ReleaseArtifactBindings($Report, [string] $CandidateSha, [string] $Root) {
    $errors = [Collections.Generic.List[string]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    function Read-BoundReleaseArtifact([string] $RelativePath, [string] $Hash, [long] $MaximumBytes) {
        if ($clock.Elapsed.TotalSeconds -ge 110 -or [string]::IsNullOrWhiteSpace($RelativePath) -or
            [IO.Path]::IsPathRooted($RelativePath) -or $Hash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Release artifact identity/time bound is invalid.' }
        $full = [IO.Path]::GetFullPath($RelativePath,$resolvedRoot)
        if (-not $full.StartsWith($resolvedRoot+[IO.Path]::DirectorySeparatorChar,$comparison) -or
            [IO.Path]::GetRelativePath($resolvedRoot,$full).Replace('\','/') -cne $RelativePath -or
            -not [IO.File]::Exists($full) -or ([IO.FileInfo]::new($full)).Length -gt $MaximumBytes) { throw 'Release artifact is missing, oversized or outside its canonical path.' }
        $component = $full
        for ($depth=0; $depth -lt 64 -and -not $component.Equals($resolvedRoot,$comparison); $depth++) {
            if ($clock.Elapsed.TotalSeconds -ge 110 -or (([IO.File]::GetAttributes($component)) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release artifact cannot traverse filesystem links.' }
            $component = [IO.Path]::GetDirectoryName($component)
        }
        if (-not $component.Equals($resolvedRoot,$comparison)) { throw 'Release artifact path depth bound exceeded.' }
        $bytes = [IO.File]::ReadAllBytes($full)
        if ($bytes.Length -gt $MaximumBytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $Hash) { throw 'Release artifact physical SHA-256 is stale.' }
        return ,$bytes
    }
    try {
        foreach ($kind in @('compiler','testsAssembly')) {
            $expectedPath = if ($kind -ceq 'compiler') { 'src/RustSharp.Cli/bin/Release/net10.0/rsc.dll' } else { 'tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll' }
            if ((Get-P1Property $Report ($kind+'Path')) -cne $expectedPath) { throw 'Release executable path is invalid.' }
            $artifactPath = [string](Get-P1Property $Report ($kind+'ArtifactPath'))
            if (-not $artifactPath.EndsWith('/build-artifacts/'+[IO.Path]::GetFileName($expectedPath),[StringComparison]::Ordinal)) { throw 'Release retained executable path is invalid.' }
            $null = Read-BoundReleaseArtifact $artifactPath ([string](Get-P1Property $Report ($kind+'Sha256'))) 32MB
        }
        $implementations = @(Get-P1Property $Report 'implementationAssemblies')
        $names = @('RustSharp.Compiler','RustSharp.Conformance','RustSharp.Runtime','RustSharp.Semantics','RustSharp.CodeGen.IL','RustSharp.Syntax')
        if ($implementations.Count -ne 6) { throw 'Release implementation assembly denominator must be six.' }
        for ($index=0; $index -lt 6; $index++) {
            $path = 'tests/RustSharp.Tests/bin/Release/net10.0/'+$names[$index]+'.dll'
            if ((Get-P1Property $implementations[$index] 'path') -cne $path) { throw 'Release implementation assembly inventory differs.' }
            $artifactPath = [string](Get-P1Property $implementations[$index] 'artifactPath')
            if (-not $artifactPath.EndsWith('/build-artifacts/'+$names[$index]+'.dll',[StringComparison]::Ordinal)) { throw 'Release retained implementation path is invalid.' }
            $null = Read-BoundReleaseArtifact $artifactPath ([string](Get-P1Property $implementations[$index] 'sha256')) 32MB
        }
        $snapshots = @(Get-P1Property $Report 'sourceSnapshots')
        if ($snapshots.Count -ne 2) { throw 'Release build must retain exactly two source snapshots.' }
        $beforeFiles = $null
        $tree = [string](Get-P1Property (Get-P1Property $Report 'sourceProvenance') 'candidateTreeSha')
        Test-P1SourceProvenance $Report $CandidateSha $tree $errors
        for ($index=0; $index -lt 2; $index++) {
            $path = [string](Get-P1Property $snapshots[$index] 'path')
            $expectedName = if ($index -eq 0) { 'build-source-before.json' } else { 'build-source-after.json' }
            if ([IO.Path]::GetFileName($path) -cne $expectedName) { throw 'Release source snapshot order/name differs.' }
            $snapshot = ConvertFrom-P1StrictJson (Read-BoundReleaseArtifact $path ([string](Get-P1Property $snapshots[$index] 'sha256')) 4MB)
            $snapshotErrors = @(Test-P1SnapshotEvidence $snapshot $resolvedRoot $CandidateSha)
            if ($snapshotErrors.Count -gt 0) { throw ('Release source snapshot failed actual candidate verification: '+($snapshotErrors -join '; ')) }
            if ((Get-P1Property $snapshot 'treeSha') -cne $tree) { throw 'Release source snapshot tree differs from the build.' }
            $files = Get-P1Property $snapshot 'files' | ConvertTo-Json -Depth 6 -Compress
            if ($index -eq 0) { $beforeFiles=$files } elseif ($files -cne $beforeFiles) { throw 'Release source inputs changed across build.' }
            $processes = @(Get-P1Property $snapshot 'processes')
            if ($processes.Count -lt 1 -or $processes.Count -gt 12) { throw 'Release snapshot process denominator is invalid.' }
            if ($index -eq 0) {
                $finished = [DateTimeOffset](Get-P1Property $processes[-1] 'finishedAtUtc')
                $sdkStarted = [DateTimeOffset](Get-P1Property (Get-P1Property $Report 'sdkProbe') 'StartedAt')
                if ($finished -gt $sdkStarted) { throw 'Release first source snapshot must precede the build SDK probe.' }
            } else {
                $started = [DateTimeOffset](Get-P1Property $processes[0] 'startedAtUtc')
                $inventoryFinished = [DateTimeOffset](Get-P1Property (Get-P1Property $Report 'registrationInventoryProcess') 'FinishedAt')
                if ($started -lt $inventoryFinished) { throw 'Release final source snapshot must follow fresh registration.' }
            }
        }
        $steps = @(Get-P1Property $Report 'steps')
        if ($steps.Count -ne 2) { throw 'Release command denominator differs.' }
        foreach ($step in $steps) {
            $name = [string](Get-P1Property $step 'name')
            $process = Get-P1Property $step 'process'
            $arguments = @(Get-P1Property $process 'Arguments')
            if ($name -cnotin @('restore','build') -or $arguments.Count -lt 4 -or $arguments[1] -cne $name -or
                $arguments[2] -cne 'RustSharp.slnx' -or [IO.Path]::GetFileName(([string](Get-P1Property $process 'FilePath')).Replace('\','/')) -cnotin @('dotnet','dotnet.exe') -or
                -not ([string]$arguments[0]).Replace('\','/').EndsWith('/sdk/'+[string](Get-P1Property $Report 'sdkVersion')+'/dotnet.dll',[StringComparison]::Ordinal) -or
                [string]::IsNullOrWhiteSpace([string](Get-P1Property $process 'WorkingDirectory')) -or
                '--disable-build-servers' -cnotin $arguments -or ($name -ceq 'build' -and ('Release' -cnotin $arguments -or '--no-restore' -cnotin $arguments))) { throw 'Release process command does not bind the selected SDK and solution.' }
        }
    } catch { $errors.Add('Release physical/source bindings failed: '+$_.Exception.Message) }
    return @($errors.ToArray())
}
