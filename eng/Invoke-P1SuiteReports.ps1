[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string] $PlatformName
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = Join-Path $root "artifacts/p1-expanded/$PlatformName"
$null = [IO.Directory]::CreateDirectory($directory)
$clock = [Diagnostics.Stopwatch]::StartNew()
$failures = [Collections.Generic.List[string]]::new()
$pwsh = if ($IsWindows) { 'C:\Program Files\PowerShell\7\pwsh.exe' } else { (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$assembly = 'tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll'
$harness = 'tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll'
$invocations = @(
    @{ name='coverage'; file='dotnet'; timeout=60; arguments=@($assembly,'--profile','p1-coverage-v1','--report',(Join-Path $directory 'p1-coverage-v1.json')) },
    @{ name='differential'; file='dotnet'; timeout=930; arguments=@($assembly,'--profile','p1-differential-v3','--oracle','rustc-1.98','--timeout','60','--deadline','900','--report',(Join-Path $directory 'p1-differential-v3.json')) },
    @{ name='platform'; file='dotnet'; timeout=930; arguments=@($assembly,'--profile','p1-platform-v2','--timeout','120','--deadline','900','--report',(Join-Path $directory 'p1-platform-v2.json')) },
    @{ name='harness'; file='dotnet'; timeout=960; arguments=@($harness,'--report',(Join-Path $directory 'regression-harness.json'),'--candidate-sha',$CandidateSha,'--deadline','900','--timeout','120') },
    @{ name='baseline-audit'; file=$pwsh; timeout=60; arguments=@('-NoLogo','-NoProfile','-File','eng/Test-P1RegressionAudit.ps1','-RepositoryRoot',$root,'-CandidateSha',$CandidateSha,'-ReportPath',(Join-Path $directory 'immutable-regression-audit.json')) }
)
# Five fixed invocations with an independent 3,000-second wall-clock bound.
# Each command owns process-tree cleanup and bounded captures in the helper.
foreach ($invocation in $invocations) {
    if ($clock.Elapsed.TotalSeconds -ge 3000) { $failures.Add('Native report orchestration deadline expired.'); break }
    Write-Host "Running P1 evidence stage: $($invocation.name)."
    try {
        & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $invocation.file -ArgumentList $invocation.arguments -WorkingDirectory $root -TimeoutSeconds ([Math]::Min($invocation.timeout, [Math]::Max(1, 3000 - [int]$clock.Elapsed.TotalSeconds))) -CapturePrefix (Join-Path $directory ($invocation.name + '-launch'))
    } catch { $failures.Add($invocation.name + ': ' + $_.Exception.Message); Write-Warning $failures[$failures.Count - 1] }
}
if ($failures.Count -gt 0) { Write-Warning ($failures -join '; '); exit 1 }
