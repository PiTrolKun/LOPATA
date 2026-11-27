#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lopata.VisualConstructor;

public enum NumberOperation
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
    Kelvin
}

public static class NumberActions
{
    public static decimal? Parse(string? text, CultureInfo culture)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        return decimal.TryParse(text, styles, culture, out decimal number) ? number : null;
    }

    public static string Format(decimal value, int decimalPlaces, CultureInfo culture)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimalPlaces, 28);
        return value.ToString("F" + decimalPlaces.ToString(CultureInfo.InvariantCulture), culture);
    }

    public static decimal Calculate(decimal left, NumberOperation operation, decimal right)
    {
        return operation switch
        {
            NumberOperation.Add => left + right,
            NumberOperation.Subtract => left - right,
            NumberOperation.Multiply => left * right,
            NumberOperation.Divide => left / right,
            NumberOperation.Remainder => left % right,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    public static decimal Percent(decimal amount, decimal percent)
    {
        return amount * (percent / 100m);
    }

    public static decimal Round(decimal value, int decimalPlaces, MidpointRounding rounding)
    {
        return decimal.Round(value, decimalPlaces, rounding);
    }

    public static decimal Clamp(decimal value, decimal minimum, decimal maximum)
    {
        return Math.Clamp(value, minimum, maximum);
    }

    public static decimal ConvertTemperature(decimal value, TemperatureUnit from, TemperatureUnit to)
    {
        if (!Enum.IsDefined(from))
            throw new ArgumentOutOfRangeException(nameof(from));
        if (!Enum.IsDefined(to))
            throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to)
            return value;

        decimal celsius = from switch
        {
            TemperatureUnit.Fahrenheit => (value - 32m) * 5m / 9m,
            TemperatureUnit.Kelvin => value - 273.15m,
            _ => value
        };
        return to switch
        {
            TemperatureUnit.Fahrenheit => celsius * 9m / 5m + 32m,
            TemperatureUnit.Kelvin => celsius + 273.15m,
            _ => celsius
        };
    }
}

public static class TextActions
{
    public static bool IsBlank(string? text)
    {
        return string.IsNullOrWhiteSpace(text);
    }

    public static string Trim(string text)
    {
        return text.Trim();
    }

    public static bool Contains(string text, string query, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        return text.Contains(query, comparison);
    }

    public static string Replace(string text, string oldText, string newText,
        StringComparison comparison = StringComparison.Ordinal)
    {
        return text.Replace(oldText, newText, comparison);
    }

    public static string[] Split(string text, string separator, bool removeEmpty = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(separator);
        var options = removeEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None;
        return text.Split(separator, options);
    }

    public static string Join(IEnumerable<string> values, string separator)
    {
        return string.Join(separator, values);
    }

    public static int Length(string text)
    {
        return new StringInfo(text).LengthInTextElements;
    }

    public static string Slice(string text, int start, int count)
    {
        var elements = new StringInfo(text);
        int length = elements.LengthInTextElements;
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, length);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, length - start);
        return count == 0 ? string.Empty : elements.SubstringByTextElements(start, count);
    }

    public static string ChangeCase(string text, bool upperCase, CultureInfo culture)
    {
        return upperCase ? text.ToUpper(culture) : text.ToLower(culture);
    }

    public static bool MatchesPattern(string text, string pattern, bool ignoreCase = false)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(text, pattern, options, TimeSpan.FromSeconds(1));
    }
}

public static class DateActions
{
    public static DateTimeOffset Now()
    {
        return DateTimeOffset.Now;
    }

    public static DateOnly? ParseDate(string? text, CultureInfo culture)
    {
        return DateOnly.TryParse(text, culture, DateTimeStyles.None, out DateOnly date) ? date : null;
    }

    public static string FormatDate(DateOnly date, string format, CultureInfo culture)
    {
        return date.ToString(format, culture);
    }

    public static DateOnly AddDays(DateOnly date, int days)
    {
        return date.AddDays(days);
    }

    public static TimeSpan Difference(DateTimeOffset start, DateTimeOffset end)
    {
        return end - start;
    }

    public static DateTimeOffset InTimeZone(DateTimeOffset moment, string timeZoneId)
    {
        return TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
    }
}

public static class ListActions
{
    public static string[] FilterText(IEnumerable<string> values, string search,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        return values.Where(value => value.Contains(search, comparison)).ToArray();
    }

    public static string[] SortText(IEnumerable<string> values, CultureInfo culture, bool descending = false)
    {
        var comparer = StringComparer.Create(culture, ignoreCase: true);
        return descending
            ? values.OrderByDescending(value => value, comparer).ToArray()
            : values.OrderBy(value => value, comparer).ToArray();
    }

    public static string[] UniqueText(IEnumerable<string> values, bool ignoreCase = true)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        return values.Where(seen.Add).ToArray();
    }

    public static T[] TakeRange<T>(IEnumerable<T> values, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return values.Skip(offset).Take(count).ToArray();
    }

    public static decimal Sum(IEnumerable<decimal> values)
    {
        return values.Sum();
    }

    public static decimal? Average(IEnumerable<decimal> values)
    {
        return values.Select(value => (decimal?)value).Average();
    }
}

public static class JsonActions
{
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement? ReadProperty(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property)
            ? property.Clone()
            : null;
    }

    public static JsonElement? ReadItem(JsonElement value, int index)
    {
        if (value.ValueKind != JsonValueKind.Array || index < 0 || index >= value.GetArrayLength())
            return null;
        return value[index].Clone();
    }

    public static string Format(JsonElement value, bool indent = false)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = indent });
    }
}

public sealed record FileDetails(string Path, long Bytes, DateTime LastWriteTimeUtc);
public sealed record ZipEntryDetails(string Name, long Bytes, long CompressedBytes);

public static class FileActions
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static FileDetails GetDetails(string path)
    {
        var info = new FileInfo(path);
        return new FileDetails(info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    public static string[] ListFiles(string directory, string pattern = "*")
    {
        if (Path.IsPathRooted(pattern) || pattern.Contains(Path.DirectorySeparatorChar) ||
            pattern.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Use a file-name pattern within the selected directory.", nameof(pattern));
        }

        return Directory.EnumerateFiles(Path.GetFullPath(directory), pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string CreateDirectory(string path)
    {
        return Directory.CreateDirectory(path).FullName;
    }

    public static async Task<string> ReadTextAsync(
        string path,
        Encoding? encoding = null,
        long? maximumBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream stream = OpenRead(path);
        if (maximumBytes is long limit && stream.Length > limit)
            throw new IOException($"The input exceeds the configured limit of {limit} bytes.");

        using var reader = new StreamReader(stream, encoding ?? Utf8, detectEncodingFromByteOrderMarks: encoding is null);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteTextAsync(
        string destination,
        string text,
        Encoding? encoding = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        return WriteOutputAsync(destination, overwrite, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, encoding ?? Utf8, leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public static Task CopyAsync(
        string source, string destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        string sourcePath = Path.GetFullPath(source);
        string destinationPath = Path.GetFullPath(destination);
        if (StringComparer.OrdinalIgnoreCase.Equals(sourcePath, destinationPath))
            throw new ArgumentException("Source and destination refer to the same path.", nameof(destination));

        return WriteOutputAsync(destinationPath, overwrite, async (output, token) =>
        {
            await using FileStream input = OpenRead(sourcePath);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream stream = OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static ZipEntryDetails[] ListZipEntries(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry =>
            new ZipEntryDetails(entry.FullName, entry.Length, entry.CompressedLength)).ToArray();
    }

    public static Task CreateZipAsync(
        IEnumerable<string> sourceFiles,
        string destination,
        CompressionLevel compression = CompressionLevel.Optimal,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        string[] sources = sourceFiles.Select(Path.GetFullPath).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StringComparer.OrdinalIgnoreCase.Equals(source, target))
                throw new ArgumentException("The archive path also appears in the input files.", nameof(destination));
            string name = Path.GetFileName(source);
            if (name.Length == 0 || !names.Add(name))
                throw new ArgumentException($"The archive entry name is empty or repeated: {name}", nameof(sourceFiles));
        }

        return WriteOutputAsync(target, overwrite, async (stream, token) =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                await using FileStream input = OpenRead(source);
                ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(source), compression);
                await using Stream output = entry.Open();
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static async Task WriteOutputAsync(
        string destination,
        bool overwrite,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("Select a file path.", nameof(destination));
        if (!overwrite && File.Exists(target))
            throw new IOException("A file with that name already exists.");

        string stagingPath = Path.Combine(directory, $".lopata-{Guid.NewGuid():N}.tmp");
        bool created = false;

        try
        {
            await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, target, overwrite);
        }
        catch
        {
            if (created)
            {
                // Preserve the original error if Windows still has the staging file open.
                try { File.Delete(stagingPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}

public static class TaskActions
{
    public static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        return Task.Delay(delay, cancellationToken);
    }
}

public static class ControlActions
{
    public static string ReadText(TextBox field)
    {
        return field.Text;
    }

    public static void WriteText(TextBox field, string value)
    {
        field.SetCurrentValue(TextBox.TextProperty, value);
    }

    public static void ShowText(TextBlock label, string value)
    {
        label.SetCurrentValue(TextBlock.TextProperty, value);
    }

    public static void SetCaption(ContentControl control, string value)
    {
        control.SetCurrentValue(ContentControl.ContentProperty, value);
    }

    public static object? ReadSelection(Selector list)
    {
        return list.SelectedValue;
    }

    public static void SelectIndex(Selector list, int index)
    {
        if (index < -1 || index >= list.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        list.SetCurrentValue(Selector.SelectedIndexProperty, index);
    }

    public static bool? ReadCheck(ToggleButton checkBox)
    {
        return checkBox.IsChecked;
    }

    public static void SetCheck(ToggleButton checkBox, bool? value)
    {
        checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, value);
    }

    public static double ReadRange(RangeBase control)
    {
        return control.Value;
    }

    public static double SetRange(RangeBase control, double value)
    {
        control.SetCurrentValue(RangeBase.ValueProperty, value);
        return control.Value;
    }

    public static void SetVisibility(UIElement element, bool visible, bool keepSpace = false)
    {
        Visibility visibility = visible ? Visibility.Visible : keepSpace ? Visibility.Hidden : Visibility.Collapsed;
        element.SetCurrentValue(UIElement.VisibilityProperty, visibility);
    }

    public static void SetEnabled(UIElement element, bool enabled)
    {
        element.SetCurrentValue(UIElement.IsEnabledProperty, enabled);
    }

    public static bool Focus(UIElement element)
    {
        return element.Focus();
    }
}

public sealed record WindowSettings(
    string Title,
    double Width,
    double Height,
    double MinimumWidth = 0,
    double MinimumHeight = 0,
    double? MaximumWidth = null,
    double? MaximumHeight = null,
    bool Resizable = true,
    bool ShowInTaskbar = true,
    bool StartMaximized = false);

public static class WindowActions
{
    public static void ApplySettings(Window window, WindowSettings settings)
    {
        window.VerifyAccess();
        ValidateSize(settings.Width, settings.MinimumWidth, settings.MaximumWidth, nameof(settings.Width));
        ValidateSize(settings.Height, settings.MinimumHeight, settings.MaximumHeight, nameof(settings.Height));
        if (!settings.Resizable && settings.StartMaximized)
            throw new ArgumentException("Maximized startup needs a resizable window.", nameof(settings));

        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.SizeToContent = SizeToContent.Manual;
        window.Title = settings.Title;
        window.MinWidth = settings.MinimumWidth;
        window.MinHeight = settings.MinimumHeight;
        window.MaxWidth = settings.MaximumWidth ?? double.PositiveInfinity;
        window.MaxHeight = settings.MaximumHeight ?? double.PositiveInfinity;
        window.Width = settings.Width;
        window.Height = settings.Height;
        window.ResizeMode = settings.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        window.ShowInTaskbar = settings.ShowInTaskbar;
        window.WindowState = settings.StartMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    public static void Open(Window window, Window? owner = null)
    {
        if (owner is not null)
            window.Owner = owner;
        window.Show();
    }

    public static bool? OpenDialog(Window window, Window? owner = null)
    {
        if (owner is not null)
            window.Owner = owner;
        return window.ShowDialog();
    }

    public static void RequestClose(Window window)
    {
        window.Close();
    }

    public static bool Activate(Window window)
    {
        return window.Activate();
    }

    public static Size ReadContentSize(FrameworkElement content)
    {
        return new Size(content.ActualWidth, content.ActualHeight);
    }

    private static void ValidateSize(double initial, double minimum, double? maximum, string name)
    {
        if (!double.IsFinite(initial) || initial <= 0 || !double.IsFinite(minimum) || minimum < 0 ||
            minimum > initial || maximum is double upper && (!double.IsFinite(upper) || upper < initial))
        {
            throw new ArgumentOutOfRangeException(name, "Choose a positive initial size within its limits.");
        }
    }
}

public static class DesktopActions
{
    public static string[]? ChooseFiles(Window? owner, string? filter = null, bool multiple = false)
    {
        var dialog = new OpenFileDialog { Multiselect = multiple, CheckFileExists = true, Filter = filter ?? "" };
        return Accepted(dialog, owner) ? dialog.FileNames : null;
    }

    public static string? ChooseFolder(Window? owner)
    {
        var dialog = new OpenFolderDialog();
        return Accepted(dialog, owner) ? dialog.FolderName : null;
    }

    public static string? ChooseSavePath(Window? owner, string? filter = null, string? suggestedName = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter ?? "",
            FileName = suggestedName ?? "",
            OverwritePrompt = true
        };
        return Accepted(dialog, owner) ? dialog.FileName : null;
    }

    public static void ShowMessage(Window? owner, string message, string title)
    {
        if (owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static bool Confirm(Window? owner, string message, string title)
    {
        MessageBoxResult result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void CopyText(string text)
    {
        Clipboard.SetText(text);
    }

    public static string? ReadClipboardText()
    {
        return Clipboard.GetDataObject()?.GetData(DataFormats.UnicodeText) as string;
    }

    private static bool Accepted(CommonDialog dialog, Window? owner)
    {
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
    }
}

public static class EventActions
{
    public static Action OnClick(ButtonBase control, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        control.VerifyAccess();
        RoutedEventHandler handler = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, control))
                callback();
        };
        control.Click += handler;
        return () => control.Click -= handler;
    }

    public static Action OnSelectionChange(Selector control, Action<object?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        control.VerifyAccess();
        SelectionChangedEventHandler handler = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, control))
                callback(control.SelectedValue);
        };
        control.SelectionChanged += handler;
        return () => control.SelectionChanged -= handler;
    }

    public static Action OnTextChange(TextBox control, Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        control.VerifyAccess();
        TextChangedEventHandler handler = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, control))
                callback(control.Text);
        };
        control.TextChanged += handler;
        return () => control.TextChanged -= handler;
    }

    public static Action OnResize(FrameworkElement element, Action<Size> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        element.VerifyAccess();
        SizeChangedEventHandler handler = (_, args) => callback(args.NewSize);
        element.SizeChanged += handler;
        return () => element.SizeChanged -= handler;
    }
}

public static class ImageActions
{
    public static BitmapSource Load(string path, int? decodeWidth = null)
    {
        if (decodeWidth is <= 0)
            throw new ArgumentOutOfRangeException(nameof(decodeWidth));

        using FileStream stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (decodeWidth is int width)
            bitmap.DecodePixelWidth = width;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource Crop(BitmapSource image, Int32Rect rectangle)
    {
        var cropped = new CroppedBitmap(image, rectangle);
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource Fit(BitmapSource image, int maximumWidth, int maximumHeight, bool enlarge = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        double scale = Math.Min((double)maximumWidth / image.PixelWidth, (double)maximumHeight / image.PixelHeight);
        if (!enlarge)
            scale = Math.Min(1d, scale);

        var resized = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    public static Task SavePngAsync(
        BitmapSource image, string destination, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource source = image.IsFrozen ? image : image.CloneCurrentValue();
        if (!source.IsFrozen)
            source.Freeze();

        return FileActions.WriteOutputAsync(destination, overwrite, (output, token) => Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(output);
        }, token), cancellationToken);
    }
}
