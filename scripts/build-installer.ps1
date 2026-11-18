# 生成安装程序与便携版压缩包
#
#   pwsh -File scripts\build-installer.ps1
#
# 产物：
#   dist\AudioSwitch-Setup-<版本>.exe        完整版安装程序（自带 .NET 运行时，启动时请求 UAC）
#   dist\AudioSwitch-Portable-<版本>.zip     完整版便携包（解压即用）
#   dist\AudioSwitch-Lite-Setup-<版本>.exe   精简版安装程序（约 1 MB，需要 .NET 9 桌面运行时）
#   dist\AudioSwitch-Lite-<版本>.zip         精简版便携包
#   dist\SHA256SUMS.txt                      四个产物的 SHA-256
#
# 代码签名（可选，强烈建议发布前签，消除“未知发布者”与 SmartScreen 提示）：
#   pwsh -File scripts\build-installer.ps1 -SigningThumbprint <证书指纹>
#   pwsh -File scripts\build-installer.ps1 -PfxPath cert.pfx -PfxPassword <密码>
# 需要 Windows SDK 的 signtool.exe；未提供证书时跳过签名。
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$SigningThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = (Select-String -Path 'src\AudioSwitch\AudioSwitch.csproj' -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw '无法从 csproj 读取版本号' }

$signingEnabled = [bool]($SigningThumbprint -or $PfxPath)

function Get-SignToolPath {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kitsRoot) {
        foreach ($dir in (Get-ChildItem $kitsRoot -Directory | Sort-Object Name -Descending)) {
            $candidate = Join-Path $dir.FullName 'x64\signtool.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    return $null
}

$signTool = if ($signingEnabled) { Get-SignToolPath } else { $null }
if ($signingEnabled -and -not $signTool) {
    throw '找不到 signtool.exe，请安装 Windows SDK 后重试，或不带签名参数构建'
}

function Sign-Artifact([string]$path) {
    if (-not $signingEnabled) { return }
    $arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($SigningThumbprint) {
        $arguments += @('/sha1', $SigningThumbprint)
    } else {
        $arguments += @('/f', $PfxPath)
        if ($PfxPassword) { $arguments += @('/p', $PfxPassword) }
    }
    $arguments += $path
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$path" }
    $signature = Get-AuthenticodeSignature -FilePath $path
    if ($signature.Status -ne 'Valid') {
        Write-Warning "签名状态为 $($signature.Status)：$path"
    } else {
        Write-Host "已签名：$path（$($signature.SignerCertificate.Subject)）"
    }
}

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

# 先签主程序，安装后的 exe 与便携包内都带签名
Sign-Artifact (Join-Path $appDir 'AudioSwitch.exe')
Sign-Artifact (Join-Path $liteDir 'AudioSwitch.exe')

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

# 安装程序签名（决定 SmartScreen 与“未知发布者”提示）
Sign-Artifact $setupPath
Sign-Artifact $liteSetupPath

# 生成 SHA256SUMS.txt，供发布与一键安装脚本校验
$sums = foreach ($file in @($setupPath, $liteSetupPath, $portablePath, $liteZipPath)) {
    $hash = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToLower()
    "$hash *$(Split-Path -Leaf $file)"
}
Set-Content -Path (Join-Path $distDir 'SHA256SUMS.txt') -Value $sums -Encoding ascii

function Format-Size([long]$bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

Write-Host ''
Write-Host "完整版安装程序：$setupPath（$(Format-Size (Get-Item $setupPath).Length)）"
Write-Host "完整版便携包：  $portablePath（$(Format-Size (Get-Item $portablePath).Length)）"
Write-Host "精简版安装程序：$liteSetupPath（$(Format-Size (Get-Item $liteSetupPath).Length)）"
Write-Host "精简版便携包：  $liteZipPath（$(Format-Size (Get-Item $liteZipPath).Length)）"
Write-Host ''
Write-Host '自检（安装后）：--probe / --self-test / --check-update'
