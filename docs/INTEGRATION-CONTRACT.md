# SubiektBridge — przewodnik integracji KLIENTA (nowy system sprzedażowy)

> **Dotyczy:** SubiektBridge **v0.21.2** (wydane 2026-10-13; v0.21.1 wydane 2026-10-12). Zmiany między wersjami: [Historia zmian kontraktu](#9-historia-zmian-kontraktu) na końcu.

> **Topologia tej integracji.**
> ```
> Nowy system sprzedażowy  ──HTTPS + X-Bridge-Token──►  SubiektBridge  ──COM/Sfera──►  Subiekt GT
>      (Ty budujesz klienta)         JSON REST            (już istnieje, działa)        (ten sam co dla istniejącego klienta)
> ```
> Most i Subiekt **już są**. Nie budujesz mostu ani nie dotykasz Subiekta — budujesz
> **konsumenta** tego API: kod, który woła endpointy, by wystawiać FS, KFS i PZ z nowego
> systemu. Referencyjny istniejący klient to aplikacja Laravel.
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
| Szczegóły PZ: pozycje, powiązania (FZ/KFS), edytowalność, `revision` (od v0.21.0) | `GET /api/v1/receipts/{id}` | — |
| Dopisz numer dokumentu dostawcy do istniejącego PZ (`original_number`, od v0.21.0) | `PATCH /api/v1/receipts/{id}` | ✅ |
| Wystaw przesunięcie międzymagazynowe (MM) | `POST /api/v1/transfers` | ✅ |
| Rozlicz fakturę z przelewem z wyciągu (**FS i FZ**, nie korekty) | `POST /api/v1/invoices/{id}/settlements` | ✅ |
| Stan rozliczenia faktury | `GET /api/v1/invoices/{id}/settlements` | — |
| Cofnij rozliczenie | `DELETE /api/v1/invoices/{id}/settlements/{rozliczenie_id}` | — (idempotentny) |
| Wyślij e-Fakturę do KSeF (**FS i KFS**) | `POST /api/v1/invoices/{id}/ksef` | — (naturalnie idempotentny, §3.12) |
| Stan KSeF dokumentu (bierny odczyt) | `GET /api/v1/invoices/{id}/ksef` | — |
| Lista operacji bankowych z wyciągu | `GET /api/v1/bank-operations?from&to&direction&unsettled_only&limit` | — |
| Surowe przelewy z wyciągu (do dopasowania po Twojej stronie) | `GET /api/v1/bank-transactions?direction&unbooked_only&from&to&limit` | — |
| Otwarte należności (kandydaci do dopasowania z przelewem) | `GET /api/v1/invoices/open-receivables?min_amount&max_amount&currency&contractor_id&from&to&search&limit` | — |
| Otwarte zobowiązania (kandydaci do dopasowania z wypłatą, FZ) | `GET /api/v1/invoices/open-payables?min_amount&max_amount&currency&contractor_id&from&to&search&limit` | — |
| Sprawdź towar po EAN | `GET /api/v1/products?ean=` | — |
| Sprawdź kontrahenta po NIP | `GET /api/v1/contractors?nip=` | — |
| Lista FS/KFS (z `revision` od v0.21.2) | `GET /api/v1/invoices?from&to&type&notes_contains&nip&limit` | — |
| Lista PZ | `GET /api/v1/receipts?...` | — |
| Pojedynczy dokument (z `revision` FS/KFS od v0.21.2) / PDF | `GET /api/v1/invoices/{id}` , `/{id}/pdf` | — |
| Health / status | `GET /api/v1/health` | — |

`{id}` w ścieżce to **bridge id** w formacie `sub_<SUBIEKT_ID>` (np. `sub_142877`).
Dostajesz go w polach `id` / `existing_bridge_id` z response.

---

## 2. Zasady wywołań (obowiązkowe)

- **Każdy request:** nagłówek `X-Bridge-Token: <sekret>`. Brak/zły → `401`.
- **Każda operacja mutująca (POST, PATCH):** nagłówek `Idempotency-Key: <uuid>`. Brak → `400 MISSING_IDEMPOTENCY_KEY`.
  - Klucz generujesz **Ty (klient)**, jeden stabilny klucz na (zamówienie × typ dokumentu).
    Przy retry użyj **tego samego** klucza → most zwróci zapisany wynik, nie zdubluje faktury.
  - **Klucz jest ważny per operacja i dokument** (od v0.21.0): metoda + ścieżka z id z URL. Ten sam klucz na innym
    endpoincie (np. `POST /receipts` i `PATCH /receipts/{id}`) albo na innym dokumencie (`/invoices/sub_1/corrections`
    vs `/invoices/sub_2/corrections`) to osobny wpis — most nigdy nie zwróci zapisanej odpowiedzi innej operacji.
  - **Wyjątek:** `POST /invoices/{id}/ksef` NIE wymaga `Idempotency-Key` — to idempotentny
    „advance" maszyny stanów KSeF w Subiekcie; powtórny POST nic nie wyśle drugi raz (§3.12).
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

Błędy specyficzne (od v0.20.0): `422 INVALID_EXTERNAL_REFERENCE` (pusty/biały `external_reference` albo > 495 znaków —
nie retry); `503 DUPLICATE_CHECK_UNAVAILABLE` (most nie mógł sprawdzić w Subiekcie, czy dokument z tym `external_reference`
już istnieje — dokument NIE powstał w tym żądaniu, **retry z backoffem tym samym kluczem**). Pełna lista: §4.

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
(nie `quantity_change`), inaczej korekta zaksięguje 0 szt. Response: `201` z `InvoiceResponseDto`;
`contractor_subiekt_id` = kontrahent (płatnik) korygowanej FS (od v0.19.0; do v0.18.0 zawsze `0`). `0` = nieznany.
Błędy specyficzne (od v0.20.0) jak dla FS: `422 INVALID_EXTERNAL_REFERENCE`, `503 DUPLICATE_CHECK_UNAVAILABLE` (§4);
`details.document_type` = `"KFS"`. Uwagi KFS to zawsze `Korekta: <reason> | ref: <external_reference>` — przy `external_reference`
bliskim 495 znaków `reason` się nie zmieści → `422 NOTES_TOO_LONG` (§7.7).

### 3.3 Linia (`LineDto`)

```jsonc
{
  "ean": "5901234567890",   // string? — jeśli null, dopasowanie po name_fallback
  "name_fallback": "Produkt X",
  "quantity": 2,            // decimal — ułamki dozwolone (towary na kg/m), np. 1.5
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
  "symbol": "NOWYSYS_ABC",   // ⚠️ Subiekt: max 20 znaków, zalecane [A-Za-z0-9-_] (§7.2)
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

**Response `201 Created`** — ten sam kształt co FS (§3.1):

```jsonc
{
  "id": "sub_98765",
  "subiekt_id": 98765,
  "number": "PZ 57/2026",
  "issued_at": "2026-06-01T10:22:00+02:00",
  "contractor_subiekt_id": 4410,
  "totals": { "net": 200.00, "vat": 46.00, "gross": 246.00 },
  "pdf_url": null,
  "pdf_base64": null
}
```

`totals` most **odczytuje z zapisanego PZ** (kwoty wyliczone przez Subiekt, nie z Twojego payloadu) — te same wartości co
`net_amount` / `vat_amount` / `gross_amount` w `GET /receipts/{id}` (§3.6b). **Od v0.21.1 `net` i `vat` są wypełnione**
(do v0.21.0 zawsze `null`). `null` w `net`/`vat` tylko, gdy Subiekt nie udostępni tej kwoty (nigdy zastępcze `0`) — wtedy
zostaw u siebie dotychczasową wartość albo dociągnij ją przez `GET /receipts/{id}`. `gross` jest zawsze liczbą: gdy Subiekt
nie poda brutto, most liczy je z linii.

Błędy specyficzne (od v0.20.0) jak dla FS: `422 INVALID_EXTERNAL_REFERENCE`, `503 DUPLICATE_CHECK_UNAVAILABLE` (§4;
`details.document_type` = `"PZ"`).

### 3.6b `GET /api/v1/receipts/{id}` i `PATCH /api/v1/receipts/{id}` — istniejące PZ (od v0.21.0)

**GET** zwraca `ReceiptDetailDto` — nadzbiór dawnego wiersza listingu (pola `subiekt_id, number, type, issue_date, contractor_*, *_amount,
notes` bez zmian) plus:

```jsonc
{
  "original_number": "Client:test_buyer",              // „Dokument dostawcy” (SuDokument.NumerOryginalny, max 30 zn.)
  "external_reference": "nowysystem:order:ABC:supplier:42", // pełna referencja z Uwag (reguła niżej); null, gdy brak markera
  "warehouse_id": 4, "status": 1, "booking_status": 0, "period_locked": false,   // status = dok_Status; booking_status = status księgowy (0..7)
  "linked_documents": [ { "relation": "linked", "type": "FZ", "subiekt_id": 60001, "number": "FZ 7/2026" } ],  // FZ/KFS/…; [] = brak
  "editability": { "sfera_can_edit": true, "reasons": [] },  // FAKTY: CANCELLED | NOT_A_RECEIPT | PERIOD_LOCKED | BOOKED | EXPORTED_TO_ACCOUNTING | LINKED_DOCUMENT | SFERA_NOT_EDITABLE
  "revision": "r1:7f3a0b2c9d4e5f61",                      // opaque; do expected_revision
  "items": [ { "position_id": 910001, "lp": 1, "product_subiekt_id": 6931, "ean": "5901234567890", "symbol": "SKU-7890", "name": "Towar testowy",
               "kind": "product", "quantity": 35, "unit": "szt.", "quantity_in_stock_units": 35, "unit_price_net": 24.39, "discount_percent": 0,
               "vat_rate": 23, "value_net": 853.65, "value_gross": 1049.99, "issued_stock_units": 12, "issued_quantity": 12 } ],
  "warnings": []                                           // "SQL_FACTS_UNAVAILABLE" | "STOCK_FACTS_INCONSISTENT"
}
```
- `editability` to informacja, nie decyzja mostu: most NIE odmawia edycji na podstawie powiązania czy `sfera_can_edit` — o odmowie
  decyduje Sfera przy zapisie (409 `RECEIPT_LOCKED` z powodem). Dokument innego typu niż PZ i dokument unieważniony też dają `200`
  (z powodem w `reasons`). `404` tylko, gdy Subiekt POZYTYWNIE potwierdzi brak dokumentu; awaria odczytu → `502 SUBIEKT_QUERY_FAILED`.
- `external_reference` = tekst po **ostatnim** znaczniku `ref: ` (na początku Uwag albo po separatorze ` | `) **do końca pola,
  dosłownie** (ze spacjami) — tak most zapisuje referencję przy `POST` (zawsze na końcu Uwag). `null`, gdy Uwagi nie mają znacznika
  (np. referencja była już w `notes` i most jej nie dopisał). Granica: referencja zawierająca ` | ref: ` jest nieodróżnialna od notatek
  z markerem — wygrywa ostatni znacznik.
- `issued_stock_units` / `issued_quantity` = ile z dostawy tej pozycji już wydano (jednostki magazynowe / jednostki dokumentu);
  `null` + ostrzeżenie, gdy most nie mógł tego odczytać albo dane są niespójne. Pola z bazy (`warehouse_id`, `period_locked`, `ean`,
  `symbol`, `linked_documents[].type`, `issued_*`) są **fail-soft**: `null` + `warnings: ["SQL_FACTS_UNAVAILABLE"]`.

**PATCH** (wymaga `Idempotency-Key`): `{ "original_number": "FZ 1/01/2026", "expected_revision": "r1:…" }` (`expected_revision`
opcjonalne). Zasady:
- `Idempotency-Key` jest ważny dla **tego PZ i tej operacji** (§2): klucz użyty wcześniej w `POST /receipts` albo w PATCH innego PZ
  nie daje replayu. **Każda odrębna zmiana = nowy klucz** (np. `…:original_number:<wartość>`) — ten sam klucz z innym body na tym
  samym PZ zwraca zapisany wynik pierwszej zmiany (replay).
- walidacja PRZED Sferą: brak pola → `422 EMPTY_UPDATE`; `null`, nie-string, pusty → `422 INVALID_ORIGINAL_NUMBER` (`details.reason`);
  po przycięciu spacji > 30 znaków → `422 ORIGINAL_NUMBER_TOO_LONG` (`details.max_length`, `details.length`). **Bez cichego ucięcia**
  (POST `/receipts` nadal tnie `original_number` do 30 — tam nic się nie zmienia).
- ta sama wartość co w dokumencie → `200` bez zapisu (no-op, bez blokowania dokumentu w Subiekcie).
- `200` = stan dokumentu **po odczycie kontrolnym po zapisie** (`ReceiptDetailDto`), nie echo requestu.
- `expected_revision`: niezgodna z bieżącym `revision` → `409 RECEIPT_REVISION_MISMATCH` (`details.current_revision`) PRZED jakąkolwiek
  zmianą. Ponowienie po udanym zapisie: **cache hit** (ten sam `Idempotency-Key`) → `200` z cache niezależnie od `expected_revision`;
  **cache miss + stare `expected_revision`** → `409` **celowo** (sprawdź `GET`); **cache miss bez `expected_revision`** → no-op tylko gdy
  bieżąca wartość równa żądanej, inaczej zapis (nadpisze zmianę operatora — świadomie nie podałeś `expected_revision`).
- edycja pozycji PZ (`PUT …/items`) — planowana w v0.22.0, poza tym kontraktem.

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
    { "ean": "5901234567890", "quantity": 2, "unit": "szt." }
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
- Od v0.20.0 jak dla FS: `422 INVALID_EXTERNAL_REFERENCE`, `503 DUPLICATE_CHECK_UNAVAILABLE` (§4; `details.document_type` = `"MM"`).

### 3.8 Listingi i pojedynczy dokument

`GET /api/v1/invoices` query: `from` (YYYY-MM-DD), `to`, `type` (`FS`/`KFS`/brak=oba),
`notes_contains`, `nip`, `limit` (domyślnie 200, max 1000). `nip` = dokumenty, na których kontrahent
o tym NIP jest nabywcą (porównanie bez kresek i spacji); brak takiego kontrahenta = pusta tablica. Zwraca tablicę pozycji z
`subiekt_id, number, type, issue_date, contractor_*, *_amount, notes, revision` (ten sam kształt co `GET /api/v1/invoices/{id}`).
`GET /api/v1/invoices/{id}/pdf` → strumień `application/pdf` (retro generacja).

**`revision` (od v0.21.2) — znacznik treści wydruku FS/KFS.** Służy do odświeżania przechowywanego PDF, gdy operator zmieni
dokument w Subiekcie (np. nazwę pozycji albo formę płatności — zmiany, których nie widać w kwotach).

```jsonc
{ "subiekt_id": 142877, "number": "FS 12/01/2026", "type": "FS", /* … dotychczasowe pola bez zmian … */
  "revision": "f1:3c9a0e51b7d2f480" }        // opaque; null = brak rewizji (niżej)
```

- Format: `f1:` + 16 znaków hex (małe litery), 19 znaków. **Porównuj tylko równością**, nie parsuj. Pole jest zawsze w JSON
  (także jako `null`). Ten sam stan dokumentu daje tę samą wartość w `GET /invoices/{id}` i w liście.
- `null`, gdy: dokument nie jest FS ani KFS (PZ ma własne `revision` w `GET /receipts/{id}`, §3.6b — to dwie różne wartości);
  most nie odczytał rewizji z bazy (błąd zapytania SQL mostu — odpowiedź to nadal `200`, pozostałe pola są poprawne; przyczyna
  w logu mostu jako WARNING; `/health` pokaże `sql_connection: "down"` tylko wtedy, gdy padło samo połączenie z bazą); dokumentu
  nie ma już w bazie (usunięty między odczytami). Most < v0.21.2 nie zwraca pola — traktuj jak `null`.
  **Przy `null` nie odświeżaj PDF i nie nadpisuj zapisanej rewizji** — spróbuj w następnym przebiegu.
- **Kolejność:** najpierw pobierz `revision` (GET), potem PDF; zapisz rewizję odczytaną **przed** pobraniem PDF. Zmiana w Subiekcie
  w międzyczasie da jedno dodatkowe odświeżenie w następnym przebiegu. Rewizji odczytanej **po** pobraniu PDF nie zapisuj —
  mogłaby opisywać nowszy stan niż plik. Granica: jeśli dokument zmieni się w sekundach między tymi dwoma wywołaniami (plik ma
  już stan B, zapisana rewizja opisuje A), a operator przywróci stan A, zanim kolejny przebieg odświeży plik, rewizja znów
  będzie równa zapisanej i plik ze stanu B zostanie do następnej zmiany dokumentu — tego rewizja nie wykryje.
  Most czyta rewizję trzema zapytaniami (nagłówek, pozycje, tabela VAT) bez wspólnej migawki bazy, więc zapis operatora
  w trakcie odczytu (milisekundy) może dać wartość mieszaną. Skutek to co najwyżej jedno zbędne odświeżenie. Pominąć
  odświeżenie może tylko wtedy, gdy operator później przywróci część dokumentu do wartości sprzed tego zapisu — ta sama
  klasa co powyżej (cofnięcie zmiany).
- Sam odczyt, otwarcie dokumentu w Subiekcie ani wydruk (`/pdf`) **nie zmieniają** rewizji.
- Co ją zmienia: numer, unieważnienie, daty (wystawienia, sprzedaży, magazynowa, termin płatności), uwagi, sumy, waluta, tryb
  cen (od netto / od brutto), adnotacje VAT, tabela VAT (stawki i kwoty), numer KSeF (raz, po nadaniu), forma i kwoty płatności
  (gotówka/karta/przelew/kredyt, przedpłaty, do zapłaty), dane nabywcy, odbiorcy i adresu dostawy **zapamiętane na dokumencie**,
  pozycje (nazwa i symbol towaru albo nazwa usługi jednorazowej, opis, ilość, j.m., ceny, rabat, VAT, wartości, PKWiU/CN/GTU),
  a w KFS także dokument korygowany (z numerem KSeF) i przyczyny korekty.
- Granice: **zmiana nazwy towaru w kartotece Subiekta zmienia rewizję wszystkich FS/KFS z tym towarem** (Subiekt drukuje bieżącą
  nazwę z kartoteki — te PDF naprawdę się zmieniły). Zmiana danych kontrahenta w kartotece **nie** zmienia rewizji (wydruk używa
  danych zapamiętanych przy wystawieniu), dopóki operator nie zaktualizuje ich na dokumencie. Rozliczenie faktury może zmienić
  rewizję **raz** (kwota „do zapłaty”). Zmiana danych Twojej firmy, nazw słownikowych (formy płatności, stawki), księgowanie,
  realizacja skutku magazynowego (dokument odłożony → wykonany) ani wysyłka do KSeF w toku **nie** zmieniają rewizji — po zmianie
  danych firmy odśwież PDF ręcznie.
- `POST /invoices` i `POST /invoices/{id}/corrections` **nie** zwracają rewizji — pobierz ją `GET`-em.
- Zmiana algorytmu = nowy prefiks (`f2:`) = jednorazowo inna rewizja wszystkich dokumentów (ogłoszone w §9).
`GET /api/v1/receipts/{id}` od v0.21.0 zwraca rozszerzony `ReceiptDetailDto` (§3.6b) — dotychczasowe pola bez zmian.

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

**`GET /api/v1/invoices/{id}/settlements`** → stan. `settlements` to rozliczenia ze **wszystkich** rozrachunków
dokumentu (FS marketplace ma zwykle dwa: wyzerowany na kupującym + na płatniku); nagłówek (`rozrachunek_subiekt_id`,
`original_amount`, `remaining_amount`) dotyczy rozrachunku otwartego (największe pozostało), a gdy wszystkie są
zamknięte — tego z najnowszym rozliczeniem (największy `rozliczenie_id`). `is_fully_settled` = wszystkie rozrachunki zamknięte.
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
(`in`=C/wpłata, `out`=D/wypłata), `unbooked_only` (domyślnie true → bez linku **i** `hb_status` ∈ {0,4}, czyli
linie, które da się zaksięgować; linie pominięte przez operatora w Subiekcie (`hb_status`=3) nie są kandydatami),
`from`/`to`, `limit`. Zwraca surowe pola: `hb_id, date, amount, direction, contractor_name, contractor_account, title,
invoice_number, booked, bank_operation_subiekt_id` (= `hb_idOperacjiBankowej`; `null` gdy niezaksięgowana),
`rachunek_id` (rb_Id konta wyciągu, na które wpłynął przelew) + `rachunek_numer` (IBAN wyciągu),
`hb_status` (0=nowa, 1=zaksięgowana, 2=skojarzona, 3=pominięta, 4=wstępnie skojarzona). `amount` jest `0`,
gdy w Subiekcie kwota jest pusta (takiej linii `/book` nie zaksięguje — `422 INVALID_HB_AMOUNT`).

Typowy przepływ po Twojej stronie: pobierz `bank-transactions?unbooked_only=true&direction=in`, dopasuj po
`amount` + `contractor_name`/rachunku do swoich FS, **zaksięguj przelew w module Bankowość Subiekta** (operator),
a potem rozlicz `POST /invoices/{id}/settlements` przekazując `bank_operation_subiekt_id` (z `bank-transactions`
po zaksięgowaniu — pole `booked=true`). Dwuznaczność rozstrzygasz po swojej stronie.

### `POST /api/v1/bank-transactions/{hb_id}/book` — księgowanie przelewu

Tworzy operację bankową BP/BW z surowej linii wyciągu i powiązuje ją (`hb_idOperacjiBankowej`), gotową do
`/settlements`. **Aktywne domyślnie** (wyłącznik serwerowy `Bridge:EnableHbBooking=false` → `501 HB_BOOKING_NOT_SUPPORTED`,
wtedy księgowanie robi operator w module Bankowość). Działanie:

- **Nagłówek `Idempotency-Key` wymagany** (jak FS). Body opcjonalne: `{ "contractor_subiekt_id": 142877 }` (kogo
  przypisać do operacji; brak = operacja bez danych kontrahenta). Most NIE dopasowuje — dostaje rozkaz „zaksięguj hb_id".
- **201** (nowe): `{ "bank_operation_subiekt_id": 73291, "hb_id": 13128, "linked": true, "already_booked": false }`.
  **200** gdy już zaksięgowana (`already_booked: true`) — `bank_operation_subiekt_id` gotowe do `/settlements`.
- **Operacja jest tworzona 1:1 jak ręczna w module Bankowość:** powiązana z linią wyciągu, **uzgodniona** (status
  „zgodna", z numerem wyciągu i datą uzgodnienia) oraz z **surowym tytułem przelewu** (opis z banku, przycięty do
  144 znaków). Most odtwarza pełne ręczne „Zaksięguj" — nie zostawia „luźnych/nieuzgodnionych" operacji. To zachowanie
  wewnętrzne (Sfera) — **nie zmienia request/response**, ale dzięki temu operacja wygląda kompletnie w Subiekcie.
- Błędy: `404 BANK_TRANSACTION_NOT_FOUND`; `422 NO_BANK_ACCOUNT` / `INVALID_DIRECTION` / `UNSUPPORTED_FOREIGN_ACCOUNT`
  (rachunek nie-PLN) / `UNSUPPORTED_HB_STATUS` / `INVALID_HB_AMOUNT`; `503 SUBIEKT_UNAVAILABLE` (Subiekt offline
  **przed** utworzeniem operacji — nic nie zapisano, **retry tym samym kluczem**); `500 HB_BOOKING_FAILED` (**wyłącznie**
  gdy nic nie zapisano albo operacja została czysto cofnięta — **bezpieczny retry**); `500 HB_BOOKING_ORPHAN` (operacja
  powstała lub mogła powstać bez linku — **NIE retry'uj na ślepo**, zgłoś do ręcznego usunięcia; id w
  `details.bank_operation_subiekt_id`, `null` gdy nieodczytane); `501 HB_BOOKING_NOT_SUPPORTED` (flaga wyłączona).
  Gwarancja: **każdy wyjątek po zapisie operacji kończy się `HB_BOOKING_ORPHAN`**, nigdy kodem „ponów”.
- Po `201/200` → rozlicz `POST /invoices/{id}/settlements` z `bank_operation_subiekt_id`.

### 3.11 Otwarte należności — kandydaci do dopasowania z przychodzącym przelewem

Gdy dostajesz wpłatę (np. payout procesora PayNow/Allegro Pay), potrzebujesz listy **otwartych
należności** w oknie kwoty, by dopasować ją do faktury. Ten endpoint zwraca je z kontraktu —
zamiast `POST /admin/query` na wewnętrznych tabelach Subiekta. Most czyta przez **Sferę** (rozrachunki
sprzedaży z otwartym saldem `WartoscBiezaca>0`), **NIE matchuje** — dopasowanie robisz Ty (jak `GET /invoices`).

**`GET /api/v1/invoices/open-receivables`** (read-only, `X-Bridge-Token`, **bez** `Idempotency-Key`). Query:

| Param | Typ | Znaczenie |
|---|---|---|
| `min_amount` | decimal? | dolna granica **pozostałej** kwoty (PLN); brak = bez dolnej granicy |
| `max_amount` | decimal? | górna granica pozostałej kwoty (PLN); brak = bez górnej granicy |
| `currency` | string | domyślnie `PLN`; **tylko PLN** — inna waluta → `422 UNSUPPORTED_CURRENCY` (most rozlicza wyłącznie PLN) |
| `contractor_id` | long? | opcjonalne zawężenie do jednego kontrahenta |
| `from` | YYYY-MM-DD | opcjonalne — dolna granica daty dokumentu; **zalecane** (okno wokół daty przelewu) |
| `to` | YYYY-MM-DD | opcjonalne — górna granica daty dokumentu |
| `search` | string? | opcjonalna fraza **case-insensitive**. Semantyka: **nazwa kontrahenta/NIP ma precedencję nad numerem** — jeśli fraza pasuje do nazwy/NIP istniejącego kontrahenta, dostajesz **otwarte należności tego kontrahenta**; jeśli do żadnego (= fragment numeru FV), dostajesz faktury, których **numer** zawiera frazę. Pusta/brak = bez filtra |
| `limit` | int | domyślnie `50`, **max 200** — zwraca tyle **najnowszych** dopasowań (filtr działa po stronie bazy) |

**Okno kwoty liczysz Ty:** procesory potrącają prowizję (≤3%), więc faktura ≥ przelew →
`min_amount = kwota_przelewu`, `max_amount = kwota_przelewu / 0.97`. Most jest „głupi" — tylko zakres.

**Response `200`** (tablica; pusta gdy brak kandydatów w oknie):
```jsonc
[
  {
    "document_id": "sub_53447",         // bridge id → POST /invoices/{id}/settlements
    "document_subiekt_id": 53447,
    "doc_type": "FS",                    // etykieta z numeru (FS/FZ/...)
    "currency": "PLN",
    "remaining": 3372.50,                // pozostało do zapłaty (po tym matchujesz)
    "contractor_id": 13292,
    "contractor_name": "Szyszka Krzysztof",
    "number": "FS 573/05/2026"
  }
]
```

Typowy przepływ: weź `amount` z `GET /bank-transactions` (albo z `bank-operations` po zaksięgowaniu),
policz okno `[amount, amount/0.97]`, zawołaj `open-receivables` (najlepiej też z `from`/`to` wokół daty
przelewu), dopasuj kandydata po swojej stronie, a potem `POST /invoices/{id}/settlements` z `document_id`.

> **Trzy rzeczy, które musisz wiedzieć:**
> - **Tylko PLN.** `remaining` to zawsze kwota w PLN. Most nie zwraca należności walutowych (`currency`≠PLN → `422 UNSUPPORTED_CURRENCY`) — i tak nie dałoby się ich rozliczyć przez `/settlements`.
> - **Wydajność = wąskie okno.** Filtr (kwota + waluta + `contractor_id` + `from`/`to`) działa **po stronie bazy** — most nie przegląda całej kartoteki rozrachunków. Zwraca do `limit` **najnowszych** dopasowań. Podawaj **okno kwoty** i **`from`/`to`** wokół daty przelewu (faktura jest wystawiana przed wpłatą) — szybciej i trafniej. Jeśli dopasowań w oknie jest więcej niż `limit`, dostajesz `limit` najnowszych; zawęź okno albo podaj `contractor_id`.
> - **`search` po nazwie/NIP = szybko (baza), po numerze = skan.** Szukanie po **nazwie kontrahenta lub NIP** jest rozwiązywane po stronie bazy (SQL `LIKE` → lista kontrahentów → zawężenie zapytania), więc jest tanie nawet bez okna kwoty. Szukanie po **numerze FV** wymaga skanu (numer to atrybut COM, nie kolumna) — zawężaj wtedy **`from`** (np. ostatnie miesiące). Skan po numerze jest **ograniczony do najnowszych ~1000 rozrachunków** w oknie filtra; gdy szukana FV jest starsza i nie zawęzisz `from`, może nie trafić do wyniku — podaj `from` wokół spodziewanej daty.
> - **Nie wszystkie zwrócone pozycje są rozliczalne.** Filtr to „otwarte rozrachunki sprzedaży" (`nzf_Typ=39`) — mogą tu trafić też **korekty (`doc_type` np. `KFS`)**, których `POST /settlements` **nie rozliczy** (`422 UNSUPPORTED_DOCUMENT_TYPE`). Dopasowuj i rozliczaj **tylko `doc_type` faktur sprzedaży** (zwykle `FS`; w niektórych bazach prefiks bywa inny, np. `FH`) — etykietę `doc_type` interpretujesz po swojej stronie.

**`GET /api/v1/invoices/open-payables`** — lustro `open-receivables` dla **zobowiązań**
(rozrachunki zakupu `nzf_Typ=40`, FZ). Te same parametry i kształt odpowiedzi
(`doc_type` = `FZ`). PLN-only. Kandydaci do dopasowania z WYPŁATĄ (przelew `direction=out`).

---

### 3.12 KSeF — wysyłka e-Faktury (**FS i KFS**)

**`POST /api/v1/invoices/{id}/ksef`** — wyślij e-Fakturę dokumentu do Krajowego Systemu e-Faktur.
Body puste. **BEZ `Idempotency-Key`** — endpoint jest naturalnie idempotentny: to „popchnięcie do
przodu" maszyny stanów KSeF w Subiekcie (sprawdź poprawność → wygeneruj e-Fakturę → wyślij →
dociągnij numer). Powtórny POST niczego nie wyśle drugi raz. Jeśli po `202` KSeF odrzucił dokument już po
stronie mostu (w tle), kolejny POST zwraca `422 KSEF_REJECTED` z powodem — dopiero POST po poprawieniu danych
w Subiekcie ponawia wysyłkę.

**`GET /api/v1/invoices/{id}/ksef`** — czysty odczyt stanu. **UWAGA:** dokument w statusie
`processing` NIE zaktualizuje się przez GET — numer KSeF dociąga się WYŁĄCZNIE ponownym POST-em.
Polling rób POST-em (bezpieczny), GET-a używaj do biernego podglądu.

Odpowiedź (POST 200/202 i GET 200):

```json
{
  "document_id": "sub_142877",
  "ksef_status": "registered",
  "ksef_number": "1111111111-20260812-0123456789AB-CD",
  "ksef_number_date": "2026-08-12",
  "message": null
}
```

`ksef_status`: `none` | `validated` | `generated` | `sending` | `processing` | `registered` |
`rejected` | `validation_failed` | `communication_error`.

Kody POST:

| HTTP | Kiedy | Co robisz |
|---|---|---|
| 200 | `registered` — faktura ma numer KSeF (także gdy była już wysłana wcześniej) | zapisz `ksef_number`, koniec |
| 202 | `sending`/`processing` — wysyłka w toku (limit czekania mostu ~90 s minął) | ponów POST po chwili (np. 30–60 s) |
| 404 `INVOICE_NOT_FOUND` | dokument nie istnieje | nie retry'uj |
| 422 `UNSUPPORTED_DOCUMENT_TYPE` | dokument to nie FS/KFS | nie retry'uj |
| 422 `NOT_KSEF_INVOICE` | faktura nie-KSeF (np. konsumencka, `FormaDokumentu`≠KSeF) | nie retry'uj |
| 422 `KSEF_VALIDATION_FAILED` | dane nie przechodzą schemy e-Faktury (`message` = powód ze Sfery) | popraw dane / eskaluj do operatora |
| 422 `KSEF_REJECTED` | KSeF odrzucił dokument (`message` = błąd KSeF) | eskaluj do operatora |
| 502 `KSEF_COMMUNICATION_ERROR` | problem komunikacji z KSeF / brak Konta InsERT | retry z backoff |
| 502 `KSEF_SEND_INCOMPLETE` | wysyłka nie osiągnęła stanu końcowego (operacja padła bez rejestracji) | retry: ponów POST |

**Nieodwracalność:** zarejestrowanej w KSeF faktury NIE DA SIĘ wycofać — tylko skorygować (KFS).
Decyzja „kiedy wysłać" jest po Twojej stronie. **Środowisko KSeF** (produkcyjne/testowe MF) wynika
z konfiguracji podmiotu w Subiekcie, nie z mostu — upewnij się przed pierwszą wysyłką.
Limit czekania mostu konfiguruje `Bridge:KsefSendTimeoutSeconds` (default 90 s).

---

## 4. Obsługa odpowiedzi i błędów (kontrakt — zaimplementuj 1:1)

Format błędu: `{ "code", "message", "details"?, "retry_after_seconds"? }`. Reguły:

- **2xx** → sukces.
- **4xx** → błąd danych po Twojej stronie → **NIE retry'uj**, popraw request / zgłoś operatorowi. **Jedyny wyjątek:** `409 RECEIPT_LOCKED`
  z `retry_after_seconds` (PATCH PZ; powód `LOCKED_BY_SESSION` / `CONCURRENT_MODIFICATION`) — ponów po odczekaniu.
- **5xx / 502 / 503** → ponawiaj (z backoffem, **tym samym `Idempotency-Key`**) **wyłącznie kody z listy retry**:
  - **„nic nie zapisano”**: `SUBIEKT_UNAVAILABLE`, `SUBIEKT_QUERY_FAILED`, `BRIDGE_DEGRADED`,
    `CONTRACTOR_LOOKUP_UNAVAILABLE`, `DUPLICATE_CHECK_UNAVAILABLE` (od v0.20.0), `HB_BOOKING_FAILED` — gwarancja mostu:
    **w tym żądaniu nic nie zostało zapisane w Subiekcie** (albo zapis został czysto cofnięty);
  - **KSeF**: `KSEF_COMMUNICATION_ERROR`, `KSEF_SEND_INCOMPLETE` — stan dokumentu w Subiekcie **mógł** się zmienić
    (np. e-Faktura wygenerowana, wysyłka bez stanu końcowego), ale ponowny `POST .../ksef` jest bezpieczny, bo to
    idempotentny „advance” maszyny stanów (§3.12), nie dlatego, że nic nie zapisano.

  Kody spoza listy
  (`HB_BOOKING_ORPHAN`, `INTERNAL_ERROR`) = wynik nieznany albo twardy błąd → **nie ponawiaj automatycznie**,
  eskaluj do operatora (na FS/KFS/PZ/MM ręczne ponowienie jest bezpieczne — chroni anti-duplicate `409`; na PATCH PZ ręczne
  ponowienie = trzy przypadki z §3.6b: cache hit → cached `200`, stare `expected_revision` → `409`, bez `expected_revision` → no-op
  **tylko gdy** bieżąca wartość równa żądanej, inaczej zapis).
- **`502 RECEIPT_UPDATE_UNVERIFIED`** (PATCH PZ, od v0.21.0) = wynik nieznany (most nie potwierdził stanu po zapisie) → **nie ponawiaj
  automatycznie**: zrób `GET`, porównaj `original_number`/`revision` i zdecyduj; `details.saved_probably` (`true`/`false`/`null`).
- **202 (tylko KSeF)** → wysyłka w toku — ponów `POST .../ksef` po 30–60 s. To NIE jest błąd.
- **409 `DUPLICATE_INVOICE` / `DUPLICATE_RECEIPT` / `DUPLICATE_TRANSFER`** → dokument z tym
  `external_reference` już istnieje → **auto-recovery**: pobierz `details.existing_subiekt_id` /
  `existing_bridge_id` i podbij swój rekord na „wystawione" (zamiast tworzyć nowy).
  To **nie** jest błąd do retry.
- **409 `DUPLICATE_SETTLEMENT`** (settlements) → ta operacja bankowa jest już rozliczona z tym
  rozrachunkiem → **auto-recovery**: pobierz `details.existing_rozliczenie_id` (`int | null`; `null` =
  rozliczenie istnieje, ale mostowi nie udało się odczytać jego numeru — do v0.18.0 `-1`) i oznacz płatność
  jako rozliczoną. To **nie** jest błąd do retry.

| HTTP | `code` | Co robisz |
|---|---|---|
| 400 | `MISSING_IDEMPOTENCY_KEY` | dodaj nagłówek `Idempotency-Key` |
| 400 | `MISSING_NIP` / `MISSING_EAN` | brak param. w lookupie |
| 422 | `TOTAL_MISMATCH` | `totals.gross` ≠ Σ(linie+wysyłka) — popraw kwoty (§7.1) |
| 422 | `MISSING_PRODUCT` | EAN nie istnieje w Subiekcie (`details.missing_eans`) — załóż towar / popraw EAN |
| 422 | `EMPTY_LINES` | pusta lista pozycji |
| 422 | `INVALID_CONTRACTOR_SYMBOL` | `contractor.symbol`/`supplier.symbol` pusty lub > 20 znaków, a kontrahenta nie dało się dopasować po NIP (`details.symbol`, `details.max_length`) — skróć symbol (§7.2) |
| 422 | `UNSUPPORTED_CURRENCY` | most wystawia wyłącznie PLN — `currency` musi być `"PLN"` |
| 422 | `UNSUPPORTED_VAT_RATE` | `vat_rate` ≠ 23 na wysyłce/pozycji usługowej (EAN=null) — usługi tylko 23%; pozycje towarowe biorą VAT z kartoteki |
| 422 | `INVALID_DATE` | `issue_date`/`sale_date`/`source_invoice_date` nie w formacie `YYYY-MM-DD` (lub data niemożliwa kalendarzowo) |
| 422 | `NOTES_TOO_LONG` | `notes` (FS/PZ/MM) lub `reason` (KFS) nie mieści się z doklejanym `ref: <external_reference>` w 500 znakach Uwag Subiekta (`details.max_length`) — skróć (§7.7) |
| 422 | `INVALID_CORRECTION` | (KFS) linia korekty bez pasującej pozycji FS, dwie linie na tę samą pozycję albo zwrot większy niż ilość na pozycji — popraw linie, nie retry |
| 422 | `INVALID_EXTERNAL_REFERENCE` | (FS/KFS/PZ/MM, od v0.20.0) `external_reference` pusty/biały albo dłuższy niż 495 znaków (`details.max_length`) — bez niego most nie może sprawdzić duplikatu, a dłuższy nie mieści się w Uwagach z `ref: `; sprawdzane PRZED `NOTES_TOO_LONG` — popraw, nie retry |
| 404 | `INVOICE_NOT_FOUND` / `RECEIPT_NOT_FOUND` | zły `{id}` (od v0.21.0 dla PZ: tylko gdy Subiekt pozytywnie potwierdził brak dokumentu) |
| 422 | `EMPTY_UPDATE` / `INVALID_ORIGINAL_NUMBER` / `ORIGINAL_NUMBER_TOO_LONG` | (PATCH PZ, od v0.21.0) brak pola / `null`, nie-string, pusty (`details.reason`) / > 30 znaków (`details.max_length`, `details.length`); `INVALID_ORIGINAL_NUMBER` może też przyjść PO zapisie, gdy Sfera odrzuci wartość (`details.source = "sfera"`, `details.hresult`) — stan bez zmian; nie retry |
| 409 | `RECEIPT_CANCELLED` | (PATCH PZ) dokument unieważniony w Subiekcie — nie retry |
| 409 | `RECEIPT_REVISION_MISMATCH` | (PATCH PZ) `expected_revision` ≠ bieżący `revision` (`details.current_revision`) — `GET` i decyzja |
| 409 | `RECEIPT_LOCKED` | (PATCH PZ) Sfera odmówiła edycji, stan bez zmian; `details.reason` ∈ {`LOCKED_BY_SESSION` (otwarty w GUI), `CONCURRENT_MODIFICATION`, `PERMISSION_DENIED`, `PERIOD_LOCKED`, `BOOKED`, `NOT_EDITABLE` (np. dokument powiązany), `NOT_SAVED`}, `details.hresult`, `details.sfera_message`, `details.phase`, `details.sfera_can_edit`, `details.linked_documents`; `retry_after_seconds` tylko dla dwóch pierwszych powodów |
| 500 | `RECEIPT_UPDATE_FAILED` | (PATCH PZ) zapis padł z niesklasyfikowanym błędem, odczyt kontrolny potwierdza stan sprzed (`details.hresult`, `details.sfera_message`, `details.phase`) — nie retry automatycznie; ręcznie bezpiecznie |
| 502 | `RECEIPT_UPDATE_UNVERIFIED` | (PATCH PZ) wynik nieznany po zapisie (sesja zresetowana, zamknięcie niepotwierdzone, odczyt kontrolny padł, stan rozjechany, żądanie anulowane) — **`GET`, nie retry automatyczny**; `details.saved_probably` |
| 404 | `SETTLEMENT_NOT_FOUND` | (DELETE) rozliczenie nie istnieje / już cofnięte |
| 501 | `HB_BOOKING_NOT_SUPPORTED` | (book) księgowanie wyłączone serwerowo (`EnableHbBooking=false`) — księguj w module Bankowość, potem `/settlements` |
| 422 | `UNSUPPORTED_FOREIGN_ACCOUNT` / `UNSUPPORTED_HB_STATUS` | (book) rachunek wyciągu nie-PLN lub linia w nietypowym `hb_Status` — nie retry |
| 404 | `BANK_TRANSACTION_NOT_FOUND` | (book) zły `hb_id` |
| 422 | `NO_BANK_ACCOUNT` / `INVALID_DIRECTION` | (book) linia bez konta wyciągu lub `hb_Oznaczenie` ∉ {C,D} — nie retry |
| 500 | `HB_BOOKING_FAILED` | (book) **wyłącznie** gdy nic nie zapisano albo operacja została czysto cofnięta (odrzucenie przez Sferę przed/na zapisie, nieudany link z udanym rollbackiem) — **bezpieczny retry** tym samym kluczem |
| 500 | `HB_BOOKING_ORPHAN` | (book) stan niespójny: operacja powstała (lub mogła powstać) bez linku — **NIE retry** (drugi BP), zgłoś do ręcznego usunięcia w Subiekcie; `details.bank_operation_subiekt_id` (`int \| null`, `null` = wyjątek z/po zapisie, id nieodczytane) + `details.hb_id` |
| 422 | `INVALID_BRIDGE_ID` | `{id}` nie w formacie `sub_<n>` albo `n` poza zakresem `1 ≤ n ≤ 2147483647` (od v0.21.0 na wszystkich endpointach z `{id}`; identyfikatory Subiekta są 32-bitowe — wcześniej takie id dawały `404` albo błąd Subiekta) |
| 422 | `SETTLEMENT_NOT_SUPPORTED` | dokument bez rozrachunku (goły PZ/MM) lub rozrachunek na centrum kart/rat — nie retry |
| 422 | `UNSUPPORTED_DOCUMENT_TYPE` | settlements obsługują tylko FS/FZ; korekty (KFS/KFZ) i inne typy odrzucane; PATCH `/receipts/{id}` na dokumencie, który nie jest PZ (`details.document_type`) — nie retry |
| 422 | `ALREADY_SETTLED` | rozrachunek już rozliczony (np. faktura gotówkowa) — nie retry. Retry **tego samego** przelewu po pełnym rozliczeniu daje `409 DUPLICATE_SETTLEMENT` (auto-recovery), nie 422 |
| 422 | `UNSUPPORTED_BANK_OPERATION_TYPE` | `bank_operation_subiekt_id` nie wskazuje operacji bankowej BP/BW (np. podano `hb_id` zamiast `bank_operation_subiekt_id`, albo id KP/KW) — popraw id |
| 422 | `INVALID_HB_AMOUNT` | (book) linia wyciągu ma pustą/zerową kwotę w Subiekcie — nie księguj |
| 422 | `INVALID_AMOUNT` / `AMOUNT_EXCEEDS_REMAINING` | `amount` ≤ 0 lub > pozostało do zapłaty |
| 422 | `BANK_OPERATION_NOT_FOUND` / `BANK_OPERATION_EXHAUSTED` / `BANK_OPERATION_CONTRACTOR_MISMATCH` | zła/skonsumowana operacja bankowa lub inny kontrahent niż rozrachunek |
| 422 | `NOT_KSEF_INVOICE` | (ksef) faktura nie-KSeF (np. konsumencka) — nie retry |
| 422 | `KSEF_VALIDATION_FAILED` | (ksef) dane nie przechodzą schemy e-Faktury (`message` = powód) — popraw dane / operator |
| 422 | `KSEF_REJECTED` | (ksef) KSeF odrzucił dokument — eskaluj do operatora, nie retry |
| 502 | `KSEF_COMMUNICATION_ERROR` | (ksef) komunikacja z KSeF / brak Konta InsERT — **retry z backoff** |
| 502 | `KSEF_SEND_INCOMPLETE` | (ksef) wysyłka bez stanu końcowego — **retry: ponów POST** |
| **409** | **`DUPLICATE_INVOICE` / `DUPLICATE_RECEIPT` / `DUPLICATE_TRANSFER` / `DUPLICATE_SETTLEMENT`** | **auto-recovery** (patrz wyżej) |
| 501 | `NOT_IMPLEMENTED` | operacja nieobsługiwana — zgłoś, nie retry |
| 502 | `SUBIEKT_QUERY_FAILED` / `BRIDGE_DEGRADED` | Subiekt nie odpowiada — **retry** |
| 503 | (health) | sesja Sfery martwa — **retry / circuit-breaker** |
| 503 | `CONTRACTOR_LOOKUP_UNAVAILABLE` | (FS/PZ z NIP-em) most nie mógł sprawdzić kontrahenta po NIP w bazie — dokument NIE powstał, **retry z backoff** (`details.nip`) |
| 503 | `DUPLICATE_CHECK_UNAVAILABLE` | (FS/KFS/PZ/MM, od v0.20.0) most nie mógł sprawdzić w Subiekcie, czy dokument z tym `external_reference` już istnieje (baza, przekroczony czas skanu albo weryfikacja dokumentu) — dokument NIE powstał w tym żądaniu, **retry z backoff tym samym kluczem** (`details.external_reference`, `details.document_type` ∈ {`FS`,`KFS`,`PZ`,`MM`}). Do v0.19.x awaria skanu była fail-open (dokument powstawał) |
| 503 | `SUBIEKT_UNAVAILABLE` | Subiekt/Sfera offline: przy odczycie (`GET /invoices/{id}`, weryfikacja replay) albo **przed pierwszym zapisem** mutacji (FS/KFS/PZ/MM, settlements, book — most sonduje sesję na starcie) — **nic nie zapisano, retry z backoff tym samym kluczem**; klucz idempotencji zostaje. Do v0.18.0 kod nazywał się `SFERA_UNAVAILABLE` i dotyczył tylko odczytów |
| 500 | `INTERNAL_ERROR` | nieoczekiwany błąd (`details.stack`) — **nie ponawiaj automatycznie** (kod spoza listy retry) + alert. FS/KFS/PZ/MM: ręczne ponowienie bezpieczne (anti-duplicate `409`). PATCH PZ: `details.state = "saved"` = zmiana ZAPISANA, padł tylko cache idempotencji — ponowienie to cache miss, czyli przypadki 2–3 z §3.6b (bez `expected_revision` i ta sama wartość → `200` no-op; stare `expected_revision` → `409`). book: od v0.19.0 nigdy nie oznacza niepowiązanej operacji — pojawia się tylko **przed** utworzeniem BP albo po **domkniętym** linku (np. błąd zapisu cache idempotencji; ponowienie zwraca `already_booked`), błędy po zapisie BP idą jako `HB_BOOKING_ORPHAN` |

`DUPLICATE_INVOICE.details`:
```json
{
  "existing_subiekt_id": 142877,
  "existing_number": "FS 123/2026",
  "existing_bridge_id": "sub_142877",
  "external_reference": "nowysystem:order:ABC123"
}
```

`DUPLICATE_CHECK_UNAVAILABLE.details` (FS/KFS/PZ/MM, od v0.20.0; klient może je ignorować — liczy się kod):
```json
{ "external_reference": "nowysystem:order:ABC123", "document_type": "FS" }
```

`INVALID_EXTERNAL_REFERENCE.details`: `{ "max_length": 495 }`.

`HB_BOOKING_ORPHAN.details` (book):
```json
{ "bank_operation_subiekt_id": 73291, "hb_id": 13128 }
```
`bank_operation_subiekt_id` = `null`, gdy operacja mogła powstać, ale mostowi nie udało się odczytać jej id
(wyjątek z/po zapisie) — sprawdź ręcznie w module Bankowość po kwocie/dacie/tytule linii `hb_id`.

---

## 5. Idempotency — Twoja odpowiedzialność jako klienta

Most ma dwie warstwy zabezpieczeń; obie zależą od tego, **co Ty wyślesz**:

1. **`Idempotency-Key` (header)** — most cache'uje `(operacja, klucz) → response` (TTL 14 dni); operacja = metoda + ścieżka
   z id dokumentu (od v0.21.0, §2). Ten sam klucz przy retry tej samej operacji = ten sam wynik, bez dublowania. **Automatyczne ponowienia mieść w 14 dniach**
   od pierwszej próby — później cache już nie odpowie, a ponowienie trafi w warstwę 2. Od v0.20.0 warstwa 2 jest
   **fail-closed**: albo `409 DUPLICATE_*` (dokument istnieje), albo `201` (nie istniał), albo `503 DUPLICATE_CHECK_UNAVAILABLE`
   (most nie mógł sprawdzić — nic nie zapisano, ponów); nigdy duplikat przez niesprawdzony skan. Do v0.19.x błąd skanu
   nie blokował zapisu (fail-open) i skan widział tylko magazyn roboczy sesji. **Generuj jeden stabilny klucz na (zamówienie × typ)**
   i powtarzaj go przy ponowieniu. (Most dodatkowo weryfikuje, że dokument z cache nadal istnieje w Subiekcie —
   jeśli ktoś go skasował, potraktuje request jako nowy.)

2. **Anty-duplikat w Subiekcie po `external_reference`** — przed utworzeniem most szuka
   dokumentu z tym samym `external_reference` (w polu uwag, **we wszystkich magazynach** — od v0.20.0; wcześniej tylko
   w magazynie roboczym sesji Sfery). Trafienie → `409`; awaria skanu → `503 DUPLICATE_CHECK_UNAVAILABLE` (nic nie
   zapisano, ponów tym samym kluczem). `external_reference` musi być niepusty i mieć ≤ 495 znaków (`422 INVALID_EXTERNAL_REFERENCE`).
   Most **sam dokleja** `| ref: <external_reference>` do uwag dokumentu (FS/KFS/PZ/MM),
   jeśli nie umieściłeś referencji w `notes` — nie musisz (ale możesz) robić tego sam.

> ⚠️ **Jedyna realna pułapka przy współdzielonym Subiekcie.**
> Subiekt jest **wspólny** z istniejącym klientem Laravel. Anty-duplikat działa po dopasowaniu
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
`contractor.symbol`: **max 20 znaków** (kolumna `kh_Symbol varchar(20)`), zalecane `[A-Za-z0-9-_]`.
Email z `@`/`+`, UUID Allegro itp. przekraczają limit. Gdy kontrahenta nie da się dopasować po NIP,
za długi symbol kończy się `422 INVALID_CONTRACTOR_SYMBOL` (nie retry). `original_number` (PZ): max 30 znaków.
**Sanityzuj/przycinaj te pola u siebie** przed wysłaniem.

### 7.3 Korekta = ujemna ilość pod kluczem `quantity`
Patrz §3.2 — nie wymyślaj osobnego pola, ujemna wartość w `quantity`. Każda linia korekty trafia w **osobną**
pozycję FS (FS z dwiema pozycjami tego samego EAN → dwie linie korekty); zwrot większy niż ilość na pozycji →
`422 INVALID_CORRECTION`.

### 7.7 Notatki: `dok_Uwagi` ma 500 znaków
Most dokleja do `notes`/`reason` ` | ref: <external_reference>` (na tym stoi ochrona przed duplikatami).
Limit Subiekta to 500 znaków łącznie → `422 NOTES_TOO_LONG`, gdy się nie mieści. Trzymaj `notes` krótkie
(≤ ~450 znaków przy typowym `external_reference`). Sam `external_reference` ma twardy limit **495** znaków
(`422 INVALID_EXTERNAL_REFERENCE`, od v0.20.0) — to granica dla dokumentów BEZ notatek; z notatkami obowiązuje suma 500,
a KFS zawsze niesie prefiks `Korekta: <reason>`, więc przy `external_reference` ≥ ~485 znaków korekta nie przejdzie.

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
  `503` = Subiekt/Sfera offline, wstrzymaj wysyłkę (circuit-breaker). Od v0.19.0 health **sonduje** sesję
  zapytaniem do bazy (nie tylko sprawdza, czy obiekt sesji istnieje) — zamknięty przez operatora Subiekt daje `503`
  od razu, nie dopiero przy pierwszym padniętym żądaniu.
  `sql_connection` (`"ok"`/`"down"`) = osobne połączenie mostu do bazy (`/bank-transactions`, `/book`,
  filtr `nip`, `search` w open-receivables/payables, dopasowanie kontrahenta po NIP przy wystawianiu FS).
  `200` + `status: "degraded"` + `sql_connection: "down"` = sesja Sfery działa, ale zapytania SQL mostu
  nie — przyczyna w `sql_error` (od v0.21.2 także `revision: null` w `GET /invoices` i `/invoices/{id}`, §3.8). Dokumenty dla kontrahenta z NIP-em (firmy) dostaną wtedy
  `503 CONTRACTOR_LOOKUP_UNAVAILABLE` (most nie zakłada kontrahenta na ślepo) — ponów z backoffem;
  osoby prywatne (bez NIP) wystawiają się normalnie.
- **Pełne, autorytatywne DTO:** `src/SubiektBridge.Api/Models/InvoiceModels.cs`.
- **Dokładna logika statusów/błędów:** `src/SubiektBridge.Api/Controllers/*.cs`.
- **Referencyjny istniejący klient** (aplikacja Laravel): klasy
  `SubiektBridgeClient`, `{InvoiceIssuer,ReceiptIssuer,InvoiceCorrectionIssuer}`,
  `{IssueInvoiceJob,IssueCorrectionJob,IssueReceiptJob}` — wzorzec retry/409/idempotency 1:1.

---

## 9. Historia zmian kontraktu

Tylko zmiany widoczne dla klienta (nowe pola, kody, zmienione zachowanie). Pełne opisy wydań: GitHub Releases.
Kody 4xx/5xx obsługujesz wg §4 — nowe kody wpadają w te same reguły (4xx = nie retry, 5xx = retry tylko z listy retry).

### v0.21.2 (2026-10-13 — rewizja FS/KFS)
- **`GET /api/v1/invoices/{id}` i `GET /api/v1/invoices`: nowe pole `revision`** (`f1:` + 16 hex albo `null`) — znacznik treści
  wydruku FS/KFS do odświeżania przechowywanego PDF (§3.8: format, kiedy `null`, kolejność „rewizja przed PDF”, co ją zmienia).
  Dotychczasowe pola bez zmian. Bez nowych kodów błędów: awaria odczytu rewizji = `revision: null` w odpowiedzi `200`.

### v0.21.1 (2026-10-12 — kwoty PZ w odpowiedzi `POST /receipts`)
- **`POST /api/v1/receipts`: `totals.net` i `totals.vat` wypełnione** kwotami odczytanymi z zapisanego PZ (§3.6) — do v0.21.0
  zawsze `null`, więc klient nie znał netto/VAT przyjęcia bez osobnego `GET`. Wartości = `net_amount` / `vat_amount` /
  `gross_amount` z `GET /receipts/{id}` (wspólny odczyt). `null` już tylko, gdy Subiekt nie udostępni kwoty (bez zastępczego `0`).
- `totals.gross` bez zmian (z dokumentu; z linii tylko, gdy Subiekt nie poda brutto). FS/KFS bez zmian — ich `totals.net`/`vat`
  były wypełnione już wcześniej.

### v0.21.0 (2026-10-11 — edycja PZ: nagłówek)
- **`GET /api/v1/receipts/{id}`** zwraca rozszerzony `ReceiptDetailDto` (§3.6b): dotychczasowe pola bez zmian + `original_number`,
  `external_reference` (pełna referencja zapisana przez most, §3.6b), `warehouse_id`, `status`, `booking_status`, `period_locked`, `linked_documents` (FZ/KFS/inny), `editability`
  (fakty), `revision`, `items[]` (pozycje read-only z `issued_*`), `warnings`. `404` tylko przy pozytywnym braku dokumentu; awaria odczytu
  → `502 SUBIEKT_QUERY_FAILED`.
- **Nowy `PATCH /api/v1/receipts/{id}`** `{ original_number, expected_revision? }` z `Idempotency-Key`: wpisanie numeru dokumentu dostawcy
  (np. faktury z KSeF) do istniejącego PZ. `200` = stan po odczycie kontrolnym po zapisie; ta sama wartość = no-op.
- **Nowe kody:** `422 EMPTY_UPDATE` / `INVALID_ORIGINAL_NUMBER` / `ORIGINAL_NUMBER_TOO_LONG`; `409 RECEIPT_CANCELLED` /
  `RECEIPT_REVISION_MISMATCH` / `RECEIPT_LOCKED` (jedyny 4xx z `retry_after_seconds`); `500 RECEIPT_UPDATE_FAILED`;
  `502 RECEIPT_UPDATE_UNVERIFIED` (wynik nieznany → `GET`, bez retry automatycznego). `UNSUPPORTED_DOCUMENT_TYPE` także dla PATCH na nie-PZ.
- **`422 INVALID_BRIDGE_ID` także dla `sub_0`, ujemnych i `n > 2147483647`** na wszystkich endpointach z `{id}` (invoices, receipts,
  settlements, ksef) — identyfikatory Subiekta są 32-bitowe; wcześniej takie id kończyły się `404` albo błędem Subiekta.
- **`Idempotency-Key` ważny per operacja i dokument** (§2, §5): cache trzyma `(metoda + ścieżka z id, klucz)`. Ten sam klucz na innym
  endpoincie albo innym dokumencie wykonuje żądanie zamiast zwracać zapisaną odpowiedź innej operacji (do v0.20.x: np. `POST /receipts`
  kluczem z `POST /invoices` dostawał FS jako „PZ”, korekta faktury 2 kluczem korekty faktury 1 dostawała KFS faktury 1). Zgodny klient
  (stały klucz na operację, ten sam przy retry) — bez zmian. Wpisy sprzed 0.21.0 czytane przez POST-y do 14 dni po wdrożeniu (do
  usunięcia w 0.22.0); `PATCH` ich nie czyta.
- `POST /invoices/{id}/corrections`: `422 INVALID_BRIDGE_ID` sprawdzany przed replayem (jak `/settlements`).
- Edycja pozycji PZ (`PUT …/items`) — planowana w v0.22.0.

### v0.20.0 (2026-10-10 — backlog 138)
- **Nowy kod `503 DUPLICATE_CHECK_UNAVAILABLE`** (FS/KFS/PZ/MM): most nie mógł sprawdzić w Subiekcie, czy dokument z tym
  `external_reference` już istnieje — dokument NIE powstał w tym żądaniu, **retry z backoffem tym samym kluczem**
  (`details.external_reference`, `details.document_type`). Kod wchodzi na listę retry „nic nie zapisano” (§4).
- **Nowy kod `422 INVALID_EXTERNAL_REFERENCE`** (FS/KFS/PZ/MM): `external_reference` pusty/biały albo > 495 znaków
  (`details.max_length = 495`); sprawdzany PRZED `NOTES_TOO_LONG` — nie retry (§7.7).
- **Zmiana zachowania anty-duplikatu (§5):** skan jest **fail-closed** (do v0.19.x awaria skanu = „brak duplikatu” = dokument
  wystawiany) i obejmuje dokumenty ze **wszystkich magazynów** (do v0.19.x tylko z magazynu roboczego sesji Sfery — dokument
  wystawiony wcześniej na innym magazynie nie był wykrywany). Dokument unieważniony w Subiekcie nie jest duplikatem (jak dotąd).
- `/book`, `/settlements`, KSeF: bez zmian.

### v0.19.1 (2026-10-04)
- **TTL cache `Idempotency-Key` skrócony z 30 do 14 dni** (§5), wygasłe wpisy most teraz kasuje (wcześniej tylko
  ignorował). Ponowienie tym samym kluczem po 14 dniach nie dostanie replayu — trafi w anti-duplicate, który
  jest fail-open (`409 DUPLICATE_*` tylko przy udanym wykryciu; późne ponowienie poprzedź sprawdzeniem, §5).

### v0.19.0 (2026-09-29)
- **Zasada „kod = dowód o skutku”** (§4): kody z listy retry (`SUBIEKT_UNAVAILABLE`, `SUBIEKT_QUERY_FAILED`,
  `HB_BOOKING_FAILED`, …) most zwraca **wyłącznie**, gdy w tym żądaniu nic nie zapisano (albo zapis czysto cofnięto).
- **`503 SUBIEKT_UNAVAILABLE`** (zmiana nazwy z `SFERA_UNAVAILABLE` z v0.18.0) — teraz także na **każdej mutacji**
  (FS/KFS/PZ/MM, settlements, book), gdy Subiekt jest offline przed pierwszym zapisem; klucz idempotencji zostaje,
  retry tym samym kluczem → `201`.
- **book:** wyjątek z/po zapisie operacji → zawsze `500 HB_BOOKING_ORPHAN` (wcześniej mógł wyjść `HB_BOOKING_FAILED`
  lub `INTERNAL_ERROR` = klient ponawiał = drugi BP). `HB_BOOKING_ORPHAN` ma `details.bank_operation_subiekt_id`
  (`int | null`) i `details.hb_id`. `INTERNAL_ERROR` na book nigdy nie oznacza niepowiązanej operacji (tylko przed
  utworzeniem BP albo po domkniętym linku — wtedy ponowienie daje `already_booked`).
- **KFS:** `contractor_subiekt_id` = kontrahent FS źródłowej (wcześniej zawsze `0`; `0` nadal = nieznany).
- **`409 DUPLICATE_SETTLEMENT`:** `existing_rozliczenie_id` = `null` zamiast `-1`, gdy numeru nie odczytano.
- **health:** sesja Sfery sondowana zapytaniem do bazy — `503` natychmiast po zamknięciu Subiekta.

### v0.18.0 (2026-09-29)
- **Nowe kody:** `422 NOTES_TOO_LONG` (§7.7), `422 INVALID_CORRECTION` (§7.3), `422 UNSUPPORTED_BANK_OPERATION_TYPE`,
  `422 INVALID_HB_AMOUNT` (book), `503 SFERA_UNAVAILABLE` (od v0.19.0 `SUBIEKT_UNAVAILABLE`) (retry; klucz idempotencji zostaje).
- **Rozliczenia:** retry tego samego przelewu po pełnym rozliczeniu → `409 DUPLICATE_SETTLEMENT` z
  `existing_rozliczenie_id` (wcześniej `422 ALREADY_SETTLED` / `BANK_OPERATION_EXHAUSTED`). `GET .../settlements`:
  `settlements` ze **wszystkich** rozrachunków dokumentu (FS marketplace ma dwa), nagłówek z otwartego / najnowszego.
- **`GET /bank-transactions`:** nowe pole `hb_status`; `unbooked_only` pomija linie pominięte przez operatora
  (`hb_status`=3); `amount` = 0, gdy kwota w Subiekcie jest pusta.
- **`GET /products`:** `vat_rate` i `unit` z kartoteki Subiekta (do v0.17.2 zawsze `23` / `"szt."`).
- **`GET /contractors` / `contractor.email`:** `email` wypełniony i zapisywany w Subiekcie (do v0.17.2 e-mail był
  ignorowany, `email` zawsze `null`).
- **Anti-duplicate po `external_reference`:** dopasowanie całego tokenu — ref `sys:order:12` nie pasuje już do
  `sys:order:123` (wcześniej możliwe `409` z **cudzym** `existing_subiekt_id`); działa niezależnie od symbolu numeracji.
- **KSeF:** po `202` kolejny POST może zwrócić `422 KSEF_REJECTED`, gdy KSeF odrzucił dokument już po stronie mostu
  (wcześniej taki POST wysyłał dokument ponownie).
- **Pad sesji Sfery** przy `GET /invoices/{id}` i przy weryfikacji replay → `503 SFERA_UNAVAILABLE` (od v0.19.0 `SUBIEKT_UNAVAILABLE`) zamiast `404`.

### v0.17.2 (2026-09-29)
- Dopasowanie kontrahenta po NIP przy FS/PZ porównuje NIP bez kresek i spacji (kartoteka `111-111-11-11` = `1111111111`).

### v0.17.1 (2026-09-29)
- `503 CONTRACTOR_LOOKUP_UNAVAILABLE`: gdy most nie może sprawdzić kontrahenta po NIP (baza), dokument NIE powstaje
  (wcześniej po cichu zakładał kontrahenta po `symbol` — ryzyko duplikatu w kartotece). Retry z backoff.

### v0.17.0 (2026-09-29)
- `GET /invoices?nip=` działa (wcześniej każde użycie → `502`); porównanie bez kresek/spacji, brak kontrahenta → `[]`.
- `GET /health`: nowe pola `sql_connection` (`ok`/`down`) i `sql_error`; `status: "degraded"` z `200`, gdy baza nie
  odpowiada, a sesja Sfery działa.
- `422 INVALID_CONTRACTOR_SYMBOL` zamiast `500` dla `symbol` > 20 znaków (limit to 20, nie 16 jak wcześniej w §7.2).
- `quantity` (FS/KFS/PZ/MM) przyjmuje ułamki (`decimal`); wartości całkowite bez zmian.

### v0.16.1 – v0.16.2 (2026-09-29)
- Bez zmian API (aktualizacja zależności, dokumentacja).

---

### TL;DR dla agenta-integratora
1. Budujesz **klienta**, nie most. Most i Subiekt już działają.
2. Na każdy request `X-Bridge-Token`; na każdy POST **stabilny `Idempotency-Key`** (powtarzany przy retry).
3. **Prefiksuj `external_reference`** identyfikatorem nowego systemu — to jedyne, co chroni przed kolizją w współdzielonym Subiekcie.
4. Obsłuż statusy wg §4: 4xx=nie retry, 5xx=retry **tylko kody z listy retry** (tym samym kluczem), **409=auto-recovery (nie błąd)**.
5. Pre-waliduj u siebie: totals (§7.1), limit symbolu/numeru (§7.2), ujemne `quantity` w korekcie (§7.3), `is_settled` przy odroczonych (§7.4).
