param([string]$NexusDir = 'C:\Program Files\Nexus')
$ErrorActionPreference = 'Stop'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" -NexusDir `"$NexusDir`""
    exit
}

$here = Split-Path -Parent $PSCommandPath
if (-not (Test-Path (Join-Path $NexusDir 'Nexus.exe'))) {
    throw "[ERROR] Nexus не найден в $NexusDir. Сначала установите официальный Nexus 3.1.9-beta.1 / Nexus not found: install the official Nexus 3.1.9-beta.1 first."
}
if (-not (Test-Path (Join-Path $here 'Nexus.exe')) -or -not (Test-Path (Join-Path $here 'wwwroot\index.html'))) {
    throw "[ERROR] Рядом со скриптом нет Nexus.exe и wwwroot: распакуйте архив целиком / Nexus.exe and wwwroot must sit next to this script."
}

$backup = Join-Path $NexusDir ('_original-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Output "[..] Останавливаю службу Nexus / stopping the Nexus service"
Stop-Service NexusService -Force
Get-Process Nexus -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Output "[..] Сохраняю оригинал в $backup / saving the original"
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item (Join-Path $NexusDir 'Nexus.exe') $backup
Copy-Item (Join-Path $NexusDir 'wwwroot') $backup -Recurse

Write-Output "[..] Ставлю Nexus Fast / installing Nexus Fast"
Copy-Item (Join-Path $here 'Nexus.exe') (Join-Path $NexusDir 'Nexus.exe') -Force
Copy-Item (Join-Path $here 'wwwroot\*') (Join-Path $NexusDir 'wwwroot') -Recurse -Force

Start-Service NexusService
Write-Output "[OK] Nexus Fast установлен, служба: $((Get-Service NexusService).Status). Откат: restore.cmd / installed; roll back with restore.cmd"
