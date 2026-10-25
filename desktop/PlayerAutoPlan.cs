using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed class AutoPlaySettings
{
    internal double Below = 10, Above = 10, RampBars = 8, StemBars = 4, EndCutBars;
    internal bool EarlyStemTransitions;
    internal int[] Order = { 1, 2, 3, 0 };
    internal AutoPlaySettings Copy() { return new AutoPlaySettings { Below = Below, Above = Above, RampBars = RampBars, StemBars = StemBars, EndCutBars = EndCutBars, EarlyStemTransitions = EarlyStemTransitions, Order = (int[])Order.Clone() }; }
    internal void Validate()
    {
        double[] values = { Below, Above, RampBars, StemBars, EndCutBars };
        if (values.Any(value => Double.IsNaN(value) || Double.IsInfinity(value)) || Below < 0 || Below > 270 || Above < 0 || Above > 270 || RampBars < 0 || RampBars > 64 || StemBars < .25 || StemBars > 64 || EndCutBars < 0 || EndCutBars > 64 || Order == null || Order.Length != 4 || !Order.OrderBy(lane => lane).SequenceEqual(new[] { 0, 1, 2, 3 })) throw new ArgumentException("Choose valid BPM limits, bar counts, and each stem exactly once.");
    }
    internal object Saved() { return new { below = Below, above = Above, rampBars = RampBars, stemBars = StemBars, endCutBars = EndCutBars, earlyStemTransitions = EarlyStemTransitions, order = Order }; }
    internal static AutoPlaySettings Read(Dictionary<string, object> saved)
    {
        var settings = new AutoPlaySettings();
        if (saved == null) return settings;
        try
        {
            object value;
            if (saved.TryGetValue("below", out value)) settings.Below = Convert.ToDouble(value);
            if (saved.TryGetValue("above", out value)) settings.Above = Convert.ToDouble(value);
            if (saved.TryGetValue("rampBars", out value)) settings.RampBars = Convert.ToDouble(value);
            if (saved.TryGetValue("stemBars", out value)) settings.StemBars = Convert.ToDouble(value);
            if (saved.TryGetValue("endCutBars", out value)) settings.EndCutBars = Convert.ToDouble(value);
            if (saved.TryGetValue("earlyStemTransitions", out value)) settings.EarlyStemTransitions = Convert.ToBoolean(value);
            if (saved.TryGetValue("order", out value)) settings.Order = ((object[])value).Select(Convert.ToInt32).ToArray();
            settings.Validate(); return settings;
        }
        catch { return new AutoPlaySettings(); }
    }
}

internal static class AutoTempo
{
    internal static double? InRange(double current, double next, AutoPlaySettings settings)
    {
        if (!Valid(current) || !Valid(next)) return null;
        double lower = Math.Max(30, current - settings.Below), upper = Math.Min(300, current + settings.Above);
        double? best = null; double distance = Double.PositiveInfinity;
        // Compare the next song at half/double time and further powers of two.
        // Keep its audio at native speed; this is the outgoing ramp's target.
        double equivalent = next;
        while (equivalent / 2 >= 30) equivalent /= 2;
        for (; equivalent <= 300; equivalent *= 2)
        {
            if (equivalent < lower || equivalent > upper) continue;
            double change = Math.Abs(Math.Log(equivalent / current));
            if (change < distance) { best = equivalent; distance = change; }
        }
        return best;
    }
    private static bool Valid(double bpm) { return !Double.IsNaN(bpm) && !Double.IsInfinity(bpm) && bpm >= 30 && bpm <= 300; }
}

internal sealed class AutoShuffle
{
    private readonly HashSet<string> played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Random random;
    internal AutoShuffle(Random source = null) { random = source ?? new Random(); }
    internal SongInfo Choose(IEnumerable<SongInfo> songs, SongInfo current, double bpm, AutoPlaySettings settings, Func<SongInfo, bool> usable = null)
    {
        settings.Validate();
        var eligible = songs.Where(song => song.HasStems && song.Bpm.HasValue && AutoTempo.InRange(bpm, song.Bpm.Value, settings).HasValue && !String.Equals(song.PlaybackKey, current.PlaybackKey, StringComparison.OrdinalIgnoreCase) && (usable == null || usable(song))).ToList();
        var fresh = eligible.Where(song => !played.Contains(song.PlaybackKey)).ToList();
        return fresh.Count == 0 ? null : fresh[random.Next(fresh.Count)];
    }
    internal void Played(SongInfo song) { if (song != null) played.Add(song.PlaybackKey); }
    internal void Reset() { played.Clear(); }
}

// Repeat complete bars from the analyzed beat phase, rather than including
// the partial intro or tail at the file's physical edges.
internal sealed class AutoLoopTiming
{
    internal readonly long StartFrame, EndFrame;
    internal readonly long VocalOutFrame, VocalInFrame;
    internal readonly bool SmoothReplay;
    internal readonly long AvailableEndFrame;
    private readonly double phase, interval;
    internal AutoLoopTiming(double bpm, double beatOffset, int rate, long endFrame, bool outro = false)
    {
        phase = Math.Max(0, beatOffset) * rate;
        interval = 240.0 * rate / bpm;
        double end = phase + Math.Floor((endFrame - phase) / interval + .000000001) * interval;
        // A very short excerpt can still repeat complete beats.
        if (end <= phase) { interval /= 4; end = phase + Math.Floor((endFrame - phase) / interval + .000000001) * interval; }
        if (end <= phase) { phase = 0; interval = endFrame; end = endFrame; }
        EndFrame = Math.Max(1, Math.Min(endFrame, (long)Math.Round(end)));
        AvailableEndFrame = endFrame;
        StartFrame = Math.Max(0, Math.Min(EndFrame - 1, (long)Math.Round(outro ? end - interval : phase)));
    }
    internal long NextEndFrame(long position)
    {
        if (SmoothReplay) return EndFrame;
        return Math.Min(EndFrame, (long)Math.Round(phase + (Math.Floor((position - phase) / interval + .000000001) + 1) * interval));
    }
    internal AutoLoopTiming(AutoLoopTiming basis, long start, long end, long vocalOut, long vocalIn, long? availableEnd = null)
    {
        phase = basis.phase; interval = basis.interval;
        StartFrame = start; EndFrame = end; VocalOutFrame = vocalOut; VocalInFrame = vocalIn; SmoothReplay = true;
        AvailableEndFrame = availableEnd ?? basis.AvailableEndFrame;
    }
}

// Four beats per bar. The ramp is linear in musical beat position; its wall
// time is the sum of each small input interval divided by its tempo ratio.
internal sealed class AutoTransitionTiming
{
    internal readonly double FromBpm, ToBpm, IncomingBpm, SourceRampSeconds, RampSeconds, IntervalSeconds, EndCutSeconds;
    internal readonly int Rate, Steps;
    internal readonly long RampFrames, IntervalFrames, LeadFrames;
    internal long TotalFrames { get; private set; }
    internal readonly int[] Order;
    internal AutoTransitionTiming(double from, double to, int rate, AutoPlaySettings settings, double leadSeconds = 0, double? incomingBpm = null)
    {
        settings.Validate();
        double incoming = incomingBpm ?? to;
        if (from < 30 || from > 300 || to < 30 || to > 300 || incoming < 30 || incoming > 300 || rate <= 0 || Double.IsNaN(from) || Double.IsNaN(to) || Double.IsNaN(incoming) || Double.IsNaN(leadSeconds) || Double.IsInfinity(leadSeconds) || leadSeconds < 0) throw new ArgumentException("Auto play needs known BPMs between 30 and 300 and a valid transition lead.");
        FromBpm = from; ToBpm = to; IncomingBpm = incoming; Rate = rate; Order = (int[])settings.Order.Clone();
        SourceRampSeconds = settings.RampBars * 240 / from;
        EndCutSeconds = settings.EndCutBars * 240 / from;
        Steps = Math.Max(1, (int)Math.Ceiling(settings.RampBars * 64));
        for (int step = 0; step < Steps; step++) RampSeconds += SourceRampSeconds / Steps / Ratio(step);
        RampFrames = (long)Math.Round(RampSeconds * rate);
        IntervalSeconds = settings.StemBars * 240 / IncomingBpm; IntervalFrames = Math.Max(1, (long)Math.Round(IntervalSeconds * rate));
        LeadFrames = (long)Math.Round(leadSeconds * rate);
        TotalFrames = RampFrames + LeadFrames + IntervalFrames * 3 + Math.Max(1, rate / 20);
    }
    private double Ratio(int step) { return 1 + (ToBpm / FromBpm - 1) * step / Steps; }
    internal long SwapFrame(int position) { return RampFrames + LeadFrames + position * IntervalFrames; }
    internal void SetTotalFrames(long frames)
    {
        if (frames < SwapFrame(3) + Math.Max(1, Rate / 20)) throw new ArgumentException("The transition must contain every planned stem change.");
        TotalFrames = frames;
    }
    internal double IncomingSeconds { get { return (TotalFrames - RampFrames) / (double)Rate; } }
    internal long IncomingEndFrame(double beatOffset) { return (long)Math.Round(beatOffset * Rate) + TotalFrames - RampFrames; }
    internal double SourceSeconds { get { return SourceRampSeconds + IncomingSeconds * ToBpm / FromBpm; } }
    internal double SourceAt(double elapsed)
    {
        double remaining = Math.Max(0, elapsed), source = 0;
        for (int step = 0; step < Steps && remaining > 0; step++)
        {
            double duration = SourceRampSeconds / Steps / Ratio(step), used = Math.Min(remaining, duration);
            source += used * Ratio(step); remaining -= used;
        }
        return source + remaining * ToBpm / FromBpm;
    }
    internal long IncomingBarFrames { get { return Math.Max(1, (long)Math.Round(240.0 * Rate / IncomingBpm)); } }
    internal double ElapsedAtSource(double seconds)
    {
        double remaining = Math.Max(0, seconds), elapsed = 0;
        for (int step = 0; step < Steps && remaining > 0; step++)
        {
            double used = Math.Min(remaining, SourceRampSeconds / Steps);
            elapsed += used / Ratio(step); remaining -= used;
        }
        return elapsed + remaining * FromBpm / ToBpm;
    }
    internal long IncomingSourceFrame(long frame)
    {
        long relative = frame - RampFrames;
        return relative < 0 ? (relative % IncomingBarFrames + IncomingBarFrames) % IncomingBarFrames : relative;
    }
    internal double BpmAt(double elapsed)
    {
        if (elapsed >= RampSeconds) return ToBpm;
        double position = Math.Max(0, elapsed);
        for (int step = 0; step < Steps; step++)
        {
            double duration = SourceRampSeconds / Steps / Ratio(step);
            if (position <= duration) return FromBpm * Ratio(step);
            position -= duration;
        }
        return ToBpm;
    }
    internal double SourceStart(double duration, double beatOffset)
    {
        double bar = 240 / FromBpm;
        return Math.Max(0, beatOffset + Math.Floor((SourceEnd(duration) - SourceSeconds - beatOffset) / bar) * bar);
    }
    internal static double RequestedStart(double position, double bpm, double phase, double end)
    {
        // Prefer a bar, but never wait over five seconds for a downbeat.
        // Even at 30 BPM the next beat is at most two seconds away.
        double earliest = position + .05, bar = 240 / bpm, beat = 60 / bpm;
        double start = phase + Math.Ceiling((earliest - phase) / bar) * bar;
        if (start > position + 5 || start > end) start = phase + Math.Ceiling((earliest - phase) / beat) * beat;
        return Math.Max(position, Math.Min(end, start));
    }
    internal double SourceEnd(double duration) { return Math.Max(Math.Min(duration, 240 / FromBpm), duration - EndCutSeconds); }
    internal long SourceEndFrame(long availableFrames) { return Math.Max(1, (long)Math.Round(SourceEnd(availableFrames / (double)Rate) * Rate)); }
    internal string RampFilter(string project, double start, long availableFrames, int lane, bool honorEndCut = true)
    {
        StringBuilder commands = new StringBuilder();
        for (int step = 0; step <= Steps; step++) commands.Append(Number(SourceRampSeconds * step / Steps)).Append(" rubberband@ramp tempo ").Append(Number(Ratio(step))).Append(";\n");
        string hash;
        using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(commands.ToString()))).Replace("-", "").ToLowerInvariant();
        string relative = "data/auto-play/" + hash + ".commands";
        Directory.CreateDirectory(PlayerData.Data(project, "auto-play")); File.WriteAllText(Path.Combine(PlayerData.Root(project), relative.Replace('/', Path.DirectorySeparatorChar)), commands.ToString());
        long endFrame = honorEndCut ? SourceEndFrame(availableFrames) : availableFrames, startFrame = Math.Min(endFrame - 1, Math.Max(0, (long)Math.Round(start * Rate))), size = Math.Max(1, endFrame - startFrame);
        // Loop the outro if a short song cannot contain the configured transition.
        return "aresample=" + Rate + ",atrim=start_sample=" + startFrame + ":end_sample=" + endFrame + ",asetpts=PTS-STARTPTS,aloop=loop=-1:size=" + size + ",atrim=end_sample=" + (long)Math.Ceiling(SourceSeconds * Rate + Rate * .1) + ",asetnsamples=n=256:p=0,asendcmd=f=" + relative + ",rubberband@ramp=tempo=" + Number(SourceRampSeconds == 0 ? ToBpm / FromBpm : 1) + ":pitch=1:transients=" + (lane == 1 ? "crisp" : "mixed") + ":formant=preserved:pitchq=quality:channels=together,apad,atrim=end_sample=" + TotalFrames + ",asetpts=PTS-STARTPTS";
    }
    internal string IncomingFilter(double beatOffset)
    {
        string body = "aresample=" + Rate + ",atrim=start_sample=" + (long)Math.Round(beatOffset * Rate) + ",asetpts=PTS-STARTPTS";
        if (RampFrames > 0)
        {
            // A beat-aligned first bar fills the previously silent ramp. The
            // default IN points still start after it, with identical song phase.
            long phase = IncomingSourceFrame(0);
            body += ",asplit=2[body][intro];[intro]apad,atrim=end_sample=" + IncomingBarFrames + ",aloop=loop=-1:size=" + IncomingBarFrames + ",atrim=start_sample=" + phase + ":end_sample=" + (phase + RampFrames) + ",asetpts=PTS-STARTPTS[lead];[lead][body]concat=n=2:v=0:a=1";
        }
        return body + ",apad,atrim=end_sample=" + TotalFrames + ",asetpts=PTS-STARTPTS";
    }
    private static string Number(double value) { return value.ToString("0.########", CultureInfo.InvariantCulture); }
}
