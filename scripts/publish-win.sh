#!/usr/bin/env bash
# Lokalny build SubiektBridge dla Windows x86 (self-contained, runtime .NET wbudowany) -
# do testów/debugowania. Oficjalne paczki buduje GitHub Actions z taga vX.Y.Z
# (.github/workflows/release.yml) - tych samych parametrów używa ten skrypt.
#
# Tylko win-x86: in-proc COM wymaga, by bitowość mostu pasowała do Subiekta GT (32-bit).
# Binarka x64 nie połączy się ze Sferą (0x8000FFFF przy Activator.CreateInstance).
#
# Usage: ./scripts/publish-win.sh [output_dir]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${1:-${PROJECT_ROOT}/publish/win-x86}"

echo "Publishing SubiektBridge.Api (win-x86) -> ${OUTPUT_DIR}"

cd "${PROJECT_ROOT}/src/SubiektBridge.Api"

dotnet publish \
    -c Release \
    -r win-x86 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "${OUTPUT_DIR}"

echo
echo "Artefakty:"
ls -lh "${OUTPUT_DIR}/SubiektBridge.Api.exe" 2>/dev/null || echo "  (brak SubiektBridge.Api.exe!)"
echo "Łączny rozmiar: $(du -sh "${OUTPUT_DIR}" | awk '{print $1}')"
echo
echo "Instalacja na Windowsie klienta (jako Admin, natywny Windows Service przez sc.exe):"
echo "  1. Skopiuj cały folder ${OUTPUT_DIR} do C:\\SubiektBridge\\"
echo "  2. Utwórz tam appsettings.Production.json (token, operator Subiekta, MSSQL)"
echo "  3. Skopiuj deploy/install-windows.ps1 do C:\\SubiektBridge\\ i uruchom: .\\install-windows.ps1"
