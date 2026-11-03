using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub;

public partial class MainWindow
{
    private readonly ChatSpeechWork _chatSpeech = new();
    private readonly ChatSpeechPlayer _chatSpeechPlayer = new();
    private CancellationTokenSource? _chatSpeechCancel;
    private Task? _chatSpeechTask;
    private string? _chatSpeechKey;
    private long _chatSpeechCommand;

    private void ConfigureChatSpeech(LetItOutChatControl control)
    {
        control.ConfigureSpeech(ReadChatMessageAsync, StopChatSpeechAsync, CancelChatSpeech, () => _appSettings.ChatSpeech?.AutoRead == true);
        control.IsVisibleChanged -= ChatSpeechVisibilityChanged; control.IsVisibleChanged += ChatSpeechVisibilityChanged;
        RefreshChatSpeechHeader();
    }
    private void ChatSpeechVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshChatSpeechHeader();
    private void RefreshChatSpeechHeader()
    {
        if (ChatSpeechSettingsButton is null) return;
        ChatSpeechSettingsButton.Visibility = ScenarioNavigationPage.LetItOutChat?.IsVisible == true ? Visibility.Visible : Visibility.Collapsed;
        ChatSpeechSettingsButton.ToolTip = L("ChatSpeech.Settings");
        System.Windows.Automation.AutomationProperties.SetName(ChatSpeechSettingsButton, L("ChatSpeech.Settings"));
    }
    private void CancelChatSpeech()
    {
        ++_chatSpeechCommand; _chatSpeechCancel?.Cancel(); _chatSpeechPlayer.Stop();
    }
    private async Task StopChatSpeechAsync()
    {
        CancelChatSpeech();
        if (_chatSpeechTask is { IsCompleted: false } task) await task;
    }

    private async Task ReadChatMessageAsync(ChatSpeechMessage message, bool automatic)
    {
        var same = _chatSpeechKey == message.Key && _chatSpeechTask is { IsCompleted: false };
        await StopChatSpeechAsync(); if (same && !automatic) return;
        if (ScenarioNavigationPage.LetItOutChat is not { IsVisible: true } control || control.SessionId != message.Conversation) return;
        var serial = ++_chatSpeechCommand;
        _chatSpeechTask = ReadCoreAsync(); await _chatSpeechTask;
        async Task ReadCoreAsync()
        {
            using var audit = ComponentAudit.CaptureOperation();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_backgroundLifetime.Token);
            _chatSpeechCancel = cancel; _chatSpeechKey = message.Key;
            try
            {
                // Install the task before preparation can report state through the controller.
                await System.Windows.Threading.Dispatcher.Yield();
                if (serial != _chatSpeechCommand) return;
                control.SpeechRequested = true; control.ShowSpeech(message.Key, "ChatSpeech.Preparing");
                var settings = _appSettings.ChatSpeech ??= new(); var voice = settings.Choice();
                var request = new ChatSpeechRequest(Guid.NewGuid().ToString("N"), message, voice, L("ChatSpeech.Task"));
                var audio = _chatSpeech.Cached(request);
                if (audio is null)
                {
                    await control.PrepareSpeechAsync();
                    ChatSpeechWork.RecoverFailedAttempts(_backgroundOperations!);
                    while (control.IsWorking || control.IsWarming || _backgroundOperations?.BlocksNewWork == true)
                    {
                        if (!control.IsWorking && !control.IsWarming && _backgroundOperations is { BlocksNewWork: true, IsRunning: false })
                            throw new BackgroundOperationWaitingException("VideoEditor.Busy");
                        await Task.Delay(150, cancel.Token);
                    }
                    if (automatic && new ManagedModelLibraryStore().Load(VideoNarratorCatalog.ArtifactId(voice.Id))?.Status != ManagedModelStatuses.Installed)
                        throw new InvalidOperationException("ChatSpeech.Missing");
                    var root = new ManagedModelLibraryStore().Load(VideoNarratorCatalog.ArtifactId(voice.Id))?.ModelsRoot;
                    if (await PrepareVideoModelsAsync(this, new("speech", [voice.Id], root), cancel.Token) is null) return;
                    cancel.Token.ThrowIfCancellationRequested();
                    audio = await _chatSpeech.GenerateAsync(request, cancel.Token);
                }
                if (!control.IsVisible || control.SessionId != message.Conversation || serial != _chatSpeechCommand) return;
                control.ShowSpeech(message.Key, "ChatSpeech.Stop");
                await _chatSpeechPlayer.PlayAsync(audio, cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { SpeechError(error); }
            finally
            {
                if (ReferenceEquals(_chatSpeechCancel, cancel))
                {
                    _chatSpeechCancel = null; _chatSpeechKey = null;
                    control.SpeechRequested = false; control.ShowSpeech(null, null);
                }
            }
        }
    }

    private void SpeechError(Exception error)
    {
        ComponentAuditFailures.Report(error);
        StatusText.Text = error.Message.StartsWith("ChatSpeech.", StringComparison.Ordinal)
            || error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal) ? L(error.Message) : L("ChatSpeech.Failed");
        OwnedProcessRegistry.Log("chat_speech_failed", "LetItOut.Speech",
            detail: $"{error.GetType().Name}; cause={error.GetBaseException().GetType().Name}");
    }

    private async void ChatSpeechSettings_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await StopChatSpeechAsync(); var settings = _appSettings.ChatSpeech ??= new();
            var initial = settings.Choice(); var profiles = new Dictionary<string, VideoNarratorChoice>();
            foreach (var pair in settings.Profiles ?? [])
                try { var p = pair.Value; VideoNarratorCatalog.Validate(pair.Key, p.Language, p.Rate, p.Options); profiles[pair.Key] = new(pair.Key, p.Language, p.Rate, p.Options); }
                catch (InvalidDataException) { }
            VideoVoiceWindow? window = null;
            var extension = new VideoVoiceWindowExtension(L("ChatSpeech.Settings"), L("ChatSpeech.Auto"), ChatSpeechText.TestPhrase, async (choice, text, token) =>
            {
                var control = ScenarioNavigationPage.LetItOutChat;
                try
                {
                    if (control is not null) await control.PrepareSpeechAsync();
                    if (_backgroundOperations?.BlocksNewWork == true) throw new InvalidOperationException("VideoEditor.Busy");
                    var root = new ManagedModelLibraryStore().Load(VideoNarratorCatalog.ArtifactId(choice.Id))?.ModelsRoot;
                    if (await PrepareVideoModelsAsync(window!, new("speech", [choice.Id], root), token) is null) throw new OperationCanceledException();
                    var message = new ChatSpeechMessage(Guid.Empty.ToString("N"), ChatSpeechWork.Hash(text), text);
                    return (await _chatSpeech.GenerateAsync(new(Guid.NewGuid().ToString("N"), message, choice, L("ChatSpeech.Task")), token)).Files[0];
                }
                finally { if (control is not null) control.SpeechRequested = false; }
            }, OpenChatSpeechFolder, async () =>
            {
                await StopChatSpeechAsync();
                if (_backgroundOperations?.BlocksNewWork == true) throw new InvalidOperationException("VideoEditor.Busy");
                await ApplicationBackgroundOperations.RetireModelsAsync();
            }, profiles);
            window = new VideoVoiceWindow(this, L, initial, settings.AutoRead,
                (need, token) => PrepareVideoModelsAsync(window!, need, token), extension: extension);
            if (window.ShowDialog() != true || window.Choice is not { } selected) return;
            foreach (var choice in window.Profiles.Values) settings.Apply(choice);
            settings.Apply(selected); settings.AutoRead = window.Global; _appSettingsStore.Save(_appSettings);
        }
        catch (Exception error) { SpeechError(error); }
    }

    private void OpenChatSpeechFolder()
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(_chatSpeech.Root); Directory.CreateDirectory(_chatSpeech.Root);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add(_chatSpeech.Root); Process.Start(start);
    }
    private void RegisterChatSpeech()
    {
        _backgroundOperations!.Register(ChatSpeechWork.Kind, async (state, token) =>
        {
            var request = state.Input.Deserialize<ChatSpeechRequest>() ?? throw new InvalidDataException("ChatSpeech.Invalid");
            await _chatSpeech.GenerateAsync(request, token, state);
        });
        Closed += (_, _) => { CancelChatSpeech(); _chatSpeechPlayer.Dispose(); };
    }
}
