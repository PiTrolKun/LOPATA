namespace AIHub.Models;

public sealed record ImageGenerationManifest(int Schema, string BackendCommit,
    ImageGenerationArtifact[] Artifacts, ImageGenerationModel[] Models);
public sealed record ImageGenerationArtifact(string Id, string Name, string Repository,
    string Revision, string License, ManagedModelArtifactFile[] Files);
public sealed record ImageGenerationModel(string Id, string Name, string[] Components,
    int Steps, double Cfg, string Sampler, int ClipSkip, int DefaultWidth, int DefaultHeight, int MaximumSide);
public sealed record ImageGenerationRequest(string Id, string ModelId, string Prompt,
    int Width, int Height, long[] Seeds, string ModelsRoot, string SessionDirectory)
{
    // Optional metadata keeps earlier checkpoints readable. Old sessions are not exported retroactively.
    public string OutputFolder { get; init; } = "";
    public DateTimeOffset? SubmittedAt { get; init; }
    public int FirstGenerationNumber { get; init; } = 1;
    // Null for pre-metadata sessions: their existing PNG files remain untouched.
    public ImageGenerationMetadataOptions? Metadata { get; init; }
    // Zero preserves legacy delivery; the preset is fixed at submission time.
    public int OutputLongestSide { get; init; }
}
public sealed record ImageGenerationMetadataOptions(string Author);
public sealed record ImageGenerationResult(int Index, string FileName, long Seed, bool Reviewed = false)
{
    public string? ExportPath { get; init; }
    public bool Exported { get; init; }
    public string? ExportError { get; init; }
    public string? ProcessingError { get; init; }
}
public sealed record ImageGenerationTurn(ImageGenerationRequest Request, ImageGenerationResult[] Results);
public sealed record ImageGenerationSession(string Id, ImageGenerationTurn[] Turns);
