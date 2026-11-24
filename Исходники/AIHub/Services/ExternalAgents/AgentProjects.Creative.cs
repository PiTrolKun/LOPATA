using System.IO;
using AIHub.Services.AnimatedAssets;
using AIHub.Services.ImageLayers;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private sealed class AnimationDraft : Draft
    {
        public required string Description { get; init; }
        public byte[]? ReferenceBase64 { get; init; }
        public required ReactionDraft[] Reactions { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Description, "description");
            if (Reactions.Length == 0) throw new InvalidDataException("reactions must contain at least one item.");
            if (ReferenceBase64 is not null) LayerSource.Load("reference.png", ReferenceBase64);
            var project = new AnimationProject { ExternalAgentProject = true, Page = AnimationProject.ReviewPage,
                ReferenceDescription = Description, Reference = ReferenceBase64, ReferenceApproved = ReferenceBase64 is not null,
                IsPack = Reactions.Length > 1, PackDescription = Title };
            foreach (var reaction in Reactions)
            {
                if (reaction is null) throw new InvalidDataException("reactions must contain objects.");
                RequireText(reaction.Name, "reactions.name");
                RequireText(reaction.Description, "reactions.description");
                if (reaction.Seconds is < AnimationRenderer.MinimumDuration or > AnimationRenderer.MaximumDuration ||
                    reaction.Motion is not ("frames" or "jump" or "sway"))
                    throw new InvalidDataException("Use a duration of 2–30 seconds and the motion frames, jump or sway.");
                var card = new AnimationCard { Name = reaction.Name, TargetEmoji = reaction.Emoji, ReactionDetails = reaction.Description,
                    Story = reaction.Description, Duration = reaction.Seconds, Motion = reaction.Motion };
                double previous = 0;
                foreach (var pose in reaction.Poses ?? [])
                {
                    if (pose is null || pose.Time <= previous || pose.Time >= card.Duration || card.Motion != "frames")
                        throw new InvalidDataException("Poses need increasing times inside a frames animation.");
                    RequireText(pose.Description, "poses.description");
                    if (pose.ImageBase64 is not null) LayerSource.Load("pose.png", pose.ImageBase64);
                    card.Poses.Add(new() { Time = pose.Time, Text = pose.Description, Prompt = pose.Description, Image = pose.ImageBase64 });
                    previous = pose.Time;
                }
                project.Cards.Add(card);
            }
            return SaveFile(scenario, AnimationProjectStore.Extension, path => AnimationProjectStore.SaveAsync(path, project).GetAwaiter().GetResult());
        }
    }
    private sealed record ReactionDraft(string Name, string Description, string Emoji = "🙂", double Seconds = 2,
        string Motion = "frames", PoseDraft[]? Poses = null);
    private sealed record PoseDraft(double Time, string Description, byte[]? ImageBase64 = null);
}
