using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.NeuroConstructor;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public partial class NeuroAutoView : UserControl
{
    private sealed record Option(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private NeuroConstructorControl _workspace = null!;
    private NeuroProject? _project;
    private NeuroAutoResult? _savedResult;
    private bool _filling, _commandPending;
    private string? _building, _output;
    private (string Path, DateTime Written)? _lastReport;
    private string _score = "";

    public NeuroAutoView() => InitializeComponent();
    internal void Connect(NeuroConstructorControl workspace) => _workspace = workspace;
    private string T(string key, params object[] args) => _workspace.T(key,args);

    internal void ShowProject(NeuroProject project)
    {
        _savedResult = null;
        _project = project;
        ErrorText.Text = "";
        Relabel();
    }

    internal void ShowRun(NeuroAutoResult result)
    {
        _savedResult = result;
        _project = result.Project;
        ErrorText.Text = "";
        Relabel();
    }

    internal void Relabel()
    {
        if (_project is not { } p) return;
        _filling = true;
        try
        {
            var historical = _savedResult != null;
            var full = NeuroProfiles.Full(p.Composition);
            var profiles = new[]{new Option("light",T("LightProfile")),new Option("medium",T("MediumProfile")),new Option("heavy",T("HeavyProfile"))};
            Profile.ItemsSource = profiles;
            Profile.SelectedItem = profiles.First(o=>o.Id==NeuroProfiles.Id(p.Composition));
            AutoTitle.Text = T(historical ? "AutoResultTitle" : "AutoTitle");
            TaskDescription.Text = historical ? T("Option."+p.Composition) : T(full ? "MediumTask" : "AutoTask");
            Settings.Visibility = Build.Visibility = historical ? Visibility.Collapsed : Visibility.Visible;
            IncludeSpeech.IsEnabled = !full;
            IncludeSpeech.IsChecked = p.Composition != "pair";
            VisionLabel.Text=T(p.Composition switch { "medium"=>"MediumVision", "heavy"=>"HeavyVision", _=>"AutoVision" });
            SpeechLabel.Text=T(p.Composition switch { "medium"=>"MediumSpeech", "heavy"=>"HeavySpeech", _=>"AutoSpeech" });
            System.Windows.Automation.AutomationProperties.SetName(IncludeSpeech,SpeechLabel.Text);
            System.Windows.Automation.AutomationProperties.SetName(SpeechPlace,SpeechLabel.Text);
            System.Windows.Automation.AutomationProperties.SetName(VisionPlace,VisionLabel.Text);
            SpeechPlace.IsEnabled = IncludeSpeech.IsChecked == true;
            Places(LanguagePlace,p.LanguageDevice);
            Places(VisionPlace,p.VisionDevice);
            Places(SpeechPlace,p.SpeechDevice);
            Tuning.ItemsSource = new[] {new Option("ready",T("AutoReady")),new Option("tune",T("AutoTune"))};
            Tuning.SelectedIndex = p.AutoEvaluateOnly ? 0 : 1;
            Tuning.IsEnabled = !full || NeuroService.CanEvaluate(p);
            CycleEditor.Visibility = p.AutoEvaluateOnly ? Visibility.Collapsed : Visibility.Visible;
            CycleCount.Text = p.Cycles.ToString(CultureInfo.CurrentCulture);
            SourceInfo.Text = string.IsNullOrWhiteSpace(p.Source) ? T("AutoSource") : p.Source;
            _lastReport = null;
        }
        finally { _filling = false; }
        Refresh();
    }

    private void Places(ComboBox box,string selected)
    {
        var options = new List<Option> { new("auto",T("AutoPlace")),new("cpu",T("AutoRam")),new("gpu",T("AutoVram")) };
        if (selected.Length > 0 && options.All(o => o.Id != selected)) options.Add(new(selected,selected));
        box.ItemsSource = options;
        box.SelectedItem = options.FirstOrDefault(o => o.Id == selected) ?? options[0];
    }

    private void ReadSettings(NeuroProject p)
    {
        var ready = Tuning.SelectedIndex == 0;
        int cycles = p.Cycles;
        if (!ready && (!int.TryParse(CycleCount.Text, out cycles) || cycles < 1))
            throw new InvalidDataException(T("InvalidCycles"));
        var profile=((Option)Profile.SelectedItem).Id;
        p.Composition = NeuroProfiles.Full(profile) ? profile : IncludeSpeech.IsChecked == true ? "trio" : "pair";
        p.Connection = p.Composition != "pair" ? "late" : "inside";
        p.AutoEvaluateOnly = ready;
        p.Cycles = cycles;
        p.LanguageDevice = ((Option)LanguagePlace.SelectedItem).Id;
        p.VisionDevice = ((Option)VisionPlace.SelectedItem).Id;
        p.SpeechDevice = ((Option)SpeechPlace.SelectedItem).Id;
        NeuroService.Save(p);
    }

    private void SaveSelection()
    {
        if (_filling || _savedResult != null || _project is not { } p || !Settings.IsEnabled) return;
        try { ReadSettings(p); ErrorText.Text = ""; }
        catch (Exception error) { ErrorText.Text = _workspace.Explain(error); }
    }
    private void PlacementChanged(object sender, SelectionChangedEventArgs e) => SaveSelection();
    private void ProfileChanged(object sender,SelectionChangedEventArgs e)
    {
        if (_filling || _project is not { } p || _savedResult != null) return;
        try { ShowProject(NeuroService.SelectAutoProfile(p,((Option)Profile.SelectedItem).Id)); }
        catch (Exception error) { ErrorText.Text=_workspace.Explain(error); }
    }
    private void TuningChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        CycleEditor.Visibility = Tuning.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        SaveSelection();
    }
    private void CycleChanged(object sender,RoutedEventArgs e) => SaveSelection();
    private void SpeechChanged(object sender,RoutedEventArgs e)
    {
        if (_filling) return;
        SpeechPlace.IsEnabled = IncludeSpeech.IsChecked == true;
        SaveSelection();
    }
    private void PickSource(object sender,RoutedEventArgs e)
    {
        if (_project is not { } p) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("AutoSourceLabel") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        p.Source = dialog.FolderName;
        SourceInfo.Text = p.Source;
        SaveSelection();
    }

    internal void Refresh()
    {
        if (_project is not { } p) return;
        var state = _savedResult?.Status ?? NeuroService.Status(p);
        var paused = state.Status == "paused";
        var active = state.Status is "waiting" or "running" or "pausing" or "packing";
        var busy = active || _savedResult == null && _building == p.Id && !paused;
        Settings.IsEnabled = Build.IsEnabled = _savedResult == null && !busy && !paused && !_commandPending;
        if (!Tuning.IsEnabled && Settings.IsEnabled && state.Status=="completed")
            Tuning.IsEnabled = NeuroService.CanEvaluate(p);
        PauseWork.Visibility = active && state.Status != "pausing" ? Visibility.Visible : Visibility.Collapsed;
        ResumeWork.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        CancelWork.Visibility = paused || active ? Visibility.Visible : Visibility.Collapsed;
        PauseWork.IsEnabled = ResumeWork.IsEnabled = CancelWork.IsEnabled = !_commandPending;
        _output = _savedResult != null ? _savedResult.OutputPath : busy || paused ? null : NeuroService.AutoOutputPath(p);
        if (string.IsNullOrWhiteSpace(_output)) _output = null;
        var stage = _workspace.StageLabel(state.Stage);
        var status = state.Status switch {
            "ready" => "", "completed" => _output is null ? T("AutoChecked") : T("AutoDone"),
            "failed" => T("AutoFailed"), "packing" => T("Exporting"), "running" => stage.Length > 0 ? stage : T("AutoRunning"),
            _ => T("State." + state.Status)
        };
        StatusText.Text = status;
        if (state.Status == "failed" && !string.IsNullOrWhiteSpace(state.Message))
            StatusText.Text += Environment.NewLine + UserMessage(state.Message);
        Progress.Visibility = active || paused ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = active && state.Total <= 0;
        Progress.Maximum = Math.Max(1,state.Total);
        Progress.Value = Math.Clamp(state.Step,0,Math.Max(1,state.Total));
        Timing.Text = active || paused ? string.Join(" · ",new[] {
            state.Total > 0 ? T("Steps",state.Step,state.Total) : null,
            T("Elapsed",NeuroConstructorControl.Duration(state.ElapsedSeconds)),
            state.RemainingSeconds is { } left ? T("Remaining",NeuroConstructorControl.Duration(left)) : null
        }.Where(x => x != null)) : "";
        UpdateScore(_savedResult?.ReportPath ?? (busy || paused ? null : NeuroService.ReportPath(p)));
        var available = File.Exists(_output);
        Result.Text = string.Join(Environment.NewLine,new[]{ _score, _output is null ? "" : T(available ? "AutoSaved" : "AutoMoved",_output) }.Where(x => x.Length > 0));
        OpenResult.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateScore(string? path)
    {
        if (!File.Exists(path)) { _lastReport = null; _score = ""; return; }
        var stamp = (path,File.GetLastWriteTimeUtc(path));
        if (_lastReport == stamp) return;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var data = document.RootElement;
        var total = data.GetProperty("total").GetInt32();
        var details = new List<string> { T("Accuracy",data.GetProperty("baselineCorrect").GetInt32(),data.GetProperty("correct").GetInt32(),total) };
        if (total > 0 && data.TryGetProperty("baselineSeconds",out var before) && data.TryGetProperty("fusedSeconds",out var after))
            details.Add(T("Latency",1000 * before.GetDouble() / total,1000 * after.GetDouble() / total));
        var memory = new List<string>();
        if (data.TryGetProperty("peakRamBytes",out var ram)) memory.Add(T(data.TryGetProperty("visualExperts",out _) ? "MainRam" : "Ram",ram.GetDouble()/1048576));
        if (data.TryGetProperty("peakVramBytes",out var vram)) memory.Add(T("Vram",vram.GetDouble()/1048576));
        if (memory.Count > 0) details.Add(string.Join(" · ",memory));
        if (data.TryGetProperty("placement",out var placement))
        {
            var locations = new List<string>();
            foreach (var (role,label) in new[]{("language","AutoUsedLanguage"),("vision","AutoUsedVision"),("speech","AutoUsedSpeech"),("ocr","AutoUsedOcr")})
                if (placement.TryGetProperty(role,out var device) && device.GetString() is {Length:>0} name)
                    locations.Add(T(label,name == "cpu" ? "RAM" : $"VRAM ({name})"));
            if (locations.Count > 0) details.Add(string.Join(" · ",locations));
        }
        _score = string.Join(Environment.NewLine,details);
        _lastReport = stamp;
    }

    private string UserMessage(string message) => message == "Neuro.QualityBelowBaseline"
        ? T("AutoQualityBelow") : _workspace.Explain(message);
    private async void BuildClicked(object sender,RoutedEventArgs e)
    {
        if (_project is not { } p || !Build.IsEnabled) return;
        try
        {
            ReadSettings(p);
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = T("AutoSaveAs"), Filter = T("ModelFilter"),
                DefaultExt = ".lopatanet", FileName = "LOPATA-" + p.Composition + ".lopatanet", OverwritePrompt = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _building = p.Id;
            ErrorText.Text = "";
            Refresh();
            await NeuroService.BuildAutoAsync(p,dialog.FileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_savedResult == null && _project?.Id == p.Id) ErrorText.Text = UserMessage(error.Message); }
        finally { _building = null; SafeRefresh(); }
    }

    private async Task ChangeRun(Func<NeuroProject,Task> command, bool resume = false)
    {
        if (_project is not { } p || _commandPending) return;
        _commandPending = true;
        try
        {
            ErrorText.Text = "";
            var pending = command(p);
            // Resume may wait for the entire operation. Keep its pause and cancellation controls available.
            if (resume) _commandPending = false;
            Refresh();
            await pending;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_savedResult == null && _project?.Id == p.Id) ErrorText.Text = UserMessage(error.Message); }
        finally { _commandPending = false; SafeRefresh(); }
    }
    private void SafeRefresh()
    {
        try { Refresh(); }
        catch (Exception error) { ErrorText.Text = _workspace.Explain(error); }
    }
    private async void PauseClicked(object sender,RoutedEventArgs e) => await ChangeRun(NeuroService.PauseAsync);
    private async void ResumeClicked(object sender,RoutedEventArgs e) => await ChangeRun(NeuroService.ResumeAsync,resume:true);
    private async void CancelClicked(object sender,RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),T("AutoConfirmCancel"),T("Cancel"),
            MessageBoxButton.YesNo,MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ChangeRun(NeuroService.CancelAsync);
    }
    private void OpenOutput(object sender,RoutedEventArgs e)
    {
        if (_output == null) return;
        try { NeuroConstructorControl.OpenFolder(Path.GetDirectoryName(_output)!); }
        catch (Exception error) { ErrorText.Text = _workspace.Explain(error); }
    }
}
