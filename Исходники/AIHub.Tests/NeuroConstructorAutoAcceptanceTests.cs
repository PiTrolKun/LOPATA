using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.NeuroConstructor;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NeuroConstructorAutoAcceptanceTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AutoBuildUsesOnlyIncludedExamplesAndPublishesCheckedWeights(bool useReadyLink)
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_NEURO_ACCEPTANCE");
        if (string.IsNullOrWhiteSpace(evidence))
            Assert.Inconclusive("Requires prepared local model files and LOPATA_NEURO_ACCEPTANCE.");
        var work = Directory.CreateDirectory(Path.Combine(evidence, "auto-" + Guid.NewGuid().ToString("N"))).FullName;
        using var store = NeuroFiles.UseRoot(Path.Combine(work, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(work, "audit"), enabled: false);
        var pro = NeuroService.Open();
        var project = NeuroService.OpenAuto();
        Assert.AreNotEqual(pro.Id, project.Id);
        project.AutoEvaluateOnly = useReadyLink;
        project.Cycles = 2;
        project.LanguageDevice = useReadyLink && Environment.GetEnvironmentVariable("LOPATA_NEURO_TEST_GPU") == "1" ? "gpu" : "cpu";
        project.VisionDevice = "cpu";
        project.SpeechDevice = "auto";
        NeuroService.Save(project);
        var output = Path.Combine(work, "моя сборка.lopatanet");
        var previousController = ApplicationBackgroundOperations.Current;
        var previousExit = ApplicationBackgroundOperations.ExitToken;
        using var exit = new CancellationTokenSource();
        BackgroundOperationController Launch()
        {
            var controller = new BackgroundOperationController(new BackgroundOperationStore(Path.Combine(work, "active.json")));
            controller.Register(NeuroService.TrainingKind, NeuroService.RestoreAsync);
            controller.Load();
            ApplicationBackgroundOperations.Current = controller;
            return controller;
        }

        try
        {
            var controller = Launch();
            ApplicationBackgroundOperations.ExitToken = exit.Token;
            var run = NeuroService.BuildAutoAsync(project, output);
            if (useReadyLink)
            {
                // Pause after comparison and before file publication, then recreate the desktop controller.
                var cutoff = DateTime.UtcNow.AddMinutes(8);
                while (!run.IsCompleted && NeuroService.Status(project).Status != "packing")
                {
                    if (DateTime.UtcNow > cutoff) Assert.Fail("Comparison did not finish.");
                    await Task.Delay(80);
                }
                if (run.IsFaulted) await run;
                Assert.AreEqual("packing", NeuroService.Status(project).Status);
                var reportBeforePause = NeuroService.ReportPath(project);
                var savedReport = File.ReadAllBytes(reportBeforePause);
                var savedOn = File.GetLastWriteTimeUtc(reportBeforePause);
                var operationId = controller.State!.Id;
                await controller.PauseAsync(operationId);
                Assert.IsFalse(File.Exists(output), "An interrupted operation published an unchecked file.");
                controller.CheckpointForExit();
                await exit.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
                ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
                controller = Launch();
                Assert.IsTrue(controller.State!.UserPaused);
                Assert.AreEqual(operationId, controller.State.Id);
                await NeuroService.ResumeAsync(project);
                CollectionAssert.AreEqual(savedReport, File.ReadAllBytes(reportBeforePause));
                Assert.AreEqual(savedOn, File.GetLastWriteTimeUtc(reportBeforePause), "Resuming export repeated comparison.");
            }
            else
            {
                await run;
            }
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
            Assert.AreEqual(pro.Id, NeuroService.Open().Id);
            Assert.AreEqual(project.Id, NeuroService.OpenAuto().Id);
            Assert.IsTrue(File.Exists(output));
            var report = NeuroFiles.Read<JsonElement>(NeuroService.ReportPath(project));
            Assert.IsTrue(report.GetProperty("eligible").GetBoolean());
            Assert.IsTrue(report.GetProperty("correct").GetInt32() >= report.GetProperty("baselineCorrect").GetInt32());
            Assert.AreEqual(useReadyLink, report.GetProperty("evaluateOnly").GetBoolean());
            Assert.AreEqual(!useReadyLink, report.GetProperty("optimizationSteps").GetInt32() > 0);
            Assert.IsFalse(report.GetProperty("examples").EnumerateArray().Any(row => row.GetProperty("own").GetBoolean()));
            var placement = report.GetProperty("placement");
            Assert.AreEqual("cpu", placement.GetProperty("vision").GetString());
            Assert.AreEqual(project.LanguageDevice == "cpu", placement.GetProperty("language").GetString() == "cpu");
            using var archive = ZipFile.OpenRead(output);
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            var settings = JsonSerializer.Deserialize<JsonElement>(manifestStream).GetProperty("settings");
            Assert.AreEqual(project.LanguageDevice, settings.GetProperty("languageDevice").GetString());
            Assert.AreEqual(project.VisionDevice, settings.GetProperty("visionDevice").GetString());
            var check = JsonSerializer.Deserialize<JsonElement>(await NeuroService.CheckAsync(output));
            Assert.AreEqual(12, check.GetProperty("matched").GetInt32());
            Assert.AreEqual(placement.GetProperty("language").GetString(), check.GetProperty("placement").GetProperty("language").GetString());
            Assert.AreEqual(placement.GetProperty("vision").GetString(), check.GetProperty("placement").GetProperty("vision").GetString());
            NeuroFiles.Write(Path.Combine(work, "acceptance.json"), new { useReadyLink, output, placement, report, check });
        }
        finally
        {
            await exit.CancelAsync();
            ApplicationBackgroundOperations.Current = previousController;
            ApplicationBackgroundOperations.ExitToken = previousExit;
        }
    }
}
