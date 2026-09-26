#define MyAppName "Restaurant WiFi Control"
#define MyAppVersion "9.1.0"
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
OutputBaseFilename=Restaurant_WiFi_Control_V9_1_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "gateway\*"; DestDir: "{app}\Gateway"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Restaurant WiFi Control"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Restaurant WiFi Control"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{cmd}"; Parameters: "/C sc.exe stop RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe delete RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe create RestaurantWiFiGateway binPath= ""{app}\Gateway\RestaurantWiFiGateway.exe"" start= auto DisplayName= ""Restaurant WiFi Gateway"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Restaurant WiFi Portal"" >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall add rule name=""Restaurant WiFi Portal"" dir=in action=allow protocol=TCP localport=8088"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe start RestaurantWiFiGateway"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Restaurant WiFi Control"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C sc.exe stop RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe delete RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Restaurant WiFi Portal"" >nul 2>&1"; Flags: runhidden waituntilterminated
