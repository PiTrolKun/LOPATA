using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AIHub.Models;
using Microsoft.Win32;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderAssociation(string Extension, string CurrentName, string ProgId, string Executable,
    bool HasOtherReader, bool Uncertain, bool HasUserChoice)
{
    public IReadOnlyList<string> Handlers { get; init; } = [];
    internal bool CanChooseInternally => !HasOtherReader && !Uncertain;
    internal bool CanRegisterDefault => CanChooseInternally && !HasUserChoice
        && string.IsNullOrEmpty(ProgId) && string.IsNullOrEmpty(Executable);
}

internal static class ReaderAssociationDiscovery
{
    internal static bool PreferInternal(string extension, FileViewerSettings settings, ReaderAssociation association)
        => settings.PreferInternalByExtension.TryGetValue(extension, out var manual) ? manual
            : settings.PreferInternalViewers && (!settings.ReaderWindowsIntegrationEnabled || association.CanChooseInternally);

    internal static ReaderAssociation Read(string extension)
    {
        try { return ReadCore(extension); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // An unreadable association is unknown, never permission to replace it.
            return new(extension, "", "", "", true, true, false);
        }
    }

    private static ReaderAssociation ReadCore(string extension)
    {
        var progId = Query(extension, 20); var executable = Query(extension, 2); var name = Query(extension, 4);
        using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
        var chosen = choice?.GetValue("ProgId") as string;
        // Windows' own OpenWith dialog is the fallback for an unassigned type,
        // not an installed reader. Distinguish this exact sentinel from unknown apps.
        if (progId == "Unknown" && IsOpenWithFallback(executable) && string.IsNullOrEmpty(chosen))
        { progId = ""; executable = ""; name = ""; }
        var uncertain = !string.IsNullOrEmpty(chosen) && string.IsNullOrEmpty(progId);
        var other = IsOther(executable, progId);
        var candidates = new List<string>();
        IEnumAssocHandlers? handlers = null;
        try
        {
            // The unfiltered list also includes generic "open anything" tools (7-Zip,
            // media players, etc.). Recommended handlers are registered for this type.
            var status = SHAssocEnumHandlers(extension, 1, out handlers);
            if (status < 0) uncertain = true;
            if (handlers is not null)
            {
                for (var i = 0; i < 256; i++)
                {
                    IAssocHandler? handler = null;
                    try
                    {
                        if (handlers.Next(1, out handler, out var fetched) != 0 || fetched == 0) break;
                        if (handler is null || handler.GetName(out var path) < 0 || string.IsNullOrEmpty(path)) { uncertain = true; continue; }
                        candidates.Add(path);
                        if (IsOther(path, "")) other = true;
                    }
                    finally { if (handler is not null) Marshal.ReleaseComObject(handler); }
                    if (i == 255) uncertain = true;
                }
            }
        }
        catch (COMException) { uncertain = true; }
        finally { if (handlers is not null) Marshal.ReleaseComObject(handlers); }
        if (!string.IsNullOrEmpty(progId) && string.IsNullOrEmpty(executable) && !progId.StartsWith("LOPATA.Reader.", StringComparison.Ordinal)) uncertain = true;
        return new(extension, name, progId, executable, other, uncertain, !string.IsNullOrEmpty(chosen)) { Handlers = candidates };
    }

    internal static bool IsOther(string executable, string progId)
    {
        if (progId.StartsWith("LOPATA.Reader.", StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(executable)) return !string.IsNullOrEmpty(progId);
        if (string.Equals(executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
        // Only known Windows viewers are exempt. An unrecognised AppX or executable
        // is preserved even if its display name resembles a standard Windows application.
        if (!Path.IsPathFullyQualified(executable)) return true;
        var path = Path.GetFullPath(executable);
        if (IsOpenWithFallback(path)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).ToLowerInvariant() is "notepad.exe" or "wordpad.exe") return false;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            if (path.Equals(Path.Combine(Environment.GetFolderPath(folder), @"Microsoft\Edge\Application\msedge.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            if (path.Equals(Path.Combine(Environment.GetFolderPath(folder), @"Windows NT\Accessories\wordpad.exe"), StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsApps\Microsoft.WindowsNotepad_"), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Equals("notepad.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsOpenWithFallback(string path) => path.Equals(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenWith.exe"), StringComparison.OrdinalIgnoreCase);

    private static string Query(string extension, uint kind)
    {
        uint size = 32768; var value = new StringBuilder((int)size);
        return AssocQueryString(0, kind, extension, null, value, ref size) == 0 ? value.ToString() : "";
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(uint flags, uint kind, string association, string? extra, StringBuilder value, ref uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers? handlers);
    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler? handler, out uint fetched);
    }
    [ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
