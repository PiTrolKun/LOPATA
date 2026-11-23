using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.NeuroConstructor;

public sealed record NeuroAutoResult(NeuroProject Project, NeuroStatus Status, string ReportPath, string OutputPath);

public static partial class NeuroService
{
    private sealed record AutoExportBinding(string Id,string Command,string Project,string Package,string Run);

    public static Task BuildAutoAsync(NeuroProject p,string destination,CancellationToken token=default)
    {
        if (p.Mode != "auto" || p.ParentPackage.Length != 0 || p.Examples.Count != 0)
            throw new ArgumentException("Neuro.Settings");
        var path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(path),".lopatanet",StringComparison.OrdinalIgnoreCase))
            throw new IOException("Neuro.NewFileRequired");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Neuro.AutoOutputExists");
        p.Connection = p.Composition == "pair" ? "inside" : "late";
        p.UseReferenceExamples = true;
        p.Attempts = 1;
        return StartRunAsync(p,p.AutoEvaluateOnly,token,path);
    }

    private static void SaveAutoExport(NeuroProject p,string run,string path)
    {
        // Auto always publishes a new file. A file appearing during training is owned by someone else.
        var originals = new Dictionary<string,Stamp> { [path] = new(false,0,0) };
        var job = new PackageJob(run,"pack",p.Id,path,p.Device,[],p,run,originals);
        NeuroFiles.Write(Path.Combine(JobFolder(run),"request.json"),job);
    }

    public static string? AutoOutputPath(NeuroProject p)
    {
        if (p.Mode != "auto" || Pointer(p.Id,"current") is not { } id) return null;
        var file = Path.Combine(NeuroFiles.Run(p.Id,id),"auto-output.json");
        if (!File.Exists(file)) return null;
        var path = NeuroFiles.Read<string>(file);
        return File.Exists(path) ? path : null;
    }

    public static NeuroAutoResult SavedAutoResult(string projectId,string runId)
    {
        var folder = NeuroFiles.Run(projectId,runId);
        var project = NeuroFiles.Read<NeuroProject>(Path.Combine(folder,"settings.json"));
        var status = NeuroFiles.Read<NeuroStatus>(Path.Combine(folder,"auto-status.json"));
        var reportPath = Path.Combine(folder,"report.json");
        var report = NeuroFiles.Read<JsonElement>(reportPath);
        var output = NeuroFiles.Read<string>(Path.Combine(folder,"auto-output.json"));
        var job = NeuroFiles.Read<AutoExportBinding>(Path.Combine(JobFolder(runId),"request.json"));
        if (project.Id != projectId || project.Mode != "auto" || status.Status != "completed" ||
            job.Id != runId || job.Project != projectId || job.Run != runId || job.Command != "pack" ||
            !Path.IsPathFullyQualified(output) || !string.Equals(output,job.Package,StringComparison.OrdinalIgnoreCase) ||
            !report.TryGetProperty("runId",out var id) || id.GetString() != runId ||
            !report.TryGetProperty("eligible",out var eligible) || eligible.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Neuro.ProjectId");
        return new NeuroAutoResult(project,status,reportPath,output);
    }

    private static async Task<bool> ExecuteAutoBuildAsync(RunPointer pointer,CancellationToken token)
    {
        var directory = NeuroFiles.Run(pointer.ProjectId,pointer.RunId);
        var display = Path.Combine(directory,"auto-status.json");
        var completed = Path.Combine(directory,"auto-output.json");
        var progress = new NeuroStatus("running","",0,0,0,null,"");
        try
        {
            await ExecuteTrainingAsync(pointer,token);
            var report = NeuroFiles.Read<JsonElement>(Path.Combine(directory,"report.json"));
            if (!report.GetProperty("eligible").GetBoolean())
                throw new InvalidOperationException("Neuro.QualityBelowBaseline");
            progress = NeuroFiles.Read<NeuroStatus>(Path.Combine(directory,"status.json"));
            NeuroFiles.Write(display,progress with { Status="packing", RemainingSeconds=null });
            token.ThrowIfCancellationRequested();
            var response = await ExecutePackageAsync(pointer.RunId,token);
            using var package = JsonDocument.Parse(response);
            var path = package.RootElement.GetProperty("file").GetString() ?? throw new InvalidDataException("Neuro.Package");
            NeuroFiles.Write(completed,path);
            NeuroFiles.Write(display,progress with { Status="completed" });
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NeuroFiles.Write(display,progress with { Status="failed",Message=ex.Message });
            throw new BackgroundOperationFailedException(ex.Message,ex);
        }
    }
}
