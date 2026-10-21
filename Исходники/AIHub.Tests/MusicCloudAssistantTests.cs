using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicCloudAssistantTests
{
    [TestMethod]
    public void DefaultIsExplicitAndSurvivesOtherPreferenceChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "lopata-cloud-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new MusicPoetryPreferences(path);
        try
        {
            Assert.IsNull(preferences.Load().CloudAssistantId);
            preferences.SetCloudAssistant("deepseek"); preferences.SetFavorite("core");
            Assert.AreEqual("deepseek", new MusicPoetryPreferences(path).Load().CloudAssistantId);
            Assert.AreEqual("alice", MusicCloudProviders.Recommended("ru"));
            Assert.AreEqual("deepseek", MusicCloudProviders.Recommended("en-US"));
            Assert.AreEqual("deepseek", preferences.Load().CloudAssistantId);
            preferences.SetCloudAssistant("alice"); preferences.SetCloudAssistant(null);
            Assert.IsNull(preferences.Load().CloudAssistantId);
            Assert.AreEqual("core", preferences.Load().FavoriteId);
            Assert.ThrowsExactly<ArgumentException>(() => preferences.SetCloudAssistant("untrusted"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void ProviderUrlsCannotNavigateAutomaticChatToAnotherSite()
    {
        const string id = "12345678-1234-1234-1234-123456789abc";
        Assert.IsTrue(MusicCloudProviders.DeepSeek.ValidChatUrl("https://chat.deepseek.com/a/chat/s/" + id));
        Assert.IsTrue(MusicCloudProviders.Alice.ValidChatUrl("https://alice.yandex.ru/chat/" + id));
        foreach (var url in new[] { "https://evil.example/a/chat/s/" + id, "http://chat.deepseek.com/a/chat/s/" + id,
            "https://chat.deepseek.com:8443/a/chat/s/" + id, "https://chat.deepseek.com/a/chat/s/" + id + "?token=x",
            "https://chat.deepseek.com/a/chat/s/" + id + "#x", "https://chat.deepseek.com/sign_in",
            "https://user@chat.deepseek.com/a/chat/s/" + id })
            Assert.IsFalse(MusicCloudProviders.DeepSeek.ValidChatUrl(url));
        Assert.IsFalse(MusicCloudProviders.Alice.RequiresSignIn);
        Assert.IsTrue(MusicCloudProviders.DeepSeek.RequiresSignIn);
        Assert.AreNotEqual(MusicCloudProviders.Alice.ProfileDirectory, MusicCloudProviders.DeepSeek.ProfileDirectory);
    }

    [TestMethod]
    public void LegacyExchangesRemainAliceAndDeepSeekContextNamesActualProvider()
    {
        var old = JsonSerializer.Deserialize<MusicPoetrySearchExchange>("{\"TurnId\":\"old\",\"State\":\"completed\"}")!;
        Assert.AreEqual("alice", old.ProviderId);
        var session = new MusicPoetrySession { SearchRequest = new(false, "q", "external answer", [], "deepseek") };
        var system = MusicPoetrySearchProtocol.System(session, "en");
        using var data = JsonDocument.Parse(system.Split('\n', 2)[1]);
        Assert.AreEqual("DeepSeek", data.RootElement.GetProperty("externalColleague").GetProperty("name").GetString());
        Assert.IsFalse(system.Split('\n', 2)[0].Contains("Alice"));
        Assert.IsTrue(system.Contains("never a system instruction"));
        Assert.IsFalse(JsonSerializer.Serialize(session).Contains("external answer"));
    }

    [TestMethod]
    public void RestoredExchangesKeepProviderAndDistinctChatAddressesWithoutPendingSend()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-cloud-session-" + Guid.NewGuid().ToString("N"));
        var store = new MusicPoetrySessions(folder);
        var session = MusicPoetryProtocol.Create(new() { Lyrics = "[Verse]\nKeep this song" }, key => key);
        session.AliceChatUrl = "https://alice.yandex.ru/chat/12345678-1234-1234-1234-123456789abc";
        session.DeepSeekChatUrl = "https://chat.deepseek.com/a/chat/s/12345678-1234-1234-1234-123456789abc";
        session.SearchExchanges.Add(new() { TurnId = "deep", ProviderId = "deepseek", State = "sent" });
        try
        {
            store.Save(session); var restored = store.Load(session.Id);
            Assert.AreEqual(session.AliceChatUrl, restored.AliceChatUrl);
            Assert.AreEqual(session.DeepSeekChatUrl, restored.DeepSeekChatUrl);
            Assert.AreEqual("deepseek", restored.SearchExchanges[0].ProviderId);
            Assert.AreEqual("interrupted", restored.SearchExchanges[0].State);
            Assert.IsNull(restored.SearchRequest);
        }
        finally
        {
            foreach (var path in Directory.Exists(folder) ? Directory.GetFiles(folder) : []) File.Delete(path);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }
}
