using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIHub.Services;

/// <summary>Per-user image verbs; writes and removal are confined to the application's marked subtree.</summary>
public sealed class ImageShellIntegration
{
    public const string RegistryPath = @"Software\Classes\SystemFileAssociations\image\shell\LOPATA";
    public const string OwnerMarker = "AIHub.ImageShellIntegration.v1";
    private const string MarkerValue = "LOPATA.Owner";
    private const string ExecutableValue = "LOPATA.Executable";
    private readonly string _path, _executable;
    private readonly bool _notify;

    public ImageShellIntegration(string executablePath)
        : this(executablePath, RegistryPath, notify: true) { }

    // Tests cannot redirect this API into Classes, Run, another application, or an arbitrary registry subtree.
    internal ImageShellIntegration(string executablePath, string isolatedTestRoot)
        : this(executablePath, TestPath(isolatedTestRoot), notify: false) { }

    private ImageShellIntegration(string executablePath, string path, bool notify)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("An absolute executable path is required.", nameof(executablePath));
        _executable = Path.GetFullPath(executablePath); _path = path; _notify = notify;
    }
    internal string RegisteredPath => _path;
    private static string TestPath(string root)
    {
        const string prefix = @"Software\AIHub.Tests\";
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(root[prefix.Length..], "N", out _))
            throw new ArgumentException("An isolated AIHub.Tests GUID root is required.", nameof(root));
        return root + @"\ImageShellIntegration";
    }

    public void Apply(bool enabled, string menuLabel, string webpLabel, string upscaleLabel)
    {
        if (!enabled) { RemoveOwned(); return; }
        if (!File.Exists(_executable)) throw new FileNotFoundException("Application executable is unavailable.", _executable);
        using (var existing = Registry.CurrentUser.OpenSubKey(_path))
            if (existing is not null && !Equals(existing.GetValue(MarkerValue), OwnerMarker))
                throw new InvalidOperationException("The image menu registry key is not owned by LOPATA.");
        var previous = Capture();
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(_path, writable: true);
            parent.DeleteValue("", throwOnMissingValue: false);
            parent.SetValue(MarkerValue, OwnerMarker);
            parent.SetValue(ExecutableValue, _executable);
            parent.SetValue("MUIVerb", menuLabel);
            parent.SetValue("Icon", '"' + _executable + "\",0");
            parent.SetValue("SubCommands", "");
            parent.SetValue("MultiSelectModel", "Document");
            WriteVerb(parent, "01-webp", "webp", webpLabel);
            WriteVerb(parent, "02-upscale2", "upscale2", upscaleLabel);
            if (_notify) NotifyAssociations();
        }
        catch
        {
            Restore(previous); throw;
        }
    }
    private void WriteVerb(RegistryKey parent, string name, string mode, string label)
    {
        using var verb = parent.CreateSubKey(@"shell\" + name, writable: true);
        verb.SetValue("MUIVerb", label); verb.SetValue("MultiSelectModel", "Document");
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue("", Command(_executable, mode));
    }
    internal static string Command(string executable, string mode)
    {
        if (mode is not ("webp" or "upscale2")) throw new ArgumentOutOfRangeException(nameof(mode));
        return '"' + executable + "\" --shell-image " + mode + " -- \"%1\"";
    }
    private void RemoveOwned()
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(_path))
        {
            if (existing is null || !Equals(existing.GetValue(MarkerValue), OwnerMarker)
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
