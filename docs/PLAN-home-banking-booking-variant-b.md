# Plan pracy: księgowanie przelewu HB (wariant B — Sfera + raw UPDATE)

> Status: **PLAN** (nie zaimplementowane). Decyzja o realizacji — po stronie właściciela.
> Stan obecny: `POST /bank-transactions/{hb_id}/book` → `501 HB_BOOKING_NOT_SUPPORTED` (v0.9.6).
> Kontekst pełny: `~/.claude/.../memory/home-banking-booking.md`.

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

## 3. Dokładny UPDATE (z profilera, parametryzowany)

```sql
UPDATE hb_Transakcja
SET hb_idOperacjiBankowej = @nzfId,   -- nzf_Id operacji utworzonej przez DodajOperacjeBankowa
    hb_Status = 1                     -- status "zaksięgowana" (z profilera)
WHERE hb_IdNaglowekTr = @nag          -- nagłówek wyciągu (z hb_Transakcja danej linii)
  AND hb_IdTransakcji = @hbId;        -- id linii wyciągu
```
- Zweryfikować `@@ROWCOUNT == 1` (dokładnie jeden wiersz). 0 lub >1 → błąd, rollback.
- `@nag` odczytać z `hb_Transakcja.hb_IdNaglowekTr` w tym samym kroku co reszta danych transakcji.

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
4. **raw UPDATE** (NOWE — zastępuje obecny „re-check + rollback Branch B"): wykonaj UPDATE z §3 przez
   `SqlConnection` (`SqlConnStr()`). Sprawdź `@@ROWCOUNT==1`.
5. **Weryfikacja**: `ReadHbLink(hb_id)` → potwierdź `hb_idOperacjiBankowej == nzfId`. Jeśli nie → rollback
   (`TryRollbackBp` — `Usun` operacji + log).
6. Zwróć `{ bank_operation_subiekt_id: nzfId, hb_id, linked: true }`.

**Sekcja krytyczna NIEANULOWALNA** (`CancellationToken.None`) od `DodajOperacjeBankowa` po UPDATE+weryfikację
(jak już zaimplementowano dla poprzedniego wariantu). Lock per `hb_id` (`SemaphoreSlim`, istnieje). Idempotency-Key
wymagany, replay-with-verify po `GetBookedOperationIdAsync` (istnieje).

## 5. Bezpieczeństwo / mitygacje ryzyka

- **Flaga w configu** `Bridge:EnableHbBooking` (domyślnie `false`). Gdy false → 501-stub (obecny stan).
  Pozwala wyłączyć bez redeployu kodu, gdyby raw UPDATE zaczął szkodzić.
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

## 8. Ryzyka

| Ryzyko | Mitygacja |
|---|---|
| Nazwy kolumn `hb_` zmienią się w przyszłej wersji Subiekta | walidacja kolumn przy starcie (fail-fast); flaga wyłączająca |
| Brak transakcyjności Sfera↔UPDATE | rollback operacji gdy UPDATE/weryfikacja zawiedzie |
| Operacja luźna psuje wyciągi | nie dotyczy (klient nie używa wyciągów) — zapisać w kontrakcie |
| InsERT nie wspiera tej drogi | świadoma decyzja; flaga pozwala szybko wyłączyć |

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

- [ ] `EnableHbBooking` w configu (default false)
- [ ] `BookBankTransactionAsync`: Sfera operacja → raw UPDATE (§3, `@@ROWCOUNT==1`) → weryfikacja → rollback przy błędzie
- [ ] walidacja kolumn `hb_Transakcja` przy starcie (defensywnie)
- [ ] audit log
- [ ] Fake + testy xUnit
- [ ] docs + `cp CLAUDE.md AGENTS.md`
- [ ] release (tag) → wdrożenie → test odwracalny na prodzie (§7) z flagą włączoną
- [ ] po potwierdzeniu: ewentualnie `book-and-settle`
