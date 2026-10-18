using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>Verified optional components and durable user choice, separate from the application update manifest.</summary>
public sealed partial class EngineSetService
{
    private static readonly Lazy<EngineSetService> Default = new(CreateDefault);
    public static EngineSetService Shared => Default.Value;
    private readonly EngineSetCatalog _catalog;
    private readonly string _statePath, _installedRoot, _bundledRoot, _cacheRoot;
    private readonly HttpClient _http;
    private readonly Func<bool> _isBusy;
    private readonly Func<IReadOnlyCollection<string>> _requiredCapabilities;
    private readonly IReadOnlyDictionary<string, string> _legacyDirectories;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineSetState? _state;

    public EngineSetService(EngineSetCatalog catalog, IReadOnlyDictionary<string, string> trustedKeys,
        string statePath, string installedRoot, string bundledRoot, HttpClient http,
        Func<bool>? isBusy = null, Func<IReadOnlyCollection<string>>? requiredCapabilities = null,
        IReadOnlyDictionary<string, string>? legacyDirectories = null)
    {
        catalog.Validate(trustedKeys);
        _catalog = catalog; _statePath = Path.GetFullPath(statePath);
        _installedRoot = Path.GetFullPath(installedRoot); _bundledRoot = Path.GetFullPath(bundledRoot);
        _cacheRoot = Path.Combine(Path.GetDirectoryName(_statePath)!, "EngineSetDownloads");
        _http = http; _isBusy = isBusy ?? (() => false);
        _requiredCapabilities = requiredCapabilities ?? (() => []);
        _legacyDirectories = legacyDirectories ?? new Dictionary<string, string>();
    }

    public IReadOnlyList<EngineSetDefinition> GetCatalog() => _catalog.Sets.ToArray();
    public EngineSetState GetState() => _state ?? throw new InvalidOperationException("Initialize engine sets first.");
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not null) return;
            if (File.Exists(_statePath))
            {
                SafeUpdatePath.RejectLinks(_statePath);
                if (new FileInfo(_statePath).Length > 64 * 1024) throw new InvalidDataException("Invalid engine selection state.");
                var saved = JsonSerializer.Deserialize<EngineSetState>(await File.ReadAllBytesAsync(_statePath, ct), EngineSetCatalog.JsonOptions)
                    ?? throw new InvalidDataException("Missing engine selection state.");
                _ = Find(saved.ActiveId);
                _state = saved;
            }
            else
            {
                var legacy = _catalog.Sets.SingleOrDefault(s => s.Id == "001");
                var migrated = legacy is not null && _legacyDirectories.Count > 0 && await MigrateLegacyAsync(legacy, ct);
                var first = legacy is not null && (migrated || await VerifyAsync(legacy, ct)) ? legacy.Id : _catalog.RecommendedId;
                Save(new(first));
            }
            _ = await VerifyAsync(Find(_state!.ActiveId), ct);
            EngineSetSelection.Publish(_state!.ActiveId, _catalog.RecommendedId);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetInstalledAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        foreach (var set in _catalog.Sets) if (await VerifyAsync(set, ct)) result.Add(set.Id);
        return result;
    }
    public async Task<EngineSetDefinition?> GetOfferAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var state = GetState();
        var recommended = Find(_catalog.RecommendedId);
        return state.ActiveId != recommended.Id && state.DeclinedRecommendation != recommended.Id
            && await VerifyAsync(recommended, ct) ? recommended : null;
    }
    public async Task ActivateAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_isBusy()) throw new InvalidOperationException("An operation still owns its engine set.");
            var set = Find(id);
            var missing = _requiredCapabilities().Except(set.SupportedCapabilities, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0) throw new InvalidDataException("The selected set does not support: " + string.Join(", ", missing));
            var verified = await VerifyAsync(set, ct);
            ct.ThrowIfCancellationRequested();
            if (!verified) throw new InvalidDataException("The selected engine set is missing or damaged. Download it again.");
            if (_isBusy()) throw new InvalidOperationException("An operation started before the switch completed.");
            ct.ThrowIfCancellationRequested();
            Save(GetState() with { ActiveId = id, ManualSelection = true,
                DeclinedRecommendation = id == _catalog.RecommendedId ? id : GetState().DeclinedRecommendation });
            EngineSetSelection.Publish(id, _catalog.RecommendedId);
        }
        finally { _gate.Release(); }
    }
    public void Decline(string id)
    {
        _ = Find(id);
        _gate.Wait();
        try { Save(GetState() with { DeclinedRecommendation = id }); }
        finally { _gate.Release(); }
    }
    private EngineSetDefinition Find(string id) => _catalog.Sets.SingleOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("Unknown engine set: " + id);
    private async Task<bool> VerifyAsync(EngineSetDefinition set, CancellationToken ct)
    {
        foreach (var parent in new[] { _installedRoot, _bundledRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (await VerifyAtAsync(set, SafeUpdatePath.Resolve(parent, set.Id), ct))
            { EngineSetSelection.RegisterDirectory(set.Id, SafeUpdatePath.Resolve(parent, set.Id)); return true; }
        return false;
    }
    private static async Task<bool> VerifyAtAsync(EngineSetDefinition set, string root, CancellationToken ct)
    {
        try
        {
            foreach (var file in set.VerifiedManifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = SafeUpdatePath.Resolve(root, file.Path);
                if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, file, ct)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    private void Save(EngineSetState value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        SafeUpdatePath.RejectLinks(_statePath);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(file, value, EngineSetCatalog.JsonOptions); file.Flush(true); }
        File.Move(temporary, _statePath, true);
        _state = value;
    }
}
