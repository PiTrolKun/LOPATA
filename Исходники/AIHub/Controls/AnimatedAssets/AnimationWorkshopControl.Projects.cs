using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;
using Image = System.Windows.Controls.Image;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private static readonly string LastDraftPointer = Path.Combine(RecoveryFolder, "latest.txt");
    private IReadOnlyList<BitmapSource> _previewFrames = [];
    private readonly Stopwatch _playback = new();
    private int _previewFrameRate;
    private bool _previewPlaying;
    private string? _previewCardId;
    private Image? _player;
    private BackgroundOperationPhase? _shownPhase;

    private async Task SaveProjectAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = F("ProjectFilter", AnimationProjectStore.Extension),
            DefaultExt = AnimationProjectStore.Extension,
            AddExtension = true,
            FileName = Path.GetFileName(_projectPath) ?? T("ProjectName")
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await AnimationProjectStore.SaveAsync(dialog.FileName, _project.Snapshot());
        _projectPath = dialog.FileName;
        await CheckpointAsync();
        _status = F("Saved", dialog.FileName);
    }

    private async Task StartNewProjectAsync()
    {
        await CheckpointAsync();
        _project = new();
        _recoveryPath = NewRecoveryPath();
        _projectPath = null;
        _documentStarted = false;
        _previewFrames = [];
        _previewCardId = null;
        _poseIndex = 0;
    }

    private async Task OpenProjectAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = F("ProjectFilter", AnimationProjectStore.Extension), CheckFileExists = true
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await OpenFileAsync(dialog.FileName, recovery: false, CancellationToken.None);
        await CheckpointAsync();
    }

    private async Task ResumeDraftAsync()
    {
        var path = await File.ReadAllTextAsync(LastDraftPointer);
        await OpenFileAsync(path.Trim(), recovery: true, CancellationToken.None);
    }

    internal async Task OpenRecoveryAsync(string path, CancellationToken token = default)
    {
        if (_recoveryPath == path && _documentStarted) return;
        await OpenFileAsync(path, recovery: true, token);
    }

    private async Task OpenFileAsync(string path, bool recovery, CancellationToken token)
    {
        if (recovery && !IsRecoveryPath(path)) throw new InvalidDataException("Animation.ProjectInvalid");
        _saveTimer.Stop();
        var project = await AnimationProjectStore.LoadAsync(path, token);
        if (project.Page is < StartPage or > PreviewPage) project.Page = StartPage;
        if (recovery) project.Page = DraftResumePage(project);
        if (project.Page > ReferencePage && !project.ReferenceApproved)
            project.Page = ReferencePage;
        if (project.Page >= SketchPage && project.Cards.Count == 0)
            project.Page = project.IsPack ? PackPage : ReferencePage;
        _project = project;
        _recoveryPath = recovery ? path : NewRecoveryPath();
        _projectPath = recovery ? null : path;
        _documentStarted = true;
        _previewFrames = [];
        _previewCardId = null;
        _poseIndex = 0;
        BuildPage();
    }

    internal static int DraftResumePage(AnimationProject project)
    {
        if (project.Page != StartPage) return project.Page;
        if (!project.ReferenceApproved || project.Reference is null)
            return project.Reference is not null || project.Cards.Count > 0 ? ReferencePage : StartPage;
        if (IsAutoEmojiPack(project))
            return project.Cards.Count > 0 && project.Cards.Count == project.AutoPackEmojis!.Count &&
                project.Cards.All(AnimationEmojiPackPlan.IsReady) ? PreviewPage : PackPage;
        if (project.Cards.Count == 0)
            return project.IsPack ? PackPage : ReferencePage;
        if (project.IsPack && project.Cards.Any(card => card.Still is null)) return SketchPage;

        var selected = project.Cards[Math.Clamp(project.SelectedCard, 0, project.Cards.Count - 1)];
        if (selected.Motion != "frames" || selected.Poses.Count == 0) return MotionPage;
        return selected.Poses.All(pose => pose.Image is not null) ? PreviewPage : PosesPage;
    }

    private async Task PreparePreviewAsync(AnimationCard card)
    {
        var snapshot = _project.Snapshot();
        var candidate = card.Snapshot();
        _previewFrames = await Task.Run(() => AnimationRenderer.Render(snapshot, candidate, ApplicationBackgroundOperations.ExitToken));
        _previewFrameRate = AnimationRenderer.GetFramesPerSecond(candidate);
        _previewCardId = card.Id;
        _project.Page = PreviewPage;
    }

    private void ShowPreview()
    {
        Label("Preview");
        var autoPack = IsAutoEmojiPack(_project);
        if (autoPack)
            _form.Children.Add(LiteraryUi.Text(F("AutoPackReady", _project.Cards.Count)));
        ChooseCard();
        var card = Card;
        AddButtons(Command("Play", () => PreparePreviewAsync(card)),
            PlainButton(_darkPreview ? "LightBackground" : "DarkBackground", () =>
            {
                _darkPreview = !_darkPreview;
                BuildPage();
            }),
            PlainButton(_smallPreview ? "LargePreview" : "ChatSize", () =>
            {
                _smallPreview = !_smallPreview;
                BuildPage();
            }));
        ShowTimeline(card, _form);
        _player = new Image { Stretch = System.Windows.Media.Stretch.Uniform, MaxWidth = _smallPreview ? 96 : 390 };
        if (_previewCardId == card.Id && _previewFrames.Count > 0)
        {
            _player.Source = _previewFrames[0];
            _playback.Reset();
            StartPreviewPlayback();
        }
        else if ((autoPack ? card.Poses.FirstOrDefault()?.Image ?? BaseImage(card) : BaseImage(card)) is { } baseImage)
        {
            try { _player.Source = AnimationRenderer.LoadStill(baseImage); }
            catch (InvalidDataException error) { _form.Children.Add(LiteraryUi.Text(_localize(error.Message))); }
        }
        var contrast = _darkPreview ? System.Windows.Media.Color.FromRgb(26, 30, 39) : System.Windows.Media.Color.FromRgb(250, 250, 251);
        _art.Children.Add(new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(contrast), MinHeight = _smallPreview ? 118 : 300,
            Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Child = _player
        });
        if (_project.IsPack && !autoPack) ShowCardGrid(_art);
        ShowExportOptions();
    }

    private void StartPreviewPlayback()
    {
        if (_previewPlaying || !IsLoaded || !IsVisible || _running || _project.Page != PreviewPage ||
            _player is null || _previewFrames.Count == 0 || _previewCardId != Card.Id)
            return;
        CompositionTarget.Rendering += PlayFrame;
        _previewPlaying = true;
        _playback.Start();
    }

    private void StopPreviewPlayback()
    {
        if (!_previewPlaying)
            return;
        CompositionTarget.Rendering -= PlayFrame;
        _previewPlaying = false;
        _playback.Stop();
    }

    private void PlayFrame(object? sender, EventArgs args)
    {
        if (_player is null || _previewFrames.Count == 0)
            return;
        var index = (long)(_playback.Elapsed.TotalSeconds * _previewFrameRate) % _previewFrames.Count;
        var frame = _previewFrames[(int)index];
        if (!ReferenceEquals(_player.Source, frame))
            _player.Source = frame;
    }

    private void ShowExportOptions()
    {
        Label("Export");
        var format = _project.ExportProfile is "telegram-sticker" or "telegram-emoji" ? _project.ExportProfile :
            _project.ExportFormat is "webp" or "webm" or "gif" or "png" ? _project.ExportFormat :
            _project.Purpose == "chat" ? "telegram-sticker" : "webp";
        void SetFormat(string value)
        {
            _project.ExportProfile = value.StartsWith("telegram-", StringComparison.Ordinal) ? value : "";
            _project.ExportFormat = _project.ExportProfile.Length == 0 ? value : "webm";
        }
        SetFormat(format);
        Choice("ExportType", format, value => { SetFormat(value); BuildPage(); }, ("telegram-sticker", "TelegramSticker"),
            ("telegram-emoji", "TelegramEmoji"), ("webp", "Webp"), ("webm", "Webm"), ("gif", "Gif"), ("png", "Png"));
        if (IsTelegramProfile && Card.Duration > AnimationRenderer.TelegramMaximumDuration)
            _form.Children.Add(LiteraryUi.Text(_localize("AnimatedAssets.Error.TelegramDuration")));
        if (_project.ExportFormat == "png" && Card.Poses.Any(pose => pose.Image is not null))
        {
            var stills = Card.Poses.Where(pose => pose.Image is not null).ToArray();
            _form.Children.Add(LiteraryUi.Text(T("PngFrame")));
            var picker = new System.Windows.Controls.ComboBox
            {
                ItemsSource = new[] { T("StartFrame") }.Concat(stills.Select(pose => pose.Text)).ToList(),
                SelectedIndex = Array.FindIndex(stills, pose => pose.Id == _project.PngPoseId) + 1
            };
            picker.SelectionChanged += (_, _) =>
            {
                _project.PngPoseId = picker.SelectedIndex > 0 ? stills[picker.SelectedIndex - 1].Id : null;
                ScheduleSave();
            };
            _form.Children.Add(picker);
        }
        var autoPack = IsAutoEmojiPack(_project);
        AddButtons(Command("ExportOne", ExportCardAsync, !autoPack));
        if (_project.IsPack) AddButtons(Command("ExportAll", ExportPackAsync, autoPack));
    }

    private bool IsTelegramProfile => _project.ExportProfile is "telegram-sticker" or "telegram-emoji";

    private async Task ExportCardAsync()
    {
        if (IsTelegramProfile && Card.Duration > AnimationRenderer.TelegramMaximumDuration)
            throw new InvalidDataException("AnimatedAssets.Error.TelegramDuration");
        var extension = "." + _project.ExportFormat;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = F("ExportFilter", extension), DefaultExt = extension, AddExtension = true,
            FileName = FileName(Card.Name)
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var snapshot = _project.Snapshot();
        var card = Card.Snapshot();
        await ExportAsync(dialog.FileName, snapshot, card);
        _status = F("Saved", dialog.FileName);
    }

    private async Task ExportPackAsync()
    {
        if (IsTelegramProfile && _project.Cards.Any(card => card.Duration > AnimationRenderer.TelegramMaximumDuration))
            throw new InvalidDataException("AnimatedAssets.Error.TelegramDuration");
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("SelectFolder") };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var snapshot = _project.Snapshot();
        var destination = Path.Combine(dialog.FolderName, T("PackFolder") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(destination);
        for (var index = 0; index < snapshot.Cards.Count; index++)
        {
            var card = snapshot.Cards[index];
            if (snapshot.ExportFormat == "png" && IsAutoEmojiPack(snapshot))
                snapshot.PngPoseId = card.Poses.FirstOrDefault()?.Id;
            var path = Path.Combine(destination, $"{index + 1:D2}-{FileName(card.Name)}.{snapshot.ExportFormat}");
            try { await ExportAsync(path, snapshot, card); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(F("PackExportError", index + 1, _localize(ex.Message), destination), ex);
            }
        }
        _status = F("Saved", destination);
    }

    private Task ExportAsync(string path, AnimationProject project, AnimationCard card) =>
        Task.Run(() => AnimationRenderer.ExportAsync(path, project, card, _models.FfmpegPath,
            ApplicationBackgroundOperations.ExitToken));

    private static string FileName(string value) =>
        string.Concat(value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.')
        is { Length: > 0 } valid ? valid[..Math.Min(80, valid.Length)] : "animation";

    private async Task DrawAsync(string target, string prompt, byte[]? source, bool prepare = true)
    {
        if (prepare && !_models.Prepare(OwnerWindow, artist: true, withImages: source is not null)) return;
        await CheckpointAsync();
        await _models.DrawAsync(_recoveryPath, prompt, source, _project.Transparent, target, ApplyModelResultAsync,
            ApplicationBackgroundOperations.ExitToken);
    }

    private async Task PlanAsync(AnimationCard card)
    {
        var image = BaseImage(card) ?? throw new InvalidOperationException("Animation.NeedImage");
        if (string.IsNullOrWhiteSpace(card.Story)) throw new InvalidOperationException("Animation.NeedDescription");
        if (!_models.Prepare(OwnerWindow, artist: false)) return;
        await CheckpointAsync();
        await _models.PlanAsync(_recoveryPath, card.Story, image, card.Duration, card.Keep, $"card/{card.Id}/plan",
            ApplyModelResultAsync, ApplicationBackgroundOperations.ExitToken);
    }

    private Task DrawPoseAsync(AnimationCard card, AnimationPose pose, bool prepare = true)
    {
        var hint = ReactionPoseInstruction(_project, card, pose, T, forArtist: true);
        if (string.IsNullOrWhiteSpace(hint)) throw new InvalidOperationException("Animation.NeedDescription");
        var source = BaseImage(card) ?? throw new InvalidOperationException("Animation.NeedImage");
        var prompt = card.Kind == "scene" ? F("PosePrompt", hint, card.Keep, card.Ambient, pose.Time, card.Duration) : hint;
        return DrawAsync($"card/{card.Id}/pose/{pose.Id}", prompt, source, prepare);
    }

    private async Task DrawMissingAsync(AnimationCard card)
    {
        if (card.Poses.All(pose => pose.Image is not null)) return;
        if (!_models.Prepare(OwnerWindow, artist: true)) return;
        foreach (var pose in card.Poses.Where(pose => pose.Image is null).ToList())
        {
            await DrawPoseAsync(card, pose, prepare: false);
            if (pose.Image is null || Pending is not null) break;
        }
    }

    internal async Task RestoreOperationAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Project is null) throw new InvalidDataException("Animation.ProjectInvalid");
        await OpenRecoveryAsync(state.Project, token);
        var previousBusy = _running;
        _running = true;
        BuildPage();
        try { await _models.RestoreAsync(state, ApplyModelResultAsync, token); }
        finally { _running = previousBusy; BuildPage(); }
    }

    internal void RefreshBackgroundState()
    {
        var phase = Pending?.Phase;
        if (phase == _shownPhase) return;
        _shownPhase = phase;
        if (IsLoaded) BuildPage();
    }

    private Task ApplyModelResultAsync(AnimationModelResult result) =>
        Dispatcher.InvokeAsync(() => ApplyModelResultOnUiAsync(result)).Task.Unwrap();

    private async Task ApplyModelResultOnUiAsync(AnimationModelResult result)
    {
        var autoPack = IsAutoEmojiPack(_project);
        if (result.Target == "pack")
        {
            if (!autoPack || _project.Cards.Count == 0 ||
                _project.Cards.Count != _project.AutoPackEmojis!.Count ||
                !_project.Cards.All(AnimationEmojiPackPlan.IsReady))
                throw new InvalidDataException("Animation.ProjectInvalid");
            _previewFrames = [];
            _previewCardId = null;
            _project.Page = PreviewPage;
        }
        else if (result.Target == "reference" && result.Drawing is { } reference)
        {
            SetReference(reference.Image, reference.Model);
            if (_project.ReferenceIsEmoji && string.IsNullOrWhiteSpace(_project.ReferenceEmoji))
            {
                _project.ReferenceEmoji = "🙂";
                _project.ReferenceEmojiName = T("Smile");
            }
        }
        else
        {
            var target = result.Target.Split('/');
            var card = target.Length >= 3 && target[0] == "card"
                ? _project.Cards.FirstOrDefault(item => item.Id == target[1]) : null;
            if (card is null) throw new InvalidDataException("Animation.ProjectInvalid");
            if (target[2] == "plan" && result.Plan is { } plan)
            {
                card.Poses = plan.Poses;
                card.Keep = string.IsNullOrWhiteSpace(card.Keep) ? plan.Keep : card.Keep;
                card.Ambient = string.IsNullOrWhiteSpace(card.Ambient) ? plan.Ambient : card.Ambient;
                if (!card.Models.Contains(plan.Model)) card.Models.Add(plan.Model);
                _project.Page = PosesPage;
                _poseIndex = 0;
            }
            else if (target[2] == "still" && result.Drawing is { } still)
            {
                card.Still = still.Image;
                card.Models = [.. card.Models.Where(model => model.Role != still.Model.Role), still.Model];
                card.Reviewed = false;
            }
            else if (target.Length == 4 && target[2] == "pose" && result.Drawing is { } drawn &&
                card.Poses.FirstOrDefault(pose => pose.Id == target[3]) is { } pose)
            {
                pose.Image = drawn.Image;
                pose.Models = [drawn.Model];
                if (autoPack) card.Reviewed = true;
            }
            else throw new InvalidDataException("Animation.ProjectInvalid");
        }
        await CheckpointAsync();
        if (autoPack && _project.Page == PackPage && result.Drawing is not null)
            UpdatePackProgress();
        else if (IsLoaded)
            BuildPage();
    }
}
