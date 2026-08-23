namespace JarvisAI.Web.Services;

public sealed class VoiceUiState
{
    private bool _enabled;

    public event Action? Changed;

    public bool Enabled => _enabled;

    public void SetEnabled(bool value)
    {
        if (_enabled == value) return;
        _enabled = value;
        Changed?.Invoke();
    }
}
