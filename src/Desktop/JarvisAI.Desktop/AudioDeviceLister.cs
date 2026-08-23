using JarvisAI.Application.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JarvisAI.Desktop;

/// <summary>
/// Enumère les périphériques audio réels du système via NAudio (WASAPI en priorité,
/// MME/WaveIn/WaveOut en complément). L'id est le nom affiché, identique à celui que
/// BackgroundVoiceEngine utilise pour sélectionner le micro (FriendlyName/ProductName).
/// </summary>
public sealed class AudioDeviceLister : IAudioDeviceLister
{
    public IReadOnlyList<AudioDeviceInfo> ListInputs()
    {
        var result = new List<AudioDeviceInfo>();
        TryAddWasapi(DataFlow.Capture, result);
        TryAddWaveIn(result);
        return result;
    }

    public IReadOnlyList<AudioDeviceInfo> ListOutputs()
    {
        var result = new List<AudioDeviceInfo>();
        TryAddWasapi(DataFlow.Render, result);
        return result;
    }

    private static void TryAddWasapi(DataFlow flow, List<AudioDeviceInfo> result)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var name = dev.FriendlyName;
                if (!string.IsNullOrWhiteSpace(name) && !result.Any(d => string.Equals(d.Id, name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new AudioDeviceInfo(name, name));
            }
        }
        catch { }
    }

    private static void TryAddWaveIn(List<AudioDeviceInfo> result)
    {
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var name = WaveInEvent.GetCapabilities(i).ProductName;
                if (!string.IsNullOrWhiteSpace(name) && !result.Any(d => string.Equals(d.Id, name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new AudioDeviceInfo(name, name));
            }
        }
        catch { }
    }
}
