[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidateSha,[Parameter(Mandatory)][ValidateSet('windows-x64','linux-x64')][string]$PlatformName,[Parameter(Mandatory)][string]$RunId,[Parameter(Mandatory)][string]$RunAttempt,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
$ErrorActionPreference='Stop';Set-StrictMode -Version 3.0
if($PSVersionTable.PSVersion.Major -lt7){throw 'PowerShell 7 required.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'P1SuiteEvidenceValidation.ps1');. (Join-Path $PSScriptRoot 'P1DropTransportContract.ps1')
$CancellationToken.ThrowIfCancellationRequested()
if($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_SHA -cne $CandidateSha -or $env:GITHUB_RUN_ID -cne $RunId -or $env:GITHUB_RUN_ATTEMPT -cne $RunAttempt -or $env:GITHUB_REPOSITORY -cne 'IoTSharp/RustSharp' -or $env:GITHUB_JOB -cne 'production-gate'){throw 'Authenticated aggregate Actions context required.'}
$capture=Read-P1DropTransportBytes -Root $root -Path ('artifacts/p1-drop-ci/'+$PlatformName+'/transport-index.json') -MaximumBytes 4194304 -Capture -CancellationToken $CancellationToken
$index=ConvertFrom-P1StrictJson $capture.content
$proof=Invoke-P1DropTransportContract -Root $root -ReceiptPath ('artifacts/p1-drop-ci/'+$PlatformName+'/native-receipt.json') -CandidateSha $CandidateSha -PlatformName $PlatformName -RunId $RunId -RunAttempt $RunAttempt -TransportIndex $index -CancellationToken $CancellationToken
Write-P1DropTransportProof -Root $root -Destination ('artifacts/p1-candidate/drop-transport-'+$PlatformName+'.json') -Document $proof -CancellationToken $CancellationToken
