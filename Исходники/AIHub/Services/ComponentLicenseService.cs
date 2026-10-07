using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record ComponentLicenseEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string License { get; init; } = "";
    public string Source { get; init; } = "";
    public string Checked { get; init; } = "";
    public string Ru { get; init; } = "";
    public string En { get; init; } = "";
    public string Delivery { get; init; } = "download";
    public bool Basic { get; init; }
    public string Terms { get; init; } = "1";
    public string[] Texts { get; init; } = [];
}

public sealed record ComponentLicenseReceipt(string Id, string Terms, DateTimeOffset AcceptedAt,
    string Source, string AppVersion);

/// <summary>Local acknowledgements, not license interpretation or anti-tamper protection.</summary>
public sealed class ComponentLicenseService(string directory, string statePath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyList<ComponentLicenseEntry> Entries { get; } =
        JsonSerializer.Deserialize<List<ComponentLicenseEntry>>(File.ReadAllText(Path.Combine(directory, "catalog.json")))
        ?? throw new InvalidDataException("License catalog is empty.");

    public static IReadOnlyList<string> ExpandSelection(IEnumerable<string> requested, IEnumerable<ComponentLicenseEntry> catalog)
    {
        var ids = requested.Distinct(StringComparer.Ordinal).ToArray();
        return ids.Where(id => id != "basic")
            .Concat(ids.Contains("basic", StringComparer.Ordinal)
                ? catalog.Where(entry => entry.Basic).Select(entry => entry.Id) : [])
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ComponentLicenseReceipt> ReadReceipts()
    {
        return ReadFile(statePath).Concat(ReadFile(Path.Combine(Path.GetDirectoryName(statePath)!, "installer-receipts.json"))).ToList();
    }

    private static IReadOnlyList<ComponentLicenseReceipt> ReadFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<ComponentLicenseReceipt>>(File.ReadAllText(path)) ?? [])
                    .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Terms))
                    .ToList() : [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public string ReadText(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("License text path escapes the catalog.");
        return File.ReadAllText(path);
    }

    public async Task EnsureAsync(IEnumerable<string> ids,
        Func<IReadOnlyList<ComponentLicenseEntry>, Task<bool>> confirm, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var selected = ids.Distinct(StringComparer.Ordinal).Select(id =>
                Entries.SingleOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException($"Component license is not registered: {id}")).ToList();
            var receipts = ReadReceipts().ToList();
            var pending = selected.Where(x => !receipts.Any(r => r.Id == x.Id && r.Terms == x.Terms)).ToList();
            if (pending.Count == 0) return;
            if (!await confirm(pending)) throw new OperationCanceledException("Component acknowledgement declined.", token);
            token.ThrowIfCancellationRequested();
            foreach (var entry in pending)
            {
                receipts.RemoveAll(x => x.Id == entry.Id);
                receipts.Add(new(entry.Id, entry.Terms, DateTimeOffset.UtcNow, "application",
                    typeof(ComponentLicenseService).Assembly.GetName().Version?.ToString() ?? ""));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipts), token);
                File.Move(temporary, statePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
}

/// <summary>The desktop host installs one gate; headless service consumers can provide their own policy.</summary>
public static class ComponentLicenseGate
{
    public static Func<IReadOnlyList<string>, CancellationToken, Task>? ConfirmAsync { get; set; }
    public static Task EnsureAsync(string id, CancellationToken token) => EnsureAsync([id], token);
    public static Task EnsureAsync(IReadOnlyList<string> ids, CancellationToken token) =>
        ConfirmAsync?.Invoke(ids, token) ?? Task.CompletedTask;
}
