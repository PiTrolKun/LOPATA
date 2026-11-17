using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualScenario;

internal sealed record VisualAdvisorJob(string ProjectId, string Id, string Advisor, bool Compose, VisualInput[] Images)
{
    public bool Warmup { get; init; }
    public bool ForceCpu { get; init; }
}

internal sealed partial class VisualScenarioControl
{
    private ContextMenu? _chatTextMenu;
    private Task? _advisorWarmup;
    private readonly Dictionary<string, CheckBox> _fragmentSwitches = [];
    private static readonly JsonSerializerOptions AdvisorPromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private VisualFragment[] SelectedFragments() => _project.Messages.SelectMany(message => message.Fragments).Where(fragment => fragment.Selected).ToArray();

    internal BackgroundOperationState? Pending => ApplicationBackgroundOperations.Current?.PendingOperations
        .FirstOrDefault(state => state.Project == _project.Id && (state.Kind is VisualAdvisor.ChatKind or DrawingKind));

    private bool CanEditSettings => !_changingModel && (_warmingAdvisor || !_busy && Pending is null);

    internal void UpdateCommands()
    {
        var pending = Pending;
        var free = !_busy && !_changingModel && pending is null;
        _input.IsEnabled = free || _warmingAdvisor;
        _send.IsEnabled = _draw.IsEnabled = _attach.IsEnabled = free;
        if (_generationSettings is not null) _generationSettings.IsEnabled = CanEditSettings;
        _attach.Visibility = VisualComponents.CanSeeImages(_project.Advisor) ? Visibility.Visible : Visibility.Collapsed;
        _pause.Visibility = _continue.Visibility = _cancel.Visibility = pending is not null || _busy ? Visibility.Visible : Visibility.Collapsed;
        _pause.IsEnabled = pending?.Phase == BackgroundOperationPhase.Running;
        _continue.IsEnabled = pending?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting;
        _cancel.IsEnabled = _busy || pending is not null;
        _context.IsEnabled = free && _project.Messages.Count > 0;
        _context.Update(_meter, LiteraryContextWindowParts.Meter(_meter, _localize), _localize("Studio.Context.Title"));
    }

    private async Task ChangeModelAsync(string id, bool artist)
    {
        if (!CanEditSettings) { RenderLeft(); return; }
        if (id == (artist ? _project.Artist : _project.Advisor)) return;
        var warmAdvisor = false;
        _changingModel = true; UpdateCommands();
        try
        {
            if (artist)
            {
                if (PrepareArtist(id, true) is not null)
                {
                    await _artistSession.RetireAsync();
                    if (_project.Artist == VisualSenseNova.Id && _project.Advisor != VisualSenseNova.Id)
                        await _senseNova.RetireAsync();
                    _project.Artist = id;
                }
            }
            else if (PrepareAdvisor(id) is not null)
            {
                if (_warmingAdvisor && _advisorWarmup is { } running)
                {
                    _attempt?.Cancel();
                    try { await running; }
                    catch (OperationCanceledException) when (!ApplicationBackgroundOperations.ExitToken.IsCancellationRequested) { }
                }
                if (Pending is { } pending && pending.Input.TryGetProperty(nameof(VisualAdvisorJob.Warmup), out var flag)
                    && flag.ValueKind == JsonValueKind.True)
                    ApplicationBackgroundOperations.Current?.DiscardPending(pending.Id);
                _project.Advisor = id;
                _meter = null;
                warmAdvisor = true;
            }
            Save();
        }
        finally { _changingModel = false; Render(); }
        if (id == (artist ? _project.Artist : _project.Advisor)) ShowSenseNovaHint(id, artist);
        if (warmAdvisor) await WarmAdvisorAsync();
    }

    private string? PrepareAdvisor(string id)
    {
        string? path = null;
        var window = new VideoPreparationWindow(OwnerWindow, _localize, _components.AdvisorName(id), async (download, progress, token) =>
            (path = await _components.PrepareAdvisorAsync(id, download, progress, token)) is not null, ApplicationBackgroundOperations.ExitToken);
        return BackgroundUserDecision.ShowDialog(window, OwnerWindow, ApplicationBackgroundOperations.ExitToken) == true ? path : null;
    }

    private Task<string?> RequireAdvisorAsync(string id) => RequirePreparationAsync(
        token => _components.PrepareAdvisorAsync(id, false, null, token), () => PrepareAdvisor(id));

    private async Task AttachAsync()
    {
        if (!VisualComponents.CanSeeImages(_project.Advisor) || _busy || Pending is not null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = T("Attach"), Multiselect = true,
            Filter = T("ImageFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var project = _project;
        foreach (var file in dialog.FileNames)
        {
            _ = await VisualAdvisor.ImageUrlAsync(file, ApplicationBackgroundOperations.ExitToken);
            if (project != _project) return;
            project.DraftImages.Add(_projects.Attach(project, file).Id);
        }
        Save();
        RenderDraftImages();
    }

    private void RenderDraftImages()
    {
        _attachments.Children.Clear();
        foreach (var id in _project.DraftImages)
            _attachments.Children.Add(ImageCard(id, T("Remove"), () => { _project.DraftImages.Remove(id); Save(); RenderDraftImages(); }));
    }

    private void RenderChat()
    {
        _fragmentSwitches.Clear();
        _chat.Children.Clear();
        foreach (var message in _project.Messages)
        {
            var panel = new StackPanel { Margin = new(0, 4, 0, 12) };
            panel.Children.Add(MusicWishUi.Text(T(message.User ? "You" : "Advisor"), true));
            if (!string.IsNullOrWhiteSpace(message.Text))
                panel.Children.Add(SelectableChatText(message.Text, message.User ? null : message.Id));
            foreach (var id in message.Images)
            {
                var image = _project.Images.Single(item => item.Id == id);
                panel.Children.Add(Thumbnail(_projects.ImagePath(_project, id), 110));
                var actions = new WrapPanel();
                actions.Children.Add(Toggle("UseReference", image.Selected, selected => SelectImage(id, selected)));
                actions.Children.Add(ButtonFor("EditImage", () => SetBase(id)));
                panel.Children.Add(actions);
            }
            foreach (var fragment in message.Fragments)
                panel.Children.Add(ChatFragment(fragment));
            _chat.Children.Add(panel);
        }
        _chatScroll.ScrollToEnd();
    }

    private StackPanel ChatFragment(VisualFragment fragment)
    {
        var block = new StackPanel();
        var toggle = Toggle("KeepFragment", fragment.Selected, selected => SelectFragment(fragment.Id, selected));
        _fragmentSwitches[fragment.Id] = toggle;
        block.Children.Add(SelectableChatText(fragment.Text));
        block.Children.Add(toggle);
        return block;
    }

    private void RevealFragment(string id)
    {
        if (!_fragmentSwitches.TryGetValue(id, out var toggle)) return;
        if (toggle.Parent is StackPanel block) block.BringIntoView();
        toggle.Focus();
        toggle.BringIntoView();
    }

    private TextBox SelectableChatText(string text, string? advisorMessageId = null) => new()
    {
        Text = text,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = System.Windows.Media.Brushes.Transparent,
        BorderThickness = new(0),
        Padding = new(0),
        Margin = new(0, 4, 0, 6),
        FontSize = 13,
        Tag = advisorMessageId,
        ContextMenu = _chatTextMenu ??= CreateChatTextMenu()
    };

    private ContextMenu CreateChatTextMenu()
    {
        var menu = new ContextMenu { FontSize = 13 };
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/VoiceNotifications/VoiceNotificationMenuResources.xaml", UriKind.Relative)
        });
        var copy = new MenuItem { Command = ApplicationCommands.Copy };
        var selectAll = new MenuItem { Command = ApplicationCommands.SelectAll };
        var makeFragment = new MenuItem();
        string? advisorMessageId = null;
        var selectedText = "";
        makeFragment.Click += (_, _) =>
        {
            if (advisorMessageId is { } id) MakeFragment(id, selectedText);
        };
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Items.Add(makeFragment);
        menu.Opened += (_, _) =>
        {
            copy.Header = T("CopyChatText");
            selectAll.Header = T("SelectChatText");
            copy.CommandTarget = selectAll.CommandTarget = menu.PlacementTarget;
            var textBox = menu.PlacementTarget as TextBox;
            advisorMessageId = textBox?.Tag as string;
            selectedText = advisorMessageId is null ? "" : textBox!.SelectedText.Trim().ReplaceLineEndings("\n");
            makeFragment.Header = T("MakeFragment");
            makeFragment.Visibility = advisorMessageId is null ? Visibility.Collapsed : Visibility.Visible;
            makeFragment.IsEnabled = selectedText.Length > 0 && !_busy && Pending is null;
        };
        menu.Closed += (_, _) => copy.CommandTarget = selectAll.CommandTarget = null;
        return menu;
    }

    private void MakeFragment(string messageId, string text)
    {
        if (text.Length == 0 || _busy || Pending is not null) return;
        var index = _project.Messages.FindIndex(message => !message.User && message.Id == messageId);
        if (index < 0 || index >= _chat.Children.Count || _chat.Children[index] is not StackPanel panel) return;
        var message = _project.Messages[index];
        var existing = message.Fragments.FirstOrDefault(fragment => fragment.Text.ReplaceLineEndings("\n") == text);
        if (existing is not null)
        {
            RevealFragment(existing.Id);
            return;
        }

        var fragment = new VisualFragment(Guid.NewGuid().ToString("N"), text);
        _project.Messages[index] = message with { Fragments = [.. message.Fragments, fragment] };
        if (!Save(current: false))
        {
            _project.Messages[index] = message;
            return;
        }
        panel.Children.Add(ChatFragment(fragment));
        _chatScroll.UpdateLayout();
        RevealFragment(fragment.Id);
    }

    private CheckBox Toggle(string key, bool selected, Action<bool> change)
    {
        var box = new CheckBox { Content = T(key), IsChecked = selected, Margin = new(0, 3, 5, 3), IsEnabled = !_busy };
        box.Checked += (_, _) => change(true);
        box.Unchecked += (_, _) => change(false);
        return box;
    }

    private void SelectImage(string id, bool selected)
    {
        if (_busy || Pending is not null) return;
        var index = _project.Images.FindIndex(image => image.Id == id);
        if (index < 0 || _project.Images[index].Selected == selected) return;
        _project.Images[index] = _project.Images[index] with { Selected = selected };
        Save(); RenderLeft(); RenderChat();
    }

    private void SelectFragment(string id, bool selected)
    {
        if (_busy || Pending is not null) return;
        for (var index = 0; index < _project.Messages.Count; index++)
        {
            var message = _project.Messages[index];
            if (!message.Fragments.Any(fragment => fragment.Id == id)) continue;
            _project.Messages[index] = message with { Fragments = message.Fragments.Select(fragment =>
                fragment.Id == id ? fragment with { Selected = selected } : fragment).ToArray() };
            Save(); RenderLeft();
            return;
        }
    }

    private void SetBase(string id)
    {
        if (_busy || Pending is not null) return;
        _project.BaseImage = id;
        SetActivePrompt(null);
        Save(); RenderLeft(); RenderGallery();
    }

    private async Task SendAsync()
    {
        if (_busy || Pending is not null) return;
        if (_project.DraftImages.Count > 0 && !VisualComponents.CanSeeImages(_project.Advisor))
            throw new InvalidOperationException("Visual.SelectGemma");
        var text = _project.Draft.Trim();
        var retry = text.Length == 0 && _project.DraftImages.Count == 0;
        if (retry && _project.Messages.LastOrDefault()?.User != true) return;
        var model = await RequireAdvisorAsync(_project.Advisor);
        if (model is null) return;
        if (!retry)
        {
            if (!_project.Messages.Any(message => message.User)) _settings.IsExpanded = false;
            _project.Messages.Add(new(Guid.NewGuid().ToString("N"), true, text, [], _project.DraftImages.ToArray()));
            _project.DraftImages.Clear();
            _project.Draft = "";
        }
        _project.Persistent = true;
        if (!Save()) return;
        await RunAdvisorAsync(new(_project.Id, Guid.NewGuid().ToString("N"), _project.Advisor, false, CurrentImages()), model);
    }

    internal void WarmAdvisorOnEntry() => RunUi(WarmAdvisorAsync);

    private async Task WarmAdvisorAsync()
    {
        if (_busy || Pending is not null || ApplicationBackgroundOperations.Current?.BlocksNewWork == true) return;
        var job = new VisualAdvisorJob(_project.Id, Guid.NewGuid().ToString("N"), _project.Advisor, false, CurrentImages()) { Warmup = true };
        _projects.Save(_project);
        await RunWarmupAsync(job);
    }

    private Task RunWarmupAsync(VisualAdvisorJob job, BackgroundOperationState? restored = null, CancellationToken token = default)
        => _advisorWarmup = RunAdvisorAsync(job, null, restored, token);

    private async Task RunAdvisorAsync(VisualAdvisorJob job, string? model, BackgroundOperationState? restored = null,
        CancellationToken token = default)
    {
        _busy = true; _warmingAdvisor = job.Warmup; _contextRejected = false;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _attempt = attempt;
        var status = T(job.Warmup ? "AdvisorWarming" : job.Compose ? "Composing" : "Thinking");
        _status.Text = status;
        if (job.Warmup) UpdateCommands();
        else Render();
        try
        {
            if (job.Warmup)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                attempt.Token.ThrowIfCancellationRequested();
                restored ??= new() { Id = job.Id, Kind = VisualAdvisor.ChatKind, Project = job.ProjectId, Title = status,
                    Input = JsonSerializer.SerializeToElement(job) };
            }
            await ApplicationBackgroundOperations.RunAsync(VisualAdvisor.ChatKind, T("Title"), job.ProjectId, job, async ct =>
            {
                if (!job.Compose && _project.Messages.Any(message => message.Id == job.Id)) return true;
                var path = model ?? await _components.PrepareAdvisorAsync(job.Advisor, false, null, ct)
                    ?? throw new BackgroundOperationFailedException("Generation.DownloadRequired");
                var gemma = job.Advisor == VisualComponents.Gemma;
                if (job.Warmup)
                {
                    var vision = gemma && (_project.DraftImages.Count > 0 || _project.Messages.Any(message =>
                        message.User && !_project.Hidden.Contains(message.Id) && (message.Images.Length > 0 || job.Images.Length > 0)));
                    await _advisor.PrepareAsync(path, gemma, vision, ct, job.ForceCpu, job.Advisor == VisualSenseNova.Id);
                    return true;
                }
                var (messages, images) = await BuildMessagesAsync(job, ct);
                var prepared = await _advisor.PrepareAsync(path, gemma, images, ct, job.ForceCpu, job.Advisor == VisualSenseNova.Id);
                if (!job.Compose) _meter = prepared.Measure(await _advisor.CountAsync(prepared, messages, ct));
                string answer;
                try { answer = await _advisor.CompleteAsync(prepared, messages, ct, structured: true); }
                catch (Exception error) when (error.Message == "Visual.ContextFull" || error is LetItOutChatContextException)
                {
                    throw new BackgroundOperationFailedException("Visual.ContextFull", error);
                }
                ct.ThrowIfCancellationRequested();
                if (job.Compose)
                {
                    var prompt = VisualAdvisor.PreparePrompt(answer, job.Images, _project.ManualLines);
                    _project.PromptDraft = prompt.Text;
                    _project.PreviewImages = prompt.Images;
                    _project.PromptAdvisor = _components.AdvisorUse(job.Advisor);
                }
                else
                {
                    var advice = VisualAdvisor.ParseAdvice(answer);
                    _project.Messages.Add(new(job.Id, false, advice.Reply, advice.Fragments.Select(text =>
                        new VisualFragment(Guid.NewGuid().ToString("N"), text)).ToArray(), []));
                }
                _projects.Save(_project);
                return true;
            }, attempt.Token, restored);
            if (job.Warmup) ApplicationBackgroundOperations.Current?.Acknowledge(job.Id);
            _status.Text = "";
        }
        catch (BackgroundOperationFailedException error) when (error.Message == "Visual.ContextFull")
        { _contextRejected = true; throw; }
        finally
        {
            _attempt = null;
            _busy = _warmingAdvisor = false;
            if (job.Warmup)
            {
                if (attempt.IsCancellationRequested) _status.Text = "";
                UpdateCommands();
            }
            else Render();
        }
        if (job.Compose && _project.PromptDraft is { } preparedPrompt)
            await ConfirmAndDrawAsync(new(preparedPrompt, _project.PreviewImages ?? job.Images, _project.PromptAdvisor));
    }

    private const string AdvisorInstructions = """
        You are the visual Advisor in LOPATA. Help the user build an image direction from incomplete ideas.
        Ask focused questions where decisions are missing. Discuss alternatives in plain language.
        Adapt to an original image, a new composition borrowing only selected traits from references,
        or a specific edit to a source. Ask what is to change or be preserved when ambiguous.
        Return JSON with "reply" (your conversational answer in the user's language) and "fragments"
        (an array of standalone image prompt suggestions). Include each concrete visual option you
        offer in "reply" as its own string in "fragments", in the same order. For example, if you
        describe three possible outfits, backgrounds, or compositions, provide three corresponding
        fragments. Each fragment must describe the option well enough to use independently.
        Carry the concrete visual details of each option from the reply into its fragment.
        Keep explanations, questions, and requests for clarification in "reply". Use an empty
        "fragments" array only when you do not offer a concrete visual option. Fragments are
        suggestions until individually approved. Do not run or claim to have run generation.
        """;

    private async Task<(IReadOnlyList<object> Messages, bool Images)> BuildMessagesAsync(VisualAdvisorJob job, CancellationToken token,
        IReadOnlySet<string>? excluded = null, string? summaryOverride = null)
    {
        List<object> messages = [];
        var role = job.Compose ? """
            Build one coherent scene description for the selected image generation model from the approved project material.
            Review every approved fragment. Carry forward every distinct compatible visual detail
            in full descriptive sentences. Scale the detail to the material, covering the subject,
            clothing, pose, framing, setting, lighting, and effects wherever supplied.
            Merge duplicates and resolve conflicting details individually, keeping the rest of each
            fragment usable. Use the supplied material as the source of the scene.
            Return a JSON object with "prompt" (the usable image prompt) and "roles" (short descriptions
            of the purpose of each supplied image in input order, in the user's language).
            Treat every item in the manual array as a required instruction. Preserve its precise details,
            including measurements and framing. Where a manual instruction conflicts with an approved fragment
            or an older draft, revise the conflicting wording to follow the manual instruction.
            Preserve the remaining approved constraints and exact image numbering. State what to
            preserve, change, or borrow from the images whenever the material specifies it.
            If a draft for revision is provided, use it as a starting point and incorporate all current changes.
            """ : AdvisorInstructions;
        var input = JsonSerializer.Serialize(new
        {
            approved = SelectedFragments().Select(fragment => fragment.Text), manual = _project.ManualLines,
            savedPrompt = job.Compose ? null : _project.SelectedPrompt?.Prompt.Text,
            draftForRevision = job.Compose ? _project.DraftForRevision : null,
            images = job.Images.Select((image, index) => new { number = index + 1, image.Role, image.IsBase })
        }, AdvisorPromptJson);
        if (job.Compose)
        {
            messages.Add(VisualAdvisor.Message("system", role));
            messages.Add(VisualAdvisor.Message("user", input));
            return (messages, false);
        }
        var summary = ContextSummary(_project.Summary, excluded, summaryOverride);
        messages.Add(VisualAdvisor.Message("system", role + "\nUser-approved project material and current prompt selection:\n" + input
            + (summary is { Length: > 0 } ? "\nEarlier conversation summary:\n" + summary : "")));
        var hasImages = false;
        var visible = _project.Messages.Where(message =>
            !_project.Hidden.Contains(message.Id) && excluded?.Contains(message.Id) != true).ToArray();
        var lastUser = visible.LastOrDefault(message => message.User);
        foreach (var message in visible)
        {
            var text = message.User ? message.Text : SerializeAdvisorHistory(message);
            var embedded = message == lastUser ? message.Images.Concat(job.Images.Select(image => image.ImageId)).Distinct().ToArray() : message.Images;
            if (!message.User || !VisualComponents.CanSeeImages(job.Advisor) || embedded.Length == 0)
            {
                messages.Add(VisualAdvisor.Message(message.User ? "user" : "assistant", text));
                continue;
            }
            var urls = new List<string>();
            foreach (var id in embedded)
                urls.Add(await VisualAdvisor.ImageUrlAsync(_projects.ImagePath(_project, id), token));
            messages.Add(VisualAdvisor.ImageMessage(text, urls));
            hasImages = true;
        }
        return (messages, hasImages);
    }

    // This JSON becomes text inside the model conversation, separately from the HTTP JSON envelope.
    internal static string SerializeAdvisorHistory(VisualMessage message) => JsonSerializer.Serialize(new
    {
        reply = message.Text,
        fragments = message.Fragments.Select(fragment => new { fragment.Text, fragment.Selected })
    }, AdvisorPromptJson);
}
