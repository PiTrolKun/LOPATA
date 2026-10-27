using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoWarmReviewTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(35));
    private static VideoPreparationControl Control(VideoPreparationTests.TestFiles files, out LocalizationService language)
    {
        var session = new VideoPreparationSession(files.File("recovery")); var p = session.Project;
        p.Form = "story"; p.IdeaText = "A cat walks through a quiet workshop."; VideoTranslationHistory.UseOriginal(p);
        p.IdeaText += " The clock strikes midnight."; p.CurrentStep = "translation";
        session.Changed(); session.Save(files.File("project.lopataAIVid"));
        language = new(); language.Load("ru"); return new(language.T, session);
    }
    private static void Decide(Window owner, string value, Action<VideoPreparationDecisionWindow>? inspect = null) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    {
        var dialog = owner.OwnedWindows.OfType<VideoPreparationDecisionWindow>().Single(); inspect?.Invoke(dialog);
        Find<Button>(dialog, "VideoPrep.Decision." + value).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }));
    [TestMethod]
    public Task NextAsksForReviewAndCancelPreservesAllPreviousVersions() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, out _); var p = control.Session.Project;
        var owner = new Window { Content = control, Width = 1100, Height = 850 }; owner.Show();
        var english = p.EnglishText; var versions = p.TranslationHistory.Count;
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "VideoPrep.TranslationConfirmSource"));
        Decide(owner, "cancel", dialog => Assert.IsTrue(dialog.Title.Contains("Проверьте")));
        Assert.IsFalse(await control.NextAsync()); Assert.AreEqual("translation", p.CurrentStep); Assert.AreEqual(english, p.EnglishText);
        Assert.AreEqual(versions, p.TranslationHistory.Count); Assert.IsFalse(VideoTranslationHistory.IsCurrent(p));
        Decide(owner, "continue"); Assert.IsTrue(await control.NextAsync());
        Assert.AreEqual("development", p.CurrentStep); Assert.AreEqual(english, p.EnglishText); Assert.AreEqual(versions + 1, p.TranslationHistory.Count);
        Assert.IsTrue(VideoTranslationHistory.IsCurrent(p)); owner.Close();
    });
    [TestMethod]
    public Task AWorldReviewAlsoConfirmsItsWorkingVersionInOneDialog() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root(); var w = p.World!;
        w.Cards[0].Text.IdeaText = "A warm workshop with soft window light."; VideoWorld.Capture(p); VideoTranslationHistory.UseOriginal(w.Working);
        p.Development!.EnglishText += " A new location."; VideoDevelopment.Capture(p); p.CurrentStep = "world_translation";
        var path = files.File("world.lopataAIVid"); VideoPreparationArchive.Save(p,path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path); var l = new LocalizationService(); l.Load("ru");
        var control = new VideoPreparationControl(l.T,session); var owner = new Window{Content=control,Width=1000,Height=800}; owner.Show();
        Decide(owner,"continue"); Assert.IsTrue(await control.NextAsync());
        Assert.AreEqual("references",session.Project.CurrentStep); Assert.IsTrue(VideoTranslationHistory.IsCurrent(session.Project.World!.Working)); owner.Close();
    });
    [TestMethod]
    [DataRow("retry", LlamaGpuDecision.Retry)] [DataRow("hybrid", LlamaGpuDecision.Hybrid)]
    [DataRow("cancel", LlamaGpuDecision.Cancel)] [DataRow("cpu", LlamaGpuDecision.Cpu)]
    public Task GpuPlacementIsAnExplicitOwnedChoice(string choice, LlamaGpuDecision expected) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, out _);
        var owner = new Window { Content = control, Width = 1000, Height = 800 }; owner.Show();
        Decide(owner, choice);
        var result = await control.DecideGpuAsync(new(choice == "cpu" ? "cpu_fallback" : "gpu_memory_busy", "Test GPU", 18L << 30, 8L << 30, 24L << 30), default);
        Assert.AreEqual(expected, result); owner.Close();
    });
    [TestMethod]
    public void FullGpuBudgetDistinguishesBusyMemoryFromInsufficientHardware()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.IsTrue(LlamaGpuAdmissionPolicy.FitsHardware(18 * gb, 24 * gb));
        Assert.IsFalse(LlamaGpuAdmissionPolicy.FitsFree(18 * gb, new("GPU0", "GPU", 24 * gb, 8 * gb)));
        Assert.IsTrue(LlamaGpuAdmissionPolicy.FitsFree(18 * gb, new("GPU0", "GPU", 24 * gb, 22 * gb)));
        Assert.IsFalse(LlamaGpuAdmissionPolicy.FitsHardware(18 * gb, 12 * gb));
    }
    internal sealed class Resident : IVideoConceptAssistant, IVideoConceptResident
    {
        public event Action? RuntimeChanged;
        public VideoConceptRuntimeInfo? RuntimeInfo { get; private set; }
        public readonly List<VideoConceptModelRequest> WarmRequests = [];
        public int Generations;
        public Task WarmAsync(VideoConceptModelRequest r, CancellationToken token)
        { token.ThrowIfCancellationRequested(); WarmRequests.Add(r); RuntimeInfo = new(100, r.ModelId, "Vulkan", "GPU", "Gpu", 65, 8192); RuntimeChanged?.Invoke(); return Task.CompletedTask; }
        public Task RetireAsync() { RuntimeInfo = null; RuntimeChanged?.Invoke(); return Task.CompletedTask; }
        public Task<VideoConceptAnswer> GenerateAsync(VideoConceptRequest r, CancellationToken token) { Generations++; return Task.FromResult(VideoWorldTests.Answer()); }
    }
    [TestMethod]
    public Task EnteringScenarioWarmsStrongestDefaultWithoutGeneratingText() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, out _); var resident = new Resident();
        control.ConfigureConceptAssistant(resident, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new DebugModelInfo { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()));
        var owner = new Window { Content = control, Width = 1100, Height = 850 }; owner.Show();
        for (var i = 0; i < 100 && resident.WarmRequests.Count == 0; i++) await Task.Delay(10);
        Assert.AreEqual("executor", resident.WarmRequests.Single().ModelId); Assert.AreEqual(0, resident.Generations);
        Assert.IsTrue(Find<TextBlock>(control, "VideoPrep.RuntimeStatus").Text.Contains("Полный GPU")); owner.Close();
    });
    [TestMethod]
    public async Task IndependentOptionsDoNotRetireTheResidentBetweenCalls()
    {
        using var files = new VideoPreparationTests.TestFiles(); var resident = new Resident();
        ApplicationBackgroundOperations.RegisterModel(resident, r => r.RetireAsync());
        await resident.WarmAsync(new("core", "test.gguf", EngineSetSelection.ActiveId), default);
        var p = VideoWorldTests.Root(); var batch = VideoWorldTests.Batch(p);
        await VideoWorldWork.RunAsync(batch, "test", resident, new(files.File("receipts")), (_, _, _) => Task.CompletedTask, default);
        Assert.AreEqual(3, resident.Generations); Assert.IsNotNull(resident.RuntimeInfo); Assert.HasCount(1, resident.WarmRequests);
    }
}
