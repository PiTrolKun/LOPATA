using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualScenario;

internal sealed partial class VisualScenarioControl
{
    internal const string DrawingKind = "visual-scenario.drawing";

    private void RenderGallery()
    {
        _galleryHeading.Text = T("Gallery");
        _previewHeading.Text = T("LivePreview");
        AutomationProperties.SetName(_drawingFrame, _previewHeading.Text);
        var cards = _project.PromptCards();
        RenderPromptCards(cards);
        var numbers = cards.SelectMany(card => card.Results.Select(result => (result.Id, card.Number)))
            .ToDictionary(item => item.Id, item => item.Number);
        _gallery.Children.Clear();
        foreach (var result in _project.Results.AsEnumerable().Reverse())
        {
            var image = Thumbnail(result.Result, 184);
            image.ToolTip = T("OpenImage");
            image.MouseLeftButtonUp += (_, _) => OpenImage(result.Result);
            _gallery.Children.Add(image);
            var row = new WrapPanel { Margin = new(0, 0, 0, 10) };
            var caption = MusicWishUi.Text(string.Format(T("PromptNumber"), numbers[result.Id]));
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new(0, 0, 8, 0);
            row.Children.Add(caption);
            row.Children.Add(ButtonFor("EditImage", () => UseGalleryImage(result, true)));
            row.Children.Add(ButtonFor("UseReference", () => UseGalleryImage(result, false)));
            _gallery.Children.Add(row);
        }
    }

    private void RenderPromptCards(IReadOnlyList<VisualPromptCard> cards)
    {
        _promptCards.Children.Clear();
        var disabled = _busy || Pending is not null;
        foreach (var card in cards)
        {
            var active = card.Results.FirstOrDefault(item => item.Id == _project.ActivePrompt);
            var result = active ?? card.Results[0];
            var label = string.Format(T("PromptNumber"), card.Number);
            var key = $"Visual.PromptCard.{card.Number}";
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var button = ButtonFor("ShowPrompt", () => ShowSavedPrompt(result));
            button.Content = string.Format(T("PromptCardNumber"), card.Number);
            button.Margin = new(0);
            button.Padding = new(8, 3, 8, 3);
            button.MinHeight = 30;
            button.ToolTip = T("ShowPrompt");
            AutomationProperties.SetAutomationId(button, key + ".Open");
            AutomationProperties.SetName(button, $"{T("ShowPrompt")} · {label}");
            row.Children.Add(button);

            var selector = Toggle("ReusePrompt", active is not null, enabled =>
            {
                if (_busy || Pending is not null) return;
                SetActivePrompt(enabled ? result.Id : null);
                Save();
                RenderPromptCards(cards);
            });
            selector.Content = null;
            selector.Margin = new(2, 0, 0, 0);
            selector.MinHeight = 30;
            selector.IsEnabled = !disabled;
            selector.ToolTip = T("ReusePrompt");
            AutomationProperties.SetName(selector, $"{label} · {T("ReusePrompt")}");
            AutomationProperties.SetAutomationId(selector, key + ".Repeat");
            row.Children.Add(selector);

            var frame = new Border
            {
                Child = row, Padding = new(4), Margin = new(0, 0, 6, 6),
                CornerRadius = new(7), BorderThickness = new(1)
            };
            frame.SetResourceReference(BackgroundProperty, "PanelBrush");
            frame.SetResourceReference(BorderBrushProperty, active is null ? "LineBrush" : "AccentBrush");
            _promptCards.Children.Add(frame);
        }
    }

    private void SetActivePrompt(string? id)
    {
        _project.ActivePrompt = id;
        if (id is null) RunUi(_artistSession.RetireAsync);
    }

    private void UseGalleryImage(VisualResult result, bool edit)
    {
        if (_busy || Pending is not null) return;
        var image = _projects.Attach(_project, result.Result);
        if (edit) SetBase(image.Id);
        else SelectImage(image.Id, true);
    }

    private void ShowSavedPrompt(VisualResult result)
    {
        var window = PromptWindow("ShowPrompt", result.Prompt, false, out _);
        var root = (DockPanel)window.Content;
        var controls = new WrapPanel();
        controls.Children.Add(ButtonFor("RevisePrompt", () =>
        {
            if (_busy || Pending is not null) return;
            _project.PromptRevisionId = result.Id;
            _project.PromptDraft = null;
            _project.PreviewImages = null;
            _project.PromptAdvisor = null;
            SetActivePrompt(null);
            Save(); RenderGallery(); window.Close();
        }));
        controls.Children.Add(ButtonFor("Close", window.Close));
        AddBottom(root, controls);
        window.ShowDialog();
    }

    private async Task DrawAsync()
    {
        if (_busy || Pending is not null) return;
        if (_project.SelectedPrompt is { } saved) { await StartDrawingAsync(saved.Prompt); return; }
        if (SelectedFragments().Length == 0 && _project.ManualLines.Count == 0 && string.IsNullOrWhiteSpace(_project.DraftForRevision))
            throw new InvalidOperationException("Visual.MaterialRequired");
        var images = CurrentImages();
        CheckImages(images, _project.Artist);
        var advisor = await RequireAdvisorAsync(_project.Advisor);
        if (advisor is null) return;
        _project.Persistent = true;
        if (!Save()) return;
        await RunAdvisorAsync(new(_project.Id, Guid.NewGuid().ToString("N"), _project.Advisor, true, images), advisor);
    }

    private VisualInput[] CurrentImages()
    {
        var result = new List<VisualInput>();
        if (_project.BaseImage is { } source) result.Add(new(source, T("BaseImage"), true));
        foreach (var image in _project.Images.Where(image => image.Selected && image.Id != _project.BaseImage))
            result.Add(new(image.Id, T("Reference")));
        return result.ToArray();
    }

    private async Task ConfirmAndDrawAsync(VisualPrompt prompt)
    {
        var window = PromptWindow("ConfirmPrompt", prompt, true, out var text);
        var root = (DockPanel)window.Content;
        AddBottom(root, MusicWishUi.Footer(_localize,
            () => { if (!string.IsNullOrWhiteSpace(text.Text)) window.DialogResult = true; },
            () => window.DialogResult = false));
        if (window.ShowDialog() != true) return;
        await StartDrawingAsync(prompt with { Text = text.Text.Trim() });
    }

    private Window PromptWindow(string key, VisualPrompt prompt, bool editable, out TextBox text)
    {
        var window = new Window { Owner = OwnerWindow };
        MusicWishUi.PrepareWindow(window, T(key), "Visual." + key, 780);
        var root = new DockPanel { Margin = new(20) };
        var images = new WrapPanel();
        for (var i = 0; i < prompt.Images.Length; i++)
        {
            var input = prompt.Images[i];
            var item = new StackPanel { Margin = new(0, 0, 10, 8) };
            item.Children.Add(Thumbnail(_projects.ImagePath(_project, input.ImageId), 78));
            item.Children.Add(MusicWishUi.Text($"{i + 1} · {input.Role}"));
            images.Children.Add(item);
        }
        DockPanel.SetDock(images, Dock.Top); root.Children.Add(images);
        text = new TextBox { Text = prompt.Text, IsReadOnly = !editable, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(text);
        window.Content = root;
        return window;
    }

    private static void AddBottom(DockPanel root, UIElement element)
    {
        DockPanel.SetDock(element, Dock.Bottom); root.Children.Insert(0, element);
    }

    private IReadOnlyList<ManagedModelArtifactCard>? PrepareArtist(string id, bool withImages)
    {
        IReadOnlyList<ManagedModelArtifactCard>? cards = null;
        var window = new VideoPreparationWindow(OwnerWindow, _localize, ImageGenerationCatalog.DisplayName(id),
            async (download, progress, token) =>
                (cards = await _components.PrepareArtistAsync(id, withImages, download, progress, token)) is not null,
            ApplicationBackgroundOperations.ExitToken);
        return BackgroundUserDecision.ShowDialog(window, OwnerWindow, ApplicationBackgroundOperations.ExitToken) == true ? cards : null;
    }

    private void CheckImages(VisualInput[] images, string artist)
    {
        if (images.Length > VisualComponents.ImageLimit(artist))
            throw new InvalidOperationException(string.Format(T("ImageLimit"), VisualComponents.ImageLimit(artist)));
        if (images.Any(image => !File.Exists(_projects.ImagePath(_project, image.ImageId))))
            throw new InvalidDataException("Visual.MissingImage");
    }

    private async Task StartDrawingAsync(VisualPrompt prompt)
    {
        CheckImages(prompt.Images, _project.Artist);
        if (!Path.IsPathFullyQualified(_project.Folder)) throw new InvalidOperationException("Generation.FolderRequired");
        var model = ImageGenerationCatalog.Get(_project.Artist);
        var ratio = ImageGenerationDimensions.Ratios.FirstOrDefault(value => $"{value.Item1}:{value.Item2}" == _project.Aspect, (1, 1));
        var size = ImageGenerationDimensions.Fit(ratio.Item1, ratio.Item2, _project.Size, model.MaximumSide);
        var withImages = prompt.Images.Length > 0;
        var cards = await RequirePreparationAsync(token => _components.PrepareArtistAsync(model.Id, withImages, false, null, token),
            () => PrepareArtist(model.Id, withImages));
        if (cards is null) return;
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), model.Id, prompt.Text, size.Width, size.Height,
            [(long)RandomNumberGenerator.GetInt32(int.MaxValue)], _components.Root, _projects.DirectoryFor(_project.Id))
        {
            EngineSetId = EngineSetSelection.ActiveId, SubmittedAt = DateTimeOffset.Now, FirstGenerationNumber = _project.Results.Count + 1,
            OutputFolder = _project.Folder, OutputFormat = _project.OutputFormat,
            Metadata = new(ImageGenerationMetadata.Author(_metadataAuthor()), "image-generation/visual-scenario")
            {
                AdditionalModels = prompt.Advisor is { } advisor ? [advisor] : [],
                ArtistRevision = cards.FirstOrDefault(card => card.Files.Any(file => file.Purpose == "diffusion"))?.Revision
            },
            ReferenceImages = prompt.Images.Select(input => _projects.ImagePath(_project, input.ImageId)).ToArray()
        };
        _project.Persistent = true;
        _project.PromptRevisionId = null;
        _project.PromptDraft = null;
        _project.PreviewImages = null;
        _project.PromptAdvisor = null;
        if (!Save()) return;
        await RunDrawingAsync(new(_project.Id, prompt, request), cards);
    }

    private async Task RunDrawingAsync(VisualJob job, IReadOnlyList<ManagedModelArtifactCard> cards,
        BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        _busy = true;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _attempt = attempt;
        var frames = new Progress<BitmapSource>(frame =>
        {
            if (_attempt != attempt || attempt.IsCancellationRequested) return;
            _drawingFrame.Source = frame;
            _drawingPreview.Visibility = Visibility.Visible;
        });
        _status.Text = T("Drawing");
        Render();
        try
        {
            await ApplicationBackgroundOperations.RunAsync(DrawingKind, T("Title"), job.ProjectId, job, async ct =>
            {
                _projects.EnsureDrawingSession(job);
                var resident = _project.SelectedPrompt is null || job.Standalone ? null : _artistSession;
                await ApplicationBackgroundOperations.RetireModelsExceptAsync(resident);
                var turn = await new ImageGenerationRuntime(new ImageGenerationNativeWorker(frames, resident))
                    .RunAsync(job.Request, cards, null, ct);
                var result = turn.Results.Single();
                var path = ImageGenerationSessionStore.ResultPath(job.Request, result.Index);
                if (_project.Results.All(item => item.Id != job.Request.Id))
                    _project.Results.Add(new(job.Request.Id, job.Prompt, path, DateTimeOffset.Now));
                _projects.Save(_project);
                _status.Text = (result.ProcessingError ?? result.ExportError) is { } error ? _localize(error) : T("Ready");
                return true;
            }, attempt.Token, restored);
        }
        finally
        {
            _attempt = null;
            _drawingPreview.Visibility = Visibility.Collapsed;
            _drawingFrame.Source = null;
            _busy = false;
            Render();
        }
    }

    internal async Task RestoreAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Project is null) return;
        RestoreProject(state.Project);
        if (state.Kind == DrawingKind && state.Input.Deserialize<VisualJob>() is { } job)
        {
            if (ImageGenerationResident.RequiresStandalone(state.Detail))
            {
                job = job with { Standalone = true };
                state = state with { Input = JsonSerializer.SerializeToElement(job) };
            }
            CheckImages(job.Prompt.Images, job.Request.ModelId);
            var cards = await _components.PrepareArtistAsync(job.Request.ModelId, job.Prompt.Images.Length > 0, false, null, token);
            if (cards is null) throw new InvalidOperationException("Generation.DownloadRequired");
            await RunDrawingAsync(job, cards, state, token);
        }
        else if (state.Kind == VisualAdvisor.ChatKind && state.Input.Deserialize<VisualAdvisorJob>() is { } advice)
        {
            if (advice.Advisor == VisualComponents.Gemma && VisualAdvisor.RequiresCpuRecovery(state.Detail))
            {
                advice = advice with { ForceCpu = true };
                state = state with { Input = JsonSerializer.SerializeToElement(advice) };
            }
            if (advice.Warmup) { await RunWarmupAsync(advice, state, token); return; }
            var path = await _components.PrepareAdvisorAsync(advice.Advisor, false, null, token);
            if (path is null) throw new InvalidOperationException("Generation.DownloadRequired");
            await RunAdvisorAsync(advice, path, state, token);
        }
    }
}
