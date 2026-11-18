; AudioSwitch 安装程序（NSIS 3.11，按用户安装；启动时请求 UAC 管理员权限）
; 安装流程：解压负载到临时目录 -> 用 robocopy 带重试复制到安装目录 -> 校验 -> 写快捷方式与卸载项
; 这样即使杀毒软件在复制瞬间扫描/锁定某个 dll，也能通过重试完成安装，而不是直接失败。
; 用 /DLITE 编译精简版：负载来自 build\app-lite，安装前检查 .NET 9 桌面运行时。
Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel admin
BrandingText "AudioSwitch"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define APP_NAME "AudioSwitch"
!ifndef APP_VERSION
  !define APP_VERSION "1.4.0"
!endif
!ifdef LITE
  !define PAYLOAD_DIR "..\build\app-lite"
  !define SETUP_FILE "..\dist\AudioSwitch-Lite-Setup-${APP_VERSION}.exe"
  !define FLAVOR_LABEL " 精简版"
!else
  !define PAYLOAD_DIR "..\build\app"
  !define SETUP_FILE "..\dist\AudioSwitch-Setup-${APP_VERSION}.exe"
  !define FLAVOR_LABEL ""
!endif
!define APP_EXE "AudioSwitch.exe"
!define APP_PUBLISHER "AudioSwitch"
!define APP_REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioSwitch"
!define INSTALL_LOG "$TEMP\AudioSwitch-install.log"

Name "${APP_NAME} ${APP_VERSION}${FLAVOR_LABEL}"
OutFile "${SETUP_FILE}"
InstallDir "$LOCALAPPDATA\Programs\AudioSwitch"
InstallDirRegKey HKCU "Software\AudioSwitch" "InstallDir"
Icon "..\src\AudioSwitch\Assets\app.ico"
UninstallIcon "..\src\AudioSwitch\Assets\app.ico"

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" ""

!define MUI_ICON "..\src\AudioSwitch\Assets\app.ico"
!define MUI_UNICON "..\src\AudioSwitch\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 AudioSwitch ${APP_VERSION}${FLAVOR_LABEL}"
!ifdef LITE
  !define MUI_WELCOMEPAGE_TEXT "AudioSwitch 是一个常驻托盘的 Windows 音频输出切换工具。$\r$\n$\r$\n这是精简版：程序本体只有约 1 MB，但需要系统已安装 .NET 9 桌面运行时（x64）。安装程序会先检查运行时，缺少时会给出下载地址。$\r$\n$\r$\n安装会请求管理员权限（Windows 会弹出 UAC 确认框），以确保文件写入不会被安全策略拦截。程序安装在当前用户目录中，不修改系统目录。$\r$\n$\r$\n点击“下一步”继续。"
!else
  !define MUI_WELCOMEPAGE_TEXT "AudioSwitch 是一个常驻托盘的 Windows 音频输出切换工具。$\r$\n$\r$\n安装会请求管理员权限（Windows 会弹出 UAC 确认框），以确保文件写入不会被安全策略拦截。程序安装在当前用户目录中，不修改系统目录。$\r$\n$\r$\n点击“下一步”继续。"
!endif
!define MUI_DIRECTORYPAGE_TEXT_TOP "安装程序已请求管理员权限（UAC）。默认装到当前用户目录 $\"$LOCALAPPDATA\Programs\AudioSwitch$\"，不修改系统目录；也可以用下面的按钮换到其他位置。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 AudioSwitch"
!define MUI_FINISHPAGE_LINK "打开项目说明（README）"
!define MUI_FINISHPAGE_LINK_LOCATION "$INSTDIR\README.md"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

!ifdef LITE
LangString DESC_CORE ${LANG_SIMPCHINESE} "AudioSwitch 主程序（精简版，需要已安装 .NET 9 桌面运行时）。"
!else
LangString DESC_CORE ${LANG_SIMPCHINESE} "AudioSwitch 主程序与运行库（必需）。"
!endif
LangString DESC_DESKTOP ${LANG_SIMPCHINESE} "在桌面创建 AudioSwitch 快捷方式。"
LangString DESC_STARTUP ${LANG_SIMPCHINESE} "登录 Windows 时在托盘静默启动，不显示窗口。"

!macro LOG_TEXT TEXT
  FileWrite $9 "${TEXT}$\r$\n"
!macroend

!ifdef LITE
; 精简版依赖 .NET 9 桌面运行时（x64）；按标准安装目录检查 9.x 版本
Function CheckDotNetRuntime
  StrCpy $R4 0

  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\9.*"
  ${If} $1 != ""
    StrCpy $R4 1
  ${EndIf}
  FindClose $0

  ${If} $R4 != 1
    FindFirst $0 $1 "$LOCALAPPDATA\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App\9.*"
    ${If} $1 != ""
      StrCpy $R4 1
    ${EndIf}
    FindClose $0
  ${EndIf}

  ${If} $R4 != 1
    FileOpen $9 "${INSTALL_LOG}" a
    ${If} $9 != ""
      !insertmacro LOG_TEXT "missing: .NET 9 Desktop Runtime (x64)"
      FileClose $9
    ${EndIf}
    MessageBox MB_ICONSTOP|MB_OK ".NET 9 桌面运行时（x64）未安装，精简版无法运行。$\r$\n$\r$\n请先安装运行时：$\r$\nhttps://dotnet.microsoft.com/download/dotnet/9.0$\r$\n$\r$\n或者改用“完整版”安装包（自带运行时，无需额外安装）。"
    Abort
  ${EndIf}
FunctionEnd
!endif

Function .onInit
  InitPluginsDir

  !ifdef LITE
    Call CheckDotNetRuntime
  !endif

  ; 只允许一个安装实例
  System::Call 'kernel32::CreateMutex(p 0, i 0, t "AudioSwitchSetupMutex") p .r1 ?e'
  Pop $R0
  ${If} $R0 != 0
    MessageBox MB_ICONSTOP|MB_OK "安装程序已经在运行。"
    Abort
  ${EndIf}

  FileOpen $9 "${INSTALL_LOG}" w
  ${If} $9 != ""
    !insertmacro LOG_TEXT "AudioSwitch ${APP_VERSION} installer log"
    FileClose $9
  ${EndIf}
FunctionEnd

; 结束正在运行的实例；返回后如果进程仍在，则提示用户手动退出
Function EnsureNotRunning
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0
  Sleep 600

  nsExec::Exec 'cmd /c tasklist /FI "IMAGENAME eq ${APP_EXE}" /NH | find /I "${APP_EXE}" >nul'
  Pop $0
  ${If} $0 == 0
    MessageBox MB_ICONSTOP|MB_OK "AudioSwitch 正在运行，安装程序无法替换文件。$\r$\n$\r$\n请先在托盘图标上右键退出（或按 Ctrl+Shift+Esc 在任务管理器中结束 AudioSwitch.exe），再点击“重试”。"
    Abort
  ${EndIf}
FunctionEnd

; 当前进程是否以低权限（低完整性级别）运行：
; 从浏览器的下载列表/受保护视图直接启动的 exe 会处于低完整性，写不了 %LOCALAPPDATA%，
; 系统会把它的 %TEMP% 重定向到 ...\Temp\Low。
Function IsLowIntegrity
  StrCpy $R3 0
  ${If} "$TEMP" == "$LOCALAPPDATA\Temp\Low"
    StrCpy $R3 1
  ${EndIf}
FunctionEnd

; 以管理员身份重新启动自身，然后退出
Function RelaunchElevated
  ExecShell "runas" "$EXEPATH" "" SW_SHOWNORMAL
  Quit
FunctionEnd

; 检查安装目录与临时目录是否可写；不可写时尝试提权或给出明确指引
Function EnsureWritable
  Call IsLowIntegrity
  ${If} $R3 == 1
    ${If} ${Silent}
      FileOpen $9 "${INSTALL_LOG}" a
      ${If} $9 != ""
        !insertmacro LOG_TEXT "warning: low integrity context (TEMP=$TEMP), continuing in silent mode"
        FileClose $9
      ${EndIf}
    ${Else}
      MessageBox MB_YESNO|MB_ICONQUESTION "安装程序当前以“低权限”运行（通常是因为从浏览器的下载列表直接打开）。$\r$\n$\r$\n在这种权限下，Windows 不允许写入 $LOCALAPPDATA，所以安装一定会失败。$\r$\n$\r$\n是否以管理员身份重新启动安装程序？" IDYES relaunch IDNO abort_low
      relaunch:
        Call RelaunchElevated
      abort_low:
        Abort
    ${EndIf}
  ${EndIf}

  ; 临时目录可写性（解压负载需要）
  CreateDirectory "$PLUGINSDIR\payload"
  FileOpen $9 "$PLUGINSDIR\write-test.tmp" w
  ${If} $9 == ""
    ${If} ${Silent}
      Abort
    ${EndIf}
    MessageBox MB_ICONSTOP|MB_OK "临时目录不可写，无法解压安装文件：$\r$\n$TEMP$\r$\n$\r$\n请检查磁盘空间或安全策略后重试。"
    Abort
  ${EndIf}
  FileClose $9
  Delete "$PLUGINSDIR\write-test.tmp"

  ; 安装目录可写性（在真正复制前就给出明确提示，避免复制到一半才失败）
  CreateDirectory "$INSTDIR"
  FileOpen $9 "$INSTDIR\write-test.tmp" w
  ${If} $9 == ""
    ${If} ${Silent}
      Abort
    ${EndIf}
    MessageBox MB_YESNO|MB_ICONQUESTION "无法写入安装目录：$\r$\n$INSTDIR$\r$\n$\r$\n常见原因是安全软件/组策略拦截，或该目录由管理员创建过。$\r$\n$\r$\n是否以管理员身份重新启动安装程序？" IDYES relaunch2 IDNO abort_dir
    relaunch2:
      Call RelaunchElevated
    abort_dir:
      Abort
  ${EndIf}
  FileClose $9
  Delete "$INSTDIR\write-test.tmp"
FunctionEnd

; 用 robocopy 复制负载，失败自动重试；返回成功/失败
Function CopyPayload
  StrCpy $R1 0
  ${Do}
    IntOp $R1 $R1 + 1
    nsExec::ExecToStack '"$SYSDIR\robocopy.exe" "$PLUGINSDIR\payload" "$INSTDIR" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP'
    Pop $0
    Pop $2
    FileOpen $9 "${INSTALL_LOG}" a
    ${If} $9 != ""
      !insertmacro LOG_TEXT "robocopy attempt $R1 exit=$0"
      !insertmacro LOG_TEXT "$2"
      FileClose $9
    ${EndIf}
    ${If} $0 < 8
      StrCpy $R2 1
      ${ExitDo}
    ${EndIf}
    Sleep 1200
  ${LoopUntil} $R1 >= 4
FunctionEnd

Section "AudioSwitch 程序（必需）" SEC_CORE
  SectionIn RO

  Call EnsureNotRunning
  Call EnsureWritable

  ; 先在临时目录展开负载，再复制，避免逐个文件写入时被安全软件锁定导致整体失败
  SetOutPath "$PLUGINSDIR\payload"
  File /r "${PAYLOAD_DIR}\*.*"
  File "..\README.md"

  CreateDirectory "$INSTDIR"
  StrCpy $R2 0
  Call CopyPayload
  ${If} $R2 != 1
    MessageBox MB_ICONSTOP|MB_OK "复制程序文件失败。$\r$\n$\r$\n这通常是安全软件（Defender、第三方杀毒或公司安全策略）在复制过程中锁定文件导致的。$\r$\n$\r$\n可以尝试：$\r$\n1. 把安装目录换成非系统盘，例如 D:\Tools\AudioSwitch；$\r$\n2. 临时关闭杀毒软件的实时防护后重试；$\r$\n3. 直接解压 AudioSwitch-Portable-${APP_VERSION}.zip 到任意文件夹使用（不需要安装）。$\r$\n$\r$\n详细日志：${INSTALL_LOG}"
    Abort
  ${EndIf}

  ; 校验关键文件确实落地
  ClearErrors
  IfFileExists "$INSTDIR\${APP_EXE}" 0 missing_core
  !ifndef LITE
    IfFileExists "$INSTDIR\Accessibility.dll" 0 missing_core
  !endif
  Goto core_ok
  missing_core:
    MessageBox MB_ICONSTOP|MB_OK "安装后的文件不完整（关键文件缺失）。$\r$\n$\r$\n这通常是安全软件删除了刚写入的文件。请把安装目录加入白名单，或改用便携版 zip。$\r$\n$\r$\n详细日志：${INSTALL_LOG}"
    Abort
  core_ok:

  FileOpen $9 "${INSTALL_LOG}" a
  ${If} $9 != ""
    !insertmacro LOG_TEXT "payload copied to $INSTDIR"
    FileClose $9
  ${EndIf}

  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\AudioSwitch"
  CreateShortCut "$SMPROGRAMS\AudioSwitch\AudioSwitch.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\AudioSwitch\卸载 AudioSwitch.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\AudioSwitch" "InstallDir" "$INSTDIR"

  WriteRegStr HKCU "${APP_REGKEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${APP_REGKEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${APP_REGKEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${APP_REGKEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${APP_REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${APP_REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${APP_REGKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${APP_REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${APP_REGKEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${APP_REGKEY}" "EstimatedSize" "$0"
SectionEnd

Section "创建桌面快捷方式" SEC_DESKTOP
  CreateShortCut "$DESKTOP\AudioSwitch.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "开机自动启动" SEC_STARTUP
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "AudioSwitch" '"$INSTDIR\${APP_EXE}" --tray'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CORE} $(DESC_CORE)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} $(DESC_DESKTOP)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTUP} $(DESC_STARTUP)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'

  Delete "$SMPROGRAMS\AudioSwitch\AudioSwitch.lnk"
  Delete "$SMPROGRAMS\AudioSwitch\卸载 AudioSwitch.lnk"
  RMDir "$SMPROGRAMS\AudioSwitch"

  Delete "$DESKTOP\AudioSwitch.lnk"

  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "AudioSwitch"
  ${If} $0 == '"$INSTDIR\${APP_EXE}" --tray'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "AudioSwitch"
  ${EndIf}

  DeleteRegKey HKCU "${APP_REGKEY}"
  DeleteRegKey HKCU "Software\AudioSwitch"

  RMDir /r "$INSTDIR"
SectionEnd
