using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoPortableTests
{
    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lopata-video-pack-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static VideoProject Fixture(string root)
    {
        var p = new VideoProject { Name = "Кот 日本語", PreviewDirectory = System.IO.Path.Combine(root, "Preview") };
        Directory.CreateDirectory(p.PreviewDirectory);
        foreach (var kind in new[] { "video", "audio", "svg", "image" }) {
            var path = System.IO.Path.Combine(root, kind + "." + (kind == "video" ? "mp4" : kind == "audio" ? "wav" : kind == "image" ? "png" : "svg"));
            File.WriteAllText(path, "Original bytes: " + kind); var f = new FileInfo(path);
            p.Assets.Add(new() { Path = f.FullName, Kind = kind, Name = f.Name, Length = f.Length, LastWriteUtc = f.LastWriteTimeUtc, Duration = 5, Models = kind == "svg" ? [new("actual", "Actual SVG model", "revision", "SVG generation")] : [] });
        }
        var first = p.Assets[0]; first.Proxy = System.IO.Path.Combine(p.PreviewDirectory, first.Id + "-gop1.mp4"); File.WriteAllText(first.Proxy, "proxy bytes");
        File.WriteAllText(System.IO.Path.Combine(p.PreviewDirectory, first.Id + ".png"), "thumbnail");
        var srt = new FileInfo(System.IO.Path.Combine(root, "subtitles.srt")); File.WriteAllText(srt.FullName, "1\n00:00:00,000 --> 00:00:01,000\nКот"); srt.Refresh(); p.ImportedFiles.Add(new(srt.FullName, srt.Name, srt.Length, srt.LastWriteTimeUtc));
        p.Tracks[0].Clips.Add(new() { AssetId = first.Id, Duration = 2, X = 12, Scale = 1.5 });
        p.Narration.Add(new() { Start = 0, End = 2, Text = "Проверка", Models = [new("actual-text", "Actual text model", "rev-text", "narration draft")] });
        VideoSeries.Ensure(p); return p;
    }
    [TestMethod]
    public void ArchiveIsPortableContainsUnusedMediaAndProvenanceAndPreservesOriginalBytes()
    {
        using var folder = new Folder(); var originals = System.IO.Path.Combine(folder.Path, "originals"); Directory.CreateDirectory(originals);
        var p = Fixture(originals); var path = System.IO.Path.Combine(folder.Path, "Переносимый.lopataVideo"); var hashes = p.Assets.Select(a => SHA256.HashData(File.ReadAllBytes(a.Path))).ToArray();
        var duplicate = JsonSerializer.Deserialize<VideoAsset>(JsonSerializer.Serialize(p.Assets[0]))!; duplicate.Id = Guid.NewGuid().ToString("N"); p.Assets.Add(duplicate);
        var before = JsonSerializer.Serialize(p); VideoProjectArchive.Save(p, path);
        using (var zip = ZipFile.OpenRead(path)) {
            using var reader = new StreamReader(zip.GetEntry("project.json")!.Open()); var manifest = reader.ReadToEnd();
            Assert.IsFalse(manifest.Contains(originals, StringComparison.Ordinal)); Assert.IsFalse(manifest.Contains(originals.Replace("\\", "\\\\"), StringComparison.Ordinal));
            using var doc = JsonDocument.Parse(manifest); var saved = doc.RootElement.GetProperty("Project"); Assert.AreEqual("LOPATA", saved.GetProperty("Provenance").GetProperty("Program").GetString());
            Assert.AreEqual(2, saved.GetProperty("Provenance").GetProperty("Models").GetArrayLength());
            Assert.AreEqual(7, doc.RootElement.GetProperty("Files").GetArrayLength(), "Four originals, one proxy, one thumbnail and imported SRT; reused sources are stored once");
        }
        Assert.AreEqual(before, JsonSerializer.Serialize(p));
        Directory.Move(originals, System.IO.Path.Combine(folder.Path, "originals-unavailable"));
        var loaded = VideoProjectStore.Load(path);
        try {
            for (var i = 0; i < hashes.Length; i++) CollectionAssert.AreEqual(hashes[i], SHA256.HashData(File.ReadAllBytes(loaded.Assets[i].Path)));
            Assert.IsTrue(File.Exists(loaded.Assets[0].Proxy)); Assert.AreEqual(12, loaded.Tracks[0].Clips[0].X); Assert.AreEqual(1.5, loaded.Tracks[0].Clips[0].Scale);
            Assert.IsTrue(File.Exists(loaded.ImportedFiles.Single().Path)); Assert.AreEqual("Проверка", loaded.Narration.Single().Text); VideoProjectStore.VerifySources(loaded);
            var moved = System.IO.Path.Combine(folder.Path, "Повторно.lopataVideo"); VideoProjectArchive.Save(loaded, moved); Assert.IsTrue(File.Exists(moved));
        }
        finally { if (loaded.PreviewDirectory is {} preview) Directory.Delete(System.IO.Path.GetDirectoryName(preview)!, true); }
    }
    [TestMethod]
    public void FailedOrCancelledSavePreservesPreviousArchiveAndProtectsSources()
    {
        using var folder = new Folder(); var p = Fixture(folder.Path); var destination = System.IO.Path.Combine(folder.Path, "project.lopataVideo");
        VideoProjectArchive.Save(p, destination); var hash = SHA256.HashData(File.ReadAllBytes(destination));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.ThrowsExactly<OperationCanceledException>(() => VideoProjectArchive.Save(p, destination, cancelled.Token));
        var thumbnail = System.IO.Path.Combine(p.PreviewDirectory!, p.Assets[0].Id + ".png");
        using (var locked = new FileStream(thumbnail, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Throws<IOException>(() => VideoProjectArchive.Save(p, destination));
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(destination))); Assert.AreEqual(0, Directory.GetFiles(folder.Path, "*.tmp").Length);
        Assert.Throws<IOException>(() => VideoProjectArchive.Save(p, p.Assets[0].Path));
        var foreign = new VideoProject(); Assert.Throws<IOException>(() => VideoProjectArchive.Save(foreign, destination));
    }
    [TestMethod]
    public void ArchiveRejectsTraversalDuplicatesAndModifiedPayloadWithoutLeavingExtractedFiles()
    {
        using var folder = new Folder(); var p = Fixture(folder.Path); var path = System.IO.Path.Combine(folder.Path, "bad.lopataVideo"); VideoProjectArchive.Save(p, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { var entry = zip.Entries.First(e => e.FullName.StartsWith("media/")); var name = entry.FullName; var length = entry.Length; entry.Delete(); using var output = zip.CreateEntry(name).Open(); output.Write(new byte[length]); }
        var packages = System.IO.Path.Combine(VideoProjectStore.Work(p.Id), "Packages");
        Assert.Throws<InvalidDataException>(() => VideoProjectArchive.Load(path)); Assert.AreEqual(0, Directory.GetDirectories(packages).Length);
        File.Delete(path); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape").Open()); writer.Write("bad"); }
        Assert.Throws<InvalidDataException>(() => VideoProjectArchive.Load(path)); Assert.IsFalse(File.Exists(System.IO.Path.Combine(folder.Path, "escape")));
        File.Delete(path); VideoProjectArchive.Save(p, path); using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("project.json").Open()); writer.Write("{}"); }
        Assert.Throws<InvalidDataException>(() => VideoProjectArchive.Load(path));
    }
    [TestMethod]
    public async Task LegacySnapshotsAndShellProtocolRemainCompatibleAndRegistrationIsOwned()
    {
        using var folder = new Folder(); var p = Fixture(folder.Path); var old = System.IO.Path.Combine(folder.Path, "legacy.lopataVideo"); VideoProjectStore.Save(p, old);
        Assert.AreEqual('{', File.ReadAllText(old).TrimStart()[0]); Assert.AreEqual(p.Id, VideoProjectStore.Load(old).Id);
        var request = new VideoFileRequest(old); Assert.AreEqual(request, VideoFileRequest.Parse(["--video-project", "--", old]));
        Assert.Throws<InvalidDataException>(() => VideoFileRequest.Parse(["--video-project", old])); Assert.Throws<InvalidDataException>(() => new VideoFileRequest(@"C:\bad.exe").Validate());
        using var stream = new MemoryStream(); await VideoFileProtocol.WriteAsync(stream, request, default); stream.Position = 1; Assert.AreEqual(request, await VideoFileProtocol.ReadAsync(stream, default));
        var root = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        try {
            var shell = new VideoShellIntegration(@"H:\Test LOPATA\AIHub.exe", root); shell.Apply(true, "Video project");
            using (var extension = Registry.CurrentUser.OpenSubKey(root + @"\.lopataVideo")) Assert.AreEqual(shell.ProgId, extension!.GetValue(""));
            using (var command = Registry.CurrentUser.OpenSubKey(root + "\\" + shell.ProgId + @"\shell\open\command")) Assert.AreEqual("\"H:\\Test LOPATA\\AIHub.exe\" --video-project -- \"%1\"", command!.GetValue(""));
            using (var icon = Registry.CurrentUser.OpenSubKey(root + "\\" + shell.ProgId + @"\DefaultIcon")) StringAssert.Contains(icon!.GetValue("") as string, "VideoFileIcon.ico");
            using (var extension = Registry.CurrentUser.CreateSubKey(root + @"\.lopataVideo")) extension.SetValue("", "Other.Video"); shell.Apply(true, "Video project"); shell.Apply(false, "");
            using (var extension = Registry.CurrentUser.OpenSubKey(root + @"\.lopataVideo")) Assert.AreEqual("Other.Video", extension!.GetValue(""));
            Assert.IsNull(Registry.CurrentUser.OpenSubKey(root + "\\" + shell.ProgId));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
}
