#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Instaluje SubiektBridge jako Windows Service (natywnie, bez NSSM).

.DESCRIPTION
    Skrypt do uruchomienia raz, po pierwszym wgraniu binarki na hosta z Subiektem GT.
    Używa wbudowanego sc.exe + Microsoft.Extensions.Hosting.WindowsServices po stronie .NET
    (zero zewnętrznych narzędzi).

    Robi:
    - Sanity check (Subiekt zainstalowany, COM ProgID działa)
    - Tworzy katalog C:\SubiektBridge\data\ (idempotency SQLite, PDFs)
    - Rejestruje Windows Service przez sc.exe z auto-startem i auto-restart on failure
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

# ---------------------- Windows Service (sc.exe) ----------------------
Write-Section "Windows Service ($ServiceName)"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Serwis '$ServiceName' już istnieje - usuwam i tworzę od nowa..." -ForegroundColor Yellow
    if ($existingService.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# binPath: scPath musi być ABSOLUTNĄ ścieżką, w cudzysłowie jeśli zawiera spacje.
$binPathArg = "`"$exePath`""

sc.exe create $ServiceName binPath= $binPathArg `
    DisplayName= "Subiekt Bridge" `
    start= auto `
    obj= "LocalSystem" | Out-Null
Test-Or-Die ($LASTEXITCODE -eq 0) "sc.exe create"

sc.exe description $ServiceName "HTTP bridge marketplace-manage <-> Subiekt GT (Sfera)" | Out-Null

# Auto-restart przy crashu: 3 razy z opóźnieniem 60 sekund.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Test-Or-Die ($LASTEXITCODE -eq 0) "sc.exe failure (auto-restart skonfigurowany)"

# ASPNETCORE_ENVIRONMENT=Production na poziomie serwisu (Windows 10+)
# Multi-string na poziomie sc.exe wymaga `\0` separator, więc używamy registry:
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $envKey -Name "Environment" -Type MultiString -Value @(
    "ASPNETCORE_ENVIRONMENT=Production"
)
Write-Host "OK: ASPNETCORE_ENVIRONMENT=Production ustawione w rejestrze serwisu" -ForegroundColor Green

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

Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

# Health check
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
    Write-Host "      Sprawdź logi: $logsDir\subiekt-bridge-*.log" -ForegroundColor Yellow
    Write-Host "      Sprawdź status: Get-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "      Event Log: Get-WinEvent -LogName Application | Where-Object Source -eq '$ServiceName'" -ForegroundColor Yellow
    exit 1
}

Write-Section "Gotowe"
Write-Host "Bridge działa jako Windows Service '$ServiceName'." -ForegroundColor Green
Write-Host ""
Write-Host "Zarządzanie serwisem:" -ForegroundColor Cyan
Write-Host "  Start-Service $ServiceName"
Write-Host "  Stop-Service $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  Get-Service $ServiceName"
Write-Host ""
Write-Host "Logi:" -ForegroundColor Cyan
Write-Host "  $logsDir\subiekt-bridge-*.log  (Serilog rolling, 30 dni)"
Write-Host "  Get-WinEvent -LogName Application | Where-Object Source -eq '$ServiceName' | Select -First 20"
Write-Host ""
Write-Host "Następne kroki w marketplace-manage (.env na serwerze Linux):" -ForegroundColor Cyan
Write-Host "  SUBIEKT_BRIDGE_URL=https://<IP-tego-Windowsa>:$Port"
Write-Host "  SUBIEKT_BRIDGE_TOKEN=<ten-sam-token-co-w-appsettings.Production.json>"
Write-Host "  SUBIEKT_BRIDGE_VERIFY_TLS=false  # dla self-signed cert"
