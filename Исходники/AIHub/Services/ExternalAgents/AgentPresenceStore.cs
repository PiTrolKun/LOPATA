using System.IO;
using System.Text.Json;
using Lopata.Island;

namespace AIHub.Services.ExternalAgents;

internal sealed record AgentWork(AgentIdentity Client, string Tool);
internal sealed record AgentWorkItem(AgentWork Work, DateTime Updated, bool Running);

/// <summary>One short-lived file per operation. Windows releases the writer's lock even if the caller crashes.</summary>
internal sealed class AgentPresenceStore(string directory)
{
    internal static readonly AgentPresenceStore Current = new(Path.Combine(AppDataPaths.BaseDirectory,
        "ExternalAgents", "Activity", IslandLink.InstallationName(AppContext.BaseDirectory)));
    internal string DirectoryPath => directory;
    internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);
    private long _nextCleanup;

    internal void Remember(AgentIdentity client)
    {
        if (AgentClients.Match(client.Name) is not { } product) return;
        try
        {
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, product.Id + ".connected");
            if (!File.Exists(marker))
                using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal bool HasConnected(string id) => AgentClients.Products.Any(p => p.Id == id)
        && File.Exists(Path.Combine(directory, id + ".connected"));

    internal IDisposable? Begin(AgentIdentity client, string tool)
    {
        client = AgentIdentity.FromName(client.Name) with { Icon = client.Icon is { Length: <= AgentIdentity.MaxIconChars } ? client.Icon : null };
        FileStream? writer = null;
        try
        {
            Directory.CreateDirectory(directory);
            var now = Environment.TickCount64;
            if (now >= Interlocked.Read(ref _nextCleanup))
            {
                Interlocked.Exchange(ref _nextCleanup, now + 60_000);
                foreach (var file in Directory.EnumerateFiles(directory, "*.activity"))
                {
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Linger && !IsHeld(file)) File.Delete(file); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".activity");
            writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(writer, new AgentWork(client, tool), AgentProtocol.Json);
            writer.Flush();
            return new Lease(writer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            writer?.Dispose();
            return null;
        }
    }

    private sealed class Lease(FileStream stream) : IDisposable
    {
        private int _closed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try
            {
                // Update the modification time at completion, including after a long download.
                stream.WriteByte((byte)' ');
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { stream.Dispose(); }
                catch (IOException) { }
            }
        }
    }

    private static bool IsHeld(string path)
    {
        try
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return false;
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
    }

    internal List<AgentWorkItem> Read()
    {
        var items = new List<AgentWorkItem>();
        if (!Directory.Exists(directory)) return items;
        foreach (var path in Directory.GetFiles(directory, "*.activity"))
        {
            try
            {
                var running = IsHeld(path);
                var updated = File.GetLastWriteTimeUtc(path);
                if (!running && DateTime.UtcNow - updated > Linger)
                {
                    File.Delete(path);
                    continue;
                }
                using var reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (reader.Length is 0 or > 140_000) continue;
                if (JsonSerializer.Deserialize<AgentWork>(reader, AgentProtocol.Json) is { Client.Name: not null, Tool: not null } work)
                    items.Add(new(work, updated, running));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return items;
    }
}
