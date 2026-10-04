using System.Diagnostics;

namespace PrintHub.App.Services;

/// <summary>Writes "how long since the process started" marks to the log, so start-up time can be measured instead of guessed.</summary>
public static class StartupTrace
{
    static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;
    public static void Mark(string what) => AppLog.Write($"[startup] +{(DateTime.Now - ProcessStart).TotalMilliseconds,6:0} ms  {what}");
}