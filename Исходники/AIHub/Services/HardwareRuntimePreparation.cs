using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One acquisition plan for hardware libraries, separate from model availability.</summary>
public sealed partial class HardwareRuntimePreparation(ComponentManager manager)
{
    [GeneratedRegex(@"\bversion:\s*9442\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PinnedVersion();
    public static bool IsPinnedVersion(string output) => PinnedVersion().IsMatch(output)
        && output.Contains("d4c8e2c", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSelectedNativeLayoutReady(string setId)
    {
        NativeRuntimeReleasePolicy.ValidateSetId(setId);
        return setId == NativeRuntimeReleasePolicy.LegacySet || new[] { ("llama", "cpu"), ("llama", "vulkan"),
            ("sd", "cpu"), ("sd", "vulkan"), ("sd", "cuda12") }.All(item => NativeEngineBundleVerifier.HasCompleteLayout(
                item.Item1, item.Item2, EngineSetSelection.ResolveDirectory(setId, item.Item1, item.Item2)));
    }

    private static bool HasPackagedLegacyNativeLayout() => new[]
    {
        (HardwareRuntimeCatalog.LlamaCpuId, "llama", "cpu"),
        (HardwareRuntimeCatalog.LlamaVulkanId, "llama", "vulkan"),
        (HardwareRuntimeCatalog.SdCpuId, "sd", "cpu"),
        (HardwareRuntimeCatalog.SdVulkanId, "sd", "vulkan")
    }.All(item => HardwareRuntimeBundleVerifier.HasCompleteLayout(item.Item1,
        EngineSetSelection.ResolveDirectory(NativeRuntimeReleasePolicy.LegacySet, item.Item2, item.Item3)));

    /// <summary>Startup inventory only; consumers still verify content before executing a selected runtime.</summary>
    public async Task<ComponentAcquisitionPlan> CheckReadinessAsync(IEnumerable<GpuPassport> devices, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var setId = EngineSetSelection.ActiveId;
        if (!IsSelectedNativeLayoutReady(setId)) throw new InvalidDataException("The selected engine set is incomplete. Prepare or select a verified engine set first.");
        var inventory = devices.ToArray();
        var cudaMajors = inventory.Any(gpu => gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            ? await Task.Run(NvidiaDriverCapabilities.ReadComputeMajors, token) : [];
        token.ThrowIfCancellationRequested();
        var ids = HardwareRuntimeCatalog.RequiredComponents(inventory, cudaMajors);
        if (setId == NativeRuntimeReleasePolicy.UpdatedSet || HasPackagedLegacyNativeLayout())
            ids = ids.Where(id => !HardwareRuntimeBundleVerifier.IsManaged(id)).ToArray();
        return manager.BuildPlanForComponents(ids, "Hardware runtime preparation; engineSet=" + setId);
    }

    /// <summary>Explicit complete integrity check, including executable version probes.</summary>
    public async Task<ComponentAcquisitionPlan> CheckAsync(IEnumerable<GpuPassport> devices, CancellationToken token)
    {
        var plan = await CheckReadinessAsync(devices, token);
        foreach (var item in plan.Items.Where(item => item.AlreadyAvailable))
        {
            var entry = ComponentCatalog.Find(item.ComponentId)!;
            try
            {
                await VerifyAsync(entry.Id, manager.GetInstallDirectory(entry), token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            { item.AlreadyAvailable = false; }
        }
        return plan;
    }

    public async Task InstallAsync(ComponentAcquisitionPlan plan, IProgress<ComponentDownloadProgress>? progress, CancellationToken token)
    {
        foreach (var item in plan.Items.Where(item => !item.AlreadyAvailable))
        {
            var installed = await manager.DownloadAndInstallAsync(item.ComponentId, progress, token, forceReinstall: true);
            if (!installed.IsAvailable) throw new IOException("Hardware runtime installation is incomplete.");
            await VerifyAsync(item.ComponentId, installed.Record.InstallPath, token);
            item.AlreadyAvailable = true;
        }
    }

    private static Task VerifyAsync(string id, string directory, CancellationToken token) =>
        ComponentCatalog.Find(id)?.DeliveryKind == ComponentDeliveryKinds.PythonProfile
            ? PythonRuntimeBundleVerifier.VerifyAsync(HardwareRuntimeCatalog.PythonProfile(id), directory, token)
            : HardwareRuntimeBundleVerifier.VerifyExecutableAsync(id, directory, token);
}
