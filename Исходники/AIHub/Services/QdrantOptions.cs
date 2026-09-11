using System.IO;

namespace AIHub.Services;

public sealed record QdrantOptions
{
    public const string Version = "1.19.1";
    public const string LicenseId = "backend.qdrant";
    public const string ArchiveSha256 = "9b6f69bd85f6abed4bc13f943099f55c6ffd55f5dd90388635320d8fbb569eb0";
    public const long ArchiveBytes = 29671153;
    public const string ExecutableSha256 = "b5354e3c8f9d13d92294f38d4988d4ddedc04acaa2ce51f0a2d65b30e2d4cd69";
    public static Uri DownloadUri { get; } = new($"https://github.com/qdrant/qdrant/releases/download/v{Version}/qdrant-x86_64-pc-windows-msvc.zip");
    public string InstallDirectory { get; init; } = Path.Combine(AppDataPaths.BackendsDirectory, "qdrant", Version, "win-x64");
    public string DataDirectory { get; init; } = Path.Combine(AppDataPaths.BaseDirectory, "Memory", "Qdrant");
    public Action? ValidateStorage { get; init; }
    public string HelperExecutable { get; init; } = Path.ChangeExtension(typeof(QdrantOptions).Assembly.Location, ".exe");
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public string Executable => Path.Combine(InstallDirectory, "qdrant.exe");
    public string LogPath => Path.Combine(DataDirectory, "qdrant.log");
}
