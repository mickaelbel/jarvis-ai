namespace JarvisAI.Application.Voice;

/// <summary>
/// Mode dictÃ©e : le transcript vocal est injectÃ© comme texte dans l'app au
/// premier plan (clipboard + Ctrl+V) au lieu d'Ãªtre envoyÃ© au LLM.
/// </summary>
public interface IDictationService
{
    bool IsEnabled { get; }
    void Activate();
    void Deactivate();
}

public sealed class DictationService : IDictationService
{
    private volatile bool _enabled;
    public bool IsEnabled => _enabled;
    public void Activate() => _enabled = true;
    public void Deactivate() => _enabled = false;
}
