# Subiekt Bridge

Most HTTP→COM/Sfera dla integracji Subiekt GT z marketplace-manage (Laravel).

## Architektura

```
marketplace-manage (Linux/Laravel)
        ↓ HTTPS + X-Bridge-Token
SubiektBridge.Api (Windows .NET 8)
        ↓ COM (InsERT.GT) - tylko na Windowsie
Subiekt GT + Sfera + MSSQL
```

Cały kontekst architektoniczny + kontrakt API: zobacz `~/.claude/plans/tak-radiant-puddle.md`
w głównym repo marketplace-manage.

## Build

### Lokalnie na macOS/Linux (dev z FakeSfera)

```bash
cd src/SubiektBridge.Api
dotnet run
# Otwiera HTTPS na https://localhost:8443
# Sfera jest mockowana (FakeSferaSession), brak COM-u
```

### Build pod Windows (deploy)

```bash
cd src/SubiektBridge.Api
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=false \
    -o ../../publish/win-x64
```

Output: folder `publish/win-x64/` z `SubiektBridge.Api.exe` + DLL.
Self-contained = nie wymaga zainstalowanego .NET na Windowsie klienta.

### Deploy na Windowsie klienta (raz)

```powershell
# 1. Skopiuj folder publish/win-x64/ na hosta z Subiektem (np. C:\SubiektBridge\)
# 2. Przygotuj appsettings.Production.json (token, ścieżki bazy, login operatora)
# 3. Zainstaluj jako Windows Service przez NSSM
nssm install SubiektBridge "C:\SubiektBridge\SubiektBridge.Api.exe"
nssm set SubiektBridge AppDirectory "C:\SubiektBridge"
nssm set SubiektBridge Start SERVICE_AUTO_START
nssm start SubiektBridge

# 4. Logi: C:\SubiektBridge\logs\ (Serilog rolling)
```

### Update binarek (kolejne deploye)

```powershell
nssm stop SubiektBridge
# rsync/copy nowego folderu publish/win-x64/* do C:\SubiektBridge\
nssm start SubiektBridge
```

## Konfiguracja

`appsettings.json` + `appsettings.{Environment}.json` (override).

Krytyczne klucze:
- `Bridge:Token` - statyczny API key z marketplace-manage
- `Bridge:UseFakeSfera` - true na macOS/dev, false na produkcji
- `Subiekt:Server`, `Subiekt:Database` - MSSQL Subiekta
- `Subiekt:Operator`, `Subiekt:OperatorPassword` - login operatora Subiekta
  (hasło zaszyfrowane przez `InsERT.Dodatki.Szyfruj()` przed zapisaniem)
- `Subiekt:PdfTemplateId` - null = domyślny wzorzec, int = custom

## Test (smoke)

```bash
# health
curl -k https://localhost:8443/api/v1/health -H "X-Bridge-Token: dev-token"

# wystaw FV (z FakeSfera - zwróci sztywny response)
curl -k -X POST https://localhost:8443/api/v1/invoices \
    -H "X-Bridge-Token: dev-token" \
    -H "Idempotency-Key: order:1:type:FS" \
    -H "Content-Type: application/json" \
    -d @sample-payload.json
```
