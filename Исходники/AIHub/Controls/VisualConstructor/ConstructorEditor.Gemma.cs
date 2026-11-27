using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly Button _suggestActions = new();
    private readonly Button _writeCode = new();
    private readonly Button _prepareGemma = new();
    private readonly Button _undoSuggestion = new();
    private readonly WrapPanel _gemmaCommands = new() { Margin = new Thickness(0, 0, 6, 0) };
    private (string Project, ConstructorEventSite Site, string[] Steps)? _lastSuggestion;
    private bool _shareGemmaProjectSummary;

    private void AddGemmaTools()
    {
        AutomationProperties.SetAutomationId(_gemmaCommands, "VisualConstructor.GemmaCommands");
        Add(_suggestActions, "SuggestActions", () => AskGemmaAsync(false));
        Add(_writeCode, "WriteCode", () => AskGemmaAsync(true));
        Add(_prepareGemma, "PrepareGemma", PrepareGemmaAsync);
        Add(_undoSuggestion, "UndoSuggestion", () => { UndoSuggestion(); return Task.CompletedTask; });

        void Add(Button button, string key, Func<Task> command)
        {
            Configure(button, key);
            button.Margin = new Thickness(0, 0, 5, 3);
            button.Click += async (_, _) =>
            {
                try { await command(); }
                catch (BackgroundOperationWaitingException) { _state.Text = T("GemmaWaiting"); }
                catch (Exception error) { ShowError(error); }
            };
            _gemmaCommands.Children.Add(button);
        }
    }

    private void ConfigureGemmaLabels()
    {
        SetCommandLabel(_suggestActions, "SuggestActions");
        SetCommandLabel(_writeCode, "WriteCode");
        _writeCode.ToolTip = T("WriteCodeHint");
        SetCommandLabel(_prepareGemma, "PrepareGemma");
        SetCommandLabel(_undoSuggestion, "UndoSuggestion");
    }

    private void RefreshGemmaButtons(bool canEditChain)
    {
        _suggestActions.IsEnabled = canEditChain;
        _writeCode.IsEnabled = canEditChain && SelectedStep?.Id.StartsWith("Step_", StringComparison.Ordinal) == true;
        _undoSuggestion.IsEnabled = _lastSuggestion is { } undo && undo.Project == _project?.FilePath
            && undo.Site == CurrentSite && _chain is { Opaque: false } chain
            && undo.Steps.All(id => chain.Steps.Any(step => step.Id == id));
    }

    private async Task PrepareGemmaAsync()
    {
        var storage = new StorageSettingsStore().LoadOrCreate();
        string Localize(string key) => key switch
        {
            "LetItOut.Preparation.Title" => T("PrepareGemma"),
            "LetItOut.Preparation.Hint" => T("PrepareGemmaHint"),
            _ => _localize(key)
        };
        var preparation = new LetItOutChatPreparationWindow(OwnerWindow, storage, 4, Localize, ApplicationBackgroundOperations.ExitToken);
        if (preparation.ShowDialog() == true) _state.Text = T("GemmaReady");
        await Task.CompletedTask;
    }

    private async Task AskGemmaAsync(bool code, string? prefill = null, string? diagnostics = null)
    {
        if (_project is null || CurrentSite is not { } site || !Save()) return;
        var project = _project;
        var chain = project.ReadChain(site);
        var selected = code ? SelectedStep : null;
        if (code && selected is null) return;
        var source = _pendingResearchSource is { } pending && pending.Project == project.FilePath ? pending.Source : null;
        var description = GemmaTarget(project, site);
        if (source is not null) description += "\n" + string.Format(T("ResearchAttached"), source.Title);
        var prompt = EditGemmaTask(T(code ? "WriteCode" : "SuggestActions"), description, prefill ?? "", initial: true);
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (prompt.Length > 4000) throw new InvalidDataException("VisualConstructor.GemmaPromptLong");
        if (source is not null) _pendingResearchSource = null;
        var revision = _shareGemmaProjectSummary ? project.ReadGemmaSummary(site, code).Revision : null;
        await WorkAsync(async token =>
        {
            var references = await Task.Run(() => ConstructorResearchArchive.OpenDefault().Search(prompt, 2), token);
            var sources = references.Select(material => material.Reference());
            if (source is not null) sources = sources.Prepend(source);
            await ProposeAsync(new(project.FilePath, site, chain.Revision, prompt, selected?.Id, diagnostics,
                Sources: sources.DistinctBy(item => (item.Title, item.Url)).Take(2).ToArray(), ProjectSummaryRevision: revision), token);
        });
    }

    internal async Task ResumeGemmaAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (_operation is not null || !LeaveDraft()) throw new BackgroundOperationWaitingException("VisualConstructor.GemmaWaiting");
        var request = state.Input.Deserialize<ConstructorGemmaRequest>() ?? throw new InvalidDataException("VisualConstructor.InvalidGemmaReply");
        View(ConstructorProject.Open(request.ProjectFile));
        ViewForm(request.Site.Form);
        RefreshElementList(request.Site.Element);
        _events.SelectedValue = request.Site.Event;
        LoadChain(request.SelectedStep);
        await WorkAsync(async local =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(local, token);
            await ProposeAsync(request, linked.Token, state);
        });
    }

    private async Task ProposeAsync(ConstructorGemmaRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (request.HistoryId == Guid.Empty)
            request = request with { HistoryId = Guid.TryParse(restored?.Id, out var id) ? id : Guid.NewGuid() };
        var code = request.SelectedStep is not null;
        var title = T(code ? "WriteCode" : "SuggestActions");
        var subject = GemmaTarget(_project!, request.Site);
        var history = GemmaHistory(request.ProjectFile);
        var turn = new ConstructorGemmaTurn(request.HistoryId, restored?.UpdatedUtc ?? DateTimeOffset.UtcNow, subject, code, request.Prompt);
        history.Save(turn);
        var progress = new Progress<string>(key => _state.Text = _localize(key));
        ConstructorGemmaProposal proposal;
        try
        {
            proposal = await ConstructorGemmaWork.RunAsync(request, title, progress, token, restored);
        }
        catch (BackgroundOperationWaitingException)
        {
            history.Save(turn with { Reply = T("GemmaWaiting") });
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!ApplicationBackgroundOperations.IsSuspending(token))
                history.Save(turn with { Reply = T("Cancelled") });
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            history.Save(turn with { Reply = T("Failed") + "\n" + _localize(error.Message) });
            throw;
        }
        var diagnostics = proposal.Diagnostics;
        var answer = proposal.Reply;
        var summary = subject + "\n\n" + T("GemmaTask") + "\n" + request.Prompt + "\n\n";
        if (answer.NeedsInput)
        {
            var question = answer.Question.Length != 0;
            var status = question ? "Clarify" : "GemmaNoProposal";
            _state.Text = T(status);
            var explanation = question ? answer.Question
                : answer.Problem == "VisualConstructor.GemmaNoMatch" ? T("GemmaNoMatch") : answer.Problem;
            var response = T(status) + "\n" + explanation;
            history.Save(turn with { Reply = response });
            token.ThrowIfCancellationRequested();
            var decision = Review(title, summary + response, question ? "ClarifyAnswer" : "EditGemmaTask", null, token);
            if (decision != 1 || token.IsCancellationRequested) return;
            var prompt = EditGemmaTask(title, question ? explanation : subject, question ? "" : request.Prompt);
            if (question && !string.IsNullOrWhiteSpace(prompt))
            {
                prompt = request.Prompt + "\n\n" + answer.Question + "\n" + prompt;
                if (prompt.Length > 4000) throw new InvalidDataException("VisualConstructor.GemmaPromptLong");
            }
            if (!string.IsNullOrWhiteSpace(prompt)) await ProposeAsync(request with { Prompt = prompt, HistoryId = default }, token);
            return;
        }
        var display = code ? answer.Body : string.Join("\n\n", answer.Steps.Select((step, index) =>
            $"{index + 1}. {DescribeProposalStep(_project!, request.Site, step)}"));
        _state.Text = T(diagnostics is null ? "GemmaProposalReady" : "BuildFailed");
        var replyText = T(code ? "GemmaProposedCode" : "GemmaProposedSteps") + "\n" + display + "\n\n"
            + T(diagnostics is null ? "GemmaBuildReview" : "BuildFailed");
        if (diagnostics is not null) replyText += "\n\n" + diagnostics;
        history.Save(turn with { Reply = replyText });
        token.ThrowIfCancellationRequested();
        var repair = code && diagnostics is not null;
        var apply = Review(title, summary + replyText, diagnostics is null ? "ApplyProposal" : null, repair ? "RepairCode" : "EditGemmaTask", token);
        token.ThrowIfCancellationRequested();
        if (apply == 2)
        {
            if (repair)
            {
                await ProposeAsync(request with { Diagnostics = diagnostics + "\n\nProposed method body:\n" + answer.Body, HistoryId = default }, token);
                return;
            }
            var prompt = EditGemmaTask(title, subject, request.Prompt);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                await ProposeAsync(request with { Prompt = prompt, HistoryId = default }, token);
                return;
            }
            _state.Text = T("ProposalDiscarded");
            return;
        }
        if (apply != 1) { _state.Text = T("ProposalDiscarded"); return; }
        var ids = ConstructorGemmaWork.Put(_project!, request, answer, record: true);
        if (!code) _lastSuggestion = (request.ProjectFile, request.Site, ids);
        ReloadActions(request.Site, ids[0]);
    }

    private string GemmaTarget(ConstructorProject project, ConstructorEventSite site)
    {
        var title = project.FormTitle(site.Form);
        var element = site.Element is null ? null : new ConstructorLayout(project.ReadForm(site.Form)).Elements
            .FirstOrDefault(node => ConstructorLayout.Name(node) == site.Element);
        if (element is not null) title += " · " + DescribeElement(element);
        return title + " · " + T("Event." + site.Event);
    }

    private string DescribeProposalStep(ConstructorProject project, ConstructorEventSite site, ConstructorStepSpec step)
    {
        var layout = new ConstructorLayout(project.ReadForm(site.Form));
        var forms = project.ReadForms();
        var description = ConstructorSteps.Arguments(step.Kind).Select((arg, i) =>
        {
            var value = step.Arguments[i];
            var label = arg.Kind switch
            {
                ConstructorArgumentKind.Form => forms.FirstOrDefault(form => form.Id == value) is { } form ? project.FormTitle(form) : value,
                ConstructorArgumentKind.ReadElement or ConstructorArgumentKind.WriteElement =>
                    layout.Elements.FirstOrDefault(node => ConstructorLayout.Name(node) == value) is { } element ? DescribeElement(element) : value,
                _ => value
            };
            return T(arg.Key) + "  " + label;
        });
        return string.Join("\n", new[] { T("Action." + step.Kind) }.Concat(description));
    }

    private string? EditGemmaTask(string title, string context, string prompt, bool initial = false)
    {
        var field = new TextBox { Text = prompt, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 4000 };
        var panel = new DockPanel();
        var heading = DialogText("GemmaTask", heading: true);
        heading.Text = context + "\n\n" + heading.Text;
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var share = new System.Windows.Controls.CheckBox { Content = T("GemmaProjectSummary"), IsChecked = _shareGemmaProjectSummary,
            Margin = new Thickness(0, 10, 0, 0), ToolTip = T("GemmaProjectSummaryHint") };
        if (initial)
        {
            AutomationProperties.SetAutomationId(share, "VisualConstructor.GemmaProjectSummary");
            DockPanel.SetDock(share, Dock.Bottom);
            panel.Children.Add(share);
        }
        panel.Children.Add(field);
        AutomationProperties.SetLabeledBy(field, heading);
        AutomationProperties.SetAutomationId(field, "VisualConstructor.GemmaTask");
        var dialog = EditDialog(title, panel, out var buttons, width: 740, height: 420);
        var submit = EditDialogButtons(dialog, buttons, "SubmitGemmaTask", "SubmitGemmaTask", () => dialog.DialogResult = true);
        submit.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
        field.TextChanged += (_, _) => submit.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
        dialog.Loaded += (_, _) => { field.Focus(); field.CaretIndex = field.Text.Length; };
        if (dialog.ShowDialog() != true) return null;
        if (initial) _shareGemmaProjectSummary = share.IsChecked == true;
        return field.Text.Trim();
    }

    private int Review(string title, string text, string? primary, string? alternate, CancellationToken token)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(box, "VisualConstructor.GemmaReviewDetails");
        var dialog = EditDialog(title, box, out var buttons, width: 800, height: 540);
        int decision = 0;
        if (primary is not null) Add(primary, 1, true);
        if (alternate is not null) Add(alternate, 2, primary is null);
        Add("CloseProposal", 0, false);
        using var close = token.Register(() => Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        if (!token.IsCancellationRequested) dialog.ShowDialog();
        return decision;

        void Add(string key, int result, bool important)
        {
            var button = PromptDialogUi.Button(T(key), () => { decision = result; dialog.Close(); }, primary: important);
            AutomationProperties.SetAutomationId(button, "VisualConstructor." + key);
            button.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(button);
        }
    }

    private async Task CheckBuildAsync()
    {
        if (_project is null || !Save()) return;
        var project = _project;
        await WorkAsync(async token =>
        {
            _state.Text = T("CheckingCode");
            var diagnostics = await ConstructorBuild.CheckAsync(project, _ => { }, token);
            _state.Text = T(diagnostics is null ? "BuildPassed" : "BuildFailed");
            if (diagnostics is null) return;
            var step = SelectedStep;
            var canRepair = step is not null && step.Id.StartsWith("Step_", StringComparison.Ordinal)
                && diagnostics.Contains(Path.GetFileName(step.FilePath), StringComparison.OrdinalIgnoreCase);
            if (Review(T("BuildFailed"), diagnostics, canRepair ? "RepairCode" : null, null, token) == 1)
                await PromptRepairAsync(diagnostics, token);
        });
    }

    private async Task PromptRepairAsync(string diagnostics, CancellationToken token)
    {
        if (CurrentSite is not { } site || SelectedStep is not { } step) return;
        var chain = _project!.ReadChain(site);
        var prompt = LiteraryEditorDialogs.Edit(this, _localize, T("RepairCode"), T("RepairPrompt"), multiline: true)?.Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
            await ProposeAsync(new(_project.FilePath, site, chain.Revision, prompt, step.Id, diagnostics), token);
    }

    private void UndoSuggestion()
    {
        if (_lastSuggestion is not { } undo || CurrentSite != undo.Site || _project?.FilePath != undo.Project) return;
        Save();
        var chain = _project.ReadChain(undo.Site);
        _project.CommitChain(chain, chain.Steps.Where(step => !undo.Steps.Contains(step.Id)).ToArray());
        _lastSuggestion = null;
        ReloadActions(undo.Site);
    }
}
