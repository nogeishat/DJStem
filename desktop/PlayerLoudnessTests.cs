using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.Wave;

internal static class PlayerLoudnessTests
{
    private const int Rate = 16000;
    private static int checks;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Get(object item, string field) { return item.GetType().GetField(field, Fields).GetValue(item); }
    private static void Set(object item, string field, object value) { item.GetType().GetField(field, Fields).SetValue(item, value); }
    private static object Invoke(object item, string method, params object[] arguments) { return item.GetType().GetMethod(method, Fields).Invoke(item, arguments); }
    private static void Finish(Task task)
    {
        var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 45000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Loudness preparation must finish without blocking the UI"); task.GetAwaiter().GetResult();
    }
    private static void Wave(string file, double amplitude, bool mono = false, bool intro = false)
    {
        using (var writer = new WaveFileWriter(file, new WaveFormat(Rate, 24, mono ? 1 : 2)))
        for (int frame = 0; frame < Rate * 6; frame++)
        {
            float value = (float)(amplitude * (intro && frame < Rate * 2 ? .1 : 1) * Math.Sin(2 * Math.PI * 1000 * frame / Rate));
            writer.WriteSample(value); if (!mono) writer.WriteSample(value);
        }
    }
    private static SongInfo Song(string root, string name, double amplitude)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); string audio = Path.Combine(folder, "song.wav"); Wave(audio, amplitude);
        foreach (string stem in StemMixer.StemNames) Wave(Path.Combine(folder, stem + ".wav"), amplitude / 4);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), new JavaScriptSerializer().Serialize(new { bpm = 120, key = "A minor", analysis = new { version = 2, source = "mp3", beatOffset = 0, beatConfidence = .9, keyConfidence = .9 } }));
        return new SongInfo { Audio = audio, Folder = folder, Cover = Path.Combine(folder, "missing.jpg"), HasStems = true, Bpm = 120, Artist = "Artist", Title = name };
    }
    private static float Gain(string root, string audio) { Task<float> task = PlayerLoudness.Gain(root, audio, CancellationToken.None); Finish(task); return task.Result; }
    private static float[] Read(StemMixer mixer, double at = 1)
    {
        mixer.Seek(at); var data = new float[Rate * 2]; Check(mixer.Read(data, 0, data.Length) == data.Length, "Matching loudness must retain the audio timeline"); return data;
    }
    private static double Rms(float[] data) { return Math.Sqrt(data.Select(value => (double)value * value).Average()); }
    private static bool Near(double a, double b) { return Math.Abs(a - b) < .001; }
    private static void Measurements(string root, SongInfo quiet, SongInfo loud, out float quietGain, out float loudGain)
    {
        byte[] original = File.ReadAllBytes(quiet.Audio); quietGain = Gain(root, quiet.Audio); loudGain = Gain(root, loud.Audio);
        Check(quietGain > 1 && loudGain < 1 && Math.Abs(20 * Math.Log10(quietGain / loudGain) - 18.0618) < .1, "Quiet and loud recordings must get opposite corrections matching their measured level difference");
        Check(File.ReadAllBytes(quiet.Audio).SequenceEqual(original), "Analysis must leave the original audio untouched");
        Check(Directory.GetFiles(Path.Combine(root, "data", "loudness"), "*.json").Length == 2, "Measurements must be cached without rendering new audio files");
        string executable = Environment.GetEnvironmentVariable("STEMKIT_FFMPEG");
        try
        {
            Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", Path.Combine(root, "missing-ffmpeg.exe"));
            Check(Gain(root, quiet.Audio) == quietGain, "Repeated plays must reuse the in-memory measurement");
            ((IDictionary)typeof(PlayerLoudness).GetField("memory", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Clear();
            Check(Gain(root, quiet.Audio) == quietGain && Gain(root, loud.Audio) == loudGain, "A new player must reuse disk measurements without running FFmpeg again");
        }
        finally { Environment.SetEnvironmentVariable("STEMKIT_FFMPEG", executable); }
        string changed = Path.Combine(root, "changed.wav"); Wave(changed, .1); float first = Gain(root, changed); Wave(changed, .2); float second = Gain(root, changed);
        Check(Math.Abs(20 * Math.Log10(first / second) - 6.0206) < .1, "Changing a recording must invalidate its old measurement");
        string mono = Path.Combine(root, "mono.wav"); Wave(mono, .05, true);
        Check(Math.Abs(20 * Math.Log10(Gain(root, mono) / quietGain)) < .05, "Mono analysis must account for stereo duplication during playback");
        string silence = Path.Combine(root, "silence.wav"); Wave(silence, 0); Check(Gain(root, silence) == 1, "Silence must never be amplified");
        string corrupt = Path.Combine(root, "corrupt.wav"); File.WriteAllText(corrupt, "invalid"); Check(Gain(root, corrupt) == 1, "Measurement failure must leave ordinary playback available");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool stopped = false;
            try { PlayerLoudness.Gain(root, quiet.Audio, canceled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Cancellation must be honored even for cached measurements");
        }
        string cancelAudio = Path.Combine(root, "cancel.wav"); Wave(cancelAudio, .1);
        using (var canceled = new CancellationTokenSource())
        {
            Task<float> task = PlayerLoudness.Gain(root, cancelAudio, canceled.Token); canceled.Cancel();
            var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
            Check(task.IsCanceled, "Canceling active analysis must stop its process promptly");
        }
        File.Delete(cancelAudio); Check(!File.Exists(cancelAudio), "Canceled analysis must release the recording handle");
        Check(Near(PlayerLoudness.GainFor(-22, -.5), Math.Pow(10, 6.0 / 20)), "A quiet recording with isolated loud peaks must still get its loudness correction; output protection handles the peaks separately");
        Check(PlayerLoudness.GainFor(Double.NaN, -20) == 1 && PlayerLoudness.GainFor(Double.NegativeInfinity, Double.NegativeInfinity) == 1 && PlayerLoudness.GainFor(-70, -60) == 1 && PlayerLoudness.GainFor(-50, -45) <= 4, "Invalid, silent, and very quiet measurements must never produce excessive gain");
    }
    private static void Audio(SongInfo quiet, SongInfo loud, float quietGain, float loudGain, string root)
    {
        using (var a = new StemMixer(quiet.Audio, quiet.Folder))
        using (var b = new StemMixer(loud.Audio, loud.Folder))
        {
            a.SetLoudness(quietGain); b.SetLoudness(loudGain); a.Volume = b.Volume = 1; double baseline = Rms(Read(a));
            Check(Math.Abs(20 * Math.Log10(baseline / Rms(Read(b)))) < .1, "Quiet and loud songs must have matching audible stem levels");
            a.OriginalMode = b.OriginalMode = true;
            Check(Near(Rms(Read(a)), baseline) && Near(Rms(Read(b)), baseline), "Original mode and all stems must share the recording's gain");
            a.Volume = .5f; Check(Near(Rms(Read(a)), baseline / 2), "Master volume must retain its chosen level"); a.Volume = 1;
            a.OriginalMode = false; var mix = new PlayerMix(); mix.ToggleMute(0); a.ApplyMix(mix);
            Check(Near(Rms(Read(a)), baseline * .75), "Muting must reduce volume without automatic compensation");
            mix.ToggleMute(0); mix.ToggleSolo(0); a.ApplyMix(mix);
            Check(Near(Rms(Read(a)), baseline / 4), "Solo must retain the relative stem balance");
            a.ReplaceStem(0, Path.Combine(loud.Folder, "vocals.wav"), loudGain);
            Check(Near(Rms(Read(a)), baseline / 4) && a.StemLoudnessGain(0) == loudGain && a.OriginalLoudnessGain == quietGain, "A replacement stem must use its donor's gain and retain the original song's gain");
            a.ReplaceStem(0, Path.Combine(quiet.Folder, "vocals.wav"), a.OriginalLoudnessGain);
            Check(Near(Rms(Read(a)), baseline / 4), "Reset must restore the original stem's loudness");
            mix.Solo[0] = false; mix.Levels[1] = .5f; a.ApplyMix(mix);
            Check(Near(Rms(Read(a)), baseline * .875), "Stem faders must retain their relative volume");
        }
        string passage = Path.Combine(root, "passage.wav"); Wave(passage, .1, false, true);
        using (var mixer = new StemMixer(passage, ""))
        {
            mixer.SetLoudness(2); mixer.Volume = 1;
            Check(Math.Abs(20 * Math.Log10(Rms(Read(mixer, .5)) / Rms(Read(mixer, 3))) + 20) < .05, "Quiet passages must retain their dynamics without moment-to-moment normalization");
        }
        string peaks = Path.Combine(root, "peaks.wav"); Wave(peaks, .8);
        using (var mixer = new StemMixer(peaks, ""))
        {
            mixer.SetLoudness(4); mixer.Volume = 1; float[] data = Read(mixer); double ceiling = Math.Pow(10, PlayerLoudness.PeakDb / 20);
            Check(data.All(value => !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= ceiling + .000001), "Boosted mixed audio must stay below the output peak ceiling");
            Check(data.Where((value, index) => index % 2 == 0).SequenceEqual(data.Where((value, index) => index % 2 == 1)), "Peak protection must link stereo channels");
        }
        using (var bridge = new StemMixer(quiet.Audio, quiet.Folder))
        {
            bridge.SetLoudness(quietGain); bridge.Volume = 1; var mix = new PlayerMix(); mix.Solo[0] = true; bridge.ApplyMix(mix); bridge.Seek(0);
            bridge.ScheduleStem(0, Rate, Path.Combine(loud.Folder, "vocals.wav"), loudGain);
            var data = new float[Rate * 4]; Check(bridge.Read(data, 0, data.Length) == data.Length, "Scheduled changes must keep audio uninterrupted across callbacks");
            double before = Rms(data.Take(Rate).ToArray()), after = Rms(data.Skip(Rate * 3).ToArray());
            Check(Math.Abs(20 * Math.Log10(before / after)) < .1 && bridge.StemLoudnessGain(0) == loudGain, "Scheduled stems must crossfade with their own recording's correction");
        }
    }
    private static void Integration(string root, SongInfo quiet, SongInfo loud, float quietGain, float loudGain)
    {
        SongInfo later = Song(root, "Later", .08); float laterGain = Gain(root, later.Audio);
        using (var form = new PlayerForm(root, root))
        {
            form.Disposed += delegate { Invoke(form, "CloseAudio"); }; IntPtr handle = form.Handle;
            Set(form, "songs", new List<SongInfo> { quiet, loud, later }); ((MixSlider)Get(form, "volume")).Value = 0;
            try
            {
                Finish((Task)Invoke(form, "LoadSong", quiet, false, 0.0, false));
                var native = (StemMixer)Get(form, "mixer"); Check(native.OriginalLoudnessGain == quietGain, "Normal loading must install the measured gain");
                Set(form, "autoSettings", new AutoPlaySettings { RampBars = 1, StemBars = .25 }); Set(form, "autoPlayEnabled", true);
                var queue = (PlaybackQueue)Get(form, "songQueue"); Invoke(form, "AddQueuedSong", loud, queue.Items.Count, false); Invoke(form, "AddQueuedSong", later, queue.Items.Count, false);
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var plan = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(plan != null && plan.Next.OriginalLoudnessGain == loudGain && plan.Bank.LoudnessGain == loudGain && plan.Bridge.OriginalLoudnessGain == quietGain, "Autoplay must preserve outgoing gain and prepare the incoming correction");
                var preload = (AutoSongPreload)Get(form, "autoPreload"); Finish(preload.Loading);
                Check(preload.Loading.Result != null && preload.Loading.Result.LoudnessGain == laterGain, "Second-song preloading must include loudness matching");
                var playback = (PlayerPlayback)Get(form, "playback"); var buffer = new float[4096];
                while (playback.Current != plan.Next) { playback.Read(buffer, 0, buffer.Length); Invoke(form, "PollAutoTransition"); }
                Check(((StemMixer)Get(form, "mixer")).OriginalLoudnessGain == loudGain && Get(form, "current") == loud, "Final handoff must retain the incoming gain");
                Finish((Task)Invoke(form, "PrepareAutoTransition")); var following = (PreparedAutoTransition)Get(form, "autoTransition");
                Check(following != null && following.Song == later && following.Next.OriginalLoudnessGain == laterGain, "The following handoff must reuse the preloaded correction");
                Invoke(form, "CancelAutoPreparation");
                var bank = new StemSourceBank { Song = quiet, LoudnessGain = quietGain, Traits = new MusicTraits { Bpm = 120, Tonic = 9, Minor = true }, Plan = new StemMatchPlan { Tempo = 1, MatchedBpm = 120 }, Files = StemMixer.StemNames.Select(stem => Path.Combine(quiet.Folder, stem + ".wav")).ToArray(), Peaks = Enumerable.Range(0, 4).Select(lane => Enumerable.Repeat(.5f, 1600).ToArray()).ToArray() };
                ((StemSourceState)Get(form, "stemSources")).Available = bank; Invoke(form, "UpdateSourceButtons"); Invoke(form, "ToggleStemSource", 0);
                Check(((StemMixer)Get(form, "mixer")).StemLoudnessGain(0) == quietGain, "The source button must apply its donor's measured loudness");
                Finish((Task)Invoke(form, "ApplyTuning", (double?)132, new Dictionary<string, int>()));
                var tuned = (StemMixer)Get(form, "mixer");
                Check(tuned.OriginalLoudnessGain == loudGain && tuned.StemLoudnessGain(0) == quietGain && ((StemSourceState)Get(form, "stemSources")).Active[0].LoudnessGain == quietGain, "Tempo/pitch changes must retain both original and donor gains instead of remeasuring fragments");
                Invoke(form, "RestoreStems");
                Check(Enumerable.Range(0, 4).All(lane => tuned.StemLoudnessGain(lane) == loudGain), "Reset stems must restore the original song's gain on every lane");
            }
            finally { Invoke(form, "StopAutoTimeline"); if (Get(form, "autoPreloadTask") != null) Finish((Task)Get(form, "autoPreloadTask")); Invoke(form, "CloseAudio"); }
        }
    }
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "stemkit-loudness-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            SongInfo quiet = Song(root, "Quiet", .05), loud = Song(root, "Loud", .4); float quietGain, loudGain;
            Measurements(root, quiet, loud, out quietGain, out loudGain); Audio(quiet, loud, quietGain, loudGain, root); Integration(root, quiet, loud, quietGain, loudGain);
            Console.WriteLine("Loudness matching: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
