# SubiektBridge

Most HTTP→COM/Sfera dla Subiekta GT. Stoi na Windowsie obok Subiekta klienta,
udostępnia HTTPS REST API z którego korzysta Laravel-owy konsument
(np. marketplace-manage).

## Architektura

```
Klient HTTP (Linux/Mac)        SubiektBridge (Windows Service)        Subiekt GT
  cURL / Postman                ASP.NET Core 10 (win-x86)               InsERT.GT (32-bit COM)
  Laravel SubiektBridgeClient   ┌─────────────────────────┐             ┌──────────────────┐
                       HTTPS    │ Controllers             │   COM/STA   │ SuDokumentyMgr   │
                     X-Bridge   │ ↓                       │   worker    │ Towary           │
                      Token     │ ISferaSession           │  ─────────► │ Kontrahenci      │
                     ────────► │   ↓ RealSferaSession    │             │ Baza             │
                                │   ↓ FakeSferaSession    │             └──────────────────┘
                                │ IdempotencyStore (SQLite)│             MSSQL (lokalnie)
                                │ Microsoft.Data.SqlClient │  ────────►  - kh__Kontrahent
                                └─────────────────────────┘             - dok__Dokument
                                                                         - adr__Ewid
                                                                         - sl_FormaPlatnosci
```

**Stack:** .NET 10, ASP.NET Core, **win-x86** (Subiekt GT to wyłącznie 32-bit),
Windows Service via `Microsoft.Extensions.Hosting.WindowsServices` (zero NSSM).

**Bezpieczeństwo:** HTTPS auto-generated self-signed cert (`data/cert.pfx`),
statyczny `X-Bridge-Token` w nagłówku, opcjonalny IP whitelist w Windows Firewall.

## Build / Release

```bash
# Lokalnie (macOS/Linux z .NET 10 SDK)
dotnet build SubiektBridge.sln

# Self-contained (~46 MB, runtime wbudowany)
./scripts/publish-win.sh

# Release przez tag (GitHub Actions auto-builduje 2 ZIP-y)
git tag -a vX.Y.Z -m "..."
git push origin vX.Y.Z
```

GitHub Actions matrix: **win-x86 only**. x64 nie obsługujemy - in-proc COM
wymaga że bit-level Bridge'a pasuje do Subiekta (linia GT to wyłącznie 32-bit;
Subiekt Nexo x64 to inny produkt, nie targetujemy).

## Deployment na Windowsie klienta

```powershell
# Pierwsza instalacja (jako Admin)
cd C:\SubiektBridge
.\install-windows.ps1 -LaravelHostIp 1.2.3.4

# Update przez self-update endpoint (wszystkie kolejne)
Invoke-RestMethod -Uri "https://localhost:988/api/v1/admin/update" -Method POST `
  -Headers @{'X-Bridge-Token'='<TOKEN>';'Content-Type'='application/json'} -Body '{}'

# Albo manualny update (`update-bridge.ps1` self-contained, sam pobiera ZIP)
.\update-bridge.ps1
```

## Endpointy

| Endpoint | Funkcja |
|---|---|
| `GET /api/v1/health` | Sfera session status + Subiekt version |
| `GET /api/v1/products?ean=` | Lookup towaru |
| `GET /api/v1/contractors?nip=` | Lookup kontrahenta po NIP |
| `GET /api/v1/invoices?from&to&type&notes_contains&nip&limit` | Listing FS/KFS (filtry whitelist) |
| `GET /api/v1/invoices/{id}` | Single FV metadata |
| `GET /api/v1/invoices/{id}/pdf` | Retro PDF generation |
| `POST /api/v1/invoices` | Wystaw FS (Idempotency-Key required) |
| `POST /api/v1/invoices/{id}/corrections` | Wystaw KFS |
| `GET /api/v1/receipts?...` | Listing PZ |
| `GET /api/v1/receipts/{id}` / `/pdf` | Single PZ + retro PDF |
| `POST /api/v1/receipts` | Wystaw PZ (dropshipping) |
| `POST /api/v1/admin/query` | Read-only SQL (whitelist SELECT/WITH) |
| `POST /api/v1/admin/update` | Self-update Bridge'a (detached PowerShell) |
| `POST /api/v1/sfera/raw` | Escape hatch (whitelist metod w configu) |

Wszystkie wymagają nagłówka `X-Bridge-Token: <secret>`. Operacje mutujące
(POST `/invoices`, `/corrections`, `/receipts`) wymagają też `Idempotency-Key`.

## Krytyczne wzorce (każdy z nich kosztował debug session)

### 1. STA threading dla COM in-proc

`InsERT.GT` jest **STA (apartment-threaded)**. ASP.NET Core thread pool jest MTA.
Próba `Activator.CreateInstance(InsERT.GT)` z MTA threada rzuca `0x8000FFFF E_UNEXPECTED`.

**Rozwiązanie:** dedykowany Thread z `SetApartmentState(STA)` + `BlockingCollection<Action>`
jako kolejka. Wszystkie metody `RealSferaSession` używają `RunOnStaAsync(Func<T>)`.

PowerShell sanity check (`New-Object -ComObject "InsERT.GT"`) **przejdzie** mimo że
Bridge faila - bo PS jest STA z natury.

### 2. Bit-level COM in-proc

Bridge x64 nie połączy się z Subiektem x86 - `0x8000FFFF Katastrofalny błąd` z
`Activator.CreateInstance`. Cała linia "GT" InsERT to 32-bit; Subiekt Nexo (x64)
to osobny produkt.

### 3. HTTPS cert auto-gen

Kestrel default chce `dotnet dev-certs https` którego nie ma na świeżym Windows
Server 2016+. `Program.cs::EnsureSelfSignedCertificate` (przed `builder.Build()`)
generuje 2048-bit RSA cert do `data/cert.pfx` jeśli nie istnieje. SAN: hostname +
localhost + 127.0.0.1 + ::1. Klient (Laravel) używa `verify=false`.

### 4. Auto-create folderu dla SQLite

`IdempotencyStore` ctor robi `Directory.CreateDirectory(Path.GetDirectoryName(path))`
przed `conn.Open()` — SQLite tworzy plik bazy auto, ale **nie folder rodzica**.
Bez tego Error 14 "unable to open database file".

### 5. Logi - absolute path

Windows Service ma `WorkingDirectory=C:\Windows\System32` (default). Relative
`logs/` w Serilog config trafiało gdzie indziej, folder `C:\SubiektBridge\logs\`
był pusty. Fix: `Path.Combine(AppContext.BaseDirectory, "logs", ...)`.

## Pułapki Sfery / Subiekt API

### Asymetria magazynów: FS vs PZ

- **FS:** `SuDokument.MagazynNadawczyId` rzuca `NotImplementedException` przez COM.
  Trzeba `SuPozycja.MagazynId` **per pozycja** po `Pozycje.Dodaj(towar)`.
- **PZ:** Odwrotnie - `SuDokument.MagazynOdbiorczyId` MUSI być set na dokumencie
  (mapuje na `dok_MagId`). Bez tego `pz.Zapisz()` rzuca `0x80004005`.

### `dok_NumerPelny` to atrybut COM, NIE kolumna SQL

`SQL filter "dok_NumerPelny LIKE 'FS %'"` rzuca syntax error. To computed atrybut
Sfery (z `dok_TypNr + dok_Nr/dok_Rok`). Filtruj client-side po pobraniu kolekcji.

### `OtworzKolekcje` zwraca duchy

Rekordy z `dok__Dokument` po anulacji wciąż w wynikach `OtworzKolekcje(filtr)`.
Anti-duplicate check **musi weryfikować** że `WczytajDokument(id)` zwraca obiekt
przed traktowaniem jako duplikat.

### NIP w `adr__Ewid`, NIE w `kh__Kontrahent`

```sql
SELECT k.kh_Id
FROM kh__Kontrahent k
JOIN adr__Ewid a ON a.adr_IdObiektu = k.kh_Id AND a.adr_TypAdresu = 1
WHERE a.adr_NIP = @nip
```

### Symbol kontrahenta - limit 16 znaków

`Kontrahenci.Symbol` w Subiekcie ma hard limit 16 znaków + ograniczone znaki
(litery/cyfry/-/_). Email z `@` `+` lub UUID Allegro przekracza i MSSQL rzuca
`0x80040E21` (multi-step OLE DB).

### `LiczonyOdCenBrutto + Rozliczony=true` konwertuje formę płatności

Z dokumentacji `SuDokument_PlatnoscPrzelewKwota.htm`:

> "jesli zostanie ustawione: `PlatnoscPrzelewKwota=0` i `Rozliczony=True`
> to przy zapisie dokumentu zostanie wykonane:
> `PlatnoscPrzelewKwota := PlatnoscKredytKwota` i `PlatnoscKredytKwota := 0`"

Sfera **automatycznie konwertuje** PlatnoscKredyt na PlatnoscPrzelew gdy oba
są spełnione. Dla form odroczonych (kredyt kupiecki, "Allegro Pay") **musi być
`Rozliczony=false`**.

### `Subiekt.Baza.PolaczenieAdoNet` - SqlConnection jako ComObject

Sfera dokumentacja mówi że `PolaczenieAdoNet` zwraca `System.Data.SqlClient.SqlConnection`,
ale w realu przychodzi jako `__ComObject` - dynamic binder NIE widzi metod
(`CreateCommand` rzuca `RuntimeBinderException`).

**Rozwiązanie:** Bridge robi własny `Microsoft.Data.SqlClient.SqlConnection` z
opcji `Subiekt:Server/Database/DbUser/DbPassword` (te same credentials co Sfera).

### `PlatnoscPrzelewId` NIE ISTNIEJE

Tylko `PlatnoscPrzelewKwota` (Sfera traktuje przelew jako "zapłacono" bez ID
słownika). Inne formy mają Id:
- `PlatnoscKredytId` → `sl_FormaPlatnosci` (fp_Typ=0)
- `PlatnoscKartaId` → `sl_FormaPlatnosci` (Sfera używa tego samego słownika dla obu)
- `PlatnoscRatyId`
- `PlatnoscGotowka*` - tylko Kwota + Reszta, NIE Id

`PaymentDto.MethodSubiektId` jest **`int?`** (nullable) - dla form bez Id
(Gotowka, Przelew) Bridge ustawia tylko `*Kwota`.

### Native Windows Service przez `sc.exe`

`Microsoft.Extensions.Hosting.WindowsServices` + `builder.Host.UseWindowsService()` -
no-op gdy nie running as service. Ten sam binarka działa interaktywnie i jako service.
Zero NSSM (NSSM 2.24 z 2014).

## Idempotency (3 warstwy)

1. **`Idempotency-Key` header** - SQLite cache (TTL 30 dni). Replay zwraca
   cached response, ALE weryfikujemy że cached `subiekt_id` wciąż istnieje
   (gdy user anulował FV w Subiekcie - cache invalidate + new request).
2. **Anti-duplicate w Subiekcie** - przed `DodajFS` szuka `dok_Uwagi LIKE
   '%external_reference%'` + verify przez `WczytajDokument`. Match → 409
   `DUPLICATE_INVOICE` z `existing_subiekt_id` w details.
3. **Klient (Laravel)** - `UNIQUE(order_id, type)` w DB + `ShouldBeUnique` na jobie.

## Self-update flow

```
POST /api/v1/admin/update {refresh_script: true}
  ↓
Bridge:
  1. (opcjonalnie) GET https://raw.githubusercontent.com/.../update-bridge.ps1 → C:\SubiektBridge\
  2. Process.Start("cmd.exe", "/c timeout 5 & powershell update-bridge.ps1 -Force")
  3. Return 202 Accepted (klient ma 5s na otrzymanie response)
  ↓ (5s później)
Detached PowerShell (przeżyje śmierć Bridge'a):
  1. Detect latest tag z GitHub Releases API
  2. Download SubiektBridge-X.Y.Z-win-x86-fxdep.zip → %TEMP%
  3. Stop-Service SubiektBridge
  4. Backup appsettings.Production.json (in-memory)
  5. Copy bin do C:\SubiektBridge\ (zachowując data/, logs/)
  6. Restore appsettings
  7. Start-Service SubiektBridge
  8. Health check
```

## Reguły bezpieczeństwa repo public

**Nie commituj** w to repo:
- konkretnych danych klienta (database name, server hostname, NIP-y, nazwiska)
- credentials (token, hasła SQL/operatora)
- email-i, adresów

Sanityzacja przed pierwszym pushem v0.1.0:
- `Database: ONEE` → `MAGAZYN`
- `Server: WIN-MSSQL\SQLEXPRESS` → `.\SQLEXPRESS`
- `Mock Allegro Sp. z o.o.` → `Mock Test Sp. z o.o.`
- adresy biura → `ul. Testowa 1, Warszawa`

Zasada na przyszłość: przed pushem grep:
```bash
grep -rEi "ONEE|onee.pl|WIN-MSSQL|onee-sync|test@allegro" .
```
Jeśli coś wraca - sanityzuj.

## Reguły deploy ASCII-only (PowerShell)

PowerShell 5.x na Windowsie klienta nie czyta UTF-8 bez BOM jako UTF-8 -
interpretuje jako Windows-1252 i polskie znaki łamią parser. Wszystkie skrypty
w `deploy/` muszą być **7-bit ASCII clean**:

```bash
grep -nP "[\x80-\xff]" deploy/*.ps1   # powinno być puste
```

PowerShell 5.x NIE obsługuje też:
- `?.` (null-conditional) - PS 7+ feature
- `??` (null coalescing) - PS 7+ feature

Używaj klasycznego `if-else`.

## TLS 1.2 dla GitHub w PS 5.x

Default PS 5.x to TLS 1.0/1.1 - GitHub wymaga 1.2+. Każdy skrypt zaczyna od:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
```

## Diagnostyka

Logi w `C:\SubiektBridge\logs\subiekt-bridge-YYYY-MM-DD.log` (Serilog rolling daily):
```powershell
Get-ChildItem C:\SubiektBridge\logs\ | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 60 }
```

Stan service:
```powershell
Get-Service SubiektBridge
sc.exe qc SubiektBridge
```

Health endpoint zwraca pełen status:
```json
{
  "status": "ok",
  "bridge_version": "0.7.29.0",
  "subiekt_version": "1.87 SP3 HF1",
  "sfera_session": "active",
  "last_invoice_at": "...",
  "queue_depth": 0,
  "last_error": null
}
```

## Klient Laravel-side

Reference implementation: https://github.com/robsonek/marketplace-manage (private)
- `app/Modules/Invoicing/Bridge/SubiektBridgeClient.php`
- `app/Modules/Invoicing/Services/{InvoiceIssuer,ReceiptIssuer,InvoiceCorrectionIssuer}.php`
- `app/Modules/Invoicing/Jobs/{IssueInvoiceJob,IssueCorrectionJob,IssueReceiptJob}.php`

Endpoint conventions:
- `GET` zwraca null gdy 404 (lookup nie istnieje)
- `POST` mutating wymaga Idempotency-Key
- 4xx = walidacja (NIE retry'ować)
- 5xx + 502/503 = Bridge/Subiekt offline (retry z backoff)
- 409 DUPLICATE_INVOICE = auto-recovery (UPDATE pending Invoice na issued)
