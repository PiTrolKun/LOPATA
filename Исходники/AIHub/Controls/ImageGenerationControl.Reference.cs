using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private IImageReferenceAnalyzer? _referenceAnalyzer;
    private Func<ImageAnalysisBundleInstallationSnapshot>? _checkReference;
    private Func<ImageAnalysisBundleInstallationSnapshot, Task<ImageAnalysisBundleInstallationSnapshot>>? _prepareReference;
    private Func<Task<ImageReferenceSelection?>>? _selectReference;
    private readonly ImageReferenceSessionGate _referenceGate = new();
    private readonly HashSet<string> _appliedReferences = [];
    private bool IsReferenceOperation => ApplicationBackgroundOperations.Current?.State?.Kind == ImageReferenceAnalyzer.BackgroundKind;
    public void ConfigureReferences(IImageReferenceAnalyzer analyzer, Func<ImageAnalysisBundleInstallationSnapshot> check,
        Func<ImageAnalysisBundleInstallationSnapshot, Task<ImageAnalysisBundleInstallationSnapshot>> prepare,
        Func<Task<ImageReferenceSelection?>>? select = null)
    { _referenceAnalyzer = analyzer; _checkReference = check; _prepareReference = prepare; _selectReference = select ?? SelectReferenceAsync; }

    private async Task AttachReferenceAsync()
    {
        if (_busy || HasPendingGeneration() || _referenceAnalyzer is null || _sessionDirectory is null) return;
        _busy = true; Render();
        ImageReferenceRequest? request = null;
        try
        {
            var snapshot = _referenceGate.Get(_sessionDirectory, _checkReference!);
            if (!snapshot.CanStart)
            {
                snapshot = await _prepareReference!(snapshot); _referenceGate.Update(_sessionDirectory, snapshot);
                Status(L(snapshot.CanStart ? "ReferenceRetry" : "ReferenceNotReady")); return;
            }
            var selection = await _selectReference!(); if (selection is null) return;
            request = await ImageReferenceStore.CreateAsync(_sessionDirectory, _prompt, _languageCode, selection, ApplicationBackgroundOperations.ExitToken);
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception error) { ReferenceError(error); }
        finally { _busy = false; Render(); }
        if (request is not null) await AnalyzeReferenceAsync(request, CancellationToken.None);
    }

    private Task<ImageReferenceSelection?> SelectReferenceAsync()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = L("ReferenceTitle"), Filter = _l("ImageAnalysis.Workspace.DialogFilter"), Multiselect = false, CheckFileExists = true };
        var owner = Window.GetWindow(this);
        if (picker.ShowDialog(owner) != true) return Task.FromResult<ImageReferenceSelection?>(null);
        var window = new ImageReferenceSelectionWindow(picker.FileName, _l) { Owner = owner };
        return Task.FromResult(window.ShowDialog() == true ? window.Selection : null);
    }

    private async Task AnalyzeReferenceAsync(ImageReferenceRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_referenceAnalyzer is null) throw new InvalidOperationException("Generation.ReferenceBetaRequired");
        _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); Status(L("ReferenceWorking")); Render();
        try
        {
            restored ??= new BackgroundOperationState { Id = request.Id, Kind = ImageReferenceAnalyzer.BackgroundKind,
                Title = L("ReferenceTitle"), Project = request.SessionDirectory, Input = JsonSerializer.SerializeToElement(request) };
            var fragment = await ApplicationBackgroundOperations.RunAsync(ImageReferenceAnalyzer.BackgroundKind, L("ReferenceTitle"), request.SessionDirectory,
                request, async attempt =>
                {
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    if (ImageReferenceStore.Read(request) is { } saved) return saved;
                    try
                    {
                        var answer = await _referenceAnalyzer.GenerateAsync(request, attempt);
                        attempt.ThrowIfCancellationRequested(); answer = ImagePromptAssistant.CleanResponse(answer);
                        ImageReferenceStore.Save(request, answer); return answer;
                    }
                    finally { await ApplicationBackgroundOperations.RetireModelsAsync(); }
                }, _cancel.Token, restored);
            ApplyReference(request, fragment); Status(L("ReferenceReady"));
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception error) { ReferenceError(error); }
        finally
        {
            _busy = false; _cancel.Dispose(); _cancel = null; Render();
            if (_page == 2) { _promptBox?.Focus(); _promptBox?.Select(_promptBox.Text.Length, 0); }
        }
    }
    private void ReferenceError(Exception error)
    {
        if (error.Message == "Generation.PromptEmptyReply") Status(L("ReferenceEmptyReply"));
        else if (error.Message.StartsWith("Generation.", StringComparison.Ordinal)) Error(error);
        else Status(L("ReferenceFailed") + "\n" + error.Message);
    }
    private void ApplyReference(ImageReferenceRequest request, string fragment)
    {
        if (_appliedReferences.Contains(request.Id)) return;
        ReplacePrompt(ImageReferenceStore.Append(_prompt, fragment)); _appliedReferences.Add(request.Id);
    }
    public async Task ResumeReferenceAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<ImageReferenceRequest>() ?? throw new InvalidDataException("Generation.ReferenceInvalidReceipt");
        Restore(request.SessionDirectory); if (!_appliedReferences.Contains(request.Id)) ReplacePrompt(request.Draft);
        await AnalyzeReferenceAsync(request, token, state);
    }
    public void RestoreReferenceResult(BackgroundOperationNotice notice)
    {
        if (notice.Kind != ImageReferenceAnalyzer.BackgroundKind || notice.Project is null) throw new InvalidDataException("Generation.ReferenceInvalidReceipt");
        var request = ImageReferenceStore.ReadRequest(notice.Project, notice.Id);
        if (_appliedReferences.Contains(request.Id)) return;
        var sameSession = string.Equals(_sessionDirectory, request.SessionDirectory, StringComparison.OrdinalIgnoreCase);
        Restore(request.SessionDirectory); if (!sameSession) ReplacePrompt(request.Draft);
        if (ImageReferenceStore.Read(request) is { } fragment) { ApplyReference(request, fragment); Status(L("ReferenceReady")); }
    }
}
