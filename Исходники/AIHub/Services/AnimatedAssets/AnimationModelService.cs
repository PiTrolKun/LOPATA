using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services.ImageLayers;
using AIHub.Services.VisualScenario;
using Lopata.Updates;

namespace AIHub.Services.AnimatedAssets;

internal sealed record AnimationDrawing(byte[] Image, AnimationModelUse Model);
internal sealed record AnimationModelResult(string Target, AnimationDrawing? Drawing = null, AnimationScenePlan? Plan = null);

internal sealed partial class AnimationModelService
{
    internal const string OperationKind = "animated-assets.model";
    private const string Draw = "draw";
    private const string Plan = "plan";

    private sealed record Job(string Id, string Action, string Target, string Prompt, string Model,
        string EngineSet, string Author, DateTimeOffset SubmittedAt, bool HasImage, bool Transparent,
        int Seed, double Duration, string Keep, string Language, bool ForceCpu = false, PackPoseJob[]? Items = null);

    internal async Task<AnimationDrawing> DrawAsync(string projectPath, string prompt, byte[]? image,
        bool transparent, string target, Func<AnimationModelResult, Task> apply, CancellationToken token = default)
    {
        var job = CreateJob(Draw, target, prompt, ArtistId, image is not null, transparent);
        var keepSourceBackground = image is not null && target != "reference";
        var sourceIsTransparent = await StageImageAsync(job, image, token);
        if (keepSourceBackground) job = job with { Transparent = sourceIsTransparent };
        var result = await RunAsync(projectPath, job, apply, token);
        return result.Drawing!;
    }

    internal async Task<AnimationScenePlan> PlanAsync(string projectPath, string description, byte[] image,
        double duration, string keep, string target, Func<AnimationModelResult, Task> apply, CancellationToken token = default)
    {
        if (!ValidDuration(duration))
            throw new ArgumentException("AnimatedAssets.Error.Duration");
        var job = CreateJob(Plan, target, description, AdvisorId, hasImage: true, transparent: false) with
        {
            Duration = duration, Keep = keep, Language = _language?.Invoke() == "en" ? "English" : "Russian"
        };
        await StageImageAsync(job, image, token);
        var result = await RunAsync(projectPath, job, apply, token);
        return result.Plan!;
    }

    internal async Task RestoreAsync(BackgroundOperationState state, Func<AnimationModelResult, Task> apply,
        CancellationToken token)
    {
        if (state.Kind != OperationKind || state.Project is null || state.Input.Deserialize<Job>() is not { } job)
            throw new InvalidDataException("Visual.InvalidProject");
        if (job.Action == DrawPack)
        {
            await RunPackAsync(state.Project, job, apply, token, state);
            return;
        }
        if (job.Action == Plan && VisualAdvisor.RequiresCpuRecovery(state.Detail))
        {
            job = job with { ForceCpu = true };
            state = state with { Input = JsonSerializer.SerializeToElement(job) };
        }
        await RunAsync(state.Project, job, apply, token, state);
    }

    private Job CreateJob(string action, string target, string text, string model, bool hasImage, bool transparent)
    {
        if (action != DrawPack && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Visual.MaterialRequired");
        return new(Guid.NewGuid().ToString("N"), action, target, text.Trim(), model, EngineSetSelection.ActiveId,
            ImageGenerationMetadata.Author(_metadataAuthor?.Invoke()), DateTimeOffset.Now, hasImage, transparent,
            RandomNumberGenerator.GetInt32(int.MaxValue), 0, "", "Russian");
    }

    private async Task<AnimationModelResult> RunAsync(string projectPath, Job job, Func<AnimationModelResult, Task> apply,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (!Path.IsPathFullyQualified(projectPath) || string.IsNullOrWhiteSpace(job.Target)
            || job.Action is not (Draw or Plan) || job.Action == Plan && (job.Model != AdvisorId || !job.HasImage || !ValidDuration(job.Duration))
            || job.Action == Draw && job.Model is not (VisualComponents.Turbo or VisualComponents.Base))
            throw new InvalidDataException("Visual.InvalidProject");
        var directory = JobDirectory(job);
        var result = await ApplicationBackgroundOperations.RunAsync(OperationKind, _localize("Animation.Title"), projectPath, job,
            async ct =>
            {
                var source = job.HasImage ? Path.Combine(directory, "source.png") : null;
                if (source is not null)
                {
                    SafeUpdatePath.RejectLinks(source);
                    if (!File.Exists(source)) throw new BackgroundOperationFailedException("Visual.MissingImage");
                }
                var completed = job.Action == Draw
                    ? new AnimationModelResult(job.Target, Drawing: await RenderStillAsync(job, directory, source, ct))
                    : new AnimationModelResult(job.Target, Plan: await PlanSceneAsync(job, directory, source!, ct));
                ct.ThrowIfCancellationRequested();
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { job.Id, job.Target, Ready = true });
                await apply(completed);
                return completed;
            }, token, restored);
        DeleteCompletedStage(directory);
        return result;
    }

    private async Task<AnimationDrawing> RenderStillAsync(Job job, string directory, string? source, CancellationToken token,
        bool recordBackgroundCheckpoint = true)
    {
        var sessionPath = Path.Combine(directory, "session.json");
        SafeUpdatePath.RejectLinks(sessionPath);
        if (File.Exists(sessionPath))
        {
            var savedTurn = ImageGenerationSessionStore.Load(directory).Turns.SingleOrDefault(item => item.Request.Id == job.Id);
            if (savedTurn?.Results.SingleOrDefault() is { ProcessingError: null } saved)
            {
                var existingPath = Path.Combine(directory, job.Id + "_" + saved.Index + ".png");
                SafeUpdatePath.RejectLinks(existingPath);
                if (ImageGenerationRuntime.IsValidImage(existingPath, savedTurn.Request))
                    return new(await File.ReadAllBytesAsync(existingPath, token), new("artist", job.Model,
                        saved.Runtime?.ArtistRevision ?? savedTurn.Request.Metadata?.ArtistRevision));
            }
        }
        RequireEngineSet(job);
        var cards = await _components.PrepareArtistAsync(job.Model, job.HasImage, false, null, token)
            ?? throw new BackgroundOperationWaitingException("Generation.DownloadRequired");
        var model = ImageGenerationCatalog.Get(job.Model);
        var size = (Width: 768, Height: 768);
        if (source is not null)
        {
            using var stream = File.OpenRead(source);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            size = ImageGenerationDimensions.Fit(image.PixelWidth, image.PixelHeight, 768, model.MaximumSide);
        }
        var revision = cards.FirstOrDefault(card => card.Files.Any(file => file.Purpose == "diffusion"))?.Revision;
        var request = new ImageGenerationRequest(job.Id, job.Model, StillPrompt(job), size.Width, size.Height,
            [job.Seed], _components.Root, directory)
        {
            EngineSetId = job.EngineSet,
            SubmittedAt = job.SubmittedAt,
            ReferenceImages = source is null ? [] : [source],
            Metadata = new(job.Author, "animated-assets") { ArtistRevision = revision }
        };
        ImageGenerationSessionStore.EnsureInitialized(directory, job.Id);
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var turn = await new ImageGenerationRuntime().RunAsync(request, cards, null, token, recordBackgroundCheckpoint);
        var result = turn.Results.Single();
        if (result.ProcessingError is { } error) throw new BackgroundOperationFailedException(error);
        var bytes = await File.ReadAllBytesAsync(ImageGenerationSessionStore.ResultPath(request, result.Index), token);
        return new(bytes, new("artist", job.Model, result.Runtime?.ArtistRevision ?? revision));
    }

    private async Task<AnimationScenePlan> PlanSceneAsync(Job job, string directory, string source, CancellationToken token)
    {
        var path = Path.Combine(directory, "plan.json");
        SafeUpdatePath.RejectLinks(path);
        if (File.Exists(path))
            return JsonSerializer.Deserialize<AnimationScenePlan>(await File.ReadAllBytesAsync(path, token))
                ?? throw new BackgroundOperationFailedException("Animation.InvalidScenePlan");
        RequireEngineSet(job);
        var modelPath = await _components.PrepareAdvisorAsync(AdvisorId, false, null, token)
            ?? throw new BackgroundOperationWaitingException("Generation.DownloadRequired");
        var use = _components.AdvisorUse(AdvisorId);
        var advisor = new VisualAdvisor();
        try
        {
            var imageUrl = await VisualAdvisor.ImageUrlAsync(source, token);
            var prepared = await advisor.PrepareAsync(modelPath, gemma: true, images: true, token, forceCpu: job.ForceCpu);
            var result = await AnimationScenePlanner.PlanAsync(advisor, prepared, imageUrl, job.Prompt,
                job.Duration, job.Keep, job.Language, new("advisor", use.Id, use.Revision), token);
            SavePlan(path, result, token);
            return result;
        }
        finally { await advisor.RetireAsync(); }
    }

    private static bool ValidDuration(double duration) => double.IsFinite(duration)
        && duration is >= AnimationRenderer.MinimumDuration and <= AnimationRenderer.MaximumDuration;

    private static void RequireEngineSet(Job job)
    {
        if (job.EngineSet != EngineSetSelection.ActiveId)
            throw new BackgroundOperationWaitingException(job.Action == Draw
                ? "Generation.Qwen.EngineSetRequired" : "LetItOut.Preparation.EngineRequired");
    }

    private static string StillPrompt(Job job)
    {
        var prompt = (job.HasImage, job.Target) switch
        {
            (false, _) => "Draw one finished still image for a short animation. " + job.Prompt,
            (true, "reference") => "Use the supplied image as the starting point for the requested artwork. Apply any requested "
                + "changes to style, proportions, expression, and framing. Keep unspecified details. " + job.Prompt,
            _ => "Edit the supplied reference as one still image. Preserve identity, art style, framing, scale, and all details "
                + "not mentioned in the requested change. Make only this change: " + job.Prompt
        };
        return job.Transparent
            ? "This is an RGBA image with transparency. " + prompt + " The image has alpha channel and the background is transparent."
            : job.HasImage && job.Target != "reference"
                ? prompt + " Keep the original opaque background, colors and lighting."
                : prompt;
    }

    private static string JobDirectory(Job job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out var id)) throw new InvalidDataException("Visual.InvalidProject");
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "AnimatedAssets", "ModelJobs", id.ToString("N"));
        SafeUpdatePath.RejectLinks(directory);
        return directory;
    }

    private static async Task<bool> StageImageAsync(Job job, byte[]? input, CancellationToken token)
    {
        var directory = JobDirectory(job);
        Directory.CreateDirectory(directory);
        if (input is null) return false;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var image = LayerSource.Load("source.png", input);
            var png = image.Encode(image.Frame, forModel: false);
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(Path.Combine(directory, "source.png"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            file.Write(png);
            file.Flush(true);
            return job.Action is Draw or DrawPack && job.Target != "reference" && HasVisibleTransparency(image.Pixels);
        }, token);
    }

    internal static bool HasVisibleTransparency(ReadOnlySpan<byte> bgra)
    {
        // Isolated nearly opaque pixels introduced during encoding do not represent an alpha background.
        var requiredPixels = Math.Max(1, bgra.Length / 400);
        for (var alpha = 3; alpha < bgra.Length; alpha += 4)
            if (bgra[alpha] < 248 && --requiredPixels == 0)
                return true;
        return false;
    }

    private static void SavePlan(string path, AnimationScenePlan result, CancellationToken token)
    {
        var temporary = path + ".pending";
        SafeUpdatePath.RejectLinks(temporary);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, result);
            file.Flush(true);
        }
        token.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteCompletedStage(string directory)
    {
        try
        {
            SafeUpdatePath.RejectLinks(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                SafeUpdatePath.RejectLinks(file);
                File.Delete(file);
            }
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            OwnedProcessRegistry.Log("animation_stage_cleanup_failed", "AnimatedAssets", detail: error.GetType().Name);
        }
    }
}
