using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderSpeechInstalledTests
{
    [TestMethod, DataRow("ru", "Привет! Проверяем чтение документа."), DataRow("en", "Hello! This is a document reading test.")]
    public async Task InstalledDefaultQwenReadsBothLanguagesAndExportsDocumentOwnedAudio(string language, string text)
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_READER_SPEECH_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt in to installed-model synthesis."); return; }
        var folder = Path.Combine(output, language); Directory.CreateDirectory(folder);
        var library = new ManagedModelLibraryStore();
        var voice = new ReaderAiSettings().Speech.Choice() with { Language = language };
        Assert.AreEqual("qwen3-17b", voice.Id); Assert.AreEqual("Ryan", voice.Options.Speaker);
        var card = library.Load(VideoNarratorCatalog.ArtifactId(voice.Id)); Assert.IsNotNull(card);
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new(Path.Combine(folder, "background", "operation.json")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var session = new ReaderSpeechSession(Path.Combine(folder, "temporary"));
        try
        {
            using var preparation = new VideoModelPreparation(library);
            Assert.IsTrue(await preparation.PrepareAsync(card.ModelsRoot, new("speech", [voice.Id]), false, null, timeout.Token), "No downloads in this probe.");
            var timer = Stopwatch.StartNew();
            var project = Path.Combine(folder, "speech.LOPATAdoxAI");
            var work = await OfficeTextWork.OpenAsync(null, timeout.Token);
            ReaderSpeechResult result;
            try
            {
                var journal = new OfficeAiJournal(OfficeSheets.PrimaryId, "Speech", (entry, file) => work.UpsertAiEntryAsync(entry, timeout.Token, file));
                result = await new ReaderSpeechWork(session).GenerateAsync(new(Guid.NewGuid().ToString("N"), text, voice, "Reader voice probe"), null, timeout.Token, journal: journal);
                await work.SaveAsync(project, timeout.Token);
            }
            finally { work.Close(true); }
            var saved = Path.Combine(folder, "reader-qwen.wav");
            await ReaderSpeechExport.SaveAsync(result.Path, saved, Path.Combine(folder, "document.pdf"));
            using (var reader = new WaveFileReader(saved))
            { Assert.IsTrue(reader.TotalTime.TotalSeconds > .5); Assert.AreEqual(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding); }
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(saved, timeout.Token));
            StringAssert.Contains(bytes, "office/reader/speech"); StringAssert.Contains(bytes, card.Revision);
            session.Dispose(); Assert.IsFalse(Directory.Exists(session.DirectoryPath)); Assert.IsTrue(File.Exists(saved));
            work = await OfficeTextWork.OpenAsync(project, timeout.Token);
            OfficeAiEntry recorded;
            try
            {
                recorded = work.AiEntries.Single();
                Assert.AreEqual("completed", recorded.Status);
                Assert.AreEqual(text, recorded.Request);
                Assert.IsNull(recorded.SystemPrompt);
                Assert.AreEqual(OfficeSheets.PrimaryId, recorded.SheetId);
                CollectionAssert.AreEqual(result.Models, recorded.Models);
                var extracted = Path.Combine(folder, "from-project.wav");
                await work.ExportAiResultAsync(recorded.Id, extracted, timeout.Token);
                CollectionAssert.AreEqual(File.ReadAllBytes(saved), File.ReadAllBytes(extracted));
            }
            finally { work.Close(true); }
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { voice, elapsedSeconds = timer.Elapsed.TotalSeconds, audioSeconds = result.Seconds,
                temporaryDeleted = true, exportPreserved = true, recorded, projectAudioRestored = true }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
            OfficeReaderTextTests.MakePdf(folder, "reader-ui.pdf");
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
}
