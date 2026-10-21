using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.MusicLora;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicLoraController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly TextBlock _count = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Func<string, MusicLoraUse[], CancellationToken, Task<bool>> _prepare;
    private Func<string, string> _l = key => key;
    private string _root = "";
    private bool HasPresets => MusicLoraCatalog.For(_view.Generation.Variation).Length > 0;
    private bool CanUse => HasPresets && _root.Length > 0 && _view.Session.CanChangeModel;
    public MusicLoraSelection Selection { get; private set; } = new();
    public MusicLoraController(MusicWorkspaceControl view)
    {
        _view = view; var button = view.Generation.LoraButton;
        var icon = (UIElement)button.Content; button.Content = null;
        var grid = new Grid(); grid.Children.Add(icon); grid.Children.Add(_count); button.Content = grid;
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => Open(); button.PreviewMouseRightButtonUp += (_, e) => {
            e.Handled = true; if (CanUse) Change(Selection with { Enabled = !Selection.Enabled }); };
        view.Session.StateChanged += Refresh;
        _prepare = (root, uses, token) => view.Dispatcher.InvokeAsync(() =>
            BackgroundUserDecision.ShowDialog(new MusicLoraPreparationWindow(Window.GetWindow(view), root, uses, _l, token), Window.GetWindow(view), token) == true).Task;
        MusicLoraAssets.RequestPreparation = _prepare;
        Refresh();
    }
    public void Configure(string root) { _root = root; Refresh(); }
    public void Localize(Func<string, string> localize) { _l = localize; Refresh(); }
    public void Restore(MusicLoraSelection selection) { selection.Validate(); Selection = selection.Snapshot(); Refresh(); }
    private void Change(MusicLoraSelection selection)
    {
        var previous = Selection; Restore(selection);
        try { _view.Projects.SaveLoraWorkspace(); }
        catch (Exception error) { Restore(previous); _view.Status.AppendLog(error.Message); }
    }
    public void Refresh()
    {
        var button = _view.Generation.LoraButton; var count = Selection.Choices(_view.Generation.Variation).Length;
        var available = HasPresets;
        _count.Text = count.ToString(); _count.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        button.Opacity = available && Selection.Enabled ? 1 : .45;
        button.IsEnabled = CanUse;
        MusicAudioUi.Label(button, available
            ? string.Format(_l(Selection.Enabled ? "Music.Lora.ButtonOn" : "Music.Lora.ButtonOff"), count)
            : _l("Music.Lora.ButtonUnavailable"));
    }
    private void Open()
    {
        if (!CanUse) return;
        try {
            var window = new MusicLoraWindow(Window.GetWindow(_view), _root, _view.Generation.Variation, Selection,
                _view.Wishes.RequestStyle, _view.Editor.Lyrics, _view.Generation.ExpertSettings,
                _view.Generation.Options.DurationSeconds ?? 360, _l, _view.Wishes.State);
            if (window.ShowDialog() == true) Change(window.Selection);
        } catch (Exception error) { _view.Status.AppendLog(_l("Music.Lora.Failed") + " " + _l(error.Message)); }
    }
    public void Dispose()
    {
        _view.Session.StateChanged -= Refresh;
        if (MusicLoraAssets.RequestPreparation == _prepare) MusicLoraAssets.RequestPreparation = null;
    }
}
