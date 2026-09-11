using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class LiteraryProjectCreateControl
{
    private LiterarySourceIndex? _preparedIndex;
    private LiteraryProjectReservation? _reservation;
    private CancellationTokenSource? _indexCancellation;
    private Task _indexTask = Task.CompletedTask;
    private int _indexGeneration;
    private readonly ProgressBar _indexProgress = new() { Minimum = 0, Maximum = 100, Height = 10, MinWidth = 120, Margin = new Thickness(10, 8, 0, 8) };
    private readonly TextBlock _indexStatus = new() { TextWrapping = TextWrapping.Wrap };

    private FrameworkElement BuildSourcePreparation()
    {
        var panel = new StackPanel();
        _indexProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _indexProgress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        var row = new DockPanel();
        var add = LiteraryUi.Button(_l("Literary.Create.AddMaterials"), AddMaterials);
        DockPanel.SetDock(add, Dock.Left); row.Children.Add(add); row.Children.Add(_indexProgress);
        panel.Children.Add(row);
        _indexStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); panel.Children.Add(_indexStatus);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        actions.Children.Add(LiteraryUi.Button(_l("Literary.Rag.Retry"), RestartIndexing));
        actions.Children.Add(LiteraryUi.Button(_l("Literary.Prepare.Stop"), CancelIndexing)); panel.Children.Add(actions);
        return panel;
    }
    private void UpdateCreateAvailability()
    {
        _create.IsEnabled = !_saving && (_type.SelectedIndex != 1 || _materials.Count == 0 || _preparedIndex?.Ready == true);
        _create.Opacity = _create.IsEnabled ? 1 : 0.45;
        _create.ToolTip = _create.IsEnabled ? null : _l("Literary.Rag.Wait");
    }
    public void CancelIndexing()
    {
        _indexCancellation?.Cancel();
        _indexGeneration++;
        var ready = _preparedIndex; _preparedIndex = null;
        if (ready is not null) _indexTask = DisposeAfterAsync(_indexTask, ready);
        _indexProgress.IsIndeterminate = false;
        _indexStatus.Text = _l("Literary.Prepare.Cancelled");
        UpdateCreateAvailability();
    }
    private static async Task DisposeAfterAsync(Task previous, LiterarySourceIndex index)
    {
        try { await previous; } finally { await index.DisposeAsync(); }
    }
    private void RestartIndexing()
    {
        CancelIndexing();
        var previous = _indexTask;
        var paths = _type.SelectedIndex == 1 ? _materials.ToArray() : [];
        var generation = _indexGeneration;
        _indexCancellation = new();
        var cancellation = _indexCancellation;
        _indexStatus.Text = _l(paths.Length == 0 ? "Literary.Rag.NoSources" : "Literary.Rag.Wait");
        _indexTask = PrepareSourcesAsync(previous, paths, generation, cancellation);
    }
    private async Task PrepareSourcesAsync(Task previous, string[] paths, int generation, CancellationTokenSource cancellation)
    {
        LiterarySourceIndex? index = null;
        try
        {
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            if (paths.Length == 0) return;
            if (!System.IO.Directory.Exists(_folder.Text.Trim()) || !LiteraryProjectStore.IsValidProjectName(_name.Text.Trim()))
            { _indexStatus.Text = _l("Literary.Rag.LocationFirst"); return; }
            _indexProgress.IsIndeterminate = true;
            if (_reservation is null)
                _reservation = new LiteraryProjectReservation(_folder.Text.Trim(), _name.Text.Trim());
            if (!string.Equals(_reservation.Root, System.IO.Path.Combine(_folder.Text.Trim(), _name.Text.Trim()), StringComparison.OrdinalIgnoreCase))
                throw new System.IO.IOException("Prepared project location changed. Restore its folder and name.");
            index = new LiterarySourceIndex(projectRoot: _reservation.Root);
            var progress = new Progress<LiteraryPreparationProgress>(p =>
            {
                if (generation != _indexGeneration) return;
                _indexStatus.Text = _l("Literary.Rag." + p.Stage) + (p.Detail.Length > 0 ? " · " + p.Detail : "")
                    + (p.Percent >= 0 ? $" · {p.Percent:0}%" : "");
                _indexProgress.IsIndeterminate = p.Percent < 0;
                if (p.Percent >= 0) _indexProgress.Value = p.Stage == "Ready" ? 100 : Math.Min(99, p.Percent);
            });
            await index.PrepareAsync(paths, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation == _indexGeneration) { _preparedIndex = index; index = null; }
        }
        catch (OperationCanceledException) { if (generation == _indexGeneration) _indexStatus.Text = _l("Literary.Prepare.Cancelled"); }
        catch (Exception ex) { if (generation == _indexGeneration) _indexStatus.Text = _l("Literary.Prepare.Error") + " " + ex.Message; }
        finally
        {
            if (index is not null) await index.DisposeAsync();
            if (generation == _indexGeneration) { _indexProgress.IsIndeterminate = false; UpdateCreateAvailability(); }
            if (_indexCancellation == cancellation) _indexCancellation = null;
            cancellation.Dispose();
        }
    }
}
