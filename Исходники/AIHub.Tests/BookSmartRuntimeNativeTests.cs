using System.IO;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartRuntimeNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, DataRow("paddle"), DataRow("ovis"), TestCategory("Native")]
    public async Task PortableRuntimeIsPreparedFromTrustedArchivesAndLaunchesWithoutSharedPythonChanges(string choice)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_PREP_NATIVE") != "1")
            Assert.Inconclusive("Explicit native preparation opt-in is required; this check downloads pinned runtime archives.");
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var requested = new List<string>();
        ComponentLicenseGate.ConfirmAsync = (ids, token) =>
        {
            token.ThrowIfCancellationRequested(); requested.AddRange(ids);
            TestContext.WriteLine("Stand preparation license IDs: " + string.Join(", ", ids));
            return Task.CompletedTask; // The explicit stand opt-in owns this decision; no user receipts are written.
        };
        var sharedFiles = new[] { Path.Combine(ManagedPythonRuntime.CpuDirectory, "python.exe"), Path.Combine(ManagedPythonRuntime.CpuDirectory, "python312._pth") }
            .Where(File.Exists).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var runtime = new BookSmartOcrRuntime(key => key);
            var selection = new BookSmartSelection(choice);
            var result = await runtime.PrepareRuntimeAsync(selection, true, new LogProgress(TestContext), CancellationToken.None);
            Assert.IsTrue(result.All(item => item.Ready));
            Assert.IsTrue(File.Exists(BookSmartOcrRuntime.Python(selection)));
            var checkedAgain = await runtime.PrepareRuntimeAsync(selection, false, new LogProgress(TestContext), CancellationToken.None);
            Assert.IsTrue(checkedAgain.All(item => item.Ready));
            foreach (var file in sharedFiles) CollectionAssert.AreEqual(file.Value, SHA256.HashData(File.ReadAllBytes(file.Key)));
            TestContext.WriteLine("Portable runtime: " + BookSmartOcrRuntime.DirectoryFor(selection));
            TestContext.WriteLine("License requests: " + string.Join(", ", requested.Distinct()));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    private sealed class LogProgress(TestContext context) : IProgress<string>
    { public void Report(string value) => context.WriteLine(value); }
}
