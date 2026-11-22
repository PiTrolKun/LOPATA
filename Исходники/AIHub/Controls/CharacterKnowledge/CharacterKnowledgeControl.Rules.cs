using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.CharacterKnowledge;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;

namespace AIHub.Controls.CharacterKnowledge;

internal sealed partial class CharacterKnowledgeControl
{
    private sealed class RuleEditor : DockPanel
    {
        private readonly CharacterKnowledgeControl _page;
        private readonly KnowledgeRule _rule;
        private readonly bool _editable;
        private readonly TreeView _tree = new() { MinHeight = 82 };
        private readonly TextBlock _stepLabel;
        private readonly TextBox _stepText;
        private readonly StackPanel _stepField = new() { Margin = new(0, 12, 0, 0) };
        private readonly WrapPanel _stepCommands;
        private readonly Button _moveUp, _moveDown, _delete;
        private bool _refreshing;
        private TreeViewItem? _target;

        internal TextBox ConditionBox { get; }
        private TreeViewItem? Selected => _tree.SelectedItem as TreeViewItem;
        private Node? Selection => Selected?.Tag as Node;

        internal RuleEditor(CharacterKnowledgeControl page, KnowledgeRule rule, bool editable)
        {
            _page = page;
            _rule = rule;
            _editable = editable;
            ConditionBox = _page.TextField("Condition", rule.Condition, editable, 60);
            ConditionBox.MaxHeight = 128;
            ConditionBox.TextChanged += (_, _) =>
            {
                if (!_editable) return;
                _rule.Condition = ConditionBox.Text;
                _page.DraftChanged();
            };
            var conditionField = _page.Labeled("If", ConditionBox);
            conditionField.Margin = new(0, 0, 0, 10);
            SetDock(conditionField, Dock.Top);
            Children.Add(conditionField);

            var additions = Row(_page.Command("AddAction", () => Add(KnowledgeStep.Action)),
                _page.Command("AddReaction", () => Add(KnowledgeStep.Reaction)),
                _page.Command("AddBranch", () => Add(KnowledgeStep.Branch)));
            additions.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            SetDock(additions, Dock.Top);
            Children.Add(additions);

            _moveUp = _page.Command("MoveUp", () => Move(-1));
            _moveDown = _page.Command("MoveDown", () => Move(1));
            _delete = _page.Command("DeleteStep", Remove);
            _stepCommands = Row(_moveUp, _moveDown, _delete);
            _stepCommands.Margin = new(0, 8, 0, 0);
            _stepLabel = Text("");
            _stepLabel.Margin = new(0, 0, 0, 5);
            _stepText = _page.TextField("StepText", "", editable, 68);
            _stepText.MaxHeight = 144;
            AutomationProperties.SetLabeledBy(_stepText, _stepLabel);
            _stepField.Children.Add(_stepLabel);
            _stepField.Children.Add(_stepText);
            _stepField.Children.Add(_stepCommands);
            SetDock(_stepField, Dock.Bottom);
            Children.Add(_stepField);

            Identify(_tree, "RuleTree");
            AutomationProperties.SetName(_tree, _page.T("Rules"));
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Auto);
            _tree.SelectedItemChanged += (_, _) => RefreshSelection();
            _stepText.TextChanged += (_, _) => ChangeStepText();
            Children.Add(_tree);
            RebuildTree(_rule.Then);
        }

        private void RebuildTree(object target)
        {
            _refreshing = true;
            try
            {
                _target = null;
                _tree.Items.Clear();
                var first = AddBranch(_tree, "Then", _rule.Then, target);
                AddBranch(_tree, "Else", _rule.Else, target);
                (_target ?? first).IsSelected = true;
            }
            finally { _refreshing = false; }
            RefreshSelection();
        }

        private TreeViewItem AddBranch(ItemsControl parent, string key, List<KnowledgeStep> steps, object target)
        {
            var branch = new TreeViewItem { Header = _page.T(key), IsExpanded = true, Tag = new Node(steps, null) };
            parent.Items.Add(branch);
            if (ReferenceEquals(steps, target)) _target = branch;
            foreach (var step in steps)
            {
                var item = new TreeViewItem { Header = Caption(step), IsExpanded = true, Tag = new Node(steps, step) };
                branch.Items.Add(item);
                if (ReferenceEquals(step, target)) _target = item;
                if (step.Kind == KnowledgeStep.Branch)
                {
                    AddBranch(item, "Then", step.Then!, target);
                    AddBranch(item, "Else", step.Else!, target);
                }
            }
            return branch;
        }

        private string Caption(KnowledgeStep step)
        {
            var label = _page.T(KindLabel(step));
            var text = Preview(step.Text ?? step.Condition ?? "");
            return text.Length > 0 ? label + " · " + text : label;
        }

        private void RefreshSelection()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var selection = Selection;
                var step = selection?.Step;
                _stepField.Visibility = step is null ? Visibility.Collapsed : Visibility.Visible;
                _stepCommands.Visibility = _editable ? Visibility.Visible : Visibility.Collapsed;
                if (step is null || selection is null) return;
                var index = selection.Steps.IndexOf(step);
                _stepLabel.Text = _page.T(KindLabel(step));
                _stepText.Text = step.Condition ?? step.Text ?? "";
                _moveUp.IsEnabled = index > 0;
                _moveDown.IsEnabled = index < selection.Steps.Count - 1;
            }
            finally { _refreshing = false; }
        }

        private void ChangeStepText()
        {
            if (_refreshing || !_editable || Selected is not { Tag: Node { Step: { } step } } item) return;
            if (step.Kind == KnowledgeStep.Branch) step.Condition = _stepText.Text;
            else step.Text = _stepText.Text;
            item.Header = Caption(step);
            _page.DraftChanged();
        }

        private void Add(string kind)
        {
            if (!_editable || Selection is not { } selection) return;
            var step = KnowledgeStep.Empty(kind);
            if (selection.Step is { } after) selection.Steps.Insert(selection.Steps.IndexOf(after) + 1, step);
            else selection.Steps.Add(step);
            RebuildTree(step);
            _page.DraftChanged();
            _stepText.Focus();
        }

        private void Move(int offset)
        {
            if (!_editable || Selection is not { Step: { } step } selection) return;
            var from = selection.Steps.IndexOf(step);
            var to = from + offset;
            if (to < 0 || to >= selection.Steps.Count) return;
            selection.Steps.RemoveAt(from);
            selection.Steps.Insert(to, step);
            RebuildTree(step);
            _page.DraftChanged();
        }

        private void Remove()
        {
            if (!_editable || Selection is not { Step: { } step } selection) return;
            selection.Steps.Remove(step);
            RebuildTree(selection.Steps);
            _page.DraftChanged();
        }

        private static string KindLabel(KnowledgeStep step) => step.Kind switch
        {
            KnowledgeStep.Action => "Action",
            KnowledgeStep.Reaction => "Reaction",
            _ => "If"
        };

        private sealed record Node(List<KnowledgeStep> Steps, KnowledgeStep? Step);
    }
}
