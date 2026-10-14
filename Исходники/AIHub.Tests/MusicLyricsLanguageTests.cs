using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicLyricsLanguageTests
{
    private static MusicGenerationJob Job(string lyrics, params string[] language) => new(Guid.NewGuid().ToString("N"),
        "models", "output", "test", "rock", lyrics, 30, DateTime.Now, []) {
        Wishes = new(false, false, false, new() { ["language"] = language }, [])
    };

    [TestMethod]
    public void ExplicitSongLanguageOverridesTheScriptAndNeverUsesInterfaceLanguage()
    {
        Assert.AreEqual("en", MusicLyricsSpeechLanguage.Resolve(Job("Кот на крыше включает солнце", "english")));
        var job = Job("An English song", "russian");
        Assert.AreEqual("ru", MusicLyricsSpeechLanguage.Resolve(job));
        job.Expert.TextValues["vocal_language"] = "de";
        Assert.AreEqual("de", MusicLyricsSpeechLanguage.Resolve(job));
    }

    [TestMethod]
    public void OldRussianSourceWithEnglishSectionsDoesNotLetTheIntroChooseEnglish()
    {
        Assert.AreEqual("ru", MusicLyricsSpeechLanguage.Resolve(Job("[Verse 1]\nУ подъезда кот мяукает: я опять включил рассвет!\n[Chorus]")));
        Assert.AreEqual("auto", MusicLyricsSpeechLanguage.Resolve(Job("[Verse 1]\nAn English song without saved language.")));
    }

    [TestMethod]
    public void MultilingualAndUncertainSourcesRemainAutomatic()
    {
        Assert.AreEqual("auto", MusicLyricsSpeechLanguage.Resolve(Job("Я пою на двух языках", "russian", "english")));
        Assert.AreEqual("auto", MusicLyricsSpeechLanguage.Resolve(Job("Я співаю пісню про сонце і ранкове світло")));
        Assert.AreEqual("auto", MusicLyricsSpeechLanguage.Resolve(Job("[Verse 1]\nЯ")));
    }
}
