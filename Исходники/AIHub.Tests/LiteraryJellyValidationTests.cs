using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryJellyValidationTests
{
    [TestMethod]
    public void SharedInaccurateQuoteReportsEvidenceForAllThreeFactsAndAcceptsExactSource()
    {
        const string source = "Мальчик не оправдывался, не читал лекций о физике и не жаловался на судьбу";
        foreach (var relation in new[] { "не оправдывался", "не читал лекции о физике", "не жаловался на судьбу" })
        {
            var fact = new LiteraryJellyFact { Subject = "Мальчик", Relation = relation,
                Evidence = source.Replace("лекций", "лекции") };
            var issues = LiteraryJellyValidation.Check(fact, source);
            Assert.HasCount(1, issues);
            Assert.AreEqual(LiteraryJellyField.Evidence, issues[0].Field);
            Assert.AreEqual("Literary.Jelly.Error.ExactQuote", issues[0].MessageKey);
            Assert.ThrowsExactly<System.IO.InvalidDataException>(() => LiteraryJellyContract.Validate(fact, source));
            // Editing an unrelated field must not mask the quote mismatch.
            fact.Subject = "ГГ";
            Assert.HasCount(1, LiteraryJellyValidation.Check(fact, source));
            fact.Evidence = source;
            LiteraryJellyContract.Validate(fact, source);
            Assert.HasCount(0, LiteraryJellyValidation.Check(fact, source));
        }
    }

    [TestMethod]
    public void ExcludedFactDoesNotBlockSaveButReinclusionRestoresItsErrors()
    {
        var fact = new LiteraryJellyFact { Accepted = false, Kind = "unknown" };
        LiteraryJellyContract.Validate(fact, "Source");
        Assert.HasCount(0, LiteraryJellyValidation.Check(fact, "Source"));
        fact.Accepted = true;
        CollectionAssert.AreEquivalent(new[] { LiteraryJellyField.Subject, LiteraryJellyField.Relation,
            LiteraryJellyField.Kind, LiteraryJellyField.Evidence }, LiteraryJellyValidation.Check(fact, "Source").Select(x => x.Field).ToArray());
    }
}
