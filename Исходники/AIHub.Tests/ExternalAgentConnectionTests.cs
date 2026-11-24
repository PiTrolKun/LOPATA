using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.ExternalAgents;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ExternalAgentConnectionTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task ConnectionShortcutFollowsSmileAndKeepsColor(bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var page = ScenarioNavigationTests.Create("ru", 1, dark);
        var buttons = ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>().ToArray();
        var smile = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Animation.Open");
        var connect = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "ExternalAgents.Open");
        var row = (StackPanel)smile.Parent;
        Assert.AreSame(row, connect.Parent);
        Assert.AreEqual(row.Children.IndexOf(smile) + 1, row.Children.IndexOf(connect));
        foreach (var shortcut in row.Children.OfType<Button>())
        {
            var icon = (FrameworkElement)shortcut.Content;
            Assert.AreEqual(56d, icon.Width);
            Assert.AreEqual(icon.Width, icon.Height);
            Assert.IsGreaterThan(icon.Width, shortcut.Width);
            Assert.IsGreaterThan(icon.Height, shortcut.Height);
        }
        Assert.AreEqual("Подключить агента", connect.ToolTip);
        var bitmap = (BitmapSource)((Image)connect.Content).Source;
        Assert.IsGreaterThan(0, bitmap.PixelWidth);
        var bgra = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        Assert.IsTrue(Enumerable.Range(0, pixels.Length / 4)
            .Any(i => pixels[4 * i + 3] > 200 && pixels[4 * i + 2] > pixels[4 * i] + 50));
    });

    [TestMethod]
    public Task ScenarioListStartsCollapsedOnEveryOpening() => ScenarioNavigationTests.Sta(() =>
    {
        var owner = new Window();
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var language = new LocalizationService();
        language.Load("ru");
        try
        {
            for (var opening = 0; opening < 2; opening++)
            {
                var window = new ExternalAgentConnectionWindow(owner, language.T);
                try
                {
                    var list = ScenarioNavigationTests.LogicalDescendants(window).OfType<Expander>().Single();
                    Assert.IsFalse(list.IsExpanded);
                    Assert.AreEqual(string.Format(language.T("ExternalAgents.Scenarios"), AgentProjects.NavigationEntries().Count()), list.Header);
                    list.IsExpanded = true;
                    Assert.IsTrue(((StackPanel)list.Content).Children.Count > 0);
                }
                finally { window.Close(); }
            }
        }
        finally { owner.Close(); }
    });

    [TestMethod]
    [DataRow("ru", "Создание › Изображения › Пошаговый визуальный сценарий › Проекты…")]
    [DataRow("en", "Creation › Images › Step-by-step visual workspace › Projects…")]
    public void ProjectMenuPathsUseTheInterfaceLanguage(string code, string visualPath)
    {
        var language = new LocalizationService();
        language.Load(code);
        using var dictionary = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", code + ".json")));
        var labels = dictionary.RootElement.EnumerateObject().Select(p => language.T(p.Name)).ToHashSet();
        var projects = JsonSerializer.SerializeToElement(AgentProjects.List(language)).EnumerateArray()
            .ToDictionary(p => p.GetProperty("scenario").GetString()!);
        foreach (var node in AgentProjects.NavigationEntries())
        {
            var project = projects[node.Id];
            Assert.AreEqual(language.T(node.TitleKey), project.GetProperty("title").GetString());
            var path = project.GetProperty("openIn").GetString()!;
            StringAssert.Contains(path, language.T(node.TitleKey) + " › ");
            foreach (var label in path.Split(" › "))
                Assert.IsTrue(labels.Contains(label), $"Missing interface label: {label}");
        }
        Assert.AreEqual(visualPath, projects[ScenarioNavigationCatalog.VisualScenario].GetProperty("openIn").GetString());
        Assert.AreEqual(".lopataAIVid", projects[ScenarioNavigationCatalog.VideoGeneration].GetProperty("fileExtension").GetString());
    }

    [TestMethod]
    public void FirstConnectionUsesThisCopyAndPreservesEditedGuide()
    {
        var root = Directory.CreateTempSubdirectory("lopata-connection-").FullName;
        try
        {
            var app = Path.Combine(root, "Копия приложения");
            var assets = Path.Combine(app, "Assets", "ExternalAgents");
            Directory.CreateDirectory(assets);
            foreach (var name in new[] { "SKILL.md", "emblem.png" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ExternalAgents", name), Path.Combine(assets, name));
            var executable = Path.Combine(app, "Lopata.Agent.exe");
            File.WriteAllText(executable, "");
            var setup = new AgentConnectionSetup(app, Path.Combine(root, "Profile"));
            var guide = setup.CreateInstructions();
            Assert.AreEqual(guide, setup.CreateInstructions());
            var instructions = File.ReadAllText(guide);
            StringAssert.Contains(instructions, executable);
            StringAssert.Contains(instructions, "project_scenarios");
            StringAssert.Contains(instructions, "project_instructions");
            File.WriteAllText(guide, "User's own instructions");
            Assert.AreNotEqual(guide, setup.CreateInstructions());
            Assert.AreEqual("User's own instructions", File.ReadAllText(guide));

            setup.PrepareMarketplace("LOPATA");
            var plugin = Path.Combine(setup.MarketplaceRoot, "plugins", AgentConnectionSetup.PluginName);
            using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, ".mcp.json")));
            Assert.AreEqual(executable, mcp.RootElement.GetProperty("mcpServers")
                .GetProperty(AgentConnectionSetup.PluginName).GetProperty("command").GetString());
            var skill = File.ReadAllText(Path.Combine(plugin, "skills", "lopata", "SKILL.md"));
            StringAssert.Contains(skill, "project_scenarios");
            StringAssert.Contains(skill, "project_instructions");
            Assert.IsFalse(setup.HasRegistration);
            setup.RecordRegistration();
            Assert.IsTrue(setup.HasRegistration);
            var prompt = Uri.UnescapeDataString(CodexPluginClient.NewChat("Тест & пробел").Query);
            StringAssert.Contains(prompt, $"plugin://{AgentConnectionSetup.PluginName}@{AgentConnectionSetup.MarketName}");
            StringAssert.Contains(prompt, "Тест & пробел");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
