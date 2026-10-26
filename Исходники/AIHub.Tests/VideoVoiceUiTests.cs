using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceUiTests
{
    [TestMethod]
    public async Task DialogOffersCompletedModelsPreservesDraftAndReturnsProfileOnlyOnApply()
    {
        using var f = new VideoMusicTests.Folder();
        await OnUi(async () => {
            var l = new LocalizationService(); l.Load("ru"); var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
            owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = System.Windows.Media.Brushes.WhiteSmoke;
            owner.Resources["TextSecondaryBrush"] = System.Windows.Media.Brushes.LightGray; owner.Resources["LineBrush"] = System.Windows.Media.Brushes.SlateGray;
            owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 40));
            owner.Resources["AccentBrush"] = System.Windows.Media.Brushes.RoyalBlue; owner.Resources["StepBadgeBrush"] = System.Windows.Media.Brushes.SlateGray; owner.Resources["UiBodyFontSize"] = 14d; owner.Show();
            var preparationCalls = 0;
            var dialog = new VideoVoiceWindow(owner, l.T, new("kokoro-sveta", "ru", 100, new()), false,
                (_, _) => { preparationCalls++; return Task.FromResult<VideoPreparedModels?>(null); }, new(Path.Combine(f.Path, "library"))) { ShowActivated = false };
            T Field<T>(string name) => (T)typeof(VideoVoiceWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            try
            {
                dialog.Show(); await Task.Delay(80);
                var models = Field<ListBox>("_models"); Assert.AreEqual(10, models.Items.Count);
                models.SelectedIndex = 5; await Task.Delay(20); // Qwen 1.7B
                Field<ComboBox>("_device").SelectedValue = "gpu";
                Field<TextBox>("_style").Text = "Спокойно"; Field<ComboBox>("_speaker").SelectedItem = "Aiden";
                models.SelectedIndex = 3; Assert.IsTrue(Field<Button>("_apply").IsEnabled); Assert.IsTrue(Field<Button>("_download").IsEnabled);
                Assert.IsTrue(Field<TextBlock>("_description").Text.Contains("Латинские буквы"));
                models.SelectedIndex = 5; Assert.AreEqual("Спокойно", Field<TextBox>("_style").Text); Assert.AreEqual("Aiden", Field<ComboBox>("_speaker").SelectedItem);
                Assert.AreEqual("gpu", Field<ComboBox>("_device").SelectedValue);
                foreach (var index in Enumerable.Range(0, models.Items.Count))
                {
                    models.SelectedIndex = index;
                    Assert.IsTrue(Field<ComboBox>("_device").IsEnabled);
                    Assert.AreEqual(3, Field<ComboBox>("_device").Items.Count);
                }
                models.SelectedIndex = 5;
                Assert.IsFalse(Field<Button>("_play").IsEnabled); Assert.IsTrue(Field<TextBlock>("_sample").Text.Contains("нет"));
                Assert.IsNull(dialog.Choice); Assert.IsFalse(Field<TextBox>("_rate").IsEnabled);
                var examples = Field<List<Button>>("_exampleButtons"); Assert.AreEqual(20, examples.Count);
                foreach (var model in VideoNarratorCatalog.Models)
                    foreach (var language in new[] { "ru", "en" })
                    {
                        var button = examples.Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VideoEditor.Voices.Sample." + model.Id + "." + language);
                        var sample = VideoNarratorSamples.Find(model.Id, language);
                        Assert.AreEqual(sample is not null, button.IsEnabled);
                        Assert.IsTrue(sample is not null || VideoNarratorSamples.UnavailableReason(model.Id, language) is not null, "Every attempted case must have audio or an explicit failure.");
                        if (!model.Languages.Contains(language))
                        {
                            Assert.IsTrue(button.Content.ToString()!.Contains("эксп."));
                            if (sample is not null) Assert.IsTrue(sample.Experimental && model.Languages.Contains(sample.SynthesisLanguage!));
                        }
                    }
                var voskExample = examples.Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VideoEditor.Voices.Sample.vosk010.ru");
                Assert.IsTrue(voskExample.IsEnabled); Assert.IsNull(VideoNarratorSamples.UnavailableReason("vosk010", "ru"));
                var player = Field<MediaPlayer>("_player"); player.Volume = 0;
                foreach (var button in examples.Where(b => b.IsEnabled))
                {
                    var sample = (VideoNarratorSample)button.Tag;
                    if (sample.Id == "vosk010" && sample.Language == "ru")
                    {
                        Assert.AreEqual("Лопата для вас приготовила тестовую запись диктора. Лопата умеет говорить во многих сценариях.", sample.Text);
                        Assert.IsFalse(sample.Text.Any(char.IsAsciiLetter));
                    }
                    else Assert.IsTrue(sample.Text.Contains("Лопата") && sample.Text.Contains("LOPATA"));
                    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Opened(object? sender, EventArgs e) => opened.TrySetResult();
                    player.MediaOpened += Opened;
                    try
                    {
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.AreEqual(VideoNarratorSamples.AudioPath(sample), player.Source.LocalPath);
                        Assert.IsTrue(player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan.TotalSeconds > 0.1);
                        Assert.IsTrue(Field<Button>("_exampleStop").IsEnabled);
                        Field<Button>("_exampleStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.IsFalse(Field<Button>("_exampleStop").IsEnabled);
                    }
                    finally { player.MediaOpened -= Opened; }
                }
                Assert.AreEqual(0, preparationCalls, "Recorded examples must never prepare or download a model.");
                Assert.IsNull(dialog.Choice, "Examples must not apply a project profile.");
                var entriesDirectory = Path.Combine(f.Path, "library", "Entries");
                Assert.IsTrue(!Directory.Exists(entriesDirectory) || !Directory.EnumerateFiles(entriesDirectory, "*.json").Any());
                models.SelectedIndex = 3; models.ScrollIntoView(models.Items[3]); await Task.Delay(30);
                var output = Path.Combine("H:\\AI_HUB\\Тесты\\VideoVoiceGpu_20261026", "voice-window.png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                dialog.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(output); png.Save(stream);
            }
            finally { dialog.Close(); owner.Close(); }
        });
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
