using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AIHub.Services.OfficeReader;

/// <summary>Per-user, per-installation candidate registration. Never writes Windows UserChoice.</summary>
internal sealed class ReaderShellIntegration
{
    private const string Marker = "office-reader-v1";
    private readonly string _executable, _classes, _capabilities, _applications;
    private readonly bool _test;
    internal string ProgId { get; }
    internal string ProjectProgId => ProgId + ".Project";
    internal string RegisteredName => "LOPATA Reader " + ProgId[^8..];
    internal string RegisteredPath => _classes + @"\" + ProgId;
    private string ProjectPath => _classes + @"\" + ProjectProgId;
    internal string CapabilitiesPath => _capabilities;

    internal ReaderShellIntegration(string executable, string? testRoot = null)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException("Invalid executable.");
        if (testRoot is not null && (!testRoot.StartsWith(@"Software\AIHub.Tests\", StringComparison.Ordinal)
            || !Guid.TryParseExact(testRoot[@"Software\AIHub.Tests\".Length..], "N", out _))) throw new ArgumentException("Invalid test root.");
        _executable = Path.GetFullPath(executable); _test = testRoot is not null;
        ProgId = "LOPATA.Reader." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_executable.ToUpperInvariant())))[..16];
        _classes = testRoot is null ? @"Software\Classes" : testRoot + @"\Classes";
        _capabilities = (testRoot ?? @"Software\LOPATA") + @"\Reader\" + ProgId + @"\Capabilities";
        _applications = testRoot is null ? @"Software\RegisteredApplications" : testRoot + @"\RegisteredApplications";
    }

    internal void Apply(bool enabled, string title, string description, Func<string, ReaderAssociation>? inspect = null,
        string projectTypeName = "LOPATAdoxAI")
    {
        inspect ??= ReaderAssociationDiscovery.Read;
        using var existing = Registry.CurrentUser.OpenSubKey(RegisteredPath);
        using var project = Registry.CurrentUser.OpenSubKey(ProjectPath);
        using var capability = Registry.CurrentUser.OpenSubKey(_capabilities);
        var ownsReader = existing is not null && Owned(existing);
        var ownsProject = project is not null && Owned(project);
        var ownsCapabilities = capability is not null && Owned(capability);
        if (enabled)
        {
            if ((existing is not null && !ownsReader) || (project is not null && !ownsProject) || (capability is not null && !ownsCapabilities))
                throw new InvalidOperationException("Foreign reader registration.");
            using var applications = Registry.CurrentUser.OpenSubKey(_applications);
            if (applications?.GetValue(RegisteredName) is string registered && registered != _capabilities)
                throw new InvalidOperationException("Foreign application registration.");
            // Read before registering ourselves, otherwise our own candidate can conceal
            // that a format previously had no handler. Unknown results never claim it.
            var states = ReaderFileRequest.Extensions.ToDictionary(e => e, inspect, StringComparer.OrdinalIgnoreCase);
            var readerIcon = IconLocation("ReaderFileIcon.ico");
            RegisterFileClass(RegisteredPath, title, title, description, readerIcon);
            RegisterFileClass(ProjectPath, projectTypeName, title, description, IconLocation("OfficeProjectFileIcon.ico"));
            using var caps = Registry.CurrentUser.CreateSubKey(_capabilities);
            SetOwner(caps); caps.SetValue("ApplicationName", RegisteredName);
            caps.SetValue("ApplicationDescription", description); caps.SetValue("ApplicationIcon", readerIcon);
            using (var associations = caps.CreateSubKey("FileAssociations"))
                foreach (var extension in ReaderFileRequest.Extensions) associations.SetValue(extension, FileClass(extension));
            using (var apps = Registry.CurrentUser.CreateSubKey(_applications))
            {
                apps.SetValue(RegisteredName, _capabilities);
                if (apps.GetValue(ProgId) as string == _capabilities) apps.DeleteValue(ProgId, false);
            }
            foreach (var extension in ReaderFileRequest.Extensions)
            {
                using var ext = Registry.CurrentUser.CreateSubKey(_classes + @"\" + extension);
                var fileClass = FileClass(extension);
                using var open = ext.CreateSubKey("OpenWithProgids");
                open.SetValue(fileClass, "");
                var migrate = ownsReader && fileClass == ProjectProgId;
                if (migrate) open.DeleteValue(ProgId, false);
                var current = ext.GetValue("") as string;
                var state = states[extension];
                if ((state.CanRegisterDefault && string.IsNullOrEmpty(current)) ||
                    (migrate && !state.HasUserChoice && string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase)))
                    ext.SetValue("", fileClass);
            }
            ApplyApplicationEntry(true, title);
        }
        else if (ownsReader || ownsProject || ownsCapabilities)
        {
            foreach (var extension in ReaderFileRequest.Extensions)
            {
                using var ext = Registry.CurrentUser.OpenSubKey(_classes + @"\" + extension, writable: true);
                if (ext is null) continue;
                using var open = ext.OpenSubKey("OpenWithProgids", writable: true);
                var isProject = FileClass(extension) == ProjectProgId;
                if (ownsReader) open?.DeleteValue(ProgId, false);
                if (isProject && ownsProject) open?.DeleteValue(ProjectProgId, false);
                var current = ext.GetValue("") as string;
                if ((ownsReader && string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase)) ||
                    (isProject && ownsProject && string.Equals(current, ProjectProgId, StringComparison.OrdinalIgnoreCase)))
                    ext.DeleteValue("", false);
            }
            using (var apps = Registry.CurrentUser.OpenSubKey(_applications, writable: true))
            {
                if (ownsCapabilities && string.Equals(apps?.GetValue(RegisteredName) as string, _capabilities, StringComparison.Ordinal)) apps!.DeleteValue(RegisteredName, false);
                if (ownsCapabilities && string.Equals(apps?.GetValue(ProgId) as string, _capabilities, StringComparison.Ordinal)) apps!.DeleteValue(ProgId, false);
            }
            if (ownsReader) Registry.CurrentUser.DeleteSubKeyTree(RegisteredPath, false);
            if (ownsProject) Registry.CurrentUser.DeleteSubKeyTree(ProjectPath, false);
            if (ownsCapabilities) Registry.CurrentUser.DeleteSubKeyTree(_capabilities, false);
            ApplyApplicationEntry(false, "");
        }
        if (!_test) SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    }

    private string FileClass(string extension) =>
        extension.Equals(OfficeEditor.OfficePackage.Extension, StringComparison.OrdinalIgnoreCase) ? ProjectProgId : ProgId;
    private string IconLocation(string name) => $"\"{Path.Combine(Path.GetDirectoryName(_executable)!, "Assets", name)}\",0";
    private void RegisterFileClass(string path, string typeName, string title, string description, string iconLocation)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        SetOwner(key); key.SetValue("", typeName); key.SetValue("FriendlyTypeName", typeName); key.SetValue("FriendlyAppName", title);
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", iconLocation);
        using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{_executable}\" --reader -- \"%1\"");
        using (var open = key.CreateSubKey(@"shell\open")) open.SetValue("MultiSelectModel", "Document");
        using var application = key.CreateSubKey("Application");
        application.SetValue("ApplicationName", title); application.SetValue("ApplicationDescription", description);
        application.SetValue("ApplicationIcon", iconLocation);
    }

    private bool Owned(RegistryKey key) => key.GetValue("LOPATA.Owner") as string == Marker
        && string.Equals(key.GetValue("LOPATA.Executable") as string, _executable, StringComparison.OrdinalIgnoreCase);
    private void SetOwner(RegistryKey key) { key.SetValue("LOPATA.Owner", Marker); key.SetValue("LOPATA.Executable", _executable); }
    private void ApplyApplicationEntry(bool enabled, string title)
    {
        var path = _classes + @"\Applications\" + Path.GetFileName(_executable);
        using var previous = Registry.CurrentUser.OpenSubKey(path);
        // Applications is keyed by executable filename. An installed/portable copy
        // with the same filename may already own it; its launch command must survive.
        if (previous is not null && !Owned(previous)) return;
        if (!enabled) { if (previous is not null) Registry.CurrentUser.DeleteSubKeyTree(path, false); return; }
        using var app = Registry.CurrentUser.CreateSubKey(path); SetOwner(app);
        app.SetValue("FriendlyAppName", title);
        using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{_executable}\" --reader -- \"%1\"");
        using var types = app.CreateSubKey("SupportedTypes");
        foreach (var extension in ReaderFileRequest.Extensions) types.SetValue(extension, "");
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint id, uint flags, IntPtr first, IntPtr second);
}
