# 生成安装程序与便携版压缩包
#
#   pwsh -File scripts\build-installer.ps1
#
# 产物：
#   dist\AudioSwitch-Setup-<版本>.exe      按用户安装的安装程序（无需管理员）
#   dist\AudioSwitch-Portable-<版本>.zip   便携版（解压即用，不含单文件自解压）
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
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }

Write-Host '发布文件夹版本（供安装程序与便携版使用）'
dotnet publish 'src\AudioSwitch\AudioSwitch.csproj' -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $distDir | Out-Null

$portablePath = Join-Path $distDir "AudioSwitch-Portable-$version.zip"
if (Test-Path $portablePath) { Remove-Item $portablePath -Force }
Write-Host '打包便携版'
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $portablePath -CompressionLevel Optimal

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

Write-Host '编译安装程序'
# NSIS 读取含中文的脚本时需要 UTF-8 BOM
$nsiPath = Join-Path $root 'installer\installer.nsi'
$nsiText = [System.IO.File]::ReadAllText($nsiPath)
[System.IO.File]::WriteAllText($nsiPath, $nsiText, (New-Object System.Text.UTF8Encoding($true)))
& $makensis "/DAPP_VERSION=$version" '/V2' 'installer\installer.nsi'
if ($LASTEXITCODE -ne 0) { throw 'makensis 失败' }

$setupPath = Join-Path $distDir "AudioSwitch-Setup-$version.exe"
if (-not (Test-Path $setupPath)) { throw "安装程序未生成：$setupPath" }

function Format-Size([long]$bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

Write-Host ''
Write-Host "安装程序：$setupPath（$(Format-Size (Get-Item $setupPath).Length)）"
Write-Host "便携版：  $portablePath（$(Format-Size (Get-Item $portablePath).Length)）"
Write-Host ''
Write-Host '自检（安装后）：dist\AudioSwitch-Setup-*.exe'
