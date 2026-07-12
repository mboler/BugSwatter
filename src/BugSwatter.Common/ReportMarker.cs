namespace BugSwatter.Common;

/// <summary>The stdout contract between the reviewer (Informant) and a supervising dispatcher (Marshal): the reviewer prints this prefix followed by the primary report's absolute path, or "none" when it produced no report, so the dispatcher records the exact artifact instead of guessing the newest file by timestamp</summary>
public static class ReportMarker
{
    /// <summary>Line prefix the reviewer prints on its own stdout line, followed by the absolute report path or the literal "none"</summary>
    public const string Prefix = "INFORMANT-REPORT:";
}
