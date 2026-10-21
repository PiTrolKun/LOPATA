using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLora;

/// <summary>Personal list ordering; deliberately separate from project adapter choices.</summary>
internal sealed class MusicLoraFavorites(string path)
{
    private static readonly object Gate = new();
    internal static MusicLoraFavorites Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "LoraFavorites.json"));
    internal HashSet<string> Load()
    {
        lock (Gate) {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("Music.Lora.FavoritesError");
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Music.Lora.FavoritesError");
            if (ids.Any(id => id is null || !Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,79}\z")))
                throw new InvalidDataException("Music.Lora.FavoritesError");
            return ids.ToHashSet(StringComparer.Ordinal);
        }
    }
    internal HashSet<string> Toggle(string id)
    {
        lock (Gate) {
            if (!Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,79}\z")) throw new InvalidDataException("Music.Lora.FavoritesError");
            var ids = Load(); if (!ids.Add(id)) ids.Remove(id);
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            RagBookPreparation.AtomicJson(path, ids.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            return ids;
        }
    }
}
