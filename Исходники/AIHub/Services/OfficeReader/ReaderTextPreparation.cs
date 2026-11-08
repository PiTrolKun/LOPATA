using System.IO;
using System.Net.Http;
using AIHub.Models;

namespace AIHub.Services.OfficeReader;

/// <summary>Prepares weights through shared policy. It does not claim the model passed a runtime test.</summary>
internal sealed class ReaderTextPreparation(ManagedModelLibraryStore library, HttpClient? client = null) : IDisposable
{
    private readonly ManagedModelAcquisitionService _files = new(library, client);
    internal int ParallelConnections { set => _files.MaximumParallelConnections = value; }
    internal async Task<bool> PrepareAsync(ManagedModelArtifactCard card, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!card.IsManaged) throw new InvalidOperationException("Reader.Ai.ExternalFile");
        await ComponentLicenseGate.EnsureAsync(card.ModelArtifactId, token);
        library.Upsert(card);
        var result = await _files.VerifyAsync(card.ModelArtifactId, progress, token);
        if (!Prepared(result) && download)
        {
            if (!card.SupportsDirectDownload || card.Files.Where(f => f.IsRequired).Any(f => string.IsNullOrWhiteSpace(f.SourceUrl)))
                throw new InvalidOperationException("Reader.Ai.NoSource");
            result = await _files.DownloadAsync(card.ModelArtifactId, progress, token);
        }
        return Prepared(result);
    }
    private static bool Prepared(ManagedModelArtifactCard card) => card.Status == ManagedModelStatuses.Installed
        || card.Status == ManagedModelStatuses.NeedsVerification && card.LastVerifiedAt is not null;
    public void Dispose() => _files.Dispose();
}
