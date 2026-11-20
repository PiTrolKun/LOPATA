using System.IO;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal static class MiniMaxImageRuntime
{
    internal const string ModelId = "minimax-h3-image-int8";
    internal const string ArtifactId = "generation-minimax-h3-image-runtime";
    internal const string Commit = "532d2c15c430a785a712b2d94281f04e733008a4";
    internal static readonly string[] DependencyLicenses = ["runtime.sd-msvc", "runtime.llama-vulkan-loader"];

    internal static async Task PrepareAsync(ManagedModelArtifactCard card, CancellationToken token)
    {
        foreach (var file in card.Files)
        {
            var source = PinnedZipExpansionVerifier.ResolveArchivePath(card.InstallDirectory, file.RelativePath);
            var directory = BackendDirectory(card, file.Purpose);
            SafeUpdatePath.RejectLinks(directory);
            if (!Directory.Exists(directory))
                await Task.Run(() => Extract(source, directory, token), token);
            await PinnedZipExpansionVerifier.VerifyAsync(source, file, directory, token);
        }
    }

    internal static async Task<SdRuntimeSelection> SelectAsync(ManagedModelArtifactCard runtime,
        IReadOnlyList<ManagedModelArtifactCard> cards, bool forceCpu, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync([ArtifactId, .. DependencyLicenses], token);
        await PrepareAsync(runtime, token);
        SdRuntimeSelection Selection(string backend) => new(ArtifactId, BackendDirectory(runtime, backend), backend == "vulkan")
        {
            Backend = backend, PackageVersion = runtime.Revision, PackageRevision = Commit
        };

        if (!forceCpu)
        {
            var vulkan = Selection("vulkan");
            try
            {
                var inventory = await RuntimeDeviceProbe.RunAsync(vulkan.Executable, "--list-devices", token);
                if (SdRuntimeSelector.HasVulkanDevice(inventory)) return vulkan;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException
                or System.ComponentModel.Win32Exception)
            {
                // The CPU archive is independently verified and can run without a Vulkan device.
            }
        }

        var weightBytes = cards.Where(card => card.ModelArtifactId != ArtifactId).Sum(card => card.TotalBytes);
        if (!LiteraryRamReservePolicy.EvaluateCurrent(checked(weightBytes * 2)).Allowed)
            throw new BackgroundOperationFailedException("Generation.MiniMax.MemoryUnavailable");
        return Selection("cpu");
    }

    internal static string BackendDirectory(ManagedModelArtifactCard card, string backend) =>
        Path.Combine(card.InstallDirectory, "bin", backend);

    internal static long RemainingDownloadBytes(IEnumerable<ManagedModelArtifactCard> cards) => cards
        .Where(card => card.Status != ManagedModelStatuses.Installed)
        .Sum(card => card.Files.Sum(file =>
        {
            var path = Path.Combine(card.InstallDirectory, file.RelativePath);
            var stored = card.LastError == $"Integrity verification failed: {file.RelativePath}"
                ? SegmentedModelFileDownloader.GetPartialStoredBytes(path, file.SizeBytes)
                : SegmentedModelFileDownloader.GetStoredBytes(path, file.SizeBytes);
            return Math.Max(0, file.SizeBytes - stored);
        }));

    private static void Extract(string source, string directory, CancellationToken token)
    {
        var staging = directory + "." + Guid.NewGuid().ToString("N");
        SafeUpdatePath.RejectLinks(staging);
        Directory.CreateDirectory(staging);
        try
        {
            ImageGenerationInstallation.ExtractArchive(source, staging, token);
            if (!File.Exists(Path.Combine(staging, "sd-cli.exe"))) throw new InvalidDataException("Generation.VerificationFailed");
            token.ThrowIfCancellationRequested();
            try { Directory.Move(staging, directory); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
