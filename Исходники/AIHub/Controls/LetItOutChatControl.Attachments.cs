using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private readonly List<string> _draftAttachments = [];
    private readonly WrapPanel _attachmentCards = new();
    private Button? _attach;
    private bool _importingAttachments;
    private Func<CancellationToken, Task<bool>>? _prepareImages;
    private Action<string>? _openAttachment;
    private CancellationTokenSource? _attachmentCancel;
    internal void ConfigureAttachments(Func<CancellationToken, Task<bool>> prepareImages, Action<string> open)
    { _prepareImages = prepareImages; _openAttachment = open; RenderAttachmentDraft(); }

    private void BuildAttachmentInput(StackPanel bottom, Border composer)
    {
        bottom.Children.Remove(composer);
        var frame = new Grid(); frame.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); frame.ColumnDefinitions.Add(new());
        _attach = new Button { Content = "♧", Width = 28, Height = 30, MinWidth = 0, Padding = new(4),
            Background = Brushes.Transparent, BorderThickness = new(0), Opacity = .65, Margin = new(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Bottom };
        var clip = new System.Windows.Shapes.Path { Data = Geometry.Parse("M9,17 L17,9 C20,6 16,2 13,5 L5,13 C0,18 6,24 11,19 L20,10 M8,16 L16,8"),
            StrokeThickness = 1.7, Width = 19, Height = 22, Stretch = Stretch.Uniform };
        clip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush"); _attach.Content = clip;
        AutomationProperties.SetAutomationId(_attach, "LetItOut.Attachments.Add"); _attach.ToolTip = _l("LetItOut.Attachments.Add");
        AutomationProperties.SetName(_attach, _l("LetItOut.Attachments.Add"));
        _attach.Click += (_, _) => SelectAttachments(); frame.Children.Add(_attach);
        Grid.SetColumn(composer, 1); frame.Children.Add(composer); bottom.Children.Add(frame);
        var input = composer.Child as UIElement; composer.Child = null;
        var stack = new Grid(); stack.RowDefinitions.Add(new() { Height = GridLength.Auto }); stack.RowDefinitions.Add(new());
        var cards = new ScrollViewer { Content = _attachmentCards, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        stack.Children.Add(cards); if (input is not null) { Grid.SetRow(input, 1); stack.Children.Add(input); } composer.Child = stack;
        AllowDrop = true;
        PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        PreviewDrop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; _ = AddAttachmentPathsAsync(files); } };
        DataObject.AddPastingHandler(_input, (_, e) => { if (ClipboardAttachment()) e.CancelCommand(); });
        RenderAttachmentDraft();
    }
    private void SelectAttachments()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = _l("LetItOut.Attachments.Add"), Multiselect = true,
            CheckFileExists = true, Filter = _l("LetItOut.Attachments.Filter") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = AddAttachmentPathsAsync(dialog.FileNames);
    }
    internal bool ClipboardAttachment()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            { _ = AddAttachmentPathsAsync(System.Windows.Clipboard.GetFileDropList().Cast<string>()); return true; }
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
            {
                var folder = Path.Combine(AppDataPaths.BaseDirectory, "Chat", "Clipboard", Guid.NewGuid().ToString("N"));
                Lopata.Updates.SafeUpdatePath.RejectLinks(folder); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "clipboard.png");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = File.Create(path)) encoder.Save(output);
                _ = ImportClipboardAsync(path); return true;
            }
        }
        catch (Exception error) { AttachmentError(error); return true; }
        return false;
    }
    private async Task ImportClipboardAsync(string path)
    { try { await AddAttachmentPathsAsync([path]); } finally { if (File.Exists(path)) File.Delete(path); } }

    internal async Task AddAttachmentPathsAsync(IEnumerable<string> paths)
    {
        if (_library is null || _importingAttachments || _disposed) return;
        var files = paths.ToArray();
        if (files.Length + _draftAttachments.Count > LetItOutConversationStore.AttachmentLimit)
        { AttachmentError(new InvalidDataException("LetItOut.Attachments.Count")); return; }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _attachmentCancel = cancel; var conversation = _sessionId; _importingAttachments = true; Availability();
        try
        {
            if (files.Any(LetItOutConversationStore.IsAudioAttachment)) _audioNotice?.Invoke();
            var parserLicenses = files.Select(Path.GetExtension).Select(extension => (extension ?? "").ToLowerInvariant() switch {
                ".docx" => "nuget.DocumentFormat.OpenXml", ".pdf" => "nuget.PdfPig", _ => "" }).Where(id => id.Length > 0).Distinct().ToArray();
            if (parserLicenses.Length > 0) await ComponentLicenseGate.EnsureAsync(parserLicenses, cancel.Token);
            if (!_persisted) { _library.Create(conversation); _persisted = true; }
            foreach (var file in files)
            {
                var attachment = await Task.Run(() => _library.ImportAttachmentAsync(conversation, file, cancel.Token), cancel.Token);
                if (_sessionId != conversation || _disposed || cancel.IsCancellationRequested) return;
                _draftAttachments.Add(attachment.Id); SaveAttachmentDraft(); RenderAttachmentDraft();
            }
            _statusKey = "LetItOut.Ready";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AttachmentError(error); }
        finally { _attachmentCancel = null; _importingAttachments = false; RefreshLibrary(); QueueContextRefresh(); Availability(); }
    }
    private void AttachmentError(Exception error)
    {
        _statusKey = error.Message.StartsWith("LetItOut.Attachments.", StringComparison.Ordinal) ? error.Message : "LetItOut.Attachments.Failed";
        OwnedProcessRegistry.Log("chat_attachment_failed", "LetItOut.Attachments", detail: error.GetType().Name); Availability();
    }
    private void SaveAttachmentDraft()
    { if (_persisted && _library is not null) _library.SaveDraftAttachments(_sessionId, _draftAttachments); }
    private void RestoreAttachmentDraft(LetItOutConversation? conversation)
    { _attachmentCancel?.Cancel(); _draftAttachments.Clear(); _draftAttachments.AddRange(conversation?.DraftAttachments ?? []); RenderAttachmentDraft(); }
    private void RenderAttachmentDraft()
    {
        _attachmentCards.Children.Clear();
        if (_attach is not null) { _attach.ToolTip = _l("LetItOut.Attachments.Add"); AutomationProperties.SetName(_attach, _l("LetItOut.Attachments.Add")); }
        if (_library is null) return;
        foreach (var id in _draftAttachments.ToArray()) _attachmentCards.Children.Add(AttachmentCard(id, true));
    }
    private UIElement AttachmentCard(string id, bool draft)
    {
        var panel = new Grid { Margin = new(4), Width = 170 };
        try
        {
            var a = _library!.Attachment(_sessionId, id); var path = _library.AttachmentPath(_sessionId, a);
            var body = new StackPanel();
            if (a.Kind == "audio") body.Children.Add(Label("🎙 " + _l("LetItOut.Attachments.AudioKind"), 12));
            if (a.Kind == "image")
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 150; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
                body.Children.Add(new System.Windows.Controls.Image { Source = bitmap, Height = 56, Stretch = Stretch.Uniform });
            }
            body.Children.Add(new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Margin = new(4) });
            var open = new Button { Content = body, ToolTip = a.Name + "\n" + a.Bytes.ToString("N0") + " bytes", Padding = new(4), MinWidth = 0 };
            open.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            AutomationProperties.SetAutomationId(open, "LetItOut.Attachment." + id); AutomationProperties.SetName(open, a.Name);
            open.Click += (_, _) => { try { _openAttachment?.Invoke(path); } catch (Exception error) { AttachmentError(error); } }; panel.Children.Add(open);
            if (draft)
            {
                var remove = new Button { Content = "×", Width = 22, Height = 22, MinWidth = 0, Padding = new(0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = _l("LetItOut.Attachments.Remove") };
                remove.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); AutomationProperties.SetAutomationId(remove, "LetItOut.Attachment.Remove." + id);
                remove.Click += (_, _) => { _draftAttachments.Remove(id); SaveAttachmentDraft(); RenderAttachmentDraft(); QueueContextRefresh(); Availability(); }; panel.Children.Add(remove);
            }
        }
        catch { panel.Children.Add(Label(_l("LetItOut.Attachments.Missing"), 12)); }
        return panel;
    }
    private void RenderMessageAttachments(Entry entry, StackPanel body)
    {
        if (entry.Attachments is not { Count: > 0 } || _library is null) return;
        var cards = new WrapPanel(); foreach (var id in entry.Attachments) cards.Children.Add(AttachmentCard(id, false)); body.Children.Add(cards);
    }
}
