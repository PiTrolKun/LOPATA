using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderSuiteTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task SuppliedRussianEnglishCorpusOpensAndRendersFirstAndLastPages()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_SUITE") != "1") Assert.Inconclusive("Explicit supplied-corpus run required.");
        var evidence = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107");
        var root = Path.Combine(evidence, "user-suite", "AI_Office_TestSuite_RU_EN");
        var output = Path.Combine(evidence, "suite-pages"); Directory.CreateDirectory(output);
        var files = Directory.EnumerateDirectories(root).Where(p => char.IsAsciiDigit(Path.GetFileName(p)[0]))
            .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
            .Where(p => !Path.GetFileName(p).Equals("epub_source.md", StringComparison.Ordinal)).Order().ToArray();
        Assert.HasCount(56, files);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var results = new List<object>(); var failures = new List<string>();
        // Exercise the shared first-full / subsequent-presence audit policy in
        // isolation, without changing the user's saved audit setting.
        using var auditStore = ComponentAudit.UseTestStore(Path.Combine(evidence, "suite-audit"), false);
        using var audit = ComponentAudit.CaptureOperation();
        try
        {
            foreach (var source in files)
            {
                var name = Path.GetRelativePath(root, source); var hash = SHA256.HashData(File.ReadAllBytes(source));
                var start = DateTime.UtcNow;
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    await using var document = await ReaderPreparation.OpenAsync(source, null, limit.Token);
                    Assert.IsGreaterThan(0, document.Pages.Count);
                    foreach (var page in new[] { 0, document.Pages.Count - 1 }.Distinct())
                    {
                        var file = await document.Backend.RenderAsync(page, 850, limit.Token);
                        Assert.IsGreaterThan(100, new FileInfo(file).Length);
                        File.Copy(file, Path.Combine(output, name.Replace(Path.DirectorySeparatorChar, '_') + "." + page + ".png"), true);
                    }
                    CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source)));
                    results.Add(new { file = name, status = "PASS_RENDER", pages = document.Pages.Count, seconds = (DateTime.UtcNow - start).TotalSeconds });
                }
                catch (Exception error)
                { failures.Add(name + ": " + error.Message); results.Add(new { file = name, status = "FAIL", error = error.ToString() }); }
                File.WriteAllText(Path.Combine(evidence, "suite-result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.WriteLine(name + " · " + results.Count + "/56");
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }
}
