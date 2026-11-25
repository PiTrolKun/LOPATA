using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal sealed record SongClipDenoEnvironment(H3MontageEnvironment Montage, string Vfx, string Attention, string? AcceleratedTorch);
internal sealed record SongClipDenoRuntime(string Vfx, string Attention, string? AcceleratedTorch,
    IReadOnlyList<ManagedModelArtifactCard> Cards);

internal static partial class SongClipDenoComponents
{
    internal const string VfxId = "song-clip-deno-nvidia-vfx";
    internal const string SageId = "song-clip-deno-sageattention";
    internal const string TritonId = "song-clip-deno-triton";
    private const string SageSource = "https://github.com/Comfy-Org/wheels/releases/tag/sageattention-latest";
    private const string VfxSource = "https://pypi.nvidia.com/nvidia-vfx/";
    private const string TritonSource = "https://pypi.org/project/triton-windows/3.6.0.post26/";
    private static readonly SemaphoreSlim Gate = new(1);

    private static Dictionary<string, PinnedTreeFile[]> ReadTrees(string name)
    {
        using var resource = typeof(SongClipDenoComponents).Assembly.GetManifestResourceStream("AIHub.SongClip." + name)
            ?? throw new InvalidDataException("Missing Deno component manifest.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, PinnedTreeFile[]>>(gzip)
            ?? throw new InvalidDataException("Invalid Deno component manifest.");
    }

    internal static ManagedModelArtifactCard[] Cards(string root, bool accelerated = false)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Model storage must be selected.");
        const string sageWheel = "sageattention-2.2.0+cu126torch2.10-cp312-cp312-win_amd64.whl";
        const string tritonWheel = "triton_windows-3.6.0.post26-cp312-cp312-win_amd64.whl";
        var vfx = VfxCard(root);
        var triton = Card(root, TritonId, "Triton Windows", "triton-lang / triton-windows", "3.6.0.post26", "MIT", TritonSource,
            tritonWheel, 47402104, "189d8c57911aa9d2ff983a715e5c967b325f576307db60924cab22b501a36515",
            "https://files.pythonhosted.org/packages/62/1e/8d9814e67ba3f20094cf3c69e7815a491f20beb86469a647550ba86728b0/" + tritonWheel);
        if (accelerated) return [vfx, triton, .. AcceleratedCards(root)];
        return [vfx,
            Card(root, SageId, "SageAttention", "THU-ML / Comfy-Org", "2.2.0+cu126torch2.10", "Apache-2.0", SageSource,
                sageWheel, 15184343, "737f8a22933389a07b1dbb055f7f40e4f28a59476cb83f42ed80afda3c1a8ede",
                "https://github.com/Comfy-Org/wheels/releases/download/sageattention-latest/" + Uri.EscapeDataString(sageWheel)),
            triton
        ];
    }

    internal static ManagedModelArtifactCard VfxCard(string root)
    {
        const string wheel = "nvidia_vfx-0.2.0.0-cp312-abi3-win_amd64.whl";
        var card = Card(root, VfxId, "NVIDIA Video Effects", "NVIDIA", "0.2.0.0", "LicenseRef-NvidiaProprietary", VfxSource,
            wheel, 435807359, "cebaa12411f7e22b39293205c5c4d07b8073d14ca1e0e7727ee4375207a36445", VfxSource + wheel);
        card.Consumers.Add(new() { Id = "video_upscale", DisplayName = "Апскейл видео / Video upscale", Kind = "utility" });
        return card;
    }

    internal static async Task PrepareVfxAsync(ManagedModelArtifactCard card, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await InstallAsync(card, ReadTrees("DenoTrees")[VfxId], token);
        }
        finally { Gate.Release(); }
    }

    private static ManagedModelArtifactCard Card(string root, string id, string name, string provider, string version, string license,
            string source, string file, long size, string hash, string url) => new()
        {
            ModelArtifactId = id, Family = SongClipDeno.Title, DisplayName = name, Role = ManagedModelRoles.Tool,
            Provider = provider, Revision = version, License = license, SourcePage = source, RepositoryId = id,
            IsManaged = true, IsPinned = true, Origin = ManagedModelOrigins.PredefinedScenario,
            ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "SongClip", id, version),
            Files = [new() { RelativePath = file, SizeBytes = size, Sha256 = hash, SourceUrl = url }],
            Consumers = [new() { Id = "song_clip", DisplayName = SongClipDeno.Title, Kind = "scenario" }]
        };
    internal static async Task<SongClipDenoEnvironment> PrepareAsync(string root, int connections,
        CancellationToken token, Action<string> log, bool accelerated = false)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var montage = await H3MontageComponents.PrepareAsync(root, connections, token, log, device: "cuda");
        var runtime = await PrepareRuntimeAsync(root, connections, montage.Motion.H3.Python, accelerated, log, token);
        return new(montage with { Cards = [.. montage.Cards, .. runtime.Cards] },
            runtime.Vfx, runtime.Attention, runtime.AcceleratedTorch);
    }

    internal static async Task<SongClipDenoRuntime> PrepareRuntimeAsync(string root, int connections,
        ManagedPythonSelection python, bool accelerated, Action<string> log, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!python.Device.StartsWith("cuda:", StringComparison.Ordinal)
            || python.Entry.Id is not (HardwareRuntimeCatalog.PythonCudaId or HardwareRuntimeCatalog.PythonCuda128Id))
            throw new IOException("SongClip.DenoGpuRequired");
        var cards = Cards(root, accelerated);
        var store = new ManagedModelLibraryStore();
        await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), token);
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var card in cards)
        {
            store.Upsert(card);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("SongClip.DownloadRequired");
        }
        var trees = ReadTrees("DenoTrees");
        if (accelerated)
            foreach (var (id, files) in ReadTrees("DenoSolTrees"))
                trees.Add(id, files);
        var paths = new Dictionary<string, string>();
        await Gate.WaitAsync(token);
        try
        {
            foreach (var card in cards)
            {
                log("[Prepare] " + card.DisplayName);
                await InstallAsync(card, trees[card.ModelArtifactId], token);
                paths.Add(card.ModelArtifactId, PackageDirectory(card));
            }
        }
        finally { Gate.Release(); }
        var sage = accelerated ? SolSageId : SageId;
        var attention = accelerated || python.Entry.Id == HardwareRuntimeCatalog.PythonCudaId
            ? string.Join(Path.PathSeparator, paths[sage], paths[TritonId]) : "";
        return new(paths[VfxId], attention, paths.GetValueOrDefault(SolTorchId), cards);
    }

    private static Task InstallAsync(ManagedModelArtifactCard card, PinnedTreeFile[] files, CancellationToken token) =>
        GlitchframeComponents.InstallTreeAsync(card, files, RuntimeDirectory(card.ModelArtifactId, card.Revision), wheels: true, token, repair: true);

    internal static string PackageDirectory(ManagedModelArtifactCard card) =>
        Path.Combine(RuntimeDirectory(card.ModelArtifactId, card.Revision), "Lib", "site-packages");

    internal static string RuntimeDirectory(string id, string revision) =>
        Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", id, revision);
}
