[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SampleRoot,
    [string]$OutputRoot=(Join-Path $PSScriptRoot ('..\..\artifacts\aspose-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [ValidateRange(30,86400)][int]$TimeoutSeconds=600,
    [string]$Filter='*.dll',
    [string[]]$IncludeNames=@(),
    [string]$DependencyRoot='',
    [ValidateRange(1,16)][int]$ShardCount=1,
    [ValidateRange(0,15)][int]$ShardIndex=0,
    [string]$WorkerPath=(Join-Path $PSScriptRoot '..\bin\Release\net48\worker\Unpacker.Worker.exe'),
    [switch]$NoBuild,
    [switch]$RequireComplete
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$SampleRoot=(Resolve-Path -LiteralPath $SampleRoot).Path.TrimEnd('\')
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if($OutputRoot -eq $SampleRoot -or $OutputRoot.StartsWith($SampleRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside SampleRoot (never include outputs in the original corpus).'
}
if(!$NoBuild) {
    & dotnet build (Join-Path $repository 'Unpacker.slnx') -c Release --nologo -v:q
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
}
$WorkerPath=[IO.Path]::GetFullPath($WorkerPath)
if(!(Test-Path -LiteralPath $WorkerPath)){throw ('Worker not found: '+$WorkerPath)}
$samples=@(Get-ChildItem -LiteralPath $SampleRoot -Recurse -File -Filter $Filter |
    Where-Object {$_.Extension -eq '.dll' -and $_.BaseName -notmatch '(?i)unpacked$' -and ($IncludeNames.Count -eq 0 -or $_.Name -in $IncludeNames)} | Sort-Object FullName)
if($ShardIndex -ge $ShardCount){throw 'ShardIndex must be less than ShardCount.'}
$samples=@(for($i=0;$i -lt $samples.Count;$i++){if(($i % $ShardCount) -eq $ShardIndex){$samples[$i]}})
if($samples.Count -eq 0){throw 'No original DLLs found (unpacked suffixes excluded).'}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
# Snapshot the complete backend so rebuilding the repository during a long
# corpus run cannot silently mix different engine versions in one report.
$runtime=Join-Path $OutputRoot 'runtime'
if(Test-Path -LiteralPath $runtime){throw 'Use a fresh OutputRoot; runtime snapshot already exists.'}
New-Item -ItemType Directory -Path $runtime | Out-Null
Copy-Item -Path (Join-Path (Split-Path $WorkerPath) '*') -Destination $runtime -Recurse
Get-ChildItem -LiteralPath $runtime -Recurse -File | Where-Object Extension -in '.exe','.dll','.json','.txt','.config' |
    ForEach-Object {[pscustomobject]@{File=$_.FullName.Substring($runtime.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputRoot 'runtime-manifest.json') -Encoding UTF8
$worker=Join-Path $runtime (Split-Path $WorkerPath -Leaf)
function Quote-Argument([string]$Value) {
    '"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'
}
function Read-Json([string]$Path) {
    if(Test-Path -LiteralPath $Path){Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json}
}
$rows=@()
foreach($file in $samples) {
    $relative=$file.FullName.Substring($SampleRoot.Length+1)
    $case=Join-Path (Join-Path $OutputRoot 'cases') ([IO.Path]::ChangeExtension($relative,$null).TrimEnd('.'))
    New-Item -ItemType Directory -Path $case -Force | Out-Null
    $dependencyPath=if($DependencyRoot){Join-Path ([IO.Path]::GetFullPath($DependencyRoot)) $file.BaseName}else{''}
    if($dependencyPath -and (Test-Path -LiteralPath $dependencyPath)) {
        Copy-Item -Path (Join-Path $dependencyPath '*') -Destination $case -Recurse -Force
    }
    $dependencyFiles=@(if($dependencyPath -and (Test-Path -LiteralPath $dependencyPath)){
        Get-ChildItem -LiteralPath $dependencyPath -Recurse -File|ForEach-Object{
            $relativeDependency=$_.FullName.Substring($dependencyPath.Length+1)
            [pscustomobject]@{File=$relativeDependency;Sha256=(Get-FileHash -LiteralPath (Join-Path $case $relativeDependency) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    })
    $before=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $arguments=@('pipeline',$file.FullName,$case,'False','False',[string]$TimeoutSeconds,'True')
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $oldDependencyPaths=$env:UNPACKER_DEPENDENCY_PATHS
    try {
        if($dependencyPath -and (Test-Path -LiteralPath $dependencyPath)){
            $env:UNPACKER_DEPENDENCY_PATHS=$dependencyPath+[IO.Path]::PathSeparator+$oldDependencyPaths
        }
        $process=Start-Process -FilePath $worker -ArgumentList (($arguments | ForEach-Object {Quote-Argument $_}) -join ' ') `
            -WorkingDirectory $runtime -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $case 'stdout.log') -RedirectStandardError (Join-Path $case 'stderr.log')
    }
    finally {$env:UNPACKER_DEPENDENCY_PATHS=$oldDependencyPaths}
    $timedOut=!$process.WaitForExit(($TimeoutSeconds+30)*1000)
    if($timedOut) {
        & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $process.Id /T /F | Out-Null
        $process.WaitForExit()
    }
    $process.Refresh();$code=if($timedOut){124}else{$process.ExitCode};$process.Dispose();$watch.Stop()
    $after=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if($before -ne $after){throw ('Input changed: '+$file.FullName)}
    $result=Read-Json (Join-Path $case 'result.json')
    $vm=@(Read-Json (Join-Path $case 'vm-methods.json') | Where-Object {$null -ne $_})
    $clr=Read-Json (Join-Path $case 'clr-validation.json')
    $flow=Read-Json (Join-Path $case 'control-flow-cleanup.json')
    $pre=Read-Json (Join-Path $case 'pre-vm-control-flow.json')
    $proxies=@(Read-Json (Join-Path $case 'safe-proxies.json') | Where-Object {$null -ne $_})
    $published='';$outputHash=''
    if($result.Output -and (Test-Path -LiteralPath $result.Output)) {
        $published=Join-Path (Join-Path $OutputRoot 'unpacked') ([IO.Path]::ChangeExtension($relative,$null).TrimEnd('.')+'_unpacked.dll')
        New-Item -ItemType Directory -Path (Split-Path $published) -Force | Out-Null
        Copy-Item -LiteralPath $result.Output -Destination $published
        if(Test-Path -LiteralPath ($result.Output+'.config')){Copy-Item -LiteralPath ($result.Output+'.config') -Destination ($published+'.config')}
        $pdb=[IO.Path]::ChangeExtension($result.Output,'.pdb')
        if(Test-Path -LiteralPath $pdb){
            $publishedPdb=[IO.Path]::ChangeExtension($published,'.pdb')
            Copy-Item -LiteralPath $pdb -Destination $publishedPdb
            $symbols=Start-Process -FilePath $worker -ArgumentList ('--publish-symbols '+(Quote-Argument $published)+' '+(Quote-Argument $publishedPdb)) -WindowStyle Hidden -PassThru -Wait
            if($symbols.ExitCode -ne 0){throw 'CodeView symbol publication failed.'}
        }
        $outputHash=(Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $row=[pscustomobject]@{
        Input=$file.FullName;InputSha256=$before;InputUnchanged=$true;ExitCode=$code
        ElapsedSeconds=[Math]::Round($watch.Elapsed.TotalSeconds,2)
        DependencyDirectory=if($dependencyPath -and (Test-Path -LiteralPath $dependencyPath)){$dependencyPath}else{''}
        DependencyFiles=$dependencyFiles
        Status=if($result){$result.Status}else{'failed'};PublishedOutput=$published;OutputSha256=$outputHash;DiagnosticsDirectory=$case
        VmDetected=if($result.Before){@($result.Before.VirtualizedMethods).Count}else{$null}
        VmReported=$vm.Count;VmRestored=@($vm | Where-Object Restored).Count
        VmRestoredHosts=@($vm | Where-Object Restored | Select-Object -ExpandProperty Token -Unique).Count
        VmRemaining=if($result.After){@($result.After.VirtualizedMethods).Count}else{$null}
        VmFailures=@($vm | Where-Object {!$_.Restored} | Select-Object Token,Method,Failure)
        ClrPrepared=if($clr){$clr.Prepared}else{0};ClrFailed=if($clr){$clr.Failed}else{0}
        StringLiteralsBefore=if($result.Before){$result.Before.StringLiterals}else{$null}
        StringLiteralsAfter=if($result.After){$result.After.StringLiterals}else{$null}
        ProxyChanges=$proxies.Count
        FlowFolded=($flow.FoldedBranches+$pre.FoldedBranches);FlowRemoved=($flow.RemovedInstructions+$pre.RemovedInstructions)
        IncompleteStages=@($result.Stages | Where-Object Status -in 'failed','partial','skipped' | Select-Object Name,Status,Detail)
        ValidationScope='Metadata reload and recovered-method CLR preparation; not proof of complete deobfuscation or business equivalence. No name-based dead-type deletion.'
    }
    $rows+=$row
    ConvertTo-Json -InputObject @($rows) -Depth 40 | Set-Content -LiteralPath (Join-Path $OutputRoot 'report.json') -Encoding UTF8
    $rows | Select-Object Input,Status,ExitCode,ElapsedSeconds,VmDetected,VmRestored,VmRemaining,ClrPrepared,ClrFailed,PublishedOutput |
        Export-Csv -LiteralPath (Join-Path $OutputRoot 'report.csv') -NoTypeInformation -Encoding UTF8
    Write-Host ($file.Name+': '+$row.Status+'; VM '+$row.VmRestored+'/'+$row.VmDetected+'; remaining '+$row.VmRemaining+'; '+$row.ElapsedSeconds+'s')
}
Write-Output ('Evidence: '+(Join-Path $OutputRoot 'report.json'))
if($RequireComplete -and @($rows | Where-Object {$_.ExitCode -ne 0 -or $_.Status -ne 'complete' -or $_.VmRemaining -gt 0}).Count -gt 0){exit 2}
exit 0
