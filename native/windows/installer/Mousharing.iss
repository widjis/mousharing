#ifndef AppVersion
  #error AppVersion required
#endif
#ifndef PublishDir
  #error PublishDir required
#endif
#ifndef OutputPath
  #error OutputPath required
#endif
[Setup]
AppId={{950DDB0A-E346-4B0E-B8E2-B81D71163D63}
AppMutex=Local\Mousharing.Native.Windows
AppName=Mousharing
AppVersion={#AppVersion}
AppPublisher=Widjis
AppPublisherURL=https://github.com/widjis/mousharing
AppSupportURL=https://github.com/widjis/mousharing/issues
DefaultDirName={localappdata}\Programs\Mousharing
DefaultGroupName=Mousharing
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
OutputDir={#OutputPath}
OutputBaseFilename=Mousharing-{#AppVersion}-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Mousharing.Windows.exe
CloseApplications=yes
CloseApplicationsFilter=Mousharing.Windows.exe
RestartApplications=no
SetupLogging=yes
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\Mousharing"; Filename: "{app}\Mousharing.Windows.exe"
Name: "{autodesktop}\Mousharing"; Filename: "{app}\Mousharing.Windows.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Mousharing.Windows.exe"; Description: "Open Mousharing"; Flags: nowait postinstall skipifsilent unchecked
; Intentionally no config deletion, firewall changes, startup registration or pairing.
