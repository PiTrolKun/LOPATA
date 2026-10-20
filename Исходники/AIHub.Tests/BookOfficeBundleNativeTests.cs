using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.Books;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookOfficeBundleNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task ReleaseBundleConvertsThroughPrivateCacheWithoutDownloadOrProgramDirectoryWrites()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_OFFICE_BUNDLE_NATIVE") != "1")
            Assert.Inconclusive("Explicit Release bundled-office smoke test required; uses shipped local ZIP, no download.");
        var archive = Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime", BookOfficeBundle.FileName);
        Assert.IsTrue(File.Exists(archive), "Run this native smoke in Release, with the archive payload.");
        Assert.IsFalse(File.Exists(Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime", "program", "soffice.com")),
            "The test must exercise ZIP distribution, not an old expanded payload.");
        var folder = Path.Combine(AppDataPaths.ProjectRoot!, "_tmp", "office-bundle-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.docx");
        var book = new BookContent { Title = "Pinned office bundle smoke" };
        const string author = "Исходный русский текст — ёж, кавычки и цифры 3,14.";
        book.Parts.Add(new("section", "<p>" + BookMarkup.Escape(author) + "</p>"));
        BookConversionWriter.Write(book, source, "docx", new(), CancellationToken.None);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var gate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (ids, token) =>
        { token.ThrowIfCancellationRequested(); TestContext.WriteLine("Native smoke accepted existing components: " + string.Join(",", ids)); return Task.CompletedTask; };
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var executable = BookConverterRuntime.FindBundledLibreOffice(); Assert.IsNotNull(executable);
            Assert.IsTrue(executable.StartsWith(AppDataPaths.RuntimeDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var result = await new BookConversionService().ConvertAsync(new()
            { Sources = [source], OutputFormat = "odt", OutputDirectory = Path.Combine(folder, "out") }, cancellationToken: deadline.Token);
            Assert.AreEqual(0, result.Failures.Length, string.Join(";", result.Failures.Select(f => f.Message)));
            Assert.AreEqual(1, result.Outputs.Length);
            using var zip = ZipFile.OpenRead(result.Outputs[0]);
            using var text = new StreamReader(zip.GetEntry("content.xml")!.Open());
            StringAssert.Contains(await text.ReadToEndAsync(), author);
            using var metadata = new StreamReader(zip.GetEntry("meta.xml")!.Open());
            StringAssert.Contains(await metadata.ReadToEndAsync(), "LOPATA");
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            Assert.IsTrue(File.Exists(executable));
            Assert.IsFalse(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "BookOfficeRuntime", "program")), "Application directory remains read-only input.");
            TestContext.WriteLine("Validated native bundled-office ODT: " + result.Outputs[0]);
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
