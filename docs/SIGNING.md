# 代码签名与 SmartScreen / Defender

本文说明为什么 AudioSwitch 的安装包会出现「Windows 已保护你的电脑」或「未知发布者」，以及怎样彻底消除它。

## 为什么会提示

Windows 对从网络下载的可执行文件做两层检查：

1. **Mark-of-the-Web（MOTW）**：浏览器、聊天软件下载的 exe 会被写入 `Zone.Identifier` 备用数据流。
2. **Authenticode 签名与信誉**：带 MOTW 且**没有可信代码签名**的 exe，会触发 SmartScreen：

   ```text
   Windows 已保护你的电脑
   发布者：未知发布者
   ```

   需要点「更多信息 → 仍要运行」才能继续。这是 Windows 的安全机制，不是程序本身的缺陷。

安装程序自身声明的 `requireAdministrator` 只决定 **UAC 提权**，与 SmartScreen 无关；所以安装包会先过 SmartScreen，再弹 UAC。

## 不能靠代码绕过

以下做法**不会**真正解决问题，也不建议采用：

- 把安装包改名、重新压缩、塞进 ISO/自解压包来规避 MOTW —— 属于规避安全提示，且仍可能被 Defender 启发式拦截。
- 用自签名证书（self-signed）签名 —— 证书不在用户的受信任根里，SmartScreen 仍会提示，还可能被当成可疑程序。
- 在安装程序里自动添加 Defender 排除项 —— 需要先运行起来，且属于降低用户安全性的行为。

**唯一正规的消除方式，是使用受信任 CA 签发的代码签名证书。**

## 方案对比

| 方案 | 效果 | 成本/门槛 |
| --- | --- | --- |
| **EV 代码签名证书** | 签名后立即获得 SmartScreen 信誉，通常不再提示 | 较贵（约 $200–400/年），需要企业/个人实名与硬件令牌或云签名 |
| **OV 代码签名证书** | 签名有效，但新证书需要下载量积累信誉，前期仍可能提示 | 约 $100–200/年，实名验证 |
| **Azure Trusted Signing** | 微软云签名，符合条件后可直接被信任 | 需要 Azure 订阅与身份验证，按签名次数计费 |
| **SignPath Foundation** | 面向开源项目的免费签名服务 | 需要申请与项目审核，非即时 |
| **Microsoft Store（MSIX）** | 商店分发，安装由商店完成 | 需要开发者账号与上架审核 |
| 不签名 + `scripts/install-latest.ps1` | 绕过浏览器 MOTW，只剩 UAC 一次点击 | 免费，已在 README 提供 |

## 用证书签名构建

`scripts/build-installer.ps1` 已内置签名支持，需要 Windows SDK 的 `signtool.exe`（`signtool` 在 PATH 中，或安装在 `%ProgramFiles(x86)%\Windows Kits\10\bin\<版本>\x64\`）。

使用证书指纹（证书已安装到当前用户或计算机的证书存储）：

```powershell
pwsh -File scripts\build-installer.ps1 -SigningThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

使用 PFX 文件：

```powershell
pwsh -File scripts\build-installer.ps1 -PfxPath .\cert.pfx -PfxPassword '证书密码'
```

可选参数：`-TimestampUrl`（默认 `http://timestamp.digicert.com`，用于时间戳，保证证书过期后签名仍有效）。

脚本会依次签名：

1. `build\app\AudioSwitch.exe` 与 `build\app-lite\AudioSwitch.exe`（安装后与便携包内的主程序）；
2. `dist\AudioSwitch-Setup-<版本>.exe` 与 `dist\AudioSwitch-Lite-Setup-<版本>.exe`（决定下载时的 SmartScreen 体验）。

签名失败会直接终止构建；签名状态不是 `Valid` 时给出警告。

## 验证签名

```powershell
Get-AuthenticodeSignature dist\AudioSwitch-Setup-1.4.1.exe |
    Format-List Status, StatusMessage, SignerCertificate
```

`Status` 应为 `Valid`，`SignerCertificate.Subject` 应显示证书主体。也可以右键 exe → 属性 → 数字签名查看。

## Defender 误报

签名能消除「未知发布者」，但不能 100% 排除 Defender 的启发式误报（尤其是 NSIS 打包、带 `robocopy`/`taskkill` 的安装程序）。如果遇到：

1. 在 <https://www.microsoft.com/en-us/wdsi/filesubmission> 提交文件，选择「软件开发者」并说明误报；
2. 同时提交给第三方杀毒厂商（如卡巴斯基、火绒）的白名单流程；
3. 在 GitHub Release 中附上 SHA-256 与源码链接，方便用户核对。

> 发布前请确认 `SHA256SUMS.txt` 与实际上传的四个产物一致；签名后的文件哈希会变化，必须重新生成。

## CI 中签名（可选）

把 PFX 以 Base64 存入 GitHub Actions Secret，在发布工作流中还原后调用同一脚本：

```powershell
[IO.File]::WriteAllBytes("$env:RUNNER_TEMP\cert.pfx", [Convert]::FromBase64String($env:CERT_PFX_BASE64))
pwsh -File scripts\build-installer.ps1 -PfxPath "$env:RUNNER_TEMP\cert.pfx" -PfxPassword $env:CERT_PFX_PASSWORD
Remove-Item "$env:RUNNER_TEMP\cert.pfx" -Force
```

证书密码与 PFX 不要提交到仓库；`.gitignore` 已忽略 `*.pfx`。
