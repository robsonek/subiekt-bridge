# SubiektBridge

Most HTTP→COM/Sfera dla Subiekta GT. Stoi na Windowsie obok Subiekta klienta,
udostępnia HTTPS REST API z którego korzysta Laravel-owy konsument.

```
Klient HTTP (Linux/Mac)        SubiektBridge (Windows Service)        Subiekt GT
  Laravel SubiektBridgeClient   ASP.NET Core 10 (win-x86)               InsERT.GT (32-bit COM)
         HTTPS + X-Bridge-Token  Controllers → ISferaSession   COM/STA   SuDokumentyMgr, Towary,
  ─────────────────────────────►   ↳ RealSferaSession (COM)  ────────►  Kontrahenci, FinManager
                                   ↳ FakeSferaSession (dev/testy)
                                 IdempotencyStore (SQLite)
                                 Microsoft.Data.SqlClient    ────────►  MSSQL (raw SQL read + hb_ link)
```

**Stack:** .NET 10, ASP.NET Core, **win-x86** (Subiekt GT to wyłącznie 32-bit), Windows Service przez
`Microsoft.Extensions.Hosting.WindowsServices` (zero NSSM). **Bezpieczeństwo:** HTTPS z auto-generowanym
self-signed certem (`data/cert.pfx`), statyczny `X-Bridge-Token`, opcjonalny IP whitelist w Windows Firewall.

## Szczegółowe pułapki: `.claude/rules/`

Pułapki z poszczególnych obszarów są w `.claude/rules/*.md`. Claude Code wczytuje je automatycznie przy pracy
z pasującymi plikami (`paths:` we frontmatterze). **Inne narzędzia/agenci: przeczytaj właściwy plik ręcznie,
zanim dotkniesz danego obszaru.**

| Plik | Obszar |
|---|---|
| `sfera-documents.md` | FS/KFS/PZ/MM, kontrahenci (NIP, Symbol), płatności, magazyn sesji, ilości, anti-duplicate |
| `settlements.md` | rozliczenia rozrachunków z operacjami bankowymi (`/settlements`, `/bank-operations`) |
| `ksef.md` | wysyłka e-Faktur (`EFakturyKSeFManager`, `OperacjaWTle`, maszyna stanów) |
| `home-banking.md` | `/bank-transactions`, `/book` (wariant B, raw UPDATE `hb_Transakcja`), open-receivables/payables |
| `deploy-windows.md` | skrypty PS (ASCII-only, PS 5.x, TLS 1.2), usługa, cert, logi, self-update, build paczek |

## Build / Test / Run

```bash
# Lokalnie (macOS/Linux z .NET 10 SDK)
dotnet build SubiektBridge.sln
dotnet test SubiektBridge.sln    # xUnit v3 na Microsoft Testing Platform (opt-in w global.json;
                                 # bez tego .NET 10 SDK odrzuca xunit.v3 4.x w trybie VSTest)

# Dev run — FakeSferaSession zamiast COM (appsettings.Development.json: Bridge:UseFakeSfera=true,
# token "dev-token", HTTP :8080 + HTTPS :988)
dotnet run --project src/SubiektBridge.Api

# Release: tag → GitHub Actions buduje 2 ZIP-y win-x86 (fxdep + self-contained)
git tag -a vX.Y.Z -m "..." && git push origin vX.Y.Z
```

`RealSferaSession` (COM) odpali się tylko na Windowsie obok Subiekta — na innym OS Program.cs rzuca
z podpowiedzią ustawienia `UseFakeSfera`. Testy idą przeciw `FakeSferaSession`; logikę testowalną
cross-platform wydzielaj z windows-only `RealSferaSession` do statycznych helperów (np. `InvoiceQueryFields`).

## Źródła prawdy o Sferze i bazie (zanim zgadniesz)

**CHM Sfery offline:** `InsERT/pomoc/gta/htm/` — ~2900 stron HTML oficjalnej dokumentacji (gitignored, licencja
nie pozwala na redystrybucję). **Zanim zgadniesz sygnaturę/zachowanie API Sfery — grepnij tam** (pliki są
w Windows-1250: `iconv -f WINDOWS-1250 -t UTF-8`):

```bash
ls InsERT/pomoc/gta/htm/ | grep -i "DodajFS\|SuDokument_Platnosc"
grep -rl "MagazynId" InsERT/pomoc/gta/htm/ | head
```

**Zrzut schematu SQL** (autorytatywne nazwy/typy kolumn): `InsERT/Skrypty_SQL_1_88_HF3(1)/Tables/dbo.*.sql`
— jeden plik per tabela, z `MS_Description`, CHECK/FK/indeksami (+ `Types/`, `Views/`, `Stored Procedures/`).
**Zanim napiszesz raw SQL lub filtr `OtworzKolekcje` na surowych kolumnach — sprawdź tam nazwę i semantykę:**

```bash
D="InsERT/Skrypty_SQL_1_88_HF3(1)/Tables"
grep -iE "nzf_(Wartosc|IdWaluty|IdObiektu)" "$D/dbo.nz__Finanse.sql"   # + MS_Description niżej w pliku
```

- ⚠️ **Zrzut jest z 1.88 HF3, produkcja to 1.89 HF2** (od 2026-08). Całego zrzutu z 1.89 nie porównywano.
  Nową kolumnę w raw SQL potwierdź na prod read-only: `POST /admin/query` z
  `SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '<tabela>'`.
- ⚠️ **Polskie collation bazy:** zakres `[A-Z]` w `LIKE` łapie też polskie litery. Klasy znaków licz
  z `COLLATE Latin1_General_BIN` (bez tego zapytanie na `kh_Symbol` zgubiło na prod 3 z 2 859 symboli).
- Rozstrzygnięte: `nzf_Wartosc`=pozostało (PLN), `nzf_WartoscPierwotna`=pierwotna, `nzf_NumerPelny` to REALNA
  kolumna (≠ `dok_NumerPelny`, computed COM — w `dok__Dokument` numer to `dok_NrPelny`); NIP w `adr__Ewid`,
  nie w `kh__Kontrahent`; kontrahent dokumentu sprzedaży = `dok_PlatnikId`.

## Kontrakt API dla klientów

`docs/INTEGRATION-CONTRACT.md` — kompletny przewodnik dla konsumenta API: formaty request/response, kody
błędów, namespacing `external_reference`. **Plik jest PUBLICZNY** (jedyny wyjątek z gitignorowanego `docs/`):
przykłady tylko z fikcyjnymi danymi (NIP `1111111111`, EAN-y `5901...`), ZERO danych klienta. Reszta `docs/`
(plany, specy, audyty) zostaje lokalna. **Zmieniasz endpointy / kody błędów → zaktualizuj kontrakt.**

## Endpointy

| Endpoint | Funkcja |
|---|---|
| `GET /api/v1/health` | Sesja Sfery + wersja Subiekta + `sql_connection` (patrz Diagnostyka) |
| `GET /api/v1/products?ean=` / `GET /api/v1/contractors?nip=` | Lookup towaru / kontrahenta (COM) |
| `GET /api/v1/invoices?from&to&type&notes_contains&nip&limit` | Listing FS/KFS; `nip` → `dok_PlatnikId` |
| `GET /api/v1/invoices/{id}` / `/pdf` | Metadane FV / retro PDF |
| `GET /api/v1/invoices/open-receivables` / `open-payables` | Otwarte rozrachunki sprzedaży (39) / zakupu (40) w oknie kwoty — kandydaci do matchingu |
| `POST /api/v1/invoices` | Wystaw FS |
| `POST /api/v1/invoices/{id}/corrections` | Wystaw KFS |
| `POST /api/v1/invoices/{id}/ksef` / `GET` | Wyślij e-Fakturę do KSeF (advance maszyny stanów) / odczyt stanu |
| `GET /api/v1/receipts?...` / `{id}` / `{id}/pdf` | Listing / metadane / PDF PZ |
| `POST /api/v1/receipts` | Wystaw PZ (dropshipping) |
| `POST /api/v1/transfers` | Wystaw MM (dokument wewnętrzny, NIE KSeF) |
| `GET /api/v1/bank-operations` | Operacje bankowe BP/BW z wyciągu |
| `GET /api/v1/bank-transactions` | Surowy passthrough `hb_Transakcja` (read-only SQL) |
| `POST /api/v1/bank-transactions/{hb_id}/book` | Księgowanie przelewu (wariant B; `Bridge:EnableHbBooking=false` → 501) |
| `POST` / `GET /api/v1/invoices/{id}/settlements` | Rozlicz FS/FZ z operacją bankową / stan rozliczenia |
| `DELETE /api/v1/invoices/{id}/settlements/{rozliczenie_id}` | Cofnij rozliczenie |
| `POST /api/v1/admin/query` | Read-only SQL (whitelist SELECT/WITH) |
| `GET /api/v1/admin/logs?tail=N&grep=substr` | Tail najnowszego pliku logu (domyślnie 100, max 5000 linii; `grep` = substring, case-insensitive) — zdalna weryfikacja bez RDP |
| `POST /api/v1/admin/update` | Self-update (detached PowerShell) |
| `POST /api/v1/sfera/raw` | Escape hatch (whitelist metod w configu) |

Wszystkie wymagają `X-Bridge-Token: <secret>`. Mutujące POST (`/invoices`, `/corrections`, `/receipts`,
`/transfers`, `/settlements`, `/book`) wymagają też `Idempotency-Key`. Bez klucza: `DELETE .../settlements/{id}`
(idempotentny z natury) i `POST .../ksef` (idempotencja przez `StatusKSeF` w Subiekcie).

## Krytyczne wzorce COM (każdy kosztował debug session)

- **STA:** `InsERT.GT` jest apartment-threaded, a pula wątków ASP.NET Core to MTA —
  `Activator.CreateInstance(InsERT.GT)` z MTA rzuca `0x8000FFFF E_UNEXPECTED`. Dedykowany wątek
  `SetApartmentState(STA)` + `BlockingCollection<Action>`; każda metoda `RealSferaSession` idzie przez
  `RunOnStaAsync(Func<T>)`. PowerShellowy `New-Object -ComObject "InsERT.GT"` **przejdzie** mimo błędu mostu (PS jest STA).
- **Bitowość:** Bridge x64 nie połączy się z Subiektem x86 (`0x8000FFFF Katastrofalny błąd`). Cała linia „GT”
  InsERT to 32-bit; Subiekt Nexo (x64) to inny produkt. CI matrix: **win-x86 only**.
- **RCW:** kolekcje z `OtworzKolekcje` i dokumenty zwalniaj (`Zamknij()` / `Marshal.ReleaseComObject`) w `finally`.
  Kwoty do COM jako `double` (nie `decimal` → VT_DECIMAL). Wywołań prywatnych metod z argumentem `dynamic`
  unikaj (runtime binder) — rzutuj na `object`.
- **Raw SQL:** własny `SqlConnection` (`SqlConnStr()`), zawsze parametryzowany; `PolaczenieAdoNet` Sfery nie działa
  (przychodzi jako `__ComObject`). Zapis raw SQL tylko w `LinkHbToOperation`; `nz__Finanse` wyłącznie przez Sferę.

## Idempotency (3 warstwy)

1. **`Idempotency-Key`** — SQLite cache (TTL 14 dni = okno automatycznych ponowień klienta; wpisy starsze kasuje `IdempotencyCleanupService` raz na dobę
   — retencja i `VACUUM`: `deploy-windows.md`). Replay zwraca zapisany response, ale najpierw weryfikuje,
   że cached `subiekt_id` wciąż istnieje (anulowana FV w Subiekcie → invalidate + nowy request). Do cache trafia
   TYLKO sukces — `503 SUBIEKT_UNAVAILABLE` z preflightu sesji (Subiekt offline przed pierwszym zapisem) nie jest
   zapisywane, retry tym samym kluczem wykonuje pełny flow.
2. **Anti-duplicate w Subiekcie** — po `external_reference` w `dok_Uwagi` → 409 `DUPLICATE_*` (szczegóły:
   `sfera-documents.md`; rozliczenia fail-closed po stanie: `settlements.md`).
3. **Klient (Laravel)** — `UNIQUE(order_id, type)` w DB + `ShouldBeUnique` na jobie.

## Deployment na Windowsie klienta

```powershell
# Pierwsza instalacja (jako Admin)
cd C:\SubiektBridge
.\install-windows.ps1 -LaravelHostIp 1.2.3.4

# Kolejne aktualizacje: self-update endpoint albo .\update-bridge.ps1 (sam pobiera ZIP)
Invoke-RestMethod -Uri "https://localhost:988/api/v1/admin/update" -Method POST `
  -Headers @{'X-Bridge-Token'='<TOKEN>';'Content-Type'='application/json'} -Body '{}'
```

## Reguły bezpieczeństwa — repo PUBLICZNE

**Nie commituj:** danych klienta (nazwa bazy, hostname serwera, NIP-y, nazwiska), credentials (token, hasła
SQL/operatora), e-maili, adresów, nazwy prywatnego repo klienta Laravel. Sanityzacja z v0.1.0:
`Database: ONEE` → `MAGAZYN`, `Server: WIN-MSSQL\SQLEXPRESS` → `.\SQLEXPRESS`,
`Mock Allegro Sp. z o.o.` → `Mock Test Sp. z o.o.`, adresy → `ul. Testowa 1, Warszawa`. Przed pushem:

```bash
grep -rEi "ONEE|onee.pl|WIN-MSSQL|onee-sync|test@allegro" .   # jeśli coś wraca — sanityzuj
```

## Diagnostyka

Logi: `C:\SubiektBridge\logs\subiekt-bridge-yyyyMMdd.log` (Serilog rolling daily, data BEZ kresek, np.
`subiekt-bridge-20260929.log`); zdalnie: `GET /api/v1/admin/logs?tail=200&grep=KFS`. Usługa:
`Get-Service SubiektBridge`, `sc.exe qc SubiektBridge`.

```json
{ "status": "ok", "bridge_version": "0.17.0.0", "subiekt_version": "1.89 HF2", "sfera_session": "active",
  "last_invoice_at": null, "queue_depth": 0, "last_error": null, "sql_connection": "ok", "sql_error": null }
```

`sql_connection` = `SELECT 1` przez własny `SqlConnection` (poza STA). **Sesja Sfery (COM) NIE używa SqlClienta** —
zepsuty SqlClient daje `200` + `status: "degraded"` + `sql_connection: "down"`; `503` tylko przy padniętej sesji
Sfery. `sfera_session` jest od v0.19.0 sprawdzane sondą (`IsSessionAlive`: `OtworzKolekcje("dok_Id=-1")`, runda do
SQL), nie samą obecnością obiektu sesji — zamknięty Subiekt daje `503` od razu, martwa sesja jest resetowana. SqlClient używają: `/bank-transactions`, `/book`, `admin/query`, filtr `nip`, `search` w open-receivables,
MM (magazyn dokumentu) i lookup NIP przy FS/PZ (fail-closed → `503 CONTRACTOR_LOOKUP_UNAVAILABLE`).
**`/contractors?nip=` to COM, nie SQL.** `last_invoice_at` trzymane w pamięci — po restarcie `null`.

## Klient Laravel-side

Reference implementation (prywatny klient Laravel): `app/Modules/Invoicing/Bridge/SubiektBridgeClient.php`,
`Services/{InvoiceIssuer,ReceiptIssuer,InvoiceCorrectionIssuer}.php`, `Jobs/{IssueInvoiceJob,IssueCorrectionJob,IssueReceiptJob}.php`.
Konwencje: `GET` → null przy 404; mutujący `POST` wymaga Idempotency-Key; 4xx = walidacja (NIE retry);
5xx: klient ponawia (tym samym kluczem) **wyłącznie kody z listy retry** w `INTEGRATION-CONTRACT.md` §4
(`SUBIEKT_UNAVAILABLE`, `SUBIEKT_QUERY_FAILED`, `HB_BOOKING_FAILED`, …) — każdy z nich gwarantuje „nic nie
zapisano w tym żądaniu” (wyjątek: kody KSeF są retry-safe przez idempotentną maszynę stanów, nie przez „nic nie
zapisano”); `HB_BOOKING_ORPHAN` / `INTERNAL_ERROR` = ręcznie; 409 `DUPLICATE_*` = auto-recovery.
**Nowy kod 5xx bez wpisu na liście retry klient traktuje jako twardy błąd** (lekcja `SFERA_UNAVAILABLE` z v0.18.0).
