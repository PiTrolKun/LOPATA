using Lopata.Island;

namespace AIHub.Tests;

[TestClass]
public sealed class IslandVoiceCommandTests
{
    [TestMethod]
    public void ListedCommandsRoundTripInBothInterfaceLanguages()
    {
        foreach (var russian in new[] { false, true })
        foreach (var entry in IslandVoiceCommands.Entries)
        foreach (string phrase in entry.Examples(russian))
            Assert.AreEqual(entry.Action, IslandVoiceCommands.Parse(phrase, russian)?.Action, phrase);
    }

    [TestMethod]
    [DataRow("Громкость музыки девяносто пять процентов", true, "Volume", 95)]
    [DataRow("Громкость музыки 30%.", true, "Volume", 30)]
    [DataRow("Громкость плеера на тридцать процентов", true, "Volume", 30)]
    [DataRow("Продолжим музыку.", true, "Resume", 0)]
    [DataRow("Продолжай.", true, "Resume", 0)]
    [DataRow("Продолжай, пожалуйста.", true, "Resume", 0)]
    [DataRow("Давай на паузу.", true, "Pause", 0)]
    [DataRow("Pause.", true, "Pause", 0)]
    [DataRow("Пауза.", false, "Pause", 0)]
    [DataRow("Pausa.", false, "Pause", 0)]
    [DataRow("Please pause.", true, "Pause", 0)]
    [DataRow("Репит.", false, "Repeat", 0)]
    [DataRow("Shuffle.", true, "Shuffle", 0)]
    [DataRow("Продолжи воспроизведение.", true, "Resume", 0)]
    [DataRow("Поставь музыку на паузу.", true, "Pause", 0)]
    [DataRow("Убавь громкость.", true, "Quieter", 0)]
    [DataRow("Дальше.", true, "Next", 0)]
    [DataRow("Что за песня?", true, "Current", 0)]
    [DataRow("Поставь громкость плеера на сорок два процента.", true, "Volume", 42)]
    [DataRow("Промотай на пятнадцать секунд вперёд.", true, "SeekRelative", 15)]
    [DataRow("Перейди к 1:42.", true, "SeekAbsolute", 102)]
    [DataRow("Jump to one minute and thirty seconds.", false, "SeekAbsolute", 90)]
    [DataRow("Music volume one hundred percent", false, "Volume", 100)]
    [DataRow("Перемотай на две минуты пятнадцать секунд назад", true, "SeekRelative", -135)]
    [DataRow("Seek to one minute and thirty seconds", false, "SeekAbsolute", 90)]
    [DataRow("Перемотай на 1:30.", true, "SeekAbsolute", 90)]
    public void RecognizedNumbersAreParsed(string text, bool russian, string name, int value)
    {
        var command = IslandVoiceCommands.Parse(text, russian);
        Assert.IsNotNull(command);
        Assert.AreEqual(name, command.Action.ToString());
        Assert.AreEqual(value, command.Value);
    }

    [TestMethod]
    [DataRow("Пауса", true, "Pause", 0)]
    [DataRow("Продалжи", true, "Resume", 0)]
    [DataRow("Продолжим музку", true, "Resume", 0)]
    [DataRow("Громксть музыки 37%", true, "Volume", 37)]
    [DataRow("Перематай на 1:42", true, "SeekAbsolute", 102)]
    [DataRow("Перематай на минуту назад", true, "SeekRelative", -60)]
    [DataRow("Nex track", false, "Next", 0)]
    [DataRow("Nex", false, "Next", 0)]
    public void CloseUnambiguousMatchesKeepTheActualNumber(string text, bool russian, string name, int value)
    {
        var command = IslandVoiceCommands.Parse(text, russian);
        Assert.IsNotNull(command);
        Assert.AreEqual(name, command.Action.ToString());
        Assert.AreEqual(value, command.Value);
        Assert.IsLessThan(100, command.Similarity);
    }

    [TestMethod]
    [DataRow("Пбавь громкость", true)]
    [DataRow("Не продолжи воспроизведение", true)]
    [DataRow("Непродолжи воспроизведение", true)]
    [DataRow("Громксть музыки минус тридцать процентов", true)]
    [DataRow("Громкость музыки -1%", true)]
    [DataRow("Громксть музыки 30.5%", true)]
    [DataRow("Громкость музыки 3 7%", true)]
    [DataRow("Громксть Windows 30%", true)]
    [DataRow("Перематай на минуту позади", true)]
    [DataRow("Иди в магазин", true)]
    [DataRow("Don't pause music.", false)]
    [DataRow("Не Pause, пожалуйста.", true)]
    [DataRow("Continue.", true)]
    [DataRow("Продолжай.", false)]
    [DataRow("Не дальше, пожалуйста.", true)]
    [DataRow("Поставь громкость плеера на сорок или пятьдесят процентов.", true)]
    [DataRow("Промотай на пятнадцать секунд.", true)]
    public void AmbiguousNegatedAndUnrelatedSpeechHasNoAction(string text, bool russian) =>
        Assert.IsNull(IslandVoiceCommands.Parse(text, russian));

    [TestMethod]
    public void WakePhraseIsSeparateFromCommandsAndUsesWholePhrase()
    {
        Assert.IsTrue(IslandVoiceCommands.IsWake("Лопата, прием!", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Lopata, come in.", "Lopata, come in"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("LOPATA, приём!", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Лапата, приём!", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("LAPATA, come in.", "Lopata, come in"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Lapata, приём!", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Лапата слушай", "Лопата, слушай"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Лопата: come inn.", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Лопата, приемм.", "Lopata, come in"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("LOPATA слушай", "Лопата, слушай"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Lopada come in.", "Lopata, come in"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Лопада приём", "Лопата приём"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Лапада приём", "Лопата приём"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("лапатами приём", "Лопата приём"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Лопада слушай", "Лопата слушай"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("лопатами приём", "Лопата приём"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Лопата.", "Лопата приём"));
        Assert.IsTrue(IslandVoiceCommands.IsWake("Остров, слушай.", "Остров слушай"));
        Assert.IsFalse(IslandVoiceCommands.IsWake("Лопата приём, выключи музыку", "Лопата приём"));
        Assert.IsNull(IslandVoiceCommands.Parse("Лопата приём", true));
        Assert.IsNull(IslandVoiceCommands.Parse("Громкость музыки 101 процент", true));
        Assert.IsNull(IslandVoiceCommands.Parse("Громкость Windows 101 процент", true));
        Assert.IsNull(IslandVoiceCommands.Parse("Seek to 1:77", false));
        Assert.AreEqual(IslandVoiceAction.Pause, IslandVoiceCommands.Parse("Pausa.", true)?.Action);
    }

    [TestMethod]
    public void ExpandedCommandsPreservePayloadAndRejectPartialLinkedRequests()
    {
        Assert.AreEqual("du hast", IslandVoiceCommands.Parse("Включи Du Hast", true)?.Query);
        Assert.AreEqual(IslandVoiceAction.FindAlbum, IslandVoiceCommands.Parse("Play album Mutter", false)?.Action);
        Assert.AreEqual(IslandVoiceAction.FindDescription, IslandVoiceCommands.Parse("Поставь что-нибудь спокойное с женским вокалом", true)?.Action);
        Assert.AreEqual(IslandVoiceAction.FindHistory, IslandVoiceCommands.Parse("Включи то, что играло вчера перед Rammstein", true)?.Action);
        Assert.AreEqual(420, IslandVoiceCommands.Parse("Поставь таймер на семь минут", true)?.Value);
        Assert.AreEqual(IslandVoiceAction.WindowsVolume, IslandVoiceCommands.Parse("Громкость Windows 30 процентов", true)?.Action);
        var linked = IslandVoiceCommands.Parse("Включи избранное и выключи музыку через полчаса", true);
        Assert.AreEqual(IslandVoiceAction.PlayFavorites, linked?.Action);
        Assert.AreEqual(1800, linked?.StopAfterSeconds);
        Assert.IsNull(IslandVoiceCommands.Parse("Включи избранное и выключи музыку через не знаю", true));
        Assert.IsNull(IslandVoiceCommands.Parse("Не запускай таймер на семь минут", true));
    }

    [TestMethod]
    [DataRow("Запиши в заметку", true)]
    [DataRow("Пожалуйста, запиши в заметки проверить обложку.", true)]
    [DataRow("Запиши заметку. Проверить обложку.", true)]
    [DataRow("Запишы в заметку.", true)]
    [DataRow("Добавь заметку", true)]
    [DataRow("Add a note", false)]
    [DataRow("Take a note: Check the cover.", false)]
    public void ARequestForANoteAlwaysStartsASeparateUtterance(string phrase, bool russian)
    {
        var note = IslandVoiceCommands.Parse(phrase, russian);
        Assert.AreEqual(IslandVoiceAction.StartNote, note?.Action);
        Assert.AreEqual("", note?.Query);
    }

    [TestMethod]
    public void DictatedTextOnlyFollowsThePromptAndKeepsPunctuation()
    {
        const string phrase = "Не забыть обложку!";
        Assert.IsNull(IslandVoiceCommands.Parse(phrase, true));
        Assert.IsNull(IslandVoiceCommands.Parse("Не запиши в заметку.", true));
        Assert.AreEqual(IslandVoiceAction.ShowNotes, IslandVoiceCommands.Parse("Покажи заметки", true)?.Action);
        var note = IslandVoiceCommands.ParseNoteReply("  " + phrase + " ");
        Assert.AreEqual(IslandVoiceAction.AddNote, note?.Action);
        Assert.AreEqual(phrase, note?.Query);
        Assert.AreEqual(IslandVoiceAction.AddNote, IslandVoiceCommands.ParseNoteReply("Пауза.")?.Action);
        Assert.AreEqual(IslandVoiceAction.CancelNote, IslandVoiceCommands.ParseNoteReply("Отмена, пожалуйста.")?.Action);
        Assert.IsNull(IslandVoiceCommands.ParseNoteReply("..."));
    }

    [TestMethod]
    public void MicrophoneLeaseExcludesOtherCaptureAndReleasesOnClose()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-voice-lease-");
        try
        {
            using (var first = IslandMicrophoneLease.TryAcquire(directory.FullName))
            {
                Assert.IsNotNull(first);
                Assert.IsNull(IslandMicrophoneLease.TryAcquire(directory.FullName));
            }
            using var next = IslandMicrophoneLease.TryAcquire(directory.FullName);
            Assert.IsNotNull(next);
        }
        finally { directory.Delete(recursive: true); }
    }
}
