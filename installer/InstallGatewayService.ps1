# Runs elevated inside the Inno Setup installer AFTER service files are copied.
# Reconcile an existing (possibly broken) service registration without deleting its data.
param(
    [Parameter(Mandatory = $true)][string] $BinaryPath,
    [Parameter(Mandatory = $true)][string] $LogPath
)
$ErrorActionPreference = 'Stop'
$serviceName = 'RestaurantWiFiGateway'
function Record([string]$message) {
    $line = "$(Get-Date -Format o) $message"
    try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch {}
    Write-Output $line
}

try {
    if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) {
        throw "Gateway executable is missing: $BinaryPath"
    }
    $expectedPath = '"' + [System.IO.Path]::GetFullPath($BinaryPath) + '"'
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop

    if ($null -eq $service) {
        Record 'GATEWAY_SERVICE_MISSING: registering service for first installation'
        New-Service -Name $serviceName -DisplayName 'Restaurant WiFi Gateway' `
            -BinaryPathName $expectedPath -StartupType Automatic -ErrorAction Stop | Out-Null
    } else {
        Record "GATEWAY_SERVICE_EXISTS: state=$($service.State), path=$($service.PathName)"
        # Keep registration from old installations, but fix a bad executable path.
        if ($service.PathName.Trim() -ne $expectedPath) {
            $result = Invoke-CimMethod -InputObject $service -MethodName Change `
                -Arguments @{ PathName = $expectedPath; StartMode = 'Automatic' } -ErrorAction Stop
            if ($result.ReturnValue -ne 0) {
                throw "Windows refused to update Gateway service path. Win32_Service.Change=$($result.ReturnValue)"
            }
            Record 'GATEWAY_SERVICE_PATH_REPAIRED'
        }
    }

    Set-Service -Name $serviceName -StartupType Automatic -ErrorAction Stop
    $current = Get-Service -Name $serviceName -ErrorAction Stop
    $current.Refresh()
    if ($current.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        if ($current.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
            $current.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(20))
        }
        Record "GATEWAY_START_REQUEST: previous state=$($current.Status)"
        Start-Service -Name $serviceName -ErrorAction Stop
    }
    $current.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(35))
    Start-Sleep -Seconds 1
    $current.Refresh()
    if ($current.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw "Gateway did not remain running: $($current.Status)"
    }
    Record 'GATEWAY_RUNNING_CONFIRMED'
    exit 0
} catch {
    Record "GATEWAY_INSTALL_FAILURE: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    try {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        if ($svc) { Record "GATEWAY_AFTER_FAILURE: state=$($svc.State), path=$($svc.PathName)" }
        foreach ($event in (Get-WinEvent -FilterHashtable @{
            LogName = 'System'; ProviderName = 'Service Control Manager';
            StartTime = (Get-Date).AddMinutes(-4)
        } -ErrorAction SilentlyContinue | Select-Object -First 4)) {
            Record ("SCM_EVENT_$($event.Id): " + ($event.Message -replace '[\r\n]+', ' '))
        }
    } catch {}
    exit 10
}
