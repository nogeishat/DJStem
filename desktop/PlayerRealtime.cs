using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// A virtual output timeline, independent of the decoder's read-ahead position.
// Immutable so export snapshots can share settings without sharing DSP state.
internal sealed class PlaybackTransform
{
    internal readonly double Tempo, Pitch, Offset;
    internal readonly int Rate, Stem;
    internal readonly long Frames;
    internal PlaybackTransform(StemMatchPlan plan, int rate, long frames, int stem = 0)
    {
        PlayerMatching.Filter(plan, rate, frames, stem); // Validate before opening any readers.
        if (rate <= 0 || frames <= 0) throw new ArgumentOutOfRangeException("frames");
        Tempo = plan.Tempo; Pitch = plan.Pitch; Offset = plan.Offset; Rate = rate; Frames = frames; Stem = stem;
    }
    internal StemMatchPlan Plan { get { return new StemMatchPlan { Tempo = Tempo, Pitch = Pitch, Offset = Offset }; } }
    internal WaveStream Open(string file) { return new RealtimeWaveStream(file, this); }
    internal static WaveStream OpenFile(string file, PlaybackTransform transform = null)
    {
        return transform != null ? transform.Open(file) : Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? (WaveStream)new WaveFileReader(file) : new MediaFoundationReader(file);
    }
    // Tempo moves the existing amplitude envelope in time; pitch does not need
    // a new full-file scan just to draw an approximate normalized waveform.
    internal float[] Peaks(float[] source, double sourceDuration)
    {
        if (source == null) return null;
        float[] result = new float[source.Length];
        double duration = Frames / (double)Rate;
        for (int i = 0; i < result.Length; i++)
        {
            double from = (i * duration / result.Length - Offset) * Tempo;
            double to = ((i + 1) * duration / result.Length - Offset) * Tempo;
            int first = (int)Math.Floor(from / sourceDuration * source.Length), end = (int)Math.Ceiling(to / sourceDuration * source.Length);
            for (int j = Math.Max(0, first); j < Math.Min(source.Length, end); j++) result[i] = Math.Max(result[i], source[j]);
        }
        return result;
    }
}

// Pull-based real-time Rubber Band R2 processing, following Mixxx's buffer
// scaler design. Startup padding/delay are compensated on every seek. Only
// small fixed buffers are retained, regardless of the song's length.
internal sealed class RealtimeWaveStream : WaveStream
{
    private const int Block = 1024;
    private readonly PlaybackTransform transform;
    private readonly WaveStream decoder;
    private ISampleProvider input;
    private IntPtr state, planes, leftMemory, rightMemory;
    private readonly int options;
    private readonly float[] interleaved = new float[Block * 2], left = new float[Block], right = new float[Block];
    private long frame, discard;
    private bool initialized, hasProcessed, final, closed;
    private readonly WaveFormat format;
    public override WaveFormat WaveFormat { get { return format; } }
    public override long Length { get { return checked(transform.Frames * 8); } }
    public override long Position
    {
        get { return frame * 8; }
        set
        {
            long next = Math.Max(0, Math.Min(transform.Frames, value / 8));
            if (next != frame) { frame = next; initialized = false; }
        }
    }
    internal RealtimeWaveStream(string file, PlaybackTransform settings)
    {
        transform = settings; format = WaveFormat.CreateIeeeFloatWaveFormat(settings.Rate, 2);
        try
        {
            decoder = PlaybackTransform.OpenFile(file);
            // Real-time, no worker allocation inside the audio callback,
            // preserved formants, stereo coherence, consistent shifted pitch.
            options = 0x00000001 | 0x00010000 | 0x01000000 | 0x04000000 | 0x10000000;
            if (settings.Stem != 1 || settings.Tempo < 1) options |= 0x00000100;
            state = Native.New((uint)settings.Rate, 2, options, 1 / settings.Tempo, settings.Pitch);
            if (state == IntPtr.Zero) throw new InvalidOperationException("Could not initialize the live tempo/key processor.");
            Native.MaxSize(state, Block);
            leftMemory = Marshal.AllocHGlobal(Block * 4); rightMemory = Marshal.AllocHGlobal(Block * 4);
            planes = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(planes, 0, leftMemory); Marshal.WriteIntPtr(planes, IntPtr.Size, rightMemory);
        }
        catch { Dispose(); throw; }
    }
    private void Process(int frames, bool last)
    {
        Marshal.Copy(left, 0, leftMemory, frames); Marshal.Copy(right, 0, rightMemory, frames);
        Native.Process(state, planes, (uint)frames, last ? 1 : 0);
    }
    private void Initialize()
    {
        // Some R2 builds retain phase history after reset(). A fresh state at
        // discontinuities makes repeated seeks and export snapshots agree.
        if (hasProcessed)
        {
            Native.Delete(state);
            state = IntPtr.Zero;
            state = Native.New((uint)transform.Rate, 2, options, 1 / transform.Tempo, transform.Pitch);
            if (state == IntPtr.Zero) throw new InvalidOperationException("Could not reset live tempo/key processing.");
            Native.MaxSize(state, Block);
        }
        hasProcessed = true; final = false;
        long offset = (long)Math.Round(transform.Offset * transform.Rate);
        long requested = Math.Max(0, frame - offset);
        // A little real input before a seek lets the spectral windows settle.
        long pre = Math.Min(requested, transform.Rate / 10);
        decoder.CurrentTime = TimeSpan.FromSeconds((requested - pre) * transform.Tempo / transform.Rate);
        input = StemMixer.Samples(decoder);
        if (input.WaveFormat.Channels == 1) input = new MonoToStereoSampleProvider(input);
        if (input.WaveFormat.Channels != 2) throw new InvalidDataException("Only mono or stereo music is supported.");
        if (input.WaveFormat.SampleRate != transform.Rate) input = new WdlResamplingSampleProvider(input, transform.Rate);
        Array.Clear(left, 0, Block); Array.Clear(right, 0, Block);
        int pad = (int)Native.Pad(state);
        while (pad > 0) { int part = Math.Min(Block, pad); Process(part, false); pad -= part; }
        discard = Native.Delay(state) + pre; initialized = true;
    }
    // Warm at the requested position off the playback callback, then restore
    // the virtual position while retaining the DSP's ready output samples.
    internal void Warm()
    {
        if (initialized || frame >= transform.Frames) return;
        Initialize();
        while (discard > 0)
        {
            int available = EnsureAvailable(); if (available <= 0) break;
            int take = (int)Math.Min(Block, Math.Min(available, discard));
            uint retrieved = Native.Retrieve(state, planes, (uint)take);
            if (retrieved == 0) throw new InvalidDataException("The live tempo/key processor stopped during preparation.");
            discard -= retrieved;
        }
        EnsureAvailable();
    }
    private int EnsureAvailable()
    {
        int available;
        while ((available = Native.Available(state)) == 0 && !final)
        {
            int read = 0;
            while (read < interleaved.Length)
            {
                int more = input.Read(interleaved, read, interleaved.Length - read);
                if (more == 0) break; read += more;
            }
            for (int i = 0; i < read / 2; i++) { left[i] = interleaved[i * 2]; right[i] = interleaved[i * 2 + 1]; }
            final = read < interleaved.Length; Process(read / 2, final);
        }
        return available;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (closed) return 0;
        if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentException("Invalid audio buffer.");
        int frames = (int)Math.Min(count / 8, transform.Frames - frame), written = 0;
        long delay = (long)Math.Round(transform.Offset * transform.Rate);
        while (written < frames)
        {
            if (frame < delay)
            {
                int zeros = (int)Math.Min(frames - written, delay - frame);
                Array.Clear(buffer, offset + written * 8, zeros * 8); written += zeros; frame += zeros; continue;
            }
            if (!initialized) Initialize();
            int available = EnsureAvailable();
            if (available <= 0)
            {
                Array.Clear(buffer, offset + written * 8, (frames - written) * 8); frame += frames - written; written = frames; break;
            }
            int take = (int)Math.Min(Block, Math.Min(available, discard > 0 ? discard : frames - written));
            take = (int)Native.Retrieve(state, planes, (uint)take);
            if (take <= 0) throw new InvalidDataException("The live tempo/key processor stopped producing samples.");
            if (discard > 0) { discard -= take; continue; }
            Marshal.Copy(leftMemory, left, 0, take); Marshal.Copy(rightMemory, right, 0, take);
            for (int i = 0; i < take; i++) { interleaved[2 * i] = left[i]; interleaved[2 * i + 1] = right[i]; }
            Buffer.BlockCopy(interleaved, 0, buffer, offset + written * 8, take * 8);
            written += take; frame += take;
        }
        return written * 8;
    }
    protected override void Dispose(bool disposing)
    {
        if (!closed)
        {
            closed = true;
            if (state != IntPtr.Zero) { Native.Delete(state); state = IntPtr.Zero; }
            if (planes != IntPtr.Zero) Marshal.FreeHGlobal(planes);
            if (leftMemory != IntPtr.Zero) Marshal.FreeHGlobal(leftMemory);
            if (rightMemory != IntPtr.Zero) Marshal.FreeHGlobal(rightMemory);
            if (disposing && decoder != null) decoder.Dispose();
        }
        base.Dispose(disposing);
    }
    ~RealtimeWaveStream() { Dispose(false); }
    private static class Native
    {
        private const string Dll = "rubberband-3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_new")] internal static extern IntPtr New(uint rate, uint channels, int options, double time, double pitch);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_delete")] internal static extern void Delete(IntPtr state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_set_max_process_size")] internal static extern void MaxSize(IntPtr state, uint frames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_get_preferred_start_pad")] internal static extern uint Pad(IntPtr state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_get_start_delay")] internal static extern uint Delay(IntPtr state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_process")] internal static extern void Process(IntPtr state, IntPtr input, uint frames, int final);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_available")] internal static extern int Available(IntPtr state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rubberband_retrieve")] internal static extern uint Retrieve(IntPtr state, IntPtr output, uint frames);
    }
}
