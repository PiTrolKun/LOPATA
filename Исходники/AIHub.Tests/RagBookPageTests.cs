using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Models;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagBookPageTests
{
    [TestMethod]
    public Task RealHiddenPreparationPausesAndResumesAfterHostRestart() => Sta(async () =>
    {
        var book = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_BOOK");
        var models = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_MODELS");
        var evidence = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_OUTPUT");
        if (book is null || models is null || evidence is null) { Assert.Inconclusive("Explicit local integration input is required."); return; }
        var previousController = ApplicationBackgroundOperations.Current; var previousExit = ApplicationBackgroundOperations.ExitToken;
        var previousLicense = ComponentLicenseGate.ConfirmAsync;
        using var firstLifetime = new CancellationTokenSource(); using var secondLifetime = new CancellationTokenSource();
        var folder = Path.Combine(evidence, "ui-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var statePath = Path.Combine(folder, "background.json"); var drafts = new RagBookDraftStore(Path.Combine(folder, "draft.json"));
        drafts.Save(new() { Name = "Hidden recovery", OutputDirectory = folder, Books = [book] });
        var controller = new BackgroundOperationController(new(statePath));
        var localization = new LocalizationService(); localization.Load("ru");
        var owner = new Window { Width = 1200, Height = 900, Left = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        RagBookWorkspaceControl? workspace = null;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = firstLifetime.Token;
            Theme(owner, true); workspace = new(localization.T, models, folder, 4, _ => { }, () => { }, drafts);
            owner.Content = workspace; owner.Show();
            All(workspace).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "RagBooks.Start").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            owner.Hide();
            var library = RagBookPreparation.LibraryDirectory("Hidden recovery", folder);
            string? checkpoint = null; var completed = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
            while (completed == 0)
            {
                await Task.Delay(40);
                if (!controller.IsRunning) Assert.Fail("Preparation ended before the interruption point.");
                if (clock.Elapsed > TimeSpan.FromMinutes(4)) Assert.Fail("No vector checkpoint in four minutes.");
                checkpoint = Directory.Exists(library) ? Directory.GetFiles(library, "checkpoint.sqlite", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (checkpoint is null) continue;
                try
                {
                    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + checkpoint + ";Mode=ReadOnly;Pooling=False"); db.Open();
                    using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM chunks WHERE vector IS NOT NULL";
                    completed = Convert.ToInt32(command.ExecuteScalar());
                }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
            }
            await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            var operationId = controller.State.Id; var job = RagBookPreparation.Read(controller.State);
            Assert.IsFalse(controller.BlocksNewWork);
            using (controller.BeginAuxiliary()) { Assert.IsTrue(controller.IsRunning); }
            var second = new BackgroundOperationState { Kind = "another.utility", Title = "Another utility", Input = System.Text.Json.JsonSerializer.SerializeToElement(new { }) };
            Assert.AreEqual(42, await controller.RunAsync(second, _ => Task.FromResult(42), () => Task.CompletedTask, firstLifetime.Token));
            Assert.AreEqual(operationId, controller.PausedOperations.Single().Id);
            firstLifetime.Cancel(); await controller.WaitForAllIdleAsync();
            while (controller.IsRunning) await Task.Delay(10);
            await Task.Delay(40); workspace.Dispose(); owner.Content = null;
            controller = new(new(statePath)); controller.Load();
            var restoredJob = controller.FindOperation(RagBookPreparation.Kind)!;
            Assert.IsTrue(restoredJob.UserPaused); Assert.AreEqual(operationId, restoredJob.Id);
            Assert.AreEqual(job.Id, RagBookPreparation.Read(restoredJob).Id);
            ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = secondLifetime.Token;
            workspace = new(localization.T, models, folder, 4, _ => { }, () => { }, drafts); owner.Content = workspace;
            controller.Register(RagBookPreparation.Kind, (state, token) => workspace.ResumePreparationAsync(state, token));
            Assert.IsFalse(controller.IsRunning, "Explicit pause must survive host restart.");
            await controller.ResumeAsync(operationId, secondLifetime.Token);
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
            Assert.AreEqual(operationId, controller.State.Id);
            Assert.IsTrue(File.Exists(Path.Combine(job.Directory, "current.json")));
            using var verify = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + checkpoint + ";Mode=ReadOnly;Pooling=False"); verify.Open();
            using var count = verify.CreateCommand(); count.CommandText = "SELECT count(*) FROM chunks WHERE vector IS NULL";
            Assert.AreEqual(0L, count.ExecuteScalar());
            owner.Show(); owner.UpdateLayout(); Capture(workspace, "ready-after-host-restart.png");
            Console.WriteLine($"Preserved {completed} committed vectors; pause/restart/resume finished in {clock.Elapsed.TotalSeconds:0.0}s. Library: {library}");
        }
        finally
        {
            firstLifetime.Cancel(); secondLifetime.Cancel(); workspace?.Dispose(); await controller.WaitForIdleAsync(); owner.Close();
            ApplicationBackgroundOperations.Current = previousController; ApplicationBackgroundOperations.ExitToken = previousExit; ComponentLicenseGate.ConfirmAsync = previousLicense;
        }
    }, TimeSpan.FromMinutes(8));

    [TestMethod]
    [DataRow("ru", true, 1200d)]
    [DataRow("ru", false, 1200d)]
    [DataRow("en", true, 1200d)]
    [DataRow("en", false, 1200d)]
    [DataRow("ru", true, 700d)]
    [DataRow("ru", false, 700d)]
    [DataRow("en", true, 700d)]
    [DataRow("en", false, 700d)]
    public Task WorkspaceAndDownloadsRenderWithoutStartingProcessing(string language, bool dark, double width) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rag-ui-" + Guid.NewGuid().ToString("N"));
        Window? owner = null; RagBookWorkspaceControl? workspace = null; RagBookComponentsControl? components = null;
        try
        {
            Directory.CreateDirectory(root); var source = Path.Combine(root, "Короткий пример.txt");
            File.WriteAllText(source, "Ямб, хорей, рифма.\nСтроки сохраняются.\n");
            var store = new RagBookDraftStore(Path.Combine(root, "workspace.json"));
            store.Save(new() { Name = "Стихосложение / Versification", OutputDirectory = root, Books = [source] });
            var l = new LocalizationService(); l.Load(language);
            owner = new Window { Width = 10, Height = 10, Opacity = 0, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            Theme(owner, dark); owner.Show();
            var sourceOpens = 0; var licenseOpens = 0;
            workspace = new(l.T, Path.Combine(root, "models"), root, 3, _ => sourceOpens++, () => licenseOpens++, store)
            { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            owner.Width = width; owner.Height = 820; owner.Content = workspace;
            await Task.Delay(80); owner.UpdateLayout();
            Button Find(string id) => All(workspace).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "RagBooks." + id);
            Assert.IsTrue(Find("Start").IsEnabled); Assert.IsFalse(Find("Pause").IsEnabled);
            Assert.IsTrue(Find("Add").IsEnabled);
            Assert.IsTrue(Find("AddLexicon").IsEnabled);
            Assert.AreEqual(l.T("RagBooks.AddLexicon"), AutomationProperties.GetName(Find("AddLexicon")));
            var layer = All(workspace).OfType<System.Windows.Controls.ComboBox>()
                .Single(c => AutomationProperties.GetAutomationId(c) == "RagBooks.SearchLayer");
            Assert.AreEqual(2, layer.Items.Count);
            layer.SelectedIndex = 1; Assert.IsFalse(Find("Find").IsEnabled);
            layer.SelectedIndex = 0;
            var cpuTarget = All(workspace).OfType<System.Windows.Controls.ComboBox>()
                .Single(c => AutomationProperties.GetAutomationId(c) == "RagBooks.CpuTarget");
            Assert.AreEqual(50, cpuTarget.SelectedItem);
            cpuTarget.SelectedItem = 75;
            Assert.AreEqual(75, store.Load().CpuTargetPercent);
            Assert.IsFalse(All(workspace).OfType<TextBlock>().Any(t => t.Text.StartsWith("RagBooks.", StringComparison.Ordinal)));
            var preview = All(workspace).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "RagBooks.Preview");
            Assert.Contains("Ямб, хорей", preview.Text);
            Assert.IsTrue(preview.ActualWidth < width);
            Find("OpenSource").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(1, sourceOpens);
            Find("Licenses").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(1, licenseOpens);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "models")));
            Capture(workspace, $"workspace-{language}-{dark}-{width:0}.png");
            Find("Components").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); owner.UpdateLayout();
            components = All(workspace).OfType<RagBookComponentsControl>().Single();
            Assert.IsTrue(components.IsVisible); Assert.AreEqual(0, owner.OwnedWindows.Count);
            Assert.IsTrue(All(components).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "RagBooks.DownloadRequired" && b.IsEnabled));
            Assert.IsFalse(All(components).OfType<TextBlock>().Any(t => t.Text.StartsWith("RagBooks.", StringComparison.Ordinal)));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "models")), "Opening acquisition UI must not download anything.");
            Capture(components, $"components-{language}-{dark}-{width:0}.png");
            owner.Hide(); await Task.Delay(30); Assert.IsFalse(workspace.IsVisible);
            owner.Show(); await Task.Delay(30); owner.UpdateLayout();
            Assert.IsTrue(components.IsVisible);
            Assert.AreSame(components, All(workspace).OfType<RagBookComponentsControl>().Single());
            l.Load(language == "ru" ? "en" : "ru"); workspace.RefreshLocalization();
            Assert.IsTrue(All(components).OfType<TextBlock>().Any(t => t.Text == l.T("RagBooks.Components")));
            Assert.AreEqual(l.T("RagBooks.SearchLexicon"), ((ComboBoxItem)layer.Items[1]).Content);
            l.Load(language); workspace.RefreshLocalization();
            Assert.IsTrue(workspace.GoBack()); Assert.IsFalse(components.IsVisible);
            Assert.AreEqual(0, owner.OwnedWindows.Count);
            Find("Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(workspace.SaveForExit()); workspace.Dispose(); workspace = null;
            Assert.AreEqual(0, store.Load().Books.Length); Assert.IsTrue(File.Exists(source));
            Assert.AreEqual(75, store.Load().CpuTargetPercent);
        }
        finally
        {
            workspace?.Dispose(); owner?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [TestMethod]
    public Task HiddenEmbeddedAcquisitionKeepsWorkingAndResumesThroughSharedController() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rag-hidden-" + Guid.NewGuid().ToString("N"));
        var background = new BackgroundOperationController(new(Path.Combine(root, "operation.json")));
        var previous = ApplicationBackgroundOperations.Current; var previousExit = ApplicationBackgroundOperations.ExitToken;
        var previousLicense = ComponentLicenseGate.ConfirmAsync;
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0; Window? owner = null; RagBookComponentsControl? control = null;
        try
        {
            ApplicationBackgroundOperations.Current = background; ApplicationBackgroundOperations.ExitToken = lifetime.Token;
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var work = new RagBookAcquisitionWork(background, _ => new ViewPreparation(async (id, token) =>
            {
                if (++attempts == 1) entered.TrySetResult(); else replay.TrySetResult();
                await finish.Task.WaitAsync(token);
                return new() { ModelArtifactId = id, Status = ManagedModelStatuses.Installed };
            }));
            var l = new LocalizationService(); l.Load("ru");
            owner = new Window { Width = 900, Height = 700, ShowInTaskbar = false, ShowActivated = false, Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            Theme(owner, true);
            control = new(l.T, Path.Combine(root, "models"), 4, () => { }, work); owner.Content = control; owner.Show();
            Button Find(string id) => All(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
            Find("RagBooks.Download." + RagBookModels.EmbeddingId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var operationId = background.State!.Id;
            owner.Hide(); owner.Content = null; await Task.Delay(40);
            Assert.Throws<BackgroundOperationWaitingException>(() => BackgroundUserDecision.RequireVisible(owner));
            Assert.IsTrue(background.IsRunning); Assert.AreEqual(operationId, background.State.Id);
            owner.Content = control; owner.Show();
            await background.PauseAsync(); Assert.AreEqual(BackgroundOperationPhase.Paused, background.State.Phase);
            owner.Hide(); await background.ResumeAsync(default); await replay.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(background.IsRunning); finish.TrySetResult();
            await background.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(40);
            owner.Show(); await Task.Delay(20);
            Assert.AreEqual(BackgroundOperationPhase.Completed, background.State.Phase); Assert.AreEqual(operationId, background.State.Id);
            Assert.AreEqual(2, attempts); Assert.IsTrue(Find("RagBooks.CheckFiles").IsEnabled);
        }
        finally
        {
            lifetime.Cancel(); control?.Dispose();
            await background.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); owner?.Close();
            ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousExit;
            ComponentLicenseGate.ConfirmAsync = previousLicense;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [TestMethod]
    public Task TrayHasOneLocalizedEntryWithCurrentStatusAndWorkingOpenCommand() => Sta(() =>
    {
        var l = new LocalizationService(); l.Load("ru");
        using var tray = ApplicationTrayController.TryCreate(l.T, () => { }, () => { }, () => { }, () => { });
        if (tray is null) Assert.Inconclusive("Windows tray is unavailable in this environment.");
        var opens = 0; tray!.AddRagBooksCommand(() => ++opens); tray.AddRagBooksCommand(() => ++opens);
        tray.SetRagBooksStatus(l.T("RagBooks.State.Paused"));
        var field = typeof(ApplicationTrayController).GetField("_ragBooks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var menu = (System.Windows.Forms.ToolStripMenuItem)field.GetValue(tray)!;
        Assert.Contains(l.T("RagBooks.State.Paused"), menu.Text ?? ""); menu.PerformClick(); Assert.AreEqual(1, opens);
        l.Load("en"); tray.SetRagBooksStatus(l.T("RagBooks.State.Running")); tray.RefreshLocalization();
        Assert.Contains(l.T("RagBooks.Title"), menu.Text ?? ""); Assert.Contains(l.T("RagBooks.State.Running"), menu.Text ?? "");
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task PublishedDictionarySearchWorksThroughEmbeddedPage() => Sta(async () =>
    {
        var library = Environment.GetEnvironmentVariable("LOPATA_LEXICON_READY");
        if (library is null) { Assert.Inconclusive("Published local dictionary input required."); return; }
        var root = Path.Combine(Path.GetTempPath(), "lexicon-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var l = new LocalizationService(); l.Load("ru");
        var drafts = new RagBookDraftStore(Path.Combine(root,"workspace.json"));
        Assert.AreEqual(library,RagBookPreparation.LibraryDirectory("Dictionary integration",Path.GetDirectoryName(library)!));
        drafts.Save(new() { Name = "Dictionary integration", OutputDirectory = Path.GetDirectoryName(library)! });
        var owner = new Window { Width = 1200, Height = 1050, Left = -20000, ShowInTaskbar = false, ShowActivated = false };
        RagBookWorkspaceControl? workspace = null;
        try
        {
            Theme(owner,true);
            workspace = new(l.T,Path.Combine(root,"no-models"),root,4,_ => { },() => { },drafts);
            owner.Content = workspace; owner.Show(); await Task.Delay(80); owner.UpdateLayout();
            var layer = All(workspace).OfType<System.Windows.Controls.ComboBox>().Single(c => AutomationProperties.GetAutomationId(c)=="RagBooks.SearchLayer");
            Assert.AreEqual(1,layer.SelectedIndex);
            var find = All(workspace).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b)=="RagBooks.Find");
            Assert.IsTrue(find.IsEnabled);
            All(workspace).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t)=="RagBooks.Query").Text="замок";
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!find.IsEnabled && clock.Elapsed<TimeSpan.FromSeconds(30)) await Task.Delay(50);
            Assert.IsTrue(find.IsEnabled);
            var hits = All(workspace).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t)=="RagBooks.Hits");
            Assert.Contains("за́мок",hits.Text); Assert.Contains("замо́к",hits.Text);
            Assert.Contains(l.T("RagBooks.RequiresContext"),hits.Text);
            foreach (var scroll in All(workspace).OfType<ScrollViewer>()) scroll.ScrollToBottom();
            owner.UpdateLayout(); Capture(workspace,"dictionary-ready-query.png");
            Assert.IsFalse(Directory.Exists(Path.Combine(root,"no-models")));
        }
        finally { workspace?.Dispose(); owner.Close(); Directory.Delete(root,true); }
    });

    private sealed class ViewPreparation(Func<string, CancellationToken, Task<ManagedModelArtifactCard>> prepare) : IRagBookModelPreparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards => [];
        public Task<ManagedModelArtifactCard> PrepareAsync(string id, bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token) => prepare(id, token);
        public void Dispose() { }
    }

    private static IEnumerable<DependencyObject> All(DependencyObject root) => ScenarioNavigationTests.LogicalDescendants(root);
    private static void Theme(Window owner, bool dark)
    {
        System.Windows.Media.Brush Brush(string hex) => (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(hex)!;
        owner.Resources["WindowBackgroundBrush"] = Brush(dark ? "#111827" : "#F3F3F3");
        owner.Resources["PanelBrush"] = Brush(dark ? "#172033" : "#FFFFFF");
        owner.Resources["SecondaryButtonBackgroundBrush"] = Brush(dark ? "#111827" : "#F8F8F8");
        owner.Resources["TextPrimaryBrush"] = Brush(dark ? "#F8FAFC" : "#1F1F1F");
        owner.Resources["TextSecondaryBrush"] = Brush(dark ? "#AAB4C4" : "#5D6470");
        owner.Resources["LineBrush"] = Brush(dark ? "#2D374B" : "#DADDE3");
        owner.Resources["StepBadgeBrush"] = Brush(dark ? "#1E3A5F" : "#EAF1FF");
        owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
    }
    private static void Capture(FrameworkElement content, string name)
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_RAG_UI_CAPTURE"); if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle((System.Windows.Media.Brush)content.FindResource("WindowBackgroundBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(path, name)); png.Save(stream);
    }
    private static async Task Sta(Func<Task> action, TimeSpan? timeout = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = Run(); Dispatcher.Run();
            async Task Run()
            {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(40));
    }
}
