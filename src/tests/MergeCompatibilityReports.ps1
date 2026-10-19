[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SampleRoot,
    [string[]]$IncludeNames=@(),
    [Parameter(Mandatory=$true)][string[]]$Reports,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$WorkerPath=(Join-Path $PSScriptRoot '..\bin\Release\net48\worker\Unpacker.Worker.exe')
)
$ErrorActionPreference='Stop'
function Publish-Symbols([string]$Assembly,[string]$Pdb){
    if(!(Test-Path -LiteralPath $Pdb)){return}
    $arguments=@('--publish-symbols',$Assembly,$Pdb)|ForEach-Object{'"'+$_.Replace('"','\"')+'"'}
    $process=Start-Process -FilePath $WorkerPath -ArgumentList ($arguments -join ' ') -WindowStyle Hidden -Wait -PassThru
    if($process.ExitCode -ne 0){throw ('CodeView symbol publication failed: '+$Assembly)}
}
$SampleRoot=(Resolve-Path -LiteralPath $SampleRoot).Path.TrimEnd('\')
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if($OutputRoot -eq $SampleRoot -or $OutputRoot.StartsWith($SampleRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Delivery directory must be outside the sample corpus.'
}
if(Test-Path -LiteralPath (Join-Path $OutputRoot 'report.json')){throw 'Use a fresh delivery directory to avoid stale outputs.'}
$expected=@(Get-ChildItem -LiteralPath $SampleRoot -Recurse -File -Filter '*.dll' | Where-Object {
    $_.BaseName -notmatch '(?i)unpacked$' -and (!$IncludeNames.Count -or $_.Name -in $IncludeNames)
})
if(!$expected.Count){throw 'No original DLL matches the selected corpus.'}
$sources=@($Reports | ForEach-Object {(Resolve-Path -LiteralPath $_).Path})
$all=@($sources | ForEach-Object {Get-Content -LiteralPath $_ -Raw|ConvertFrom-Json})
$duplicates=@($all | Group-Object Input | Where-Object Count -ne 1)
if($duplicates.Count){throw 'Every original must have exactly one explicitly selected report entry; do not silently hide failed or older runs.'}
if(@($all | Where-Object {$_.Input -notin $expected.FullName}).Count){throw 'Report contains inputs outside the selected original corpus.'}
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'unpacked') -Force|Out-Null
$rows=@()
$deliveryFiles=@()
foreach($sample in $expected|Sort-Object FullName){
    $row=$all|Where-Object Input -EQ $sample.FullName
    if(!$row){throw ('Missing report for '+$sample.FullName)}
    $actualInput=(Get-FileHash -LiteralPath $sample.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if(!$row.InputUnchanged -or $actualInput -ne $row.InputSha256){throw ('Original hash changed: '+$sample.FullName)}
    if(!$row.PublishedOutput -or !(Test-Path -LiteralPath $row.PublishedOutput)){throw ('No recovered output: '+$sample.FullName)}
    $hash=(Get-FileHash -LiteralPath $row.PublishedOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    if($hash -ne $row.OutputSha256){throw ('Recovered output hash differs from tested output: '+$sample.FullName)}
    $relative=$sample.FullName.Substring($SampleRoot.Length+1)
    $destination=Join-Path (Join-Path $OutputRoot 'unpacked') ([IO.Path]::ChangeExtension($relative,$null).TrimEnd('.')+'_unpacked.dll')
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force|Out-Null
    Copy-Item -LiteralPath $row.PublishedOutput -Destination $destination
    $pdb=[IO.Path]::ChangeExtension($row.PublishedOutput,'.pdb')
    if(Test-Path -LiteralPath $pdb){Copy-Item -LiteralPath $pdb -Destination ([IO.Path]::ChangeExtension($destination,'.pdb'))}
    Publish-Symbols $destination ([IO.Path]::ChangeExtension($destination,'.pdb'))
    # Earlier report producers kept the config in the case directory only.
    $caseResultPath=Join-Path $row.DiagnosticsDirectory 'result.json'
    $caseResult=Get-Content -LiteralPath $caseResultPath -Raw|ConvertFrom-Json
    $configuration=$caseResult.Output+'.config'
    if(Test-Path -LiteralPath $configuration){Copy-Item -LiteralPath $configuration -Destination ($destination+'.config')}
    $package=Join-Path (Join-Path $OutputRoot 'packages') $sample.BaseName
    New-Item -ItemType Directory -Path $package -Force|Out-Null
    $ownName=[Reflection.AssemblyName]::GetAssemblyName($sample.FullName).Name
    $packageName=$ownName+'.dll'
    Copy-Item -LiteralPath $destination -Destination (Join-Path $package $packageName)
    $pdb=[IO.Path]::ChangeExtension($destination,'.pdb')
    if(Test-Path -LiteralPath $pdb){Copy-Item -LiteralPath $pdb -Destination (Join-Path $package ([IO.Path]::ChangeExtension($packageName,'.pdb')))}
    Publish-Symbols (Join-Path $package $packageName) (Join-Path $package ([IO.Path]::ChangeExtension($packageName,'.pdb')))
    if(Test-Path -LiteralPath ($destination+'.config')){Copy-Item -LiteralPath ($destination+'.config') -Destination (Join-Path $package ($packageName+'.config'))}
    if($row.DependencyDirectory -and (Test-Path -LiteralPath $row.DependencyDirectory)){
        foreach($source in Get-ChildItem -LiteralPath $row.DependencyDirectory -Recurse -File){
            $relativeDependency=$source.FullName.Substring($row.DependencyDirectory.Length+1)
            $snapshot=Join-Path $row.DiagnosticsDirectory $relativeDependency
            if(!(Test-Path -LiteralPath $snapshot)){throw ('Missing tested dependency snapshot: '+$snapshot)}
            $dependencyHash=(Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant()
            $expectedDependency=@($row.DependencyFiles|Where-Object File -eq $relativeDependency)
            if($expectedDependency.Count -eq 1 -and $expectedDependency[0].Sha256 -ne $dependencyHash){throw ('Tested dependency hash changed: '+$snapshot)}
            if((Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dependencyHash){throw ('Dependency source differs from its tested snapshot: '+$source.FullName)}
            $deployedDependency=Join-Path $package $relativeDependency
            New-Item -ItemType Directory -Path (Split-Path $deployedDependency) -Force|Out-Null
            Copy-Item -LiteralPath $snapshot -Destination $deployedDependency -Force
        }
    }
    # Include dependencies extracted from the protected input (e.g. embedded
    # libraries), while excluding all intermediate copies of the input itself.
    foreach($dependency in Get-ChildItem -LiteralPath $row.DiagnosticsDirectory -File -Filter '*.dll'){
        try {$name=[Reflection.AssemblyName]::GetAssemblyName($dependency.FullName).Name}
        catch [BadImageFormatException] {continue}
        if($name -ne $ownName){Copy-Item -LiteralPath $dependency.FullName -Destination (Join-Path $package $dependency.Name) -Force}
    }
    $row|Add-Member -NotePropertyName DeployablePackage -NotePropertyValue $package
    # Symbol publication rebinds only CodeView debug data to the one final PDB.
    # Track both the tested pre-publication image and each published image hash.
    $row|Add-Member -NotePropertyName PrePublicationOutputSha256 -NotePropertyValue $hash -Force
    $row|Add-Member -NotePropertyName PackageOutput -NotePropertyValue (Join-Path $package $packageName) -Force
    $row|Add-Member -NotePropertyName PackageOutputSha256 -NotePropertyValue ((Get-FileHash -LiteralPath (Join-Path $package $packageName) -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
    $row.OutputSha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $row|Add-Member -NotePropertyName TestedOutput -NotePropertyValue $row.PublishedOutput
    $row.PublishedOutput=$destination
    $rows+=$row
}
Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'packages') -Recurse -File|ForEach-Object{
    $deliveryFiles+=[pscustomobject]@{File=$_.FullName.Substring($OutputRoot.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
ConvertTo-Json -InputObject $deliveryFiles -Depth 5|Set-Content -LiteralPath (Join-Path $OutputRoot 'package-manifest.json') -Encoding UTF8
ConvertTo-Json -InputObject $rows -Depth 40|Set-Content -LiteralPath (Join-Path $OutputRoot 'report.json') -Encoding UTF8
$rows|Select-Object Input,Status,VmDetected,VmRestoredHosts,VmRestored,VmRemaining,ClrPrepared,ClrFailed,PublishedOutput|
    Export-Csv -LiteralPath (Join-Path $OutputRoot 'report.csv') -NoTypeInformation -Encoding UTF8
$incomplete=@($rows|Where-Object {$_.Status -ne 'complete' -or $_.ExitCode -ne 0 -or $_.VmRemaining -ne 0 -or $_.ClrFailed -ne 0 -or $_.VmRestoredHosts -ne $_.VmDetected})
$summary=[ordered]@{
    OriginalCount=$expected.Count;RecoveredCount=$rows.Count;CompleteCount=$rows.Count-$incomplete.Count
    OriginalHashesVerified=$true;OutputHashesVerified=$true
    VmBodiesRestored=($rows|Measure-Object VmRestored -Sum).Sum
    VmHostsRestored=($rows|Measure-Object VmRestoredHosts -Sum).Sum
    ClrPrepared=($rows|Measure-Object ClrPrepared -Sum).Sum
    Reports=$sources
    ValidationScope='All detected VM call sites, metadata reload and host/adapter/access-bridge CLR preparation. Not proof of business equivalence or recovery of destroyed original symbols. Unproven reachable code and reflection-visible definitions retained.'
}
$summary|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding UTF8
$summary|ConvertTo-Json -Depth 4|Write-Output
if($incomplete.Count){exit 2}
exit 0
