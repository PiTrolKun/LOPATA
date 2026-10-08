using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class ModelExpertWindow
{
    private void RenderAce()
    {
        string Ace(string key) => _l("Music.Ace." + key);
        _parameters.Children.Add(MusicWishUi.Text(Ace("ExpertHint")));
        foreach (var group in MusicAceCatalog.Parameters.Select(p => p.Group).Concat(MusicAceCatalog.TextParameters.Select(p => p.Group)).Distinct()) {
            _parameters.Children.Add(MusicWishUi.Text(Ace("Group." + group), true));
            foreach (var p in MusicAceCatalog.Parameters.Where(p => p.Group == group)) {
                var panel = new StackPanel { Margin = new(0, 3, 8, 12) };
                panel.Children.Add(MusicWishUi.Text(Ace("Parameter." + p.Key) + " · " + p.Key));
                panel.Children.Add(MusicWishUi.Text(Ace("Hint." + p.Key)));
                var follow = MusicWishUi.Button("", "Music.Expert.Follow." + p.Key, () => Guard(() => {
                    _draft = MusicTuningProfile.Release(_draft, p.Key); Render(); Changed();
                }));
                _pinButtons[p.Key] = follow; UpdatePin(p.Key); panel.Children.Add(follow);
                if (p.Choices is { } choices) {
                    var box = new ComboBox { ItemsSource = choices, SelectedIndex = checked((int)(_draft.Get(p.Key) - p.Minimum)), MinWidth = 160,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(box, "Music.Expert." + p.Key);
                    box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { _draft.Values[p.Key] = p.Minimum + box.SelectedIndex; Edited(p.Key); } };
                    panel.Children.Add(box);
                } else if (p.Minimum == 0 && p.Maximum == 1 && p.Step == 1) {
                    var check = new CheckBox { Content = Ace("Enabled"), IsChecked = _draft.Get(p.Key) == 1, IsEnabled = p.Key != "use_flash_attention" };
                    AutomationProperties.SetAutomationId(check, "Music.Expert." + p.Key);
                    check.Checked += (_, _) => { _draft.Values[p.Key] = 1; Edited(p.Key); };
                    check.Unchecked += (_, _) => { _draft.Values[p.Key] = 0; Edited(p.Key); };
                    panel.Children.Add(check);
                } else {
                    var input = new TextBox { Text = Number(_draft.Get(p.Key)), MinWidth = 150, MaxWidth = 220, Padding = new(4),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(input, "Music.Expert." + p.Key);
                    input.TextChanged += (_, _) => {
                        _draft.Values[p.Key] = double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                            double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
                        Edited(p.Key);
                    };
                    panel.Children.Add(input);
                }
                _parameters.Children.Add(panel);
            }
            foreach (var p in MusicAceCatalog.TextParameters.Where(p => p.Group == group)) {
                var panel = new StackPanel { Margin = new(0, 3, 8, 12) };
                panel.Children.Add(MusicWishUi.Text(Ace("Parameter." + p.Key) + " · " + p.Key));
                panel.Children.Add(MusicWishUi.Text(Ace("Hint." + p.Key)));
                if (p.Choices is { } choices) {
                    var box = new ComboBox { ItemsSource = choices, SelectedItem = _draft.TextValues[p.Key], MinWidth = 160,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
                    AutomationProperties.SetAutomationId(box, "Music.Expert." + p.Key);
                    box.SelectionChanged += (_, _) => { if (box.SelectedItem is string value) { _draft.TextValues[p.Key] = value; AceTextEdited(); } };
                    panel.Children.Add(box);
                } else {
                    var input = new TextBox { Text = _draft.TextValues[p.Key], MinWidth = 200, MaxWidth = 700, Padding = new(4) };
                    AutomationProperties.SetAutomationId(input, "Music.Expert." + p.Key);
                    input.TextChanged += (_, _) => { _draft.TextValues[p.Key] = input.Text; AceTextEdited(); };
                    panel.Children.Add(input);
                }
                _parameters.Children.Add(panel);
            }
        }
        _parameters.Children.Add(MusicWishUi.Text(Ace("Unsupported")));
        _parameters.Children.Add(MusicWishUi.Text(L("Managed")));
    }
    private void AceTextEdited()
    {
        _draft = _draft with { Tuning = MusicTuningProfile.State(_draft) with { SimpleModified = true, ExpertModified = true } };
        Changed();
    }
}
