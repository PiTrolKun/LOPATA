using System.IO;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SpeechAssets;

internal sealed class SpeechAssetSession
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public VideoNarratorChoice Voice { get; set; } = new("qwen3-17b", "ru", 100, new("Ryan"));
    public Dictionary<string, VideoNarratorChoice> Profiles { get; set; } = [];
    public List<SpeechAssetRow> Rows { get; set; } = [];
    public MusicAudioFormat ExportFormat { get; set; } = MusicAudioFormat.Opus;
    public int ExportBitrate { get; set; } = 64;
    public SpeechAssetJob? PendingJob { get; set; }
    public long Number { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Persistent { get; set; }
    public bool Manual { get; set; }
    public List<SpeechAssetRun> Runs { get; set; } = [];
}

internal sealed class SpeechAssetRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string State { get; set; } = "Pending";
    public SpeechAssetJob Job { get; set; } = null!;
}

internal sealed class SpeechAssetRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Group { get; set; } = "";
    public string SourceText { get; set; } = "";
    public string Text { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool Included { get; set; } = true;
    public bool Confirmed { get; set; }
    public List<SpeechAssetTake> Takes { get; set; } = [];
    public string LastError { get; set; } = "";
    public int FailedAttempts { get; set; }
    public void Edit(string text)
    {
        if (Text == text) return;
        Text = text; Confirmed = false;
    }
    public void SelectTake(string id)
    {
        var selected = Takes.SingleOrDefault(t => t.Id == id) ?? throw new InvalidDataException("SpeechAssets.Invalid");
        if (selected.Selected && Takes.Count(t => t.Selected) == 1) return;
        foreach (var take in Takes) take.Selected = ReferenceEquals(take, selected);
        Confirmed = false;
    }
    public bool CanConfirm => !string.IsNullOrWhiteSpace(Text) && Takes.Count(t => t.Selected) == 1
        && Takes.Any(t => t.Selected && t.Text == Text);
    public void Confirm()
    {
        if (!CanConfirm) throw new InvalidDataException("SpeechAssets.ReviewRequired");
        Confirmed = true;
    }
}

internal sealed class SpeechAssetTake
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public double Seconds { get; set; }
    public VideoNarratorChoice Voice { get; set; } = new("qwen3-17b", "ru", 100, new());
    public OutputModelUse[] Models { get; set; } = [];
    public bool Selected { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
