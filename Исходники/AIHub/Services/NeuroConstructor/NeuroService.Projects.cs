using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.NeuroConstructor;

public static partial class NeuroService
{
    private static readonly object Gate = new();
    private sealed record Template(string Name, NeuroProject Settings);
    private static string LastProject(string mode) => Path.Combine(NeuroFiles.Root,mode == "auto" ? "latest-auto.json" : "latest.json");
    public static NeuroProject Open() => File.Exists(LastProject("pro")) ? OpenProject(NeuroFiles.Read<string>(LastProject("pro"))) : New();
    public static NeuroProject OpenAuto()
    {
        lock (Gate)
        {
            if (File.Exists(LastProject("auto"))) return OpenProject(NeuroFiles.Read<string>(LastProject("auto")));
            var p = new NeuroProject { Mode="auto", Source=DefaultSource(), LanguageDevice="auto", VisionDevice="auto", SpeechDevice="auto" };
            Save(p);
            return p;
        }
    }
    public static NeuroProject ActivateProject(string id)
    {
        lock (Gate)
        {
            var p = OpenProject(id);
            NeuroFiles.Write(LastProject(p.Mode),id);
            return p;
        }
    }
    public static NeuroProject SelectAutoProfile(NeuroProject current,string profile)
    {
        lock (Gate)
        {
            if (current.Mode != "auto" || Pending(current.Id) != null) throw new InvalidOperationException("Neuro.RunPending");
            if (profile is not ("light" or "medium" or "heavy")) throw new ArgumentException("Neuro.Settings");
            if (profile==NeuroProfiles.Id(current.Composition)) return current;
            var full = NeuroProfiles.Full(profile);
            Save(current);
            var saved = AutoProfilePointer(profile);
            var project = File.Exists(saved) ? OpenProject(NeuroFiles.Read<string>(saved)) :
                new NeuroProject { Mode="auto", Composition=full ? profile : "pair", Source=DefaultSource(profile),
                    Connection=full ? "late" : "inside", AutoEvaluateOnly=!full, Cycles=full ? 6 : 1,
                    LanguageDevice="auto",VisionDevice="auto",SpeechDevice="auto" };
            Save(project);
            return project;
        }
    }
    private static string AutoProfilePointer(string profile) =>
        Path.Combine(NeuroFiles.Root,"latest-auto-"+profile+".json");
    public static NeuroProject OpenProject(string id)
    {
        var project = NeuroFiles.Read<NeuroProject>(Path.Combine(NeuroFiles.Project(id),"project.json"));
        if (project.Id != id) throw new InvalidDataException("Neuro.ProjectId");
        return project;
    }
    public static IReadOnlyList<NeuroProject> Projects()
    {
        return NeuroFiles.Projects().Select(id => Path.Combine(NeuroFiles.Project(id), "project.json"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).Select(NeuroFiles.Read<NeuroProject>)
            .Where(p => p.Mode != "auto").ToArray();
    }
    private static string DefaultSource(string composition="light")
    {
        var installed = Path.Combine(AppDataPaths.ComponentModelsDirectory,"NeuroConstructor");
        var full=NeuroProfiles.Full(composition);
        if (full) installed = Path.Combine(installed,composition);
        if (Directory.Exists(installed)) return installed;
        if (AppDataPaths.ProjectRoot is { } root)
        {
            var local = full ? Path.Combine(root,"Тесты","Нейроконструктор",composition=="medium" ? "Средний_набор" : "Тяжёлый_набор")
                               : Path.Combine(root,"Тесты","Исследования","2026-11-23_Гетерогенная_нейромодель");
            if (Directory.Exists(Path.Combine(local,"models"))) return local;
        }
        return "";
    }
    public static NeuroProject New(string composition = "pair")
    {
        var project = new NeuroProject { Composition = composition, Connection = composition == "pair" ? "inside" : "late", Source = DefaultSource(composition) };
        Save(project);
        return project;
    }
    internal static NeuroProject NewForLibrary(string title, string composition, int cycles, int attempts)
    {
        var project = new NeuroProject { ExternalAgentProject = true, TaskName = title, Composition = composition, Source = DefaultSource(composition),
            Connection = composition == "pair" ? "inside" : "late", Cycles = cycles, Attempts = attempts };
        Save(project, makeCurrent: false);
        return project;
    }
    public static void Save(NeuroProject p) => Save(p, makeCurrent: true);
    private static void Save(NeuroProject p, bool makeCurrent)
    {
        if (p.Mode is not ("auto" or "pro") || p.Composition is not ("pair" or "trio") && !NeuroProfiles.Full(p.Composition) || p.Cycles < 1 || p.Attempts < 1 || p.MemoryMiB < 0)
            throw new ArgumentException("Neuro.Settings");
        lock (Gate)
        {
            NeuroFiles.Write(Path.Combine(NeuroFiles.Project(p.Id),"project.json"),p);
            if (!makeCurrent) return;
            NeuroFiles.Write(LastProject(p.Mode),p.Id);
            if (p.Mode=="auto") NeuroFiles.Write(AutoProfilePointer(NeuroProfiles.Id(p.Composition)),p.Id);
        }
    }
    public static NeuroProject Import(string package)
    {
        var original = Path.GetFullPath(package);
        var p = new NeuroProject { ImportedFrom = original, Source = DefaultSource() };
        var directory = Directory.CreateDirectory(NeuroFiles.Project(p.Id)).FullName;
        p.ParentPackage = Path.Combine(directory,"parent.lopatanet");
        try
        {
            File.Copy(original,p.ParentPackage,false);
            using var zip = ZipFile.OpenRead(p.ParentPackage);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Neuro.Package");
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.GetProperty("format").GetString() != "lopata-neuro" ||
                root.GetProperty("version").GetInt32() != (NeuroProfiles.Full(root.GetProperty("kind").GetString()) ? 2 : 1))
                throw new InvalidDataException("Neuro.Package");
            p.Composition = root.GetProperty("kind").GetString() ?? "";
            var settings = root.GetProperty("settings");
            string? Setting(string key) => settings.TryGetProperty(key,out var value) ? value.GetString() : null;
            p.Channel = Setting("channel") switch {
                "byte" => Setting("send") == "changed" ? "cached" : "byte",
                "feedback" => "feedback",
                "continuous" when Setting("send") == "changed" => "cached-continuous",
                _ => "continuous" };
            p.AudioPolicy = Setting("routing") switch { "always" => "always", "learned" => "learned", _ => "needed" };
            p.Device = Setting("device") ?? "auto";
            p.LanguageDevice = Setting("languageDevice") ?? "";
            p.VisionDevice = Setting("visionDevice") ?? "";
            p.SpeechDevice = Setting("speechDevice") ?? "";
            if (p.VisionDevice.Length > 0) p.Device = p.VisionDevice;
            if (root.GetProperty("graph").TryGetProperty(p.Composition == "pair" ? "pair_connection" : "topology",out var topology))
                p.Connection = topology.GetString() ?? "inside";
            Save(p);
            return p;
        }
        catch { File.Delete(p.ParentPackage); throw; }
    }
    private static string CopyInput(string directory,string file,string extension)
    {
        Directory.CreateDirectory(directory);
        using var source = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(SHA256.HashData(source));
        var target = Path.Combine(directory,hash + extension);
        if (File.Exists(target)) return target;
        var tmp = target + "." + Guid.NewGuid().ToString("N");
        try
        {
            source.Position = 0;
            using (var output = File.Create(tmp)) { source.CopyTo(output); output.Flush(true); }
            File.Move(tmp,target);
        }
        finally { File.Delete(tmp); }
        return target;
    }
    public static void AddExample(NeuroProject p,string image,string text,int slot,string[]? audio=null,string split="train")
    {
        if (slot is < 0 or > 3 || string.IsNullOrWhiteSpace(text) || split is not ("train" or "dev" or "test")
            || audio?.Length is not (null or 0 or 2)) throw new ArgumentException("Neuro.Example");
        var dir = Path.Combine(NeuroFiles.Project(p.Id),"inputs");
        lock (Gate)
        {
            var stored = CopyInput(dir,image,".png");
            var sounds = (audio ?? []).Select(f => CopyInput(dir,f,".wav")).ToArray();
            if (p.Examples.Any(e => e.Split != split && (e.Image == stored || e.Audio.Intersect(sounds).Any())))
                throw new InvalidDataException("Neuro.SplitOverlap");
            var example = new NeuroExample(stored,text.Trim(),slot,sounds,split);
            var index = p.Examples.FindIndex(e => e.Image == stored && e.Text == example.Text && e.Audio.SequenceEqual(sounds));
            if (index < 0) p.Examples.Add(example); else p.Examples[index] = example;
            Save(p);
        }
    }
    private static string Templates => Path.Combine(NeuroFiles.Root,"presets");
    private static string PresetPath(string name) => Path.Combine(Templates,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim()))) + ".json");
    public static string[] Presets() => Directory.Exists(Templates) ? Directory.EnumerateFiles(Templates,"*.json")
        .Select(path => NeuroFiles.Read<Template>(path).Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray() : [];
    private static List<NeuroExample> CopyExamples(IEnumerable<NeuroExample> examples,string directory) =>
        examples.Select(e => e with { Image=CopyInput(directory,e.Image,".png"),
            Audio=e.Audio.Select(file=>CopyInput(directory,file,".wav")).ToArray() }).ToList();
    public static void SavePreset(NeuroProject p,string name,bool includeExamples=false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate)
        {
            var copies=includeExamples ? CopyExamples(p.Examples,Path.Combine(Templates,"inputs")) : [];
            NeuroFiles.Write(PresetPath(name),new Template(name.Trim(),p with { Examples=copies, Source="", ParentPackage="", ImportedFrom="" }));
        }
    }
    public static NeuroProject FromPreset(string name)
    {
        var saved = NeuroFiles.Read<Template>(PresetPath(name)).Settings;
        var id = Guid.NewGuid().ToString("N");
        var p = saved with { Id=id, Source=DefaultSource(saved.Composition), Examples=CopyExamples(saved.Examples,Path.Combine(NeuroFiles.Project(id),"inputs")) };
        Save(p);
        return p;
    }
}
