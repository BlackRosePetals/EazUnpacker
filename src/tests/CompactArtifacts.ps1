param([switch]$Execute)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root=[IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if(!(Test-Path -LiteralPath $root -PathType Container)){throw 'Artifacts directory is missing.'}
if((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Artifacts root must not be a reparse point.'}
$rootPrefix=$root.TrimEnd('\')+'\'
function Assert-Contained([string]$path){
    $full=[IO.Path]::GetFullPath($path)
    if(!$full.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Outside artifacts: $full"}
    return $full
}
$keep=@('aspose-delivery','vba-delivery-final','final-audit')
$targets=@(Get-ChildItem -LiteralPath $root -Force | Where-Object {$_.Name -notin $keep})
foreach($target in $targets){
    Assert-Contained $target.FullName | Out-Null
    if($target.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Refusing to traverse a reparse target: $($target.FullName)"}
    if($target.PSIsContainer -and @(Get-ChildItem -LiteralPath $target.FullName -Recurse -Force -Attributes ReparsePoint).Count){throw "Nested reparse point found: $($target.FullName)"}
}
$before=[long](Get-ChildItem -LiteralPath $root -Recurse -File | Measure-Object Length -Sum).Sum
if(!$Execute){[pscustomobject]@{Root=$root;BeforeGiB=[math]::Round($before/1GB,2);Keep=$keep;DeleteTargets=$targets.Name}|ConvertTo-Json -Depth 5;exit 0}
$audit=Join-Path $root 'final-audit';New-Item $audit -ItemType Directory -Force|Out-Null
$reports=@((Join-Path $root 'aspose-delivery/report.json'),(Join-Path $root 'vba-delivery-final/report.json'))
$all=@();foreach($path in $reports){$all+=@(Get-Content $path -Raw|ConvertFrom-Json)}
# Preserve current, directly readable evidence before removing any intermediates.
foreach($row in $all){
    if((Get-FileHash $row.PublishedOutput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.OutputSha256){throw 'Published DLL hash mismatch; cleanup stopped.'}
    $name=[IO.Path]::GetFileNameWithoutExtension($row.Input)
    $case=Join-Path $audit ('cases/'+$name);New-Item $case -ItemType Directory -Force|Out-Null
    $recent=$row.ReadabilityValidation.DiagnosticsDirectory
    foreach($file in @('vm-methods.json','result.json','async-recovery.json','integrity-compatibility.json')){
        $source=Join-Path $row.DiagnosticsDirectory $file
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $case $file) -Force}
    }
    foreach($file in @('rename-map.json','control-flow-cleanup.json','safe-proxies.json','clr-validation.json','ilspy11-final.log','ilspy11-final.err')){
        $source=Join-Path $recent $file
        if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $case $file) -Force}
    }
    $row | Add-Member HistoricalDiagnosticsDirectory $row.DiagnosticsDirectory -Force
    $row.DiagnosticsDirectory=$case;$row.TestedOutput=$row.PublishedOutput
    $row.ReadabilityValidation.DiagnosticsDirectory=$case
    if($row.ReadabilityValidation.PSObject.Properties.Name -contains 'ILSpyValidation'){$row.ReadabilityValidation.ILSpyValidation.Evidence=Join-Path $case 'ilspy11-final.log'}
    if($row.PSObject.Properties.Name -contains 'PreviousSymbolRepairValidation'){$row.PreviousSymbolRepairValidation.Evidence=Join-Path $audit 'history.zip'}
    $resultPath=Join-Path $case 'result.json'
    if(Test-Path $resultPath){$result=Get-Content $resultPath -Raw|ConvertFrom-Json;$result.Output=$row.PublishedOutput;$result.DiagnosticsDirectory=$case;$result|ConvertTo-Json -Depth 30|Set-Content $resultPath -Encoding UTF8}
    $row|ConvertTo-Json -Depth 30|Set-Content (Join-Path $case 'delivery-row.json') -Encoding UTF8
}
$logs=Join-Path $audit 'logging';New-Item $logs -ItemType Directory -Force|Out-Null
foreach($directory in @('generic-key-imaging','generic-key-psd','generic-key-tests','logging-cli-imaging','logging-cli-smoke','vba-original-check')){
    $destination=Join-Path $logs $directory;New-Item $destination -ItemType Directory -Force|Out-Null
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $root $directory) -File | Where-Object {$_.Extension -in '.log','.json','.jsonl','.err','.txt' -and $_.Length -le 5MB}){
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
$example=Join-Path $root 'naming-generic-preview/method-comparison.cs.txt'
if(Test-Path $example){Copy-Item $example (Join-Path $audit 'method-comparison.cs.txt') -Force}
$refresh=@(Get-Content (Join-Path $root 'readability-generic/report.json') -Raw|ConvertFrom-Json)
foreach($entry in $refresh){$entry.Output=$entry.PublishedOutput;$entry.DiagnosticsDirectory=Join-Path $audit ('cases/'+[IO.Path]::GetFileNameWithoutExtension($entry.Input))}
$refresh|ConvertTo-Json -Depth 15|Set-Content (Join-Path $audit 'readability-report.json') -Encoding UTF8
# Keep historical text evidence compressed, never duplicate PE files or runtimes.
$zipPath=Join-Path $audit 'history.zip';$archived=0;$bytesArchived=[long]0;$skipped=0
$zip=[IO.Compression.ZipArchive]::new([IO.File]::Create($zipPath),[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($target in $targets){
        $files=if($target.PSIsContainer){Get-ChildItem -LiteralPath $target.FullName -Recurse -File}else{@($target)}
        foreach($file in $files){
            if($file.FullName -match '\\(bin|obj|runtime|decoder-runtime|input|packages|unpacked|previous-delivery)\\'){continue}
            if($file.Extension -notin '.json','.jsonl','.log','.txt','.md','.csv','.ps1','.cs','.csproj'){continue}
            if($file.Length -gt 10MB -or $bytesArchived+$file.Length -gt 300MB){$skipped++;continue}
            $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
            $entry=$zip.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
            $source=[IO.File]::OpenRead($file.FullName);$destination=$entry.Open()
            try{$source.CopyTo($destination)}finally{$source.Dispose();$destination.Dispose()}
            $archived++;$bytesArchived+=$file.Length
        }
    }
}finally{$zip.Dispose()}
# Update retained report pointers and manifests, so active references remain valid.
foreach($reportPath in $reports){
    $dir=Split-Path $reportPath;$rows=@($all|Where-Object {$_.PublishedOutput.StartsWith($dir+'\',[StringComparison]::OrdinalIgnoreCase)})
    $rows|ConvertTo-Json -Depth 30|Set-Content $reportPath -Encoding UTF8
    $rows|Export-Csv (Join-Path $dir 'report.csv') -NoTypeInformation -Encoding UTF8
    $manifest=@(Get-ChildItem -LiteralPath (Join-Path $dir 'packages') -Recurse -File|ForEach-Object {[pscustomobject]@{File=[IO.Path]::GetRelativePath($dir,$_.FullName);Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $manifest|ConvertTo-Json -Depth 5|Set-Content (Join-Path $dir 'package-manifest.json') -Encoding UTF8
    $summaryPath=Join-Path $dir 'summary.json'
    if(Test-Path $summaryPath){$summary=Get-Content $summaryPath -Raw|ConvertFrom-Json;$summary|Add-Member ReadabilityRefreshReport (Join-Path $audit 'readability-report.json') -Force;$summary|Add-Member HistoricalEvidenceArchive $zipPath -Force;if($summary.PSObject.Properties.Name -contains 'Reports'){$summary.Reports=@($reportPath,$zipPath)};$summary|ConvertTo-Json -Depth 20|Set-Content $summaryPath -Encoding UTF8}
}
foreach($target in $targets){
    $full=Assert-Contained $target.FullName
    if($target.PSIsContainer){
        if(@(Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint).Count){throw "Nested reparse point found; refusing recursive deletion: $full"}
        Remove-Item -LiteralPath $full -Recurse -Force
    }else{Remove-Item -LiteralPath $full -Force}
}
$after=[long](Get-ChildItem -LiteralPath $root -Recurse -File|Measure-Object Length -Sum).Sum
$report=[pscustomobject]@{Root=$root;BeforeBytes=$before;AfterBytes=$after;ReclaimedBytes=$before-$after;BeforeGiB=[math]::Round($before/1GB,2);AfterGiB=[math]::Round($after/1GB,2);ReclaimedGiB=[math]::Round(($before-$after)/1GB,2);Retained=$keep;ArchivedTextFiles=$archived;ArchivedUncompressedBytes=$bytesArchived;SkippedOversizedOrBudgetedTextFiles=$skipped;RemovedTargets=$targets.Name;OriginalSamplesTouched=$false;Note='Historical intermediate PE files, runtime snapshots and old backup binaries removed after current deliveries were verified. Historical text evidence is compressed in history.zip.'}
$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $audit 'cleanup-report.json') -Encoding UTF8
$report|ConvertTo-Json -Depth 6
