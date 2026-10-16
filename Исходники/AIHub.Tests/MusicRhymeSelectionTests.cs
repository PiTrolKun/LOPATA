using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

/// <summary>Editorial selection contracts, using a tiny deterministic dictionary instead of models or the shipped corpus.</summary>
[TestClass]
public sealed class MusicRhymeSelectionTests
{
    private const string Cat = "В саду шумит весёлый кот,";
    private const string Song = "И песню тихую поёт.";
    private const string Moon = "Над крышей разлилась луна,";
    private const string River = "В реке качается она.";

    [TestMethod]
    [DataRow("AABB")]
    [DataRow("ABAB")]
    [DataRow("ABBA")]
    public void GoodSchemesOfferAnActualRhymingPairAsVariation(string scheme)
    {
        var verse = scheme switch {
            "ABAB" => new[] { Cat, Moon, Song, River },
            "ABBA" => new[] { Cat, Moon, River, Song },
            _ => new[] { Cat, Song, Moon, River }
        };
        var result = Analyze("[Куплет]\n" + string.Join('\n', verse));
        Assert.AreEqual(1, result.Fragments.Length);
        var fragment = result.Fragments.Single();
        Assert.AreEqual("variation", fragment.Kind);
        Assert.AreEqual(2, fragment.Lines.Length);
        Assert.IsTrue(result.PreserveRhymePairs.Any(p => p.SequenceEqual(fragment.Lines)));
        var pair = fragment.Lines.Select(n => result.Lines.Single(l => l.Number == n)).ToArray();
        Assert.IsTrue(pair[0].Readings.Any(a => pair[1].Readings.Any(b => a.Key == b.Key)));
        Assert.AreNotEqual(pair[0].Ending, pair[1].Ending);
        AssertReasonNamesSource(result);
    }

    [TestMethod]
    public void EnglishGoodTextGetsVariationWithoutAnInventedFault()
    {
        var result = Analyze("[Verse]\nThe moon is bright tonight,\nThe stars are shining light.\nI hear the rain below,\nAnd watch the rivers flow.");
        Assert.AreEqual("variation", result.Fragments.Single().Kind);
        Assert.AreEqual(2, result.Fragments.Single().Lines.Length);
        Assert.IsTrue(result.PreserveRhymePairs.Any(p => p.SequenceEqual(result.Fragments.Single().Lines)));
        AssertReasonNamesSource(result);
    }

    [TestMethod]
    public void KnownBrokenPairIsChosenWhileTheObservedAnchorIsPreserved()
    {
        var result = Analyze("[Куплет]\n" + string.Join('\n', Cat, Song, Moon, "В реке качается рассвет."));
        Assert.IsTrue(result.Lines.All(l => l.Readings.Length > 0 && l.Readings.All(r => !r.Uncertain)),
            "This fixture tests a known broken pair, so every ending must have an unambiguous reading.");
        var fragment = result.Fragments.Single();
        Assert.AreEqual("mismatch", fragment.Kind);
        CollectionAssert.AreEqual(new[] { 3, 4 }, fragment.Lines);
        Assert.IsTrue(result.PreserveRhymePairs.Any(p => p.SequenceEqual(new[] { 1, 2 })));
        Assert.IsFalse(fragment.Lines.Intersect(new[] { 1, 2 }).Any());
    }

    [TestMethod]
    [DataRow("[Куплет]\nМы молча подошли к замку,\nИ видим древнюю реку.")]
    [DataRow("[Verse]\nThe moon is bright tonight,\nI whisper flarnovex.")]
    public void MissingOrAmbiguousReadingStillOffersAPlaceWithoutClaimingMismatch(string source)
    {
        var result = Analyze(source);
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsFalse(result.Fragments.Any(f => f.Kind is "mismatch" or "length-opportunity"));
        Assert.IsTrue(result.Lines.Any(l => l.Readings.Length == 0 || l.Readings.Any(r => r.Uncertain)));
        AssertReasonNamesSource(result);
    }

    [TestMethod]
    [DataRow("Одинокий неизвестнослов.")]
    [DataRow("[Intro]\nЛа-ла!\n[Outro]\nМяу!")]
    public void SingleLineAndSungPerformanceLyricsHaveAQualifiedFallback(string source)
    {
        var result = Analyze(source);
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsTrue(result.Fragments.All(f => f.Kind == "variation"));
        AssertReasonNamesSource(result);
    }

    [TestMethod]
    [DataRow(" \r\n\t")]
    [DataRow("**Название: Ночной сад**")]
    [DataRow("[Куплет]\n[Припев]\n")]
    [DataRow("[Instrumental]\n(Guitar solo)\n(Drum fill)")]
    [DataRow("{Вокалист}\n(Шёпотом)")]
    public void MetadataCannotSatisfyTheMinimumByPretendingToBeLyrics(string source)
    {
        var result = Analyze(source);
        Assert.AreEqual(0, result.Lines.Length);
        Assert.AreEqual(0, result.Fragments.Length);
    }

    [TestMethod]
    public void MarkdownCrLfEmojiAndStressRetainOriginalUtf16SlicesInsideTheSelectedScope()
    {
        const string source = "**Название: 🌙 Ночной сад**\r\n\r\n**[Куплет]**\r\n*Смотрю на старый за́мок,*\r\n*На стенах много ра́мок.*\r\n\r\n[Outro]\r\nТут другая строка.";
        var first = source.IndexOf("*Смотрю", StringComparison.Ordinal);
        var last = source.IndexOf("\r\n\r\n[Outro]", first, StringComparison.Ordinal);
        var scope = MusicRhymeAnalyzer.Scope(new(source, first + 4, last - first - 4), true);
        var result = MusicRhymeAnalyzer.Analyze(source, Lookup, scopeStart: scope.Start, scopeLength: scope.Length);
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.AreEqual(3, result.Lines.Length);
        foreach (var line in result.Lines)
        {
            Assert.AreEqual(line.Text, source.Substring(line.Start, line.Length));
            Assert.AreEqual(line.Ending, source.Substring(line.EndingStart, line.Ending.Length));
        }
        foreach (var line in Selected(result))
        {
            Assert.IsTrue(line.Start >= scope.Start && line.Start + line.Length <= scope.Start + scope.Length);
            Assert.IsTrue(line.Text.Contains('\u0301'));
            Assert.IsFalse(line.Text.Contains("другая", StringComparison.Ordinal));
        }
        Assert.IsFalse(result.Fragments.Any(f => f.Kind == "mismatch"));
    }

    [TestMethod]
    public void OnlySelectedLyricLinesCanBeColoredEvenWhenTheirStanzaIsContext()
    {
        var source = "[Куплет]\n" + string.Join('\n', Cat, Song, Moon, "В реке качается рассвет.");
        var start = source.IndexOf(Moon, StringComparison.Ordinal);
        var result = MusicRhymeAnalyzer.Analyze(source, Lookup, scopeStart: start, scopeLength: Moon.Length);
        CollectionAssert.AreEqual(new[] { 3 }, result.Fragments.SelectMany(f => f.Lines).Distinct().ToArray());
        Assert.AreEqual(4, result.Lines.Length); // The rest of the stanza is read-only context.
        var heading = MusicRhymeAnalyzer.Analyze(source, Lookup, scopeStart: 0, scopeLength: "[Куплет]".Length);
        Assert.AreEqual(0, heading.Fragments.Length);
    }

    [TestMethod]
    [DataRow("В реке медленно качается она.", true)]
    [DataRow("В реке тихо качается она.", false)]
    public void LengthOpportunityBeginsAtThreeSyllablesWithoutCallingItMeterFailure(string last, bool expected)
    {
        var result = Analyze("[Куплет]\n" + string.Join('\n', Cat, Song, Moon, last));
        Assert.AreEqual(8, result.Lines[0].Syllables);
        Assert.AreEqual(expected ? 11 : 10, result.Lines[3].Syllables);
        Assert.AreEqual(expected, result.Fragments.Any(f => f.Kind == "length-opportunity"));
        Assert.IsFalse(result.Fragments.Any(f => f.Kind == "mismatch"));
    }

    [TestMethod]
    public void LengthRequiresThreeReferenceLinesAndAtLeastSixtyPercent()
    {
        const string longer = "В реке медленно качается она."; // 11 syllables.
        const string evenLonger = "В реке задумчиво качается она."; // 12 syllables.
        var sixty = Analyze(string.Join('\n', Cat, Song, Moon, longer, evenLonger));
        var half = Analyze(string.Join('\n', Cat, Song, Moon, longer, evenLonger, "В реке очень медленно качается она."));
        var onlyTwo = Analyze(string.Join('\n', Cat, Song, longer));
        Assert.IsTrue(sixty.Fragments.Any(f => f.Kind == "length-opportunity"));
        Assert.IsFalse(half.Fragments.Any(f => f.Kind == "length-opportunity"));
        Assert.IsFalse(onlyTwo.Fragments.Any(f => f.Kind == "length-opportunity"));
    }

    [TestMethod]
    [DataRow("[Куплет]\nСпит кот\nИ тихо песенку поёт\nСпит кот\nИ тихо песенку поёт\nСпит кот\nИ тихо песенку поёт")]
    [DataRow("[Рэп]\nСнег.\nЯ прохожу по улицам знакомого квартала.\nКот не спешит.\nА за стеной опять кому-то обещают лето.\nТишина.")]
    [DataRow("[Blues]\nСпит кот\nСпит кот\nНад крышей разлилась луна")]
    public void AlternationFreeVerseAndBluesAreNotFlattenedToOneLineLength(string source)
    {
        var result = Analyze(source);
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsFalse(result.Fragments.Any(f => f.Kind == "length-opportunity"));
    }

    [TestMethod]
    [DataRow("[Припев]\nОгонь зажигает моряка,\nГде ветер — шепот, а волны — стон.\nОгонь зажигает моряка,\nВ сердце — тень, что не отпускает.")]
    [DataRow("[Куплет]\nТени знакомых стен — как старые друзья,\nНо в глазах их пустота, как вчера.\nЯ шёл по тропе, что звал меня домой,\nА сердце вспоминало всё, что лишилось.")]
    public void IdenticalWordsAndOneVowelAnchorsCannotProveAnotherPairWrong(string source)
    {
        var result = Analyze(source);
        Assert.IsTrue(result.Lines.All(l => l.Readings.Length > 0 && l.Readings.All(r => !r.Uncertain)));
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsFalse(result.Fragments.Any(f => f.Kind == "mismatch"));
    }

    [TestMethod]
    public void SixLineStanzaDoesNotMakeItsLastTwoLinesAnObligatoryCouplet()
    {
        var result = Analyze("[Строфа]\nПод старым ковром, среди пыли,\nНашёл я письмо, но не отправил.\nРукописный шёпот, как шёпот лет,\nСквозь годы дошёл до моей души.\nЛисты пожелтели, но слова чисты —\nВсё, что хотел сказать, осталось в строках.");
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsFalse(result.Fragments.Any(f => f.Kind == "mismatch" && f.Lines.Intersect(new[] { 5, 6 }).Any()));
    }

    [TestMethod]
    public void RepeatedChorusesDoNotConsumeTheBudgetWithIdenticalFragments()
    {
        const string chorus = "Огонь зажигает моряка,\nГде ветер — шепот, а волны — стон.\nОгонь зажигает моряка,\nВ сердце — тень, что не отпускает.";
        var result = Analyze(string.Join("\n\n", Enumerable.Repeat("[Припев]\n" + chorus, 5)));
        Assert.IsTrue(result.Fragments.Length is > 0 and <= 3);
        var signatures = result.Fragments.Select(f => string.Join('\n', f.Lines.Select(n =>
            result.Lines.Single(l => l.Number == n).Text.Trim()))).ToArray();
        Assert.AreEqual(signatures.Length, signatures.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(Selected(result).Count(), result.Fragments.Sum(f => f.Lines.Length));
    }

    [TestMethod]
    public void UnresolvedWindowsDoNotCutTheKnownConnectedSentencePairs()
    {
        var result = Analyze("Нет смысла в сдаче, но есть в этом смысл,\nКогда город дышит, и твой путь — это волшебство.\nСобрал сон, что упал в урну,\nИ превратил в сказку, что не принесет золота.\nВ каждом крике — звук, что не уходит,\nГород — это не место, а история.");
        Assert.IsTrue(result.Fragments.Length > 0);
        Assert.IsFalse(result.Fragments.Any(f => f.Lines.SequenceEqual(new[] { 2, 3 }) || f.Lines.SequenceEqual(new[] { 4, 5 })));
        AssertReasonNamesSource(result);
    }

    [TestMethod]
    public void CancellationIsHonoredBeforeAndDuringDictionaryWork()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        Assert.Throws<OperationCanceledException>(() => MusicRhymeAnalyzer.Analyze("Спит кот", Lookup, before.Token));
        using var during = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => MusicRhymeAnalyzer.Analyze("Спит кот\nВидит сад", word => {
            during.Cancel(); return Lookup(word);
        }, during.Token));
    }

    private static MusicRhymeAnalysis Analyze(string text) => MusicRhymeAnalyzer.Analyze(text, Lookup);

    private static IEnumerable<MusicRhymeLine> Selected(MusicRhymeAnalysis result) =>
        result.Fragments.SelectMany(f => f.Lines).Distinct().Select(n => result.Lines.Single(l => l.Number == n));

    private static void AssertReasonNamesSource(MusicRhymeAnalysis result)
    {
        foreach (var fragment in result.Fragments)
        {
            Assert.IsTrue(fragment.ReasonKey.StartsWith("Music.Rhyme.Selection.", StringComparison.Ordinal));
            Assert.IsTrue(fragment.Arguments.Length > 0);
            if (fragment.Kind is not ("variation" or "opportunity" or "uncertainty")) continue;
            var arguments = string.Join(' ', fragment.Arguments);
            Assert.IsTrue(fragment.Lines.Any(n => arguments.Contains(result.Lines.Single(l => l.Number == n).Ending, StringComparison.Ordinal)),
                "An editorial explanation must refer to an actual selected ending.");
        }
    }

    private static IReadOnlyList<RagLexiconHit> Lookup(string word)
    {
        var clean = MusicRhymePhonetics.Clean(word);
        var stressed = clean switch {
            "луна" => "луна́", "она" => "она́", "рассвет" => "рассве́т", "замку" => "за́мку|замку́", "реку" => "ре́ку",
            "моряка" => "моряка́", "отпускает" => "отпуска́ет", "друзья" => "друзья́", "вчера" => "вчера́",
            "домой" => "домо́й", "лишилось" => "лиши́лось", "пыли" => "пы́ли", "отправил" => "отпра́вил",
            "души" => "души́", "чисты" => "чисты́", "строках" => "стро́ках", _ => null
        };
        if (stressed is not null)
            return [new(clean, "stress", JsonSerializer.SerializeToElement(new { variants = stressed.Split('|') }),
                clean == "замку", "test", "CC0-1.0", "test fixture", clean)];
        var phones = clean switch {
            "the" => "DH AH0", "moon" => "M UW1 N", "is" => "IH1 Z", "bright" => "B R AY1 T", "tonight" => "T AH0 N AY1 T",
            "stars" => "S T AA1 R Z", "are" => "AA1 R", "shining" => "SH AY1 N IH0 NG", "light" => "L AY1 T", "i" => "AY1",
            "hear" => "HH IH1 R", "rain" => "R EY1 N", "below" => "B IH0 L OW1", "and" => "AE1 N D", "watch" => "W AA1 CH",
            "rivers" => "R IH1 V ER0 Z", "flow" => "F L OW1", "whisper" => "W IH1 S P ER0", _ => null
        };
        return phones is null ? [] : [new(clean, "pronunciation", JsonSerializer.SerializeToElement(new { phonemes = phones.Split(' ') }),
            false, "test", "CC0-1.0", "test fixture", clean)];
    }
}
