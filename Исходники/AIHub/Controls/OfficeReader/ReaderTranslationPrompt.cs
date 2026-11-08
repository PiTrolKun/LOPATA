using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderTranslationPrompt : Window
{
    internal ReaderSpeechRange? Range { get; private set; }
    internal string Source => (string)((ComboBoxItem)_source.SelectedItem).Tag;
    internal string Target => (string)((ComboBoxItem)_target.SelectedItem).Tag;
    private readonly ComboBox _source = new() { MinWidth = 160 }, _target = new() { MinWidth = 160 };

    internal ReaderTranslationPrompt(Window owner, Func<string, string> localize, int pages, ReaderSpeechRange? selected, string model,
        bool review = false, bool selectionCopy = false)
    {
        string T(string key) => localize("Reader.Translation." + key);
        string S(string key) => localize("Reader.Speech." + key);
        Owner = owner; Icon = owner.Icon; Title = T("Title"); Width = 540; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources); Background = owner.Background; Foreground = owner.Foreground; FontSize = 14;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderLanguageResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Office.Reader.Translation.Prompt");
        var panel = new StackPanel { Margin = new(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = string.Format(T("Model"), model), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var languageRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new(0, 0, 0, 14) };
        foreach (var code in new[] { "ru", "en", "de", "fr", "es", "it", "pt", "zh", "ja", "ko", "ar", "hi", "tr", "pl", "uk", "be", "kk", "cs", "nl", "sv", "fi", "el", "he", "vi", "id" })
        {
            var name = new CultureInfo(code).NativeName;
            _source.Items.Add(new ComboBoxItem { Content = name, Tag = code }); _target.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        _source.SelectedIndex = 0; _target.SelectedIndex = 1;
        AutomationProperties.SetAutomationId(_source, "Office.Reader.Translation.Source"); AutomationProperties.SetName(_source, T("Source"));
        AutomationProperties.SetAutomationId(_target, "Office.Reader.Translation.Target"); AutomationProperties.SetName(_target, T("Target"));
        var swap = new Button { Content = "⇄", ToolTip = T("Swap") };
        AutomationProperties.SetAutomationId(swap, "Office.Reader.Translation.Swap"); AutomationProperties.SetName(swap, T("Swap"));
        swap.Click += (_, _) => { var first = _source.SelectedIndex; _source.SelectedIndex = _target.SelectedIndex; _target.SelectedIndex = first; };
        languageRow.Children.Add(_source); languageRow.Children.Add(swap); languageRow.Children.Add(_target); panel.Children.Add(languageRow);
        var selection = new RadioButton { Content = T("Selection"), IsChecked = selected is not null, GroupName = "scope", Foreground = Foreground, Margin = new(0, 4, 0, 8) };
        var all = new RadioButton { Content = T("Pages"), IsChecked = selected is null, GroupName = "scope", Foreground = Foreground, Margin = new(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(selection, "Office.Reader.Translation.Selection");
        AutomationProperties.SetAutomationId(all, "Office.Reader.Translation.Pages");
        if (selected is not null) { panel.Children.Add(selection); panel.Children.Add(all); }
        var fields = new StackPanel { IsEnabled = selected is null };
        var firstPage = new TextBox { Text = "1", Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        var count = new TextBox { Text = pages.ToString(), Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(firstPage, "Office.Reader.Translation.FirstPage"); AutomationProperties.SetAutomationId(count, "Office.Reader.Translation.PageCount");
        if (pages > 1)
        {
            fields.Children.Add(new TextBlock { Text = string.Format(S("AvailablePages"), pages), Margin = new(0, 0, 0, 8) });
            fields.Children.Add(new TextBlock { Text = S("FirstPage") }); fields.Children.Add(firstPage);
            fields.Children.Add(new TextBlock { Text = T("PageCount"), Margin = new(0, 8, 0, 0) }); fields.Children.Add(count); panel.Children.Add(fields);
        }
        else if (selected is null && !review) panel.Children.Add(new TextBlock { Text = T("ConfirmOne"), TextWrapping = TextWrapping.Wrap });
        selection.Checked += (_, _) => fields.IsEnabled = false; all.Checked += (_, _) => fields.IsEnabled = true;
        panel.Children.Add(new TextBlock { Text = review ? localize("OfficeEditor.ReviewTranslationHint") : T("LayoutNote"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 12, 0, 0) });
        if (selectionCopy) panel.Children.Add(new TextBlock { Text=T("SelectionCopy"),TextWrapping=TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        buttons.Children.Add(new Button { Content = S("Cancel"), IsCancel = true });
        var start = new Button { Content = T("Start"), IsDefault = true }; buttons.Children.Add(start); panel.Children.Add(buttons);
        AutomationProperties.SetAutomationId(start, "Office.Reader.Translation.Start");
        start.Click += (_, _) => {
            if (Source == Target) { error.Text = T("SameLanguage"); return; }
            if (selected is not null && selection.IsChecked == true) Range = selected;
            else if (int.TryParse(firstPage.Text, out var from) && int.TryParse(count.Text, out var length)
                && from >= 1 && from <= pages && length >= 1 && length <= pages - from + 1) Range = new(from - 1, length);
            else { error.Text = S("InvalidRange"); return; }
            DialogResult = true;
        };
    }
}
