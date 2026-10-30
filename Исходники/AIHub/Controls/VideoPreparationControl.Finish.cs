using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProgressBar = System.Windows.Controls.ProgressBar;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void BuildPackage(StackPanel page)
    {
        page.Children.Add(Text("PackageHint")); var issues = VideoPreparationReadiness.Check(Session.Project);
        page.Children.Add(Text(issues.Any(i => i.Blocking) ? "PackageHasIssues" : "PackageReady", true));
        var summary = Text("PackageSummary");
        summary.Text = string.Format(T("PackageSummary"), Session.Project.World?.Cards.Count ?? 0, Session.Project.References?.Images.Count ?? 0,
            Session.Project.Shots?.Scenes.Count ?? 0, Session.Project.Storyboard?.Frames.Count ?? 0, VideoStoryboard.Duration(Session.Project).ToString("0.###", CultureInfo.InvariantCulture)); page.Children.Add(summary);
        foreach (var issue in issues)
        {
            var label = Text(issue.Key); if (issue.Detail.Length > 0) label.Text += " · " + issue.Detail;
            label.Text = (issue.Blocking ? "● " : "○ ") + label.Text; page.Children.Add(label);
            var go = Action("PackageFix", () => { if (Checkpoint()) { Session.Project.CurrentStep = issue.Step; Render(); ScheduleWarmup(); } });
            go.Content = T("PackageFix") + ": " + T("Step." + issue.Step); page.Children.Add(go);
        }
        page.Children.Add(Action("PackageRecheck", Render));
    }
    private void BuildFinish(StackPanel page)
    {
        if (BuildFinishProgress(page)) return;
        page.Children.Add(Text("FinishHint"));
        var actions = new WrapPanel();
        actions.Children.Add(Action("FinishExport", () => _ = ExportPreparationAsync()));
        actions.Children.Add(Action("FinishChooseModel", () => ChooseFinishModel())); page.Children.Add(actions);
        if (VideoGeneratorPlan.Selected(Session.Project) is { } selected)
        {
            var label = Text("FinishSelectedModel"); label.Text = string.Format(T("FinishSelectedModel"), VideoConceptModelName(selected.Settings.ModelId)); page.Children.Add(label);
            if (!VideoGeneratorPlan.IsAvailable(selected.Settings.ModelId)) page.Children.Add(Text("GeneratorUnavailable"));
        }
        if (_lastExportPath is not null) { var result = Text("ExportDone"); result.Text += "\n" + _lastExportPath; page.Children.Add(result); }
    }
    public event Action<VideoGeneratorSettings>? GeneratorRequested;
    private bool ChooseFinishModel()
    {
        if (ConceptBusy || PendingConcept) return false;
        var p = Session.Project;
        var selected = VideoGeneratorPlan.Selected(p)?.Settings;
        var dialog = new VideoGeneratorChoiceWindow(Window.GetWindow(this), _localize, selected?.ModelId);
        _conceptDialogOpen = true;
        bool accepted;
        try { accepted = BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) == true; }
        finally { _conceptDialogOpen = false; }
        if (!accepted || dialog.SelectedModelId is not { } id || !ReferenceEquals(p, Session.Project)) return false;
        var settings = VideoGeneratorPlan.SettingsForModel(p, id);
        VideoGeneratorPlan.SaveChoice(p, settings); Session.Changed();
        var saved = Save(); Render(); return saved;
    }
    private bool ContinueFromFinish()
    {
        if (!VideoPreparationReadiness.Ready(Session.Project)) { _statusKey = "PackageNotReady"; RefreshStatus(); return false; }
        if ((VideoGeneratorPlan.Selected(Session.Project) is not { } selected || !VideoGeneratorPlan.IsAvailable(selected.Settings.ModelId))
            && !ChooseFinishModel()) return false;
        if ((Session.IsDirty || Session.ArchivePath is null) && !Save()) return false;
        return OpenSelectedGenerator();
    }
    private bool OpenSelectedGenerator()
    {
        var selected = VideoGeneratorPlan.Selected(Session.Project)!;
        if (!VideoGeneratorPlan.IsAvailable(selected.Settings.ModelId))
        { _statusKey = "GeneratorUnavailable"; RefreshStatus(); return false; }
        if (GeneratorRequested is { } next) { next(selected.Settings); return true; }
        _statusKey = "GeneratorNextPending"; RefreshStatus(); return false;
    }
    private static string VideoConceptModelName(string id) => VideoGeneratorPlan.Candidates.FirstOrDefault(c => c.Id == id)?.Name ?? id;
    private bool FinishBeforeNext()
    {
        var step = Session.Project.CurrentStep;
        if (step == "storyboard" && (_boardInvalidDuration || !VideoPreparationReadiness.BoardReady(Session.Project)))
        { _statusKey = _boardInvalidDuration ? "BoardDurationInvalid" : "BoardNotReady"; RefreshStatus(); return false; }
        if (step == "package" && !VideoPreparationReadiness.Ready(Session.Project)) { _statusKey = "PackageNotReady"; RefreshStatus(); return false; }
        return true;
    }
    public string? AskOpenRoute(VideoPreparationProject candidate)
    {
        if (candidate.Generator?.FinalReached != true && candidate.CurrentStep is not ("finish" or "generator")) return candidate.CurrentStep;
        var question = T("OpenRouteQuestion");
        if (VideoGeneratorPlan.Selected(candidate) is { } selected && !VideoGeneratorPlan.IsAvailable(selected.Settings.ModelId))
            question += "\n\n" + T("GeneratorUnavailable");
        var dialog = new VideoPreparationDecisionWindow(Window.GetWindow(this), _localize, "OpenRouteTitle", question,
            ("OpenRouteGenerator", "generator"), ("OpenRoutePreparation", "preparation"), ("OpenRouteCancel", "cancel"));
        var generator = ScenarioButton(dialog, "VideoPrep.Decision.generator"); generator.IsEnabled = VideoGeneratorPlan.CanResume(candidate);
        if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) != true || dialog.Choice == "cancel") return null;
        return dialog.Choice == "generator" ? "generator" : candidate.Generator?.PreparationStep ?? "package";
    }
    private bool LoadUserProject(string path, bool recovery)
    {
        _autosave.Stop();
        var loaded = VideoPreparationArchive.Load(path, Path.Combine(Session.RecoveryRoot, "Opened"));
        var route = AskOpenRoute(loaded); if (route is null) return false;
        loaded.CurrentStep = route;
        if (route == "generator" && VideoGeneratorPlan.Selected(loaded) is { } selected) loaded.Generator!.Draft = selected.Settings;
        Session.ApplyLoaded(loaded, path, recovery); ClearLocationError(); _statusKey = ""; Render();
        if (route == "generator") OpenSelectedGenerator();
        return true;
    }
    private bool BuildFinishProgress(StackPanel page)
    {
        if (!_finishingBusy && PendingConceptState?.Kind != ExportKind) return false;
        page.Children.Add(Text(_finishOperationKey)); _exportProgress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 5, IsIndeterminate = true };
        page.Children.Add(_exportProgress);
        if (PendingConceptState?.Kind == ExportKind) page.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync()));
        page.Children.Add(Action("FinishCancel", () => { _finishingCancel?.Cancel(); CancelConcept(); })); return true;
    }
    private static System.Windows.Controls.Button ScenarioButton(DependencyObject root, string id)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { if (child is System.Windows.Controls.Button b && System.Windows.Automation.AutomationProperties.GetAutomationId(b) == id) return b;
            try { return ScenarioButton(child, id); } catch (InvalidOperationException) { } }
        throw new InvalidOperationException("Missing route button.");
    }
}
