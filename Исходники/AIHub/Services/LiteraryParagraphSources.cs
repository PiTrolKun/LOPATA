using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ParagraphMaterial(string Id, string Kind, string Revision, object Data);
public sealed record ParagraphReceipt(string Id, string Label, string Status, string Comment, string[] Materials, string Detail);
public sealed record ParagraphEvidence(IReadOnlyList<ParagraphMaterial> Materials, IReadOnlyList<ParagraphReceipt> Receipts)
{
    public bool Complete => Receipts.All(r=>r.Status is "found" or "empty" or "partial");
}

/// <summary>Program-controlled read capabilities, independent of model tool willingness.</summary>
public sealed class LiteraryParagraphSources(LiteraryProject project, LiteraryEditorSnapshot editor, LiteraryParagraphCatalog catalog,
    Func<string,string>? localize = null)
{
    private readonly LiteraryProjectLayout _layout = new(editor.Directory);
    private readonly Dictionary<string,ParagraphMaterial> _materials = [];
    private readonly Dictionary<string,Task<string[]>> _reads = [];
    private IReadOnlyList<LiteraryJellyEntry>? _jellyRows;
    private readonly Dictionary<(string Kind, string Key), List<LiteraryJellyEntry>> _jellyScopes = [];
    private readonly Dictionary<string, string> _jellyPartRevisions = [];
    private IReadOnlyDictionary<string,ParagraphSelection> _selection = new Dictionary<string,ParagraphSelection>();

    public async Task<ParagraphEvidence> ReadAsync(string query, IReadOnlyDictionary<string,ParagraphSelection> selected,
        Action<ParagraphReceipt> progress, CancellationToken ct)
    {
        _materials.Clear(); _reads.Clear(); _jellyScopes.Clear(); _jellyPartRevisions.Clear(); _jellyRows = null;
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
                var ids=await ReadNode(node,query+(comment.Length>0?"\n"+comment:""),ct);
                var excluded = ids.Select(key => _materials[key]).Where(m => m.Kind == "source_coverage")
                    .SelectMany(m => JsonSerializer.SerializeToElement(m.Data).GetProperty("excludedParts").EnumerateArray().Select(x => x.GetString()!))
                    .Distinct().Order().ToArray();
                var searchOnly = node.All().Any(n => n.Kind.StartsWith("rag", StringComparison.Ordinal));
                var detail = searchOnly ? localize?.Invoke("Paragraph.Receipt.SearchOnly") ?? "Search excerpts only; the selected sources were not read in full. Empty matches do not prove absence."
                    : "All available records in the selected scope were read.";
                if (excluded.Length > 0) detail += " " + string.Format(localize?.Invoke("Paragraph.Receipt.Excluded") ?? "Unreviewed parts excluded: {0}.", string.Join(", ", excluded));
                receipt=new(id,node.Label,searchOnly || excluded.Length > 0 ? "partial" : ids.Length>0?"found":"empty",comment,ids,detail);
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) { receipt=new(id,nodes.GetValueOrDefault(id)?.Label??id,"error",comment,[],ex.Message); }
            receipts.Add(receipt); progress(receipt);
        }
        return new(_materials.Values.ToArray(),receipts);
    }
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
            var notices = exclusions.Length == 0 ? Array.Empty<string>() : new[] { Add(node.Id+"/coverage", "source_coverage",
                new { excludedParts = exclusions, note = "Unreviewed import passages excluded. The search does not cover the entire book." }) };
            var matches=json["matches"]?.AsArray() ?? throw new InvalidDataException("Invalid RAG result.");
            return matches.Select(m=>m!["fragment"]!).Select(f=>Add("rag/"+f["kind"]+"/"+f["number"]+"/"+f["offset"],
                reference?"original_book_fragment":"completed_project_fragment",f.DeepClone())).Concat(notices).ToArray();
        }
        if(node.Kind.StartsWith("jelly"))
        {
            var scoped=JellyScope(node,ct);
            foreach(var group in scoped.GroupBy(e=>e.PartId))
            {
                ct.ThrowIfCancellationRequested();
                if (!_jellyPartRevisions.TryGetValue(group.Key, out var revision))
                {
                    var part=editor.Sources.SingleOrDefault(p=>p.Id==group.Key && p.Finished && p.Id!=editor.ActiveId)
                        ?? throw new IOException("Memory references an unavailable completed part.");
                    var data=JsonNode.Parse(new LiteraryProjectReader(editor).Execute(new("read",part.Number),ct).Json)!;
                    if(data["error"] is not null) throw new IOException("Memory references an unavailable or unreviewed part.");
                    _jellyPartRevisions[group.Key] = revision = data["revision"]!.ToString();
                }
                if(group.Any(e=>e.Revision!=revision)) throw new IOException("Memory references an outdated part revision.");
            }
            var result = new List<string>();
            foreach (var entry in scoped)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(Add("jelly/"+entry.Id,"confirmed_project_memory",new { entry.Id,entry.Number,entry.Revision,entry.Version,entry.Fact,
                    note="Confirmed project memory in the selected scope; draft may describe later events. Plans and beliefs are not completed events." }));
            }
            return result.ToArray();
        }
        throw new InvalidDataException("Unsupported source scope.");
    }

    private IReadOnlyList<LiteraryJellyEntry> JellyScope(ParagraphSource node, CancellationToken ct)
    {
        if (_jellyRows is null)
        {
            // Build the selection index once per request. Overlapping selected ancestors and
            // descendants share the same fact IDs, source validation and material entries.
            var rows = new LiteraryJellyStore(_layout).Read();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                Index("jellyKind", row.Fact.Kind, row);
                Index("jellySubject", ParagraphJson.Encode(new[] { row.Fact.Kind, row.Fact.Subject }), row);
                Index("jellyRelation", ParagraphJson.Encode(new[] { row.Fact.Kind, row.Fact.Subject, row.Fact.Relation }), row);
                Index("jellyPart", ParagraphJson.Encode(new[] { row.Fact.Kind, row.Fact.Subject, row.Fact.Relation, row.PartId }), row);
            }
            _jellyRows = rows;
        }
        return node.Kind == "jelly" ? _jellyRows : _jellyScopes.GetValueOrDefault((node.Kind, node.Key)) ?? [];

        void Index(string kind, string key, LiteraryJellyEntry row)
        {
            if (!_jellyScopes.TryGetValue((kind, key), out var values)) _jellyScopes[(kind, key)] = values = [];
            values.Add(row);
        }
    }
}
