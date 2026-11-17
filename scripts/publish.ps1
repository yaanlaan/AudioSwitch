# 发布自包含单文件版本到 dist\AudioSwitch.exe
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root 'dist'
}

$project = Join-Path $root 'src\AudioSwitch\AudioSwitch.csproj'
if (-not (Test-Path $project)) {
    throw "找不到项目文件：$project"
}

# 应用正在运行时文件会被锁定，先结束旧进程
Get-Process -Name AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output

Get-ChildItem $Output -Filter '*.pdb' | Remove-Item -Force

$exe = Join-Path $Output 'AudioSwitch.exe'
if (-not (Test-Path $exe)) {
    throw "发布失败：没有生成 $exe"
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "发布完成：$exe（$size MB）" -ForegroundColor Green
Write-Host "自检：& '$exe' --self-test" -ForegroundColor DarkGray
