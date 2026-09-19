using JarvisAI.Application.Security;
using JarvisAI.Web.Services;

namespace JarvisAI.Web.Services;

public sealed class ExecutionModeProvider : IExecutionModeProvider
{
    private readonly AdvancedSettingsService _settings;

    public string CurrentMode => _settings.Get().Mode.ToString();
    public event Action<string>? ModeChanged;

    public ExecutionModeProvider(AdvancedSettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        ModeChanged?.Invoke(CurrentMode);
    }
}
