using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Dsp;

// Analyze source samples before scheduling, never the user's volume, mute,
// solo, or normalized waveform display. Look ahead to reject short rests.
internal sealed class AutoAudioDynamics
{
    private readonly double[] levels;
    private readonly double windowSeconds;
    private readonly double reference;
    private readonly float[][] tones;
    internal bool HasSignal { get { return levels.Any(value => value > .002); } }
    internal double WindowSeconds { get { return windowSeconds; } }
    private AutoAudioDynamics(double[] values, double window, float[][] fingerprints = null)
    {
        levels = values; windowSeconds = window; tones = fingerprints;
        double[] audible = values.Where(value => value > .002).OrderBy(value => value).ToArray();
        reference = audible.Length == 0 ? 0 : audible[(audible.Length - 1) * 4 / 5];
    }

    internal static AutoAudioDynamics Read(string file, CancellationToken cancellation, bool tonal = false, double resolution = .1)
    {
        cancellation.ThrowIfCancellationRequested();
        using (WaveStream reader = Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? (WaveStream)new WaveFileReader(file) : new MediaFoundationReader(file))
        {
            var samples = StemMixer.Samples(reader);
            int channels = reader.WaveFormat.Channels, window = Math.Max(1, (int)Math.Round(reader.WaveFormat.SampleRate * resolution)), frames = 0;
            var values = new List<double>();
            var fingerprints = tonal ? new List<float[]>() : null;
            var ring = tonal ? new float[1024] : null;
            var spectrum = tonal ? new Complex[1024] : null;
            int decimation = Math.Max(1, reader.WaveFormat.SampleRate / 4000), decimated = 0, ringPosition = 0;
            float[] buffer = new float[8192 * channels]; double energy = 0; int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int sample = 0; sample < read; sample += channels)
                {
                    for (int channel = 0; channel < channels; channel++) energy += (double)buffer[sample + channel] * buffer[sample + channel];
                    if (tonal && decimated++ % decimation == 0)
                    {
                        float mono = 0; for (int channel = 0; channel < channels; channel++) mono += buffer[sample + channel] / channels;
                        ring[ringPosition++ % ring.Length] = mono;
                    }
                    if (++frames == window)
                    {
                        values.Add(Math.Sqrt(energy / (frames * channels))); frames = 0; energy = 0;
                        if (tonal) fingerprints.Add(Fingerprint(ring, ringPosition, spectrum, reader.WaveFormat.SampleRate / (double)decimation));
                    }
                }
            }
            if (frames > 0)
            {
                values.Add(Math.Sqrt(energy / (frames * channels)));
                if (tonal) fingerprints.Add(Fingerprint(ring, ringPosition, spectrum, reader.WaveFormat.SampleRate / (double)decimation));
            }
            cancellation.ThrowIfCancellationRequested();
            return new AutoAudioDynamics(values.ToArray(), window / (double)reader.WaveFormat.SampleRate, tonal ? fingerprints.ToArray() : null);
        }
    }

    private static float[] Fingerprint(float[] ring, int position, Complex[] spectrum, double rate)
    {
        for (int index = 0; index < ring.Length; index++)
        {
            spectrum[index].X = (float)(ring[(position + index) % ring.Length] * (.5 - .5 * Math.Cos(2 * Math.PI * index / (ring.Length - 1))));
            spectrum[index].Y = 0;
        }
        FastFourierTransform.FFT(true, 10, spectrum);
        var chroma = new float[12];
        for (int bin = 1; bin < spectrum.Length / 2; bin++)
        {
            double hz = bin * rate / spectrum.Length; if (hz < 45 || hz > 1800) continue;
            int note = (int)Math.Round(69 + 12 * Math.Log(hz / 440, 2));
            chroma[(note % 12 + 12) % 12] += spectrum[bin].X * spectrum[bin].X + spectrum[bin].Y * spectrum[bin].Y;
        }
        double total = chroma.Sum(value => (double)value);
        if (total > 0) for (int note = 0; note < 12; note++) chroma[note] = (float)Math.Sqrt(chroma[note] / total);
        return chroma;
    }

    internal AutoAudioDynamics ForTimeline(PlaybackTransform transform)
    {
        if (transform == null) return this;
        int count = (int)Math.Ceiling(transform.Frames / (double)transform.Rate / windowSeconds);
        var mapped = new double[count]; var fingerprints = tones == null ? null : new float[count][];
        for (int index = 0; index < count; index++)
        {
            double at = (index * windowSeconds - transform.Offset) * transform.Tempo;
            int source = (int)Math.Floor(at / windowSeconds);
            if (source < 0 || source >= levels.Length) continue;
            mapped[index] = levels[source]; if (tones != null) fingerprints[index] = tones[source];
        }
        return new AutoAudioDynamics(mapped, windowSeconds, fingerprints);
    }

    internal double Difference(double first, double second)
    {
        int a = (int)Math.Floor(first / windowSeconds), b = (int)Math.Floor(second / windowSeconds);
        if (a < 0 || b < 0 || a >= levels.Length || b >= levels.Length) return 0;
        double cost = Math.Abs(levels[a] - levels[b]) / Math.Max(.002, reference);
        if (tones != null && tones[a] != null && tones[b] != null)
            for (int note = 0; note < 12; note++) cost += Math.Abs(tones[a][note] - tones[b][note]) / 3;
        return cost;
    }

    private double RelativeLevel(double at)
    {
        int index = (int)Math.Floor(at / windowSeconds + .00000001);
        return index < 0 || index >= levels.Length ? 0 : levels[index] / Math.Max(.002, reference);
    }

    internal IEnumerable<double> DrumBoundaries()
    {
        // Leave the splice two analysis windows before the attack.
        for (int index = 1; index + 1 < levels.Length; index++)
        {
            double level = levels[index] / Math.Max(.002, reference);
            if (level >= .5 && levels[index] > levels[index - 1] * 1.5)
                yield return Math.Max(0, (index - 2) * windowSeconds);
            else if (level < .4 && levels[index] < levels[index - 1] && levels[index] <= levels[index + 1])
                yield return index * windowSeconds;
        }
    }

    internal double DrumBoundaryCost(double at, double bpm)
    {
        double level = Math.Max(RelativeLevel(at), RelativeLevel(at + windowSeconds / 2));
        double peak = 0, untilPeak = 60 / bpm;
        for (double ahead = windowSeconds; ahead <= 60 / bpm; ahead += windowSeconds)
        {
            double next = RelativeLevel(at + ahead);
            if (next > peak) { peak = next; untilPeak = ahead; }
        }
        // Prefer a quiet lead into a hit, or the valley before a rising peak
        // when a continuous drum bed has no actual silence.
        return level * 20 + (peak >= .5 ? Math.Abs(untilPeak - 2 * windowSeconds) * 8 : level <= .2 ? 2 : 8);
    }

    // Only interior pauses count as phrase endings and beginnings. A silent
    // intro/tail cannot promise a new verse, and word-sized dips are ignored.
    internal IEnumerable<double[]> VocalGaps(double bpm)
    {
        double quiet = Math.Max(.001, reference * .2);
        for (int index = 0; index < levels.Length; index++)
        {
            if (levels[index] > quiet) continue;
            int first = index;
            while (index + 1 < levels.Length && levels[index + 1] <= quiet) index++;
            if (first > 0 && index + 1 < levels.Length && (index - first + 1) * windowSeconds >= Math.Max(.4, 60 / bpm))
                yield return new[] { (first + 1) * windowSeconds, (index + 1) * windowSeconds };
        }
    }

    internal double EarlySeconds(double earliest, double planned, double bpm)
    {
        if (planned <= earliest || levels.Length == 0) return planned;
        int begin = Math.Max(0, (int)Math.Floor(earliest / windowSeconds));
        int end = Math.Min(levels.Length, (int)Math.Ceiling(planned / windowSeconds));
        if (begin >= end) return planned;
        int hold = Math.Max(1, (int)Math.Ceiling(Math.Max(1, 120 / bpm) / windowSeconds));
        // Relative quiet works for softly mastered stems; the absolute floor
        // also catches a silent or nearly absent lane without normalization.
        int referenceStart = Math.Max(0, begin - (int)Math.Ceiling(30 / windowSeconds));
        double[] reference = levels.Skip(referenceStart).Take(Math.Min(levels.Length, end + hold) - referenceStart).OrderBy(value => value).ToArray();
        double quiet = Math.Max(.001, reference[(reference.Length - 1) * 4 / 5] * .04);
        int quietStart = end;
        for (int index = end - 1; index >= begin && levels[index] <= quiet; index--) quietStart = index;
        double result = planned;
        if (quietStart < end && quietStart + hold <= levels.Length && levels.Skip(quietStart).Take(Math.Max(hold, end - quietStart)).All(value => value <= quiet)) result = Math.Max(earliest, quietStart * windowSeconds);

        // Beat-sized maxima retain drum transients. A fade must decline over
        // at least one bar/two seconds and stay down through the planned swap.
        int beat = Math.Max(1, (int)Math.Round(60 / bpm / windowSeconds));
        int fade = Math.Max(4, (int)Math.Ceiling(2 / (beat * windowSeconds)));
        int first = begin / beat, last = (end + beat - 1) / beat;
        double[] envelope = new double[(levels.Length + beat - 1) / beat];
        for (int index = 0; index < levels.Length; index++) envelope[index / beat] = Math.Max(envelope[index / beat], levels[index]);
        for (int index = first; index < last && index + fade < envelope.Length; index++)
        {
            double initial = envelope[index];
            if (initial <= quiet * 2 || envelope[index + 1] >= initial * .97 || envelope[index + fade] > initial * .8) continue;
            bool falling = true; int declines = 0;
            for (int step = index + 1; step <= Math.Max(index + fade, last - 1); step++)
            {
                if (envelope[step] > initial * 1.05 || (step <= index + fade && envelope[step] > envelope[step - 1] * 1.08)) { falling = false; break; }
                if (step <= index + fade && envelope[step] < envelope[step - 1] * .98) declines++;
            }
            if (falling && declines >= fade / 2) { result = Math.Min(result, Math.Max(earliest, index * beat * windowSeconds)); break; }
        }
        return Math.Min(planned, result);
    }
    internal double BoundaryCost(double at, int lane, double bpm, bool incoming = false)
    {
        if (reference == 0) return 0;
        int index = Math.Max(0, Math.Min(levels.Length - 1, (int)Math.Floor(at / windowSeconds)));
        double level = Math.Max(levels[index], index > 0 ? levels[index - 1] : levels[index]);
        if (lane == 0)
        {
            double quiet = Math.Max(.001, reference * .2);
            if (incoming && level > quiet)
            {
                int lead = index - 1;
                while (lead >= 0 && levels[lead] <= quiet) lead--;
                if ((index - lead - 1) * windowSeconds >= Math.Max(.4, 60 / bpm) && levels[index] > quiet && at - index * windowSeconds < windowSeconds) return 0;
            }
            if (level > quiet) return 1000 + level / reference * 100;
            int first = index, last = index;
            while (first > 0 && levels[first - 1] <= quiet) first--;
            while (last + 1 < levels.Length && levels[last + 1] <= quiet) last++;
            // Word-sized dips are not verse boundaries. Prefer a pause of at
            // least a beat, with enough quiet around the 20 ms audio join.
            if ((last - first + 1) * windowSeconds < Math.Max(.4, 60 / bpm)) return 500;
            return level / reference;
        }
        // Drum/other peaks matter more than bass, but rhythmic valleys may
        // be shorter than a vocal phrase break.
        double threshold = lane == 2 ? .85 : .6, weight = lane == 2 ? 2 : 20;
        return Math.Max(0, level / reference - threshold) * weight;
    }
    internal IEnumerable<double> PhraseBoundaries(double bpm)
    {
        double quiet = Math.Max(.001, reference * .2);
        for (int index = 0; index < levels.Length; index++)
        {
            if (levels[index] > quiet) continue;
            int first = index;
            while (index + 1 < levels.Length && levels[index + 1] <= quiet) index++;
            if ((index - first + 1) * windowSeconds < Math.Max(.4, 60 / bpm)) continue;
            yield return Math.Min(index, first + 1) * windowSeconds;
            yield return (index + 1) * windowSeconds;
        }
    }
}

internal sealed class AutoStemTransitionPlan
{
    internal readonly long[] CutFrames = new long[4], StartFrames = new long[4];
}

internal static class AutoStemTransitions
{
    internal static long? SnapEditor(double seconds, int lane, bool incoming, AutoAudioDynamics dynamics, AutoTransitionTiming timing, bool snap = true)
    {
        if (Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return null;
        double wanted = Math.Max(0, Math.Min(timing.TotalFrames, seconds * timing.Rate));
        if (!snap) return (long)Math.Round(wanted);
        // Editor snapping is a convenience only: both range edges, ramp beats,
        // coincident lane points, and either IN/OUT ordering remain available.
        var candidates = new List<long> { 0, timing.TotalFrames };
        double bpm = incoming ? timing.IncomingBpm : timing.ToBpm, beatFrames = 60 / bpm * timing.Rate;
        double firstBeat = incoming ? Math.Ceiling(-timing.RampFrames / beatFrames) : 0;
        for (double index = firstBeat; ; index++)
        {
            long frame = timing.RampFrames + (long)Math.Round(index * beatFrames);
            if (frame > timing.TotalFrames) break;
            candidates.Add(frame);
        }
        if (!incoming)
            for (double source = 0; source < timing.SourceRampSeconds; source += 60 / timing.FromBpm)
                candidates.Add((long)Math.Round(timing.ElapsedAtSource(source) * timing.Rate));
        if (lane == 0 && dynamics != null)
            candidates.AddRange(dynamics.PhraseBoundaries(bpm).Select(at => (long)Math.Round(at * timing.Rate)));
        return candidates.Where(frame => frame >= 0 && frame <= timing.TotalFrames).Distinct().OrderBy(frame => Math.Abs(frame - wanted)).ThenBy(frame => frame).First();
    }
    internal static long? Snap(double seconds, int lane, bool incoming, AutoAudioDynamics dynamics, AutoTransitionTiming timing, long minimum, long maximum, IEnumerable<long> reserved)
    {
        if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || minimum > maximum) return null;
        minimum = Math.Max(timing.RampFrames, minimum);
        maximum = Math.Min(timing.TotalFrames - Math.Max(1, timing.Rate / 10), maximum);
        if (minimum > maximum) return null;
        double bpm = incoming ? timing.IncomingBpm : timing.ToBpm, beat = 60 / bpm;
        long[] occupied = reserved.ToArray(); long gap = Math.Max(1, timing.Rate / 12);
        var beats = new List<long>();
        double firstBeat = Math.Ceiling((minimum - timing.RampFrames) / (beat * timing.Rate));
        for (double index = firstBeat; ; index++)
        {
            long frame = timing.RampFrames + (long)Math.Round(index * beat * timing.Rate);
            if (frame > maximum) break;
            beats.Add(frame);
        }
        var candidates = new List<long>(beats);
        if (lane == 0 && dynamics != null) candidates.AddRange(dynamics.PhraseBoundaries(bpm).Select(at => (long)Math.Round(at * timing.Rate)));
        candidates = candidates.Where(frame => frame >= minimum && frame <= maximum && occupied.All(other => Math.Abs(frame - other) >= gap)).Distinct().ToList();
        if (lane == 0 && dynamics != null)
        {
            var phrases = candidates.Where(frame => dynamics.BoundaryCost(frame / (double)timing.Rate, lane, bpm, incoming) < 500).ToList();
            if (phrases.Count != 0) candidates = phrases;
        }
        if (candidates.Count == 0) return null;
        double wanted = Math.Max(minimum, Math.Min(maximum, seconds * timing.Rate));
        return candidates.OrderBy(frame => Math.Abs(frame - wanted) / timing.Rate + (dynamics == null ? 0 : Math.Min(2, dynamics.BoundaryCost(frame / (double)timing.Rate, lane, bpm, incoming)) * beat * .05)).ThenBy(frame => frame).First();
    }
    internal static AutoStemTransitionPlan Plan(AutoAudioDynamics[] outgoing, AutoAudioDynamics[] incoming, AutoTransitionTiming timing, CancellationToken cancellation)
    {
        var plan = new AutoStemTransitionPlan(); var reserved = new List<long>();
        long first = timing.RampFrames, last = timing.TotalFrames - Math.Max(1, timing.Rate / 10);
        long gap = Math.Max(1, (long)Math.Round(Math.Max(.08, Math.Min(.25, 30 / timing.ToBpm)) * timing.Rate));
        gap = Math.Min(gap, Math.Max(1, (last - first) / 8));
        int[] priority = new[] { 0 }.Concat(timing.Order.Where(lane => lane == 1 || lane == 3)).Concat(new[] { 2 }).ToArray();
        foreach (int lane in priority)
        {
            cancellation.ThrowIfCancellationRequested();
            double desired = outgoing[lane].EarlySeconds(first / (double)timing.Rate, timing.SwapFrame(Array.IndexOf(timing.Order, lane)) / (double)timing.Rate, timing.ToBpm);
            long step = Math.Max(1, Math.Min(gap / 2, (long)Math.Round(outgoing[lane].WindowSeconds * timing.Rate / 2)));
            var candidates = new List<long>();
            for (long frame = first; frame <= last; frame += step) candidates.Add(frame);
            candidates.Add(Math.Max(first, Math.Min(last, (long)Math.Round(desired * timing.Rate))));
            candidates.Add(last);
            Func<long, bool> free = frame => reserved.All(other => Math.Abs(frame - other) >= gap);
            Func<long, double> distance = frame => Math.Abs(frame / (double)timing.Rate - desired) / Math.Max(.5, 240 / timing.ToBpm);
            long cut, start;
            if (lane == 0)
            {
                // Preserve the outgoing phrase first. The incoming verse may
                // wait in silence until its own quiet lead-in or phrase ending.
                cut = candidates.Where(free).OrderBy(frame => outgoing[lane].BoundaryCost(frame / (double)timing.Rate, lane, timing.ToBpm) + distance(frame) * .1).First();
                var arrivals = candidates.Where(frame => frame >= cut && free(frame) && (frame == cut || frame - cut >= gap)).ToList();
                start = arrivals.OrderBy(frame => incoming[lane].BoundaryCost(frame / (double)timing.Rate, lane, timing.IncomingBpm, true) + (frame - cut) / (double)timing.Rate * .02).First();
            }
            else
            {
                // Less strict lanes usually share a single quiet join; moving
                // them must never displace a higher-priority vocal boundary.
                cut = start = candidates.Where(free).OrderBy(frame => outgoing[lane].BoundaryCost(frame / (double)timing.Rate, lane, timing.ToBpm) + incoming[lane].BoundaryCost(frame / (double)timing.Rate, lane, timing.IncomingBpm) + distance(frame)).First();
            }
            plan.CutFrames[lane] = cut; plan.StartFrames[lane] = start;
            reserved.Add(cut); if (start != cut) reserved.Add(start);
        }
        return plan;
    }
}
