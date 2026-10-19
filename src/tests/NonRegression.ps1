[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SampleRoot,
    [Parameter(Mandatory=$true)][string]$BaselineWorker,
    [string]$CurrentWorker=(Join-Path $PSScriptRoot '..\bin\Release\net48\worker\Unpacker.Worker.exe'),
    [string]$OutputRoot=(Join-Path $PSScriptRoot '..\..\artifacts\non-regression'),
    [ValidateRange(60,86400)][int]$TimeoutSeconds=900
)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$SampleRoot=(Resolve-Path -LiteralPath $SampleRoot).Path
$BaselineWorker=(Resolve-Path -LiteralPath $BaselineWorker).Path
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if($OutputRoot.TrimEnd('\') -eq $SampleRoot.TrimEnd('\') -or
    $OutputRoot.StartsWith($SampleRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'OutputRoot must be outside SampleRoot.'}
$worker=(Resolve-Path -LiteralPath $CurrentWorker).Path
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$samples=@(Get-ChildItem -LiteralPath $SampleRoot -Recurse -File | Where-Object {
    $relative=$_.FullName.Substring($SampleRoot.Length).TrimStart('\')
    $_.Extension -in '.exe','.dll' -and $relative -notmatch '^Eaz 20\d\d\.' -and
    $relative -notmatch '(?i)\\(bin|obj)\\|unpacked|[-_]cleaned|[-_]recovered|[-_]devirt|[-_]compatible'
} | Sort-Object FullName)
if(!$samples.Count){throw 'No remaining original sample assemblies found.'}
function Quote-Argument([string]$Value) {'"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'}
function Invoke-Pipeline([string]$Executable,[string]$InputPath,[string]$Case) {
    New-Item -ItemType Directory -Path $Case -Force | Out-Null
    $arguments=@('pipeline',$InputPath,$Case,'False','False',[string]$TimeoutSeconds,'True')
    $process=Start-Process -FilePath $Executable -ArgumentList (($arguments|ForEach-Object {Quote-Argument $_}) -join ' ') `
        -WorkingDirectory (Split-Path $Executable) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Case 'stdout.log') -RedirectStandardError (Join-Path $Case 'stderr.log')
    $timeout=!$process.WaitForExit(($TimeoutSeconds+30)*1000)
    if($timeout){& (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $process.Id /T /F | Out-Null;$process.WaitForExit()}
    $process.Refresh();$code=if($timeout){124}else{$process.ExitCode};$process.Dispose()
    $result=$null
    if(Test-Path -LiteralPath (Join-Path $Case 'result.json')){$result=Get-Content -LiteralPath (Join-Path $Case 'result.json') -Raw|ConvertFrom-Json}
    [pscustomobject]@{ExitCode=$code;Result=$result;Case=$Case}
}
Add-Type -Path (Join-Path (Split-Path $worker) 'dnlib.dll')
function Resource-Digests([string]$Assembly) {
    if(!$Assembly -or !(Test-Path -LiteralPath $Assembly)){return @()}
    $module=[dnlib.DotNet.ModuleDefMD]::Load($Assembly)
    try {
        @($module.Resources | Where-Object {$_ -is [dnlib.DotNet.EmbeddedResource]} | ForEach-Object {
            $sha=[Security.Cryptography.SHA256]::Create()
            try{[BitConverter]::ToString($sha.ComputeHash($_.CreateReader().ToArray())).Replace('-','').ToLowerInvariant()}
            finally{$sha.Dispose()}
        } | Sort-Object -Unique)
    } finally{$module.Dispose()}
}
$rows=@()
foreach($file in $samples) {
    $relative=$file.FullName.Substring($SampleRoot.Length).TrimStart('\')
    $before=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $case=Join-Path $OutputRoot ($relative.Replace('\','_')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    Write-Host ('Comparing '+$relative)
    $baseline=Invoke-Pipeline $BaselineWorker $file.FullName (Join-Path $case 'baseline')
    $current=Invoke-Pipeline $worker $file.FullName (Join-Path $case 'current')
    $after=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if($before -ne $after){throw ('Sample changed: '+$file.FullName)}
    $old=$baseline.Result;$new=$current.Result
    $regressions=@()
    $oldVm=($old.Stages|Where-Object Name -eq 'VM'|Measure-Object Changes -Sum).Sum
    $newVm=($new.Stages|Where-Object Name -eq 'VM'|Measure-Object Changes -Sum).Sum
    if($null -eq $oldVm){$oldVm=0};if($null -eq $newVm){$newVm=0}
    $oldClr=$null;$newClr=$null
    if(Test-Path -LiteralPath (Join-Path $baseline.Case 'clr-validation.json')){$oldClr=Get-Content (Join-Path $baseline.Case 'clr-validation.json') -Raw|ConvertFrom-Json}
    if(Test-Path -LiteralPath (Join-Path $current.Case 'clr-validation.json')){$newClr=Get-Content (Join-Path $current.Case 'clr-validation.json') -Raw|ConvertFrom-Json}
    $previousPasses=@($oldClr.Methods|Where-Object Status -eq 'success'|Select-Object -ExpandProperty Token)
    $currentPasses=@($newClr.Methods|Where-Object Status -eq 'success'|Select-Object -ExpandProperty Token)
    $lostPasses=@($previousPasses|Where-Object {$_ -notin $currentPasses})
    if($lostPasses.Count){$regressions+='Previously CLR-valid VM methods no longer validate'}
    if(!$new.Output -or !(Test-Path -LiteralPath $new.Output)){$regressions+='No current output'}
    if($old.Status -eq 'complete' -and $new.Status -ne 'complete'){$regressions+='Complete baseline became incomplete'}
    if($newVm -lt $oldVm){$regressions+='Fewer restored VM methods'}
    if($old.After -and $new.After -and @($new.After.VirtualizedMethods).Count -gt @($old.After.VirtualizedMethods).Count){$regressions+='More residual VM methods'}
    if($new.After -and $old.After -and ($new.After.Types -ne $old.After.Types -or $new.After.Methods -ne $old.After.Methods)){$regressions+='Metadata definition count changed'}
    $previousResources=@(Resource-Digests $old.Output);$currentResources=@(Resource-Digests $new.Output)
    $lostResources=@($previousResources|Where-Object {$_ -notin $currentResources})
    if($lostResources.Count){$regressions+='Previously recovered resource bytes disappeared'}
    $row=[pscustomobject]@{
        Sample=$relative;Input=$file.FullName;InputSha256=$before.ToLowerInvariant();InputUnchanged=$true
        BaselineStatus=$old.Status;Status=$new.Status;BaselineExitCode=$baseline.ExitCode;ExitCode=$current.ExitCode
        BaselineRestoredVm=$oldVm;RestoredVm=$newVm;ResidualVm=if($new.After){@($new.After.VirtualizedMethods).Count}else{$null}
        BaselineClrPrepared=$oldClr.Prepared;ClrPrepared=$newClr.Prepared;ClrFailed=$newClr.Failed;LostClrSuccessTokens=$lostPasses
        BaselineStrings=$old.After.StringLiterals;Strings=$new.After.StringLiterals
        BaselineResources=$previousResources.Count;Resources=$currentResources.Count;MissingResourceDigests=$lostResources
        Regressions=$regressions;Output=$new.Output;BaselineDirectory=$baseline.Case;DiagnosticsDirectory=$current.Case
        Limitations='Bounded structural/JIT regression, not full application equivalence; see separate behavior/source tests.'
    }
    $rows+=$row
    ConvertTo-Json -InputObject @($rows) -Depth 15|Set-Content -LiteralPath (Join-Path $OutputRoot 'report.json') -Encoding UTF8
    Write-Host ($relative+': baseline '+$old.Status+' VM '+$oldVm+' -> current '+$new.Status+' VM '+$newVm+'; regression count '+$regressions.Count)
}
$rows|Select-Object Sample,BaselineStatus,Status,BaselineRestoredVm,RestoredVm,ResidualVm,@{n='Regressions';e={@($_.Regressions).Count}}|Format-Table -AutoSize
Write-Output ('Evidence: '+(Join-Path $OutputRoot 'report.json'))
if(@($rows|Where-Object {@($_.Regressions).Count -gt 0}).Count){exit 2}
exit 0
