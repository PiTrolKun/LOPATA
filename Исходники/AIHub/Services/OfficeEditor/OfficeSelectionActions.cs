using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.OfficeEditor;

internal static class OfficeSelectionActions
{
    internal const string Kind = "office.text.rephrase";

    internal static Uri SearchUri(string text) => new("https://www.google.com/search?q=" + Uri.EscapeDataString(text.Trim()));
    internal static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    internal static async Task<string> RephraseAsync(string text, string style, ManagedModelArtifactCard model,
        OfficeAiJournal journal, string title, CancellationToken token)
    {
        var state = new BackgroundOperationState { Id = Guid.NewGuid().ToString("N"), Kind = Kind, Title = title, Private = true,
            Input = JsonSerializer.SerializeToElement(new { Model = model.ModelArtifactId }) };
        var result = await ApplicationBackgroundOperations.RunAsync(Kind, title, null, state.Input, async attempt =>
        {
            using var runtime = new ReaderTranslationRuntime(journal);
            return await runtime.RephraseAsync(model, text, style, attempt);
        }, token, state);
        ApplicationBackgroundOperations.Current?.Acknowledge(state.Id);
        return result;
    }
}
