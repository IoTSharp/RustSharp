[CmdletBinding()]
param([ValidateSet(1,9)][int] $MaximumChecks=9, [ValidateRange(1,60)][int] $DeadlineSeconds=30)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1')
$clock=[Diagnostics.Stopwatch]::StartNew()
$checks=0
function Assert-CapacityResult([object[]] $Errors,[string] $Expected='') {
    if ($checks -ge $MaximumChecks -or $clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Capacity validation budget exceeded.' }
    if ($Expected) {
        if (@($Errors | Where-Object { $_ -match $Expected }).Count -eq 0) { throw "Expected rejection '$Expected': $($Errors -join '; ')" }
    } elseif ($Errors.Count -ne 0) { throw "Unit positive control failed: $($Errors -join '; ')" }
    $script:checks++
}
function New-CapacityUnitInventory([int] $Count,[int] $Version) {
    if ($Count -lt 1 -or $Count -gt 4097) { throw 'Unit fixture denominator is outside its bound.' }
    $ids=@(for ($index=0;$index -lt $Count -and $index -lt 4097;$index++) {
        if ($clock.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'Fixture generation deadline exceeded.' }
        'capacity-unit-'+$index
    })
    if ($ids.Count -ne $Count) { throw 'Fixture generation was incomplete.' }
    return [pscustomobject]@{schemaVersion=$Version;evidenceKind='p1-regression-registration-inventory';runtimeIdentifier='win-x64';
        buildConfiguration='Release';assemblySha256='B'*64;registeredDenominator=$Count;registeredIds=$ids;
        registeredIdsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ids -join "`n")))}
}
# These invented registration lists are unit inputs, never compiler/platform evidence.
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 464 1) ('B'*64) 'win-x64')
if ($MaximumChecks -eq 1) { Write-Output '✅ Complete: one bounded capacity unit trial.'; return }
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 1024 1) ('B'*64) 'win-x64')
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 1025 1) ('B'*64) 'win-x64') 'denominator'
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 1025 2) ('B'*64) 'win-x64')
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 4096 2) ('B'*64) 'win-x64')
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 4097 2) ('B'*64) 'win-x64') 'denominator'
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 464 3) ('B'*64) 'win-x64') 'inventory or tests assembly binding'
$duplicate=New-CapacityUnitInventory 1025 2; $duplicate.registeredIds[1]=$duplicate.registeredIds[0]
Assert-CapacityResult @(Test-P1RegistrationInventory $duplicate ('B'*64) 'win-x64') 'duplicate IDs'
Assert-CapacityResult @(Test-P1RegistrationInventory (New-CapacityUnitInventory 1025 2) ('C'*64) 'win-x64') 'inventory or tests assembly binding'
if ($checks -ne 9) { throw 'Capacity unit denominator changed.' }
Write-Output ('✅ Complete: {0}/9 versioned capacity unit checks; elapsed={1:N3}s; no execution closure claimed.' -f $checks,$clock.Elapsed.TotalSeconds)
