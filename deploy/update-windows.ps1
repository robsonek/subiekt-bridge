#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Update binarek SubiektBridge bez tracenia configu i danych.

.DESCRIPTION
    Zatrzymuje Windows Service, kopiuje nowe pliki .exe/.dll z bieżącego katalogu,
    zachowuje appsettings.Production.json i data/ (idempotency.db), restartuje serwis.

.EXAMPLE
    cd C:\SubiektBridge\publish\win-x64
    .\update-windows.ps1
#>

param(
    [string] $InstallDir  = "C:\SubiektBridge",
    [string] $ServiceName = "SubiektBridge",
    [string] $SourceDir   = "."
)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path $SourceDir).Path

Write-Host "=== Update SubiektBridge ===" -ForegroundColor Cyan
Write-Host "Źródło: $source"
Write-Host "Docel:  $InstallDir"

# Stop
Write-Host "Zatrzymuję serwis..." -ForegroundColor Yellow
nssm stop $ServiceName 2>&1 | Out-Null
Start-Sleep -Seconds 2

# Backup configu (zawsze)
$configPath = Join-Path $InstallDir "appsettings.Production.json"
$configBackup = $null
if (Test-Path $configPath) {
    $configBackup = Get-Content $configPath -Raw
    Write-Host "Zachowuję istniejący appsettings.Production.json" -ForegroundColor Green
}

# Kopiuj wszystko poza appsettings.Production.json i data/
Get-ChildItem -Path $source -Exclude @("appsettings.Production.json", "data", "logs", "*.log") | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
}

# Przywróć config
if ($configBackup) {
    Set-Content -Path $configPath -Value $configBackup -NoNewline
    Write-Host "Przywrócony appsettings.Production.json" -ForegroundColor Green
}

# Start
Write-Host "Startuję serwis..." -ForegroundColor Yellow
nssm start $ServiceName
Start-Sleep -Seconds 5

$port = 8443
$healthUrl = "https://localhost:$port/api/v1/health"
try {
    $response = Invoke-WebRequest -Uri $healthUrl -SkipCertificateCheck -UseBasicParsing -TimeoutSec 10
    Write-Host "OK: health $($response.StatusCode)" -ForegroundColor Green
    Write-Host $response.Content
} catch {
    Write-Host "OSTRZEŻENIE: health endpoint nie odpowiada - sprawdź C:\SubiektBridge\logs\stderr.log" -ForegroundColor Red
    exit 1
}
