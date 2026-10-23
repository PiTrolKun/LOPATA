using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

/// <summary>Explains a missing destination before submission; choosing a folder never submits a job.</summary>
internal sealed class MusicOutputFolderPrompt : Window
{
    internal MusicOutputFolderPrompt(Window? owner, Func<string, string> l)
    {
        MusicWishUi.PrepareWindow(this, l("Music.OutputFolder.Title"), "Music.OutputFolder.Window", 540);
        if (owner is not null) Owner = owner;
        MinHeight = 0; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        MaxHeight = SystemParameters.WorkArea.Height;
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(MusicWishUi.Text(l("Music.OutputFolder.Message")));
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) };
        var choose = MusicWishUi.Button(l("Music.OutputFolder.Choose"), "Music.OutputFolder.Choose", () => DialogResult = true);
        choose.IsDefault = true; choose.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle");
        var cancel = MusicWishUi.Button(l("Music.OutputFolder.Cancel"), "Music.OutputFolder.Cancel", () => DialogResult = false);
        cancel.IsCancel = true; actions.Children.Add(choose); actions.Children.Add(cancel);
        panel.Children.Add(actions); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal static bool EnsureSelected(MusicTracksControl tracks, Func<string, string> l) =>
        Check(tracks.OutputFolder, () => new MusicOutputFolderPrompt(GetWindow(tracks), l).ShowDialog() == true,
            tracks.ChooseOutputFolder);

    internal static bool Check(string folder, Func<bool> requestChoice, Action chooseFolder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) return true;
        if (requestChoice()) chooseFolder();
        return false;
    }
}
