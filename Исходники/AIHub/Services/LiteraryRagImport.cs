using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>The same checked import is used for reference books, fixed parts and migration.</summary>
public static class LiteraryRagImport
{
    public static async Task<int> ReplaceAsync(QdrantRuntime runtime, string id, string vectorsPath,
        IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        // Validate the complete artifact before replacing a previously committed collection.
        var count = 0;
        var ids = new HashSet<string>();
        using (var input = File.OpenText(vectorsPath))
            while (await input.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line); LiterarySourceIndex.ValidatePoint(doc.RootElement);
                if (!ids.Add(doc.RootElement.GetProperty("id").ToString())) throw new InvalidDataException("Duplicate vector identifier.");
                count++;
            }
        if (count == 0) throw new InvalidDataException("Empty vector artifact.");
        await runtime.DeleteLiteraryIndexAsync(id, ct);
        await runtime.CreateLiteraryIndexAsync(id, ct);
        var batch = new List<JsonElement>(); var written = 0; JsonElement first = default;
        using (var input = File.OpenText(vectorsPath))
            while (await input.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (written == 0 && batch.Count == 0) first = doc.RootElement.Clone();
                batch.Add(doc.RootElement.Clone());
                if (batch.Count == 32) await Flush();
            }
        if (batch.Count > 0) await Flush();
        if (await runtime.LiteraryPointCountAsync(id, ct) != count) throw new InvalidDataException("Qdrant count mismatch.");
        await runtime.VerifyLiterarySearchAsync(id, first, ct);
        return count;
        async Task Flush()
        {
            await runtime.WriteLiteraryPointsAsync(id, batch, ct); written += batch.Count; batch.Clear();
            progress.Report(new("Writing", 100.0 * written / count, written.ToString()));
        }
    }
}
