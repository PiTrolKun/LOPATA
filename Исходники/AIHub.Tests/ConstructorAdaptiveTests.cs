using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls.VisualConstructor;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass]
public sealed class ConstructorAdaptiveTests
{
    [TestMethod]
    public void TabsSupportNestedLayoutAndRoundtrip()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Tabs fixture");
        var source = project.ReadWindow();
        var layout = new ConstructorLayout(source);
        var tabs = layout.Add(ConstructorElementKind.Tabs, "Документы", null, "Первая");
        var page = layout.Add(ConstructorElementKind.TabPage, "Настройки", tabs);
        layout.SetArrangement("grid", page);
        var input = layout.Add(ConstructorElementKind.Input, "Сохраняемый ввод", page);
        project.SaveWindow(source, layout.Document);
        var reopened = new ConstructorLayout(ConstructorProject.Open(project.FilePath).ReadWindow());
        var saved = reopened.Elements.Single(node => ConstructorLayout.Name(node) == ConstructorLayout.Name(input));
        Assert.AreEqual(ConstructorElementKind.TabPage, ConstructorLayout.Kind(saved.Parent!.Parent!));
        Assert.AreEqual("grid", reopened.Arrangement(saved.Parent!.Parent));
        Assert.AreEqual("Сохраняемый ввод", ConstructorLayout.Caption(saved));
        Assert.HasCount(2, reopened.Elements.Where(node => ConstructorLayout.Kind(node) == ConstructorElementKind.TabPage).ToArray());
    }

    [TestMethod]
    public void RemovingLinkedFormMovesCanonicalActionsAndKeepsTheirIds()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Retarget fixture");
        var removed = project.AddForm("Старая");
        var replacement = project.AddForm("Новая");
        var source = project.ReadWindow();
        var layout = new ConstructorLayout(source);
        var site = new ConstructorEventSite(ConstructorProject.MainForm,
            ConstructorLayout.Name(layout.Add(ConstructorElementKind.Button, "Перейти", null)), "Click");
        project.SaveWindow(source, layout.Document);
        var step = project.CreateStep(site, new(ConstructorStepKind.Inside, [removed.Id]));
        project.CommitChain(project.ReadChain(site), [step]);
        Assert.IsTrue(project.CanRetargetForm(removed, project.ReferencingActions(removed)));
        project.ArchiveForm(removed, replacement);
        var changed = project.ReadChain(site).Steps.Single();
        Assert.AreEqual(step.Id, changed.Id);
        Assert.AreEqual(replacement.Id, changed.Spec!.Arguments.Single());
        Assert.IsFalse(project.ReadForms().Contains(removed));
    }

    [TestMethod]
    public Task ResearcherOpensWithoutAProjectWithIndependentOfflineArchive() => ScenarioNavigationTests.Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var archive = new ConstructorResearchArchive(Path.Combine(stand.FullPath, "archive"));
        archive.Save(new ResearchMaterial(Guid.NewGuid(), "Windows layout", "https://example.test/layout",
            DateTimeOffset.UtcNow, "10", "MIT", "Stored entry", [], []));
        var panel = new ConstructorResearcherPanel(key => key, stand.FullPath, null, null, archive);
        var window = new Window { Width = 1000, Height = 700, Content = panel };
        try
        {
            window.Show();
            var tabs = Visuals(panel).OfType<TabControl>().First();
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            var materials = Visuals(panel).OfType<ListBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "VisualConstructor.ResearchMaterials");
            Assert.AreEqual("Windows layout", ((ResearchMaterial)materials.SelectedItem).Title);
            Assert.IsNull(panel.Project);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task ResearcherHandsOffOnlyTheSelectedFile() => ScenarioNavigationTests.Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var archive = new ConstructorResearchArchive(Path.Combine(stand.FullPath, "archive"));
        archive.Save(new ResearchMaterial(Guid.NewGuid(), "Selected source", "https://example.test/repository",
            DateTimeOffset.UtcNow, "v2", "MIT", "Overview", [new("A.cs", "first"), new("B.cs", "chosen")], []));
        ConstructorReference? received = null;
        var panel = new ConstructorResearcherPanel(key => key, stand.FullPath, Path.Combine(stand.FullPath, "Example.csproj"),
            source => received = source, archive);
        var window = new Window { Width = 1000, Height = 680, Content = panel };
        try
        {
            window.Show();
            Visuals(panel).OfType<TabControl>().First().SelectedIndex = 1;
            window.UpdateLayout();
            Find<ComboBox>("ResearchFiles").SelectedItem = "B.cs";
            Find<Button>("ResearchPass").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(received);
            Assert.AreEqual("chosen", received.Text);
            Assert.AreEqual("v2", received.Version);
            Assert.AreEqual("MIT", received.License);
        }
        finally { window.Close(); }

        T Find<T>(string id) where T : DependencyObject => Visuals(panel).OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "VisualConstructor." + id);
    });

    [TestMethod]
    public Task PreviewProbeSizeStaysTemporaryUntilAppliedAndPanelsCanReopen() => ScenarioNavigationTests.Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Preview fixture");
        var source = project.ReadWindow();
        File.WriteAllText(Path.Combine(stand.FullPath, "visual-constructor-recent.json"), JsonSerializer.Serialize(new[] { project.FilePath }));
        var editor = new ConstructorEditor(key => key, stand.FullPath);
        var window = new Window { Width = 1060, Height = 690, Content = editor };
        try
        {
            window.Show();
            Find<ComboBox>("Recent").SelectedValue = project.FilePath;
            var preview = Find<Border>("FormPreview");
            var opening = preview.Width;
            Find<ComboBox>("PreviewSizeMode").SelectedValue = "minimum";
            Assert.IsTrue(preview.Width < opening);
            Assert.AreEqual(source, project.ReadWindow());
            Find<Button>("PreviewApplySize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Find<TextBox>("MinWidth").Text, Find<TextBox>("Width").Text);
            Find<Button>("Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreNotEqual(source, project.ReadWindow());
            window.Width = 720;
            window.UpdateLayout();
            var pane = Find<ScrollViewer>("WindowProperties");
            Assert.AreEqual(Visibility.Collapsed, pane.Visibility);
            Find<Button>("ToggleEditorProperties").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Visible, pane.Visibility);
        }
        finally { window.Close(); }

        T Find<T>(string id) where T : DependencyObject => Visuals(editor).OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "VisualConstructor." + id);
    });

    private static IEnumerable<DependencyObject> Visuals(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(node, i))) yield return item;
    }
}
