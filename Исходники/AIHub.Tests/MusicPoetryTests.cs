using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-poetry-test-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-poetry-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private MusicPoetrySessions Store => new(_root);
    private static MusicPoetrySession Session(string song = "[Verse]\nСтихи с ё и тегами [MALE]") => MusicPoetryProtocol.Create(new() { Lyrics = song }, key => key);
    private static UserContextService UserContext() => new(new UserProfileStore(), new IpLocationService());
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, args);
    private static void Model(MusicPoetryWindow w) => w.GetType().GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w,
        new DebugModelInfo { Name = "Test core", Path = "fixture.gguf", Format = "gguf", IsCoreModel = true });

    [TestMethod]
    public void WorkspacePromptKeepsVisibleUnicodeAndRoundTripsJsonCharacters()
    {
        var song = "[Verse]\nЧёрный кот: \"мяу\". <>& \\ путь\n黑猫 🎵";
        var s = Session(song); s.Parameters = new(MusicStudioRuntime.Variation, "Блюз и гитара");
        var prompt = MusicPoetryProtocol.System(s, "Русская памятка");
        StringAssert.Contains(prompt, "Чёрный кот"); StringAssert.Contains(prompt, "Блюз и гитара");
        Assert.IsFalse(prompt.Contains("\\u04", StringComparison.OrdinalIgnoreCase));
        using var workspace = JsonDocument.Parse(prompt[(MusicPoetryProtocol.Instructions.Length + 1)..]);
        Assert.AreEqual(song, workspace.RootElement.GetProperty("song").GetString());
        var transport = JsonSerializer.Serialize(new { content = prompt });
        using var envelope = JsonDocument.Parse(transport);
        Assert.AreEqual(prompt, envelope.RootElement.GetProperty("content").GetString());
    }
    [TestMethod]
    public async Task InstalledCoreReplaysReportedRevisionWithOnlyWorkspaceEncodingChanged()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_POETRY_REPLAY");
        if (string.IsNullOrWhiteSpace(source)) Assert.Inconclusive("Opt-in read-only saved revision comparison.");
        var s = JsonSerializer.Deserialize<MusicPoetrySession>(await File.ReadAllTextAsync(source))!;
        s.Messages = s.Messages.Take(3).ToList(); // first successful turn and its follow-up, without retries
        var settings = JsonSerializer.Deserialize<StorageSettings>(await File.ReadAllTextAsync(AppDataPaths.StorageSettingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var model = MusicPoetryRuntime.Discover(settings).Single(m => m.Path.Equals(s.ModelPath, StringComparison.OrdinalIgnoreCase));
        var l = new LocalizationService(); l.Load("ru"); var guide = MusicPoetryProtocol.Guidance(s.Parameters.Variation, l.T);
        var workspace = new { target = MusicModelVariants.Name(s.Parameters.Variation), targetId = s.Parameters.Variation,
            song = s.Lyrics, parameters = s.Parameters.Text, guidance = guide };
        var baseline = MusicPoetryProtocol.Instructions + "\n" + JsonSerializer.Serialize(workspace);
        var fixedPrompt = MusicPoetryProtocol.System(s, guide);
        var folder = Path.Combine(Path.GetTempPath(), "lopata-poetry-unicode-replay"); Directory.CreateDirectory(folder);
        using var runtime = new LlamaServerRuntimeService(UserContext());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try {
            await runtime.PrepareAsync(model, _ => {}, deadline.Token);
            var results = new List<object>();
            foreach (var (name, prompt) in new[] { ("escaped", baseline), ("unicode", fixedPrompt) }) {
                var inputTokens = await runtime.CountPoetryAsync(prompt, s.Messages, deadline.Token);
                var body = JsonSerializer.Serialize(new { model = "local", messages = new[] { new { role = "system", content = prompt } }
                        .Concat(s.Messages.Select(m => new { role = m.Role, content = m.Text })).ToArray(),
                    max_tokens = 4096, temperature = .8, top_p = .95, seed = 42, stream = false, cache_prompt = false,
                    chat_template_kwargs = new { enable_thinking = false }, response_format = MusicPoetryProtocol.Schema });
                var raw = await runtime.GeneratePrivateJsonAsync(model, body, deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(folder, name + ".json"), raw);
                using var response = JsonDocument.Parse(raw); var choice = response.RootElement.GetProperty("choices")[0];
                var content = choice.GetProperty("message").GetProperty("content").GetString()!;
                var finish = choice.GetProperty("finish_reason").GetString();
                var completionTokens = response.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32();
                results.Add(new { name, inputTokens, completionTokens, finish, unicodeEscapes = System.Text.RegularExpressions.Regex.Matches(content, @"\\u[0-9a-fA-F]{4}").Count });
                await File.WriteAllTextAsync(Path.Combine(folder, "comparison.json"), JsonSerializer.Serialize(results));
                if (name == "unicode") { Assert.AreEqual("stop", finish); Assert.IsNotNull(MusicPoetryProtocol.Parse(content).Song); }
            }
        } finally { await runtime.RetirePoetryAsync(); }
    }
    [TestMethod]
    public async Task InstalledCoreAssemblesReportedDiscussionAndPreviewMatchesNative()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_POETRY_NATIVE") != "1") Assert.Inconclusive("Opt-in assembly probe.");
        var settings = JsonSerializer.Deserialize<StorageSettings>(await File.ReadAllTextAsync(AppDataPaths.StorageSettingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var model = MusicPoetryRuntime.Discover(settings).FirstOrDefault(m => m.IsCoreModel); Assert.IsNotNull(model);
        var fixture = Environment.GetEnvironmentVariable("LOPATA_POETRY_ASSEMBLY_FIXTURE");
        var s = string.IsNullOrWhiteSpace(fixture) ? Session("")
            : JsonSerializer.Deserialize<MusicPoetrySession>(await File.ReadAllTextAsync(fixture))!;
        if (s.Messages.Count == 0) s.Messages.Add(new("user", "Кот грызёт зарядку, потом прячется от наказания. Хочу рок-песню.", "idea"));
        var l = new LocalizationService(); l.Load("ru");
        s.Messages.Add(new("user", l.T("Music.Poetry.AssemblyRequest"), "assembly"));
        var preview = MusicPoetryTokenPreview.Load(model); Assert.IsTrue(preview.UsesTokenizer);
        var guide = MusicPoetryProtocol.Guidance(s.Parameters.Variation, l.T);
        var previewTokens = preview.Count(MusicPoetryProtocol.System(s, guide), s.Messages);
        using var runtime = new MusicPoetryRuntime(UserContext());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        MusicPoetryContext? context = null;
        var written = await runtime.GenerateAsync(model, s, guide, c => context = c, deadline.Token, true);
        var folder = Path.Combine(Path.GetTempPath(), "lopata-poetry-assembly-probe"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,"reply.json"),written);
        await File.WriteAllTextAsync(Path.Combine(folder,"context.json"),JsonSerializer.Serialize(new { previewTokens, exactTokens=context!.Tokens }));
        var assembled = MusicPoetryProtocol.Parse(written, true);
        Assert.IsFalse(string.IsNullOrWhiteSpace(assembled.Song)); Assert.IsFalse(string.IsNullOrWhiteSpace(assembled.Parameters));
        Assert.AreEqual("", assembled.Chat); Assert.IsNull(assembled.Variation);
        Assert.IsTrue(assembled.Parameters.Contains("рок", StringComparison.OrdinalIgnoreCase)
            || assembled.Parameters.Contains("rock", StringComparison.OrdinalIgnoreCase), "Requested rock genre must reach parameters.");
        Assert.IsTrue(Math.Abs(previewTokens-context.Tokens) < 256, "Preview should be close to the native tokenizer, not a byte ceiling.");
    }
    [TestMethod]
    public async Task InstalledCoreCanRouteARealShortRussianSong()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_POETRY_NATIVE") != "1") Assert.Inconclusive("Opt-in installed core probe.");
        var settings = JsonSerializer.Deserialize<StorageSettings>(File.ReadAllText(AppDataPaths.StorageSettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var model = MusicPoetryRuntime.Discover(settings).FirstOrDefault(m => m.IsCoreModel);
        Assert.IsNotNull(model);
        var session = Session(""); session.Messages.Add(new("user", "Напиши короткий куплет из четырёх строк о весеннем дожде, без обсуждения и без изменения параметров.", "probe"));
        using var runtime = new MusicPoetryRuntime(UserContext()); using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        MusicPoetryContext? context = null;
        var raw = await runtime.GenerateAsync(model, session, MusicPoetryProtocol.Guidance(session.Parameters.Variation, key => key), c => context = c, deadline.Token);
        var reply = MusicPoetryProtocol.Parse(raw); Assert.IsNotNull(reply.Song); Assert.IsTrue(reply.Song.Length > 10);
        Assert.IsNotNull(context); Assert.IsFalse(context.Estimated); Assert.IsTrue(context.Tokens + 4096 + 128 <= context.Capacity);
        var evidence = Path.Combine(Path.GetTempPath(), "lopata-poetry-native"); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "reply.json"), raw);
        await File.WriteAllTextAsync(Path.Combine(evidence, "context.json"), JsonSerializer.Serialize(new { model.Name, context.Tokens, context.Capacity, context.Estimated }));
    }

    [TestMethod]
    public void OnlySongStartsPersistenceAndClearingKeepsAllHistoryAndDrafts()
    {
        var s = Session(""); s.Messages.Add(new("user", "Только обсуждение", "t")); s.Draft = "Черновик";
        Store.Save(s); Assert.AreEqual(0, Store.List().Count); Assert.IsFalse(Directory.Exists(_root));
        s.Lyrics = "[Chorus]\nНовый стих"; Store.Save(s); Assert.AreEqual(0, s.LyricsSteps.Count);
        s.CommitLyrics(); s.Lyrics = ""; s.CommitLyrics(); Store.Save(s);
        var loaded = Store.Load(s.Id);
        Assert.IsTrue(loaded.Persistent); Assert.AreEqual("", loaded.Lyrics);
        CollectionAssert.AreEqual(new[] { "[Chorus]\nНовый стих", "" }, loaded.LyricsSteps);
        Assert.AreEqual("Черновик", loaded.Draft); Assert.AreEqual("Только обсуждение", loaded.Messages.Single().Text);
    }
    [TestMethod]
    public void TwoHistoriesPreserveBranchesAndUncommittedEdits()
    {
        var s = Session("A"); s.Lyrics = "B"; s.CommitLyrics(); s.Lyrics = "C"; s.CommitLyrics();
        var p0 = s.Parameters; s.Parameters = new(MusicAceCatalog.Variation, "p1"); s.CommitParameters();
        s.NavigateLyrics(-1); s.Lyrics = "B edited"; s.NavigateLyrics(-1);
        Assert.AreEqual("A", s.Lyrics); CollectionAssert.AreEqual(new[] { "A", "B", "C", "B edited" }, s.LyricsSteps);
        Assert.AreEqual("p1", s.Parameters.Text); s.NavigateParameters(-1); Assert.AreEqual(p0, s.Parameters);
        s.Apply(new("Discuss", "Model song", "model parameters", MusicDiffRhythmCatalog.Variation));
        Assert.AreEqual("Model song", s.Lyrics); Assert.AreEqual("model parameters", s.Parameters.Text);
        Assert.IsTrue(s.LyricsSteps.Contains("C")); Assert.IsTrue(s.ParameterSteps.Any(p => p.Text == "p1"));
        Store.Save(s); Assert.AreEqual(s.LyricsCursor, Store.Load(s.Id).LyricsCursor);
    }
    [TestMethod]
    public void DamagedPrimaryRecoversBackupWithoutDestroyingItAndIdentityIsValidated()
    {
        var s = Session("First"); Store.Save(s); s.Lyrics = "Second"; Store.Save(s);
        var path = Path.Combine(_root, s.Id + ".json"); File.WriteAllText(path, "{broken");
        var restored = Store.Load(s.Id); Assert.AreEqual("First", restored.Lyrics);
        restored.Lyrics = "Recovered"; Store.Save(restored); Assert.AreEqual("Recovered", Store.Load(s.Id).Lyrics);
        Assert.AreEqual("First", JsonSerializer.Deserialize<MusicPoetrySession>(File.ReadAllText(path + ".bak"))!.Lyrics);
        Assert.AreEqual(1, Directory.GetFiles(_root, "*.damaged-*").Length);
        Assert.ThrowsExactly<InvalidDataException>(() => Store.Load("../../outside"));
        Assert.ThrowsExactly<InvalidDataException>(() => Store.Delete("../../outside"));
        Store.Delete(s.Id); Assert.AreEqual(0, Store.List().Count);
    }
    [TestMethod]
    public void InvalidStateCannotOverwriteGoodSavedLyrics()
    {
        var s = Session("Valid"); Store.Save(s); s.LyricsCursor = 999;
        Assert.ThrowsExactly<InvalidDataException>(() => Store.Save(s)); Assert.AreEqual("Valid", Store.Load(s.Id).Lyrics);
    }
    [TestMethod]
    public void StructuredCorruptionAlsoRecoversAndIncompleteArchiveSurvivesLaterSuccess()
    {
        var s = Session("First"); s.IncompleteReplies.Add(new(DateTimeOffset.UtcNow, "Partial older")); Store.Save(s);
        s.Lyrics = "Second"; Store.Save(s);
        var path = Path.Combine(_root,s.Id + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Schema\": 1", "\"Schema\": 99"));
        var restored = Store.Load(s.Id); Assert.AreEqual("First", restored.Lyrics);
        restored.PartialReply = "Partial newer"; restored.IncompleteReplies.Add(new(DateTimeOffset.UtcNow,"Partial newer"));
        restored.Apply(new("", "Success", null, null)); Store.Save(restored);
        Assert.AreEqual(2, Store.Load(s.Id).IncompleteReplies.Count); Assert.AreEqual("", restored.PartialReply);
    }
    [TestMethod]
    public void ProtocolRoutesSongWithTagsAndRejectsBrokenOrUnknownFields()
    {
        var s = Session(); var song = "[Verse: MALE]\nРаз, два\n[Chorus]\nПрипев";
        var reply = MusicPoetryProtocol.Parse(JsonSerializer.Serialize(new { chat = "Вот вариант.", song, parameters = "Style: rock", variation = MusicAceCatalog.Variation, actions = new[] { "song", "parameters", "variation" } }));
        s.Apply(reply); Assert.AreEqual(song, s.Lyrics); Assert.AreEqual("Вот вариант.", reply.Chat);
        Assert.AreEqual(MusicAceCatalog.Variation, s.Parameters.Variation);
        var original = s.Lyrics;
        Assert.Throws<JsonException>(() => MusicPoetryProtocol.Parse("{\"song\":\"partial"));
        Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse("{\"chat\":\"x\",\"song\":null,\"parameters\":null,\"variation\":\"unknown\"}"));
        Assert.AreEqual(original, s.Lyrics);
    }
    [TestMethod]
    public async Task ContextRollsWholeTurnsKeepsFixedWorkspaceAndExcludesUserOnlyNotices()
    {
        var s = Session("Visible song"); s.Messages.AddRange([new("user", "old user", "a"), new("assistant", "old answer", "a"),
            new("notice", "Model switched secret notice", "b"), new("user", "new user", "c")]);
        var calls = 0;
        var c = await MusicPoetryContextWindow.BuildAsync(s, "Visible guide", 1200, 300, (system, messages) => {
            calls++; StringAssert.Contains(system, "Visible song"); StringAssert.Contains(system, "Visible guide");
            Assert.IsFalse(system.Contains("Model switched")); return Task.FromResult(messages.Count * 300); }, false);
        Assert.AreEqual(2, calls); Assert.AreEqual(1, c.Messages.Length); Assert.AreEqual("c", c.Messages[0].TurnId);
        Assert.IsFalse(s.Messages[0].InContext); Assert.IsFalse(s.Messages[1].InContext); Assert.IsFalse(s.Messages[2].InContext);
        Assert.IsTrue(s.Messages[3].InContext); Assert.AreEqual(4, s.Messages.Count);
    }
    [TestMethod]
    public async Task OversizeFixedContextIsRejectedWithoutTrimmingSong()
    {
        var s = Session(new string('я', 10000)); s.Messages.Add(new("user", "Write", "t"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MusicPoetryContextWindow.BuildAsync(s, "guide", 1000, 200,
            (system, messages) => Task.FromResult(2000), false));
        Assert.AreEqual(10000, s.Lyrics.Length); Assert.AreEqual(1, s.Messages.Count);
    }
    [TestMethod]
    public async Task AuxiliaryLeaseExcludesMainGenerationAndReleasesIdempotently()
    {
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "operation.json")));
        var lease = controller.BeginAuxiliary(); Assert.IsTrue(controller.IsRunning);
        var idle = controller.WaitForIdleAsync(); Assert.IsFalse(idle.IsCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(() => controller.BeginAuxiliary());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.RunAsync(new() { Kind = "test", Title = "Test" },
            _ => Task.FromResult(1), () => Task.CompletedTask, CancellationToken.None));
        lease.Dispose(); lease.Dispose(); await idle; Assert.IsFalse(controller.IsRunning);
        Assert.AreEqual(1, await controller.RunAsync(new() { Kind = "test", Title = "Test", Input = JsonSerializer.SerializeToElement("input") }, _ => Task.FromResult(1), () => Task.CompletedTask, CancellationToken.None));
        using var second = controller.BeginAuxiliary(); Assert.IsTrue(controller.IsRunning);
    }
    [TestMethod]
    public void BinTruncationKeepsAvailableContentWhileExistingClientsStillRejectIt()
    {
        var response = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Partial poem\"}}],\"usage\":{\"completion_tokens\":4096}}";
        var part = "";
        Assert.ThrowsExactly<BackgroundOperationWaitingException>(() => FinancialModelRuntime.ParseResponse(response, 4096, text => part = text));
        Assert.AreEqual("Partial poem", part);
        Assert.ThrowsExactly<BackgroundOperationWaitingException>(() => FinancialModelRuntime.ParseResponse(response, 4096));
    }
    [TestMethod]
    public Task WindowCopiesSeedOnceCommitsActivityRoutesReplyAndPreservesOnError() => ScenarioNavigationTests.Sta(() =>
    {
        var snapshot = new MusicProjectSnapshot { Lyrics = "Imported song", Title = "Imported title" };
        var fake = new FakeRuntime();
        var w = new MusicPoetryWindow(() => snapshot, new(), UserContext(), key => key, "ru", Store, fake);
        Model(w);
        var lyrics = Field<System.Windows.Controls.TextBox>(w, "_lyrics"); var input = Field<System.Windows.Controls.TextBox>(w, "_input");
        var session = Field<MusicPoetrySession>(w, "_session"); snapshot = snapshot with { Lyrics = "Source changed" };
        Assert.AreEqual("Imported song", lyrics.Text);
        Call(w, "Activity", "song"); lyrics.Text = "Edited song";
        Assert.AreEqual(1, session.LyricsSteps.Count); Call(w, "Activity", "chat"); Assert.AreEqual(2, session.LyricsSteps.Count);
        input.Text = "Write a song"; var send = (Task)Call(w, "SendAsync")!; send.GetAwaiter().GetResult();
        Assert.AreEqual("[Verse]\nGenerated song", lyrics.Text); Assert.AreEqual(1, fake.Calls);
        Assert.IsFalse(session.Messages.Any(m => m.Text.Contains("Generated song")));
        Assert.AreEqual("Write a song", fake.Request!.Messages.Single().Text);
        fake.Reply = "{partial"; input.Text = "Another"; ((Task)Call(w, "SendAsync")!).GetAwaiter().GetResult();
        Assert.AreEqual("[Verse]\nGenerated song", lyrics.Text); Assert.AreEqual("{partial", session.PartialReply);
        Assert.AreEqual("{partial", Store.Load(session.Id).PartialReply); w.Close();
    });
    [TestMethod]
    public Task WindowModelSwitchPreservesEverythingAndOnlyAddsExcludedNotice() => ScenarioNavigationTests.Sta(() =>
    {
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Saved song" }, new(), UserContext(), key => key, "en", Store, new FakeRuntime());
        var s = Field<MusicPoetrySession>(w, "_session"); s.Messages.Add(new("user", "Old request", "old"));
        Call(w, "SelectModel", new DebugModelInfo { Name = "Other", Path = "other.gguf", Format = "gguf" });
        Assert.AreEqual("Saved song", s.Lyrics); Assert.AreEqual(2, s.Messages.Count); Assert.AreEqual("notice", s.Messages[^1].Role);
        Assert.IsFalse(s.Messages[^1].InContext); Assert.AreEqual("other.gguf", Store.Load(s.Id).ModelPath); w.Close();
    });
    [TestMethod]
    public Task OtherGenerationDisablesSendAndCancellationPreservesSongAndReleasesLease() => ScenarioNavigationTests.Sta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "operation.json")));
        ApplicationBackgroundOperations.Current = controller;
        try
        {
            var runtime = new HoldingRuntime();
            var w = new MusicPoetryWindow(() => new() { Lyrics = "Keep me" }, new(), UserContext(), key => key, "en", new(Path.Combine(_root,"sessions")), runtime);
            var workingStates = new List<bool>(); w.WorkingChanged += () => workingStates.Add(w.IsWorking);
            Model(w); Field<System.Windows.Controls.TextBox>(w,"_input").Text = "Request";
            using (controller.BeginAuxiliary()) { Call(w,"Availability"); Assert.IsFalse(Field<Button>(w,"_send").IsEnabled); }
            Call(w,"Availability"); Assert.IsTrue(Field<Button>(w,"_send").IsEnabled);
            var work = (Task)Call(w,"SendAsync")!; Assert.IsTrue(controller.IsRunning); Assert.IsTrue(w.IsWorking);
            Assert.IsFalse(Field<Button>(w,"_assemble").IsEnabled); w.Close();
            var frame = new System.Windows.Threading.DispatcherFrame(); var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_,_) => { if (work.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame); work.GetAwaiter().GetResult();
            Assert.IsFalse(w.IsWorking); Assert.IsFalse(controller.IsRunning); Assert.AreEqual("Keep me", Field<MusicPoetrySession>(w,"_session").Lyrics);
            CollectionAssert.AreEqual(new[] { true, false }, workingStates);
            w.Close();
        } finally { ApplicationBackgroundOperations.Current = previous; SynchronizationContext.SetSynchronizationContext(null); }
    });
    [TestMethod]
    public Task PoetryButtonRestoresThemeAndAnimationAcrossVisibilityChanges() => ScenarioNavigationTests.Sta(() =>
    {
        var button = MusicAudioUi.IconButton("PoetryChat", "M2,2 L20,20", () => { });
        button.Style = new Style(typeof(Button)) { Setters = { new Setter(Control.BorderBrushProperty, Brushes.Gray) } };
        var icon = (System.Windows.Shapes.Path)((Viewbox)button.Content).Child;
        var indicator = new MusicPoetryButtonIndicator(button);
        var host = new Window { Content = button, Width = 150, Height = 150, ShowInTaskbar = false };
        host.Resources["TextPrimaryBrush"] = Brushes.White;
        try
        {
            host.Show(); host.UpdateLayout(); indicator.SetState(true, false);
            var red = (SolidColorBrush)icon.Stroke;
            Assert.AreSame(red, button.BorderBrush);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, red.HasAnimatedProperties);
            button.Visibility = Visibility.Collapsed; Assert.IsFalse(red.HasAnimatedProperties);
            button.Visibility = Visibility.Visible;
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, red.HasAnimatedProperties);
            indicator.SetState(true, true);
            Assert.IsFalse(red.HasAnimatedProperties); Assert.AreEqual(Color.FromRgb(239, 68, 68), red.Color);
            host.Hide(); host.Show(); Assert.IsFalse(red.HasAnimatedProperties);
            indicator.SetState(true, false);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, red.HasAnimatedProperties);
            indicator.SetState(false, false); Assert.IsFalse(red.HasAnimatedProperties);
            Assert.AreEqual(Brushes.White, icon.Stroke); Assert.AreEqual(Brushes.Gray, button.BorderBrush);
            host.Resources["TextPrimaryBrush"] = Brushes.Black; Assert.AreEqual(Brushes.Black, icon.Stroke);
        }
        finally { host.Close(); Assert.IsFalse(((SolidColorBrush)button.BorderBrush).HasAnimatedProperties); }
    });

    [TestMethod]
    public Task SaveFailureKeepsLiveLyricsAndMusicCommandsRespectAuxiliaryWork() => ScenarioNavigationTests.Sta(() =>
    {
        Directory.CreateDirectory(_root); var obstacle = Path.Combine(_root, "blocked"); File.WriteAllText(obstacle, "file");
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Only live poem" }, new(), UserContext(), key => key, "ru", new(obstacle), new FakeRuntime());
        Assert.IsFalse(w.SaveForExit()); Assert.AreEqual("Only live poem", Field<System.Windows.Controls.TextBox>(w,"_lyrics").Text);
        File.Delete(obstacle); Assert.IsTrue(w.SaveForExit()); w.Close();
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "operation.json")));
        ApplicationBackgroundOperations.Current = controller;
        try {
            var view = new MusicWorkspaceControl();
            using (controller.BeginAuxiliary()) { Assert.IsFalse(view.Session.CanChangeModel); }
            Assert.IsTrue(view.Session.CanChangeModel); view.Dispose();
        } finally { ApplicationBackgroundOperations.Current = previous; }
    });
    [TestMethod]
    public Task UiIsCopyableSpellcheckedAndFitsBothThemesAndLanguages() => ScenarioNavigationTests.Sta(() =>
    {
        foreach (var lang in new[] { "ru", "en" }) foreach (var dark in new[] { false, true })
        {
            var localization = new LocalizationService(); localization.Load(lang);
            var w = new MusicPoetryWindow(() => new(), new(), UserContext(), localization.T, lang, Store, new FakeRuntime());
            Assert.IsNull(w.Owner); Assert.IsFalse(w.Topmost); Assert.IsTrue(w.ShowInTaskbar);
            foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "InputBrush" }) w.Resources[key] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
            w.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            w.Resources["TextSecondaryBrush"] = Brushes.Gray; w.Resources["LineBrush"] = Brushes.Gray;
            w.Resources["AccentBrush"] = Brushes.RoyalBlue; w.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(25,35,52) : Colors.WhiteSmoke);
            w.Resources["UiBodyFontSize"] = 16d; w.Resources["StepBadgeBrush"] = Brushes.RoyalBlue;
            w.Width = 900; w.Height = 500; w.Show(); w.UpdateLayout();
            var elements = ScenarioNavigationTests.LogicalDescendants(w).OfType<FrameworkElement>().ToArray();
            Assert.IsFalse(elements.OfType<TextBlock>().Any(t => t.Text.StartsWith("Music.Poetry.")), lang);
            var lyrics = Field<System.Windows.Controls.TextBox>(w, "_lyrics"); var input = Field<System.Windows.Controls.TextBox>(w, "_input");
            Assert.IsTrue(SpellCheck.GetIsEnabled(lyrics)); Assert.IsTrue(SpellCheck.GetIsEnabled(input));
            Assert.IsTrue(lyrics.ActualWidth >= 180); Assert.IsTrue(Field<System.Windows.Controls.TextBox>(w, "_parameters").ActualWidth >= 180);
            var send = elements.Single(e => AutomationProperties.GetAutomationId(e) == "Music.Poetry.Send");
            var location = send.TranslatePoint(new(0, 0), w); Assert.IsTrue(location.Y + send.ActualHeight < w.ActualHeight);
            var folder = Path.Combine(Path.GetTempPath(), "lopata-poetry-ui"); Directory.CreateDirectory(folder);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w.ActualWidth * 1.5), (int)(w.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(w);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(folder, lang + "-" + dark + ".png"))) encoder.Save(file);
            w.Close();
        }
    });

    [TestMethod]
    public void AssemblyRequiresBothEditorsAndRejectsDuplicateLyrics()
    {
        var schema = JsonSerializer.SerializeToElement(MusicPoetryProtocol.AssemblySchema);
        var props = schema.GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
        Assert.AreEqual("string", props.GetProperty("song").GetProperty("type").GetString());
        Assert.AreEqual("string", props.GetProperty("parameters").GetProperty("type").GetString());
        foreach (var bad in new[] {
            "{\"chat\":\"Ready?\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}",
            "{\"chat\":\"\",\"song\":\"Poem\",\"parameters\":\" \",\"variation\":null,\"actions\":[]}",
            "{\"chat\":\"Poem\",\"song\":\"Poem\",\"parameters\":\"Rock\",\"variation\":null,\"actions\":[]}" })
            Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse(bad, true));
        Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse("{\"chat\":\"Here: Poem\",\"song\":\"Poem\",\"parameters\":null,\"variation\":null,\"actions\":[]}"));
    }
    [TestMethod]
    public Task AssemblyButtonIsExplicitAtomicAndEnterUsesInlineSend() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new FakeRuntime { Reply = "{\"chat\":\"\",\"song\":\"New lyrics\",\"parameters\":\"Rock\",\"variation\":null,\"actions\":[\"song\",\"parameters\"]}" };
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Old lyrics" }, new(), UserContext(), key => key, "ru", Store, fake);
        Model(w); Call(w,"Availability"); var s = Field<MusicPoetrySession>(w,"_session"); var input = Field<TextBox>(w,"_input");
        Assert.IsFalse(input.AcceptsReturn); Assert.IsFalse(input.AcceptsTab);
        Assert.IsTrue(Field<Button>(w,"_assemble").IsEnabled);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(w).OfType<FrameworkElement>()
            .Any(e => AutomationProperties.GetAutomationId(e) == "Music.Poetry.Stop"));
        input.Text = "More action"; ((Task)Call(w,"SendCoreAsync",true)!).GetAwaiter().GetResult();
        Assert.IsTrue(fake.Assemble); Assert.IsTrue(fake.Request!.Messages[^1].Text.Contains("More action"));
        Assert.IsTrue(fake.Request.Messages[^1].Text.Contains("Music.Poetry.AssemblyRequest"));
        Assert.AreEqual("New lyrics",s.Lyrics); Assert.AreEqual("Rock",s.Parameters.Text);
        Assert.IsFalse(s.Messages.Any(m => m.Text.Contains("New lyrics")));
        var oldSteps = s.LyricsSteps.Count; var oldParams = s.ParameterSteps.Count;
        fake.Reply = "{\"chat\":\"\",\"song\":\"Do not apply\",\"parameters\":null,\"variation\":null,\"actions\":[]}";
        ((Task)Call(w,"SendCoreAsync",true)!).GetAwaiter().GetResult();
        Assert.AreEqual("New lyrics",s.Lyrics); Assert.AreEqual("Rock",s.Parameters.Text);
        Assert.AreEqual(oldSteps,s.LyricsSteps.Count); Assert.AreEqual(oldParams,s.ParameterSteps.Count);
        Assert.AreEqual(fake.Reply,s.PartialReply);
        fake.Reply = "{\"chat\":\"Discussion\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}";
        w.Show(); input.Text = "A message";
        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(w)!,0,System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        Call(w,"InputKeyDown",input,key); Assert.IsTrue(key.Handled);
        Assert.IsFalse(fake.Assemble); Assert.AreEqual("A message",s.Messages.Last(m => m.Role == "user").Text); w.Close();
    });

    private sealed class FakeRuntime : IMusicPoetryRuntime
    {
        public string Reply = "{\"chat\":\"Here is a variant.\",\"song\":\"[Verse]\\nGenerated song\",\"parameters\":null,\"variation\":null,\"actions\":[\"song\"]}";
        public int Calls; public bool Assemble; public MusicPoetrySession? Request;
        public Queue<string> Replies = new();
        public Action<MusicPoetrySession>? BeforeReply;
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance, Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false)
        { Calls++; Assemble = assemble; Request = JsonSerializer.Deserialize<MusicPoetrySession>(JsonSerializer.Serialize(request));
            BeforeReply?.Invoke(request);
            ready(new(MusicPoetryProtocol.System(request, guidance), request.Messages.ToArray(), 500, 32768, false));
            return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : Reply); }
        public void Dispose() { }
    }

    private static string Routed(string chat, string? song, string? parameters, params string[] actions) =>
        JsonSerializer.Serialize(new { chat, song, parameters, variation = (string?)null, actions });

    [TestMethod]
    public void ActionGuardRejectsClaimsWithoutChangedArtifactsAndWhitespaceCopies()
    {
        var s = Session("Old lyrics\nLine two"); s.Parameters = new(MusicStudioRuntime.Variation, "Old parameters");
        foreach (var song in new string?[] { null, s.Lyrics, "  Old lyrics  \r\nLine two \r\n" })
        {
            var reply = MusicPoetryProtocol.Parse(Routed("Переработал текст", song, null, "song"));
            Assert.AreEqual("Music.Poetry.Missing.song", MusicPoetryReplyGuard.Check(s, reply).Error);
        }
        Assert.AreEqual("Music.Poetry.Missing.parameters", MusicPoetryReplyGuard.Check(s,
            MusicPoetryProtocol.Parse(Routed("All edited", "New lyrics", "Old parameters", "song", "parameters"))).Error);
        Assert.AreEqual("Music.Poetry.UnchangedResult", MusicPoetryReplyGuard.Check(s,
            MusicPoetryProtocol.Parse(Routed("Done", "Old lyrics\nLine two", null))).Error);
        Assert.AreEqual("Music.Poetry.NoResult", MusicPoetryReplyGuard.Check(s,
            MusicPoetryProtocol.Parse(Routed("", null, null))).Error);
        Assert.AreEqual("Music.Poetry.UndeclaredAction", MusicPoetryReplyGuard.Check(s,
            MusicPoetryProtocol.Parse(Routed("", "New lyrics", null))).Error);
        Assert.AreEqual("Old lyrics\nLine two", s.Lyrics);
    }

    [TestMethod]
    public void GuardAllowsDiscussionAndDoesNotInventChangesForRepeatedAssembly()
    {
        var s = Session("Saved lyrics");
        var discussion = MusicPoetryReplyGuard.Check(s, MusicPoetryProtocol.Parse(Routed("Let's try a blues mood.", null, null)));
        Assert.IsNull(discussion.Error); Assert.AreEqual(0, discussion.Changes.Length);
        var repeated = MusicPoetryReplyGuard.Check(s, MusicPoetryProtocol.Parse(Routed("", s.Lyrics, s.Parameters.Text), true), true);
        Assert.IsNull(repeated.Error); Assert.AreEqual(0, repeated.Changes.Length);
        Assert.IsNull(repeated.Reply.Song); Assert.IsNull(repeated.Reply.Parameters);
        var steps = s.LyricsSteps.Count; s.Apply(repeated.Reply); Assert.AreEqual(steps, s.LyricsSteps.Count);
        foreach (var actions in new[] { new[] { "shell" }, new[] { "song", "song" } })
            Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse(Routed("", "New", null, actions)));
    }

    [TestMethod]
    public Task FailedClaimIsRepairedOnceAndOnlyVerifiedResultsEnterEditors() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new FakeRuntime();
        fake.Replies.Enqueue(Routed("Переработал текст", null, null, "song"));
        fake.Replies.Enqueue(Routed("", "Really revised lyrics", "New parameters", "song", "parameters"));
        var l = new LocalizationService(); l.Load("ru");
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Keep old poem" }, new(), UserContext(), l.T, "ru", Store, fake);
        Model(w); var s = Field<MusicPoetrySession>(w, "_session");
        fake.BeforeReply = request => {
            Assert.AreEqual("Keep old poem", request.Lyrics);
            if (fake.Calls == 2) Assert.IsTrue(request.Messages[^1].IsProgrammatic);
        };
        Field<TextBox>(w, "_input").Text = "Сделай текст логически цельным";
        ((Task)Call(w, "SendAsync")!).GetAwaiter().GetResult();
        Assert.AreEqual(2, fake.Calls); Assert.AreEqual("Really revised lyrics", s.Lyrics);
        Assert.AreEqual("New parameters", s.Parameters.Text);
        Assert.AreEqual(2, s.ReplyAttempts.Count); Assert.AreEqual("applied", s.ReplyAttempts[^1].Outcome);
        Assert.IsFalse(s.Messages.Any(m => m.Text == "Переработал текст"));
        Assert.IsTrue(s.Messages[^1].IsProgrammatic); StringAssert.Contains(s.Messages[^1].Text, "Текст песни обновлён");
        Assert.AreEqual(1, s.IncompleteReplies.Count);
        var restored = Store.Load(s.Id); Assert.AreEqual(2, restored.ReplyAttempts.Count); Assert.AreEqual(s.ReplyAttempts[0].Raw, restored.ReplyAttempts[0].Raw);
        w.Close();
    });

    [TestMethod]
    public Task TwoFailedActionsPreserveBothEditorsAndHistoriesAtomically() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new FakeRuntime();
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Original poem" }, new(), UserContext(), key => key, "en", Store, fake);
        Model(w); var s = Field<MusicPoetrySession>(w, "_session"); var before = s.Parameters;
        var lyricsCount = s.LyricsSteps.Count; var paramsCount = s.ParameterSteps.Count;
        fake.Reply = Routed("I fixed both", "New but not applied", before.Text, "song", "parameters");
        Field<TextBox>(w, "_input").Text = "Revise both";
        ((Task)Call(w, "SendAsync")!).GetAwaiter().GetResult();
        Assert.AreEqual(2, fake.Calls); Assert.AreEqual("Original poem", s.Lyrics); Assert.AreEqual(before, s.Parameters);
        Assert.AreEqual(lyricsCount, s.LyricsSteps.Count); Assert.AreEqual(paramsCount, s.ParameterSteps.Count);
        Assert.AreEqual(2, s.IncompleteReplies.Count); Assert.AreEqual(2, s.ReplyAttempts.Count);
        Assert.IsFalse(s.Messages.Any(m => m.Text == "I fixed both"));
        StringAssert.Contains(s.Messages[^1].Text, "Music.Poetry.NotApplied");
        Assert.IsFalse(w.IsWorking); w.Close();
    });

    [TestMethod]
    public Task ConversationGetsTruthfulProgramReceiptWithoutForcedEdits() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new FakeRuntime { Reply = Routed("What about an adventurous mood?", null, null) };
        var w = new MusicPoetryWindow(() => new() { Lyrics = "Keep poem" }, new(), UserContext(), key => key, "en", Store, fake);
        Model(w); Field<TextBox>(w, "_input").Text = "Let's discuss mood";
        ((Task)Call(w, "SendAsync")!).GetAwaiter().GetResult();
        var s = Field<MusicPoetrySession>(w, "_session"); Assert.AreEqual(1, fake.Calls);
        Assert.AreEqual("Keep poem", s.Lyrics); Assert.AreEqual(1, s.LyricsSteps.Count);
        Assert.AreEqual("What about an adventurous mood?", s.Messages[^2].Text);
        Assert.IsTrue(s.Messages[^1].IsProgrammatic); Assert.AreEqual("Music.Poetry.DiscussionOnly", s.Messages[^1].Text); w.Close();
    });

    [TestMethod]
    public Task ClosingDuringRepairCancelsWithoutApplyingRejectedData() => ScenarioNavigationTests.Sta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "operation.json")));
        ApplicationBackgroundOperations.Current = controller;
        try
        {
            var runtime = new RepairHoldingRuntime();
            var w = new MusicPoetryWindow(() => new() { Lyrics = "Original poem" }, new(), UserContext(), key => key, "en", Store, runtime);
            Model(w); Field<TextBox>(w, "_input").Text = "Revise";
            var work = (Task)Call(w, "SendAsync")!; var s = Field<MusicPoetrySession>(w, "_session");
            Assert.AreEqual(2, runtime.Calls); Assert.IsTrue(controller.IsRunning); Assert.AreEqual(1, s.ReplyAttempts.Count);
            w.Close(); var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (work.IsCompleted) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); work.GetAwaiter().GetResult();
            Assert.IsFalse(controller.IsRunning); Assert.IsFalse(w.IsWorking); Assert.AreEqual("Original poem", s.Lyrics);
            Assert.AreEqual(1, s.LyricsSteps.Count); Assert.AreEqual(1, Store.Load(s.Id).ReplyAttempts.Count);
        }
        finally { ApplicationBackgroundOperations.Current = previous; SynchronizationContext.SetSynchronizationContext(null); }
    });

    private sealed class RepairHoldingRuntime : IMusicPoetryRuntime
    {
        public int Calls;
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false)
        {
            if (++Calls == 1) return Routed("I edited it", null, null, "song");
            await Task.Delay(Timeout.Infinite, token); return "";
        }
        public void Dispose() { }
    }

    [TestMethod]
    public async Task InstalledModelReturnsVerifiedRevisionOrIsBlockedWithoutApplyingIt()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_POETRY_NATIVE") != "1") Assert.Inconclusive("Opt-in action guard probe.");
        var fixture = Environment.GetEnvironmentVariable("LOPATA_POETRY_ACTION_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) Assert.Inconclusive("Read-only session fixture required.");
        var s = JsonSerializer.Deserialize<MusicPoetrySession>(await File.ReadAllTextAsync(fixture))!;
        var settings = JsonSerializer.Deserialize<StorageSettings>(await File.ReadAllTextAsync(AppDataPaths.StorageSettingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var available = MusicPoetryRuntime.Discover(settings);
        var requested = Environment.GetEnvironmentVariable("LOPATA_POETRY_ACTION_MODEL");
        var model = string.IsNullOrWhiteSpace(requested) ? available.FirstOrDefault(m => m.IsCoreModel)
            : available.FirstOrDefault(m => m.Name == requested); Assert.IsNotNull(model);
        var original = s.Lyrics; var originalParameters = s.Parameters;
        var originalSteps = s.LyricsSteps.Count; var attempts = 0; var l = new LocalizationService(); l.Load("ru");
        using var runtime = new MusicPoetryRuntime(UserContext());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var folder = Path.Combine(Path.GetTempPath(), "lopata-poetry-action-probe"); Directory.CreateDirectory(folder);
        MusicPoetryReplyGuard.Result? result = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            attempts = attempt;
            var raw = await runtime.GenerateAsync(model, s, MusicPoetryProtocol.Guidance(s.Parameters.Variation, l.T),
                _ => { }, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "reply-" + attempt + ".json"), raw);
            result = MusicPoetryReplyGuard.Check(s, MusicPoetryProtocol.Parse(raw));
            await File.WriteAllTextAsync(Path.Combine(folder, "check-" + attempt + ".json"), JsonSerializer.Serialize(result));
            if (result.Error is null) break;
            s.Messages.Add(new("user", string.Format(l.T("Music.Poetry.RepairRequest"), l.T(result.Error)), s.Messages[^1].TurnId) { IsProgrammatic = true });
        }
        if (result!.Error is null)
        {
            CollectionAssert.Contains(result.Changes, "song"); s.Apply(result.Reply); Assert.AreNotEqual(original, s.Lyrics);
        }
        else
        {
            Assert.AreEqual(2, attempts); Assert.AreEqual(original, s.Lyrics);
            Assert.AreEqual(originalParameters, s.Parameters); Assert.AreEqual(originalSteps, s.LyricsSteps.Count);
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "outcome.json"), JsonSerializer.Serialize(new {
            model = model.Name, attempts, outcome = result.Error is null ? "applied" : "blocked", result.Error, result.Changes,
            lyricsChanged = s.Lyrics != original, parametersChanged = s.Parameters != originalParameters }));
    }
    private sealed class HoldingRuntime : IMusicPoetryRuntime
    {
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance, Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false)
        { await Task.Delay(Timeout.Infinite, token); return ""; }
        public void Dispose() { }
    }
}
