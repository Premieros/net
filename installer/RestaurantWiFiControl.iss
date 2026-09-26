#define MyAppName "Restaurant WiFi Control"
#define MyAppVersion "9.0.0"
#define MyAppPublisher "Premieros"
#define MyAppExeName "RestaurantWiFiControl.exe"

[Setup]
AppId={{F0CB2B1D-AC94-4C50-8A8C-2DAF14E9A909}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Restaurant WiFi Control
DefaultGroupName=Restaurant WiFi Control
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=output
OutputBaseFilename=Restaurant_WiFi_Control_V9_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Restaurant WiFi Control"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Restaurant WiFi Control"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Restaurant WiFi Control"; Flags: nowait postinstall skipifsilent
