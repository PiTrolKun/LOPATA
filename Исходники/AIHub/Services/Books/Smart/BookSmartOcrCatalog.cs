using System.IO;
using AIHub.Models;

namespace AIHub.Services.Books.Smart;

internal static class BookSmartOcrCatalog
{
    internal const string PaddleId = "model-book-paddleocr-vl16";
    internal const string OvisId = "model-book-ovisocr2-q8";
    internal const string LayoutId = "model-book-paddle-doclayout-v3";
    private const string PaddleRepository = "PaddlePaddle/PaddleOCR-VL-1.6-GGUF";
    private const string PaddleRevision = "511b09642bb324401f15f97cc23bc67e8f0a291d";
    private const string OvisRepository = "bartowski/ATH-MaaS_OvisOCR2-GGUF";
    private const string OvisRevision = "ab22420f3d44201d3aa5a62ca49a665a46b507e9";
    private const string LayoutRepository = "PaddlePaddle/PP-DocLayoutV3";
    private const string LayoutRevision = "241f8bdfc77a7c7bee915a5057aaee58c235a8d3";

    internal static IReadOnlyList<ManagedModelArtifactCard> CreateCards(BookSmartSelection selection, string root) => selection.OcrId switch
    {
        "paddle" => [Paddle(root), Layout(root)],
        "ovis" => [Ovis(root)],
        _ => throw new ArgumentException("Unknown OCR selection.", nameof(selection))
    };

    internal static OmniLlamaProfile Profile(BookSmartSelection selection) => selection.OcrId switch
    {
        "paddle" => new("book-smart", "paddle-ocr-vl", "1.6", PaddleId, PaddleRepository, PaddleRevision,
            "PaddlePaddle/PaddleOCR-VL-1.6", "BF16", "PaddleOCR-VL 1.6") { Temperature = 0, ProjectorQuantization = "BF16" },
        "ovis" => new("book-smart", "ovis-ocr", "2", OvisId, OvisRepository, OvisRevision,
            "ATH-MaaS/OvisOCR2", "Q8_0", "OvisOCR2") { Temperature = 0 },
        _ => throw new ArgumentException("Unknown OCR selection.", nameof(selection))
    };

    internal static OutputModelUse SourceIdentity(BookSmartSelection selection)
    {
        var profile = Profile(selection);
        return new(profile.Repository, profile.Label, profile.Revision, "document OCR");
    }
    internal static OutputModelUse LayoutIdentity() => new(LayoutRepository, "PP-DocLayoutV3", LayoutRevision, "document layout and reading order");
    internal static string LayoutDirectory(string root) => Layout(root).InstallDirectory;

    private static ManagedModelArtifactCard Paddle(string root) => Card(root, PaddleId, "PaddleOCR-VL 1.6 GGUF", PaddleRepository, PaddleRevision, "GGUF", "paddleocr", "BF16",
        [File(PaddleRepository, PaddleRevision, "PaddleOCR-VL-1.6-GGUF.gguf", 935769056, "f3ae46ec885050acf4b3d31944431e1fd90d50664fb09126af4a3c050ba14ee8", "main_model"),
         File(PaddleRepository, PaddleRevision, "PaddleOCR-VL-1.6-GGUF-mmproj.gguf", 881770560, "204d757d7610d9b3faab10d506d69e5b244e32bf765e2bab2d0167e65e0a058a", "projector")]);

    // This quantization is by bartowski; the model itself is by ATH-MaaS. Preserve both in the pin/source metadata.
    private static ManagedModelArtifactCard Ovis(string root) => Card(root, OvisId, "OvisOCR2 Q8_0 (bartowski GGUF)", OvisRepository, OvisRevision, "GGUF", "ovis2", "Q8_0",
        [File(OvisRepository, OvisRevision, "ATH-MaaS_OvisOCR2-Q8_0.gguf", 813576320, "8ca48f886bcd5f33636be25c935f07e0d2a83e839d9bce29c7fd3e4bfb344d33", "main_model"),
         File(OvisRepository, OvisRevision, "mmproj-ATH-MaaS_OvisOCR2-f16.gguf", 204987040, "4e0e9cb9d79dd0f423ba152a51816aa82a1f1a9d1a0190b6f67b2cd4cc5dd681", "projector")]);

    private static ManagedModelArtifactCard Layout(string root) => Card(root, LayoutId, "PP-DocLayoutV3", LayoutRepository, LayoutRevision, "Paddle", "doclayout", "FP32",
        [File(LayoutRepository, LayoutRevision, "inference.json", 1196890, "2b68367c5b312a03de5a6e1642c597c8f95165a7e40cd59c6700cf4a5042f4fd", "configuration"),
         File(LayoutRepository, LayoutRevision, "inference.pdiparams", 130806572, "70bd316b0582769ec968829fd1feb1a6a58b7c941b938327e551b6b12b45c137", "main_model"),
         File(LayoutRepository, LayoutRevision, "inference.yml", 1482, "506fcfac13b3b546ae40d7886b44126420f392adb694e3f8bb6a6286a1f90fdc", "configuration")]);

    private static ManagedModelArtifactCard Card(string root, string id, string name, string repository, string revision,
        string format, string architecture, string quantization, List<ManagedModelArtifactFile> files) => new()
    {
        ModelArtifactId = id, Family = name, DisplayName = name, RepositoryId = repository, Revision = revision,
        SourcePage = "https://huggingface.co/" + repository, Provider = "Hugging Face", License = "Apache-2.0",
        Format = format, Architecture = architecture, Quantization = quantization, Role = ManagedModelRoles.Tool,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "Books", id, revision[..12]),
        IsManaged = true, CanRemoveFiles = true, Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = format == "GGUF" ? LlamaBackendPaths.DisplayName : "PaddleOCR Windows CPU",
        Consumers = [new() { Id = "book-smart", DisplayName = "LOPATA · Book smart mode", Kind = "scenario_bundle" }], Files = files
    };

    private static ManagedModelArtifactFile File(string repo, string revision, string name, long size, string sha, string purpose) => new()
    { RelativePath = name, SourceUrl = $"https://huggingface.co/{repo}/resolve/{revision}/{name}", SizeBytes = size, Sha256 = sha, Purpose = purpose };
}
