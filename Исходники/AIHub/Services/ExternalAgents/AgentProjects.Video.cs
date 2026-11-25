using System.IO;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private sealed class VideoDraft : Draft
    {
        public required string Idea { get; init; }
        public string Form { get; init; } = "story";
        public string EnglishIdea { get; init; } = "";
        public string Development { get; init; } = "";
        public string EnglishDevelopment { get; init; } = "";
        public ReferenceDraft[] References { get; init; } = [];
        public SceneDraft[] Scenes { get; init; } = [];
        public string? Model { get; init; }
        public DenoProjectOptions? Deno { get; init; }

        internal override object Create(Scenario scenario)
        {
            RequireText(Idea, "idea");
            if (Form is not ("story" or "imagery")) throw new InvalidDataException("form must be story or imagery.");
            var project = new VideoPreparationProject { Title = Title, IdeaText = Idea, Form = Form, CurrentStep = "finish",
                ExternalAgentProject = true, EnglishText = EnglishIdea };
            VideoConceptHistory.Capture(project);
            VideoTranslationHistory.Capture(project);
            if (Development.Length + EnglishDevelopment.Length > 0)
            {
                var development = VideoDevelopment.Ensure(project);
                development.IdeaText = Development;
                development.EnglishText = EnglishDevelopment;
                VideoConceptHistory.Capture(development);
                VideoTranslationHistory.Capture(development);
            }
            if (References.Length > 0)
            {
                VideoReferences.Ensure(project).Choice = "create";
                VideoReferencePlans.Ensure(project).Mode = "manual";
                foreach (var reference in References)
                {
                    if (reference is null) throw new InvalidDataException("references must contain objects.");
                    RequireText(reference.Name, "references.name");
                    RequireText(reference.Prompt, "references.prompt");
                    var item = VideoReferencePlans.AddDrawingPrompt(project);
                    item.Name = reference.Name;
                    item.Brief = reference.Description;
                    item.Native = reference.Prompt;
                    item.English = reference.EnglishPrompt;
                }
                VideoReferencePlans.Capture(project);
            }
            if (Scenes.Length > 0)
            {
                var shots = VideoShots.Ensure(project);
                foreach (var scene in Scenes)
                {
                    if (scene is null) throw new InvalidDataException("scenes must contain objects.");
                    RequireText(scene.Name, "scenes.name");
                    var plan = new VideoScenePlan { Name = scene.Name, Intent = scene.Description };
                    foreach (var shot in scene.Shots)
                    {
                        if (shot is null) throw new InvalidDataException("scenes.shots must contain objects.");
                        RequireText(shot.Name, "scenes.shots.name");
                        plan.Shots.Add(new() { Name = shot.Name, Start = shot.Start, End = shot.End, Camera = shot.Camera,
                            Composition = shot.Composition, Light = shot.Light, Duration = shot.Seconds });
                    }
                    shots.Scenes.Add(plan);
                }
                shots.Source = VideoShots.Hash(VideoShots.Source(project));
                shots.SelectedSceneId = shots.Scenes[0].Id;
                shots.SelectedShotId = shots.Scenes[0].Shots.FirstOrDefault()?.Id ?? "";
                VideoShots.Capture(project);
            }
            if (Model is not null || Deno is not null)
            {
                var model = Model ?? VideoH3Catalog.ModelId;
                if (!VideoGeneratorPlan.IsAvailable(model)) throw new InvalidDataException("Unknown video generation model.");
                var settings = VideoH3WorkspaceState.Ensure(project, model).Settings;
                if (Deno is { } deno)
                {
                    var output = deno.Resolve();
                    settings.Aspect = output.Width < output.Height ? "9:16" : "16:9";
                    VideoH3WorkspaceState.Resolution(settings, model == VideoH3LiteCatalog.ModelId ? .4 : .98);
                    settings.OutputHeight = settings.DenoOutput = deno.Resolution;
                    settings.DenoAccelerate = output.Accelerate;
                }
                VideoGeneratorPlan.SaveChoice(project, new(model, settings.Width, settings.Height, 24, settings.Seed, ""));
            }
            return SaveFile(scenario, VideoPreparationArchive.Extension, file => VideoPreparationArchive.Save(project, file));
        }
    }
    private sealed record ReferenceDraft(string Name, string Prompt, string Description = "", string EnglishPrompt = "");
    private sealed class SceneDraft
    {
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public ShotDraft[] Shots { get; init; } = [];
    }
    private sealed record ShotDraft(string Name, string Start = "", string End = "", double Seconds = 0,
        string Camera = "", string Composition = "", string Light = "");
}
