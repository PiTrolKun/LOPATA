using System.Globalization;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ImageUtilityAiService
{
    internal const string NcnnMsvcLicenseId = "runtime.image-utility-msvc-openmp";

    private static IReadOnlyList<string> NativeLicenseIds(ManagedModelArtifactCard card, string methodId) =>
        methodId == "real-cugan" ? [card.ModelArtifactId, NcnnMsvcLicenseId] : [card.ModelArtifactId];

    private static Task VerifyNativeAsync(ManagedModelArtifactCard card, CancellationToken token)
    {
        var archive = card.Files.Single(file => file.Purpose == "runtime");
        return PinnedZipExpansionVerifier.VerifyAsync(Path.Combine(card.InstallDirectory, archive.RelativePath),
            archive, Path.Combine(card.InstallDirectory, "expanded"), token);
    }

    public async Task<IReadOnlyList<NcnnDevice>> ListNativeDevicesAsync(string methodId, CancellationToken token)
    {
        if (methodId is not ("real-esrgan" or "real-cugan")) throw new ArgumentException("Unknown native image method.");
        if (!IsReady(methodId)) throw new ImageUtilityException("ImageUtility.Ai.DownloadRequired");
        var card = Register(methodId).Single();
        await ComponentLicenseGate.EnsureAsync(NativeLicenseIds(card, methodId), token);
        await VerifyNativeAsync(card, token);
        return await NcnnDeviceProbe.ReadAsync(NativeExecutable(card, methodId), token);
    }

    internal static void ValidateNativeDevices(string methodId, string selection, IReadOnlyList<NcnnDevice> available)
    {
        if (selection == "auto")
        {
            if (methodId == "real-esrgan" && available.Count == 0)
                throw new ImageUtilityException("ImageUtility.Ai.NoCompatibleDevice");
            return; // CUGAN's pinned native automatic policy supports CPU when Vulkan has no devices.
        }
        foreach (var id in selection.Split(','))
            if (!(methodId == "real-cugan" && id == "-1")
                && (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || !available.Any(device => device.Index == index)))
                throw new ImageUtilityException("ImageUtility.Ai.NoCompatibleDevice");
    }
}
