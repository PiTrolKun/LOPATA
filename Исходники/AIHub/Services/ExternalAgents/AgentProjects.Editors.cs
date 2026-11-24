using System.IO;
using AIHub.Services.ImageLayers;
using AIHub.Services.OfficeEditor;
using AIHub.Services.VectorEditor;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private sealed class OfficeDraft : Draft
    {
        public required string Text { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Text, "text");
            var manifest = OfficeSheets.Initialize(new() { ExternalAgentProject = true, Provenance = new OutputProvenance {
                Scenario = "office-editor/project", ModelHistory = "not-applicable" } }, OfficeSheetKind.Text, Title, existing: false);
            var document = OfficeTextFormats.PlainDocument(Text);
            return SaveFile(scenario, ".LOPATAdoxAI", path => OfficeTextPackage.Store(path, document, manifest, previous: null));
        }
    }

    private sealed class VectorDraft : Draft
    {
        public required string Svg { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Svg, "svg");
            var project = VectorProject.New(Title, Title) with { ExternalAgentProject = true };
            project.Sheets[0] = project.Sheets[0] with { Svg = Svg };
            return SaveFile(scenario, ".lopataSVG", path => VectorProjectArchive.Save(project, path));
        }
    }

    private sealed class LayersDraft : Draft
    {
        public required byte[] ImageBase64 { get; init; }
        internal override object Create(Scenario scenario)
        {
            var document = new LayerDocument(LayerSource.Load(Title, ImageBase64)) { ExternalAgentProject = true };
            return SaveFile(scenario, LayerProjectArchive.Extension, path => LayerProjectArchive.Save(path, document));
        }
    }

    private sealed class EditorDraft : Draft
    {
        public int Width { get; init; } = 1920;
        public int Height { get; init; } = 1080;
        public double Fps { get; init; } = 30;
        public required NarrationDraft[] Narration { get; init; }
        internal override object Create(Scenario scenario)
        {
            var project = new VideoProject { Name = Title, Width = Width, Height = Height, Fps = Fps, ExternalAgentProject = true };
            foreach (var line in Narration)
            {
                if (line is null) throw new InvalidDataException("narration must contain segments.");
                RequireText(line.Text, "narration.text");
                project.Narration.Add(new() { Start = line.Start, End = line.End, Language = line.Language, Text = line.Text });
            }
            project.Validate();
            return SaveFile(scenario, ".lopataVideo", path => VideoProjectArchive.Save(project, path));
        }
    }
    private sealed record NarrationDraft(string Text, double Start, double End, string Language = "ru");

    private sealed class UpscaleDraft : Draft
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public string Algorithm { get; init; } = "lanczos";
        internal override object Create(Scenario scenario)
        {
            var step = UpscaleCatalog.New(UpscaleEngine.Scale, Width, Height);
            step.Algorithm = Algorithm;
            var project = new UpscaleProject { Name = Title, Mode = UpscaleMode.Manual, Steps = [step], ExternalAgentProject = true };
            project.Workspace = UpscaleStore.Work(project.Id);
            UpscaleCatalog.Validate(project);
            UpscaleStore.SaveProject(project);
            return new { scenario = scenario.Id, title = Title, origin = ExternalOrigin, path = UpscaleStore.ProjectPath(project.Id), openIn = scenario.OpenIn };
        }
    }
}
