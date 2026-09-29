namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany gdy linii korekty nie da się poprawnie zmapować na pozycje FS skopiowane przez
/// <c>NaPodstawie()</c>: brak pasującej pozycji, druga linia na tę samą pozycję albo korekta
/// większa niż ilość na pozycji (ujemna ilość po korekcie). Wcześniej część z tego kończyła się
/// 500 (retry w nieskończoność) albo cicho zaniżonym KFS (ilość zerowana).
///
/// Controller mapuje na 422 z code='INVALID_CORRECTION'.
/// </summary>
public sealed class InvalidCorrectionException : Exception
{
    public InvalidCorrectionException(string message) : base(message) { }
}
