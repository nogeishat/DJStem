using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// The transport advances continuously, including muted and independently looped stems.
internal sealed partial class StemMixer : ISampleProvider, IDisposable
{
    internal static readonly string[] StemNames = { "vocals", "drums", "bass", "other" };
    private readonly object gate = new object();
    private readonly WaveStream[] readers = new WaveStream[5];
    private readonly string[] sourceFiles = new string[5];
    private readonly PlaybackTransform[] sourceTransforms = new PlaybackTransform[5];
    private readonly ISampleProvider[] sources = new ISampleProvider[5];
    private readonly float[][] buffers = new float[5][];
    private readonly float[] gains = new float[5];
    private readonly bool[] enabled = { true, true, true, true };
    private readonly bool[] solo = new bool[4];
    private readonly float[] levels = { 1, 1, 1, 1 };
    private readonly float?[] scheduledLevels = new float?[4], scheduledVolumes = new float?[4];
    internal bool FollowLiveMix = true;
    private readonly WaveStream[] previousReaders = new WaveStream[4];
    private readonly ISampleProvider[] previousSources = new ISampleProvider[4];
    private readonly float[][] previousBuffers = new float[4][];
    private readonly int[] transitionFrames = new int[4];
    private bool originalMode, disposed;
    private float volume = 0.7f;
    private long position;
    internal AutoReplayAnalysis ReplayAnalysis;
    private long replayVocalOut = Int64.MaxValue, replayVocalIn;
    private bool replayWrapped;
    private float replayVocalGain = 1;
    internal bool ReplayVocalsCommitted { get { lock (gate) return position >= replayVocalOut || (replayWrapped && position < replayVocalIn); } }
    internal void SetReplayVocals(AutoLoopTiming loop, bool wrapped = false, bool oneShot = false)
    {
        lock (gate)
        {
            replayVocalOut = loop != null && loop.SmoothReplay && !oneShot ? loop.VocalOutFrame : Int64.MaxValue;
            replayVocalIn = loop != null && loop.SmoothReplay && wrapped ? loop.VocalInFrame : 0;
            replayWrapped = wrapped;
            if (wrapped && loop != null && loop.SmoothReplay) replayVocalGain = 0;
        }
    }
    internal void ContinueReplayVocals(StemMixer previous)
    {
        long remaining; float gain;
        lock (previous.gate)
        {
            if (!previous.replayWrapped) return;
            remaining = Math.Max(0, previous.replayVocalIn - previous.position); gain = previous.replayVocalGain;
        }
        lock (gate)
        {
            replayWrapped = true; replayVocalOut = Int64.MaxValue;
            replayVocalIn = position + remaining; replayVocalGain = gain;
        }
    }
    private sealed class ScheduledStem { internal int Lane; internal long Frame; internal WaveFileReader Reader; internal string File; internal float Loudness; internal float? Level, Volume; }
    private readonly List<ScheduledStem> scheduled = new List<ScheduledStem>();
    // Auto transitions retain both readers so independently edited IN and OUT
    // points can overlap, leave a gap, or change after playback passes them.
    private sealed class StemTransition
    {
        internal WaveFileReader Reader;
        internal ISampleProvider Source;
        internal float[] Buffer;
        internal long Cut, Start;
        internal float Loudness, OutGate = 1, InGate, Gain;
        internal float? Level, Volume;
    }
    private readonly StemTransition[] stemTransitions = new StemTransition[4];
    internal readonly long TotalFrames;
    internal readonly bool HasStems;
    public WaveFormat WaveFormat { get; private set; }

    internal StemMixer(string audio, string folder, string[] originalFiles = null, PlaybackTransform audioTransform = null, PlaybackTransform[] stemTransforms = null)
    {
        try
        {
            if (originalFiles != null && originalFiles.Length != 4) throw new ArgumentException("Four original stem files are required.");
            HasStems = originalFiles != null || Directory.Exists(folder);
            for (int i = 0; i < 4; i++) HasStems &= File.Exists(originalFiles == null ? Path.Combine(folder, StemNames[i] + ".wav") : originalFiles[i]);
            sourceTransforms[0] = audioTransform;
            readers[0] = PlaybackTransform.OpenFile(audio, audioTransform);
            sourceFiles[0] = Path.GetFullPath(audio);
            int rate = readers[0].WaveFormat.SampleRate;
            if (HasStems)
            {
                for (int i = 0; i < 4; i++)
                {
                    sourceFiles[i + 1] = Path.GetFullPath(originalFiles == null ? Path.Combine(folder, StemNames[i] + ".wav") : originalFiles[i]);
                    sourceTransforms[i + 1] = stemTransforms == null ? null : stemTransforms[i];
                    readers[i + 1] = PlaybackTransform.OpenFile(sourceFiles[i + 1], sourceTransforms[i + 1]);
                }
                rate = readers[1].WaveFormat.SampleRate;
                TotalFrames = readers[1].Length / readers[1].WaveFormat.BlockAlign;
                for (int i = 1; i < 5; i++)
                    if (readers[i].WaveFormat.SampleRate != rate || readers[i].WaveFormat.Channels != 2 || readers[i].Length / readers[i].WaveFormat.BlockAlign != TotalFrames)
                        throw new InvalidDataException("The four stems must have matching sample rates, stereo channels, and lengths.");
            }
            else TotalFrames = audioTransform == null ? (long)(readers[0].TotalTime.TotalSeconds * rate) : audioTransform.Frames;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            originalMode = !HasStems;
            RebuildSources();
            SetInitialGains();
        }
        catch { Dispose(); throw; }
    }

    private void RebuildSources()
    {
        for (int i = 0; i < readers.Length; i++)
        {
            if (readers[i] == null) continue;
            ISampleProvider source = Samples(readers[i]);
            if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
            if (source.WaveFormat.Channels != 2) throw new InvalidDataException("Only mono or stereo music is supported.");
            if (source.WaveFormat.SampleRate != WaveFormat.SampleRate) source = new WdlResamplingSampleProvider(source, WaveFormat.SampleRate);
            sources[i] = source;
        }
    }

    internal void ReplaceStem(int index, string file, float? loudnessGain = null, PlaybackTransform transform = null)
    {
        if (index < 0 || index >= 4 || !HasStems) throw new ArgumentException("A completed stem lane is required.");
        if (loudnessGain.HasValue && !PlayerLoudness.ValidGain(loudnessGain.Value)) throw new ArgumentException("A valid song loudness gain is required.");
        WaveStream replacement = PlaybackTransform.OpenFile(file, transform);
        try
        {
            if (replacement.WaveFormat.SampleRate != WaveFormat.SampleRate || replacement.WaveFormat.Channels != 2 || replacement.Length / replacement.WaveFormat.BlockAlign < TotalFrames)
                throw new InvalidDataException("Replacement stems must match the stereo sample rate and cover the current song's timeline.");
            ISampleProvider replacementSource = Samples(replacement);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("StemMixer");
                long remaining; long frame = stemTiming[index].SourceFrame(position, WaveFormat.SampleRate, out remaining);
                replacement.Position = Math.Max(0, Math.Min(replacement.Length / replacement.WaveFormat.BlockAlign, frame)) * replacement.WaveFormat.BlockAlign;
                ClearTransition(index);
                scheduledLevels[index] = scheduledVolumes[index] = null;
                previousReaders[index] = readers[index + 1]; previousSources[index] = sources[index + 1];
                previousLoudness[index] = sourceLoudness[index + 1];
                sourceLoudness[index + 1] = loudnessGain ?? sourceLoudness[0]; loudnessMatched |= loudnessGain.HasValue;
                transitionFrames[index] = Math.Max(1, WaveFormat.SampleRate / 50);
                readers[index + 1] = replacement; sources[index + 1] = replacementSource; sourceFiles[index + 1] = Path.GetFullPath(file); sourceTransforms[index + 1] = transform; replacement = null;
                ReplayAnalysis = null;
            }
        }
        finally { if (replacement != null) replacement.Dispose(); }
    }
    internal void ScheduleStem(int lane, long frame, string file, float? loudnessGain = null, float? level = null, float? stemVolume = null)
    {
        if (loudnessGain.HasValue && !PlayerLoudness.ValidGain(loudnessGain.Value)) throw new ArgumentException("A valid song loudness gain is required.");
        WaveFileReader reader = new WaveFileReader(file);
        try
        {
            if (lane < 0 || lane > 3 || frame < 0 || frame >= TotalFrames || !HasStems || reader.WaveFormat.SampleRate != WaveFormat.SampleRate || reader.WaveFormat.Channels != 2 || reader.Length / reader.WaveFormat.BlockAlign != TotalFrames) throw new ArgumentException("Scheduled stems must share the transition timeline.");
            if (level.HasValue && (Single.IsNaN(level.Value) || level < 0 || level > 1) || stemVolume.HasValue && (Single.IsNaN(stemVolume.Value) || stemVolume < 0 || stemVolume > 1)) throw new ArgumentException("Scheduled mix levels and volumes must be between zero and one.");
            lock (gate) { scheduled.Add(new ScheduledStem { Lane = lane, Frame = frame, Reader = reader, File = Path.GetFullPath(file), Loudness = loudnessGain ?? sourceLoudness[0], Level = level, Volume = stemVolume }); loudnessMatched |= loudnessGain.HasValue; scheduled.Sort((a, b) => a.Frame.CompareTo(b.Frame)); reader = null; }
        }
        finally { if (reader != null) reader.Dispose(); }
    }
    internal void ScheduleStemSilence(int lane, long frame)
    {
        if (lane < 0 || lane > 3 || frame < 0 || frame >= TotalFrames || !HasStems) throw new ArgumentException("Scheduled silence must fit the transition timeline.");
        lock (gate) { scheduled.Add(new ScheduledStem { Lane = lane, Frame = frame }); scheduled.Sort((a, b) => a.Frame.CompareTo(b.Frame)); }
    }
    internal void ScheduleStemTransition(int lane, long cut, long start, string file, float loudness, float? level = null, float? stemVolume = null)
    {
        if (lane < 0 || lane > 3 || !HasStems || cut < 0 || cut > TotalFrames || start < 0 || start > TotalFrames || !PlayerLoudness.ValidGain(loudness)) throw new ArgumentException("Transition points must fit the shared timeline.");
        if (level.HasValue && (Single.IsNaN(level.Value) || level < 0 || level > 1) || stemVolume.HasValue && (Single.IsNaN(stemVolume.Value) || stemVolume < 0 || stemVolume > 1)) throw new ArgumentException("Transition mix levels must be between zero and one.");
        WaveFileReader reader = new WaveFileReader(file);
        try
        {
            if (reader.WaveFormat.SampleRate != WaveFormat.SampleRate || reader.WaveFormat.Channels != 2 || reader.Length / reader.WaveFormat.BlockAlign != TotalFrames) throw new ArgumentException("Transition stems must share the stereo timeline.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("StemMixer");
                reader.Position = position * reader.WaveFormat.BlockAlign;
                if (stemTransitions[lane] != null) stemTransitions[lane].Reader.Dispose();
                stemTransitions[lane] = new StemTransition { Reader = reader, Source = Samples(reader), Cut = cut, Start = start, Loudness = loudness, Level = level, Volume = stemVolume, Gain = level ?? Target(lane + 1) };
                loudnessMatched = true; reader = null;
            }
        }
        finally { if (reader != null) reader.Dispose(); }
    }
    internal bool RescheduleStemTransition(int lane, long cut, long start, long minimum)
    {
        lock (gate)
        {
            if (disposed || lane < 0 || lane > 3) return false;
            StemTransition transition = stemTransitions[lane];
            if (transition != null)
            {
                if (cut < 0 || cut > TotalFrames || start < 0 || start > TotalFrames) return false;
                transition.Cut = cut; transition.Start = start; return true;
            }
            minimum = Math.Max(minimum, position);
            if (disposed || lane < 0 || lane > 3 || cut < 0 || cut > start || start < minimum || start >= TotalFrames - WaveFormat.SampleRate / 20) return false;
            ScheduledStem arrival = scheduled.Find(change => change.Lane == lane && change.Reader != null);
            if (arrival == null) return false;
            scheduled.RemoveAll(change => change.Lane == lane && change.Reader == null);
            arrival.Frame = start;
            if (cut < start && cut > position) scheduled.Add(new ScheduledStem { Lane = lane, Frame = cut });
            scheduled.Sort((a, b) => a.Frame.CompareTo(b.Frame)); return true;
        }
    }
    private void ApplyScheduled()
    {
        while (scheduled.Count != 0 && scheduled[0].Frame <= position)
        {
            ScheduledStem change = scheduled[0]; scheduled.RemoveAt(0); int lane = change.Lane;
            scheduledLevels[lane] = change.Level; scheduledVolumes[lane] = change.Volume;
            if (change.Reader != null) change.Reader.CurrentTime = TimeSpan.FromSeconds((double)position / WaveFormat.SampleRate); ClearTransition(lane);
            previousReaders[lane] = readers[lane + 1]; previousSources[lane] = sources[lane + 1]; transitionFrames[lane] = Math.Max(1, WaveFormat.SampleRate / 50);
            previousLoudness[lane] = sourceLoudness[lane + 1]; sourceLoudness[lane + 1] = change.Reader == null ? 0 : change.Loudness;
            readers[lane + 1] = change.Reader; sources[lane + 1] = change.Reader == null ? new SilenceProvider(WaveFormat).ToSampleProvider() : Samples(change.Reader);
            sourceFiles[lane + 1] = change.File;
            sourceTransforms[lane + 1] = null;
        }
    }
    private void ClearTransition(int index)
    {
        if (previousReaders[index] != null) previousReaders[index].Dispose();
        previousReaders[index] = null; previousSources[index] = null; transitionFrames[index] = 0;
    }
    internal static ISampleProvider Samples(IWaveProvider provider)
    {
        // FFmpeg's 24-bit WAVs use an extensible PCM header. Adapt the reported
        // format for NAudio 2.2.1 while leaving every audio byte untouched.
        if (provider.WaveFormat.Encoding == WaveFormatEncoding.Extensible) provider = new StandardFormatProvider(provider);
        return provider.ToSampleProvider();
    }
    private sealed class StandardFormatProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        internal StandardFormatProvider(IWaveProvider provider)
        {
            source = provider; WaveFormat = provider.WaveFormat.AsStandardWaveFormat();
            WaveFormatExtraData extra = provider.WaveFormat as WaveFormatExtraData;
            if (WaveFormat.Encoding == WaveFormatEncoding.Extensible && extra != null && extra.ExtraSize >= 22)
            {
                byte[] guidBytes = new byte[16]; Array.Copy(extra.ExtraData, 6, guidBytes, 0, 16); Guid subformat = new Guid(guidBytes);
                if (subformat == new Guid("00000001-0000-0010-8000-00aa00389b71")) WaveFormat = new WaveFormat(extra.SampleRate, extra.BitsPerSample, extra.Channels);
                else if (subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") && extra.BitsPerSample == 32) WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(extra.SampleRate, extra.Channels);
            }
        }
        public WaveFormat WaveFormat { get; private set; }
        public int Read(byte[] buffer, int offset, int count) { return source.Read(buffer, offset, count); }
    }

    private float Target(int i)
    {
        if (i == 0) return originalMode ? 1 : 0;
        if (!originalMode && scheduledLevels[i - 1].HasValue) return scheduledLevels[i - 1].Value;
        bool anySolo = Array.Exists(solo, value => value);
        return !originalMode && enabled[i - 1] && (!anySolo || solo[i - 1]) ? levels[i - 1] : 0;
    }
    private void SetInitialGains() { for (int i = 0; i < gains.Length; i++) gains[i] = Target(i); }
    internal long PositionFrames { get { lock (gate) return position; } }
    internal double Seconds { get { return (double)PositionFrames / WaveFormat.SampleRate; } }
    internal double Duration { get { return (double)TotalFrames / WaveFormat.SampleRate; } }
    internal bool OriginalMode { get { lock (gate) return originalMode; } set { lock (gate) originalMode = value || !HasStems; } }
    internal float Volume { get { lock (gate) return volume; } set { lock (gate) volume = Math.Max(0, Math.Min(1, value)); } }
    internal void EnableStem(int index, bool value) { lock (gate) enabled[index] = value; }
    internal void ApplyMix(PlayerMix mix)
    {
        lock (gate)
        {
            for (int i = 0; i < 4; i++) { enabled[i] = !mix.Muted[i]; solo[i] = mix.Solo[i]; levels[i] = Math.Max(0, Math.Min(1, mix.Levels[i])); }
        }
    }

    internal void Seek(double seconds)
    {
        SeekFrame((long)(seconds * WaveFormat.SampleRate));
    }
    internal void WarmTransforms()
    {
        lock (gate) foreach (WaveStream reader in readers) { var live = reader as RealtimeWaveStream; if (live != null) live.Warm(); }
    }
    internal void CatchUpFrame(long target)
    {
        lock (gate)
        {
            target = Math.Max(0, Math.Min(TotalFrames, target));
            if (target < position || target - position > WaveFormat.SampleRate / 2)
            {
                SeekFrame(target); WarmTransforms(); return;
            }
            // Playback continued while this mixer warmed. Consume only that
            // short interval so the handoff cannot replay an earlier beat.
            var skipped = new float[2048];
            while (position < target) if (Read(skipped, 0, (int)Math.Min(skipped.Length, (target - position) * 2)) == 0) break;
        }
    }
    internal void ContinueTransition(StemMixer bridge)
    {
        float inheritedPeak; var incomingGains = new float[4];
        lock (bridge.gate)
        {
            // The full song follows these same incoming samples. Preserve the
            // live fader ramps and limiter instead of starting new envelopes.
            for (int lane = 0; lane < 4; lane++)
            {
                StemTransition transition = bridge.stemTransitions[lane];
                if (transition == null) return;
                incomingGains[lane] = transition.Gain * transition.InGate;
            }
            inheritedPeak = bridge.peakGain;
        }
        lock (gate)
        {
            peakGain = inheritedPeak;
            for (int lane = 0; lane < 4; lane++) gains[lane + 1] = incomingGains[lane];
        }
    }
    internal void SeekFrame(long targetFrame)
    {
        lock (gate)
        {
            position = Math.Max(0, Math.Min(TotalFrames, targetFrame));
            replayVocalOut = Int64.MaxValue; replayVocalIn = 0; replayWrapped = false; replayVocalGain = 1;
            for (int i = 0; i < 4; i++) ClearTransition(i);
            if (readers[0] != null) readers[0].CurrentTime = TimeSpan.FromSeconds((double)position / WaveFormat.SampleRate);
            for (int lane = 0; lane < 4; lane++)
            {
                long remaining; long frame = stemTiming[lane].SourceFrame(position, WaveFormat.SampleRate, out remaining);
                if (readers[lane + 1] != null) readers[lane + 1].Position = Math.Max(0, Math.Min(StemFrames(lane), frame)) * readers[lane + 1].WaveFormat.BlockAlign;
                stemFadeFrames[lane] = 0; stemLastLeft[lane] = stemLastRight[lane] = 0;
                StemTransition transition = stemTransitions[lane];
                if (transition != null)
                {
                    transition.Reader.Position = position * transition.Reader.WaveFormat.BlockAlign;
                    transition.Source = Samples(transition.Reader);
                    transition.OutGate = position < transition.Cut ? 1 : 0; transition.InGate = position >= transition.Start ? 1 : 0;
                    transition.Gain = transition.Level ?? Target(lane + 1);
                }
            }
            RebuildSources(); // Reset the resampler's buffered samples after repositioning.
            SetInitialGains();
            peakGain = 1;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            if (disposed) return 0;
            int written = 0;
            while (written < count - count % 2)
            {
                ApplyScheduled();
                int part = count - count % 2 - written;
                if (scheduled.Count != 0) part = (int)Math.Min(part, (scheduled[0].Frame - position) * 2);
                int read = ReadSegment(buffer, offset + written, part); if (read == 0) break; written += read;
            }
            return written;
        }
    }
    private int ReadSegment(float[] buffer, int offset, int count)
    {
            count = (int)Math.Min(count / 2 * 2, (TotalFrames - position) * 2);
            if (count <= 0) return 0;
            Array.Clear(buffer, offset, count);
            for (int source = 0; source < sources.Length; source++)
            {
                if (sources[source] == null) continue;
                if (buffers[source] == null || buffers[source].Length < count) buffers[source] = new float[count];
                Array.Clear(buffers[source], 0, count);
                int read = 0;
                if (source > 0) ReadStemSamples(source - 1, buffers[source], count);
                else while (read < count)
                {
                    int next = sources[source].Read(buffers[source], read, count - read);
                    if (next == 0) break;
                    read += next;
                }
                for (int sample = 0; sample < count; sample++) buffers[source][sample] *= sourceLoudness[source];
                if (source > 0 && previousSources[source - 1] != null)
                {
                    int lane = source - 1;
                    if (previousBuffers[lane] == null || previousBuffers[lane].Length < count) previousBuffers[lane] = new float[count];
                    Array.Clear(previousBuffers[lane], 0, count);
                    int oldRead = 0;
                    while (oldRead < count)
                    {
                        int more = previousSources[lane].Read(previousBuffers[lane], oldRead, count - oldRead);
                        if (more == 0) break; oldRead += more;
                    }
                    int length = Math.Max(1, WaveFormat.SampleRate / 50);
                    for (int sample = 0; sample < count && transitionFrames[lane] > 0; sample += 2)
                    {
                        float amount = 1f - transitionFrames[lane] / (float)length;
                        buffers[source][sample] = previousBuffers[lane][sample] * previousLoudness[lane] * (1 - amount) + buffers[source][sample] * amount;
                        buffers[source][sample + 1] = previousBuffers[lane][sample + 1] * previousLoudness[lane] * (1 - amount) + buffers[source][sample + 1] * amount;
                        transitionFrames[lane]--;
                    }
                    if (transitionFrames[lane] == 0) ClearTransition(lane);
                }
                float target = Target(source);
                float step = 1f / (WaveFormat.SampleRate * 0.008f); // Eight millisecond anti-click ramp.
                float sourceVolume = source > 0 && scheduledVolumes[source - 1].HasValue ? scheduledVolumes[source - 1].Value : volume;
                StemTransition transition = source > 0 ? stemTransitions[source - 1] : null;
                if (transition != null)
                {
                    if (transition.Buffer == null || transition.Buffer.Length < count) transition.Buffer = new float[count];
                    Array.Clear(transition.Buffer, 0, count);
                    int incomingRead = 0;
                    while (incomingRead < count)
                    {
                        int more = transition.Source.Read(transition.Buffer, incomingRead, count - incomingRead);
                        if (more == 0) break; incomingRead += more;
                    }
                }
                float gateStep = 1f / Math.Max(1, WaveFormat.SampleRate / 50);
                for (int sample = 0; sample < count; sample += 2)
                {
                    gains[source] += Math.Max(-step, Math.Min(step, target - gains[source]));
                    float outgoingGate = 1;
                    if (source == 1)
                    {
                        long frame = position + sample / 2;
                        float vocalTarget = frame >= replayVocalOut || (replayWrapped && frame < replayVocalIn) ? 0 : 1;
                        float vocalStep = 1f / Math.Max(1, WaveFormat.SampleRate / 20);
                        replayVocalGain += Math.Max(-vocalStep, Math.Min(vocalStep, vocalTarget - replayVocalGain));
                        outgoingGate = replayVocalGain;
                    }
                    if (transition != null)
                    {
                        long frame = position + sample / 2;
                        transition.OutGate += Math.Max(-gateStep, Math.Min(gateStep, (frame < transition.Cut ? 1 : 0) - transition.OutGate));
                        transition.InGate += Math.Max(-gateStep, Math.Min(gateStep, (frame >= transition.Start ? 1 : 0) - transition.InGate));
                        float incomingTarget = originalMode ? 0 : transition.Level ?? target;
                        transition.Gain += Math.Max(-step, Math.Min(step, incomingTarget - transition.Gain));
                        float incomingGain = transition.InGate * transition.Gain * transition.Loudness * (transition.Volume ?? volume);
                        buffer[offset + sample] += transition.Buffer[sample] * incomingGain;
                        buffer[offset + sample + 1] += transition.Buffer[sample + 1] * incomingGain;
                        outgoingGate *= transition.OutGate;
                    }
                    buffer[offset + sample] += buffers[source][sample] * gains[source] * sourceVolume * outgoingGate;
                    buffer[offset + sample + 1] += buffers[source][sample + 1] * gains[source] * sourceVolume * outgoingGate;
                }
            }
            if (loudnessMatched) ProtectPeaks(buffer, offset, count);
            else for (int sample = 0; sample < count; sample++) buffer[offset + sample] = Math.Max(-1, Math.Min(1, buffer[offset + sample]));
            position += count / 2;
            return count;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (ScheduledStem change in scheduled) if (change.Reader != null) change.Reader.Dispose(); scheduled.Clear();
            foreach (WaveStream reader in readers) if (reader != null) reader.Dispose();
            for (int i = 0; i < 4; i++) if (stemTransitions[i] != null) { stemTransitions[i].Reader.Dispose(); stemTransitions[i] = null; }
            for (int i = 0; i < 4; i++) ClearTransition(i);
        }
    }
}
