#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Self-contained update SubiektBridge'a do najnowszej wersji z GitHub Releases.

.DESCRIPTION
    Skrypt do uruchamiania przy kazdym update - sam pobiera najnowszy release ZIP,
    rozpakowuje, wymienia binarki (zachowujac appsettings.Production.json i data/),
    restartuje Windows Service i sprawdza /health.

    Idempotentny: jesli wersja jest juz ta sama, uzyj -Force zeby wymusic.

    Wszystko w jednym pliku - zapisz raz w C:\SubiektBridge\update-bridge.ps1
    i odpalaj kiedy chcesz update.

.PARAMETER Tag
    Tag wersji do pobrania (np. "v0.7.10"). Default: najnowszy z GitHub Releases.

.PARAMETER InstallDir
    Docelowy folder Bridge'a. Default: C:\SubiektBridge

.PARAMETER ServiceName
    Nazwa Windows Service. Default: SubiektBridge

.PARAMETER Port
    Port HTTPS Bridge'a (do health check). Default: 988

.PARAMETER Token
    X-Bridge-Token do health check. Default: czytany z appsettings.Production.json

.PARAMETER Force
    Wymusza update nawet gdy zainstalowana wersja = latest.

.PARAMETER KeepZip
    Zachowaj pobrany ZIP w %TEMP% (default: usuwany po update).

.EXAMPLE
    # Najprostsze - update do najnowszej wersji
    .\update-bridge.ps1

    # Konkretna wersja
    .\update-bridge.ps1 -Tag v0.7.9

    # Inny port (jesli zmieniles w appsettings)
    .\update-bridge.ps1 -Port 8443
#>

param(
    [string] $Tag,
    [string] $InstallDir   = "C:\SubiektBridge",
    [string] $ServiceName  = "SubiektBridge",
    [int]    $Port         = 988,
    [string] $Token,
    [switch] $Force,
    [switch] $KeepZip
)

$ErrorActionPreference = "Stop"
$Repo = "robsonek/subiekt-bridge"

function Write-Section($text) {
    Write-Host ""
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Fail($msg) {
    Write-Host "BLAD: $msg" -ForegroundColor Red
    exit 1
}

# ---------------------- TLS 1.2 ----------------------
# PowerShell 5.x (Windows PS) domyslnie uzywa TLS 1.0/1.1, GitHub wymaga 1.2+.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---------------------- Sanity ----------------------
Write-Section "Sanity check"

if (!(Test-Path $InstallDir)) {
    Fail "InstallDir '$InstallDir' nie istnieje. Pierwsza instalacja - uzyj install-windows.ps1."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Fail "Service '$ServiceName' nie zarejestrowany. Pierwsza instalacja - uzyj install-windows.ps1."
}
Write-Host "OK: service '$ServiceName' istnieje (status: $($service.Status))" -ForegroundColor Green

# ---------------------- Detect latest tag ----------------------
Write-Section "GitHub Releases"

if (-not $Tag) {
    Write-Host "Pobieranie najnowszego tagu z GitHub..."
    try {
        $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing
        $Tag = $latest.tag_name
    } catch {
        Fail "Nie moge pobrac latest tag: $($_.Exception.Message)"
    }
}

$Version = $Tag.TrimStart('v')
Write-Host "Target version: $Tag" -ForegroundColor Yellow

# Sprawdz obecna wersje (z health endpoint jesli serwis dziala)
$currentVersion = $null
if ($service.Status -eq 'Running') {
    try {
        $tokenForCheck = $Token
        if (-not $tokenForCheck) {
            # sprobuj wyciagnac z appsettings.Production.json
            $configPath = Join-Path $InstallDir "appsettings.Production.json"
            if (Test-Path $configPath) {
                $config = Get-Content $configPath -Raw | ConvertFrom-Json
                $tokenForCheck = $config.Bridge.Token
            }
        }
        if ($tokenForCheck) {
            $health = Invoke-RestMethod -Uri "https://localhost:$Port/api/v1/health" `
                -Headers @{ 'X-Bridge-Token' = $tokenForCheck } `
                -SkipCertificateCheck -UseBasicParsing -TimeoutSec 5
            $currentVersion = $health.bridge_version
            Write-Host "Aktualna wersja na zywo: $currentVersion" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "Nie udalo sie sprawdzic obecnej wersji ($($_.Exception.Message)) - kontynuuje" -ForegroundColor DarkYellow
    }
}

if ($currentVersion -and ($currentVersion -like "$Version*") -and (-not $Force)) {
    Write-Host "Wersja $currentVersion juz zainstalowana. Uzyj -Force zeby wymusic." -ForegroundColor Green
    exit 0
}

# ---------------------- Download ZIP ----------------------
Write-Section "Pobieranie ZIP"

$zipName = "SubiektBridge-$Version-win-x86.zip"
$zipUrl = "https://github.com/$Repo/releases/download/$Tag/$zipName"
$zipPath = Join-Path $env:TEMP $zipName

Write-Host "URL: $zipUrl"
Write-Host "Local: $zipPath"

try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "OK: pobrano $sizeMB MB" -ForegroundColor Green
} catch {
    Fail "Download zawiodl: $($_.Exception.Message)"
}

# ---------------------- Extract ----------------------
Write-Section "Rozpakowywanie"

$extractDir = Join-Path $env:TEMP "SubiektBridge-extract-$([guid]::NewGuid().ToString('N'))"
try {
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    Write-Host "OK: rozpakowano do $extractDir" -ForegroundColor Green
} catch {
    Fail "Rozpakowanie zawiodlo: $($_.Exception.Message)"
}

# ---------------------- Stop service ----------------------
Write-Section "Stop service"

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    # Czekaj az faktycznie sie zatrzyma (process unlock plikow .exe/.dll)
    $svc = Get-Service -Name $ServiceName
    $tries = 0
    while ($svc.Status -ne 'Stopped' -and $tries -lt 20) {
        Start-Sleep -Milliseconds 500
        $svc.Refresh()
        $tries++
    }
    if ($svc.Status -ne 'Stopped') {
        Fail "Service nie zatrzymal sie w 10s (status: $($svc.Status))"
    }
}
Write-Host "OK: service zatrzymany" -ForegroundColor Green

# ---------------------- Backup config ----------------------
$configPath = Join-Path $InstallDir "appsettings.Production.json"
$configBackup = $null
if (Test-Path $configPath) {
    $configBackup = Get-Content $configPath -Raw
    Write-Host "OK: backup appsettings.Production.json (in-memory)" -ForegroundColor Green
}

# ---------------------- Replace files ----------------------
Write-Section "Wymiana binariow"

# Skopiuj wszystko OPROCZ tych co zachowujemy.
$excludes = @("appsettings.Production.json", "data", "logs", "*.log")

Get-ChildItem -Path $extractDir -Exclude $excludes | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
}

# Przywroc config
if ($configBackup) {
    Set-Content -Path $configPath -Value $configBackup -NoNewline
    Write-Host "OK: przywrocony appsettings.Production.json" -ForegroundColor Green
}

# Sprzatanie tymczasowe
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepZip) {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}

# ---------------------- Start service ----------------------
Write-Section "Start service"

Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$svc = Get-Service -Name $ServiceName
Write-Host "Status: $($svc.Status)" -ForegroundColor $(if ($svc.Status -eq 'Running') { 'Green' } else { 'Red' })

# ---------------------- Health check ----------------------
Write-Section "Health check"

$tokenForCheck = $Token
if (-not $tokenForCheck -and (Test-Path $configPath)) {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $tokenForCheck = $config.Bridge.Token
}

if (-not $tokenForCheck) {
    Write-Host "Brak tokenu - pomijam health check. Sprawdz recznie." -ForegroundColor Yellow
    exit 0
}

try {
    $health = Invoke-RestMethod -Uri "https://localhost:$Port/api/v1/health" `
        -Headers @{ 'X-Bridge-Token' = $tokenForCheck } `
        -SkipCertificateCheck -UseBasicParsing -TimeoutSec 15
    Write-Host "OK: $($health.status) | bridge=$($health.bridge_version) | subiekt=$($health.subiekt_version) | sfera=$($health.sfera_session)" -ForegroundColor Green
    if ($health.last_error) {
        Write-Host "OSTRZEZENIE last_error: $($health.last_error)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "OSTRZEZENIE: health endpoint nie odpowiada - sprawdz $InstallDir\logs\" -ForegroundColor Red
    Write-Host "Status serwisu: $($svc.Status)" -ForegroundColor Yellow
    exit 1
}

Write-Section "Update zakonczony"
Write-Host "Wersja: $Tag" -ForegroundColor Green
