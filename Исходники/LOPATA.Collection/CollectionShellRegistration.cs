using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Lopata.Collection;

/// <summary>Owns only the current installation's per-user Explorer namespace registration.</summary>
public sealed class CollectionShellRegistration
{
    public const string ClassId = "{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}";
    private const string Owner = "LOPATA.Collection.v1";
    private readonly string installation;
    private readonly string prefix;
    private readonly Action<string>? isolatedWriteCheckpoint;

    public CollectionShellRegistration(string executable, string? testRoot = null, Action<string>? isolatedWriteCheckpoint = null)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new ArgumentException("Invalid application path.", nameof(executable));
        if (testRoot is not null && (!testRoot.StartsWith(@"Software\AIHub.Tests\", StringComparison.Ordinal)
            || !Guid.TryParseExact(testRoot[@"Software\AIHub.Tests\".Length..], "N", out _)))
            throw new ArgumentException("Invalid isolated registry root.", nameof(testRoot));
        if (isolatedWriteCheckpoint is not null && testRoot is null)
            throw new ArgumentException("Fault injection requires an isolated registry root.", nameof(isolatedWriteCheckpoint));
        installation = Path.GetFullPath(executable);
        prefix = testRoot ?? "Software";
        this.isolatedWriteCheckpoint = isolatedWriteCheckpoint;
    }

    public string ClassPath => prefix + @"\Classes\CLSID\" + ClassId;
    public string NamespacePath => prefix + @"\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + ClassId;
    private string DesktopPath => prefix + @"\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    public bool CanManageRegistration
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassPath);
            using var node = Registry.CurrentUser.OpenSubKey(NamespacePath);
            return (key is null || Owns(key)) && (node is null || Owns(node));
        }
    }

    public bool HasOwnedRegistration
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassPath);
            using var node = Registry.CurrentUser.OpenSubKey(NamespacePath);
            return (key is null || Owns(key)) && (node is null || Owns(node)) && (key is not null || node is not null);
        }
    }

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassPath);
            using var node = Registry.CurrentUser.OpenSubKey(NamespacePath);
            return key is not null && node is not null && Owns(key) && Owns(node);
        }
    }

    public void Enable(string nativePath, string workerPath, string dataDirectory, string language)
    {
        nativePath = ExistingFile(nativePath);
        workerPath = ExistingFile(workerPath);
        if (!Path.IsPathFullyQualified(dataDirectory) || dataDirectory.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new ArgumentException("Invalid collection data directory.");
        dataDirectory = Path.GetFullPath(dataDirectory);
        using var gate = Enter();
        try
        {
            AssertAvailable(ClassPath); AssertAvailable(NamespacePath);
            Change(() =>
            {
                var desktop = SavedDesktop(ClassPath) ?? SavedDesktop(NamespacePath) ?? new DesktopState(ReadValue(DesktopPath, ClassId));
                using (var key = Registry.CurrentUser.CreateSubKey(ClassPath))
                {
                    Stamp(key); SaveDesktop(key, desktop);
                    key.SetValue("", language == "ru" ? "ЛОПАТА" : "LOPATA");
                    key.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
                    key.SetValue("SortOrderIndex", 0x43, RegistryValueKind.DWord);
                    key.SetValue("DataDirectory", dataDirectory);
                    key.SetValue("WorkerPath", workerPath);
                    key.SetValue("Language", language == "ru" ? "ru" : "en");
                    using (var server = key.CreateSubKey("InprocServer32"))
                    {
                        server.SetValue("", nativePath);
                        server.SetValue("ThreadingModel", "Apartment");
                    }
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + installation + "\",0");
                    using var folder = key.CreateSubKey("ShellFolder");
                    folder.SetValue("Attributes", unchecked((int)0x28000000), RegistryValueKind.DWord);
                }
                isolatedWriteCheckpoint?.Invoke("class");
                using (var key = Registry.CurrentUser.CreateSubKey(NamespacePath))
                {
                    Stamp(key); SaveDesktop(key, desktop); key.SetValue("", "LOPATA");
                }
                isolatedWriteCheckpoint?.Invoke("namespace");
                WriteValue(DesktopPath, ClassId, new(1, RegistryValueKind.DWord));
                isolatedWriteCheckpoint?.Invoke("desktop");
            });
        }
        finally { gate.ReleaseMutex(); }
        NotifyShell();
    }

    public void Disable()
    {
        using var gate = Enter();
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ClassPath))
            using (var node = Registry.CurrentUser.OpenSubKey(NamespacePath))
                if (key is null && node is null || key is not null && !Owns(key) || node is not null && !Owns(node)) return;
            var desktop = SavedDesktop(ClassPath) ?? SavedDesktop(NamespacePath);
            Change(() =>
            {
                RemoveOwned(NamespacePath); RemoveOwned(ClassPath);
                // Preserve a desktop visibility choice changed outside LOPATA while enabled.
                var current = ReadValue(DesktopPath, ClassId);
                if (desktop is not null && current is { Kind: RegistryValueKind.DWord, Data: 1 })
                    WriteValue(DesktopPath, ClassId, desktop.Value);
            });
        }
        finally { gate.ReleaseMutex(); }
        NotifyShell();
    }

    private void AssertAvailable(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is not null && !Owns(key)) throw new IOException("The collection namespace belongs to another installation.");
    }

    private bool Owns(RegistryKey key) => Raw(key, "LOPATA.Owner") as string == Owner
        && string.Equals(Raw(key, "Executable") as string, installation, StringComparison.OrdinalIgnoreCase);

    private static object? Raw(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

    private void Stamp(RegistryKey key)
    {
        key.SetValue("LOPATA.Owner", Owner);
        key.SetValue("Executable", installation);
    }

    private void RemoveOwned(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
            if (key is null || !Owns(key)) return;
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    private sealed record Value(object Data, RegistryValueKind Kind);
    private sealed record DesktopState(Value? Value);
    private sealed record Tree(Dictionary<string, Value> Values, Dictionary<string, Tree> Children);

    private static Value? ReadValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
            ? null : new(Raw(key, name)!, key.GetValueKind(name));
    }

    private static void WriteValue(string path, string name, Value? value)
    {
        if (value is null) { using var existing = Registry.CurrentUser.OpenSubKey(path, true); existing?.DeleteValue(name, false); }
        else { using var key = Registry.CurrentUser.CreateSubKey(path); key.SetValue(name, value.Data, value.Kind); }
    }

    private static void SaveDesktop(RegistryKey key, DesktopState desktop)
    {
        using var state = key.CreateSubKey("LOPATA.DesktopState");
        state.SetValue("Exists", desktop.Value is null ? 0 : 1, RegistryValueKind.DWord);
        if (desktop.Value is { } value) state.SetValue("Value", value.Data, value.Kind);
        else state.DeleteValue("Value", false);
    }

    private static DesktopState? SavedDesktop(string path)
    {
        using var state = Registry.CurrentUser.OpenSubKey(path + @"\LOPATA.DesktopState");
        if (state is null) return null;
        if (state.GetValue("Exists") is not int exists || exists is not (0 or 1)) throw new IOException("Invalid desktop state.");
        var value = ReadValue(path + @"\LOPATA.DesktopState", "Value");
        if (exists == 1 && value is null) throw new IOException("Missing desktop state.");
        return new(exists == 0 ? null : value);
    }

    private static Tree? Capture(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null) return null;
        return new(key.GetValueNames().ToDictionary(name => name, name => new Value(Raw(key, name)!, key.GetValueKind(name))),
            key.GetSubKeyNames().ToDictionary(name => name, name => Capture(path + "\\" + name)!));
    }

    private static void WriteTree(string path, Tree tree)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        foreach (var (name, value) in tree.Values) key.SetValue(name, value.Data, value.Kind);
        foreach (var (name, child) in tree.Children) WriteTree(path + "\\" + name, child);
    }

    private void Restore(string path, Tree? before)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is not null && !Owns(key) && !(key.ValueCount == 0 && key.SubKeyCount == 0)
                && !(Raw(key, "LOPATA.Owner") as string == Owner && Raw(key, "Executable") is null))
                throw new IOException("A foreign registration appeared during rollback.");
        }
        Registry.CurrentUser.DeleteSubKeyTree(path, false);
        if (before is not null) WriteTree(path, before);
    }

    private void Change(Action update)
    {
        var classes = Capture(ClassPath); var node = Capture(NamespacePath); var desktop = ReadValue(DesktopPath, ClassId);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { ClassPath, NamespacePath, DesktopPath })
            for (var parent = path; parent.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase); parent = parent[..parent.LastIndexOf('\\')])
            { using var key = Registry.CurrentUser.OpenSubKey(parent); if (key is null) missing.Add(parent); }
        try { update(); }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var undo in new Action[] { () => Restore(ClassPath, classes), () => Restore(NamespacePath, node), () => WriteValue(DesktopPath, ClassId, desktop) })
                try { undo(); } catch (Exception error) { failures.Add(error); }
            foreach (var path in missing.OrderByDescending(p => p.Length))
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(path))
                        if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) continue;
                    Registry.CurrentUser.DeleteSubKey(path, false);
                }
                catch (Exception error) { failures.Add(error); }
            if (failures.Count > 1) throw new AggregateException("Collection registration rollback failed.", failures);
            throw;
        }
    }

    private Mutex Enter()
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prefix.ToUpperInvariant())));
        var gate = new Mutex(false, @"Local\LOPATA.Collection.Registration." + id);
        try
        {
            try { if (!gate.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("Collection registration is busy."); }
            catch (AbandonedMutexException) { }
            return gate;
        }
        catch { gate.Dispose(); throw; }
    }

    private static string ExistingFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0 || !File.Exists(path))
            throw new FileNotFoundException("The collection component is missing.");
        return Path.GetFullPath(path);
    }

    private void NotifyShell()
    {
        if (prefix == "Software") SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
}
