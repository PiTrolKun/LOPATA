using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using SharpCompress.Compressors.LZMA;

namespace AIHub.Services.Books;

/// <summary>Pinned, self-contained official calibre CLI. No system installation or library import.</summary>
public static partial class BookConverterRuntime
{
    public const string Version = "9.16.0";
    public const string LicenseId = "books.calibre";
    public const string DownloadUrl = "https://github.com/kovidgoyal/calibre/releases/download/v9.16.0/calibre-portable-installer-9.16.0.exe";
    public const string DownloadSha256 = "BE3B24228133EC64148ACF5E6A3244DBC88A9B509AE8992B7922C425DA0F1AAE";
    private const long DownloadBytes = 217118472;
    private static readonly SemaphoreSlim VerificationGate = new(1, 1);
    private static string? _verifiedRoot;
    private static string ManagedRoot => Path.Combine(AppDataPaths.RuntimeDirectory, "BookConverter", "calibre-" + Version);
    private static string RelativeExecutable => Path.Combine("Calibre Portable", "Calibre", "ebook-convert.exe");

    public static string? FindCalibre()
    {
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "BookConverterRuntime"), ManagedRoot })
        {
            var candidate = Path.Combine(root, RelativeExecutable);
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "app", "bin"))) return candidate;
        }
        return null;
    }

    public static string? FindLibreOffice()
    {
        var bundled = FindBundledLibreOffice();
        if (bundled is not null) return bundled;
        var managed = FindManagedLibreOffice();
        if (managed is not null) return managed;
        var discovered = new SystemCliDiscoveryService().Find("convert.legacy_office")?.ExecutablePath;
        if (discovered is not null) return OfficeConsole(discovered);
        var installed = new ComponentManager().GetStatus().FirstOrDefault(x => x.Entry.Id == "runtime.libreoffice" && x.IsAvailable)?.Record.InstallPath;
        foreach (var root in new[] { installed, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var relative in new[] { Path.Combine("program", "soffice.exe"), Path.Combine("LibreOffice", "program", "soffice.exe"), "soffice.exe" })
                if (File.Exists(Path.Combine(root, relative))) return OfficeConsole(Path.Combine(root, relative));
        }
        return null;
    }

    private static string OfficeConsole(string executable)
    {
        var console = Path.ChangeExtension(executable, ".com");
        return File.Exists(console) ? console : executable;
    }

    public static bool IsCalibreLicenseAccepted
    {
        get
        {
            try
            {
                var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
                var entry = service.Entries.SingleOrDefault(x => x.Id == LicenseId);
                return entry is not null && service.ReadReceipts().Any(x => x.Id == LicenseId && x.Terms == entry.Terms);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
        }
    }

    public static async Task EnsureCalibreLicenseAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(LicenseId, token);
        var executable = FindCalibre();
        if (executable is null) return; // Interactive preparation owns downloading; quiet callers report missing component.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)))!;
        await VerificationGate.WaitAsync(token);
        try
        {
            if (root == _verifiedRoot) return;
            await using var manifest = typeof(BookConverterRuntime).Assembly.GetManifestResourceStream("AIHub.BookConverterManifest")
                ?? throw new InvalidDataException("Missing calibre file manifest.");
            using var doc = await JsonDocument.ParseAsync(manifest, cancellationToken: token);
            foreach (var entry in doc.RootElement.GetProperty("Files").EnumerateArray())
            {
                var path = Path.GetFullPath(Path.Combine(root, entry.GetProperty("Name").GetString()!));
                if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid calibre manifest path.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(path);
                await using var file = File.OpenRead(path);
                if (file.Length != entry.GetProperty("Bytes").GetInt64() || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Calibre file checksum mismatch: " + entry.GetProperty("Name").GetString());
            }
            _verifiedRoot = root;
        }
        finally { VerificationGate.Release(); }
    }

    /// <summary>Compatibility entry point: validates the shipped component; never downloads.</summary>
    public static async Task EnsureCalibreAsync(IProgress<string>? progress, CancellationToken token)
    {
        if (FindCalibre() is null) throw new FileNotFoundException("BookConverter.Error.CalibreRequired");
        await EnsureCalibreLicenseAsync(token);
    }

    private static async Task<bool> MatchesAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != DownloadBytes) return false;
        Lopata.Updates.SafeUpdatePath.RejectLinks(file);
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) == DownloadSha256;
    }

    internal static async Task ExtractPortableAsync(string installer, string destination, CancellationToken token)
    {
        // The official pinned executable stores a version-0 lzip ZIP at this PE resource offset.
        // Decode data only; do not execute the installer or its global path/running-app checks.
        if (!await MatchesAsync(installer, token)) throw new InvalidDataException("Unverified calibre installer.");
        var stage = destination + ".stage-" + Guid.NewGuid().ToString("N");
        var zipPath = stage + ".zip";
        Directory.CreateDirectory(stage);
        try
        {
            await using (var file = File.OpenRead(installer))
            {
                file.Position = 244040;
                var header = new byte[6]; await file.ReadExactlyAsync(header, token);
                if (!header.SequenceEqual(new byte[] { 76, 90, 73, 80, 0, 23 })) throw new InvalidDataException("Unexpected calibre resource.");
                var properties = new byte[] { 93, 0, 0, 128, 0 }; // lc=3/lp=0/pb=2, 8 MiB dictionary
                using var lzma = new LzmaStream(properties, file, 216858487 - 18, 695874913);
                await using var zip = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await lzma.CopyToAsync(zip, token);
                if (zip.Length != 695874913) throw new InvalidDataException("Incomplete calibre ZIP.");
            }
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                long bytes = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested(); bytes = checked(bytes + entry.Length);
                    if (zip.Entries.Count > 5000 || bytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Calibre archive exceeds its pinned profile.");
                    var output = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!output.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                        throw new InvalidDataException("Unsafe calibre archive entry.");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await using var input = entry.Open();
                    await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(target, token);
                }
            }
            if (!File.Exists(Path.Combine(stage, RelativeExecutable))) throw new InvalidDataException("Missing calibre executable.");
            // No deletion/replacement of an existing installation or a concurrent preparation.
            if (Directory.Exists(destination)) throw new IOException("Calibre destination already exists; inspect the incomplete installation before retrying.");
            Directory.Move(stage, destination);
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
