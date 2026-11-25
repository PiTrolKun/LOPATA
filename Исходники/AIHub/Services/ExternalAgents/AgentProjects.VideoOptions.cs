using System.IO;
using System.Text.Json.Nodes;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private static IEnumerable<SongClipVariant> SongVariants => SongClipVariantCatalog.VisibleVariants.Where(v => v.Available);
    private static readonly string[] Orientations = ["landscape", "portrait"];
    private static readonly int[] DenoResolutions = SongClipDeno.Sizes.Select(s => Math.Min(s.Width, s.Height)).Distinct().ToArray();
    private const string Standard = "standard";
    internal const string DenoOperation = "deno";
    internal const string AcceleratedDenoOperation = "deno-accelerated";
    internal static readonly IReadOnlyDictionary<string, ShortsGenerationProfile> ShortsOperations =
        new Dictionary<string, ShortsGenerationProfile>(StringComparer.Ordinal)
        {
            ["generate"] = new(false, false, false),
            ["generate/deno"] = new(false, true, false),
            ["generate/deno-accelerated"] = new(false, true, true),
            ["generate/from-song"] = new(true, true, true)
        };

    private static ShortsGenerationProfile ShortsProfile(bool fromSong, string? variant, bool? accelerate)
    {
        var selected = variant ?? (fromSong ? DenoOperation : Standard);
        if (selected == DenoOperation && (!fromSong || accelerate != false))
            return ShortsGenerationProfile.FromDraft(new ShortsDraft(FromSong: fromSong) { UseDeno = true, AccelerateDeno = accelerate == true });
        if (selected == Standard && !fromSong && accelerate != true) return default;
        throw new InvalidDataException("Use standard or deno for a description. Shorts from a song use deno with acceleration.");
    }

    private sealed class DenoProjectOptions
    {
        public int Resolution { get; init; } = 1080;
        public string Orientation { get; init; } = "landscape";
        public bool Accelerate { get; init; }

        internal SongClipDenoSettings Resolve()
        {
            if (!Orientations.Contains(Orientation) || !DenoResolutions.Contains(Resolution))
                throw new InvalidDataException("deno requires a supported resolution and orientation. Read project_instructions.");
            return SongClipDeno.Sizes.Single(size => Math.Min(size.Width, size.Height) == Resolution
                && (size.Width < size.Height) == (Orientation == "portrait")) with { Accelerate = Accelerate };
        }
    }

    private static void DescribeVideoOptions(string scenario, JsonNode schema)
    {
        var properties = schema["properties"]!;
        switch (scenario)
        {
            case ScenarioNavigationCatalog.SongClip:
                Choices(properties["variant"]!, SongVariants.Select(v => v.Id));
                Numbers(properties["piH3Steps"]!, PiH3Components.StepOptions, allowNull: true);
                properties["piH3Steps"]!["description"] = "Applies only to the pi-h3-dwd variant.";
                DescribeDeno(properties["deno"]!, "Applies only to the deno variant.");
                break;
            case ScenarioNavigationCatalog.Shorts:
                Choices(properties["variant"]!, [Standard, DenoOperation], allowNull: true);
                properties["fromSong"]!["description"] = "The user selects source audio in LOPATA. Song shorts use Deno with acceleration.";
                properties["accelerate"]!["description"] = "For Deno. The default is on for song shorts and off for a description.";
                break;
            case ScenarioNavigationCatalog.VideoGeneration:
                Choices(properties["model"]!, VideoGeneratorPlan.AvailableCandidates.Select(c => c.Id), allowNull: true);
                DescribeDeno(properties["deno"]!, "Selects Deno RTX for the H3 workspace.");
                break;
            case ScenarioNavigationCatalog.VideoUpscale:
                Choices(properties["preset"]!, ["manual", DenoOperation]);
                Choices(properties["algorithm"]!, UpscaleCatalog.Algorithms, allowNull: true);
                properties["algorithm"]!["description"] = "Applies to the manual preset.";
                break;
        }

        static void Choices(JsonNode property, IEnumerable<string> choices, bool allowNull = false)
        {
            var values = new JsonArray(choices.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            if (allowNull) values.Add((JsonNode?)null);
            property["enum"] = values;
        }
        static void Numbers(JsonNode property, IEnumerable<int> choices, bool allowNull = false)
        {
            var values = new JsonArray(choices.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            if (allowNull) values.Add((JsonNode?)null);
            property["enum"] = values;
        }
        static void DescribeDeno(JsonNode property, string description)
        {
            property["description"] = description;
            var nested = property["properties"]!;
            Numbers(nested["resolution"]!, DenoResolutions);
            Choices(nested["orientation"]!, Orientations);
            nested["accelerate"]!["description"] = "Optional speed-up. Lip movement may shift by one or two frames.";
        }
    }
}
