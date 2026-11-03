using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using AIHub.Services;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Path = System.Windows.Shapes.Path;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private void ConfigureSendIcon()
    {
        _send.Width = _send.Height = 44; _send.MinWidth = _send.MinHeight = 0;
        _send.Padding = new(0); _send.Margin = new(8, 0, 0, 0);
        _send.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        _send.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        var arrow = new Path { Data = Geometry.Parse("M 12,20 L 12,4 M 5,11 L 12,4 L 19,11"),
            StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 20, Height = 20, Stretch = Stretch.Uniform };
        arrow.SetBinding(Path.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = _send });
        _send.Content = arrow;
    }

    private void RenderStatus(string key)
    {
        var state = key switch {
            "LetItOut.Ready" or "LetItOut.Export.Saved" => "Ready",
            "LetItOut.Working" or "LetItOut.Library.Naming" or "LetItOut.Attachments.AudioWorking" or "LetItOut.Export.Working" => "Working",
            "LetItOut.Warming" => "Loading",
            "LetItOut.Busy" or "LetItOut.WarmDeferred" => "Waiting",
            "LetItOut.Paused" or "LetItOut.WarmPaused" => "Paused",
            "LetItOut.Stopped" or "LetItOut.WarmStopped" => "Stopped",
            "LetItOut.ContextFull" => "ContextFull",
            "LetItOut.RamRequired" or "LetItOut.VramChanged" => "Memory",
            "LetItOut.NotPrepared" or "LetItOut.ModelChanged" or "LetItOut.Preparation.EngineRequired" => "NotReady",
            _ => "Error"
        };
        var detail = _l(key);
        _status.Text = string.Format(_l("LetItOut.AiStatus"), _l("LetItOut.AiState." + state));
        _status.ToolTip = new TextBlock { Text = detail + "\n" + LetItOutModel.DisplayName + "\n" + _l("LetItOut.Mode." + _mode),
            MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHelpText(_status, detail);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
    }
}
