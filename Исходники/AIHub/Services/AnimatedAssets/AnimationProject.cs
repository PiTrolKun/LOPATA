namespace AIHub.Services.AnimatedAssets;

internal sealed record AnimationModelUse(string Role, string Id, string? Revision = null);

internal sealed class AnimationProject
{
    internal const int ReviewPage = 6;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExternalAgentProject { get; set; }
    public string Purpose { get; set; } = "chat";
    public bool IsPack { get; set; }
    public bool IsTeasePack { get; set; }
    public string PackDescription { get; set; } = "";
    public List<string>? AutoPackEmojis { get; set; }
    public double AutoPackDuration { get; set; } = 2;
    public string? AutoPackReferenceSha256 { get; set; }
    public bool Quick { get; set; }
    public byte[]? Reference { get; set; }
    public byte[]? ReferenceGuide { get; set; }
    public bool Transparent { get; set; } = true;
    public bool ReferenceApproved { get; set; }
    public bool ReferenceIsEmoji { get; set; } = true;
    public string ReferenceEmoji { get; set; } = "";
    public string ReferenceEmojiName { get; set; } = "";
    public string ReferenceDescription { get; set; } = "";
    public List<AnimationCard> Cards { get; set; } = [];
    public int Page { get; set; }
    public int SelectedCard { get; set; }
    public string ExportFormat { get; set; } = "";
    public string ExportProfile { get; set; } = "";
    public string? PngPoseId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public List<AnimationModelUse> Models { get; set; } = [];

    // Image payloads are immutable after assignment. Copy the editable state for background work.
    public AnimationProject Snapshot()
    {
        var copy = (AnimationProject)MemberwiseClone();
        copy.AutoPackEmojis = AutoPackEmojis is null ? null : [.. AutoPackEmojis];
        copy.Models = [.. Models];
        copy.Cards = Cards.ConvertAll(card => card.Snapshot());
        return copy;
    }
}

internal sealed class AnimationCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "reaction";
    public string Sequence { get; set; } = "=) / ;) / =)-";
    public string TargetEmoji { get; set; } = "😉";
    public string ReactionDetails { get; set; } = "";
    public string Story { get; set; } = "";
    public string Keep { get; set; } = "";
    public string Ambient { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Motion { get; set; } = "frames";
    public double Duration { get; set; } = 2;
    public double Strength { get; set; } = 0.5;
    public byte[]? Still { get; set; }
    public byte[]? ClosedEye { get; set; }
    public AnimationEyeRegion? EyeRegion { get; set; }
    public List<AnimationPose> Poses { get; set; } = [];
    public List<AnimationModelUse> Models { get; set; } = [];

    public AnimationCard Snapshot()
    {
        var copy = (AnimationCard)MemberwiseClone();
        copy.Models = [.. Models];
        copy.Poses = Poses.ConvertAll(pose => pose.Snapshot());
        return copy;
    }
}

internal sealed record AnimationEyeRegion(double X, double Y, double Width, double Height);

internal sealed class AnimationPose
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Time { get; set; }
    public string Text { get; set; } = "";
    public string Prompt { get; set; } = "";
    public byte[]? Image { get; set; }
    public List<AnimationEffect> Effects { get; set; } = [];
    public List<AnimationModelUse> Models { get; set; } = [];

    public AnimationPose Snapshot()
    {
        var copy = (AnimationPose)MemberwiseClone();
        copy.Models = [.. Models];
        copy.Effects = Effects.ConvertAll(effect => effect.Snapshot());
        return copy;
    }
}

internal sealed class AnimationEffect
{
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Duration { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }

    public AnimationEffect Snapshot() => (AnimationEffect)MemberwiseClone();
}
