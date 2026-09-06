namespace JarvisAI.Application.Abstractions;

/// <summary>
/// Interface for ComfyUI setup/installation service.
/// </summary>
public interface IComfyUISetup
{
    bool IsSetupDone { get; }
    bool IsSetupRunning { get; }
    void StartSetupIfNeeded();
}
