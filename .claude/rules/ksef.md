---
paths:
  - "src/SubiektBridge.Api/Sfera/RealSferaSession.cs"
  - "src/SubiektBridge.Api/**/*Ksef*.cs"
  - "tests/SubiektBridge.Tests/KsefTests.cs"
---

# KSeF przez Sferę (`EFakturyKSeFManager`) — pułapki

Wysyłka e-Faktur (`POST /invoices/{id}/ksef`): pipeline `SprawdzPoprawnoscEFakturyKSeF` →
`GenerujEFaktureKSeF` → `WyslijEFaktureKSeF(id, tryb=1 Czekaj)` → dla statusu 4 `PobierzNumerKSeF`.
POST = idempotentny „advance" maszyny stanów `StatusKSeF`; sync z capem `Bridge:KsefSendTimeoutSeconds`
(90 s), po capie 202 (klient ponawia POST). BEZ Idempotency-Key (stan w Subiekcie; powtórny POST nic nie
wysyła 2x). `GET .../ksef` = czysty odczyt, niczego nie dociąga.

- **Dozwolone statusy wejściowe** (CHM): `Sprawdz` {0,1,6,7}; `Generuj` {0,1,2,6,7}; `Wyslij` wymaga
  wygenerowanej e-Faktury (status 2; **status 8 też ją MA** — retry po błędzie komunikacji idzie
  prosto w `Wyslij`, `Generuj` dla 8 jest NIEdozwolone).
- **Status 4 (`PrzetwarzanaWKSeF`) NIE przejdzie sam w 5** — numer KSeF trzeba DOCIĄGNĄĆ
  (`PobierzNumerKSeF`). Dlatego POST jest „advance" i klient polluje POST-em, nie GET-em.
- **`Wyslij`/`PobierzNumer` zwracają `OperacjaWTle`** (`Zakonczona`/`Opis`/`Status`/`Blad`) —
  poll KRÓTKIMI jobami STA co 500 ms (`await Task.Delay` między), NIE blokującą pętlą w jednym
  jobie (most byłby głuchy na health/invoices przez cały czas wysyłki).
- **Release `OperacjaWTle` DOPIERO po `Zakonczona==true`** (CHM nie potwierdza bezpieczeństwa
  release'u niedokończonej operacji). Po capie HTTP własność RCW przejmuje task w tle
  (`ContinueKsefPollInBackgroundAsync`) — klient dostaje 202 i ponawia POST. Wyjątek
  last-resort (BEST-EFFORT): po ~15 min task zwalnia RCW mimo braku `Zakonczona` + log ERROR;
  przy trwale zaklinowanym STA release się nie wykona — ale wtedy martwy jest cały most,
  restart usługi leczy (śmierć procesu zwalnia RCW).
- **Gate `_ksefInFlight` per dokument** — równoległe POSTy nie wchodzą w pipeline (kolejka STA
  serializuje pojedyncze joby, NIE cały flow; synchroniczność ustawienia statusu 3 przez Sferę
  nieweryfikowalna). Drugi POST → snapshot ze statusem `sending` → 202.
- **Anulowanie klienta sprawdzane tylko PRZED startem pipeline** — start i poll na
  `CancellationToken.None` (anulowany Task w `RunOnStaAsync` porzuciłby zwrócony RCW).
- **Kontroler: mapowanie statusów WYCZERPUJĄCE** — 200 tylko dla `registered`; stan nie-końcowy
  po zakończonej operacji → 502 `KSEF_SEND_INCOMPLETE` (nie udawać sukcesu).
- **Po każdej operacji status czytaj z PRZEŁADOWANEGO dokumentu** (`WczytajDokument`); dokument
  wczytuj tylko do odczytu metadanych i zwalniaj PRZED wywołaniami managera (metody biorą dokId).
- **Wysyłka wymaga podłączenia Subiekta do Konta InsERT** + skonfigurowanego KSeF podmiotu —
  najczęstsza przyczyna `KSEF_COMMUNICATION_ERROR` (502) na świeżej instalacji.
- **`StatusKSeF` stosuje się tylko do dokumentów z `FormaDokumentu=1`** — inne odrzucamy 422
  `NOT_KSEF_INVOICE` przed dotknięciem managera.
- Wysyłka NIEODWRACALNA; środowisko KSeF (prod/test MF) to konfiguracja podmiotu w Subiekcie.
- Dostępność API: od GT 1.77/1.80 (prod klienta 1.89 HF2 OK; przy pisaniu endpointu 1.88 HF4).
- **Odrzucenie osiągnięte W TLE (po capie) jest zapamiętywane** (`_ksefBackgroundRejection`, od v0.18.0) i
  najbliższy POST zwraca je jako `422 KSEF_REJECTED` (jednorazowo, tylko gdy dokument nadal ma status 6).
  Bez tego: klient wg kontraktu polluje POST-em → status 6 na wejściu = „walidacja od nowa” = PONOWNA WYSYŁKA
  w pętli, a 422 nigdy nie docierał. Status 8 (błąd komunikacji) NIE jest zapamiętywany — retry przez Wyslij
  jest zamierzony.
- **Na realnym COM nigdy niezweryfikowane** — checklist w `docs/superpowers/plans/2026-08-12-ksef-endpoint.md` (Task 6).
