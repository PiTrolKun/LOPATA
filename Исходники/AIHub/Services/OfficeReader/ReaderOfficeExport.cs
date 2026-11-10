using System.IO;
using System.Text;
using AIHub.Services.Books;

namespace AIHub.Services.OfficeReader;

internal static class ReaderOfficeExport
{
    internal static string ReadText(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException) { return File.ReadAllText(path, Encoding.GetEncoding(1251)); }
    }

    internal static async Task<string> ExportAsync(string original, string source, string workspace, string extension, CancellationToken token)
    {
        if (ReaderTextPreview.Extensions.Contains(extension))
            return await ReaderTextPreview.ExportAsync(original, source, workspace, extension, token);
        await BookConverterRuntime.EnsureLibreOfficeAsync(null, token);
        var executable = BookConverterRuntime.FindLibreOffice()!;
        var pdf = Path.Combine(workspace, "preview.pdf");
        {
            // Tables/presentations use the same shipped headless office image as
            // the editor's format importer. No Python or LibreOfficeKit loader.
            var profile = Path.Combine(workspace, "cli-profile", "user"); Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "registrymodifications.xcu"),
                "<?xml version='1.0'?><oor:items xmlns:oor='http://openoffice.org/2001/registry'><item oor:path='/org.openoffice.Office.Common/Security/Scripting'><prop oor:name='MacroSecurityLevel' oor:op='fuse'><value>3</value></prop><prop oor:name='DisableMacrosExecution' oor:op='fuse'><value>true</value></prop></item></oor:items>", token);
            var args = new List<string> { "-env:UserInstallation=" + new Uri(Path.GetDirectoryName(profile)!).AbsoluteUri,
                "--headless", "--nologo", "--nodefault", "--norestore", "--nofirststartwizard" };
            if (extension == ".csv")
            {
                var text = await Task.Run(() => ReadText(source), token);
                await File.WriteAllTextAsync(source, text, new UTF8Encoding(true), token);
                var first = text.Split('\n', 2)[0];
                var separator = first.Count(c => c == ';') > first.Count(c => c == ',') ? 59 : 44;
                args.Add("--infilter=Text - txt - csv (StarCalc):" + separator + ",34,76,1");
            }
            args.AddRange(["--convert-to", "pdf", "--outdir", workspace, source]);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await ReaderProcess.RunAsync(executable, args, deadline.Token);
            var result = Path.ChangeExtension(source, ".pdf");
            if (!File.Exists(result) || new FileInfo(result).Length == 0) throw new InvalidDataException("Reader.RenderFailed");
            File.Move(result, pdf);
        }
        return pdf;
    }
}
