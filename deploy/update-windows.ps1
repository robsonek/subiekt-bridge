#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Update binarek SubiektBridge bez tracenia configu i danych.

.DESCRIPTION
    Zatrzymuje Windows Service, kopiuje nowe pliki .exe/.dll z biezacego katalogu,
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

# Windows PowerShell 5.1 on Windows Server 2016 does not support
# Invoke-WebRequest -SkipCertificateCheck. Bridge uses a self-signed cert,
# so accept it process-wide for this script.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$source = (Resolve-Path $SourceDir).Path
# PowerShell 5.x (Windows PS) nie ma null-conditional ?. - klasyczny if dla zgodnosci.
$resolvedInstall = Resolve-Path $InstallDir -ErrorAction SilentlyContinue
if ($resolvedInstall) {
    $installPath = $resolvedInstall.Path
} else {
    $installPath = $InstallDir
}

Write-Host "=== Update SubiektBridge ===" -ForegroundColor Cyan
Write-Host "Zrodlo: $source"
Write-Host "Docel:  $installPath"

# Sanity check: $source nie moze byc tym samym folderem co $InstallDir.
# Inaczej Copy-Item rzuca "Cannot overwrite the item with itself" w srodku
# operacji i zostawia serwis zatrzymany.
if ($source -ieq $installPath) {
    Write-Host ""
    Write-Host "BLAD: skrypt uruchomiony z $installPath - tego samego co InstallDir." -ForegroundColor Red
    Write-Host "Rozwiazanie:" -ForegroundColor Yellow
    Write-Host "  1. Rozpakuj nowy ZIP do osobnego folderu, np. C:\SubiektBridge_new"
    Write-Host "  2. cd C:\SubiektBridge_new"
    Write-Host "  3. .\update-windows.ps1"
    exit 1
}

# Stop
Write-Host "Zatrzymuje serwis..." -ForegroundColor Yellow
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
    Write-Host "Zachowuje istniejacy appsettings.Production.json" -ForegroundColor Green
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

# Przywroc config
if ($configBackup) {
    Set-Content -Path $configPath -Value $configBackup -NoNewline
    Write-Host "Przywrocony appsettings.Production.json" -ForegroundColor Green
}

# Start
Write-Host "Startuje serwis..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$port = 988
$healthUrl = "https://localhost:$port/api/v1/health"
try {
    $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10
    Write-Host "OK: health $($response.StatusCode)" -ForegroundColor Green
    Write-Host $response.Content
} catch {
    $logDir = Join-Path $InstallDir "logs"
    $serviceStatus = (Get-Service -Name $ServiceName).Status
    Write-Host "OSTRZEZENIE: health endpoint nie odpowiada - sprawdz $logDir" -ForegroundColor Red
    Write-Host "Status serwisu: $serviceStatus" -ForegroundColor Yellow
    exit 1
}
