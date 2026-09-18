Unicode True
ManifestDPIAware True
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"

!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef FILE_VERSION
  !error "FILE_VERSION is required"
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef INSTALLER_ICON
  !error "INSTALLER_ICON is required"
!endif

!ifndef PRODUCT_NAME
  !define PRODUCT_NAME "GameLibrary"
!endif
!ifndef PRODUCT_ID
  !define PRODUCT_ID "GameLibrary"
!endif
!ifndef START_MENU_FOLDER
  !define START_MENU_FOLDER "GameLibrary"
!endif

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"

Name "${PRODUCT_NAME} ${VERSION}"
Caption "${PRODUCT_NAME} ${VERSION} 安装"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
Icon "${INSTALLER_ICON}"
UninstallIcon "${INSTALLER_ICON}"

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright 2026 GameLibrary contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${INSTALLER_ICON}"
!define MUI_UNICON "${INSTALLER_ICON}"
!define MUI_WELCOMEPAGE_TITLE "欢迎安装 ${PRODUCT_NAME}"
!define MUI_WELCOMEPAGE_TEXT "安装向导将把 ${PRODUCT_NAME} ${VERSION} 安装到当前 Windows 用户。$\r$\n$\r$\n你可以在下一步修改安装位置；安装过程不会打开终端窗口，也不需要管理员权限。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择 ${PRODUCT_NAME} 的程序安装位置。游戏库数据保存在独立的用户数据目录，卸载程序不会删除这些数据。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\GameLibrary.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${PRODUCT_NAME}"
!define MUI_FINISHPAGE_LINK "查看项目主页"
!define MUI_FINISHPAGE_LINK_LOCATION "https://github.com/sumingwang233/GameLibrary"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Section "GameLibrary" SecMain
  SetShellVarContext current
  SetOutPath "$INSTDIR"

!ifndef TEST_BUILD
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /F /IM GameLibrary.Desktop.exe'
  Pop $0
  Pop $1
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /F /IM GameLibrary.Host.exe'
  Pop $0
  Pop $1
!endif

  File /oname=GameLibrary.Desktop.exe "${PAYLOAD_DIR}\GameLibrary.Desktop.exe"
  File /oname=GameLibrary.Host.exe "${PAYLOAD_DIR}\GameLibrary.Host.exe"
  File /oname=SHA256SUMS.txt "${PAYLOAD_DIR}\SHA256SUMS.txt"

  FileOpen $0 "$INSTDIR\.gamelibrary-install" w
  FileWrite $0 "${PRODUCT_ID} ${VERSION}$\r$\n"
  FileClose $0

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${START_MENU_FOLDER}"
  CreateShortcut "$SMPROGRAMS\${START_MENU_FOLDER}\${PRODUCT_NAME}.lnk" "$INSTDIR\GameLibrary.Desktop.exe" "" "$INSTDIR\GameLibrary.Desktop.exe" 0
  CreateShortcut "$SMPROGRAMS\${START_MENU_FOLDER}\卸载 ${PRODUCT_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "GameLibrary contributors"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\GameLibrary.Desktop.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/sumingwang233/GameLibrary"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Comments" "本地优先的 Windows 游戏库管理器"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  SectionGetSize ${SecMain} $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0

!ifndef TEST_BUILD
  StrCmp $INSTDIR "$LOCALAPPDATA\Programs\GameLibrary" 0 +2
  Delete "$LOCALAPPDATA\Programs\GameLibrary-Uninstall.ps1"
!endif
SectionEnd

Section "Uninstall"
  SetShellVarContext current

!ifndef TEST_BUILD
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /F /IM GameLibrary.Desktop.exe'
  Pop $0
  Pop $1
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /F /IM GameLibrary.Host.exe'
  Pop $0
  Pop $1
!endif

  Delete "$SMPROGRAMS\${START_MENU_FOLDER}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${START_MENU_FOLDER}\卸载 ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${START_MENU_FOLDER}"

  Delete "$INSTDIR\GameLibrary.Desktop.exe"
  Delete "$INSTDIR\GameLibrary.Host.exe"
  Delete "$INSTDIR\SHA256SUMS.txt"
  Delete "$INSTDIR\.gamelibrary-install"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
