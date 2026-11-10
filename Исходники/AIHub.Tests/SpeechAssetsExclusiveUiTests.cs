using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls.SpeechAssets;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Tests;

public sealed partial class SpeechAssetsUiTests
{
    [TestMethod]
    public void BundledMarkdownExamplesAreAvailableAsFilesAndImportable()
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var file = Path.Combine(SpeechAssetsControl.ExamplesDirectory, language == "en"
                ? "LOPATA_Notifications_and_Statuses_en.md" : "LOPATA_уведомления_и_статусы_ru.md");
            Assert.IsTrue(File.Exists(file), "An installed example must be an ordinary user-accessible file.");
            var rows = SpeechAssetMarkdown.Parse(File.ReadAllText(file));
            Assert.HasCount(1529, rows); Assert.IsTrue(rows.All(r => !string.IsNullOrWhiteSpace(r.Text)));
        }
    }

    [TestMethod, TestCategory("Native"), DataRow("ru"), DataRow("en")]
    public Task ChoosingCurrentTakeUnchecksOldTextAndAllowsConfirmation(string language) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_UI") != "1") Assert.Inconclusive("Explicit UI test required.");
        var root = Path.Combine(Path.GetTempPath(), "speech-exclusive-" + Guid.NewGuid().ToString("N"));
        var store = new SpeechAssetStore(root);
        var row = new SpeechAssetRow { SourceText = "MP3", Text = "эмпэ три", FileName = "0001" };
        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid().ToString("N"); var relative = "audio/" + id + ".wav"; var path = store.FileFor("00000000000000000000000000000001", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var wave = new WaveFileWriter(path, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
            row.Takes.Add(new() { Id = id, Text = i == 0 ? row.SourceText : row.Text, Selected = true,
                File = relative, Sha256 = SpeechAssetStore.Hash(path), Seconds = .1 });
        }
        var session = new SpeechAssetSession { Id = "00000000000000000000000000000001", Rows = [row] }; store.Save(session);
        File.WriteAllText(Path.Combine(root, "last.txt"), session.Id);
        var localizer = new LocalizationService(); localizer.Load(language);
        var owner = new Window { Width = 1400, Height = 850 };
        var control = new SpeechAssetsControl(owner, localizer.T, store); owner.Content = control; owner.Show();
        try
        {
            await Task.Delay(100); owner.UpdateLayout();
            var choices = Desc<RadioButton>(control).ToArray(); Assert.HasCount(2, choices);
            Assert.IsTrue(choices.All(c => c.IsChecked == false), "Ambiguous legacy selection requires a new explicit choice.");
            choices[1].IsChecked = true;
            Assert.IsFalse(choices[0].IsChecked); Assert.IsTrue(choices[1].IsChecked);
            Desc<Button>(control).Single(b => Equals(b.Content, localizer.T("SpeechAssets.Confirm"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(600);
            var result = store.Load(session.Id).Rows.Single(); Assert.IsTrue(result.Confirmed);
            Assert.AreEqual(row.Takes[1].Id, result.Takes.Single(t => t.Selected).Id); Assert.HasCount(2, result.Takes);
            choices[0].IsChecked = true; await Task.Delay(600);
            result = store.Load(session.Id).Rows.Single(); Assert.IsFalse(result.Confirmed); Assert.IsFalse(result.CanConfirm);
            Assert.IsFalse(choices[1].IsChecked); Assert.HasCount(2, result.Takes);
            foreach (var take in result.Takes) Assert.AreEqual(take.Sha256, SpeechAssetStore.Hash(store.FileFor(session.Id, take.File)));
        }
        finally { owner.Close(); Directory.Delete(root, true); }
    });
}
