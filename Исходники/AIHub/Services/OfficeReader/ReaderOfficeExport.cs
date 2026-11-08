using System.IO;
using System.Net;
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
        await BookConverterRuntime.EnsureLibreOfficeAsync(null, token);
        var executable = BookConverterRuntime.FindLibreOffice()!;
        var program = Path.GetDirectoryName(executable)!;
        var pdf = Path.Combine(workspace, "preview.pdf");
        if (extension is ".md" or ".markdown" or ".html" or ".htm")
        {
            source = Path.Combine(workspace, "source.html");
            await File.WriteAllTextAsync(source, await Task.Run(() => ReaderMarkup.Prepare(original, token), token), token);
        }
        else if (extension == ".txt")
        {
            var text = await Task.Run(() => ReadText(source), token);
            source = Path.Combine(workspace, "source.html");
            await File.WriteAllTextAsync(source, "<!doctype html><meta charset='utf-8'><pre style='white-space:pre-wrap;overflow-wrap:anywhere'>" + WebUtility.HtmlEncode(text) + "</pre>", token);
        }
        if (Path.GetExtension(source).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension is ".odt" or ".xlsx" or ".xlsm" or ".xls" or ".ods" or ".csv" or ".pptx" or ".ppt" or ".odp")
        {
            // Windows LOK Calc, HTML and ODT block during load on supplied fixtures.
            // Select the official headless loader up front, without a retry.
            var profile = Path.Combine(workspace, "cli-profile", "user"); Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "registrymodifications.xcu"),
                "<?xml version='1.0'?><oor:items xmlns:oor='http://openoffice.org/2001/registry'><item oor:path='/org.openoffice.Office.Common/Security/Scripting'><prop oor:name='MacroSecurityLevel' oor:op='fuse'><value>3</value></prop><prop oor:name='DisableMacrosExecution' oor:op='fuse'><value>true</value></prop></item></oor:items>", token);
            var args = new List<string> { "-env:UserInstallation=" + new Uri(Path.GetDirectoryName(profile)!).AbsoluteUri,
                "--headless", "--nologo", "--nodefault", "--nofirststartwizard" };
            if (extension == ".csv")
            {
                var text = await Task.Run(() => ReadText(source), token);
                await File.WriteAllTextAsync(source, text, new UTF8Encoding(true), token);
                var first = text.Split('\n', 2)[0];
                var separator = first.Count(c => c == ';') > first.Count(c => c == ',') ? 59 : 44;
                args.Add("--infilter=Text - txt - csv (StarCalc):" + separator + ",34,76,1");
            }
            args.AddRange(["--convert-to", "pdf", "--outdir", workspace, source]);
            await ReaderProcess.RunAsync(executable, args, token);
            var result = Path.ChangeExtension(source, ".pdf");
            if (!File.Exists(result)) throw new InvalidDataException("Reader.RenderFailed");
            File.Move(result, pdf);
        }
        else
        {
            var python = Path.Combine(program, "python.exe");
            if (!File.Exists(python) || !File.Exists(Path.Combine(program, "mergedlo.dll")))
                throw new FileNotFoundException("Reader.OfficeKitRequired");
            if (!await ReaderOfficeSession.TryExportAsync(program, source, pdf, token))
                await ReaderProcess.RunAsync(python, [Path.Combine(AppContext.BaseDirectory, "Tools", "office_reader_export.py"), program, source, pdf], token);
        }
        return pdf;
    }
}
