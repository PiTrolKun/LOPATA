using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal static class LetItOutTreePolicy
{
    internal static LetItOutTreeNode[] Apply(LetItOutTreeNode[] previous,LetItOutTreePatch patch,int through)
    {
        if(string.IsNullOrWhiteSpace(patch.Title)||patch.Title.Length>120||patch.Upsert is null||patch.Remove is null)
            throw new InvalidDataException("Invalid map patch.");
        if(patch.Upsert.Select(n=>n.Id).Distinct().Count()!=patch.Upsert.Length)throw new InvalidDataException("Duplicate node id in patch.");
        var nodes=previous.ToDictionary(n=>n.Id);
        foreach(var id in patch.Remove)nodes.Remove(id);
        foreach(var node in patch.Upsert)nodes[node.Id]=node;
        var result=nodes.Values.ToArray();Validate(result,through);return result;
    }
    internal static void Validate(LetItOutTreeNode[] entries,int through)
    {
        if(entries.Length==0||entries.Count(n=>n.Parent=="")!=1||entries.Select(n=>n.Id).Distinct().Count()!=entries.Length)
            throw new InvalidDataException("Map must have exactly one root and unique IDs.");
        var nodes=entries.ToDictionary(n=>n.Id);
        foreach(var n in entries)
        {
            if(string.IsNullOrWhiteSpace(n.Id)||n.Id.Length>80||string.IsNullOrWhiteSpace(n.Title)||n.Title.Length>120
                ||n.Detail is null||n.Detail.Length>600||n.Importance is <1 or >5||n.Sources is null
                ||n.Sources.Any(s=>s<1||s>through)||n.Sources.Distinct().Count()!=n.Sources.Length
                ||n.Kind is not ("theme" or "event" or "detail" or "interpretation")
                ||n.Status is not ("reported" or "planned" or "uncertain" or "corrected" or "suggestion" or "completed")
                ||n.Kind!="theme"&&n.Sources.Length==0)throw new InvalidDataException("Invalid node or missing source.");
            var seen=new HashSet<string>{n.Id};var current=n;
            while(current.Parent!="")
            {
                if(!nodes.TryGetValue(current.Parent,out var parent)||!seen.Add(parent.Id))throw new InvalidDataException("Missing parent or cycle.");
                current=parent;
            }
        }
    }

    internal static object Schema(int through)
    {
        var text=new{type="string"};
        var node=new{type="object",properties=new{id=text,parent=text,title=new{type="string",maxLength=120},detail=new{type="string",maxLength=600},
            kind=new{type="string",@enum=new[]{"theme","event","detail","interpretation"}},
            status=new{type="string",@enum=new[]{"reported","planned","uncertain","corrected","suggestion","completed"}},
            importance=new{type="integer",minimum=1,maximum=5},sources=new{type="array",items=new{type="integer",minimum=1,maximum=through},uniqueItems=true}},
            required=new[]{"id","parent","title","detail","kind","status","importance","sources"},additionalProperties=false};
        return new{type="object",properties=new{title=new{type="string",maxLength=120},upsert=new{type="array",items=node,maxItems=48},remove=new{type="array",items=text}},
            required=new[]{"title","upsert","remove"},additionalProperties=false};
    }
}
