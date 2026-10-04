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
        var method = ActionButton("ChooseMethod", ChooseMethodAsync);
        method.Content = L(ImageUtilityCatalog.GetMethod(Options.MethodId).NameKey) + "  ▾";
        method.IsEnabled = !Options.FormatOnly && !IsBusy && !HasPendingOperation(); panel.Children.Add(method);
        Check(panel, "FormatOnly", Options.FormatOnly, value => { Options.FormatOnly = value; SavePreferences(); RenderPrimarySettings(panel); RenderMethodSettings(); RefreshRows(); });
        Choice(panel, "Resolution", Options.Preset.ToString(CultureInfo.InvariantCulture),
            [new("360", "360"), new("720", "720"), new("1080", "1080"), new("2160", "4K"), new("4320", "8K")],
            value => { Options.Preset = int.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); RefreshRows(); }, !Options.FormatOnly);
        panel.Children.Add(ImageUtilityUi.Text(L("ResolutionHint")));
        var available = _formats;
        Choice(panel, "Format", Options.Format, available.Select(x => new ImageUtilityUi.Choice(x.Id, x.Id.ToUpperInvariant())).ToArray(),
            value => { Options.Format = value; SavePreferences(); RenderMethodSettings(); }, available.Count > 0);
        if (available.Count == 0) panel.Children.Add(ImageUtilityUi.Text(L("CheckingFormats")));
        var folder = ImageUtilityUi.Input(Options.ExportFolder, "ExportFolder");
        panel.Children.Add(ImageUtilityUi.Text(L("ExportFolder"))); panel.Children.Add(folder);
        folder.LostKeyboardFocus += (_, _) => { Options.ExportFolder = folder.Text.Trim(); SavePreferences(); };
        panel.Children.Add(ActionButton("ChooseOutputFolder", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("ChooseOutputFolder"), InitialDirectory = Directory.Exists(Options.ExportFolder) ? Options.ExportFolder : "" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) { Options.ExportFolder = picker.FolderName; folder.Text = picker.FolderName; SavePreferences(); }
        }));
        panel.Children.Add(ImageUtilityUi.Text(L("CustomName")));
        var name = ImageUtilityUi.Input(Options.CustomName, "CustomName"); panel.Children.Add(name);
        name.LostKeyboardFocus += (_, _) => { Options.CustomName = name.Text.Trim(); SavePreferences(); };
        panel.Children.Add(ImageUtilityUi.Text(L("CustomNameHint")));
        Check(panel, "Recursive", Options.IncludeSubfolders, value => { Options.IncludeSubfolders = value; SavePreferences(); });
        panel.IsEnabled = !IsBusy && !HasPendingOperation();
    }
    private void RenderMethodSettings()
    {
        if (_settingsPanel is null) return;
        _selectedAiReady = !ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi || _ai.IsReady(Options.MethodId);
        var panel = _settingsPanel; panel.Children.Clear();
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
                    panel.Children.Add(ImageUtilityUi.Text(L("IntegerScaleHint"))); break;
                case "hq2x":
                    Choice(panel, "Passes", Parameter("passes", "1"), [new("1", "1 × HQ2x"), new("2", "2 × HQ2x")], value => SetParameter("passes", value));
                    panel.Children.Add(ImageUtilityUi.Text(L("PassesHint"))); break;
            }
            Number(panel, "Sharpen", Options.Sharpen.ToString(CultureInfo.InvariantCulture), "SharpenHint", 0, 3,
                value => { Options.Sharpen = double.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); });
        }
        else panel.Children.Add(ImageUtilityUi.Text(L("FormatOnlyHint")));
        var format = (_formats.Count > 0 ? _formats : ImageUtilityFormats.All).FirstOrDefault(x => x.Id == Options.Format);
        if (Options.Format is "webp" or "jxl" or "avif")
        {
            Check(panel, "Lossless", Parameter("lossless", "false") == "true", value => { SetParameter("lossless", value ? "true" : "false"); RenderMethodSettings(); });
            panel.Children.Add(ImageUtilityUi.Text(L("LosslessHint")));
        }
        if (Options.Format == "png")
            Number(panel, "PngCompression", Parameter("pngCompression", "6"), "PngCompressionHint", 0, 9,
                value => SetParameter("pngCompression", value), true);
        if (Options.Format == "tiff")
        {
            Choice(panel, "TiffCompression", Parameter("tiffCompression", "lzw"),
                new[] { "none", "lzw", "zip", "jpeg", "zstd", "webp" }.Select(value => new ImageUtilityUi.Choice(value, L("Compression." + value))).ToArray(),
                value => SetParameter("tiffCompression", value));
            panel.Children.Add(ImageUtilityUi.Text(L("TiffCompressionHint")));
        }
        if (format?.HasQuality == true && !(Options.Format is "webp" or "jxl" or "avif" && Parameter("lossless", "false") == "true"))
            Number(panel, "Quality", Options.Quality.ToString(CultureInfo.InvariantCulture), "QualityHint", 1, 100,
                value => { Options.Quality = int.Parse(value, CultureInfo.InvariantCulture); SavePreferences(); }, true);
        if (format?.SupportsAlpha == true)
            Check(panel, "Transparency", Options.PreserveTransparency, value => { Options.PreserveTransparency = value; SavePreferences(); RenderMethodSettings(); });
        if (format?.SupportsAlpha != true || !Options.PreserveTransparency)
        {
            panel.Children.Add(ImageUtilityUi.Text(L("Background")));
            var background = ImageUtilityUi.Input(Options.BackgroundColor, "Background"); panel.Children.Add(background);
            panel.Children.Add(ImageUtilityUi.Text(L("BackgroundHint")));
            background.LostKeyboardFocus += (_, _) =>
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(background.Text, "^#[0-9a-fA-F]{6}$")) { Options.BackgroundColor = background.Text; SavePreferences(); }
                else { background.Text = Options.BackgroundColor; Error(new ImageUtilityException("ImageUtility.InvalidColor")); }
            };
        }
        panel.Children.Add(ActionButton("Recommended", () =>
        { Options.Parameters = ImageUtilityCatalog.RecommendedParameters(Options.MethodId); Options.Sharpen = 0; SavePreferences(); RenderMethodSettings(); RefreshRows(); }));
        panel.IsEnabled = !IsBusy && !HasPendingOperation();
    }
    private void Number(StackPanel panel, string label, string value, string description, double min, double max, Action<string> changed, bool integer = false)
    {
        panel.Children.Add(ImageUtilityUi.Text(L(label)));
        var input = ImageUtilityUi.Input(value, label); input.MaxWidth = 160; input.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        panel.Children.Add(input); panel.Children.Add(ImageUtilityUi.Text(L(description)));
        input.LostKeyboardFocus += (_, _) =>
        {
            if (double.TryParse(input.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) && number >= min && number <= max && (!integer || number == Math.Truncate(number)))
            { value = number.ToString(CultureInfo.InvariantCulture); input.Text = value; changed(value); }
            else { input.Text = value; Error(new ImageUtilityException("ImageUtility.InvalidNumber", $"{min}–{max}")); }
        };
    }
    private void Check(StackPanel panel, string key, bool selected, Action<bool> changed)
    {
        var check = new CheckBox { Content = L(key), IsChecked = selected, Margin = new(0, 6, 0, 8) };
        AutomationProperties.SetAutomationId(check, "ImageUtility." + key);
        check.Checked += (_, _) => changed(true); check.Unchecked += (_, _) => changed(false); panel.Children.Add(check);
    }
    private void Choice(StackPanel panel, string key, string selected, ImageUtilityUi.Choice[] choices, Action<string> changed, bool enabled = true)
    {
        panel.Children.Add(ImageUtilityUi.Text(L(key)));
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(x => x.Id == selected), MinHeight = 32, Margin = new(0, 0, 0, 5), IsEnabled = enabled };
        AutomationProperties.SetAutomationId(combo, "ImageUtility." + key);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ImageUtilityUi.Choice option) changed(option.Id); };
        panel.Children.Add(combo);
    }
    private string Parameter(string key, string fallback) => Options.Parameters.GetValueOrDefault(key, fallback);
    private void RenderAiSettings(StackPanel panel)
    {
        foreach (var setting in ImageUtilityAiCatalog.Settings(Options.MethodId))
        {
            var title = L(setting.NameKey); if (setting.Experimental) title += " · " + L("Advanced");
            if (setting.Choices is { } choices)
            {
                var options = choices.Select(value => new ImageUtilityUi.Choice(value,
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : L("Ai.Value." + value))).ToArray();
                Choice(panel, setting.NameKey, Parameter(setting.Key, setting.DefaultValue), options, value => SetParameter(setting.Key, value));
                if (setting.Experimental) panel.Children.Add(ImageUtilityUi.Text(L("Advanced")));
            }
            else
            {
                panel.Children.Add(ImageUtilityUi.Text(title));
                var input = ImageUtilityUi.Input(Parameter(setting.Key, setting.DefaultValue), "Ai." + setting.Key); panel.Children.Add(input);
                input.LostKeyboardFocus += (_, _) => SetParameter(setting.Key, input.Text.Trim());
            }
            panel.Children.Add(ImageUtilityUi.Text(L(setting.DescriptionKey)));
        }
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
