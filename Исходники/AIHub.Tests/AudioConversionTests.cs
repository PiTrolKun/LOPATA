using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class AudioConversionTests
{
    [TestMethod]
    public async Task FourFormatsKeepTagsCoversOpaqueDataAndSourcePrecision()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "Песня ё.wav"); Wave(source);
            var tags = new Dictionary<string, string> { ["title"] = "Название ё = ; # \\", ["artist"] = "Исполнитель", ["album"] = "Альбом",
                ["lyrics"] = "[Verse]\r\nСлова песни\rТретья\nстрока", ["comment"] = "Заметка\nВторая строка",
                ["LOPATA_PARAMETERS"] = "{\"temperature\":0.7}", ["LOPATA_SEED"] = "42", ["custom_key"] = "неизвестный тег" };
            using var bitmap = new SkiaSharp.SKBitmap(2, 2); bitmap.Erase(SkiaSharp.SKColors.Red);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            var cover = new AudioCover("image/png", 3, "Обложка", png.ToArray());
            AppendChunk(source, "id3 ", AudioConversionMetadata.Id3(tags, [cover]));
            AppendChunk(source, "test", [3, 2, 1, 0, 254]);
            var before = File.ReadAllBytes(source); var service = new AudioConversionService();
            foreach (var format in Enum.GetValues<MusicAudioFormat>())
            {
                var result = await service.ConvertAsync(source, null, format, 160, CancellationToken.None);
                var found = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, result.OutputPath, CancellationToken.None);
                foreach (var pair in tags) Assert.AreEqual(pair.Value, found.Tags[pair.Key], format + " lost " + pair.Key);
                Assert.AreEqual(1, found.Channels); Assert.AreEqual(format == MusicAudioFormat.Opus ? 48000 : 44100, found.SampleRate);
                Assert.IsTrue(found.Covers.Any(c => c.Data.SequenceEqual(cover.Data)), format + " lost artwork");
                using var json = JsonDocument.Parse(File.ReadAllText(result.MetadataPath));
                var blocks = json.RootElement.GetProperty("Sources")[0].GetProperty("Blocks");
                CollectionAssert.AreEqual(new byte[] { 3, 2, 1, 0, 254 }, blocks.EnumerateArray().Single(b => b.GetProperty("Kind").GetString() == "WAV:test").GetProperty("Bytes").GetBytesFromBase64());
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.OutputPath)));
                Assert.AreEqual(hash, json.RootElement.GetProperty("AudioSha256").GetString());
                var roundtrip = await service.ConvertAsync(result.OutputPath, root, MusicAudioFormat.Flac, 320, CancellationToken.None);
                using var inherited = JsonDocument.Parse(File.ReadAllText(roundtrip.MetadataPath));
                Assert.AreEqual(2, inherited.RootElement.GetProperty("Sources").GetArrayLength());
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(source));
            Assert.IsFalse(Directory.GetFiles(root).Any(f => f.Contains(".lopata-") || f.EndsWith(".lopata-converting")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CollisionsCancellationCorruptionAndSidecarMismatchKeepExistingFiles()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "source.wav"); Wave(source);
            var existing = Path.Combine(root, "source_mp3.mp3"); File.WriteAllText(existing, "foreign");
            var service = new AudioConversionService();
            var result = await service.ConvertAsync(source, root, MusicAudioFormat.Mp3, 192, CancellationToken.None);
            StringAssert.EndsWith(result.OutputPath, "source_mp3_2.mp3"); Assert.AreEqual("foreign", File.ReadAllText(existing));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ConvertAsync(source, root, MusicAudioFormat.Flac, 320, cancelled.Token));
            File.WriteAllText(result.MetadataPath, "{\"SchemaVersion\":1,\"AudioSha256\":\"wrong\",\"Sources\":[]}");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(result.OutputPath, root, MusicAudioFormat.Flac, 320, CancellationToken.None));
            var corrupt = Path.Combine(root, "bad.wav"); File.WriteAllText(corrupt, "not audio");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(corrupt, root, MusicAudioFormat.Flac, 320, CancellationToken.None));
            Assert.IsTrue(File.Exists(source)); Assert.AreEqual("not audio", File.ReadAllText(corrupt));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.flac").Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancelDuringEncodingRemovesOnlyOwnedPartialsAndUnsupportedWavDoesNotLoseBits()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "long.wav"); Wave(source); const int dataSize = 64 * 1024 * 1024;
            using (var file = new FileStream(source, FileMode.Open, FileAccess.ReadWrite))
            using (var writer = new BinaryWriter(file))
            {
                file.SetLength(44L + dataSize); file.Position = 4; writer.Write(36 + dataSize); file.Position = 40; writer.Write(dataSize);
            }
            var service = new AudioConversionService(); await service.PrepareAsync(CancellationToken.None);
            using var cancel = new CancellationTokenSource(); var run = service.ConvertAsync(source, root, MusicAudioFormat.Mp3, 320, cancel.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!Directory.GetFiles(root, ".lopata-audio-*").Any() && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.IsFalse(run.IsCompleted, "The cancellation fixture must still be running."); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);
            Assert.AreEqual(1, Directory.GetFiles(root).Length); Assert.IsTrue(File.Exists(source));
            var highBits = Path.Combine(root, "24bit.wav"); Wave(highBits);
            using (var file = new FileStream(highBits, FileMode.Open, FileAccess.ReadWrite))
            using (var writer = new BinaryWriter(file))
            { file.Position = 28; writer.Write(44100 * 3); writer.Write((short)3); writer.Write((short)24); }
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAsync(highBits, root, MusicAudioFormat.Flac, 320, CancellationToken.None));
            Assert.AreEqual("AudioShell.UnsupportedCodec", error.Message); Assert.AreEqual(0, Directory.GetFiles(root, "*.flac").Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ShellProtocolGuardsPathsAndAcceptsOnlyAfterDialogDelivery()
    {
        var request = AudioShellRequest.ParseArguments(["--shell-audio", "--", @"C:\Music\Песня.mp3", @"C:\Music\Песня.mp3"]);
        Assert.AreEqual(1, request!.Paths.Count);
        foreach (var path in new[] { "relative.mp3", @"C:\Music\x.exe", @"C:\Music\x.mp3:secret", @"\\.\pipe\x.mp3" })
            Assert.Throws<InvalidDataException>(() => AudioShellRequest.ParseArguments(["--shell-audio", "--", path]));
        Assert.IsNull(AudioShellRequest.ParseArguments([]));
        using var buffer = new MemoryStream(); await AudioShellProtocol.WriteAsync(buffer, request, CancellationToken.None);
        Assert.AreEqual(AudioShellProtocol.RequestMarker, buffer.ToArray()[0]); buffer.Position = 1; var accepted = false;
        await AudioShellProtocol.ReceiveAsync(buffer, value => { accepted = true; CollectionAssert.AreEqual(request.Paths.ToArray(), value.Paths.ToArray()); return Task.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(accepted); Assert.AreEqual(ImageShellProtocol.Accepted, buffer.ToArray()[^1]);
    }

    [TestMethod]
    public void RegistryCommandIsQuotedAndRemovalCannotTouchAnotherCopy()
    {
        var root = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N"); var folder = MusicOutputTests.Temporary();
        try
        {
            var exe = Path.Combine(folder, "ЛОПАТА test.exe"); File.WriteAllText(exe, "test");
            var registration = new AudioShellIntegration(exe, root); registration.Apply(true, "ЛОПАТА", "Перекодировать аудио…");
            using (var key = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath + @"\shell\01-convert\command"))
                Assert.AreEqual('"' + exe + "\" --shell-audio -- \"%1\"", key!.GetValue(""));
            var other = Path.Combine(folder, "other.exe"); File.WriteAllText(other, "test");
            new AudioShellIntegration(other, root).Apply(true, "LOPATA", "Convert audio…");
            registration.Apply(false, "", "");
            using (var key = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath)) Assert.AreEqual(other, key!.GetValue("LOPATA.Executable"));
            new AudioShellIntegration(other, root).Apply(false, "", "");
            using (var missing = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath)) Assert.IsNull(missing);
            using (var foreign = Registry.CurrentUser.CreateSubKey(registration.RegisteredPath)) foreign.SetValue("MUIVerb", "foreign");
            Assert.Throws<InvalidOperationException>(() => registration.Apply(true, "LOPATA", "Convert"));
            registration.Apply(false, "", "");
            using var untouched = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath); Assert.AreEqual("foreign", untouched!.GetValue("MUIVerb"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); Directory.Delete(folder, true); }
    }

    [TestMethod]
    public Task DialogWrapsInBothThemesAndLanguages() => ScenarioNavigationTests.Sta(() =>
    {
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            var l = new LocalizationService(); l.Load(language);
            var owner = new Window();
            foreach (var key in new[] { "PanelBrush", "InputBrush", "WindowBrush" }) owner.Resources[key] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
            owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["UiBodyFontSize"] = 17d;
            owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(25, 35, 52) : Color.FromRgb(240, 242, 245));
            owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["StepBadgeBrush"] = Brushes.RoyalBlue;
            new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            var dialog = new AudioConversionWindow(owner, l.T); dialog.AddPaths([@"C:\Очень длинная папка\Очень длинная песня с русскими словами.mp3"]);
            dialog.Width = 510; dialog.Height = 520; dialog.Show(); dialog.UpdateLayout();
            foreach (var text in ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBlock>()) Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBlock>().Any(t => t.Text.StartsWith("AudioShell.")));
            var folder = Path.Combine(Path.GetTempPath(), "lopata-audio-ui"); Directory.CreateDirectory(folder);
            var snapshot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            snapshot.Render(dialog); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(snapshot));
            using (var output = File.Create(Path.Combine(folder, language + "-" + dark + ".png"))) encoder.Save(output);
            dialog.Close(); owner.Close();
        }
    });

    private static void Wave(string path)
    {
        using var writer = new BinaryWriter(File.Create(path)); const int samples = 44100;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1);
        writer.Write((short)1); writer.Write(samples); writer.Write(samples * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * .05) * 8000));
    }
    private static void AppendChunk(string path, string kind, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite); using var writer = new BinaryWriter(stream);
        stream.Position = stream.Length; writer.Write(System.Text.Encoding.ASCII.GetBytes(kind)); writer.Write(data.Length); writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0); stream.Position = 4; writer.Write(checked((int)stream.Length - 8));
    }
}
