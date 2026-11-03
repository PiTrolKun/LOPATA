using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private LetItOutChatRuntime? _letItOutRuntime;
    private LetItOutChatWork? _letItOutWork;
    private LetItOutConversationStore? _letItOutLibrary;
    private string? _letItOutCorePath;
    private bool _openingLetItOut;
    private readonly Dictionary<string, LetItOutChatCheckpoint> _letItOutResults = [];

    private void ConfigureLetItOutChat(LetItOutChatControl control)
    {
        _letItOutRuntime ??= new LetItOutChatRuntime(_userContextService);
        _letItOutRuntime.DecideGpu = control.DecideGpuAsync;
        _letItOutRuntime.ModeChanged = control.ShowMode;
        _letItOutRuntime.ContextMeasured = control.AcceptContextSnapshot;
        control.ContextInfoChanged -= RefreshUniversalMemoryIndicator;
        control.ContextInfoChanged += RefreshUniversalMemoryIndicator;
        _letItOutWork ??= new LetItOutChatWork(_letItOutRuntime);
        control.Configure(L);
        _letItOutLibrary ??= new LetItOutConversationStore();
        _letItOutRuntime.MemoryStore = _letItOutLibrary;
        control.ConfigureHistory(_letItOutLibrary);
        control.ConfigureFlowInput();
        control.ConfigureAttachments(PrepareLetItOutImagesAsync,
            path => _fileViewerService.Open(this, path, _appSettings.FileViewer, _isDarkTheme, _localizationService));
        control.ConfigureAudioAttachments(PrepareLetItOutAudioAsync, () => System.Windows.MessageBox.Show(this,
            L("LetItOut.Attachments.AudioNotice"), L("LetItOut.Attachments.AudioKind"), MessageBoxButton.OK, MessageBoxImage.Information));
        _letItOutRuntime.AudioThreads = () => _appSettings.Dictation.EffectiveThreads;
        _letItOutRuntime.AttachmentStageChanged = control.ShowAttachmentStage;
        ConfigureChatSpeech(control);
        control.ConfigureRuntime(() => _letItOutCorePath, _letItOutWork, () => _localizationService.CurrentLanguageCode);
        control.ConfigureWarmup(_letItOutRuntime);
        control.AnswerCompleted -= RememberLetItOutResult;
        control.AnswerCompleted += RememberLetItOutResult;
    }

    private void RememberLetItOutResult(LetItOutChatCheckpoint checkpoint) =>
        _letItOutResults[checkpoint.Request.TurnId] = checkpoint;

    private async Task OpenLetItOutAsync()
    {
        if (_openingLetItOut) return;
        _openingLetItOut = true;
        try
        {
            var pending = _backgroundOperations?.PendingOperations
                .Where(state => state.Kind is LetItOutChatWork.Kind or LetItOutChatWarmup.Kind or LetItOutChatRuntime.MemoryWorkKind or LetItOutChatRuntime.TreeWorkKind)
                .OrderByDescending(state => state.Project == ScenarioNavigationPage.LetItOutChat?.SessionId)
                .ThenByDescending(state => state.UpdatedUtc).FirstOrDefault();
            if (pending is not null)
            {
                _letItOutCorePath = pending.Kind == LetItOutChatWarmup.Kind
                    ? pending.Input.Deserialize<LetItOutWarmupRequest>()?.ModelPath
                    : pending.Kind == LetItOutChatRuntime.TreeWorkKind
                        ? pending.Input.Deserialize<LetItOutTreeRequest>()?.ModelPath
                    : pending.Kind == LetItOutChatRuntime.MemoryWorkKind
                        ? pending.Input.Deserialize<LetItOutMemoryCloseRequest>()?.ModelPath
                        : pending.Input.Deserialize<LetItOutChatRequest>()?.CoreModelPath;
                var chat = RevealLetItOutChat();
                if (!chat.IsWorking)
                {
                    if (pending.Kind == LetItOutChatWarmup.Kind) chat.ShowWarmupPending(pending);
                    else if (pending.Kind is LetItOutChatRuntime.MemoryWorkKind or LetItOutChatRuntime.TreeWorkKind) chat.OpenConversation(pending.Project!);
                    else chat.ShowPending(pending);
                }
                return;
            }
            if (_backgroundOperations?.BlocksNewWork == true)
            {
                StatusText.Text = L("LetItOut.Busy");
                return;
            }
            // Allow the route's click to settle before entering the standard modal preparation flow.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var preparation = new LetItOutChatPreparationWindow(this, _storageSettings,
                _appSettings.ModelDownloads.MaximumParallelConnections, L, _backgroundLifetime.Token);
            if (preparation.ShowDialog() != true) return;
            _letItOutCorePath = preparation.CoreModelPath;
            RevealLetItOutChat();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            StatusText.Text = L("LetItOut.PreparationFailed");
        }
        finally { _openingLetItOut = false; }
    }

    private LetItOutChatControl RevealLetItOutChat()
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach (var page in new FrameworkElement[] { FinancialPage, CapturePage, GenerationPage,
            ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost }) page.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        ScenarioNavigationPage.ShowLetItOutChat();
        var control = ScenarioNavigationPage.LetItOutChat!;
        ConfigureLetItOutChat(control);
        return control;
    }

    private void RegisterLetItOutBackgroundOperation()
    {
        _backgroundOperations!.Register(LetItOutConversationExport.Kind, async (state, token) =>
        {
            var request = state.Input.Deserialize<LetItOutExportRequest>() ?? throw new InvalidOperationException("LetItOut.InvalidCheckpoint");
            await LetItOutConversationExport.RunAsync(_letItOutLibrary ??= new(), request, token, state);
        });
        _backgroundOperations!.Register(LetItOutChatRuntime.TreeWorkKind,async(state,token)=>
        {
            var request=state.Input.Deserialize<LetItOutTreeRequest>()??throw new InvalidOperationException("LetItOut.InvalidCheckpoint");
            _letItOutCorePath=request.ModelPath;
            await RevealLetItOutChat().ResumeTreeAsync(state,token);
        });
        _backgroundOperations!.Register(LetItOutChatWarmup.Kind, async (state, token) =>
        {
            _letItOutCorePath = state.Input.Deserialize<LetItOutWarmupRequest>()?.ModelPath;
            await RevealLetItOutChat().ResumeWarmupAsync(state, token);
        });
        _backgroundOperations.Register(LetItOutChatRuntime.MemoryWorkKind, async (state, token) =>
        {
            var request = state.Input.Deserialize<LetItOutMemoryCloseRequest>() ?? throw new InvalidOperationException("Missing memory input.");
            _letItOutCorePath = request.ModelPath;
            RevealLetItOutChat().OpenConversation(request.ConversationId);
            await _letItOutWork!.CloseMemoryAsync(request, state, token);
        });
        _backgroundOperations!.Register(LetItOutChatWork.Kind, async (state, token) =>
        {
            var request = state.Input.Deserialize<LetItOutChatRequest>() ?? throw new InvalidOperationException("Missing chat input.");
            _letItOutCorePath = request.CoreModelPath;
            await RevealLetItOutChat().ResumeAsync(state, token);
        });
        Closed += (_, _) =>
        {
            ScenarioNavigationPage.LetItOutChat?.Dispose();
            _letItOutRuntime?.Dispose();
            _letItOutResults.Clear();
        };
    }

    private void ViewLetItOutResult(BackgroundOperationNotice notice)
    {
        RestoreFromTray();
        if (notice.Kind is LetItOutChatRuntime.MemoryWorkKind or LetItOutChatRuntime.TreeWorkKind && notice.Project is { } id)
        {
            RevealLetItOutChat().OpenConversation(id);
            _backgroundOperations!.Acknowledge(notice.Id); return;
        }
        if (ScenarioNavigationPage.LetItOutChat is { } chat && chat.SessionId == notice.Project)
            RevealLetItOutChat();
        else if (_letItOutResults.TryGetValue(notice.Id, out var result))
        {
            var text = string.Join("\n\n", result.Request.Messages.Select(message =>
                L(message.Role == "user" ? "LetItOut.You" : "LetItOut.Assistant") + ":\n" + message.Content));
            ShowBackgroundResultPreview(L("LetItOut.Title"), text + "\n\n" + L("LetItOut.Assistant") + ":\n" + result.Result);
        }
        _backgroundOperations!.Acknowledge(notice.Id);
    }
}
