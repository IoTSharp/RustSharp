[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string] $CandidateSha,
    [Parameter(Mandatory)][string] $ReportPath,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string] $DotNetPath = 'dotnet',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $SdkVersion = '10.0.400'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$report = [IO.Path]::GetFullPath($ReportPath, $root)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report))
$started = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$steps = [Collections.Generic.List[object]]::new()
$failure = $null
$warnings = $null
$errors = $null
$sdkProbe = $null
$compilerHash = $null
$compilerPath = 'src/RustSharp.Cli/bin/Release/net10.0/rsc.dll'
$testsAssemblyPath = 'tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll'
$testsAssemblyHash = $null
$registrationInventory = $null
$registrationInventoryProcess = $null
$registrationInventoryPath = $null
$registrationInventoryHash = $null
$oldLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$oldBuildServer = $env:DOTNET_CLI_USE_MSBUILD_SERVER
try {
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $dotnet = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $dotnetInfo = [IO.FileInfo]::new($dotnet)
    $dotnetTarget = $dotnetInfo.ResolveLinkTarget($true)
    $dotnetRoot = Split-Path -Parent $(if ($null -eq $dotnetTarget) { $dotnet } else { $dotnetTarget.FullName })
    $sdkDriver = Join-Path $dotnetRoot ("sdk/$SdkVersion/dotnet.dll")
    if (-not [IO.File]::Exists($sdkDriver)) { throw "Requested .NET SDK driver is unavailable: $sdkDriver" }
    $sdkPrefix = Join-Path ([IO.Path]::GetDirectoryName($report)) 'build-sdk'
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList @($sdkDriver,'--version') -WorkingDirectory $root -TimeoutSeconds 15 -CapturePrefix $sdkPrefix
    $observedSdk = [IO.File]::ReadAllText($sdkPrefix + '.stdout.log').Trim()
    $sdkProbe = [IO.File]::ReadAllText($sdkPrefix + '.process.json') | ConvertFrom-Json
    if ($observedSdk -cne $SdkVersion) { throw 'Observed .NET SDK does not match the selected SDK.' }
    $commands = @(
        @{ name='restore'; timeout=180; arguments=@('restore', 'RustSharp.slnx', '--disable-build-servers') },
        @{ name='build'; timeout=300; arguments=@('build', 'RustSharp.slnx', '-c', 'Release', '--no-restore', '-m:1', '--disable-build-servers', '-p:UseSharedCompilation=false') }
    )
    # Exactly two build commands, one SDK probe and one registration inventory
    # command; the helper records identity and reclaims its owned tree.
    foreach ($command in $commands) {
        if ($clock.Elapsed.TotalSeconds -ge 515) { throw 'Release build orchestration deadline expired.' }
        $prefix = Join-Path ([IO.Path]::GetDirectoryName($report)) ('build-' + $command.name)
        $stepFailure = $null
        try {
            & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList (@($sdkDriver) + $command.arguments) -WorkingDirectory $root -TimeoutSeconds $command.timeout -CapturePrefix $prefix
        } catch { $stepFailure = $_.Exception.Message }
        $processPath = $prefix + '.process.json'
        $process = if ([IO.File]::Exists($processPath)) { [IO.File]::ReadAllText($processPath) | ConvertFrom-Json } else { $null }
        $steps.Add([ordered]@{ name=$command.name; succeeded=($null -eq $stepFailure); process=$process; stdoutPath=[IO.Path]::GetRelativePath($root, $prefix + '.stdout.log').Replace('\','/'); stderrPath=[IO.Path]::GetRelativePath($root, $prefix + '.stderr.log').Replace('\','/'); harnessError=$stepFailure })
        if ($stepFailure) { throw $stepFailure }
        if ($command.name -ceq 'build') {
            $output = [IO.File]::ReadAllText($prefix + '.stdout.log')
            $warningMatches = [regex]::Matches($output, '(?m)^\s*([0-9]+) Warning\(s\)\s*$')
            $errorMatches = [regex]::Matches($output, '(?m)^\s*([0-9]+) Error\(s\)\s*$')
            if ($warningMatches.Count -ne 1 -or $errorMatches.Count -ne 1) { throw 'Release build did not publish an unambiguous warning/error summary.' }
            $warnings = [int]$warningMatches[0].Groups[1].Value
            $errors = [int]$errorMatches[0].Groups[1].Value
            if ($warnings -ne 0 -or $errors -ne 0) { throw 'Release acceptance requires zero warnings and errors.' }
            $compilerFullPath = Join-Path $root $compilerPath
            if (-not [IO.File]::Exists($compilerFullPath)) { throw 'Release build did not produce the compiler assembly.' }
            $compilerHash = (Get-FileHash -LiteralPath $compilerFullPath -Algorithm SHA256).Hash
        }
    }
    if ($clock.Elapsed.TotalSeconds -ge 535) { throw 'Release inventory orchestration deadline expired.' }
    $testsAssemblyFullPath = Join-Path $root $testsAssemblyPath
    if (-not [IO.File]::Exists($testsAssemblyFullPath)) { throw 'Release build did not produce the tests assembly.' }
    $testsAssemblyHash = (Get-FileHash -LiteralPath $testsAssemblyFullPath -Algorithm SHA256).Hash
    $inventoryPrefix = Join-Path ([IO.Path]::GetDirectoryName($report)) 'build-registration-inventory'
    & (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1') -FilePath $dotnet -ArgumentList @($testsAssemblyFullPath,'--list') -WorkingDirectory $root -TimeoutSeconds 20 -MaximumOutputBytes 1MB -CapturePrefix $inventoryPrefix
    $registrationInventoryProcess = [IO.File]::ReadAllText($inventoryPrefix + '.process.json') | ConvertFrom-Json
    $inventoryBytes = [IO.File]::ReadAllBytes($inventoryPrefix + '.stdout.log')
    if ($inventoryBytes.Length -gt 1MB) { throw 'Release registration inventory exceeds its one MiB bound.' }
    $registrationInventory = ConvertFrom-P1StrictJson $inventoryBytes
    $inventoryErrors = @(Test-P1RegistrationInventory $registrationInventory $testsAssemblyHash)
    if ($inventoryErrors.Count -gt 0) { throw ($inventoryErrors -join '; ') }
    if ((Get-FileHash -LiteralPath $testsAssemblyFullPath -Algorithm SHA256).Hash -ine $testsAssemblyHash) { throw 'Tests assembly changed while listing its registration inventory.' }
    $registrationInventoryPath = [IO.Path]::GetRelativePath($root, $inventoryPrefix + '.stdout.log').Replace('\','/')
    $registrationInventoryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inventoryBytes))
} catch { $failure = $_.Exception.Message }
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $oldLanguage
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $oldBuildServer
    $succeeded = $null -eq $failure -and $steps.Count -eq 2 -and $warnings -ceq 0 -and $errors -ceq 0 -and $null -ne $registrationInventory
    [ordered]@{ schemaVersion=1; evidenceKind='p1-release-build'; candidateSha=$CandidateSha; configuration='Release'; succeeded=$succeeded; sdkVersion=$SdkVersion; sdkProbe=$sdkProbe; compilerPath=$compilerPath; compilerSha256=$compilerHash; testsAssemblyPath=$testsAssemblyPath; testsAssemblySha256=$testsAssemblyHash; registrationInventory=$registrationInventory; registrationInventoryProcess=$registrationInventoryProcess; registrationInventoryPath=$registrationInventoryPath; registrationInventorySha256=$registrationInventoryHash; summary=@{ status=if($succeeded){'passed'}else{'blocked'}; warnings=$warnings; errors=$errors }; steps=@($steps.ToArray()); execution=@{ startedAtUtc=$started.ToString('O'); finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); maximumCommands=4; deadlineSeconds=535 }; harnessError=$failure } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8
}
if (-not $succeeded) { Write-Warning $failure; exit 2 }
