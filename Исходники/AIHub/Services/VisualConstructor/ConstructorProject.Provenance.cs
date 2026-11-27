using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed partial class ConstructorProject
{
    private sealed record ModelReceipt(string SourceHash, OutputModelUse Model);

    internal void RecordModelUse(ConstructorChainStep step, OutputModelUse model)
    {
        var path = step.FilePath + ".model.json";
        SafeUpdatePath.RejectLinks(path);
        WriteSource(path, File.Exists(path) ? File.ReadAllText(path) : null,
            JsonSerializer.Serialize(new ModelReceipt(Fingerprint([step.Source]), model)));
    }

    internal OutputModelUse[] ReadAppliedModelUses()
    {
        var result = new List<OutputModelUse>();
        foreach (var source in ReadForms().SelectMany(ActionSourceFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = source + ".model.json";
            SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) continue;
            var receipt = JsonSerializer.Deserialize<ModelReceipt>(File.ReadAllText(path));
            if (receipt?.SourceHash == Fingerprint([File.ReadAllText(source)]))
                result.Add(receipt.Model);
        }
        return result.Distinct().ToArray();
    }
}
