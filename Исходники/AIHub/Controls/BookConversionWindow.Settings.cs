using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Services.Books;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Slider = System.Windows.Controls.Slider;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private void BuildSettings()
    {
        _settings.Children.Add(MusicWishUi.Text(L("OutputFormat"), true));
        AutomationProperties.SetAutomationId(_format, "BookConverter.Format");
        _format.SelectionChanged += (_, _) => { if (!_changingFormats) RefreshOptions(); };
        _settings.Children.Add(_format);
        _settings.Children.Add(_merge);
        _settings.Children.Add(_mergeFields);
        _settings.Children.Add(MusicWishUi.Text(L("SourceSafety")));
        _settings.Children.Add(MusicWishUi.Text(L("OutputDirectory")));
        var outputRow = new DockPanel();
        var choose = Action("Browse", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L("OutputDirectory") };
            if (dialog.ShowDialog(this) == true) _directory.Text = dialog.FolderName;
            return Task.CompletedTask;
        });
        DockPanel.SetDock(choose, Dock.Right); outputRow.Children.Add(choose);
        AutomationProperties.SetAutomationId(_directory, "BookConverter.OutputDirectory");
        outputRow.Children.Add(_directory); _settings.Children.Add(outputRow);
        _settings.Children.Add(MusicWishUi.Text(L("OutputDirectoryHint")));
        var tabs = new TabControl { Margin = new(0, 8, 0, 10) };
        tabs.Items.Add(new TabItem { Header = L("Basic"), Content = _basicOptions });
        tabs.Items.Add(new TabItem { Header = L("Professional"), Content = _expertOptions });
        _settings.Children.Add(tabs);
        _mergeFields.Children.Add(MusicWishUi.Text(L("MergeMetadata"), true));
        _mergeFields.Children.Add(MusicWishUi.Text(L("MergeMetadataHint")));
        AddTextField(_mergeFields, "TitleField", _title);
        _title.Text = L("DefaultCollectionTitle");
        AddTextField(_mergeFields, "AuthorField", _author);
        AddTextField(_mergeFields, "LanguageField", _language);
        var cover = new Image { Height = 95, Width = 95, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 5, 0, 5) };
        var coverName = MusicWishUi.Text(L("NoCover"));
        var coverButtons = new WrapPanel();
        coverButtons.Children.Add(Action("ChooseCover", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = L("ChooseCover"), Filter = L("CoverFilter") };
            if (dialog.ShowDialog(this) == true)
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 190; image.UriSource = new Uri(dialog.FileName); image.EndInit(); image.Freeze();
                _coverPath = dialog.FileName; cover.Source = image; coverName.Text = Path.GetFileName(dialog.FileName);
            }
            return Task.CompletedTask;
        }));
        coverButtons.Children.Add(Action("ClearCover", () => { _coverPath = null; cover.Source = null; coverName.Text = L("NoCover"); return Task.CompletedTask; }));
        _coverFields.Children.Add(coverName); _coverFields.Children.Add(cover); _coverFields.Children.Add(coverButtons);
        _coverFields.Children.Add(MusicWishUi.Text(L("MergeCoverHint")));
        _mergeFields.Children.Add(_coverFields);
        _mergeFields.Visibility = Visibility.Collapsed;
    }

    private void AddTextField(Panel parent, string key, TextBox text)
    {
        parent.Children.Add(MusicWishUi.Text(L(key))); text.Margin = new(0, 0, 0, 6);
        AutomationProperties.SetAutomationId(text, "BookConverter." + key);
        AutomationProperties.SetName(text, L(key)); parent.Children.Add(text);
    }

    private void RefreshFormats()
    {
        var previous = _format.SelectedItem as string ?? "EPUB";
        var formats = BookConversionCapabilities.GetOutputFormats(_entries.Where(row => row.IsChecked).Select(row => row.Path), _smartMode)
            .Select(value => value.ToUpperInvariant()).ToArray();
        _changingFormats = true;
        _format.ItemsSource = formats;
        _format.SelectedItem = formats.Contains(previous) ? previous : formats.FirstOrDefault();
        _changingFormats = false;
        RefreshOptions(); RefreshActions(); RefreshSourceSelection();
    }

    private void RefreshOptions()
    {
        var format = (_format.SelectedItem as string ?? "epub").ToLowerInvariant();
        _coverFields.Visibility = format is "epub" or "fb2" or "docx" or "pdf" ? Visibility.Visible : Visibility.Collapsed;
        _basicOptions.Children.Clear(); _expertOptions.Children.Clear();
        if (format == "epub")
        {
            var toc = MusicWishUi.Check(L("TableOfContents"), _options.GenerateTableOfContents,
                value => _options = _options with { GenerateTableOfContents = value });
            AutomationProperties.SetAutomationId(toc, "BookConverter.TableOfContents"); _basicOptions.Children.Add(toc);
        }
        _basicOptions.Children.Add(MusicWishUi.Text(L("PreserveHint")));
        if (format is "txt" or "md") _basicOptions.Children.Add(MusicWishUi.Text(L("PlainTextHint")));
        if (_entries.Any(row => row.IsChecked && Path.GetExtension(row.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            _basicOptions.Children.Add(MusicWishUi.Text(L("PdfHint")));
        if (format is "pdf" or "html" or "epub")
            AddSlider(_expertOptions, "FontSize", 8, 28, _options.FontSize, value => _options = _options with { FontSize = value });
        if (format is "pdf" or "html")
            AddSlider(_expertOptions, "Margin", 0, 40, _options.MarginMillimeters, value => _options = _options with { MarginMillimeters = value });
        if (format == "cbz")
            AddSlider(_expertOptions, "ImageQuality", 40, 100, _options.ImageQuality, value => _options = _options with { ImageQuality = (int)value });
        if (_entries.Any(row => row.IsChecked && Path.GetExtension(row.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            _expertOptions.Children.Add(MusicWishUi.Text(L("TextEncoding")));
            var encoding = new ComboBox { ItemsSource = new[] { "utf-8", "windows-1251", "utf-16", "windows-1252" }, SelectedItem = _options.TextEncoding, Margin = new(0, 3, 0, 8) };
            AutomationProperties.SetAutomationId(encoding, "BookConverter.TextEncoding");
            encoding.SelectionChanged += (_, _) => _options = _options with { TextEncoding = encoding.SelectedItem as string ?? "utf-8" };
            _expertOptions.Children.Add(encoding);
        }
        if (_expertOptions.Children.Count == 0) _expertOptions.Children.Add(MusicWishUi.Text(L("NoAdvancedOptions")));
    }

    private void AddSlider(Panel parent, string key, double min, double max, double value, Action<double> changed)
    {
        var label = MusicWishUi.Text(L(key) + ": " + value.ToString("0")); parent.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(2, 2, 9, 12) };
        AutomationProperties.SetAutomationId(slider, "BookConverter." + key); AutomationProperties.SetName(slider, L(key));
        slider.ValueChanged += (_, _) => { label.Text = L(key) + ": " + slider.Value.ToString("0"); changed(slider.Value); };
        parent.Children.Add(slider);
    }

}
