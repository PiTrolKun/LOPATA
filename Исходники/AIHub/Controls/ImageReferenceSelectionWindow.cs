using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Application = System.Windows.Application;

namespace AIHub.Controls;

public sealed class ImageReferenceSelectionWindow : Window
{
    private readonly string _path;
    private readonly ComboBox _scope = new();
    public ImageReferenceSelection? Selection { get; private set; }
    public ImageReferenceSelectionWindow(string path, Func<string, string> localize)
    {
        _path = path; Title = localize("Generation.ReferenceTitle"); Width = 620; Height = 650;
        MinWidth = 400; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.MainWindow is { } main) Resources.MergedDictionaries.Add(main.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label(localize("Generation.ReferenceHint")));
        using (var input = File.OpenRead(path))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 540; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            panel.Children.Add(new Image { Source = bitmap, Height = 340, Stretch = Stretch.Uniform });
        }
        panel.Children.Add(Label(localize("Generation.ReferenceScope")));
        foreach (var value in Enum.GetValues<ImageReferenceScope>())
            _scope.Items.Add(new ComboBoxItem { Content = localize("Generation.ReferenceScope." + value), Tag = value });
        _scope.SelectedIndex = 0; AutomationProperties.SetAutomationId(_scope, "Generation.Reference.Scope"); panel.Children.Add(_scope);
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var analyze = new Button { Content = localize("Generation.ReferenceAnalyze"), Padding = new Thickness(14, 8, 14, 8), IsDefault = true };
        analyze.Click += (_, _) => { Selection = new(_path, (ImageReferenceScope)((ComboBoxItem)_scope.SelectedItem).Tag); DialogResult = true; };
        var cancel = new Button { Content = localize("Common.Cancel"), IsCancel = true, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(14, 8, 14, 8) };
        buttons.Children.Add(analyze); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return label;
    }
}
