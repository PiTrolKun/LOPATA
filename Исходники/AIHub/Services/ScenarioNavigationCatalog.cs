namespace AIHub.Services;

public enum ScenarioNavigationKind { Direction, Group, Scenario }

public sealed record ScenarioNavigationNode(
    string Id, ScenarioNavigationKind Kind, string? ParentId,
    string TitleKey, string DescriptionKey, string Icon,
    bool IsAvailable, string EntryTargetId,
    IReadOnlyList<string> Tags, IReadOnlyList<string> RelatedIds);

public sealed record ScenarioNavigationTag(string Id, string TitleKey, string DescriptionKey, string TargetId);

/// <summary>Canonical user navigation. IDs are independent of translated labels and storage paths.</summary>
public static class ScenarioNavigationCatalog
{
    public const string Creation = "creation";
    public const string Analysis = "analysis";
    public const string Experiments = "experiments";
    public const string Utilities = "processing_utilities";
    public const string Literary = "literary";
    public const string Images = "image_analysis";
    public const string Sandbox = "uncertainty";
    public const string Finance = "finance";
    public const string Capture = "screen_capture";
    public const string ImageGeneration = "image_generation";
    public const string ImageUtility = "image_utility";
    public const string Music = "music";

    public static IReadOnlyList<ScenarioNavigationNode> Nodes { get; } = Array.AsReadOnly<ScenarioNavigationNode>(
    [
        Node(Creation, ScenarioNavigationKind.Direction, null, "Navigation.Creation", "Navigation.CreationHint", "create"),
        Node(Analysis, ScenarioNavigationKind.Direction, null, "Navigation.Analysis", "Navigation.AnalysisHint", "analyze"),
        Node(Experiments, ScenarioNavigationKind.Direction, null, "Navigation.Experiments", "Navigation.ExperimentsHint", "experiment"),
        Node(Utilities, ScenarioNavigationKind.Direction, null, "Navigation.Utilities", "Navigation.UtilitiesHint", "tools"),
        Node("utilities_capture", ScenarioNavigationKind.Group, Utilities, "Capture.Group", "", "tools"),
        Node(Capture, ScenarioNavigationKind.Scenario, "utilities_capture", "Capture.Title", "Capture.Description", "analyze",
            tags: ["screenshot", "screen_capture", "gif_capture", "video_capture"], related: []),
        Node("utilities_images", ScenarioNavigationKind.Group, Utilities, "Navigation.Images", "", "tools"),
        Node(ImageUtility, ScenarioNavigationKind.Scenario, "utilities_images", "ImageUtility.Title", "ImageUtility.Description", "tools",
            tags: ["image_resize", "image_upscale", "image_convert", "image_batch", "image_shell"], related: [ImageGeneration]),
        Node("creation_literature", ScenarioNavigationKind.Group, Creation, "Navigation.Literature", "", "create"),
        Node("creation_images", ScenarioNavigationKind.Group, Creation, "Navigation.Images", "", "create"),
        Node("creation_music", ScenarioNavigationKind.Group, Creation, "Music.Group", "", "create"),
        Node(Music, ScenarioNavigationKind.Scenario, "creation_music", "Music.Title", "Music.Description", "create",
            tags: ["music_preparation", "music_wishes", "music_performers", "music_text_tools", "music_player", "music_status", "music_generation", "music_expert", "music_presets"], related: []),
        Node(ImageGeneration, ScenarioNavigationKind.Scenario, "creation_images", "Generation.Title", "Generation.Description", "create",
            tags: ["image_generation", "image_variation", "generation_history", "image_autosave", "generation_model_switch", "prompt_spelling", "prompt_assistant", "image_reference", "image_metadata", "image_resize"], related: [Images]),
        Node("analysis_images", ScenarioNavigationKind.Group, Analysis, "Navigation.Images", "", "analyze"),
        Node("experiment_tests", ScenarioNavigationKind.Group, Experiments, "Navigation.Tests", "", "experiment"),
        Node("experiment_finance", ScenarioNavigationKind.Group, Experiments, "Finance.Group", "", "finance"),
        Node(Finance, ScenarioNavigationKind.Scenario, "experiment_finance", "Finance.Title", "Finance.Description", "finance",
            tags: ["finance", "budget", "expenses", "income", "external_coverage", "financial_behavior", "savings_discussion"], related: []),
        Node(Literary, ScenarioNavigationKind.Scenario, "creation_literature", "Literary.Title", "Literary.Description", "create",
            tags: ["advisor", "writer", "retelling", "rag", "jelly", "export"], related: [Images]),
        Node(Images, ScenarioNavigationKind.Scenario, "analysis_images", "ImageAnalysis.Scenario.Title", "ImageAnalysis.Scenario.Description", "analyze",
            tags: ["image_description", "batch_description", "speech", "export"], related: [Literary]),
        Node(Sandbox, ScenarioNavigationKind.Scenario, "experiment_tests", "WorkStart.ReasoningTitle", "WorkStart.ReasoningDescription", "experiment",
            tags: ["research", "tools", "executor"], related: [])
    ]);

    public static ScenarioNavigationNode Get(string id) => Nodes.First(node => node.Id == id);

    // Mechanic labels and canonical entrances share this catalog; no per-page tag lists.
    public static IReadOnlyList<ScenarioNavigationTag> CloudTags { get; } = Array.AsReadOnly<ScenarioNavigationTag>(
    [
        Tag("music_preparation", Music),
        Tag("music_generation", Music),
        Tag("music_expert", Music), Tag("music_presets", Music),
        Tag("music_wishes", Music), Tag("music_performers", Music), Tag("music_text_tools", Music), Tag("music_player", Music), Tag("music_status", Music),
        Tag("advisor", Literary), Tag("writer", Literary), Tag("retelling", Literary),
        Tag("rag", Literary), Tag("jelly", Literary), Tag("anchors", Literary),
        Tag("route", Literary), Tag("idea", Literary), Tag("import", Literary),
        Tag("export", Literary), Tag("calibration", Literary),
        Tag("image_description", Images), Tag("batch_description", Images), Tag("speech", Images),
        Tag("research", Sandbox), Tag("tools", Sandbox), Tag("executor", Sandbox),
        Tag("literature", "creation_literature"), Tag("images", "analysis_images"),
        Tag("experiments", Experiments), Tag("finance", Finance), Tag("budget", Finance),
        Tag("expenses", Finance), Tag("income", Finance), Tag("external_coverage", Finance), Tag("financial_behavior", Finance), Tag("savings_discussion", Finance),
        Tag("screenshot", Capture), Tag("screen_capture", Capture), Tag("gif_capture", Capture), Tag("video_capture", Capture),
        Tag("image_generation", ImageGeneration), Tag("image_variation", ImageGeneration),
        Tag("generation_history", ImageGeneration), Tag("image_autosave", ImageGeneration), Tag("generation_model_switch", ImageGeneration), Tag("prompt_spelling", ImageGeneration), Tag("prompt_assistant", ImageGeneration), Tag("image_reference", ImageGeneration), Tag("image_metadata", ImageGeneration), Tag("image_resize", "utilities_images"),
        Tag("image_upscale", ImageUtility), Tag("image_convert", ImageUtility), Tag("image_batch", ImageUtility), Tag("image_shell", ImageUtility)
    ]);

    public static ScenarioNavigationTag GetTag(string id) => CloudTags.First(tag => tag.Id == id);

    private static ScenarioNavigationTag Tag(string id, string target) =>
        new(id, "Cloud.Tag." + id, "Cloud.Description." + id, target);

    public static IEnumerable<ScenarioNavigationNode> Children(string? parentId) =>
        Nodes.Where(node => node.ParentId == parentId);

    private static ScenarioNavigationNode Node(string id, ScenarioNavigationKind kind, string? parent,
        string title, string hint, string icon, bool available = true, string[]? tags = null, string[]? related = null) =>
        new(id, kind, parent, title, hint, icon, available, id,
            Array.AsReadOnly(tags ?? []), Array.AsReadOnly(related ?? []));
}
