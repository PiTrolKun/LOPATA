using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

public sealed partial class LivingArcadeControl
{
    private string? _pendingStartId;

    private void ShowMenu() => ShowMenuCore(true);

    private void ShowMenuCore(bool cancelPending)
    {
        SaveExperience();
        if (cancelPending) _pendingStartId = null;
        _playing = false;
        _host?.Stop(); _body.Content = null;
        _host?.Dispose(); _host = null; _simulation = null;
        ShowMenuChrome();
        _hud.Text = _l("Arcade.MenuTitle");
        _actions.Children.Clear();
        var content = new StackPanel { MaxWidth = 1100, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        content.Children.Add(LiteraryUi.Text(_l("Arcade.Description")));
        content.Children.Add(LiteraryUi.Text(_l("Arcade.ControlsTitle"), true));
        content.Children.Add(LiteraryUi.Text(_l("Arcade.Controls")));
        content.Children.Add(LiteraryUi.Text(_l("Arcade.Rules")));
        content.Children.Add(LiteraryUi.Text(_l("Arcade.PerspectiveHelp")));
        content.Children.Add(LiteraryUi.Text(_l("Arcade.RunsTitle"), true));
        try
        {
            var runs = _store.List();
            foreach (var run in runs)
            {
                var panel = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(MakeButton("Arcade.Continue", () => StartRun(run.Id), "Arcade.Continue." + run.Id, true));
                buttons.Children.Add(MakeButton("Arcade.Rename", () => RenameRun(run.Id, run.Name), "Arcade.Rename." + run.Id));
                buttons.Children.Add(MakeButton("Arcade.Delete", () => DeleteRun(run.Id, run.Name), "Arcade.Delete." + run.Id));
                DockPanel.SetDock(buttons, Dock.Right); panel.Children.Add(buttons);
                panel.Children.Add(LiteraryUi.Text(run.Name + " · " + _l("Arcade.Level") + " " + run.CurrentLevel));
                content.Children.Add(panel);
                if (run.RecoveredFromBackup) content.Children.Add(LiteraryUi.Text(_l("LivingArcade.Recovered")));
            }
            if (runs.Count == 0) content.Children.Add(LiteraryUi.Text(_l("Arcade.Empty")));
            var create = MakeButton("Arcade.New", CreateRun, "Arcade.New", true);
            create.IsEnabled = runs.Count < 3;
            content.Children.Add(create);
            if (runs.Count >= 3) content.Children.Add(LiteraryUi.Text(_l("Arcade.Full")));
        }
        catch (Exception error) { ShowError(error); }
        content.Children.Add(LiteraryUi.Text(_l("Arcade.SoundTitle"), true));
        AddVolume(content, "Arcade.SoundEffects", _effectsVolume, value => _effectsVolume = value, "Arcade.EffectsVolume");
        AddVolume(content, "Arcade.Music", _musicVolume, value => _musicVolume = value, "Arcade.MusicVolume");
        _body.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _actions.Children.Add(MakeButton("Arcade.PrepareNext", () => {
            if (_pendingStartId is { } pending) StartRun(pending); else _ = PrepareAheadAsync();
        }, "Arcade.PrepareNext"));
        _actions.Children.Add(MakeButton("Arcade.RetryWarmup", () => _ = WarmAsync(), "Arcade.RetryWarmup"));
        _actions.Children.Add(MakeButton("Arcade.PauseGeneration", () => _ = PauseGenerationAsync(), "Arcade.PauseGeneration"));
        _actions.Children.Add(MakeButton("Arcade.ResumeGeneration", () => _ = ResumeGenerationAsync(), "Arcade.ResumeGeneration"));
    }

    private void AddVolume(Panel parent, string key, double value, Action<double> change, string id)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(LiteraryUi.Text(_l(key)));
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, Width = 220, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetAutomationId(slider, id);
        AutomationProperties.SetName(slider, _l(key));
        slider.ValueChanged += (_, _) => change(slider.Value);
        row.Children.Add(slider); parent.Children.Add(row);
    }

    private void CreateRun()
    {
        var name = AskName(_l("Arcade.DefaultName"));
        if (name is null) return;
        try { var run = _store.Create(name); ShowMenu(); StartRun(run.Id); }
        catch (Exception error) { ShowError(error); }
    }

    private void RenameRun(string id, string current)
    {
        var name = AskName(current);
        if (name is null) return;
        try { _store.Rename(id, name); ShowMenu(); }
        catch (Exception error) { ShowError(error); }
    }

    private void DeleteRun(string id, string name)
    {
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
            _l("Arcade.DeleteConfirm") + "\n" + name, _l("Arcade.Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _store.Delete(id);
            if (ApplicationBackgroundOperations.Current is { } controller)
                LivingArcadePendingWork.DiscardDeleted(controller, _store);
            if (_runId == id) _runId = null;
            if (_pendingStartId == id) _pendingStartId = null;
            ShowMenu();
        }
        catch (Exception error) { ShowError(error); }
    }

    private string? AskName(string current)
    {
        var input = new TextBox { Text = current, MaxLength = 80, Margin = new Thickness(0, 10, 0, 15) };
        AutomationProperties.SetAutomationId(input, "Arcade.RunName");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Title = _l("Arcade.NameTitle"), Width = 520, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), Content = panel };
        if (dialog.Owner is { } owner) dialog.Resources.MergedDictionaries.Add(owner.Resources);
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        panel.Children.Add(LiteraryUi.Text(_l("Arcade.NameTitle")));
        panel.Children.Add(input);
        var ok = MakeButton("Arcade.SaveName", () => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; }, "Arcade.SaveName", true);
        ok.IsDefault = true; panel.Children.Add(ok);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private async Task PauseGenerationAsync()
    {
        try { if (ApplicationBackgroundOperations.Current is { } controller
            && LivingArcadePendingWork.Find(controller, _runId, _shownOperationId) is { } pending) await controller.PauseAsync(pending.State.Id); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task ResumeGenerationAsync()
    {
        try
        {
            if (ApplicationBackgroundOperations.Current is not { } controller) return;
            LivingArcadePendingWork.DiscardDeleted(controller, _store);
            if (LivingArcadePendingWork.Find(controller, _runId, _shownOperationId) is { } pending)
                await controller.ResumeAsync(pending.State.Id, _lifetime);
        }
        catch (Exception error) { ShowError(error); }
    }
}
