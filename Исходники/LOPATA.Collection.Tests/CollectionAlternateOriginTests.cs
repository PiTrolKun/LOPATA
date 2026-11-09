using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lopata.Collection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Collection.Tests;

[TestClass]
public sealed class CollectionAlternateOriginTests
{
    private const string Provenance = "{\"Schema\":1,\"Program\":\"LOPATA\",\"Scenario\":\"images/utility\",\"ProgramVersion\":\"test\"}";
    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [TestMethod]
    public void AlternateStreamPreservesPrimaryBytesAndSurvivesRenameAndCopy()
    {
        using var fixture = new Fixture();
        var first = fixture.File("first.qoi");
        var bytes = Encoding.UTF8.GetBytes("A primary image payload whose bytes must stay identical.");
        File.WriteAllBytes(first, bytes);
        RequireStreams(CollectionAlternateOrigin.Write(first, Provenance, Digest(first)));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(first));
        var moved = fixture.File("renamed.qoi");
        File.Move(first, moved);
        Assert.AreEqual("images/utility", CollectionOriginReader.Read(moved)?.Scenario);
        var copied = fixture.File("copied.qoi");
        File.Copy(moved, copied);
        Assert.AreEqual("images/utility", CollectionOriginReader.Read(copied)?.Scenario);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(copied));
    }

    [TestMethod]
    public void ChangedPrimaryCannotReuseAlternateStreamOrScannerCacheWithRestoredStats()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("original"));
        RequireStreams(CollectionAlternateOrigin.Write(path, Provenance, Digest(path)));
        var modified = File.GetLastWriteTimeUtc(path); var created = File.GetCreationTimeUtc(path);
        using var scanner = new CollectionScanner();
        Assert.AreEqual(1, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("replaced"));
        File.SetCreationTimeUtc(path, created); File.SetLastWriteTimeUtc(path, modified);
        Assert.IsNull(CollectionOriginReader.Read(path));
        Assert.AreEqual(0, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
    }

    [TestMethod]
    public void CopiedStreamDoesNotMarkForeignReplacement()
    {
        using var fixture = new Fixture();
        var source = fixture.File("source.farbfeld");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        RequireStreams(CollectionAlternateOrigin.Write(source, Provenance, Digest(source)));
        var copy = fixture.File("replacement.farbfeld");
        File.Copy(source, copy); File.WriteAllBytes(copy, [4, 3, 2, 1]);
        Assert.IsNull(CollectionOriginReader.Read(copy));
    }

    [TestMethod]
    public void WriterRejectsMissingSchemaForeignProgramAndOversizeMetadata()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi"); File.WriteAllBytes(path, [1, 2, 3]);
        var digest = Digest(path);
        foreach (var invalid in new[] { "{\"Program\":\"LOPATA\"}", "{\"Schema\":1,\"Program\":\"Other\"}",
            "{\"Schema\":2,\"Program\":\"LOPATA\"}", new string('x', CollectionAlternateOrigin.MaximumBytes + 1) })
            Assert.Throws<InvalidDataException>(() => CollectionAlternateOrigin.Write(path, invalid, digest));
        Assert.Throws<ArgumentException>(() => CollectionAlternateOrigin.Write(path, Provenance, "not-a-digest"));
        Assert.AreEqual(digest, Digest(path));
        Assert.IsNull(CollectionAlternateOrigin.Read(path));
    }

    [TestMethod]
    public void ReaderRejectsMalformedEnvelopeShapesAndOversizedStreams()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi"); File.WriteAllBytes(path, [1, 2, 3]);
        RequireStreams(CollectionAlternateOrigin.Write(path, Provenance, Digest(path)));
        var stream = path + ":" + CollectionAlternateOrigin.StreamName;
        foreach (var invalid in new[] { "{\"schema\":\"1\"}", "{\"schema\":1,\"length\":\"3\"}",
            "{\"schema\":1,\"length\":3,\"sha256\":0}", new string('x', CollectionAlternateOrigin.MaximumBytes + 1) })
        {
            File.WriteAllText(stream, invalid);
            Assert.IsNull(CollectionAlternateOrigin.Read(path));
        }
    }

    [TestMethod]
    public void JournalDigestFallbackAdmitsExactCompletedOutputWithoutAlternateStreams()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi"); File.WriteAllBytes(path, [7, 8, 9]);
        Assert.IsNull(CollectionOriginReader.Read(path));
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, Digest(path)));
        Assert.AreEqual("images/utility", CollectionJournal.TryResolveOrigin(path, fixture.Root)?.Scenario);
        using var scanner = new CollectionScanner();
        Assert.AreEqual(1, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
    }

    [TestMethod]
    public void JournalAddressAloneWrongDigestAndChangedStatsDoNotAdmitOutput()
    {
        using var fixture = new Fixture();
        var path = fixture.File("LOPATA.qoi"); File.WriteAllBytes(path, [7, 8, 9]);
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root));
        Assert.IsNull(CollectionJournal.TryResolveOrigin(path, fixture.Root));
        Assert.IsFalse(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, "invalid"));
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, new string('0', 64)));
        Assert.IsNull(CollectionJournal.TryResolveOrigin(path, fixture.Root));
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, Digest(path)));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        Assert.IsNull(CollectionJournal.TryResolveOrigin(path, fixture.Root));
    }

    [TestMethod]
    public void HashReceiptIsRevalidatedAfterSameLengthReplacementAndRestoredStats()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi"); File.WriteAllBytes(path, [1, 2, 3]);
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, Digest(path)));
        using var scanner = new CollectionScanner();
        Assert.AreEqual(1, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
        var created = File.GetCreationTimeUtc(path); var modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, [3, 2, 1]); File.SetCreationTimeUtc(path, created); File.SetLastWriteTimeUtc(path, modified);
        Assert.IsNull(CollectionJournal.TryResolveOrigin(path, fixture.Root));
        Assert.AreEqual(0, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
    }

    [TestMethod]
    public void PublicationUsesOnlyExplicitJournalProfileAndRejectsStaleReceipt()
    {
        using var fixture = new Fixture();
        var path = fixture.File("image.qoi"); File.WriteAllBytes(path, [1, 2, 3]);
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "images/utility", fixture.Root, Digest(path)));
        CollectionItem[] candidate = [new(path, "Images", "Images")];
        Assert.AreEqual(0, CollectionPublication.MergeValidated(fixture.Config, [], candidate).Count);
        Assert.AreEqual(0, CollectionPublication.MergeValidated(fixture.Config, [], candidate, Path.Combine(fixture.Root, "other-profile")).Count);
        Assert.AreEqual(1, CollectionPublication.MergeValidated(fixture.Config, [], candidate, fixture.Root).Count);
        File.WriteAllBytes(path, [3, 2, 1]);
        Assert.AreEqual(0, CollectionPublication.MergeValidated(fixture.Config, [], candidate, fixture.Root).Count);
    }

    [TestMethod]
    public void PreparedOutputIsInvisibleUntilCommittedDespiteValidAlternateProvenanceAndReceipt()
    {
        using var fixture = new Fixture();
        var prepared = fixture.File(".lopata-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(prepared, [1, 2, 3]);
        RequireStreams(CollectionAlternateOrigin.Write(prepared, Provenance, Digest(prepared)));
        Assert.AreEqual("images/utility", CollectionOriginReader.Read(prepared)?.Scenario);
        Assert.IsTrue(CollectionJournal.RecordCompleted(prepared, "images/utility", fixture.Root, Digest(prepared)));
        using var scanner = new CollectionScanner();
        Assert.AreEqual(0, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
        Assert.AreEqual(0, CollectionPublication.MergeValidated(fixture.Config, [], [new(prepared, "Images", "Images")], fixture.Root).Count);
        var committed = fixture.File("finished.qoi");
        File.Move(prepared, committed);
        var result = scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(committed, result.Items[0].Path);
    }

    [TestMethod]
    public void OrdinaryTemporaryAndSimilarNamesRemainEligible()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "report.tmp", ".lopata-0123456789abcdef0123456789abcdef.txt",
            ".lopata-0123456789abcdef0123456789abcde.tmp", ".lopata-0123456789abcdef0123456789abcdeg.tmp" })
        {
            var path = fixture.File(name); File.WriteAllBytes(path, [1, 2, 3]);
            RequireStreams(CollectionAlternateOrigin.Write(path, Provenance, Digest(path)));
            Assert.IsTrue(CollectionFilePolicy.IsAllowed(path, fixture.Config));
        }
        using var scanner = new CollectionScanner();
        Assert.AreEqual(4, scanner.Scan(fixture.Root, fixture.Config, CancellationToken.None).Items.Count);
    }

    private static void RequireStreams(bool written)
    {
        if (!written) Assert.Inconclusive("The isolated test volume does not support alternate streams.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LopataAlternateOriginTests", Guid.NewGuid().ToString("N"));
        public string Corpus => Path.Combine(Root, "corpus");
        public CollectionConfig Config => new() { Roots = [Corpus], SearchEnabled = false, Language = "en" };
        public Fixture() => Directory.CreateDirectory(Corpus);
        public string File(string name) => Path.Combine(Corpus, name);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
