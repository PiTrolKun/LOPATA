using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace AIHub.Controls;

public sealed class LiteraryImportReviewWindow : Window
{
    public bool Changed { get; private set; }
    public LiteraryImportReviewWindow(Window owner, string rootPath, Func<string, string> l)
    {
        LiteraryPromptDialogUi.Configure(this, owner, l("Literary.Import.Review"));
        Width = 960; Height = 720; MinWidth = 620; MinHeight = 430;
        var store = new LiteraryChapterStore(rootPath); store.Open();
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(LiteraryUi.Text(l("Literary.Import.ReviewHint")));
        var choices = new ComboBox { Margin = new Thickness(0, 8, 0, 12), DisplayMemberPath = "FileName" }; top.Children.Add(choices);
        var status = LiteraryUi.Text(""); top.Children.Add(status);
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var editor = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18 };
        editor.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); root.Children.Add(editor);
        string loadedText = "";
        System.Windows.Controls.Button? approve = null;
        approve = LiteraryUi.Button(l("Literary.Import.ApprovePart"), () =>
        {
            if (choices.SelectedItem is not LiteraryChapterPart part) return;
            try
            {
                var current = LiteraryChapterFiles.Read(Path.Combine(rootPath, "chapters", part.FileName));
                if (current != loadedText) throw new IOException(l("Literary.Import.Changed"));
                ImportEligibility.ConfirmPart(rootPath, part.Id, current); Changed = true; Reload();
            }
            catch (Exception ex) { status.Text = ex.Message.StartsWith("Literary.") ? l(ex.Message) : ex.Message; }
        }, true);
        footer.Children.Add(LiteraryUi.Button(l("Literary.Import.ShowSource"), () =>
        {
            if (choices.SelectedItem is not LiteraryChapterPart part) return;
            try
            {
                var review = ImportEligibility.Review(rootPath, part.Id)!;
                var path = Path.Combine(rootPath, "Import", "sources.json");
                var sources = System.Text.Json.JsonSerializer.Deserialize<ImportUnit[]>(File.ReadAllText(path), ImportJson.Options)!;
                var ids = review.Doubts.Select(s => s.UnitId).ToHashSet();
                var text = string.Join("\n\n", sources.Where(u => ids.Contains(u.Id)).Select(u =>
                    string.Join("\n", review.Doubts.Where(s => s.UnitId == u.Id).Select(s => s.Reason).Distinct()) + "\n\n" + u.Text));
                var window = new Window(); LiteraryPromptDialogUi.Configure(window, this, l("Literary.Import.ShowSource"));
                window.Width = 850; window.Height = 650;
                var box = new System.Windows.Controls.TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18 };
                box.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                window.Content = box; window.ShowDialog();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }));
        footer.Children.Add(LiteraryUi.Button(l("Literary.Import.EditPart"), () =>
        {
            if (choices.SelectedItem is not LiteraryChapterPart part) return;
            var dialog=new Window(); LiteraryPromptDialogUi.Configure(dialog,this,l("Literary.Import.EditPart"));
            dialog.Width=880; dialog.Height=680; dialog.MinWidth=520; dialog.MinHeight=400;
            var panel=new DockPanel { Margin=new Thickness(18) }; dialog.Content=panel;
            var hint=LiteraryUi.Text(l("Literary.Import.EditHint")); DockPanel.SetDock(hint,Dock.Top); panel.Children.Add(hint);
            var bottom=new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); panel.Children.Add(bottom);
            var error=LiteraryUi.Text(""); bottom.Children.Add(error); var buttons=new WrapPanel(); bottom.Children.Add(buttons);
            var text=new System.Windows.Controls.TextBox { Text=loadedText, AcceptsReturn=true, AcceptsTab=true,
                TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, FontSize=18, UndoLimit=100 };
            LiterarySpellChecking.Enable(text, owner.Language.IetfLanguageTag);
            text.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush"); text.SetResourceReference(ForegroundProperty,"TextPrimaryBrush"); panel.Children.Add(text);
            buttons.Children.Add(LiteraryUi.Button(l("Literary.Import.RemoveSelected"),()=> { if(text.SelectionLength>0) text.SelectedText=""; }));
            buttons.Children.Add(LiteraryUi.Button(l("Literary.Import.SaveReviewed"),()=>
            {
                try { ImportReviewEdits.Apply(rootPath,part.Id,loadedText,text.Text); Changed=true; dialog.DialogResult=true; }
                catch(Exception ex) { error.Text=ex.Message.StartsWith("Literary.")?l(ex.Message):ex.Message; }
            },true));
            buttons.Children.Add(LiteraryUi.Button(l("Common.Cancel"),dialog.Close));
            if(dialog.ShowDialog()==true) Reload();
        }));
        footer.Children.Add(LiteraryUi.Button(l("Literary.Import.ExcludePart"),()=>
        {
            if(choices.SelectedItem is not LiteraryChapterPart part) return;
            if(System.Windows.MessageBox.Show(this,l("Literary.Import.ExcludeConfirm"),l("Literary.Import.ExcludePart"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
            try { ImportReviewEdits.Apply(rootPath,part.Id,loadedText,"",exclude:true); Changed=true; Reload(); }
            catch(Exception ex) { status.Text=ex.Message.StartsWith("Literary.")?l(ex.Message):ex.Message; }
        }));
        footer.Children.Add(approve); footer.Children.Add(LiteraryUi.Button(l("Common.Close"), Close));
        choices.SelectionChanged += (_, _) =>
        {
            editor.Document = new FlowDocument();
            if (choices.SelectedItem is not LiteraryChapterPart part) return;
            var text = loadedText = LiteraryChapterFiles.Read(Path.Combine(rootPath, "chapters", part.FileName));
            var review = ImportEligibility.Review(rootPath, part.Id)!;
            var spans = review.Doubts;
            if (review.Revision != LiteraryWorkIndex.Revision(text)) { spans = [new(0, text.Length, "", l("Literary.Import.Changed"))]; }
            var paragraph = new Paragraph(); editor.Document.Blocks.Add(paragraph); var start = 0;
            foreach (var span in spans.OrderBy(s => s.Start))
            {
                if (span.Start > start) paragraph.Inlines.Add(new Run(text[start..span.Start]));
                paragraph.Inlines.Add(new Run(text.Substring(span.Start, span.Length))
                    { Background = Brushes.LightGoldenrodYellow, Foreground = Brushes.DarkRed, ToolTip = span.Reason });
                start = span.Start + span.Length;
            }
            if (start < text.Length) paragraph.Inlines.Add(new Run(text[start..]));
        };
        void Reload()
        {
            choices.ItemsSource = store.Index.Parts.Where(p => ImportEligibility.Review(rootPath, p.Id)?.Doubts.Length > 0).ToArray();
            choices.SelectedIndex = 0; approve!.IsEnabled = choices.Items.Count > 0;
            status.Text = choices.Items.Count == 0 ? l("Literary.Import.NonePending") : "";
        }
        Reload();
    }
}
