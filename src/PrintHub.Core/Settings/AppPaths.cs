namespace PrintHub.Core.Settings;

/// <summary>Where the app keeps its own files. Override with the HSA_DATA_DIR environment variable (used by automated UI tests).</summary>
public static class AppPaths
{
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("HSA_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HP Smart Alternative");
}
