using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoVaceItem(string Id, string SceneId, string Prompt, double Seconds, long Seed, string[] Images);
internal sealed record VideoVaceJob(string ProjectId, string ArchivePath, string ModelsRoot, int Connections,
    VideoVaceSettings Settings, VideoVaceItem[] Items, VideoVaceRun Run)
{
    public DateTimeOffset? ScheduledUtc { get; init; }
    internal const string Kind = "video.vace13.generate";

    internal static VideoVaceJob Create(VideoPreparationProject project, string archive, string modelsRoot, int connections)
    {
        var state = VideoVaceWorkspaceState.Ensure(project); VideoVaceWorkspaceState.Validate(state);
        var settings = VideoShots.Copy(state.Settings);
        var plans = VideoStoryboard.Plans(project).ToArray();
        var ordered = project.Storyboard is { Frames.Count: > 0 } board
            ? board.Frames.Select(f => (Pair: plans.FirstOrDefault(x => x.Shot.Id == f.ShotId), Frame: (VideoBoardFrame?)f)).Where(x => x.Pair.Shot is not null)
            : plans.Select(x => (Pair: x, Frame: (VideoBoardFrame?)null));
        var items = ordered.Select(x =>
        {
            var scene = x.Pair.Scene; var shot = x.Pair.Shot;
            var translated = project.Shots is { } document && VideoShots.TranslationCurrent(document, scene)
                ? VideoShots.SelectedTranslation(document, scene.Id)?.Scene.Shots.FirstOrDefault(t => t.Id == shot.Id) : null;
            var prompt = VideoPreparationExport.Prompt(translated ?? shot);
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidDataException("VideoPrep.VacePromptRequired");
            // Native WanVaceToVideo accepts one reference. The explicitly chosen storyboard image takes precedence.
            var imageId = !string.IsNullOrEmpty(x.Frame?.ImageId) ? x.Frame.ImageId : shot.ReferenceIds.FirstOrDefault();
            var image = string.IsNullOrEmpty(imageId) ? null : project.References?.Images.SingleOrDefault(i => i.Id == imageId)
                ?? throw new InvalidDataException("VideoPrep.MissingAttachment");
            string[] images = image is null ? [] : [VideoReferences.PathFor(project, image)];
            var seconds = x.Frame?.Seconds ?? (shot.Duration > 0 ? shot.Duration : 5);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400) throw new InvalidDataException("VideoPrep.VaceSettingsInvalid");
            return new VideoVaceItem(shot.Id, scene.Id, prompt, seconds, settings.Seed, images);
        }).ToArray();
        if (items.Length == 0) throw new InvalidDataException("VideoPrep.VaceNoPlans");
        var total = items.Sum(i => i.Seconds);
        if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("VideoPrep.VaceSettingsInvalid");
        if (settings.TargetSeconds > 0) items = items.Select(i => i with { Seconds = i.Seconds / total * settings.TargetSeconds }).ToArray();
        var hashSettings = VideoShots.Copy(settings); hashSettings.ExportName = ""; hashSettings.RetryCount = 3;
        var hashInput = new StringBuilder(JsonSerializer.Serialize(new
        {
            Contract = "native-vace-firstclip-mask-v1", VideoVaceCatalog.ModelId, VideoVaceCatalog.Repository,
            VideoVaceCatalog.Revision, VideoVaceCatalog.Files, Settings = hashSettings,
            Core = VideoH3Catalog.Parts.Single(p => p.Id == "video-h3-source-comfy").Revision,
            Libraries = VideoH3RuntimeCatalog.Data.Revision
        }));
        foreach (var resource in new[] { "AIHub.VaceWorker", "AIHub.VaceNative", "AIHub.VaceMedia" })
        {
            using var source = typeof(VideoVaceJob).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("VideoPrep.VaceRuntimeRequired");
            hashInput.Append(Convert.ToHexString(SHA256.HashData(source)));
        }
        foreach (var item in items)
        {
            hashInput.Append(JsonSerializer.Serialize(item with { Images = [] }));
            foreach (var path in item.Images)
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(path));
                using var input = File.OpenRead(path); hashInput.Append(Convert.ToHexString(SHA256.HashData(input)));
            }
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        var run = state.Runs.LastOrDefault(r => r.InputHash.Equals(hash, StringComparison.OrdinalIgnoreCase) && !r.Complete);
        if (run is null) { run = new() { InputHash = hash }; state.Runs.Add(run); }
        return new(project.Id, archive, modelsRoot, connections, settings, items, run);
    }
}
