using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Durable, idempotent delivery of a generated PNG. Local delivery never implies human review.</summary>
public static class ImageGenerationExport
{
    private static readonly object Gate = new();

    public static ImageGenerationResult Save(ImageGenerationRequest request, ImageGenerationResult result, string? retryFolder = null)
    {
        lock (Gate)
        {
            result = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single(t => t.Request.Id == request.Id).Results.Single(r => r.Index == result.Index);
            var folder = retryFolder ?? request.OutputFolder;
            if (string.IsNullOrWhiteSpace(folder) || (result.Exported && retryFolder is null)) return result;
            var source = ImageGenerationSessionStore.ResultPath(request, result.Index);
            string? temporary = null;
            try
            {
                if (!Path.IsPathFullyQualified(folder)) throw new IOException("Generation.FolderRequired");
                Directory.CreateDirectory(folder);
                var target = result.ExportPath;
                if (retryFolder is not null && !string.Equals(Path.GetDirectoryName(target), folder, StringComparison.OrdinalIgnoreCase)) target = null;
                if (target is null || (File.Exists(target) && !SameContent(source, target)))
                {
                    var number = request.FirstGenerationNumber + result.Index;
                    do { target = Path.Combine(folder, FileName(request, number++)); } while (File.Exists(target));
                }
                // Commit intent before copying, so a restart can adopt an already delivered file.
                result = result with { ExportPath = target, Exported = false, ExportError = null };
                result = Persist(request, result);
                if (!File.Exists(target))
                {
                    temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var input = File.OpenRead(source))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    { input.CopyTo(output); output.Flush(true); }
                    File.Move(temporary, target, false);
                }
                result = result with { Exported = true };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { result = result with { Exported = false, ExportError = error.Message }; }
            finally
            {
                if (temporary is not null)
                { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            return Persist(request, result);
        }
    }

    private static ImageGenerationResult Persist(ImageGenerationRequest request, ImageGenerationResult result) =>
        ImageGenerationSessionStore.UpdateResult(request, result.Index, current => result with { Reviewed = current.Reviewed || result.Reviewed });

    public static string FileName(ImageGenerationRequest request, int number)
    {
        var name = ImageGenerationCatalog.DisplayName(request.ModelId);
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return name + "_" + (request.SubmittedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + number.ToString("D4", CultureInfo.InvariantCulture) + ".png";
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var left = File.OpenRead(a); using var right = File.OpenRead(b);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }
}
