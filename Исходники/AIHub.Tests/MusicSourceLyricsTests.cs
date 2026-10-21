using System.IO;
using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicSourceLyricsTests
{
    [TestMethod]
    public void ResolverUsesTheVerifiedInstallationAndFindsReleaseSubfolder()
    {
        using var files = new MusicProjectTests.Files(); var release = Path.Combine(files.Root, "Release"); Directory.CreateDirectory(release);
        var executable = Path.Combine(release, "whisper-cli.exe"); File.WriteAllText(executable, "fixture");
        var status = Status(files.Root); Assert.AreEqual(executable, InstalledComponentArtifact.Resolve(status));
        var direct = Path.Combine(files.Root, "whisper-cli.exe"); File.WriteAllText(direct, "preferred direct fixture");
        Assert.AreEqual(direct, InstalledComponentArtifact.Resolve(status));
        Assert.AreEqual(executable, InstalledComponentArtifact.Resolve(Status(executable)));
    }
    [TestMethod]
    public void ResolverRejectsMissingAmbiguousAndUnhealthyInstallations()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        Assert.Throws<FileNotFoundException>(() => InstalledComponentArtifact.Resolve(Status(files.Root)));
        foreach (var folder in new[] { "a", "b" }) { Directory.CreateDirectory(Path.Combine(files.Root, folder)); File.WriteAllText(Path.Combine(files.Root, folder, "whisper-cli.exe"), "fixture"); }
        Assert.Throws<FileNotFoundException>(() => InstalledComponentArtifact.Resolve(Status(files.Root)));
        Assert.Throws<FileNotFoundException>(() => InstalledComponentArtifact.Resolve(new() { Entry = new() { Id = "fixture", HealthCheckRelativePath = "whisper-cli.exe" }, Record = new() { InstallPath = files.Root }, IsHealthy = false, IsInstalled = true }));
    }
    [TestMethod]
    public void SourceTextSupportsRussianEncodingsWithoutChangingWords()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        const string words = "Куплет\r\nНочь и свет\r\nПрипев\r\nПесня летит";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encodings = new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.GetEncoding(1251) };
        for (var i = 0; i < encodings.Length; i++) { var path = Path.Combine(files.Root, i + ".txt"); File.WriteAllText(path, words, encodings[i]); Assert.AreEqual(words, MusicSourceText.Read(path)); }
    }
    [TestMethod]
    public void SourceTextRejectsBinaryEmptyAndOversizedInput()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root); var path = Path.Combine(files.Root, "source.txt");
        File.WriteAllBytes(path, [0,1,2,3]); Assert.Throws<InvalidDataException>(() => MusicSourceText.Read(path));
        Assert.Throws<InvalidDataException>(() => MusicSourceText.Validate(" \n"));
        Assert.Throws<InvalidDataException>(() => MusicSourceText.Validate(new string('a', MusicSourceText.MaximumCharacters + 1)));
    }
    private static ComponentStatusSnapshot Status(string path) => new() {
        Entry = new() { Id = "fixture.whisper", HealthCheckRelativePath = "whisper-cli.exe" }, Record = new() { InstallPath = path }, IsHealthy = true, IsInstalled = true };
}
