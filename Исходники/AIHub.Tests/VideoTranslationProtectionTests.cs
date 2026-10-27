using System.IO;
using System.Text.RegularExpressions;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoTranslationProtectionTests
{
    [TestMethod]
    [DataRow("Лопата машет и говорит «Снято!». Лопату видно рядом с камерой.")]
    [DataRow("LOPATA says \"Привет!\" and ЛОПАТА waves.")]
    [DataRow("He doesn't know. He says 'Кто здесь?' (по-русски).")]
    [DataRow("Он говорит “Привет” или „Тихо“. Лопатой управляет камера.")]
    public void LiteralSpansRoundtripOutsideTheModel(string source)
    {
        var protection = new VideoTranslationProtection(source);
        Assert.AreNotEqual(source, protection.Text);
        var answer = protection.Restore(new(protection.Text, "", "", VideoConceptTests.Model));
        Assert.AreEqual(source, answer.Text);
    }

    [TestMethod]
    public void MissingDuplicatedOrInflectedMarkersRejectTheAnswer()
    {
        var protection = new VideoTranslationProtection("Лопата говорит «Снято!».");
        var token = Regex.Match(protection.Text, @"LOPATA_KEEP_[a-f0-9]+_0000").Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        foreach (var bad in new[] { protection.Text.Replace(token, "Lopata", StringComparison.Ordinal),
            protection.Text + " " + token, protection.Text.Replace(token, token + "а", StringComparison.Ordinal),
            protection.Text + " " + token.Replace("0000", "9999", StringComparison.Ordinal) })
            Assert.ThrowsExactly<InvalidDataException>(() => protection.Restore(new(bad, "", "", VideoConceptTests.Model)));
    }

    [TestMethod]
    public void LiteralRejectionDoesNotSuggestRepairingInstalledComponents()
    {
        Assert.IsFalse(AIHub.Services.ComponentAuditFailures.IsEligible(new InvalidDataException("VideoPrep.TranslationLiteralLost")));
        Assert.IsTrue(AIHub.Services.ComponentAuditFailures.IsEligible(new FileNotFoundException("worker.exe is missing")));
    }

    [TestMethod]
    public void OrdinaryNarrativeAndParentheticalExplanationsRemainTranslatable()
    {
        var source = "Кот берёт лопату (по-русски) и говорит «Привет»";
        var protection = new VideoTranslationProtection(source);
        StringAssert.Contains(protection.Text, "Кот берёт лопату (по-русски)");
        Assert.IsFalse(protection.Text.Contains("«Привет»", StringComparison.Ordinal));
        var translated = protection.Text.Replace("Кот берёт лопату (по-русски) и говорит", "The cat takes a shovel (in Russian) and says", StringComparison.Ordinal);
        StringAssert.Contains(protection.Restore(new(translated, "", "", VideoConceptTests.Model)).Text, "«Привет»");
    }
}
