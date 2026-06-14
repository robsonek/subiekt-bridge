# SubiektBridge — przewodnik integracji KLIENTA (nowy system sprzedażowy)

> **Topologia tej integracji.**
> ```
> Nowy system sprzedażowy  ──HTTPS + X-Bridge-Token──►  SubiektBridge  ──COM/Sfera──►  Subiekt GT
>      (Ty budujesz klienta)         JSON REST            (już istnieje, działa)        (ten sam co dla marketplace-manage)
> ```
> Most i Subiekt **już są**. Nie budujesz mostu ani nie dotykasz Subiekta — budujesz
> **konsumenta** tego API: kod, który woła endpointy, by wystawiać FS, KFS i PZ z nowego
> systemu. Referencyjny istniejący klient to Laravelowy `marketplace-manage`.
>
> Faktury z nowego systemu trafiają do **tego samego** Subiekta co dotychczasowe.
> Zamówienia są rozłączne (nowe), więc realnej kolizji nie ma — **pod jednym warunkiem**:
> patrz §5 (namespacing `external_reference`).

---

## 1. Co możesz wywołać

| Operacja | Wywołanie | Wymaga `Idempotency-Key` |
|---|---|---|
| Wystaw fakturę sprzedaży (FS) | `POST /api/v1/invoices` | ✅ |
| Wystaw korektę (KFS) | `POST /api/v1/invoices/{id}/corrections` | ✅ |
| Wystaw przyjęcie magazynowe (PZ) | `POST /api/v1/receipts` | ✅ |
| Wystaw przesunięcie międzymagazynowe (MM) | `POST /api/v1/transfers` | ✅ |
| Rozlicz fakturę z przelewem z wyciągu (**FS i FZ**, nie korekty) | `POST /api/v1/invoices/{id}/settlements` | ✅ |
| Stan rozliczenia faktury | `GET /api/v1/invoices/{id}/settlements` | — |
| Cofnij rozliczenie | `DELETE /api/v1/invoices/{id}/settlements/{rozliczenie_id}` | — (idempotentny) |
| Lista operacji bankowych z wyciągu | `GET /api/v1/bank-operations?from&to&direction&unsettled_only&limit` | — |
| Surowe przelewy z wyciągu (do dopasowania po Twojej stronie) | `GET /api/v1/bank-transactions?direction&unbooked_only&from&to&limit` | — |
| Sprawdź towar po EAN | `GET /api/v1/products?ean=` | — |
| Sprawdź kontrahenta po NIP | `GET /api/v1/contractors?nip=` | — |
| Lista FS/KFS | `GET /api/v1/invoices?from&to&type&notes_contains&nip&limit` | — |
| Lista PZ | `GET /api/v1/receipts?...` | — |
| Pojedynczy dokument / PDF | `GET /api/v1/invoices/{id}` , `/{id}/pdf` | — |
| Health / status | `GET /api/v1/health` | — |

`{id}` w ścieżce to **bridge id** w formacie `sub_<SUBIEKT_ID>` (np. `sub_142877`).
Dostajesz go w polach `id` / `existing_bridge_id` z response.

---

## 2. Zasady wywołań (obowiązkowe)

- **Każdy request:** nagłówek `X-Bridge-Token: <sekret>`. Brak/zły → `401`.
- **Każda operacja mutująca (POST):** nagłówek `Idempotency-Key: <uuid>`. Brak → `400 MISSING_IDEMPOTENCY_KEY`.
  - Klucz generujesz **Ty (klient)**, jeden stabilny klucz na (zamówienie × typ dokumentu).
    Przy retry użyj **tego samego** klucza → most zwróci zapisany wynik, nie zdubluje faktury.
- **HTTPS z self-signed certem** — most generuje własny cert. W kliencie ustaw `verify=false`
  (albo zaufaj certowi mostu). To celowe, nie błąd.
- **JSON snake_case** — wszystkie pola tak jak poniżej. Nie camelCase.
- **Daty** w requestach: `YYYY-MM-DD`. **Kwoty:** brutto-centryczne `decimal`. **Waluta:** ISO (`PLN`).
  **Kraj:** ISO alfa‑2 (`PL`). **VAT:** liczba (np. `23`).

---

## 3. Co wysyłasz / co dostajesz

Autorytatywne źródło: `src/SubiektBridge.Api/Models/InvoiceModels.cs`.

### 3.1 `POST /api/v1/invoices` — FS

```jsonc
{
  "type": "FS",
  "issue_date": "2026-06-01",   // YYYY-MM-DD; data = dziś → Subiekt sam nadaje; data wsteczna → most ustawia twardo
  "sale_date": "2026-06-01",    // jw. (w Subiekcie: data zakończenia dostawy)
  "payment": {
    "attribute": "PlatnoscPrzelew",   // forma płatności (§3.5)
    "method_subiekt_id": null,         // int? — id słownika; null gdy forma go nie ma
    "amount": 123.45,
    "is_settled": false                // czy zapłacone (UWAGA §7.4)
  },
  "currency": "PLN",
  "contractor": { /* §3.4 */ },
  "lines": [ /* §3.3 */ ],
  "shipping": { "include": true, "name": "Wysyłka", "unit_price_gross": 12.99, "vat_rate": 23 },
  "totals": { "net": 100.36, "vat": 23.09, "gross": 123.45 },
  "external_reference": "nowysystem:order:ABC123",   // klucz biznesowy → anty-duplikat (§5)
  "notes": "...",
  "warehouse_subiekt_id": 1            // int? — id magazynu w Subiekcie (null = domyślny)
}
```

**Response `201 Created`:**

```jsonc
{
  "id": "sub_142877",
  "subiekt_id": 142877,
  "number": "FS 123/2026",
  "issued_at": "2026-06-01T10:22:00+02:00",
  "contractor_subiekt_id": 5512,
  "totals": { "net": 100.36, "vat": 23.09, "gross": 123.45 },
  "pdf_url": null,
  "pdf_base64": null
}
```

Zapisz u siebie `subiekt_id` / `id` jako wskaźnik na dokument w Subiekcie.

### 3.2 `POST /api/v1/invoices/{id}/corrections` — KFS

```jsonc
{
  "issue_date": "2026-06-10",
  "reason": "Zwrot towaru",
  "source_is_external": false,         // true tylko gdy korygujesz dokument spoza Subiekta
  "source_invoice_number": null,       // wymagane gdy source_is_external=true
  "source_invoice_date": null,
  "lines": [ /* CorrectionLineDto */ ],
  "external_reference": "nowysystem:order:ABC123:correction",
  "payment": null                       // opcjonalnie wymuś formę płatności korekty (§7.4)
}
```

`{id}` = bridge id korygowanej FS (`sub_142877`). `CorrectionLineDto` ma **ten sam shape co linia FS**,
ale `quantity` to **zmiana ilości** — **ujemna** dla zwrotu. ⚠️ Klucz musi się nazywać `quantity`
(nie `quantity_change`), inaczej korekta zaksięguje 0 szt. Response: `201` z `InvoiceResponseDto`.

### 3.3 Linia (`LineDto`)

```jsonc
{
  "ean": "5901234567890",   // string? — jeśli null, dopasowanie po name_fallback
  "name_fallback": "Produkt X",
  "quantity": 2,
  "unit": "szt",
  "unit_price_gross": 49.99,
  "vat_rate": 23,
  "unit_price_net": null    // decimal? — opcja TYLKO dla PZ (§3.6); FS/KFS ignoruje
}
```

Most szuka towaru po `ean` w kartotece Subiekta. **Brak towaru → `422 MISSING_PRODUCT`**
(`details.missing_eans`). Dlatego rób lookup zawczasu (§6, krok 1) albo zadbaj, by EAN-y istniały w Subiekcie.

### 3.4 Kontrahent (`ContractorDto`)

```jsonc
{
  "is_person": false,
  "symbol": "NOWYSYS_ABC",   // ⚠️ Subiekt: max 16 znaków, tylko [A-Za-z0-9-_] (§7.2)
  "nip": "1234567890",       // string? — null dla osoby fizycznej
  "name": "Firma Sp. z o.o.",
  "full_name": null,
  "first_name": null, "last_name": null,
  "email": null,
  "address": { "street": "...", "post_code": "00-000", "city": "...", "country_code": "PL" }
}
```

### 3.5 Płatność (`PaymentDto`)

- `attribute` — forma płatności w terminologii Subiekta: `PlatnoscPrzelew`, `PlatnoscGotowka`,
  `PlatnoscKredyt`, `PlatnoscKarta`, `PlatnoscRaty`.
- `method_subiekt_id` (`int?`) — id słownika formy (`sl_FormaPlatnosci`). `PlatnoscPrzelew`
  i `PlatnoscGotowka` **nie mają** id → wyślij `null`, most ustawi tylko kwotę.
- `is_settled` — patrz §7.4, ma realny wpływ na księgowanie form odroczonych.

### 3.6 `POST /api/v1/receipts` — PZ (przyjęcie magazynowe)

```jsonc
{
  "issue_date": "2026-06-01",
  "warehouse_subiekt_id": 1,
  "supplier": { /* ContractorDto — dostawca */ },
  "lines": [ /* LineDto — cena = cena ZAKUPU; patrz uwaga o netto niżej */ ],
  // source_invoice_subiekt_id: USUNIĘTE — było martwe (FS wymaga stanu → PZ idzie pierwsze); jeśli je wyślesz, most je zignoruje
  "external_reference": "nowysystem:order:ABC123:supplier:42",
  "notes": "...",
  "original_number": "..."             // string? — numer dok. dostawcy (Subiekt: max 30 zn.)
}
```

**Cena dla PZ — netto czy brutto?** PZ w Subiekcie trzyma się **ceny netto** (`ob_CenaNetto`
wpisane wprost, `ob_CenaBrutto` wyliczane z VAT). Masz dwie opcje:

- **Domyślnie** wysyłasz `unit_price_gross` + `vat_rate`, a most sam przelicza
  `netto = brutto / (1 + vat/100)`. Wygodne, ale przy nietypowych cenach finalne netto
  może różnić się o grosz od Twojej intencji (artefakt zaokrąglenia).
- **Zalecane dla cen zakupu:** podaj `unit_price_net` w linii. Wtedy most wpisuje tę
  wartość **wprost** jako cenę netto pozycji, **bez przeliczania** — żadnych rozjazdów groszowych.
  Gdy `unit_price_net` jest podane, `unit_price_gross` dla tej pozycji jest ignorowane (możesz wysłać `0`).
  `vat_rate` nadal podawaj (Subiekt z niego wyliczy brutto).

`unit_price_net` działa **tylko dla PZ**. Dla FS/KFS jest ignorowane (te dokumenty liczą od brutto).

### 3.7 `POST /api/v1/transfers` — MM (przesunięcie międzymagazynowe)

Przenosi stan towaru między magazynami. **Dokument wewnętrzny magazynowy — NIE idzie do KSeF**
(magazyn nie jest polem schematu e-faktury). Użycie: korekta stanu, gdy dokument wystawiono na
zły magazyn, a samego dokumentu nie da się już zmienić (np. KFS wysłany do KSeF) — przesuwasz
wówczas tylko stan, nie ruszając dokumentu.

```jsonc
{
  "source_warehouse_id": 4,            // int — magazyn źródłowy (sl_Magazyn.mag_Id)
  "dest_warehouse_id": 1,              // int — magazyn docelowy
  "lines": [
    { "ean": "5907085551654", "quantity": 2, "unit": "szt." }
  ],
  "external_reference": "...",         // wymagane — anti-duplicate w Uwagach dokumentu
  "notes": "..."                       // string? — trafia do Uwag
}
```

- **Pozycje tylko towarowe** — `ean` wymagany (przesuwasz realny stan magazynowy, nie usługę).
  Brak EAN lub towar nieznany w Subiekcie → `422` (`MISSING_PRODUCT`).
- **Ceny nie podajesz** — wartość MM (koszt) Subiekt liczy sam z partii (FIFO). Kolumna
  „Cena netto" pozycji zostaje 0; „Wartość magazynowa" niesie rzeczywisty koszt.
- Wymaga `Idempotency-Key`. Response: `{ id, subiekt_id, number, issued_at, source_warehouse_id, dest_warehouse_id }`.
- `source_warehouse_id == dest_warehouse_id` → `422` (`SAME_WAREHOUSE`).
- Powtórzony `external_reference` (dokument już istnieje) → `409` (`DUPLICATE_TRANSFER`).

### 3.8 Listingi i pojedynczy dokument

`GET /api/v1/invoices` query: `from` (YYYY-MM-DD), `to`, `type` (`FS`/`KFS`/brak=oba),
`notes_contains`, `nip`, `limit` (domyślnie 200, max 1000). Zwraca tablicę pozycji z
`subiekt_id, number, type, issue_date, contractor_*, *_amount, notes`.
`GET /api/v1/invoices/{id}/pdf` → strumień `application/pdf` (retro generacja).

### 3.9 Rozliczenia — spinanie faktur z przelewami z wyciągu (**FS i FZ**)

Zakłada, że wyciąg jest **już zaimportowany do Subiekta** (operacje bankowe BP/BW istnieją).
Most spina istniejącą operację bankową z rozrachunkiem faktury — nie tworzy operacji ani nie zaciąga z banku.
Obsługiwane: FS (sprzedaż, wpłata BP) i FZ (zakup, wypłata BW). Korekty (KFS/KFZ) → `422 UNSUPPORTED_DOCUMENT_TYPE`.

**`POST /api/v1/invoices/{id}/settlements`** (wymaga `Idempotency-Key`). `{id}` = bridge id **FS lub FZ**.
```jsonc
{
  "bank_operation_subiekt_id": 277,    // nzf_Id operacji bankowej (z GET /bank-operations)
  "amount": 123.45,                    // PLN; częściowe rozliczenie dozwolone (≤ pozostało)
  "external_reference": "nowysystem:order:ABC123"  // opcjonalne; tylko składnik Twojego Idempotency-Key (NIE zapisuje się w Subiekcie)
}
```
> Most sam wybiera właściwy rozrachunek dokumentu po **otwartej kwocie** i **kontrahencie operacji bankowej**
> (FS marketplace ma dwa rozrachunki: wyzerowany na kupującym + otwarty na płatniku/Allegro Pay). Dlatego
> `bank_operation_subiekt_id` musi wskazywać przelew **tego samego kontrahenta** co otwarty rozrachunek — inaczej
> `422 BANK_OPERATION_CONTRACTOR_MISMATCH`.
Response `201`:
```jsonc
{
  "rozliczenie_id": 5012, "document_id": "sub_142877", "document_subiekt_id": 142877,
  "rozrachunek_subiekt_id": 90011, "bank_operation_subiekt_id": 277,
  "amount": 123.45, "remaining_after": 0.00, "is_fully_settled": true,
  "settled_at": "2026-06-14T10:00:00+00:00"
}
```

**`GET /api/v1/invoices/{id}/settlements`** → stan:
```jsonc
{
  "document_id": "sub_142877", "document_subiekt_id": 142877, "rozrachunek_subiekt_id": 90011,
  "original_amount": 123.45, "remaining_amount": 0.00, "is_fully_settled": true,
  "last_settlement_at": "2026-06-14T10:00:00+00:00",
  "settlements": [
    { "rozliczenie_id": 5012, "amount": 123.45, "settled_at": "2026-06-14T10:00:00+00:00",
      "splata_subiekt_id": 277, "dlug_subiekt_id": 90011, "type": 1 }
  ]
}
```

**`DELETE /api/v1/invoices/{id}/settlements/{rozliczenie_id}`** → `204` (cofa rozliczenie; nie kasuje
operacji bankowej ani faktury). Idempotentny: powtórny → `404 SETTLEMENT_NOT_FOUND`. Bez `Idempotency-Key`.

**`GET /api/v1/bank-operations`** query: `from`/`to` (YYYY-MM-DD), `direction` (`in`=wpłata BP / `out`=wypłata BW),
`unsettled_only` (true = tylko z niewykorzystanym saldem), `limit` (max 1000). Zwraca `subiekt_id, direction,
date, amount, remaining, contractor_id, title, number` — `subiekt_id` to `bank_operation_subiekt_id` do POST settlements.

### 3.10 Surowe przelewy z wyciągu — **most wystawia DANE, dopasowanie robisz TY**

Analogicznie do `GET /invoices` (dopasowanie do zamówień): most to **głupi passthrough**, NIE matchuje, NIE
klasyfikuje, NIE rozpoznaje kontrahenta. Logika „która wpłata do której należności" + tiery pewności + auto vs
ręcznie = Twoja strona (znasz `subiekt_id` swoich FS z własnego modelu + `remaining` z `GET /invoices/{id}/settlements`).

**`GET /api/v1/bank-transactions`** — surowe przelewy z wyciągu (przed zaksięgowaniem). Query: `direction`
(`in`=C/wpłata, `out`=D/wypłata), `unbooked_only` (domyślnie true → `hb_idOperacjiBankowej IS NULL`), `from`/`to`,
`limit`. Zwraca surowe pola: `hb_id, date, amount, direction, contractor_name, contractor_account, title,
invoice_number, booked, bank_operation_subiekt_id` (= `hb_idOperacjiBankowej`; `null` gdy niezaksięgowana),
`rachunek_id` (rb_Id konta wyciągu, na które wpłynął przelew) + `rachunek_numer` (IBAN wyciągu).

Typowy przepływ po Twojej stronie: pobierz `bank-transactions?unbooked_only=true&direction=in`, dopasuj po
`amount` + `contractor_name`/rachunku do swoich otwartych FS, zdecyduj (auto/ręcznie), zaksięguj
(`POST /bank-transactions/{hb_id}/book`), a potem rozlicz `POST /invoices/{id}/settlements` przekazując
`bank_operation_subiekt_id` zwrócony przez book. Dwuznaczność (np. dwie faktury tej samej kwoty) rozstrzygasz po swojej stronie.

**`POST /api/v1/bank-transactions/{hb_id}/book`** (wymaga `Idempotency-Key`). Body opcjonalne:
`{ "contractor_subiekt_id": <id|null>, "keep_unlinked": false }`. Tworzy operację bankową BP (C/wpłata) lub BW
(D/wypłata) na koncie wyciągu. Response:
```jsonc
{ "bank_operation_subiekt_id": 72946, "hb_id": 13128, "linked": true, "already_booked": false, "message": null }
```
- **Sprawdzaj `linked`, NIE sam status HTTP.** `linked=true` → operacja powiązana z wyciągiem, `bank_operation_subiekt_id`
  gotowy do `/settlements` (201). `already_booked=true` → była już zaksięgowana (200, idempotentne).
- `linked=false` → Sfera nie powiązała operacji z linią wyciągu; most **cofa** utworzony BP (200, `bank_operation_subiekt_id=null`,
  `message` wyjaśnia). Wtedy księgowanie zostaje w module Bankowość Subiekta. (`keep_unlinked=true` zostawia BP do inspekcji — tylko do diagnostyki.)
- Idempotentne: powtórka z tym samym kluczem → ten sam wynik; ponowne `book` już zaksięgowanej → `already_booked`.

---

## 4. Obsługa odpowiedzi i błędów (kontrakt — zaimplementuj 1:1)

Format błędu: `{ "code", "message", "details"?, "retry_after_seconds"? }`. Reguły:

- **2xx** → sukces.
- **4xx** → błąd danych po Twojej stronie → **NIE retry'uj**, popraw request / zgłoś operatorowi.
- **5xx / 502 / 503** → most lub Subiekt offline → **retry z backoffem**.
- **409 `DUPLICATE_INVOICE` / `DUPLICATE_RECEIPT` / `DUPLICATE_TRANSFER`** → dokument z tym
  `external_reference` już istnieje → **auto-recovery**: pobierz `details.existing_subiekt_id` /
  `existing_bridge_id` i podbij swój rekord na „wystawione" (zamiast tworzyć nowy).
  To **nie** jest błąd do retry.
- **409 `DUPLICATE_SETTLEMENT`** (settlements) → ta operacja bankowa jest już rozliczona z tym
  rozrachunkiem → **auto-recovery**: pobierz `details.existing_rozliczenie_id` i oznacz płatność jako
  rozliczoną. To **nie** jest błąd do retry.

| HTTP | `code` | Co robisz |
|---|---|---|
| 400 | `MISSING_IDEMPOTENCY_KEY` | dodaj nagłówek `Idempotency-Key` |
| 400 | `MISSING_NIP` / `MISSING_EAN` | brak param. w lookupie |
| 422 | `TOTAL_MISMATCH` | `totals.gross` ≠ Σ(linie+wysyłka) — popraw kwoty (§7.1) |
| 422 | `MISSING_PRODUCT` | EAN nie istnieje w Subiekcie (`details.missing_eans`) — załóż towar / popraw EAN |
| 422 | `EMPTY_LINES` | pusta lista pozycji |
| 422 | `UNSUPPORTED_CURRENCY` | most wystawia wyłącznie PLN — `currency` musi być `"PLN"` |
| 422 | `UNSUPPORTED_VAT_RATE` | `vat_rate` ≠ 23 na wysyłce/pozycji usługowej (EAN=null) — usługi tylko 23%; pozycje towarowe biorą VAT z kartoteki |
| 422 | `INVALID_DATE` | `issue_date`/`sale_date`/`source_invoice_date` nie w formacie `YYYY-MM-DD` (lub data niemożliwa kalendarzowo) |
| 404 | `INVOICE_NOT_FOUND` / `RECEIPT_NOT_FOUND` | zły `{id}` |
| 404 | `SETTLEMENT_NOT_FOUND` | (DELETE) rozliczenie nie istnieje / już cofnięte |
| 422 | `INVALID_BRIDGE_ID` | `{id}` nie w formacie `sub_<n>` |
| 422 | `SETTLEMENT_NOT_SUPPORTED` | dokument bez rozrachunku (goły PZ/MM) lub rozrachunek na centrum kart/rat — nie retry |
| 422 | `UNSUPPORTED_DOCUMENT_TYPE` | settlements obsługują tylko FS/FZ; korekty (KFS/KFZ) i inne typy odrzucane — nie retry |
| 422 | `ALREADY_SETTLED` | rozrachunek już rozliczony (np. faktura gotówkowa) — nie retry |
| 422 | `INVALID_AMOUNT` / `AMOUNT_EXCEEDS_REMAINING` | `amount` ≤ 0 lub > pozostało do zapłaty |
| 422 | `BANK_OPERATION_NOT_FOUND` / `BANK_OPERATION_EXHAUSTED` / `BANK_OPERATION_CONTRACTOR_MISMATCH` | zła/skonsumowana operacja bankowa lub inny kontrahent niż rozrachunek |
| **409** | **`DUPLICATE_INVOICE` / `DUPLICATE_RECEIPT` / `DUPLICATE_TRANSFER` / `DUPLICATE_SETTLEMENT`** | **auto-recovery** (patrz wyżej) |
| 501 | `NOT_IMPLEMENTED` | operacja nieobsługiwana — zgłoś, nie retry |
| 502 | `SUBIEKT_QUERY_FAILED` / `BRIDGE_DEGRADED` | Subiekt nie odpowiada — **retry** |
| 503 | (health) | sesja Sfery martwa — **retry / circuit-breaker** |
| 500 | `INTERNAL_ERROR` | nieoczekiwany błąd (`details.stack`) — retry ograniczony + alert |

`DUPLICATE_INVOICE.details`:
```json
{
  "existing_subiekt_id": 142877,
  "existing_number": "FS 123/2026",
  "existing_bridge_id": "sub_142877",
  "external_reference": "nowysystem:order:ABC123"
}
```

---

## 5. Idempotency — Twoja odpowiedzialność jako klienta

Most ma dwie warstwy zabezpieczeń; obie zależą od tego, **co Ty wyślesz**:

1. **`Idempotency-Key` (header)** — most cache'uje `klucz → response` (TTL 30 dni).
   Ten sam klucz przy retry = ten sam wynik, bez dublowania. **Generuj jeden stabilny
   klucz na (zamówienie × typ)** i powtarzaj go przy ponowieniu. (Most dodatkowo weryfikuje,
   że dokument z cache nadal istnieje w Subiekcie — jeśli ktoś go skasował, potraktuje request jako nowy.)

2. **Anty-duplikat w Subiekcie po `external_reference`** — przed utworzeniem most szuka
   dokumentu z tym samym `external_reference` (w polu uwag). Trafienie → `409`.
   Most **sam dokleja** `| ref: <external_reference>` do uwag dokumentu (FS/KFS/PZ/MM),
   jeśli nie umieściłeś referencji w `notes` — nie musisz (ale możesz) robić tego sam.

> ⚠️ **Jedyna realna pułapka przy współdzielonym Subiekcie.**
> Subiekt jest **wspólny** z `marketplace-manage`. Anty-duplikat działa po dopasowaniu
> tekstu `external_reference` w uwagach. Jeśli oba systemy użyją np. `order:123`, mogą
> się **przypadkiem zderzyć**. Zamówienia są nowe i rozłączne — ale **żeby tak zostało,
> prefiksuj `external_reference` identyfikatorem systemu**, np. `nowysystem:order:<id>`.
> Wtedy przestrzenie kluczy są rozłączne i żaden 409 nie pojawi się „przez pomyłkę".

---

## 6. Rekomendowany przepływ klienta (per zamówienie)

```
1. (opcjonalnie, zalecane) Lookup:
     GET /products?ean=...  dla każdej pozycji  → 404 = towaru brak w Subiekcie
     GET /contractors?nip=...                    → 404 = kontrahent nowy (most go założy z danych w request)
   Cel: złapać brak towaru ZANIM POST zwróci 422 MISSING_PRODUCT.

2. Policz totals u siebie (Σ qty*unit_price_gross + shipping) i wstaw do `totals` (§7.1).

3. POST /invoices  z X-Bridge-Token + Idempotency-Key(stabilny) + external_reference(prefiksowany):
     201 → zapisz subiekt_id / id, oznacz zamówienie 'zafakturowane'
     409 DUPLICATE_INVOICE → weź existing_subiekt_id, oznacz jako 'zafakturowane' (auto-recovery)
     422 → błąd danych, nie retry, zaloguj/alert
     5xx/502/503 → retry z backoffem (ten SAM Idempotency-Key!)

4. PDF (gdy potrzebny): GET /invoices/{id}/pdf

5. Korekta / PZ: analogicznie, własne stabilne klucze i prefiksowane external_reference.
```

---

## 7. Pułapki, które musisz uszanować po stronie klienta

### 7.1 `totals` jest walidowane przez most
Most sam liczy Σ(qty × unit_price_gross) + shipping i porównuje z `totals.gross`. Rozjazd
(np. groszowe zaokrąglenie) → `422 TOTAL_MISMATCH` **zanim** cokolwiek powstanie w Subiekcie.
Licz kwoty tak samo (brutto, ta sama reguła zaokrągleń).

### 7.2 Limity pól kartoteki Subiekta
`contractor.symbol`: **max 16 znaków, tylko `[A-Za-z0-9-_]`**. Email z `@`/`+`, UUID Allegro itp.
przekraczają i Subiekt rzuci kryptyczny błąd. `original_number` (PZ): max 30 znaków.
**Sanityzuj/przycinaj te pola u siebie** przed wysłaniem.

### 7.3 Korekta = ujemna ilość pod kluczem `quantity`
Patrz §3.2 — nie wymyślaj osobnego pola, ujemna wartość w `quantity`.

### 7.4 `is_settled` a forma płatności odroczonej
Dla płatności **odroczonych** (kredyt kupiecki, „Allegro Pay" itp.) wysyłaj `is_settled=false`.
Subiekt przy `kwota=0 + rozliczone=true` auto-konwertuje formę płatności (kredyt → przelew),
co psuje raportowanie. Dla KFS, jeśli zależy Ci na konkretnej formie zwrotu, podaj jawnie
`payment` (inaczej Subiekt domyślnie wrzuca całość na gotówkę).

### 7.5 Magazyn
`warehouse_subiekt_id` to id magazynu **w Subiekcie** (nie Twój wewnętrzny). Jeśli nie znasz —
zostaw `null` (domyślny) albo dogadaj mapowanie magazynów z administratorem Subiekta.

### 7.6 Rozliczenia (settlements)
- **FS i FZ** obsługiwane (`SuDokument.Typ` 2/1). Korekty (KFS/KFZ) → `422 UNSUPPORTED_DOCUMENT_TYPE`
  (mają dwa rozrachunki z różnymi kontrahentami — niejednoznaczne).
- **Tylko płatność odroczona** (przelew/kredyt kupiecki). FS gotówkowa/kartowa/ratalna →
  `422 ALREADY_SETTLED` (rozrachunek auto-rozliczony) lub `BANK_OPERATION_CONTRACTOR_MISMATCH`
  (płatność kartą/ratami: rozrachunek na centrum autoryzacji, nie na kontrahencie przelewu).
- **`Idempotency-Key`**: zbuduj stabilny klucz z `(document_subiekt_id × bank_operation_subiekt_id × amount)`.
  `external_reference` służy tylko jako jego składnik — **nie zapisuje się w Subiekcie** (rozliczenie nie ma pola uwag).
- **Jedna operacja bankowa rozlicza dany rozrachunek tylko raz** (powtórka → `409 DUPLICATE_SETTLEMENT`).
  Zbiorcza wpłata na **różne** faktury (różne rozrachunki, ten sam `bank_operation_subiekt_id`) działa normalnie.
- Tylko **PLN**. Walutowe rozrachunki → `422 UNSUPPORTED_CURRENCY`.

---

## 8. Diagnostyka i pliki referencyjne

- **Health przed kampanią requestów:** `GET /api/v1/health` → `200` + `sfera_session: "active"`.
  `503` = Subiekt/Sfera offline, wstrzymaj wysyłkę (circuit-breaker).
- **Pełne, autorytatywne DTO:** `src/SubiektBridge.Api/Models/InvoiceModels.cs`.
- **Dokładna logika statusów/błędów:** `src/SubiektBridge.Api/Controllers/*.cs`.
- **Referencyjny istniejący klient** (jak to robi marketplace-manage): klasy
  `SubiektBridgeClient`, `{InvoiceIssuer,ReceiptIssuer,InvoiceCorrectionIssuer}`,
  `{IssueInvoiceJob,IssueCorrectionJob,IssueReceiptJob}` — wzorzec retry/409/idempotency 1:1.

---

### TL;DR dla agenta-integratora
1. Budujesz **klienta**, nie most. Most i Subiekt już działają.
2. Na każdy request `X-Bridge-Token`; na każdy POST **stabilny `Idempotency-Key`** (powtarzany przy retry).
3. **Prefiksuj `external_reference`** identyfikatorem nowego systemu — to jedyne, co chroni przed kolizją w współdzielonym Subiekcie.
4. Obsłuż statusy wg §4: 4xx=nie retry, 5xx/502/503=retry, **409=auto-recovery (nie błąd)**.
5. Pre-waliduj u siebie: totals (§7.1), limit symbolu/numeru (§7.2), ujemne `quantity` w korekcie (§7.3), `is_settled` przy odroczonych (§7.4).
