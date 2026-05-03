#!/usr/bin/env bash
# Build SubiektBridge dla Windows x64. Produkuje self-contained binarkę z
# wbudowanym runtime'em .NET - klient nie musi nic instalować.
#
# Usage: ./scripts/publish-win.sh [output_dir]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${1:-${PROJECT_ROOT}/publish/win-x64}"

echo "Publishing SubiektBridge.Api -> ${OUTPUT_DIR}"

cd "${PROJECT_ROOT}/src/SubiektBridge.Api"

dotnet publish \
    -c Release \
    -r win-x64 \
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
echo "Następne kroki na Windowsie klienta:"
echo "  1. Skopiuj cały folder ${OUTPUT_DIR} do C:\\SubiektBridge\\"
echo "  2. Edytuj appsettings.json (token, login operatora Subiekta, MSSQL)"
echo "  3. nssm install SubiektBridge C:\\SubiektBridge\\SubiektBridge.Api.exe"
echo "  4. nssm start SubiektBridge"
