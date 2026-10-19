; OneDesk Windows client installer (the machine without the keyboard and mouse).
; Build with: powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1

#define MyAppName "OneDesk Client"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "OneDesk"
#define MyAppExeName "OneDeskClient.exe"

[Setup]
AppId={{A1D4E7B2-5C08-4F63-B9D1-7E2A4C6F8B15}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\OneDeskClient
DefaultGroupName=OneDesk Client
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=OneDeskClient-Setup-{#MyAppVersion}
SetupIconFile=..\..\assets\OneDesk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\OneDesk.Client
DisableWelcomePage=no
LicenseFile=..\..\LICENSE
; The only HKCU access is removing the autostart value on uninstall, which belongs to the
; user who ran the installer. Inno would otherwise warn about admin mode plus per-user areas.
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\publish\win-x64-client\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OneDesk Client"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,OneDesk Client}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\OneDesk Client"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; The app adds this itself when "登录 Windows 时自动启动" is enabled; do not leave a dangling entry.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OneDeskClient"; Flags: uninsdeletevalue

[Run]
; No firewall rule is needed: the client only makes outbound connections to the host.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,OneDesk Client}"; Flags: nowait postinstall skipifsilent
