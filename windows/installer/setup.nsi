Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "WordFunc.nsh"
!ifndef APP_VERSION
!define APP_VERSION "1.0.1.0"
!endif
!ifndef APP_EXE
!error "APP_EXE required"
!endif
!ifndef OUTPUT_FILE
!error "OUTPUT_FILE required"
!endif
!define PRODUCT "Star Codex Switcher"
!ifndef REGKEY
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexAccountSwitcher"
!endif
!ifndef SHORTCUT
!define SHORTCUT "Star Codex Switcher"
!endif
!ifndef LEGACY_SHORTCUT
!define LEGACY_SHORTCUT "Codex Account Switcher"
!endif
Name "${PRODUCT}"
OutFile "${OUTPUT_FILE}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Star Codex Switcher"
InstallDirRegKey HKCU "${REGKEY}" "InstallLocation"
SetCompressor /SOLID lzma
CRCCheck force
VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT} 安装程序"
VIAddVersionKey /LANG=2052 "FileDescription" "Codex 账号切换器安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
!define MUI_ICON "..\CodexAccountSwitcher\app.ico"
!define MUI_UNICON "..\CodexAccountSwitcher\app.ico"
Icon "${MUI_ICON}"
UninstallIcon "${MUI_UNICON}"
Var UpdateMode
Var Params
Var ExistingVersion
Var LegacyMode
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 Star Codex Switcher"
!define MUI_WELCOMEPAGE_TEXT "仅支持 Codex 账号管理与切换。$\r$\n$\r$\n安装在当前用户目录，无需管理员权限。已有账号和登录数据会保留。$\r$\n$\r$\n继续前请先完成或取消切换器中正在进行的操作。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\StarCodexSwitcher.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 Star Codex Switcher"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 Windows x64。" /SD IDOK
    SetErrorLevel 11
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "需要 Windows 10 或更新版本。" /SD IDOK
    SetErrorLevel 11
    Quit
  ${EndIf}
  StrCpy $UpdateMode 0
  ${GetParameters} $Params
  ClearErrors
  ${GetOptions} $Params "/UPDATE" $0
  ${IfNot} ${Errors}
    StrCpy $UpdateMode 1
  ${EndIf}
FunctionEnd

Section "Codex 账号切换器" SecApp
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $ExistingVersion HKCU "${REGKEY}" "DisplayVersion"
  ${If} $ExistingVersion != ""
    ${VersionCompare} $ExistingVersion "${APP_VERSION}" $0
    ${If} $0 == 1
      MessageBox MB_OK "已安装更新的版本，不能降级覆盖。" /SD IDOK
      SetErrorLevel 15
      Abort
    ${EndIf}
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=StarCodexSwitcher.exe "${APP_EXE}"
  ; Graceful IPC shutdown only; busy tasks and legacy switchers make setup stop safely.
  ClearErrors
  ExecWait '"$PLUGINSDIR\StarCodexSwitcher.exe" --prepare-install' $0
  ${If} ${Errors}
    SetErrorLevel 10
    Abort
  ${EndIf}
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先从托盘退出旧切换器，或等待当前操作完成后再安装。账号数据不会被删除。" /SD IDOK
    SetErrorLevel 10
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  StrCpy $LegacyMode 0
  ${If} ${FileExists} "$INSTDIR\CodexAccountSwitcher.exe"
    StrCpy $LegacyMode 1
    ${If} ${FileExists} "$INSTDIR\StarCodexSwitcher.exe"
      MessageBox MB_OK|MB_ICONEXCLAMATION "安装目录同时存在新旧程序。请先启动新程序完成迁移，或选择其他目录。已有文件未修改。" /SD IDOK
      SetErrorLevel 17
      Abort
    ${EndIf}
  ${EndIf}
  ClearErrors
  CopyFiles /SILENT "$PLUGINSDIR\StarCodexSwitcher.exe" "$INSTDIR\StarCodexSwitcher.exe.new"
  ${If} ${Errors}
    SetErrorLevel 12
    Abort
  ${EndIf}
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe.new"
  ${If} ${Errors}
    Delete "$INSTDIR\StarCodexSwitcher.exe.new"
    Delete "$INSTDIR\Uninstall.exe.new"
    SetErrorLevel 16
    Abort
  ${EndIf}
  Delete "$INSTDIR\StarCodexSwitcher.exe.previous"
  ${If} ${FileExists} "$INSTDIR\StarCodexSwitcher.exe"
    ClearErrors
    Rename "$INSTDIR\StarCodexSwitcher.exe" "$INSTDIR\StarCodexSwitcher.exe.previous"
    ${If} ${Errors}
      Delete "$INSTDIR\StarCodexSwitcher.exe.new"
      MessageBox MB_OK|MB_ICONEXCLAMATION "程序仍在使用，无法覆盖。请退出后重试。" /SD IDOK
      SetErrorLevel 13
      Abort
    ${EndIf}
  ${EndIf}
  ClearErrors
  Rename "$INSTDIR\StarCodexSwitcher.exe.new" "$INSTDIR\StarCodexSwitcher.exe"
  ${If} ${Errors}
    Rename "$INSTDIR\StarCodexSwitcher.exe.previous" "$INSTDIR\StarCodexSwitcher.exe"
    SetErrorLevel 14
    Abort
  ${EndIf}
  Delete "$INSTDIR\Uninstall.exe.previous"
  ClearErrors
  ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
    Rename "$INSTDIR\Uninstall.exe" "$INSTDIR\Uninstall.exe.previous"
  ${EndIf}
  ${IfNot} ${Errors}
    Rename "$INSTDIR\Uninstall.exe.new" "$INSTDIR\Uninstall.exe"
  ${EndIf}
  ${If} ${Errors}
    Delete "$INSTDIR\StarCodexSwitcher.exe"
    Rename "$INSTDIR\StarCodexSwitcher.exe.previous" "$INSTDIR\StarCodexSwitcher.exe"
    ${IfNot} ${FileExists} "$INSTDIR\Uninstall.exe"
      Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
    ${EndIf}
    SetErrorLevel 16
    Abort
  ${EndIf}
  ${If} $LegacyMode == 1
    ; Deployed 1.0.0 helpers verify and launch this filename. Keep an identical
    ; temporary alias until the new application has completed its startup.
    Delete "$INSTDIR\CodexAccountSwitcher.exe.previous"
    ClearErrors
    Rename "$INSTDIR\CodexAccountSwitcher.exe" "$INSTDIR\CodexAccountSwitcher.exe.previous"
    ${If} ${Errors}
      Delete "$INSTDIR\StarCodexSwitcher.exe"
      Rename "$INSTDIR\StarCodexSwitcher.exe.previous" "$INSTDIR\StarCodexSwitcher.exe"
      Delete "$INSTDIR\Uninstall.exe"
      Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
      SetErrorLevel 17
      Abort
    ${EndIf}
    ClearErrors
    CopyFiles /SILENT "$INSTDIR\StarCodexSwitcher.exe" "$INSTDIR\CodexAccountSwitcher.exe"
    ${If} ${Errors}
      Delete "$INSTDIR\CodexAccountSwitcher.exe"
      Rename "$INSTDIR\CodexAccountSwitcher.exe.previous" "$INSTDIR\CodexAccountSwitcher.exe"
      Delete "$INSTDIR\StarCodexSwitcher.exe"
      Rename "$INSTDIR\StarCodexSwitcher.exe.previous" "$INSTDIR\StarCodexSwitcher.exe"
      Delete "$INSTDIR\Uninstall.exe"
      Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
      SetErrorLevel 17
      Abort
    ${EndIf}
    ClearErrors
    WriteINIStr "$INSTDIR\.star-migration.ini" "Migration" "RegistryKey" "${REGKEY}"
    IfErrors migration_failed
    WriteINIStr "$INSTDIR\.star-migration.ini" "Migration" "Shortcut" "${SHORTCUT}"
    IfErrors migration_failed
    WriteINIStr "$INSTDIR\.star-migration.ini" "Migration" "LegacyShortcut" "${LEGACY_SHORTCUT}"
    IfErrors migration_failed
    WriteINIStr "$INSTDIR\.star-migration.ini" "Migration" "OldVersion" "$ExistingVersion"
    IfErrors migration_failed
    Delete "$INSTDIR\CodexAccountSwitcher.exe.previous"
    Goto migration_complete
    migration_failed:
      Delete "$INSTDIR\CodexAccountSwitcher.exe"
      Rename "$INSTDIR\CodexAccountSwitcher.exe.previous" "$INSTDIR\CodexAccountSwitcher.exe"
      Delete "$INSTDIR\StarCodexSwitcher.exe"
      Rename "$INSTDIR\StarCodexSwitcher.exe.previous" "$INSTDIR\StarCodexSwitcher.exe"
      Delete "$INSTDIR\Uninstall.exe"
      Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
      SetErrorLevel 17
      Abort
    migration_complete:
  ${Else}
    WriteRegStr HKCU "${REGKEY}" "DisplayName" "${PRODUCT}"
    WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\StarCodexSwitcher.exe"
  ${EndIf}
  Delete "$INSTDIR\StarCodexSwitcher.exe.previous"
  Delete "$INSTDIR\Uninstall.exe.previous"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${REGKEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  ; Preserve users' shortcut choices on update.
  ${If} $UpdateMode == 0
  ${AndIf} $LegacyMode == 0
    ${IfNot} ${FileExists} "$DESKTOP\${SHORTCUT}.lnk"
      CreateShortcut "$DESKTOP\${SHORTCUT}.lnk" "$INSTDIR\StarCodexSwitcher.exe"
    ${EndIf}
    ${IfNot} ${FileExists} "$SMPROGRAMS\${SHORTCUT}.lnk"
      CreateShortcut "$SMPROGRAMS\${SHORTCUT}.lnk" "$INSTDIR\StarCodexSwitcher.exe"
    ${EndIf}
  ${EndIf}
!ifndef TEST_MODE
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexAccountSwitcher"
  ${If} $LegacyMode == 0
    ${If} $0 == '$\"$INSTDIR\CodexAccountSwitcher.exe$\" --background'
      WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexAccountSwitcher" '$\"$INSTDIR\StarCodexSwitcher.exe$\" --background'
    ${EndIf}
  ${EndIf}
!endif
  SetErrorLevel 0
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd
Section "Uninstall"
  ${IfNot} ${FileExists} "$INSTDIR\StarCodexSwitcher.exe"
  ${AndIf} ${FileExists} "$INSTDIR\CodexAccountSwitcher.exe"
    ExecWait '"$INSTDIR\CodexAccountSwitcher.exe" --prepare-install' $0
    ${If} $0 != 0
      SetErrorLevel 10
      Abort
    ${EndIf}
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\StarCodexSwitcher.exe"
    ClearErrors
    ExecWait '"$INSTDIR\StarCodexSwitcher.exe" --prepare-install' $0
    ${If} ${Errors}
      SetErrorLevel 10
      Abort
    ${EndIf}
    ${If} $0 != 0
      MessageBox MB_OK "请先退出切换器后再卸载。" /SD IDOK
      SetErrorLevel 10
      Abort
    ${EndIf}
    ; The application verifies shortcut targets before removing owned entries.
    ClearErrors
    ExecWait '"$INSTDIR\StarCodexSwitcher.exe" --cleanup-install-shortcuts "${SHORTCUT}" "${LEGACY_SHORTCUT}"' $0
    ${If} ${Errors}
      SetErrorLevel 18
      Abort
    ${EndIf}
    ${If} $0 != 0
      SetErrorLevel 18
      Abort
    ${EndIf}
  ${EndIf}
  ; Delete named program files only. Never recurse into account storage or .codex.
  ClearErrors
  Delete "$INSTDIR\StarCodexSwitcher.exe"
  ${If} ${Errors}
    SetErrorLevel 13
    Abort
  ${EndIf}
  Delete "$INSTDIR\StarCodexSwitcher.exe.previous"
  Delete "$INSTDIR\StarCodexSwitcher.exe.new"
  Delete "$INSTDIR\CodexAccountSwitcher.exe"
  Delete "$INSTDIR\CodexAccountSwitcher.exe.previous"
  Delete "$INSTDIR\CodexAccountSwitcher.exe.new"
  Delete "$INSTDIR\.star-migration.ini"
  Delete "$INSTDIR\Uninstall.exe.new"
  Delete "$INSTDIR\Uninstall.exe.previous"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
!ifndef TEST_MODE
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexAccountSwitcher"
  ${If} $0 == '$\"$INSTDIR\StarCodexSwitcher.exe$\" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexAccountSwitcher"
  ${ElseIf} $0 == '$\"$INSTDIR\CodexAccountSwitcher.exe$\" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexAccountSwitcher"
  ${EndIf}
!endif
  DeleteRegKey HKCU "${REGKEY}"
  SetErrorLevel 0
SectionEnd
