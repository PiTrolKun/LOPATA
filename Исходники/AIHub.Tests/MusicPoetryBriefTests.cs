using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryBriefTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-poetry-brief-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-poetry-brief-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private const string Legacy = "Название: Кот\nКомментарий: Смешной рассказ\nЖелаемая длительность: Автоматически\nВарианты: 3\nФормат: Opus\nПожелания: blues, guitar\nБез хора: Нет\nТекущие настройки музыкального движка:\ncot = 0\nsteps = 32\nseed = -1\nabc_sampling.temperature = 0.7\nsemantic_sampling.max_tokens = 9000\nКомпозиция и исполнение:\n{\"Profile\":\"YuE\",\"Pins\":[],\"X\":0}\nМоя особая просьба: припев шёпотом";

    [TestMethod] public void InlineExplicitHeadingsRouteMusicWithoutLosingTheBrief()
    {
        var text = "[Idea]: Кот учится говорить\n[Structure]: Куплет и припев [Music]: Heavy Metal, guitar";
        var brief = MusicPoetryBrief.Parse(text);
        Assert.AreEqual("Кот учится говорить", brief.Idea);
        Assert.AreEqual("Куплет и припев", brief.Structure);
        Assert.AreEqual("Heavy Metal, guitar", brief.Music);
        Assert.AreEqual(brief, MusicPoetryBrief.Parse(brief.ToText()));
        var literal = MusicPoetryBrief.Parse("[Idea]: Обсудить тег [Music] и образ [Moon]: ночь");
        Assert.AreEqual("Обсудить тег [Music] и образ [Moon]: ночь", literal.Idea);
        Assert.AreEqual("", literal.Music);
    }

    [TestMethod] public void LegacyTechnicalNoiseIsExcludedButFreeWishesAndOriginalTextSurvive()
    {
        var brief = MusicPoetryBrief.Parse(Legacy);
        StringAssert.Contains(brief.Idea, "Название: Кот"); StringAssert.Contains(brief.Music, "blues, guitar");
        StringAssert.Contains(brief.Music, "Моя особая просьба: припев шёпотом");
        var session = MusicPoetryProtocol.Create(new() { Lyrics = "Keep song" }, key => key);
        session.Parameters = new(session.Parameters.Variation, Legacy);
        using var workspace = JsonDocument.Parse(MusicPoetryProtocol.System(session, "[Verse]").Split('\n', 2)[1]);
        var text = workspace.RootElement.GetProperty("parameters").GetString()!;
        foreach (var forbidden in new[] { "cot =", "seed =", "steps =", "abc_sampling", "semantic_sampling", "Profile", "Opus", "Автоматически", "Варианты", "Без хора: Нет" })
            Assert.IsFalse(text.Contains(forbidden), forbidden);
        Assert.AreEqual(Legacy, session.Parameters.Text); Assert.AreEqual("Keep song", session.Lyrics);
        var english = "Title: Moon\nDesired duration: 90\nVariants: 2\nFormat: MP3\nCurrent music engine settings:\ncfg_scale = 1.2\nWishes: rock\nNo choir: No";
        StringAssert.Contains(MusicPoetryBrief.Filter(english), "Wishes: rock");
        Assert.IsFalse(MusicPoetryBrief.Filter(english).Contains("cfg_scale"));
        StringAssert.Contains(MusicPoetryBrief.Filter("Моя формула: луна = солнце\ncustom_motif = hope"), "custom_motif = hope");
    }

    [TestMethod] public void SeedHasOnlyUsefulCreativeDataAcrossAllTargets()
    {
        var localize = new LocalizationService(); localize.Load("ru");
        foreach (var target in MusicPoetryProtocol.Variations)
        {
            var expert = MusicAceCatalog.Defaults(); expert.Values["bpm"] = 120;
            expert.TextValues["vocal_language"] = "ru"; expert.TextValues["timesignature"] = "4";
            expert.TextValues["keyscale"] = "D minor";
            var session = MusicPoetryProtocol.Create(new() { Variation = target, Title = "Ночь", Comment = "От лица Луны",
                Lyrics = "Saved verse", DurationSeconds = 180, Variants = 3, Expert = expert }, localize.T);
            var brief = MusicPoetryBrief.Parse(session.Parameters.Text);
            StringAssert.Contains(brief.Idea, "Ночь"); StringAssert.Contains(brief.Structure, "vocal_language = ru");
            StringAssert.Contains(brief.Music, "bpm = 120"); StringAssert.Contains(brief.Music, "D minor");
            Assert.AreEqual(target, session.Parameters.Variation); Assert.AreEqual("Saved verse", session.Lyrics);
            foreach (var key in new[] { "inference_steps", "lm_temperature", "seed", "Duration", "Формат", "Варианты" })
                Assert.IsFalse(session.Parameters.Text.Contains(key), key);
        }
        Assert.AreEqual("", MusicPoetryProtocol.Create(new(), localize.T).Parameters.Text);
    }

    [TestMethod] public void TopicsRoundTripAndTuningJsonCanSpanSeveralLines()
    {
        var original = new MusicPoetryBrief("Луна — рассказчик\nОставить образ", "Русский\nABAB, два куплета", "Rock\nДуэт, шёпот");
        Assert.AreEqual(original, MusicPoetryBrief.Parse(original.ToText()));
        var pretty = "Идея песни:\nСюжет\nМузыка и исполнение:\nГитара\nКомпозиция и исполнение:\n{\n  \"Profile\": \"YuE\",\n  \"Pins\": [],\n  \"X\": 0\n}\nНужен финальный вздох";
        var filtered = MusicPoetryBrief.Filter(pretty);
        Assert.IsFalse(filtered.Contains("Profile")); StringAssert.Contains(filtered, "Нужен финальный вздох");
    }

    [TestMethod] public void GuardDoesNotConfirmPureTechnicalOrHeadingChanges()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key); session.Parameters = new(session.Parameters.Variation, Legacy);
        var cleaned = MusicPoetryBrief.Parse(Legacy).ToText();
        Assert.AreEqual("Music.Poetry.Missing.parameters", MusicPoetryReplyGuard.Check(session,
            new("", null, cleaned, null, ["parameters"])).Error);
        Assert.AreEqual("Music.Poetry.Missing.parameters", MusicPoetryReplyGuard.Check(session,
            new("Done", null, "seed = 10\nsteps = 60", null, ["parameters"])).Error);
        var changed = new MusicPoetryBrief("Название: Новая Луна", "ABAB", "Rock, duet").ToText();
        var result = MusicPoetryReplyGuard.Check(session, new("", null, changed + "\nseed = 99", null, ["parameters"]));
        Assert.IsNull(result.Error); CollectionAssert.AreEqual(new[] { "parameters" }, result.Changes);
        Assert.IsFalse(result.Reply.Parameters!.Contains("seed"));
    }

    [TestMethod] public Task ViewingOldStepDoesNotRewriteItAndAllTopicsShareOneHistory() => ScenarioNavigationTests.Sta(() =>
    {
        var store = new MusicPoetrySessions(_root);
        var window = new MusicPoetryWindow(() => new() { Lyrics = "Old song" }, new(),
            new(new UserProfileStore(), new IpLocationService()), key => key, "ru", store, new NoGeneration(),
            new(Path.Combine(_root, "preferences.json")), () => []);
        try
        {
            var session = Get<MusicPoetrySession>(window, "_session"); session.Parameters = new(session.Parameters.Variation, Legacy);
            session.ParameterSteps.Clear(); session.ParameterSteps.Add(session.Parameters); session.ParameterCursor = 0;
            Call(window, "LoadSession"); Call(window, "Persist");
            Assert.AreEqual(Legacy, store.Load(session.Id).Parameters.Text);
            var control = Get<MusicPoetryBriefControl>(window, "_brief"); Assert.IsFalse(control.Music.Text.Contains("steps"));
            Call(window, "Activity", "parameters"); control.Idea.Text = "Новая идея"; control.Structure.Text = "ABAB";
            control.Music.Text = "Блюз"; Assert.AreEqual(1, session.ParameterSteps.Count);
            Call(window, "CommitZone"); Assert.AreEqual(2, session.ParameterSteps.Count);
            Call(window, "Navigate", true, -1); Assert.AreEqual(Legacy, session.Parameters.Text);
            Assert.AreEqual(Legacy, session.ParameterSteps[0].Text); Assert.AreEqual("Old song", session.Lyrics);
            Assert.IsFalse(control.Guidance.IsExpanded);
            var clouds = control.Children.OfType<Expander>().ToArray();
            Assert.AreEqual(4, clouds.Length);
            Assert.AreEqual(((SolidColorBrush)clouds[0].Tag).Color, ((SolidColorBrush)clouds[2].Tag).Color);
            Assert.AreNotEqual(((SolidColorBrush)clouds[0].Tag).Color, ((SolidColorBrush)clouds[1].Tag).Color);
            control.SetReadOnly(true); Assert.IsTrue(control.Idea.IsReadOnly && control.Structure.IsReadOnly && control.Music.IsReadOnly);
            var guidance = Get<System.Windows.Controls.TextBox>(window, "_guidance").Text;
            StringAssert.Contains(MusicPoetryProtocol.System(session, guidance), "Music.Poetry.Guide.Yue");
        }
        finally { window.Close(); }
    });
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private sealed class NoGeneration : IMusicPoetryRuntime
    {
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> ready, CancellationToken token, bool assemble = false, string? reasoningMode = null, Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null) => throw new AssertFailedException("Unexpected generation");
        public void Dispose() { }
    }
}
