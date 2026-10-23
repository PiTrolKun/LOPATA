using System.IO;

namespace AIHub.Services.VectorGraphics;

public sealed record VectorPreparationState(bool RendererReady, bool ModelReady)
{
    public bool Ready => RendererReady && ModelReady;
}

public static class VectorPreparation
{
    public const string BackgroundKind = "vector-graphics.prepare";
    public static Task<VectorPreparationState> CheckAsync(IProgress<double> progress, CancellationToken token) =>
        CheckAsync(VectorModels.Ridge, progress, token);

    public static async Task<VectorPreparationState> CheckAsync(string modelId, IProgress<double> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        _ = VectorModels.Validate(modelId);
        return await CheckComponentsAsync(VectorRenderer.VerifyAsync,
            ct => LiteraryModelLocation.ResolveAsync(ct, progress: progress), token);
    }

    internal static async Task<VectorPreparationState> CheckComponentsAsync(
        Func<CancellationToken, Task> renderer, Func<CancellationToken, Task<string>> model, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var rendererReady = true; var modelReady = true;
        try { await renderer(token); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { ComponentAuditFailures.Report(error); rendererReady = false; }
        try { _ = await model(token); }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
        { ComponentAuditFailures.Report(error); modelReady = false; }
        token.ThrowIfCancellationRequested();
        return new(rendererReady, modelReady);
    }

    public static Task<string> RunAsync(IProgress<double> progress, CancellationToken token) => PrepareAsync(true, progress, token);
    public static async Task<string> PrepareAsync(bool download, IProgress<double> progress, CancellationToken token)
        => await PrepareAsync(VectorModels.Ridge, download, progress, token);

    public static async Task<string> PrepareAsync(string modelId, bool download, IProgress<double> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync([VectorModels.ArtifactId(modelId), VectorRenderer.TermsId], token);
        await VectorRenderer.VerifyAsync(token);
        try { return await LiteraryModelLocation.ResolveAsync(token); }
        catch (Exception error) when (download && error is FileNotFoundException or InvalidDataException)
        {
            var path = LiteraryModelLocation.DownloadPath;
            await LiteraryArtifactDownload.GetAsync(new Uri(LiteraryModelLocation.DownloadUrl), path,
                LiteraryModelLocation.SizeBytes, LiteraryModelLocation.Sha256, "sha256", progress, token);
            return await LiteraryModelLocation.ResolveAsync(token);
        }
    }
}
