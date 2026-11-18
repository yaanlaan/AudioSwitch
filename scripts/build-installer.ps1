# 生成安装程序与便携版压缩包
#
#   pwsh -File scripts\build-installer.ps1
#
# 产物：
#   dist\AudioSwitch-Setup-<版本>.exe        完整版安装程序（自带 .NET 运行时，启动时请求 UAC）
#   dist\AudioSwitch-Portable-<版本>.zip     完整版便携包（解压即用）
#   dist\AudioSwitch-Lite-Setup-<版本>.exe   精简版安装程序（约 1 MB，需要 .NET 9 桌面运行时）
#   dist\AudioSwitch-Lite-<版本>.zip         精简版便携包
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = (Select-String -Path 'src\AudioSwitch\AudioSwitch.csproj' -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw '无法从 csproj 读取版本号' }

Write-Host "AudioSwitch $version：结束正在运行的实例"
Get-Process AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force

$appDir = Join-Path $root 'build\app'
$liteDir = Join-Path $root 'build\app-lite'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
if (Test-Path $liteDir) { Remove-Item $liteDir -Recurse -Force }

Write-Host '发布完整版（自带运行时，供安装程序与便携版使用）'
dotnet publish 'src\AudioSwitch\AudioSwitch.csproj' -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '发布精简版（依赖 .NET 9 桌面运行时）'
dotnet publish 'src\AudioSwitch\AudioSwitch.csproj' -c $Configuration -r $Runtime --self-contained false `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o $liteDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish（精简版）失败' }

Get-ChildItem $appDir, $liteDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $distDir | Out-Null

$portablePath = Join-Path $distDir "AudioSwitch-Portable-$version.zip"
$liteZipPath = Join-Path $distDir "AudioSwitch-Lite-$version.zip"
foreach ($zip in @($portablePath, $liteZipPath)) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
}

Write-Host '打包便携版'
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $portablePath -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $liteDir '*') -DestinationPath $liteZipPath -CompressionLevel Optimal

# 获取 NSIS（便携版，不安装到系统）
$nsisRoot = Join-Path $root 'build-tools\nsis\nsis-3.11'
$makensis = Join-Path $nsisRoot 'makensis.exe'
if (-not (Test-Path $makensis)) {
    Write-Host '下载 NSIS 3.11'
    $tools = Join-Path $root 'build-tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $zip = Join-Path $tools 'nsis.zip'
    curl.exe -L --fail --max-time 300 -o $zip 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip'
    if ($LASTEXITCODE -ne 0) { throw '下载 NSIS 失败' }
    Expand-Archive -Path $zip -DestinationPath (Join-Path $tools 'nsis') -Force
}

# NSIS 需要可写的临时目录
$tempDir = Join-Path $root 'build-tools\tmp'
New-Item -ItemType Directory -Force $tempDir | Out-Null
$env:TEMP = $tempDir
$env:TMP = $tempDir

Write-Host '编译安装程序（完整版）'
# NSIS 读取含中文的脚本时需要 UTF-8 BOM
$nsiPath = Join-Path $root 'installer\installer.nsi'
$nsiText = [System.IO.File]::ReadAllText($nsiPath)
[System.IO.File]::WriteAllText($nsiPath, $nsiText, (New-Object System.Text.UTF8Encoding($true)))
& $makensis "/DAPP_VERSION=$version" '/V2' 'installer\installer.nsi'
if ($LASTEXITCODE -ne 0) { throw 'makensis（完整版）失败' }

Write-Host '编译安装程序（精简版）'
& $makensis "/DAPP_VERSION=$version" '/DLITE' '/V2' 'installer\installer.nsi'
if ($LASTEXITCODE -ne 0) { throw 'makensis（精简版）失败' }

$setupPath = Join-Path $distDir "AudioSwitch-Setup-$version.exe"
$liteSetupPath = Join-Path $distDir "AudioSwitch-Lite-Setup-$version.exe"
if (-not (Test-Path $setupPath)) { throw "安装程序未生成：$setupPath" }
if (-not (Test-Path $liteSetupPath)) { throw "精简版安装程序未生成：$liteSetupPath" }

function Format-Size([long]$bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

Write-Host ''
Write-Host "完整版安装程序：$setupPath（$(Format-Size (Get-Item $setupPath).Length)）"
Write-Host "完整版便携包：  $portablePath（$(Format-Size (Get-Item $portablePath).Length)）"
Write-Host "精简版安装程序：$liteSetupPath（$(Format-Size (Get-Item $liteSetupPath).Length)）"
Write-Host "精简版便携包：  $liteZipPath（$(Format-Size (Get-Item $liteZipPath).Length)）"
Write-Host ''
Write-Host '自检（安装后）：--probe / --self-test / --check-update'
