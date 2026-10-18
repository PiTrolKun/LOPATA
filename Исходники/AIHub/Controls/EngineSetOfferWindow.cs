using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

/// <summary>Closing this offer is the same as keeping the current set; only an explicit action enables a new one.</summary>
public sealed class EngineSetOfferWindow : Window
{
    public EngineSetOfferWindow(Window owner, string recommended, string active, Func<string, string> text)
    {
        Owner = owner;
        MusicWishUi.PrepareWindow(this, text("EngineSets.NewTitle"), "EngineSets.Offer", 620);
        SizeToContent = SizeToContent.Height;
        MinHeight = 180; MaxHeight = SystemParameters.WorkArea.Height;
        var body = new StackPanel { Margin = new(20) };
        body.Children.Add(MusicWishUi.Text(string.Format(text("EngineSets.OfferText"), recommended, active)));
        var actions = new WrapPanel { Margin = new(0, 16, 0, 0) };
        actions.Children.Add(MusicWishUi.Button(string.Format(text("EngineSets.Accept"), recommended),
            "EngineSets.Offer.Accept", () => DialogResult = true));
        var keep = MusicWishUi.Button(string.Format(text("EngineSets.Keep"), active),
            "EngineSets.Offer.Keep", () => DialogResult = false);
        keep.IsCancel = true; keep.IsDefault = true;
        actions.Children.Add(keep); body.Children.Add(actions); Content = body;
    }
}
