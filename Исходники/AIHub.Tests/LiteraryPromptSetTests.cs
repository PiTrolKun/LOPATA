using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryPromptSetTests
{
    private string _root = "";
    [TestInitialize] public void Setup() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "lopata-prompts-" + Guid.NewGuid().ToString("N")));
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod] public void EditingACopyCannotChangeDefaultsOrOtherActions()
    {
        var defaults = LiteraryPromptSets.Defaults("default"); var copy = defaults.Copy();
        copy.Actions["Discuss"] = new("custom role", "custom action");
        Assert.AreEqual(12, defaults.Actions.Count);
        Assert.AreEqual(LiteraryParagraphPrompts.Discussion, defaults.Actions["Discuss"].Role);
        Assert.AreEqual(LiteraryStudioPrompts.Get("Discuss").Prompt, defaults.Actions["Discuss"].Action);
        Assert.AreEqual(defaults.Actions["Tone"], copy.Actions["Tone"]);
        Assert.AreEqual(defaults.Actions["Discuss"], LiteraryPromptSets.Defaults("another").Actions["Discuss"]);
    }

    [TestMethod] public void LegacySelectionsRemainEffectiveUntilExplicitSelectionAndStandardModeWins()
    {
        var state = new LiteraryStudioState { PromptVariants = new() { ["Discuss"] = "old action" }, RolePromptVariants = new() { ["Tone"] = "old role" } };
        var before = JsonSerializer.Serialize(state);
        var selected = LiteraryPromptSets.Read(state, "Current");
        Assert.AreEqual(before, JsonSerializer.Serialize(state));
        Assert.IsTrue(selected.Custom);
        Assert.AreEqual("old action", selected.Selected!.Actions["Discuss"].Action);
        Assert.AreEqual("old role", selected.Selected.Actions["Tone"].Role);
        Assert.AreEqual("old action", LiteraryPromptSets.Resolve(state, "Discuss").Action);
        LiteraryPromptSets.Apply(state, selected, () => true);
        LiteraryPromptSets.Apply(state, selected with { Custom = false }, () => true);
        Assert.AreEqual(new LiteraryActionPrompt("", ""), LiteraryPromptSets.Resolve(state, "Discuss"));
        Assert.AreEqual("old action", state.PromptVariants["Discuss"]);
        Assert.AreEqual("old action", state.PromptSettings!.Selected!.Actions["Discuss"].Action);
    }

    [TestMethod] public void FailedProjectSaveRestoresPreviousSelectionAndSuccessfulApplyCopies()
    {
        var state = new LiteraryStudioState(); var preset = LiteraryPromptSets.Defaults("Mine");
        var settings = new LiteraryPromptSettings { Custom = true, Selected = preset };
        Assert.IsFalse(LiteraryPromptSets.Apply(state, settings, () => false)); Assert.IsNull(state.PromptSettings);
        Assert.Throws<IOException>(() => LiteraryPromptSets.Apply(state, settings, () => throw new IOException("locked")));
        Assert.IsNull(state.PromptSettings);
        Assert.IsTrue(LiteraryPromptSets.Apply(state, settings, () => true));
        preset.Actions["Discuss"] = new("changed later", "changed later");
        Assert.AreEqual(LiteraryParagraphPrompts.Discussion, LiteraryPromptSets.Resolve(state, "Discuss").Role);
    }

    [TestMethod] public void EmptyCustomModeNeverSilentlyUsesDefaults()
    {
        var state = new LiteraryStudioState { PromptSettings = new() { Custom = true } };
        Assert.Throws<InvalidOperationException>(() => LiteraryPromptSets.Resolve(state, "Continue"));
    }

    [TestMethod] public void LibraryRoundtripOrderDeletionAndConcurrentWritesDoNotChangeProjectCopy()
    {
        var path = Path.Combine(_root, "sets.json"); var store = new LiteraryPromptSetStore(path);
        var first = LiteraryPromptSets.Defaults("One"); var second = LiteraryPromptSets.Defaults("Two");
        Assert.AreEqual(0, store.Load().Count); store.Save([first, second]);
        var rival = new LiteraryPromptSetStore(path); rival.Load();
        var state = new LiteraryStudioState(); LiteraryPromptSets.Apply(state, new() { Custom = true, Selected = first }, () => true);
        store.Save([second, first]);
        Assert.Throws<InvalidOperationException>(() => rival.Save([]));
        CollectionAssert.AreEqual(new[] { "Two", "One" }, new LiteraryPromptSetStore(path).Load().Select(p => p.Name).ToArray());
        store.Save([second]);
        Assert.IsTrue(LiteraryPromptSets.Same(first, state.PromptSettings!.Selected!));
        Assert.AreEqual(first.Actions["Continue"], LiteraryPromptSets.Resolve(state, "Continue"));
    }

    [TestMethod] public void MalformedLibraryIsNotReplacedAndDuplicateNamesAreRejected()
    {
        var path = Path.Combine(_root, "sets.json"); File.WriteAllText(path, "broken");
        var store = new LiteraryPromptSetStore(path);
        Assert.Throws<JsonException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save([]));
        Assert.AreEqual("broken", File.ReadAllText(path));
        var first = LiteraryPromptSets.Defaults("One");
        Assert.Throws<InvalidDataException>(() => LiteraryPromptSets.Validate([first, LiteraryPromptSets.Defaults(" ONE ")]));
        var incomplete = first.Copy(); incomplete.Actions.Remove("Tone");
        Assert.Throws<InvalidDataException>(() => LiteraryPromptSets.Validate([incomplete]));
        var blank = first.Copy(); blank.Actions["Discuss"] = new(" ", "x");
        Assert.Throws<InvalidDataException>(() => LiteraryPromptSets.Validate([blank]));
    }

    [TestMethod] public void LegacyLibraryAndImagePresetsAreUntouchedByNewLibrary()
    {
        var path = Path.Combine(_root, "pairs.json"); var legacy = new PromptPairStore(path); legacy.Load();
        var old = new PromptPairPreset { Name = "Old", ContractId = LiteraryPromptSets.LegacyPrefix + "Discuss", AnalysisPrompt = "role", ComposePrompt = "action" };
        var image = old with { Id = Guid.NewGuid().ToString("N"), ContractId = "image", Name = "Image" };
        legacy.Save([old, image]); var bytes = File.ReadAllBytes(path);
        var store = new LiteraryPromptSetStore(Path.Combine(_root, "sets.json")); store.Load(); store.Save([LiteraryPromptSets.Defaults("new")]);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual("Discuss", LiteraryPromptSets.LegacyAction(old)); Assert.IsNull(LiteraryPromptSets.LegacyAction(image));
    }

    [TestMethod] public void ProjectReloadPreservesSelectedSetAndClearDoesNotResetPrompts()
    {
        var project = new LiteraryProject(); File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(project));
        var layout = new LiteraryProjectLayout(_root); layout.Initialize();
        var store = new LiteraryStudioStore(layout); var state = store.Load();
        var selected = LiteraryPromptSets.Defaults("Roundtrip"); selected.Actions["Find"] = new("role", "action");
        Assert.IsTrue(LiteraryPromptSets.Apply(state, new() { Custom = true, Selected = selected }, () => { store.Save(state); return true; }));
        var restored = new LiteraryStudioStore(layout).Load(); restored.Clear();
        Assert.IsTrue(restored.PromptSettings!.Custom);
        Assert.AreEqual(new LiteraryActionPrompt("role", "action"), LiteraryPromptSets.Resolve(restored, "Find"));
    }

    [TestMethod] public void EveryActionPassesItsOwnPairToPacketAndStandardRestoresBuiltInRole()
    {
        var project = new LiteraryProject { Genres = ["comedy"] }; File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(project));
        new LiteraryProjectLayout(_root).Initialize(); var chapters = new LiteraryChapterStore(_root); chapters.Open(); chapters.Save("draft");
        var editor = LiteraryEditorSnapshot.Capture(project.Id, _root, chapters.Index, chapters.Load(), false);
        var preset = LiteraryPromptSets.Defaults("all");
        foreach (var action in LiteraryPromptSets.Actions) preset.Actions[action.Id] = new("ROLE_" + action.Id, "ACTION_" + action.Id);
        var state = new LiteraryStudioState { PromptSettings = new() { Custom = true, Selected = preset } };
        foreach (var action in LiteraryPromptSets.Actions)
        {
            var role = LiteraryStudioPrompts.Writer.Contains(action) ? LiteraryChatProfile.Writer : LiteraryChatProfile.Advisor;
            var pair = LiteraryPromptSets.Resolve(state, action.Id);
            var request = new StudioRequest(new(role, "request", editor, [], new Dictionary<string,ParagraphSelection>(), "", "session", true),
                action.Id, [], [], "task", "result", [], false, pair.Action, pair.Role);
            var prompt = LiteraryStudioPrompts.Build(request, new([], []), new(project, editor, k => k), project, k => k)[0].Content;
            StringAssert.Contains(prompt, "ROLE_" + action.Id); StringAssert.Contains(prompt, "ACTION_" + action.Id);
            if (role == LiteraryChatProfile.Writer) StringAssert.Contains(prompt, "одного абзаца");
            state.PromptSettings = state.PromptSettings! with { Custom = false };
            pair = LiteraryPromptSets.Resolve(state, action.Id);
            var standard = LiteraryStudioPrompts.Build(request with { CustomPrompt = pair.Action, CustomRolePrompt = pair.Role }, new([], []), new(project, editor, k => k), project, k => k)[0].Content;
            Assert.IsFalse(standard.Contains("ROLE_" + action.Id)); StringAssert.Contains(standard, action.Prompt);
            state.PromptSettings = state.PromptSettings with { Custom = true };
        }
    }
}
