using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class MusicAudioTaskWindow
{
    private sealed record ReferenceRow(MusicAudioInput Input, CheckBox Enabled, ComboBox Role, TextBox Start, TextBox End)
    {
        public MusicAudioInput Read() => Input with { Enabled = Enabled.IsChecked == true, Role = (MusicReferenceRole)Role.SelectedIndex, Start = MusicAudioTaskWindow.Read(Start), End = ReadOptional(End), Sha256 = null };
    }
    private const string AudioFilter = "Audio (*.wav;*.flac;*.mp3;*.opus;*.ogg)|*.wav;*.flac;*.mp3;*.opus;*.ogg";
    private void PickSource()
    { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = AudioFilter }; if (dialog.ShowDialog(this) == true) SetSource(dialog.FileName); }
    private void SetSource(string path) { _source.Text = path; _start.Text = "0"; _end.Text = ""; }
    private void AddReferences()
    { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = AudioFilter, Multiselect = true }; if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FileNames) AddReference(new(path)); }
    private void AddReference(MusicAudioInput input)
    {
        var panel = new StackPanel { Margin = new(0, 6, 0, 8) }; var head = new WrapPanel();
        var enabled = new CheckBox { Content = Path.GetFileName(input.Path), IsChecked = input.Enabled, ToolTip = input.Path };
        head.Children.Add(enabled); Id(enabled, "Reference.Enabled"); var role = new ComboBox { MinWidth = 160 };
        role.Items.Add(T(_variation == MusicStudioRuntime.Variation ? "RoleMusicalParameters" : "RoleSound")); role.Items.Add(T("RoleVoice")); role.SelectedIndex = (int)input.Role;
        role.IsEnabled = _variation == MusicAceCatalog.Variation; head.Children.Add(role);
        var start = new TextBox { Width = 80, Text = Number(input.Start) }; var end = new TextBox { Width = 80, Text = input.End is { } finish ? Number(finish) : "" };
        var row = new ReferenceRow(input, enabled, role, start, end); _rows.Add(row);
        head.Children.Add(MusicWishUi.Button(T("Listen"), "Music.Transform.Reference.Listen", () => Preview(input.Path)));
        head.Children.Add(MusicWishUi.Button(T("Remove"), "Music.Transform.Reference.Remove", () => { _rows.Remove(row); _references.Children.Remove(panel); }));
        panel.Children.Add(head); panel.Children.Add(Range("ReferenceRange", start, end)); _references.Children.Add(panel);
    }
    private void Preview(string path)
    {
        if (!File.Exists(path)) { _status.Text = T("MissingInput"); return; }
        var player = new MusicPlayerControl(); player.Localize(_l);
        var window = new Window { Title = T("Listen"), Owner = this, Content = player, Width = 600, Height = 260, ShowInTaskbar = false };
        MusicWishUi.PrepareWindow(window, T("Listen"), "Music.Transform.Preview", 600);
        window.Closed += (_, _) => player.Dispose(); window.Show();
        player.Select(new(path, Path.GetFileName(path), TimeSpan.Zero, DateTime.Now), true);
    }
}
