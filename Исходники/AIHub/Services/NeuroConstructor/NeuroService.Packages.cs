using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services.NeuroConstructor;

public static partial class NeuroService
{
    public static async Task<(string Device,long FreeBytes)> PrepareAsync(NeuroProject p,CancellationToken token=default)
    {
        using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var audit = ComponentAudit.CaptureOperation();
        using var storage = await ModelStorageWorkGate.Current.EnterWorkAsync(token);
        try
        {
            await NeuroRuntime.ConfirmModelsAsync(p.Composition,token);
            await NeuroRuntime.AuditAsync(token);
            await NeuroRuntime.AuditModelsAsync(p.Source,p.Composition,ParentKind(p),token);
            var python = await NeuroPlacement.ResolveAsync(p,token);
            if (p.ParentPackage.Length > 0)
                await NeuroRuntime.ExecuteAsync(python,"neuro_cli.py",["inspect","--model",p.ParentPackage],token);
            return (python.Device,python.FreeBytes);
        }
        catch (Exception ex) { ComponentAuditFailures.Report(ex); throw; }
    }
    private static bool HasReadyConnection(NeuroProject p)
    {
        if (string.IsNullOrWhiteSpace(p.ParentPackage))
            return !NeuroProfiles.Full(p.Composition) && p.Connection == (p.Composition == "pair" ? "inside" : "late");
        using var zip = ZipFile.OpenRead(p.ParentPackage);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        using var json = JsonDocument.Parse(stream);
        var parent = json.RootElement;
        if (p.Composition == "trio" && parent.GetProperty("kind").GetString() == "pair") return false;
        var graph = parent.GetProperty("graph");
        var connection = graph.TryGetProperty(p.Composition == "pair" ? "pair_connection" : "topology",out var v) ? v.GetString() : "inside";
        return p.Connection == connection;
    }
    private static string ParentKind(NeuroProject p)
    {
        if (string.IsNullOrWhiteSpace(p.ParentPackage)) return "";
        return PackageKind(p.ParentPackage);
    }
    private static JsonElement PackageManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Neuro.Package");
        if (entry.Length > 4*1024*1024) throw new InvalidDataException("Neuro.Package");
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }
    private static string PackageKind(string path) =>
        PackageManifest(path).GetProperty("kind").GetString() ?? throw new InvalidDataException("Neuro.Package");
    public static Task ExportAsync(NeuroProject p,string destination,CancellationToken token=default)
    {
        if (Pending(p.Id) != null || Active.ContainsKey(p.Id)) throw new InvalidOperationException("Neuro.RunPending");
        var path = Path.GetFullPath(destination);
        if (File.Exists(path) && !string.Equals(Path.GetExtension(path),".lopatanet",StringComparison.OrdinalIgnoreCase))
            throw new IOException("Neuro.NewFileRequired");
        var targetIdentity=File.Exists(path) ? ImageShellFileLease.ReadIdentity(path) : null;
        foreach (var parent in new[]{p.ImportedFrom,p.ParentPackage}.Where(f=>!string.IsNullOrWhiteSpace(f)))
            if (string.Equals(Path.GetFullPath(parent),path,StringComparison.OrdinalIgnoreCase) ||
                targetIdentity!=null && File.Exists(parent) && ImageShellFileLease.ReadIdentity(parent)==targetIdentity)
                throw new IOException("Neuro.NewFileRequired");
        var run=Pointer(p.Id,"finished");
        if (run!=null)
        {
            if (!ReportMatches(p)) throw new InvalidOperationException("Neuro.CheckBeforeExport");
            if (!NeuroFiles.Read<JsonElement>(ReportPath(p)).GetProperty("eligible").GetBoolean())
                throw new InvalidOperationException("Neuro.QualityBelowBaseline");
        }
        else if (!HasReadyConnection(p)) throw new InvalidOperationException("Neuro.TrainingRequired");
        return StartPackageWork("pack",p.Id,path,p.Device,[],p,run,token);
    }
    private static async Task PackAsync(NeuroProject p,string? run,string path,CancellationToken token)
    {
        try
        {
            await NeuroRuntime.ConfirmModelsAsync(p.Composition,token);
            await NeuroRuntime.AuditAsync(token);
            var parentKind = ParentKind(p);
            await NeuroRuntime.AuditModelsAsync(p.Source,p.Composition,parentKind,token);
            var python = await NeuroPlacement.ResolveAsync(p,token);
            var options = Path.Combine(NeuroFiles.Project(p.Id),Guid.NewGuid().ToString("N")+".json");
            try
            {
                NeuroFiles.Write(options,NeuroPlacement.Options(p,new {routing=p.AudioPolicy switch {"needed"=>"input",_=>p.AudioPolicy},
                    channel=p.Channel switch { "cached"=>"byte","cached-continuous"=>"continuous",_=>p.Channel },
                    send=p.Channel is "cached" or "cached-continuous" ? "changed":"always",
                    device=NeuroPlacement.HasPolicy(p) ? "auto" : p.Device.StartsWith("hip",StringComparison.Ordinal) ? python.Device : p.Device}));
                var args = new List<string>{"pack","--kind",p.Composition,"--output",path,"--app-version",ApplicationBuildVersion.Value,"--settings",options};
                if (parentKind.Length > 0) args.AddRange(["--parent",p.ParentPackage]);
                if (p.Source.Length > 0 && (parentKind.Length == 0 || parentKind != p.Composition)) args.AddRange(["--source",p.Source]);
                if (run!=null)
                {
                    var dir=NeuroFiles.Run(p.Id,run);
                    args.AddRange(["--overrides",Path.Combine(dir,"learned")]);
                }
                await NeuroRuntime.ExecuteAsync(python,"neuro_cli.py",args,token);
            }
            finally { File.Delete(options); }
        }
        catch (Exception ex) { ComponentAuditFailures.Report(ex); throw; }
    }
    public static Task<string> CheckAsync(string package,string device="auto",CancellationToken token=default,string? projectId=null) =>
        StartPackageWork("check",projectId,package,device,[],null,null,token);
    public static Task<string> PredictAsync(string package,string image,string text,string[] audio,string device="auto",CancellationToken token=default,string? projectId=null)
    {
        var args = new List<string>{"--image",Path.GetFullPath(image),"--text",text};
        if(audio.Length>0) { args.Add("--audio"); args.AddRange(audio.Select(Path.GetFullPath)); }
        return StartPackageWork("predict",projectId,package,device,args,null,null,token);
    }
    private static async Task<string> InferenceAsync(string package,string command,List<string> inputs,string device,CancellationToken token)
    {
        try
        {
            var manifest = PackageManifest(package);
            var composition = manifest.GetProperty("kind").GetString() ?? throw new InvalidDataException("Neuro.Package");
            await NeuroRuntime.ConfirmModelsAsync(composition,token);
            await NeuroRuntime.AuditAsync(token);
            var settings = manifest.GetProperty("settings");
            var fromFile = device == "auto" && settings.TryGetProperty("visionDevice",out _);
            string Role(string key) => settings.TryGetProperty(key,out var value) ? value.GetString() ?? "auto" : "auto";
            var python = fromFile
                ? await NeuroPlacement.ResolveAsync(new NeuroProject { Mode="auto",Composition=composition,
                    LanguageDevice=Role("languageDevice"), VisionDevice=Role("visionDevice"), SpeechDevice=Role("speechDevice") },token)
                : await NeuroRuntime.ResolveAsync(device,token);
            var args = new List<string>{command,"--model",Path.GetFullPath(package),"--device",fromFile ? "auto" : python.Device};
            args.AddRange(inputs);
            return await NeuroRuntime.ExecuteAsync(python,"neuro_cli.py",args,token);
        }
        catch (Exception ex) { ComponentAuditFailures.Report(ex); throw; }
    }
}
