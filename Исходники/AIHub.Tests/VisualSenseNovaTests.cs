using System.IO;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VisualSenseNovaTests
{
    [TestMethod]
    public void FullCheckpointIsOneManagedPackageForBothVisualRoles()
    {
        var root = Path.GetTempPath();
        var card = VisualSenseNova.Card(root);
        Assert.IsTrue(VisualComponents.Advisors.Contains(VisualSenseNova.Id));
        Assert.IsTrue(VisualComponents.Artists.Contains(VisualSenseNova.Id));
        Assert.IsTrue(VisualComponents.CanSeeImages(VisualSenseNova.Id));
        Assert.AreEqual(VisualSenseNova.Id, ImageGenerationCatalog.Get(VisualSenseNova.Id).Id);
        Assert.IsFalse(ImageGenerationCatalog.IsAvailable(VisualSenseNova.Id));
        Assert.AreEqual(8, card.Files.Count(file => file.RelativePath.StartsWith("weights/model-", StringComparison.Ordinal)
            && file.RelativePath.EndsWith(".safetensors", StringComparison.Ordinal)));
        Assert.AreEqual(card.Files.Count, card.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(card.Files.All(file => file.SizeBytes > 0 && file.Sha256.Length == 64));
    }

    [TestMethod]
    public void BothRoleSelectorsReviewTheSameDownloadedFiles()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var root = files.File("models");
        var storage = new StorageSettings { Models = new() { Locations = [new() { Path = root }] } };
        var card = VisualSenseNova.Card(root);
        var path = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        var components = new VisualComponents(storage, 1);
        var advisor = components.ReviewRemoval(VisualSenseNova.Id, artist: false);
        var artist = components.ReviewRemoval(VisualSenseNova.Id, artist: true);
        Assert.IsNotNull(advisor);
        Assert.IsNotNull(artist);
        Assert.AreEqual(advisor.Bytes, artist.Bytes);
        Assert.AreEqual(3L, artist.Remove());
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(components.HasRemovableFiles(VisualSenseNova.Id, artist: false));
    }

    [TestMethod]
    public async Task InstalledSenseNovaRespondsInRussianAndDrawsTwiceInOneWorker()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SENSENOVA_TEST_ROOT");
        var fixtures = Environment.GetEnvironmentVariable("LOPATA_SENSENOVA_TEST_FIXTURES");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fixtures))
            Assert.Inconclusive("Opt-in real installed SenseNova smoke test.");
        var storage = new StorageSettings { Models = new() { Locations = [new() { Path = root }] } };
        var components = new VisualComponents(storage, 1);
        var session = new VisualSenseNovaSession();
        var advisor = new VisualAdvisor(session);
        using var files = new VideoPreparationTests.TestFiles();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var token = deadline.Token;
        try
        {
            var path = await components.PrepareAdvisorAsync(VisualSenseNova.Id, false, null, token);
            Assert.IsNotNull(path);
            var prepared = await advisor.PrepareAsync(path, false, true, token, senseNova: true);
            var pid = session.ProcessId;
            Assert.IsNotNull(pid);
            object[] messages = [VisualAdvisor.Message("system", "Отвечай кратко и по-русски."),
                VisualAdvisor.Message("user", "Если ты понимаешь этот русский вопрос, напиши одно слово: понимаю.")];
            Assert.IsGreaterThan(0, await advisor.CountAsync(prepared, messages, token));
            StringAssert.Contains((await session.CompleteAsync(path, messages, 28, token)).ToLowerInvariant(), "понимаю");

            var cards = await components.PrepareArtistAsync(VisualSenseNova.Id, true, false, null, token);
            Assert.IsNotNull(cards);
            var refs = new[] { "input.png", "reference-color.png", "reference-square.png" }
                .Select(name => Path.Combine(fixtures, name)).ToArray();
            for (var i = 0; i < 2; i++)
            {
                var directory = files.File(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), VisualSenseNova.Id,
                    "Сделай кружку зелёной по второму изображению. Сохрани фон и жёлтый квадрат с третьего изображения.",
                    512, 512, [42L + i], root, directory) { ReferenceImages = refs };
                ImageGenerationSessionStore.EnsureInitialized(directory, request.Id);
                var turn = await new ImageGenerationRuntime(session).RunAsync(request, cards, null, token);
                Assert.HasCount(1, turn.Results);
                Assert.IsTrue(ImageGenerationRuntime.IsValidImage(ImageGenerationSessionStore.ResultPath(request, 0), request));
                Assert.AreEqual(pid, session.ProcessId);
                File.Copy(ImageGenerationSessionStore.ResultPath(request, 0), Path.Combine(fixtures, $"lopata-sensenova-ru-{i + 1}.png"), true);
                if (i == 0)
                    StringAssert.Contains((await session.CompleteAsync(path, messages, 28, token)).ToLowerInvariant(), "понимаю");
            }
        }
        finally { await session.RetireAsync(); }
        Assert.IsFalse(session.IsRunning);
    }
}
