# Deployment SubiektBridge na Windows klienta

## Wymagania na hoście Windows

| Element | Wersja | Uwagi |
|---|---|---|
| Windows | 10 / 11 / Server 2019+ | x64 |
| Subiekt GT | dowolna stabilna | **musi być x64** (ma być spójne z bit-level Bridge'a) |
| Sfera dla Subiekta | aktywna licencja | sprawdź w InsERT przed deployem |
| Operator Subiekta | dedykowany dla integracji | rekomendacja: użytkownik "INTEGRACJA" |
| .NET Runtime | **niepotrzebny** | self-contained binarka ma runtime wbudowany |

> Bridge rejestruje się jako Windows Service przez wbudowane `sc.exe` (Service Control Manager).
> Zero zewnętrznych narzędzi typu NSSM/WinSW — wszystko po stronie .NET przez
> `Microsoft.Extensions.Hosting.WindowsServices`.

> **UWAGA dot. bit-level**: `InsERT.GT` to in-process COM. Bridge x64 NIE połączy się z Subiektem x86. Jeśli klient ma Subiekta 32-bit — przebudujemy Bridge'a jako `win-x86`.

## Krok 1 — Wgranie binarki

### Opcja A — pobierz prebuilt z GitHub Releases (rekomendowane)

Każdy tag `vX.Y.Z` w repozytorium auto-buildujemy GitHub Actions i publikujemy `SubiektBridge-win-x64.zip` jako asset Release. Pobierz najnowszy z: https://github.com/robsonek/subiekt-bridge/releases

### Opcja B — zbuduj lokalnie

Wymaga .NET SDK 10+ na maszynie buildującej (macOS/Linux/Windows):

```bash
git clone https://github.com/robsonek/subiekt-bridge.git
cd subiekt-bridge
./scripts/publish-win.sh
# Output: publish/win-x64/ (~108 MB, self-contained)
```

Skopiuj cały folder `publish/win-x64/` na Windowsa klienta — np. do `C:\SubiektBridge\`. Sposoby:
- `scp -r publish/win-x64/* user@WIN-HOST:/c/SubiektBridge/` (jeśli OpenSSH na Windowsie)
- `rsync -avz publish/win-x64/ user@WIN-HOST:/c/SubiektBridge/`
- ZIP + ręcznie

Dorzuć też pliki z `deploy/`:
- `install-windows.ps1`
- `update-windows.ps1`

## Krok 2 — Konfiguracja produkcyjna

Na Windowsie klienta:

```powershell
cd C:\SubiektBridge\
Copy-Item appsettings.Production.json.template appsettings.Production.json
notepad appsettings.Production.json
```

Wypełnij:

| Pole | Wartość |
|---|---|
| `Bridge.Token` | wygeneruj losowy 64+ znaków, np. PowerShell: `[Convert]::ToBase64String((1..48 \| ForEach-Object { Get-Random -Maximum 256 }))` |
| `Subiekt.Server` | nazwa instancji MSSQL (np. `.\SQLEXPRESS` dla lokalnej, lub `HOST\NAZWA`) |
| `Subiekt.Database` | nazwa bazy Subiekta (sprawdź w Subiekt → Plik → Otwórz podmiot) |
| `Subiekt.DbUser` / `DbPassword` | login do MSSQL (zwykły SQL Auth) |
| `Subiekt.Operator` / `OperatorPassword` | login operatora Subiekta + jego hasło (PLAINTEXT — Bridge sam zaszyfruje przez `InsERT.Dodatki.Szyfruj()`) |
| `Subiekt.PdfTemplateId` | `null` = domyślny wzorzec, lub ID custom wzorca |

Zapamiętaj `Bridge.Token` — wpiszesz go w `.env` Laravela.

## Krok 3 — Instalacja jako Windows Service

PowerShell **jako Administrator**:

```powershell
cd C:\SubiektBridge\

# Bez Windows Firewall (zarządzasz blokadą sam - router/perimeter firewall):
.\install-windows.ps1

# Jeden adres IP (skrypt utworzy regułę whitelist):
.\install-windows.ps1 -LaravelHostIp 1.2.3.4

# Kilka IP (np. prod + staging Laravela):
.\install-windows.ps1 -LaravelHostIp 1.2.3.4,5.6.7.8

# Cały podsieć CIDR:
.\install-windows.ps1 -LaravelHostIp 10.0.0.0/24

# Mieszane (IPv4 + CIDR + IPv6):
.\install-windows.ps1 -LaravelHostIp 1.2.3.4,10.0.0.0/24,2001:db8::/32

# TYLKO testy - otwarcie dla całego internetu (z ostrzeżeniem):
.\install-windows.ps1 -LaravelHostIp Any
```

> **Bez `-LaravelHostIp`** skrypt nie tyka Windows Firewalla — wyświetla tylko
> przykładową komendę. Załatw sobie blokadę sam (np. zewnętrzny firewall, router,
> ręczna `New-NetFirewallRule`).

Skrypt zrobi:
1. Sanity check: czy plik `.exe` istnieje, czy NSSM jest w PATH, czy `New-Object -ComObject "InsERT.GT"` działa.
2. Utworzy katalogi `C:\SubiektBridge\data\` (idempotency SQLite) i `C:\SubiektBridge\logs\`.
3. Zarejestruje Windows Service `SubiektBridge` z auto-startem.
4. Doda regułę firewall: TCP 988 dostępny **tylko z IP Laravela**.
5. Wystartuje serwis i sprawdzi `https://localhost:988/api/v1/health`.

Jeśli któryś krok zawiedzie — skrypt pokaże dokładnie co (z kodem wyjścia ≠ 0).

## Krok 4 — Konfiguracja Laravela (na serwerze Linux)

Na serwerze marketplace-manage, edytuj `.env`:

```bash
SUBIEKT_BRIDGE_URL=https://WIN-HOST-IP:988
SUBIEKT_BRIDGE_TOKEN=<dokładnie ten sam co w appsettings.Production.json>
SUBIEKT_BRIDGE_VERIFY_TLS=false   # self-signed cert na początek
SUBIEKT_BRIDGE_TIMEOUT=60         # FV może wystawiać się kilkanaście sekund

# Mapping form płatności na ID słownika Subiekta (do ustalenia z klientem!):
SUBIEKT_PAYMENT_TRANSFER_ID=1
SUBIEKT_PAYMENT_COD_ID=2
SUBIEKT_PAYMENT_CARD_ID=3
SUBIEKT_PAYMENT_CASH_ID=4
```

```bash
php artisan config:clear
```

## Krok 5 — Test end-to-end

### a) Z Linuxa, smoke health:

```bash
curl -k https://WIN-HOST-IP:988/api/v1/health
# Powinien zwrócić: {"status":"ok","subiekt_version":"1.78.0",...}
```

### b) Z Laravela, wystaw FV testową (przez Tinker):

```bash
# Na serwerze z marketplace-manage
cd /path/to/marketplace-manage

php artisan tinker --execute="
\$order = App\Modules\Order\Models\Order::find(<ID-zamowienia-testowego>);
App\Modules\Invoicing\Jobs\IssueInvoiceJob::dispatchSync(\$order->id);
\$invoice = \$order->invoices()->latest()->first();
echo 'Status: ' . \$invoice->status . PHP_EOL;
echo 'Numer:  ' . \$invoice->number . PHP_EOL;
echo 'Sub ID: ' . \$invoice->subiekt_id . PHP_EOL;
"
```

### c) Z UI Filament:

Admin → Zamówienia → wybierz zamówienie → akcja "Wystaw FV (Subiekt)" → zatwierdź.
Status zobaczysz w Admin → Sprzedaż → Faktury.

## Update (kolejne deploye)

Na macOS:
```bash
./scripts/publish-win.sh
# Skopiuj publish/win-x64/* na Windowsa do tymczasowego folderu, np. C:\SubiektBridge\new\
```

Na Windowsie (jako Administrator):
```powershell
cd C:\SubiektBridge\new\
.\update-windows.ps1
# Skrypt: stop service -> kopiuj nowe pliki (zachowując appsettings.Production.json i data/) -> start -> health check
```

## Diagnostyka

| Problem | Sprawdź |
|---|---|
| Service nie startuje | `Get-Service SubiektBridge`; logi w `C:\SubiektBridge\logs\subiekt-bridge-*.log` lub `Get-WinEvent -LogName Application \| Where-Object Source -eq 'SubiektBridge'` |
| `/health` zwraca 503 + `sfera_session: down` | Czy Subiekt GT odpalony jako program? Sprawdź `New-Object -ComObject "InsERT.GT"` w PowerShell |
| 401 z Laravela | Token się nie zgadza między `.env` a `appsettings.Production.json` |
| 400 `MISSING_IDEMPOTENCY_KEY` | Klient (Laravel) nie wysyła nagłówka `Idempotency-Key` (regression — sprawdź `SubiektBridgeClient::mutate`) |
| 422 `MISSING_PRODUCT` | Towar o tym EAN nie istnieje w Subiekcie. Bridge fallbackuje do "usługi jednorazowej" tylko gdy EAN jest pusty; gdy EAN podany ale nieznany — fail. |
| 502 `SUBIEKT_UNAVAILABLE` | Sesja Sfery padła. Bridge auto-reconnectuje na następnym requeście. Logi pokażą `Sfera health check failed`. |
| Polskie znaki połamane na FV | Sprawdź `Subiekt.Encoding` (powinno być `windows-1250`); na Windowsie kompileruje z `CodePagesEncodingProvider` rejestrowany w `RealSferaSession` ctor. |

## Bezpieczeństwo

- Token w `appsettings.Production.json` ma uprawnienia 0600 (read-only przez user serwisu) — `icacls C:\SubiektBridge\appsettings.Production.json /inheritance:r /grant:r "NT AUTHORITY\SYSTEM:R"`.
- Firewall otwiera 988 **tylko z IP Laravela** (parametr `-LaravelHostIp` w `install-windows.ps1`).
- Self-signed cert na MVP — wystarczy bo `SUBIEKT_BRIDGE_VERIFY_TLS=false` po stronie Laravela. Dla pełnej produkcji można później wystawić Let's Encrypt jeśli host ma publiczny DNS.
