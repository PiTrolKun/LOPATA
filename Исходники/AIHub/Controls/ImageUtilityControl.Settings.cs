using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private void RenderPrimarySettings(StackPanel panel)
    {
        panel.Children.Clear(); panel.Children.Add(ImageUtilityUi.Text(L("Title"), true));
        ResetSettingsFlow(panel);
        var method = ActionButton("ChooseMethod", ChooseMethodAsync);
        method.Content = L(ImageUtilityCatalog.GetMethod(Options.MethodId).NameKey) + "  ▾";
        var longest = ImageUtilityCatalog.Methods.Max(x => new System.Windows.Media.FormattedText(L(x.NameKey) + "  ▾", CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, new System.Windows.Media.Typeface(method.FontFamily, method.FontStyle, method.FontWeight, method.FontStretch),
            method.FontSize, System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip).Width);
        method.Width = longest + 32; method.Margin = new(0, 0, 0, 4); method.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        method.IsEnabled = !Options.FormatOnly && !IsBusy && !HasPendingOperation(); var methodGroup = SettingGroup(panel); methodGroup.MinWidth = method.Width; methodGroup.Children.Add(method);
        Check(panel, "FormatOnly", Options.FormatOnly, value => { Options.FormatOnly = value; SavePreferences(); RenderPrimarySettings(panel); RenderMethodSettings(); RefreshRows(); });
        EndSettingsFlow(panel);
        Choice(panel, "Resolution", Options.Preset.ToString(CultureInfo.InvariantCulture),
            [new("360", "360"), new("720", "720"), new("1080", "1080"), new("2160", "4K"), new("4320", "8K")],
            value => { Options.Preset = int.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); RefreshRows(); }, !Options.FormatOnly);
        SettingHint(panel, "ResolutionHint");
        var available = _formats;
        Choice(panel, "Format", Options.Format, available.Select(x => new ImageUtilityUi.Choice(x.Id, x.Id.ToUpperInvariant())).ToArray(),
            value => { Options.Format = value; SavePreferences(); RenderMethodSettings(); }, available.Count > 0);
        if (available.Count == 0) SettingHint(panel, "CheckingFormats");
        EndSettingsFlow(panel);
        var folder = ImageUtilityUi.Input(Options.ExportFolder, "ExportFolder");
        panel.Children.Add(ImageUtilityUi.Text(L("ExportFolder"))); var folderRow = new DockPanel();
        folder.LostKeyboardFocus += (_, _) => { Options.ExportFolder = folder.Text.Trim(); SavePreferences(); };
        var pickFolder = ActionButton("ChooseOutputFolder", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("ChooseOutputFolder"), InitialDirectory = Directory.Exists(Options.ExportFolder) ? Options.ExportFolder : "" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) { Options.ExportFolder = picker.FolderName; folder.Text = picker.FolderName; SavePreferences(); }
        });
        pickFolder.ToolTip = L("ChooseOutputFolder"); AutomationProperties.SetName(pickFolder, L("ChooseOutputFolder"));
        pickFolder.Content = "📁"; pickFolder.Width = 42; DockPanel.SetDock(pickFolder, Dock.Right); folderRow.Children.Add(pickFolder); folderRow.Children.Add(folder); panel.Children.Add(folderRow);
        if (Options.NamingMode == "legacy" && _job.Items.Count == 0 && _job.OutputFolder is null)
            Options.NamingMode = string.IsNullOrWhiteSpace(Options.CustomName) ? "original" : "series";
        var namingModes = new List<string> { "original", "method", "series", "date" };
        if (Options.NamingMode == "legacy") namingModes.Add("legacy");
        Choice(panel, "Naming", Options.NamingMode, namingModes.Select(x => new ImageUtilityUi.Choice(x, L("Naming." + x))).ToArray(),
            value => { Options.NamingMode = value; SavePreferences(); RenderPrimarySettings(panel); });
        if (Options.NamingMode == "series" || Options.NamingMode == "legacy" && !string.IsNullOrWhiteSpace(Options.CustomName))
        {
            var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L("SeriesName")));
            var name = ImageUtilityUi.Input(Options.CustomName, "CustomName"); group.Children.Add(name);
            name.LostKeyboardFocus += (_, _) => { Options.CustomName = name.Text.Trim(); SavePreferences(); RenderPrimarySettings(panel); };
        }
        EndSettingsFlow(panel);
        var example = ImageUtilityNaming.CreateStem(new() { DisplayName = "image.png" }, Options);
        if (Options.NamingMode is "series" or "date") example += "_0001";
        panel.Children.Add(ImageUtilityUi.Text(L("NameExample") + " " + ImageUtilityProcessor.SafeFileName(example) + "." + ImageUtilityFormats.Get(Options.Format).Extension));
        Check(panel, "Recursive", Options.IncludeSubfolders, value => { Options.IncludeSubfolders = value; SavePreferences(); });
        panel.IsEnabled = !IsBusy && !HasPendingOperation();
    }
    private void RenderMethodSettings()
    {
        if (_settingsPanel is null) return;
        _selectedAiReady = !ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi || _ai.IsReady(Options.MethodId);
        var panel = _settingsPanel; panel.Children.Clear();
        ResetSettingsFlow(panel);
        panel.Children.Add(ImageUtilityUi.Text(L("Settings"), true));
        if (!Options.FormatOnly)
        {
            panel.Children.Add(ImageUtilityUi.Text(L(ImageUtilityCatalog.GetMethod(Options.MethodId).DescriptionKey)));
            if (ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi)
            {
                var warning = ImageUtilityUi.Text(L("AiWarning")); warning.Foreground = System.Windows.Media.Brushes.DarkOrange; panel.Children.Add(warning);
                RenderAiSettings(panel);
            }
            else switch (Options.MethodId)
            {
                case "mitchell": case "catmull-rom":
                    Number(panel, "Softness", Parameter("b", "0"), "SoftnessHint", -2, 2, value => SetParameter("b", value));
                    Number(panel, "Contour", Parameter("c", "0.5"), "ContourHint", -2, 2, value => SetParameter("c", value)); break;
                case "lanczos3":
                    Number(panel, "DetailRange", Parameter("lobes", "3"), "DetailRangeHint", 1, 8, value => SetParameter("lobes", value), true); break;
                case "nearest":
                    Choice(panel, "IntegerScale", Parameter("integerScale", "0"),
                        new[] { new ImageUtilityUi.Choice("0", L("MatchResolution")) }.Concat(Enumerable.Range(2, 15).Select(x => new ImageUtilityUi.Choice(x.ToString(CultureInfo.InvariantCulture), "×" + x))).ToArray(),
                        value => { SetParameter("integerScale", value); RefreshRows(); });
                    SettingHint(panel, "IntegerScaleHint"); break;
                case "hq2x":
                    Choice(panel, "Passes", Parameter("passes", "1"), [new("1", "1 × HQ2x"), new("2", "2 × HQ2x")], value => SetParameter("passes", value));
                    SettingHint(panel, "PassesHint"); break;
            }
            Number(panel, "Sharpen", Options.Sharpen.ToString(CultureInfo.InvariantCulture), "SharpenHint", 0, 3,
                value => { Options.Sharpen = double.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); });
        }
        else panel.Children.Add(ImageUtilityUi.Text(L("FormatOnlyHint")));
        var format = (_formats.Count > 0 ? _formats : ImageUtilityFormats.All).FirstOrDefault(x => x.Id == Options.Format);
        if (Options.Format is "webp" or "jxl" or "avif")
        {
            Check(panel, "Lossless", Parameter("lossless", "false") == "true", value => { SetParameter("lossless", value ? "true" : "false"); RenderMethodSettings(); });
            SettingHint(panel, "LosslessHint");
        }
        if (Options.Format == "png")
            Number(panel, "PngCompression", Parameter("pngCompression", "6"), "PngCompressionHint", 0, 9,
                value => SetParameter("pngCompression", value), true);
        if (Options.Format == "tiff")
        {
            Choice(panel, "TiffCompression", Parameter("tiffCompression", "lzw"),
                new[] { "none", "lzw", "zip", "jpeg", "zstd", "webp" }.Select(value => new ImageUtilityUi.Choice(value, L("Compression." + value))).ToArray(),
                value => SetParameter("tiffCompression", value));
            SettingHint(panel, "TiffCompressionHint");
        }
        if (format?.HasQuality == true && !(Options.Format is "webp" or "jxl" or "avif" && Parameter("lossless", "false") == "true"))
            Number(panel, "Quality", Options.Quality.ToString(CultureInfo.InvariantCulture), "QualityHint", 1, 100,
                value => { Options.Quality = int.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); }, true);
        if (format?.SupportsAlpha == true)
            Check(panel, "Transparency", Options.PreserveTransparency, value => { Options.PreserveTransparency = value; SavePreferences(); RenderMethodSettings(); });
        if (format?.SupportsAlpha != true || !Options.PreserveTransparency)
        {
            var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L("Background")));
            var color = ActionButton("Background", () => { using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(Options.BackgroundColor) };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Options.BackgroundColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"; SavePreferences(); RenderMethodSettings(); } });
            color.Content = "■  " + Options.BackgroundColor; group.Children.Add(color); SettingHint(panel, "BackgroundHint");
        }
        EndSettingsFlow(panel);
        panel.Children.Add(ActionButton("Recommended", () =>
        { Options.Parameters = ImageUtilityCatalog.RecommendedParameters(Options.MethodId); Options.Sharpen = 0; SavePreferences(); RenderMethodSettings(); RefreshRows(); }));
        panel.IsEnabled = !IsBusy && !HasPendingOperation();
    }
    private void Number(StackPanel panel, string label, string value, string description, double min, double max, Action<string> changed, bool integer = false)
    {
        var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L(label)));
        var row = new DockPanel(); var display = ImageUtilityUi.Text(""); display.MinWidth = 52; DockPanel.SetDock(display, Dock.Right); row.Children.Add(display);
        var slider = new Slider { Minimum = min, Maximum = max, Value = double.Parse(value, CultureInfo.InvariantCulture),
            TickFrequency = label == "Ai.SwinirTile" ? 8 : integer ? 1 : 0.01, IsSnapToTickEnabled = true, MinWidth = 120, Margin = new(0, 6, 8, 6) };
        AutomationProperties.SetAutomationId(slider, "ImageUtility." + label); AutomationProperties.SetName(slider, L(label));
        display.Text = slider.Value.ToString(integer ? "0" : "0.##", CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, _) => { display.Text = slider.Value.ToString(integer ? "0" : "0.##", CultureInfo.CurrentCulture); changed(slider.Value.ToString(CultureInfo.InvariantCulture)); };
        row.Children.Add(slider); group.Children.Add(row); SettingHint(panel, description);
    }
    private void Check(StackPanel panel, string key, bool selected, Action<bool> changed)
    {
        var check = new CheckBox { Content = L(key), IsChecked = selected, Margin = new(0, 6, 0, 8) };
        AutomationProperties.SetAutomationId(check, "ImageUtility." + key);
        check.Checked += (_, _) => changed(true); check.Unchecked += (_, _) => changed(false); SettingGroup(panel).Children.Add(check);
    }
    private void Choice(StackPanel panel, string key, string selected, ImageUtilityUi.Choice[] choices, Action<string> changed, bool enabled = true)
    {
        var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L(key)));
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(x => x.Id == selected), MinHeight = 32, Margin = new(0, 0, 0, 5), IsEnabled = enabled };
        AutomationProperties.SetAutomationId(combo, "ImageUtility." + key);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ImageUtilityUi.Choice option) changed(option.Id); };
        combo.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; combo.MinWidth = 96; combo.MaxWidth = 250;
        group.Children.Add(combo);
    }
    private string Parameter(string key, string fallback) => Options.Parameters.GetValueOrDefault(key, fallback);
    private void RenderAiSettings(StackPanel panel)
    {
        RenderCompactAiSettings(panel);
    }
    private void SetParameter(string key, string value) { Options.Parameters[key] = value; SavePreferences(); }
    private async Task ChooseMethodAsync()
    {
        var dialog = new ImageUtilityMethodWindow(_l, _ai, Options.MethodId, _preferences.FavoriteMethodId) { Owner = Window.GetWindow(this) };
        _methodWindow = dialog;
        dialog.DownloadFinished += () => { if (!dialog.IsLoaded && ReferenceEquals(_methodWindow, dialog)) _methodWindow = null; };
        try { dialog.ShowDialog(); } finally { if (!dialog.IsDownloading) _methodWindow = null; }
        if (dialog.FavoriteMethodId is { } favorite) _preferences.FavoriteMethodId = favorite;
        if (dialog.SelectedMethodId is { } chosen && chosen != Options.MethodId)
        {
            var exportSettings = Options.Parameters.Where(x => x.Key is "lossless" or "pngCompression" or "tiffCompression").ToArray();
            Options.MethodId = chosen; Options.Parameters = ImageUtilityCatalog.RecommendedParameters(chosen);
            foreach (var entry in exportSettings) Options.Parameters[entry.Key] = entry.Value;
        }
        SavePreferences(); Render(); await Task.CompletedTask;
    }
}
