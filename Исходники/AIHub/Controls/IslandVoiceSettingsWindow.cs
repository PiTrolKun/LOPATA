using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Lopata.Island;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal sealed class IslandVoiceSettingsWindow : Window
{
    internal IslandVoiceSettingsWindow(Window owner, Func<string, string> localize, bool russian, bool edit,
        Action? openUtility)
    {
        Owner = owner;
        Title = localize("Island.VoiceCommands");
        Width = 645;
        Height = 650;
        MinWidth = 420;
        MinHeight = 310;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative)
        });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");

        var body = new StackPanel { Margin = new(20) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var saved = IslandVoicePreferences.Read();
        AddText(body, localize("Island.VoiceDescription"), bold: true);
        AddText(body, localize("NabyScape." + IslandVoiceCommands.ScopeDescription));
        var wakeHint = AddText(body, string.Format(localize("Island.VoiceActivation"), saved.Wake(russian)));
        if (edit) AddEditors(body, localize, saved, russian, wakeHint);
        else if (openUtility is not null)
        {
            var settings = new Button
            {
                Content = localize("Island.VoiceOpenUtility"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new(0, 3, 0, 10)
            };
            settings.Click += (_, _) => { Close(); Dispatcher.BeginInvoke(openUtility); };
            body.Children.Add(settings);
        }

        foreach (var entry in IslandVoiceCommands.Entries)
        {
            AddText(body, entry.Example(russian), bold: true);
            AddText(body, localize("NabyScape." + entry.Description));
        }
    }

    private static void AddEditors(StackPanel body, Func<string, string> l, IslandVoicePreferences saved,
        bool russian, TextBlock wakeHint)
    {
        var wakeLabel = l("Island.VoiceWake");
        AddText(body, wakeLabel, bold: true);
        var wake = new TextBox { Text = saved.Wake(russian), MaxLength = 120, Margin = new(0, 0, 0, 9) };
        AutomationProperties.SetName(wake, wakeLabel);
        body.Children.Add(wake);
        string? path = saved.Reply(russian);
        var reply = AddText(body, path is null ? l("Island.VoiceDefaultReply") : Path.GetFileName(path));
        var status = AddText(body, "");
        var actions = new WrapPanel { Margin = new(0, 0, 0, 12) };
        body.Children.Add(actions);
        Button AddButton(string key, Action click)
        {
            var button = new Button { Content = l(key), Margin = new(0, 0, 8, 4) };
            button.Click += (_, _) => click();
            actions.Children.Add(button);
            return button;
        }
        AddButton("Island.VoiceChooseReply", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "WAV (*.wav)|*.wav", CheckFileExists = true };
            if (picker.ShowDialog(Window.GetWindow(body)) != true) return;
            try
            {
                using var reader = new WaveFileReader(picker.FileName);
                if (reader.TotalTime.TotalSeconds is <= 0 or > 12) throw new InvalidDataException();
                path = picker.FileName;
                reply.Text = Path.GetFileName(path);
                status.Text = "";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                status.Text = l("Island.VoiceInvalidReply");
            }
        });
        AddButton("Island.VoiceReset", () =>
        {
            wake.Text = IslandVoicePreferences.DefaultWake(russian);
            path = null;
            reply.Text = l("Island.VoiceDefaultReply");
            status.Text = "";
        });
        AddButton("Island.Apply", () =>
        {
            string phrase = wake.Text.Trim();
            if (IslandVoiceCommands.Normalize(phrase).Length == 0)
            {
                status.Text = l("Island.VoiceInvalidWake");
                return;
            }
            try
            {
                var current = IslandVoicePreferences.Read();
                (russian ? current with { RussianWake = phrase, RussianReply = path ?? "" }
                    : current with { EnglishWake = phrase, EnglishReply = path ?? "" }).Save();
                wakeHint.Text = string.Format(l("Island.VoiceActivation"), phrase);
                status.Text = l("Island.VoiceSaved");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = l("Island.Failed");
            }
        });
    }

    private static TextBlock AddText(StackPanel body, string text, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, bold ? 7 : 0, 0, 5),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, bold ? "TextPrimaryBrush" : "TextSecondaryBrush");
        body.Children.Add(label);
        return label;
    }
}
