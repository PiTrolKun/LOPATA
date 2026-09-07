namespace AIHub.Models;

public sealed class ImageBatchJob
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public string ModelRevision { get; set; } = string.Empty;
    public ImageAnalysisLiterarySettings Settings { get; set; } = new();
    public bool SingleDocument { get; set; } = true;
    public bool Started { get; set; }
    public string Status { get; set; } = "draft";
    public string Error { get; set; } = string.Empty;
    public List<ImageBatchItem> Items { get; set; } = [];
}

public sealed class ImageBatchItem
{
    public int Position { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ImageAnalysisFilePassport File { get; set; } = new();
    public string Status { get; set; } = "pending";
    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
}

public sealed record ImageBatchSection(string Id, string Title, string[] Paragraphs);
public sealed record ImageBatchAnalysis(string Details, string Summary);
public sealed record ImageBatchProgress(int Finished, int Total, int Succeeded, int Failed, string Stage);
