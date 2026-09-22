[CmdletBinding()]
param([string] $ReportPath = 'artifacts/p1-05/safe-core-generics-v1.json')

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$report = Get-Content -Raw -Encoding utf8 -LiteralPath $ReportPath | ConvertFrom-Json
$manifestPath = Join-Path $PSScriptRoot '../tools/RustSharp.Conformance/manifests/safe-core-generics-v1.json'
$manifest = Get-Content -Raw -Encoding utf8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($report.schemaVersion -ne 2 -or $report.profile -cne 'safe-core-generics-v1' -or
    $report.catalogVersion -ne 2 -or $report.catalogValidated -ne $true -or
    $report.executableConformance -ne $true -or
    $report.evidenceScope -cne 'generic-check-execution-differential-and-profile-boundaries' -or
    (@($report.requiredCategories) -join ',') -cne 'calls,bodies,bounds,coherence,names,boundaries,execution' -or
    $report.manifestSha256 -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -or
    $report.summary.status -cne 'passed' -or $report.summary.denominator -ne 32 -or
    $report.summary.executed -ne 32 -or $report.summary.passed -ne 32 -or
    $report.summary.failed -ne 0 -or $report.summary.skipped -ne 0 -or
    $report.cancelledOrDeadlineExpired -ne $false -or $report.oracle.available -ne $true -or
    -not [string]::IsNullOrEmpty($report.runDirectoryCleanupDiagnostic) -or
    @($report.cases).Count -ne 32 -or @($report.cases | Where-Object kind -CEQ 'run-pass').Count -ne 8 -or
    (@($report.cases.id | Sort-Object) -join ',') -cne (@($manifest.cases.id | Sort-Object) -join ',')) {
    throw 'Generic executable evidence is incomplete or does not match the fixed corpus.'
}
foreach ($case in $report.cases) {
    $sourcePath = Join-Path $PSScriptRoot ('../tools/RustSharp.Conformance/fixtures/generics/' + $case.id + '.rs')
    $fixture = @($manifest.cases | Where-Object id -CEQ $case.id)
    if ($fixture.Count -ne 1 -or $case.category -cne $fixture[0].category -or
        $case.status -cne 'passed' -or $case.sourceSha256 -cne (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) {
        throw 'A generic fixture result is missing or stale.'
    }
    if ($case.kind -cne 'run-pass') { continue }
    if ($case.category -cne 'execution' -or $case.rustSharpCompiled -ne $true -or
        $case.assemblySha256 -cnotmatch '^[A-F0-9]{64}$' -or
        $case.expectedStandardOutput -cne $fixture[0].expectedStandardOutput) {
        throw 'A generic execution fixture lacks compiled output or fixed stdout evidence.'
    }
    foreach ($probe in @($case.managedExecution, $case.rustcExecution)) {
        $process = $probe.result
        if ($null -eq $process -or -not [string]::IsNullOrEmpty($probe.error) -or
            $process.termination -cne 'Exited' -or $process.exitCode -ne 0 -or
            $process.outputTruncated -ne $false -or $process.outputReadTimedOut -ne $false -or
            $process.outputDrainTimedOut -ne $false -or $process.outputReadLimitReached -ne $false -or
            $process.processTreeCleanupIncomplete -ne $false -or $process.standardError -cne '' -or
            $process.standardOutput.Replace("`r`n", "`n") -cne $fixture[0].expectedStandardOutput) {
            throw 'A generic executable process lacks clean, matching runtime evidence.'
        }
    }
}
Write-Output 'Generic evidence: 32/32 fixed cases, including 8/8 matching executions.'
