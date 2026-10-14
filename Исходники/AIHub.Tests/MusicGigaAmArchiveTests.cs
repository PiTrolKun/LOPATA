using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicGigaAmArchiveTests
{
    private static string Archive(string root,string name,bool link=false)
    {
        var path=Path.Combine(root,Guid.NewGuid()+".tar.gz");
        using var file=File.Create(path); using var gzip=new GZipStream(file,CompressionMode.Compress);
        using var writer=new TarWriter(gzip);
        var entry=new PaxTarEntry(link?TarEntryType.SymbolicLink:TarEntryType.RegularFile,name);
        if(link) entry.LinkName="../../outside";
        else entry.DataStream=new MemoryStream([1,2,3]);
        writer.WriteEntry(entry); return path;
    }

    [TestMethod]
    public void TarGzipExtractsThePythonSourceInsteadOfAnOpaqueInnerTar()
    {
        using var files=new MusicProjectTests.Files();
        var destination=Path.Combine(files.Root,"stage"); Directory.CreateDirectory(destination);
        ComponentTarGzipExtractor.Extract(Archive(files.Root,"package/src/antlr4/__init__.py"),destination,100,10);
        CollectionAssert.AreEqual(new byte[]{1,2,3},File.ReadAllBytes(Path.Combine(destination,"package/src/antlr4/__init__.py")));
    }

    [TestMethod]
    [DataRow("../outside.py",false)]
    [DataRow("package/src/link.py",true)]
    [DataRow("package/file.py:stream",false)]
    public void TarGzipRejectsTraversalLinksAndAlternateStreams(string name,bool link)
    {
        using var files=new MusicProjectTests.Files();
        var destination=Path.Combine(files.Root,"stage"); Directory.CreateDirectory(destination);
        Assert.Throws<InvalidDataException>(()=>ComponentTarGzipExtractor.Extract(Archive(files.Root,name,link),destination,100,10));
        Assert.IsFalse(Directory.EnumerateFiles(destination,"*",SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public void TarGzipEnforcesUncompressedByteLimit()
    {
        using var files=new MusicProjectTests.Files();
        var destination=Path.Combine(files.Root,"stage"); Directory.CreateDirectory(destination);
        Assert.Throws<InvalidDataException>(()=>ComponentTarGzipExtractor.Extract(Archive(files.Root,"package/file.py"),destination,2,10));
    }
}
