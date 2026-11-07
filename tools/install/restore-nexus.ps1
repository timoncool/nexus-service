param([string]$NexusDir = 'C:\Program Files\Nexus')
$ErrorActionPreference = 'Stop'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$PSCommandPath`" -NexusDir `"$NexusDir`""
    exit
}

$backup = Get-ChildItem $NexusDir -Directory -Filter '_original-*' | Sort-Object Name -Descending | Select-Object -First 1
if (-not $backup) {
    throw "[ERROR] В $NexusDir нет сохранённого оригинала (_original-*) / no saved original found"
}

Write-Output "[..] Останавливаю службу Nexus / stopping the Nexus service"
Stop-Service NexusService -Force
Get-Process Nexus -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Output "[..] Возвращаю оригинал из $($backup.FullName) / restoring the original"
Copy-Item (Join-Path $backup.FullName 'Nexus.exe') (Join-Path $NexusDir 'Nexus.exe') -Force
Copy-Item (Join-Path $backup.FullName 'wwwroot\*') (Join-Path $NexusDir 'wwwroot') -Recurse -Force

Start-Service NexusService
Write-Output "[OK] Оригинальный Nexus возвращён, служба: $((Get-Service NexusService).Status) / original Nexus restored"
