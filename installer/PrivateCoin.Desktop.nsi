Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !error "Defina APP_VERSION com a versão do executável."
!endif
!ifndef PAYLOAD_DIR
  !error "Defina PAYLOAD_DIR com os arquivos compilados."
!endif
!ifndef OUTPUT_FILE
  !error "Defina OUTPUT_FILE com o caminho do instalador."
!endif

Name "POVIX (PrivateCoin.Desktop)"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\PrivateCoin"
InstallDirRegKey HKCU "Software\PrivateCoin\Desktop" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=1046 "ProductName" "PrivateCoin.Desktop"
VIAddVersionKey /LANG=1046 "FileDescription" "Instalador do POVIX Desktop"
VIAddVersionKey /LANG=1046 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1046 "LegalCopyright" "PrivateCoin"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Este assistente instala o POVIX Desktop e verifica o .NET Framework 4.8.$\r$\n$\r$\nFeche o aplicativo antes de instalar uma atualização. Seus dados de carteira serão preservados.$\r$\n$\r$\nSe faltar o .NET, poderá ser solicitada autorização de administrador."
!define MUI_FINISHPAGE_TEXT "O POVIX Desktop foi instalado. Abra o aplicativo pelo Menu Iniciar.$\r$\n$\r$\nSe foi solicitado reiniciar o Windows, reinicie antes de abrir o aplicativo."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "O aplicativo será removido. As carteiras, a blockchain, os pares e os dados de recuperação serão preservados."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "PortugueseBR"

!macro CheckClosed PREFIX
  IfFileExists "$INSTDIR\PrivateCoin.Desktop.exe" 0 ${PREFIX}closed
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\PrivateCoin.Desktop.exe", i 0xC0000000, i 0, p 0, i 3, i 0, p 0) p .r0'
  ${If} $0 == -1
    MessageBox MB_OK|MB_ICONEXCLAMATION "Feche o POVIX Desktop e tente novamente. O executável está em uso ou não pode ser alterado." /SD IDOK
    SetErrorLevel 1618
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r0)'
  ${PREFIX}closed:
!macroend

Function .onInit
  SetShellVarContext current
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Este instalador requer Windows 10 ou Windows 11." /SD IDOK
    SetErrorLevel 1633
    Abort
  ${EndIf}
  ${If} ${IsNativeARM64}
    MessageBox MB_OK|MB_ICONSTOP "Este pacote é destinado ao Windows x86 ou x64." /SD IDOK
    SetErrorLevel 1633
    Abort
  ${EndIf}
  !insertmacro CheckClosed install_
FunctionEnd

Section "POVIX Desktop" MainSection
  ; Release >= 528040 means .NET Framework 4.8 or a later in-place version.
  SetRegView 32
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "Ensure-DotNet48.ps1"
    !ifdef DOTNET48_INSTALLER
      File /oname=ndp48-x86-x64-allos-enu.exe "${DOTNET48_INSTALLER}"
    !endif
    DetailPrint "Preparando o .NET Framework 4.8. Aguarde a conclusão."
    ClearErrors
    ExecShellWait "runas" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Ensure-DotNet48.ps1"' SW_SHOWNORMAL $0
    ${If} ${Errors}
      MessageBox MB_OK|MB_ICONSTOP "Não foi possível iniciar a instalação do .NET. Autorize a solicitação de administrador e tente novamente." /SD IDOK
      SetErrorLevel 1603
      Abort
    ${EndIf}
    ${If} $0 == 3010
    ${OrIf} $0 == 1641
      SetRebootFlag true
    ${ElseIf} $0 != 0
      MessageBox MB_OK|MB_ICONSTOP "Não foi possível instalar o .NET Framework 4.8 (código $0). Verifique a conexão e instale o .NET 4.8 pelo site da Microsoft antes de tentar novamente." /SD IDOK
      SetErrorLevel $0
      Abort
    ${Else}
      ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
      ${If} $0 < 528040
        MessageBox MB_OK|MB_ICONSTOP "O .NET Framework 4.8 não foi detectado. Reinicie o Windows após instalar o .NET e tente novamente." /SD IDOK
        SetErrorLevel 1603
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}

  SetOutPath "$INSTDIR"
  ClearErrors
  File "${PAYLOAD_DIR}/PrivateCoin.Desktop.exe"
  File "${PAYLOAD_DIR}/PrivateCoin.Core.dll"
  File "LEIA-ME.txt"
  ; Preserve local seeds and other user configuration during upgrades.
  SetOverwrite off
  File "${PAYLOAD_DIR}/PrivateCoin.Desktop.exe.config"
  SetOverwrite on
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Não foi possível copiar os arquivos. Verifique o espaço livre e feche o aplicativo antes de tentar novamente." /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\POVIX"
  CreateShortcut "$SMPROGRAMS\POVIX\POVIX Desktop.lnk" "$INSTDIR\PrivateCoin.Desktop.exe"
  CreateShortcut "$SMPROGRAMS\POVIX\Desinstalar.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\POVIX Desktop.lnk" "$INSTDIR\PrivateCoin.Desktop.exe"
  WriteRegStr HKCU "Software\PrivateCoin\Desktop" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "DisplayName" "POVIX (PrivateCoin.Desktop)"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "DisplayIcon" "$INSTDIR\PrivateCoin.Desktop.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop" "NoRepair" 1
  ${If} ${RebootFlag}
    SetErrorLevel 3010
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 32
  !insertmacro CheckClosed uninstall_
FunctionEnd

Section "Uninstall"
  ; Never remove a directory recursively: legacy wallets may live beside the EXE.
  Delete "$INSTDIR\PrivateCoin.Desktop.exe"
  Delete "$INSTDIR\PrivateCoin.Core.dll"
  Delete "$INSTDIR\PrivateCoin.Desktop.exe.config"
  Delete "$INSTDIR\LEIA-ME.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\POVIX\POVIX Desktop.lnk"
  Delete "$SMPROGRAMS\POVIX\Desinstalar.lnk"
  RMDir "$SMPROGRAMS\POVIX"
  Delete "$DESKTOP\POVIX Desktop.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PrivateCoin.Desktop"
  DeleteRegKey HKCU "Software\PrivateCoin\Desktop"
SectionEnd
