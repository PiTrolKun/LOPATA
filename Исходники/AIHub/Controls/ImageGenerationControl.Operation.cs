using System.Security.Cryptography;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private static bool IsGenerationOperation(string kind) => kind is ImageGenerationCatalog.BackgroundKind or ImagePromptAssistant.BackgroundKind or ImageReferenceAnalyzer.BackgroundKind;
    private bool HasPendingGeneration() => ApplicationBackgroundOperations.Current is { HasPending: true, State: { } state } && IsGenerationOperation(state.Kind);
    private async Task PauseResumeAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State is not { } state || !IsGenerationOperation(state.Kind)) return;
        if (controller.State.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing) await controller.PauseAsync();
        else await controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
        RefreshBackgroundStatus();
    }
    private async Task SendAsync()
    {
        if (_busy || HasPendingGeneration() || _promptBox is null || string.IsNullOrWhiteSpace(_promptBox.Text)) return;
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), _modelId, _promptBox.Text, _width, _height,
            Enumerable.Range(0, _count).Select(_ => (long)RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray(), _modelsRoot, _sessionDirectory!);
        ImageGenerationCatalog.Validate(request);
        if (!EnsureOutputFolder()) return;
        request = WithDelivery(request);
        ImageGenerationSessionStore.AddTurn(request); // Publish the accepted raw prompt before preparation or model work.
        _promptBox.Text = ""; _prompt = ""; _selectedTurnId = request.Id; _selectedResultIndex = 0;
        await GenerateAsync(request, CancellationToken.None);
    }
    private ImageGenerationRequest WithDelivery(ImageGenerationRequest request) => request with
    {
        OutputFolder = _settings.Folder, SubmittedAt = DateTimeOffset.Now,
        FirstGenerationNumber = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Sum(t => t.Request.Seeds.Length) + 1,
        Metadata = new(ImageGenerationMetadata.Author(_metadataAuthor())),
        OutputLongestSide = ImageOutputDimensions.Normalize(_settings.OutputLongestSide)
    };
    private async Task NewVariantAsync(ImageGenerationRequest previous)
    {
        if (_busy || HasPendingGeneration()) return;
        if (!ImageGenerationCatalog.IsAvailable(previous.ModelId)) throw new InvalidOperationException("Generation.ModelUnavailable");
        if (!EnsureOutputFolder()) return;
        _modelId = previous.ModelId;
        var request = WithDelivery(previous with { Id = Guid.NewGuid().ToString("N"), Seeds = [(long)RandomNumberGenerator.GetInt32(int.MaxValue)] });
        ImageGenerationSessionStore.AddTurn(request); _selectedTurnId = request.Id; _selectedResultIndex = 0;
        await GenerateAsync(request, CancellationToken.None);
    }
    private async Task GenerateAsync(ImageGenerationRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); Status(L("Generating")); Render();
        try
        {
            await ApplicationBackgroundOperations.RunAsync(ImageGenerationCatalog.BackgroundKind, L("Title"), request.SessionDirectory, request,
                async attempt =>
                {
                    var cards = await _installation.PrepareAsync(request.ModelsRoot, request.ModelId, false, null, attempt);
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    return await _runtime.RunAsync(request, cards, () => Dispatcher.InvokeAsync(() =>
                    { _selectedTurnId = request.Id; _selectedResultIndex = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single(t => t.Request.Id == request.Id).Results.Last().Index; if (_page == 2) Render(); }), attempt);
                }, _cancel.Token, restored);
            Status(L("Ready"));
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception error) { Error(error); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; Render(); }
    }
}
