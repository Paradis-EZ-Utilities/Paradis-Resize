namespace ParadisResize.Core.Util;

/// <summary>
/// The engine's way into the app's log file. The app points these at its logger at startup; tests
/// can leave them alone (they do nothing) or capture them.
/// </summary>
public static class CoreLog
{
    public static Action<string> Info { get; set; } = _ => { };
    public static Action<string> Warn { get; set; } = _ => { };
    public static Action<string, Exception?> Error { get; set; } = (_, _) => { };
}
