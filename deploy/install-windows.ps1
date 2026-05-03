#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Instaluje SubiektBridge jako Windows Service (przez NSSM).

.DESCRIPTION
    Skrypt do uruchomienia raz, po pierwszym wgraniu binarki na hosta z Subiektem GT.
    Robi:
    - Sanity check (Subiekt zainstalowany, NSSM dostępny, COM ProgID działa)
    - Tworzy katalog C:\SubiektBridge\data\ (idempotency SQLite, PDFs)
    - Rejestruje Windows Service z auto-startem przez NSSM
    - Otwiera port 8443 w Windows Firewall (TYLKO z IP marketplace-manage)
    - Uruchamia serwis i weryfikuje /api/v1/health

.PARAMETER InstallDir
    Docelowy katalog na binarki. Domyślnie C:\SubiektBridge\

.PARAMETER ServiceName
    Nazwa Windows Service. Domyślnie "SubiektBridge".

.PARAMETER LaravelHostIp
    IP serwera marketplace-manage (Linux). Wymagane dla reguły firewalla.
    Wstaw "0.0.0.0/0" tylko do testów - na produkcji zawsze konkretny adres.

.EXAMPLE
    .\install-windows.ps1 -LaravelHostIp 1.2.3.4

    .\install-windows.ps1 -InstallDir D:\SubiektBridge -LaravelHostIp 1.2.3.4
#>

param(
    [string] $InstallDir   = "C:\SubiektBridge",
    [string] $ServiceName  = "SubiektBridge",
    [Parameter(Mandatory=$true)]
    [string] $LaravelHostIp,
    [int]    $Port         = 8443
)

$ErrorActionPreference = "Stop"

function Write-Section($text) {
    Write-Host ""
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Test-Or-Die($condition, $message) {
    if (-not $condition) {
        Write-Host "FAIL: $message" -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: $message" -ForegroundColor Green
}

# ---------------------- Sanity checks ----------------------
Write-Section "Sanity checks"

$exePath = Join-Path $InstallDir "SubiektBridge.Api.exe"
Test-Or-Die (Test-Path $exePath) "binarka na miejscu: $exePath"

$configPath = Join-Path $InstallDir "appsettings.Production.json"
Test-Or-Die (Test-Path $configPath) "config produkcyjny istnieje: $configPath (jeśli nie, skopiuj z .template i wypełnij)"

# Sprawdź ProgID Subiekta
try {
    $gt = New-Object -ComObject "InsERT.GT"
    Test-Or-Die ($gt -ne $null) "ProgID 'InsERT.GT' działa (Sfera dostępna)"
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($gt) | Out-Null
} catch {
    Write-Host "FAIL: nie udało się utworzyć obiektu InsERT.GT - czy Subiekt + Sfera są zainstalowane?" -ForegroundColor Red
    Write-Host "      Szczegóły: $_" -ForegroundColor Red
    exit 1
}

# Sprawdź NSSM
$nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue
if (-not $nssm) {
    Write-Host "FAIL: NSSM nie znaleziony w PATH. Zainstaluj z https://nssm.cc/download" -ForegroundColor Red
    Write-Host "      (rekomendowane: rozpakuj win64\nssm.exe do C:\Windows\System32\)" -ForegroundColor Yellow
    exit 1
}
Test-Or-Die $true "NSSM dostępne: $($nssm.Source)"

# ---------------------- Katalog danych ----------------------
Write-Section "Przygotowanie katalogu danych"

$dataDir = Join-Path $InstallDir "data"
$logsDir = Join-Path $InstallDir "logs"
foreach ($dir in @($dataDir, $logsDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
        Write-Host "Utworzono: $dir" -ForegroundColor Green
    } else {
        Write-Host "Istnieje:  $dir" -ForegroundColor Gray
    }
}

# ---------------------- Windows Service ----------------------
Write-Section "Windows Service ($ServiceName)"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Serwis '$ServiceName' już istnieje - aktualizuję konfigurację..." -ForegroundColor Yellow
    nssm stop $ServiceName 2>&1 | Out-Null
    nssm remove $ServiceName confirm 2>&1 | Out-Null
}

nssm install $ServiceName $exePath
nssm set $ServiceName AppDirectory $InstallDir
nssm set $ServiceName AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production"
nssm set $ServiceName Start SERVICE_AUTO_START
nssm set $ServiceName AppStdout "$logsDir\stdout.log"
nssm set $ServiceName AppStderr "$logsDir\stderr.log"
nssm set $ServiceName AppRotateFiles 1
nssm set $ServiceName AppRotateBytes 10485760  # 10 MB rotation
nssm set $ServiceName Description "HTTP bridge marketplace-manage <-> Subiekt GT (Sfera)"

Write-Host "OK: serwis zarejestrowany" -ForegroundColor Green

# ---------------------- Firewall ----------------------
Write-Section "Windows Firewall (port $Port -> $LaravelHostIp)"

$ruleName = "SubiektBridge-HTTPS-In"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ($LaravelHostIp -eq "0.0.0.0/0") {
    Write-Host "OSTRZEŻENIE: otwierasz port $Port dla CAŁEGO internetu - tylko do testów!" -ForegroundColor Red
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
} else {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -RemoteAddress $LaravelHostIp -Action Allow | Out-Null
}
Test-Or-Die $true "reguła firewall: $ruleName ($Port/tcp z $LaravelHostIp)"

# ---------------------- Start ----------------------
Write-Section "Start serwisu"

nssm start $ServiceName
Start-Sleep -Seconds 5

# Health check (lokalnie, bez weryfikacji TLS - self-signed cert OK na początek)
$healthUrl = "https://localhost:$Port/api/v1/health"
try {
    $response = Invoke-WebRequest -Uri $healthUrl -SkipCertificateCheck -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -eq 200) {
        Write-Host "OK: $healthUrl zwraca 200" -ForegroundColor Green
        Write-Host $response.Content
    } else {
        Write-Host "OSTRZEŻENIE: $healthUrl zwrócił $($response.StatusCode)" -ForegroundColor Yellow
        Write-Host $response.Content
    }
} catch {
    Write-Host "FAIL: nie udało się zapytać $healthUrl" -ForegroundColor Red
    Write-Host "      Sprawdź logi: $logsDir\stderr.log" -ForegroundColor Yellow
    Write-Host "      Sprawdź status: Get-Service $ServiceName" -ForegroundColor Yellow
    exit 1
}

Write-Section "Gotowe"
Write-Host "Bridge działa jako Windows Service '$ServiceName'." -ForegroundColor Green
Write-Host ""
Write-Host "Następne kroki w marketplace-manage (.env na serwerze Linux):" -ForegroundColor Cyan
Write-Host "  SUBIEKT_BRIDGE_URL=https://<IP-tego-Windowsa>:$Port"
Write-Host "  SUBIEKT_BRIDGE_TOKEN=<ten-sam-token-co-w-appsettings.Production.json>"
Write-Host "  SUBIEKT_BRIDGE_VERIFY_TLS=false  # dla self-signed cert"
Write-Host ""
Write-Host "Test z Linuxa: curl -k https://<IP-tego-Windowsa>:$Port/api/v1/health"
