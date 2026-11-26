; NotchSetup.exe (tools\release.ps1 derler):  ISCC /DAppVersion=1.5.0 tools\setup.iss
;
; Kurulum yalnizca imzali Notch.exe'yi kullanici klasorune kopyalar. Baslat menusu kisayolu, Windows
; "Uygulamalar" girdisi, kaldirma ve guncelleme uygulamanin kendisindedir (Services\Installer.cs);
; bu yuzden Inno kendi kaldiricisini olusturmaz.

#ifndef AppVersion
  #error AppVersion verilmedi (/DAppVersion=x.y.z)
#endif

[Setup]
AppName=Notch
AppVersion={#AppVersion}
AppPublisher=NotchHub contributors
AppPublisherURL=https://github.com/atak946/NotchHub-Windows
VersionInfoVersion={#AppVersion}
; Installer.InstallDir ile ayni olmali
DefaultDirName={localappdata}\Programs\Notch
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Uninstallable=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Calisan Notch varsa kapatilmasi istenir
AppMutex=Notch_SingleInstance
OutputDir=..\dist
OutputBaseFilename=NotchSetup
WizardStyle=modern
SolidCompression=yes

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\Notch.exe"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\Notch.exe"; Parameters: "--register"; Flags: runhidden
Filename: "{app}\Notch.exe"; Description: "{cm:LaunchProgram,Notch}"; Flags: nowait postinstall skipifsilent
