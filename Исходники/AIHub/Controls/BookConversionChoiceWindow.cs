using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.Books;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Choose what to do with an already converted private target, before publication.</summary>
public sealed class BookConversionChoiceWindow : Window
{
    public bool ReviewRequested { get; private set; }

    public BookConversionChoiceWindow(Window owner, BookReviewSession session, Func<string, string> localize)
    {
        Owner = owner;
        string T(string key) => localize("BookConverter.Choice." + key);
        MusicWishUi.PrepareWindow(this, T("Title"), "BookConverter.Choice.Window", 650);
        Resources.MergedDictionaries.Insert(0, owner.Resources);
        Height = 270; MinWidth = 480; MinHeight = 240; ResizeMode = ResizeMode.NoResize;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var review = MusicWishUi.Button(T("Review"), "BookConverter.Choice.Review", () => { ReviewRequested = true; DialogResult = true; });
        var save = MusicWishUi.Button(T("Save"), "BookConverter.Choice.Save", () => { ReviewRequested = false; DialogResult = true; });
        var cancel = MusicWishUi.Button(T("Cancel"), "BookConverter.Choice.Cancel", () => { DialogResult = false; });
        review.IsDefault = true; cancel.IsCancel = true;
        buttons.Children.Add(review); buttons.Children.Add(save); buttons.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(body);
        body.Children.Add(MusicWishUi.Text(Path.ChangeExtension(session.SourceName, session.OutputFormat), true));
        body.Children.Add(MusicWishUi.Text(T("Question")));
        body.Children.Add(MusicWishUi.Text(T("Hint")));
    }
}
