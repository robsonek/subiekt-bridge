---
paths:
  - "src/SubiektBridge.Api/Sfera/RealSferaSession.cs"
  - "src/SubiektBridge.Api/Sfera/BankBooking*.cs"
  - "src/SubiektBridge.Api/Sfera/BookFailureClassifier.cs"
  - "src/SubiektBridge.Api/Controllers/{BankTransactions,Invoices}Controller.cs"
  - "src/SubiektBridge.Api/Models/{BankTransaction,OpenReceivable}Models.cs"
  - "tests/SubiektBridge.Tests/{OpenReceivables,OpenPayables,BankReconciliation,BookFailureClassifier,ErrorCodeEffectContract}Tests.cs"
---

# Home banking i otwarte rozrachunki — most = GŁUPIE prymitywy, matching robi Laravel

Most to cienki adapter: surowe prymitywy nad Sferą, ZERO klasyfikacji/matchingu/tierów. „Który przelew do
której faktury" + decyzja auto/ręcznie → Laravel (jak dopasowanie `GET /invoices` do zamówień).

- **`GET /bank-transactions`** — czysty passthrough `hb_Transakcja` (read-only SQL, bo `hb_Transakcja` nie jest
  w Sferze): surowe pola (hb_id, data, kwota, direction, hb_Kontrahent, hb_RachKontrahent, hb_Tytul, hb_NrFaktury,
  booked, bank_operation_subiekt_id=hb_idOperacjiBankowej, rachunek_id/rachunek_numer=konto wyciągu przez
  `hb_NaglowekIStopka` LEFT JOIN po `hb_IdNaglowekTr`, **`hb_status`**). `unbooked_only` = `IS NULL AND hb_Status IN (0,4)`
  (linie POMINIĘTE przez operatora, status 3, nie są kandydatami — `/book` i tak odrzuca je 422). Most NIE
  rozpoznaje kontrahenta po rachunku, NIE matchuje.
- **`POST /bank-transactions/{hb_id}/book` — WARIANT B, AKTYWNY domyślnie** (`Bridge:EnableHbBooking=false` wyłącza → 501-stub).
  **Sfera NIE wystawia API księgowania home-bankingu** (probe prod + SQL Profiler + CHM + research; cała rodzina `hb_`
  poza biblioteką Sfery, 0/2946 stron CHM). „Zaksięguj" w GUI = 3 zapisy w JEDNEJ niejawnej transakcji: `INSERT nz__Finanse`
  + `INSERT nz_FinanseSplata` + `UPDATE hb_Transakcja SET hb_idOperacjiBankowej, hb_Status=1`. Wariant B: most robi operację
  przez Sferę (`DodajOperacjeBankowa`), a link domyka **raw `UPDATE hb_Transakcja`** (`LinkHbToOperation`, własny `SqlConnection`).
  - **Audyt schematu (InsERT GT 1.88) potwierdził, że raw UPDATE jest bezpieczny:** `hb_Transakcja` ma **0 triggerów**,
    **0 FK/CHECK** na `hb_idOperacjiBankowej`/`hb_Status`, żaden artefakt SQL nie zapisuje tych kolumn (logika linkowania
    = EXE Subiekta), saldo rachunku WYLICZANE (0 `UPDATE rb__RachBankowy`). UPDATE = bajt-w-bajt jak GUI, `hb_Status=1` LITERAL.
  - **Guard `AND hb_idOperacjiBankowej IS NULL` + `@@ROWCOUNT`** (atomowo blokuje podwójny link): ==1 sukces; ==0 = ktoś
    zaksięgował równolegle → cofnij swój BP, zwróć zwycięzcę (already_booked). `ins_blokada`=`sp_getapplock` — **świadomie
    NIE replikowane** (guard `IS NULL` wystarcza, replikacja groziłaby stale-lockiem blokującym operatora; szczegóły w planie §5).
  - **Guardy fail-fast przed utworzeniem BP:** kierunek C/D, `rb_IdWaluty='PLN'` (`UNSUPPORTED_FOREIGN_ACCOUNT` — bo trigger
    `tr_NzFinanse_OpBank` i tak rolluje walutę), `hb_Status IN (0,4)` (`UNSUPPORTED_HB_STATUS`). Orphan (rollback BP padł) →
    **500 `HB_BOOKING_ORPHAN`** (NIE 2xx — interwencja ręczna). Replay idempotency FAIL-CLOSED.
  - **R2 (odwracalność linku w GUI) i R3 (re-import wyciągu) nierozstrzygalne statycznie, NIEzweryfikowane empirycznie**
    (klient bez dostępu do serwera → test §7 niewykonany; świadome ryzyko właściciela 2026-06-14). Integralność zapisu
    chronią mechanizmy w kodzie (guard IS NULL + rollback/orphan→500 + guardy), niezależne od flagi. Wyłącznik: `=false`+restart.
  - **Journal write-ahead (od v0.18.0):** `IdempotencyStore.SavePendingBookingAsync(hb_id, nzf_id)` zaraz po
    `bp.Zapisz()`, usuwany po sukcesie/rollbacku. Na wejściu `/book`: pending + `FinManager.Istnieje(nzf_id)` →
    dokończenie LINKU zamiast drugiego BP (śmierć procesu między `Zapisz` a UPDATE — np. `Stop-Service` przy
    self-update — dawała cichy orphan + duplikat przy retry). Orphan zostawia wpis (retry może się samowyleczyć).
  - **Wyjątek przy odbiorze wyniku UPDATE ≠ UPDATE nie wykonany** (batch `UPDATE; SELECT @@ROWCOUNT` w
    autocommit): przed rollbackiem `ReadHbLink` — link = nasz nzf_id → sukces; null → rollback; cudzy → ścieżka
    wyścigu; odczyt padł → Orphan **bez** cofania BP (martwy `hb_idOperacjiBankowej` gorszy niż orphan).
  - `hb_Kwota` jest `money NULL` → listing daje 0, `/book` → `422 INVALID_HB_AMOUNT`; `hb_DataKsiegowania` NULL →
    fallback `hb_DataWaluty`. Komunikaty błędów z `cex.Message` (`GetExceptionForHR` dawał generyczne E_FAIL).
  - **Kod błędu = dowód o skutku (od v0.19.0; spec `docs/superpowers/specs/2026-09-29-kody-bledow-a-skutek-design.md`).**
    `HB_BOOKING_FAILED` (`BookError.Internal`; klient ponawia = NOWY BP, bo BP nie ma anti-duplicate po ref) wolno
    zwrócić WYŁĄCZNIE, gdy nic nie zapisano albo BP czysto cofnięto. `CreateBankOperationCore` idzie przez
    `BankOperationSave.Run(build, save, readId)` + `BookFailureClassifier.Classify(faza, wyjątek)`: **Build** → zawsze
    `Internal`; **Save** (`Zapisz`) → `Internal` tylko dla HRESULT-ów „odrzucone przez żywy proces”
    (`IsRejectedNothingSaved`: E_FAIL `0x80004005`, OLE DB `0x80040E00–EFF`, DISP_E_EXCEPTION, E_INVALIDARG,
    `0x80040F60/62`) **bez komunikatu transportowego** (`LooksLikeTransportFailure`: network/sieci, communication link,
    TCP Provider, DBNETLIB, forcibly closed, połączeni, timeout/limit czasu — dostawca SQL raportuje zerwanie W TRAKCIE
    polecenia jako E_FAIL z takim opisem, a `0x80040E31`/`0x80040E4E` = timeout/anulowanie → zawsze `Orphan`), reszta
    (RPC `0x8001xxxx`, RPC_S_* `0x800706xx`, E_UNEXPECTED, nieznane, wyjątki nie-COM) → `Orphan`
    BEZ id (Subiekt to osobny proces — utrata odpowiedzi po COMMIT jest realna); **ReadId** (Zapisz wróciło) → 2 próby
    odczytu `Identyfikator`, potem `Orphan` bez id. Sekcja po `nzfId` = `LinkCreatedBankOperationAsync` z catch-all:
    nieprzewidziany wyjątek → odczyt linku → link nasz = sukces / brak linku + rollback OK = `Internal` / inaczej
    `Orphan(nzfId)`. Każdy `Orphan` niesie `BankBookingException.BankOperationSubiektId` →
    `details.bank_operation_subiekt_id` (`null` = id nieznane) + `details.hb_id`. `rows > 1` (UPDATE ma WHERE po
    `hb_id`, więc link na nasz BP JEST ustawiony) → **NIE cofamy BP** (byłby martwy link) → `Orphan(nzfId)`, ręczna
    diagnoza schema drift. Sonda orphana po kwocie/dacie
    świadomie NIE wdrożona (główny scenariusz = martwa sesja, COM i tak nie odpowie).
  - **Preflight sesji (W3):** po guardach, przed journalem/BP, `EnsureSessionForMutation()` (sonda `IsSessionAlive` +
    jedna próba ponownego otwarcia) → `SferaUnavailableException` → `503 SUBIEKT_UNAVAILABLE` (nic nie zapisano,
    retry tym samym kluczem, brak wpisu w cache). Po preflight ŻADNEGO mapowania błędów RPC na 503.
  - Plan + pełny audyt: `docs/PLAN-home-banking-booking-variant-b.md`. NIE pisać raw SQL
    do `hb_Transakcja` poza `LinkHbToOperation`; NIGDY do `nz__Finanse`/`nz_FinanseSplata` (te tylko przez Sferę).
- Rozliczenie już jest (`POST /invoices/{id}/settlements`) — most nie decyduje co z czym, dostaje rozkaz
  „zaksięguj X" / „rozlicz Y z Z". Otwarte rozrachunki Laravel zna z własnego modelu + `GET /invoices/{id}/settlements`.

## `GET /invoices/open-receivables` i `/open-payables`

Strona NALEŻNOŚCI tej samej filozofii: most zwraca okno otwartych rozrachunków sprzedaży (kandydaci do
dopasowania z wpłatą), Laravel matchuje. `open-payables` = lustro dla zakupu (`nzf_Typ=40`, FZ, wypłaty
`direction=out`); wspólny rdzeń `QueryOpenSettlementsCore(nzfTyp, …)`, ten sam request/DTO/search/scan-cap.

- **Czysto COM, NIE raw SQL na `nz__Finanse`** (właściciel wprost; raw SQL na `nz__Finanse` kruche).
  Mapowania COM `FinDokument` (CHM, od GT 1.13): `DokumentZrodlowyId`=`nzf_IdDokumentAuto` (id dok. handlowego →
  `document_subiekt_id`); `WartoscBiezaca`=pozostało (RO, PLN); `NumerPelny` rozrachunku=numer dok. źródłowego
  (`doc_type`=prefiks; COM `Typ` ≠ `nzf_Typ` od GT 1.17); `ObiektPowiazanyId`+`Kontrahenci.Wczytaj(id).Nazwa`;
  `Waluta`=symbol sl_Waluta.
- **PERF (fix po prod v0.11.0: 35s / timeout >90s → <kilka s):** WSZYSTKIE predykaty
  (kwota/waluta/kontrahent/data) idą do **filtra `OtworzKolekcje`** (raw kolumny `nz__Finanse`, jak `/bank-operations`),
  więc Sfera filtruje po stronie BAZY i zwraca małą kolekcję — NIE enumerujemy ~30k rozrachunków typ-39 w pamięci
  (to był bug). Filtr: `nzf_Typ=39 AND nzf_Wartosc>0 AND nzf_IdWaluty='PLN'` [+ `nzf_Wartosc>=/<=`, `nzf_IdObiektu=`,
  `nzf_Data>=/<=`]; sort `nzf_Data DESC`; `limit` cap WYNIKU (najnowsze), nie skanu. Kolumny ze zrzutu schematu:
  **`nzf_Wartosc`=pozostało (PLN, = `WartoscBiezaca`; maleje z rozliczeniami, `<=nzf_WartoscPierwotna`)**,
  `nzf_WartoscPierwotna`=pierwotna, `nzf_NumerPelny`=pełny numer (REALNA kolumna, nie atrybut COM jak `dok_NumerPelny`!),
  `nzf_IdObiektu`=kontrahent, `nzf_IdWaluty`=waluta.
- **Tylko PLN** (controller odrzuca `currency≠PLN` → 422; `nzf_Wartosc` jest w PLN, wiersz walutowy byłby
  mislabel + nierozliczalny). `nzf_Typ=39` obejmuje też korekty (`doc_type` np. KFS) nierozliczalne przez
  `/settlements` — klient filtruje po `doc_type` (NIE w moście: prefiks bywa „FH", głupi most jak `/invoices`).
- **`search` (v0.14.0)** = fraza case-insensitive z **precedencją scope kontrahenta nad numerem**: SQL `LIKE`
  po `adr_Nazwa`/`adr_NIP` (`adr__Ewid`, TypAdresu=1) → `kh_Id` → zawężenie `OtworzKolekcje` przez
  `nzf_IdObiektu IN (...)`; gdy fraza NIE pasuje do żadnego kontrahenta (= numer FV) → pełny skan z tanim
  number-checkiem przed COM `ResolveContractor`. Eliminuje COM `Kontrahenci.Wczytaj` per wiersz.
  `OpenReceivableFields.{MatchesSearch,EscapeLikeWildcards}`. **NIE `kh_Nazwa`** — nazwa kontrahenta jest
  w `adr__Ewid.adr_Nazwa`, nie w `kh__Kontrahent`. Błąd SQL w `search` → pusty zbiór i skan po numerze (fail-open,
  listing to tylko podpowiedź dla operatora).
