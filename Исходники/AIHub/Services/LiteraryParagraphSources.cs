using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ParagraphMaterial(string Id, string Kind, string Revision, object Data);
public sealed record ParagraphReceipt(string Id, string Label, string Status, string Comment, string[] Materials, string Detail);
public sealed record ParagraphEvidence(IReadOnlyList<ParagraphMaterial> Materials, IReadOnlyList<ParagraphReceipt> Receipts)
{
    public bool Complete => Receipts.All(r=>r.Status is "found" or "empty" or "partial");
}

/// <summary>Program-controlled read capabilities, independent of model tool willingness.</summary>
public sealed class LiteraryParagraphSources(LiteraryProject project, LiteraryEditorSnapshot editor, LiteraryParagraphCatalog catalog)
{
    private readonly LiteraryProjectLayout _layout = new(editor.Directory);
    private readonly Dictionary<string,ParagraphMaterial> _materials = [];
    private readonly Dictionary<string,Task<string[]>> _reads = [];
    private IReadOnlyDictionary<string,ParagraphSelection> _selection = new Dictionary<string,ParagraphSelection>();

    public async Task<ParagraphEvidence> ReadAsync(string query, IReadOnlyDictionary<string,ParagraphSelection> selected,
        Action<ParagraphReceipt> progress, CancellationToken ct)
    {
        _selection=selected;
        var nodes=catalog.Nodes; var receipts=new List<ParagraphReceipt>();
        foreach(var (id,choice) in selected.Where(x=>x.Value.Selected))
            progress(new(id,nodes.GetValueOrDefault(id)?.Label??id,"not_read",choice.Comment,[],"Reading has not executed."));
        foreach(var (id,choice) in selected.Where(x=>x.Value.Selected))
        {
            ct.ThrowIfCancellationRequested();
            var comments=selected.Where(x=> (id==x.Key || id.StartsWith(x.Key+"/",StringComparison.Ordinal)) && x.Value.Comment.Length>0)
                .Select(x=>x.Value.Comment);
            var comment=string.Join("\n",comments);
            ParagraphReceipt receipt;
            try
            {
                if(!nodes.TryGetValue(id,out var node)) throw new IOException("Selected scope is no longer in this project.");
                _excludedParts.Clear();
                var ids=await ReadNode(node,query+(comment.Length>0?"\n"+comment:""),ct);
                receipt=new(id,node.Label,_excludedParts.Count > 0 ? "partial" : ids.Length>0?"found":"empty",comment,ids,
                    _excludedParts.Count > 0 ? string.Join(", ", _excludedParts.Order()) : "Bounded search is not a complete audit. Empty results do not prove absence.");
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) { receipt=new(id,nodes.GetValueOrDefault(id)?.Label??id,"error",comment,[],ex.Message); }
            receipts.Add(receipt); progress(receipt);
        }
        return new(_materials.Values.ToArray(),receipts);
    }
    private readonly HashSet<string> _excludedParts = [];
    private Task<string[]> ReadNode(ParagraphSource node,string query,CancellationToken ct)
    {
        var key=node.Id+"\n"+query;
        if(_reads.TryGetValue(key,out var cached)) return cached;
        return _reads[key]=ReadCore(node,query,ct);
    }
    private string Add(string id,string kind,object data)
    {
        var revision=LiteraryWorkIndex.Revision(ParagraphJson.Encode(data));
        var key=id+"@"+revision;
        _materials.TryAdd(key,new(key,kind,revision,data)); return key;
    }
    private async Task<string[]> ReadCore(ParagraphSource node,string query,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); _layout.EnsurePresent();
        if(node.Error is not null) throw new IOException(node.Error);
        if(node.Kind=="group")
        {
            var all=new List<string>();
            foreach(var child in node.Children)
            {
                var comment=_selection.GetValueOrDefault(child.Id)?.Comment??"";
                all.AddRange(await ReadNode(child,query+(comment.Length>0?"\n"+comment:""),ct));
            }
            return all.Distinct().ToArray();
        }
        if(node.Kind=="creation")
        {
            var doc=new LiteraryCalibrationDocument(project.CreationBrief);
            var text=node.Key=="plain"?project.CreationBrief:doc.Fields[int.Parse(node.Key)].Value;
            if(string.IsNullOrWhiteSpace(text)) return [];
            return [Add(node.Id,node.Id.StartsWith("route/")?"future_route":"confirmed_creation_intent",new { label=node.Label,text,
                source="project.json / CreationBrief", sourceRevision=LiteraryWorkIndex.Revision(project.CreationBrief) })];
        }
        if(node.Kind=="anchor")
        {
            var parts=node.Key.Split('/'); var anchor=new LiteraryPlotAnchorStore(_layout,Enum.Parse<LiteraryChatProfile>(parts[0])).Load();
            var text=parts[1]=="avoid"?anchor.Avoid:anchor.Text;
            return string.IsNullOrWhiteSpace(text)?[]:[Add(node.Id,"author_anchor",new { role=parts[0],purpose=parts[1],text,sourceRevision=anchor.Revision })];
        }
        if(node.Kind=="chapter")
        {
            var reader=new LiteraryProjectReader(editor); var offset=0; var fragments=new List<string>(); string? revision=null;
            do
            {
                var json=JsonNode.Parse(reader.Execute(new("read",node.Key,offset),ct).Json)!;
                if(json["error"] is not null) throw new IOException(json["error"]!.ToString());
                if(revision is not null && revision!=json["revision"]!.ToString()) throw new IOException("Chapter changed during reading.");
                revision=json["revision"]!.ToString();
                fragments.Add(Add("chapters/"+node.Key+"/"+offset,"completed_project_part",json));
                if(json["nextOffset"] is null) break;
                offset=json["nextOffset"]!.GetValue<int>();
            } while(true);
            return fragments.ToArray();
        }
        if(node.Kind.StartsWith("rag"))
        {
            var reference=node.Kind is "ragReference" or "ragSection";
            var json=JsonNode.Parse(await new LiteraryRagReader(editor).SearchScopedAsync(query,reference,node.Key,node.Kind=="ragSection",ct))!;
            if(json["error"] is not null) throw new IOException(json["error"]!.ToString());
            var exclusions = json["excluded"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
            foreach (var part in exclusions) _excludedParts.Add(part);
            var notices = exclusions.Length == 0 ? Array.Empty<string>() : new[] { Add(node.Id+"/coverage", "source_coverage",
                new { excludedParts = exclusions, note = "Unreviewed import passages excluded. The search does not cover the entire book." }) };
            var matches=json["matches"]?.AsArray() ?? throw new InvalidDataException("Invalid RAG result.");
            return matches.Select(m=>m!["fragment"]!).Select(f=>Add("rag/"+f["kind"]+"/"+f["number"]+"/"+f["offset"],
                reference?"original_book_fragment":"completed_project_fragment",f.DeepClone())).Concat(notices).ToArray();
        }
        if(node.Kind.StartsWith("jelly"))
        {
            var rows=new LiteraryJellyStore(_layout).Read();
            var keys=node.Kind=="jellyKind"?new[]{node.Key}:node.Kind=="jelly"?[]:JsonSerializer.Deserialize<string[]>(node.Key)!;
            var scoped=rows.Where(e=>keys.Length==0 || (e.Fact.Kind==keys[0] && (keys.Length<2 || e.Fact.Subject==keys[1])
                && (keys.Length<3 || e.Fact.Relation==keys[2]) && (keys.Length<4 || e.PartId==keys[3]))).ToArray();
            foreach(var group in scoped.GroupBy(e=>e.PartId))
            {
                var part=editor.Sources.SingleOrDefault(p=>p.Id==group.Key && p.Finished && p.Id!=editor.ActiveId)
                    ?? throw new IOException("Memory references an unavailable completed part.");
                var data=JsonNode.Parse(new LiteraryProjectReader(editor).Execute(new("read",part.Number),ct).Json)!;
                if(data["error"] is not null || group.Any(e=>e.Revision!=data["revision"]?.ToString())) throw new IOException("Memory references an outdated part revision.");
            }
            var words=Regex.Matches(query,@"[\p{L}\p{N}]{3,}").Select(m=>m.Value[..Math.Min(5,m.Length)]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ranked=scoped.OrderByDescending(e=>words.Count(w=>(e.Fact.Subject+" "+e.Fact.Relation+" "+e.Fact.Value).Contains(w,StringComparison.OrdinalIgnoreCase))).Take(12);
            return ranked.Select(e=>Add("jelly/"+e.Id,"confirmed_project_memory",new { e.Id,e.Number,e.Revision,e.Version,e.Fact,
                limit=12,note="Bounded confirmed project memory; draft may describe later events. Plans and beliefs are not completed events." })).ToArray();
        }
        throw new InvalidDataException("Unsupported source scope.");
    }
}
