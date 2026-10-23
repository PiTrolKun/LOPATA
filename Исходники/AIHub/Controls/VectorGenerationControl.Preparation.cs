using System.Windows;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl
{
    private int _page;
    private string _modelId = VectorModels.Ridge;
    private VectorPreparationState? _preparationState;
    private ProgressBar? _preparationProgress;

    public void OpenSelection()
    {
        if (HasPending() && ApplicationBackgroundOperations.Current?.State is { } pending)
        {
            try
            {
                _modelId = pending.Kind == VectorPreparation.BackgroundKind ? VectorModels.RestorePreparationModel(pending.Input)
                    : VectorModels.Validate(pending.Input.Deserialize<VectorRequest>()?.ModelId ?? VectorModels.Ridge);
            }
            catch (System.IO.InvalidDataException)
            {
                // A retired request must never be silently rerouted to Qwen; keep its Cancel action available.
                _modelId = VectorModels.Ridge; _page = 1; _preparationState = null;
                _statusKey = "Error.Model"; _statusDetail = ""; Render(); return;
            }
        }
        _page = _busy || HasPending()
            ? ApplicationBackgroundOperations.Current?.State?.Kind == VectorPreparation.BackgroundKind ? 1 : 2
            : 0;
        if (_page == 0) { _preparationState = null; _statusDetail = ""; }
        Render();
    }
    public bool GoBack()
    {
        if (_page == 0) return false;
        if (_page == 1)
        {
            if (ApplicationBackgroundOperations.Current?.State?.Kind != VectorStore.BackgroundKind || !HasPending()) _cancel?.Cancel();
            _preparationState = null;
        }
        _page--; Render(); return true;
    }
    private Button TextAction(string key, Func<Task> action, string id, bool enabled = true)
    {
        var button = Action(key, L(key), action, id, enabled);
        button.Padding = new Thickness(14, 8, 14, 8); return button;
    }
    private void RenderSelection()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Text(L("Title"), true));
        panel.Children.Add(Text(L("ChooseModel")));
        panel.Children.Add(Text(L("Examples.Note")));
        var row = new Grid { Margin = new Thickness(0, 8, 0, 16) };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new());
        var description = new StackPanel();
        description.Children.Add(Text(LiteraryModelLocation.DisplayName, true));
        foreach (var key in new[] { "Model.Description", "Model.Pros", "Model.Cons", "Model.Resources" })
            description.Children.Add(Text(L(key)));
        var card = new Button { Content = description, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(20), Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top, IsEnabled = !_busy && !HasPending() };
        AutomationProperties.SetAutomationId(card, "Vector.Model.ridge");
        card.Click += async (_, _) => await SelectModelAsync(VectorModels.Ridge);
        row.Children.Add(card);
        var examples = new Grid(); examples.ColumnDefinitions.Add(new()); examples.ColumnDefinitions.Add(new());
        examples.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = card });
        Grid.SetColumn(examples, 1); row.Children.Add(examples);
        for (var i = 0; i < VectorExamples.All.Count; i++)
        {
            var example = VectorExamples.All[i];
            var content = new Grid(); content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.Children.Add(new System.Windows.Controls.Image { Source = VectorExamples.Image(example),
                Stretch = System.Windows.Media.Stretch.Uniform });
            var caption = Text(L("Examples." + example.Id)); Grid.SetRow(caption, 1); content.Children.Add(caption);
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0), ToolTip = L("Examples.Open") };
            AutomationProperties.SetAutomationId(button, "Vector.Example." + example.Id);
            button.Click += (_, _) => { try { OpenExample(example); } catch (Exception error) { ShowError(error); } };
            Grid.SetColumn(button, i); examples.Children.Add(button);
        }
        panel.Children.Add(row);
        if (_busy || HasPending()) panel.Children.Add(TextAction("Workspace", () => { _page = 2; Render(); return Task.CompletedTask; }, "Workspace"));
        _status = Text(_statusDetail); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void RenderPreparation()
    {
        var panel = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 24, 24, 12) };
        panel.Children.Add(Text(L("Preparation.Title") + " · " + VectorModels.Name(_modelId), true));
        panel.Children.Add(Text(L("Preparation.Hint")));
        string State(bool? ready) => L(ready is null ? "Preparation.Checking" : ready.Value ? "Preparation.Available" : "Preparation.Missing");
        panel.Children.Add(Text(VectorModels.Name(_modelId) + " — " + State(_preparationState?.ModelReady)));
        panel.Children.Add(Text("resvg " + VectorRenderer.Revision + " — " + State(_preparationState?.RendererReady)));
        if (_preparationState is { RendererReady: false }) panel.Children.Add(Text(L("Error.RendererMissing")));
        _status = Text(string.IsNullOrEmpty(_statusDetail) ? L(_statusKey) : _statusDetail); panel.Children.Add(_status);
        _preparationProgress = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100, IsIndeterminate = _busy,
            Value = _preparationState?.Ready == true ? 100 : 0, Margin = new Thickness(0, 10, 0, 14) };
        _preparationProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _preparationProgress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush"); panel.Children.Add(_preparationProgress);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (_preparationState is { ModelReady: false, RendererReady: true })
        {
            var download = TextAction("Download", PrepareAsync, "Download", !_busy && !HasPending());
            download.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle"); actions.Children.Add(download);
        }
        actions.Children.Add(TextAction("Verify", CheckPreparationAsync, "Verify", !_busy && !HasPending()));
        var next = TextAction("Next", ContinuePreparedAsync, "OpenChat", !_busy && !HasPending() && _preparationState?.Ready == true && VectorModels.CanGenerate(_modelId));
        next.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle"); actions.Children.Add(next);
        if (_busy || HasPending())
        {
            if (HasPending()) actions.Children.Add(TextAction("PauseResume", PauseResumeAsync, "PauseResume"));
            actions.Children.Add(TextAction("Cancel", CancelAsync, "Cancel"));
        }
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private IProgress<double> PreparationProgress()
    {
        var operation = _cancel;
        return new Progress<double>(p =>
        {
            if (_page != 1 || !_busy || _cancel != operation) return;
            if (_status is not null) _status.Text = L(_statusKey) + " · " + p.ToString("F0") + "%";
            if (_preparationProgress is { } bar) { bar.IsIndeterminate = false; bar.Value = p; }
        });
    }
    private async Task CheckPreparationAsync()
    {
        if (_busy || HasPending()) return;
        _busy = true; _cancel = new(); _preparationState = null; _statusKey = "Verifying"; _statusDetail = ""; Render();
        try
        {
            _preparationState = await VectorPreparation.CheckAsync(_modelId, PreparationProgress(), _cancel.Token);
            _statusKey = PreparationReadyKey();
        }
        catch (OperationCanceledException) { _statusKey = "Canceled"; }
        catch (Exception error) { _statusKey = "Error"; ShowError(error); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; Render(); }
    }
    private async Task ContinuePreparedAsync()
    {
        if (_busy || HasPending() || _preparationState?.Ready != true || !VectorModels.CanGenerate(_modelId)) return;
        // The entry check never authorizes a later use after files or audit mode changed.
        await PrepareAsync(CancellationToken.None, null, false);
        if (_page != 1 || _preparationState?.Ready != true || _busy || HasPending()) return;
        _page = 2; _statusKey = "Ready"; _statusDetail = ""; Render();
    }
    private async Task SelectModelAsync(string modelId)
    {
        if (_busy || HasPending()) return;
        _modelId = VectorModels.Validate(modelId); _page = 1; _preparationState = null; _statusDetail = "";
        await CheckPreparationAsync();
    }
    private string PreparationReadyKey() => _preparationState?.Ready == true ? "Preparation.Ready" : "Preparation.NeedsDownload";
}
