<#
.SYNOPSIS
  Sondy pozycji PZ przez Sfere na DOKUMENCIE TESTOWYM (okno wdrozenia SubiektBridge v0.21.0; spec edycji PZ, sekcja 8.2).

.DESCRIPTION
  UWAGA: ZAPIS W ZYWYM SUBIEKCIE. Uzywaj wylacznie na PZ utworzonym do testow, za zgoda usera. -DokId jest obowiazkowy,
  kazdy scenariusz poza 'inspect' wymaga -Confirm. Skrypt wypisuje faze (load/set/zapisz/close), HRESULT (0x........) i komunikat
  Sfery z najglebszego InnerException oraz SQL do odczytu skutkow przez POST /api/v1/admin/query.
  Subiekt GT i Sfera sa 32-bit: uruchamiaj 32-bitowy Windows PowerShell w STA. Z PowerShell:
    & "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -STA -ExecutionPolicy Bypass -File deploy\probe-pz-items.ps1 ...
  (z cmd.exe: %windir%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -STA -ExecutionPolicy Bypass -File deploy\probe-pz-items.ps1 ...)
  Kody wyjscia (jedna sciezka: exit po finally): 0 = sonda wykonana, Zamknij/Zakoncz OK; 1 = blad COM w sondzie (faza w logu);
  2 = warunki wstepne (x64, nie-STA, brak dokumentu, nie-PZ, zly DokId); 3 = brak -Confirm; 4 = NIEPOTWIERDZONE: sonda przeszla,
  ale Zamknij albo Zakoncz rzucilo (lock w sesji moze wisiec - sprawdz GUI i SQL).
  Wlasnosc RCW: pozycje/towary -> kolekcja Pozycje -> dokument (Zamknij, ReleaseComObject) -> sesja (Zakoncz, Release) -> GT/Dodatki.
  PS 5.x, ASCII-only (bez polskich znakow w tresci skryptu - zasada repo).

.EXAMPLE
  ... -Server ".\SQLEXPRESS" -Database MAGAZYN -DbUser sa -Operator Szef -DokId 123456 -Scenario inspect
  ... -DokId 123456 -Scenario reduce -NoSave -Confirm
  ... -DokId 123456 -Scenario add -Ean 5901234567890 -Confirm
#>
param(
  [Parameter(Mandatory = $true)][string]$Server,
  [Parameter(Mandatory = $true)][string]$Database,
  [Parameter(Mandatory = $true)][string]$DbUser,
  [Parameter(Mandatory = $true)][string]$Operator,
  [Parameter(Mandatory = $true)][int]$DokId,
  [Parameter(Mandatory = $true)][ValidateSet('inspect', 'patch', 'reduce', 'remove', 'add', 'replace', 'two-changes')][string]$Scenario,
  [string]$Ean = '',
  [string]$OriginalNumber = 'PROBE 1/2026',
  [switch]$NoSave,
  [switch]$Confirm
)

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = 'Stop'

function Write-Phase([string]$phase, [string]$msg) { Write-Host ("[{0}] {1}" -f $phase, $msg) }

function Format-ComError($errorRecord) {
  $ex = $errorRecord.Exception
  while ($ex.InnerException -ne $null) { $ex = $ex.InnerException }
  $hr = [int64]$ex.HResult -band 0xFFFFFFFF
  return ("HRESULT=0x{0:X8} type={1} msg={2}" -f $hr, $ex.GetType().Name, $ex.Message)
}

function ConvertTo-Plain([System.Security.SecureString]$secure) {
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Release-Handle($obj) {
  if ($obj -ne $null -and [Runtime.InteropServices.Marshal]::IsComObject($obj)) {
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($obj) } catch { }
  }
}

# Bitowosc i apartment PRZED dotknieciem Sfery (AGENTS.md: cala linia GT jest 32-bit; InsERT.GT jest apartment-threaded).
if ([Environment]::Is64BitProcess) {
  Write-Host 'FAIL: proces 64-bit. Uruchom: & "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -STA -File deploy\probe-pz-items.ps1 ...'
  exit 2
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
  Write-Host "FAIL: apartment != STA. Uruchom powershell.exe z parametrem -STA."
  exit 2
}
if ($DokId -le 0) { Write-Host "FAIL: -DokId musi byc dodatni (dok_Id dokumentu TESTOWEGO)."; exit 2 }

$script:cleanupFailed = $false

# Cala praca w funkcji: 'return <kod>' wewnatrz try wykonuje finally (cleanup RCW) i oddaje kod; jedyne 'exit' jest na koncu skryptu.
# Kazde wywolanie metody COM, ktore moze cos zwrocic, idzie w [void] - inaczej wynik trafilby do strumienia wyjscia funkcji.
function Invoke-Probe {
  $gt = $null; $dodatki = $null; $sub = $null; $dok = $null; $pozycje = $null
  $items = New-Object System.Collections.ArrayList
  try {
    $dbPass = Read-Host -AsSecureString ("Haslo SQL dla " + $DbUser)
    $opPass = Read-Host -AsSecureString ("Haslo operatora " + $Operator)

    $gt = New-Object -ComObject "InsERT.GT"
    $dodatki = New-Object -ComObject "InsERT.Dodatki"
    $gt.Produkt = 1
    $gt.Autentykacja = 0
    $gt.Serwer = $Server
    $gt.Baza = $Database
    $gt.Uzytkownik = $DbUser
    $gt.UzytkownikHaslo = $dodatki.Szyfruj((ConvertTo-Plain $dbPass))
    $gt.Operator = $Operator
    $gt.OperatorHaslo = $dodatki.Szyfruj((ConvertTo-Plain $opPass))
    # Uruchom(0 = dopasuj do dzialajacej instancji, 4 = w tle bez okna) - jak RealSferaSession.OpenSession.
    $sub = $gt.Uruchom(0, 4)

    Write-Phase 'load' ("WczytajDokument(" + $DokId + ")")
    $dok = $sub.SuDokumentyManager.WczytajDokument($DokId)
    if ($dok -eq $null) { Write-Host "FAIL: WczytajDokument zwrocil null - dokument nie istnieje?"; return 2 }
    Write-Host ("  NumerPelny={0} Typ={1} StatusDokumentu={2} MoznaEdytowac={3} DoDokumentuId={4} DoDokumentuNumerPelny={5}" -f `
      $dok.NumerPelny, $dok.Typ, $dok.StatusDokumentu, $dok.MoznaEdytowac, $dok.DoDokumentuId, $dok.DoDokumentuNumerPelny)
    Write-Host ("  NumerOryginalny={0}" -f $dok.NumerOryginalny)
    Write-Host ("  Uwagi={0}" -f $dok.Uwagi)
    $pozycje = $dok.Pozycje
    $n = [int]$pozycje.Liczba
    for ($i = 1; $i -le $n; $i++) {
      $p = $pozycje.Element($i)
      [void]$items.Add($p)
      Write-Host ("  poz lp={0} Id={1} TowarId={2} TowarRodzaj={3} IloscJm={4} Ilosc={5} Jm={6} CenaNettoPrzedRabatem={7} RabatProcent={8}" -f `
        $p.Lp, $p.Id, $p.TowarId, $p.TowarRodzaj, $p.IloscJm, $p.Ilosc, $p.Jm, $p.CenaNettoPrzedRabatem, $p.RabatProcent)
    }
    if ([int]$dok.Typ -ne 10) { Write-Host ("FAIL: dokument nie jest PZ (Typ=" + $dok.Typ + ")"); return 2 }
    if ($Scenario -eq 'inspect') { Write-Phase 'inspect' 'tylko odczyt, bez zmian'; return 0 }
    if (-not $Confirm) {
      Write-Host ("STOP: scenariusz '" + $Scenario + "' ZAPISUJE w zywym Subiekcie. Potwierdz, ze " + $dok.NumerPelny + " jest dokumentem TESTOWYM: dodaj -Confirm")
      return 3
    }

    Write-Phase 'set' ("scenario=" + $Scenario + " (pierwsza zmiana atrybutu = lock Sfery)")
    switch ($Scenario) {
      'patch' { $dok.NumerOryginalny = $OriginalNumber }
      'reduce' { $p = $items[0]; $p.IloscJm = ([double]$p.IloscJm - 1) }
      'remove' { $p = $items[0]; [void]$p.Usun() }
      'add' {
        if ($Ean -eq '') { throw "-Ean jest wymagany dla scenariusza add" }
        $tw = $sub.Towary.Wczytaj($Ean); [void]$items.Add($tw)
        $np = $pozycje.Dodaj($tw.Identyfikator); [void]$items.Add($np)
        $np.IloscJm = 1
        $np.CenaNettoPrzedRabatem = [double]1.0
        Write-Host ("  Pozycje.Liczba po Dodaj={0} (wiecej niz +1 = asortyment powiazany)" -f $pozycje.Liczba)
      }
      'replace' {
        if ($Ean -eq '') { throw "-Ean jest wymagany dla scenariusza replace" }
        $p = $items[0]; [void]$p.Usun()
        $tw = $sub.Towary.Wczytaj($Ean); [void]$items.Add($tw)
        $np = $pozycje.Dodaj($tw.Identyfikator); [void]$items.Add($np)
        $np.IloscJm = 1
        $np.CenaNettoPrzedRabatem = [double]1.0
      }
      'two-changes' {
        if ($n -lt 2) { throw "two-changes wymaga PZ z co najmniej 2 pozycjami" }
        $p1 = $items[0]; $p1.IloscJm = ([double]$p1.IloscJm + 1)
        $p2 = $items[1]; $p2.IloscJm = ([double]$p2.IloscJm - 1)
      }
    }
    Write-Phase 'set' 'OK'
    if ($NoSave) { Write-Phase 'nosave' 'pominieto Zapisz (-NoSave); Zamknij zwolni lock'; return 0 }

    Write-Phase 'zapisz' 'Zapisz()'
    [void]$dok.Zapisz()
    Write-Phase 'zapisz' 'OK'
    return 0
  }
  catch {
    Write-Host ("ERR " + (Format-ComError $_))
    return 1
  }
  finally {
    # Kolejnosc zwalniania: pozycje/towary -> kolekcja -> dokument -> sesja -> GT/Dodatki. Blad Zamknij/Zakoncz = wynik NIEPOTWIERDZONY (kod 4).
    foreach ($h in $items) { Release-Handle $h }
    $items.Clear()
    Release-Handle $pozycje
    if ($dok -ne $null) {
      try { [void]$dok.Zamknij(); Write-Phase 'close' 'Zamknij OK' }
      catch { Write-Host ("close ERR " + (Format-ComError $_)); $script:cleanupFailed = $true }
      Release-Handle $dok
    }
    if ($sub -ne $null) {
      try { [void]$sub.Zakoncz(); Write-Phase 'close' 'Zakoncz OK' }
      catch { Write-Host ("zakoncz ERR " + (Format-ComError $_)); $script:cleanupFailed = $true }
      Release-Handle $sub
    }
    Release-Handle $dodatki
    Release-Handle $gt
    Write-Host ""
    Write-Host "SQL do odczytu skutkow (POST /api/v1/admin/query, read-only):"
    Write-Host ("  SELECT dok_Id, dok_NrPelny, dok_NrPelnyOryg, dok_Status, dok_DoDokId, dok_StatusBlok, dok_StatusKsieg FROM dok__Dokument WHERE dok_Id = " + $DokId)
    Write-Host ("  SELECT ob_Id, ob_DokHanId, ob_TowId, ob_Ilosc, ob_IloscMag, ob_CenaNetto FROM dok_Pozycja WHERE ob_DokMagId = " + $DokId)
    Write-Host ("  SELECT mr_Id, mr_PozId, mr_Ilosc, mr_Pozostalo, mr_Cena, mr_SeriaId FROM dok_MagRuch WHERE mr_PozId IN (SELECT ob_Id FROM dok_Pozycja WHERE ob_DokMagId = " + $DokId + ")")
  }
}

$code = [int](Invoke-Probe | Select-Object -Last 1)
if ($code -eq 0 -and $script:cleanupFailed) {
  $code = 4
  Write-Host "RESULT: NIEPOTWIERDZONE (kod 4) - sonda przeszla, ale Zamknij/Zakoncz rzucilo; sprawdz lock w GUI Subiekta i SQL wyzej."
}
Write-Host ("EXIT " + $code)
exit $code
