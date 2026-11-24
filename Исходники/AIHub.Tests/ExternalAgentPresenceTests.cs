using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.ExternalAgents;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentPresenceTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-agent-test-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void LooksForSpecificAppsIncludingBothOpenAiClients()
    {
        foreach (var name in new[] { "Codex", "ChatGPT", "Claude", "Google Antigravity", "Qwen Code",
                 "DeepSeek Harness", "GitHub Copilot Chat", "Cursor", "Windsurf", "Kiro", "CherryStudio" })
            Assert.IsNotNull(AgentClients.Installed(name, ""), name);
        Assert.AreEqual("codex", AgentClients.Installed("ChatGPT", "OpenAI.Codex_example!App")?.Id);
        Assert.AreEqual("chatgpt", AgentClients.Installed("ChatGPT Classic", "OpenAI.ChatGPT-Desktop_example!ChatGPT")?.Id);
        Assert.IsNull(AgentClients.Installed("Copilot", "Microsoft.MicrosoftOfficeHub_example!App"));
        Assert.IsNull(AgentClients.Installed("Visual Studio Code", "Microsoft.VisualStudioCode"));
    }

    [TestMethod]
    public void IdentityUsesProvidedNameAndOnlyEmbeddedSupportedImages()
    {
        var icons = new[] { new { src = "https://example.invalid/logo.png" }, new { src = "data:image/png;base64,AA==" } };
        var request = JsonSerializer.SerializeToElement(new { clientInfo = new { name = "sdk", title = "Windsurf", icons } });
        var client = AgentIdentity.FromHandshake(request, AgentIdentity.Anonymous);
        Assert.AreEqual("Devin Desktop", client.Name);
        Assert.AreEqual(icons[1].src, client.Icon);
        Assert.AreEqual("Codex", AgentIdentity.FromHandshake(request, AgentIdentity.FromName("codex-cli")).Name);
        Assert.AreEqual(AgentIdentity.Anonymous, AgentIdentity.FromHandshake(JsonSerializer.SerializeToElement(new { }), AgentIdentity.Anonymous));
    }

    [TestMethod]
    public void OnlyAnIdentifiedCodexToolCallSuppliesAMissingName()
    {
        Assert.AreEqual("Codex", AgentIdentity.ResolveCaller(AgentIdentity.Anonymous, "thread", "call").Name);
        Assert.AreEqual("", AgentIdentity.ResolveCaller(AgentIdentity.Anonymous, "thread", null).Name);
        Assert.AreEqual("", AgentIdentity.ResolveCaller(AgentIdentity.Anonymous, null, "call").Name);
        var named = new AgentIdentity("Other agent");
        Assert.AreEqual(named, AgentIdentity.ResolveCaller(named, "thread", "call"));
    }

    [TestMethod]
    public Task UnknownClientHasItsOwnMonitorPictogram() => ScenarioNavigationTests.Sta(() =>
    {
        var fallback = AgentAppIcon.Load(new AgentIdentity("Other agent", "data:image/png;base64,bad"), null);
        Assert.IsInstanceOfType<System.Windows.Media.DrawingImage>(fallback);
        Assert.IsTrue(fallback.IsFrozen);
        var emblem = new InstalledAgentApp("Test", null, Path.Combine(AppContext.BaseDirectory, "Assets", "ExternalAgents", "emblem.png"));
        var pixels = System.Windows.Media.Imaging.BitmapSource.Create(2, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[8], 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(pixels));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var client = new AgentIdentity("Test", "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()));
        var supplied = (System.Windows.Media.Imaging.BitmapSource)AgentAppIcon.Load(client, null);
        var installed = (System.Windows.Media.Imaging.BitmapSource)AgentAppIcon.Load(client, emblem);
        Assert.AreEqual(supplied.PixelWidth, 2 * supplied.PixelHeight);
        Assert.AreEqual(installed.PixelWidth, installed.PixelHeight);
    });

    [TestMethod]
    public void ConnectionAndOverlappingOperationsAreIndependentAndLocalToOneCopy()
    {
        var store = new AgentPresenceStore(_root);
        var client = AgentIdentity.FromName("Cursor");
        store.Remember(client);
        store.Remember(AgentIdentity.Anonymous);
        Assert.IsTrue(store.HasConnected("cursor"));
        Assert.IsFalse(new AgentPresenceStore(Path.Combine(_root, "other")).HasConnected("cursor"));
        Assert.IsEmpty(store.Read());
        using var first = store.Begin(client, "logs_search");
        using var second = store.Begin(AgentIdentity.Anonymous, "lopata_info");
        Assert.AreEqual(2, store.Read().Count(item => item.Running));
        first!.Dispose();
        var result = store.Read();
        Assert.AreEqual(1, result.Count(item => item.Running));
        Assert.IsFalse(result.Single(item => item.Work.Tool == "logs_search").Running);
        second!.Dispose();
        foreach (var file in Directory.GetFiles(_root, "*.activity")) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        Assert.IsEmpty(store.Read());
        Assert.IsTrue(store.HasConnected("cursor"));
    }

    [TestMethod]
    public async Task BackgroundPreparationKeepsIndicatorUntilCancellation()
    {
        var store = new AgentPresenceStore(_root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new AgentEnvironmentJobs(() => new(), () => 1, stop.Token,
            async (_, _, _, _, download, _, token) =>
            {
                if (!download) return false;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            }, store);
        var request = new AgentEnvironmentRequest("environment_prepare", "animated_assets", "artist", AgentIdentity.FromName("Kiro"));
        jobs.Handle(request);
        await started.Task.WaitAsync(stop.Token);
        Assert.AreEqual("Kiro", store.Read().Single(item => item.Running).Work.Client.Name);
        jobs.Handle(request with { Action = "environment_cancel" });
        Assert.IsTrue(SpinWait.SpinUntil(() => store.Read().TrueForAll(item => !item.Running), TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void CodexMarketplaceNeedsEnabledPluginPointingAtThisCopy()
    {
        var market = AgentConnectionSetup.MarketName;
        var plugin = AgentConnectionSetup.PluginName;
        var folder = Path.Combine(_root, "plugins", "cache", market, plugin, "1.0");
        Directory.CreateDirectory(folder);
        var agent = Path.Combine(_root, "Lopata.Agent.exe");
        var config = Path.Combine(_root, "config.toml");
        void Configure(bool enabled) => File.WriteAllText(config, $"[plugins.\"{plugin}@{market}\"]\nenabled  =  {enabled.ToString().ToLowerInvariant()}\n");
        Configure(true);
        Assert.AreEqual(false, CodexPluginConnection.Read(_root, agent));
        var servers = new Dictionary<string, object> { [plugin] = new { command = agent } };
        File.WriteAllText(Path.Combine(folder, ".mcp.json"), JsonSerializer.Serialize(new { mcpServers = servers }));
        Assert.AreEqual(true, CodexPluginConnection.Read(_root, agent));
        Assert.AreEqual(false, CodexPluginConnection.Read(_root, Path.Combine(_root, "other.exe")));
        Configure(false);
        Assert.AreEqual(false, CodexPluginConnection.Read(_root, agent));
    }

    [TestMethod]
    public Task ShortcutPulsesOnlyWhileAnUnconnectedAppIsAvailable() => ScenarioNavigationTests.Sta(() =>
    {
        var page = ScenarioNavigationTests.Create("ru", 1, true);
        var window = new Window { Content = page, Width = 800, Height = 650, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var entrance = ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>().Single(b =>
                AutomationProperties.GetAutomationId(b) == "ExternalAgents.Open");
            var glyph = (FrameworkElement)entrance.Content;
            Assert.IsFalse(glyph.HasAnimatedProperties);
            page.SetUnconnectedAgents("Kiro");
            StringAssert.Contains(AutomationProperties.GetHelpText(entrance), "Kiro");
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, glyph.HasAnimatedProperties);
            page.SetUnconnectedAgents("");
            Assert.IsFalse(glyph.HasAnimatedProperties);
            Assert.AreEqual("Подключить агента", entrance.ToolTip);
        }
        finally { window.Close(); }
    });
}
