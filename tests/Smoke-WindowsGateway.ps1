# Smoke-test the actual self-contained Windows gateway as a console process.
# This catches ACL, named-pipe, SQLite and startup failures that compilation cannot.
$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path 'installer/gateway/RestaurantWiFiGateway.exe').Path
$gateway = Start-Process -FilePath $binary -PassThru -WindowStyle Hidden
$success = $false
$lastError = ''
try {
    for ($attempt = 0; $attempt -lt 35; $attempt++) {
        $gateway.Refresh()
        if ($gateway.HasExited) {
            throw "Gateway exited before admin IPC became ready (exit $($gateway.ExitCode))."
        }

        $pipe = $null
        try {
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.', 'RestaurantWiFiControlAdmin',
                [System.IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(500)
            $pipe.ReadTimeout = 3000
            $pipe.WriteTimeout = 3000
            $request = [System.Text.Encoding]::UTF8.GetBytes('{"Operation":"read"}')
            $writer = [System.IO.BinaryWriter]::new($pipe)
            $reader = [System.IO.BinaryReader]::new($pipe)
            $writer.Write([int]$request.Length)
            $writer.Write([byte[]]$request)
            $writer.Flush()
            $length = $reader.ReadInt32()
            if ($length -le 0 -or $length -gt 8388608) { throw "Invalid pipe reply length: $length" }
            $responseBytes = $reader.ReadBytes($length)
            if ($responseBytes.Length -ne $length) { throw "Incomplete pipe response." }
            $response = [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
            if ($response.Success -ne $true) { throw "Admin read returned an error: $($response.Error)" }
            Write-Host 'PASS: Gateway starts; restricted admin named pipe accepts a read request.'
            $success = $true
            break
        }
        catch {
            $lastError = $_.Exception.Message
        }
        finally { if ($pipe) { $pipe.Dispose() } }
        Start-Sleep -Milliseconds 600
    }
    if (-not $success) { throw "Gateway admin named pipe did not respond: $lastError" }
}
finally {
    $gateway.Refresh()
    if (-not $gateway.HasExited) {
        Stop-Process -Id $gateway.Id -Force -ErrorAction SilentlyContinue
        $gateway.WaitForExit(10000) | Out-Null
    }
    $diagnostic = Join-Path $env:ProgramData 'Restaurant WiFi Control/gateway-startup-error.txt'
    if (-not $success -and (Test-Path $diagnostic)) {
        Write-Host 'Gateway startup diagnostic:'
        Get-Content $diagnostic | Select-Object -Last 4
    }
}
