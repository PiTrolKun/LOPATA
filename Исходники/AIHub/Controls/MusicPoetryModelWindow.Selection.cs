using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using AIHub.Models;

namespace AIHub.Controls;

public sealed partial class MusicPoetryModelWindow
{
    private string? _selectedPath;
    private readonly List<(Border Frame, TextBlock Name, Button Select, Expander Expander, Func<DebugModelInfo?> Model)> _selectionCards = [];

    private void RefreshSelection(string? path)
    {
        _selectedPath = path;
        foreach (var (frame, name, button, expander, model) in _selectionCards)
        {
            var available = model();
            var current = available?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true;
            frame.SetResourceReference(Border.BorderBrushProperty, current ? "AccentBrush" : "PanelBrush");
            name.SetResourceReference(TextBlock.ForegroundProperty, current ? "AccentBrush" : "TextPrimaryBrush");
            var status = current ? _downloadLocalize("Current") : "";
            AutomationProperties.SetItemStatus(expander, status);
            AutomationProperties.SetItemStatus(frame, status);
            button.Content = _downloadLocalize(current ? "Current" : "Select");
            button.IsEnabled = available is not null && !current && !IsDownloading && !IsRemoving;
        }
    }
}
