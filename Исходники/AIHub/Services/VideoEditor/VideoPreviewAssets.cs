using System.IO;

namespace AIHub.Services.VideoEditor;

internal static class VideoPreviewAssets
{
    public static string DirectoryFor(VideoProject p) => p.PreviewDirectory ?? Path.Combine(VideoProjectStore.Work(p.Id), "Preview");
    internal static string ProxyPath(VideoProject p, VideoAsset a) => Path.Combine(DirectoryFor(p), a.Id + (a.Kind == "audio" ? ".wav" : "-gop1.mp4"));
    internal static string AudioPath(VideoProject p, VideoAsset a) => Path.Combine(DirectoryFor(p), a.Id + ".audio.m4a");
    public static async Task PrepareAsync(VideoProject p, VideoAsset a, CancellationToken token)
    {
        var directory = DirectoryFor(p); Directory.CreateDirectory(directory);
        if (a.Kind is "image" or "svg") { a.Proxy = a.Path; return; }
        if (a.Kind == "video" && a.AudioStream >= 0) await PrepareAudioAsync(p, a, token);
        if (a.Proxy is {} prepared && File.Exists(prepared)) return;
        var destination = ProxyPath(p, a);
        if (File.Exists(destination)) { a.Proxy = destination; return; }
        var temporary = Path.Combine(directory, a.Id + ".partial" + Path.GetExtension(destination));
        if (File.Exists(temporary)) File.Delete(temporary);
        var args = new List<string> { "-v", "warning", "-nostdin", "-n", "-i", a.Path };
        if (a.Kind == "video") args.AddRange(["-map", "0:" + a.VideoStream, "-vf", "scale=960:540:force_original_aspect_ratio=decrease,setsar=1", "-c:v", "libopenh264", "-b:v", "2500k", "-r", "30", "-g", "30"]);
        else args.Add("-vn");
        if (a.AudioStream >= 0) args.AddRange(["-map", "0:" + a.AudioStream, "-ar", "48000", "-ac", "2", "-c:a", a.Kind == "audio" ? "pcm_s16le" : "aac"]);
        else args.Add("-an");
        if (a.Kind == "video") args.AddRange(["-movflags", "+faststart"]);
        args.Add(temporary);
        try { await VideoRuntime.RunAsync(false, args, token); token.ThrowIfCancellationRequested(); File.Move(temporary, destination); a.Proxy = destination; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static async Task PrepareAudioAsync(VideoProject p, VideoAsset a, CancellationToken token)
    {
        var path = AudioPath(p, a); if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".partial.m4a";
        if (File.Exists(temporary)) File.Delete(temporary);
        try {
            await VideoRuntime.RunAsync(false, ["-v", "warning", "-nostdin", "-n", "-i", a.Path, "-map", "0:" + a.AudioStream, "-vn", "-ar", "48000", "-ac", "2", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4", temporary], token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static Task<VideoWaveform> WaveformAsync(VideoAsset a, string work, CancellationToken token) => VideoWaveform.LoadAsync(a, work, token);
}
