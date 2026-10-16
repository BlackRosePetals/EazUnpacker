param(
    [Parameter(Mandatory=$true)][string]$InputAssembly,
    [string]$Tool = (Join-Path $PSScriptRoot '..\bin\Release\net48\Unpacker.exe')
)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('unpacker-partial-publication-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$inputHash = (Get-FileHash -LiteralPath $InputAssembly -Algorithm SHA256).Hash
# A harmless dup/pop in a handler makes a valid, deliberately unsupported VM
# variant, so this regression continues to cover partial recovery after fixes.
Add-Type -Path (Join-Path (Split-Path $Tool) 'dnlib.dll')
$fixtureInput = Join-Path $root 'partial-input.exe'
$module = [dnlib.DotNet.ModuleDefMD]::Load([IO.Path]::GetFullPath($InputAssembly))
try {
    $changed = 0
    foreach ($type in $module.GetTypes()) {
        foreach ($method in $type.Methods) {
            if (!$method.HasBody) { continue }
            $instructions = $method.Body.Instructions
            for ($i = 3; $i -lt $instructions.Count - 1; $i++) {
                if ($instructions[$i].Operand -is [dnlib.DotNet.IMethod] -and
                    $instructions[$i].Operand.FullName -eq 'System.Object System.Reflection.FieldInfo::GetValue(System.Object)' -and
                    $instructions[$i-1].IsLdloc() -and $instructions[$i-2].IsLdloc() -and
                    $instructions[$i-3].OpCode.Code -eq [dnlib.DotNet.Emit.Code]::Ldarg_0) {
                    $instructions.Insert($i+1, [dnlib.DotNet.Emit.OpCodes]::Dup.ToInstruction())
                    $instructions.Insert($i+2, [dnlib.DotNet.Emit.OpCodes]::Pop.ToInstruction())
                    $changed++
                    break
                }
            }
        }
    }
    if ($changed -eq 0) { throw 'Fixture must contain an instance FieldInfo.GetValue VM handler' }
    $writerOptions = [dnlib.DotNet.Writer.ModuleWriterOptions]::new($module)
    $writerOptions.MetadataOptions.Flags = [dnlib.DotNet.Writer.MetadataFlags]::PreserveAll
    $module.Write($fixtureInput, $writerOptions)
} finally { $module.Dispose() }
if (Test-Path -LiteralPath ($InputAssembly + '.config')) { Copy-Item -LiteralPath ($InputAssembly + '.config') -Destination ($fixtureInput + '.config') }
foreach ($malformedConfig in @($false, $true)) {
    $output = Join-Path $root ('partial-' + $malformedConfig + '.exe')
    if ($malformedConfig) { Set-Content -LiteralPath ($output + '.config') -Value '<configuration><runtime>' }
    & $Tool unpack $fixtureInput $output *> (Join-Path $root ('run-' + $malformedConfig + '.log'))
    $code = $LASTEXITCODE
    if ($code -ne 2 -or !(Test-Path -LiteralPath $output)) { throw "Expected partial exit 2 and a published file; got $code. See $root" }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($output).ToUpperInvariant()))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $latestPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Unpacker\work\' + $key + '\latest.json')
    $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    $backend = Get-Content -LiteralPath (Join-Path $latest.DiagnosticsDirectory 'result.json') -Raw | ConvertFrom-Json
    if ($backend.Status -ne 'partial' -or $backend.After.VirtualizedMethods.Count -eq 0) { throw "Fixture must exercise incomplete VM recovery. See $root" }
    if ($latest.Status -ne 'partial' -or $latest.Output -ne $output -or !($latest.Stages | Where-Object { $_.Name -eq 'Publish' -and $_.Status -eq 'success' })) { throw "Published path or partial status missing. See $root" }
    if ((Get-FileHash -LiteralPath $backend.Output).Hash -ne (Get-FileHash -LiteralPath $output).Hash) { throw "Published output differs from recovered IL. See $root" }
    if ($malformedConfig) {
        if (!($latest.Stages | Where-Object { $_.Name -eq 'RuntimeSignatureCompatibility' -and $_.Status -eq 'failed' })) { throw "Missing configuration failure record. See $root" }
    } elseif (!(Test-Path -LiteralPath ($output + '.config')) -or ([xml](Get-Content -LiteralPath ($output + '.config') -Raw)).configuration.runtime.bypassTrustedAppStrongNames.enabled -ne 'true') {
        throw "Missing runtime configuration. See $root"
    }
}
$missingOutput = Join-Path $root 'missing-input.exe'
& $Tool unpack (Join-Path $root 'does-not-exist.exe') $missingOutput *> (Join-Path $root 'missing-input.log')
if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $missingOutput)) { throw "Missing input must not create a recovery output. See $root" }
if ($inputHash -ne (Get-FileHash -LiteralPath $InputAssembly -Algorithm SHA256).Hash) { throw 'Original input changed' }
Write-Host "PASS: incomplete VM recovery is published unchanged, configuration errors retain output, exit 2/partial are preserved, and missing input creates no output. Evidence: $root"
exit 0
