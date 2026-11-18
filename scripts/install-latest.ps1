# 一键安装 AudioSwitch：从 GitHub Releases 下载安装包、校验 SHA-256，再以管理员身份运行。
#
# 最简用法（PowerShell 中执行，不需要浏览器下载，因此不会出现 SmartScreen 的“仍要运行”）：
#   irm https://raw.githubusercontent.com/yaanlaan/AudioSwitch/main/scripts/install-latest.ps1 | iex
#
# 也可以先下载本脚本再带参数运行：
#   pwsh -File scripts\install-latest.ps1                      # 完整版，静默安装
#   pwsh -File scripts\install-latest.ps1 -Flavor lite          # 精简版（需 .NET 9 桌面运行时）
#   pwsh -File scripts\install-latest.ps1 -InstallDir D:\Tools\AudioSwitch
#   pwsh -File scripts\install-latest.ps1 -NoLaunch             # 只下载并校验，不运行
param(
    [ValidateSet('full', 'lite')]
    [string]$Flavor = 'full',
    [string]$Version,
    [string]$InstallDir,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$repo = 'yaanlaan/AudioSwitch'
$headers = @{ 'User-Agent' = 'AudioSwitch-Installer' }
$hasCurl = [bool](Get-Command 'curl.exe' -ErrorAction SilentlyContinue)

function Save-Url {
    param([string]$Uri, [string]$Path, [string]$Description)
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path $Path) { Remove-Item $Path -Force }
            if ($hasCurl) {
                & curl.exe -sS -L --fail --max-time 300 --retry 3 --retry-all-errors -o $Path $Uri
                if ($LASTEXITCODE -ne 0) { throw "curl 退出码 $LASTEXITCODE" }
            } else {
                Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing -TimeoutSec 300
            }
            if ((Test-Path $Path) -and (Get-Item $Path).Length -gt 0) { return }
            throw '下载结果为空'
        } catch {
            if ($attempt -eq 5) { throw "$Description 失败：$($_.Exception.Message)" }
            Start-Sleep -Seconds (3 * $attempt)
        }
    }
}

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('AudioSwitch-setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

Write-Host '获取 AudioSwitch 版本信息…'
$releaseJson = Join-Path $tempDir 'release.json'
if ($Version) {
    $tag = 'v' + $Version.TrimStart('v')
    $releaseUrl = "https://api.github.com/repos/$repo/releases/tags/$tag"
} else {
    $tag = $null
    $releaseUrl = "https://api.github.com/repos/$repo/releases/latest"
}
Save-Url -Uri $releaseUrl -Path $releaseJson -Description '获取版本信息'
$release = Get-Content -Path $releaseJson -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $release.tag_name) { throw '无法解析 Release 信息' }

$number = $release.tag_name.TrimStart('v')
if ($Flavor -eq 'lite') {
    $assetName = "AudioSwitch-Lite-Setup-$number.exe"
} else {
    $assetName = "AudioSwitch-Setup-$number.exe"
}

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
$sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) 中没有 $assetName" }
if (-not $sumsAsset) { throw "Release $($release.tag_name) 中没有 SHA256SUMS.txt，无法校验" }

$setupPath = Join-Path $tempDir $assetName
Write-Host "下载 $assetName（$([math]::Round($asset.size / 1MB, 1)) MB）…"
Save-Url -Uri $asset.browser_download_url -Path $setupPath -Description "下载 $assetName"

$sumsPath = Join-Path $tempDir 'SHA256SUMS.txt'
Save-Url -Uri $sumsAsset.browser_download_url -Path $sumsPath -Description '下载 SHA256SUMS.txt'
$sumsText = Get-Content -Path $sumsPath -Raw -Encoding UTF8
$line = ($sumsText -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($assetName) } | Select-Object -First 1
if (-not $line) { throw "SHA256SUMS.txt 中没有 $assetName" }

$expected = ($line.Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 校验失败：期望 $expected，实际 $actual" }
Write-Host 'SHA-256 校验通过'

# PowerShell 下载的文件不带“来自 Internet”标记；这里再兜底解除一次
Unblock-File -Path $setupPath -ErrorAction SilentlyContinue

if ($NoLaunch) {
    Write-Host "已下载到：$setupPath"
    return
}

$arguments = @('/S')
if ($InstallDir) { $arguments += "/D=$InstallDir" }

Write-Host '以管理员身份运行安装程序，Windows 会弹出 UAC，请选择“是”…'
$process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Verb RunAs -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "安装程序退出码：$($process.ExitCode)" }

$targetDir = if ($InstallDir) { $InstallDir } else { Join-Path $env:LOCALAPPDATA 'Programs\AudioSwitch' }
$installedExe = Join-Path $targetDir 'AudioSwitch.exe'
if (Test-Path $installedExe) {
    Write-Host "安装完成：$installedExe"
    Write-Host '可以从开始菜单启动 AudioSwitch，或运行上面的 exe。'
} else {
    Write-Warning "安装程序已结束，但未找到 $installedExe；请检查安装日志 %TEMP%\AudioSwitch-install.log"
}
