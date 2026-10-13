using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryThoughtUiTests
{
    [TestMethod] public Task ActiveTextStreamsAtThreeTimesThenCollapsesOnceAndManualExpansionSurvives() => Sta(() =>
    {
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { false, true })
        {
            using var fixture = new Fixture(language, dark); var runtime = fixture.Runtime; var editor = fixture.Editor;
            var work = Start(editor);
            Pump(() => fixture.Session.Thoughts.Count == 1 && fixture.View.Body.Text.Length > 0);
            var view = fixture.View;
            Assert.AreEqual(42d, view.Body.FontSize); Assert.IsTrue(view.IsExpanded);
            Assert.AreSame(editor.Resources["PoetryThoughtActiveBrush"], view.Body.Foreground);
            var activeColor = ((SolidColorBrush)view.Body.Foreground).Color;
            Assert.IsTrue(activeColor.G > activeColor.R && activeColor.G > activeColor.B);
            Assert.AreEqual("PRIVATE_THOUGHT first part. ", view.Body.Text);
            Assert.AreEqual(MusicPoetryThought.Active, fixture.Session.Thoughts[0].State);
            Assert.IsFalse(work.IsCompleted); Screenshot(editor, language + "-" + dark + "-active.png");
            editor.Width = 900; editor.Height = 500; editor.UpdateLayout(); PumpFor(50);
            Assert.IsTrue(view.ActualWidth <= Field<RichTextBox>(editor, "_chat").ActualWidth);
            Screenshot(editor, language + "-" + dark + "-active-narrow.png");
            editor.Width = 1380; editor.Height = 870; editor.UpdateLayout(); PumpFor(50);
            runtime.EndThought.TrySetResult();
            Pump(() => fixture.Session.Thoughts[0].State == MusicPoetryThought.Completed);
            Assert.AreEqual(14d, view.Body.FontSize); Assert.IsFalse(view.IsExpanded);
            Assert.AreSame(editor.Resources["TextPrimaryBrush"], view.Body.Foreground);
            Assert.IsFalse(work.IsCompleted); Screenshot(editor, language + "-" + dark + "-collapsed.png");
            view.IsExpanded = true; Call(editor, "RenderChat"); Call(editor, "RenderChat");
            Assert.AreSame(view, fixture.View); Assert.IsTrue(fixture.View.IsExpanded);
            Assert.AreEqual(14d, fixture.View.Body.FontSize);
            runtime.Reply.TrySetResult(); Pump(() => work.IsCompleted); work.GetAwaiter().GetResult();
            Assert.IsTrue(fixture.View.IsExpanded); Assert.AreEqual("old song", fixture.Session.Lyrics);
            Assert.AreEqual(1, fixture.Session.Thoughts.Count);
            var restored = fixture.Store.Load(fixture.Session.Id);
            Assert.IsTrue(restored.Thoughts[0].Expanded); Assert.AreEqual(MusicPoetryThought.Completed, restored.Thoughts[0].State);
            Assert.IsFalse(MusicPoetryProtocol.System(fixture.Session, "guide").Contains("PRIVATE_THOUGHT"));
            Assert.IsFalse(MusicPoetryModelMessages.Select(fixture.Session).Any(m => m.Text.Contains("PRIVATE_THOUGHT")));
            Assert.IsTrue(System.Windows.Markup.XamlWriter.Save(Field<RichTextBox>(editor, "_chat").Document).Contains("PRIVATE_THOUGHT"),
                "Rich chat copy/serialization must still support its embedded native controls.");
            editor.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, restored);
            Call(editor, "LoadSession"); fixture.View.IsExpanded = false;
            Assert.IsFalse(fixture.Session.Thoughts[0].Expanded); fixture.View.IsExpanded = true;
            Assert.IsTrue(fixture.Session.Thoughts[0].Expanded);
            Screenshot(editor, language + "-" + dark + "-expanded.png");
        }
    });

    [TestMethod] public Task CancellationShrinksAndLabelsPartialThoughtWithoutApplyingAnAnswer() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true); var work = Start(fixture.Editor);
        Pump(() => fixture.Session.Thoughts.Count == 1 && fixture.View.Body.Text.Length > 0);
        fixture.Editor.Close(); Pump(() => work.IsCompleted);
        Assert.IsFalse(fixture.Editor.IsWorking); Assert.AreEqual("old song", fixture.Session.Lyrics);
        Assert.AreEqual(MusicPoetryThought.Interrupted, fixture.Session.Thoughts[0].State);
        Assert.AreEqual(14d, fixture.View.Body.FontSize); Assert.IsFalse(fixture.View.IsExpanded);
        Assert.AreEqual(MusicPoetryThought.Interrupted, fixture.Store.Load(fixture.Session.Id).Thoughts[0].State);
    });

    [TestMethod] public Task NoThoughtChannelLeavesOrdinaryStatusAndCreatesNoEmptyBlock() => Sta(() =>
    {
        using var fixture = new Fixture("en", false, false); var work = Start(fixture.Editor);
        Pump(() => fixture.Editor.IsWorking); Assert.AreEqual(0, fixture.Session.Thoughts.Count);
        fixture.Runtime.EndThought.TrySetResult(); fixture.Runtime.Reply.TrySetResult();
        Pump(() => work.IsCompleted); Assert.AreEqual(0, fixture.Session.Thoughts.Count);
        Assert.AreEqual("old song", fixture.Session.Lyrics);
    });

    [TestMethod] public Task StreamErrorLeavesInterruptedThoughtAndRepairKeepsTwoSeparateRealAttempts() => Sta(() =>
    {
        using (var fixture = new Fixture("ru", true))
        {
            fixture.Runtime.Failure = new IOException("Music.Poetry.StreamFailed"); var work = Start(fixture.Editor);
            Pump(() => fixture.Session.Thoughts.Count == 1 && fixture.View.Body.Text.Length > 0);
            fixture.Runtime.EndThought.TrySetResult(); Pump(() => work.IsCompleted);
            Assert.AreEqual(MusicPoetryThought.Interrupted, fixture.Session.Thoughts[0].State);
            Assert.IsFalse(fixture.View.IsExpanded); Assert.AreEqual("old song", fixture.Session.Lyrics);
        }
        using (var fixture = new Fixture("en", false, runtimeOverride: new RepairRuntime()))
        {
            var work = Start(fixture.Editor); Pump(() => work.IsCompleted);
            Assert.AreEqual(2, fixture.Session.Thoughts.Count);
            Assert.AreEqual(1, fixture.Session.Thoughts[0].Attempt); Assert.AreEqual(2, fixture.Session.Thoughts[1].Attempt);
            Assert.IsTrue(fixture.Session.Thoughts[1].AnchorMessage > fixture.Session.Thoughts[0].AnchorMessage);
            Assert.AreEqual("attempt 1", fixture.Session.Thoughts[0].Text); Assert.AreEqual("attempt 2", fixture.Session.Thoughts[1].Text);
            Assert.IsTrue(fixture.Session.Thoughts.All(t => t.State == MusicPoetryThought.Completed && !t.Expanded));
            Assert.AreEqual(2, fixture.Session.ReplyAttempts.Count); Assert.AreEqual("old song", fixture.Session.Lyrics);
        }
    });

    [TestMethod] public Task ReadingAboveActiveBlockIsPreservedThroughUpdatesAndAutomaticCollapse() => Sta(() =>
    {
        using var fixture = new Fixture("en", false);
        for (var i = 0; i < 45; i++) fixture.Session.Messages.Add(new("user", "Earlier message " + i + "\nSecond line for scrolling.", "old-" + i));
        Call(fixture.Editor, "RenderChat"); PumpFor(100);
        var chat = Field<RichTextBox>(fixture.Editor, "_chat");
        Assert.IsTrue(chat.ExtentHeight > chat.ViewportHeight);
        var work = Start(fixture.Editor); Pump(() => fixture.Session.Thoughts.Count == 1 && fixture.View.Body.Text.Length > 0);
        PumpFor(100); Assert.IsTrue(chat.VerticalOffset > 0);
        chat.ScrollToVerticalOffset(100); PumpFor(100); var before = chat.VerticalOffset;
        fixture.Runtime.Progress!(new(string.Concat(Enumerable.Repeat("More real test thought. ", 80))));
        PumpFor(100); Assert.AreEqual(before, chat.VerticalOffset, 3d, "Growing thought pulled an earlier reader down.");
        fixture.Runtime.EndThought.TrySetResult(); Pump(() => fixture.Session.Thoughts[0].State == MusicPoetryThought.Completed);
        PumpFor(100); Assert.AreEqual(before, chat.VerticalOffset, 3d, "Automatic collapse changed the earlier reading position.");
        fixture.View.IsExpanded = true; PumpFor(100); Assert.AreEqual(before, chat.VerticalOffset, 3d);
        fixture.Runtime.Reply.TrySetResult(); Pump(() => work.IsCompleted); PumpFor(100);
        Assert.AreEqual(before, chat.VerticalOffset, 3d, "Final message rebuild pulled the reader down.");
    });

    [TestMethod] public Task InterfaceLanguageIsReadAgainForTheNextRequest() => Sta(() =>
    {
        var currentLanguage = "ru";
        using var fixture = new Fixture("ru", true, interfaceLanguage: () => currentLanguage);
        var first = Start(fixture.Editor); Pump(() => fixture.Runtime.Languages.Count == 1);
        currentLanguage = "en";
        fixture.Runtime.EndThought.TrySetResult(); fixture.Runtime.Reply.TrySetResult();
        Pump(() => first.IsCompleted); first.GetAwaiter().GetResult();
        var second = Start(fixture.Editor); Pump(() => second.IsCompleted); second.GetAwaiter().GetResult();
        CollectionAssert.AreEqual(new[] { "ru", "en" }, fixture.Runtime.Languages);
        Assert.AreEqual("old song", fixture.Session.Lyrics);
    });

    [TestMethod] public Task RightClickFontSliderChangesOnlyLiveThoughtsAndRemembersItsLastValue() => Sta(() =>
    {
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { false, true })
        {
            using var fixture = new Fixture(language, dark); var editor = fixture.Editor;
            var button = Field<System.Windows.Controls.Button>(editor, "_reasoning");
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) {
                RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = button });
            var popup = Field<System.Windows.Controls.Primitives.Popup>(editor, "_thoughtScalePopup");
            Assert.IsTrue(popup.IsOpen);
            var slider = Field<System.Windows.Controls.Slider>(editor, "_thoughtScaleSlider");
            Assert.AreEqual(200d, slider.Value); slider.Value = 100;
            Assert.AreEqual(100, Field<MusicPoetryPreferences>(editor, "_preferences").Load().ThoughtFontIncreasePercent);
            PumpFor(50); Screenshot((FrameworkElement)popup.Child, language + "-" + dark + "-font-slider.png");
            popup.IsOpen = false;
            var work = Start(editor); Pump(() => fixture.Session.Thoughts.Count == 1 && fixture.View.Body.Text.Length > 0);
            Assert.AreEqual(28d, fixture.View.Body.FontSize);
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) {
                RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = button });
            popup = Field<System.Windows.Controls.Primitives.Popup>(editor, "_thoughtScalePopup"); Assert.IsTrue(popup.IsOpen);
            slider = Field<System.Windows.Controls.Slider>(editor, "_thoughtScaleSlider");
            slider.Value = 0; Assert.AreEqual(14d, fixture.View.Body.FontSize);
            slider.Value = 400; Assert.AreEqual(70d, fixture.View.Body.FontSize);
            Assert.AreSame(editor.Resources["PoetryThoughtActiveBrush"], fixture.View.Body.Foreground);
            fixture.Runtime.EndThought.TrySetResult(); fixture.Runtime.Reply.TrySetResult();
            Pump(() => work.IsCompleted); work.GetAwaiter().GetResult();
            fixture.View.IsExpanded = true; Call(editor, "OpenThoughtScale");
            popup = Field<System.Windows.Controls.Primitives.Popup>(editor, "_thoughtScalePopup");
            slider = Field<System.Windows.Controls.Slider>(editor, "_thoughtScaleSlider"); slider.Value = 50;
            Assert.AreEqual(14d, fixture.View.Body.FontSize); Assert.IsTrue(fixture.View.IsExpanded);
            Assert.AreSame(editor.Resources["TextPrimaryBrush"], fixture.View.Body.Foreground);
            editor.Hide(); Assert.IsFalse(popup.IsOpen);
            var reopened = new MusicPoetryWindow(() => new(), new(), new(new UserProfileStore(), new IpLocationService()),
                key => key, language, fixture.Store, new GateRuntime(false), new MusicPoetryPreferences(fixture.PreferencesPath));
            try { Assert.AreEqual(50, Field<int>(reopened, "_thoughtFontIncrease")); }
            finally { reopened.Close(); }
        }
    });

    [TestMethod] public Task FontSaveFailureKeepsTheLiveAndDisplayedValue() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true); var editor = fixture.Editor;
        Call(editor, "OpenThoughtScale");
        var slider = Field<System.Windows.Controls.Slider>(editor, "_thoughtScaleSlider");
        File.Delete(fixture.PreferencesPath); Directory.CreateDirectory(fixture.PreferencesPath);
        slider.Value = 100;
        Assert.AreEqual(200d, slider.Value); Assert.AreEqual(200, Field<int>(editor, "_thoughtFontIncrease"));
        Assert.IsTrue(Field<System.Windows.Controls.TextBlock>(editor, "_status").Text.Length > 0);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-thought-ui-" + Guid.NewGuid().ToString("N"));
        internal MusicPoetrySessions Store { get; }
        internal string PreferencesPath => Path.Combine(_root, "preferences.json");
        internal GateRuntime Runtime { get; }
        internal MusicPoetryWindow Editor { get; }
        internal MusicPoetrySession Session => Field<MusicPoetrySession>(Editor, "_session");
        internal MusicPoetryThoughtBlock View => Field<Dictionary<string, MusicPoetryThoughtBlock>>(Editor, "_thoughtViews").Values.Single();
        internal Fixture(string language, bool dark, bool thoughts = true, IMusicPoetryRuntime? runtimeOverride = null,
            Func<string>? interfaceLanguage = null)
        {
            Directory.CreateDirectory(_root); Store = new(Path.Combine(_root, "sessions")); Runtime = new(thoughts);
            var l = new LocalizationService(); l.Load(language);
            var preferences = new MusicPoetryPreferences(Path.Combine(_root, "preferences.json")); preferences.SetFavorite("core");
            var candidate = MusicPoetryModelCatalog.All.Single(c => c.Id == "core");
            var model = new DebugModelInfo { Path = Path.Combine(_root, candidate.FileName), Name = candidate.FileName, Format = "gguf", IsRunnable = true, IsCoreModel = true };
            Editor = new(() => new() { Lyrics = "old song" }, new(), new(new UserProfileStore(), new IpLocationService()), l.T,
                language, Store, runtimeOverride ?? Runtime, preferences, () => [new(candidate, model)], interfaceLanguage);
            foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "InputBrush" }) Editor.Resources[key] = dark ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            Editor.Resources["TextPrimaryBrush"] = dark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
            Editor.Resources["TextSecondaryBrush"] = Editor.Resources["LineBrush"] = System.Windows.Media.Brushes.Gray;
            Editor.Resources["PoetryThoughtActiveBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#B6D8C3" : "#3F6851"));
            Editor.Resources["AccentBrush"] = Editor.Resources["StepBadgeBrush"] = System.Windows.Media.Brushes.RoyalBlue;
            Editor.Resources["SecondaryButtonBackgroundBrush"] = dark ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.WhiteSmoke;
            Editor.Resources["UiBodyFontSize"] = 16d;
            Editor.Show(); Pump(() => Field<DebugModelInfo?>(Editor, "_model") is not null);
        }
        public void Dispose()
        {
            Runtime.EndThought.TrySetResult(); Runtime.Reply.TrySetResult();
            Editor.Close(); Pump(() => !Editor.IsWorking); Editor.Close();
            var resolved = Path.GetFullPath(_root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lopata-thought-ui-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class GateRuntime(bool thoughts) : IMusicPoetryRuntime
    {
        internal TaskCompletionSource EndThought { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<MusicPoetryStreamUpdate>? Progress;
        internal Exception? Failure;
        internal List<string?> Languages { get; } = [];
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false, string? reasoningMode = null,
            Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null, double? temperature = null)
        {
            Languages.Add(interfaceLanguage);
            ready(new("system", request.Messages.ToArray(), 100, 32768, false)); Progress = reasoningProgress;
            if (thoughts) reasoningProgress?.Invoke(new("PRIVATE_THOUGHT first part. "));
            await EndThought.Task.WaitAsync(token);
            if (Failure is not null) throw Failure;
            if (thoughts) { reasoningProgress?.Invoke(new("second part.")); reasoningProgress?.Invoke(new("", true)); }
            await Reply.Task.WaitAsync(token);
            return "{\"chat\":\"Готово\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}";
        }
        public void Dispose() { }
    }
    private sealed class RepairRuntime : IMusicPoetryRuntime
    {
        private int _attempt;
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false, string? reasoningMode = null,
            Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null, double? temperature = null)
        {
            ++_attempt; ready(new("system", request.Messages.ToArray(), 100, 32768, false));
            reasoningProgress?.Invoke(new("attempt " + _attempt)); reasoningProgress?.Invoke(new("", true));
            return Task.FromResult(_attempt == 1
                ? "{\"chat\":\"Created song\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[\"song\"]}"
                : "{\"chat\":\"Hello\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}");
        }
        public void Dispose() { }
    }
    private static Task Start(MusicPoetryWindow editor)
    {
        Field<System.Windows.Controls.TextBox>(editor, "_input").Text = "request";
        return (Task)editor.GetType().GetMethod("SendCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false])!;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, []);
    private static Task Sta(Action action) => ScenarioNavigationTests.Sta(() => {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); try { action(); } finally { SynchronizationContext.SetSynchronizationContext(null); }
    });
    private static void PumpFor(int milliseconds) { var timer = Stopwatch.StartNew(); Pump(() => timer.ElapsedMilliseconds >= milliseconds); }
    private static void Pump(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || watch.ElapsedMilliseconds > 5000) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(condition(), "UI timed out.");
    }
    private static void Screenshot(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var folder = Path.Combine(Path.GetTempPath(), "lopata-thought-display-ui"); Directory.CreateDirectory(folder);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
}
