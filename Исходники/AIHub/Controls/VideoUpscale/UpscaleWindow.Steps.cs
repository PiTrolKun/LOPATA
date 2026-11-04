using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoUpscale;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    private UpscaleMode? SelectMode()
    {
        UpscaleMode? value = null;
        var window = new Window { Owner = this }; MusicWishUi.PrepareWindow(window, T("ChooseMode"), "VideoUpscale.Mode", 650); window.Height = 480;
        var panel = new StackPanel { Margin = new(20) };
        foreach (var mode in Enum.GetValues<UpscaleMode>()) {
            var selected = mode; panel.Children.Add(MusicWishUi.Button(T("Mode." + mode), "VideoUpscale.Mode." + mode, () => { value = selected; window.DialogResult = true; }));
            panel.Children.Add(MusicWishUi.Text(T("Mode." + mode + ".Help")));
        }
        window.Content = panel; window.ShowDialog(); return value;
    }
    private void SetMode(UpscaleMode mode) { _project.Mode = mode; _project.Steps = UpscaleCatalog.Preset(mode); RefreshSteps(); Save(); }
    private void ChangeMode()
    {
        if (_busy) return; var mode = SelectMode(); if (mode is null) return;
        // Switching to manual reveals the current chain without discarding calibration.
        if (mode == UpscaleMode.Manual) { _project.Mode = mode.Value; RefreshSteps(); Save(); } else SetMode(mode.Value);
    }
    private void RefreshSteps()
    {
        _software.Children.Clear(); _ai.Children.Clear(); _aiCommands.Children.Clear(); var manual = _project.Mode == UpscaleMode.Manual;
        Title = T("Title") + " · " + T("Mode." + _project.Mode);
        _chain.Text = T("Chain") + ": " + string.Join(" → ", _project.Steps.Select((s, i) => s.Enabled ? (i + 1) + ". " + T("Engine." + s.Engine) : null).OfType<string>());
        for (var i = 0; i < _project.Steps.Count; i++) {
            var index = i; var step = _project.Steps[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(7, 3, 7, 3) };
            var enabled = MusicWishUi.Check("", step.Enabled, value => { step.Enabled = value; Save(); }); enabled.ToolTip = T("EnableStep"); row.Children.Add(enabled);
            var caption = new TextBlock { Text = $"{i + 1}. {T("Engine." + step.Engine)}", MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(5, 0, 8, 0), ToolTip = T("Engine." + step.Engine + ".Help") };
            row.Children.Add(caption);
            row.Children.Add(new TextBlock { Text = $"{step.Width} × {step.Height}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
            var settings = Command("StepSettings", () => EditStep(index));
            System.Windows.Automation.AutomationProperties.SetAutomationId(settings, "VideoUpscale.StepSettings." + step.Id); row.Children.Add(settings);
            if (manual) {
                var remove = Command("Remove", () => RemoveStep(step)); remove.Content = "×"; remove.Width = 32; remove.Padding = new(4);
                System.Windows.Automation.AutomationProperties.SetAutomationId(remove, "VideoUpscale.RemoveStep." + step.Id); row.Children.Add(remove);
            }
            var border = new Border { Child = row, BorderThickness = new(1), CornerRadius = new(5), Margin = new(0, 0, 7, 0) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            (step.Engine == UpscaleEngine.Scale ? _software : _ai).Children.Add(border);
        }
        Add(_aiCommands, "Preview", () => Safe(() => Process(true)));
        if (manual) {
            Add(_aiCommands, "AddStep", AddStep); Add(_aiCommands, "SavePreset", SavePreset); Add(_aiCommands, "LoadPreset", LoadPreset);
        }
        Add(_aiCommands, "ExportSettings", ExportSettings);
    }
    private void AddStep()
    {
        if (_busy) return;
        var selected = UpscaleEngine.Scale; var panel = new StackPanel { Margin = new(16) };
        panel.Children.Add(MusicWishUi.Text(T("ChooseEngine"), true)); panel.Children.Add(EnginePicker(selected, value => selected = value));
        var window = SettingsWindow(T("AddStep"), panel); window.Height = 720;
        Add(panel, "AddSelectedStep", () => {
            var vertical = _project.Media.Height > _project.Media.Width;
            _project.Steps.Add(UpscaleCatalog.New(selected, vertical ? 2160 : 3840, vertical ? 3840 : 2160)); RefreshSteps(); Save(); window.Close();
        });
        window.ShowDialog();
    }
    private void EditStep(int index)
    {
        if (_busy) return;
        var step = _project.Steps[index]; var manual = _project.Mode == UpscaleMode.Manual;
        var root = new Grid { Margin = new(12) }; root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(300) }); body.ColumnDefinitions.Add(new()); root.Children.Add(body);
        var panel = new StackPanel { Margin = new(14, 0, 4, 0) };
        var detail = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(detail, 1); body.Children.Add(detail);
        var window = SettingsWindow(T("StepSettings"), root); window.Content = root; window.Width = 1100; window.Height = Math.Min(840, SystemParameters.WorkArea.Height); window.MinWidth = 800;
        var engines = new StackPanel(); engines.Children.Add(MusicWishUi.Text(T("ChooseEngine"), true));
        engines.Children.Add(EnginePicker(step.Engine, value => {
            if (step.Engine == value) return;
            step.Engine = value; step.Parameters.Clear(); Render(); detail.ScrollToTop(); Save();
        }));
        body.Children.Add(new ScrollViewer { Content = engines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void Render() {
            panel.Children.Clear(); panel.Children.Add(MusicWishUi.Text(T("Engine." + step.Engine), true));
            panel.Children.Add(MusicWishUi.Text(T("Engine." + step.Engine + ".Help")));
            ResolutionPicker(panel, step, manual);
            Level(panel, "Mix", step.Mix, 0, 1, .01, value => step.Mix = value, true); panel.Children.Add(MusicWishUi.Text(T("Mix.Help")));
            if (step.Engine == UpscaleEngine.Scale) Choice(panel, "Algorithm", UpscaleCatalog.Algorithms, step.Algorithm, value => step.Algorithm = value);
            var advanced = new StackPanel();
            foreach (var field in UpscaleCatalog.Parameters(step.Engine)) {
                var primary = field.Key is "sharpen" or "denoise" or "nr_intensity" or "nr_style" or "mode" or "precision" or "upscale_vsr_quality" or "upscale_dlss_mode" or "vfx_vsr_mode" or "vfx_vsr_strength" or "vfx_upscale_strength";
                Parameter(manual || primary ? panel : advanced, step, field, manual, Render);
            }
            if (advanced.Children.Count > 0) panel.Children.Add(new Expander { Header = T("AdvancedSettings"), Content = advanced, Margin = new(0, 8, 0, 0) });
        }
        Render();
        var actions = new WrapPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(actions, 1); root.Children.Add(actions);
        Add(actions, "Reset", () => { step.Parameters.Clear(); step.Mix = 1; step.Algorithm = "lanczos"; Render(); Save(); });
        if (manual) {
            Add(actions, "Up", () => { Move(index, -1); window.Close(); }); Add(actions, "Down", () => { Move(index, 1); window.Close(); });
            Add(actions, "Duplicate", () => { var copy = UpscaleStore.Clone(step); copy.Id = Guid.NewGuid().ToString("N"); _project.Steps.Insert(index + 1, copy); RefreshSteps(); Save(); window.Close(); });
            Add(actions, "Remove", () => { RemoveStep(step); window.Close(); });
        }
        Add(actions, "Done", window.Close); window.ShowDialog(); RefreshSteps(); Save();
    }
    private void ExportSettings()
    {
        if (_busy) return;
        var panel = new StackPanel { Margin = new(16) };
        Choice(panel, "Codec", ["libopenh264", "hevc_nvenc", "ffv1"], _project.Codec, v => _project.Codec = v);
        Level(panel, "BitrateMbps", _project.BitrateMbps, 1, 200, 1, v => _project.BitrateMbps = (int)v); panel.Children.Add(MusicWishUi.Text(T("BitrateMbps.Help")));
        if (_project.Mode == UpscaleMode.Manual) {
            Level(panel, "CpuThreads", _project.CpuThreads, 0, 256, 1, v => _project.CpuThreads = (int)v); panel.Children.Add(MusicWishUi.Text(T("CpuThreads.Help")));
            Level(panel, "ChunkSeconds", _project.ChunkSeconds, .5, 5, .1, v => _project.ChunkSeconds = v); panel.Children.Add(MusicWishUi.Text(T("ChunkSeconds.Help")));
        }
        panel.Children.Add(MusicWishUi.Text(T("ExportHelp"))); var window = SettingsWindow(T("ExportSettings"), panel);
        Add(panel, "Done", window.Close); window.ShowDialog(); Save();
    }
    private Window SettingsWindow(string title, UIElement content)
    {
        var window = new Window { Owner = this }; MusicWishUi.PrepareWindow(window, title, "VideoUpscale.Settings", 800);
        window.Resources.MergedDictionaries.Insert(0, Resources); window.Height = 560;
        if (content is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        window.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return window;
    }
    private void Move(int index, int offset) { if (_busy || index + offset < 0 || index + offset >= _project.Steps.Count) return; (_project.Steps[index], _project.Steps[index + offset]) = (_project.Steps[index + offset], _project.Steps[index]); RefreshSteps(); Save(); }
    private void Field(Panel parent, string key, string value, Action<string> changed, Func<string>? current = null)
    {
        parent.Children.Add(MusicWishUi.Text(T(key))); var box = new TextBox { Text = value, MinWidth = 90, Margin = new(5), ToolTip = T(key + ".Help") }; parent.Children.Add(box);
        box.LostKeyboardFocus += (_, _) => {
            if (_busy) return;
            try { changed(box.Text.Trim()); Save(); box.ToolTip = T(key + ".Help"); }
            catch (Exception e) when (e is FormatException or OverflowException) { box.Text = current?.Invoke() ?? value; _status.Text = T("InvalidParameter"); }
        };
    }
    private void SavePreset()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = T("PresetFilter"), DefaultExt = ".lopataUpscalePreset" };
        if (dialog.ShowDialog(this) == true) UpscaleStore.Save(dialog.FileName, _project.Steps);
    }
    private void LoadPreset()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("PresetFilter") };
        if (dialog.ShowDialog(this) != true) return;
        try { var copy = UpscaleStore.Clone(_project); copy.Steps = UpscaleStore.Load<List<UpscaleStep>>(dialog.FileName); UpscaleCatalog.Validate(copy, allowEmptyChain: true); _project.Steps = copy.Steps; RefreshSteps(); Save(); }
        catch (Exception error) { AIHub.Services.VideoEditor.VideoErrors.Report(error); _status.Text = T("InvalidProject"); }
    }
}
