using AIHub.Services.Books;
await BookConverterRuntime.ExtractPortableAsync(args[0], args[1], CancellationToken.None);
Console.WriteLine("EXTRACTED " + args[1]);
namespace AIHub.Services
{
    public static class AppDataPaths { public static string RuntimeDirectory => Path.GetTempPath(); public static string BaseDirectory => Path.GetTempPath(); }
    public static class ComponentLicenseGate { public static Task EnsureAsync(string id, CancellationToken token) => Task.CompletedTask; }
    public record Entry(string Id); public record Record(string InstallPath); public record Status(Entry Entry, Record Record, bool IsAvailable);
    public class ComponentManager { public Status[] GetStatus() => []; }
    public record CliResult(string ExecutablePath);
    public class SystemCliDiscoveryService { public CliResult? Find(string id) => null; }
    public record Receipt(string Id,string Terms);
    public record LicenseEntry(string Id,string Terms);
    public class ComponentLicenseService { public ComponentLicenseService(string directory, string path) {} public LicenseEntry[] Entries => []; public Receipt[] ReadReceipts() => []; }
    public class OwnedProcessRegistry { public static OwnedProcessRegistry Shared { get; } = new(); public System.Diagnostics.Process Start(System.Diagnostics.ProcessStartInfo info, string operation) => System.Diagnostics.Process.Start(info)!; }
}
namespace Lopata.Updates { public static class SafeUpdatePath { public static void RejectLinks(string path) { } } }
