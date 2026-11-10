using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models.OneC;
using Lopata.Updates;

namespace AIHub.Services.OneC;

internal sealed partial class OneCProjectStore
{
    private sealed record Ownership(string ProjectId, string FileId, string OperationId, string Name);
    private string Receipt(string project, string file)
    {
        Id(project); Id(file);
        var path = Path.Combine(Root, ".ownership", project, file + ".json");
        SafeUpdatePath.RejectLinks(path); return path;
    }
    internal bool CanEdit(OneCProject project, OneCFile file)
    {
        try { RequireOwned(project, file); return true; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return false; }
    }
    private void RequireOwned(OneCProject project, OneCFile file)
    {
        if (file.OwnershipUnavailable || file.Origin != "created" || !project.Files.Any(f => ReferenceEquals(f, file))) throw new InvalidDataException("OneC.ReadOnly");
        Ownership receipt;
        try { receipt = JsonSerializer.Deserialize<Ownership>(File.ReadAllText(Receipt(project.Id, file.Id)))
            ?? throw new InvalidDataException("OneC.ReadOnly"); }
        catch (Exception e) when (e is IOException or JsonException) { throw new InvalidDataException("OneC.ReadOnly", e); }
        if (receipt.ProjectId != project.Id || receipt.FileId != file.Id || receipt.OperationId != file.CreatedOperationId
            || receipt.Name != file.Name) throw new InvalidDataException("OneC.ReadOnly");
        if (file.DiskPath.Length > 0 && !file.DiskPath.StartsWith("owned/" + file.Id + "/", StringComparison.Ordinal))
            throw new InvalidDataException("OneC.InvalidPath");
    }
    internal OneCFile AddMaterial(OneCProject project, string name, string code, string encoding = "utf-8")
    {
        if (code.Length is < 1 or > 2_000_000) throw new InvalidDataException("OneC.InputLimit");
        var file = new OneCFile { Name = Name(name), Code = code, Encoding = encoding, SavedCodeHash = Hash(code) };
        project.Files.Add(file);
        project.History.Add(new(OneCIds.New(), "importMaterial", file.Id, DateTimeOffset.UtcNow));
        Save(project); return file;
    }
    internal OneCFile Import(OneCProject project, string path)
    {
        SafeUpdatePath.RejectLinks(path);
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("OneC.InputLimit");
        var allowed = new[] { ".bsl", ".os", ".xml", ".txt", ".md", ".json" };
        if (!allowed.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("OneC.UnsupportedFormat");
        var bytes = File.ReadAllBytes(path);
        Encoding encoding;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else
        {
            encoding = new UTF8Encoding(false, true);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
            try { _ = encoding.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException)
            { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); encoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        }
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidDataException("OneC.UnsupportedFormat");
        return AddMaterial(project, Path.GetFileName(path), text, encoding.WebName);
    }
    internal OneCFile CreateFile(OneCProject project, string name, string code, OutputModelUse[]? models = null, string proposalId = "")
    {
        Name(name);
        if (!new[] { ".bsl", ".os", ".txt", ".md" }.Contains(Path.GetExtension(name).ToLowerInvariant()))
            throw new InvalidDataException("OneC.UnsupportedFormat");
        if (project.Files.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("OneC.NameExists");
        if (code.Length > 2_000_000) throw new InvalidDataException("OneC.InputLimit");
        var operation = OneCIds.New();
        var file = new OneCFile { Name = name, Origin = "created", CreatedOperationId = operation, Code = code,
            Models = models ?? [], SourceProposalId = proposalId };
        // The creation receipt is outside the project manifest. Importing or changing an origin flag cannot create it.
        Atomic(Receipt(project.Id, file.Id), JsonSerializer.Serialize(new Ownership(project.Id, file.Id, operation, name)));
        project.Files.Add(file); project.History.Add(new(operation, "createOwnedFile", file.Id, DateTimeOffset.UtcNow));
        Save(project); return file;
    }
    private void SaveOwnedRevision(OneCProject project, OneCFile file)
    {
        var codeHash = Hash(file.Code);
        var version = OneCIds.New();
        var relative = "owned/" + file.Id + "/" + version + "/" + file.Name;
        var destination = FileFor(project.Id, relative);
        var facts = new OutputProvenance { Scenario = "onec/owned-file", CreatedUtc = DateTimeOffset.UtcNow,
            ModelHistory = file.Models.Length > 0 ? "recorded" : "not-applicable", Models = file.Models };
        var prefix = Path.GetExtension(file.Name).ToLowerInvariant() switch
        { ".md" => "<!-- LOPATA " + facts.Json() + " -->\n", ".txt" => "# LOPATA " + facts.Json() + "\n", _ => "// LOPATA " + facts.Json() + "\n" };
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(Encoding.UTF8.GetBytes(prefix + file.Code)); stream.Flush(true); }
        file.DiskPath = relative; file.DiskHash = HashFile(destination); file.SavedCodeHash = codeHash;
    }
    internal void VerifyDisk(OneCProject project, OneCFile file)
    {
        var path = FileFor(project.Id, file.DiskPath);
        if (!File.Exists(path) || HashFile(path) != file.DiskHash) throw new InvalidDataException("OneC.FileChanged");
    }
    internal static string CompleteCode(OneCProposal proposal)
    {
        var b = proposal.Binding;
        return b.WholeFile || b.FileId.Length == 0 ? proposal.Code
            : b.FullSource[..b.Start] + proposal.Code + b.FullSource[(b.Start + b.Length)..];
    }
    internal OneCFile CreateFromProposal(OneCProject project, OneCProposal proposal, string name)
    {
        if (!project.Proposals.Contains(proposal)) throw new InvalidDataException("OneC.InvalidProject");
        return CreateFile(project, name, CompleteCode(proposal), proposal.Models, proposal.Id);
    }
    internal OneCFile Apply(OneCProject project, OneCProposal proposal)
    {
        if (!project.Proposals.Contains(proposal)) throw new InvalidDataException("OneC.InvalidProject");
        if (proposal.AppliedFileId.Length > 0) return project.Files.Single(f => f.Id == proposal.AppliedFileId);
        if (string.IsNullOrWhiteSpace(proposal.Code) || proposal.MissingContext.Length > 0) throw new InvalidDataException("OneC.MissingContext");
        var file = project.Files.SingleOrDefault(f => f.Id == proposal.Binding.FileId) ?? throw new InvalidDataException("OneC.ReadOnly");
        RequireOwned(project, file);
        if (OneCProtocol.Stale(project, proposal)) throw new InvalidOperationException("OneC.StaleProposal");
        file.Code = CompleteCode(proposal); file.Models = proposal.Models; file.SourceProposalId = proposal.Id;
        proposal.AppliedFileId = file.Id;
        project.History.Add(new(OneCIds.New(), "applyProposal", proposal.Id, DateTimeOffset.UtcNow));
        Save(project); return file;
    }
}
