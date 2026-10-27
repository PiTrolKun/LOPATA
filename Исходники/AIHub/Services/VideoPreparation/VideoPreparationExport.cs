using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using Lopata.Updates;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoPreparationExportResult(string Directory, int Files);
public static class VideoPreparationExport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string SafeName(string value)
    {
        var text = new string(value.Normalize().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return text.Length == 0 ? "untitled" : text[..Math.Min(70, text.Length)];
    }
    public static async Task<VideoPreparationExportResult> ExportAsync(VideoPreparationProject source, string destination, CancellationToken token, IProgress<double>? progress = null, string? operationId = null)
    {
        token.ThrowIfCancellationRequested();
        var p = source.Clone(); p.Validate();
        if (!VideoPreparationReadiness.Ready(p)) throw new InvalidDataException("VideoPrep.PackageNotReady");
        destination = Path.GetFullPath(destination); var parent = Path.GetDirectoryName(destination) ?? throw new IOException("VideoPrep.ExportInvalidFolder");
        SafeUpdatePath.RejectLinks(destination);
        if (Directory.Exists(destination) && operationId is not null)
            return await VerifyCompletedAsync(destination, p.Id, operationId, token);
        if (!Directory.Exists(parent) || Directory.Exists(destination) || File.Exists(destination)) throw new IOException("VideoPrep.ExportExisting");
        if (p.Attachments.Select(a => a.Role).Distinct(StringComparer.Ordinal).Count() != p.Attachments.Count) throw new InvalidDataException("VideoPrep.InvalidArchive");
        var temporary = Path.Combine(parent, ".lopata-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary); var madeFiles = new List<string>(); var madeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { temporary };
        string Target(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(temporary, relative));
            if (!full.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("VideoPrep.ExportInvalidFolder");
            SafeUpdatePath.RejectLinks(full); var dir = Path.GetDirectoryName(full)!;
            while (!Directory.Exists(dir)) { madeDirectories.Add(dir); dir = Path.GetDirectoryName(dir)!; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); madeFiles.Add(full); return full;
        }
        VideoTranslationHistory.UpdateProvenance(p);
        var provenance = p.Provenance with { ProgramVersion = ApplicationBuildVersion.Value, Scenario = "video-preparation/export", ExportedUtc = DateTimeOffset.UtcNow };
        var stamp = provenance.Json();
        Task Text(string file, string text) => File.WriteAllTextAsync(Target(file), "# LOPATA " + stamp + "\n\n" + text, new UTF8Encoding(false), token);
        Task Data(string file, object? data) => File.WriteAllTextAsync(Target(file), JsonSerializer.Serialize(new { provenance, data }, Json), new UTF8Encoding(false), token);
        var roles = new Dictionary<string, string>(); var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < p.Attachments.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var a = p.Attachments[i]; var fullSource = Path.GetFullPath(a.Path); SafeUpdatePath.RejectLinks(fullSource);
                if (!paths.TryGetValue(fullSource, out var relative))
                {
                    var ext = Path.GetExtension(fullSource).ToLowerInvariant(); if (ext.Length > 16 || ext.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) ext = ".bin";
                    var folder = a.Role.StartsWith("video-reference/", StringComparison.Ordinal) ? "references" : a.Role.StartsWith("video-board/audio/", StringComparison.Ordinal) ? "audio" : "materials";
                    relative = folder + "/" + (i + 1).ToString("D5", CultureInfo.InvariantCulture) + "_" + SafeName(Path.GetFileNameWithoutExtension(fullSource)) + ext;
                    using var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(Target(relative), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _ = VideoProjectArchive.Copy(input, output, input.Length, token); output.Flush(true); paths[fullSource] = relative;
                }
                roles[a.Role] = relative; progress?.Report(.3 * (i + 1) / Math.Max(1, p.Attachments.Count));
            }
            string? Reference(string id) => p.References?.Images.FirstOrDefault(i => i.Id == id) is { } image && roles.TryGetValue(image.AttachmentRole, out var path) ? path : null;
            await Text("texts/concept.native.txt", p.IdeaText); await Text("texts/concept.en.txt", p.EnglishText);
            await Text("texts/development.native.txt", p.Development?.IdeaText ?? ""); await Text("texts/development.en.txt", p.Development?.EnglishText ?? "");
            await Text("texts/world.en.txt", p.World?.Working.EnglishText ?? "");
            await Data("texts/world.json", p.World?.Cards.Select(c => new { c.Id, c.Name, c.Kind, Native = c.Text.IdeaText, c.Fixed }));
            await Data("references/index.json", new { images = p.References?.Images.Select(i => new { i.Id, i.Name, file = Reference(i.Id), i.Origin }), bindings = p.References?.Bindings });
            var entries = new List<VideoTransferFrame>(); var time = 0d; var row = 0;
            foreach (var f in p.Storyboard!.Frames)
            {
                token.ThrowIfCancellationRequested(); var pair = VideoStoryboard.Plans(p).Single(x => x.Shot.Id == f.ShotId);
                var translated = VideoShots.SelectedTranslation(p.Shots!, pair.Scene.Id)!.Scene.Shots.Single(t => t.Id == f.ShotId);
                var folder = "scenes/" + (++row).ToString("D4", CultureInfo.InvariantCulture) + "_" + SafeName(pair.Shot.Name);
                var prompt = Prompt(translated); var image = Reference(f.ImageId);
                var entry = new VideoTransferFrame(row, f.Id, pair.Scene.Id, pair.Shot.Id, pair.Scene.Name, pair.Shot.Name, time, time + f.Seconds, f.Seconds,
                    f.Estimated, image, pair.Shot.ReferenceIds.Select(Reference).Where(s => s is not null).Cast<string>().ToArray(), f.Notes, prompt);
                entries.Add(entry); time += f.Seconds;
                await Data(folder + "/shot.json", new { timeline = entry, native = pair.Shot, working = translated });
                await Text(folder + "/prompt.en.txt", prompt);
                progress?.Report(.3 + .45 * row / p.Storyboard.Frames.Count);
            }
            await Data("timeline.json", new { durationSeconds = time, frames = entries, audio = p.Storyboard.Audio is { } audio ? new { file = roles[audio.Role], audio.Seconds, audio.TimedLyrics, audio.MetadataNote } : null });
            var csv = new StringBuilder("# LOPATA " + stamp + "\norder,scene_id,shot_id,name,start_seconds,end_seconds,duration_seconds,image,notes,prompt_en\n");
            foreach (var e in entries) csv.AppendLine(string.Join(',', new[] { e.Order.ToString(CultureInfo.InvariantCulture), e.SceneId, e.ShotId, e.Name,
                e.Start.ToString("R", CultureInfo.InvariantCulture), e.End.ToString("R", CultureInfo.InvariantCulture), e.Seconds.ToString("R", CultureInfo.InvariantCulture), e.Image ?? "", e.Notes, e.Prompt }.Select(Csv)));
            await File.WriteAllTextAsync(Target("timeline.csv"), csv.ToString(), new UTF8Encoding(false), token);
            var portable = p.Clone(); portable.Attachments = portable.Attachments.Select(a => a with { Path = roles[a.Role] }).ToList();
            await Data("project.json", new { format = "LOPATA.VideoPreparation.Transfer", schema = 1, preparation = portable });
            // Archive the exact copies already exported, even if an original changes later.
            var archived = p.Clone(); archived.Attachments = archived.Attachments.Select(a => a with { Path = Path.Combine(temporary, roles[a.Role]) }).ToList();
            VideoPreparationArchive.Save(archived, Target("project.lopataAIVid"), token);
            await File.WriteAllTextAsync(Target("storyboard.html"), VideoStoryboardHtml.Create(entries, p.Title, p.Storyboard.Audio is { } previewAudio ? roles[previewAudio.Role] : null, provenance), new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Target("README.md"), "<!-- LOPATA " + stamp + " -->\n\n# " + p.Title.Replace('\n', ' ') + "\n\n" + Guide(), new UTF8Encoding(false), token);
            var hashes = new List<object>();
            foreach (var file in madeFiles.ToArray())
            { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(file); hashes.Add(new { path = Path.GetRelativePath(temporary, file).Replace('\\', '/'), bytes = input.Length, sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) }); }
            await Data("manifest.json", new { format = "LOPATA.VideoPreparation.Export", schema = 1, projectId = p.Id, operationId, files = hashes });
            token.ThrowIfCancellationRequested(); SafeUpdatePath.RejectLinks(destination); Directory.Move(temporary, destination); progress?.Report(1);
            return new(destination, madeFiles.Count);
        }
        finally
        {
            // Delete only explicitly created files and empty directories; never recurse into
            // injected links, a user's destination folder, or the imported original files.
            if (Directory.Exists(temporary))
            {
                foreach (var file in madeFiles.AsEnumerable().Reverse()) { SafeUpdatePath.RejectLinks(file); if (File.Exists(file)) File.Delete(file); }
                foreach (var dir in madeDirectories.OrderByDescending(d => d.Length)) { SafeUpdatePath.RejectLinks(dir); if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            }
        }
    }
    public static async Task<VideoPreparationExportResult> VerifyCompletedAsync(string destination, string projectId, string operationId, CancellationToken token)
    {
        destination = Path.GetFullPath(destination); SafeUpdatePath.RejectLinks(destination);
        var manifest = Path.Combine(destination, "manifest.json"); SafeUpdatePath.RejectLinks(manifest);
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 32 * 1024 * 1024) throw new IOException("VideoPrep.ExportExisting");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token)); var data = document.RootElement.GetProperty("data");
        if (data.GetProperty("format").GetString() != "LOPATA.VideoPreparation.Export" || data.GetProperty("projectId").GetString() != projectId
            || data.GetProperty("operationId").GetString() != operationId) throw new IOException("VideoPrep.ExportExisting");
        var count = 1;
        foreach (var entry in data.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); var path = Path.GetFullPath(Path.Combine(destination, entry.GetProperty("path").GetString()!));
            if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoPrep.InvalidArchive");
            SafeUpdatePath.RejectLinks(path); using var input = File.OpenRead(path);
            if (input.Length != entry.GetProperty("bytes").GetInt64() || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)), entry.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("VideoPrep.ExportChanged"); count++;
        }
        return new(destination, count);
    }
    public static string Prompt(VideoShotPlan shot) => string.Join("\n", new[] { shot.Start, shot.End, shot.Composition, shot.Camera, shot.Light, shot.Continuity, shot.Constraints }.Where(s => !string.IsNullOrWhiteSpace(s)));
    private static string Csv(string value)
    { if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
    private static string Guide() => """
        ## Как использовать / How to use
        Откройте storyboard.html в браузере для просмотра неподвижной раскадровки под аудио. Это аниматик, без генерации движения.
        Open storyboard.html locally to preview the still storyboard, with optional audio. No video synthesis is performed.

        project.lopataAIVid — полный проект ЛОПАТЫ со всеми сохранёнными материалами и историей; project.json — открытая структура данных с относительными путями.
        texts/ — замысел, развитие, визуальный мир; references/ — исходные изображения и связи; audio/ — оригинальный звук; scenes/ — планы в порядке раскадровки с запросом и параметрами; timeline.json/csv — точные интервалы.
        Файлы TXT и CSV начинаются строкой # LOPATA с происхождением: при передаче запроса модели используйте только текст ниже этой строки. CSV-загрузчику нужно пропустить первую строку. JSON хранит provenance и data отдельными полями. Импортированные оригиналы скопированы побайтно.

        Внешняя среда должна сопоставить запрос, изображения и интервалы со своим workflow. Это универсальный набор материалов, а не обещание прямого импорта любой программой. Разрешение, FPS, длительность, генеративные настройки и лицензия выбранного исполнителя проверяются отдельно. Сохранённая видеомодель — план дальнейшего подключения, не сведения о выполненной генерации.
        Source images and audio are byte-identical copies. Match the prompts, reference files and timeline to your target workflow; automatic import into every application is not implied. Verify the target model's supported parameters and licensing separately.

        manifest.json перечисляет SHA256 и размеры всех остальных файлов. Храните структуру папок; перенесите весь каталог целиком.
        """;
}
public sealed record VideoTransferFrame(int Order, string Id, string SceneId, string ShotId, string Scene, string Name, double Start, double End,
    double Seconds, bool Estimated, string? Image, string[] References, string Notes, string Prompt);
