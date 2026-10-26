using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

/// <summary>Persist actual synthesis facts atomically; old clips retain their original profile when a new one is selected.</summary>
internal sealed record VideoNarrationReceipt(OutputModelUse[] Models, VideoSpeechProfile? Profile = null)
{
    internal static VideoNarrationReceipt Read(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path)); var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Array) return new(root.Deserialize<OutputModelUse[]>()!);
        return root.TryGetProperty("Models", out _) ? root.Deserialize<VideoNarrationReceipt>()! : new([root.Deserialize<OutputModelUse>()!]);
    }
    internal async Task SaveAsync(string path, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this), token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static IEnumerable<OutputModelUse> SourceModels(IEnumerable<OutputModelUse> history) => history.Where(m =>
        m.Role is not ("narration speech synthesis" or "speech runtime" or "speech hardware runtime" or "speech interpreter" or "Kitten GPU waveform decoder")
        && !m.Role.StartsWith("speech hardware runtime ·", StringComparison.Ordinal));
}
