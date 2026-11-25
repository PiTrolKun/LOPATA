using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoConverter;
using AIHub.Services.VideoUpscale;
using Microsoft.Win32;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using ListBox = System.Windows.Controls.ListBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AIHub.Controls.VideoConverter;

internal sealed partial class VideoConverterWindow : Window
{
    private readonly Func<string, string> _localize;
    private readonly Func<Task> _ensureStartup;
    private readonly Dictionary<string, (ConversionMedia? Media, string? Error)> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _paths = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ConversionOptions _options = new();
    private ConversionJob? _job;
    private CancellationTokenSource? _run;
    private bool _sync, _reading, _closed, _recycling, _starting;
    private bool Busy => _run is not null || _recycling || _starting;
    private string T(string key) => _localize("VideoConverter." + key);

    internal VideoConverterWindow(Window owner, Func<string, string> text, Func<Task> ensureStartup)
    {
        _localize = text;
        _ensureStartup = ensureStartup;
        PublisherUi.Prepare(this, owner, T("Title"), 1240, 780);
        MinWidth = Math.Min(770, Width);
        MinHeight = Math.Min(540, Height);
        if (!owner.IsVisible) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        BuildLayout();
        try
        {
            var draft = ConversionStore.ReadDraft();
            draft.Options.Validate();
            _options = draft.Options;
            if (draft.JobId is { } id) _job = ConversionStore.ReadJob(id);
            _paths.AddRange(draft.Inputs.Where(path => Path.IsPathFullyQualified(path) && ConversionOptions.Accepts(path))
                .Concat(_job?.Items.Select(item => item.Input) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(ConversionStore.QueueLimit));
            if (_job is not null) _activity.Text = T("Restored");
        }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
        SyncOptions();
        Render();
        Loaded += (_, _) => _ = ReadInputsAsync();
        Closing += (_, e) =>
        {
            if (Busy && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        };
    }

    internal void AddPaths(IEnumerable<string> candidates)
    {
        var paths = candidates.Where(path => Path.IsPathFullyQualified(path) && ConversionOptions.Accepts(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !_paths.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (_paths.Count + paths.Length > ConversionStore.QueueLimit)
        {
            _activity.Text = string.Format(T("QueueLimit"), ConversionStore.QueueLimit);
            return;
        }
        if (paths.Length == 0) return;
        _paths.AddRange(paths);
        if (Busy) _activity.Text = T("QueuedNext");
        SaveDraft();
        Render();
        if (IsLoaded) _ = ReadInputsAsync();
    }

    private async Task ReadInputsAsync()
    {
        if (_reading || _closed || !_paths.Any(p => !_media.ContainsKey(p))) return;
        _reading = true;
        UpdateButtons();
        var token = _lifetime.Token;
        try
        {
            await _ensureStartup();
            await UpscaleFfmpeg.PrepareAsync(token);
            while (_paths.FirstOrDefault(path => !_media.ContainsKey(path)) is { } path)
            {
                token.ThrowIfCancellationRequested();
                try { _media[path] = (await ConversionMedia.ProbeAsync(path, token), null); }
                catch (Exception error) when (Recoverable(error)) { _media[path] = (null, ConversionEncoder.ShortError(error)); }
                Render();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
        finally { _reading = false; UpdateButtons(); }
    }

    private void AddFromPicker()
    {
        var extensions = string.Join(";", ConversionOptions.Extensions.Select(ext => "*" + ext));
        var dialog = new OpenFileDialog { Title = T("Add"), Filter = $"{T("Files")}|{extensions}", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = T("PickFolder") };
        if (Directory.Exists(_options.OutputDirectory)) dialog.InitialDirectory = _options.OutputDirectory;
        if (dialog.ShowDialog(this) != true) return;
        _options = _options with { OutputDirectory = dialog.FolderName };
        SaveDraft();
        Render();
    }

    private void ChangeResolution()
    {
        var dialog = new Window();
        PublisherUi.Prepare(dialog, this, T("Resolution"), 460, 350);
        dialog.Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(T("ResolutionTip")));
        var fields = new StackPanel();
        body.Content = fields;
        var size = new System.Windows.Controls.ComboBox();
        AddChoice(size, T("FrameOriginal"), 0);
        foreach (var edge in new[] { 360, 480, 540, 720, 1080, 1440, 2160, 4320 })
            AddChoice(size, edge == 2160 ? "2160 (4K)" : edge == 4320 ? "4320 (8K)" : edge.ToString(), edge);
        if (_options.ShortEdge > 0 && !size.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, _options.ShortEdge)))
            AddChoice(size, _options.ShortEdge.ToString(), _options.ShortEdge);
        Select(size, _options.ShortEdge);
        fields.Children.Add(Labeled(T("ShortEdge"), size, "VideoConverter.ShortEdge"));
        var apply = ActionButton(T("Apply"), "ApplyResolution", () => dialog.DialogResult = true);
        apply.IsDefault = true;
        footer.Children.Add(apply);
        if (dialog.ShowDialog() != true) return;
        _options = _options with { ShortEdge = Choice<int>(size) };
        Render();
        SaveDraft();
    }

    private void OnCodecChanged()
    {
        if (_sync) return;
        _sync = true;
        try
        {
            FillContainers(Choice<ConversionCodec>(_codec), Choice<ConversionContainer>(_container));
            if (Choice<ConversionCodec>(_codec) == ConversionCodec.Ffv1)
            {
                Select(_executor, ConversionExecutor.Cpu);
                if (Choice<ConversionMode>(_mode) is ConversionMode.Quality or ConversionMode.Bitrate)
                    Select(_mode, ConversionMode.Percent);
            }
        }
        finally { _sync = false; }
        OptionsChanged();
    }

    private void FillContainers(ConversionCodec codec, ConversionContainer selected)
    {
        _container.Items.Clear();
        foreach (var container in ConversionOptions.Containers(codec))
            AddChoice(_container, container.ToString().ToUpperInvariant(), container);
        Select(_container, selected);
    }

    private void SyncOptions()
    {
        _sync = true;
        try
        {
            Select(_codec, _options.Codec);
            FillContainers(_options.Codec, _options.Container);
            Select(_executor, _options.Executor);
            Select(_mode, _options.Mode);
            Select(_quality, _options.Quality);
            if (!_fps.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, _options.FramesPerSecond)))
                AddChoice(_fps, _options.FramesPerSecond.ToString(), _options.FramesPerSecond);
            Select(_fps, _options.FramesPerSecond);
            _reduction.Value = _options.Reduction;
            _limit.Text = _options.LimitMb.ToString(CultureInfo.CurrentCulture);
            _bitrate.Text = _options.BitrateKbps.ToString(CultureInfo.CurrentCulture);
            _picture.IsChecked = _options.PictureMayChange;
            _audio.IsChecked = _options.AudioMayChange;
            _motion.IsChecked = _options.MotionMayChange;
            _tracks.IsChecked = _options.OmitIncompatibleTracks;
            _archive.IsChecked = _options.Archive;
        }
        finally { _sync = false; }
    }

    private void OptionsChanged()
    {
        if (_sync) return;
        try
        {
            _options = CollectOptions();
            SaveDraft();
        }
        catch (InvalidDataException error) { _activity.Text = error.Message; }
        Render();
    }

    private ConversionOptions CollectOptions()
    {
        var mode = Choice<ConversionMode>(_mode);
        int ReadNumber(string value, int previous, ConversionMode applies) => mode != applies ? previous
            : int.TryParse(value, CultureInfo.CurrentCulture, out var chosen) ? chosen : throw new InvalidDataException(T("InvalidNumber"));
        var codec = Choice<ConversionCodec>(_codec);
        if (codec == ConversionCodec.Ffv1 && mode is ConversionMode.Quality or ConversionMode.Bitrate)
            throw new InvalidDataException(T("Ffv1Mode"));
        var next = _options with
        {
            Codec = codec, Container = Choice<ConversionContainer>(_container), Executor = Choice<ConversionExecutor>(_executor), Mode = mode,
            Reduction = (int)_reduction.Value, LimitMb = ReadNumber(_limit.Text, _options.LimitMb, ConversionMode.Size),
            BitrateKbps = ReadNumber(_bitrate.Text, _options.BitrateKbps, ConversionMode.Bitrate), Quality = Choice<int>(_quality),
            PictureMayChange = codec != ConversionCodec.Ffv1 && _picture.IsChecked == true, AudioMayChange = _audio.IsChecked == true,
            MotionMayChange = _motion.IsChecked == true, FramesPerSecond = _motion.IsChecked == true ? Choice<int>(_fps) : 0,
            OmitIncompatibleTracks = _tracks.IsChecked == true, Archive = _archive.IsChecked == true
        };
        next.Validate();
        return next;
    }

    private void OpenPrepared()
    {
        var picker = new OpenFileDialog { Title = T("OpenSettings"), Filter = "LOPATA Video Converter (*.lopata-converter.json)|*.lopata-converter.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            using var input = File.OpenRead(picker.FileName);
            if (input.Length > 2_000_000) throw new InvalidDataException(T("InvalidProject"));
            var data = JsonSerializer.Deserialize<ConversionDraft>(input, BackgroundOperationStore.Options) ?? throw new InvalidDataException(T("InvalidProject"));
            data.Options.Validate();
            _options = data.Options;
            SyncOptions();
            AddPaths(data.Inputs);
            SaveDraft();
            Render();
        }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
    }

    private void SaveDraft()
    {
        try { ConversionStore.SaveDraft(new(_options, _paths.ToArray(), _job?.Id)); }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
    }

    private string? Selected(ListBox list) => (list.SelectedItem as ListBoxItem)?.Tag as string;
    private ConversionItem? ResultSelected() => _job?.Items.FirstOrDefault(item =>
        item.Input.Equals(Selected(_results), StringComparison.OrdinalIgnoreCase));
    private void RemoveSelected()
    {
        if (Busy || Selected(_sources) is not { } path) return;
        try
        {
            if (_job?.Items.FirstOrDefault(item => item.Input.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                ClearCandidate(_job, item);
                ConversionStore.DiscardStagedOutput(_job, item);
                _job = _job with { Items = _job.Items.Where(current => current != item).ToArray() };
                ConversionStore.SaveJob(_job);
            }
            _paths.Remove(path);
            _media.Remove(path);
            SaveDraft();
            Render();
        }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
    }
    private void ResetQueue()
    {
        if (Busy) return;
        try
        {
            if (_job is { } job)
            {
                foreach (var item in job.Items)
                {
                    ClearCandidate(job, item);
                    ConversionStore.DiscardStagedOutput(job, item);
                }
                if (job.ArchiveCandidate is { } archive) ConversionStore.DiscardCandidate(job, job.Id, archive, "archive");
            }
            _job = null;
            _paths.Clear();
            _media.Clear();
            SaveDraft();
            Render();
            _activity.Text = "";
        }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
    }
    private static void ClearCandidate(ConversionJob job, ConversionItem item)
    {
        if (item.Candidate is not { } candidate) return;
        ConversionStore.DiscardCandidate(job, item.Id, candidate);
    }

    private async Task StartAsync(bool retry = false, string? saveLarger = null)
    {
        if (Busy) return;
        try
        {
            if (_job is { } saved && ApplicationBackgroundOperations.Current is { } controller
                && controller.FindOperations(ConversionBatch.Kind, saved.Id).FirstOrDefault() is { } pending)
            {
                await controller.ResumeAsync(pending.Id, _lifetime.Token);
                return;
            }
            _starting = true;
            UpdateButtons();
            _options = CollectOptions();
            var options = _options with
            {
                OutputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.OutputDirectory.Length > 0
                    ? _options.OutputDirectory : ConversionStore.DefaultOutput))
            };
            if (saveLarger is not null && _job is not null) options = _job.Options;
            var job = _job ?? new(ConversionStore.NewId(), options, []);
            var existing = job.Items.Select(i => i.Input).ToHashSet(StringComparer.OrdinalIgnoreCase);
            job = job with { Options = options, Items = [.. job.Items, .. _paths.Where(path => !existing.Contains(path))
                .Select(path => new ConversionItem(ConversionStore.NewId(), path))] };
            var selected = retry ? ResultSelected() : null;
            string[] ids = job.Items.Where(item => saveLarger is not null ? item.Id == saveLarger
                : retry ? CanRetry(item.Status) && (selected is null || item.Id == selected.Id)
                : item.Status is ConversionStatus.Pending or ConversionStatus.Running
                  || !options.Archive && ConversionStore.HasUnpackedOutput(job, item)).Select(item => item.Id).ToArray();
            if (ids.Length == 0 && (!options.Archive || !job.Items.Any(i => i.Status == ConversionStatus.Ready))) return;
            if (saveLarger is null && _job is not null && (retry || options != _job.Options))
                for (int i = 0; i < job.Items.Length; i++)
                {
                    var item = job.Items[i];
                    if (!ids.Contains(item.Id) || item.Candidate is not { } candidate
                        || await ConversionStore.VerifyFile(candidate.Path, candidate, _lifetime.Token)) continue;
                    ClearCandidate(job, item);
                    job.Items[i] = item with { Candidate = null, Status = ConversionStatus.Pending };
                }
            _job = job;
            ConversionStore.SaveJob(job);
            SaveDraft();
            _starting = false;
            await ExecuteAsync(job, new(job.Id, ids, saveLarger), _lifetime.Token, offerRecycle: _delete.IsChecked == true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
        finally { _starting = false; if (!_closed) UpdateButtons(); }
    }

    private async Task SaveLargerAsync()
    {
        if (ResultSelected() is not { Status: ConversionStatus.OverLimit, Candidate: { } candidate } item) return;
        if (PublisherUi.Confirm(this, _localize, string.Format(T("ConfirmLarger"), Path.GetFileName(item.Input),
            Bytes(candidate.Bytes), Bytes(candidate.Limit ?? 0)), T("Title")))
            await StartAsync(saveLarger: item.Id);
    }

    internal async Task RestoreAsync(ConversionJob job, ConversionCheckpoint checkpoint, BackgroundOperationState state, CancellationToken token)
    {
        if (Busy) throw new InvalidOperationException(T("AlreadyRunning"));
        _activity.Text = T("Restored");
        if (_job?.Id != job.Id)
        {
            _job = job;
            AddPaths(job.Items.Select(item => item.Input));
        }
        SaveDraft();
        await ExecuteAsync(job, checkpoint, token, state, offerRecycle: false);
    }

    private async Task ExecuteAsync(ConversionJob job, ConversionCheckpoint checkpoint, CancellationToken token,
        BackgroundOperationState? state = null, bool offerRecycle = false)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
        _run = run;
        _progress.ShowProgress(true, null);
        UpdateButtons();
        var progress = new Progress<ConversionProgress>(change =>
        {
            if (_closed || _run != run) return;
            if (_job is null || !change.Job.Items.SequenceEqual(_job.Items) || change.Job.Archive != _job.Archive)
            {
                _job = change.Job;
                Render();
            }
            _progress.ShowProgress(true, change.Fraction);
            _activity.Text = change.Position == 0 || change.Count == 0 ? change.Stage : $"{change.Position}/{change.Count} · {change.Stage}";
        });
        var completed = false;
        try
        {
            await _ensureStartup();
            _job = await ConversionBatch.RunAsync(job, checkpoint, progress, run.Token, state);
            var ready = _job.Items.Count(item => item.Status == ConversionStatus.Ready && !ConversionStore.HasUnpackedOutput(_job, item));
            _activity.Text = string.Format(T("Summary"), ready, _job.Items.Count(item => CanRetry(item.Status)));
            completed = true;
        }
        catch (OperationCanceledException)
        {
            _activity.Text = T(ApplicationBackgroundOperations.IsSuspending(run.Token) ? "Paused" : "Cancelled");
            if (state is not null) throw;
        }
        catch (Exception error) when (Recoverable(error))
        {
            ShowError(error);
            if (state is not null) throw;
        }
        finally
        {
            _run = null;
            if (!_closed)
            {
                if (!completed) try { _job = ConversionStore.ReadJob(job.Id) ?? _job; } catch (Exception error) when (Recoverable(error)) { ShowError(error); }
                _progress.ShowProgress(false, completed && _job is { ArchiveError: null } delivered
                    && delivered.Items.All(item => item.Status == ConversionStatus.Ready && !ConversionStore.HasUnpackedOutput(delivered, item))
                    ? 1 : null);
                Render();
                SaveDraft();
            }
        }
        if (completed && offerRecycle && IsVisible && !_closed && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested)
            await RecycleAsync(checkpoint.Items);
    }

    private async Task RecycleAsync(string[] allowed)
    {
        if (Busy || _job is not { } job || allowed.Length == 0) return;
        _recycling = true;
        UpdateButtons();
        var token = _lifetime.Token;
        try
        {
            List<(ConversionItem Item, int Index)> eligible = [];
            for (int i = 0; i < job.Items.Length; i++)
            {
                var item = job.Items[i];
                if (!allowed.Contains(item.Id) || item.Status != ConversionStatus.Ready
                    || ConversionStore.DeliveredPath(job, item) is not { } path
                    || job.Items.Count(other => other.Input.Equals(item.Input, StringComparison.OrdinalIgnoreCase)) != 1
                    || item.Output?.ArchiveName is null && job.Items.Count(other =>
                        string.Equals(path, other.Output?.Path, StringComparison.OrdinalIgnoreCase)) != 1
                    || job.Items.Any(other => other.Input.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                string? note = null;
                try
                {
                    if (await ConversionRecycle.CheckAsync(job, item, token)) { eligible.Add((item, i)); continue; }
                    note = T("RecycleMissing");
                }
                catch (Exception error) when (Recoverable(error)) { note = ConversionEncoder.ShortError(error); }
                job.Items[i] = item with { RecycleNote = note };
            }
            ConversionStore.SaveJob(job);
            if (eligible.Count == 0)
            {
                _activity.Text = T("NoRecycle");
                return;
            }
            var question = T("ConfirmRecycle") + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine,
                eligible.Select(entry => entry.Item.Input + Environment.NewLine + "→ " + ConversionStore.DeliveredPath(job, entry.Item)));
            if (!PublisherUi.Confirm(this, _localize, question, T("Title"))) return;
            foreach (var (item, index) in eligible)
            {
                string note;
                try { note = await ConversionRecycle.RecycleAsync(job, item, new WindowInteropHelper(this).Handle, token); }
                catch (Exception error) when (Recoverable(error)) { note = T("RecycleMissing") + " " + ConversionEncoder.ShortError(error); }
                job.Items[index] = item with { RecycleNote = note };
                ConversionStore.SaveJob(job);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
        finally { _recycling = false; Render(); }
    }

    private void Reveal(string? path)
    {
        if (path is null || !File.Exists(path)) { _activity.Text = T("MissingResult"); return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
        }
        catch (Exception error) when (Recoverable(error)) { ShowError(error); }
    }

    private void ShowError(Exception error)
    {
        _activity.Text = ConversionEncoder.ShortError(error);
        OwnedProcessRegistry.Log("video_conversion_failed", "VideoConverter", detail: error.GetType().Name);
    }

    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or ArgumentException
        or System.Runtime.InteropServices.COMException or InvalidOperationException;
}
