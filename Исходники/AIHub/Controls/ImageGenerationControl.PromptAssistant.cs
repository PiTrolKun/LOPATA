using System.Text.Json;
using System.IO;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private IImagePromptAssistant? _promptAssistant;
    private Func<string?>? _corePath;
    private string? _appliedPromptRequest;
    private TextBlock? _promptPlaceholder;
    private bool IsPromptOperation => ApplicationBackgroundOperations.Current?.State?.Kind == ImagePromptAssistant.BackgroundKind;
    public void ConfigurePromptAssistant(IImagePromptAssistant assistant, Func<string?> corePath)
    { _promptAssistant = assistant; _corePath = corePath; }

    private async Task AssistPromptAsync()
    {
        if (_busy || HasPendingGeneration() || _promptAssistant is null || _promptBox is null || _sessionDirectory is null) return;
        var path = _corePath?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) { Status(L("PromptCoreMissing")); return; }
        var request = new ImagePromptAssistRequest(Guid.NewGuid().ToString("N"), _promptBox.Text, _languageCode, path, _sessionDirectory);
        await AssistPromptAsync(request, CancellationToken.None);
    }

    private async Task AssistPromptAsync(ImagePromptAssistRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_promptAssistant is null) throw new InvalidOperationException("Generation.PromptCoreMissing");
        _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Status(L("PromptWorking")); Render();
        try
        {
            restored ??= new BackgroundOperationState { Id = request.Id, Kind = ImagePromptAssistant.BackgroundKind,
                Title = L("PromptAssistant"), Project = request.SessionDirectory, Input = JsonSerializer.SerializeToElement(request) };
            var text = await ApplicationBackgroundOperations.RunAsync(ImagePromptAssistant.BackgroundKind, L("PromptAssistant"),
                request.SessionDirectory, request, async attempt =>
                {
                    if (ImagePromptAssistStore.Read(request) is { } saved) return saved;
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    try
                    {
                        var answer = await _promptAssistant.GenerateAsync(request, attempt);
                        attempt.ThrowIfCancellationRequested();
                        answer = ImagePromptAssistant.CleanResponse(answer);
                        ImagePromptAssistStore.Save(request, answer);
                        return answer;
                    }
                    finally { await ApplicationBackgroundOperations.RetireModelsAsync(); }
                }, _cancel.Token, restored);
            ApplyPreparedPrompt(request, text); Status(L("PromptReady"));
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception error) { Error(error); }
        finally
        {
            _busy = false; _cancel.Dispose(); _cancel = null; Render();
            if (_page == 2) { _promptBox?.Focus(); _promptBox?.Select(_promptBox.Text.Length, 0); }
        }
    }

    private void ApplyPreparedPrompt(ImagePromptAssistRequest request, string text)
    {
        if (_appliedPromptRequest == request.Id) return;
        // SelectedText participates in WPF's undo stack; assigning Text would erase it.
        ReplacePrompt(text); _appliedPromptRequest = request.Id;
    }

    private void ReplacePrompt(string text)
    {
        if (_promptBox is null) { _prompt = text; return; }
        // A cold tray restore can edit the new field before WPF's first layout pass.
        _promptBox.ApplyTemplate();
        var enabled = _promptBox.IsEnabled;
        _promptBox.IsEnabled = true; // WPF does not record edits made to a disabled text editor.
        _promptBox.BeginChange();
        try { _promptBox.SelectAll(); _promptBox.SelectedText = text; }
        finally { _promptBox.EndChange(); _promptBox.IsEnabled = enabled; }
        _prompt = _promptBox.Text;
    }

    public async Task ResumePromptAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<ImagePromptAssistRequest>() ?? throw new InvalidDataException("Invalid prompt checkpoint.");
        Restore(request.SessionDirectory); ReplacePrompt(request.Prompt);
        await AssistPromptAsync(request, token, state);
    }

    public void RestorePromptResult(BackgroundOperationNotice notice)
    {
        if (notice.Kind != ImagePromptAssistant.BackgroundKind || notice.Project is null)
            throw new InvalidDataException("Generation.PromptInvalidReceipt");
        var request = ImagePromptAssistStore.ReadRequest(notice.Project, notice.Id);
        Restore(request.SessionDirectory);
        if (ImagePromptAssistStore.Read(request) is { } text) { ApplyPreparedPrompt(request, text); Status(L("PromptReady")); }
    }
}
