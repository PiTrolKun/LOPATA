using System.IO;

namespace AIHub.Services;

public static class OutputWaveMetadata
{
    public static async Task ExportAsync(string source, string destination, OutputProvenance provenance, bool recordCollection = true)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            await AudioMetadataWriter.AttachAsync(temporary, MusicAudioFormat.Wav,
                new Dictionary<string, string> { ["software"] = "LOPATA " + provenance.ProgramVersion,
                    ["encoded_by"] = "LOPATA", ["LOPATA_PROVENANCE"] = provenance.Json() }, [], CancellationToken.None);
            File.Move(temporary, destination, true);
            if (recordCollection) CollectionExportJournal.Record(destination, provenance.Scenario);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
