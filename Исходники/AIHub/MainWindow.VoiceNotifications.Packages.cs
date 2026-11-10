using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using AIHub.Services;
using AIHub.Services.VoiceNotifications;

namespace AIHub;

public partial class MainWindow
{
    private async Task ImportVoiceNotificationPackageAsync()
    {
        if (_voiceNotificationStore is not { } store || _voiceNotifications is not { } voice) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = L("VoiceNotifications.Import"), Filter = "ZIP|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var plan = await Task.Run(() => VoicePackageImporter.Inspect(picker.FileName));
            var catalog = store.Catalog(); var known = catalog.Clips.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var lines = plan.Parts.Select(part => part.Language + ": " + part.Items.Length + ", " +
                L("VoiceNotifications.ImportKnown") + " " + part.Items.Count(item => known.Contains(item.ClipId)) + ", " +
                L("VoiceNotifications.ImportMissing") + " " + known.Except(part.Items.Select(item => item.ClipId)).Count() + ", " +
                L("VoiceNotifications.ImportOutdated") + " " + part.Items.Count(item => catalog.Clips.FirstOrDefault(clip => clip.Id == item.ClipId)?.Texts.GetValueOrDefault(part.Language) is { } expected && expected != item.SourceText)).ToList();
            lines.AddRange(plan.Translations.Keys.Select(language => L("VoiceNotifications.ImportText") + ": " + language));
            var summary = string.Join("\n", lines) + "\n\n" + L("VoiceNotifications.ImportConfirm");
            if (System.Windows.MessageBox.Show(this, summary, L("VoiceNotifications.Import"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await Task.Run(() => store.ImportAsync(plan, CancellationToken.None));
            _localizationService.Load(_appSettings.LanguageCode); PopulateLanguageComboBox(); ApplyLocalization();
            await voice.SetLanguageAsync(_localizationService.CurrentLanguageCode);
            RefreshVoiceNotificationUi();
            System.Windows.MessageBox.Show(this, L("VoiceNotifications.Imported"), L("VoiceNotifications.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            OwnedProcessRegistry.Log("voice_import_failed", "VoiceNotifications", detail: error.GetType().Name);
            System.Windows.MessageBox.Show(this, L("VoiceNotifications.InvalidPackage"), L("VoiceNotifications.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async Task ExportVoiceNotificationTextsAsync()
    {
        if (_voiceNotificationStore is not { } store) return;
        var language = _localizationService.CurrentLanguageCode;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = L("VoiceNotifications.ExportTexts"), Filter = "Markdown|*.md", DefaultExt = ".md", AddExtension = true,
            OverwritePrompt = true, FileName = "LOPATA_Voice_Notifications_" + language + ".md" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var data = store.Catalog(); var translation = store.Translation(language);
            var provenance = new OutputProvenance { Scenario = "voice-notifications/localization-template", ModelHistory = "not-applicable", CreatedUtc = DateTimeOffset.UtcNow };
            var output = new StringBuilder("<!-- LOPATA_PROVENANCE " + provenance.Json() + " -->\n\n# " + L("VoiceNotifications.Title") + "\n\n");
            output.Append("<!-- TargetLanguage: ").Append(language).Append("; untranslated entries use the Russian source. -->\n\n");
            for (var i = 0; i < data.Clips.Length; i++)
            {
                var clip = data.Clips[i]; var text = translation.GetValueOrDefault("VoiceNotifications.Text." + clip.Id)
                    ?? clip.Texts.GetValueOrDefault(language) ?? clip.Texts["ru"];
                text = Regex.Replace(text, @"\s+", " ").Trim();
                output.Append("## ").Append((i + 1).ToString("D4")).Append(" · ").Append(clip.Id).Append("\n\n- ")
                    .Append(text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_")).Append("\n\n");
            }
            await File.WriteAllTextAsync(picker.FileName, output.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            OwnedProcessRegistry.Log("voice_text_export_failed", "VoiceNotifications", detail: error.GetType().Name);
            System.Windows.MessageBox.Show(this, L("VoiceNotifications.ExportFailed"), L("VoiceNotifications.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
