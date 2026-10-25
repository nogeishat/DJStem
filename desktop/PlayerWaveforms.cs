using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;

internal static class PlayerWaveforms
{
    private static readonly object gate = new object();
    private static readonly Dictionary<string, float[]> cache = new Dictionary<string, float[]>();
    internal static float[] Read(string file, CancellationToken cancellation, Action<double> progress = null)
    {
        FileInfo info = new FileInfo(file);
        string key = info.FullName + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        lock (gate) { float[] stored; if (cache.TryGetValue(key, out stored)) { if (progress != null) progress(1); return stored; } }
        const int buckets = 1600;
        float[] peaks = new float[buckets];
        using (WaveFileReader reader = new WaveFileReader(file))
        {
            var samples = StemMixer.Samples(reader);
            long frames = reader.Length / reader.WaveFormat.BlockAlign, frame = 0;
            int channels = reader.WaveFormat.Channels;
            float[] buffer = new float[8192 * channels];
            int read; double reported = 0;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < read; i += channels, frame++)
                {
                    int bucket = (int)Math.Min(buckets - 1, frame * buckets / Math.Max(1, frames));
                    for (int channel = 0; channel < channels && i + channel < read; channel++) peaks[bucket] = Math.Max(peaks[bucket], Math.Abs(buffer[i + channel]));
                }
                double fraction = frame / (double)Math.Max(1, frames);
                if (progress != null && fraction - reported >= .01) { progress(fraction); reported = fraction; }
            }
        }
        float maximum = 0.0001f;
        foreach (float peak in peaks) maximum = Math.Max(maximum, peak);
        for (int i = 0; i < peaks.Length; i++) peaks[i] = Math.Min(1, peaks[i] / maximum);
        cancellation.ThrowIfCancellationRequested();
        lock (gate) { if (cache.Count >= 64) cache.Clear(); cache[key] = peaks; }
        if (progress != null) progress(1);
        return peaks;
    }
}
