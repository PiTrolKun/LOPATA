using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VideoEditor;

public sealed record VideoExportOptions(int Width, int Height, double Fps, double From, double To, bool EmbeddedSubtitles, bool SeparateSrt, int BitrateMbps = 12);
internal static class VideoExport
{
    internal static string N(double x) => x.ToString("0.########", CultureInfo.InvariantCulture);
    public static async Task<string> RunAsync(VideoProject project, string destination, VideoExportOptions options, string operationId, Action<double>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new InvalidDataException("VideoEditor.InvalidProject");
        project.Validate(); Validate(project, destination, options); VideoProjectStore.VerifySources(project); await VideoRuntime.PrepareAsync(token);
        var work = Path.Combine(VideoProjectStore.Work(project.Id), "Export", operationId); Directory.CreateDirectory(work);
        var temporary = Path.Combine(work, "result" + Path.GetExtension(destination));
        if (File.Exists(temporary)) File.Delete(temporary);
        var args = new List<string> { "-hide_banner", "-v", "warning", "-nostdin", "-n", "-filter_complex_threads", "1", "-f", "lavfi", "-i", $"color=c=black:s={project.Width}x{project.Height}:r={N(options.Fps)}:d={N(project.Duration)}", "-f", "lavfi", "-i", $"anullsrc=r=48000:cl=stereo:d={N(project.Duration)}" };
        var graph = new List<string> { "[0:v]format=rgba[base0]" }; var audios = new List<string> { "[1:a]" }; var input = 2; var layer = 0;
        foreach (var track in project.Tracks.Where(t => !t.Hidden && !t.Muted))
        foreach (var clip in track.Clips.OrderBy(c => c.Start))
        {
            var asset = clip.AssetId is not null ? project.Asset(clip.AssetId) : null;
            var path = asset?.Path;
            var image = clip.Text is not null || asset?.Kind is "image" or "svg";
            var framedVector = false;
            if (track.Kind == "video" && (clip.Text is not null || asset?.Kind == "svg"))
            {
                path = Path.Combine(work, clip.Id + ".png");
                if (!File.Exists(path))
                {
                    var svg = clip.Text is not null ? TitleSvg(clip.Text, project.Width, project.Height) : await File.ReadAllTextAsync(asset!.Path, token);
                    var frameAsset = asset ?? new VideoAsset { Width = project.Width, Height = project.Height };
                    var frame = VideoFraming.Geometry(project, frameAsset, clip);
                    var original = XElement.Parse(svg); original.SetAttributeValue("x", 0); original.SetAttributeValue("y", 0);
                    original.SetAttributeValue("width", frameAsset.Width); original.SetAttributeValue("height", frameAsset.Height);
                    var viewport = new XElement(XNamespace.Get("http://www.w3.org/2000/svg") + "svg", new XAttribute("width", frame.Width), new XAttribute("height", frame.Height),
                        new XAttribute("viewBox", $"{frame.CropX} {frame.CropY} {frame.CropWidth} {frame.CropHeight}"), new XAttribute("preserveAspectRatio", "none"), original);
                    await VectorRenderer.RenderFrameAsync(viewport.ToString(SaveOptions.DisableFormatting), path, frame.Width, frame.Height, token);
                }
                framedVector = true;
            }
            if (path is null) continue;
            if (image) args.AddRange(["-loop", "1", "-t", N(clip.Duration * clip.Speed)]);
            else args.AddRange(["-ss", N(clip.SourceIn), "-t", N(clip.Freeze ? 1 / options.Fps : clip.Duration * clip.Speed)]);
            args.AddRange(["-i", path]);
            if (track.Kind == "audio")
            {
                if (asset?.AudioStream is null or < 0) throw new InvalidDataException("VideoEditor.UnsupportedMedia");
                var a = $"[{input}:{asset.AudioStream}]asetpts=PTS-STARTPTS,{Tempo(clip.Speed)},volume={N(track.Volume * clip.Volume)}";
                if (clip.FadeIn > 0) a += $",afade=t=in:st=0:d={N(Math.Min(clip.Duration, clip.FadeIn))}";
                if (clip.FadeOut > 0) a += $",afade=t=out:st={N(Math.Max(0, clip.Duration - clip.FadeOut))}:d={N(clip.FadeOut)}";
                a += $",adelay={N(Math.Round(clip.Start * 1000))}:all=1[a{input}]"; graph.Add(a); audios.Add($"[a{input}]");
            }
            else
            {
                var stream = image ? "v:0" : asset!.VideoStream.ToString(CultureInfo.InvariantCulture);
                var filters = $"[{input}:{stream}]setpts=(PTS-STARTPTS)/{N(clip.Speed)},format=rgba";
                if (clip.Freeze) filters += $",trim=end_frame=1,tpad=stop_mode=clone:stop_duration={N(clip.Duration)}";
                if (!framedVector) {
                    var frameAsset = asset ?? new VideoAsset { Width = project.Width, Height = project.Height };
                    var geometry = VideoFraming.Geometry(project, frameAsset, clip);
                    filters += $",crop={geometry.CropWidth}:{geometry.CropHeight}:{geometry.CropX}:{geometry.CropY},scale={geometry.Width}:{geometry.Height}";
                }
                filters += ",setsar=1";
                if (clip.Contrast != 1 || clip.Brightness != 0) { var expression = $"clip((val-128)*{N(clip.Contrast)}+128+{N(clip.Brightness * 255)},0,255)"; filters += $",lutrgb=r='{expression}':g='{expression}':b='{expression}'"; }
                if (clip.Saturation != 1) filters += $",hue=s={N(clip.Saturation)}";
                if (clip.Rotation != 0) { var angle = N(clip.Rotation * Math.PI / 180); filters += $",rotate={angle}:ow=rotw({angle}):oh=roth({angle}):c=none"; }
                if (clip.Opacity < 1) filters += $",lutrgb=a='val*{N(clip.Opacity)}'";
                if (clip.FadeIn > 0) filters += $",fade=t=in:st=0:d={N(clip.FadeIn)}:alpha=1";
                if (clip.FadeOut > 0) filters += $",fade=t=out:st={N(Math.Max(0, clip.Duration - clip.FadeOut))}:d={N(clip.FadeOut)}:alpha=1";
                filters += $",setpts=PTS+{N(clip.Start)}/TB[v{input}]"; graph.Add(filters);
                graph.Add($"[base{layer}][v{input}]overlay=x=(W-w)/2+{N(clip.X)}:y=(H-h)/2+{N(clip.Y)}:eof_action=pass:repeatlast=0:enable='gte(t,{N(clip.Start)})*lt(t,{N(clip.Start + clip.Duration)})'[base{++layer}]");
            }
            input++;
        }
        graph.Add($"[base{layer}]trim=start={N(options.From)}:end={N(options.To)},setpts=PTS-STARTPTS,scale={options.Width}:{options.Height},format=yuv420p[vout]");
        graph.Add(string.Concat(audios) + $"amix=inputs={audios.Count}:normalize=0,alimiter=limit=0.95:latency=1,atrim=start={N(options.From)}:end={N(options.To)},asetpts=PTS-STARTPTS[aout]");
        var graphPath = Path.Combine(work, "graph.txt"); await File.WriteAllTextAsync(graphPath, string.Join(";\n", graph), new UTF8Encoding(false), token);
        var subtitlePaths = new List<(string Path, string Language, string Title, bool Default, bool Forced)>();
        foreach (var group in SubtitleGroups(project, options))
        {
            var subs = VideoSubtitles.Srt(group, options.From, options.To); if (subs.Length == 0) continue;
            var subPath = Path.Combine(work, "subtitles-" + subtitlePaths.Count + ".srt"); await File.WriteAllTextAsync(subPath, subs, new UTF8Encoding(false), token);
            var first = group.First(); subtitlePaths.Add((subPath, first.Language, first.SubtitleTitle, first.SubtitleDefault, first.SubtitleForced)); if (options.EmbeddedSubtitles) args.AddRange(["-i", subPath]);
        }
        args.AddRange(["-/filter_complex", graphPath, "-map", "[vout]", "-map", "[aout]", "-c:v", "libopenh264", "-b:v", options.BitrateMbps + "M", "-g", Math.Max(1, (int)Math.Round(options.Fps)).ToString(CultureInfo.InvariantCulture), "-c:a", "aac", "-b:a", "192k", "-r", N(options.Fps), "-t", N(options.To - options.From)]);
        if (options.EmbeddedSubtitles) for (var s = 0; s < subtitlePaths.Count; s++) {
            var sub = subtitlePaths[s]; var flags = new List<string>(); if (sub.Default) flags.Add("default"); if (sub.Forced) flags.Add("forced");
            args.AddRange(["-map", (input + s) + ":s:0", "-metadata:s:s:" + s, "language=" + ContainerLanguage(sub.Language), "-metadata:s:s:" + s, "title=" + sub.Title, "-disposition:s:" + s, flags.Count > 0 ? string.Join('+', flags) : "0"]);
        }
        if (options.EmbeddedSubtitles && subtitlePaths.Count > 0) args.AddRange(["-c:s", Path.GetExtension(destination).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "subrip" : "mov_text"]);
        var facts = new OutputProvenance { Scenario = "video-editor/export", CreatedUtc = project.CreatedUtc, Models = project.Assets.SelectMany(a => a.Models).Concat(project.Narration.SelectMany(n => n.Models)).Distinct().ToArray(), ModelHistory = "recorded-project; imported-history-may-be-unknown" };
        args.AddRange(["-metadata", "encoded_by=LOPATA " + facts.ProgramVersion, "-metadata", "LOPATA_VIDEO=" + facts.Json(), "-metadata", "LOPATA_OPERATION=" + operationId, "-metadata", "creation_time=" + facts.ExportedUtc.ToString("O")]);
        if (Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-movflags", "+faststart+use_metadata_tags"]);
        args.AddRange(["-progress", "pipe:1", temporary]);
        try
        {
            await VideoRuntime.RunAsync(false, args, token, line => { if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], out var us)) progress?.Invoke(Math.Clamp(us / 1e6 / (options.To - options.From), 0, 1)); });
            using var verified = await VideoRuntime.ProbeAsync(temporary, token);
            var fmt = verified.RootElement.GetProperty("format");
            if (Math.Abs(double.Parse(fmt.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - (options.To - options.From)) > .25
                || fmt.GetProperty("tags").EnumerateObject().All(p => !p.Name.Equals("LOPATA_OPERATION", StringComparison.OrdinalIgnoreCase) || p.Value.GetString() != operationId)) throw new IOException("VideoEditor.ExportInvalid");
            VideoProjectStore.VerifySources(project); token.ThrowIfCancellationRequested();
            var published = new List<(string Source, string Target)>();
            try
            {
                if (options.SeparateSrt) foreach (var s in subtitlePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var target = SubtitlePath(destination, s.Language, subtitlePaths.IndexOf(s), subtitlePaths.Count);
                    File.Copy(s.Path, target, false); published.Add((s.Path, target));
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            }
            catch { foreach (var file in published) if (File.Exists(file.Target) && File.ReadAllBytes(file.Source).SequenceEqual(File.ReadAllBytes(file.Target))) File.Delete(file.Target); throw; }
            progress?.Invoke(1); return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Validate(VideoProject p, string output, VideoExportOptions o)
    {
        if (!Path.IsPathFullyQualified(output) || File.Exists(output) || Path.GetExtension(output).ToLowerInvariant() is not (".mp4" or ".mkv")) throw new IOException("VideoEditor.NewOutputRequired");
        if (o.Width is < 16 or > 16384 || o.Height is < 16 or > 16384 || o.Width % 2 != 0 || o.Height % 2 != 0 || !double.IsFinite(o.Fps) || o.Fps is < 1 or > 120 || !double.IsFinite(o.From) || !double.IsFinite(o.To) || o.From < 0 || o.To <= o.From || o.To > p.Duration + .001 || o.BitrateMbps is < 1 or > 200) throw new InvalidDataException("VideoEditor.ExportSettingsInvalid");
        var groups = SubtitleGroups(p, o).ToArray();
        if (o.SeparateSrt) foreach (var lang in groups.Select((g, i) => (Language: g.Key.Language, Index: i)))
        {
            var path = SubtitlePath(output, lang.Language, lang.Index, groups.Length);
            if (File.Exists(path) || p.Assets.Any(a => Path.GetFullPath(a.Path).Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("VideoEditor.NewOutputRequired");
        }
    }
    internal static string Tempo(double speed)
    {
        var filters = new List<string>(); while (speed > 2) { filters.Add("atempo=2"); speed /= 2; } while (speed < .5) { filters.Add("atempo=0.5"); speed /= .5; } filters.Add("atempo=" + N(speed)); return string.Join(',', filters);
    }
    private static string SafeLanguage(string lang) => lang.Length is > 0 and < 20 && lang.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? lang : throw new InvalidDataException("VideoEditor.SubtitleInvalid");
    private static IEnumerable<IGrouping<(string SubtitleTrackId, string Language), NarrationSegment>> SubtitleGroups(VideoProject p, VideoExportOptions o) => VideoSubtitles.Cues(p).Where(n => !string.IsNullOrWhiteSpace(n.Text) && n.End > o.From && n.Start < o.To).GroupBy(n => (n.SubtitleTrackId, n.Language));
    private static string SubtitlePath(string output, string language, int index, int count) => Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + (count == 1 ? "" : "." + SafeLanguage(language) + "-" + index) + ".srt");
    private static string ContainerLanguage(string language) => language.ToLowerInvariant() switch { "ru" => "rus", "en" => "eng", "de" => "deu", "fr" => "fra", "es" => "spa", "it" => "ita", "pt" => "por", "ja" => "jpn", "zh" => "zho", _ => SafeLanguage(language) };
    internal static string TitleSvg(string text, int w, int h) => new XElement(XName.Get("svg", "http://www.w3.org/2000/svg"), new XAttribute("viewBox", $"0 0 {w} {h}"), new XElement(XName.Get("text", "http://www.w3.org/2000/svg"), new XAttribute("x", w / 2), new XAttribute("y", h * .85), new XAttribute("text-anchor", "middle"), new XAttribute("font-family", "Segoe UI"), new XAttribute("font-size", Math.Max(16, h / 20)), new XAttribute("fill", "white"), new XAttribute("stroke", "black"), new XAttribute("stroke-width", 1), text)).ToString();
}
