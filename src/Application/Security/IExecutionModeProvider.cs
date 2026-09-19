namespace JarvisAI.Application.Security;

public interface IExecutionModeProvider
{
    string CurrentMode { get; }
    event Action<string>? ModeChanged;
}
