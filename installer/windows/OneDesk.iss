; OneDesk Windows host installer.
; Build with: powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1

#define MyAppName "OneDesk"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "OneDesk"
#define MyAppExeName "OneDesk.exe"

[Setup]
AppId={{8C3E0F1A-6B2D-4A77-9E3C-1F5D7A9B2C44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\OneDesk
DefaultGroupName=OneDesk
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=OneDesk-Setup-{#MyAppVersion}
SetupIconFile=..\..\assets\OneDesk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\OneDesk.Host
DisableWelcomePage=no
LicenseFile=..\..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OneDesk"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,OneDesk}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\OneDesk"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Replace any rule left by an older installation.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OneDesk"""; Flags: runhidden waituntilterminated
; Allow the Mac client to connect on private and domain networks only.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""OneDesk"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=private,domain"; Flags: runhidden waituntilterminated; StatusMsg: "配置 Windows 防火墙..."
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,OneDesk}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OneDesk"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallRule"
