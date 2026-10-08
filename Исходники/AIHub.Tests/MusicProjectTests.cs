using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicProjectTests
{
    [TestMethod]
    public void DraftsReserveUniqueNumbersButDoNotPersistContent()
    {
        using var files = new Files(); var store = new MusicProjects(files.Root);
        var first = store.CreateDraft(); var second = new MusicProjects(files.Root).CreateDraft();
        Assert.AreEqual(first.Number + 1, second.Number); Assert.HasCount(0, store.List());
        Assert.HasCount(0, Directory.GetFiles(files.Root, "*.project.json"));
        first = store.Rename(first, "Не сохраняется"); Assert.HasCount(0, store.List());
        var fixedProject = store.Fix(first, new() { Lyrics = "Молоко́\n[Chorus]" });
        Assert.IsTrue(fixedProject.Manual); Assert.HasCount(0, fixedProject.Steps);
        Assert.AreEqual("Молоко́\n[Chorus]", new MusicProjects(files.Root).Load(first.Id).Saved.Lyrics);
    }
    [TestMethod]
    public void RequestsAppendAndRecoveryDoesNotDuplicateSnapshots()
    {
        using var files = new Files(); var store = new MusicProjects(files.Root); var project = store.CreateDraft();
        var snapshot = new MusicProjectSnapshot { Lyrics = "Первый текст" }; snapshot.Expert.Values["seed"] = 42;
        var job = Guid.NewGuid().ToString("N"); project = store.AddRequest(project, job, snapshot);
        snapshot.Expert.Values["seed"] = 77;
        Assert.AreEqual(42d, project.Steps[0].Snapshot.Expert.Values["seed"]);
        project = store.AddRequest(project, job, new() { Lyrics = "Повторная доставка" }); Assert.HasCount(1, project.Steps);
        project = store.SetOutcome(project.Id, job, MusicProjectOutcome.Failed, "Сбой исполнителя");
        var original = project.Steps[0];
        project = store.AddRequest(project, Guid.NewGuid().ToString("N"), new() { Lyrics = "Второй текст" });
        project = store.AddRequest(project, Guid.NewGuid().ToString("N"), original.Snapshot);
        Assert.HasCount(3, project.Steps); Assert.AreEqual("Второй текст", project.Steps[1].Snapshot.Lyrics);
        Assert.AreEqual(MusicProjectOutcome.Failed, project.Steps[0].Outcome); Assert.AreEqual("Сбой исполнителя", project.Steps[0].Message);
        Assert.AreEqual("Первый текст", project.Saved.Lyrics); Assert.IsFalse(project.Manual);
        project = store.Fix(project, new() { Lyrics = "Ручная фиксация" }); Assert.IsTrue(project.Manual); Assert.HasCount(3, project.Steps);
        Assert.HasCount(3, new MusicProjects(files.Root).Load(project.Id).Steps);
    }
    [TestMethod]
    public void CorruptedHistoryAndEscapingIdentifiersAreRefusedWithoutOverwriting()
    {
        using var files = new Files(); var store = new MusicProjects(files.Root);
        var project = store.AddRequest(store.CreateDraft(), Guid.NewGuid().ToString("N"), new());
        Assert.Throws<InvalidDataException>(() => store.Load("../elsewhere"));
        var path = Path.Combine(files.Root, project.Id + ".project.json");
        var invalid = project with { Steps = [project.Steps[0] with { Number = 8 }] };
        var raw = JsonSerializer.Serialize(invalid); File.WriteAllText(path, raw);
        Assert.Throws<InvalidDataException>(() => store.Load(project.Id));
        Assert.Throws<InvalidDataException>(() => store.Fix(project, new())); Assert.AreEqual(raw, File.ReadAllText(path));
        Assert.HasCount(0, Directory.GetFiles(files.Root, "*.tmp"));
    }
    [TestMethod]
    public void ConcurrentDraftsNeverReuseCounterNumbers()
    {
        using var files = new Files(); var numbers = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 20, _ => numbers.Add(new MusicProjects(files.Root).CreateDraft().Number));
        Assert.AreEqual(20, numbers.Distinct().Count()); Assert.AreEqual(20L, numbers.Max());
    }
    [TestMethod]
    public async Task LinkedJobPreservesProvenanceAndCompletedAudioAcrossRecovery()
    {
        using var files = new Files(); var store = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Тест", 2, 30, "rock", "Текст песни");
        var project = store.AddRequest(store.CreateDraft(), job.Id, MusicProjectSnapshot.FromJob(job));
        job = job with { ProjectId = project.Id, ProjectStep = 1, ProjectName = "Проверка" }; jobs.Save(job);
        var worker = new Worker(); await new MusicGenerationRunner(jobs, worker).RunAsync(job.Id, default);
        project = store.SetOutcome(project.Id, job.Id, MusicProjectOutcome.Completed);
        await new MusicGenerationRunner(jobs, worker).RunAsync(job.Id, default);
        Assert.AreEqual(2, worker.Calls); Assert.HasCount(2, jobs.Tracks(job.Id)); Assert.HasCount(1, project.Steps);
        var restored = jobs.Load(job.Id); Assert.AreEqual(project.Id, restored.ProjectId);
        var tags = MusicSongMetadata.Create(restored, restored.Variants[0], 0);
        Assert.AreEqual(project.Id, tags["LOPATA_PROJECT"]); Assert.AreEqual("1", tags["LOPATA_PROJECT_STEP"]);
        Assert.AreEqual("Текст песни", tags["lyrics"]);
        File.Delete(restored.Variants[0].ResultPath); Assert.HasCount(1, jobs.Tracks(job.Id));
        Assert.HasCount(1, store.Load(project.Id).Steps);
    }
    private sealed class Worker : IMusicYueWorker
    {
        public int Calls;
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
            File.WriteAllTextAsync(planPath, "X:1\nK:C\nC", token);
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { Calls++; MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
    internal sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-projects-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root)); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
