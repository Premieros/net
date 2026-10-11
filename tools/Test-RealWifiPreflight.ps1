# Restaurant WiFi Control - read-only physical test preflight
# Run from an elevated PowerShell window on the CASHIER Windows 11 PC.
# This script does not change ICS, firewall, WFP, DNS or router settings.
$ErrorActionPreference = 'Continue'
Write-Host '=== Restaurant WiFi Control: test preflight ==='
Write-Host 'Expected: incoming Wi-Fi 2, outgoing Ethernet 2, downstream 192.168.137.1'
Get-NetAdapter | Select-Object Name, Status, ifIndex, InterfaceDescription | Format-Table -AutoSize
Write-Host '=== IPv4 adapter addresses ==='
Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress, PrefixLength | Format-Table -AutoSize
Write-Host '=== IPv6 adapter addresses (inspect downstream for bypass) ==='
Get-NetIPAddress -AddressFamily IPv6 | Where-Object InterfaceAlias -eq 'Ethernet 2' |
    Select-Object InterfaceAlias, IPAddress, PrefixLength, AddressState | Format-Table -AutoSize
Write-Host '=== Default routes ==='
Get-NetRoute -DestinationPrefix '0.0.0.0/0' |
    Select-Object InterfaceAlias, NextHop, RouteMetric | Format-Table -AutoSize
Write-Host '=== Gateway service ==='
Get-Service -Name RestaurantWiFiGateway -ErrorAction SilentlyContinue |
    Select-Object Name, Status, StartType | Format-Table -AutoSize
Write-Host '=== Local portal and guard status ==='
try { Invoke-RestMethod 'http://127.0.0.1:8765/status/' -TimeoutSec 5 | ConvertTo-Json -Depth 8 }
catch { Write-Warning ('Service status is unavailable: ' + $_.Exception.Message) }
Write-Host '=== Test-only AP cable / phone check ==='
Write-Host 'Confirm TP-Link WAN/uplink cable leads ONLY to cashier Ethernet 2, not to source router.'
Write-Host 'On a test phone, confirm Gateway=192.168.137.1 and Wi-Fi IP=192.168.137.x.'
Write-Host 'Do NOT invoke persistent-ipv4-guard install: current persistent deny does not support voucher permits.'
