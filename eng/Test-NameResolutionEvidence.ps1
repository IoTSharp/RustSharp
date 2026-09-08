#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ReportPath = 'artifacts/conformance/safe-core-name-resolution.json',
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [System.Threading.CancellationToken] $CancellationToken = [System.Threading.CancellationToken]::None
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$evidenceClock = [System.Diagnostics.Stopwatch]::StartNew()

function Test-EvidenceBudget {
    $CancellationToken.ThrowIfCancellationRequested()
    if ($evidenceClock.Elapsed.TotalSeconds -ge 10) {
        throw 'Name-resolution evidence verification exceeded its ten-second deadline.'
    }
}

function Read-BoundedEvidenceText([string] $Path, [long] $MaximumBytes) {
    Test-EvidenceBudget
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or $item.Length -gt $MaximumBytes) { throw "Evidence file '$Path' exceeds its size limit." }
    $text = [IO.File]::ReadAllText($item.FullName)
    Test-EvidenceBudget
    return $text
}

function Assert-EvidenceInteger($Value, [long] $Expected, [string] $Name) {
    if ($Value -isnot [long] -or $Value -ne $Expected) { throw "Invalid name-resolution evidence integer '$Name'." }
}

function Assert-EvidenceSpan($Span, [int] $SourceLength) {
    if ($Span.start -isnot [long] -or $Span.length -isnot [long] -or
        $Span.start -lt 0 -or $Span.length -lt 0 -or
        $Span.start -gt $SourceLength - $Span.length) { throw 'Invalid name-resolution evidence source span.' }
}

try {
    $manifestRelativePath = 'tools/RustSharp.Conformance/fixtures/safe-core-name-resolution-manifest.json'
    $manifestPath = Join-Path $RepositoryRoot $manifestRelativePath
    $manifest = Read-BoundedEvidenceText $manifestPath 262144 | ConvertFrom-Json -AsHashtable -Depth 32
    $report = Read-BoundedEvidenceText $ReportPath 4194304 | ConvertFrom-Json -AsHashtable -Depth 32
    Test-EvidenceBudget
    $denominator = $manifest.denominator
    if ($denominator -isnot [long] -or $denominator -lt 1 -or $denominator -gt 256 -or
        $manifest.cases -isnot [array] -or $manifest.cases.Count -ne $denominator -or
        $report.cases -isnot [array] -or $report.cases.Count -ne $denominator) {
        throw 'Name-resolution case counts differ from the bounded current manifest.'
    }
    Assert-EvidenceInteger $manifest.version 1 'manifest.version'
    Assert-EvidenceInteger $report.schemaVersion 1 'schemaVersion'
    Assert-EvidenceInteger $report.manifest.version $manifest.version 'report.manifest.version'
    Assert-EvidenceInteger $report.manifest.denominator $denominator 'report.manifest.denominator'
    Assert-EvidenceInteger $report.manifest.caseCount $denominator 'report.manifest.caseCount'
    $parser = 'RustSharp.Syntax.SafeCoreSyntax.Parse'
    $resolver = 'RustSharp.Syntax.SafeCoreNameResolution.Resolve'
    if ($manifest.profile -cne 'safe-core-name-resolution' -or $report.profile -cne $manifest.profile -or
        $manifest.parser -cne $parser -or $manifest.resolver -cne $resolver -or
        $report.manifest.parser -cne $parser -or $report.manifest.resolver -cne $resolver -or
        $report.pipeline.parser -cne $parser -or $report.pipeline.resolver -cne $resolver -or
        $report.evidenceKind -cne 'name-resolution-acceptance' -or
        $report.scope.rustcConformance -isnot [bool] -or $report.scope.rustcConformance -or
        $report.scope.runtimeConformance -isnot [bool] -or $report.scope.runtimeConformance -or
        $report.scope.statement -cne 'This report measures only in-process RustSharp safe-core parser and name-resolution acceptance; it is not rustc differential or runtime conformance evidence.' -or
        $report.manifest.validated -isnot [bool] -or -not $report.manifest.validated -or
        $report.manifest.path -cne $manifestRelativePath -or
        $report.manifest.sha256 -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -or
        $null -ne $report.manifest.error -or $null -ne $report.harnessError -or
        $report.execution.deadlineExpired -isnot [bool] -or $report.execution.deadlineExpired -or
        $report.summary.status -cne 'passed') {
        throw 'Name-resolution evidence scope or manifest metadata is incomplete or stale.'
    }
    foreach ($field in @('denominator', 'executed', 'resolutionExecuted', 'passed')) {
        Test-EvidenceBudget
        Assert-EvidenceInteger $report.summary[$field] $denominator "summary.$field"
    }
    foreach ($field in @('failed', 'errors', 'skipped', 'exitCode')) {
        Test-EvidenceBudget
        Assert-EvidenceInteger $report.summary[$field] 0 "summary.$field"
    }

    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # Every loop is bounded by a fixed list or a checked array count, and shares
    # the deadline/cancellation check. There are at most 256 fixture cases.
    for ($index = 0; $index -lt $denominator; $index++) {
        Test-EvidenceBudget
        if ($index % 16 -eq 0) {
            Write-Progress -Id 7303 -Activity 'Verify name-resolution evidence' -Status "$index/$denominator cases" -PercentComplete (100 * $index / $denominator)
        }
        $expected = $manifest.cases[$index]
        $actual = $report.cases[$index]
        if ($expected.id -isnot [string] -or [string]::IsNullOrWhiteSpace($expected.id) -or
            -not $ids.Add($expected.id) -or $actual.id -cne $expected.id -or
            $expected.file -isnot [string] -or $expected.file.Length -gt 128 -or
            $expected.file -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\.rs\z' -or
            $actual.source -cne $expected.file -or
            $expected.expected -cnotin @('resolution-pass', 'resolution-fail') -or
            $actual.expected -cne $expected.expected -or $actual.actual -cne $expected.expected -or
            $actual.status -cne 'passed' -or $null -ne $actual.difference) {
            throw "Name-resolution case at index $index is incomplete or out of order."
        }
        foreach ($field in @('parserInvoked', 'resolverInvoked')) {
            Test-EvidenceBudget
            if ($actual[$field] -isnot [bool] -or -not $actual[$field]) { throw "Case '$($expected.id)' did not invoke $field." }
        }
        foreach ($field in @('syntaxDiagnosticsTruncated', 'resolutionDiagnosticsTruncated', 'resolutionsTruncated', 'resolutionWasTruncated')) {
            Test-EvidenceBudget
            if ($actual[$field] -isnot [bool] -or $actual[$field]) { throw "Case '$($expected.id)' contains truncated evidence." }
        }
        $sourcePath = Join-Path (Split-Path -Parent $manifestPath) $expected.file
        $source = Read-BoundedEvidenceText $sourcePath 262144
        if ($actual.sourceSha256 -cne (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) {
            throw "Name-resolution case '$($expected.id)' source hash is stale."
        }
        Assert-EvidenceInteger $actual.sourceLength $source.Length 'case.sourceLength'
        $minimumSymbols = if ($expected.ContainsKey('minimumSymbols')) { [long]$expected.minimumSymbols } else { 0L }
        $minimumScopes = if ($expected.ContainsKey('minimumScopes')) { [long]$expected.minimumScopes } else { 0L }
        if ($actual.symbolCount -isnot [long] -or $actual.symbolCount -lt $minimumSymbols -or
            $actual.scopeCount -isnot [long] -or $actual.scopeCount -lt $minimumScopes -or
            $actual.syntaxDiagnostics -isnot [array] -or $actual.syntaxDiagnostics.Count -ne 0 -or
            $actual.resolutionDiagnostics -isnot [array] -or $actual.resolutionDiagnostics.Count -gt 128 -or
            $actual.resolutions -isnot [array] -or $actual.resolutions.Count -gt 256 -or
            $expected.expectedDiagnostics -isnot [array] -or $expected.expectedDiagnostics.Count -gt 128 -or
            $expected.expectedResolutions -isnot [array] -or $expected.expectedResolutions.Count -gt 64) {
            throw "Name-resolution case '$($expected.id)' has invalid evidence counts."
        }
        Assert-EvidenceInteger $actual.syntaxDiagnosticCount 0 'case.syntaxDiagnosticCount'
        Assert-EvidenceInteger $actual.resolutionDiagnosticCount $actual.resolutionDiagnostics.Count 'case.resolutionDiagnosticCount'
        Assert-EvidenceInteger $actual.resolutionCount $actual.resolutions.Count 'case.resolutionCount'
        if (($expected.expected -ceq 'resolution-pass' -and $expected.expectedDiagnostics.Count -ne 0) -or
            ($expected.expected -ceq 'resolution-fail' -and $expected.expectedDiagnostics.Count -eq 0) -or
            ($expected.expectedResolutions.Count -eq 0 -and $expected.expected -cne 'resolution-fail')) {
            throw "Name-resolution case '$($expected.id)' lacks explicit acceptance expectations."
        }

        $diagnosticsByCode = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([StringComparer]::Ordinal)
        foreach ($diagnostic in $actual.resolutionDiagnostics) {
            Test-EvidenceBudget
            Assert-EvidenceSpan $diagnostic $source.Length
            if ($diagnostic.code -isnot [string] -or [string]::IsNullOrWhiteSpace($diagnostic.code) -or
                $diagnostic.messageTruncated -isnot [bool] -or $diagnostic.messageTruncated) { throw 'Invalid diagnostic evidence.' }
            if (-not $diagnosticsByCode.ContainsKey($diagnostic.code)) {
                $diagnosticsByCode.Add($diagnostic.code, [System.Collections.Generic.List[object]]::new())
            }
            $diagnosticsByCode[$diagnostic.code].Add($diagnostic)
        }
        if ($diagnosticsByCode.Count -ne $expected.expectedDiagnostics.Count) { throw "Diagnostic codes differ for '$($expected.id)'." }
        $expectedCodes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($expectation in $expected.expectedDiagnostics) {
            Test-EvidenceBudget
            if (-not $expectedCodes.Add($expectation.code) -or -not $diagnosticsByCode.ContainsKey($expectation.code) -or
                $expectation.count -isnot [long] -or $expectation.count -lt 1 -or $expectation.count -gt 128 -or
                $diagnosticsByCode[$expectation.code].Count -ne $expectation.count) { throw "Diagnostic counts differ for '$($expected.id)'." }
            if ($expectation.ContainsKey('occurrences') -and $null -ne $expectation.occurrences) {
                if ($expectation.occurrences -isnot [array] -or $expectation.occurrences.Count -ne $expectation.count) { throw 'Invalid expected diagnostic occurrences.' }
                $occurrences = [System.Collections.Generic.HashSet[long]]::new()
                foreach ($location in $expectation.occurrences) {
                    Test-EvidenceBudget
                    Assert-EvidenceSpan $location $source.Length
                    if ($location.occurrence -isnot [long] -or $location.occurrence -lt 0 -or
                        $location.occurrence -ge $expectation.count -or -not $occurrences.Add($location.occurrence)) { throw 'Invalid expected diagnostic occurrence.' }
                    $observed = $diagnosticsByCode[$expectation.code][$location.occurrence]
                    if ($observed.start -ne $location.start -or $observed.length -ne $location.length) { throw "Diagnostic span differs for '$($expected.id)'." }
                }
            }
        }
        foreach ($resolution in $actual.resolutions) {
            Test-EvidenceBudget
            Assert-EvidenceSpan $resolution $source.Length
        }
        foreach ($expectation in $expected.expectedResolutions) {
            Test-EvidenceBudget
            if ($expectation.occurrence -isnot [long] -or $expectation.occurrence -lt 0 -or $expectation.occurrence -ge 64) { throw 'Invalid expected path occurrence.' }
            $occurrence = 0
            $observed = $null
            foreach ($resolution in $actual.resolutions) {
                Test-EvidenceBudget
                if ($resolution.path -ceq $expectation.path -and $resolution.scopePath -ceq $expectation.scopePath) {
                    if ($occurrence -eq $expectation.occurrence) { $observed = $resolution; break }
                    $occurrence++
                }
            }
            $expectedSymbolKind = if ($expectation.ContainsKey('symbolKind')) { $expectation.symbolKind } else { $null }
            $expectedQualifiedName = if ($expectation.ContainsKey('symbolQualifiedName')) { $expectation.symbolQualifiedName } else { $null }
            $observedPresent = $null -ne $observed
            $observedSymbolKind = if ($observedPresent -and $observed.ContainsKey('symbolKind')) { $observed.symbolKind } else { $null }
            $observedQualifiedName = if ($observedPresent -and $observed.ContainsKey('symbolQualifiedName')) { $observed.symbolQualifiedName } else { $null }
            if ($null -eq $observed -or $observed.status -cne $expectation.status -or
                $observedSymbolKind -cne $expectedSymbolKind -or
                $observedQualifiedName -cne $expectedQualifiedName) {
                throw "Path binding differs for '$($expected.id)': '$($expectation.path)'."
            }
            Assert-EvidenceInteger $observed.candidateCount $expectation.candidateCount 'resolution.candidateCount'
        }
    }
    Test-EvidenceBudget
    Write-Host "Verified $denominator/$denominator name-resolution cases against the current manifest and fixture sources."
}
finally {
    $evidenceClock.Stop()
    Write-Progress -Id 7303 -Activity 'Verify name-resolution evidence' -Completed
}
