using System.Text.Json;
using AIHub.Models;
using AIHub.Services.Books;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class BookWindowsSettingsTests
{
    [TestMethod]
    public void MissingPreferenceMigratesRegistryButExplicitChoicesWin()
    {
        var old = JsonSerializer.Deserialize<ApplicationBehaviorSettings>("{}");
        Assert.IsNotNull(old); Assert.IsNull(old.BookShellIntegrationEnabled);
        Assert.IsFalse(BookShellIntegration.ResolveEnabled(old.BookShellIntegrationEnabled, false));
        Assert.IsTrue(BookShellIntegration.ResolveEnabled(old.BookShellIntegrationEnabled, true));
        Assert.IsFalse(BookShellIntegration.ResolveEnabled(false, true));
        Assert.IsTrue(BookShellIntegration.ResolveEnabled(true, false));
        old.BookShellIntegrationEnabled = false;
        var restored = JsonSerializer.Deserialize<ApplicationBehaviorSettings>(JsonSerializer.Serialize(old))!;
        Assert.AreEqual(false, restored.BookShellIntegrationEnabled);
        Assert.IsTrue(restored.ImageShellIntegrationEnabled, "The previous image/audio setting is independent.");
    }

    [TestMethod]
    public void OwnedOldMenuIsRenamedWithoutEnablingAnAbsentMenu()
    {
        var parent = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        var path = parent + @"\BookShellIntegration";
        var integration = new BookShellIntegration(typeof(BookWindowsSettingsTests).Assembly.Location, parent);
        try
        {
            Assert.IsFalse(BookShellIntegration.ResolveEnabled(null, integration.IsEnabled));
            integration.Apply(BookShellIntegration.ResolveEnabled(null, integration.IsEnabled), key => key);
            Assert.IsFalse(integration.IsEnabled);
            integration.Apply(true, key => key == "BookConverter.ToFormat" ? "To {0}" : "Old title");
            Assert.IsTrue(BookShellIntegration.ResolveEnabled(null, integration.IsEnabled));
            integration.Apply(BookShellIntegration.ResolveEnabled(null, integration.IsEnabled), key => key switch
            {
                "BookConverter.ShellMenuTitle" => "Перекодировать",
                "BookConverter.ToFormat" => "В {0}",
                _ => "Unexpected localization key: " + key
            });
            using (var root = Registry.CurrentUser.OpenSubKey(path)!)
                Assert.AreEqual("Перекодировать", root.GetValue("MUIVerb"));
            Assert.IsTrue(integration.IsEnabled);
            integration.Apply(BookShellIntegration.ResolveEnabled(false, integration.IsEnabled), _ => "");
            Assert.IsFalse(integration.IsEnabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(parent, false); }
    }
}
