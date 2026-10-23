using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Clipboard = System.Windows.Clipboard;
using TabControl = System.Windows.Controls.TabControl;

namespace AIHub.Controls.VectorEditor;

internal sealed partial class VectorEditorWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly VectorEditorBrowser _browser = new();
    private readonly VectorProjectHistory _history = new();
    private readonly TreeView _tree = new();
    private readonly ListBox _library = new();
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _zoom = new();
    private readonly TextBox _prompt = new() { AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap, FontSize = 16, Padding = new(12, 8, 12, 8) };
    private readonly StackPanel _properties = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    private VectorProject _project;
    private string? _path;
    private bool _dirty, _loading, _treeUpdating, _closing, _initialized, _applicationExiting;
    private string[] _selection = [];
    private string _treeSignature="";
    private readonly Func<bool> _insertOnCanvas;
    private readonly Action<bool> _saveInsert;
    private readonly string _author;
    private VectorGenerationOptions _options;
    private VectorSheet Active => _project.Sheets.First(x => x.Id == _project.ActiveSheetId);
    internal string ProjectId => _project.Id;
    private string T(string key) => _l("Vector.Editor." + key);
    internal VectorEditorWindow(Window owner, Func<string, string> localize, VectorProject project, string? path,
        string author, VectorGenerationOptions options, Func<bool> insertOnCanvas, Action<bool> saveInsert)
    {
        _l = localize; _project = project; _path = path; _author = author; _options = options;
        _insertOnCanvas = insertOnCanvas; _saveInsert = saveInsert;
        Resources.MergedDictionaries.Add(owner.Resources);
        Icon=owner.Icon;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VectorEditor/VectorEditorResources.xaml", UriKind.Relative) });
        FontSize=14;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Width = Math.Min(1560, SystemParameters.WorkArea.Width - 40); Height = Math.Min(1000, SystemParameters.WorkArea.Height - 40);
        MinWidth = Math.Min(900, Width); MinHeight = Math.Min(580, Height); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "Vector.Editor.Window");
        VirtualizingStackPanel.SetIsVirtualizing(_tree,true);
        VirtualizingStackPanel.SetVirtualizationMode(_tree,VirtualizationMode.Recycling);
        Content = Layout(); RefreshTabs(); RefreshLibrary(); UpdateTitle();
        if(VectorProjectRecovery.Available().Length>0)_status.Text=T("RecoveryAvailable");
        _browser.Message += BrowserMessage;
        Loaded += async (_, _) => { try { await _browser.InitializeAsync(); _initialized = true; await UpdateDrawing(); await LoadSheet(true); } catch (Exception e) { Error(e); } };
        Closing += OnClosing;
        Closed += (_, _) => { _autosave.Stop(); _browser.CloseView(); DisposeGeneration(); };
        _autosave.Tick += async (_, _) => { _autosave.Stop(); var snapshot=_project.Copy();try { await Task.Run(()=>VectorProjectRecovery.Save(snapshot));if(!_closing)_status.Text = T("Recovered"); } catch (Exception e) { Error(e); } };
        PreviewKeyDown += OnKey;
    }
    private void UpdateTitle() => Title = "ЛОПАТА — SVG · " + _project.Name + (_dirty ? " *" : "");
    private void Changed()
    {
        _dirty = true; _project.ModifiedUtc = DateTimeOffset.UtcNow; UpdateTitle();
        _autosave.Stop(); _autosave.Start();
    }
    private async Task LoadSheet(bool fit = false)
    {
        if (!_initialized) return;
        _loading = true;
        try { var s = Active; await _browser.LoadAsync(s.Svg, s.Id, s.Zoom, s.PanX, s.PanY, fit);
            if(TryFindResource("WindowBackgroundBrush") is SolidColorBrush brush)await _browser.CallAsync("theme",new{background=$"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"});
        }
        finally { _loading = false; }
    }
    private void BrowserMessage(JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("sheet", out var sheet) || sheet.GetString() != _project.ActiveSheetId) return;
            var kind = message.GetProperty("kind").GetString();
            if(kind=="viewport"){
                var zoom=message.GetProperty("zoom").GetDouble();var x=message.GetProperty("panX").GetDouble();var y=message.GetProperty("panY").GetDouble();
                if(!double.IsFinite(zoom)||zoom is < .01 or > 128||!double.IsFinite(x)||!double.IsFinite(y))return;
                var index=_project.Sheets.FindIndex(s=>s.Id==Active.Id);_project.Sheets[index]=Active with{Zoom=zoom,PanX=x,PanY=y};
                _zoom.Text=(zoom*100).ToString("0.#",CultureInfo.CurrentCulture)+"%";return;
            }
            if (kind is "state" or "commit")
            {
                _selection = message.GetProperty("selected").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var index = _project.Sheets.FindIndex(x => x.Id == Active.Id); var current = Active;
                if (kind == "commit")
                {
                    var svg = message.GetProperty("svg").GetString()!; _ = VectorSvg.Parse(svg);
                    if (svg != current.Svg) { _history.Record(_project); _project.Sheets[index] = current = current with { Svg = svg }; Changed(); }
                }
                var zoom = message.GetProperty("zoom").GetDouble(); var x = message.GetProperty("panX").GetDouble(); var y = message.GetProperty("panY").GetDouble();
                if (double.IsFinite(zoom) && zoom is >= .01 and <= 128 && double.IsFinite(x) && double.IsFinite(y))
                    _project.Sheets[index] = current with { Zoom = zoom, PanX = x, PanY = y };
                _zoom.Text = (zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
                var signature=message.GetProperty("nodes").GetRawText()+string.Join("",_selection);
                if(signature!=_treeSignature){_treeSignature=signature;RenderTree(message.GetProperty("nodes").Clone());} RenderProperties(message);
            }
            else if (kind == "shortcut") _ = Safe(() => Shortcut(message.GetProperty("command").GetString()!));
            else if (kind == "duplicate") _ = Safe(() => InsertSvgAsync(message.GetProperty("svg").GetString()!, 20, 20));
            else if (kind == "text") _ = Safe(async () => { var text = Ask(T("Text"), T("Text")); if (!string.IsNullOrWhiteSpace(text)) await Command("text", new { value = text, x = message.GetProperty("x").GetDouble(), y = message.GetProperty("y").GetDouble() }); });
            else if (kind == "error") _status.Text = T("Error." + message.GetProperty("key").GetString());
        }
        catch (Exception e) { Error(e); _ = Safe(() => LoadSheet()); }
    }
    private async Task Safe(Func<Task> action) { try { await action(); } catch (Exception e) { Error(e); } }
    private void Error(Exception e)
    {
        ComponentAuditFailures.Report(e);
        _status.Text = e.Message.StartsWith("Vector.", StringComparison.Ordinal) ? _l(e.Message) : T("Error.General") + " " + e.Message;
    }
    private async Task Command(string name, object? values = null)
    {
        var args = values is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(values))!;
        args["name"] = name; await _browser.CallAsync("command", args);
    }
    private Button Action(string key, string symbol, Func<Task> action)
    {
        var button = new Button { Content = symbol, ToolTip = T(key), Padding = new(10, 7, 10, 7), Margin = new(3), MinWidth = 36 };
        AutomationProperties.SetAutomationId(button, "Vector.Editor." + key); AutomationProperties.SetName(button, T(key));
        button.Click += async (_, _) => await Safe(action); return button;
    }
    private TextBlock Caption(string text, bool heading = false)
    {
        var b = new TextBlock { Text = text, FontSize = heading ? 17 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new(5, heading ? 12 : 5, 5, 5), TextWrapping = TextWrapping.Wrap };
        b.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return b;
    }
    private Border Panel(UIElement child, Thickness? margin = null)
    {
        var b = new Border { Child = child, CornerRadius = new(9), BorderThickness = new(1), Padding = new(8), Margin = margin ?? new(4) };
        b.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); b.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return b;
    }
    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var command = control ? e.Key switch { Key.S => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "saveAs" : "save", Key.Z => "undo", Key.Y => "redo", Key.C => "copy", Key.X => "cut", Key.V => "paste", Key.A => "all", _ => null } : e.Key == Key.Delete ? "delete" : null;
        if (command is null) return; e.Handled = true; _ = Safe(() => Shortcut(command));
    }
    private string? Ask(string title, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 250, Margin = new(12) };
        var yes = new Button { Content = T("Apply"), IsDefault = true, Margin = new(12), Padding = new(12, 6, 12, 6) };
        var stack = new StackPanel(); stack.Children.Add(input); stack.Children.Add(yes);
        var dialog = new Window { Owner = this, Title = title, Content = stack, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources.MergedDictionaries.Add(Resources); dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); yes.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { input.SelectAll(); input.Focus(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if(_applicationExiting){_closing=true;return;}
        if (_closing) return;
        if (_generationBusy) { e.Cancel = true; Hide(); _progressWindow?.Hide(); return; }
        if (!_dirty) { VectorProjectRecovery.Remove(_project.Id); return; }
        var result = System.Windows.MessageBox.Show(this, T("SaveBeforeClose"), T("Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (result == MessageBoxResult.Yes && !SaveProject(false)) { e.Cancel = true; return; }
        VectorProjectRecovery.Remove(_project.Id); _closing = true;
    }
    internal void CheckpointForExit()
    {
        if(_dirty||_generationBusy){_project.ModifiedUtc=DateTimeOffset.UtcNow;VectorProjectRecovery.Save(_project);}
    }
    internal void MarkRecovered(){_dirty=true;UpdateTitle();_status.Text=T("RecoveryRestored");}
    internal void CloseForApplicationExit()
    {
        CheckpointForExit();_applicationExiting=true;Close();
    }
}
