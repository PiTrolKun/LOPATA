using System.Runtime.InteropServices;

namespace Lopata.Collection;

public sealed record CollectionIndexResult(IReadOnlyList<string> Paths, string? Error, bool Limited);

public static class CollectionWindowsSearch
{
    private const string OriginCondition = "(System.ApplicationName = 'LOPATA' OR System.ApplicationName LIKE 'LOPATA %' "
        + "OR System.Media.EncodedBy = 'LOPATA' OR System.Media.EncodedBy LIKE 'LOPATA %' OR System.Keywords = 'LOPATA-COLLECTION-V1')";

    public static CollectionIndexResult Query(CollectionConfig config, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? error = null; var limited = false;
        var projected = Execute(OriginCondition, token, deadline.Token);
        paths.UnionWith(projected.Paths); error = projected.Error; limited |= projected.Limited;
        // Windows often indexes only the path of these containers, not their private provenance.
        // Ask its existing index for candidates in explicitly permitted locations, then parse origin.
        var scopes = config.SearchRoots.Length > 0 ? config.SearchRoots : config.Roots;
        if (scopes.Length > 0 && error is null)
        {
            var scope = "(" + string.Join(" OR ", scopes.Where(p => config.AllowNetwork || !p.StartsWith(@"\\", StringComparison.Ordinal))
                .Select(p => "SCOPE='" + FileUrl(p).Replace("'", "''", StringComparison.Ordinal) + "'")) + ")";
            if (scope != "()")
            {
                const string extensions = "(System.FileExtension = '.flac' OR System.FileExtension = '.opus' OR System.FileExtension = '.ogg' "
                    + "OR System.FileExtension = '.wav' OR System.FileExtension = '.mp3' OR System.FileExtension = '.m4a' OR System.FileExtension = '.docx' "
                    + "OR System.FileExtension = '.xlsx' OR System.FileExtension = '.pptx' OR System.FileExtension = '.odt' "
                    + "OR System.FileExtension = '.ods' OR System.FileExtension = '.odp' OR System.FileExtension = '.epub' "
                    + "OR System.FileExtension = '.fb2' OR System.FileExtension = '.cbz' OR System.FileExtension = '.svg' "
                    + "OR System.FileExtension = '.lopatadoxai' OR System.FileExtension = '.lopatavideo' OR System.FileExtension = '.lopatasvg' "
                    + "OR System.FileExtension = '.txt' OR System.FileExtension = '.md' OR System.FileExtension = '.pdf' "
                    + "OR System.FileExtension = '.html' OR System.FileExtension = '.mp4' OR System.FileExtension = '.mov' "
                    + "OR System.FileExtension = '.rtf' OR System.FileExtension = '.zip' OR System.FileExtension = '.json' "
                    + "OR System.FileExtension = '.csv' OR System.FileExtension = '.lopataaivid' "
                    + "OR System.FileExtension = '.mid' OR System.FileExtension = '.midi' OR System.FileExtension = '.gif' "
                    + "OR System.FileExtension = '.mkv' OR System.FileExtension = '.webm' "
                    + "OR System.FileExtension = '.png' OR System.FileExtension = '.jpg' OR System.FileExtension = '.jpeg' "
                    + "OR System.FileExtension = '.webp' OR System.FileExtension = '.avif' OR System.FileExtension = '.jxl' "
                    + "OR System.FileExtension = '.tif' OR System.FileExtension = '.tiff' OR System.FileExtension = '.bmp' "
                    + "OR System.FileExtension = '.tga' OR System.FileExtension = '.qoi' OR System.FileExtension = '.jp2' "
                    + "OR System.FileExtension = '.j2k' OR System.FileExtension = '.jng' OR System.FileExtension = '.dds' "
                    + "OR System.FileExtension = '.exr' OR System.FileExtension = '.hdr' OR System.FileExtension = '.dpx' "
                    + "OR System.FileExtension = '.cin' OR System.FileExtension = '.ico' OR System.FileExtension = '.pcx' "
                    + "OR System.FileExtension = '.psd' OR System.FileExtension = '.psb' OR System.FileExtension = '.mng' "
                    + "OR System.FileExtension = '.pbm' OR System.FileExtension = '.pgm' OR System.FileExtension = '.ppm' "
                    + "OR System.FileExtension = '.pam' OR System.FileExtension = '.pfm' OR System.FileExtension = '.phm' "
                    + "OR System.FileExtension = '.pgx' OR System.FileExtension = '.sgi' OR System.FileExtension = '.ras' "
                    + "OR System.FileExtension = '.xbm' OR System.FileExtension = '.xpm' OR System.FileExtension = '.wbmp' "
                    + "OR System.FileExtension = '.fits' OR System.FileExtension = '.farbfeld' OR System.FileExtension = '.art' "
                    + "OR System.FileExtension = '.avs' OR System.FileExtension = '.dcx' OR System.FileExtension = '.mtv' "
                    + "OR System.FileExtension = '.otb' OR System.FileExtension = '.palm' OR System.FileExtension = '.pdb' "
                    + "OR System.FileExtension = '.vicar' OR System.FileExtension = '.viff' OR System.FileExtension = '.vips')";
                var fallback = Execute(scope + " AND " + extensions, token, deadline.Token);
                paths.UnionWith(fallback.Paths); error = fallback.Error; limited |= fallback.Limited;
            }
        }
        if (paths.Count > CollectionSnapshot.MaxEntries) limited = true;
        return new(paths.Take(CollectionSnapshot.MaxEntries).ToArray(), error, limited);
    }

    private static CollectionIndexResult Execute(string condition, CancellationToken token, CancellationToken deadline)
    {
        var paths = new List<string>(); object? connectionObject = null; object? recordsObject = null;
        try
        {
            deadline.ThrowIfCancellationRequested();
            var type = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("Windows Search provider is unavailable.");
            connectionObject = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Search provider could not be created.");
            dynamic connection = connectionObject;
            connection.ConnectionTimeout = 5;
            connection.CommandTimeout = 10;
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            deadline.ThrowIfCancellationRequested();
            recordsObject = connection.Execute($"SELECT TOP {CollectionSnapshot.MaxEntries + 1} System.ItemUrl FROM SystemIndex WHERE {condition}");
            dynamic records = recordsObject;
            while (!(bool)records.EOF && paths.Count <= CollectionSnapshot.MaxEntries)
            {
                deadline.ThrowIfCancellationRequested();
                string value = Convert.ToString(records.Fields.Item(0).Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) paths.Add(uri.LocalPath);
                records.MoveNext();
            }
            var limited = paths.Count > CollectionSnapshot.MaxEntries;
            return new(paths.Take(CollectionSnapshot.MaxEntries).ToArray(), null, limited);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(paths, "windows-search:timeout", true);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new(paths, "windows-search:" + error.GetType().Name, false);
        }
        finally
        {
            if (recordsObject is not null)
            {
                try { ((dynamic)recordsObject).Close(); } catch (COMException) { }
                Marshal.FinalReleaseComObject(recordsObject);
            }
            if (connectionObject is not null)
            {
                try { ((dynamic)connectionObject).Close(); } catch (COMException) { }
                Marshal.FinalReleaseComObject(connectionObject);
            }
        }
    }

    private static string FileUrl(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return new Uri(path).AbsoluteUri.TrimEnd('/');
        return "file:" + Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
    }
}
