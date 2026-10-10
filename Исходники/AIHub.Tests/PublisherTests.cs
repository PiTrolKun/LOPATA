using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PublisherTests
{
    private static PublisherRelease Release(long id) => new(id, "v" + id, "Release " + id,
        "# Changes\n\n**Русский текст** and [details](https://example.org/details)", DateTimeOffset.UtcNow.AddDays(id - 100), "https://github.com/owner/repo/releases/tag/v" + id);
    private static PublisherDirection Direction() => new() { Repository = "owner/repo", CommunityId = 42, CommunityName = "Community" };
    private const string Secret = "FAKE_TEST_TOKEN_NOT_A_REAL_SECRET";

    [TestMethod]
    public void RepositoryAndCommunityValidationRejectsUnsafeAddresses()
    {
        Assert.AreEqual("owner/repo", PublisherGitHubClient.ParseRepository("https://github.com/owner/repo.git/"));
        Assert.AreEqual("PiTrolKun/LOPATA", PublisherGitHubClient.ParseRepository("https://github.com/PiTrolKun/LOPATA/releases"));
        Assert.AreEqual("owner/repo", PublisherGitHubClient.ParseRepository("https://github.com/owner/repo/releases/"));
        foreach (var url in new[] { "http://github.com/owner/repo", "https://github.com.evil/owner/repo", "https://user@github.com/owner/repo", "https://github.com/owner/repo?token=secret", "https://github.com/owner/repo/issues",
            "https://github.com/owner/repo/releases/tag/v1", "https://github.com/owner/repo/releases?token=secret", "https://github.com/owner/repo/releases#fragment" })
            Assert.ThrowsExactly<PublisherException>(() => PublisherGitHubClient.ParseRepository(url));
        Assert.AreEqual("42", PublisherVkClient.ParseCommunity("https://vk.ru/club42"));
        Assert.ThrowsExactly<PublisherException>(() => PublisherVkClient.ParseCommunity("https://vk.ru/club42?act=tokens"));
    }
    [TestMethod]
    public void FormatterPreservesLinkAndSurrogatesWhenShortening()
    {
        var release = Release(1) with { Body = string.Concat(Enumerable.Repeat("🎵", 8000)) };
        var text = PublisherPostFormatter.Format("owner/repo", release, "Сокращено");
        Assert.IsTrue(text.Length <= PublisherPostFormatter.MaximumCharacters);
        Assert.IsTrue(text.EndsWith(release.Url, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Сокращено", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("\uFFFD", StringComparison.Ordinal));
    }
    [TestMethod]
    public void DpapiRoundTripAndEncryptedStorage()
    {
        var encrypted = PublisherSecretProtection.Protect(Secret);
        Assert.IsFalse(encrypted.Contains(Secret, StringComparison.Ordinal));
        Assert.AreEqual(Secret, PublisherSecretProtection.Unprotect(encrypted));
        var corrupted = Convert.FromBase64String(encrypted); corrupted[^1] ^= 0xff;
        Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(() => PublisherSecretProtection.Unprotect(Convert.ToBase64String(corrupted)));
    }
    [TestMethod]
    public async Task GitHubReadsEveryPageIncludingBetaAndSkipsDrafts()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var rows = Enumerable.Range((calls - 1) * 100 + 1, calls == 1 ? 100 : 2).Select(i => new
            { id = i, tag_name = "v" + i + "-beta", name = "Release", body = "Changes", draft = i == 102,
                prerelease = true, published_at = DateTimeOffset.UtcNow, html_url = "https://github.com/owner/repo/releases/tag/v" + i });
            return Json(rows);
        }));
        var result = await new PublisherGitHubClient(http).ReadAsync("owner/repo", CancellationToken.None);
        Assert.AreEqual(2, calls); Assert.HasCount(101, result);
    }
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ResponseBodyIsBoundedAndCallerCancellationIsPreserved(bool vk, bool cancelCaller)
    {
        using var content = new WaitingContent();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = content }))
        { Timeout = TimeSpan.FromMilliseconds(150) };
        using var caller = new CancellationTokenSource();
        if (cancelCaller) caller.CancelAfter(TimeSpan.FromMilliseconds(50));
        Task operation = vk
            ? new PublisherVkClient(http).CheckAsync("42", Secret, caller.Token)
            : new PublisherGitHubClient(http).ReadAsync("owner/repo", caller.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(content.Started);
        Assert.AreEqual(cancelCaller, caller.IsCancellationRequested);
    }
    [TestMethod]
    public async Task VkPostsAsCommunityAndNeverEchoesTokenFromErrors()
    {
        string? sent = null;
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            Assert.AreEqual("https://api.vk.com/method/wall.post", request.RequestUri!.AbsoluteUri);
            sent = await request.Content!.ReadAsStringAsync();
            return Json(new { error = new { error_code = 27, error_msg = Secret, request_params = new { access_token = Secret } } });
        }));
        var error = await Assert.ThrowsExactlyAsync<PublisherException>(() => new PublisherVkClient(http).PostAsync(42, Secret, "Post", "guid", CancellationToken.None));
        Assert.IsFalse(error.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.IsTrue(sent!.Contains("from_group=1", StringComparison.Ordinal));
        Assert.IsTrue(sent.Contains("owner_id=-42", StringComparison.Ordinal));
    }
    [TestMethod]
    public async Task InitialHistoryIsNotPostedAndNewReleaseIsDeduplicated()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk(); var d = Direction(); d.AutoPublish = true;
        try
        {
            await using var coordinator = Create(directory, github, vk);
            await coordinator.AddAsync(d, Secret); await coordinator.TickAsync(DateTimeOffset.UtcNow, "Full release");
            Assert.AreEqual(0, vk.Posts);
            github.Releases.Add(Release(2)); await coordinator.ConfigureAsync(d.Id, true, 15);
            await coordinator.TickAsync(DateTimeOffset.UtcNow.AddMinutes(1), "Full release");
            await coordinator.TickAsync(DateTimeOffset.UtcNow.AddHours(2), "Full release");
            Assert.AreEqual(1, vk.Posts); Assert.AreEqual(2L, coordinator.Snapshot().Directions[0].Publications[0].ReleaseId);
            Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "directions.json")).Contains(Secret, StringComparison.Ordinal));
        }
        finally { Remove(directory); }
    }
    [TestMethod]
    public async Task SuccessfulPostHistorySurvivesRestartAndBlocksRepeat()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk(); var d = Direction();
        try
        {
            await using (var first = Create(directory, github, vk))
            { await first.AddAsync(d, Secret); await first.PublishAsync(d.Id, 1, "Full release"); }
            await using var resumed = Create(directory, github, vk);
            await Assert.ThrowsExactlyAsync<PublisherException>(() => resumed.PublishAsync(d.Id, 1, "Full release"));
            Assert.AreEqual(1, vk.Posts); Assert.AreEqual(101L, resumed.Snapshot().Directions[0].Publications[0].PostId);
        }
        finally { Remove(directory); }
    }
    [TestMethod]
    public async Task LostResponsePersistsUncertainAndNeverAutomaticallyResends()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk { LoseResponse = true }; var d = Direction();
        try
        {
            await using (var first = Create(directory, github, vk))
            { await first.AddAsync(d, Secret); await first.EnqueueAsync(d.Id, [1], 24); await first.TickAsync(DateTimeOffset.UtcNow, "Full release"); }
            await using var resumed = Create(directory, github, vk);
            Assert.AreEqual("uncertain", resumed.Snapshot().Directions[0].Publications[0].Status);
            await resumed.TickAsync(DateTimeOffset.UtcNow.AddDays(10), "Full release");
            await Assert.ThrowsExactlyAsync<PublisherException>(() => resumed.PublishAsync(d.Id, 1, "Full release"));
            Assert.AreEqual(1, vk.Posts);
        }
        finally { Remove(directory); }
    }
    [TestMethod]
    public async Task HistoricalQueuePostsOneItemAfterDowntimeAndDeletionKeepsOtherRoute()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1), Release(2), Release(3)] }; var vk = new FakeVk(); var d = Direction();
        try
        {
            await using var coordinator = Create(directory, github, vk); await coordinator.AddAsync(d, Secret);
            var other = Direction(); other.CommunityId = 43; await coordinator.AddAsync(other, Secret);
            await coordinator.EnqueueAsync(d.Id, [3, 1, 2], 24);
            await coordinator.TickAsync(DateTimeOffset.UtcNow.AddDays(30), "Full release");
            Assert.AreEqual(1, vk.Posts); Assert.AreEqual(1L, coordinator.Snapshot().Directions[0].Publications[0].ReleaseId);
            // The next scheduled post is based on actual send time, not accumulated missed slots.
            await coordinator.TickAsync(DateTimeOffset.UtcNow.AddMinutes(1), "Full release"); Assert.AreEqual(1, vk.Posts);
            await coordinator.DeleteAsync(d.Id); Assert.AreEqual(other.Id, coordinator.Snapshot().Directions.Single().Id);
        }
        finally { Remove(directory); }
    }
    [TestMethod]
    public void CorruptHistoryIsNotSilentlyReset()
    {
        var directory = Temp();
        try
        {
            File.WriteAllText(Path.Combine(directory, "directions.json"), "{broken");
            using var store = new PublisherStore(directory);
            Assert.ThrowsExactly<PublisherException>(() => store.Load());
            Assert.AreEqual("{broken", File.ReadAllText(Path.Combine(directory, "directions.json")));
        }
        finally { Remove(directory); }
    }
    private static PublisherCoordinator Create(string path, FakeGitHub github, FakeVk vk) => new(new PublisherStore(path), github, vk);

    [TestMethod]
    public async Task VkReadCheckRequiresWallPermissionAndTheCorrectCommunity()
    {
        foreach (var ownId in new[] { 42, 99 })
        {
            using var http = new HttpClient(new AsyncHandler(async request =>
            {
                var form = await request.Content!.ReadAsStringAsync();
                if (request.RequestUri!.AbsolutePath.EndsWith("groups.getTokenPermissions", StringComparison.Ordinal))
                    return Json(new { response = new { permissions = new[] { new { name = "wall", setting = 1 } } } });
                Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("groups.getById", StringComparison.Ordinal));
                return Json(new { response = new { groups = new[] { new { id = form.Contains("group_ids", StringComparison.Ordinal) ? 42 : ownId, name = "Community" } } } });
            }));
            var client = new PublisherVkClient(http);
            if (ownId == 42) Assert.AreEqual(42L, (await client.CheckAsync("42", Secret, CancellationToken.None)).Id);
            else Assert.AreEqual("Publisher.CommunityTokenMismatch", (await Assert.ThrowsExactlyAsync<PublisherException>(() => client.CheckAsync("42", Secret, CancellationToken.None))).Key);
        }
        foreach (var admin in new[] { 0, 1 })
        {
            using var http = new HttpClient(new Handler(request =>
            {
                var method = request.RequestUri!.AbsolutePath;
                if (method.EndsWith("groups.getTokenPermissions", StringComparison.Ordinal))
                    return Json(new { error = new { error_code = 27 } });
                if (method.EndsWith("account.getAppPermissions", StringComparison.Ordinal)) return Json(new { response = 8192 });
                return Json(new { response = new { groups = new[] { new { id = 42, name = "Community", is_admin = admin } } } });
            }));
            var client = new PublisherVkClient(http);
            if (admin == 1) Assert.AreEqual(42L, (await client.CheckAsync("42", Secret, CancellationToken.None)).Id);
            else Assert.AreEqual("Publisher.VkPermission", (await Assert.ThrowsExactlyAsync<PublisherException>(() => client.CheckAsync("42", Secret, CancellationToken.None))).Key);
        }
    }

    [TestMethod]
    public async Task DisablingAutomaticModeKeepsExplicitHistoricalQueue()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk(); var d = Direction(); d.AutoPublish = true;
        try
        {
            await using var coordinator = Create(directory, github, vk); await coordinator.AddAsync(d, Secret);
            await coordinator.EnqueueAsync(d.Id, [1], 24); github.Releases.Add(Release(2)); await coordinator.CheckAsync(d.Id);
            Assert.HasCount(2, coordinator.Snapshot().Directions[0].Queue);
            await coordinator.ConfigureAsync(d.Id, false, 60);
            CollectionAssert.AreEqual(new List<long> { 1 }, coordinator.Snapshot().Directions[0].Queue);
            await coordinator.TickAsync(DateTimeOffset.UtcNow, "Full release"); Assert.AreEqual(1, vk.Posts);
        }
        finally { Remove(directory); }
    }

    [TestMethod]
    public async Task PersistedInFlightRequestRequiresReviewAfterRestart()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk(); var d = Direction();
        try
        {
            await using (var first = Create(directory, github, vk)) { await first.AddAsync(d, Secret); await first.EnqueueAsync(d.Id, [1], 0); }
            using (var store = new PublisherStore(directory))
            { var state = store.Load(); state.Directions[0].Publications.Add(new() { ReleaseId = 1, Status = "sending" }); store.Save(state); }
            await using var resumed = Create(directory, github, vk);
            await resumed.TickAsync(DateTimeOffset.UtcNow.AddDays(1), "Full release");
            Assert.AreEqual(0, vk.Posts); Assert.AreEqual("uncertain", resumed.Snapshot().Directions[0].Publications[0].Status);
            await resumed.ResolveAsync(d.Id, 1, 900);
            await resumed.TickAsync(DateTimeOffset.UtcNow.AddDays(2), "Full release"); Assert.AreEqual(0, vk.Posts);
        }
        finally { Remove(directory); }
    }

    [TestMethod]
    public async Task DamagedLocalSecretPausesOnlyItsRouteWithoutPosting()
    {
        var directory = Temp(); var github = new FakeGitHub { Releases = [Release(1)] }; var vk = new FakeVk(); var d = Direction(); var other = Direction(); other.CommunityId = 43;
        try
        {
            await using (var first = Create(directory, github, vk))
            { await first.AddAsync(d, Secret); await first.AddAsync(other, Secret); await first.EnqueueAsync(d.Id, [1], 0); await first.EnqueueAsync(other.Id, [1], 0); }
            using (var store = new PublisherStore(directory))
            { var state = store.Load(); state.Directions[0].ProtectedToken = "invalid-base64"; store.Save(state); }
            await using var resumed = Create(directory, github, vk); await resumed.TickAsync(DateTimeOffset.UtcNow, "Full release");
            Assert.AreEqual(1, vk.Posts); Assert.IsTrue(resumed.Snapshot().Directions[0].QueuePaused);
            Assert.AreEqual("Publisher.SecretUnavailable", resumed.Snapshot().Directions[0].StatusKey);
            Assert.HasCount(0, resumed.Snapshot().Directions[0].Publications);
        }
        finally { Remove(directory); }
    }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "lopata-publisher-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Remove(string path)
    { var full = Path.GetFullPath(path); if (full.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("lopata-publisher-test-", StringComparison.Ordinal)) Directory.Delete(full, true); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(action(request)); }
    private sealed class WaitingContent : HttpContent
    {
        public bool Started { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("A cancellation token is required for the response body.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        { Started = true; return Task.Delay(Timeout.Infinite, token); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request); }
    private sealed class FakeGitHub : IPublisherGitHubClient
    { public List<PublisherRelease> Releases { get; set; } = []; public Task<List<PublisherRelease>> ReadAsync(string repo, CancellationToken token) => Task.FromResult(Releases.ToList()); }
    private sealed class FakeVk : IPublisherVkClient
    {
        public int Posts { get; private set; }
        public bool LoseResponse { get; set; }
        public Task<(long Id, string Name)> CheckAsync(string address, string secret, CancellationToken token) => Task.FromResult((42L, "Community"));
        public Task<long> PostAsync(long community, string secret, string message, string guid, CancellationToken token)
        { Posts++; if (LoseResponse) throw new HttpRequestException("Lost response"); return Task.FromResult(100L + Posts); }
    }
}
