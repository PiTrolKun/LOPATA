using System.IO;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Lopata.Updates;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private const string ExternalOrigin = "external_agent";
    private sealed record Scenario(string Id, string Title, string OpenIn, string Instructions, Type Input);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    private static readonly Scenario[] Scenarios = [
        new(ScenarioNavigationCatalog.VideoGeneration, "Video preparation", ".lopataAIVid",
            "Provide an idea and a title. Form is story or imagery. Scenes, shots, development, translations and reference prompts may be supplied as drafts. Missing images remain pending.", typeof(VideoDraft)),
        new(ScenarioNavigationCatalog.Literary, "Literary studio", "Literary studio → Projects", "Provide a premise and genre. The title becomes the work title and project folder name (up to 100 valid filename characters).", typeof(LiteraryDraft)),
        new(ScenarioNavigationCatalog.Music, "Music", "Music → Projects", "Prepare lyrics, artist and comments. Optional duration is 1–360 seconds.", typeof(MusicDraft)),
        new(ScenarioNavigationCatalog.SongClip, "Song to clip", "Song to clip → Projects", "Prepare the lyrics, timings and visual prompt. The user can attach audio before generating.", typeof(SongDraft)),
        new(ScenarioNavigationCatalog.Shorts, "Shorts", "Shorts → Projects", "Prepare a description for a vertical video. Duration in seconds is 1–179.", typeof(ShortDraft)),
        new(ScenarioNavigationCatalog.VisualScenario, "Step-by-step image creation", "Step-by-step image creation → Projects", "Provide the final image prompt for review before drawing.", typeof(VisualDraft)),
        new(ScenarioNavigationCatalog.GameSounds, "Game sounds", "Game sounds → Projects", "Describe a sound effect and whether it loops.", typeof(SoundDraft)),
        new(ScenarioNavigationCatalog.SpeechAssets, "Speech assets", "Speech assets → Projects", "Provide one utterance per line. Takes are generated on user request.", typeof(SpeechDraft)),
        new(ScenarioNavigationCatalog.OneC, "1C", "1C → Projects", "Provide a task description. Optional source code is a reference material.", typeof(OneCDraft)),
        new(ScenarioNavigationCatalog.CharacterKnowledge, "Character knowledge", "Character knowledge → Bases", "Each entry needs an input and an output. Type is chat_example, thought, character_trait, knowledge_or_memory or habit.", typeof(KnowledgeDraft)),
        new(ScenarioNavigationCatalog.NeuroConstructor, "Neuroconstructor", "Neuroconstructor → Projects", "Choose pair or trio, cycles and attempts. Open the project in Pro mode to inspect the configuration.", typeof(NeuroDraft)),
        new(ScenarioNavigationCatalog.OfficeEditor, "AI Office", ".LOPATAdoxAI", "Provide plain document text. Paragraphs and tabs are preserved in an editable workbook.", typeof(OfficeDraft)),
        new(ScenarioNavigationCatalog.VectorEditor, "Vector editor", ".lopataSVG", "Provide a static, self-contained SVG using the editor's supported elements.", typeof(VectorDraft)),
        new(ScenarioNavigationCatalog.ImageLayers, "Image layers", ".lopataIMGp", "Provide an encoded source image (PNG, JPEG or WebP) in imageBase64.", typeof(LayersDraft)),
        new(ScenarioNavigationCatalog.AnimatedAssets, "Animated asset workshop", ".lopatazipP", "Describe the reference image and each requested reaction. Supply referenceBase64 if the reference image is ready.", typeof(AnimationDraft)),
        new(ScenarioNavigationCatalog.VideoEditor, "Video editor", ".lopataVideo", "Provide timed narration segments on the timeline, with start and end in seconds.", typeof(EditorDraft)),
        new(ScenarioNavigationCatalog.VideoUpscale, "Video upscale", ".lopataUpscale", "Prepare a scaling pipeline for a source the user selects later. Dimensions must be even and between 128 and 16384.", typeof(UpscaleDraft)),
    ];

    public static object List() => Scenarios.Select(s => new { scenario = s.Id, title = s.Title, openIn = s.OpenIn }).ToArray();

    internal static IEnumerable<ScenarioNavigationNode> NavigationEntries() =>
        Scenarios.Select(s => ScenarioNavigationCatalog.Get(s.Id));

    public static object Describe(string scenario)
    {
        var s = Find(scenario);
        return new { scenario = s.Id, title = s.Title, openIn = s.OpenIn, instructions = s.Instructions,
            dataSchema = Json.GetJsonSchemaAsNode(s.Input, new() { TreatNullObliviousAsNonNullable = true }) };
    }

    public static object Create(string scenario, JsonElement data, string? projectDirectory = null)
    {
        var s = Find(scenario);
        var draft = (Draft?)data.Deserialize(s.Input, Json) ?? throw new InvalidDataException("data must be an object.");
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200 || draft.Title.Any(char.IsControl))
            throw new InvalidDataException("title must contain 1 to 200 printable characters.");
        draft.DestinationDirectory = projectDirectory;
        return draft.Create(s);
    }

    private static Scenario Find(string id) => Scenarios.FirstOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("Unknown scenario. Use the scenario list to select an available project type.");

    private abstract class Draft
    {
        public required string Title { get; init; }
        internal string? DestinationDirectory { private get; set; }
        internal abstract object Create(Scenario scenario);
        protected object SaveFile(Scenario scenario, string extension, Action<string> save)
        {
            var directory = FileProjectDirectory();
            var stem = string.Concat(Title.Take(64).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var file = Path.Combine(directory, $"Project-{stem}-{Guid.NewGuid():N}{extension}");
            SafeUpdatePath.RejectLinks(file);
            save(file);
            CollectionExportJournal.Record(file, scenario.Id);
            return new { scenario = scenario.Id, title = Title, origin = ExternalOrigin, path = file, openIn = scenario.OpenIn };
        }
        protected object InLibrary(Scenario scenario, string projectId) =>
            new { scenario = scenario.Id, title = Title, origin = ExternalOrigin, projectId, openIn = scenario.OpenIn };
        protected string FileProjectDirectory()
        {
            if (DestinationDirectory is not null) return DestinationDirectory;
            var settings = new StorageSettingsStore().LoadOrCreate();
            var location = settings.Results.Locations.Select(x => x.Path).FirstOrDefault(Path.IsPathFullyQualified)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(location)) throw new IOException("Configure a results folder in LOPATA.");
            var directory = Path.Combine(location, "LOPATA", "Projects");
            SafeUpdatePath.RejectLinks(directory);
            Directory.CreateDirectory(directory);
            return directory;
        }
        protected static void RequireText(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{field} must contain text.");
        }
    }
}
