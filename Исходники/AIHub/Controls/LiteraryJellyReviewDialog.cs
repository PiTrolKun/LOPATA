using System.Windows;
using System.Windows.Controls;
using System.IO;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

public sealed record LiteraryJellyReviewItem(LiteraryJellyFact Fact, string Number, string SourceText);

public static class LiteraryJellyReviewDialog
{
    public static bool Show(FrameworkElement owner, Func<string, string> l, IReadOnlyList<LiteraryJellyReviewItem> items,
        Func<IReadOnlyList<LiteraryJellyFact>, Task> save, Action<string, object>? diagnostic = null)
    {
        var root = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(LiteraryUi.Text(l("Literary.Jelly.ReviewHint")));
        var status = LiteraryUi.Text(""); header.Children.Add(status);
        var cancel = LiteraryUi.Button(l("Literary.Editor.Cancel"), () => { }); header.Children.Add(cancel);
        var list = new StackPanel(); var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var window = LiteraryEditorDialogs.Create(owner, l("Literary.Jelly.Title"), root);
        var inherited = window.Resources;
        window.Resources = new ResourceDictionary(); window.Resources.MergedDictionaries.Add(inherited);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        window.SizeToContent = SizeToContent.Manual;
        window.Width = Math.Min(920, window.MaxWidth); window.Height = Math.Min(760, window.MaxHeight); window.MinWidth = 520;
        var saving = false; cancel.IsCancel = true; cancel.Click += (_, _) => { if (!saving) window.DialogResult = false; };
        window.Closing += (_, e) => { if (saving) e.Cancel = true; };
        var readers = new List<Func<LiteraryJellyFact>>();
        var cards = new List<FrameworkElement>();
        foreach (var item in items)
        {
            var fact = item.Fact;
            var card = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
            var accepted = new CheckBox { Content = $"[{item.Number}] · " + l("Literary.Jelly.Include"), IsChecked = fact.Accepted, Margin = new Thickness(0, 6, 0, 8) };
            accepted.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); card.Children.Add(accepted);
            TextBox Field(string label, string text, int limit, bool multi = false)
            {
                card.Children.Add(LiteraryUi.Text(l(label)));
                var input = LiteraryWorkspaceParts.TextArea(false); input.Text = text; input.MaxLength = limit;
                input.AcceptsReturn = multi; input.Height = multi ? 74 : 42; input.MinHeight = 0; input.Margin = new Thickness(0, 0, 0, 7);
                input.Padding = new Thickness(8, 5, 8, 5);
                if (!multi) { input.TextWrapping = TextWrapping.NoWrap; input.VerticalContentAlignment = VerticalAlignment.Center; input.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; }
                card.Children.Add(input); return input;
            }
            var subject = Field("Literary.Jelly.Subject", fact.Subject, 200);
            var relation = Field("Literary.Jelly.Relation", fact.Relation, 300);
            var value = Field("Literary.Jelly.Value", fact.Value, 600, true);
            card.Children.Add(LiteraryUi.Text(l("Literary.Jelly.Kind")));
            var kind = new ComboBox { Margin = new Thickness(0, 0, 0, 7), MinHeight = 30 };
            foreach (var key in LiteraryJellyContract.Kinds) kind.Items.Add(new ComboBoxItem { Content = l("Literary.Jelly.Kind." + key), Tag = key });
            kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == fact.Kind); card.Children.Add(kind);
            var evidence = Field("Literary.Jelly.Evidence", fact.Evidence, 2000, true);
            var source = LiteraryUi.Button(l("Literary.Jelly.Source"), () =>
            {
                var input = LiteraryWorkspaceParts.TextArea(true); input.Text = item.SourceText;
                var sourceWindow = LiteraryEditorDialogs.Create(window, $"[{item.Number}]", input);
                sourceWindow.Width = Math.Min(780, sourceWindow.MaxWidth); sourceWindow.Height = Math.Min(650, sourceWindow.MaxHeight); sourceWindow.ShowDialog();
            }); card.Children.Add(source);
            var frame = LiteraryWorkspaceParts.Card(card); list.Children.Add(frame); cards.Add(frame);
            readers.Add(() => fact with { Subject = subject.Text.Trim(), Relation = relation.Text.Trim(), Value = value.Text.Trim(),
                Evidence = evidence.Text, Kind = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Accepted = accepted.IsChecked == true });
        }
        if (items.Count == 0) list.Children.Add(LiteraryUi.Text(l("Literary.Jelly.EmptyReview")));
        var confirm = LiteraryUi.Button(l("Literary.Jelly.Confirm"), async () =>
        {
            if (saving) return;
            var decisions = readers.Select(read => read()).ToArray();
            diagnostic?.Invoke("review_submit", decisions);
            try
            {
                for (var i = 0; i < decisions.Length; i++)
                {
                    try { LiteraryJellyContract.Validate(decisions[i], items[i].SourceText); }
                    catch (InvalidDataException)
                    {
                        status.Text = string.Format(l("Literary.Jelly.InvalidFact"), i + 1);
                        cards[i].BringIntoView(); diagnostic?.Invoke("review_validation_failed", new { number = i + 1 });
                        return;
                    }
                }
                saving = true; list.IsEnabled = false; cancel.IsEnabled = false; status.Text = l("Literary.Editor.Saving");
                await save(decisions);
                saving = false; window.DialogResult = true;
            }
            catch (Exception ex) { diagnostic?.Invoke("review_save_failed", new { type = ex.GetType().Name, ex.Message }); status.Text = l("Literary.Jelly.SaveError"); }
            finally { saving = false; list.IsEnabled = true; cancel.IsEnabled = true; }
        }, true);
        // Confirmation follows every fact in the scroll; it is never a hidden tab's action.
        list.Children.Add(confirm);
        return window.ShowDialog() == true;
    }
}
