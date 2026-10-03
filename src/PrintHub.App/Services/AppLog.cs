using System.Text;

namespace PrintHub.App.Services;

public static class AppLog
{
    public static string Folder { get; } = Path.Combine(PrintHub.Core.Settings.AppPaths.DataDir, "logs");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"printhub-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never throw */ }
    }
}
