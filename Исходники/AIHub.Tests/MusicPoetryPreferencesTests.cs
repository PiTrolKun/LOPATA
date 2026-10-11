using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryPreferencesTests
{
    private string _root = null!;
    private MusicPoetryPreferences Preferences => new(Path.Combine(_root, "preferences.json"));
    [TestInitialize] public void Initialize()
    { _root = Path.Combine(Path.GetTempPath(), "lopata-poetry-preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("lopata-poetry-preferences-", StringComparison.Ordinal)) throw new InvalidOperationException();
        Directory.Delete(resolved, true);
    }

    [TestMethod] public void OneFavoriteAndCommentsSurviveFreshStoreWithoutErasingEachOther()
    {
        Preferences.SetFavorite("core"); Preferences.SetComments(new Dictionary<string, string> { ["core"] = "Мой кот\nЁлка ★", ["executor"] = "Slow but better" });
        Preferences.SetFavorite("executor");
        var state = Preferences.Load(); Assert.AreEqual("executor", state.FavoriteId);
        Assert.AreEqual("Мой кот\nЁлка ★", state.Comments["core"]);
        Preferences.SetFavorite(null); Preferences.SetComments(new Dictionary<string, string> { ["executor"] = "" });
        Assert.IsNull(Preferences.Load().FavoriteId); Assert.IsFalse(Preferences.Load().Comments.ContainsKey("executor"));
        Assert.AreEqual("Мой кот\nЁлка ★", Preferences.Load().Comments["core"]);
        Assert.ThrowsExactly<ArgumentException>(() => Preferences.SetFavorite("alpha"));
    }

    [TestMethod] public void CorruptPreferencesAreNotOverwrittenByAStarOrComment()
    {
        var path = Path.Combine(_root, "preferences.json"); File.WriteAllText(path, "corrupt");
        Assert.ThrowsExactly<InvalidDataException>(() => Preferences.SetFavorite("core"));
        Assert.ThrowsExactly<InvalidDataException>(() => Preferences.SetComments(new Dictionary<string, string> { ["core"] = "note" }));
        Assert.AreEqual("corrupt", File.ReadAllText(path));
    }

    [TestMethod] public Task StarsMoveAndClearWithoutChangingCurrentModelAndNotesPersistOnClose() => ScenarioNavigationTests.Sta(() =>
    {
        var choices = Choices(); DebugModelInfo? selected = null;
        var picker = new MusicPoetryModelWindow(choices, choices[0].Model!.Path, key => key, model => selected = model, Preferences);
        try
        {
            var elements = ScenarioNavigationTests.LogicalDescendants(picker).OfType<FrameworkElement>().ToArray();
            var cards = elements.OfType<Expander>().ToArray(); Assert.IsTrue(cards.All(card => !card.IsExpanded));
            var stars = elements.OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).Contains(".Favorite."))
                .ToDictionary(button => AutomationProperties.GetAutomationId(button));
            Click(stars["Music.Poetry.SelectModel.Favorite.core"]); Click(stars["Music.Poetry.SelectModel.Favorite.executor"]);
            Assert.IsNull(selected); Assert.AreEqual("executor", Preferences.Load().FavoriteId);
            Assert.AreEqual(1, stars.Values.Count(star => Equals(star.Content, "★")));
            Click(stars["Music.Poetry.SelectModel.Favorite.executor"]); Assert.IsNull(Preferences.Load().FavoriteId);
            var comment = elements.OfType<TextBox>().Single(input => AutomationProperties.GetAutomationId(input) == "Music.Poetry.ModelComment.executor");
            comment.Text = "Медленная, но рифма лучше\nMy note"; picker.Close();
            Assert.AreEqual(comment.Text, Preferences.Load().Comments["executor"]);
            var reopened = new MusicPoetryModelWindow(choices, null, key => key, _ => { }, Preferences);
            try { Assert.AreEqual(comment.Text, ScenarioNavigationTests.LogicalDescendants(reopened).OfType<TextBox>()
                .Single(input => AutomationProperties.GetAutomationId(input) == "Music.Poetry.ModelComment.executor").Text); }
            finally { reopened.Close(); }
        }
        finally { picker.Close(); }
    });

    [TestMethod] public Task FailedNoteWriteKeepsPickerOpenUntilItCanSave() => ScenarioNavigationTests.Sta(() =>
    {
        var destination = Path.Combine(_root, "preferences.json"); Directory.CreateDirectory(destination);
        var picker = new MusicPoetryModelWindow(Choices(), null, key => key, _ => { }, Preferences);
        try
        {
            picker.Show();
            var comment = ScenarioNavigationTests.LogicalDescendants(picker).OfType<TextBox>().First(); comment.Text = "Keep this note";
            Assert.IsFalse(picker.SaveForExit()); picker.Close(); Assert.IsTrue(picker.IsVisible);
            Directory.Delete(destination); Assert.IsTrue(picker.SaveForExit()); picker.Close();
            Assert.AreEqual("Keep this note", Preferences.Load().Comments["core"]);
        }
        finally { if (Directory.Exists(destination)) Directory.Delete(destination); picker.Close(); }
    });

    [TestMethod] public Task FavoriteIsChosenOnBothEntryPointsWithoutPickerOrGeneration() => Sta(() =>
    {
        Preferences.SetFavorite("executor");
        foreach (var embedded in new[] { false, true })
        {
            var choices = Choices(); var runtime = new NoGeneration(); var editor = Editor(choices, runtime);
            var host = new ContentControl(); var shell = new Window { Content = host };
            try
            {
                if (embedded) { editor.PresentIn(host); shell.Show(); } else editor.PresentAsWindow();
                PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
                Assert.AreSame(choices[1].Model, Get<DebugModelInfo>(editor, "_model"));
                PumpFor(1100); Assert.IsNull(Get<Window?>(editor, "_modelSelector")); Assert.AreEqual(0, runtime.Calls);
                Assert.AreEqual("Keep poem", Get<MusicPoetrySession>(editor, "_session").Lyrics);
            }
            finally { editor.Close(); shell.Close(); }
        }
    });

    [TestMethod] public Task NoFavoriteOrMissingFavoriteOpensPickerAfterOneSecondInBothHosts() => Sta(() =>
    {
        foreach (var embedded in new[] { false, true }) foreach (var missing in new[] { false, true })
        {
            Preferences.SetFavorite(missing ? "executor" : null);
            var choices = Choices(); if (missing) choices[1] = choices[1] with { Model = null };
            var editor = Editor(choices, new NoGeneration()); var host = new ContentControl(); var shell = new Window { Content = host };
            try
            {
                var stopwatch = Stopwatch.StartNew();
                if (embedded) { editor.PresentIn(host); shell.Show(); } else editor.PresentAsWindow();
                PumpUntil(() => Get<Window?>(editor, "_modelSelector") is not null);
                Assert.IsTrue(stopwatch.ElapsedMilliseconds >= 900);
                Assert.AreSame(embedded ? shell : editor, Get<Window>(editor, "_modelSelector").Owner);
                Assert.AreEqual(missing ? "executor" : null, Preferences.Load().FavoriteId);
                Assert.AreEqual(0, Get<MusicPoetrySession>(editor, "_session").Messages.Count);
            }
            finally { editor.Close(); shell.Close(); }
        }
    });

    [TestMethod] public Task ManualSelectionAndLeavingBeforeTimerPreventLatePicker() => Sta(() =>
    {
        foreach (var close in new[] { false, true })
        {
            var choices = Choices(); var editor = Editor(choices, new NoGeneration());
            try
            {
                editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
                if (close) editor.Close(); else Call(editor, "SelectModel", choices[1].Model!);
                PumpFor(1150); Assert.IsNull(Get<Window?>(editor, "_modelSelector"));
                if (!close) Assert.AreSame(choices[1].Model, Get<DebugModelInfo>(editor, "_model"));
            }
            finally { editor.Close(); }
        }
    });

    [TestMethod] public Task HostTransferCancelsOldEntryAndPickerUsesNewOwner() => Sta(() =>
    {
        var editor = Editor(Choices(), new NoGeneration()); var host = new ContentControl(); var shell = new Window { Content = host };
        try
        {
            editor.PresentIn(host); shell.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            editor.PresentAsWindow(); PumpUntil(() => Get<Window?>(editor, "_modelSelector") is not null);
            Assert.AreSame(editor, Get<Window>(editor, "_modelSelector").Owner); Assert.IsNull(host.Content);
            Assert.AreEqual(1, editor.OwnedWindows.Count);
        }
        finally { editor.Close(); shell.Close(); }
    });

    [TestMethod] public Task ParentCannotExitWithUnsavedPersonalNote() => Sta(() =>
    {
        var destination = Path.Combine(_root, "preferences.json"); Directory.CreateDirectory(destination);
        var editor = Editor(Choices(), new NoGeneration());
        try
        {
            editor.Show(); PumpUntil(() => Get<Window?>(editor, "_modelSelector") is not null);
            var picker = Get<MusicPoetryModelWindow>(editor, "_modelSelector");
            ScenarioNavigationTests.LogicalDescendants(picker).OfType<TextBox>().First().Text = "Keep my comment before exit";
            Assert.IsFalse(editor.SaveForExit()); editor.Close(); Assert.IsTrue(editor.IsVisible); Assert.IsTrue(picker.IsVisible);
            Directory.Delete(destination); Assert.IsTrue(editor.SaveForExit()); editor.Close();
            Assert.AreEqual("Keep my comment before exit", Preferences.Load().Comments["core"]);
        }
        finally { if (Directory.Exists(destination)) Directory.Delete(destination); editor.Close(); }
    });

    [TestMethod] public Task GenerationStartedBeforeDelayDoesNotGetInterruptedByPicker() => Sta(() =>
    {
        var runtime = new HoldingRuntime(); var editor = Editor(Choices(), runtime);
        try
        {
            editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            var work = (Task)editor.GetType().GetMethod("SendCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [true])!;
            PumpUntil(() => runtime.Calls > 0); PumpFor(1150);
            Assert.IsTrue(editor.IsWorking); Assert.IsNull(Get<Window?>(editor, "_modelSelector"));
            editor.Close(); PumpUntil(() => work.IsCompleted); Assert.IsFalse(editor.IsWorking);
            Assert.AreEqual("Keep poem", Get<MusicPoetrySession>(editor, "_session").Lyrics);
        }
        finally { editor.Close(); }
    });

    private MusicPoetryWindow Editor(MusicPoetryModelChoice[] choices, IMusicPoetryRuntime runtime) => new(() => new() { Lyrics = "Keep poem" }, new(),
        new(new UserProfileStore(), new IpLocationService()), key => key, "en", new(Path.Combine(_root, "sessions")), runtime, Preferences, () => choices);
    private MusicPoetryModelChoice[] Choices() => new[] { "core", "executor" }.Select(id =>
    {
        var candidate = MusicPoetryModelCatalog.All.Single(model => model.Id == id); var path = Path.Combine(_root, candidate.FileName);
        using (var file = File.Create(path)) using (var writer = new BinaryWriter(file, Encoding.UTF8))
        {
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(3ul);
            String("general.architecture"); writer.Write(8u); String(candidate.Architecture);
            String(candidate.Architecture + ".block_count"); writer.Write(4u); writer.Write(32u);
            String(candidate.Architecture + ".context_length"); writer.Write(4u); writer.Write(32768u);
            void String(string text) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        }
        return new MusicPoetryModelChoice(candidate, new() { Name = candidate.FileName, Path = path, Format = "gguf", IsRunnable = true, IsCoreModel = id == "core" });
    }).ToArray();
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string method, params object[] arguments) => target.GetType().GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Sta(Action action) => ScenarioNavigationTests.Sta(() =>
    { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); try { action(); } finally { SynchronizationContext.SetSynchronizationContext(null); } });
    private static void PumpFor(int milliseconds) { var watch = Stopwatch.StartNew(); PumpUntil(() => watch.ElapsedMilliseconds >= milliseconds); }
    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var deadline = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || deadline.ElapsedMilliseconds > 4000) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(condition(), "UI operation timed out.");
    }
    private sealed class NoGeneration : IMusicPoetryRuntime
    {
        public int Calls;
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false)
        { Calls++; throw new AssertFailedException("Choosing a model must not run generation."); }
        public void Dispose() { }
    }
    private sealed class HoldingRuntime : IMusicPoetryRuntime
    {
        public int Calls;
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false)
        { Calls++; await Task.Delay(Timeout.Infinite, token); return ""; }
        public void Dispose() { }
    }
}
