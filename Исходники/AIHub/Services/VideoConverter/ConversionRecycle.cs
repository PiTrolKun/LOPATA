using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Lopata.Updates;

namespace AIHub.Services.VideoConverter;

internal static class ConversionRecycle
{
    internal static async Task<bool> CheckAsync(ConversionJob job, ConversionItem item, CancellationToken token)
    {
        if (Result(job, item) is not { } result) return false;
        SafeUpdatePath.RejectLinks(item.Input);
        SafeUpdatePath.RejectLinks(result.Path);
        if (!File.Exists(item.Input) || !File.Exists(result.Path)) return false;
        await using var ready = File.OpenRead(result.Path);
        await using var original = File.OpenRead(item.Input);
        return await ResultMatches(ready, result.Entry, item.Output!, token)
            && await SameSource(item.Input, original, item.Source!, token);
    }

    internal static async Task<string> RecycleAsync(ConversionJob job, ConversionItem item, nint owner, CancellationToken token)
    {
        if (Result(job, item) is not { } result)
            return "Исходник сохранён. Проверенный результат отсутствует.";
        SafeUpdatePath.RejectLinks(item.Input);
        SafeUpdatePath.RejectLinks(result.Path);
        // Hold the saved file or archive open while the Shell recycles the source.
        await using var ready = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var original = new FileStream(item.Input, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!await ResultMatches(ready, result.Entry, item.Output!, token)
            || !await SameSource(item.Input, original, item.Source!, token))
            return "Исходник сохранён. Исходник или результат изменился после обработки.";
        token.ThrowIfCancellationRequested();
        return ModelStorageWindowsCopy.RecycleFile(item.Input, owner) ? "Исходник отправлен в корзину Windows."
            : "Исходник сохранён. Для этого файла недоступна корзина Windows.";
    }

    private static (string Path, string? Entry)? Result(ConversionJob job, ConversionItem item)
    {
        if (item is not { Status: ConversionStatus.Ready, Output: { } output, Source: not null }
            || ConversionStore.DeliveredPath(job, item) is not { } path
            || path.Equals(item.Input, StringComparison.OrdinalIgnoreCase)) return null;
        return (path, output.ArchiveName is null ? null : ConversionStore.ArchivedEntry(job.Archive, item)?.Name);
    }

    private static async Task<bool> ResultMatches(Stream ready, string? archiveEntry, ConversionArtifact output, CancellationToken token)
    {
        if (archiveEntry is null)
            return ready.Length == output.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(ready, token)) == output.Sha256;
        using var zip = new ZipArchive(ready, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.GetEntry(archiveEntry) is not { } member || member.Length != output.Bytes) return false;
        await using var video = member.Open();
        return Convert.ToHexString(await SHA256.HashDataAsync(video, token)) == output.Sha256;
    }

    private static async Task<bool> SameSource(string path, Stream file, ConversionSource source, CancellationToken token) =>
        file.Length == source.Size && File.GetLastWriteTimeUtc(path) == source.WriteUtc
        && Convert.ToHexString(await SHA256.HashDataAsync(file, token)) == source.Sha256;
}
