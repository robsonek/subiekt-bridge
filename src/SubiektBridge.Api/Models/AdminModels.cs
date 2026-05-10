using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

/// <summary>Body POST /api/v1/admin/update - wszystkie pola opcjonalne.</summary>
public sealed record UpdateRequestDto(
    /// <summary>Tag wersji (np. "v0.7.13"). Default: latest z GitHub Releases.</summary>
    [property: JsonPropertyName("tag")] string? Tag,
    /// <summary>True = pobierz fxdep (~4MB, wymaga ASP.NET Core 10 x86 runtime). Default: self-contained (~46MB, runtime wbudowany).</summary>
    [property: JsonPropertyName("fxdep")] bool Fxdep = false,
    /// <summary>True (default) = pobierz swiezszy update-bridge.ps1 z main przed uruchomieniem.</summary>
    [property: JsonPropertyName("refresh_script")] bool RefreshScript = true
);

public sealed record UpdateResponseDto(
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset ScheduledAt,
    [property: JsonPropertyName("delay_seconds")] int DelaySeconds,
    [property: JsonPropertyName("estimated_duration_seconds")] int EstimatedDurationSeconds,
    [property: JsonPropertyName("script_path")] string ScriptPath,
    [property: JsonPropertyName("message")] string Message
);
