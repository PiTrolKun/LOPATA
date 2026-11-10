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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Models.OneC;
using AIHub.Services.OneC;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private void BuildResult(StackPanel body)
    {
        var p = Project!; var results = p.Proposals.Where(v => v.TaskId == p.ActiveTaskId).ToArray();
        if (results.Length == 0)
        {
            body.Children.Add(Label(T("NoProposal"), true));
            if (p.Pending is { Error.Length: > 0 } failed) body.Children.Add(Label(_l(failed.Error)));
            return;
        }
        var proposal = results.SingleOrDefault(v => v.Id == p.ActiveProposalId) ?? results[^1];
        p.ActiveProposalId = proposal.Id; _displayedProposal = proposal;
        var source = p.Files.SingleOrDefault(f => f.Id == proposal.Binding.FileId);
        body.Children.Add(new Separator { Margin = new(3, 10, 3, 10) });
        var relation = Label((source?.Name ?? T("NewCode")) + " → " + T("Task") + " №" + (p.Tasks.FindIndex(t => t.Id == proposal.TaskId) + 1)
            + " → " + T("Proposal") + " №" + (p.Proposals.IndexOf(proposal) + 1));
        relation.FontWeight = FontWeights.SemiBold; body.Children.Add(relation);
        if (proposal.Models.Length > 0) body.Children.Add(Label(string.Join(" · ", proposal.Models.Select(m => m.Name)), true));
        if (proposal.ParentProposalId.Length > 0)
            body.Children.Add(Label(T("RefinesProposal") + " №" + (p.Proposals.FindIndex(v => v.Id == proposal.ParentProposalId) + 1), true));
        var feedback = p.Task.Feedback.Where(f => f.ProposalId == proposal.Id).ToArray();
        if (feedback.Length > 0)
        {
            var messages = new StackPanel();
            foreach (var f in feedback) messages.Children.Add(Label(f.Created.ToLocalTime().ToString("g") + " · " + f.Error));
            body.Children.Add(new Expander { Header = T("UserReportedErrors"), Content = messages, Margin = new(3) });
        }
        _variants = Choices(results.Reverse().Select(v => new Choice(v.Id, T("Proposal") + " №" + (p.Proposals.IndexOf(v) + 1)
            + " · " + v.Created.ToLocalTime().ToString("g"))), proposal.Id, "VariantList");
        _variants.SelectionChanged += async (_, _) =>
        {
            if (_rendering || _variants.SelectedItem is not Choice choice || Project!.ActiveProposalId == choice.Id) return;
            if (!await SaveDraftAsync()) return;
            Project.ActiveProposalId = choice.Id; _proposalEditor = null; _displayedProposal = null;
            Dirty(); await SaveDraftAsync(); Render();
        };
        body.Children.Add(_variants);
        body.Children.Add(Label(proposal.Explanation));
        if (proposal.MissingContext.Length > 0) body.Children.Add(Label(T("MissingContext") + "\n" + string.Join("\n", proposal.MissingContext)));
        if (proposal.InsertLocation.Length > 0) body.Children.Add(Label(T("WhereToPaste") + ": " + proposal.InsertLocation, true));
        var compare = new WrapPanel(); body.Children.Add(compare);
        _compareToggle = new CheckBox { Content = T("Compare"), IsChecked = proposal.Binding.FileId.Length > 0, Margin = new(3, 8, 10, 6) };
        _diffLabel = Label(""); compare.Children.Add(_compareToggle); compare.Children.Add(_diffLabel);
        _comparison = new Grid { Margin = new(3), MinHeight = 220 };
        _comparison.ColumnDefinitions.Add(new()); _comparison.ColumnDefinitions.Add(new());
        _comparison.RowDefinitions.Add(new()); _comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var baseKey = EditorKey("base", proposal.Id);
        if (!_editors.TryGetValue(baseKey, out _baseEditor))
        {
            _baseEditor = new(_l, "OneC.SourceSnapshot." + proposal.Id) { ReadOnly = true };
            var binding = proposal.Binding; _baseEditor.SetText(binding.FullSource.Substring(binding.Start, binding.Length));
            _editors.Add(baseKey, _baseEditor);
        }
        _proposalEditor = ResultEditor(proposal);
        _comparison.Children.Add(CodeCard(source?.Name ?? T("Source"), T("ReadOnly"), _baseEditor));
        var next = CodeCard(T("Proposal"), T("EditableProposal"), _proposalEditor); Grid.SetColumn(next, 1); _comparison.Children.Add(next);
        _comparison.SizeChanged += (_, _) => ComparisonLayout();
        _compareToggle.Checked += (_, _) => ComparisonLayout(); _compareToggle.Unchecked += (_, _) => ComparisonLayout();
        body.Children.Add(_comparison); _bindingLabel = Label("", true); body.Children.Add(_bindingLabel);
        var actions = new WrapPanel(); body.Children.Add(actions);
        var copy = Button("CopyResult", () => CopyAsync(_proposalEditor?.Text ?? ""), "CopyResult"); copy.IsEnabled = proposal.Code.Length > 0; actions.Children.Add(copy);
        actions.Children.Add(Button("CopyExplanation", () => CopyAsync(proposal.Explanation), "CopyExplanation"));
        actions.Children.Add(Button("ExportResult", ExportResultAsync, "ExportResult", true));
        var create = Button("CreateResult", CreateResultAsync, "CreateResult", true); create.IsEnabled = proposal.Code.Length > 0; actions.Children.Add(create);
        if (source is not null && Store.CanEdit(p, source)) { _apply = Button("ApplyOwned", ApplyAsync, "Apply", true); actions.Children.Add(_apply); } else _apply = null;
        actions.Children.Add(Button("CheckProposal", CheckProposalAsync, "CheckProposal", true));
        actions.Children.Add(Button("ErrorInOneC", ErrorFeedbackAsync, "ErrorFeedback", true));
        _checkStatus = Label("", true); body.Children.Add(_checkStatus);
        _checkNotes = new StackPanel();
        body.Children.Add(new Expander { Header = T("ProjectNotes"), Content = _checkNotes, IsExpanded = proposal.Check?.Diagnostics.Length > 0, Margin = new(3) });
        RefreshCheckNotes();
        RefreshComparison();
    }
    private void RefreshCheckNotes()
    {
        if (_checkNotes is null || _displayedProposal is not { } proposal) return;
        _checkNotes.Children.Clear();
        if (proposal.Check is { } check)
            foreach (var diagnostic in check.Diagnostics)
            {
                var item = Button("GoTo", () => ShowDiagnosticAsync(proposal, diagnostic), "Diagnostic." + diagnostic.Id);
                item.Content = new TextBlock { Text = diagnostic.Severity + " · " + diagnostic.Line + ":" + diagnostic.Column + " · " + diagnostic.Message, TextWrapping = TextWrapping.Wrap };
                item.HorizontalContentAlignment = HorizontalAlignment.Left; _checkNotes.Children.Add(item);
            }
    }
    private FrameworkElement CodeCard(string title, string hint, OneCCodeEditor editor)
    {
        var panel = new DockPanel { Margin = new(7) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(Label(title)); top.Children.Add(Label(hint, true));
        editor.MinHeight = 180; editor.Height = 300; panel.Children.Add(editor);
        var border = new Border { Child = panel, BorderThickness = new(1), CornerRadius = new(5), Margin = new(0, 0, 6, 6) };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    private void ComparisonLayout()
    {
        if (_comparison is null || _comparison.Children.Count != 2) return;
        var first = (FrameworkElement)_comparison.Children[0]; var second = (FrameworkElement)_comparison.Children[1];
        var show = _compareToggle?.IsChecked == true;
        first.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var stacked = show && _comparison.ActualWidth is > 0 and < 680;
        Grid.SetRow(second, stacked ? 1 : 0); Grid.SetColumn(second, show && !stacked ? 1 : 0);
        Grid.SetColumnSpan(first, stacked ? 2 : 1); Grid.SetColumnSpan(second, show && !stacked ? 1 : 2);
    }
    private void RefreshComparison()
    {
        if (_displayedProposal is not { } proposal || _proposalEditor is null || _baseEditor is null || Project is null) return;
        var changes = OneCLineDiff.Compare(_baseEditor.Text, _proposalEditor.Text);
        _baseEditor.MarkLines(changes.Removed, false); _proposalEditor.MarkLines(changes.Added, true);
        if (_diffLabel is not null) _diffLabel.Text = "− " + changes.Removed.Length + " / + " + changes.Added.Length + " " + T("Lines");
        var stale = OneCProtocol.Stale(Project, proposal);
        if (_bindingLabel is not null) _bindingLabel.Text = (stale ? T("StaleProposal") + " · " : "")
            + T("Target") + ": " + (Project.Files.SingleOrDefault(f => f.Id == proposal.Binding.FileId)?.Name ?? T("NewCode"))
            + " · " + T(proposal.Binding.WholeFile ? "WholeFile" : "Selection")
            + " · " + T("Lines") + " " + (proposal.Binding.FullSource[..proposal.Binding.Start].Count(c => c == '\n') + 1)
            + "–" + (proposal.Binding.FullSource[..(proposal.Binding.Start + proposal.Binding.Length)].Count(c => c == '\n') + 1);
        if (_apply is not null) _apply.IsEnabled = !_busy && !stale && proposal.AppliedFileId.Length == 0 && proposal.MissingContext.Length == 0 && _proposalEditor.Text.Length > 0;
        var complete = proposal.Binding.WholeFile || proposal.Binding.FileId.Length == 0 ? _proposalEditor.Text
            : proposal.Binding.FullSource[..proposal.Binding.Start] + _proposalEditor.Text + proposal.Binding.FullSource[(proposal.Binding.Start + proposal.Binding.Length)..];
        if (_checkStatus is not null) _checkStatus.Text = T("PlatformNotChecked") + " · "
            + (proposal.Check is null ? T("NotChecked") : proposal.Check.CodeHash != OneCProjectStore.Hash(complete) ? T("CheckStale")
                : proposal.Check.State == "failed" ? T("CheckerFailed") : T(proposal.Check.Diagnostics.Length == 0 ? "StaticClear" : "StaticNotes") + " " + proposal.Check.Diagnostics.Length);
        ComparisonLayout();
    }
    private Task CopyAsync(string text)
    {
        if (!string.IsNullOrEmpty(text)) { System.Windows.Clipboard.SetText(text); _status.Text = T("Copied"); }
        return Task.CompletedTask;
    }
}
