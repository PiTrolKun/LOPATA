using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSelectionActionsTests
{
    [TestMethod]
    public void RephrasePromptContainsReadableSourceAndRejectsChangedNumbers()
    {
        const string original = "Я отправлю документы завтра в 10 часов.";
        var input = OfficeRephraseText.Input(original, "Официальнее");
        StringAssert.Contains(input, original);
        StringAssert.Contains(input, "Официальнее");
        OfficeRephraseText.Validate(original, "Документы будут направлены завтра в 10 часов.");
        OfficeRephraseText.Validate(original, "Документы будут направлены завтра в 10:00.");
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => OfficeRephraseText.Validate(original, "Документы будут направлены в 11 часов."));
    }

    [TestMethod, DataRow("Привет мир & знак=плюс+"), DataRow("https://example.com/?x=1&y=2"), DataRow("cmd /c echo test\n& powershell")]
    public void SearchKeepsSelectedTextInsideAnEncodedHttpsQuery(string text)
    {
        var uri = OfficeSelectionActions.SearchUri(text);
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("www.google.com", uri.Host);
        Assert.AreEqual(text.Trim(), Uri.UnescapeDataString(uri.Query[3..]));
        Assert.AreEqual(1, uri.Query.Count(c => c == '='));
        Assert.IsFalse(uri.Query.Contains('&'));
    }
}
