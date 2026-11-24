using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
        Assert.AreEqual(smile.Width, connect.Width);
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
            StringAssert.Contains(File.ReadAllText(guide), executable);
            File.WriteAllText(guide, "User's own instructions");
            Assert.AreNotEqual(guide, setup.CreateInstructions());
            Assert.AreEqual("User's own instructions", File.ReadAllText(guide));

            setup.PrepareMarketplace("LOPATA");
            var plugin = Path.Combine(setup.MarketplaceRoot, "plugins", AgentConnectionSetup.PluginName);
            using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, ".mcp.json")));
            Assert.AreEqual(executable, mcp.RootElement.GetProperty("mcpServers")
                .GetProperty(AgentConnectionSetup.PluginName).GetProperty("command").GetString());
            Assert.IsTrue(File.Exists(Path.Combine(plugin, "skills", "lopata", "SKILL.md")));
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
