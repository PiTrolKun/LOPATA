using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services;
using AngleSharp.Html.Parser;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Microsoft.Data.Sqlite;
using MimeKit;
using SharpCompress.Archives;
using UglyToad.PdfPig;
using YamlDotNet.Serialization;

namespace AIHub;

public partial class FileViewerWindow : Window
{
    private const long MaximumTextBytes = 16L * 1024 * 1024;
    private const int MaximumTableRows = 10_000;
    private readonly string _path;
    private readonly Func<string, string> _localize;

    public FileViewerWindow(
        string path,
        bool isDarkTheme,
        LocalizationService localization)
        : this(path, isDarkTheme, localization.T) { }

    public FileViewerWindow(string path, bool isDarkTheme, Func<string, string> localize, bool originalPdfPages = false)
    {
        _path = Path.GetFullPath(path);
        _localize = localize;
        InitializeComponent();
        Title = T("FileViewer.WindowTitle");
        OpenExternalButton.Content = T("FileViewer.OpenExternal");
        ShowInExplorerButton.Content = T("FileViewer.ShowInExplorer");
        MediaPlayButton.Content = T("FileViewer.Play");
        MediaPauseButton.Content = T("FileViewer.Pause");
        MediaStopButton.Content = T("FileViewer.Stop");
        FileNameText.Text = Path.GetFileName(_path);
        var info = new FileInfo(_path);
        FileDetailsText.Text = $"{info.FullName}  |  {FormatBytes(info.Length)}  |  {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        ApplyTheme(isDarkTheme);
        Loaded += async (_, _) =>
        {
            if (originalPdfPages && Path.GetExtension(_path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) await ShowBookPdfAsync();
            else await LoadFileAsync();
        };
        Closed += (_, _) => _bookPdfLifetime.Cancel();
    }

    public void ApplyTheme(bool dark)
    {
        var background = Brush(dark ? "#111827" : "#F3F3F3");
        var panel = Brush(dark ? "#172033" : "#FFFFFF");
        var border = Brush(dark ? "#2D374B" : "#DADDE3");
        var primary = Brush(dark ? "#F8FAFC" : "#1F1F1F");
        var secondary = Brush(dark ? "#AAB4C4" : "#5D6470");
        RootGrid.Background = background;
        HeaderBorder.Background = panel;
        HeaderBorder.BorderBrush = border;
        StatusBorder.Background = panel;
        StatusBorder.BorderBrush = border;
        FileNameText.Foreground = primary;
        FileDetailsText.Foreground = secondary;
        StatusText.Foreground = secondary;
        TextViewer.Background = panel;
        TextViewer.Foreground = primary;
        TextViewer.BorderBrush = border;
        TableViewer.Background = panel;
        TableViewer.Foreground = primary;
        TableViewer.BorderBrush = border;
    }

    private async Task LoadFileAsync()
    {
        try
        {
            StatusText.Text = T("FileViewer.Reading");
            var extension = Path.GetExtension(_path).ToLowerInvariant();
            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".tif":
                case ".tiff":
                case ".wdp":
                    ShowImage();
                    break;
                case ".csv":
                case ".tsv":
                    await ShowCsvAsync(extension == ".tsv" ? "\t" : ",");
                    break;
                case ".xlsx":
                case ".xlsm":
                    await Task.Run(ShowSpreadsheet);
                    break;
                case ".docx":
                    await Task.Run(ShowWordDocument);
                    break;
                case ".pptx":
                    await Task.Run(ShowPresentation);
                    break;
                case ".pdf":
                    await Task.Run(ShowPdfText);
                    break;
                case ".html":
                case ".htm":
                case ".svg":
                    await ShowHtmlAsync();
                    break;
                case ".epub":
                    await Task.Run(ShowEpub);
                    break;
                case ".mp3":
                case ".wav":
                case ".wma":
                case ".mp4":
                case ".wmv":
                case ".avi":
                    ShowMedia();
                    break;
                case ".zip":
                case ".7z":
                case ".rar":
                case ".tar":
                case ".gz":
                case ".tgz":
                    await Task.Run(ShowArchive);
                    break;
                case ".eml":
                case ".mime":
                    await Task.Run(ShowEmail);
                    break;
                case ".sqlite":
                case ".sqlite3":
                case ".db":
                    await Task.Run(ShowSqlite);
                    break;
                case ".json":
                    await ShowJsonAsync();
                    break;
                case ".md":
                case ".markdown":
                    await ShowMarkdownAsync();
                    break;
                case ".yaml":
                case ".yml":
                    await ShowYamlAsync();
                    break;
                default:
                    await ShowTextAsync();
                    break;
            }

            StatusText.Text = T("FileViewer.ReadOnlyStatus");
        }
        catch (Exception ex)
        {
            ShowText($"{T("FileViewer.InternalFailed")}{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            StatusText.Text = T("FileViewer.TryExternal");
        }
    }

    private void ShowImage()
    {
        Dispatcher.Invoke(() =>
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            HideAll();
            ImageViewer.Source = bitmap;
            ImageViewerScroll.Visibility = Visibility.Visible;
        });
    }

    private async Task ShowCsvAsync(string delimiter)
    {
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            BadDataFound = null,
            MissingFieldFound = null
        });
        using var dataReader = new CsvDataReader(csv);
        var table = new DataTable();
        table.Load(dataReader);
        if (table.Rows.Count > MaximumTableRows)
        {
            while (table.Rows.Count > MaximumTableRows)
            {
                table.Rows.RemoveAt(table.Rows.Count - 1);
            }
        }
        ShowTable(table);
    }

    private void ShowSpreadsheet()
    {
        using var workbook = new XLWorkbook(_path);
        var worksheet = workbook.Worksheets.First();
        var range = worksheet.RangeUsed();
        if (range is null)
        {
            ShowText(T("FileViewer.EmptySheet"));
            return;
        }

        var table = new DataTable(worksheet.Name);
        var columnCount = Math.Min(range.ColumnCount(), 256);
        for (var column = 1; column <= columnCount; column++)
        {
            var rawName = range.Cell(1, column).GetFormattedString();
            var name = string.IsNullOrWhiteSpace(rawName) ? $"Column {column}" : rawName;
            while (table.Columns.Contains(name))
            {
                name += "_";
            }
            table.Columns.Add(name);
        }

        var rowCount = Math.Min(range.RowCount(), MaximumTableRows + 1);
        for (var row = 2; row <= rowCount; row++)
        {
            table.Rows.Add(Enumerable.Range(1, columnCount)
                .Select(column => range.Cell(row, column).GetFormattedString())
                .ToArray());
        }
        ShowTable(table);
    }

    private void ShowPdfText()
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(_path);
        foreach (var page in document.GetPages())
        {
            builder.AppendLine($"--- {T("FileViewer.Page")} {page.Number} ---");
            builder.AppendLine(page.Text);
            builder.AppendLine();
        }
        ShowText(builder.ToString());
    }

    private void ShowWordDocument()
    {
        using var document = WordprocessingDocument.Open(_path, false);
        var text = document.MainDocumentPart?.Document?.Body?
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
            .Select(node => node.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            ?? [];
        ShowText(string.Join(Environment.NewLine, text));
    }

    private void ShowPresentation()
    {
        using var document = PresentationDocument.Open(_path, false);
        var builder = new StringBuilder();
        var slides = document.PresentationPart?.SlideParts
            .Where(part => part.Slide is not null)
            .ToList() ?? [];
        for (var index = 0; index < slides.Count; index++)
        {
            builder.AppendLine($"--- {T("FileViewer.Slide")} {index + 1} ---");
            foreach (var node in slides[index].Slide!
                         .Descendants<DocumentFormat.OpenXml.Drawing.Text>())
            {
                if (!string.IsNullOrWhiteSpace(node.Text))
                {
                    builder.AppendLine(node.Text);
                }
            }
            builder.AppendLine();
        }
        ShowText(builder.ToString());
    }

    private async Task ShowHtmlAsync()
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(await ReadBoundedTextAsync());
        ShowText(document.Body?.TextContent ?? document.DocumentElement.TextContent);
    }

    private void ShowEpub()
    {
        var builder = new StringBuilder();
        var parser = new HtmlParser();
        using var archive = ArchiveFactory.Open(_path);
        foreach (var entry in archive.Entries
                     .Where(entry => !entry.IsDirectory
                         && ((entry.Key ?? string.Empty).EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                             || (entry.Key ?? string.Empty).EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                             || (entry.Key ?? string.Empty).EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
                     .Take(500))
        {
            using var stream = entry.OpenEntryStream();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var document = parser.ParseDocument(reader.ReadToEnd());
            var text = document.Body?.TextContent;
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text.Trim());
                builder.AppendLine();
            }
        }
        ShowText(builder.ToString());
    }

    private void ShowMedia()
    {
        Dispatcher.Invoke(() =>
        {
            HideAll();
            MediaViewer.Source = new Uri(_path);
            MediaViewerPanel.Visibility = Visibility.Visible;
            MediaViewer.Play();
        });
    }

    private void ShowArchive()
    {
        var builder = new StringBuilder();
        using var archive = ArchiveFactory.Open(_path);
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory).Take(20_000))
        {
            builder.AppendLine($"{entry.Size,14:N0}  {entry.Key}");
        }
        ShowText(builder.ToString());
    }

    private void ShowEmail()
    {
        var message = MimeMessage.Load(_path);
        var builder = new StringBuilder();
        builder.AppendLine($"From: {message.From}");
        builder.AppendLine($"To: {message.To}");
        builder.AppendLine($"Cc: {message.Cc}");
        builder.AppendLine($"Date: {message.Date}");
        builder.AppendLine($"Subject: {message.Subject}");
        builder.AppendLine();
        builder.AppendLine(message.TextBody ?? message.HtmlBody ?? T("FileViewer.NoTextBody"));
        if (message.Attachments.Any())
        {
            builder.AppendLine();
            builder.AppendLine(T("FileViewer.Attachments"));
            foreach (var attachment in message.Attachments)
            {
                builder.AppendLine($"- {attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? "attachment"}");
            }
        }
        ShowText(builder.ToString());
    }

    private void ShowSqlite()
    {
        var builder = new StringBuilder();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') ORDER BY type, name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            builder.AppendLine($"{reader.GetString(1)}: {reader.GetString(0)}");
            if (!reader.IsDBNull(2))
            {
                builder.AppendLine(reader.GetString(2));
            }
            builder.AppendLine();
        }
        ShowText(builder.ToString());
    }

    private async Task ShowJsonAsync()
    {
        var source = await ReadBoundedTextAsync();
        using var document = JsonDocument.Parse(source);
        ShowText(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task ShowMarkdownAsync()
    {
        var source = await ReadBoundedTextAsync();
        var plain = Markdown.ToPlainText(source);
        ShowText(plain);
    }

    private async Task ShowYamlAsync()
    {
        var source = await ReadBoundedTextAsync();
        var deserializer = new DeserializerBuilder().Build();
        var data = deserializer.Deserialize<object>(source);
        var serializer = new SerializerBuilder().Build();
        ShowText(serializer.Serialize(data));
    }

    private async Task ShowTextAsync() => ShowText(await ReadBoundedTextAsync());

    private async Task<string> ReadBoundedTextAsync()
    {
        var info = new FileInfo(_path);
        if (info.Length > MaximumTextBytes)
        {
            throw new InvalidDataException(
                string.Format(
                    T("FileViewer.TextLimit"),
                    FormatBytes(MaximumTextBytes)));
        }
        return await File.ReadAllTextAsync(_path);
    }

    private void ShowText(string value) => Dispatcher.Invoke(() =>
    {
        HideAll();
        TextViewer.Text = value;
        TextViewer.Visibility = Visibility.Visible;
    });

    private void ShowTable(DataTable table) => Dispatcher.Invoke(() =>
    {
        HideAll();
        TableViewer.ItemsSource = table.DefaultView;
        TableViewer.Visibility = Visibility.Visible;
    });

    private void HideAll()
    {
        TextViewer.Visibility = Visibility.Collapsed;
        ImageViewerScroll.Visibility = Visibility.Collapsed;
        TableViewer.Visibility = Visibility.Collapsed;
        MediaViewerPanel.Visibility = Visibility.Collapsed;
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e) => OpenExternal();

    private void ShowInExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"")
        {
            UseShellExecute = true
        });
    }

    private void OpenExternal()
    {
        Process.Start(new ProcessStartInfo(_path)
        {
            UseShellExecute = true
        });
    }

    private void MediaPlayButton_Click(object sender, RoutedEventArgs e) => MediaViewer.Play();

    private void MediaPauseButton_Click(object sender, RoutedEventArgs e) => MediaViewer.Pause();

    private void MediaStopButton_Click(object sender, RoutedEventArgs e) => MediaViewer.Stop();

    private static SolidColorBrush Brush(string value) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));

    private string T(string key) => _localize(key);

    private string FormatBytes(long bytes)
    {
        string[] suffixes =
        [
            T("Units.Bytes"),
            T("Units.Kb"),
            T("Units.Mb"),
            T("Units.Gb")
        ];
        var value = (double)Math.Max(0, bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
    }
}
