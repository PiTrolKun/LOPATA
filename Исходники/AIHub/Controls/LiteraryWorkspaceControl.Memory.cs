using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private readonly ProgressBar _memoryProgress = new() { Minimum = 0, Maximum = 100, Height = 10, Margin = new Thickness(0, 12, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _memoryStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.Button _memoryRetry = new() { Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _memoryCancellation;
    private bool _memoryStarted;
    private Window? _memoryWindow;
    private readonly List<(System.Windows.Controls.Primitives.ButtonBase Button, bool Enabled)> _blockedButtons = [];
    private readonly System.Windows.Threading.DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _projectMissing;
    private readonly Func<IProgress<LiteraryPreparationProgress>, CancellationToken, Task>? _memoryPreparation;
    private bool Indexing => _memoryCancellation is not null || _jellyEditing;
    public bool IsIndexing => Indexing;
    private async Task PrepareMemoryAsync()
    {
        if (Indexing || _runtime.IsBusy) return;
        _studio?.ClearRequestStatus();
        using var cancellation = new CancellationTokenSource(); _memoryCancellation = cancellation;
        _writer.ActionsBlocked = _advisor.ActionsBlocked = true;
        _writer.RefreshAvailability(); _advisor.RefreshAvailability();
        _draft.BlockActions(true); EditorHost.IsEnabled = false;
        if (_memoryWindow is not null) BlockButtons(_memoryWindow);
        _memoryProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _memoryProgress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        _memoryProgress.Visibility = Visibility.Visible; _memoryProgress.IsIndeterminate = true;
        _memoryRetry.Visibility = Visibility.Collapsed;
        _memoryStatus.Text = _l("Literary.Rag.ProjectWait");
        var acceptingProgress = true;
        var progress = new Progress<LiteraryPreparationProgress>(p =>
        {
            if (!acceptingProgress) return;
            // A sub-operation can finish while further project files still need indexing.
            if (p.Stage == "Ready") return;
            _memoryProgress.IsIndeterminate = p.Percent < 0;
            // Ready is the only stage allowed to display complete readiness.
            if (p.Percent >= 0) _memoryProgress.Value = p.Stage == "Ready" ? 100 : Math.Min(99, p.Percent);
            _memoryStatus.Text = _l("Literary.Rag." + p.Stage) + (p.Detail.Length > 0 ? " · " + p.Detail : "");
        });
        try
        {
            bool jellyReady;
            if (_memoryPreparation is not null)
            {
                await _memoryPreparation(progress, cancellation.Token);
                jellyReady = await PrepareJellyAsync(progress, cancellation.Token);
            }
            else
            {
                jellyReady = await PrepareMaterialsAsync(progress, cancellation.Token);
            }
            acceptingProgress = false;
            _memoryStatus.Text = _l(jellyReady ? "Literary.Rag.Ready" : "Literary.Preparation.Deferred");
            if (!jellyReady) _memoryRetry.Visibility = Visibility.Visible;
            if (jellyReady && System.IO.File.Exists(System.IO.Path.Combine(_entry.ProjectPath,"Import","review.json")))
            {
                _memoryStatus.Text=AIHub.Services.LiteraryImport.ImportProjectStatus.Read(_entry.ProjectPath).Describe(_l);
                await RefreshImportExportAsync();
            }
        }
        catch (OperationCanceledException) { acceptingProgress = false; _memoryStatus.Text = _l("Literary.Prepare.Cancelled"); _memoryRetry.Visibility = Visibility.Visible; }
        catch (Exception failure) when (failure.GetBaseException() is LiteraryGpuMemoryException)
        {
            acceptingProgress = false;
            var ex = (LiteraryGpuMemoryException)failure.GetBaseException();
            _memoryStatus.Text = string.Format(_l("Literary.Jelly.GpuMemory"), ex.RequiredBytes / 1073741824.0, ex.FreeBytes / 1073741824.0);
            _memoryRetry.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            acceptingProgress = false;
            _memoryStatus.Text = _l("Literary.Rag.NotReady") + " " + ex.Message;
            _memoryRetry.Visibility = Visibility.Visible;
        }
        finally
        {
            _memoryProgress.IsIndeterminate = false; _memoryProgress.Visibility = Visibility.Collapsed;
            _memoryCancellation = null;
            foreach (var (button, enabled) in _blockedButtons) button.IsEnabled = enabled;
            _blockedButtons.Clear();
            _writer.ActionsBlocked = _advisor.ActionsBlocked = _projectMissing;
            _writer.RefreshAvailability(); _advisor.RefreshAvailability();
            _draft.BlockActions(_projectMissing); EditorHost.IsEnabled = !_projectMissing;
            if (!cancellation.IsCancellationRequested && _memoryRetry.Visibility != Visibility.Visible) ScheduleFirstCalibration();
        }
    }
    private void BlockButtons(DependencyObject root)
    {
        // EditorHost already blocks this subtree. Child IsEnabled is coerced to
        // false by the parent; storing it would permanently disable its actions.
        if (ReferenceEquals(root, EditorHost)) return;
        if (root is System.Windows.Controls.Primitives.ButtonBase button)
        { _blockedButtons.Add((button, button.IsEnabled)); button.IsEnabled = false; return; }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            BlockButtons(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    private void AttachMemoryGuard()
    {
        _memoryWindow = Window.GetWindow(this);
        if (_memoryWindow is null) return;
        _memoryWindow.PreviewMouseDown += GuardMouse;
        _memoryWindow.PreviewKeyDown += GuardKey;
        _presenceTimer.Tick -= CheckPresence; _presenceTimer.Tick += CheckPresence; _presenceTimer.Start();
    }
    private void DetachMemoryGuard()
    {
        _memoryCancellation?.Cancel();
        _presenceTimer.Stop(); _presenceTimer.Tick -= CheckPresence;
        if (_memoryWindow is null) return;
        _memoryWindow.PreviewMouseDown -= GuardMouse;
        _memoryWindow.PreviewKeyDown -= GuardKey;
        _memoryWindow = null;
    }
    private void CheckPresence(object? sender, EventArgs e)
    {
        if (_projectMissing) return;
        try { new LiteraryProjectLayout(_entry.ProjectPath).EnsurePresent(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _projectMissing = true; _memoryCancellation?.Cancel(); _runtime.Stop();
            _writer.ActionsBlocked = _advisor.ActionsBlocked = true;
            _writer.RefreshAvailability(); _advisor.RefreshAvailability();
            _draft.BlockActions(true); EditorHost.IsEnabled = false;
            _memoryStatus.Text = _l("Literary.Project.Unavailable");
        }
    }
    private bool IsChatInput(object origin) => _writer.IsInputOrigin(origin) || _advisor.IsInputOrigin(origin);
    private void GuardMouse(object sender, MouseButtonEventArgs e) { if (Indexing && !IsChatInput(e.OriginalSource)) e.Handled = true; }
    private void GuardKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!Indexing || IsChatInput(e.OriginalSource)) return;
        if (e.Key == Key.System && e.SystemKey == Key.F4) return;
        e.Handled = true;
    }
}
