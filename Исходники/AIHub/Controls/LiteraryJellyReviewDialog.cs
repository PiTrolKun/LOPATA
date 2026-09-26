using System.Windows;
using System.Windows.Controls;
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
    private const int PageSize = 25;
    public static bool Show(FrameworkElement owner, Func<string, string> l, IReadOnlyList<LiteraryJellyReviewItem> items,
        Func<IReadOnlyList<LiteraryJellyFact>, Task> save, Action<string, object>? diagnostic = null,
        Action<string,string>? quote = null, string language = "ru", string summary = "",
        Func<IReadOnlyList<LiteraryJellyFact>, Task>? acceptWarnings = null,
        Action<IReadOnlyList<LiteraryJellyFact>>? saveDraft = null)
    {
        var root = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(LiteraryUi.Text(l("Literary.Jelly.ReviewHint")));
        if(summary.Length>0) header.Children.Add(LiteraryUi.Text(summary));
        var status = LiteraryUi.Text(""); header.Children.Add(status);
        var cancel = LiteraryUi.Button(l("Literary.Editor.Cancel"), () => { }); header.Children.Add(cancel);
        var pages = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) }; header.Children.Add(pages);
        var pageSelector = new ComboBox { MinHeight = 32, MinWidth = 260, Margin = new Thickness(8, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(pageSelector, "Literary.Jelly.Page");
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
        var validation = new List<LiteraryJellyReviewValidation>();
        var invalidRows = new List<int>();
        // Drafts outlive the controls; changing pages must never discard edits or decisions.
        var drafts = items.Select(item => item.Fact with { }).ToArray();
        var page = 0;
        var pageCount = Math.Max(1, (items.Count + PageSize - 1) / PageSize);
        var rendering = false;
        var previousPage = LiteraryUi.Button(l("Literary.Jelly.PreviousPage"), () => { });
        var nextPageButton = LiteraryUi.Button(l("Literary.Jelly.NextPage"), () => { });
        void CapturePage()
        {
            for (var i = 0; i < readers.Count; i++) drafts[page * PageSize + i] = readers[i]();
        }
        var draftTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        bool SaveDraft()
        {
            draftTimer.Stop();
            if (saveDraft is null) return true;
            CapturePage();
            try { saveDraft(drafts.Select(fact => fact with { }).ToArray()); return true; }
            catch (Exception) { status.Text = l("Literary.Jelly.SaveError"); return false; }
        }
        draftTimer.Tick += (_, _) => SaveDraft();
        window.Closing += (_, e) => { if (!saving && !SaveDraft()) e.Cancel = true; };
        window.Closed += (_, _) => draftTimer.Stop();
        Control? RefreshValidation(bool scheduleDraft = true)
        {
            if (rendering) return null;
            if (scheduleDraft && saveDraft is not null) { draftTimer.Stop(); draftTimer.Start(); }
            CapturePage();
            invalidRows.Clear();
            Control? first = null;
            for (var i = 0; i < readers.Count; i++)
            {
                var index = page * PageSize + i;
                var issues = LiteraryJellyValidation.Check(drafts[index], items[index].SourceText);
                var input = validation[i].Show(issues);
                if (input is null) continue;
                invalidRows.Add(index + 1); first ??= input;
            }
            status.Text = invalidRows.Count == 0 ? ""
                : string.Format(l("Literary.Jelly.InvalidFact"), string.Join(", ", invalidRows));
            return first;
        }
        void RenderPage(int nextPage)
        {
            CapturePage();
            rendering = true;
            page = Math.Clamp(nextPage, 0, pageCount - 1);
            readers.Clear(); validation.Clear(); list.Children.Clear();
            pageSelector.SelectedIndex = page;
            previousPage.IsEnabled = page > 0;
            nextPageButton.IsEnabled = page + 1 < pageCount;
            for (var itemIndex = page * PageSize; itemIndex < Math.Min(items.Count, (page + 1) * PageSize); itemIndex++)
            {
                var item = items[itemIndex];
                var fact = drafts[itemIndex];
                var card = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
                var fieldValidation = new LiteraryJellyReviewValidation(window, l);
                var accepted = new CheckBox { Content = $"[{item.Number}] · " + l("Literary.Jelly.Include"), IsChecked = fact.Accepted, Margin = new Thickness(0, 6, 0, 8) };
                accepted.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); card.Children.Add(accepted);
                TextBox Field(LiteraryJellyField field, string label, string text, int limit, bool multi = false)
                {
                    card.Children.Add(LiteraryUi.Text(l(label)));
                    var input = LiteraryWorkspaceParts.TextArea(false); input.Text = text; input.MaxLength = limit;
                    input.AcceptsReturn = multi; input.Height = multi ? 74 : 42; input.MinHeight = 0; input.Margin = new Thickness(0, 0, 0, 7);
                    input.Padding = new Thickness(8, 5, 8, 5);
                    System.Windows.Automation.AutomationProperties.SetAutomationId(input, $"Literary.Jelly.Fact.{itemIndex}.{field}");
                    LiterarySpellChecking.Enable(input,language);
                    if (!multi) { input.TextWrapping = TextWrapping.NoWrap; input.VerticalContentAlignment = VerticalAlignment.Center; input.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; }
                    card.Children.Add(input); fieldValidation.Add(card, field, input); return input;
                }
                var subject = Field(LiteraryJellyField.Subject, "Literary.Jelly.Subject", fact.Subject, 200);
                var relation = Field(LiteraryJellyField.Relation, "Literary.Jelly.Relation", fact.Relation, 300);
                var value = Field(LiteraryJellyField.Value, "Literary.Jelly.Value", fact.Value, 600, true);
                card.Children.Add(LiteraryUi.Text(l("Literary.Jelly.Kind")));
                var kind = new ComboBox { Margin = new Thickness(0, 0, 0, 7), MinHeight = 30 };
                foreach (var key in LiteraryJellyContract.Kinds) kind.Items.Add(new ComboBoxItem { Content = l("Literary.Jelly.Kind." + key), Tag = key });
                if (acceptWarnings is not null && !LiteraryJellyContract.Kinds.Contains(fact.Kind))
                    kind.Items.Add(new ComboBoxItem { Content = fact.Kind, Tag = fact.Kind });
                kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == fact.Kind); card.Children.Add(kind);
                fieldValidation.Add(card, LiteraryJellyField.Kind, kind);
                var evidence = Field(LiteraryJellyField.Evidence, "Literary.Jelly.Evidence", fact.Evidence, 2000, true);
                var source = LiteraryUi.Button(l("Literary.Jelly.Source"), () =>
                {
                    var input = LiteraryWorkspaceParts.TextArea(true); input.Text = item.SourceText;
                    var panel=new DockPanel();
                    var use=LiteraryUi.Button(l("Literary.Jelly.UseQuote"),()=> { if(input.SelectionLength>0) evidence.Text=input.SelectedText; });
                    DockPanel.SetDock(use,Dock.Bottom); panel.Children.Add(use); panel.Children.Add(input);
                    var sourceWindow = LiteraryEditorDialogs.Create(window, $"[{item.Number}]", panel);
                    sourceWindow.Width = Math.Min(780, sourceWindow.MaxWidth); sourceWindow.Height = Math.Min(650, sourceWindow.MaxHeight); sourceWindow.ShowDialog();
                }); card.Children.Add(source);
                if (quote is not null)
                    card.Children.Add(LiteraryUi.Button(l("Studio.Quote"),()=>
                    {
                        var text = new[]{subject,relation,value,evidence}.FirstOrDefault(f=>f.SelectionLength>0)?.SelectedText;
                        quote(l("Studio.Jelly")+" · ["+item.Number+"]",text ?? subject.Text+" — "+relation.Text+" — "+value.Text);
                        status.Text=l("Studio.QuoteAttached");
                    }));
                var frame = LiteraryWorkspaceParts.Card(card); list.Children.Add(frame);
                readers.Add(() => fact with { Subject = subject.Text.Trim(), Relation = relation.Text.Trim(), Value = value.Text.Trim(),
                    Evidence = evidence.Text, Kind = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Accepted = accepted.IsChecked == true });
                validation.Add(fieldValidation);
                foreach (var input in new[] { subject, relation, value, evidence })
                    input.TextChanged += (_, _) => RefreshValidation();
                kind.SelectionChanged += (_, _) => RefreshValidation();
                accepted.Checked += (_, _) => RefreshValidation();
                accepted.Unchecked += (_, _) => RefreshValidation();
            }
            if (items.Count == 0) list.Children.Add(LiteraryUi.Text(l("Literary.Jelly.EmptyReview")));
            rendering = false;
            RefreshValidation(false);
            scroll.ScrollToTop();
        }
        previousPage.Click += (_, _) => RenderPage(page - 1);
        nextPageButton.Click += (_, _) => RenderPage(page + 1);
        pages.Children.Add(previousPage); pages.Children.Add(pageSelector); pages.Children.Add(nextPageButton);
        for (var i = 0; i < pageCount; i++)
            pageSelector.Items.Add(string.Format(l("Literary.Jelly.ReviewPage"), i + 1, pageCount,
                items.Count == 0 ? 0 : i * PageSize + 1, Math.Min(items.Count, (i + 1) * PageSize), items.Count));
        pageSelector.SelectionChanged += (_, _) =>
        {
            if (!rendering && !saving && pageSelector.SelectedIndex >= 0) RenderPage(pageSelector.SelectedIndex);
        };
        async Task Submit(bool ignoreWarnings)
        {
            if (saving) return;
            CapturePage();
            var decisions = drafts.Select(fact => fact with { }).ToArray();
            diagnostic?.Invoke("review_submit", decisions);
            try
            {
                // Submission checks every page, including pages the user has not opened.
                var allInvalid = Enumerable.Range(0, decisions.Length)
                    .Where(i => LiteraryJellyValidation.Check(decisions[i], items[i].SourceText).Count > 0).ToArray();
                if (allInvalid.Length > 0 && !ignoreWarnings)
                {
                    diagnostic?.Invoke("review_validation_failed", new { number = allInvalid[0] + 1, numbers = allInvalid.Select(i => i + 1).ToArray() });
                    RenderPage(allInvalid[0] / PageSize);
                    var invalidInput = RefreshValidation(false)!;
                    var firstValidation = validation[allInvalid[0] % PageSize];
                    await window.Dispatcher.InvokeAsync(
                        () => { if (window.IsVisible) { invalidInput.Focus(); firstValidation.Reveal(invalidInput); } },
                        System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }
                saving = true; list.IsEnabled = false; pages.IsEnabled = false; cancel.IsEnabled = false; status.Text = l("Literary.Editor.Saving");
                if (!SaveDraft()) return;
                await (ignoreWarnings ? acceptWarnings! : save)(decisions);
                saving = false; window.DialogResult = true;
            }
            catch (Exception ex) { diagnostic?.Invoke("review_save_failed", new { type = ex.GetType().Name, ex.Message }); status.Text = l("Literary.Jelly.SaveError"); }
            finally { saving = false; list.IsEnabled = true; pages.IsEnabled = true; cancel.IsEnabled = true; }
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Insert(root.Children.Count - 1, actions);
        var confirm = LiteraryUi.Button(l("Literary.Jelly.Confirm"), async () => await Submit(false), true);
        actions.Children.Add(confirm);
        if (acceptWarnings is not null)
            actions.Children.Add(LiteraryUi.Button(l("Literary.Import.Jelly.AcceptWarnings"), async () => await Submit(true)));
        RenderPage(0);
        return window.ShowDialog() == true;
    }
}
