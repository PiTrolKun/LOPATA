using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Binding = System.Windows.Data.Binding;

namespace AIHub.Controls;

public sealed class MusicOutputControl : UserControl
{
    private readonly ComboBox _format = new() { Width = 86, MinHeight = 30 }, _extra = new() { Width = 86, MinHeight = 30 };
    private readonly ComboBox _bitrate = new() { Width = 64, MinHeight = 30 }, _extraBitrate = new() { Width = 64, MinHeight = 30 };
    private readonly CheckBox _duplicate = new() { Content = "+", VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 0, 0) };
    private readonly TextBlock _label = MusicAudioUi.Text(12), _extraLabel = MusicAudioUi.Text(11);
    private readonly StackPanel _extraRow;
    private bool _updating;
    private Func<string, string> _l = key => key;
    public event Action? Changed;
    public MusicOutputSettings Settings => new() { Format = (MusicAudioFormat)(_format.SelectedItem ?? MusicAudioFormat.Opus),
        Bitrate = (int)(_bitrate.SelectedItem ?? MusicOutputSettings.DefaultBitrate), AdditionalFormat = _duplicate.IsChecked == true ? (MusicAudioFormat?)_extra.SelectedItem : null,
        AdditionalBitrate = (int)(_extraBitrate.SelectedItem ?? MusicOutputSettings.DefaultBitrate) };
    public MusicOutputControl()
    {
        _duplicate.Width = 26; _duplicate.Height = 30; _duplicate.MinHeight = 0; _duplicate.MinWidth = 0;
        _duplicate.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <Border x:Name="Frame" CornerRadius="5" Background="Transparent" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1">
                <TextBlock x:Name="Symbol" Text="{TemplateBinding Content}" FontSize="18" TextWrapping="NoWrap" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimaryBrush}"/>
              </Border>
              <ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}" ClickMode="Press">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="{DynamicResource SecondaryButtonBackgroundBrush}" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1" CornerRadius="6">
                    <TextBlock Text="⌄" Margin="0,0,5,0" HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimaryBrush}"/>
                  </Border></ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter Margin="5,3,16,3" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" TextElement.Foreground="{DynamicResource TextPrimaryBrush}"/>
                <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}">
                  <Border Background="{DynamicResource PanelBrush}" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1" MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}">
                    <ScrollViewer MaxHeight="240"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        foreach (var box in new[] { _format, _extra, _bitrate, _extraBitrate }) {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new FormatLabel() });
            box.Template = template; box.FontSize = 14; box.ItemTemplate = new DataTemplate { VisualTree = text };
        }
        var root = new StackPanel(); root.Children.Add(_label);
        var primary = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        primary.Children.Add(_format); primary.Children.Add(_duplicate); primary.Children.Add(_bitrate); root.Children.Add(primary);
        root.Children.Add(_extraLabel); _extraRow = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _extraRow.Children.Add(_extra); _extraRow.Children.Add(_extraBitrate); root.Children.Add(_extraRow); Content = root;
        foreach (var format in Enum.GetValues<MusicAudioFormat>()) _format.Items.Add(format);
        foreach (var value in MusicOutputSettings.Bitrates) { _bitrate.Items.Add(value); _extraBitrate.Items.Add(value); }
        AutomationProperties.SetAutomationId(_format, "Music.Output.Format"); AutomationProperties.SetAutomationId(_extra, "Music.Output.AdditionalFormat");
        AutomationProperties.SetAutomationId(_bitrate, "Music.Output.Bitrate"); AutomationProperties.SetAutomationId(_extraBitrate, "Music.Output.AdditionalBitrate");
        AutomationProperties.SetAutomationId(_duplicate, "Music.Output.Duplicate");
        _format.SelectionChanged += (_, _) => Refresh(); _extra.SelectionChanged += (_, _) => Refresh();
        _bitrate.SelectionChanged += (_, _) => Refresh(); _extraBitrate.SelectionChanged += (_, _) => Refresh();
        _duplicate.Checked += (_, _) => Refresh(); _duplicate.Unchecked += (_, _) => Refresh();
        Set(new());
        SizeChanged += (_, _) => {
            var width = Math.Clamp(ActualWidth - 96, 56, 86); _format.Width = _extra.Width = width;
        };
    }
    public void Set(MusicOutputSettings settings)
    {
        settings.Validate(); _updating = true;
        _format.SelectedItem = settings.Format; _bitrate.SelectedItem = settings.Bitrate;
        FillExtra(settings.AdditionalFormat ?? MusicAudioFormat.Flac); _extraBitrate.SelectedItem = settings.AdditionalBitrate;
        _duplicate.IsChecked = settings.AdditionalFormat.HasValue; _updating = false; Refresh();
    }
    private void FillExtra(MusicAudioFormat preferred)
    {
        _extra.Items.Clear(); foreach (var value in Enum.GetValues<MusicAudioFormat>()) if (!Equals(value, _format.SelectedItem)) _extra.Items.Add(value);
        _extra.SelectedItem = _extra.Items.Contains(preferred) ? preferred : _extra.Items.Contains(MusicAudioFormat.Flac) ? MusicAudioFormat.Flac : _extra.Items[0];
    }
    private void Refresh()
    {
        if (_updating) return; _updating = true;
        if (_extra.Items.Contains(_format.SelectedItem)) FillExtra((MusicAudioFormat)(_extra.SelectedItem ?? MusicAudioFormat.Flac));
        _bitrate.Visibility = MusicOutputSettings.Lossy(Settings.Format) ? Visibility.Visible : Visibility.Collapsed;
        _extraBitrate.Visibility = MusicOutputSettings.Lossy((MusicAudioFormat)(_extra.SelectedItem ?? MusicAudioFormat.Flac)) ? Visibility.Visible : Visibility.Collapsed;
        _extraRow.Visibility = _extraLabel.Visibility = _duplicate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDuplicateButton();
        _updating = false; Changed?.Invoke();
    }
    private void UpdateDuplicateButton()
    {
        var enabled = _duplicate.IsChecked == true;
        _duplicate.Content = enabled ? "−" : "+";
        var label = _l(enabled ? "Music.Output.RemoveDuplicate" : "Music.Output.Duplicate");
        _duplicate.ToolTip = label; AutomationProperties.SetName(_duplicate, label);
    }
    public void Localize(Func<string, string> l)
    {
        _l = l;
        _label.Text = l("Music.Output.Format"); _extraLabel.Text = l("Music.Output.Additional");
        UpdateDuplicateButton();
        _bitrate.ToolTip = _extraBitrate.ToolTip = l("Music.Output.Bitrate");
        _format.ToolTip = _extra.ToolTip = l("Music.Output.Hint");
    }
    private sealed class FormatLabel : IValueConverter
    {
        public object Convert(object value, Type target, object parameter, CultureInfo culture) => value is MusicAudioFormat format
            ? format == MusicAudioFormat.Opus ? "Opus" : format.ToString().ToUpperInvariant() : value.ToString() ?? "";
        public object ConvertBack(object value, Type target, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
