using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub;

public partial class MainWindow
{
    private ComponentLicenseService? _componentLicenses;

    private void InitializeComponentLicenses()
    {
        _componentLicenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        ComponentLicenseGate.ConfirmAsync = async (ids, token) =>
        {
            var selected = ids.Where(x => x != "basic")
                .Concat(_componentLicenses.Entries.Where(x => x.Basic).Select(x => x.Id)).ToArray();
            try
            {
                await _componentLicenses.EnsureAsync(selected, async entries =>
                {
                    var separate = entries.Where(e => e.Id == "generation-krea").ToArray();
                    var common = entries.Except(separate).ToArray();
                    if (common.Length > 0 && !await Dispatcher.InvokeAsync(() => ShowComponentLicenses(common, true))) return false;
                    foreach (var entry in separate)
                        if (!await Dispatcher.InvokeAsync(() => ShowComponentLicenses([entry], true))) return false;
                    return true;
                }, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dispatcher.InvokeAsync(() => MessageBox.Show(this, L("licenses.save_error"), L("licenses.title")));
                throw new OperationCanceledException("Could not save component acknowledgement.", ex, token);
            }
        };
    }

    private void ComponentLicensesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_componentLicenses is not null) ShowComponentLicenses(_componentLicenses.Entries, false);
    }

    private bool ShowComponentLicenses(IReadOnlyList<ComponentLicenseEntry> entries, bool accept)
    {
        var russian = _localizationService.CurrentLanguageCode == "ru";
        string Describe(ComponentLicenseEntry entry)
        {
            var content = new StringBuilder();
            content.AppendLine(entry.Name + " — " + entry.License);
            content.AppendLine(L(entry.Delivery == "bundled" ? "licenses.bundled" : "licenses.download"));
            content.AppendLine(entry.Author);
            content.AppendLine(russian ? entry.Ru : entry.En);
            content.AppendLine(L("licenses.checked") + " " + entry.Checked);
            content.AppendLine(entry.Source).AppendLine();
            foreach (var file in entry.Texts)
                content.AppendLine(_componentLicenses!.ReadText(file)).AppendLine();
            if (entry.Texts.Length == 0) content.AppendLine(L("licenses.source_only"));
            return content.ToString();
        }
        var window = new Window { Owner = this, Title = L("licenses.title"), Width = 820, Height = 620,
            MinWidth = 500, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        if (accept)
        {
            var yes = new Button { Content = L("licenses.accept"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(5) };
            yes.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(yes);
        }
        var close = new Button { Content = L(accept ? "licenses.cancel" : "licenses.close"), IsCancel = true,
            Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(5) };
        close.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var intro = new TextBlock { Text = L("licenses.explanation"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);
        var source = new Button { Content = L("licenses.open_source"), Margin = new Thickness(4), Padding = new Thickness(8) };
        buttons.Children.Insert(0, source);
        var list = new ListBox { ItemsSource = entries, DisplayMemberPath = nameof(ComponentLicenseEntry.Name),
            Width = 230, Margin = new Thickness(0,0,10,0) };
        DockPanel.SetDock(list, Dock.Left);
        panel.Children.Add(list);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(details);
        list.SelectionChanged += (_, _) => {
            if (list.SelectedItem is ComponentLicenseEntry entry) details.Text = Describe(entry);
        };
        source.Click += (_, _) => {
            if (list.SelectedItem is ComponentLicenseEntry entry && Uri.TryCreate(entry.Source, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        list.SelectedIndex = 0;
        window.Content = panel;
        return window.ShowDialog() == true;
    }
}
