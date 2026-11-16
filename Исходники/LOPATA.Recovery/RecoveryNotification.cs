using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Lopata.Recovery;

internal static class RecoveryNotification
{
    private const string AppId = "Lopata.Recovery";

    internal static void BeforeRestart(RecoveryPaths paths, ProcessIdentity crashed, bool safeMode)
    {
        var submission = Task.Run(() => Submit(paths, safeMode));
        if (!submission.Wait(TimeSpan.FromSeconds(1)))
        {
            paths.Log("restart_notification_timeout", crashed);
            return;
        }

        var result = submission.Result;
        paths.Log(result.Action, crashed, result.Detail);
    }

    private static (string Action, object? Detail) Submit(RecoveryPaths paths, bool safeMode)
    {
        try
        {
            var settings = RecoveryPaths.Read<NotificationSettings>(Path.Combine(paths.StateRoot, "settings.json"));
            var language = settings?.LanguageCode ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            bool russian = language.Equals("ru", StringComparison.OrdinalIgnoreCase);
            bool autoResume = settings?.Behavior?.AutoResume != false;
            var (title, body) = (russian, safeMode, autoResume) switch
            {
                (true, true, _) => ("ЛОПАТА запускается в безопасном режиме",
                    "Произошёл повторный сбой. Сохранённые задачи можно продолжить вручную."),
                (true, false, true) => ("ЛОПАТА перезапускается после сбоя",
                    "Дождитесь запуска. Задачи с автопродолжением возобновятся с сохранённого этапа."),
                (true, false, false) => ("ЛОПАТА перезапускается после сбоя",
                    "Дождитесь запуска. Сохранённые задачи можно продолжить вручную."),
                (false, true, _) => ("LOPATA is starting in safe mode",
                    "The app closed unexpectedly again. You can resume saved tasks manually."),
                (false, false, true) => ("LOPATA is restarting after a crash",
                    "Wait for the app to start. Tasks with automatic resume will continue from the last saved step."),
                _ => ("LOPATA is restarting after a crash",
                    "Wait for the app to start. You can resume saved tasks manually.")
            };

            bool fixture = !string.Equals(paths.StateRoot, RecoveryPaths.DefaultStateRoot, StringComparison.OrdinalIgnoreCase);
            string appId = fixture ? AppId + ".Test" : AppId;
            using var registration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + appId);
            registration.SetValue("DisplayName", fixture ? "LOPATA Test" : russian ? "ЛОПАТА" : "LOPATA");

            var toast = new XElement("toast", new XAttribute("activationType", "system"), new XAttribute("launch", "dismiss"),
                new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title), new XElement("text", body))),
                new XElement("audio", new XAttribute("silent", "true")));
            WindowsToast.Show(appId, toast.ToString(SaveOptions.DisableFormatting), suppressPopup: fixture);
            return ("restart_notification_submitted", null);
        }
        catch (Exception error)
        {
            return ("restart_notification_failed", new { type = error.GetType().Name, error.HResult });
        }
    }

    private sealed record NotificationSettings(
        [property: JsonPropertyName("languageCode")] string? LanguageCode,
        [property: JsonPropertyName("behavior")] BehaviorSettings? Behavior);

    private sealed record BehaviorSettings(
        [property: JsonPropertyName("autoResumeBackgroundOperation")] bool? AutoResume);
}
