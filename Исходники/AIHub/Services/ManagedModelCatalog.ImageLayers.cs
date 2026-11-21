using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    internal const string ImageLayerSam2Id = "model-image-layers-sam2-1-tiny";
    internal const string ImageLayerSam2Name = "SAM 2.1 Hiera Tiny";
    internal const string ImageLayerSam2Revision = "de431c4043854a71d8101e17995dfe596bf101a5";
    private const string ImageLayerSam2Source = "https://huggingface.co/facebook/sam2.1-hiera-tiny";

    private static ManagedModelArtifactCard CreateImageLayerSam2(string modelsRoot) => new()
    {
        ModelArtifactId = ImageLayerSam2Id,
        Family = "SAM 2.1",
        DisplayName = ImageLayerSam2Name,
        Role = ManagedModelRoles.Vision,
        Provider = "Hugging Face",
        RepositoryId = "facebook/sam2.1-hiera-tiny",
        Revision = ImageLayerSam2Revision,
        Format = "Safetensors",
        Architecture = "sam2",
        License = "Apache-2.0",
        SourcePage = ImageLayerSam2Source,
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Vision", "SAM2.1-Hiera-Tiny", ImageLayerSam2Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = "Transformers SAM 2 / PyTorch CPU",
        Consumers = [Consumer(ScenarioNavigationCatalog.ImageLayers, "Разбор изображения на слои", "scenario_bundle")],
        Files =
        [
            SamFile("model.safetensors", 155_908_064, "48c14467e5cf9e51870511feb72c89688e82dd74523142c0538b663e193ac2a7", "main_model"),
            SamFile("config.json", 5695, "860aff9751b139d83a4ad7df1e5535416fded533e0ead02625edbefcb9953cce", "configuration"),
            SamFile("preprocessor_config.json", 683, "6ebf229ee259368ce4a8d4f2fe893a72b053023710853e257253939e601f583d", "image_processor"),
            SamFile("README.md", 19965, "dfa401b1b9358b9650ae487dd2f2bb336fd745c3609e3244adf2e97a22343857", "notice")
        ]
    };

    private static ManagedModelArtifactFile SamFile(string path, long length, string sha256, string role) =>
        File(path, $"{ImageLayerSam2Source}/resolve/{ImageLayerSam2Revision}/{path}", length, sha256, role);

    internal const string ImageLayerMediaPipeId = "model-image-layers-mediapipe";
    internal const string ImageLayerMediaPipeName = "MediaPipe SelfieMulticlass";
    internal const string ImageLayerMediaPipeRevision = "c7b7e9e0f1e73c10a12114379077b5257e60c25c";
    internal const string ImageLayerMediaPipeFile = "selfie_multiclass_256x256.tflite";
    private const string MediaPipeSource = "https://huggingface.co/litert-community/MediaPipe-ImageSegmenter-Selfie-Multiclass";

    private static ManagedModelArtifactCard CreateImageLayerMediaPipe(string root) =>
        PortraitCard(root, ImageLayerMediaPipeId, ImageLayerMediaPipeName, ImageLayerMediaPipeRevision,
            "Google", MediaPipeSource, "litert-community/MediaPipe-ImageSegmenter-Selfie-Multiclass",
            "TensorFlow Lite", "MediaPipe Tasks / CPU", "Apache-2.0",
            [File(ImageLayerMediaPipeFile,
                "https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/1/selfie_multiclass_256x256.tflite",
                16_371_837, "c6748b1253a99067ef71f7e26ca71096cd449baefa8f101900ea23016507e0e0", "main_model")]);

    internal const string ImageLayerModNetId = "model-image-layers-modnet";
    internal const string ImageLayerModNetName = "MODNet";
    internal const string ImageLayerModNetRevision = "28165a451e4610c9d77cfdf925a94610bb2810fb";
    internal const string ImageLayerModNetFile = "modnet_photographic_portrait_matting.onnx";
    private const string ModNetSource = "https://github.com/ZHKKKe/MODNet";

    private static ManagedModelArtifactCard CreateImageLayerModNet(string root) =>
        PortraitCard(root, ImageLayerModNetId, ImageLayerModNetName, ImageLayerModNetRevision, "MODNet authors",
            ModNetSource, "ZHKKKe/MODNet", "ONNX", "ONNX Runtime / CPU", "Apache-2.0",
            [File(ImageLayerModNetFile, "https://drive.usercontent.google.com/download?id=1cgycTQlYXpTh26gB9FTnthE7AvruV8hd&export=download&confirm=t",
                25_888_640, "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9", "main_model")]);

    internal const string ImageLayerFlorenceId = "model-image-layers-florence-base-ft";
    internal const string ImageLayerFlorenceName = "Florence-2-base-ft";
    internal const string ImageLayerFlorenceRevision = "f6c1a25888ffc1d945ee8a1a77ac833c7303d46e";
    private const string FlorenceSource = "https://huggingface.co/microsoft/Florence-2-base-ft";
    private const string FlorenceProcessorSource = "https://huggingface.co/florence-community/Florence-2-base-ft/resolve/0b03b6f15a4a211370fb204aee4e7dd48887ea37";

    private static ManagedModelArtifactCard CreateImageLayerFlorence(string root) =>
        PortraitCard(root, ImageLayerFlorenceId, ImageLayerFlorenceName, ImageLayerFlorenceRevision, "Microsoft",
            FlorenceSource, "microsoft/Florence-2-base-ft", "Safetensors", "Transformers Florence-2 / PyTorch CPU", "MIT; Apache-2.0 (conversion mapping)",
            [
                File("model.safetensors", $"{FlorenceSource}/resolve/{ImageLayerFlorenceRevision}/model.safetensors", 463_221_266,
                    "58757d657ff44051314c8030b68e04cb1bb618ca9a4885418f111f6fb708185a", "main_model"),
                File("LICENSE", $"{FlorenceSource}/resolve/{ImageLayerFlorenceRevision}/LICENSE", 1_141,
                    "c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383", "notice"),
                File("convert_mapping.py",
                    "https://raw.githubusercontent.com/huggingface/transformers/b76caaa88d9de399ec8c2ad1338c69d31e4fef32/src/transformers/models/florence2/convert_florence2_original_pytorch_to_hf.py",
                    18_526, "56f57582b5441e8660a5bd7bfafcd15e3ba04a50b001167417a2fca9c94afacc", "configuration"),
                ProcessorFile("config.json", 2_387, "2a4dfe9885d183da121f239ada734bd700ee6908b650a7a25ab32eb104fe1d7a"),
                ProcessorFile("generation_config.json", 292, "0251459c49cc358ac033b5d4b8569e61ac22bde5d76be404b97a44cfb33fb12e"),
                ProcessorFile("preprocessor_config.json", 603, "1396ec5a0a7adfe1c04fb777b09e8ba753be6dbb5868212ab3c3ef39d91fe031"),
                ProcessorFile("processor_config.json", 2_264, "cd0e3bf41a39b1276503fbd273bc03b9afc70d7a15ea681a92a1b4b77f858ee6"),
                ProcessorFile("tokenizer_config.json", 197_922, "cb5f80bd9afa767bb1bb798ee5f0a79eae45239b8e72506166b4218175a16723"),
                ProcessorFile("tokenizer.json", 3_748_144, "3ad7001f773409abe6bba33eac92662611a73d72f459bda2f00d2a221dd31ce4")
            ]);

    private static ManagedModelArtifactFile ProcessorFile(string name, long bytes, string sha) =>
        File($"processor/{name}", $"{FlorenceProcessorSource}/{name}", bytes, sha, "configuration");

    internal const string ImageLayerTextId = "model-image-layers-ppocr";
    internal const string ImageLayerTextName = "PP-OCRv6 Small / PP-OCRv5 Cyrillic";
    internal const string ImageLayerTextRevision = "rapidocr-v3.10.0";
    private const string OcrSource = "https://www.modelscope.cn/models/RapidAI/RapidOCR";

    private static ManagedModelArtifactCard CreateImageLayerText(string root) => new()
    {
        ModelArtifactId = ImageLayerTextId, Family = "PaddleOCR", DisplayName = ImageLayerTextName,
        Role = ManagedModelRoles.Vision, Architecture = "PP-OCR", Format = "ONNX", License = "Apache-2.0",
        Provider = "ModelScope", RepositoryId = "RapidAI/RapidOCR", Revision = ImageLayerTextRevision,
        SourcePage = OcrSource, IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = CombineIfRoot(root, "Vision", "PP-OCR", ImageLayerTextRevision),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "ONNX Runtime / CPU",
        Consumers = [Consumer(ScenarioNavigationCatalog.ImageLayers, "Разбор изображения на слои", "scenario_bundle")],
        Files =
        [
            OcrFile("PP-OCRv6_det_small.onnx", "PP-OCRv6/det", 9_929_594,
                "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f", "text_detection"),
            OcrFile("PP-OCRv6_rec_small.onnx", "PP-OCRv6/rec", 21_234_383,
                "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884", "text_recognition"),
            OcrFile("cyrillic_PP-OCRv5_rec_mobile.onnx", "PP-OCRv5/rec", 8_074_092,
                "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711", "text_recognition_cyrillic")
        ]
    };

    private static ManagedModelArtifactFile OcrFile(string path, string directory, long length, string sha256, string role) =>
        File(path, $"{OcrSource}/resolve/v3.10.0/onnx/{directory}/{path}", length, sha256, role);

    private static ManagedModelArtifactCard PortraitCard(string root, string id, string name, string revision,
        string provider, string source, string repository, string format, string backend, string license,
        List<ManagedModelArtifactFile> files) => new()
    {
        ModelArtifactId = id, Family = name, DisplayName = name, Role = ManagedModelRoles.Vision,
        Provider = provider, RepositoryId = repository, Revision = revision, Format = format, Architecture = name,
        License = license, SourcePage = source, IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = CombineIfRoot(root, "Vision", name.Replace(' ', '-'), revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = backend,
        Consumers = [Consumer(ScenarioNavigationCatalog.ImageLayers, "Разбор изображения на слои", "scenario_bundle")],
        Files = files
    };
}
