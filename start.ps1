$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:SIMULATOR_DATA_ROOT = Join-Path $PSScriptRoot 'runtime'
$simulatorDll = Join-Path $PSScriptRoot 'bin\Debug\net8.0\TpPavoSimulator.dll'
if (-not (Test-Path -LiteralPath $simulatorDll)) { throw 'Once README icindeki yerel derleme adimlarini uygulayin.' }
Write-Host 'Yalnizca 127.0.0.1:18080. Durdurmak icin Ctrl+C.'
& dotnet $simulatorDll
exit $LASTEXITCODE
