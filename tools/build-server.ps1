# 编译 PC 端服务
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet build (Join-Path $root 'pc\PcAudioServer.csproj') -c Release -o (Join-Path $root 'bin')
if($LASTEXITCODE -ne 0){ throw 'PC 端编译失败' }
Write-Host ('编译完成: ' + (Join-Path $root 'bin\PcAudioServer.exe')) -ForegroundColor Green