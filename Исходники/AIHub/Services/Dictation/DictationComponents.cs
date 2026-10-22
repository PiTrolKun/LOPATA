using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services.Dictation;

public static class DictationComponents
{
    public const string ModelId = "model.parakeet.tdt-v3-int8";
    public const string RuntimeId = "runtime.dictation.sherpa-onnx";
    public const string TermsId = "dictation-parakeet";
    public const string Revision = "2bda32ec70b097a55adaa07d9a7173915b43cc78";
    public sealed record Asset(string Id, string File, long Bytes, string Hash);
    public static readonly Asset[] Assets = [
        new(ModelId, "encoder.int8.onnx", 652184281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
        new("model.parakeet.decoder", "decoder.int8.onnx", 11845275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
        new("model.parakeet.joiner", "joiner.int8.onnx", 6355277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
        new("model.parakeet.tokens", "tokens.txt", 93939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")];
    public static IReadOnlyList<ComponentCatalogEntry> Entries { get; } = [
        new() { Id=RuntimeId, Name="sherpa-onnx CPU · dictation", Version="1.13.8", License="Apache-2.0; MIT",
            LicenseIds=[TermsId], Source="https://github.com/k2-fsa/sherpa-onnx", DeliveryKind=ComponentDeliveryKinds.BuiltIn },
        .. Assets.Select(a => new ComponentCatalogEntry {
            Id=a.Id, Name=a.Id==ModelId ? "Parakeet TDT 0.6B v3 INT8" : "Parakeet · "+a.File,
            Version=Revision, License="CC BY 4.0", LicenseIds=[TermsId], Source="https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            DeliveryKind=ComponentDeliveryKinds.File, FileName="parakeet-v3-"+a.File, DownloadSizeBytes=a.Bytes, InstalledSizeBytes=a.Bytes,
            Sha256=a.Hash, HealthCheckRelativePath="parakeet-v3-"+a.File,
            DownloadUrl=$"https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/{Revision}/{a.File}",
            Dependencies=a.Id==ModelId ? [RuntimeId, .. Assets.Skip(1).Select(x=>x.Id)] : [],
            Capabilities=a.Id==ModelId ? ["dictation.local"] : [] })];
    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory,"VoiceInputRuntime");
    public static ComponentSemanticPassport? Passport(string id)=>id==RuntimeId ? new(
        "Локальный исполнитель sherpa-onnx для голосового ввода на CPU. Веса Parakeet устанавливаются отдельно; запись включается только пользователем.",
        "Local sherpa-onnx CPU runtime for dictation. Parakeet weights are installed separately; only the user starts microphone capture.") : Assets.Any(a=>a.Id==id) ? new(
        "Часть Parakeet TDT v3 INT8 для локального голосового ввода, в том числе на русском и английском. Работает совместно с остальными частями и sherpa-onnx; возможны ошибки распознавания.",
        "Part of Parakeet TDT v3 INT8 for local dictation, including Russian and English. Requires the other model parts and sherpa-onnx; recognition may contain errors.") : null;
    public static string[] GetModelFiles(ComponentManager manager) => Assets.Select(a=>
        Path.Combine(manager.GetInstallDirectory(ComponentCatalog.Find(a.Id)!), "parakeet-v3-"+a.File)).ToArray();
    public static bool IsInstalled(ComponentManager manager) => GetModelFiles(manager).All(File.Exists);
    public static bool HasAcknowledgement {
        get {
            var service=new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory,"Licenses"),Path.Combine(AppDataPaths.BaseDirectory,"Licenses","receipts.json"));
            var entry=service.Entries.Single(e=>e.Id==TermsId);
            return service.ReadReceipts().Any(r=>r.Id==TermsId&&r.Terms==entry.Terms);
        }
    }

    public static async Task AuditAsync(string[] files, CancellationToken token)
    {
        if(files.Length!=Assets.Length)throw new InvalidDataException("Invalid dictation model layout.");
        for(var i=0;i<files.Length;i++) {
            var index=i;var path=Path.GetFullPath(files[index]);var asset=Assets[index];
            Task Presence(CancellationToken ct) { ct.ThrowIfCancellationRequested();if(!File.Exists(path))throw new FileNotFoundException(asset.File,path);return Task.CompletedTask; }
            async Task Full(CancellationToken ct) {
                await Presence(ct);await using var stream=File.OpenRead(path);
                if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct))!=asset.Hash)throw new InvalidDataException("Parakeet SHA256 mismatch: "+asset.File);
            }
            await ComponentAudit.RunAsync(asset.Id,Path.GetDirectoryName(path)!,Revision,Presence,Full,token);
        }
        var runtime=RuntimeDirectory;
        using var manifestStream=typeof(DictationComponents).Assembly.GetManifestResourceStream("AIHub.DictationRuntimeManifest")!;
        var manifest=System.Text.Json.JsonSerializer.Deserialize<Asset[]>(manifestStream)
            ?? throw new InvalidDataException("Dictation runtime inventory missing.");
        string RuntimeFile(Asset a)=>Path.Combine(a.File=="sherpa-onnx.dll" ? AppContext.BaseDirectory : runtime,a.File);
        Task RuntimePresence(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();foreach(var a in manifest)if(!File.Exists(RuntimeFile(a)))throw new FileNotFoundException(a.File);return Task.CompletedTask;
        }
        async Task RuntimeFull(CancellationToken ct) {
            await RuntimePresence(ct);foreach(var a in manifest) {
                await using var stream=File.OpenRead(RuntimeFile(a));
                if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct))!=a.Hash)throw new InvalidDataException("Dictation runtime SHA256 mismatch: "+a.File);
            }
        }
        await ComponentAudit.RunAsync(RuntimeId,runtime,"1.13.8",RuntimePresence,RuntimeFull,token);
    }
}
