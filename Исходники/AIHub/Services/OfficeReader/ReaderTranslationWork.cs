using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderTranslationRequest(string Id, ReaderTranslationLayout Layout, ManagedModelArtifactCard Model,
    string Source, string Target, string Title);
internal sealed record ReaderTranslationResult(string Path, string Model, string Source, string Target)
{
    internal OutputModelUse[] Models { get; init; } = [];
}

internal sealed class ReaderTranslationWork(ReaderSpeechSession session, Func<IReaderTranslationRuntime>? factory = null)
{
    internal const string Kind = "office.reader.translation";
    internal static string TemporaryRoot => Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "Translation", "Temporary");
    private sealed record Receipt(string InputHash, ReaderTranslationAnswer Answer);

    internal async Task<ReaderTranslationResult> GenerateAsync(ReaderTranslationRequest request,
        IProgress<(int Done, int Total)>? progress, CancellationToken token, Func<Task>? onInferenceStarted = null)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || request.Layout.Pieces.Length == 0 || request.Source == request.Target)
            throw new InvalidDataException("Reader.Translation.InvalidReply");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var directory = Path.Combine(session.DirectoryPath, request.Id);
        var notified = false;
        var state = new BackgroundOperationState { Id = request.Id, Kind = Kind, Title = request.Title, Project = session.Id,
            Private = true, Input = JsonSerializer.SerializeToElement(new { Session = session.Id, Request = request }) };
        return await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, session.Id, state.Input, async attempt =>
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.CreateDirectory(directory);
            using var runtime = factory?.Invoke() ?? new ReaderTranslationRuntime();
            var batches = ReaderTranslationProtocol.Batches(request.Layout.Pieces).ToArray();
            var text = new Dictionary<string, string>(); var models = new List<OutputModelUse>();
            try
            {
                for (var n = 0; n < batches.Length; n++)
                {
                    attempt.ThrowIfCancellationRequested(); progress?.Report((n, batches.Length));
                    var input = ReaderTranslationProtocol.Input(request.Source, request.Target, batches[n]) + request.Model.ModelArtifactId + request.Model.Revision;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
                    var file = Path.Combine(directory, $"{n:D6}.json");
                    var receipt = Read(file, hash, batches[n]);
                    if (receipt is null)
                    {
                        if (!notified && onInferenceStarted is not null)
                        {
                            await onInferenceStarted();
                            notified = true;
                        }
                        var answer = await runtime.TranslateAsync(request.Model, request.Source, request.Target, batches[n], attempt);
                        Validate(answer, batches[n]); attempt.ThrowIfCancellationRequested(); receipt = new(hash, answer);
                        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(receipt), attempt); File.Move(file + ".tmp", file, true);
                    }
                    foreach (var pair in receipt.Answer.Text) text.Add(pair.Key, pair.Value);
                    models.AddRange(receipt.Answer.Models);
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { CompletedParts = n + 1, TotalParts = batches.Length });
                    progress?.Report((n + 1, batches.Length));
                }
                var pdf = Path.Combine(directory, "translation.pdf");
                var usedModels = models.Distinct().ToArray();
                await Task.Run(() => ReaderTranslationPdf.Write(request.Layout, text, pdf, usedModels, attempt), attempt);
                return new ReaderTranslationResult(pdf, request.Model.DisplayName, request.Source, request.Target) { Models = usedModels };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { throw new BackgroundOperationFailedException("Reader.Translation.Failed", error); }
        }, lifetime.Token, state);
    }
    private static void Validate(ReaderTranslationAnswer answer, ReaderTranslationPiece[] pieces)
    {
        if (answer.Models.Length == 0 || answer.Text.Count != pieces.Length
            || pieces.Any(p => !answer.Text.TryGetValue(p.Id, out var text) || string.IsNullOrWhiteSpace(text) || text.Length > 16000))
            throw new InvalidDataException("Reader.Translation.InvalidReply");
    }
    private static Receipt? Read(string path, string hash, ReaderTranslationPiece[] pieces)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
            if (receipt?.InputHash != hash) return null;
            Validate(receipt.Answer, pieces); return receipt;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or NullReferenceException) { return null; }
    }
    internal static void DiscardOrphans(BackgroundOperationController controller, string? root = null)
    {
        foreach (var state in controller.PendingOperations.Where(s => s.Kind == Kind))
            if (!controller.HasLiveOperation(state.Id)) controller.DiscardPending(state.Id);
        ReaderSpeechSession.RemoveOrphans(root ?? TemporaryRoot);
    }
}
