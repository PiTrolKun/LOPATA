using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

/// <summary>Explicit authoring operation; never runs inference in routine tests.</summary>
[TestClass, DoNotParallelize]
public sealed class VideoVoiceSamplePreparationTests
{
    [TestMethod]
    public async Task PrepareRequestedBundledSamples()
    {
        var destination = Environment.GetEnvironmentVariable("LOPATA_VOICE_EXAMPLES_OUTPUT");
        var modelsRoot = Environment.GetEnvironmentVariable("LOPATA_VOICE_MODELS_ROOT");
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(modelsRoot))
        { Assert.Inconclusive("Explicit sample authoring paths are required."); return; }
        Directory.CreateDirectory(destination);
        var evidence = Path.Combine(destination, "evidence"); Directory.CreateDirectory(evidence);
        var library = Path.Combine(evidence, "library"); Directory.CreateDirectory(Path.Combine(library, "Entries"));
        foreach (var file in Directory.EnumerateFiles(AppDataPaths.ManagedModelLibraryEntriesDirectory, "*.json"))
            File.Copy(file, Path.Combine(library, "Entries", Path.GetFileName(file)), overwrite: true);
        using var audit = ComponentAudit.UseTestStore(Environment.GetEnvironmentVariable("LOPATA_VOICE_EXAMPLES_AUDIT_STORE") ?? Path.Combine(evidence, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var manifest = Path.Combine(destination, "samples.json");
        var previous = File.Exists(manifest) ? JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(manifest))! : [];
        var entries = previous.Cast<object>().ToList();
        var failures = new List<object>();
        var skip = (Environment.GetEnvironmentVariable("LOPATA_VOICE_EXAMPLES_SKIP") ?? "").Split(',');
        var requestedLanguage = Environment.GetEnvironmentVariable("LOPATA_VOICE_EXAMPLES_LANGUAGE");
        var languages = new[] { "ru", "en" }.Where(l => string.IsNullOrEmpty(requestedLanguage) || l == requestedLanguage).ToArray();
        Assert.IsTrue(languages.Length > 0, "Only RU and EN samples are supported.");
        try
        {
            var store = new ManagedModelLibraryStore(library);
            using var prepare = new VideoModelPreparation(store);
            foreach (var model in VideoNarratorCatalog.Models.Where(m => !skip.Contains(m.Id)))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                Assert.IsTrue(await prepare.PrepareAsync(modelsRoot, new("speech", [model.Id]), false, null, deadline.Token), model.Id);
                using var narrator = new VideoNarratorRegistry(store).Create(model.Id);
                foreach (var language in languages)
                {
                    var text = language == "ru"
                        ? "Лопата для вас приготовила тестовую запись диктора. LOPATA умеет говорить во многих сценариях."
                        : "Лопата has prepared a narrator test recording for you. LOPATA can speak in many scenarios.";
                    if (model.Id == "vosk010" && language == "ru") text = text.Replace("LOPATA", "Лопата", StringComparison.Ordinal);
                    var name = model.Id + "-" + language + ".wav";
                    var target = Path.Combine(destination, name);
                    if (File.Exists(target))
                    {
                        Assert.IsTrue(previous.Any(e => e.GetProperty("file").GetString() == name && e.GetProperty("text").GetString() == text), "Resume only a matching recorded sample.");
                        continue;
                    }
                    Console.WriteLine("Recording " + model.Id + " / " + language);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    // Foreign-language text is tested through this model's native pipeline.
                    // Never select a different model just because it supports the text's language.
                    var experimental = !model.Languages.Contains(language);
                    var synthesisLanguage = experimental ? model.Languages[0] : language;
                    try
                    {
                        var audio = narrator is IConfiguredVideoNarrator configured
                            ? await configured.GenerateAsync(model.Id, synthesisLanguage, text, 100, new(model.DefaultSpeaker), deadline.Token)
                            : await narrator.GenerateAsync(model.Id, synthesisLanguage, text, 100, deadline.Token);
                        using var wave = new NAudio.Wave.WaveFileReader(audio.Path);
                        Assert.AreEqual(16, wave.WaveFormat.BitsPerSample); Assert.IsTrue(wave.TotalTime.TotalSeconds > 0.1);
                        var buffer = new byte[4096]; double peak = 0; int read;
                        while ((read = wave.Read(buffer, 0, buffer.Length)) > 0)
                            for (var i = 0; i + 1 < read; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
                        Assert.IsTrue(peak > 0.001, "The example must contain non-silent PCM audio.");
                        var bytes = await File.ReadAllBytesAsync(audio.Path, deadline.Token);
                        Assert.IsTrue(bytes.AsSpan().IndexOf("LOPATA_PROVENANCE"u8) >= 0);
                        File.Copy(audio.Path, target);
                        var entry = new { id = model.Id, language, synthesisLanguage, experimental, speaker = model.DefaultSpeaker, file = name, text,
                            seconds = wave.TotalTime.TotalSeconds, generationSeconds = timer.Elapsed.TotalSeconds,
                            license = model.License, source = model.Source, models = audio.Models };
                        entries.Add(entry);
                        await File.WriteAllTextAsync(Path.Combine(evidence, model.Id + "-" + language + ".json"), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
                        await File.WriteAllTextAsync(Path.Combine(destination, "samples.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch (Exception error)
                    {
                        await File.WriteAllTextAsync(Path.Combine(evidence, model.Id + "-" + language + ".failure.txt"), error.ToString());
                        failures.Add(new { model.Id, Language = language, ReasonKey = model.Id == "vosk010" && error.Message.Contains("KeyError") ? "RecordedMixedScriptFailure" : "RecordedExampleSynthesisFailure" });
                        await File.WriteAllTextAsync(Path.Combine(destination, "unavailable.json"), JsonSerializer.Serialize(failures, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine("FAILED " + model.Id + " / " + language + ": " + error.Message);
                    }
                }
            }
            await File.WriteAllTextAsync(Path.Combine(destination, "unavailable.json"), JsonSerializer.Serialize(failures, new JsonSerializerOptions { WriteIndented = true }));
            Assert.AreEqual(VideoNarratorCatalog.Models.Count(m => !skip.Contains(m.Id)) * languages.Length, entries.Count + failures.Count, "Every requested case must have audio or an explicit recorded failure.");
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
