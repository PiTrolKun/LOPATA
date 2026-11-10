using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls.SpeechAssets;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using NAudio.Wave;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Tests;

public sealed partial class SpeechAssetsUiTests
{
    [TestMethod, TestCategory("Native")]
    public Task LargeRecoveryKeepsListIdentityAndDispatcherAndNeverRegeneratesReadyTakes() => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_UI") != "1") Assert.Inconclusive("Explicit UI test required.");
        var root = Path.Combine(Path.GetTempPath(), "speech-recovery-" + Guid.NewGuid().ToString("N"));
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(root, "background.json")));
        ApplicationBackgroundOperations.Current = controller;
        var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var project = new SpeechAssetSession { Voice = new("silero56", "ru", 100, new("xenia")),
            Rows = SpeechAssetMarkdown.Parse(string.Join("\n", Enumerable.Range(1, 1500).Select(i => "- Line " + i))) };
        var items = project.Rows.Select(r => new SpeechAssetJobItem(r.Id, Guid.NewGuid().ToString("N"), r.Text)).ToArray();
        var before = new Dictionary<string, string>();
        for (var i = 0; i < 1497; i++)
        {
            var relative = "audio/" + items[i].TakeId + ".wav"; var file = store.FileFor(project.Id, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var wave = new WaveFileWriter(file, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
            var hash = SpeechAssetStore.Hash(file); before[relative] = hash;
            project.Rows[i].Takes.Add(new() { Id = items[i].TakeId, File = relative, Text = items[i].Text,
                Sha256 = hash, Seconds = .1, Voice = project.Voice, Selected = true });
        }
        store.FixProject(project);
        var job = new SpeechAssetJob(project.Id, project.Voice, items, store.Root, true, Guid.NewGuid().ToString("N"));
        store.BeginRun(project, job);
        var calls = 0; var registry = new VideoNarratorRegistry();
        registry.Register("silero56", () => new SpeechAssetResidentTests.Probe(root, async token =>
        { Interlocked.Increment(ref calls); await Task.Delay(250, token); }, () => { }));
        var resident = new SpeechAssetModelSession(registry, Path.Combine(root, "warm"));
        var owner = new Window { Width = 1100, Height = 700 };
        var control = new SpeechAssetsControl(owner, key => key, store, resident, (_, _) => Task.FromResult(true));
        owner.Content = control; owner.Show(); control.OpenSavedSession(project.Id); owner.UpdateLayout();
        var list = Desc<ListBox>(control).Single(); var source = list.ItemsSource;
        var resets = 0;
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));
        EventHandler changed = (_, _) => resets++; descriptor.AddValueChanged(list, changed);
        var beats = 0; var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        pulse.Tick += (_, _) => { beats++; if (beats % 3 == 0) { owner.Left += beats % 2 == 0 ? 20 : -20; owner.Width += beats % 2 == 0 ? 1 : -1; } };
        try
        {
            pulse.Start();
            var state = new BackgroundOperationState { Kind = SpeechAssetWork.Kind, Project = project.Id,
                Title = "Recovery", Input = JsonSerializer.SerializeToElement(job) };
            await controller.RunAsync(state, async token =>
            {
                // The real restore callback enters the scenario from inside the scheduler's operation scope.
                // It must not start an independent warmup alongside the restored job.
                control.EnterScenario(); await control.ResumeAsync(state, token); return true;
            }, () => resident.RetireAsync(), default);
            Assert.AreEqual(3, calls); Assert.IsTrue(beats >= 5, "The dispatcher must process input/layout throughout recovery.");
            Assert.AreSame(source, list.ItemsSource); Assert.AreEqual(0, resets, "Per-row progress and busy state must not reset the list.");
            Assert.IsTrue(Desc<ListBoxItem>(list).Count() < 40, "The large list must remain virtualized.");
            var result = store.Load(project.Id); Assert.IsTrue(result.Rows.All(r => r.Takes.Count == 1));
            foreach (var file in before) Assert.AreEqual(file.Value, SpeechAssetStore.Hash(store.FileFor(project.Id, file.Key)));
            await new SpeechAssetWork(store, registry).RunAsync(job, "Replay", null, default);
            Assert.AreEqual(3, calls, "A fully completed restored job must not call a narrator.");
        }
        finally
        {
            pulse.Stop(); descriptor.RemoveValueChanged(list, changed); await resident.RetireAsync(); owner.Close();
            ApplicationBackgroundOperations.Current = previous; Directory.Delete(root, true);
        }
    }, TimeSpan.FromMinutes(3));
}
