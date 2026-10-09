# This test executes on a GitHub-hosted Windows runner only.
# It verifies the actual Windows Service Control Manager registration/start path;
# console-process smoke tests alone do not catch error 1053.
$ErrorActionPreference = 'Stop'
$name = 'RestaurantWiFiGateway'
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($null -ne $existing) { throw "Test runner has an unexpected existing service: $name" }

$binary = (Resolve-Path 'installer/gateway/RestaurantWiFiGateway.exe').Path
$started = $false
try {
    New-Service -Name $name -DisplayName 'Restaurant WiFi Gateway' `
        -BinaryPathName ('"' + $binary + '"') -StartupType Manual | Out-Null
    Write-Host 'Windows SCM service registered.'
    try {
        Start-Service -Name $name -ErrorAction Stop
        $service = Get-Service -Name $name -ErrorAction Stop
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(35))
        $service.Refresh()
        if ($service.Status -ne 'Running') {
            throw "Gateway service was not Running (state $($service.Status))."
        }
        $started = $true
        Write-Host 'PASS: Windows SCM started the actual Gateway service.'
    } catch {
        Write-Host "Service startup failure: $($_.Exception.Message)"
        $diagnostic = Join-Path $env:ProgramData 'Restaurant WiFi Control/gateway-startup-error.txt'
        if (Test-Path $diagnostic) { Get-Content $diagnostic | Select-Object -Last 4 }
        Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            StartTime = (Get-Date).AddMinutes(-3)
        } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error') -or
                $_.Message -match 'RestaurantWiFiGateway' } |
            Select-Object -First 4 -ExpandProperty Message | Write-Host
        throw
    }

    $pipeReady = $false
    $lastPipeError = ''
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $pipe = $null
        try {
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.', 'RestaurantWiFiControlAdmin', [System.IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(600)
            $request = [System.Text.Encoding]::UTF8.GetBytes('{"Operation":"read"}')
            $writer = [System.IO.BinaryWriter]::new($pipe)
            $reader = [System.IO.BinaryReader]::new($pipe)
            $writer.Write([int]$request.Length)
            $writer.Write([byte[]]$request)
            $writer.Flush()
            $size = $reader.ReadInt32()
            if ($size -lt 1 -or $size -gt 8388608) { throw "Invalid reply size" }
            $response = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes($size)) |
                ConvertFrom-Json
            if ($response.Success -ne $true) { throw "Pipe rejected read: $($response.Error)" }
            $pipeReady = $true
            break
        } catch {
            $lastPipeError = $_.Exception.ToString()
            Start-Sleep -Milliseconds 350
        }
        finally { if ($null -ne $pipe) { $pipe.Dispose() } }
    }
    if (-not $pipeReady) {
        Write-Host "Last IPC failure: $lastPipeError"
        Write-Host 'Final SCM service status:'
        Get-Service -Name $name | Format-List Name,Status,StartType | Out-Host
        Write-Host 'Recent .NET/SCM events:'
        foreach ($log in @('Application','System')) {
            Get-WinEvent -FilterHashtable @{
                LogName = $log
                StartTime = (Get-Date).AddMinutes(-4)
            } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'RestaurantWiFiGateway|Restaurant WiFi Gateway' } |
            Select-Object -First 5 -Property ProviderName,Id,Message | Format-List | Out-Host
        }
        throw 'Running Windows service did not respond to administrator IPC.'
    }
    Write-Host 'PASS: Running Windows service responds over restricted admin named pipe.'
} finally {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        if ($started) {
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
        & sc.exe delete $name | Out-Host
    }
}
