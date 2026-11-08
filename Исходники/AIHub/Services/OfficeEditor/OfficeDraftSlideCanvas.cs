namespace AIHub.Services.OfficeEditor;

// Project drafts use the same 16:9 coordinate space as their PowerPoint export.
internal static class OfficeDraftSlideCanvas
{
    internal const int Width = 12192000, Height = 6858000;
    internal static OfficeSlideBounds Title(OfficeSheetSection section) =>
        section.TitleBox ?? new OfficeSlideBounds(640000, 360000, 10912000, 1040000);
    internal static OfficeSlideBounds Body(OfficeSheetSection section) =>
        section.BodyBox ?? new OfficeSlideBounds(640000, 1550000, 10912000, 4700000);

    internal static OfficeSlideCanvasModel Open(OfficeSheetSection section, string title, string body)
    {
        var objects = new List<OfficeSlideObject>();
        void Move(string id, bool front)
        {
            var order = objects.Select(o => o.Id).Where(key => key != id).ToList();
            order.Insert(front ? order.Count : 0, id);
            section.ObjectOrder = order;
        }
        if (!section.HideTitle) objects.Add(new("Title", title, false, () => Title(section), value => section.TitleBox = value,
            showEmptyOutline: () => string.IsNullOrWhiteSpace(section.Title))
            { Angle = () => section.TitleAngle, Rotate = d => section.TitleAngle = (section.TitleAngle+d+360)%360,
              TextBlocks = () => [new(() => section.Title, text => section.Title = text)],
              ChangeOrder = front => Move("Title", front), Delete = () => { section.HideTitle = true; section.Title = ""; } });
        if (!section.HideBody) objects.Add(new("Body", body, false, () => Body(section), value => section.BodyBox = value,
            showEmptyOutline: () => string.IsNullOrWhiteSpace(section.Body))
            { Angle = () => section.BodyAngle, Rotate = d => section.BodyAngle = (section.BodyAngle+d+360)%360,
              TextBlocks = () => [new(() => section.Body, text => section.Body = text)],
              ChangeOrder = front => Move("Body", front), Delete = () => { section.HideBody = true; section.Body = ""; } });
        foreach (var shape in section.Shapes)
            objects.Add(new(shape.Id, shape.Text, shape.Picture != null, () => shape.Box, b => shape.Box = b,
                shape.Picture == null ? null : picture => shape.Picture = picture)
                { Delete = () => section.Shapes.Remove(shape), Angle = () => shape.Angle,
                  TextBlocks = shape.Picture == null ? () => [new(() => shape.Text, text => shape.Text = text)] : null,
                  ChangeOrder = front => Move(shape.Id, front),
                  Rotate = d => shape.Angle = (shape.Angle+d+360)%360 });
        if (section.ObjectOrder.Count != 0)
        {
            var positions = new Dictionary<string,int>();
            foreach (var id in section.ObjectOrder) positions.TryAdd(id,positions.Count);
            objects = objects.OrderBy(o => positions.GetValueOrDefault(o.Id,int.MaxValue)).ToList();
        }
        return new(Width, Height, objects) { Insert = (text, picture) => section.Shapes.Add(new()
            { Text = text, Picture = picture, Box = OfficeSlideFormat.InsertionBounds(Width, Height, picture) }) };
    }

    internal static Task<byte[]> SnapshotAsync(List<OfficeSheetSection> sections, CancellationToken token)
    {
        var snapshot = sections.Select(s => new OfficeSheetSection
            { Title = s.Title, Body = s.Body, TitleBox = s.TitleBox, BodyBox = s.BodyBox,
              HideTitle = s.HideTitle, HideBody = s.HideBody, TitleAngle = s.TitleAngle, BodyAngle = s.BodyAngle,
              ObjectOrder = [..s.ObjectOrder], Shapes = s.Shapes.Select(o => o.Snapshot()).ToList() }).ToList();
        return Task.Run(() => OfficeNativeDocument.Create(".pptx", snapshot), token);
    }
}
