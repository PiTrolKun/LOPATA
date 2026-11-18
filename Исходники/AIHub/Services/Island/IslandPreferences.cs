using System;
using System.IO;
using System.Text.Json;

namespace Lopata.Island;

internal sealed record IslandPreferences
{
    internal static string Root => Environment.GetEnvironmentVariable("LOPATA_ISLAND_PROFILE") is { Length: > 0 } path
        ? Path.GetFullPath(path)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB", "Island");

    public string Tone { get; init; } = "Graphite";
    public bool AboveWindows { get; init; } = true;
    public bool Advanced { get; init; }
    public bool ReduceMotion { get; init; }
    public bool FollowPointer { get; init; }
    public bool Hotkey { get; init; }
    public string Monitor { get; init; } = "";
    public int ClosedWidth { get; init; } = 300;
    public int ClosedHeight { get; init; } = 36;
    public int HoverWidth { get; init; } = 355;
    internal const int MinimumHoverHeight = 184;
    public int HoverHeight { get; init; } = MinimumHoverHeight;
    public int PanelWidth { get; init; } = 400;
    public int PanelHeight { get; init; } = 390;

    internal string Surface => Tone switch { "Ocean" => "#14233B", "Forest" => "#14251E", _ => "#121820" };
    internal string Accent => Tone switch { "Ocean" => "#93C5FD", "Forest" => "#9EEAC8", _ => "#9FE3C4" };

    internal IslandPreferences Normalize() => this with
    {
        Tone = Tone is "Ocean" or "Forest" ? Tone : "Graphite",
        Monitor = Monitor is { Length: <= 120 } ? Monitor : "",
        ClosedWidth = Math.Clamp(ClosedWidth, 240, 520),
        ClosedHeight = Math.Clamp(ClosedHeight, 30, 58),
        HoverWidth = Math.Clamp(HoverWidth, 300, 540),
        HoverHeight = Math.Clamp(HoverHeight, MinimumHoverHeight, 260),
        PanelWidth = Math.Clamp(PanelWidth, 320, 580),
        PanelHeight = Math.Clamp(PanelHeight, 270, 540)
    };

    internal static IslandPreferences Read() => (ReadJson<IslandPreferences>("appearance.json") ?? new()).Normalize();
    internal static void Save(IslandPreferences value) => SaveJson("appearance.json", value.Normalize());

    internal static T? ReadJson<T>(string file, int maximumBytes = 128 * 1024) where T : class
    {
        try
        {
            using var stream = new FileStream(Path.Combine(Root, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length <= maximumBytes ? JsonSerializer.Deserialize<T>(stream) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void SaveJson<T>(string file, T value)
    {
        var root = Root;
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, file);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, value);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record IslandLanguage(string Code, System.Collections.Generic.Dictionary<string, string> Captions)
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
}
