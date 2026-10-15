using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using Microsoft.Data.Sqlite;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagLexiconTests
{
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Passport(string root, string adapter = "cmudict", string? file = null, string? sha = null)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "words.dict"), "cat K AE1 T");
        File.WriteAllText(Path.Combine(root, "LICENSE"), "Test license notice");
        var passport = Path.Combine(root, "test.lopata-lexicon.json");
        RagBookPreparation.AtomicJson(passport, new { schema = 1, profile = RagLexiconPackage.Profile, sources = new[] {
            new { id = "fixture", adapter, file = file ?? "words.dict", sha256 = sha ?? Hash(Path.Combine(root,"words.dict")),
                license = "Test-only", attribution = "Fixture author", revision = "fixture-1", source_url = "https://example.org/fixture",
                license_files = new[] { new { file = "LICENSE", sha256 = Hash(Path.Combine(root,"LICENSE")) } } } } });
        return passport;
    }
    [TestMethod]
    [DataRow("ruaccent.accents_nn")]
    [DataRow("arbitrary.python")]
    public void UnsupportedProfilesRejected(string adapter)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try { Assert.Throws<InvalidDataException>(() => RagLexiconPackage.Read(Passport(root, adapter))); }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }
    [TestMethod]
    [DataRow("../outside.dict")]
    [DataRow("C:\\outside.dict")]
    public void PassportCannotReadOutsideItsFolder(string file)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var passport = Passport(root, file: file);
            Assert.Throws<InvalidDataException>(() => RagLexiconPackage.Prepare([passport], Path.Combine(root,"library"),
                Path.Combine(root,"work"), new Progress<RagBookProgress>(), default));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }
    [TestMethod]
    public void SnapshotVerifiesBytesAndLicenseAndPreservesOriginal()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var passport = Passport(root); var progress = new Progress<RagBookProgress>();
            var input = RagLexiconPackage.Prepare([passport], Path.Combine(root,"library"), Path.Combine(root,"work"), progress, default);
            using var request = JsonDocument.Parse(File.ReadAllText(input));
            var source = request.RootElement.GetProperty("sources")[0];
            Assert.AreEqual(Hash(Path.Combine(root,"words.dict")), Hash(source.GetProperty("snapshot").GetString()!));
            Assert.IsTrue(File.Exists(source.GetProperty("license_files")[0].GetProperty("path").GetString()));
            File.WriteAllText(Path.Combine(root,"words.dict"), "changed");
            Assert.AreNotEqual(Hash(Path.Combine(root,"words.dict")), Hash(source.GetProperty("snapshot").GetString()!));
            passport = Passport(root,sha:new string('0',64));
            Assert.Throws<InvalidDataException>(() => RagLexiconPackage.Prepare([passport], Path.Combine(root,"bad"), Path.Combine(root,"work2"), progress, default));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }
    [TestMethod]
    public void CyrillicStressNormalizationRetainsEnglishApostropheAndRussianLetter()
    {
        Assert.AreEqual("замок",RagLexiconReader.Normalize(" Замо́к "));
        Assert.AreEqual("йод",RagLexiconReader.Normalize("Йод"));
        Assert.AreEqual("елка",RagLexiconReader.Normalize("Ёлка"));
        Assert.AreEqual("don't",RagLexiconReader.Normalize("Don't"));
    }

    [TestMethod]
    public async Task PublishedPackageUpdatesOnlyChangedAdapter()
    {
        var library = Environment.GetEnvironmentVariable("LOPATA_LEXICON_READY");
        var package = Environment.GetEnvironmentVariable("LOPATA_LEXICON_UPDATE_PACKAGE");
        if (library is null || package is null) { Assert.Inconclusive("Explicit published local update input required."); return; }
        var job = RagBookPreparation.Create("Dictionary integration",Path.GetDirectoryName(library)!,Path.Combine(Path.GetDirectoryName(library)!,"no-models"),[package]);
        Assert.AreEqual(library,job.Directory);
        var (_,before) = RagLexiconReader.Open(library,default);
        var changed = new HashSet<string>();
        await new RagBookPreparation().RunAsync(job,"Dictionary update",new InlineProgress<RagBookProgress>(p =>
        { if(p.Stage=="LexiconIndex") { changed.Add(p.Detail.Split(':')[0]); Console.WriteLine(p.Detail); } }),default);
        var (_,after) = RagLexiconReader.Open(library,default);
        Assert.AreEqual(before.GetProperty("records").GetInt64(),after.GetProperty("records").GetInt64());
        Assert.IsTrue(changed.Count>0 && changed.All(s => s.StartsWith("openrussian-",StringComparison.Ordinal)));
        var hits = RagLexiconReader.Search(library,"замок",default).Where(h=>h.Kind=="forms").ToArray();
        Assert.IsTrue(hits.Length>0);
        Assert.IsTrue(hits.SelectMany(h=>h.Data.GetProperty("forms").EnumerateArray()).All(f=>f.GetProperty("form").GetString()!.Any(char.IsLetter)));
    }

    [TestMethod]
    public void PublishedStressAndPronunciationRetainContextAcrossSources()
    {
        var library = Environment.GetEnvironmentVariable("LOPATA_LEXICON_READY");
        if (library is null) { Assert.Inconclusive("Published local dictionary input required."); return; }
        foreach (var word in new[] { "замок", "мука", "все", "read", "lead" })
        {
            var hits = RagLexiconReader.Search(library, word, default);
            var kind = word is "read" or "lead" ? "pronunciation" : "stress";
            Assert.IsTrue(hits.Any(h => h.Kind == kind));
            Assert.IsTrue(hits.Where(h => h.Kind == kind).All(h => h.RequiresContext),word);
            if (word is "read" or "lead") Assert.AreEqual(kind,hits[0].Kind);
        }
    }

    [TestMethod]
    public async Task RealFullPackagePausesRestoresAndQueriesWithoutModels()
    {
        var package = Environment.GetEnvironmentVariable("LOPATA_LEXICON_PACKAGE");
        var output = Environment.GetEnvironmentVariable("LOPATA_LEXICON_OUTPUT");
        if (package is null || output is null) { Assert.Inconclusive("Explicit local dictionary input required."); return; }
        Directory.CreateDirectory(output); var previous = ApplicationBackgroundOperations.Current;
        var previousExit = ApplicationBackgroundOperations.ExitToken;
        using var firstLifetime = new CancellationTokenSource();
        ApplicationBackgroundOperations.ExitToken = firstLifetime.Token;
        var statePath = Path.Combine(output,"session-"+Guid.NewGuid().ToString("N"),"background.json");
        var controller = new BackgroundOperationController(new(statePath));
        ApplicationBackgroundOperations.Current = controller;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        try
        {
            var job = RagBookPreparation.Create("Dictionary integration",output,Path.Combine(output,"no-models"),[package]);
            if (RagLexiconReader.Exists(job.Directory) || File.Exists(Path.Combine(job.Directory,"current.json")))
                job = RagBookPreparation.Create("Dictionary integration",Path.Combine(output,"run-"+Guid.NewGuid().ToString("N")),job.ModelsRoot,[package]);
            Console.WriteLine("Library: "+job.Directory);
            var bookPointer = Path.Combine(job.Directory,"current.json"); Directory.CreateDirectory(job.Directory);
            File.WriteAllText(bookPointer,"Fixture book pointer must be unchanged"); var bookHash = Hash(bookPointer);
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new InlineProgress<RagBookProgress>(p => {
                if (p.Stage == "LexiconIndex") committed.TrySetResult();
                if (p.Stage != "LexiconIndex" || p.Done > 0) Console.WriteLine($"{clock.Elapsed.TotalSeconds:0.0}s {p.Stage} {p.Done}/{p.Total} {p.Detail}");
            });
            var running = new RagBookPreparation().RunAsync(job,"Dictionary integration",progress,deadline.Token);
            await Task.WhenAny(running,committed.Task).WaitAsync(deadline.Token);
            if (running.IsFaulted) await running;
            Assert.IsFalse(running.IsCompleted,"Import must reach its checkpoint before completion.");
            await controller.PauseAsync();
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Paused,controller.State!.Phase);
            Assert.IsFalse(controller.BlocksNewWork);
            using (controller.BeginAuxiliary()) { }
            var id = controller.State.Id;
            firstLifetime.Cancel();
            try { await running; Assert.Fail("Paused worker returned success."); } catch (OperationCanceledException) { }
            await controller.WaitForAllIdleAsync();
            ApplicationBackgroundOperations.ExitToken = deadline.Token;
            controller = new(new(statePath)); controller.Load(); ApplicationBackgroundOperations.Current=controller;
            Assert.IsTrue(controller.FindOperation(RagBookPreparation.Kind)!.UserPaused);
            controller.Register(RagBookPreparation.Kind,(state,token) => new RagBookPreparation().RunAsync(RagBookPreparation.Read(state),state.Title,progress,token,state));
            await controller.ResumeAsync(id,deadline.Token);
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Completed,controller.State!.Phase);
            Assert.IsFalse(Directory.Exists(job.ModelsRoot),"Dictionary import must not acquire or load model weights.");
            Assert.AreEqual(bookHash,Hash(bookPointer));
            var (database,manifest) = RagLexiconReader.Open(job.Directory,deadline.Token);
            Assert.IsTrue(manifest.GetProperty("records").GetInt64()>3_000_000);
            foreach (var query in new[] { "замок", "все", "люди", "кошка", "read", "entity", "tropical bird" })
            {
                var hits = RagLexiconReader.Search(job.Directory,query,deadline.Token);
                Assert.IsTrue(hits.Count>0,query);
                if (query == "замок") Assert.IsTrue(hits.Any(h => h.Kind=="stress" && h.RequiresContext && h.Data.GetProperty("variants").GetArrayLength()>1));
                if (query == "read") Assert.IsTrue(hits.Any(h => h.Kind=="pronunciation" && h.RequiresContext));
                RagBookPreparation.AtomicJson(Path.Combine(output,"query-"+query.Replace(' ','_')+".json"),hits);
            }
            Console.WriteLine($"Completed {manifest.GetProperty("records")} records, {manifest.GetProperty("excluded")} excluded in {clock.Elapsed.TotalSeconds:0.0}s. DB: {database}");
            RagBookPreparation.AtomicJson(Path.Combine(output,"result.json"),new { library=job.Directory, elapsed_seconds=clock.Elapsed.TotalSeconds,manifest,book_pointer_unchanged=true,pause_restored=true });
        }
        finally { firstLifetime.Cancel(); deadline.Cancel(); await controller.WaitForAllIdleAsync(); ApplicationBackgroundOperations.Current=previous; ApplicationBackgroundOperations.ExitToken=previousExit; }
    }
}
