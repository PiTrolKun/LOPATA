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

    [TestMethod] public void ThoughtFontPersistsIndependentlyOfModeNotesAndFutureFields()
    {
        Assert.AreEqual(200, Preferences.Load().ThoughtFontIncreasePercent);
        File.WriteAllText(Path.Combine(_root, "preferences.json"), "{\"FutureField\":{\"keep\":true}}");
        Preferences.SetThoughtFontIncrease(0); Preferences.SetReasoning("core", "off");
        Preferences.SetComments(new Dictionary<string, string> { ["core"] = "note" }); Preferences.SetFavorite("core");
        var fresh = new MusicPoetryPreferences(Path.Combine(_root, "preferences.json"));
        Assert.AreEqual(0, fresh.Load().ThoughtFontIncreasePercent);
        fresh.SetThoughtFontIncrease(400); Preferences.SetReasoning("core", "on");
        var state = fresh.Load(); Assert.AreEqual(400, state.ThoughtFontIncreasePercent);
        Assert.AreEqual("note", state.Comments["core"]); Assert.AreEqual("core", state.FavoriteId);
        Assert.IsTrue(state.Additional!["FutureField"].GetProperty("keep").GetBoolean());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fresh.SetThoughtFontIncrease(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fresh.SetThoughtFontIncrease(401));
        Assert.AreEqual(400, fresh.Load().ThoughtFontIncreasePercent);
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
            Call(editor, "SetReasoning", "off");
            var work = (Task)editor.GetType().GetMethod("SendCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [true])!;
            PumpUntil(() => runtime.Calls > 0); PumpFor(1150);
            Assert.IsTrue(editor.IsWorking); Assert.IsNull(Get<Window?>(editor, "_modelSelector"));
            Assert.AreEqual("off", runtime.ReasoningMode);
            Assert.IsTrue(Get<Button>(editor, "_reasoning").IsEnabled);
            Click(Get<Button>(editor, "_reasoning"));
            Assert.IsFalse(Get<Button>(editor, "_reasoning").ContextMenu?.IsOpen == true);
            Call(editor, "SetReasoning", "on"); Assert.AreEqual("off", Preferences.Load().Reasoning["core"].ModeId);
            editor.Close(); PumpUntil(() => work.IsCompleted); Assert.IsFalse(editor.IsWorking);
            Assert.AreEqual("Keep poem", Get<MusicPoetrySession>(editor, "_session").Lyrics);
        }
        finally { editor.Close(); }
    });

    [TestMethod] public void SavedOffSurvivesNotesStarsAndFutureFieldsWithoutBeingReplacedByDefault()
    {
        var path = Path.Combine(_root, "preferences.json");
        File.WriteAllText(path, """
            {"FavoriteId":"core","Comments":{},"FutureRoot":{"keep":true},"Reasoning":{
                "core":{"ModeId":"off","FutureToggle":true},"future-model":{"ModeId":"sixth","OtherToggle":false}}}
            """);
        Preferences.SetFavorite("executor"); Preferences.SetComments(new Dictionary<string, string> { ["core"] = "note" });
        Assert.AreEqual("off", Preferences.Load().Reasoning["core"].ModeId);
        Preferences.SetReasoning("core", "on"); Preferences.SetReasoning("core", "off");
        var state = Preferences.Load(); Assert.AreEqual("off", state.Reasoning["core"].ModeId);
        Assert.IsTrue(state.Reasoning["core"].Additional!["FutureToggle"].GetBoolean());
        Assert.AreEqual("sixth", state.Reasoning["future-model"].ModeId);
        Assert.IsTrue(state.Additional!["FutureRoot"].GetProperty("keep").GetBoolean());
        Assert.ThrowsExactly<ArgumentException>(() => Preferences.SetReasoning("instruct", "on"));
    }

    [TestMethod] public Task ConnectedModesStayIndependentAcrossModelSwitchesAndFreshWindow() => Sta(() =>
    {
        var ids = new[] { "core", "profile-kaine", "profile-abliterated", "beta" };
        var choices = Choices(ids); Preferences.SetFavorite("core");
        var expected = new Dictionary<string, string> { ["core"] = "off", ["profile-kaine"] = "on",
            ["profile-abliterated"] = "off", ["beta"] = "on" };
        var editor = Editor(choices, new NoGeneration());
        try
        {
            editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            foreach (var choice in choices)
            {
                Call(editor, "SelectModel", choice.Model!);
                Assert.IsTrue(Get<Button>(editor, "_reasoning").IsEnabled);
                Assert.IsTrue(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled);
                Call(editor, "SetReasoning", expected[choice.Candidate.Id]);
            }
            foreach (var choice in choices.Reverse())
            {
                Call(editor, "SelectModel", choice.Model!);
                var selected = Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions");
                Assert.AreEqual(expected[choice.Candidate.Id] == "on", selected.Enabled);
                Assert.AreEqual(selected.Enabled ? 2048 : 0, selected.Budget);
            }
        }
        finally { editor.Close(); }
        var fresh = new MusicPoetryPreferences(Path.Combine(_root, "preferences.json"));
        foreach (var id in ids) Assert.AreEqual(expected[id], fresh.Load().Reasoning[id].ModeId);
        var reopened = Editor(choices, new NoGeneration());
        try
        {
            reopened.Show(); PumpUntil(() => Get<DebugModelInfo?>(reopened, "_model") is not null);
            foreach (var choice in choices)
            {
                Call(reopened, "SelectModel", choice.Model!);
                Assert.AreEqual(expected[choice.Candidate.Id] == "on", Get<MusicPoetryReasoningOptions>(reopened, "_reasoningOptions").Enabled);
            }
        }
        finally { reopened.Close(); }
    });

    [TestMethod] public Task CoreToggleRestoresAcrossModelsAndNewWindowAndFailedSaveKeepsLiveChoice() => Sta(() =>
    {
        var choices = Choices("core", "instruct"); Preferences.SetFavorite("core");
        var editor = Editor(choices, new NoGeneration());
        try
        {
            editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            Assert.IsTrue(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled);
            var button = Get<Button>(editor, "_reasoning"); Click(button);
            Assert.AreEqual(2, button.ContextMenu.Items.Count);
            Assert.IsTrue(((MenuItem)button.ContextMenu.Items[0]).IsChecked);
            button.ContextMenu.IsOpen = false;
            Call(editor, "SetReasoning", "off");
            Assert.IsFalse(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled);
            Call(editor, "SelectModel", choices[1].Model!); Assert.IsFalse(button.IsEnabled);
            Call(editor, "SelectModel", choices[0].Model!);
            Assert.IsTrue(button.IsEnabled); Assert.IsFalse(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled);
            var file = Path.Combine(_root, "preferences.json"); var held = file + ".held";
            File.Move(file, held); Directory.CreateDirectory(file);
            try { Call(editor, "SetReasoning", "on"); Assert.IsFalse(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled); }
            finally { Directory.Delete(file); File.Move(held, file); }
        }
        finally { editor.Close(); }
        var reopened = Editor(choices, new NoGeneration());
        try { reopened.Show(); PumpUntil(() => Get<DebugModelInfo?>(reopened, "_model") is not null);
            Assert.IsFalse(Get<MusicPoetryReasoningOptions>(reopened, "_reasoningOptions").Enabled); }
        finally { reopened.Close(); }
    });

    [TestMethod]
    [DataRow("core")]
    [DataRow("profile-kaine")]
    [DataRow("profile-abliterated")]
    [DataRow("beta")]
    public Task UnknownSavedChoiceBlocksSendingUntilUserSelectsSupportedMode(string modelId) => Sta(() =>
    {
        var file = Path.Combine(_root, "preferences.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { FavoriteId = modelId,
            Reasoning = new Dictionary<string, object> { [modelId] = new { ModeId = "future" } } }));
        var editor = Editor(Choices(modelId), new NoGeneration());
        try { editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            Get<TextBox>(editor, "_input").Text = "request";
            Assert.IsFalse(Get<Button>(editor, "_send").IsEnabled); Assert.IsTrue(Get<Button>(editor, "_reasoning").IsEnabled);
            Assert.AreEqual("future", Preferences.Load().Reasoning[modelId].ModeId);
            Call(editor, "SetReasoning", "off"); Assert.IsTrue(Get<Button>(editor, "_send").IsEnabled);
            Assert.AreEqual("off", Preferences.Load().Reasoning[modelId].ModeId); }
        finally { editor.Close(); }
    });

    [TestMethod]
    [DataRow("core")]
    [DataRow("profile-kaine")]
    [DataRow("profile-abliterated")]
    [DataRow("beta")]
    [DataRow("gamma")]
    [DataRow("executor")]
    public Task ConnectedMenuFitsBothThemesLanguagesAndNarrowComposer(string modelId) => Sta(() =>
    {
        Preferences.SetFavorite(modelId); var choices = Choices(modelId);
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { false, true })
        {
            var l = new LocalizationService(); l.Load(language);
            var editor = new MusicPoetryWindow(() => new(), new(), new(new UserProfileStore(), new IpLocationService()),
                l.T, language, new(Path.Combine(_root, "ui-sessions")), new NoGeneration(), Preferences, () => choices);
            try
            {
                foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "InputBrush" })
                    editor.Resources[key] = dark ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
                editor.Resources["TextPrimaryBrush"] = dark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
                editor.Resources["TextSecondaryBrush"] = editor.Resources["LineBrush"] = System.Windows.Media.Brushes.Gray;
                editor.Resources["AccentBrush"] = editor.Resources["StepBadgeBrush"] = System.Windows.Media.Brushes.RoyalBlue;
                editor.Resources["SecondaryButtonBackgroundBrush"] = dark ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.WhiteSmoke;
                editor.Resources["UiBodyFontSize"] = 16d;
                editor.Width = 900; editor.Height = 500; editor.Show();
                PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null); editor.UpdateLayout();
                var button = Get<Button>(editor, "_reasoning"); Click(button); PumpFor(50);
                Assert.IsTrue(button.ContextMenu.IsOpen);
                Assert.AreEqual(modelId == "gamma" ? 8 : modelId == "executor" ? 6 : 2, button.ContextMenu.Items.Count);
                Assert.IsTrue(((MenuItem)button.ContextMenu.Items[0]).IsChecked);
                Assert.IsFalse(button.Content.ToString()!.StartsWith("Music.Poetry."));
                var location = button.TranslatePoint(new(), editor);
                Assert.IsTrue(location.X >= 0 && location.X + button.ActualWidth <= editor.ActualWidth);
                Assert.IsTrue(location.Y + button.ActualHeight < editor.ActualHeight);
                var output = Path.Combine(Path.GetTempPath(), "lopata-poetry-reasoning-ui"); Directory.CreateDirectory(output);
                Render(editor, Path.Combine(output, modelId + "-" + language + "-" + dark + "-narrow.png"));
                Render(button.ContextMenu, Path.Combine(output, modelId + "-" + language + "-" + dark + "-menu.png"));
                editor.Hide(); Assert.IsFalse(button.ContextMenu.IsOpen);
                editor.Show(); PumpUntil(() => button.IsEnabled);
                Assert.IsTrue(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Enabled);
                Call(editor, "SetReasoning", "off");
                Assert.IsTrue(button.Content.ToString()!.Contains(l.T("Music.Poetry.ReasoningOff")));
                editor.Width = 1380; editor.Height = 870; editor.UpdateLayout();
                Render(editor, Path.Combine(output, modelId + "-" + language + "-" + dark + "-wide-off.png"));
                Call(editor, "SetReasoning", "on");
            }
            finally { editor.Close(); }
        }
        static void Render(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5),
                (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file);
        }
    });

    [TestMethod] public Task AdvancedStateSurvivesOffSwitchesAndRestartAndUnknownBudgetNeedsExplicitRecovery() => Sta(() =>
    {
        var choices = Choices("gamma", "executor"); Preferences.SetFavorite("gamma");
        Preferences.SetReasoningOptions("gamma", new() { ModeId = "on", Budget = 4096, Effort = "xhigh", PreserveThoughts = false });
        var editor = Editor(choices, new NoGeneration());
        try
        {
            editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            Assert.AreEqual(4096, Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Budget);
            Call(editor, "SetReasoning", "off"); Call(editor, "SetReasoning", "on");
            var restored = Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions");
            Assert.AreEqual("xhigh", restored.Effort); Assert.IsFalse(restored.PreserveThoughts);
            Call(editor, "SelectModel", choices[1].Model!);
            Assert.IsTrue(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").PreserveThoughts);
            Assert.AreEqual(2048, Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Budget);
            Call(editor, "SelectModel", choices[0].Model!);
            var file = Path.Combine(_root, "preferences.json"); var held = file + ".held";
            File.Move(file, held); Directory.CreateDirectory(file);
            try { Call(editor, "SetReasoningOptions", new MusicPoetryReasoningPreference { ModeId = "on", Budget = 2048 });
                Assert.AreEqual(4096, Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").Budget); }
            finally { Directory.Delete(file); File.Move(held, file); }
        }
        finally { editor.Close(); }
        var reopened = Editor(choices, new NoGeneration());
        try { reopened.Show(); PumpUntil(() => Get<DebugModelInfo?>(reopened, "_model") is not null);
            Assert.AreEqual(4096, Get<MusicPoetryReasoningOptions>(reopened, "_reasoningOptions").Budget);
            Assert.IsFalse(Get<MusicPoetryReasoningOptions>(reopened, "_reasoningOptions").PreserveThoughts); }
        finally { reopened.Close(); }
        File.WriteAllText(Path.Combine(_root, "preferences.json"), """{"FavoriteId":"gamma","Reasoning":{"gamma":{"ModeId":"on","Budget":8192}}}""");
        var blocked = Editor(choices, new NoGeneration());
        try { blocked.Show(); PumpUntil(() => Get<DebugModelInfo?>(blocked, "_model") is not null);
            Get<TextBox>(blocked, "_input").Text = "request"; Assert.IsFalse(Get<Button>(blocked, "_send").IsEnabled);
            Assert.AreEqual(8192, Preferences.Load().Reasoning["gamma"].Budget);
            Call(blocked, "SetReasoningOptions", new MusicPoetryReasoningPreference { ModeId = "on", Budget = 2048 });
            Assert.IsTrue(Get<Button>(blocked, "_send").IsEnabled); }
        finally { blocked.Close(); }
    });

    [TestMethod]
    [DataRow("gamma")]
    [DataRow("executor")]
    public Task AdvancedMenuSavesExplicitChoicesAndBusyRequestKeepsOneSnapshot(string modelId) => Sta(() =>
    {
        Preferences.SetFavorite(modelId); var runtime = new HoldingRuntime();
        var editor = Editor(Choices(modelId), runtime); Task? work = null;
        try
        {
            editor.Show(); PumpUntil(() => Get<DebugModelInfo?>(editor, "_model") is not null);
            Pick("Budget.4096");
            if (modelId == "gamma") Pick("Effort.xhigh");
            Pick("Preserve");
            var preference = Preferences.Load().Reasoning[modelId];
            Assert.AreEqual(4096, preference.Budget); Assert.IsFalse(preference.PreserveThoughts!.Value);
            if (modelId == "gamma") Assert.AreEqual("xhigh", preference.Effort);
            Call(editor, "SetReasoning", "off"); Click(Get<Button>(editor, "_reasoning"));
            Assert.IsFalse(Item("Budget.4096").IsEnabled); Assert.IsFalse(Item("Preserve").IsEnabled);
            Get<Button>(editor, "_reasoning").ContextMenu.IsOpen = false; Call(editor, "SetReasoning", "on");
            Assert.IsFalse(Get<MusicPoetryReasoningOptions>(editor, "_reasoningOptions").PreserveThoughts);
            work = (Task)editor.GetType().GetMethod("SendCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [true])!;
            PumpUntil(() => runtime.Calls > 0);
            Call(editor, "SetReasoningOptions", preference with { Budget = 2048, PreserveThoughts = true });
            Assert.AreEqual(4096, Preferences.Load().Reasoning[modelId].Budget);
            Assert.IsFalse(Preferences.Load().Reasoning[modelId].PreserveThoughts!.Value);
            Assert.AreEqual(4096, runtime.ReasoningPreference!.Budget);
            Assert.IsFalse(runtime.ReasoningPreference.PreserveThoughts!.Value);
        }
        finally { editor.Close(); if (work is not null) PumpUntil(() => work.IsCompleted); }
        MenuItem Item(string suffix) => Get<Button>(editor, "_reasoning").ContextMenu.Items.OfType<MenuItem>()
            .Single(i => AutomationProperties.GetAutomationId(i) == "Music.Poetry.Reasoning." + suffix);
        void Pick(string suffix)
        {
            Click(Get<Button>(editor, "_reasoning"));
            Item(suffix).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
    });

    [TestMethod] public void AdvancedPreferenceUpdatePreservesUnknownFieldsAndIndependentModelChoices()
    {
        File.WriteAllText(Path.Combine(_root, "preferences.json"), """
            {"FavoriteId":"gamma","Comments":{"gamma":"note"},"FutureRoot":true,"Reasoning":{
            "gamma":{"ModeId":"off","FutureToggle":true},"executor":{"ModeId":"off","Budget":2048}}}
            """);
        Preferences.SetReasoningOptions("gamma", new() { ModeId = "on", Budget = 4096, Effort = "xhigh", PreserveThoughts = false });
        Preferences.SetReasoning("gamma", "off"); Preferences.SetReasoning("gamma", "on");
        var fresh = new MusicPoetryPreferences(Path.Combine(_root, "preferences.json")).Load();
        Assert.IsTrue(fresh.Reasoning["gamma"].Additional!["FutureToggle"].GetBoolean());
        Assert.IsTrue(fresh.Additional!["FutureRoot"].GetBoolean()); Assert.AreEqual("note", fresh.Comments["gamma"]);
        Assert.AreEqual("off", fresh.Reasoning["executor"].ModeId); Assert.AreEqual(4096, fresh.Reasoning["gamma"].Budget);
        Assert.IsFalse(fresh.Reasoning["gamma"].PreserveThoughts!.Value);
    }

    private MusicPoetryWindow Editor(MusicPoetryModelChoice[] choices, IMusicPoetryRuntime runtime) => new(() => new() { Lyrics = "Keep poem" }, new(),
        new(new UserProfileStore(), new IpLocationService()), key => key, "en", new(Path.Combine(_root, "sessions")), runtime, Preferences, () => choices);
    private MusicPoetryModelChoice[] Choices(params string[] ids) => (ids.Length == 0 ? new[] { "core", "executor" } : ids).Select(id =>
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
            Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false, string? reasoningMode = null, Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null)
        { Calls++; throw new AssertFailedException("Choosing a model must not run generation."); }
        public void Dispose() { }
    }
    private sealed class HoldingRuntime : IMusicPoetryRuntime
    {
        public int Calls;
        public string? ReasoningMode;
        public MusicPoetryReasoningPreference? ReasoningPreference;
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false, string? reasoningMode = null, Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null)
        { Calls++; ReasoningMode = reasoningMode; ReasoningPreference = reasoningPreference; await Task.Delay(Timeout.Infinite, token); return ""; }
        public void Dispose() { }
    }
}
