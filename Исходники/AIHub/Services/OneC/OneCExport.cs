using System.IO;
using System.Text;
using AIHub.Models.OneC;
using Lopata.Updates;

namespace AIHub.Services.OneC;

internal static class OneCExport
{
    internal static void SaveNew(string destination, string code, OutputModelUse[] models)
    {
        destination = Path.GetFullPath(destination); SafeUpdatePath.RejectLinks(destination);
        var extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".bsl" or ".os" or ".txt" or ".md")) throw new InvalidDataException("OneC.UnsupportedFormat");
        if (File.Exists(destination)) throw new InvalidDataException("OneC.NameExists");
        var facts = new OutputProvenance { Scenario = "onec/export", CreatedUtc = DateTimeOffset.UtcNow,
            Models = models, ModelHistory = models.Length > 0 ? "recorded" : "not-applicable" };
        var prefix = extension == ".md" ? "<!-- LOPATA " + facts.Json() + " -->\n"
            : (extension == ".txt" ? "# LOPATA " : "// LOPATA ") + facts.Json() + "\n";
        var temporary = destination + "." + OneCIds.New() + ".tmp";
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; stream.Write(Encoding.UTF8.GetBytes(prefix + code)); stream.Flush(true); }
            File.Move(temporary, destination, false);
            CollectionExportJournal.Record(destination, facts.Scenario);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
