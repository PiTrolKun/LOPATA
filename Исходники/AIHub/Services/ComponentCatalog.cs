using AIHub.Models;

namespace AIHub.Services;

public static class ComponentCatalog
{
    private static readonly IReadOnlyList<ComponentCatalogEntry> Entries =
    [
        BuiltIn(
            "builtin.dotnet",
            ".NET file primitives",
            "10",
            "TXT, JSON, XML, streams and basic file metadata.",
            "MIT",
            ["read.text", "read.json", "read.xml"]),
        BuiltIn(
            "builtin.wpf-images",
            "WPF BitmapDecoder",
            "10",
            "PNG, JPEG, GIF, BMP, TIFF and WDP decoding.",
            "Microsoft .NET Library License",
            ["read.image_pixels"],
            [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".wdp"]),
        BuiltIn(
            "builtin.openxml",
            "DocumentFormat.OpenXml",
            "3.5.1",
            "Low-level DOCX, XLSX and PPTX package access.",
            "MIT",
            ["read.office_openxml", "edit.office_openxml"],
            [".docx", ".xlsx", ".pptx"]),
        BuiltIn(
            "builtin.closedxml",
            "ClosedXML",
            "0.105.0",
            "Convenient XLSX and XLSM reading and editing.",
            "MIT",
            ["read.spreadsheet", "edit.spreadsheet"],
            [".xlsx", ".xlsm"]),
        BuiltIn(
            "builtin.pdfpig",
            "PdfPig",
            "0.1.15",
            "PDF text, page structure and embedded image extraction.",
            "Apache-2.0",
            ["read.pdf_text"],
            [".pdf"]),
        BuiltIn(
            "builtin.sharpcompress",
            "SharpCompress",
            "1.0.0",
            "ZIP, 7z, RAR, TAR, GZip and related archive reading.",
            "MIT",
            ["read.archive"],
            [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"]),
        BuiltIn(
            "builtin.csvhelper",
            "CsvHelper",
            "33.1.0",
            "CSV and delimited text reading and writing.",
            "MS-PL or Apache-2.0",
            ["read.csv", "edit.csv"],
            [".csv", ".tsv"]),
        BuiltIn(
            "builtin.anglesharp",
            "AngleSharp",
            "1.5.2",
            "HTML, SVG, CSS and DOM parsing.",
            "MIT",
            ["read.html", "read.svg"],
            [".html", ".htm", ".svg", ".css"]),
        BuiltIn(
            "builtin.markdig",
            "Markdig",
            "1.3.2",
            "Markdown parsing and safe local rendering.",
            "BSD-2-Clause",
            ["read.markdown"],
            [".md", ".markdown"]),
        BuiltIn(
            "builtin.yamldotnet",
            "YamlDotNet",
            "18.1.0",
            "YAML reading and writing.",
            "MIT",
            ["read.yaml", "edit.yaml"],
            [".yaml", ".yml"]),
        BuiltIn(
            "builtin.mimekit",
            "MimeKit",
            "4.17.0",
            "EML, MIME and mail attachment parsing.",
            "MIT",
            ["read.email"],
            [".eml", ".mime"]),
        BuiltIn(
            "builtin.sqlite",
            "Microsoft.Data.Sqlite",
            "10.0.10",
            "Controlled read-only SQLite access.",
            "MIT",
            ["read.database.sqlite"],
            [".sqlite", ".sqlite3", ".db"]),

        Download(
            "runtime.java.temurin21",
            "Eclipse Temurin JRE",
            "21",
            ComponentDeliveryKinds.Archive,
            "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse?project=jdk",
            "temurin-jre-21-x64.zip",
            49_010_279,
            120_000_000,
            "GPL-2.0 with Classpath Exception",
            "Eclipse Adoptium",
            [],
            [],
            "bin/java.exe"),
        Download(
            "runtime.apache-tika",
            "Apache Tika Server",
            "3.3.2",
            ComponentDeliveryKinds.Archive,
            "https://dlcdn.apache.org/tika/3.3.2/tika-server-standard-3.3.2-bin.zip",
            "tika-server-standard-3.3.2-bin.zip",
            65_882_112,
            90_000_000,
            "Apache-2.0",
            "Apache Software Foundation",
            ["runtime.java.temurin21"],
            ["read.universal_extract"],
            ""),
        Download(
            "runtime.imagemagick",
            "ImageMagick portable Q16 HDRI",
            "7.1.2-27",
            ComponentDeliveryKinds.Archive,
            "https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-27/ImageMagick-7.1.2-27-portable-Q16-HDRI-x64.7z",
            "ImageMagick-7.1.2-27-portable-Q16-HDRI-x64.7z",
            22_114_714,
            100_000_000,
            "ImageMagick License",
            "ImageMagick",
            [],
            ["read.image_extended", "edit.image", "convert.image"],
            "magick.exe"),
        Download(
            "runtime.tesseract",
            "Tesseract OCR",
            "5.4.0",
            ComponentDeliveryKinds.SystemInstaller,
            "https://github.com/UB-Mannheim/tesseract/releases/download/v5.4.0.20240606/tesseract-ocr-w64-setup-5.4.0.20240606.exe",
            "tesseract-ocr-w64-setup-5.4.0.20240606.exe",
            50_174_976,
            170_000_000,
            "Apache-2.0",
            "UB Mannheim Windows build",
            [],
            ["extract.image_ocr"],
            ""),
        Download(
            "language.tesseract.eng",
            "Tesseract English fast data",
            "main",
            ComponentDeliveryKinds.File,
            "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata",
            "eng.traineddata",
            4_113_088,
            4_113_088,
            "Apache-2.0",
            "tesseract-ocr/tessdata_fast",
            ["runtime.tesseract"],
            ["extract.image_ocr.en"],
            "eng.traineddata"),
        Download(
            "language.tesseract.rus",
            "Tesseract Russian fast data",
            "main",
            ComponentDeliveryKinds.File,
            "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/rus.traineddata",
            "rus.traineddata",
            3_858_432,
            3_858_432,
            "Apache-2.0",
            "tesseract-ocr/tessdata_fast",
            ["runtime.tesseract"],
            ["extract.image_ocr.ru"],
            "rus.traineddata"),
        Download(
            "runtime.ffmpeg",
            "FFmpeg LGPL shared",
            "8.1",
            ComponentDeliveryKinds.Archive,
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip",
            "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip",
            70_506_250,
            260_000_000,
            "LGPL-2.1-or-later",
            "BtbN FFmpeg Builds",
            [],
            ["read.audio", "read.video", "extract.video_frames", "edit.audio", "edit.video"],
            "bin/ffmpeg.exe"),
        Download(
            "runtime.libreoffice",
            "LibreOffice",
            "26.2.4",
            ComponentDeliveryKinds.SystemInstaller,
            "https://download.documentfoundation.org/libreoffice/stable/26.2.4/win/x86_64/LibreOffice_26.2.4_Win_x86-64.msi",
            "LibreOffice_26.2.4_Win_x86-64.msi",
            372_538_163,
            1_100_000_000,
            "MPL-2.0 and others",
            "The Document Foundation",
            [],
            ["convert.legacy_office"],
            ""),
        Download(
            "runtime.whisper.cpu",
            "whisper.cpp CPU runtime",
            "1.9.1",
            ComponentDeliveryKinds.Archive,
            "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip",
            "whisper-bin-x64.zip",
            7_979_008,
            25_000_000,
            "MIT",
            "ggml-org/whisper.cpp",
            [],
            ["extract.audio_transcript"],
            "whisper-cli.exe"),
        Download(
            "runtime.whisper.cuda124",
            "whisper.cpp CUDA 12.4 accelerator",
            "1.9.1",
            ComponentDeliveryKinds.Archive,
            "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-cublas-12.4.0-bin-x64.zip",
            "whisper-cublas-12.4.0-bin-x64.zip",
            677_883_412,
            1_100_000_000,
            "MIT and NVIDIA redistributable terms",
            "ggml-org/whisper.cpp",
            ["runtime.whisper.cpu"],
            ["accelerate.audio_transcript.cuda"],
            ""),
        Download(
            "model.whisper.small",
            "Whisper multilingual small",
            "small",
            ComponentDeliveryKinds.File,
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true",
            "ggml-small.bin",
            487_598_080,
            487_598_080,
            "MIT",
            "ggerganov/whisper.cpp",
            ["runtime.whisper.cpu"],
            ["extract.audio_transcript.multilingual"],
            "ggml-small.bin"),
        Download(
            "model.vision.smolvlm2.projector",
            "SmolVLM2 2.2B multimodal projector",
            "1bc3c9f7",
            ComponentDeliveryKinds.File,
            "https://huggingface.co/ggml-org/SmolVLM2-2.2B-Instruct-GGUF/resolve/1bc3c9f74ceafd4c8d4411cc9cf188bba3798f91/mmproj-SmolVLM2-2.2B-Instruct-Q8_0.gguf?download=true",
            "mmproj-SmolVLM2-2.2B-Instruct-Q8_0.gguf",
            592_523_200,
            592_523_200,
            "Apache-2.0",
            "ggml-org/SmolVLM2-2.2B-Instruct-GGUF",
            [],
            [],
            "mmproj-SmolVLM2-2.2B-Instruct-Q8_0.gguf",
            "ae07ea1facd07dd3230c4483b63e8cda96c6944ad2481f33d531f79e892dd024"),
        Download(
            "model.vision.smolvlm2.q4km",
            "SmolVLM2 2.2B Instruct Q4_K_M",
            "1bc3c9f7",
            ComponentDeliveryKinds.File,
            "https://huggingface.co/ggml-org/SmolVLM2-2.2B-Instruct-GGUF/resolve/1bc3c9f74ceafd4c8d4411cc9cf188bba3798f91/SmolVLM2-2.2B-Instruct-Q4_K_M.gguf?download=true",
            "SmolVLM2-2.2B-Instruct-Q4_K_M.gguf",
            1_112_602_656,
            1_112_602_656,
            "Apache-2.0",
            "ggml-org/SmolVLM2-2.2B-Instruct-GGUF",
            ["model.vision.smolvlm2.projector"],
            ["analyze.image.semantic"],
            "SmolVLM2-2.2B-Instruct-Q4_K_M.gguf",
            "0cf76814555b8665149075b74ab6b5c1d428ea1d3d01c1918c12012e8d7c9f58"),
        new ComponentCatalogEntry
        {
            Id = "runtime.comfyui",
            Kind = ComponentKinds.Processing,
            Name = "ComfyUI NVIDIA portable",
            Version = "0.28.0",
            Description = "Planned image generation runtime. Disabled until a dedicated integration and sandbox specification.",
            DeliveryKind = ComponentDeliveryKinds.Planned,
            DownloadUrl = "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.28.0/ComfyUI_windows_portable_nvidia_cu126.7z",
            FileName = "ComfyUI_windows_portable_nvidia_cu126.7z",
            DownloadSizeBytes = 2_034_409_472,
            InstalledSizeBytes = 5_000_000_000,
            License = "GPL-3.0 and bundled dependencies",
            Source = "Comfy-Org/ComfyUI",
            Capabilities = ["generate.image"]
        },

        Viewer(
            "viewer.webview2",
            "WebView2 document host",
            "1.0.4078.44",
            "HTML, SVG and local web-based representations.",
            "https://go.microsoft.com/fwlink/?linkid=2124701",
            "MicrosoftEdgeWebView2RuntimeInstallerX64.exe",
            203_843_174,
            "Microsoft Software License Terms",
            [".html", ".htm", ".svg"]),
        Viewer(
            "viewer.pdfjs",
            "PDF.js",
            "6.1.200",
            "Paged PDF view with search and selectable text.",
            "https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-6.1.200.tgz",
            "pdfjs-dist-6.1.200.tgz",
            9_175_040,
            "Apache-2.0",
            [".pdf"],
            ["viewer.webview2"]),
        Viewer(
            "viewer.epubjs",
            "EPUB.js",
            "0.3.93",
            "EPUB navigation and table of contents.",
            "https://registry.npmjs.org/epubjs/-/epubjs-0.3.93.tgz",
            "epubjs-0.3.93.tgz",
            2_234_368,
            "BSD-2-Clause",
            [".epub"],
            ["viewer.webview2"]),
        Viewer(
            "viewer.libvlc",
            "LibVLC media viewer",
            "3.0.23.1",
            "Local audio and video playback.",
            "https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.23.1/videolan.libvlc.windows.3.0.23.1.nupkg",
            "videolan.libvlc.windows.3.0.23.1.nupkg",
            134_280_806,
            "LGPL-2.1",
            [".mp3", ".wav", ".flac", ".mp4", ".mkv", ".webm", ".avi"]),
        Viewer(
            "viewer.openseadragon",
            "OpenSeadragon",
            "6.0.2",
            "Large-image pan, zoom and tiled view.",
            "https://registry.npmjs.org/openseadragon/-/openseadragon-6.0.2.tgz",
            "openseadragon-6.0.2.tgz",
            996_147,
            "BSD-3-Clause",
            [".dzi", ".tif", ".tiff"],
            ["viewer.webview2"]),
        Viewer(
            "viewer.babylon",
            "Babylon.js 3D viewer",
            "9.18.0",
            "Local glTF and GLB model viewing.",
            "https://registry.npmjs.org/babylonjs/-/babylonjs-9.18.0.tgz",
            "babylonjs-9.18.0.tgz",
            18_434_048,
            "Apache-2.0",
            [".gltf", ".glb"],
            ["viewer.webview2"]),
        Viewer(
            "viewer.avalonedit",
            "AvalonEdit",
            "6.3.1.120",
            "Large text and code viewing with lines and search.",
            "https://api.nuget.org/v3-flatcontainer/avalonedit/6.3.1.120/avalonedit.6.3.1.120.nupkg",
            "avalonedit.6.3.1.120.nupkg",
            901_775,
            "MIT",
            [".log", ".cs", ".js", ".ts", ".py", ".cpp", ".h", ".sql"])
    ];

    public static IReadOnlyList<ComponentCatalogEntry> All { get; } = [.. Entries, .. HardwareRuntimeCatalog.Components];

    public static IReadOnlyList<ComponentCatalogEntry> Processing => All
        .Where(entry => entry.Kind == ComponentKinds.Processing)
        .ToList();

    public static IReadOnlyList<ComponentCatalogEntry> Viewers => All
        .Where(entry => entry.Kind == ComponentKinds.Viewer)
        .ToList();

    public static ComponentCatalogEntry? Find(string id) => All.FirstOrDefault(entry =>
        string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ComponentCatalogEntry> FindProviders(string capability) => Processing
        .Where(entry => entry.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
        .ToList();

    public static IReadOnlyList<ComponentCatalogEntry> ResolveDependencies(IEnumerable<string> componentIds)
    {
        var ordered = new List<ComponentCatalogEntry>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in componentIds)
        {
            Visit(id);
        }

        return ordered;

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new InvalidOperationException($"Component dependency cycle detected at '{id}'.");
            }

            var entry = Find(id) ?? throw new InvalidOperationException($"Unknown component '{id}'.");
            foreach (var dependency in entry.Dependencies)
            {
                Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(entry);
        }
    }

    private static ComponentCatalogEntry BuiltIn(
        string id,
        string name,
        string version,
        string description,
        string license,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string>? extensions = null) => new()
        {
            Id = id,
            Kind = ComponentKinds.Processing,
            Name = name,
            Version = version,
            Description = description,
            DeliveryKind = ComponentDeliveryKinds.BuiltIn,
            License = license,
            Source = "LOPATA application package",
            Capabilities = capabilities,
            Extensions = extensions ?? []
        };

    private static ComponentCatalogEntry Download(
        string id,
        string name,
        string version,
        string delivery,
        string url,
        string fileName,
        long downloadBytes,
        long installedBytes,
        string license,
        string source,
        IReadOnlyList<string> dependencies,
        IReadOnlyList<string> capabilities,
        string healthCheck,
        string sha256 = "") => new()
        {
            Id = id,
            Kind = ComponentKinds.Processing,
            Name = name,
            Version = version,
            Description = string.Join(", ", capabilities),
            DeliveryKind = delivery,
            DownloadUrl = url,
            FileName = fileName,
            Sha256 = sha256,
            DownloadSizeBytes = downloadBytes,
            InstalledSizeBytes = installedBytes,
            License = license,
            Source = source,
            Dependencies = dependencies,
            Capabilities = capabilities,
            HealthCheckRelativePath = healthCheck
        };

    private static ComponentCatalogEntry Viewer(
        string id,
        string name,
        string version,
        string description,
        string url,
        string fileName,
        long downloadBytes,
        string license,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? dependencies = null) => new()
        {
            Id = id,
            Kind = ComponentKinds.Viewer,
            Name = name,
            Version = version,
            Description = description,
            DeliveryKind = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? ComponentDeliveryKinds.SystemInstaller
                : ComponentDeliveryKinds.Archive,
            DownloadUrl = url,
            FileName = fileName,
            DownloadSizeBytes = downloadBytes,
            InstalledSizeBytes = downloadBytes * 2,
            License = license,
            Source = new Uri(url).Host,
            Dependencies = dependencies ?? [],
            Extensions = extensions,
            Capabilities = []
        };
}
