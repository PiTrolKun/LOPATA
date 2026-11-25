using System.IO;
using AIHub.Services.ImageLayers;
using AIHub.Services.OfficeEditor;
using AIHub.Services.VectorEditor;
using AIHub.Services.VideoConverter;
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

    private sealed class ConverterDraft : Draft
    {
        public string[] Inputs { get; init; } = [];
        public ConversionOptions Options { get; init; } = new();

        internal override object Create(Scenario scenario)
        {
            Options.Validate();
            var validated = Inputs.Length == 0 ? [] : new Models.VideoConverterRequest(ConversionStore.NewId(), Inputs).Validate().Paths.ToArray();
            return SaveFile(scenario, ".lopata-converter.json",
                path => BackgroundOperationStore.SaveAtomic(path, new ConversionDraft(Options, validated, null)));
        }
    }

    private sealed class UpscaleDraft : Draft
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public string? Algorithm { get; init; }
        public string Preset { get; init; } = "manual";
        internal override object Create(Scenario scenario)
        {
            var mode = Preset switch
            {
                "manual" => UpscaleMode.Manual,
                "deno" => UpscaleMode.Deno,
                _ => throw new InvalidDataException("preset must be manual or deno.")
            };
            if (Algorithm is not null && mode != UpscaleMode.Manual)
                throw new InvalidDataException("algorithm is available only for the manual preset.");
            var steps = UpscaleCatalog.Preset(mode, Width < Height);
            steps[0].Width = Width;
            steps[0].Height = Height;
            if (Algorithm is not null) steps[0].Algorithm = Algorithm;
            var project = new UpscaleProject { Name = Title, Mode = mode, Steps = steps, ExternalAgentProject = true };
            project.Workspace = UpscaleStore.Work(project.Id);
            UpscaleCatalog.Validate(project);
            UpscaleStore.SaveProject(project);
            return new { scenario = scenario.Id, title = Title, origin = ExternalOrigin, path = UpscaleStore.ProjectPath(project.Id), openIn = scenario.OpenIn };
        }
    }
}
