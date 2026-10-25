using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

// Analyze source audio off the device thread. Drums choose the splice;
// vocals have their own phrase exit and entry around that instrumental join.
internal sealed class AutoReplayAnalysis
{
    private readonly AutoAudioDynamics[] stems;
    private sealed class ReplayPlan
    {
        internal double Bpm, Phase, TransitionStart;
        internal int Rate;
        internal long End, Position;
        internal bool Early;
        internal AutoLoopTiming Loop;
        internal bool Matches(double bpm, double phase, int rate, long end, long position, double start, bool early)
        {
            return Bpm == bpm && Phase == phase && Rate == rate && End == end && Position == position && TransitionStart == start && Early == early;
        }
    }
    private readonly List<ReplayPlan> plans = new List<ReplayPlan>();
    private AutoReplayAnalysis(AutoAudioDynamics[] values) { stems = values; }
    internal static AutoReplayAnalysis Read(string[] files, PlaybackTransform[] transforms, CancellationToken token)
    {
        try
        {
            var values = new AutoAudioDynamics[4];
            for (int lane = 0; lane < 4; lane++)
            {
                token.ThrowIfCancellationRequested();
                values[lane] = AutoAudioDynamics.Read(files[lane], token, lane >= 2, lane == 1 ? .01 : .1).ForTimeline(transforms == null ? null : transforms[lane]);
            }
            return new AutoReplayAnalysis(values);
        }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // Even if analysis is unavailable, never fall back to replaying the intro.
    internal static AutoLoopTiming Fallback(double bpm, double phase, int rate, long endFrame, long position, double transitionStart)
    {
        var basis = new AutoLoopTiming(bpm, phase, rate, endFrame, true);
        double beat = 60 / bpm, bar = beat * 4, end = basis.EndFrame / (double)rate, now = position / (double)rate;
        if (end <= now + .1) end = endFrame / (double)rate;
        double first = phase + bar;
        if (first >= end - beat) first = phase + beat;
        if (first >= end - beat / 2) first = Math.Max(1.0 / rate, end / 3);
        double latest = transitionStart >= first + beat ? Math.Min(end - beat, transitionStart - beat) : end - beat;
        double start = Math.Max(first, Math.Min(latest, phase + Math.Floor((end - 4 * bar - phase) / bar) * bar));
        start = Math.Max(1.0 / rate, Math.Min(end - 1.0 / rate, start));
        double cut = Math.Min(end - .05, Math.Max(now + .02, end - beat));
        double entry = Math.Min(end - .05, start + Math.Min(bar, Math.Max(.05, (end - start) / 2)));
        return new AutoLoopTiming(basis, (long)Math.Round(start * rate), Math.Max(2, (long)Math.Round(end * rate)), Math.Max(0, (long)Math.Round(cut * rate)), Math.Max(0, (long)Math.Round((entry - .05) * rate)), endFrame);
    }

    internal AutoLoopTiming Plan(double bpm, double phase, int rate, long endFrame, long position, double transitionStart, bool early = false)
    {
        // Incoming mixers stay parked at the same position until handoff. The
        // autoplay UI timer asks for this same protection every 100 ms. Analysis
        // and loop timings are immutable, so retain a small set of exact results
        // rather than repeating the drum/phrase search on the scrolling thread.
        lock (plans)
        {
            AutoLoopTiming cached = FindPlan(bpm, phase, rate, endFrame, position, transitionStart, early);
            if (cached != null) return cached;
        }
        // Do not hold the cache lock during analysis: a background preparation
        // must not make a UI cache hit wait for a different, expensive search.
        AutoLoopTiming result = BuildPlan(bpm, phase, rate, endFrame, position, transitionStart, early);
        lock (plans)
        {
            AutoLoopTiming cached = FindPlan(bpm, phase, rate, endFrame, position, transitionStart, early);
            if (cached != null) return cached;
            if (plans.Count == 8) plans.RemoveAt(0);
            plans.Add(new ReplayPlan { Bpm = bpm, Phase = phase, Rate = rate, End = endFrame, Position = position, TransitionStart = transitionStart, Early = early, Loop = result });
        }
        return result;
    }
    private AutoLoopTiming FindPlan(double bpm, double phase, int rate, long endFrame, long position, double transitionStart, bool early)
    {
        for (int index = 0; index < plans.Count; index++)
        {
            ReplayPlan plan = plans[index];
            if (!plan.Matches(bpm, phase, rate, endFrame, position, transitionStart, early)) continue;
            plans.RemoveAt(index); plans.Add(plan); return plan.Loop;
        }
        return null;
    }
    private AutoLoopTiming BuildPlan(double bpm, double phase, int rate, long endFrame, long position, double transitionStart, bool early)
    {
        AutoLoopTiming fallback = Fallback(bpm, phase, rate, endFrame, position, transitionStart);
        double beat = 60 / bpm, bar = beat * 4, end = endFrame / (double)rate;
        double now = position / (double)rate, earliest = now + .15;
        if (end <= earliest || end - phase < 2 * beat) return fallback;
        double firstStart = phase + bar;
        if (firstStart >= end - bar) firstStart = phase + beat;
        if (firstStart >= end - beat) return fallback;
        double limit = transitionStart >= earliest + beat ? Math.Min(end, transitionStart) : end;
        double wanted = early ? Math.Min(limit, earliest + bar) : limit;
        double firstFinish = Math.Max(earliest, wanted - 8 * bar), lastFinish = Math.Min(limit, wanted + 8 * bar);
        var points = new List<double>(stems[1].DrumBoundaries().Where(at => at >= firstStart && at <= lastFinish));
        for (double at = phase + beat; at <= lastFinish; at += beat) points.Add(at);
        double[] candidates = points.Select(at => Math.Round(at * rate) / rate).Distinct().OrderBy(at => at).ToArray();
        var drumCosts = candidates.ToDictionary(at => at, at => stems[1].DrumBoundaryCost(at, bpm));
        double[][] gaps = stems[0].VocalGaps(bpm).ToArray();
        double best = Double.MaxValue; AutoLoopTiming chosen = null;
        foreach (double finish in candidates.Where(at => at >= firstFinish && at <= lastFinish))
        {
            double[] outgoing = gaps.LastOrDefault(gap => gap[0] >= now + .05 && gap[0] <= finish - .05 && gap[0] >= finish - 4 * bar);
            double cut = outgoing == null ? phase + Math.Floor((finish - .05 - phase) / beat) * beat : outgoing[0];
            if (cut < now + .02 || cut >= finish - .05) continue;
            double lastStart = finish - beat;
            if (transitionStart >= firstStart + beat) lastStart = Math.Min(lastStart, transitionStart - beat);
            double exitCost = drumCosts[finish];
            // Bound matching work on long songs while retaining grid fallbacks.
            double[] starts = candidates.Where(at => at >= firstStart && at <= lastStart)
                .Where(at => Math.Abs((finish - at) / beat - Math.Round((finish - at) / beat)) * beat <= .035)
                .OrderBy(at => drumCosts[at]).ThenBy(at => Math.Abs(finish - at - 8 * bar)).Take(128).ToArray();
            foreach (double start in starts)
            {
                double entryLimit = Math.Min(finish - .1, transitionStart > start + beat ? transitionStart - .05 : finish - .1);
                double[] incoming = gaps.FirstOrDefault(gap => gap[1] >= start + .1 && gap[1] <= entryLimit && gap[1] <= start + 8 * bar);
                double entry = incoming == null ? Math.Min(start + bar, start + (finish - start) / 2) : incoming[1];
                if (entry > entryLimit || entry >= cut) continue;
                double cost = exitCost + drumCosts[start];
                for (int step = -8; step < 8; step++)
                {
                    double offset = step * beat / 4;
                    cost += stems[1].Difference(finish + offset, start + offset) * .6;
                    for (int lane = 2; lane < 4; lane++) cost += stems[lane].Difference(finish + offset, start + offset) / 16;
                }
                cost += (outgoing == null ? 1 : 0) + (incoming == null ? 1 : 0);
                cost += Math.Abs(finish - wanted) / bar * .08;
                cost += Math.Abs((finish - start) / bar - 8) * .02;
                cost += Math.Abs((finish - start) / bar - Math.Round((finish - start) / bar)) * .5;
                if (cost >= best) continue;
                best = cost;
                chosen = new AutoLoopTiming(fallback, (long)Math.Round(start * rate), (long)Math.Round(finish * rate), (long)Math.Round(cut * rate), (long)Math.Round((entry - .05) * rate), endFrame);
            }
        }
        return chosen ?? fallback;
    }
}
