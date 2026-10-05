using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed class MusicPerformerEditorWindow : Window
{
    private readonly TextBox _name = new(), _notes = new();
    private readonly ComboBox _voice = new(), _range = new(), _language = new();
    private readonly HashSet<string> _timbres, _delivery;
    public MusicPerformer? Result { get; private set; }
    public MusicPerformerEditorWindow(Func<string, string> l, IReadOnlyList<MusicPerformer> others, MusicPerformer? current = null)
    {
        string L(string key) => l("Music.Wishes." + key);
        MusicWishUi.PrepareWindow(this, L(current is null ? "NewPerformer" : "Edit"), "Music.Performer.Editor");
        _timbres = new(current?.Timbres ?? [], StringComparer.Ordinal); _delivery = new(current?.Delivery ?? [], StringComparer.Ordinal);
        var dock = new DockPanel { Margin = new(18) }; var form = new StackPanel(); var error = MusicWishUi.Text("");
        error.Foreground = System.Windows.Media.Brushes.Red;
        var bottom = new StackPanel(); bottom.Children.Add(error);
        bottom.Children.Add(MusicWishUi.Footer(l, () =>
        {
            if (!MusicPreferences.ValidPerformerName(_name.Text, others, current?.Id)) { error.Text = L("NameError"); _name.Focus(); return; }
            Result = new(current?.Id ?? Guid.NewGuid().ToString("N"), _name.Text.Trim(), Value(_voice), Value(_range), Value(_language),
                _timbres.Order().ToArray(), _delivery.Order().ToArray(), _notes.Text.Trim()); DialogResult = true;
        }, () => DialogResult = false));
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        form.Children.Add(MusicWishUi.Text(L("Name"))); _name.Text = current?.Name ?? "";
        AutomationProperties.SetAutomationId(_name, "Music.Performer.Name"); form.Children.Add(_name);
        var choices = new WrapPanel();
        _voice = Choice("voice", "Voice", current?.Voice ?? "");
        _range = Choice("range", "Range", current?.Range ?? "");
        _language = Choice("language", "Language", current?.Language ?? ""); form.Children.Add(choices);
        var properties = new Grid(); properties.ColumnDefinitions.Add(new()); properties.ColumnDefinitions.Add(new());
        AddChecks("timbre", "Timbre", _timbres, 0); AddChecks("delivery", "DeliveryHeading", _delivery, 1); form.Children.Add(properties);
        form.Children.Add(MusicWishUi.Text(L("Notes"))); _notes.Text = current?.Notes ?? "";
        _notes.AcceptsReturn = true; _notes.TextWrapping = TextWrapping.Wrap; _notes.MinHeight = 90;
        AutomationProperties.SetAutomationId(_notes, "Music.Performer.Notes"); form.Children.Add(_notes);
        dock.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock;
        ComboBox Choice(string category, string label, string selected)
        {
            var panel = new StackPanel { Width = 195, Margin = new(0, 0, 8, 8) }; panel.Children.Add(MusicWishUi.Text(L(label)));
            var items = new[] { new Pick("", L("Auto")) }.Concat(MusicWishCatalog.Group(category).Select(x => new Pick(x.Id, MusicWishCatalog.Label(x, l)))).ToArray();
            var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = nameof(Pick.Label), SelectedValuePath = nameof(Pick.Id), SelectedValue = selected };
            AutomationProperties.SetAutomationId(combo, "Music.Performer." + label); panel.Children.Add(combo); choices.Children.Add(panel); return combo;
        }
        void AddChecks(string category, string label, HashSet<string> selected, int column)
        {
            var panel = new StackPanel { Margin = new(0, 5, 12, 5) }; panel.Children.Add(MusicWishUi.Text(L(label), true));
            foreach (var choice in MusicWishCatalog.Group(category))
                panel.Children.Add(MusicWishUi.Check(MusicWishCatalog.Label(choice, l), selected.Contains(choice.Id),
                    value => { if (value) selected.Add(choice.Id); else selected.Remove(choice.Id); }));
            Grid.SetColumn(panel, column); properties.Children.Add(panel);
        }
    }
    private static string Value(ComboBox box) => box.SelectedValue as string ?? "";
    private sealed record Pick(string Id, string Label);
}
