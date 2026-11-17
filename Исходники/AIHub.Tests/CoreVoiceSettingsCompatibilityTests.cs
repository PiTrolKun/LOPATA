using System.IO;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;
using Lopata.Updates;

namespace AIHub.Tests;

[TestClass]
public sealed class CoreVoiceSettingsCompatibilityTests
{
    [TestMethod]
    public void LegacyMiniSurvivesMigrationAndAnOldVersionSavingSettings()
    {
        using var profile = new TestProfile();
        profile.WriteLegacy(CoreVoiceSettings.KittenMiniProvider);
        CoreVoiceSelection? before = null;
        var store = new AppSettingsStore(profile.DirectoryPath, selection => before = selection);

        var settings = store.LoadOrCreate();

        Assert.AreEqual(new CoreVoiceSelection(CoreVoiceSettings.KittenMiniProvider, null), before);
        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, settings.CoreVoice.Provider);
        Assert.AreEqual(CoreVoiceSettings.EspeakProvider, profile.Voice.Read().LegacyProvider);
        Assert.IsFalse(settings.CoreVoice.Enabled);
        Assert.AreEqual(82, settings.CoreVoice.Volume);
        Assert.AreEqual(135, settings.CoreVoice.Rate);
        Assert.AreEqual("keep", profile.ReadShared()["unrelated"]!.GetValue<string>());

        profile.WriteLegacy(CoreVoiceSettings.RhVoiceProvider);
        settings = store.LoadOrCreate();
        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, settings.CoreVoice.Provider);
        Assert.AreEqual(CoreVoiceSettings.RhVoiceProvider, settings.CoreVoice.FallbackProvider);

        store.SelectCoreVoice(settings, CoreVoiceSettings.EspeakProvider);
        Assert.AreEqual(CoreVoiceSettings.EspeakProvider, store.LoadOrCreate().CoreVoice.Provider);
        store.SelectCoreVoice(settings, CoreVoiceSettings.RhVoiceProvider);
        Assert.AreEqual(CoreVoiceSettings.RhVoiceProvider, store.LoadOrCreate().CoreVoice.Provider);
        Assert.IsNull(profile.Voice.Read().PreferredProvider);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("future-dictor")]
    public void UnknownPreferenceSurvivesOpeningAndSaving(string provider)
    {
        using var profile = new TestProfile();
        profile.WriteLegacy(provider);
        var store = new AppSettingsStore(profile.DirectoryPath);

        var settings = store.LoadOrCreate();
        store.Save(settings);

        Assert.AreEqual(CoreVoiceSettings.EspeakProvider, settings.CoreVoice.SelectedProvider);
        Assert.AreEqual(provider, store.LoadOrCreate().CoreVoice.Provider);
        Assert.AreEqual(new CoreVoiceSelection(CoreVoiceSettings.EspeakProvider, provider), profile.Voice.Read());
    }

    [TestMethod]
    public void MigrationResumesAfterWritingOnlyTheSeparatePreference()
    {
        using var profile = new TestProfile();
        profile.WriteLegacy(CoreVoiceSettings.KittenMiniProvider);
        using (profile.Voice.Acquire()) profile.Voice.SavePreference(CoreVoiceSettings.KittenMiniProvider);

        var settings = new AppSettingsStore(profile.DirectoryPath).LoadOrCreate();

        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, settings.CoreVoice.Provider);
        Assert.AreEqual(CoreVoiceSettings.EspeakProvider, profile.Voice.Read().LegacyProvider);
    }

    [TestMethod]
    public void FreshProfileStartsWithMiniAndWritesACompatibleProvider()
    {
        using var profile = new TestProfile();

        var settings = new AppSettingsStore(profile.DirectoryPath).LoadOrCreate();

        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, settings.CoreVoice.Provider);
        Assert.AreEqual(new CoreVoiceSelection(CoreVoiceSettings.EspeakProvider, CoreVoiceSettings.KittenMiniProvider), profile.Voice.Read());
    }

    private sealed class TestProfile : IDisposable
    {
        public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("lopata-core-voice-").FullName;
        public CoreVoiceProfileStore Voice => new(DirectoryPath);

        public void WriteLegacy(string provider) => File.WriteAllText(Voice.SettingsPath, new JsonObject
        {
            ["coreVoice"] = new JsonObject { ["provider"] = provider, ["enabled"] = false, ["volume"] = 82, ["rate"] = 135 },
            ["unrelated"] = "keep"
        }.ToJsonString());

        public JsonObject ReadShared() => JsonNode.Parse(File.ReadAllText(Voice.SettingsPath))!.AsObject();

        public void Dispose() => System.IO.Directory.Delete(DirectoryPath, recursive: true);
    }
}
