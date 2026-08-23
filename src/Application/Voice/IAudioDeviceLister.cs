namespace JarvisAI.Application.Voice;

public sealed record AudioDeviceInfo(string Id, string Label);

/// <summary>
/// Liste les périphériques audio du système exposés par l'application de bureau.
/// L'id correspond au nom affiché (FriendlyName/ProductName) : c'est exactement ce
/// que le moteur vocal desktop compare via Contains() pour sélectionner le micro.
/// </summary>
public interface IAudioDeviceLister
{
    IReadOnlyList<AudioDeviceInfo> ListInputs();
    IReadOnlyList<AudioDeviceInfo> ListOutputs();
}
