using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>All personal data belongs here. No session archive, catalog card or shared diagnostic log.</summary>
public sealed class FinancialRunStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string DirectoryPath { get; }
    public FinancialRunStore(string path) { DirectoryPath = Path.GetFullPath(path); Lopata.Updates.SafeUpdatePath.RejectLinks(DirectoryPath); }
    public static string Revision(FinancialInput input) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, Options)));
    public void Create(FinancialInput input, DebugModelInfo model)
    {
        FinancialCalculator.Validate(input);
        if (File.Exists(Path.Combine(DirectoryPath, "input.json"))) throw new IOException("Existing financial data must not be overwritten.");
        Directory.CreateDirectory(DirectoryPath);
        Write("input.json", new FinancialRun(input, model, Revision(input)));
        Write("calculation.json", FinancialCalculator.Calculate(input));
    }
    public FinancialRun Load()
    {
        var run = Read<FinancialRun>("input.json"); FinancialCalculator.Validate(run.Input);
        if (run.Model is null || run.Revision != Revision(run.Input)) throw new InvalidDataException("Financial input changed; create a new run to recalculate.");
        return run;
    }
    public void ChangeModel(DebugModelInfo model) { var run = Load(); Write("input.json", run with { Model = model }); }
    public FinancialStageResult? ReadStage(string id)
    {
        CheckStageId(id);
        if (!File.Exists(Path.Combine(DirectoryPath, "stages", id + ".json"))) return null;
        var result = Read<FinancialStageResult>("stages/" + id + ".json");
        if (result.Id != id || result.Revision != Load().Revision || string.IsNullOrWhiteSpace(result.Text)
            || result.Text.Length > 100_000 || !double.IsFinite(result.Seconds) || result.Seconds < 0)
            throw new InvalidDataException("Invalid analytical stage; original retained.");
        return result;
    }
    public FinancialStageResult? ReadCurrentStage(string id)
    {
        var result = ReadStage(id);
        return result?.AnalysisVersion == FinancialAnalysisPlan.AnalysisVersion ? result : null;
    }
    public void SaveStage(FinancialStageResult result)
    {
        CheckStageId(result.Id);
        var previous = ReadStage(result.Id);
        if (previous is not null && previous.AnalysisVersion != result.AnalysisVersion)
        {
            // Preserve the exact old checkpoint privately before replacing its analytical contract.
            var archived = "stages/previous/" + result.Id + "-" + Guid.NewGuid().ToString("N") + ".json";
            WriteText(archived, File.ReadAllText(Checked("stages/" + result.Id + ".json")));
        }
        Write("stages/" + result.Id + ".json", result);
    }
    public T Read<T>(string file)
    {
        var path = Checked(file);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Private file is unexpectedly large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Empty private file.");
    }
    public void Write<T>(string file, T value) => WriteText(file, JsonSerializer.Serialize(value, Options));
    public void WriteText(string file, string text)
    {
        var path = Checked(file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            Lopata.Updates.SafeUpdatePath.RejectLinks(path); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string Checked(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
        if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Outside private folder.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    private static void CheckStageId(string id) { if (!FinancialAnalysisPlan.Stages.Any(s => s.Id == id)) throw new InvalidDataException("Unknown stage."); }
}
