param(
    [Parameter(Mandatory=$true)][string]$InputAssembly,
    [string]$Tool=(Join-Path $PSScriptRoot '..\bin\Release\net48\Unpacker.exe')
)
$ErrorActionPreference='Stop'
$Tool=[IO.Path]::GetFullPath($Tool);$InputAssembly=[IO.Path]::GetFullPath($InputAssembly)
$program=Split-Path $Tool;$temp=Join-Path $program 'temp'
$root=Join-Path $temp ('option-tests-'+[guid]::NewGuid().ToString('N'));New-Item $root -ItemType Directory -Force|Out-Null
$output=Join-Path $root ('unpacked'+[IO.Path]::GetExtension($InputAssembly))
$sha=[Security.Cryptography.SHA256]::Create()
try{$key=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
$latest=Join-Path $temp ('work/'+$key+'/latest.json');$originalHash=(Get-FileHash $InputAssembly -Algorithm SHA256).Hash
$results=@()
function Run([string]$name,[bool]$validate,[bool]$leading=$false){
    $arguments=@('unpack',$InputAssembly,$output)
    if($validate){$arguments=if($leading){@('unpack','--validate-clr',$InputAssembly,$output)}else{$arguments+@('--validate-clr')}}
    & $Tool @arguments > (Join-Path $root ($name+'.log')) 2> (Join-Path $root ($name+'.err'))
    $code=$LASTEXITCODE
    if($code -notin 0,2 -or !(Test-Path $output)){throw "No recovered output: $name, exit $code"}
    $result=Get-Content $latest -Raw|ConvertFrom-Json
    if($result.ClrValidationRequested -ne $validate){throw 'CLR option did not reach the pipeline.'}
    if(!$result.DiagnosticsDirectory.StartsWith($temp+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Work directory is not beside the executable.'}
    $clr=Join-Path $result.DiagnosticsDirectory 'clr-validation.json'
    $stages=@($result.Stages|Where-Object {$_.Name -in 'CLRValidation','RebuiltCLRValidation'})
    if(!$validate -and ((Test-Path $clr) -or $stages.Count)){throw 'Default mode executed a CLR check.'}
    $hasRecovered=@($result.Stages|Where-Object {$_.Name -eq 'VM' -and $_.Changes -gt 0}).Count -gt 0
    if($validate -and $hasRecovered -and (!(Test-Path $clr) -or !$stages.Count)){throw 'Explicit CLR request did not create its report.'}
    $stats=Get-Content (Join-Path $result.DiagnosticsDirectory recovery-statistics.json) -Raw|ConvertFrom-Json
    if($stats.ClrValidationRequested -ne $validate -or (!$validate -and $null -ne $stats.ClrPrepared)){throw 'Summary misrepresents an unrequested check as passed.'}
    [pscustomobject]@{Name=$name;ExitCode=$code;Requested=$validate;ClrReport=(Test-Path $clr);DiagnosticsDirectory=$result.DiagnosticsDirectory}
}
$foreign=Join-Path $root foreign;New-Item $foreign -ItemType Directory|Out-Null
Push-Location $foreign
try{
    $results+=Run 'default' $false
    $results+=Run 'explicit-trailing' $true
    $results+=Run 'explicit-leading' $true $true
    $results+=Run 'default-after-explicit' $false
    $analysis=& $Tool analyzer $output|ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $analysis.DiagnosticsDirectory -ne $results[-1].DiagnosticsDirectory){throw 'Analyzer did not find the program-local work report.'}
    $beforeDuplicate=(Get-FileHash $output -Algorithm SHA256).Hash
    & $Tool unpack $InputAssembly $output --validate-clr --validate-clr > (Join-Path $root duplicate.log) 2>&1
    if($LASTEXITCODE -ne 1){throw 'Duplicate CLR option accepted.'}
    if((Get-FileHash $output -Algorithm SHA256).Hash -ne $beforeDuplicate){throw 'Invalid options changed an existing output.'}
    if(Test-Path (Join-Path $foreign temp)){throw 'Temporary files were placed in the shell working directory.'}
}finally{Pop-Location}
if((Get-FileHash $InputAssembly -Algorithm SHA256).Hash -ne $originalHash){throw 'Original input changed.'}
$results|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root report.json) -Encoding UTF8
Write-Output "PASS: default CLR checks disabled; explicit leading/trailing requests honored; repeated default has no stale validation; analyzer and workers use program-local temp. Evidence: $root"
exit 0
