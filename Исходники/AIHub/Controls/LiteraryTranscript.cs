using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace AIHub.Controls;

/// <summary>Selectable conversation with a theme-aware foreground for each speaker.</summary>
public sealed class LiteraryTranscript : RichTextBox
{
    private Run? _response;
    public LiteraryTranscript()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        Padding = new Thickness(10);
        BorderThickness = new Thickness(1);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "LineBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        Document.PagePadding = new Thickness(0);
        Document.Blocks.Clear();
    }

    public void Clear() { Document.Blocks.Clear(); _response = null; }
    public void ShowHistory(IEnumerable<(bool User, string Text)> messages, string userLabel, string modelLabel)
    {
        Clear();
        foreach (var message in messages) AddMessage(message.User, message.User ? userLabel : modelLabel, message.Text);
        ScrollToEnd();
    }

    // Keep line breaks inside the themed run: RichTextBox.AppendText would create
    // paragraphs with copied local colors that stop following theme changes.
    public void AppendResponse(string text)
    {
        if (_response is null) throw new InvalidOperationException("BeginReply must precede streaming.");
        _response.ContentEnd.InsertTextInRun(text);
    }

    // Rebuild only at request/retry boundaries. Streamed chunks append to the final model paragraph.
    public void BeginReply(IEnumerable<(bool User, string Text)> messages, string userLabel, string modelLabel)
    {
        Clear();
        foreach (var message in messages)
            AddMessage(message.User, message.User ? userLabel : modelLabel, message.Text);
        AddMessage(false, modelLabel, "");
        ScrollToEnd();
    }

    private void AddMessage(bool user, string label, string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, user ? "ChatUserTextBrush" : "TextPrimaryBrush");
        paragraph.Inlines.Add(new Run(label + ":") { FontWeight = FontWeights.SemiBold });
        paragraph.Inlines.Add(new LineBreak());
        _response = new Run(text) { FontWeight = FontWeights.Normal };
        paragraph.Inlines.Add(_response);
        Document.Blocks.Add(paragraph);
    }
}
