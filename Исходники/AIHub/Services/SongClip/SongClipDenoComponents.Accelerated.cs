using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static partial class SongClipDenoComponents
{
    internal const string SolTorchId = "song-clip-deno-sol-torch";
    internal const string SolSageId = "song-clip-deno-sol-sage";
    private const string TorchSource = "https://download.pytorch.org/whl/cu130/";

    internal static ManagedModelArtifactCard[] AcceleratedCards(string root)
    {
        const string torch = "torch-2.10.0+cu130-cp312-cp312-win_amd64.whl";
        const string sage = "sageattention-2.2.0+cu130torch2.10-cp312-cp312-win_amd64.whl";
        return [
            Card(root, SolTorchId, "Deno RTX · PyTorch CUDA 13", "PyTorch", "2.10.0+cu130",
                "BSD-3-Clause and upstream NVIDIA notices", TorchSource + "torch/",
                torch, 1867405006, "224649fa0ab181ec483cc368e3303dda1760e4ba31bea806b88979f855436aaa",
                TorchSource + Uri.EscapeDataString(torch)),
            Card(root, SolSageId, "Deno RTX · SageAttention CUDA 13", "THU-ML / Comfy-Org",
                "2.2.0+cu130torch2.10", "Apache-2.0", SageSource,
                sage, 15410669, "597737e3405e24f20db10ff940b61fdd6e7454c91531001a91255ccef89870d1",
                "https://github.com/Comfy-Org/wheels/releases/download/sageattention-latest/" + Uri.EscapeDataString(sage))
        ];
    }
}
