using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services.VideoEditor;

/// <summary>Explicit archive saves are separate from recovery and derived preview files.</summary>
internal sealed class VideoSaveChanges(VideoProject initial, bool savedArchive, bool recovered = false)
{
    private string? _saved = recovered ? null : Signature(initial);
    private bool _savedArchive = savedArchive;
    private long _previousActions = recovered ? 2 : 0;
    internal bool HasChanges(VideoProject project) => _saved != Signature(project);
    internal bool NeedsPrompt(VideoProject project, long revision) => (_savedArchive || _previousActions + revision > 1) && HasChanges(project);
    internal void Replaced(long oldRevision) => _previousActions += oldRevision + 1;
    internal void Saved(VideoProject project) { _saved = Signature(project); _savedArchive = true; _previousActions = 0; }
    private static string Signature(VideoProject project)
    {
        var value = JsonSerializer.SerializeToNode(project)!.AsObject();
        value.Remove(nameof(VideoProject.ModifiedUtc)); value.Remove(nameof(VideoProject.Provenance));
        foreach (var asset in value[nameof(VideoProject.Assets)]!.AsArray()) asset!.AsObject().Remove(nameof(VideoAsset.Proxy));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }
}
