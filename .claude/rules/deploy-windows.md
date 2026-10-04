---
paths:
  - "deploy/**"
  - "scripts/**"
  - ".github/workflows/**"
  - "src/SubiektBridge.Api/Program.cs"
  - "src/SubiektBridge.Api/Idempotency/**"
  - "src/SubiektBridge.Api/Controllers/AdminController.cs"
---

# Deploy na Windowsie klienta — pułapki

## Skrypty PowerShell: ASCII-only, PS 5.x

PowerShell 5.x na Windowsie klienta nie czyta UTF-8 bez BOM jako UTF-8 -
interpretuje jako Windows-1252 i polskie znaki łamią parser. Wszystkie skrypty
w `deploy/` muszą być **7-bit ASCII clean**:

```bash
grep -nP "[\x80-\xff]" deploy/*.ps1   # powinno być puste
```

PowerShell 5.x NIE obsługuje też `?.` (null-conditional) ani `??` (null coalescing) — PS 7+.
Używaj klasycznego `if-else`.

Default PS 5.x to TLS 1.0/1.1 - GitHub wymaga 1.2+. Każdy skrypt zaczyna od:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
```

## Native Windows Service przez `sc.exe`

`Microsoft.Extensions.Hosting.WindowsServices` + `builder.Host.UseWindowsService()` -
no-op gdy nie running as service. Ta sama binarka działa interaktywnie i jako service.
Zero NSSM (NSSM 2.24 z 2014).

## HTTPS cert auto-gen

Kestrel default chce `dotnet dev-certs https` którego nie ma na świeżym Windows
Server 2016+. `Program.cs::EnsureSelfSignedCertificate` (przed `builder.Build()`)
generuje 2048-bit RSA cert do `data/cert.pfx` jeśli nie istnieje. SAN: hostname +
localhost + 127.0.0.1 + ::1. Klient (Laravel) używa `verify=false`.

## Auto-create folderu dla SQLite

`IdempotencyStore` ctor robi `Directory.CreateDirectory(Path.GetDirectoryName(path))`
przed `conn.Open()` — SQLite tworzy plik bazy auto, ale **nie folder rodzica**.
Bez tego Error 14 "unable to open database file".

## Retencja `idempotency.db`

Każda FS/KFS zapisuje w cache odpowiedź z pełnym `pdf_base64` (dziesiątki KB). Do v0.19.0 TTL działał tylko przy
odczycie — plik rósł bez końca (~400 MB po 5 miesiącach; pełny dysk hosta = `SQLite Error 13: database or disk is
full`). `IdempotencyCleanupService` (start +5 min, potem co 24 h) kasuje wpisy starsze niż TTL partiami po 200 i robi
`VACUUM` tylko przy ≥ 1/4 wolnych stron (pierwszy przebieg po wdrożeniu; potem strony z freelisty są ponownie
używane i plik stoi na ~TTL dni wpisów). `pending_bookings` (journal `/book`) nie ma TTL — nigdy nie jest czyszczony
automatycznie i **nie wolno kasować całego pliku** przy wpisie w tej tabeli (retry stworzyłby drugi BP).

TTL (domyślnie 14 dni od v0.19.1, wcześniej 30): **szablon `appsettings.Production.json` do v0.19.0 przypinał
`"IdempotencyTtlDays": 30`**, a self-update zachowuje ten plik — w instalacji z takiego szablonu zmień ręcznie na 14
(albo usuń klucz, pilnując przecinka w JSON) i zrestartuj usługę. Efektywny TTL loguje start usługi:
`GET /api/v1/admin/logs?grep=Idempotency%20cleanup`.

## Logi - absolute path

Windows Service ma `WorkingDirectory=C:\Windows\System32` (default). Relative
`logs/` w Serilog config trafiało gdzie indziej, folder `C:\SubiektBridge\logs\`
był pusty. Fix: `Path.Combine(AppContext.BaseDirectory, "logs", ...)`.

## Self-update flow

```
POST /api/v1/admin/update {refresh_script: true}
  ↓
Bridge:
  1. (opcjonalnie) GET https://raw.githubusercontent.com/.../update-bridge.ps1 → C:\SubiektBridge\
  2. Process.Start("cmd.exe", "/c timeout 5 & powershell update-bridge.ps1 -Force")
  3. Return 202 Accepted (klient ma 5s na otrzymanie response)
  ↓ (5s później)
Detached PowerShell (przeżyje śmierć Bridge'a):
  1. Detect latest tag z GitHub Releases API
  2. Download SubiektBridge-X.Y.Z-win-x86-fxdep.zip → %TEMP%
  3. Stop-Service SubiektBridge
  4. Backup appsettings.Production.json (in-memory)
  5. Copy bin do C:\SubiektBridge\ (zachowując data/, logs/)
  6. Restore appsettings
  7. Start-Service SubiektBridge
  8. Health check
```

Self-update NIE odświeża opisu usługi Windows (`sc.exe description` ustawia tylko `install-windows.ps1`
przy pierwszej instalacji).

## Podgląd logów na serwerze

Bez RDP: `GET /api/v1/admin/logs?tail=200&grep=<fraza>` (X-Bridge-Token) zwraca ogon najnowszego pliku
`logs\subiekt-bridge-yyyyMMdd.log` (data bez kresek). Na hoście:

```powershell
Get-ChildItem C:\SubiektBridge\logs\ | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 60 }
```

## Build paczek

Oficjalne ZIP-y (win-x86 fxdep + self-contained) buduje GitHub Actions z taga `vX.Y.Z`
(`.github/workflows/release.yml`). `scripts/publish-win.sh` = lokalny build win-x86 tymi samymi
parametrami, tylko do testów. **Tylko win-x86** (in-proc COM, Subiekt GT 32-bit).
