using System.Windows.Controls;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class LiteraryProjectCreateControl
{
    private readonly ComboBox _jellyMode = new() { MinHeight = 32 };
    private readonly TextBox _writerPlan = Input(true), _writerAvoid = Input(true), _advisorPlan = Input(true), _advisorAvoid = Input(true);
    private void BuildMemory()
    {
        var panel = Section("Literary.MemorySetup.Title");
        foreach (var mode in new[] { "runeweaver", "gliner", "nuextract" })
            _jellyMode.Items.Add(new ComboBoxItem { Content = _l("Literary.MemorySetup." + mode), Tag = mode });
        Field(panel, "Literary.MemorySetup.Executor", _jellyMode);
        var explanation = LiteraryUi.Text(""); panel.Children.Add(explanation);
        _jellyMode.SelectionChanged += (_, _) => explanation.Text = _l("Literary.MemorySetup." + ((ComboBoxItem)_jellyMode.SelectedItem).Tag + ".Hint");
        _jellyMode.SelectedIndex = 0;
        panel.Children.Add(LiteraryUi.Text(_l("Literary.MemorySetup.AnchorsHint")));
        panel.Children.Add(LiteraryUi.Text(_l("Literary.Workspace.Writer"), true));
        Field(panel, "Literary.Anchor.Positive", _writerPlan); Field(panel, "Literary.Anchor.Negative", _writerAvoid);
        panel.Children.Add(LiteraryUi.Text(_l("Literary.Workspace.Advisor"), true));
        Field(panel, "Literary.Anchor.Positive", _advisorPlan); Field(panel, "Literary.Anchor.Negative", _advisorAvoid);
        foreach (var box in new[] { _writerPlan, _writerAvoid, _advisorPlan, _advisorAvoid }) box.MaxLength = LiteraryPlotAnchorStore.MaxCharacters;
    }
    private bool ValidateAnchors()
    {
        foreach (var pair in new[] { (_writerPlan, _writerAvoid), (_advisorPlan, _advisorAvoid) })
            if (pair.Item1.Text.Length + pair.Item2.Text.Length > LiteraryPlotAnchorStore.MaxCharacters)
            { _error.Text = _l("Literary.MemorySetup.AnchorTooLong"); pair.Item1.BringIntoView(); pair.Item1.Focus(); return false; }
        return true;
    }
    // Capture UI values before entering the background project transaction.
    private Action<string> InitialAnchors()
    {
        var writer = (_writerPlan.Text, _writerAvoid.Text); var advisor = (_advisorPlan.Text, _advisorAvoid.Text);
        return root =>
        {
            var layout = new LiteraryProjectLayout(root);
            foreach (var (role, values) in new[] { (LiteraryChatProfile.Writer, writer), (LiteraryChatProfile.Advisor, advisor) })
            {
                var store = new LiteraryPlotAnchorStore(layout, role);
                store.Save(values.Item1, values.Item2, store.Load().Revision);
            }
        };
    }
}
