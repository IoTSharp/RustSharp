# Shared, bounded checks for expanded execution evidence and P1 closure.
Set-StrictMode -Version 3.0

function Get-P1Property($Object, [string] $Name) {
    if ($Object -is [Collections.IDictionary]) { if ($Object.Contains($Name)) { return $Object[$Name] }; return $null }
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { return $null }
    return $Object.PSObject.Properties[$Name].Value
}

function Get-P1NormalizedOutput($Process) {
    return ([string](Get-P1Property $Process 'standardOutput')).Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-P1ExpectedPlatformOutput([string] $Id) {
    $outputs=@{
        'borrow-shared'="7`n"; 'borrow-mutable-reborrow'="9`n"; 'drop-return-order'="body`ndrop`n"; 'drop-early-return'="drop`n"
        'borrow-write-read'="9`n9`n"; 'borrow-shared-after-update'="1`n9`n"; 'borrow-mutable-reborrow-chain'="9`n9`n9`n"; 'borrow-shared-reborrow'="7`n9`n"
        'drop-reverse-locals'="body`nsecond`nfirst`n"; 'drop-nested-scopes'="nested`ninner`nafter`nouter`n"; 'drop-return-reverse'="return`nsecond`nfirst`ncaller`n"; 'drop-branch-return'="early`nsecond`nfirst`nlate`nfirst`n"
        'slice-unsize'="3`n5`n"; 'pattern-capture'="7`n"; 'generic-import-call'="42`ntrue`n9`n"; 'byref-import-call'="7`n"
        'metadata-contract'="42`n"; 'mir-projection'="7`n"; 'mir-family'="7`n"; 'source-package'="42`n"
        'aggregate-struct-drop'="body`naggregate`nfirst`nsecond`n"; 'aggregate-enum-drop'="one`n"; 'panic-unwind-generated'="body`ninner-drop`nouter-drop`n"; 'panic-abort-generated'="drop`nbody`ndrop`n"
    }
    return $outputs[$Id]
}

function Test-P1DifferentialPlatformEvidence($Platform, $Versions, [string] $Rid, [Collections.Generic.List[string]] $Errors) {
    $observedRid=[string](Get-P1Property $Platform 'runtimeIdentifier')
    $name=if ($Rid -ceq 'win-x64') { 'windows-x64' } else { 'linux-x64' }
    $operatingSystem=[string](Get-P1Property $Platform 'operatingSystem')
    $ridMatches=if ($Rid -ceq 'win-x64') { $observedRid -ceq 'win-x64' } else { $Rid -ceq 'linux-x64' -and $observedRid -cmatch '^(linux|ubuntu(?:\.[0-9]+\.[0-9]+)?)-x64$' }
    $osMatches=$false
    if ($operatingSystem.Length -gt 0 -and $operatingSystem.Length -le 512) {
        if ($Rid -ceq 'win-x64') { $osMatches=$operatingSystem -cmatch '^Microsoft Windows (?:NT )?[0-9]+\.[0-9]+' }
        else {
            $osMatches=$operatingSystem -cmatch '^(?:Ubuntu [0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[ \t]+[A-Za-z0-9(). _-]+)?|Debian (?:GNU/Linux )?[0-9]+(?:[ \t]+[A-Za-z0-9(). _-]+)?|(?:Linux|Unix) [0-9]+\.[0-9]+[A-Za-z0-9.()+ _/-]*)$'
            if ($observedRid -cmatch '^ubuntu(?:\.([0-9]+\.[0-9]+))?-x64$') {
                $distributionVersion=$Matches[1]
                $osMatches=$osMatches -and $operatingSystem.StartsWith('Ubuntu ',[StringComparison]::Ordinal)
                if ($distributionVersion) { $osMatches=$osMatches -and $operatingSystem -cmatch ('^Ubuntu '+[regex]::Escape($distributionVersion)+'(?:\.| |$)') }
            }
        }
    }
    if (-not $ridMatches) { $Errors.Add('Native runtime identifier mismatch.') }
    if ((Get-P1Property $Platform 'nativeHost') -cne $true -or (Get-P1Property $Platform 'name') -cne $name -or
        -not $osMatches -or (Get-P1Property $Platform 'osArchitecture') -cne 'X64' -or
        (Get-P1Property $Platform 'processArchitecture') -cne 'X64') { $Errors.Add('Observed native x64 host provenance is missing or mismatched.') }
    foreach ($entry in @(@{field='sdkVersion';label='sdk'},@{field='dotnet';label='runtime'})) {
        $version=[string](Get-P1Property $Versions $entry.field)
        if ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][a-zA-Z0-9.]+)?$') { $Errors.Add("Observed $($entry.label) version is missing or invalid.") }
    }
    if ((Get-P1Property $Versions 'rustc') -cne (Get-P1Property $Platform 'oracle')) { $Errors.Add('Observed rustc tool/version provenance is inconsistent.') }
}

function Test-P1DifferentialCase($Case, [string] $Id, [Collections.Generic.List[string]] $Errors) {
    # These identities are frozen in expanded manifest revision 2. A failed
    # process proves a semantic negative only with its exact diagnostic.
    $negative = @{
        'borrow-fail-mut-alias' = @('E0499', 'RSO1002')
        'borrow-fail-shared-write' = @('E0506', 'RSO1002')
        'borrow-fail-moved-mut-ref' = @('E0382', 'RSO1001')
        'borrow-fail-escape' = @('E0597', 'RSO1005')
        'drop-partial-move' = @('E0509', 'RSO1008')
    }
    $runtimeNegative = @{
        'drop-unwind-nested' = "body`ninner-drop`nouter-drop`n"
        'drop-double-panic' = "body`ndrop-start`n"
    }
    $fixedOutput = @{
        'drop-aggregate-fields' = "body`naggregate`nfirst-field`nsecond-field`n"
        'drop-assignment-replacement' = "drop`nbody`ndrop`n"
        'drop-temporary-scope' = "drop`nafter`n"
    }
    $isCompileNegative = $negative.ContainsKey($Id)
    $expectedOutcome = if ($isCompileNegative) { 'compile-fail' } elseif ($runtimeNegative.ContainsKey($Id)) { 'run-fail' } else { 'run-pass' }
    if ((Get-P1Property $Case 'expectedOutcome') -cne $expectedOutcome -or
        -not [string]::IsNullOrEmpty([string](Get-P1Property $Case 'failureKind'))) { $Errors.Add("Case '$Id' semantic outcome or failure classification is invalid.") }
    $compilerNames = @('rustcCompile', 'rustSharpCheck', 'rustSharpCompile')
    for ($ordinal = 0; $ordinal -lt 3; $ordinal++) {
        $name = $compilerNames[$ordinal]
        $process = Get-P1Property $Case $name
        Test-P1ProcessEvidence $process "$Id $name" $Errors -Successful:(-not $isCompileNegative)
        if ($isCompileNegative) {
            $code = $negative[$Id][[int]($ordinal -gt 0)]
            $exitCode = Get-P1Property $process 'exitCode'
            $diagnostics = [string](Get-P1Property $process 'standardError') + [string](Get-P1Property $process 'standardOutput')
            if ($null -eq $exitCode -or $exitCode -eq 0 -or $diagnostics -notmatch ('\b' + $code + '\b') -or $diagnostics -match '\bRSC0009\b') { $Errors.Add("Case '$Id' expected semantic diagnostic $code is missing or replaced by unsupported lowering.") }
        }
    }
    if ($isCompileNegative) {
        if ($null -ne (Get-P1Property $Case 'rustcRun') -or $null -ne (Get-P1Property $Case 'rustSharpRun')) { $Errors.Add("Case '$Id' compile-fail evidence contains unexpected execution.") }
        return
    }
    foreach ($name in @('rustcRun', 'rustSharpRun')) {
        $process = Get-P1Property $Case $name
        Test-P1ProcessEvidence $process "$Id $name" $Errors -Successful:($expectedOutcome -ceq 'run-pass')
        if ($expectedOutcome -ceq 'run-fail' -and (Get-P1Property $process 'exitCode') -eq 0) { $Errors.Add("Case '$Id' expected runtime failure executed successfully.") }
    }
    $oracleOutput = Get-P1NormalizedOutput (Get-P1Property $Case 'rustcRun')
    $managedOutput = Get-P1NormalizedOutput (Get-P1Property $Case 'rustSharpRun')
    if ($oracleOutput -cne $managedOutput -or
        ($runtimeNegative.ContainsKey($Id) -and $oracleOutput -cne $runtimeNegative[$Id]) -or
        ($fixedOutput.ContainsKey($Id) -and $oracleOutput -cne $fixedOutput[$Id])) { $Errors.Add("Case '$Id' exact differential output does not match.") }
}

function Test-P1LabelPlaceholder([string] $Source, [string] $Id) {
    $normalized = $Source.Replace("`r`n", "`n").Trim()
    $quote = [char]34
    $expected = '// frozen P1 fixture: ' + $Id + "`nfn main() { println!(" + $quote + $Id + $quote + "); }"
    return $normalized -ceq $expected
}

function Test-P1ProcessEvidence($Process, [string] $Label, [Collections.Generic.List[string]] $Errors, [switch] $Successful) {
    if ($null -eq $Process) { $Errors.Add("$Label process evidence is missing."); return }
    foreach ($name in @('commandLine', 'startedAtUtc')) {
        if ([string]::IsNullOrWhiteSpace([string](Get-P1Property $Process $name))) { $Errors.Add("$Label.$name is missing.") }
    }
    foreach ($name in @('processId', 'parentProcessId')) {
        if ((Get-P1Property $Process $name) -isnot [long] -and (Get-P1Property $Process $name) -isnot [int]) { $Errors.Add("$Label.$name must be a positive integer.") }
        elseif ((Get-P1Property $Process $name) -le 0) { $Errors.Add("$Label.$name must be positive.") }
    }
    if ((Get-P1Property $Process 'cleanupIncomplete') -cne $false) { $Errors.Add("$Label process cleanup is incomplete or missing.") }
    # A process can exit successfully while its captured output is truncated or
    # its reader/drain hit a timeout/limit. Such evidence cannot establish a
    # semantic result, so every bounded-output marker must be present and false.
    foreach ($name in @('outputTruncated', 'outputReadTimedOut', 'outputDrainTimedOut', 'outputReadLimitReached')) {
        $value = Get-P1Property $Process $name
        if ($value -isnot [bool]) { $Errors.Add("$Label.$name must be a boolean false marker.") }
        elseif ($value) { $Errors.Add("$Label output evidence is incomplete ($name is true).") }
    }
    if ((Get-P1Property $Process 'termination') -cne 'exited') { $Errors.Add("$Label did not exit normally.") }
    $exitCode = Get-P1Property $Process 'exitCode'
    if ($null -eq $exitCode -or ($Successful -and $exitCode -ne 0)) { $Errors.Add("$Label exit code is missing or unsuccessful.") }
}

function Test-P1ExpandedEvidence {
    param([object] $Report, [string] $Root, [string] $Profile, [string] $Rid, [string] $CandidateSha)
    $errors = [Collections.Generic.List[string]]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($null -eq $Report) { return @('blocked: execution report is missing.') }
        if ((Get-P1Property $Report 'schemaVersion') -ne 1) { $errors.Add('Expanded schema version is invalid.') }
        if ([string]::IsNullOrWhiteSpace($CandidateSha)) { $errors.Add('blocked: candidate SHA is required.') }
        elseif ($CandidateSha -notmatch '^[0-9a-fA-F]{40,64}$' -or (Get-P1Property $Report 'candidateSha') -cne $CandidateSha) { $errors.Add('Candidate SHA is invalid or mismatched.') }
        if ((Get-P1Property $Report 'profile') -cne $Profile) { $errors.Add('Expanded profile mismatch.') }
        if ((Get-P1Property $Report 'semanticClosureEligible') -cne $true) { $errors.Add('blocked: frozen source coverage is not eligible for semantic closure.') }
        $kind = if ($Profile -ceq 'p1-platform-v2') { 'p1-platform-coreclr-ilverify-native-aot' } else { 'p1-source-borrow-drop-differential' }
        if ((Get-P1Property $Report 'evidenceKind') -cne $kind) { $errors.Add('Expanded evidence kind mismatch.') }
        $platform = Get-P1Property $Report 'platform'
        if ($Profile -ceq 'p1-differential-v3') {
            # Differential and platform runners deliberately emit different
            # provenance envelopes. Validate the observed fields of each;
            # absent platform-runner aliases cannot replace these facts.
            Test-P1DifferentialPlatformEvidence $platform (Get-P1Property $Report 'toolVersions') $Rid $errors
        }
        else {
            if ((Get-P1Property $platform 'runtimeIdentifier') -cne $Rid) { $errors.Add('Native runtime identifier mismatch.') }
            $observedRid = [string](Get-P1Property $platform 'observedRuntimeIdentifier')
            $hostMatches = if ($Rid -ceq 'win-x64') { $observedRid -ceq 'win-x64' } else { $observedRid -cmatch '^(linux|ubuntu(?:\.\d+\.\d+)?)-x64$' }
            if ((Get-P1Property $platform 'nativeExecution') -cne $true -or -not $hostMatches -or (Get-P1Property $platform 'architecture') -cne 'X64') { $errors.Add('Observed native x64 host provenance is missing or mismatched.') }
            foreach ($name in @('sdk', 'runtime')) {
                if ([string]::IsNullOrWhiteSpace([string](Get-P1Property $platform $name)) -or (Get-P1Property $platform $name) -eq 'unavailable') { $errors.Add("Observed $name version is missing.") }
            }
        }
        if ((Get-P1Property $platform 'oracle') -notmatch '^rustc 1\.98\.0 \(') { $errors.Add('Observed rustc 1.98.0 version is missing.') }
        $compilerHash = [string](Get-P1Property (Get-P1Property $Report 'compiler') 'sha256')
        if ($compilerHash -notmatch '^[a-fA-F0-9]{64}$' -or $compilerHash -cne [string](Get-P1Property $Report 'compilerSha256')) { $errors.Add('Actual compiler SHA-256 provenance is missing or inconsistent.') }
        if ((Get-P1Property (Get-P1Property $Report 'cleanup') 'completed') -cne $true) { $errors.Add('Report cleanup did not complete.') }
        $execution = Get-P1Property $Report 'execution'
        foreach ($name in @('startedAtUtc', 'finishedAtUtc')) {
            if ([string]::IsNullOrWhiteSpace([string](Get-P1Property $execution $name))) { $errors.Add("execution.$name is missing.") }
        }
        if ((Get-P1Property $execution 'deadlineExpired') -cne $false) { $errors.Add('Execution deadline state is missing or expired.') }

        $manifestPath = Join-Path $Root 'tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v2-manifest.json'
        $info = [IO.FileInfo]::new($manifestPath)
        if (-not $info.Exists -or $info.Length -gt 256KB) { return @('blocked: expanded manifest is missing or oversized.') }
        $bytes = [IO.File]::ReadAllBytes($manifestPath)
        $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 32
        $suite = @($manifest.suites | Where-Object profile -CEQ $Profile)
        if ($suite.Count -ne 1) { return @('Expanded manifest suite is missing or duplicated.') }
        $suite = $suite[0]
        $count = [int]$suite.denominator
        if ($count -notin @(24, 32) -or @($suite.cases).Count -ne $count) { return @('Expanded manifest denominator is invalid.') }
        $stamp = Get-P1Property $Report 'manifest'
        if ((Get-P1Property $stamp 'sha256') -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -or (Get-P1Property $stamp 'validated') -cne $true -or (Get-P1Property $stamp 'version') -ne $manifest.version -or (Get-P1Property $stamp 'denominator') -ne $count) { $errors.Add('Expanded manifest provenance is stale or missing.') }
        $summary = Get-P1Property $Report 'summary'
        if ((Get-P1Property $summary 'status') -cne 'passed') { $errors.Add('Expanded execution summary did not pass.') }
        foreach ($name in @('denominator', 'executed', 'passed')) { if ((Get-P1Property $summary $name) -ne $count) { $errors.Add("summary.$name does not close the fixed denominator.") } }
        foreach ($name in @('failed', 'blocked', 'skipped')) { if ((Get-P1Property $summary $name) -cne 0) { $errors.Add("summary.$name must be zero.") } }
        $cases = @(Get-P1Property $Report 'cases')
        if ($cases.Count -ne $count) { return @($errors.ToArray()) + @('Expanded case denominator mismatch.') }
        $expected = @{}
        foreach ($fixture in $suite.cases) { $expected[$fixture.id] = $fixture }
        $seen = @{}
        for ($index = 0; $index -lt $count -and $watch.Elapsed.TotalSeconds -lt 15; $index++) {
            $case = $cases[$index]
            $id = [string](Get-P1Property $case 'id')
            if ($seen.ContainsKey($id) -or -not $expected.ContainsKey($id)) { $errors.Add("Unexpected or duplicate expanded case '$id'."); continue }
            $seen[$id] = $true
            if ((Get-P1Property $case 'status') -cne 'passed') { $errors.Add("Case '$id' did not pass.") }
            if ((Get-P1Property $case 'semanticClosureEligible') -cne $true) { $errors.Add("blocked: case '$id' lacks semantic coverage.") }
            foreach ($name in @('sourceSha256', 'expectationSha256')) { if ((Get-P1Property $case $name) -cne $expected[$id].$name) { $errors.Add("Case '$id' $name is stale or missing.") } }
            $sourcePath = Join-Path $Root ('tools/RustSharp.Conformance/fixtures/' + $expected[$id].source)
            $sourceInfo = [IO.FileInfo]::new($sourcePath)
            if (-not $sourceInfo.Exists -or $sourceInfo.Length -gt 256KB) { $errors.Add("Case '$id' source is missing or oversized.") }
            else {
                if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -cne $expected[$id].sourceSha256) { $errors.Add("Case '$id' source file hash differs from the frozen manifest.") }
                if (Test-P1LabelPlaceholder ([IO.File]::ReadAllText($sourcePath)) $id) { $errors.Add("blocked: case '$id' is a frozen label-only placeholder.") }
            }
            if ($Profile -ceq 'p1-platform-v2') {
                Test-P1ProcessEvidence (Get-P1Property $case 'coreClrCompile') "$id compile" $errors -Successful
                Test-P1ProcessEvidence (Get-P1Property $case 'coreClrRun') "$id CoreCLR" $errors -Successful
                if ((Get-P1Property (Get-P1Property $case 'coreClrRun') 'outputMatches') -cne $true) { $errors.Add("Case '$id' CoreCLR output was not verified.") }
                $verify = Get-P1Property $case 'ilVerify'
                if ((Get-P1Property $verify 'status') -cne 'passed' -or (Get-P1Property $verify 'succeeded') -cne $true) { $errors.Add("Case '$id' ILVerify evidence is incomplete.") }
                Test-P1ProcessEvidence (Get-P1Property $verify 'process') "$id ILVerify" $errors -Successful
                $aot = Get-P1Property $case 'nativeAot'
                if ((Get-P1Property $aot 'status') -cne 'passed' -or (Get-P1Property $aot 'succeeded') -cne $true -or (Get-P1Property $aot 'outputMatches') -cne $true -or (Get-P1Property $aot 'hostCleanupIncomplete') -cne $false) { $errors.Add("Case '$id' Native AOT evidence is incomplete.") }
                Test-P1ProcessEvidence (Get-P1Property $aot 'publish') "$id AOT publish" $errors -Successful
                Test-P1ProcessEvidence (Get-P1Property $aot 'run') "$id AOT run" $errors -Successful
                $output = Get-P1ExpectedPlatformOutput $id
                $hasReportedExpectation = ($case -is [Collections.IDictionary] -and $case.Contains('expectedOutput')) -or
                    ($case -isnot [Collections.IDictionary] -and $null -ne $case.PSObject.Properties['expectedOutput'])
                if ($null -eq $output -or ($hasReportedExpectation -and (Get-P1Property $case 'expectedOutput') -cne $output) -or
                    (Get-P1NormalizedOutput (Get-P1Property $case 'coreClrRun')) -cne $output -or
                    (Get-P1NormalizedOutput (Get-P1Property $aot 'run')) -cne $output) { $errors.Add("Case '$id' exact CoreCLR/Native AOT frozen output is missing or mismatched.") }
            }
            else {
                Test-P1DifferentialCase $case $id $errors
            }
        }
        if ($seen.Count -ne $count -or $watch.Elapsed.TotalSeconds -ge 15) { $errors.Add('Expanded evidence validation exceeded its item/time bound.') }
    }
    catch { $errors.Add('Malformed expanded evidence: ' + $_.Exception.Message) }
    return @($errors.ToArray())
}

function Test-P1ClosureRecord {
    param([object] $Record, [string] $CandidateSha, [string] $Root, [object[]] $Inputs)
    $errors = [Collections.Generic.List[string]]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ((Get-P1Property $Record 'schemaVersion') -ne 1 -or (Get-P1Property $Record 'candidateSha') -cne $CandidateSha -or (Get-P1Property $Record 'status') -cne 'complete') { $errors.Add('Closure schema, candidate SHA, or status is invalid.') }
    if ((Get-P1Property $Record 'gate') -cne 'P1-GATE.05') { $errors.Add('Closure must bind P1-GATE.05.') }
    if ((Get-P1Property (Get-P1Property $Record 'cleanup') 'completed') -cne $true -or (Get-P1Property $Record 'cleanDiff') -cne $true) { $errors.Add('Closure clean diff and resource cleanup evidence are required.') }
    $leaves = @(Get-P1Property $Record 'leaves')
    if ($leaves.Count -ne 85) { $errors.Add('Closure must contain all 85 implementation leaves.') }
    else {
        $roadmap = [IO.File]::ReadAllText((Join-Path $Root 'docs/roadmap/P1.md'))
        $chineseRoadmap = [IO.File]::ReadAllText((Join-Path $Root 'docs/roadmap/P1_zh.md'))
        $required = @([regex]::Matches($roadmap, '(?m)^\| (P1-\d{2}\.\d{2}) \|') | ForEach-Object { $_.Groups[1].Value })
        $seen = @{}
        for ($index = 0; $index -lt 85 -and $watch.Elapsed.TotalSeconds -lt 15; $index++) {
            $leaf = $leaves[$index]; $id = [string](Get-P1Property $leaf 'id')
            if ($id -cnotin $required -or $seen.ContainsKey($id) -or (Get-P1Property $leaf 'status') -cne 'complete' -or @((Get-P1Property $leaf 'evidence')).Count -eq 0) { $errors.Add("Closure leaf '$id' is missing, duplicated, unfinished, or lacks evidence.") }
            $leafEvidence = @(Get-P1Property $leaf 'evidence')
            if ($leafEvidence.Count -gt 16 -or @($leafEvidence | Where-Object { $_ -cnotin @($Inputs.Name) }).Count -gt 0) { $errors.Add("Closure leaf '$id' references unbound evidence.") }
            $seen[$id] = $true
            if ($roadmap -notmatch ('(?m)^\| ' + [regex]::Escape($id) + ' \| ✅ Complete \|')) { $errors.Add("blocked: roadmap leaf '$id' remains open.") }
            if ($chineseRoadmap -notmatch ('(?m)^\| ' + [regex]::Escape($id) + ' \| ✅ 已完成 \|')) { $errors.Add("blocked: Chinese roadmap leaf '$id' remains open.") }
        }
        if ($seen.Count -ne 85 -or $watch.Elapsed.TotalSeconds -ge 15) { $errors.Add('Closure leaf validation did not finish within its bounds.') }
    }
    $reports = @(Get-P1Property $Record 'reports')
    if ($reports.Count -ne $Inputs.Count -or $reports.Count -gt 16) { $errors.Add('Closure report bindings are incomplete.') }
    else {
        $seen = @{}
        foreach ($inputRecord in $Inputs) {
            $bound = @($reports | Where-Object { (Get-P1Property $_ 'name') -ceq $inputRecord.Name })
            if ($bound.Count -ne 1 -or $seen.ContainsKey($inputRecord.Name) -or (Get-P1Property $bound[0] 'sha256') -cne $inputRecord.Sha256) { $errors.Add("Closure report '$($inputRecord.Name)' hash is missing or stale.") }
            $seen[$inputRecord.Name] = $true
        }
    }
    $runs = @(Get-P1Property $Record 'nativeRuns')
    if ($runs.Count -ne 2) { $errors.Add('Closure requires two native CI run/artifact bindings.') }
    else { foreach ($rid in @('win-x64', 'linux-x64')) {
        $run = @($runs | Where-Object { (Get-P1Property $_ 'runtimeIdentifier') -ceq $rid })
        if ($run.Count -ne 1 -or (Get-P1Property $run[0] 'candidateSha') -cne $CandidateSha -or (Get-P1Property $run[0] 'runUrl') -notmatch '^https://github\.com/IoTSharp/RustSharp/actions/runs/[0-9]+$' -or [string]::IsNullOrWhiteSpace([string](Get-P1Property $run[0] 'artifact'))) { $errors.Add("Closure native '$rid' run/artifact binding is missing or invalid.") }
    } }
    $documents = @(Get-P1Property $Record 'documents')
    foreach ($path in @('docs/p1-completion.md', 'docs/p1-completion_zh.md')) {
        $bound = @($documents | Where-Object { (Get-P1Property $_ 'path') -ceq $path })
        $full = Join-Path $Root $path
        if ($bound.Count -ne 1 -or -not [IO.File]::Exists($full)) { $errors.Add("Closure document '$path' is missing."); continue }
        $info = [IO.FileInfo]::new($full)
        if ($info.Length -gt 1MB -or (Get-P1Property $bound[0] 'sha256') -cne (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash) { $errors.Add("Closure document '$path' hash is stale or oversized.") }
    }
    return @($errors.ToArray())
}

function Test-P1RequirementClosure([string] $Root, [string] $EnglishText = '', [string] $ChineseText = '') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $errors = [Collections.Generic.List[string]]::new()
    $path = Join-Path $Root 'tools/RustSharp.Conformance/fixtures/p1-gate-coverage-v1-manifest.json'
    if (([IO.FileInfo]::new($path)).Length -gt 262144) { return @('blocked: frozen gate mapping is oversized.') }
    $manifest = [IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 24
    $requirements = @($manifest.requirements)
    if ($manifest.profile -cne 'p1-gate-coverage-v1' -or $manifest.requirementDenominator -ne 40 -or $manifest.catalogueDenominator -ne 160 -or $requirements.Count -ne 40) { return @('Frozen gate mapping identity or denominator changed.') }
    $english = if ($EnglishText) { $EnglishText } else { [IO.File]::ReadAllText((Join-Path $Root 'docs/roadmap/P1.md')) }
    $chinese = if ($ChineseText) { $ChineseText } else { [IO.File]::ReadAllText((Join-Path $Root 'docs/roadmap/P1_zh.md')) }
    for ($index = 0; $index -lt 40 -and $watch.Elapsed.TotalSeconds -lt 15; $index++) {
        $requirement = $requirements[$index]
        $leaves = @($requirement.implementationLeaves)
        if ($leaves.Count -gt 32 -or ($requirement.role -cne 'aggregate-ownership' -and $leaves.Count -eq 0)) { $errors.Add('Missing or oversized actual implementation leaf mapping.'); continue }
        foreach ($leaf in $leaves) {
            if ($watch.Elapsed.TotalSeconds -ge 15) { break }
            $prefix = '(?m)^\| ' + [regex]::Escape([string]$leaf) + ' \| '
            if (-not [regex]::IsMatch($english,$prefix+'✅ Complete \|',[Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromSeconds(1)) -or
                -not [regex]::IsMatch($chinese,$prefix+'✅ 已完成 \|',[Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromSeconds(1))) { $errors.Add("blocked: requirement '$($requirement.id)' designated leaf '$leaf' remains open.") }
        }
    }
    if ($index -ne 40 -or $watch.Elapsed.TotalSeconds -ge 15) { $errors.Add('Requirement closure validation timed out.') }
    # Status reconciliation only; full harness and physical backend evidence remain independent obligations.
    return @($errors.ToArray())
}
