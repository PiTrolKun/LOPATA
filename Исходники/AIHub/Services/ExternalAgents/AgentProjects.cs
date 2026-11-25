using System.IO;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lopata.Updates;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private const string ExternalOrigin = "external_agent";
    private sealed record Scenario(string Id, string Title, string OpenIn);

    private sealed record ProjectType(string Id, string[] OpenActions, string Instructions, Type Input, string? FileExtension = null)
    {
        public Scenario Locate(LocalizationService language)
        {
            var node = ScenarioNavigationCatalog.Get(Id);
            var title = language.T(node.TitleKey);
            var path = new Stack<string>();
            path.Push(title);
            while (node.ParentId is { } parent)
            {
                node = ScenarioNavigationCatalog.Get(parent);
                path.Push(language.T(node.TitleKey));
            }
            return new(Id, title, string.Join(" › ", path.Concat(OpenActions.Select(language.T))));
        }
    }

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

    private static readonly ProjectType[] Scenarios = [
        new(ScenarioNavigationCatalog.VideoGeneration, ["VideoPrep.Open"],
            "Provide an idea and a title. Form is story or imagery. Scenes, shots, development, translations and reference prompts may be supplied as drafts. Missing images remain pending.", typeof(VideoDraft), ".lopataAIVid"),
        new(ScenarioNavigationCatalog.Literary, ["Literary.Select"], "Provide a premise and genre. The title becomes the work title and project folder name (up to 100 valid filename characters).", typeof(LiteraryDraft)),
        new(ScenarioNavigationCatalog.Music, ["Music.Projects.Title"], "Prepare lyrics, artist and comments. Optional duration is 1–360 seconds.", typeof(MusicDraft)),
        new(ScenarioNavigationCatalog.SongClip, ["SongClip.Projects"], "Prepare the lyrics, timings and visual prompt. The user can attach audio before generating.", typeof(SongDraft)),
        new(ScenarioNavigationCatalog.Shorts, ["Shorts.Projects.Manage"], "Prepare a description for a vertical video. Duration in seconds is 1–179.", typeof(ShortDraft)),
        new(ScenarioNavigationCatalog.VisualScenario, ["Visual.Projects"], "Provide the final image prompt for review before drawing.", typeof(VisualDraft)),
        new(ScenarioNavigationCatalog.GameSounds, ["GameSounds.Projects"], "Describe a sound effect and whether it loops.", typeof(SoundDraft)),
        new(ScenarioNavigationCatalog.SpeechAssets, ["SpeechAssets.Projects"], "Provide one utterance per line. Takes are generated on user request.", typeof(SpeechDraft)),
        new(ScenarioNavigationCatalog.OneC, ["OneC.Projects", "OneC.OpenProject"], "Provide a task description. Optional source code is a reference material.", typeof(OneCDraft)),
        new(ScenarioNavigationCatalog.CharacterKnowledge, ["CharacterKnowledge.Base"], "Each entry needs an input and an output. Type is chat_example, thought, character_trait, knowledge_or_memory or habit.", typeof(KnowledgeDraft)),
        new(ScenarioNavigationCatalog.NeuroConstructor, ["Neuro.ProMode", "Neuro.OpenProject"], "Choose pair or trio, cycles and attempts. Open the project in Pro mode to inspect the configuration.", typeof(NeuroDraft)),
        new(ScenarioNavigationCatalog.OfficeEditor, ["OfficeEditor.Open"], "Provide plain document text. Paragraphs and tabs are preserved in an editable workbook.", typeof(OfficeDraft), ".LOPATAdoxAI"),
        new(ScenarioNavigationCatalog.VectorEditor, ["Vector.Editor.Open"], "Provide a static, self-contained SVG using the editor's supported elements.", typeof(VectorDraft), ".lopataSVG"),
        new(ScenarioNavigationCatalog.ImageLayers, ["ImageLayers.Open"], "Provide an encoded source image (PNG, JPEG or WebP) in imageBase64.", typeof(LayersDraft), ".lopataIMGp"),
        new(ScenarioNavigationCatalog.AnimatedAssets, ["Animation.Projects", "Animation.OpenProject"], "Describe the reference image and each requested reaction. Supply referenceBase64 if the reference image is ready.", typeof(AnimationDraft), ".lopatazipP"),
        new(ScenarioNavigationCatalog.VideoEditor, ["VideoEditor.OpenProject"], "Provide timed narration segments on the timeline, with start and end in seconds.", typeof(EditorDraft), ".lopataVideo"),
        new(ScenarioNavigationCatalog.VideoUpscale, ["VideoUpscale.OpenProject"], "Prepare a scaling pipeline for a source the user selects later. Dimensions must be even and between 128 and 16384.", typeof(UpscaleDraft), ".lopataUpscale"),
        new(ScenarioNavigationCatalog.VideoConverter, ["VideoConverter.Advanced", "VideoConverter.OpenSettings"],
            "Prepare a local conversion queue. Defaults are MP4 + AV1, Auto processor, 10% target reduction and original resolution. Provide paths only for videos the user selected. Processing starts in LOPATA after review. The prepared queue cannot request deletion of originals.",
            typeof(ConverterDraft), ".lopata-converter.json"),
    ];

    public static object List(LocalizationService? language = null)
    {
        language ??= LocalizationService.ForCurrentInterface();
        return Scenarios.Select(type =>
        {
            var location = type.Locate(language);
            return new { scenario = location.Id, title = location.Title, openIn = location.OpenIn, fileExtension = type.FileExtension,
                mechanics = ScenarioMechanics(location.Id).Select(tag => tag.Id) };
        }).ToArray();
    }

    internal static IEnumerable<ScenarioNavigationNode> NavigationEntries() =>
        Scenarios.Select(s => ScenarioNavigationCatalog.Get(s.Id));

    public static object Describe(string scenario)
    {
        var type = Find(scenario);
        var location = type.Locate(LocalizationService.ForCurrentInterface());
        var language = LocalizationService.ForCurrentInterface();
        return new { scenario = location.Id, title = location.Title, openIn = location.OpenIn, fileExtension = type.FileExtension,
            instructions = type.Instructions, dataSchema = Schema(type),
            modelOperations = AgentModelPlan.Operations.GetValueOrDefault(scenario, []),
            mechanics = ScenarioMechanics(scenario).Select(tag => new { tag.Id, title = language.T(tag.TitleKey), description = language.T(tag.DescriptionKey) }) };
    }

    internal static object Contracts() => Scenarios.Select(type => new { type.Id, type.Instructions, schema = Schema(type) });

    private static IEnumerable<ScenarioNavigationTag> ScenarioMechanics(string scenario) =>
        ScenarioNavigationCatalog.CloudTags.Where(tag => tag.TargetId == scenario);

    private static JsonNode Schema(ProjectType type)
    {
        var schema = Json.GetJsonSchemaAsNode(type.Input, new() { TreatNullObliviousAsNonNullable = true });
        DescribeVideoOptions(type.Id, schema);
        return schema;
    }

    public static object Create(string scenario, JsonElement data, string? projectDirectory = null)
    {
        var type = Find(scenario);
        var draft = (Draft?)data.Deserialize(type.Input, Json) ?? throw new InvalidDataException("data must be an object.");
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200 || draft.Title.Any(char.IsControl))
            throw new InvalidDataException("title must contain 1 to 200 printable characters.");
        draft.DestinationDirectory = projectDirectory;
        return draft.Create(type.Locate(LocalizationService.ForCurrentInterface()));
    }

    private static ProjectType Find(string id) => Scenarios.FirstOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("Unknown scenario. Use the scenario list to select an available project type.");

    private abstract class Draft
    {
        public required string Title { get; init; }
        internal string? DestinationDirectory { get; set; }
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
