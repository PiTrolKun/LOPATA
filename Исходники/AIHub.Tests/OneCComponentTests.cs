using System.IO;
using AIHub.Services;
using AIHub.Services.OneC;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OneCComponentTests
{
    [TestMethod] public async Task ManagedCheckerInstallsFromVerifiedCacheAndChecksRealCode()
    {
        var archive = Environment.GetEnvironmentVariable("LOPATA_ONEC_BSL_ZIP");
        if (string.IsNullOrEmpty(archive)) Assert.Inconclusive("Set LOPATA_ONEC_BSL_ZIP for managed component installation.");
        var entry = ComponentCatalog.Find(OneCBslRuntime.ComponentId)!; Directory.CreateDirectory(AppDataPaths.ComponentDownloadsDirectory);
        var cache = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
        if (!File.Exists(cache)) File.Copy(archive!, cache);
        Assert.AreEqual(entry.Sha256, OneCProjectStore.HashFile(cache));
        var runtime = new OneCBslRuntime(); using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await runtime.PrepareAsync(true, null, cancel.Token); Assert.IsTrue(runtime.Present);
        var check = await new OneCBslChecker(runtime).CheckAsync("Функция Сумма(А, Б) Экспорт\n    Возврат А + Б;\nКонецФункции", "Module.bsl", "ru", cancel.Token);
        Assert.IsFalse(check.Diagnostics.Any(d => d.Severity == "Error")); Assert.AreEqual("1.0.7", check.Version);
    }
    [TestMethod] public async Task ActualCheckerHonorsFirstOffOnMissingAndReinstallAudit()
    {
        var executable = Environment.GetEnvironmentVariable("LOPATA_ONEC_BSL_EXE");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set LOPATA_ONEC_BSL_EXE for actual checker audit coverage.");
        var source = Path.GetDirectoryName(executable)!;
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Audit-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime"); var bundle = Path.Combine(runtime, "bsl-language-server");
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(bundle, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file, to);
            }
            using var isolated = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
            var checker = new OneCBslRuntime(runtime); Assert.IsTrue(checker.Present);
            await checker.AuditAsync(default); // First OFF performs complete hashes and real version launch.
            var release = Path.Combine(bundle, "runtime", "release");
            await File.AppendAllTextAsync(release, "\n# changed by isolated audit test\n");
            await checker.AuditAsync(default); // Repeat OFF intentionally checks presence only.
            using (ComponentAudit.ForceFull()) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => checker.AuditAsync(default));
            File.Copy(Path.Combine(source, "runtime", "release"), release, true);
            ComponentAudit.Invalidate(runtime); await checker.AuditAsync(default); // Reinstallation invalidates proof.
            var jar = Path.Combine(bundle, "app", "bsl-language-server-1.0.7-exec.jar"); File.Delete(jar);
            Assert.IsFalse(checker.Present); await Assert.ThrowsExactlyAsync<InvalidDataException>(() => checker.AuditAsync(default));
            File.Copy(Path.Combine(source, "app", Path.GetFileName(jar)), jar);
            var jvm = Path.Combine(bundle, "runtime", "bin", "server", "jvm.dll"); File.Delete(jvm);
            Assert.IsFalse(checker.Present); await Assert.ThrowsExactlyAsync<InvalidDataException>(() => checker.AuditAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod] public async Task DeclinedComponentLicenseDoesNotStartTheChecker()
    {
        var previous = ComponentLicenseGate.ConfirmAsync; var requested = new List<string>();
        try
        {
            ComponentLicenseGate.ConfirmAsync = (ids, _) => { requested.AddRange(ids); throw new OperationCanceledException(); };
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new OneCBslRuntime().PrepareAsync(false, null, default));
            CollectionAssert.Contains(requested, OneCBslRuntime.ComponentId); CollectionAssert.Contains(requested, "nuget.AvalonEdit");
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}
