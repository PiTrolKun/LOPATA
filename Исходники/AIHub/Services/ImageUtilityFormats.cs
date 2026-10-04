using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public static partial class ImageUtilityFormats
{
    // User-facing raster formats only: omit raw channel dumps, script/pseudo coders,
    // document/vector wrappers, aliases and formats requiring multiple output sidecars.
    public static IReadOnlyList<ImageUtilityFormat> All { get; } = new[]
    {
        F("png", true), F("jpeg", false, quality: true, extension: "jpg"), F("webp", true, true, true),
        F("avif", true, quality: true), F("jxl", true, quality: true), F("tiff", true, quality: true, extension: "tif"),
        F("gif", true, true), F("bmp", false), F("tga", true), F("qoi", true),
        F("jp2", true, quality: true), F("j2k", false, quality: true), F("jng", true, quality: true),
        F("dds", true), F("exr", true), F("hdr", false), F("dpx", true), F("cin", false),
        F("ico", true), F("pcx", false), F("psd", true), F("psb", true),
        F("mng", true, true), F("pbm", false), F("pgm", false), F("ppm", false), F("pam", true),
        F("pfm", false), F("phm", false), F("pgx", false), F("sgi", true), F("ras", true),
        F("xbm", false), F("xpm", true), F("wbmp", false), F("fits", false), F("farbfeld", true),
        F("art", false), F("avs", true), F("dcx", false), F("mtv", false),
        F("otb", false), F("palm", false), F("pdb", false), F("vicar", false),
        F("viff", true), F("vips", true)
    };

    public static ImageUtilityFormat Get(string id) => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        || x.Extension.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ImageUtilityException("ImageUtility.Error.Format");

    public static IReadOnlyList<ImageUtilityFormat> FromRuntimeList(string output)
    {
        var supported = FormatPattern().Matches(output).Where(x => x.Groups[2].Value.Contains('w'))
            .Select(x => x.Groups[1].Value.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(x => supported.Contains(x.Id)).ToArray();
    }

    private static ImageUtilityFormat F(string id, bool alpha, bool animation = false, bool quality = false, string? extension = null)
        => new(id, extension ?? id, alpha, animation, quality);

    [GeneratedRegex(@"^\s*([A-Z0-9]+)\*?\s+([r-][w-][+-])\s", RegexOptions.Multiline)]
    private static partial Regex FormatPattern();
}
