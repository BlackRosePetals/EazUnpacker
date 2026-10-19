param(
    [Parameter(Mandatory=$true)][string]$InputAssembly,
    [string]$Tool = (Join-Path $PSScriptRoot '..\bin\Release\net48\Unpacker.exe'),
    [string]$ResultRoot = (Join-Path $PSScriptRoot '..\test-results\source-export-options')
)
$ErrorActionPreference = 'Stop'
$InputAssembly = [IO.Path]::GetFullPath($InputAssembly)
$Tool = [IO.Path]::GetFullPath($Tool)
$root = Join-Path ([IO.Path]::GetFullPath($ResultRoot)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$output = Join-Path $root ('unpacked output' + [IO.Path]::GetExtension($InputAssembly))
$inputHash = (Get-FileHash -LiteralPath $InputAssembly -Algorithm SHA256).Hash
$sha = [Security.Cryptography.SHA256]::Create()
try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
$latestPath = Join-Path (Split-Path $Tool) ('temp\work\' + $key + '\latest.json')

function Invoke-Unpack([string]$name, [string[]]$commandArguments, [bool]$exportSource, [bool]$validateClr=$false) {
    & $Tool @commandArguments *> (Join-Path $root ($name + '.log'))
    $code = $LASTEXITCODE
    if ($code -ne 0 -or !(Test-Path -LiteralPath $output)) { throw "Expected complete recovery, got exit $code. Use a fully supported sample. See $root" }
    $result = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    if ($result.Status -ne 'complete' -or $result.Output -ne $output) { throw "Unexpected recovery status or published path. See $root" }
    if($result.ClrValidationRequested -ne $validateClr){throw "Unexpected CLR option state. See $root"}
    if(!$validateClr -and (@($result.Stages|Where-Object {$_.Name -in 'CLRValidation','RebuiltCLRValidation'}).Count -or (Test-Path (Join-Path $result.DiagnosticsDirectory 'clr-validation.json')) -or (Test-Path (Join-Path $result.DiagnosticsDirectory 'rebuilt-clr-validation.json')))){throw "Unrequested CLR validation ran. See $root"}
    if(@(Get-ChildItem -LiteralPath (Split-Path $output) -File -Filter '*-recovered.pdb').Count){throw "Public output contains a recovered-name PDB alias. See $root"}
    $sourceStages = @($result.Stages | Where-Object { $_.Name -in 'StringSearchIndex','SourceBuild','RebuiltCLRValidation' })
    if ($exportSource) {
        foreach ($path in @($result.SourceProject, $result.RebuiltAssembly, $result.StringIndex)) {
            if (!$path -or !(Test-Path -LiteralPath $path)) { throw "Missing optional source export artifact. See $root" }
        }
        if (!($sourceStages | Where-Object { $_.Name -eq 'SourceBuild' -and $_.Status -eq 'success' })) { throw "Source build did not run successfully. See $root" }
        if ($validateClr -and (Test-Path -LiteralPath (Join-Path $result.DiagnosticsDirectory 'vm-methods.json')) -and
            !($sourceStages | Where-Object { $_.Name -eq 'RebuiltCLRValidation' -and $_.Status -eq 'success' })) { throw "Missing rebuilt VM validation. See $root" }
    } else {
        if ($result.SourceProject -or $result.RebuiltAssembly -or $result.StringIndex -or $sourceStages.Count) { throw "Default unpack ran optional source stages or retained stale source metadata. See $root" }
        foreach ($name in @('source','source.log','source-build.json','string-index','string-index.log','rebuilt-vm-methods.json','rebuilt-clr-validation.json','rebuilt-clr-validation.log','clr-validation.json','clr-validation.log')) {
            if (Test-Path -LiteralPath (Join-Path $result.DiagnosticsDirectory $name)) { throw "Default unpack created optional artifact $name. See $root" }
        }
        $events = @(Get-Content -LiteralPath (Join-Path $result.DiagnosticsDirectory 'progress.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
        if ($events | Where-Object { $_.Event.Stage -in 6,7 }) { throw "Default unpack entered decompilation/rebuild progress stages. See $root" }
    }
    return $result
}

# A missing compiler SDK must not affect the default recovery-only command.
$previous = $env:MSBuildSDKsPath
try {
    $env:MSBuildSDKsPath = Join-Path $root 'missing-sdk'
    $default = Invoke-Unpack 'default-no-sdk' @('unpack', $InputAssembly, $output) $false
} finally { $env:MSBuildSDKsPath = $previous }
$outputHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
$sourceWithoutClr = Invoke-Unpack 'export-without-clr' @('unpack', $InputAssembly, $output, '--export-source') $true
$trailing = Invoke-Unpack 'export-trailing' @('unpack', $InputAssembly, $output, '--export-source','--validate-clr') $true $true
$leading = Invoke-Unpack 'export-leading' @('unpack', '--export-source', '--validate-clr', $InputAssembly, $output) $true $true
$repeat = Invoke-Unpack 'default-after-export' @('unpack', $InputAssembly, $output) $false
if ($default.DiagnosticsDirectory -eq $repeat.DiagnosticsDirectory -or
    !(Test-Path -LiteralPath $trailing.SourceProject) -or !(Test-Path -LiteralPath $leading.SourceProject) -or
    $outputHash -ne (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash) { throw "Repeat unpack overwrote source history or changed recovered IL. See $root" }
$analysis = & $Tool analyzer $output | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $analysis.DiagnosticsDirectory -ne $repeat.DiagnosticsDirectory -or
    $analysis.SourceProject -or $analysis.RebuiltAssembly -or $analysis.StringIndex) { throw "Analyzer retained stale optional source artifacts. See $root" }

$invalidOutput = Join-Path $root 'invalid.exe'
$invalid = @(
    @{ Name='unknown-option'; Arguments=@('unpack', $InputAssembly, $invalidOutput, '--unknown') },
    @{ Name='duplicate-option'; Arguments=@('unpack', '--export-source', $InputAssembly, $invalidOutput, '--export-source') },
    @{ Name='missing-output'; Arguments=@('unpack', $InputAssembly, '--export-source') },
    @{ Name='extra-path'; Arguments=@('unpack', $InputAssembly, $invalidOutput, 'extra.exe') },
    @{ Name='analyzer-option'; Arguments=@('analyzer', $InputAssembly, '--export-source') },
    @{ Name='usage'; Arguments=@() }
)
foreach ($test in $invalid) {
    $arguments = $test.Arguments
    & $Tool @arguments *> (Join-Path $root ($test.Name + '.log'))
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $invalidOutput)) { throw "Invalid CLI arguments were accepted: $($test.Name). See $root" }
}
if (!(Get-Content -LiteralPath (Join-Path $root 'usage.log') -Raw).Contains('--export-source')) { throw "Usage omits source export option. See $root" }
if ($inputHash -ne (Get-FileHash -LiteralPath $InputAssembly -Algorithm SHA256).Hash) { throw 'Original input changed' }
@($default, $sourceWithoutClr, $trailing, $leading, $repeat) | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $root 'report.json') -Encoding UTF8
Write-Host "PASS: default unpack needs no source SDK; explicit source export works; repeated default runs clear optional metadata; invalid options are rejected. Evidence: $root"
exit 0
