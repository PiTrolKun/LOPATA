using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageUtilitySources
{
    public static IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(
        ("png,jpg,jpeg,jpe,webp,gif,bmp,bmp2,bmp3,tif,tiff,tga,avif,heic,heif,jxl,apng,mng," +
         "jp2,j2k,jpc,jng,dds,dpx,exr,hdr,ico,cur,pcx,pnm,pbm,pgm,ppm,pam,pfm,psd,psb,qoi," +
         "sgi,ras,xbm,xpm,wbmp,fits,fit,fts,cin,farbfeld,ff,art,avs,cut,dcx,hrz,mtv,otb,palm," +
         "pct,pict,pdb,pgx,phm,rla,rle,sct,vicar,viff,vips,xv,raw,dng,arw,cr2,cr3,nef,nrw,orf," +
         "raf,rw2,srw,pef,3fr,iiq,erf,kdc,mos,mrw,sr2,srf,x3f").Split(',').Select(x => "." + x),
        StringComparer.OrdinalIgnoreCase);

    public static Task AddAsync(ImageUtilityJob job, IEnumerable<string> sources, bool recursive,
        IProgress<ImageUtilityProgress>? progress = null, CancellationToken token = default)
    {
        // Enumerating large directory trees must not occupy the dispatcher thread.
        return Task.Run(() =>
        {
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                var value = source.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    Add(job, value, Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) is { Length: > 0 } name ? name : uri.Host, "");
                }
                else if (Directory.Exists(value))
                {
                    var root = Path.GetFullPath(value);
                    foreach (var file in Enumerate(root, recursive, token, progress))
                    {
                        var relative = recursive ? Path.GetDirectoryName(Path.GetRelativePath(root, file)) ?? "" : "";
                        Add(job, file, Path.GetFileName(file), relative);
                    }
                }
                else if (File.Exists(value))
                {
                    var fullPath = Path.GetFullPath(value);
                    Add(job, fullPath, Path.GetFileName(fullPath), "");
                }
                else
                {
                    job.Items.Add(new ImageUtilityItem { Source = value, DisplayName = Path.GetFileName(value),
                        Status = ImageUtilityItemStatus.Failed, ErrorKey = "ImageUtility.Error.SourceMissing" });
                }
            }
            progress?.Report(new("ImageUtility.Event.Added", [job.Items.Count]));
        }, token);
    }

    public static string Identity(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "url:" + source;
        try { return "file:" + Path.GetFullPath(source).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return "invalid:" + source; }
    }

    public static void RecheckDuplicates(ImageUtilityJob job)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in job.Items)
        {
            var duplicate = !seen.Add(Identity(item.Source));
            if (duplicate && item.Status != ImageUtilityItemStatus.Completed)
            {
                item.Status = ImageUtilityItemStatus.Duplicate;
                item.ErrorKey = "ImageUtility.Error.Duplicate";
            }
            else if (!duplicate && item.Status == ImageUtilityItemStatus.Duplicate)
            {
                item.Status = ImageUtilityItemStatus.Pending;
                item.ErrorKey = null;
            }
        }
    }

    private static void Add(ImageUtilityJob job, string source, string name, string relative)
    {
        var identity = Identity(source);
        var duplicate = job.Items.Any(x => Identity(x.Source) == identity);
        job.Items.Add(new ImageUtilityItem { Source = source, DisplayName = name, RelativeFolder = relative,
            Status = duplicate ? ImageUtilityItemStatus.Duplicate : ImageUtilityItemStatus.Pending,
            ErrorKey = duplicate ? "ImageUtility.Error.Duplicate" : null });
    }

    private static IEnumerable<string> Enumerate(string root, bool recursive, CancellationToken token,
        IProgress<ImageUtilityProgress>? progress)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = recursive ? Directory.GetDirectories(directory) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                progress?.Report(new("ImageUtility.Error.Directory", [directory, ex.Message], IsError: true));
                continue;
            }
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
                if (SupportedExtensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in directories.OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                // Junctions and symbolic links do not recurse outside the selected tree or loop.
                try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                { progress?.Report(new("ImageUtility.Error.Directory", [child, ex.Message], IsError: true)); }
            }
        }
    }
}
