# Builds the ysfUtil plugin: dist\ds5-haptics.ysfplugin.zip, versioned like the app.
# Needs ysfUtil checked out next to this repository (..\ysfUtil), or pass -Sdk <path to YsfUtil.Sdk.csproj>.
param([string]$Sdk)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $root 'DS5BtHaptics.App\DS5BtHaptics.App.csproj')
$version = ($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1

$out = Join-Path $root 'dist\plugin'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$arguments = @((Join-Path $PSScriptRoot 'DS5BtHaptics.Plugin.csproj'), '-c', 'Release', '-o', $out, "-p:PluginVersion=$version", '-nologo')
if ($Sdk) { $arguments += "-p:YsfUtilSdk=$Sdk" }
dotnet publish @arguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

# ysfUtil brings its own SDK; a copy in the plugin folder would only be ignored.
Get-ChildItem $out -Filter 'YsfUtil.Sdk.*' | Remove-Item
if (-not (Test-Path (Join-Path $out 'plugin.json'))) { throw 'plugin.json is missing.' }

$zip = Join-Path $root 'dist\ds5-haptics.ysfplugin.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Plugin $version -> $zip"
