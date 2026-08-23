using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace JarvisAI.Desktop;

// Raccourcis clavier globaux (fonctionnent même si Jarvis n'a pas le focus).
//  - Ctrl+Alt+J : push-to-talk — ouvre une fenêtre d'écoute forcée de 10 s
//    sans mot-clé ni seuil VAD normal, pour les environnements bruyants.
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_J = 0x4A;
    private const int HotkeyIdPushToTalk = 1;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private HwndSourceHook? _hook;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>À appeler depuis le thread UI WPF.</summary>
    public void Start(Action pushToTalk)
    {
        try
        {
            var parameters = new HwndSourceParameters("JarvisAI.Hotkeys")
            {
                ParentWindow = HwndMessage,
                PositionX = 0,
                PositionY = 0,
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(parameters);
            _hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && wParam.ToInt64() == HotkeyIdPushToTalk)
                {
                    pushToTalk();
                    handled = true;
                }
                return IntPtr.Zero;
            };
            _source.AddHook(_hook);

            if (!RegisterHotKey(_source.Handle, HotkeyIdPushToTalk, MOD_CONTROL | MOD_ALT, VK_J))
                App.Log("[Hotkeys] Ctrl+Alt+J indisponible (raccourci pris par une autre app ?)");
            else
                App.Log("[Hotkeys] Push-to-talk actif : Ctrl+Alt+J");
        }
        catch (Exception ex)
        {
            App.Log("[Hotkeys] Initialisation échouée : " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            try { UnregisterHotKey(_source.Handle, HotkeyIdPushToTalk); } catch { }
            if (_hook is not null) _source.RemoveHook(_hook);
            _source.Dispose();
            _source = null;
            _hook = null;
        }
    }
}