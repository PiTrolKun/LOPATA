using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderSpeechPrompt : Window
{
    internal ReaderSpeechRange? Range { get; private set; }

    internal ReaderSpeechPrompt(Window owner, Func<string, string> localize, int pages,
        ReaderSpeechRange? selected, VideoNarratorChoice voice)
    {
        string T(string key) => localize("Reader.Speech." + key);
        Owner = owner; Icon = owner.Icon; Title = T("Title"); Width = 490; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(owner.Resources); Background = owner.Background; Foreground = owner.Foreground;
        FontSize = 14;
        AutomationProperties.SetAutomationId(this, "Office.Reader.Speech.Prompt");
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        var name = VideoNarratorCatalog.Get(voice.Id).Name + " · " + voice.Options.Speaker;
        panel.Children.Add(new TextBlock { Text = string.Format(T("Model"), name), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 15) });
        var selection = new RadioButton { Content = T("Selection"), IsChecked = selected is not null, GroupName = "scope", Margin = new(0, 4, 0, 8), Foreground = Foreground };
        AutomationProperties.SetAutomationId(selection, "Office.Reader.Speech.Selection");
        if (selected is not null) panel.Children.Add(selection);
        var all = new RadioButton { Content = T("Pages"), IsChecked = selected is null, GroupName = "scope", Margin = new(0, 4, 0, 8), Foreground = Foreground };
        if (selected is not null) panel.Children.Add(all);
        var fields = new StackPanel();
        var first = new TextBox { Text = "1", Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        var count = new TextBox { Text = pages.ToString(), Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(first, "Office.Reader.Speech.FirstPage"); AutomationProperties.SetAutomationId(count, "Office.Reader.Speech.PageCount");
        if (pages > 1)
        {
            fields.Children.Add(new TextBlock { Text = string.Format(T("AvailablePages"), pages), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
            fields.Children.Add(new TextBlock { Text = T("FirstPage") }); fields.Children.Add(first);
            fields.Children.Add(new TextBlock { Text = T("PageCount"), Margin = new(0, 8, 0, 0) }); fields.Children.Add(count);
            panel.Children.Add(fields);
        }
        else if (selected is null) panel.Children.Add(new TextBlock { Text = T("ConfirmOne"), TextWrapping = TextWrapping.Wrap });
        fields.IsEnabled = selected is null;
        selection.Checked += (_, _) => fields.IsEnabled = false;
        all.Checked += (_, _) => fields.IsEnabled = true;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true }; buttons.Children.Add(cancel);
        var start = new Button { Content = T("Start"), IsDefault = true }; buttons.Children.Add(start);
        AutomationProperties.SetAutomationId(start, "Office.Reader.Speech.Start"); panel.Children.Add(buttons);
        start.Click += (_, _) => {
            if (selected is not null && selection.IsChecked == true) Range = selected;
            else if (int.TryParse(first.Text, out var from) && int.TryParse(count.Text, out var length)
                && from >= 1 && from <= pages && length >= 1 && length <= pages - from + 1) Range = new(from - 1, length);
            else { error.Text = T("InvalidRange"); return; }
            DialogResult = true;
        };
    }
}
