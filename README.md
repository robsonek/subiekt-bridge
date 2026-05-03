# SubiektBridge

[![Build](https://github.com/robsonek/subiekt-bridge/actions/workflows/build.yml/badge.svg)](https://github.com/robsonek/subiekt-bridge/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/robsonek/subiekt-bridge)](https://github.com/robsonek/subiekt-bridge/releases/latest)

Most HTTP→COM/Sfera dla integracji **Subiekt GT** z aplikacjami Linux/macOS (np. Laravelowy
[marketplace-manage](https://github.com/robsonek/marketplace-manage)). Wystawia faktury sprzedaży
i korygujące przez oficjalne API Sfery, działa jako Windows Service obok Subiekta.

## Architektura

```
Twoja aplikacja  ── HTTPS + X-Bridge-Token ──►  SubiektBridge.Api
(Linux/macOS)         JSON REST                  (Windows .NET 10)
                                                       │
                                                       ▼ COM/OLE Automation
                                              Subiekt GT + Sfera + MSSQL
```

REST API (5 endpointów + escape hatch):

| Endpoint | Funkcja |
|---|---|
| `POST /api/v1/invoices` | Wystaw FV sprzedaży (z `Idempotency-Key`) |
| `POST /api/v1/invoices/{id}/corrections` | Wystaw FV korygującą (KFS) |
| `GET /api/v1/products?ean=...` | Sprawdź czy towar istnieje w Subiekcie |
| `GET /api/v1/contractors?nip=...` | Sprawdź czy kontrahent istnieje |
| `GET /api/v1/health` | Diagnostyka (publiczny, bez tokena) |
| `POST /api/v1/sfera/raw` | Escape hatch z whitelistą metod |

Pełen kontrakt: zobacz `src/SubiektBridge.Api/Models/InvoiceModels.cs`.

## Wymagania

- **Subiekt GT** (cała linia "GT" jest 32-bit) z wykupioną **Sferą**
- Windows 10/11/Server 2016+ (działa też na 32- i 64-bitowym Windowsie — Bridge to x86, na 64-bit jedzie przez WOW64)
- **Brak zewnętrznych narzędzi** — rejestracja jako Windows Service przez wbudowane `sc.exe`
- **.NET Runtime** — opcjonalny (zobacz "Wybór wariantu" niżej)

## Wybór wariantu

Subiekt GT jest tylko 32-bit, więc Bridge musi być **win-x86** (in-process COM = bit-level musi pasować). Każdy release ma dwa pliki ZIP:

| Wariant | Rozmiar (ZIP / rozpakowane) | Wymagania na Windowsie |
|---|---|---|
| **`SubiektBridge-X.Y.Z-win-x86.zip`** (self-contained) | ~45 MB / ~100 MB | Nic — runtime wbudowany w binarki |
| **`SubiektBridge-X.Y.Z-win-x86-fxdep.zip`** (framework-dependent) | ~1.5 MB / ~3 MB | [ASP.NET Core Runtime 10 (x86)](https://dotnet.microsoft.com/download/dotnet/10.0) |

**Niepewny? Bierz self-contained** — kosztuje 100 MB na dysku, ale eliminuje całą klasę problemów ("brakuje runtime", "zła wersja .NET", konflikt z innymi appkami na hoście).

**Masz już zainstalowane .NET 10 SDK lub ASP.NET Core Runtime x86?** Bierz fxdep.

## Szybki start (produkcja)

1. **Pobierz** najnowszy ZIP z [Releases](https://github.com/robsonek/subiekt-bridge/releases/latest)
   (zobacz "Wybór wariantu" wyżej).
2. **Rozpakuj** do `C:\SubiektBridge\`
3. **Skopiuj** `appsettings.Production.json.template` jako `appsettings.Production.json` i wypełnij
   (token, login operatora Subiekta, ścieżki MSSQL).
4. **Zainstaluj** jako Windows Service (PowerShell jako Admin):
   ```powershell
   cd C:\SubiektBridge\
   .\install-windows.ps1 -LaravelHostIp 1.2.3.4
   ```
5. **Test**:
   ```bash
   curl -k https://WIN-HOST:8443/api/v1/health
   # {"status":"ok","subiekt_version":"1.78.0",...}
   ```

Pełny playbook: [`deploy/README.md`](./deploy/README.md).

## Update na produkcji

Pobierz nowy ZIP z Releases, rozpakuj do tymczasowego folderu, w PowerShell jako Admin:

```powershell
cd C:\SubiektBridge\new\
.\update-windows.ps1
```

Skrypt: stop service → kopiuje nowe pliki (zachowując `appsettings.Production.json` i `data/`) → start → health check.

## Dev (build lokalny)

Wymaga .NET SDK 10+:

```bash
git clone https://github.com/robsonek/subiekt-bridge.git
cd subiekt-bridge

# Build i run lokalnie z mock Sfera (działa na macOS/Linux/Windows):
dotnet run --project src/SubiektBridge.Api
# https://localhost:8443/api/v1/health
# Włączane przez Bridge:UseFakeSfera=true w appsettings.Development.json

# Build pod Windows (self-contained):
./scripts/publish-win.sh
# Output: publish/win-x64/SubiektBridge.Api.exe (~108 MB)
```

## Wydanie nowej wersji

```bash
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions auto-buduje `SubiektBridge-1.0.1-win-x64.zip` + SHA256 i publikuje w GitHub Release.

## Konfiguracja

Wszystkie klucze w `appsettings.Production.json`:

| Klucz | Opis |
|---|---|
| `Bridge.Token` | Statyczny API key (X-Bridge-Token). Wygeneruj losowo, NIE commituj |
| `Bridge.UseFakeSfera` | `true` — mock dla dev. `false` na produkcji |
| `Bridge.AllowedRawSferaMethods` | Whitelist dla `/sfera/raw`. Domyślnie pusta |
| `Bridge.IdempotencyStorePath` | Ścieżka SQLite cache idempotency-keys |
| `Subiekt.Server` | MSSQL instance (np. `.\SQLEXPRESS` dla lokalnej) |
| `Subiekt.Database` | Nazwa bazy Subiekta (sprawdź w Subiekt → Plik → Otwórz podmiot) |
| `Subiekt.DbUser/DbPassword` | SQL Auth do MSSQL |
| `Subiekt.Operator/OperatorPassword` | Login operatora Subiekta + hasło **plaintext** (Bridge szyfruje przez `InsERT.Dodatki.Szyfruj()`) |
| `Subiekt.PdfTemplateId` | `null` = domyślny wzorzec, lub int = custom |

## Bezpieczeństwo

- **HTTPS** — Kestrel z self-signed cert generowanym przy pierwszym `dotnet run`
  (na produkcji warto wystawić Let's Encrypt jeśli host ma publiczny DNS)
- **Token** w nagłówku `X-Bridge-Token` z constant-time compare (no timing attacks)
- **IP whitelist** w Windows Firewall (regułę dorzuca `install-windows.ps1`)
- **Idempotency** — powtórzony POST z tym samym `Idempotency-Key` zwraca cached response,
  zapobiega duplikatom FV przy retry
- **Single-thread COM** — wszystkie wywołania Sfery serializowane przez globalny `lock`
  (MVP — wystarcza dla typowego ruchu kilka FV/min)

## Ograniczenia

- Tylko Windows ma realny COM — na macOS/Linux działa tylko `FakeSferaSession`
- Polskie znaki w Sferze: encoding Windows-1250 (zarejestrowany przez `CodePagesEncodingProvider`)
- Bit-level binarki MUSI pasować do Subiekta. Domyślnie x64; dla x86 zbuduj `-r win-x86`

## Licencja

[MIT](./LICENSE)
