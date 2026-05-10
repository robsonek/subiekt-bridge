#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Instaluje SubiektBridge jako Windows Service (natywnie, bez NSSM).

.DESCRIPTION
    Skrypt do uruchomienia raz, po pierwszym wgraniu binarki na hosta z Subiektem GT.
    Uzywa wbudowanego sc.exe + Microsoft.Extensions.Hosting.WindowsServices po stronie .NET
    (zero zewnetrznych narzedzi).

    Robi:
    - Sanity check (Subiekt zainstalowany, COM ProgID dziala)
    - Tworzy katalog C:\SubiektBridge\data\ (idempotency SQLite, PDFs)
    - Rejestruje Windows Service przez sc.exe z auto-startem i auto-restart on failure
    - Otwiera port 988 w Windows Firewall (TYLKO z IP marketplace-manage)
    - Uruchamia serwis i weryfikuje /api/v1/health

.PARAMETER InstallDir
    Docelowy katalog na binarki. Domyslnie C:\SubiektBridge\

.PARAMETER ServiceName
    Nazwa Windows Service. Domyslnie "SubiektBridge".

.PARAMETER LaravelHostIp
    OPCJONALNE. IP/IPs serwera(ow) marketplace-manage uprawnionych do polaczenia.
    Jesli podasz - skrypt utworzy regule Windows Firewall z whitelista.
    Jesli pominiesz - skrypt NIE TWORZY reguly firewall (zarzadzasz sam, np. router/perimeter firewall).

    Akceptuje:
    - pojedynczy adres:    "1.2.3.4"
    - wiele adresow:       "1.2.3.4","5.6.7.8"
    - zakres CIDR:         "10.0.0.0/24"
    - mieszane:            "1.2.3.4","10.0.0.0/24","2001:db8::/32"
    - "Any" (specjalna wartosc) - dla CALEGO internetu (TYLKO testy!)

.PARAMETER Port
    Port HTTPS (default 988). Bridge listen.

.EXAMPLE
    # Bez firewalla - sam zarzadzasz regula poza skryptem.
    .\install-windows.ps1

.EXAMPLE
    # Whitelist jednego IP.
    .\install-windows.ps1 -LaravelHostIp 1.2.3.4

.EXAMPLE
    # Whitelist kilku adresow / CIDR / IPv6.
    .\install-windows.ps1 -LaravelHostIp 1.2.3.4,10.0.0.0/24
#>

param(
    [string]   $InstallDir    = "C:\SubiektBridge",
    [string]   $ServiceName   = "SubiektBridge",
    [string[]] $LaravelHostIp,   # OPCJONALNE - patrz .PARAMETER LaravelHostIp
    [int]      $Port          = 988
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 on Windows Server 2016 does not support
# Invoke-WebRequest -SkipCertificateCheck. Bridge uses a self-signed cert,
# so accept it process-wide for this script.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

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
Test-Or-Die (Test-Path $configPath) "config produkcyjny istnieje: $configPath (jesli nie, skopiuj z .template i wypelnij)"

# Sprawdz ProgID Subiekta
try {
    $gt = New-Object -ComObject "InsERT.GT"
    Test-Or-Die ($gt -ne $null) "ProgID 'InsERT.GT' dziala (Sfera dostepna)"
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($gt) | Out-Null
} catch {
    Write-Host "FAIL: nie udalo sie utworzyc obiektu InsERT.GT - czy Subiekt + Sfera sa zainstalowane?" -ForegroundColor Red
    Write-Host "      Szczegoly: $_" -ForegroundColor Red
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
    Write-Host "Serwis '$ServiceName' juz istnieje - usuwam i tworze od nowa..." -ForegroundColor Yellow
    if ($existingService.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# binPath: scPath musi byc ABSOLUTNA sciezka, w cudzyslowie jesli zawiera spacje.
$binPathArg = "`"$exePath`""

sc.exe create $ServiceName binPath= $binPathArg `
    DisplayName= "Subiekt Bridge" `
    start= auto `
    obj= "LocalSystem" | Out-Null
Test-Or-Die ($LASTEXITCODE -eq 0) "sc.exe create"

sc.exe description $ServiceName "HTTP bridge marketplace-manage <-> Subiekt GT (Sfera)" | Out-Null

# Auto-restart przy crashu: 3 razy z opoznieniem 60 sekund.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Test-Or-Die ($LASTEXITCODE -eq 0) "sc.exe failure (auto-restart skonfigurowany)"

# ASPNETCORE_ENVIRONMENT=Production na poziomie serwisu (Windows 10+)
# Multi-string na poziomie sc.exe wymaga `\0` separator, wiec uzywamy registry:
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $envKey -Name "Environment" -Type MultiString -Value @(
    "ASPNETCORE_ENVIRONMENT=Production"
)
Write-Host "OK: ASPNETCORE_ENVIRONMENT=Production ustawione w rejestrze serwisu" -ForegroundColor Green

# ---------------------- Firewall (opcjonalny) ----------------------
Write-Section "Windows Firewall"

$ruleName = "SubiektBridge-HTTPS-In"

if (-not $LaravelHostIp -or $LaravelHostIp.Count -eq 0) {
    # Bez parametru - skrypt nie zarzadza regula. Uzytkownik konfiguruje sam
    # (np. zewnetrzny perimeter firewall albo recznie).
    Write-Host "Pominieto konfiguracje Windows Firewall (brak -LaravelHostIp)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Pamietaj samodzielnie zezwolic na ruch TCP/$Port z konkretnych IP. Przyklad:" -ForegroundColor Yellow
    Write-Host "  New-NetFirewallRule -DisplayName '$ruleName' \`" -ForegroundColor Gray
    Write-Host "    -Direction Inbound -Protocol TCP -LocalPort $Port \`" -ForegroundColor Gray
    Write-Host "    -RemoteAddress 1.2.3.4,5.6.7.8 -Action Allow" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Albo skonfiguruj reguly na zewnetrznym firewallu/routerze." -ForegroundColor Yellow
}
elseif ($LaravelHostIp.Count -eq 1 -and $LaravelHostIp[0] -ieq "Any") {
    # Specjalna wartosc "Any" - otwiera dla calego internetu (testy!).
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "OSTRZEZENIE: otwierasz port $Port dla CALEGO internetu - tylko do testow!" -ForegroundColor Red
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound `
        -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    Write-Host "OK: regula $ruleName ($Port/tcp z Any)" -ForegroundColor Green
}
else {
    # Whitelist konkretnych IP / CIDR. New-NetFirewallRule -RemoteAddress przyjmuje string[].
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -RemoteAddress $LaravelHostIp -Action Allow | Out-Null
    $ipsDisplay = $LaravelHostIp -join ", "
    Write-Host "OK: regula $ruleName ($Port/tcp z $ipsDisplay)" -ForegroundColor Green
}

# ---------------------- Start ----------------------
Write-Section "Start serwisu"

Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

# Health check
$healthUrl = "https://localhost:$Port/api/v1/health"
try {
    $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -eq 200) {
        Write-Host "OK: $healthUrl zwraca 200" -ForegroundColor Green
        Write-Host $response.Content
    } else {
        Write-Host "OSTRZEZENIE: $healthUrl zwrocil $($response.StatusCode)" -ForegroundColor Yellow
        Write-Host $response.Content
    }
} catch {
    Write-Host "FAIL: nie udalo sie zapytac $healthUrl" -ForegroundColor Red
    Write-Host "      Sprawdz logi: $logsDir\subiekt-bridge-*.log" -ForegroundColor Yellow
    Write-Host "      Sprawdz status: Get-Service $ServiceName" -ForegroundColor Yellow
    Write-Host "      Event Log: Get-WinEvent -LogName Application | Where-Object Source -eq '$ServiceName'" -ForegroundColor Yellow
    exit 1
}

Write-Section "Gotowe"
Write-Host "Bridge dziala jako Windows Service '$ServiceName'." -ForegroundColor Green
Write-Host ""
Write-Host "Zarzadzanie serwisem:" -ForegroundColor Cyan
Write-Host "  Start-Service $ServiceName"
Write-Host "  Stop-Service $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  Get-Service $ServiceName"
Write-Host ""
Write-Host "Logi:" -ForegroundColor Cyan
Write-Host "  $logsDir\subiekt-bridge-*.log  (Serilog rolling, 30 dni)"
Write-Host "  Get-WinEvent -LogName Application | Where-Object Source -eq '$ServiceName' | Select -First 20"
Write-Host ""
Write-Host "Nastepne kroki w marketplace-manage (.env na serwerze Linux):" -ForegroundColor Cyan
Write-Host "  SUBIEKT_BRIDGE_URL=https://<IP-tego-Windowsa>:$Port"
Write-Host "  SUBIEKT_BRIDGE_TOKEN=<ten-sam-token-co-w-appsettings.Production.json>"
Write-Host "  SUBIEKT_BRIDGE_VERIFY_TLS=false  # dla self-signed cert"
