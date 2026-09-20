using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl : UserControl, IDisposable
{
    private readonly Func<string, string> _l;
    private readonly string _language;
    private readonly LiteraryProjectStore _store;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sessionLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 5, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _cancel;
    private readonly TextBox _folder = new(), _name = new(), _genre = new();
    private readonly ComboBox _works = new();
    private readonly List<(CheckBox Box, string Id)> _conversations = [];
    private ImportSession? _session;
    private ImportInput? _input;
    private ImportDecision[]? _first;
    private LiteraryChatRuntime? _runtime;
    private CancellationTokenSource? _operation;
    public bool IsBusy => _operation is not null;
    public event Action? BackRequested;
    public event Action<LiteraryProjectEntry>? OpenRequested;
    public LiteraryImportControl(Func<string, string> l, string language, string folder, LiteraryProjectStore store)
    {
        _l = l; _language = language; _store = store;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryRagEditorTheme.xaml", UriKind.Relative) });
        _folder.Text = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var box in new[] { _folder, _name, _genre })
        { box.Margin = new Thickness(0, 4, 0, 10); box.Padding = new Thickness(8); box.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); }
        var root = new DockPanel { Margin = new Thickness(36, 24, 36, 24), MaxWidth = 1200 };
        var heading = LiteraryUi.Text(L("Title"), true); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _status.SetResourceReference(ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_status); footer.Children.Add(_progress);
        _sessionLabel.SetResourceReference(ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_sessionLabel);
        var row = new WrapPanel();
        _cancel = LiteraryUi.Button(L("Stop"), () => _operation?.Cancel()); _cancel.IsEnabled = false; row.Children.Add(_cancel);
        row.Children.Add(LiteraryUi.Button(l("Literary.Back"), () => { if (!IsBusy) BackRequested?.Invoke(); })); footer.Children.Add(row);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root; ShowSource();
    }
    private string L(string key) => _l("Literary.Import." + key);
    private void ShowSource()
    {
        _body.Children.Clear();
        _body.Children.Add(LiteraryUi.Text(L("LocalOnly")));
        var sources = new ComboBox { Margin = new Thickness(0, 8, 0, 8), SelectedIndex = 0 };
        sources.Items.Add(new ComboBoxItem { Content = "DeepSeek · ZIP / JSON" });
        foreach (var source in new[] { "ChatGPT", "Claude", "Gemini", "Grok", "Copilot", "Perplexity", "Qwen", "Kimi", "Mistral", "Poe", "TXT", "ЛОПАТА" })
            sources.Items.Add(new ComboBoxItem { Content = source + " · " + _l("Literary.Pending"), IsEnabled = false });
        _body.Children.Add(sources);
        var modes = new ComboBox { Margin = new Thickness(0, 8, 0, 12), SelectedIndex = 0 };
        modes.Items.Add(new ComboBoxItem { Content = L("Quick") });
        foreach (var mode in new[] { "Semi", "Auto" }) modes.Items.Add(new ComboBoxItem { Content = L(mode) + " · " + _l("Literary.Pending"), IsEnabled = false });
        _body.Children.Add(modes);
        _body.Children.Add(LiteraryUi.Text(L("Folder"))); _body.Children.Add(_folder);
        _body.Children.Add(LiteraryUi.Button(L("ChooseFolder"), ChooseFolder));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(LiteraryUi.Button(L("ChooseFile"), ChooseFile, true));
        buttons.Children.Add(LiteraryUi.Button(L("Resume"), Resume)); _body.Children.Add(buttons);
    }
    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _folder.Text = dialog.FolderName;
    }
    private void ChooseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "DeepSeek (*.zip;*.json)|*.zip;*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = _folder.Text; var source = dialog.FileName;
        _ = RunAsync(async ct =>
        {
            _session?.Dispose(); _session = null;
            _session = await Task.Run(() => ImportSession.Create(folder, source, ct), ct);
            _input = await Task.Run(() => DeepSeekImportReader.ReadAsync(_session, ct), ct);
            ShowConversations();
        });
    }
    private void Resume()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LOPATA import (session.json)|session.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = RunAsync(async ct =>
        {
            _session?.Dispose(); _session = null;
            _session = await Task.Run(() => ImportSession.Open(Path.GetDirectoryName(dialog.FileName)!), ct);
            _input = await Task.Run(() => DeepSeekImportReader.ReadAsync(_session, ct), ct);
            ShowConversations();
        });
    }
    private void ShowConversations()
    {
        _body.Children.Clear(); _conversations.Clear();
        _body.Children.Add(LiteraryUi.Text(L("ChooseConversations"), true));
        foreach (var conversation in _input!.Conversations)
        {
            var check = new CheckBox { Content = conversation.Title + " · " + conversation.Units,
                IsChecked = _session!.State.Conversations.Contains(conversation.Id), Margin = new Thickness(0, 8, 0, 8) };
            check.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            _conversations.Add((check, conversation.Id)); _body.Children.Add(check);
        }
        if (_conversations.Count == 1) _conversations[0].Box.IsChecked = true;
        _body.Children.Add(LiteraryUi.Text(L("Inventory") + $" {_input.Inventory.Count}; " + L("Warnings") + $" {_input.Warnings.Count}"));
        if (_input.Warnings.Count > 0)
            _body.Children.Add(LiteraryUi.Text(L("FormatNotice")));
        foreach (var entry in _input.Inventory.Where(e => e.Status != "conversations"))
            _body.Children.Add(LiteraryUi.Text(entry.Name + " · " + L("AttachmentSkipped")));
        _body.Children.Add(LiteraryUi.Button(L("Analyze"), Analyze, true));
    }
    public void Dispose()
    {
        if (IsBusy) { _operation!.Cancel(); return; }
        _runtime?.Dispose(); _runtime = null; _session?.Dispose(); _session = null;
    }
}
