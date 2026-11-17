using System.Text.Json;
using Lopata.Updates;

namespace Lopata.Updater;

internal static class SettingsRollbackPrompt
{
    public static void Show(IWin32Window owner, CoreVoiceRollbackStore store, string id)
    {
        var rollbackTitle = HostText.Get("UpdateHost.RollbackTitle");
        CoreVoiceRestorePreview? preview;
        try { preview = store.Preview(id); }
        catch (Exception error) when (IsSettingsError(error))
        {
            ShowUnavailable(owner, rollbackTitle);
            return;
        }
        if (preview is null)
        {
            MessageBox.Show(owner, HostText.Get("UpdateHost.AppRolledBack"), rollbackTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var title = HostText.Get("UpdateHost.SettingsChanged");
        using var dialog = new Form
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Font,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Padding = new(20)
        };
        var details = new Label { AutoSize = true, MaximumSize = new(500, 0), UseMnemonic = false };
        var restore = new Button { AutoSize = true, Text = HostText.Get("UpdateHost.RestoreSettings") };
        var keep = new Button { AutoSize = true, Text = HostText.Get("UpdateHost.KeepSettings"), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 14, 0, 0) };
        buttons.Controls.AddRange([restore, keep]);
        var content = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        content.Controls.AddRange([details, buttons]);
        dialog.Controls.Add(content);
        dialog.AcceptButton = keep;
        dialog.CancelButton = keep;
        dialog.Shown += (_, _) => keep.Focus();
        UpdateDetails();
        restore.Click += (_, _) =>
        {
            try
            {
                if (store.TryRestore(id, preview!, out var fresh))
                {
                    dialog.Close();
                    return;
                }
                preview = fresh;
                if (preview is null)
                {
                    MessageBox.Show(dialog, HostText.Get("UpdateHost.SettingsAlreadyMatch"), title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    dialog.Close();
                    return;
                }
                UpdateDetails(HostText.Get("UpdateHost.ReviewSettingsAgain"));
                keep.Focus();
            }
            catch (Exception error) when (IsSettingsError(error))
            {
                ShowUnavailable(dialog, title);
                dialog.Close();
            }
        };
        dialog.ShowDialog(owner);

        void UpdateDetails(string? changed = null)
        {
            var current = preview!.Current;
            var saved = preview.Saved;
            var lines = new List<string> { HostText.Get("UpdateHost.AppRolledBack"), "", HostText.Get("UpdateHost.SettingsOffer") };
            if (changed is not null) lines.Add(changed);
            if (current.Preferred != saved.Preferred)
                AddValue("UpdateHost.PreferredVoice", current.Preferred, saved.Preferred);
            if (current.LegacyProvider != saved.CompatibleProvider)
                AddValue("UpdateHost.CompatibleVoice", current.LegacyProvider, saved.CompatibleProvider);
            if (current.Preferred == saved.Preferred && current.LegacyProvider == saved.CompatibleProvider)
                AddValue("UpdateHost.SeparateVoice", current.SeparatePreference, saved.SeparatePreference);
            details.Text = string.Join(Environment.NewLine, lines);

            void AddValue(string label, string? before, string? after)
            {
                lines.Add("");
                lines.Add(HostText.Get(label));
                lines.Add(string.Format(HostText.Get("UpdateHost.CurrentValue"), DisplayVoice(before)));
                lines.Add(string.Format(HostText.Get("UpdateHost.SavedValue"), DisplayVoice(after)));
            }
        }
    }

    private static string DisplayVoice(string? value) => value switch
    {
        CoreVoiceProfileStore.Mini => "Kitten TTS 2 Mini",
        CoreVoiceProfileStore.Espeak => "eSpeak NG",
        CoreVoiceProfileStore.RhVoice => "RHVoice",
        _ => string.IsNullOrWhiteSpace(value) ? HostText.Get("UpdateHost.EmptyVoice") : value
    };

    private static bool IsSettingsError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    private static void ShowUnavailable(IWin32Window owner, string title) => MessageBox.Show(owner,
        HostText.Get("UpdateHost.AppRolledBack") + Environment.NewLine + HostText.Get("UpdateHost.SettingsRestoreUnavailable"),
        title, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
