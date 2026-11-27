using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private ConstructorGemmaHistory GemmaHistory(string projectFile) => new(Path.GetDirectoryName(_recentFile)!, projectFile);

    private async Task BrowseGemmaHistoryAsync()
    {
        if (_project is null) return;
        var history = GemmaHistory(_project.FilePath).Read();
        var list = DialogChoices(history.Select(turn => new Choice(turn.Id.ToString("N"),
            $"{turn.Started.ToLocalTime():g} · {turn.Prompt.Split('\n', 2)[0].Trim()}")));
        var details = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(details, "VisualConstructor.GemmaHistoryDetails");
        var panel = new StackPanel();
        panel.Children.Add(details);
        var body = ChoiceDialogBody(list, panel, "GemmaHistoryEntries", "GemmaHistoryEntries");
        var dialog = EditDialog(T("GemmaHistory"), body, out var buttons, width: 900, height: 610);
        var reuse = EditDialogButtons(dialog, buttons, "GemmaHistoryReuse", "GemmaHistoryReuse", () => dialog.DialogResult = true);
        reuse.ToolTip = T("GemmaHistoryReuseHint");
        ConstructorGemmaTurn? selected = null;
        list.SelectionChanged += (_, _) =>
        {
            selected = history.FirstOrDefault(turn => turn.Id.ToString("N") == list.SelectedValue as string);
            Refresh();
        };
        list.SelectedIndex = history.Count == 0 ? -1 : 0;
        Refresh();
        if (dialog.ShowDialog() == true && selected is not null)
            await AskGemmaAsync(selected.Code, selected.Prompt);

        void Refresh()
        {
            details.Text = selected is null ? T("GemmaHistoryEmpty") :
                $"{selected.Target}\n{selected.Started.ToLocalTime():g} · {T(selected.Code ? "WriteCode" : "SuggestActions")}\n\n" +
                $"{T("GemmaHistoryRequest")}\n{selected.Prompt}\n\n{T("GemmaHistoryReply")}\n{selected.Reply ?? T("GemmaHistoryPending")}";
            reuse.IsEnabled = selected is not null && (selected.Code ? _writeCode.IsEnabled : _suggestActions.IsEnabled);
        }
    }
}
