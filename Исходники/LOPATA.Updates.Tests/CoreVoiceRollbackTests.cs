using System.Text.Json.Nodes;
using Lopata.Updates;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class CoreVoiceRollbackTests
{
    [TestMethod]
    public void RestoreRequiresFreshConfirmationAndOnlyChangesVoiceSelection()
    {
        using var fixture = new UpdateFixture();
        var profile = new CoreVoiceProfileStore(fixture.Folder);
        var store = new CoreVoiceRollbackStore(fixture.Folder, profile);
        var id = Guid.NewGuid().ToString("N");
        var projectPath = Path.Combine(fixture.Folder, "project.txt");
        File.WriteAllText(projectPath, "user project");
        File.WriteAllText(profile.SettingsPath, """{"coreVoice":{"provider":"kitten2-mini","volume":82},"interface":{"zoom":125}}""");
        using (profile.Acquire())
        {
            store.Capture(id, profile.Read());
            profile.SavePreference(CoreVoiceProfileStore.Mini);
        }
        var interrupted = store.Preview(id)!;
        Assert.AreEqual(CoreVoiceProfileStore.Mini, interrupted.Current.LegacyProvider);
        Assert.IsTrue(store.TryRestore(id, interrupted, out _));

        Assert.IsNull(store.Preview(id)); // Moving the same preference does not offer a meaningless restore.
        ChangeTo(CoreVoiceProfileStore.RhVoice);
        var first = store.Preview(id)!;
        Assert.AreEqual(CoreVoiceProfileStore.RhVoice, first.Current.Preferred);
        Assert.AreEqual(CoreVoiceProfileStore.Mini, first.Saved.Preferred);
        ChangeTo(CoreVoiceProfileStore.Espeak);
        Assert.IsFalse(store.TryRestore(id, first, out var fresh));
        Assert.IsNotNull(fresh);
        Assert.AreEqual(CoreVoiceProfileStore.Espeak, profile.Read().Preferred);

        Assert.IsTrue(store.TryRestore(id, fresh, out _));
        Assert.AreEqual(new CoreVoiceSelection(CoreVoiceProfileStore.Espeak, CoreVoiceProfileStore.Mini), profile.Read());
        var json = JsonNode.Parse(File.ReadAllText(profile.SettingsPath))!;
        Assert.AreEqual(82, json["coreVoice"]!["volume"]!.GetValue<int>());
        Assert.AreEqual(125, json["interface"]!["zoom"]!.GetValue<int>());
        Assert.AreEqual("user project", File.ReadAllText(projectPath));

        void ChangeTo(string provider)
        {
            using var lease = profile.Acquire();
            profile.SavePreference(null);
            profile.SaveCompatibleProvider(provider);
        }
    }

    [TestMethod]
    public void MissingBackupAndDismissalDoNotWriteTheProfile()
    {
        using var fixture = new UpdateFixture();
        var profile = new CoreVoiceProfileStore(fixture.Folder);
        File.WriteAllText(profile.SettingsPath, """{"coreVoice":{"provider":"rhvoice"}}""");
        var original = File.ReadAllBytes(profile.SettingsPath);
        var store = new CoreVoiceRollbackStore(fixture.Folder, profile);
        var id = Guid.NewGuid().ToString("N");

        Assert.IsNull(store.Preview(id));
        Assert.IsFalse(store.WasReported(id));
        using var report = store.TryBeginReport(id);
        Assert.IsNotNull(report);
        Assert.IsNull(store.TryBeginReport(id));
        store.MarkReported(id);
        Assert.IsTrue(store.WasReported(id));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(profile.SettingsPath));
    }
}
