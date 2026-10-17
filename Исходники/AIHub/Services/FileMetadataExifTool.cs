using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

internal sealed class FileMetadataExifTool
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _verified;
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "MetadataRuntime");
    internal static async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("metadata.exiftool", token);
        await Gate.WaitAsync(token);
        try
        {
            if (_verified) return;
            using var stream = typeof(FileMetadataExifTool).Assembly.GetManifestResourceStream("AIHub.MetadataExifToolManifest")
                ?? throw new InvalidDataException("Metadata.RuntimeMissing");
            var files = JsonSerializer.Deserialize<PinnedTreeFile[]>(stream) ?? throw new InvalidDataException("Metadata.RuntimeMissing");
            await PinnedFileTreeVerifier.VerifyAsync(DirectoryPath, files, token);
            _verified = true;
        }
        finally { Gate.Release(); }
    }
    internal static async Task<JsonElement> ReadAsync(string path, CancellationToken token)
    {
        await PrepareAsync(token);
        var data = await InvokeAsync(["-j", "-G1:4", "-a", "-s", "-u", "-struct", "-b", path], token);
        using var json = JsonDocument.Parse(data);
        return json.RootElement[0].Clone();
    }
    internal static async Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        var files = new List<string>();
        try
        {
            var arguments = new List<string> { "-overwrite_original" };
            foreach (var (key, value) in values)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(key,@"\A[A-Za-z0-9:-]+\z")) throw new InvalidDataException("Metadata.Protected");
                var file = path + "." + Guid.NewGuid().ToString("N") + ".value"; files.Add(file);
                // Raw UTF-8 files avoid ExifTool's JSON surrogate-pair conversion and argfile injection.
                await File.WriteAllTextAsync(file,value,new UTF8Encoding(false),token);
                arguments.Add("-" + key + "<=" + file);
            }
            arguments.Add(path); _ = await InvokeAsync(arguments, token);
        }
        finally { foreach(var file in files) if(File.Exists(file)) File.Delete(file); }
    }
    private static Task<byte[]> InvokeAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var args = arguments.ToArray();
        if (args.Any(a => a.Contains('\n') || a.Contains('\r'))) throw new InvalidDataException("Metadata.InvalidPath");
        return FileMetadataProcess.RunAsync(Path.Combine(DirectoryPath, "exiftool.exe"),
            ["-config", "", "-charset", "filename=UTF8", "-@", "-"], token, string.Join('\n', args) + "\n");
    }
}
