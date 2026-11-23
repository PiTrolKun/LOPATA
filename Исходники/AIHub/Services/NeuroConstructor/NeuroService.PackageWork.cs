using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.NeuroConstructor;

public static partial class NeuroService
{
    internal const string PackageKindId = "neuroconstructor.package";
    private sealed record Stamp(bool Exists,long Length,long Modified)
    {
        internal static Stamp Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(true,file.Length,file.LastWriteTimeUtc.Ticks) : new(false,0,0);
        }
    }
    private sealed record PackageJob(string Id,string Command,string? Project,string Package,string Device,
        List<string> Inputs,NeuroProject? Settings,string? Run,Dictionary<string,Stamp> Originals);
    private static string JobFolder(string id) => Guid.TryParseExact(id,"N",out _)
        ? Path.Combine(NeuroFiles.Root,"operations",id) : throw new InvalidDataException("Neuro.ProjectId");
    internal static string PackageResult(string id) => Path.Combine(JobFolder(id),"result.json");
    private static Task<string> StartPackageWork(string command,string? project,string package,string device,
        List<string> inputs,NeuroProject? settings,string? run,CancellationToken token)
    {
        var id=Guid.NewGuid().ToString("N");
        var path=Path.GetFullPath(package);
        var originals=new Dictionary<string,Stamp>(StringComparer.OrdinalIgnoreCase){[path]=Stamp.Read(path)};
        if (command=="predict")
        {
            // The image and optional audio files are positional values of this fixed CLI contract.
            originals[inputs[1]]=Stamp.Read(inputs[1]);
            foreach (var audio in inputs.Skip(5)) originals[audio]=Stamp.Read(audio);
        }
        var job=new PackageJob(id,command,project,path,device,inputs,settings,run,originals);
        NeuroFiles.Write(Path.Combine(JobFolder(id),"request.json"),job);
        return RunPackageAsync(id,project,null,token);
    }
    internal static Task RestorePackageAsync(BackgroundOperationState state,CancellationToken token) =>
        RunPackageAsync(state.Input.GetProperty("Job").GetString()!,state.Project,state,token);
    private static Task<string> RunPackageAsync(string id,string? project,BackgroundOperationState? restored,CancellationToken token) =>
        ApplicationBackgroundOperations.RunAsync(PackageKindId,"Нейроконструктор",project,new { Job=id },
            ct=>ExecutePackageAsync(id,ct),token,restored);
    private static async Task<string> ExecutePackageAsync(string id,CancellationToken token)
    {
        var folder=JobFolder(id);
        var result=PackageResult(id);
        var job=NeuroFiles.Read<PackageJob>(Path.Combine(folder,"request.json"));
        if (job.Id!=id || job.Command is not ("pack" or "predict" or "check")) throw new InvalidDataException("Neuro.Package");
        if (job.Command!="pack" && File.Exists(result)) return NeuroFiles.Read<JsonElement>(result).GetRawText();
        try
        {
            string response;
            if (job.Command=="pack") response=await PublishAsync(job,folder,token);
            else
            {
                foreach (var (input,stamp) in job.Originals)
                    if (Stamp.Read(input)!=stamp) throw new IOException("Neuro.InputChanged");
                response=await InferenceAsync(job.Package,job.Command,job.Inputs,job.Device,token);
            }
            var json=JsonSerializer.Deserialize<JsonElement>(response);
            NeuroFiles.Write(result,json);
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new BackgroundOperationFailedException(ex.Message,ex); }
    }
    private static async Task<string> PublishAsync(PackageJob job,string folder,CancellationToken token)
    {
        var receipt=Path.Combine(folder,"export.json");
        var staged=job.Package+"."+job.Id+".pending";
        var path=job.Package;
        static async Task<string> Digest(string file,CancellationToken ct)
        {
            using var input=File.OpenRead(file);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct));
        }
        if (!File.Exists(receipt))
        {
            if (job.Originals[path]!=Stamp.Read(path)) throw new IOException("Neuro.OutputChanged");
            if (!File.Exists(staged)) await PackAsync(job.Settings!,job.Run,staged,token);
            NeuroFiles.Write(receipt,await Digest(staged,token));
        }
        var expected=NeuroFiles.Read<string>(receipt);
        if (File.Exists(staged))
        {
            if (job.Originals[path]!=Stamp.Read(path)) throw new IOException("Neuro.OutputChanged");
            if (await Digest(staged,token)!=expected) throw new InvalidDataException("Neuro.Package");
            File.Move(staged,path,job.Originals[path].Exists);
        }
        else if (!File.Exists(path) || await Digest(path,token)!=expected) throw new IOException("Neuro.OutputChanged");
        return JsonSerializer.Serialize(new { file=path,sha256=expected });
    }
}
