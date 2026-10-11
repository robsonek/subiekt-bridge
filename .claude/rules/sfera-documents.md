---
paths:
  - "src/SubiektBridge.Api/Sfera/**"
  - "src/SubiektBridge.Api/Controllers/{Invoices,Receipts,Transfers,Products,Contractors}Controller.cs"
  - "src/SubiektBridge.Api/Models/InvoiceModels.cs"
  - "tests/**"
---

# Pułapki Sfery — dokumenty, kontrahenci, płatności

## `FormaDokumentu = 1` (KSeF) dla FS firmowych — CELOWE, nie ruszać

`FormaDokumentuEnum`: 0 = faktura tradycyjna, **1 = faktura KSeF**, 2 = tryb awaryjny,
3 = offline24. Bridge ustawia `fs.FormaDokumentu = 1` dla kontrahenta-firmy **świadomie**:
w Polsce faktury B2B muszą iść do KSeF; Bridge oznacza formę; wysyłkę do KSeF robi operator w Subiekcie LUB klient przez
`POST /invoices/{id}/ksef` (od v0.16.0). NIE zmieniać na 0.

## Data sprzedaży FS = `DataZakonczeniaDostawy`, NIE `DataSprzedazy`

`SuDokument.DataSprzedazy` dotyczy TYLKO dokumentów ZW (Typ=14) i PA (Typ=21) — wg pomocy
Sfery. Dla FS datę sprzedaży ustawia się przez `DataZakonczeniaDostawy`. Daty Bridge ustawia
tylko gdy klient podał datę inną niż dzisiejsza (`SetDocumentDateIfBackdated`) — domyślny
przypadek zostawia nadanie daty Subiektowi. Dokumenty magazynowe (PZ/MM) wymagają pary
`DataMagazynowa` + `DataWystawienia`.

## `WartoscVat`, NIE `WartoscPodatku`

Atrybut `WartoscPodatku` nie istnieje w Sferze (0 trafień w całym CHM) — `TryReadDecimal`
połykał błąd i `totals.vat` było zawsze null (naprawione w audycie 2026-06-10,
`docs/AUDIT-2026-06-10.md`).

## Magazyn dokumentu = magazyn SESJI Sfery, NIE per pozycja (od v0.7.50)

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

## PZ liczy od cen NETTO (`LiczonyOdCenBrutto=true` rzuca `0x80004005`)

PZ trzyma `ob_CenaNetto` wpisane wprost, `ob_CenaBrutto` wyliczane z `VatProc`.
`AddLineToDocument(useNetPrice: true)` ustawia `CenaNettoPrzedRabatem`. Domyślnie most
przelicza `netto = unit_price_gross / (1 + vat/100)`. Od v0.7.49 `LineDto.UnitPriceNet`
(opcjonalne, `decimal?`) pozwala podać netto **wprost** - wtedy brak przeliczania i brak
groszowych rozjazdów (cenne dla cen zakupu). Pole znaczące tylko dla PZ; FS/KFS je ignorują.

## `dok_NumerPelny` to atrybut COM, NIE kolumna SQL

`SQL filter "dok_NumerPelny LIKE 'FS %'"` rzuca syntax error. To computed atrybut
Sfery (z `dok_TypNr + dok_Nr/dok_Rok`). Filtruj client-side po pobraniu kolekcji.

## `OtworzKolekcje` zwraca duchy

Rekordy z `dok__Dokument` po anulacji wciąż w wynikach `OtworzKolekcje(filtr)`.
Anti-duplicate check **musi weryfikować** że `WczytajDokument(id)` zwraca obiekt
przed traktowaniem jako duplikat. Kolekcję zwalniaj (`Marshal.ReleaseComObject`) w `finally`
po enumeracji — osobny RCW, bez tego powolny wyciek na STA. Kolekcje Sfery są **1-indeksowane**
(`Element(1..Liczba)`). **`OtworzKolekcje` zwraca TYLKO dokumenty z magazynu kontekstu sesji** (`Subiekt.MagazynId`;
CHM `SuDokumentyManager_OtworzKolekcje`) — skan po wszystkich magazynach wymaga SQL po `dok__Dokument`.
**Duch ≠ wyjątek:** „dokument nie istnieje” tylko gdy `dok_Status = 2` (`gtaSubiektDokumentStatusAnulowany`); semantykę
statusów czytaj z CHM `SubiektDokumentStatusEnum.htm`, nie ze skrótu `MS_Description` zrzutu — `0` to „wycofany SKUTEK
MAGAZYNOWY” (dokument istnieje), `3` odłożony skutek, `4` MM wywołany na źródłowym. Wyjątek `WczytajDokument` dla
dokumentu o innym statusie to awaria (sonda sesji → `SferaUnavailableException` / `DuplicateCheckUnavailableException`), nie duch.

## Anti-duplicate po `external_reference` — token i `dok_Typ` (od v0.18.0)

- `LIKE '%ref%'` to tylko pre-filtr **podciągu**: ref `order:12` pasował do `order:123` → 409 z CUDZYM
  `existing_subiekt_id` → Laravel przypinał zamówieniu obcą FS. Po pobraniu kolekcji most sprawdza
  `UwagiFields.ContainsReferenceToken(dok.Uwagi, ref)` — ref jako CAŁY token (znak przed/po nie jest znakiem
  identyfikatora), case-insensitive jak LIKE pod polskim collation.
- Typ dokumentu przez `dok_Typ` w filtrze (2=FS, 6=KFS, 9=MM, 10=PZ), NIE przez prefiks numeru — symbol
  numeracji bywa niestandardowy („FH”) i prefiks nigdy nie trafiał (warstwa 2 cicho martwa).
- `dok_Uwagi` = `TUwagi` = **varchar(500)**: `UwagiFields.Build` zawsze zachowuje ` | ref: X` (obcina notatki),
  kontrolery odrzucają za długie `notes`/`reason` → `422 NOTES_TOO_LONG`.

## Atrybuty, które NIE istnieją (TrySet/TryRead połykały — cicho złe dane)

- `Kontrahent.AdresEMail` → jest **`Email`** (`Kontrahent_Email.htm`). Do v0.17.2 e-mail nigdy nie był
  zapisywany ani czytany. `Kraj` → jest `Panstwo` (id ze `sl_Panstwo`; świadomie niemapowane, `country_code`
  zawsze „PL”).
- `Towar.VatStawka`/`JmZakupu`/`JmSprzedazy` → są **`SprzedazVatId`** (id/symbol z `sl_StawkaVAT`, procent w
  `vat_Stawka`), **`SprzedazJm`/`ZakupJm`**. Do v0.17.2 `/products` zwracał zawsze `vat_rate: 23`, `unit: "szt."`.
- Zanim użyjesz nowego atrybutu: `ls InsERT/pomoc/gta/htm | grep -i "^<Obiekt>_<Atrybut>"` — 0 trafień = nie istnieje.

## Korekta (KFS) — mapowanie linii na pozycje FS

Po `NaPodstawie()` KFS nie pozwala dodać pozycji; każda linia korekty musi trafić w istniejącą pozycję. Każda
linia zużywa **osobną** pozycję (`usedPositions` — FS z powtórzonym EAN ma dwie pozycje, dwie linie korekty
trafiały w pierwszą i druga nadpisywała pierwszą). Zwrot większy niż ilość na pozycji → `InvalidCorrectionException`
→ `422 INVALID_CORRECTION` (było ciche zerowanie = zaniżony KFS). Niezmapowana linia → to samo 422 (było 500).

## NIP w `adr__Ewid`, NIE w `kh__Kontrahent`

```sql
SELECT k.kh_Id
FROM kh__Kontrahent k
JOIN adr__Ewid a ON a.adr_IdObiektu = k.kh_Id AND a.adr_TypAdresu = 1
WHERE a.adr_NIP = @nip
```

- **Sonda sesji** (`IsSessionAlive`, od v0.18.0) = `SuDokumentyManager.OtworzKolekcje("dok_Id=-1")` + `Liczba`
  (runda do SQL; `Aplikacja.Wersja` jest in-proc i NIE wykrywa padu MSSQL). Po wyjątku `WczytajDokument`/`Istnieje`
  odczyt znaczy „nie istnieje” TYLKO gdy sonda żyje; martwa → reset + `SferaUnavailableException`
  (503 `SUBIEKT_UNAVAILABLE`; do v0.18.0 `SFERA_UNAVAILABLE`) / `KsefError.CommunicationError` (502) /
  `SettlementError.ScanFailed` (502). **Preflight mutacji** (od v0.19.0): `EnsureSessionForMutation` w
  `RunMutationOnStaAsync` (FS/KFS/PZ/MM/settlements) i w `/book` = sonda + jedna próba ponownego otwarcia PRZED
  pierwszym zapisem → 503 tylko, gdy nic nie zapisano; health też sonduje. Replay idempotencji przy padniętej sesji
  NIE kasuje klucza (kasacja + nowy request = duplikat).
- **KFS `contractor_subiekt_id`** (od v0.19.0) = `TryGetLong(kfs, "KontrahentId")` PO `NaPodstawie`, PRZED `Zapisz`
  (CHM: `KontrahentId` = płatnik = `dok_PlatnikId`, dziedziczony z FS); fallback po `Zapisz`, nieodczytane → `0` +
  warning. Nic po `Zapisz` nie może rzucić z powodu tego odczytu (zapisany KFS + 500 = ponowienie).
- **KAŻDE porównanie po NIP normalizuje obie strony** (`ContractorFields.NormalizeNip` = bez `-`/spacji,
  + `REPLACE(REPLACE(adr_NIP,'-',''),' ','')` w SQL) — dopasowanie przy FS/PZ i filtr `?nip=` tak samo.
  Starsze/ręczne kartoteki mają NIP z kreskami; dosłowne `=` dawało chybienie → duplikat po Symbolu.
  Prefiks `PL` NIE jest obcinany (nie wiadomo, czy Subiekt trzyma go w `adr_NIP` — brak osobnej kolumny).
- **Lookup po NIP przy FS/PZ (`FindContractorIdByNip`) jest FAIL-CLOSED** (od v0.17.1): błąd SQL →
  `ContractorLookupUnavailableException` → `503 CONTRACTOR_LOOKUP_UNAVAILABLE`, dokument nie powstaje.
  Wcześniej błąd był połykany → kontrahent zakładany po Symbolu → duplikat, gdy kartotekę założono
  ręcznie z innym symbolem. Osoby bez NIP-u nie pytają SQL.
- **Filtr `GET /invoices?nip=`**: NIP → wszystkie `kh_Id` (`=`, bez TOP, normalizacja `-`/spacji po obu
  stronach) → `dok_PlatnikId IN (...)` (`InvoiceQueryFields`). NIE `dok_OdbiorcaId` (na MM = id magazynu),
  NIE `dok_NrIdentNabywcy` (tylko fiskalizacja), **`dok_NabKodSlownik` NIE istnieje**. Zweryfikowane na prod 29.09.
- **`/contractors?nip=` to COM** (`Kontrahenci.Wczytaj` matchuje symbol lub NIP natywnie), nie SQL.

## Symbol kontrahenta - limit 20 znaków

`kh_Symbol` = typ `TSymbol` = **`varchar(20)`** (zrzut schematu, `Types/User-defined Data Types/dbo.TSymbol.sql`; wcześniej
notowane „16" było błędne). Email z `@` `+` lub UUID Allegro przekracza i MSSQL rzuca `0x80040E21`
(multi-step OLE DB). Na prod (29.09) są symbole 20-znakowe — limit potwierdzony; 17% symboli ma spacje
(także na końcu) i wielkie polskie litery, **`@`/`+` = 0 na 16 688** (czy Subiekt je odrzuca — niesprawdzone;
test: symbol `TEST@+1` w GUI Subiekta). Walidujemy tylko długość. Od v0.17.0 `ResolveOrCreateContractor` waliduje Symbol (`ContractorFields.ValidateSymbol`)
**dopiero po chybionym lookupie po NIP** (z NIP-em Symbol nie idzie do Subiekta) → `InvalidContractorSymbolException`
→ `422 INVALID_CONTRACTOR_SYMBOL` zamiast 500.

## Ilości `decimal` (od v0.17.0)

`LineDto.Quantity` / `CorrectionLineDto.QuantityChange` / `TransferLineDto.Quantity` są `decimal` (towary
na kg/m). Do COM idzie `ToComQuantity`: **całkowita jako `int`** (VT_I4 — bajt w bajt jak przed zmianą),
ułamkowa jako `double`. Odczyt `IloscJm` przy korektach przez `Convert.ToDecimal` (nie `ToInt32`).
**Ułamki na prawdziwym COM niezweryfikowane** (Subiekt może zaokrąglać wg precyzji jednostki towaru); na prod
(29.09) 0 z 61 062 pozycji `dok_Pozycja` ma ułamkową ilość (`ob_Ilosc` = `money`). Przed pierwszym towarem na
kg/m: test przez `POST /transfers` (MM, dokument wewnętrzny) z `quantity: 0.5` i MM zwrotne.

## `LiczonyOdCenBrutto + Rozliczony=true` konwertuje formę płatności

Z dokumentacji `SuDokument_PlatnoscPrzelewKwota.htm`:

> "jesli zostanie ustawione: `PlatnoscPrzelewKwota=0` i `Rozliczony=True`
> to przy zapisie dokumentu zostanie wykonane:
> `PlatnoscPrzelewKwota := PlatnoscKredytKwota` i `PlatnoscKredytKwota := 0`"

Sfera **automatycznie konwertuje** PlatnoscKredyt na PlatnoscPrzelew gdy oba
są spełnione. Dla form odroczonych (kredyt kupiecki, "Allegro Pay") **musi być
`Rozliczony=false`**.

## `PlatnoscPrzelewId` NIE ISTNIEJE

Tylko `PlatnoscPrzelewKwota` (Sfera traktuje przelew jako "zapłacono" bez ID
słownika). Inne formy mają Id:
- `PlatnoscKredytId` → `sl_FormaPlatnosci` (fp_Typ=0)
- `PlatnoscKartaId` → `sl_FormaPlatnosci` (Sfera używa tego samego słownika dla obu)
- `PlatnoscRatyId`
- `PlatnoscGotowka*` - tylko Kwota + Reszta, NIE Id

`PaymentDto.MethodSubiektId` jest **`int?`** (nullable) - dla form bez Id
(Gotowka, Przelew) Bridge ustawia tylko `*Kwota`.

## `Subiekt.Baza.PolaczenieAdoNet` - SqlConnection jako ComObject

Sfera dokumentacja mówi że `PolaczenieAdoNet` zwraca `System.Data.SqlClient.SqlConnection`,
ale w realu przychodzi jako `__ComObject` - dynamic binder NIE widzi metod
(`CreateCommand` rzuca `RuntimeBinderException`).

**Rozwiązanie:** Bridge robi własny `Microsoft.Data.SqlClient.SqlConnection` z
opcji `Subiekt:Server/Database/DbUser/DbPassword` (te same credentials co Sfera) — `SqlConnStr()`.

## Anti-duplicate w Subiekcie (warstwa 2 idempotencji)

Przed `DodajFS`/`DodajKFS`/`DodajPZ`/`DodajMM` most szuka dokumentu z `external_reference` w Uwagach. Match → 409
`DUPLICATE_INVOICE`/`DUPLICATE_RECEIPT`/`DUPLICATE_TRANSFER` z `existing_subiekt_id` w details. Bridge SAM dokleja
`| ref: <external_reference>` do Uwag dokumentu (`BuildUwagiWithReference`) — warstwa nie zależy od tego, czy klient
wkleił ref do notes.

**Od v0.20.0 skan jest FAIL-CLOSED i idzie przez SQL** (`FindExistingDocumentByReference` → rdzeń `DuplicateScan.Find`
na delegatach, testowalny bez COM; spec `docs/superpowers/specs/2026-10-10-booking-journal-first-dupscan-fail-closed-design.md`):
- prefiltr read-only SQL `SELECT dok_Id, dok_NrPelny, dok_Uwagi, dok_Status FROM dok__Dokument WHERE dok_Typ=@typ AND dok_Uwagi
  LIKE @pat ORDER BY dok_Id DESC` (własny `SqlConnection`, escapowanie nawiasowe jak `search`, bez `TOP` — ref `sys:order:1`
  jest podciągiem `sys:order:1xxx`), czytany strumieniowo; widzi WSZYSTKIE magazyny (COM `OtworzKolekcje` tylko magazyn sesji,
  a skan idzie PRZED `SetSessionWarehouse` — do v0.19.x dokument z innego magazynu nie był wykrywany);
- token-check `UwagiFields.ContainsReferenceToken` (LIKE to podciąg), duch WYŁĄCZNIE `dok_Status = 2`, potwierdzenie COM
  `WczytajDokument` (wyjątek: martwa sesja → `SferaUnavailableException` 503 `SUBIEKT_UNAVAILABLE`; żywa albo „nie wczytał” →
  `DuplicateCheckUnavailableException` 503 `DUPLICATE_CHECK_UNAVAILABLE`); stop na pierwszym potwierdzonym;
- budżet całego skanu `DuplicateScan.Deadline` (20 s, Stopwatch) sprawdzany PRZED każdym kandydatem i PO enumeracji przed
  `null` — przekroczenie = 503, nigdy częściowy „brak duplikatu”; `CommandTimeout` ogranicza tylko pojedynczy `Read`;
- żadnego `catch → null`; pusty ref nie dociera do skanu (`UwagiFields.ValidateExternalReference` w kontrolerach PRZED
  `ValidateNotes` → `422 INVALID_EXTERNAL_REFERENCE`, limit 495 = 500 − `"ref: "`; dłuższy ref `Build` obcinał i skan go nie znajdował).
Konsekwencja: każde wystawienie FS/KFS/PZ/MM zależy od SqlClienta (`/health` `sql_connection`). Przed wydaniem potwierdź
`dok_Status`/`dok_Uwagi` na prod read-only (`INFORMATION_SCHEMA.COLUMNS`).

## Edycja wczytanego dokumentu — PZ nagłówek (od v0.21.0, `PATCH /receipts/{id}`)

- **Lock Sfery otwiera się przy PIERWSZEJ zmianie atrybutu** (CHM `start_FAQ.htm` „Blokowanie obiektów”); kolizja z GUI =
  `0x80040F19 INS_E_CANNOTLOCKOBJECT` rzucany przy `SetComProperty`, PRZED `Zapisz` → nic nie utrwalono. **`TrySet` jest w edycji
  ZAKAZANY** (połknąłby odmowę). Sam odczyt (`WczytajDokument` + atrybuty) nie blokuje. Lock trzymany do `Zamknij` → `CloseVerified` w
  `finally` (`ReceiptCloseGuard`: raportuje wynik, `TryClose` go połyka). **Niepotwierdzone `Zamknij` po mutacji = natychmiastowy
  `ResetSessionOnSta`** (lock może wisieć w sesji; bieżące żądanie 502 przez `sessionReset`, kolejne otwiera sesję od nowa).
- **Kod po `Zapisz` wyłącznie z odczytu kontrolnego** (`ReceiptEditOutcome.Decide` R1–R8, komparator po `ob_Id` ze wszystkimi polami,
  równość dokładna po normalizacji 4 dp): stan docelowy → 200 (także gdy `Zapisz` rzucił); stan sprzed → 409/422/500 wg klasy wyjątku;
  rozjazd / brak odczytu / `Zamknij` niepotwierdzone / reset sesji → 502 `RECEIPT_UPDATE_UNVERIFIED`.
- **Lista błędów `start_Lista_bledow.htm` jest autorytatywna, strony per-metoda nie** (`SuDokument_Usun.htm` podaje `0x80040F5F` dla
  „dostawy zmodyfikowane”, lista: `0x80040F61`). Klasyfikacja = HRESULT ma pierwszeństwo nad frazami, także nad walidacją (`0x80040F8A`)
  i brakiem (`0x80040F62`); frazy całe z granicą słowa („niezaksięgowany” ≠ „zaksięgowany”).
- **404 tylko po pozytywnym `Dokumenty.Istnieje(id) == false`**; wyjątek `WczytajDokument` przy żywej sesji i `Istnieje == true` →
  502 `SUBIEKT_QUERY_FAILED` (nie zgadujemy ducha). Cały chroniony snapshot (typ, status, `NumerOryginalny`, `DoDokumentuId`, pola pozycji z `TowarId`/`UslugaJednorazowa`/`TowarRodzaj`) czyta
  `ReceiptSnapshotReader` — adapter na delegatach testowany bez COM: wyjątek albo `null` w kolumnie NOT NULL = `ReadFailed`, **nic nie jest
  podstawiane** (awaria odczytu ≠ „brak linku”/„usługa jednorazowa”/„ilość 0”); legalne nulle tylko `Uwagi`, `DoDokumentuId`, `TowarId`, `Jm`.
  Id z URL przechodzi przez `BridgeIds` (`1..int.MaxValue` — COM Long; `(int)4294967297 == 1`).
- Powiązanie PZ↔FZ/KFS = `dok_DoDokId` (link TYLKO po stronie PZ; prod: 12 561 → FZ, 1 320 → KFS) **i wspólne wiersze `dok_Pozycja`**
  (`ob_DokHanId` + `ob_DokMagId`); nagłówek `dok_NrPelnyOryg` operator edytuje w GUI także po linku. Most NIE odmawia z linku (polityka A) —
  raportuje `linked_documents`, Sfera orzeka. `DoDokumentuId` jest read-only (powiązania istniejącego FZ z PZ nie ma w Sferze).
- `SuPozycja.TowarId` tylko do odczytu (zmiana towaru = `Usun` + `Dodaj`, nowe `ob_Id`); `TowarRodzaj` (1 towar/2 usługa/4 opakowanie/8
  komplet/32 sugestia); rozchód z `dok_MagRuch` (`mr_PozId = ob_Id`, `mr_Ilosc`, `mr_Pozostalo`, j. magazynowe; `ReceiptStockFacts.FromRows`
  z inwariantami: niespójność = `null` + warning, nie liczba). `/sfera/raw` jest jednostrzałowy — do sond łańcucha Wczytaj→set→Zapisz
  służy `deploy/probe-pz-items.ps1` (32-bit PowerShell `-STA`, tylko dokument testowy).
