using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryDraftControl : UserControl
{
    private readonly TextBox _editor = LiteraryWorkspaceParts.TextArea(false);
    private readonly TextBlock _counts = new(), _status = new(), _hint = new();
    private readonly LiteraryDraftStore _store;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Func<string, string> _l;
    private string _accepted = "", _saved = "";
    private bool _restoring, _loadFailed;
    private string _statusKey = "Literary.Draft.Ready";
    public string Text => _editor.Text;
    public LiteraryDraftControl(string projectDirectory, Func<string, string> localize)
    {
        _store = new LiteraryDraftStore(projectDirectory); _l = localize;
        try { _accepted = _saved = _store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { _loadFailed = true; _statusKey = "Literary.Draft.LoadError"; }
        _editor.Text = _accepted; _editor.IsReadOnly = _loadFailed;
        _editor.TextChanged += (_, _) => Changed();
        _saveTimer.Tick += (_, _) => Save();
        Unloaded += (_, _) => Save();
        var panel = new DockPanel();
        _status.TextWrapping = _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 6);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _counts.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DockPanel.SetDock(_hint, Dock.Top); panel.Children.Add(_hint);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        DockPanel.SetDock(_counts, Dock.Bottom); panel.Children.Add(_counts);
        panel.Children.Add(_editor); Content = panel;
        ApplyLocalization(localize);
    }
    public void ApplyLocalization(Func<string, string> localize)
    {
        _l = localize; _hint.Text = _l("Literary.Draft.Hint");
        _editor.ToolTip = _store.FilePath; Refresh();
    }
    private void Changed()
    {
        if (_restoring) return;
        if (_editor.Text.Length > LiteraryModelPolicy.DraftCharacters && _editor.Text.Length > _accepted.Length)
        {
            _restoring = true;
            var caret = Math.Min(_editor.CaretIndex, _accepted.Length);
            _editor.Text = _accepted; _editor.CaretIndex = caret;
            _restoring = false; _statusKey = "Literary.Draft.Full"; Refresh(); return;
        }
        _accepted = _editor.Text;
        _statusKey = "Literary.Draft.Unsaved";
        _saveTimer.Stop(); _saveTimer.Start(); Refresh();
    }
    public void Save()
    {
        _saveTimer.Stop();
        if (_loadFailed || _accepted == _saved) return;
        try { _store.Save(_accepted); _saved = _accepted; if (_statusKey != "Literary.Draft.Full") _statusKey = "Literary.Draft.Saved"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _statusKey = "Literary.Draft.SaveError"; }
        Refresh();
    }
    private void Refresh()
    {
        _counts.Text = string.Format(_l("Literary.Draft.Counts"), _editor.Text.Length / 2500d, LiteraryModelPolicy.Pages);
        _status.Text = _l(_statusKey);
    }
}
