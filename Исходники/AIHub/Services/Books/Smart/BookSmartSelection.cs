namespace AIHub.Services.Books.Smart;

public sealed record BookSmartSelection(string OcrId = "paddle", string VisionId = "alpha");

public sealed record BookSmartPreparationItem(string Name, string Status, long MissingBytes, bool Ready);

public sealed record BookSmartReadiness(BookSmartPreparationItem[] Items)
{
    public bool Ready => Items.Length > 0 && Items.All(item => item.Ready);
}
