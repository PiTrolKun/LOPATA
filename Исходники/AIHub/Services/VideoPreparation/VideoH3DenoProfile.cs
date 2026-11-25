using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using AIHub.Models;
using AIHub.Services.SongClip;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoH3DenoProfile(bool Accelerate)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Output { get; init; }
    internal int Width => 288;
    internal int Height => 512;
    internal int Steps => 30;
    internal const string Worker = "h3_deno.py";
    internal const string Resource = "AIHub.H3Deno";

    internal static VideoH3DenoProfile? FromSettings(VideoH3Settings settings) => settings.DenoOutput == 0 ? null
        : new(settings.DenoAccelerate) { Output = settings.DenoOutput };

    internal VideoH3DenoPass[] Passes(VideoH3Settings settings)
    {
        var (wide, high) = settings.Aspect == "9:16" ? (false, true) : (settings.Aspect == "16:9", false);
        var (first, third) = settings.Steps == 30 ? (20, 5)
            : (Math.Clamp((int)Math.Round(settings.Steps * 2 / 3d), 1, settings.Steps - 2), Math.Max(1, settings.Steps / 6));
        return [Pass(first, 512, 288), Pass(settings.Steps - first - third, 768, 448), Pass(third, 1024, 576)];

        VideoH3DenoPass Pass(int count, int w, int h)
        {
            if (wide || high) return high ? new(count, h, w) : new(count, w, h);
            var size = new VideoH3Settings { Aspect = settings.Aspect };
            VideoH3WorkspaceState.Resolution(size, w * h / 1048576d);
            return new(count, size.Width, size.Height);
        }
    }

    internal void Apply(VideoH3Settings settings)
    {
        settings.Steps = settings.DenoSteps == 0 ? Steps : settings.DenoSteps;
        var first = Passes(settings)[0];
        settings.Width = first.Width;
        settings.Height = first.Height;
        settings.Upscale = false;
        settings.Attention = "pytorch";
        settings.Spectrum = false;
        settings.Turbo = "off";
        settings.ContentLoraFile = "";
        settings.OutputHeight = 0; // The assembled source goes to the RTX finisher once.
        if (Accelerate) settings.VramReserveGb = Math.Max(4, settings.VramReserveGb);
    }

    internal static ManagedModelArtifactCard LatentCard(string root) =>
        VideoH3Catalog.Cards(root).Single(card => card.ModelArtifactId == "video-h3-upscale-model");

    internal ManagedModelArtifactCard[] AdditionalCards(string root, bool lite) => lite
        ? [LatentCard(root), .. SongClipDenoComponents.Cards(root, Accelerate)]
        : SongClipDenoComponents.Cards(root, Accelerate);

    internal static PinnedTreeFile Pin()
    {
        using var script = typeof(VideoH3DenoProfile).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("VideoPrep.H3FilesRequired");
        return new(Worker, script.Length, Convert.ToHexString(SHA256.HashData(script)));
    }
}

internal sealed record VideoH3DenoPass(int Steps, int Width, int Height);
