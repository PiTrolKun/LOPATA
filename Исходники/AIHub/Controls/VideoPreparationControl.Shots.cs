using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void BuildShots(StackPanel page)
    {
        _shotsInvalidDuration = false;
        var p = Session.Project; var d = VideoShots.Ensure(p); var translation = p.CurrentStep == "shots_translation";
        var busy = ConceptBusy || PendingConcept || _shotsStarting;
        page.Children.Add(Text(translation ? "ShotsTranslationHint" : "ShotsHint"));
        var tools = new WrapPanel();
        var model = Action("ShotsModel", () => _ = ShotsModelAsync()); model.Content = T("AiModel") + ": " + VideoConceptModels.Name(
            d.ModelId.Length > 0 ? d.ModelId : p.LastAssistantModelId.Length > 0 ? p.LastAssistantModelId : "executor");
        model.IsEnabled = !busy; tools.Children.Add(model);
        var run = Action(translation ? "ShotsTranslateAgain" : d.Scenes.Count == 0 ? "ShotsBuild" : "ShotsRebuild", () => _ = StartShotsAsync(translation: translation));
        run.IsEnabled = !busy; tools.Children.Add(run);
        if (!translation)
        {
            var alternatives = Action("ShotsAlternatives", () => _ = StartShotsAsync(alternatives: true)); alternatives.IsEnabled = !busy; tools.Children.Add(alternatives);
            var saved = d.Candidates.Where(c => !c.Automatic && c.Complete && c.Scenes.Count > 0).TakeLast(3).Select(c => c.Id).ToArray();
            if (saved.Length > 0) { var show = Action("ShotsSavedOptions", () => OpenShotOptions(saved)); show.IsEnabled = !busy; tools.Children.Add(show); }
        }
        page.Children.Add(tools);
        if (!translation && !busy)
        {
            var versions = new WrapPanel();
            foreach (var direction in new[] { -1, 1 })
            { var move = Action(direction == -1 ? "ShotsPrevious" : "ShotsNext", () => { if (VideoShots.MoveVersion(p, direction)) { Changed(); Checkpoint(); Render(); } }); move.Content = direction == -1 ? "←" : "→"; versions.Children.Add(move); }
            var info = Text("Version"); info.Text = string.Format(T("Version"), Math.Max(1, d.History.FindIndex(v => v.Id == d.RevisionId) + 1), Math.Max(1, d.History.Count)); versions.Children.Add(info); page.Children.Add(versions);
        }
        if (PendingConcept && !_shotsBusy && !_shotsStarting)
        { var pending = new WrapPanel(); pending.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync())); pending.Children.Add(Action("ShotsCancel", CancelConcept)); page.Children.Add(pending); }
        if (_shotsBusy || _shotsStarting)
        {
            var progress = new Grid { Margin = new Thickness(0, 16, 0, 16) };
            var circle = new System.Windows.Shapes.Ellipse { Width = 42, Height = 42, StrokeThickness = 4,
                StrokeDashArray = new DoubleCollection { 3, 1 }, RenderTransformOrigin = new System.Windows.Point(.5, .5), RenderTransform = new RotateTransform() };
            circle.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(circle, "VideoPrep.Shots.Spinner");
            var paused = PendingConceptState?.Phase is AIHub.Models.BackgroundOperationPhase.Paused or AIHub.Models.BackgroundOperationPhase.Pausing or AIHub.Models.BackgroundOperationPhase.Waiting;
            circle.Loaded += (_, _) => { if (!paused) ((RotateTransform)circle.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever }); };
            circle.Unloaded += (_, _) => ((RotateTransform)circle.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
            progress.Children.Add(circle); page.Children.Add(progress);
            _shotsStageText = Text("ShotsWorking", true); AutomationProperties.SetAutomationId(_shotsStageText, "VideoPrep.Shots.Stage"); page.Children.Add(_shotsStageText); UpdateShotsProgress();
            page.Children.Add(Text("ShotsReasoning"));
            _shotsThoughtBox = new System.Windows.Controls.TextBox { Text = _shotsThoughts, IsReadOnly = true, Height = 220,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Opacity = .75 };
            AutomationProperties.SetAutomationId(_shotsThoughtBox, "VideoPrep.Shots.Reasoning"); page.Children.Add(_shotsThoughtBox);
            if (_shotsThoughts.Length == 0) page.Children.Add(Text("ShotsReasoningWaiting"));
            var controls = new WrapPanel(); controls.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync()));
            controls.Children.Add(Action("ShotsCancel", () => _shotsCancel?.Cancel())); page.Children.Add(controls);
            var candidate = d.Candidates.LastOrDefault();
            if (candidate is not null)
            {
                var completed = Text("ShotsWorking"); completed.Text = string.Join("\n", candidate.Scenes.Select((s, i) =>
                    (s.Shots.Count > 0 ? "✓ " : "○ ") + ShotSceneLabel(s, i)));
                AutomationProperties.SetAutomationId(completed, "VideoPrep.Shots.Completed"); page.Children.Add(completed);
            }
            return;
        }
        _shotsThoughtBox = null; _shotsStageText = null;
        if (!VideoShots.Current(p) && d.Scenes.Count > 0) page.Children.Add(Text("ShotsStale"));
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); var scene = d.Scenes.FirstOrDefault(s => s.Id == d.SelectedSceneId) ?? d.Scenes.FirstOrDefault();
        foreach (var s in d.Scenes)
        {
            var chooseScene = Action("ShotsSelectScene", () => { d.SelectedSceneId = s.Id; d.SelectedShotId = ""; Render(); });
            chooseScene.Content = $"{d.Scenes.IndexOf(s) + 1}. " + ShotSceneLabel(s, d.Scenes.IndexOf(s));
            AutomationProperties.SetAutomationId(chooseScene, "VideoPrep.Shots.Scene." + s.Id); left.Children.Add(chooseScene);
            foreach (var t in s.Shots)
            {
                var choose = Action("ShotsSelectPlan", () => { d.SelectedSceneId = s.Id; d.SelectedShotId = t.Id; Render(); });
                choose.Content = $"   {d.Scenes.IndexOf(s) + 1}.{s.Shots.IndexOf(t) + 1} " + (t.Name.Length == 0 ? T("ShotsNewPlan") : t.Name);
                AutomationProperties.SetAutomationId(choose, "VideoPrep.Shots.Plan." + t.Id); left.Children.Add(choose);
            }
        }
        var side = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var edits = new WrapPanel();
        if (!translation)
        {
            var add = Action("ShotsAdd", () => { }); add.Content = "+";
            var menu = new System.Windows.Controls.ContextMenu();
            foreach (var (key, action) in new (string, System.Action)[] { ("ShotsAddScene", () => { var s = new VideoScenePlan(); d.Scenes.Add(s); d.SelectedSceneId = s.Id; d.SelectedShotId = ""; }),
                ("ShotsAddPlan", () => { scene ??= new VideoScenePlan(); if (!d.Scenes.Contains(scene)) d.Scenes.Add(scene); var t = new VideoShotPlan(); scene.Shots.Add(t); d.SelectedSceneId = scene.Id; d.SelectedShotId = t.Id; }) })
            { var item = new System.Windows.Controls.MenuItem { Header = T(key) }; item.Click += (_, _) => { VideoShots.Capture(p); action(); d.Attempted = true; if (d.Source == "") d.Source = VideoShots.Hash(VideoShots.Source(p)); Changed(); Checkpoint(); Render(); }; menu.Items.Add(item); }
            add.ContextMenu = menu; add.Click += (_, _) => { menu.PlacementTarget = add; menu.IsOpen = true; }; edits.Children.Add(add);
            edits.Children.Add(Action("ShotsUp", () => MoveShot(-1))); edits.Children.Add(Action("ShotsDown", () => MoveShot(1)));
            edits.Children.Add(Action("ShotsDelete", DeleteShot)); DockPanel.SetDock(edits, Dock.Top); side.Children.Add(edits);
        }
        var list = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(list, "VideoPrep.Shots.ListScroll");
        FitShotsList(list, body, edits);
        side.VerticalAlignment = VerticalAlignment.Top; side.Children.Add(list); body.Children.Add(side);
        var editor = new StackPanel(); Grid.SetColumn(editor, 1); body.Children.Add(editor); page.Children.Add(body);
        if (scene is null) { editor.Children.Add(Text("ShotsEmpty")); return; }
        d.SelectedSceneId = scene.Id;
        var nativeScene = scene; System.Action edited = () => Changed();
        if (translation)
        {
            var version = VideoShots.SelectedTranslation(d, scene.Id);
            if (version is null) { editor.Children.Add(Text("ShotsMissingTranslation")); return; }
            scene = VideoShots.Copy(version.Scene); var draftSaved = false;
            edited = () =>
            {
                if (!draftSaved)
                {
                    var draft = new VideoShotTranslation { Id = Guid.NewGuid().ToString("N"), Scene = scene, NativeSnapshot = version.NativeSnapshot, Model = version.Model, Manual = true };
                    d.Translations.Add(draft); d.SelectedTranslations[nativeScene.Id] = draft.Id; draftSaved = true;
                }
                Changed();
            };
            var versions = d.Translations.Where(t => t.Scene.Id == scene.Id).ToArray(); var index = Array.FindIndex(versions, t => t.Id == version.Id);
            var history = new WrapPanel();
            foreach (var direction in new[] { -1, 1 })
            {
                var move = Action(direction == -1 ? "ShotsEnglishPrevious" : "ShotsEnglishNext", () =>
                { d.SelectedTranslations[nativeScene.Id] = versions[index + direction].Id; Changed(); Checkpoint(); Render(); });
                move.Content = direction == -1 ? "←" : "→"; move.IsEnabled = index + direction >= 0 && index + direction < versions.Length; history.Children.Add(move);
            }
            var label = Text("Version"); label.Text = string.Format(T("Version"), index + 1, versions.Length); history.Children.Add(label); editor.Children.Add(history);
            if (!VideoShots.TranslationCurrent(d, nativeScene)) editor.Children.Add(Text("ShotsEnglishStale"));
        }
        ShotField(editor, "ShotsSceneName", scene.Name, value => scene.Name = value, edited, translation, false);
        ShotField(editor, "ShotsSceneIntent", scene.Intent, value => scene.Intent = value, edited, translation, true);
        var shot = scene.Shots.FirstOrDefault(t => t.Id == d.SelectedShotId);
        if (shot is null) { editor.Children.Add(Text("ShotsNoPlan")); return; }
        ShotField(editor, "ShotsName", shot.Name, value => shot.Name = value, edited, translation, false);
        ShotField(editor, "ShotsStart", shot.Start, value => shot.Start = value, edited, translation, true);
        ShotField(editor, "ShotsEnd", shot.End, value => shot.End = value, edited, translation, true);
        BuildShotReferences(editor, shot, edited, translation);
        var details = new StackPanel(); var expander = new Expander { Header = T("ShotsDetails"), Content = details, Margin = new Thickness(0, 10, 0, 10) };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); editor.Children.Add(expander);
        foreach (var (key, get, set) in new (string, Func<string>, System.Action<string>)[] {
            ("ShotsFunction", () => shot.Function, v => shot.Function = v), ("ShotsComposition", () => shot.Composition, v => shot.Composition = v),
            ("ShotsCamera", () => shot.Camera, v => shot.Camera = v), ("ShotsLight", () => shot.Light, v => shot.Light = v),
            ("ShotsContinuity", () => shot.Continuity, v => shot.Continuity = v), ("ShotsConstraints", () => shot.Constraints, v => shot.Constraints = v) })
            ShotField(details, key, get(), set, edited, translation, true);
        var duration = new ExampleTextInput(T("ShotsDuration"), T("ShotsDurationExample"), "VideoPrep.Shots.Duration");
        duration.Input.Text = shot.Duration == 0 ? "" : shot.Duration.ToString(CultureInfo.InvariantCulture); duration.Input.IsReadOnly = translation;
        duration.Input.TextChanged += (_, _) => { var empty = duration.Input.Text.Trim() == "";
            var valid = double.TryParse(duration.Input.Text.Replace(',', '.'), CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= 86400;
            duration.Input.ToolTip = empty || valid ? T("ShotsDurationExample") : T("ShotsDurationInvalid");
            _shotsInvalidDuration = !empty && !valid;
            duration.Input.BorderBrush = empty || valid ? null : System.Windows.Media.Brushes.OrangeRed;
            if (empty || valid) { shot.Duration = empty ? 0 : seconds; edited(); } };
        details.Children.Add(Text("ShotsDuration")); details.Children.Add(duration);
        var cards = new WrapPanel();
        foreach (var card in p.World?.Cards ?? [])
        {
            var check = new System.Windows.Controls.CheckBox { Content = card.Name, IsChecked = shot.CardIds.Contains(card.Id), IsEnabled = !translation, Margin = new Thickness(4) };
            check.Checked += (_, _) => { shot.CardIds = shot.CardIds.Append(card.Id).Distinct().ToArray(); edited(); };
            check.Unchecked += (_, _) => { shot.CardIds = shot.CardIds.Where(id => id != card.Id).ToArray(); edited(); }; cards.Children.Add(check);
        }
        details.Children.Add(Text("ShotsParticipants")); details.Children.Add(cards);
        if (!translation)
        {
            var improve = ConceptIcon("ShotsImprove", "✨", () => _ = ShotsModelAsync(true, shot.Id)); editor.Children.Add(improve);
        }
        var total = d.Scenes.SelectMany(s => s.Shots).Sum(t => t.Duration); var unspecified = d.Scenes.SelectMany(s => s.Shots).Count(t => t.Duration == 0);
        var summary = Text("ShotsSummary"); summary.Text = string.Format(T("ShotsSummary"), d.Scenes.Count, d.Scenes.Sum(s => s.Shots.Count), total, unspecified); page.Children.Add(summary);
    }
    private void ShotField(StackPanel page, string key, string value, System.Action<string> set, System.Action edited, bool english, bool multiline)
    {
        page.Children.Add(Text(key)); var input = new ExampleTextInput(T(key), T(key + "Example"), "VideoPrep." + key, multiline);
        input.Input.Text = value; input.Input.MaxLength = 20000; if (multiline) input.Input.Height = 90;
        LiterarySpellChecking.Enable(input.Input, english ? "en-US" : InputLanguage);
        input.Input.TextChanged += (_, _) => { set(input.Input.Text); edited(); }; page.Children.Add(input);
    }
    private void MoveShot(int direction)
    {
        var d = VideoShots.Ensure(Session.Project); var scene = d.Scenes.FirstOrDefault(s => s.Id == d.SelectedSceneId); if (scene is null) return;
        VideoShots.Capture(Session.Project);
        if (d.SelectedShotId != "") { var i = scene.Shots.FindIndex(t => t.Id == d.SelectedShotId); var j = i + direction; if (i >= 0 && j >= 0 && j < scene.Shots.Count) (scene.Shots[i], scene.Shots[j]) = (scene.Shots[j], scene.Shots[i]); }
        else { var i = d.Scenes.IndexOf(scene); var j = i + direction; if (j >= 0 && j < d.Scenes.Count) (d.Scenes[i], d.Scenes[j]) = (d.Scenes[j], d.Scenes[i]); }
        Changed(); Checkpoint(); Render();
    }
    private void DeleteShot()
    {
        var p = Session.Project; var d = VideoShots.Ensure(p); var scene = d.Scenes.FirstOrDefault(s => s.Id == d.SelectedSceneId); if (scene is null) return;
        VideoShots.Capture(p); if (d.SelectedShotId != "") { scene.Shots.RemoveAll(t => t.Id == d.SelectedShotId); d.SelectedShotId = ""; } else d.Scenes.Remove(scene);
        Changed(); Checkpoint(); Render();
    }
}
