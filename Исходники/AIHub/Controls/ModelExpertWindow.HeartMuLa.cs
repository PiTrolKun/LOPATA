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
    private void RenderHeartMuLa()
    {
        string Heart(string key) => _l("Music.HeartMuLa." + key);
        _parameters.Children.Add(MusicWishUi.Text(Heart("ExpertHint")));
        foreach (var group in MusicHeartMuLaCatalog.Parameters.GroupBy(p => p.Group)) {
            _parameters.Children.Add(MusicWishUi.Text(Heart("Group." + group.Key), true));
            foreach (var p in group) {
                var panel = new StackPanel { Margin = new(0, 4, 8, 12) };
                panel.Children.Add(MusicWishUi.Text(Heart("Parameter." + p.Key) + " · " + p.Key));
                panel.Children.Add(MusicWishUi.Text(Heart("Hint." + p.Key)));
                if (p.Choices is { } choices) {
                    var box = new ComboBox { ItemsSource = choices.Select(c => Heart("Choice." + c)).ToArray(), SelectedIndex = _draft.Integer(p.Key), MinWidth = 160 };
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
