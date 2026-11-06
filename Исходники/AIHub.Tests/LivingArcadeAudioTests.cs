using System.Buffers.Binary;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeAudioTests
{
    [TestMethod]
    public void EveryGameplayCueHasAudibleBoundedPcm16WithoutExternalFiles()
    {
        string[] cues = ["shoot", "hit", "enemy_down", "bonus", "negative", "boss", "boss_phase",
            "speed_up", "slow_down", "super", "defeat", "complete"];
        var hashes = new HashSet<string>();
        foreach (var cue in cues)
        {
            var data = LivingArcadeAudio.SynthesizeEffect(cue);
            Assert.IsTrue(data.Length >= LivingArcadeAudio.SampleRate / 10, cue);
            Assert.IsTrue(data.Length <= LivingArcadeAudio.SampleRate * 2, cue);
            Assert.AreEqual(0, data.Length % 2, cue);
            var peak = 0;
            for (var i = 0; i < data.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2))));
            Assert.IsTrue(peak > 2000 && peak < short.MaxValue, cue);
            Assert.AreEqual((short)0, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0, 2)), cue);
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
        }
        Assert.AreEqual(cues.Length, hashes.Count);
    }

    [TestMethod]
    public void MusicIsRepeatableAndHonorsValidatedArtistNotes()
    {
        var a = LivingArcadeAudio.SynthesizeMusic(17, [60, 64, 67, 72]);
        var b = LivingArcadeAudio.SynthesizeMusic(17, [60, 64, 67, 72]);
        var otherNotes = LivingArcadeAudio.SynthesizeMusic(17, [62, 65, 69, 74]);
        CollectionAssert.AreEqual(a, b);
        Assert.IsFalse(a.SequenceEqual(otherNotes));
        Assert.IsTrue(a.Length is > 200_000 and < 1_000_000);
        Assert.AreEqual((short)0, BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(0, 2)));
        Assert.IsTrue(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(a.Length - 2, 2))) < 200);
        for (var i = 0; i < a.Length; i += 2)
            Assert.IsTrue(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(i, 2))) < short.MaxValue);
        var longMelody = Enumerable.Repeat(60, 64).ToArray();
        var baseline = LivingArcadeAudio.SynthesizeMusic(17, longMelody);
        longMelody[40] = 67;
        Assert.IsFalse(baseline.SequenceEqual(LivingArcadeAudio.SynthesizeMusic(17, longMelody)),
            "A model melody longer than 32 notes must be played in full.");
    }

    [TestMethod]
    public void InvalidArtistMelodyFallsBackAndVolumesAreIndependent()
    {
        CollectionAssert.AreEqual(LivingArcadeAudio.SynthesizeMusic(1), LivingArcadeAudio.SynthesizeMusic(1, [int.MinValue]));
        using var audio = new LivingArcadeAudio();
        audio.EffectsVolume = 2;
        audio.MusicVolume = -.5f;
        Assert.AreEqual(1f, audio.EffectsVolume);
        Assert.AreEqual(0f, audio.MusicVolume);
        audio.MusicVolume = float.NaN;
        Assert.AreEqual(0f, audio.MusicVolume);
        Assert.IsTrue(audio.IsAvailable);
    }

    [TestMethod]
    public void MixerPreservesEndingCueButClearsOtherEffectsOnPause()
    {
        var mixer = new LivingArcadeAudio.PcmMixer();
        mixer.SetVolumes(1, 1);
        mixer.SetMusic(LivingArcadeAudio.SynthesizeMusic(9));
        mixer.SetPaused(false, false);
        mixer.Add(LivingArcadeAudio.SynthesizeEffect("shoot"), false, false);
        mixer.Add(LivingArcadeAudio.SynthesizeEffect("complete"), true, true);
        Assert.IsTrue(mixer.SetPaused(true, true));
        var output = new byte[LivingArcadeAudio.SampleRate * 2];
        Assert.AreEqual(output.Length, mixer.Read(output, 0, output.Length));
        Assert.IsTrue(output.Any(b => b != 0), "The ending cue must survive pausing the simulation.");
        Array.Clear(output);
        mixer.Read(output, 0, output.Length);
        Assert.IsTrue(output.All(b => b == 0), "Paused background music and cleared shots must stay silent.");
        mixer.SetPaused(false, false);
        mixer.Read(output, 0, output.Length);
        Assert.IsTrue(output.Any(b => b != 0), "Resuming restores the procedural background music.");
    }
}
