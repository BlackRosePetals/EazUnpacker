param(
    [Parameter(Mandatory=$true)][string]$InputAssembly,
    [string]$Tool = (Join-Path $PSScriptRoot '..\bin\Release\net48\Unpacker.exe')
)
$ErrorActionPreference = 'Stop'
$root = Join-Path (Join-Path (Split-Path ([IO.Path]::GetFullPath($Tool))) 'temp') ('unpacker-source-failure-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$output = Join-Path $root 'unpacked.exe'
$previous = $env:MSBuildSDKsPath
try {
    # Break only the optional source compiler, not the recovery worker.
    $env:MSBuildSDKsPath = Join-Path $root 'missing-sdk'
    & $Tool unpack $InputAssembly $output --export-source *> (Join-Path $root 'run.log')
    $code = $LASTEXITCODE
    if ($code -ne 2 -or !(Test-Path -LiteralPath $output)) { throw "Expected partial exit 2 and published IL; got $code. See $root" }
    if (!(Test-Path -LiteralPath ($output+'.config')) -or ([xml](Get-Content ($output+'.config') -Raw)).configuration.runtime.bypassTrustedAppStrongNames.enabled -ne 'true') { throw "Missing runtime compatibility policy after source failure. See $root" }
    $info = (& $Tool analyzer $output | ConvertFrom-Json)
    $latest = Get-Content (Join-Path (Split-Path $info.DiagnosticsDirectory) 'latest.json') -Raw | ConvertFrom-Json
    if ($latest.Status -ne 'partial') { throw "Expected partial status, got $($latest.Status). See $root" }
    if ($latest.Output -ne $output -or !($latest.Stages | Where-Object { $_.Name -eq 'SourceBuild' -and $_.Status -eq 'failed' })) { throw "Missing published path or source failure record. See $root" }
    Write-Host "PASS: failed source build retains unpacked assembly, exit 2 and partial diagnostics. Evidence: $root"
} finally {
    $env:MSBuildSDKsPath = $previous
}
