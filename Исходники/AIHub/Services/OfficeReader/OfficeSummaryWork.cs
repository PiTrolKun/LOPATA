using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.OfficeEditor;

namespace AIHub.Services.OfficeReader;

internal sealed record OfficeSummaryResult(string Text, OutputModelUse[] Models);

internal static class OfficeSummaryWork
{
    internal const string Kind = "office.document.summary";

    internal static async Task<OfficeSummaryResult> GenerateAsync(string text, ManagedModelArtifactCard model, string language,
        OfficeAiJournal journal, string title, IProgress<string>? progress, CancellationToken token,
        Func<string, CancellationToken, Task<string>>? generate = null)
    {
        var parts = ChatSpeechText.Parts(text, maximum: 6000, stripMarkup: false);
        if (parts.Count == 0) throw new InvalidDataException("Reader.Translation.NoText");
        var completed = new Dictionary<string, string>();
        var state = new BackgroundOperationState { Id = Guid.NewGuid().ToString("N"), Kind = Kind, Title = title,
            Private = true, Input = JsonSerializer.SerializeToElement(new { Parts = parts.Count, Model = model.ModelArtifactId, Language = language }) };
        var result = await ApplicationBackgroundOperations.RunAsync(Kind, title, null, state.Input, async attempt =>
        {
            using var runtime = new ReaderTranslationRuntime(journal);
            var layer = parts.ToArray();
            var level = 0;
            do
            {
                var next = new List<string>();
                var size = level == 0 ? 1 : 2;
                for (var i = 0; i < layer.Length; i += size)
                {
                    attempt.ThrowIfCancellationRequested();
                    var key = level + ":" + i;
                    progress?.Report($"{level + 1} · {i / size + 1} / {(layer.Length + size - 1) / size}");
                    if (!completed.TryGetValue(key, out var answer))
                    {
                        var input = string.Join("\n\n", layer.Skip(i).Take(size));
                        answer = generate is null ? await runtime.SummarizeAsync(model, input, language, attempt) : await generate(input, attempt);
                        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 2500) throw new InvalidDataException("Reader.Translation.InvalidReply");
                        attempt.ThrowIfCancellationRequested();
                        completed[key] = answer;
                        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Completed = completed.Count, Level = level });
                    }
                    next.Add(answer);
                }
                layer = next.ToArray(); level++;
            } while (layer.Length > 1);
            return new OfficeSummaryResult(layer[0], [new(model.ModelArtifactId, model.DisplayName, model.Revision, "document summary")]);
        }, token, state);
        ApplicationBackgroundOperations.Current?.Acknowledge(state.Id);
        return result;
    }

    internal static async Task SaveAsync(OfficeSummaryResult result, string path, CancellationToken token)
    {
        var facts = new OutputProvenance { Scenario = Kind, CreatedUtc = DateTimeOffset.UtcNow, ModelHistory = "recorded", Models = result.Models };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, "<!-- LOPATA " + facts.Json() + " -->\n\n" + result.Text,
                new System.Text.UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
