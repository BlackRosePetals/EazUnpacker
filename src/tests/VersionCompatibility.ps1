[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SampleRoot,
    [string]$OutputRoot=(Join-Path $PSScriptRoot ('..\..\artifacts\version-compatibility-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [ValidateRange(30,86400)][int]$TimeoutSeconds=600,
    [switch]$NoBuild,
    [string]$WorkerPath=(Join-Path $PSScriptRoot '..\bin\Release\net48\worker\Unpacker.Worker.exe'),
    [switch]$RequireComplete
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$SampleRoot=(Resolve-Path -LiteralPath $SampleRoot).Path
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
# Keep test products out of the sample corpus, including on repeat runs.
if($OutputRoot.TrimEnd('\') -eq $SampleRoot.TrimEnd('\') -or
   $OutputRoot.StartsWith($SampleRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside SampleRoot.'
}
if(!$NoBuild) {
    & dotnet build (Join-Path $repository 'Unpacker.slnx') -c Release --nologo -v:q
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
}
$worker=[IO.Path]::GetFullPath($WorkerPath)
if(!(Test-Path -LiteralPath $worker)){throw ('Worker not found: '+$worker)}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$samples=@(Get-ChildItem -LiteralPath $SampleRoot -Directory |
    Where-Object Name -Match '^Eaz 20\d\d\.' | Sort-Object Name | ForEach-Object {
        $version=$_.Name
        Get-ChildItem -LiteralPath $_.FullName -File | Where-Object {
            $_.Extension -in '.exe','.dll' -and $_.BaseName -notmatch '(?i)(?:unpacked|cleaned|recovered|compatible|devirt)'
        } | Sort-Object Name | ForEach-Object {
            [pscustomobject]@{Version=$version;File=$_}
        }
    })
if($samples.Count -eq 0){throw 'No original EXE/DLL files found in Eaz version directories.'}
function Quote-Argument([string]$Value) {
    # Windows CommandLineToArgvW escaping, including a final backslash.
    '"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'
}
$rows=@()
foreach($sample in $samples) {
    $file=$sample.File
    $before=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $case=Join-Path $OutputRoot ($sample.Version+'\'+$file.BaseName+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $case -Force | Out-Null
    $arguments=@('pipeline',$file.FullName,$case,'False','False',[string]$TimeoutSeconds,'True')
    $process=Start-Process -FilePath $worker -ArgumentList (($arguments | ForEach-Object {Quote-Argument $_}) -join ' ') `
        -WorkingDirectory (Split-Path $worker) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $case 'stdout.log') -RedirectStandardError (Join-Path $case 'stderr.log')
    $timedOut=!$process.WaitForExit(($TimeoutSeconds+30)*1000)
    if($timedOut) {
        & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $process.Id /T /F | Out-Null
        $process.WaitForExit()
    }
    $process.Refresh()
    $code=if($timedOut){124}else{$process.ExitCode}
    $process.Dispose()
    $after=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if($before -ne $after){throw ('Input changed: '+$file.FullName)}
    $result=$null;$vm=@();$clr=$null;$flow=$null;$preFlow=$null
    if(Test-Path -LiteralPath (Join-Path $case 'result.json')) {
        $result=Get-Content -LiteralPath (Join-Path $case 'result.json') -Raw | ConvertFrom-Json
    }
    if(Test-Path -LiteralPath (Join-Path $case 'vm-methods.json')) {
        $vm=@(Get-Content -LiteralPath (Join-Path $case 'vm-methods.json') -Raw | ConvertFrom-Json | Where-Object {$null -ne $_})
    }
    if(Test-Path -LiteralPath (Join-Path $case 'clr-validation.json')) {
        $clr=Get-Content -LiteralPath (Join-Path $case 'clr-validation.json') -Raw | ConvertFrom-Json
    }
    if(Test-Path -LiteralPath (Join-Path $case 'control-flow-cleanup.json')) {
        $flow=Get-Content -LiteralPath (Join-Path $case 'control-flow-cleanup.json') -Raw | ConvertFrom-Json
    }
    if(Test-Path -LiteralPath (Join-Path $case 'pre-vm-control-flow.json')) {
        $preFlow=Get-Content -LiteralPath (Join-Path $case 'pre-vm-control-flow.json') -Raw | ConvertFrom-Json
    }
    $row=[pscustomobject]@{
        Version=$sample.Version;Input=$file.FullName;InputSha256=$before.ToLowerInvariant();InputUnchanged=$true
        ExitCode=$code;Status=if($result){$result.Status}else{'failed'}
        Output=if($result){$result.Output}else{''};DiagnosticsDirectory=$case
        OutputSha256=if($result.Output -and (Test-Path -LiteralPath $result.Output)){
            (Get-FileHash -LiteralPath $result.Output -Algorithm SHA256).Hash.ToLowerInvariant()
        }else{''}
        VmDetected=if($result.Before){@($result.Before.VirtualizedMethods).Count}else{$null}
        VmReported=$vm.Count;VmRestored=@($vm | Where-Object Restored).Count
        VmRemaining=if($result.After){@($result.After.VirtualizedMethods).Count}else{$null}
        VmFailures=@($vm | Where-Object {!$_.Restored} | Select-Object Token,Method,Failure)
        StringLiteralsBefore=if($result.Before){$result.Before.StringLiterals}else{$null}
        StringLiteralsAfter=if($result.After){$result.After.StringLiterals}else{$null}
        ClrPrepared=if($clr){$clr.Prepared}else{0};ClrFailed=if($clr){$clr.Failed}else{0}
        MissingDependencies=@($clr.MissingDependencies | Where-Object {$_})
        FlowFolded=if($flow){$flow.FoldedBranches}else{0};FlowRemoved=if($flow){$flow.RemovedInstructions}else{0}
        PreVmFlowFolded=if($preFlow){$preFlow.FoldedBranches}else{0};PreVmFlowRemoved=if($preFlow){$preFlow.RemovedInstructions}else{0}
        IncompleteStages=@($result.Stages | Where-Object Status -in 'failed','partial','skipped' | Select-Object Name,Status,Detail)
        Equivalence='Not established: metadata reload and CLR preparation are not business behavior equivalence.'
    }
    $rows+=$row
    # Checkpoint after every sample so interruptions retain all completed evidence.
    ConvertTo-Json -InputObject @($rows) -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputRoot 'report.json') -Encoding UTF8
    Write-Host ($row.Version+': '+$row.Status+'; VM '+$row.VmRestored+'/'+$row.VmDetected+'; CLR '+$row.ClrPrepared+' passed / '+$row.ClrFailed+' failed')
}
$rows | Select-Object Version,Status,VmDetected,VmRestored,VmRemaining,ClrPrepared,ClrFailed | Format-Table -AutoSize
Write-Output ('Evidence: '+(Join-Path $OutputRoot 'report.json'))
if($RequireComplete -and @($rows | Where-Object {$_.ExitCode -ne 0 -or $_.Status -ne 'complete' -or $_.VmRemaining -gt 0}).Count -gt 0){exit 2}
exit 0
