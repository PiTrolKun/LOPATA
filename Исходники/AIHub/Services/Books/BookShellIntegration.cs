using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIHub.Services.Books;

/// <summary>Optional, per-user, single-file verbs. Does not change file associations.</summary>
public sealed class BookShellIntegration
{
    public const string OwnerMarker = "AIHub.BookShellIntegration.v1";
    public static readonly string[] PopularFormats = ["epub", "fb2", "docx", "pdf", "txt"];
    private const string OwnerValue = "LOPATA.Owner";
    private const string ExecutableValue = "LOPATA.Executable";
    private readonly string _executable;
    private readonly string? _testRoot;
    private string[] Paths => _testRoot is null
        ? BookConversionFormats.InputExtensions.Select(e => @"Software\Classes\SystemFileAssociations\." + e + @"\shell\LOPATABook").ToArray()
        : [_testRoot];

    public BookShellIntegration(string executablePath) : this(executablePath, null) { }
    internal BookShellIntegration(string executablePath, string? isolatedTestRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A safe absolute executable path is required.", nameof(executablePath));
        _executable = Path.GetFullPath(executablePath);
        if (isolatedTestRoot is not null)
        {
            const string prefix = @"Software\AIHub.Tests\";
            if (!isolatedTestRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(isolatedTestRoot[prefix.Length..], "N", out _))
                throw new ArgumentException("An isolated test GUID root is required.", nameof(isolatedTestRoot));
            _testRoot = isolatedTestRoot + @"\BookShellIntegration";
        }
    }

    public bool IsEnabled => Paths.Any(path =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(OwnerValue) as string == OwnerMarker
            && string.Equals(key.GetValue(ExecutableValue) as string, _executable, StringComparison.OrdinalIgnoreCase);
    });

    public static bool ResolveEnabled(bool? preference, bool registered) => preference ?? registered;

    public void Apply(bool enabled, Func<string, string> localize)
    {
        if (enabled && !File.Exists(_executable)) throw new FileNotFoundException("BookConverter.Error.ApplicationMissing", _executable);
        var previous = Paths.Select(path => (Path: path, Values: Capture(path))).ToArray();
        // Do not remove or replace registry subtrees owned by another installation/application.
        if (enabled && previous.Any(p => p.Values is not null && (!p.Values.Values.TryGetValue(OwnerValue, out var owner) || !Equals(owner.Data, OwnerMarker)
            || !p.Values.Values.TryGetValue(ExecutableValue, out var executable) || !string.Equals(executable.Data as string, _executable, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("BookConverter.Error.MenuOwned");
        try
        {
            foreach (var item in previous)
            {
                if (!enabled)
                {
                    if (item.Values?.Values.TryGetValue(OwnerValue, out var owner) == true && Equals(owner.Data, OwnerMarker)
                        && item.Values.Values.TryGetValue(ExecutableValue, out var executable)
                        && string.Equals(executable.Data as string, _executable, StringComparison.OrdinalIgnoreCase))
                        Registry.CurrentUser.DeleteSubKeyTree(item.Path, false);
                    continue;
                }
                using var parent = Registry.CurrentUser.CreateSubKey(item.Path, true);
                parent.SetValue(OwnerValue, OwnerMarker); parent.SetValue(ExecutableValue, _executable);
                parent.SetValue("MUIVerb", localize("BookConverter.ShellMenuTitle")); parent.SetValue("Icon", '"' + _executable + "\",0");
                parent.SetValue("SubCommands", ""); parent.SetValue("MultiSelectModel", "Single");
                var extension = _testRoot is null ? item.Path.Split('\\')[3].TrimStart('.') : "txt";
                var supported = BookConversionCapabilities.GetOutputFormats(["sample." + extension]);
                using var verbs = parent.CreateSubKey("shell", true);
                foreach (var format in PopularFormats)
                {
                    // Omit a no-op same-format command and impossible graphical-to-text routes.
                    if (BookConversionFormats.Normalize(extension) == format || !supported.Contains(format, StringComparer.OrdinalIgnoreCase)) continue;
                    using var verb = verbs.CreateSubKey(format, true);
                    verb.SetValue("MUIVerb", localize("BookConverter.ToFormat").Replace("{0}", format.ToUpperInvariant(), StringComparison.Ordinal));
                    verb.SetValue("MultiSelectModel", "Single");
                    using var command = verb.CreateSubKey("command", true); command.SetValue("", Command(_executable, format));
                }
            }
            if (_testRoot is null) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            foreach (var item in previous) Restore(item.Path, item.Values);
            throw;
        }
    }

    internal static string Command(string executable, string format)
    {
        if (!PopularFormats.Contains(format, StringComparer.Ordinal) || !Path.IsPathFullyQualified(executable)
            || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException("Invalid book verb.");
        return '"' + executable + "\" --shell-book " + format + " -- \"%1\"";
    }

    private sealed record Value(object Data, RegistryValueKind Kind);
    private sealed record Snapshot(Dictionary<string, Value> Values, Dictionary<string, Snapshot> Children);
    private static Snapshot? Capture(string path) { using var key = Registry.CurrentUser.OpenSubKey(path); return key is null ? null : Read(key); }
    private static Snapshot Read(RegistryKey key)
    {
        var values = key.GetValueNames().ToDictionary(n => n, n => new Value(key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(n)));
        var children = new Dictionary<string, Snapshot>();
        foreach (var name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name)!; children[name] = Read(child); }
        return new(values, children);
    }
    private static void Restore(string path, Snapshot? snapshot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, false);
        if (snapshot is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(path, true); Write(key, snapshot);
    }
    private static void Write(RegistryKey key, Snapshot snapshot)
    {
        foreach (var value in snapshot.Values) key.SetValue(value.Key, value.Value.Data, value.Value.Kind);
        foreach (var child in snapshot.Children) { using var sub = key.CreateSubKey(child.Key, true); Write(sub, child.Value); }
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
