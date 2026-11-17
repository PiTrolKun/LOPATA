namespace Lopata.Updates;

public sealed record CoreVoiceRestorePreview(CoreVoiceSelection Current, CoreVoiceSelection Saved);

/// <summary>Backs up only the values involved in voice preference migration.</summary>
public sealed class CoreVoiceRollbackStore(string updateStateDirectory, CoreVoiceProfileStore profile)
{
    public IDisposable? TryBeginReport(string transactionId)
    {
        var path = PathFor(transactionId, "report.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    // The application holds the profile lock while recording and performing its migration.
    public void Capture(string transactionId, CoreVoiceSelection before)
    {
        var path = PathFor(transactionId, "backup.json");
        if (!File.Exists(path)) DurableUpdateFiles.WriteJson(path, before);
    }

    public CoreVoiceRestorePreview? Preview(string transactionId)
    {
        using var lease = profile.Acquire();
        return ReadPreview(transactionId);
    }

    public bool TryRestore(string transactionId, CoreVoiceRestorePreview confirmed,
        out CoreVoiceRestorePreview? fresh)
    {
        using var lease = profile.Acquire();
        fresh = ReadPreview(transactionId);
        if (fresh != confirmed) return false;

        var saved = confirmed.Saved.ForCompatibleStorage();
        profile.SavePreference(saved.PreferredProvider);
        profile.SaveCompatibleProvider(saved.CompatibleProvider);
        return true;
    }

    public bool WasReported(string transactionId) => File.Exists(PathFor(transactionId, "reported.json"));

    public void MarkReported(string transactionId) =>
        DurableUpdateFiles.WriteJson(PathFor(transactionId, "reported.json"), true);

    private CoreVoiceRestorePreview? ReadPreview(string transactionId)
    {
        var path = PathFor(transactionId, "backup.json");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var saved = System.Text.Json.JsonSerializer.Deserialize<CoreVoiceSelection>(stream, UpdateManifest.JsonOptions)
            ?? throw new InvalidDataException("Invalid voice settings backup.");
        var current = profile.Read();
        return current != saved.ForCompatibleStorage() ? new(current, saved) : null;
    }

    private string PathFor(string transactionId, string suffix)
    {
        if (!Guid.TryParseExact(transactionId, "N", out var id))
            throw new InvalidDataException("Invalid voice settings transaction.");
        return SafeUpdatePath.Resolve(updateStateDirectory, $"settings-recovery/{id:N}.{suffix}");
    }
}
