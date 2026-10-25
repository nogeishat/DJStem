using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerRealtimeTests
{
    private const int Rate = 44100;
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Tone(string file, int seconds)
    {
        using (var writer = new WaveFileWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
        {
            var samples = new float[Rate * 2];
            for (int i = 0; i < Rate; i++) { samples[2 * i] = (float)(.1 * Math.Sin(2 * Math.PI * 440 * i / Rate)); samples[2 * i + 1] = samples[2 * i] * .5f; }
            for (int i = 0; i < seconds; i++) writer.WriteSamples(samples, 0, samples.Length);
        }
    }
    private static float[] Read(ISampleProvider source, int frames)
    {
        float[] result = new float[frames * 2]; int read = 0;
        while (read < result.Length) { int more = source.Read(result, read, Math.Min(2048, result.Length - read)); if (more == 0) break; read += more; }
        Check(read == result.Length, "The virtual stream must fill the requested output frames"); return result;
    }
    private static double Frequency(float[] samples, int rate = Rate)
    {
        int crossings = 0;
        for (int i = 2; i < samples.Length; i += 2) if (samples[i - 2] < 0 && samples[i] >= 0) crossings++;
        return crossings * rate / (samples.Length / 2.0);
    }
    private static double Energy(float[] samples) { return Math.Sqrt(samples.Sum(value => (double)value * value) / samples.Length); }
    private static PlaybackTransform Transform(double tempo, double pitch, double offset = 0, int seconds = 12, int stem = 0)
    {
        return new PlaybackTransform(new StemMatchPlan { Tempo = tempo, Pitch = pitch, Offset = offset }, Rate, (long)(seconds / tempo * Rate), stem);
    }
    private static void StreamTests(string root, string audio)
    {
        foreach (double tempo in new[] { .75, 1.0, 1.5 })
        foreach (double pitch in new[] { Math.Pow(2, -3 / 12.0), 1.0, Math.Pow(2, 4 / 12.0), TuningReference.Pitch(440, 432) })
        using (WaveStream reader = Transform(tempo, pitch).Open(audio))
        {
            Check(reader.Length / 8 == (long)(12 / tempo * Rate), "Tempo must change duration independently of pitch");
            reader.CurrentTime = TimeSpan.FromSeconds(2);
            var samples = Read(StemMixer.Samples(reader), Rate);
            Check(Math.Abs(Frequency(samples) - 440 * pitch) < 4, "Live pitch must transpose without changing the requested tempo");
            Check(Energy(samples) > .01 && samples.All(value => !Single.IsNaN(value) && !Single.IsInfinity(value)), "DSP output must remain finite and audible");
            Check(Enumerable.Range(0, samples.Length / 2).Max(i => Math.Abs(samples[i * 2 + 1] - samples[i * 2] * .5f)) < .0001, "Stereo channels must preserve their coherent phase and gain");
            reader.Position = reader.Length - 8;
            Check(reader.Read(new byte[80], 0, 80) == 8 && reader.Read(new byte[8], 0, 8) == 0, "EOF must be limited to the exact virtual frame count");
        }
        using (WaveStream reader = Transform(1.25, 1, .5).Open(audio))
        {
            var provider = StemMixer.Samples(reader);
            Check(Read(provider, Rate / 2).All(value => value == 0), "Positive alignment offsets must insert exact leading silence");
            Check(Energy(Read(provider, Rate / 2)) > .01, "Aligned audio must resume after its leading delay");
            reader.CurrentTime = TimeSpan.FromSeconds(4); var first = Read(StemMixer.Samples(reader), Rate / 4);
            reader.CurrentTime = TimeSpan.FromSeconds(4); var again = Read(StemMixer.Samples(reader), Rate / 4);
            double seekError = first.Zip(again, (a, b) => Math.Abs(a - b)).Max();
            Check(seekError < .000001, "Repeated seeks must reset decoder read-ahead and DSP state deterministically (error " + seekError + ")");
        }
        using (WaveStream reader = Transform(.8, 1.1, -.3).Open(audio)) Check(Energy(Read(StemMixer.Samples(reader), Rate / 2)) > .01, "Negative alignment offsets must trim the source instead of inserting silence");
        var peaks = new PlaybackTransform(new StemMatchPlan { Tempo = 2, Pitch = 1, Offset = .5 }, Rate, Rate * 2).Peaks(new float[] { 1, .5f, .25f, 0 }, 3);
        Check(peaks[0] == 0 && peaks[1] == 1, "Waveform envelopes must reflect tempo and beat offsets without scanning transformed audio");
        string mono = Path.Combine(root, "mono.wav");
        using (var writer = new WaveFileWriter(mono, new WaveFormat(48000, 24, 1)))
            for (int i = 0; i < 48000 * 3; i++) writer.WriteSample((float)(.25 * Math.Sin(2 * Math.PI * 440 * i / 48000)));
        var monoTransform = new PlaybackTransform(new StemMatchPlan { Tempo = .75, Pitch = 1 }, 22050, 22050 * 5, 1);
        using (var reader = monoTransform.Open(mono))
        {
            reader.CurrentTime = TimeSpan.FromSeconds(1);
            var samples = Read(StemMixer.Samples(reader), 22050);
            Check(Math.Abs(Frequency(samples, 22050) - 440) < 3, "Slowed drum components must retain pitch, including at lower output sample rates");
            Check(Enumerable.Range(0, samples.Length / 2).All(i => Math.Abs(samples[i * 2] - samples[i * 2 + 1]) < .000001), "A mono decoder at a different rate must produce coherent resampled stereo");
            reader.CurrentTime = TimeSpan.FromSeconds(4.5);
            Check(Read(StemMixer.Samples(reader), 22050 / 4).All(value => Math.Abs(value) < .000001), "A short source must pad its remaining virtual timeline with silence");
        }
    }
    private static void MixerTests(string root, string audio)
    {
        string[] files = StemMixer.StemNames.Select(stem => Path.Combine(root, stem + ".wav")).ToArray();
        foreach (string file in files) File.Copy(audio, file);
        PlaybackTransform audioTransform = Transform(1.25, Math.Pow(2, 2 / 12.0));
        PlaybackTransform[] transforms = Enumerable.Range(0, 4).Select(lane => Transform(1.25, audioTransform.Pitch, stem: lane)).ToArray();
        using (var mixer = new StemMixer(audio, root, files, audioTransform, transforms))
        {
            mixer.Volume = 1; mixer.Seek(3); mixer.SetStemShift(0, .4);
            mixer.StartStemLoop(1, 2, 150, .08);
            var before = mixer.StemState(1);
            using (var snapshot = mixer.ExportCopy())
            {
                Check(snapshot.TotalFrames == mixer.TotalFrames && snapshot.StemState(1).LoopStart == before.LoopStart, "Export snapshots must preserve live tempo, duration, and independent loops");
                mixer.Seek(3); snapshot.Seek(3);
                float[] live = Read(mixer, Rate / 2), export = Read(snapshot, Rate / 2);
                Check(live.Zip(export, (a, b) => Math.Abs(a - b)).Max() < .000001, "Export must reproduce the same transformed stems and timing edits");
                mixer.ReplaceStem(2, files[2], 1, Transform(1.25, .8));
                snapshot.Seek(3); Check(Energy(Read(snapshot, Rate / 4)) > .01, "An export must own independent DSP/decoder state after the live mix changes");
            }
            using (var playback = new PlayerPlayback(mixer))
            {
                Read(playback, Rate / 10);
                var next = new StemMixer(audio, root, files, Transform(.8, 1), Enumerable.Range(0, 4).Select(lane => Transform(.8, 1, stem: lane)).ToArray());
                next.Seek(mixer.Seconds * 1.25 / .8); next.WarmTransforms();
                Read(playback, Rate / 10); long handoffFrame = mixer.PositionFrames;
                Check(playback.ReplaceCurrent(mixer, next, 1.25 / .8), "Tuning must swap timelines through the existing audio provider");
                Check(Math.Abs(next.PositionFrames - handoffFrame * 1.25 / .8) <= 1, "A running handoff must catch up the frames played while the new processors warmed");
                Read(playback, Rate / 10); playback.ReleaseRetired();
                Check(playback.Current == next && Math.Abs(next.Duration - 15) < .00001, "Playback must continue across the tuning crossfade at its new duration");
            }
        }
        Stopwatch clock = Stopwatch.StartNew();
        var longTransform = Transform(1.17, 1.12, seconds: 180);
        using (var mixer = new StemMixer(audio, root, files, longTransform, Enumerable.Repeat(longTransform, 4).ToArray()))
        {
            mixer.Seek(60); mixer.WarmTransforms();
            long startup = clock.ElapsedMilliseconds;
            clock.Restart(); Read(mixer, Rate * 3);
            Console.WriteLine("Five stereo streams: warm/seek " + startup + " ms; 3 s audio processed in " + clock.ElapsedMilliseconds + " ms.");
            Check(startup < 2000, "A long-song live change must prepare in bounded time without rendering the song");
            Check(clock.ElapsedMilliseconds < 2500, "Five concurrent live stereo streams must process faster than playback");
        }
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    private static object Invoke(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Private).Invoke(target, args); }
    private static long Tune(PlayerForm form, double? bpm, Dictionary<string, int> shifts)
    {
        var clock = Stopwatch.StartNew(); Task task = (Task)Invoke(form, "ApplyTuning", bpm, shifts);
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(1); }
        Check(task.IsCompleted, "Applying a tune must finish on the UI context"); task.GetAwaiter().GetResult(); return clock.ElapsedMilliseconds;
    }
    private static void FormTests(string root, string audio)
    {
        string[] files = StemMixer.StemNames.Select(stem => Path.Combine(root, stem + ".wav")).ToArray();
        var original = new SongInfo { Audio = audio, Folder = root, Title = "Long original", Artist = "Test", Cover = "", HasStems = true, Bpm = 120 };
        var donor = new SongInfo { Audio = Path.Combine(root, "donor.wav"), Folder = root, Title = "Added", Artist = "Test", Cover = "", HasStems = true, Bpm = 100 };
        var traits = new MusicTraits { Bpm = 120, BeatOffset = .1, Tonic = 9, Minor = true };
        var donorTraits = new MusicTraits { Bpm = 100, BeatOffset = .2, Tonic = 0 };
        var plan = StemMatchPlan.Create(traits, donorTraits);
        var sources = new StemSourceState(root);
        var bank = new StemSourceBank { Song = donor, Traits = donorTraits, Plan = plan, Files = files, Peaks = new float[4][], Tuning = new PlaybackTransform[4] };
        for (int lane = 0; lane < 4; lane++)
        {
            sources.OriginalPeaks[lane] = PlayerWaveforms.Read(files[lane], CancellationToken.None);
            bank.Tuning[lane] = PlayerMatching.LiveTransform(files[lane], plan, Rate, Rate * 180, lane);
            bank.Peaks[lane] = bank.Tuning[lane].Peaks(sources.OriginalPeaks[lane], 180);
        }
        sources.Available = sources.Active[1] = bank;
        var initial = new StemMixer(audio, root); initial.Seek(30);
        initial.SetStemShift(0, .5); initial.StartStemLoop(2, 10, 120, .1);
        initial.ReplaceStem(1, bank.Files[1], 1, bank.Tuning[1]);
        var playback = new PlayerPlayback(initial);
        string configured = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        try
        {
            // A deliberately missing encoder proves tuning never shells out to
            // FFmpeg or waits for the full-file rendering gate.
            Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", Path.Combine(root, "missing-ffmpeg.exe"));
            using (var form = new PlayerForm(root, root))
            {
                IntPtr handle = form.Handle;
                Set(form, "current", original); Set(form, "mixer", initial); Set(form, "playback", playback); Set(form, "stemSources", sources);
                Set(form, "currentNativeTraits", traits); Set(form, "nativeRate", Rate); Set(form, "nativeFrames", Rate * 180L);
                var shifts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { original.Audio, 2 }, { donor.Audio, -3 } };
                PlayerMatchedCache.RenderGate.Wait(); long elapsed;
                try { elapsed = Tune(form, 150, shifts); } finally { PlayerMatchedCache.RenderGate.Release(); }
                var tuned = (StemMixer)Get(form, "mixer");
                Check(tuned != initial && playback.Current == tuned, "The Apply action must install a new live timeline without replacing playback");
                Check(Math.Abs(tuned.Duration - 144) < .00001 && Math.Abs(tuned.Seconds - 24) < .00001, "BPM changes must preserve the exact native musical position");
                Check(Math.Abs(tuned.StemState(0).Shift - .4) < .00001 && tuned.StemState(2).Looping, "BPM changes must scale stem offsets and preserve loops");
                Check(elapsed < 2000, "Applying BPM/key to a three-minute song must avoid song-length work");
                var tunedSources = (StemSourceState)Get(form, "stemSources");
                Check(Math.Abs(tunedSources.Active[1].Tuning[1].Tempo - 1.5) < .00001 && Math.Abs(tunedSources.Active[1].Tuning[1].Pitch - Camelot.Pitch(-3)) < .000001, "Replacement songs must follow the shared BPM with independent key shifts");
                long keyElapsed = Tune(form, 150, new Dictionary<string, int> { { original.Audio, -2 }, { donor.Audio, 1 } });
                tuned = (StemMixer)Get(form, "mixer");
                Check(Math.Abs(tuned.Duration - 144) < .00001, "Changing only key must leave the tempo and transport duration intact");
                Invoke(form, "ToggleStemSource", 1); Invoke(form, "ToggleStemSource", 1);
                tuned.Seek(20); Check(Energy(Read(tuned, Rate / 10)) > .01, "Tuned replacement stems must remain switchable and audible");
                Tune(form, null, new Dictionary<string, int>());
                tuned = (StemMixer)Get(form, "mixer");
                Check(tuned.TotalFrames == Rate * 180L && Math.Abs((double)Get(form, "originalTempo") - 1) < .000001, "Resetting tempo must recover the native transport length");
                Check(!Directory.Exists(Path.Combine(root, "data", "matched-stems")), "Applying and resetting tunes must create no full-song render cache");
                original.TuningHz = 440; donor.TuningHz = 442; Set(form, "loadedTuningHz", (double?)432);
                Tune(form, 150, new Dictionary<string, int> { { original.Audio, 2 }, { donor.Audio, -3 } });
                var originalTransform = (PlaybackTransform)Get(form, "originalPlaybackTransform");
                tunedSources = (StemSourceState)Get(form, "stemSources");
                Check(Math.Abs(originalTransform.Pitch - 432.0 / 440 * Camelot.Pitch(2)) < .000001, "Original reference correction and key shift must combine independently of shared BPM");
                Check(Math.Abs(tunedSources.Active[1].Plan.Pitch - 432.0 / 442 * Camelot.Pitch(-3)) < .000001, "Each donor must use its own estimated reference");
                Tune(form, null, new Dictionary<string, int>());
                originalTransform = (PlaybackTransform)Get(form, "originalPlaybackTransform");
                Check(originalTransform != null && Math.Abs(originalTransform.Pitch - 432.0 / 440) < .000001, "Resetting BPM and key must retain the selected tuning reference");
                Console.WriteLine("Apply on 3-minute song + donor: " + elapsed + " ms; key-only: " + keyElapsed + " ms; no FFmpeg or render gate.");
                Invoke(form, "CloseAudio");
            }
        }
        finally { playback.Dispose(); Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", configured); }
    }
    private static void ReferenceLoadingTests(string root, string audio)
    {
        Check(Math.Abs(TuningReference.Pitch(432, 440) - 440.0 / 432) < .0000001, "A 432 Hz song must convert relative to its detected reference");
        Check(TuningReference.Pitch(null, 432) == 1 && TuningReference.Pitch(440, null) == 1, "Unknown tuning and Original must bypass correction");
        foreach (bool stems in new[] { false, true })
        using (var form = new PlayerForm(root, root))
        {
            IntPtr handle = form.Handle; Set(form, "tuningReferenceHz", (double?)432);
            var song = new SongInfo { Audio = audio, Folder = root, Title = "Reference", Artist = "Test", Cover = "", TuningHz = 440, HasStems = stems };
            Task task = (Task)Invoke(form, "LoadSong", song, false, 0.0, false);
            var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(1); }
            Check(task.IsCompleted, "Loading a tuned song must finish"); task.GetAwaiter().GetResult();
            var mixer = (StemMixer)Get(form, "mixer");
            Check(mixer != null && mixer.TotalFrames == Rate * 180L, "Reference tuning must load with or without stems or a known BPM and preserve duration");
            mixer.OriginalMode = !stems; mixer.Seek(3);
            double frequency = Frequency(Read(mixer, Rate));
            Check(Math.Abs(frequency - 432) < 4, "Both MP3/original mode and all stems must load at the selected A4 frequency");
            Set(form, "tuningReferenceHz", (double?)442.25); Invoke(form, "WritePreferences");
            Set(form, "tuningReferenceHz", null); Invoke(form, "ReadPreferences");
            Check((double?)Get(form, "tuningReferenceHz") == 442.25, "The custom frequency must persist in player preferences");
            Invoke(form, "CloseAudio");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "rt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string audio = Path.Combine(root, "song.wav"); Tone(audio, 180);
            StreamTests(root, audio); MixerTests(root, audio); FormTests(root, audio); ReferenceLoadingTests(root, audio);
            Console.WriteLine("Live tempo/key: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "rt-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
