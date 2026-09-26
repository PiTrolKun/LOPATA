using System.IO;

namespace AIHub.Services;

/// <summary>Detect external edits without including their content in model context.</summary>
public static class LiteraryParagraphRevision
{
    public static string Capture(LiteraryEditorSnapshot editor)
    {
        var layout=new LiteraryProjectLayout(editor.Directory); layout.EnsurePresent();
        var paths=new List<string> { "project.json", "chapters/index.json", "Plot/Writer.json", "Plot/Advisor.json", "Jelly/память.lopata", "Import/review.json" };
        paths.AddRange(editor.Sources.Where(s=>s.Id!=editor.ActiveId).Select(s=>"chapters/"+s.FileName));
        paths.AddRange(editor.Sources.Where(s=>s.Id!=editor.ActiveId).Select(s=>"Rag/Work/"+s.Id+"/manifest.json"));
        foreach(var dir in new[]{"Rag/Source","Materials"})
        {
            var full=Path.Combine(layout.Root,dir); LiteraryProjectLayout.CheckTreePath(full);
            if(Directory.Exists(full)) paths.AddRange(Directory.EnumerateFiles(full,"*",SearchOption.TopDirectoryOnly).Select(p=>Path.GetRelativePath(layout.Root,p)));
        }
        return LiteraryWorkIndex.Revision(ParagraphJson.Encode(new { editor = editor.Revision, sources = paths.Distinct().Order().Select(rel=>
        {
            var file=Path.Combine(layout.Root,rel); LiteraryProjectLayout.CheckTreePath(file);
            if(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked project source is unsupported.");
            // Include metadata and a content hash: equal-length edits within one timer tick still invalidate the request.
            return new { rel, revision=File.Exists(file)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))):"missing" };
        }) }));
    }
}
