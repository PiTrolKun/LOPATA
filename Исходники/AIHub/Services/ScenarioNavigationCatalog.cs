namespace AIHub.Services;

public enum ScenarioNavigationKind { Direction, Group, Scenario }

public sealed record ScenarioNavigationNode(
    string Id, ScenarioNavigationKind Kind, string? ParentId,
    string TitleKey, string DescriptionKey, string Icon,
    bool IsAvailable, string EntryTargetId,
    IReadOnlyList<string> Tags, IReadOnlyList<string> RelatedIds, bool IsPlaceholder = false);

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
    public const string VideoGeneration = "video_generation";
    public const string VectorGeneration = "vector_generation";
    public const string VectorEditor = "vector_editor";
    public const string ImageUtility = "image_utility";
    public const string Music = "music";
    public const string Publisher = "github_vk_publisher";
    public const string RagBooks = "rag_book_preparation";
    public const string Metadata = "file_metadata";
    public const string LyricVideo = "music_video";
    public const string VideoEditor = "video_editor";
    public const string BookConversion = "text_to_book";
    public const string Dictation = "voice_input";

    public static IReadOnlyList<ScenarioNavigationNode> Nodes { get; } = Array.AsReadOnly<ScenarioNavigationNode>(
    [
        Node(Creation, ScenarioNavigationKind.Direction, null, "Navigation.Creation", "Navigation.CreationHint", "create"),
        Node(Analysis, ScenarioNavigationKind.Direction, null, "Navigation.Analysis", "Navigation.AnalysisHint", "analyze"),
        Node(Experiments, ScenarioNavigationKind.Direction, null, "Navigation.Experiments", "Navigation.ExperimentsHint", "experiment"),
        Node(Utilities, ScenarioNavigationKind.Direction, null, "Navigation.Utilities", "Navigation.UtilitiesHint", "tools", tags: ["audio_convert", "audio_shell", "background_pause", "component_audit"]),
        Node("utilities_text", ScenarioNavigationKind.Group, Utilities, "BookConverter.Group", "", "tools"),
        Node(Dictation,ScenarioNavigationKind.Scenario,"utilities_text","Dictation.Title","Dictation.Help","tools",tags:["voice_input"]),
        Node(BookConversion, ScenarioNavigationKind.Scenario, "utilities_text", "BookConverter.Title", "BookConverter.Description", "tools",
            tags: ["text_to_book", "book_merge", "book_shell", "book_view", "book_editor", "book_smart", "book_logs"], related: [RagBooks]),
        Node("utilities_knowledge", ScenarioNavigationKind.Group, Utilities, "RagBooks.Group", "", "tools"),
        Node(RagBooks, ScenarioNavigationKind.Scenario, "utilities_knowledge", "RagBooks.Title", "RagBooks.Description", "tools",
            tags: ["rag_books", "rag_lexicon", "stress_tables"], related: []),
        Node(Metadata, ScenarioNavigationKind.Scenario, "utilities_knowledge", "Metadata.Title", "Metadata.Description", "tools",
            tags: ["file_metadata"], related: []),
        Node("utilities_publishing", ScenarioNavigationKind.Group, Utilities, "Publisher.Group", "Publisher.Description", "tools"),
        Node(Publisher, ScenarioNavigationKind.Scenario, "utilities_publishing", "Publisher.Title", "Publisher.Description", "tools",
            tags: ["github_releases", "vk_publishing", "publishing_queue"], related: []),
        Node("utilities_capture", ScenarioNavigationKind.Group, Utilities, "Capture.Group", "", "tools"),
        Node(Capture, ScenarioNavigationKind.Scenario, "utilities_capture", "Capture.Title", "Capture.Description", "analyze",
            tags: ["screenshot", "screen_capture", "gif_capture", "video_capture"], related: []),
        Node("utilities_images", ScenarioNavigationKind.Group, Utilities, "Navigation.Images", "", "tools"),
        Node("utilities_video", ScenarioNavigationKind.Group, Utilities, "LyricVideo.Group", "", "tools"),
        Node(LyricVideo, ScenarioNavigationKind.Scenario, "utilities_video", "LyricVideo.Title", "LyricVideo.Description", "tools",
            tags: ["lyric_video", "matrix_rain", "video_rain_text"], related: [Music]),
        Node(VideoEditor, ScenarioNavigationKind.Scenario, "utilities_video", "VideoEditor.Title", "VideoEditor.Description", "tools",
            tags: ["video_editor", "video_narration", "video_subtitles", "video_svg"], related: [VectorGeneration, Capture]),
        Node(ImageUtility, ScenarioNavigationKind.Scenario, "utilities_images", "ImageUtility.Title", "ImageUtility.Description", "tools",
            tags: ["image_resize", "image_upscale", "image_convert", "image_batch", "image_shell"], related: [ImageGeneration]),
        Node("creation_video", ScenarioNavigationKind.Group, Creation, "Navigation.Video", "", "create"),
        Node("creation_images", ScenarioNavigationKind.Group, Creation, "Navigation.Images", "", "create"),
        Node("creation_music", ScenarioNavigationKind.Group, Creation, "Music.Group", "", "create"),
        Node("creation_literature", ScenarioNavigationKind.Group, Creation, "Navigation.Literature", "", "create"),
        Node(VideoGeneration, ScenarioNavigationKind.Scenario, "creation_video", "VideoGeneration.Title", "VideoGeneration.Description", "create",
            tags: ["video_generation", "video_concept", "video_concept_ai", "video_development", "video_world", "video_assistant_warmup", "video_reference_prompts", "video_storyboard", "video_preparation_export", "video_generator_plan", "video_h3_preparation"]),
        Node(Music, ScenarioNavigationKind.Scenario, "creation_music", "Music.Title", "Music.Description", "create",
            tags: ["music_preparation", "music_ace_ideas", "music_ace_retake", "music_score_plans", "music_score_editor", "music_score_sections", "music_midi", "music_melody", "music_wishes", "music_performers", "music_text_tools", "music_rhyme", "music_rhyme_creative", "music_player", "music_lyrics_sync", "music_status", "music_generation", "music_expert", "music_presets", "music_models", "music_formats", "music_metadata", "music_stems", "music_audio_cover", "music_audio_references", "music_audio_processing", "music_cover", "music_examples", "music_studio", "music_tuning", "music_diffrhythm", "music_heartmula", "music_model_remove", "music_poetry", "music_poetry_sessions", "music_poetry_assembly", "music_poetry_reasoning", "music_poetry_search", "music_lyrics_library", "music_lora"], related: []),
        Node(ImageGeneration, ScenarioNavigationKind.Scenario, "creation_images", "Generation.Title", "Generation.Description", "create",
            tags: ["image_generation", "image_variation", "generation_history", "image_autosave", "generation_model_switch", "prompt_spelling", "prompt_assistant", "image_reference", "image_metadata", "image_resize"], related: [Images]),
        Node(VectorGeneration, ScenarioNavigationKind.Scenario, "creation_images", "Vector.Title", "Vector.Description", "create", tags: ["vector_graphics", "svg_generation"], related: [ImageGeneration]),
        Node(VectorEditor, ScenarioNavigationKind.Scenario, "utilities_images", "Vector.Editor.Title", "Vector.Editor.Description", "tools", tags: ["svg_viewer", "svg_editor", "svg_project", "svg_plasticity"], related: [VectorGeneration]),
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
        new ScenarioNavigationTag("component_audit", "ComponentAudit.Title", "ComponentAudit.TagDescription", Utilities),
        new ScenarioNavigationTag("voice_input","Dictation.Title","Dictation.Help",Dictation),
        Tag("engine_sets", Creation),
        Tag("text_to_book", BookConversion), Tag("book_merge", BookConversion), Tag("book_shell", BookConversion),
        Tag("book_view", BookConversion), Tag("book_editor", BookConversion), Tag("book_smart", BookConversion), Tag("book_logs", BookConversion),
        Tag("lyric_video", LyricVideo), Tag("matrix_rain", LyricVideo), Tag("video_rain_text", LyricVideo),
        Tag("video_editor", VideoEditor), Tag("video_narration", VideoEditor), Tag("video_subtitles", VideoEditor), Tag("video_svg", VideoEditor),
        Tag("video_linked_selection", VideoEditor), Tag("video_audio_stretch", VideoEditor), Tag("video_waveform", VideoEditor),
        Tag("video_project_archive", VideoEditor), Tag("video_expanded_preview", VideoEditor),
        Tag("video_background_music", VideoEditor), Tag("video_narrator_models", VideoEditor),
        Tag("video_generation", VideoGeneration), Tag("video_concept", VideoGeneration), Tag("video_concept_ai", VideoGeneration), Tag("video_development", VideoGeneration), Tag("video_world", VideoGeneration), Tag("video_assistant_warmup", VideoGeneration), Tag("video_reference_prompts", VideoGeneration),
        Tag("video_storyboard", VideoGeneration), Tag("video_preparation_export", VideoGeneration), Tag("video_generator_plan", VideoGeneration), Tag("video_h3_preparation", VideoGeneration),
        Tag("music_preparation", Music), Tag("music_ace_ideas", Music), Tag("music_ace_retake", Music), Tag("music_score_plans", Music), Tag("music_score_editor", Music), Tag("music_score_sections", Music), Tag("music_midi", Music), Tag("music_melody", Music),
        Tag("music_poetry", Music), Tag("music_poetry_sessions", Music), Tag("music_poetry_assembly", Music),
        Tag("music_generation", Music), Tag("music_poetry_reasoning", Music), Tag("music_lyrics_sync", Music), Tag("music_poetry_search", Music),
        Tag("music_formats", Music), Tag("music_metadata", Music), Tag("music_stems", Music), Tag("music_audio_cover", Music), Tag("music_audio_references", Music), Tag("music_audio_processing", Music), Tag("music_cover", Music), Tag("music_projects", Music), Tag("music_history", Music), Tag("music_examples", Music),
        Tag("music_expert", Music), Tag("music_presets", Music), Tag("music_models", Music), Tag("music_tuning", Music),
        Tag("music_model_switch", Music), Tag("music_model_remove", Music), Tag("music_bf16", Music), Tag("music_studio", Music), Tag("music_diffrhythm", Music), Tag("music_heartmula", Music),
        Tag("music_wishes", Music), Tag("music_performers", Music), Tag("music_text_tools", Music), Tag("music_rhyme", Music), Tag("music_rhyme_creative", Music), Tag("music_player", Music), Tag("music_status", Music), Tag("music_lyrics_library", Music), Tag("music_lora", Music),
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
        Tag("vector_graphics", VectorGeneration), Tag("svg_generation", VectorGeneration),
        Tag("svg_viewer", VectorEditor), Tag("svg_editor", VectorEditor), Tag("svg_project", VectorEditor), Tag("svg_plasticity", VectorEditor),
        Tag("image_generation", ImageGeneration), Tag("image_variation", ImageGeneration),
        Tag("generation_history", ImageGeneration), Tag("image_autosave", ImageGeneration), Tag("generation_model_switch", ImageGeneration), Tag("prompt_spelling", ImageGeneration), Tag("prompt_assistant", ImageGeneration), Tag("image_reference", ImageGeneration), Tag("image_metadata", ImageGeneration), Tag("image_resize", "utilities_images"),
        Tag("image_upscale", ImageUtility), Tag("image_convert", ImageUtility), Tag("image_batch", ImageUtility), Tag("image_shell", ImageUtility),
        Tag("github_releases", Publisher), Tag("vk_publishing", Publisher), Tag("publishing_queue", Publisher),
        Tag("audio_convert", Utilities), Tag("audio_shell", Utilities), Tag("rag_books", RagBooks), Tag("rag_lexicon", RagBooks), Tag("stress_tables", RagBooks), Tag("file_metadata", Metadata), Tag("background_pause", Utilities)
    ]);

    public static ScenarioNavigationTag GetTag(string id) => CloudTags.First(tag => tag.Id == id);

    private static ScenarioNavigationTag Tag(string id, string target) =>
        new(id, "Cloud.Tag." + id, "Cloud.Description." + id, target);

    public static IEnumerable<ScenarioNavigationNode> Children(string? parentId) =>
        Nodes.Where(node => node.ParentId == parentId);

    private static ScenarioNavigationNode Node(string id, ScenarioNavigationKind kind, string? parent,
        string title, string hint, string icon, bool available = true, string[]? tags = null, string[]? related = null, bool placeholder = false) =>
        new(id, kind, parent, title, hint, icon, available, id,
            Array.AsReadOnly(tags ?? []), Array.AsReadOnly(related ?? []), placeholder);
}
