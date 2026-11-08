using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private string ActivityTitle(OfficeActivityEntry entry)
    {
        var label = SheetText("Action." + entry.Action);
        return label == "OfficeEditor.Sheet.Action." + entry.Action ? entry.Action : label;
    }

    private void ShowJournal(OfficeSheetKind kind)
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(2,GridUnitType.Star), MinWidth = 170 });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(3,GridUnitType.Star) });
        var entries = new ListBox { Margin = new Thickness(0,0,10,0) };
        entries.SetResourceReference(ForegroundProperty,"ReaderText");
        entries.SetResourceReference(BackgroundProperty,"ReaderPanel");
        AutomationProperties.SetAutomationId(entries,"Office.Editor.Journal.Entries");
        var details = new StackPanel { Margin = new Thickness(6,0,12,8) };
        var scroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll,1); grid.Children.Add(entries); grid.Children.Add(scroll);
        var sheetNames = Work.Sheets.ToDictionary(s => s.Id, SheetName);
        if (kind == OfficeSheetKind.ActivityLog)
        {
            var actions = Work.ActivityLog.Reverse().ToList();
            entries.ItemsSource = actions.Select(a => a.AtUtc.ToLocalTime().ToString("g") + "  ·  " + ActivityTitle(a)).ToList();
            entries.SelectionChanged += (_,_) =>
            {
                if (entries.SelectedIndex < 0) return;
                var a = actions[entries.SelectedIndex];
                details.Children.Clear();
                JournalField(details, SheetText("Action"),ActivityTitle(a));
                JournalField(details, SheetText("Time"), a.AtUtc.ToLocalTime().ToString("G"));
                JournalField(details, SheetText("Material"), sheetNames.GetValueOrDefault(a.SheetId,a.SheetId));
                if (!string.IsNullOrWhiteSpace(a.Detail)) JournalField(details,SheetText("Details"),a.Detail);
            };
        }
        else
        {
            var history = Work.AiEntries.Reverse().ToList();
            entries.ItemsSource = history.Select(a => a.StartedUtc.ToLocalTime().ToString("g") + "  ·  " + a.Action).ToList();
            entries.SelectionChanged += (_,_) =>
            {
                if (entries.SelectedIndex < 0) return;
                var entry = history[entries.SelectedIndex];
                details.Children.Clear();
                JournalField(details, SheetText("Action"),entry.Action);
                JournalField(details, SheetText("Status"), SheetText("Status." + entry.Status));
                JournalField(details, SheetText("Material"), sheetNames.GetValueOrDefault(entry.SheetId,entry.SheetId));
                var unknown = SheetText("Unrecorded");
                var models = entry.Models.Select(m => string.Join(" · ",new[] {m.Name,m.Id,m.Revision,m.Role}
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)));
                JournalField(details, SheetText("Models"),entry.Models.Length == 0 ? unknown : string.Join(Environment.NewLine,models));
                if (!string.IsNullOrWhiteSpace(entry.ModelRequested)) JournalField(details,SheetText("RequestedModel"),entry.ModelRequested);
                JournalField(details,SheetText("Request"),string.IsNullOrWhiteSpace(entry.Request) ? unknown : entry.Request);
                var prompt = new Expander { Header = SheetText("SystemPrompt"), IsExpanded = false, Margin = new Thickness(0,6,0,4),
                    Content = JournalValue(entry.SystemPrompt ?? (entry.Status == "legacy" ? unknown : SheetText("NoPrompt"))) };
                prompt.SetResourceReference(ForegroundProperty,"ReaderText");
                AutomationProperties.SetAutomationId(prompt,"Office.Editor.Journal.SystemPrompt");
                details.Children.Add(prompt);
                if (!string.IsNullOrWhiteSpace(entry.Parameters)) JournalField(details,SheetText("Parameters"),entry.Parameters);
                if (!string.IsNullOrWhiteSpace(entry.Response)) JournalField(details,SheetText("Response"),entry.Response);
                if (!string.IsNullOrWhiteSpace(entry.Failure)) JournalField(details, SheetText("Failure"),entry.Failure);
                if (entry.ResultName is { Length: > 0 } result)
                    details.Children.Add(Action(SheetText("Extract") + " · " + result,"Journal.ExtractResult",() => _ = GuardAsync(async () =>
                    {
                        var picker = new Microsoft.Win32.SaveFileDialog { Title = SheetText("Extract"),FileName = result,OverwritePrompt = true };
                        if (picker.ShowDialog(_owner) == true) await Work.ExportAiResultAsync(entry.Id,picker.FileName,_lifetime.Token);
                    })));
                else if (entry.Response == null && entry.Status == "legacy") JournalField(details,SheetText("Response"),unknown);
            };
        }
        entries.SelectedIndex = entries.Items.Count > 0 ? 0 : -1;
        _sheetPanel.Content = grid;
    }

    private static TextBox JournalValue(string value) => new()
    {
        Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0), Padding = new Thickness(6,3,6,6), AcceptsReturn = true
    };

    private static void JournalField(StackPanel panel,string label,string value)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,8,0,2) });
        var content = JournalValue(value);
        AutomationProperties.SetName(content,label);
        panel.Children.Add(content);
    }
}
