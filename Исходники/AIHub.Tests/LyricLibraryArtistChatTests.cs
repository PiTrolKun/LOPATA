using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricLibraryArtistChatTests
{
    [TestMethod]
    public async Task NativeArtistNamesThroughActualMusicChatWindow()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_LYRICS_EVIDENCE");
        var corpus = Environment.GetEnvironmentVariable("LOPATA_LYRICS_NATIVE_CORPUS");
        if (Environment.GetEnvironmentVariable("LOPATA_LYRICS_ARTIST_CHAT") != "1"
            || string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(corpus))
            Assert.Inconclusive("Explicit opt-in: updates only synthetic fixture artists in the personal catalog and runs real chat windows.");
        Directory.CreateDirectory(evidence);
        var store = LyricLibraryStore.Default;
        LyricLibraryImport.Export(store, Path.Combine(evidence, "catalog-before.json"));
        var fixtures = LyricLibraryImport.Read(corpus, CancellationToken.None);
        var snapshot = store.Snapshot();
        var targets = new Dictionary<string, string> { ["Семь порций"] = "Линия 47",
            ["Повар ледокола"] = "Сектор 19", ["The Tailor of Empty Coats"] = "Unit 83" };
        var edits = new List<object>();
        for (var i = 0; i < fixtures.Length; i++)
        {
            var fixture = fixtures[i];
            var entry = snapshot.Entries.Single(e => e.TextHash == fixture.TextHash);
            Assert.AreEqual("new_synthetic_text", entry.Origin);
            var artist = targets.GetValueOrDefault(entry.Title) ?? $"Test Act {(i < 30 ? "RU" : "EN")}{i % 30 + 1:00}";
            if (entry.Artist == artist) continue;
            Assert.IsTrue(string.IsNullOrEmpty(entry.Artist), "Do not overwrite a user's artist edit.");
            var saved = store.Save(entry with { Artist = artist,
                Notes = entry.Notes + "\nFictional test artist; synthetic lyrics, no real performer attribution." });
            Assert.IsFalse(saved.Duplicate);
            Assert.AreEqual(entry.TextHash, saved.Entry.TextHash);
            edits.Add(new { entry.Id, entry.Title, before = entry.Artist, after = artist, saved.Entry.Revision });
        }
        LyricLibraryImport.Export(store, Path.Combine(evidence, "catalog-after.json"));
        await File.WriteAllTextAsync(Path.Combine(evidence, "artist-edits.json"), JsonSerializer.Serialize(edits));
        var old = LyricLibraryPreparation.RequestPreparation;
        LyricLibraryPreparation.RequestPreparation = (root, token) => LyricLibraryPreparation.PrepareAsync(root, false,
            new InlineProgress<string>(Console.WriteLine), token);
        try { await Sta(() => RunChat(evidence, targets)); }
        finally { LyricLibraryPreparation.RequestPreparation = old; }
    }

    private static async Task RunChat(string evidence, Dictionary<string, string> targets)
    {
        var settings = new StorageSettingsStore().LoadOrCreate();
        var model = MusicPoetryRuntime.Discover(settings).First(m => m.IsCoreModel);
        var choice = new MusicPoetryModelChoice(MusicPoetryModelCatalog.Find(model)!, model);
        var results = new List<object>();
        var tasks = new[] {
            (Artist: targets["Семь порций"], Language: "ru", Task: "Напиши восемь строк новой песни на русском, похожей по манере на исполнителя «Линия 47». Сюжет: велокурьер после смены помогает незнакомцу довезти сломанный велосипед. Если в личной базе есть тексты этого исполнителя, используй их только как образцы манеры; не копируй строки и сюжет. Не придумывай биографию исполнителя. Не задавай уточняющих вопросов, сразу предложи песенный текст."),
            (Artist: targets["Повар ледокола"], Language: "ru", Task: "Напиши восемь строк новой песни на русском, похожей по манере на исполнителя «Сектор 19». Сюжет: бригада на рассвете чинит упрямый трамвай. Если в личной базе есть тексты этого исполнителя, используй их только как образцы манеры; не копируй строки и сюжет. Не придумывай биографию исполнителя. Не задавай уточняющих вопросов, сразу предложи песенный текст."),
            (Artist: targets["The Tailor of Empty Coats"], Language: "en", Task: "Write eight lines of a new English song in a manner similar to the performer Unit 83. Story: the caretaker of a closed railway station prepares for one final passenger. If their lyrics are available in the personal library, use them only as references for writing manner, not as lines or plot to copy. Do not invent a biography. Do not ask clarification questions; propose lyrics now.") };
        var missingOnly = Environment.GetEnvironmentVariable("LOPATA_LYRICS_ARTIST_MISSING_ONLY") == "1";
        if (missingOnly) tasks = [("Absent Test Unit 999", "en", "Write four lines of a new English song like the performer «Absent Test Unit 999». If their lyrics are not in my personal library, say so and offer an original song about a night bus. Do not ask questions.")];
        foreach (var (artist, language, task) in tasks)
        foreach (var enabled in missingOnly ? new[] { true } : new[] { false, true })
        {
            var name = $"{results.Count + 1:00}-{language}-{enabled}";
            var preferences = new MusicPoetryPreferences(Path.Combine(evidence, name + "-preferences.json"));
            preferences.SetFavorite("core"); preferences.SetReasoning("core", "off"); preferences.SetCreativity("core", .1);
            var sessions = new MusicPoetrySessions(Path.Combine(evidence, "sessions"));
            var localization = new LocalizationService(); localization.Load(language);
            var runtime = new RecordingRuntime();
            var window = new MusicPoetryWindow(() => new(), settings, new(new UserProfileStore(), new IpLocationService()),
                localization.T, language, sessions, runtime, preferences, () => [choice]);
            window.Resources["TextPrimaryBrush"] = Brushes.White;
            window.Resources["TextSecondaryBrush"] = Brushes.LightGray;
            window.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(22,31,48));
            window.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(15,23,38));
            window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
            window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
            window.Resources["UiBodyFontSize"] = 14d;
            window.Width = 1380; window.Height = 870; window.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Get<Task>(window, "_modelEntryTask");
                var toggle = Get<System.Windows.Controls.Primitives.ToggleButton>(window, "_libraryToggle");
                toggle.IsChecked = enabled;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Get<System.Windows.Controls.TextBox>(window, "_input").Text = task;
                Assert.IsTrue(Get<System.Windows.Controls.Button>(window, "_send").IsEnabled);
                var started = DateTime.UtcNow;
                Console.WriteLine($"Chat {name}: {artist}");
                await ((Task)typeof(MusicPoetryWindow).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!)
                    .WaitAsync(TimeSpan.FromMinutes(5));
                var session = Get<MusicPoetrySession>(window, "_session");
                window.SaveForExit();
                var used = session.LibraryUses.SelectMany(u => u.Examples).ToArray();
                results.Add(new { name, artist, language, enabled, task, model = model.Name,
                    temperature = .1, seed = "not fixed: production chat sampler", seconds = (DateTime.UtcNow - started).TotalSeconds,
                    session.Id, song = session.Lyrics, session.Parameters, session.Messages, session.ReplyAttempts,
                    used = used.Select(e => new { e.Id, e.Revision, e.Title, e.Section,
                        artist = LyricLibraryStore.Default.Find(e.Id)?.Artist }), contexts = runtime.Contexts,
                    status = Get<TextBlock>(window, "_status").Text });
                await File.WriteAllTextAsync(Path.Combine(evidence, "artist-chat-results.json"), JsonSerializer.Serialize(results,
                    new JsonSerializerOptions { WriteIndented = true }));
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(evidence, name + ".png"))) png.Save(file);
                Assert.IsGreaterThan(0, runtime.Contexts.Count, "A real model context must have been sent.");
                if (!enabled || missingOnly) Assert.AreEqual(0, used.Length);
                else
                {
                    Assert.IsGreaterThan(0, used.Length);
                    Assert.IsTrue(used.All(e => e.Artist == artist && e.MatchKind == "artist"), "Only the requested artist may reach the chat.");
                    Assert.IsTrue(runtime.Contexts.All(c => c.System.Contains(artist, StringComparison.Ordinal)));
                    Assert.IsTrue(sessions.Load(session.Id).LibraryUses.SelectMany(u => u.Examples).All(e => e.Artist == artist));
                }
                if (missingOnly)
                {
                    Assert.IsTrue(session.Messages.Any(m => m.Text == string.Format(localization.T("Music.Library.ArtistMissing"), artist)));
                    Assert.IsTrue(runtime.Contexts.All(c => c.System.Contains(artist, StringComparison.Ordinal)
                        && c.System.Contains("no usable personal examples", StringComparison.Ordinal)));
                }
            }
            finally { window.Close(); }
        }
        Assert.AreEqual(missingOnly ? 1 : 6, results.Count);
    }

    private static T Get<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(18));
    }
    private sealed class RecordingRuntime : IMusicPoetryRuntime
    {
        private readonly MusicPoetryRuntime _runtime = new(new(new UserProfileStore(), new IpLocationService()));
        public List<MusicPoetryContext> Contexts { get; } = [];
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance, Action<MusicPoetryContext> contextReady,
            CancellationToken token, bool assemble = false, string? reasoningMode = null, Action<MusicPoetryStreamUpdate>? reasoningProgress = null,
            string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null, double? temperature = null) =>
            _runtime.GenerateAsync(model, request, guidance, context => { Contexts.Add(context); contextReady(context); }, token,
                assemble, reasoningMode, reasoningProgress, interfaceLanguage, reasoningPreference, temperature);
        public void Dispose() => _runtime.Dispose();
    }
}
