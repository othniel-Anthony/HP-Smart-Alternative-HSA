namespace PrintHub.Core.Printing;

/// <summary>
/// Counts the operations that hold a printer's USB connection for themselves (waste ink counters, cleaning, nozzle check, power flush).
/// While one is running, HSA's own background work on that printer (automatic searches, re-selecting the printer, status requests) is held back:
/// the printer answers one conversation at a time, and each interruption made the counter read take about a minute instead of one second.
/// </summary>
public static class PrinterActivity
{
    static int _busy;

    /// <summary>True while at least one operation holds a printer's USB connection.</summary>
    public static bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>Call when an operation starts; dispose the result when it ends (also when it fails).</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _busy);
        return new Hold();
    }

    sealed class Hold : IDisposable
    {
        int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _busy); }
    }
}
