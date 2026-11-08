using System.Buffers.Binary;
using System.IO;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderWindowsTests
{
    private static ReaderAssociation Empty(string ext) => new(ext, "", "", "", false, false, false);

    [TestMethod]
    public void RegistrationPreservesExistingDefaultsAndUserChoiceAndCoversRealFormatsOnly()
    {
        using var fixture = new Fixture(); var registration = new ReaderShellIntegration(fixture.Executable, fixture.Root);
        using (var ext = Registry.CurrentUser.CreateSubKey(fixture.Root + @"\Classes\.docx")) ext.SetValue("", "Word.Document.12");
        using (var choice = Registry.CurrentUser.CreateSubKey(fixture.Root + @"\Classes\.epub\UserChoice")) choice.SetValue("Hash", "unchanged");
        registration.Apply(true, "Читалка ЛОПАТЫ", "Документы и книги", ext => ext == ".epub" ? Empty(ext) with { HasUserChoice = true } : Empty(ext));
        using var command = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath + @"\shell\open\command");
        Assert.AreEqual($"\"{fixture.Executable}\" --reader -- \"%1\"", command!.GetValue(""));
        using var doc = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.docx");
        Assert.AreEqual("Word.Document.12", doc!.GetValue(""));
        using var book = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.fb2");
        Assert.AreEqual(registration.ProgId, book!.GetValue(""));
        using var epub = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.epub"); Assert.IsNull(epub!.GetValue(""));
        using var caps = Registry.CurrentUser.OpenSubKey(registration.CapabilitiesPath + @"\FileAssociations");
        CollectionAssert.AreEquivalent(ReaderFileRequest.Extensions.ToArray(), caps!.GetValueNames());
        Assert.AreEqual(registration.ProjectProgId, caps.GetValue(".lopatadoxai"));
        Assert.AreEqual(registration.ProgId, caps.GetValue(".docx"));
        using var app = Registry.CurrentUser.OpenSubKey(registration.CapabilitiesPath);
        Assert.AreEqual(registration.RegisteredName, app!.GetValue("ApplicationName"));
        using var advertised = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\RegisteredApplications");
        Assert.AreEqual(registration.CapabilitiesPath, advertised!.GetValue(registration.RegisteredName));
        Assert.IsTrue(ReaderFileRequest.Extensions.Contains(".LOPATAdoxAI", StringComparer.OrdinalIgnoreCase));
        foreach (var ext in new[] { ".zip", ".png", ".jpg", ".AIdox" })
            Assert.IsFalse(ReaderFileRequest.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
        registration.Apply(false, "", "");
        Assert.AreEqual("Word.Document.12", doc.GetValue(""));
        using var preserved = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.epub\UserChoice"); Assert.AreEqual("unchanged", preserved!.GetValue("Hash"));
        using var removed = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath); Assert.IsNull(removed);
    }

    [TestMethod]
    public void ProjectHasItsOwnIconAndUpgradesOnlyTheOwnedLegacyDefault()
    {
        using var fixture = new Fixture();
        var shell = new ReaderShellIntegration(fixture.Executable, fixture.Root);
        shell.Apply(true, "Reader", "Books", Empty, "Проект ЛОПАТЫ");
        var projectClass = fixture.Root + @"\Classes\" + shell.ProjectProgId;
        string icon(string name) => $"\"{Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "Assets", name)}\",0";
        Assert.AreEqual(icon("ReaderFileIcon.ico"), Registry.GetValue(@"HKEY_CURRENT_USER\" + shell.RegisteredPath + @"\DefaultIcon", "", null));
        Assert.AreEqual(icon("OfficeProjectFileIcon.ico"), Registry.GetValue(@"HKEY_CURRENT_USER\" + projectClass + @"\DefaultIcon", "", null));
        Assert.AreEqual($"\"{fixture.Executable}\" --reader -- \"%1\"", Registry.GetValue(@"HKEY_CURRENT_USER\" + projectClass + @"\shell\open\command", "", null));
        Assert.AreEqual("Проект ЛОПАТЫ", Registry.GetValue(@"HKEY_CURRENT_USER\" + projectClass, "FriendlyTypeName", null));
        using var ext = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.lopatadoxai", true)!;
        Assert.AreEqual(shell.ProjectProgId, ext.GetValue(""));
        Registry.CurrentUser.DeleteSubKeyTree(projectClass);
        ext.SetValue("", shell.ProgId);
        using var candidates = ext.OpenSubKey("OpenWithProgids", true)!;
        candidates.DeleteValue(shell.ProjectProgId); candidates.SetValue(shell.ProgId, "");
        shell.Apply(true, "Reader", "Books", e => Empty(e) with { ProgId = shell.ProgId });
        Assert.AreEqual(shell.ProjectProgId, ext.GetValue(""));
        Assert.IsNotNull(candidates.GetValue(shell.ProjectProgId));
        Assert.IsNull(candidates.GetValue(shell.ProgId));
        ext.SetValue("", shell.ProgId);
        shell.Apply(true, "Reader", "Books", e => Empty(e) with { HasUserChoice = true, ProgId = shell.ProgId });
        Assert.AreEqual(shell.ProgId, ext.GetValue(""));
        ext.SetValue("", "Other.Project");
        shell.Apply(true, "Reader", "Books", e => Empty(e) with { ProgId = "Other.Project" });
        shell.Apply(false, "", "");
        Assert.AreEqual("Other.Project", ext.GetValue(""));
        Assert.IsNull(candidates.GetValue(shell.ProjectProgId));
        using var removed = Registry.CurrentUser.OpenSubKey(projectClass);
        Assert.IsNull(removed);
    }

    [TestMethod]
    public void RemovingOneInstallationKeepsOtherCandidatesAndChangedDefaults()
    {
        using var fixture = new Fixture();
        var first = new ReaderShellIntegration(fixture.Executable, fixture.Root);
        var second = new ReaderShellIntegration(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "Other.exe"), fixture.Root);
        first.Apply(true, "First", "Reader", Empty); second.Apply(true, "Second", "Reader", ext => Empty(ext) with { ProgId = first.ProgId });
        using (var ext = Registry.CurrentUser.CreateSubKey(fixture.Root + @"\Classes\.fb2")) ext.SetValue("", "Another.Reader");
        first.Apply(false, "", "");
        using var other = Registry.CurrentUser.OpenSubKey(second.RegisteredPath); Assert.IsNotNull(other);
        using var open = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.fb2\OpenWithProgids");
        Assert.IsNotNull(open!.GetValue(second.ProgId)); Assert.IsNull(open.GetValue(first.ProgId));
        using var current = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.fb2"); Assert.AreEqual("Another.Reader", current!.GetValue(""));
        using var projects = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\.lopatadoxai\OpenWithProgids");
        Assert.IsNotNull(projects!.GetValue(second.ProjectProgId)); Assert.IsNull(projects.GetValue(first.ProjectProgId));
        using var otherProject = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\Classes\" + second.ProjectProgId);
        Assert.IsNotNull(otherProject);
    }

    [TestMethod]
    public void ForeignOwnershipAndUnsafeTestRootsAreRejected()
    {
        using var fixture = new Fixture(); var registration = new ReaderShellIntegration(fixture.Executable, fixture.Root);
        using (var foreign = Registry.CurrentUser.CreateSubKey(registration.RegisteredPath)) foreign.SetValue("", "Foreign");
        Assert.Throws<InvalidOperationException>(() => registration.Apply(true, "Reader", "Books", Empty));
        registration.Apply(false, "", "");
        using var unchanged = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath); Assert.AreEqual("Foreign", unchanged!.GetValue(""));
        Assert.Throws<ArgumentException>(() => new ReaderShellIntegration(fixture.Executable, @"Software\Classes"));
        Assert.Throws<ArgumentException>(() => new ReaderShellIntegration("relative.exe", fixture.Root));
    }

    [TestMethod]
    public void ExistingApplicationsEntryWithSameExecutableNameIsNotReplaced()
    {
        using var fixture = new Fixture();
        var application = fixture.Root + @"\Classes\Applications\AIHub.exe";
        using (var foreign = Registry.CurrentUser.CreateSubKey(application + @"\shell\open\command")) foreign.SetValue("", "Another installed copy");
        var registration = new ReaderShellIntegration(fixture.Executable, fixture.Root);
        registration.Apply(true, "Reader", "Books", Empty); registration.Apply(false, "", "");
        using var preserved = Registry.CurrentUser.OpenSubKey(application + @"\shell\open\command");
        Assert.AreEqual("Another installed copy", preserved!.GetValue(""));
    }

    [TestMethod]
    public void AutomaticChoiceIsConservativeAndManualPreferenceWins()
    {
        var settings = new FileViewerSettings(); var free = Empty(".pdf");
        Assert.IsTrue(ReaderAssociationDiscovery.PreferInternal(".pdf", settings, free));
        Assert.IsFalse(ReaderAssociationDiscovery.PreferInternal(".pdf", settings, free with { HasOtherReader = true }));
        Assert.IsFalse(ReaderAssociationDiscovery.PreferInternal(".pdf", settings, free with { Uncertain = true }));
        Assert.IsFalse((free with { HasUserChoice = true }).CanRegisterDefault);
        settings.PreferInternalByExtension[".pdf"] = true;
        Assert.IsTrue(ReaderAssociationDiscovery.PreferInternal(".pdf", settings, free with { HasOtherReader = true }));
        settings.PreferInternalByExtension[".pdf"] = false; Assert.IsFalse(ReaderAssociationDiscovery.PreferInternal(".pdf", settings, free));
        Assert.IsTrue(ReaderAssociationDiscovery.IsOther(@"C:\Custom\notepad.exe", ""));
        Assert.IsTrue(ReaderAssociationDiscovery.IsOther("Unknown.AppX.Reader", ""));
    }

    [TestMethod]
    public async Task UnicodeFileRequestsRoundTripAndRejectMalformedPayloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "Книга с пробелами & знаком!.EPUB");
        var request = ReaderFileRequest.Parse(["--reader", "--", path]); Assert.IsNotNull(request);
        Assert.AreEqual(request, ReaderFileRequest.Parse([path]));
        using var stream = new MemoryStream(); await ReaderFileProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0; Assert.AreEqual((int)ReaderFileProtocol.Marker, stream.ReadByte());
        Assert.AreEqual(request, await ReaderFileProtocol.ReadAsync(stream, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ReaderFileRequest.Parse(["--reader", path]));
        Assert.Throws<InvalidDataException>(() => ReaderFileRequest.Parse(["--reader", "--", path, "extra"]));
        Assert.Throws<InvalidDataException>(() => ReaderFileRequest.Parse(["--reader", "--", "relative.epub"]));
        Assert.Throws<InvalidDataException>(() => new ReaderFileRequest(Path.ChangeExtension(path, ".exe")).Validate());
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, 131041); using var oversized = new MemoryStream(length);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReaderFileProtocol.ReadAsync(oversized, CancellationToken.None));
        using var invalidUtf8 = new MemoryStream([1, 0, 0, 0, 0xff]);
        await Assert.ThrowsExactlyAsync<System.Text.DecoderFallbackException>(() => ReaderFileProtocol.ReadAsync(invalidUtf8, CancellationToken.None));
    }

    [TestMethod]
    public void NativeAssociationReadIsBoundedAndReportsActualCurrentHandler()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var states = ReaderFileRequest.Extensions.Select(ReaderAssociationDiscovery.Read).ToArray();
        Assert.AreEqual(ReaderFileRequest.Extensions.Count, states.Length);
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(20), "Reading registered readers was unexpectedly slow.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107");
        File.WriteAllText(Path.Combine(root, "windows-associations-current.json"), System.Text.Json.JsonSerializer.Serialize(states,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        internal string Executable { get; } = Path.Combine(Path.GetTempPath(), "Читалка ЛОПАТЫ", "AIHub.exe");
        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Root, false);
    }
}
