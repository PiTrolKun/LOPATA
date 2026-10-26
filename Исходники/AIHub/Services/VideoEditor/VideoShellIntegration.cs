using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AIHub.Services.VideoEditor;

/// <summary>Owned per-user private project association. Does not touch Windows UserChoice.</summary>
public sealed class VideoShellIntegration
{
    private const string Marker = "video-editor-v1";
    private readonly string _executable, _root;
    private readonly bool _test;
    public string ProgId { get; }
    public VideoShellIntegration(string executable, string? testRoot = null)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException("Invalid executable.");
        if (testRoot is not null && (!testRoot.StartsWith(@"Software\AIHub.Tests\", StringComparison.Ordinal) || !Guid.TryParseExact(testRoot[@"Software\AIHub.Tests\".Length..], "N", out _))) throw new ArgumentException("Invalid test root.");
        _executable = Path.GetFullPath(executable); _root = testRoot ?? @"Software\Classes"; _test = testRoot is not null;
        ProgId = "LOPATA.Video." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_executable.ToUpperInvariant())))[..16];
    }
    public void Apply(bool enabled, string title)
    {
        var path = _root + "\\" + ProgId; var extension = _root + @"\.lopataVideo";
        using (var existing = Registry.CurrentUser.OpenSubKey(path)) if (existing is not null && (!Equals(existing.GetValue("LOPATA.Owner"), Marker) || !Equals(existing.GetValue("LOPATA.Executable"), _executable))) throw new IOException("Project registration belongs to another application.");
        // The whole owned subtree is recreated from three values; foreign extension defaults remain intact.
        using var previous = Registry.CurrentUser.OpenSubKey(extension); var previousDefault = previous?.GetValue("");
        if (enabled) {
            using (var key = Registry.CurrentUser.CreateSubKey(path)) {
                key.SetValue("LOPATA.Owner", Marker); key.SetValue("LOPATA.Executable", _executable); key.SetValue("", title);
                using var icon = key.CreateSubKey("DefaultIcon"); icon.SetValue("", '"' + Path.Combine(Path.GetDirectoryName(_executable)!, "Assets", "VideoFileIcon.ico") + '"');
                using var command = key.CreateSubKey(@"shell\open\command"); command.SetValue("", '"' + _executable + "\" --video-project -- \"%1\"");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(extension + @"\OpenWithProgids")) key.SetValue(ProgId, "");
            if (string.IsNullOrEmpty(previousDefault as string)) { using var key = Registry.CurrentUser.CreateSubKey(extension); key.SetValue("", ProgId); }
        }
        else {
            using (var key = Registry.CurrentUser.OpenSubKey(extension + @"\OpenWithProgids", true)) key?.DeleteValue(ProgId, false);
            using (var key = Registry.CurrentUser.OpenSubKey(extension, true)) if (Equals(key?.GetValue(""), ProgId)) key!.DeleteValue("", false);
            using var owned = Registry.CurrentUser.OpenSubKey(path); if (owned is not null) Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
        if (!_test) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
