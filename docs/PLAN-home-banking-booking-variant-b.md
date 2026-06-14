# Plan pracy: księgowanie przelewu HB (wariant B — Sfera + raw UPDATE)

> Status: **ZAIMPLEMENTOWANE za flagą** (branch `feat/hb-booking-variant-b`, 46/46 testów, po adwersaryjnym review).
> `POST /bank-transactions/{hb_id}/book`: `EnableHbBooking=false` (default) → `501 HB_BOOKING_NOT_SUPPORTED`;
> `=true` → realny flow wariantu B. **Flagę włączyć DOPIERO po zielonym teście odwracalnym na prodzie (§7) —
> R2/R3 nierozstrzygalne statycznie.** Kontekst pełny: `~/.claude/.../memory/home-banking-booking.md`.

## 1. Cel

Pełna automatyzacja księgowania przelewu z wyciągu home-bankingu przez most: z surowej linii
`hb_Transakcja` powstaje operacja bankowa BP/BW **powiązana** (`hb_idOperacjiBankowej`), gotowa do
rozliczenia z fakturą. Dziś robi to ręcznie operator w module Bankowość Subiekta.

## 2. Dlaczego raw UPDATE (tło decyzji)

Ustalone w sesji 2026-06-14 (probe prod + SQL Profiler + CHM + adwersaryjny research + internet):
- **Sfera NIE wystawia API księgowania home-bankingu.** Cała rodzina tabel `hb_` jest poza biblioteką
  obiektową Sfery (0/2946 stron CHM; Subiekt i Rewizor identycznie). `FinManager.DodajOperacjeBankowa`
  tworzy operację **samodzielną** — nie ustawia `hb_idOperacjiBankowej`.
- Write-flow „Zaksięguj" z SQL Profilera = **3 zapisy danych**:
  1. `INSERT nz__Finanse` (operacja BP) — most umie przez Sferę (`DodajOperacjeBankowa`)
  2. `INSERT nz_FinanseSplata` (rozliczenie) — most umie przez Sferę (`Rozlicz` / `/settlements`)
  3. `UPDATE hb_Transakcja SET hb_idOperacjiBankowej=@nzf, hb_Status=1 WHERE hb_IdNaglowekTr=@nag AND hb_IdTransakcji=@hb`
     — **jedyna luka**; Sfera tego nie robi → raw SQL.
- Klient **nie używa wyciągów bankowych** (`wb_`), księguje operacje pojedynczo, **1 płatność = 1 faktura**.
  Dlatego „operacja luźna" (bez `nzf_NumerWyciagu`, którą tworzy `DodajOperacjeBankowa`) **mu nie przeszkadza**
  (problem „nie można zatwierdzić wyciągu z operacjami nienależącymi do wyciągu" go nie dotyczy).
- **To świadome odstępstwo od zasady mostu „nigdy raw SQL do wnętrzności Subiekta poza Sferą".** Zaakceptowane
  dla tego konkretnego, wąskiego UPDATE (2 kolumny, dokładnie znany z profilera).

## 2a. Audyt statyczny (schemat DB + profiler, 2026-06-14) — POTWIERDZONY

Pełny audyt adwersaryjny (17 agentów: rekonstrukcja profilera + dump InsERT GT 1.88 HF3 — 962 tabele,
606 procedur, 372 widoki) odpowiedział na dwa pytania właściciela:

**(1) Czy raw UPDATE wywołuje procedury/triggery? — NIE, definitywnie.**
- `hb_Transakcja` **nie ma żadnego triggera** (~127 `CREATE TRIGGER` w dumpie, **żaden** na tej tabeli).
- `hb_idOperacjiBankowej` i `hb_Status` są **bez FK i bez CHECK** (jedyny CHECK to `hb_Zrodlo IN (1,2)`;
  jedyny FK to `hb_IdNaglowekTr→hb_NaglowekIStopka`, którego UPDATE nie rusza).
- **Żaden** artefakt SQL nie *zapisuje* tych kolumn — cała logika linkowania/rozkojarzania żyje w aplikacji
  Subiekta (EXE). Jedyny czytelnik w bazie: `spHB_CzyMoznaUsunacOperacjeBankowa` (czysty SELECT).
- GUI robi link **dosłownie tym samym gołym `sp_executesql` UPDATE** (profiler L5514) — bez procedury wokół.

**(2) Czy coś się zepsuje? — Na poziomie integralności DB: NIE.** Saldo rachunku jest **wyliczane, nie
przechowywane** (0 `UPDATE rb__RachBankowy` w trace), żaden constraint nie pęka, stan po naszym UPDATE jest
**bajt-w-bajt identyczny** ze stanem po natywnym „Zaksięguj" w GUI.

**ALE — kluczowa delta:** GUI robi operację + spłatę + flagi + link + blokady **w JEDNEJ niejawnej transakcji**
(`set implicit_transactions on … IF @@TRANCOUNT>0 COMMIT TRAN`). Wariant B rozbija to na 3 nieatomowe kroki.
Stąd ryzyka R1/R2/R3 (patrz §8) — z których **R2 (odwracalność w GUI) i R3 (re-import wyciągu) są
NIEROZSTRZYGALNE statycznie** (logika importu/odlinkowania jest w EXE, nie w bazie) → twarda bramka = test §7.

## 3. Dokładny UPDATE (z profilera, parametryzowany + guard z audytu)

```sql
UPDATE hb_Transakcja
SET hb_idOperacjiBankowej = @nzfId,   -- nzf_Id operacji utworzonej przez DodajOperacjeBankowa
    hb_Status = 1                     -- LITERAL "zaksięgowana" (z profilera; NIE 2/SKOJARZONA — patrz R10)
WHERE hb_IdNaglowekTr = @nag          -- nagłówek wyciągu (z hb_Transakcja danej linii)
  AND hb_IdTransakcji = @hbId         -- id linii wyciągu (PK CLUSTERED)
  AND hb_idOperacjiBankowej IS NULL;  -- GUARD (R4): atomowo blokuje podwójne księgowanie
```
- **`@@ROWCOUNT == 0` ⇒ „ktoś już zaksięgował"** (operator w GUI lub równoległy request) → **NIE** twórz/zrolluj
  utworzony BP. To eliminuje wyścig na poziomie DB, czego sam `SemaphoreSlim` (single-process) **nie** gwarantuje
  względem operatora w module Bankowość.
- `@@ROWCOUNT == 1` → OK. `> 1` niemożliwe (PK), ale traktuj defensywnie jako błąd.
- `@nag` odczytać z `hb_Transakcja.hb_IdNaglowekTr` w tym samym kroku co reszta danych transakcji.
- Przed UPDATE księguj tylko gdy `hb_Status IN (0,4)` (NOWA/WSTĘPNIESKOJARZONA) — nie nadpisuj `hb_Status=2`.

## 4. Architektura (most: Sfera tworzy + raw domyka)

Odblokować `POST /bank-transactions/{hb_id}/book` (dziś 501). Przepływ w `RealSferaSession`
(metody w większości UŚPIONE już istnieją z poprzednich iteracji — odblokować/dostosować):

1. **SQL read** (`ReadHbTransactionForBooking`, istnieje): kwota, data, `hb_Oznaczenie` (C→BP/19, D→BW/20),
   `hb_idOperacjiBankowej` (idempotent: jeśli != NULL → zwróć istniejącą), `hb_IdNaglowekTr`, `rb_Id`
   (przez `hb_NaglowekIStopka.hb_IdRachunku`).
2. **Guard** `hb_Oznaczenie ∈ {C,D}` (istnieje), guard `rb_Id` not null (istnieje).
3. **STA: `DodajOperacjeBankowa(19|20, rb_Id)`** (`CreateBankOperationCore`, istnieje):
   `WartoscPoczatkowaWaluta` (NIE `WartoscPoczatkowa` — read-only!), `Data`, `ObiektPowiazanyWstaw(1, kh)`
   lub `OperacjaBezDanychKh`, `Tytulem`, `Zapisz` → `nzfId`.
4. **raw UPDATE** (`LinkHbToOperation`, NOWE — zastępuje stary „re-check + rollback Branch B"): UPDATE z §3
   przez własny `SqlConnection` (`SqlConnStr()`), pojedyncza atomowa instrukcja + `SELECT @@ROWCOUNT`.
   - `@@ROWCOUNT == 1` → link ustawiony, sukces (201).
   - `@@ROWCOUNT == 0` → linia powiązana równolegle (operator GUI / wyścig) — guard `IS NULL` ochronił przed
     nadpisaniem cudzego linku; cofnij NASZ świeży BP, odczytaj zwycięzcę (`ReadHbLink`) i zwróć `already_booked`
     (200) z jego `bank_operation_subiekt_id`. Gdy zwycięzca NULL (anomalia WHERE) → `Internal` (retryowalne).
   - raw UPDATE rzucił → rollback BP; sukces rollbacku → `Internal` (500 retryowalne, stan spójny), porażka → `Orphan` (500).
   - **Atomowość (R1):** Sfera (COM) i raw UPDATE to dwa osobne połączenia — brak wspólnej transakcji DB. Zamiast
     niej: sekcja krytyczna `CancellationToken.None` + kompensacyjny rollback (`TryRollbackBp`) + **głośny `LogError`
     na każdy orphan**. `PolaczenieAdoNet` przychodzi jako `__ComObject` (CLAUDE.md) — wspólna transakcja nieosiągalna.
5. **Weryfikacja**: `@@ROWCOUNT==1` JEST potwierdzeniem (to MY ustawiliśmy link z guardem `IS NULL`) — osobny
   re-read zbędny (audyt: 0 triggerów na hb_Transakcja, nic nie nadpisuje kolumny w tej samej chwili).
6. Zwróć `{ bank_operation_subiekt_id: nzfId, hb_id, linked: true }`.

**Sekcja krytyczna NIEANULOWALNA** (`CancellationToken.None`) od `DodajOperacjeBankowa` po UPDATE+weryfikację
(jak już zaimplementowano dla poprzedniego wariantu). Lock per `hb_id` (`SemaphoreSlim`, istnieje). Idempotency-Key
wymagany, replay-with-verify po `GetBookedOperationIdAsync` (istnieje).

## 5. Bezpieczeństwo / mitygacje ryzyka

- **Flaga w configu** `Bridge:EnableHbBooking` (domyślnie `false`). Gdy false → 501-stub (obecny stan).
  Pozwala wyłączyć bez redeployu kodu, gdyby raw UPDATE zaczął szkodzić.
- **Koordynacja cross-process z operatorem (R6) — ŚWIADOMA DECYZJA: NIE `sp_getapplock`/`ins_blokada`, tylko guard `IS NULL`.**
  Ustalenie z audytu: `ins_blokada` jest **oparte na `sp_getapplock`** — trigger `INSTEAD OF INSERT` robi
  `sp_getapplock @resource='<obid>:<obextra>:<obtype>','exclusive','session',0` (DELETE → `sp_releaseapplock`).
  Więc koordynacja z GUI byłaby możliwa tylko przez **INSERT do `ins_blokada`** (`hbId,0,247`) na dedykowanym
  połączeniu trzymanym przez całą sekcję krytyczną. **Nie robimy tego**, bo: (a) atomowy guard `AND
  hb_idOperacjiBankowej IS NULL` sam eliminuje podwójny LINK (przegrany dostaje `@@ROWCOUNT=0`, cofa swój BP);
  (b) replikacja `ins_blokada` = kolejny raw write do wnętrzności Subiekta + ryzyko **stale-locka blokującego
  operatora** przy źle obsłużonym połączeniu — gorszy tryb awarii niż rzadki wyścig, na nietestowalnej (macOS)
  ścieżce write; (c) operator i most nie księgują tej samej linii równolegle (księgowanie zautomatyzowane przez
  Laravel — sens wariantu B to brak ręcznego księgowania). **Gdyby** kiedyś trzeba pełnej wzajemnej wykluczalności:
  INSERT/DELETE `ins_blokada(hbId,0,247)` na dedykowanym połączeniu obejmującym sekcję krytyczną (trigger zrobi
  applock/release). Na teraz: `SemaphoreSlim` per hb_id (intra-proces) + guard `IS NULL` (cross-proces) wystarcza.
- **Rachunek walutowy (R7):** trigger `tr_NzFinanse_OpBank` (`nz__Finanse.sql`) **ROLLBACKuje** INSERT operacji
  19/20 gdy `rb_IdWaluty != 'PLN' AND waluta_dok != waluta_rach` (RAISERROR sev 16). To fail-closed (operacja
  nie powstaje, brak orphana), ale krok 3 padnie. **Przed księgowaniem odczytaj `rb_IdWaluty`; jeśli != 'PLN'
  → odrzuć `UNSUPPORTED_FOREIGN_ACCOUNT`** (albo rozszerz `CreateBankOperationCore` o `Waluta`+`Kurs`).
- **Rollback**: jeśli UPDATE nie ustawi linku albo `@@ROWCOUNT != 1` → `Usun` utworzonej operacji (zero orphanów).
- **Walidacja kolumn przy starcie** (opcjonalnie, defensywnie): sprawdź że `hb_Transakcja` ma kolumny
  `hb_idOperacjiBankowej`, `hb_Status`, `hb_IdNaglowekTr`, `hb_IdTransakcji` (gdyby przyszła wersja Subiekta zmieniła schemat → fail-fast z czytelnym błędem zamiast cichego psucia).
- **Audit log**: przed/po (`hb_id`, `nzfId`, `@@ROWCOUNT`).
- Operacja będzie „luźna" (bez `nzf_NumerWyciagu`) — **akceptowalne** TYLKO dla klienta nieużywającego wyciągów.
  Udokumentować w kontrakcie, że endpoint zakłada brak użycia modułu Wyciągi.

## 6. Rozliczenie (po book)

`1 płatność = 1 faktura` → rozsądny `book-and-settle`: po `book` od razu `/settlements` (reuse
`CreateSettlementAsync`) z `bank_operation_subiekt_id` + `invoice_id`. Albo zostaw rozdzielnie (book → settle
osobno) — decyzja przy implementacji. Matching (która faktura) robi Laravel, NIE most.

## 7. Test akceptacyjny (odwracalny, na prodzie)

Endpoint i tak musi być przetestowany na Windowsie (COM + write). Plan:
1. Wybierz odwracalną transakcję (np. `hb_id=13128`, Szyszka 3372,50, konto rb_Id=1).
2. `POST /bank-transactions/13128/book` (Idempotency-Key).
3. Weryfikacja przez `/admin/query` (read-only):
   - `SELECT hb_idOperacjiBankowej, hb_Status FROM hb_Transakcja WHERE hb_IdTransakcji=13128` → link ustawiony, status=1.
   - `SELECT * FROM nz__Finanse WHERE nzf_Id=<zwrócony nzfId>` → operacja BP poprawna.
   - transakcja znika z `GET /bank-transactions?unbooked_only=true`.
4. `POST /invoices/{FS}/settlements` z `bank_operation_subiekt_id` → rozrachunek rozliczony.
5. **Cofnięcie testu**: usuń rozliczenie (`DELETE /settlements`), cofnij operację w GUI; ręcznie
   `UPDATE hb_Transakcja SET hb_idOperacjiBankowej=NULL, hb_Status=<oryginalny> WHERE hb_IdTransakcji=13128`
   (jednorazowo, do testu — albo zostaw zaksięgowane jeśli poprawne).

## 8. Ryzyka (zweryfikowane adwersaryjnie 2026-06-14)

| # | Ryzyko | Sev | Rozstrzygalne statycznie? | Mitygacja |
|---|---|---|---|---|
| R1 | Brak atomowości Sfera-INSERT ↔ raw UPDATE → orphan BP przy crashu | high | tak | sekcja krytyczna `CancellationToken.None` + kompensacyjny rollback; orphan → `LogError` + **500 `HB_BOOKING_ORPHAN`** (nie 2xx) |
| R2 | **Odwracalność linku w GUI** (cofnięcie operacji utworzonej spoza Sfery) | high | **NIE → bramka §7** | test odwracalny na prodzie PRZED włączeniem flagi |
| R3 | **Re-import/sync wyciągu** może nadpisać/zignorować link | high | **NIE → bramka §7** | guard `hb_Status IN (0,4)` + test re-importu przy SQL Profiler |
| R4 | Podwójne księgowanie (równoległe `/book`) | med | tak | guard `AND hb_idOperacjiBankowej IS NULL` + `@@ROWCOUNT==0` (§3) |
| R5 | GUI księguje atomowo z rozliczeniem; my osobny `/settlements` | low | tak | `book-and-settle` w jednym handlerze (§6); trigger liczy z (operacja,dług) → stan zbieżny |
| R6 | Pomijamy `ins_blokada` → brak koordynacji z operatorem w GUI | low-med | tak | **świadomie**: guard `IS NULL` wystarcza (przegrany cofa BP); `ins_blokada`=`sp_getapplock` NIE replikowane — patrz §5 |
| R7 | Rachunek walutowy → `tr_NzFinanse_OpBank` rolluje INSERT (fail-closed) | low | tak | odrzucać `rb_IdWaluty != 'PLN'` lub ustawiać `Waluta`+`Kurs` (§5) |
| R8 | Nazwy/semantyka kolumn `hb_` zmienią się w przyszłej wersji | low | częściowo | walidacja kolumn przy starcie (fail-fast); flaga wyłączająca |
| R10 | `hb_Status` musi być `=1` (WYGENEROWANA), nie `2` | info | tak | hardcode literal `1` (potwierdzone w obu trace) |

**Potwierdzone „NIE psuje":** integralność DB (0 złamanych FK/CHECK), saldo rachunku (wyliczane, 0 `UPDATE rb__RachBankowy`),
`nz__Finanse` po insercie (0 UPDATE), `hb_PowiazanieTransakcji` (0 zapisów — to matching importu, nie link operacji).
**Główne otwarte:** R2 i R3 nierozstrzygalne ze statycznych plików — wymagają empirycznego testu §7 jako bramki przed flagą.

## 9. Pliki do zmiany

- `src/SubiektBridge.Api/Sfera/RealSferaSession.cs` — `BookBankTransactionAsync` (zamień Branch B rollback na
  raw UPDATE + weryfikację), `CreateBankOperationCore` (jest), `ReadHbTransactionForBooking`/`ReadHbLink` (są).
- `src/SubiektBridge.Api/Controllers/BankTransactionsController.cs` — odblokuj `Book` (z 501-stub na realny flow;
  przywróć z historii git PR #4/#5, dostosuj: zamiast rollback przy linked=false → raw UPDATE).
- `src/SubiektBridge.Api/Configuration/BridgeOptions.cs` — `EnableHbBooking` (bool, default false).
- `src/SubiektBridge.Api/Sfera/FakeSferaSession.cs` — przywróć symulację linked=true.
- `tests/SubiektBridge.Tests/BankReconciliationTests.cs` — testy book happy/idempotent/guards.
- Docs: `INTEGRATION-CONTRACT.md`, `CLAUDE.md`(+AGENTS), `README.md` — opis `/book` jako działający (z flagą).

## 10. Checklist wykonawczy

- [x] `EnableHbBooking` w configu (default false) — bramka w kontrolerze (flaga off → 501-stub)
- [x] `BookBankTransactionAsync`: Sfera operacja → raw UPDATE (`LinkHbToOperation`) z guardem `IS NULL` (§3) → `@@ROWCOUNT` (==1 sukces / ==0 already_booked / błąd → rollback)
- [x] guard `hb_Status IN (0,4)` przed księgowaniem (nie nadpisuj `=2`) → `UNSUPPORTED_HB_STATUS`
- [x] ~~`sp_getapplock`~~ R6: **świadomie pominięte** — guard `IS NULL` wystarcza (uzasadnienie §5)
- [x] atomowość R1: sekcja krytyczna `CancellationToken.None` + kompensacyjny rollback; orphan → 500 `HB_BOOKING_ORPHAN` (głośny `LogError`)
- [x] odrzucanie rachunku walutowego `rb_IdWaluty != 'PLN'` (R7) → `UNSUPPORTED_FOREIGN_ACCOUNT`
- [x] schema drift (R8): nazwane kolumny w SELECT/UPDATE faila-fast czytelnym błędem (osobna walidacja przy starcie zbędna)
- [x] audit log: `LogInformation` przed/po (`hb_id`, `nzfId`), `LogError` na każdą ścieżkę orphan/błędu
- [x] replay idempotency FAIL-CLOSED (verify-fail → pełny flow, nie cache) + nowe kody błędów (HB_BOOKING_FAILED/ORPHAN)
- [x] Fake (sentinele 66666/55555/44444) + 12 testów xUnit (46/46 zielone)
- [x] docs: plan + `CLAUDE.md`(+AGENTS) + `INTEGRATION-CONTRACT.md`
- [ ] release (tag) → wdrożenie → **test odwracalny na prodzie (§7) jako BRAMKA** (R2 odwracalność + R3 re-import) → dopiero potem `EnableHbBooking=true`
- [ ] po potwierdzeniu: ewentualnie `book-and-settle`
