# Uwaga: NIE uzywaj #Requires -RunAsAdministrator - blokuje self-update
# z Windows Service (LocalSystem jest adminem, ale sesja PS nie jest "elevated"
# w kontekscie detached cmd.exe /c powershell.exe).

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

.PARAMETER SelfContained
    Pobierz wariant self-contained (~46 MB, runtime wbudowany w binarki).
    Default: framework-dependent (~1.5 MB) - wymaga ASP.NET Core 10 Runtime (x86)
    zainstalowanego na hoscie.

.EXAMPLE
    # Najprostsze - update do najnowszej wersji (fxdep, maly download)
    .\update-bridge.ps1

    # Konkretna wersja
    .\update-bridge.ps1 -Tag v0.7.9

    # Self-contained (gdy nie chcesz instalowac ASP.NET Core 10 Runtime osobno)
    .\update-bridge.ps1 -SelfContained

    # Inny port (jesli zmieniles w appsettings)
    .\update-bridge.ps1 -Port 988
#>

param(
    [string] $Tag,
    [string] $InstallDir    = "C:\SubiektBridge",
    [string] $ServiceName   = "SubiektBridge",
    [int]    $Port          = 988,
    [string] $Token,
    [switch] $Force,
    [switch] $KeepZip,
    [switch] $SelfContained,
    [switch] $Detached
)

# --- Self-detach: jesli jestesmy child processem serwisu Bridge'a, Stop-Service
# zabije cale drzewo procesow (w tym nas). Rozwiazanie: re-launch jako niezalezny
# proces i wyjdz. Flaga -Detached zapobiega nieskonczonej petli.
if (-not $Detached) {
    $relaunchArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Detached", "-Force")
    if ($Tag)           { $relaunchArgs += "-Tag";           $relaunchArgs += $Tag }
    if ($Token)         { $relaunchArgs += "-Token";         $relaunchArgs += $Token }
    if ($InstallDir -ne "C:\SubiektBridge") { $relaunchArgs += "-InstallDir"; $relaunchArgs += $InstallDir }
    if ($ServiceName -ne "SubiektBridge")   { $relaunchArgs += "-ServiceName"; $relaunchArgs += $ServiceName }
    if ($Port -ne 988)  { $relaunchArgs += "-Port";          $relaunchArgs += $Port }
    if ($KeepZip)       { $relaunchArgs += "-KeepZip" }
    if ($SelfContained) { $relaunchArgs += "-SelfContained" }

    $logFile = Join-Path $InstallDir "logs\update-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
    Start-Process powershell.exe -ArgumentList $relaunchArgs -WindowStyle Hidden `
        -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err"
    exit 0
}

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

# ---------------------- TLS 1.2+ + ignore self-signed cert ----------------------
# PowerShell 5.x (Windows PS) domyslnie uzywa TLS 1.0/1.1, GitHub wymaga 1.2+.
# Probuj TLS 1.3 jesli dostepne (PS 5.x na starszym .NET moze nie miec Tls13 enum).
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
} catch {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

# Bridge ma self-signed cert (data/cert.pfx auto-generated).
# ServerCertificateValidationCallback dziala na .NET Framework i .NET Core.
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

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

$ghHeaders = @{ 'User-Agent' = 'SubiektBridge-Updater/1.0' }

if (-not $Tag) {
    Write-Host "Pobieranie najnowszego tagu z GitHub..."
    $ghUrl = "https://api.github.com/repos/$Repo/releases/latest"
    try {
        $latest = Invoke-RestMethod -Uri $ghUrl -Headers $ghHeaders -UseBasicParsing
        $Tag = $latest.tag_name
    } catch {
        Write-Host "Invoke-RestMethod padl: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "Na Windows Server 2016 PowerShell 5.x nie obsluguje cipher suites wymaganych przez GitHub." -ForegroundColor Yellow
        Write-Host "Uzyj self-update przez Bridge (POST /api/v1/admin/update) - Bridge pobierze tag sam." -ForegroundColor Yellow
        Fail "Podaj -Tag recznie (np. .\update-bridge.ps1 -Tag v0.7.29)"
    }
}

$Version = $Tag.TrimStart('v')
$variant = if ($SelfContained) { "win-x86" } else { "win-x86-fxdep" }
Write-Host "Target version: $Tag" -ForegroundColor Yellow
Write-Host "Variant: $variant" -ForegroundColor Yellow

# Dla fxdep ostrzezenie gdy ASP.NET Core 10 (x86) nie zainstalowany.
# Self-contained nie wymaga niczego.
if (-not $SelfContained) {
    $aspnetX86 = "C:\Program Files (x86)\dotnet\shared\Microsoft.AspNetCore.App"
    $hasAspnet10 = (Test-Path $aspnetX86) -and
        (Get-ChildItem $aspnetX86 -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "10.*" })
    if (-not $hasAspnet10) {
        Write-Host "OSTRZEZENIE: nie wykryto ASP.NET Core 10 Runtime (x86)." -ForegroundColor Yellow
        Write-Host "Pobierz: https://dotnet.microsoft.com/download/dotnet/10.0 (Runtime, x86, ASP.NET Core)" -ForegroundColor Yellow
        Write-Host "Albo uzyj -SelfContained zeby pobrac wariant bez tej zaleznosci." -ForegroundColor Yellow
    }
}

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
                -UseBasicParsing -TimeoutSec 5
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

$zipName = "SubiektBridge-$Version-$variant.zip"
$zipUrl = "https://github.com/$Repo/releases/download/$Tag/$zipName"
$zipPath = Join-Path $env:TEMP $zipName

Write-Host "URL: $zipUrl"
Write-Host "Local: $zipPath"

try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -Headers $ghHeaders -UseBasicParsing
    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "OK: pobrano $sizeMB MB" -ForegroundColor Green
} catch {
    Write-Host "Invoke-WebRequest padl ($($_.Exception.Message)) - probuje Start-BitsTransfer..." -ForegroundColor Yellow
    try {
        Import-Module BitsTransfer -ErrorAction Stop
        Start-BitsTransfer -Source $zipUrl -Destination $zipPath -ErrorAction Stop
        $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "OK: pobrano $sizeMB MB (BITS)" -ForegroundColor Green
    } catch {
        Fail "Download zawiodl (Invoke-WebRequest + BITS). Sprawdz lacznosc z github.com: $($_.Exception.Message)"
    }
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
    # sc.exe stop jest asynchroniczny - nie blokuje jak Stop-Service.
    # Unikamy deadlocka gdy ten skrypt jest child processem serwisu
    # (Stop-Service czeka na zakonczenie child procesow).
    & sc.exe stop $ServiceName 2>$null
    $svc = Get-Service -Name $ServiceName
    $tries = 0
    while ($svc.Status -ne 'Stopped' -and $tries -lt 30) {
        Start-Sleep -Milliseconds 500
        $svc.Refresh()
        $tries++
    }
    if ($svc.Status -ne 'Stopped') {
        Write-Host "sc.exe stop timeout - probuje taskkill" -ForegroundColor Yellow
        $proc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" | Select-Object -ExpandProperty ProcessId
        if ($proc -and $proc -ne 0) {
            & taskkill /F /PID $proc 2>$null
            Start-Sleep -Seconds 2
        }
        $svc.Refresh()
        if ($svc.Status -ne 'Stopped') {
            Fail "Service nie zatrzymal sie w 15s (status: $($svc.Status))"
        }
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

# Detect: GitHub Actions ZIP ma struct $extractDir/win-x86/* (z 1 subfolderem).
# Jesli w extractDir jest tylko 1 folder - wejdz do niego (faktyczne pliki).
# Bez tego Copy-Item kopiowal CALY folder do C:\SubiektBridge\win-x86\
# zostawiajac stare binaria v0.7.x w C:\SubiektBridge\.
$rootContents = @(Get-ChildItem -Path $extractDir)
if ($rootContents.Count -eq 1 -and $rootContents[0].PSIsContainer) {
    $sourceDir = $rootContents[0].FullName
    Write-Host "INFO: ZIP ma subfolder '$($rootContents[0].Name)' - kopiuje z niego" -ForegroundColor DarkGray
} else {
    $sourceDir = $extractDir
}

# Skopiuj wszystko OPROCZ tych co zachowujemy.
$excludes = @("appsettings.Production.json", "data", "logs", "*.log")

Get-ChildItem -Path $sourceDir -Exclude $excludes | ForEach-Object {
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
        -UseBasicParsing -TimeoutSec 15
    Write-Host "OK: $($health.status) | bridge=$($health.bridge_version) | subiekt=$($health.subiekt_version) | sfera=$($health.sfera_session)" -ForegroundColor Green
    if ($health.last_error) {
        Write-Host "OSTRZEZENIE last_error: $($health.last_error)" -ForegroundColor Yellow
    }
} catch {
    $logDir = Join-Path $InstallDir "logs"
    Write-Host "OSTRZEZENIE: health endpoint nie odpowiada - sprawdz $logDir" -ForegroundColor Red
    Write-Host "Status serwisu: $($svc.Status)" -ForegroundColor Yellow
    exit 1
}

Write-Section "Update zakonczony"
Write-Host "Wersja: $Tag" -ForegroundColor Green
