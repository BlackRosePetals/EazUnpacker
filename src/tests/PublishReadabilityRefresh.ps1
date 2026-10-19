param([string]$Root='artifacts/readability-generic')
$ErrorActionPreference='Stop'
$Root=[IO.Path]::GetFullPath($Root)
$audit=Join-Path $Root 'report.json'
$refresh=@(Get-Content $audit -Raw | ConvertFrom-Json)
$reports=@{}
function FailureKeys($validation){
    return (@($validation.Methods | Where-Object {$_.Status -ne 'success'} | ForEach-Object {
        "$($_.Token)|$($_.Status)|$($_.FailureKind)|$(($_.RootCause -split ' -> ')[-1])"
    } | Sort-Object) -join "`n")
}
# Validate the entire batch before changing any existing delivery.
foreach($entry in $refresh){
    if(!$reports.ContainsKey($entry.Report)){$reports[$entry.Report]=@(Get-Content $entry.Report -Raw|ConvertFrom-Json)}
    $row=@($reports[$entry.Report] | Where-Object {$_.Input -eq $entry.Input})[0]
    if(!$row){throw "Missing original report row: $($entry.Input)"}
    if((Get-FileHash $row.PublishedOutput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.PreviousSha256){throw 'Previous delivery hash changed.'}
    if((Get-FileHash $entry.Output -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.OutputSha256){throw 'Tested output hash changed.'}
    if(!(Test-Path ([IO.Path]::ChangeExtension($entry.Output,'.pdb')))){throw 'Tested portable symbols are missing.'}
    $packageMain=if($row.PSObject.Properties.Name -contains 'PackageOutput' -and (Test-Path -LiteralPath $row.PackageOutput)){
        if((Get-FileHash $row.PackageOutput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.PackageOutputSha256){throw 'Package output hash changed.'}
        @(Get-Item -LiteralPath $row.PackageOutput)
    }else{@(Get-ChildItem -LiteralPath $row.DeployablePackage -Filter '*.dll' | Where-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $entry.PreviousSha256
    })}
    if($packageMain.Count -ne 1){throw "Package main assembly is ambiguous: $($entry.Input)"}
    $entry | Add-Member PackageOutput $packageMain[0].FullName -Force
    $old=Get-Content (Join-Path $row.DiagnosticsDirectory 'clr-validation.json') -Raw|ConvertFrom-Json
    $new=Get-Content (Join-Path $entry.DiagnosticsDirectory 'clr-validation.json') -Raw|ConvertFrom-Json
    $same=(FailureKeys $old) -eq (FailureKeys $new)
    $entry | Add-Member SameFailureTokensAndCauses $same -Force
    if(!$entry.NoClrCountRegression -or !$same){throw "CLR regression: $($entry.Input)"}
    if((Get-FileHash $row.Input -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.InputSha256){throw 'Original input changed.'}
}
$refresh | ConvertTo-Json -Depth 12 | Set-Content $audit -Encoding UTF8
$backup=Join-Path $Root 'previous-delivery'
if(Test-Path $backup){throw 'A previous-delivery backup already exists; refusing to overwrite it.'}
New-Item $backup -ItemType Directory | Out-Null
$worker=[IO.Path]::GetFullPath('src/bin/Release/net48/worker/Unpacker.Worker.exe')
foreach($report in @($reports.Keys)){
    $reportDir=Split-Path $report
    $save=Join-Path $backup (Split-Path $reportDir -Leaf)
    New-Item $save -ItemType Directory | Out-Null
    foreach($file in @('report.json','report.csv','summary.json','package-manifest.json','table.md')){
        if(Test-Path (Join-Path $reportDir $file)){Copy-Item -LiteralPath (Join-Path $reportDir $file) -Destination $save}
    }
}
foreach($entry in $refresh){
    $row=@($reports[$entry.Report] | Where-Object {$_.Input -eq $entry.Input})[0]
    $name=[IO.Path]::GetFileNameWithoutExtension($row.Input)
    $save=Join-Path $backup $name;New-Item $save -ItemType Directory | Out-Null
    Copy-Item -LiteralPath $row.PublishedOutput -Destination $save
    $oldPdb=[IO.Path]::ChangeExtension($row.PublishedOutput,'.pdb')
    if(Test-Path $oldPdb){Copy-Item -LiteralPath $oldPdb -Destination $save}
    foreach($destination in @($row.PublishedOutput,$entry.PackageOutput)){
        $priorPdb=[IO.Path]::ChangeExtension($destination,'.pdb')
        if((Test-Path $destination) -and (Test-Path $priorPdb)){
            & $worker --publish-symbols $destination $priorPdb
            if($LASTEXITCODE -ne 0){throw "Previous symbol migration failed: $destination"}
        }
        Copy-Item -LiteralPath $entry.Output -Destination $destination -Force
        $pdb=[IO.Path]::ChangeExtension($destination,'.pdb')
        Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($entry.Output,'.pdb')) -Destination $pdb -Force
        & $worker --publish-symbols $destination $pdb
        if($LASTEXITCODE -ne 0){throw "Symbol publication failed: $destination"}
    }
    $row | Add-Member PreReadabilityOutputSha256 $entry.PreviousSha256 -Force
    $row.OutputSha256=(Get-FileHash $row.PublishedOutput -Algorithm SHA256).Hash.ToLowerInvariant();$row.TestedOutput=$entry.Output
    $row | Add-Member PackageOutput $entry.PackageOutput -Force
    $row | Add-Member PackageOutputSha256 ((Get-FileHash $entry.PackageOutput -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
    $row.FlowFolded += $entry.AdditionalFolded;$row.FlowRemoved += $entry.AdditionalRemoved
    $row | Add-Member ReadabilityValidation ([pscustomobject]@{
        DiagnosticsDirectory=$entry.DiagnosticsDirectory;Prepared=$entry.Prepared;Failed=$entry.Failed
        SameFailureTokensAndCauses=$entry.SameFailureTokensAndCauses;AdditionalFolded=$entry.AdditionalFolded;AdditionalRemoved=$entry.AdditionalRemoved
    }) -Force
    if($row.PSObject.Properties.Name -contains 'SymbolRepairValidation'){
        $row | Add-Member PreviousSymbolRepairValidation $row.SymbolRepairValidation -Force
        $row.PSObject.Properties.Remove('SymbolRepairValidation')
    }
}
foreach($report in @($reports.Keys)){
    $rows=$reports[$report];$dir=Split-Path $report
    $rows | ConvertTo-Json -Depth 25 | Set-Content $report -Encoding UTF8
    $rows | Export-Csv (Join-Path $dir 'report.csv') -NoTypeInformation -Encoding UTF8
    $manifest=@(Get-ChildItem -LiteralPath (Join-Path $dir 'packages') -Recurse -File | ForEach-Object {
        [pscustomobject]@{File=[IO.Path]::GetRelativePath($dir,$_.FullName);Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'package-manifest.json') -Encoding UTF8
    $summaryPath=Join-Path $dir 'summary.json'
    if(Test-Path $summaryPath){
        $summary=Get-Content $summaryPath -Raw|ConvertFrom-Json
        $summary | Add-Member ReadabilityRefreshReport $audit -Force
        $summary | ConvertTo-Json -Depth 15 | Set-Content $summaryPath -Encoding UTF8
    }
    $table=@('| DLL | VM hosts | VM bodies | CLR prepared | Proxy changes | Branch folds | Removed IL |','| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
    foreach($row in $rows){$table += "| $([IO.Path]::GetFileName($row.Input)) | $($row.VmRestoredHosts) | $($row.VmRestored) | $($row.ClrPrepared) | $($row.ProxyChanges) | $($row.FlowFolded) | $($row.FlowRemoved) |"}
    $table | Set-Content (Join-Path $dir 'table.md') -Encoding UTF8
}
Write-Output "Published $($refresh.Count) validated readability refreshes; prior files and reports saved in $backup"
