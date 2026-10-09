#define MyAppName "Restaurant WiFi Control"
#define MyAppVersion "9.2.0"
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
OutputBaseFilename=Restaurant_WiFi_Control_V9_2_Beta_Setup
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
; The gateway is stopped in CurStepChanged(ssInstall), BEFORE [Files] replaces its executable.
; Keep the existing service during upgrades rather than deleting it while running.
Filename: "{cmd}"; Parameters: "/C sc.exe query RestaurantWiFiGateway >nul 2>&1 || sc.exe create RestaurantWiFiGateway binPath= ""{app}\Gateway\RestaurantWiFiGateway.exe"" start= auto DisplayName= ""Restaurant WiFi Gateway"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe config RestaurantWiFiGateway binPath= ""{app}\Gateway\RestaurantWiFiGateway.exe"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Restaurant WiFi Portal"" >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall add rule name=""Restaurant WiFi Portal"" dir=in action=allow protocol=TCP localport=8088"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe start RestaurantWiFiGateway"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Restaurant WiFi Control"; Flags: nowait postinstall skipifsilent; BeforeInstall: WaitForGatewayRunning

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C sc.exe stop RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc.exe delete RestaurantWiFiGateway >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Restaurant WiFi Portal"" >nul 2>&1"; Flags: runhidden waituntilterminated

[Code]
procedure WaitForGatewayRunning();
var
  Attempt: Integer;
  ResultCode: Integer;
  QuerySucceeded: Boolean;
begin
  { Do not launch the desktop until Service Control Manager shows a running Gateway.
    The desktop also retries its named pipe independently. }
  for Attempt := 1 to 25 do
  begin
    QuerySucceeded := Exec(ExpandConstant('{cmd}'),
      '/C sc.exe query RestaurantWiFiGateway | findstr /R /C:"STATE.*RUNNING"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if QuerySucceeded and (ResultCode = 0) then Exit;
    Sleep(1000);
  end;
  MsgBox('Restaurant WiFi Gateway did not reach RUNNING after installation.' + #13#10 +
    'Open services.msc and review the Gateway service and Windows Event Viewer.' + #13#10 +
    'Setup will not launch the desktop until the service is fixed.',
    mbError, MB_OK);
  Abort;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Parameters: String;
begin
  if CurStep <> ssInstall then
    Exit;

  { Stop the existing service before overwriting locked files.
    Initial installations with no service are safe to proceed. }
  Parameters := '-NoProfile -NonInteractive -Command "' +
    '$ErrorActionPreference = ''Stop''; ' +
    '$s = Get-Service -Name ''RestaurantWiFiGateway'' -ErrorAction SilentlyContinue; ' +
    'if ($null -ne $s -and $s.Status -ne ''Stopped'') { ' +
    'Stop-Service -Name ''RestaurantWiFiGateway'' -Force; ' +
    '$s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(30)); ' +
    'if ($s.Status -ne ''Stopped'') { exit 1 } }"';

  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
  begin
    MsgBox('Cannot stop the existing Restaurant WiFi Gateway service. ' +
      'The installer will not overwrite running service files. ' +
      'Close the application and retry as administrator.',
      mbError, MB_OK);
    Abort;
  end;
end;
