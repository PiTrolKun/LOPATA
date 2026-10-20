using System.Diagnostics;
using System.IO;
using System.Text;
using SkiaSharp;

namespace AIHub.Services.Books.Smart;

/// <summary>Private inference copy, always PNG. The source document's original image bytes stay untouched.</summary>
internal static class BookSmartImage
{
    internal static async Task<byte[]> NormalizeAsync(byte[] data, string extension, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data); token.ThrowIfCancellationRequested();
        if (data.Length > 128L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ImageBytes");
        if (extension.Trim().TrimStart('.').Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            BookConversionResources.ValidateSvg(data); // No allowed resources: even relative external references are refused.
            // The shared validator walks Descendants(). Also validate the actual root's attributes as a child.
            using var input = new MemoryStream(data);
            var root = BookMarkup.Xml(input).Root!;
            var frame = new System.Xml.Linq.XElement(root.Name, new System.Xml.Linq.XElement(root));
            BookConversionResources.ValidateSvg(Encoding.UTF8.GetBytes(frame.ToString()));
            return await SvgAsync(data, token);
        }
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); BookConversionResources.ValidateImage(data);
            using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidDataException("BookConverter.Error.InvalidImage");
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("BookConverter.Error.InvalidImage");
            token.ThrowIfCancellationRequested();
            if (encoded.Size > 128L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ImageBytes");
            return encoded.ToArray();
        }, token);
    }

    // Calls calibre's ready-made SVG rasterizer, rather than implementing an SVG renderer.
    // Pinned upstream: https://github.com/kovidgoyal/calibre/blob/v9.16.0/src/calibre/ebooks/oeb/transforms/rasterize.py
    private const string SvgScript = """
        import sys
        from pathlib import Path
        from calibre.gui2 import must_use_qt
        from calibre.ebooks.oeb.transforms.rasterize import rasterize_svg
        from qt.core import QSvgRenderer, QByteArray
        must_use_qt()
        data = Path(sys.argv[1]).read_bytes()
        renderer = QSvgRenderer(QByteArray(data))
        if not renderer.isValid():
            raise ValueError('Invalid SVG image')
        size = renderer.defaultSize()
        width, height = size.width(), size.height()
        if width < 1 or height < 1 or width * height > 100000000:
            raise ValueError('SVG image pixel budget exceeded')
        Path(sys.argv[2]).write_bytes(rasterize_svg(data=data))
        """;

    private static async Task<byte[]> SvgAsync(byte[] data, CancellationToken token)
    {
        await BookConverterRuntime.EnsureCalibreAsync(null, token);
        var converter = BookConverterRuntime.FindCalibre() ?? throw new FileNotFoundException("BookConverter.Error.CalibreRequired");
        var executable = Path.Combine(Path.GetDirectoryName(converter)!, "calibre-debug.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("BookConverter.Error.CalibreRequired");
        var work = Path.Combine(Path.GetTempPath(), "LOPATA-BookSmartImage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "source.svg"); var output = Path.Combine(work, "inference.png");
            await File.WriteAllBytesAsync(input, data, token);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "-c", SvgScript, "--", input, output }) start.ArgumentList.Add(argument);
            start.Environment["QT_QPA_PLATFORM"] = "offscreen";
            start.Environment["CALIBRE_CONFIG_DIRECTORY"] = Path.Combine(work, "config");
            start.Environment["CALIBRE_TEMP_DIR"] = work;
            using var process = OwnedProcessRegistry.Shared.Start(start, "Books.Smart.Image");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None); throw;
            }
            var details = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                OwnedProcessRegistry.Log("book_image_normalize_failed", "Books.Smart", detail: details);
                throw new InvalidDataException("BookConverter.Error.InvalidImage");
            }
            if (new FileInfo(output).Length > 128L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ImageBytes");
            var png = await File.ReadAllBytesAsync(output, token); BookConversionResources.ValidateImage(png); return png;
        }
        finally
        {
            // Only the new private directory is removed. No user directory or source path is accepted here.
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
