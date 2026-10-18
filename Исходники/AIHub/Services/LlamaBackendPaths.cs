using System.IO;

namespace AIHub.Services;

public static class LlamaBackendPaths
{
    public const string Release = "b9442";
    public const string Platform = "win-cuda-12.4-x64";

    public static string DirectoryPath => Path.Combine(
        AppDataPaths.BackendsDirectory,
        "llama.cpp",
        Release,
        Platform);

    public static string ServerExecutablePath => Path.Combine(DirectoryPath, "llama-server.exe");

    public static string CliExecutablePath => Path.Combine(DirectoryPath, "llama-cli.exe");

    // This is a catalog family label, not evidence about an executed bundle.
    public static string DisplayName => "llama.cpp";
}
