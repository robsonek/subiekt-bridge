## Pliki do pobrania

Subiekt GT jest wyłącznie 32-bit (cała linia "GT" InsERT). Bridge to in-process COM
klient — bit-level musi pasować, dlatego tylko **win-x86**. Działa na 32- i 64-bitowym
Windowsie (na 64-bit przez WOW64).

| Plik | Rozmiar | Wymagania |
|---|---|---|
| `SubiektBridge-__VERSION__-win-x86.zip` | ~45 MB | **Nic** — runtime wbudowany (self-contained) |
| `SubiektBridge-__VERSION__-win-x86-fxdep.zip` | ~2 MB | **ASP.NET Core Runtime 10 (x86)** zainstalowany |

Niepewny? Bierz self-contained.

ASP.NET Core Runtime 10 (x86) dla Windows: https://dotnet.microsoft.com/download/dotnet/10.0
(przy pobieraniu wybierz "x86" — nie "x64").
