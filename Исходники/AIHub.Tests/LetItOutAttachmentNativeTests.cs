using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutAttachmentNativeTests
{
    [TestMethod]
    public async Task InstalledGemmaSeesImageAndFindsAnUnseenDocumentFragment()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_ATTACHMENTS_NATIVE");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt in to the authorized vision-module preparation and actual Gemma test."); return; }
        Directory.CreateDirectory(output); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var storage = new StorageSettingsStore().LoadOrCreate();
        var prepared = await LetItOutChatPreparation.PrepareAsync(storage, 2, false, new Progress<LetItOutChatPreparationProgress>(), timeout.Token);
        Assert.IsTrue(prepared.Ready); Assert.IsTrue(await LetItOutVision.PrepareAsync(prepared.CoreModelPath!, true, null, timeout.Token));
        var store = new LetItOutConversationStore(Path.Combine(output, "chat.db")); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        var image = Path.Combine(output, "рисунок.png"); MakeImage(image);
        var attached = await store.ImportAttachmentAsync(id, image, timeout.Token);
        var history = new List<LetItOutSavedMessage> { new("user", "Назови цвет и форму двух фигур и слово на картинке. Ответь коротко.", Attachments: [attached.Id]) };
        store.SaveContent(id, history, "");
        using var runtime = new LetItOutChatRuntime(new(new UserProfileStore(), new IpLocationService())) { MemoryStore = store,
            DecideGpu = (_, _) => Task.FromResult(LlamaGpuDecision.Hybrid) };
        try
        {
            LetItOutChatRequest Request() => new(id, Guid.NewGuid().ToString("N"), prepared.CoreModelPath!, history.Select(m => new LetItOutChatMessage(m.Role, m.Content, m.Attachments)).ToArray(), "ru");
            var first = await runtime.GenerateAsync(Request(), new Progress<ModelStreamChunk>(), timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "image-answer.txt"), first, timeout.Token);
            StringAssert.Contains(first.ToLowerInvariant(), "чай");
            Assert.IsTrue(first.Contains("красн", StringComparison.OrdinalIgnoreCase) && first.Contains("син", StringComparison.OrdinalIgnoreCase));
            history.Add(new("assistant", first));
            var document = Path.Combine(output, "заметка.txt");
            await File.WriteAllTextAsync(document, string.Concat(Enumerable.Repeat("В заметке описан обычный спокойный день. Мы обсуждали музыку и книги.\n", 600)) + "\nДля шкафчика записан код ZETA-4827.\n", timeout.Token);
            var doc = await store.ImportAttachmentAsync(id, document, timeout.Token);
            history.Add(new("user", "Найди в приложенной заметке код для шкафчика. Назови его точно.", Attachments: [doc.Id])); store.SaveContent(id, history, "");
            var second = await runtime.GenerateAsync(Request(), new Progress<ModelStreamChunk>(), timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "document-answer.txt"), second, timeout.Token);
            StringAssert.Contains(second, "ZETA-4827");
            history.Add(new("assistant", second)); store.SaveContent(id, history, "");
            var measuredRequest = Request() with { Messages = Request().Messages.Append(new("user", "Снова посмотри на картинку", [attached.Id])).ToArray() };
            var before = await runtime.MeasureContextAsync(measuredRequest, timeout.Token);
            Assert.IsTrue(before.UsedTokens >= 1088 && before.CanTrim);
            var trimmed = await runtime.TrimContextAsync(measuredRequest, timeout.Token);
            Assert.IsTrue(trimmed.UsedTokens >= 1088 && trimmed.UsedTokens < before.UsedTokens);
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { image = first, document = second, pairs = store.MemoryPairs(id).Count }), timeout.Token);
        }
        finally { await runtime.RetireAsync(); }
    }
    private static void MakeImage(string path)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try {
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(Brushes.White, null, new(0, 0, 400, 240));
                dc.DrawRectangle(Brushes.Blue, null, new(30, 25, 140, 80));
                dc.DrawEllipse(Brushes.Red, null, new(280, 65), 45, 45);
                dc.DrawText(new FormattedText("ЧАЙ", System.Globalization.CultureInfo.GetCultureInfo("ru"), System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Arial"), 60, Brushes.Black, 1), new(105, 140)); }
            var bitmap = new RenderTargetBitmap(400, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
        } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
    }
}
