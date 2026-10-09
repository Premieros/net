#define MyAppName "Restaurant WiFi Control"
#define MyAppVersion "9.2.3"
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
OutputBaseFilename=Restaurant_WiFi_Control_V9_2_3_Beta_Installer_Fix_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes
; Ask Windows Restart Manager to close the desktop before copying its EXE.
; Never silently force-close an administrator's application.
CloseApplications=yes
CloseApplicationsFilter=RestaurantWiFiControl.exe
RestartApplications=no

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "gateway\*"; DestDir: "{app}\Gateway"; Flags: ignoreversion recursesubdirs createallsubdirs
; Embedded for pre-copy service stop; no scripts are installed in Program Files.
Source: "StopGatewayForInstall.ps1"; Flags: dontcopy

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
  QuerySucceeded: Boolean;
  StopError: AnsiString;
begin
  if CurStep <> ssInstall then
    Exit;

  { Inno Setup Restart Manager requests that GUI apps close. Check explicitly
    before replacing the EXE as the app may be elevated or still running in
    another user session. Never force-kill processes or skip files. }
  while True do
  begin
    QuerySucceeded := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "' +
      'if (Get-Process -Name ''RestaurantWiFiControl'' -ErrorAction SilentlyContinue) { exit 2 }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not QuerySucceeded then
    begin
      MsgBox('Could not verify whether Restaurant WiFi Control is running.' + #13#10 +
        'Installation cannot safely overwrite the app executable.',
        mbError, MB_OK);
      Abort;
    end;
    if ResultCode = 0 then Break;
    if ResultCode <> 2 then
    begin
      MsgBox('Could not verify desktop process state. PowerShell exit code: ' +
        IntToStr(ResultCode), mbError, MB_OK);
      Abort;
    end;
    if MsgBox('Restaurant WiFi Control is still running, possibly in the background.' + #13#10 +
      'Close every instance using Task Manager and then click Retry.' + #13#10 +
      'Choose Cancel to leave the existing installation unchanged.',
      mbError, MB_RETRYCANCEL) <> IDRETRY then Abort;
  end;

  { Use a testable helper instead of fragile nested PowerShell -Command quotes.
    It MUST succeed when the service is not installed (first/partial install).
    It also succeeds when already stopped and waits for a running service to stop. }
  ExtractTemporaryFile('StopGatewayForInstall.ps1');
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\StopGatewayForInstall.ps1') +
    '" -LogPath "' + ExpandConstant('{tmp}\gateway-stop.log') + '"';

  if (not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or
     (ResultCode <> 0) then
  begin
    if not LoadStringFromFile(ExpandConstant('{tmp}\gateway-stop.log'), StopError) then
      StopError := 'No service-stop diagnostic log available.';
    MsgBox('Could not safely stop the existing Gateway service.' + #13#10 +
      'Windows exit code: ' + IntToStr(ResultCode) + #13#10 +
      Copy(StopError, 1, 800) + #13#10 +
      'Your existing program and database were not deleted.',
      mbError, MB_OK);
    Abort;
  end;
end;
