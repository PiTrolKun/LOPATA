using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.OfficeEditor;

internal sealed class OfficeSheetSection
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OfficeSlideBounds? TitleBox { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OfficeSlideBounds? BodyBox { get; set; }
    public double TitleAngle { get; set; }
    public double BodyAngle { get; set; }
    public bool HideTitle { get; set; }
    public bool HideBody { get; set; }
    public List<OfficeDraftShape> Shapes { get; set; } = [];
    public List<string> ObjectOrder { get; set; } = [];
}

internal sealed class OfficeDraftShape
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public OfficeSlidePicture? Picture { get; set; }
    public OfficeSlideBounds Box { get; set; }
    public double Angle { get; set; }
    internal OfficeDraftShape Snapshot() => (OfficeDraftShape)MemberwiseClone();
}

internal sealed record OfficeSectionDraft(int Version, List<OfficeSheetSection> Sections);

internal static class OfficeSectionContent
{
    internal static List<OfficeSheetSection> Read(byte[] bytes)
    {
        if (bytes.Length == 0) return [new()];
        var draft = JsonSerializer.Deserialize<OfficeSectionDraft>(bytes);
        if (draft is not { Version: 1, Sections.Count: > 0 } || draft.Sections.Any(s => s?.Title == null || s.Body == null
            || s.TitleBox is { IsValid: false } || s.BodyBox is { IsValid: false } || s.Shapes == null || s.ObjectOrder == null ||
            s.ObjectOrder.Any(string.IsNullOrWhiteSpace) || s.Shapes.Any(o => o == null || !o.Box.IsValid || o.Text == null || string.IsNullOrWhiteSpace(o.Id) ||
                o.Picture != null && o.Picture is not ({ Extension: ".png", MimeType: "image/png", Bytes.Length: > 0 }
                    or { Extension: ".jpg", MimeType: "image/jpeg", Bytes.Length: > 0 }))))
            throw new InvalidDataException("OfficeEditor.InvalidPackage");
        return draft.Sections;
    }

    internal static byte[] ToDocument(IEnumerable<OfficeSheetSection> sections) =>
        OfficeTextFormats.PlainDocument(string.Join(Environment.NewLine + Environment.NewLine,
            sections.Select(s => s.Title.Length == 0 ? BodyText(s) : s.Title + Environment.NewLine + BodyText(s))));

    internal static string BodyText(OfficeSheetSection section) => string.Join(Environment.NewLine,
        new[] { section.Body }.Concat(section.Shapes.Where(s => s.Picture == null).Select(s => s.Text)));
}
