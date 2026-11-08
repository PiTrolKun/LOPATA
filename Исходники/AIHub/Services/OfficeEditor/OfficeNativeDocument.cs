using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal sealed class OfficeNativePage(Func<string> title, IReadOnlyList<OfficeNativeBlock> blocks)
{
    internal string Title => title();
    internal IReadOnlyList<OfficeNativeBlock> Blocks { get; } = blocks;
}

internal sealed class OfficeNativeBlock(Func<string> read, Action<string> write, Func<bool>? bold = null,
    Action<bool>? setBold = null, Func<bool>? italic = null, Action<bool>? setItalic = null,
    Func<double?>? fontSize = null, Action<double>? setFontSize = null, bool allowLineBreaks = true)
{
    internal string Text => read();
    internal bool AllowLineBreaks => allowLineBreaks;
    internal bool CanBold => setBold != null;
    internal bool CanItalic => setItalic != null;
    internal bool CanSetFontSize => setFontSize != null;
    internal bool Bold => bold?.Invoke() == true;
    internal bool Italic => italic?.Invoke() == true;
    internal double? FontSize => fontSize?.Invoke();
    internal void SetText(string value)
    {
        value = XmlConvert.VerifyXmlChars(value.ReplaceLineEndings("\n"));
        if (!allowLineBreaks && value.Contains('\n')) throw new ArgumentException("OfficeEditor.NativeInvalidText", nameof(value));
        if (Text != value) write(value);
    }
    internal void SetBold(bool value) => setBold?.Invoke(value);
    internal void SetItalic(bool value) => setItalic?.Invoke(value);
    internal void SetFontSize(double value)
    {
        if (!double.IsFinite(value) || value < 1 || value > 400) throw new ArgumentOutOfRangeException(nameof(value));
        setFontSize?.Invoke(value);
    }
}

internal sealed class OfficeNativeDocument
{
    private readonly OfficeNativePackage _package;
    internal string Extension { get; }
    internal bool CanAppend { get; }
    internal IReadOnlyList<OfficeNativePage> Pages { get; private set; }
    internal void RefreshSlides() => Pages = OfficeSlideFormat.Read(_package, Extension);
    internal bool ReadOnly => ReadOnlyReason != null;
    internal string? ReadOnlyReason { get; }
    internal bool HasEditableText => !ReadOnly && Pages.Any(p => p.Blocks.Count != 0);
    internal bool CanEdit => !ReadOnly && (CanAppend || HasEditableText ||
        Extension is ".pptx" or ".odp" && Enumerable.Range(0, Pages.Count).Any(i => SlideCanvas(i) is { Objects.Count: > 0 }));
    internal OfficeSlideCanvasModel? SlideCanvas(int index)
    {
        if (ReadOnly || index < 0 || index >= Pages.Count) return null;
        try
        {
            return Extension switch
            {
                ".pptx" => OfficeSlideFormat.ReadPptxObjects(_package, index),
                ".odp" => OfficeSlideFormat.ReadOdpObjects(_package, index),
                _ => null
            };
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or FormatException or InvalidOperationException)
        {
            // Some decks render successfully with incomplete layout metadata.
            // Keep reading and text editing available on that slide.
            return null;
        }
    }
    internal static bool Supports(string? extension) => extension?.ToLowerInvariant() is ".pptx" or ".odp" or ".epub" or ".fb2";

    private OfficeNativeDocument(OfficeNativePackage package, string extension, IReadOnlyList<OfficeNativePage> pages, string? reason)
    {
        _package = package; Extension = extension; Pages = pages; ReadOnlyReason = reason;
        CanAppend = reason == null && (extension is ".pptx" or ".odp" ? OfficeSlideFormat.CanAppend(package, extension) :
            OfficeBookFormat.CanAppend(package, extension));
    }

    internal static OfficeNativeDocument Open(byte[] bytes, string extension)
    {
        extension = extension.ToLowerInvariant();
        if (!Supports(extension)) throw new InvalidDataException("OfficeEditor.Unsupported");
        var package = new OfficeNativePackage(bytes, extension == ".fb2");
        try
        {
            if (IsProtected(package, extension)) return new(package, extension, [], "OfficeEditor.NativeProtected");
            var pages = extension is ".pptx" or ".odp" ? OfficeSlideFormat.Read(package, extension) : OfficeBookFormat.Read(package, extension);
            return new(package, extension, pages, null);
        }
        catch (Exception error) when (error is XmlException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("OfficeEditor.InvalidPackage", error); }
        finally { package.EndRead(); }
    }

    private static bool IsProtected(OfficeNativePackage package, string extension)
    {
        if (package.Entries.Any(p => p.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("signatures.xml", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (extension == ".epub" || extension == ".fb2") return OfficeBookFormat.IsProtected(package, extension);
        if (extension == ".pptx")
            return package.Entries.Any(p => p.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase)) ||
                OfficeSlideFormat.IsProtected(package, extension);
        XNamespace manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        return package.Entries.Contains("META-INF/manifest.xml") &&
            package.Xml("META-INF/manifest.xml").Descendants(manifest + "encryption-data").Any();
    }

    internal static byte[] Create(string extension, IReadOnlyList<OfficeSheetSection> sections) => extension.ToLowerInvariant() switch
    {
        ".epub" or ".fb2" => OfficeBookFormat.Create(extension.ToLowerInvariant(), sections),
        ".pptx" or ".odp" => OfficeSlideFormat.Create(extension.ToLowerInvariant(), sections),
        _ => throw new InvalidDataException("OfficeEditor.Unsupported")
    };

    internal byte[] Save() => _package.Snapshot()();
    internal Task<byte[]> SaveAsync(CancellationToken token) => Task.Run(_package.Snapshot(), token);

    internal Task<OfficeNativeDocument> AppendAsync(string title, CancellationToken token)
    {
        if (!CanAppend) throw new InvalidOperationException("OfficeEditor.Unsupported");
        var snapshot = _package.Snapshot();
        // Stage the new chapter/slide and its relationships together. The live
        // document remains usable if assembling the new package fails.
        return Task.Run(() =>
        {
            var package = new OfficeNativePackage(snapshot(), Extension == ".fb2");
            try
            {
                IReadOnlyList<OfficeNativePage> pages;
                if (Extension is ".pptx" or ".odp")
                {
                    OfficeSlideFormat.Append(package, Extension, title);
                    pages = OfficeSlideFormat.Read(package, Extension);
                }
                else
                {
                    OfficeBookFormat.Append(package, Extension, title);
                    pages = OfficeBookFormat.Read(package, Extension);
                }
                token.ThrowIfCancellationRequested();
                return new OfficeNativeDocument(package, Extension, pages, null);
            }
            finally { package.EndRead(); }
        }, token);
    }
}
