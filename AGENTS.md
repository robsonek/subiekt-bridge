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

> **AGENTS.md jest kopią tego pliku.** Po każdej edycji CLAUDE.md zsynchronizuj:
> `cp CLAUDE.md AGENTS.md`.

## Build / Test / Run

```bash
# Lokalnie (macOS/Linux z .NET 10 SDK)
dotnet build SubiektBridge.sln
dotnet test SubiektBridge.sln    # xUnit (na razie placeholder, realne testy: faza 2.7+)

# Dev run na macOS/Linux — FakeSferaSession zamiast COM
# (appsettings.Development.json: Bridge:UseFakeSfera=true, token "dev-token",
#  HTTP :8080 + HTTPS :988)
dotnet run --project src/SubiektBridge.Api

# Self-contained (~46 MB, runtime wbudowany)
./scripts/publish-win.sh

# Release przez tag (GitHub Actions auto-builduje 2 ZIP-y)
git tag -a vX.Y.Z -m "..."
git push origin vX.Y.Z
```

`RealSferaSession` (COM) odpali się tylko na Windowsie obok Subiekta — na innym OS
Program.cs rzuca z podpowiedzią ustawienia `UseFakeSfera`.

## Dokumentacja Sfery offline

`InsERT/pomoc/gta/htm/` zawiera ~2900 rozpakowanych stron HTML oficjalnej dokumentacji
Sfery (CHM InsERTu; gitignored — licencja nie pozwala na redystrybucję, ale lokalnie jest).
**Zanim zgadniesz sygnaturę/zachowanie API Sfery — grepnij tam**, np.:

```bash
ls InsERT/pomoc/gta/htm/ | grep -i "DodajFS\|SuDokument_Platnosc"
grep -rl "MagazynId" InsERT/pomoc/gta/htm/ | head
```

## Kontrakt API dla klientów

`docs/INTEGRATION-CONTRACT.md` — kompletny przewodnik dla konsumenta API (nowego systemu
sprzedażowego): formaty request/response, obsługa błędów, namespacing `external_reference`.
Publiczne `{id}` w ścieżkach to **bridge id** w formacie `sub_<SUBIEKT_ID>` (np. `sub_142877`).
Zmieniasz endpointy → zaktualizuj kontrakt.

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
| `POST /api/v1/transfers` | Wystaw MM — przesunięcie międzymagazynowe (DodajMM, dokument wewnętrzny, NIE KSeF) |
| `GET /api/v1/bank-operations?from&to&direction&unsettled_only&limit` | Listing operacji bankowych BP/BW z wyciągu (źródło `bank_operation_subiekt_id`) |
| `GET /api/v1/bank-transactions?direction&unbooked_only&from&to&limit` | Surowy passthrough `hb_Transakcja` (read-only) — pula „do zaksięgowania" |
| `POST /api/v1/bank-transactions/{hb_id}/book` | Zaksięguj przelew na operację BP/BW (Idempotency-Key); zwraca `bank_operation_subiekt_id` + `linked` (Branch A/B) |
| `POST /api/v1/invoices/{id}/settlements` | Rozlicz rozrachunek FS/FZ z operacją bankową (Idempotency-Key required) — korekty nieobsługiwane |
| `GET /api/v1/invoices/{id}/settlements` | Stan rozliczenia dokumentu (pozostało + lista rozliczeń) |
| `DELETE /api/v1/invoices/{id}/settlements/{rozliczenie_id}` | Cofnij rozliczenie (FinRozliczenie.Usun, rozkojarza) |
| `POST /api/v1/admin/query` | Read-only SQL (whitelist SELECT/WITH) |
| `POST /api/v1/admin/update` | Self-update Bridge'a (detached PowerShell) |
| `POST /api/v1/sfera/raw` | Escape hatch (whitelist metod w configu) |

Wszystkie wymagają nagłówka `X-Bridge-Token: <secret>`. Operacje mutujące
(POST `/invoices`, `/corrections`, `/receipts`, `/transfers`, `/invoices/{id}/settlements`)
wymagają też `Idempotency-Key`. `DELETE .../settlements/{id}` jest idempotentny z natury
(powtórny → `404 SETTLEMENT_NOT_FOUND`), bez `Idempotency-Key`.

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

### `FormaDokumentu = 1` (KSeF) dla FS firmowych — CELOWE, nie ruszać

`FormaDokumentuEnum`: 0 = faktura tradycyjna, **1 = faktura KSeF**, 2 = tryb awaryjny,
3 = offline24. Bridge ustawia `fs.FormaDokumentu = 1` dla kontrahenta-firmy **świadomie**:
w Polsce faktury B2B muszą iść do KSeF; Bridge tylko oznacza formę, samą wysyłkę do KSeF
robi operator w Subiekcie po sprawdzeniu poprawności faktur. NIE zmieniać na 0.

### Data sprzedaży FS = `DataZakonczeniaDostawy`, NIE `DataSprzedazy`

`SuDokument.DataSprzedazy` dotyczy TYLKO dokumentów ZW (Typ=14) i PA (Typ=21) — wg pomocy
Sfery. Dla FS datę sprzedaży ustawia się przez `DataZakonczeniaDostawy`. Daty Bridge ustawia
tylko gdy klient podał datę inną niż dzisiejsza (`SetDocumentDateIfBackdated`) — domyślny
przypadek zostawia nadanie daty Subiektowi. Dokumenty magazynowe (PZ/MM) wymagają pary
`DataMagazynowa` + `DataWystawienia`.

### `WartoscVat`, NIE `WartoscPodatku`

Atrybut `WartoscPodatku` nie istnieje w Sferze (0 trafień w całym CHM) — `TryReadDecimal`
połykał błąd i `totals.vat` było zawsze null (naprawione w audycie 2026-06-10,
`docs/AUDIT-2026-06-10.md` — tam też lista otwartych findingów).

### Magazyn dokumentu = magazyn SESJI Sfery, NIE per pozycja (od v0.7.50)

**Magazyn dokumentu (`dok_MagId`) bierze się z magazynu roboczego SESJI Sfery
(`Subiekt.MagazynId`), NIE z `SuPozycja.MagazynId` per pozycja.** Ustawienie per-pozycja
NIE zmienia `dok_MagId` na jednomagazynowej sesji — sprawdzone empirycznie 2026-06-06
(prod Subiekt 1.88 HF4): FS/PZ z `warehouse_subiekt_id=4` lądowały na magazynie 1 (Główny)
mimo `SuPozycja.MagazynId=4`.

- **Fix (v0.7.50):** `SetSessionWarehouse(int?)` ustawia `Subiekt.MagazynId` per request
  przed `DodajFS`/`DodajPZ` (TWARDY set — rzuca czytelny błąd przy braku dostępu operatora
  do magazynu, NIE połyka jak `TrySet`); `RestoreSessionWarehouse` przywraca w `finally`;
  tworzenie dokumentu w `try` (gwarancja restore). KFS dziedziczy magazyn z FS przez
  `NaPodstawie` (bez zmian — `warehouse_subiekt_id` w korekcie nie ma).
- **Wymóg:** operator, jako który Bridge loguje się do Sfery, musi mieć dostęp do
  docelowych magazynów w Subiekcie (`pd_UzytkMagazyn`), inaczej hard-set rzuca.
- **Per-pozycja `SuPozycja.MagazynId`** wciąż jest ustawiane (przez połykający `TrySet`) —
  nieszkodliwe i redundantne (pozycje i tak dziedziczą `dok_MagId` z sesji).
- **Historycznie (do v0.7.49, NIEAKTUALNE):** `SuDokument.MagazynNadawczyId` (FS) /
  `MagazynOdbiorczyId` (PZ) rzucają `NotImplementedException`/`0x80004005`, więc próbowano
  routingu per-pozycja — ale to NIE wpływa na `dok_MagId` (patrz wyżej).

### PZ liczy od cen NETTO (`LiczonyOdCenBrutto=true` rzuca `0x80004005`)

PZ trzyma `ob_CenaNetto` wpisane wprost, `ob_CenaBrutto` wyliczane z `VatProc`.
`AddLineToDocument(useNetPrice: true)` ustawia `CenaNettoPrzedRabatem`. Domyślnie most
przelicza `netto = unit_price_gross / (1 + vat/100)`. Od v0.7.49 `LineDto.UnitPriceNet`
(opcjonalne, `decimal?`) pozwala podać netto **wprost** - wtedy brak przeliczania i brak
groszowych rozjazdów (cenne dla cen zakupu). Pole znaczące tylko dla PZ; FS/KFS je ignorują.

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

### Rozliczenia rozrachunków (settlements) — pułapki

Spinanie zaimportowanych z wyciągu operacji bankowych z fakturami (`/invoices/{id}/settlements`).
- **`Rozlicz` to metoda KOLEKCJI `FinDokument.Rozliczenia`**, wołana OD STRONY ROZRACHUNKU:
  `rozrachunek.Rozliczenia.Rozlicz(operacjaBankowa, kwota)`. Tylko ten kierunek jest poprawny dla
  metody kasowej VAT (obiekt rozliczenia sprzedaży/zakupu powstaje na dokumencie, na którym wołano Rozlicz).
- **Kwotę przekazuj jako `(double)`**, NIE `decimal` — `decimal` binduje się do VT_DECIMAL; most wszędzie
  marshaluje kwoty pieniężne jako `double` (jak `ApplyPayment`).
- **„Pozostało do zapłaty" rozrachunku = `FinDokument.WartoscBiezaca`** (RO), pierwotna = `WartoscPoczatkowa`.
  `FinDokument` NIE ma `Rozliczony`/`WartoscRozliczona` → „rozliczony" = `WartoscBiezaca ≈ 0` (tolerancja 0.005).
- **`RozliczenieId = -1` przed `Zapisz`**; atrybuty/`SplataId`/`RozliczenieId` odświeżają się dopiero po
  przeładowaniu (`FinManager.Wczytaj(rozrachunekId)`). Reload owinięty retry (transient COM).
- **Istniejący przelew ładujemy po id**: `FinManager.WczytajDokument(nzf_Id)` / `Istnieje(id)`. BP/BW jest
  wprost spłatą w `Rozlicz` (bez `DodajSplate`).
- **Wybór rozrachunku (NIE `PodajRozrachunek`, NIE `.Element(1)`)**: FS marketplace ma zwykle DWA rozrachunki
  typ=39 — wyzerowany na kupującym (Podtyp=1) + OTWARTY na płatniku (Podtyp=4, Allegro Pay). Wybieramy przez
  `FinManager.OtworzKolekcje("nzf_IdDokumentAuto=<docId> AND nzf_Typ IN (39,40)")`, biorąc wiersz z **otwartą
  kwotą** (`WartoscBiezaca>0`) i **kontrahentem == kontrahent operacji bankowej** (`ObiektPowiazanyId`). Brak
  otwartego → `ALREADY_SETTLED`; brak dopasowania kontrahenta → `BANK_OPERATION_CONTRACTOR_MISMATCH` (łapie też
  kartę/raty: rozrachunek na centrum autoryzacji, przelew z innego kontrahenta). Zweryfikowane na prod 2026-06
  (8816 FZ = zawsze 1 wiersz; 13763 FS = 2 wiersze {wyzerowany + otwarty}).
- **Typ dokumentu przez `SuDokument.Typ`** (= `dok_Typ`), NIE prefiks numeru (symbol bywa „FH"): **1=FZ, 2=FS**
  obsługiwane; korekty (5=KFZ, 6=KFS) i inne → `UNSUPPORTED_DOCUMENT_TYPE` (korekty mają 2 wiersze z RÓŻNYMI
  kontrahentami → niejednoznaczne, świadomie poza zakresem).
- **`FinRozliczenie.PodajDokument`** (NIE `PodajFinDokument`). `Usun` rozkojarza rozrachunek/spłatę,
  NIE kasuje dokumentów — potem `Zapisz` na rozrachunku.
- **Bank-operations: filtruj po kolumnie DB `nzf_Typ`** (19=BP/20=BW) w stringu `OtworzKolekcje`,
  NIE po `FinDokument.Typ` (atrybut COM ≠ DB od v1.17).

### Home banking — most = GŁUPIE prymitywy, matching robi Laravel

Most to cienki adapter: surowe prymitywy nad Sferą, ZERO klasyfikacji/matchingu/tierów. „Który przelew do
której faktury" + decyzja auto/ręcznie → Laravel (jak dopasowanie `GET /invoices` do zamówień).
- **`GET /bank-transactions`** — czysty passthrough `hb_Transakcja` (read-only SQL, bo `hb_Transakcja` nie jest
  w Sferze): surowe pola (hb_id, data, kwota, direction, hb_Kontrahent, hb_RachKontrahent, hb_Tytul, hb_NrFaktury,
  booked, bank_operation_subiekt_id=hb_idOperacjiBankowej, rachunek_id/rachunek_numer=konto wyciągu przez
  `hb_NaglowekIStopka` LEFT JOIN po `hb_IdNaglowekTr`). Most NIE rozpoznaje kontrahenta po rachunku, NIE matchuje.
- **`POST /bank-transactions/{hb_id}/book`** — `FinManager.DodajOperacjeBankowa(19|20, rb_Id)` (na STA): kontrahent
  (`ObiektPowiazanyWstaw(1,kh)`)/`OperacjaBezDanychKh` PRZED kwotą, kwota przez **`WartoscPoczatkowaWaluta`**
  (NIE `WartoscPoczatkowa` — to **read-only**, set rzuca; CHM: „aby ustawić wartość początkową użyj WartoscPoczatkowaWaluta"),
  Data, Zapisz, potem SQL re-check
  `hb_idOperacjiBankowej`. Sfera NIE ma metody bookingu HB (brak HB-managera w CHM; `Importer`=EPP), więc czy BP
  zostanie powiązany rozstrzyga się **empirycznie** — pole `linked` w odpowiedzi:
  - `linked=true` (Branch A) → 201, gotowe do `/settlements`.
  - `linked=false` (Branch B) → most **COFA BP** (`DeleteBankOperationCore`/`Usun`, zero orphanów), 200 + `message`;
    flaga `keep_unlinked=true` zostawia BP do inspekcji (tylko probe). NIE pisać raw SQL do `hb_Transakcja`/`nz__Finanse`.
  - **Lock per `hb_id`** (`SemaphoreSlim`, proces jednoinstancyjny) + guard `ExistingOpId` → brak podwójnego BP.
    Status: 201 tylko czysty nowy+linked; 200 dla already_booked i linked=false — klient MUSI sprawdzać `linked`, nie status.
  - **Sekcja krytyczna NIEANULOWALNA:** od `DodajOperacjeBankowa`/`Zapisz` aż po verify+rollback używamy
    `CancellationToken.None` (timeout/cancel klienta po Zapisz porzuciłby `nzfId` → orphan BP). Verify (`ReadHbLink`)
    w `try/catch` — gdy padnie po utworzeniu BP, i tak rollback (`TryRollbackBp`). Guard `hb_Oznaczenie ∈ {C,D}` PRZED
    utworzeniem (puste → nie twórz po cichu BW). Replay weryfikuje `GetBookedOperationIdAsync` (cache nie maskuje usuniętego BP).
- Rozliczenie już jest (`POST /invoices/{id}/settlements`) — most nie decyduje co z czym, dostaje rozkaz
  „zaksięguj X" / „rozlicz Y z Z". Otwarte rozrachunki Laravel zna z własnego modelu + `GET /invoices/{id}/settlements`.

## Idempotency (3 warstwy)

1. **`Idempotency-Key` header** - SQLite cache (TTL 30 dni). Replay zwraca
   cached response, ALE weryfikujemy że cached `subiekt_id` wciąż istnieje
   (gdy user anulował FV w Subiekcie - cache invalidate + new request).
2. **Anti-duplicate w Subiekcie** - przed `DodajFS`/`DodajKFS`/`DodajPZ`/`DodajMM`
   szuka `dok_Uwagi LIKE '%external_reference%'` + verify przez `WczytajDokument`.
   Match → 409 `DUPLICATE_INVOICE`/`DUPLICATE_RECEIPT`/`DUPLICATE_TRANSFER` z
   `existing_subiekt_id` w details. Bridge SAM dokleja `| ref: <external_reference>`
   do Uwag dokumentu (`BuildUwagiWithReference`) — warstwa nie zależy od tego,
   czy klient wkleił ref do notes.
   - **Settlements**: rozliczenie NIE ma pola Uwagi → anti-duplicate czyta STAN
     (`FinDokument.Rozliczenia` po `SplataId == bank_operation_subiekt_id`), **FAIL-CLOSED**
     (każdy wyjątek skanu przerywa flow — podwójne rozliczenie tej samej kwoty to błąd księgowy,
     inaczej niż fail-open dla FS). Match → 409 `DUPLICATE_SETTLEMENT` z `existing_rozliczenie_id`.
     Replay-with-verify weryfikuje po `RozliczenieId` (nie po istnieniu dokumentu — ten zawsze istnieje).
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
