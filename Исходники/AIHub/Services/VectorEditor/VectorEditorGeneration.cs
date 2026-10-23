using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VectorEditor;

public sealed record VectorEditorContext(string ProjectId, string SheetId, bool InsertOnCanvas,
    string? EditedObjectId = null, string? SourceSvg = null, string? SourceHash = null, string? StyleSvg = null);

public static class VectorEditorGeneration
{
    public const string BackgroundKind = "vector-editor.generate";
    public static string Hash(string svg) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(VectorSvg.Parse(svg).ToString(System.Xml.Linq.SaveOptions.DisableFormatting))));
    public static string Compose(VectorRequest request)
    {
        var context = request.Editor;
        if (context is null) return VectorPrompt.Compose(request.Prompt, request.Options);
        var instruction = context.EditedObjectId is null
            ? "Create one self-contained visual object/module for insertion into a vector composition. The module may contain any number of shapes, groups, paths, details and definitions."
            : "Edit the supplied selected SVG object according to the user's instruction. Return its complete new version as one visual object/module in one standalone SVG file. Retain its coordinate frame so the edited version can replace the selection.";
        var prompt = instruction + "\nUser request:\n" + request.Prompt;
        if (context.SourceSvg is { } source) { _ = VectorSvg.Parse(source); prompt += "\nSelected object SVG:\n" + source; }
        if (context.StyleSvg is { } style) { _ = VectorSvg.Parse(style); prompt += "\nSVG style reference (use its visual language for the new module):\n" + style; }
        return VectorPrompt.Compose(prompt, request.Options);
    }
}
