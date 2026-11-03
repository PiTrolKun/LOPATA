using AIHub.Services.VideoEditor;

namespace AIHub.Models;

public sealed class ChatSpeechSettings
{
    public bool AutoRead { get; set; }
    public string Selected { get; set; } = "vosk010";
    public Dictionary<string, VideoSpeechProfile> Profiles { get; set; } = [];

    internal VideoNarratorChoice Choice()
    {
        var model = VideoNarratorCatalog.Get(Selected);
        var profile = Profiles?.GetValueOrDefault(Selected);
        var choice = profile is null ? new VideoNarratorChoice(model.Id, model.Languages.Contains("ru") ? "ru" : model.Languages[0],
            100, new(Speaker: model.DefaultSpeaker, Device: "cpu"))
            : new(model.Id, profile.Language, profile.Rate, profile.Options);
        VideoNarratorCatalog.Validate(choice.Id, choice.Language, choice.Rate, choice.Options);
        return choice;
    }
    internal void Apply(VideoNarratorChoice choice)
    {
        VideoNarratorCatalog.Validate(choice.Id, choice.Language, choice.Rate, choice.Options);
        Selected = choice.Id; Profiles ??= [];
        Profiles[choice.Id] = new(choice.Id, choice.Language, choice.Rate, choice.Options);
    }
}
