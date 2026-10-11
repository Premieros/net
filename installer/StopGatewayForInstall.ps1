# Invoked by the elevated Inno Setup installer BEFORE it overwrites any files.
# Never delete the service, the database, or customer settings.
# Already stopped or not-installed services are successful no-ops.
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath
)

$ErrorActionPreference = 'Stop'
function Report([string] $message) {
    $line = "$(Get-Date -Format o) $message"
    Write-Output $line
    try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch {}
}

try {
    $svc = Get-Service -Name 'RestaurantWiFiGateway' -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Report 'SERVICE_NOT_INSTALLED - no running Gateway to stop'
        exit 0
    }
    $svc.Refresh()
    Report "SERVICE_BEFORE: $($svc.Status); CanStop=$($svc.CanStop)"
    if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Report 'SERVICE_ALREADY_STOPPED'
        exit 0
    }

    if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
        Report 'SERVICE_STOP_PENDING - waiting'
    } else {
        if (-not $svc.CanStop) {
            Report "STOP_NOT_ACCEPTED: Service status=$($svc.Status)"
            exit 3
        }
        Stop-Service -Name 'RestaurantWiFiGateway' -Force -ErrorAction Stop
        Report 'STOP_REQUEST_ACCEPTED'
    }

    $svc.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(40))
    $svc.Refresh()
    if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Report "STOP_FAILED: Service status=$($svc.Status)"
        exit 4
    }
    Report 'STOP_CONFIRMED'
    exit 0
}
catch {
    # A service may already have stopped between the initial query and Stop-Service.
    $latest = Get-Service -Name 'RestaurantWiFiGateway' -ErrorAction SilentlyContinue
    if ($null -eq $latest -or
        $latest.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Report 'STOP_CONFIRMED_AFTER_RACE'
        exit 0
    }
    Report "STOP_EXCEPTION: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    exit 5
}
