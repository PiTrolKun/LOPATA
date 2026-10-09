using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ManagedModelLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _entriesDirectory;
    private readonly string _eventsPath;

    public ManagedModelLibraryStore(string? libraryDirectory = null)
    {
        var root = libraryDirectory ?? AppDataPaths.ManagedModelLibraryDirectory;
        _entriesDirectory = Path.Combine(root, "Entries");
        _eventsPath = Path.Combine(root, "events.jsonl");
    }

    public IReadOnlyList<ManagedModelArtifactCard> LoadAll()
    {
        EnsureDirectory();
        var cards = new List<ManagedModelArtifactCard>();
        foreach (var path in Directory.EnumerateFiles(_entriesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var card = JsonSerializer.Deserialize<ManagedModelArtifactCard>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions);
                if (card is null || string.IsNullOrWhiteSpace(card.ModelArtifactId))
                {
                    Log("entry_skipped", Path.GetFileNameWithoutExtension(path), "The entry is empty or has no ID.");
                    continue;
                }

                Normalize(card);
                cards.Add(card);
            }
            catch (Exception ex)
            {
                Log("entry_corrupted", Path.GetFileNameWithoutExtension(path), ex.Message);
            }
        }

        return cards
            .OrderBy(card => card.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => card.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ManagedModelArtifactCard? Load(string modelArtifactId)
    {
        var path = GetEntryPath(modelArtifactId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var card = JsonSerializer.Deserialize<ManagedModelArtifactCard>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions);
            if (card is not null)
            {
                Normalize(card);
            }
            return card;
        }
        catch (Exception ex)
        {
            Log("entry_corrupted", modelArtifactId, ex.Message);
            return null;
        }
    }

    public ManagedModelArtifactCard Upsert(ManagedModelArtifactCard candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Normalize(candidate);
        candidate.ModelArtifactId = string.IsNullOrWhiteSpace(candidate.ModelArtifactId)
            ? CreateStableId(candidate)
            : candidate.ModelArtifactId.Trim();

        var existing = Load(candidate.ModelArtifactId);
        if (existing is null)
        {
            existing = LoadAll().FirstOrDefault(card => SameArtifactCoordinates(card, candidate));
            if (existing is not null)
            {
                candidate.ModelArtifactId = existing.ModelArtifactId;
            }
        }
        if (existing is not null)
        {
            var sameDirectory = ManagedModelPathIdentity.SameDirectory(existing.InstallDirectory, candidate.InstallDirectory);
            candidate.FirstDiscoveredAt = existing.FirstDiscoveredAt;
            candidate.FirstInstalledAt ??= existing.FirstInstalledAt;
            if (sameDirectory)
            {
                candidate.LastVerifiedAt ??= existing.LastVerifiedAt;
                candidate.RuntimeVerifiedAt ??= existing.RuntimeVerifiedAt;
            }
            else
            {
                candidate.LastVerifiedAt = null; candidate.RuntimeVerifiedAt = null;
                if (candidate.Status == ManagedModelStatuses.Installed) candidate.Status = ManagedModelStatuses.NeedsVerification;
                foreach (var file in candidate.Files) { file.VerifiedSizeBytes = 0; file.VerifiedLastWriteTimeUtc = null; }
            }
            if (candidate.SemanticPassport.Status == ModelSemanticPassportStatuses.Missing)
            {
                candidate.SemanticPassport = existing.SemanticPassport;
            }
            if (existing.Origin == ManagedModelOrigins.Sandbox
                && candidate.Origin == ManagedModelOrigins.ExistingManifest)
            {
                candidate.Origin = existing.Origin;
                candidate.Discovery = existing.Discovery;
            }
            foreach (var file in candidate.Files)
            {
                var previousFile = existing.Files.FirstOrDefault(item => string.Equals(
                    item.RelativePath,
                    file.RelativePath,
                    StringComparison.OrdinalIgnoreCase));
                if (sameDirectory && previousFile is not null && file.VerifiedLastWriteTimeUtc is null)
                {
                    file.VerifiedSizeBytes = previousFile.VerifiedSizeBytes;
                    file.VerifiedLastWriteTimeUtc = previousFile.VerifiedLastWriteTimeUtc;
                }
            }

            candidate.Consumers = existing.Consumers
                .Concat(candidate.Consumers)
                .GroupBy(consumer => consumer.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
        }

        SaveAtomic(GetEntryPath(candidate.ModelArtifactId), candidate);
        Log(existing is null ? "entry_created" : "entry_updated", candidate.ModelArtifactId, candidate.Status);
        return candidate;
    }

    public ManagedModelArtifactCard RegisterDynamicArtifact(
        ManagedModelArtifactCard card,
        string scenarioId,
        string sessionId,
        string searchSnapshot,
        string selectionReason,
        string appVersion)
    {
        ArgumentNullException.ThrowIfNull(card);
        card.Origin = ManagedModelOrigins.Sandbox;
        card.IsManaged = true;
        card.Status = ManagedModelStatuses.AwaitingConfirmation;
        card.Discovery = new ManagedModelDiscoveryProvenance
        {
            ScenarioId = scenarioId,
            SessionId = sessionId,
            SearchSnapshot = searchSnapshot,
            SelectionReason = selectionReason,
            AppVersion = appVersion
        };
        return Upsert(card);
    }

    public static string CreateStableId(ManagedModelArtifactCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var identity = string.Join(
            "\n",
            card.Provider.Trim().ToLowerInvariant(),
            card.RepositoryId.Trim().ToLowerInvariant(),
            card.Revision.Trim().ToLowerInvariant(),
            string.Join(
                "\n",
                card.Files
                    .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(file => $"{file.RelativePath.Trim().ToLowerInvariant()}|{file.SizeBytes}|{file.Sha256.Trim().ToLowerInvariant()}")));
        return "model-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];
    }

    internal void ResetMusicInstallationReceipt(string artifactId, string status, long storedBytes)
    {
        var card = Load(artifactId) ?? throw new InvalidOperationException("Model card disappeared during removal.");
        card.Status = status; card.StoredBytes = storedBytes;
        card.LastVerifiedAt = null; card.RuntimeVerifiedAt = null; card.LastError = "";
        foreach (var file in card.Files) { file.VerifiedSizeBytes = 0; file.VerifiedLastWriteTimeUtc = null; }
        SaveAtomic(GetEntryPath(artifactId), card);
        Log("music_removal_state", artifactId, status);
    }

    private void SaveAtomic(string path, ManagedModelArtifactCard card)
    {
        EnsureDirectory();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(card, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetEntryPath(string modelArtifactId)
    {
        if (string.IsNullOrWhiteSpace(modelArtifactId)
            || modelArtifactId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Model artifact ID contains unsafe characters.");
        }
        return Path.Combine(_entriesDirectory, modelArtifactId + ".json");
    }

    private static void Normalize(ManagedModelArtifactCard card)
    {
        card.Files ??= [];
        card.Consumers ??= [];
        card.Discovery ??= new ManagedModelDiscoveryProvenance();
        card.SemanticPassport ??= new ModelSemanticPassport();
        card.SchemaVersion = Math.Max(1, card.SchemaVersion);
    }

    private static bool SameArtifactCoordinates(
        ManagedModelArtifactCard first,
        ManagedModelArtifactCard second) =>
        !string.IsNullOrWhiteSpace(first.RepositoryId)
        && string.Equals(first.RepositoryId, second.RepositoryId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Revision, second.Revision, StringComparison.OrdinalIgnoreCase)
        && first.Files.Count == second.Files.Count
        && first.Files
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Zip(second.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            .All(pair => string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase)
                && pair.First.SizeBytes == pair.Second.SizeBytes);

    private void EnsureDirectory() => Directory.CreateDirectory(_entriesDirectory);

    private void Log(string action, string modelArtifactId, string detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_eventsPath)!);
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                action,
                modelArtifactId,
                detail
            });
            File.AppendAllText(_eventsPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // A diagnostic log must never make the model library unavailable.
        }
    }
}
