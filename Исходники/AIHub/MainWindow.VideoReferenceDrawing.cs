using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub;

public partial class MainWindow
{
    private VideoReferencePromptsWindow? _manualReferencePrompts;
    private Window? _manualReferenceWindow;
    private ImageGenerationControl? _manualReferenceGenerator;
    private void OpenManualReferenceDrawing(VideoPreparationProject p)
    {
        _manualReferencePrompts?.Close();
        _manualReferencePrompts = new(this, L, p); _manualReferencePrompts.Show();
        if (_manualReferenceWindow is null)
        {
            var page = new ImageGenerationControl(); _manualReferenceGenerator = page;
            ConfigureImageGeneration(page);
            var window = new Window(); VideoReferenceWindowUi.Configure(window, this, L("Generation.Title"));
            var root = new DockPanel(); var tools = new WrapPanel(); DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
            tools.Children.Add(MusicWishUi.Button(L("Generation.ChangeModel"), "Generation.ChangeModel", () => page.ShowModelMenu(tools)));
            tools.Children.Add(MusicWishUi.Button(L("Generation.ClearWorkspace"), "Generation.ClearWorkspace", page.ClearWorkspace));
            root.Children.Add(page); window.Content = root;
            // Hiding preserves the ordinary generator's pending operation and result workspace.
            window.Closing += (_, e) => { if (!_processShutdownComplete && !_sessionEnding && !Dispatcher.HasShutdownStarted) { e.Cancel = true; window.Hide(); } };
            Closed += (_, _) => page.DisposeRuntime(); _manualReferenceWindow = window;
        }
        _manualReferenceWindow.Show(); _manualReferenceWindow.Activate();
    }
}
