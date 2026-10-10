using System.IO;
using System.Net.Http;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>Connects delivery selection and durable consent to the UI without changing installed files.</summary>
public sealed class ApplicationUpdateCoordinator
{
    private readonly HttpClient _http;
    private readonly ApplicationUpdateService _installers;
    private readonly string _updatesDirectory;
    private readonly PreparedUpdateStore _prepared;
    public InstalledUpdateState? Installation { get; }
    public bool CanSchedule => Installation is not null && File.Exists(InstalledUpdateState.LauncherPath);

    public ApplicationUpdateCoordinator(HttpClient http, string updatesDirectory)
    {
        _http = http; _updatesDirectory = updatesDirectory;
        _installers = new(http, updatesDirectory);
        var registered = InstalledUpdateState.Read();
        if (AppDataPaths.ProjectRoot is null && registered is not null
            && Path.GetFullPath(registered.AppDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            Installation = registered;
        _prepared = new(Installation?.StateDirectory ?? Path.Combine(updatesDirectory, "Unregistered"), UpdateReleaseKeys.Trusted);
    }

    public UpdateDelivery? ReadDirection() => UpdateChannelStore.Read(_updatesDirectory);
    public void SaveDirection(UpdateDelivery delivery) => UpdateChannelStore.Save(_updatesDirectory, delivery);
    public PreparedApplicationUpdate? ReadPrepared() => _prepared.Read();
    public void Schedule(bool nextLaunch)
    {
        if (nextLaunch && !CanSchedule) throw new InvalidOperationException("Install the transition release before scheduling updates.");
        var prepared = _prepared.Read() ?? throw new InvalidOperationException("No verified update is prepared.");
        _prepared.Save(prepared with { ApplyOnNextLaunch = nextLaunch });
    }
    public void CancelPrepared() => _prepared.Clear();

    public Task<UpdateOffer?> CheckAsync(string current, UpdateDelivery delivery, CancellationToken token) =>
        new PublishedUpdateCatalog(_http, UpdateReleaseKeys.Trusted).CheckAsync(current, delivery, token);

    public static UpdateOffer PreparedOffer(PreparedApplicationUpdate prepared)
    {
        var manifest = prepared.Files?.Verify(UpdateReleaseKeys.Trusted);
        return new(prepared.Version, prepared.Delivery, prepared.Notes ?? manifest?.Notes ?? [],
            prepared.Installer, prepared.Files, manifest, prepared.NotesIncomplete);
    }

    public async Task<long> DownloadSizeAsync(UpdateOffer offer, CancellationToken token)
    {
        if (offer.Installer is { } installer) return installer.Size;
        if (Installation is null) return offer.Files?.Packages.Sum(p => p.Size) ?? 0;
        return (await PlanAsync(offer, token)).DownloadBytes;
    }

    private async Task<UpdatePlan> PlanAsync(UpdateOffer offer, CancellationToken token)
    {
        var installation = Installation ?? throw new InvalidOperationException("A transition installer is required.");
        var transaction = new UpdateTransaction(installation.Roots(), UpdateReleaseKeys.Trusted, installation.StateDirectory);
        var previous = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath, token)).Verify(UpdateReleaseKeys.Trusted);
        return await Task.Run(() => UpdatePlanner.CreateAsync(previous,
            offer.SignedFiles!.Verify(UpdateReleaseKeys.Trusted), installation.Roots(), token), token);
    }

    public async Task PrepareAsync(UpdateOffer offer, int connections, IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken token)
    {
        if (_prepared.Read() is not null) throw new InvalidOperationException("Decide what to do with the prepared release first.");
        PreparedApplicationUpdate prepared;
        if (offer.Installer is { } installer && offer.Delivery == UpdateDelivery.FullInstaller)
        {
            var legacy = ToInstallerUpdate(offer);
            var path = await _installers.DownloadAsync(legacy, connections, progress, token);
            prepared = new(offer.Version, offer.Delivery, installer, path, null, false, offer.Notes, offer.NotesIncomplete);
        }
        else
        {
            var plan = await PlanAsync(offer, token);
            var completed = new UpdateBatchProgress(plan.Packages);
            var transfer = new Progress<UpdateTransferProgress>(p =>
            {
                var batch = completed.Accept(p);
                progress?.Report(new("application-update", p.Package, batch.Bytes, plan.DownloadBytes, 0, batch.Stage));
            });
            await Task.Run(() => new FileUpdatePreparation(_http, UpdateReleaseKeys.Trusted)
                { MaximumParallelConnections = connections }
                .StageAsync(Installation!, offer.SignedFiles!, transfer, token), token);
            prepared = new(offer.Version, offer.Delivery, null, null, offer.SignedFiles, false, offer.Notes, offer.NotesIncomplete);
        }
        _prepared.Save(prepared);
    }

    public static ApplicationUpdate ToInstallerUpdate(UpdateOffer offer)
    {
        var file = offer.Installer ?? throw new InvalidOperationException("Not an installer update.");
        return new(offer.Version, file.FileName, file.Url, file.Size, file.Sha256, "");
    }
}
