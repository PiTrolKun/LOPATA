using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class LiteraryInterviewControl
{
    private void BuildMandatory()
    {
        var q = LiteraryInterviewCatalog.Get(S.Step);
        _body.Children.Add(LiteraryUi.Text(_l(q.Key),true));
        var previous = S.Records.FirstOrDefault(r => r.Step == S.Step && !r.Adaptive);
        if (previous is not null)
        {
            _body.Children.Add(LiteraryUi.Text(previous.Text));
            _body.Children.Add(LiteraryUi.Text(T("ReadOnly")));
            _next = LiteraryUi.Button(T("Next"), () => { _session.Advance(); Save(); Render(); });
            _body.Children.Add(_next); return;
        }
        var value = S.Inputs.GetValueOrDefault(S.Step, "");
        if (q.Input == InterviewInput.Route) BuildRoute();
        else if (q.Input == InterviewInput.Materials) BuildMaterials();
        else
        {
            if (q.Input is InterviewInput.Memory or InterviewInput.Form or InterviewInput.Basis) BuildChoice(q);
            if (q.Input == InterviewInput.Genres) BuildGenres();
            if (q.Input is not (InterviewInput.Memory or InterviewInput.Form or InterviewInput.Basis))
            {
                if (q.Input == InterviewInput.Folder && value.Length == 0) value = S.Parent;
                var step = S.Step;
                var input = Input(value, text => S.Inputs[step] = text, q.Creative || q.Input == InterviewInput.Genres);
                if (q.Input is InterviewInput.Folder or InterviewInput.Name && _session.Root is not null) input.IsReadOnly = true;
                _body.Children.Add(input);
                if (q.Input == InterviewInput.Folder) _body.Children.Add(LiteraryUi.Button(_l("Literary.Create.Browse"), () =>
                {
                    if (_session.Root is not null) return;
                    var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = S.Parent };
                    if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName;
                }));
            }
            var options = new WrapPanel();
            foreach (var option in q.Options ?? []) options.Children.Add(LiteraryUi.Button(T("Option." + option), () => Execute(() => AcceptDirectAsync(T("Option." + option), option == "NoSource" ? "not_applicable" : "statement"))));
            if (q.Creative || q.Input == InterviewInput.Genres) options.Children.Add(LiteraryUi.Button(T("Unknown"), () => Execute(() => AcceptDirectAsync(T("Unknown"),"unknown"))));
            _body.Children.Add(options);
        }
        _next = LiteraryUi.Button(T("Next"), () => Execute(SubmitAsync), true);
        _next.Margin = new Thickness(0,16,0,0); _body.Children.Add(_next);
    }
    private void BuildChoice(InterviewQuestion q)
    {
        var ids = q.Input == InterviewInput.Memory ? new[] { "runeweaver","gliner","nuextract" }
            : q.Input == InterviewInput.Form ? LiteraryChoices.Forms : new[] { "original","existing" };
        var prefix = q.Input == InterviewInput.Memory ? "Literary.MemorySetup." : q.Input == InterviewInput.Form ? "Literary.Form." : "Literary.Interview.Basis.";
        var fallback = q.Input == InterviewInput.Memory ? "runeweaver" : q.Input == InterviewInput.Form ? "free" : "original";
        var choice = new ComboBox { MinHeight = 36, Margin = new Thickness(0,12,0,12) };
        foreach (var id in ids) choice.Items.Add(new ComboBoxItem { Content = _l(prefix + id), Tag = id });
        choice.SelectedIndex = Array.IndexOf(ids, S.Selections.GetValueOrDefault(q.Number, fallback));
        if (choice.SelectedIndex < 0) choice.SelectedIndex = 0;
        var hint = LiteraryUi.Text("");
        void Selected()
        {
            var selected = (ComboBoxItem)choice.SelectedItem;
            S.Selections[q.Number] = (string)selected.Tag; S.Inputs[q.Number] = selected.Content.ToString()!;
            hint.Text = q.Input == InterviewInput.Memory ? _l(prefix + selected.Tag + ".Hint") : "";
        }
        Selected(); choice.SelectionChanged += (_, _) => { Selected(); Change(); };
        _body.Children.Add(choice); _body.Children.Add(hint);
    }
    private void BuildGenres()
    {
        var panel = new WrapPanel();
        var selected = S.Selections.GetValueOrDefault(6, "").Split('|',StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var id in LiteraryInterviewCatalog.Genres)
        {
            var box = new CheckBox { Content = T("Genre." + id), ToolTip = T("GenreHint." + id), IsChecked = selected.Contains(id), Margin = new Thickness(4,6,18,6) };
            box.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
            box.Checked += (_, _) => { selected.Add(id); S.Selections[6] = string.Join('|',selected); Change(); };
            box.Unchecked += (_, _) => { selected.Remove(id); S.Selections[6] = string.Join('|',selected); Change(); };
            panel.Children.Add(box);
        }
        _body.Children.Add(panel); _body.Children.Add(LiteraryUi.Text(T("Custom")));
    }
    private void BuildRoute()
    {
        var table = new StackPanel();
        table.Children.Add(LiteraryUi.Text(T("RouteHint")));
        foreach (var row in S.Route)
        {
            var line = new Grid { Margin = new Thickness(4) };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(36) });
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = new GridLength(2,GridUnitType.Star) });
            line.Children.Add(LiteraryUi.Text(row.Number.ToString()));
            var name = Input(row.Title,t => row.Title=t,false); name.ToolTip = T("RouteTitle");
            var description = Input(row.Description,t => row.Description=t); description.ToolTip = T("RouteDescription");
            name.Margin = new Thickness(4); description.Margin = new Thickness(4);
            Grid.SetColumn(name,1); Grid.SetColumn(description,2); line.Children.Add(name); line.Children.Add(description);
            var panel = new StackPanel();
            if (row.Number <= 2) panel.Children.Add(LiteraryUi.Text(T("Required")));
            panel.Children.Add(line);
            var border = new Border { Child = panel, BorderThickness = new Thickness(1), Margin = new Thickness(0,4,0,4), Padding = new Thickness(6), CornerRadius = new CornerRadius(6) };
            border.SetResourceReference(Border.BorderBrushProperty,row.Number <= 2 ? "AccentBrush" : "LineBrush"); table.Children.Add(border);
        }
        _body.Children.Add(table);
    }
    private bool ValidInput()
    {
        if (S.Stage != InterviewStage.Mandatory) return true;
        if (S.Records.Any(r => r.Step == S.Step && !r.Adaptive)) return true;
        var input = S.Inputs.GetValueOrDefault(S.Step,"");
        return LiteraryInterviewCatalog.Get(S.Step).Input switch
        {
            InterviewInput.Folder => Directory.Exists(input.Length == 0 ? S.Parent : input) && Path.IsPathFullyQualified(input.Length == 0 ? S.Parent : input),
            InterviewInput.Name => LiteraryProjectStore.IsValidProjectName(input),
            InterviewInput.Route => S.Route.Take(2).All(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Description)),
            InterviewInput.Materials or InterviewInput.Author or InterviewInput.Title => true,
            InterviewInput.Genres => S.Selections.GetValueOrDefault(6,"").Length > 0 || !string.IsNullOrWhiteSpace(input),
            _ => !string.IsNullOrWhiteSpace(input)
        };
    }
    private void UpdateNext()
    {
        if (_next is null) return;
        _next.IsEnabled = !_busy && ValidInput();
        _next.ToolTip = _next.IsEnabled ? null : T("RequiredHint");
    }
    private void BuildUnderstanding()
    {
        if (S.ConfirmationReturnStep is not null) _body.Children.Add(LiteraryUi.Text(T("RecoveredAnswer")));
        _body.Children.Add(LiteraryUi.Text(T("Understood"),true));
        _body.Children.Add(LiteraryUi.Text(S.Pending));
        _body.Children.Add(new Expander { Header = LiteraryUi.Text(T("OriginalAnswer")), Content = LiteraryUi.Text(S.AdaptiveAnswer ? S.AdaptiveInput : S.Inputs.GetValueOrDefault(S.Step,"")) });
        if (S.Stage == InterviewStage.Correction)
        {
            _body.Children.Add(Input(S.Correction,t => S.Correction=t));
            _body.Children.Add(LiteraryUi.Button(T("Next"), () => Execute(async () =>
            {
                if (string.IsNullOrWhiteSpace(S.Correction)) return;
                if (S.AiDisabled) _session.Propose(S.Correction,S.AdaptiveAnswer);
                else await InferAsync(false);
                Save();
            }),true));
        }
        else
        {
            _body.Children.Add(LiteraryUi.Button(T("Correct"), () => Execute(async () =>
            {
                _session.Confirm(CurrentQuestion(),S.AdaptiveAnswer ? S.AdaptiveInput : S.Inputs.GetValueOrDefault(S.Step,""));
                if (Save()) await NextAdaptiveAsync();
            }),true));
            _body.Children.Add(LiteraryUi.Button(T("Clarify"), () => { S.Stage=InterviewStage.Correction; Save(); Render(); }));
        }
    }
    private void BuildAdaptive()
    {
        _body.Children.Add(LiteraryUi.Text(T("Adaptive"),true));
        if (S.AdaptiveQuestion.Length == 0)
            _body.Children.Add(LiteraryUi.Button(T("NextQuestion"), () => Execute(() => InferAsync(true)),true));
        else
        {
            _body.Children.Add(LiteraryUi.Text(S.AdaptiveQuestion,true));
            _body.Children.Add(Input(S.AdaptiveInput,t => S.AdaptiveInput=t));
            _body.Children.Add(LiteraryUi.Button(T("Next"), () => Execute(async () =>
            { if (string.IsNullOrWhiteSpace(S.AdaptiveInput)) return; S.AdaptiveAnswer=true; await InferAsync(false); }),true));
        }
        _body.Children.Add(LiteraryUi.Button(T("EndTopic"), () => { _session.EndTopic(); Save(); Render(); }));
    }
    private void BuildReview()
    {
        _body.Children.Add(LiteraryUi.Text(T("Review"),true));
        if (!_session.Complete) _body.Children.Add(LiteraryUi.Text(T("MissingSteps")));
        S.InitialPositive ??= string.Join("\n",S.Records.Where(r => r.Step is 31 or 33 or 34).Select(r => r.Text));
        S.InitialNegative ??= string.Join("\n",S.Records.Where(r => r.Step == 32).Select(r => r.Text));
        _body.Children.Add(LiteraryUi.Text(_l("Literary.Anchor.Positive")));
        _body.Children.Add(Input(S.InitialPositive,text => S.InitialPositive=text));
        _body.Children.Add(LiteraryUi.Text(_l("Literary.Anchor.Negative")));
        _body.Children.Add(Input(S.InitialNegative,text => S.InitialNegative=text));
        _body.Children.Add(LiteraryUi.Text(_l("Literary.MemorySetup.AnchorsHint")));
        foreach (var r in S.Records) _body.Children.Add(LiteraryUi.Text(r.Question + "\n" + r.Text));
        _body.Children.Add(LiteraryUi.Button(_l("Literary.Create.Save"), _session.Complete ? () => Execute(CreateAsync) : null,true));
    }
}
