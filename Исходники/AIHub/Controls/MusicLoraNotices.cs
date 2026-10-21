using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>Visible warning in the active application; uses the existing desktop channel while hidden.</summary>
internal static class MusicLoraNotices
{
    internal static void Show(FrameworkElement source, string key, Func<string, string> localize)
    {
        if (source.Dispatcher.HasShutdownStarted) return;
        source.Dispatcher.BeginInvoke(() => {
            if (!source.IsLoaded) return;
            var title = localize("Music.Lora.NoticeTitle"); var message = localize(key);
            var owner = Window.GetWindow(source);
            if (owner?.IsVisible != true) { DesktopAttentionNotification.Show(title, message); return; }
            var window = new Window { Owner = owner };
            MusicWishUi.PrepareWindow(window, title, "Music.Lora.Notice", 580);
            window.MinHeight = 170; window.Height = 270; window.SizeToContent = SizeToContent.Height;
            var panel = new StackPanel { Margin = new(18) };
            panel.Children.Add(new ScrollViewer { Content = MusicWishUi.Text(message), MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var button = MusicWishUi.Button(localize("Music.Lora.NoticeClose"), "Music.Lora.Notice.Close", window.Close);
            button.IsDefault = true; button.IsCancel = true; button.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            panel.Children.Add(button); window.Content = panel; window.ShowDialog();
        });
    }
}
