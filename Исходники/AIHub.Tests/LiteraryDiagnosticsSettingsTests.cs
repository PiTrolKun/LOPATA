using System.Text.Json;
using AIHub.Models;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryDiagnosticsSettingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [TestMethod]
    public void NewSettings_EnableDetailedLiteraryDiagnostics()
    {
        Assert.IsTrue(new AppSettings().DetailedLiteraryDiagnostics);
    }

    [TestMethod]
    public void LegacySettingsWithoutDiagnostics_EnableItAndPreserveOtherSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"languageCode":"en","languageWasChosen":true}""", JsonOptions)!;

        Assert.IsTrue(settings.DetailedLiteraryDiagnostics);
        Assert.AreEqual("en", settings.LanguageCode);
        Assert.IsTrue(settings.LanguageWasChosen);

        var restored = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
        Assert.IsTrue(restored.DetailedLiteraryDiagnostics);
    }

    [TestMethod]
    public void ExplicitlyDisabledDiagnostics_RemainDisabledAfterRoundTrip()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"detailedLiteraryDiagnostics":false,"languageCode":"en"}""", JsonOptions)!;

        Assert.IsFalse(settings.DetailedLiteraryDiagnostics);

        var saved = JsonSerializer.Serialize(settings, JsonOptions);
        using var document = JsonDocument.Parse(saved);
        Assert.IsFalse(document.RootElement.GetProperty("detailedLiteraryDiagnostics").GetBoolean());

        var restored = JsonSerializer.Deserialize<AppSettings>(saved, JsonOptions)!;
        Assert.IsFalse(restored.DetailedLiteraryDiagnostics);
        Assert.AreEqual("en", restored.LanguageCode);
    }
}
