# Runs only on ephemeral GitHub Windows runners, after the Inno EXE is produced.
# Regression: user saw "Cannot stop existing Gateway service" even though no service existed.
# Test a FRESH install and IN-PLACE reinstall of the same artifact without deleting ProgramData.
$ErrorActionPreference = 'Stop'
$serviceName = 'RestaurantWiFiGateway'
$helper = (Resolve-Path 'installer/StopGatewayForInstall.ps1').Path
$installer = (Resolve-Path 'installer/output/Restaurant_WiFi_Control_V9_2_3_Beta_Installer_Fix_Setup.exe').Path
$stamp = [guid]::NewGuid().ToString('n').Substring(0,8)
$installLog = Join-Path $env:TEMP "restaurant-wifi-install-$stamp.log"
$stopLog = Join-Path $env:TEMP "restaurant-wifi-stop-$stamp.log"

function RunInstaller([string]$phase) {
    Write-Host "Starting $phase: $installer"
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
    if ($svc.Status -ne 'Running') { throw "$phase: Gateway was not Running." }
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
RunInstaller 'In-place reinstall / upgrade'

$serviceAfter = Get-Service -Name $serviceName -ErrorAction Stop
if ($serviceAfter.Status -ne 'Running') { throw 'Gateway not running after upgrade.' }
Write-Host 'PASS: two consecutive actual Windows installer runs.'
