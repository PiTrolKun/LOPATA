using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

/// <summary>Binds even a finished worker receipt to immutable inputs before any fast reuse.</summary>
internal static class H3MusicAttemptBinding
{
    private const string MotionWorker = "AIHub.SongClip.MotionWorker";
    // The sole pre-semantic-planning worker accepted for PlanningVersion 0.
    // Other adapters, component pins, inputs and settings must match exactly.
    private const string LegacyMotionWorkerHash = "5A75C63AA6F1A947689F995E8C5BAA5185DC591303F9059024C4B26ADF384CFE";
    private const string SingleReferenceWorkerHash = "685A48AD75E18C61B337D244054BD9525C7A1A2D1C435ED8CD473C50753ED7B5";
    private sealed record Receipt(int Schema, string InputHash);
    internal static async Task EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var adapters = new Dictionary<string, string>();
        foreach (var resource in new[] { "AIHub.SongClip.MotionWorker", "AIHub.SongClip.MotionState", "AIHub.H3Memory" })
        {
            using var input = typeof(H3MusicAttemptBinding).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("Missing H3 music adapter identity.");
            adapters[resource] = Convert.ToHexString(SHA256.HashData(input));
        }
        var identity = new
        {
            attempt,
            songSha256 = await HashAsync(store.Resolve(attempt.ProjectId, attempt.Song), token),
            referenceSha256 = await SongClipReferences.HashAsync(attempt, store, token),
            pins = cards.OrderBy(c => c.ModelArtifactId, StringComparer.Ordinal).Select(c => new
            {
                c.ModelArtifactId, c.Revision,
                files = c.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256 })
            }),
            adapters
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
        var binding = RuntimeFileAudit.Contained(job, "input-binding.json");
        if (File.Exists(binding))
        {
            var prior = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(binding, token));
            if (prior is null || prior.Schema != 1)
                throw new InvalidDataException("H3 music attempt inputs changed; existing results were preserved.");
            if (prior.InputHash == hash) return;
            if (attempt.References is not null)
                throw new InvalidDataException("H3 music attempt inputs changed; existing results were preserved.");
            string HashWith(Dictionary<string, string> known) => Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(identity with { adapters = known })));
            var knownHashes = attempt.PlanningVersion == 0 ? new[] { SingleReferenceWorkerHash, LegacyMotionWorkerHash }
                : new[] { SingleReferenceWorkerHash };
            var legacyAdapters = knownHashes.Select(known => new Dictionary<string, string>(adapters) { [MotionWorker] = known })
                .FirstOrDefault(known => prior.InputHash == HashWith(known));
            if (legacyAdapters is null)
                throw new InvalidDataException("H3 music attempt inputs changed; existing results were preserved.");
            var legacyHash = HashWith(legacyAdapters);
            var compatibility = new
            {
                Schema = 1, Policy = legacyAdapters[MotionWorker] == LegacyMotionWorkerHash
                    ? "known-pre-semantic-h3-motion-worker" : "known-single-reference-h3-motion-worker",
                PlanningVersion = attempt.PlanningVersion,
                LegacyInputHash = legacyHash, CurrentInputHash = hash,
                LegacyAdapters = legacyAdapters, CurrentAdapters = adapters
            };
            var compatibilityPath = RuntimeFileAudit.Contained(job, "input-binding-compatibility.json");
            if (File.Exists(compatibilityPath))
            {
                using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(compatibilityPath, token));
                if (!JsonElement.DeepEquals(recorded.RootElement, JsonSerializer.SerializeToElement(compatibility)))
                {
                    // A .94/.95 compatibility receipt may already bind the
                    // pre-semantic worker to the exact single-reference worker.
                    // Verify that whole receipt before adding a separate link;
                    // never rewrite the original evidence or accept other pins.
                    var priorAdapters = new Dictionary<string, string>(adapters) { [MotionWorker] = SingleReferenceWorkerHash };
                    var priorCompatibility = compatibility with { CurrentInputHash = HashWith(priorAdapters), CurrentAdapters = priorAdapters };
                    if (legacyAdapters[MotionWorker] != LegacyMotionWorkerHash
                        || !JsonElement.DeepEquals(recorded.RootElement, JsonSerializer.SerializeToElement(priorCompatibility)))
                        throw new InvalidDataException("H3 music binding compatibility evidence changed; existing results were preserved.");
                    var nextPath = RuntimeFileAudit.Contained(job, "input-binding-reference-compatibility.json");
                    if (File.Exists(nextPath)) {
                        using var next = JsonDocument.Parse(await File.ReadAllTextAsync(nextPath, token));
                        if (!JsonElement.DeepEquals(next.RootElement, JsonSerializer.SerializeToElement(compatibility)))
                            throw new InvalidDataException("H3 music reference compatibility evidence changed; existing results were preserved.");
                    }
                    else { token.ThrowIfCancellationRequested(); SongClipStore.Atomic(nextPath, compatibility); }
                }
            }
            else { token.ThrowIfCancellationRequested(); SongClipStore.Atomic(compatibilityPath, compatibility); }
            // Keep the original binding as the authoritative identity of old work.
            // This explicit side receipt records the narrow adapter compatibility.
            return;
        }
        // An existing result must never be adopted under a newly invented identity.
        if (new[] { "request.json", "motion-state.json", "worker-result.json", "clip.mp4" }
            .Any(name => File.Exists(RuntimeFileAudit.Contained(job, name))))
            throw new InvalidDataException("H3 music attempt has no input binding; existing results were preserved.");
        token.ThrowIfCancellationRequested(); SongClipStore.Atomic(binding, new Receipt(1, hash));
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
}
