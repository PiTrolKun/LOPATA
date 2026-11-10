using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TabControl = System.Windows.Controls.TabControl;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models.OneC;
using AIHub.Services.OneC;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private Task ShowDiagnosticAsync(OneCProposal proposal, OneCDiagnostic diagnostic)
    {
        ShowCheckWindow(proposal.SuggestedName, OneCProjectStore.CompleteCode(proposal), proposal.Check!, diagnostic.Line, diagnostic.Column);
        return Task.CompletedTask;
    }
    private Window Child(string title, double width = 1000, double height = 680)
    {
        var window = new Window { Title = title, Owner = _owner, Icon = _owner.Icon, Width = width, Height = height,
            MinWidth = 400, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Resources.MergedDictionaries.Add(_owner.Resources);
        window.Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OneC/OneCResources.xaml", UriKind.Relative) });
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        return window;
    }
    private void ShowCheckWindow(string name, string code, OneCCheck check, int line = 1, int column = 1)
    {
        var window = Child(T("CheckFile") + " · " + name); var body = new DockPanel { Margin = new(14) }; window.Content = body;
        var header = Label(T("PlatformNotChecked") + " · BSL Language Server " + check.Version); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var codeEditor = new OneCCodeEditor(_l, "OneC.CheckedCode") { ReadOnly = true }; codeEditor.SetText(code);
        var notes = new StackPanel();
        foreach (var d in check.Diagnostics)
        {
            var button = Button("GoTo", () => { codeEditor.Jump(d.Line, d.Column); return Task.CompletedTask; }, "CheckNote." + d.Id);
            button.Content = new TextBlock { Text = d.Line + ":" + d.Column + " · " + d.Message, TextWrapping = TextWrapping.Wrap };
            button.HorizontalContentAlignment = HorizontalAlignment.Left; notes.Children.Add(button);
        }
        notes.Children.Add(Label(CheckSummary(check)));
        var scroll = new ScrollViewer { Content = notes, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Bottom); body.Children.Add(scroll); body.Children.Add(codeEditor);
        window.Loaded += (_, _) => codeEditor.Jump(line, column); window.Show();
    }
    private string CheckSummary(OneCCheck check) => check.State == "failed" ? T("CheckerFailed")
        : check.Diagnostics.Length == 0 ? T("StaticClear") : T("StaticNotes") + " " + check.Diagnostics.Length;
    private Task ShowHelpAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", "OneC", "README." + UiLanguage + ".md");
        var window = Child(T("Help")); var body = new DockPanel { Margin = new(18) }; window.Content = body;
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        buttons.Children.Add(Button("DeveloperGuide", () =>
        {
            var guide = Path.Combine(AppContext.BaseDirectory, "Tools", "OneC", "DEVELOPMENT." + UiLanguage + ".md");
            Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true }); return Task.CompletedTask;
        }, "DeveloperGuide"));
        buttons.Children.Add(Button("SourceCode", () =>
        {
            var root = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(root, "Исходники", "AIHub", "Services", "OneC")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
            var source = Path.Combine(root, "Исходники", "AIHub", "Services", "OneC");
            var target = Directory.Exists(source) ? source : "https://github.com/PiTrolKun/LOPATA/tree/main/Исходники/AIHub/Services/OneC";
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return Task.CompletedTask;
        }, "SourceCode"));
        var text = new System.Windows.Controls.TextBox { Text = File.ReadAllText(path), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(8) };
        body.Children.Add(text); window.Show(); return Task.CompletedTask;
    }
}
