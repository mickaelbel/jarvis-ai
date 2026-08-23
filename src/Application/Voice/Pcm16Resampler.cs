namespace JarvisAI.Application.Voice;

public static class Pcm16Resampler
{
    public static byte[] Resample(byte[] pcm16, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0 || sourceRate == targetRate || pcm16.Length < 2)
            return pcm16;

        var sourceFrames = pcm16.Length / 2;
        var targetFrames = (int)((long)sourceFrames * targetRate / sourceRate);
        if (targetFrames <= 0) return pcm16;

        var output = new byte[targetFrames * 2];
        var ratio = (double)sourceRate / targetRate;

        for (var i = 0; i < targetFrames; i++)
        {
            var pos = i * ratio;
            var idx = (int)pos;
            var frac = pos - idx;
            if (idx >= sourceFrames - 1)
            {
                idx = sourceFrames - 2 < 0 ? 0 : sourceFrames - 2;
                frac = 1.0;
            }
            var sampleA = (short)(pcm16[idx * 2] | (pcm16[idx * 2 + 1] << 8));
            var sampleB = (short)(pcm16[(idx + 1) * 2] | (pcm16[(idx + 1) * 2 + 1] << 8));
            var value = sampleA + (sampleB - sampleA) * frac;
            var clipped = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(clipped & 0xFF);
            output[i * 2 + 1] = (byte)((clipped >> 8) & 0xFF);
        }

        return output;
    }
}
