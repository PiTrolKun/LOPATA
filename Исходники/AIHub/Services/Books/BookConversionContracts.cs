namespace AIHub.Services.Books;

public sealed record BookConversionOptions
{
    public bool GenerateTableOfContents { get; init; } = true;
    public double FontSize { get; init; } = 12;
    public double MarginMillimeters { get; init; } = 15;
    public int ImageQuality { get; init; } = 90;
    public string TextEncoding { get; init; } = "utf-8";
}

public sealed record BookConversionRequest
{
    public Smart.BookSmartSelection? Smart { get; init; }
    public string[] Sources { get; init; } = [];
    public string OutputFormat { get; init; } = "epub";
    public string OutputDirectory { get; init; } = "";
    public bool Merge { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Language { get; init; }
    public string? CoverPath { get; init; }
    public BookConversionOptions Options { get; init; } = new();
}

public sealed record BookConversionProgress(string Phase, int Completed, int Total, string Source = "", string OutputPath = "", string Message = "")
{
    public int Section { get; init; }
    public int SectionCount { get; init; }
    public int Item { get; init; }
    public int ItemCount { get; init; }
}
public sealed record BookConversionFailure(string Source, string Message);
public sealed record BookConversionResult
{
    public string[] Outputs { get; init; } = [];
    public string[] Skipped { get; init; } = [];
    public BookConversionFailure[] Failures { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public bool Cancelled { get; init; }
}

/// <summary>Capabilities advertised separately for input and output. External formats require preparation.</summary>
public static class BookConversionFormats
{
    public static readonly string[] NativeInputs = ["pdf", "txt", "md", "markdown", "html", "htm", "xhtml", "epub", "fb2", "docx", "cbz", "png", "jpg", "jpeg", "tif", "tiff", "webp", "bmp", "gif"];
    public static readonly string[] CalibreInputs = ["azw", "azw3", "mobi", "prc", "pdb", "lrf", "lit", "chm", "djvu", "rtf", "odt", "cbr", "fbz", "htmlz", "pml", "rb", "snb", "tcr", "txtz", "kepub", "zip"];
    public static readonly string[] NativeOutputs = ["epub", "fb2", "docx", "html", "md", "txt", "cbz", "pdf"];
    public static readonly string[] CalibreOutputs = ["azw3", "mobi", "rtf", "odt", "kepub"];
    public static string[] InputExtensions => NativeInputs.Concat(CalibreInputs).Append("doc").Distinct().ToArray();
    public static string[] OutputExtensions => NativeOutputs.Concat(CalibreOutputs).ToArray();
    public static string Normalize(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch { "markdown" => "md", "htm" or "xhtml" => "html", var x => x };
}
