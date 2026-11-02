using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static class GlitchframeComponents
{
    internal sealed record Part(string Id, string Name, string Repository, string Revision, string License, ManagedModelArtifactFile[] Files);
    internal static T Read<T>(string resource)
    {
        using var stream = typeof(GlitchframeComponents).Assembly.GetManifestResourceStream("AIHub.SongClip." + resource) ?? throw new InvalidDataException("Glitchframe manifest missing.");
        return JsonSerializer.Deserialize<T>(stream)!;
    }
    internal static Part[] Parts { get; } = Read<Part[]>("Components");
    internal static ManagedModelArtifactCard[] Cards(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Model storage must be selected.");
        return Parts.Select(p => new ManagedModelArtifactCard {
            ModelArtifactId=p.Id, Family="Glitchframe", DisplayName=p.Name, Role=ManagedModelRoles.Tool,
            RepositoryId=p.Repository, Revision=p.Revision, License=p.License,
            SourcePage=p.Id.EndsWith("sdxl", StringComparison.Ordinal) ? "https://huggingface.co/"+p.Repository : "https://github.com/OlaProeis/Glitchframe",
            IsManaged=true, IsPinned=true, Origin=ManagedModelOrigins.PredefinedScenario, ModelsRoot=root,
            InstallDirectory=Path.Combine(root,"Video","SongClip",p.Id,p.Revision), Files=p.Files.ToList(),
            Consumers=[new(){Id="song_clip",DisplayName="Glitchframe",Kind="scenario"}]
        }).ToArray();
    }
    internal static async Task<(ManagedPythonSelection Python, string Libraries, string Source, string Model)> PrepareAsync(
        string root, int connections, CancellationToken token, Action<string> log)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var cards = Cards(root); var store = new ManagedModelLibraryStore();
        await ComponentLicenseGate.EnsureAsync(cards.Select(c=>c.ModelArtifactId).ToArray(), token);
        using var acquisition = new ManagedModelAcquisitionService(store) {MaximumParallelConnections=connections};
        foreach(var card in cards) {
            store.Upsert(card);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId,null,token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("SongClip.DownloadRequired");
        }
        // This ready upstream pipeline explicitly requires CUDA for SDXL generation.
        var python = await ManagedPythonRuntime.ResolveAsync("auto",token,
            new HashSet<string>{HardwareRuntimeCatalog.PythonCudaId,HardwareRuntimeCatalog.PythonCuda128Id},log);
        if (!python.Device.StartsWith("cuda",StringComparison.Ordinal)) throw new IOException("SongClip.CudaRequired");
        var model=cards.Single(c=>c.ModelArtifactId.EndsWith("sdxl",StringComparison.Ordinal));
        await RuntimeFileAudit.VerifyAsync("SongClip.SDXL",model.InstallDirectory,model.Revision,
            model.Files.Select(f=>new PinnedTreeFile(f.RelativePath,f.SizeBytes,f.Sha256)).ToArray(),token,log);
        var trees=await PrepareTreesAsync(cards,token);
        return (python,trees.Libraries,trees.Source,model.InstallDirectory);
    }
    internal static async Task<(string Libraries,string Source)> PrepareTreesAsync(IReadOnlyList<ManagedModelArtifactCard> cards,CancellationToken token,bool repair=false)
    {
        async Task<string> Tree(string suffix,string resource,bool wheels) {
            var card=cards.Single(c=>c.ModelArtifactId.EndsWith(suffix,StringComparison.Ordinal));
            var target=Path.Combine(AppDataPaths.BaseDirectory,"SongClip","Runtime",suffix,card.Revision);
            return await InstallTreeAsync(card,Read<PinnedTreeFile[]>(resource),target,wheels,token,repair);
        }
        var libraries=await Tree("libraries","LibraryTree",true);var source=await Tree("source","SourceTree",false);
        return (Path.Combine(libraries,"Lib","site-packages"),Directory.GetDirectories(source).Single());
    }
    internal static async Task<string> InstallTreeAsync(ManagedModelArtifactCard card,PinnedTreeFile[] inventory,string target,bool wheels,CancellationToken token,bool repair)
    {
        target=Path.GetFullPath(target);Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        if(Directory.Exists(target)) {
            try {await PinnedFileTreeVerifier.VerifyAsync(target,inventory,token,pythonCacheQuarantineDirectory:target+".bytecode");return target;}
            catch(Exception error) when(repair&&error is IOException or InvalidDataException) {
                // Only derived files are moved; downloaded originals and user media are untouched.
                token.ThrowIfCancellationRequested();Directory.Move(target,target+".quarantine-"+Guid.NewGuid().ToString("N"));ComponentAudit.Invalidate(target);
            }
        }
        var staging=target+"."+Guid.NewGuid().ToString("N")+".installing";Directory.CreateDirectory(staging);
        try {
            foreach(var f in card.Files)await PythonWheelExtractor.ExtractAsync(new(card.ModelArtifactId,card.Revision,f.RelativePath,f.SizeBytes,f.Sha256,new(f.SourceUrl)),Path.Combine(card.InstallDirectory,f.RelativePath),staging,token,wheels);
            await PinnedFileTreeVerifier.VerifyAsync(staging,inventory,token);token.ThrowIfCancellationRequested();Directory.Move(staging,target);ComponentAudit.Invalidate(target);
        } finally {if(Directory.Exists(staging))Directory.Delete(staging,true);}
        await PinnedFileTreeVerifier.VerifyAsync(target,inventory,token);return target;
    }
}
