#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Update binarek SubiektBridge bez tracenia configu i danych.

.DESCRIPTION
    Zatrzymuje Windows Service, kopiuje nowe pliki .exe/.dll z bieżącego katalogu,
    zachowuje appsettings.Production.json i data/ (idempotency.db), restartuje serwis.

.EXAMPLE
    cd C:\SubiektBridge\new\
    .\update-windows.ps1
#>

param(
    [string] $InstallDir  = "C:\SubiektBridge",
    [string] $ServiceName = "SubiektBridge",
    [string] $SourceDir   = "."
)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path $SourceDir).Path
# PowerShell 5.x (Windows PS) nie ma null-conditional ?. - klasyczny if dla zgodności.
$resolvedInstall = Resolve-Path $InstallDir -ErrorAction SilentlyContinue
if ($resolvedInstall) {
    $installPath = $resolvedInstall.Path
} else {
    $installPath = $InstallDir
}

Write-Host "=== Update SubiektBridge ===" -ForegroundColor Cyan
Write-Host "Źródło: $source"
Write-Host "Docel:  $installPath"

# Sanity check: $source nie może być tym samym folderem co $InstallDir.
# Inaczej Copy-Item rzuca "Cannot overwrite the item with itself" w środku
# operacji i zostawia serwis zatrzymany.
if ($source -ieq $installPath) {
    Write-Host ""
    Write-Host "BŁĄD: skrypt uruchomiony z $installPath - tego samego co InstallDir." -ForegroundColor Red
    Write-Host "Rozwiązanie:" -ForegroundColor Yellow
    Write-Host "  1. Rozpakuj nowy ZIP do osobnego folderu, np. C:\SubiektBridge_new\"
    Write-Host "  2. cd C:\SubiektBridge_new\"
    Write-Host "  3. .\update-windows.ps1"
    exit 1
}

# Stop
Write-Host "Zatrzymuję serwis..." -ForegroundColor Yellow
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
}

# Backup configu (zawsze)
$configPath = Join-Path $InstallDir "appsettings.Production.json"
$configBackup = $null
if (Test-Path $configPath) {
    $configBackup = Get-Content $configPath -Raw
    Write-Host "Zachowuję istniejący appsettings.Production.json" -ForegroundColor Green
}

# Kopiuj wszystko poza appsettings.Production.json i data/ i logs/
Get-ChildItem -Path $source -Exclude @(
    "appsettings.Production.json",
    "data",
    "logs",
    "*.log"
) | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
}

# Przywróć config
if ($configBackup) {
    Set-Content -Path $configPath -Value $configBackup -NoNewline
    Write-Host "Przywrócony appsettings.Production.json" -ForegroundColor Green
}

# Start
Write-Host "Startuję serwis..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$port = 8443
$healthUrl = "https://localhost:$port/api/v1/health"
try {
    $response = Invoke-WebRequest -Uri $healthUrl -SkipCertificateCheck -UseBasicParsing -TimeoutSec 10
    Write-Host "OK: health $($response.StatusCode)" -ForegroundColor Green
    Write-Host $response.Content
} catch {
    Write-Host "OSTRZEŻENIE: health endpoint nie odpowiada - sprawdź $InstallDir\logs\" -ForegroundColor Red
    Write-Host "Status serwisu: $((Get-Service -Name $ServiceName).Status)" -ForegroundColor Yellow
    exit 1
}
