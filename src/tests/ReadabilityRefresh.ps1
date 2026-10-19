param(
    [string[]]$Reports = @('artifacts/aspose-delivery/report.json','artifacts/vba-delivery-final/report.json'),
    [string]$OutputRoot = 'artifacts/readability-generic',
    [ValidateRange(30,3600)][int]$TimeoutSeconds=600
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputRoot)
New-Item $root -ItemType Directory -Force | Out-Null
$worker=[IO.Path]::GetFullPath('src/bin/Release/net48/worker/Unpacker.Worker.exe')
$modern=[IO.Path]::GetFullPath('src/bin/Release/net48/worker/modern/Unpacker.ModernWorker.dll')
function Invoke-BoundedWorker([string]$Executable,[string[]]$Arguments,[string]$Log,[string]$ErrorLog){
    $quoted=@($Arguments | ForEach-Object {'"'+[regex]::Replace([regex]::Replace($_,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'}) -join ' '
    $process=Start-Process -FilePath $Executable -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput $Log -RedirectStandardError $ErrorLog
    if(!$process.WaitForExit($TimeoutSeconds*1000)){
        & "$env:SystemRoot/System32/taskkill.exe" /PID $process.Id /T /F | Out-Null
        $process.Dispose();throw "Readability worker timed out; see $Log"
    }
    $process.Refresh();$code=$process.ExitCode;$process.Dispose();return $code
}
$results=@()
foreach($reportPath in $Reports){
    foreach($row in @(Get-Content $reportPath -Raw | ConvertFrom-Json)){
        $name=[IO.Path]::GetFileNameWithoutExtension($row.Input)
        $dir=Join-Path $root $name
        New-Item $dir -ItemType Directory -Force | Out-Null
        $output=Join-Path $dir ($name+'.dll')
        $exit=Invoke-BoundedWorker $worker @('--names-worker',$row.PublishedOutput,$output,(Join-Path $dir 'rename-map.json')) (Join-Path $dir 'names.log') (Join-Path $dir 'names.err')
        if($exit -ne 0){throw "Naming failed: $name"}
        $old=Get-Content (Join-Path $row.DiagnosticsDirectory 'result.json') -Raw | ConvertFrom-Json
        $env:UNPACKER_DEPENDENCY_PATHS=$row.DeployablePackage
        $arguments=@('--validate-worker',$output,(Join-Path $row.DiagnosticsDirectory 'vm-methods.json'),(Join-Path $dir 'clr-validation.json'),'5',$row.Input)
        if($old.Before.ModernRuntime){$exit=Invoke-BoundedWorker (Get-Command dotnet).Source (@($modern)+$arguments) (Join-Path $dir 'validate.log') (Join-Path $dir 'validate.err')}
        else {$exit=Invoke-BoundedWorker $worker $arguments (Join-Path $dir 'validate.log') (Join-Path $dir 'validate.err')}
        $validation=Get-Content (Join-Path $dir 'clr-validation.json') -Raw | ConvertFrom-Json
        $previousValidation=Get-Content (Join-Path $row.DiagnosticsDirectory 'clr-validation.json') -Raw | ConvertFrom-Json
        $previousFailures=@($previousValidation.Methods | Where-Object {$_.Status -ne 'success'} | ForEach-Object {"$($_.Token)|$($_.Status)|$($_.FailureKind)|$(($_.RootCause -split ' -> ')[-1])"} | Sort-Object)
        $currentFailures=@($validation.Methods | Where-Object {$_.Status -ne 'success'} | ForEach-Object {"$($_.Token)|$($_.Status)|$($_.FailureKind)|$(($_.RootCause -split ' -> ')[-1])"} | Sort-Object)
        $sameFailures=(($previousFailures -join "`n") -eq ($currentFailures -join "`n"))
        $flow=Get-Content (Join-Path $dir 'control-flow-cleanup.json') -Raw | ConvertFrom-Json
        $inlineCount=@(Get-Content (Join-Path $dir 'inline-expressions.json') -Raw|ConvertFrom-Json).Count
        $results += [pscustomobject]@{
            Input=$row.Input;PublishedOutput=$row.PublishedOutput;PreviousSha256=$row.OutputSha256
            Output=$output;OutputSha256=(Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant()
            Report=[IO.Path]::GetFullPath($reportPath);DiagnosticsDirectory=$dir
            ValidationExit=$exit;Complete=$validation.Complete;Prepared=$validation.Prepared;Failed=$validation.Failed
            PreviousPrepared=$row.ClrPrepared;PreviousFailed=$row.ClrFailed
            NoClrCountRegression=($validation.Complete -and $validation.Prepared -eq $row.ClrPrepared -and $validation.Failed -eq $row.ClrFailed)
            SameFailureTokensAndCauses=$sameFailures
            AdditionalFolded=$flow.FoldedBranches;AdditionalRemoved=$flow.RemovedInstructions
            InlinedExpressions=$inlineCount
        }
        $results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'report.json') -Encoding UTF8
        Write-Output "$name : CLR $($validation.Prepared)/$($validation.Expected), failures $($validation.Failed), additional folds $($flow.FoldedBranches)"
    }
}
if(@($results | Where-Object {-not $_.NoClrCountRegression -or -not $_.SameFailureTokensAndCauses}).Count){throw 'Readability refresh has a validation regression; existing deliveries were not changed.'}
