using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services;
using Lopata.Collection;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageUtilityCollectionTests
{
    public TestContext TestContext { get; set; } = null!;
    private string _root = "", _journal = "";
    private string? _previous;
    private string Magick => Path.Combine(AppContext.BaseDirectory, "ImageUtilityRuntime", "ImageMagick", "magick.exe");
    private string ExifTool => Path.Combine(AppContext.BaseDirectory, "MetadataRuntime", "exiftool.exe");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "LOPATA-ImageUtilityCollection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root); _journal = Path.Combine(_root, "collection");
        _previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", _journal);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", _previous);
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-ImageUtilityCollection-tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(prefix, Path.GetFullPath(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task AllWritableFormatsPublishVerifiedOriginAndExactReceiptsAfterTheMarker()
    {
        var source = await SourceAsync(); var original = Hash(source);
        var artSource = Path.Combine(_root, "small-art-source.png");
        _ = await Run(Magick, "-size", "8x8", "gradient:red-blue", "-depth", "8", artSource);
        var originalArt = Hash(artSource);
        var ai = new ControlledAi(); var processor = new ImageUtilityProcessor(Magick, ai);
        var formats = await processor.GetFormatsAsync();
        CollectionAssert.AreEquivalent(ImageUtilityFormats.All.Select(f => f.Id).ToArray(), formats.Select(f => f.Id).ToArray());
        var rows = new List<object>(); var failures = new List<string>();
        foreach (var format in formats)
        {
            try
            {
                var folder = Path.Combine(_root, format.Id); Directory.CreateDirectory(folder);
                // The bundled ART coder rejects its own 64x48 baseline output at EOF; 8x8 is a valid decoded fixture.
                var item = new ImageUtilityItem { Source = format.Id == "art" ? artSource : source, DisplayName = "результат.png" };
                var before = CollectionJournal.Read(_journal).Count();
                var checkpoint = 0; var preparedMarker = false; var notPublished = false; var notJournaled = false; string? pixelBefore = null;
                var output = await processor.ProcessAsync(item, new() { FormatOnly = true, MethodId = "real-esrgan", Format = format.Id }, folder,
                    checkpoint: () =>
                    {
                        checkpoint++;
                        var staged = Directory.GetFiles(folder, ".lopata-*.tmp").Single();
                        preparedMarker = CollectionAlternateOrigin.Read(staged)?.Scenario == "image-utility/conversion";
                        notPublished = !File.Exists(item.PlannedOutputPath);
                        notJournaled = CollectionJournal.Read(_journal).Count() == before;
                        pixelBefore = PixelDigestAsync(format.Id, staged).GetAwaiter().GetResult();
                    });
                Assert.AreEqual(1, checkpoint, format.Id);
                Assert.IsTrue(preparedMarker && notPublished && notJournaled, format.Id + " published or journaled before verified marker");
                Assert.AreEqual(pixelBefore, await PixelDigestAsync(format.Id, output), format.Id + " decoded pixels changed during publication");
                var info = await processor.InspectAsync(output);
                Assert.AreEqual(format.Id == "art" ? (8, 8) : (64, 48), (info.Width, info.Height), format.Id);
                var origin = CollectionOriginReader.Read(output);
                Assert.AreEqual("image-utility/conversion", origin?.Scenario, format.Id);
                Assert.IsTrue(origin!.RequiresContentValidation, format.Id);
                var receipt = CollectionJournal.Read(_journal).Single(r => r.Path == output);
                Assert.AreEqual(Hash(output), receipt.ContentSha256, true, format.Id);
                Assert.AreEqual(new FileInfo(output).Length, receipt.Length, format.Id);
                Assert.AreEqual("image-utility/conversion", CollectionJournal.TryResolveOrigin(output, _journal)?.Scenario, format.Id);
                var facts = Facts(output);
                Assert.IsEmpty(facts.Models, format.Id + " conversion falsely claims AI use");
                Assert.IsFalse(facts.Json().Replace("\\\\", "\\", StringComparison.Ordinal).Contains(_root, StringComparison.OrdinalIgnoreCase), format.Id);
                rows.Add(new { format.Id, info.Width, info.Height, receipt.Length, receipt.ContentSha256, pixelBefore, origin.Scenario });
            }
            catch (Exception error) { failures.Add(format.Id + ": " + error); }
        }
        var evidenceRoot = Path.Combine(AppContext.BaseDirectory, "image-utility-collection-evidence");
        Directory.CreateDirectory(evidenceRoot);
        var evidence = Path.Combine(evidenceRoot, Guid.NewGuid().ToString("N") + "-matrix.json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new { rows, failures }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddResultFile(evidence);
        Assert.IsEmpty(failures, string.Join("\n", failures));
        Assert.AreEqual(0, ai.Calls);
        Assert.AreEqual(formats.Count, CollectionJournal.Read(_journal).Count());
        Assert.AreEqual(original, Hash(source), "The input file was changed.");
        Assert.AreEqual(originalArt, Hash(artSource));
        Assert.IsFalse(CollectionJournal.Read(_journal).Any(r => r.Path.Contains(".tmp", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("png")]
    [DataRow("jpeg")]
    [DataRow("webp")]
    [DataRow("tiff")]
    public async Task NativeXmpRetainsHumanAuthorRightsAndDescription(string format)
    {
        var source = await SourceAsync(); var xmp = Path.Combine(_root, "human.xmp");
        const string author = "Автор 日本語", rights = "Copyright remains with the human", description = "Original human description";
        File.WriteAllText(xmp, "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:CreatorTool='Other producer'><dc:creator><rdf:Seq><rdf:li>" + author + "</rdf:li></rdf:Seq></dc:creator><dc:rights><rdf:Alt><rdf:li xml:lang='x-default'>" + rights + "</rdf:li></rdf:Alt></dc:rights><dc:description><rdf:Alt><rdf:li xml:lang='x-default'>" + description + "</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>");
        var tagged = Path.Combine(_root, "human-source.png");
        _ = await Run(Magick, source, "-profile", xmp, "-set", "Author", author, tagged); var before = Hash(tagged);
        var output = await new ImageUtilityProcessor(Magick).ProcessAsync(new() { Source = tagged, DisplayName = "human-source.png" },
            new() { FormatOnly = true, Format = format }, Path.Combine(_root, "output"));
        var xml = XDocument.Parse(await Run(ExifTool, "-b", "-XMP", output));
        XNamespace dc = "http://purl.org/dc/elements/1.1/", xp = "http://ns.adobe.com/xap/1.0/", lp = "https://lopata.app/ns/provenance/1.0/";
        Assert.AreEqual(author, xml.Descendants(dc + "creator").Single().Value);
        Assert.AreEqual(rights, xml.Descendants(dc + "rights").Single().Value);
        Assert.AreEqual(description, xml.Descendants(dc + "description").Single().Value);
        Assert.AreEqual("LOPATA", xml.Descendants(xp + "CreatorTool").Single().Value);
        var facts = JsonSerializer.Deserialize<OutputProvenance>(xml.Descendants(lp + "Provenance").Single().Value)!;
        Assert.AreEqual("LOPATA", facts.Program); Assert.AreEqual("image-utility/conversion", facts.Scenario);
        Assert.IsEmpty(facts.Models); Assert.AreEqual("prior-history-unknown", facts.ModelHistory);
        if (format is "png" or "tiff")
        {
            using var tags = JsonDocument.Parse(await Run(ExifTool, "-j", "-Software", output));
            Assert.AreEqual("LOPATA", tags.RootElement[0].GetProperty("Software").GetString());
        }
        Assert.AreEqual(before, Hash(tagged));
    }

    [TestMethod]
    [DataRow("real-esrgan", null, "realesrgan-x4plus")]
    [DataRow("real-cugan", null, "models-se")]
    [DataRow("real-esrgan", "realesrgan-x4plus-anime", "realesrgan-x4plus-anime")]
    public async Task AiFactsDescribeTheInvokedVariantAndKeepPriorHistoryUnknown(string method, string? selected, string expected)
    {
        var source = await SourceAsync(); var ai = new ControlledAi(); var options = new ImageUtilityOptions { MethodId = method, Preset = 360 };
        if (selected is not null) options.Parameters["model"] = selected;
        options.Parameters["private-path"] = source;
        options.Parameters["private-text"] = "Private text must not enter public provenance";
        var output = await new ImageUtilityProcessor(Magick, ai).ProcessAsync(new() { Source = source, DisplayName = "input.png" }, options, Path.Combine(_root, "output"));
        Assert.AreEqual(1, ai.Calls); Assert.AreEqual(method, ai.Method);
        Assert.AreEqual(expected, ai.Variant);
        var facts = Facts(output); Assert.HasCount(1, facts.Models);
        Assert.AreEqual(method + "/" + expected, facts.Models[0].Id); Assert.AreEqual(expected, facts.Models[0].Name);
        Assert.AreEqual("", facts.Models[0].Revision); Assert.Contains("prior-history-unknown", facts.ModelHistory);
        Assert.IsFalse(facts.Json().Contains("Private text", StringComparison.Ordinal));
        Assert.IsFalse(facts.Json().Replace("\\\\", "\\", StringComparison.Ordinal).Contains(_root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task FailedPublicationKeepsUserCollisionAndCreatesNoCompletedReceipt()
    {
        var source = await SourceAsync(); var folder = Path.Combine(_root, "output"); Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, "kept.png"); File.WriteAllText(existing, "user owned bytes");
        var item = new ImageUtilityItem { Source = source, DisplayName = "kept.png" };
        await Assert.ThrowsAsync<IOException>(() => new ImageUtilityProcessor(Magick).ProcessAsync(item,
            new() { FormatOnly = true, Format = "png" }, folder, checkpoint: () => throw new IOException("Injected before publication")));
        Assert.AreEqual("user owned bytes", File.ReadAllText(existing)); Assert.IsEmpty(CollectionJournal.Read(_journal));
        Assert.HasCount(1, Directory.GetFiles(folder)); Assert.IsFalse(File.Exists(item.PlannedOutputPath));
    }

    [TestMethod]
    public async Task AStrippedStreamUsesOnlyTheExactSuccessfulContentReceipt()
    {
        var source = await SourceAsync();
        var output = await new ImageUtilityProcessor(Magick).ProcessAsync(new() { Source = source, DisplayName = "plain.png" },
            new() { FormatOnly = true, Format = "qoi" }, Path.Combine(_root, "output"));
        var receipt = CollectionJournal.Read(_journal).Single();
        File.Delete(output + ":" + CollectionAlternateOrigin.StreamName);
        Assert.IsNull(CollectionOriginReader.Read(output));
        Assert.AreEqual("image-utility/conversion", CollectionJournal.TryResolveOrigin(output, _journal)?.Scenario);
        var bytes = File.ReadAllBytes(output); bytes[14] ^= 1; File.WriteAllBytes(output, bytes);
        File.SetLastWriteTimeUtc(output, new DateTime(receipt.ModifiedUtcTicks, DateTimeKind.Utc));
        File.SetCreationTimeUtc(output, new DateTime(receipt.CreatedUtcTicks, DateTimeKind.Utc));
        Assert.IsNull(CollectionJournal.TryResolveOrigin(output, _journal), "Unchanged path, length and timestamps cannot authorize changed bytes.");
    }

    [TestMethod]
    public async Task JournalFailureReportsTheWarningWhileTheVerifiedFileRemainsUsable()
    {
        var source = await SourceAsync(); var blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "owned fixture");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", blocked);
        var messages = new List<ImageUtilityProgress>();
        var output = await new ImageUtilityProcessor(Magick).ProcessAsync(new() { Source = source, DisplayName = "output.png" },
            new() { FormatOnly = true }, Path.Combine(_root, "output"), new RecordedProgress(messages));
        Assert.IsTrue(File.Exists(output)); Assert.AreEqual("image-utility/conversion", CollectionOriginReader.Read(output)?.Scenario);
        var warning = messages.Single(p => p.MessageKey == "ImageUtility.Event.CollectionNotRecorded");
        Assert.IsTrue(warning.Arguments!.Contains(Path.GetFileName(output)));
        Assert.IsFalse(messages.Any(p => p.MessageKey == "ImageUtility.Event.ProvenanceLocalOnly"));
        Assert.AreEqual("owned fixture", File.ReadAllText(blocked));
    }

    private async Task<string> SourceAsync()
    {
        var source = Path.Combine(_root, "source.png");
        _ = await Run(Magick, "-size", "64x48", "gradient:red-blue", "-depth", "8", source); return source;
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static OutputProvenance Facts(string path)
    {
        using var envelope = JsonDocument.Parse(File.ReadAllText(path + ":" + CollectionAlternateOrigin.StreamName));
        return JsonSerializer.Deserialize<OutputProvenance>(envelope.RootElement.GetProperty("provenance").GetRawText())!;
    }
    private async Task<string> PixelDigestAsync(string format, string path)
    {
        var bytes = await FileMetadataProcess.RunAsync(Magick, [format + ":" + path, "-coalesce", "-depth", "16", "rgba:-"], default);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static async Task<string> Run(string executable, params string[] arguments) =>
        Encoding.UTF8.GetString(await FileMetadataProcess.RunAsync(executable, arguments, default));
    private sealed class RecordedProgress(List<ImageUtilityProgress> messages) : IProgress<ImageUtilityProgress>
    { public void Report(ImageUtilityProgress value) => messages.Add(value); }
    private sealed class ControlledAi : IImageUtilityAiProcessor
    {
        internal int Calls; internal string? Method, Variant;
        public Task ProcessAsync(string methodId, string inputPath, string outputPngPath, IReadOnlyDictionary<string, string> parameters,
            IProgress<ImageUtilityProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Method = methodId;
            Variant = parameters.GetValueOrDefault("model") ?? ImageUtilityCatalog.RecommendedParameters(methodId).GetValueOrDefault("model");
            File.Copy(inputPath, outputPngPath); return Task.CompletedTask;
        }
    }
}
