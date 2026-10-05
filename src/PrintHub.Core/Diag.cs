using System.Diagnostics;

namespace PrintHub.Core;

/// <summary>Where the core library reports what it is doing (USB traffic, scanner driver fallbacks, print timings). The app routes it into its log file.</summary>
public static class Diag
{
    public static Action<string>? Sink { get; set; }

    public static void Log(string message)
    {
        try { Sink?.Invoke(message); } catch { /* logging must never break the work */ }
    }

    /// <summary>Run <paramref name="f"/> and log how long it took (also when it throws).</summary>
    public static T Time<T>(string what, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        try { return f(); }
        finally { Log($"{what} took {sw.ElapsedMilliseconds} ms"); }
    }

    public static async Task<T> TimeAsync<T>(string what, Func<Task<T>> f)
    {
        var sw = Stopwatch.StartNew();
        try { return await f().ConfigureAwait(false); }
        finally { Log($"{what} took {sw.ElapsedMilliseconds} ms"); }
    }
}
