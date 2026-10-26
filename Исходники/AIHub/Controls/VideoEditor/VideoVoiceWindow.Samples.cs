using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoVoiceWindow
{
    private readonly Button _exampleStop = new();
    private readonly List<Button> _exampleButtons = [];

    private StackPanel ExampleControls(VideoNarratorModel model)
    {
        var container = new StackPanel();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0) };
        container.Children.Add(panel);
        foreach (var language in new[] { "ru", "en" })
        {
            var sample = VideoNarratorSamples.Find(model.Id, language);
            var button = new Button { Content = "▶ " + language.ToUpperInvariant() + (!model.Languages.Contains(language) ? " · " + L("ExperimentShort") : ""), Margin = new(0, 0, 6, 0),
                IsEnabled = sample is not null, Tag = sample,
                ToolTip = sample is not null ? (sample.Experimental ? L("RecordedLanguageExperiment") + "\n" : "") + string.Format(L("RecordedExampleDetails"), sample.Speaker, sample.Text)
                    : L(VideoNarratorSamples.UnavailableReason(model.Id, language) ?? (model.Languages.Contains(language) ? "RecordedExampleMissing" : "RecordedLanguageUnsupported")) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "VideoEditor.Voices.Sample." + model.Id + "." + language);
            ToolTipService.SetShowOnDisabled(button, true);
            System.Windows.Automation.AutomationProperties.SetName(button, model.Name + " · " + language.ToUpperInvariant() + " · " + L("RecordedExample"));
            if (sample is not null) button.Click += (_, e) => { e.Handled = true; PlayExample(sample); };
            panel.Children.Add(button); _exampleButtons.Add(button);
        }
        foreach (var failed in new[] { "ru", "en" }.Select(language => new { language, reason = VideoNarratorSamples.UnavailableReason(model.Id, language) }).Where(f => f.reason is not null).GroupBy(f => f.reason))
            container.Children.Add(new TextBlock { Text = string.Join(" / ", failed.Select(f => f.language.ToUpperInvariant())) + ": " + L(failed.Key!), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        return container;
    }
    private void PlayExample(VideoNarratorSample sample)
    {
        if (_busy) return;
        _player.Stop(); _player.Open(new Uri(VideoNarratorSamples.AudioPath(sample))); _player.Play();
        _exampleStop.IsEnabled = true;
        _status.Text = string.Format(L("RecordedExamplePlaying"), VideoNarratorCatalog.Get(sample.Id).Name, sample.Speaker, sample.Language.ToUpperInvariant())
            + (sample.Experimental ? "\n" + L("RecordedLanguageExperiment") : "") + "\n" + sample.Text;
    }
    private void StopExample() { _player.Stop(); _exampleStop.IsEnabled = false; }
}
