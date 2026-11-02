using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class WorkBlockedTests
{
    [TestMethod]
    public async Task OnlyRejectedWorkCommandsSignalAndReadingOrUnblockingStaysSilent()
    {
        using var files = new MusicProjectTests.Files();
        var store = new BackgroundOperationStore(Path.Combine(files.Root, "operation.json"));
        var blocker = Input("Other video", BackgroundOperationPhase.Waiting, userPaused: false, requiresDecision: true);
        var paused = Input("Parked music", BackgroundOperationPhase.Paused, userPaused: true, requiresDecision: false);
        store.Operation(paused.Id).Save(paused); store.Save(blocker);
        var controller = new BackgroundOperationController(store); controller.Load(); var notifications = 0;
        controller.WorkBlocked += () => notifications++;
        var checkpoint = File.ReadAllBytes(Path.Combine(files.Root, "operation.json"));

        ReadStatus(controller); Assert.AreEqual(0, notifications);
        Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary());
        Assert.AreEqual(1, notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunAsync<int>(Input("Rejected new request"),
            _ => throw new AssertFailedException("Rejected executor must never run"), () => Task.CompletedTask, default));
        Assert.AreEqual(2, notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResumeAsync(paused.Id, default));
        Assert.AreEqual(3, notifications);
        CollectionAssert.AreEqual(checkpoint, File.ReadAllBytes(Path.Combine(files.Root, "operation.json")));
        ReadStatus(controller); Assert.AreEqual(3, notifications, "Status polling must not keep reopening the notice.");

        controller.DiscardPending(blocker.Id);
        Assert.IsFalse(controller.BlocksNewWork); ReadStatus(controller);
        Assert.AreEqual(3, notifications, "Removing a blocker must not produce a new blocked-work notice.");
        using (controller.BeginAuxiliary()) { ReadStatus(controller); Assert.AreEqual(3, notifications); }
        Assert.AreEqual(42, await controller.RunAsync(Input("Allowed request"), _ => Task.FromResult(42), () => Task.CompletedTask, default));
        ReadStatus(controller); Assert.AreEqual(3, notifications);
    }

    [TestMethod, DataRow("switch"), DataRow("start")]
    public Task MusicUserActionReportsForeignBlockerWithoutChangingProjectOrCheckpoint(string action) => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new MusicProjectTests.Files();
        var checkpointPath = Path.Combine(files.Root, "operation.json");
        var store = new BackgroundOperationStore(checkpointPath);
        var blocker = Input("Unfinished video", BackgroundOperationPhase.Waiting, userPaused: false, requiresDecision: true);
        store.Save(blocker); var controller = new BackgroundOperationController(store); controller.Load();
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = controller;
        try {
            using var workspace = new MusicWorkspaceControl(
                new MusicProjects(Path.Combine(files.Root, "projects")),
                new MusicGenerationJobs(Path.Combine(files.Root, "jobs")),
                new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
            workspace.Localize(key => key); workspace.Editor.Lyrics = "Мой сохранённый текст песни";
            var projectId = workspace.Projects.Current.Id; var variation = workspace.Generation.Variation;
            var checkpoint = File.ReadAllBytes(checkpointPath); var notifications = 0;
            controller.WorkBlocked += () => notifications++;
            Assert.IsFalse(workspace.Session.CanChangeModel); ReadStatus(controller);
            Assert.AreEqual(0, notifications);

            if (action == "switch") {
                var error = Assert.Throws<InvalidOperationException>(() => workspace.Projects.SwitchModel(MusicStudioRuntime.Variation));
                Assert.AreEqual("Music.Projects.Busy", error.Message);
            }
            else {
                Assert.IsNotNull(workspace.Generation.StartPause);
                workspace.Generation.StartPause().GetAwaiter().GetResult();
            }

            Assert.AreEqual(1, notifications);
            Assert.AreEqual(projectId, workspace.Projects.Current.Id);
            Assert.AreEqual(variation, workspace.Generation.Variation);
            Assert.AreEqual("Мой сохранённый текст песни", workspace.Editor.Lyrics);
            Assert.HasCount(0, workspace.Projects.Current.Steps);
            Assert.IsFalse(controller.IsRunning); Assert.IsTrue(controller.BlocksNewWork);
            CollectionAssert.AreEqual(checkpoint, File.ReadAllBytes(checkpointPath));
            workspace.Localize(key => key); ReadStatus(controller);
            Assert.AreEqual(1, notifications, "Localization and button-state refresh are passive reads.");
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    private static BackgroundOperationState Input(string title, BackgroundOperationPhase phase = BackgroundOperationPhase.Running,
        bool userPaused = false, bool requiresDecision = false) => new() {
        Kind = "work-blocked.test", Title = title, Phase = phase, UserPaused = userPaused, RequiresDecision = requiresDecision,
        Input = JsonSerializer.SerializeToElement(new { Song = title }),
        Checkpoint = JsonSerializer.SerializeToElement(new { CompletedParts = 2 })
    };

    private static void ReadStatus(BackgroundOperationController controller)
    {
        for (var index = 0; index < 20; index++) {
            _ = controller.IsRunning; _ = controller.BlocksNewWork; _ = controller.State;
            _ = controller.PausedOperations; _ = controller.PendingOperations; _ = controller.HasPending;
        }
    }
}
