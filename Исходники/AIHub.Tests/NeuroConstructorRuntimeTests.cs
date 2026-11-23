using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.NeuroConstructor;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NeuroConstructorRuntimeTests
{
    [TestMethod]
    public void PresetsKeepIndependentSettingsAndLabelsSurviveReopening()
    {
        var directory=Path.Join(Path.GetTempPath(),"lopata-neuro-"+Guid.NewGuid().ToString("N"));
        using var store=NeuroFiles.UseRoot(directory);
        try
        {
            var original=NeuroService.New("trio");
            Assert.AreEqual("late",original.Connection);
            NeuroService.SavePreset(original,"Слух");
            var clone=NeuroService.FromPreset("Слух");
            clone.Composition="pair"; clone.Connection="inside";
            NeuroService.SavePreset(clone,"Картинки");
            CollectionAssert.AreEquivalent(new[]{"Слух","Картинки"},NeuroService.Presets());
            Assert.AreEqual("trio",NeuroService.FromPreset("Слух").Composition);
            Assert.AreEqual("pair",NeuroService.FromPreset("Картинки").Composition);
            var image=Path.Combine(directory,"icon.png");
            File.WriteAllBytes(image,[10,20,30]);
            NeuroService.AddExample(clone,image,"Найди настройки",2);
            var labelled=NeuroService.OpenProject(clone.Id).Examples.Single();
            Assert.AreEqual(2,labelled.Slot);
            Assert.IsTrue(File.Exists(labelled.Image));
            Assert.ThrowsExactly<InvalidDataException>(()=>NeuroService.AddExample(clone,image,"Покажи настройки",1,split:"test"));
            NeuroService.AddExample(clone,image,"Найди настройки",1);
            Assert.AreEqual(1,NeuroService.OpenProject(clone.Id).Examples.Single().Slot);
        }
        finally { Directory.Delete(directory,true); }
    }

    [TestMethod]
    public async Task RestoreAttachesToTheLockedRunAndReadsItsPublishedResult()
    {
        var temporary=Directory.CreateTempSubdirectory("lopata-neuro-attach-");
        using var store=NeuroFiles.UseRoot(temporary.FullName);
        try
        {
            var p=NeuroService.New();
            var id=Guid.NewGuid().ToString("N");
            var dir=NeuroFiles.Run(p.Id,id);
            NeuroFiles.Write(Path.Combine(dir,"settings.json"),p);
            NeuroFiles.Write(Path.Combine(dir,"run.json"),new {runId=id});
            var status=Path.Combine(dir,"status.json");
            NeuroFiles.Write(status,new NeuroStatus("running","test",1,2,1,null,""));
            var state=new BackgroundOperationState { Kind=NeuroService.TrainingKind,Title="test",Project=p.Id,
                Input=JsonSerializer.SerializeToElement(new {ProjectId=p.Id,RunId=id}) };
            using var held=new FileStream(Path.Combine(dir,"worker.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.ReadWrite);
            held.SetLength(1);
            held.Lock(0,1);
            var restoring=NeuroService.RestoreAsync(state,CancellationToken.None);
            try
            {
                await Task.Delay(300);
                Assert.IsFalse(restoring.IsCompleted,"Restore must wait for the executor that owns the run.");
                Directory.CreateDirectory(Path.Combine(dir,"learned"));
                File.WriteAllBytes(Path.Combine(dir,"learned","pair.pt"),[1]);
                NeuroFiles.Write(Path.Combine(dir,"report.json"),new {eligible=true});
                NeuroFiles.Write(status,new NeuroStatus("completed","test",2,2,2,0,""));
            }
            finally { held.Unlock(0,1); }
            await restoring.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(id,NeuroFiles.Read<string>(Path.Combine(NeuroFiles.Project(p.Id),"finished.json")));
        }
        finally { temporary.Delete(true); }
    }

    // Loads real experts, writes portable weights and performs inference through the application service.
    [TestMethod]
    public async Task DesktopServiceExportsAndExtendsRealModel()
    {
        var work=Environment.GetEnvironmentVariable("LOPATA_NEURO_ACCEPTANCE");
        if (string.IsNullOrWhiteSpace(work)) Assert.Inconclusive("Set LOPATA_NEURO_ACCEPTANCE to a fresh output directory for the local model acceptance run.");
        Directory.CreateDirectory(work);
        using var store=NeuroFiles.UseRoot(Path.Combine(work,"projects-data"));
        using var audit=ComponentAudit.UseTestStore(Path.Combine(work,"audit"),enabled:false);
        var pair=NeuroService.New();
        var parent=Path.Combine(work,"language-vision.lopatanet");
        await NeuroService.ExportAsync(pair,parent);
        string Digest(string path) { using var input=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
        var parentSha=Digest(parent);
        var check=JsonSerializer.Deserialize<JsonElement>(await NeuroService.CheckAsync(parent));
        Assert.AreEqual(12,check.GetProperty("matched").GetInt32());
        var child=NeuroService.Import(parent);
        child.Composition="trio"; child.Connection="late";
        var examples=Path.Combine(NeuroRuntime.DirectoryPath,"training_examples");
        var row=NeuroFiles.Read<JsonElement>(Path.Combine(examples,"trio.json")).EnumerateArray().First(r=>r.GetProperty("split").GetString()=="train");
        var image=Path.Combine(examples,row.GetProperty("image").GetString()!);
        var sounds=row.GetProperty("audio").EnumerateArray().Select(s=>Path.Combine(examples,s.GetString()!)).ToArray();
        var ownImage=Path.Combine(work,"my-panel.png");
        using (var original=new System.Drawing.Bitmap(image))
        using (var panel=new System.Drawing.Bitmap(original))
        {
            panel.SetPixel(0,0,System.Drawing.Color.Coral);
            panel.Save(ownImage,System.Drawing.Imaging.ImageFormat.Png);
        }
        NeuroService.AddExample(child,ownImage,row.GetProperty("text").GetString()!,row.GetProperty("slot").GetInt32(),sounds);
        await NeuroService.TrainAsync(child);
        Assert.AreEqual("completed",NeuroService.Status(child).Status);
        Assert.IsTrue(NeuroService.ReportMatches(child));
        var report=NeuroFiles.Read<JsonElement>(NeuroService.ReportPath(child));
        Assert.IsTrue(report.GetProperty("examples").EnumerateArray().Any(e=>e.GetProperty("own").GetBoolean() && e.GetProperty("split").GetString()=="train"));
        Assert.IsTrue(report.GetProperty("eligible").GetBoolean());
        var derived=Path.Combine(work,"language-vision-speech.lopatanet");
        await NeuroService.ExportAsync(child,derived);
        Assert.AreEqual(parentSha,Digest(parent));
        var voiceCheck=JsonSerializer.Deserialize<JsonElement>(await NeuroService.CheckAsync(derived));
        Assert.AreEqual(12,voiceCheck.GetProperty("matched").GetInt32());
        using var archive=ZipFile.OpenRead(derived);
        using var controls=archive.GetEntry("checks/pair.json")!.Open();
        var oldAnswers=JsonSerializer.Deserialize<JsonElement>(controls).EnumerateArray().Select(r=>r.GetProperty("expected").GetInt32()).ToArray();
        CollectionAssert.AreEqual(check.GetProperty("rows").EnumerateArray().Select(r=>r.GetProperty("predicted").GetInt32()).ToArray(),oldAnswers);
        NeuroFiles.Write(Path.Combine(work,"desktop-acceptance.json"),new { parentSha,derivedSha=Digest(derived),
            pair=check,trio=voiceCheck,retainedAnswers=oldAnswers,report });
    }

    [TestMethod]
    public async Task TrayPauseSurvivesDesktopRestartAndResumesTheSameTrainingRun()
    {
        var work=Environment.GetEnvironmentVariable("LOPATA_NEURO_ACCEPTANCE");
        if (string.IsNullOrWhiteSpace(work)) Assert.Inconclusive("Requires prepared local experts and LOPATA_NEURO_ACCEPTANCE.");
        var root=Path.Combine(work,"tray-"+Guid.NewGuid().ToString("N"));
        using var store=NeuroFiles.UseRoot(root);
        using var audit=ComponentAudit.UseTestStore(Path.Combine(work,"audit"),enabled:false);
        using var exit=new CancellationTokenSource();
        var previousController=ApplicationBackgroundOperations.Current;
        var previousToken=ApplicationBackgroundOperations.ExitToken;
        var operationPath=Path.Combine(root,"background","active.json");
        BackgroundOperationController Launch()
        {
            var controller=new BackgroundOperationController(new BackgroundOperationStore(operationPath));
            controller.Register(NeuroService.TrainingKind,NeuroService.RestoreAsync);
            controller.Load();
            ApplicationBackgroundOperations.Current=controller;
            return controller;
        }
        try
        {
            var controller=Launch();
            ApplicationBackgroundOperations.ExitToken=exit.Token;
            var p=NeuroService.New();
            p.Cycles=6;
            var running=NeuroService.TrainAsync(p);
            var timeout=DateTime.UtcNow.AddMinutes(8);
            while (NeuroService.Status(p).Step < 1 && !running.IsCompleted)
            {
                if (DateTime.UtcNow > timeout) Assert.Fail("Worker did not reach a training step");
                await Task.Delay(50);
            }
            if (running.IsFaulted) await running;
            var operation=controller.State!.Id;
            await controller.PauseAsync(operation);
            Assert.AreEqual("paused",NeuroService.Status(p).Status);
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(worker=>worker.Component=="NeuroConstructor"));
            var id=NeuroFiles.Read<string>(Path.Combine(NeuroFiles.Project(p.Id),"current.json"));
            controller.CheckpointForExit();
            await exit.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            // A torn progress display must not invalidate the separately committed optimizer state.
            File.WriteAllText(Path.Combine(NeuroFiles.Run(p.Id,id),"status.json"),"{");
            ApplicationBackgroundOperations.ExitToken=CancellationToken.None;
            var afterRestart=Launch();
            Assert.IsTrue(afterRestart.State!.UserPaused);
            Assert.AreEqual(operation,afterRestart.State.Id);
            p.Cycles=1;
            NeuroService.Save(p);
            await NeuroService.ResumeAsync(p);
            Assert.AreEqual(BackgroundOperationPhase.Completed,afterRestart.State!.Phase);
            var folder=NeuroFiles.Run(p.Id,id);
            Assert.AreEqual(6,NeuroFiles.Read<JsonElement>(Path.Join(folder,"run.json")).GetProperty("cycles").GetInt32());
            Assert.AreEqual(id,NeuroFiles.Read<string>(Path.Combine(NeuroFiles.Project(p.Id),"finished.json")));
            Assert.IsFalse(NeuroService.ReportMatches(p));
            NeuroFiles.Write(Path.Combine(work,"desktop-pause.json"),new { operation,id,
                manualPauseRestored=true,workerReleased=true,completed=NeuroService.Status(p),report=NeuroService.ReportPath(p) });
        }
        finally
        {
            await exit.CancelAsync();
            ApplicationBackgroundOperations.Current=previousController;
            ApplicationBackgroundOperations.ExitToken=previousToken;
        }
    }
}
