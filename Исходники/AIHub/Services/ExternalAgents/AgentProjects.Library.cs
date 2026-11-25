using System.IO;
using AIHub.Models;
using AIHub.Services.CharacterKnowledge;
using AIHub.Services.GameSounds;
using AIHub.Services.NeuroConstructor;
using AIHub.Services.OneC;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VisualScenario;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentProjects
{
    private sealed class LiteraryDraft : Draft
    {
        public required string Premise { get; init; }
        public required string Genre { get; init; }
        public string Author { get; init; } = "";
        public string Language { get; init; } = "ru";
        internal override object Create(Scenario scenario)
        {
            RequireText(Premise, "premise");
            RequireText(Genre, "genre");
            if (!LiteraryProjectStore.IsValidProjectName(Title)) throw new InvalidDataException("title is not a valid literary project folder name.");
            var project = new LiteraryProject { ExternalAgentProject = true, WorkTitle = Title, ProjectName = Title, Premise = Premise,
                CustomGenres = Genre, Author = Author, LanguageCode = Language, CreationBrief = Premise };
            var created = LiteraryProjectStore.Default().Create(FileProjectDirectory(), project, []);
            return InLibrary(scenario, created.Id);
        }
    }

    private sealed class MusicDraft : Draft
    {
        public required string Lyrics { get; init; }
        public string Artist { get; init; } = "";
        public string Comment { get; init; } = "";
        public int? DurationSeconds { get; init; }
        internal override object Create(Scenario scenario)
        {
            var snapshot = new MusicProjectSnapshot { Title = Title, Artist = Artist, Lyrics = Lyrics,
                Comment = Comment, DurationSeconds = DurationSeconds };
            snapshot.Validate();
            var store = MusicProjects.Default;
            var project = store.Fix(store.CreateDraft() with { Name = Title, ExternalAgentProject = true }, snapshot);
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class SongDraft : Draft
    {
        public required string Prompt { get; init; }
        public string Lyrics { get; init; } = "";
        public string Timings { get; init; } = "";
        public string Variant { get; init; } = "glitchframe";
        public int? PiH3Steps { get; init; }
        public DenoProjectOptions? Deno { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Prompt, "prompt");
            var variant = SongVariants.SingleOrDefault(v => v.Id == Variant)
                ?? throw new InvalidDataException("Unknown song clip variant. Read project_instructions for current variants.");
            if (PiH3Steps is { } steps && (Variant != PiH3Components.VariantId || !PiH3Components.SupportsSteps(steps)))
                throw new InvalidDataException("piH3Steps must be 2 or 4 and requires the pi-h3-dwd variant.");
            if (Deno is not null && Variant != SongClipDeno.Mode)
                throw new InvalidDataException("deno settings require the deno variant.");
            var settings = new SongClipSettings { Prompt = Prompt, Motion = new() { Mode = variant.Mode } };
            if (Variant == PiH3Components.VariantId) settings.Geek = new() { PiH3Steps = PiH3Steps ?? PiH3Components.DefaultSteps };
            if (Variant == SongClipDeno.Mode) settings.Deno = (Deno ?? new()).Resolve();
            var project = new SongClipProject { ExternalAgentProject = true, Name = Title, Lyrics = Lyrics, Timings = Timings,
                Model = variant.Model, Settings = settings };
            new SongClipStore(DestinationDirectory).Save(project);
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class ShortDraft : Draft
    {
        public required string Prompt { get; init; }
        public int Seconds { get; init; } = 15;
        public bool FromSong { get; init; }
        public string? Variant { get; init; }
        public bool? Accelerate { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Prompt, "prompt");
            if (Seconds is < 1 or > ShortsJob.MaximumSeconds) throw new InvalidDataException("seconds must be 1–179.");
            var profile = ShortsProfile(FromSong, Variant, Accelerate);
            var store = DestinationDirectory is { } directory ? new ShortsProjects(directory) : ShortsProjects.Default;
            var content = new ShortsDraft(FromSong: FromSong, Seconds: Seconds, Prompt: Prompt)
            {
                UseDeno = profile.Deno, AccelerateDeno = profile.Accelerate
            };
            var draft = store.New(content) with { ExternalAgentProject = true };
            var project = store.SaveWorkspace(store.Rename(draft, Title), content, manual: true);
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class VisualDraft : Draft
    {
        public required string Prompt { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Prompt, "prompt");
            var project = new VisualProject { Name = Title, Manual = true, Persistent = true, Draft = Prompt,
                PromptDraft = Prompt, ExternalAgentProject = true };
            project.ManualLines.Add(Prompt);
            VisualProjects.Default.Save(project, current: false);
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class SoundDraft : Draft
    {
        public required string Description { get; init; }
        public bool Loop { get; init; }
        internal override object Create(Scenario scenario)
        {
            RequireText(Description, "description");
            var store = GameSoundProjects.Default;
            var project = store.Create();
            project.ExternalAgentProject = true;
            project.Name = Title;
            project.Draft = Description;
            project.Loop = Loop;
            store.Save(project);
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class SpeechDraft : Draft
    {
        public required string[] Lines { get; init; }
        internal override object Create(Scenario scenario)
        {
            if (Lines.Length == 0) throw new InvalidDataException("lines must contain at least one utterance.");
            var rows = new List<SpeechAssetRow>();
            foreach (var line in Lines)
            {
                RequireText(line, "lines");
                rows.Add(new() { SourceText = line, Text = line, FileName = $"line-{rows.Count + 1:D3}" });
            }
            var store = new SpeechAssetStore();
            var session = store.CreateDraft();
            session.ExternalAgentProject = true;
            session.Name = Title;
            session.Rows = rows;
            store.FixProject(session);
            return InLibrary(scenario, session.Id);
        }
    }

    private sealed class OneCDraft : Draft
    {
        public required string Task { get; init; }
        public string Code { get; init; } = "";
        internal override object Create(Scenario scenario)
        {
            RequireText(Task, "task");
            var store = new OneCProjectStore();
            var project = store.Create(Title, p =>
            {
                p.ExternalAgentProject = true;
                p.Task.Prompt = Task;
                if (Code.Length > 0)
                {
                    var file = OneCProjectStore.AttachMaterial(p, "module.bsl", Code);
                    p.OpenFileIds.Add(file.Id);
                    p.ActiveFileId = file.Id;
                }
            });
            return InLibrary(scenario, project.Id);
        }
    }

    private sealed class KnowledgeDraft : Draft
    {
        public required KnowledgeItem[] Entries { get; init; }
        internal override object Create(Scenario scenario)
        {
            if (Entries.Any(item => item is null)) throw new InvalidDataException("entries must contain objects.");
            var entries = Entries.Select(item => new KnowledgeEntry(Guid.NewGuid(), item.Type, item.Input, item.Output)).ToArray();
            var project = new KnowledgeStore().Create(Title, new(entries, []), externalAgentProject: true);
            return InLibrary(scenario, project.Base.BaseId.ToString("N"));
        }
    }
    private sealed record KnowledgeItem(string Input, string Output, string Type = "knowledge_or_memory");

    private sealed class NeuroDraft : Draft
    {
        public string Composition { get; init; } = "pair";
        public int Cycles { get; init; } = 1;
        public int Attempts { get; init; } = 1;
        internal override object Create(Scenario scenario)
        {
            if (Composition is not ("pair" or "trio")) throw new InvalidDataException("composition must be pair or trio.");
            var project = NeuroService.NewForLibrary(Title, Composition, Cycles, Attempts);
            return InLibrary(scenario, project.Id);
        }
    }
}
