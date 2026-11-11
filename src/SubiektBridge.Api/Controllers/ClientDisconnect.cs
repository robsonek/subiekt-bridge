using Microsoft.AspNetCore.Mvc;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Rozłączenie klienta w trakcie odczytu (anulowany RequestAborted) to nie awaria mostu ani Subiekta. Do v0.23.0 listingi łapały je
/// ogólnym catch → `[ERR] ... failed` + 502 SUBIEKT_QUERY_FAILED, co zaśmiecało kontrolę „zero błędów w logu" (incydent 2026-11-09:
/// każdy timeout klienta = wpis ERR). Odpowiedzi i tak nikt nie odbierze - 499 (konwencja „client closed request") trafia tylko
/// do logu żądań, na poziomie Information. Wołać z `catch (OperationCanceledException) when (ct.IsCancellationRequested)`.
/// </summary>
public static class ClientDisconnect
{
    public const int StatusCode = 499;

    public static StatusCodeResult Result(ILogger logger, string operation)
    {
        logger.LogInformation("{Operation}: klient rozlaczyl sie przed odpowiedzia - odczyt przerwany", operation);
        return new StatusCodeResult(StatusCode);
    }
}
