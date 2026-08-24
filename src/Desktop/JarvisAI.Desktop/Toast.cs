using Windows.UI.Notifications;

namespace JarvisAI.Desktop;

/// <summary>Notifications toast Windows 10/11 natives (TFM windows10, AUMID "JarvisAI").</summary>
public static class Toast
{
    private const string AppId = "JarvisAI";

    public static void Show(string titre, string message)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var noeuds = xml.GetElementsByTagName("text");
            noeuds[0].AppendChild(xml.CreateTextNode(titre));
            noeuds[1].AppendChild(xml.CreateTextNode(message.Length > 180 ? message[..180] : message));
            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch
        {
            // Les toasts exigent un raccourci avec AUMID ; silencieux sinon.
        }
    }
}
