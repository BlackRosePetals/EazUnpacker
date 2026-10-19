[CmdletBinding()]
param([string]$OutputRoot=(Join-Path $PSScriptRoot ('..\..\artifacts\delivery-test-'+[Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$samples=Join-Path $OutputRoot 'samples';$case=Join-Path $OutputRoot 'case';$dependencies=Join-Path $OutputRoot 'dependencies'
foreach($path in $samples,$case,$dependencies){New-Item -ItemType Directory -Path $path -Force|Out-Null}
$input=Join-Path $samples 'Fixture.dll';$recovered=Join-Path $case 'Fixture-recovered.dll'
$fixture=Join-Path $PSScriptRoot '..\bin\Release\net48\worker\Newtonsoft.Json.dll'
if(!(Test-Path -LiteralPath $fixture)){throw 'Build the solution first.'}
Copy-Item -LiteralPath $fixture -Destination $input;Copy-Item -LiteralPath $fixture -Destination $recovered
Copy-Item -LiteralPath $fixture -Destination (Join-Path $samples 'Unselected.dll')
Set-Content -LiteralPath ([IO.Path]::ChangeExtension($recovered,'.pdb')) -Value 'Synthetic paired debug-symbol bytes'
Set-Content -LiteralPath ($recovered+'.config') -Value '<configuration><appSettings><add key="Fixture" value="retained"/></appSettings></configuration>'
Set-Content -LiteralPath (Join-Path $dependencies 'native-asset.bin') -Value 'Tested native dependency asset bytes'
Copy-Item -LiteralPath (Join-Path $dependencies 'native-asset.bin') -Destination $case
$dependencyHash=(Get-FileHash -LiteralPath (Join-Path $case 'native-asset.bin')).Hash.ToLowerInvariant()
@{Output=$recovered}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $case 'result.json')
$hash=(Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash.ToLowerInvariant()
$report=Join-Path $OutputRoot 'source-report.json'
$rows=@([ordered]@{
    Input=$input;InputSha256=$hash;InputUnchanged=$true;Status='complete';ExitCode=0
    PublishedOutput=$recovered;OutputSha256=$hash;DiagnosticsDirectory=$case
    DependencyDirectory=$dependencies;DependencyFiles=@(@{File='native-asset.bin';Sha256=$dependencyHash})
    VmDetected=0;VmRestored=0;VmRestoredHosts=0;VmRemaining=0;ClrPrepared=0;ClrFailed=0
})
ConvertTo-Json -InputObject $rows -Depth 8|Set-Content -LiteralPath $report
$delivery=Join-Path $OutputRoot 'delivery'
& (Join-Path $PSScriptRoot 'MergeCompatibilityReports.ps1') -SampleRoot $samples -IncludeNames 'Fixture.dll' -Reports $report -OutputRoot $delivery
if($LASTEXITCODE -ne 0){throw 'Valid delivery merge failed.'}
$package=Join-Path $delivery 'packages\Fixture'
if(!(Test-Path -LiteralPath (Join-Path $package 'Newtonsoft.Json.dll')) -or
   !(Test-Path -LiteralPath (Join-Path $package 'Newtonsoft.Json.dll.config')) -or
   !(Test-Path -LiteralPath (Join-Path $package 'Newtonsoft.Json.pdb')) -or
   !(Test-Path -LiteralPath (Join-Path $package 'native-asset.bin'))){throw 'Delivery lost its tested library/configuration/dependency.'}
$manifest=@(Get-Content -LiteralPath (Join-Path $delivery 'package-manifest.json') -Raw|ConvertFrom-Json)
if($manifest.Count -ne 4){throw 'Delivery manifest did not cover every deployed byte file.'}
foreach($entry in $manifest){
    if((Get-FileHash -LiteralPath (Join-Path $delivery $entry.File) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Sha256){throw 'Delivery manifest hash mismatch.'}
}
Set-Content -LiteralPath (Join-Path $dependencies 'native-asset.bin') -Value 'Changed after validation'
$rejected=$false
try {& (Join-Path $PSScriptRoot 'MergeCompatibilityReports.ps1') -SampleRoot $samples -IncludeNames 'Fixture.dll' -Reports $report -OutputRoot (Join-Path $OutputRoot 'changed-delivery')}
catch {if($_.Exception.Message -like 'Dependency source differs*'){$rejected=$true}else{throw}}
if(!$rejected){throw 'Changed dependency bytes were silently accepted.'}
Write-Output ('PASS: delivery retains configuration, exact dependency snapshots and per-file hashes; changed dependencies rejected. '+$OutputRoot)
exit 0
