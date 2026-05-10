using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Self-update endpoint. Spawnuje detached PowerShell job ktory za 5s zatrzyma
/// service, wymieni binaria z GitHub Release ZIP-a i wystartuje service ponownie.
/// Bridge zwraca 202 PRZED zatrzymaniem - klient sprawdza /health w petli zeby
/// wiedziec kiedy nowa wersja wstala.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class AdminController : ControllerBase
{
    private static readonly Regex ReleaseTagRegex = new(
        @"^v?\d+\.\d+\.\d+(?:[-.][0-9A-Za-z]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly BridgeOptions _options;
    private readonly ISferaSession _sfera;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IOptions<BridgeOptions> options, ISferaSession sfera, ILogger<AdminController> logger)
    {
        _options = options.Value;
        _sfera = sfera;
        _logger = logger;
    }

    [HttpPost("query")]
    public async Task<ActionResult<QueryResultDto>> Query(
        [FromBody] QueryRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            return BadRequest(new ErrorResponseDto("EMPTY_SQL", "Pole 'sql' wymagane."));
        }

        // Whitelist: tylko read-only operations.
        var sqlTrimmed = request.Sql.TrimStart();
        var firstWord = new string(sqlTrimmed.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (firstWord != "SELECT" && firstWord != "WITH")
        {
            return BadRequest(new ErrorResponseDto(
                "READONLY_ONLY",
                "Tylko SELECT/WITH dozwolone. Bridge nie wykonuje INSERT/UPDATE/DELETE/DROP."));
        }

        // Reject DML/DDL slow w srodku query (np. WITH ... INSERT ... SELECT)
        var upper = request.Sql.ToUpperInvariant();
        foreach (var forbidden in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "TRUNCATE ", "ALTER ", "CREATE ", "EXEC ", "EXECUTE ", "MERGE ", "GRANT ", "REVOKE " })
        {
            if (upper.Contains(forbidden))
            {
                return BadRequest(new ErrorResponseDto(
                    "READONLY_ONLY",
                    $"Wykryto slowo kluczowe '{forbidden.Trim()}' - tylko SELECT/WITH dozwolone."));
            }
        }

        var maxRows = Math.Clamp(request.MaxRows ?? 100, 1, 1000);

        try
        {
            var result = await _sfera.QueryAsync(request.Sql, maxRows, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryAsync failed: {Sql}", request.Sql);
            return StatusCode(500, new ErrorResponseDto(
                "QUERY_FAILED",
                ex.GetType().Name + ": " + ex.Message));
        }
    }

    /// <summary>
    /// GET /api/v1/admin/logs?tail=N&grep=substr - tail z najnowszego pliku Serilog.
    /// Domyslnie 100 linii. grep filtruje case-insensitive (substring match).
    /// Logi w {InstallDir}\logs\subiekt-bridge-*.log (Serilog rolling daily).
    /// </summary>
    [HttpGet("logs")]
    public ActionResult<LogsResponseDto> Logs([FromQuery] int tail = 100, [FromQuery] string? grep = null)
    {
        if (tail < 1) tail = 1;
        if (tail > 5000) tail = 5000;

        var logsDir = Path.Combine(ResolveInstallDir(), "logs");
        if (!Directory.Exists(logsDir))
        {
            return NotFound(new ErrorResponseDto("LOGS_NOT_FOUND", $"Brak katalogu logow: {logsDir}"));
        }

        var newest = new DirectoryInfo(logsDir)
            .EnumerateFiles("subiekt-bridge*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            return NotFound(new ErrorResponseDto("LOGS_NOT_FOUND", "Brak plikow log w " + logsDir));
        }

        // FileShare.ReadWrite - Serilog trzyma file open dla rolling, nie blokujemy go.
        List<string> lines;
        try
        {
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            lines = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) lines.Add(l);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                "LOGS_READ_FAILED", ex.GetType().Name + ": " + ex.Message));
        }

        IEnumerable<string> filtered = lines;
        if (!string.IsNullOrEmpty(grep))
        {
            filtered = filtered.Where(l => l.Contains(grep, StringComparison.OrdinalIgnoreCase));
        }

        var result = filtered.TakeLast(tail).ToArray();
        return Ok(new LogsResponseDto(
            File: newest.Name,
            TotalLines: lines.Count,
            Returned: result.Length,
            Lines: result));
    }

    [HttpPost("update")]
    public ActionResult<UpdateResponseDto> Update([FromBody] UpdateRequestDto? request)
    {
        if (!_options.AllowSelfUpdate)
        {
            return NotFound(new ErrorResponseDto(
                Code: "SELF_UPDATE_DISABLED",
                Message: "Self-update jest wylaczony w appsettings (Bridge.AllowSelfUpdate=false)."));
        }

        request ??= new UpdateRequestDto(null, false, true);

        if (!string.IsNullOrWhiteSpace(request.Tag))
            request = request with { Tag = request.Tag.Trim() };

        if (!string.IsNullOrWhiteSpace(request.Tag) && !IsValidReleaseTag(request.Tag))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "INVALID_TAG",
                Message: "Tag release ma nieprawidlowy format. Oczekiwany format: v1.2.3 lub v1.2.3-suffix."));
        }

        var installDir = ResolveInstallDir();
        var scriptPath = Path.Combine(installDir, "update-bridge.ps1");

        // Pobierz swiezszy skrypt z main jesli proszony.
        if (request.RefreshScript)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "SubiektBridge-SelfUpdate/1.0");
                var url = "https://raw.githubusercontent.com/robsonek/subiekt-bridge/main/deploy/update-bridge.ps1";
                var content = http.GetStringAsync(url).GetAwaiter().GetResult();
                System.IO.File.WriteAllText(scriptPath, content);
                _logger.LogInformation("Pobrano swiezszy update-bridge.ps1 z {Url} ({Bytes} bajtow)",
                    url, content.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refresh skryptu z GitHub padl - uzyje istniejacego {Path}", scriptPath);
            }
        }

        if (!System.IO.File.Exists(scriptPath))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "SCRIPT_NOT_FOUND",
                Message: $"update-bridge.ps1 nie istnieje w {installDir}. Pobierz recznie: " +
                         "Invoke-WebRequest https://raw.githubusercontent.com/robsonek/subiekt-bridge/main/deploy/update-bridge.ps1 -OutFile " + scriptPath));
        }

        // Resolve tag: jesli klient nie podal, pobierz latest z GitHub.
        // Robimy to tutaj (C# / .NET Core) bo PS 5.x na Windows Server 2016
        // nie radzi sobie z TLS do api.github.com (brak wymaganych cipher suites).
        var resolvedTag = request.Tag;
        if (string.IsNullOrWhiteSpace(resolvedTag))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "SubiektBridge-SelfUpdate/1.0");
                var json = http.GetStringAsync($"https://api.github.com/repos/robsonek/subiekt-bridge/releases/latest")
                    .GetAwaiter().GetResult();
                var doc = System.Text.Json.JsonDocument.Parse(json);
                resolvedTag = doc.RootElement.GetProperty("tag_name").GetString()?.Trim();
                _logger.LogInformation("Resolved latest tag from GitHub: {Tag}", resolvedTag);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udalo sie pobrac latest tag z GitHub - skrypt PS sprobuje sam");
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedTag) && !IsValidReleaseTag(resolvedTag))
        {
            _logger.LogError("GitHub latest tag has invalid format: {Tag}", resolvedTag);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "INVALID_RELEASE_TAG",
                Message: "GitHub Releases zwrocil tag w nieprawidlowym formacie."));
        }

        var scriptArgs = new StringBuilder();
        scriptArgs.Append(" -Force");
        if (!string.IsNullOrWhiteSpace(resolvedTag))
            scriptArgs.Append(" -Tag ").Append(resolvedTag);
        // Self-contained jest default (bezpieczniejsze - nie zaklada x86 runtime na hoscie).
        // Aby pobrac fxdep wariant ustaw fxdep=true w body POST /admin/update.
        if (request.Fxdep)
            scriptArgs.Append(" -Fxdep");

        // Delay + skrypt w jednym poleceniu PowerShell:
        // Start-Sleep daje Bridge'owi czas na zwrocenie 202 zanim service
        // zostanie zatrzymany. Calosc w jednym procesie powershell.exe
        // (cmd.exe + timeout nie dziala jako child serwisu Windows).
        var delaySeconds = 5;
        var psCommand = $"Start-Sleep -Seconds {delaySeconds}; & '{scriptPath}'{scriptArgs}";
        var psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = psArgs,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = installDir,
        };

        try
        {
            var proc = Process.Start(psi);
            _logger.LogWarning("Self-update scheduled - PID {Pid}, delay {Delay}s, command: {Command}",
                proc?.Id, delaySeconds, psCommand);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udalo sie spawnowac update-bridge.ps1");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "SPAWN_FAILED",
                Message: "Nie udalo sie uruchomic update-bridge.ps1: " + ex.Message));
        }

        return Accepted(new UpdateResponseDto(
            ScheduledAt: DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
            DelaySeconds: delaySeconds,
            EstimatedDurationSeconds: 60,
            ScriptPath: scriptPath,
            Message: "Update zaplanowany. Bridge zostanie zatrzymany za " + delaySeconds + "s. " +
                     "Sprawdz /health w petli (~30-60s) zeby wiedziec kiedy nowa wersja wstala."));
    }

    private static bool IsValidReleaseTag(string tag) =>
        ReleaseTagRegex.IsMatch(tag);

    private string ResolveInstallDir()
    {
        if (!string.IsNullOrWhiteSpace(_options.InstallDir))
            return _options.InstallDir;

        // Default: katalog z ktorego uruchomiono SubiektBridge.Api.exe
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        var path = entryAssembly?.Location ?? AppContext.BaseDirectory;
        return Path.GetDirectoryName(path) ?? "C:\\SubiektBridge";
    }
}
