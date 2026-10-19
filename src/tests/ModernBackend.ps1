[CmdletBinding()]
param([string]$OutputRoot=(Join-Path $PSScriptRoot ('..\..\artifacts\modern-backend-'+[Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& dotnet build (Join-Path $repository 'Unpacker.slnx') -c Release --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Build failed.'}
$fixture=Join-Path $OutputRoot 'fixture'
New-Item -ItemType Directory -Path $fixture | Out-Null
$dependency=Join-Path $OutputRoot 'dependency'
New-Item -ItemType Directory -Path $dependency | Out-Null
Set-Content -LiteralPath (Join-Path $dependency 'Dependency.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>ModernDependencyFixture</AssemblyName><AssemblyVersion>1.0.0.0</AssemblyVersion></PropertyGroup></Project>'
Set-Content -LiteralPath (Join-Path $dependency 'Dependency.cs') -Value 'public static class Dependency { public static int Compute(int value) => value * 2; }'
& dotnet build (Join-Path $dependency 'Dependency.csproj') -c Release --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Dependency build failed.'}
Set-Content -LiteralPath (Join-Path $fixture 'Fixture.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>ModernValidationFixture</AssemblyName></PropertyGroup></Project>'
$project=[xml](Get-Content -LiteralPath (Join-Path $fixture 'Fixture.csproj') -Raw)
$group=$project.CreateElement('ItemGroup');$reference=$project.CreateElement('Reference');$reference.SetAttribute('Include','ModernDependencyFixture')
$hint=$project.CreateElement('HintPath');$hint.InnerText=(Join-Path $dependency 'bin\Release\net8.0\ModernDependencyFixture.dll')
$reference.AppendChild($hint)|Out-Null;$group.AppendChild($reference)|Out-Null;$project.Project.AppendChild($group)|Out-Null
$project.Save((Join-Path $fixture 'Fixture.csproj'))
Set-Content -LiteralPath (Join-Path $fixture 'Fixture.cs') -Value 'public static class Fixture { public static int Add(int value) => value + 1; public static int CallDependency(int value) => Dependency.Compute(value); }'
& dotnet build (Join-Path $fixture 'Fixture.csproj') -c Release --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
$assembly=Join-Path $fixture 'bin\Release\net8.0\ModernValidationFixture.dll'
# Compile against v1, then provide only v2, as in the supplied Aspose corpus.
$dependencyProject=(Get-Content -LiteralPath (Join-Path $dependency 'Dependency.csproj') -Raw).Replace('1.0.0.0','2.0.0.0')
Set-Content -LiteralPath (Join-Path $dependency 'Dependency.csproj') -Value $dependencyProject
& dotnet build (Join-Path $dependency 'Dependency.csproj') -c Release --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'Higher dependency build failed.'}
Copy-Item -LiteralPath (Join-Path $dependency 'bin\Release\net8.0\ModernDependencyFixture.dll') -Destination (Join-Path (Split-Path $assembly) 'ModernDependencyFixture.dll') -Force
$vm=Join-Path $OutputRoot 'vm-methods.json'
Set-Content -LiteralPath $vm -Value '[{"Token":"0x06000001","Restored":true,"Inlined":true,"RecoveredAdapterToken":"0x06000002"}]'
$report=Join-Path $OutputRoot 'clr-validation.json'
$worker=Join-Path $repository 'src\bin\Release\net48\worker\modern\Unpacker.ModernWorker.exe'
& $worker --validate-worker $assembly $vm $report 5 $assembly
if($LASTEXITCODE -ne 0){throw 'Modern CLR validation failed.'}
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if($result.Expected -ne 2 -or $result.Prepared -ne 2 -or $result.Failed -ne 0){throw 'Incorrect modern host/adapter validation coverage.'}
if(@($result.DependencyBindings | Where-Object {$_.Loaded -like 'ModernDependencyFixture, Version=2.0.0.0*' -and $_.Basis -like 'CoreCLR*'}).Count -ne 1){throw 'Higher-version dependency binding was not audited.'}
# A malformed recovered body must not be reported as successfully prepared.
Set-Content -LiteralPath $vm -Value '[{"Token":"0x0600FFFF","Restored":true}]'
$bad=Join-Path $OutputRoot 'invalid-token-validation.json'
& $worker --validate-worker $assembly $vm $bad 5 $assembly
if($LASTEXITCODE -eq 0){throw 'Invalid recovered method token was accepted.'}
$failure=Get-Content -LiteralPath $bad -Raw | ConvertFrom-Json
if($failure.Prepared -ne 0 -or $failure.Failed -ne 1){throw 'Invalid-token failure was not retained.'}
Write-Output ('PASS: modern CLR prepares host and adapter, audits a same-identity higher-version dependency and rejects invalid tokens. Evidence: '+$OutputRoot)
exit 0
