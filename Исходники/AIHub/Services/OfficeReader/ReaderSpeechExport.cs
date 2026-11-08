using System.IO;

namespace AIHub.Services.OfficeReader;

internal static class ReaderSpeechExport
{
    internal static async Task SaveAsync(string audio, string destination, string document)
    {
        var target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || target.Equals(Path.GetFullPath(document), StringComparison.OrdinalIgnoreCase)
            || target.Equals(Path.GetFullPath(audio), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reader.Speech.SaveWav");
        Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Preserve the actual synthesis provenance already embedded in the WAV.
            await using (var input = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await input.CopyToAsync(output); await output.FlushAsync(); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
