using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.VideoPreparation;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void ReferenceChoice(string choice)
    {
        VideoReferences.Ensure(Session.Project).Choice = choice; Changed(); Checkpoint(); Render();
    }
    private void BuildReferences(StackPanel page)
    {
        var p = Session.Project; var r = VideoReferences.Ensure(p);
        if (r is { Choice: "create", Plan.Page: "prompts" or "ready" }) { BuildReferencePlan(page); return; }
        page.Children.Add(Text("RefQuestion"));
        var choices = new WrapPanel();
        foreach (var (key, value) in new[] { ("RefHaveFiles", "import"), ("RefNeedCreate", "create"), ("RefSkip", "skip") })
        {
            var button = Action(key, () => ReferenceChoice(value)); button.IsEnabled = !ConceptBusy && !PendingConcept;
            if (r.Choice == value) button.SetResourceReference(System.Windows.Controls.Button.StyleProperty, "SettingsPrimaryActionStyle");
            choices.Children.Add(button);
        }
        page.Children.Add(choices);
        if (r.Choice == "create") BuildReferencePlan(page);
        else if (r.Choice == "skip") page.Children.Add(Text("RefSkipHint"));
        if (r.Choice != "import") return;
        page.Children.Add(Text("RefImportHint"));
        var actions = new WrapPanel(); var add = Action("RefImport", () => _ = ChooseReferenceFilesAsync());
        add.IsEnabled = !ConceptBusy && !PendingConcept; actions.Children.Add(add);
        if (_referenceImporting) actions.Children.Add(Action("RefCancelImport", () => _referenceCancel?.Cancel()));
        page.Children.Add(actions);
        if (_referenceImportMessage.Length > 0)
        { var message = Text("RefImportHint"); message.Text = _referenceImportMessage; page.Children.Add(message); }
        if (_referenceImporting) return;
        if (r.Images.Count == 0) { page.Children.Add(Text("RefEmpty")); return; }
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var files = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };
        foreach (var image in r.Images)
        {
            var choose = Action("RefImage", () => { r.SelectedImageId = image.Id; Changed(); Checkpoint(); Render(); });
            choose.Content = (r.SelectedImageId == image.Id ? "● " : "") + image.Name;
            AutomationProperties.SetAutomationId(choose, "VideoPrep.RefImage." + image.Id);
            choose.IsEnabled = !_referenceImporting; files.Children.Add(choose);
        }
        body.Children.Add(new ScrollViewer { Content = files, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Top });
        var editor = new StackPanel(); Grid.SetColumn(editor, 1); body.Children.Add(editor); page.Children.Add(body);
        var selected = r.Images.FirstOrDefault(i => i.Id == r.SelectedImageId) ?? r.Images[0];
        var caption = Text("RefImage"); caption.Text = selected.Name + $" · {selected.Width} × {selected.Height}"; editor.Children.Add(caption);
        try
        {
            using var pixels = VideoReferenceFiles.Preview(VideoReferences.PathFor(p, selected));
            var preview = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null,
                pixels.GetPixels(), pixels.ByteCount, pixels.RowBytes); preview.Freeze();
            var image = new System.Windows.Controls.Image { Source = preview, Height = 280, Stretch = Stretch.Uniform };
            AutomationProperties.SetAutomationId(image, "VideoPrep.RefPreview"); AutomationProperties.SetName(image, selected.Name); editor.Children.Add(image);
        }
        catch (Exception error) when (IsStorageError(error)) { editor.Children.Add(Text("RefPreviewFailed")); }
        editor.Children.Add(Text("RefPreviewHint"));
        BuildReferenceBinding(editor, selected);
    }
    private void BuildReferenceBinding(StackPanel editor, VideoReferenceImage image)
    {
        var p = Session.Project; var r = p.References!;
        editor.Children.Add(Text("RefTarget"));
        var targets = new[] { new ReferenceTarget("", T("RefGeneral")) }.Concat((p.World?.Cards ?? [])
            .Select(c => new ReferenceTarget(c.Id, c.Name.Length == 0 ? T("WorldKind." + c.Kind) : c.Name))).ToArray();
        var target = new ComboBox { ItemsSource = targets, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = r.DraftCardId, MinHeight = 34 };
        AutomationProperties.SetAutomationId(target, "VideoPrep.RefTarget"); AutomationProperties.SetName(target, T("RefTarget"));
        target.SelectionChanged += (_, _) => { r.DraftCardId = (string?)target.SelectedValue ?? ""; Changed(); }; editor.Children.Add(target);
        var description = Text("RefCardDescription");
        string Describe()
        {
            var cards = (p.World?.Cards ?? []).Where(c => r.DraftCardId == "" || c.Id == r.DraftCardId);
            return string.Join("\n\n", cards.Select(c => (c.Name.Length == 0 ? T("WorldKind." + c.Kind) : c.Name)
                + "\n" + c.Text.IdeaText + (c.Fixed.Length == 0 ? "" : "\n" + T("WorldFixed") + ": " + c.Fixed)));
        }
        description.Text = Describe(); target.SelectionChanged += (_, _) => description.Text = Describe();
        var source = new Expander { Header = T("RefCardDescription"), Content = new ScrollViewer { Content = description,
            MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 6, 0, 6) };
        source.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); editor.Children.Add(source);
        editor.Children.Add(Text("RefPurpose"));
        var purpose = new ComboBox { ItemsSource = VideoReferences.Purposes.Select(k => new ReferenceTarget(k, T("RefPurpose." + k))).ToArray(),
            DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = r.DraftPurpose, MinHeight = 34 };
        AutomationProperties.SetAutomationId(purpose, "VideoPrep.RefPurpose"); AutomationProperties.SetName(purpose, T("RefPurpose"));
        purpose.SelectionChanged += (_, _) => { r.DraftPurpose = (string?)purpose.SelectedValue ?? "general"; Changed(); }; editor.Children.Add(purpose);
        editor.Children.Add(Text("RefNotes"));
        var notes = new ExampleTextInput(T("RefNotes"), T("RefNotesExample"), "VideoPrep.RefNotes", multiline: true);
        notes.Input.Height = 100; notes.Input.MaxLength = 10000; notes.Input.Text = r.DraftNotes; LiterarySpellChecking.Enable(notes.Input, InputLanguage);
        notes.Input.TextChanged += (_, _) => { r.DraftNotes = notes.Input.Text; Changed(); }; editor.Children.Add(notes);
        var bind = Action("RefBind", () =>
        {
            try { VideoReferences.Bind(p, image.Id, r.DraftCardId, r.DraftPurpose, r.DraftNotes); Changed(); Checkpoint(); Render(); }
            catch (Exception error) when (IsStorageError(error)) { ShowError(error); }
        }); bind.IsEnabled = !ConceptBusy && !PendingConcept; editor.Children.Add(bind);
        editor.Children.Add(Text("RefBindings"));
        foreach (var b in r.Bindings.Where(b => b.ImageId == image.Id).ToArray())
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            var label = Text("RefBindings"); label.Text = (b.CardId == "" ? T("RefGeneral") : b.CardName.Length == 0 ? T("RefUnnamedCard") : b.CardName)
                + " · " + T("RefPurpose." + b.Purpose) + (b.Notes.Length == 0 ? "" : "\n" + b.Notes);
            row.Children.Add(label);
            if (VideoReferences.NeedsReview(p, b))
            {
                row.Children.Add(Text("RefStale"));
                var review = Action("RefConfirm", () => { VideoReferences.Confirm(p, b); Changed(); Checkpoint(); Render(); });
                review.IsEnabled = !ConceptBusy && !PendingConcept && VideoReferences.Description(p, b.CardId) is not null; row.Children.Add(review);
            }
            var unlink = Action("RefUnlink", () => { r.Bindings.Remove(b); Changed(); Checkpoint(); Render(); });
            AutomationProperties.SetAutomationId(unlink, "VideoPrep.RefUnlink." + b.Id);
            unlink.IsEnabled = !ConceptBusy && !PendingConcept; row.Children.Add(unlink); editor.Children.Add(row);
        }
    }
    private sealed record ReferenceTarget(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
