using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ParagraphRoute(string Id,string Title,string Description);

public sealed class ParagraphSource(string id, string label, string kind, string key = "")
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public List<ParagraphSource> Children { get; } = [];
    public string? Error { get; set; }
    public IEnumerable<ParagraphSource> All() => new[] { this }.Concat(Children.SelectMany(c => c.All()));
}

/// <summary>Only IDs and labels from this catalog may reach an unchecked-source recommendation.</summary>
public sealed class LiteraryParagraphCatalog
{
    public List<ParagraphSource> Roots { get; } = [];
    public Dictionary<string,ParagraphRoute> Routes { get; } = [];
    public Dictionary<string, ParagraphSource> Nodes => Roots.SelectMany(r => r.All()).ToDictionary(n => n.Id);
    public LiteraryParagraphCatalog(LiteraryProject project, LiteraryEditorSnapshot editor, Func<string,string> l)
    {
        var labels = new LiteraryCalibrationLabels(l);
        var creation = new ParagraphSource("creation", l("Paragraph.Source.Creation"), "group"); Roots.Add(creation);
        var document = new LiteraryCalibrationDocument(project.CreationBrief);
        if (document.Structured)
            foreach (var topic in document.Fields.Select((f,i)=>(f,i)).Where(x=>x.f.Topic!=8).GroupBy(x=>x.f.Topic))
            {
                var group = new ParagraphSource("creation/topic/"+topic.Key, l("Literary.Interview.Topic"+topic.Key), "group"); creation.Children.Add(group);
                foreach (var (field,index) in topic) group.Children.Add(new("creation/field/"+index, labels.Get(field), "creation", index.ToString()));
            }
        else creation.Children.Add(new("creation/plain", l("Paragraph.Source.Creation"), "creation", "plain"));
        var anchors = new ParagraphSource("anchors",l("Paragraph.Source.Anchors"),"group"); Roots.Add(anchors);
        foreach (var role in new[]{LiteraryChatProfile.Writer,LiteraryChatProfile.Advisor})
        {
            var node = new ParagraphSource("anchors/"+role,l("Paragraph."+role),"group"); anchors.Children.Add(node);
            node.Children.Add(new(node.Id+"/include",l("Paragraph.Include"),"anchor",role+"/include"));
            node.Children.Add(new(node.Id+"/avoid",l("Paragraph.Avoid"),"anchor",role+"/avoid"));
        }
        var chapters = new ParagraphSource("chapters",l("Paragraph.Source.Chapters"),"group"); Roots.Add(chapters);
        foreach (var p in editor.Sources.Where(s=>s.Finished && s.Id!=editor.ActiveId))
            chapters.Children.Add(new("chapters/"+p.Id,p.Number+" · "+p.Title,"chapter",p.Number));
        var rag = new ParagraphSource("rag",l("Paragraph.Source.Rag"),"group"); Roots.Add(rag);
        var references = new ParagraphSource("rag/reference",l("Paragraph.Source.Reference"),"ragReference"); rag.Children.Add(references);
        try
        {
            foreach (var source in new LiteraryRagReader(editor).ReferenceScopes().GroupBy(s=>s.Source))
            {
                var group = new ParagraphSource(references.Id+"/"+LiteraryWorkIndex.Revision(source.Key),source.Key,"ragReference",source.Key);
                references.Children.Add(group);
                foreach(var section in source) group.Children.Add(new(group.Id+"/"+section.Number,section.Section,"ragSection",section.Number));
            }
        }
        catch(Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
        { references.Error = ex.Message; }
        var projectRag = new ParagraphSource("rag/project",l("Paragraph.Source.ProjectRag"),"ragProject"); rag.Children.Add(projectRag);
        foreach(var p in editor.Sources.Where(s=>s.Finished && s.Id!=editor.ActiveId))
            projectRag.Children.Add(new(projectRag.Id+"/"+p.Id,p.Number+" · "+p.Title,"ragPart",p.Id));
        var jelly = new ParagraphSource("jelly",l("Paragraph.Source.Jelly"),"jelly"); Roots.Add(jelly);
        try
        {
            foreach(var kind in new LiteraryJellyStore(new(editor.Directory)).Read().GroupBy(e=>e.Fact.Kind))
            {
                var kn = new ParagraphSource("jelly/"+kind.Key,l("Literary.Jelly.Kind."+kind.Key),"jellyKind",kind.Key); jelly.Children.Add(kn);
                foreach(var subject in kind.GroupBy(e=>e.Fact.Subject))
                {
                    var sn = new ParagraphSource(kn.Id+"/"+LiteraryWorkIndex.Revision(subject.Key),subject.Key,"jellySubject",ParagraphJson.Encode(new[]{kind.Key,subject.Key})); kn.Children.Add(sn);
                    foreach(var relation in subject.GroupBy(e=>e.Fact.Relation))
                    {
                        var rn = new ParagraphSource(sn.Id+"/"+LiteraryWorkIndex.Revision(relation.Key),relation.Key,"jellyRelation",ParagraphJson.Encode(new[]{kind.Key,subject.Key,relation.Key})); sn.Children.Add(rn);
                        foreach(var part in relation.GroupBy(e=>e.PartId)) rn.Children.Add(new(rn.Id+"/"+part.Key,part.First().Number,"jellyPart",ParagraphJson.Encode(new[]{kind.Key,subject.Key,relation.Key,part.Key})));
                    }
                }
            }
        }
        catch(Exception ex) { jelly.Error=ex.Message; }
        var route = new ParagraphSource("route",l("Paragraph.Source.Route"),"group"); Roots.Add(route);
        foreach(var (field,index) in document.Fields.Select((f,i)=>(f,i)).Where(x=>x.f.Topic==8 && x.f.Label.StartsWith("route-title:")))
        {
            var number=field.Label.Split(':')[1];
            var node=new ParagraphSource("route/"+number,number+" · "+field.Value,"group"); route.Children.Add(node);
            node.Children.Add(new(node.Id+"/title",l("Paragraph.RouteTitle"),"creation",index.ToString()));
            var description=document.Fields.FindIndex(f=>f.Label=="route-description:"+number);
            if(description>=0) node.Children.Add(new(node.Id+"/description",l("Paragraph.RouteDescription"),"creation",description.ToString()));
            Routes.Add(node.Id,new(node.Id,field.Value,description>=0?document.Fields[description].Value:""));
        }
    }
}
