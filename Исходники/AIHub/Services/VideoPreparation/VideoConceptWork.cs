using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services.VideoPreparation;

/// <summary>The answer is durable before applying it; replay reuses the receipt and cannot duplicate a version.</summary>
public sealed class VideoConceptReceiptStore(string? root = null)
{
    private sealed record Receipt(VideoConceptRequest Request, VideoConceptAnswer Answer);
    private readonly string _root = root ?? Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "Assistance");
    private string PathFor(VideoConceptRequest request)
    {
        if (!Guid.TryParseExact(request.ProjectId, "N", out _) || !Guid.TryParseExact(request.Id, "N", out _))
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        var path = Path.Combine(_root, request.ProjectId, request.Id + ".json");
        SafeUpdatePath.RejectLinks(path); return path;
    }
    public VideoConceptAnswer? Read(VideoConceptRequest request)
    {
        var path = PathFor(request); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("VideoPrep.AiInvalidReceipt");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt?.Request != request) throw new InvalidDataException("VideoPrep.AiInvalidReceipt");
        VideoConceptProtocol.Validate(receipt.Answer); return receipt.Answer;
    }
    public void Save(VideoConceptRequest request, VideoConceptAnswer answer)
    {
        VideoConceptProtocol.Validate(answer);
        var path = PathFor(request); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Receipt(request, answer));
                if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("VideoPrep.AiInvalidReceipt");
                stream.Write(bytes); stream.Flush(true);
            }
            SafeUpdatePath.RejectLinks(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class VideoConceptWork
{
    public const string Kind = "video.preparation.concept";
    public static string KindFor(VideoConceptRequest request) => VideoWorld.IsRequest(request)
        ? request.Purpose == VideoWorld.TranslationPurpose ? VideoWorld.TranslationKind : request.Purpose == VideoWorld.CardPurpose ? VideoWorld.CardKind : VideoWorld.Kind
        : VideoDevelopment.IsDevelopment(request)
        ? VideoTranslationProtocol.IsTranslation(request) ? VideoDevelopment.TranslationKind : VideoDevelopment.Kind
        : VideoTranslationProtocol.IsTranslation(request) ? VideoTranslationProtocol.Kind : Kind;
    public static Task<VideoConceptAnswer> RunAsync(VideoConceptRequest request, string title, IVideoConceptAssistant assistant,
        VideoConceptReceiptStore receipts, Func<VideoConceptAnswer, Task> commit, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var state = restored ?? new BackgroundOperationState
        {
            Id = request.Id,
            Kind = KindFor(request),
            Title = title,
            Project = request.ProjectPath,
            Input = JsonSerializer.SerializeToElement(request)
        };
        return ApplicationBackgroundOperations.RunAsync(KindFor(request), title, request.ProjectPath, request, async attempt =>
        {
            var answer = receipts.Read(request);
            if (answer is null)
            {
                await VideoConceptRuntimeLifecycle.BeforeAsync(assistant);
                try { answer = await assistant.GenerateAsync(request, attempt); }
                finally { await VideoConceptRuntimeLifecycle.AfterAsync(assistant); }
                attempt.ThrowIfCancellationRequested();
                receipts.Save(request, answer);
            }
            attempt.ThrowIfCancellationRequested();
            await commit(answer);
            attempt.ThrowIfCancellationRequested();
            return answer;
        }, token, state);
    }
}
