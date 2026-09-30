using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ImageBackgroundInput(string SessionId, StorageSettings Storage, string Action,
    string Request, string SelectedVersionId, string ResultVersionId, ImageBackgroundSpeechOptions? Speech = null);
public sealed record ImageBackgroundSpeechOptions(string Language, string Mode, ImageAnalysisSpeechSettings Settings,
    ImageAnalysisHeavySpeechSettings? Heavy, CoreVoiceSettings Core);

/// <summary>Durable single-image work. UI presentation is outside the inference attempt.</summary>
public sealed class ImageBackgroundWork(ImageAnalysisSessionStore store)
{
    public const string Kind = "image.single";

    public async Task<ImageAnalysisLiteraryVersion> RunAsync(ImageAnalysisLiterarySession session,
        StorageSettings storage, string request, Func<ISingleImageLiteraryPipeline> pipeline,
        Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress,
        IProgress<ModelStreamChunk>? stream, CancellationToken token, BackgroundOperationState? restored = null,
        Func<CancellationToken, Task>? afterText = null, ImageBackgroundSpeechOptions? speech = null)
    {
        var input = restored?.Input.Deserialize<ImageBackgroundInput>()
            ?? new(session.SessionId, storage, string.IsNullOrWhiteSpace(request) ? "create" : "revise",
                request, session.SelectedVersionId, Guid.NewGuid().ToString("N"), speech);
        if (input.SessionId != session.SessionId) throw new InvalidDataException("Image session mismatch.");
        storage = input.Storage;
        store.Save(session, storage); // Validate the session ID before using it as a directory.
        await PreserveTemporaryAsync(session, storage, token);
        store.Save(session, storage);
        var state = restored ?? new BackgroundOperationState
        {
            Kind = Kind, Title = session.File?.DisplayName ?? "Image", Project = session.SessionId,
            Input = JsonSerializer.SerializeToElement(input)
        };
        return await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, state.Project, input, async attempt =>
        {
            var version = session.Versions.FirstOrDefault(v => v.VersionId == input.ResultVersionId);
            if (version is null)
            {
                await ValidateSourceAsync(session.File!, attempt);
                string text;
                if (input.Action == "revise")
                {
                    session.SelectedVersionId = input.SelectedVersionId;
                    text = await pipeline().ReviseAsync(session, input.Request, storage, log, progress, stream, attempt);
                }
                else
                {
                    var result = await pipeline().CreateAsync(session.File!, session.Settings, storage, session,
                        log, progress, stream, checkpoint =>
                        {
                            session.VisualReport = checkpoint.VisualReport;
                            session.HiddenConversation = checkpoint.HiddenConversation.ToList();
                            session.Status = ImageAnalysisLiteraryStatuses.Writing;
                            store.Save(session, storage);
                        }, attempt);
                    session.VisualReport = result.VisualReport;
                    if (result.HiddenConversation is not null) session.HiddenConversation = result.HiddenConversation.ToList();
                    session.ReviewSummary = result.ReviewSummary;
                    session.RuntimeMetrics.VisualPassMilliseconds = result.VisualPassMilliseconds;
                    session.RuntimeMetrics.ComposePassMilliseconds = result.ComposePassMilliseconds;
                    text = result.Description;
                }
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty image description.");
                version = new() { VersionId = input.ResultVersionId, Number = session.Versions.Count + 1,
                    Text = text, ChangeRequest = input.Request, Source = input.Action == "create" ? "initial" : "revision" };
                session.Versions.Add(version);
                session.SelectedVersionId = version.VersionId;
                session.Status = ImageAnalysisLiteraryStatuses.ResultReady;
                session.CurrentStep = ImageAnalysisLiterarySteps.Result;
                session.LastError = "";
                // This version ID is the receipt. A crash before completion never creates a duplicate.
                store.Save(session, storage);
            }
            if (afterText is not null) await afterText(attempt);
            return version;
        }, token, state);
    }

    internal async Task PreserveTemporaryAsync(ImageAnalysisLiterarySession session, StorageSettings storage, CancellationToken token)
    {
        if (session.File is not { StorageKind: ImageAssetKinds.Temporary } file) return;
        if (file.Extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') || file.Extension.Length > 12)
            throw new InvalidDataException("Invalid image extension.");
        var directory = Path.Combine(store.GetProjectsDirectory(storage), session.SessionId, "Input");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "source" + file.Extension);
        await ValidateSourceAsync(file, token);
        await using (var source = File.OpenRead(file.SourcePath))
        await using (var output = new FileStream(destination + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { await source.CopyToAsync(output, token); output.Flush(true); }
        File.Move(destination + ".tmp", destination, true);
        file.SourcePath = destination;
        file.StorageKind = ImageAssetKinds.Saved;
    }

    internal static async Task ValidateSourceAsync(ImageAnalysisFilePassport file, CancellationToken token)
    {
        await using var source = File.OpenRead(file.SourcePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
        if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source image changed since selection.");
    }
}
