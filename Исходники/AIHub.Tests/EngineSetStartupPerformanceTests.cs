using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using AIHub.Services;
using Lopata.Updates;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class EngineSetStartupPerformanceTests
{
    [TestMethod]
    public async Task MeasureActualInstalledSetsWithoutChangingUserSelection()
    {
        var phase = Environment.GetEnvironmentVariable("LOPATA_ENGINE_STARTUP_BENCH");
        if (phase is not ("before" or "after")) Assert.Inconclusive("Opt-in read-only installed engines benchmark.");
        var project = AppDataPaths.ProjectRoot!;
        var evidence = Path.Combine(project, "_tmp", "engine-startup-20261019");
        Directory.CreateDirectory(evidence);
        var state = await File.ReadAllBytesAsync(Path.Combine(AppDataPaths.BaseDirectory, "engine-sets.json"));
        var isolated = Path.Combine(evidence, "selection-" + phase + ".json");
        await File.WriteAllBytesAsync(isolated, state);
        using var stream = typeof(EngineSetService).Assembly.GetManifestResourceStream("AIHub.EngineSets.Catalog")!;
        var catalog = JsonSerializer.Deserialize<EngineSetCatalog>(stream, EngineSetCatalog.JsonOptions)!;
        using var http = new HttpClient(new NoNetwork());
        var service = new EngineSetService(catalog, UpdateReleaseKeys.Trusted, isolated,
            Path.Combine(AppDataPaths.RuntimeDirectory, "EngineSets"),
            Path.Combine(project, "Исходники", "AIHub", "bin", "Debug", "net10.0-windows", "EngineSets"), http);
        var timings = new List<object>();
        await Sta(async () =>
        {
            var timer = Stopwatch.StartNew();
            await service.InitializeAsync();
            timings.Add(new { stage = "initializeActive", elapsedMs = timer.ElapsedMilliseconds });
            timer.Restart();
            var installed = await service.GetInstalledAsync();
            timings.Add(new { stage = "inventoryAll", elapsedMs = timer.ElapsedMilliseconds, installed });
            timer.Restart();
            await service.GetOfferAsync();
            timings.Add(new { stage = "offer", elapsedMs = timer.ElapsedMilliseconds });
            Assert.IsTrue(installed.Contains(service.GetState().ActiveId));
        });
        CollectionAssert.AreEqual(state, await File.ReadAllBytesAsync(Path.Combine(AppDataPaths.BaseDirectory, "engine-sets.json")));
        await File.WriteAllTextAsync(Path.Combine(evidence, phase + ".json"), JsonSerializer.Serialize(new
        {
            phase, active = service.GetState().ActiveId, timings,
            sets = catalog.Sets.Select(s => new { s.Id, files = s.VerifiedManifest.Files.Length, bytes = s.InstalledBytes }),
            userSelectionUnchanged = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Startup measurement must not download anything.");
    }

    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
    }
}
