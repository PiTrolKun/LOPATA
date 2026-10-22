using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.Books;

public static partial class BookConverterRuntime
{
    public const string LibreOfficeVersion = "26.2.4.2";
    public const string LibreOfficeUrl = "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.4.2/win/x86_64/LibreOffice_26.2.4.2_Win_x86-64.msi";
    private static readonly SemaphoreSlim OfficeVerificationGate = new(1, 1);
    private static string OfficeRoot => Path.Combine(AppDataPaths.RuntimeDirectory, "BookConverter", "libreoffice-" + LibreOfficeVersion);

    // This lookup is independent of PATH, user state and ComponentManager.
    public static string? FindBundledLibreOffice() => FindBundledLibreOffice(AppContext.BaseDirectory);
    internal static string? FindBundledLibreOffice(string applicationDirectory)
    {
        var root = Path.Combine(applicationDirectory, "BookOfficeRuntime");
        if (File.Exists(Path.Combine(root, BookOfficeBundle.FileName))) return Path.Combine(BundledOfficeCache, "program", "soffice.com");
        var executable = Path.Combine(root, "program", "soffice.com");
        return File.Exists(executable) && File.Exists(Path.Combine(root, "program", "soffice.bin"))
            && Directory.Exists(Path.Combine(root, "share")) ? executable : null;
    }

    private static readonly Lazy<string> OfficeManifestId = new(() =>
    {
        using var manifest = typeof(BookConverterRuntime).Assembly.GetManifestResourceStream("AIHub.BookOfficeManifest")!;
        return Convert.ToHexStringLower(SHA256.HashData(manifest));
    });
    private static string BundledOfficeCache => Path.Combine(AppDataPaths.RuntimeDirectory, "BookConverter",
        "bundled-office-" + LibreOfficeVersion + "-" + OfficeManifestId.Value[..16]);

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
            await using var manifest = typeof(BookConverterRuntime).Assembly.GetManifestResourceStream("AIHub.BookOfficeManifest")
                ?? throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
            var archive = Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime", BookOfficeBundle.FileName);
            if (File.Exists(archive)) await BookOfficeBundle.EnsureAsync(archive, root, manifest, token);
            else await VerifyOfficeFilesAsync(root, manifest, token);
        }
        finally { OfficeVerificationGate.Release(); }
    }

    internal static async Task VerifyOfficeFilesAsync(string root, Stream manifest, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: token);
        var expected = document.RootElement.GetProperty("Files").EnumerateArray().Select(f => new PinnedTreeFile(
            f.GetProperty("Name").GetString()!, f.GetProperty("Bytes").GetInt64(), f.GetProperty("Sha256").GetString()!)).ToArray();
        if (expected.Length == 0) throw new InvalidDataException("BookConverter.Error.OfficeBundleInvalid");
        await RuntimeFileAudit.VerifyAsync("Books.LibreOffice", root, LibreOfficeVersion, expected, token);
    }
}
