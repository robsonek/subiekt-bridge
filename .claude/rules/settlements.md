---
paths:
  - "src/SubiektBridge.Api/Sfera/RealSferaSession.cs"
  - "src/SubiektBridge.Api/Sfera/*Settlement*.cs"
  - "src/SubiektBridge.Api/Controllers/{Settlements,BankOperations}Controller.cs"
  - "src/SubiektBridge.Api/Models/SettlementModels.cs"
  - "tests/SubiektBridge.Tests/{Settlements,BankReconciliation}Tests.cs"
---

# Rozliczenia rozrachunków (settlements) — pułapki

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

- **`bank_operation_subiekt_id` musi być BP/BW** — guard `nzf_Typ IN (19,20)` przez `OtworzKolekcje("nzf_Id=…")`
  → inaczej `422 UNSUPPORTED_BANK_OPERATION_TYPE`. Bez tego KP/KW albo inny rozrachunek tego samego kontrahenta
  przechodził wszystkie guardy i `Rozlicz` je spinał.
- **`GET settlements` agreguje `settlements` ze WSZYSTKICH wierszy** rozrachunku; nagłówek z otwartego (max
  pozostało), a gdy wszystkie zamknięte — z wiersza o największym `RozliczenieId` (NIE po `DataOstatniejSplaty`:
  wg CHM to późniejsza z dat POWSTANIA rozrachunku/spłaty, oba wiersze FS mają ją równą). Wcześniej po pełnym rozliczeniu wiersza płatnika nagłówek+lista mogły
  pochodzić z wiersza kupującego (kolejność DB) → replay idempotencji nie znajdował świeżego `RozliczenieId`.

## Idempotencja rozliczeń

**Kolejność guardów: duplikat PRZED `BankOperationExhausted`/`AlreadySettled`/`ContractorMismatch`, skan po
WSZYSTKICH wierszach.** (`Exhausted` też musi być za skanem: po pełnym rozliczeniu przelewu 1:1 `WartoscBiezaca`
BP = 0.) Wyjątek przy weryfikacji replay w kontrolerze → 502 BEZ kasowania klucza (jak FS/PZ/MM).
Po pełnym rozliczeniu (typowy przypadek) retry trafiał w „brak otwartej kwoty” → `422 ALREADY_SETTLED` (Laravel:
koniec, płatność błędna) zamiast `409 DUPLICATE_SETTLEMENT` z `existing_rozliczenie_id` (auto-recovery).
Skan czyta `SplataId` akcesorem **rzucającym** (`ReadInt64OrNull`) — `TryReadInt64` połykał wyjątek → null →
„brak duplikatu” → `Rozlicz` (fail-open). Realny null (kompensata, `nzs_IdSplaty` NULL) jest tolerowany.

Rozliczenie NIE ma pola Uwagi → anti-duplicate czyta STAN (`FinDokument.Rozliczenia` po
`SplataId == bank_operation_subiekt_id`), **FAIL-CLOSED** (każdy wyjątek skanu przerywa flow — podwójne
rozliczenie tej samej kwoty to błąd księgowy, inaczej niż fail-open dla FS). Match → 409 `DUPLICATE_SETTLEMENT`
z `existing_rozliczenie_id`. Replay-with-verify weryfikuje po `RozliczenieId` (nie po istnieniu dokumentu —
ten zawsze istnieje). `DELETE .../settlements/{id}` idempotentny z natury (powtórny → `404 SETTLEMENT_NOT_FOUND`).
