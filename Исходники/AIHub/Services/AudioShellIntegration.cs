using System.IO;
using System.Runtime.InteropServices;
using AIHub.Services.VideoConverter;
using Microsoft.Win32;

namespace AIHub.Services;

public sealed class AudioShellIntegration
{
    public const string RegistryPath = @"Software\Classes\SystemFileAssociations\.mp3\shell\LOPATA";
    public const string OwnerMarker = "AIHub.AudioShellIntegration.v1";
    private static readonly string[] Extensions = [".mp3", ".opus", ".flac", ".wav"];
    private readonly MediaShellIntegration _menu;
    public AudioShellIntegration(string executablePath) => _menu = MediaShellIntegration.Audio(executablePath, Extensions);
    internal AudioShellIntegration(string executablePath, string isolatedTestRoot) =>
        _menu = MediaShellIntegration.Audio(executablePath, Extensions, isolatedTestRoot);
    internal string RegisteredPath => _menu.RegisteredPath;
    public void Apply(bool enabled, string menuLabel, string convertLabel) => _menu.Apply(enabled, menuLabel, convertLabel);
    internal static string Command(string executable, string mode) => mode == "convert"
        ? MediaShellIntegration.Command(executable, "--shell-audio") : throw new ArgumentOutOfRangeException(nameof(mode));
}

/// <summary>Per-user conversion verb confined to the application's marked extension keys.</summary>
internal sealed class MediaShellIntegration
{
    internal const string VideoMarker = "AIHub.VideoConverterShell.v1";
    private const string MarkerValue = "LOPATA.Owner";
    private const string ExecutableValue = "LOPATA.Executable";
    private readonly string _path, _executable, _ownerMarker, _argument;
    private readonly string[] _entries;
    private readonly bool _notify;

    internal static MediaShellIntegration Audio(string executable, string[] extensions, string? testRoot = null) =>
        Create(executable, extensions, "LOPATA", AudioShellIntegration.OwnerMarker, "--shell-audio", "AudioShellIntegration", testRoot);

    internal static MediaShellIntegration Video(string executable, string? testRoot = null) =>
        Create(executable, ConversionOptions.Extensions, "LOPATA.VideoConverter", VideoMarker, "--shell-video-convert", "VideoConverterShell", testRoot);

    private static MediaShellIntegration Create(string executable, string[] extensions, string key, string marker,
        string argument, string testName, string? testRoot)
    {
        string[] paths = testRoot is null
            ? extensions.Select(ext => $@"Software\Classes\SystemFileAssociations\{ext}\shell\{key}").ToArray()
            : [TestPath(testRoot, testName)];
        return new(executable, paths[0], marker, argument, paths, testRoot is null);
    }

    private MediaShellIntegration(string executablePath, string path, string marker, string argument, string[] entries, bool notify)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("An absolute executable path is required.", nameof(executablePath));
        _executable = Path.GetFullPath(executablePath); _path = path; _notify = notify;
        _ownerMarker = marker; _argument = argument; _entries = entries;
    }
    internal string RegisteredPath => _path;
    private static string TestPath(string root, string name)
    {
        const string prefix = @"Software\AIHub.Tests\";
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(root[prefix.Length..], "N", out _))
            throw new ArgumentException("An isolated AIHub.Tests GUID root is required.", nameof(root));
        return root + @"\" + name;
    }

    public void Apply(bool enabled, string menuLabel, string convertLabel)
    {
        var entries = _entries.Select(path => new MediaShellIntegration(_executable, path, _ownerMarker, _argument, [path], false)).ToArray();
        var previous = entries.Select(e => e.Capture()).ToArray();
        if (enabled && previous.Any(s => s is not null && (!s.Values.TryGetValue(MarkerValue, out var owner) || !Equals(owner.Data, _ownerMarker))))
            throw new InvalidOperationException("A media menu registry key is not owned by LOPATA.");
        try
        {
            foreach (var entry in entries) entry.ApplySingle(enabled, menuLabel, convertLabel);
            if (_notify) NotifyAssociations();
        }
        catch
        {
            for (var i = 0; i < entries.Length; i++) entries[i].Restore(previous[i]);
            throw;
        }
    }
    private void ApplySingle(bool enabled, string menuLabel, string convertLabel)
    {
        if (!enabled) { RemoveOwned(); return; }
        if (!File.Exists(_executable)) throw new FileNotFoundException("Application executable is unavailable.", _executable);
        using (var existing = Registry.CurrentUser.OpenSubKey(_path))
            if (existing is not null && !Equals(existing.GetValue(MarkerValue), _ownerMarker))
                throw new InvalidOperationException("The media menu registry key is not owned by LOPATA.");
        var previous = Capture();
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(_path, writable: true);
            parent.DeleteValue("", throwOnMissingValue: false);
            parent.SetValue(MarkerValue, _ownerMarker);
            parent.SetValue(ExecutableValue, _executable);
            parent.SetValue("MUIVerb", menuLabel);
            parent.SetValue("Icon", '"' + _executable + "\",0");
            parent.SetValue("SubCommands", "");
            parent.SetValue("MultiSelectModel", "Document");
            WriteVerb(parent, "01-convert", convertLabel);

            if (_notify) NotifyAssociations();
        }
        catch
        {
            Restore(previous); throw;
        }
    }
    private void WriteVerb(RegistryKey parent, string name, string label)
    {
        using var verb = parent.CreateSubKey(@"shell\" + name, writable: true);
        verb.SetValue("MUIVerb", label); verb.SetValue("MultiSelectModel", "Document");
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue("", Command(_executable, _argument));
    }
    internal static string Command(string executable, string argument) => '"' + executable + "\" " + argument + " -- \"%1\"";
    private void RemoveOwned()
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(_path))
        {
            if (existing is null || !Equals(existing.GetValue(MarkerValue), _ownerMarker)
                || !string.Equals(existing.GetValue(ExecutableValue) as string, _executable, StringComparison.OrdinalIgnoreCase)) return;
        }
        // _path is either the fixed LOPATA key or a validated isolated test root; never a parent association key.
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        if (_notify) NotifyAssociations();
    }
    private sealed record Value(object Data, RegistryValueKind Kind);
    private sealed record Snapshot(Dictionary<string, Value> Values, Dictionary<string, Snapshot> Children);
    private Snapshot? Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_path); return key is null ? null : Read(key);
    }
    private static Snapshot Read(RegistryKey key)
    {
        var values = key.GetValueNames().ToDictionary(name => name,
            name => new Value(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name)));
        var children = new Dictionary<string, Snapshot>();
        foreach (var name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name)!; children[name] = Read(child); }
        return new(values, children);
    }
    private void Restore(Snapshot? snapshot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        if (snapshot is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(_path, writable: true); Write(key, snapshot);
    }
    private static void Write(RegistryKey key, Snapshot snapshot)
    {
        foreach (var (name, value) in snapshot.Values) key.SetValue(name, value.Data, value.Kind);
        foreach (var (name, child) in snapshot.Children) { using var subkey = key.CreateSubKey(name, writable: true); Write(subkey, child); }
    }
    private static void NotifyAssociations() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
