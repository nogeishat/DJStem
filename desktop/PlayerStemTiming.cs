using System;
using System.Threading;
using NAudio.Wave;

// Source time is independent of transport time. Loops never extend TotalFrames.
internal sealed class StemTiming
{
    internal double Shift, LoopStart, LoopEnd, LoopAnchor, LoopShift;
    internal bool Looping { get { return LoopEnd > LoopStart; } }
    internal bool Changed { get { return Looping || Math.Abs(Shift) > .000001; } }
    internal StemTiming Copy(double scale = 1)
    {
        return new StemTiming { Shift = Shift * scale, LoopStart = LoopStart * scale, LoopEnd = LoopEnd * scale, LoopAnchor = LoopAnchor * scale, LoopShift = LoopShift * scale };
    }
    internal static double SnapShift(double shift, double bpm) { return Math.Round(shift * bpm / 60, MidpointRounding.AwayFromZero) * 60 / bpm; }
    internal static double BarStart(double at, double bpm, double phase, double duration)
    {
        double bar = 240 / bpm, first = Math.Max(0, phase), last = first + Math.Floor((duration - bar - first) / bar) * bar;
        if (last < first) throw new InvalidOperationException("This stem is too short for a complete bar.");
        return Math.Max(first, Math.Min(last, phase + Math.Round((at - phase) / bar, MidpointRounding.AwayFromZero) * bar));
    }
    internal void StartLoop(double at, double now, double bpm, double phase, double duration)
    {
        LoopStart = BarStart(at, bpm, phase, duration); LoopEnd = LoopStart + 240 / bpm;
        LoopAnchor = Math.Max(0, phase + Math.Ceiling((now - phase) / (240 / bpm) - .0000001) * (240 / bpm)); LoopShift = Shift;
    }
    internal void ExtendLoop(double end, double bpm, double duration)
    {
        if (!Looping) return;
        double bar = 240 / bpm;
        int maximum = Math.Max(1, (int)Math.Floor((duration - LoopStart) / bar + .0000001));
        int bars = Math.Max(1, Math.Min(maximum, (int)Math.Round((end - LoopStart) / bar, MidpointRounding.AwayFromZero)));
        LoopEnd = LoopStart + bars * bar;
    }
    internal long SourceFrame(long transport, int rate, out long remaining)
    {
        long anchor = (long)Math.Round(LoopAnchor * rate);
        if (Looping && transport >= anchor)
        {
            long length = Math.Max(1, (long)Math.Round((LoopEnd - LoopStart) * rate));
            long phase = (transport - anchor - (long)Math.Round((Shift - LoopShift) * rate)) % length;
            if (phase < 0) phase += length;
            remaining = length - phase;
            return (long)Math.Round(LoopStart * rate) + phase;
        }
        remaining = Looping ? Math.Max(1, anchor - transport) : Int64.MaxValue;
        return transport - (long)Math.Round(Shift * rate);
    }
    // Feed the edited timeline into the existing pitch-preserving autoplay ramp.
    internal string Filter(int rate, long frames)
    {
        string shift = "aresample=" + rate;
        long shifted = (long)Math.Round(Shift * rate);
        if (shifted > 0) shift += ",adelay=" + shifted + "S:all=1";
        else if (shifted < 0) shift += ",atrim=start_sample=" + (-shifted) + ",asetpts=PTS-STARTPTS";
        shift += ",apad,atrim=end_sample=" + frames + ",asetpts=PTS-STARTPTS";
        long anchor = Math.Max(0, Math.Min(frames, (long)Math.Round(LoopAnchor * rate)));
        if (!Looping || anchor == frames) return shift;
        long start = (long)Math.Round(LoopStart * rate), size = Math.Max(1, (long)Math.Round((LoopEnd - LoopStart) * rate));
        long phase = -(long)Math.Round((Shift - LoopShift) * rate) % size; if (phase < 0) phase += size;
        string loop = "aresample=" + rate + ",atrim=start_sample=" + start + ":end_sample=" + (start + size) + ",asetpts=PTS-STARTPTS,aloop=loop=-1:size=" + size + ",atrim=start_sample=" + phase + ":end_sample=" + (phase + frames - anchor) + ",asetpts=PTS-STARTPTS";
        if (anchor == 0) return loop;
        return "asplit=2[before][loop];[before]" + shift + ",atrim=end_sample=" + anchor + "[prefix];[loop]" + loop + "[repeat];[prefix][repeat]concat=n=2:v=0:a=1";
    }
}

internal sealed partial class StemMixer
{
    internal sealed class DisplayState
    {
        internal double Seconds, Duration;
        internal readonly StemTiming[] Timing = new StemTiming[4];
        internal readonly double[] SourceDurations = new double[4];
    }
    // A single consistent snapshot, acquired only when the audio callback is
    // idle. A missed display frame is preferable to stalling every UI control.
    internal bool TryGetDisplayState(out DisplayState state)
    {
        state = null;
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (disposed) return false;
            state = new DisplayState { Seconds = position / (double)WaveFormat.SampleRate, Duration = Duration };
            for (int lane = 0; lane < 4; lane++) { state.Timing[lane] = stemTiming[lane].Copy(); state.SourceDurations[lane] = StemFrames(lane) / (double)WaveFormat.SampleRate; }
            return true;
        }
        finally { Monitor.Exit(gate); }
    }
    private readonly StemTiming[] stemTiming = { new StemTiming(), new StemTiming(), new StemTiming(), new StemTiming() };
    private readonly int[] stemFadeFrames = new int[4];
    private readonly float[] stemLastLeft = new float[4], stemLastRight = new float[4], stemFadeLeft = new float[4], stemFadeRight = new float[4];
    private long StemFrames(int lane) { return readers[lane + 1] == null ? TotalFrames : readers[lane + 1].Length / readers[lane + 1].WaveFormat.BlockAlign; }
    internal double StemDuration(int lane) { lock (gate) return StemFrames(lane) / (double)WaveFormat.SampleRate; }
    internal StemTiming StemState(int lane) { lock (gate) return stemTiming[lane].Copy(); }
    internal void SetStemShift(int lane, double seconds)
    {
        lock (gate) stemTiming[lane].Shift = Math.Max(-StemDuration(lane), Math.Min(Duration, seconds));
    }
    internal void StartStemLoop(int lane, double at, double bpm, double phase)
    {
        lock (gate) stemTiming[lane].StartLoop(at, Seconds, bpm, phase, StemDuration(lane));
    }
    internal void ExtendStemLoop(int lane, double end, double bpm) { lock (gate) stemTiming[lane].ExtendLoop(end, bpm, StemDuration(lane)); }
    internal void RestoreStemTiming(StemTiming[] timing, double scale = 1)
    {
        lock (gate) for (int lane = 0; lane < 4; lane++) stemTiming[lane] = timing[lane].Copy(scale);
    }
    private void ReadStemSamples(int lane, float[] buffer, int count)
    {
        int source = lane + 1, written = 0, fadeLength = Math.Max(1, WaveFormat.SampleRate / 200);
        WaveStream reader = readers[source];
        if (reader == null) return;
        long sourceFrames = StemFrames(lane);
        while (written < count)
        {
            long available; long frame = stemTiming[lane].SourceFrame(position + written / 2, WaveFormat.SampleRate, out available);
            int frames = (int)Math.Min((count - written) / 2, available);
            if (frame < 0) frames = (int)Math.Min(frames, -frame);
            else if (frame < sourceFrames) frames = (int)Math.Min(frames, sourceFrames - frame);
            if (frame >= 0 && frame < sourceFrames)
            {
                if (reader.Position / reader.WaveFormat.BlockAlign != frame)
                {
                    reader.Position = frame * reader.WaveFormat.BlockAlign;
                    stemFadeFrames[lane] = fadeLength; stemFadeLeft[lane] = stemLastLeft[lane]; stemFadeRight[lane] = stemLastRight[lane];
                }
                int read = 0;
                while (read < frames * 2) { int more = sources[source].Read(buffer, written + read, frames * 2 - read); if (more == 0) break; read += more; }
            }
            for (int sample = written; sample < written + frames * 2; sample += 2)
            {
                if (stemFadeFrames[lane] > 0)
                {
                    float amount = 1f - stemFadeFrames[lane]-- / (float)fadeLength;
                    buffer[sample] = stemFadeLeft[lane] * (1 - amount) + buffer[sample] * amount;
                    buffer[sample + 1] = stemFadeRight[lane] * (1 - amount) + buffer[sample + 1] * amount;
                }
                stemLastLeft[lane] = buffer[sample]; stemLastRight[lane] = buffer[sample + 1];
            }
            written += frames * 2;
        }
    }
}
