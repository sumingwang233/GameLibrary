Unicode True
ManifestDPIAware True
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"
!insertmacro GetParent

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
; 静默安装（/S）遇到旧版时的默认选择：默认卸载旧版，保证无人值守升级干净覆盖。
!ifndef OLD_VERSION_SILENT_ANSWER
  !define OLD_VERSION_SILENT_ANSWER IDYES
!endif

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"

Name "${PRODUCT_NAME} ${VERSION}"
!ifdef UI_PREVIEW
Caption "$(GL_Text0)"
!else
Caption "$(GL_Text1)"
!endif
OutFile "${OUTPUT_FILE}"
!ifdef TEST_BUILD
InstallDir "${PAYLOAD_DIR}\..\default install"
!else
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
!endif
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
Icon "${INSTALLER_ICON}"
UninstallIcon "${INSTALLER_ICON}"
SetFont "Microsoft YaHei UI" 9
BrandingText /TRIMRIGHT "$(GL_Text2)"

Var WizardDialog
Var UpgradeRadio
Var RemovePrevious
Var PreviousUninstaller
Var PreviousLocation
Var PreviousVersion
Var HeaderBitmap

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright 2026 GameLibrary contributors"
VIAddVersionKey /LANG=1028 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1028 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1028 "FileDescription" "${PRODUCT_NAME} 安裝程式"
VIAddVersionKey /LANG=1028 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1028 "LegalCopyright" "Copyright 2026 GameLibrary contributors"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 GameLibrary contributors"
VIAddVersionKey /LANG=1041 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1041 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1041 "FileDescription" "${PRODUCT_NAME} セットアップ"
VIAddVersionKey /LANG=1041 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "Copyright 2026 GameLibrary contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${INSTALLER_ICON}"
!define MUI_UNICON "${INSTALLER_ICON}"
!define MUI_BGCOLOR "FFFFFF"
!define MUI_TEXTCOLOR "1F2937"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "${__FILEDIR__}\header.bmp"
!define MUI_CUSTOMFUNCTION_GUIINIT SmoothHeader
!define MUI_CUSTOMFUNCTION_UNGUIINIT un.SmoothHeader
!define MUI_WELCOMEFINISHPAGE_BITMAP "${__FILEDIR__}\wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${__FILEDIR__}\wizard.bmp"
!define MUI_INSTFILESPAGE_COLORS "1F2937 FFFFFF"
!ifdef UI_PREVIEW
  !define MUI_FINISHPAGE_TITLE "$(GL_Text4)"
  !define MUI_FINISHPAGE_TEXT "界面预览已完成。$\r$\n$\r$\n此预览没有安装程序、创建快捷方式或修改卸载登记。"
!else
!define MUI_FINISHPAGE_TITLE "$(GL_Text6)"
!define MUI_FINISHPAGE_RUN "$INSTDIR\GameLibrary.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(GL_Text7)"
!endif
!define MUI_FINISHPAGE_LINK "$(GL_Text8)"
!define MUI_FINISHPAGE_LINK_LOCATION "https://github.com/sumingwang233/GameLibrary"

Page custom WelcomePage
Page custom UpgradePage UpgradePageLeave

!define MUI_PAGE_HEADER_TEXT "$(GL_Text9)"
!define MUI_PAGE_HEADER_SUBTEXT "$(GL_Text10)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(GL_Text11)"
!define MUI_LICENSEPAGE_RADIOBUTTONS
!define MUI_LICENSEPAGE_RADIOBUTTONS_TEXT_ACCEPT "$(GL_Text12)"
!define MUI_LICENSEPAGE_RADIOBUTTONS_TEXT_DECLINE "$(GL_Text13)"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW StylePage
!insertmacro MUI_PAGE_LICENSE "${__FILEDIR__}\..\..\LICENSE"

!define MUI_PAGE_HEADER_TEXT "$(GL_Text14)"
!define MUI_PAGE_HEADER_SUBTEXT "$(GL_Text15)"
!define MUI_DIRECTORYPAGE_TEXT_TOP "$(GL_Text16)"
!define MUI_DIRECTORYPAGE_BGCOLOR "FFFFFF"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW StylePage
!insertmacro MUI_PAGE_DIRECTORY

Page custom ReadyPage
!define MUI_PAGE_CUSTOMFUNCTION_SHOW StylePage
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_SHOW SmoothFinish
!insertmacro MUI_PAGE_FINISH

!ifndef UI_PREVIEW
!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.StylePage
!insertmacro MUI_UNPAGE_CONFIRM
!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.StylePage
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_SHOW un.SmoothFinish
!insertmacro MUI_UNPAGE_FINISH
!endif

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Japanese"
!include "${__FILEDIR__}\\Languages.nsh"
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_LANGDLL_WINDOWTITLE "GameLibrary · 语言 / Language"
!define MUI_LANGDLL_INFO "请选择语言 / Select a language / 言語を選択してください"
!insertmacro MUI_RESERVEFILE_LANGDLL

; LoadImage /RESIZETOFIT does not provide filtered resampling. Render the 4x
; artwork into a control-sized bitmap using GDI HALFTONE, including on high DPI.
!macro SmoothBranding PREFIX
Function ${PREFIX}SmoothBitmap
  System::Store "S"
  Pop $3 ; source height
  Pop $2 ; source width
  Pop $1 ; bitmap path
  Pop $0 ; image control
  SendMessage $0 ${STM_GETIMAGE} ${IMAGE_BITMAP} 0 $R9
  System::Call 'user32::LoadImage(p0,tr1,i${IMAGE_BITMAP},i0,i0,i${LR_LOADFROMFILE})p.r4'
  System::Call 'user32::GetDC(pr0)p.r5'
  System::Call 'gdi32::CreateCompatibleDC(pr5)p.r6'
  System::Call 'gdi32::CreateCompatibleDC(pr5)p.r7'
  System::Alloc 16
  Pop $8
  System::Call 'user32::GetClientRect(pr0,pr8)'
  System::Call '*$8(i,i,i.r9,i.R0)'
  System::Free $8
  System::Call 'gdi32::CreateCompatibleBitmap(pr5,ir9,iR0)p.R1'
  ${If} $4 P<> 0
  ${AndIf} $5 P<> 0
  ${AndIf} $6 P<> 0
  ${AndIf} $7 P<> 0
  ${AndIf} $R1 P<> 0
    System::Call 'gdi32::SelectObject(pr6,pr4)p.R2'
    System::Call 'gdi32::SelectObject(pr7,pR1)p.R3'
    System::Call 'gdi32::SetStretchBltMode(pr7,i4)' ; HALFTONE
    System::Call 'gdi32::SetBrushOrgEx(pr7,i0,i0,p0)'
    System::Call 'gdi32::StretchBlt(pr7,i0,i0,ir9,iR0,pr6,i0,i0,ir2,ir3,i0x00CC0020)i.R4'
    System::Call 'gdi32::SelectObject(pr6,pR2)'
    System::Call 'gdi32::SelectObject(pr7,pR3)'
    ${If} $R4 != 0
      SendMessage $0 ${STM_SETIMAGE} ${IMAGE_BITMAP} $R1 $R9
      System::Call 'gdi32::DeleteObject(pR9)'
      StrCpy $R9 $R1
      StrCpy $R1 0 ; ownership passes to the page
    ${EndIf}
  ${EndIf}
  System::Call 'gdi32::DeleteObject(pR1)'
  System::Call 'gdi32::DeleteObject(pr4)'
  System::Call 'gdi32::DeleteDC(pr6)'
  System::Call 'gdi32::DeleteDC(pr7)'
  System::Call 'user32::ReleaseDC(pr0,pr5)'
  Push $R9
  System::Store "L"
FunctionEnd

Function ${PREFIX}SmoothHeader
  Push $mui.Header.Image
  Push "$PLUGINSDIR\modern-header.bmp"
  Push 600
  Push 228
  Call ${PREFIX}SmoothBitmap
  Pop $HeaderBitmap
FunctionEnd

Function ${PREFIX}SmoothFinish
  Push $mui.FinishPage.Image
  Push "$PLUGINSDIR\modern-wizard.bmp"
  Push 656
  Push 1256
  Call ${PREFIX}SmoothBitmap
  Pop $mui.FinishPage.Image.Bitmap ; MUI releases this when the page is destroyed
FunctionEnd

!if "${PREFIX}" == ""
Function .onGUIEnd
!else
Function un.onGUIEnd
!endif
  ${NSD_FreeImage} $HeaderBitmap
FunctionEnd
!macroend
!insertmacro SmoothBranding ""
!ifndef UI_PREVIEW
!insertmacro SmoothBranding "un."
!endif

; 只改变页面颜色，保留系统按钮、输入框、焦点和 DPI 缩放行为。
!macro WhitePageFunction PREFIX
Function ${PREFIX}StylePage
  Push $0
  Push $1
  FindWindow $0 "#32770" "" $HWNDPARENT
  SetCtlColors $0 "1F2937" "FFFFFF"
  System::Call 'user32::GetWindow(p r0, i 5) p .r1'
  ${While} $1 != 0
    SetCtlColors $1 "1F2937" "FFFFFF"
    System::Call 'user32::GetWindow(p r1, i 2) p .r1'
  ${EndWhile}
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro WhitePageFunction ""
!ifndef UI_PREVIEW
!insertmacro WhitePageFunction "un."
!endif

Function CreateWizardPage
  nsDialogs::Create 1018
  Pop $WizardDialog
  ${If} $WizardDialog == error
    Abort
  ${EndIf}
  SetCtlColors $WizardDialog "1F2937" "FFFFFF"
FunctionEnd

Function WelcomePage
  Call CreateWizardPage
  !insertmacro MUI_HEADER_TEXT "$(GL_Text17)" "$(GL_Text18)"
  ${NSD_CreateLabel} 0 12u 100% 18u "${PRODUCT_NAME} ${VERSION}"
  Pop $0
  ${NSD_CreateLabel} 0 40u 100% 34u "$(GL_Text19)"
  Pop $0
  ${NSD_CreateLabel} 0 86u 100% 30u "$(GL_Text20)"
  Pop $0
  Call StylePage
  ${NSD_CreateLink} 0 132u 100% 12u "github.com/sumingwang233/GameLibrary"
  Pop $0
  ${NSD_OnClick} $0 OpenProject
  nsDialogs::Show
FunctionEnd

Function OpenProject
  Pop $0
  ExecShell "open" "https://github.com/sumingwang233/GameLibrary"
FunctionEnd

Function FindPreviousInstall
  ReadRegStr $PreviousUninstaller HKCU "${UNINSTALL_KEY}" "UninstallString"
  ReadRegStr $PreviousLocation HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ReadRegStr $PreviousVersion HKCU "${UNINSTALL_KEY}" "DisplayVersion"
  ; 沿用旧版引号剥离规则，FileExists 和 ExecWait 均使用裸路径。
  StrCpy $R1 $PreviousUninstaller 1
  ${If} $R1 == '$\"'
    StrCpy $PreviousUninstaller $PreviousUninstaller "" 1
    StrLen $R1 $PreviousUninstaller
    IntOp $R1 $R1 - 1
    StrCpy $R2 $PreviousUninstaller "" $R1
    ${If} $R2 == '$\"'
      StrCpy $PreviousUninstaller $PreviousUninstaller $R1
    ${EndIf}
  ${EndIf}
  ${IfNot} ${FileExists} "$PreviousUninstaller"
    StrCpy $PreviousUninstaller ""
  ${ElseIf} $PreviousLocation == ""
    ${GetParent} $PreviousUninstaller $PreviousLocation
  ${EndIf}
FunctionEnd

Function .onInit
  StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
  ReadRegStr $R0 HKCU "${UNINSTALL_KEY}" "InstallerLanguage"
  ${If} $R0 != ""
    StrCpy $LANGUAGE $R0
  ${EndIf}
  ${GetParameters} $R0
  ${GetOptions} $R0 "/LANG=" $R1
  ${If} $R1 == "2052"
  ${OrIf} $R1 == "1028"
  ${OrIf} $R1 == "1033"
  ${OrIf} $R1 == "1041"
    StrCpy $LANGUAGE $R1
  ${Else}
    !insertmacro MUI_LANGDLL_DISPLAY
  ${EndIf}
  StrCpy $RemovePrevious 1
  !if "${OLD_VERSION_SILENT_ANSWER}" == "IDNO"
    StrCpy $RemovePrevious 0
  !endif
  Call FindPreviousInstall
  !ifdef UI_PREVIEW
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\GameLibrary"
    !ifdef PREVIEW_OLD_VERSION
      ; 模拟旧版页面；预览构建不会执行任何卸载器。
      StrCpy $PreviousUninstaller "preview"
      StrCpy $PreviousLocation "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
      StrCpy $PreviousVersion "$(GL_Text21)"
    !endif
  !endif
FunctionEnd

Function UpgradePage
  ${If} $PreviousUninstaller == ""
    Abort
  ${EndIf}
  Call CreateWizardPage
  !insertmacro MUI_HEADER_TEXT "$(GL_Text22)" "$(GL_Text23)"
  ${NSD_CreateLabel} 0 8u 100% 32u "$(GL_Text24)"
  Pop $0
  ${NSD_CreateRadioButton} 10u 52u 95% 16u "$(GL_Text25)"
  Pop $UpgradeRadio
  ${NSD_CreateRadioButton} 10u 80u 95% 16u "$(GL_Text26)"
  Pop $0
  ${If} $RemovePrevious == 1
    ${NSD_Check} $UpgradeRadio
  ${Else}
    ${NSD_Check} $0
  ${EndIf}
  ${NSD_CreateLabel} 0 120u 100% 28u "$(GL_Text27)"
  Pop $0
  Call StylePage
  nsDialogs::Show
FunctionEnd

Function UpgradePageLeave
  ${NSD_GetState} $UpgradeRadio $RemovePrevious
FunctionEnd

Function ReadyPage
  Call CreateWizardPage
  !insertmacro MUI_HEADER_TEXT "$(GL_Text28)" "$(GL_Text29)"
  StrCpy $1 "$(GL_Text30)"
  ${If} $PreviousUninstaller != ""
    ${If} $RemovePrevious == 1
      StrCpy $1 "$(GL_Text31)"
    ${Else}
      StrCpy $1 "$(GL_Text32)"
    ${EndIf}
  ${EndIf}
  nsDialogs::CreateControl "EDIT" ${DEFAULT_STYLES}|${WS_TABSTOP}|${ES_MULTILINE}|${ES_READONLY}|${ES_AUTOVSCROLL}|${WS_VSCROLL} ${WS_EX_CLIENTEDGE} 0 8u 100% 110u "$(GL_Text33)"
  Pop $0
  ${NSD_CreateLabel} 0 130u 100% 20u "$(GL_Text34)"
  Pop $0
  Call StylePage
  SendMessage $mui.Button.Next ${WM_SETTEXT} 0 "STR:$(GL_Text35)"
  nsDialogs::Show
FunctionEnd

Section "GameLibrary" SecMain
!ifdef UI_PREVIEW
  DetailPrint "$(GL_Text36)"
!else
  SetShellVarContext current

  ; v1.4.7：复制新文件前检测当前用户旧版卸载项，由用户选择是否先卸载，
  ; 避免覆盖安装后旧版残留文件与新版本冲突。游戏库数据目录不受卸载影响。
  ; 点击“安装”后重新核对卸载项；展示升级页面本身不会卸载程序。
  Call FindPreviousInstall
  ${If} $PreviousUninstaller != ""
      ${If} $RemovePrevious == 1
        DetailPrint "$(GL_Text37)"
        ; _?= 指定旧安装目录并要求卸载程序原地同步执行，ExecWait 等卸载完成后再复制新文件。
        ExecWait '"$PreviousUninstaller" /S _?=$PreviousLocation' $R3
        ${If} $R3 != 0
          MessageBox MB_OK|MB_ICONSTOP "卸载旧版 ${PRODUCT_NAME} 失败（退出码 $R3），安装已中止。$\r$\n请关闭正在运行的 ${PRODUCT_NAME} 后重新运行安装程序。" /SD IDOK
          Abort
        ${EndIf}
      ${Else}
        DetailPrint "$(GL_Text39)"
      ${EndIf}
  ${EndIf}

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
  File /oname=GameLibrary.TauriBridge.exe "${PAYLOAD_DIR}\GameLibrary.TauriBridge.exe"
  File /oname=GameLibrary.Host.exe "${PAYLOAD_DIR}\GameLibrary.Host.exe"
  File /oname=SHA256SUMS.txt "${PAYLOAD_DIR}\SHA256SUMS.txt"

  FileOpen $0 "$INSTDIR\.gamelibrary-install" w
  FileWrite $0 "${PRODUCT_ID} ${VERSION}$\r$\n"
  FileClose $0

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${START_MENU_FOLDER}"
  CreateShortcut "$SMPROGRAMS\${START_MENU_FOLDER}\${PRODUCT_NAME}.lnk" "$INSTDIR\GameLibrary.Desktop.exe" "" "$INSTDIR\GameLibrary.Desktop.exe" 0
  CreateShortcut "$SMPROGRAMS\${START_MENU_FOLDER}\$(GL_Text41).lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0

  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallerLanguage" $LANGUAGE
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "GameLibrary contributors"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\GameLibrary.Desktop.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/sumingwang233/GameLibrary"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Comments" "$(GL_Text40)"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  SectionGetSize ${SecMain} $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0

!ifndef TEST_BUILD
  StrCmp $INSTDIR "$LOCALAPPDATA\Programs\GameLibrary" 0 +2
  Delete "$LOCALAPPDATA\Programs\GameLibrary-Uninstall.ps1"
!endif
!endif
SectionEnd

!ifndef UI_PREVIEW
Function un.onInit
  StrCpy $LANGUAGE ${LANG_SIMPCHINESE}
  ReadRegStr $R0 HKCU "${UNINSTALL_KEY}" "InstallerLanguage"
  ${If} $R0 != ""
    StrCpy $LANGUAGE $R0
  ${EndIf}
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

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
  Delete "$SMPROGRAMS\${START_MENU_FOLDER}\卸載 ${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${START_MENU_FOLDER}\Uninstall ${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${START_MENU_FOLDER}\${PRODUCT_NAME} をアンインストール.lnk"
  RMDir "$SMPROGRAMS\${START_MENU_FOLDER}"

  Delete "$INSTDIR\GameLibrary.Desktop.exe"
  Delete "$INSTDIR\GameLibrary.TauriBridge.exe"
  Delete "$INSTDIR\GameLibrary.Host.exe"
  Delete "$INSTDIR\SHA256SUMS.txt"
  Delete "$INSTDIR\.gamelibrary-install"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
!endif
