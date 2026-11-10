using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;

namespace AIHub.Services.VoiceNotifications;

internal static class VoiceMediaElement
{
    private sealed class Hooks { internal string? Source; internal string[] Contexts = []; }
    private static readonly ConditionalWeakTable<MediaElement, Hooks> Attached = new();
    private static readonly ConditionalWeakTable<MediaPlayer, Hooks> Players = new();
    internal static void Play(MediaElement media)
    {
        var hooks = Attached.GetValue(media, element => {
            element.MediaEnded += (_, _) => VoicePlaybackHub.MediaState(element, false);
            element.MediaFailed += (_, _) => VoicePlaybackHub.MediaState(element, false);
            element.Unloaded += (_, _) => VoicePlaybackHub.MediaState(element, false);
            return new();
        });
        var sourceKey = media.Source?.ToString();
        var current = VoiceNotificationContext.Element(media);
        hooks.Contexts = hooks.Source == sourceKey ? hooks.Contexts.Concat(current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : current;
        hooks.Source = sourceKey;
        var file = media.Source is { IsFile: true } source ? VoiceNotificationContext.File(source.LocalPath) : [];
        VoicePlaybackHub.MediaState(media, true, [.. hooks.Contexts, .. file]);
        media.Play();
    }
    internal static void Pause(MediaElement media) { VoicePlaybackHub.MediaState(media, false); media.Pause(); }
    internal static void Stop(MediaElement media) { VoicePlaybackHub.MediaState(media, false); media.Stop(); }
    internal static void Close(MediaElement media) { VoicePlaybackHub.MediaState(media, false); media.Close(); }
    internal static void Play(MediaPlayer media, DependencyObject? owner = null)
    {
        _ = Players.GetValue(media, player => {
            player.MediaEnded += (_, _) => VoicePlaybackHub.MediaState(player, false);
            player.MediaFailed += (_, _) => VoicePlaybackHub.MediaState(player, false);
            return new();
        });
        var file = media.Source is { IsFile: true } source ? VoiceNotificationContext.File(source.LocalPath) : [];
        VoicePlaybackHub.MediaState(media, true, [.. VoiceNotificationContext.Element(owner), .. file]); media.Play();
    }
    internal static void Pause(MediaPlayer media) { VoicePlaybackHub.MediaState(media, false); media.Pause(); }
    internal static void Stop(MediaPlayer media) { VoicePlaybackHub.MediaState(media, false); media.Stop(); }
    internal static void Close(MediaPlayer media) { VoicePlaybackHub.MediaState(media, false); media.Close(); }
}
