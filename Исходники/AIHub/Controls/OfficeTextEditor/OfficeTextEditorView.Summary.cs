using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls.OfficeReader;
using AIHub.Services.OfficeReader;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private TabControl? _aiTabs;
    private TabItem? _aiTab;
    private OfficeSummaryView? _summaryView;
    private bool _summarizing;

    private async Task SummarizeAsync()
    {
        if (!_ready || _closing || _saving || _speaking || _reviewOpening || _review != null || _summarizing || !CanReadActiveSheet) return;
        _summarizing = true;
        try
        {
            var selected = IsTextSheet ? (await RunAsync(new { action = "text", selection = true }))?.GetString()
                : _workbook?.SummarySelection() ?? SelectedNativeText(_sheetPanel);
            var journal = CreateAiJournal(_l("OfficeSummary.Title"));
            if (_summaryView != null) await _summaryView.DisposeAsync();
            if (_aiTabs == null)
            {
                _workspace.Children.Remove(_root);
                _aiTabs = new TabControl();
                _aiTabs.Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/OfficeSummaryResources.xaml", UriKind.Relative) });
                _aiTabs.Items.Add(new TabItem { Header = _l("OfficeSummary.DocumentTab"), Content = _root });
                _aiTab = new TabItem { Header = _l("OfficeSummary.AiTab") };
                AutomationProperties.SetAutomationId(_aiTab, "Office.Editor.AiTab");
                _aiTabs.Items.Add(_aiTab); _workspace.Children.Add(_aiTabs);
            }
            _summaryView = new(_owner, _l, !string.IsNullOrWhiteSpace(selected), async token =>
            {
                await FinishDictationAsync();
                if (!string.IsNullOrWhiteSpace(selected)) return selected;
                if (IsTextSheet) return (await RunAsync(new { action = "text", selection = false }))?.GetString() ?? "";
                var path = await CreateReaderSnapshotAsync();
                try
                {
                    await using var document = await ReaderPreparation.OpenAsync(path, null, token, useCache: false);
                    return await new ReaderSpeechRange(0, document.Pages.Count).ReadAsync(document, token);
                }
                finally { File.Delete(path); }
            }, journal, () => _aiTabs.SelectedIndex = 0, SourcePath, TargetPath);
            _summaryView.Completed += () => { _root.IsEnabled = true; _summarizing = false; };
            _root.IsEnabled = false;
            _aiTab!.Content = _summaryView;
            _aiTabs.SelectedItem = _aiTab;
        }
        catch { _summarizing = false; _root.IsEnabled = true; throw; }
    }

    private static string? SelectedNativeText(DependencyObject parent)
    {
        if (parent is TextBox { SelectionLength: > 0 } text) return text.SelectedText;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (SelectedNativeText(VisualTreeHelper.GetChild(parent, i)) is { } selected) return selected;
        return null;
    }
}
