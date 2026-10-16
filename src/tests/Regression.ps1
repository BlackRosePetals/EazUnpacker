param(
    [string]$SampleRoot = (Join-Path $PSScriptRoot '..\..\样本'),
    [switch]$CheckRepeat,
    [switch]$CheckKeyRejection,
    [switch]$IncludeAva,
    [string]$ResultRoot = (Join-Path $PSScriptRoot '..\test-results\final-single-file')
)
$ErrorActionPreference = 'Stop'
function Get-WorkReport([string]$outputFile) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $key=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($outputFile).ToUpperInvariant()))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Unpacker\work\'+$key+'\latest.json')
}
$exe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Release\net48\Unpacker.exe'))
if (!(Test-Path -LiteralPath $exe)) { throw 'Unpacker.exe 尚未构建' }
$resultRoot = [IO.Path]::GetFullPath($ResultRoot)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$samples = @(
    @{ Relative='EAZ202101\ggggg.exe'; Name='ggggg'; Output='ggggg.exe'; Vm=32; Regions=2 },
    @{ Relative='EAZ202102\crk2.exe'; Name='crk2'; Output='crk2.exe'; Vm=12; Regions=0 },
    @{ Relative='EAZ2025_01\WindowsFormsApplication37.exe'; Name='WindowsFormsApplication37'; Output='WindowsFormsApplication37.exe'; Vm=2; Regions=0 },
    @{ Relative='EAZ2025_01\WindowsFormsApplication41.exe'; Name='WindowsFormsApplication41'; Output='WindowsFormsApplication41.exe'; Vm=53; Regions=4 },
    @{ Relative='EAZ_UnknownVer\SKUFSLIUNX-033.exe'; Name='SKUFSLIUNX-033'; Output='SKUF_unpacked.exe'; Vm=0; Regions=0 }
)
if ($IncludeAva) { $samples += @{ Relative='EAZ202602\AvaKiller (2).exe'; Name='AvaKiller'; Output='AvaKiller.exe'; Vm=322; Regions=-1 } }
$reports = @()
$hashes = @()
foreach ($sample in $samples) {
    $inputFile = [IO.Path]::GetFullPath((Join-Path $SampleRoot $sample.Relative))
    $inputHash = (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash
    $outputFile = Join-Path (Join-Path $resultRoot $sample.Name) $sample.Output
    & $exe analyzer $inputFile | Set-Content -LiteralPath (Join-Path $resultRoot ($sample.Name+'.analysis.json')) -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw ('静态分析失败：'+$sample.Name) }
    & $exe unpack $inputFile $outputFile
    if ($LASTEXITCODE -ne 0) { throw ('完整恢复或 C# 重构建失败：'+$sample.Name) }
    $item = Get-Content -LiteralPath (Get-WorkReport $outputFile) -Raw | ConvertFrom-Json
    $expectedFiles=if($item.RuntimeConfig){2}else{1}
    if (@(Get-ChildItem -LiteralPath (Split-Path $outputFile) -Force).Count -ne $expectedFiles) { throw '目标目录产生了程序和运行配置以外的内容' }
    if($item.RuntimeConfig -and (!(Test-Path -LiteralPath $item.RuntimeConfig) -or ([xml](Get-Content $item.RuntimeConfig -Raw)).configuration.runtime.bypassTrustedAppStrongNames.enabled -ne 'true')) { throw '缺少强名称运行兼容配置' }
    if ($item.Status -ne 'complete' -or $item.Output -ne $outputFile -or !(Test-Path -LiteralPath $outputFile)) { throw '输出状态或文件路径不符' }
    if (!(Test-Path -LiteralPath $item.SourceProject) -or !(Test-Path -LiteralPath $item.RebuiltAssembly)) { throw '缺少完整 C# 工程或实际重构建产物' }
    $build = Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'source-build.json') -Raw | ConvertFrom-Json
    if ($build.ExitCode -ne 0 -or $build.DecompilerErrors -ne 0) { throw '完整反编译或编译检查失败' }
    $resourceChecks=@(Get-Content -LiteralPath (Join-Path (Split-Path $item.SourceProject) 'resource-validation.json') -Raw|ConvertFrom-Json)
    if(@($resourceChecks|Where-Object {-not $_.Equal}).Count -ne 0){throw 'C# 重编译资源名称或原始字节不符'}
    $names = @(Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'rename-map.json') -Raw | ConvertFrom-Json)
    if (@($names | Where-Object Kind -eq 'Type').Count -eq 0 -or @($names | Where-Object { $_.Kind -eq 'Local' -and $_.Type }).Count -eq 0) { throw '缺少类型命名或带真实类型的局部变量映射' }
    if ($item.After.VirtualizedMethods.Count -ne 0) { throw ('存在 VM 残留：'+$sample.Name) }
    $cleanAnalysis=& $exe analyzer $outputFile | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $cleanAnalysis.SourceProject -ne $item.SourceProject) { throw '输出分析或源码入口不符' }
    $mainAnalysis=@($cleanAnalysis.Assemblies | Where-Object File -ne 'System.IO.Ports.dll')
    if(@($mainAnalysis | Where-Object { $_.ObfuscatedTypes -ne 0 -or $_.ObfuscatedMethods -ne 0 -or $_.ObfuscatedFields -ne 0 }).Count -ne 0) { throw '存在未恢复的随机元数据名称' }
    if ($sample.Vm -gt 0) {
        $vm = $item.Stages | Where-Object Name -eq 'VM'
        if ($vm.Changes -ne $sample.Vm -or $item.Before.VirtualizedMethods.Count -ne $sample.Vm) { throw 'VM 数量不符' }
        $methods = @(Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'vm-methods.json') -Raw | ConvertFrom-Json)
        if (@($methods | Where-Object {-not $_.Restored}).Count -ne 0) { throw '存在未恢复方法' }
        $clr = Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'clr-validation.json') -Raw | ConvertFrom-Json
        if ($clr.Prepared -ne $sample.Vm -or $clr.Failed -ne 0 -or $clr.Skipped -ne 0) { throw '恢复方法 CLR 编译失败' }
        $rebuiltClr = Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'rebuilt-clr-validation.json') -Raw | ConvertFrom-Json
        if ($rebuiltClr.Prepared -ne $sample.Vm -or $rebuiltClr.Failed -ne 0 -or $rebuiltClr.Skipped -ne 0) { throw '重编译 C# 方法 CLR 编译失败' }
        $mapped = @(Get-Content -LiteralPath (Join-Path $item.DiagnosticsDirectory 'rebuilt-vm-methods.json') -Raw | ConvertFrom-Json)
        if ($mapped.Count -ne $sample.Vm -or @($mapped | Where-Object { -not $_.Restored -or -not $_.Signature -or -not $_.Token }).Count -ne 0) { throw '重编译 VM 方法签名映射不完整' }
        if ($sample.Regions -ge 0 -and ($methods | Measure-Object EncryptedRegions -Sum).Sum -ne $sample.Regions) { throw '加密区段数量不符' }
    }
    if ($sample.Name -eq 'crk2' -and $item.After.Resources -ne 6) { throw '资源恢复数量不符' }
    if ($sample.Name -eq 'SKUFSLIUNX-033') {
        # The naming stage removes statically unreachable IL, including ldstr.
        # Check all decoded table entries before cleanup, then compare the
        # published payload's surviving literals with the final inspection.
        if (($item.Stages | Where-Object Name -eq 'RvaStrings').Changes -ne 7830 -or $item.After.StringLiterals -le 0 -or ($item.Stages | Where-Object Name -eq 'Bundle').Changes -ne 4) { throw '单文件或字符串恢复数量不符' }
        $packed = & $exe analyzer $outputFile | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or !$packed.Bundle -or @($packed.Payloads).Count -ne 4 -or
            @($packed.Assemblies | Where-Object { $_.File -eq 'SKUFStools.dll' -and $_.Info.StringLiterals -eq $item.After.StringLiterals }).Count -ne 1) { throw '单文件 EXE 的载荷或恢复字符串不符' }
        foreach ($file in @('SKUFStools.runtimeconfig.json','SKUFStools.deps.json','System.IO.Ports.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path (Join-Path $item.DiagnosticsDirectory 'bundle') $file))) { throw ('缺少缓存依赖：'+$file) }
        }
    }
    if ((Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $inputHash) { throw '原样本发生变化' }
    $reports += $item
    $hashes += [PSCustomObject]@{ Input=$inputFile; InputSha256=$inputHash; Output=$outputFile; OutputSha256=(Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash }
}
$reports | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $resultRoot 'report.json') -Encoding UTF8
$fixture = Join-Path $PSScriptRoot 'VmBehavior\bin\Release\net48\VmBehavior.exe'
function Invoke-Probe([string[]]$probeArguments,[string]$probeExe=$fixture) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $probeExe
    $start.Arguments = ($probeArguments | ForEach-Object { '"'+([regex]::Replace([regex]::Replace($_,'(\\*)"','$1$1\"'),'(\\+)$','$1$1'))+'"' }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(45000)) {
            & (Join-Path ([Environment]::SystemDirectory) 'taskkill.exe') /PID $process.Id /T /F | Out-Null
            throw ('行为检查超时，已终止进程树：'+$probeArguments[0])
        }
        $stdout=$process.StandardOutput.ReadToEnd()
        $stderr=$process.StandardError.ReadToEnd()
        if ($stdout) { Write-Output $stdout.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw ('行为检查失败：'+$probeArguments[0]+[Environment]::NewLine+$stderr) }
    } finally { $process.Dispose() }
}
function Test-FieldData($item) {
    $candidate=Get-ChildItem -LiteralPath $item.DiagnosticsDirectory -Filter '*-recovered.*' | Where-Object Extension -in '.exe','.dll' | Select-Object -First 1
    $dataReport=Join-Path (Split-Path $item.SourceProject) 'field-data.json'
    $runtimeReport=Join-Path $item.DiagnosticsDirectory 'rebuilt-field-runtime.json'
    if(Test-Path -LiteralPath $runtimeReport){Remove-Item -LiteralPath $runtimeReport}
    $dataFixture=if($item.After.ModernRuntime){Join-Path $PSScriptRoot 'VmBehavior\bin\Release\net8.0-windows\VmBehavior.exe'}else{$fixture}
    Invoke-Probe -probeArguments @('check-field-data',$candidate.FullName,$item.RebuiltAssembly,$dataReport,$runtimeReport) -probeExe $dataFixture
    if (!(Test-Path -LiteralPath $runtimeReport)) { throw '字段检查进程提前退出，未生成结果' }
    $data=@(Get-Content -LiteralPath $dataReport -Raw | ConvertFrom-Json | Where-Object Exported)
    $runtime=Get-Content -LiteralPath $runtimeReport -Raw | ConvertFrom-Json
    if ($runtime.Status -ne 'success' -or $runtime.Fields -ne $data.Count) { throw 'RVA 字段运行时检查不完整' }
}
& dotnet build (Join-Path $PSScriptRoot 'VmBehavior\VmBehavior.csproj') -c Release --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw '行为对照程序构建失败' }
foreach($item in $reports) {
    $candidate=Get-ChildItem -LiteralPath $item.DiagnosticsDirectory -Filter '*-recovered.*' | Where-Object Extension -in '.exe','.dll' | Select-Object -First 1
    Invoke-Probe @('check-initialization',$candidate.FullName,$item.RebuiltAssembly)
    Test-FieldData $item
}
$gg = $reports | Where-Object { [IO.Path]::GetFileName($_.Input) -eq 'ggggg.exe' }
Invoke-Probe @('compare-hash',$gg.Input,$gg.Output,'0600004B',$gg.RebuiltAssembly)
$form = $reports | Where-Object { [IO.Path]::GetFileName($_.Input) -eq 'WindowsFormsApplication41.exe' }
Invoke-Probe @('compare-out',$form.Input,$form.Output,'06000126','060003EB',$form.RebuiltAssembly)
foreach ($invalid in @('source','--self-test','--vm-worker','--output')) {
    & $exe $invalid 2>$null
    if ($LASTEXITCODE -ne 1) { throw ('公开 CLI 接受了额外命令：'+$invalid) }
}
if ($CheckKeyRejection) {
    $negativeRoot = Join-Path $resultRoot ('key-rejection-'+[Guid]::NewGuid().ToString('N'))
    $isolatedBin = Join-Path $negativeRoot 'bin'
    New-Item -ItemType Directory -Path $isolatedBin -Force | Out-Null
    Get-ChildItem -LiteralPath (Split-Path $exe) | Copy-Item -Destination $isolatedBin -Recurse
    foreach ($profile in @((Join-Path $isolatedBin 'vm-key-profiles.txt'),(Join-Path $isolatedBin 'worker\vm-key-profiles.txt'))) {
        Set-Content -LiteralPath $profile -Value '# disabled for negative fixture' -Encoding ASCII
    }
    $previousKeys=$env:UNPACKER_VM_INT32_KEYS
    $previousLimit=$env:UNPACKER_VM_KEY_SEARCH_LIMIT
    try {
        $env:UNPACKER_VM_INT32_KEYS='0,1'
        $env:UNPACKER_VM_KEY_SEARCH_LIMIT='0'
        $negativeOutput=Join-Path $negativeRoot 'negative.exe'
        & (Join-Path $isolatedBin 'Unpacker.exe') unpack $gg.Input $negativeOutput
        if ($LASTEXITCODE -ne 2) { throw '错误密钥未报告部分恢复' }
        $negative=Get-Content -LiteralPath (Get-WorkReport $negativeOutput) -Raw | ConvertFrom-Json
        if (!(Test-Path -LiteralPath $negativeOutput) -or $negative.After.VirtualizedMethods.Count -ne 2 -or ($negative.Stages | Where-Object Name -eq 'VM').Changes -ne 30) { throw '错误密钥被接受或部分恢复文件未输出' }
    } finally { $env:UNPACKER_VM_INT32_KEYS=$previousKeys; $env:UNPACKER_VM_KEY_SEARCH_LIMIT=$previousLimit }
    Write-Output 'PASS: 错误密钥被拒绝，保留未恢复的原始方法体，部分恢复文件仍输出。'
}
if ($CheckRepeat) {
    foreach ($original in @($reports | Where-Object { $_.Input -eq $gg.Input -or $_.After.ModernRuntime })) {
    $previousHash=(Get-FileHash -LiteralPath $original.Output -Algorithm SHA256).Hash
    $previousProject=$original.SourceProject
    & $exe unpack $original.Input $original.Output
    if ($LASTEXITCODE -ne 0) { throw '相同输出文件重复运行失败' }
    $repeat=Get-Content -LiteralPath (Get-WorkReport $original.Output) -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $original.Output -Algorithm SHA256).Hash -ne $previousHash -or $repeat.SourceProject -eq $previousProject -or !(Test-Path -LiteralPath $previousProject) -or
        @(Get-ChildItem -LiteralPath (Split-Path $original.Output) -Force).Count -ne $(if($repeat.RuntimeConfig){2}else{1})) { throw '重复输出内容不同、旧源码被覆盖或产生了额外文件' }
    Test-FieldData $repeat
    $reports=@($reports | ForEach-Object { if ($_.Input -eq $original.Input) { $repeat } else { $_ } })
    }
    $reports | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $resultRoot 'report.json') -Encoding UTF8
    Write-Output 'PASS: 输出文件可重复生成，内容一致，旧 C# 工程保留。'
}
$hashes | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $resultRoot 'hashes.json') -Encoding UTF8
Write-Output ('PASS: '+$samples.Count+' 个原件未变；完整 C# 工程全部重新构建；'+($samples | Measure-Object Vm -Sum).Sum+' 个 VM 方法恢复。')
# Expected CLI rejection probes above leave LASTEXITCODE=1.
exit 0

