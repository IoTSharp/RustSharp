#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ReportPath = 'artifacts/conformance/safe-core-syntax.json',
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$manifestPath = Join-Path $RepositoryRoot 'tools/RustSharp.Conformance/fixtures/safe-core-syntax-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable
$report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json -AsHashtable
$denominator = $manifest.denominator
if ($denominator -isnot [long] -or $denominator -lt 1 -or $denominator -gt 64 -or
    $manifest.cases.Count -ne $denominator -or $report.cases.Count -ne $denominator -or
    $report.schemaVersion -ne 2 -or $report.profile -cne 'safe-core-syntax' -or
    $report.evidenceKind -cne 'parser-acceptance' -or
    $report.rustVersion -cne '1.98.0' -or $report.edition -cne '2024' -or
    $report.scope.rustcConformance -isnot [bool] -or $report.scope.rustcConformance -or
    $report.scope.runtimeConformance -isnot [bool] -or $report.scope.runtimeConformance -or
    $report.manifest.validated -isnot [bool] -or -not $report.manifest.validated -or
    $manifest.version -ne 2 -or $report.manifest.version -ne 2 -or
    $report.manifest.caseCount -ne $denominator -or
    $report.manifest.sha256 -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -or
    $report.summary.status -cne 'passed' -or $report.summary.exitCode -ne 0 -or
    $report.summary.denominator -ne $denominator -or $report.summary.executed -ne $denominator -or
    $report.summary.passed -ne $denominator -or $report.summary.failed -ne 0 -or
    $report.summary.errors -ne 0 -or $report.summary.skipped -ne 0 -or
    $null -ne $report.harnessError) {
    throw 'Syntax evidence is incomplete or does not match the current manifest.'
}

$categories = @('modules', 'imports', 'functions', 'structs', 'enums', 'aliases-constants',
    'statements', 'expressions', 'operator-binding', 'patterns', 'types', 'generics',
    'attributes', 'literals', 'malformed', 'unsupported')
if ($report.coverage.Count -ne $categories.Count -or $manifest.coverage.Count -ne $categories.Count) {
    throw 'Syntax coverage category counts differ.'
}
foreach ($category in $categories) {
    if ($clock.Elapsed.TotalSeconds -gt 30) { throw 'Syntax evidence verification timed out.' }
    $expectedIds = $manifest.coverage[$category]
    $actualIds = $report.coverage[$category]
    if ($null -eq $expectedIds -or $null -eq $actualIds -or $expectedIds.Count -gt 64 -or
        ($expectedIds -join '|') -cne ($actualIds -join '|')) {
        throw "Syntax coverage differs for '$category'."
    }
}

$ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
for ($index = 0; $index -lt $denominator; $index++) {
    if ($clock.Elapsed.TotalSeconds -gt 30) { throw 'Syntax evidence verification timed out.' }
    $expected = $manifest.cases[$index]
    $actual = $report.cases[$index]
    if ([IO.Path]::GetFileName($expected.file) -cne $expected.file) { throw 'Invalid fixture path.' }
    $sourcePath = Join-Path (Split-Path -Parent $manifestPath) $expected.file
    if (-not $ids.Add($actual.id) -or $actual.id -cne $expected.id -or
        $actual.source -cne $expected.file -or $actual.expected -cne $expected.expected -or
        $actual.actual -cne $expected.expected -or $actual.status -cne 'passed' -or
        $actual.parserInvoked -isnot [bool] -or -not $actual.parserInvoked -or
        $actual.isTruncated -isnot [bool] -or $actual.isTruncated -or
        $actual.diagnosticsTruncated -isnot [bool] -or $actual.diagnosticsTruncated -or
        $actual.sourceSha256 -cne (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) {
        throw "Syntax case '$($expected.id)' is incomplete or stale."
    }
    if ($expected.expected -ceq 'parse-fail') {
        if ($actual.diagnostics.Count -gt 128) { throw 'Syntax diagnostics exceed the report bound.' }
        $source = [IO.File]::ReadAllText($sourcePath)
        $matched = $false
        foreach ($diagnostic in $actual.diagnostics) {
            if ($diagnostic.start -lt 0 -or $diagnostic.length -lt 0 -or
                $diagnostic.start -gt $source.Length - $diagnostic.length) { throw 'Invalid diagnostic span.' }
            if ($diagnostic.code -ceq $expected.diagnosticCode -and
                $source.Substring($diagnostic.start, $diagnostic.length) -ceq $expected.diagnosticText) {
                $matched = $true
            }
        }
        if (-not $matched) { throw "Syntax case '$($expected.id)' lacks its expected diagnostic span." }
    }
}
Write-Host "Verified $denominator/$denominator syntax cases and $($categories.Count)/$($categories.Count) categories against current sources."
