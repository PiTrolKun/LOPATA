using System.IO;

namespace AIHub.Services;

public static class MusicTrackFiles
{
    public static void Copy(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            if (new FileInfo(source).Length != new FileInfo(temporary).Length) throw new IOException("Incomplete audio copy.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
