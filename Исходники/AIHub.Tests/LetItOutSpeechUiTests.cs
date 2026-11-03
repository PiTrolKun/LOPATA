using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task SpeechButtonsCoverBothRolesAndAutoReadsOnlyNewCompleteAnswers(string language, bool dark) => Sta(() =>
    {
        using var fixture = new Fixture(language, dark);
        var reads = new List<(ChatSpeechMessage Message, bool Auto)>(); var automatic = true; var stopped = 0;
        fixture.Control.ConfigureSpeech((message, auto) => { reads.Add((message, auto)); return Task.CompletedTask; },
            () => { stopped++; return Task.CompletedTask; }, () => stopped++, () => automatic);
        fixture.Input.Text = "Мой вопрос"; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
        var buttons = fixture.Descendants.OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("LetItOut.Speech.")).ToArray();
        Assert.AreEqual(1, buttons.Length, "An incomplete streamed answer must not have a reading button.");
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual("Мой вопрос", reads.Single().Message.Text);
        fixture.Runtime.Complete(0, "Полный ответ."); Pump(() => !fixture.Control.IsWorking && reads.Count == 2);
        Assert.IsTrue(reads[1].Auto); Assert.AreEqual("Полный ответ.", reads[1].Message.Text);
        Assert.AreEqual(2, fixture.Descendants.OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("LetItOut.Speech.")));
        var assistant = reads[1].Message;
        fixture.Control.ShowSpeech(assistant.Key, "ChatSpeech.Stop");
        Assert.AreEqual("■", fixture.Button("LetItOut.Speech." + assistant.Key).Content);
        Assert.AreEqual(fixture.Localize("ChatSpeech.Stop"), AutomationProperties.GetName(fixture.Button("LetItOut.Speech." + assistant.Key)));
        fixture.Control.ShowSpeech(null, null);
        automatic = false; fixture.Input.Text = "Второй вопрос"; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 2);
        fixture.Runtime.Complete(1, "Ещё один ответ."); Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual(2, reads.Count);
        fixture.Control.ConfigureSpeech((message, auto) => { reads.Add((message, auto)); return Task.CompletedTask; }, () => Task.CompletedTask, () => stopped++, () => true);
        Assert.AreEqual(2, reads.Count, "Rendering existing history never auto-reads it.");
        fixture.Control.Visibility = Visibility.Collapsed; fixture.Window.UpdateLayout(); Assert.IsTrue(stopped > 1);
    });

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task ChatAudioSettingsReuseAllModelsAndPassTheActualTrialProfile(string language, bool dark) => Sta(() =>
    {
        using var fixture = new Fixture(language, dark);
        fixture.Window.Resources.MergedDictionaries.Add(fixture.Control.Resources);
        fixture.Window.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
        var root = Path.Combine(Path.GetTempPath(), "lopata-chat-voice-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var wavePath = Path.Combine(root, "test.wav"); using (var wave = new WaveFileWriter(wavePath, new WaveFormat(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
        var library = new ManagedModelLibraryStore(Path.Combine(root, "library")); library.Upsert(VideoNarratorCatalog.Card(root, "kokoro-heart"));
        VideoNarratorChoice? spoken = null; string? spokenText = null; var prepared = 0; var folders = 0;
        var trial = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extension = new VideoVoiceWindowExtension(fixture.Localize("ChatSpeech.Settings"), fixture.Localize("ChatSpeech.Auto"), ChatSpeechText.TestPhrase,
            (choice, text, _) => { spoken = choice; spokenText = text; return trial.Task; }, () => folders++, () => Task.CompletedTask, new Dictionary<string, VideoNarratorChoice>());
        var window = new VideoVoiceWindow(fixture.Window, fixture.Localize, new("vosk010", "ru", 100, new("0", Device: "cpu")), false,
            (_, _) => { prepared++; return Task.FromResult<VideoPreparedModels?>(new(root)); }, library, extension);
        try
        {
            window.Show(); window.UpdateLayout(); Field<MediaPlayer>("_player").Volume = 0;
            Assert.AreEqual(fixture.Localize("ChatSpeech.Settings"), window.Title);
            Assert.AreEqual(10, Field<ListBox>("_models").Items.Count);
            Assert.AreEqual(fixture.Localize("ChatSpeech.Auto"), Field<System.Windows.Controls.CheckBox>("_global").Content);
            Assert.IsTrue(Field<System.Windows.Controls.CheckBox>("_global").IsEnabled); Assert.IsFalse(window.Global);
            var models = Field<ListBox>("_models"); models.SelectedIndex = 1;
            Assert.IsTrue(Field<Button>("_remove").IsEnabled, "Chat settings allow managed Kokoro removal through the common guarded service.");
            Assert.IsTrue(Field<TextBox>("_trial").Text.StartsWith("Hello"), "Test content follows synthesis language, not UI language.");
            models.SelectedIndex = 5; Field<ComboBox>("_language").SelectedValue = "ru";
            Field<ComboBox>("_speaker").SelectedItem = "Aiden"; Field<TextBox>("_style").Text = "Говори спокойно";
            Field<ComboBox>("_device").SelectedValue = "cpu";
            var check = ScenarioNavigationTests.LogicalDescendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "ChatSpeech.Test");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => spoken is not null);
            Assert.AreEqual("qwen3-17b", spoken!.Id); Assert.AreEqual("Aiden", spoken.Options.Speaker);
            Assert.AreEqual("Говори спокойно", spoken.Options.Style); Assert.AreEqual("cpu", spoken.Options.Device);
            Assert.AreEqual(ChatSpeechText.TestPhrase("ru"), spokenText);
            window.UpdateLayout(); Assert.IsFalse(models.IsEnabled);
            var frame = (Border)models.Template.FindName("ListFrame", models);
            Assert.AreEqual(dark ? Colors.Black : Colors.White, ((SolidColorBrush)frame.Background).Color,
                "The actual disabled template must preserve the owner palette during generation.");
            Assert.AreEqual(dark ? Colors.White : Colors.Black, ((SolidColorBrush)models.Foreground).Color);
            trial.TrySetResult(wavePath); Pump(() => models.IsEnabled);
            Field<Button>("_download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => prepared == 1);
            var folder = ScenarioNavigationTests.LogicalDescendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "ChatSpeech.Folder");
            folder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(1, folders);
            Assert.IsNull(window.Choice, "Trials do not apply settings by themselves.");
        }
        finally { trial.TrySetResult(wavePath); window.Close(); Directory.Delete(root, true); }
        T Field<T>(string name) => (T)typeof(VideoVoiceWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    });
}
