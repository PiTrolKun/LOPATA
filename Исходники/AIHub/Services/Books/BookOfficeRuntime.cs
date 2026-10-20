using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.Books;

public static partial class BookConverterRuntime
{
    public const string LibreOfficeVersion = "26.2.4.2";
    public const string LibreOfficeUrl = "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.4.2/win/x86_64/LibreOffice_26.2.4.2_Win_x86-64.msi";
    private static readonly SemaphoreSlim OfficeVerificationGate = new(1, 1);
    private static string? _verifiedOfficeRoot;
    private static string OfficeRoot => Path.Combine(AppDataPaths.RuntimeDirectory, "BookConverter", "libreoffice-" + LibreOfficeVersion);

    // This lookup is independent of PATH, user state and ComponentManager.
    public static string? FindBundledLibreOffice() => FindBundledLibreOffice(AppContext.BaseDirectory);
    internal static string? FindBundledLibreOffice(string applicationDirectory)
    {
        var root = Path.Combine(applicationDirectory, "BookOfficeRuntime");
        var executable = Path.Combine(root, "program", "soffice.com");
        return File.Exists(executable) && File.Exists(Path.Combine(root, "program", "soffice.bin"))
            && Directory.Exists(Path.Combine(root, "share")) ? executable : null;
    }

    private static string? FindManagedLibreOffice()
    {
        if (!Directory.Exists(OfficeRoot)) return null;
        foreach (var root in new[] { OfficeRoot }.Concat(Directory.EnumerateDirectories(OfficeRoot)))
        {
            var candidate = Path.Combine(root, "program", "soffice.com");
            if (!File.Exists(candidate)) candidate = Path.Combine(root, "program", "soffice.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Checks the shipped image on first office use, never at startup. No downloads.</summary>
    public static async Task EnsureLibreOfficeAsync(IProgress<string>? progress, CancellationToken token)
    {
        var executable = FindLibreOffice() ?? throw new FileNotFoundException("BookConverter.Error.OfficeRequired");
        await ComponentLicenseGate.EnsureAsync("runtime.libreoffice", token);
        if (!string.Equals(executable, FindBundledLibreOffice(), StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(executable))!;
        await OfficeVerificationGate.WaitAsync(token);
        try
        {
            if (_verifiedOfficeRoot == root) return;
            await using var manifest = typeof(BookConverterRuntime).Assembly.GetManifestResourceStream("AIHub.BookOfficeManifest")
                ?? throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
            await VerifyOfficeFilesAsync(root, manifest, token);
            _verifiedOfficeRoot = root;
        }
        finally { OfficeVerificationGate.Release(); }
    }

    internal static async Task VerifyOfficeFilesAsync(string root, Stream manifest, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: token);
        var basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var files = document.RootElement.GetProperty("Files");
        if (files.GetArrayLength() == 0) throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
        foreach (var entry in files.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var name = entry.GetProperty("Name").GetString()!;
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (Path.IsPathFullyQualified(name) || !path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
            await using var file = File.OpenRead(path);
            if (file.Length != entry.GetProperty("Bytes").GetInt64()
                || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
        }
    }
}
