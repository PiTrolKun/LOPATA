using System.IO;

namespace AIHub.Services;

/// <summary>A job pins one immutable Studio contract; changing the recommendation never rewrites it.</summary>
public sealed record MusicStudioProfile(string EngineSetId, string Revision, string SourceRevision, string EngineRevision,
    string Pack, bool SeparateNormalization)
{
    public string DirectoryPath => MusicStudioRuntime.ProfileDirectory(this);
    public bool SupportsAb2 => SeparateNormalization;
    public string OutputPolicy => SeparateNormalization
        ? "headless;keep_loaded=false;max_batch=1;backend=auto;output=wav32;companion_scale=1;lyric_timing=false;normalize=studio-processing-peak_clip;intermediate=flac24;stage=pcm16"
        : "headless;keep_loaded=false;max_batch=1;output=wav16;backend=auto";
}
