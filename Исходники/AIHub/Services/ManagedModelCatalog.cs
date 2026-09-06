using AIHub.Models;
using System.IO;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public const string CoreArtifactId = "model-qwen3-8b-core-q4km";
    public const string KimiLegacyArtifactId = "model-kimi-vl-a3b-thinking-2506-q4km";
    public const string KimiMediumArtifactId = "model-kimi-vl-a3b-thinking-2506-chatllm-q4_1";
    public const string FlorenceLargeArtifactId = "model-florence-2-large-ft";
    public const string KokoroEnglishArtifactId = "model-kokoro-82m-en-af-heart";
    public const string KokoroRussianArtifactId = "model-kokoro-ru-sveta";
    public const string Qwen25OmniHeavyArtifactId = "model-qwen2-5-omni-3b";

    public const string KimiRepository = "judd2024/chatllm_quantized_kimi-vl";
    public const string KimiRevision = "master";
    public const string FlorenceRepository = "microsoft/Florence-2-large-ft";
    public const string FlorenceRevision = "4a12a2b54b7016a48a22037fbd62da90cd566f2a";
    public const string KokoroEnglishRepository = "hexgrad/Kokoro-82M";
    public const string KokoroEnglishRevision = "f3ff3571791e39611d31c381e3a41a3af07b4987";
    public const string KokoroRussianRepository = "zaakirio/kokoro-ru";
    public const string KokoroRussianRevision = "27d078fe1c0cab919613a64e906919214385f21d";
    public const string RuAccentRepository = "ruaccent/accentuator";
    public const string RuAccentRevision = "b78ae5ea1e62beaf138bed1865cd8c3b0b5ca855";
    public const string Qwen25OmniRepository = "Qwen/Qwen2.5-Omni-3B";
    public const string Qwen25OmniRevision = "f75b40e3da2003cdd6e1829b1f420ca70797c34e";

    public static IReadOnlyList<ManagedModelArtifactCard> CreatePredefined(StorageSettings settings)
    {
        var modelsRoot = settings.Models.Locations
            .Select(location => location.Path?.Trim())
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? string.Empty;
        return
        [
            CreateCore(modelsRoot),
            CreateKimiMedium(modelsRoot),
            CreateFlorenceLarge(modelsRoot),
            CreateQwen25OmniHeavy(modelsRoot),
            CreateOmniAlpha(modelsRoot),
            CreateOmniBeta(modelsRoot),
        CreateOmniGamma(modelsRoot),
            CreateKokoroEnglish(modelsRoot),
            CreateKokoroRussian(modelsRoot)
        ];
    }

    public static ManagedModelArtifactCard CreateCore(string modelsRoot) => new()
    {
        ModelArtifactId = CoreArtifactId,
        Family = "Qwen3-8B",
        DisplayName = CoreModelManager.CoreModelDisplayName,
        Role = ManagedModelRoles.Core,
        Provider = "Hugging Face",
        RepositoryId = "Qwen/Qwen3-8B-GGUF",
        Revision = "7c41481f57cb95916b40956ab2f0b139b296d974",
        Format = "GGUF",
        Architecture = "qwen3",
        Quantization = "Q4_K_M",
        ParameterCount = 8_000_000_000,
        License = "Apache-2.0",
        SourcePage = "https://huggingface.co/Qwen/Qwen3-8B-GGUF",
        IsManaged = true,
        IsSystem = true,
        IsPinned = false,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Core", "Qwen3-8B"),
        Origin = ManagedModelOrigins.ExistingManifest,
        RuntimeBackend = LlamaBackendPaths.DisplayName,
        Consumers =
        [
            Consumer("ai-hub-core", "Ядро ЛОПАТА", "system"),
            Consumer("image-analysis-medium", "Анализ изображений — Средний", "scenario_bundle")
        ],
        Files =
        [
            File(
                CoreModelManager.CoreModelFileName,
                "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/7c41481f57cb95916b40956ab2f0b139b296d974/Qwen3-8B-Q4_K_M.gguf",
                CoreModelManager.CoreModelTotalBytes,
                "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785",
                "main_model")
        ]
    };

    public static ManagedModelArtifactCard CreateQwen25OmniHeavy(string modelsRoot) => new()
    {
        ModelArtifactId = Qwen25OmniHeavyArtifactId,
        Family = "Qwen2.5-Omni-3B",
        DisplayName = "Qwen2.5-Omni-3B BF16",
        Role = ManagedModelRoles.Vision,
        Provider = "Hugging Face",
        RepositoryId = Qwen25OmniRepository,
        Revision = Qwen25OmniRevision,
        Format = "Safetensors",
        Architecture = "qwen2_5_omni",
        ParameterCount = 3_000_000_000,
        License = "Qwen Research License (non-commercial research/evaluation only)",
        SourcePage = $"https://huggingface.co/{Qwen25OmniRepository}",
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(
            modelsRoot,
            "Vision",
            "Qwen2.5-Omni-3B",
            Qwen25OmniRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = "Python Transformers / CUDA (isolated Heavy worker, GPU-only)",
        Consumers =
        [
            Consumer("image-analysis-heavy", "Анализ изображений — Тяжёлый", "scenario_bundle")
        ],
        Files =
        [
            OmniFile("LICENSE", 7_387, "ae8ef9d3fb476f735d9b9abeaab73b7f778b121c77427cd918ec3da01eefbc44", "license"),
            OmniFile("added_tokens.json", 579, "e81a2cc3bd867a1217019eed202d1d8a07e1063ece716f22060dda14f6cc07d8", "tokenizer"),
            OmniFile("chat_template.json", 1_313, "1933f7ca08e6503460e981d9037ddc0b851196abfa3aceea52d6ee0c0fdfd9f0", "chat_template"),
            OmniFile("config.json", 13_183, "20790f362c37a1718a3e764f597ae33dfc20177399762018dffaabf8321d4dd1", "configuration"),
            OmniFile("generation_config.json", 74, "b9acf52072249e0e1850541d51ca4c85a30b9d7e23c2f2093ec0fb4139059ac7", "generation_configuration"),
            OmniFile("merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5", "tokenizer"),
            OmniFile("model-00001-of-00003.safetensors", 4_994_850_880, "349972cebff443030e9e96e1e930d7230979c6961cf046fa29fc580e4bcf49a6", "model_weights"),
            OmniFile("model-00002-of-00003.safetensors", 4_999_787_448, "b29a76dfefb3aa33a5bd0faa19c681d039a44de57223653ea0e933df728c6d8c", "model_weights"),
            OmniFile("model-00003-of-00003.safetensors", 1_978_024_880, "8f2138170a87ec604b53232491a96d6dcf19e0724aca37e69aace143e508b170", "model_weights"),
            OmniFile("model.safetensors.index.json", 241_817, "5b7629198e2ef80e37612a491d9bfd71639d2f212632d36d8ab086922e74e129", "model_index"),
            OmniFile("preprocessor_config.json", 667, "b47055ce61463ce143e9aab741d55c0aa520801a0a5d63be73c5b17cecb6bc69", "processor_configuration"),
            OmniFile("special_tokens_map.json", 832, "dc241604d085780a33f760f69c47e6622451104c20eee3cd3a12b6e884b43f44", "tokenizer"),
            OmniFile("spk_dict.pt", 259_544, "6a05609b28f5d42b7b748f0f07592545c8f1f6885b9ae8fff64baf56e86b2a18", "speaker_dictionary"),
            OmniFile("tokenizer.json", 11_421_870, "8441917e39ae0244e06d704b95b3124795cec478e297f9afac39ba670d7e9d99", "tokenizer"),
            OmniFile("tokenizer_config.json", 6_469, "569aa7a9171e36dfff80f0a7550ab0c9c09e46ac840fea5030641417394fb0d2", "tokenizer_configuration"),
            OmniFile("vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910", "vocabulary")
        ]
    };

    public static ManagedModelArtifactCard CreateKimiMedium(string modelsRoot) => new()
    {
        ModelArtifactId = KimiMediumArtifactId,
        Family = "Kimi-VL-A3B-Thinking-2506",
        DisplayName = "Kimi-VL-A3B-Thinking-2506 GGMM Q4_1",
        Role = ManagedModelRoles.Vision,
        Provider = "ModelScope",
        RepositoryId = KimiRepository,
        Revision = KimiRevision,
        Format = "GGMM",
        Architecture = "kimi-vl",
        Quantization = "Q4_1",
        License = "MIT (upstream Moonshot AI model)",
        SourcePage = $"https://modelscope.cn/models/{KimiRepository}",
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Vision", "Kimi-VL-A3B-Thinking-2506", "chatllm-v24-q4_1"),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = ChatLlmBackendPaths.DisplayName,
        Consumers =
        [
            Consumer("image-analysis-medium", "Анализ изображений — Средний", "scenario_bundle")
        ],
        Files =
        [
            File(
                "kimi-vl-thinking-2506-q4_1.bin",
                ResolveModelScope(KimiRepository, "kimi-vl-thinking-2506-q4_1.bin"),
                10_447_149_104,
                "33700ea2f4c8467fbcc4efa060c763e035a8e73003424634125b5a3c64ce02c9",
                "main_model")
        ]
    };

    public static ManagedModelArtifactCard CreateFlorenceLarge(string modelsRoot) => new()
    {
        ModelArtifactId = FlorenceLargeArtifactId,
        Family = "Florence-2-large-ft",
        DisplayName = "Florence-2-large-ft",
        Role = ManagedModelRoles.Localizer,
        Provider = "Hugging Face",
        RepositoryId = FlorenceRepository,
        Revision = FlorenceRevision,
        Format = "Safetensors",
        Architecture = "florence2",
        ParameterCount = 770_000_000,
        License = "MIT",
        SourcePage = $"https://huggingface.co/{FlorenceRepository}",
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Shared", "Florence-2-large-ft", FlorenceRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = "Python Transformers (local-only, pinned code)",
        Consumers =
        [
            Consumer("image-analysis-medium", "Анализ изображений — Средний", "scenario_bundle"),
            Consumer("image-analysis-heavy", "Анализ изображений — Тяжёлый", "future_bundle")
        ],
        Files =
        [
            FlorenceFile("config.json", 2_445, "fa081841369aa9c6e42faf5c52368d673b561e2c5f8fa03d1256e7408cb4130e", "configuration"),
            FlorenceFile("configuration_florence2.py", 15_125, "653bafddc9651eaff1583a16db4a2bb27d33ec7d541dfab7201aaa4ecaa1cfbf", "pinned_model_code"),
            FlorenceFile("generation_config.json", 51, "30e9865458ecc8ee931eeeb43f44f1d169c5ab95be39e0072142a7a6b8f31990", "generation_configuration"),
            FlorenceFile("model.safetensors", 1_540_980_506, "8b4e610c952eef90a836c56cda0f398a672a3a6ca7b4d96b0e09a86dee42e2c3", "model_weights"),
            FlorenceFile("modeling_florence2.py", 127_415, "5bb7aa72c6ba62e96e1bbae6bc1aaf7b4e8e28cdfc62e670de3d5b67eeab1fdf", "pinned_model_code"),
            FlorenceFile("preprocessor_config.json", 806, "2f5921bbc53c7cc04251e1027b45b1cec726276be6db23d1bb40641bfbe2cf29", "processor_configuration"),
            FlorenceFile("processing_florence2.py", 46_372, "4bd7158536cbf1c7891fc8efd94437d79fd09f07f539c7398fab8a885d7d8bca", "pinned_processor_code"),
            FlorenceFile("tokenizer.json", 1_355_863, "847bbeab6174d66a88898f729d52fa8d355fafe1bea101cf960dd404581df70e", "tokenizer"),
            FlorenceFile("tokenizer_config.json", 34, "79ffcf43af8ebda99d165f61d243180da2e2639952e41e71e11611c18770489c", "tokenizer_configuration"),
            FlorenceFile("vocab.json", 1_099_884, "394fdc63c71aabe0a9b97117f5d62fb5fcc4d59b2b3ea929a3929e6a53217b3c", "vocabulary")
        ]
    };

    public static ManagedModelArtifactCard CreateKokoroEnglish(string modelsRoot) => new()
    {
        ModelArtifactId = KokoroEnglishArtifactId,
        Family = "Kokoro-82M",
        DisplayName = "Kokoro-82M — English (af_heart)",
        Role = ManagedModelRoles.Speech,
        Provider = "Hugging Face",
        RepositoryId = KokoroEnglishRepository,
        Revision = KokoroEnglishRevision,
        Format = "PyTorch",
        Architecture = "kokoro-styletts2",
        ParameterCount = 82_000_000,
        License = "Apache-2.0",
        SourcePage = $"https://huggingface.co/{KokoroEnglishRepository}",
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Speech", "Kokoro", "en", KokoroEnglishRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = "Python Kokoro 0.9.4 (CPU)",
        Consumers =
        [
            Consumer("image-analysis-speech", "Анализ изображений — озвучивание", "scenario_option")
        ],
        Files =
        [
            File("config.json", Resolve(KokoroEnglishRepository, KokoroEnglishRevision, "config.json"), 2_351, "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f", "configuration"),
            File("kokoro-v1_0.pth", Resolve(KokoroEnglishRepository, KokoroEnglishRevision, "kokoro-v1_0.pth"), 327_212_226, "496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4", "model_weights"),
            File("voices/af_heart.pt", Resolve(KokoroEnglishRepository, KokoroEnglishRevision, "voices/af_heart.pt"), 523_425, "0ab5709b8ffab19bfd849cd11d98f75b60af7733253ad0d67b12382a102cb4ff", "voice_pack")
        ]
    };

    public static ManagedModelArtifactCard CreateKokoroRussian(string modelsRoot) => new()
    {
        ModelArtifactId = KokoroRussianArtifactId,
        Family = "Kokoro-RU",
        DisplayName = "Kokoro-RU — Русский (Света)",
        Role = ManagedModelRoles.Speech,
        Provider = "Hugging Face",
        RepositoryId = KokoroRussianRepository,
        Revision = KokoroRussianRevision,
        Format = "PyTorch",
        Architecture = "kokoro-styletts2",
        ParameterCount = 81_810_000,
        License = "OpenRAIL weights (exact terms unavailable); Apache-2.0 code; accentuator declares MIT; GPL-3.0-or-later eSpeak data",
        SourcePage = $"https://huggingface.co/{KokoroRussianRepository}",
        IsManaged = true,
        CanRemoveFiles = true,
        ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Speech", "Kokoro", "ru", KokoroRussianRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = "Python Kokoro 0.9.4 + RUAccent (CPU)",
        Consumers =
        [
            Consumer("image-analysis-speech", "Анализ изображений — озвучивание", "scenario_option")
        ],
        Files =
        [
            File("kokoro-config.json", Resolve(KokoroRussianRepository, KokoroRussianRevision, "kokoro-config.json"), 2_351, "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f", "configuration"),
            File("kokoro-ru-v2-base.pth", Resolve(KokoroRussianRepository, KokoroRussianRevision, "kokoro-ru-v2-base.pth"), 327_220_619, "3bbee5bc05cfa182afc365b9116eaed8355f939c3c0af8aa0e43fdc45343ca15", "model_weights"),
            File("ru_g2p.py", Resolve(KokoroRussianRepository, KokoroRussianRevision, "ru_g2p.py"), 14_311, "d45057711b453a6349dac9d56b24272feaba42fbbd9ce43304dc9d82404777b4", "language_frontend"),
            File("voices/sveta.pt", Resolve(KokoroRussianRepository, KokoroRussianRevision, "voices/sveta.pt"), 523_831, "248c00e98f7ce20c31ff5537b52ea3d3b204a58845d86da73960f30f112f60a7", "voice_pack"),
            KokoroRussianFile("espeak-data/intonations", 2_312, "031a105bf6bb2ebcd73a2c579ccfb42469b27266db0078af65ae298fd3588b21"),
            KokoroRussianFile("espeak-data/phondata", 554_740, "a0b643b155cb6b12628d9e7865b57d9fca0d35844614f2594a5e009c80c80bb4"),
            KokoroRussianFile("espeak-data/phonindex", 43_316, "384e5fa6f714ba5356c58008249b78699c31c1ad044b243068d263fb806b7d73"),
            KokoroRussianFile("espeak-data/phontab", 58_652, "1b40690667e1e9aa1ba5e5234773c799e7e72ea751426e5150423d53c3f24fa2"),
            KokoroRussianFile("espeak-data/ru_dict", 108_695, "fec9c58731c7670b31ec6c36045d954760308234057c449565878b7e17266433"),
            KokoroRussianFile("espeak-data/lang/zle/ru", 57, "9f52d00a279aaeaa45a786b4fd3a98b34e95fedbf823f8cdb9fecc1339751d3a"),
            RuAccentFile("dictionary/accents.json.gz", 20_954_156, "aa460ebba90de00fbbf3d41d121961f605b98667e45efb7920f127473b15515e"),
            RuAccentFile("dictionary/accents_nn.json.gz", 845_996, "8395664000b80c1afe09bfea3650945b0933482b8e3dee5bb9d429eb18c44935"),
            RuAccentFile("dictionary/omographs.json.gz", 219_047, "04a9e81c68d65f65ba493fe0110f99e79087548c2beeec3032e2b66e28706f36"),
            RuAccentFile("dictionary/rule_engine/accents.json", 694_782, "74a749fac2a9fb82b4faebac3bb901217686bcd6365294dc3e304a2c44a2530d"),
            RuAccentFile("dictionary/rule_engine/forms.json", 2_669, "89a4c3c7529e299df58a0460589bf658a4f5427d9678ab78720320cf9ad3f3da"),
            RuAccentFile("dictionary/yo_homographs.json.gz", 5_747, "c4ee777bbbab87f9eac838f370ad92974e079d02b21903e480c54b5f0c8c60d1"),
            RuAccentFile("dictionary/yo_words.json.gz", 548_914, "a19fa89a964a0691d9fe4ee384783e3934904891843d8f59a1c480d67947a82a"),
            RuAccentFile("koziev/rulemma/rulemma.dat", 16_703_198, "bf2b3ef3ff7a0aa6e4250aa4e9c8ed568e25f825deebdb12dee1b46b785ba9fc"),
            RuAccentFile("koziev/rulemma/rulemma.py", 11_166, "61c0ae4ea718fc85000284dcbf798fb51657c41f3db3ae029e8bebfb33075f44"),
            RuAccentFile("koziev/rupostagger/__init__.py", 111, "68f14d0db5bc92e4da7363ca6c2c34812b18a87bdfe07714329bcfd711bdbe11"),
            RuAccentFile("koziev/rupostagger/database/ruword2tags.db", 168_816_640, "a06848e656bef642aafb4440c03554fa78f2f32dde92ea66f3f86ce9977b167e"),
            RuAccentFile("koziev/rupostagger/rupostagger.config", 276, "c8c19d1cd92855bc281b36ae80c3b10ed20d7f0aeeca54467c8fd00df319c614"),
            RuAccentFile("koziev/rupostagger/rupostagger.model", 2_417_464, "21b7b0bfd7427b5fdc1604052176db8aa3b139b3ce03be440cfce48536f8e5ef"),
            RuAccentFile("koziev/rupostagger/rupostagger.py", 7_118, "111b8c22db18407fb34bcd6e00b3c2245165de98ddec0fdb40862d82af864530"),
            RuAccentFile("koziev/rupostagger/rusyllab.py", 18_975, "331fdd0d9d10025bbebfc09ae648b21399d9f73cd168e1eeb76c10d6b0a31c00"),
            RuAccentFile("koziev/rupostagger/ruword2tags.dat", 9_683_765, "dde47b5f1d48ff899887ac07812dcabd2966e48e84646f3065bfd06627c2af58"),
            RuAccentFile("koziev/rupostagger/ruword2tags.py", 18_086, "2858ad021915f3fe5bf9589ab1773f8c2cdd664f053f3f6bf317fec3fa3ed23b"),
            RuAccentFile("nn/nn_accent/big.onnx", 2_285_217, "47e69d9ae19f2a82e21b1c70f6a4bbfb1abc5759e98b2e67d009c5e9d7af18c9"),
            RuAccentFile("nn/nn_accent/config.json", 841, "e377f159a8fc8673e211f100d4a237d5fe3dceb3b6789a5a738bf50bda23be49"),
            RuAccentFile("nn/nn_accent/model.onnx", 803_402, "4e393144e45626f6f1062a0784ef06f921b97321a8e7b87ac2a09a892286500a"),
            RuAccentFile("nn/nn_accent/ort_config.json", 727, "31dd1c06997b2c5097b558829e46b4e91a5ef8e5c6bea07dfc67e48b3bbc8775"),
            RuAccentFile("nn/nn_accent/special_tokens_map.json", 99, "2acac3fb89054158ccbeff3c52744ca93979f738002709c0aea001597213024b"),
            RuAccentFile("nn/nn_accent/tokenizer_config.json", 257, "efea18a18af2809330eb0f431ab86979304d2559571e887de7b6359e3f18f0d8"),
            RuAccentFile("nn/nn_accent/vocab.txt", 140, "242882b2f27800e49a6babf46867dabf6dc9b6535b600776a68815fe4d4f4382"),
            RuAccentFile("nn/nn_omograph/turbo3.1/added_tokens.json", 279_418, "071c01df9ba59b64b9d0d9af0eaac5412a5fc558d540a214ada4ee7531d38096"),
            RuAccentFile("nn/nn_omograph/turbo3.1/config.json", 723, "fa47e006d2a15a164d36e0303b5f8344a795f2108a3e55724133a87710216136"),
            RuAccentFile("nn/nn_omograph/turbo3.1/merges.txt", 1_213_606, "120c75258d87ba748b2025f517819fd0e0386e8d31cc6b29a21f900d395dd825"),
            RuAccentFile("nn/nn_omograph/turbo3.1/model.onnx", 359_306_923, "2cb6a174c4cdb45bd3132b4f7c8a3779fc4b6869863180ed7d0e421bcd453dbd"),
            RuAccentFile("nn/nn_omograph/turbo3.1/special_tokens_map.json", 280, "06e405a36dfe4b9604f484f6a1e619af1a7f7d09e34a8555eb0b77b66318067f"),
            RuAccentFile("nn/nn_omograph/turbo3.1/tokenizer.json", 5_532_343, "eb84a77af38a91f6f327486c7827d8541f8d446250f8d408909999b6be817afa"),
            RuAccentFile("nn/nn_omograph/turbo3.1/tokenizer_config.json", 492, "f159df92d0b0be03e1ceb0768222aa4c21dcfd37c5763b6b395b6864e023c7fb"),
            RuAccentFile("nn/nn_omograph/turbo3.1/vocab.json", 1_555_282, "ba028ba89c029a473ac369716b4d52bb252a38004fc5dd085d18f4ada4a760b3"),
            RuAccentFile("nn/nn_stress_usage_predictor/config.json", 822, "aa0638edf56254f123e593e96f034c2b5e68303ac207b29f64f0878dc4c22631"),
            RuAccentFile("nn/nn_stress_usage_predictor/model.onnx", 116_473_561, "3d547500637b4ddfec8880ed6d1405fd50ee9d3f0131ef8a2a69dcf961dbefeb"),
            RuAccentFile("nn/nn_stress_usage_predictor/special_tokens_map.json", 125, "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3"),
            RuAccentFile("nn/nn_stress_usage_predictor/tokenizer.json", 2_413_536, "ce51cde5df60ecb167abec4c90db11360482150628f7a91f3e316df6d6ff6f6f"),
            RuAccentFile("nn/nn_stress_usage_predictor/tokenizer_config.json", 368, "4466c2e475c434d35bce0f4a46f918730155386b50582fa3742b7b7d67897e96"),
            RuAccentFile("nn/nn_stress_usage_predictor/vocab.txt", 1_080_667, "f056a69b097422652053bf87565c35543e5d81540ca4b7dddd28de4157a969e0"),
            RuAccentFile("nn/nn_yo_homograph_resolver/config.json", 625, "adf7fd2c67be1498071f447ec4a23dbe84904c49f95d1d3e6c7d9df269b9a2e9"),
            RuAccentFile("nn/nn_yo_homograph_resolver/model.onnx", 14_332_169, "42cc85bf0c4b319dfe3d89fa17b162a92fd5e1c651a657cb3d5f44978d4e70ac"),
            RuAccentFile("nn/nn_yo_homograph_resolver/special_tokens_map.json", 125, "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3"),
            RuAccentFile("nn/nn_yo_homograph_resolver/tokenizer.json", 126_952, "f2bf174f2fe62ff11c28d56555f9c0cf16b24e162ef53bcf42ff85ebcf6ebfa8"),
            RuAccentFile("nn/nn_yo_homograph_resolver/tokenizer_config.json", 401, "804b8a4b4a02c9d38af8c88706206718759061ff9a102ad8483e9ac00011188c"),
            RuAccentFile("nn/nn_yo_homograph_resolver/vocab.txt", 48_576, "9276b81ff34cd9560563a476de6735abf5ed9a3871131644992230dc4e630056")
        ]
    };

    public static string ResolveKokoroArtifactId(string? languageCode) =>
        languageCode?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? KokoroEnglishArtifactId
            : KokoroRussianArtifactId;

    private static ManagedModelArtifactFile FlorenceFile(string name, long size, string sha256, string purpose) =>
        File(name, Resolve(FlorenceRepository, FlorenceRevision, name), size, sha256, purpose);

    private static ManagedModelArtifactFile OmniFile(string name, long size, string sha256, string purpose) =>
        File(name, Resolve(Qwen25OmniRepository, Qwen25OmniRevision, name), size, sha256, purpose);

    private static ManagedModelArtifactFile KokoroRussianFile(string name, long size, string sha256) =>
        File(name, Resolve(KokoroRussianRepository, KokoroRussianRevision, name), size, sha256, "language_frontend");

    private static ManagedModelArtifactFile RuAccentFile(string name, long size, string sha256) =>
        File(
            $"ruaccent/{name}",
            Resolve(RuAccentRepository, RuAccentRevision, name),
            size,
            sha256,
            "language_frontend");

    private static ManagedModelArtifactFile File(string path, string source, long size, string sha256, string purpose) => new()
    {
        RelativePath = path,
        SourceUrl = source,
        SizeBytes = size,
        Sha256 = sha256,
        Purpose = purpose
    };

    private static ManagedModelConsumer Consumer(string id, string name, string kind) => new()
    {
        Id = id,
        DisplayName = name,
        Kind = kind
    };

    private static string Resolve(string repository, string revision, string file) =>
        $"https://huggingface.co/{repository}/resolve/{revision}/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}";

    private static string ResolveModelScope(string repository, string file) =>
        $"https://modelscope.cn/api/v1/models/{repository}/repo?Revision=master&FilePath={Uri.EscapeDataString(file)}";

    private static string CombineIfRoot(string root, params string[] parts) => string.IsNullOrWhiteSpace(root)
        ? string.Empty
        : parts.Aggregate(root, Path.Combine);
}
