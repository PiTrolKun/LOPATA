using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class ModelExpertWindow
{
    private void RenderDiffRhythm()
    {
        string Diff(string key) => _l("Music.DiffRhythm." + key);
        _parameters.Children.Add(MusicWishUi.Text(Diff("ExpertHint")));
        foreach (var group in MusicDiffRhythmCatalog.Parameters.GroupBy(p => p.Group)) {
            _parameters.Children.Add(MusicWishUi.Text(Diff("Group." + group.Key), true));
            foreach (var p in group) {
                var panel = new StackPanel { Margin = new(0, 4, 8, 12) };
                panel.Children.Add(MusicWishUi.Text(Diff("Parameter." + p.Key) + " · " + p.Key));
                panel.Children.Add(MusicWishUi.Text(Diff("Hint." + p.Key)));
                if (p.Choices is { } choices) {
                    var box = new ComboBox { ItemsSource = choices, SelectedIndex = _draft.Integer(p.Key), MinWidth = 160 };
                    AutomationProperties.SetAutomationId(box, "Music.Expert." + p.Key);
                    box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { _draft.Values[p.Key] = box.SelectedIndex; Changed(); } };
                    panel.Children.Add(box);
                } else {
                    var input = new TextBox { Text = Number(_draft.Get(p.Key)), MinWidth = 150, MaxWidth = 220, Padding = new(4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(input, "Music.Expert." + p.Key);
                    input.TextChanged += (_, _) => {
                        _draft.Values[p.Key] = double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
                        Changed();
                    };
                    panel.Children.Add(input);
                }
                _parameters.Children.Add(panel);
            }
        }
    }
}
