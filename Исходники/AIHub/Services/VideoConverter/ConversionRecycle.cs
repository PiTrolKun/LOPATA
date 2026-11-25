using System.IO;
using System.Security.Cryptography;
using Lopata.Updates;

namespace AIHub.Services.VideoConverter;

internal static class ConversionRecycle
{
    internal static async Task<bool> CheckAsync(ConversionItem item, CancellationToken token)
    {
        if (item is not { Status: ConversionStatus.Ready, Output: { } output, Source: { } source }
            || output.Path.Equals(item.Input, StringComparison.OrdinalIgnoreCase)) return false;
        SafeUpdatePath.RejectLinks(item.Input);
        if (!File.Exists(item.Input) || !await ConversionStore.VerifyFile(output.Path, output, token)) return false;
        await using var stream = File.OpenRead(item.Input);
        return await SameSource(item.Input, stream, source, token);
    }

    internal static async Task<string> RecycleAsync(ConversionItem item, nint owner, CancellationToken token)
    {
        if (item is not { Status: ConversionStatus.Ready, Output: { } output, Source: { } source }
            || output.Path.Equals(item.Input, StringComparison.OrdinalIgnoreCase))
            return "Исходник сохранён. Проверенный результат отсутствует.";
        SafeUpdatePath.RejectLinks(item.Input);
        SafeUpdatePath.RejectLinks(output.Path);
        // Keep the verified output open and prevent writes to both files while the Shell recycles the source.
        await using var ready = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var original = new FileStream(item.Input, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (ready.Length != output.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(ready, token)) != output.Sha256
            || !await SameSource(item.Input, original, source, token))
            return "Исходник сохранён. Исходник или результат изменился после обработки.";
        token.ThrowIfCancellationRequested();
        return ModelStorageWindowsCopy.RecycleFile(item.Input, owner) ? "Исходник отправлен в корзину Windows."
            : "Исходник сохранён. Для этого файла недоступна корзина Windows.";
    }

    private static async Task<bool> SameSource(string path, Stream file, ConversionSource source, CancellationToken token) =>
        file.Length == source.Size && File.GetLastWriteTimeUtc(path) == source.WriteUtc
        && Convert.ToHexString(await SHA256.HashDataAsync(file, token)) == source.Sha256;
}
