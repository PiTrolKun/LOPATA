using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoPreparationTests
{
    [TestMethod]
    public void ConceptSurvivesPortableSaveAutosaveRecoveryAndCloneWithoutRewriting()
    {
        using var files = new TestFiles();
        var text = "  Мирон и LOPATA.\r\n\tБез монстров. Don't translate «Лопата».\r\n" + new string('я', 12000) + "  ";
        var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Form = "story"; session.Project.CurrentStep = "idea";
        session.Project.IdeaText = text; session.Changed(); session.Checkpoint();
        Assert.IsTrue(session.HasContent);
        Assert.AreEqual(text, session.Project.Clone().IdeaText);
        var restored = new VideoPreparationSession(files.File("recovery"));
        restored.Load(session.RecoveryPath, recovery: true);
        Assert.AreEqual(text, restored.Project.IdeaText);
        restored.Save(files.File("concept.lopataAIVid"));
        restored.Project.IdeaText += "\nЕщё один абзац."; restored.Changed(); restored.Checkpoint();
        var moved = files.File("moved.lopataAIVid"); File.Move(restored.ArchivePath!, moved);
        var loaded = VideoPreparationArchive.Load(moved, files.File("opened"));
        Assert.AreEqual(restored.Project.IdeaText, loaded.IdeaText);
        using var zip = ZipFile.OpenRead(moved);
        using var reader = new StreamReader(zip.GetEntry("project.json")!.Open());
        var json = JsonNode.Parse(reader.ReadToEnd())!;
        Assert.AreEqual(restored.Project.IdeaText, (string?)json["Project"]!["IdeaText"]);
        Assert.AreEqual("LOPATA", (string?)json["Project"]!["Provenance"]!["Program"]);
    }

    [TestMethod]
    public void StepOneArchiveWithoutConceptOpensEmptyButExplicitNullIsRejected()
    {
        using var files = new TestFiles();
        var archive = files.File("old.lopataAIVid");
        VideoPreparationArchive.Save(new() { Title = "Старый проект", Form = "story", CurrentStep = "idea" }, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!; JsonNode json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
            json["Project"]!.AsObject().Remove("IdeaText"); entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open()); writer.Write(json.ToJsonString());
        }
        var loaded = VideoPreparationArchive.Load(archive, files.File("opened"));
        Assert.AreEqual("", loaded.IdeaText); Assert.AreEqual("Старый проект", loaded.Title);
        loaded.IdeaText = null!;
        Assert.ThrowsExactly<InvalidDataException>(() => VideoPreparationArchive.Save(loaded, files.File("invalid.lopataAIVid")));
    }

    [TestMethod]
    public void PortableArchiveKeepsAllAttachmentsWithoutOriginalPaths()
    {
        using var files = new TestFiles();
        var original = files.File("original.svg"); File.WriteAllText(original, "<svg>Кадр</svg>");
        var project = new VideoPreparationProject { Title = "Ночной город", Form = "imagery", CurrentStep = "idea" };
        project.Attachments.Add(new(original, "reference")); project.Attachments.Add(new(original, "alternative"));
        var archive = files.File("clip.lopataAIVid"); VideoPreparationArchive.Save(project, archive);
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.AreEqual(2, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("project.json")!.Open());
            var manifest = reader.ReadToEnd(); Assert.IsFalse(manifest.Contains(files.Root, StringComparison.Ordinal));
            var json = JsonNode.Parse(manifest)!;
            Assert.AreEqual("LOPATA", (string?)json["Project"]!["Provenance"]!["Program"]);
            Assert.AreEqual("video-preparation/project", (string?)json["Project"]!["Provenance"]!["Scenario"]);
        }
        File.Delete(original);
        var moved = files.File("moved.lopataAIVid"); File.Move(archive, moved);
        var loaded = VideoPreparationArchive.Load(moved, files.File("opened"));
        Assert.AreEqual(project.Id, loaded.Id); Assert.AreEqual("idea", loaded.CurrentStep);
        Assert.AreEqual("Ночной город", loaded.Title);
        Assert.AreEqual(loaded.Attachments[0].Path, loaded.Attachments[1].Path);
        Assert.AreEqual("<svg>Кадр</svg>", File.ReadAllText(loaded.Attachments[0].Path));
    }

    [TestMethod]
    public void CancelledOrFailedSavePreservesPreviousArchiveAndOtherProject()
    {
        using var files = new TestFiles(); var project = new VideoPreparationProject { Form = "story" };
        var path = files.File("clip.lopataAIVid"); VideoPreparationArchive.Save(project, path);
        var bytes = File.ReadAllBytes(path);
        Assert.ThrowsExactly<OperationCanceledException>(() => VideoPreparationArchive.Save(project, path, new CancellationToken(true)));
        project.Attachments.Add(new(files.File("missing.png"), "reference"));
        Assert.ThrowsExactly<IOException>(() => VideoPreparationArchive.Save(project, path));
        Assert.ThrowsExactly<IOException>(() => VideoPreparationArchive.Save(new(), path));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.tmp").Length);
    }

    [TestMethod]
    public void DamagedAttachmentIsRejectedAndPartialExtractionRemoved()
    {
        using var files = new TestFiles(); var original = files.File("audio.wav"); File.WriteAllBytes(original, [1, 2, 3]);
        var project = new VideoPreparationProject(); project.Attachments.Add(new(original, "audio"));
        var archive = files.File("clip.lopataAIVid"); VideoPreparationArchive.Save(project, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(e => e.FullName.StartsWith("media/", StringComparison.Ordinal));
            using var stream = entry.Open(); stream.Write([9, 9, 9]);
        }
        var workspace = files.File("opened");
        Assert.ThrowsExactly<InvalidDataException>(() => VideoPreparationArchive.Load(archive, workspace));
        Assert.AreEqual(0, Directory.GetDirectories(workspace).Length);
    }

    [TestMethod]
    public void ArchiveTraversalAndUnsupportedSchemaAreRejectedBeforeExtraction()
    {
        using var files = new TestFiles(); var archive = files.File("clip.lopataAIVid");
        VideoPreparationArchive.Save(new(), archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry("../escape.txt").Open(); stream.WriteByte(1);
        }
        Assert.ThrowsExactly<InvalidDataException>(() => VideoPreparationArchive.Load(archive, files.File("opened")));
        Assert.IsFalse(File.Exists(files.File("escape.txt")));
        var future = new VideoPreparationProject { Schema = 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => VideoPreparationArchive.Save(future, files.File("future.lopataAIVid")));
    }

    [TestMethod]
    public void RecoveryIsDistinctFromChosenArchiveAndRecoversUnsavedInput()
    {
        using var files = new TestFiles(); var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Title = "Мой клип"; session.Project.Form = "story"; session.Changed(); session.Checkpoint();
        Assert.IsNull(session.ArchivePath); Assert.IsTrue(session.IsDirty); Assert.IsTrue(session.RecoveryCurrent);
        var restored = new VideoPreparationSession(files.File("recovery")); restored.Load(session.RecoveryPath, recovery: true);
        Assert.AreEqual("Мой клип", restored.Project.Title); Assert.IsNull(restored.ArchivePath);
        var destination = files.File("chosen.lopataAIVid"); restored.Save(destination);
        restored.Project.Title = "Правка"; restored.Changed(); restored.Checkpoint();
        Assert.IsFalse(restored.IsDirty);
        Assert.AreEqual("Правка", VideoPreparationArchive.Load(destination, files.File("opened")).Title);
    }

    [TestMethod]
    public void FailedOpenOrSaveDoesNotReplaceCurrentSession()
    {
        using var files = new TestFiles(); var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Title = "Не терять"; session.Changed();
        var broken = files.File("bad.lopataAIVid"); File.WriteAllText(broken, "invalid");
        Assert.ThrowsExactly<InvalidDataException>(() => session.Load(broken));
        Assert.AreEqual("Не терять", session.Project.Title);
        Assert.ThrowsExactly<IOException>(() => session.Save(files.File("wrong.txt")));
        Assert.IsNull(session.ArchivePath); Assert.IsTrue(session.IsDirty);
    }

    [TestMethod]
    public async Task ExplorerOpeningUsesDistinctOwnedIconAndExistingInstanceProtocol()
    {
        using var files = new TestFiles(); var path = files.File("Клип.lopataAIVid");
        var request = AIHub.Services.VideoEditor.VideoFileRequest.Parse(["--video-project", "--", path]);
        Assert.IsNotNull(request);
        Assert.IsTrue(AIHub.Services.ApplicationUpdateStartup.IsVideoFileLaunch(["--video-project", "--", path]));
        Assert.IsFalse(AIHub.Services.ApplicationUpdateStartup.IsVideoFileLaunch(["--background"]));
        using var stream = new MemoryStream();
        await AIHub.Services.VideoEditor.VideoFileProtocol.WriteAsync(stream, request, default);
        stream.Position = 1;
        Assert.AreEqual(request, await AIHub.Services.VideoEditor.VideoFileProtocol.ReadAsync(stream, default));
        var root = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var shell = new AIHub.Services.VideoEditor.VideoShellIntegration(@"H:\Test LOPATA\AIHub.exe", root, preparation: true);
            shell.Apply(true, "AI video project");
            using (var extension = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\.lopataAIVid"))
                Assert.AreEqual(shell.ProgId, extension!.GetValue(""));
            using (var icon = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + shell.ProgId + @"\DefaultIcon"))
                StringAssert.Contains(icon!.GetValue("") as string, "AIVideoFileIcon.ico");
            using (var extension = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\.lopataAIVid")) extension.SetValue("", "Other.App");
            shell.Apply(true, "AI video project"); shell.Apply(false, "");
            using (var extension = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\.lopataAIVid"))
                Assert.AreEqual("Other.App", extension!.GetValue(""));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }

    [TestMethod]
    public void UninstallRemovesBothOwnVideoAssociationsAndPreservesOtherOwners()
    {
        var root = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        const string executable = @"H:\Test LOPATA\AIHub.exe";
        var editor = new AIHub.Services.VideoEditor.VideoShellIntegration(executable, root);
        var preparation = new AIHub.Services.VideoEditor.VideoShellIntegration(executable, root, preparation: true);
        var other = new AIHub.Services.VideoEditor.VideoShellIntegration(@"H:\Other LOPATA\AIHub.exe", root, preparation: true);
        try
        {
            editor.Apply(true, "Video"); preparation.Apply(true, "AI video"); other.Apply(true, "Other installation");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\.lopataAIVid")) key.SetValue("", "Other.App");
            AIHub.Services.VideoEditor.VideoShellIntegration.RemoveOwnedProjects(executable, root);
            using var oldEditor = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + editor.ProgId);
            using var oldPreparation = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + preparation.ProgId);
            using var unrelated = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + other.ProgId);
            using var defaultValue = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\.lopataAIVid");
            using var openWith = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\.lopataAIVid\OpenWithProgids");
            Assert.IsNull(oldEditor); Assert.IsNull(oldPreparation); Assert.IsNotNull(unrelated);
            Assert.AreEqual("Other.App", defaultValue!.GetValue(""));
            Assert.IsNull(openWith!.GetValue(preparation.ProgId)); Assert.IsNotNull(openWith.GetValue(other.ProgId));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
    internal sealed class TestFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LOPATA-VideoPreparation-" + Guid.NewGuid().ToString("N"));
        public TestFiles() => Directory.CreateDirectory(Root);
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(absolute) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(absolute).StartsWith("LOPATA-VideoPreparation-", StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
