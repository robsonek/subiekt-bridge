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
(`Element(1..Liczba)`).

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

Przed `DodajFS`/`DodajKFS`/`DodajPZ`/`DodajMM` most szuka `dok_Uwagi LIKE '%external_reference%'`
+ verify przez `WczytajDokument`. Match → 409 `DUPLICATE_INVOICE`/`DUPLICATE_RECEIPT`/`DUPLICATE_TRANSFER`
z `existing_subiekt_id` w details. Bridge SAM dokleja `| ref: <external_reference>` do Uwag dokumentu
(`BuildUwagiWithReference`) — warstwa nie zależy od tego, czy klient wkleił ref do notes. Skan jest
fail-open (błąd skanu = brak duplikatu — lepszy ewentualny duplikat niż zablokowane wystawianie FS).
