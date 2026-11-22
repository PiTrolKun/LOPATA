using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Controls.CharacterKnowledge;
using AIHub.Services;
using AIHub.Services.CharacterKnowledge;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class CharacterKnowledgeUiTests
{
    [TestMethod]
    public Task CardOpensTheNextPageAndDraftSurvivesBackAndReopen() => ScenarioNavigationTests.Sta(() =>
    {
        using var root = new CharacterKnowledgeTests.TestDirectory();
        var store = new KnowledgeStore(root.Path);
        var project = store.Create("Персонаж");
        store.Remember(project.Base.BaseId);
        var navigation = ScenarioNavigationTests.Create("ru", 1, true);
        var owner = new Window { Content = navigation };
        navigation.ScenarioRequested += id =>
        {
            if (id == ScenarioNavigationCatalog.CharacterKnowledge) navigation.ShowCharacterKnowledge(owner, () => "ru", store);
        };

        navigation.SelectDirection(ScenarioNavigationCatalog.Utilities);
        var card = Find<Button>(navigation, "Navigation.character_knowledge");
        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var page = (CharacterKnowledgeControl)navigation.Content;
        Assert.AreEqual("utilities_knowledge", ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.CharacterKnowledge).ParentId);
        Assert.AreEqual(ScenarioNavigationCatalog.CharacterKnowledge, navigation.RecoveryScenarioId);
        Assert.IsTrue(navigation.IsCharacterKnowledge);

        Click(page, "Add");
        Find<ComboBox>(page, Id("Type")).SelectedValue = "chat_example";
        Find<TextBox>(page, Id("Input")).Text = "Ты вернулся?";
        Find<TextBox>(page, Id("Output")).Text = "Рад тебя видеть.";
        Click(page, "Save");
        Click(page, "Variant");
        Click(page, "Save");
        Assert.AreEqual("Такая запись уже есть в базе.", Find<TextBlock>(page, Id("Status")).Text);
        Assert.AreEqual("Ты вернулся?", Find<TextBox>(page, Id("Input")).Text);
        Find<TextBox>(page, Id("Output")).Text = "Рад тебя видеть!";
        Click(page, "SaveNext");
        Assert.AreEqual("", Find<TextBox>(page, Id("Input")).Text);
        Assert.HasCount(2, store.Load(project.Base.BaseId).Knowledge.Entries);

        Find<TextBox>(page, Id("Input")).Text = "Черновик\nв новой записи";
        Assert.IsTrue(navigation.GoBack());
        Assert.IsFalse(navigation.IsCharacterKnowledge);
        Assert.AreEqual(ScenarioNavigationCatalog.Utilities, navigation.SelectedDirectionId);
        var reloaded = new CharacterKnowledgeControl(owner, key => key, () => "ru", new KnowledgeStore(root.Path));
        reloaded.Enter();
        Assert.AreEqual("Черновик\nв новой записи", Find<TextBox>(reloaded, Id("Input")).Text);
        owner.Close();
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task AddingANestedRuleChangesTheScaleOnce(bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        using var root = new CharacterKnowledgeTests.TestDirectory();
        var store = new KnowledgeStore(root.Path);
        var original = store.Create("Character");
        var entries = Enumerable.Range(1, 999).Select(index =>
            new KnowledgeEntry(Guid.NewGuid(), "chat_example", $"Question {index}", "Response")).ToArray();
        store.Save(original with { Knowledge = new(entries, []) });
        store.Remember(original.Base.BaseId);

        var navigation = ScenarioNavigationTests.Create("en", 1, dark);
        navigation.Resources["PoetryThoughtActiveBrush"] = Brushes.SeaGreen;
        var owner = new Window { Content = navigation };
        navigation.ShowCharacterKnowledge(owner, () => "en", store);
        var page = (CharacterKnowledgeControl)navigation.Content;
        page.Measure(new Size(1110, 650));
        page.Arrange(new Rect(0, 0, 1110, 650));
        page.UpdateLayout();
        var progress = Find<ProgressBar>(page, Id("Progress"));
        Assert.AreEqual(0.999, progress.Value, 0.0001);
        Assert.AreEqual(Visibility.Visible, Find<TextBlock>(page, Id("Rag")).Visibility);
        Assert.AreSame(Brushes.RoyalBlue, progress.Foreground);

        Click(page, "Rules");
        Click(page, "Add");
        Find<TextBox>(page, Id("Condition")).Text = "The user comes back";
        Click(page, "AddReaction");
        Find<TextBox>(page, Id("StepText")).Text = "Smile";
        Click(page, "AddBranch");
        Find<TextBox>(page, Id("StepText")).Text = "The user needs quiet";
        var rootThen = (TreeViewItem)Find<TreeView>(page, Id("RuleTree")).Items[0];
        var nested = (TreeViewItem)rootThen.Items[1];
        ((TreeViewItem)nested.Items[0]).IsSelected = true;
        Click(page, "AddAction");
        Find<TextBox>(page, Id("StepText")).Text = "Postpone reminder";
        Click(page, "Save");

        var saved = store.Load(original.Base.BaseId);
        Assert.HasCount(999, saved.Knowledge.Entries);
        Assert.AreEqual("Postpone reminder", saved.Knowledge.Rules.Single().Then[1].Then![0].Text);
        Assert.AreEqual(0.0, progress.Value);
        Assert.AreEqual(Visibility.Collapsed, Find<TextBlock>(page, Id("Rag")).Visibility);
        Assert.AreSame(Brushes.SeaGreen, progress.Foreground);
        owner.Close();
    });

    [TestMethod]
    public Task SupportedTagsOpenTheUtilityAndBackReturnsToTheOrigin() => ScenarioNavigationTests.Sta(() =>
    {
        using var root = new CharacterKnowledgeTests.TestDirectory();
        var navigation = ScenarioNavigationTests.Create("en", 1, false);
        var owner = new Window { Content = navigation };
        navigation.ScenarioRequested += id =>
        {
            if (id == ScenarioNavigationCatalog.CharacterKnowledge)
                navigation.ShowCharacterKnowledge(owner, () => "en", new KnowledgeStore(root.Path));
        };
        foreach (var id in new[] { "character_knowledge", "character_behavior_rules" })
        {
            navigation.SelectDirection(ScenarioNavigationCatalog.Creation);
            navigation.NavigateFromTag(ScenarioNavigationCatalog.GetTag(id));
            Assert.IsTrue(navigation.IsCharacterKnowledge);
            Assert.IsTrue(navigation.GoBack());
            Assert.AreEqual(ScenarioNavigationCatalog.Creation, navigation.SelectedDirectionId);
        }
        owner.Close();
    });

    [TestMethod]
    [DataRow("ru", "ru-RU", "привте")]
    [DataRow("en", "en-US", "helo")]
    public Task EditableFieldsUseTheSharedSpellingChecker(string language, string expectedLanguage, string authoredText) =>
        ScenarioNavigationTests.Sta(() =>
        {
            using var root = new CharacterKnowledgeTests.TestDirectory();
            var store = new KnowledgeStore(root.Path);
            var project = store.Create("Character");
            store.Remember(project.Base.BaseId);
            var navigation = ScenarioNavigationTests.Create(language, 1, true);
            var owner = new Window
            {
                Content = navigation, Width = 1100, Height = 680, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            owner.Show();
            try
            {
                navigation.ShowCharacterKnowledge(owner, () => language, store);
                var page = (CharacterKnowledgeControl)navigation.Content;

                var inspectName = owner.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = owner.OwnedWindows.OfType<Window>().Single();
                    try { AssertSpelling(Find<TextBox>(dialog, Id("BaseName")), expectedLanguage); }
                    finally { dialog.Close(); }
                }, DispatcherPriority.ApplicationIdle);
                Click(page, "CreateBase");
                inspectName.Task.GetAwaiter().GetResult();

                Click(page, "Add");
                var input = Find<TextBox>(page, Id("Input"));
                var output = Find<TextBox>(page, Id("Output"));
                AssertSpelling(input, expectedLanguage);
                AssertSpelling(output, expectedLanguage);
                Find<ComboBox>(page, Id("Type")).SelectedValue = "chat_example";
                input.Text = output.Text = authoredText;
                Click(page, "Save");
                Assert.IsFalse(SpellCheck.GetIsEnabled(input));
                Assert.IsFalse(SpellCheck.GetIsEnabled(output));
                var preview = Find<TextBox>(page, Id("Input"));
                Assert.IsTrue(preview.IsReadOnly);
                Assert.IsFalse(SpellCheck.GetIsEnabled(preview));
                Assert.AreEqual(authoredText, store.Load(project.Base.BaseId).Knowledge.Entries.Single().Output);

                Click(page, "Rules");
                Click(page, "Add");
                AssertSpelling(Find<TextBox>(page, Id("Condition")), expectedLanguage);
                Click(page, "AddAction");
                AssertSpelling(Find<TextBox>(page, Id("StepText")), expectedLanguage);
                var previous = Find<TextBox>(page, Id("StepText"));
                language = language == "ru" ? "en" : "ru";
                page.Configure(key => key);
                Assert.IsFalse(SpellCheck.GetIsEnabled(previous));
                AssertSpelling(Find<TextBox>(page, Id("StepText")), language == "ru" ? "ru-RU" : "en-US");
                Assert.IsTrue(navigation.GoBack());
            }
            finally { owner.Close(); }
        });

    private static void AssertSpelling(TextBox field, string language)
    {
        Assert.IsTrue(SpellCheck.GetIsEnabled(field));
        Assert.AreEqual(language, field.Language.IetfLanguageTag, StringComparer.OrdinalIgnoreCase);
        Assert.IsNull(field.ContextMenu);
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == id);

    private static string Id(string name) => "CharacterKnowledge." + name;

    private static void Click(DependencyObject root, string name)
    {
        var button = Find<Button>(root, Id(name));
        Assert.IsTrue(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
