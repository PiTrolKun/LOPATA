using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services;
using AIHub.Services.GameSounds;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl
{
    private readonly List<Action> _refreshSoundValues = [];
    private bool _syncSound, _advanced;
    private TextBlock? _soundCaption;
    private CancellationTokenSource? _savedPlayback;
    private ComboBox? _outputFormatPicker;

    private void RenderLeft()
    {
        _left.Children.Clear();
        _refreshSoundValues.Clear();
        var model = new StackPanel { Margin = new(0, 0, 6, 5) };
        model.Children.Add(Label("Model", true));
        model.Children.Add(LabelText((_cards.FirstOrDefault(c => c.ModelArtifactId == Project.Options.ModelId)?.DisplayName ?? "") +
            " · " + T("Placement" + Project.Options.Placement)));
        model.Children.Add(Action("ChangeModel", ChangeModelAsync));
        model.Children.Add(Number("Variety", "Percent", 0, 100, () => Project.Options.Variety, value =>
        { Project.Options = Project.Options with { Variety = value }; Changed(); }));
        model.Children.Add(Check("EditLayer", Project.Options.EditSelectedLayer, value =>
        { Project.Options = Project.Options with { EditSelectedLayer = value }; Changed(); }));
        model.Children.Add(Check("GenerationThinking", Project.Options.GenerationThinking, value =>
        { Project.Options = Project.Options with { GenerationThinking = value }; Changed(); }));
        _left.Children.Add(model);

        var sound = new StackPanel { Margin = new(0, 0, 6, 5) };
        sound.Children.Add(Label("Sound", true));
        var presets = Select(GameSoundRecipes.Presets.Select(p => T(p.Key)));
        AutomationProperties.SetName(presets, T("Presets"));
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex < 0) return;
            var preset = GameSoundRecipes.Presets[presets.SelectedIndex];
            var recipe = GameSoundRecipe.Create(T(preset.Key), preset.Sound);
            Project.Replace(recipe); Project.SelectedId = recipe.Id;
            presets.SelectedIndex = -1; Changed(); RefreshSoundValues(); RenderResults(); InvalidatePreview();
        };
        presets.ToolTip = T("Presets"); sound.Children.Add(presets);
        foreach (var parameter in GameSoundRecipes.Parameters.Take(8)) sound.Children.Add(SoundNumber(parameter));
        var advanced = new Expander { Header = T("Advanced"), IsExpanded = _advanced, Margin = new(0, 5, 0, 3) };
        advanced.Expanded += (_, _) => _advanced = true; advanced.Collapsed += (_, _) => _advanced = false;
        var advancedPanel = new StackPanel { Margin = new(4, 0, 0, 0) };
        var wave = Select(GameSoundRecipes.Waveforms.Select(w => T(w)));
        AutomationProperties.SetName(wave, T("Waveform"));
        _refreshSoundValues.Add(() => wave.SelectedIndex = Array.IndexOf(GameSoundRecipes.Waveforms, CurrentSound.Waveform));
        wave.SelectionChanged += (_, _) =>
        {
            if (!_syncSound && wave.SelectedIndex >= 0)
                EditSound(CurrentSound with { Waveform = GameSoundRecipes.Waveforms[wave.SelectedIndex] });
        };
        advancedPanel.Children.Add(Label("Waveform")); advancedPanel.Children.Add(wave);
        foreach (var parameter in GameSoundRecipes.Parameters.Skip(8)) advancedPanel.Children.Add(SoundNumber(parameter));
        advanced.Content = advancedPanel; sound.Children.Add(advanced);
        _left.Children.Add(sound);

        var file = new StackPanel { Margin = new(0, 0, 6, 5) };
        file.Children.Add(Label("Output", true)); file.Children.Add(Label("FileName"));
        var name = Input(Project.OutputName, "FileName"); file.Children.Add(name);
        name.TextChanged += (_, _) => { Project.OutputName = name.Text; Changed(); };
        var folder = LabelText(Project.OutputFolder);
        file.Children.Add(folder);
        file.Children.Add(Action("OutputFolder", () =>
        {
            var chooser = new Microsoft.Win32.OpenFolderDialog { Title = T("OutputFolder"), InitialDirectory = Project.OutputFolder };
            if (chooser.ShowDialog(_owner) != true) return;
            Project.OutputFolder = chooser.FolderName; folder.Text = chooser.FolderName; Changed();
        }));
        _outputFormatPicker = AudioFormatPicker();
        file.Children.Add(Label("AudioFormat")); file.Children.Add(_outputFormatPicker);
        file.Children.Add(Check("ExportRecipeWithAudio", Project.ExportRecipeWithAudio, value =>
        { Project.ExportRecipeWithAudio = value; Changed(); }));
        file.Children.Add(Action("OpenRecipe", OpenRecipeAsync));
        _left.Children.Add(file);
        RefreshSoundValues();
    }

    private GameSoundParameters CurrentSound => Project.Selected?.Sound ?? new();

    private ComboBox AudioFormatPicker()
    {
        var formats = GameSoundAudio.Formats;
        var picker = Select(formats.Select(format => T("Format" + format)).ToArray());
        picker.SelectedItem = T("Format" + Project.OutputFormat);
        picker.ToolTip = T("AudioFormat");
        AutomationProperties.SetName(picker, T("AudioFormat"));
        AutomationProperties.SetAutomationId(picker, "GameSounds.AudioFormat");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            var format = formats[picker.SelectedIndex];
            if (format == Project.OutputFormat) return;
            Project.OutputFormat = format;
            if (_outputFormatPicker is { } mainPicker && mainPicker != picker)
                mainPicker.SelectedIndex = picker.SelectedIndex;
            Changed();
        };
        return picker;
    }

    private TextBox Input(string value, string key)
    {
        var box = new TextBox { Text = value, Padding = new(5), Margin = new(0, 2, 0, 3) };
        AutomationProperties.SetName(box, T(key)); AutomationProperties.SetAutomationId(box, "GameSounds." + key + ".Input");
        ThemeInput(box); return box;
    }

    private UIElement SoundNumber(GameSoundParameter parameter) => Number(parameter.Key, parameter.Unit,
        parameter.Minimum, parameter.Maximum, () => parameter.Read(CurrentSound), value =>
            EditSound(GameSoundRecipes.FitEnvelope(parameter.Write(CurrentSound, value), parameter.Key)), sound: true);

    private UIElement Number(string key, string unit, int minimum, int maximum, Func<int> read, Action<int> change,
        bool sound = false, bool showLabel = true)
    {
        var row = new DockPanel { Margin = new(0, 1, 0, 1), MinHeight = 25 };
        if (showLabel)
        {
            var label = Label(key);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.MinWidth = 90;
            label.MaxWidth = 101;
            DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
        }
        var details = T(key + "Hint");
        if (details != "GameSounds." + key + "Hint")
        {
            row.ToolTip = details;
            AutomationProperties.SetHelpText(row, details);
        }
        var suffix = Label(unit); suffix.MinWidth = 17; suffix.MaxWidth = 35;
        suffix.VerticalAlignment = VerticalAlignment.Center; suffix.FontSize = 11; suffix.Margin = new(3, 0, 0, 0);
        DockPanel.SetDock(suffix, Dock.Right); row.Children.Add(suffix);
        var entry = Input(read().ToString(CultureInfo.CurrentCulture), key);
        entry.Width = 43; entry.FontSize = 12; entry.Padding = new(2);
        entry.Margin = new(3, 0, 0, 0);
        DockPanel.SetDock(entry, Dock.Right); row.Children.Add(entry);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = Math.Max(1, (maximum - minimum) / 10), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, T(key)); AutomationProperties.SetAutomationId(slider, "GameSounds." + key);
        if (row.ToolTip is string help) AutomationProperties.SetHelpText(slider, help);
        row.Children.Add(slider);
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            var value = read();
            slider.Value = value; entry.Text = value.ToString(CultureInfo.CurrentCulture);
            syncing = false;
        }
        slider.ValueChanged += (_, _) =>
        {
            if (syncing || _syncSound) return;
            change((int)slider.Value); Refresh();
        };
        void ApplyEntry()
        {
            if (int.TryParse(entry.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value != read())
                change(Math.Clamp(value, minimum, maximum));
            Refresh();
        }
        entry.LostKeyboardFocus += (_, _) => ApplyEntry();
        entry.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyEntry(); e.Handled = true; } };
        if (sound) _refreshSoundValues.Add(Refresh);
        Refresh(); return row;
    }

    private void RefreshSoundValues()
    {
        _syncSound = true;
        try { foreach (var update in _refreshSoundValues) update(); }
        finally { _syncSound = false; }
    }

    private void EditSound(GameSoundParameters sound)
    {
        var selected = Project.Selected;
        if (selected is null)
        {
            var recipe = GameSoundRecipe.Create(T("UntitledSound"), sound);
            Project.Replace(recipe); Project.SelectedId = recipe.Id; RenderResults();
        }
        else
        {
            Project.Replace(selected with { Sound = sound });
            if (_soundCaption is not null) _soundCaption.Text = F("DurationCaption", sound.DurationMs);
        }
        Changed(); RefreshSoundValues(); InvalidatePreview();
    }

    private void RenderResults()
    {
        _results.Children.Clear(); _soundCaption = null;
        foreach (var recipe in Project.Recipes.Where(recipe => recipe.Id != Project.SelectedId).Reverse())
        {
            var row = new DockPanel();
            if (Project.Orchestrate)
            {
                var check = TrackCheck(recipe);
                DockPanel.SetDock(check, Dock.Left); row.Children.Add(check);
            }
            var button = MusicWishUi.Button(recipe.Name, "GameSounds.Recipe." + recipe.Id, () =>
            {
                Project.SelectedId = recipe.Id; Changed(); RenderResults(); RefreshSoundValues(); InvalidatePreview();
            });
            StyleButton(button);
            row.Children.Add(button); _results.Children.Add(row);
        }
        var card = new StackPanel();
        card.Children.Add(Label("Current", true));
        if (Project.Selected is { } current)
        {
            var title = new DockPanel();
            _soundCaption = LabelText(F("DurationCaption", current.Sound.DurationMs));
            _soundCaption.Margin = new(6, 1, 0, 2);
            DockPanel.SetDock(_soundCaption, Dock.Right); title.Children.Add(_soundCaption);
            title.Children.Add(LabelText(current.Name, true)); card.Children.Add(title);
            if (Project.Orchestrate)
            {
                var track = TrackCheck(current); track.Content = Label("IncludeLayer"); card.Children.Add(track);
            }
            var actions = Row();
            actions.Children.Add(Action("SaveAudio", () => ExportAsync(false, true), primary: true));
            actions.Children.Add(Action("Listen", async () =>
            {
                StopSavedPlayback();
                _preview.Stop(); _status.Text = T("Rendering");
                await _player.PlayAsync(Project.Sources(false), false, ApplicationBackgroundOperations.ExitToken);
                _status.Text = T("Done");
            }));
            actions.Children.Add(Action("StopAudio", StopPlayback));
            actions.Children.Add(Action("SaveRecipe", () => ExportAsync(false, false)));
            if (_previous.ContainsKey(current.Id))
                actions.Children.Add(Action("Undo", () =>
                {
                    Project.Replace(_previous[current.Id]); _previous.Remove(current.Id);
                    Changed(); RenderResults(); RefreshSoundValues(); InvalidatePreview();
                }));
            card.Children.Add(actions);
        }
        else card.Children.Add(Label("ChooseSound"));
        _current.Child = card; UpdateLayerCount();
    }

    private System.Windows.Controls.CheckBox TrackCheck(GameSoundRecipe recipe)
    {
        var check = new System.Windows.Controls.CheckBox
        {
            IsChecked = Project.Tracks.Any(t => t.RecipeId == recipe.Id), Margin = new(3, 6, 6, 4),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = T("IncludeLayer")
        };
        check.Style = null;
        AutomationProperties.SetName(check, F("TrackFor", recipe.Name));
        check.Checked += (_, _) => { Project.Tracks.Add(new(recipe.Id)); Changed(); UpdateLayerCount(); };
        check.Unchecked += (_, _) => { Project.Tracks.RemoveAll(t => t.RecipeId == recipe.Id); Changed(); UpdateLayerCount(); };
        return check;
    }

    private void UpdateLayerCount()
    {
        _layerCount.Text = F("LayerCount", Project.Tracks.Count);
        _openMixer.IsEnabled = Project.Orchestrate && Project.Tracks.Count > 0;
    }

    private void RenderFiles()
    {
        _files.Children.Clear();
        foreach (var path in Project.Files.AsEnumerable().Reverse())
        {
            var row = new DockPanel();
            var play = Action("PlaySaved", () => PlaySavedAsync(path));
            play.Content = "▶";
            AutomationProperties.SetName(play, F("PlaySavedName", Path.GetFileName(path)));
            play.ToolTip = T("PlaySaved");
            var stop = Action("StopAudio", StopPlayback);
            stop.Content = "■"; stop.ToolTip = T("StopAudio");
            AutomationProperties.SetName(stop, T("StopAudio"));
            DockPanel.SetDock(play, Dock.Left); row.Children.Add(play);
            DockPanel.SetDock(stop, Dock.Left); row.Children.Add(stop);
            var button = MusicWishUi.Button(Path.GetFileName(path), "GameSounds.SavedFile", () =>
            {
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(T("FileMissing"), path);
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                }
                catch (Exception error) { Report(error); }
            });
            var label = (TextBlock)button.Content;
            label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            button.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            button.ToolTip = path; StyleButton(button);
            row.Children.Add(button); _files.Children.Add(row);
        }
    }

    private async Task PlaySavedAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(T("FileMissing"), path);
        StopPlayback(); _status.Text = T("Rendering");
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _savedPlayback = playback;
        var token = playback.Token;
        try
        {
            if (GameSoundAudio.FormatFromPath(path) is not null) await _savedPlayer.PlayFileAsync(path, token);
            else
            {
                var document = await Task.Run(() => GameSoundFiles.Load(path), token);
                token.ThrowIfCancellationRequested();
                await _savedPlayer.PlayAsync(document.Layers, false, token);
            }
            _status.Text = F("PlayingSaved", Path.GetFileName(path));
        }
        finally { if (ReferenceEquals(_savedPlayback, playback)) _savedPlayback = null; }
    }

    private void StopPlayback()
    {
        _preview.Stop(); _player.Invalidate(); StopSavedPlayback();
    }

    private void StopSavedPlayback() { _savedPlayback?.Cancel(); _savedPlayer.Invalidate(); }

    private async Task OpenRecipeAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("RecipeFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(_owner) != true) return;
        var document = await Task.Run(() => GameSoundFiles.Load(dialog.FileName));
        var restoreMix = document.IsMix || document.Layers.Length > 1 || document.Layers.Any(layer =>
            layer.StartMs != 0 || layer.Level != 100 || !layer.Enabled);
        if (restoreMix) { Project.Tracks.Clear(); Project.Orchestrate = true; }
        foreach (var layer in document.Layers)
        {
            var clone = layer.Recipe with { Id = Guid.NewGuid().ToString("N") };
            Project.Replace(clone); Project.SelectedId = clone.Id;
            if (restoreMix) Project.Tracks.Add(new(clone.Id, layer.StartMs, layer.Level, layer.Enabled));
        }
        Changed(); Checkpoint(force: true); Render();
    }

    private async Task ExportAsync(bool mix, bool audio, Window? parent = null)
    {
        if (_exporting) return;
        var sources = Project.Sources(mix);
        var name = string.IsNullOrWhiteSpace(Project.OutputName) ? mix ? T("MixName") : Project.Selected?.Name ?? T("UntitledSound") : Project.OutputName;
        var suggested = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        var format = Project.OutputFormat;
        var extension = audio ? MusicOutputSettings.Extension(format) : GameSoundFiles.RecipeExtension;
        var filter = audio ? $"{T("Format" + format)} (*{extension})|*{extension}" : T("RecipeFilter");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, DefaultExt = extension,
            FileName = (suggested.Length == 0 ? "sound" : suggested) + extension, InitialDirectory = Project.OutputFolder,
            AddExtension = true, OverwritePrompt = true };
        dialog.FileOk += (_, e) =>
        {
            if (dialog.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            System.Windows.MessageBox.Show(parent ?? _owner, F("ExpectedExtension", extension), T("Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        if (dialog.ShowDialog(parent ?? _owner) != true) return;
        var document = GameSoundFiles.Create(name, sources, mix);
        string? companion = null;
        if (audio && Project.ExportRecipeWithAudio)
        {
            companion = Path.ChangeExtension(dialog.FileName, GameSoundFiles.RecipeExtension);
            if (File.Exists(companion))
            {
                var result = System.Windows.MessageBox.Show(parent ?? _owner, F("ReplaceRecipe", Path.GetFileName(companion)),
                    T("SaveRecipe"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.No) companion = null;
            }
        }
        _exporting = true;
        try
        {
            var token = ApplicationBackgroundOperations.ExitToken;
            _status.Text = T("Rendering");
            if (audio) await Task.Run(() => GameSoundFiles.SaveAudioAsync(dialog.FileName, document, format, token), token);
            else await Task.Run(() => GameSoundFiles.SaveRecipe(dialog.FileName, document, token), token);
            CollectionExportJournal.Record(dialog.FileName, audio ? GameSoundFiles.AudioScenario(format) : document.Provenance.Scenario);
            if (!Project.Files.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase)) Project.Files.Add(dialog.FileName);
            Project.OutputFolder = Path.GetDirectoryName(dialog.FileName)!;
            Changed(); Checkpoint(force: true); RenderFiles(); _status.Text = T("FileSaved");
            if (companion is not null)
            {
                await Task.Run(() => GameSoundFiles.SaveRecipe(companion, document, token), token);
                CollectionExportJournal.Record(companion, document.Provenance.Scenario);
                if (!Project.Files.Contains(companion, StringComparer.OrdinalIgnoreCase)) Project.Files.Add(companion);
                Changed(); Checkpoint(force: true); RenderFiles();
            }
        }
        finally { _exporting = false; }
    }
}
