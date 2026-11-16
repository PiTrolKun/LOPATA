using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services.Shorts;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

internal sealed class ShortsProgressPanel : StackPanel
{
    private static readonly string[] StageKeys = ["Shorts.StagePrepare", "Shorts.StageGenerate", "Shorts.StageUpscale", "Shorts.StageSave"];
    private readonly Border[] _stageBorders = new Border[StageKeys.Length];
    private readonly TextBlock[] _stageLabels = new TextBlock[StageKeys.Length];
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 6) };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100 };
    private Func<string, string> _localize;

    internal ShortsProgressPanel(Func<string, string> localize)
    {
        _localize = localize;
        AutomationProperties.SetAutomationId(this, "Shorts.Progress");
        AutomationProperties.SetAutomationId(_caption, "Shorts.Progress.Detail");
        AutomationProperties.SetAutomationId(_bar, "Shorts.Progress.Fragment");
        _bar.SetResourceReference(StyleProperty, "ShortsProgressBarStyle");
        var stages = new UniformGrid { Rows = 1 };
        for (var index = 0; index < StageKeys.Length; index++)
        {
            var label = _stageLabels[index] = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var box = _stageBorders[index] = new Border
            {
                Child = label, CornerRadius = new(5), BorderThickness = new(1), Padding = new(5), Margin = new(0, 0, 4, 0)
            };
            stages.Children.Add(box);
        }
        Children.Add(stages);
        Children.Add(_caption);
        Children.Add(_bar);
        Configure(localize);
    }

    internal void Configure(Func<string, string> localize)
    {
        _localize = localize;
        for (var index = 0; index < StageKeys.Length; index++)
            _stageLabels[index].Text = localize(StageKeys[index]);
    }

    internal void Show(ShortsProgress progress, bool active, string? stateKey)
    {
        for (var index = 0; index < StageKeys.Length; index++)
        {
            var current = index == (int)progress.Stage;
            _stageLabels[index].FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            _stageBorders[index].SetResourceReference(Border.BorderBrushProperty, current ? "AccentBrush" : "LineBrush");
            _stageBorders[index].SetResourceReference(Border.BackgroundProperty,
                index <= (int)progress.Stage ? "StepBadgeBrush" : "PanelBrush");
        }
        var key = stateKey ?? progress.Detail;
        var detail = _localize(key);
        if (detail == key) detail = _localize(StageKeys[(int)progress.Stage]);
        List<string> labels = [detail];
        if (progress.Fragment > 0)
        {
            var fragmentKey = progress.Total <= 0 ? "Shorts.FragmentNumber" :
                progress.EstimatedTotal ? "Shorts.FragmentEstimate" : "Shorts.FragmentCount";
            labels.Add(Format(fragmentKey, progress.Fragment, progress.Total));
        }
        if (progress.Steps > 0 && progress.Detail != "Shorts.FragmentReady")
            labels.Add(Format("Shorts.FragmentStep", progress.Step, progress.Steps));
        _caption.Text = string.Join(" · ", labels);
        _bar.IsIndeterminate = active && progress.Steps == 0;
        _bar.Value = progress.Steps > 0 ? 100d * progress.Step / progress.Steps : 0;
        _bar.Visibility = active || progress.Steps > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_bar, _caption.Text);
    }

    private string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, _localize(key), values);
}
