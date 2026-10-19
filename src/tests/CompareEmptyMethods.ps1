param(
    [Parameter(Mandatory=$true)][string]$Original,
    [Parameter(Mandatory=$true)][string]$Recovered,
    [Parameter(Mandatory=$true)][string]$Report,
    [string]$Devirtualized,
    [string]$VmReport,
    [string]$Dnlib=(Join-Path $PSScriptRoot '..\bin\Release\net48\dnlib.dll')
)
$ErrorActionPreference='Stop'
Add-Type -Path ([IO.Path]::GetFullPath($Dnlib))
function Is-Empty($method) {
    if(!$method.HasBody -or $method.MethodSig.RetType.FullName -ne 'System.Void'){return $false}
    $ops=@($method.Body.Instructions | Where-Object {$_.OpCode.Code.ToString() -ne 'Nop'})
    return $ops.Count -eq 1 -and $ops[0].OpCode.Code.ToString() -eq 'Ret'
}
function Describe($method) {
    if(!$method){return $null}
    [ordered]@{Name=$method.FullName;Rva=('0x{0:X8}' -f [uint32]$method.RVA);HasBody=$method.HasBody
        InstructionCount=$(if($method.HasBody){$method.Body.Instructions.Count}else{0})
        IL=$(if($method.HasBody){@($method.Body.Instructions|ForEach-Object {$_.ToString()})}else{@()})}
}
$a=[dnlib.DotNet.ModuleDefMD]::Load([IO.Path]::GetFullPath($Original))
$b=[dnlib.DotNet.ModuleDefMD]::Load([IO.Path]::GetFullPath($Recovered))
$stage=$null
try {
    # A token match is only meaningful for the token-preserving recovery pipeline.
    if($a.Mvid -ne $b.Mvid){throw 'Module identities differ; token-based comparison is not safe.'}
    $originalByToken=@{}; $originalMethods=@($a.GetTypes()|ForEach-Object {$_.Methods})
    foreach($m in $originalMethods){$originalByToken[[uint32]$m.MDToken.Raw]=$m}
    $recoveredMethods=@($b.GetTypes()|ForEach-Object {$_.Methods})
    $empty=@($recoveredMethods|Where-Object {Is-Empty $_})
    $vm=@()
    if($Devirtualized){$stage=[dnlib.DotNet.ModuleDefMD]::Load([IO.Path]::GetFullPath($Devirtualized));if($stage.Mvid -ne $a.Mvid){throw 'VM-stage module identity differs.'}}
    if($VmReport){$vm=@(Get-Content -LiteralPath $VmReport -Raw|ConvertFrom-Json)}
    $rows=@(foreach($m in $empty){
        $old=$originalByToken[[uint32]$m.MDToken.Raw]
        if($old -and ($old.MethodSig.Params.Count -ne $m.MethodSig.Params.Count -or $old.MethodSig.RetType.ElementType -ne $m.MethodSig.RetType.ElementType)){
            throw ('Method token/signature mismatch: '+$m.MDToken)
        }
        $token='0x{0:X8}' -f $m.MDToken.Raw
        $decoded=$null;if($stage){$decoded=$stage.ResolveToken([uint32]$m.MDToken.Raw)}
        [ordered]@{Token=$token;TypeToken=('0x{0:X8}' -f $m.DeclaringType.MDToken.Raw)
            WasAlreadyEmpty=$(if($old){Is-Empty $old}else{$false});AddedMethod=($null -eq $old)
            EmptyBeforeReadabilityCleanup=$(if($decoded){Is-Empty $decoded}else{$null})
            VmRecovery=@($vm|Where-Object Token -eq $token)
            Original=(Describe $old);Recovered=(Describe $m)}
    })
    $result=[ordered]@{Original=[IO.Path]::GetFullPath($Original);Recovered=[IO.Path]::GetFullPath($Recovered)
        OriginalSha256=(Get-FileHash -LiteralPath $Original).Hash;RecoveredSha256=(Get-FileHash -LiteralPath $Recovered).Hash
        Mvid=$a.Mvid.ToString();OriginalMethodCount=$originalMethods.Count;RecoveredMethodCount=$recoveredMethods.Count
        OriginalEmptyVoidMethods=@($originalMethods|Where-Object {Is-Empty $_}).Count;RecoveredEmptyVoidMethods=$empty.Count
        AlreadyEmpty=@($rows|Where-Object {$_.WasAlreadyEmpty}).Count
        BecameEmpty=@($rows|Where-Object {!$_.WasAlreadyEmpty -and !$_.AddedMethod}).Count
        AddedEmpty=@($rows|Where-Object {$_.AddedMethod}).Count;Methods=$rows}
    $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $Report -Encoding UTF8
    $result.Remove('Methods');[pscustomobject]$result|Format-List
} finally {if($stage){$stage.Dispose()};$a.Dispose();$b.Dispose()}
