using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

// Switch whole timelines inside one audio callback, filling the same output
// buffer across their boundary. No device restart is needed for auto play.
internal sealed class PlayerPlayback : ISampleProvider, IDisposable
{
    private readonly object gate = new object();
    private readonly Queue<StemMixer> queued = new Queue<StemMixer>();
    private readonly List<StemMixer> retired = new List<StemMixer>();
    private bool disposed;
    private StemMixer current;
    private StemMixer fadeFrom;
    private int fadeFrames;
    private float[] fadeBuffer;
    private StemMixer guardIncoming, guardedCurrent, restartedHandoff;
    private AutoLoopTiming incomingLoop;
    private AutoLoopTiming currentLoop;
    private long incomingFirstEnd, fullRestartEnd;
    private bool switchAfterLoop;
    private int restartFrames, restartCount;
    private float lastLeft, lastRight, restartLeft, restartRight;
    private readonly float[] restartTail;
    private int restartTailFrames, restartTailPosition;
    private long switchFrame;
    private long? loopStartFrame, loopEndFrame;
    internal long? LoopStartFrame { get { lock (gate) return loopStartFrame; } set { lock (gate) { loopStartFrame = value; if (!value.HasValue) { loopEndFrame = null; currentLoop = null; current.SetReplayVocals(null); } } } }
    internal bool WaitingForLoop { get { lock (gate) return switchAfterLoop; } }
    internal void SetOutroLoop(long start, long end)
    {
        lock (gate) { loopEndFrame = Math.Max(1, Math.Min(current.TotalFrames, end)); loopStartFrame = Math.Max(0, Math.Min(loopEndFrame.Value - 1, start)); }
    }
    internal void SetOutroLoop(AutoLoopTiming loop)
    {
        lock (gate)
        {
            if (currentLoop != null && currentLoop.SmoothReplay && current.ReplayVocalsCommitted && current.PositionFrames < currentLoop.EndFrame) return;
            currentLoop = loop; SetOutroLoop(loop.StartFrame, loop.EndFrame); current.SetReplayVocals(loop);
        }
    }
    internal void SetSongRestartLoop(StemMixer expected, AutoLoopTiming loop)
    {
        lock (gate)
        {
            if (current != expected) return;
            if (currentLoop != null && currentLoop.SmoothReplay && current.ReplayVocalsCommitted && current.PositionFrames < currentLoop.EndFrame) return;
            // Preserve an earlier, bar-aligned protective restart installed at
            // handoff while preparation replaces its background loading task.
            long end = !loop.SmoothReplay && guardedCurrent == current && loopEndFrame.HasValue ? Math.Min(loopEndFrame.Value, loop.EndFrame) : loop.EndFrame;
            guardedCurrent = current; fullRestartEnd = loop.EndFrame; currentLoop = loop;
            SetOutroLoop(loop.StartFrame, end); current.SetReplayVocals(loop);
        }
    }
    internal PlayerPlayback(StemMixer mixer) { current = mixer; WaveFormat = mixer.WaveFormat; restartTail = new float[Math.Max(1, WaveFormat.SampleRate / 100) * 2]; }
    public WaveFormat WaveFormat { get; private set; }
    internal StemMixer Current { get { return Volatile.Read(ref current); } }
    internal bool ReplaceCurrent(StemMixer expected, StemMixer replacement, double? positionScale = null)
    {
        lock (gate)
        {
            if (disposed || current != expected || queued.Count != 0) return false;
            if (replacement.WaveFormat.SampleRate != WaveFormat.SampleRate) throw new ArgumentException("Live tuning must preserve the output sample rate.");
            if (positionScale.HasValue) replacement.CatchUpFrame((long)Math.Round(expected.PositionFrames * positionScale.Value));
            fadeFrom = current; fadeFrames = Math.Max(1, WaveFormat.SampleRate / 50);
            retired.Add(current); current = replacement; switchFrame = replacement.TotalFrames;
            LoopStartFrame = null; ClearRestartProtection(); return true;
        }
    }
    internal int RestartCount { get { lock (gate) return restartCount; } }
    internal bool RestartedHandoff(StemMixer song) { lock (gate) return restartedHandoff == song; }
    internal void ProtectIncoming(StemMixer next, bool restart, bool guard, AutoLoopTiming loop)
    {
        lock (gate)
        {
            if (!queued.Contains(next)) return;
            guardIncoming = guard ? next : null; incomingLoop = guard ? loop : null;
            incomingFirstEnd = restart ? loop.NextEndFrame(next.PositionFrames) : loop.EndFrame;
        }
    }
    internal void TryProtectIncoming(StemMixer next, bool restart, bool guard, AutoLoopTiming loop)
    {
        if (!Monitor.TryEnter(gate)) return;
        try { ProtectIncoming(next, restart, guard, loop); }
        finally { Monitor.Exit(gate); }
    }
    internal void ClearRestartProtection()
    {
        lock (gate)
        {
            guardIncoming = null; incomingLoop = null;
            if (guardedCurrent == current && queued.Count == 0) LoopStartFrame = null;
            guardedCurrent = null;
        }
    }
    private void RepeatCurrent(long start, bool restart)
    {
        restartTailFrames = restartTailPosition = 0;
        if (currentLoop != null && currentLoop.SmoothReplay)
        {
            int frames = (int)Math.Min(restartTail.Length / 2, Math.Max(0, currentLoop.AvailableEndFrame - current.PositionFrames));
            if (frames > 0) restartTailFrames = current.Read(restartTail, 0, frames * 2) / 2;
        }
        current.SeekFrame(start);
        if (currentLoop != null) current.SetReplayVocals(currentLoop, true);
        if (restart) { restartCount++; restartedHandoff = current; }
        restartLeft = lastLeft; restartRight = lastRight;
        restartFrames = restartTailFrames > 0 ? restartTailFrames : Math.Max(1, WaveFormat.SampleRate / 200);
    }
    internal bool Schedule(StemMixer expected, long startFrame, StemMixer bridge, StemMixer next, bool restartIfLate = false, bool requireFutureStart = false)
    {
        lock (gate)
        {
            if (disposed || current != expected || queued.Count != 0) return false;
            if (requireFutureStart && current.PositionFrames > startFrame) return false;
            if (bridge.WaveFormat.SampleRate != WaveFormat.SampleRate || next.WaveFormat.SampleRate != WaveFormat.SampleRate) throw new ArgumentException("Auto play timelines must use the same sample rate.");
            switchAfterLoop = restartIfLate && (current.PositionFrames > startFrame + WaveFormat.SampleRate / 20 || current.ReplayVocalsCommitted);
            if (switchAfterLoop && !loopStartFrame.HasValue)
            {
                var fallback = AutoReplayAnalysis.Fallback(120, 0, WaveFormat.SampleRate, current.TotalFrames, current.PositionFrames, startFrame / (double)WaveFormat.SampleRate);
                SetSongRestartLoop(current, fallback);
            }
            switchFrame = Math.Min(current.TotalFrames, Math.Max(switchAfterLoop ? loopStartFrame.Value : current.PositionFrames, startFrame));
            queued.Enqueue(bridge); queued.Enqueue(next);
            if (!switchAfterLoop) LoopStartFrame = null;
            return true;
        }
    }
    internal bool CancelPending(StemMixer expected = null)
    {
        lock (gate) { if (expected != null && current != expected) return false; while (queued.Count != 0) queued.Dequeue().Dispose(); switchAfterLoop = false; LoopStartFrame = null; ClearRestartProtection(); return true; }
    }
    internal bool ReplaceScheduledBridge(StemMixer expected, StemMixer previous, StemMixer bridge, StemMixer next, long startFrame, bool requireFutureStart = false)
    {
        lock (gate)
        {
            if (disposed || current != expected || queued.Count != 2 || queued.Peek() != previous || queued.Last() != next) return false;
            if (requireFutureStart && current.PositionFrames > startFrame) return false;
            queued.Clear(); queued.Enqueue(bridge); queued.Enqueue(next);
            switchFrame = Math.Min(expected.TotalFrames, Math.Max(expected.PositionFrames, startFrame));
            switchAfterLoop = false; LoopStartFrame = null;
            if (guardedCurrent == expected) guardedCurrent = null;
            if (previous != bridge) retired.Add(previous);
            return true;
        }
    }
    internal bool SkipTransition(StemMixer bridge, StemMixer next, Func<long, long> incomingFrame)
    {
        lock (gate)
        {
            if (disposed) return false;
            // The audio callback may have completed this same handoff already.
            if (current == next) return true;
            if (current != bridge || queued.Count != 1 || queued.Peek() != next) return false;
            next.SeekFrame(incomingFrame(bridge.PositionFrames));
            next.ContinueTransition(bridge);
            queued.Dequeue(); retired.Add(bridge);
            fadeFrom = bridge; fadeFrames = Math.Max(1, WaveFormat.SampleRate / 50);
            current = next; switchFrame = next.TotalFrames;
            switchAfterLoop = false; LoopStartFrame = null; guardedCurrent = null; restartFrames = 0;
            if (current == guardIncoming)
            {
                currentLoop = incomingLoop; current.SetReplayVocals(incomingLoop);
                guardedCurrent = current; loopStartFrame = incomingLoop.StartFrame; loopEndFrame = incomingFirstEnd; fullRestartEnd = incomingLoop.EndFrame;
                guardIncoming = null; incomingLoop = null;
            }
            return true;
        }
    }
    internal bool SeekScheduled(StemMixer expected, StemMixer bridge, StemMixer next, long frame, long startFrame, long bridgeFrame, long nextFrame)
    {
        lock (gate)
        {
            if (current != expected || queued.Count != 2 || queued.Peek() != bridge || queued.Last() != next) return false;
            expected.SeekFrame(frame); bridge.SeekFrame(bridgeFrame); next.SeekFrame(nextFrame);
            switchFrame = Math.Max(0, Math.Min(expected.TotalFrames, startFrame));
            // An explicit seek replaces a pending protective replay, while the
            // prepared bridge, lane edits and incoming restart guard survive.
            switchAfterLoop = false; LoopStartFrame = null;
            if (guardedCurrent == expected) guardedCurrent = null;
            fadeFrom = null; fadeFrames = restartFrames = 0;
            lastLeft = lastRight = 0;
            return true;
        }
    }
    internal bool EditScheduledTransition(StemMixer expected, StemMixer bridge, Func<long, bool> edit)
    {
        lock (gate)
        {
            if (current != bridge && (current != expected || !queued.Contains(bridge))) return false;
            return edit(current == bridge ? bridge.PositionFrames + WaveFormat.SampleRate / 10 : 0);
        }
    }
    internal void Apply(float volume, PlayerMix mix)
    {
        lock (gate) foreach (StemMixer mixer in queued.Concat(new[] { current, fadeFrom }).Where(mixer => mixer != null && (mixer == current || mixer.FollowLiveMix))) { mixer.Volume = volume; mixer.ApplyMix(mix); }
    }
    internal void ReleaseRetired()
    {
        StemMixer[] finished;
        lock (gate)
        {
            // Decoder shutdown can block. Transfer ownership under the audio
            // lock, then close readers without delaying the next callback.
            // A short bridge can still be reading the initial crossfade.
            finished = retired.Where(mixer => mixer != fadeFrom).ToArray();
            retired.RemoveAll(mixer => mixer != fadeFrom);
        }
        foreach (StemMixer mixer in finished) mixer.Dispose();
    }
    internal void ReleaseRetiredInBackground()
    {
        if (!Monitor.TryEnter(gate)) return;
        StemMixer[] finished;
        try
        {
            if (retired.Count == 0) return;
            finished = retired.Where(mixer => mixer != fadeFrom).ToArray();
            retired.RemoveAll(mixer => mixer != fadeFrom);
        }
        finally { Monitor.Exit(gate); }
        // Ownership has left the timeline. Decoder shutdown may wait for native
        // workers; it must not run on the scrolling/painting thread either.
        if (finished.Length != 0) Task.Run(() => { foreach (StemMixer mixer in finished) mixer.Dispose(); });
    }
    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            if (disposed) return 0;
            int written = 0; count -= count % 2;
            while (written < count)
            {
                long end = queued.Count == 0 || switchAfterLoop ? loopEndFrame ?? current.TotalFrames : switchFrame;
                if (current.PositionFrames >= end)
                {
                    if (queued.Count != 0 && !switchAfterLoop)
                    {
                        // The outro starts at a cut inside the old timeline.
                        // Blend that join; the final handoff already continues
                        // the same incoming samples at their next frame.
                        if (current.PositionFrames < current.TotalFrames) { fadeFrom = current; fadeFrames = Math.Max(1, WaveFormat.SampleRate / 50); }
                        StemMixer previous = current;
                        current = queued.Dequeue();
                        if (queued.Count == 1) current.ContinueReplayVocals(previous);
                        if (previous.PositionFrames >= previous.TotalFrames) current.ContinueTransition(previous);
                        retired.Add(previous); switchFrame = current.TotalFrames;
                        if (current == guardIncoming)
                        {
                            currentLoop = incomingLoop; current.SetReplayVocals(incomingLoop);
                            guardedCurrent = current; loopStartFrame = incomingLoop.StartFrame; loopEndFrame = incomingFirstEnd; fullRestartEnd = incomingLoop.EndFrame;
                            guardIncoming = null; incomingLoop = null;
                        }
                        continue;
                    }
                    if (loopStartFrame.HasValue && loopStartFrame.Value < end)
                    {
                        AutoLoopTiming repeated = currentLoop;
                        RepeatCurrent(loopStartFrame.Value, guardedCurrent == current);
                        if (switchAfterLoop) { switchAfterLoop = false; LoopStartFrame = null; if (repeated != null) current.SetReplayVocals(repeated, true, true); }
                        else if (guardedCurrent == current) loopEndFrame = fullRestartEnd;
                        continue;
                    }
                    break;
                }
                int part = (int)Math.Min(count - written, (end - current.PositionFrames) * 2);
                int read = current.Read(buffer, offset + written, part); if (read == 0) break;
                if (fadeFrom != null)
                {
                    int length = Math.Min(read, fadeFrames * 2);
                    if (fadeBuffer == null || fadeBuffer.Length < length) fadeBuffer = new float[length];
                    Array.Clear(fadeBuffer, 0, length); fadeFrom.Read(fadeBuffer, 0, length);
                    int total = Math.Max(1, WaveFormat.SampleRate / 50);
                    for (int sample = 0; sample < length; sample += 2)
                    {
                        float amount = 1f - fadeFrames / (float)total;
                        for (int channel = 0; channel < 2; channel++) buffer[offset + written + sample + channel] = fadeBuffer[sample + channel] * (1 - amount) + buffer[offset + written + sample + channel] * amount;
                        fadeFrames--;
                    }
                    if (fadeFrames == 0) fadeFrom = null;
                }
                int restartLength = restartTailFrames > 0 ? restartTailFrames : Math.Max(1, WaveFormat.SampleRate / 200);
                for (int sample = 0; sample < read && restartFrames > 0; sample += 2)
                {
                    float amount = 1f - restartFrames-- / (float)restartLength;
                    float left = restartTailFrames > 0 ? restartTail[restartTailPosition * 2] : restartLeft;
                    float right = restartTailFrames > 0 ? restartTail[restartTailPosition++ * 2 + 1] : restartRight;
                    buffer[offset + written + sample] = left * (1 - amount) + buffer[offset + written + sample] * amount;
                    buffer[offset + written + sample + 1] = right * (1 - amount) + buffer[offset + written + sample + 1] * amount;
                }
                lastLeft = buffer[offset + written + read - 2]; lastRight = buffer[offset + written + read - 1];
                written += read;
            }
            return written;
        }
    }
    public void Dispose()
    {
        StemMixer[] remaining;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            remaining = queued.Concat(retired).Concat(new[] { current, fadeFrom }).Where(mixer => mixer != null).Distinct().ToArray();
            queued.Clear(); retired.Clear(); fadeFrom = null;
        }
        foreach (StemMixer mixer in remaining) mixer.Dispose();
    }
}
