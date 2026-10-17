using System.IO;

namespace AIHub.Services;

public static class OutputWaveMetadata
{
    public static async Task ExportAsync(string source, string destination, OutputProvenance provenance)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            await AudioMetadataWriter.AttachAsync(temporary, MusicAudioFormat.Wav,
                new Dictionary<string, string> { ["software"] = "LOPATA " + provenance.ProgramVersion, ["LOPATA_PROVENANCE"] = provenance.Json() }, [], CancellationToken.None);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
