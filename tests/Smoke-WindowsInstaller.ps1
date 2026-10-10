# Runs only on ephemeral GitHub Windows runners, after the Inno EXE is produced.
# Regression: user saw "Cannot stop existing Gateway service" even though no service existed.
# Test a FRESH install and IN-PLACE reinstall of the same artifact without deleting ProgramData.
$ErrorActionPreference = 'Stop'
$serviceName = 'RestaurantWiFiGateway'
$helper = (Resolve-Path 'installer/StopGatewayForInstall.ps1').Path
$installer = (Resolve-Path 'installer/output/Restaurant_WiFi_Control_V9_3_4_Automated_Expiry_Setup.exe').Path
$stamp = [guid]::NewGuid().ToString('n').Substring(0,8)
$installLog = Join-Path $env:TEMP "restaurant-wifi-install-$stamp.log"
$stopLog = Join-Path $env:TEMP "restaurant-wifi-stop-$stamp.log"

function RunInstaller([string]$phase) {
    Write-Host "Starting ${phase}: $installer"
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $installLog + '"'
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        if (Test-Path $installLog) { Get-Content $installLog | Select-Object -Last 55 | Out-Host }
        throw "$phase failed with setup exit code $($process.ExitCode)."
    }
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        if (Test-Path $installLog) { Get-Content $installLog | Select-Object -Last 55 | Out-Host }
        throw "$phase did not register the Gateway Windows service."
    }
    $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(35))
    $svc.Refresh()
    if ($svc.Status -ne 'Running') { throw "${phase}: Gateway was not Running." }
    Write-Host "PASS: $phase installed a running Gateway Windows service."
}

# Prior service smoke-test deletes its test service, but SCM removal can be asynchronous.
for ($wait=0; $wait -lt 20 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $wait++) {
    Start-Sleep -Milliseconds 500
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw 'CI test service was not fully deleted; cannot verify fresh install.'
}

& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -LogPath $stopLog
if ($LASTEXITCODE -ne 0) { throw 'Missing-service stop helper should exit successfully.' }
if (-not ((Get-Content $stopLog -Raw) -match 'SERVICE_NOT_INSTALLED')) {
    throw 'Missing-service branch was not exercised.'
}
Write-Host 'PASS: no existing Gateway service is a valid install condition.'

RunInstaller 'Fresh install'

# TEST RUNNER ONLY: force the exact migration path from user hardware.
# This disposable Windows VM has no customer data; NEVER do this on a user PC.
Stop-Service -Name $serviceName -Force -ErrorAction Stop
$svcStopped = Get-Service -Name $serviceName
$svcStopped.WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
    [TimeSpan]::FromSeconds(25))
foreach ($file in @('wifi-state.db', 'wifi-state.db-wal', 'wifi-state.db-shm',
                    'wifi-state.db-journal', 'v9-data.import.json',
                    'v9-data.json.pre-sqlite.bak')) {
    $target = Join-Path (Join-Path $env:ProgramData 'Restaurant WiFi Control') $file
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}
Write-Host 'Disposable CI database cleared solely to validate protected legacy JSON migration.'

# Regression for the real user Windows 11 crash (Event 1026):
# old v9-data.json cannot have its DACL changed by LocalSystem.
# The service must protect SQLite without modifying any legacy JSON ACL.
$dataDir = Join-Path $env:ProgramData 'Restaurant WiFi Control'
$legacyJson = Join-Path $dataDir 'v9-data.json'
if (Test-Path -LiteralPath $legacyJson) {
    throw 'Unexpected pre-existing v9-data.json on ephemeral CI runner.'
}
$jsonContent = '{"LegacyPermissionRegression":true}'
Set-Content -LiteralPath $legacyJson -Value $jsonContent -Encoding Ascii -NoNewline

function InvokeIcacls([string[]]$parameters) {
    & "$env:WINDIR\System32\icacls.exe" @parameters | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit $LASTEXITCODE" }
}
# Preserve Administrator access, allow only reading as SYSTEM, then deny
# SYSTEM permission to change the legacy file's DACL.
InvokeIcacls -parameters @($legacyJson, '/grant:r', '*S-1-5-18:R', '*S-1-5-32-544:F')
InvokeIcacls -parameters @($legacyJson, '/inheritance:r')
InvokeIcacls -parameters @($legacyJson, '/deny', '*S-1-5-18:(R,WDAC)')
$expectedLegacyHash = (Get-FileHash -LiteralPath $legacyJson -Algorithm SHA256).Hash
$expectedLegacyAcl = (Get-Acl -LiteralPath $legacyJson).Sddl
Write-Host 'Legacy JSON test fixture: SYSTEM cannot read the original or edit its DACL.'

RunInstaller 'In-place reinstall / upgrade with protected legacy JSON'
$staged = Join-Path $dataDir 'v9-data.import.json'
if (-not (Test-Path -LiteralPath $staged)) {
    throw 'Elevated installer did not stage SYSTEM-readable legacy JSON.'
}
if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $expectedLegacyHash) {
    throw 'Protected staging copy differs from original legacy JSON.'
}

# Test the service successfully imported the protected JSON into SQLite.
$adminPipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.', 'RestaurantWiFiControlAdmin', [System.IO.Pipes.PipeDirection]::InOut)
try {
    $adminPipe.Connect(2500)
    $writer = [System.IO.BinaryWriter]::new($adminPipe)
    $reader = [System.IO.BinaryReader]::new($adminPipe)
    $payload = [System.Text.Encoding]::UTF8.GetBytes('{"Operation":"read"}')
    $writer.Write([int]$payload.Length)
    $writer.Write([byte[]]$payload)
    $writer.Flush()
    $size = $reader.ReadInt32()
    if ($size -lt 1 -or $size -gt 8388608) { throw 'Invalid IPC response size.' }
    $answer = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes($size)) | ConvertFrom-Json
    if (-not $answer.Success -or -not (($answer.Data | ConvertFrom-Json).LegacyPermissionRegression)) {
        throw 'SQLite state did not contain the protected legacy records.'
    }
} finally { $adminPipe.Dispose() }
Write-Host 'PASS: Gateway imported legacy JSON denied to SYSTEM into SQLite without data loss.'

if ((Get-FileHash -LiteralPath $legacyJson -Algorithm SHA256).Hash -ne $expectedLegacyHash) {
    throw 'Installer or service altered historical v9-data.json contents.'
}
if ((Get-Acl -LiteralPath $legacyJson).Sddl -ne $expectedLegacyAcl) {
    throw 'Installer or service altered the protected legacy JSON file ACL.'
}
Write-Host 'PASS: protected legacy JSON contents and ACL remain untouched after service restart.'

$serviceAfter = Get-Service -Name $serviceName -ErrorAction Stop
if ($serviceAfter.Status -ne 'Running') { throw 'Gateway not running after upgrade.' }
Write-Host 'PASS: two consecutive actual Windows installer runs.'
