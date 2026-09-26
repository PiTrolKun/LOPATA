using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    public Action<string, string>? AttentionNotification { get; set; }
    private bool _attentionNotified;

    private void NotifyRequestNeedsAttention()
    {
        if (_attentionNotified) return;
        _attentionNotified = true;
        try
        {
            var title = _l("Literary.Notification.AttentionTitle");
            var message = _l("Literary.Notification.AttentionBody");
            if (AttentionNotification is { } notify) notify(title, message);
            else DesktopAttentionNotification.Show(title, message);
        }
        catch (Exception error)
        {
            // Failure to notify must never lose restored input or block error handling.
            System.Diagnostics.Trace.TraceWarning("Desktop notification failed: {0}", error.GetType().Name);
        }
    }
}
