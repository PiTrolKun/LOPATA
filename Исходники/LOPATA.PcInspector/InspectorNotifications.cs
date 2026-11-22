using System.Runtime.InteropServices;
using System.Xml.Linq;
using Lopata.Recovery;
using Microsoft.Win32;

namespace Lopata.PcInspector;

internal static class InspectorNotifications
{
    private const string AppId = "Lopata.PcInspector";

    internal static bool TryShow(string title, string body, bool russian)
    {
        try
        {
            using var registration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            registration.SetValue("DisplayName", russian ? "Фоновый ИИ-инспектор ПК" : "Background PC inspector");
            var toast = new XElement("toast", new XAttribute("activationType", "system"), new XAttribute("launch", "dismiss"),
                new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", title), new XElement("text", body))),
                new XElement("audio", new XAttribute("silent", "true")));
            WindowsToast.Show(AppId, toast.ToString(SaveOptions.DisableFormatting), suppressPopup: false);
            return true;
        }
        catch (Exception error) when (error is ExternalException or IOException or UnauthorizedAccessException or
                                     System.Security.SecurityException or ArgumentException)
        { return false; }
    }
}
