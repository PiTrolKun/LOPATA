using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoH3Item(string Id, string SceneId, string Prompt, double Seconds, long Seed, string[] Images, string[] Video, string[] Audio, string Operation);
internal sealed record VideoH3Job(string ProjectId, string ArchivePath, string ModelsRoot, int Connections, VideoH3Settings Settings,
    VideoH3Item[] Items, VideoH3Run Run)
{
    public DateTimeOffset? ScheduledUtc { get; init; }
    public bool FinalizeOnly { get; init; }
    public string ModelId { get; init; } = VideoH3Catalog.ModelId;
    internal const string Kind = "video.h3.generate";
    internal string CompletionMessageKey => !Run.Complete ? "H3FragmentReady"
        : Settings.RunMode == "review" ? "H3FragmentsGenerated" : "H3Generated";
    internal static VideoH3Job Create(VideoPreparationProject project, string archive, string root, int connections, string modelId = VideoH3Catalog.ModelId)
    {
        var state = VideoH3WorkspaceState.Ensure(project, modelId); VideoH3WorkspaceState.Validate(state);
        var settings = VideoShots.Copy(state.Settings);
        if (modelId == VideoH3LiteCatalog.ModelId && settings.Upscale) throw new InvalidDataException("VideoPrep.H3LiteUpscaleUnavailable");
        if (settings.ClipSeconds < 5 || settings.ClipSeconds > 15) throw new InvalidDataException("VideoPrep.H3PassDuration");
        if (settings.Continuation is not ("auto" or "new" or "extend")) throw new InvalidDataException("VideoPrep.H3OperationUnavailable");
        if (settings.Attention != "pytorch" || settings.Spectrum || settings.Turbo != "off" || settings.ContentLoraFile.Length != 0)
            throw new InvalidDataException("VideoPrep.H3AccelerationUnavailable");
        var items = VideoH3WorkspacePlans.From(project).Select(plan =>
        {
            state.Plans.TryGetValue(plan.Shot.Id, out var edit);
            var prompt = edit?.Prompt ?? plan.Prompt;
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("VideoPrep.H3PromptRequired");
            var ids = edit?.ReferenceIds ?? plan.Shot.ReferenceIds.Concat(string.IsNullOrEmpty(plan.Frame?.ImageId) ? [] : new[] { plan.Frame!.ImageId }).Distinct().ToList();
            var images = ids.Select(id => project.References?.Images.SingleOrDefault(i => i.Id == id)
                ?? throw new InvalidDataException("VideoPrep.MissingAttachment")).Select(image => VideoReferences.PathFor(project, image)).ToArray();
            if (images.Length > 4) throw new InvalidDataException("VideoPrep.H3ReferenceLimit");
            if (edit is { VideoReferenceRoles.Count: > 0 } or { AudioReferenceRoles.Count: > 0 }) throw new InvalidDataException("VideoPrep.H3MediaUnavailable");
            if (edit is { Operation: not ("auto" or "new" or "extend") }) throw new InvalidDataException("VideoPrep.H3OperationUnavailable");
            return new VideoH3Item(plan.Shot.Id, plan.Scene.Id, prompt, edit?.Seconds > 0 ? edit.Seconds : plan.Seconds,
                edit?.Seed ?? settings.Seed, images, [], [], edit?.Operation is { } op && op != "auto" ? op : settings.Continuation);
        }).ToArray();
        if (items.Length == 0) throw new InvalidDataException("VideoPrep.H3NoPlans");
        var total = items.Sum(i => i.Seconds);
        if (settings.TargetSeconds > 0) items = items.Select(i => i with { Seconds = i.Seconds / total * settings.TargetSeconds }).ToArray();
        var hashSettings = VideoShots.Copy(settings); hashSettings.ExportName = ""; hashSettings.RetryCount = 3;
        var hashInput = new StringBuilder(JsonSerializer.Serialize(hashSettings));
        if (modelId == VideoH3LiteCatalog.ModelId)
        {
            hashInput.Append(modelId);
            foreach (var id in new[] { "AIHub.VideoH3LiteManifest", "AIHub.VideoH3LiteSourceTree" })
            { using var resource = typeof(VideoH3Job).Assembly.GetManifestResourceStream(id)!; hashInput.Append(Convert.ToHexString(SHA256.HashData(resource))); }
        }
        foreach (var resource in new[] { "AIHub.VideoH3Manifest", "AIHub.VideoH3RuntimeManifest", "AIHub.H3Worker", "AIHub.H3Native", "AIHub.H3Memory", "AIHub.H3Assemble", "AIHub.H3RefinementCache" })
        {
            using var source = typeof(VideoH3Job).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("VideoPrep.H3FilesRequired");
            hashInput.Append(Convert.ToHexString(SHA256.HashData(source)));
        }
        foreach (var item in items)
        {
            hashInput.Append(JsonSerializer.Serialize(item with { Images = [] }));
            foreach (var image in item.Images) { using var input = File.OpenRead(image); hashInput.Append(Convert.ToHexString(SHA256.HashData(input))); }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        var run = state.Runs.LastOrDefault(r => r.InputHash == hash && !r.Complete);
        if (run is null) { run = new() { InputHash = hash, GenerationSeconds = 0 }; state.Runs.Add(run); }
        return new(project.Id, archive, root, connections, settings, items, run) { ModelId = modelId };
    }
}
