using System.IO;
using AIHub.Services.LiteraryImport;

namespace AIHub.Services;

public sealed partial class LiteraryChapterStore
{
    /// <summary>One index commit activates the corrected book; legacy files are retained for recovery.</summary>
    public void ActivateImport(LiteraryChapterIndex next, Dictionary<string, string> texts,
        string previousTransaction, Dictionary<string, string> previousHashes, string activation)
    {
        using var lease = Enter(); RecoverTransaction(); Index = ReadIndex();
        if (Index.Transaction == activation) return;
        if (Index.Transaction != previousTransaction || previousHashes.Any(p => ImportSession.HashFile(Resolve(p.Key)) != p.Value))
            throw new IOException("Literary.Import.Workspace.SourceChanged");
        if (!Guid.TryParseExact(activation, "N", out _) || !texts.Keys.ToHashSet().SetEquals(next.Parts.Select(p => p.FileName)))
            throw new InvalidDataException("Invalid activation.");
        Validate(next);
        var backup = Path.Combine(Path.GetDirectoryName(_root)!, "Import", "BeforeWorkspace", activation);
        Directory.CreateDirectory(backup);
        foreach (var name in Index.Parts.Select(p => p.FileName).Append("index.json"))
        {
            var destination = Path.Combine(backup, name);
            if (File.Exists(destination))
            {
                if (ImportSession.HashFile(destination) != ImportSession.HashFile(Resolve(name)))
                    throw new IOException("Recovery copy changed.");
            }
            else File.Copy(Resolve(name), destination);
        }
        var review = Path.Combine(Path.GetDirectoryName(_root)!, "Import", "review.json");
        if (!File.Exists(Path.Combine(backup, "review.json"))) File.Copy(review, Path.Combine(backup, "review.json"));
        // Files outside the old index are not ours to overwrite.
        foreach (var name in texts.Keys)
            if (File.Exists(Resolve(name)) && !previousHashes.ContainsKey(name)) throw new IOException("Unowned chapter file.");
        var retired = Index.Parts.Select(p => p.FileName).Except(texts.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        Commit(next, texts, retired, activation);
    }
}
