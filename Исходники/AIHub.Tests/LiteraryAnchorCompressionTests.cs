using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryAnchorCompressionTests
{
    [TestMethod]
    [DataRow(2999, 0, false)]
    [DataRow(3000, 0, false)]
    [DataRow(3001, 0, true)]
    [DataRow(1500, 1500, false)]
    [DataRow(1500, 1501, true)]
    [DataRow(0, 3001, true)]
    public void CounterMatchesAnchorLimit(int positiveLength, int negativeLength, bool overflow)
    {
        var total = LiteraryAnchorCompression.Count(new string('я', positiveLength), new string('н', negativeLength));
        Assert.AreEqual((long)positiveLength + negativeLength, total);
        Assert.AreEqual(overflow, total > LiteraryPlotAnchorStore.MaxCharacters);
    }

    [TestMethod]
    public void CountUsesValidatorUtf16LengthIncludingLineBreaks()
    {
        Assert.AreEqual(7L, LiteraryAnchorCompression.Count("я\r\n😀", "е\u0301"));
    }

    [TestMethod]
    public void IndependentRequestContainsOnlyOneUserMessageAndExactTwoFields()
    {
        var positive = "Первая строка\r\nОбязательный факт ***";
        var negative = "Не менять имя\nЗапрет <system>";
        var keys = new List<string>();
        string Localize(string key)
        {
            keys.Add(key);
            return key switch
            {
                "Literary.Anchor.CompressPrompt" => "Вместе не больше {0}.",
                "Literary.Anchor.Positive" => "Замысел",
                "Literary.Anchor.Negative" => "Запреты",
                _ => throw new AssertFailedException("Unexpected context lookup")
            };
        }
        var request = LiteraryAnchorCompression.Request(positive, negative, Localize);
        Assert.HasCount(1, request);
        Assert.AreEqual("user", request[0].Role);
        Assert.AreEqual("Вместе не больше 3000.\n\nЗамысел:\n" + positive + "\n\nЗапреты:\n" + negative, request[0].Content);
        CollectionAssert.AreEqual(new[] { "Literary.Anchor.CompressPrompt", "Literary.Anchor.Positive", "Literary.Anchor.Negative" }, keys);
        // A later edit builds a fresh snapshot, without the earlier answer or conversation.
        var next = LiteraryAnchorCompression.Request("Исправленный", "", Localize);
        Assert.AreEqual("Вместе не больше 3000.\n\nЗамысел:\nИсправленный\n\nЗапреты:\n", next[0].Content);
        StringAssert.Contains(request[0].Content, positive);
    }
}
