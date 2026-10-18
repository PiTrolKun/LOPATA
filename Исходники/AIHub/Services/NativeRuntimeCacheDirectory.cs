using System.IO;

namespace AIHub.Services;

/// <summary>Binary KV caches never cross native engine revisions or hardware backends.</summary>
internal static class NativeRuntimeCacheDirectory
{
    internal static string ForLlama(string root, LlamaRuntimeBundle bundle)
    {
        NativeRuntimeReleasePolicy.ValidateSetId(bundle.EngineSetId);
        if (bundle.Backend is not ("CPU" or "Vulkan" or "CUDA")) throw new ArgumentException("Unknown cache backend.");
        return Path.Combine(root, "llama-" + bundle.EngineSetId + "-" + bundle.Revision[..12] + "-" + bundle.Backend.ToLowerInvariant());
    }
}
