using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Measure the full recording once, then apply one fixed gain to all its parts.
// Quiet passages and muted/soloed stems never cause automatic gain changes.
internal static class PlayerLoudness
{
    internal const double TargetLufs = -16, PeakDb = -1.5;
    private static readonly SemaphoreSlim analysisGate = new SemaphoreSlim(1);
    private static readonly Dictionary<string, float> memory = new Dictionary<string, float>();
    internal static bool ValidGain(float gain) { return !Single.IsNaN(gain) && !Single.IsInfinity(gain) && gain >= .01f && gain <= 4; }
    internal static float GainFor(double integrated, double peak)
    {
        if (Double.IsNaN(integrated) || Double.IsInfinity(integrated) || Double.IsNaN(peak) || Double.IsInfinity(peak) || integrated < -60) return 1;
        // Match perceived level even when a quiet recording has a few loud
        // transients. Output peak protection handles those peaks separately.
        double db = Math.Max(-30, Math.Min(12, TargetLufs - integrated));
        return (float)Math.Pow(10, db / 20);
    }
    private static string Identity(string audio)
    {
        var file = new FileInfo(audio);
        if (!file.Exists) throw new FileNotFoundException("The recording is unavailable.", audio);
        string key = "loudness-v2|-16|-1.5|" + file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
    }
    internal static async Task<float> Gain(string project, string audio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            string identity = Identity(audio); float gain;
            lock (memory) if (memory.TryGetValue(identity, out gain)) return gain;
            await analysisGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                lock (memory) if (memory.TryGetValue(identity, out gain)) return gain;
                string cache = PlayerData.Data(project, "loudness", identity + ".json");
                bool saved = false;
                try
                {
                    var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(cache));
                    gain = Convert.ToSingle(data["gain"], CultureInfo.InvariantCulture);
                    saved = Convert.ToInt32(data["version"]) == 2 && ValidGain(gain);
                }
                catch { gain = 1; }
                if (!saved)
                {
                    string report = await PlayerMatching.MeasureLoudness(project, audio, token);
                    int marker = report.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
                    if (marker < 0) throw new InvalidDataException("No loudness measurement was returned.");
                    int start = report.LastIndexOf('{', marker), end = report.IndexOf('}', marker);
                    if (start < 0 || end < start) throw new InvalidDataException("Incomplete loudness measurement.");
                    var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(report.Substring(start, end - start + 1));
                    double integrated = 0, peak = 0;
                    bool valid = Double.TryParse(Convert.ToString(data["input_i"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out integrated)
                        && Double.TryParse(Convert.ToString(data["input_tp"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out peak);
                    gain = valid ? GainFor(integrated, peak) : 1;
                    token.ThrowIfCancellationRequested();
                    if (identity != Identity(audio)) return 1;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cache));
                        File.WriteAllText(cache, new JavaScriptSerializer().Serialize(new { version = 2, gain = gain }));
                    }
                    catch { } // Playback still works when a cache cannot be saved.
                }
                token.ThrowIfCancellationRequested();
                if (identity != Identity(audio)) return 1;
                lock (memory) memory[identity] = gain;
                return gain;
            }
            finally { analysisGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch { return 1; } // Decode failures must not prevent ordinary playback.
    }
}

internal sealed partial class StemMixer
{
    private readonly float[] sourceLoudness = { 1, 1, 1, 1, 1 }, previousLoudness = { 1, 1, 1, 1 };
    private bool loudnessMatched;
    private float peakGain = 1;
    internal float OriginalLoudnessGain { get { lock (gate) return sourceLoudness[0]; } }
    internal float StemLoudnessGain(int lane) { lock (gate) return sourceLoudness[lane + 1]; }
    internal void SetLoudness(float gain, float[] lanes = null)
    {
        if (!PlayerLoudness.ValidGain(gain) || lanes != null && (lanes.Length != 4 || Array.Exists(lanes, value => !PlayerLoudness.ValidGain(value)))) throw new ArgumentException("Valid song loudness gains are required.");
        lock (gate)
        {
            sourceLoudness[0] = gain;
            for (int lane = 0; lane < 4; lane++) sourceLoudness[lane + 1] = lanes == null ? gain : lanes[lane];
            loudnessMatched = true; peakGain = 1;
        }
    }
    private void ProtectPeaks(float[] buffer, int offset, int count)
    {
        float ceiling = (float)Math.Pow(10, PlayerLoudness.PeakDb / 20);
        float release = (float)Math.Exp(-1.0 / (WaveFormat.SampleRate * .08));
        for (int sample = 0; sample < count; sample += 2)
        {
            float left = buffer[offset + sample], right = buffer[offset + sample + 1];
            float peak = Math.Max(Math.Abs(left), Math.Abs(right));
            float target = peak > ceiling ? ceiling / peak : 1;
            peakGain = Math.Min(target, 1 - (1 - peakGain) * release);
            buffer[offset + sample] = left * peakGain; buffer[offset + sample + 1] = right * peakGain;
        }
    }
}
