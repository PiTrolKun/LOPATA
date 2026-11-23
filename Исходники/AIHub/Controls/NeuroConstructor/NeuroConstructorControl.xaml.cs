using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services.NeuroConstructor;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public partial class NeuroConstructorControl : UserControl
{
    public sealed class Labels(Func<string, string> localize)
    {
        public string this[string key] => localize("Neuro." + key);
    }
    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private Labels _labels;
    private NeuroProject? _project;
    private string InferenceDevice => _project is { VisionDevice.Length: > 0 } ? "auto" : _project?.Device ?? "auto";
    private string? _lastPackage;
    private bool _filling, _working;
    private bool _canEvaluate;
    private readonly HashSet<string> _waitingForRun = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public NeuroConstructorControl(Func<string, string> localize)
    {
        _labels = new(localize);
        InitializeComponent();
        DataContext = _labels;
        AutoView.Connect(this);
        _timer.Tick += (_, _) => Try(RefreshWorkspace);
        Loaded += (_, _) => { Try(RefreshWorkspace); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    internal void Configure(Func<string, string> localize)
    {
        _labels = new(localize);
        DataContext = _labels;
        if (Pages.Visibility == Visibility.Visible && _project is not null) Try(() => DisplaySettings(_project));
        if (AutoView.Visibility == Visibility.Visible) Try(AutoView.Relabel);
    }

    internal void ShowModeChoice()
    {
        ModeChoice.Visibility = Visibility.Visible;
        Pages.Visibility = AutoView.Visibility = ProjectLabel.Visibility = ChangeMode.Visibility = Visibility.Collapsed;
        Notice.Text = "";
    }

    private void EnterMode(bool automatic)
    {
        ModeChoice.Visibility = Visibility.Collapsed;
        ChangeMode.Visibility = Visibility.Visible;
        AutoView.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        ProjectLabel.Visibility = Pages.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        Notice.Text = "";
    }

    private void ChangeModeClicked(object sender, RoutedEventArgs e) => ShowModeChoice();
    private void AutoModeClicked(object sender, RoutedEventArgs e) => Try(() =>
    {
        var project = NeuroService.OpenAuto();
        EnterMode(true);
        AutoView.ShowProject(project);
    });
    private void ProModeClicked(object sender, RoutedEventArgs e) => Try(() =>
    {
        var project = NeuroService.Open();
        EnterMode(false);
        if (_project?.Id != project.Id) LoadProject(project);
        else { DisplaySettings(_project); RefreshStatus(); }
    });
    private void RefreshWorkspace()
    {
        if (Pages.Visibility == Visibility.Visible) RefreshStatus();
        else if (AutoView.Visibility == Visibility.Visible) AutoView.Refresh();
    }

    internal bool ShowResult(string id, string? runId = null)
    {
        try
        {
            if (runId != null && NeuroService.OpenProject(id).Mode == "auto")
            {
                var result = NeuroService.SavedAutoResult(id,runId);
                EnterMode(true);
                AutoView.ShowRun(result);
                return true;
            }
            var project = NeuroService.ActivateProject(id);
            var automatic = project.Mode == "auto";
            EnterMode(automatic);
            if (automatic) AutoView.ShowProject(project);
            else { LoadProject(project); Pages.SelectedIndex = 2; }
            return true;
        }
        catch (Exception error) { Notice.Text = Explain(error); return false; }
    }

    internal string T(string key, params object[] args) => args.Length == 0 ? _labels[key]
        : string.Format(CultureInfo.CurrentCulture, _labels[key], args);
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e) { Notice.Text = Explain(e); }
    }
    private async Task Perform(Func<Task> action, string? message = null)
    {
        if (_working) return;
        _working = true;
        try
        {
            Notice.Text = message is null ? "" : T(message);
            RefreshStatus();
            await action();
        }
        catch (Exception e) { Notice.Text = Explain(e); }
        finally { _working = false; Try(RefreshStatus); }
    }

    private void LoadProject(NeuroProject p)
    {
        _project = p;
        _lastPackage = string.IsNullOrWhiteSpace(p.ParentPackage) ? null : p.ParentPackage;
        _reportStamp = null;
        PortableResult.Text = Notice.Text = "";
        PreparationResult.Text = "";
        Results.Visibility = Picker.Visibility = Visibility.Collapsed;
        ClearDraft();
        DisplaySettings(p);
        RefreshStatus();
    }

    private void DisplaySettings(NeuroProject p)
    {
        _filling = true;
        try
        {
            ProjectLabel.Text = T("CurrentProject", ProjectName(p));
            TaskName.Text = p.TaskName;
            Options(Dataset,p.UseReferenceExamples ? "reference" : "own","reference","own");
            DatasetHint.Text = T(p.UseReferenceExamples ? "ReferenceHint" : "OwnHint");
            var full = NeuroProfiles.Full(p.Composition);
            var trio = p.Composition != "pair";
            Options(Composition, p.Composition, full ? [p.Composition] : ["pair","trio"]);
            Options(Connection, p.Connection, full ? ["late"] : trio ? ["late", "early"] : ["inside", "late"]);
            Options(Channel, p.Channel, full ? ["continuous"] : trio ? ["continuous", "cached-continuous"] : ["continuous", "byte", "cached", "feedback"]);
            Options(AudioPolicy, p.AudioPolicy, full ? ["needed"] : ["needed","always","learned"]);
            Options(Device, p.Device is "auto" or "cpu" ? p.Device : "custom", "auto", "cpu", "custom");
            CustomDevice.Text = p.Device is "cpu" or "auto" ? "" : p.Device;
            CustomDevice.Visibility = Selected(Device) == "custom" ? Visibility.Visible : Visibility.Collapsed;
            var split = Selected(Split);
            Options(Split, split, "train", "dev", "test");
            HearingOptions.Visibility = AudioFiles.Visibility = trio ? Visibility.Visible : Visibility.Collapsed;
            TaskHint.Text = T(full ? "MediumTask" : trio ? "TrioHint" : "PairHint");
            DemoAnswer.Text = T(trio ? "DemoTrio" : "DemoPair");
            RefreshReadiness(p);
            Cycles.Text = p.Cycles.ToString(); Attempts.Text = p.Attempts.ToString(); Memory.Text = p.MemoryMiB.ToString();
            Attempts.IsEnabled = !full;
            SourcePath.Text = !string.IsNullOrWhiteSpace(p.Source) ? p.Source : T(
                string.IsNullOrWhiteSpace(p.ParentPackage) ? "AutoSource" : "FromPackage");
            RefreshExamples();
        }
        finally { _filling = false; }
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as Choice)?.Id ?? "";
    private void RefreshReadiness(NeuroProject p)
    {
        _canEvaluate = NeuroService.CanEvaluate(p);
        if (!_canEvaluate && p.Channel == "feedback")
        {
            SettingsHint.Text = T("PrepareFeedback");
            return;
        }
        var action = T(_canEvaluate ? "ReadyToEvaluate" : "NeedsTraining");
        var channelHint = _canEvaluate ? p.Channel switch
        {
            "cached" => T("CacheHint"), "cached-continuous" => T("RepeatDetail"),
            "feedback" => T("FeedbackHint"), _ => ""
        } : "";
        SettingsHint.Text = string.Join(" ", new[]{action,channelHint}.Where(value => value.Length > 0));
    }
    private string ProjectName(NeuroProject p)
    {
        var task = !string.IsNullOrWhiteSpace(p.TaskName) ? p.TaskName :
            string.IsNullOrWhiteSpace(p.ImportedFrom) ? T("Option." + p.Composition) : Path.GetFileNameWithoutExtension(p.ImportedFrom);
        return T("ProjectOption", task, p.Id[..Math.Min(8,p.Id.Length)]);
    }
    private void Options(ComboBox box, string current, params string[] ids)
    {
        box.ItemsSource = ids.Select(id => new Choice(id, T("Option." + id))).ToArray();
        box.SelectedIndex = Math.Max(0, Array.IndexOf(ids, current));
    }
    private static int Number(TextBox field, int minimum, string message) =>
        int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) && value >= minimum
            ? value : throw new InvalidDataException(message);

    private void SaveSettings()
    {
        if (_project is not { } p || _filling) return;
        var cycles = Number(Cycles, 1, T("InvalidCycles"));
        var attempts = Number(Attempts, 1, T("InvalidAttempts"));
        var memory = Number(Memory, 0, T("InvalidMemory"));
        var custom = Selected(Device) == "custom";
        if (custom && string.IsNullOrWhiteSpace(CustomDevice.Text)) throw new InvalidDataException(T("DeviceId"));
        p.Cycles = cycles; p.Attempts = attempts; p.MemoryMiB = memory;
        p.TaskName = TaskName.Text.Trim();
        p.UseReferenceExamples = Selected(Dataset) == "reference";
        p.Device = custom ? CustomDevice.Text.Trim() : Selected(Device);
        var comp = Selected(Composition);
        if (p.Composition != comp)
        {
            p.Connection = comp == "trio" ? "late" : "inside";
            p.Channel = "continuous";
        }
        else { p.Connection = Selected(Connection); p.Channel = Selected(Channel); }
        p.AudioPolicy = Selected(AudioPolicy);
        p.Composition = comp;
        NeuroService.Save(p);
        ProjectLabel.Text = T("CurrentProject", ProjectName(p));
        _reportStamp = null;
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _project is null) return;
        if (sender == Device && Selected(Device) == "custom")
        {
            CustomDevice.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(CustomDevice.Text)) { Notice.Text = T("DeviceId"); CustomDevice.Focus(); return; }
        }
        Try(() => { SaveSettings(); DisplaySettings(_project); RefreshStatus(); PreparationResult.Text = Notice.Text = ""; });
    }
    private void FieldChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling && _project is not null && EditSettings.IsEnabled) Try(SaveSettings);
    }
    private void GoExamples(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 1;
    private void GoTraining(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    private void NewClicked(object sender, RoutedEventArgs e) => Try(() => LoadProject(NeuroService.New()));

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var path = SelectFile("ModelFilter", "Derive");
        if (path is not null) await Perform(async () => LoadProject(await Task.Run(() => NeuroService.Import(path))), "Opening");
    }

    private string? SelectFile(string filter, string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T(filter), Title = T(title) };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
    private void ChooseSource(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (_project is not { } p) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("Source") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        p.Source = dialog.FolderName;
        NeuroService.Save(p);
        _reportStamp = null;
        SourcePath.Text = p.Source;
        PreparationResult.Text = "";
        RefreshReadiness(p);
    });
    private async void PrepareClicked(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        if (_project is not { } p) return;
        SaveSettings();
        PreparationResult.Text = "";
        var (device,freeBytes) = await NeuroService.PrepareAsync(p);
        if (!ReferenceEquals(p,_project)) return;
        var roles = T(p.Composition == "heavy" ? "HeavyVision" : p.Composition == "medium" ? "MediumVision" : p.Composition == "trio" ? "TrioRoles" : "PairRoles");
        PreparationResult.Text = T("Prepared", roles, device, DateTime.Now.ToShortTimeString()) + " " +
            (freeBytes < 0 ? T("MemoryUnavailable") : T("AvailableMemory", freeBytes / 1048576d));
        Notice.Text = "";
    }, "CheckingModels");

    private enum PickerKind { Project, Preset, Name }
    private PickerKind _pickerKind;
    private void ShowPicker(PickerKind kind, string label, IEnumerable<Choice> choices)
    {
        _pickerKind = kind;
        PickerApply.Content = T(kind == PickerKind.Name ? "SavePreset" : "Apply");
        Picker.Visibility = Visibility.Visible;
        PickerLabel.Text = T(label);
        PickerItems.ItemsSource = choices.ToArray();
        PickerItems.SelectedIndex = PickerItems.Items.Count > 0 ? 0 : -1;
        PickerItems.Visibility = kind == PickerKind.Name ? Visibility.Collapsed : Visibility.Visible;
        PresetName.Visibility = PresetHint.Visibility = IncludeExamples.Visibility = kind == PickerKind.Name ? Visibility.Visible : Visibility.Collapsed;
        if (kind == PickerKind.Name) { PresetName.Clear(); IncludeExamples.IsChecked = false; PresetName.Focus(); }
    }
    private void ChoosePreset(object sender, RoutedEventArgs e) => Try(() => ShowPicker(PickerKind.Preset, "UsePreset",
        new[] { new Choice("#pair", T("Option.pair")), new Choice("#trio", T("Option.trio")) }
            .Concat(NeuroService.Presets().Select(name => new Choice("user/" + name, name)))));
    private void ChooseProject(object sender, RoutedEventArgs e) => Try(() => ShowPicker(PickerKind.Project, "OpenProject",
        NeuroService.Projects().Select(p => new Choice(p.Id, ProjectName(p)))));
    private void SavePreset(object sender, RoutedEventArgs e) => Try(() =>
    {
        SaveSettings(); ShowPicker(PickerKind.Name, "PresetName", []); Pages.SelectedIndex = 0;
        Picker.BringIntoView(); PresetName.Focus();
    });
    private void ClosePicker(object sender, RoutedEventArgs e) => Picker.Visibility = Visibility.Collapsed;
    private void ApplyPicker(object sender, RoutedEventArgs e) => Try(() =>
    {
        var id = Selected(PickerItems);
        switch (_pickerKind)
        {
            case PickerKind.Name:
                if (_project is null || string.IsNullOrWhiteSpace(PresetName.Text)) throw new InvalidDataException(T("PresetName"));
                var name = PresetName.Text.Trim();
                if (NeuroService.Presets().Contains(name,StringComparer.Ordinal)
                    && System.Windows.MessageBox.Show(Window.GetWindow(this),T("ReplacePreset",name),T("SavePreset"),
                        MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                NeuroService.SavePreset(_project, name,IncludeExamples.IsChecked == true);
                Notice.Text = T("PresetSaved"); break;
            case PickerKind.Project when id.Length > 0: LoadProject(NeuroService.ActivateProject(id)); break;
            case PickerKind.Preset when id.Length > 0:
                LoadProject(id.StartsWith('#') ? NeuroService.New(id[1..]) : NeuroService.FromPreset(id["user/".Length..])); break;
            default: return;
        }
        Picker.Visibility = Visibility.Collapsed;
    });
}
